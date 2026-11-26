using System.Reflection;
using RemoteCommanderTray.Core;
using Xunit;

namespace RemoteCommanderTray.Core.Tests;

public class AgentSupervisorTests : IDisposable
{
    private static readonly TimeSpan FastSchedule = TimeSpan.FromMilliseconds(20);

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "rct-sup-" + Guid.NewGuid().ToString("N"));

    private readonly FakeAgentProcessFactory _factory = new();
    private readonly TraySettings _settings = new() { StartFailureAlertThreshold = 2, HealthyRunSeconds = 60 };
    private readonly List<TrayNotification> _notifications = [];
    private readonly RollingFileLog _log;
    private readonly AgentStateMachine _machine = new();
    private readonly AgentSupervisor _supervisor;

    public AgentSupervisorTests()
    {
        Directory.CreateDirectory(_folder);
        _log = new RollingFileLog(Path.Combine(_folder, "agent.log"), 64 * 1024, 1);
        _supervisor = new AgentSupervisor(
            _machine,
            _factory,
            new AgentCommandResolver(new InstalledAgentEnvironment()),
            () => _settings,
            _log,
            backoff: new RestartBackoff([FastSchedule]));
        _supervisor.Notification += (_, n) =>
        {
            lock (_notifications)
            {
                _notifications.Add(n);
            }
        };
    }

    [Fact]
    public async Task Wake_keeps_the_agent_quiet_until_a_reported_disconnect_really_stalls()
    {
        var now = DateTimeOffset.UtcNow;
        var machine = new AgentStateMachine(() => now);
        var backoff = new RestartBackoff();
        await using var supervisor = new AgentSupervisor(machine, _factory,
            new AgentCommandResolver(new InstalledAgentEnvironment()), () => _settings, _log, clock: () => now,
            backoff: backoff);
        var notices = new List<TrayNotification>();
        supervisor.Notification += (_, notice) => notices.Add(notice);
        var health = typeof(AgentSupervisor).GetMethod("RunHealthCheck", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await supervisor.StartAsync();
        var agent = _factory.Latest;
        agent.Emit("Device ready:");
        backoff.NextDelay(); // A previous failed attempt awaits the normal healthy-run reset.
        machine.OnSystemSuspending();
        now = now.AddSeconds(_settings.HealthyRunSeconds);
        machine.OnSystemResumed();
        health.Invoke(supervisor, null);
        Assert.Equal(0, backoff.Attempt);
        now = now.AddDays(1);
        health.Invoke(supervisor, null);
        Assert.Equal(AgentState.Unconfirmed, supervisor.Snapshot.State);
        Assert.Null(supervisor.Snapshot.NextRestartUtc);
        Assert.Equal(0, supervisor.Snapshot.TotalRestartCount);
        Assert.Single(_factory.Created);
        Assert.True(supervisor.IsRunning);
        Assert.False(agent.StopRequested);
        Assert.Empty(notices);

        agent.Emit("Channel closed", isError: true);
        health.Invoke(supervisor, null);
        Assert.Equal(AgentState.Connecting, supervisor.Snapshot.State);
        Assert.Empty(notices);
        now = now.AddMinutes(_settings.StalledConnectionMinutes);
        health.Invoke(supervisor, null);
        health.Invoke(supervisor, null);
        Assert.Equal(AgentState.Error, supervisor.Snapshot.State);
        Assert.Equal(NotificationKind.ConnectionStalled, Assert.Single(notices).Kind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Wake_cannot_override_stop_or_a_replacement_generation(bool restart, bool delayedWake)
    {
        await _supervisor.StartAsync();
        _factory.Latest.Emit("Device ready:");
        _machine.OnSystemSuspending();
        if (!delayedWake)
        {
            _machine.OnSystemResumed();
            Assert.Equal(AgentState.Unconfirmed, _supervisor.Snapshot.State);
        }
        if (restart)
        {
            await _supervisor.RestartAsync();
            _factory.Latest.Emit("Device ready:");
        }
        else
        {
            await _supervisor.StopAsync();
        }
        _machine.OnSystemResumed();
        Assert.Equal(restart ? AgentState.Online : AgentState.Stopped, _supervisor.Snapshot.State);
        Assert.Empty(_notifications);
    }

    [Fact]
    public async Task Session_expiry_after_wake_still_pauses_for_one_sign_in_notice()
    {
        await _supervisor.StartAsync();
        var agent = _factory.Latest;
        agent.Emit("Device ready:");
        _machine.OnSystemSuspending();
        _machine.OnSystemResumed();
        agent.Emit("Remote session expired and could not be renewed.");
        await WaitUntil(() => !_supervisor.Snapshot.ProcessRunning && _supervisor.Snapshot.RequiresReauthentication);
        _machine.OnSystemSuspending();
        _machine.OnSystemResumed();
        Assert.Equal(AgentState.AuthenticationRequired, _supervisor.Snapshot.State);
        Assert.Equal(NotificationKind.AuthenticationRequired, Assert.Single(_notifications).Kind);
        Assert.True(agent.StopRequested);
        Assert.Null(_supervisor.Snapshot.NextRestartUtc);
    }

    [Fact]
    public async Task Start_launches_exactly_one_agent_and_a_second_start_does_not_add_another()
    {
        await _supervisor.StartAsync();
        await _supervisor.StartAsync();

        Assert.Equal(1, _factory.CreatedCount);
        Assert.True(_factory.Latest.Started);
        Assert.True(_supervisor.IsRunning);
    }

    [Fact]
    public async Task Agent_output_drives_the_state()
    {
        await _supervisor.StartAsync();
        var agent = _factory.Latest;

        agent.Emit("\U0001F680 Starting MCP Device...");
        agent.Emit("⏳ Connecting to Remote MCP https://mcp.desktopcommander.app");
        agent.Emit("✅ Device ready:");
        agent.Emit("   - Device Name:  SHANXINMEOWPEOW");

        Assert.Equal(AgentState.Online, _supervisor.Snapshot.State);
        Assert.Equal("SHANXINMEOWPEOW", _supervisor.Snapshot.DeviceName);
    }

    [Fact]
    public async Task Timestamped_ready_output_prevents_the_five_minute_false_error()
    {
        var now = new DateTimeOffset(2026, 11, 7, 8, 25, 6, TimeSpan.Zero);
        var notices = new List<TrayNotification>();
        await using var supervisor = new AgentSupervisor(
            new AgentStateMachine(() => now), _factory,
            new AgentCommandResolver(new InstalledAgentEnvironment()),
            () => _settings, _log, clock: () => now);
        supervisor.Notification += (_, notice) => notices.Add(notice);

        await supervisor.StartAsync();
        var agent = _factory.Latest;
        agent.Emit("[16:25:06] \U0001F680 Starting MCP Device...");
        agent.Emit("[16:25:06] ⏳ Connecting to Remote MCP https://example.com");
        agent.Emit("[16:25:06] ✅ Device ready:");
        agent.Emit("[16:25:06]    - Device Name:  TEST-PC");

        now = now.AddMinutes(6);
        // Trigger the existing timer callback without a wall-clock wait.
        typeof(AgentSupervisor).GetMethod("RunHealthCheck", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(supervisor, null);

        Assert.Equal(AgentState.Online, supervisor.Snapshot.State);
        Assert.Equal("Online", supervisor.Snapshot.StateLabel);
        Assert.Equal("TEST-PC", supervisor.Snapshot.DeviceName);
        Assert.True(supervisor.IsRunning);
        Assert.Equal(1, _factory.CreatedCount);
        Assert.False(agent.StopRequested);
        Assert.DoesNotContain(notices, n => n.Kind == NotificationKind.ConnectionStalled);
        Assert.Contains(_log.Tail(50), line => line.Contains("Device ready.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_stall_after_brief_recovery_warns_even_between_health_checks()
    {
        var now = new DateTimeOffset(2026, 11, 26, 1, 0, 0, TimeSpan.Zero);
        var machine = new AgentStateMachine(() => now);
        await using var supervisor = new AgentSupervisor(machine, _factory,
            new AgentCommandResolver(new InstalledAgentEnvironment()), () => _settings, _log, clock: () => now);
        var notices = new List<TrayNotification>();
        supervisor.Notification += (_, notice) => notices.Add(notice);
        var health = typeof(AgentSupervisor).GetMethod("RunHealthCheck", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await supervisor.StartAsync();
        now = now.AddMinutes(_settings.StalledConnectionMinutes);
        health.Invoke(supervisor, null);
        Assert.Equal(AgentState.Error, supervisor.Snapshot.State);
        Assert.Single(notices, notice => notice.Kind == NotificationKind.ConnectionStalled);

        _factory.Latest.Emit("Device ready:");
        Assert.Equal(AgentState.Online, supervisor.Snapshot.State);
        _factory.Latest.Emit("Device marked as offline");
        now = now.AddMinutes(_settings.StalledConnectionMinutes);
        health.Invoke(supervisor, null);
        health.Invoke(supervisor, null);

        Assert.Equal(AgentState.Error, supervisor.Snapshot.State);
        Assert.Equal(2, notices.Count(notice => notice.Kind == NotificationKind.ConnectionStalled));
        Assert.True(supervisor.IsRunning);
        Assert.Single(_factory.Created);
    }

    [Fact]
    public async Task Consecutive_crashes_follow_each_delay_and_a_long_run_resets_the_streak()
    {
        var now = new DateTimeOffset(2026, 11, 26, 1, 0, 0, TimeSpan.Zero);
        TimeSpan[] delays = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(80),
            TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(160)];
        await using var supervisor = new AgentSupervisor(new AgentStateMachine(() => now), _factory,
            new AgentCommandResolver(new InstalledAgentEnvironment()), () => _settings, _log,
            clock: () => now, backoff: new RestartBackoff(delays));
        var scheduled = System.Threading.Channels.Channel.CreateUnbounded<AgentSnapshot>();
        DateTimeOffset? previousDeadline = null;
        supervisor.Changed += (_, snapshot) =>
        {
            if (snapshot.NextRestartUtc is not null && snapshot.NextRestartUtc != previousDeadline)
                scheduled.Writer.TryWrite(snapshot);
            previousDeadline = snapshot.NextRestartUtc;
        };
        _factory.NextExitDuringStart = 7;
        await supervisor.StartAsync();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var snapshot = await scheduled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(attempt, snapshot.RestartCount);
            Assert.Equal(now + delays[Math.Min(attempt - 1, delays.Length - 1)], snapshot.NextRestartUtc);
            await WaitUntil(() => _factory.CreatedCount == attempt + 1 && _factory.Latest.Started);
            if (attempt < 5) _factory.Latest.Crash();
        }

        await WaitUntil(() => !supervisor.Snapshot.NextRestartUtc.HasValue);
        now = now.AddSeconds(_settings.HealthyRunSeconds);
        _factory.Latest.Crash();
        var reset = await scheduled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, reset.RestartCount);
        Assert.Equal(now + delays[0], reset.NextRestartUtc);
        Assert.Equal(5, reset.TotalRestartCount);
    }

    [Fact]
    public async Task Timestamped_tool_content_cannot_claim_online_or_reach_logs()
    {
        await _supervisor.StartAsync();
        var agent = _factory.Latest;
        agent.Emit("[16:25:06] \U0001F527 Received tool call 42: write_file {\"content\":\"Device ready: private-result-sentinel\"} metadata: {}");
        agent.Emit("[16:25:06] ✅ Tool call write_file completed:");
        agent.Emit("[16:25:06] Device marked as online private-result-sentinel");
        agent.Emit("[16:25:06] {\"Device ready\":\"private-result-sentinel\"}");

        Assert.Equal(AgentState.Starting, _supervisor.Snapshot.State);
        var logged = string.Join('\n', _log.Tail(50));
        Assert.DoesNotContain("private-result-sentinel", logged);
        Assert.Contains("Tool activity (arguments and results omitted).", logged);
        Assert.Contains("tool result omitted", logged);

        agent.Emit("[16:25:07] ✅ Device ready:");
        Assert.Equal(AgentState.Online, _supervisor.Snapshot.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timestamped_authentication_prompts_keep_sign_in_details_out_of_logs(bool isError)
    {
        await _supervisor.StartAsync();
        var agent = _factory.Latest;
        agent.Emit("[16:25:06] \U0001F510 Starting device authorization flow...", isError);
        agent.Emit("[16:25:06]    1. Verify this device in your browser:", isError);
        agent.Emit("[16:25:06]       https://example.com/device?code=TEST-CODE", isError);
        agent.Emit("[16:25:06]    2. Make sure the code matches:", isError);
        agent.Emit("[16:25:06]       TEST-CODE", isError);

        Assert.Equal(AgentState.AuthenticationRequired, _supervisor.Snapshot.State);
        Assert.Equal("https://example.com/device?code=TEST-CODE", _supervisor.Snapshot.VerificationUri);
        Assert.Equal("TEST-CODE", _supervisor.Snapshot.UserCode);
        Assert.Single(_notifications, n => n.Kind == NotificationKind.AuthenticationRequired);
        var logged = string.Join('\n', _log.Tail(50));
        Assert.DoesNotContain("example.com", logged);
        Assert.DoesNotContain("TEST-CODE", logged);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[16:25:06] ")]
    public async Task A_dropped_channel_does_not_restart_the_process(string prefix)
    {
        await _supervisor.StartAsync();
        var agent = _factory.Latest;
        agent.Emit(prefix + "✅ Device ready:");
        agent.Emit(prefix + "❌ Channel error: websocket closed");

        await Task.Delay(120);

        Assert.Equal(1, _factory.CreatedCount);
        Assert.Equal(AgentState.Connecting, _supervisor.Snapshot.State);
        Assert.False(agent.StopRequested);

        agent.Emit(prefix + "✅ Channel subscribed");
        Assert.Equal(AgentState.Online, _supervisor.Snapshot.State);
    }

    [Fact]
    public async Task A_crash_is_restarted_after_the_backoff()
    {
        await _supervisor.StartAsync();
        _factory.Latest.Crash(1);

        await WaitUntil(() => _factory.CreatedCount == 2);

        Assert.Equal(2, _factory.CreatedCount);
        Assert.True(_factory.Latest.Started);
    }

    [Fact]
    public async Task A_user_stop_is_final()
    {
        await _supervisor.StartAsync();
        await _supervisor.StopAsync();

        await Task.Delay(150);

        Assert.Equal(1, _factory.CreatedCount);
        Assert.Equal(AgentState.Stopped, _supervisor.Snapshot.State);
        Assert.False(_supervisor.Snapshot.AgentWanted);
        Assert.False(_supervisor.IsRunning);
    }

    [Fact]
    public async Task A_crash_after_a_user_stop_is_not_restarted()
    {
        await _supervisor.StartAsync();
        var agent = _factory.Latest;
        await _supervisor.StopAsync();

        // The real process can report its exit after the stop call returns.
        agent.Crash(1);
        await Task.Delay(150);

        Assert.Equal(1, _factory.CreatedCount);
        Assert.Equal(AgentState.Stopped, _supervisor.Snapshot.State);
    }

    [Fact]
    public async Task Restart_stops_the_old_agent_and_starts_a_new_one()
    {
        await _supervisor.StartAsync();
        var first = _factory.Latest;

        await _supervisor.RestartAsync();

        Assert.True(first.StopRequested);
        Assert.Equal(2, _factory.CreatedCount);
        Assert.NotSame(first, _factory.Latest);
    }

    [Fact]
    public async Task Reauthenticate_runs_the_official_logout_then_restarts()
    {
        await _supervisor.StartAsync();
        var first = _factory.Latest;

        await _supervisor.ReauthenticateAsync();

        Assert.True(first.StopRequested);
        var logout = Assert.Single(_factory.OneShotCommands);
        Assert.EndsWith("remote --logout", logout.Arguments);
        Assert.Equal(2, _factory.CreatedCount);
        Assert.Contains(_notifications, n => n.Kind == NotificationKind.ReauthenticationStarted);
    }

    [Fact]
    public async Task Repeated_start_failures_notify_once()
    {
        _factory.NextStartFailure = new InvalidOperationException("node.exe is missing");

        await _supervisor.StartAsync();
        await WaitUntil(() => _factory.CreatedCount >= 3);
        await _supervisor.StopAsync();

        var failures = _notifications.Where(n => n.Kind == NotificationKind.StartFailure).ToList();
        Assert.Single(failures);
        Assert.Contains("failed to stay running", failures[0].Message);
    }

    [Fact]
    public async Task An_authentication_prompt_notifies_with_the_sign_in_page()
    {
        await _supervisor.StartAsync();
        var agent = _factory.Latest;

        agent.Emit("\U0001F510 Starting device authorization flow...");
        agent.Emit("   1. Verify this device in your browser:");
        agent.Emit("      https://desktopcommander.app/device?code=WDJB-MJHT");
        agent.Emit("   2. Make sure the code matches:");
        agent.Emit("      WDJB-MJHT");

        Assert.Equal(AgentState.AuthenticationRequired, _supervisor.Snapshot.State);
        Assert.Equal("WDJB-MJHT", _supervisor.Snapshot.UserCode);

        var notification = Assert.Single(_notifications, n => n.Kind == NotificationKind.AuthenticationRequired);
        Assert.Contains("sign in", notification.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Notifications_can_be_switched_off()
    {
        _settings.NotificationsEnabled = false;
        await _supervisor.StartAsync();
        _factory.Latest.Emit("\U0001F510 Starting device authorization flow...");

        Assert.Empty(_notifications);
    }

    [Fact]
    public async Task A_missing_cli_is_reported_rather_than_retried_silently()
    {
        var log = new RollingFileLog(Path.Combine(_folder, "missing.log"), 64 * 1024, 1);
        var notifications = new List<TrayNotification>();
        await using var supervisor = new AgentSupervisor(
            new AgentStateMachine(),
            new FakeAgentProcessFactory(),
            new AgentCommandResolver(new EmptyEnvironment()),
            () => _settings,
            log);
        supervisor.Notification += (_, n) => notifications.Add(n);

        await supervisor.StartAsync();

        Assert.Contains(notifications, n => n.Kind == NotificationKind.AgentUnavailable);
        Assert.False(supervisor.IsRunning);
    }

    [Fact]
    public async Task Stop_restart_and_quit_each_cancel_an_unexpired_crash_retry()
    {
        var retryDelay = TimeSpan.FromMilliseconds(500);
        foreach (var action in new[] { "stop", "restart", "quit" })
        {
            var factory = new FakeAgentProcessFactory();
            await using var supervisor = new AgentSupervisor(new(), factory,
                new AgentCommandResolver(new InstalledAgentEnvironment()), () => _settings, _log,
                backoff: new RestartBackoff([retryDelay]));
            await supervisor.StartAsync();
            factory.Latest.Crash();
            await WaitUntil(() => supervisor.Snapshot.NextRestartUtc.HasValue);

            switch (action)
            {
                case "stop": await supervisor.StopAsync(); break;
                case "restart": await supervisor.RestartAsync(); break;
                case "quit": await supervisor.DisposeAsync(); break;
            }

            await Task.Delay(retryDelay + TimeSpan.FromMilliseconds(150));
            Assert.Null(supervisor.Snapshot.NextRestartUtc);
            Assert.Equal(action == "restart" ? 2 : 1, factory.CreatedCount);
            Assert.Equal(action == "restart", supervisor.IsRunning);
        }
    }

    [Fact]
    public async Task Missing_dependency_at_retry_does_not_leave_a_countdown()
    {
        var environment = new AgentCommandResolverTests.FakeEnvironment
        {
            Variables = { ["PATH"] = @"C:\Tools" },
            Files = { @"C:\Tools\node.exe", @"C:\Tools\desktop-commander.cmd" },
        };
        await using var supervisor = new AgentSupervisor(new(), _factory, new(environment),
            () => _settings, _log, backoff: new RestartBackoff([TimeSpan.FromMilliseconds(20)]));
        var notice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.Notification += (_, notification) =>
        {
            if (notification.Kind == NotificationKind.AgentUnavailable) notice.TrySetResult();
        };

        await supervisor.StartAsync();
        environment.Files.Clear();
        _factory.Latest.Crash();
        await notice.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(_factory.Created);
        Assert.Null(supervisor.Snapshot.NextRestartUtc);
        Assert.Equal(AgentState.Error, supervisor.Snapshot.State);
        Assert.Contains("Node.js", supervisor.Snapshot.LastError);
    }

    [Fact]
    public async Task Disposing_stops_the_agent()
    {
        await _supervisor.StartAsync();
        var agent = _factory.Latest;

        await _supervisor.DisposeAsync();

        Assert.True(agent.StopRequested);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "Timed out waiting for the expected state.");
    }

    public void Dispose()
    {
        _supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _log.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Test scratch space.
        }

        GC.SuppressFinalize(this);
    }

    private sealed class EmptyEnvironment : IAgentEnvironment
    {
        public bool FileExists(string path) => false;

        public string? GetVariable(string name) => null;
    }
}
