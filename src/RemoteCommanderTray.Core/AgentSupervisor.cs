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
    private readonly AgentOutputParser _errorParser = new();
    private readonly IAgentProcessFactory _factory;
    private readonly AgentCommandResolver _resolver;
    private readonly Func<TraySettings> _settings;
    private readonly RollingFileLog _log;
    private readonly RollingFileLog? _verboseLog;
    private readonly RestartBackoff _backoff;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Timer _healthTimer;

    // Read from callback threads without the mutex, so the reference must be published.
    private volatile IAgentProcess? _process;
    private CancellationTokenSource? _restartCts;
    private bool _agentWanted;
    private volatile bool _disposed;
    private DateTimeOffset _runStartedUtc;
    private int _consecutiveFailures;
    private bool _stallNotified;
    private bool _startFailureNotified;
    private bool _authNotified;
    private bool _sessionLossHandled;
    private bool _acceptOutput;
    private string _launchDescription = "(not resolved)";

    public AgentSupervisor(
        AgentStateMachine machine,
        IAgentProcessFactory factory,
        AgentCommandResolver resolver,
        Func<TraySettings> settings,
        RollingFileLog log,
        Func<DateTimeOffset>? clock = null,
        RestartBackoff? backoff = null,
        RollingFileLog? verboseLog = null)
    {
        _machine = machine;
        _factory = factory;
        _resolver = resolver;
        _settings = settings;
        _log = log;
        _verboseLog = verboseLog;
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
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is not null && !_acceptOutput)
                throw new InvalidOperationException("Retry Stop before starting a new generation.");
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
            ObjectDisposedException.ThrowIf(_disposed, this);
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
            ObjectDisposedException.ThrowIf(_disposed, this);
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
            ObjectDisposedException.ThrowIf(_disposed, this);
            _log.Write(LogSource.Tray, "Re-authentication requested.");
            _agentWanted = true;
            _machine.SetAgentWanted(true);
            CancelPendingRestart();
            await StopCoreAsync().ConfigureAwait(false);
            _machine.OnProcessExited(null, userRequested: true);

            var settings = _settings();
            var resolution = _resolver.Resolve(AgentCommand.Logout, settings);
            if (resolution.Spec is null)
            {
                ReportUnavailable(resolution.Problem);
                return;
            }

            _log.Write(LogSource.Tray, "Running official logout command.");
            AgentCommandResult result;
            try
            {
                result = await _factory.RunOnceAsync(resolution.Spec, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Write(LogSource.Tray, $"Official logout command failed ({ex.GetType().Name}).");
                result = new AgentCommandResult(null, string.Empty);
            }

            var logoutParser = new AgentOutputParser();
            foreach (var line in SplitLines(result.Output))
            {
                _log.Write(LogSource.Agent, AgentLogPolicy.Sanitize(line, logoutParser.Parse(line)));
                _verboseLog?.Write(LogSource.Agent, line);
            }

            if (!result.Succeeded)
            {
                _agentWanted = false;
                _machine.SetAgentWanted(false);
                _machine.OnOperationFailed("Official logout failed; sign-in was not restarted.", false);
                _log.Write(LogSource.Tray, "Official logout failed; no replacement was started.");
                Raise(new TrayNotification(NotificationKind.StartFailure,
                    "Remote Commander - sign-out failed",
                    "The official CLI could not clear its credentials. Retry Re-authenticate; no replacement agent was started."));
                return;
            }

            _log.Write(LogSource.Tray, "Official logout completed.");

            _backoff.Reset();
            _consecutiveFailures = 0;
            _startFailureNotified = false;
            StartCore();
            _authNotified = true;

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
        _errorParser.Reset();
        _sessionLossHandled = false;
        _authNotified = false;

        // A user start can overtake a queued exit callback. Drain the old generation
        // before publishing a replacement, even when its root has already exited.
        if (_process is { } previous)
        {
            previous.OutputReceived -= HandleOutput;
            previous.Exited -= HandleExited;
            _acceptOutput = false;
            try { previous.Dispose(); }
            catch (Exception ex) { BlockReplacement(ex); return; }
            _process = null;
        }

        IAgentProcess process;
        try
        {
            process = _factory.Create(resolution.Spec);
        }
        catch (Exception ex)
        {
            _log.Write(LogSource.Tray, $"Agent creation failed ({ex.GetType().Name}).");
            _machine.OnProcessExited(null, userRequested: false);
            _consecutiveFailures++;
            NotifyStartFailureIfNeeded("Could not create the supervised process.");
            ScheduleRestart();
            return;
        }

        // The generation and its state are published *before* Start, because the real
        // process begins raising exit and output events from inside Start. Registering
        // afterwards lost an immediate exit for good, and let a late "Starting" overwrite
        // an Online that early output had already established.
        _process = process;
        _acceptOutput = true;
        _runStartedUtc = _clock();
        _stallNotified = false;
        _machine.OnProcessStarted();

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
            _acceptOutput = false;
            try { process.Dispose(); }
            catch (Exception cleanupError) { BlockReplacement(cleanupError); return; }
            _process = null;
            _log.Write(LogSource.Tray, $"Failed to launch agent ({ex.GetType().Name}).");
            _machine.OnProcessExited(null, userRequested: false);
            _consecutiveFailures++;
            NotifyStartFailureIfNeeded("Could not launch the supervised process.");
            ScheduleRestart();
            return;
        }

        _log.Write(
            LogSource.Tray,
            $"Agent started (pid {process.ProcessId?.ToString() ?? "?"}).");
    }

    private void BlockReplacement(Exception error)
    {
        _agentWanted = false;
        _machine.SetAgentWanted(false);
        CancelPendingRestart();
        _machine.OnOperationFailed("Agent cleanup failed. Retry Stop before restarting.", true);
        _log.Write(LogSource.Tray, $"Replacement blocked after cleanup failure ({error.GetType().Name}).");
        Raise(new TrayNotification(NotificationKind.StartFailure, "Remote Commander - cleanup failed",
            "Retry Stop before restarting the agent."));
    }

    private async Task StopCoreAsync()
    {
        var process = _process;
        if (process is null) return;
        _acceptOutput = false;
        process.OutputReceived -= HandleOutput;
        process.Exited -= HandleExited;
        try
        {
            // Stop must drain the whole generation, not just wait for the root.
            await process.StopAsync(StopGracePeriod).ConfigureAwait(false);
            process.Dispose();
            _process = null;
        }
        catch
        {
            // Keep ownership so a later Stop can retry. Never start a replacement
            // while termination is unconfirmed.
            _agentWanted = false;
            _machine.SetAgentWanted(false);
            CancelPendingRestart();
            _machine.OnOperationFailed("Agent cleanup failed. Retry Stop before restarting.", true);
            throw;
        }
    }

    private void HandleOutput(object? sender, AgentOutputLine line)
    {
        if (sender is IAgentProcess source && !_disposed)
            _ = OnOutputAsync(source, line);
    }

    private async Task OnOutputAsync(IAgentProcess source, AgentOutputLine line)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            // Parsing, generation validation and state changes use the lifecycle lock.
            // An old callback cannot resume inside a replacement's parser/state.
            if (_disposed || !_acceptOutput || !ReferenceEquals(source, _process)) return;
            var parser = line.IsError ? _errorParser : _parser;
            var signal = parser.Parse(line.Text);
            var logSource = line.IsError ? LogSource.AgentError : LogSource.Agent;
            _log.Write(logSource, AgentLogPolicy.Sanitize(line.Text, signal));
            _verboseLog?.Write(logSource, line.Text);
            if (signal.Kind == AgentSignalKind.SessionExpired && !_sessionLossHandled)
            {
                _sessionLossHandled = true;
                _authNotified = false;
            }
            _machine.Apply(signal);
        }
        catch (Exception ex)
        {
            _log.Write(LogSource.Tray, $"Agent output handling failed ({ex.GetType().Name}).");
        }
        finally { _mutex.Release(); }
    }

    private void HandleExited(object? sender, int? exitCode)
    {
        if (sender is not IAgentProcess source)
        {
            return;
        }

        _ = OnAgentExitedAsync(source, exitCode);
    }

    private async Task OnAgentExitedAsync(IAgentProcess source, int? exitCode)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            // Re-check identity under the lock. This callback can sit in the queue while a
            // Restart or Re-authenticate installs a different process; acting on whatever
            // _process happens to hold now would detach the replacement and start a third
            // agent alongside it.
            if (_disposed || !ReferenceEquals(_process, source))
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);

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
        catch (Exception ex)
        {
            _log.Write(LogSource.Tray, $"Agent exit cleanup failed ({ex.GetType().Name}); automatic restart stopped.");
            Raise(new TrayNotification(NotificationKind.StartFailure,
                "Remote Commander - cleanup failed", "Retry Stop before restarting the agent."));
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
        var token = cts.Token; // Capture before CancelPendingRestart can dispose the source.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await _mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                if (token.IsCancellationRequested || !_agentWanted || _disposed)
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
                    snapshot.RequiresReauthentication
                        ? "Your session expired. Click this notification or choose Re-authenticate to sign in again."
                        : "Please sign in using your browser, or choose Open sign-in page in the tray menu.",
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

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        await _healthTimer.DisposeAsync().ConfigureAwait(false);
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            _agentWanted = false;
            _machine.SetAgentWanted(false);
            _acceptOutput = false;
            CancelPendingRestart();
            try { await StopCoreAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log.Write(LogSource.Tray, $"Exit cleanup failed ({ex.GetType().Name}); closing the owned job as a final backstop.");
            }
            finally
            {
                // A failing drain still must not retain the kill-on-close handle when
                // exiting. During ordinary Restart we instead keep ownership and block.
                var remaining = _process;
                _process = null;
                if (remaining is not null)
                {
                    remaining.OutputReceived -= HandleOutput;
                    remaining.Exited -= HandleExited;
                    try { remaining.Dispose(); }
                    catch (Exception ex) { _log.Write(LogSource.Tray, $"Job disposal reported {ex.GetType().Name}."); }
                }
            }
        }
        finally { _mutex.Release(); }
        // Queued callbacks can acquire, observe _disposed and return. No native wait
        // handle is allocated by this SemaphoreSlim; do not dispose beneath waiters.
    }
}
