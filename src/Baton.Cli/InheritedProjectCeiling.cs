using Baton.Accounting;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// #2076: gives a worktree or clone of an <b>already-trusted repository</b> the ceiling that
/// repository already carries, so a fresh checkout does not need an operator's <c>baton trust</c> by
/// hand before its first lane can run.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it does not change: an untrusted repository still fails closed.</b> Inheritance only ever
/// copies a ceiling an operator already recorded, from a path in the SAME repository — decision 0004's
/// "never ask in headless" (spec/baton.md §9) is untouched, and a workspace whose repository has no
/// recorded ceiling anywhere still reaches <c>ProjectCeilingGate</c> with nothing to find and refuses.
/// A revoked parent propagates nothing for the same reason: <c>baton trust --revoke</c> removes the
/// entry, and what is not in the store cannot be a source — and it removes every entry that was copied
/// FROM that source too (<see cref="ProjectCeilingStore.Revoke"/>), because the copy is a one-time
/// snapshot that would otherwise outlive the decision it was derived from.
/// </para>
/// <para>
/// <b>A probe that answers nothing is not "no match".</b> <see cref="TryInheritAsync"/> reports the two
/// apart (<see cref="InheritanceOutcome.NoIdentity"/> versus
/// <see cref="InheritanceOutcome.NoTrustedSource"/>) because a caller with a fallback of its own —
/// <c>queue add --issue</c>'s <c>all</c> — must not take it on a git that is missing, timed out, or
/// exited non-zero: that is the one path on which a transient failure would widen a ceiling the
/// operator deliberately narrowed, and it fails closed instead.
/// </para>
/// <para>
/// <b>Repository identity, not path shape</b> — <see cref="RepositoryIdentity"/>, the same resolver the
/// cost ledger and the memory store key on, so a linked worktree (which shares its main checkout's git
/// common directory) and a separate clone (which shares its <c>origin</c> URL) both resolve to the one
/// identity their sibling was recorded under. Nothing here compares directory names or looks for a
/// parent directory: <c>C:\repos\w2069</c> is a worktree of <c>C:\repos\baton</c> because git says so,
/// not because the paths look related.
/// </para>
/// <para>
/// <b>The narrowest matching ceiling wins</b>, ties broken by the ordinal path order the store already
/// enumerates in. Two paths in one repository normally carry the same ceiling, so the rule almost never
/// discriminates — but when it does, inheriting the widest of them would let a stale sibling entry
/// widen what the operator most recently decided, which is the wrong direction for a control whose
/// whole job is being an outer bound.
/// </para>
/// <para>
/// <b>Every caller must print what <see cref="TryRecordAsync"/> returns, adjacent to the call.</b> This
/// is the canonical statement of that rule; the two call sites cite it rather than restating it. A
/// ceiling that appeared without an operator typing <c>baton trust</c> is the kind of widening that
/// must not be silent, and #2076 asks for it to be said once in the room. <b>The window is one call
/// wide</b>: the moment the entry is written, every later call for that workspace takes the
/// already-recorded early exit below and returns <see langword="null"/>, so a returned line that is
/// dropped — thrown away, or skipped by a refusal between the call and a distant print — is not merely
/// late. It is gone, on every path, forever.
/// </para>
/// <para>
/// <b>What it costs, and why the cost is bounded.</b> Finding a source means probing git for each
/// recorded path, which is a process spawn per candidate — so it runs only for a workspace that has NO
/// recorded ceiling at all, and the entry it writes is what stops it running again for that workspace
/// ever. Paths that no longer exist on disk are skipped without a spawn, which is most of a real
/// store: the entries are one per lane worktree ever dispatched and lane worktrees are deleted, so the
/// scan's spawn count tracks the LIVE checkouts, not the recorded ones. No count is transcribed here
/// because it drifts every night — <c>baton trust --list</c> against a store is what measures it. A
/// dispatch into an already-trusted workspace — every repeat lane — spawns nothing and reads the store
/// once.
/// <para>
/// <b>What does not shrink: the scan cannot stop early.</b> "Narrowest wins" is a property of the whole
/// candidate set, so a match does not end the loop — every surviving entry is probed on the one dispatch
/// that inherits. That cost grows with the store, and this type is itself what makes the store grow
/// without an operator typing anything.
/// </para>
/// </para>
/// </remarks>
internal static class InheritedProjectCeiling
{
    /// <summary>
    /// Records the ceiling <paramref name="workspacePath"/> inherits from an already-trusted path in the
    /// same repository, and returns the one line the caller prints — or <see langword="null"/> when
    /// nothing was inherited and nothing was written. The dispatch-side shape: a caller that has no
    /// fallback of its own and needs no more than the line, because <c>ProjectCeilingGate</c> refuses
    /// behind it either way. <see cref="TryInheritAsync"/> is the same lookup with the outcome kept.
    /// </summary>
    public static async Task<string?> TryRecordAsync(
        string workspacePath,
        string storePath,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> probe,
        CancellationToken cancellationToken = default) =>
        (await TryInheritAsync(workspacePath, storePath, probe, cancellationToken).ConfigureAwait(false)).Fact;

    /// <summary>
    /// Records the ceiling <paramref name="workspacePath"/> inherits from an already-trusted path in the
    /// same repository, and says which of the four ways the lookup ended.
    /// </summary>
    /// <param name="workspacePath">The workspace a dispatch is about to run in.</param>
    /// <param name="storePath">The ceiling store to read and write — <see cref="ProjectCeilingStore.DefaultPath"/> in production.</param>
    /// <param name="probe">
    /// The git probe. Injected rather than called directly, the same seam
    /// <see cref="RepositoryIdentityResolver.TryResolveForRoomAsync(string, IReadOnlyList{Baton.Status.RoomRegistryEntry}, Func{string, CancellationToken, Task{RepositoryIdentity?}}, CancellationToken)"/>
    /// and <c>LedgerBackfillCommand</c>'s <c>RepositoryProbe</c> already use, so every arm of this is
    /// exercisable against a temp store with no repository on disk and no process spawned.
    /// </param>
    /// <returns>
    /// <see cref="InheritanceOutcome.Inherited"/> with the fact line for the caller's own output; any other
    /// outcome with no line, because nothing was written. Silence is the normal case: nothing is said
    /// when nothing was decided.
    /// </returns>
    public static async Task<InheritanceResult> TryInheritAsync(
        string workspacePath,
        string storePath,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> probe,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storePath);
        ArgumentNullException.ThrowIfNull(probe);

        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return new InheritanceResult(InheritanceOutcome.NoIdentity);
        }

        var ceilings = ProjectCeilingStore.Load(storePath);
        var key = ProjectCeilingStore.CanonicalKey(workspacePath);

        // Already trusted exits before the probe, so the common case (a repeat lane in a workspace that
        // already has an entry) spawns no git at all. An EMPTY store does not short-circuit: the probe
        // has to run so that "git answered nothing" is reported as that, not as "no trusted source".
        if (ceilings.ContainsKey(key))
        {
            return new InheritanceResult(InheritanceOutcome.AlreadyTrusted);
        }

        if (await probe(workspacePath, cancellationToken).ConfigureAwait(false) is not { } identity)
        {
            return new InheritanceResult(InheritanceOutcome.NoIdentity);
        }

        string? sourcePath = null;
        ProjectCeiling? source = null;
        foreach (var (recordedPath, recorded) in ceilings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(recordedPath))
            {
                continue;
            }

            var candidate = await probe(recordedPath, cancellationToken).ConfigureAwait(false);
            if (candidate is null || !string.Equals(candidate.Value, identity.Value, StringComparison.Ordinal))
            {
                continue;
            }

            if (source is null || OpenCategories(recorded) < OpenCategories(source))
            {
                sourcePath = recordedPath;
                source = recorded;
            }
        }

        if (source is null || sourcePath is null)
        {
            return new InheritanceResult(InheritanceOutcome.NoTrustedSource);
        }

        // InheritedFrom is overwritten rather than carried through: a chain of inheritances names the
        // path this entry was actually copied from, which is the one an operator can go and inspect.
        var inherited = source with { InheritedFrom = sourcePath };
        ProjectCeilingStore.Set(workspacePath, inherited, storePath);

        return new InheritanceResult(
            InheritanceOutcome.Inherited,
            $"Project ceiling: '{key}' inherited '{inherited.Describe()}' from '{sourcePath}' "
            + $"(same repository: {identity.Value}) — no 'baton trust' was needed.");
    }

    /// <summary>How many of the four categories a ceiling leaves open — the "narrowest wins" ordering.</summary>
    private static int OpenCategories(ProjectCeiling ceiling) =>
        (ceiling.ReadFiles ? 1 : 0)
        + (ceiling.WriteFiles ? 1 : 0)
        + (ceiling.RunShellCommands ? 1 : 0)
        + (ceiling.NetworkAccess ? 1 : 0);
}

/// <summary>How <see cref="InheritedProjectCeiling.TryInheritAsync"/> ended. Only <see cref="Inherited"/> wrote anything.</summary>
internal enum InheritanceOutcome
{
    /// <summary>The workspace already carries an entry; nothing was probed and nothing was touched.</summary>
    AlreadyTrusted,

    /// <summary>The probe yielded no identity — git missing, timed out, exited non-zero, or no path to probe. A caller with a fallback must fail closed here, not take it.</summary>
    NoIdentity,

    /// <summary>The workspace has an identity, and no trusted path shares it.</summary>
    NoTrustedSource,

    /// <summary>A ceiling was copied and recorded; <see cref="InheritanceResult.Fact"/> is the line to print.</summary>
    Inherited,
}

/// <param name="Outcome">Which way the lookup ended.</param>
/// <param name="Fact">The line for the caller's output — set only for <see cref="InheritanceOutcome.Inherited"/>.</param>
internal sealed record InheritanceResult(InheritanceOutcome Outcome, string? Fact = null);
