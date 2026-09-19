namespace RemoteCommanderTray.Core;

/// <summary>How the official CLI should be invoked.</summary>
/// <param name="FileName">Executable to launch.</param>
/// <param name="Arguments">Raw command line, already quoted for the target executable.</param>
/// <param name="Description">Human-readable form for logs and diagnostics.</param>
public sealed record AgentLaunchSpec(string FileName, string Arguments, string Description)
{
    public override string ToString() => Description;
}

/// <summary>Which official sub-command to run.</summary>
public enum AgentCommand
{
    /// <summary><c>desktop-commander remote</c> - the long-running device.</summary>
    Remote,

    /// <summary><c>desktop-commander remote --logout</c> - drops the saved credentials and exits.</summary>
    Logout,
}

/// <summary>
/// A running agent child process. Abstracted so the supervisor can be tested without
/// spawning anything, and so the Windows-only bits (job objects, hidden windows) stay
/// out of the portable core.
/// </summary>
public interface IAgentProcess : IDisposable
{
    /// <summary>OS process id, or null before start / after exit.</summary>
    int? ProcessId { get; }

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>A line arrived on stdout or stderr.</summary>
    event EventHandler<AgentOutputLine>? OutputReceived;

    /// <summary>The process ended. The argument is the exit code, when known.</summary>
    event EventHandler<int?>? Exited;

    /// <summary>Launches the process. Throws if it cannot be started.</summary>
    void Start();

    /// <summary>Asks the process to stop, then kills the whole tree if it will not.</summary>
    Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken = default);
}

/// <summary>One line of child output.</summary>
/// <param name="Text">The line, without its trailing newline.</param>
/// <param name="IsError">True when it came from stderr.</param>
public readonly record struct AgentOutputLine(string Text, bool IsError);

/// <summary>Result of a short-lived command such as <c>remote --logout</c>.</summary>
/// <param name="ExitCode">Process exit code, or null if it had to be killed.</param>
/// <param name="Output">Combined stdout and stderr.</param>
public readonly record struct AgentCommandResult(int? ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Creates agent processes. Replaced by a fake in tests.</summary>
public interface IAgentProcessFactory
{
    /// <summary>Creates (but does not start) a long-running agent process.</summary>
    IAgentProcess Create(AgentLaunchSpec spec);

    /// <summary>Runs a short command to completion and captures its output.</summary>
    Task<AgentCommandResult> RunOnceAsync(AgentLaunchSpec spec, TimeSpan timeout, CancellationToken cancellationToken = default);
}
