using Baton.Accounting;
using Baton.Cli.Tests.TestSupport;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

public sealed class LifecycleTrustPreflightTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly RepositoryIdentity Repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Fresh_admission_refuses_foreign_sibling_before_reservation_or_provisioning(bool task, bool trustedSource)
    {
        using var home = new IsolatedBatonHome();
        var source = Directory.CreateDirectory(Path.Combine(home.Path, "source")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(home.Path, "sibling")).FullName;
        var target = Path.Combine(home.Path, "w2560");
        var brief = Path.Combine(home.Path, "brief.md");
        await File.WriteAllTextAsync(brief, "unchanged input", Ct);
        ProjectCeilingStore.Set(sibling, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        if (trustedSource) ProjectCeilingStore.Set(source, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with { Held = true }, Ct);
        await ConductorClaimStore.ClaimAsync(Repository, "owner", home.Path, cancellationToken: Ct);
        var queueBytes = await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct);
        var trustBytes = await File.ReadAllBytesAsync(ProjectCeilingStore.DefaultPath, Ct);
        var briefBytes = await File.ReadAllBytesAsync(brief, Ct);
        var entries = 0;
        Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) =>
            Task.FromResult<RepositoryIdentity?>(trustedSource && path == sibling ? null : Repository);
        async Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
            int issue, string project, string? root, string repository, bool lifecycle, TextWriter writer, CancellationToken token)
        {
            entries++;
            Directory.CreateDirectory(target);
            await IssueWorktreeProvisioner.TrustAsync(target, source, true, Resolve, cancellationToken: token);
            return new(target, "2560-lane");
        }
        var failure = await Record.ExceptionAsync(async () =>
        {
            if (task)
                await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Submit, 2560, source,
                    new TaskSizeDeclaration(DeclaredTaskSize.Small, "one trust boundary"), brief),
                    TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions);
            else
                await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.Add, Tag: "2560-lane", Role: "implement",
                    SpecFilePath: brief, Issue: 2560, Lifecycle: true,
                    DeclaredTaskSize: new TaskSizeDeclaration(DeclaredTaskSize.Small, "one trust boundary")),
                    TextWriter.Null, Ct, source, Resolve, Provision, preparationRunner: IssuePreparationRunner.NoCollisions);
        });
        Assert.Equal(0, entries);
        Assert.IsType<ProjectNotTrustedException>(failure);
        Assert.Equal(queueBytes, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
        Assert.Equal(trustBytes, await File.ReadAllBytesAsync(ProjectCeilingStore.DefaultPath, Ct));
        Assert.Equal(briefBytes, await File.ReadAllBytesAsync(brief, Ct));
        Assert.Empty((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(BatonPaths.QueueSpecsDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_admission_reuses_registered_trusted_target_without_source_authority(bool task)
    {
        using var home = new IsolatedBatonHome();
        var source = Directory.CreateDirectory(Path.Combine(home.Path, "source")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(home.Path, "w2560")).FullName;
        var brief = Path.Combine(home.Path, "brief.md");
        await File.WriteAllTextAsync(brief, "input", Ct);
        ProjectCeilingStore.Set(target, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        ProjectCeilingStore.Set(source, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        ProjectCeilingStore.Revoke(source, ProjectCeilingStore.DefaultPath);
        var before = await File.ReadAllBytesAsync(ProjectCeilingStore.DefaultPath, Ct);
        await ConductorClaimStore.ClaimAsync(Repository, "owner", home.Path, cancellationToken: Ct);
        Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(Repository);
        Task<(int, string)> Runner(string file, IReadOnlyList<string> args, string directory, CancellationToken token)
        {
            Assert.Equal("git", file);
            Assert.Equal(new[] { "worktree", "list", "--porcelain" }, args);
            return Task.FromResult((0, $"worktree {target}\nbranch refs/heads/2560-lane\n"));
        }
        Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(int issue, string project, string? root,
            string repository, bool lifecycle, TextWriter writer, CancellationToken token) =>
            IssueWorktreeProvisioner.ProvisionAsync(issue, project, root, repository, Runner, Resolve,
                writer, token, deterministicSourceCeiling: lifecycle);
        var exit = task
            ? await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Submit, 2560, source,
                new TaskSizeDeclaration(DeclaredTaskSize.Small, "one exact reuse"), brief),
                TextWriter.Null, Resolve, Provision, Ct, Runner)
            : await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.Add, Tag: "2560-lane", Role: "implement",
                SpecFilePath: brief, Issue: 2560, Lifecycle: true,
                DeclaredTaskSize: new TaskSizeDeclaration(DeclaredTaskSize.Small, "one exact reuse")),
                TextWriter.Null, Ct, source, Resolve, Provision, preparationRunner: Runner);
        Assert.Equal(0, exit);
        var row = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
        Assert.Equal(TaskPreparationState.Prepared, row.IssuePreparation!.State);
        Assert.Equal(target, row.Workspace);
        Assert.Equal(before, await File.ReadAllBytesAsync(ProjectCeilingStore.DefaultPath, Ct));
    }

    [Fact]
    public async Task Known_role_ceiling_refusal_precedes_reservation_and_provisioning()
    {
        using var home = new IsolatedBatonHome();
        var source = Directory.CreateDirectory(Path.Combine(home.Path, "source")).FullName;
        var brief = Path.Combine(home.Path, "brief.md");
        await File.WriteAllTextAsync(brief, "input", Ct);
        ProjectCeilingStore.Set(source, new ProjectCeiling(true, true, true, false), ProjectCeilingStore.DefaultPath);
        var before = await File.ReadAllBytesAsync(ProjectCeilingStore.DefaultPath, Ct);
        await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
            new QueueOptions(QueueVerb.Add, Tag: "2560-lane", Role: "implement", SpecFilePath: brief,
                Issue: 2560, Lifecycle: true,
                DeclaredTaskSize: new TaskSizeDeclaration(DeclaredTaskSize.Small, "one role refusal")),
            TextWriter.Null, Ct, source, (_, _) => Task.FromResult<RepositoryIdentity?>(Repository),
            (_, _, _, _, _, _, _) => throw new InvalidOperationException("provisioner must not be entered"),
            preparationRunner: IssuePreparationRunner.NoCollisions));
        Assert.False(File.Exists(BatonPaths.QueueFile));
        Assert.Equal(before, await File.ReadAllBytesAsync(ProjectCeilingStore.DefaultPath, Ct));
        Assert.False(Directory.Exists(BatonPaths.QueueSpecsDirectory));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("never-trusted")]
    [InlineData("bootstrap-lineage")]
    [InlineData("registered-target")]
    [InlineData("deleted-record")]
    public async Task Preflight_preserves_authority_and_is_read_only(string population)
    {
        using var home = new IsolatedBatonHome();
        var source = Directory.CreateDirectory(Path.Combine(home.Path, "source")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(home.Path, "sibling")).FullName;
        var target = Path.Combine(home.Path, "w2560");
        var sourceKey = ProjectCeilingStore.CanonicalKey(source);
        var ceiling = new ProjectCeiling(true, true, false, true);
        switch (population)
        {
            case "source":
                ProjectCeilingStore.Set(source, ceiling, ProjectCeilingStore.DefaultPath);
                ProjectCeilingStore.Set(sibling, new ProjectCeiling(true, false, false, false), ProjectCeilingStore.DefaultPath);
                break;
            case "bootstrap-lineage":
                ProjectCeilingStore.Set(sibling, ceiling with { InheritedFrom = sourceKey }, ProjectCeilingStore.DefaultPath);
                break;
            case "registered-target":
                Directory.CreateDirectory(target);
                ProjectCeilingStore.Set(target, ceiling, ProjectCeilingStore.DefaultPath);
                ProjectCeilingStore.Set(sibling, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                ProjectCeilingStore.Set(source, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                ProjectCeilingStore.Revoke(source, ProjectCeilingStore.DefaultPath);
                break;
            case "deleted-record":
                ProjectCeilingStore.Set(Path.Combine(home.Path, "deleted"), ceiling, ProjectCeilingStore.DefaultPath);
                break;
        }
        var before = File.Exists(ProjectCeilingStore.DefaultPath)
            ? await File.ReadAllBytesAsync(ProjectCeilingStore.DefaultPath, Ct) : null;
        var probes = new List<string>();
        Task<RepositoryIdentity?> Resolve(string path, CancellationToken token)
        {
            probes.Add(path);
            return Task.FromResult<RepositoryIdentity?>(Repository);
        }
        Task<(int, string)> Runner(string file, IReadOnlyList<string> args, string directory, CancellationToken token) =>
            args is ["worktree", "list", "--porcelain"]
                ? Task.FromResult((0, $"worktree {target}\nHEAD head\nbranch refs/heads/2560-lane\n"))
                : IssuePreparationRunner.NoCollisions(file, args, directory, token);
        var result = await IssueWorktreeProvisioner.PreflightAsync(2560, source, home.Path,
            Repository.Value, Resolve, Runner, Ct);
        Assert.Equal(target, result.Candidate.Workspace);
        Assert.Equal(population == "registered-target", result.Candidate.Reuse);
        Assert.Equal(population is "never-trusted" or "deleted-record"
            ? ProjectCeiling.Unrestricted with { InheritedFrom = sourceKey }
            : population == "registered-target" ? ceiling : ceiling with { InheritedFrom = sourceKey }, result.Ceiling);
        Assert.Equal(before, File.Exists(ProjectCeilingStore.DefaultPath)
            ? await File.ReadAllBytesAsync(ProjectCeilingStore.DefaultPath, Ct) : null);
        Assert.False(File.Exists(BatonPaths.QueueFile));
        if (population != "registered-target") Assert.DoesNotContain(target, probes);
    }

    [Theory]
    [InlineData("revoked-source")]
    [InlineData("revoked-only")]
    [InlineData("unknown-sibling")]
    [InlineData("throwing-sibling")]
    [InlineData("null-source")]
    [InlineData("throwing-source")]
    [InlineData("wrong-repository")]
    [InlineData("malformed-store")]
    [InlineData("untrusted-target")]
    [InlineData("unregistered-target")]
    [InlineData("unknown-branch")]
    public async Task Preflight_refuses_unproven_authority_without_writes(string population)
    {
        using var home = new IsolatedBatonHome();
        var source = Directory.CreateDirectory(Path.Combine(home.Path, "source")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(home.Path, "sibling")).FullName;
        var target = Path.Combine(home.Path, "w2560");
        ProjectCeilingStore.Set(sibling, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        if (population is "revoked-source" or "unknown-sibling" or "throwing-sibling")
            ProjectCeilingStore.Set(source, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        if (population == "revoked-source") ProjectCeilingStore.Revoke(source, ProjectCeilingStore.DefaultPath);
        if (population == "revoked-only") ProjectCeilingStore.Revoke(sibling, ProjectCeilingStore.DefaultPath);
        if (population is "untrusted-target" or "unregistered-target") Directory.CreateDirectory(target);
        if (population == "malformed-store") await File.WriteAllTextAsync(ProjectCeilingStore.DefaultPath, "{broken", Ct);
        var before = await File.ReadAllBytesAsync(ProjectCeilingStore.DefaultPath, Ct);
        Task<RepositoryIdentity?> Resolve(string path, CancellationToken token)
        {
            if ((population == "null-source" && path == source) || (population == "unknown-sibling" && path == sibling))
                return Task.FromResult<RepositoryIdentity?>(null);
            if ((population == "throwing-source" && path == source) || (population == "throwing-sibling" && path == sibling))
                throw new InvalidOperationException("synthetic identity failure");
            return Task.FromResult<RepositoryIdentity?>(population == "wrong-repository"
                ? RepositoryIdentity.From("https://github.com/foreign/repo", null) : Repository);
        }
        Task<(int, string)> Runner(string file, IReadOnlyList<string> args, string directory, CancellationToken token)
        {
            Assert.Equal("git", file);
            if (args is ["worktree", "list", "--porcelain"])
                return Task.FromResult((0, population == "unregistered-target" ? "" : $"worktree {target}\nbranch refs/heads/2560-lane\n"));
            if (population == "unknown-branch") return Task.FromResult((2, "probe unavailable"));
            return IssuePreparationRunner.NoCollisions(file, args, directory, token);
        }
        var failure = await Record.ExceptionAsync(() => IssueWorktreeProvisioner.PreflightAsync(2560, source,
            home.Path, Repository.Value, Resolve, Runner, Ct));
        if (population == "malformed-store") Assert.IsType<ProjectCeilingStoreException>(failure);
        else if (population is "unknown-branch" or "unregistered-target") Assert.IsType<CliArgumentException>(failure);
        else Assert.IsType<ProjectNotTrustedException>(failure);
        Assert.Equal(before, await File.ReadAllBytesAsync(ProjectCeilingStore.DefaultPath, Ct));
        Assert.False(File.Exists(BatonPaths.QueueFile));
        Assert.False(Directory.Exists(Path.Combine(home.Path, "w2560-2")));
    }

    [Theory]
    [InlineData("source-revoked", 0)]
    [InlineData("candidate-changed", 0)]
    [InlineData("cleanup-changed", 0)]
    [InlineData("late-revocation", 2)]
    public async Task Fresh_evidence_is_rechecked_and_late_failure_retains_truthful_blocked_reservation(string race, int expectedMutations)
    {
        using var home = new IsolatedBatonHome();
        var source = Directory.CreateDirectory(Path.Combine(home.Path, "source")).FullName;
        var target = Path.Combine(home.Path, "w2560");
        var brief = Path.Combine(home.Path, "brief.md");
        await File.WriteAllTextAsync(brief, "input", Ct);
        if (race is "candidate-changed" or "cleanup-changed")
        {
            Directory.CreateDirectory(target);
            ProjectCeilingStore.Set(target, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        }
        else ProjectCeilingStore.Set(source, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        await ConductorClaimStore.ClaimAsync(Repository, "owner", home.Path, cancellationToken: Ct);
        var changed = false;
        var mutations = 0;
        Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(Repository);
        Task<(int, string)> Runner(string file, IReadOnlyList<string> args, string directory, CancellationToken token)
        {
            if (args is ["worktree", "list", "--porcelain"])
                return Task.FromResult((0, $"worktree {target}\nbranch refs/heads/{(changed ? "old-lane" : "2560-lane")}\n"));
            if (changed && args is ["show-ref", "--verify", "--quiet", "refs/heads/2560-lane"])
                return Task.FromResult((0, ""));
            if (file == "gh" && args is ["issue", "develop", ..])
            {
                mutations++;
                return Task.FromResult((0, ""));
            }
            if (args is ["worktree", "add", var path, ..])
            {
                mutations++;
                Directory.CreateDirectory(path);
                if (race == "late-revocation") ProjectCeilingStore.Revoke(source, ProjectCeilingStore.DefaultPath);
                return Task.FromResult((0, ""));
            }
            return IssuePreparationRunner.NoCollisions(file, args, directory, token);
        }
        async Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(int issue, string project, string? root,
            string repository, bool lifecycle, TextWriter writer, CancellationToken token)
        {
            if (race == "source-revoked") ProjectCeilingStore.Revoke(source, ProjectCeilingStore.DefaultPath);
            if (race == "candidate-changed") changed = true;
            if (race == "cleanup-changed")
                Assert.NotNull(await QueueStore.TryClaimWorktreeCleanupAsync(BatonPaths.QueueFile, target,
                    Repository.Value, "2560-lane", new string('a', 40), token));
            return await IssueWorktreeProvisioner.ProvisionAsync(issue, project, root, repository, Runner, Resolve,
                writer, token, deterministicSourceCeiling: lifecycle);
        }
        Assert.Equal(0, await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Submit, 2560, source,
            new TaskSizeDeclaration(DeclaredTaskSize.Small, "one trust race"), brief),
            TextWriter.Null, Resolve, Provision, Ct, Runner));
        Assert.Equal(expectedMutations, mutations);
        var row = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
        Assert.Equal(TaskPreparationState.Blocked, row.IssuePreparation!.State);
        Assert.Null(row.WorkerAssignment);
        Assert.Null(row.IssuePreparation.ExpectedWorkspace);
        Assert.Null(row.AttemptId);
        Assert.False(File.Exists(row.SpecFile));
        if (race == "late-revocation")
        {
            Assert.True(Directory.Exists(target));
            Assert.Null(ProjectCeilingStore.TryGetRecord(target, ProjectCeilingStore.DefaultPath));
        }
        var queueBytes = await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct);
        Assert.Equal(0, await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Submit, 2560, source,
            new TaskSizeDeclaration(DeclaredTaskSize.Small, "one trust race"), brief),
            TextWriter.Null, Resolve, Provision, Ct, (_, _, _, _) => throw new InvalidOperationException("idempotency must precede preflight")));
        Assert.Equal(queueBytes, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
        Assert.Equal(expectedMutations, mutations);
    }

    [Fact]
    public async Task Two_fresh_preflights_still_reserve_and_provision_only_once()
    {
        using var home = new IsolatedBatonHome();
        var source = Directory.CreateDirectory(Path.Combine(home.Path, "source")).FullName;
        var target = Path.Combine(home.Path, "w2560");
        var brief = Path.Combine(home.Path, "brief.md");
        await File.WriteAllTextAsync(brief, "input", Ct);
        await ConductorClaimStore.ClaimAsync(Repository, "owner", home.Path, cancellationToken: Ct);
        var observed = 0;
        var entered = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provisionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(Repository);
        async Task<(int, string)> Runner(string file, IReadOnlyList<string> args, string directory, CancellationToken token)
        {
            if (args is ["show-ref", ..])
            {
                if (Interlocked.Increment(ref observed) == 2) both.SetResult();
                await both.Task.WaitAsync(token);
            }
            return await IssuePreparationRunner.NoCollisions(file, args, directory, token);
        }
        async Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(int issue, string project, string? root,
            string repository, bool lifecycle, TextWriter writer, CancellationToken token)
        {
            Interlocked.Increment(ref entered);
            provisionEntered.SetResult();
            await release.Task.WaitAsync(token);
            Directory.CreateDirectory(target);
            ProjectCeilingStore.Set(target, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
            return new(target, "2560-lane");
        }
        var options = new TaskOptions(TaskVerb.Submit, 2560, source,
            new TaskSizeDeclaration(DeclaredTaskSize.Small, "one reservation race"), brief);
        var first = TaskCommand.ExecuteAsync(options, TextWriter.Null, Resolve, Provision, Ct, Runner);
        var second = TaskCommand.ExecuteAsync(options, TextWriter.Null, Resolve, Provision, Ct, Runner);
        await provisionEntered.Task.WaitAsync(Ct);
        await Task.WhenAny(first, second).WaitAsync(Ct);
        Assert.Equal(2, observed);
        Assert.Equal(1, entered);
        Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
        release.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Equal(new[] { 0, 0 }, results);
        Assert.Equal(1, entered);
        Assert.Equal(TaskPreparationState.Prepared, Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).IssuePreparation!.State);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("revoked")]
    [InlineData("narrowed")]
    [InlineData("unchanged")]
    public async Task Prepared_commit_rechecks_exact_target_after_earlier_admission(string change)
    {
        using var home = new IsolatedBatonHome();
        var source = Directory.CreateDirectory(Path.Combine(home.Path, "source")).FullName;
        var target = Path.Combine(home.Path, "w2560");
        var brief = Path.Combine(home.Path, "brief.md");
        await File.WriteAllTextAsync(brief, "unchanged original input", Ct);
        var specDestination = BatonPaths.QueueSpecFile("2560-lane");
        Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
        await File.WriteAllTextAsync(specDestination, "previous copied bytes", Ct);
        var copiedBefore = await File.ReadAllBytesAsync(specDestination, Ct);
        var briefBefore = await File.ReadAllBytesAsync(brief, Ct);
        ProjectCeilingStore.Set(source, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        var reads = 0;
        var writes = 0;
        Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(Repository);
        Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(int issue, string project, string? root,
            string repository, bool lifecycle, TextWriter writer, CancellationToken token)
        {
            Directory.CreateDirectory(target);
            ProjectCeilingStore.Set(target, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
            return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(target, "2560-lane"));
        }
        var failure = await Record.ExceptionAsync(() => QueueCommand.ExecuteAsync(
            new QueueOptions(QueueVerb.Add, Tag: "2560-lane", Role: "implement", SpecFilePath: brief,
                Issue: 2560, Lifecycle: true,
                DeclaredTaskSize: new TaskSizeDeclaration(DeclaredTaskSize.Small, "one exact target recheck")),
            TextWriter.Null, Ct, source, Resolve, Provision,
            writeSpecFile: (path, contents) =>
            {
                writes++;
                QueueCommand.WriteSpecFileAtomically(path, contents);
            },
            preparationRunner: IssuePreparationRunner.NoCollisions,
            beforePreparationCommit: () =>
            {
                reads++;
                var reservation = Assert.Single(QueueStore.LoadAsync(BatonPaths.QueueFile, Ct).GetAwaiter().GetResult().Items);
                Assert.Equal(TaskPreparationState.Preparing, reservation.IssuePreparation!.State);
                Assert.Equal(target, reservation.IssuePreparation.ExpectedWorkspace);
                Assert.True(ProjectCeilingStore.TryGetRecord(target, ProjectCeilingStore.DefaultPath)!.IsUnrestricted);
                Assert.True(RecordedProjectCeilingAdmission.Evaluate(new QueueItem
                {
                    Tag = "2560-lane",
                    Role = "implement",
                    Workspace = target,
                    SpecFile = specDestination,
                    Requirements = [],
                }, WorkerRoleCatalog.For("implement"), false).Admission.Result != TaskRequirementAdmission.Refused);
                switch (change)
                {
                    case "deleted": ProjectCeilingStore.Forget(target, ProjectCeilingStore.DefaultPath); break;
                    case "revoked": ProjectCeilingStore.Revoke(target, ProjectCeilingStore.DefaultPath); break;
                    case "narrowed": ProjectCeilingStore.Set(target, new ProjectCeiling(true, true, true, false), ProjectCeilingStore.DefaultPath); break;
                }
            }));
        Assert.Equal(1, reads);
        var row = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
        Assert.Equal(briefBefore, await File.ReadAllBytesAsync(brief, Ct));
        Assert.True(ProjectCeilingStore.TryGetRecord(source, ProjectCeilingStore.DefaultPath)!.IsUnrestricted);
        Assert.Null(row.AttemptId);
        Assert.True(Directory.Exists(target));
        if (change == "unchanged")
        {
            Assert.Null(failure);
            Assert.Equal(1, writes);
            Assert.Equal(TaskPreparationState.Prepared, row.IssuePreparation!.State);
            Assert.NotNull(row.WorkerAssignment);
            Assert.Equal(TaskRequirementAdmission.Admitted, row.LastAdmission!.Result);
            Assert.Contains("unchanged original input", await File.ReadAllTextAsync(specDestination, Ct), StringComparison.Ordinal);
        }
        else
        {
            Assert.IsType<CliArgumentException>(failure);
            Assert.Equal(0, writes);
            Assert.Equal(TaskPreparationState.Blocked, row.IssuePreparation!.State);
            Assert.Equal(target, row.IssuePreparation.ExpectedWorkspace);
            Assert.Null(row.WorkerAssignment);
            Assert.Equal(copiedBefore, await File.ReadAllBytesAsync(specDestination, Ct));
        }
    }

    [Theory]
    [InlineData("preparing")]
    [InlineData("blocked")]
    [InlineData("queued")]
    [InlineData("launched")]
    [InlineData("review")]
    [InlineData("ready")]
    [InlineData("done")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("retired")]
    public async Task Identical_input_bypasses_preflight_and_changed_input_conflicts_at_every_retained_stage(string retainedStage)
    {
        using var home = new IsolatedBatonHome();
        var source = Directory.CreateDirectory(Path.Combine(home.Path, "source")).FullName;
        var target = Path.Combine(home.Path, "w2560");
        var brief = Path.Combine(home.Path, "brief.md");
        await File.WriteAllTextAsync(brief, "input", Ct);
        await ConductorClaimStore.ClaimAsync(Repository, "owner", home.Path, cancellationToken: Ct);
        Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(Repository);
        Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(int issue, string project, string? root,
            string repository, bool lifecycle, TextWriter writer, CancellationToken token)
        {
            Directory.CreateDirectory(target);
            ProjectCeilingStore.Set(target, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
            return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(target, "2560-lane"));
        }
        var options = new TaskOptions(TaskVerb.Submit, 2560, source,
            new TaskSizeDeclaration(DeclaredTaskSize.Small, "one idempotency population"), brief);
        Assert.Equal(0, await TaskCommand.ExecuteAsync(options, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
        {
            Items = snapshot.Items.Select(row => row with
            {
                IssuePreparation = row.IssuePreparation! with
                {
                    State = retainedStage == "preparing" ? TaskPreparationState.Preparing
                        : retainedStage == "blocked" ? TaskPreparationState.Blocked : TaskPreparationState.Prepared,
                },
                Stage = retainedStage == "review" ? WorkStage.Review : retainedStage == "ready" ? WorkStage.Ready : WorkStage.Implement,
                State = retainedStage switch
                {
                    "launched" => QueueItemState.Launched,
                    "done" => QueueItemState.Done,
                    "failed" => QueueItemState.Failed,
                    "cancelled" => QueueItemState.Cancelled,
                    _ => QueueItemState.Queued,
                },
                Retirement = retainedStage == "retired" ? new QueueRetirement("operator", DateTimeOffset.UtcNow, "retained disposition") : null,
            }).ToList(),
        }, Ct);
        ProjectCeilingStore.Revoke(target, ProjectCeilingStore.DefaultPath);
        var before = await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct);
        Task<(int, string)> NoProbe(string file, IReadOnlyList<string> args, string directory, CancellationToken token) =>
            throw new InvalidOperationException("retained input must not reach trust preflight");
        Assert.Equal(0, await TaskCommand.ExecuteAsync(options, TextWriter.Null, Resolve, Provision, Ct, NoProbe));
        await File.WriteAllTextAsync(brief, "changed explicit input", Ct);
        await Assert.ThrowsAsync<CliArgumentException>(() => TaskCommand.ExecuteAsync(options, TextWriter.Null,
            Resolve, Provision, Ct, NoProbe));
        Assert.Equal(before, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
    }
}
