using System.Reflection;
using RemoteCommanderTray.Core;
using Xunit;

namespace RemoteCommanderTray.Core.Tests;

public sealed class WakeGraceTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rct-wake-" + Guid.NewGuid());
    private readonly FakeAgentProcessFactory _factory = new();
    private readonly AgentStateMachine _machine;
    private readonly RollingFileLog _log;
    private readonly AgentSupervisor _supervisor;
    private readonly List<TrayNotification> _notices = [];
    private TraySettings _settings = new();
    private DateTimeOffset _now = new(2026, 11, 26, 1, 0, 0, TimeSpan.Zero);

    public WakeGraceTests()
    {
        Directory.CreateDirectory(_folder);
        _machine = new(() => _now);
        _log = new(Path.Combine(_folder, "agent.log"), 65536, 1);
        _supervisor = new(_machine, _factory, new(new InstalledAgentEnvironment()),
            () => _settings, _log, clock: () => _now);
        _supervisor.Notification += (_, notice) => _notices.Add(notice);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _supervisor.DisposeAsync();
        _log.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private async Task<AgentSnapshot> WakeOnlineAgent()
    {
        await _supervisor.StartAsync();
        _factory.Latest.Emit("Device ready:");
        var online = _machine.Snapshot;
        _machine.OnSystemSuspending();
        _now = _now.AddHours(1);
        _machine.OnSystemResumed();
        return online;
    }

    private void CheckHealth() =>
        typeof(AgentSupervisor).GetMethod("RunHealthCheck", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_supervisor, null);

    [Theory]
    [InlineData("{}", 180)]
    [InlineData("{\"wakeGraceSeconds\":240}", 240)]
    [InlineData("{\"wakeGraceSeconds\":1}", 90)]
    public async Task Quiet_wake_restores_only_the_display_at_the_configured_deadline(string json, int seconds)
    {
        var path = Path.Combine(_folder, "settings.json");
        File.WriteAllText(path, json);
        _settings = new SettingsStore(path).Load();
        var online = await WakeOnlineAgent();
        _now = _now.AddSeconds(seconds - 1);
        CheckHealth();
        Assert.Equal(AgentState.Unconfirmed, _machine.Snapshot.State);
        _now = _now.AddSeconds(1);
        CheckHealth();
        // Record equality also covers LastConnectedUtc, OnlineSinceUtc and both restart counts.
        Assert.Equal(online with { StateSinceUtc = _now }, _machine.Snapshot);
        Assert.Single(_factory.Created);
        Assert.False(_factory.Latest.StopRequested);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task A_second_sleep_during_grace_requires_a_full_window_after_the_latest_wake()
    {
        await WakeOnlineAgent();
        _now = _now.AddSeconds(170);
        _machine.OnSystemSuspending();
        _now = _now.AddHours(1);
        CheckHealth(); // The health timer can run before Windows delivers resume.
        Assert.Equal(AgentState.Unconfirmed, _machine.Snapshot.State);
        _machine.OnSystemResumed();
        _now = _now.AddSeconds(179);
        _machine.OnSystemResumed(); // Duplicate resume must not extend the window.
        CheckHealth();
        Assert.Equal(AgentState.Unconfirmed, _machine.Snapshot.State);
        _now = _now.AddSeconds(1);
        CheckHealth();
        Assert.Equal(AgentState.Online, _machine.Snapshot.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Grace_expiry_during_teardown_cannot_restore_the_stopped_or_replacement_agent(bool restart)
    {
        await WakeOnlineAgent();
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.Latest.StopGate = drain;
        var command = restart ? _supervisor.RestartAsync() : _supervisor.StopAsync();
        try
        {
            _now = _now.AddSeconds(180);
            CheckHealth();
            Assert.Equal(AgentState.Stopping, _machine.Snapshot.State);
        }
        finally
        {
            drain.TrySetResult();
            await command;
        }
        CheckHealth();
        Assert.Equal(restart ? AgentState.Starting : AgentState.Stopped, _machine.Snapshot.State);
        if (restart)
        {
            _factory.Latest.Emit("Device ready:");
            _machine.OnSystemSuspending();
            _machine.OnSystemResumed();
            CheckHealth();
            Assert.Equal(AgentState.Unconfirmed, _machine.Snapshot.State);
        }
        Assert.Empty(_notices);
    }
}
