using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;

namespace Baton.Cli.Tests.Daemon;

public sealed class ConductorObligationProjectionTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"baton-obligation-view-{Guid.NewGuid():N}");
    private readonly IDisposable _scope;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private const string StoppedKey = "stopped-judgment:github.com/example/project:job:attempt:review";
    private const string StoppedRepository = "github.com/example/project";
    private const string StoppedTag = "job";
    private const string StoppedAttempt = "attempt";
    private const string StoppedHead = "0123456789abcdef0123456789abcdef01234567";
    private static readonly DateTimeOffset StoppedAt = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ConductorObligationProjectionTests()
    {
        Directory.CreateDirectory(_home);
        _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = _home });
    }

    public void Dispose()
    {
        _scope.Dispose();
        DirectoryCleanup.DeleteRecursively(_home);
    }

    private static ConductorObligation Row(ConductorObligationStatus status) => new(
        "id", "key", "private-project", "private-room", null, null, "continue", "queue-lifecycle",
        DateTimeOffset.Parse("2026-09-29T12:00:00Z"), "adapter", "capability", true, status,
        ActionObservedAt: status == ConductorObligationStatus.ActionObserved ? DateTimeOffset.UtcNow : null,
        TransportReceipt: "secret-receipt", Reason: "C:\\secret\\cause", ActionProof: "secret-proof",
        TargetWorkspace: "C:\\private", ContextSha256: "secret-context");

    private void Snapshot(params ConductorObligation[] rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.ConductorObligationsFile)!);
        File.WriteAllText(BatonPaths.ConductorObligationsFile, JsonSerializer.Serialize(new
        {
            version = 2,
            openObligations = rows.Where(row => row.Status is not (ConductorObligationStatus.ActionObserved
                or ConductorObligationStatus.Blocked or ConductorObligationStatus.Unsupported)),
            terminalObligations = rows.Where(row => row.Status is ConductorObligationStatus.ActionObserved
                or ConductorObligationStatus.Blocked or ConductorObligationStatus.Unsupported),
        }, Json));
    }

    private static ConductorObligation StoppedRow(ConductorObligationStatus status) => new(
        "stopped-obligation", StoppedKey, StoppedRepository, null, StoppedTag, null,
        StoppedWorkJudgmentKey.Action, "repository-conductor", StoppedAt,
        StoppedWorkJudgmentKey.Adapter, StoppedWorkJudgmentKey.Capability, true, status,
        TargetRevision: StoppedHead, ContextSha256: "stopped-context");

    private static string StoppedEvidenceDirectory(string key) => Path.Combine(
        Path.GetDirectoryName(BatonPaths.ConductorObligationsFile)!, "stopped-work-advice",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant());

    private static string Marker(ConductorObligation row) => JsonSerializer.Serialize(new
    {
        obligationId = row.ObligationId,
        contextSha256 = row.ContextSha256,
        startedAt = StoppedAt,
    }, Json);

    private static RetainedStoppedWorkAdviceResponse Response(ConductorObligation row) => new(
        new StoppedWorkAdviceDecision(row.ObligationId, StoppedRepository, StoppedTag,
            StoppedAttempt, row.ContextSha256!, StoppedWorkAdviceChoice.Hold, "Needs operator review."),
        StoppedWorkJudgmentKey.Adapter, "gpt-5.6-luna", "low", StoppedAt,
        new StoppedWorkAdviceUsage(null, null, null));

    [Fact]
    public void Stopped_work_is_an_unresolved_conductor_request_without_private_owner()
    {
        var row = Row(ConductorObligationStatus.TransportAcknowledged) with
        {
            IdempotencyKey = "stopped-judgment:github.com/example/project:job:attempt:review",
            RequestedAction = "stopped-work-judgment",
            Owner = "secret-owner",
            AdapterCapability = "stopped-work-advice",
        };
        var view = ConductorObligationProjection.Project(new([row], new Dictionary<string, string>()));
        Assert.Equal("Assess stopped work", view["rows"]![0]!["requestedAction"]!.GetValue<string>());
        Assert.Equal("Repository conductor", view["rows"]![0]!["owner"]!.GetValue<string>());
        Assert.Equal(1, view["unresolvedCount"]!.GetValue<int>());
        Assert.Equal(0, view["completedCount"]!.GetValue<int>());
        Assert.DoesNotContain("secret", view.ToJsonString());
    }

    [Theory]
    [InlineData("not-a-receipt\n")]
    [InlineData("stopped-work-advice-sha256:wrong\n")]
    public async Task Corrupt_or_mismatched_receipt_never_exposes_retained_advice(string receipt)
    {
        var row = StoppedRow(ConductorObligationStatus.TransportAcknowledged) with
        {
            TransportReceipt = "stopped-work-advice-sha256:acknowledged-but-wrong",
        };
        Snapshot(row);
        var evidence = StoppedEvidenceDirectory(row.IdempotencyKey);
        Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence, "launch.json"), Marker(row));
        File.WriteAllText(Path.Combine(evidence, "response.json"), JsonSerializer.Serialize(Response(row), Json));
        File.WriteAllText(Path.Combine(evidence, "receipt.json"), receipt);

        using var document = JsonDocument.Parse(
            await new FleetProjectionWriter().BuildProjectionJsonAsync(TestContext.Current.CancellationToken));
        var advice = document.RootElement.GetProperty("conductorObligations").GetProperty("rows")[0]
            .GetProperty("advice");
        Assert.Equal("uncertain", advice.GetProperty("state").GetString());
        Assert.False(advice.TryGetProperty("choice", out _));
        Assert.False(advice.TryGetProperty("explanation", out _));
    }

    [Fact]
    public async Task Launch_marker_without_response_projects_uncertain_not_success()
    {
        var row = StoppedRow(ConductorObligationStatus.Submitted);
        Snapshot(row);
        var evidence = StoppedEvidenceDirectory(row.IdempotencyKey);
        Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence, "launch.json"), Marker(row));

        using var document = JsonDocument.Parse(
            await new FleetProjectionWriter().BuildProjectionJsonAsync(TestContext.Current.CancellationToken));
        var advice = document.RootElement.GetProperty("conductorObligations").GetProperty("rows")[0]
            .GetProperty("advice");
        Assert.Equal("uncertain", advice.GetProperty("state").GetString());
        Assert.False(advice.TryGetProperty("choice", out _));
        Assert.False(advice.TryGetProperty("explanation", out _));
    }

    [Fact]
    public async Task Transport_acknowledgement_remains_unresolved_and_has_no_advice_authority()
    {
        var row = StoppedRow(ConductorObligationStatus.TransportAcknowledged);
        Snapshot(row);

        using var document = JsonDocument.Parse(
            await new FleetProjectionWriter().BuildProjectionJsonAsync(TestContext.Current.CancellationToken));
        var obligations = document.RootElement.GetProperty("conductorObligations");
        var projected = obligations.GetProperty("rows")[0];
        Assert.Equal("TransportAcknowledged", projected.GetProperty("status").GetString());
        Assert.Equal(1, obligations.GetProperty("unresolvedCount").GetInt32());
        Assert.Equal("pending", projected.GetProperty("advice").GetProperty("state").GetString());
        Assert.False(projected.GetProperty("advice").TryGetProperty("choice", out _));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Unbound_halted_stopped_intent_is_visible_as_safe_blocked_work(
        bool missingHolder, bool missingAttempt)
    {
        var source = UnboundStoppedSource(missingHolder, missingAttempt);
        var view = ConductorObligationProjection.Project(
            new([], new Dictionary<string, string>()),
            stoppedSources: [source]);

        var projected = view["rows"]![0]!.AsObject();
        Assert.Equal("Assess stopped work", projected["requestedAction"]!.GetValue<string>());
        Assert.Equal("Repository conductor", projected["owner"]!.GetValue<string>());
        Assert.Equal("Blocked", projected["status"]!.GetValue<string>());
        Assert.Equal("Stopped-work evidence is incomplete; operator review required.",
            projected["reason"]!.GetValue<string>());
        Assert.Equal("blocked", projected["advice"]!["state"]!.GetValue<string>());
        Assert.Equal("review", projected["stage"]!.GetValue<string>());
        Assert.Equal(1934, projected["issue"]!.GetValue<int>());
        Assert.False(projected.ContainsKey("obligationId"));
        Assert.Equal(1, view["unresolvedCount"]!.GetValue<int>());
        Assert.Equal(0, view["completedCount"]!.GetValue<int>());
        Assert.Equal(0, view["omittedCount"]!.GetValue<int>());
        Assert.DoesNotContain("private-holder", view.ToJsonString());
        Assert.DoesNotContain("private-attempt", view.ToJsonString());
        Assert.DoesNotContain("private-key", view.ToJsonString());
    }

    [Fact]
    public async Task Read_async_keeps_queue_only_unbound_stopped_intent_visible()
    {
        await QueueStore.MutateAsync(BatonPaths.QueueFile,
            _ => new QueueSnapshot([UnboundStoppedSource(missingHolder: true, missingAttempt: false)]), Ct);

        var view = await ConductorObligationProjection.ReadAsync(Ct);

        Assert.True(view["available"]!.GetValue<bool>());
        Assert.Equal("Blocked", view["rows"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(1, view["unresolvedCount"]!.GetValue<int>());
        Assert.DoesNotContain("private-holder", view.ToJsonString());
    }

    [Theory]
    [InlineData(ConductorObligationStatus.Pending)]
    [InlineData(ConductorObligationStatus.Blocked)]
    public void Bound_stopped_rows_take_issue_and_stage_from_queue_evidence(
        ConductorObligationStatus status)
    {
        var row = StoppedRow(status);
        var view = ConductorObligationProjection.Project(
            new([row], new Dictionary<string, string>()),
            stoppedSources: [BoundStoppedSource(row)]);

        var projected = view["rows"]![0]!.AsObject();
        Assert.Equal(1934, projected["issue"]!.GetValue<int>());
        Assert.Equal("review", projected["stage"]!.GetValue<string>());
    }

    private static QueueItem UnboundStoppedSource(bool missingHolder, bool missingAttempt)
    {
        var attempt = new FleetAttemptId("private-attempt");
        return new QueueItem
        {
            Tag = "private-tag",
            Workspace = Path.GetTempPath(),
            Role = "review",
            Repository = StoppedRepository,
            Issue = 1934,
            Stage = WorkStage.Review,
            State = QueueItemState.Failed,
            Halted = true,
            AttemptId = missingAttempt ? null : attempt,
            SpecFile = Path.Combine(Path.GetTempPath(), "private-spec.md"),
            StoppedWorkJudgment = new StoppedWorkJudgment(
                Key: missingAttempt ? null : StoppedWorkJudgmentKey.For(
                    StoppedRepository, "private-tag", attempt, WorkStage.Review),
                Repository: StoppedRepository,
                Tag: "private-tag",
                AttemptId: missingAttempt ? null : attempt,
                Stage: WorkStage.Review,
                ObservedAt: StoppedAt,
                Holder: missingHolder ? null : "private-holder",
                PullRequest: 77,
                PullRequestHead: StoppedHead,
                AttemptBaseRevision: null,
                TerminalOutcome: "Succeeded",
                TerminalEvidenceAvailable: true,
                Checks: "passing",
                ChecksObservedAt: StoppedAt,
                ContextSha256: "private-context",
                HaltCause: StoppedWorkHaltCause.MissingVerdict,
                State: StoppedWorkJudgmentState.Blocked,
                Reason: "private cause"),
        };
    }

    private static QueueItem BoundStoppedSource(ConductorObligation row)
    {
        var attempt = new FleetAttemptId(StoppedAttempt);
        return new QueueItem
        {
            Tag = StoppedTag,
            Workspace = Path.GetTempPath(),
            Role = "review",
            Repository = StoppedRepository,
            Issue = 1934,
            Stage = WorkStage.Review,
            State = QueueItemState.Failed,
            Halted = true,
            AttemptId = attempt,
            SpecFile = Path.Combine(Path.GetTempPath(), "stopped-spec.md"),
            StoppedWorkJudgment = new StoppedWorkJudgment(
                row.IdempotencyKey,
                StoppedRepository,
                StoppedTag,
                attempt,
                WorkStage.Review,
                StoppedAt,
                "private-holder",
                77,
                StoppedHead,
                null,
                "Succeeded",
                true,
                "passing",
                StoppedAt,
                row.ContextSha256!,
                StoppedWorkHaltCause.MissingVerdict),
        };
    }

    [Theory]
    [InlineData(ConductorObligationStatus.Pending)]
    [InlineData(ConductorObligationStatus.Submitted)]
    [InlineData(ConductorObligationStatus.TransportAcknowledged)]
    [InlineData(ConductorObligationStatus.ActionObserved)]
    [InlineData(ConductorObligationStatus.Blocked)]
    [InlineData(ConductorObligationStatus.Unsupported)]
    public async Task Each_state_is_projected_without_private_payload_or_writes(ConductorObligationStatus status)
    {
        Snapshot(Row(status));
        var before = File.ReadAllBytes(BatonPaths.ConductorObligationsFile);
        var view = await ConductorObligationProjection.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(view["available"]!.GetValue<bool>());
        Assert.Equal(status.ToString(), view["rows"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(status == ConductorObligationStatus.ActionObserved ? 0 : 1,
            view["unresolvedCount"]!.GetValue<int>());
        Assert.DoesNotContain("secret", view.ToJsonString());
        Assert.DoesNotContain("private", view.ToJsonString());
        Assert.Equal(before, File.ReadAllBytes(BatonPaths.ConductorObligationsFile));
        Assert.False(File.Exists(BatonPaths.FleetEventsFile));
    }

    [Fact]
    public async Task Missing_invalid_and_unreadable_are_not_empty_success()
    {
        Assert.False((await ConductorObligationProjection.ReadAsync(TestContext.Current.CancellationToken))["available"]!.GetValue<bool>());
        Snapshot();
        Assert.True((await ConductorObligationProjection.ReadAsync(TestContext.Current.CancellationToken))["available"]!.GetValue<bool>());
        File.WriteAllText(BatonPaths.ConductorObligationsFile, "{broken");
        Assert.False((await ConductorObligationProjection.ReadAsync(TestContext.Current.CancellationToken))["available"]!.GetValue<bool>());
        Assert.Equal("{broken", File.ReadAllText(BatonPaths.ConductorObligationsFile));
        FileCleanup.EnsureDeleted(BatonPaths.ConductorObligationsFile);
        Directory.CreateDirectory(BatonPaths.ConductorObligationsFile);
        Assert.False((await ConductorObligationProjection.ReadAsync(TestContext.Current.CancellationToken))["available"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Actual_quarantined_facts_are_visible_without_repairing_any_files()
    {
        Snapshot(Row(ConductorObligationStatus.ActionObserved));
        var log = FleetEventLog.OpenOperational();
        await log.Append(new FleetEventDraft(FleetEventKind.ConductorObligationPending, "malformed",
            DateTimeOffset.UtcNow, ObligationIdempotencyKey: "key"), TestContext.Current.CancellationToken);
        var snapshotBefore = File.ReadAllBytes(BatonPaths.ConductorObligationsFile);
        var eventsBefore = File.ReadAllBytes(BatonPaths.FleetEventsFile);
        var view = await ConductorObligationProjection.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(view["available"]!.GetValue<bool>());
        Assert.Equal(1, view["quarantinedCount"]!.GetValue<int>());
        Assert.Empty(view["rows"]!.AsArray());
        Assert.Equal(snapshotBefore, File.ReadAllBytes(BatonPaths.ConductorObligationsFile));
        Assert.Equal(eventsBefore, File.ReadAllBytes(BatonPaths.FleetEventsFile));
    }

    [Fact]
    public async Task Fleet_writer_attaches_section_to_real_projection()
    {
        Snapshot(Row(ConductorObligationStatus.TransportAcknowledged));
        using var document = JsonDocument.Parse(await new FleetProjectionWriter()
            .BuildProjectionJsonAsync(TestContext.Current.CancellationToken));
        Assert.Equal("TransportAcknowledged", document.RootElement.GetProperty("conductorObligations")
            .GetProperty("rows")[0].GetProperty("status").GetString());
        Assert.Equal(FleetProjectionWriter.StaleAfter().TotalSeconds,
            document.RootElement.GetProperty("projectionStaleAfterSeconds").GetDouble());
    }

    [Fact]
    public void Quarantine_cannot_publish_completed_row_or_private_diagnostics()
    {
        var view = ConductorObligationProjection.Project(new([Row(ConductorObligationStatus.ActionObserved)],
            new Dictionary<string, string> { ["key"] = "C:\\secret\\corrupt" }));
        Assert.Empty(view["rows"]!.AsArray());
        Assert.Equal(1, view["quarantinedCount"]!.GetValue<int>());
        Assert.Equal(0, view["completedCount"]!.GetValue<int>());
        Assert.DoesNotContain("secret", view.ToJsonString());
    }

    [Fact]
    public void Row_cap_prioritizes_unresolved_and_retains_total_counts()
    {
        var rows = Enumerable.Range(0, 105).Select(index => Row(ConductorObligationStatus.ActionObserved) with
        { ObligationId = index.ToString(), IdempotencyKey = index.ToString() }).ToList();
        rows.Add(Row(ConductorObligationStatus.Blocked) with { Owner = "secret-owner", RequestedAction = "secret-action" });
        var view = ConductorObligationProjection.Project(new(rows, new Dictionary<string, string>()));
        Assert.Equal(100, view["rows"]!.AsArray().Count);
        Assert.Equal("Blocked", view["rows"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(105, view["completedCount"]!.GetValue<int>());
        Assert.Equal(6, view["omittedCount"]!.GetValue<int>());
        Assert.DoesNotContain("secret", view.ToJsonString());
    }

    [Theory]
    [InlineData(ConductorObligationStatus.Blocked, "the open obligation is not a deterministic queue continuation", "Invalid continuation identity")]
    [InlineData(ConductorObligationStatus.Blocked, "durable queue evidence contains more than one continuation for the obligation", "Conflicting continuation records")]
    [InlineData(ConductorObligationStatus.Blocked, "the source attempt is no longer present and no continuation queue evidence exists", "Source attempt missing; no continuation recorded")]
    [InlineData(ConductorObligationStatus.Unsupported, "the adapter does not support this obligation", "Adapter capability unavailable")]
    [InlineData(ConductorObligationStatus.Blocked, "C:\\secret\\reason", "Cause withheld; inspect local evidence for details")]
    [InlineData(ConductorObligationStatus.Unsupported, "the adapter does not support this obligation secret-token", "Cause withheld; inspect local evidence for details")]
    public void Known_causes_are_distinct_but_unknown_or_suffixed_causes_are_withheld(
        ConductorObligationStatus status, string cause, string expected)
    {
        var view = ConductorObligationProjection.Project(new([Row(status) with { Reason = cause }],
            new Dictionary<string, string>()));
        Assert.Equal(expected, view["rows"]![0]!["reason"]!.GetValue<string>());
        Assert.DoesNotContain("secret", view.ToJsonString());
    }
}
