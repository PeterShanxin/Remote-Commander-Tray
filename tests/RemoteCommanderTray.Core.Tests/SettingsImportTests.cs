using RemoteCommanderTray.Core;
using Xunit;

namespace RemoteCommanderTray.Core.Tests;

public sealed class SettingsImportTests
{
    [Fact]
    public void Read_only_import_preserves_corrupt_source_and_existing_backup()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrayImport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        try
        {
            File.WriteAllText(path, "{broken");
            File.WriteAllText(path + ".invalid", "older backup");
            var settings = new SettingsStore(path).Load(quarantineInvalid: false);
            Assert.True(settings.StartAgentOnLaunch);
            Assert.Equal("{broken", File.ReadAllText(path));
            Assert.Equal("older backup", File.ReadAllText(path + ".invalid"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Read_only_import_retains_valid_preferences_without_rewriting_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrayImport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        const string original = "{\"startAgentOnLaunch\":false,\"notificationsEnabled\":false}";
        try
        {
            File.WriteAllText(path, original);
            var settings = new SettingsStore(path).Load(quarantineInvalid: false);
            Assert.False(settings.StartAgentOnLaunch);
            Assert.False(settings.NotificationsEnabled);
            Assert.Equal(original, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".invalid"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
