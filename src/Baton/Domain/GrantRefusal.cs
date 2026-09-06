namespace Baton.Domain;

/// <summary>
/// The one marker every grant refusal carries, and the one function that puts it there (#1921).
/// <para>
/// <b>What a refusal is</b>: a tool call that returned no information because Baton's permission grant
/// declined it — a shell segment outside the scoped allow list or inside the standing deny list, a
/// command line the matcher could not parse under a scoped grant, a path outside the readable roots or
/// the workspace root, a tool withheld from the role entirely. The vendor still bills the turn, so the
/// step is spent; what it bought is the boundary's location.
/// </para>
/// <para>
/// <b>Why a marker rather than a list of phrasings.</b> Before this constant existed, counting refusals
/// meant matching five different sentences produced at four sites, and a sixth phrasing added anywhere
/// would have gone uncounted with nothing failing. The count is now a substring test for
/// <see cref="Marker"/>, and each producing site has a test asserting it stamps one — so a new refusal
/// phrasing cannot escape the count without that site's own test going red.
/// </para>
/// <para>
/// <b>The marker travels in the tool RESULT the worker reads</b>, which is what puts it into the room's
/// captured <c>.stdout.log</c> and therefore in reach of
/// <see cref="Baton.Status.ToolStepTally"/>. Measured on all three vendors' real streams: claude carries
/// it in a <c>tool_result</c> block's text, agy in <c>step_update.tool_info.error.message</c>, codex in
/// an <c>item.completed</c> <c>aggregated_output</c>. It is deliberately visible to the model as well —
/// it costs a handful of characters and makes "Baton refused this" unambiguous in a transcript where
/// vendors wrap our text in their own error prose.
/// </para>
/// </summary>
public static class GrantRefusal
{
    /// <summary>
    /// The literal. Bracketed and namespaced so it cannot plausibly occur in a file a worker read, a
    /// command it ran, or a vendor's own error text — this string is matched against whole captured
    /// streams, where a common word would count file contents as refusals.
    /// </summary>
    public const string Marker = "[baton:grant-refused]";

    /// <summary>
    /// <paramref name="reason"/> carrying <see cref="Marker"/>, exactly once.
    /// <para>
    /// <b>Idempotent, and that is load-bearing rather than defensive.</b> Refusal texts compose: a hook
    /// command wraps <c>ShellCommandPatternMatcher</c>'s own reason inside its sentence, and both are
    /// producing sites this issue stamps. Stamping twice would be harmless for a substring count and
    /// wrong for a human reading the transcript, so a reason that already carries the marker is
    /// returned unchanged and the marker stays where the innermost producer put it.
    /// </para>
    /// </summary>
    public static string Stamp(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return reason.Contains(Marker, StringComparison.Ordinal) ? reason : Marker + " " + reason;
    }

    /// <summary>Whether <paramref name="text"/> is a refusal this build produced. Null and empty are not.</summary>
    public static bool IsRefusal(string? text) =>
        text is not null && text.Contains(Marker, StringComparison.Ordinal);
}
