using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using RemoteCommanderTray.Core;
using Xunit;

namespace RemoteCommanderTray.Core.Tests;

public sealed class ReviewBoundaryTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rct-boundary-" + Guid.NewGuid());
    private readonly FakeAgentProcessFactory _factory = new();
    private readonly ConcurrentQueue<TrayNotification> _notices = new();
    private readonly RollingFileLog _log;
    private readonly AgentSupervisor _supervisor;
    private SemaphoreSlim Gate => (SemaphoreSlim)typeof(AgentSupervisor)
        .GetField("_mutex", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_supervisor)!;

    public ReviewBoundaryTests()
    {
        _log = new RollingFileLog(Path.Combine(_folder, "agent.log"), 65536, 1);
        _supervisor = new AgentSupervisor(new(), _factory, new(new InstalledAgentEnvironment()),
            () => new TraySettings(), _log, backoff: new RestartBackoff([TimeSpan.FromMilliseconds(20)]));
        _supervisor.Notification += (_, n) => _notices.Enqueue(n);
    }
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        foreach (var process in _factory.Created) process.StopFailure = null;
        await _supervisor.DisposeAsync();
        _log.Dispose();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }
    private static async Task Until(Func<bool> condition)
    {
        var time = Stopwatch.StartNew();
        while (!condition() && time.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.True(condition(), "Expected asynchronous condition did not occur.");
    }

    [Fact]
    public async Task Old_exit_already_queued_behind_restart_cannot_touch_new_generation()
    {
        await _supervisor.StartAsync();
        var old = _factory.Latest;
        await Gate.WaitAsync();
        Task restart;
        try
        {
            restart = _supervisor.RestartAsync(); // Queues first.
            old.Crash(); // The subscribed handler queues behind it, before unsubscribe.
            old.Emit("Device startup failed: stale_callback_secret");
        }
        finally { Gate.Release(); }
        await restart;
        _factory.Latest.Emit("Device ready:");
        await Until(() => _supervisor.Snapshot.State == AgentState.Online);
        await Task.Delay(150);
        Assert.Equal(2, _factory.CreatedCount);
        Assert.True(_supervisor.IsRunning);
        Assert.DoesNotContain("stale_callback_secret", string.Join('\n', _log.Tail(100)));
    }

    [Fact]
    public async Task Old_auth_output_already_queued_cannot_force_replacement_to_sign_in()
    {
        await _supervisor.StartAsync();
        var old = _factory.Latest;
        await Gate.WaitAsync();
        Task restart;
        try
        {
            restart = _supervisor.RestartAsync();
            old.Emit("Remote session expired and could not be renewed.");
        }
        finally { Gate.Release(); }
        await restart;
        _factory.Latest.Emit("Device ready:");
        await Until(() => _supervisor.Snapshot.State == AgentState.Online);
        Assert.Empty(_notices);
        Assert.False(_supervisor.Snapshot.RequiresReauthentication);
        Assert.Equal(2, _factory.CreatedCount);
    }

    [Fact]
    public async Task Terminal_auth_loss_latches_until_user_reauthentication_and_notifies_once()
    {
        await _supervisor.StartAsync();
        var old = _factory.Latest;
        old.Emit("Device ready:");
        old.Emit("Remote session expired and could not be renewed.");
        old.Emit("Channel subscribed");
        old.Emit("Device ready:");
        old.Emit("Remote session expired and could not be renewed.");
        Assert.Equal(AgentState.AuthenticationRequired, _supervisor.Snapshot.State);
        Assert.True(_supervisor.Snapshot.RequiresReauthentication);
        Assert.Single(_notices, n => n.Kind == NotificationKind.AuthenticationRequired);
        Assert.Equal(1, _factory.CreatedCount);
        await _supervisor.ReauthenticateAsync();
        Assert.True(old.StopRequested);
        Assert.Single(_factory.OneShotCommands);
        _factory.Latest.Emit("Device ready:");
        Assert.Equal(AgentState.Online, _supervisor.Snapshot.State);
        Assert.False(_supervisor.Snapshot.RequiresReauthentication);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_throwing_logout_never_starts_a_replacement(bool throws)
    {
        await _supervisor.StartAsync();
        _factory.LogoutResult = new(1, "short_opaque_credential");
        if (throws) _factory.LogoutFailure = new IOException("short_opaque_credential");
        await _supervisor.ReauthenticateAsync();
        Assert.Equal(1, _factory.CreatedCount);
        Assert.False(_supervisor.Snapshot.ProcessRunning);
        Assert.False(_supervisor.Snapshot.AgentWanted);
        Assert.Equal(AgentState.Error, _supervisor.Snapshot.State);
        Assert.DoesNotContain("short_opaque_credential", string.Join('\n', _log.Tail(100)));
    }

    [Fact]
    public async Task Cleanup_failure_blocks_restart_until_explicit_stop_succeeds()
    {
        await _supervisor.StartAsync();
        var old = _factory.Latest;
        old.StopFailure = new IOException("cannot drain");
        await Assert.ThrowsAsync<IOException>(() => _supervisor.RestartAsync());
        old.Emit("Device ready:");
        Assert.Equal(AgentState.Error, _supervisor.Snapshot.State);
        Assert.Equal(1, _factory.CreatedCount);
        old.StopFailure = null;
        await _supervisor.StopAsync();
        await _supervisor.StartAsync();
        Assert.Equal(2, _factory.CreatedCount);
    }

    [Fact]
    public async Task Factory_creation_failure_is_retried_without_losing_supervision()
    {
        _factory.CreateFailure = new IOException("synthetic creation failure");
        await _supervisor.StartAsync();
        Assert.NotNull(_supervisor.Snapshot.NextRestartUtc);
        _factory.CreateFailure = null;
        await Until(() => _factory.CreatedCount == 1);
        Assert.True(_supervisor.IsRunning);
    }

    [Fact]
    public async Task Pending_callbacks_after_disposal_are_harmless_and_agent_is_stopped()
    {
        await _supervisor.StartAsync();
        var old = _factory.Latest;
        await Gate.WaitAsync();
        var disposing = _supervisor.DisposeAsync().AsTask();
        old.Emit("Device ready:"); old.Crash();
        Gate.Release();
        await disposing;
        Assert.True(old.StopRequested);
        Assert.False(_supervisor.IsRunning);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _supervisor.StartAsync());
    }

    [Theory]
    [InlineData("short_opaque_credential")]
    [InlineData("Device startup failed: short_opaque_credential")]
    [InlineData("Device Name: short_opaque_credential")]
    [InlineData("{\"text\":\"{\\\"refresh_token\\\":\\\"short_opaque_credential\\\"}\"}")]
    public async Task Operational_log_and_clipboard_never_include_arbitrary_cli_text(string line)
    {
        await _supervisor.StartAsync(); _factory.Latest.Emit(line);
        var logged = string.Join('\n', _log.Tail(100));
        Assert.DoesNotContain("short_opaque_credential", logged);
        var report = DiagnosticsReport.Build(_supervisor.Snapshot with
        {
            DeviceName = "short_opaque_credential", LastError = "short_opaque_credential",
        }, new("0.1.0", "Windows", "Arm64", "short_opaque_credential", "short_opaque_credential", StartupState.Unknown),
            ["legacy raw log: short_opaque_credential"]);
        Assert.DoesNotContain("short_opaque_credential", report);
    }

    [Fact]
    public void Unrecognized_startup_data_is_never_assumed_enabled()
    {
        Assert.Equal(StartupState.Unknown, StartupApproval.Resolve(true, new byte[] { 3 }));
        Assert.Equal(StartupState.Unknown, StartupApproval.Resolve(true, new byte[12]));
        Assert.Equal(StartupState.Unknown, StartupApproval.Resolve(true, "malformed"));
    }
}
