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
/// <param name="UndoManifestPath">The manifest to reverse, for <c>--undo</c>. Mutually exclusive with everything above.</param>
/// <param name="Help">Print usage and exit.</param>
public sealed record MemoryImportOptions(
    bool DryRun,
    IReadOnlyList<string> Roots,
    string? UndoManifestPath,
    bool Help);
