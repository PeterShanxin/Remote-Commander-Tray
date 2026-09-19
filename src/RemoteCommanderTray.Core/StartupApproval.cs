namespace RemoteCommanderTray.Core;

/// <summary>Observed Windows startup registration, not a guarantee of the next logon.</summary>
public enum StartupState { NotRegistered, Enabled, DisabledByWindows, Unknown }

/// <summary>Read-only interpretation of known StartupApproved records. Windows owns
/// the record format; unfamiliar or malformed records must not claim Enabled.</summary>
public static class StartupApproval
{
    public static bool IsDisabledByWindows(byte[]? approvalValue)
        => approvalValue is { Length: 12 } && approvalValue[0] is 0x03 or 0x07;

    public static StartupState Resolve(bool hasRunValue, byte[]? approvalValue)
    {
        if (!hasRunValue) return StartupState.NotRegistered;
        if (approvalValue is null) return StartupState.Enabled;
        if (approvalValue.Length != 12 || approvalValue[1] != 0 || approvalValue[2] != 0 || approvalValue[3] != 0)
            return StartupState.Unknown;
        return approvalValue[0] switch
        {
            0x02 or 0x06 => StartupState.Enabled,
            0x03 or 0x07 => StartupState.DisabledByWindows,
            _ => StartupState.Unknown,
        };
    }
}
