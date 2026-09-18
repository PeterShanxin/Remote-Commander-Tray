using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using RemoteCommanderTray.Core;

namespace RemoteCommanderTray.Windows;

/// <summary>Creates real child processes, all sharing one kill-on-close job object.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAgentProcessFactory : IAgentProcessFactory, IDisposable
{
    private readonly JobObject? _job;

    public WindowsAgentProcessFactory(Action<string>? onJobUnavailable = null)
    {
        try
        {
            _job = new JobObject();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Without a job object the supervisor still kills the tree on exit; this only
            // loses the backstop for a tray that is itself killed.
            onJobUnavailable?.Invoke(ex.Message);
            _job = null;
        }
    }

    public IAgentProcess Create(AgentLaunchSpec spec) => new WindowsAgentProcess(spec, _job);

    public async Task<AgentCommandResult> RunOnceAsync(
        AgentLaunchSpec spec,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
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

        _job?.TryAssign(process.Handle);
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

    public void Dispose() => _job?.Dispose();
}
