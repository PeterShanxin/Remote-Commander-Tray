using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);
var mode = args.FirstOrDefault() ?? "exit";
var marker = args.ElementAtOrDefault(1);
if (mode == "child") { await Task.Delay(TimeSpan.FromSeconds(30)); return; }
if (mode == "birth")
{
    if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob)) throw new InvalidOperationException();
    Console.WriteLine($"inJob={inJob};console={GetConsoleWindow() != IntPtr.Zero}");
    return;
}
if (mode is "orphan" or "tree")
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    // Tests launch via dotnet.exe, not an apphost. Keep the child in the same runtime.
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("child");
    using var child = Process.Start(start)!;
    File.WriteAllText(marker!, child.Id.ToString());
    if (mode == "orphan") return;
    await Task.Delay(TimeSpan.FromSeconds(30));
    return;
}
if (mode == "streams")
{
    await Task.WhenAll(Task.Run(() => { for (var i = 0; i < 150; i++) Console.WriteLine($"OUT-{i}-猫"); }),
        Task.Run(() => { for (var i = 0; i < 150; i++) Console.Error.WriteLine($"ERR-{i}-猫"); }));
    return;
}
if (mode == "long") { Console.WriteLine(new string('x', 100_000)); return; }
if (mode == "auth")
{
    Console.WriteLine("Starting device authorization flow...");
    Console.WriteLine("1. Verify this device in your browser:");
    Console.WriteLine("https://mcp.desktopcommander.app/device/verify");
    Console.WriteLine("2. Make sure the code matches:");
    Console.WriteLine("ABCD-EFGH");
    await Task.Delay(TimeSpan.FromSeconds(30)); return;
}
if (mode == "ready") { Console.WriteLine("Device ready:"); await Task.Delay(TimeSpan.FromSeconds(30)); }

[DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
[DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
[DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
