using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using RemoteCommanderTray.Core;

namespace RemoteCommanderTray.Windows;

/// <summary>
/// Creates real child processes, each owning its own kill-on-close job object.
/// </summary>
/// <remarks>
/// A job per generation rather than one per factory: a shared job outlives every restart,
/// so a descendant that survived its own root process would stay alive until the tray
/// itself exited. Each <see cref="WindowsAgentProcess"/> now owns and terminates its own.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAgentProcessFactory : IAgentProcessFactory
{
    private readonly Func<bool> _requireJobObject;
    private readonly Action<string> _log;

    public WindowsAgentProcessFactory(Func<bool> requireJobObject, Action<string> log)
    {
        _requireJobObject = requireJobObject;
        _log = log;
    }

    public IAgentProcess Create(AgentLaunchSpec spec)
        => new WindowsAgentProcess(spec, TryCreateJob(), _requireJobObject(), _log);

    public async Task<AgentCommandResult> RunOnceAsync(
        AgentLaunchSpec spec,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        // The logout one-shot gets the same bounded lifecycle as a long-running agent:
        // its own job, terminated whether it finishes, times out, or throws.
        var job = TryCreateJob();
        if (job is null && _requireJobObject())
        {
            return new AgentCommandResult(
                null,
                "No job object is available, so the command was not run. "
                + "Set \"requireJobObject\": false in settings.json to run it anyway.");
        }

        try
        {
            return await RunOnceCoreAsync(spec, timeout, job, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            job?.Terminate();
            job?.Dispose();
        }
    }

    private async Task<AgentCommandResult> RunOnceCoreAsync(
        AgentLaunchSpec spec,
        TimeSpan timeout,
        JobObject? job,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = spec.FileName,
                Arguments = spec.Arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            },
        };

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);

        if (!process.Start())
        {
            return new AgentCommandResult(null, $"Could not start {spec.Description}.");
        }

        try
        {
            job?.Assign(process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log($"Could not place the command in a job object: {ex.Message}");
            if (_requireJobObject())
            {
                TryKill(process);
                return new AgentCommandResult(null, "The command could not be placed in a job object.");
            }
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return new AgentCommandResult(process.ExitCode, output.ToString());
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new AgentCommandResult(null, output + Environment.NewLine + "Timed out.");
        }
    }

    private JobObject? TryCreateJob()
    {
        try
        {
            return new JobObject();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            _log($"Job object unavailable: {ex.Message}");
            return null;
        }
    }

    private static void Append(StringBuilder builder, string? data)
    {
        if (data is not null)
        {
            builder.AppendLine(data);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}
