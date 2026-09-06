using System.Text.Json;
using Baton.Domain;

using Baton.Store;

namespace Baton.Tests.Domain;

public class FlowEventSerializationTests
{
    private static readonly ExecutionId ExecutionId = new("exec-1");
    private static readonly StepId StepId = new("build");

    public static IEnumerable<object[]> AllEventVariants()
    {
        var request = new ExecutionRequest(
            ExecutionId,
            new WorkflowId("wf-1"),
            StepId,
            "claude",
            Inputs: ["/artifacts/execution_1/goal.md"],
            Outputs: ["/artifacts/execution_2/plan.md"],
            Timeout: TimeSpan.FromMinutes(10),
            Environment:
            [
                new EnvironmentVariable.BatonComputed("BATON_OUTPUT_DIR", "/artifacts/execution_2"),
                new EnvironmentVariable.PassThrough("ANTHROPIC_API_KEY"),
            ],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId> { [new StepId("architect")] = new ExecutionId("exec-0") });

        // A step-less supplementary execution: StepId and Timeout are both null.
        var stepLessRequest = new ExecutionRequest(
            new ExecutionId("exec-supplement"),
            new WorkflowId("wf-1"),
            StepId: null,
            "human",
            Inputs: [],
            Outputs: ["revision.md"],
            Timeout: null,
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        var auditedRequest = new ExecutionRequest(
            new ExecutionId("exec-audited"),
            new WorkflowId("wf-1"),
            StepId,
            "gemini",
            Inputs: ["/artifacts/execution_1/goal.md"],
            Outputs: ["/artifacts/execution_2/report.md"],
            Timeout: TimeSpan.FromMinutes(5),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            GrantAuditMode: GrantAuditMode.AuditedNotEnforced);


        yield return [new FlowEvent.ExecutionRequestAccepted(request)];
        yield return [new FlowEvent.ExecutionRequestAccepted(stepLessRequest)];
        yield return [new FlowEvent.ExecutionRequestAccepted(auditedRequest)];
        yield return [new FlowEvent.ExecutionRequestRejected(ExecutionId, "concurrency cap reached")];
        // #1373 follow-up
        yield return [new FlowEvent.ExecutionAttemptStarted(ExecutionId, "a1b2c3d4e5f6")];

        yield return [new FlowEvent.ExecutionSucceeded(ExecutionId)];
        // #1709: the peak reaches ExecutionSucceeded/ExecutionFailed too now, not only ExecutionArrested.
        yield return [new FlowEvent.ExecutionSucceeded(ExecutionId, PeakBilledInWindow: 344_225)];
        yield return [new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification.Retryable)];
        yield return [new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification.Retryable, PeakBilledInWindow: 228_536)];
        yield return [new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification: null)];
        yield return [new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification.Retryable, "Worker process exited with code 1")];
        yield return [new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification: null, "Missing required output file 'plan.md'")];
        // #1594, conductor-writes shape: the captured-response fact -- pins that CapturedResponseFile
        // and UnsatisfiedOutputNames actually round-trip through flow.jsonl, not just through memory.
        yield return
        [
            new FlowEvent.ExecutionFailed(
                ExecutionId, FailureClassification.Permanent,
                "Contract not satisfied: 'advice.md' is missing. Response captured to '.captured-response.md'; awaiting conductor resolution.",
                RetryNotBefore: null,
                CapturedResponseFile: ".captured-response.md",
                UnsatisfiedOutputNames: ["advice.md"])
        ];
        yield return [new FlowEvent.ExecutionCancelled(ExecutionId)];
        yield return [new FlowEvent.CancellationRequested(ExecutionId)];
        // #1762: the two Origin values, so a shape drift on either is caught here too.
        yield return [new FlowEvent.CancellationRequested(ExecutionId, CancellationOrigin.Operator)];
        yield return [new FlowEvent.CancellationRequested(ExecutionId, CancellationOrigin.HostStop)];
        yield return [new FlowEvent.WorkflowPaused(ExecutionId, StepId)];
        yield return
        [
            new FlowEvent.ExternalDecisionRecorded(
                new DecisionId("decision-1"),
                ExecutionId,
                DecisionType.Supersede,
                new StepId("architect"),
                new ExecutionId("exec-9"))
        ];
        yield return [new FlowEvent.WorkflowResumed(new DecisionId("decision-1"))];
        // #1586 S1
        yield return [new FlowEvent.StepRetryForeclosed(StepId, ExecutionId, "dead pump, unfireable park")];
        yield return [new FlowEvent.StepRetryForeclosed(StepId, ExecutionId, "dead pump, unfireable park", ForeclosedBy: "settle")];
        yield return [new FlowEvent.ZeroOutputsDespiteSubstantialWork(ExecutionId, "4 turns, 500 output tokens")];

        // #1623
        yield return [new FlowEvent.VerifyStarted(ExecutionId)];
        yield return [new FlowEvent.VerifyPassed(ExecutionId)];
        yield return [new FlowEvent.VerifyFailed(ExecutionId)];
        yield return [new FlowEvent.VerifyFailed(ExecutionId, ["fmt-check", "lint"], "GATES: FAIL 2 of 25 -- fmt-check, lint")];
        yield return [new FlowEvent.VerifyFailed(ExecutionId, null, "timed out", VerifyFailedKind.TimedOut)];
        yield return [new FlowEvent.VerifyFailed(ExecutionId, null, "cancelled", VerifyFailedKind.Cancelled)];
        yield return [new FlowEvent.VerifyFailed(ExecutionId, null, "restart", VerifyFailedKind.EngineRestart)];
        // #1788
        yield return [new FlowEvent.VerifyFailed(ExecutionId, ["branch-on-origin"], "1788-lane is 1 commit ahead of origin", VerifyFailedKind.DeliveryFailed)];
        // #1702
        yield return [new FlowEvent.VerifyNotRun(ExecutionId, "task absent: gates-quiet")];
        // #1708 H1 -- both digests present, and the "nothing committed" shape that carries a null.
        yield return [new FlowEvent.VerifyDeclarationIgnored(ExecutionId, "0f2b", "9ac1")];
        yield return [new FlowEvent.VerifyDeclarationIgnored(ExecutionId, null, "9ac1")];
        // #1708 M1 -- the HEAD fallback's announcement, and the null-digest shape.
        yield return [new FlowEvent.VerifyDeclarationUnreviewed(ExecutionId, "0f2b")];
        yield return [new FlowEvent.VerifyDeclarationUnreviewed(ExecutionId, null)];
        // #1929 review MEDIUM -- the placement record, with and without adapter group labels, and
        // (round 3) the no-digest shape a journal line predating the digest deserializes to.
        yield return
        [
            new FlowEvent.EngineFilesPlaced(
                ExecutionId,
                [new EnginePlacedFile(@"C:\repo\.claude\skills\audit-tool\SKILL.md", "9f2b1c")],
                ["audit-tool"])
        ];
        yield return
        [
            new FlowEvent.EngineFilesPlaced(
                ExecutionId, [new EnginePlacedFile(@"C:\repo\.claude\skills\a\SKILL.md", null)], [])
        ];
        yield return [new FlowEvent.EngineFilesPlaced(ExecutionId, null, [])];
        yield return [new FlowEvent.ExecutionArrested(ExecutionId)];
        yield return
        [
            new FlowEvent.ExecutionArrested(
                ExecutionId,
                new WorkerUsage(TokensIn: 500_000, TokensOut: 120_000),
                LastToolNames: ["manage_task", "manage_task", "run_command"])
        ];

        // #1682: the two new fields -- both reasons, so a shape drift on either is caught here too.
        yield return
        [
            new FlowEvent.ExecutionArrested(
                ExecutionId,
                new WorkerUsage(TokensIn: 500_000, TokensOut: 120_000, BilledTokens: 620_000),
                LastToolNames: ["run_command"],
                Reason: ArrestReason.TokenBudget,
                ToolStepCount: 55)
        ];
        yield return
        [
            new FlowEvent.ExecutionArrested(
                ExecutionId,
                Usage: null,
                LastToolNames: ["run_command", "run_command"],
                Reason: ArrestReason.ToolStepCap,
                ToolStepCount: 81)
        ];
        // #1691: the third reason plus the two fields it brought. PeakBilledInWindow is carried on
        // EVERY arrest, so the TokenBudget/ToolStepCap variants above deliberately leave it null --
        // that is the pre-#1691 ledger shape, and it must keep round-tripping as absent.
        yield return
        [
            new FlowEvent.ExecutionArrested(
                ExecutionId,
                new WorkerUsage(TokensIn: 96_546, TokensOut: 3_679, BilledTokens: 278_565),
                LastToolNames: ["run_command"],
                Reason: ArrestReason.BilledRate,
                ToolStepCount: 26,
                PeakBilledInWindow: 278_565,
                BilledRateLimit: 250_000)
        ];

        // #1583
        yield return [new FlowEvent.StepRebound(StepId, ExecutionId, "agy", "gemini-3-pro", "claude", "sonnet", "Vendor failover")];
        yield return [new FlowEvent.StepRebound(StepId, ExecutionId, "agy", null, "claude", null)];

        // #1549
        yield return [new FlowEvent.ExecutionProgress(ExecutionId)];
        yield return [new FlowEvent.CancellationDelivered(ExecutionId)];
        yield return [new FlowEvent.CancellationRejected(ExecutionId)];
        // #1530: Reason is the newest additive member on CancellationRejected.
        yield return [new FlowEvent.CancellationRejected(ExecutionId, "arrest requested but not yet confirmed settled")];

        // #734
        yield return [new FlowEvent.DeliveryPrOpened(123)];
        yield return [new FlowEvent.DeliveryPrOpened(123, "734-lane")];
        yield return [new FlowEvent.DeliveryChecksGreen(123)];
        yield return [new FlowEvent.DeliveryChecksRed(123)];
        yield return [new FlowEvent.DeliveryMerged(123, Merged: true)];
        yield return [new FlowEvent.DeliveryMerged(123, Merged: false)];
    }

    [Theory]
    [MemberData(nameof(AllEventVariants))]
    public void RoundTrips_through_the_FlowEvent_base_type_without_data_loss(FlowEvent original)
    {
        var json = JsonSerializer.Serialize(original, typeof(FlowEvent), FlowEventLogJson.Options);

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(json, FlowEventLogJson.Options);
        Assert.NotNull(deserialized);

        var reserialized = JsonSerializer.Serialize(deserialized, typeof(FlowEvent), FlowEventLogJson.Options);
        Assert.Equal(json, reserialized);
        Assert.Equal(original.GetType(), deserialized.GetType());
    }

    /// <summary>
    /// #1779 owner ruling: an unrecognized <c>eventType</c> is a newer writer, not corruption -- it
    /// deserializes to the internal <see cref="FlowEvent.UnknownFlowEvent"/> sentinel rather than
    /// throwing. Goes through <see cref="FlowEventLogJson.DeserializeLine"/> rather than a bare
    /// <c>JsonSerializer.Deserialize&lt;FlowEvent&gt;</c> call, because the tolerance lives at that
    /// line-level entry point (see its own remarks for why it isn't a <see cref="JsonConverter{T}"/>
    /// on <see cref="FlowEventLogJson.Options"/>). <see cref="Store.FlowEventLogReaderTests"/> covers
    /// the skip-and-count behaviour the sentinel exists to enable; this only pins the contract.
    /// </summary>
    [Fact]
    public void Deserializing_an_unknown_event_type_discriminator_does_not_throw()
    {
        const string json = """{"owner":"flow","Event":{"eventType":"somethingElse"}}""";

        var deserialized = FlowEventLogJson.DeserializeLine(json);

        var flowLogEntry = Assert.IsType<LogEntry.FlowLogEntry>(deserialized);
        var unknown = Assert.IsType<FlowEvent.UnknownFlowEvent>(flowLogEntry.Event);
        Assert.Equal("somethingElse", unknown.Kind);
    }

    /// <summary>
    /// Polarity control for the test above: a KNOWN kind with a lost/renamed required member must
    /// still throw -- "loud beats silent" is unchanged for that case, only for a genuinely unknown
    /// discriminator (#1779).
    /// </summary>
    [Fact]
    public void Deserializing_a_known_event_type_missing_a_required_member_still_throws()
    {
        const string json = """{"eventType":"executionFailed"}"""; // ExecutionId has no default -- required.

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<FlowEvent>(json, FlowEventLogJson.Options));
    }

    /// <summary>
    /// Produces the <c>flow.jsonl</c> line an AER build from before #597 would have written, by
    /// serializing a current event and deleting the <c>Reason</c> property from the wire form.
    /// </summary>
    /// <remarks>
    /// Deriving it beats hand-typing it. The first version of these two tests hand-wrote
    /// <c>{"eventType":"executionFailed","executionId":"exec-1",…}</c> in camelCase; the real
    /// serializer emits members in PascalCase and only the discriminator in camelCase, so every
    /// property silently missed and deserialized to its default. The tests failed for a reason that
    /// had nothing to do with what they were written to check. Derived this way, the fixture tracks
    /// the wire format automatically rather than being hand-typed.
    /// <para>
    /// It derives from the <i>default</i> serializer deliberately, not from
    /// <see cref="Baton.Store.FlowEventLogJson.Options"/>: the default emits the ordinal enum shape
    /// (<c>"FailureClassification":0</c>) a genuinely historical line carries, which is precisely what
    /// this fixture exists to reproduce. The read side uses the journal's real options, so the test
    /// still drives production's reader against a pre-#604 line. The earlier claim here — that the
    /// fixture "cannot drift away from the wire format again" — stopped being true when the two
    /// diverged in #604, and is not restated.
    /// </para>
    /// </remarks>
    private static string LegacyExecutionFailedJson(FailureClassification? classification)
    {
        var current = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionFailed(ExecutionId, classification, "some reason"),
            typeof(FlowEvent));

        var node = System.Text.Json.Nodes.JsonNode.Parse(current)!.AsObject();

        // Guards the derivation itself: if the property were ever renamed, Remove would return
        // false and this fixture would quietly become a *current* line, making the legacy test
        // pass while proving nothing.
        Assert.True(node.Remove(nameof(FlowEvent.ExecutionFailed.Reason)));

        return node.ToJsonString();
    }

    [Theory]
    [InlineData(FailureClassification.Retryable)]
    [InlineData(FailureClassification.Permanent)]
    [InlineData(null)]
    public void Deserializing_legacy_ExecutionFailed_without_Reason_property_deserializes_with_null_Reason(
        FailureClassification? classification)
    {
        // #597 added Reason as a trailing defaulted member specifically so lines already on disk
        // stay readable. A journal that stopped deserializing after an upgrade is unrecoverable
        // state, which is why this is asserted rather than assumed.
        var deserialized = JsonSerializer.Deserialize<FlowEvent>(
            LegacyExecutionFailedJson(classification), FlowEventLogJson.Options);

        var failed = Assert.IsType<FlowEvent.ExecutionFailed>(deserialized);
        Assert.Equal(ExecutionId, failed.ExecutionId);
        Assert.Equal(classification, failed.FailureClassification);
        Assert.Null(failed.Reason);
    }

    [Fact]
    public void Deserializing_current_ExecutionFailed_with_Reason_property_sets_Reason()
    {
        // The polarity control for the test above: same event shape, Reason present rather than
        // stripped. Without this arm, an implementation that never read Reason at all would pass
        // the legacy test — null is what it asserts.
        const string reason = "Missing required output file 'plan'";

        var currentJson = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification.Retryable, reason),
            typeof(FlowEvent));

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(currentJson, FlowEventLogJson.Options);

        var failed = Assert.IsType<FlowEvent.ExecutionFailed>(deserialized);
        Assert.Equal(reason, failed.Reason);
    }

    private static string LegacyCancellationRequestedJson()
    {
        // #1762: Origin is the newest additive member on CancellationRequested -- the same
        // durability claim ExecutionFailed.Reason's own legacy fixture above pins, mirrored for this
        // field. A line written before #1762 landed has no Origin property at all.
        var current = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.CancellationRequested(ExecutionId, CancellationOrigin.Operator),
            typeof(FlowEvent));

        var node = System.Text.Json.Nodes.JsonNode.Parse(current)!.AsObject();

        Assert.True(node.Remove(nameof(FlowEvent.CancellationRequested.Origin)));

        return node.ToJsonString();
    }

    [Fact]
    public void Deserializing_legacy_CancellationRequested_without_Origin_property_deserializes_with_null_Origin()
    {
        // A journal that stopped deserializing after this upgrade is unrecoverable state, which is
        // why this is asserted rather than assumed -- MutationInterface's ledger-read rule
        // (spec/baton.md §2) depends on a legacy line replaying with a null Origin, not throwing.
        var deserialized = JsonSerializer.Deserialize<FlowEvent>(
            LegacyCancellationRequestedJson(), FlowEventLogJson.Options);

        var cancellationRequested = Assert.IsType<FlowEvent.CancellationRequested>(deserialized);
        Assert.Equal(ExecutionId, cancellationRequested.ExecutionId);
        Assert.Null(cancellationRequested.Origin);
    }

    [Fact]
    public void Deserializing_current_CancellationRequested_with_Origin_property_sets_Origin()
    {
        // The polarity control for the test above: same event shape, Origin present rather than
        // stripped. Without this arm, an implementation that never read Origin at all would pass the
        // legacy test -- null is what it asserts.
        var currentJson = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.CancellationRequested(ExecutionId, CancellationOrigin.HostStop),
            typeof(FlowEvent),
            FlowEventLogJson.Options);

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(currentJson, FlowEventLogJson.Options);

        var cancellationRequested = Assert.IsType<FlowEvent.CancellationRequested>(deserialized);
        Assert.Equal(CancellationOrigin.HostStop, cancellationRequested.Origin);
    }

    private static string LegacyCancellationRejectedJson()
    {
        // #1530: Reason is the newest additive member on CancellationRejected -- same durability
        // claim as LegacyCancellationRequestedJson's own Origin fixture above, mirrored for this field.
        var current = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.CancellationRejected(ExecutionId, "some reason"),
            typeof(FlowEvent));

        var node = System.Text.Json.Nodes.JsonNode.Parse(current)!.AsObject();

        Assert.True(node.Remove(nameof(FlowEvent.CancellationRejected.Reason)));

        return node.ToJsonString();
    }

    [Fact]
    public void Deserializing_legacy_CancellationRejected_without_Reason_property_deserializes_with_null_Reason()
    {
        var deserialized = JsonSerializer.Deserialize<FlowEvent>(
            LegacyCancellationRejectedJson(), FlowEventLogJson.Options);

        var cancellationRejected = Assert.IsType<FlowEvent.CancellationRejected>(deserialized);
        Assert.Equal(ExecutionId, cancellationRejected.ExecutionId);
        Assert.Null(cancellationRejected.Reason);
    }

    [Fact]
    public void Deserializing_current_CancellationRejected_with_Reason_property_sets_Reason()
    {
        // The polarity control for the test above: same event shape, Reason present rather than
        // stripped.
        var currentJson = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.CancellationRejected(ExecutionId, "arrest requested but not yet confirmed settled"),
            typeof(FlowEvent),
            FlowEventLogJson.Options);

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(currentJson, FlowEventLogJson.Options);

        var cancellationRejected = Assert.IsType<FlowEvent.CancellationRejected>(deserialized);
        Assert.Equal("arrest requested but not yet confirmed settled", cancellationRejected.Reason);
    }

    /// <summary>
    /// #1709: a <c>flow.jsonl</c> line written before this issue's <c>PeakBilledInWindow</c> field
    /// existed, derived the same way <see cref="LegacyExecutionFailedJson"/> derives its own fixture —
    /// see that method's remarks for why deriving beats hand-typing.
    /// </summary>
    private static string LegacyExecutionSucceededJsonWithoutPeakBilledInWindow()
    {
        var current = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionSucceeded(ExecutionId, PeakBilledInWindow: 500_000),
            typeof(FlowEvent));

        var node = System.Text.Json.Nodes.JsonNode.Parse(current)!.AsObject();

        Assert.True(node.Remove(nameof(FlowEvent.ExecutionSucceeded.PeakBilledInWindow)));

        return node.ToJsonString();
    }

    [Fact]
    public void Deserializing_legacy_ExecutionSucceeded_without_PeakBilledInWindow_property_deserializes_with_null_PeakBilledInWindow()
    {
        // #1709 added PeakBilledInWindow as a trailing defaulted member specifically so lines already
        // on disk stay readable -- the same "= null default is load-bearing" rule FlowEvent.cs's own
        // remarks state, pinned here the same way #597's Reason field already is above.
        var deserialized = JsonSerializer.Deserialize<FlowEvent>(
            LegacyExecutionSucceededJsonWithoutPeakBilledInWindow(), FlowEventLogJson.Options);

        var succeeded = Assert.IsType<FlowEvent.ExecutionSucceeded>(deserialized);
        Assert.Equal(ExecutionId, succeeded.ExecutionId);
        Assert.Null(succeeded.PeakBilledInWindow);
    }

    [Fact]
    public void Deserializing_current_ExecutionSucceeded_with_PeakBilledInWindow_property_sets_PeakBilledInWindow()
    {
        // The polarity control for the test above: same event shape, PeakBilledInWindow present rather
        // than stripped. Without this arm, an implementation that never read the property at all would
        // pass the legacy test -- null is what it asserts.
        var currentJson = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionSucceeded(ExecutionId, PeakBilledInWindow: 500_000),
            typeof(FlowEvent));

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(currentJson, FlowEventLogJson.Options);

        var succeeded = Assert.IsType<FlowEvent.ExecutionSucceeded>(deserialized);
        Assert.Equal(500_000, succeeded.PeakBilledInWindow);
    }

    /// <summary>#1709: ExecutionFailed's own PeakBilledInWindow legacy fixture, same shape as ExecutionSucceeded's above.</summary>
    private static string LegacyExecutionFailedJsonWithoutPeakBilledInWindow()
    {
        var current = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification.Retryable, PeakBilledInWindow: 500_000),
            typeof(FlowEvent));

        var node = System.Text.Json.Nodes.JsonNode.Parse(current)!.AsObject();

        Assert.True(node.Remove(nameof(FlowEvent.ExecutionFailed.PeakBilledInWindow)));

        return node.ToJsonString();
    }

    [Fact]
    public void Deserializing_legacy_ExecutionFailed_without_PeakBilledInWindow_property_deserializes_with_null_PeakBilledInWindow()
    {
        var deserialized = JsonSerializer.Deserialize<FlowEvent>(
            LegacyExecutionFailedJsonWithoutPeakBilledInWindow(), FlowEventLogJson.Options);

        var failed = Assert.IsType<FlowEvent.ExecutionFailed>(deserialized);
        Assert.Equal(ExecutionId, failed.ExecutionId);
        Assert.Null(failed.PeakBilledInWindow);
    }

    [Fact]
    public void Deserializing_current_ExecutionFailed_with_PeakBilledInWindow_property_sets_PeakBilledInWindow()
    {
        // The polarity control for the test above -- see the ExecutionSucceeded pair's own remarks.
        var currentJson = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification.Retryable, PeakBilledInWindow: 500_000),
            typeof(FlowEvent));

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(currentJson, FlowEventLogJson.Options);

        var failed = Assert.IsType<FlowEvent.ExecutionFailed>(deserialized);
        Assert.Equal(500_000, failed.PeakBilledInWindow);
    }

    private static string LegacyExecutionRequestAcceptedJson()
    {
        var request = new ExecutionRequest(
            ExecutionId,
            new WorkflowId("wf-1"),
            StepId,
            "gemini",
            Inputs: ["/artifacts/execution_1/goal.md"],
            Outputs: ["/artifacts/execution_2/report.md"],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            GrantAuditMode: GrantAuditMode.AuditedNotEnforced);

        var current = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionRequestAccepted(request),
            typeof(FlowEvent),
            FlowEventLogJson.Options);

        var node = System.Text.Json.Nodes.JsonNode.Parse(current)!.AsObject();
        var requestNode = node["Request"]!.AsObject();

        Assert.True(requestNode.Remove(nameof(ExecutionRequest.GrantAuditMode)));

        return node.ToJsonString();
    }

    [Fact]
    public void Deserializing_legacy_ExecutionRequestAccepted_without_GrantAuditMode_deserializes_with_null_mode()
    {
        var deserialized = JsonSerializer.Deserialize<FlowEvent>(
            LegacyExecutionRequestAcceptedJson(), FlowEventLogJson.Options);

        var accepted = Assert.IsType<FlowEvent.ExecutionRequestAccepted>(deserialized);
        Assert.Equal(ExecutionId, accepted.Request.ExecutionId);
        Assert.Null(accepted.Request.GrantAuditMode);
    }

    [Fact]
    public void Deserializing_current_ExecutionRequestAccepted_with_GrantAuditMode_sets_mode()
    {
        var request = new ExecutionRequest(
            ExecutionId,
            new WorkflowId("wf-1"),
            StepId,
            "gemini",
            Inputs: ["/artifacts/execution_1/goal.md"],
            Outputs: ["/artifacts/execution_2/report.md"],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            GrantAuditMode: GrantAuditMode.AuditedNotEnforced);

        var currentJson = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionRequestAccepted(request),
            typeof(FlowEvent),
            FlowEventLogJson.Options);

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(currentJson, FlowEventLogJson.Options);

        var accepted = Assert.IsType<FlowEvent.ExecutionRequestAccepted>(deserialized);
        Assert.Equal(GrantAuditMode.AuditedNotEnforced, accepted.Request.GrantAuditMode);
    }

    private static string LegacyExecutionRequestAcceptedJsonWithNoRecordedAdapter()
    {
        // Issue #1567: Adapter/Model are the newest additive members on ExecutionRequest -- the same
        // durability claim GrantAuditMode's own legacy fixture above pins, mirrored for the field this
        // PR adds. A line written before #1567 landed has neither property at all.
        var request = new ExecutionRequest(
            ExecutionId,
            new WorkflowId("wf-1"),
            StepId,
            "claude",
            Inputs: ["/artifacts/execution_1/goal.md"],
            Outputs: ["/artifacts/execution_2/report.md"],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            Adapter: "claude",
            Model: "sonnet");

        var current = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionRequestAccepted(request),
            typeof(FlowEvent),
            FlowEventLogJson.Options);

        var node = System.Text.Json.Nodes.JsonNode.Parse(current)!.AsObject();
        var requestNode = node["Request"]!.AsObject();

        Assert.True(requestNode.Remove(nameof(ExecutionRequest.Adapter)));
        Assert.True(requestNode.Remove(nameof(ExecutionRequest.Model)));

        return node.ToJsonString();
    }

    [Fact]
    public void Deserializing_legacy_ExecutionRequestAccepted_without_Adapter_or_Model_deserializes_with_both_null()
    {
        var deserialized = JsonSerializer.Deserialize<FlowEvent>(
            LegacyExecutionRequestAcceptedJsonWithNoRecordedAdapter(), FlowEventLogJson.Options);

        var accepted = Assert.IsType<FlowEvent.ExecutionRequestAccepted>(deserialized);
        Assert.Equal(ExecutionId, accepted.Request.ExecutionId);
        Assert.Null(accepted.Request.Adapter);
        Assert.Null(accepted.Request.Model);
    }

    [Fact]
    public void Deserializing_current_ExecutionRequestAccepted_with_Adapter_and_Model_sets_both()
    {
        // The polarity control for the test above: same event shape, Adapter/Model present rather
        // than stripped. Without this arm, an implementation that never read either property at all
        // would pass the legacy test -- null is what it asserts.
        var request = new ExecutionRequest(
            ExecutionId,
            new WorkflowId("wf-1"),
            StepId,
            "claude",
            Inputs: ["/artifacts/execution_1/goal.md"],
            Outputs: ["/artifacts/execution_2/report.md"],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            Adapter: "agy",
            Model: "gemini-3-pro");

        var currentJson = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionRequestAccepted(request),
            typeof(FlowEvent),
            FlowEventLogJson.Options);

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(currentJson, FlowEventLogJson.Options);

        var accepted = Assert.IsType<FlowEvent.ExecutionRequestAccepted>(deserialized);
        Assert.Equal("agy", accepted.Request.Adapter);
        Assert.Equal("gemini-3-pro", accepted.Request.Model);
    }

    private static string LegacyExecutionRequestAcceptedJsonWithNoRecordedHookCanary()
    {
        // #1741 (spec/baton.md §9): HookCanaryArmed/HookVerdictLedgerFileName are the newest
        // additive members on ExecutionRequest -- the same durability claim GrantAuditMode's and
        // Adapter/Model's own legacy fixtures above pin, mirrored for this pair. A line written
        // before #1741 landed has neither property at all.
        var request = new ExecutionRequest(
            ExecutionId,
            new WorkflowId("wf-1"),
            StepId,
            "agy",
            Inputs: ["/artifacts/execution_1/goal.md"],
            Outputs: ["/artifacts/execution_2/report.md"],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            Adapter: "agy",
            HookCanaryArmed: true,
            HookVerdictLedgerFileName: "verdicts.ndjson");

        var current = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionRequestAccepted(request),
            typeof(FlowEvent),
            FlowEventLogJson.Options);

        var node = System.Text.Json.Nodes.JsonNode.Parse(current)!.AsObject();
        var requestNode = node["Request"]!.AsObject();

        Assert.True(requestNode.Remove(nameof(ExecutionRequest.HookCanaryArmed)));
        Assert.True(requestNode.Remove(nameof(ExecutionRequest.HookVerdictLedgerFileName)));

        return node.ToJsonString();
    }

    [Fact]
    public void Deserializing_legacy_ExecutionRequestAccepted_without_HookCanaryArmed_or_HookVerdictLedgerFileName_deserializes_with_both_null()
    {
        var deserialized = JsonSerializer.Deserialize<FlowEvent>(
            LegacyExecutionRequestAcceptedJsonWithNoRecordedHookCanary(), FlowEventLogJson.Options);

        var accepted = Assert.IsType<FlowEvent.ExecutionRequestAccepted>(deserialized);
        Assert.Equal(ExecutionId, accepted.Request.ExecutionId);
        Assert.Null(accepted.Request.HookCanaryArmed);
        Assert.Null(accepted.Request.HookVerdictLedgerFileName);
    }

    [Fact]
    public void Deserializing_current_ExecutionRequestAccepted_with_HookCanaryArmed_and_HookVerdictLedgerFileName_sets_both()
    {
        // The polarity control for the test above: same event shape, both fields present rather
        // than stripped. Without this arm, an implementation that never read either property at all
        // would pass the legacy test -- null is what it asserts.
        var request = new ExecutionRequest(
            ExecutionId,
            new WorkflowId("wf-1"),
            StepId,
            "agy",
            Inputs: ["/artifacts/execution_1/goal.md"],
            Outputs: ["/artifacts/execution_2/report.md"],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            Adapter: "agy",
            HookCanaryArmed: true,
            HookVerdictLedgerFileName: "verdicts.ndjson");

        var currentJson = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionRequestAccepted(request),
            typeof(FlowEvent),
            FlowEventLogJson.Options);

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(currentJson, FlowEventLogJson.Options);

        var accepted = Assert.IsType<FlowEvent.ExecutionRequestAccepted>(deserialized);
        Assert.True(accepted.Request.HookCanaryArmed);
        Assert.Equal("verdicts.ndjson", accepted.Request.HookVerdictLedgerFileName);
    }
}


