using System.Runtime.Versioning;
using RemoteCommanderTray.Core;
using RemoteCommanderTray.Windows;

namespace RemoteCommanderTray.UI;

/// <summary>
/// The whole user interface: one notify icon and one context menu, with no main window.
/// </summary>
/// <remarks>
/// This class only renders <see cref="AgentSnapshot"/> and turns clicks into supervisor
/// calls. It contains no log parsing and no state rules, which is what keeps the state
/// machine testable without a message loop.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int TooltipMaxLength = 63;
    private readonly System.Windows.Forms.Timer _menuTimer = new() { Interval = 1000 };

    private readonly AppPaths _paths;
    private readonly RollingFileLog _log;
    private readonly RollingFileLog? _verboseLog;
    private readonly AgentSupervisor _supervisor;
    private readonly WindowsAgentProcessFactory _processFactory;
    private readonly TrayIcons _icons = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Control _marshal;
    private readonly string _version;
    private readonly Func<TraySettings> _settings;

    private readonly ToolStripMenuItem _headerItem;
    private readonly ToolStripMenuItem _detailItem;
    private readonly ToolStripSeparator _signInSeparator;
    private readonly ToolStripMenuItem _openSignInItem;
    private readonly ToolStripMenuItem _copyCodeItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly ToolStripMenuItem _reauthenticateItem;
    private readonly ToolStripMenuItem _toggleAgentItem;
    private readonly ToolStripMenuItem _launchAtSignInItem;

    private AgentSnapshot _snapshot = AgentSnapshot.Initial;
    private bool _busy;
    private bool _exiting;

    public TrayApplicationContext(
        AppPaths paths,
        TraySettings settings,
        RollingFileLog log,
        string version)
    {
        _paths = paths;
        _log = log;
        _version = version;
        _settings = () => settings;

        // A real control gives a dependable BeginInvoke target for supervisor callbacks,
        // which arrive on thread-pool threads.
        _marshal = new Control();
        _marshal.CreateControl();

        _processFactory = new WindowsAgentProcessFactory();

        // Raw agent output can contain whatever a remote tool call read, so it only gets
        // written when the user opts in, and is never copied into diagnostics.
        _verboseLog = settings.VerboseAgentLog
            ? new RollingFileLog(paths.VerboseAgentLogFile, settings.LogMaxBytes, settings.LogRetainedFiles)
            : null;

        var machine = new AgentStateMachine();
        _supervisor = new AgentSupervisor(
            machine,
            _processFactory,
            new AgentCommandResolver(),
            _settings,
            _log,
            verboseLog: _verboseLog);

        _headerItem = new ToolStripMenuItem("Starting...") { Enabled = false };
        _detailItem = new ToolStripMenuItem("Last connected: never") { Enabled = false };
        _signInSeparator = new ToolStripSeparator { Visible = false };
        _openSignInItem = new ToolStripMenuItem("Open sign-in page", null, (_, _) => OpenSignInPage())
        {
            Visible = false,
        };
        _copyCodeItem = new ToolStripMenuItem("Copy sign-in code", null, (_, _) => CopySignInCode())
        {
            Visible = false,
        };
        _restartItem = new ToolStripMenuItem("Restart connection", null, (_, _) => Run(_supervisor.RestartAsync));
        _reauthenticateItem = new ToolStripMenuItem("Re-authenticate...", null, (_, _) => Reauthenticate());
        _toggleAgentItem = new ToolStripMenuItem("Stop agent", null, (_, _) => ToggleAgent());
        _launchAtSignInItem = new ToolStripMenuItem("Launch at sign-in", null, (_, _) => ToggleLaunchAtSignIn())
        {
            CheckOnClick = false,
        };

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(
        [
            _headerItem,
            _detailItem,
            _signInSeparator,
            _openSignInItem,
            _copyCodeItem,
            new ToolStripSeparator(),
            _restartItem,
            _reauthenticateItem,
            _toggleAgentItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open Remote MCP", null, (_, _) => Shell.OpenUrl(_settings().RemoteMcpUrl)),
            new ToolStripMenuItem("Open logs", null, (_, _) => OpenLogs()),
            new ToolStripMenuItem("Copy diagnostics", null, (_, _) => CopyDiagnostics()),
            new ToolStripSeparator(),
            _launchAtSignInItem,
            new ToolStripMenuItem("About", null, (_, _) => ShowAbout()),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()),
        ]);
        _menu.Opening += (_, _) => { RefreshMenu(); _menuTimer.Start(); };
        _menu.Closed += (_, _) => _menuTimer.Stop();
        _menuTimer.Tick += (_, _) => RefreshMenu();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icons.Get(AgentState.Stopped),
            Text = "Remote Commander",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.BalloonTipClicked += (_, _) => OnBalloonClicked();
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowStatusBalloon();
            }
        };

        _supervisor.Changed += (_, snapshot) => OnUiThread(() => ApplySnapshot(snapshot));
        _supervisor.Notification += (_, notification) => OnUiThread(() => ShowNotification(notification));

        ApplySnapshot(_supervisor.Snapshot);
        Run(_supervisor.InitializeAsync);
    }

    // --- rendering -------------------------------------------------------

    private void ApplySnapshot(AgentSnapshot snapshot)
    {
        _snapshot = snapshot;
        _notifyIcon.Icon = _icons.Get(snapshot.State);
        _notifyIcon.Text = BuildTooltip(snapshot);
        RefreshMenu();
    }

    private void RefreshMenu()
    {
        var snapshot = _snapshot;

        _headerItem.Text = $"{GlyphFor(snapshot.State)} {snapshot.StateLabel}{DeviceSuffix(snapshot)}";
        _detailItem.Text = BuildDetailLine(snapshot);

        var signInVisible = snapshot.State == AgentState.AuthenticationRequired;
        _signInSeparator.Visible = signInVisible;
        _openSignInItem.Visible = signInVisible;
        _openSignInItem.Enabled = !_busy && (snapshot.RequiresReauthentication || !string.IsNullOrWhiteSpace(snapshot.VerificationUri));
        _openSignInItem.Text = snapshot.RequiresReauthentication ? "Sign in again..." : "Open sign-in page";
        _copyCodeItem.Visible = signInVisible && !string.IsNullOrWhiteSpace(snapshot.UserCode);
        _copyCodeItem.Text = snapshot.UserCode is null
            ? "Copy sign-in code"
            : $"Copy code: {snapshot.UserCode}";

        _toggleAgentItem.Text = snapshot.AgentWanted ? "Stop agent" : "Start agent";
        _restartItem.Enabled = !_busy;
        _reauthenticateItem.Enabled = !_busy;
        _toggleAgentItem.Enabled = !_busy;
        RefreshStartupItem();
    }

    /// <summary>
    /// Shows the observed registration and Windows approval state. A Run entry that Windows has
    /// switched off is not "on", and re-ticking it here would not turn it back on.
    /// </summary>
    private void RefreshStartupItem()
    {
        var state = StartupRegistration.GetState();
        _launchAtSignInItem.Checked = state == StartupState.Enabled;
        _launchAtSignInItem.Text = state switch
        {
            StartupState.DisabledByWindows => "Launch at sign-in (turned off in Windows)",
            StartupState.Unknown => "Launch at sign-in (check Windows settings)",
            _ => "Launch at sign-in",
        };
    }

    private static string GlyphFor(AgentState state) => state switch
    {
        AgentState.Online => "✓",                 // check
        AgentState.Connecting or AgentState.Starting => "…", // ellipsis
        AgentState.AuthenticationRequired => "\U0001F511", // key
        AgentState.Error => "×",                  // multiplication sign
        _ => "⏸",                                 // pause
    };

    private static string DeviceSuffix(AgentSnapshot snapshot)
        => string.IsNullOrWhiteSpace(snapshot.DeviceName) ? string.Empty : $" - {snapshot.DeviceName}";

    private string BuildDetailLine(AgentSnapshot snapshot)
    {
        if (snapshot.NextRestartUtc is { } due)
        {
            var seconds = Math.Max(0, (int)(due - DateTimeOffset.UtcNow).TotalSeconds);
            return $"Retrying in {seconds}s (attempt {snapshot.RestartCount})";
        }

        if (snapshot.State is AgentState.Error or AgentState.AuthenticationRequired && !string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            return Truncate(snapshot.LastError, 60);
        }

        return snapshot.LastConnectedUtc is { } connected
            ? $"Last connected: {connected.ToLocalTime():HH:mm}"
            : "Last connected: never";
    }

    private static string BuildTooltip(AgentSnapshot snapshot)
    {
        // NotifyIcon.Text refuses anything longer than 63 characters.
        var full = $"Remote Commander - {snapshot.StateLabel}{DeviceSuffix(snapshot)}";
        return full.Length <= TooltipMaxLength
            ? full
            : Truncate(full, TooltipMaxLength);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    // --- commands --------------------------------------------------------

    private void ToggleAgent()
    {
        if (_snapshot.AgentWanted)
        {
            Run(_supervisor.StopAsync);
        }
        else
        {
            Run(_supervisor.StartAsync);
        }
    }

    private void Reauthenticate()
    {
        var answer = MessageBox.Show(
            "Sign out of Desktop Commander on this device and sign in again?\n\n"
            + "The official Desktop Commander CLI clears its own saved credentials and "
            + "opens your browser. Remote Commander Tray never reads or stores them.",
            "Re-authenticate",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        if (answer == DialogResult.OK)
        {
            Run(_supervisor.ReauthenticateAsync);
        }
    }

    private void OpenSignInPage()
    {
        if (_snapshot.RequiresReauthentication) { Reauthenticate(); return; }
        if (!Shell.OpenUrl(_snapshot.VerificationUri))
        {
            MessageBox.Show(
                "No sign-in page has been reported yet. Open logs to see what the agent is doing.",
                "Remote Commander Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private void CopySignInCode() => CopyToClipboard(_snapshot.UserCode, "Sign-in code copied.");

    private void OpenLogs()
    {
        if (!Shell.OpenFile(_log.Path) && !Shell.RevealInExplorer(_paths.LogDirectory))
        {
            MessageBox.Show(
                $"Could not open the log folder:\n{_paths.LogDirectory}",
                "Remote Commander Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void CopyDiagnostics()
    {
        var context = new DiagnosticsContext(
            _version,
            Environment.OSVersion.VersionString,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            _supervisor.LaunchDescription,
            _log.Path,
            StartupRegistration.GetState());

        var report = DiagnosticsReport.Build(_snapshot, context);
        CopyToClipboard(report, "Diagnostics copied to the clipboard.");
    }

    private void ToggleLaunchAtSignIn()
    {
        var state = StartupRegistration.GetState();
        if (state is StartupState.DisabledByWindows or StartupState.Unknown)
        {
            // Windows owns this decision; writing the Run value again would not clear it.
            var open = MessageBox.Show(
                "Windows startup is disabled or its state could not be verified.\n\n"
                + "Check or re-enable it in Windows Startup Apps settings now?",
                "Launch at sign-in",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information);

            if (open == DialogResult.OK)
            {
                OpenStartupSettings();
            }

            return;
        }

        var target = state == StartupState.NotRegistered;
        if (StartupRegistration.TrySet(target, out var error))
        {
            RefreshStartupItem();
            if (target && StartupRegistration.GetState() is StartupState.DisabledByWindows or StartupState.Unknown)
                OpenStartupSettings();
            _log.Write(LogSource.Tray, $"Startup registration updated; observed state: {StartupRegistration.GetState()}.");
            return;
        }

        _log.Write(LogSource.Tray, $"Could not change launch at sign-in: {error}");
        MessageBox.Show(
            $"Could not change the startup setting:\n{error}",
            "Remote Commander Tray",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private static void OpenStartupSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                StartupRegistration.StartupAppsSettingsUri)
            {
                UseShellExecute = true,
            })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // The Settings app is unavailable; the message box already said what to do.
        }
    }

    private void ShowAbout()
    {
        // The dialog gets its own copy: the cached tray icons outlive it.
        using var icon = (Icon)_icons.Get(_snapshot.State).Clone();
        using var about = new AboutForm(_version, icon, _paths.Root);
        about.ShowDialog();
    }

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _notifyIcon.Visible = false;
        _log.Write(LogSource.Tray, "Exit requested; stopping agent.");

        _ = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        try
        {
            await _supervisor.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Write(LogSource.Tray, $"Error during shutdown: {ex.Message}");
        }

        _log.Write(LogSource.Tray, "Tray exited.");
        OnUiThread(ExitThread);
    }

    // --- notifications ---------------------------------------------------

    private void ShowNotification(TrayNotification notification)
    {
        var icon = notification.Kind switch
        {
            NotificationKind.AuthenticationRequired => ToolTipIcon.Warning,
            NotificationKind.ReauthenticationStarted => ToolTipIcon.Info,
            _ => ToolTipIcon.Error,
        };

        _notifyIcon.ShowBalloonTip(10_000, notification.Title, notification.Message, icon);
    }

    private void ShowStatusBalloon()
    {

        _notifyIcon.ShowBalloonTip(
            5_000,
            $"Remote Commander - {_snapshot.StateLabel}",
            BuildDetailLine(_snapshot),
            ToolTipIcon.None);
    }

    private void OnBalloonClicked()
    {
        // The auth notification may have preceded the CLI's URL line. Always use
        // the current generation's live prompt, never a stale completed sign-in URL.
        var uri = _snapshot.State == AgentState.AuthenticationRequired ? _snapshot.VerificationUri : null;
        if (!Shell.OpenUrl(uri))
        {
            if (_snapshot.State == AgentState.AuthenticationRequired) Reauthenticate();
            else Shell.OpenFile(_log.Path);
        }
    }

    // --- plumbing --------------------------------------------------------

    private void Run(Func<Task> command)
    {
        if (_busy || _exiting) return;
        _busy = true;
        RefreshMenu();
        _ = RunCoreAsync(command);
    }

    private async Task RunCoreAsync(Func<Task> command)
    {
        try
        {
            await command().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Write(LogSource.Tray, $"Command failed ({ex.GetType().Name}, HRESULT={ex.HResult:X8}).");
            OnUiThread(() => MessageBox.Show(
                $"That did not work:\n{ex.Message}",
                "Remote Commander Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning));
        }
        finally
        {
            OnUiThread(() =>
            {
                _busy = false;
                RefreshMenu();
            });
        }
    }

    private void CopyToClipboard(string? value, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            Clipboard.SetText(value);
            _notifyIcon.ShowBalloonTip(3_000, "Remote Commander Tray", successMessage, ToolTipIcon.None);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            _log.Write(LogSource.Tray, $"Clipboard unavailable: {ex.Message}");
            MessageBox.Show(
                "Windows would not let the clipboard be written just now. Try again.",
                "Remote Commander Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OnUiThread(Action action)
    {
        if (_marshal.IsDisposed)
        {
            return;
        }

        try
        {
            if (_marshal.InvokeRequired)
            {
                _marshal.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The message loop is gone; nothing left to render.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _menuTimer.Dispose();
            _supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _verboseLog?.Dispose();
            _menu.Dispose();
            _icons.Dispose();
            _marshal.Dispose();
            _log.Dispose();
        }

        base.Dispose(disposing);
    }
}
