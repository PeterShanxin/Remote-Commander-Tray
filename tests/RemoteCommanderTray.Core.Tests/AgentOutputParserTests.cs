using RemoteCommanderTray.Core;
using Xunit;

namespace RemoteCommanderTray.Core.Tests;

/// <summary>
/// The fixtures below are copied from the output of
/// <c>@wonderwhy-er/desktop-commander</c> 0.2.51, emoji and indentation included.
/// </summary>
public class AgentOutputParserTests
{
    [Theory]
    [InlineData("\U0001F680 Starting MCP Device...", AgentSignalKind.DeviceStarting)]
    [InlineData("⏳ Connecting to Remote MCP https://mcp.desktopcommander.app", AgentSignalKind.ConnectingToRemote)]
    [InlineData("   - \U0001F50C Connected to Remote MCP", AgentSignalKind.ConnectedToRemote)]
    [InlineData("   - ✅ Session restored", AgentSignalKind.SessionRestored)]
    [InlineData("\n\U0001F510 Authenticating with Remote MCP server...", AgentSignalKind.AuthenticationStarted)]
    [InlineData("\U0001F510 Starting device authorization flow...", AgentSignalKind.AuthenticationStarted)]
    [InlineData("   - ✅ Authorization successful!", AgentSignalKind.AuthorizationSucceeded)]
    [InlineData("✅ Device ready:", AgentSignalKind.DeviceReady)]
    [InlineData("\U0001F50C Device marked as online", AgentSignalKind.DeviceOnline)]
    [InlineData("\U0001F50C Device marked as offline", AgentSignalKind.DeviceOffline)]
    [InlineData("✅ Channel subscribed", AgentSignalKind.ChannelSubscribed)]
    [InlineData("❌ Channel error: websocket closed - CLOSED", AgentSignalKind.ChannelDisrupted)]
    [InlineData("⏱️ Channel subscription timed out, Reconnecting...", AgentSignalKind.ChannelDisrupted)]
    [InlineData("\U0001F504 Recreating channel... (attempt 2)", AgentSignalKind.ChannelDisrupted)]
    [InlineData(" - ❌ Device startup failed: fetch failed", AgentSignalKind.StartupFailed)]
    [InlineData("\n⚠️  Remote session expired and could not be renewed.", AgentSignalKind.SessionExpired)]
    [InlineData("\n\U0001F6D1 Shutting down device...", AgentSignalKind.ShuttingDown)]
    public void Recognizes_official_cli_lines(string line, AgentSignalKind expected)
        => Assert.Equal(expected, new AgentOutputParser().Parse(line).Kind);

    [Fact]
    public void Reads_device_identity_from_the_ready_block()
    {
        var parser = new AgentOutputParser();

        Assert.Equal(AgentSignalKind.DeviceReady, parser.Parse("✅ Device ready:").Kind);

        var user = parser.Parse("   - User:         someone@example.com");
        Assert.Equal(AgentSignalKind.UserEmail, user.Kind);
        Assert.Equal("someone@example.com", user.Value);

        var id = parser.Parse("   - Device ID:    9f1c2b64-1a11-4f0e-8a0f-2f2f3c9b1234");
        Assert.Equal(AgentSignalKind.DeviceId, id.Kind);
        Assert.Equal("9f1c2b64-1a11-4f0e-8a0f-2f2f3c9b1234", id.Value);

        var name = parser.Parse("   - Device Name:  SHANXINMEOWPEOW");
        Assert.Equal(AgentSignalKind.DeviceName, name.Kind);
        Assert.Equal("SHANXINMEOWPEOW", name.Value);
    }

    [Fact]
    public void Device_id_assigned_line_is_not_mistaken_for_the_ready_block()
    {
        var signal = new AgentOutputParser().Parse("   - ✅ Device ID assigned: abc-123");
        Assert.Equal(AgentSignalKind.DeviceId, signal.Kind);
        Assert.Equal("abc-123", signal.Value);
    }

    [Fact]
    public void Captures_the_sign_in_url_and_code_that_follow_their_labels()
    {
        var parser = new AgentOutputParser();

        Assert.Equal(AgentSignalKind.None, parser.Parse("   1. Verify this device in your browser:").Kind);

        var uri = parser.Parse("      https://desktopcommander.app/device?code=WDJB-MJHT");
        Assert.Equal(AgentSignalKind.VerificationUri, uri.Kind);
        Assert.Equal("https://desktopcommander.app/device?code=WDJB-MJHT", uri.Value);

        Assert.Equal(AgentSignalKind.None, parser.Parse("   2. Make sure the code matches:").Kind);

        var code = parser.Parse("      WDJB-MJHT");
        Assert.Equal(AgentSignalKind.UserCode, code.Kind);
        Assert.Equal("WDJB-MJHT", code.Value);
    }

    [Fact]
    public void Falls_back_to_the_please_visit_line_when_the_browser_did_not_open()
    {
        var parser = new AgentOutputParser();
        parser.Parse("   - Could not open browser automatically.");

        var uri = parser.Parse("   - Please visit: https://desktopcommander.app/device");
        Assert.Equal(AgentSignalKind.VerificationUri, uri.Kind);
        Assert.Equal("https://desktopcommander.app/device", uri.Value);
    }

    [Fact]
    public void Reset_forgets_a_half_seen_authorization_prompt()
    {
        var parser = new AgentOutputParser();
        parser.Parse("   1. Verify this device in your browser:");
        parser.Reset();

        Assert.Equal(AgentSignalKind.None, parser.Parse("      https://example.com/device").Kind);
    }

    [Fact]
    public void A_logged_tool_call_cannot_forge_a_status_change()
    {
        // Tool arguments are attacker-controllable text that lands in the same stream.
        var line = "\U0001F527 Received tool call 42: write_file "
                   + "{\"content\":\"Device marked as online\"} metadata: {}";

        var signal = new AgentOutputParser().Parse(line);

        Assert.Equal(AgentSignalKind.ToolCall, signal.Kind);
        Assert.Equal("write_file", signal.Value);
    }

    [Fact]
    public void The_line_after_a_completed_tool_call_is_treated_as_payload()
    {
        var parser = new AgentOutputParser();

        Assert.Equal(AgentSignalKind.ToolCall, parser.Parse("\u2705 Tool call read_file completed:").Kind);

        // The CLI emits the serialized result on the next line, with nothing to identify
        // it by, so position is the only thing that can classify it.
        var payload = parser.Parse(" {\"content\":\"Device marked as online\"}");
        Assert.Equal(AgentSignalKind.ToolPayload, payload.Kind);

        // ...and only that one line.
        Assert.Equal(AgentSignalKind.DeviceOnline, parser.Parse("\U0001F50C Device marked as online").Kind);
    }

    [Fact]
    public void Ignores_blank_and_decoration_only_lines()
    {
        var parser = new AgentOutputParser();
        Assert.Equal(AgentSignalKind.None, parser.Parse("").Kind);
        Assert.Equal(AgentSignalKind.None, parser.Parse("   ").Kind);
        Assert.Equal(AgentSignalKind.None, parser.Parse("   ---   ").Kind);
    }

    [Fact]
    public void Strips_ansi_colour_codes()
        => Assert.Equal(
            AgentSignalKind.DeviceStarting,
            new AgentOutputParser().Parse("[32m\U0001F680 Starting MCP Device...[0m").Kind);
}
