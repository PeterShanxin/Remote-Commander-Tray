using System.Diagnostics;
using System.Reflection;
using RemoteCommanderTray.Core;
using RemoteCommanderTray.UI;
using Xunit;
namespace RemoteCommanderTray.Windows.Tests;

public sealed class TrayTests
{
    [Fact] public async Task Real_tray_controls_manage_only_synthetic_agents()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "rct-tray-ui-" + Guid.NewGuid());
            try
            {
                var paths = new AppPaths(root); paths.EnsureCreated();
                var settings = new TraySettings { StartAgentOnLaunch = false, NotificationsEnabled = false,
                    AgentExecutable = ProcessTests.Dotnet, AgentArguments = $"\"{ProcessTests.Probe}\" ready" };
                using var log = new RollingFileLog(paths.AgentLogFile, 65536, 1);
                using var tray = new TrayApplicationContext(paths, settings, log, "test");
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
    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Pump(Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready() && elapsed.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(10); }
        Application.DoEvents(); Assert.True(ready(), "Tray did not reach its expected state.");
    }
}
