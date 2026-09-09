namespace Baton.Cli;

/// <summary>
/// Parsed <c>baton memory retract</c> arguments (#2113). A record for the reason every other options
/// type in this directory is one — the parser decides, the command reads.
/// </summary>
/// <param name="EntryId">
/// The <c>MemoryEntry.Id</c> to retract, as typed. The command matches it against the store
/// case-insensitively and records the store's own spelling.
/// </param>
/// <param name="Reason">
/// Why the entry no longer holds, stored verbatim on the retraction row. Required and non-blank: a
/// retraction without one is indistinguishable from a deletion, which is the thing this verb exists
/// not to be.
/// </param>
/// <param name="Repository">
/// The canonical subject, or <see langword="null"/> to probe git at the working directory — the same
/// default <c>baton memory add</c> takes, held to the same write-path refusal.
/// </param>
/// <param name="Help">Whether <c>--help</c> was passed.</param>
public sealed record MemoryRetractOptions(
    string EntryId,
    string Reason,
    string? Repository,
    bool Help);
