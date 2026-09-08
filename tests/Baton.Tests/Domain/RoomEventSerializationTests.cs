using System.Text.Json;
using Baton.Domain;
using Baton.Projection;
using Baton.Store;

namespace Baton.Tests.Domain;

public class RoomEventSerializationTests
{
    private static readonly HeldWorkRef LaneRef = new("lanes/lane-1");
    private const string CitedSubject = "exec-lane-1";

    /// <summary>
    /// A fixed instant, not <see cref="DateTimeOffset.UtcNow"/> (#1206). xunit builds a theory case's
    /// NAME out of its arguments, so a clock reading in the data meant every run invented new test
    /// names for the same cases — nothing downstream could follow one across runs, and a genuine
    /// flake in one would have looked like a brand-new test each time rather than a recurrence.
    /// Measured by flake-watch's first run: 81 cases here and in the two sibling round-trip classes,
    /// each seen in exactly one of three passes. Nothing in these round-trips reads the value, only
    /// carries it, so a constant costs the coverage nothing.
    /// </summary>
    private static readonly DateTimeOffset FixedInstant = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<RoomEvent> AllRoomEventVariants() =>
    [
        new RoomEvent.HeldWorkDispatched(LaneRef, "shape-flow", TimeSpan.FromMinutes(10), "operator-alice"),
        new RoomEvent.HeldWorkEscalated(LaneRef, "operator-bob"),
        new RoomEvent.HeldWorkResolved(LaneRef, new HeldWorkCitation(CitedSubject, "executionSucceeded", 1)),
        new RoomEvent.TurnHostDormancyEntered(3, FixedInstant),
        new RoomEvent.TurnHostDormancyCleared("operator", FixedInstant),
        new RoomEvent.RuntimePermissionAsked("req-1", new ExecutionId("ex-1"), new StepId("st-1"), "w-1", "claude", "corr-1", "ReadFiles", "{}", "ReadFiles", FixedInstant),
        new RoomEvent.RuntimePermissionAnswered("req-1", "AllowOnce", "{}", "ok", "op-1", FixedInstant),
        new RoomEvent.RuntimePermissionRevoked("req-1", "timeout", FixedInstant),
        new RoomEvent.WorkflowSwitched(false, "operator", FixedInstant),
        new RoomEvent.WorkflowSwitched(true, "operator", FixedInstant),
        new RoomEvent.WorkerJoined(new WorkerId("chat-worker"), "claude", "claude", "sonnet", "standard", FixedInstant),
        new RoomEvent.WorkerJoined(new WorkerId("chat-worker"), "claude", "claude", null, null, FixedInstant),
        new RoomEvent.WorkerRenamed(new WorkerId("chat-worker"), "claude-reviewer", FixedInstant),
        // Both AssignedBy shapes (#592 ruling 4): null is the implicit first assignment
        // (InteractiveSessions.cs's materialization, and every pre-#592 journal line); "operator" is
        // an explicit reassignment through the endpoint. Same null/non-null pairing WorkerJoined's
        // two rows above cover for Model/Effort.
        new RoomEvent.OrchestratorAssigned(new WorkerId("chat-worker"), FixedInstant),
        new RoomEvent.OrchestratorAssigned(new WorkerId("chat-worker"), FixedInstant, "operator"),
        // #1530
        new RoomEvent.ArrestRequestUnresolvable("latest", "ambiguous — 2 candidates", FixedInstant, FixedInstant),
        new RoomEvent.ArrestRequestExpired("exec-1", FixedInstant, FixedInstant),
        // #2073: both Reason shapes -- a bare `baton cancel` records null, `--reason` records the text.
        new RoomEvent.ArrestIntentRecorded("exec-1", "operator", "lane is looping", FixedInstant),
        new RoomEvent.ArrestIntentRecorded("exec-1", "operator", null, FixedInstant),
    ];

    [Theory]
    [MemberData(nameof(AllRoomEventVariants))]
    public void RoundTrips_through_RoomEvent_base_type_without_data_loss(RoomEvent original)
    {
        var json = JsonSerializer.Serialize(original, typeof(RoomEvent), FlowEventLogJson.Options);
        var deserialized = JsonSerializer.Deserialize<RoomEvent>(json, FlowEventLogJson.Options);

        Assert.NotNull(deserialized);
        var reserialized = JsonSerializer.Serialize(deserialized, typeof(RoomEvent), FlowEventLogJson.Options);

        Assert.Equal(json, reserialized);
        Assert.Equal(original.GetType(), deserialized.GetType());
    }

    [Fact]
    public void TurnHostDormancy_Projects_IsDormant_State()
    {
        var now = DateTimeOffset.UtcNow;
        var events = new RoomEvent[]
        {
            new RoomEvent.TurnHostDormancyEntered(3, now),
        };
        var state1 = RoomProjector.Project(events);
        Assert.True(state1.IsDormant);

        var events2 = new RoomEvent[]
        {
            new RoomEvent.TurnHostDormancyEntered(3, now),
            new RoomEvent.TurnHostDormancyCleared("operator", now.AddMinutes(1)),
        };
        var state2 = RoomProjector.Project(events2);
        Assert.False(state2.IsDormant);
    }

    [Fact]
    public void Deserializing_unknown_eventType_discriminator_throws()
    {
        const string json = """{"eventType":"unknownRoomEvent"}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RoomEvent>(json, FlowEventLogJson.Options));
    }
}
