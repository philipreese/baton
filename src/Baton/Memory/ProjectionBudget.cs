using System.Globalization;
using System.Text.Json.Serialization;

namespace Baton.Memory;

/// <summary>
/// How much of one repository's canonical memory a projection may carry (#1852 phase C), and — the
/// part that matters — <b>which entries a projection that does not fit leaves out</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Truncation is stop-at-the-first-entry-that-does-not-fit, never skip-and-continue.</b> Both are
/// deterministic, so determinism is not what picks between them. What picks is whether an operator can
/// predict the answer: under skip-and-continue, whether a given memory appears depends on the sizes of
/// every other memory ahead of it, so adding one unrelated entry can silently swap which of two others
/// survives. Under stop-at-first, the projection is a prefix of the total order
/// (<see cref="MemoryProjection"/> states what that order is) and the drop set is its suffix — which is
/// a sentence an operator can hold, and which is what <c>MemoryProjectionTests</c> pins as the exact
/// tail rather than as "something was dropped".
/// </para>
/// <para>
/// <b>Dropped entries are NAMED, never counted.</b> <see cref="MemoryProjection"/> returns one
/// <see cref="ProjectionOmission"/> per drop, carrying the canonical entry id and the source file it
/// came from, and <c>baton memory sync</c> prints every one. A budget that reported "17 entries
/// dropped" would be a silent loss with a number attached: the operator cannot tell whether the thing
/// they are missing is in the 17 without being told which 17.
/// </para>
/// <para>
/// <b>The defaults are a bound on a projected FILE, not a measurement of a vendor limit.</b> Neither
/// Claude Code nor Codex publishes a size at which a memory file stops being read, and inventing one
/// here would be exactly the kind of unmeasured vendor claim <c>docs/vendor-doc-audit.md</c> exists to
/// keep out of this tree. What these numbers are is a ceiling on what one generated cache file may
/// grow to before the operator is told it was truncated — chosen so the observed populations (14 files
/// in the largest live root #1852's survey found) fit whole with room to spare.
/// </para>
/// </remarks>
/// <param name="MaxBodyBytes">
/// UTF-8 byte ceiling on the projected <b>body</b> — the entry sections, excluding the header, because
/// the header's own length varies with the counts it reports and a budget whose meaning shifted with
/// its own reporting would not be a fixed bound at all.
/// </param>
/// <param name="MaxEntries">Ceiling on how many entries the body may carry.</param>
public sealed record ProjectionBudget(
    [property: JsonPropertyName("maxBodyBytes")]
    int MaxBodyBytes,
    [property: JsonPropertyName("maxEntries")]
    int MaxEntries)
{
    /// <summary>The bound applied when <c>baton memory sync</c> is given no narrower one.</summary>
    public static ProjectionBudget Default { get; } = new(MaxBodyBytes: 256 * 1024, MaxEntries: 500);

    /// <summary>
    /// This budget in the projection header's own words. Rendered through
    /// <see cref="CultureInfo.InvariantCulture"/> for the reason <see cref="MemoryProjection"/> gives:
    /// a projection whose bytes depend on the machine's number formatting is not byte-identical across
    /// machines, and the header is the only place a number reaches the file.
    /// </summary>
    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{MaxBodyBytes} body bytes, {MaxEntries} entries");
}
