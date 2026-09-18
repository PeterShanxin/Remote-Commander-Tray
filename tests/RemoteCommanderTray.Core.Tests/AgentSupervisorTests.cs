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
    public async Task A_dropped_channel_does_not_restart_the_process()
    {
        await _supervisor.StartAsync();
        var agent = _factory.Latest;
        agent.Emit("✅ Device ready:");
        agent.Emit("❌ Channel error: websocket closed");

        await Task.Delay(120);

        Assert.Equal(1, _factory.CreatedCount);
        Assert.Equal(AgentState.Connecting, _supervisor.Snapshot.State);
        Assert.False(agent.StopRequested);
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
