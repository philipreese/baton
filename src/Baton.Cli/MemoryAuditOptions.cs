namespace Baton.Cli;

/// <summary>How <c>baton memory audit</c> renders its report. <see cref="Json"/> is the machine contract phase B reads.</summary>
public enum MemoryAuditOutputFormat
{
    Text,
    Json,
}

/// <summary>
/// Parsed arguments for <c>baton memory audit</c> (#1852 phase A) — see
/// <see cref="MemoryAuditCommand"/> for what it does and <see cref="MemoryAuditOptionsParser"/> for
/// the grammar.
/// </summary>
/// <remarks>
/// <b>There is deliberately no <c>--dry-run</c> here</b>, and the parser rejects one by name rather
/// than as a generic unknown option: the verb is read-only by construction, so a dry-run flag would
/// imply a wet run that does not exist. <see cref="Baton.Memory.MemoryAuditReport"/>'s own remarks state the
/// construction; <see cref="MemoryAuditOptionsParser.HelpLines"/> states it where an operator sees it.
/// </remarks>
/// <param name="Format">Text for a person, JSON for a program.</param>
/// <param name="Help">
/// <c>--help</c>: print the grammar and what each finding kind means, then exit 0 without scanning
/// anything.
/// </param>
/// <param name="Repository">
/// <c>--repository &lt;id&gt;|fleet</c> (#2112): restrict the CANONICAL STORES section to one store.
/// <see langword="null"/> reports every store found. The root inventory is machine-wide and is never
/// filtered by it — a vendor root's mapping is what the findings are about, and hiding roots would
/// hide findings.
/// </param>
public sealed record MemoryAuditOptions(
    MemoryAuditOutputFormat Format = MemoryAuditOutputFormat.Text,
    bool Help = false,
    string? Repository = null);
