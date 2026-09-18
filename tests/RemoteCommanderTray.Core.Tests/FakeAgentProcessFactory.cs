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
    }

    public Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
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

    public AgentCommandResult LogoutResult { get; set; } = new(0, "Logged out locally.");

    public List<AgentLaunchSpec> OneShotCommands { get; } = [];

    public IReadOnlyCollection<FakeAgentProcess> Created => _created;

    public FakeAgentProcess Latest => _created.Last();

    public int CreatedCount => _created.Count;

    public IAgentProcess Create(AgentLaunchSpec spec)
    {
        var process = new FakeAgentProcess(spec) { StartFailure = NextStartFailure };
        _created.Enqueue(process);
        return process;
    }

    public Task<AgentCommandResult> RunOnceAsync(
        AgentLaunchSpec spec,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        OneShotCommands.Add(spec);
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
