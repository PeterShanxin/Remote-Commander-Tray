using System.Diagnostics;
using System.Reflection;
using RemoteCommanderTray.Core;
using RemoteCommanderTray.UI;
using RemoteCommanderTray.Windows;
using Xunit;
namespace RemoteCommanderTray.Windows.Tests;

public sealed class TrayTests
{
    [Fact]
    public async Task Queued_online_status_cannot_overwrite_a_newer_stop_on_the_tray()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "rct-tray-status-" + Guid.NewGuid());
            try
            {
                var paths = new AppPaths(root);
                paths.EnsureCreated();
                var settings = new TraySettings { StartAgentOnLaunch = false, NotificationsEnabled = false, Language = "en" };
                using var log = new RollingFileLog(paths.AgentLogFile, 65536, 1);
                using var tray = new TrayApplicationContext(paths, settings, log, "test", startedByWindows: true);
                var supervisor = Field<AgentSupervisor>(tray, "_supervisor");
                var machine = Field<AgentStateMachine>(supervisor, "_machine");
                var icon = Field<NotifyIcon>(tray, "_notifyIcon");
                Task.Run(() =>
                {
                    machine.OnProcessStarted();
                    machine.Apply(new AgentSignal(AgentSignalKind.DeviceReady));
                }).GetAwaiter().GetResult();

                // Stop renders directly on the UI thread before the queued Online callback runs.
                machine.OnProcessExited(null, userRequested: true);
                Assert.Contains("Stopped", icon.Text);
                Application.DoEvents();
                Assert.Contains("Stopped", icon.Text);
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact] public async Task Real_tray_controls_manage_only_synthetic_agents()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "rct-tray-ui-" + Guid.NewGuid());
            try
            {
                var paths = new AppPaths(root); paths.EnsureCreated();
                var settings = new TraySettings { StartAgentOnLaunch = false, NotificationsEnabled = false, Language = "en",
                    AgentExecutable = ProcessTests.Dotnet, AgentArguments = $"\"{ProcessTests.Probe}\" ready" };
                using var log = new RollingFileLog(paths.AgentLogFile, 65536, 1);
                using var tray = new TrayApplicationContext(paths, settings, log, "test", startedByWindows: true);
                var supervisor = Field<AgentSupervisor>(tray, "_supervisor");
                var toggle = Field<ToolStripMenuItem>(tray, "_toggleAgentItem");
                var restart = Field<ToolStripMenuItem>(tray, "_restartItem");
                var icon = Field<NotifyIcon>(tray, "_notifyIcon");
                Assert.True(icon.Visible); Assert.Equal("Start agent", toggle.Text);
                toggle.PerformClick(); Pump(() => supervisor.Snapshot.State == AgentState.Online && toggle.Enabled);
                Pump(() => icon.Text.Contains("Online", StringComparison.Ordinal));
                Assert.Equal("Stop agent", toggle.Text);
                restart.PerformClick(); Pump(() => supervisor.Snapshot.TotalRestartCount == 1 && supervisor.Snapshot.State == AgentState.Online && restart.Enabled);
                toggle.PerformClick(); Pump(() => supervisor.Snapshot.State == AgentState.Stopped && toggle.Enabled);
                Assert.False(supervisor.IsRunning);
                settings.AgentArguments = $"\"{ProcessTests.Probe}\" auth";
                toggle.PerformClick(); Pump(() => supervisor.Snapshot.UserCode == "ABCD-EFGH");
                Pump(() => Field<ToolStripMenuItem>(tray, "_openSignInItem").Enabled);
                Assert.Contains("ABCD-EFGH", Field<ToolStripMenuItem>(tray, "_copyCodeItem").Text);
                // Never click a real URL or use the clipboard. Exit/dispose must still drain.
                tray.Dispose(); Assert.False(supervisor.IsRunning); Assert.False(icon.Visible);
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_failures_are_reported_without_logging_raw_exception_text(bool exitFromMenu)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "rct-tray-dispose-" + Guid.NewGuid());
            TrayApplicationContext? tray = null;
            try
            {
                var paths = new AppPaths(root); paths.EnsureCreated();
                var settings = new TraySettings { StartAgentOnLaunch = false, NotificationsEnabled = false };
                var log = new RollingFileLog(paths.AgentLogFile, 65536, 1);
                tray = new TrayApplicationContext(paths, settings, log, "test", startedByWindows: true);

                // Dispose the real supervisor first, then inject a failed cleanup into
                // the normal Exit or Dispose path.
                const string privateOutput = "synthetic-private-output-marker";
                Field<AgentSupervisor>(tray, "_supervisor").DisposeAsync().AsTask().GetAwaiter().GetResult();
                SetField(tray, "_supervisorDisposeTask", Task.FromException(new IOException(privateOutput)));
                if (exitFromMenu)
                    typeof(TrayApplicationContext).GetMethod("ExitApplication", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(tray, null);

                var error = Record.Exception(tray.Dispose);
                tray = null;
                Assert.Null(error);
                Assert.Contains(log.Tail(10), line => line.Contains("IOException", StringComparison.Ordinal));
                Assert.DoesNotContain(log.Tail(10), line => line.Contains(privateOutput, StringComparison.Ordinal));
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally
            {
                tray?.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Switching_to_simplified_chinese_updates_menu_and_saves_setting()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "rct-tray-lang-" + Guid.NewGuid());
            try
            {
                var paths = new AppPaths(root); paths.EnsureCreated();
                var settings = new TraySettings { Language = "en", StartAgentOnLaunch = false, NotificationsEnabled = false };
                using var log = new RollingFileLog(paths.AgentLogFile, 65536, 1);
                using var tray = new TrayApplicationContext(paths, settings, log, "test", startedByWindows: true);
                var toggle = Field<ToolStripMenuItem>(tray, "_toggleAgentItem");
                var menu = Field<ToolStripMenuItem>(tray, "_languageItem");
                Assert.Equal("Start agent", toggle.Text);
                var chinese = menu.DropDownItems.OfType<ToolStripMenuItem>().Single(x => (string?)x.Tag == "zh-CN");
                chinese.PerformClick();
                Assert.Equal("启动 Agent", toggle.Text);
                var saved = new SettingsStore(paths.SettingsFile).Load();
                Assert.Equal("zh-CN", saved.Language);
                Assert.True(chinese.Checked);
                using (var about = new AboutForm("test", SystemIcons.Application, paths.Root, new UiStrings(saved.Language)))
                {
                    Assert.Equal("关于 Remote Commander Tray", about.Text);
                    Assert.Equal("关闭", Assert.IsType<Button>(about.AcceptButton).Text);
                    Assert.True(about.AutoSize);
                }
                tray.Dispose();

                using var reopened = new TrayApplicationContext(paths, saved, log, "test", startedByWindows: true);
                var reopenedToggle = Field<ToolStripMenuItem>(reopened, "_toggleAgentItem");
                Assert.Equal("启动 Agent", reopenedToggle.Text);
                var reopenedMenu = Field<ToolStripMenuItem>(reopened, "_languageItem");
                var english = reopenedMenu.DropDownItems.OfType<ToolStripMenuItem>()
                    .Single(x => (string?)x.Tag == "en");
                english.PerformClick();
                Assert.Equal("Start agent", reopenedToggle.Text);
                Assert.Equal("en", new SettingsStore(paths.SettingsFile).Load().Language);
                Assert.True(english.Checked);
                var followWindows = reopenedMenu.DropDownItems.OfType<ToolStripMenuItem>()
                    .Single(x => (string?)x.Tag == "auto");
                followWindows.PerformClick();
                var windowsLanguages = WindowsUiLanguages.GetPreferred();
                Assert.NotEmpty(windowsLanguages);
                var expected = UiStrings.UseSimplifiedChinese(windowsLanguages) ? "启动 Agent" : "Start agent";
                Assert.Equal(expected, reopenedToggle.Text);
                Assert.Equal("auto", new SettingsStore(paths.SettingsFile).Load().Language);
                Assert.True(followWindows.Checked);
                reopened.Dispose();
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Theory]
    [InlineData(false, true, true, "en", "Remote Commander Tray is running", "Connecting to Desktop Commander…")]
    [InlineData(false, true, false, "zh-CN", "Remote Commander Tray 已启动", "打开托盘菜单即可启动 Agent。")]
    [InlineData(false, false, true, "en", null, null)]
    [InlineData(true, true, true, "en", null, null)]
    public async Task Only_manual_launch_with_notifications_enabled_confirms_startup(
        bool startedByWindows, bool notificationsEnabled, bool startAgent,
        string language, string? expectedTitle, string? expectedMessage)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "rct-tray-launch-" + Guid.NewGuid());
            try
            {
                var paths = new AppPaths(root);
                paths.EnsureCreated();
                var settings = new TraySettings
                {
                    StartAgentOnLaunch = startAgent,
                    NotificationsEnabled = notificationsEnabled,
                    Language = language,
                    AgentExecutable = ProcessTests.Dotnet,
                    AgentArguments = $"\"{ProcessTests.Probe}\" ready",
                };
                using var log = new RollingFileLog(paths.AgentLogFile, 65536, 1);
                var balloons = new List<(string Title, string Message, ToolTipIcon Icon)>();
                using var tray = new TrayApplicationContext(paths, settings, log, "test", startedByWindows,
                    (_, title, message, icon) => balloons.Add((title, message, icon)));
                if (expectedTitle is null) Assert.Empty(balloons);
                else Assert.Equal((expectedTitle, expectedMessage, ToolTipIcon.Info), Assert.Single(balloons));

                if (startAgent)
                {
                    var supervisor = Field<AgentSupervisor>(tray, "_supervisor");
                    var restart = Field<ToolStripMenuItem>(tray, "_restartItem");
                    Pump(() => supervisor.Snapshot.State == AgentState.Online && restart.Enabled);
                    restart.PerformClick();
                    Pump(() => supervisor.Snapshot.State == AgentState.Online && supervisor.Snapshot.TotalRestartCount == 1 && restart.Enabled);
                    Assert.Equal(expectedTitle is null ? 0 : 1, balloons.Count);
                }
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Pump(Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready() && elapsed.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(10); }
        Application.DoEvents(); Assert.True(ready(), "Tray did not reach its expected state.");
    }
}
