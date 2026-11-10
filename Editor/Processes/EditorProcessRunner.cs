using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Orbiters.Toolkit.Editor.Processes
{
    public sealed class EditorProcessResult
    {
        public int ExitCode = -1;
        public bool TimedOut, Cancelled;
        /// <summary>The <see cref="EditorProcessRequest.Output"/> callback asked to stop: the process was stopped.</summary>
        public bool Stopped;
        /// <summary>The process stopped reading its input before the end (it failed); its exit code and errors say why.</summary>
        public bool InputFailed;
        /// <summary>The process exited, but a program it started kept its output open: what was read may be incomplete.</summary>
        public bool OutputHeldOpen;
        public string StandardOutput = string.Empty, StandardError = string.Empty;
        public bool Success => ExitCode == 0 && !TimedOut && !Cancelled && !Stopped && !InputFailed;
    }

    /// <summary>One process for <see cref="EditorProcessRunner.Run(EditorProcessRequest)"/>.</summary>
    public sealed class EditorProcessRequest
    {
        public ProcessStartInfo StartInfo;
        public int TimeoutMilliseconds;
        public Func<bool> Cancelled;
        /// <summary>
        /// The process's whole stdin, complete before it starts (nothing can fail halfway through a list but the pipe itself),
        /// written on a thread of its own while the outputs are read, so no pipe can fill up and stall both sides. Stdin is
        /// closed only after the last byte: a list cut short never reaches the process as a complete one. Needs
        /// <see cref="ProcessStartInfo.RedirectStandardInput"/> and <see cref="EditorProcessRunner.CleanInput"/>. Null: a
        /// redirected stdin is closed at once.
        /// </summary>
        public byte[] Input;
        /// <summary>
        /// The process changes something a user owns (a repository, a project's files): a reload or quit lets it finish (up
        /// to <see cref="EditorProcessRunner.WriteGraceMilliseconds"/>, its output still read) instead of stopping it at once.
        /// </summary>
        public bool Write;
        /// <summary>Stdout as raw bytes, as they come. Return false to stop: the process is stopped and the result says
        /// <see cref="EditorProcessResult.Stopped"/>. When set, stdout is neither kept as text nor split into lines.</summary>
        public Func<byte[], int, int, bool> Output;
        /// <summary>Stdout's bytes copied here as they come (no text kept). The caller owns it; the runner never closes it.</summary>
        public Stream OutputDestination;
        /// <summary>Each line of stdout (only when stdout is kept as text) and of stderr.</summary>
        public Action<string> OutputLine, ErrorLine;
    }

    /// <summary>
    /// Owned editor jobs with synchronous, readiness-checked pipe reads and bounded teardown. Every process runs in a job of
    /// its own, all starts share one lock (no process takes another's pipe ends with it), and all are stopped at reload and
    /// quit; writes get a grace to finish first. Call from a background worker for long operations; callbacks run on it.
    /// </summary>
    public static class EditorProcessRunner
    {
        public static bool IsStopping => EditorProcessLifetime.IsStopping;
        public static int ActiveCount => EditorProcessLifetime.ActiveCount;
        /// <summary>How long a reload or quit lets writes in flight finish before it stops them.</summary>
        public const int WriteGraceMilliseconds = EditorProcessLifetime.WriteGraceMilliseconds;
        private const int DrainTimeoutMilliseconds = 1000;
        private const int InputBlockBytes = 64 * 1024;

        // Mono's Process writes the encoding's preamble to stdin as soon as the process starts (its StreamWriter flushes at
        // once), with Console.InputEncoding unless told otherwise: with Windows' "UTF-8 for worldwide language support" that
        // is a byte order mark in front of the first byte written. Starts with input name UTF-8 without one where the
        // runtime lets them, and the bytes go to the base stream, never through a writer.
        private static readonly PropertyInfo InputEncoding = typeof(ProcessStartInfo).GetProperty("StandardInputEncoding");
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>
        /// Whether a process's stdin starts with exactly the bytes written (the start info can name a preamble-free encoding,
        /// or the console's has none). When false, <see cref="Run(EditorProcessRequest)"/> refuses input rather than send it
        /// with a mark in front: put the list in a file instead.
        /// </summary>
        public static bool CleanInput
        {
            get
            {
                // On Windows the runner makes the pipes itself and writes bytes: no preamble ever.
                if (Path.DirectorySeparatorChar == '\\') return true;
                if (InputEncoding != null && InputEncoding.CanWrite) return true;
                try { return Console.InputEncoding.GetPreamble().Length == 0; }
                catch (IOException) { return true; }
            }
        }

        /// <summary>Names UTF-8 without a preamble as the stdin encoding, where the runtime allows it (see <see cref="CleanInput"/>).</summary>
        public static void UseCleanInput(ProcessStartInfo startInfo)
        {
            if (startInfo != null && InputEncoding != null && InputEncoding.CanWrite) InputEncoding.SetValue(startInfo, Utf8);
        }

        /// <summary>Whether a reload or quit is under way and a process should stop now (a write in flight may finish).</summary>
        public static bool ShouldStop(bool write) => EditorProcessLifetime.ShouldStop(write);

        /// <summary>
        /// Called once when everything stops (reload, quit), before the processes are stopped: owners of kept-alive processes
        /// (<see cref="EditorLiveProcess"/>) forget them here. Register once per domain (a static constructor).
        /// </summary>
        public static void OnStopping(Action listener) => EditorProcessLifetime.OnStopping(listener);

        /// <summary>For tests: what a reload does (reads stop at once, writes get <paramref name="graceMilliseconds"/>), then
        /// starting is allowed again.</summary>
        internal static void StopAllThenResume(int graceMilliseconds) => EditorProcessLifetime.StopAllThenResume(graceMilliseconds);

        // Call from a background worker for long operations. Callbacks run on that worker.
        // The caller owns standardOutputDestination; it is never closed by the runner.
        public static EditorProcessResult Run(ProcessStartInfo startInfo, int timeoutMilliseconds,
            Func<bool> cancelled = null, Action<string> stdoutLine = null, Action<string> stderrLine = null,
            Stream standardOutputDestination = null)
        {
            return Run(new EditorProcessRequest
            {
                StartInfo = startInfo,
                TimeoutMilliseconds = timeoutMilliseconds,
                Cancelled = cancelled,
                OutputLine = stdoutLine,
                ErrorLine = stderrLine,
                OutputDestination = standardOutputDestination
            });
        }

        /// <summary>Runs one process to its end (or its timeout, cancel or early stop).</summary>
        public static EditorProcessResult Run(EditorProcessRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            ProcessStartInfo startInfo = request.StartInfo;
            if (startInfo == null) throw new ArgumentNullException(nameof(request.StartInfo));
            if (request.TimeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(request.TimeoutMilliseconds));
            bool write = request.Write;
            var result = new EditorProcessResult();
            if (IsStopping || request.Cancelled?.Invoke() == true) { result.Cancelled = true; return result; }
            if (request.Input != null)
            {
                if (!startInfo.RedirectStandardInput) throw new ArgumentException("Input needs a redirected stdin.", nameof(request));
                if (!CleanInput) throw new InvalidOperationException("The process's input would start with a byte order mark on this system.");
                UseCleanInput(startInfo);
            }

            EditorLiveProcess process = EditorLiveProcess.Start(startInfo, write, request.ErrorLine, int.MaxValue);
            if (process == null) { result.Cancelled = true; return result; }
            bool keepText = request.Output == null && request.OutputDestination == null && startInfo.RedirectStandardOutput;
            ProcessText text = keepText ? new ProcessText(process.OutputEncoding, request.OutputLine) : null;
            Thread writer = null;
            int inputFailed = 0;
            try
            {
                if (request.Input != null) writer = StartInputWriter(process, request.Input, () => Interlocked.Exchange(ref inputFailed, 1));
                else process.CloseInput();

                var elapsed = Stopwatch.StartNew();
                long exitedAt = -1;
                var idle = new IdleWait();
                while (true)
                {
                    // A write keeps running (and its output keeps being read) while a reload gives it time to finish.
                    if (ShouldStop(write) || request.Cancelled?.Invoke() == true) { result.Cancelled = true; break; }
                    if (elapsed.ElapsedMilliseconds >= request.TimeoutMilliseconds) { result.TimedOut = true; break; }

                    bool read = false;
                    int count = process.ReadOutput();
                    if (count > 0)
                    {
                        read = true;
                        if (!Deliver(request, text, process.Chunk, count)) { result.Stopped = true; break; }
                    }
                    read |= process.DrainError();

                    if (process.HasExited)
                    {
                        if (exitedAt < 0) exitedAt = elapsed.ElapsedMilliseconds;
                        if (process.OutputClosed && process.ErrorClosed)
                        {
                            // Stopped from outside (a reload's grace ran out): its exit code is the job's, not its own.
                            if (process.WasStopped) result.Cancelled = true;
                            else result.ExitCode = process.ExitCode;
                            break;
                        }
                        // Another handle to a pipe keeps it open. When nothing of the process's job runs any more, all it
                        // wrote has arrived (the holder is a process started outside the start lock that inherited the handle
                        // at the same moment, and writes nothing): what is left is read and the run is complete. Else a
                        // descendant may still write: never wait for its end without limit.
                        if (elapsed.ElapsedMilliseconds - exitedAt >= DrainTimeoutMilliseconds)
                        {
                            if (process.JobEmpty)
                            {
                                while (!result.Stopped)
                                {
                                    int rest = process.ReadOutput();
                                    if (rest <= 0) break;
                                    if (!Deliver(request, text, process.Chunk, rest)) result.Stopped = true;
                                }
                                while (process.DrainError()) { } // nothing of its job runs: what is left is finite
                                if (process.WasStopped) result.Cancelled = true;
                                else if (!result.Stopped) result.ExitCode = process.ExitCode;
                                break;
                            }
                            result.TimedOut = result.OutputHeldOpen = true;
                            break; // A descendant may still own a pipe; never wait indefinitely for EOF.
                        }
                    }

                    if (read) idle.Reset();
                    else idle.Wait();
                }
            }
            finally
            {
                // Always stop the owned job on cancellation, exceptions and ThreadAbort, not just on timeout.
                process.Dispose();
                writer?.Join(1000);
                if (Interlocked.CompareExchange(ref inputFailed, 0, 0) != 0 && result.ExitCode != 0) result.InputFailed = true;
                text?.Finish();
                process.FinishError();
                result.StandardOutput = text?.Text ?? string.Empty;
                result.StandardError = process.StandardError;
            }
            return result;
        }

        private static bool Deliver(EditorProcessRequest request, ProcessText text, byte[] bytes, int count)
        {
            if (request.Output != null) return request.Output(bytes, 0, count);
            if (request.OutputDestination != null) request.OutputDestination.Write(bytes, 0, count);
            else text?.Add(bytes, 0, count);
            return true;
        }

        // The input is complete before the process starts: only the pipe can fail here, when the process stopped reading (it
        // failed, and says why). Stdin is closed only after the last byte, so a cut list never reaches it as a complete one.
        private static Thread StartInputWriter(EditorLiveProcess process, byte[] bytes, Action failed)
        {
            Stream input = process.Input;
            var writer = new Thread(() =>
            {
                try
                {
                    for (int offset = 0; offset < bytes.Length; offset += InputBlockBytes)
                        input.Write(bytes, offset, Math.Min(InputBlockBytes, bytes.Length - offset));
                    input.Flush();
                    process.CloseInput();
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
                {
                    // A closed pipe: the process stopped reading (Mono reports it on a raw handle as access denied).
                    failed();
                }
            }) { IsBackground = true, Name = "Orbiters process stdin" };
            writer.Start();
            return writer;
        }
    }
}
