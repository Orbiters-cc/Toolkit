using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Orbiters.Toolkit.Editor.Processes
{
    // A single reader per pipe. Never schedules a Mono FileStream BeginRead callback:
    // those callbacks can be aborted before AsyncStreamReader.Dispose finishes at reload.
    internal sealed class ProcessPipe
    {
        private readonly SafeFileHandle handle;
        private readonly byte[] bytes = new byte[8192];
        private readonly char[] characters;
        private readonly Decoder decoder;
        private readonly StringBuilder text = new StringBuilder();
        private readonly StringBuilder pendingLine = new StringBuilder();
        private readonly Action<string> onLine;
        private readonly Stream destination;
        private bool atStart = true, previousCarriageReturn;
        internal bool Closed { get; private set; }
        internal string Text => text.ToString();

        internal ProcessPipe(StreamReader reader, Action<string> onLine, Stream destination = null)
        {
            handle = ((FileStream)reader.BaseStream).SafeFileHandle;
            decoder = reader.CurrentEncoding.GetDecoder();
            characters = new char[reader.CurrentEncoding.GetMaxCharCount(bytes.Length)];
            this.onLine = onLine;
            this.destination = destination;
        }

        internal bool Pump()
        {
            if (Closed) return false;
            int count = ReadAvailable();
            if (count < 0) { Finish(); return false; }
            if (count == 0) return false;
            if (destination != null) destination.Write(bytes, 0, count);
            else Append(decoder.GetChars(bytes, 0, count, characters, 0, false));
            return true;
        }

        internal void Finish()
        {
            if (Closed) return;
            Closed = true;
            if (destination != null) return;
            Append(decoder.GetChars(Array.Empty<byte>(), 0, 0, characters, 0, true));
            if (pendingLine.Length > 0) EmitLine();
        }

        private void Append(int count)
        {
            if (count == 0) return;
            int start = atStart && characters[0] == '\ufeff' ? 1 : 0;
            atStart = false;
            text.Append(characters, start, count - start);
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

        private int ReadAvailable()
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                if (!PeekNamedPipe(handle, IntPtr.Zero, 0, IntPtr.Zero, out uint available, IntPtr.Zero))
                    return PipeError(Marshal.GetLastWin32Error());
                if (available == 0) return 0;
                // No other reader consumes this handle between peek and read. Only request available bytes.
                if (!ReadFile(handle, bytes, (uint)Math.Min(bytes.Length, available), out uint read, IntPtr.Zero))
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
            long length = Read(descriptor.File, bytes, new UIntPtr((uint)bytes.Length)).ToInt64();
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
}
