using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using RemoteCommanderTray.Core;

namespace RemoteCommanderTray.Windows;

/// <summary>
/// Launches the official CLI as a hidden child process with both streams redirected.
/// </summary>
/// <remarks>
/// <c>CreateNoWindow</c> plus <c>UseShellExecute = false</c> is what keeps a console
/// window from flashing at sign-in. Because there is no console, there is also no way to
/// send Ctrl+C, so stopping means killing the process tree; the device's own heartbeat
/// timeout is what marks it offline server-side.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAgentProcess : IAgentProcess
{
    private readonly AgentLaunchSpec _spec;
    private readonly JobObject? _job;
    private readonly Process _process;
    private int _exitRaised;
    private bool _disposed;

    public WindowsAgentProcess(AgentLaunchSpec spec, JobObject? job)
    {
        _spec = spec;
        _job = job;
        _process = new Process
        {
            StartInfo = CreateStartInfo(spec),
            EnableRaisingEvents = true,
        };

        _process.OutputDataReceived += (_, e) => Emit(e.Data, isError: false);
        _process.ErrorDataReceived += (_, e) => Emit(e.Data, isError: true);
        _process.Exited += (_, _) => RaiseExited();
    }

    public event EventHandler<AgentOutputLine>? OutputReceived;

    public event EventHandler<int?>? Exited;

    public int? ProcessId { get; private set; }

    public bool HasExited
    {
        get
        {
            try
            {
                return ProcessId is null || _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public void Start()
    {
        if (!_process.Start())
        {
            throw new InvalidOperationException($"Could not start {_spec.Description}.");
        }

        ProcessId = _process.Id;

        // Assign before the child gets far, so anything it spawns is inside the job too.
        _job?.TryAssign(_process.Handle);

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public async Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        if (ProcessId is null)
        {
            return;
        }

        try
        {
            if (_process.HasExited)
            {
                return;
            }

            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or gone by the time the kill landed.
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(gracePeriod);
        try
        {
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The job object is the backstop for anything that refuses to die.
        }
    }

    private static ProcessStartInfo CreateStartInfo(AgentLaunchSpec spec)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            Arguments = spec.Arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        // Keeps Node from buffering its status lines behind a pipe, so the tray icon
        // reflects what the device is doing rather than what it did a minute ago.
        startInfo.Environment["NODE_NO_READLINE"] = "1";
        startInfo.Environment["FORCE_COLOR"] = "0";
        return startInfo;
    }

    private void Emit(string? data, bool isError)
    {
        if (data is null)
        {
            return;
        }

        OutputReceived?.Invoke(this, new AgentOutputLine(data, isError));
    }

    private void RaiseExited()
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) != 0)
        {
            return;
        }

        int? code;
        try
        {
            code = _process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            code = null;
        }

        Exited?.Invoke(this, code);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _process.Dispose();
        }
        catch (InvalidOperationException)
        {
            // Nothing to release.
        }
    }
}
