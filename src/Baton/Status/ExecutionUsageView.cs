using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;

namespace Baton.Status;

/// <summary>
/// One execution's usage, per <c>baton status --json</c>'s additive shape (issue #1360, extended by
/// #1569). Canonical field list and wire contract at <c>spec/baton.md</c> §3, not restated here.
/// <see cref="WallClockMs"/> is always present — it is derived from the ledger's own
/// <see cref="CoreEvent.ExecutionStarted"/>/<see cref="CoreEvent.ExecutionExited"/> timestamps, which
/// every completed execution has. Every other field is independently omitted from the serialized JSON
/// (never emitted as <c>null</c>, never fabricated as zero) when the vendor's captured stdout carried
/// no such figure — see <see cref="ExecutionUsageProjector"/> for how they are read. #1876 widened
/// "no such figure" by one source (the in-memory fallback off <see cref="FlowEvent.ExecutionArrested"/>,
/// which reaches these six fields and nothing below); <c>spec/baton.md</c> §3 has the rule and its
/// limit, and <see cref="ExecutionUsageProjector"/> the code that draws it. These fields are
/// per-execution attribution, not a complete burn figure — see <c>spec/baton.md</c> §3/§7 for why.
/// <para>
/// #1706's reconciliation triple. <see cref="BilledTokens"/> is the AUTHORITATIVE per-execution billed
/// figure — <c>TokensIn + TokensOut + CacheCreationTokens</c> off the terminal line, which on claude is
/// now the whole-tree <c>modelUsage</c> read (<c>ClaudeUsageParser.TryParseFinalUsage</c>).
/// <see cref="LiveBilledTokens"/> is what <see cref="Mutation.TokenBudgetMonitor"/> — the real one,
/// replayed over the same captured stream, never a second implementation of its arithmetic — saw while
/// the execution was running, i.e. the quantity a budget actually arrested on.
/// <see cref="BilledUnderReadTokens"/> is their difference: how much of this room's real spend the live
/// budget could not see. All three are omitted together unless both figures were computable, and when
/// they are omitted for any reason other than "this execution has no captured stream at all",
/// <see cref="BilledReconciliationUnavailable"/> carries the reason (#1706 review M2/M3): the shape is
/// ALL THREE PRESENT, or all three absent plus a reason. A consumer must never read the presence of one
/// of the three as evidence the replay was complete. Why the difference is still emitted at zero, and
/// why this is derived on read rather than journaled, are <c>spec/baton.md</c> §3's own statement of the
/// wire contract, not restated here.
/// </para>
/// </summary>
public sealed record ExecutionUsageView(
    [property: JsonPropertyName("wallClockMs")] long WallClockMs,
    [property: JsonPropertyName("tokensIn")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TokensIn = null,
    [property: JsonPropertyName("tokensOut")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TokensOut = null,
    [property: JsonPropertyName("turns")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Turns = null,
    [property: JsonPropertyName("cacheReadTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CacheReadTokens = null,
    [property: JsonPropertyName("cacheCreationTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CacheCreationTokens = null,
    [property: JsonPropertyName("thinkingTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? ThinkingTokens = null,
    [property: JsonPropertyName("billedTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? BilledTokens = null,
    [property: JsonPropertyName("liveBilledTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? LiveBilledTokens = null,
    [property: JsonPropertyName("billedUnderReadTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? BilledUnderReadTokens = null,
    /// <summary>
    /// #1706 review M3: why the reconciliation triple above is absent, when it is absent for a reason a
    /// consumer can act on. One of <c>stream-truncated-by-rollover</c> (the capture is provably not the
    /// whole stream — <see cref="Dispatch.ExecutionStreamLogger.StdoutTruncationMarkerFileName"/>),
    /// <c>stream-truncated-by-write-failure</c> (#1876 — provably not the whole stream for the other
    /// reason: the host obstructed the writer past its retry buffer,
    /// <see cref="Dispatch.ExecutionStreamLogger.StdoutWriteFailureMarkerFileName"/>),
    /// <c>rollover-segment-unreadable</c>, <c>no-live-billed-figure</c> (the replay parsed no usage line
    /// carrying a billed component) or <c>no-terminal-billed-figure</c> (the terminal line reported
    /// none). Absent — like the triple itself — when the execution simply has no captured stream at
    /// all: that is the pre-#1706 "nothing was read" case, not a number being withheld.
    /// <para>
    /// #1885: <c>stream-truncated-by-write-failure</c> now has TWO sources —
    /// <see cref="FlowEvent.StreamLogLossDeclared"/> as well as the marker file — and the projector below
    /// implements <c>spec/baton.md</c> §3's two-channel rule over them, which is stated there and not
    /// restated here.
    /// </para>
    /// </summary>
    [property: JsonPropertyName("billedReconciliationUnavailable")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? BilledReconciliationUnavailable = null,
    /// <summary>
    /// #1709: <c>FlowEvent.ExecutionSucceeded.PeakBilledInWindow</c>/<c>FlowEvent.ExecutionFailed.PeakBilledInWindow</c>
    /// — and, since #1876, <c>FlowEvent.ExecutionArrested.PeakBilledInWindow</c>, the same journalled
    /// figure off the third way an execution ends — read back verbatim off this execution's own
    /// outcome event; see that field's own doc comment for when it is null. NOT the same kind of figure as <see cref="LiveBilledTokens"/> above:
    /// this one is a JOURNALLED measurement from the live execution itself, where <see cref="LiveBilledTokens"/>
    /// is this projector's own REPLAY over the captured stream (this type's own remarks, above, state
    /// that distinction for the whole reconciliation triple).
    /// </summary>
    [property: JsonPropertyName("peakBilledInWindow")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? PeakBilledInWindow = null,
    /// <summary>
    /// #1883 review F1: <see cref="WorkerUsage.ModelsObserved"/>, carried through verbatim off the same
    /// terminal reading the six token figures above come from. That field's own doc states what it means
    /// and what its absence does and does not say.
    /// </summary>
    [property: JsonPropertyName("modelsObserved")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? ModelsObserved = null,
    /// <summary>
    /// #1927: the model the vendor CLI itself reported having RUN, off the last line of the captured
    /// stream that named one (<see cref="IWorkerUsageParser.TryParseEchoedModel"/>). <b>Not the model
    /// that was requested</b> — that is the binding's, and a substitution or quota-driven downgrade is
    /// visible only as a difference between the two. Absent when the vendor echoes none, which on agy
    /// and codex is structural (neither has a line carrying a <c>model</c> key — each parser's own doc
    /// says why, and the two reasons differ) and never "blank".
    /// <see cref="ModelsObserved"/> is a different fact and not a substitute —
    /// <c>Accounting.CostLedgerEntry.ModelEchoed</c>, which this feeds, states the distinction.
    /// </summary>
    [property: JsonPropertyName("modelEchoed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ModelEchoed = null,
    /// <summary>
    /// #1882: how long <see cref="Mutation.VerifyStepRunner"/>'s commands took, in milliseconds. NOT a
    /// token figure and not part of any Σ above. Attribution, the all-or-nothing pairing with
    /// <see cref="VerifyResultsBytes"/>, and what an absent sidecar means are spec/baton.md §3's
    /// contract, not restated here.
    /// </summary>
    [property: JsonPropertyName("verifyStepMs")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? VerifyStepMs = null,
    /// <summary>
    /// #1882: the size in bytes of the <c>verify-results.md</c> that step wrote — the READ cost the
    /// reviewer pays for its evidence, which is the half of this feature that is not free. Same gate
    /// as <see cref="VerifyStepMs"/>, never one without the other.
    /// </summary>
    [property: JsonPropertyName("verifyResultsBytes")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? VerifyResultsBytes = null,
    /// <summary>
    /// #1921: <see cref="ToolStepCounts.ToolSteps"/> over this execution's whole captured stream — how
    /// many real tool calls it made, in the unit <c>MaxToolSteps</c> caps.
    /// <b>Present exactly when the next three are</b>, which <see cref="ToolStepTally.Snapshot"/> decides
    /// for all four at once — including why a stream with no readable tool activity is absent here
    /// rather than zero.
    /// </summary>
    [property: JsonPropertyName("toolSteps")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ToolSteps = null,
    /// <summary>
    /// #1921: <see cref="ToolStepCounts.Refused"/> — steps whose result carried
    /// <see cref="Domain.GrantRefusal.Marker"/>. <see cref="ToolStepTally"/>'s remarks state the one
    /// conclusion this figure does not support on an older room.
    /// </summary>
    [property: JsonPropertyName("refusedToolSteps")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? RefusedToolSteps = null,
    /// <summary>
    /// #1921: <see cref="ToolStepCounts.Repeated"/> — occurrences beyond the first of an identical
    /// tool+arguments pair. That field's own doc states the arithmetic and the two different things a 0
    /// can mean.
    /// </summary>
    [property: JsonPropertyName("repeatedToolSteps")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? RepeatedToolSteps = null,
    /// <summary>
    /// #1921: <see cref="ToolStepCounts.EmptyResults"/>. Reported by <c>baton audit lanes</c> and
    /// deliberately not carried onto the cost-ledger row —
    /// <see cref="IWorkerUsageParser.CountEmptyToolResults"/> states why the two are not the same kind of
    /// waste.
    /// </summary>
    [property: JsonPropertyName("emptyToolResults")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? EmptyToolResults = null)
{
    /// <summary>The capture is provably not the whole stream — <see cref="Dispatch.ExecutionStreamLogger.StdoutTruncationMarkerFileName"/>.</summary>
    public const string StreamTruncatedByRolloverReason = "stream-truncated-by-rollover";

    /// <summary>
    /// #1876's reason — see <see cref="BilledReconciliationUnavailable"/> above for what it means.
    /// #1885 gave it a SECOND producer, the journalled <see cref="FlowEvent.StreamLogLossDeclared"/>,
    /// which is why the literal now lives here rather than being spelled out at each of the three
    /// production sites: `record-once`, so a rewording cannot silently make one channel's announcement
    /// stop matching the other's.
    /// </summary>
    public const string StreamTruncatedByWriteFailureReason = "stream-truncated-by-write-failure";

    /// <summary>The rolled-over segment exists but could not be read, so no Σ over the whole stream is possible.</summary>
    public const string RolloverSegmentUnreadableReason = "rollover-segment-unreadable";

    /// <summary>The replay parsed no usage line carrying a billed component.</summary>
    public const string NoLiveBilledFigureReason = "no-live-billed-figure";

    /// <summary>
    /// The terminal line reported no billed component. #1883 review F2: this conflates "a complete
    /// stream that carried no billed figure" with "the last non-blank line failed to parse" — a worker
    /// killed mid-stream — and nothing downstream can tell those apart, which is why a consumer must
    /// treat it as an incomplete capture rather than a complete one.
    /// </summary>
    public const string NoTerminalBilledFigureReason = "no-terminal-billed-figure";

    /// <summary>
    /// Every value <see cref="BilledReconciliationUnavailable"/> can carry, in one place, so a consumer
    /// mapping them (<c>Accounting.CostLedgerStore</c>) can be tested against the producer's whole
    /// vocabulary rather than against a restatement of it that goes stale when a reason is added
    /// (#1883 review F3 — the two strings used to be spelled out a second time in that store).
    /// </summary>
    public static IReadOnlySet<string> KnownUnavailableReasons { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        StreamTruncatedByRolloverReason,
        StreamTruncatedByWriteFailureReason,
        RolloverSegmentUnreadableReason,
        NoLiveBilledFigureReason,
        NoTerminalBilledFigureReason,
    };
}

/// <summary>
/// Builds one <see cref="ExecutionUsageView"/> per <see cref="ExecutionId"/> that has both a recorded
/// <see cref="CoreEvent.ExecutionStarted"/> and <see cref="CoreEvent.ExecutionExited"/> (issue #1360)
/// — an execution still running, or one that crashed before Core recorded either lifecycle event, has
/// no wall-clock to derive and is simply absent from the result rather than reported as zero.
/// <para>
/// Token/turn counts are read from the execution's already-captured <c>.stdout.log</c>
/// (<see cref="ExecutionStreamLogger"/>) — never a new ledger event, per the issue's own preference
/// for deriving over recording twice. Which adapter's parser to trust is resolved by preferring the
/// accepted request's own recorded <see cref="ExecutionRequest.Adapter"/> — see that field's doc
/// comment (issue #1567) for why, and for the one path where it is not the guarantee it usually is.
/// Only the resolved adapter's <see cref="IWorkerUsageParser.TryParseFinalUsage"/> is tried, and
/// only against the last non-blank line of the captured stream.
/// </para>
/// </summary>
public static class ExecutionUsageProjector
{
    private const string RoomBindingsFileName = "bindings.json";

    public static IReadOnlyDictionary<string, ExecutionUsageView> BuildByExecutionId(
        IReadOnlyList<LogEntry> entries,
        string artifactsRootPath,
        IReadOnlyDictionary<string, IWorkerUsageParser>? adapters = null,
        string? roomDirectoryPath = null) =>
        BuildByExecutionId<IWorkerUsageParser>(entries, artifactsRootPath, adapters, roomDirectoryPath);

    public static IReadOnlyDictionary<string, ExecutionUsageView> BuildByExecutionId<TParser>(
        IReadOnlyList<LogEntry> entries,
        string artifactsRootPath,
        IReadOnlyDictionary<string, TParser>? adapters = null,
        string? roomDirectoryPath = null)
        where TParser : IWorkerUsageParser
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        var startedTimestamps = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var exitedTimestamps = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var workerNameByExecutionId = new Dictionary<string, string>(StringComparer.Ordinal);
        // #1709: FlowEvent.ExecutionSucceeded/ExecutionFailed's own PeakBilledInWindow -- see
        // ExecutionUsageView.PeakBilledInWindow's own doc comment for what this is and is not the same
        // figure as.
        var peakBilledInWindowByExecutionId = new Dictionary<string, long>(StringComparer.Ordinal);
        // #1876: the usage the LIVE monitor accumulated in memory, journalled on the arrest event. This
        // is the only token reading that exists for an execution whose captured stream cannot supply
        // one -- it never touched the disk, so a disk problem cannot erase it. Read below only when the
        // stream yielded no terminal reading of its own; see that site for why it is deliberately not
        // allowed to stand in for the AUTHORITATIVE terminal figure.
        var arrestedUsageByExecutionId = new Dictionary<string, WorkerUsage>(StringComparer.Ordinal);
        // #1885: the JOURNALLED half of the loss announcement, filtered to the one stream that bears on
        // a billed reconciliation -- spec/baton.md §3 is where that scoping is ruled. Last one wins,
        // which is the terminal re-announcement when there is one; the reason is identical either way,
        // so this only decides whose BytesSurrendered/MarkerLanded a future reader would see.
        var journalledStreamLossByExecutionId = new Dictionary<string, FlowEvent.StreamLogLossDeclared>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry is LogEntry.FlowLogEntry { Event: FlowEvent.ExecutionRequestAccepted accepted })
            {
                workerNameByExecutionId[accepted.Request.ExecutionId.Value] = accepted.Request.Worker;
            }

            if (entry is LogEntry.FlowLogEntry flowEntry)
            {
                (ExecutionId, long)? peak = flowEntry.Event switch
                {
                    FlowEvent.ExecutionSucceeded { PeakBilledInWindow: { } p } succeeded => (succeeded.ExecutionId, p),
                    FlowEvent.ExecutionFailed { PeakBilledInWindow: { } p } failed => (failed.ExecutionId, p),
                    // #1876: an ARREST carries one too, and was being dropped -- so the execution shape
                    // most likely to have no usable captured stream (killed mid-turn, terminal line
                    // never emitted) was also the one shape whose journalled figure went unread.
                    FlowEvent.ExecutionArrested { PeakBilledInWindow: { } p } arrested => (arrested.ExecutionId, p),
                    _ => null,
                };
                if (peak is { } recorded)
                {
                    peakBilledInWindowByExecutionId[recorded.Item1.Value] = recorded.Item2;
                }

                if (flowEntry.Event is FlowEvent.ExecutionArrested { Usage: { } arrestedUsage } arrestedEvent)
                {
                    arrestedUsageByExecutionId[arrestedEvent.ExecutionId.Value] = arrestedUsage;
                }

                if (flowEntry.Event is FlowEvent.StreamLogLossDeclared loss
                    && string.Equals(loss.Stream, ExecutionStreamLogger.StdoutStreamName, StringComparison.Ordinal))
                {
                    journalledStreamLossByExecutionId[loss.ExecutionId.Value] = loss;
                }
            }

            if (entry is not LogEntry.CoreLogEntry { WriterUtcTimestamp: { } timestamp } coreEntry)
            {
                continue;
            }

            switch (coreEntry.Event)
            {
                case CoreEvent.ExecutionStarted started:
                    startedTimestamps[started.ExecutionId.Value] = timestamp;
                    break;
                case CoreEvent.ExecutionExited exited:
                    exitedTimestamps[exited.ExecutionId.Value] = timestamp;
                    break;
            }
        }

        var bindings = TryLoadBindings(roomDirectoryPath);
        // #1583/#1781: the recorded-adapter-with-StepRebound-override precedence is one primitive now,
        // shared with QuotaLedgerStore.BuildEntries -- see ExecutionBindingResolver's own doc comment.
        var resolvedBindings = ExecutionBindingResolver.Resolve(entries);

        // #1882: the room's pre-turn verify step ran ONCE, before the first worker turn, so its cost
        // belongs to exactly one execution. Attributing it to every execution would double-count it in
        // #1849's ledger the moment a step retried; attributing it to none would hide a real cost. The
        // earliest execution with a start/exit pair is the one the step preceded — ties broken by id
        // so the answer is deterministic rather than dictionary-order. The exit half of that pair is
        // not an extra rule this projection invented: the loop below emits NO view at all for an
        // execution that never exited (wallClockMs is unconditional on the view), so an id chosen
        // without it would attribute the cost to a row that is never written and lose the figures
        // entirely. spec/baton.md §3 states the same condition, in those terms.
        var verifyStep = VerifyStepReport.TryReadSidecar(artifactsRootPath);
        var verifyStepExecutionId = verifyStep is null
            ? null
            : startedTimestamps
                .Where(pair => exitedTimestamps.ContainsKey(pair.Key))
                .OrderBy(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key)
                .FirstOrDefault();

        var result = new Dictionary<string, ExecutionUsageView>(StringComparer.Ordinal);
        foreach (var (executionId, startedAt) in startedTimestamps)
        {
            if (!exitedTimestamps.TryGetValue(executionId, out var exitedAt))
            {
                continue;
            }

            var wallClockMs = (long)(exitedAt - startedAt).TotalMilliseconds;
            if (wallClockMs < 0)
            {
                // #1360 F6 (review): a clamp to 0 here would print the exact "zero standing in for
                // unknown" the issue rules out, indistinguishable from a genuinely instantaneous
                // execution. The only way this fires is a backwards clock step (NTP correction, VM
                // resume) mid-execution -- the honest response is to skip the entry, same as an
                // execution with no exit event yet.
                continue;
            }

            workerNameByExecutionId.TryGetValue(executionId, out var workerName);
            resolvedBindings.TryGetValue(executionId, out var resolvedBinding);
            var reading = TryReadWorkerUsage(artifactsRootPath, executionId, workerName, resolvedBinding.Adapter, bindings, adapters);
            var usage = reading?.Terminal;
            // #1706: the terminal billed total, on the SAME arithmetic WorkerUsage.BilledTokens
            // documents (input + output + cache_creation, never cache_read). Null unless the terminal
            // line reported at least one of those three -- never a fabricated zero.
            long? billed = usage is null || (usage.TokensIn is null && usage.TokensOut is null && usage.CacheCreationTokens is null)
                ? null
                : (usage.TokensIn ?? 0) + (usage.TokensOut ?? 0) + (usage.CacheCreationTokens ?? 0);
            var liveBilled = reading?.LiveBilled;

            // #1876: the in-memory fallback, and the exact line at which it stops. The per-dimension
            // fields fall back to what the live monitor observed and journalled on the arrest when the
            // captured stream yielded NO terminal reading -- because a disk problem (or an arrest that
            // preempted the vendor's terminal line) must not be able to erase token counts that were
            // already observed in memory. It deliberately does NOT feed `billed` above: that figure's
            // documented meaning is "off the terminal line", a live Σ is a floor rather than an
            // authoritative total, and pairing a floor with the replay Σ as though it were the terminal
            // figure would fabricate exactly the under-read the reconciliation triple exists to expose.
            // So a fallback reading reports dimensions, never a reconciliation -- the triple stays
            // withheld and the reason string stays whatever it already was.
            var dimensions = usage ?? (arrestedUsageByExecutionId.TryGetValue(executionId, out var fromArrest) ? fromArrest : null);

            // #1885: the other channel, read FIRST (spec/baton.md §3). A journalled loss must SUPPRESS
            // the reconciliation, not merely fill a reason string that happened to be empty -- so the
            // live figure is dropped here, which is what makes `reconciled` below false and the reason
            // reachable. Deliberately AFTER `dimensions`: spec/baton.md §3's shape for a lost stream
            // keeps them, and
            // the in-memory arrest fallback above is what supplies them.
            var journalledReason = journalledStreamLossByExecutionId.TryGetValue(executionId, out var journalledLoss)
                ? journalledLoss.Reason
                : null;
            if (journalledReason is not null)
            {
                WarnOnChannelDisagreement(executionId, journalledReason, reading?.LiveUnavailableReason);
                liveBilled = null;
            }

            // #1706 review M2: ALL THREE or none. The previous shape emitted `billedTokens` alone
            // whenever the terminal figure was computable but the replay was not -- which is exactly
            // the case the rollover guard below exists to SIGNAL, so a consumer following the
            // documented "all three together" contract would have read a partial answer as a complete
            // one. The reason string is what replaces the number.
            var reconciled = billed is not null && liveBilled is not null;
            string? unavailable = null;
            if (!reconciled)
            {
                // #1885: event first, marker second. The journalled reason also stands alone -- with no
                // stream file and no marker, `reading` is null and the pre-#1885 shape reported no
                // reason at all, which is the exact case a host refusing every file create produces.
                // The `reading is not null` guard is therefore INSIDE the fallback rather than on the
                // `if` above (where #1883 left it), which is the one place these two changes collide.
                unavailable = journalledReason
                    ?? (reading is not null
                        ? reading.LiveUnavailableReason
                          ?? (billed is null
                              ? ExecutionUsageView.NoTerminalBilledFigureReason
                              : ExecutionUsageView.NoLiveBilledFigureReason)
                        : null);
            }

            long? peakBilledInWindow = peakBilledInWindowByExecutionId.TryGetValue(executionId, out var recordedPeak)
                ? recordedPeak
                : null;

            result[executionId] = new ExecutionUsageView(
                wallClockMs,
                dimensions?.TokensIn,
                dimensions?.TokensOut,
                dimensions?.Turns,
                dimensions?.CacheReadTokens,
                dimensions?.CacheCreationTokens,
                dimensions?.ThinkingTokens,
                reconciled ? billed : null,
                reconciled ? liveBilled : null,
                reconciled ? billed!.Value - liveBilled!.Value : null,
                unavailable,
                peakBilledInWindow,
                usage?.ModelsObserved,
                // #1927: off `reading`, not `usage` -- the echo survives an execution whose terminal
                // usage line was never parsed (an arrest, a truncated capture), which is precisely the
                // execution whose model a reader most needs named.
                reading?.ModelEchoed,
                // #1882: both figures together or neither -- see VerifyStepMs's own remarks.
                string.Equals(executionId, verifyStepExecutionId, StringComparison.Ordinal) ? verifyStep!.TotalWallClockMs : null,
                string.Equals(executionId, verifyStepExecutionId, StringComparison.Ordinal) ? verifyStep!.ResultsBytes : null,
                // #1921: all four together or all four absent. Read off the ONE nullable struct rather
                // than four independent coalesces, so a row can never carry three measured counts and a
                // fourth that silently defaulted -- ToolStepTally.Snapshot is the decision point.
                reading?.ToolStepCounts?.ToolSteps,
                reading?.ToolStepCounts?.Refused,
                reading?.ToolStepCounts?.Repeated,
                reading?.ToolStepCounts?.EmptyResults);
        }

        return result;
    }

    /// <summary>
    /// #1885: <c>spec/baton.md</c> §3's agreement rule, enforced. The two channels announce one
    /// write-failure loss off one in-memory latch and carry the same literal, so a mismatch is never
    /// those two disagreeing about it.
    /// <para>
    /// #1888 corrects what this comment used to claim. It said a mismatch was unreachable from one
    /// execution's own writer, which was false, and the marker-read order was what made it false: with
    /// rollover checked first, a stream that both double-rolled and lost a chunk announced
    /// <c>stream-truncated-by-rollover</c> on the file channel against a journalled
    /// <c>stream-truncated-by-write-failure</c> — one writer, both channels truthful, warning printed.
    /// <c>TryReadWorkerUsage</c> now reads the write-failure marker first, so that room compares like
    /// with like and is silent.
    /// </para>
    /// <para>
    /// What stays reachable, and is not a defect: the SAME room with the write-failure marker refused
    /// (an obstructed output directory) and only the rollover marker on disk. The file channel then
    /// names the rollover gap while the event names the write-failure one — two gaps, not two accounts
    /// of one — and the warning fires. Its wording is true of that case: the files may describe a
    /// different gap than the one the writer declared. Comparing the journalled reason against the SET
    /// of markers present, rather than against the precedence winner, is what would distinguish it from
    /// genuine drift; #1888 did not do that, and until something does, the remaining causes of a
    /// mismatch (hand-edited ledger, mis-keyed execution id, a future third producer) reach the same
    /// line as that benign one.
    /// </para>
    /// <para>
    /// Once per execution id per process. This projector re-runs on every <c>fleet_status</c> poll over
    /// an overwhelmingly complete execution set, so an unconditional write would repeat one stale line
    /// at the poll cadence for the life of the daemon — the same reason
    /// <c>ExecutionStreamLogger.MarkerFailureWarned</c> exists. The dictionary is uncapped and stays so:
    /// it now admits a reachable case rather than a provably empty one, but it holds one string per
    /// execution that announced a loss two ways, which is bounded by the room's own execution count.
    /// </para>
    /// </summary>
    private static void WarnOnChannelDisagreement(string executionId, string journalledReason, string? markerReason)
    {
        if (markerReason is null || string.Equals(markerReason, journalledReason, StringComparison.Ordinal))
        {
            return;
        }

        if (!DisagreementWarnedExecutionIds.TryAdd(executionId, 0))
        {
            return;
        }

        Console.Error.WriteLine(
            $"Warning: execution '{executionId}' announces its stream-log loss two ways that disagree — the journalled "
            + $"FlowEvent.StreamLogLossDeclared says '{journalledReason}' and the captured stream's own files say "
            + $"'{markerReason}'. Reporting the journalled reason; the files may describe a different gap than the one "
            + "the writer declared.");
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> DisagreementWarnedExecutionIds =
        new(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> TryLoadBindings(string? roomDirectoryPath)
    {
        if (roomDirectoryPath is null)
        {
            return EmptyBindings;
        }

        var bindingsPath = Path.Combine(roomDirectoryPath, RoomBindingsFileName);
        if (!File.Exists(bindingsPath))
        {
            return EmptyBindings;
        }

        try
        {
            using var stream = File.OpenRead(bindingsPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return EmptyBindings;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object
                    && prop.Value.TryGetProperty("Adapter", out var adapterProp)
                    && adapterProp.ValueKind == JsonValueKind.String
                    && adapterProp.GetString() is { } adapterName
                    && !string.IsNullOrWhiteSpace(adapterName))
                {
                    result[prop.Name] = adapterName;
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return EmptyBindings;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyBindings =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// #1706: one captured stream read once, yielding both the terminal reading and the live-billed Σ
    /// the budget monitor would have accumulated over the same bytes. Kept together because they come
    /// from the same file read — splitting them would read a multi-megabyte log twice to answer one
    /// question.
    /// <para>
    /// Cost, since <c>fleet_status</c> polls this: the replay hands every captured line to the vendor
    /// parser, which parses it up to SIX times — tool name, tool-step count and incremental usage for the
    /// budget monitor, plus #1921's refused-step count, empty-result count and invocation keys for
    /// <see cref="ToolStepTally"/>. Doubling that was judged affordable because the memo below is what
    /// the polling path actually pays, and a completed room re-reads nothing. #1927's echoed-model scan
    /// is a SEVENTH parse of a line, but not of every line: <see cref="ScanEchoedModel"/> runs backwards
    /// and stops at the first line naming a model — the terminal event on a whole claude stream — and on
    /// a vendor that overrides nothing it parses no line at all, the interface default answering without
    /// touching the JSON. Before
    /// #1706 this projector parsed exactly one line per execution. Bounded by the stream logger's own
    /// 8 MiB-plus-one-rollover retention BOUND rather than by anything here, and measured at ~9 MB for
    /// the largest room on the machine this was developed against. That bound is retention, NOT a
    /// completeness guarantee — see the truncation-marker arm below for what happens past it.
    /// </para>
    /// <para>
    /// #1706 review L3: memoized per execution, keyed on the stream files' own (path, length, last-write)
    /// triple, because <c>fleet_status</c> and <c>baton status --json</c> re-run this projector on every
    /// poll over an execution set that is overwhelmingly COMPLETE and therefore byte-identical between
    /// polls. A completed room's stream never changes again, so the second and every later poll costs a
    /// stat instead of a multi-megabyte JSON re-parse; a still-growing stream changes length on each
    /// append and re-reads, which is correct rather than merely tolerable.
    /// </para>
    /// </summary>
    /// <param name="ModelEchoed">
    /// #1927: the last model any line of the captured stream reported the vendor as having RUN
    /// (<see cref="IWorkerUsageParser.TryParseEchoedModel"/>), scanned across the rolled segment as
    /// well as the current file. Null when this vendor echoes none — which for agy and codex is
    /// structural, not a read failure.
    /// </param>
    /// <param name="ToolStepCounts">
    /// #1921: what <see cref="ToolStepTally"/> accumulated over the same replay that produced
    /// <paramref name="LiveBilled"/> — same pass over the same bytes, for the same reason the terminal
    /// reading and the live Σ are kept together here. Null on every early return above: a stream this
    /// method refused to read whole must not report a step count derived from part of it, which is the
    /// fabricated under-read the rollover comment in that method is about. Not the same rule as
    /// <paramref name="ModelEchoed"/> beside it, which those returns deliberately DO carry.
    /// </param>
    private sealed record UsageReading(
        WorkerUsage? Terminal,
        long? LiveBilled,
        string? LiveUnavailableReason = null,
        string? ModelEchoed = null,
        ToolStepCounts? ToolStepCounts = null);

    /// <summary>
    /// The memo behind <see cref="UsageReading"/>'s L3 note. Concurrent because both readers above are
    /// reachable from the daemon's own polling; cleared wholesale past a generous cap rather than given
    /// an eviction policy, since the population is "executions whose streams this process has read" and
    /// an occasional full re-read is cheaper than the bookkeeping an LRU would need.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, UsageReading> ReadingCache = new(StringComparer.Ordinal);

    private const int ReadingCacheCap = 4096;

    private static UsageReading? TryReadWorkerUsage<TParser>(
        string artifactsRootPath,
        string executionId,
        string? workerName,
        string? recordedAdapter,
        IReadOnlyDictionary<string, string> bindings,
        IReadOnlyDictionary<string, TParser>? adapters)
        where TParser : IWorkerUsageParser
    {
        // #1567: the recorded adapter wins whenever present -- see ExecutionRequest.Adapter's doc for
        // why, and for the resubmit-path case (#1583) where it isn't the guarantee it usually is. The
        // bindings.json fallback below covers lines that predate the field, and non-process
        // dispatches, which never carry one.
        var adapterName = recordedAdapter;
        if (adapterName is null && workerName is not null)
        {
            bindings.TryGetValue(workerName, out adapterName);
        }

        if (adapterName is null)
        {
            return null;
        }

        // Overload resolution against an unbound TParser can never apply the non-generic overload's
        // `?? StandardWorkerUsageParsers.Default` fallback (invariance -- see #1590) -- so a null
        // registry is resolved against the built-in parsers here instead, once, regardless of which
        // overload the caller went through.
        IWorkerUsageParser? adapter = adapters is not null
            ? adapters.TryGetValue(adapterName, out var registered) ? registered : null
            : StandardWorkerUsageParsers.Default.TryGetValue(adapterName, out var defaultParser) ? defaultParser : null;

        if (adapter is null)
        {
            return null;
        }

        var id = new ExecutionId(executionId);
        var stdoutPath = Path.Combine(ArtifactManager.ResolveOutputDirectory(artifactsRootPath, id), ExecutionStreamLogger.StdoutLogFileName);
        if (!File.Exists(stdoutPath))
        {
            // #1360 F7 (review): a retention sweep moves the whole execution directory -- .stdout.log
            // included -- to the pruned location (RoomRetentionSweep -> ArtifactPruner). Without this
            // fallback, terminal.json (written before any sweep) and a post-sweep status read of the
            // same unchanged room would disagree about a figure both once knew.
            stdoutPath = Path.Combine(ArtifactManager.ResolvePrunedOutputDirectory(artifactsRootPath, id), ExecutionStreamLogger.StdoutLogFileName);
            if (!File.Exists(stdoutPath))
            {
                // #1879 review HIGH 2: no stream file is normally the pre-#1706 "nothing was read"
                // case, which carries no reason -- but the write-failure marker can be there WITHOUT
                // one, and that combination means something quite different: the logger declared the
                // capture lost before a single byte of it existed (an initialization failure disables
                // it for the whole execution). Reported rather than swallowed, so an operator can tell
                // "this worker emitted nothing" from "the host would not let us record what it
                // emitted". Deliberately not memoized: there is no stream file to key the memo on, and
                // the rollover marker is not consulted first the way it is below -- with no stream
                // file there is nothing for a rollover to be a gap IN.
                foreach (var directory in new[]
                         {
                             ArtifactManager.ResolveOutputDirectory(artifactsRootPath, id),
                             ArtifactManager.ResolvePrunedOutputDirectory(artifactsRootPath, id),
                         })
                {
                    if (File.Exists(Path.Combine(directory, ExecutionStreamLogger.StdoutWriteFailureMarkerFileName)))
                    {
                        return new UsageReading(null, null, ExecutionUsageView.StreamTruncatedByWriteFailureReason);
                    }
                }

                return null;
            }
        }

        var rolloverPath = Path.Combine(Path.GetDirectoryName(stdoutPath)!, ExecutionStreamLogger.StdoutRolloverFileName);
        var truncationMarkerPath = Path.Combine(Path.GetDirectoryName(stdoutPath)!, ExecutionStreamLogger.StdoutTruncationMarkerFileName);
        var writeFailureMarkerPath = Path.Combine(Path.GetDirectoryName(stdoutPath)!, ExecutionStreamLogger.StdoutWriteFailureMarkerFileName);

        // #1706 review L3: see UsageReading's own doc. Stat both stream files before reading either --
        // a completed execution's pair is byte-identical poll to poll, and re-parsing megabytes of JSON
        // to re-derive an answer that cannot have changed is what this key avoids.
        // #1691 merge / #1706: resolved HERE rather than at the replay site, because the memo key must
        // name BOTH parsers. They are resolved independently and differ silently -- see the replay
        // comment below -- so keying on `adapter` alone would let two calls that pass different
        // `adapters` registries for the same stream collide and serve each other's reading.
        var replayParser = StandardWorkerUsageParsers.Default.TryGetValue(adapterName, out var liveParser) ? liveParser : adapter;
        // #1876: the marker paths join the key because the write-failure marker can appear while the
        // stream files' own (length, last-write) pair does not move -- the whole point of that marker is
        // that bytes went MISSING -- so a memo keyed on the bytes alone would keep serving a reading
        // taken before the writer admitted the gap. The rollover marker is redundant here (a rollover
        // always moves both files) and is included anyway rather than relying on that coincidence.
        var cacheKey = BuildReadingCacheKey(
            stdoutPath, rolloverPath, truncationMarkerPath, writeFailureMarkerPath, adapterName, adapter, replayParser);
        if (cacheKey is not null && ReadingCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(stdoutPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held by a writer that has not yet reached MarkTerminal, a transient sharing race, or an
            // ACL'd stream log (UnauthorizedAccessException is not an IOException in .NET, so the
            // review's minor finding needed its own arm) -- none of these are this projector's failure
            // to surface; the caller simply sees no usage this time.
            return null;
        }

        WorkerUsage? terminal = null;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            terminal = adapter.TryParseFinalUsage(line, out var usage) ? usage : null;
            break;
        }

        // The rollover segment, read HERE rather than at its point of use below purely so the echoed-
        // model scan can span it too (#1927 review LOW: the scan claimed to read the stream in full
        // and read only the current file, which is only its tail once the stream has rolled -- see the
        // #1706 block below). The unreadable case is carried as a flag rather than returned from here,
        // so the two truncation markers keep being checked FIRST: which reason outranks which is
        // spec/baton.md §3's ruling, and #1888 is what found it.
        string[] rolledLines = [];
        var rolloverUnreadable = false;
        if (File.Exists(rolloverPath))
        {
            try
            {
                rolledLines = File.ReadAllLines(rolloverPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rolloverUnreadable = true;
            }
        }

        // #1927: the echoed model, scanned from the END so the LAST line that names one wins -- a
        // substitution announced on the terminal event outranks whatever the opening lifecycle line
        // claimed. Read through `replayParser`, NOT `adapter`, for the reason the replay site below
        // spells out at length: the vendor ADAPTERS delegate only TryParseFinalUsage, so every optional
        // interface method reached through one silently takes its default -- here, null on every real
        // execution while a unit test constructing ClaudeUsageParser directly passes.
        //
        // The current file first and the rolled segment only if it named nothing, which IS last-wins:
        // the rolled segment is the EARLIER half of one stream. Harmless for a terminal event (always
        // in the current file) and real for claude's message.model fallback on a rolled stream that
        // was arrested before producing one.
        //
        // Deliberately computed BEFORE the two marker early-returns: both segments have already been
        // read, and a stream whose reconciliation is unavailable is exactly a stream whose model is
        // still worth naming. Withholding it there would make the vendor-substitution signal vanish on
        // the executions most likely to have suffered one.
        var modelEchoed = ScanEchoedModel(lines, replayParser) ?? ScanEchoedModel(rolledLines, replayParser);

        // #1706 review: the replay must span the WHOLE stream, and `.stdout.log` is only its tail once
        // ExecutionStreamLogger has rolled over at 8 MiB (its single `.stdout.log.1`, written FIRST and
        // therefore replayed first). Reading the current file alone was harmless while this projector
        // needed exactly one line -- the terminal `result`, always in the current file -- and became a
        // defect the moment #1706 added an accumulation over every line: measured on a real rolled room
        // (`dispatch-implement-fd196a41`), the current file alone yields 30,593 against a terminal
        // 356,563, a fabricated 91% "under-read" that is pure rollover artifact and would have been
        // read as this vendor's worst measured room. A missing or unreadable rollover file contributes
        // nothing rather than failing the read -- it is absent on every execution that never grew past
        // the threshold, which is nearly all of them.
        //
        // #1706 review M3: reading both files is complete for a stream that rolled ONCE and silently
        // PARTIAL for one that rolled twice. Why a reader cannot tell those apart from the bytes, and
        // why the writer therefore has to say so, is on
        // ExecutionStreamLogger.StdoutTruncationMarkerFileName. Here: seeing the marker turns a
        // would-be fabricated under-read into an honest "unknown". Not seeing it is only evidence for
        // streams captured since that landed.
        // #1876: the other announced gap -- same posture as the rollover branch below, deliberately a
        // DIFFERENT reason string. Why the two are kept apart, and why a failure the retry buffer
        // absorbed writes no marker and so reaches neither branch as an ordinary whole stream, is in
        // spec/baton.md §3.
        //
        // #1888: and it is read FIRST, which matters only when BOTH markers are on disk. This function
        // reports one reason, and which of the two outranks the other is spec/baton.md §3's ruling --
        // not restated here, including its second half about what the order costs the agreement check.
        // Checking rollover first is what #1888 found: see WarnOnChannelDisagreement.
        if (File.Exists(writeFailureMarkerPath))
        {
            return Memoize(cacheKey, new UsageReading(terminal, null, ExecutionUsageView.StreamTruncatedByWriteFailureReason, modelEchoed));
        }

        if (File.Exists(truncationMarkerPath))
        {
            return Memoize(cacheKey, new UsageReading(terminal, null, ExecutionUsageView.StreamTruncatedByRolloverReason, modelEchoed));
        }

        if (rolloverUnreadable)
        {
            // Same posture as the current-file arm above -- except that here the honest response is
            // to report NO live figure at all rather than a partial one, since a partial Σ over the
            // tail alone is exactly the fabricated under-read this whole comment exists about. The read
            // itself moved above the echoed-model scan; only this decision stayed here, after both
            // markers.
            return Memoize(cacheKey, new UsageReading(terminal, null, ExecutionUsageView.RolloverSegmentUnreadableReason, modelEchoed));
        }

        // #1706: the REAL monitor, no triggers armed, replayed over the same captured stream -- so the
        // live figure this reports and the one a running execution actually arrests on cannot drift
        // apart, which two separate implementations of the same Σ inevitably would.
        //
        // It must be handed the SAME parser the live monitor was handed, which is
        // StandardWorkerUsageParsers.Default[adapterName] (MutationInterface.DispatchAndRecordOutcomeAsync),
        // NOT the adapter resolved above. Those differ, and silently: IWorkerUsageParser's
        // TryParseIncrementalUsage/CountToolSteps have default implementations returning false/0, and
        // the vendor ADAPTERS delegate only TryParseFinalUsage -- so replaying through an adapter reads
        // zero usage lines and reports no live figure at all, on every real execution. Caught by
        // ExecutionUsageProjectorTests' own #1706 arms, which go through WorkerAdapterRegistry.Default
        // exactly as `baton status` does. When the vendor is not one this engine ships a parser for,
        // the resolved adapter is still tried rather than skipping the replay outright.
        // #1691 merge: billedRateLimit is null here for the same reason budget/maxToolSteps are -- a
        // replay must not be able to arrest anything; it only reads.
        var replayMonitor = new TokenBudgetMonitor(budget: null, maxToolSteps: null, billedRateLimit: null, replayParser);
        // #1921: fed from the same loops, off the same parser, so the step count and the billed Σ are
        // always over identical bytes. A separate object rather than another counter on the monitor
        // because that monitor's job is to arrest a LIVE execution and this one only ever reads --
        // ToolStepTally's own remarks state why they are not merged.
        var toolStepTally = new ToolStepTally(replayParser);
        foreach (var line in rolledLines)
        {
            replayMonitor.OnStdoutLine(line);
            toolStepTally.OnStdoutLine(line);
        }

        foreach (var line in lines)
        {
            replayMonitor.OnStdoutLine(line);
            toolStepTally.OnStdoutLine(line);
        }

        return Memoize(cacheKey, new UsageReading(
            terminal,
            replayMonitor.SnapshotUsage().BilledTokens,
            null,
            modelEchoed,
            toolStepTally.Snapshot()));
    }

    /// <summary>
    /// #1927: the last line of one stream segment that names a model, scanned from the END so the last
    /// answer wins. Its two callers are the current file and the rolled segment, in that order.
    /// </summary>
    private static string? ScanEchoedModel(string[] segment, IWorkerUsageParser replayParser)
    {
        for (var i = segment.Length - 1; i >= 0; i--)
        {
            if (replayParser.TryParseEchoedModel(segment[i]) is { Length: > 0 } echoed)
            {
                return echoed;
            }
        }

        return null;
    }

    /// <summary>
    /// #1706 review L3: the identity of the bytes this projector read — both stream files' length and
    /// last-write time, plus BOTH resolved parsers. The terminal parser and the replay parser are
    /// resolved independently and can differ (see the replay site), so keying on one of them would let
    /// two calls passing different <c>adapters</c> registries for the same stream collide and serve
    /// each other's reading. Null when either stat throws, which simply disables the memo for that
    /// execution rather than guessing at an identity.
    /// </summary>
    private static string? BuildReadingCacheKey(
        string stdoutPath,
        string rolloverPath,
        string truncationMarkerPath,
        string writeFailureMarkerPath,
        string adapterName,
        IWorkerUsageParser adapter,
        IWorkerUsageParser replayParser)
    {
        try
        {
            var current = new FileInfo(stdoutPath);
            var rolled = new FileInfo(rolloverPath);
            var rolledPart = rolled.Exists
                ? $"{rolled.Length}:{rolled.LastWriteTimeUtc.Ticks}"
                : "none";
            // #1876: presence only -- both markers are empty by design, so there is nothing else about
            // them to key on, and their appearance is the whole state change.
            var markersPart = $"{(File.Exists(truncationMarkerPath) ? 'r' : '-')}{(File.Exists(writeFailureMarkerPath) ? 'w' : '-')}";
            return string.Join(
                '|',
                stdoutPath,
                current.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                current.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                rolledPart,
                markersPart,
                adapterName,
                adapter.GetType().FullName ?? adapter.GetType().Name,
                replayParser.GetType().FullName ?? replayParser.GetType().Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static UsageReading Memoize(string? cacheKey, UsageReading reading)
    {
        if (cacheKey is null)
        {
            return reading;
        }

        if (ReadingCache.Count >= ReadingCacheCap)
        {
            ReadingCache.Clear();
        }

        ReadingCache[cacheKey] = reading;
        return reading;
    }
}
