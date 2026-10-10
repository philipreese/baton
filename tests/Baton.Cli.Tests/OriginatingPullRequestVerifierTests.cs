using Baton.Cli.Tests.TestSupport;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Baton.CrashTestHost;
using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests;

[Collection(SerializedEnvironmentCollection.Name)]
public sealed class OriginatingPullRequestVerifierTests
{
    private const string Head = "0123456789abcdef0123456789abcdef01234567";
    private const string PreservedHead = "fedcba9876543210fedcba9876543210fedcba98";
    private const string RecoveryAttemptId = "2178continuationattempt000000000000";
    private static readonly GhPullRequestCreateIdentity Identity = new("aer-works/baton", "2178-lane");

    [Theory]
    [InlineData("aer-works/baton#2304", "aer-works/baton", 2304)]
    [InlineData("AER-WORKS/BATON#1", "aer-works/baton", 1)]
    public void A_repository_qualified_reference_is_parsed_canonically(
        string raw, string expectedRepository, int expectedNumber)
    {
        var parsed = OriginatingPullRequestVerifier.ParseReference(raw);

        Assert.Equal(expectedRepository, parsed.Repository);
        Assert.Equal(expectedNumber, parsed.Number);
    }

    [Theory]
    [InlineData("2304")]
    [InlineData("aer-works/baton")]
    [InlineData("aer-works/baton#0")]
    [InlineData("aer-works/baton#nope")]
    [InlineData("https://github.com/aer-works/baton#2304")]
    public void An_ambiguous_or_malformed_reference_is_refused(string raw) =>
        Assert.Throws<CliArgumentException>(() => OriginatingPullRequestVerifier.ParseReference(raw));

    [Fact]
    public void A_queue_repository_identity_is_narrowed_to_the_GitHub_owner_repo_reference()
    {
        Assert.Equal(
            "aer-works/baton#2304",
            OriginatingPullRequestVerifier.CanonicalReference("github.com/AER-WORKS/BATON", 2304));
        Assert.Throws<CliArgumentException>(() =>
            OriginatingPullRequestVerifier.CanonicalReference("gitlab.com/aer-works/baton", 2304));
    }

    [Fact]
    public void An_open_PR_matching_the_verified_repository_branch_and_launch_HEAD_is_bound()
    {
        var ownership = OriginatingPullRequestVerifier.ValidateResponse(
            "aer-works/baton", 2304, Identity, Head, 0,
            $$"""{"state":"OPEN","headRefName":"2178-lane","headRefOid":"{{Head.ToUpperInvariant()}}","isCrossRepository":false}""");

        Assert.Equal(new OriginatingPullRequestOwnership("aer-works/baton", 2304, "2178-lane", Head), ownership);
    }

    [Fact]
    public void A_recovery_PR_may_remain_at_the_retained_head_when_workspace_HEAD_is_a_descendant()
    {
        var ownership = OriginatingPullRequestVerifier.ValidateResponse(
            "aer-works/baton", 2304, Identity, Head, 0,
            $$"""{"state":"OPEN","headRefName":"2178-lane","headRefOid":"{{PreservedHead}}","isCrossRepository":false}""",
            PreservedHead);

        Assert.Equal(new OriginatingPullRequestOwnership("aer-works/baton", 2304, "2178-lane", Head), ownership);
    }

    [Fact]
    public void Existing_lineage_PR_is_the_only_PR_the_follow_on_policy_can_read()
    {
        var rule = new OwnPullRequestOnlyRule();
        rule.Observe(new OriginatingPullRequestOwnership(
            "aer-works/baton", 2304, "2178-lane", Head).ToEvidence());

        Assert.Null(rule.Refuse("gh pr view 2304"));
        Assert.NotNull(rule.Refuse("gh pr view 2305"));
        Assert.NotNull(rule.Refuse("gh pr view 2304 --repo other/repo"));
    }

    [Fact]
    public void A_missing_originating_PR_is_refused_before_binding()
    {
        Assert.Throws<CliArgumentException>(() => OriginatingPullRequestVerifier.ValidateResponse(
            "aer-works/baton", 2304, Identity, Head, 1, "not found"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("string")]
    [InlineData("number")]
    [InlineData("object")]
    [InlineData("array")]
    [InlineData("true")]
    public void An_originating_PR_must_explicitly_report_a_same_repository_response(string crossRepository)
    {
        var response = crossRepository == "missing"
            ? $$"""{"state":"OPEN","headRefName":"2178-lane","headRefOid":"{{Head}}"}"""
            : $$"""{"state":"OPEN","headRefName":"2178-lane","headRefOid":"{{Head}}","isCrossRepository":{{CrossRepositoryValue(crossRepository)}}}""";

        var exception = Assert.Throws<CliArgumentException>(() => OriginatingPullRequestVerifier.ValidateResponse(
            "aer-works/baton", 2304, Identity, Head, 0, response));

        Assert.Contains("isCrossRepository", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_originating_PR_accepts_an_explicit_JSON_false_same_repository_response()
    {
        var ownership = OriginatingPullRequestVerifier.ValidateResponse(
            "aer-works/baton", 2304, Identity, Head, 0,
            $$"""{"state":"OPEN","headRefName":"2178-lane","headRefOid":"{{Head}}","isCrossRepository":false}""");

        Assert.Equal(new OriginatingPullRequestOwnership("aer-works/baton", 2304, "2178-lane", Head), ownership);
    }

    [Fact]
    public void A_stale_originating_PR_head_is_refused_before_binding()
    {
        Assert.Throws<CliArgumentException>(() => OriginatingPullRequestVerifier.ValidateResponse(
            "aer-works/baton", 2304, Identity, Head,
            0, $$"""{"state":"OPEN","headRefName":"2178-lane","headRefOid":"{{Head}}","isCrossRepository":false}""",
            PreservedHead));
    }

    [Fact]
    public void An_unrelated_PR_branch_is_refused_before_binding()
    {
        Assert.Throws<CliArgumentException>(() => OriginatingPullRequestVerifier.ValidateResponse(
            "aer-works/baton", 2304, Identity, Head,
            0, $$"""{"state":"OPEN","headRefName":"unrelated-lane","headRefOid":"{{Head}}","isCrossRepository":false}"""));
    }

    [Fact]
    public void A_malformed_retained_head_is_refused()
    {
        Assert.Throws<CliArgumentException>(() => OriginatingPullRequestVerifier.ValidateResponse(
            "aer-works/baton", 2304, Identity, Head, 0,
            $$"""{"state":"OPEN","headRefName":"2178-lane","headRefOid":"{{PreservedHead}}","isCrossRepository":false}""",
            "not-a-sha"));
    }

    [Fact]
    public void A_direct_caller_cannot_forge_recovery_identity_without_a_durable_queue_row()
    {
        var options = RecoveryOptions();

        Assert.Throws<CliArgumentException>(() => OriginatingPullRequestVerifier.ValidateRecoveryEvidence(
            options, Path.GetTempPath(), new QueueSnapshot([])));
    }

    [Fact]
    public void A_queued_recovery_attempt_is_refused()
    {
        var workspace = Path.GetTempPath();
        var item = new QueueItem
        {
            Tag = "2178-lane",
            Role = "implement",
            Workspace = workspace,
            SpecFile = "2178.md",
            State = QueueItemState.Queued,
            Stage = WorkStage.Continue,
            Repository = "github.com/aer-works/baton",
            PullRequest = 2304,
            Branch = "2178-lane",
            ExpectedOriginatingPullRequestHead = PreservedHead,
            AttemptId = new FleetAttemptId(RecoveryAttemptId),
            AttemptEnvelope = new QueueAttemptEnvelope(
                new FleetAttemptId(RecoveryAttemptId), null, "2178", null, 2304, WorkStage.Continue,
                "implement", null, null, null, [], null, null, "admitted", null, null, null,
                DateTimeOffset.UnixEpoch),
        };

        Assert.Throws<CliArgumentException>(() => OriginatingPullRequestVerifier.ValidateRecoveryEvidence(
            RecoveryOptions(), workspace, new QueueSnapshot([item])));
    }

    [Fact]
    public async Task A_launched_recovery_claims_once_while_its_assigned_room_is_still_absent()
    {
        using var home = new IsolatedBatonHome();
        var workspace = Directory.CreateDirectory(Path.Combine(home.Path, "workspace")).FullName;
        var room = Path.Combine(home.Path, "rooms", "queue-2178-lane-future");
        var attemptId = new FleetAttemptId(RecoveryAttemptId);
        const string proof = "one-shot-proof";
        var item = new QueueItem
        {
            Tag = "2178-lane",
            Role = "implement",
            Workspace = workspace,
            SpecFile = "2178.md",
            State = QueueItemState.Launched,
            Stage = WorkStage.Continue,
            Repository = "github.com/aer-works/baton",
            PullRequest = 2304,
            Branch = "2178-lane",
            RoomDirectory = room,
            ExpectedOriginatingPullRequestHead = PreservedHead,
            OriginatingPullRequestRecoveryProofDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proof))),
            AttemptId = attemptId,
            AttemptEnvelope = new QueueAttemptEnvelope(
                attemptId, null, "2178", null, 2304, WorkStage.Continue,
                "implement", null, null, null, [], null, null, "admitted", room,
                BatonPaths.RecordKey(room), null, DateTimeOffset.UnixEpoch),
        };
        await QueueStore.MutateAsync(
            BatonPaths.QueueFile, snapshot => snapshot with { Items = [item] },
            TestContext.Current.CancellationToken);
        var options = RecoveryOptions(room);

        Assert.False(Directory.Exists(room));
        var originalInput = Console.In;
        try
        {
            Console.SetIn(new StringReader(proof + Environment.NewLine));
            Assert.Equal(
                PreservedHead,
                await OriginatingPullRequestVerifier.ResolveRecoveryExpectedHeadAsync(
                    options, workspace, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<CliArgumentException>(() =>
                OriginatingPullRequestVerifier.ResolveRecoveryExpectedHeadAsync(
                    options, workspace, TestContext.Current.CancellationToken));
        }
        finally
        {
            Console.SetIn(originalInput);
        }

        var claimed = Assert.Single((await QueueStore.LoadAsync(
            BatonPaths.QueueFile, TestContext.Current.CancellationToken)).Items);
        Assert.Equal(attemptId, claimed.OriginatingPullRequestRecoveryClaim);
        Assert.Null(claimed.OriginatingPullRequestRecoveryProofDigest);
        Assert.False(Directory.Exists(room));
    }

    [Fact]
    public void A_future_path_compares_lexically_but_an_existing_link_ancestor_is_refused()
    {
        var root = Directory.CreateTempSubdirectory("baton-origin-room-").FullName;
        try
        {
            var rooms = Directory.CreateDirectory(Path.Combine(root, "rooms")).FullName;
            var future = Path.Combine(rooms, "queue-future");
            Assert.True(OriginatingPullRequestVerifier.SameWorkspace(
                future, Path.Combine(rooms, ".", "queue-future")));
            Assert.Equal(
                OperatingSystem.IsWindows(),
                OriginatingPullRequestVerifier.SameWorkspace(future, future.ToUpperInvariant()));

            var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
            var alias = Path.Combine(root, "rooms-alias");
            try
            {
                Directory.CreateSymbolicLink(alias, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            var aliasedFuture = Path.Combine(alias, "queue-future");
            Assert.False(OriginatingPullRequestVerifier.SameWorkspace(aliasedFuture, aliasedFuture));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Theory]
    [InlineData("CLOSED", "2178-lane", Head)]
    [InlineData("MERGED", "2178-lane", Head)]
    [InlineData("OPEN", "another-branch", Head)]
    [InlineData("OPEN", "2178-lane", "ffffffffffffffffffffffffffffffffffffffff")]
    public void A_closed_merged_or_workspace_mismatched_PR_is_refused(
        string state, string branch, string head)
    {
        var response = $$"""{"state":"{{state}}","headRefName":"{{branch}}","headRefOid":"{{head}}","isCrossRepository":false}""";

        Assert.Throws<CliArgumentException>(() => OriginatingPullRequestVerifier.ValidateResponse(
            "aer-works/baton", 2304, Identity, Head, 0, response));
    }

    [Theory]
    [InlineData(1, "{}")]
    [InlineData(0, "not json")]
    [InlineData(0, "{}")]
    public void An_unreadable_or_malformed_forge_response_is_refused(int exitCode, string response) =>
        Assert.Throws<CliArgumentException>(() => OriginatingPullRequestVerifier.ValidateResponse(
            "aer-works/baton", 2304, Identity, Head, exitCode, response));

    [Fact]
    public void A_queue_recorded_branch_must_match_before_the_forge_is_spawned()
    {
        OriginatingPullRequestVerifier.ValidateExpectedBranch(Identity, "2178-lane");
        Assert.Throws<CliArgumentException>(() =>
            OriginatingPullRequestVerifier.ValidateExpectedBranch(Identity, "worker-changed-branch"));
    }

    [Fact]
    public void Gh_resolution_skips_a_workspace_local_executable_and_requires_an_outside_absolute_one()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-origin-gh-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var outside = Path.Combine(root, "trusted-bin");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(workspace, "gh.exe"), "worker fake");
        File.WriteAllText(Path.Combine(outside, "gh.exe"), "conductor fake");
        try
        {
            var search = string.Join(Path.PathSeparator, workspace, outside);
            Assert.Equal(
                Path.Combine(outside, "gh.exe"),
                OriginatingPullRequestVerifier.ResolveExecutable(workspace, search, isWindows: true),
                ignoreCase: true);
            Assert.Throws<CliArgumentException>(() =>
                OriginatingPullRequestVerifier.ResolveExecutable(workspace, workspace, isWindows: true));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Verification_spawns_the_external_gh_and_never_the_workspace_fake()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-origin-gh-spawn-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var outside = Path.Combine(root, "trusted-bin");
        var marker = Path.Combine(root, "launched-gh.txt");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        CopyHermeticProbeHost(outside);
        var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var appHost = Path.Combine(outside, "Baton.CrashTestHost" + suffix);
        var externalGit = Path.Combine(outside, "git" + suffix);
        var externalGh = Path.Combine(outside, "gh" + suffix);
        File.Copy(appHost, externalGit);
        File.Copy(appHost, externalGh);
        MakeExecutable(externalGit);
        MakeExecutable(externalGh);

        // If VerifyAsync ever regresses to a bare "gh" launch, PATH selects this invalid worker
        // executable first: the spawn fails and the external marker never lands.
        var workspaceGh = Path.Combine(workspace, "gh" + suffix);
        File.WriteAllText(
            workspaceGh,
            OperatingSystem.IsWindows()
                ? "worker-controlled fake; must not launch"
                : "#!/bin/sh\nexit 99\n");
        MakeExecutable(workspaceGh);

        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldMarker = Environment.GetEnvironmentVariable("BATON_CRASH_TEST_GH_MARKER");
        try
        {
            Environment.SetEnvironmentVariable(
                "PATH", string.Join(Path.PathSeparator, workspace, outside, oldPath));
            Environment.SetEnvironmentVariable("BATON_CRASH_TEST_GH_MARKER", marker);

            var ownership = await OriginatingPullRequestVerifier.VerifyAsync(
                "aer-works/baton#2304", workspace, TestContext.Current.CancellationToken,
                "2190-verified-pr-ownership");

            Assert.Equal(new OriginatingPullRequestOwnership(
                "aer-works/baton", 2304, "2190-verified-pr-ownership", Head), ownership);
            Assert.Equal(
                Path.GetFullPath(externalGh),
                Path.GetFullPath(await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken)),
                ignoreCase: OperatingSystem.IsWindows());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("BATON_CRASH_TEST_GH_MARKER", oldMarker);
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("changed-bytes")]
    [InlineData("unrelated-file")]
    [InlineData("tracked-edit")]
    [InlineData("missing-journal")]
    [InlineData("torn-journal")]
    [InlineData("null-manifest")]
    [InlineData("escaped-manifest")]
    [InlineData("missing-lineage")]
    [InlineData("wrong-claim")]
    [InlineData("wrong-workspace")]
    [InlineData("wrong-head")]
    [InlineData("wrong-invoking-room")]
    [InlineData("wrong-invoking-pr")]
    [InlineData("wrong-invoking-branch")]
    [InlineData("other-claimed-row")]
    [InlineData("envelope-parent-drift")]
    [InlineData("room-envelope-drift")]
    [InlineData("relative-manifest")]
    [InlineData("linked-file")]
    [InlineData("placement-before-acceptance")]
    [InlineData("duplicate-acceptance")]
    [InlineData("duplicate-placement")]
    public async Task Preserved_continuation_accepts_only_a_bound_prior_engine_placement(string scenario)
    {
        // Relative-path evidence must resolve to the same file even when TEMP is on another drive.
        using var home = new IsolatedBatonHome(scenario == "relative-manifest" ? Directory.GetCurrentDirectory() : null);
        var workspace = Directory.CreateDirectory(Path.Combine(home.Path, "workspace")).FullName;
        await RunRealGitAsync(workspace, "init", "-q");
        await RunRealGitAsync(workspace, "config", "user.name", "Baton Test");
        await RunRealGitAsync(workspace, "config", "user.email", "test@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "base", TestContext.Current.CancellationToken);
        await RunRealGitAsync(workspace, "add", "README.md");
        await RunRealGitAsync(workspace, "commit", "-qm", "base");
        var head = await ReadRealGitAsync(workspace, "rev-parse", "HEAD");

        var projected = Path.Combine(workspace, ".claude", "skills", "baton-review", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(projected)!);
        await File.WriteAllTextAsync(projected, "engine-owned", TestContext.Current.CancellationToken);
        var parent = new FleetAttemptId("parent-engine-projection");
        var current = new FleetAttemptId("current-continuation");
        var room = Directory.CreateDirectory(Path.Combine(BatonPaths.Rooms, "queue-parent")).FullName;
        await using (var writer = new FlowEventLogWriter(Path.Combine(room, BatonPaths.FlowLogFileName)))
        {
            var execution = new ExecutionId("placed-execution");
            var acceptance = new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                execution, new WorkflowId("dispatch-review"), new StepId("review"), "review",
                Inputs: [], Outputs: [], Timeout: TimeSpan.FromMinutes(5), Environment: [],
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>()));
            var placement = new FlowEvent.EngineFilesPlaced(execution,
                [new EnginePlacedFile(projected, EnginePlacedFile.TryDigest(projected))], []);
            if (scenario == "placement-before-acceptance")
                await writer.AppendAsync(placement, TestContext.Current.CancellationToken);
            await writer.AppendAsync(acceptance, TestContext.Current.CancellationToken);
            if (scenario == "duplicate-acceptance")
                await writer.AppendAsync(acceptance, TestContext.Current.CancellationToken);
            if (scenario != "placement-before-acceptance")
                await writer.AppendAsync(placement, TestContext.Current.CancellationToken);
            if (scenario == "duplicate-placement")
                await writer.AppendAsync(placement, TestContext.Current.CancellationToken);
        }
        var log = new FleetEventLog(BatonPaths.FleetEventsFile, BatonPaths.FleetEventsRolloverFile, 100_000);
        var at = DateTimeOffset.UtcNow.AddMinutes(-2);
        await log.Append(new FleetEventDraft(FleetEventKind.AttemptStarted, "attempt-started:parent", at,
            AttemptId: parent, WorkId: new FleetWorkId("owned-task"),
            RoomId: new FleetRoomId(BatonPaths.RecordKey(room))), TestContext.Current.CancellationToken);
        await log.Append(new FleetEventDraft(FleetEventKind.AttemptSettled, "attempt-settled:parent", at.AddMinutes(1),
            AttemptId: parent, WorkId: new FleetWorkId("owned-task"),
            RoomId: new FleetRoomId(BatonPaths.RecordKey(room)), Outcome: WorkflowOutcome.Succeeded),
            TestContext.Current.CancellationToken);
        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
        {
            Items = [new QueueItem
            {
                Tag = "owned-task", Role = "implement", Workspace = workspace,
                SpecFile = "brief.md", State = QueueItemState.Launched, Stage = WorkStage.Continue,
                Repository = "github.com/aer-works/baton", Branch = "2178-lane", PullRequest = 2304,
                AttemptId = current, ParentAttemptId = parent,
                OriginatingPullRequestRecoveryClaim = current,
                ExpectedOriginatingPullRequestHead = head,
                AttemptEnvelope = new QueueAttemptEnvelope(current, parent, "owned-task", null, 2304,
                    WorkStage.Continue, "implement", null, null, null, [], null, null, "admitted",
                    Path.Combine(BatonPaths.Rooms, "queue-current"), null, head, DateTimeOffset.UtcNow),
            }],
        }, TestContext.Current.CancellationToken);

        switch (scenario)
        {
            case "changed-bytes":
                await File.WriteAllTextAsync(projected, "worker modified", TestContext.Current.CancellationToken);
                break;
            case "unrelated-file":
                await File.WriteAllTextAsync(Path.Combine(workspace, "unrelated.txt"), "worker", TestContext.Current.CancellationToken);
                break;
            case "tracked-edit":
                await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "modified", TestContext.Current.CancellationToken);
                break;
            case "missing-journal":
                FileCleanup.EnsureDeleted(Path.Combine(room, BatonPaths.FlowLogFileName));
                break;
            case "torn-journal":
                await File.AppendAllTextAsync(Path.Combine(room, BatonPaths.FlowLogFileName), "{", TestContext.Current.CancellationToken);
                break;
            case "null-manifest":
            case "escaped-manifest":
            case "relative-manifest":
                if (scenario == "escaped-manifest")
                {
                    var outside = Path.Combine(home.Path, "outside.txt");
                    await File.WriteAllTextAsync(outside, "outside", TestContext.Current.CancellationToken);
                    await using var escaped = new FlowEventLogWriter(Path.Combine(room, BatonPaths.FlowLogFileName));
                    await escaped.AppendAsync(new FlowEvent.EngineFilesPlaced(new ExecutionId("placed-execution"),
                        [new EnginePlacedFile(outside, EnginePlacedFile.TryDigest(outside))], []),
                        TestContext.Current.CancellationToken);
                }
                else if (scenario == "relative-manifest")
                {
                    var relativePlacement = Path.GetRelativePath(Directory.GetCurrentDirectory(), projected);
                    Assert.False(Path.IsPathFullyQualified(relativePlacement));
                    Assert.Equal(projected, Path.GetFullPath(relativePlacement));
                    await using var relative = new FlowEventLogWriter(Path.Combine(room, BatonPaths.FlowLogFileName));
                    await relative.AppendAsync(new FlowEvent.EngineFilesPlaced(new ExecutionId("placed-execution"),
                        [new EnginePlacedFile(relativePlacement, EnginePlacedFile.TryDigest(projected))], []),
                        TestContext.Current.CancellationToken);
                }
                else
                {
                    await using var empty = new FlowEventLogWriter(Path.Combine(room, BatonPaths.FlowLogFileName));
                    await empty.AppendAsync(new FlowEvent.EngineFilesPlaced(new ExecutionId("placed-execution"),
                        null, []), TestContext.Current.CancellationToken);
                }
                break;
            case "missing-lineage":
            case "wrong-claim":
            case "wrong-workspace":
            case "envelope-parent-drift":
            case "room-envelope-drift":
                await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
                {
                    Items = snapshot.Items.Select(item => scenario switch
                    {
                        "missing-lineage" => item with { ParentAttemptId = new FleetAttemptId("unrecorded-parent") },
                        "wrong-claim" => item with { OriginatingPullRequestRecoveryClaim = new FleetAttemptId("other-attempt") },
                        "envelope-parent-drift" => item with { ParentAttemptId = new FleetAttemptId("different-parent") },
                        "room-envelope-drift" => item with
                        {
                            RoomDirectory = Path.Combine(BatonPaths.Rooms, "queue-current"),
                            AttemptEnvelope = item.AttemptEnvelope! with
                            { RoomDirectory = Path.Combine(BatonPaths.Rooms, "other-room") },
                        },
                        _ => item with { Workspace = Path.Combine(home.Path, "other-workspace") },
                    }).ToArray(),
                }, TestContext.Current.CancellationToken);
                break;
            case "other-claimed-row":
                var otherParent = new FleetAttemptId("other-parent");
                var otherCurrent = new FleetAttemptId("other-current");
                var otherRoom = Directory.CreateDirectory(Path.Combine(BatonPaths.Rooms, "queue-other-parent")).FullName;
                await using (var otherWriter = new FlowEventLogWriter(Path.Combine(otherRoom, BatonPaths.FlowLogFileName)))
                {
                    var otherExecution = new ExecutionId("other-placement");
                    await otherWriter.AppendAsync(new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                        otherExecution, new WorkflowId("dispatch-review"), new StepId("review"), "review",
                        Inputs: [], Outputs: [], Timeout: TimeSpan.FromMinutes(5), Environment: [],
                        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>())), TestContext.Current.CancellationToken);
                    await otherWriter.AppendAsync(new FlowEvent.EngineFilesPlaced(otherExecution,
                        [new EnginePlacedFile(projected, EnginePlacedFile.TryDigest(projected))], []),
                        TestContext.Current.CancellationToken);
                }
                await log.Append(new FleetEventDraft(FleetEventKind.AttemptStarted, "attempt-started:other-parent", at,
                    AttemptId: otherParent, WorkId: new FleetWorkId("other-task"),
                    RoomId: new FleetRoomId(BatonPaths.RecordKey(otherRoom))), TestContext.Current.CancellationToken);
                await log.Append(new FleetEventDraft(FleetEventKind.AttemptSettled, "attempt-settled:other-parent", at.AddMinutes(1),
                    AttemptId: otherParent, WorkId: new FleetWorkId("other-task"),
                    RoomId: new FleetRoomId(BatonPaths.RecordKey(otherRoom)), Outcome: WorkflowOutcome.Succeeded),
                    TestContext.Current.CancellationToken);
                await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
                {
                    Items = [snapshot.Items[0] with { OriginatingPullRequestRecoveryClaim = null },
                        snapshot.Items[0] with
                        {
                            Tag = "other-task", AttemptId = otherCurrent, ParentAttemptId = otherParent,
                            OriginatingPullRequestRecoveryClaim = otherCurrent,
                            AttemptEnvelope = new QueueAttemptEnvelope(otherCurrent, otherParent,
                                "other-task", null, 2304, WorkStage.Continue, "implement", null, null, null,
                                [], null, null, "admitted", Path.Combine(BatonPaths.Rooms, "queue-other-current"),
                                null, head, DateTimeOffset.UtcNow),
                        }],
                }, TestContext.Current.CancellationToken);
                break;
            case "linked-file":
                var referent = Path.Combine(home.Path, "referent.txt");
                await File.WriteAllTextAsync(referent, "engine-owned", TestContext.Current.CancellationToken);
                FileCleanup.EnsureDeleted(projected);
                try { File.CreateSymbolicLink(projected, referent); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Assert.Skip($"this host cannot create file symbolic links: {ex.Message}");
                }
                break;
        }

        var claimedHead = scenario == "wrong-head" ? new string('a', 40) : head;
        var recovery = RecoveryOptions(Path.Combine(BatonPaths.Rooms, "queue-current"));
        recovery = scenario switch
        {
            "wrong-invoking-room" => recovery with { RoomDirectoryPath = Path.Combine(BatonPaths.Rooms, "other-room") },
            "wrong-invoking-pr" => recovery with { OriginatingPullRequest = "aer-works/baton#2305" },
            "wrong-invoking-branch" => recovery with { OriginatingPullRequestBranch = "other-branch" },
            _ => recovery,
        };
        if (scenario is "valid" or "duplicate-placement")
        {
            await OriginatingPullRequestVerifier.ValidatePreservedContinuationAsync(
                workspace, claimedHead, head, TestContext.Current.CancellationToken, recovery);
        }
        else
        {
            var refused = await Assert.ThrowsAsync<CliArgumentException>(() =>
                OriginatingPullRequestVerifier.ValidatePreservedContinuationAsync(
                    workspace, claimedHead, head, TestContext.Current.CancellationToken, recovery));
            Assert.Contains("ownership was refused before launch", refused.Message, StringComparison.Ordinal);
        }
    }

    private static async Task RunRealGitAsync(string workspace, params string[] args) =>
        _ = await ReadRealGitAsync(workspace, args);

    private static async Task<string> ReadRealGitAsync(string workspace, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = global::Baton.Core.ProcessLaunch.Start(start)!;
        var (stdout, stderr) = await BoundedProcessWait.RunToExitAsync(
            process, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, stderr);
        return stdout.Trim();
    }

    private static string CrossRepositoryValue(string kind) => kind switch
    {
        "null" => "null",
        "string" => "\"false\"",
        "number" => "0",
        "object" => "{}",
        "array" => "[]",
        "true" => "true",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static void CopyHermeticProbeHost(string destination)
    {
        var sourceDirectory = Path.GetDirectoryName(typeof(Scenarios).Assembly.Location)!;
        const string hostPrefix = "Baton.CrashTestHost";
        foreach (var source in Directory.EnumerateFiles(sourceDirectory))
        {
            var name = Path.GetFileName(source);
            if (name.StartsWith(hostPrefix, StringComparison.Ordinal)
                || name.Equals("Baton.dll", StringComparison.Ordinal))
            {
                File.Copy(source, Path.Combine(destination, name));
            }
        }
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
        }
    }

    private static DispatchOptions RecoveryOptions(string room = "room") => new(
        "implement", "2178.md", room,
        OriginatingPullRequest: "aer-works/baton#2304",
        OriginatingPullRequestBranch: "2178-lane");
}
