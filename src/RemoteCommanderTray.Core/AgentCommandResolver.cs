namespace RemoteCommanderTray.Core;

/// <summary>Filesystem and environment lookups, abstracted so resolution can be unit-tested.</summary>
public interface IAgentEnvironment
{
    bool FileExists(string path);

    string? GetVariable(string name);
}

/// <summary>Real process environment.</summary>
public sealed class SystemAgentEnvironment : IAgentEnvironment
{
    public bool FileExists(string path) => File.Exists(path);

    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);
}

/// <summary>Outcome of looking for the official CLI.</summary>
/// <param name="Spec">How to launch it, or null when it was not found.</param>
/// <param name="Problem">Why it was not found, when <paramref name="Spec"/> is null.</param>
public readonly record struct AgentResolution(AgentLaunchSpec? Spec, string? Problem)
{
    public bool Found => Spec is not null;
}

/// <summary>
/// Works out how to start <c>desktop-commander remote</c> on this machine.
/// </summary>
/// <remarks>
/// Preference order, best first:
/// <list type="number">
/// <item>an explicit override in <c>settings.json</c>;</item>
/// <item><c>node.exe</c> plus the globally installed package's <c>dist/index.js</c> - no shell,
/// no shim, so stdout redirection and process-tree teardown behave predictably;</item>
/// <item>the <c>desktop-commander.cmd</c> npm shim, run through <c>cmd.exe</c>;</item>
/// <item><c>npx</c>, which downloads the package on first use.</item>
/// </list>
/// </remarks>
public sealed class AgentCommandResolver
{
    public const string PackageName = "@wonderwhy-er/desktop-commander";

    private static readonly string[] PackageRelativePath =
        ["node_modules", "@wonderwhy-er", "desktop-commander", "dist", "index.js"];

    // Windows path conventions are hard-coded rather than taken from Path.Combine and
    // Path.PathSeparator: the paths being resolved are always Windows paths, whatever
    // host the unit tests happen to run on.
    private const char DirectorySeparator = '\\';
    private const char PathListSeparator = ';';

    private readonly IAgentEnvironment _environment;

    public AgentCommandResolver(IAgentEnvironment? environment = null)
        => _environment = environment ?? new SystemAgentEnvironment();

    public AgentResolution Resolve(AgentCommand command, TraySettings settings)
    {
        var suffix = command == AgentCommand.Logout ? "remote --logout" : "remote";

        if (!string.IsNullOrWhiteSpace(settings.AgentExecutable))
        {
            var exe = settings.AgentExecutable.Trim();
            var prefix = string.IsNullOrWhiteSpace(settings.AgentArguments)
                ? string.Empty
                : settings.AgentArguments.Trim() + " ";
            return new AgentResolution(
                new AgentLaunchSpec(exe, prefix + suffix, $"{Quote(exe)} {prefix}{suffix}"),
                null);
        }

        var node = FindNode();
        var entryPoint = FindPackageEntryPoint();
        if (node is not null && entryPoint is not null)
        {
            var arguments = $"{Quote(entryPoint)} {suffix}";
            return new AgentResolution(
                new AgentLaunchSpec(node, arguments, $"{Quote(node)} {arguments}"),
                null);
        }

        var shim = FindOnPath("desktop-commander.cmd") ?? FindOnPath("desktop-commander.exe");
        if (shim is not null)
        {
            return new AgentResolution(WrapInCmd($"{Quote(shim)} {suffix}"), null);
        }

        var npx = FindOnPath("npx.cmd") ?? FindOnPath("npx.exe");
        if (npx is not null)
        {
            return new AgentResolution(WrapInCmd($"{Quote(npx)} -y {PackageName}@latest {suffix}"), null);
        }

        return new AgentResolution(
            null,
            $"Could not find Desktop Commander. Install Node.js and run \"npm install -g {PackageName}\", "
            + "or set \"agentExecutable\" in settings.json.");
    }

    private AgentLaunchSpec WrapInCmd(string commandLine)
    {
        var comSpec = _environment.GetVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(comSpec) || !_environment.FileExists(comSpec))
        {
            comSpec = "cmd.exe";
        }

        // /d skips AutoRun scripts, /s keeps the outer quotes intact for /c.
        return new AgentLaunchSpec(comSpec, $"/d /s /c \"{commandLine}\"", commandLine);
    }

    private string? FindNode()
        => FindOnPath("node.exe")
           ?? FirstExisting(
               Combine(_environment.GetVariable("ProgramFiles"), "nodejs", "node.exe"),
               Combine(_environment.GetVariable("ProgramFiles(x86)"), "nodejs", "node.exe"),
               Combine(_environment.GetVariable("LOCALAPPDATA"), "Programs", "nodejs", "node.exe"));

    private string? FindPackageEntryPoint()
    {
        var roots = new List<string?>
        {
            _environment.GetVariable("NPM_CONFIG_PREFIX"),
            Combine(_environment.GetVariable("APPDATA"), "npm"),
            Combine(_environment.GetVariable("ProgramFiles"), "nodejs"),
            Combine(_environment.GetVariable("LOCALAPPDATA"), "Programs", "nodejs"),
            Combine(_environment.GetVariable("ProgramData"), "npm"),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var candidate = Combine(root, PackageRelativePath);
            if (candidate is not null && _environment.FileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string? FindOnPath(string fileName)
    {
        var path = _environment.GetVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(PathListSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }

            var candidate = Combine(trimmed, fileName);
            if (candidate is not null && _environment.FileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string? FirstExisting(params string?[] candidates)
        => candidates.FirstOrDefault(c => c is not null && _environment.FileExists(c));

    private static string? Combine(string? root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var combined = root.TrimEnd(DirectorySeparator, '/');
        foreach (var part in parts)
        {
            combined += DirectorySeparator + part;
        }

        return combined;
    }

    /// <summary>
    /// Always quotes, rather than only when the path contains a space: "C:\Program
    /// Files" is the obvious case, but an unquoted path also lets cmd.exe interpret
    /// characters like &amp; and ^ that are legal in a Windows folder name.
    /// </summary>
    internal static string Quote(string value) => $"\"{value}\"";
}
