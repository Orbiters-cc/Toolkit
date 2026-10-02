using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Orbiters.Toolkit.Editor.Processes
{
    public sealed class EditorProcessResult
    {
        public int ExitCode = -1;
        public bool TimedOut, Cancelled;
        public string StandardOutput = string.Empty, StandardError = string.Empty;
        public bool Success => ExitCode == 0 && !TimedOut && !Cancelled;
    }

    /// <summary>Owned editor jobs with synchronous, readiness-checked pipe reads and bounded teardown.</summary>
    public static class EditorProcessRunner
    {
        public static bool IsStopping => EditorProcessLifetime.IsStopping;
        public static int ActiveCount => EditorProcessLifetime.ActiveCount;
        private const int DrainTimeoutMilliseconds = 1000;

        // Call from a background worker for long operations. Callbacks run on that worker.
        // The caller owns standardOutputDestination; it is never closed by the runner.
        public static EditorProcessResult Run(ProcessStartInfo startInfo, int timeoutMilliseconds,
            Func<bool> cancelled = null, Action<string> stdoutLine = null, Action<string> stderrLine = null,
            Stream standardOutputDestination = null)
        {
            return RunCore(startInfo, timeoutMilliseconds, cancelled, stdoutLine, stderrLine, standardOutputDestination, true);
        }

        internal static EditorProcessResult RunCleanup(ProcessStartInfo startInfo, int timeoutMilliseconds)
        {
            return RunCore(startInfo, timeoutMilliseconds, null, null, null, null, false);
        }

        private static EditorProcessResult RunCore(ProcessStartInfo startInfo, int timeoutMilliseconds,
            Func<bool> cancelled, Action<string> stdoutLine, Action<string> stderrLine, Stream destination, bool tracked)
        {
            if (startInfo == null) throw new ArgumentNullException(nameof(startInfo));
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            var result = new EditorProcessResult();
            if ((tracked && IsStopping) || cancelled?.Invoke() == true) { result.Cancelled = true; return result; }
            using (var process = new Process { StartInfo = startInfo })
            {
                bool started = false;
                ProcessPipe stdout = null, stderr = null;
                try
                {
                    started = tracked ? EditorProcessLifetime.Start(process) : process.Start();
                    if (!started) { result.Cancelled = tracked && IsStopping; return result; }
                    if (startInfo.RedirectStandardInput) process.StandardInput.Close();
                    if (startInfo.RedirectStandardOutput) stdout = new ProcessPipe(process.StandardOutput, stdoutLine, destination);
                    if (startInfo.RedirectStandardError) stderr = new ProcessPipe(process.StandardError, stderrLine);
                    var elapsed = Stopwatch.StartNew();
                    long exitedAt = -1;
                    while (true)
                    {
                        if ((tracked && IsStopping) || cancelled?.Invoke() == true)
                        {
                            result.Cancelled = true;
                            break;
                        }
                        if (elapsed.ElapsedMilliseconds >= timeoutMilliseconds)
                        {
                            result.TimedOut = true;
                            break;
                        }
                        bool read = stdout?.Pump() == true;
                        read |= stderr?.Pump() == true;
                        if (process.HasExited)
                        {
                            if (exitedAt < 0) exitedAt = elapsed.ElapsedMilliseconds;
                            if ((stdout == null || stdout.Closed) && (stderr == null || stderr.Closed))
                            {
                                result.ExitCode = process.ExitCode;
                                break;
                            }
                            if (elapsed.ElapsedMilliseconds - exitedAt >= DrainTimeoutMilliseconds)
                            {
                                result.TimedOut = true;
                                break; // A descendant may still own a pipe; never wait indefinitely for EOF.
                            }
                        }
                        if (!read) Thread.Sleep(5);
                    }
                }
                finally
                {
                    // No Begin*ReadLine/ReadToEndAsync was used, so Process.Dispose has no async reader to wait for.
                    // Always stop an owned job on cancellation, exceptions and ThreadAbort, not just on timeout.
                    try
                    {
                        if (started && !process.HasExited)
                        {
                            if (tracked) EditorProcessLifetime.StopTree(process);
                            else process.Kill();
                        }
                    }
                    finally
                    {
                        if (tracked) EditorProcessLifetime.Remove(process);
                        stdout?.Finish(); stderr?.Finish();
                        result.StandardOutput = stdout?.Text ?? string.Empty;
                        result.StandardError = stderr?.Text ?? string.Empty;
                    }
                }
            }
            return result;
        }
    }
}
