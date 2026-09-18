namespace RemoteCommanderTray.Core;

/// <summary>
/// Holds the single source of truth for what the tray shows. It is fed process
/// lifecycle events by <see cref="AgentSupervisor"/> and parsed log signals by
/// <see cref="AgentOutputParser"/>, and it emits an immutable
/// <see cref="AgentSnapshot"/> whenever anything visible changes.
/// </summary>
/// <remarks>
/// No UI type is referenced here, and no log text is parsed here. That separation is
/// what keeps the state rules readable and unit-testable.
/// </remarks>
public sealed class AgentStateMachine
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private AgentSnapshot _snapshot = AgentSnapshot.Initial;
    private bool _deviceReadySeen;

    public AgentStateMachine(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _snapshot = AgentSnapshot.Initial with { StateSinceUtc = _clock() };
    }

    /// <summary>Raised after any change that the tray should render. Never raised for no-op updates.</summary>
    public event EventHandler<AgentSnapshot>? Changed;

    public AgentSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>Records whether the user wants the agent running at all.</summary>
    public void SetAgentWanted(bool wanted)
        => Update(s => s with { AgentWanted = wanted });

    /// <summary>A child process has just been launched.</summary>
    public void OnProcessStarted()
    {
        Update(s =>
        {
            _deviceReadySeen = false;
            return Transition(
            s with
            {
                ProcessRunning = true,
                AgentWanted = true,
                VerificationUri = null,
                UserCode = null,
                NextRestartUtc = null,
                DeviceName = null,
                DeviceId = null,
                UserEmail = null,
                LastError = null,
            },
            AgentState.Starting);
        });
    }

    /// <summary>The child process is gone.</summary>
    /// <param name="exitCode">Process exit code, when one is available.</param>
    /// <param name="userRequested">True when the tray asked it to stop.</param>
    public void OnProcessExited(int? exitCode, bool userRequested)
    {
        Update(s =>
        {
            _deviceReadySeen = false;
            var next = s with
            {
                ProcessRunning = false,
                VerificationUri = null,
                UserCode = null,
            };

            if (userRequested || !s.AgentWanted)
            {
                return Transition(next with { LastError = null }, AgentState.Stopped);
            }

            var reason = exitCode is null
                ? "The agent process ended unexpectedly."
                : $"The agent process exited with code {exitCode}.";
            return Transition(next with { LastError = reason }, AgentState.Error);
        });
    }

    /// <summary>The supervisor has queued a restart attempt.</summary>
    public void OnRestartScheduled(int attempt, DateTimeOffset whenUtc)
        => Update(s => s with { RestartCount = attempt, NextRestartUtc = whenUtc });

    /// <summary>The supervisor gave up waiting for a reconnect that never completed.</summary>
    public void OnConnectionStalled(TimeSpan elapsed)
        => Update(s => s.State is AgentState.Connecting or AgentState.Starting
            ? Transition(
                s with { LastError = $"No connection for {FormatDuration(elapsed)}." },
                AgentState.Error)
            : s);

    /// <summary>
    /// The remote session was lost while the process stayed alive, and the tray is about
    /// to restart it. Distinct from an exit so the menu can say what actually happened.
    /// </summary>
    public void OnSessionLost(string reason)
        => Update(s => Transition(
            s with { ProcessRunning = false, VerificationUri = null, UserCode = null, LastError = reason, NextRestartUtc = null },
            AgentState.AuthenticationRequired));

    /// <summary>Clears a cancelled retry without leaving a stale countdown.</summary>
    public void ClearPendingRestart()
        => Update(s => s with { NextRestartUtc = null });

    /// <summary>Counts a completed restart for diagnostics.</summary>
    public void OnRestartPerformed()
        => Update(s => s with { TotalRestartCount = s.TotalRestartCount + 1 });

    /// <summary>Applies one parsed line of agent output.</summary>
    public void Apply(AgentSignal signal)
    {
        if (signal.Kind == AgentSignalKind.None)
        {
            return;
        }

        Update(s => Apply(s, signal));
    }

    private AgentSnapshot Apply(AgentSnapshot s, AgentSignal signal)
    {
        if (s.State == AgentState.AuthenticationRequired && s.LastError is not null &&
            signal.Kind is AgentSignalKind.DeviceOnline or AgentSignalKind.DeviceReady or AgentSignalKind.ChannelSubscribed)
            return s;
        switch (signal.Kind)
        {
            case AgentSignalKind.DeviceStarting:
                return Transition(s, AgentState.Starting);

            case AgentSignalKind.ConnectingToRemote:
            case AgentSignalKind.ConnectedToRemote:
                return Transition(s, AgentState.Connecting);

            case AgentSignalKind.SessionRestored:
                return Transition(
                    s with { VerificationUri = null, UserCode = null },
                    AgentState.Connecting);

            case AgentSignalKind.AuthenticationStarted:
                return Transition(s, AgentState.AuthenticationRequired);

            case AgentSignalKind.VerificationUri:
                return Transition(
                    s with { VerificationUri = NullIfBlank(signal.Value) },
                    AgentState.AuthenticationRequired);

            case AgentSignalKind.UserCode:
                return Transition(
                    s with { UserCode = NullIfBlank(signal.Value) },
                    AgentState.AuthenticationRequired);

            case AgentSignalKind.AuthorizationSucceeded:
                return Transition(
                    s with { VerificationUri = null, UserCode = null, LastError = null },
                    AgentState.Connecting);

            case AgentSignalKind.SessionInvalid:
                // Printed just before the CLI falls back to a fresh authorization flow.
                return s with { LastError = NullIfBlank(signal.Value) };

            case AgentSignalKind.SessionExpired:
                return Transition(
                    s with { LastError = "Remote session expired. Use Re-authenticate to sign in again.", VerificationUri = null, UserCode = null, NextRestartUtc = null },
                    AgentState.AuthenticationRequired);

            case AgentSignalKind.DeviceReady:
                _deviceReadySeen = true;
                return MarkOnline(s);

            case AgentSignalKind.DeviceName:
                return s with { DeviceName = NullIfBlank(signal.Value) };

            case AgentSignalKind.DeviceId:
                return s with { DeviceId = NullIfBlank(signal.Value) };

            case AgentSignalKind.UserEmail:
                return s with { UserEmail = NullIfBlank(signal.Value) };

            case AgentSignalKind.DeviceOnline:
                return MarkOnline(s);

            case AgentSignalKind.ChannelSubscribed:
                // Realtime is back. Only claim Online if the device finished registering.
                return _deviceReadySeen ? MarkOnline(s) : s;

            case AgentSignalKind.DeviceOffline:
            case AgentSignalKind.ChannelDisrupted:
                // The official device owns heartbeat and channel recovery, so a blip is
                // a status change for the icon, never a reason to restart the process.
                return s.State == AgentState.Online
                    ? Transition(s with { LastError = NullIfBlank(signal.Value) }, AgentState.Connecting)
                    : s;

            case AgentSignalKind.StartupFailed:
                return Transition(
                    s with { LastError = NullIfBlank(signal.Value) ?? "Device startup failed." },
                    AgentState.Error);

            case AgentSignalKind.ShuttingDown:
                return s;

            default:
                return s;
        }
    }

    private AgentSnapshot MarkOnline(AgentSnapshot s)
    {
        var now = _clock();
        return Transition(
            s with
            {
                LastConnectedUtc = now,
                VerificationUri = null,
                UserCode = null,
                LastError = null,
                RestartCount = 0,
                NextRestartUtc = null,
            },
            AgentState.Online);
    }

    private AgentSnapshot Transition(AgentSnapshot s, AgentState next)
        => s.State == next ? s : s with { State = next, StateSinceUtc = _clock() };

    private void Update(Func<AgentSnapshot, AgentSnapshot> mutate)
    {
        AgentSnapshot updated;
        lock (_gate)
        {
            updated = mutate(_snapshot);
            if (updated == _snapshot)
            {
                return;
            }

            _snapshot = updated;
        }

        Changed?.Invoke(this, updated);
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string FormatDuration(TimeSpan value)
    {
        if (value.TotalMinutes < 1)
        {
            return $"{Math.Max(1, (int)value.TotalSeconds)}s";
        }

        return value.TotalHours < 1
            ? $"{(int)value.TotalMinutes} min"
            : $"{(int)value.TotalHours}h {value.Minutes} min";
    }
}
