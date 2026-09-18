using System.Collections.Concurrent;
using RemoteCommanderTray.Core;

namespace RemoteCommanderTray.Core.Tests;

/// <summary>An agent that never exists: it emits whatever a test tells it to.</summary>
internal sealed class FakeAgentProcess : IAgentProcess
{
    private int _exited;

    public FakeAgentProcess(AgentLaunchSpec spec) => Spec = spec;

    public AgentLaunchSpec Spec { get; }

    public int? ProcessId { get; private set; }

    public bool HasExited => Volatile.Read(ref _exited) != 0;

    public bool Started { get; private set; }

    public bool StopRequested { get; private set; }

    /// <summary>Set to make <see cref="Start"/> throw, standing in for a bad command line.</summary>
    public Exception? StartFailure { get; init; }

    /// <summary>
    /// Output the process emits from inside <see cref="Start"/>, before it returns. The
    /// real process begins reading stdout during Start, so this is not a contrived case.
    /// </summary>
    public IReadOnlyList<string> OutputDuringStart { get; init; } = [];

    /// <summary>When set, the process exits from inside <see cref="Start"/>.</summary>
    public int? ExitDuringStart { get; init; }

    public event EventHandler<AgentOutputLine>? OutputReceived;

    public event EventHandler<int?>? Exited;

    public void Start()
    {
        if (StartFailure is not null)
        {
            throw StartFailure;
        }

        Started = true;
        ProcessId = Environment.ProcessId;

        foreach (var line in OutputDuringStart)
        {
            Emit(line);
        }

        if (ExitDuringStart is { } code)
        {
            Crash(code);
        }
    }

    public Exception? StopFailure { get; set; }

    public Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        if (StopFailure is not null) return Task.FromException(StopFailure);
        StopRequested = true;
        Volatile.Write(ref _exited, 1);
        return Task.CompletedTask;
    }

    /// <summary>Pushes a line of output as the real child would.</summary>
    public void Emit(string line, bool isError = false)
        => OutputReceived?.Invoke(this, new AgentOutputLine(line, isError));

    /// <summary>Ends the process, as a crash would.</summary>
    public void Crash(int exitCode = 1)
    {
        if (Interlocked.Exchange(ref _exited, 1) != 0)
        {
            return;
        }

        Exited?.Invoke(this, exitCode);
    }

    public void Dispose()
    {
    }
}

/// <summary>Hands out <see cref="FakeAgentProcess"/> instances and records logout calls.</summary>
internal sealed class FakeAgentProcessFactory : IAgentProcessFactory
{
    private readonly ConcurrentQueue<FakeAgentProcess> _created = new();

    public Exception? NextStartFailure { get; set; }
    public Exception? CreateFailure { get; set; }
    public Exception? LogoutFailure { get; set; }

    /// <summary>Applied to the next process created.</summary>
    public IReadOnlyList<string> NextOutputDuringStart { get; set; } = [];

    /// <summary>Applied to the next process created.</summary>
    public int? NextExitDuringStart { get; set; }

    public AgentCommandResult LogoutResult { get; set; } = new(0, "Logged out locally.");

    public List<AgentLaunchSpec> OneShotCommands { get; } = [];

    public IReadOnlyCollection<FakeAgentProcess> Created => _created;

    public FakeAgentProcess Latest => _created.Last();

    public int CreatedCount => _created.Count;

    public IAgentProcess Create(AgentLaunchSpec spec)
    {
        if (CreateFailure is not null) throw CreateFailure;
        var process = new FakeAgentProcess(spec)
        {
            StartFailure = NextStartFailure,
            OutputDuringStart = NextOutputDuringStart,
            ExitDuringStart = NextExitDuringStart,
        };

        // One-shot: only the next generation gets the injected behaviour, so a test can
        // exercise a bad first start followed by a healthy restart.
        NextOutputDuringStart = [];
        NextExitDuringStart = null;

        _created.Enqueue(process);
        return process;
    }

    public Task<AgentCommandResult> RunOnceAsync(
        AgentLaunchSpec spec,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        OneShotCommands.Add(spec);
        if (LogoutFailure is not null) return Task.FromException<AgentCommandResult>(LogoutFailure);
        return Task.FromResult(LogoutResult);
    }
}

/// <summary>An environment where the official CLI is always installed.</summary>
internal sealed class InstalledAgentEnvironment : IAgentEnvironment
{
    public const string Node = @"C:\Program Files\nodejs\node.exe";

    public const string Entry =
        @"C:\Users\test\AppData\Roaming\npm\node_modules\@wonderwhy-er\desktop-commander\dist\index.js";

    public bool FileExists(string path) => path is Node or Entry;

    public string? GetVariable(string name) => name switch
    {
        "PATH" => @"C:\Program Files\nodejs",
        "APPDATA" => @"C:\Users\test\AppData\Roaming",
        _ => null,
    };
}
