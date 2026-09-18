using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RemoteCommanderTray.Core;

namespace RemoteCommanderTray.Windows;

/// <summary>Hidden, bounded-output process born inside its generation's Job Object.
/// No uncontained fallback, PID-tree guessing or global process-name killing.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAgentProcess : IAgentProcess
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
    private readonly AgentLaunchSpec _spec;
    private readonly JobObject _job = new();
    private readonly TaskCompletionSource<int?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _readCancellation = new();
    private SafeKernelHandle? _handle;
    private AnonymousPipeServerStream? _stdout, _stderr, _stdin;
    private WaitHandle? _waitHandle;
    private RegisteredWaitHandle? _registeredWait;
    private Task? _monitor;
    private int _disposed;
    private volatile bool _hasExited = true;
    private bool _started;

    public WindowsAgentProcess(AgentLaunchSpec spec) => _spec = spec;
    public event EventHandler<AgentOutputLine>? OutputReceived;
    public event EventHandler<int?>? Exited;
    public int? ProcessId { get; private set; }
    public bool HasExited => _hasExited;
    internal Task<int?> Completion => _completion.Task;
    internal uint ActiveProcessCount => _job.ActiveProcessCount;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_started) throw new InvalidOperationException("A process generation can only be started once.");
        _started = true;
        var executable = ResolveExecutable(_spec.FileName);
        _stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        _stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        _stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var attributes = new ProcessAttributes(_job, _stdin.ClientSafePipeHandle,
            _stdout.ClientSafePipeHandle, _stderr.ClientSafePipeHandle);
        var startup = new NativeMethods.StartupInfoEx
        {
            Info = new NativeMethods.StartupInfo
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.StartupInfoEx>(),
                Flags = NativeMethods.StartfUseStdHandles,
                StandardInput = _stdin.ClientSafePipeHandle.DangerousGetHandle(),
                StandardOutput = _stdout.ClientSafePipeHandle.DangerousGetHandle(),
                StandardError = _stderr.ClientSafePipeHandle.DangerousGetHandle(),
            },
            Attributes = attributes.Pointer,
        };
        if (!NativeMethods.CreateProcessW(executable, new StringBuilder($"\"{executable}\" {_spec.Arguments}"),
            IntPtr.Zero, IntPtr.Zero, true, NativeMethods.CreateNoWindow | NativeMethods.ExtendedStartupInfoPresent,
            IntPtr.Zero, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the contained agent process.");

        _handle = new SafeKernelHandle(info.Process);
        using var thread = new SafeKernelHandle(info.Thread);
        ProcessId = checked((int)info.ProcessId);
        _hasExited = false;
        // The child inherited only its three stdio handles, NEVER the owning job handle.
        _stdout.DisposeLocalCopyOfClientHandle();
        _stderr.DisposeLocalCopyOfClientHandle();
        _stdin.DisposeLocalCopyOfClientHandle();
        var rootExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waitHandle = new ProcessWaitHandle(_handle);
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(_waitHandle,
            (_, _) => rootExit.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        _monitor = ObserveAsync(rootExit.Task, ReadOutputAsync(_stdout, false), ReadOutputAsync(_stderr, true));
    }

    private async Task ObserveAsync(Task rootExit, Task stdout, Task stderr)
    {
        int? exitCode = null;
        try
        {
            await rootExit.ConfigureAwait(false);
            _hasExited = true;
            if (_handle is not null && NativeMethods.GetExitCodeProcess(_handle, out var code)) exitCode = unchecked((int)code);
            // Root exit is not group exit. Do not publish Completion until descendants
            // are gone, including children holding redirected pipe handles open.
            await _job.TerminateAndDrainAsync(DrainTimeout).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(DrainTimeout).ConfigureAwait(false);
            _completion.TrySetResult(exitCode);
        }
        catch (Exception ex)
        {
            _readCancellation.Cancel();
            _completion.TrySetException(ex);
        }
        finally
        {
            Exited?.Invoke(this, exitCode);
        }
    }

    private async Task ReadOutputAsync(Stream stream, bool isError)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), true, 4096, leaveOpen: true);
        var buffer = new char[4096];
        var line = new StringBuilder();
        var oversized = false;
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), _readCancellation.Token).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    var c = buffer[i];
                    if (c == '\n')
                    {
                        Emit(oversized ? "<oversized agent output omitted>" : line.ToString().TrimEnd('\r'), isError);
                        line.Clear();
                        oversized = false;
                    }
                    else if (line.Length < 8192) line.Append(c);
                    else oversized = true;
                }
            }
            if (line.Length > 0 || oversized) Emit(oversized ? "<oversized agent output omitted>" : line.ToString(), isError);
        }
        catch (OperationCanceledException) when (_readCancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed != 0) { }
    }

    private void Emit(string line, bool isError) => OutputReceived?.Invoke(this, new(line, isError));

    public async Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        if (_handle is null) return;
        _job.Terminate();
        // Cancellation cannot bypass cleanup: terminate first, then bound the wait.
        await Completion.WaitAsync(gracePeriod, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            // Also covers a partial Start failure before the monitor was installed.
            _job.TerminateAndDrainAsync(DrainTimeout).GetAwaiter().GetResult();
            if (_monitor is not null) Completion.WaitAsync(DrainTimeout).GetAwaiter().GetResult();
        }
        finally
        {
            _readCancellation.Cancel();
            _registeredWait?.Unregister(null);
            _waitHandle?.Dispose();
            _stdin?.Dispose();
            _stdout?.Dispose();
            _stderr?.Dispose();
            _handle?.Dispose();
            _job.Dispose();
            _hasExited = true;
        }
    }

    private static string ResolveExecutable(string executable)
    {
        if (Path.IsPathRooted(executable)) return executable;
        foreach (var directory in new[] { Environment.SystemDirectory }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim().Trim('"'), executable);
            if (File.Exists(candidate)) return candidate;
            if (File.Exists(candidate + ".exe")) return candidate + ".exe";
        }
        throw new FileNotFoundException("The agent executable was not found on PATH.");
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        // Owner outlives the registered wait; this borrowed handle does not own the process.
        public ProcessWaitHandle(SafeKernelHandle owner)
            => SafeWaitHandle = new SafeWaitHandle(owner.DangerousGetHandle(), ownsHandle: false);
    }

    private sealed class ProcessAttributes : IDisposable
    {
        private IntPtr _handles, _job;
        private bool _initialized;
        public IntPtr Pointer { get; private set; }
        public ProcessAttributes(JobObject job, params SafePipeHandle[] handles)
        {
            try
            {
                nuint size = 0;
                NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
                if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                Pointer = Marshal.AllocHGlobal(checked((int)size));
                if (!NativeMethods.InitializeProcThreadAttributeList(Pointer, 2, 0, ref size))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                _initialized = true;
                _handles = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
                for (var i = 0; i < handles.Length; i++) Marshal.WriteIntPtr(_handles, i * IntPtr.Size, handles[i].DangerousGetHandle());
                Set(NativeMethods.HandleList, _handles, (nuint)(handles.Length * IntPtr.Size));
                _job = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(_job, job.Handle.DangerousGetHandle());
                Set(NativeMethods.JobList, _job, (nuint)IntPtr.Size);
            }
            catch { Dispose(); throw; }
        }
        private void Set(nuint key, IntPtr value, nuint size)
        {
            if (!NativeMethods.UpdateProcThreadAttribute(Pointer, 0, key, value, size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose()
        {
            if (_initialized) NativeMethods.DeleteProcThreadAttributeList(Pointer);
            Marshal.FreeHGlobal(Pointer);
            Marshal.FreeHGlobal(_handles);
            Marshal.FreeHGlobal(_job);
            Pointer = _handles = _job = IntPtr.Zero;
            _initialized = false;
        }
    }
}
