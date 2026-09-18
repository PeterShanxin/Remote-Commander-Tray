namespace RemoteCommanderTray.Core;

/// <summary>Observed startup registration, not a guarantee that Windows will launch it.</summary>
public enum StartupState
{
    NotRegistered,
    Enabled,
    DisabledByWindows,
    Unknown,
}

/// <summary>Read-only interpretation of Windows StartupApproved observations.</summary>
/// <remarks>
/// This registry format is not a public Windows contract. Only observed full records
/// are classified; missing permission, malformed data or new flags must stay Unknown.
/// Never write StartupApproved: Windows Startup Apps owns the user's decision.
/// </remarks>
public static class StartupApproval
{
    public static bool IsDisabledByWindows(byte[]? value)
        => Resolve(true, value) == StartupState.DisabledByWindows;

    public static StartupState Resolve(bool hasRunValue, object? approvalValue)
    {
        if (!hasRunValue) return StartupState.NotRegistered;
        if (approvalValue is null) return StartupState.Enabled;
        if (approvalValue is not byte[] { Length: 12 } bytes) return StartupState.Unknown;
        return BitConverter.ToUInt32(bytes, 0) switch
        {
            2 or 6 => StartupState.Enabled,
            3 or 7 => StartupState.DisabledByWindows,
            _ => StartupState.Unknown,
        };
    }
}
