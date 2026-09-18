using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteCommanderTray.Windows;

/// <summary>
/// A kill-on-close job object that every agent process is assigned to.
/// </summary>
/// <remarks>
/// <para>
/// One job is created per agent generation, not one per tray. A shared job only dies when
/// the tray disposes it, so a descendant that outlived its own root process - the local
/// MCP node.exe an npm shim spawned, say - would survive every restart until the tray
/// exited. Owning the job per generation means it can be terminated the moment that
/// generation ends.
/// </para>
/// <para>
/// Kill-on-close is still the backstop for a tray that is killed outright from Task
/// Manager: Windows tears the job down when the last handle to it closes.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private IntPtr _handle;

    public JobObject()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateJobObject failed (error {Marshal.GetLastWin32Error()}).");
        }

        var limits = new JobObjectExtendedLimitInformationStruct
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                // Kill on close only. Breakaway is deliberately not permitted: a
                // grandchild that escaped the job is exactly the leftover agent this
                // exists to prevent.
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                throw new InvalidOperationException(
                    $"SetInformationJobObject failed (error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Adds a process to the job.</summary>
    /// <exception cref="InvalidOperationException">
    /// The process could not be assigned, which means nothing guarantees its descendants
    /// will be cleaned up. The caller decides whether that is fatal.
    /// </exception>
    public void Assign(IntPtr processHandle)
    {
        if (_handle == IntPtr.Zero || processHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The job object or the process handle is not available.");
        }

        if (!AssignProcessToJobObject(_handle, processHandle))
        {
            throw new InvalidOperationException(
                $"AssignProcessToJobObject failed (error {Marshal.GetLastWin32Error()}).");
        }
    }

    /// <summary>
    /// Kills every process still in the job, including descendants whose own root has
    /// already exited. Safe to call more than once.
    /// </summary>
    public void Terminate()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        // A job with no live members returns false with ERROR_ACCESS_DENIED on some
        // builds; there is nothing to do about it and nothing left to kill.
        TerminateJobObject(_handle, 0);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int infoClass,
        IntPtr info,
        uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
