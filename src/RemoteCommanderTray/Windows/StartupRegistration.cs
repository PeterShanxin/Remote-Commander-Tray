using System.Runtime.Versioning;
using Microsoft.Win32;
using RemoteCommanderTray.Core;

namespace RemoteCommanderTray.Windows;

/// <summary>
/// "Launch at sign-in", implemented as a per-user <c>Run</c> registry value.
/// </summary>
/// <remarks>
/// <para>
/// No scheduled task and no admin rights: <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>
/// is writable by the signed-in user, survives upgrades, and is the same place the
/// Startup Apps settings page shows, so the user can turn it off from either side.
/// </para>
/// <para>
/// The registry is the single source of truth - nothing mirrors it in settings.json - but
/// it takes two values to read, not one. Disabling a startup app in Windows leaves the
/// <c>Run</c> value in place and records the decision under <c>StartupApproved</c>, so the
/// presence of a command is not proof that the next sign-in will run it. That user
/// decision is preserved here rather than overwritten: re-enabling is offered through
/// Windows' own Startup Apps page.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ApprovedKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private const string ValueName = "RemoteCommanderTray";

    /// <summary>The Settings page that owns the enabled/disabled decision.</summary>
    public const string StartupAppsSettingsUri = "ms-settings:startupapps";

    /// <summary>Passed by the registry entry so the app knows it was started by Windows.</summary>
    public const string AutostartSwitch = "--autostart";

    /// <summary>The effective state, combining the command and Windows' own decision.</summary>
    public static StartupState GetState()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var hasRunValue = runKey?.GetValue(ValueName) is string value && value.Length > 0;

            using var approvedKey = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: false);
            var approval = approvedKey?.GetValue(ValueName) as byte[];

            return StartupApproval.Resolve(hasRunValue, approval);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return StartupState.NotRegistered;
        }
    }

    /// <summary>True only when the next sign-in will actually launch the tray.</summary>
    public static bool IsEnabled() => GetState() == StartupState.Enabled;

    public static bool TrySet(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "Could not open the Windows Run key.";
                return false;
            }

            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executable))
                {
                    error = "Could not determine this application's path.";
                    return false;
                }

                key.SetValue(ValueName, $"\"{executable}\" {AutostartSwitch}", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Repoints an existing entry at the current executable, so moving or updating the
    /// app does not leave a startup entry aimed at a path that no longer exists.
    /// </summary>
    /// <remarks>
    /// Only touches the command. An entry the user disabled in Windows stays disabled.
    /// </remarks>
    public static void RefreshIfRegistered()
    {
        if (GetState() != StartupState.NotRegistered)
        {
            TrySet(true, out _);
        }
    }
}
