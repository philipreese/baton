using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Vendors.Tests;

/// <summary>
/// #2020, over the whole seam the defect lived in: app-server notifications → the broker's captured
/// <c>.stdout.log</c> → the figures <c>baton status</c> reports for that execution. A parser fixture
/// alone cannot answer this — the review's HIGH finding was precisely that the fold was correct while
/// the emitter never produced more than one line for it to fold, so the only instrument that
/// discriminates is one that starts at the notifications.
/// <para>
/// <b>On the notification shape.</b> No captured room under <c>~/.baton/rooms/</c> holds a RAW
/// app-server notification: the room capture is the broker's own OUTPUT, and the only in-tree
/// recording of the input side is <see cref="CodexAppServerBrokerTests"/>'s transcript, whose
/// <c>thread/tokenUsage/updated</c> payload this file reuses key for key. The FIGURES are scrubbed
/// from the real capture instead: the third round-trip below is
/// <c>tests/Baton.Cli.Tests/Fixtures/codex-live-stream.jsonl</c>'s terminal line verbatim (689 output
/// against 127,806 input over a 126,720 cached prefix), which is what makes the pre-fix answer
/// recognisable in the assertions.
/// </para>
/// </summary>
public sealed class CodexBrokerRoomUsageTests
{
    // The three round-trips, in the app-server's own camelCase keys. Cached input grows the way a real
    // conversation's does; the last row is the real capture's.
    private static readonly (int Input, int Cached, int CacheWrite, int Output, int Reasoning)[] RoundTrips =
    [
        (40_000, 38_000, 500, 300, 64),
        (90_000, 88_000, 700, 450, 128),
        (127_806, 126_720, 900, 689, 256),
    ];

    [Fact]
    public async Task A_three_round_trip_codex_turn_reports_every_round_trip_in_the_rooms_usage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-codex-room-usage-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var brokerOutput = Path.Combine(root, "broker-output");
        var roomRoot = Path.Combine(root, "room");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(brokerOutput);
        try
        {
            var emitted = await RunBrokerAsync(workspace, brokerOutput);

            // The emitter's own half of the claim: one usage line per notification, plus the terminal
            // turn.completed. Before this change the broker emitted the terminal line alone.
            Assert.Equal(
                RoundTrips.Length,
                emitted.Count(line => line.Contains($"\"type\":\"{CodexUsageParser.TurnUsageEventType}\"", StringComparison.Ordinal)));

            var view = ProjectRoomUsage(roomRoot, emitted);

            // Sums over all three round-trips. The pre-fix room reported the LAST one alone
            // (1,086 / 689 / 1 turn), so every figure here discriminates.
            Assert.Equal(2_000 + 2_000 + 1_086, view.TokensIn);
            Assert.Equal(300 + 450 + 689, view.TokensOut);
            Assert.Equal(3, view.Turns);
            Assert.Equal(38_000 + 88_000 + 126_720, view.CacheReadTokens);
            Assert.Equal(500 + 700 + 900, view.CacheCreationTokens);
            Assert.Equal(64 + 128 + 256, view.ThinkingTokens);

            // The no-double-count check, over the real TokenBudgetMonitor replayed on these same
            // lines: the terminal turn.completed restates round-trip 3, and if its output were
            // accumulated a second time the live Σ would exceed the terminal Σ by 689.
            Assert.Equal(5_086 + 1_439 + 2_100, view.BilledTokens);
            Assert.Equal(view.BilledTokens, view.LiveBilledTokens);
            Assert.Equal(0, view.BilledUnderReadTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// The control arm for the test above, and the one that makes its numbers mean something: the SAME
    /// projection over a stream captured before this emitter landed (a terminal <c>turn.completed</c>
    /// and nothing else) still reports that line's figures rather than regressing to absent. Without
    /// it, a fold that simply ignored the terminal line would pass the test above unnoticed while
    /// blanking every historical codex room.
    /// <para>
    /// <b><see cref="ExecutionUsageView.LiveBilledTokens"/> is the assertion this arm turns on</b>, and
    /// it is a different reader from the four settle-time figures beneath it: those come from
    /// <see cref="CodexUsageParser.ParseExecutionUsage"/>, which already folded a legacy stream's
    /// terminal line correctly, so they cannot tell a live-side regression from a healthy one. The live
    /// figure is the REAL <c>Mutation.TokenBudgetMonitor</c> replayed over these same captured bytes
    /// (<c>ExecutionUsageView</c>'s replay site), reading through
    /// <see cref="CodexUsageParser.TryParseIncrementalUsage"/> — so a version of that method matching
    /// <c>turn.usage</c> ALONE reports null here on every room captured before the emitter, which is
    /// precisely the regression the round-trip index exists to avoid paying for.
    /// </para>
    /// </summary>
    [Fact]
    public void A_stream_captured_before_the_per_round_trip_emitter_still_reports_its_terminal_line()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-codex-legacy-usage-{Guid.NewGuid():N}");
        try
        {
            var view = ProjectRoomUsage(root,
            [
                """{"type":"thread.started","thread_id":"thread-1"}""",
                """{"type":"turn.completed","usage":{"input_tokens":127806,"cached_input_tokens":126720,"cache_write_input_tokens":900,"output_tokens":689,"reasoning_output_tokens":256}}""",
            ]);

            Assert.Equal(1_086, view.TokensIn);
            Assert.Equal(689, view.TokensOut);
            Assert.Equal(1, view.Turns);
            Assert.Equal(126_720, view.CacheReadTokens);

            // The half the fold cannot answer -- see this method's own remark.
            Assert.Equal(1_086 + 689 + 900, view.LiveBilledTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// The settle-time half of the no-double-count claim the live monitor's half is asserted on above.
    /// <c>CodexAppServerBroker</c>'s class remark states what a current stream carries and why;
    /// <see cref="CodexUsageParser.ParseExecutionUsage"/> states why the fold partitions by line type
    /// where the monitor deduplicates by <see cref="CodexUsageParser.RoundTripField"/>. This is the arm
    /// for the fold's side of that agreement.
    /// </summary>
    [Fact]
    public void The_terminal_line_is_not_folded_a_second_time_on_a_stream_that_carries_both()
    {
        var usage = new CodexUsageParser().ParseExecutionUsage(
        [
            """{"type":"turn.usage","usage":{"input_tokens":100,"cached_input_tokens":60,"output_tokens":20,"round_trip":1}}""",
            """{"type":"turn.usage","usage":{"input_tokens":300,"cached_input_tokens":260,"output_tokens":30,"round_trip":2}}""",
            """{"type":"turn.completed","usage":{"input_tokens":300,"cached_input_tokens":260,"output_tokens":30,"round_trip":2}}""",
        ]);

        Assert.NotNull(usage);
        Assert.Equal(80, usage.TokensIn);
        Assert.Equal(50, usage.TokensOut);
        Assert.Equal(2, usage.Turns);
        Assert.Equal(320, usage.CacheReadTokens);

        // A total is not any one round-trip, so it carries no round-trip identity -- ParseExecutionUsage
        // states why that is dropped rather than inherited from the first reading folded.
        Assert.Null(usage.MessageId);
    }

    private static async Task<string[]> RunBrokerAsync(string workspace, string brokerOutput)
    {
        var grant = new PermissionGrant(ReadFiles: true);
        var configuration = new CodexBrokerConfiguration(
            workspace, "gpt-5.6-luna", "low", null, false, grant, ["report.md"], false);
        var policy = new CodexDynamicToolPolicy(grant, workspace, brokerOutput, [], ["report.md"]);

        var transcript = new List<string>
        {
            """{"id":1,"result":{"userAgent":"fixture"}}""",
            """{"id":2,"result":{"thread":{"id":"thread-1"}}}""",
            """{"id":3,"result":{"turn":{"id":"turn-1","status":"inProgress","items":[]}}}""",
        };
        var cumulativeOutput = 0;
        foreach (var (input, cached, cacheWrite, output, reasoning) in RoundTrips)
        {
            cumulativeOutput += output;
            // `total` is present because the real notification carries it, and deliberately NOT read:
            // see CodexAppServerBroker's class remark for why summing `last` is the shape that asserts
            // no unmeasured vendor fact.
            var last = $"{{\"inputTokens\":{input},\"cachedInputTokens\":{cached},"
                + $"\"cacheWriteInputTokens\":{cacheWrite},\"outputTokens\":{output},"
                + $"\"reasoningOutputTokens\":{reasoning},\"totalTokens\":{input + output}}}";
            var cumulative = $"{{\"inputTokens\":{input},\"cachedInputTokens\":{cached},"
                + $"\"cacheWriteInputTokens\":{cacheWrite},\"outputTokens\":{cumulativeOutput},"
                + $"\"reasoningOutputTokens\":{reasoning},\"totalTokens\":{input + cumulativeOutput}}}";
            transcript.Add(
                "{\"method\":\"thread/tokenUsage/updated\",\"params\":{\"threadId\":\"thread-1\","
                + $"\"turnId\":\"turn-1\",\"tokenUsage\":{{\"last\":{last},\"total\":{cumulative}}}}}}}");
        }

        transcript.Add(
            """{"method":"item/completed","params":{"threadId":"thread-1","turnId":"turn-1","item":{"id":"message-1","type":"agentMessage","text":"done"}}}""");
        transcript.Add(
            """{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[]}}}""");

        using var serverOutput = new StringReader(string.Join('\n', transcript) + "\n");
        using var serverInput = new StringWriter();
        using var batonOutput = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await CodexAppServerBroker.RunProtocolAsync(
            configuration, "Write the report.", policy, serverInput, serverOutput,
            batonOutput, error, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        return batonOutput.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// The broker's emitted lines laid out as a real room's capture, then read back through the
    /// projector exactly as <c>baton status</c> does — same registry, same bindings attribution.
    /// </summary>
    private static ExecutionUsageView ProjectRoomUsage(string roomRoot, IReadOnlyList<string> streamLines)
    {
        var executionId = new ExecutionId("exec-codex-usage");
        var outputDirectory = ArtifactManager.ResolveOutputDirectory(roomRoot, executionId);
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(
            Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName),
            string.Join('\n', streamLines) + "\n");

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>(StringComparer.Ordinal)
        {
            ["implement"] = new(
                "codex", new WorkerContract("implement", [], [], []), "unused prompt", TimeSpan.FromSeconds(30)),
        };
        Directory.CreateDirectory(roomRoot);
        File.WriteAllText(
            BatonPaths.RoomBindingsFile(roomRoot),
            System.Text.Json.JsonSerializer.Serialize(bindings));

        var start = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        var entries = new List<LogEntry>
        {
            new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                executionId,
                new WorkflowId("wf-2020"),
                new StepId("implement"),
                "implement",
                Inputs: [],
                Outputs: [],
                Timeout: TimeSpan.FromMinutes(30),
                Environment: [],
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
                Adapter: "codex"))),
            new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
            new LogEntry.CoreLogEntry(
                new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddMinutes(34)),
        };

        var usage = ExecutionUsageProjector.BuildByExecutionId(
            entries, roomRoot, WorkerAdapterRegistry.Default, roomRoot);
        return Assert.Single(usage).Value;
    }
}
