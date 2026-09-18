using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace RemoteCommanderTray.Windows;

/// <summary>One kill-on-close container per agent generation, never shared between restarts.</summary>
[SupportedOSPlatform("windows")]
internal sealed class JobObject : IDisposable
{
    private readonly SafeFileHandle _handle;
    internal IntPtr Handle => _handle.DangerousGetHandle();

    public JobObject()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits
        {
            Basic = new BasicLimits { Flags = 0x2000 }, // KILL_ON_JOB_CLOSE; no breakaway.
        };
        try
        {
            if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { _handle.Dispose(); throw; }
    }

    /// <summary>Do not start a replacement until all descendants have actually terminated.</summary>
    public void TerminateAndWait(TimeSpan timeout)
    {
        if (_handle.IsClosed) return;
        if (ActiveProcesses() == 0) return;
        if (!TerminateJobObject(_handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var clock = Stopwatch.StartNew();
        while (ActiveProcesses() != 0)
        {
            if (clock.Elapsed >= timeout)
                throw new TimeoutException("The agent job did not drain; replacement is blocked.");
            Thread.Sleep(20);
        }
    }

    private uint ActiveProcesses()
    {
        if (!QueryInformationJobObject(_handle, 1, out Accounting info,
            (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.ActiveProcesses;
    }

    // SafeHandle is also the crash/finalization backstop. The explicit drain above is
    // still required for normal Stop/Restart so generations never overlap.
    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
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
        public long TotalUser, TotalKernel, PeriodUser, PeriodKernel;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out Accounting info, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
