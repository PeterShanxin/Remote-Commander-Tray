#if STORE_DISTRIBUTION
using System.Runtime.InteropServices;
using RemoteCommanderTray.Core;
using Windows.ApplicationModel;

namespace RemoteCommanderTray.Windows;

/// <summary>MSIX startup state is owned by Windows, never by a virtualized Run key.</summary>
internal static class PackagedStartupRegistration
{
    internal const string TaskId = "RemoteCommanderTrayStartup";
    private static StartupTask? _task;
    private static string? _initializationError;

    // Called before any WinForms controls install a synchronization context.
    public static void Initialize()
    {
        try { _task = StartupTask.GetAsync(TaskId).AsTask().GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        { _initializationError = "Windows could not read the packaged startup task. Open Windows Startup Apps settings."; }
    }

    public static StartupState GetState()
    {
        try
        {
            return _task?.State switch
            {
                StartupTaskState.Disabled => StartupState.NotRegistered,
                StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => StartupState.Enabled,
                StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy => StartupState.DisabledByWindows,
                _ => StartupState.Unknown,
            };
        }
        catch (COMException) { return StartupState.Unknown; }
    }

    public static async Task<(bool Success, string? Error)> TrySetAsync(bool enabled)
    {
        if (_task is null) return (false, _initializationError ?? "The packaged startup task is unavailable.");
        try
        {
            if (_task.State is StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy)
                return (false, "This startup setting is managed by Windows policy.");
            if (enabled)
            {
                var state = await _task.RequestEnableAsync();
                return state == StartupTaskState.Enabled
                    ? (true, null)
                    : (false, "Windows did not enable startup. Check Windows Startup Apps settings.");
            }
            _task.Disable();
            return (GetState() == StartupState.NotRegistered, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        { return (false, "Windows could not change the startup setting. Open Windows Startup Apps settings."); }
    }
}
#endif
