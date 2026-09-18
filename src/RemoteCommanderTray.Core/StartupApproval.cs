namespace RemoteCommanderTray.Core;

/// <summary>What "Launch at sign-in" actually amounts to right now.</summary>
public enum StartupState
{
    /// <summary>No <c>Run</c> entry exists.</summary>
    NotRegistered,

    /// <summary>A <c>Run</c> entry exists and Windows will act on it.</summary>
    Enabled,

    /// <summary>
    /// A <c>Run</c> entry exists but the user switched it off in Windows' own Startup Apps
    /// page, so it will not launch.
    /// </summary>
    DisabledByWindows,
}

/// <summary>
/// Reads the flag Windows keeps alongside a <c>Run</c> entry when the user disables it.
/// </summary>
/// <remarks>
/// Turning a startup app off in Settings or Task Manager does not delete its <c>Run</c>
/// value. Windows records the decision separately, under
/// <c>Explorer\StartupApproved\Run</c>, as a binary value whose first byte carries the
/// state: bit 0 set means disabled. Treating the presence of the <c>Run</c> value as proof
/// that the app will start is how a tray ends up showing a ticked "Launch at sign-in" for
/// something Windows has switched off - and rewriting the <c>Run</c> value does not clear
/// that separate decision.
/// </remarks>
public static class StartupApproval
{
    /// <summary>
    /// Interprets a <c>StartupApproved</c> value.
    /// </summary>
    /// <param name="approvalValue">
    /// The raw bytes, or null when Windows has no record - which means "not disabled".
    /// </param>
    public static bool IsDisabledByWindows(byte[]? approvalValue)
        => approvalValue is { Length: > 0 } && (approvalValue[0] & 0x01) != 0;

    /// <summary>Combines the two registry reads into one answer.</summary>
    /// <param name="hasRunValue">Whether the <c>Run</c> value exists.</param>
    /// <param name="approvalValue">The <c>StartupApproved\Run</c> value, if any.</param>
    public static StartupState Resolve(bool hasRunValue, byte[]? approvalValue)
    {
        if (!hasRunValue)
        {
            return StartupState.NotRegistered;
        }

        return IsDisabledByWindows(approvalValue) ? StartupState.DisabledByWindows : StartupState.Enabled;
    }
}
