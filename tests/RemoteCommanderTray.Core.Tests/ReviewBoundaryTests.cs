using System.Collections.Concurrent;
using System.Reflection;
using RemoteCommanderTray.Core;
using Xunit;
namespace RemoteCommanderTray.Core.Tests;

public sealed class ReviewBoundaryTests
{
    [Fact] public async Task Queued_old_exit_cannot_dispose_the_new_generation()
    {
        await using var h = new Harness(); await h.Supervisor.StartAsync();
        var first = h.Factory.Last;
        var mutex = Gate(h.Supervisor); await mutex.WaitAsync();
        Task restart;
        try { restart = h.Supervisor.RestartAsync(); first.Crash(); await Task.Delay(100); }
        finally { mutex.Release(); }
        await restart; await Task.Delay(150);
        Assert.Equal(2, h.Factory.Processes.Count);
        Assert.True(first.Disposed); Assert.False(h.Factory.Last.Disposed);
        Assert.True(h.Supervisor.IsRunning);
    }
    [Fact] public async Task Queued_old_session_loss_cannot_stop_a_replacement()
    {
        await using var h = new Harness(); await h.Supervisor.StartAsync();
        var first = h.Factory.Last; var mutex = Gate(h.Supervisor); await mutex.WaitAsync();
        Task restart;
        try { restart = h.Supervisor.RestartAsync(); first.Emit("Remote session expired and could not be renewed."); await Task.Delay(100); }
        finally { mutex.Release(); }
        await restart; h.Factory.Last.Emit("Device ready:"); await Task.Delay(100);
        Assert.Equal(2, h.Factory.Processes.Count); Assert.True(h.Supervisor.IsRunning);
        Assert.Equal(AgentState.Online, h.Supervisor.Snapshot.State);
    }
    [Fact] public async Task Already_captured_output_callback_cannot_mutate_new_state_or_logs()
    {
        await using var h = new Harness(); await h.Supervisor.StartAsync();
        var first = h.Factory.Last; var callback = first.CaptureOutput();
        await h.Supervisor.RestartAsync(); h.Factory.Last.Emit("Device ready:");
        callback?.Invoke(first, new("Device startup failed: old-generation-opaque", false));
        Assert.Equal(AgentState.Online, h.Supervisor.Snapshot.State);
        Assert.DoesNotContain("old-generation-opaque", string.Join('\n', h.Log.Tail(100)));
    }
    [Fact] public async Task Start_with_dead_root_drains_it_before_creating_a_replacement()
    {
        await using var h = new Harness(); await h.Supervisor.StartAsync();
        var first = h.Factory.Last; var mutex = Gate(h.Supervisor); await mutex.WaitAsync();
        Task start;
        try { start = h.Supervisor.StartAsync(); first.Crash(); await Task.Delay(80); }
        finally { mutex.Release(); }
        await start; await Task.Delay(80);
        Assert.True(first.Disposed); Assert.Equal(2, h.Factory.Processes.Count);
        Assert.Equal(1, h.Factory.Processes.Count(p => !p.HasExited));
    }
    [Fact] public async Task Factory_creation_failure_is_recovered_without_leaking_a_retry_task()
    {
        await using var h = new Harness(); h.Factory.FailNextCreate = true;
        await h.Supervisor.StartAsync(); await Until(() => h.Supervisor.IsRunning);
        Assert.Single(h.Factory.Processes);
    }
    [Fact] public async Task Failed_logout_does_not_claim_success_or_launch_an_agent()
    {
        await using var h = new Harness(); await h.Supervisor.StartAsync();
        h.Factory.Logout = new(1, "arbitrary-secret-from-logout");
        await h.Supervisor.ReauthenticateAsync(); await Task.Delay(100);
        Assert.Single(h.Factory.Processes); Assert.False(h.Supervisor.IsRunning);
        Assert.Equal(AgentState.AuthenticationRequired, h.Supervisor.Snapshot.State);
        Assert.DoesNotContain(h.Notices, n => n.Kind == NotificationKind.ReauthenticationStarted);
        Assert.DoesNotContain("arbitrary-secret-from-logout", string.Join('\n', h.Log.Tail(100)));
    }
    [Fact] public async Task Terminal_auth_loss_is_one_actionable_notice_and_never_a_browser_loop()
    {
        await using var h = new Harness(); await h.Supervisor.StartAsync();
        var first = h.Factory.Last; first.Emit("Device ready:");
        first.Emit("Remote session expired and could not be renewed.");
        first.Emit("Remote session expired and could not be renewed.");
        await Until(() => first.Disposed); await Task.Delay(150);
        Assert.Single(h.Factory.Processes); Assert.Single(h.Notices, n => n.Kind == NotificationKind.AuthenticationRequired);
        Assert.Equal(AgentState.AuthenticationRequired, h.Supervisor.Snapshot.State);
        Assert.False(h.Supervisor.Snapshot.ProcessRunning); Assert.Null(h.Supervisor.Snapshot.NextRestartUtc);
    }
    [Fact] public async Task Auth_timeout_does_not_automatically_restart_the_browser_flow()
    {
        await using var h = new Harness(); await h.Supervisor.StartAsync();
        h.Factory.Last.Emit("Starting device authorization flow..."); h.Factory.Last.Crash();
        await Until(() => h.Factory.Last.Disposed); await Task.Delay(150);
        Assert.Single(h.Factory.Processes); Assert.Equal(AgentState.AuthenticationRequired, h.Supervisor.Snapshot.State);
    }
    [Fact] public async Task Stop_cancels_a_scheduled_retry_and_clears_its_countdown()
    {
        await using var h = new Harness(TimeSpan.FromSeconds(1)); await h.Supervisor.StartAsync();
        h.Factory.Last.Crash(); await Until(() => h.Supervisor.Snapshot.NextRestartUtc is not null);
        await h.Supervisor.StopAsync();
        Assert.Null(h.Supervisor.Snapshot.NextRestartUtc); Assert.False(h.Supervisor.Snapshot.AgentWanted);
    }
    [Fact] public async Task Disposal_is_idempotent_and_queued_commands_cannot_restart_after_exit()
    {
        var h = new Harness(); await h.Supervisor.StartAsync();
        var commands = Enumerable.Range(0, 25).Select(_ => h.Supervisor.RestartAsync()).ToArray();
        await h.Supervisor.DisposeAsync(); await Task.WhenAll(commands);
        await h.Supervisor.StartAsync(); await h.Supervisor.DisposeAsync();
        Assert.All(h.Factory.Processes, p => Assert.True(p.Disposed)); Assert.False(h.Supervisor.IsRunning);
        await h.DisposeAsync();
    }
    [Fact] public async Task Multiline_stdout_prompt_is_not_consumed_by_an_interleaved_stderr_line()
    {
        await using var h = new Harness(); await h.Supervisor.StartAsync();
        var p = h.Factory.Last;
        p.Emit("Starting device authorization flow..."); p.Emit("1. Open this URL in your browser:");
        p.Emit("https://example.invalid/not-the-sign-in-page", true);
        p.Emit("https://mcp.desktopcommander.app/device/verify");
        p.Emit("2. Enter this code when prompted:"); p.Emit("ABCD-EFGH");
        Assert.Equal("https://mcp.desktopcommander.app/device/verify", h.Supervisor.Snapshot.VerificationUri);
        Assert.Equal("ABCD-EFGH", h.Supervisor.Snapshot.UserCode);
    }
    [Theory]
    [InlineData("opaque-private-value")]
    [InlineData("Device startup failed: opaque-private-value")]
    [InlineData("Device ready: opaque-private-value")]
    [InlineData("{\"Device ready\":\"opaque-private-value\"}")]
    public void Ordinary_logs_do_not_preserve_unknown_text_or_status_suffixes(string line)
    {
        var result = AgentLogPolicy.Sanitize(line, new AgentOutputParser().Parse(line));
        Assert.DoesNotContain("opaque-private-value", result);
    }
    [Fact] public void Diagnostics_excludes_old_log_tails_and_all_arbitrary_cli_fields()
    {
        const string secret = "opaque-private-value";
        var snapshot = AgentSnapshot.Initial with { LastError = secret, DeviceName = secret, UserEmail = secret, VerificationUri = secret };
        var report = DiagnosticsReport.Build(snapshot, new("0.1", "Windows", "ARM64", secret, secret, StartupState.Enabled), [secret]);
        Assert.DoesNotContain(secret, report);
    }
    [Theory] [InlineData(0)] [InlineData(4)] [InlineData(255)]
    public void Unknown_approval_values_do_not_claim_startup_enabled(byte value)
        => Assert.Equal(StartupState.Unknown, StartupApproval.Resolve(true, [value, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
    [Fact] public void Malformed_approval_is_unknown()
        => Assert.Equal(StartupState.Unknown, StartupApproval.Resolve(true, [2]));

    private static SemaphoreSlim Gate(AgentSupervisor supervisor)
        => (SemaphoreSlim)typeof(AgentSupervisor).GetField("_mutex", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(supervisor)!;
    private static async Task Until(Func<bool> predicate)
    {
        for (var i = 0; i < 300 && !predicate(); i++) await Task.Delay(10);
        Assert.True(predicate(), "Expected lifecycle transition did not occur.");
    }
    private sealed class Probe : IAgentProcess
    {
        public int? ProcessId => 42;
        public bool HasExited { get; private set; }
        public bool Disposed { get; private set; }
        public event EventHandler<AgentOutputLine>? OutputReceived;
        public event EventHandler<int?>? Exited;
        public void Start() { }
        public void Emit(string line, bool error = false) => OutputReceived?.Invoke(this, new(line, error));
        public EventHandler<AgentOutputLine>? CaptureOutput() => OutputReceived;
        public void Crash() { HasExited = true; Exited?.Invoke(this, 1); }
        public Task StopAsync(TimeSpan grace, CancellationToken cancellationToken = default) { HasExited = true; return Task.CompletedTask; }
        public void Dispose() { HasExited = true; Disposed = true; }
    }
    private sealed class Factory : IAgentProcessFactory
    {
        public ConcurrentQueue<Probe> Processes { get; } = new();
        public Probe Last => Processes.Last();
        public bool FailNextCreate;
        public AgentCommandResult Logout = new(0, "done");
        public IAgentProcess Create(AgentLaunchSpec spec)
        {
            if (FailNextCreate) { FailNextCreate = false; throw new InvalidOperationException(); }
            var probe = new Probe(); Processes.Enqueue(probe); return probe;
        }
        public Task<AgentCommandResult> RunOnceAsync(AgentLaunchSpec spec, TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult(Logout);
    }
    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rct-boundary-" + Guid.NewGuid());
        public Factory Factory { get; } = new();
        public RollingFileLog Log { get; }
        public AgentSupervisor Supervisor { get; }
        public ConcurrentQueue<TrayNotification> Notices { get; } = new();
        public Harness(TimeSpan? retry = null)
        {
            Log = new(Path.Combine(_root, "safe.log"), 65536, 1);
            Supervisor = new(new(), Factory, new(new InstalledAgentEnvironment()), () => new(), Log,
                backoff: new RestartBackoff([retry ?? TimeSpan.FromMilliseconds(20)]));
            Supervisor.Notification += (_, n) => Notices.Enqueue(n);
        }
        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisposeAsync(); Log.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
