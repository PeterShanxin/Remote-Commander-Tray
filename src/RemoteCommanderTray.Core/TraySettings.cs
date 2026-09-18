using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteCommanderTray.Core;

/// <summary>
/// User-editable knobs, persisted to <c>settings.json</c>.
/// </summary>
/// <remarks>
/// "Launch at sign-in" is deliberately absent: the Windows <c>Run</c> registry value is
/// the single source of truth for it, so that toggling it outside the app cannot drift
/// from a copy kept here.
/// </remarks>
public sealed class TraySettings
{
    /// <summary>Start the agent as soon as the tray starts.</summary>
    [JsonPropertyName("startAgentOnLaunch")]
    public bool StartAgentOnLaunch { get; set; } = true;

    /// <summary>Override the executable used to launch the official CLI. Null means auto-detect.</summary>
    [JsonPropertyName("agentExecutable")]
    public string? AgentExecutable { get; set; }

    /// <summary>Arguments placed before the <c>remote</c> sub-command when <see cref="AgentExecutable"/> is set.</summary>
    [JsonPropertyName("agentArguments")]
    public string? AgentArguments { get; set; }

    /// <summary>Page opened by "Open Remote MCP".</summary>
    [JsonPropertyName("remoteMcpUrl")]
    public string RemoteMcpUrl { get; set; } = "https://mcp.desktopcommander.app";

    /// <summary>How long a non-Online run may last before the user is told the connection is stuck.</summary>
    [JsonPropertyName("stalledConnectionMinutes")]
    public int StalledConnectionMinutes { get; set; } = 5;

    /// <summary>How many consecutive failed starts before notifying.</summary>
    [JsonPropertyName("startFailureAlertThreshold")]
    public int StartFailureAlertThreshold { get; set; } = 3;

    /// <summary>A run that lasts this long counts as healthy and clears the restart backoff.</summary>
    [JsonPropertyName("healthyRunSeconds")]
    public int HealthyRunSeconds { get; set; } = 60;

    /// <summary>Rotate <c>agent.log</c> once it passes this size.</summary>
    [JsonPropertyName("logMaxBytes")]
    public long LogMaxBytes { get; set; } = 1024 * 1024;

    /// <summary>How many rotated log files to keep.</summary>
    [JsonPropertyName("logRetainedFiles")]
    public int LogRetainedFiles { get; set; } = 3;

    /// <summary>
    /// Also write the agent's raw output to <c>logs/agent-verbose.log</c>.
    /// </summary>
    /// <remarks>
    /// Off by default, and deliberately not part of "Copy diagnostics": the official CLI
    /// serializes tool arguments and results, so raw output can contain the contents of
    /// any file a remote tool call read. Turning this on is an explicit choice to keep a
    /// file that may hold sensitive data.
    /// </remarks>
    [JsonPropertyName("verboseAgentLog")]
    public bool VerboseAgentLog { get; set; }

    /// <summary>
    /// Refuse to launch an agent that could not be placed in a job object.
    /// </summary>
    /// <remarks>
    /// The job object is what guarantees no orphaned agent survives the tray. Failing
    /// closed is the safe default; it can be turned off for environments where nested job
    /// objects are unavailable, at the cost of that guarantee.
    /// </remarks>
    [JsonPropertyName("requireJobObject")]
    public bool RequireJobObject { get; set; } = true;

    /// <summary>Show desktop notifications for events that need the user.</summary>
    [JsonPropertyName("notificationsEnabled")]
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>Clamps anything a hand-edited file could have made nonsensical.</summary>
    public TraySettings Normalized()
    {
        StalledConnectionMinutes = Math.Clamp(StalledConnectionMinutes, 1, 120);
        StartFailureAlertThreshold = Math.Clamp(StartFailureAlertThreshold, 1, 20);
        HealthyRunSeconds = Math.Clamp(HealthyRunSeconds, 5, 3600);
        LogMaxBytes = Math.Clamp(LogMaxBytes, 64 * 1024, 64L * 1024 * 1024);
        LogRetainedFiles = Math.Clamp(LogRetainedFiles, 0, 20);

        if (string.IsNullOrWhiteSpace(RemoteMcpUrl)
            || !Uri.TryCreate(RemoteMcpUrl, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            RemoteMcpUrl = "https://mcp.desktopcommander.app";
        }

        if (string.IsNullOrWhiteSpace(AgentExecutable))
        {
            AgentExecutable = null;
            AgentArguments = null;
        }

        return this;
    }
}

/// <summary>Loads and saves <see cref="TraySettings"/>, tolerating a missing or broken file.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string path) => _path = path;

    /// <summary>Reads settings, falling back to defaults when the file is absent or unreadable.</summary>
    public TraySettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return new TraySettings();
                }

                var json = File.ReadAllText(_path);
                var parsed = JsonSerializer.Deserialize<TraySettings>(json, SerializerOptions);
                return (parsed ?? new TraySettings()).Normalized();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                TryQuarantine();
                return new TraySettings();
            }
        }
    }

    /// <summary>Writes settings. Failures are reported to the caller rather than thrown at the UI thread.</summary>
    public bool TrySave(TraySettings settings, out string? error)
    {
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings.Normalized(), SerializerOptions));
                File.Move(temp, _path, overwrite: true);
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    private void TryQuarantine()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Move(_path, _path + ".invalid", overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do; defaults are used either way.
        }
    }
}
