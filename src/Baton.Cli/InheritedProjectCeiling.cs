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
/// A revoked parent propagates nothing either: <c>baton trust --revoke</c> leaves a tombstone
/// (<see cref="ProjectCeiling.RevokedAt"/>), which is never a source — and it tombstones every entry
/// that was copied FROM that source too (<see cref="ProjectCeilingStore.Revoke"/>), because the copy is
/// a one-time snapshot that would otherwise outlive the decision it was derived from. When the only
/// entries sharing the workspace's identity are tombstones, the outcome is
/// <see cref="InheritanceOutcome.Revoked"/> rather than <see cref="InheritanceOutcome.NoTrustedSource"/>
/// (#2121): the two are told apart so the provisioner's unrestricted default is unreachable from a
/// repository the operator deliberately withdrew.
/// </para>
/// <para>
/// <b>A probe that answers nothing is not "no match" — for the workspace or for a candidate.</b>
/// <see cref="TryInheritAsync"/> reports the two apart (<see cref="InheritanceOutcome.NoIdentity"/>
/// versus <see cref="InheritanceOutcome.NoTrustedSource"/>) because a caller with a fallback of its
/// own — <c>queue add --issue</c>'s <c>all</c> — must not take it on a git that is missing, timed out,
/// or exited non-zero: that is the one path on which a transient failure would widen a ceiling the
/// operator deliberately narrowed, and it fails closed instead. The same holds for a <b>recorded</b>
/// path whose probe answers nothing or throws (<see cref="InheritanceOutcome.CandidateUnknown"/>,
/// #2121): a candidate that cannot be identified might be the tombstone that makes this repository
/// revoked, so skipping it as "no match" would let the fallback reach a repository the operator
/// withdrew. It is reported <b>only when nothing live matched</b>: the scan finishes past it, and a
/// live entry sharing the workspace's identity still inherits, because once one matches the
/// repository cannot be <see cref="InheritanceOutcome.Revoked"/> whatever the unknown was — and
/// refusing there would make one stale record a machine-wide outage (the re-review's finding).
/// spec/baton.md §9 states the residual that buys (an unknown that was itself a narrower sibling)
/// and why it is bounded; it is not restated here. The outcome names the path so the operator can repair the
/// checkout or <c>baton trust &lt;path&gt; --forget</c> the record; a recorded path whose directory is
/// gone is still skipped without a probe, which spec/baton.md §9 states as the store's boundary.
/// </para>
/// <para>
/// <b>Repository identity, not path shape</b> — <see cref="RepositoryIdentity"/>, the same resolver the
/// cost ledger and the memory store key on, so a linked worktree (which shares its main checkout's git
/// common directory) and a separate clone (which shares its <c>origin</c> URL) both resolve to the one
/// identity their sibling was recorded under. Nothing here compares directory names or looks for a
/// parent directory: <c>C:\repos\w2069</c> matches <c>C:\repos\baton</c> because the two report the
/// same repository, not because the paths look related.
/// </para>
/// <para>
/// <b>The match is on the repository's self-reported origin, accepted by spec/baton.md §9's ruling.</b>
/// The origin URL is a string the directory's own <c>.git/config</c> supplies, so git verifies no
/// structural relationship here: a fresh <c>git init</c> with <c>remote.origin.url</c> set to a trusted
/// repository's URL inherits exactly as a clone does. That section's inheritance exception (ruled
/// 2026-09-08, #2076) is the one statement of why this is the accepted boundary and not a gap; it is
/// not restated here, and <c>A_directory_claiming_a_trusted_origin_inherits_by_ruling</c> pins the shape.
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
    /// same repository, and says which way the lookup ended (<see cref="InheritanceOutcome"/> enumerates them).
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
        // A tombstone at the key (#2121) is not "already trusted": it falls through to the scan, where
        // it is one of the candidates, so a revoked workspace whose repository has since been
        // re-trusted elsewhere inherits again, and one whose repository has not is reported Revoked.
        if (ceilings.TryGetValue(key, out var own) && !own.IsRevoked)
        {
            return new InheritanceResult(InheritanceOutcome.AlreadyTrusted);
        }

        if (await probe(workspacePath, cancellationToken).ConfigureAwait(false) is not { } identity)
        {
            return new InheritanceResult(InheritanceOutcome.NoIdentity);
        }

        string? sourcePath = null;
        ProjectCeiling? source = null;
        string? revokedPath = null;
        DateTimeOffset? revokedAt = null;
        string? unknownPath = null;
        string? unknownFailure = null;
        foreach (var (recordedPath, recorded) in ceilings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(recordedPath))
            {
                continue;
            }

            // An unidentifiable candidate does not end the scan (#2121 re-review): it is held (first
            // in ordinal order wins) and decides the outcome only if nothing live matched, below. A
            // thrown probe is the same "cannot identify" as a null one, mapped to a structured outcome
            // rather than swallowed.
            RepositoryIdentity? candidate;
            try
            {
                candidate = await probe(recordedPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unknownPath ??= recordedPath;
                unknownFailure ??= $"the repository-identity probe threw: {ex.Message}";
                continue;
            }

            if (candidate is null)
            {
                unknownPath ??= recordedPath;
                unknownFailure ??= "the repository-identity probe answered nothing (git missing, timed out, exited non-zero, or the directory is no longer a git checkout)";
                continue;
            }

            if (!string.Equals(candidate.Value, identity.Value, StringComparison.Ordinal))
            {
                continue;
            }

            // Tombstones are matched by the Directory.Exists + probe above, the same filter a live entry
            // passed through (spec/baton.md §9 has why a deleted checkout's tombstone therefore matches
            // nothing). A tombstone is never a source; it only makes the repository Revoked when no live
            // entry matches.
            if (recorded.IsRevoked)
            {
                revokedPath ??= recordedPath;
                revokedAt ??= recorded.RevokedAt;
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
            // Revoked outranks CandidateUnknown: a matching tombstone is known, so the unknown cannot
            // change the answer. CandidateUnknown outranks NoTrustedSource: the unknown might be the
            // tombstone, which is the one case the never-trusted fallback must not be taken on.
            if (revokedPath is not null && revokedAt is { } at)
            {
                return new InheritanceResult(InheritanceOutcome.Revoked, RevokedPath: revokedPath, RevokedAt: at);
            }

            return unknownPath is not null
                ? new InheritanceResult(InheritanceOutcome.CandidateUnknown, CandidatePath: unknownPath, ProbeFailure: unknownFailure)
                : new InheritanceResult(InheritanceOutcome.NoTrustedSource);
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

    /// <summary>The workspace has an identity, no trusted path shares it, no revoked path does either, and every recorded path whose directory exists was identified — the never-trusted repository.</summary>
    NoTrustedSource,

    /// <summary>
    /// #2121: no live entry shares the workspace's identity, no tombstone does, and at least one
    /// recorded path whose directory exists could not be identified — its probe answered nothing or
    /// threw — so whether the repository is never-trusted or revoked is unknown. Nothing is written. A
    /// caller with a fallback must refuse here, naming <see cref="InheritanceResult.CandidatePath"/>
    /// (the first such path in ordinal order) and <see cref="InheritanceResult.ProbeFailure"/>; the
    /// type remarks have why this is not "no match", and why a live match outranks it.
    /// </summary>
    CandidateUnknown,

    /// <summary>
    /// #2121: the workspace has an identity, no live entry shares it, and at least one tombstone does —
    /// every recorded path of this repository was revoked. Nothing is written. A caller with a fallback
    /// must refuse here (<see cref="ProjectNotTrustedException"/> naming the revocation), never take it.
    /// spec/baton.md §9 has the tombstone's definition.
    /// </summary>
    Revoked,

    /// <summary>A ceiling was copied and recorded; <see cref="InheritanceResult.Fact"/> is the line to print.</summary>
    Inherited,
}

/// <param name="Outcome">Which way the lookup ended.</param>
/// <param name="Fact">The line for the caller's output — set only for <see cref="InheritanceOutcome.Inherited"/>.</param>
/// <param name="RevokedPath">The first tombstoned path (ordinal order) sharing the workspace's identity — set only for <see cref="InheritanceOutcome.Revoked"/>.</param>
/// <param name="RevokedAt">When <paramref name="RevokedPath"/> was revoked — set only for <see cref="InheritanceOutcome.Revoked"/>.</param>
/// <param name="CandidatePath">The first recorded path (ordinal order) whose probe failed — set only for <see cref="InheritanceOutcome.CandidateUnknown"/>.</param>
/// <param name="ProbeFailure">What that probe could not do, in words the caller's refusal can quote — set only for <see cref="InheritanceOutcome.CandidateUnknown"/>.</param>
internal sealed record InheritanceResult(
    InheritanceOutcome Outcome,
    string? Fact = null,
    string? RevokedPath = null,
    DateTimeOffset? RevokedAt = null,
    string? CandidatePath = null,
    string? ProbeFailure = null);
