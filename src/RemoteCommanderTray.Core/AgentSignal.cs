namespace RemoteCommanderTray.Core;

/// <summary>
/// Meaning extracted from one line of the official CLI's stdout/stderr.
/// The parser never interprets state; it only names what the line said.
/// </summary>
public enum AgentSignalKind
{
    /// <summary>Nothing the state machine cares about.</summary>
    None,

    /// <summary>"Starting MCP Device".</summary>
    DeviceStarting,

    /// <summary>"Connecting to Remote MCP ...".</summary>
    ConnectingToRemote,

    /// <summary>"Connected to Remote MCP".</summary>
    ConnectedToRemote,

    /// <summary>"Session restored" - persisted credentials were accepted.</summary>
    SessionRestored,

    /// <summary>"Authenticating with Remote MCP server" / "Starting device authorization flow".</summary>
    AuthenticationStarted,

    /// <summary>The sign-in URL printed by the device authorization flow.</summary>
    VerificationUri,

    /// <summary>The user code printed by the device authorization flow.</summary>
    UserCode,

    /// <summary>"Authorization successful".</summary>
    AuthorizationSucceeded,

    /// <summary>Persisted credentials were rejected or the device was revoked.</summary>
    SessionInvalid,

    /// <summary>"Device ready:".</summary>
    DeviceReady,

    /// <summary>"- Device Name:  HOSTNAME".</summary>
    DeviceName,

    /// <summary>"- Device ID:    ...".</summary>
    DeviceId,

    /// <summary>"- User:         someone@example.com".</summary>
    UserEmail,

    /// <summary>"Device marked as online" / "Presence tracked".</summary>
    DeviceOnline,

    /// <summary>"Device marked as offline".</summary>
    DeviceOffline,

    /// <summary>"Channel subscribed".</summary>
    ChannelSubscribed,

    /// <summary>Channel error / closed / timed out / recreating. Transient by design - never a restart trigger.</summary>
    ChannelDisrupted,

    /// <summary>"Remote session expired and could not be renewed."</summary>
    SessionExpired,

    /// <summary>"Device startup failed: ...".</summary>
    StartupFailed,

    /// <summary>The CLI is shutting itself down (signal, or a remote shutdown request).</summary>
    ShuttingDown,
}

/// <summary>One parsed line.</summary>
/// <param name="Kind">What the line meant.</param>
/// <param name="Value">Payload for signals that carry one (device name, URL, code, error text).</param>
public readonly record struct AgentSignal(AgentSignalKind Kind, string? Value = null)
{
    public static readonly AgentSignal None = new(AgentSignalKind.None);
}
