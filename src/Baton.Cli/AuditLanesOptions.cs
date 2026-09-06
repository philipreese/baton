namespace Baton.Cli;

/// <summary>How <c>baton audit lanes</c> renders its report. <see cref="Json"/> is the machine contract.</summary>
public enum AuditLanesOutputFormat
{
    Text,
    Json,
}

/// <summary>
/// Parsed arguments for <c>baton audit lanes</c> (#1921) — see <see cref="AuditLanesCommand"/> for what
/// it does and <see cref="AuditLanesOptionsParser"/> for the grammar.
/// </summary>
/// <remarks>
/// <b>Read-only by construction</b>, like <c>baton memory audit</c>: it opens each room's flow log and
/// captured stdout, and writes, moves and deletes nothing anywhere. There is therefore no
/// <c>--dry-run</c>; <see cref="AuditLanesOptionsParser"/>'s own arm for that flag states how it is
/// refused and why.
/// </remarks>
/// <param name="Since">
/// Only rooms whose flow log was last written within this much of now. Absent = every room under the
/// root. A <b>duration</b> rather than <c>baton ledger --since</c>'s instant, because the question this
/// verb answers ("what has the fleet been wasting lately") is asked relative to now, and because it
/// filters on a FILE's last-write time rather than on a row's `endedAt` — the two are not the same
/// clock and giving them the same spelling would invite reading them as one.
/// </param>
/// <param name="Vendor">
/// Only executions whose resolved adapter matches, ordinal case-insensitive. Absent = every vendor. A
/// room whose executions are all filtered out is absent from the report rather than reported at zero,
/// for the reason every count here follows: this verb never substitutes a zero for a thing it did not
/// look at. It is disclosed under <see cref="AuditLanesReport.RoomsExcludedByVendor"/>, which states why
/// that is a separate bucket from the unreadable-stream one.
/// </param>
/// <param name="RoomsRoot">Walk this directory's immediate children instead of <c>~/.baton/rooms</c>.</param>
/// <param name="Format">Text for a person, JSON for a program.</param>
/// <param name="Help"><c>--help</c>: print the grammar and what each column means, then exit 0 without scanning anything.</param>
public sealed record AuditLanesOptions(
    TimeSpan? Since = null,
    string? Vendor = null,
    string? RoomsRoot = null,
    AuditLanesOutputFormat Format = AuditLanesOutputFormat.Text,
    bool Help = false);
