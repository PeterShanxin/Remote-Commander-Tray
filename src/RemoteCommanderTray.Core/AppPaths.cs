namespace RemoteCommanderTray.Core;

/// <summary>
/// Everything the tray writes lives under one per-user folder. Nothing is written
/// outside it, and the official device's own credential store
/// (<c>%USERPROFILE%\.desktop-commander-device</c>) is never read or touched.
/// </summary>
public sealed class AppPaths
{
    public const string FolderName = "RemoteCommanderTray";

    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            FolderName);
        LogDirectory = Path.Combine(Root, "logs");
        SettingsFile = Path.Combine(Root, "settings.json");
        AgentLogFile = Path.Combine(LogDirectory, "agent.log");
        VerboseAgentLogFile = Path.Combine(LogDirectory, "agent-verbose.log");
    }

    /// <summary><c>%LOCALAPPDATA%\RemoteCommanderTray</c>.</summary>
    public string Root { get; }

    /// <summary><c>%LOCALAPPDATA%\RemoteCommanderTray\logs</c>.</summary>
    public string LogDirectory { get; }

    /// <summary><c>%LOCALAPPDATA%\RemoteCommanderTray\settings.json</c>.</summary>
    public string SettingsFile { get; }

    /// <summary><c>%LOCALAPPDATA%\RemoteCommanderTray\logs\agent.log</c>.</summary>
    public string AgentLogFile { get; }

    /// <summary>
    /// <c>%LOCALAPPDATA%\RemoteCommanderTray\logs\agent-verbose.log</c>, written only
    /// when the user opts in. Never read by diagnostics.
    /// </summary>
    public string VerboseAgentLogFile { get; }

    /// <summary>Creates the folders if they are missing.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }
}
