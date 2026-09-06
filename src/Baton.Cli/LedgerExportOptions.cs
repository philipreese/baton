namespace Baton.Cli;

/// <summary>
/// Parsed arguments for <c>baton ledger export</c> (#1901 C3) — see <see cref="LedgerExportCommand"/>
/// for what it does and <see cref="LedgerExportOptionsParser"/> for the grammar.
/// </summary>
/// <param name="TargetDirectoryPath">
/// <c>--to</c>: the directory the dated CSV and its <c>README.md</c> index live in. Required — this
/// verb has no default destination, because the destination is a repository working tree and guessing
/// one would be guessing which repository the operator meant to publish to.
/// </param>
/// <param name="AsOf">
/// <c>--as-of</c>: the date the written file is NAMED for, defaulting to today in the operator's local
/// zone. It <b>names the file; it does not window the content</b> — an export is always the whole store
/// as it stands at write time. Backdating a name over a store that has since grown would produce a file
/// whose name says one thing and whose rows say another, so the two are kept deliberately separate and
/// the README's "newest row" column is what tells a reader how current a given file actually is.
/// </param>
/// <param name="RepositoryIdentityKey">
/// <c>--repo-identity</c>: which repository's ledger file to export, resolved exactly the way
/// <see cref="LedgerViewCommand"/> resolves it for a reading. Unset is the repository the operator is
/// standing in.
/// </param>
/// <param name="Help"><c>--help</c>: print the grammar and exit 0 without reading or writing anything.</param>
public sealed record LedgerExportOptions(
    string? TargetDirectoryPath,
    DateTime? AsOf = null,
    string? RepositoryIdentityKey = null,
    bool Help = false);
