using RemoteCommanderTray.Core;
using Xunit;

namespace RemoteCommanderTray.Core.Tests;

public class AgentCommandResolverTests
{
    private const string NodeExe = @"C:\Program Files\nodejs\node.exe";
    private const string PackageEntry =
        @"C:\Users\peter\AppData\Roaming\npm\node_modules\@wonderwhy-er\desktop-commander\dist\index.js";

    [Fact]
    public void Prefers_node_plus_the_global_package()
    {
        var env = new FakeEnvironment
        {
            Variables =
            {
                ["PATH"] = @"C:\Windows;C:\Program Files\nodejs",
                ["APPDATA"] = @"C:\Users\peter\AppData\Roaming",
                ["ProgramFiles"] = @"C:\Program Files",
            },
            Files = { NodeExe, PackageEntry, @"C:\Users\peter\AppData\Roaming\npm\desktop-commander.cmd" },
        };

        var resolution = new AgentCommandResolver(env).Resolve(AgentCommand.Remote, new TraySettings());

        Assert.True(resolution.Found);
        Assert.Equal(NodeExe, resolution.Spec!.FileName);
        Assert.Equal($"\"{PackageEntry}\" remote", resolution.Spec.Arguments);
    }

    [Fact]
    public void Logout_uses_the_official_flag()
    {
        var env = new FakeEnvironment
        {
            Variables = { ["PATH"] = @"C:\Program Files\nodejs", ["APPDATA"] = @"C:\Users\peter\AppData\Roaming" },
            Files = { NodeExe, PackageEntry },
        };

        var resolution = new AgentCommandResolver(env).Resolve(AgentCommand.Logout, new TraySettings());

        Assert.EndsWith("remote --logout", resolution.Spec!.Arguments);
    }

    [Fact]
    public void Falls_back_to_the_npm_shim_through_cmd()
    {
        var env = new FakeEnvironment
        {
            Variables =
            {
                ["PATH"] = @"C:\Users\peter\AppData\Roaming\npm",
                ["ComSpec"] = @"C:\Windows\System32\cmd.exe",
            },
            Files = { @"C:\Users\peter\AppData\Roaming\npm\desktop-commander.cmd", @"C:\Windows\System32\cmd.exe" },
        };

        var resolution = new AgentCommandResolver(env).Resolve(AgentCommand.Remote, new TraySettings());

        Assert.Equal(@"C:\Windows\System32\cmd.exe", resolution.Spec!.FileName);
        Assert.Equal(
            "/d /s /c \"\"C:\\Users\\peter\\AppData\\Roaming\\npm\\desktop-commander.cmd\" remote\"",
            resolution.Spec.Arguments);
    }

    [Fact]
    public void Falls_back_to_npx_when_nothing_is_installed_globally()
    {
        var env = new FakeEnvironment
        {
            Variables = { ["PATH"] = @"C:\Program Files\nodejs" },
            Files = { @"C:\Program Files\nodejs\npx.cmd" },
        };

        var resolution = new AgentCommandResolver(env).Resolve(AgentCommand.Remote, new TraySettings());

        Assert.Contains(AgentCommandResolver.PackageName, resolution.Spec!.Arguments);
        Assert.Contains("-y", resolution.Spec.Arguments);
    }

    [Fact]
    public void An_explicit_override_wins()
    {
        var env = new FakeEnvironment
        {
            Variables = { ["PATH"] = @"C:\Program Files\nodejs", ["APPDATA"] = @"C:\Users\peter\AppData\Roaming" },
            Files = { NodeExe, PackageEntry },
        };
        var settings = new TraySettings
        {
            AgentExecutable = @"D:\tools\dc.exe",
            AgentArguments = "--verbose",
        };

        var resolution = new AgentCommandResolver(env).Resolve(AgentCommand.Remote, settings);

        Assert.Equal(@"D:\tools\dc.exe", resolution.Spec!.FileName);
        Assert.Equal("--verbose remote", resolution.Spec.Arguments);
    }

    [Fact]
    public void Explains_itself_when_nothing_is_found()
    {
        var resolution = new AgentCommandResolver(new FakeEnvironment())
            .Resolve(AgentCommand.Remote, new TraySettings());

        Assert.False(resolution.Found);
        Assert.Contains("npm install -g", resolution.Problem);
    }

    private sealed class FakeEnvironment : IAgentEnvironment
    {
        public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => Files.Contains(path);

        public string? GetVariable(string name) => Variables.GetValueOrDefault(name);
    }
}
