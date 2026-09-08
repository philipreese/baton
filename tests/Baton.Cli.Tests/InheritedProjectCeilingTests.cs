using System.Diagnostics;
using Baton.Accounting;
using Baton.Cli.Tests.TestSupport;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #2076's four required arms against <see cref="InheritedProjectCeiling"/>, plus the two rules its
/// own doc states that the four do not reach (narrowest-wins, and leaving a recorded entry alone). The
/// rule itself lives on that type; what is here is what discriminates it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The git probe is injected, the identity derivation is not.</b> Each fake below answers with a
/// real <see cref="RepositoryIdentity"/> built by <see cref="RepositoryIdentity.From"/> from the two
/// strings git would have printed — so the matching under test runs the product's own normalisation
/// (userinfo stripped, <c>.git</c> suffix dropped, case folded) rather than comparing two strings the
/// test typed identically. The clone arm depends on that: its <c>origin</c> is spelled
/// <c>https://philipreese@github.com/philipreese/baton.git</c> against a root recorded from
/// <c>https://github.com/philipreese/baton</c>, which is the spelling difference actually present in
/// the author's own store.
/// </para>
/// <para>
/// <b>Directories are real, because the scan skips paths that no longer exist</b> — a recorded path
/// with no directory behind it is never probed, so a fixture that skipped <c>Directory.CreateDirectory</c>
/// would pass every arm for the wrong reason.
/// </para>
/// </remarks>
public sealed class InheritedProjectCeilingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"inherit-ceiling-{Guid.NewGuid():N}");

    private string Store => Path.Combine(_root, "project-ceilings.json");

    private string MakeDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A probe that answers from an explicit path → (origin, common-dir) map; anything else has no identity.</summary>
    private static Func<string, CancellationToken, Task<RepositoryIdentity?>> ProbeOf(
        Dictionary<string, (string? Origin, string? CommonDir)> answers) =>
        (path, _) => Task.FromResult(
            answers.TryGetValue(path, out var answer) ? RepositoryIdentity.From(answer.Origin, answer.CommonDir) : null);

    [Fact]
    public async Task A_worktree_of_a_trusted_repository_inherits_its_ceiling()
    {
        var main = MakeDirectory("baton");
        var worktree = MakeDirectory("w2076");
        var commonDir = Path.Combine(main, ".git");
        ProjectCeilingStore.Set(main, ProjectCeiling.Unrestricted, Store);

        var fact = await InheritedProjectCeiling.TryRecordAsync(
            worktree,
            Store,
            // No origin on either: a worktree shares its main checkout's git COMMON directory, which is
            // the fallback derivation, and the one that makes the two paths one identity.
            ProbeOf(new() { [main] = (null, commonDir), [worktree] = (null, commonDir) }),
            TestContext.Current.CancellationToken);

        var recorded = ProjectCeilingStore.TryGet(worktree, Store);
        Assert.NotNull(recorded);
        Assert.True(recorded.IsUnrestricted);
        Assert.Equal(ProjectCeilingStore.CanonicalKey(main), recorded.InheritedFrom);
        Assert.NotNull(fact);
        Assert.Contains(main, fact, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_clone_of_a_trusted_repository_inherits_its_ceiling()
    {
        var main = MakeDirectory("baton");
        var clone = MakeDirectory("baton-clone");
        ProjectCeilingStore.Set(main, ProjectCeiling.Unrestricted, Store);

        var fact = await InheritedProjectCeiling.TryRecordAsync(
            clone,
            Store,
            ProbeOf(new()
            {
                // Two spellings of one remote, and two unrelated git directories: only the normalized
                // origin can make these one repository.
                [main] = ("https://github.com/philipreese/baton", Path.Combine(main, ".git")),
                [clone] = ("https://philipreese@github.com/philipreese/baton.git", Path.Combine(clone, ".git")),
            }),
            TestContext.Current.CancellationToken);

        Assert.NotNull(fact);
        Assert.True(ProjectCeilingStore.TryGet(clone, Store)?.IsUnrestricted);
    }

    [Fact]
    public async Task An_unrelated_directory_inherits_nothing()
    {
        var main = MakeDirectory("baton");
        var stranger = MakeDirectory("some-other-project");
        ProjectCeilingStore.Set(main, ProjectCeiling.Unrestricted, Store);

        var fact = await InheritedProjectCeiling.TryRecordAsync(
            stranger,
            Store,
            ProbeOf(new()
            {
                [main] = ("https://github.com/philipreese/baton", null),
                [stranger] = ("https://github.com/philipreese/basis", null),
            }),
            TestContext.Current.CancellationToken);

        Assert.Null(fact);
        Assert.Null(ProjectCeilingStore.TryGet(stranger, Store));
    }

    /// <summary>
    /// Pins the shape the #2076 inheritance ruling in spec/baton.md §9 accepts (2026-09-08): a directory
    /// that is neither a worktree nor a clone of anything, only a fresh <c>git init</c> whose
    /// <c>remote.origin.url</c> names a trusted repository, inherits that repository's ceiling. Against
    /// REAL git and the REAL probe (<see cref="RepositoryIdentityResolver.TryResolveAsync"/>) rather
    /// than a fake, because a fake would only restate the ruling — what is pinned is that git echoes
    /// the config string the directory supplies, which is the fact the ruling accepts. No network:
    /// <c>git remote add</c> writes config and fetches nothing. The control is the same directory one
    /// config line earlier: before the remote is added its identity is its own git directory, and it
    /// inherits nothing.
    /// </summary>
    [Fact]
    public async Task A_directory_claiming_a_trusted_origin_inherits_by_ruling()
    {
        const string trustedOrigin = "https://github.com/philipreese/baton";
        var main = MakeDirectory("baton");
        var claimant = MakeDirectory("claims-to-be-baton");
        await RunGitAsync(main, "init", "-q");
        await RunGitAsync(main, "remote", "add", "origin", trustedOrigin);
        await RunGitAsync(claimant, "init", "-q");
        ProjectCeilingStore.Set(main, ProjectCeiling.Unrestricted, Store);

        var beforeClaim = await InheritedProjectCeiling.TryInheritAsync(
            claimant, Store, RepositoryIdentityResolver.TryResolveAsync, TestContext.Current.CancellationToken);
        await RunGitAsync(claimant, "remote", "add", "origin", trustedOrigin);
        var afterClaim = await InheritedProjectCeiling.TryInheritAsync(
            claimant, Store, RepositoryIdentityResolver.TryResolveAsync, TestContext.Current.CancellationToken);

        Assert.Equal(InheritanceOutcome.NoTrustedSource, beforeClaim.Outcome);
        Assert.Equal(InheritanceOutcome.Inherited, afterClaim.Outcome);
        var recorded = ProjectCeilingStore.TryGet(claimant, Store);
        Assert.NotNull(recorded);
        Assert.True(recorded.IsUnrestricted);
        Assert.Equal(ProjectCeilingStore.CanonicalKey(main), recorded.InheritedFrom);
        Assert.Contains("github.com/philipreese/baton", afterClaim.Fact, StringComparison.Ordinal);
    }

    /// <summary>
    /// Revokes the SOURCE only. The earlier shape of this arm revoked the derived entry by hand too,
    /// which made the "nothing re-inherited" assertion pass whether or not the derived entry had
    /// survived — it could not discriminate. What is asserted now is what revoke actually does to the
    /// copy: it goes with its source (the cascade <see cref="ProjectCeilingStore.Revoke"/> states), a
    /// copy of the copy goes too, an unrelated entry stays, and nothing is re-inherited afterwards —
    /// the lookup reports the repository revoked (#2121).
    /// </summary>
    [Fact]
    public async Task Revoking_the_source_removes_its_inherited_entries_and_nothing_re_inherits()
    {
        var main = MakeDirectory("baton");
        var worktree = MakeDirectory("w2076");
        var grandchild = MakeDirectory("w2077");
        var unrelated = MakeDirectory("basis");
        var commonDir = Path.Combine(main, ".git");
        ProjectCeilingStore.Set(main, ProjectCeiling.Unrestricted, Store);
        ProjectCeilingStore.Set(unrelated, ProjectCeiling.Unrestricted, Store);

        var probe = ProbeOf(new()
        {
            [main] = (null, commonDir),
            [worktree] = (null, commonDir),
            [unrelated] = ("https://github.com/philipreese/basis", null),
        });

        // Positive control first: with the parent trusted, this exact call DOES inherit — so the
        // refusal below is the revoke, not a fixture that never matched anything.
        Assert.NotNull(await InheritedProjectCeiling.TryRecordAsync(
            worktree, Store, probe, TestContext.Current.CancellationToken));
        // A second-generation copy, recorded as TryRecordAsync would when the worktree was the
        // narrowest match: InheritedFrom names the worktree, not the root.
        ProjectCeilingStore.Set(
            grandchild, ProjectCeiling.Unrestricted with { InheritedFrom = ProjectCeilingStore.CanonicalKey(worktree) }, Store);

        var revocation = ProjectCeilingStore.Revoke(main, Store);

        Assert.True(revocation.Revoked);
        Assert.Equal(
            [ProjectCeilingStore.CanonicalKey(worktree), ProjectCeilingStore.CanonicalKey(grandchild)],
            revocation.CascadedPaths);
        Assert.Null(ProjectCeilingStore.TryGet(worktree, Store));
        Assert.Null(ProjectCeilingStore.TryGet(grandchild, Store));
        Assert.NotNull(ProjectCeilingStore.TryGet(unrelated, Store));

        var afterRevoke = await InheritedProjectCeiling.TryInheritAsync(
            worktree, Store, probe, TestContext.Current.CancellationToken);

        // #2121: not NoTrustedSource. The repository was revoked, and the outcome says so, naming the
        // tombstone that made it revoked (the root, ordinal-first among the tombstones that match).
        Assert.Equal(InheritanceOutcome.Revoked, afterRevoke.Outcome);
        Assert.Null(afterRevoke.Fact);
        Assert.Equal(ProjectCeilingStore.CanonicalKey(main), afterRevoke.RevokedPath);
        Assert.Equal(ProjectCeilingStore.TryGetRecord(main, Store)?.RevokedAt, afterRevoke.RevokedAt);
        Assert.Null(ProjectCeilingStore.TryGet(worktree, Store));
    }

    /// <summary>
    /// #2121, both polarities of "every recorded path revoked": with the root revoked but a hand-typed
    /// sibling still live, the repository is not revoked and the worktree inherits the sibling's
    /// ceiling; revoke that sibling too and the same call reports <see cref="InheritanceOutcome.Revoked"/>.
    /// </summary>
    [Fact]
    public async Task A_repository_is_revoked_only_when_no_live_path_of_it_remains()
    {
        var main = MakeDirectory("baton");
        var sibling = MakeDirectory("w1999");
        var worktree = MakeDirectory("w2121");
        var commonDir = Path.Combine(main, ".git");
        var readOnly = new ProjectCeiling(ReadFiles: true, WriteFiles: false, RunShellCommands: false, NetworkAccess: false);
        ProjectCeilingStore.Set(main, ProjectCeiling.Unrestricted, Store);
        ProjectCeilingStore.Set(sibling, readOnly, Store);
        ProjectCeilingStore.Revoke(main, Store);
        var probe = ProbeOf(new() { [main] = (null, commonDir), [sibling] = (null, commonDir), [worktree] = (null, commonDir) });

        var withLiveSibling = await InheritedProjectCeiling.TryInheritAsync(
            worktree, Store, probe, TestContext.Current.CancellationToken);

        Assert.Equal(InheritanceOutcome.Inherited, withLiveSibling.Outcome);
        Assert.Equal(readOnly with { InheritedFrom = ProjectCeilingStore.CanonicalKey(sibling) }, ProjectCeilingStore.TryGet(worktree, Store));

        ProjectCeilingStore.Revoke(sibling, Store);
        var fresh = MakeDirectory("w2122");
        var everyPathRevoked = await InheritedProjectCeiling.TryInheritAsync(
            fresh, Store,
            ProbeOf(new() { [main] = (null, commonDir), [sibling] = (null, commonDir), [worktree] = (null, commonDir), [fresh] = (null, commonDir) }),
            TestContext.Current.CancellationToken);

        Assert.Equal(InheritanceOutcome.Revoked, everyPathRevoked.Outcome);
        Assert.Null(ProjectCeilingStore.TryGet(fresh, Store));
    }

    /// <summary>
    /// #2121: a tombstone at the workspace's own key is not "already trusted". Revoke the root (the
    /// worktree's copy goes with it), re-trust the root, and the worktree — still tombstoned — inherits
    /// again and its tombstone is overwritten by the new copy.
    /// </summary>
    [Fact]
    public async Task A_tombstoned_workspace_inherits_again_once_its_repository_is_re_trusted()
    {
        var main = MakeDirectory("baton");
        var worktree = MakeDirectory("w2121");
        var commonDir = Path.Combine(main, ".git");
        var probe = ProbeOf(new() { [main] = (null, commonDir), [worktree] = (null, commonDir) });
        ProjectCeilingStore.Set(main, ProjectCeiling.Unrestricted, Store);
        Assert.NotNull(await InheritedProjectCeiling.TryRecordAsync(worktree, Store, probe, TestContext.Current.CancellationToken));
        ProjectCeilingStore.Revoke(main, Store);
        Assert.True(ProjectCeilingStore.TryGetRecord(worktree, Store)?.IsRevoked);

        var narrowed = new ProjectCeiling(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: false);
        ProjectCeilingStore.Set(main, narrowed, Store);
        var afterReTrust = await InheritedProjectCeiling.TryInheritAsync(
            worktree, Store, probe, TestContext.Current.CancellationToken);

        Assert.Equal(InheritanceOutcome.Inherited, afterReTrust.Outcome);
        var recorded = ProjectCeilingStore.TryGet(worktree, Store);
        Assert.NotNull(recorded);
        Assert.False(recorded.IsRevoked);
        Assert.False(recorded.NetworkAccess);
        Assert.Equal(ProjectCeilingStore.CanonicalKey(main), recorded.InheritedFrom);
    }

    /// <summary>
    /// The two <see langword="null"/>s <see cref="InheritedProjectCeiling.TryRecordAsync"/> collapses
    /// are told apart by <see cref="InheritedProjectCeiling.TryInheritAsync"/>: a probe that answers
    /// nothing is <see cref="InheritanceOutcome.NoIdentity"/>, an identity no trusted path shares is
    /// <see cref="InheritanceOutcome.NoTrustedSource"/>. Both against an EMPTY store, which used to
    /// short-circuit before the probe and so could not have told them apart at all.
    /// </summary>
    [Fact]
    public async Task A_probe_that_answers_nothing_is_reported_apart_from_no_trusted_source()
    {
        var stranger = MakeDirectory("some-other-project");

        var noIdentity = await InheritedProjectCeiling.TryInheritAsync(
            stranger, Store, ProbeOf(new()), TestContext.Current.CancellationToken);
        var noSource = await InheritedProjectCeiling.TryInheritAsync(
            stranger, Store, ProbeOf(new() { [stranger] = ("https://github.com/philipreese/basis", null) }),
            TestContext.Current.CancellationToken);

        Assert.Equal(InheritanceOutcome.NoIdentity, noIdentity.Outcome);
        Assert.Equal(InheritanceOutcome.NoTrustedSource, noSource.Outcome);
        Assert.Null(noIdentity.Fact);
        Assert.Null(noSource.Fact);
        Assert.Null(ProjectCeilingStore.TryGet(stranger, Store));
    }

    /// <summary>
    /// #2121: a recorded path whose directory exists but whose probe answers nothing, or throws, ends
    /// the lookup as <see cref="InheritanceOutcome.CandidateUnknown"/> naming that path — never as
    /// <see cref="InheritanceOutcome.NoTrustedSource"/>, which is the outcome a fallback-taking caller
    /// would widen on. The control is the same store with the candidate identified: it inherits.
    /// </summary>
    [Fact]
    public async Task A_recorded_path_the_probe_cannot_identify_is_reported_unknown_not_unmatched()
    {
        var main = MakeDirectory("baton");
        var worktree = MakeDirectory("w2121");
        var commonDir = Path.Combine(main, ".git");
        ProjectCeilingStore.Set(main, ProjectCeiling.Unrestricted, Store);

        var silent = await InheritedProjectCeiling.TryInheritAsync(
            worktree, Store, ProbeOf(new() { [worktree] = (null, commonDir) }), TestContext.Current.CancellationToken);
        var throwing = await InheritedProjectCeiling.TryInheritAsync(
            worktree, Store,
            (path, _) => path == worktree
                ? Task.FromResult<RepositoryIdentity?>(RepositoryIdentity.From(null, commonDir))
                : throw new InvalidOperationException("boom"),
            TestContext.Current.CancellationToken);
        var identified = await InheritedProjectCeiling.TryInheritAsync(
            worktree, Store, ProbeOf(new() { [main] = (null, commonDir), [worktree] = (null, commonDir) }), TestContext.Current.CancellationToken);

        Assert.Equal(InheritanceOutcome.CandidateUnknown, silent.Outcome);
        Assert.Equal(ProjectCeilingStore.CanonicalKey(main), silent.CandidatePath);
        Assert.Contains("answered nothing", silent.ProbeFailure, StringComparison.Ordinal);
        Assert.Equal(InheritanceOutcome.CandidateUnknown, throwing.Outcome);
        Assert.Equal(ProjectCeilingStore.CanonicalKey(main), throwing.CandidatePath);
        Assert.Contains("boom", throwing.ProbeFailure, StringComparison.Ordinal);
        Assert.Null(silent.Fact);
        Assert.Null(throwing.Fact);
        Assert.Equal(InheritanceOutcome.Inherited, identified.Outcome);
    }

    /// <summary>
    /// #2121 re-review: an unidentifiable recorded path does not abort the scan. With a live matching
    /// source also in the store the worktree inherits from it — one stale record anywhere on the machine
    /// must not block every dispatch — and the scan runs PAST the unknown in ordinal order (the unknown
    /// sorts first here). The control is the same store with the live source revoked: the same unknown
    /// candidate is then what the lookup reports, so the arm above and this one are one condition apart.
    /// </summary>
    [Fact]
    public async Task An_unidentifiable_candidate_does_not_block_inheritance_from_a_live_matching_source()
    {
        var archived = MakeDirectory("archived-w2100");
        var main = MakeDirectory("baton");
        var worktree = MakeDirectory("w2121");
        var commonDir = Path.Combine(main, ".git");
        ProjectCeilingStore.Set(archived, ProjectCeiling.Unrestricted, Store);
        ProjectCeilingStore.Set(main, ProjectCeiling.Unrestricted, Store);
        var probe = ProbeOf(new() { [main] = (null, commonDir), [worktree] = (null, commonDir) });

        var withLiveSource = await InheritedProjectCeiling.TryInheritAsync(
            worktree, Store, probe, TestContext.Current.CancellationToken);

        Assert.Equal(InheritanceOutcome.Inherited, withLiveSource.Outcome);
        Assert.Equal(ProjectCeilingStore.CanonicalKey(main), ProjectCeilingStore.TryGet(worktree, Store)?.InheritedFrom);

        ProjectCeilingStore.Forget(worktree, Store);
        ProjectCeilingStore.Revoke(main, Store);
        var withoutLiveSource = await InheritedProjectCeiling.TryInheritAsync(
            worktree, Store, probe, TestContext.Current.CancellationToken);

        // A matching tombstone is known, so Revoked outranks the unknown; forget the tombstone too and
        // the unknown is all that is left to report.
        Assert.Equal(InheritanceOutcome.Revoked, withoutLiveSource.Outcome);
        ProjectCeilingStore.Forget(main, Store);
        var onlyTheUnknown = await InheritedProjectCeiling.TryInheritAsync(
            worktree, Store, probe, TestContext.Current.CancellationToken);
        Assert.Equal(InheritanceOutcome.CandidateUnknown, onlyTheUnknown.Outcome);
        Assert.Equal(ProjectCeilingStore.CanonicalKey(archived), onlyTheUnknown.CandidatePath);
        Assert.Null(ProjectCeilingStore.TryGet(worktree, Store));
    }

    [Fact]
    public async Task The_narrowest_matching_ceiling_wins()
    {
        var main = MakeDirectory("baton");
        var sibling = MakeDirectory("w1999");
        var worktree = MakeDirectory("w2076");
        var commonDir = Path.Combine(main, ".git");
        var readOnly = new ProjectCeiling(ReadFiles: true, WriteFiles: false, RunShellCommands: false, NetworkAccess: false);
        ProjectCeilingStore.Set(main, ProjectCeiling.Unrestricted, Store);
        ProjectCeilingStore.Set(sibling, readOnly, Store);

        await InheritedProjectCeiling.TryRecordAsync(
            worktree,
            Store,
            ProbeOf(new() { [main] = (null, commonDir), [sibling] = (null, commonDir), [worktree] = (null, commonDir) }),
            TestContext.Current.CancellationToken);

        var recorded = ProjectCeilingStore.TryGet(worktree, Store);
        Assert.NotNull(recorded);
        Assert.False(recorded.IsUnrestricted);
        Assert.True(recorded.ReadFiles);
        Assert.False(recorded.WriteFiles);
        Assert.Equal(ProjectCeilingStore.CanonicalKey(sibling), recorded.InheritedFrom);
    }

    [Fact]
    public async Task An_already_trusted_workspace_is_left_exactly_as_the_operator_recorded_it()
    {
        var main = MakeDirectory("baton");
        var worktree = MakeDirectory("w2076");
        var commonDir = Path.Combine(main, ".git");
        var narrowed = new ProjectCeiling(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: false);
        ProjectCeilingStore.Set(main, ProjectCeiling.Unrestricted, Store);
        ProjectCeilingStore.Set(worktree, narrowed, Store);

        var fact = await InheritedProjectCeiling.TryRecordAsync(
            worktree,
            Store,
            ProbeOf(new() { [main] = (null, commonDir), [worktree] = (null, commonDir) }),
            TestContext.Current.CancellationToken);

        Assert.Null(fact);
        Assert.Equal(narrowed, ProjectCeilingStore.TryGet(worktree, Store));
    }

    [Fact]
    public async Task Trust_list_names_the_source_of_an_inherited_entry_and_says_nothing_for_a_typed_one()
    {
        using var home = new IsolatedBatonHome();
        var typed = Path.Combine(_root, "baton");
        var inherited = Path.Combine(_root, "w2076");
        ProjectCeilingStore.Set(typed, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        ProjectCeilingStore.Set(
            inherited,
            ProjectCeiling.Unrestricted with { InheritedFrom = ProjectCeilingStore.CanonicalKey(typed) },
            ProjectCeilingStore.DefaultPath);
        var output = new StringWriter();

        await TrustCommand.ExecuteAsync(
            new TrustOptions(TrustMode.List, null, null), output, TestContext.Current.CancellationToken);

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var inheritedLine = Assert.Single(lines, line => line.StartsWith(ProjectCeilingStore.CanonicalKey(inherited), StringComparison.OrdinalIgnoreCase));
        var typedLine = Assert.Single(lines, line => line.StartsWith(ProjectCeilingStore.CanonicalKey(typed), StringComparison.OrdinalIgnoreCase));
        Assert.Contains($"(inherited from {ProjectCeilingStore.CanonicalKey(typed)})", inheritedLine, StringComparison.Ordinal);
        Assert.DoesNotContain("inherited from", typedLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// The round trip the <c>--list</c> arm above cannot see: <see cref="ProjectCeiling.InheritedFrom"/>
    /// survives being written and re-read, and is omitted from an entry that has none — so recording one
    /// inherited ceiling does not stamp a null field onto every other entry in the map.
    /// </summary>
    [Fact]
    public void Provenance_round_trips_and_is_absent_from_the_json_of_a_typed_entry()
    {
        var typed = Path.Combine(_root, "baton");
        var inherited = Path.Combine(_root, "w2076");
        ProjectCeilingStore.Set(typed, ProjectCeiling.Unrestricted, Store);
        ProjectCeilingStore.Set(inherited, ProjectCeiling.Unrestricted with { InheritedFrom = typed }, Store);

        Assert.Equal(typed, ProjectCeilingStore.TryGet(inherited, Store)?.InheritedFrom);
        Assert.Null(ProjectCeilingStore.TryGet(typed, Store)?.InheritedFrom);
        var json = File.ReadAllText(Store);
        Assert.Equal(1, json.Split("InheritedFrom").Length - 1);
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git — is it on PATH? This test needs git.");
        var (stdout, stderr) = await BoundedProcessWait.RunToExitAsync(
            process, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdout} {stderr.Trim()}");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively(_root);
        }
    }
}
