using System.Reflection;
using System.Runtime.Versioning;
using RemoteCommanderTray.Core;
using RemoteCommanderTray.UI;
using RemoteCommanderTray.Windows;

namespace RemoteCommanderTray;

[SupportedOSPlatform("windows")]
internal static class Program
{
    /// <summary>
    /// Per-user, per-session name. A second sign-in on the same machine gets its own
    /// tray and its own agent, which is what you want; a second launch by the same user
    /// does not.
    /// </summary>
    private static readonly string MutexName =
        $"Local\\RemoteCommanderTray.SingleInstance.{Environment.UserName}";

    [STAThread]
    private static int Main(string[] args)
    {
        var startedByWindows = args.Any(a =>
            string.Equals(a, StartupRegistration.AutostartSwitch, StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Silent when Windows started us at sign-in; a nudge when a human did.
            if (!startedByWindows)
            {
                MessageBox.Show(
                    "Remote Commander Tray is already running. Look for it in the notification area.",
                    "Remote Commander Tray",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return 0;
        }

        ApplicationConfiguration.Initialize();

        AppPaths paths;
        TraySettings settings;
        RollingFileLog log;
        try
        {
            // Creating the data directory can fail on its own - a redirected, full or
            // locked %LOCALAPPDATA%. Doing it outside the handler below turned that into
            // an unhandled exception and a launch that simply never appeared.
            paths = new AppPaths();
            paths.EnsureCreated();

            var settingsStore = new SettingsStore(paths.SettingsFile);
            settings = settingsStore.Load();

            // Writing the file back on first run gives users something to edit.
            if (!File.Exists(paths.SettingsFile))
            {
                settingsStore.TrySave(settings, out _);
            }

            log = new RollingFileLog(paths.AgentLogFile, settings.LogMaxBytes, settings.LogRetainedFiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(
                $"Remote Commander Tray could not prepare its data folder:\n\n{ex.Message}",
                "Remote Commander Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            mutex.ReleaseMutex();
            return 1;
        }

        var version = ReadVersion();
        log.Write(
            LogSource.Tray,
            $"Remote Commander Tray {version} starting "
            + $"({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, "
            + $"startedByWindows={startedByWindows}).");

        // Keeps a registered startup entry pointing at wherever the app lives now,
        // without disturbing a disable the user applied in Windows.
        StartupRegistration.RefreshIfRegistered();

        Application.ThreadException += (_, e) =>
            log.Write(LogSource.Tray, $"Unhandled UI exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Write(LogSource.Tray, $"Unhandled exception: {e.ExceptionObject}");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        try
        {
            using var context = new TrayApplicationContext(paths, settings, log, version);
            Application.Run(context);
        }
        catch (Exception ex)
        {
            log.Write(LogSource.Tray, $"Fatal: {ex}");
            MessageBox.Show(
                $"Remote Commander Tray could not start:\n\n{ex.Message}",
                "Remote Commander Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            log.Dispose();
            mutex.ReleaseMutex();
        }

        return 0;
    }

    private static string ReadVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip the "+<commit sha>" the SDK appends.
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        // Assembly.Location is empty in a single-file publish, so the assembly version
        // is the only fallback left.
        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
