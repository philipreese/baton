using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Cli.Tests.TestSupport;
using Baton.CrashTestHost;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using Baton.Tests.Shared;
using Baton.Templates;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>Real dispatch, acceptance and ledger publication; only vendor/forge processes are fixtures.</summary>
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class OwnedTaskAccountingJourneyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Repository = "github.com/aer-works/baton";
    private const string Branch = "2190-verified-pr-ownership";
    private const string Head = "0123456789abcdef0123456789abcdef01234567";
    private static readonly RepositoryIdentity Identity = RepositoryIdentity.From("https://" + Repository, null)!;
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Transported_implementation_review_fix_rereview_publish_queryable_identity_and_preserve_usage()
    {
        using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Activate();
        var historical = new CostLedgerEntry(CostSourceKind.BatonExecution,
            Repository: Repository, Room: "historical-room", Execution: "historical-unknown", TokensIn: 7);
        await CostLedgerStore.AppendAsync([historical], fixture.Ledger, Ct);

        var stages = new[] { WorkStage.Implement, WorkStage.Review, WorkStage.Fix, WorkStage.ReReview };
        foreach (var stage in stages)
        {
            var (result, request) = await fixture.LaunchAsync(stage);
            var accepted = Assert.Single(await AcceptedAsync(result.RoomDirectoryPath!));
            Assert.Equal(2637, accepted.OwnedTaskIdentity?.Issue);
            Assert.Equal(request.Item.AttemptId?.Value, accepted.OwnedTaskIdentity?.AttemptId);
            Assert.Equal(accepted.ExecutionId.Value, accepted.OwnedTaskIdentity?.ExecutionId);
            Assert.Equal(result.RoomDirectoryPath, accepted.OwnedTaskIdentity?.RoomDirectory);
            var registration = Assert.Single(await RoomRegistryStore.ReadDistinctByRoomAsync(BatonPaths.RoomRegistryFile, Ct),
                r => BatonPaths.RecordKeyComparer.Equals(r.RoomPath, BatonPaths.RecordKey(result.RoomDirectoryPath!)));
            Assert.Equal(fixture.Workspace, registration.ProjectRoot);
            // Repeat actual publication: append-only deduplication must keep one usage row per execution.
            await TerminalSettleRecorder.RecordAsync(result, Ct);
        }

        var rows = await CostLedgerStore.ReadAllAsync(fixture.Ledger, Ct);
        Assert.Equal(5, rows.Count);
        Assert.Equal(historical, rows.Single(r => r.Execution == "historical-unknown"));
        var owned = rows.Where(r => r.Execution != "historical-unknown").ToList();
        Assert.All(owned, r =>
        {
            Assert.Equal("2637", r.Issue);
            Assert.Equal(RepositoryIdentitySource.RecordedRoot, r.IdentitySource);
            Assert.Equal(100, r.TokensIn);
            Assert.Equal(50, r.TokensOut);
        });
        Assert.Equal(new[] { "implement", "review", "implement", "review" }, owned.Select(r => r.Role));

        foreach (var filter in new[] { "2637", "#2637" })
        {
            using var json = await QueryAsync(filter);
            Assert.Equal(4, json.RootElement.GetProperty("rows").GetArrayLength());
            Assert.Equal(400, json.RootElement.GetProperty("total").GetProperty("tokensIn").GetInt64());
            Assert.Equal(200, json.RootElement.GetProperty("total").GetProperty("tokensOut").GetInt64());
        }
        using var unrelated = await QueryAsync("9999");
        Assert.Empty(unrelated.RootElement.GetProperty("rows").EnumerateArray());
        using var all = await QueryAsync(null);
        Assert.Equal(407, all.RootElement.GetProperty("total").GetProperty("tokensIn").GetInt64());
    }

    [Fact]
    public async Task Supplementary_children_publish_their_own_owner_without_borrowing_parent_or_sibling()
    {
        using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Activate();
        var (parent, _) = await fixture.LaunchAsync(WorkStage.Implement);
        var parentRequest = Assert.Single(await AcceptedAsync(parent.RoomDirectoryPath!));
        var owner = parentRequest.OwnedTaskIdentity!;
        var graceParentId = new ExecutionId("grace-accounting-parent");
        var graceId = new ExecutionId("grace-accounting-child");
        var checkpointId = new ExecutionId("checkpoint-accounting-child");
        var legacyCheckpointId = new ExecutionId("legacy-checkpoint-accounting-child");
        var mismatchedCheckpointId = new ExecutionId("mismatched-checkpoint-accounting-child");

        await AppendSupplementaryAccountingEventsAsync(parent.RoomDirectoryPath!, parentRequest, owner, graceParentId,
            graceId, checkpointId, legacyCheckpointId, mismatchedCheckpointId);
        await TerminalSettleRecorder.RecordAsync(parent, Ct);

        var rows = await CostLedgerStore.ReadAllAsync(fixture.Ledger, Ct);
        Assert.Equal(5, rows.Count);
        Assert.Equal("2637", Assert.Single(rows, row => row.Execution == parentRequest.ExecutionId.Value).Issue);
        Assert.Equal("2637", Assert.Single(rows, row => row.Execution == graceId.Value).Issue);
        Assert.Equal("2637", Assert.Single(rows, row => row.Execution == checkpointId.Value).Issue);
        Assert.Null(Assert.Single(rows, row => row.Execution == legacyCheckpointId.Value).Issue);
        Assert.Null(Assert.Single(rows, row => row.Execution == mismatchedCheckpointId.Value).Issue);

        using var owned = await QueryAsync("2637");
        Assert.Equal(3, owned.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal(124, owned.RootElement.GetProperty("total").GetProperty("tokensIn").GetInt64());
        Assert.Equal(62, owned.RootElement.GetProperty("total").GetProperty("tokensOut").GetInt64());

        await TerminalSettleRecorder.RecordAsync(parent, Ct);
        Assert.Equal(5, (await CostLedgerStore.ReadAllAsync(fixture.Ledger, Ct)).Count);

        var backfillDirectory = Path.Combine(fixture.Root, "supplementary-backfill-ledger");
        using var output = new StringWriter();
        for (var run = 0; run < 2; run++)
            Assert.Equal(0, await LedgerBackfillCommand.ExecuteAsync(new LedgerBackfillOptions(), output,
                new AccountingForge(), backfillDirectory, repositoryProbe: null, cancellationToken: Ct));

        var backfilled = await CostLedgerStore.ReadAllAsync(
            Path.Combine(backfillDirectory, Identity.FileSlug + ".jsonl"), Ct);
        Assert.Equal(5, backfilled.Count);
        Assert.Equal("2637", Assert.Single(backfilled, row => row.Execution == parentRequest.ExecutionId.Value).Issue);
        Assert.Equal("2637", Assert.Single(backfilled, row => row.Execution == graceId.Value).Issue);
        Assert.Equal("2637", Assert.Single(backfilled, row => row.Execution == checkpointId.Value).Issue);
        Assert.Null(Assert.Single(backfilled, row => row.Execution == legacyCheckpointId.Value).Issue);
        Assert.Null(Assert.Single(backfilled, row => row.Execution == mismatchedCheckpointId.Value).Issue);
        Assert.Equal(124, backfilled.Where(row => row.Issue == "2637").Sum(row => row.TokensIn));
        Assert.Equal(62, backfilled.Where(row => row.Issue == "2637").Sum(row => row.TokensOut));
    }

    private static async Task AppendSupplementaryAccountingEventsAsync(
        string room,
        ExecutionRequest parentRequest,
        OwnedTaskExecutionIdentity owner,
        ExecutionId graceParentId,
        ExecutionId graceId,
        ExecutionId checkpointId,
        ExecutionId legacyCheckpointId,
        ExecutionId mismatchedCheckpointId)
    {
        var graceParentRequest = parentRequest with
        {
            ExecutionId = graceParentId,
            OwnedTaskIdentity = owner with { ExecutionId = graceParentId.Value },
        };
        var graceRequest = graceParentRequest with
        {
            ExecutionId = graceId,
            Limits = GraceTurn.CreateLimitEvidence(monitorInputsKnown: true),
            OwnedTaskIdentity = owner with { ExecutionId = graceId.Value },
        };
        var checkpointRequest = parentRequest with
        {
            ExecutionId = checkpointId,
            Timeout = ArtifactCheckpoint.WallClockTimeout,
            Limits = ArtifactCheckpoint.CreateLimitEvidence(monitorInputsKnown: true),
            OwnedTaskIdentity = owner with { ExecutionId = checkpointId.Value },
        };
        var mismatchedRequest = parentRequest with
        {
            ExecutionId = mismatchedCheckpointId,
            Timeout = ArtifactCheckpoint.WallClockTimeout,
            Limits = ArtifactCheckpoint.CreateLimitEvidence(monitorInputsKnown: true),
            OwnedTaskIdentity = owner with { ExecutionId = "not-the-child" },
        };
        var baseline = new GraceCheckpointEvidence(
            "head", "refs/heads/main", "origin", "refs/heads/main", "tip", "endpoint", "config", "workspace");
        await using var writer = new FlowEventLogWriter(Path.Combine(room, BatonPaths.FlowLogFileName));
        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(graceParentRequest), Ct);
        var parentArrest = new FlowEvent.ExecutionArrested(graceParentId, Reason: ArrestReason.TokenBudget);
        await writer.AppendAsync(parentArrest, Ct);
        await writer.AppendAsync(new FlowEvent.GraceTurnClaimed(
            graceParentId, graceId, graceRequest, baseline,
            new GraceParentRecoveryEvidence(true, -1, CoreExitReason.CancelRequested, false, false, parentArrest)), Ct);
        await writer.AppendAsync(new FlowEvent.GraceTurnCompleted(
            graceId, CoreExitReason.Natural, new WorkerUsage(TokensIn: 11, TokensOut: 5)), Ct);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(graceId, 1), Ct);
        await writer.AppendAsync(new CoreEvent.ExecutionExited(graceId, 0, CoreExitReason.Natural), Ct);
        await writer.AppendAsync(new FlowEvent.ArtifactCheckpointAttempted(
            checkpointId, parentRequest.ExecutionId, ["report.md"], checkpointRequest), Ct);
        await writer.AppendAsync(new FlowEvent.ArtifactCheckpointCompleted(
            checkpointId, CoreExitReason.Natural, new WorkerUsage(TokensIn: 13, TokensOut: 7)), Ct);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(checkpointId, 2), Ct);
        await writer.AppendAsync(new CoreEvent.ExecutionExited(checkpointId, 0, CoreExitReason.Natural), Ct);
        await writer.AppendAsync(new FlowEvent.ArtifactCheckpointAttempted(
            legacyCheckpointId, parentRequest.ExecutionId, ["legacy.md"]), Ct);
        await writer.AppendAsync(new FlowEvent.ArtifactCheckpointCompleted(
            legacyCheckpointId, CoreExitReason.Natural, new WorkerUsage(TokensIn: 17, TokensOut: 9)), Ct);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(legacyCheckpointId, 3), Ct);
        await writer.AppendAsync(new CoreEvent.ExecutionExited(legacyCheckpointId, 0, CoreExitReason.Natural), Ct);
        await writer.AppendAsync(new FlowEvent.ArtifactCheckpointAttempted(
            mismatchedCheckpointId, parentRequest.ExecutionId, ["mismatched.md"], mismatchedRequest), Ct);
        await writer.AppendAsync(new FlowEvent.ArtifactCheckpointCompleted(
            mismatchedCheckpointId, CoreExitReason.Natural, new WorkerUsage(TokensIn: 19, TokensOut: 11)), Ct);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(mismatchedCheckpointId, 4), Ct);
        await writer.AppendAsync(new CoreEvent.ExecutionExited(mismatchedCheckpointId, 0, CoreExitReason.Natural), Ct);
    }

    [Fact]
    public async Task Resume_continue_and_redispatch_inherit_exact_accepted_owner_when_binding_omits_it()
    {
        using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Activate();
        var (parent, _) = await fixture.LaunchAsync(WorkStage.Implement);
        var room = parent.RoomDirectoryPath!;
        var original = Assert.Single(await AcceptedAsync(room));
        await ClearDeclarationAsync(room);

        var resumed = await ResumeCommand.ExecuteAsync(
            new ResumeOptions(room, "implement", "Continue the bounded change.", null, BatonPaths.RoomBindingsFile(room)),
            fixture.Adapters, Ct);
        await TerminalSettleRecorder.RecordAsync(resumed, Ct);
        var resumeRequest = (await AcceptedAsync(room))[^1];
        Assert.Equal(original.ExecutionId, resumeRequest.LinkedFromExecutionId);
        Assert.Equal(original.OwnedTaskIdentity! with { ExecutionId = resumeRequest.ExecutionId.Value }, resumeRequest.OwnedTaskIdentity);

        var childRoom = Path.Combine(fixture.Root, "continued");
        var child = await DispatchCommand.ExecuteAsync(new DispatchOptions(
            "implement", fixture.Spec, childRoom, Adapter: "claude", Model: "sonnet", WorkspaceDirectory: fixture.Workspace,
            ContinueFromRoomDirectoryPath: room, NoDefaultSkills: true),
            fixture.Adapters, Ct, evaluateRunway: RunwayTestGate.Admit);
        await TerminalSettleRecorder.RecordAsync(child, Ct);
        var childRequest = Assert.Single(await AcceptedAsync(childRoom));
        Assert.Equal(resumeRequest.OwnedTaskIdentity! with
        {
            RoomDirectory = childRoom,
            ExecutionId = childRequest.ExecutionId.Value,
        }, childRequest.OwnedTaskIdentity);

        var redispatchRoom = Path.Combine(fixture.Root, "redispatched");
        var redispatched = await RedispatchCommand.ExecuteAsync(
            new RedispatchOptions(childRoom, redispatchRoom), fixture.Adapters, Ct);
        await TerminalSettleRecorder.RecordAsync(redispatched, Ct);
        var redispatchRequest = Assert.Single(await AcceptedAsync(redispatchRoom));
        Assert.Equal(childRequest.OwnedTaskIdentity! with
        {
            RoomDirectory = redispatchRoom,
            ExecutionId = redispatchRequest.ExecutionId.Value,
        }, redispatchRequest.OwnedTaskIdentity);
        Assert.Equal(4, (await CostLedgerStore.ReadAllAsync(fixture.Ledger, Ct)).Count);
        Assert.All(await CostLedgerStore.ReadAllAsync(fixture.Ledger, Ct), r => Assert.Equal("2637", r.Issue));
    }

    [Theory]
    [InlineData("task")]
    [InlineData("attempt")]
    [InlineData("repository")]
    [InlineData("issue")]
    [InlineData("room")]
    [InlineData("execution")]
    public async Task Conflicting_parent_declarations_cannot_replace_resume_or_redispatch_ownership(string mutation)
    {
        using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Activate();
        var (parent, _) = await fixture.LaunchAsync(WorkStage.Implement);
        var room = parent.RoomDirectoryPath!;
        var path = BatonPaths.RoomBindingsFile(room);
        var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(path, Ct);
        var owner = bindings["implement"].OwnedTaskIdentity!;
        var forged = mutation switch
        {
            "task" => owner with { TaskId = "invented-task" },
            "attempt" => owner with { AttemptId = "invented-attempt" },
            "repository" => owner with
            {
                Repository = "github.com/example/other",
                TaskId = OwnedTaskExecutionIdentity.TaskIdFor("github.com/example/other", 2637)
            },
            "issue" => owner with { Issue = 9999, TaskId = OwnedTaskExecutionIdentity.TaskIdFor(Repository, 9999) },
            "room" => owner with { RoomDirectory = Path.Combine(fixture.Root, "other-room") },
            _ => owner with { ExecutionId = "invented-execution" },
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["implement"] = bindings["implement"] with { OwnedTaskIdentity = forged },
        }, path, Ct);
        await Assert.ThrowsAsync<InvalidOwnedTaskIdentityException>(() => ResumeCommand.ExecuteAsync(
            new ResumeOptions(room, "implement", "Try again.", null, path), fixture.Adapters, Ct));
        var child = Path.Combine(fixture.Root, "refused-redispatch");
        await Assert.ThrowsAsync<InvalidOwnedTaskIdentityException>(() => RedispatchCommand.ExecuteAsync(
            new RedispatchOptions(room, child), fixture.Adapters, Ct));
        Assert.False(Directory.Exists(child));
        Assert.Single(await AcceptedAsync(room));
        Assert.Equal("2637", Assert.Single(await CostLedgerStore.ReadAllAsync(fixture.Ledger, Ct)).Issue);
    }

    [Theory]
    [InlineData("run", true)]
    [InlineData("run", false)]
    [InlineData("core", true)]
    [InlineData("core", false)]
    public async Task Fresh_unrelated_acceptance_cannot_copy_an_existing_owners_raw_predecessor(string entryPoint, bool declareOwner)
    {
        using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Activate();
        var (parent, _) = await fixture.LaunchAsync(WorkStage.Implement);
        var accepted = Assert.Single(await AcceptedAsync(parent.RoomDirectoryPath!));
        var room = Path.Combine(fixture.Root, "unrelated");
        var workflowPath = Path.Combine(fixture.Root, "unrelated-workflow.json");
        var definition = new WorkflowDefinition(new WorkflowTemplateId("unrelated"), 1,
            [new WorkflowStepDefinition(new StepId("worker"), "worker", [], ["result"], [], new RetryPolicy(1))]);
        await WorkflowDefinitionWriter.SaveToFileAsync(definition, workflowPath, Ct);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["worker"] = new WorkerBindingConfigEntry("claude",
                new WorkerContract("worker", [], [new ProducedOutput("result")], []), "Unrelated work.",
                TimeSpan.FromSeconds(30), Model: "sonnet", WorkingDirectory: fixture.Workspace,
                VerifiesWorkspace: false,
                OwnedTaskIdentity: declareOwner
                    ? accepted.OwnedTaskIdentity! with { RoomDirectory = room, ExecutionId = null }
                    : null,
                OwnedTaskPredecessor: new OwnedTaskExecutionPredecessor(parent.RoomDirectoryPath!, accepted.ExecutionId.Value)),
        };
        var path = Path.Combine(fixture.Root, "unrelated-bindings.json");
        await WorkerBindingConfigWriter.SaveToFileAsync(config, path, Ct);
        if (entryPoint == "run")
        {
            await Assert.ThrowsAsync<CliArgumentException>(() => RunCommand.ExecuteAsync(
                new RunOptions(workflowPath, path, room), fixture.Adapters, cancellationToken: Ct));
            Assert.False(Directory.Exists(room));
        }
        else
        {
            var (snapshot, _) = Scenarios.Build(ScenarioWorker.QuickSuccess);
            var processBindings = WorkerBindingResolver.Resolve(await WorkerBindingConfigParser.LoadFromFileAsync(path, Ct),
                fixture.Adapters, roomDirectory: room);
            await using var writer = new FlowEventLogWriter(Path.Combine(room, BatonPaths.FlowLogFileName));
            var reader = new FlowEventLogReader(Path.Combine(room, BatonPaths.FlowLogFileName));
            await Assert.ThrowsAsync<InvalidOwnedTaskIdentityException>(() => MutationInterface.StartWorkflowAsync(
                Scenarios.WorkflowId, room, snapshot, processBindings, Path.Combine(room, "artifacts"),
                reader, writer, new CoreDispatcher(writer, writer), cancellationToken: Ct));
            Assert.Empty((await reader.ReadAllAsync(Ct)).OfType<FlowEvent.ExecutionRequestAccepted>());
        }
    }

    [Fact]
    public async Task Exact_unknown_predecessor_does_not_borrow_another_workers_known_owner()
    {
        using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Activate();
        var (parent, _) = await fixture.LaunchAsync(WorkStage.Implement);
        var parentRequest = Assert.Single(await AcceptedAsync(parent.RoomDirectoryPath!));
        var room = Path.Combine(fixture.Root, "mixed-predecessors");
        var workflowPath = Path.Combine(fixture.Root, "mixed-workflow.json");
        var source = new StepId("unknown");
        var definition = new WorkflowDefinition(new WorkflowTemplateId("mixed-ownership"), 1,
        [
            new WorkflowStepDefinition(source, "unknown", [], ["unknown.txt"], [], new RetryPolicy(1)),
            new WorkflowStepDefinition(new StepId("owned"), "owned", [], ["owned.txt"], [source], new RetryPolicy(1)),
        ]);
        await WorkflowDefinitionWriter.SaveToFileAsync(definition, workflowPath, Ct);
        var bindings = new Dictionary<string, WorkerBindingConfigEntry>();
        foreach (var worker in new[] { "unknown", "owned" })
            bindings[worker] = new WorkerBindingConfigEntry("claude",
                new WorkerContract(worker, [], [new ProducedOutput(worker + ".txt")], []),
                "Fixture work.", TimeSpan.FromSeconds(30), Model: "sonnet",
                WorkingDirectory: fixture.Workspace, SessionId: "owned-session", VerifiesWorkspace: false,
                OwnedTaskPredecessor: null);
        var path = BatonPaths.RoomBindingsFile(room);
        await WorkerBindingConfigWriter.SaveToFileAsync(bindings, path, Ct);
        var initial = await RunCommand.ExecuteAsync(new RunOptions(workflowPath, path, room,
            ProjectRootDirectory: fixture.Workspace, Register: true), fixture.Adapters, cancellationToken: Ct);
        Assert.Equal(WorkflowStatus.Terminal, initial.State.Status);
        var before = await AcceptedAsync(room);
        var unknown = Assert.Single(before, r => r.Worker == "unknown");
        Assert.Null(unknown.OwnedTaskIdentity);
        // Historical mixed evidence is an input to this resolver-only control, not admission authority.
        // Both executions above were genuinely accepted unknown; no raw cross-room reference launched work.
        var owned = Assert.Single(before, r => r.Worker == "owned");
        var historical = before.Select(r => new FlowEvent.ExecutionRequestAccepted(r.Worker == "owned"
            ? r with
            {
                OwnedTaskIdentity = parentRequest.OwnedTaskIdentity! with
                { RoomDirectory = room, ExecutionId = owned.ExecutionId.Value }
            }
            : r)).Cast<FlowEvent>().ToList();
        Assert.Null(await OwnedTaskOwnership.ResolveAsync(room, null, historical,
            new OwnedTaskExecutionPredecessor(room, unknown.ExecutionId.Value), Ct));
    }

    [Fact]
    public async Task Backfill_of_actual_dispatched_execution_preserves_identity_usage_and_deduplication()
    {
        using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Activate();
        var (parent, _) = await fixture.LaunchAsync(WorkStage.Review);
        var settled = Assert.Single(await CostLedgerStore.ReadAllAsync(fixture.Ledger, Ct));
        var directory = Path.Combine(fixture.Root, "backfill-ledger");
        using var output = new StringWriter();
        for (var run = 0; run < 2; run++)
            Assert.Equal(0, await LedgerBackfillCommand.ExecuteAsync(new LedgerBackfillOptions(), output,
                new AccountingForge(), directory, repositoryProbe: null, cancellationToken: Ct));
        var recovered = Assert.Single(await CostLedgerStore.ReadAllAsync(
            Path.Combine(directory, Identity.FileSlug + ".jsonl"), Ct));
        Assert.Equal(parent.RoomDirectoryPath, recovered.Room);
        Assert.Equal(settled.Execution, recovered.Execution);
        Assert.Equal("2637", recovered.Issue);
        Assert.Equal(settled.TokensIn, recovered.TokensIn);
        Assert.Equal(settled.TokensOut, recovered.TokensOut);
    }

    [Fact]
    public async Task Actual_resolution_copies_settled_identity_without_counting_usage_again()
    {
        using var fixture = await Fixture.CreateAsync(satisfyOutputs: false);
        using var scope = fixture.Activate();
        var (parent, _) = await fixture.LaunchAsync(WorkStage.Implement);
        var room = parent.RoomDirectoryPath!;
        var before = Assert.Single(await CostLedgerStore.ReadAllAsync(fixture.Ledger, Ct));
        Assert.Equal("2637", before.Issue);
        Assert.Equal(100, before.TokensIn);
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { typeof(DispatchCommand).Assembly.Location, "resolve", room,
            "--execution", before.Execution!, "--reject", "--reason", "Fixture output was absent." })
            start.ArgumentList.Add(argument);
        start.Environment[BatonPaths.HomeEnvironmentVariable] = Path.Combine(fixture.Root, "home");
        using var process = Process.Start(start)!;
        var (_, error) = await BoundedProcessWait.RunToExitAsync(process, TimeSpan.FromSeconds(30), Ct);
        Assert.True(process.ExitCode is 0 or 1, error);
        var after = await CostLedgerStore.ReadAllAsync(fixture.Ledger, Ct);
        Assert.Equal(2, after.Count);
        var correction = Assert.Single(after, r => r.Resolution is not null);
        Assert.Equal(before.Issue, correction.Issue);
        Assert.Null(correction.TokensIn);
        Assert.Equal(before.TokensIn, LedgerRollup.Build(after, new LedgerQuery()).Total.TokensIn);
    }


    [Fact]
    public async Task Core_acceptance_cannot_mint_ownership_from_a_self_consistent_unadmitted_binding()
    {
        using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Activate();
        var room = Path.Combine(fixture.Root, "unadmitted-core");
        var (snapshot, bindings) = Scenarios.Build(ScenarioWorker.QuickSuccess);
        var forged = new OwnedTaskExecutionIdentity(OwnedTaskExecutionIdentity.TaskIdFor(Repository, 9999),
            Repository, 9999, "invented-attempt", room);
        var declared = bindings.ToDictionary(p => p.Key, p => (WorkerBinding)
            (Assert.IsType<WorkerBinding.Process>(p.Value) with { OwnedTaskIdentity = forged }));
        await using var writer = new FlowEventLogWriter(Path.Combine(room, BatonPaths.FlowLogFileName));
        var reader = new FlowEventLogReader(Path.Combine(room, BatonPaths.FlowLogFileName));
        await Assert.ThrowsAsync<InvalidOwnedTaskIdentityException>(() => MutationInterface.StartWorkflowAsync(
            Scenarios.WorkflowId, room, snapshot, declared, Path.Combine(room, "artifacts"),
            reader, writer, new CoreDispatcher(writer, writer), cancellationToken: Ct));
        Assert.Empty((await reader.ReadAllAsync(Ct)).OfType<FlowEvent.ExecutionRequestAccepted>());
    }

    [Fact]
    public async Task Supply_cannot_admit_forged_downstream_ownership()
    {
        using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Activate();
        var room = Path.Combine(fixture.Root, "supply");
        var (snapshot, _) = Scenarios.Build(ScenarioWorker.QuickSuccess);
        await Baton.Templates.SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, BatonPaths.SnapshotFileName), Ct);
        var forged = new OwnedTaskExecutionIdentity(OwnedTaskExecutionIdentity.TaskIdFor(Repository, 9999),
            Repository, 9999, "invented-attempt", room);
        var path = BatonPaths.RoomBindingsFile(room);
        await WorkerBindingConfigWriter.SaveToFileAsync(new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["worker"] = new WorkerBindingConfigEntry("claude",
                new WorkerContract("worker", [], [new ProducedOutput("result")], []), "Fixture work.",
                TimeSpan.FromSeconds(30), Model: "sonnet", WorkingDirectory: fixture.Workspace,
                VerifiesWorkspace: false, OwnedTaskIdentity: forged),
        }, path, Ct);
        await Assert.ThrowsAsync<InvalidOwnedTaskIdentityException>(() => SupplyCommand.ExecuteAsync(
            new SupplyOptions(room, "human", "supplement.txt", fixture.Spec, path), fixture.Adapters, Ct));
        Assert.All(await AcceptedAsync(room), request => Assert.Null(request.OwnedTaskIdentity));
    }

    [Theory]
    [InlineData("decide", false)]
    [InlineData("decide", true)]
    [InlineData("resume", false)]
    [InlineData("resume", true)]
    public async Task Downstream_acceptance_inherits_known_owner_or_refuses_a_conflicting_declaration(string verb, bool forge)
    {
        using var fixture = await Fixture.CreateAsync();
        using var scope = fixture.Activate();
        var workflowPath = Path.Combine(fixture.Root, "downstream-workflow.json");
        var source = new StepId("source");
        var definition = new WorkflowDefinition(new WorkflowTemplateId("ownership-downstream"), 1,
        [
            new WorkflowStepDefinition(source, "source", [], ["source.txt"], [], new RetryPolicy(1),
                verb == "decide" ? new PausePoint([]) : null),
            new WorkflowStepDefinition(new StepId("downstream"), "downstream", [], ["downstream.txt"], [source], new RetryPolicy(1)),
        ]);
        await WorkflowDefinitionWriter.SaveToFileAsync(definition, workflowPath, Ct);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["source"] = new WorkerBindingConfigEntry("source-fixture",
                new WorkerContract("source", [], [new ProducedOutput("source.txt")], []),
                "Fixture source.", TimeSpan.FromSeconds(30), WorkingDirectory: fixture.Workspace,
                SessionId: "owned-session", VerifiesWorkspace: false),
            ["downstream"] = new WorkerBindingConfigEntry("claude",
                new WorkerContract("downstream", [], [new ProducedOutput("downstream.txt")], []),
                "Fixture downstream.", TimeSpan.FromSeconds(30), Model: "sonnet",
                WorkingDirectory: fixture.Workspace, VerifiesWorkspace: false),
        };
        var adapters = new Dictionary<string, IWorkerAdapter>(fixture.Adapters)
        {
            ["source-fixture"] = new ContractOutputWorkerAdapter(verb == "decide", failureExitCode: 1),
        };
        // The first acceptance uses a real frozen queue admission, not a raw cross-room reference.
        var (initial, launch) = await fixture.LaunchAsync(WorkStage.Implement, async (options, token) =>
        {
            config["source"] = config["source"] with { OwnedTaskIdentity = options.OwnedTaskIdentity };
            var bindingsPath = BatonPaths.RoomBindingsFile(options.RoomDirectoryPath);
            await WorkerBindingConfigWriter.SaveToFileAsync(config, bindingsPath, token);
            return await RunCommand.ExecuteAsync(new RunOptions(workflowPath, bindingsPath, options.RoomDirectoryPath,
                ProjectRootDirectory: fixture.Workspace, Register: true), adapters, cancellationToken: token);
        });
        var room = launch.RoomDirectory;
        var path = BatonPaths.RoomBindingsFile(room);
        Assert.Equal(verb == "decide" ? WorkflowStatus.Paused : WorkflowStatus.Terminal, initial.State.Status);
        var first = Assert.Single(await AcceptedAsync(room));
        Assert.Equal(2637, first.OwnedTaskIdentity?.Issue);
        if (forge)
        {
            config["downstream"] = config["downstream"] with
            {
                OwnedTaskIdentity = new OwnedTaskExecutionIdentity(OwnedTaskExecutionIdentity.TaskIdFor(Repository, 9999),
                    Repository, 9999, first.OwnedTaskIdentity!.AttemptId, room),
            };
            await WorkerBindingConfigWriter.SaveToFileAsync(config, path, Ct);
        }
        adapters["source-fixture"] = new ContractOutputWorkerAdapter(true);
        Task<CommandResult> ContinueAsync() => verb == "decide"
            ? DecideCommand.ExecuteAsync(new DecideOptions(room, first.ExecutionId.Value, DecisionType.Resume,
                null, null, path), adapters, cancellationToken: Ct)
            : ResumeCommand.ExecuteAsync(new ResumeOptions(room, "source", "Finish source.", null, path), adapters, Ct);
        if (forge)
        {
            await Assert.ThrowsAsync<InvalidOwnedTaskIdentityException>(ContinueAsync);
            Assert.DoesNotContain(await AcceptedAsync(room), r => r.Worker == "downstream");
        }
        else
        {
            var result = await ContinueAsync();
            await TerminalSettleRecorder.RecordAsync(result, Ct);
            var downstream = Assert.Single(await AcceptedAsync(room), r => r.Worker == "downstream");
            Assert.Equal(first.OwnedTaskIdentity! with { ExecutionId = downstream.ExecutionId.Value }, downstream.OwnedTaskIdentity);
            Assert.All((await CostLedgerStore.ReadAllAsync(fixture.Ledger, Ct))
                .Where(r => r.Room == BatonPaths.RecordKey(room)), r => Assert.Equal("2637", r.Issue));
        }
    }
    private static async Task<JsonDocument> QueryAsync(string? issue)
    {
        var args = new List<string> { "--repo-identity", Repository, "--format", "json", "--drill" };
        if (issue is not null) args.AddRange(["--issue", issue]);
        using var output = new StringWriter();
        Assert.Equal(0, await LedgerViewCommand.ExecuteAsync(LedgerViewOptionsParser.Parse(args), output, cancellationToken: Ct));
        return JsonDocument.Parse(output.ToString());
    }

    private static async Task<IReadOnlyList<ExecutionRequest>> AcceptedAsync(string room) =>
        (await new FlowEventLogReader(Path.Combine(room, BatonPaths.FlowLogFileName)).ReadAllAsync(Ct))
            .OfType<FlowEvent.ExecutionRequestAccepted>().Select(e => e.Request).ToList();

    private static async Task ClearDeclarationAsync(string room)
    {
        var path = BatonPaths.RoomBindingsFile(room);
        var entries = await WorkerBindingConfigParser.LoadFromFileAsync(path, Ct);
        await WorkerBindingConfigWriter.SaveToFileAsync(entries.ToDictionary(p => p.Key,
            p => p.Value with { OwnedTaskIdentity = null }), path, Ct);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "owned-accounting-" + Guid.NewGuid().ToString("N"));
        public string Workspace => Path.Combine(Root, "review-workspace");
        public string Spec => Path.Combine(Root, "brief.md");
        public string Ledger => BatonPaths.CostLedgerFile(Identity.FileSlug);
        public IReadOnlyDictionary<string, IWorkerAdapter> Adapters { get; private set; } = null!;
        private BatonEnvironmentSnapshot _environment = null!;
        private readonly string? _oldPath = Environment.GetEnvironmentVariable("PATH");

        public IDisposable Activate() => BatonEnvironmentSnapshot.BeginScope(_environment);

        public static async Task<Fixture> CreateAsync(bool satisfyOutputs = true)
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.Workspace);
            var bin = Directory.CreateDirectory(Path.Combine(fixture.Root, "fixture-bin")).FullName;
            // Existing deterministic git/gh apphost: canonical remote, non-issue branch and PR 2304.
            // No live forge/vendor call can escape this fixture.
            var hostDirectory = Path.GetDirectoryName(typeof(Scenarios).Assembly.Location)!;
            foreach (var source in Directory.EnumerateFiles(hostDirectory))
            {
                var name = Path.GetFileName(source);
                if (name.StartsWith("Baton.CrashTestHost", StringComparison.Ordinal) || name == "Baton.dll")
                    File.Copy(source, Path.Combine(bin, name));
            }
            foreach (var name in new[] { "git", "gh" })
                File.Copy(Path.Combine(bin, "Baton.CrashTestHost.exe"), Path.Combine(bin, name + ".exe"));
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + fixture._oldPath);

            // Bound the accounting fixture to acceptance/settlement, without branch-delivery or workspace verify.
            // The production role materializer, schemas and identity transport still run.
            var catalog = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"), Ct))!;
            foreach (var role in catalog.AsArray().OfType<JsonObject>())
            {
                role["write_files"] = false;
                role["run_shell_commands"] = false;
                role["network_access"] = false;
                role["exact_file_restore"] = false;
                role["verifies_workspace"] = false;
                role["delivers_branch"] = false;
                role["verify_pixi_task"] = null;
            }
            var roles = Path.Combine(fixture.Root, "roles.json");
            await File.WriteAllTextAsync(roles, catalog.ToJsonString(), Ct);
            fixture._environment = BatonEnvironmentSnapshot.Blank with
            {
                HomeOverride = Path.Combine(fixture.Root, "home"),
                WorkerRolesPathOverride = roles,
                WorkerTiersPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"),
                WorkflowTemplatesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkflowTemplates.json"),
            };
            await File.WriteAllTextAsync(fixture.Spec, "Exercise task accounting.", Ct);
            var usage = Path.Combine(fixture.Root, "usage.jsonl");
            await File.WriteAllTextAsync(usage,
                """{"type":"system","subtype":"init","session_id":"owned-session"}""" + "\n"
                + """{"type":"assistant","message":{"id":"msg_1","usage":{"input_tokens":100,"output_tokens":50,"cache_creation_input_tokens":10,"cache_read_input_tokens":5}}}""" + "\n"
                + """{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"done","session_id":"owned-session","usage":{"input_tokens":100,"output_tokens":50,"cache_creation_input_tokens":10,"cache_read_input_tokens":5}}""" + "\n", Ct);
            var verdict = Path.Combine(fixture.Root, "verdict.json");
            await File.WriteAllTextAsync(verdict, JsonSerializer.Serialize(new ReviewVerdict("#2304", [], "Recorded fixture review.")), Ct);
            fixture.Adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["claude"] = new AccountingAdapter(new ContractOutputWorkerAdapter(satisfyOutputs,
                    outputFixtures: new Dictionary<string, string> { ["verdict.json"] = verdict }), usage),
            };
            return fixture;
        }

        public async Task<(CommandResult Result, QueueLaunchRequest Request)> LaunchAsync(WorkStage stage,
            Func<DispatchOptions, CancellationToken, Task<CommandResult>>? execute = null)
        {
            var role = WorkStages.RoleFor(stage);
            var owner = new OwnedTaskSubmission(OwnedTaskExecutionIdentity.TaskIdFor(Repository, 2637),
                Repository, 2637, "fixture-input", "fixture-conductor", Now);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, _ => new QueueSnapshot(
            [
                new QueueItem
                {
                    Tag = "accounting-" + WorkStages.Token(stage), Role = role, ScopeClass = "engine",
                    Stage = stage, Issue = 2637, Repository = Repository, Workspace = Workspace, SpecFile = Spec,
                    Branch = Branch, PullRequest = stage == WorkStage.Implement ? null : 2304,
                    Adapter = "claude", Model = "sonnet", Effort = "low", Reason = "deterministic fixture",
                    OwnedTask = owner, IssuePreparation = new QueueIssuePreparation(TaskPreparationState.Prepared, Now),
                },
            ]), Ct);
            CommandResult? result = null;
            QueueLaunchRequest? launched = null;
            var scheduler = new QueueSchedulerService(async (request, token) =>
            {
                launched = request;
                var options = QueueLauncher.BuildOptions(request);
                var parsed = DispatchOptionsParser.Parse(QueueLauncher.BuildArguments(options).Skip(1).ToList());
                Assert.Equal(options.OwnedTaskIdentity, parsed.OwnedTaskIdentity);
                result = execute is null
                    ? await DispatchCommand.ExecuteAsync(parsed with { NoDefaultSkills = true },
                        Adapters, token, evaluateRunway: RunwayTestGate.Admit)
                    : await execute(parsed, token);
                await TerminalSettleRecorder.RecordAsync(result, token);
                return new QueueLaunchOutcome(request.RoomDirectory);
            }, _ => Task.FromResult(0d), () => 16d, () => Now,
                advancer: new WorkItemAdvancer(new AccountingForge(),
                    (_, _) => Task.FromResult<string?>(Head), (_, _) => Task.FromResult<RepositoryIdentity?>(Identity)),
                workspaceHead: (_, _) => Task.FromResult<string?>(Head), workspaceLocks: _ => []);
            await scheduler.TickOnceAsync(Ct);
            Assert.True(launched is not null, (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single().Error);
            Assert.True(result is not null, (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single().Error);
            if (execute is null) Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            // Settlement/resumption must no longer need the mutable admitted queue row.
            await QueueStore.MutateAsync(BatonPaths.QueueFile, _ => QueueSnapshot.Empty, Ct);
            return (result, launched);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _oldPath);
            DirectoryCleanup.DeleteRecursively(Root);
        }
    }

    private sealed class AccountingAdapter(ContractOutputWorkerAdapter inner, string stdoutFixture) : IWorkerAdapter
    {
        public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
        {
            var target = inner.Resolve(invocation, contract);
            // Emit telemetry after copy's status line, so the terminal usage envelope is truly final.
            var script = target.Args[1] == "exit 0" ? "type " + stdoutFixture : target.Args[1] + " & type " + stdoutFixture;
            return target with { Args = ["/c", script] };
        }
        public bool TryParseSessionId(string line, out string? sessionId) =>
            new ClaudeWorkerAdapter().TryParseSessionId(line, out sessionId);
    }

    private sealed class AccountingForge : IGhCliRunner
    {
        public Task<GhCliResult> RunAsync(string workspace, IReadOnlyList<string> args, CancellationToken token)
        {
            if (args.Contains("merged"))
                return Task.FromResult(new GhCliResult(true, 0, "[]", ""));
            if (args[0] == "api")
                return Task.FromResult(Daemon.RequiredCheckFixture.Read(args, Repository, Head,
                    new GhCliResult(true, 0, """[{"name":"ci","bucket":"pass"}]""", "")));
            Assert.Equal("pr", args[0]);
            var json = $$"""{"number":2304,"state":"OPEN","isDraft":true,"headRefOid":"{{Head}}","headRefName":"{{Branch}}","baseRefName":"main","isCrossRepository":false,"statusCheckRollup":[]}""";
            return Task.FromResult(new GhCliResult(true, 0, args[1] == "list" ? "[" + json + "]" : json, ""));
        }
    }
}
