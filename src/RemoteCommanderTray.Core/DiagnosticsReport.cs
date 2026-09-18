using System.Text;

namespace RemoteCommanderTray.Core;

/// <summary>Everything the report needs that is not part of the agent snapshot.</summary>
/// <param name="TrayVersion">Tray version string.</param>
/// <param name="OperatingSystem">OS description.</param>
/// <param name="Architecture">Process architecture, e.g. X64 or Arm64.</param>
/// <param name="LaunchDescription">Resolved agent command line.</param>
/// <param name="LogPath">Path of the current log file.</param>
/// <param name="LaunchAtSignIn">
/// The effective startup state, which is not the same as "a Run value exists".
/// </param>
public readonly record struct DiagnosticsContext(
    string TrayVersion,
    string OperatingSystem,
    string Architecture,
    string LaunchDescription,
    string LogPath,
    StartupState LaunchAtSignIn);

/// <summary>
/// Builds the text behind "Copy diagnostics".
/// </summary>
/// <remarks>
/// Only tray-owned fields and values the CLI printed itself are included. No token, no
/// <c>device.json</c> content, and every line goes through <see cref="SecretRedactor"/>
/// before it reaches the clipboard.
/// </remarks>
public static class DiagnosticsReport
{
    public static string Build(AgentSnapshot snapshot, DiagnosticsContext context, IReadOnlyList<string>? logTail = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Tray:           {context.TrayVersion}");
        builder.AppendLine($"OS:             {context.OperatingSystem} ({context.Architecture})");
        builder.AppendLine($"State:          {snapshot.StateLabel}");
        builder.AppendLine($"Device:         {snapshot.DeviceName ?? "(unknown)"}");
        builder.AppendLine($"Device ID:      {snapshot.DeviceId ?? "(unknown)"}");
        builder.AppendLine($"Account:        {snapshot.UserEmail ?? "(not signed in)"}");
        builder.AppendLine($"Agent:          {(snapshot.ProcessRunning ? "running" : "not running")}");
        builder.AppendLine($"Agent wanted:   {(snapshot.AgentWanted ? "yes" : "no")}");
        builder.AppendLine($"Last connected: {FormatTimestamp(snapshot.LastConnectedUtc)}");
        builder.AppendLine($"State since:    {FormatTimestamp(snapshot.StateSinceUtc)}");
        builder.AppendLine($"Restart count:  {snapshot.RestartCount} (total {snapshot.TotalRestartCount})");
        builder.AppendLine($"Next restart:   {FormatTimestamp(snapshot.NextRestartUtc)}");
        builder.AppendLine($"Launch at sign-in: {DescribeStartup(context.LaunchAtSignIn)}");
        builder.AppendLine($"Command:        {context.LaunchDescription}");
        builder.AppendLine($"Log:            {context.LogPath}");
        builder.AppendLine($"Last error:     {snapshot.LastError ?? "(none)"}");
        builder.AppendLine();
        builder.AppendLine("No access token, refresh token or device.json content is included.");

        if (logTail is { Count: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine($"--- last {logTail.Count} log lines ---");
            foreach (var line in logTail)
            {
                builder.AppendLine(line);
            }
        }

        return SecretRedactor.Redact(builder.ToString());
    }

    private static string DescribeStartup(StartupState state) => state switch
    {
        StartupState.Enabled => "on",
        StartupState.DisabledByWindows => "registered, but turned off in Windows",
        _ => "off",
    };

    private static string FormatTimestamp(DateTimeOffset? value)
        => value is null || value == DateTimeOffset.MinValue
            ? "(never)"
            : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
