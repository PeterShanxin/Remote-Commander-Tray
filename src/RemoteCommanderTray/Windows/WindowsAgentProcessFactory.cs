using System.Runtime.Versioning;
using System.Text;
using RemoteCommanderTray.Core;

namespace RemoteCommanderTray.Windows;

/// <summary>Both the long-lived agent and logout use exactly the same containment.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAgentProcessFactory : IAgentProcessFactory
{
    public IAgentProcess Create(AgentLaunchSpec spec) => new WindowsAgentProcess(spec);

    public async Task<AgentCommandResult> RunOnceAsync(AgentLaunchSpec spec, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new WindowsAgentProcess(spec);
        var output = new StringBuilder();
        var gate = new object();
        process.OutputReceived += (_, line) =>
        {
            lock (gate)
            {
                // stdout/stderr callbacks may overlap; capture is bounded even on error.
                var available = Math.Max(0, 32 * 1024 - output.Length);
                output.Append(line.Text.AsSpan(0, Math.Min(line.Text.Length, available)));
                if (available > line.Text.Length) output.AppendLine();
            }
        };
        process.Start();
        try
        {
            var exitCode = await process.Completion.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            lock (gate) return new(exitCode, output.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await process.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            throw;
        }
        catch (TimeoutException)
        {
            await process.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return new(null, "Command timed out; its process group was stopped.");
        }
    }
}
