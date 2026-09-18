using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using RemoteCommanderTray.Core;
using RemoteCommanderTray.Windows;

[assembly: SupportedOSPlatform("windows")]

internal static class Program
{
    private static readonly string Self = Environment.ProcessPath!;
    private static readonly string Prefix = Path.GetFileNameWithoutExtension(Self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? $"\"{Assembly.GetExecutingAssembly().Location}\" " : string.Empty;
    private static AgentLaunchSpec Spec(string args) => new(Self, Prefix + args, "isolated lifecycle probe");
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length > 0) return await Helper(args);
        Environment.SetEnvironmentVariable("RCT_INTEGRATION_MARKER", "expected");
        var folder = Path.Combine(Path.GetTempPath(), "rct-win-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            await Check("live UTF-8 output, hidden console and inherited environment", OutputAndEnvironment);
            await Check("root exit drains descendants before Exited", () => RootExit(folder));
            await Check("Stop drains a live tree without stopping other jobs", () => StopIsolation(folder));
            await Check("Dispose drains a live tree", () => DisposeTree(folder));
            await Check("one-shot timeout and cancellation drain descendants", () => OneShots(folder));
            await Check("owner crash closes the job and kills descendants", () => OwnerCrash(folder));
            await Check("fast exits are observed repeatedly", FastExits);
            await Check("invalid executable fails closed", InvalidExecutable);
            await Check("oversized output is bounded", BoundedOutput);
            await Check("startup reads preserve external disable and unknown state", StartupRecords);
            Console.WriteLine($"All 10 native checks passed ({RuntimeInformation.ProcessArchitecture}).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task<int> Helper(string[] args)
    {
        switch (args[0])
        {
            case "--worker":
                await Task.Delay(20_000); return 0;
            case "--output":
                Console.WriteLine("Device ready: 喵");
                Console.Error.WriteLine("stderr-probe");
                Console.WriteLine($"console={GetConsoleWindow()};color={Environment.GetEnvironmentVariable("RCT_INTEGRATION_MARKER")}");
                Console.Out.Flush(); Console.Error.Flush();
                await Task.Delay(20_000); return 0;
            case "--oversize":
                Console.Write(new string('x', 80_000)); Console.WriteLine();
                Console.WriteLine("Device ready:"); return 0;
            case "--exit": return 7;
            case "--spawn":
                using (var worker = Process.Start(new ProcessStartInfo(Self, Prefix + "--worker")
                { UseShellExecute = false, CreateNoWindow = true })!)
                {
                    File.WriteAllText(args[1], worker.Id.ToString());
                    if (args.Length > 2 && args[2] == "stay") await Task.Delay(20_000);
                    return 0;
                }
            case "--owner":
                using (var child = new WindowsAgentProcess(Spec($"--spawn \"{args[1]}\" stay")))
                {
                    child.Start();
                    await Until(() => File.Exists(args[1]));
                    File.WriteAllText(args[1] + ".owner", child.ProcessId!.Value.ToString());
                    await Task.Delay(20_000); return 0;
                }
            default: throw new ArgumentException("Unknown probe command.");
        }
    }

    private static async Task Check(string name, Func<Task> check)
    {
        await check(); Console.WriteLine("PASS " + name);
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < DrainTimeout) await Task.Delay(10);
        Require(condition(), "Timed out waiting for native condition.");
    }
    private static bool Gone(int id)
    {
        try { using var process = Process.GetProcessById(id); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
    private static async Task<int> Marker(string path)
    {
        int id = 0;
        await Until(() =>
        {
            try { return File.Exists(path) && int.TryParse(File.ReadAllText(path), out id); }
            catch (IOException) { return false; }
        });
        return id;
    }

    private static async Task OutputAndEnvironment()
    {
        using var agent = new WindowsAgentProcess(Spec("--output"));
        var lines = new ConcurrentQueue<string>();
        agent.OutputReceived += (_, line) => lines.Enqueue(line.Text);
        agent.Start();
        await Until(() => lines.Any(x => x.Contains("console=")) && lines.Contains("stderr-probe"));
        Require(lines.Contains("Device ready: 喵"), "UTF-8 stdout was corrupted or not delivered live.");
        Require(lines.Contains("console=0;color=expected"), "The agent received a console window or wrong environment.");
        Require(!agent.HasExited, "Output must arrive while the process is still alive.");
        await agent.StopAsync(DrainTimeout);
    }

    private static async Task RootExit(string folder)
    {
        var marker = Path.Combine(folder, "root-exit.pid");
        using var agent = new WindowsAgentProcess(Spec($"--spawn \"{marker}\""));
        var ended = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.Exited += (_, code) => ended.TrySetResult(code);
        agent.Start();
        var worker = await Marker(marker);
        var exit = await ended.Task.WaitAsync(DrainTimeout);
        Require(exit == 0, "Root exit code must survive descendant cleanup.");
        Require(Gone(worker), "Exited was raised while a descendant was still running.");
        await agent.StopAsync(DrainTimeout); agent.Dispose(); // Idempotent after natural exit.
    }

    private static async Task StopIsolation(string folder)
    {
        var marker = Path.Combine(folder, "stop.pid");
        using var unrelated = new WindowsAgentProcess(Spec("--worker"));
        using var agent = new WindowsAgentProcess(Spec($"--spawn \"{marker}\" stay"));
        unrelated.Start(); agent.Start();
        var worker = await Marker(marker);
        await agent.StopAsync(DrainTimeout);
        Require(Gone(worker) && agent.HasExited, "Stop left part of its generation running.");
        Require(!unrelated.HasExited, "Stop killed another independent job.");
    }

    private static async Task DisposeTree(string folder)
    {
        var marker = Path.Combine(folder, "dispose.pid");
        using var agent = new WindowsAgentProcess(Spec($"--spawn \"{marker}\" stay"));
        agent.Start(); var worker = await Marker(marker);
        agent.Dispose();
        Require(Gone(worker), "Dispose left a descendant running.");
    }

    private static async Task OneShots(string folder)
    {
        var factory = new WindowsAgentProcessFactory();
        var timeoutMarker = Path.Combine(folder, "timeout.pid");
        var result = await factory.RunOnceAsync(Spec($"--spawn \"{timeoutMarker}\" stay"), TimeSpan.FromSeconds(2));
        Require(!result.Succeeded && Gone(await Marker(timeoutMarker)), "Timeout did not drain the one-shot tree.");
        var cancelMarker = Path.Combine(folder, "cancel.pid");
        using var cts = new CancellationTokenSource();
        var command = factory.RunOnceAsync(Spec($"--spawn \"{cancelMarker}\" stay"), TimeSpan.FromSeconds(15), cts.Token);
        var worker = await Marker(cancelMarker); cts.Cancel();
        try { await command; throw new InvalidOperationException("Cancellation was swallowed."); }
        catch (OperationCanceledException) { }
        Require(Gone(worker), "Cancellation abandoned a descendant.");
    }

    private static async Task OwnerCrash(string folder)
    {
        var marker = Path.Combine(folder, "owner.pid");
        using var owner = Process.Start(new ProcessStartInfo(Self, Prefix + $"--owner \"{marker}\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var child = await Marker(marker + ".owner");
            var worker = await Marker(marker);
            owner.Kill(); // Kill ONLY this test's owner, not its tree; job close must do the rest.
            await owner.WaitForExitAsync();
            await Until(() => Gone(child) && Gone(worker));
        }
        finally
        {
            if (!owner.HasExited) { owner.Kill(); await owner.WaitForExitAsync(); }
        }
    }

    private static async Task FastExits()
    {
        var factory = new WindowsAgentProcessFactory();
        for (var i = 0; i < 12; i++)
            Require((await factory.RunOnceAsync(Spec("--exit"), DrainTimeout)).ExitCode == 7, "A fast exit was lost.");
    }

    private static Task InvalidExecutable()
    {
        using var agent = new WindowsAgentProcess(new(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), "", "missing"));
        try { agent.Start(); throw new InvalidOperationException("Missing executable unexpectedly started."); }
        catch (System.ComponentModel.Win32Exception) { Require(agent.ProcessId is null, "A process escaped failed creation."); }
        return Task.CompletedTask;
    }

    private static async Task BoundedOutput()
    {
        var result = await new WindowsAgentProcessFactory().RunOnceAsync(Spec("--oversize"), DrainTimeout);
        Require(result.Succeeded && result.Output.Contains("oversized agent output omitted") && result.Output.Contains("Device ready:"),
            "Oversized output was not omitted or subsequent status was lost.");
        Require(result.Output.Length < 1024, "The bounded reader retained an oversized payload.");
    }

    private static Task StartupRecords()
    {
        var key = @"Software\RemoteCommanderTrayIntegration\" + Guid.NewGuid();
        var runPath = key + @"\Run"; var approvalPath = key + @"\StartupApproved";
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(runPath);
            using var approval = Registry.CurrentUser.CreateSubKey(approvalPath);
            run.SetValue("Probe", "old.exe");
            var disabled = new byte[12]; disabled[0] = 3;
            approval.SetValue("Probe", disabled);
            Require(StartupRegistration.GetState(runPath, approvalPath, "Probe") == StartupState.DisabledByWindows,
                "Disabled startup was reported enabled.");
            run.SetValue("Probe", "new.exe");
            Require(StartupRegistration.GetState(runPath, approvalPath, "Probe") == StartupState.DisabledByWindows,
                "Repointing Run must not clear Windows' disable decision.");
            Require(((byte[])approval.GetValue("Probe")!).SequenceEqual(disabled), "Read modified the Windows approval record.");
            approval.SetValue("Probe", "unknown format");
            Require(StartupRegistration.GetState(runPath, approvalPath, "Probe") == StartupState.Unknown,
                "Malformed approval must not be treated as enabled.");
            run.DeleteValue("Probe");
            Require(StartupRegistration.GetState(runPath, approvalPath, "Probe") == StartupState.NotRegistered,
                "Absent Run registration was misclassified.");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, false); }
        return Task.CompletedTask;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
