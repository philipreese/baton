using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Baton.Domain;
using Baton.Store;

namespace Baton.Tests.Store;

/// <summary>
/// #604: the journal's wire contract. A <c>flow.jsonl</c> line that has lost a required member used
/// to deserialize into a valid-looking event for execution <c>""</c> and take part in projection as
/// though it were real, and enums used to persist as ordinals so reordering a declaration
/// reinterpreted every line already on disk. Both are silent, which is the worst failure available
/// to an event-sourced store — so both get an arm here, in both directions.
/// </summary>
public class FlowEventLogJsonTests
{
    private static readonly ExecutionId ExecutionId = new("exec-1");
    private static readonly WorkflowId WorkflowId = new("wf-1");
    private static readonly StepId StepId = new("step-1");
    private static readonly DecisionId DecisionId = new("dec-1");

    /// <summary>
    /// One instance of every <see cref="FlowEvent"/> variant. Hand-built rather than reflected into
    /// existence so each is a realistic line; <see cref="Every_FlowEvent_variant_is_covered_by_these_tests"/>
    /// is what stops the list from silently falling behind the type.
    /// </summary>
    // See RoomEventSerializationTests.FixedInstant (#1206): a clock reading in theory data renames
    // the case on every run.
    private static readonly DateTimeOffset FixedInstant = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<FlowEvent> AllVariants() =>
    [
        new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
            ExecutionId, WorkflowId, StepId, "worker", [], [], TimeSpan.FromSeconds(30), [],
            new Dictionary<StepId, ExecutionId>())),
        new FlowEvent.ExecutionRequestRejected(ExecutionId, "rejected"),
        new FlowEvent.ExecutionSucceeded(ExecutionId),
        new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification.Permanent, "reason"),
        new FlowEvent.ExecutionCancelled(ExecutionId),
        new FlowEvent.CancellationRequested(ExecutionId),
        new FlowEvent.WorkflowPaused(ExecutionId, StepId),
        new FlowEvent.ExternalDecisionRecorded(DecisionId, ExecutionId, DecisionType.Resume, StepId, null),
        new FlowEvent.WorkflowResumed(DecisionId),
        // #1577: EnginePid/EngineStartTime exercised non-null here -- a revival renewal, not just
        // the original null-identity creation, is the shape this round-trip must survive too.
        new FlowEvent.StepRetryScheduled(StepId, ExecutionId, FixedInstant, 100, EnginePid: 4242, EngineStartTime: FixedInstant),
        new FlowEvent.StepRetryForeclosed(StepId, ExecutionId, "dead pump, unfireable park", ForeclosedBy: "settle"),
        new FlowEvent.ZeroOutputsDespiteSubstantialWork(ExecutionId, "4 turns, 500 output tokens"),
        new FlowEvent.VerifyStarted(ExecutionId),
        new FlowEvent.VerifyPassed(ExecutionId),
        new FlowEvent.VerifyFailed(ExecutionId, ["fmt-check"], "GATES: FAIL 1 of 25 -- fmt-check", VerifyFailedKind.GatesFailed),
        new FlowEvent.VerifyNotRun(ExecutionId, "task absent: gates-quiet"),
        new FlowEvent.VerifyDeclarationIgnored(ExecutionId, "0f2b", "9ac1"),
        new FlowEvent.VerifyDeclarationUnreviewed(ExecutionId, "0f2b"),
        new FlowEvent.EngineFilesPlaced(ExecutionId, [@"C:\repo\.claude\skills\audit-tool\SKILL.md"], ["audit-tool"]),
        new FlowEvent.ExecutionArrested(ExecutionId, new WorkerUsage(TokensIn: 500_000, TokensOut: 120_000), ["manage_task"]),
        new FlowEvent.StepRebound(StepId, ExecutionId, "agy", "gemini-3-pro", "claude", "sonnet", "Vendor failover"),
        new FlowEvent.ExecutionIndeterminate(ExecutionId, "reason", ".captured-response.md", ["advice.md"]),
        new FlowEvent.CaptureResolved(StepId, ExecutionId, Accepted: true, Reason: "capture honestly satisfies advice.md", ResolvedOutputNames: ["advice.md"]),
        new FlowEvent.ExecutionProgress(ExecutionId),
        new FlowEvent.CancellationDelivered(ExecutionId),
        new FlowEvent.CancellationRejected(ExecutionId),
        new FlowEvent.DeliveryPrOpened(123, "734-lane"),
        new FlowEvent.DeliveryChecksGreen(123),
        new FlowEvent.DeliveryChecksRed(123),
        // Both Merged shapes: the bool has no natural "unset" wire value the way a nullable reference
        // does, so a strip test run only against one polarity would never notice a fail-open default.
        new FlowEvent.DeliveryMerged(123, Merged: true),
        new FlowEvent.DeliveryMerged(123, Merged: false),
        // #1373: the per-attempt start sha the crash-recovery timeout probe reads back.
        new FlowEvent.ExecutionAttemptStarted(ExecutionId, "0f2b9ac1"),
        // #1885: both MarkerLanded polarities, for the same reason DeliveryMerged carries both — the
        // bool has no natural "unset" wire value, and a strip test run only against `false` would read
        // a fail-open default as a correct replay. `false` is the load-bearing one: it is the durable
        // record that the marker channel never carried this loss at all.
        // #1888 adds TerminalReannouncement, whose three states are all on the wire here: the
        // declaration (false), the terminal re-announcement (true), and the pre-#1888 line that carried
        // no such field (null, and OMITTED rather than written -- the one WhenWritingNull member in this
        // union, so a replayed old journal is not re-serialized with a field its writer never had).
        new FlowEvent.StreamLogLossDeclared(
            ExecutionId, "stdout", "stream-truncated-by-write-failure", BytesSurrendered: 4096, MarkerLanded: false,
            TerminalReannouncement: false),
        new FlowEvent.StreamLogLossDeclared(
            ExecutionId, "stdout", "stream-truncated-by-write-failure", BytesSurrendered: null, MarkerLanded: false,
            TerminalReannouncement: true),
        new FlowEvent.StreamLogLossDeclared(
            ExecutionId, "stderr", "stream-truncated-by-write-failure", BytesSurrendered: 4096, MarkerLanded: true),
    ];

    /// <summary>
    /// #604 measured its finding on <c>ExecutionFailed</c> alone and said the fix should confirm
    /// across the population rather than assume it. This is what makes that true and keeps it true:
    /// a tenth variant added to <see cref="FlowEvent"/> fails here until it is covered, rather than
    /// quietly inheriting whatever the serializer happens to do.
    /// </summary>
    [Fact]
    public void Every_FlowEvent_variant_is_covered_by_these_tests()
    {
        var declared = typeof(FlowEvent)
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .Select(a => a.DerivedType)
            .ToHashSet();

        var covered = AllVariants().Select(row => row.Data.GetType()).ToHashSet();

        Assert.Equal(declared.OrderBy(t => t.Name), covered.OrderBy(t => t.Name));
    }

    [Theory]
    [MemberData(nameof(AllVariants))]
    public void A_line_that_lost_a_required_member_fails_replay_loudly(FlowEvent original)
    {
        var node = JsonNode.Parse(
            JsonSerializer.Serialize(original, typeof(FlowEvent), FlowEventLogJson.Options))!.AsObject();

        // Every member except the discriminator, one at a time — the shape of a truncated write, a
        // partial fsync, or a member renamed by a later version.
        var members = node.Select(pair => pair.Key).Where(k => k != "eventType").ToList();
        Assert.NotEmpty(members);

        foreach (var member in members)
        {
            var damaged = JsonNode.Parse(node.ToJsonString())!.AsObject();
            Assert.True(damaged.Remove(member));

            var json = damaged.ToJsonString();
            var exception = Record.Exception(
                () => JsonSerializer.Deserialize<FlowEvent>(json, FlowEventLogJson.Options));

            // A member with a default is optional by design (see the Reason arm below), so absence is
            // only required to throw for members that carry no default. Either way it must never
            // deserialize into an event whose required member is silently missing.
            if (exception is null)
            {
                var round = JsonSerializer.Deserialize<FlowEvent>(json, FlowEventLogJson.Options);
                Assert.NotNull(round);
                Assert.True(
                    IsOptional(original.GetType(), member),
                    $"{original.GetType().Name}.{member} deserialized while absent but is not an optional "
                    + "parameter — that is the silent-corruption path #604 exists to close.");
            }
            else
            {
                Assert.IsType<JsonException>(exception);
            }
        }
    }

    /// <summary>
    /// The control for the test above. Without this, a serializer that rejected <i>every</i> line
    /// would pass it — and a journal that stopped replaying after an upgrade is unrecoverable state.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllVariants))]
    public void An_intact_line_round_trips(FlowEvent original)
    {
        var json = JsonSerializer.Serialize(original, typeof(FlowEvent), FlowEventLogJson.Options);
        var deserialized = JsonSerializer.Deserialize<FlowEvent>(json, FlowEventLogJson.Options);

        Assert.Equal(
            json, JsonSerializer.Serialize(deserialized, typeof(FlowEvent), FlowEventLogJson.Options));
    }

    /// <summary>
    /// The additive direction #597 relied on, asserted against the real options rather than the
    /// defaults the test suite used to reach for. Adding a trailing member with a default must stay
    /// safe even though removing one is now loud — that distinction is the point of #604, and a fix
    /// that made both throw would break every journal written before the member existed.
    /// </summary>
    [Fact]
    public void A_line_predating_an_added_optional_member_still_replays_with_the_default()
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification.Permanent, "reason"),
            typeof(FlowEvent),
            FlowEventLogJson.Options))!.AsObject();

        // Guards the fixture itself: a rename would make Remove return false and this would quietly
        // become a test of a *current* line, passing while proving nothing.
        Assert.True(node.Remove(nameof(FlowEvent.ExecutionFailed.Reason)));

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(node.ToJsonString(), FlowEventLogJson.Options);

        var failed = Assert.IsType<FlowEvent.ExecutionFailed>(deserialized);
        Assert.Equal(ExecutionId, failed.ExecutionId);
        Assert.Equal(FailureClassification.Permanent, failed.FailureClassification);
        Assert.Null(failed.Reason);
    }

    /// <summary>
    /// #734 review: `DeliveryMerged.Merged` defaults `false` specifically so a line that lost the
    /// field replays as the unremarkable outcome (closed-unmerged), never as a fabricated merge — the
    /// generic strip test above only proves the line still deserializes, not which way it defaults.
    /// This pins the direction directly, the same way `A_line_predating_an_added_optional_member_still_replays_with_the_default`
    /// pins `ExecutionFailed.Reason`'s.
    /// </summary>
    [Fact]
    public void A_DeliveryMerged_line_that_lost_the_Merged_property_replays_as_false_not_true()
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.DeliveryMerged(123, Merged: true),
            typeof(FlowEvent),
            FlowEventLogJson.Options))!.AsObject();

        Assert.True(node.Remove(nameof(FlowEvent.DeliveryMerged.Merged)));

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(node.ToJsonString(), FlowEventLogJson.Options);

        var merged = Assert.IsType<FlowEvent.DeliveryMerged>(deserialized);
        Assert.Equal(123, merged.PullRequestNumber);
        Assert.False(merged.Merged);
    }

    /// <summary>
    /// #1888, the same pattern as the arm above and for the same reason: the generic strip test proves
    /// a line without <c>TerminalReannouncement</c> still replays, never which way it lands. The
    /// direction is a claim <c>spec/baton.md</c> §3 makes — a pre-#1888 line carried no such field, and
    /// its absence must read as UNKNOWN rather than as "this was the declaration", which is what a
    /// non-nullable <c>bool</c> would have asserted about every journal written before the field
    /// existed.
    /// </summary>
    [Fact]
    public void A_StreamLogLossDeclared_line_that_lost_TerminalReannouncement_replays_as_null_not_false()
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.StreamLogLossDeclared(
                ExecutionId, "stdout", "stream-truncated-by-write-failure", BytesSurrendered: 4096,
                MarkerLanded: false, TerminalReannouncement: false),
            typeof(FlowEvent),
            FlowEventLogJson.Options))!.AsObject();

        // Guards the fixture: `false` is WRITTEN (WhenWritingNull only omits null), so a rename or a
        // widened ignore condition would make this Remove return false and the assertion below would
        // pass against a line that never carried the member.
        Assert.True(node.Remove(nameof(FlowEvent.StreamLogLossDeclared.TerminalReannouncement)));

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(node.ToJsonString(), FlowEventLogJson.Options);

        var loss = Assert.IsType<FlowEvent.StreamLogLossDeclared>(deserialized);
        Assert.False(loss.MarkerLanded);
        Assert.Null(loss.TerminalReannouncement);
    }

    [Fact]
    public void An_ExecutionRequestAccepted_line_predating_engine_process_identity_still_replays_with_nulls()
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionRequestAccepted(
                new ExecutionRequest(ExecutionId, WorkflowId, StepId, "worker", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>()),
                EnginePid: 12345,
                EngineStartTime: DateTimeOffset.UtcNow),
            typeof(FlowEvent),
            FlowEventLogJson.Options))!.AsObject();

        Assert.True(node.Remove(nameof(FlowEvent.ExecutionRequestAccepted.EnginePid)));
        Assert.True(node.Remove(nameof(FlowEvent.ExecutionRequestAccepted.EngineStartTime)));

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(node.ToJsonString(), FlowEventLogJson.Options);

        var accepted = Assert.IsType<FlowEvent.ExecutionRequestAccepted>(deserialized);
        Assert.Equal(ExecutionId, accepted.Request.ExecutionId);
        Assert.Null(accepted.EnginePid);
        Assert.Null(accepted.EngineStartTime);
    }

    /// <summary>
    /// <see cref="FlowEventLogJson"/>'s remarks forbid setting <c>DefaultIgnoreCondition</c>, and
    /// until now that rule lived only in prose. <c>WhenWritingNull</c> is the natural (and correct)
    /// choice for wire-frame options elsewhere in the tree, so copy-pasting it here is a live
    /// hazard — and it would make the writer omit a null required member the reader then rejects: a
    /// store that cannot read its own output. The round-trip theory above catches that for the
    /// variants that happen to carry a null; this pins the setting itself so the mistake fails by
    /// name.
    /// </summary>
    [Fact]
    public void The_options_never_omit_a_null_member_the_reader_requires()
    {
        Assert.Equal(JsonIgnoreCondition.Never, FlowEventLogJson.Options.DefaultIgnoreCondition);
    }

    [Fact]
    public void Enums_persist_by_name_so_reordering_a_declaration_cannot_reinterpret_the_journal()
    {
        var json = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification.Permanent, "reason"),
            typeof(FlowEvent),
            FlowEventLogJson.Options);

        Assert.Contains("\"Permanent\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"FailureClassification\":1", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="CoreExitReason"/> is persisted too, in <c>CoreEvent.ExecutionExited</c>. Its own doc
    /// comment (<c>src/Baton/Domain/CoreEvent.cs</c>) promises the journal survives
    /// <c>BatonExitReason</c> being reordered or renumbered later. Storing it as an ordinal instead of
    /// by name would break that promise by re-coupling stability to this repo's own declaration order.
    /// Asserted so the comment and the code cannot drift apart again.
    /// </summary>
    /// <summary>
    /// #759's second reader's C1 DEFECT: the additive-member compat pattern above existed only for
    /// <see cref="FlowEvent"/>, never for <see cref="CoreEvent"/> — where #759's <c>StderrTail</c>
    /// actually landed. Every journal written before #759 carries <c>executionExited</c> lines
    /// without the key; this is the test that makes breaking them loud instead of discovered.
    /// </summary>
    [Fact]
    public void A_CoreEvent_line_predating_StderrTail_still_replays_with_null()
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(
            (CoreEvent)new CoreEvent.ExecutionExited(ExecutionId, 1, CoreExitReason.Natural, "tail text"),
            typeof(CoreEvent),
            FlowEventLogJson.Options))!.AsObject();

        // Same fixture guard as the FlowEvent arm: a rename must fail here, not silently turn this
        // into a test of a current line.
        Assert.True(node.Remove(nameof(CoreEvent.ExecutionExited.StderrTail)));

        var deserialized = JsonSerializer.Deserialize<CoreEvent>(node.ToJsonString(), FlowEventLogJson.Options);

        var exited = Assert.IsType<CoreEvent.ExecutionExited>(deserialized);
        Assert.Equal(ExecutionId, exited.ExecutionId);
        Assert.Equal(1, exited.ExitCode);
        Assert.Null(exited.StderrTail);
    }

    [Fact]
    public void CoreExitReason_persists_by_name_as_its_own_stability_claim_requires()
    {
        var json = JsonSerializer.Serialize(
            (CoreEvent)new CoreEvent.ExecutionExited(ExecutionId, 0, CoreExitReason.TimedOut),
            typeof(CoreEvent),
            FlowEventLogJson.Options);

        Assert.Contains("\"TimedOut\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Reason\":1", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The migration answer, and the reason there is no migration step: journals written before #604
    /// carry ordinals, and the reader still accepts them. Without this the change would be a breaking
    /// change to durable data rather than a widening of the reader.
    /// </summary>
    [Theory]
    [InlineData(0, FailureClassification.Retryable)]
    [InlineData(1, FailureClassification.Permanent)]
    [InlineData(2, FailureClassification.ExhaustedUntil)]
    public void A_journal_written_before_this_change_still_replays_its_ordinal_enums(
        int ordinal, FailureClassification expected)
    {
        var legacy =
            $"{{\"eventType\":\"executionFailed\",\"ExecutionId\":\"exec-1\","
            + $"\"FailureClassification\":{ordinal},\"Reason\":\"reason\"}}";

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(legacy, FlowEventLogJson.Options);

        var failed = Assert.IsType<FlowEvent.ExecutionFailed>(deserialized);
        Assert.Equal(expected, failed.FailureClassification);
    }

    /// <summary>
    /// Pins that the ordinal each legacy journal line carries still means what it meant when written.
    /// The compatibility test above reads ordinals, but it would keep passing if the declaration were
    /// reordered — both it and the journal would move together. This is what actually fails if a
    /// member is inserted or two are swapped, which is the edit #604 says nothing prevented.
    /// </summary>
    [Fact]
    public void The_ordinals_legacy_journals_carry_still_mean_what_they_meant_when_written()
    {
        Assert.Equal(0, (int)FailureClassification.Retryable);
        Assert.Equal(1, (int)FailureClassification.Permanent);
        Assert.Equal(2, (int)FailureClassification.ExhaustedUntil);

        Assert.Equal(0, (int)CoreExitReason.Natural);
        Assert.Equal(1, (int)CoreExitReason.TimedOut);
        Assert.Equal(2, (int)CoreExitReason.CancelRequested);

        // Reordering these silently reinterprets any pre-#604 externalDecisionRecorded line, exactly
        // as it would for the two above. Review of #604 found this enum was the one of the three with
        // neither arm — no ordinal pin and no by-name assertion — so nothing failed if it moved.
        Assert.Equal(0, (int)DecisionType.Resume);
        Assert.Equal(1, (int)DecisionType.Reject);
        Assert.Equal(2, (int)DecisionType.RetryWithRevision);
        Assert.Equal(3, (int)DecisionType.Supersede);

        // GrantAuditMode is deliberately absent: it carries JsonStringEnumConverter, so it
        // serializes by name and reordering cannot reinterpret a line — the walker below exempts
        // it for the same reason.
    }


    /// <summary>
    /// What made <c>DecisionType</c>'s gap invisible: the variant population is policed
    /// (<see cref="Every_FlowEvent_variant_is_covered_by_these_tests"/>) but the *enum* population was
    /// not, so a journal-reachable enum could carry no arm at all and nothing would notice. This walks
    /// the constructor graph of every persisted event and fails when it finds an enum the pins above
    /// do not name.
    /// </summary>
    [Fact]
    public void Every_enum_reachable_from_a_journal_line_is_pinned_by_these_tests()
    {
        // DeciderKind is absent for the same reason as GrantAuditMode: born with
        // JsonStringEnumConverter, so its former entry here pinned nothing.
        var pinned = new[] { typeof(FailureClassification), typeof(CoreExitReason), typeof(DecisionType) };

        var reachable = new HashSet<Type>();
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>(
            new[] { typeof(FlowEvent), typeof(CoreEvent) }
                .SelectMany(root => root
                    .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
                    .Cast<JsonDerivedTypeAttribute>()
                    .Select(a => a.DerivedType)));

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type))
            {
                continue;
            }

            foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()))
            {
                foreach (var candidate in Unwrap(parameter.ParameterType))
                {
                    if (candidate.IsEnum)
                    {
                        // An enum carrying JsonStringEnumConverter serializes by NAME: reordering
                        // its members cannot reinterpret a journal line, so it needs no ordinal
                        // pin. Adding the converter to an ALREADY-pinned enum makes this test fail
                        // (pinned != reachable) — deliberately, because that change reinterprets
                        // nothing going forward but strands the ordinals already on disk, and the
                        // author must face that before deleting the pin.
                        var stringConverted = candidate
                            .GetCustomAttributes(typeof(JsonConverterAttribute), inherit: false)
                            .Cast<JsonConverterAttribute>()
                            .Any(a => a.ConverterType == typeof(JsonStringEnumConverter));
                        if (!stringConverted)
                        {
                            reachable.Add(candidate);
                        }
                    }
                    else if (candidate.Namespace?.StartsWith("Baton", StringComparison.Ordinal) == true)
                    {
                        queue.Enqueue(candidate);
                    }
                }
            }
        }

        Assert.Equal(pinned.OrderBy(t => t.Name), reachable.OrderBy(t => t.Name));
    }

    /// <summary>Peels nullable and collection wrappers so the enum inside either is still seen.</summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            yield return underlying;
            yield break;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var inner in Unwrap(argument))
                {
                    yield return inner;
                }
            }

            yield break;
        }

        yield return type;
    }

    private static bool IsOptional(Type eventType, string memberName) =>
        eventType.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Any(p => string.Equals(p.Name, memberName, StringComparison.OrdinalIgnoreCase)
                && p.HasDefaultValue);
}



