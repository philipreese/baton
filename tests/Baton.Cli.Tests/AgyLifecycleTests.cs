extern alias old068;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Cli.Tests.TestSupport;
using Baton.Conductor;
using Baton.Domain;
using Baton.Mutation;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;
using OldQueue = old068::Baton.Compatibility068.IsolatedQueueProbe;

namespace Baton.Cli.Tests;

public sealed class AgyLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Named_transport_only_stage_is_retained_without_an_axis_rationale()
    {
        var task = TaskOptionsParser.Parse(["submit", "--issue", "42", "--project", "C:/repo",
            "--declared-size", "small", "--size-rationale", "one cluster", "--scope", "engine",
            "--stage", "review", "--enable-agy-correction"]);
        var selection = Assert.Single(task.StageSelections!);
        Assert.Equal(WorkStage.Review, selection.Stage);
        Assert.Null(selection.Adapter);
        Assert.Null(selection.Reason);
        Assert.Contains("\"EnableAgyCorrection\":true", JsonSerializer.Serialize(selection), StringComparison.Ordinal);
    }

    [Fact]
    public void Queue_requires_a_named_unpinned_lifecycle_stage_and_nonrepeatable_flag()
    {
        string[] task = ["submit", "--issue", "42", "--project", "C:/repo", "--declared-size", "small",
            "--size-rationale", "one cluster"];
        string[] queue = ["add", "--issue", "42", "--lifecycle", "--declared-size", "small",
            "--size-rationale", "one cluster", "--scope", "engine"];
        var parsed = QueueOptionsParser.Parse([.. queue, "--stage", "review", "--enable-agy-correction",
            "--stage", "fix", "--adapter", "agy", "--reason", "explicit stage binding"]);
        Assert.True(parsed.StageSelections![0].EnableAgyCorrection);
        Assert.False(parsed.StageSelections[1].EnableAgyCorrection);
        Assert.Null(parsed.StageSelections[0].Adapter);
        Assert.True(QueueOptionsParser.Parse([.. queue, "--adapter", "agy", "--reason", "explicit",
            "--stage", "implement", "--enable-agy-correction"]).StageSelections!.Single().EnableAgyCorrection);
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. task, "--enable-agy-correction"]));
        Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse([.. queue, "--enable-agy-correction"]));
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. task, "--stage", "review",
            "--enable-agy-correction", "--stage", "review", "--enable-agy-correction"]));
        Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse([.. queue, "--stage", "review",
            "--enable-agy-correction", "--enable-agy-correction"]));
        Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse([.. queue, "--lifecycle-pin",
            "--stage", "review", "--enable-agy-correction"]));
        Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse(["add", "single", "--role", "review",
            "--workspace", "C:/repo", "--spec", "brief.md", "--stage", "review", "--enable-agy-correction"]));
    }

    [Fact]
    public void Opted_digest_covers_all_explicit_inputs_and_stable_stage_declarations()
    {
        var size = new TaskSizeDeclaration(DeclaredTaskSize.Small, "one cluster");
        QueueStageSelection[] stages =
        [
            new() { Stage = WorkStage.Review, EnableAgyCorrection = true },
            new() { Stage = WorkStage.Fix, Adapter = "agy", Model = "gemini-3.1-pro", Effort = "high", Reason = "explicit" },
        ];
        string Digest(string repository = "github.com/example/repo", int issue = 42,
            TaskSizeDeclaration? declaration = null, byte[]? spec = null, string? scope = "engine", string? reason = null,
            IReadOnlyList<QueueStageSelection>? selections = null, int timeout = 12, int tools = 8, long tokens = 2000) =>
            TaskCommand.ComputeInputDigest(repository, issue, declaration ?? size, spec ?? Encoding.UTF8.GetBytes("brief"),
                null, scope, reason, selections ?? stages, timeout, tools, tokens);
        var expected = Digest();
        Assert.Equal(expected, Digest(selections: stages.Reverse().ToArray()));
        Assert.NotEqual(expected, Digest(repository: "github.com/example/other"));
        Assert.NotEqual(expected, Digest(issue: 43));
        Assert.NotEqual(expected, Digest(declaration: size with { Rationale = "other" }));
        Assert.NotEqual(expected, Digest(declaration: size with { Size = DeclaredTaskSize.Medium }));
        Assert.NotEqual(expected, Digest(spec: []));
        Assert.NotEqual(expected, Digest(scope: "docs"));
        Assert.NotEqual(expected, Digest(reason: "global"));
        Assert.NotEqual(expected, Digest(timeout: 13));
        Assert.NotEqual(expected, Digest(tools: 9));
        Assert.NotEqual(expected, Digest(tokens: 2001));
        Assert.NotEqual(expected, Digest(selections: [stages[0] with { EnableAgyCorrection = false }, stages[1]]));
        Assert.NotEqual(expected, Digest(selections: [stages[0], stages[1] with { EnableAgyCorrection = true }]));
        Assert.NotEqual(expected, Digest(selections: [stages[0] with { Stage = WorkStage.Continue }, stages[1]]));
        foreach (var changed in new[]
        {
            stages[1] with { Adapter = "codex" }, stages[1] with { Model = "different" },
            stages[1] with { Effort = "medium" }, stages[1] with { Reason = "different" },
            stages[1] with { Requirements = [TaskRequirements.RepositoryRead] },
        })
            Assert.NotEqual(expected, Digest(selections: [stages[0], changed]));
    }

    [Fact]
    public async Task Submission_replay_and_transport_only_conflict_preserve_accepted_bytes()
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;
            await ConductorClaimStore.ClaimAsync(repository, "owner", home, cancellationToken: Ct);
            var project = Directory.CreateDirectory(Path.Combine(home, "source")).FullName;
            var workspace = Path.Combine(home, "workspace");
            var spec = Path.Combine(home, "brief.md");
            await File.WriteAllTextAsync(spec, "bounded task", Ct);
            var provisions = 0;
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(repository);
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(int issue, string source, string? root,
                string repo, bool lifecycle, TextWriter writer, CancellationToken token)
            {
                provisions++;
                Directory.CreateDirectory(workspace);
                ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "42-lane"));
            }
            var options = TaskOptionsParser.Parse(["submit", "--issue", "42", "--project", project,
                "--declared-size", "small", "--size-rationale", "one cluster", "--spec", spec,
                "--timeout", "12", "--max-tool-steps", "8", "--token-budget", "2000",
                "--stage", "implement", "--adapter", "agy", "--enable-agy-correction",
                "--stage", "review", "--adapter", "codex"]);
            Assert.Equal(0, await TaskCommand.ExecuteAsync(options, TextWriter.Null, Resolve, Provision, Ct,
                IssuePreparationRunner.NoCollisions));
            var bytes = await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.True(item.StageSelections!.Single(selection => selection.Stage == WorkStage.Implement).EnableAgyCorrection);
            Assert.False(item.StageSelections!.Single(selection => selection.Stage == WorkStage.Review).EnableAgyCorrection);
            Assert.Equal(0, await TaskCommand.ExecuteAsync(options, TextWriter.Null, Resolve, Provision, Ct));
            await Assert.ThrowsAsync<CliArgumentException>(() => TaskCommand.ExecuteAsync(options with
            {
                StageSelections = options.StageSelections!.Select(selection => selection with { EnableAgyCorrection = false }).ToArray(),
            }, TextWriter.Null, Resolve, Provision, Ct));
            Assert.Equal(1, provisions);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Theory]
    [InlineData(WorkStage.Implement)]
    [InlineData(WorkStage.Review)]
    [InlineData(WorkStage.Fix)]
    [InlineData(WorkStage.ReReview)]
    [InlineData(WorkStage.Continue)]
    public void Mixed_stage_options_argv_materialization_and_real_resolver_select_only_transport(WorkStage selected)
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            ProjectCeilingStore.Set(home, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
            var item = Item(home) with
            {
                Stage = selected,
                Role = WorkStages.RoleFor(selected),
                Repository = "github.com/example/repo",
                Branch = "42-lane",
                PullRequest = 43,
                StageSelections = [new QueueStageSelection { Stage = selected, EnableAgyCorrection = true }],
                TimeoutMinutes = 12,
                TokenBudget = 2000,
                MaxToolSteps = 8,
                MaxRepeatedToolSteps = 3,
            };
            var tier = new QueueTierResolution(null, "agy", "gemini-3.1-pro", "high", false, null);
            var options = QueueLauncher.BuildOptions(new QueueLaunchRequest(item, tier, Path.Combine(home, "room")));
            var parsed = DispatchOptionsParser.Parse(QueueLauncher.BuildArguments(options).Skip(1).ToArray());
            Assert.True(parsed.EnableAgyCorrection);
            Assert.Null(parsed.ContinueFromRoomDirectoryPath);
            Assert.Equal(options.Timeout, parsed.Timeout);
            Assert.Equal(options.TokenBudget, parsed.TokenBudget);
            Assert.Equal(options.MaxToolSteps, parsed.MaxToolSteps);
            Assert.Equal(options.MaxRepeatedToolSteps, parsed.MaxRepeatedToolSteps);
            var role = WorkerRoleCatalog.For(item.Role);
            var opted = Materialize(role, parsed);
            var ordinary = Materialize(role, parsed with { EnableAgyCorrection = false });
            var binding = opted.Bindings.Single().Value;
            var control = ordinary.Bindings.Single().Value;
            Assert.True(binding.EnableAgyCorrection);
            Assert.True(binding.StreamJson);
            Assert.False(binding.ResumeSession);
            Assert.Equal(control.PermissionGrant, binding.PermissionGrant);
            Assert.Equal(control.Timeout, binding.Timeout);
            Assert.Equal(control.TokenBudget, binding.TokenBudget);
            Assert.Equal(control.MaxToolSteps, binding.MaxToolSteps);
            Assert.Equal(control.MaxRepeatedToolSteps, binding.MaxRepeatedToolSteps);
            Assert.Equal(JsonSerializer.Serialize(ordinary.Definition), JsonSerializer.Serialize(opted.Definition));
            var resolved = Assert.IsType<WorkerBinding.Process>(WorkerBindingResolver.Resolve(
                opted.Bindings, WorkerAdapterRegistry.Default, bindingsFileDirectory: home)[item.Role]);
            Assert.Equal(AgyStreamingHost.Transport, resolved.Target.ExactRunningTransport);
            Assert.NotNull(resolved.Target.CreateProcessTransport);
            Assert.Equal(control.PermissionGrant, binding.PermissionGrant);
            var omitted = Assert.IsType<WorkerBinding.Process>(WorkerBindingResolver.Resolve(
                ordinary.Bindings, WorkerAdapterRegistry.Default, bindingsFileDirectory: home)[item.Role]);
            Assert.Null(omitted.Target.ExactRunningTransport);
            Assert.False(QueueLauncher.BuildOptions(new QueueLaunchRequest(item with
            {
                Stage = selected == WorkStage.Review ? WorkStage.Implement : WorkStage.Review,
            }, tier, Path.Combine(home, "other"))).EnableAgyCorrection);
            var fallback = WorkerBindingResolver.ToFallbackEntry(binding, new FallbackBinding("agy"));
            Assert.False(fallback.EnableAgyCorrection);
            Assert.Throws<InvalidOperationException>(() => WorkerBindingResolver.Resolve(
                new Dictionary<string, WorkerBindingConfigEntry> { [item.Role] = binding with { Adapter = "codex", Model = "gpt-6.1-sol" } },
                WorkerAdapterRegistry.Default, bindingsFileDirectory: home));
            Assert.Throws<InvalidOperationException>(() => WorkerBindingResolver.Resolve(
                new Dictionary<string, WorkerBindingConfigEntry> { [item.Role] = binding with { ResumeSession = true } },
                WorkerAdapterRegistry.Default, bindingsFileDirectory: home));
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Theory]
    [InlineData("{\"version\":2,\"selections\":[]}")]
    [InlineData("{\"version\":\"1\",\"selections\":[]}")]
    [InlineData("{\"version\":1,\"selections\":[]}")]
    [InlineData("{\"version\":1,\"selections\":[null]}")]
    [InlineData("{\"version\":1,\"selections\":[{\"Stage\":\"Ready\",\"EnableAgyCorrection\":true}]}")]
    [InlineData("{\"version\":1,\"selections\":[{\"Stage\":99,\"EnableAgyCorrection\":true}]}")]
    [InlineData("{\"version\":1,\"selections\":[{\"EnableAgyCorrection\":true}]}")]
    [InlineData("{\"version\":1,\"selections\":[{\"Stage\":\"Review\",\"EnableAgyCorrection\":true},{\"Stage\":\"Review\"}]}")]
    [InlineData("[{\"Stage\":\"Review\",\"EnableAgyCorrection\":true}]")]
    public async Task Malformed_or_unprotected_transport_refuses_before_mutation(string selections)
    {
        var home = TempHome();
        try
        {
            var path = Path.Combine(home, "queue.json");
            await File.WriteAllTextAsync(path, $$"""{"items":[{"Tag":"test","Role":"review","Workspace":"fixture","SpecFile":"fixture","Stage":"Review","StageSelections":{{selections}}}]}""", Ct);
            var before = await File.ReadAllBytesAsync(path, Ct);
            var refusal = await Assert.ThrowsAsync<QueueStoreException>(() => QueueStore.LoadAsync(path, Ct));
            if (selections.StartsWith("{\"version\":2", StringComparison.Ordinal)
                || selections.StartsWith("{\"version\":\"1\"", StringComparison.Ordinal))
                Assert.Contains("Unsupported or malformed stage selections representation; use a compatible release.",
                    refusal.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<QueueStoreException>(() => QueueStore.MutateAsync(path,
                _ => throw new InvalidOperationException("callback must never be entered"), Ct));
            Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frozen_transport_requires_its_matching_retained_stage_declaration(bool strip)
    {
        var home = TempHome();
        try
        {
            var item = Item(home) with { AttemptEnvelope = Envelope(home), StageSelections = ProtectedSelections() };
            var path = Path.Combine(home, "queue.json");
            await QueueStore.MutateAsync(path, _ => new QueueSnapshot([item]), Ct);
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!;
            var row = json["items"]![0]!;
            if (strip) row["StageSelections"] = null;
            else row["AttemptEnvelope"]!["enableAgyCorrection"] = false;
            await File.WriteAllTextAsync(path, json.ToJsonString(), Ct);
            var before = await File.ReadAllBytesAsync(path, Ct);
            await Assert.ThrowsAsync<QueueStoreException>(() => QueueStore.LoadAsync(path, Ct));
            await Assert.ThrowsAsync<QueueStoreException>(() => QueueStore.MutateAsync(path,
                _ => throw new InvalidOperationException("callback must never be entered"), Ct));
            Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
            var launchRefusal = Assert.Throws<CliArgumentException>(() => QueueLauncher.BuildOptions(new QueueLaunchRequest(item with
            {
                StageSelections = strip ? null : [],
            }, new QueueTierResolution(null, "codex", null, null, false, null), Path.Combine(home, "room"))));
            Assert.Equal("Missing or contradictory retained AGY correction declaration; use a compatible release.",
                launchRefusal.Message);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Fact]
    public async Task Exact_068_reader_mutates_and_fake_launches_legacy_but_refuses_every_protected_snapshot()
    {
        Assert.True(OldQueue.HasExactSource());
        var home = TempHome();
        try
        {
            var path = Path.Combine(home, "queue.json");
            var item = Item(home) with { StageSelections = [] };
            await QueueStore.MutateAsync(path, _ => new QueueSnapshot([item]), Ct);
            Assert.False(await OldQueue.ReadRefusedAsync(path));
            var mutations = 0;
            var launches = 0;
            Assert.True(await OldQueue.MutateAndLaunchAsync(path, () => mutations++, () => launches++));
            Assert.Equal(1, mutations);
            Assert.Equal(1, launches);
            Assert.DoesNotContain("EnableAgyCorrection", await File.ReadAllTextAsync(path, Ct), StringComparison.Ordinal);
            item = item with { StageSelections = ProtectedSelections() };
            foreach (var snapshot in new[]
            {
                item,
                item with { AttemptEnvelope = Envelope(home), AttemptAdmissionFactDurable = true },
                item with
                {
                    AttemptEnvelope = Envelope(home),
                    State = QueueItemState.Launched,
                    LaunchMayHaveBegunAt = DateTimeOffset.UtcNow },
                item with { Stage = WorkStage.Review, Role = "review", State = QueueItemState.Done },
                item with { Stage = WorkStage.Ready, State = QueueItemState.Cancelled },
                item with
                {
                    Stage = WorkStage.Ready,
                    State = QueueItemState.Done,
                    Retirement = new QueueRetirement(QueueRetirement.Operator, DateTimeOffset.UtcNow, "retained")
                },
            })
            {
                await QueueStore.MutateAsync(path, _ => new QueueSnapshot([snapshot]), Ct);
                await Refused();
                await QueueStore.MutateAsync(path, current => current with
                {
                    Held = true,
                    Items = [.. current.Items, Item(home) with { Tag = "unrelated", Stage = null }]
                }, Ct);
                await Refused();
                await QueueStore.MutateAsync(path, current => current with
                {
                    Items = current.Items.Select(row => row.Tag == item.Tag ? row with
                    {
                        Stage = WorkStage.Ready,
                        State = QueueItemState.Done,
                    } : row).ToArray(),
                }, Ct);
                await Refused();
            }
            async Task Refused()
            {
                var before = await File.ReadAllBytesAsync(path, Ct);
                Assert.True(await OldQueue.ReadRefusedAsync(path));
                Assert.False(await OldQueue.MutateAndLaunchAsync(path, () => mutations++, () => launches++));
                Assert.Equal(1, mutations);
                Assert.Equal(1, launches);
                Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
                Assert.False(Directory.Exists(Path.Combine(home, "room")));
                Assert.True(Assert.Single((await QueueStore.LoadAsync(path, Ct)).Items, row => row.Tag == item.Tag)
                    .StageSelections!.Single().EnableAgyCorrection);
            }
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[{\"Stage\":\"Review\",\"Adapter\":\"agy\",\"Model\":\"gemini-3.1-pro\",\"Effort\":\"high\",\"Requirements\":[\"repository-read\"],\"Reason\":\"explicit\"}]")]
    public async Task Historical_null_and_nonempty_arrays_keep_exact_old_writer_representation(string selections)
    {
        var home = TempHome();
        try
        {
            var path = Path.Combine(home, "queue.json");
            await QueueStore.MutateAsync(path, _ => new QueueSnapshot([Item(home)]), Ct);
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!;
            json["items"]![0]!["StageSelections"] = JsonNode.Parse(selections);
            await File.WriteAllTextAsync(path, json.ToJsonString(), Ct);
            var loaded = Assert.Single((await QueueStore.LoadAsync(path, Ct)).Items);
            if (selections == "null") Assert.Null(loaded.StageSelections);
            else Assert.False(Assert.Single(loaded.StageSelections!).EnableAgyCorrection);
            await QueueStore.MutateAsync(path, snapshot => snapshot with { Held = true }, Ct);
            var currentBytes = await File.ReadAllBytesAsync(path, Ct);
            var newRepresentation = JsonNode.Parse(currentBytes)!["items"]![0]!["StageSelections"]?.ToJsonString() ?? "null";
            Assert.Equal(selections, newRepresentation);
            Assert.DoesNotContain("EnableAgyCorrection", Encoding.UTF8.GetString(currentBytes), StringComparison.Ordinal);
            var mutations = 0;
            var launches = 0;
            Assert.True(await OldQueue.MutateAndLaunchAsync(path, () => mutations++, () => launches++));
            Assert.Equal(1, mutations);
            Assert.Equal(1, launches);
            var oldRepresentation = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!["items"]![0]!["StageSelections"]?.ToJsonString() ?? "null";
            Assert.Equal(newRepresentation, oldRepresentation);
            await QueueStore.MutateAsync(path, snapshot => snapshot with { Held = true }, Ct);
            Assert.Equal(currentBytes, await File.ReadAllBytesAsync(path, Ct));
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Fact]
    public async Task Admission_freezes_transport_and_restart_uses_envelope_against_changed_defaults()
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = Item(home) with
            {
                StageSelections = [new QueueStageSelection
                {
                    Stage = WorkStage.Implement,
                    Adapter = "agy",
                    EnableAgyCorrection = true
                }],
                TimeoutMinutes = 12,
                MaxToolSteps = 8,
                TokenBudget = 2000,
            };
            await File.WriteAllTextAsync(item.SpecFile, "bounded task", Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, _ => new QueueSnapshot([item]), Ct);
            var first = new QueueSchedulerService((_, _) => throw new InvalidOperationException("must freeze first"),
                _ => Task.FromResult(0d), () => 16d, () => DateTimeOffset.UtcNow,
                beforeLaunchClaim: _ => throw new InvalidOperationException("simulated restart"),
                workspaceHead: (_, _) => Task.FromResult<string?>("base"), workspaceLocks: _ => []);
            var stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => first.TickOnceAsync(Ct));
            Assert.Equal("simulated restart", stopped.Message);
            var frozen = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.True(frozen.AttemptEnvelope!.EnableAgyCorrection);
            Assert.Equal("agy", frozen.AttemptEnvelope.Adapter);
            Assert.True(frozen.AttemptAdmissionFactDurable);
            Assert.False(Directory.Exists(frozen.AttemptEnvelope.RoomDirectory));
            var protectedBytes = await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct);
            Assert.True(await OldQueue.ReadRefusedAsync(BatonPaths.QueueFile));
            Assert.False(await OldQueue.MutateAndLaunchAsync(BatonPaths.QueueFile,
                () => throw new InvalidOperationException("old mutation entered"),
                () => throw new InvalidOperationException("old launch entered")));
            Assert.Equal(protectedBytes, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [frozen with { StageSelections = [frozen.StageSelections!.Single() with { Adapter = "codex" }] }],
            }, Ct);
            QueueLaunchRequest? launch = null;
            var restarted = new QueueSchedulerService((request, _) =>
            {
                launch = request;
                return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
            }, _ => Task.FromResult(0d), () => 16d, () => DateTimeOffset.UtcNow, workspaceLocks: _ => []);
            await restarted.TickOnceAsync(Ct);
            Assert.NotNull(launch);
            Assert.Equal(JsonSerializer.Serialize(frozen.AttemptEnvelope), JsonSerializer.Serialize(launch!.Item.AttemptEnvelope));
            Assert.Equal(frozen.AttemptEnvelope.EffectiveGrant, launch.Item.LastAdmission!.EffectiveGrant);
            var options = QueueLauncher.BuildOptions(launch);
            Assert.True(options.EnableAgyCorrection);
            Assert.Equal("agy", options.Adapter);
            Assert.Equal(TimeSpan.FromMinutes(12), options.Timeout);
            Assert.Equal(8, options.MaxToolSteps);
            Assert.Equal(2000, options.TokenBudget);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_declaration_race_rejects_stale_admission_before_commit_or_claim(bool afterAdmission)
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = Item(home) with
            {
                StageSelections = [new QueueStageSelection
                {
                    Stage = WorkStage.Implement,
                    Adapter = "agy", EnableAgyCorrection = true,
                }]
            };
            await File.WriteAllTextAsync(item.SpecFile, "bounded task", Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, _ => new QueueSnapshot([item]), Ct);
            var changed = false;
            var launches = new List<QueueLaunchRequest>();
            async Task ChangeOnce()
            {
                if (changed) return;
                changed = true;
                await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
                {
                    Items = snapshot.Items.Select(row => row with
                    {
                        StageSelections = [row.StageSelections!.Single() with { EnableAgyCorrection = false }],
                        AttemptEnvelope = row.AttemptEnvelope is { } envelope
                            ? envelope with { EnableAgyCorrection = false } : null,
                    }).ToArray(),
                }, Ct);
            }
            var service = new QueueSchedulerService((request, _) =>
            {
                launches.Add(request);
                return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
            }, _ => Task.FromResult(0d), () => 16d, () => DateTimeOffset.UtcNow,
                beforeLaunchClaim: afterAdmission ? _ => ChangeOnce() : null,
                workspaceHead: async (_, _) =>
                {
                    if (!afterAdmission) await ChangeOnce();
                    return "base";
                }, workspaceLocks: _ => []);
            await service.TickOnceAsync(Ct);
            Assert.True(changed);
            var launch = Assert.Single(launches);
            Assert.False(launch.Item.AttemptEnvelope!.EnableAgyCorrection);
            Assert.False(QueueLauncher.BuildOptions(launch).EnableAgyCorrection);
            Assert.False(Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items)
                .AttemptEnvelope!.EnableAgyCorrection);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Fact]
    public async Task Lifecycle_queue_add_retains_transport_only_review_without_implicitly_selecting_agy()
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(home, "source")).FullName;
            var workspace = Path.Combine(home, "workspace");
            var spec = Path.Combine(home, "brief.md");
            await File.WriteAllTextAsync(spec, "bounded task", Ct);
            var repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(int issue, string source, string? root,
                string repo, bool lifecycle, TextWriter writer, CancellationToken token)
            {
                Directory.CreateDirectory(workspace);
                ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "42-lane"));
            }
            var options = QueueOptionsParser.Parse(["add", "--issue", "42", "--lifecycle", "--declared-size", "small",
                "--size-rationale", "one cluster", "--spec", spec, "--scope", "engine",
                "--stage", "review", "--enable-agy-correction"]);
            Assert.Equal(0, await QueueCommand.ExecuteAsync(options, TextWriter.Null, Ct, project,
                (_, _) => Task.FromResult<RepositoryIdentity?>(repository), Provision,
                preparationRunner: IssuePreparationRunner.NoCollisions));
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            var selection = Assert.Single(item.StageSelections!);
            Assert.True(selection.EnableAgyCorrection);
            Assert.Null(selection.Adapter);
            Assert.Null(selection.Reason);
            var settings = await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, Ct);
            var tier = QueueTierTable.ResolveForStage(item, WorkStage.Review, settings.Queue,
                WorkerRoleCatalog.QueueTierFor, WorkerRoleCatalog.QueueTierForRole);
            var ordinaryTier = QueueTierTable.ResolveForStage(item with { StageSelections = [] }, WorkStage.Review,
                settings.Queue, WorkerRoleCatalog.QueueTierFor, WorkerRoleCatalog.QueueTierForRole);
            Assert.Equal(ordinaryTier.Adapter, tier.Adapter);
            Assert.NotEqual("agy", tier.Adapter);
            var dispatch = QueueLauncher.BuildOptions(new QueueLaunchRequest(item with
            {
                Stage = WorkStage.Review,
                Role = "review"
            }, tier, Path.Combine(home, "room")));
            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(dispatch,
                WorkerAdapterRegistry.Default, Ct, evaluateRunway: RunwayTestGate.Admit));
            Assert.Contains("actual resolved adapter", ex.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(dispatch.RoomDirectoryPath));
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    private static (WorkflowDefinition Definition, IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings)
        Materialize(WorkerRole role, DispatchOptions options) => RoleSpecMaterializer.Materialize(role, "bounded task",
            options.Adapter, options.WorkspaceDirectory, options.Model, options.Effort, null, options.Timeout,
            null, options.RoomDirectoryPath, options.TokenBudget, options.MaxToolSteps, null, null,
            attachDefaultSkills: false, maxRepeatedToolStepsOverride: options.MaxRepeatedToolSteps,
            enableAgyCorrection: options.EnableAgyCorrection);

    private static QueueItem Item(string home) => new()
    {
        Tag = "transport",
        Role = "implement",
        Stage = WorkStage.Implement,
        Issue = 42,
        Workspace = home,
        SpecFile = Path.Combine(home, "brief.md"),
        DeclaredTaskSize = new(DeclaredTaskSize.Small, "one cluster"),
        Requirements = [],
    };

    private static QueueStageSelection[] ProtectedSelections() =>
        [new QueueStageSelection { Stage = WorkStage.Implement, EnableAgyCorrection = true }];

    private static QueueAttemptEnvelope Envelope(string home) => new(FleetAttemptId.New(), null, "transport", 42,
        null, WorkStage.Implement, "implement", "agy", "gemini-3.1-pro", "high", [], [], [],
        TaskRequirementAdmission.Admitted, Path.Combine(home, "room"), "room", null, DateTimeOffset.UtcNow,
        EnableAgyCorrection: true);

    private static string TempHome() => Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "baton-agy-lifecycle-" + Guid.NewGuid().ToString("N"))).FullName;
}
