using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteCommanderTray.Windows;

/// <summary>One generation's non-inheritable kill-on-close job. Windows 10+ atomic
/// JOB_LIST process creation prevents a child from escaping before assignment.</summary>
[SupportedOSPlatform("windows")]
internal sealed class JobObject : IDisposable
{
    internal SafeKernelHandle Handle { get; }

    public JobObject()
    {
        Handle = CreateJobObjectW(IntPtr.Zero, null);
        if (Handle.IsInvalid) { Handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (!SetInformationJobObject(Handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Handle.Dispose(); throw; }
    }

    internal uint ActiveProcessCount
    {
        get
        {
            if (!QueryInformationJobObject(Handle, 1, out var accounting, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return accounting.ActiveProcesses;
        }
    }

    internal void Terminate()
    {
        if (!TerminateJobObject(Handle, 1))
        {
            var error = Marshal.GetLastWin32Error();
            if (ActiveProcessCount != 0) throw new Win32Exception(error);
        }
    }

    internal async Task TerminateAndDrainAsync(TimeSpan timeout)
    {
        Terminate();
        var elapsed = Stopwatch.StartNew();
        while (ActiveProcessCount != 0)
        {
            if (elapsed.Elapsed >= timeout) throw new TimeoutException("Agent process group did not drain.");
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    public void Dispose() => Handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeKernelHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeKernelHandle job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeKernelHandle job, int infoClass, out Accounting info, uint length, IntPtr returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeKernelHandle job, uint exitCode);
}
