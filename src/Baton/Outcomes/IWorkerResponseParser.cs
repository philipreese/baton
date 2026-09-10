namespace Baton.Outcomes;

/// <summary>
/// An optional capability provided by worker adapters (#1594) to recover a worker's own final
/// answer from its terminal stream envelope. Exists for exactly one consumer,
/// <see cref="OutputMaterializer"/>: a worker that did the work but never wrote its declared output
/// file still said something on its way out, and that response is the only recovery an engine that
/// never reads conversation content (docs/agents/developing-baton.md's Architecture Rule 1) is allowed to reach for.
/// </summary>
public interface IWorkerResponseParser
{
    /// <summary>
    /// Attempts to interpret one raw stdout line — the last non-blank line of a completed execution's
    /// captured stream — as this vendor's terminal response text. False (and a null
    /// <paramref name="response"/>) for a line this vendor doesn't recognize, a non-terminal line, an
    /// error turn, or a terminal line whose response text is empty — an adapter that has nothing to
    /// report must not fabricate one.
    /// </summary>
    bool TryParseFinalResponse(string rawLine, out string? response)
    {
        response = null;
        return false;
    }

    /// <summary>
    /// True only when <paramref name="rawLine"/> is a vendor-defined terminal trailer that may
    /// legitimately follow the line carrying the final response. The default is false so a stray
    /// trailing line remains a hard boundary rather than making the materializer search arbitrarily
    /// far backward — but a vendor may declare MORE THAN ONE trailer type, and the materializer's scan
    /// walks back over however many consecutive trailers it meets. Codex is the current exception and
    /// declares two: <c>turn.completed</c> follows the completed <c>agent_message</c> item, and since
    /// #2020 a <c>turn.usage</c> line per model round-trip can land between them (that adapter's own
    /// remark has why it must be a trailer under either ordering).
    /// </summary>
    bool IsPostResponseTerminalLine(string rawLine) => false;
}
