using System.Runtime.Versioning;
using Microsoft.Win32;
using RemoteCommanderTray.Core;
namespace RemoteCommanderTray.Windows;

/// <summary>User-level Run registration. Approval records are read, NEVER modified.
/// The overloads let Windows integration tests use disposable keys, not real startup.</summary>
[SupportedOSPlatform("windows")]
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "RemoteCommanderTray";
    public const string StartupAppsSettingsUri = "ms-settings:startupapps";
    public const string AutostartSwitch = "--autostart";

    public static StartupState GetState() => GetState(RunKeyPath, ApprovedKeyPath, ValueName);
    public static bool IsEnabled() => GetState() == StartupState.Enabled;
    internal static StartupState GetState(string runPath, string approvedPath, string name)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(runPath);
            var command = run?.GetValue(name);
            if (command is null) return StartupState.NotRegistered;
            if (command is not string text || string.IsNullOrWhiteSpace(text)) return StartupState.Unknown;
            using var approved = Registry.CurrentUser.OpenSubKey(approvedPath);
            var record = approved?.GetValue(name);
            if (record is not null && record is not byte[]) return StartupState.Unknown;
            return StartupApproval.Resolve(true, record as byte[]);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { return StartupState.Unknown; }
    }

    public static bool TrySet(bool enabled, out string? error)
        => TrySet(enabled, Environment.ProcessPath, RunKeyPath, ValueName, out error);
    internal static bool TrySet(bool enabled, string? executable, string runPath, string name, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(runPath, writable: true);
            if (key is null) { error = "Could not open the Windows Run key."; return false; }
            if (enabled)
            {
                if (string.IsNullOrWhiteSpace(executable) || executable.Contains('"'))
                { error = "Could not determine a valid application path."; return false; }
                key.SetValue(name, $"\"{executable}\" {AutostartSwitch}", RegistryValueKind.String);
            }
            else key.DeleteValue(name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { error = ex.Message; return false; }
    }

    public static void RefreshIfRegistered()
    {
        if (GetState() is StartupState.Enabled or StartupState.DisabledByWindows) TrySet(true, out _);
    }
}
