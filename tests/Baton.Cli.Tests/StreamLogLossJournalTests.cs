using System.Text;
using Baton.Artifacts;
using Baton.Cli.Tests.TestSupport;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1885. The two-channel rule for a declared stream-log loss, exercised. The rule itself, and the
/// condition every arm here reproduces, are <c>spec/baton.md</c> §3's — read it first; each arm below
/// says only what it does to that condition and what must follow.
/// <para>
/// The control arm throughout is a failure the retry buffer ABSORBS: nothing was lost, so nothing is
/// journalled and no reason appears. Without it these fixtures would only prove that a callback can be
/// invoked, not that it is invoked for the right reason — and #1879's own marker is deliberately absent
/// in exactly the same case, so the two channels have to agree on the negative too.
/// </para>
/// </summary>
[Collection(ConsoleErrorCaptureCollection.Name)]
public sealed class StreamLogLossJournalTests
{
    /// <summary>
    /// The #1876/#1879 shape, deterministic: every append refuses. Non-failing calls do the real append,
    /// so the surviving bytes below are file contents rather than a recording of intended writes.
    /// </summary>
    private sealed class RefusingAppender(int failuresToInject)
    {
        private int _remaining = failuresToInject;

        public void Heal() => _remaining = 0;

        public void Append(string path, byte[] data)
        {
            if (_remaining > 0)
            {
                _remaining--;
                throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
            }

            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.Write(data, 0, data.Length);
            fs.Flush();
        }
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string NewDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"stream-loss-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Makes the write-failure marker permanently uncreatable without touching the appends: a DIRECTORY
    /// at the marker's own path. <c>File.Exists</c> reads false, <c>File.WriteAllBytes</c> throws
    /// <see cref="UnauthorizedAccessException"/>, and <c>TryWriteMarker</c> catches exactly that and
    /// returns false. Deterministic and account-independent, unlike ACL manipulation, which would not
    /// survive a different CI identity.
    /// </summary>
    private static void BlockMarker(string outputDirectory) =>
        Directory.CreateDirectory(Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutWriteFailureMarkerFileName));

    private static bool MarkerLanded(string outputDirectory) =>
        File.Exists(Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutWriteFailureMarkerFileName));

    [Fact]
    public void A_loss_whose_marker_can_never_land_is_reported_once_when_declared_and_again_at_terminal()
    {
        var dir = NewDir("declared");
        try
        {
            BlockMarker(dir);
            var reports = new List<ExecutionStreamLogger.StreamLogLoss>();
            var appender = new RefusingAppender(failuresToInject: int.MaxValue);

            // maxPendingBytes: 0 surrenders on the first failure -- the same control the #1876 fixtures
            // use to reproduce the pre-buffer drop exactly.
            var logger = new ExecutionStreamLogger(
                dir, maxPendingBytes: 0, appendBytes: appender.Append, onLossDeclared: reports.Add);
            logger.AppendStdout(Bytes("lost\n"));
            logger.AppendStdout(Bytes("lost too\n"));
            logger.AppendStdout(Bytes("and again\n"));
            logger.MarkTerminal();

            Assert.False(MarkerLanded(dir));

            // Exactly two, not one per failed chunk: the declaration is a false->true transition, and
            // DeclareWriteLoss re-runs on every later chunk once the latch is set.
            var stdout = reports.Where(r => r.StreamName == ExecutionStreamLogger.StdoutStreamName).ToList();
            Assert.Equal(2, stdout.Count);

            Assert.False(stdout[0].TerminalReannouncement);
            Assert.False(stdout[0].MarkerWritten);
            Assert.Equal(Bytes("lost\n").Length, stdout[0].BytesSurrendered);

            Assert.True(stdout[1].TerminalReannouncement);
            Assert.False(stdout[1].MarkerWritten);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    [Fact]
    public void A_failure_the_retry_buffer_absorbs_reports_nothing_at_all()
    {
        // THE CONTROL. #1879's marker is absent here because nothing was lost, and the journal channel
        // must be silent for the identical reason -- otherwise every transient AV hold on a healthy
        // dispatch would journal a permanent "this stream has a gap" the projector then acts on.
        var dir = NewDir("absorbed");
        try
        {
            BlockMarker(dir);
            var reports = new List<ExecutionStreamLogger.StreamLogLoss>();
            var appender = new RefusingAppender(failuresToInject: 1);

            var logger = new ExecutionStreamLogger(dir, appendBytes: appender.Append, onLossDeclared: reports.Add);
            logger.AppendStdout(Bytes("first\n"));
            logger.AppendStdout(Bytes("second\n"));
            logger.MarkTerminal();

            Assert.Empty(reports);
            // And the bytes really did survive -- proving the absorb happened rather than the failure
            // never firing, which would make the empty report list meaningless.
            var written = File.ReadAllText(Path.Combine(dir, ExecutionStreamLogger.StdoutLogFileName));
            Assert.Equal("first\nsecond\n", written);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    [Fact]
    public void A_loss_whose_marker_does_land_is_reported_once_and_not_re_announced_at_terminal()
    {
        // The other polarity of the arm above: the file channel worked, so the terminal report -- whose
        // whole content is "the file channel never carried this" -- would be a false alarm.
        var dir = NewDir("marker-lands");
        try
        {
            var reports = new List<ExecutionStreamLogger.StreamLogLoss>();
            var appender = new RefusingAppender(failuresToInject: 1);

            var logger = new ExecutionStreamLogger(
                dir, maxPendingBytes: 0, appendBytes: appender.Append, onLossDeclared: reports.Add);
            logger.AppendStdout(Bytes("lost\n"));
            appender.Heal();
            logger.AppendStdout(Bytes("kept\n"));
            logger.MarkTerminal();

            Assert.True(MarkerLanded(dir));
            var stdout = Assert.Single(reports, r => r.StreamName == ExecutionStreamLogger.StdoutStreamName);
            Assert.False(stdout.TerminalReannouncement);
            // True on the report itself: the marker create is attempted as part of declaring the loss,
            // and only the APPENDS were being refused here — a marker file in an otherwise writable
            // directory lands on the first try. That is what makes the terminal re-announcement silent.
            Assert.True(stdout.MarkerWritten);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    [Fact]
    public void An_initialization_failure_reports_both_streams()
    {
        // #1879 review HIGH 2's largest gap: the logger never opened, so the capture is empty rather
        // than partial and no append will ever run to carry a retry. Both streams are declared, with no
        // byte count -- see StreamLogLoss.BytesSurrendered for why null and not zero.
        var dir = NewDir("init-failure");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ExecutionStreamLogger.StdoutLogFileName));
            BlockMarker(dir);
            var reports = new List<ExecutionStreamLogger.StreamLogLoss>();

            var logger = new ExecutionStreamLogger(dir, onLossDeclared: reports.Add);
            logger.MarkTerminal();

            Assert.Contains(reports, r => r.StreamName == ExecutionStreamLogger.StdoutStreamName && !r.TerminalReannouncement);
            Assert.Contains(reports, r => r.StreamName == ExecutionStreamLogger.StderrStreamName && !r.TerminalReannouncement);
            Assert.All(reports.Where(r => !r.TerminalReannouncement), r => Assert.Null(r.BytesSurrendered));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    // ---- the projector's half: the two-channel rule ----------------------------------------------

    private const string ClaudeTerminalLine =
        """{"type":"result","num_turns":2,"usage":{"input_tokens":1,"output_tokens":2,"cache_creation_input_tokens":3},"modelUsage":{"claude-opus-5":{"inputTokens":1000,"outputTokens":500,"cacheReadInputTokens":9000,"cacheCreationInputTokens":4000}}}""";

    private const string ClaudeAssistantLine =
        """{"type":"assistant","message":{"id":"msg_1","usage":{"input_tokens":2,"cache_creation_input_tokens":700,"cache_read_input_tokens":0,"output_tokens":3}}}""";

    private const string WriteFailureReason = "stream-truncated-by-write-failure";

    /// <summary>
    /// One execution, projected. <paramref name="journalledLoss"/> is the #1885 channel and
    /// <paramref name="marker"/> the #1879 one; either, both, or neither may be present, which is the
    /// whole cross-product this file pins.
    /// </summary>
    private static ExecutionUsageView Project(
        string testRoot,
        ExecutionId executionId,
        IReadOnlyList<string>? stdoutLines = null,
        bool marker = false,
        bool rolloverMarker = false,
        FlowEvent.StreamLogLossDeclared? journalledLoss = null,
        WorkerUsage? arrestedUsage = null)
    {
        var start = DateTime.UtcNow;
        WriteBindings(testRoot, "plan", "claude");

        var entries = new List<LogEntry>
        {
            new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "plan"))),
            new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
            new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),
        };

        if (arrestedUsage is not null)
        {
            entries.Insert(1, new LogEntry.FlowLogEntry(new FlowEvent.ExecutionArrested(executionId, arrestedUsage)));
        }

        if (journalledLoss is not null)
        {
            entries.Insert(1, new LogEntry.FlowLogEntry(journalledLoss));
        }

        var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
        Directory.CreateDirectory(outputDir);
        if (marker)
        {
            File.WriteAllBytes(Path.Combine(outputDir, ExecutionStreamLogger.StdoutWriteFailureMarkerFileName), []);
        }

        if (rolloverMarker)
        {
            // #1888: #1876's OTHER empty sentinel, the one a stream that rolled twice leaves. Both can
            // exist at once, which is the case the arms below are about.
            File.WriteAllBytes(Path.Combine(outputDir, ExecutionStreamLogger.StdoutTruncationMarkerFileName), []);
        }

        if (stdoutLines is not null)
        {
            File.WriteAllLines(Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName), stdoutLines);
        }

        return Assert.Single(
            ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot)).Value;
    }

    [Fact]
    public void The_journalled_event_alone_carries_the_reason_when_no_file_channel_survived()
    {
        // THE ISSUE. No .stdout.log, no marker -- the state spec/baton.md §3's obstructed host leaves
        // behind. Before
        // #1885 this reported wall-clock and nothing else, indistinguishable from a worker that emitted
        // nothing, which has a different remedy.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1885-journal-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1885-journal-only");
            var view = Project(
                testRoot,
                executionId,
                journalledLoss: new FlowEvent.StreamLogLossDeclared(
                    executionId, ExecutionStreamLogger.StdoutStreamName, WriteFailureReason, 4096, MarkerLanded: false));

            Assert.Equal(WriteFailureReason, view.BilledReconciliationUnavailable);
            Assert.Null(view.BilledTokens);
            Assert.Null(view.LiveBilledTokens);
            Assert.Null(view.BilledUnderReadTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void Neither_channel_present_leaves_the_reason_absent()
    {
        // The discriminating control for the arm above: same empty directory, no event. If this also
        // reported a reason, the arm above would be measuring the absence of a stream file rather than
        // the presence of the journalled loss.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1885-neither-{Guid.NewGuid():N}");
        try
        {
            var view = Project(testRoot, new ExecutionId("exec-1885-neither"));

            Assert.Null(view.BilledReconciliationUnavailable);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void The_marker_alone_still_carries_the_reason()
    {
        // #1876's channel, unchanged by #1885 and pinned here so that "the marker stays for readers that
        // only have the execution directory" is a checked claim rather than a sentence in a PR body.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1885-marker-{Guid.NewGuid():N}");
        try
        {
            var view = Project(
                testRoot,
                new ExecutionId("exec-1885-marker-only"),
                stdoutLines: [ClaudeAssistantLine, ClaudeTerminalLine],
                marker: true);

            Assert.Equal(WriteFailureReason, view.BilledReconciliationUnavailable);
            Assert.Null(view.BilledTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void Both_channels_agreeing_yield_one_reason_and_no_stderr_line()
    {
        // The ordinary shipped case once #1885 lands: the loss is announced twice, and a consumer sees
        // one reason, not two and not a duplicate. Paired with the disagreement arm below -- an
        // agreement that printed a warning would make that arm prove nothing.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1885-both-{Guid.NewGuid():N}");
        var originalError = Console.Error;
        using var stderr = new StringWriter();
        try
        {
            Console.SetError(stderr);
            var executionId = new ExecutionId("exec-1885-both-agree");
            var view = Project(
                testRoot,
                executionId,
                stdoutLines: [ClaudeAssistantLine, ClaudeTerminalLine],
                marker: true,
                journalledLoss: new FlowEvent.StreamLogLossDeclared(
                    executionId, ExecutionStreamLogger.StdoutStreamName, WriteFailureReason, 4096, MarkerLanded: true));

            Console.SetError(originalError);

            Assert.Equal(WriteFailureReason, view.BilledReconciliationUnavailable);
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void Two_channels_that_disagree_are_reported_on_stderr_never_silently_resolved()
    {
        // Synthetic by construction: today's writer only ever journals the write-failure literal, so a
        // journalled ROLLOVER reason means a hand-edited ledger, a mis-keyed execution id, or a future
        // third producer -- WarnOnChannelDisagreement's own doc enumerates them, and #1888 corrected
        // what that doc used to claim was unreachable. Here: the two reasons differ, so the event wins
        // AND the mismatch is stated, rather than the loser being dropped in silence.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1885-disagree-{Guid.NewGuid():N}");
        var originalError = Console.Error;
        using var stderr = new StringWriter();
        try
        {
            Console.SetError(stderr);
            var executionId = new ExecutionId("exec-1885-disagree");
            var view = Project(
                testRoot,
                executionId,
                stdoutLines: [ClaudeAssistantLine, ClaudeTerminalLine],
                marker: true,
                journalledLoss: new FlowEvent.StreamLogLossDeclared(
                    executionId, ExecutionStreamLogger.StdoutStreamName, "stream-truncated-by-rollover"));

            Console.SetError(originalError);

            // Event first, and the disagreement is stated rather than resolved in silence.
            Assert.Equal("stream-truncated-by-rollover", view.BilledReconciliationUnavailable);
            var written = stderr.ToString();
            Assert.Contains("disagree", written, StringComparison.Ordinal);
            Assert.Contains(WriteFailureReason, written, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void Both_markers_present_with_the_journalled_write_failure_agree_and_print_nothing()
    {
        // #1888. The false positive the reorder closes: a long lane that announced BOTH of
        // spec/baton.md §3's gaps -- the rollover marker and the write-failure marker, the latter also
        // journalled. Two truthful channels, one fact each -- and while the
        // rollover marker was read first, the projector compared the rollover reason against the
        // write-failure event and printed a disagreement that was not one. This arm fails on the
        // pre-#1888 order in both directions: the warning appears AND the reported reason is rollover.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1888-both-markers-{Guid.NewGuid():N}");
        var originalError = Console.Error;
        using var stderr = new StringWriter();
        try
        {
            Console.SetError(stderr);
            var executionId = new ExecutionId("exec-1888-both-markers");
            var view = Project(
                testRoot,
                executionId,
                stdoutLines: [ClaudeAssistantLine, ClaudeTerminalLine],
                marker: true,
                rolloverMarker: true,
                journalledLoss: new FlowEvent.StreamLogLossDeclared(
                    executionId, ExecutionStreamLogger.StdoutStreamName, WriteFailureReason, 4096, MarkerLanded: true));

            Console.SetError(originalError);

            Assert.Equal(WriteFailureReason, view.BilledReconciliationUnavailable);
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void Both_markers_and_no_journalled_event_report_the_write_failure_reason()
    {
        // The precedence the arm above rests on, pinned on its own so it is not merely a side effect of
        // an agreement test -- spec/baton.md §3 states this ranking (host obstruction outranks the
        // expected cost of the retention ceiling), and the ranking is what a file-channel-only reader
        // sees. Also the control for the reorder: with no event at all, the marker read alone decides.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1888-markers-only-{Guid.NewGuid():N}");
        try
        {
            var view = Project(
                testRoot,
                new ExecutionId("exec-1888-markers-only"),
                stdoutLines: [ClaudeAssistantLine, ClaudeTerminalLine],
                marker: true,
                rolloverMarker: true);

            Assert.Equal(WriteFailureReason, view.BilledReconciliationUnavailable);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void The_rollover_marker_alone_still_reports_the_rollover_reason()
    {
        // The discriminating control for the two arms above: without this, a reorder that reported
        // "write-failure" for EVERY truncated stream would pass both of them, and #1876's two reason
        // strings -- kept apart because their remedies differ -- would have quietly become one.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1888-rollover-only-{Guid.NewGuid():N}");
        try
        {
            var view = Project(
                testRoot,
                new ExecutionId("exec-1888-rollover-only"),
                stdoutLines: [ClaudeAssistantLine, ClaudeTerminalLine],
                rolloverMarker: true);

            Assert.Equal("stream-truncated-by-rollover", view.BilledReconciliationUnavailable);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void A_journalled_loss_withholds_the_triple_even_when_the_surviving_bytes_still_reconcile()
    {
        // The half a reason-string-only implementation gets wrong. The marker never landed, so the file
        // channel says nothing and the surviving tail parses cleanly -- both a terminal figure and a
        // replay Σ are computable. Serving them would contradict the suppression half of the
        // spec/baton.md §3 rule.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1885-suppress-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1885-suppress");
            var view = Project(
                testRoot,
                executionId,
                stdoutLines: [ClaudeAssistantLine, ClaudeTerminalLine],
                journalledLoss: new FlowEvent.StreamLogLossDeclared(
                    executionId, ExecutionStreamLogger.StdoutStreamName, WriteFailureReason));

            Assert.Equal(WriteFailureReason, view.BilledReconciliationUnavailable);
            Assert.Null(view.BilledTokens);
            Assert.Null(view.LiveBilledTokens);
            Assert.Null(view.BilledUnderReadTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void The_same_stream_without_the_journalled_loss_does_reconcile()
    {
        // The polarity control for the arm above -- identical bytes, no event. Without it that arm could
        // not tell "the event suppressed the triple" from "these fixtures never reconcile anyway".
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1885-reconciles-{Guid.NewGuid():N}");
        try
        {
            var view = Project(
                testRoot,
                new ExecutionId("exec-1885-reconciles"),
                stdoutLines: [ClaudeAssistantLine, ClaudeTerminalLine]);

            Assert.Equal(1000 + 500 + 4000, view.BilledTokens);
            Assert.Equal(700, view.LiveBilledTokens);
            Assert.Null(view.BilledReconciliationUnavailable);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void A_journalled_stderr_loss_does_not_touch_the_billed_reconciliation()
    {
        // Scope of the claim, per spec/baton.md §3's stream scoping: reporting a reason here would
        // withhold a sound
        // reconciliation over a stream that never lost a byte.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1885-stderr-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1885-stderr-loss");
            var view = Project(
                testRoot,
                executionId,
                stdoutLines: [ClaudeAssistantLine, ClaudeTerminalLine],
                journalledLoss: new FlowEvent.StreamLogLossDeclared(
                    executionId, ExecutionStreamLogger.StderrStreamName, WriteFailureReason));

            Assert.Null(view.BilledReconciliationUnavailable);
            Assert.Equal(1000 + 500 + 4000, view.BilledTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void A_journalled_loss_leaves_the_arrest_dimension_fallback_intact()
    {
        // spec/baton.md §3's stated shape for a lost stream: dimensions present, triple absent, reason
        // set. The in-memory arrest reading never went near the disk, so the host that erased the
        // capture cannot erase it -- and #1885's suppression of the live figure must not reach it
        // either.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1885-arrest-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1885-arrest");
            var view = Project(
                testRoot,
                executionId,
                journalledLoss: new FlowEvent.StreamLogLossDeclared(
                    executionId, ExecutionStreamLogger.StdoutStreamName, WriteFailureReason),
                arrestedUsage: new WorkerUsage(TokensIn: 11, TokensOut: 22, Turns: 3));

            Assert.Equal(WriteFailureReason, view.BilledReconciliationUnavailable);
            Assert.Equal(11, view.TokensIn);
            Assert.Equal(22, view.TokensOut);
            Assert.Equal(3, view.Turns);
            Assert.Null(view.BilledTokens);
            Assert.Null(view.LiveBilledTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void A_journalled_stdout_loss_suppresses_the_observed_billed_token_floor_with_no_terminal_line()
    {
        // #2559: existing loss authority stays strict for the new field too -- a declared stdout loss
        // withholds the floor exactly as it withholds the triple, even on the shape (no terminal line
        // at all) the floor otherwise targets.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-2559-stdout-loss-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-2559-stdout-loss");
            var view = Project(
                testRoot,
                executionId,
                stdoutLines: [ClaudeAssistantLine],
                journalledLoss: new FlowEvent.StreamLogLossDeclared(
                    executionId, ExecutionStreamLogger.StdoutStreamName, WriteFailureReason));

            Assert.Equal(WriteFailureReason, view.BilledReconciliationUnavailable);
            Assert.Null(view.ObservedBilledTokenFloor);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void A_journalled_stderr_loss_does_not_suppress_the_observed_billed_token_floor()
    {
        // The positive control: scope stays stdout-only, same as the triple's own stderr-loss control.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-2559-stderr-loss-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-2559-stderr-loss");
            var view = Project(
                testRoot,
                executionId,
                stdoutLines: [ClaudeAssistantLine],
                journalledLoss: new FlowEvent.StreamLogLossDeclared(
                    executionId, ExecutionStreamLogger.StderrStreamName, WriteFailureReason));

            // No terminal line in this fixture either way, so unavailable still reads
            // no-terminal-billed-figure -- the scope claim here is narrower: a stderr-only loss must
            // not ALSO suppress the floor on top of that, the same scope the triple's own stderr
            // control states.
            Assert.Equal("no-terminal-billed-figure", view.BilledReconciliationUnavailable);
            Assert.NotNull(view.ObservedBilledTokenFloor);
            Assert.Equal(700, view.ObservedBilledTokenFloor!.Tokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static ExecutionRequest AcceptedRequest(ExecutionId executionId, string worker) => new(
        executionId,
        new WorkflowId("wf-1885"),
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
            System.Text.Json.JsonSerializer.Serialize(config));
    }
}
