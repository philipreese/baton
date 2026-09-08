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

    [Fact]
    public async Task A_revoked_parent_does_not_propagate()
    {
        var main = MakeDirectory("baton");
        var worktree = MakeDirectory("w2076");
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

        Assert.True(ProjectCeilingStore.Revoke(main, Store));
        Assert.True(ProjectCeilingStore.Revoke(worktree, Store));

        var afterRevoke = await InheritedProjectCeiling.TryRecordAsync(
            worktree, Store, probe, TestContext.Current.CancellationToken);

        Assert.Null(afterRevoke);
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively(_root);
        }
    }
}
