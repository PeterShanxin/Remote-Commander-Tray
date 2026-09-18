namespace RemoteCommanderTray.Core;

/// <summary>
/// Decides what, if anything, a line of agent output may contribute to the ordinary log.
/// </summary>
/// <remarks>
/// <para>
/// The official CLI logs completed tool results with <c>JSON.stringify</c>, so a single
/// <c>read_file</c> of a credentials file would otherwise land verbatim in
/// <c>agent.log</c> and, through "Copy diagnostics", on the clipboard. Scrubbing that
/// with regexes cannot be sound over arbitrary tool output, so the ordinary log is an
/// allowlist instead: a line is kept verbatim only when the parser recognized it as one
/// of the CLI's own status lines.
/// </para>
/// <para>
/// Unrecognized lines still have diagnostic value - a new error message from a future
/// CLI version should not vanish - so they are kept only when they cannot plausibly be a
/// serialized payload: no braces, no brackets, no quotes, and short. Everything else is
/// replaced by a length-only placeholder.
/// </para>
/// <para>
/// Raw output is available, but only when the user turns on <c>verboseAgentLog</c>, and
/// then it goes to a separate file that diagnostics never reads.
/// </para>
/// </remarks>
public static class AgentLogPolicy
{
    /// <summary>Longest unrecognized line that may still be logged verbatim.</summary>
    public const int MaxUnrecognizedLength = 200;

    /// <summary>
    /// Returns the text to write to <c>agent.log</c> for one line of agent output.
    /// </summary>
    /// <param name="rawLine">The line as the CLI printed it.</param>
    /// <param name="signal">What the parser made of it.</param>
    public static string Sanitize(string rawLine, AgentSignal signal)
    {
        switch (signal.Kind)
        {
            case AgentSignalKind.ToolPayload:
                return $"<tool result omitted, {rawLine.Length} chars>";

            case AgentSignalKind.ToolCall:
                return $"tool call {signal.Value ?? "unknown"}";

            case AgentSignalKind.None:
                return LooksLikePayload(rawLine)
                    ? $"<agent output omitted, {rawLine.Length} chars>"
                    : SecretRedactor.Redact(rawLine);

            default:
                // A line the parser matched against one of the CLI's own status prefixes.
                return SecretRedactor.Redact(rawLine);
        }
    }

    /// <summary>True when a line could be serialized data rather than a status message.</summary>
    internal static bool LooksLikePayload(string line)
    {
        if (line.Length > MaxUnrecognizedLength)
        {
            return true;
        }

        foreach (var c in line)
        {
            if (c is '{' or '}' or '[' or ']' or '"' or '\\')
            {
                return true;
            }
        }

        return false;
    }
}
