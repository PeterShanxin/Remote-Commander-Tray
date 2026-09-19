using Microsoft.Win32;
using RemoteCommanderTray.Core;
using RemoteCommanderTray.Windows;
using Xunit;
namespace RemoteCommanderTray.Windows.Tests;
public sealed class StartupTests
{
    [Fact] public void Registry_roundtrip_respects_external_disable_and_never_writes_approval()
    {
        var root = @"Software\RemoteCommanderTray.Tests\" + Guid.NewGuid().ToString("N");
        var run = root + @"\Run"; var approval = root + @"\Approved";
        const string name = "SyntheticTray";
        try
        {
            Assert.Equal(StartupState.NotRegistered, StartupRegistration.GetState(run, approval, name));
            Assert.True(StartupRegistration.TrySet(true, @"C:\test path\tray.exe", run, name, out _));
            Assert.Equal(StartupState.Enabled, StartupRegistration.GetState(run, approval, name));
            using var key = Registry.CurrentUser.CreateSubKey(approval);
            byte[] disabled = [3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8];
            key.SetValue(name, disabled, RegistryValueKind.Binary);
            Assert.Equal(StartupState.DisabledByWindows, StartupRegistration.GetState(run, approval, name));
            Assert.True(StartupRegistration.TrySet(true, @"C:\moved\tray.exe", run, name, out _));
            Assert.Equal(disabled, Assert.IsType<byte[]>(key.GetValue(name)));
            Assert.Equal(StartupState.DisabledByWindows, StartupRegistration.GetState(run, approval, name));
            key.SetValue(name, new byte[12] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            Assert.Equal(StartupState.Enabled, StartupRegistration.GetState(run, approval, name));
            key.SetValue(name, new byte[] { 2 }, RegistryValueKind.Binary);
            Assert.Equal(StartupState.Unknown, StartupRegistration.GetState(run, approval, name));
            Assert.True(StartupRegistration.TrySet(false, null, run, name, out _));
            Assert.Equal(StartupState.NotRegistered, StartupRegistration.GetState(run, approval, name));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }
    }
}
