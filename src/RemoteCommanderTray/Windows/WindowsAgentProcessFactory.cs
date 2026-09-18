using System.Runtime.Versioning;
using System.Text;
using RemoteCommanderTray.Core;

namespace RemoteCommanderTray.Windows;

/// <summary>Every remote or logout command owns its own bounded, fail-closed job.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAgentProcessFactory : IAgentProcessFactory
{
    public IAgentProcess Create(AgentLaunchSpec spec) => new WindowsAgentProcess(spec);

    public async Task<AgentCommandResult> RunOnceAsync(AgentLaunchSpec spec, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Create(spec);
        var ended = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        var sync = new object();
        process.OutputReceived += (_, line) =>
        {
            lock (sync)
            {
                const int maximum = 64 * 1024;
                if (output.Length < maximum)
                    output.AppendLine(line.Text[..Math.Min(line.Text.Length, maximum - output.Length)]);
            }
        };
        process.Exited += (_, code) => ended.TrySetResult(code);
        try
        {
            process.Start();
            var code = await ended.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            lock (sync) return new AgentCommandResult(code, output.ToString());
        }
        catch (TimeoutException)
        {
            return new AgentCommandResult(null, "Official command timed out.");
        }
        finally
        {
            await process.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }
}
