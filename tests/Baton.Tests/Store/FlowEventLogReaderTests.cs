using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Domain;
using Baton.Store;
using Baton.Tests.Projection;

namespace Baton.Tests.Store;

[Collection(ConsoleErrorCaptureCollection.Name)]
public class FlowEventLogReaderTests
{
    private static FlowEvent.ExecutionSucceeded MakeEvent(string id) => new(new ExecutionId(id));

    [Fact]
    public async Task ReadAllAsync_returns_an_empty_list_for_a_nonexistent_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        var reader = new FlowEventLogReader(path);

        var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);

        Assert.Empty(events);
    }

    [Fact]
    public async Task ReadAllAsync_reads_back_appended_events_in_append_order()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var writer = new FlowEventLogWriter(path))
            {
                await writer.AppendAsync(MakeEvent("exec-1"), TestContext.Current.CancellationToken);
                await writer.AppendAsync(MakeEvent("exec-2"), TestContext.Current.CancellationToken);
                await writer.AppendAsync(MakeEvent("exec-3"), TestContext.Current.CancellationToken);
            }

            var events = await new FlowEventLogReader(path).ReadAllAsync(TestContext.Current.CancellationToken);

            var ids = events.Cast<FlowEvent.ExecutionSucceeded>().Select(e => e.ExecutionId.Value);
            Assert.Equal(new[] { "exec-1", "exec-2", "exec-3" }, ids);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAllAsync_excludes_a_trailing_line_with_no_newline_terminator()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var completeLine = JsonSerializer.Serialize(new LogEntry.FlowLogEntry(MakeEvent("exec-1")), typeof(LogEntry), FlowEventLogJson.Options);
            var tornLine = JsonSerializer.Serialize(new LogEntry.FlowLogEntry(MakeEvent("exec-2")), typeof(LogEntry), FlowEventLogJson.Options)[..5];
            await File.WriteAllTextAsync(path, $"{completeLine}\n{tornLine}", Encoding.UTF8, TestContext.Current.CancellationToken);

            var events = await new FlowEventLogReader(path).ReadAllAsync(TestContext.Current.CancellationToken);

            var succeeded = Assert.Single(events);
            Assert.Equal("exec-1", Assert.IsType<FlowEvent.ExecutionSucceeded>(succeeded).ExecutionId.Value);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// #604's user-visible behaviour, at the layer production actually reads. The test below uses
    /// syntactically broken JSON, which threw before #604 too and so discriminates nothing about this
    /// change; every other #604 test deserializes <c>FlowEvent</c> directly, while
    /// <see cref="FlowEventLogReader"/> deserializes <c>LogEntry</c> — a different layer, not just a
    /// different wrapper. This is the arm that proves a real journal whose second line lost a required
    /// member fails loudly, and names the offending line rather than replaying it as an event for
    /// execution <c>""</c>.
    /// </summary>
    [Fact]
    public async Task ReadAllAsync_names_the_line_when_a_journal_line_has_lost_a_required_member()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var intact = JsonSerializer.Serialize(
                (LogEntry)new LogEntry.FlowLogEntry(new FlowEvent.ExecutionSucceeded(new ExecutionId("exec-1"))),
                typeof(LogEntry),
                FlowEventLogJson.Options);

            var damagedEvent = JsonNode.Parse(JsonSerializer.Serialize(
                (FlowEvent)new FlowEvent.ExecutionFailed(new ExecutionId("exec-2"), FailureClassification.Permanent, "boom"),
                typeof(FlowEvent),
                FlowEventLogJson.Options))!.AsObject();
            Assert.True(damagedEvent.Remove(nameof(FlowEvent.ExecutionFailed.ExecutionId)));

            var damagedLine = JsonNode.Parse(intact)!.AsObject();
            damagedLine["Event"] = damagedEvent;

            await File.WriteAllTextAsync(
                path, intact + "\n" + damagedLine.ToJsonString() + "\n", Encoding.UTF8,
                TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<FlowEventLogReadException>(
                () => new FlowEventLogReader(path).ReadAllAsync(TestContext.Current.CancellationToken));

            // Naming the line is the whole recoverability argument: a bad line an operator can see is
            // fixable, a silently-bound one is not.
            Assert.Contains("executionFailed", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_malformed_line_reports_its_physical_line_number_including_empty_lines(bool snapshot)
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var intact = JsonSerializer.Serialize(
                (LogEntry)new LogEntry.FlowLogEntry(MakeEvent("exec-1")), typeof(LogEntry), FlowEventLogJson.Options);
            await File.WriteAllTextAsync(path, intact + "\n\n{ not valid json }\n", Encoding.UTF8, TestContext.Current.CancellationToken);

            var reader = new FlowEventLogReader(path);
            var exception = await Assert.ThrowsAsync<FlowEventLogReadException>(async () =>
            {
                if (snapshot)
                {
                    await reader.ReadSnapshotAsync(TestContext.Current.CancellationToken);
                }
                else
                {
                    await reader.ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);
                }
            });
            Assert.Contains("Malformed line 3", exception.Message, StringComparison.Ordinal);
            Assert.Contains("{ not valid json }", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// A line with a non-string <c>Value</c> or <c>kind</c> on an <c>EnvironmentVariable</c> — a torn
    /// write, a hand-edited journal, a foreign writer — must surface at this boundary as
    /// <see cref="FlowEventLogReadException"/>, the type every catch in the reader and its callers
    /// (<c>Program.cs</c>, <c>RoomDetailTool</c>) actually handles. Asserting only at the converter
    /// would miss a converter that throws <see cref="InvalidOperationException"/> instead of
    /// <see cref="JsonException"/>: <see cref="FlowEventLogReader"/> catches <c>JsonException</c> only
    /// (<see cref="FlowEventLogJson"/> remarks), so anything else propagates unhandled.
    /// </summary>
    [Theory]
    [InlineData("Value")]
    [InlineData("kind")]
    public async Task ReadAllAsync_throws_a_FlowEventLogReadException_for_a_non_string_EnvironmentVariable_field(string fieldName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var request = new ExecutionRequest(
                new ExecutionId("exec-1"),
                new WorkflowId("wf-1"),
                new StepId("step-1"),
                "claude",
                Inputs: [],
                Outputs: [],
                Timeout: TimeSpan.FromMinutes(10),
                Environment: [new EnvironmentVariable.BatonComputed("BATON_OUTPUT_DIR", "/artifacts/execution_1")],
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

            var line = JsonSerializer.Serialize(
                (LogEntry)new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(request)),
                typeof(LogEntry),
                FlowEventLogJson.Options);

            var lineNode = JsonNode.Parse(line)!.AsObject();
            var environmentEntry = lineNode["Event"]!["Request"]!["Environment"]!.AsArray()[0]!.AsObject();
            environmentEntry[fieldName] = 123;

            await File.WriteAllTextAsync(path, lineNode.ToJsonString() + "\n", Encoding.UTF8, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<FlowEventLogReadException>(
                () => new FlowEventLogReader(path).ReadAllAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAllAsync_skips_core_owned_lines_and_returns_only_flow_events()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var writer = new FlowEventLogWriter(path))
            {
                await writer.AppendAsync(MakeEvent("exec-1"), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new CoreEvent.ExecutionStarted(new ExecutionId("exec-1"), Pid: 42), TestContext.Current.CancellationToken);
                await writer.AppendAsync(
                    new CoreEvent.ExecutionExited(new ExecutionId("exec-1"), ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);
                await writer.AppendAsync(MakeEvent("exec-2"), TestContext.Current.CancellationToken);
            }

            var events = await new FlowEventLogReader(path).ReadAllAsync(TestContext.Current.CancellationToken);

            var ids = events.Cast<FlowEvent.ExecutionSucceeded>().Select(e => e.ExecutionId.Value);
            Assert.Equal(new[] { "exec-1", "exec-2" }, ids);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAllCoreEventsAsync_returns_an_empty_list_for_a_nonexistent_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        var reader = new FlowEventLogReader(path);

        var events = await reader.ReadAllCoreEventsAsync(TestContext.Current.CancellationToken);

        Assert.Empty(events);
    }

    [Fact]
    public async Task ReadAllCoreEventsAsync_skips_flow_owned_lines_and_returns_only_core_events_in_append_order()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var writer = new FlowEventLogWriter(path))
            {
                await writer.AppendAsync(MakeEvent("exec-1"), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new CoreEvent.ExecutionStarted(new ExecutionId("exec-1"), Pid: 42), TestContext.Current.CancellationToken);
                await writer.AppendAsync(
                    new CoreEvent.ExecutionExited(new ExecutionId("exec-1"), ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);
                await writer.AppendAsync(MakeEvent("exec-2"), TestContext.Current.CancellationToken);
            }

            var events = await new FlowEventLogReader(path).ReadAllCoreEventsAsync(TestContext.Current.CancellationToken);

            Assert.Collection(
                events,
                e => Assert.Equal("exec-1", Assert.IsType<CoreEvent.ExecutionStarted>(e).ExecutionId.Value),
                e => Assert.Equal("exec-1", Assert.IsType<CoreEvent.ExecutionExited>(e).ExecutionId.Value));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAllCoreEventsAsync_excludes_a_trailing_line_with_no_newline_terminator()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var startedEvent = new CoreEvent.ExecutionStarted(new ExecutionId("exec-1"), Pid: 42);
            var exitedEvent = new CoreEvent.ExecutionExited(new ExecutionId("exec-1"), ExitCode: 0, CoreExitReason.Natural);
            var completeLine = JsonSerializer.Serialize(new LogEntry.CoreLogEntry(startedEvent), typeof(LogEntry), FlowEventLogJson.Options);
            var tornLine = JsonSerializer.Serialize(new LogEntry.CoreLogEntry(exitedEvent), typeof(LogEntry), FlowEventLogJson.Options)[..5];
            await File.WriteAllTextAsync(path, $"{completeLine}\n{tornLine}", Encoding.UTF8, TestContext.Current.CancellationToken);

            var events = await new FlowEventLogReader(path).ReadAllCoreEventsAsync(TestContext.Current.CancellationToken);

            var started = Assert.Single(events);
            Assert.Equal("exec-1", Assert.IsType<CoreEvent.ExecutionStarted>(started).ExecutionId.Value);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAllCoreEventsAsync_throws_a_FlowEventLogReadException_for_a_complete_but_malformed_line()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            await File.WriteAllTextAsync(path, "{ not valid json }\n", Encoding.UTF8, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<FlowEventLogReadException>(() => new FlowEventLogReader(path).ReadAllCoreEventsAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task ReadSnapshotAsync_returns_both_halves_from_a_single_read()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var writer = new FlowEventLogWriter(path))
            {
                await writer.AppendAsync(MakeEvent("exec-1"), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new CoreEvent.ExecutionStarted(new ExecutionId("exec-1"), Pid: 42), TestContext.Current.CancellationToken);
                await writer.AppendAsync(
                    new CoreEvent.ExecutionExited(new ExecutionId("exec-1"), ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);
                await writer.AppendAsync(MakeEvent("exec-2"), TestContext.Current.CancellationToken);
            }

            var snapshot = await new FlowEventLogReader(path).ReadSnapshotAsync(TestContext.Current.CancellationToken);

            var flowIds = snapshot.FlowEvents.Cast<FlowEvent.ExecutionSucceeded>().Select(e => e.ExecutionId.Value);
            Assert.Equal(new[] { "exec-1", "exec-2" }, flowIds);
            Assert.Collection(
                snapshot.CoreEvents,
                e => Assert.IsType<CoreEvent.ExecutionStarted>(e),
                e => Assert.IsType<CoreEvent.ExecutionExited>(e));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Reading_a_journal_held_with_conflicting_share_throws_FlowJournalHeldException_naming_holder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            await File.WriteAllTextAsync(path, "intact\n", TestContext.Current.CancellationToken);
            using var exclusiveHolder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var ex = await Assert.ThrowsAsync<FlowJournalHeldException>(
                () => new FlowEventLogReader(path).ReadAllAsync(TestContext.Current.CancellationToken));

            Assert.IsType<IOException>(ex.InnerException);
            Assert.Contains("held open by another process", ex.Message);
            Assert.Contains("Current holder:", ex.Message);
            Assert.Contains($"(pid {Environment.ProcessId})", ex.Message);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public void IsSharingViolation_returns_false_for_non_sharing_IOException()
    {
        var nonSharingEx = new IOException("Path not found", hresult: 3);
        Assert.False(FileHolderProbe.IsSharingViolation(nonSharingEx));

        var sharingEx = new IOException("Sharing violation", hresult: FileHolderProbe.ErrorSharingViolationHResult);
        Assert.True(FileHolderProbe.IsSharingViolation(sharingEx));
    }

    /// <summary>
    /// #1779 owner ruling: the read still succeeds past an unrecognized <c>eventType</c>/<c>owner</c>,
    /// dropping the unrecognized lines from the returned sequence and reporting the count once (not
    /// per line) -- see <see cref="FlowEventLogReader"/>'s own remarks for which diagnostics channel
    /// that report lands on.
    /// </summary>
    [Fact]
    public async Task ReadAllAsync_skips_and_counts_unknown_event_kinds_instead_of_throwing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var known1 = JsonSerializer.Serialize(
                (LogEntry)new LogEntry.FlowLogEntry(MakeEvent("exec-1")), typeof(LogEntry), FlowEventLogJson.Options);
            var known2 = JsonSerializer.Serialize(
                (LogEntry)new LogEntry.FlowLogEntry(MakeEvent("exec-2")), typeof(LogEntry), FlowEventLogJson.Options);
            var known3 = JsonSerializer.Serialize(
                (LogEntry)new LogEntry.FlowLogEntry(MakeEvent("exec-3")), typeof(LogEntry), FlowEventLogJson.Options);

            // A "flow" line (known owner) carrying an eventType this binary has never heard of --
            // the shape a newer CLI writes and an older one must still be able to read past.
            const string unknownEventKind = """{"owner":"flow","Event":{"eventType":"somethingNewer"}}""";
            // An owner discriminator this binary has never heard of at all.
            const string unknownOwnerKind = """{"owner":"somethingEvenNewer"}""";

            await File.WriteAllTextAsync(
                path,
                string.Join('\n', [known1, unknownEventKind, known2, unknownOwnerKind, known3, string.Empty]),
                Encoding.UTF8,
                TestContext.Current.CancellationToken);

            using var sw = new StringWriter();
            var originalErr = Console.Error;
            Console.SetError(sw);

            IReadOnlyList<FlowEvent> events;
            try
            {
                events = await new FlowEventLogReader(path).ReadAllAsync(TestContext.Current.CancellationToken);
            }
            finally
            {
                Console.SetError(originalErr);
            }

            var ids = events.Cast<FlowEvent.ExecutionSucceeded>().Select(e => e.ExecutionId.Value);
            Assert.Equal(new[] { "exec-1", "exec-2", "exec-3" }, ids);

            var errOutput = sw.ToString();
            Assert.Contains("Skipped 2 unknown event kind line(s)", errOutput);
            Assert.Contains("somethingNewer", errOutput);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }
}
