using System.Text.Json;
using Baton.Artifacts;
using Baton.Cli.Tests.TestSupport;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1927: the settle-time read of the model a vendor CLI reported having RUN, end to end through
/// <see cref="ExecutionUsageProjector.BuildByExecutionId"/> — the same seam production goes through,
/// including the adapter registry, which is what makes these arms discriminating. A test constructing
/// <c>ClaudeUsageParser</c> directly would pass whether or not the projector reaches an optional
/// interface method at all: the vendor ADAPTERS delegate only <c>TryParseFinalUsage</c>, so an
/// implementation wired to the resolved adapter rather than to
/// <c>StandardWorkerUsageParsers.Default</c> silently takes the interface's null on every real
/// execution.
/// </summary>
public sealed class EchoedModelProjectionTests
{
    /// <summary>
    /// The MEASURED claude arm: <c>docs/vendor-doc-audit.md</c> §5 records that the assistant turn
    /// carries the CLI's own resolution in <c>message.model</c> (it is where the <c>&lt;synthetic&gt;</c>
    /// answer to a bogus id was observed). This is the shape the field actually rests on.
    /// </summary>
    [Fact]
    public void Claude_assistant_turns_model_is_recorded_as_the_echoed_model()
    {
        var view = ProjectSingle(
            "claude",
            """{"type":"assistant","message":{"model":"claude-opus-4-6-20260115","content":[]}}""",
            """{"type":"result","num_turns":4,"usage":{"input_tokens":7,"output_tokens":3}}""");

        Assert.Equal("claude-opus-4-6-20260115", view.ModelEchoed);
        // The control the arm rests on: the usage read still works, so a green ModelEchoed cannot be
        // the projector having silently skipped this stream.
        Assert.Equal(7, view.TokensIn);
    }

    /// <summary>
    /// The scan direction, pinned on two MEASURED events (#1927 review): the projector keeps the LAST
    /// line naming a model, so a mid-execution substitution outranks the opening turn's answer. This
    /// arm replaces a codex one that pinned the same direction on a shape nothing emits —
    /// <see cref="CodexUsageParser"/>'s own doc has why.
    /// </summary>
    [Fact]
    public void The_last_event_naming_a_model_outranks_an_earlier_one()
    {
        var view = ProjectSingle(
            "claude",
            """{"type":"assistant","message":{"model":"claude-opus-4-6-20260115","content":[]}}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-4-6-20260115","content":[]}}""",
            """{"type":"result","num_turns":2,"usage":{"input_tokens":5,"output_tokens":2}}""");

        Assert.Equal("claude-sonnet-4-6-20260115", view.ModelEchoed);
        Assert.Equal(5, view.TokensIn);
    }

    /// <summary>
    /// The terminal <c>result</c> event's own top-level <c>model</c> is read FIRST when present, and
    /// that rung is <b>unmeasured</b> — <c>ClaudeUsageParser.TryParseEchoedModel</c>'s own doc names the
    /// captured file and says why it settles nothing. The line below is therefore hand-written to a
    /// shape no in-tree capture confirms; it is kept because claude answers through the measured
    /// <c>assistant</c> fallback either way, so the rung costs nothing if the vendor never populates it.
    /// </summary>
    [Fact]
    public void A_result_events_own_model_is_read_when_the_vendor_does_supply_one()
    {
        var view = ProjectSingle(
            "claude",
            """{"type":"assistant","message":{"model":"claude-opus-4-6-20260115","content":[]}}""",
            """{"type":"result","model":"claude-haiku-4-5-20251001","num_turns":4,"usage":{"input_tokens":7,"output_tokens":3}}""");

        Assert.Equal("claude-haiku-4-5-20251001", view.ModelEchoed);
        Assert.Equal(7, view.TokensIn);
    }

    [Fact]
    public void Claude_system_init_is_not_read_as_an_echo_because_it_only_repeats_the_request()
    {
        // The polarity arm for the one discrimination this feature exists on --
        // ClaudeUsageParser.TryParseEchoedModel states why init is refused. Same stream shape as the
        // arm above minus the two events that DO carry a resolution, and the id here is a bogus one:
        // the field must come back absent, never "claude-bogus-nonexistent-zzz".
        var view = ProjectSingle(
            "claude",
            """{"type":"system","subtype":"init","session_id":"s-1","model":"claude-bogus-nonexistent-zzz"}""",
            """{"type":"result","num_turns":1,"usage":{"input_tokens":2,"output_tokens":1}}""");

        Assert.Null(view.ModelEchoed);
        Assert.Equal(2, view.TokensIn);
    }

    /// <summary>
    /// #1927 review HIGH. Codex leaves the field absent, and the stream here is the REAL one this
    /// vendor produces — both lines copied from what <c>CodexAppServerBroker</c> itself writes, which
    /// is the whole finding: Baton synthesizes both lifecycle events, so there is no vendor echo to
    /// read. A parser reading them back would be reading Baton's own keys.
    /// </summary>
    [Fact]
    public void Codex_streams_leave_the_echoed_model_absent_because_baton_synthesizes_both_lifecycle_events()
    {
        var view = ProjectSingle(
            "codex",
            """{"type":"thread.started","thread_id":"t-1"}""",
            """{"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":4,"output_tokens":6}}""");

        Assert.Null(view.ModelEchoed);
        // The control: the usage read off the same two lines still works, so the absence above is the
        // field having no source rather than the projector skipping this stream.
        Assert.Equal(6, view.TokensIn);
    }

    [Fact]
    public void Agy_streams_leave_the_echoed_model_absent_from_the_serialized_view_rather_than_blank()
    {
        // This vendor has nothing to echo -- AgyUsageParser's own doc has the measurement. Asserted on
        // the SERIALIZED shape rather than only on the property: the
        // acceptance wording is "absent, not blank", and only the wire form discriminates an omitted
        // key from an empty string a consumer would render as a model.
        var view = ProjectSingle(
            "agy",
            """{"event":"step_update","step_update":{"state":"DONE","tool_name":"read_file"}}""",
            """{"event":"result","result":{"num_turns":2,"usage":{"input_tokens":9,"output_tokens":4}}}""");

        Assert.Null(view.ModelEchoed);
        Assert.Equal(9, view.TokensIn);

        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain("modelEchoed", json, StringComparison.Ordinal);
        // The control: a field that IS present serializes, so the assertion above is about absence
        // rather than about this serializer emitting nothing at all.
        Assert.Contains("tokensIn", json, StringComparison.Ordinal);
    }

    private static ExecutionUsageView ProjectSingle(string adapter, params string[] streamLines)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"echoed-model-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId($"exec-{adapter}");
            var start = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
            WriteBindings(testRoot, "worker", adapter);

            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "worker"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(2)),
            };

            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(
                Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName),
                string.Join('\n', streamLines) + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(
                entries, testRoot, WorkerAdapterRegistry.Default, testRoot);
            return Assert.Single(usage).Value;
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static ExecutionRequest AcceptedRequest(ExecutionId executionId, string worker) => new(
        executionId,
        new WorkflowId("wf-echoed-model"),
        new StepId(worker),
        worker,
        Inputs: [],
        Outputs: [],
        Timeout: TimeSpan.FromSeconds(30),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static void WriteBindings(string roomDirectoryPath, string workerName, string adapter)
    {
        Directory.CreateDirectory(roomDirectoryPath);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            [workerName] = new(
                adapter, new WorkerContract(workerName, [], [], []), "unused prompt", TimeSpan.FromSeconds(30)),
        };

        File.WriteAllText(
            BatonPaths.RoomBindingsFile(roomDirectoryPath),
            JsonSerializer.Serialize(config));
    }
}
