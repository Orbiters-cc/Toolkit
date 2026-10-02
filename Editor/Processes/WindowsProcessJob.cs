using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Orbiters.Toolkit.Editor.Processes
{
    // Keep descendants owned even after their launcher exits and leaves a pipe open.
    // Closing the job at reload does not depend on running managed completion callbacks.
    internal sealed class WindowsProcessJob : SafeHandleZeroOrMinusOneIsInvalid
    {
        private WindowsProcessJob() : base(true) { }

        internal static WindowsProcessJob Create()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new Win32Exception(error);
            }
            try
            {
                var limits = new ExtendedLimits();
                limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return job;
            }
            catch { job.Dispose(); throw; }
        }

        internal void Assign(Process process)
        {
            if (!AssignProcessToJobObject(this, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern WindowsProcessJob CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(WindowsProcessJob job, int infoClass, ref ExtendedLimits limits, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(WindowsProcessJob job, IntPtr process);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
