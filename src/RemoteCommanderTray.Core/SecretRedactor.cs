using System.Text.RegularExpressions;

namespace RemoteCommanderTray.Core;

/// <summary>
/// Masks anything that looks like a credential before it reaches the log file or a
/// diagnostics dump.
/// </summary>
/// <remarks>
/// The tray never reads <c>device.json</c> and never parses a token, so this only
/// guards against the agent printing one on its own stdout. It is a last line of
/// defence, not the security boundary - the boundary is that the tray never asks for
/// credentials in the first place.
/// </remarks>
public static partial class SecretRedactor
{
    private const string Mask = "[redacted]";

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        value = JsonWebToken().Replace(value, Mask);
        value = KeyedSecret().Replace(value, m => m.Groups[1].Value + Mask);
        value = BearerHeader().Replace(value, "Bearer " + Mask);
        return value;
    }

    /// <summary>Any JWT-shaped blob, which is what Supabase access and refresh tokens look like.</summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex JsonWebToken();

    /// <summary><c>access_token=...</c>, <c>"refresh_token": "..."</c>, <c>apikey: ...</c> and friends.</summary>
    [GeneratedRegex(
        """((?:access[_-]?token|refresh[_-]?token|id[_-]?token|api[_-]?key|apikey|client[_-]?secret|password)["']?\s*[:=]\s*["']?)[A-Za-z0-9._\-]{8,}""",
        RegexOptions.IgnoreCase)]
    private static partial Regex KeyedSecret();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._\-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerHeader();
}
