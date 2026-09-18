using RemoteCommanderTray.Core;
using Xunit;

namespace RemoteCommanderTray.Core.Tests;

public class RestartBackoffTests
{
    [Fact]
    public void Follows_the_five_fifteen_thirty_sixty_schedule_then_holds()
    {
        var backoff = new RestartBackoff();

        Assert.Equal(TimeSpan.FromSeconds(5), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(15), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(60), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(60), backoff.NextDelay());
        Assert.Equal(5, backoff.Attempt);
    }

    [Fact]
    public void Reset_starts_the_schedule_again()
    {
        var backoff = new RestartBackoff();
        backoff.NextDelay();
        backoff.NextDelay();
        backoff.Reset();

        Assert.Equal(0, backoff.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(5), backoff.NextDelay());
    }
}

public class SecretRedactorTests
{
    [Fact]
    public void Masks_a_jwt()
    {
        var redacted = SecretRedactor.Redact(
            "session: eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", redacted);
        Assert.Contains("[redacted]", redacted);
    }

    [Theory]
    [InlineData("access_token=abcdef0123456789")]
    [InlineData("\"refresh_token\": \"abcdef0123456789\"")]
    [InlineData("apikey: abcdef0123456789")]
    [InlineData("Authorization: Bearer abcdef0123456789")]
    public void Masks_keyed_secrets(string input)
        => Assert.DoesNotContain("abcdef0123456789", SecretRedactor.Redact(input));

    [Fact]
    public void Leaves_ordinary_status_lines_alone()
    {
        const string line = "Device ready: SHANXINMEOWPEOW";
        Assert.Equal(line, SecretRedactor.Redact(line));
    }
}

public class DiagnosticsReportTests
{
    [Fact]
    public void Reports_the_fields_the_spec_asks_for()
    {
        var snapshot = AgentSnapshot.Initial with
        {
            State = AgentState.Online,
            ProcessRunning = true,
            AgentWanted = true,
            DeviceName = "SHANXINMEOWPEOW",
            LastConnectedUtc = new DateTimeOffset(2026, 1, 1, 13, 52, 0, TimeSpan.Zero),
        };

        var report = DiagnosticsReport.Build(
            snapshot,
            new DiagnosticsContext("0.1.0", "Windows 11", "Arm64", "node index.js remote", @"C:\logs\agent.log", StartupState.Enabled));

        Assert.Contains("Tray:           0.1.0", report);
        Assert.Contains("State:          Online", report);
        Assert.DoesNotContain("SHANXINMEOWPEOW", report);
        Assert.Contains("Agent:          running", report);
        Assert.Contains("Restart count:  0", report);
    }

    [Fact]
    public void Redacts_anything_token_shaped_that_reached_the_log()
    {
        var report = DiagnosticsReport.Build(
            AgentSnapshot.Initial,
            new DiagnosticsContext("0.1.0", "Windows 11", "X64", "cmd", "log", StartupState.NotRegistered),
            ["12:00 [agent] access_token=abcdef0123456789"]);

        Assert.DoesNotContain("abcdef0123456789", report);
    }
}

public class SettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "rct-tests-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_folder, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void Returns_defaults_when_the_file_is_missing()
    {
        var settings = new SettingsStore(SettingsPath).Load();

        Assert.True(settings.StartAgentOnLaunch);
        Assert.Equal("https://mcp.desktopcommander.app", settings.RemoteMcpUrl);
        Assert.Null(settings.AgentExecutable);
    }

    [Fact]
    public void Round_trips()
    {
        var store = new SettingsStore(SettingsPath);
        Assert.True(store.TrySave(new TraySettings { StartAgentOnLaunch = false, StalledConnectionMinutes = 9 }, out _));

        var loaded = store.Load();
        Assert.False(loaded.StartAgentOnLaunch);
        Assert.Equal(9, loaded.StalledConnectionMinutes);
    }

    [Fact]
    public void Quarantines_a_corrupt_file_and_carries_on()
    {
        File.WriteAllText(SettingsPath, "{ this is not json");

        var settings = new SettingsStore(SettingsPath).Load();

        Assert.True(settings.StartAgentOnLaunch);
        Assert.True(File.Exists(SettingsPath + ".invalid"));
    }

    [Fact]
    public void Clamps_values_a_hand_edit_could_have_broken()
    {
        var settings = new TraySettings
        {
            StalledConnectionMinutes = 0,
            LogRetainedFiles = -4,
            RemoteMcpUrl = "javascript:alert(1)",
        }.Normalized();

        Assert.Equal(1, settings.StalledConnectionMinutes);
        Assert.Equal(0, settings.LogRetainedFiles);
        Assert.Equal("https://mcp.desktopcommander.app", settings.RemoteMcpUrl);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Test scratch space; leaking it is harmless.
        }

        GC.SuppressFinalize(this);
    }
}

public class RollingFileLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "rct-log-" + Guid.NewGuid().ToString("N"));

    public RollingFileLogTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void Rotates_once_the_file_grows_past_the_limit()
    {
        var path = Path.Combine(_folder, "agent.log");
        using var log = new RollingFileLog(path, maxBytes: 16 * 1024, retainedFiles: 2);

        var line = new string('x', 1024);
        for (var i = 0; i < 40; i++)
        {
            log.Write(LogSource.Agent, line);
        }

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".1"));
        Assert.False(File.Exists(path + ".3"));
        Assert.True(new FileInfo(path).Length <= 32 * 1024);
    }

    [Fact]
    public void Redacts_on_the_way_in()
    {
        var path = Path.Combine(_folder, "agent.log");
        using (var log = new RollingFileLog(path, 64 * 1024, 1))
        {
            log.Write(LogSource.Agent, "access_token=abcdef0123456789");
        }

        Assert.DoesNotContain("abcdef0123456789", File.ReadAllText(path));
    }

    [Fact]
    public void Tail_returns_the_most_recent_lines()
    {
        var path = Path.Combine(_folder, "agent.log");
        using var log = new RollingFileLog(path, 64 * 1024, 1);
        for (var i = 0; i < 10; i++)
        {
            log.Write(LogSource.Tray, $"line {i}");
        }

        var tail = log.Tail(3);
        Assert.Equal(3, tail.Count);
        Assert.Contains("line 9", tail[^1]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Test scratch space; leaking it is harmless.
        }

        GC.SuppressFinalize(this);
    }
}
