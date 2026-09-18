namespace RemoteCommanderTray.Core;

/// <summary>
/// Owns one agent generation. Commands are serialized; callbacks must still belong to
/// that generation when they act. Network recovery belongs to the official CLI.
/// </summary>
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
    // Never hold this lock across an await or process teardown. It makes checking an
    // output's generation, parsing it and applying its state one atomic operation.
    private readonly object _callbackGate = new();
    private readonly object _backgroundGate = new();
    private readonly HashSet<Task> _background = [];
    private readonly Func<DateTimeOffset> _clock;
    private readonly Timer _healthTimer;
    private volatile IAgentProcess? _process;
    private CancellationTokenSource? _restartCts;
    private Task? _disposeTask;
    private bool _agentWanted;
    private volatile bool _disposed;
    private bool _cleanupFailed;
    private DateTimeOffset _runStartedUtc;
    private int _consecutiveFailures;
    private bool _stallNotified;
    private bool _startFailureNotified;
    private bool _authNotified;
    private bool _sessionLossHandled;
    private string _launchDescription = "(not resolved)";

    public AgentSupervisor(
        AgentStateMachine machine, IAgentProcessFactory factory,
        AgentCommandResolver resolver, Func<TraySettings> settings, RollingFileLog log,
        Func<DateTimeOffset>? clock = null, RestartBackoff? backoff = null,
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
        _machine.Changed += OnMachineChanged;
        _healthTimer = new Timer(_ => RunHealthCheck(), null, HealthInterval, HealthInterval);
    }

    public event EventHandler<AgentSnapshot>? Changed;
    public event EventHandler<TrayNotification>? Notification;
    public AgentSnapshot Snapshot => _machine.Snapshot;
    public string LaunchDescription => _launchDescription;
    public bool IsRunning => _process is { HasExited: false };

    public async Task InitializeAsync()
    {
        if (_settings().StartAgentOnLaunch)
        {
            await StartAsync().ConfigureAwait(false);
        }
    }

    public async Task StartAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            CancelPendingRestart();
            WantAgent();
            ResetFailures();
            await StartCoreAsync().ConfigureAwait(false);
        }
        finally { _mutex.Release(); }
    }

    public async Task StopAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _agentWanted = false;
            _machine.SetAgentWanted(false);
            CancelPendingRestart();
            await StopCoreAsync().ConfigureAwait(false);
            _machine.OnProcessExited(null, userRequested: true);
            _log.Write(LogSource.Tray, "Agent stopped by user.");
        }
        finally { _mutex.Release(); }
    }

    public async Task RestartAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            CancelPendingRestart();
            await StopCoreAsync().ConfigureAwait(false);
            WantAgent();
            ResetFailures();
            _machine.OnRestartPerformed();
            await StartCoreAsync().ConfigureAwait(false);
        }
        finally { _mutex.Release(); }
    }

    /// <summary>Only the official CLI may remove its credentials. A failed logout is
    /// not reported as successful sign-in and never starts a second agent.</summary>
    public async Task ReauthenticateAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            CancelPendingRestart();
            await StopCoreAsync().ConfigureAwait(false);
            WantAgent();
            var resolution = _resolver.Resolve(AgentCommand.Logout, _settings());
            if (resolution.Spec is null) { ReportUnavailable(resolution.Problem); return; }

            AgentCommandResult result;
            try
            {
                result = await _factory.RunOnceAsync(resolution.Spec, TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Write(LogSource.Tray, $"Logout failed ({ex.GetType().Name}).");
                _machine.OnSessionLost("Sign-out failed. Retry Re-authenticate.");
                return;
            }

            var logoutParser = new AgentOutputParser();
            foreach (var line in result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                _log.Write(LogSource.Agent, AgentLogPolicy.Sanitize(line, logoutParser.Parse(line)));
                _verboseLog?.Write(LogSource.Agent, line);
            }
            if (!result.Succeeded)
            {
                _log.Write(LogSource.Tray, $"Logout did not succeed (exit {result.ExitCode?.ToString() ?? "unknown"}).");
                _machine.OnSessionLost("Sign-out did not complete. Retry Re-authenticate.");
                return;
            }

            ResetFailures();
            lock (_callbackGate) { _authNotified = true; }
            Raise(new TrayNotification(NotificationKind.ReauthenticationStarted,
                "Remote Commander - sign-in needed",
                "Finish the official Desktop Commander sign-in in your browser. "
                + "Use Open sign-in page in the tray if no browser opens."));
            await StartCoreAsync().ConfigureAwait(false);
        }
        finally { _mutex.Release(); }
    }

    private void WantAgent()
    {
        _agentWanted = true;
        _machine.SetAgentWanted(true);
        lock (_callbackGate) { _authNotified = false; }
    }

    private void ResetFailures()
    {
        _backoff.Reset();
        _consecutiveFailures = 0;
        _startFailureNotified = false;
    }

    // Caller holds _mutex. The prior generation must be drained even if its root
    // already died and its exit callback is still waiting for this same semaphore.
    private async Task StartCoreAsync()
    {
        if (_disposed || IsRunning) return;
        if (_cleanupFailed)
        {
            ReportUnavailable("Previous agent cleanup failed. Exit the tray before trying again.");
            return;
        }
        await StopCoreAsync().ConfigureAwait(false);
        IAgentProcess? process = null;
        try
        {
            var resolution = _resolver.Resolve(AgentCommand.Remote, _settings());
            if (resolution.Spec is null) { ReportUnavailable(resolution.Problem); return; }
            _launchDescription = resolution.Spec.Description;
            process = _factory.Create(resolution.Spec);
            lock (_callbackGate)
            {
                _parser.Reset();
                _errorParser.Reset();
                _sessionLossHandled = false;
                _process = process;
                _runStartedUtc = _clock();
                _stallNotified = false;
                _machine.OnProcessStarted();
                process.OutputReceived += HandleOutput;
                process.Exited += HandleExited;
            }
            process.Start();
            _log.Write(LogSource.Tray, $"Agent started (pid {process.ProcessId?.ToString() ?? "unknown"}).");
        }
        catch (Exception ex)
        {
            await StopCoreAsync().ConfigureAwait(false);
            // Do not copy arbitrary command arguments or exception text to the ordinary log.
            _log.Write(LogSource.Tray, $"Failed to launch agent ({ex.GetType().Name}).");
            _machine.OnProcessExited(null, userRequested: false);
            _consecutiveFailures++;
            NotifyStartFailureIfNeeded();
            ScheduleRestart();
        }
    }

    private async Task StopCoreAsync()
    {
        IAgentProcess? process;
        lock (_callbackGate)
        {
            process = _process;
            _process = null;
            if (process is not null)
            {
                process.OutputReceived -= HandleOutput;
                process.Exited -= HandleExited;
            }
        }
        if (process is null) return;
        try
        {
            try { await process.StopAsync(StopGracePeriod).ConfigureAwait(false); }
            finally { process.Dispose(); }
        }
        catch
        {
            _cleanupFailed = true;
            _agentWanted = false;
            _machine.SetAgentWanted(false);
            CancelPendingRestart();
            _machine.OnProcessExited(null, userRequested: false);
            Raise(new TrayNotification(NotificationKind.StartFailure,
                "Remote Commander - cleanup failed",
                "The prior agent could not be fully cleaned up. No replacement will be started. Exit the tray."));
            throw;
        }
    }

    private void HandleOutput(object? sender, AgentOutputLine line)
    {
        lock (_callbackGate)
        {
            if (_disposed || sender is not IAgentProcess source || !ReferenceEquals(source, _process)) return;
            // stdout/stderr have independent multiline auth/result prompts, but state
            // mutations are serialized. No unbounded per-line Task.Run queue.
            var parser = line.IsError ? _errorParser : _parser;
            var signal = parser.Parse(line.Text);
            var logSource = line.IsError ? LogSource.AgentError : LogSource.Agent;
            _log.Write(logSource, AgentLogPolicy.Sanitize(line.Text, signal));
            _verboseLog?.Write(logSource, line.Text);
            // The UI can surface categories, not arbitrary failure text from tool output.
            signal = AgentLogPolicy.SafeStateSignal(signal);
            _machine.Apply(signal);
            if (signal.Kind == AgentSignalKind.SessionExpired && !_sessionLossHandled)
            {
                _sessionLossHandled = true;
                QueueCallback(() => OnSessionLostAsync(source));
            }
        }
    }

    private async Task OnSessionLostAsync(IAgentProcess source)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || !_agentWanted || !ReferenceEquals(source, _process)) return;
            CancelPendingRestart();
            await StopCoreAsync().ConfigureAwait(false);
            _machine.OnSessionLost("Remote session expired. Use Re-authenticate to sign in again.");
            // This is a user-action boundary, not a crash: do not repeatedly launch
            // browsers, clear credentials, or retry a revoked session automatically.
        }
        finally { _mutex.Release(); }
    }

    private void HandleExited(object? sender, int? exitCode)
    {
        if (sender is IAgentProcess source) QueueCallback(() => OnAgentExitedAsync(source, exitCode));
    }

    private async Task OnAgentExitedAsync(IAgentProcess source, int? exitCode)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || !ReferenceEquals(source, _process)) return;
            var waitingForAuthentication = Snapshot.State == AgentState.AuthenticationRequired;
            await StopCoreAsync().ConfigureAwait(false);
            _log.Write(LogSource.Tray, $"Agent exited (code {exitCode?.ToString() ?? "unknown"}).");
            if (waitingForAuthentication && _agentWanted)
            {
                _machine.OnSessionLost("Sign-in did not complete. Use Re-authenticate to retry.");
                return;
            }
            _machine.OnProcessExited(exitCode, userRequested: !_agentWanted);
            if (!_agentWanted) return;
            if (_clock() - _runStartedUtc >= TimeSpan.FromSeconds(_settings().HealthyRunSeconds)) ResetFailures();
            _consecutiveFailures++;
            NotifyStartFailureIfNeeded();
            ScheduleRestart();
        }
        finally { _mutex.Release(); }
    }

    private void ScheduleRestart()
    {
        CancelPendingRestart();
        if (_disposed || !_agentWanted || _cleanupFailed) return;
        var delay = _backoff.NextDelay();
        _machine.OnRestartScheduled(_backoff.Attempt, _clock() + delay);
        _log.Write(LogSource.Tray, $"Restart scheduled in {delay.TotalSeconds:0}s (attempt {_backoff.Attempt}).");
        var cts = new CancellationTokenSource();
        var token = cts.Token; // Capture before any other command can dispose the CTS.
        _restartCts = cts;
        QueueCallback(async () =>
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            await _mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                if (token.IsCancellationRequested || _disposed || !_agentWanted || !ReferenceEquals(cts, _restartCts)) return;
                _machine.OnRestartPerformed();
                await StartCoreAsync().ConfigureAwait(false);
            }
            finally { _mutex.Release(); }
        });
    }

    private void CancelPendingRestart()
    {
        var cts = _restartCts;
        _restartCts = null;
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
        _machine.ClearPendingRestart();
    }

    private void QueueCallback(Func<Task> work)
    {
        lock (_backgroundGate)
        {
            if (_disposed) return;
            var task = Task.Run(async () =>
            {
                try { await work().ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _log.Write(LogSource.Tray, $"Lifecycle callback failed ({ex.GetType().Name}).");
                }
            });
            _background.Add(task);
            _ = task.ContinueWith(t =>
            {
                lock (_backgroundGate) { _background.Remove(t); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void RunHealthCheck()
    {
        if (_disposed || !_mutex.Wait(0)) return;
        try
        {
            if (_disposed) return;
            var snapshot = Snapshot;
            if (snapshot.State == AgentState.Online)
            {
                _stallNotified = false;
                if (_clock() - snapshot.StateSinceUtc >= TimeSpan.FromSeconds(_settings().HealthyRunSeconds)) ResetFailures();
                return;
            }
            if (!_agentWanted || !snapshot.ProcessRunning || _stallNotified ||
                snapshot.State is not (AgentState.Connecting or AgentState.Starting)) return;
            var elapsed = _clock() - snapshot.StateSinceUtc;
            if (elapsed < TimeSpan.FromMinutes(_settings().StalledConnectionMinutes)) return;
            _stallNotified = true;
            _machine.OnConnectionStalled(elapsed);
            Raise(new TrayNotification(NotificationKind.ConnectionStalled, "Remote Commander - not connected",
                "The device has not come online. Try Restart connection, or Re-authenticate if sign-in expired."));
        }
        finally { _mutex.Release(); }
    }

    private void OnMachineChanged(object? sender, AgentSnapshot snapshot)
    {
        lock (_callbackGate)
        {
            if (snapshot.State == AgentState.AuthenticationRequired && !_authNotified)
            {
                _authNotified = true;
                Raise(new TrayNotification(NotificationKind.AuthenticationRequired,
                    "Remote Commander - sign-in required",
                    "Desktop Commander needs you to sign in. Use Open sign-in page, or Re-authenticate if the session expired.",
                    snapshot.VerificationUri));
            }
            else if (snapshot.State == AgentState.Online) _authNotified = false;
            Changed?.Invoke(this, snapshot);
        }
    }

    private void NotifyStartFailureIfNeeded()
    {
        if (_consecutiveFailures < _settings().StartFailureAlertThreshold || _startFailureNotified) return;
        _startFailureNotified = true;
        Raise(new TrayNotification(NotificationKind.StartFailure, "Remote Commander - agent keeps failing",
            $"The Desktop Commander agent failed to stay running {_consecutiveFailures} times in a row. Open logs for details."));
    }

    private void ReportUnavailable(string? problem)
    {
        _machine.OnProcessExited(null, userRequested: false);
        _log.Write(LogSource.Tray, "Agent launch unavailable. Check installation and configuration.");
        Raise(new TrayNotification(NotificationKind.AgentUnavailable, "Remote Commander - agent unavailable",
            problem ?? "Desktop Commander could not be found."));
    }

    private void Raise(TrayNotification notification)
    {
        if (_settings().NotificationsEnabled) Notification?.Invoke(this, notification);
    }

    public ValueTask DisposeAsync()
    {
        lock (_backgroundGate)
        {
            _disposed = true;
            return new ValueTask(_disposeTask ??= Task.Run(DisposeCoreAsync));
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _healthTimer.DisposeAsync().ConfigureAwait(false);
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            _agentWanted = false;
            CancelPendingRestart();
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { _mutex.Release(); }
        Task[] pending;
        lock (_backgroundGate) { pending = [.. _background]; }
        await Task.WhenAll(pending).ConfigureAwait(false);
        _machine.Changed -= OnMachineChanged;
        // SemaphoreSlim never allocates an OS handle here. Leave it usable for a public
        // command already queued before disposal; its disposed guard will make it inert.
    }
}
