using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RemoteCommanderTray.Windows;

/// <summary>
/// "Launch at sign-in", implemented as a per-user <c>Run</c> registry value.
/// </summary>
/// <remarks>
/// No scheduled task and no admin rights: <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>
/// is writable by the signed-in user, survives upgrades, and is the same place the
/// Startup Apps settings page shows, so the user can turn it off from either side.
/// The registry value is the single source of truth - nothing mirrors it in settings.json.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RemoteCommanderTray";

    /// <summary>Passed by the registry entry so the app knows it was started by Windows.</summary>
    public const string AutostartSwitch = "--autostart";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

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
    public static void RefreshIfEnabled()
    {
        if (IsEnabled())
        {
            TrySet(true, out _);
        }
    }
}
