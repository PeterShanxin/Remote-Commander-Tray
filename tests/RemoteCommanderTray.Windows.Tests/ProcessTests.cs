using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteCommanderTray.Core;
using RemoteCommanderTray.Windows;
using Xunit;
[assembly: SupportedOSPlatform("windows")]
[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace RemoteCommanderTray.Windows.Tests;

public sealed class ProcessTests
{
    internal static string Dotnet => Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
    internal static string Probe => Path.Combine(AppContext.BaseDirectory, "probe", "RemoteCommanderTray.ProcessProbe.dll");
    internal static AgentLaunchSpec Command(string mode, string? marker = null)
        => new(Dotnet, $"\"{Probe}\" {mode} \"{marker}\"", "synthetic process probe");

    [Fact] public async Task Born_in_job_without_a_console_from_the_first_instruction()
    {
        var result = await new WindowsAgentProcessFactory().RunOnceAsync(Command("birth"), TimeSpan.FromSeconds(10));
        Assert.True(result.Succeeded, result.Output);
        Assert.Contains("inJob=True;console=False", result.Output);
    }
    [Fact] public async Task Immediate_exit_is_always_observed()
    {
        for (var i = 0; i < 12; i++)
        {
            using var process = new WindowsAgentProcess(Command("exit"));
            process.Start();
            Assert.Equal(0, await process.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(process.HasExited);
            Assert.Equal(0u, process.ActiveProcessCount);
        }
    }
    [Fact] public async Task Starts_a_contained_agent_when_windows_returns_no_profile_directory()
    {
        using var process = new WindowsAgentProcess(Command("birth"));
        var birthReport = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputReceived += (_, line) => birthReport.TrySetResult(line.Text);
        process.Start(string.Empty);
        Assert.Equal(0, await process.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("inJob=True;console=False", await birthReport.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }
    [Fact] public async Task Natural_parent_exit_drains_orphan_before_completion()
    {
        var marker = Path.GetTempFileName();
        try
        {
            using var process = new WindowsAgentProcess(Command("orphan", marker));
            process.Start();
            Assert.Equal(0, await process.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0u, process.ActiveProcessCount);
            Assert.False(IsAlive(ReadPid(marker)));
            await process.StopAsync(TimeSpan.FromSeconds(2)); // idempotent after root exit
        }
        finally { CleanupProbeChild(marker); }
    }
    [Fact] public async Task Stop_and_dispose_are_generation_scoped_and_drained()
    {
        var marker = Path.GetTempFileName();
        using var unrelated = new WindowsAgentProcess(Command("child"));
        unrelated.Start();
        try
        {
            using var process = new WindowsAgentProcess(Command("tree", marker));
            process.Start();
            await WaitForPid(marker);
            await process.StopAsync(TimeSpan.FromSeconds(5));
            Assert.False(IsAlive(ReadPid(marker)));
            Assert.Equal(0u, process.ActiveProcessCount);
            Assert.False(unrelated.HasExited);
        }
        finally { CleanupProbeChild(marker); }
    }
    [Fact] public async Task Dispose_alone_terminates_an_active_generation()
    {
        var marker = Path.GetTempFileName();
        var process = new WindowsAgentProcess(Command("tree", marker));
        try
        {
            process.Start(); await WaitForPid(marker); process.Dispose();
            Assert.False(IsAlive(ReadPid(marker)));
            Assert.True(process.HasExited);
            process.Dispose();
        }
        finally { process.Dispose(); CleanupProbeChild(marker); }
    }
    [Fact] public async Task Restart_and_quit_drain_grandchildren_with_unicode_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "rct-进程 树-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        using var log = new RollingFileLog(Path.Combine(root, "agent.log"), 65536, 1);
        var marker = Path.Combine(root, "child.pid");
        var settings = new TraySettings
        {
            AgentExecutable = Dotnet,
            AgentArguments = $"\"{Probe}\" grandtree \"{marker}\"",
        };
        var supervisor = new AgentSupervisor(new(), new WindowsAgentProcessFactory(), new(), () => settings, log);
        try
        {
            await supervisor.StartAsync();
            await WaitForPid(marker);
            await WaitForPid(marker + ".grandchild");
            var oldChild = ReadPid(marker);
            var oldGrandchild = ReadPid(marker + ".grandchild");
            File.Delete(marker);
            File.Delete(marker + ".grandchild");

            await supervisor.RestartAsync();
            Assert.False(IsAlive(oldChild));
            Assert.False(IsAlive(oldGrandchild));
            await WaitForPid(marker);
            await WaitForPid(marker + ".grandchild");
            var newChild = ReadPid(marker);
            var newGrandchild = ReadPid(marker + ".grandchild");
            await supervisor.DisposeAsync();
            Assert.False(IsAlive(newChild));
            Assert.False(IsAlive(newGrandchild));
        }
        finally
        {
            await supervisor.DisposeAsync();
            Directory.Delete(root, true);
        }
    }
    [Fact] public async Task One_shot_timeout_cleans_the_complete_generation()
    {
        var marker = Path.GetTempFileName();
        try
        {
            var result = await new WindowsAgentProcessFactory().RunOnceAsync(Command("tree", marker), TimeSpan.FromSeconds(2));
            Assert.False(result.Succeeded);
            Assert.False(IsAlive(ReadPid(marker)));
        }
        finally { CleanupProbeChild(marker); }
    }
    [Fact] public async Task Cancelled_one_shot_cleans_before_returning()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsAgentProcessFactory().RunOnceAsync(Command("child"), TimeSpan.FromSeconds(10), cancelled.Token));
    }
    [Fact] public async Task Concurrent_utf8_streams_are_fully_drained_without_capture_corruption()
    {
        var result = await new WindowsAgentProcessFactory().RunOnceAsync(Command("streams"), TimeSpan.FromSeconds(10));
        Assert.True(result.Succeeded, result.Output);
        Assert.Equal(300, result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("OUT-149-猫", result.Output); Assert.Contains("ERR-149-猫", result.Output);
    }
    [Fact] public async Task Oversized_output_does_not_make_unbounded_lines()
    {
        var result = await new WindowsAgentProcessFactory().RunOnceAsync(Command("long"), TimeSpan.FromSeconds(10));
        Assert.True(result.Succeeded);
        Assert.Contains("oversized agent output omitted", result.Output);
        Assert.True(result.Output.Length < 200);
    }
    [Fact] public void Missing_executable_fails_without_an_uncontained_fallback()
    {
        using var process = new WindowsAgentProcess(new("rct-does-not-exist.exe", "", "missing probe"));
        Assert.Throws<FileNotFoundException>(process.Start);
        Assert.True(process.HasExited); Assert.Equal(0u, process.ActiveProcessCount);
    }
    private static int ReadPid(string marker) => int.Parse(File.ReadAllText(marker));
    private static bool IsAlive(int id)
    {
        try { using var p = Process.GetProcessById(id); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static async Task WaitForPid(string marker)
    {
        var deadline = Stopwatch.StartNew();
        var info = new FileInfo(marker);
        while ((!info.Exists || info.Length == 0) && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(20);
            info.Refresh();
        }
        Assert.True(info.Exists && info.Length > 0, "Probe did not report its own child.");
    }
    private static void CleanupProbeChild(string marker)
    {
        // Only the synthetic child's PID, never a global dotnet/node image-name kill.
        if (int.TryParse(File.ReadAllText(marker), out var id))
        {
            try { using var p = Process.GetProcessById(id); if (!p.HasExited) { p.Kill(); p.WaitForExit(5000); } }
            catch (ArgumentException) { }
        }
        File.Delete(marker);
    }
}
