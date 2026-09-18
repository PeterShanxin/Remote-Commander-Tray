using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RemoteCommanderTray.Core;

namespace RemoteCommanderTray.Windows;

/// <summary>A process created atomically inside its job, before any user code runs.</summary>
/// <remarks>
/// JOB_LIST closes the create-then-assign race, including a tray crash in that gap.
/// Only the three standard stream handles are inherited; never the job handle.
/// https://devblogs.microsoft.com/oldnewthing/20230209-00/?p=107812
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class ContainedProcess : IDisposable
{
    private readonly SafeFileHandle _thread;
    public Process Process { get; }
    public StreamReader Output { get; }
    public StreamReader Error { get; }

    private ContainedProcess(Process process, SafeFileHandle thread, SafeFileHandle stdout, SafeFileHandle stderr)
    {
        Process = process;
        _thread = thread;
        Output = new StreamReader(new FileStream(stdout, FileAccess.Read), Encoding.UTF8);
        Error = new StreamReader(new FileStream(stderr, FileAccess.Read), Encoding.UTF8);
    }

    public void Resume()
    {
        if (ResumeThread(_thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        _thread.Dispose();
    }

    public static ContainedProcess Create(AgentLaunchSpec spec, JobObject job)
    {
        if (!Path.IsPathFullyQualified(spec.FileName) || spec.FileName.Contains('"')
            || spec.FileName.Contains('\0') || spec.Arguments.Contains('\0'))
            throw new ArgumentException("The agent executable must be an absolute path without quotes or NULs.");

        var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
        SafeFileHandle? outRead = null, outWrite = null, errRead = null, errWrite = null;
        SafeFileHandle? inRead = null, inWrite = null;
        IntPtr attributes = IntPtr.Zero, jobValue = IntPtr.Zero, handles = IntPtr.Zero, environment = IntPtr.Zero;
        var initialized = false;
        ProcessInformation pi = default;
        Process? process = null;
        SafeFileHandle? thread = null;
        try
        {
            Check(CreatePipe(out outRead, out outWrite, ref security, 0));
            Check(CreatePipe(out errRead, out errWrite, ref security, 0));
            Check(CreatePipe(out inRead, out inWrite, ref security, 0));
            Check(SetHandleInformation(outRead, 1, 0));
            Check(SetHandleInformation(errRead, 1, 0));
            Check(SetHandleInformation(inWrite, 1, 0));

            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            attributes = Marshal.AllocHGlobal(checked((nint)size));
            Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref size));
            initialized = true;
            jobValue = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobValue, job.Handle);
            Check(UpdateProcThreadAttribute(attributes, 0, 0x0002000D, jobValue, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));

            handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handles, 0, inRead.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, outWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, errWrite.DangerousGetHandle());
            Check(UpdateProcThreadAttribute(attributes, 0, 0x00020002, handles, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));

            var startup = new StartupInfoEx
            {
                Info = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = 0x00000101, // USESTDHANDLES | USESHOWWINDOW, hidden.
                    Input = inRead.DangerousGetHandle(),
                    Output = outWrite.DangerousGetHandle(),
                    Error = errWrite.DangerousGetHandle(),
                },
                Attributes = attributes,
            };
            var env = new ProcessStartInfo().Environment;
            env["NODE_NO_READLINE"] = "1";
            env["FORCE_COLOR"] = "0";
            environment = Marshal.StringToHGlobalUni(string.Join('\0',
                env.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}={x.Value}")) + "\0\0");
            var command = new StringBuilder($"\"{spec.FileName}\" {spec.Arguments}");
            Check(CreateProcessW(spec.FileName, command, IntPtr.Zero, IntPtr.Zero, true,
                0x08080404, // NO_WINDOW | EXTENDED_STARTUPINFO | UNICODE_ENVIRONMENT | SUSPENDED
                environment, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ref startup, out pi));
            process = Process.GetProcessById((int)pi.ProcessId);
            _ = process.Handle;
            thread = new SafeFileHandle(pi.Thread, true);
            pi.Thread = IntPtr.Zero;
            var result = new ContainedProcess(process, thread, outRead, errRead);
            process = null; thread = null; outRead = null; errRead = null;
            return result;
        }
        catch
        {
            try { job.TerminateAndWait(TimeSpan.FromSeconds(5)); }
            finally { process?.Dispose(); thread?.Dispose(); }
            throw;
        }
        finally
        {
            if (pi.Thread != IntPtr.Zero) CloseHandle(pi.Thread);
            if (pi.Process != IntPtr.Zero) CloseHandle(pi.Process);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(jobValue);
            Marshal.FreeHGlobal(handles); Marshal.FreeHGlobal(environment);
            outRead?.Dispose(); outWrite?.Dispose(); errRead?.Dispose(); errWrite?.Dispose();
            inRead?.Dispose(); inWrite?.Dispose(); // stdin is EOF, not an interactive prompt.
        }
    }

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose()
    {
        _thread.Dispose(); Output.Dispose(); Error.Dispose(); Process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Size;
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
        public ushort Show, ReservedSize;
        public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
