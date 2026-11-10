using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Orbiters.Toolkit.Editor.Processes
{
    /// <summary>
    /// A process with its pipes, owned like every process the runner starts (a job of its own, stopped at reload and quit):
    /// either one command run to its end by <see cref="EditorProcessRunner.Run(EditorProcessRequest)"/>, or a process kept
    /// alive and asked one thing after the other (Git's <c>cat-file --batch</c>, a <c>log</c> read page by page). Stdout must
    /// be redirected; stdin and stderr may be. Not thread-safe: one owner reads it at a time; only stdin may be written from
    /// another thread. Dispose stops it and everything it started, and only then closes its stdin, so input cut short is
    /// never read to its end.
    /// </summary>
    public sealed class EditorLiveProcess : IDisposable
    {
        internal const int MaxErrorCharacters = 64 * 1024;
        private readonly OwnedProcess process;
        private readonly ProcessPipe output, error;
        private readonly ProcessText errorText;
        private readonly byte[] errorChunk = new byte[16 * 1024];
        private readonly Stream input;
        private readonly bool write;
        private int inputClosed;
        // Bytes read from stdout and not yet consumed by a Read* call: buffer[start..end).
        private byte[] buffer = new byte[64 * 1024];
        private int start, end;
        private int disposed;

        /// <summary>The last bytes <see cref="ReadOutput"/> read.</summary>
        internal readonly byte[] Chunk = new byte[64 * 1024];

        private EditorLiveProcess(OwnedProcess process, bool write, Action<string> errorLine, int maxErrorCharacters)
        {
            this.process = process;
            this.write = write;
            if (process.Output != null) output = new ProcessPipe(process.Output);
            if (process.Error != null)
            {
                error = new ProcessPipe(process.Error);
                errorText = new ProcessText(process.ErrorEncoding, errorLine, true, maxErrorCharacters);
            }
            // The bytes written are all the process reads: no writer, so no preamble.
            input = process.Input;
            if (input == null) inputClosed = 1;
        }

        /// <summary>
        /// Starts a process kept alive between requests. Null when the editor is stopping (reload, quit); throws when it
        /// cannot start. <paramref name="write"/>: it changes something, and a reload or quit lets it finish (up to
        /// <see cref="EditorProcessRunner.WriteGraceMilliseconds"/>) instead of stopping it at once. A redirected stdin is
        /// clean: what <see cref="Write"/> sends is all the process reads (see <see cref="EditorProcessRunner.CleanInput"/>).
        /// </summary>
        public static EditorLiveProcess Start(ProcessStartInfo startInfo, bool write = false)
        {
            if (startInfo == null) throw new ArgumentNullException(nameof(startInfo));
            if (!startInfo.RedirectStandardOutput) throw new ArgumentException("A kept-alive process needs its stdout redirected.", nameof(startInfo));
            if (startInfo.RedirectStandardInput) EditorProcessRunner.UseCleanInput(startInfo);
            return Start(startInfo, write, null, MaxErrorCharacters);
        }

        internal static EditorLiveProcess Start(ProcessStartInfo startInfo, bool write, Action<string> errorLine, int maxErrorCharacters)
        {
            OwnedProcess process = EditorProcessLifetime.Start(startInfo, write);
            if (process == null) return null;
            try
            {
                return new EditorLiveProcess(process, write, errorLine, maxErrorCharacters);
            }
            catch
            {
                EditorProcessLifetime.Stop(process);
                process.Dispose();
                throw;
            }
        }

        /// <summary>The process's stdin (null when not redirected). Write the bytes themselves: no writer, no preamble.</summary>
        public Stream Input => input;
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        /// <summary>What it wrote to stderr so far (read by the Read* calls and <see cref="DrainError"/>), at most 64K characters.</summary>
        public string StandardError => errorText?.Text ?? string.Empty;
        public bool IsDisposed => disposed != 0;
        /// <summary>Whether it ended because it was stopped (timeout, cancel, reload) rather than by itself.</summary>
        public bool WasStopped => EditorProcessLifetime.WasStopped(process);

        internal bool OutputClosed => (output == null || output.Closed) && start == end;
        internal bool ErrorClosed => error == null || error.Closed;
        internal bool JobEmpty => EditorProcessLifetime.RunningInJob(process) == 0;
        internal Encoding OutputEncoding => process.OutputEncoding;
        internal void FinishError() => errorText?.Finish();

        /// <summary>Closes stdin: the process reads its end. Safe to call more than once.</summary>
        public void CloseInput()
        {
            if (Interlocked.Exchange(ref inputClosed, 1) != 0) return;
            process.CloseInput();
        }

        /// <summary>Writes to stdin and flushes. A request to a kept-alive process is small: it never fills the pipe.</summary>
        public void Write(byte[] bytes)
        {
            if (inputClosed != 0) throw new IOException("The process's input is closed.");
            try
            {
                input.Write(bytes, 0, bytes.Length);
                input.Flush();
            }
            catch (UnauthorizedAccessException ex)
            {
                // A closed pipe (the process ended): Mono reports it on a raw handle as access denied.
                throw new IOException(ex.Message, ex);
            }
        }

        /// <summary>Reads what stdout has waiting into <see cref="Chunk"/>: the count, 0 when nothing waits, -1 at its end.
        /// For the runner only: it bypasses the buffer the Read* calls use.</summary>
        internal int ReadOutput() => output == null ? -1 : output.Read(Chunk);

        /// <summary>Reads what stderr has waiting (a process stalls once its stderr pipe is full). True when something was read.</summary>
        public bool DrainError()
        {
            if (error == null) return false;
            // A bounded share per call (the pipe's own size, or so): a process that never stops writing to stderr must
            // not keep the caller from its deadline, cancel and shutdown checks, or from stdout.
            bool any = false;
            for (int reads = 0; reads < MaxErrorReadsPerDrain; reads++)
            {
                int count = error.Read(errorChunk);
                if (count <= 0) return any;
                any = true;
                errorText.Add(errorChunk, 0, count);
            }
            return any;
        }

        private const int MaxErrorReadsPerDrain = 4;

        /// <summary>
        /// Waits until stdout has unread bytes. False at stdout's end, on timeout, on cancel and while the editor stops;
        /// <paramref name="timedOut"/> tells a timeout apart.
        /// </summary>
        private bool Fill(Stopwatch elapsed, int timeoutMilliseconds, Func<bool> cancelled, out bool timedOut)
        {
            timedOut = false;
            if (output == null) return false;
            var idle = new IdleWait();
            while (start == end)
            {
                if (disposed != 0 || EditorProcessLifetime.ShouldStop(write) || cancelled?.Invoke() == true) return false;
                if (elapsed.ElapsedMilliseconds >= timeoutMilliseconds) { timedOut = true; return false; }
                if (buffer.Length - end < Chunk.Length && start > 0)
                {
                    Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                    end -= start;
                    start = 0;
                }
                if (buffer.Length - end < Chunk.Length) Array.Resize(ref buffer, buffer.Length * 2);
                int count = output.Read(Chunk);
                bool drained = DrainError();
                if (count < 0) return false;
                if (count > 0)
                {
                    Buffer.BlockCopy(Chunk, 0, buffer, end, count);
                    end += count;
                    return true;
                }
                if (drained) idle.Reset();
                else idle.Wait();
            }
            return true;
        }

        /// <summary>
        /// Reads up to and without the next <paramref name="delimiter"/> into <paramref name="record"/> (cleared first), and
        /// consumes the delimiter. False when stdout ended, timed out or was cancelled before one came; a last record without
        /// a delimiter is still returned (true) when stdout ends after it.
        /// </summary>
        public bool ReadRecord(byte delimiter, MemoryStream record, int timeoutMilliseconds, Func<bool> cancelled, out bool timedOut)
        {
            record.SetLength(0);
            var elapsed = Stopwatch.StartNew();
            timedOut = false;
            while (true)
            {
                int at = Array.IndexOf(buffer, delimiter, start, end - start);
                if (at >= 0)
                {
                    record.Write(buffer, start, at - start);
                    start = at + 1;
                    return true;
                }
                record.Write(buffer, start, end - start);
                start = end = 0;
                if (!Fill(elapsed, timeoutMilliseconds, cancelled, out timedOut))
                    return !timedOut && record.Length > 0 && output != null && output.Closed;
            }
        }

        /// <summary>Copies exactly <paramref name="count"/> bytes of stdout to <paramref name="sink"/> (null: skipped).</summary>
        public bool ReadExactly(long count, Stream sink, int timeoutMilliseconds, Func<bool> cancelled, out bool timedOut)
        {
            var elapsed = Stopwatch.StartNew();
            timedOut = false;
            while (count > 0)
            {
                if (start == end && !Fill(elapsed, timeoutMilliseconds, cancelled, out timedOut)) return false;
                int take = (int)Math.Min(count, end - start);
                sink?.Write(buffer, start, take);
                start += take;
                count -= take;
            }
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try
            {
                EditorProcessLifetime.Stop(process);
            }
            finally
            {
                CloseInput();
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// The pause between two looks at a pipe with nothing in it. Thread.Sleep(1) lasts a whole timer tick on Windows (about
    /// 15 ms), far longer than a kept-alive process takes to answer one request: the first two milliseconds only yield, then
    /// the wait grows to 1 and 5 ms ticks.
    /// </summary>
    internal struct IdleWait
    {
        private Stopwatch since;

        internal void Reset() => since = null;

        internal void Wait()
        {
            if (since == null) since = Stopwatch.StartNew();
            long waited = since.ElapsedMilliseconds;
            if (waited < 2) Thread.Yield();
            else Thread.Sleep(waited < 50 ? 1 : 5);
        }
    }
}
