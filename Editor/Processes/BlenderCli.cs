using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace Orbiters.Toolkit.Editor.Processes
{
    /// <summary>
    /// Runs Blender without its window (<c>--background</c>) on a thread of its own, with the output in a log file,
    /// and hands the result back on the editor's main thread. Nothing here blocks the editor: a render or an export
    /// that takes minutes runs while the user keeps working.
    /// </summary>
    public static class BlenderCli
    {
        public sealed class Job
        {
            /// <summary>The Blender executable; empty picks <see cref="BlenderInstalls.GetSelectedExecutablePath"/>.</summary>
            public string Executable;
            /// <summary>The Python script Blender runs (<c>--python</c>), or null for plain arguments.</summary>
            public string Script;
            /// <summary>Arguments after <c>--</c>, for the script (<c>sys.argv</c> after the separator).</summary>
            public List<string> ScriptArguments = new List<string>();
            /// <summary>Extra Blender arguments before the script (a .blend to open, <c>--factory-startup</c>...).</summary>
            public List<string> BlenderArguments = new List<string>();
            public string WorkingDirectory;
            /// <summary>Where stdout and stderr are appended; null keeps them in the result only.</summary>
            public string LogPath;
            public int TimeoutSeconds = 1800;
            public Action<string> OnLine;
        }

        public sealed class Result
        {
            public bool Success;
            public int ExitCode;
            public bool TimedOut, Cancelled;
            public string Output = string.Empty;
            public string Error = string.Empty;
            public string LogPath;
            public TimeSpan Elapsed;
            /// <summary>The last stderr or stdout line that looked like an error, for a status line.</summary>
            public string Summary;
        }

        /// <summary>Runs the job on a dedicated thread; the task completes on that thread (await it or use <see cref="Run(Job, Action{Result})"/>).</summary>
        public static Task<Result> Run(Job job, CancellationToken cancellation = default)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            string executable = string.IsNullOrEmpty(job.Executable) ? BlenderInstalls.GetSelectedExecutablePath() : job.Executable;
            if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
            {
                return Task.FromResult(new Result { Success = false, ExitCode = -1, Summary = "Blender was not found. Pick its executable in the Orbiters settings." });
            }
            var arguments = new StringBuilder("--background --python-exit-code 1");
            foreach (string a in job.BlenderArguments) arguments.Append(' ').Append(Quote(a));
            if (!string.IsNullOrEmpty(job.Script)) arguments.Append(" --python ").Append(Quote(job.Script));
            if (job.ScriptArguments.Count > 0)
            {
                arguments.Append(" --");
                foreach (string a in job.ScriptArguments) arguments.Append(' ').Append(Quote(a));
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = string.IsNullOrEmpty(job.WorkingDirectory) ? Path.GetDirectoryName(executable) : job.WorkingDirectory
            };
            return DedicatedTask.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                StreamWriter log = null;
                var output = new StringBuilder();
                var error = new StringBuilder();
                string summary = null;
                try
                {
                    if (!string.IsNullOrEmpty(job.LogPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(job.LogPath));
                        log = new StreamWriter(job.LogPath, true, new UTF8Encoding(false));
                        log.WriteLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + executable + " " + startInfo.Arguments);
                    }
                    var result = EditorProcessRunner.Run(startInfo, job.TimeoutSeconds * 1000,
                        () => cancellation.IsCancellationRequested,
                        line =>
                        {
                            output.AppendLine(line); log?.WriteLine(line); job.OnLine?.Invoke(line);
                            if (LooksLikeError(line)) summary = line;
                        },
                        line =>
                        {
                            error.AppendLine(line); log?.WriteLine("[stderr] " + line); job.OnLine?.Invoke(line);
                            if (LooksLikeError(line) || summary == null) summary = line;
                        });
                    log?.WriteLine("[exit " + result.ExitCode + (result.TimedOut ? " timeout" : "") + (result.Cancelled ? " cancelled" : "") + " after " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s]");
                    return new Result
                    {
                        Success = result.Success, ExitCode = result.ExitCode, TimedOut = result.TimedOut, Cancelled = result.Cancelled,
                        Output = output.ToString(), Error = error.ToString(), LogPath = job.LogPath, Elapsed = watch.Elapsed,
                        Summary = result.Success ? null : result.TimedOut ? "Blender did not finish in " + job.TimeoutSeconds + " s." : summary ?? ("Blender exited with code " + result.ExitCode + ".")
                    };
                }
                finally
                {
                    log?.Dispose();
                }
            }, cancellation);
        }

        /// <summary>
        /// Runs the job and calls <paramref name="done"/> on the main thread, once, whatever happens: a job cancelled before
        /// it started, and one a script reload stops (Unity stops the processes it started with its scripts), report so
        /// (<see cref="Result.Cancelled"/>), the latter before the reload, while the caller can still keep its state.
        /// </summary>
        public static void Run(Job job, Action<Result> done, CancellationToken cancellation = default)
        {
            var task = Run(job, cancellation);
            bool finished = false;
            void Finish(Result result)
            {
                if (finished) return;
                finished = true;
                EditorApplication.update -= Poll;
                AssemblyReloadEvents.beforeAssemblyReload -= Reloading;
                done?.Invoke(result);
            }
            void Poll()
            {
                if (task.IsCompleted) Finish(ResultOf(task, job));
            }
            void Reloading()
            {
                if (task.IsCompleted) { Finish(ResultOf(task, job)); return; }
                UnityEngine.Debug.LogWarning("[Orbiters Toolkit] A Blender job was stopped because Unity reloaded its scripts" +
                                             (string.IsNullOrEmpty(job.LogPath) ? "." : " (its log: " + job.LogPath + ")."));
                Finish(new Result { Success = false, ExitCode = -1, Cancelled = true, LogPath = job.LogPath, Summary = "Blender was stopped: Unity reloaded its scripts." });
            }
            EditorApplication.update += Poll;
            AssemblyReloadEvents.beforeAssemblyReload += Reloading;
        }

        private static Result ResultOf(Task<Result> task, Job job)
        {
            if (task.IsCanceled) return new Result { Success = false, ExitCode = -1, Cancelled = true, LogPath = job.LogPath, Summary = "Cancelled before Blender started." };
            if (task.IsFaulted) return new Result { Success = false, ExitCode = -1, LogPath = job.LogPath, Summary = task.Exception?.GetBaseException().Message };
            return task.Result;
        }

        private static bool LooksLikeError(string line) =>
            line.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("Traceback", StringComparison.Ordinal) >= 0 ||
            line.IndexOf("Exception", StringComparison.Ordinal) >= 0;

        /// <summary>
        /// One argument as the command line gives it to the program (Windows' rules, which Mono follows elsewhere too):
        /// quoted when it holds a space, tab or quote; backslashes doubled only before a quote and before the closing
        /// quote, so a path keeps its single ones ("C:\My Files\a.blend" stays so).
        /// </summary>
        public static string Quote(string argument)
        {
            if (string.IsNullOrEmpty(argument)) return "\"\"";
            if (argument.IndexOfAny(new[] { ' ', '"', '\t', '\n', '\v' }) < 0) return argument;
            var quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"') quoted.Append('\\', backslashes * 2 + 1);
                else quoted.Append('\\', backslashes);
                quoted.Append(c);
                backslashes = 0;
            }
            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }
    }
}
