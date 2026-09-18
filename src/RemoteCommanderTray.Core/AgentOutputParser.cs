using System.Text;
using System.Text.RegularExpressions;

namespace RemoteCommanderTray.Core;

/// <summary>
/// Turns lines printed by <c>desktop-commander remote</c> into <see cref="AgentSignal"/>s.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place that knows what the official CLI prints. It holds a tiny
/// amount of state because the device authorization flow prints its URL and its user
/// code on the line *after* their labels; nothing else here is stateful, and it never
/// decides what a signal means for the tray - that is <see cref="AgentStateMachine"/>'s job.
/// </para>
/// <para>
/// Matching is done on a normalized line (ANSI codes, emoji, bullets and list markers
/// stripped) with <c>StartsWith</c> rather than <c>Contains</c>, so that a logged tool
/// call carrying arbitrary text cannot forge a status change.
/// </para>
/// </remarks>
public sealed partial class AgentOutputParser
{
    private bool _awaitingVerificationUri;
    private bool _awaitingUserCode;

    /// <summary>Forget any half-seen authorization prompt. Call when a new process starts.</summary>
    public void Reset()
    {
        _awaitingVerificationUri = false;
        _awaitingUserCode = false;
    }

    /// <summary>Classify a single line of agent output.</summary>
    public AgentSignal Parse(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return AgentSignal.None;
        }

        var line = Normalize(rawLine);
        if (line.Length == 0)
        {
            return AgentSignal.None;
        }

        if (_awaitingVerificationUri && TryReadUri(line, out var pendingUri))
        {
            _awaitingVerificationUri = false;
            return new AgentSignal(AgentSignalKind.VerificationUri, pendingUri);
        }

        if (_awaitingUserCode && LooksLikeUserCode(line))
        {
            _awaitingUserCode = false;
            return new AgentSignal(AgentSignalKind.UserCode, line);
        }

        // Device lifecycle.
        if (line.StartsWith("Starting MCP Device", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.DeviceStarting);
        }

        if (line.StartsWith("Connecting to Remote MCP", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.ConnectingToRemote, Rest(line, "Connecting to Remote MCP"));
        }

        if (line.StartsWith("Connected to Remote MCP", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.ConnectedToRemote);
        }

        if (line.StartsWith("Session restored", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Remote session restored", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Found persisted session", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.SessionRestored);
        }

        // Authentication.
        if (line.StartsWith("Authenticating with Remote MCP", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Starting device authorization flow", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Please complete authentication", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.AuthenticationStarted);
        }

        if (line.StartsWith("Verify this device in your browser", StringComparison.OrdinalIgnoreCase))
        {
            _awaitingVerificationUri = true;
            return AgentSignal.None;
        }

        if (line.StartsWith("Please visit:", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = Rest(line, "Please visit:");
            return TryReadUri(candidate, out var visitUri)
                ? new AgentSignal(AgentSignalKind.VerificationUri, visitUri)
                : AgentSignal.None;
        }

        if (line.StartsWith("Make sure the code matches", StringComparison.OrdinalIgnoreCase))
        {
            _awaitingUserCode = true;
            return AgentSignal.None;
        }

        if (line.StartsWith("Authorization successful", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.AuthorizationSucceeded);
        }

        if (line.StartsWith("Persisted session invalid", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Persisted device", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.SessionInvalid, line);
        }

        if (line.StartsWith("Remote session expired", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.SessionExpired, line);
        }

        // Ready block.
        if (line.StartsWith("Device ready", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.DeviceReady);
        }

        if (line.StartsWith("Device Name:", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.DeviceName, Rest(line, "Device Name:"));
        }

        if (line.StartsWith("Device ID assigned:", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.DeviceId, Rest(line, "Device ID assigned:"));
        }

        if (line.StartsWith("Device ID authenticated:", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.DeviceId, Rest(line, "Device ID authenticated:"));
        }

        if (line.StartsWith("Device ID:", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.DeviceId, Rest(line, "Device ID:"));
        }

        if (line.StartsWith("User:", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.UserEmail, Rest(line, "User:"));
        }

        // Connectivity.
        if (line.StartsWith("Device marked as online", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Presence tracked", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Local Desktop Commander MCP restarted", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.DeviceOnline);
        }

        if (line.StartsWith("Device marked as offline", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.DeviceOffline);
        }

        if (line.StartsWith("Channel subscribed", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Channel self-healed", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.ChannelSubscribed);
        }

        if (line.StartsWith("Channel error", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Channel closed", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Channel subscription timed out", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Recreating channel", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Presence track failed", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Presence track not acknowledged", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.ChannelDisrupted, line);
        }

        // Failure / shutdown.
        if (line.StartsWith("Device startup failed", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.StartupFailed, Rest(line, "Device startup failed:"));
        }

        if (line.StartsWith("Shutting down device", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Remote shutdown requested", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSignal(AgentSignalKind.ShuttingDown, line);
        }

        return AgentSignal.None;
    }

    /// <summary>
    /// Strips ANSI escapes, then any leading decoration (emoji, bullets, "1." list
    /// markers and whitespace) so that the payload can be matched by prefix.
    /// </summary>
    internal static string Normalize(string rawLine)
    {
        var line = AnsiEscape().Replace(rawLine, string.Empty);
        line = line.Replace('\t', ' ').Trim();
        line = ListMarker().Replace(line, string.Empty);

        var start = 0;
        while (start < line.Length && !char.IsLetterOrDigit(line[start]))
        {
            start++;
        }

        // A line that is nothing but decoration carries no signal.
        return start >= line.Length ? string.Empty : CollapseSpaces(line[start..]);
    }

    private static string CollapseSpaces(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var c in value)
        {
            var isSpace = c == ' ';
            if (isSpace && previousWasSpace)
            {
                continue;
            }

            builder.Append(c);
            previousWasSpace = isSpace;
        }

        return builder.ToString().TrimEnd();
    }

    private static string Rest(string line, string prefix)
        => line.Length <= prefix.Length ? string.Empty : line[prefix.Length..].Trim();

    private static bool TryReadUri(string candidate, out string uri)
    {
        uri = string.Empty;
        var token = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null)
        {
            return false;
        }

        token = token.Trim().TrimEnd('.', ',', ')');
        if (!Uri.TryCreate(token, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        uri = parsed.AbsoluteUri;
        return true;
    }

    private static bool LooksLikeUserCode(string line) => UserCode().IsMatch(line);

    [GeneratedRegex("\\x1b\\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"^\s*\d+\.\s+")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"^[A-Za-z0-9]{4,12}(-[A-Za-z0-9]{4,12}){0,3}$")]
    private static partial Regex UserCode();
}
