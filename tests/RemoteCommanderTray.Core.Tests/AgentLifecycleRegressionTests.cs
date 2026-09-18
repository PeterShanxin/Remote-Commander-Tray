using RemoteCommanderTray.Core;
using Xunit;

namespace RemoteCommanderTray.Core.Tests;

/// <summary>
/// Regressions for the lifecycle and privacy defects found in review of the first
/// revision. Each one failed against the implementation as it stood then.
/// </summary>
public class AgentLifecycleRegressionTests : IDisposable
{
    private static readonly TimeSpan FastSchedule = TimeSpan.FromMilliseconds(20);

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "rct-reg-" + Guid.NewGuid().ToString("N"));

    private readonly FakeAgentProcessFactory _factory = new();
    private readonly TraySettings _settings = new() { StartFailureAlertThreshold = 2 };
    private readonly List<TrayNotification> _notifications = [];
    private readonly RollingFileLog _log;
    private readonly AgentSupervisor _supervisor;

    public AgentLifecycleRegressionTests()
    {
        Directory.CreateDirectory(_folder);
        _log = new RollingFileLog(Path.Combine(_folder, "agent.log"), 256 * 1024, 1);
        _supervisor = new AgentSupervisor(
            new AgentStateMachine(),
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
    public async Task An_exit_raised_during_start_is_not_lost()
    {
        // The real process raises Exited from inside Start. The supervisor used to
        // register the generation only after Start returned, so this exit was discarded
        // and no retry was ever scheduled.
        _factory.NextExitDuringStart = 1;

        await _supervisor.StartAsync();
        await WaitUntil(() => _factory.CreatedCount >= 2);

        // The exit was noticed, so a replacement generation exists. Before the fix the
        // supervisor sat at Starting forever with a dead child and no scheduled retry.
        Assert.True(_factory.CreatedCount >= 2);
        Assert.True(_factory.Created.First().HasExited);
        Assert.NotSame(_factory.Created.First(), _factory.Latest);
    }

    [Fact]
    public async Task Output_arriving_during_start_is_not_overwritten_by_starting()
    {
        // Registering the Starting state after Start returned used to stamp over an
        // Online that the device had already reported.
        _factory.NextOutputDuringStart =
        [
            "\u2705 Device ready:",
            "   - Device Name:  SHANXINMEOWPEOW",
        ];

        await _supervisor.StartAsync();

        Assert.Equal(AgentState.Online, _supervisor.Snapshot.State);
        Assert.Equal("SHANXINMEOWPEOW", _supervisor.Snapshot.DeviceName);
    }

    [Fact]
    public async Task A_queued_exit_from_an_old_process_does_not_detach_its_replacement()
    {
        await _supervisor.StartAsync();
        var first = _factory.Latest;

        // Restart installs a second process. The first one's exit callback was queued
        // before that and, without an identity check, would detach the replacement and
        // start a third agent alongside it.
        await _supervisor.RestartAsync();
        var second = _factory.Latest;
        Assert.NotSame(first, second);

        first.Crash(1);
        await Task.Delay(200);

        Assert.Equal(2, _factory.CreatedCount);
        Assert.Same(second, _factory.Latest);
        Assert.True(_supervisor.IsRunning);
    }

    [Fact]
    public async Task Output_from_a_replaced_process_cannot_drive_the_icon()
    {
        await _supervisor.StartAsync();
        var first = _factory.Latest;
        await _supervisor.RestartAsync();

        _factory.Latest.Emit("\u2705 Device ready:");
        Assert.Equal(AgentState.Online, _supervisor.Snapshot.State);

        first.Emit("\u274C Device startup failed: stale");

        Assert.Equal(AgentState.Online, _supervisor.Snapshot.State);
    }

    [Fact]
    public async Task A_lost_remote_session_notifies_and_waits_for_user_sign_in()
    {
        await _supervisor.StartAsync();
        var agent = _factory.Latest;
        agent.Emit("\u2705 Device ready:");
        Assert.Equal(AgentState.Online, _supervisor.Snapshot.State);

        // The official CLI prints this, stops its heartbeat, and stays alive. There is no
        // exit to recover from, so the tray has to act on the message itself.
        agent.Emit("\n\u26A0\uFE0F  Remote session expired and could not be renewed.");

        await WaitUntil(() => agent.StopRequested);

        Assert.Single(_factory.Created);
        Assert.Equal(AgentState.AuthenticationRequired, _supervisor.Snapshot.State);
        Assert.Contains(_notifications, n => n.Kind == NotificationKind.AuthenticationRequired);
        Assert.True(agent.StopRequested);
    }

    [Fact]
    public async Task A_repeated_session_loss_notifies_only_once_per_generation()
    {
        await _supervisor.StartAsync();
        var agent = _factory.Latest;
        agent.Emit("\u2705 Device ready:");

        agent.Emit("\u26A0\uFE0F  Remote session expired and could not be renewed.");
        agent.Emit("\u26A0\uFE0F  Remote session expired and could not be renewed.");

        await Task.Delay(150);

        Assert.Single(_notifications, n => n.Kind == NotificationKind.AuthenticationRequired);
    }

    [Fact]
    public async Task Tool_call_payloads_never_reach_the_log()
    {
        await _supervisor.StartAsync();
        var agent = _factory.Latest;

        // How 0.2.51 logs a completed tool call: the serialized result lands on its own
        // line, with the inner JSON backslash-escaped by JSON.stringify.
        agent.Emit("\U0001F527 Received tool call 7: read_file {\"path\":\"C:\\\\secrets.json\"} metadata: {}");
        agent.Emit("\u2705 Tool call read_file completed:");
        agent.Emit(" {\"content\":[{\"type\":\"text\",\"text\":\"{\\\"refresh_token\\\":\\\"opaque_secret_value_1234\\\"}\"}]}");

        var logged = string.Join('\n', _log.Tail(50));

        Assert.DoesNotContain("opaque_secret_value_1234", logged);
        Assert.DoesNotContain("C:\\secrets.json", logged);
        Assert.Contains("Tool activity", logged);
        Assert.Contains("tool result omitted", logged);
    }

    [Fact]
    public async Task Status_lines_are_logged_as_safe_canonical_events()
    {
        await _supervisor.StartAsync();
        _factory.Latest.Emit("\u23F3 Connecting to Remote MCP https://mcp.desktopcommander.app");

        Assert.Contains(
            _log.Tail(50),
            line => line.Contains("Connecting to Remote MCP", StringComparison.Ordinal));
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
}

public class AgentLogPolicyTests
{
    [Fact]
    public void Keeps_a_recognized_status_line()
    {
        const string line = "\u2705 Device ready:";
        var signal = new AgentOutputParser().Parse(line);

        Assert.Equal("Device ready.", AgentLogPolicy.Sanitize(line, signal));
    }

    [Fact]
    public void Reduces_a_tool_call_to_its_name()
    {
        const string line = "\U0001F527 Received tool call 7: read_file {\"path\":\"C:\\\\secrets\"} metadata: {}";
        var signal = new AgentOutputParser().Parse(line);

        Assert.Equal("Tool activity (arguments and results omitted).", AgentLogPolicy.Sanitize(line, signal));
    }

    [Fact]
    public void Refuses_a_tool_name_that_is_not_an_identifier()
    {
        const string line = "\U0001F527 Received tool call 7: \"injected text\" {} metadata: {}";
        var signal = new AgentOutputParser().Parse(line);

        Assert.Equal("Tool activity (arguments and results omitted).", AgentLogPolicy.Sanitize(line, signal));
    }

    [Fact]
    public void Drops_an_unrecognized_line_that_could_be_serialized_data()
    {
        const string line = "{\"content\":\"anything at all\"}";
        var signal = new AgentOutputParser().Parse(line);

        Assert.StartsWith("<tool result omitted", AgentLogPolicy.Sanitize(line, signal));
    }

    [Fact]
    public void Omits_even_plain_unrecognized_messages()
    {
        const string line = "Failed to mark device offline: network unreachable";
        var signal = new AgentOutputParser().Parse(line);

        Assert.StartsWith("<agent output omitted", AgentLogPolicy.Sanitize(line, signal));
    }

    [Fact]
    public void Drops_an_over_long_unrecognized_line()
    {
        var line = new string('x', 200_000);

        Assert.StartsWith("<agent output omitted", AgentLogPolicy.Sanitize(line, AgentSignal.None));
    }
}

public class EscapedSecretRedactionTests
{
    [Fact]
    public void Masks_a_credential_inside_a_json_stringified_payload()
    {
        // JSON.stringify of a nested object escapes the inner quotes, which the first
        // version of the pattern did not allow for.
        const string line = "{\"text\":\"{\\\"refresh_token\\\":\\\"opaque_secret_value_1234\\\"}\"}";

        Assert.DoesNotContain("opaque_secret_value_1234", SecretRedactor.Redact(line));
    }

    [Theory]
    [InlineData("\\\"access_token\\\": \\\"abcdef0123456789\\\"")]
    [InlineData("secret=abcdef0123456789")]
    [InlineData("authorization: abcdef0123456789")]
    public void Masks_escaped_and_extra_key_forms(string input)
        => Assert.DoesNotContain("abcdef0123456789", SecretRedactor.Redact(input));
}

public class StartupApprovalTests
{
    [Fact]
    public void No_run_value_means_not_registered()
        => Assert.Equal(StartupState.NotRegistered, StartupApproval.Resolve(false, null));

    [Fact]
    public void A_run_value_with_no_windows_record_is_enabled()
        => Assert.Equal(StartupState.Enabled, StartupApproval.Resolve(true, null));

    [Theory]
    [InlineData(0x02)]
    [InlineData(0x06)]
    public void Windows_records_an_enabled_entry_with_bit_zero_clear(byte first)
        => Assert.Equal(
            StartupState.Enabled,
            StartupApproval.Resolve(true, [first, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));

    [Theory]
    [InlineData(0x03)]
    [InlineData(0x07)]
    public void Windows_records_a_disabled_entry_with_bit_zero_set(byte first)
        => Assert.Equal(
            StartupState.DisabledByWindows,
            StartupApproval.Resolve(true, [first, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));

    [Fact]
    public void An_empty_record_is_not_treated_as_disabled()
        => Assert.False(StartupApproval.IsDisabledByWindows([]));
}

/// <summary>
/// The sign-in latch has to outlive the process it was raised for. The expired CLI stays
/// alive, but not indefinitely, and an exit that cleared the latch would restart an agent
/// with no credentials - which makes the official CLI open a browser on its own.
/// </summary>
public class SessionLatchSurvivesExitTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "rct-latch-" + Guid.NewGuid().ToString("N"));

    private readonly FakeAgentProcessFactory _factory = new();
    private readonly TraySettings _settings = new();
    private readonly RollingFileLog _log;
    private readonly AgentSupervisor _supervisor;

    public SessionLatchSurvivesExitTests()
    {
        Directory.CreateDirectory(_folder);
        _log = new RollingFileLog(Path.Combine(_folder, "agent.log"), 64 * 1024, 1);
        _supervisor = new AgentSupervisor(
            new AgentStateMachine(),
            _factory,
            new AgentCommandResolver(new InstalledAgentEnvironment()),
            () => _settings,
            _log,
            backoff: new RestartBackoff([TimeSpan.FromMilliseconds(20)]));
    }

    [Fact]
    public async Task An_exit_after_session_loss_does_not_restart_into_an_unprompted_sign_in()
    {
        await _supervisor.StartAsync();
        var agent = _factory.Latest;
        agent.Emit("✅ Device ready:");

        agent.Emit("⚠️  Remote session expired and could not be renewed.");
        Assert.True(_supervisor.Snapshot.RequiresReauthentication);

        // The still-alive CLI eventually dies on its own.
        agent.Crash(1);
        await Task.Delay(250);

        Assert.Equal(1, _factory.CreatedCount);
        Assert.True(_supervisor.Snapshot.RequiresReauthentication);
        Assert.Equal(AgentState.AuthenticationRequired, _supervisor.Snapshot.State);
    }

    [Fact]
    public async Task A_user_stop_clears_the_latch()
    {
        await _supervisor.StartAsync();
        var agent = _factory.Latest;
        agent.Emit("⚠️  Remote session expired and could not be renewed.");
        Assert.True(_supervisor.Snapshot.RequiresReauthentication);

        await _supervisor.StopAsync();

        Assert.False(_supervisor.Snapshot.RequiresReauthentication);
        Assert.Equal(AgentState.Stopped, _supervisor.Snapshot.State);
    }

    [Fact]
    public async Task Starting_a_new_generation_clears_the_latch()
    {
        await _supervisor.StartAsync();
        _factory.Latest.Emit("⚠️  Remote session expired and could not be renewed.");
        Assert.True(_supervisor.Snapshot.RequiresReauthentication);

        await _supervisor.RestartAsync();

        Assert.False(_supervisor.Snapshot.RequiresReauthentication);
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
}
