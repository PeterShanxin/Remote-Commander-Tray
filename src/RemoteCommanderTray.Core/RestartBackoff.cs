namespace RemoteCommanderTray.Core;

/// <summary>
/// The restart schedule for a crashed agent: 5s, 15s, 30s, then 60s forever.
/// </summary>
/// <remarks>
/// The delays are deliberately fixed rather than exponential-with-jitter: the agent is
/// a local process, the ceiling is one minute, and a predictable schedule is easier to
/// explain in a diagnostics dump than a random one.
/// </remarks>
public sealed class RestartBackoff
{
    /// <summary>Default schedule from the v0.1 spec.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultSchedule =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    private readonly IReadOnlyList<TimeSpan> _schedule;
    private int _attempt;

    public RestartBackoff(IReadOnlyList<TimeSpan>? schedule = null)
    {
        _schedule = schedule is { Count: > 0 } ? schedule : DefaultSchedule;
    }

    /// <summary>Number of consecutive failures recorded since the last <see cref="Reset"/>.</summary>
    public int Attempt => _attempt;

    /// <summary>Records a failure and returns how long to wait before the next start.</summary>
    public TimeSpan NextDelay()
    {
        var index = Math.Min(_attempt, _schedule.Count - 1);
        _attempt++;
        return _schedule[index];
    }

    /// <summary>Clears the failure streak. Called once a run has stayed up long enough to count as healthy.</summary>
    public void Reset() => _attempt = 0;
}
