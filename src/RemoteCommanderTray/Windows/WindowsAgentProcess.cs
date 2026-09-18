using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using RemoteCommanderTray.Core;

namespace RemoteCommanderTray.Windows;

/// <summary>
/// Launches the official CLI as a hidden child process with both streams redirected.
/// </summary>
/// <remarks>
/// <para>
/// <c>CreateNoWindow</c> plus <c>UseShellExecute = false</c> is what keeps a console
/// window from flashing at sign-in. Because there is no console, there is also no way to
/// send Ctrl+C, so stopping means killing the process tree; the device's own heartbeat
/// timeout is what marks it offline server-side.
/// </para>
/// <para>
/// This instance owns a job object covering the whole generation it starts. Killing the
/// tree from the root is not enough on its own: once the root has exited, its surviving
/// descendants have no common parent left to walk, and only the job can reach them.
/// So the job is terminated on stop <em>and</em> on dispose after a natural exit.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAgentProcess : IAgentProcess
{
    private readonly AgentLaunchSpec _spec;
    private readonly JobObject? _job;
    private readonly bool _requireJob;
    private readonly Action<string> _log;
    private readonly Process _process;
    private int _exitRaised;
    private bool _disposed;

    public WindowsAgentProcess(AgentLaunchSpec spec, JobObject? job, bool requireJob, Action<string> log)
    {
        _spec = spec;
        _job = job;
        _requireJob = requireJob;
        _log = log;
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
        if (_job is null && _requireJob)
        {
            throw new InvalidOperationException(
                "No job object is available, so an orphaned agent could survive the tray. "
                + "Set \"requireJobObject\": false in settings.json to start anyway.");
        }

        if (!_process.Start())
        {
            throw new InvalidOperationException($"Could not start {_spec.Description}.");
        }

        ProcessId = _process.Id;

        // Assign before the child gets far, so anything it spawns is inside the job too.
        try
        {
            _job?.Assign(_process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log($"Could not place the agent in a job object: {ex.Message}");
            if (_requireJob)
            {
                // Fail closed: an unsupervised generation is exactly what the job exists
                // to prevent, so tear it down rather than run without the guarantee.
                TryKillTree();
                throw new InvalidOperationException(
                    "The agent could not be placed in a job object, so it was stopped. "
                    + "Set \"requireJobObject\": false in settings.json to start anyway.",
                    ex);
            }
        }

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
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(gracePeriod);
                try
                {
                    await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Terminating the job below is the answer to anything that will not die.
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or gone by the time the kill landed.
        }

        // The root exiting says nothing about its descendants, so the job gets the last
        // word either way.
        _job?.Terminate();
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

    private void TryKillTree()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Nothing left to kill.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Dispose is also the natural-exit path: the supervisor calls it once the root
        // process has ended. Terminating here is what reaches descendants that outlived
        // their root, which closing the Process handle alone never did.
        try
        {
            _job?.Terminate();
        }
        finally
        {
            _job?.Dispose();
        }

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
