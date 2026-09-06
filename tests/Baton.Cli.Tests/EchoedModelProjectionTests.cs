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
    [Fact]
    public void Claude_result_events_model_is_recorded_as_the_echoed_model()
    {
        var view = ProjectSingle(
            "claude",
            """{"type":"assistant","message":{"model":"claude-opus-4-6-20260115","content":[]}}""",
            """{"type":"result","model":"claude-opus-4-6-20260115","num_turns":4,"usage":{"input_tokens":7,"output_tokens":3}}""");

        Assert.Equal("claude-opus-4-6-20260115", view.ModelEchoed);
        // The control the arm rests on: the usage read still works, so a green ModelEchoed cannot be
        // the projector having silently skipped this stream.
        Assert.Equal(7, view.TokensIn);
    }

    [Fact]
    public void Claude_system_init_is_not_read_as_an_echo_because_it_only_repeats_the_request()
    {
        // The polarity arm for the one discrimination this feature exists on. `system:init` echoes the
        // --model string VERBATIM even for an id that then fails to run (docs/vendor-doc-audit.md §5),
        // so a reader that accepted it would report a model that never ran -- the exact substitution
        // this field is supposed to expose. Same stream shape as the arm above minus the two events
        // that DO carry a resolution: the field must be absent, not "claude-bogus-nonexistent-zzz".
        var view = ProjectSingle(
            "claude",
            """{"type":"system","subtype":"init","session_id":"s-1","model":"claude-bogus-nonexistent-zzz"}""",
            """{"type":"result","num_turns":1,"usage":{"input_tokens":2,"output_tokens":1}}""");

        Assert.Null(view.ModelEchoed);
        Assert.Equal(2, view.TokensIn);
    }

    [Fact]
    public void Codex_turn_completed_model_outranks_the_threads_opening_claim()
    {
        // Both events are read, and the LAST one wins -- a substitution announced on the terminal event
        // is the reading worth having, so it must not be shadowed by what the thread opened with.
        var view = ProjectSingle(
            "codex",
            """{"type":"thread.started","thread_id":"t-1","model":"gpt-6-astra"}""",
            """{"type":"turn.completed","model":"gpt-5.6-luna","usage":{"input_tokens":10,"cached_input_tokens":4,"output_tokens":6}}""");

        Assert.Equal("gpt-5.6-luna", view.ModelEchoed);
        Assert.Equal(6, view.TokensIn);
    }

    [Fact]
    public void Agy_streams_leave_the_echoed_model_absent_from_the_serialized_view_rather_than_blank()
    {
        // agy's stream carries no `model` key on any event (measured, #1927), so this vendor has
        // nothing to echo. Asserted on the SERIALIZED shape rather than only on the property: the
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
