using System.Text;
namespace RemoteCommanderTray.Core;

public readonly record struct DiagnosticsContext(
    string TrayVersion, string OperatingSystem, string Architecture,
    string LaunchDescription, string LogPath, StartupState LaunchAtSignIn);

/// <summary>Default clipboard export is a structural report, never a log export.
/// CLI-derived strings and old log tails are deliberately excluded: old versions may
/// already have written sensitive tool output, and regexes cannot sanitize all data.</summary>
public static class DiagnosticsReport
{
    public static string Build(AgentSnapshot snapshot, DiagnosticsContext context, IReadOnlyList<string>? logTail = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Tray:           {context.TrayVersion}");
        builder.AppendLine($"OS:             {context.OperatingSystem} ({context.Architecture})");
        builder.AppendLine($"State:          {snapshot.StateLabel}");
        builder.AppendLine($"Agent:          {(snapshot.ProcessRunning ? "running" : "not running")}");
        builder.AppendLine($"Agent wanted:   {(snapshot.AgentWanted ? "yes" : "no")}");
        builder.AppendLine($"Last connected: {FormatTimestamp(snapshot.LastConnectedUtc)}");
        builder.AppendLine($"State since:    {FormatTimestamp(snapshot.StateSinceUtc)}");
        builder.AppendLine($"Restart count:  {snapshot.RestartCount} (total {snapshot.TotalRestartCount})");
        builder.AppendLine($"Next restart:   {FormatTimestamp(snapshot.NextRestartUtc)}");
        builder.AppendLine($"Launch at sign-in: {context.LaunchAtSignIn}");
        builder.AppendLine($"Error present:  {(!string.IsNullOrWhiteSpace(snapshot.LastError) ? "yes" : "no")}");
        builder.AppendLine();
        builder.AppendLine("Agent output, account/device identifiers, command arguments, sign-in URLs/codes and log contents are not included.");
        return builder.ToString();
    }

    private static string FormatTimestamp(DateTimeOffset? value)
        => value is null || value == DateTimeOffset.MinValue ? "(never)" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
