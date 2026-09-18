namespace RemoteCommanderTray.Core;

/// <summary>Only generated operational event names enter the ordinary log.</summary>
/// <remarks>
/// CLI output can contain arbitrary tool results, even short plain text or a familiar
/// status prefix followed by credentials. Neither heuristics nor regex redaction can
/// make those payloads safe. Do not persist rawLine or signal.Value here.
/// </remarks>
public static class AgentLogPolicy
{
    public static string Sanitize(string rawLine, AgentSignal signal) => signal.Kind switch
    {
        AgentSignalKind.None => "<unrecognized agent output omitted>",
        AgentSignalKind.ToolPayload => "<tool result omitted>",
        AgentSignalKind.ToolCall => "Tool call observed (name and payload omitted).",
        _ => $"Agent status: {signal.Kind}.",
    };
}
