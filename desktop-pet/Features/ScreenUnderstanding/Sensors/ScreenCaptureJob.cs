using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopPet.ScreenUnderstanding;

// This job owns exactly one helper process. Closing it never terminates the
// target application, Ollama, a child of the target, or an unrelated process.
internal sealed class ScreenCaptureJob : IDisposable
{
    private readonly SafeFileHandle _handle;
    private ScreenCaptureJob(SafeFileHandle handle) => _handle = handle;
    internal static ScreenCaptureJob? Attach(Process process)
    {
        var handle = Native.CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        var limits = new ExtendedLimits();
        limits.Basic.LimitFlags = 0x00002000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!Native.SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()) ||
            !Native.AssignProcessToJobObject(handle, process.Handle))
        { handle.Dispose(); return null; }
        return new ScreenCaptureJob(handle);
    }
    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, int size);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }
}
