using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Orbiters.Toolkit.Editor.Processes
{
    // A single reader per pipe, read only when bytes are waiting (PeekNamedPipe, poll): no read ever blocks, so no thread can
    // be stuck in a native read when the domain unloads, and no Mono FileStream BeginRead callback is ever scheduled (those
    // callbacks can be aborted before AsyncStreamReader.Dispose finishes at reload).
    internal sealed class ProcessPipe
    {
        private readonly SafeFileHandle handle;
        internal bool Closed { get; private set; }

        internal ProcessPipe(Stream stream)
        {
            handle = ((FileStream)stream).SafeFileHandle;
        }

        /// <summary>Bytes read into the start of <paramref name="buffer"/> (0: none waiting), or -1 once the writer closed the pipe.</summary>
        internal int Read(byte[] buffer)
        {
            if (Closed) return -1;
            int read = ReadAvailable(buffer);
            if (read < 0) Closed = true;
            return read;
        }

        private int ReadAvailable(byte[] buffer)
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                if (!PeekNamedPipe(handle, IntPtr.Zero, 0, IntPtr.Zero, out uint available, IntPtr.Zero))
                    return PipeError(Marshal.GetLastWin32Error());
                if (available == 0) return 0;
                // No other reader consumes this handle between peek and read. Only request available bytes.
                if (!ReadFile(handle, buffer, (uint)Math.Min(buffer.Length, available), out uint read, IntPtr.Zero))
                    return PipeError(Marshal.GetLastWin32Error());
                return read == 0 ? -1 : (int)read;
            }

            var descriptor = new PollDescriptor { File = handle.DangerousGetHandle().ToInt32(), Events = 1 };
            int ready = Poll(ref descriptor, new UIntPtr(1), 0);
            if (ready < 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 4) return 0; // EINTR
                throw new Win32Exception(error);
            }
            if (ready == 0) return 0;
            if ((descriptor.ReturnedEvents & 32) != 0) throw new IOException("Process pipe closed unexpectedly.");
            long length = Read(descriptor.File, buffer, new UIntPtr((uint)buffer.Length)).ToInt64();
            if (length == 0) return -1;
            if (length > 0) return (int)length;
            int readError = Marshal.GetLastWin32Error();
            if (readError == 4 || readError == 11 || readError == 35) return 0;
            throw new Win32Exception(readError);
        }

        private static int PipeError(int error)
        {
            if (error == 109 || error == 232) return -1; // broken/disconnected pipe
            throw new Win32Exception(error);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PollDescriptor { public int File; public short Events, ReturnedEvents; }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekNamedPipe(SafeFileHandle pipe, IntPtr buffer, uint size, IntPtr read, out uint available, IntPtr left);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, uint size, out uint read, IntPtr overlapped);
        [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
        private static extern int Poll(ref PollDescriptor descriptor, UIntPtr count, int timeout);
        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
        private static extern IntPtr Read(int file, byte[] buffer, UIntPtr count);
    }

    // A pipe's bytes decoded as they come: the whole text (a leading byte order mark dropped, at most maxCharacters of it) and
    // one callback per line (CR, LF or CRLF ends one; the last line needs no ending). A character split between two reads is
    // decoded whole.
    internal sealed class ProcessText
    {
        private readonly Decoder decoder;
        private readonly Encoding encoding;
        private readonly StringBuilder text;
        private readonly StringBuilder pendingLine = new StringBuilder();
        private readonly Action<string> onLine;
        private readonly int maxCharacters;
        private char[] characters = new char[1024];
        private bool atStart = true, previousCarriageReturn, finished;

        internal ProcessText(Encoding encoding, Action<string> onLine, bool keepText = true, int maxCharacters = int.MaxValue)
        {
            this.encoding = encoding ?? new UTF8Encoding(false);
            decoder = this.encoding.GetDecoder();
            this.onLine = onLine;
            this.maxCharacters = maxCharacters;
            text = keepText ? new StringBuilder() : null;
        }

        internal string Text { get { lock (pendingLine) return text?.ToString() ?? string.Empty; } }

        internal void Add(byte[] bytes, int offset, int count)
        {
            lock (pendingLine)
            {
                if (finished || count <= 0) return;
                int needed = encoding.GetMaxCharCount(count);
                if (characters.Length < needed) characters = new char[needed];
                Append(decoder.GetChars(bytes, offset, count, characters, 0, false));
            }
        }

        internal void Finish()
        {
            lock (pendingLine)
            {
                if (finished) return;
                finished = true;
                Append(decoder.GetChars(Array.Empty<byte>(), 0, 0, characters, 0, true));
                if (pendingLine.Length > 0) EmitLine();
            }
        }

        private void Append(int count)
        {
            if (count == 0) return;
            int start = atStart && characters[0] == '﻿' ? 1 : 0;
            atStart = false;
            if (text != null && text.Length < maxCharacters) text.Append(characters, start, Math.Min(count - start, maxCharacters - text.Length));
            if (onLine == null) return;
            for (int i = start; i < count; i++)
            {
                char character = characters[i];
                if (character == '\r') { EmitLine(); previousCarriageReturn = true; }
                else if (character == '\n')
                {
                    if (!previousCarriageReturn) EmitLine();
                    previousCarriageReturn = false;
                }
                else { pendingLine.Append(character); previousCarriageReturn = false; }
            }
        }

        private void EmitLine()
        {
            string line = pendingLine.ToString();
            pendingLine.Clear();
            onLine(line);
        }
    }
}
