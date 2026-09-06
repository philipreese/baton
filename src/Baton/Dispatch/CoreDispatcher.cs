using Baton.Core;
using Baton.Artifacts;
using Baton.Domain;
using Baton.Store;

namespace Baton.Dispatch;

/// <summary>
/// The concrete binary and arguments to spawn for an <see cref="ExecutionRequest"/>. Resolving a
/// <see cref="ExecutionRequest.Worker"/> role name (e.g. <c>"architect"</c>) to this is a vendor
/// binding concern — <c>CLAUDE.md</c>'s Adapter Isolation rule keeps that resolution out of
/// <c>Baton</c> entirely, so the caller supplies it explicitly rather than the dispatcher
/// interpreting <see cref="ExecutionRequest.Worker"/> itself.
/// </summary>
/// <param name="WorkingDirectory">
/// The real, already-resolved absolute directory to spawn <see cref="Program"/> in (M23 Phase 3,
/// #272), or <see langword="null"/> to keep the prior default (Core's own process working
/// directory — AER's scratch artifacts folder, never a git-repo requirement). Vendor-agnostic: every
/// <c>IWorkerAdapter</c> forwards <c>WorkerInvocation.WorkingDirectory</c> here unchanged, so a
/// worker can operate on an arbitrary existing project the way it would run raw in a terminal.
/// </param>
/// <param name="PromptText">
/// The exact instructional text this dispatch's adapter built for the worker (issue #292) — e.g.
/// <c>ClaudeWorkerAdapter</c>/<c>AgyWorkerAdapter</c> set this to the identical string they embed
/// as their <c>-p</c> argument. May still contain unexpanded <c>%BATON_INPUT_0%</c>/<c>%BATON_OUTPUT_DIR%</c>-
/// style placeholders (same convention <see cref="Args"/> already uses) — <see cref="CoreDispatcher"/>
/// expands it the same way before durably writing it to <c>{outputDirectory}/prompt.txt</c>
/// (<see cref="ArtifactManager.PromptFileName"/>), so this record still carries no execution-specific
/// resolved path, matching every other field here. <see langword="null"/> means this adapter has
/// nothing worth capturing this way — <c>CommandWorkerAdapter</c> leaves this null since its
/// declared argv carries no prose prompt to capture. Archival
/// capture only, for UI/audit display (CLAUDE.md Architecture Rule 1) — never read back by Flow to
/// make a routing decision.
/// </param>
/// <param name="Environment">
/// Extra environment variables to set on the spawned process, beyond whatever
/// <see cref="ExecutionRequest.Environment"/>'s <see cref="EnvironmentVariable.BatonComputed"/> entries
/// already contribute (#533). This is the adapter's own seam, not the engine's: a variable like
/// Claude Code's <c>CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH</c> is a vendor quirk, and Architecture Rule
/// 2 keeps vendor quirks inside <c>Baton.Vendors</c> rather than letting <c>Baton</c> know the
/// variable's name exists. <see langword="null"/> or empty contributes nothing. Since #549 the child
/// does NOT inherit the daemon's whole environment: <c>BatonTask.WithClearEnv</c> is called first, so it
/// sees only <see cref="AssembleChildEnvironment"/>'s set — the <c>InheritedEnvironment</c> allowlist,
/// request's AER-computed variables, and these. This param adds to that set and, applied last, wins on
/// a name collision; it does not widen what the allowlist already scopes out (#895).
/// </param>
public sealed record CoreDispatchTarget(
    string Program,
    IReadOnlyList<string> Args,
    string? WorkingDirectory = null,
    Action<string>? OnStdoutLine = null,
    string? PromptText = null,
    IReadOnlyList<(string Name, string Value)>? Environment = null,
    string? StdoutArtifactName = null,
    string? OversizePromptWrapper = null,
    IReadOnlyList<CoreDispatchSeedFile>? SeedFiles = null,
    // #1089: given one complete stdout line, true iff it is this vendor's terminal "finished, status
    // success" marker. Set by the adapter (Adapter Isolation — the dispatcher never parses vendor
    // content, spec Rule 1); null on adapters/paths that do not stream, where the #1089 guard fails
    // safe to "a timeout always fails". Latched into CoreDispatchResult.TerminalSuccessObserved.
    Func<string, bool>? DetectsTerminalSuccess = null,
    // F6 (#1593 review): same shape as DetectsTerminalSuccess above, but matches ANY status, not
    // just success — see CoreDispatchResult.TerminalResultObserved's own remarks for why that
    // distinction matters. Latched there.
    Func<string, bool>? DetectsTerminalResult = null,
    // #1680/#1732 review WIRING: given this execution's own output directory, how many PreToolUse
    // hook verdicts that execution's hook recorded — Outcomes.OutcomeClassifier.Classify's
    // hookVerdictCount, deferred to dispatch time because the directory is execution-specific
    // (Adapter Isolation: Baton itself must not know the ledger's file name or format, only that a
    // count can be asked for). Non-null ONLY for a dispatch whose adapter determined its PreToolUse
    // hook is the sole thing narrowing the grant (AgyWorkerAdapter.RequiresHookAsSoleNarrowing) —
    // null everywhere else (claude, or an agy grant the hook does not solely narrow) keeps the
    // canary's parameters null and every other dispatch's classification unchanged.
    Func<string, int>? CountHookVerdicts = null,
    // #1741: the file name (not a path) CountHookVerdicts above reads, alongside it so a caller can
    // record the arming fact durably (ExecutionRequest.HookVerdictLedgerFileName) rather than only
    // holding a delegate that cannot survive a journal round-trip. Non-null exactly when
    // CountHookVerdicts is non-null -- same gate, same reason.
    string? HookVerdictLedgerFileName = null,
    // #1151: files copied verbatim into place when this execution starts, never clobbering a differing
    // existing file — see CoreDispatchSeedCopy for why this is not a CoreDispatchSeedFile carrying the
    // text. Appended last so no positional caller of the parameters above shifts.
    IReadOnlyList<CoreDispatchSeedCopy>? SeedCopies = null)
{
    /// <summary>
    /// #1373: returns this target with <paramref name="preamble"/> prepended to the instructional text
    /// the worker actually receives — <b>both</b> <see cref="PromptText"/> and the <see cref="Args"/>
    /// element that carries it, which must stay byte-identical.
    /// <para>
    /// Why both: <see cref="PromptText"/> is archival (<see cref="CoreDispatcher.DispatchAsync"/> writes
    /// it to <c>prompt.txt</c> for display, and CLAUDE.md Architecture Rule 1 forbids reading it back to
    /// route). The string the vendor CLI is actually invoked with is the <see cref="Args"/> element —
    /// every shipped adapter passes the same object as both (<c>["-p", prompt]</c> plus
    /// <c>PromptText: prompt</c>). Prepending to <see cref="PromptText"/> alone would put the preamble
    /// in the archive and nowhere else, and a test reading <c>prompt.txt</c> would certify it. Their
    /// identity is also what <see cref="CoreDispatcher.DispatchAsync"/>'s #748 oversize swap already
    /// depends on (it finds the prompt argument by <c>IndexOf(PromptText)</c>), so this rewrite
    /// preserves that lookup rather than breaking it.
    /// </para>
    /// </summary>
    /// <exception cref="PromptPreambleException">
    /// <see cref="PromptText"/> is set but no <see cref="Args"/> element equals it — the invariant above
    /// is broken, and the preamble would silently vanish. Refused loudly rather than dropped: a worker
    /// that never received its continuation brief looks exactly like one that ignored it.
    /// </exception>
    public CoreDispatchTarget WithPromptPreamble(string preamble)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preamble);

        if (PromptText is not { } promptText)
        {
            // An adapter with no prose prompt to prepend to (CommandWorkerAdapter's declared argv) —
            // a deliberate no-op, the same reading DispatchAsync's own null-PromptText guard takes.
            return this;
        }

        var args = Args.ToList();
        var promptArgIndex = args.IndexOf(promptText);
        if (promptArgIndex < 0)
        {
            throw new PromptPreambleException(
                $"Cannot prepend a prompt preamble for '{Program}': its PromptText is set but no argument " +
                "equals it, so the preamble would reach prompt.txt and never the worker. An adapter must " +
                "pass the same prompt string as both an argument and PromptText.");
        }

        var prefixed = preamble + promptText;
        args[promptArgIndex] = prefixed;
        return this with { Args = args, PromptText = prefixed };
    }
}

/// <summary>
/// A launch-configuration file an adapter needs written into place before its worker spawns, where the
/// destination and/or contents reference an AER-computed path (e.g. <c>BATON_OUTPUT_DIR</c>) that only
/// resolves inside <see cref="CoreDispatcher.DispatchAsync"/>. Both <paramref name="PathTemplate"/> and
/// <paramref name="Content"/> take the same <c>%NAME%</c>/<c>$NAME</c> placeholder grammar as target
/// arguments and environment values, and are expanded there. Kept vendor-agnostic on purpose: the
/// adapter owns what the file says (Adapter Isolation), the dispatcher only writes it.
/// </summary>
public sealed record CoreDispatchSeedFile(string PathTemplate, string Content);

/// <summary>
/// A file an adapter needs copied <em>verbatim</em> into place when an execution starts — the same
/// dispatch-time seam as <see cref="CoreDispatchSeedFile"/>, for content AER did not author and must not
/// rewrite. <paramref name="PathTemplate"/> takes the usual <c>%NAME%</c>/<c>$NAME</c> placeholder
/// grammar; the bytes at <paramref name="SourcePath"/> are copied unchanged, with no variable expansion
/// (#1929 review, HIGH at <c>ClaudeWorkerAdapter.Resolve</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Never clobbers.</b> An existing destination whose bytes differ from the source is left exactly as
/// it is and the copy is skipped — the guarantee is in the type rather than in a flag, because the first
/// user copies into the operator's own working directory, where a differing file is by definition
/// content AER did not put there. Identical bytes are rewritten harmlessly; nothing is ever pruned.
/// </para>
/// <para>
/// Deliberately byte-verbatim rather than a <see cref="CoreDispatchSeedFile"/> carrying the text: a seed
/// body is expanded by <see cref="CoreDispatcher.RenderSeedContent"/>, so a package file mentioning
/// <c>$BATON_OUTPUT_DIR</c> would land differing from its source, and any reader that predicts the
/// skip-vs-write outcome from the source bytes (the dispatch roster does) would then disagree with what
/// this loop actually did. One set of bytes, one predicate, two readers.
/// </para>
/// </remarks>
/// <param name="Group">
/// An adapter-authored label naming what this copy belongs to (the claude adapter passes the canonical
/// skill package's name), used only to compose the record the dispatcher writes after placing —
/// #1929 review MEDIUM asked that line to name the packages, and the engine must not learn a vendor's
/// grouping by parsing its paths (Architecture Rule 1). Echoed verbatim, never interpreted; null on a
/// copy with nothing to group by.
/// </param>
public sealed record CoreDispatchSeedCopy(string PathTemplate, string SourcePath, string? Group = null);

/// <summary>
/// The raw, unclassified facts of a completed dispatch (<c>NaturalExit</c> |
/// <c>TimedOut</c> | <c>CancelRequested</c> vocabulary). M7 Phase 6 explicitly excludes outcome
/// classification — mapping this into <c>ExecutionSucceeded</c>/<c>ExecutionFailed</c>/
/// <c>ExecutionCancelled</c> is the Outcome Classifier's job (Phase 7).
/// </summary>
/// <param name="StderrTail">
/// The last <see cref="CoreDispatcher.MaxRetainedStderrLength"/> characters the worker wrote to
/// stderr, or <see langword="null"/> if it wrote nothing (#563). The <i>tail</i> specifically: a
/// vendor CLI's actionable line is the last thing it prints, so head-first truncation would discard
/// exactly the message this field exists to carry.
/// <para>
/// Null also on the crash-recovery path, where <c>MutationInterface</c> rebuilds a result from a
/// stored <c>CoreEvent.ExecutionExited</c> after a restart — stderr was never written to the Event
/// Store, so it genuinely does not survive a crash. Read a null as "not recorded", never as "the
/// worker was silent".
/// </para>
/// </param>
/// <param name="TerminalSuccessObserved">
/// True when the worker emitted a <b>terminal success</b> event on stdout during the run — its vendor
/// CLI's own "I finished, status success" marker (agy's <c>{"event":"result","result":{"status":
/// "SUCCESS"}}</c>, claude's <c>{"type":"result","subtype":"success","is_error":false}</c>), detected by
/// the adapter (Adapter Isolation) via <see cref="CoreDispatchTarget.DetectsTerminalSuccess"/>. It is
/// the ONE fact that distinguishes "the worker finished, then hung at teardown" from "the worker was
/// killed mid-work": the <see cref="Outcomes.OutcomeClassifier"/> uses it to let a <c>TimedOut</c> run
/// whose declared outputs are all present classify as Succeeded instead of a doomed from-scratch retry
/// (#1089). False on the crash-recovery path and whenever the worker did not stream (no marker to see),
/// so the guard fails safe toward today's "a timeout always fails" behaviour.
/// </param>

/// <param name="StdoutTail">
/// The last <see cref="CoreDispatcher.MaxRetainedStderrLength"/> characters the worker wrote to
/// stdout, or <see langword="null"/> if it wrote nothing. The <i>tail</i> specifically: bounded
/// retention for failure classification (0026/#1115), allowing classifiers to inspect typed worker
/// outputs on stdout without loading full execution streams.
/// <para>
/// Null also on the crash-recovery path, where <c>MutationInterface</c> rebuilds a result from a
/// stored <c>CoreEvent.ExecutionExited</c> after a restart — stdout tail is not written to the Event
/// Store, so it does not survive a crash.
/// </para>
/// </param>
public sealed record CoreDispatchResult(
    int ExitCode,
    CoreExitReason Reason,
    string? StderrTail = null,
    bool TerminalSuccessObserved = false,
    string? StdoutTail = null,
    // F6 (#1593 review): latched from CoreDispatchTarget.DetectsTerminalResult — spec/baton.md §3 F6
    // is the register entry for why OutcomeClassifier's dead-worker predicate reads this field rather
    // than TerminalSuccessObserved.
    bool TerminalResultObserved = false,
    // #1929 review HIGH: the absolute paths this dispatch's own SeedCopies actually placed inside the
    // worker's working directory, so the workspace readers can subtract AER's writes from what they
    // attribute to the worker. What was WRITTEN, not what was planned — see CoreDispatchSeedCopy and
    // WorktreeProvisioner.ChangedPathsExcludingEnginePlaced. On the crash-recovery path, which rebuilds
    // a result from a recorded exit, MutationInterface refills this from the journaled
    // FlowEvent.EngineFilesPlaced through the projection (#1933) — unlike StderrTail/StdoutTail above,
    // it does survive a crash. Null only when no such fact was recorded, which counts the paths.
    IReadOnlyList<string>? EnginePlacedPaths = null,
    // The adapter's own labels for what those paths belong to (CoreDispatchSeedCopy.Group), for the
    // room fact's benefit. Echoed, never interpreted — Architecture Rule 1. Stays null on the
    // crash-recovery path: nothing downstream of a rebuilt result reads it (#1933).
    IReadOnlyList<string>? EnginePlacedGroups = null);


/// <summary>
/// What <c>MutationInterface</c> needs from a dispatcher ("Flow never executes a
/// process; it only ever reads the Event Store and emits requests" — this is the seam through
/// which it emits them). Extracted from <see cref="CoreDispatcher"/> so mutation-level tests can
/// substitute a stub with <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/>-controlled
/// completion order (M8 Phase 3) instead of spawning real processes.
/// </summary>
public interface ICoreDispatcher
{
    /// <inheritdoc cref="CoreDispatcher.DispatchAsync"/>
    Task<CoreDispatchResult> DispatchAsync(
        ExecutionRequest request,
        CoreDispatchTarget target,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Accumulates the tail of a worker's output stream as chunks arrive from the native callback
/// (#563): decodes, collapses whitespace, and keeps at most
/// <see cref="CoreDispatcher.MaxRetainedStderrLength"/> characters. Named for the stderr capture
/// it was built for; since #1115 a second instance captures the stdout tail
/// (<see cref="CoreDispatchResult.StdoutTail"/>) with identical, stream-agnostic mechanics.
/// </summary>
/// <remarks>
/// <para>
/// The three pieces of state are one object rather than three parallel locals because they are only
/// correct together: the decoder must be stateful across chunks, and so must
/// <see cref="pendingSpace"/>, or a whitespace run split across a chunk boundary collapses to two
/// spaces instead of one.
/// </para>
/// <para>
/// <b>Whitespace is collapsed here, at capture time, and that placement is the fix for a real
/// defect rather than a tidiness choice.</b> It used to happen in <c>OutcomeClassifier</c>, i.e.
/// <i>between</i> the retention cap below and the display cap there — so the two caps measured
/// different units and the "a silent drop always implies a marked drop" guarantee did not hold. Two
/// concrete failures came out of that ordering: stderr that was mostly indentation could lose
/// thousands of characters to the silent cap and still collapse to under the display cap, showing an
/// operator a truncated tail with no ellipsis; and a worker that printed a diagnostic followed by
/// enough blank lines to fill the buffer had its tail retained as pure whitespace, which collapsed to
/// nothing and restored the exact bare reason this issue exists to replace. Collapsing first makes
/// both caps count the same characters, so the ordering argument is sound and both failures are
/// impossible rather than merely reported.
/// </para>
/// </remarks>
internal sealed class StderrTailBuffer
{
    private readonly System.Text.StringBuilder buffer = new();
    private readonly System.Text.Decoder decoder = System.Text.Encoding.UTF8.GetDecoder();

    /// <summary>
    /// Whether a whitespace run has been seen whose space has not been emitted yet. Deferred rather
    /// than emitted on sight, so runs collapse to one space and neither a leading nor a trailing one
    /// is ever written.
    /// </summary>
    private bool pendingSpace;

    /// <summary>Decodes one chunk of stderr bytes and folds it into the retained tail.</summary>
    public void Append(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // Stateful decode, not one GetString per chunk: a pipe splits at arbitrary byte offsets, so a
        // multi-byte UTF-8 sequence routinely straddles two chunks. Decoding each chunk independently
        // emits a replacement character at every such boundary, corrupting exactly the non-ASCII
        // diagnostics this exists to carry.
        // GetChars runs even when the count is zero, and skipping it was a real bug rather than an
        // optimisation. GetCharCount is a pure calculation — it does NOT hand the bytes to the
        // decoder — so returning early on a zero count discarded them: the decoder never saw the
        // partial sequence it was supposed to be holding, and the next chunk then began with a
        // continuation byte it could only render as U+FFFD. It shows up solely when a chunk decodes
        // to nothing at all, i.e. when the very first bytes of the stream are a split multi-byte
        // character, which is why only the 2-byte split case in the theory catches it.
        var maxChars = decoder.GetCharCount(data, 0, data.Length, flush: false);
        var chars = new char[maxChars];
        var written = decoder.GetChars(data, 0, data.Length, chars, 0, flush: false);
        if (written > 0)
        {
            AppendCollapsed(chars.AsSpan(0, written));
        }
    }

    /// <summary>
    /// Returns the retained tail, or <see langword="null"/> if the worker wrote nothing that survived
    /// collapsing — which must stay distinguishable from "wrote something", since a caller renders an
    /// empty tail as no tail at all rather than as an empty label.
    /// </summary>
    public string? ToTailOrNull()
    {
        // Flushing emits U+FFFD for a trailing sequence the worker cut short (it died mid-write).
        // Better a visible replacement character than silently dropping the final character of the
        // very line being diagnosed.
        var maxChars = decoder.GetCharCount([], 0, 0, flush: true);
        if (maxChars > 0)
        {
            var chars = new char[maxChars];
            var written = decoder.GetChars([], 0, 0, chars, 0, flush: true);
            AppendCollapsed(chars.AsSpan(0, written));
        }

        return buffer.Length > 0 ? buffer.ToString() : null;
    }

    private void AppendCollapsed(ReadOnlySpan<char> chars)
    {
        foreach (var ch in chars)
        {
            if (char.IsWhiteSpace(ch))
            {
                // Suppressed while the buffer is empty, so a leading run never emits anything.
                pendingSpace = buffer.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                buffer.Append(' ');
                pendingSpace = false;
            }

            buffer.Append(ch);
        }

        TrimToTail(buffer);
    }

    /// <summary>
    /// Drops the oldest characters so <paramref name="target"/> holds at most
    /// <see cref="CoreDispatcher.MaxRetainedStderrLength"/> — keeping the <i>end</i>, which is where
    /// a vendor CLI puts the line worth reading.
    /// </summary>
    internal static void TrimToTail(System.Text.StringBuilder target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.Length <= CoreDispatcher.MaxRetainedStderrLength)
        {
            return;
        }

        var excess = target.Length - CoreDispatcher.MaxRetainedStderrLength;

        // Cutting from the front is the mirror of ContractValidator.TrimWithoutSplittingSurrogatePair,
        // which cuts from the back: if the first surviving char is a low surrogate, its high half is
        // among the ones being removed, so drop the orphan too rather than leaving a lone half-pair.
        // The bounds guard is unreachable while MaxRetainedStderrLength is positive, and is here for
        // the same reason its counterpart there is: this runs inside a native callback, where an
        // IndexOutOfRangeException would surface far from the edit that lowered the cap.
        if (excess < target.Length && char.IsLowSurrogate(target[excess]))
        {
            excess++;
        }

        target.Remove(0, excess);
    }
}

/// <summary>
/// Accumulates a worker's stdout and hands back whole lines, decoding STATEFULLY across chunks
/// (#642).
/// </summary>
/// <remarks>
/// Extracted from <c>RunAsync</c>'s event loop so it can be driven at chosen byte offsets. The
/// decode used to sit inline as a stateless <c>Encoding.UTF8.GetString</c> per chunk, which was
/// unreachable from a test: a pipe splits where it likes, so the defect needed a boundary landing
/// mid-character and could not be provoked deterministically through a real process.
/// <para>
/// <see cref="StderrTailBuffer"/> had carried a <c>Decoder</c> since it was written and this path
/// never did. That asymmetry is the wrong way round: stdout is the worker's own output, the text
/// rendered in the Conversation tab, so it had the weaker treatment where it mattered more.
/// </para>
/// <para>
/// NOT thread-safe, deliberately and like its stderr sibling — the caller already holds a lock for
/// the line buffer, and the decoder's cross-chunk state has to be inside that same lock rather than
/// beside it.
/// </para>
/// </remarks>
internal sealed class StdoutLineBuffer
{
    /// <summary>
    /// The ceiling on a newline-free run this buffer will hold before splitting it (#701).
    /// </summary>
    /// <remarks>
    /// Measured before chosen (#701 required exactly that order): the longest single line across
    /// 68,399 lines of the vendor CLIs' own JSONL streams on the measuring machine was 1,346,950
    /// bytes — a <c>claude</c> stream-json line; <c>agy</c>'s longest was 8,529. This ceiling is
    /// roughly six times that worst case, so no legitimately long line observed to date comes near
    /// a split, while a runaway newline-free stream (a <c>\r</c> progress bar, binary on the wrong
    /// descriptor) is bounded inside the daemon's process. Split-with-marker was chosen over the
    /// stderr sibling's keep-the-tail because stdout is what the Conversation tab renders and is
    /// read top-down: every character still arrives, in order, and the fabricated boundary is the
    /// marked thing rather than the dropped thing.
    /// </remarks>
    public const int MaxBufferedLineLength = 8_000_000;

    /// <summary>
    /// Appended to every synthetic line the ceiling fabricates, so an operator reading a fragment
    /// can tell it is one — the silent-fragment outcome is the one #701 names as unacceptable.
    /// </summary>
    public static readonly string SplitMarker =
        $" ⟦AER: no newline for {MaxBufferedLineLength:N0} characters — line split by the engine⟧";

    private readonly System.Text.StringBuilder buffer = new();
    private readonly System.Text.Decoder decoder = System.Text.Encoding.UTF8.GetDecoder();

    /// <summary>Decodes one chunk and emits every complete line it completes.</summary>
    public void Append(byte[] data, Action<string> onLine)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(onLine);

        // GetChars runs even when the count is zero. GetCharCount is a pure calculation and does NOT
        // hand the bytes to the decoder, so returning early on a zero count would discard the partial
        // sequence the decoder is meant to be holding — the defect StderrTailBuffer records having
        // shipped, and the reason the 2-byte split arm exists in both theories.
        var maxChars = decoder.GetCharCount(data, 0, data.Length, flush: false);
        var chars = new char[maxChars];
        var written = decoder.GetChars(data, 0, data.Length, chars, 0, flush: false);
        buffer.Append(chars, 0, written);

        var content = buffer.ToString();
        int newlineIndex;
        while ((newlineIndex = content.IndexOf('\n', StringComparison.Ordinal)) >= 0)
        {
            onLine(content[..newlineIndex].TrimEnd('\r'));
            content = content[(newlineIndex + 1)..];
        }

        // The remainder is a line still waiting for its newline, and a worker that never sends one
        // must not grow it forever — see MaxBufferedLineLength for the measurement behind the
        // ceiling and why splitting beats retaining a tail here. Strictly greater than: a run
        // exactly AT the ceiling is still a legitimate line waiting, never split.
        while (content.Length > MaxBufferedLineLength)
        {
            // The cut index counts UTF-16 chars, and a code point above the BMP is a surrogate
            // PAIR — cutting between its halves emits a lone surrogate that any downstream UTF-8
            // re-encode silently replaces with U+FFFD, breaking "every character still arrives".
            // Same guard StderrTailBuffer has always had at its own cut point: back off by one.
            var cut = MaxBufferedLineLength;
            if (char.IsHighSurrogate(content[cut - 1]))
            {
                cut--;
            }

            onLine(content[..cut] + SplitMarker);
            content = content[cut..];
        }

        buffer.Clear();
        buffer.Append(content);
    }

    /// <summary>Emits whatever is left when the stream ends without a trailing newline.</summary>
    public void Flush(Action<string> onLine)
    {
        ArgumentNullException.ThrowIfNull(onLine);

        // Draining the decoder is what makes a stateful decode safe at end-of-stream, and it is the
        // half a chunk-boundary test cannot reach: no mutation of Append turns this red. Without it a
        // stateful decode is STRICTLY WORSE here than the stateless one it replaced — bytes the
        // decoder is holding for a sequence the worker never finished are simply dropped, and when
        // they are all that is left the final line disappears entirely rather than arriving as U+FFFD.
        // See StderrTailBuffer.ToTailOrNull, which has always done this, for why visible beats silent.
        var maxChars = decoder.GetCharCount([], 0, 0, flush: true);
        if (maxChars > 0)
        {
            var chars = new char[maxChars];
            var written = decoder.GetChars([], 0, 0, chars, 0, flush: true);
            buffer.Append(chars, 0, written);
        }

        if (buffer.Length > 0)
        {
            onLine(buffer.ToString());
            buffer.Clear();
        }
    }
}


/// <summary>
/// Calls the managed <c>BatonTask</c> engine with an <see cref="ExecutionRequest"/> and records
/// Core's lifecycle events to the combined log (M7 Phase 6). This is the only place in
/// <c>Baton</c> that touches <c>Baton.Core</c> directly.
/// </summary>
/// <param name="streamLogLossJournal">
/// #1885: the journal handle for the ONE flow event this dispatcher writes —
/// <see cref="FlowEvent.StreamLogLossDeclared"/>. #1888 made that a fact about the type rather than a
/// promise in this comment: the parameter is an <see cref="IStreamLogLossJournal"/>, which admits that
/// event and no other, so appending anything else here does not compile.
/// Required rather than optional
/// precisely because the alternative fails open with plausible output: a caller that forgot to pass one
/// would still write the marker file on most hosts, so the missing second channel would only ever be
/// noticed on the host that needed it. <see cref="Store.ICoreEventLogWriter"/>'s own doc states the
/// caller/half separation this sits alongside, and why.
/// </param>
public sealed class CoreDispatcher(ICoreEventLogWriter coreEventLogWriter, IStreamLogLossJournal streamLogLossJournal) : ICoreDispatcher
{
    /// <summary>
    /// How many characters of a worker's stderr are retained for
    /// <see cref="CoreDispatchResult.StderrTail"/> (#563).
    /// </summary>
    /// <remarks>
    /// Deliberately larger than <c>OutcomeClassifier</c>'s own display cap. This bound exists to stop
    /// a chatty worker from growing an unbounded buffer in a native callback; deciding how much of it
    /// an operator actually reads is the classifier's job, and pre-truncating here to the display
    /// size would take that choice away from it.
    /// </remarks>
    public const int MaxRetainedStderrLength = 2000;

    /// <summary>
    /// #1885: the reason string a journalled stream-log loss carries. Deliberately the SAME const the
    /// marker channel already yields (<c>ExecutionUsageProjector</c>'s write-failure arm), because the
    /// two channels announce one fact and <c>ExecutionUsageView</c> compares them for agreement — see
    /// <see cref="FlowEvent.StreamLogLossDeclared.Reason"/>. A logger only ever declares a loss for the
    /// write-failure cause; the rollover cause is announced by its own marker and never reaches here.
    /// An alias, not a second spelling: #1883 made <c>ExecutionUsageView</c> the one place the reason
    /// vocabulary is written. Sharing the const is what makes the two channels agree BY CONSTRUCTION —
    /// which is deliberate, and is why <c>WarnOnChannelDisagreement</c>'s own doc says a disagreement
    /// is unreachable from one execution's writer today. It exists for a hand-edited ledger or a future
    /// third producer, not to catch a typo this const has now made impossible.
    /// </summary>
    private const string StreamLogLossReason = Status.ExecutionUsageView.StreamTruncatedByWriteFailureReason;

    /// <summary>
    /// Expanded-prompt length at which <see cref="DispatchAsync"/> stops passing the prompt inline
    /// and swaps in the adapter's <see cref="CoreDispatchTarget.OversizePromptWrapper"/> pointing at
    /// the already-captured <c>prompt.txt</c> (#748). Deliberately far below every platform
    /// command-line cap this class guards, and fixed rather than derived from them, so the same
    /// workflow delivers its prompt the same way on every OS.
    /// </summary>
    public const int OversizePromptThreshold = 4000;

    /// <summary>
    /// The assembled-command-line ceiling <see cref="DispatchAsync"/> guards against on Windows
    /// (#598), held below <c>CreateProcessW</c>'s documented 32,767-character <c>lpCommandLine</c>
    /// maximum. <see cref="MeasureCommandLineLength"/> is an upper bound, so this margin is not load
    /// bearing the way it was when the measure could under-count; it covers the terminating NUL that
    /// bound omits, and leaves room for the bound to be tightened later without moving the ceiling.
    /// </summary>
    internal const int WindowsCommandLineCeiling = 32_000;

    /// <summary>
    /// The single-integer, UTF-16 command-line ceiling this platform is guarded against — Windows'
    /// <c>CreateProcessW</c> <c>lpCommandLine</c> maximum, measured here against #579's
    /// <c>Win32Exception (206)</c> (Windows-only, #1405).
    /// </summary>
    internal static int PlatformCommandLineCeiling => WindowsCommandLineCeiling;

    /// <summary>
    /// An upper bound on the command line <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// assembles from <paramref name="program"/> and <paramref name="args"/> when <c>BatonTask</c>
    /// spawns the process: each argument contributes its own characters, a separating space, a
    /// surrounding quote pair, and the worst case of the runtime's escaping.
    /// </summary>
    /// <remarks>
    /// A bound rather than an exact reproduction, deliberately: being exact would mean reimplementing
    /// the BCL's Windows argument-quoting rules here and holding them in step with a runtime this repo
    /// does not vendor — a claim about someone else's internals no test of ours could keep honest. But a
    /// bound only has to be an over-estimate to be sound, which needs far less than the real rules.
    /// <para>
    /// Escaping never adds more than one character per <c>"</c> plus one per <c>\</c> in an argument:
    /// the same MSVCRT-compatible convention the BCL follows emits <c>2n+1</c> backslashes for an
    /// interior quote preceded by <c>n</c> of them (<c>n+1</c> beyond what the raw characters already
    /// contribute) and doubles a trailing backslash run (<c>n</c> beyond). Counting one for each of
    /// those characters therefore cannot under-shoot.
    /// </para>
    /// <para>
    /// This started as <c>Length + 3</c> with no escape term, on the reasoning that under-counting only
    /// reproduces today's OS-level failure rather than regressing it. True, but it made the guard miss
    /// an ordinary case: review of #598 pointed out that roughly 768 quote characters in a near-ceiling
    /// argument exhaust the whole margin below 32,767, and a prompt quoting JSON, a schema, or a file's
    /// contents reaches that easily. So the bound is exact enough to not need the margin — the margin
    /// now covers only <see cref="string.Length"/> counting UTF-16 code units, which is what
    /// <c>CreateProcessW</c> counts too, and the terminating NUL that is not counted here.
    /// </para>
    /// </remarks>
    internal static int MeasureCommandLineLength(string program, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(args);

        // The program is quoted but not preceded by a separator; every argument after it is.
        var length = EscapedLength(program) + 2;
        foreach (var arg in args)
        {
            length += EscapedLength(arg) + 3;
        }

        return length;
    }

    /// <summary>
    /// One value's characters plus the most std's Windows escaping can add to them — see
    /// <see cref="MeasureCommandLineLength"/>'s remarks for why one per <c>"</c> and one per <c>\</c>
    /// is an over-estimate rather than a reproduction of the real rules.
    /// </summary>
    private static int EscapedLength(string value)
    {
        var length = value.Length;
        foreach (var character in value)
        {
            if (character is '"' or '\\')
            {
                length++;
            }
        }

        return length;
    }

    /// <summary>
    /// Throws <see cref="CommandLineTooLongException"/> when <paramref name="program"/> and
    /// <paramref name="args"/> would assemble past <paramref name="ceiling"/> (#598). Takes the
    /// ceiling as an argument rather than reading <see cref="PlatformCommandLineCeiling"/> itself, so
    /// that the boundary is exercisable on every OS the test suite runs on and not only the one whose
    /// limit is being enforced.
    /// </summary>
    internal static void GuardCommandLineLength(string program, IReadOnlyList<string> args, int ceiling)
    {
        var length = MeasureCommandLineLength(program, args);
        if (length <= ceiling)
        {
            return;
        }

        // Report the longest single argument alongside the total rather than naming a cause. Both
        // adapters embed the whole prompt as one argument, so that figure is the prompt nearly every
        // time — but not always: a long PermissionScope or several --add-dir paths contribute too, and
        // an operator whose longest argument turns out to be small needs to see that rather than be
        // sent to shorten content that was never the problem. The guidance points at the fix decision
        // 0048 settled on — file-passing — not "make the prompt shorter", because the overflow is
        // almost always inlined content, which belongs in a file the worker reads.
        var longest = args.Count == 0 ? 0 : args.Max(arg => arg.Length);
        throw new CommandLineTooLongException(
            $"Cannot dispatch '{program}': its command line assembles to about {length} characters, "
            + $"past the {ceiling} this platform is guarded at. Its longest single argument is "
            + $"{longest} characters — a worker's prompt is passed inline as one argument. Hand large "
            + "content to the worker as a file it reads under its read-files grant (as the review workflow "
            + "does), rather than inlining it in the prompt.");
    }

    /// <summary>
    /// The exact environment the spawned child receives, in application order: the inherited allowlist
    /// (<see cref="InheritedEnvironment"/>), then <paramref name="request"/>'s AER-computed variables,
    /// then <paramref name="target"/>'s own adapter variables — later entries overriding earlier ones by
    /// name when applied, the ordering <c>ClaudeWorkerAdapter.SimpleModeVariable</c> depends on. One
    /// assembly point so there is a single place these three sources are enumerated, rather than two
    /// that could drift the moment a fourth is added.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Value)> AssembleChildEnvironment(
        ExecutionRequest request, CoreDispatchTarget target)
    {
        var environment = new List<(string Name, string Value)>();
        environment.AddRange(InheritedEnvironment.Resolve());

        var pathVariables = request.Environment
            .OfType<EnvironmentVariable.BatonComputed>()
            .ToDictionary(v => v.Name, v => v.Value);

        foreach (var environmentVariable in request.Environment)
        {
            // PassThrough variable *values* are resolved by whatever wires a concrete worker adapter
            // — out of scope here. Only AER-computed variables (paths the Artifact Manager
            // already resolved) are set.
            if (environmentVariable is EnvironmentVariable.BatonComputed batonComputed)
            {
                environment.Add((batonComputed.Name, batonComputed.Value));
            }
        }

        // Target environment VALUES take the same placeholder grammar as target arguments (#442: the
        // agy per-execution home references BATON_OUTPUT_DIR, which only exists here). Expansion is
        // keyed on the computed-variable names, so a value carrying no such token is untouched.
        if (target.Environment is { } targetEnvironment)
        {
            foreach (var (name, value) in targetEnvironment)
            {
                environment.Add((name, ExpandVariables(value, pathVariables)));
            }
        }

        return environment;
    }

    /// <summary>
    /// Spawns <paramref name="target"/> with <paramref name="request"/>'s AER-computed environment
    /// variables and timeout, and returns once the process has exited, timed out, or been
    /// cancelled. Never throws for any of those three outcomes — each is a normal result the
    /// Outcome Classifier must later classify, not an error condition — but does not suppress
    /// genuine dispatch failures (e.g. the binary could not be spawned at all), which propagate as
    /// <see cref="BatonException"/>.
    /// </summary>
    public async Task<CoreDispatchResult> DispatchAsync(
        ExecutionRequest request,
        CoreDispatchTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(target);

        // Resolve variable values from request.Environment
        var pathVariables = request.Environment
            .OfType<EnvironmentVariable.BatonComputed>()
            .ToDictionary(v => v.Name, v => v.Value);

        // Perform expansion on target arguments
        var expandedArgs = target.Args.Select(arg => ExpandVariables(arg, pathVariables)).ToList();

        var childEnvironment = AssembleChildEnvironment(request, target);

        // Issue #292: durably capture the resolved prompt a step's worker was actually invoked with
        // (CLAUDE.md Architecture Rule 1: archival capture for UI display, never read back to make a
        // routing decision). Written before BatonTask ever spawns (below), so it is present even if
        // the execution later fails or times out. Null PromptText (an adapter with nothing to
        // capture) is a deliberate no-op, not a missing-data condition.
        if (target.PromptText is { } promptText && pathVariables.TryGetValue("BATON_OUTPUT_DIR", out var outputDirectory))
        {
            var promptFilePath = Path.Combine(outputDirectory, ArtifactManager.PromptFileName);
            var expandedPromptText = ExpandVariables(promptText, pathVariables);
            await File.WriteAllTextAsync(promptFilePath, expandedPromptText, CancellationToken.None)
                .ConfigureAwait(false);

            // #748: when the adapter provides an OversizePromptWrapper and the expanded prompt length
            // reaches or exceeds OversizePromptThreshold, swap the inline prompt argument for the
            // expanded wrapper and pass BATON_PROMPT_FILE in the child environment so command-line
            // guards measure the shortened argument list.
            if (target.OversizePromptWrapper is { } wrapper && expandedPromptText.Length >= OversizePromptThreshold)
            {
                var promptArgIndex = target.Args.ToList().IndexOf(promptText);
                if (promptArgIndex >= 0)
                {
                    pathVariables["BATON_PROMPT_FILE"] = promptFilePath;
                    expandedArgs[promptArgIndex] = ExpandVariables(wrapper, pathVariables);

                    var updatedChildEnvironment = childEnvironment.ToList();
                    updatedChildEnvironment.Add(("BATON_PROMPT_FILE", promptFilePath));
                    childEnvironment = updatedChildEnvironment;
                }
            }
        }

        // Seed vendor-declared launch files (Adapter Isolation: the adapter owns the contents) whose
        // path and/or body reference an AER-computed variable that only resolves here — the same reason
        // the prompt capture and the agy per-execution home live at this point. agy's own settings.json
        // carrying a permissions.allow for the granted write is the first user (#1084): a write-granted
        // agy role with no shell/network runs under --mode accept-edits, where agy headless-denies the
        // write unless an allow-rule is present; the hook still bounds where the write may land.
        if (target.SeedFiles is { Count: > 0 } seedFiles)
        {
            foreach (var seed in seedFiles)
            {
                var seedPath = ExpandVariables(seed.PathTemplate, pathVariables);
                var seedDirectory = Path.GetDirectoryName(seedPath);
                if (!string.IsNullOrEmpty(seedDirectory))
                {
                    Directory.CreateDirectory(seedDirectory);
                }

                await File.WriteAllTextAsync(seedPath, RenderSeedContent(seed.Content, pathVariables), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        // #1151: verbatim copies, same dispatch-time seam, different guarantee — see
        // CoreDispatchSeedCopy. Never clobbers a differing destination, never prunes, and never fails
        // the dispatch: an unreadable source or a locked destination costs that one file and says so,
        // rather than throwing out of a path whose whole point is that it runs before every execution.
        // #1929 review HIGH: the paths this dispatch actually placed, so the workspace readers can
        // subtract AER's own writes from what they attribute to the worker. Collected here because
        // this is the only place that knows which copies were MADE rather than merely planned -- a
        // destination holding different bytes is skipped below, and must not be excluded from the
        // evidence, since AER did not write it.
        var enginePlacedPaths = new List<string>();
        var placedGroups = new List<string>();
        if (target.SeedCopies is { Count: > 0 } seedCopies)
        {
            foreach (var copy in seedCopies)
            {
                var destinationPath = ExpandVariables(copy.PathTemplate, pathVariables);
                try
                {
                    if (File.Exists(destinationPath) && !FilesHaveIdenticalBytes(copy.SourcePath, destinationPath))
                    {
                        continue;
                    }

                    var destinationDirectory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destinationDirectory))
                    {
                        Directory.CreateDirectory(destinationDirectory);
                    }

                    File.Copy(copy.SourcePath, destinationPath, overwrite: true);
                    enginePlacedPaths.Add(destinationPath);
                    if (copy.Group is { Length: > 0 } group && !placedGroups.Contains(group))
                    {
                        placedGroups.Add(group);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    Console.Error.WriteLine(
                        $"Warning: could not place '{destinationPath}' from '{copy.SourcePath}': {ex.Message}");
                }
            }

            // #1929 review MEDIUM: the adapter's announce at resolve time is future tense ("will be
            // placed ... when a dispatch of this binding runs") and a plan can be declared and then
            // placed zero times. This is the record of the ACT, written after the loop so its count is
            // what was copied, not what was intended. The room-visible half is
            // FlowEvent.SkillPackagesProjected, appended by MutationInterface from the same list.
            var groups = placedGroups.Count > 0 ? $" ({string.Join(", ", placedGroups)})" : string.Empty;
            var root = CommonDirectory(enginePlacedPaths);
            var where = root is null ? string.Empty : $" into '{root}'";
            Console.Error.WriteLine(
                $"Placed {enginePlacedPaths.Count} of {seedCopies.Count} declared file(s){groups}{where} "
                + "for this execution.");
        }

        // #598: measured here, on the expanded arguments, because this is the only place the real
        // command line exists — an adapter builds `%BATON_OUTPUT_DIR%`, not the absolute path that
        // placeholder becomes above, so a guard living in an adapter would measure the wrong string.
        // Deliberately after the prompt capture: a command line long enough to trip this is a prompt
        // problem, and prompt.txt is the artifact an operator needs in order to see how it got that
        // big — throwing before writing it would withhold the evidence for the very failure reported.
        GuardCommandLineLength(target.Program, expandedArgs, PlatformCommandLineCeiling);

        // Only ever invoked for a WorkerBinding.Process dispatch (MutationInterface never calls a
        // dispatcher for a NonProcess execution) — Timeout is therefore always set.
        using var task = new BatonTask(target.Program, [.. expandedArgs]).WithTimeout(request.Timeout!.Value);

        if (target.WorkingDirectory is { } workingDirectory)
        {
            task.WithCwd(workingDirectory);
        }

        // Unconditional since #563. This used to be gated on `target.OnStdoutLine is not null`, i.e.
        // the live-streaming path only, which meant an ordinary `baton run` never captured — and
        // BatonProcessRunner's discard-path drain thread (RunDiscardingOutput's `sink: null` case)
        // reads and throws every byte away, so every byte the worker wrote explaining its own
        // failure was read and discarded.
        //
        // Nothing visible regresses by turning this on: BuildStartInfo already sets
        // RedirectStandardError unconditionally and never lets the child inherit the console, so
        // this output has never reached the operator's terminal and there is no inherited stream to
        // take away.
        //
        // BatonTask.WithCaptureOutput takes one bool covering both streams — there is no
        // stderr-only capture mode — so this also starts delivering StdoutChunk for non-chat
        // dispatches. That case is a no-op below, and the guard there is *decode-free*, not
        // allocation-free: by the time it runs, BatonProcessRunner's drain thread has already
        // copied the chunk into its own managed array (StartDrainThread's per-read `byte[] copy`)
        // and RunWithLiveCapture has allocated a BatonEventArgs for it. Those allocations are a
        // layer below anything this file can suppress. Chunks are 8 KiB, and a `-p` style adapter
        // produces tens of KB, so it is a handful of short-lived arrays per dispatch — gen0 churn,
        // not a leak. Stated precisely because the earlier wording here claimed the non-chat path
        // cost nothing, which would have been read as "we checked".
        task.WithCaptureOutput(true);

        // #549: the child inherited the operator's ENTIRE environment until WithClearEnv existed, so a
        // CLAUDE_CODE_SIMPLE=1 exported anywhere in the shell that started the daemon disabled the
        // mandatory gate on every worker, silently. WithClearEnv means the child sees only
        // childEnvironment, whose source order and override semantics AssembleChildEnvironment's own doc
        // states. See InheritedEnvironment for what survives.
        task.WithClearEnv();
        foreach (var (name, value) in childEnvironment)
        {
            task.WithEnv(name, value);
        }

        var exitCode = 0;
        var reason = CoreExitReason.Natural;
        var pendingLogWrites = new List<Task>();
        var stdoutLines = new StdoutLineBuffer();
        var stdoutLock = new object();

        // #563.
        var stderrTail = new StderrTailBuffer();
        var stderrLock = new object();

        // 0026 / #1115.
        var stdoutTail = new StderrTailBuffer();

        // #1089: the terminal-success signal. The adapter (Adapter Isolation) owns what its vendor's
        // "I finished, status success" line looks like; here we only invoke that predicate on each
        // complete stdout line and latch the flag. Combined with OnStdoutLine into one sink so a line is
        // decoded once, and non-null whenever EITHER a progress callback OR a detector is present -- so
        // detection works on the dispatch path even when nothing consumes progress. Mutated on
        // BatonTask's single event-delivery thread (BatonProcessRunner.RunWithLiveCapture's chunk
        // loop) under stdoutLock (below); read after the post-run Flush, which takes the same lock,
        // so the latch is visible.
        var terminalSuccessObserved = false;
        var terminalResultObserved = false;
        var detectsTerminalSuccess = target.DetectsTerminalSuccess;
        var detectsTerminalResult = target.DetectsTerminalResult;
        Action<string>? stdoutLineSink = target.OnStdoutLine;
        if (detectsTerminalSuccess is not null || detectsTerminalResult is not null)
        {
            var innerProgress = target.OnStdoutLine;
            stdoutLineSink = line =>
            {
                innerProgress?.Invoke(line);
                if (!terminalSuccessObserved && detectsTerminalSuccess is not null && detectsTerminalSuccess(line))
                {
                    terminalSuccessObserved = true;
                }

                if (!terminalResultObserved && detectsTerminalResult is not null && detectsTerminalResult(line))
                {
                    terminalResultObserved = true;
                }
            };
        }

        // #1885: the stream logger's declared losses, journalled as they are reported. The lock is not
        // ceremony: this callback fires on BatonTask's chunk-delivery thread for a mid-run loss and on
        // this method's own thread for the terminal re-announcement, and `pendingLogWrites` is a plain
        // List the Exited arm below also appends to. AppendAsync is started, never awaited, because the
        // callback runs while ExecutionStreamLogger holds its own lock -- and it is IStreamLogLossJournal
        // .AppendAsync's own "yield before you do I/O" clause, not this call site, that keeps starting it
        // there from blocking the chunk-delivery thread. Task.WhenAll below is what actually waits for
        // these, exactly like the Core events.
        var pendingLogWritesLock = new object();
        void JournalStreamLogLoss(ExecutionStreamLogger.StreamLogLoss loss)
        {
            var append = streamLogLossJournal.AppendAsync(
                new FlowEvent.StreamLogLossDeclared(
                    request.ExecutionId,
                    loss.StreamName,
                    StreamLogLossReason,
                    loss.BytesSurrendered,
                    loss.MarkerWritten,
                    loss.TerminalReannouncement),
                CancellationToken.None);
            lock (pendingLogWritesLock)
            {
                pendingLogWrites.Add(append);
            }
        }

        ExecutionStreamLogger? streamLogger = null;
        if (pathVariables.TryGetValue("BATON_OUTPUT_DIR", out var outputDir))
        {
            try
            {
                streamLogger = new ExecutionStreamLogger(outputDir, onLossDeclared: JournalStreamLogLoss);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to create execution stream logger for '{outputDir}': {ex.Message}. Stream logging disabled for this execution.");
            }
        }

        // #887 stage 2: a deterministic command step's stdout IS its declared artifact. Resolved
        // once here, not per chunk; per-chunk open-append-flush matches what
        // ExecutionStreamLogger already does for the stream logs. The lock is insurance against
        // a future second writer, NOT against concurrent chunks -- BatonTask's live-capture pump
        // (BatonProcessRunner.RunWithLiveCapture) invokes EventRaised synchronously on one thread
        // (its own remark below on the decode says the same), so chunk appends are already
        // serialized and ordered.
        //
        // Created EAGERLY, before dispatch: a well-behaved command whose success case is empty
        // stdout (an empty `git diff`, a no-match grep) produces zero chunks, and a lazily
        // created file would then never exist -- ContractValidator would fail a correct run
        // (#887 review, medium). Same create-regardless-of-content guarantee git's own
        // `--output` gives CaptureWorkerAdapter.
        var stdoutArtifactPath = target.StdoutArtifactName is not null && outputDir is not null
            ? Path.Combine(outputDir, target.StdoutArtifactName)
            : null;
        var stdoutArtifactLock = new object();
        if (stdoutArtifactPath is not null)
        {
            Directory.CreateDirectory(outputDir!);
            using var created = new FileStream(stdoutArtifactPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        }

        task.EventRaised += (_, e) =>
        {
            switch (e.Kind)
            {
                case BatonTaskEventKind.Started:
                    // CancellationToken.None, not cancellationToken: a cancellation firing is
                    // exactly what makes this record worth having (the crash clause depends on
                    // Started actually landing before a cancel/timeout/host-stop can be attributed
                    // to it), so recording it must not itself be cancellable by that same signal —
                    // the same reasoning DispatchAndRecordOutcomeAsync's outcome append already
                    // applies to its own append.
                    lock (pendingLogWritesLock)
                    {
                        pendingLogWrites.Add(coreEventLogWriter.AppendAsync(
                            new CoreEvent.ExecutionStarted(request.ExecutionId, e.Pid), CancellationToken.None));
                    }

                    break;

                case BatonTaskEventKind.StdoutChunk:
                    if (e.Data is { Length: > 0 })
                    {
                        try
                        {
                            // #1525 F5: ExecutionStreamLogger.AppendChunk already absorbs every
                            // ordinary IO failure internally (per-stream, per-chunk, never latching
                            // permanently since the F4 fix) -- the only exception shape that can still
                            // reach here is InvalidOperationException on a post-terminal append, which
                            // BatonProcessRunner's drain-before-Exited ordering makes unreachable in
                            // practice. This try/catch stays as defense against that invariant ever
                            // breaking, but no longer nulls streamLogger out: doing so on a STDOUT
                            // failure used to blind STDERR too (and vice versa below), duplicating the
                            // same cross-stream coupling F4 removed one layer down.
                            streamLogger?.AppendStdout(e.Data);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Warning: Failed to append stdout stream log: {ex.Message}.");
                        }

                        if (stdoutArtifactPath is not null)
                        {
                            lock (stdoutArtifactLock)
                            {
                                Directory.CreateDirectory(outputDir!);
                                using var fs = new FileStream(stdoutArtifactPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                                fs.Write(e.Data, 0, e.Data.Length);
                                fs.Flush();
                            }
                        }
                        lock (stdoutLock)
                        {
                            stdoutTail.Append(e.Data);
                            if (stdoutLineSink is not null)
                            {
                                // The decode is inside the lock, unlike the stateless GetString it replaces:
                                // the buffer now carries decoder state between chunks, so two callbacks
                                // decoding concurrently would interleave into one another's partial
                                // sequences. The lock was already here for the line buffer; the decode joins
                                // it rather than sitting beside it.
                                stdoutLines.Append(e.Data, stdoutLineSink);
                            }
                        }
                    }
                    break;

                case BatonTaskEventKind.StderrChunk:
                    if (e.Data is { Length: > 0 })
                    {
                        try
                        {
                            // See the matching stdout comment above -- same reasoning, same fix.
                            streamLogger?.AppendStderr(e.Data);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Warning: Failed to append stderr stream log: {ex.Message}.");
                        }

                        lock (stderrLock)
                        {
                            stderrTail.Append(e.Data);
                        }
                    }
                    break;

                case BatonTaskEventKind.Exited:
                    try
                    {
                        streamLogger?.MarkTerminal();
                    }
                    catch (Exception ex)
                    {
                        // CLAUDE.md: no silent catch. MarkTerminal drains the retry queue and retries a
                        // pending loss marker (#1879), both of which swallow their own IO failures, so
                        // this is not expected to fire -- best-effort terminal marking means the
                        // dispatch must not fail over it, not that a genuine exception here disappears
                        // unlogged.
                        Console.Error.WriteLine($"Warning: Failed to mark stream logger terminal: {ex.Message}.");
                    }

                    exitCode = e.ExitCode;
                    reason = ToCoreExitReason(e.ExitReason);
                    string? capturedStderrTail;
                    lock (stderrLock)
                    {
                        capturedStderrTail = stderrTail.ToTailOrNull();
                    }
                    lock (pendingLogWritesLock)
                    {
                        pendingLogWrites.Add(coreEventLogWriter.AppendAsync(
                            new CoreEvent.ExecutionExited(request.ExecutionId, e.ExitCode, reason, capturedStderrTail), CancellationToken.None));
                    }

                    break;
            }
        };

        try
        {
            // Dispatch(Exited) above has already run by the time RunAsync's Task completes (native
            // callbacks fire synchronously inside aer_task_run, which returns before RunAsync's
            // wrapping Task.Run does), so exitCode/reason are already set here on the natural path.
            await task.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BatonTimeoutException)
        {
            reason = CoreExitReason.TimedOut;
        }
        catch (BatonCancelException)
        {
            reason = CoreExitReason.CancelRequested;
        }
        finally
        {
            streamLogger?.MarkTerminal();
        }

        Task[] logWrites;
        lock (pendingLogWritesLock)
        {
            logWrites = [.. pendingLogWrites];
        }

        await Task.WhenAll(logWrites).ConfigureAwait(false);

        bool terminalSuccessLatched;
        bool terminalResultLatched;
        string? capturedStdoutTail;
        lock (stdoutLock)
        {
            if (stdoutLineSink is not null)
            {
                stdoutLines.Flush(stdoutLineSink);
            }

            // Read under the same lock the sink mutates, and AFTER Flush drains the last buffered line --
            // a terminal `result` arriving in the final chunk is only latched once Flush runs it.
            terminalSuccessLatched = terminalSuccessObserved;
            terminalResultLatched = terminalResultObserved;
            capturedStdoutTail = stdoutTail.ToTailOrNull();
        }

        string? capturedStderr;
        lock (stderrLock)
        {
            capturedStderr = stderrTail.ToTailOrNull();
        }

        return new CoreDispatchResult(
            exitCode, reason, capturedStderr, terminalSuccessLatched, capturedStdoutTail, terminalResultLatched,
            enginePlacedPaths, placedGroups);

    }


    /// <summary>
    /// The deepest directory containing every path in <paramref name="paths"/>, or null when there is
    /// none to name (no paths, or paths on different roots). Used only to say WHERE a placement landed
    /// in the line the dispatcher writes after copying (#1929 review MEDIUM) — never to decide anything.
    /// </summary>
    private static string? CommonDirectory(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return null;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var common = Path.GetDirectoryName(paths[0]);
        for (var i = 1; i < paths.Count && !string.IsNullOrEmpty(common); i++)
        {
            var candidate = Path.GetDirectoryName(paths[i]);
            while (!string.IsNullOrEmpty(common)
                   && !(candidate is not null
                        && (candidate.Equals(common, comparison)
                            || candidate.StartsWith(common + Path.DirectorySeparatorChar, comparison))))
            {
                common = Path.GetDirectoryName(common);
            }
        }

        return string.IsNullOrEmpty(common) ? null : common;
    }

    private static CoreExitReason ToCoreExitReason(BatonExitReason reason) => reason switch
    {
        BatonExitReason.Natural => CoreExitReason.Natural,
        BatonExitReason.TimedOut => CoreExitReason.TimedOut,
        BatonExitReason.CancelRequested => CoreExitReason.CancelRequested,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown BatonExitReason."),
    };

    /// <summary>
    /// The one home of the placeholder token grammar (#713): <c>%NAME%</c>, <c>${NAME}</c>, or
    /// <c>$NAME</c> where the name ends at the first non-identifier character. A name that is not
    /// an AER-computed variable stays literal — this expands AER's own placeholders, it is not a
    /// shell. <c>Baton.Vendors.WorkerEnvironmentReference</c> is where a reference is
    /// <em>written</em>; this is where every reference is <em>expanded</em>, and no other layer
    /// expands one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>$NAME</c> form previously had no boundary — a bare <c>Replace</c> — so any longer
    /// word beginning with a variable's name got the value spliced in mid-word
    /// (<c>$BATON_OUTPUT_DIRECTORY</c> became the path plus <c>ECTORY</c>), and <c>${NAME}</c>, the
    /// ordinary way to disambiguate, was not recognised at all. One pass over the string rather
    /// than one pass per variable also means a substituted <em>value</em> is never itself
    /// re-scanned, and the boundary makes longest-name-first ordering unnecessary: a name that is
    /// a prefix of a longer identifier simply does not match it.
    /// </para>
    /// <para>
    /// Three edges the grammar sentence alone does not decide, found by this change's reviewer and
    /// stated here so they are decided once. There is <b>no escape</b>: a known name always
    /// expands, in every form, and only unknown names stay literal. An unknown <c>%…%</c> pair
    /// consumes its closing <c>%</c>, so in the pathological <c>%A%BATON_OUTPUT_DIR%</c> the unknown
    /// <c>%A%</c> also keeps the known name from expanding — write <c>%%</c> pairs or reorder;
    /// AER's own emissions never produce that shape. And <c>\w</c> is Unicode-wide where AER's
    /// computed names are ASCII, so a non-ASCII letter after a known name reads as more identifier
    /// and the token stays literal — an under-expansion, never a mis-expansion.
    /// </para>
    /// </remarks>
    private static readonly System.Text.RegularExpressions.Regex VariableToken = new(
        @"%(?<name>\w+)%|\$\{(?<name>\w+)\}|\$(?<name>\w+)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Expands every <see cref="VariableToken"/> — the grammar and its edges live there.</summary>
    private static string ExpandVariables(string arg, Dictionary<string, string> vars) =>
        VariableToken.Replace(
            arg,
            match => vars.TryGetValue(match.Groups["name"].Value, out var value) ? value : match.Value);

    /// <summary>
    /// Expands a seed file's CONTENT against forward-slashed variable values — distinct from the seed
    /// PATH, which expands natively. AER-computed variables are absolute paths, and on Windows their
    /// raw value carries backslashes (<c>C:\Users\...</c>). A seed body is frequently JSON (agy's
    /// <c>settings.json</c> is the first user, #1084), where a substituted <c>C:\U…</c> is an invalid
    /// string escape that voids the whole file — so an allow-rule inside it would silently never load
    /// and the write it was meant to permit would still be denied. Forward slashes are valid JSON, a
    /// path Windows still accepts, and the exact form agy normalises both rule and target to before
    /// comparing, so the rule still matches. The path stays native because
    /// <see cref="Directory.CreateDirectory(string)"/> and <see cref="File.WriteAllTextAsync(string,string?,CancellationToken)"/>
    /// want the platform separator.
    /// </summary>
    internal static string RenderSeedContent(string content, Dictionary<string, string> pathVariables) =>
        ExpandVariables(content, pathVariables.ToDictionary(kv => kv.Key, kv => kv.Value.Replace('\\', '/')));

    /// <summary>
    /// Whether two files hold the same bytes — the single predicate deciding whether a
    /// <see cref="CoreDispatchSeedCopy"/> is written or the existing file is kept (#1151).
    /// </summary>
    /// <remarks>
    /// Public, and deliberately so: an adapter that reports ahead of time how many files a projection
    /// will keep must answer that question with <em>this</em> function rather than one of its own, or the
    /// two answers can disagree — the roster stating something the write path then contradicts. Compares
    /// length first, then content, streaming rather than loading both files whole. A file that cannot be
    /// read answers <see langword="false"/> (treated as differing), so the fail direction is "keep what
    /// is already there".
    /// </remarks>
    public static bool FilesHaveIdenticalBytes(string leftPath, string rightPath)
    {
        try
        {
            var left = new FileInfo(leftPath);
            var right = new FileInfo(rightPath);
            if (!left.Exists || !right.Exists || left.Length != right.Length)
            {
                return false;
            }

            using var leftStream = File.OpenRead(leftPath);
            using var rightStream = File.OpenRead(rightPath);
            var leftBuffer = new byte[4096];
            var rightBuffer = new byte[4096];
            while (true)
            {
                var leftRead = leftStream.ReadAtLeast(leftBuffer, leftBuffer.Length, throwOnEndOfStream: false);
                var rightRead = rightStream.ReadAtLeast(rightBuffer, rightBuffer.Length, throwOnEndOfStream: false);
                if (leftRead != rightRead
                    || !leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
                {
                    return false;
                }

                if (leftRead == 0)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
