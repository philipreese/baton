namespace Baton.Memory;

/// <summary>
/// One place a projection is written: a vendor memory root that already exists, plus the single file
/// inside it that Baton owns (#1852 phase C).
/// </summary>
/// <param name="Vendor">Which vendor's root this is (<c>claude</c>, <c>codex</c>) — the same vocabulary <see cref="MemoryEntry.SourceVendor"/> uses.</param>
/// <param name="Scope">Whether the root is the vendor's own or Baton-managed, as phase A2 classifies it.</param>
/// <param name="RootDirectoryPath">The memory root the file goes in. Discovered, never constructed; see the target types' remarks.</param>
/// <param name="FilePath">The one file this target's projection is written to.</param>
public sealed record ProjectionTarget(
    string Vendor, VendorMemoryScope Scope, string RootDirectoryPath, string FilePath);

/// <summary>
/// The Claude memory roots a projection is written into — <c>{claude-home}/projects/&lt;encoded-path&gt;/memory/</c>,
/// the same shape phase A audits and phase B imports from.
/// </summary>
/// <remarks>
/// <para>
/// <b>A target directory is DISCOVERED, never constructed.</b> There is no forward encoding here that
/// turns a repository identity into a project-directory name, and that is a deliberate refusal rather
/// than a gap: <c>MemoryRootPath</c>'s remarks show the encoding is lossy in the decode direction
/// (<c>\</c> and <c>-</c> both become <c>-</c>), which means minting one would be asserting a mapping
/// nothing on the machine can confirm. What <c>baton memory sync</c> does instead is run the same
/// discovery <c>baton memory audit</c> runs, keep the roots that resolve to the repository being
/// synced, and write into those. What happens when that search comes back empty — and why it is not a
/// case for constructing one — is spec/baton.md §12's ruling.
/// </para>
/// <para>
/// <b>One file, owned outright, and not the vendor's index.</b> Baton writes exactly
/// <see cref="ProjectionFileName"/> and overwrites it in full; it does not touch <c>MEMORY.md</c> or
/// any other file in the root. What follows from that, said outright rather than left to be worked
/// out: Claude Code surfaces memories it has indexed, so a projection Baton did not add to the
/// vendor's own index may not be read by the vendor until something points at it. Editing the
/// vendor's index would make the projection reachable and would also make this verb destructive to a
/// file the operator owns, which is the trade #1852 settles in the other direction throughout.
/// </para>
/// </remarks>
public static class ClaudeProjectionTarget
{
    /// <summary>
    /// The one file Baton writes in a Claude memory root. Prefixed <c>baton-</c> so it cannot collide
    /// with a memory the vendor or the operator wrote, and suffixed <c>.md</c> because the root's other
    /// files are markdown and a reader opening it should get the same thing.
    /// </summary>
    public const string ProjectionFileName = "baton-projection.md";

    /// <summary>The target for an already-discovered Claude memory root.</summary>
    public static ProjectionTarget For(string rootDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectoryPath);

        return new ProjectionTarget(
            MemoryRootInventory.ClaudeVendor,
            VendorMemoryScope.Vendor,
            rootDirectoryPath,
            Path.Combine(rootDirectoryPath, ProjectionFileName));
    }
}

/// <summary>
/// The Codex <b>markdown</b> memory roots a projection is written into — <c>~/.codex/memories</c> and
/// the Baton-managed <c>~/.baton/codex-home/memories</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Markdown only, and that is a ruling rather than a limitation of this code.</b> Q4 (operator,
/// 2026-09-05) confined phase C to markdown targets, and the evening ruling of the same day settled
/// which Codex surface is the memory: the <c>memories_*.sqlite</c> stores are the pipeline that
/// PRODUCES these files (phase A2 measured a leased jobs queue and zero memory rows), so they are
/// inventoried and never written. Nothing here opens a database, and <c>baton memory sync --help</c>
/// says so rather than leaving an operator to infer it from silence.
/// </para>
/// <para>
/// <b>These roots are per-machine, so they carry no repository of their own.</b> Unlike a Claude root,
/// which encodes a checkout, <c>~/.codex/memories</c> encodes nothing — which is why phase B files it
/// only under an operator <c>--assert</c>. Sync inherits the same answer from the same
/// <c>MemoryAliasStore</c>: a Codex root with no assertion is not a target for any repository, and is
/// reported as unassigned rather than being handed whichever repository the command ran in.
/// </para>
/// </remarks>
public static class CodexProjectionTarget
{
    /// <summary>The one file Baton writes in a Codex markdown root, for the reason <see cref="ClaudeProjectionTarget.ProjectionFileName"/> gives.</summary>
    public const string ProjectionFileName = ClaudeProjectionTarget.ProjectionFileName;

    /// <summary>The target for an already-discovered Codex markdown root.</summary>
    public static ProjectionTarget For(string rootDirectoryPath, VendorMemoryScope scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectoryPath);

        return new ProjectionTarget(
            "codex",
            scope,
            rootDirectoryPath,
            Path.Combine(rootDirectoryPath, ProjectionFileName));
    }
}
