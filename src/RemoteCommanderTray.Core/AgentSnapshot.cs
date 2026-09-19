namespace RemoteCommanderTray.Core;

/// <summary>
/// Immutable view of everything the UI is allowed to know. Produced by
/// <see cref="AgentStateMachine"/> and handed to the tray on every change.
/// </summary>
/// <remarks>
/// Nothing here comes from an OAuth token: every field is either tray-owned
/// bookkeeping or a value the official CLI printed on its own stdout.
/// </remarks>
public sealed record AgentSnapshot
{
    public static readonly AgentSnapshot Initial = new();

    /// <summary>Current tray state.</summary>
    public AgentState State { get; init; } = AgentState.Stopped;

    /// <summary>True while a child process exists (even if it is still starting).</summary>
    public bool ProcessRunning { get; init; }

    /// <summary>True when the user asked for the agent to be running.</summary>
    public bool AgentWanted { get; init; }

    /// <summary>Device name reported by the CLI (the machine hostname).</summary>
    public string? DeviceName { get; init; }

    /// <summary>Device id reported by the CLI.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Signed-in account reported by the CLI.</summary>
    public string? UserEmail { get; init; }

    /// <summary>Last time the device reached <see cref="AgentState.Online"/>, in UTC.</summary>
    public DateTimeOffset? LastConnectedUtc { get; init; }

    /// <summary>Sign-in page printed by the CLI while a device authorization flow is pending.</summary>
    public string? VerificationUri { get; init; }

    /// <summary>User code printed by the CLI while a device authorization flow is pending.</summary>
    public string? UserCode { get; init; }

    /// <summary>Authorization was lost or incomplete and requires an explicit sign-in retry.</summary>
    public bool RequiresReauthentication { get; init; }

    /// <summary>Last error line worth surfacing.</summary>
    public string? LastError { get; init; }

    /// <summary>How many times the supervisor has restarted the agent since the last clean run.</summary>
    public int RestartCount { get; init; }

    /// <summary>Total restarts since the tray started, for diagnostics.</summary>
    public int TotalRestartCount { get; init; }

    /// <summary>When set, the supervisor is waiting out a backoff before the next restart.</summary>
    public DateTimeOffset? NextRestartUtc { get; init; }

    /// <summary>When the current state was entered, in UTC.</summary>
    public DateTimeOffset StateSinceUtc { get; init; } = DateTimeOffset.MinValue;

    /// <summary>Short human-readable label for the state, e.g. "Online".</summary>
    public string StateLabel => State switch
    {
        AgentState.Stopped => "Stopped",
        AgentState.Starting => "Starting",
        AgentState.Connecting => RestartCount > 0 || LastConnectedUtc is not null ? "Reconnecting" : "Connecting",
        AgentState.AuthenticationRequired => "Authentication required",
        AgentState.Online => "Online",
        AgentState.Error => ProcessRunning ? "Error" : "Offline",
        _ => State.ToString(),
    };
}
