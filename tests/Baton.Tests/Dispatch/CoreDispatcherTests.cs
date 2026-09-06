using Baton.Tests.TestSupport;
using System.Globalization;
using System.Text.Json;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Store;

namespace Baton.Tests.Dispatch;

/// <summary>
/// Integration tests: these spawn a real process through the managed <c>BatonTask</c> engine
/// (M7 Phase 6's acceptance criteria — a trivial worker, output file appears in the pre-allocated
/// artifact directory, Core lifecycle events land in the log). No mocking of Baton.Core itself.
/// </summary>
[Collection(SerializedEnvironmentCollection.Name)]
public class CoreDispatcherTests
{
    private static readonly ExecutionId ExecutionId = new("exec-1");

    [Fact]
    public async Task DispatchAsync_runs_a_trivial_worker_and_the_output_file_appears_in_the_pre_allocated_directory()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var environment = ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot);
            var request = MakeRequest(environment);
            var target = EchoHelloToOutputFile();

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(CoreExitReason.Natural, result.Reason);
            Assert.True(File.Exists(Path.Combine(outputDirectory, "hello.txt")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    [Fact]
    public async Task DispatchAsync_when_stream_logger_fails_execution_still_succeeds()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            // Create .stdout.log as a directory to force ExecutionStreamLogger write failure
            Directory.CreateDirectory(Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName));

            var environment = ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot);
            var request = MakeRequest(environment);
            var target = EchoHelloToOutputFile();

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(CoreExitReason.Natural, result.Reason);
            Assert.True(File.Exists(Path.Combine(outputDirectory, "hello.txt")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    /// <summary>
    /// #533: <see cref="CoreDispatchTarget.Environment"/> is the seam a vendor adapter uses to set a
    /// vendor-specific variable (e.g. Claude Code's subagent depth cap) without <c>Baton</c> ever
    /// knowing the variable's name (Architecture Rule 2). This proves it actually reaches the child
    /// process, not just that <c>CoreDispatcher</c> compiles against it.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_sets_CoreDispatchTarget_Environment_variables_on_the_child_process()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var target = EchoEnvVarToOutputFile("BATON_533_TEST_VAR", [("BATON_533_TEST_VAR", "reached-the-child")]);

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var written = await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, "hello.txt"), TestContext.Current.CancellationToken);
            Assert.Contains("reached-the-child", written);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    /// <summary>
    /// The control for the test above: an unset target requests no environment contribution, so the
    /// shell's own unset-variable expansion (empty on both cmd and sh) is what appears — proving the
    /// prior test's positive result came from <see cref="CoreDispatchTarget.Environment"/> and not
    /// from something already present in the test host's own environment.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_leaves_the_variable_unset_when_CoreDispatchTarget_Environment_is_null()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var target = EchoEnvVarToOutputFile("BATON_533_TEST_VAR", environment: null);

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var written = await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, "hello.txt"), TestContext.Current.CancellationToken);
            Assert.DoesNotContain("reached-the-child", written);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    /// <summary>
    /// N5/F6 (#1664 re-review): the two detectors run independently since F6 rewired
    /// <c>CoreDispatcher</c>'s stdout sink (`:745-761`) to test each on every line rather than short-
    /// circuiting once either fired — this was uncovered end to end. Wires ONLY
    /// <see cref="CoreDispatchTarget.DetectsTerminalResult"/> (not <see cref="CoreDispatchTarget.DetectsTerminalSuccess"/>,
    /// which stays null the way a real claude-adapter target does), so a green
    /// <see cref="CoreDispatchResult.TerminalResultObserved"/> here proves the result latch does not
    /// depend on the success latch also being wired.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_latches_TerminalResultObserved_when_only_DetectsTerminalResult_is_wired()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var baseTarget = EchoLineToStdout("RESULT_MARKER_1664");
            var target = baseTarget with { DetectsTerminalResult = line => line.Contains("RESULT_MARKER_1664", StringComparison.Ordinal) };

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.True(result.TerminalResultObserved);
            Assert.False(result.TerminalSuccessObserved);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    /// <summary>Polarity control for the test above: a line that never matches leaves the latch false.</summary>
    [Fact]
    public async Task DispatchAsync_leaves_TerminalResultObserved_false_when_the_line_never_matches()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var baseTarget = EchoLineToStdout("SOMETHING_ELSE");
            var target = baseTarget with { DetectsTerminalResult = line => line.Contains("RESULT_MARKER_1664", StringComparison.Ordinal) };

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TerminalResultObserved);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    private static CoreDispatchTarget EchoLineToStdout(string line) =>
        new("cmd", ["/c", $"echo {line}"]);

    [Fact]
    public async Task DispatchAsync_records_Started_and_Exited_CoreEvents_to_the_log()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var target = EchoHelloToOutputFile();

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await new CoreDispatcher(writer, writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);
            }

            var entries = (await File.ReadAllLinesAsync(logPath, TestContext.Current.CancellationToken))
                .Select(line => JsonSerializer.Deserialize<LogEntry>(line, FlowEventLogJson.Options))
                .Cast<LogEntry.CoreLogEntry>()
                .Select(e => e.Event)
                .ToList();

            var started = Assert.Single(entries.OfType<CoreEvent.ExecutionStarted>());
            Assert.Equal(ExecutionId, started.ExecutionId);
            Assert.True(started.Pid > 0);

            var exited = Assert.Single(entries.OfType<CoreEvent.ExecutionExited>());
            Assert.Equal(ExecutionId, exited.ExecutionId);
            Assert.Equal(0, exited.ExitCode);
            Assert.Equal(CoreExitReason.Natural, exited.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    [Fact]
    public async Task DispatchAsync_records_StderrTail_in_ExecutionExited_CoreEvent_when_process_writes_to_stderr_and_exits_non_zero()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var distinctiveStderr = "DISTINCTIVE_STDERR_TAIL_759";
            var target = new CoreDispatchTarget("cmd", ["/c", $"echo {distinctiveStderr} 1>&2 & exit 1"]);

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await new CoreDispatcher(writer, writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);
            }

            var reader = new FlowEventLogReader(logPath);
            var coreEvents = await reader.ReadAllCoreEventsAsync(TestContext.Current.CancellationToken);
            var exited = Assert.Single(coreEvents.OfType<CoreEvent.ExecutionExited>());
            Assert.Equal(1, exited.ExitCode);
            Assert.NotNull(exited.StderrTail);
            Assert.Contains(distinctiveStderr, exited.StderrTail);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    [Fact]
    public async Task DispatchAsync_surfaces_a_non_zero_exit_code_without_throwing()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var request = MakeRequest([]);
            var target = new CoreDispatchTarget("cmd", ["/c", "exit 7"]);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer, writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(7, result.ExitCode);
            Assert.Equal(CoreExitReason.Natural, result.Reason);
        }
        finally
        {
            FileCleanup.Delete(logPath);
        }
    }

    [Fact]
    public async Task DispatchAsync_does_not_resolve_pass_through_variable_values()
    {
        // Pass-through env var *values* are a future worker-adapter concern — the Core
        // Dispatcher must not accidentally leak a name-only declaration through as a literal value.
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var request = MakeRequest([new EnvironmentVariable.PassThrough("SOME_SECRET")]);
            var target = new CoreDispatchTarget("cmd", ["/c", "exit 0"]);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer, writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            FileCleanup.Delete(logPath);
        }
    }

    /// <summary>
    /// M23 Phase 3's own named verification bullet (#272): "an integration test asserting a spawned
    /// worker's actual cwd matches a configured WorkingDirectory" — through the real wiring
    /// (<see cref="CoreDispatchTarget.WorkingDirectory"/> → <see cref="CoreDispatcher.DispatchAsync"/>
    /// → <c>BatonTask.WithCwd</c>), not <c>WithCwd</c> in isolation (already proven by
    /// <c>BatonTaskTests</c>'s own <c>WithCwd_ChangesChildWorkingDirectory</c> and
    /// <c>WithCwd_InvalidDirectory_RunThrowsBatonExceptionWithSpawnFailed</c>).
    /// </summary>
    [Fact]
    public async Task DispatchAsync_spawns_the_worker_with_its_actual_cwd_set_to_the_configured_WorkingDirectory()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var configuredWorkingDirectory = Path.Combine(Path.GetTempPath(), $"cwd-target-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            Directory.CreateDirectory(configuredWorkingDirectory);

            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var environment = ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot);
            var request = MakeRequest(environment);
            var target = PrintCwdToOutputFile(configuredWorkingDirectory);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer, writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var printedCwd = (await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, "hello.txt"), TestContext.Current.CancellationToken)).Trim();
            var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredWorkingDirectory));
            var actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(printedCwd));
            Assert.Equal(expected, actual, ignoreCase: true);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            DirectoryCleanup.DeleteRecursively(configuredWorkingDirectory);
            FileCleanup.Delete(logPath);
        }
    }

    /// <summary>
    /// #1084: drives the actual seed-write path in <see cref="CoreDispatcher.DispatchAsync"/> — path
    /// expansion against native pathVariables, parent-directory creation, and the content write through
    /// <see cref="CoreDispatcher.RenderSeedContent"/> — which the adapter-level and pure-function tests
    /// do not exercise. The seed lands at the expanded (native) path under a directory that did not
    /// exist, and its body is valid, forward-slashed JSON even though BATON_OUTPUT_DIR is a native path.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_writes_a_declared_seed_file_to_its_expanded_path_with_a_valid_json_body()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var noop = new CoreDispatchTarget("cmd", ["/c", "exit 0"]);
            // Path template references BATON_OUTPUT_DIR and a not-yet-existing parent; content embeds the
            // same variable in a JSON string, the shape a raw backslash substitution would void.
            var target = noop with
            {
                SeedFiles = [new CoreDispatchSeedFile(
                    "%BATON_OUTPUT_DIR%/.seed_home/settings.json",
                    """{"target":"write_file(%BATON_OUTPUT_DIR%/out.md)"}""")],
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer, writer)
                .DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var seedPath = Path.Combine(outputDirectory, ".seed_home", "settings.json");
            Assert.True(File.Exists(seedPath));
            var body = await File.ReadAllTextAsync(seedPath, TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(body);
            var rule = doc.RootElement.GetProperty("target").GetString();
            var expectedDir = outputDirectory.Replace('\\', '/');
            Assert.Equal($"write_file({expectedDir}/out.md)", rule);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    /// <summary>
    /// #1151/#1929 review HIGH: drives the actual seed-COPY path — the seam that replaced a write
    /// performed while a binding was merely being resolved. Both polarities in one dispatch: an absent
    /// destination is placed verbatim, and a destination already holding different bytes is left exactly
    /// as it was. Nothing is pruned.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_places_a_declared_seed_copy_and_keeps_a_differing_existing_file()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(Path.GetTempPath(), $"seedcopy-src-{Guid.NewGuid():N}");
        var destinationRoot = Path.Combine(Path.GetTempPath(), $"seedcopy-dst-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            Directory.CreateDirectory(sourceRoot);
            var placedSource = Path.Combine(sourceRoot, "placed.md");
            var keptSource = Path.Combine(sourceRoot, "kept.md");
            await File.WriteAllTextAsync(placedSource, "package content", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(keptSource, "package content", TestContext.Current.CancellationToken);

            // The destination for the kept file exists already, under a directory the copy loop would
            // otherwise create, and holds content AER did not author.
            Directory.CreateDirectory(destinationRoot);
            var keptDestination = Path.Combine(destinationRoot, "kept.md");
            await File.WriteAllTextAsync(keptDestination, "the operator's own content", TestContext.Current.CancellationToken);
            var placedDestination = Path.Combine(destinationRoot, "nested", "placed.md");

            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var target = new CoreDispatchTarget("cmd", ["/c", "exit 0"]) with
            {
                SeedCopies =
                [
                    new CoreDispatchSeedCopy(placedDestination, placedSource),
                    new CoreDispatchSeedCopy(keptDestination, keptSource),
                ],
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer, writer)
                .DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(
                "package content",
                await File.ReadAllTextAsync(placedDestination, TestContext.Current.CancellationToken));
            Assert.Equal(
                "the operator's own content",
                await File.ReadAllTextAsync(keptDestination, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            DirectoryCleanup.DeleteRecursively(sourceRoot);
            DirectoryCleanup.DeleteRecursively(destinationRoot);
            FileCleanup.Delete(logPath);
        }
    }

    /// <summary>
    /// #1885: the wiring, end to end on a real dispatch. The obstruction is reproduced the way this
    /// file's own #1525 arm above already does — a DIRECTORY where the stream file has to go, so the
    /// logger's eager create throws and it declares both streams lost — extended with directories at
    /// both marker paths, so the file channel cannot announce the loss for the whole run. Why the ledger
    /// survives that and the marker does not is <c>spec/baton.md</c> §3's.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_journals_a_declared_stream_log_loss_when_no_marker_can_ever_land()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            Directory.CreateDirectory(Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName));
            Directory.CreateDirectory(Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutWriteFailureMarkerFileName));
            Directory.CreateDirectory(Path.Combine(outputDirectory, ExecutionStreamLogger.StderrWriteFailureMarkerFileName));

            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var target = EchoHelloToOutputFile();

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await new CoreDispatcher(writer, writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);
            }

            // Neither marker landed -- so if the journal is empty too, the loss reached no durable
            // channel at all, which is precisely the pre-#1885 state this arm exists to forbid.
            Assert.False(File.Exists(Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutWriteFailureMarkerFileName)));

            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            var losses = events.OfType<FlowEvent.StreamLogLossDeclared>().ToList();

            var stdout = losses.Where(l => l.Stream == ExecutionStreamLogger.StdoutStreamName).ToList();
            // #1888: exactly two, end to end -- the declaration and the terminal re-announcement. The
            // count is the assertion: `MarkTerminal` is idempotent and the dispatcher calls it from both
            // the Exited arm and its own finally, so a third here would mean a duplicate on the wire,
            // and one would mean the terminal channel never fired on a real dispatch (it was pinned at
            // unit level in StreamLogLossJournalTests and nowhere else).
            Assert.Equal(2, stdout.Count);
            Assert.All(stdout, l => Assert.Equal(ExecutionId, l.ExecutionId));
            // The literal ExecutionUsageProjector compares the marker channel against -- a different
            // string here would read as a disagreement between the writer's own two announcements.
            Assert.All(stdout, l => Assert.Equal("stream-truncated-by-write-failure", l.Reason));
            Assert.All(stdout, l => Assert.False(l.MarkerLanded));
            // Both polarities of the flag #1888 added, which is what makes the pair distinguishable on
            // the wire: MarkerLanded is false on BOTH of these, so asserting it twice would prove
            // nothing about which event is the terminal record.
            Assert.Contains(stdout, l => l.TerminalReannouncement is false);
            Assert.Contains(stdout, l => l.TerminalReannouncement is true);
            Assert.Contains(losses, l => l.Stream == ExecutionStreamLogger.StderrStreamName);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    /// <summary>
    /// The discriminating control for the arm above: an unobstructed dispatch of the same worker. A
    /// stream-log loss journalled here would mean the event fires on every healthy run, and the arm
    /// above would be measuring nothing.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_journals_no_stream_log_loss_on_an_unobstructed_run()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await new CoreDispatcher(writer, writer).DispatchAsync(
                    request, EchoHelloToOutputFile(), TestContext.Current.CancellationToken);
            }

            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.StreamLogLossDeclared>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    private static ExecutionRequest MakeRequest(IReadOnlyList<EnvironmentVariable> environment) => new(
        ExecutionId,
        new WorkflowId("wf-1"),
        new StepId("step-1"),
        "trivial",
        Inputs: [],
        Outputs: ["hello.txt"],
        Timeout: TimeSpan.FromSeconds(30),
        Environment: environment,
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static CoreDispatchTarget EchoHelloToOutputFile() =>
        new("cmd", ["/c", "echo hello > %BATON_OUTPUT_DIR%\\hello.txt"]);

    /// <summary>
    /// #549: a variable the operator's shell exports must NOT reach a worker unless it is
    /// allowlisted. <c>CLAUDE_CODE_SIMPLE</c> is the concrete hazard <c>InheritedEnvironment</c>
    /// records; this is the arm that proves the exclusion actually happens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Polarity in both directions, because "the child saw nothing" is also what a broken harness
    /// produces: the same dispatch is asked for an ALLOWLISTED variable, which must arrive. If both
    /// arms come back empty the test is measuring its own plumbing rather than the allowlist.
    /// </para>
    /// <para>
    /// <b>The <c>NUGET_HTTP_CACHE_PATH</c> arm is a control on the PLANT, not on the allowlist.</b>
    /// The negative arms prove nothing unless <c>Environment.SetEnvironmentVariable</c> actually
    /// reaches the spawned child, and the <c>PATH</c> arm cannot establish that: <c>PATH</c> is in
    /// the native block before this process starts, so it discriminates "the harness spawns and
    /// echoes", not "an operator-set variable can reach this child". This arm plants an
    /// <b>allowlisted</b> name by the same mechanism the negative arms use and requires the sentinel
    /// to <b>arrive</b>. Red here means every negative arm on that platform is vacuous — read this
    /// failure before concluding anything about the allowlist.
    /// </para>
    /// <para>
    /// <b>It exists because a reviewer argued the negative arms could not fail on Linux or macOS,
    /// and running it settled that they can.</b> The argument was that .NET on Unix does not call
    /// <c>setenv</c> for <c>SetEnvironmentVariable</c> — it mutates a managed dictionary — while the
    /// child is spawned by <c>BatonProcessRunner</c> via <see cref="System.Diagnostics.Process"/>,
    /// whose environment is built explicitly from <c>WithEnv</c>/<c>WithClearEnv</c>
    /// (<c>ProcessStartInfo.EnvironmentVariables</c>) rather than by re-reading the operator's own
    /// environment mutations at spawn time, so the sentinel would never arrive whether or not
    /// <see cref="Baton.Core.BatonTask.WithClearEnv"/> were called. All four arms pass on
    /// ubuntu-latest (CI run 30472390670, predating this repo's Windows-only pivot #1405), so the
    /// plant does reach the child and the negative arms were never vacuous. Recorded because the
    /// hypothesis was specific and plausible enough to be worth someone else's time, and the
    /// measurement is cheaper than the argument.
    /// </para>
    /// <para>
    /// <c>LC_CTYPE</c> carries the sentinel because it is on the allowlist for its own reasons and
    /// nothing in this child reads it, so overwriting it changes no behaviour — unlike <c>PATH</c>,
    /// which cannot be overwritten without breaking the spawn the other control depends on. It
    /// replaced <c>NUGET_HTTP_CACHE_PATH</c>, which this test had come to be the only justification
    /// for: an allowlist entry held in place by a test is the wrong way round.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("CLAUDE_CODE_SIMPLE", false, true, "the real hazard — disables hooks exactly as --bare does")]
    [InlineData("BATON_549_NOT_ALLOWLISTED", false, true, "an arbitrary name, so the result is about the list and not this one variable")]
    [InlineData("LC_CTYPE", true, true, "allowlisted AND planted the same way the negative arms are — the control on the plant")]
    [InlineData("PATH", true, false, "allowlisted, and load-bearing: AER spawns vendor CLIs by name")]
    public async Task An_inherited_variable_reaches_the_worker_only_when_it_is_allowlisted(
        string variableName, bool expectedToArrive, bool plantSentinel, string what)
    {
        Assert.NotEmpty(what);

        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        var original = Environment.GetEnvironmentVariable(variableName);
        const string Sentinel = "inherited-from-the-operator-shell";
        try
        {
            // Set on THIS process, which is what a worker would otherwise inherit. PATH is left alone
            // — overwriting it would break the spawn the control arm depends on — so its arrival is
            // checked by presence rather than by a sentinel.
            if (plantSentinel)
            {
                Environment.SetEnvironmentVariable(variableName, Sentinel);
            }

            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer, writer).DispatchAsync(
                request, EchoEnvVarToOutputFile(variableName, environment: null), TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var written = await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, "hello.txt"), TestContext.Current.CancellationToken);

            if (expectedToArrive && plantSentinel)
            {
                // The control on the plant. Red here means SetEnvironmentVariable never reached the
                // spawned child on this platform, so every negative arm is vacuous here too — read
                // this failure before concluding anything about the allowlist.
                Assert.Contains(Sentinel, written, StringComparison.Ordinal);
            }
            else if (expectedToArrive)
            {
                // An unexpanded "%PATH%" (cmd) or empty line (sh) is what absence looks like.
                Assert.DoesNotContain($"%{variableName}%", written, StringComparison.Ordinal);
                Assert.True(written.Trim().Length > 0, $"{variableName} did not reach the child at all.");
            }
            else
            {
                Assert.DoesNotContain(Sentinel, written, StringComparison.Ordinal);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, original);
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    private static CoreDispatchTarget EchoEnvVarToOutputFile(
        string variableName, IReadOnlyList<(string Name, string Value)>? environment) =>
        new(
            "cmd", ["/c", $"echo %{variableName}% > %BATON_OUTPUT_DIR%\\hello.txt"], Environment: environment);

    private static CoreDispatchTarget PrintCwdToOutputFile(string workingDirectory) =>
        new("cmd", ["/c", "cd > %BATON_OUTPUT_DIR%\\hello.txt"], workingDirectory);

    // Issue #292: durable capture of an ordinary step's resolved prompt, written into the execution's
    // own output directory before the worker ever spawns.

    [Fact]
    public async Task DispatchAsync_writes_the_expanded_PromptText_to_prompt_txt_in_the_output_directory()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var environment = ArtifactManager.BuildEnvironment(["/inputs/goal.md"], outputDirectory, artifactsRoot);
            var request = MakeRequest(environment);
            var promptText = "Use %BATON_INPUT_0% and write to %BATON_OUTPUT_DIR%.";
            var target = EchoHelloToOutputFile() with { PromptText = promptText };

            await using var writer = new FlowEventLogWriter(logPath);
            await new CoreDispatcher(writer, writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);

            var promptFilePath = Path.Combine(outputDirectory, ArtifactManager.PromptFileName);
            Assert.True(File.Exists(promptFilePath));
            var writtenPrompt = await File.ReadAllTextAsync(promptFilePath, TestContext.Current.CancellationToken);
            Assert.Equal("Use /inputs/goal.md and write to " + outputDirectory + ".", writtenPrompt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    /// <summary>
    /// #713: pins all three shapes of the token grammar <c>CoreDispatcher.VariableToken</c>
    /// defines -- its xmldoc is the one home for what the grammar is and what the old code did to
    /// a prompt. Red first against the unbounded form: one assertion diff showed the braced form
    /// left literal and the prose word spliced.
    /// </summary>
    [Fact]
    public async Task Variable_expansion_stops_at_an_identifier_boundary_and_accepts_the_braced_form()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var environment = ArtifactManager.BuildEnvironment(["/inputs/goal.md"], outputDirectory, artifactsRoot)
                // One known name a strict prefix of another, so the boundary rule -- not
                // longest-first ordering -- is what has to pick the right one.
                .Append(new EnvironmentVariable.BatonComputed("BATON_INPUT_0_ARCHIVED", "/archive/goal.md"))
                .ToList();
            var request = MakeRequest(environment);
            var target = EchoHelloToOutputFile() with
            {
                PromptText = "Write to ${BATON_OUTPUT_DIR}. Mention $BATON_OUTPUT_DIRECTORY verbatim. "
                    + "Read $BATON_INPUT_0_ARCHIVED, not $BATON_INPUT_0X.",
            };

            await using var writer = new FlowEventLogWriter(logPath);
            await new CoreDispatcher(writer, writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);

            var writtenPrompt = await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, ArtifactManager.PromptFileName),
                TestContext.Current.CancellationToken);
            Assert.Equal(
                $"Write to {outputDirectory}. Mention $BATON_OUTPUT_DIRECTORY verbatim. "
                + "Read /archive/goal.md, not $BATON_INPUT_0X.",
                writtenPrompt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    /// <summary>
    /// Written before the worker spawns (intent-first ordering), so the prompt stays
    /// available for audit even when the worker itself exits nonzero -- exactly the "present even if
    /// the execution later fails" guarantee issue #292 asks for.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_writes_prompt_txt_even_when_the_worker_exits_non_zero()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var environment = ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot);
            var request = MakeRequest(environment);
            var target = new CoreDispatchTarget("cmd", ["/c", "exit 7"]) with
            { PromptText = "Draft a plan." };

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer, writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(7, result.ExitCode);
            var promptFilePath = Path.Combine(outputDirectory, ArtifactManager.PromptFileName);
            Assert.True(File.Exists(promptFilePath));
            Assert.Equal("Draft a plan.", await File.ReadAllTextAsync(promptFilePath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    [Fact]
    public async Task DispatchAsync_writes_no_prompt_file_when_PromptText_is_null()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var environment = ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot);
            var request = MakeRequest(environment);
            var target = EchoHelloToOutputFile();

            await using var writer = new FlowEventLogWriter(logPath);
            await new CoreDispatcher(writer, writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.False(File.Exists(Path.Combine(outputDirectory, ArtifactManager.PromptFileName)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    // #563: a worker's stderr used to be read by the discard-path drain thread (today,
    // BatonProcessRunner's RunDiscardingOutput with sink: null) and thrown away — produced,
    // consumed, and discarded. These spawn a real process that writes to a real stderr pipe;
    // nothing here is stubbed.

    [Fact]
    public async Task DispatchAsync_captures_what_the_worker_wrote_to_stderr()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var request = MakeRequest([]);
            var target = WriteToStderrAndExit("BOILER-PLATE-DIAGNOSTIC", exitCode: 1);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer, writer).DispatchAsync(
                request, target, TestContext.Current.CancellationToken);

            Assert.Equal(1, result.ExitCode);
            Assert.NotNull(result.StderrTail);
            Assert.Contains("BOILER-PLATE-DIAGNOSTIC", result.StderrTail);
        }
        finally
        {
            FileCleanup.Delete(logPath);
        }
    }

    /// <summary>
    /// The polarity control for the test above. Without it, an implementation that returned, say, an
    /// empty string for every dispatch would still pass the positive arm's <c>Contains</c> — this is
    /// what makes <c>StderrTail</c> mean "the worker spoke" rather than "the field exists".
    /// </summary>
    [Fact]
    public async Task DispatchAsync_leaves_StderrTail_null_when_the_worker_writes_nothing_to_stderr()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var request = MakeRequest([]);
            var target = new CoreDispatchTarget("cmd", ["/c", "exit 1"]);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer, writer).DispatchAsync(
                request, target, TestContext.Current.CancellationToken);

            Assert.Equal(1, result.ExitCode);
            Assert.Null(result.StderrTail);
        }
        finally
        {
            FileCleanup.Delete(logPath);
        }
    }

    [Fact]
    public async Task DispatchAsync_records_StdoutTail_when_process_writes_to_stdout()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var request = MakeRequest([]);
            const string distinctiveStdout = "BOILER-PLATE-STDOUT-DIAGNOSTIC";
            var target = new CoreDispatchTarget("cmd", ["/c", $"echo {distinctiveStdout}& exit 1"]);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer, writer).DispatchAsync(
                request, target, TestContext.Current.CancellationToken);

            Assert.Equal(1, result.ExitCode);
            Assert.NotNull(result.StdoutTail);
            Assert.Contains("BOILER-PLATE-STDOUT-DIAGNOSTIC", result.StdoutTail);
        }
        finally
        {
            FileCleanup.Delete(logPath);
        }
    }

    [Fact]
    public async Task DispatchAsync_leaves_StdoutTail_null_when_the_worker_writes_nothing_to_stdout()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var request = MakeRequest([]);
            var target = new CoreDispatchTarget("cmd", ["/c", "exit 1"]);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer, writer).DispatchAsync(
                request, target, TestContext.Current.CancellationToken);

            Assert.Equal(1, result.ExitCode);
            Assert.Null(result.StdoutTail);
        }
        finally
        {
            FileCleanup.Delete(logPath);
        }
    }

    [Fact]
    public async Task DispatchAsync_bounds_StdoutTail_and_keeps_the_end_rather_than_the_beginning()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        var payloadDirectory = Path.Combine(Path.GetTempPath(), $"stdout-payload-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(payloadDirectory);
            const string payloadFileName = "payload_stdout.txt";
            var payload = "FIRST-STDOUT-MARKER" + new string('x', CoreDispatcher.MaxRetainedStderrLength * 3) + "LAST-STDOUT-MARKER";
            await File.WriteAllTextAsync(
                Path.Combine(payloadDirectory, payloadFileName), payload, TestContext.Current.CancellationToken);

            var target = new CoreDispatchTarget("cmd", ["/c", $"type {payloadFileName} & exit 1"], payloadDirectory);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer, writer).DispatchAsync(
                MakeRequest([]), target, TestContext.Current.CancellationToken);

            Assert.NotNull(result.StdoutTail);
            Assert.True(
                result.StdoutTail.Length <= CoreDispatcher.MaxRetainedStderrLength,
                $"retained {result.StdoutTail.Length} chars, cap is {CoreDispatcher.MaxRetainedStderrLength}");
            Assert.Contains("LAST-STDOUT-MARKER", result.StdoutTail);
            Assert.DoesNotContain("FIRST-STDOUT-MARKER", result.StdoutTail);
        }
        finally
        {
            FileCleanup.Delete(logPath);
            DirectoryCleanup.DeleteRecursively(payloadDirectory);
        }
    }


    /// <summary>
    /// Proves the buffer keeps the <i>end</i> and is bounded, in one test — the two properties are
    /// one mistake apart. A head-keeping implementation is equally "bounded" and would surface the
    /// worker's opening banner while discarding the error it exited on, which is the exact content
    /// #563 exists to deliver.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_bounds_StderrTail_and_keeps_the_end_rather_than_the_beginning()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        var payloadDirectory = Path.Combine(Path.GetTempPath(), $"stderr-payload-{Guid.NewGuid():N}");
        try
        {
            // Dumped from a file rather than generated by a shell loop: `cmd`'s `for /L` and `sh`'s
            // `for` need different quoting and escaping, and the first version of this test silently
            // emitted a single padding line on Windows — making it a test of batch syntax rather than
            // of the buffer. Writing the bytes here keeps the content identical on both platforms.
            //
            // Referenced by bare filename from a dedicated working directory, never by absolute path:
            // the whole script is one argument, so a path containing a space makes the launcher
            // re-quote it and the inner quotes then break the command. A bare name cannot contain one.
            Directory.CreateDirectory(payloadDirectory);
            const string payloadFileName = "payload.txt";
            var payload = "FIRST-MARKER" + new string('x', CoreDispatcher.MaxRetainedStderrLength * 3) + "LAST-MARKER";
            await File.WriteAllTextAsync(
                Path.Combine(payloadDirectory, payloadFileName), payload, TestContext.Current.CancellationToken);

            var target = new CoreDispatchTarget("cmd", ["/c", $"type {payloadFileName} 1>&2 & exit 1"], payloadDirectory);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer, writer).DispatchAsync(
                MakeRequest([]), target, TestContext.Current.CancellationToken);

            Assert.NotNull(result.StderrTail);
            Assert.True(
                result.StderrTail.Length <= CoreDispatcher.MaxRetainedStderrLength,
                $"retained {result.StderrTail.Length} chars, cap is {CoreDispatcher.MaxRetainedStderrLength}");
            Assert.Contains("LAST-MARKER", result.StderrTail);
            Assert.DoesNotContain("FIRST-MARKER", result.StderrTail);
        }
        finally
        {
            FileCleanup.Delete(logPath);
            DirectoryCleanup.DeleteRecursively(payloadDirectory);
        }
    }

    /// <summary>
    /// A pipe splits at arbitrary byte offsets, so a multi-byte UTF-8 sequence routinely straddles
    /// two chunks. Decoding each chunk with its own <c>GetString</c> emits U+FFFD at every such
    /// boundary and corrupts exactly the non-ASCII diagnostic the field exists to carry.
    /// </summary>
    /// <remarks>
    /// Driven through the decode helpers directly rather than through a spawned process, because a
    /// real pipe gives no control over <i>where</i> it splits: a short payload arrives in a single
    /// chunk, never reaches the boundary case, and would pass against the naive implementation this
    /// is written to exclude. Splitting the sequence by hand is what makes the test discriminate.
    /// </remarks>
    /// <summary>
    /// The same theory, on the path that had the WEAKER treatment (#642) — <c>StdoutLineBuffer</c>
    /// carries which path that was and why the asymmetry ran the wrong way.
    /// </summary>
    /// <remarks>
    /// Its stderr twin above is the CONTROL and has to keep passing: identical input, identical split
    /// offsets, on a path that was already correct. If both go red the harness is at fault rather
    /// than the decoder, which a one-sided test could not tell apart.
    /// <para>
    /// A trailing newline is appended because this buffer emits by LINE, where the stderr one retains
    /// a tail — the only difference between the two, and it is about how each surfaces text rather
    /// than about the decode under test.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(1, "inside the 2-byte é")]
    [InlineData(3, "inside the 3-byte —")]
    [InlineData(6, "inside the 4-byte 🚨")]
    [InlineData(7, "inside the 4-byte 🚨, one byte later")]
    public void Stdout_decoding_survives_a_multi_byte_sequence_split_across_two_chunks(int splitAt, string what)
    {
        Assert.NotEmpty(what);

        // Same 9 bytes as the stderr theory: é at [0,2), — at [2,5), 🚨 at [5,9).
        const string payload = "é—🚨";
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload + "\n");
        Assert.Equal(10, bytes.Length);

        var lines = new List<string>();
        var buffer = new StdoutLineBuffer();
        buffer.Append(bytes[..splitAt], lines.Add);
        buffer.Append(bytes[splitAt..], lines.Add);

        var line = Assert.Single(lines);
        Assert.Equal(payload, line);
        Assert.DoesNotContain('�', line);
    }

    /// <summary>
    /// A chunk that decodes to NOTHING still has to reach the decoder — see <c>StdoutLineBuffer</c>
    /// for why returning early on a zero count loses the partial sequence.
    /// </summary>
    /// <remarks>
    /// Its own test rather than an assumed consequence of the theories above: the case is only
    /// reachable when a stream OPENS on a split character, which no split-offset arm reproduces.
    /// </remarks>
    [Fact]
    public void A_first_chunk_that_decodes_to_nothing_still_hands_its_bytes_to_the_decoder()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("é\n");
        var lines = new List<string>();
        var buffer = new StdoutLineBuffer();

        // One byte of a 2-byte sequence: decodes to zero characters, and the decoder must keep it.
        buffer.Append(bytes[..1], lines.Add);
        Assert.Empty(lines);

        buffer.Append(bytes[1..], lines.Add);
        Assert.Equal("é", Assert.Single(lines));
    }

    /// <summary>Text with no trailing newline is emitted on flush, not silently dropped.</summary>
    [Fact]
    public void Stdout_without_a_trailing_newline_is_emitted_when_the_stream_ends()
    {
        var lines = new List<string>();
        var buffer = new StdoutLineBuffer();
        buffer.Append(System.Text.Encoding.UTF8.GetBytes("no newline here"), lines.Add);
        Assert.Empty(lines);

        buffer.Flush(lines.Add);
        Assert.Equal("no newline here", Assert.Single(lines));

        // Flushing again emits nothing: the buffer was cleared, so a second flush cannot duplicate
        // the final line into the transcript.
        buffer.Flush(lines.Add);
        Assert.Single(lines);
    }

    /// <summary>
    /// #701: a worker that never writes a newline must not grow the buffer without ceiling. Past
    /// <see cref="StdoutLineBuffer.MaxBufferedLineLength"/> the held text is emitted as a synthetic
    /// line ending in <see cref="StdoutLineBuffer.SplitMarker"/>, with nothing dropped and nothing
    /// silent — the ceiling constant's own remarks carry the measurement and the design choice.
    /// </summary>
    [Fact]
    public void A_newline_free_run_past_the_ceiling_is_emitted_as_a_marked_synthetic_line()
    {
        const int overshoot = 10;
        var payload = new string('x', StdoutLineBuffer.MaxBufferedLineLength + overshoot);
        var lines = new List<string>();
        var buffer = new StdoutLineBuffer();

        buffer.Append(System.Text.Encoding.UTF8.GetBytes(payload), lines.Add);

        var synthetic = Assert.Single(lines);
        Assert.EndsWith(StdoutLineBuffer.SplitMarker, synthetic, StringComparison.Ordinal);
        Assert.Equal(StdoutLineBuffer.MaxBufferedLineLength, synthetic.Length - StdoutLineBuffer.SplitMarker.Length);

        buffer.Flush(lines.Add);
        Assert.Equal(2, lines.Count);

        // Nothing was lost: the split fabricated a boundary, never dropped a character.
        Assert.Equal(payload, string.Concat(synthetic[..^StdoutLineBuffer.SplitMarker.Length], lines[1]));
    }

    /// <summary>
    /// #701's review finding: a code point above U+FFFF is two UTF-16 chars, so a cut landing
    /// between them used to orphan half of it — the split loop's own comment in
    /// <see cref="StdoutLineBuffer"/> carries the corruption mechanism and the stderr precedent.
    /// This pins the back-off.
    /// </summary>
    [Fact]
    public void A_split_landing_inside_a_surrogate_pair_backs_off_rather_than_severing_it()
    {
        // Ceiling-minus-one ASCII chars, then an astral-plane character: the pair straddles the cut.
        var payload = new string('x', StdoutLineBuffer.MaxBufferedLineLength - 1) + "🚨tail";
        var lines = new List<string>();
        var buffer = new StdoutLineBuffer();

        buffer.Append(System.Text.Encoding.UTF8.GetBytes(payload), lines.Add);
        buffer.Flush(lines.Add);

        Assert.Equal(2, lines.Count);
        var fragment = lines[0][..^StdoutLineBuffer.SplitMarker.Length];

        // The cut backed off: the fragment ends before the pair, the remainder carries it whole.
        Assert.Equal(StdoutLineBuffer.MaxBufferedLineLength - 1, fragment.Length);
        Assert.StartsWith("🚨", lines[1], StringComparison.Ordinal);
        Assert.Equal(payload, fragment + lines[1]);

        // Round-trip proves no lone surrogate on either side — the same technique the stderr
        // buffer's own orphaned-surrogate test uses.
        foreach (var emitted in new[] { fragment, lines[1] })
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(emitted);
            Assert.Equal(emitted, System.Text.Encoding.UTF8.GetString(bytes));
        }
    }

    /// <summary>
    /// Polarity for the ceiling: text exactly AT it stays buffered (a legitimate long line keeps
    /// waiting for its newline), and the eventual real line carries no marker.
    /// </summary>
    [Fact]
    public void A_newline_free_run_at_the_ceiling_is_never_split()
    {
        var lines = new List<string>();
        var buffer = new StdoutLineBuffer();

        buffer.Append(System.Text.Encoding.UTF8.GetBytes(new string('x', StdoutLineBuffer.MaxBufferedLineLength)), lines.Add);
        Assert.Empty(lines);

        buffer.Append("\n"u8.ToArray(), lines.Add);
        var line = Assert.Single(lines);
        Assert.DoesNotContain(StdoutLineBuffer.SplitMarker, line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A worker killed mid-character leaves a partial sequence in the decoder, and flushing has to
    /// surface it as U+FFFD rather than drop it.
    /// </summary>
    /// <remarks>
    /// This is the end-of-stream axis, and it is the one a chunk-boundary test structurally cannot
    /// reach: no mutation of <c>Append</c> makes it red, which is exactly why the omission survived a
    /// mutation pass that turned five of ten arms red on the axis it was aimed at.
    /// <para>
    /// The second arm is the sharp one. With bytes left in the <c>StringBuilder</c> a missing drain
    /// merely truncates; with the partial sequence being ALL that remains, the buffer is empty, the
    /// length guard never fires, and the worker's last line vanishes — strictly worse than the
    /// stateless decode this replaced, on the diagnostic path where it matters most.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(new byte[] { 0x61, 0x62, 0xC3 }, "ab�", "a truncated sequence after text")]
    [InlineData(new byte[] { 0xC3 }, "�", "a truncated sequence and NOTHING else")]
    public void A_trailing_partial_sequence_is_surfaced_on_flush_rather_than_dropped(
        byte[] truncated, string expected, string what)
    {
        Assert.NotEmpty(what);

        var lines = new List<string>();
        var buffer = new StdoutLineBuffer();

        // The lead byte of é with its continuation byte never written — the worker died mid-write.
        buffer.Append(truncated, lines.Add);
        Assert.Empty(lines);

        buffer.Flush(lines.Add);
        Assert.Equal(expected, Assert.Single(lines));
    }

    /// <summary>
    /// The CONTROL for the theory above, on the path that already drained its decoder: identical
    /// input, and it has to stay green. Both going red would mean the harness misunderstands
    /// <c>Decoder</c> flushing rather than that stdout was dropping bytes.
    /// </summary>
    [Fact]
    public void Stderr_surfaces_a_trailing_partial_sequence_when_the_tail_is_read()
    {
        var tail = new StderrTailBuffer();
        tail.Append([0x61, 0x62, 0xC3]);

        Assert.Equal("ab�", tail.ToTailOrNull());
    }

    [Theory]
    // One offset interior to each of the three sequence lengths present, named by what it splits
    // rather than derived from the end of the array. The first version of this test computed all
    // three offsets from `bytes.Length - 4`, which put every one of them inside the same 4-byte
    // sequence while the comment claimed it covered three different lengths.
    [InlineData(1, "inside the 2-byte é")]
    [InlineData(3, "inside the 3-byte —")]
    [InlineData(6, "inside the 4-byte 🚨")]
    [InlineData(7, "inside the 4-byte 🚨, one byte later")]
    public void Stderr_decoding_survives_a_multi_byte_sequence_split_across_two_chunks(int splitAt, string what)
    {
        Assert.NotEmpty(what);

        // 9 UTF-8 bytes: é at [0,2), — at [2,5), 🚨 at [5,9).
        const string payload = "é—🚨";
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        Assert.Equal(9, bytes.Length);

        var tail = new StderrTailBuffer();
        tail.Append(bytes[..splitAt]);
        tail.Append(bytes[splitAt..]);

        Assert.Equal(payload, tail.ToTailOrNull());
        Assert.DoesNotContain('�', tail.ToTailOrNull()!);
    }

    /// <summary>
    /// Trimming to the tail cuts from the front, so it can orphan a low surrogate whose high half is
    /// inside the removed prefix — the mirror of the hazard
    /// <c>ContractValidator.TrimWithoutSplittingSurrogatePair</c> guards at the other end. An orphan
    /// is not a rendering nicety: it is an unpaired UTF-16 code unit that does not round-trip.
    /// </summary>
    [Fact]
    public void Trimming_stderr_to_the_tail_never_leaves_an_orphaned_low_surrogate()
    {
        // The trailing "x" is what makes this test discriminate, and it is not cosmetic. Without it
        // the buffer is 4000 chars of surrogate pairs, so `excess` is 4000 - 2000 = 2000 — an EVEN
        // index, which in a run of pairs is always the HIGH half. The guard tests for a LOW
        // surrogate, so it never fired and the test passed with the guard deleted. Nor is that fixable
        // by choosing a different repeat count: for a run of pairs the parity of `excess` follows the
        // parity of the cap, so an even cap always cuts on a high surrogate. One BMP character makes
        // the length odd, `excess` 2001, and the cut land on a low surrogate — the case the guard exists for.
        var buffer = new System.Text.StringBuilder(
            string.Concat(Enumerable.Repeat("🚨", CoreDispatcher.MaxRetainedStderrLength)) + "x");

        Assert.True(char.IsLowSurrogate(buffer[buffer.Length - CoreDispatcher.MaxRetainedStderrLength]),
            "payload does not put a low surrogate at the cut index, so the guard under test is never reached");

        StderrTailBuffer.TrimToTail(buffer);

        var trimmed = buffer.ToString();
        Assert.True(trimmed.Length <= CoreDispatcher.MaxRetainedStderrLength);
        Assert.False(char.IsLowSurrogate(trimmed[0]), "leading char is an orphaned low surrogate");

        // The real proof: an orphaned surrogate does not survive a UTF-8 round-trip, so this
        // comparison fails on any implementation that leaves one behind.
        var roundTripped = System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(trimmed));
        Assert.Equal(trimmed, roundTripped);
    }

    /// <summary>
    /// The regression test for the reason whitespace is collapsed at capture time rather than at
    /// render time. A worker that prints its diagnostic and then clears a progress display on the way
    /// out — enough trailing blank lines to fill the retention buffer — used to have its entire
    /// retained tail be whitespace, which then collapsed to nothing and produced the bare pre-#563
    /// reason. The feature silently did not fire in its own headline use case.
    /// </summary>
    [Fact]
    public void A_diagnostic_followed_by_enough_blank_lines_to_fill_the_buffer_still_survives()
    {
        var tail = new StderrTailBuffer();
        tail.Append(System.Text.Encoding.UTF8.GetBytes("Error: model not found"));

        // Comfortably more than MaxRetainedStderrLength, so a buffer that retained whitespace would
        // hold nothing else by the end.
        tail.Append(System.Text.Encoding.UTF8.GetBytes(new string('\n', CoreDispatcher.MaxRetainedStderrLength * 2)));

        Assert.Equal("Error: model not found", tail.ToTailOrNull());
    }

    /// <summary>
    /// The other half of the same defect. Whitespace collapsing used to run <i>between</i> the
    /// retention cap and the display cap, so the two caps measured different units: mostly-whitespace
    /// stderr could lose thousands of characters to the silent cap and still collapse to under the
    /// marked cap, showing a truncated tail with no ellipsis. Collapsing at capture time means the
    /// retained length is already in the units the display cap compares against.
    /// </summary>
    [Fact]
    public void Mostly_whitespace_stderr_is_retained_in_the_same_units_the_display_cap_measures()
    {
        var tail = new StderrTailBuffer();

        // Each line is one visible token in a wide field of padding — the shape of an indented stack
        // trace or a column-padded table. Raw length is far past the cap; collapsed length is not.
        for (var i = 0; i < 400; i++)
        {
            tail.Append(System.Text.Encoding.UTF8.GetBytes(new string(' ', 40) + $"line{i}\n"));
        }

        var captured = tail.ToTailOrNull();
        Assert.NotNull(captured);

        // No run of whitespace survives, so every retained character counts toward the same budget
        // the classifier's display cap will apply.
        Assert.DoesNotContain("  ", captured);
        Assert.True(
            captured.Length <= CoreDispatcher.MaxRetainedStderrLength,
            $"retained {captured.Length}, cap {CoreDispatcher.MaxRetainedStderrLength}");

        // And the retained content is long enough to reach the display cap, which is what makes the
        // truncation visible rather than silent.
        Assert.True(
            captured.Length > Baton.Outcomes.OutcomeClassifier.MaxStderrTailInReason,
            "collapsed tail must still exceed the display cap, or the marker never fires");
        Assert.EndsWith("line399", captured);
    }

    /// <summary>
    /// A whitespace run split across a chunk boundary must still collapse to one space. This is the
    /// reason <c>pendingSpace</c> is instance state rather than a local inside the per-chunk decode.
    /// </summary>
    [Fact]
    public void A_whitespace_run_split_across_chunks_collapses_to_a_single_space()
    {
        var tail = new StderrTailBuffer();
        tail.Append(System.Text.Encoding.UTF8.GetBytes("before  "));
        tail.Append(System.Text.Encoding.UTF8.GetBytes("  after"));

        Assert.Equal("before after", tail.ToTailOrNull());
    }

    private static CoreDispatchTarget WriteToStderrAndExit(string message, int exitCode) =>
        new("cmd", ["/c", $"echo {message} 1>&2 & exit {exitCode}"]);

    // Issue #598: an over-long command line is refused by AER, naming its size and the limit, rather
    // than reaching BatonTask and coming back as an OS-authored complaint about a filename.

    /// <summary>
    /// Pins the arithmetic the ceiling is compared against, so that a change to the accounting has to
    /// be a deliberate edit here rather than a silent shift in where the guard fires.
    /// </summary>
    [Fact]
    public void MeasureCommandLineLength_counts_the_program_its_arguments_and_their_separators()
    {
        // "prog" quoted (6) + " " + "ab" quoted (4) => 6 + 1 + 4 = 11, and again for the second arg.
        Assert.Equal(6, CoreDispatcher.MeasureCommandLineLength("prog", []));
        Assert.Equal(11, CoreDispatcher.MeasureCommandLineLength("prog", ["ab"]));
        Assert.Equal(16, CoreDispatcher.MeasureCommandLineLength("prog", ["ab", "cd"]));
    }

    /// <summary>
    /// The escape term is what makes the measure an upper bound rather than an approximation, and it
    /// is the whole reason a quote-dense prompt cannot slip past the ceiling into an OS-level failure.
    /// Without it every assertion here would be two characters short per escaped character.
    /// </summary>
    [Fact]
    public void MeasureCommandLineLength_charges_for_what_Windows_escaping_can_add()
    {
        // Same raw length in every case; only the escapable characters differ.
        Assert.Equal(11, CoreDispatcher.MeasureCommandLineLength("prog", ["ab"]));
        Assert.Equal(12, CoreDispatcher.MeasureCommandLineLength("prog", ["a\""]));
        Assert.Equal(12, CoreDispatcher.MeasureCommandLineLength("prog", ["a\\"]));
        Assert.Equal(13, CoreDispatcher.MeasureCommandLineLength("prog", ["\"\""]));

        // The program is charged the same way, not just the arguments.
        Assert.Equal(7, CoreDispatcher.MeasureCommandLineLength("pro\"", []));
    }

    /// <summary>
    /// The case review of #598 found: an argument whose raw characters sit comfortably under the
    /// ceiling but whose escaping pushes it past. Before the measure charged for escaping this was
    /// waved through and failed at the OS instead — a prompt quoting JSON, a schema, or a file's
    /// contents reaches this easily, so it is an ordinary case rather than a pathological one.
    /// </summary>
    [Fact]
    public void GuardCommandLineLength_refuses_an_argument_only_its_escaping_pushes_over()
    {
        const int ceiling = 100;
        var quoteDense = new string('"', 60);

        // Under the ceiling on raw characters alone (60 + 6 == 66), over it once escaping is charged.
        Assert.True(quoteDense.Length + 6 <= ceiling);
        Assert.True(CoreDispatcher.MeasureCommandLineLength("p", [quoteDense]) > ceiling);

        Assert.Throws<CommandLineTooLongException>(
            () => CoreDispatcher.GuardCommandLineLength("p", [quoteDense], ceiling));
    }

    /// <summary>
    /// The ceiling's own doc justifies the number as sitting below <c>CreateProcessW</c>'s documented
    /// maximum. Asserted rather than left to the comment, so raising it past the real limit fails here
    /// instead of silently turning the guard into a formality.
    /// </summary>
    [Fact]
    public void WindowsCommandLineCeiling_stays_below_the_documented_CreateProcessW_maximum()
    {
        Assert.True(
            CoreDispatcher.WindowsCommandLineCeiling < 32_767,
            $"The ceiling ({CoreDispatcher.WindowsCommandLineCeiling}) must stay below CreateProcessW's "
            + "documented 32,767-character lpCommandLine maximum.");
    }

    /// <summary>
    /// The two arms of the boundary, one character apart, which is the pair that makes either arm
    /// mean anything: a guard that throws on everything would pass the first assertion alone, and one
    /// that throws on nothing would pass the second alone.
    /// </summary>
    [Fact]
    public void GuardCommandLineLength_fires_one_character_past_the_ceiling_and_not_at_it()
    {
        const int ceiling = 100;

        // MeasureCommandLineLength("p", [arg]) == arg.Length + 6, so this lands exactly on the ceiling.
        var exactlyAtCeiling = new string('x', ceiling - 6);
        Assert.Equal(ceiling, CoreDispatcher.MeasureCommandLineLength("p", [exactlyAtCeiling]));
        CoreDispatcher.GuardCommandLineLength("p", [exactlyAtCeiling], ceiling);

        var oneOver = exactlyAtCeiling + "x";
        Assert.Equal(ceiling + 1, CoreDispatcher.MeasureCommandLineLength("p", [oneOver]));
        Assert.Throws<CommandLineTooLongException>(
            () => CoreDispatcher.GuardCommandLineLength("p", [oneOver], ceiling));
    }

    /// <summary>
    /// The whole point of the issue is the message, not the throw: an operator who cannot see how big
    /// the prompt was, and how big it was allowed to be, is no better off than with the OS error.
    /// </summary>
    [Fact]
    public void GuardCommandLineLength_names_the_program_the_measured_size_the_ceiling_and_the_longest_argument()
    {
        const int ceiling = 100;
        var longest = new string('x', 200);

        var exception = Assert.Throws<CommandLineTooLongException>(
            () => CoreDispatcher.GuardCommandLineLength("agy", ["-p", longest], ceiling));

        // Anchored to the surrounding words, not bare numbers: three Contains on "213"/"100"/"200"
        // alone would still pass if the message printed the same figures in swapped roles.
        var measured = CoreDispatcher.MeasureCommandLineLength("agy", ["-p", longest])
            .ToString(CultureInfo.InvariantCulture);
        Assert.Contains("'agy'", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"about {measured} characters", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"past the {ceiling.ToString(CultureInfo.InvariantCulture)}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("longest single argument is 200 characters", exception.Message, StringComparison.Ordinal);
        // Pins the file-passing pointer the refusal now carries (#932).
        Assert.Contains("as a file it reads under its read-files grant", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard claims a limit measured against #579's <c>Win32Exception (206)</c> -- Windows-only
    /// (#1405), so this is always <see cref="CoreDispatcher.WindowsCommandLineCeiling"/>.
    /// </summary>
    [Fact]
    public void PlatformCommandLineCeiling_equals_the_Windows_ceiling()
    {
        Assert.Equal(CoreDispatcher.WindowsCommandLineCeiling, CoreDispatcher.PlatformCommandLineCeiling);
    }

    /// <summary>
    /// The end-to-end arm: the guard is actually wired into <see cref="CoreDispatcher.DispatchAsync"/>
    /// and refuses before <c>BatonTask</c> is reached. The boundary itself is covered on every platform
    /// by the tests above, which pass their own ceiling in.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_refuses_an_over_long_command_line_before_spawning()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var oversizedPrompt = new string('x', CoreDispatcher.WindowsCommandLineCeiling + 1_000);

            // "exit 0" would succeed if it ever ran, so a passing assertion here cannot come from the
            // command failing for some unrelated reason of its own.
            var target = new CoreDispatchTarget("cmd", ["/c", "exit 0", oversizedPrompt]);

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var exception = await Assert.ThrowsAsync<CommandLineTooLongException>(
                () => dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken));
            Assert.Contains(
                CoreDispatcher.WindowsCommandLineCeiling.ToString(CultureInfo.InvariantCulture),
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    /// <summary>
    /// The control for the test above: the identical target with an ordinary-sized argument dispatches
    /// normally. Without this, a guard that refused every dispatch outright would still look correct.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_dispatches_normally_when_the_command_line_is_within_the_ceiling()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var ordinaryPrompt = new string('x', 1_000);
            var target = new CoreDispatchTarget("cmd", ["/c", "exit 0", ordinaryPrompt]);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer, writer)
                .DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(CoreExitReason.Natural, result.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    /// <summary>
    /// Pins the deliberate ordering: the guard is measured after #292's prompt capture, so the
    /// artifact showing how the prompt got that large survives the refusal. Reversing the two would
    /// withhold the evidence for precisely the failure being reported, and nothing else would notice.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_still_captures_the_prompt_when_the_command_line_guard_fires()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var oversizedPrompt = new string('x', CoreDispatcher.WindowsCommandLineCeiling + 1_000);
            var target = new CoreDispatchTarget("cmd", ["/c", "exit 0", oversizedPrompt])
                with
            { PromptText = oversizedPrompt };

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            await Assert.ThrowsAsync<CommandLineTooLongException>(
                () => dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken));

            var promptFilePath = Path.Combine(outputDirectory, ArtifactManager.PromptFileName);
            Assert.True(File.Exists(promptFilePath));
            Assert.Equal(
                oversizedPrompt.Length,
                (await File.ReadAllTextAsync(promptFilePath, TestContext.Current.CancellationToken)).Length);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    [Fact]
    public async Task DispatchAsync_when_prompt_below_threshold_does_not_swap_wrapper_and_no_BATON_PROMPT_FILE_in_child_env()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var prompt = new string('a', CoreDispatcher.OversizePromptThreshold - 100);
            var wrapper = "Read prompt at %BATON_PROMPT_FILE%";

            var target = new CoreDispatchTarget("cmd", ["/c", "echo %BATON_PROMPT_FILE% > %BATON_OUTPUT_DIR%\\hello.txt", prompt], PromptText: prompt, OversizePromptWrapper: wrapper);

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var written = (await File.ReadAllTextAsync(Path.Combine(outputDirectory, "hello.txt"), TestContext.Current.CancellationToken)).Trim();
            Assert.DoesNotContain("prompt.txt", written);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    [Fact]
    public async Task DispatchAsync_when_prompt_above_threshold_with_wrapper_swaps_wrapper_and_sets_BATON_PROMPT_FILE()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));

            var baseCmd = "echo %BATON_PROMPT_FILE% > %BATON_OUTPUT_DIR%\\hello.txt";
            var oversizedPrompt = baseCmd + new string(' ', CoreDispatcher.WindowsCommandLineCeiling + 1_000);
            var wrapper = baseCmd;
            var promptFilePath = Path.Combine(outputDirectory, ArtifactManager.PromptFileName);

            var target = new CoreDispatchTarget("cmd", ["/c", oversizedPrompt], PromptText: oversizedPrompt, OversizePromptWrapper: wrapper);

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(promptFilePath));
            var writtenPrompt = await File.ReadAllTextAsync(promptFilePath, TestContext.Current.CancellationToken);
            Assert.True(writtenPrompt.Length >= CoreDispatcher.OversizePromptThreshold);
            Assert.Contains(outputDirectory, writtenPrompt);

            var written = (await File.ReadAllTextAsync(Path.Combine(outputDirectory, "hello.txt"), TestContext.Current.CancellationToken)).Trim();
            Assert.Equal(promptFilePath, written);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    [Fact]
    public async Task DispatchAsync_when_prompt_above_threshold_with_null_wrapper_throws_CommandLineTooLongException()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var oversizedPrompt = new string('x', CoreDispatcher.WindowsCommandLineCeiling + 1_000);

            var target = new CoreDispatchTarget("cmd", ["/c", "exit 0", oversizedPrompt], PromptText: oversizedPrompt, OversizePromptWrapper: null);

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            await Assert.ThrowsAsync<CommandLineTooLongException>(
                () => dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            FileCleanup.Delete(logPath);
        }
    }

    [Fact]
    public async Task DispatchAsync_when_prompt_above_threshold_and_BATON_OUTPUT_DIR_unresolved_does_not_swap()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var request = MakeRequest([]);
            var oversizedPrompt = new string('x', CoreDispatcher.WindowsCommandLineCeiling + 1_000);
            var wrapper = "Read prompt at %BATON_PROMPT_FILE%";

            var target = new CoreDispatchTarget("cmd", ["/c", "exit 0", oversizedPrompt], PromptText: oversizedPrompt, OversizePromptWrapper: wrapper);

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            await Assert.ThrowsAsync<CommandLineTooLongException>(
                () => dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken));
        }
        finally
        {
            FileCleanup.Delete(logPath);
        }
    }

    /// <summary>
    /// The assembled child environment is exactly what WithEnv applies to the spawned process. Asserts
    /// the source order AssembleChildEnvironment documents, and that a PassThrough variable (its value
    /// resolved elsewhere) is excluded.
    /// </summary>
    [Fact]
    public void AssembleChildEnvironment_orders_inherited_then_computed_then_target_and_drops_passthrough()
    {
        var request = MakeRequest(
        [
            new EnvironmentVariable.BatonComputed("BATON_COMPUTED_X", "cval"),
            new EnvironmentVariable.PassThrough("SOME_PASSTHROUGH"),
        ]);
        var target = new CoreDispatchTarget("sh", ["-c", "true"], Environment: [("TARGET_Y", "tval")]);

        var environment = CoreDispatcher.AssembleChildEnvironment(request, target);
        var names = environment.Select(e => e.Name).ToList();

        Assert.Contains(("BATON_COMPUTED_X", "cval"), environment);
        Assert.Contains(("TARGET_Y", "tval"), environment);
        Assert.DoesNotContain("SOME_PASSTHROUGH", names);

        // PATH is an InheritedEnvironment entry set in every process, so it stands in for "the inherited
        // allowlist came first". Then computed, then target -- the override order WithEnv depends on.
        Assert.Contains("PATH", names);
        Assert.True(names.IndexOf("PATH") < names.IndexOf("BATON_COMPUTED_X"));
        Assert.True(names.IndexOf("BATON_COMPUTED_X") < names.IndexOf("TARGET_Y"));
    }

    /// <summary>
    /// Target environment VALUES go through the same placeholder expansion as target arguments
    /// (#442's per-execution agy home is the first consumer). Polarity: a token naming a computed
    /// variable expands; a token naming anything else survives byte-for-byte, so a value that
    /// legitimately contains such text is not silently rewritten.
    /// </summary>
    [Fact]
    public void AssembleChildEnvironment_expands_computed_placeholders_in_target_values_and_leaves_unknown_tokens_alone()
    {
        var request = MakeRequest([new EnvironmentVariable.BatonComputed("BATON_OUTPUT_DIR", "/task/out")]);
        var target = new CoreDispatchTarget("sh", ["-c", "true"], Environment:
        [
            ("EXPANDED", "$BATON_OUTPUT_DIR/.gemini_home"),
            ("EXPANDED_WIN", "%BATON_OUTPUT_DIR%\\.gemini_home"),
            ("UNTOUCHED", "$NOT_A_COMPUTED_VAR/%ALSO_NOT%"),
        ]);

        var environment = CoreDispatcher.AssembleChildEnvironment(request, target);

        Assert.Contains(("EXPANDED", "/task/out/.gemini_home"), environment);
        Assert.Contains(("EXPANDED_WIN", "/task/out\\.gemini_home"), environment);
        Assert.Contains(("UNTOUCHED", "$NOT_A_COMPUTED_VAR/%ALSO_NOT%"), environment);
    }

    // #1084: a seed body is frequently JSON, and AER-computed variables are absolute paths whose raw
    // Windows value carries backslashes. RenderSeedContent forward-slashes the substituted value so the
    // body stays valid JSON. The control arm proves the raw substitution would break it -- without the
    // discriminator the green test would be about nothing.
    [Fact]
    public void RenderSeedContent_forward_slashes_substituted_values_so_a_json_body_stays_valid()
    {
        var vars = new Dictionary<string, string> { ["BATON_OUTPUT_DIR"] = @"C:\Users\me\out\exec_1" };
        var template = """{"permissions":{"allow":["write_file(%BATON_OUTPUT_DIR%/advice.md)"]}}""";

        var rendered = CoreDispatcher.RenderSeedContent(template, vars);

        using var doc = JsonDocument.Parse(rendered);
        var rule = doc.RootElement.GetProperty("permissions").GetProperty("allow")[0].GetString();
        Assert.Equal("write_file(C:/Users/me/out/exec_1/advice.md)", rule);
    }

    [Fact]
    public void Raw_backslash_substitution_would_void_the_json_body()
    {
        // The control: substituting the backslashed value verbatim yields `C:\U…`, an invalid JSON
        // escape. This is the failure RenderSeedContent's forward-slashing exists to prevent.
        var vars = new Dictionary<string, string> { ["BATON_OUTPUT_DIR"] = @"C:\Users\me\out\exec_1" };
        var template = """{"permissions":{"allow":["write_file(%BATON_OUTPUT_DIR%/advice.md)"]}}""";

        var naive = template.Replace("%BATON_OUTPUT_DIR%", vars["BATON_OUTPUT_DIR"], StringComparison.Ordinal);

        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(naive));
    }

    // #1373: CoreDispatchTarget.WithPromptPreamble.

    [Fact]
    public void WithPromptPreamble_rewrites_the_spawned_argument_and_not_only_the_archived_prompt()
    {
        var target = new CoreDispatchTarget("claude", ["-p", "do the work", "--model", "opus"], PromptText: "do the work");

        var prefixed = target.WithPromptPreamble("BRIEF: finish, do not restart.\n\n");

        // Both, and the ARGUMENT above all: PromptText only reaches prompt.txt, which the dispatcher
        // writes for display and never reads back to route. A preamble that landed there alone would
        // be visible to a person reading the artifact and invisible to the worker it was written for.
        Assert.Equal("BRIEF: finish, do not restart.\n\ndo the work", prefixed.Args[1]);
        Assert.Equal(prefixed.Args[1], prefixed.PromptText);
        // Every other argument is untouched, and the prompt stays at its own index.
        Assert.Equal(["-p", prefixed.Args[1], "--model", "opus"], prefixed.Args);
    }

    [Fact]
    public void WithPromptPreamble_is_a_no_op_for_an_adapter_with_no_prose_prompt()
    {
        // CommandWorkerAdapter's shape: a declared argv with nothing prompt-like to prepend to.
        var target = new CoreDispatchTarget("pixi", ["run", "gates"]);

        Assert.Same(target, target.WithPromptPreamble("BRIEF: finish, do not restart.\n\n"));
    }

    [Fact]
    public void WithPromptPreamble_refuses_a_target_whose_prompt_is_not_one_of_its_arguments()
    {
        // The invariant this method and the #748 oversize swap both rest on — see
        // WithPromptPreamble's own doc for why a break in it throws instead of degrading quietly.
        var target = new CoreDispatchTarget("claude", ["-p", "--prompt=do the work"], PromptText: "do the work");

        var ex = Assert.Throws<PromptPreambleException>(
            () => target.WithPromptPreamble("BRIEF: finish, do not restart.\n\n"));
        Assert.Contains("no argument", ex.Message, StringComparison.Ordinal);
    }
}
