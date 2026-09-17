using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Queue;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests.Daemon;

public sealed class QueueFleetEventOutboxTests : IDisposable
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-17T12:00:00Z");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baton-queue-outbox-{Guid.NewGuid():N}");

    private string QueuePath => Path.Combine(_root, "queue.json");
    private string LivePath => Path.Combine(_root, "events.jsonl");
    private string RolloverPath => Path.Combine(_root, "events.1.jsonl");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public QueueFleetEventOutboxTests() => Directory.CreateDirectory(_root);

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_root);

    [Theory]
    [InlineData(FleetEventKind.AdmissionDecided)]
    [InlineData(FleetEventKind.AttemptRefused)]
    [InlineData(FleetEventKind.AttemptStarted)]
    [InlineData(FleetEventKind.AttemptSettled)]
    public async Task A_crash_before_append_replays_each_attempt_fact_once(FleetEventKind kind)
    {
        await WritePendingAsync(kind);
        var crashing = new QueueFleetEventOutbox((_, _) => throw new IOException("crash before append"));

        await Assert.ThrowsAsync<IOException>(() => crashing.PumpAsync(QueuePath, Ct));
        Assert.Single((await QueueStore.LoadAsync(QueuePath, Ct)).PendingFleetEvents!);

        var log = Log();
        var restarted = new QueueFleetEventOutbox(log.Append);
        await restarted.PumpAsync(QueuePath, Ct);
        await restarted.PumpAsync(QueuePath, Ct);

        var snapshot = await QueueStore.LoadAsync(QueuePath, Ct);
        Assert.Null(snapshot.PendingFleetEvents);
        AssertDurable(Assert.Single(snapshot.Items), kind);
        Assert.Equal(kind, Assert.Single(await log.ReadAfter(0, Ct)).Kind);
    }

    [Theory]
    [InlineData(FleetEventKind.AdmissionDecided)]
    [InlineData(FleetEventKind.AttemptRefused)]
    [InlineData(FleetEventKind.AttemptStarted)]
    [InlineData(FleetEventKind.AttemptSettled)]
    public async Task A_crash_after_append_before_acknowledgement_is_deduplicated(FleetEventKind kind)
    {
        await WritePendingAsync(kind);
        var log = Log();
        var crashing = new QueueFleetEventOutbox(async (draft, cancellationToken) =>
        {
            await log.Append(draft, cancellationToken);
            throw new IOException("crash after append");
        });

        await Assert.ThrowsAsync<IOException>(() => crashing.PumpAsync(QueuePath, Ct));
        Assert.Single((await QueueStore.LoadAsync(QueuePath, Ct)).PendingFleetEvents!);
        Assert.Single(await log.ReadAfter(0, Ct));

        var restarted = new QueueFleetEventOutbox(log.Append);
        await restarted.PumpAsync(QueuePath, Ct);

        var snapshot = await QueueStore.LoadAsync(QueuePath, Ct);
        Assert.Null(snapshot.PendingFleetEvents);
        AssertDurable(Assert.Single(snapshot.Items), kind);
        Assert.Single(await log.ReadAfter(0, Ct));
    }

    [Fact]
    public async Task Replaying_an_acknowledged_queue_is_byte_for_byte_stable()
    {
        await WritePendingAsync(FleetEventKind.AttemptStarted);
        var log = Log();
        var pump = new QueueFleetEventOutbox(log.Append);
        await pump.PumpAsync(QueuePath, Ct);
        var queueAfterFirstReplay = await File.ReadAllBytesAsync(QueuePath, Ct);
        var logAfterFirstReplay = await File.ReadAllBytesAsync(LivePath, Ct);

        await pump.PumpAsync(QueuePath, Ct);

        Assert.Equal(queueAfterFirstReplay, await File.ReadAllBytesAsync(QueuePath, Ct));
        Assert.Equal(logAfterFirstReplay, await File.ReadAllBytesAsync(LivePath, Ct));
    }

    [Fact]
    public async Task A_contradictory_pending_fact_fails_closed_with_attempt_and_work_identity()
    {
        var snapshot = Snapshot(FleetEventKind.AttemptStarted);
        var contradictory = Draft(FleetEventKind.AttemptStarted) with { Model = "a-different-model" };
        snapshot = snapshot with
        {
            PendingFleetEvents = [Serialize(contradictory)],
        };
        await SaveAsync(snapshot);

        var error = await Assert.ThrowsAsync<QueueFleetEventOutboxException>(
            () => new QueueFleetEventOutbox(Log().Append).PumpAsync(QueuePath, Ct));

        Assert.Equal("attempt-a", error.AttemptId);
        Assert.Equal("work-a", error.WorkTag);
        Assert.Contains("contradicts", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(LivePath));
    }

    [Fact]
    public async Task A_started_fact_before_durable_admission_fails_closed()
    {
        var snapshot = Snapshot(FleetEventKind.AttemptStarted);
        snapshot = snapshot with
        {
            Items = snapshot.Items.Select(item => item with
            {
                AttemptAdmissionFactDurable = false,
                AttemptStartedFactDurable = false,
            }).ToList(),
        };
        await SaveAsync(snapshot);

        var error = await Assert.ThrowsAsync<QueueFleetEventOutboxException>(
            () => new QueueFleetEventOutbox(Log().Append).PumpAsync(QueuePath, Ct));

        Assert.Equal("attempt-a", error.AttemptId);
        Assert.Equal("work-a", error.WorkTag);
        Assert.Contains("fact order", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(LivePath));
    }

    [Fact]
    public async Task A_malformed_pending_fact_fails_closed_with_available_identity()
    {
        using var malformed = JsonDocument.Parse("""
            {"kind":"attemptStarted","dedupeKey":"attempt-started:attempt-a","attemptId":"attempt-a","workId":"work-a"}
            """);
        var snapshot = Snapshot(FleetEventKind.AttemptStarted) with
        {
            PendingFleetEvents = [malformed.RootElement.Clone()],
        };
        await SaveAsync(snapshot);

        var error = await Assert.ThrowsAsync<QueueFleetEventOutboxException>(
            () => new QueueFleetEventOutbox(Log().Append).PumpAsync(QueuePath, Ct));

        Assert.Equal("attempt-a", error.AttemptId);
        Assert.Equal("work-a", error.WorkTag);
        Assert.Contains("malformed", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(LivePath));
    }

    [Fact]
    public async Task Duplicate_pending_keys_fail_closed_before_any_append()
    {
        var draft = Draft(FleetEventKind.AdmissionDecided);
        var raw = Serialize(draft);
        await SaveAsync(Snapshot(FleetEventKind.AdmissionDecided) with
        {
            PendingFleetEvents = [raw, raw],
        });

        var error = await Assert.ThrowsAsync<QueueFleetEventOutboxException>(
            () => new QueueFleetEventOutbox(Log().Append).PumpAsync(QueuePath, Ct));

        Assert.Equal("attempt-a", error.AttemptId);
        Assert.Equal("work-a", error.WorkTag);
        Assert.Contains("duplicated", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(LivePath));
    }

    private FleetEventLog Log() => new(LivePath, RolloverPath, maxLiveBytes: 100_000);

    private async Task WritePendingAsync(FleetEventKind kind) => await SaveAsync(Snapshot(kind));

    private Task SaveAsync(QueueSnapshot snapshot) =>
        QueueStore.MutateAsync(QueuePath, _ => snapshot, Ct);

    private static QueueSnapshot Snapshot(FleetEventKind kind)
    {
        var item = new QueueItem
        {
            Tag = "work-a",
            Role = "implement",
            ScopeClass = "engine",
            Workspace = @"C:\repos\work-a",
            SpecFile = @"C:\specs\work-a.md",
            Stage = WorkStage.Implement,
            State = kind is FleetEventKind.AttemptRefused or FleetEventKind.AttemptSettled
                ? QueueItemState.Failed
                : QueueItemState.Launched,
            RoomDirectory = kind is FleetEventKind.AttemptStarted or FleetEventKind.AttemptSettled
                ? @"C:\rooms\room-a"
                : null,
            AttemptId = new FleetAttemptId("attempt-a"),
            AttemptBaseRevision = "base-a",
            AttemptEnvelope = Envelope(),
            AttemptAdmissionFactDurable = kind != FleetEventKind.AdmissionDecided,
            AttemptStartedFactDurable = kind == FleetEventKind.AttemptSettled,
            LastAdmission = new TaskRequirementAdmission(
                ["repository-read"], ["repository-read", "artifact:changes.md"],
                TaskRequirementAdmission.Admitted, []),
        };
        var initial = new QueueSnapshot([item]);
        return QueueFleetEventOutbox.Enqueue(initial, Draft(kind));
    }

    private static QueueAttemptEnvelope Envelope() => new(
        new FleetAttemptId("attempt-a"),
        ParentAttemptId: null,
        WorkId: "work-a",
        Issue: 2363,
        PullRequest: null,
        Stage: WorkStage.Implement,
        DeclaredRole: "implement",
        Adapter: "codex",
        Model: "gpt-5.6-terra",
        Effort: "high",
        EffectiveGrant: ["repository-read", "artifact:changes.md"],
        RequestedRequirements: ["repository-read"],
        MissingCapabilities: [],
        AdmissionDecision: TaskRequirementAdmission.Admitted,
        RoomDirectory: @"C:\rooms\room-a",
        RoomId: "room-a",
        AttemptBaseRevision: "base-a",
        FactTimestamp: At);

    private static FleetEventDraft Draft(FleetEventKind kind) => new(
        kind,
        kind switch
        {
            FleetEventKind.AdmissionDecided => "admission:attempt-a",
            FleetEventKind.AttemptRefused => "attempt-refused:attempt-a",
            FleetEventKind.AttemptStarted => "attempt-started:attempt-a",
            FleetEventKind.AttemptSettled => "attempt-settled:attempt-a",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        },
        At,
        AttemptId: new FleetAttemptId("attempt-a"),
        WorkId: new FleetWorkId("work-a"),
        RoomId: kind is FleetEventKind.AttemptStarted or FleetEventKind.AttemptSettled
            ? new FleetRoomId("room-a")
            : null,
        IssueId: 2363,
        Vendor: "codex",
        Model: "gpt-5.6-terra",
        Effort: "high",
        DeclaredRole: "implement",
        EffectiveGrant: ["repository-read", "artifact:changes.md"],
        RequestedRequirements: ["repository-read"],
        MissingCapabilities: [],
        AdmissionDecision: TaskRequirementAdmission.Admitted,
        Outcome: kind is FleetEventKind.AttemptRefused ? "refused" : null,
        Stage: WorkStages.Token(WorkStage.Implement),
        AttemptBaseRevision: "base-a");

    private static JsonElement Serialize(FleetEventDraft draft)
    {
        using var document = JsonDocument.Parse(FleetEventLog.SerializeDraft(draft));
        return document.RootElement.Clone();
    }

    private static void AssertDurable(QueueItem item, FleetEventKind kind)
    {
        Assert.True(item.AttemptAdmissionFactDurable);
        Assert.Equal(kind == FleetEventKind.AttemptRefused, item.AttemptRefusedFactDurable);
        Assert.Equal(
            kind is FleetEventKind.AttemptStarted or FleetEventKind.AttemptSettled,
            item.AttemptStartedFactDurable);
        Assert.Equal(kind == FleetEventKind.AttemptSettled, item.AttemptSettledFactDurable);
    }
}
