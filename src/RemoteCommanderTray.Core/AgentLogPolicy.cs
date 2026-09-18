namespace RemoteCommanderTray.Core;

/// <summary>
/// Ordinary logs contain only tray-authored descriptions of recognized events.
/// Neither an unfamiliar short line nor a status prefix makes arbitrary text safe.
/// Raw opt-in logs are separate, sensitive and never included in diagnostics.
/// </summary>
public static class AgentLogPolicy
{
    public static string Sanitize(string rawLine, AgentSignal signal) => signal.Kind switch
    {
        AgentSignalKind.None => $"<agent output omitted, {rawLine.Length} chars>",
        AgentSignalKind.ToolPayload => $"<tool result omitted, {rawLine.Length} chars>",
        AgentSignalKind.ToolCall => "Tool activity (arguments and results omitted).",
        AgentSignalKind.DeviceStarting => "Starting MCP Device.",
        AgentSignalKind.ConnectingToRemote => "Connecting to Remote MCP.",
        AgentSignalKind.ConnectedToRemote => "Connected to Remote MCP.",
        AgentSignalKind.SessionRestored => "Session restored.",
        AgentSignalKind.AuthenticationStarted => "Authentication required.",
        AgentSignalKind.VerificationUri => "Sign-in page available (not logged).",
        AgentSignalKind.UserCode => "Sign-in code available (not logged).",
        AgentSignalKind.AuthorizationSucceeded => "Authorization successful.",
        AgentSignalKind.SessionInvalid => "Saved session was rejected.",
        AgentSignalKind.SessionExpired => "Remote session expired; re-authentication required.",
        AgentSignalKind.DeviceReady => "Device ready.",
        AgentSignalKind.DeviceName or AgentSignalKind.DeviceId or AgentSignalKind.UserEmail => "Device metadata received (not logged).",
        AgentSignalKind.DeviceOnline => "Device online.",
        AgentSignalKind.DeviceOffline => "Device offline.",
        AgentSignalKind.ChannelSubscribed => "Channel subscribed.",
        AgentSignalKind.ChannelDisrupted => "Remote channel disrupted; awaiting CLI recovery.",
        AgentSignalKind.StartupFailed => "Device startup failed; check installation, network and sign-in.",
        AgentSignalKind.ShuttingDown => "Device shutting down.",
        _ => "<unknown agent event omitted>",
    };

    internal static AgentSignal SafeStateSignal(AgentSignal signal) => signal.Kind switch
    {
        AgentSignalKind.SessionExpired => signal with { Value = "Remote session expired. Use Re-authenticate to sign in again." },
        AgentSignalKind.SessionInvalid => signal with { Value = "Saved session rejected; waiting for sign-in." },
        AgentSignalKind.ChannelDisrupted => signal with { Value = "Remote channel disrupted." },
        AgentSignalKind.StartupFailed => signal with { Value = "Device startup failed. Check installation, network and sign-in." },
        _ => signal,
    };
}
