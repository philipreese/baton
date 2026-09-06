namespace Baton.Cli;

/// <summary>
/// <c>baton memory import</c>'s options (#1852 phase B).
/// </summary>
/// <param name="DryRun">
/// Compute the whole import and write <b>nothing at all</b> — no entry, and no manifest either. A
/// manifest describes what an import did; writing one for an import that did not happen would leave a
/// file whose replay reverses nothing.
/// </param>
/// <param name="Roots">
/// Which discovered roots to import, by directory path. Empty means all of them. <b>A filter over the
/// discovered population, never an addition to it</b>: a path that no discovered root matches is an
/// error rather than a new root, so discovery stays <c>MemoryRootInventory</c>'s alone.
/// </param>
/// <param name="Assertions">
/// Operator assertions of the form <c>&lt;path&gt;=&lt;repository-identity&gt;</c>, appended to the
/// alias store and applied to this run. The only way an entry gets a subject that git could not
/// answer for, and it never displaces one git did — see <c>Baton.Memory.MemoryAliasStore</c>.
/// </param>
/// <param name="AssertedBy">Who is asserting, stamped on every alias row this run writes.</param>
/// <param name="UndoManifestPath">The manifest to reverse, for <c>--undo</c>. Mutually exclusive with everything above.</param>
/// <param name="Help">Print usage and exit.</param>
public sealed record MemoryImportOptions(
    bool DryRun,
    IReadOnlyList<string> Roots,
    IReadOnlyList<MemoryImportAssertion> Assertions,
    string? AssertedBy,
    string? UndoManifestPath,
    bool Help);

/// <summary>One <c>--assert &lt;path&gt;=&lt;repository&gt;</c>, parsed.</summary>
/// <param name="Path">A memory root directory, or the checkout a root belongs to.</param>
/// <param name="Repository">The canonical <c>RepositoryIdentity.Value</c> being asserted for it.</param>
public sealed record MemoryImportAssertion(string Path, string Repository);
