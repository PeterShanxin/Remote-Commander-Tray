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
    private readonly Action<int, string, string, ToolTipIcon> _showBalloonTip;
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
    private readonly ToolStripMenuItem _languageItem;
    private readonly UiStrings _strings;

    private AgentSnapshot _snapshot = AgentSnapshot.Initial;
    private bool _busy;
    private bool _exiting;
    private bool _startupBusy;
    private bool _launchBalloon;
    private Task? _supervisorDisposeTask;
    private bool _supervisorDisposeFailureReported;

    public TrayApplicationContext(
        AppPaths paths,
        TraySettings settings,
        RollingFileLog log,
        string version,
        bool startedByWindows,
        Action<int, string, string, ToolTipIcon>? showBalloonTip = null)
    {
        _paths = paths;
        _log = log;
        _version = version;
        _settings = () => settings;
        _strings = new UiStrings(settings.Language);

        var machine = new AgentStateMachine();
        _marshal = new TrayMessageWindow(machine);
        _ = _marshal.Handle; // Receive broadcasts without showing a window.

        _processFactory = new WindowsAgentProcessFactory();

        // Raw agent output can contain whatever a remote tool call read, so it only gets
        // written when the user opts in, and is never copied into diagnostics.
        _verboseLog = settings.VerboseAgentLog
            ? new RollingFileLog(paths.VerboseAgentLogFile, settings.LogMaxBytes, settings.LogRetainedFiles)
            : null;

        _supervisor = new AgentSupervisor(
            machine,
            _processFactory,
#if STORE_DISTRIBUTION
            new AgentCommandResolver(allowPackageDownload: false),
#else
            new AgentCommandResolver(),
#endif
            _settings,
            _log,
            verboseLog: _verboseLog);

        _headerItem = new ToolStripMenuItem(_strings.Get(TextId.StartingEllipsis)) { Enabled = false };
        _detailItem = new ToolStripMenuItem(_strings.Get(TextId.LastConnectedNever)) { Enabled = false };
        _signInSeparator = new ToolStripSeparator { Visible = false };
        _openSignInItem = new ToolStripMenuItem(_strings.Get(TextId.OpenSignInPage), null, (_, _) => OpenSignInPage())
        {
            Visible = false,
        };
        _copyCodeItem = new ToolStripMenuItem(_strings.Get(TextId.CopySignInCode), null, (_, _) => CopySignInCode())
        {
            Visible = false,
        };
        _restartItem = new ToolStripMenuItem(_strings.Get(TextId.RestartConnection), null, (_, _) => Run(_supervisor.RestartAsync));
        _reauthenticateItem = new ToolStripMenuItem(_strings.Get(TextId.ReauthenticateMenu), null, (_, _) => Reauthenticate());
        _toggleAgentItem = new ToolStripMenuItem(_strings.Get(TextId.StopAgent), null, (_, _) => ToggleAgent());
        _launchAtSignInItem = new ToolStripMenuItem(_strings.Get(TextId.LaunchAtSignIn), null, (_, _) => ToggleLaunchAtSignIn())
        {
            CheckOnClick = false,
        };

        _languageItem = new ToolStripMenuItem(_strings.Get(TextId.LanguageMenu));
        foreach (var (code, label) in new[]
        {
            ("auto", _strings.Get(TextId.FollowWindows)),
            ("en", "English"),
            ("zh-CN", "简体中文"),
        })
        {
            var selected = code;
            _languageItem.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => ChangeLanguage(selected))
            {
                Tag = code,
                CheckOnClick = false,
            });
        }

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
            new ToolStripMenuItem(_strings.Get(TextId.OpenRemoteMcp), null, (_, _) => Shell.OpenUrl(_settings().RemoteMcpUrl)) { Tag = TextId.OpenRemoteMcp },
            new ToolStripMenuItem(_strings.Get(TextId.OpenLogs), null, (_, _) => OpenLogs()) { Tag = TextId.OpenLogs },
            new ToolStripMenuItem(_strings.Get(TextId.CopyDiagnostics), null, (_, _) => CopyDiagnostics()) { Tag = TextId.CopyDiagnostics },
            new ToolStripSeparator(),
            _launchAtSignInItem,
            _languageItem,
            new ToolStripMenuItem(_strings.Get(TextId.About), null, (_, _) => ShowAbout()) { Tag = TextId.About },
            new ToolStripMenuItem(_strings.Get(TextId.Exit), null, (_, _) => ExitApplication()) { Tag = TextId.Exit },
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
        _showBalloonTip = showBalloonTip ?? _notifyIcon.ShowBalloonTip;
        _notifyIcon.BalloonTipClicked += (_, _) => OnBalloonClicked();
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowStatusBalloon();
            }
        };

        // A worker callback may be queued before a newer change renders on the UI thread.
        _supervisor.Changed += (_, _) => OnUiThread(() => ApplySnapshot(_supervisor.Snapshot));
        _supervisor.Notification += (_, notification) => OnUiThread(() =>
        {
            if (!_exiting) ShowNotification(notification);
        });

        ApplySnapshot(_supervisor.Snapshot);
        if (!startedByWindows && settings.NotificationsEnabled)
        {
            ShowBalloon(5_000, _strings.Get(TextId.LaunchTitle),
                _strings.Get(settings.StartAgentOnLaunch ? TextId.LaunchConnecting : TextId.LaunchAgentStopped),
                ToolTipIcon.Info, launch: true);
        }

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

        _headerItem.Text = $"{GlyphFor(snapshot.State)} {_strings.State(snapshot)}{DeviceSuffix(snapshot)}";
        _detailItem.Text = BuildDetailLine(snapshot);
        _languageItem.Text = _strings.Get(TextId.LanguageMenu);
        foreach (ToolStripMenuItem option in _languageItem.DropDownItems)
            option.Checked = option.Tag is string lang && lang == _settings().Language;
        _restartItem.Text = _strings.Get(TextId.RestartConnection);
        _reauthenticateItem.Text = _strings.Get(TextId.ReauthenticateMenu);

        var signInVisible = snapshot.State == AgentState.AuthenticationRequired;
        _signInSeparator.Visible = signInVisible;
        _openSignInItem.Visible = signInVisible;
        _openSignInItem.Enabled = !_busy && (snapshot.RequiresReauthentication || !string.IsNullOrWhiteSpace(snapshot.VerificationUri));
        _openSignInItem.Text = _strings.Get(snapshot.RequiresReauthentication ? TextId.SignInAgain : TextId.OpenSignInPage);
        _copyCodeItem.Visible = signInVisible && !string.IsNullOrWhiteSpace(snapshot.UserCode);
        _copyCodeItem.Text = snapshot.UserCode is null
            ? _strings.Get(TextId.CopySignInCode)
            : _strings.Format(TextId.CopyCode, snapshot.UserCode);

        _toggleAgentItem.Text = _strings.Get(snapshot.AgentWanted ? TextId.StopAgent : TextId.StartAgent);
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
        _launchAtSignInItem.Enabled = !_startupBusy;
        _launchAtSignInItem.Text = state switch
        {
            StartupState.DisabledByWindows => _strings.Get(TextId.LaunchDisabled),
            StartupState.Unknown => _strings.Get(TextId.LaunchUnknown),
            _ => _strings.Get(TextId.LaunchAtSignIn),
        };
    }

    private static string GlyphFor(AgentState state) => state switch
    {
        AgentState.Online => "✓",                 // check
        AgentState.Unconfirmed => "?",
        AgentState.Connecting or AgentState.Starting or AgentState.Stopping => "…", // ellipsis
        AgentState.AuthenticationRequired => "\U0001F511", // key
        AgentState.Error => "×",                  // multiplication sign
        _ => "⏸",                                 // pause
    };

    private static string DeviceSuffix(AgentSnapshot snapshot)
        => string.IsNullOrWhiteSpace(snapshot.DeviceName) ? string.Empty : $" - {snapshot.DeviceName}";

    private string BuildDetailLine(AgentSnapshot snapshot)
    {
        if (snapshot.State == AgentState.Unconfirmed) return _strings.Get(TextId.WaitingAfterSleep);

        if (snapshot.NextRestartUtc is { } due)
        {
            var seconds = Math.Max(0, (int)(due - DateTimeOffset.UtcNow).TotalSeconds);
            return _strings.Format(TextId.Retrying, seconds, snapshot.RestartCount);
        }

        if (snapshot.State is AgentState.Error or AgentState.AuthenticationRequired && !string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            return Truncate(snapshot.LastError, 60);
        }

        return snapshot.LastConnectedUtc is { } connected
            ? _strings.Format(TextId.LastConnected, connected.ToLocalTime().ToString("HH:mm"))
            : _strings.Get(TextId.LastConnectedNever);
    }

    private string BuildTooltip(AgentSnapshot snapshot)
    {
        // NotifyIcon.Text refuses anything longer than 63 characters.
        var status = _strings.State(snapshot);
        var full = snapshot.State == AgentState.Unconfirmed
            ? $"{status} · {_strings.Get(TextId.WaitingAfterSleep)}"
            : _strings.Format(TextId.StatusTitle, status) + DeviceSuffix(snapshot);
        return Truncate(full, TooltipMaxLength);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    // --- commands --------------------------------------------------------

    private void ChangeLanguage(string language)
    {
        var settings = _settings();
        var previous = settings.Language;
        settings.Language = language;
        if (!new SettingsStore(_paths.SettingsFile).TrySave(settings, out var error))
        {
            settings.Language = previous;
            MessageBox.Show(string.IsNullOrWhiteSpace(error) ? _strings.Get(TextId.SaveLanguageFailed) : error,
                _strings.Get(TextId.ApplicationName),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _strings.Configure(settings.Language);
        foreach (var item in _menu.Items.OfType<ToolStripMenuItem>())
        {
            if (item.Tag is TextId label)
            {
                item.Text = _strings.Get(label);
            }
        }
        ApplySnapshot(_snapshot);
    }

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
            _strings.Get(TextId.ReauthenticatePrompt),
            _strings.Get(TextId.ReauthenticateTitle),
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
                _strings.Get(TextId.NoSignInPage),
                _strings.Get(TextId.ApplicationName),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private void CopySignInCode() => CopyToClipboard(_snapshot.UserCode, _strings.Get(TextId.SignInCodeCopied));

    private void OpenLogs()
    {
        if (!Shell.OpenFile(_log.Path) && !Shell.RevealInExplorer(_paths.LogDirectory))
        {
            MessageBox.Show(
                _strings.Format(TextId.LogFolderUnavailable, _paths.LogDirectory),
                _strings.Get(TextId.ApplicationName),
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
        CopyToClipboard(report, _strings.Get(TextId.DiagnosticsCopied));
    }

    private async void ToggleLaunchAtSignIn()
    {
        if (_startupBusy || _exiting) return;
        var state = StartupRegistration.GetState();
        if (state is StartupState.DisabledByWindows or StartupState.Unknown)
        {
            // Windows owns this decision; writing the Run value again would not clear it.
            var open = MessageBox.Show(
                _strings.Get(TextId.StartupSettingsPrompt),
                _strings.Get(TextId.LaunchAtSignIn),
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information);

            if (open == DialogResult.OK)
            {
                OpenStartupSettings();
            }

            return;
        }

        var target = state == StartupState.NotRegistered;
        _startupBusy = true;
        RefreshStartupItem();
        (bool Success, string? Error) result;
        try { result = await StartupRegistration.TrySetAsync(target); }
        finally { _startupBusy = false; }
        if (_exiting) return;
        RefreshStartupItem();
        if (result.Success)
        {
            RefreshStartupItem();
            if (target && StartupRegistration.GetState() is StartupState.DisabledByWindows or StartupState.Unknown)
                OpenStartupSettings();
            _log.Write(LogSource.Tray, $"Startup registration updated; observed state: {StartupRegistration.GetState()}.");
            return;
        }

        _log.Write(LogSource.Tray, $"Could not change launch at sign-in: {result.Error}");
        MessageBox.Show(
            _strings.Format(TextId.StartupSettingFailed, result.Error),
            _strings.Get(TextId.ApplicationName),
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
        using var about = new AboutForm(_version, icon, _paths.Root, _strings);
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
            await GetSupervisorDisposeTask().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _supervisorDisposeFailureReported = true;
            _log.Write(LogSource.Tray, $"Error during shutdown ({ex.GetType().Name}, HRESULT={ex.HResult:X8}).");
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

        var localized = _strings.Notification(notification);
        ShowBalloon(10_000, localized.Title, localized.Message, icon);
    }

    private void ShowStatusBalloon()
    {

        ShowBalloon(
            5_000,
            _strings.Format(TextId.StatusTitle, _strings.State(_snapshot)),
            BuildDetailLine(_snapshot),
            ToolTipIcon.None);
    }

    private void ShowBalloon(int timeout, string title, string message, ToolTipIcon icon, bool launch = false)
    {
        _launchBalloon = launch;
        _showBalloonTip(timeout, title, message, icon);
    }

    private void OnBalloonClicked()
    {
        if (_launchBalloon) return;

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
            OnUiThread(() =>
            {
                if (_exiting) return;
                MessageBox.Show(
                    _strings.Format(TextId.CommandFailed, ex.Message),
                    _strings.Get(TextId.ApplicationName),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            });
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
            ShowBalloon(3_000, "Remote Commander Tray", successMessage, ToolTipIcon.None);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            _log.Write(LogSource.Tray, $"Clipboard unavailable: {ex.Message}");
            MessageBox.Show(
                _strings.Get(TextId.ClipboardUnavailable),
                _strings.Get(TextId.ApplicationName),
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

    private Task GetSupervisorDisposeTask()
        => _supervisorDisposeTask ??= _supervisor.DisposeAsync().AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _menuTimer.Dispose();
            try
            {
                GetSupervisorDisposeTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // ExitApplication already reports this failure from ShutdownAsync. A
                // cached faulted disposal task must never escape ApplicationContext.Dispose
                // and get mislabeled by Program.Main as a startup failure.
                if (!_supervisorDisposeFailureReported)
                {
                    _supervisorDisposeFailureReported = true;
                    _log.Write(LogSource.Tray, $"Error during shutdown disposal ({ex.GetType().Name}, HRESULT={ex.HResult:X8}).");
                }
            }

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
