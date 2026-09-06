namespace Baton.Cli;

/// <summary>
/// Parsed <c>baton memory sync</c> arguments (#1852 phase C). A record for the reason every other
/// options type in this directory is one — the parser decides, the command reads, and nothing in
/// between can change what was asked for.
/// </summary>
/// <param name="Repository">
/// The canonical repository identity to sync, or <see langword="null"/> for every repository that has
/// a canonical store. Canonicalized by the parser, so the value here always names a store file.
/// </param>
/// <param name="Apply">
/// Whether to write. <see langword="false"/> — the default — writes nothing anywhere, creates no
/// directory, and reports what would change.
/// </param>
/// <param name="Format">Report format, <c>text</c> or <c>json</c>.</param>
/// <param name="RepositoryFactsDirectory">
/// A directory of checked-in repository facts to weigh against the vendor-sourced ones, or
/// <see langword="null"/> for none. Absent means the conflict rule has an empty population, which the
/// report says rather than leaving to be inferred from a silent zero.
/// </param>
/// <param name="Help">Whether <c>--help</c> was passed.</param>
public sealed record MemorySyncOptions(
    string? Repository,
    bool Apply,
    MemoryAuditOutputFormat Format,
    string? RepositoryFactsDirectory,
    bool Help);
