namespace RemoteCommanderTray.Core;

/// <summary>Why the user is being interrupted.</summary>
public enum NotificationKind
{
    /// <summary>Sign-in is required before the device can come online.</summary>
    AuthenticationRequired,

    /// <summary>The agent has failed to start several times in a row.</summary>
    StartFailure,

    /// <summary>The agent is running but has not reached Online for a long time.</summary>
    ConnectionStalled,

    /// <summary>The user asked to re-authenticate and now has to finish it in the browser.</summary>
    ReauthenticationStarted,

    /// <summary>The official CLI could not be found or launched at all.</summary>
    AgentUnavailable,

    /// <summary>Legacy notification category; session loss now raises AuthenticationRequired.</summary>
    SessionExpired,
}

/// <summary>
/// A desktop notification the tray should raise.
/// </summary>
/// <remarks>
/// Only events the user has to act on produce one of these. Ordinary network blips and
/// automatic reconnects change the icon and nothing else.
/// </remarks>
/// <param name="Kind">Why it fired.</param>
/// <param name="Title">Balloon title.</param>
/// <param name="Message">Balloon body.</param>
/// <param name="ActionUri">Opened if the user clicks the balloon.</param>
public sealed record TrayNotification(
    NotificationKind Kind,
    string Title,
    string Message,
    string? ActionUri = null);
