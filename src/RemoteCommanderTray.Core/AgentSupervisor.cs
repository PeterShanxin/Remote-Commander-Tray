namespace RemoteCommanderTray.Core;

/// <summary>
/// The tray's process supervisor: it owns the one and only
/// <c>desktop-commander remote</c> child, restarts it when it dies, and never restarts
/// it for anything else.
/// </summary>
/// <remarks>
/// <para>
/// The official device already handles heartbeats, stale connections and channel
/// recreation. So a dropped channel moves the icon to "Reconnecting" and is left alone;
/// only a process exit - or an explicit user action - touches the process.
/// </para>
/// <para>
/// Every public method serializes on one semaphore, which is what guarantees "at most
/// one agent" even when a restart timer and a menu click land at the same moment.
/// </para>
/// </remarks>
public sealed class AgentSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(15);

    private readonly AgentStateMachine _machine;
    private readonly AgentOutputParser _parser = new();
    private readonly IAgentProcessFactory _factory;
    private readonly AgentCommandResolver _resolver;
    private readonly Func<TraySettings> _settings;
    private readonly RollingFileLog _log;
    private readonly RestartBackoff _backoff;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly Func<DateTimeOffset> _clock;
    private readonly Timer _healthTimer;

    private IAgentProcess? _process;
    private CancellationTokenSource? _restartCts;
    private bool _agentWanted;
    private bool _disposed;
    private DateTimeOffset _runStartedUtc;
    private int _consecutiveFailures;
    private bool _stallNotified;
    private bool _startFailureNotified;
    private bool _authNotified;
    private string _launchDescription = "(not resolved)";

    public AgentSupervisor(
        AgentStateMachine machine,
        IAgentProcessFactory factory,
        AgentCommandResolver resolver,
        Func<TraySettings> settings,
        RollingFileLog log,
        Func<DateTimeOffset>? clock = null,
        RestartBackoff? backoff = null)
    {
        _machine = machine;
        _factory = factory;
        _resolver = resolver;
        _settings = settings;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _backoff = backoff ?? new RestartBackoff();

        _machine.Changed += (_, snapshot) => OnSnapshotChanged(snapshot);
        _healthTimer = new Timer(_ => RunHealthCheck(), null, HealthInterval, HealthInterval);
    }

    /// <summary>Raised when the visible state changes.</summary>
    public event EventHandler<AgentSnapshot>? Changed;

    /// <summary>Raised when the user needs to be told something.</summary>
    public event EventHandler<TrayNotification>? Notification;

    public AgentSnapshot Snapshot => _machine.Snapshot;

    /// <summary>The resolved command line, for diagnostics.</summary>
    public string LaunchDescription => _launchDescription;

    /// <summary>Applies the startup preference: start the agent, or sit idle in Stopped.</summary>
    public async Task InitializeAsync()
    {
        if (_settings().StartAgentOnLaunch)
        {
            await StartAsync().ConfigureAwait(false);
        }
        else
        {
            _log.Write(LogSource.Tray, "Agent autostart disabled by settings; staying stopped.");
            _machine.SetAgentWanted(false);
        }
    }

    /// <summary>Starts the agent if it is not already running.</summary>
    public async Task StartAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            _agentWanted = true;
            _machine.SetAgentWanted(true);
            _backoff.Reset();
            _consecutiveFailures = 0;
            _startFailureNotified = false;
            StartCore();
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Stops the agent and keeps it stopped until the user says otherwise.</summary>
    public async Task StopAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            _agentWanted = false;
            _machine.SetAgentWanted(false);
            CancelPendingRestart();
            await StopCoreAsync().ConfigureAwait(false);
            _machine.OnProcessExited(null, userRequested: true);
            _log.Write(LogSource.Tray, "Agent stopped by user.");
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Stops and immediately starts the agent, clearing the restart backoff.</summary>
    public async Task RestartAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            _log.Write(LogSource.Tray, "Restarting agent (user request).");
            _agentWanted = true;
            _machine.SetAgentWanted(true);
            CancelPendingRestart();
            await StopCoreAsync().ConfigureAwait(false);
            _backoff.Reset();
            _consecutiveFailures = 0;
            _startFailureNotified = false;
            _machine.OnRestartPerformed();
            StartCore();
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Stops the agent, asks the official CLI to drop its saved credentials, then starts
    /// it again so the official OAuth flow runs.
    /// </summary>
    /// <remarks>
    /// The tray does not implement any part of the flow: it shells out to
    /// <c>remote --logout</c> and lets the CLI open the browser. It never reads, writes
    /// or copies <c>device.json</c>.
    /// </remarks>
    public async Task ReauthenticateAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            _log.Write(LogSource.Tray, "Re-authentication requested.");
            _agentWanted = true;
            _machine.SetAgentWanted(true);
            CancelPendingRestart();
            await StopCoreAsync().ConfigureAwait(false);

            var settings = _settings();
            var resolution = _resolver.Resolve(AgentCommand.Logout, settings);
            if (resolution.Spec is null)
            {
                ReportUnavailable(resolution.Problem);
                return;
            }

            _log.Write(LogSource.Tray, $"Running logout: {resolution.Spec.Description}");
            var result = await _factory
                .RunOnceAsync(resolution.Spec, TimeSpan.FromSeconds(30))
                .ConfigureAwait(false);

            foreach (var line in SplitLines(result.Output))
            {
                _log.Write(LogSource.Agent, line);
            }

            _log.Write(
                LogSource.Tray,
                result.Succeeded
                    ? "Logout completed; saved credentials removed by the official CLI."
                    : $"Logout exited with code {result.ExitCode?.ToString() ?? "n/a"}; starting the agent anyway.");

            _backoff.Reset();
            _consecutiveFailures = 0;
            _startFailureNotified = false;
            _authNotified = true;
            StartCore();

            Raise(new TrayNotification(
                NotificationKind.ReauthenticationStarted,
                "Remote Commander - sign-in needed",
                "Desktop Commander is signing in again. Finish the sign-in in your browser.",
                _machine.Snapshot.VerificationUri));
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>True when a child process currently exists.</summary>
    public bool IsRunning => _process is { HasExited: false };

    // --- internals -------------------------------------------------------

    private void StartCore()
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        var settings = _settings();
        var resolution = _resolver.Resolve(AgentCommand.Remote, settings);
        if (resolution.Spec is null)
        {
            ReportUnavailable(resolution.Problem);
            return;
        }

        _launchDescription = resolution.Spec.Description;
        _parser.Reset();

        var process = _factory.Create(resolution.Spec);
        process.OutputReceived += HandleOutput;
        process.Exited += HandleExited;

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.OutputReceived -= HandleOutput;
            process.Exited -= HandleExited;
            process.Dispose();
            _log.Write(LogSource.Tray, $"Failed to launch agent: {ex.Message}");
            _machine.OnProcessExited(null, userRequested: false);
            _consecutiveFailures++;
            NotifyStartFailureIfNeeded(ex.Message);
            ScheduleRestart();
            return;
        }

        _process = process;
        _runStartedUtc = _clock();
        _stallNotified = false;
        _machine.OnProcessStarted();
        _log.Write(
            LogSource.Tray,
            $"Agent started (pid {process.ProcessId?.ToString() ?? "?"}): {resolution.Spec.Description}");
    }

    private async Task StopCoreAsync()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        _process = null;
        process.OutputReceived -= HandleOutput;
        process.Exited -= HandleExited;

        try
        {
            await process.StopAsync(StopGracePeriod).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Write(LogSource.Tray, $"Error while stopping agent: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    private void HandleOutput(object? sender, AgentOutputLine line)
    {
        _log.Write(line.IsError ? LogSource.AgentError : LogSource.Agent, line.Text);
        _machine.Apply(_parser.Parse(line.Text));
    }

    private void HandleExited(object? sender, int? exitCode)
    {
        if (!ReferenceEquals(sender, _process))
        {
            // A process we already detached from. Its teardown is someone else's business.
            return;
        }

        _ = Task.Run(() => OnAgentExitedAsync(exitCode));
    }

    private async Task OnAgentExitedAsync(int? exitCode)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            var process = _process;
            if (process is null)
            {
                return;
            }

            _process = null;
            process.OutputReceived -= HandleOutput;
            process.Exited -= HandleExited;
            process.Dispose();

            _log.Write(
                LogSource.Tray,
                $"Agent exited with code {exitCode?.ToString() ?? "unknown"}.");
            _machine.OnProcessExited(exitCode, userRequested: false);

            if (!_agentWanted || _disposed)
            {
                return;
            }

            var settings = _settings();
            var ranFor = _clock() - _runStartedUtc;
            if (ranFor < TimeSpan.FromSeconds(settings.HealthyRunSeconds))
            {
                _consecutiveFailures++;
            }
            else
            {
                // The run was long enough to count as healthy; treat this as a fresh streak.
                _consecutiveFailures = 1;
                _backoff.Reset();
                _startFailureNotified = false;
            }

            NotifyStartFailureIfNeeded(_machine.Snapshot.LastError);
            ScheduleRestart();
        }
        finally
        {
            _mutex.Release();
        }
    }

    private void ScheduleRestart()
    {
        CancelPendingRestart();

        var delay = _backoff.NextDelay();
        var due = _clock() + delay;
        _machine.OnRestartScheduled(_backoff.Attempt, due);
        _log.Write(LogSource.Tray, $"Restarting agent in {delay.TotalSeconds:0}s (attempt {_backoff.Attempt}).");

        var cts = new CancellationTokenSource();
        _restartCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await _mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                if (cts.IsCancellationRequested || !_agentWanted || _disposed)
                {
                    return;
                }

                _machine.OnRestartPerformed();
                StartCore();
            }
            finally
            {
                _mutex.Release();
            }
        });
    }

    private void CancelPendingRestart()
    {
        var cts = _restartCts;
        _restartCts = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }

        cts.Dispose();
    }

    private void RunHealthCheck()
    {
        if (_disposed)
        {
            return;
        }

        // Skip this tick rather than queue behind a command: the health check shares the
        // backoff and failure counters with the restart path, and a tick that waited
        // would only re-check state the command just changed.
        if (!_mutex.Wait(0))
        {
            return;
        }

        try
        {
            RunHealthCheckCore();
        }
        finally
        {
            _mutex.Release();
        }
    }

    private void RunHealthCheckCore()
    {
        var snapshot = _machine.Snapshot;
        var settings = _settings();
        var now = _clock();

        if (snapshot.State == AgentState.Online)
        {
            _stallNotified = false;
            _startFailureNotified = false;
            _consecutiveFailures = 0;
            _backoff.Reset();
            return;
        }

        if (!_agentWanted || !snapshot.ProcessRunning || _stallNotified)
        {
            return;
        }

        if (snapshot.State is not (AgentState.Connecting or AgentState.Starting))
        {
            return;
        }

        var stalledAfter = TimeSpan.FromMinutes(settings.StalledConnectionMinutes);
        var elapsed = now - snapshot.StateSinceUtc;
        if (elapsed < stalledAfter)
        {
            return;
        }

        _stallNotified = true;
        _machine.OnConnectionStalled(elapsed);
        _log.Write(LogSource.Tray, $"Connection has not reached Online for {AgentStateMachine.FormatDuration(elapsed)}.");
        Raise(new TrayNotification(
            NotificationKind.ConnectionStalled,
            "Remote Commander - not connected",
            $"The device has not come online for {AgentStateMachine.FormatDuration(elapsed)}. "
            + "Try Restart connection, or Re-authenticate if sign-in expired."));
    }

    private void OnSnapshotChanged(AgentSnapshot snapshot)
    {
        if (snapshot.State == AgentState.AuthenticationRequired)
        {
            if (!_authNotified)
            {
                _authNotified = true;
                Raise(new TrayNotification(
                    NotificationKind.AuthenticationRequired,
                    "Remote Commander - sign-in required",
                    "Desktop Commander needs you to sign in. Your browser should open automatically; "
                    + "if it does not, use \"Open sign-in page\" in the tray menu.",
                    snapshot.VerificationUri));
            }
        }
        else if (snapshot.State == AgentState.Online)
        {
            _authNotified = false;
        }

        Changed?.Invoke(this, snapshot);
    }

    private void NotifyStartFailureIfNeeded(string? detail)
    {
        var threshold = _settings().StartFailureAlertThreshold;
        if (_consecutiveFailures < threshold || _startFailureNotified)
        {
            return;
        }

        _startFailureNotified = true;
        Raise(new TrayNotification(
            NotificationKind.StartFailure,
            "Remote Commander - agent keeps failing",
            $"The Desktop Commander agent failed to stay running {_consecutiveFailures} times in a row. "
            + (string.IsNullOrWhiteSpace(detail) ? "Open logs for details." : detail)));
    }

    private void ReportUnavailable(string? problem)
    {
        var message = problem ?? "Desktop Commander could not be found.";
        _log.Write(LogSource.Tray, message);
        _machine.OnProcessExited(null, userRequested: false);
        Raise(new TrayNotification(
            NotificationKind.AgentUnavailable,
            "Remote Commander - agent not found",
            message));
    }

    private void Raise(TrayNotification notification)
    {
        if (!_settings().NotificationsEnabled)
        {
            return;
        }

        Notification?.Invoke(this, notification);
    }

    private static IEnumerable<string> SplitLines(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _healthTimer.DisposeAsync().ConfigureAwait(false);

        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            _agentWanted = false;
            CancelPendingRestart();
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        _mutex.Dispose();
    }
}
