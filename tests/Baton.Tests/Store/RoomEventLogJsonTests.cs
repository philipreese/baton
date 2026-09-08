using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Baton.Domain;
using Baton.Store;

namespace Baton.Tests.Store;

/// <summary>
/// Verifies serialization discipline for <c>room.jsonl</c> entries:
/// required parameter removal failure tests (the #784 pattern) extended to every <see cref="RoomEvent"/>
/// variant, and — since #885 — descending into every nested record a variant carries, so a required
/// member of e.g. <see cref="HeldWorkCitation"/> is exercised too, not only the outer event's members.
/// </summary>
public class RoomEventLogJsonTests
{
    private static readonly HeldWorkRef LaneRef = new("lanes/lane-1");
    private const string CitedSubject = "exec-lane-1";

    // A constant, not a clock: a theory case's NAME is built from its arguments, so a reading of
    // UtcNow here renamed these cases on every run (#1206). Reasoned out once, on
    // RoomEventSerializationTests.FixedInstant.
    private static readonly DateTimeOffset FixedInstant = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<RoomEvent> AllRoomEventVariants() =>
    [
        new RoomEvent.HeldWorkDispatched(LaneRef, "shape-flow", TimeSpan.FromMinutes(15), "operator-decider"),
        new RoomEvent.HeldWorkEscalated(LaneRef, "escalation-target"),
        new RoomEvent.HeldWorkResolved(LaneRef, new HeldWorkCitation(CitedSubject, "executionSucceeded", 0)),
        new RoomEvent.GrantRecorded(new GrantId("g-1"), new WorkerId("w-1"), GrantLevel.L1Dispatch, new GrantScope(), new SpendBounds(), "operator", FixedInstant),
        new RoomEvent.GrantAmended(new GrantId("g-2"), new GrantId("g-1"), new WorkerId("w-1"), GrantLevel.L2Tend, new GrantScope(), new SpendBounds(), "operator", FixedInstant),
        new RoomEvent.GrantRevoked(new GrantId("g-1"), "operator", FixedInstant, "reason"),
        new RoomEvent.EscalationRaised(new WorkerId("w-1"), EscalationTrigger.Spend, new EscalationSubject.Decision(new DecisionId("d-1")), FixedInstant),
        new RoomEvent.EscalationRaised(new WorkerId("turn-host"), EscalationTrigger.Confidence, new EscalationSubject.HostCondition("turn-watchdog-timeout", "turn exceeded its budget"), FixedInstant),
        new RoomEvent.TurnHostDormancyEntered(3, FixedInstant),
        new RoomEvent.TurnHostDormancyCleared("operator", FixedInstant),
        new RoomEvent.RuntimePermissionAsked("req-1", new ExecutionId("ex-1"), new StepId("st-1"), "w-1", "claude", "corr-1", "ReadFiles", "{}", "ReadFiles", FixedInstant),
        new RoomEvent.RuntimePermissionAnswered("req-1", "AllowOnce", "{}", "ok", "op-1", FixedInstant),
        new RoomEvent.RuntimePermissionRevoked("req-1", "timeout", FixedInstant),
        // Both polarities: the discriminator is shared, so a round trip that only ever carried one
        // value of IsOn would pass with the bool dropped entirely.
        new RoomEvent.WorkflowSwitched(false, "operator", FixedInstant),
        new RoomEvent.WorkflowSwitched(true, "operator", FixedInstant),
        // Both polarities of the optional ShellCommandPattern: RoomShell carries none, CommandInRoom
        // always carries one — a round trip that dropped it silently would still pass with only one.
        new RoomEvent.StandingPermissionRevoked("w-1", "RoomShell", null, "human", FixedInstant),
        new RoomEvent.StandingPermissionRevoked("w-1", "CommandInRoom", "git status", "human", FixedInstant),
        // 0054 §1/§6 (#1305): the participant lifecycle events. Two WorkerJoined variants so both
        // the all-populated and the null-model/effort shapes round-trip.
        new RoomEvent.WorkerJoined(new WorkerId("chat-worker"), "claude", "claude", "sonnet", "standard", FixedInstant),
        new RoomEvent.WorkerJoined(new WorkerId("chat-worker"), "claude", "claude", null, null, FixedInstant),
        new RoomEvent.WorkerRenamed(new WorkerId("chat-worker"), "claude-reviewer", FixedInstant),
        // Both AssignedBy shapes (#592 ruling 4), same pairing as WorkerJoined's two rows above:
        // null is the implicit first assignment, "operator" an explicit reassignment.
        new RoomEvent.OrchestratorAssigned(new WorkerId("chat-worker"), FixedInstant),
        new RoomEvent.OrchestratorAssigned(new WorkerId("chat-worker"), FixedInstant, "operator"),
        // #1530
        new RoomEvent.ArrestRequestUnresolvable("latest", "ambiguous — 2 candidates", FixedInstant, FixedInstant),
        new RoomEvent.ArrestRequestExpired("exec-1", FixedInstant, FixedInstant),
        // #2073: both Reason polarities, same pairing as WorkerJoined's rows above.
        new RoomEvent.ArrestIntentRecorded("exec-1", "operator", "lane is looping", FixedInstant),
        new RoomEvent.ArrestIntentRecorded("exec-1", "operator", null, FixedInstant),
    ];


    [Fact]
    public void Every_RoomEvent_variant_is_covered_by_these_tests()
    {
        var declared = typeof(RoomEvent)
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .Select(a => a.DerivedType)
            .ToHashSet();

        var covered = AllRoomEventVariants().Select(row => row.Data.GetType()).ToHashSet();

        Assert.Equal(declared.OrderBy(t => t.Name), covered.OrderBy(t => t.Name));
    }

    [Theory]
    [MemberData(nameof(AllRoomEventVariants))]
    public void A_room_line_that_lost_a_required_member_fails_replay_loudly(RoomEvent original)
    {
        var node = JsonNode.Parse(
            JsonSerializer.Serialize(original, typeof(RoomEvent), FlowEventLogJson.Options))!.AsObject();

        // Every removable member, top-level AND nested (e.g. citation.subject), each carrying the
        // record type that DECLARES it — so IsOptional is asked of HeldWorkCitation for a citation
        // member, not of the outer RoomEvent variant. A whole nested object (citation) is still a
        // target too, so removing a required nested record stays covered as before.
        var members = RemovableMembers(node, original.GetType());
        Assert.NotEmpty(members);

        foreach (var member in members)
        {
            var damaged = JsonNode.Parse(node.ToJsonString())!.AsObject();
            RemoveAt(damaged, member.Path);

            var json = damaged.ToJsonString();
            var exception = Record.Exception(
                () => JsonSerializer.Deserialize<RoomEvent>(json, FlowEventLogJson.Options));

            if (exception is null)
            {
                var round = JsonSerializer.Deserialize<RoomEvent>(json, FlowEventLogJson.Options);
                Assert.NotNull(round);
                Assert.True(
                    IsOptional(member.DeclaringType, member.Name),
                    $"{member.DeclaringType.Name}.{member.Name} (at {string.Join('.', member.Path)}) "
                    + "deserialized while absent but is not an optional parameter — silent corruption path.");
            }
            else
            {
                // NotSupportedException joins JsonException here for one specific removal: a
                // polymorphic subject's "kind" discriminator -- RoomEventLogReader has the why,
                // and RoomEventLogReaderCorruptionTests proves the reader wraps it loudly.
                Assert.True(exception is JsonException or NotSupportedException);
            }


        }
    }

    /// <summary>
    /// The control for the theory above: proves the removal walk actually DESCENDS into a nested
    /// record rather than skimming top-level keys. If the recursion in <see cref="RemovableMembers"/>
    /// regressed to top-level only, the theory would still pass (it just checks fewer members), so
    /// nothing else would catch the hole #885 named — this test is what discriminates.
    /// </summary>
    [Fact]
    public void The_removal_walk_descends_into_a_nested_record()
    {
        var resolved = new RoomEvent.HeldWorkResolved(
            LaneRef, new HeldWorkCitation(CitedSubject, "executionSucceeded", 0));
        var node = JsonNode.Parse(
            JsonSerializer.Serialize(resolved, typeof(RoomEvent), FlowEventLogJson.Options))!.AsObject();

        var members = RemovableMembers(node, resolved.GetType());

        Assert.Contains(members, m => m.Path.Count >= 2
            && m.DeclaringType == typeof(HeldWorkCitation)
            && string.Equals(m.Name, "subject", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(AllRoomEventVariants))]
    public void An_intact_room_line_round_trips(RoomEvent original)
    {
        var json = JsonSerializer.Serialize(original, typeof(RoomEvent), FlowEventLogJson.Options);
        var deserialized = JsonSerializer.Deserialize<RoomEvent>(json, FlowEventLogJson.Options);

        Assert.Equal(
            json, JsonSerializer.Serialize(deserialized, typeof(RoomEvent), FlowEventLogJson.Options));
    }

    private static bool IsOptional(Type declaringType, string memberName) =>
        declaringType.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Any(p => string.Equals(p.Name, memberName, StringComparison.OrdinalIgnoreCase)
                && p.HasDefaultValue);

    /// <summary>A member the walk can remove: where it sits, and the record type that declares it.</summary>
    private sealed record RemovableMember(IReadOnlyList<string> Path, Type DeclaringType, string Name);

    /// <summary>
    /// Every removable member of <paramref name="root"/>, descending into nested JSON objects. Each
    /// carries the CLR type that declares it (resolved via the owning record's constructor parameter),
    /// so a nested member is checked against its own record rather than the outer event.
    /// </summary>
    private static IReadOnlyList<RemovableMember> RemovableMembers(JsonObject root, Type variantType)
    {
        var targets = new List<RemovableMember>();
        Collect(root, variantType, [], isRoot: true, targets);
        return targets;
    }

    private static void Collect(
        JsonObject obj, Type owningType, List<string> path, bool isRoot, List<RemovableMember> targets)
    {
        foreach (var key in obj.Select(pair => pair.Key).ToList())
        {
            // "eventType" is the polymorphic discriminator ONLY at the root — nested records (e.g.
            // HeldWorkCitation.EventType) carry a real member of that name, so the skip is root-only.
            if (isRoot && key == "eventType")
            {
                continue;
            }

            var memberPath = new List<string>(path) { key };
            targets.Add(new RemovableMember(memberPath, owningType, key));

            var param = owningType.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));

            if (obj[key] is JsonObject child && param is not null)
            {
                Collect(child, param.ParameterType, memberPath, isRoot: false, targets);
            }
        }
    }

    private static void RemoveAt(JsonObject root, IReadOnlyList<string> path)
    {
        var cursor = root;
        for (var i = 0; i < path.Count - 1; i++)
        {
            cursor = cursor[path[i]]!.AsObject();
        }

        Assert.True(cursor.Remove(path[^1]));
    }
}

