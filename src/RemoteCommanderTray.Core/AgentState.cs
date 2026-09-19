namespace RemoteCommanderTray.Core;

/// <summary>
/// The states the tray can be in. Deliberately small: everything the UI needs to
/// pick an icon, a tooltip and a menu layout is derived from this plus
/// <see cref="AgentSnapshot"/>.
/// </summary>
public enum AgentState
{
    /// <summary>No agent process is running and none is wanted (user pressed Stop, or startup is disabled).</summary>
    Stopped,

    /// <summary>The process was launched; the device has not reported progress yet.</summary>
    Starting,

    /// <summary>Talking to the Remote MCP server, or re-establishing a dropped realtime channel.</summary>
    Connecting,

    /// <summary>The official CLI is running a device authorization flow and the user has to sign in.</summary>
    AuthenticationRequired,

    /// <summary>The device is registered and marked online.</summary>
    Online,

    /// <summary>The agent failed, exited unexpectedly, or has been offline long enough to be called broken.</summary>
    Error,
}
