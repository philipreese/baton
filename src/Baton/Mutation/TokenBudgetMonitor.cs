using Baton.Domain;
using Baton.Status;

namespace Baton.Mutation;

/// <summary>
/// #1623 ruling addendum (2026-09-01 night, "we have to stop letting agy run away with token
/// consumption"), corrected by #1682 ("the token budget cannot arrest the burn that costs money"):
/// accumulates a live execution's own usage from complete stdout lines — via
/// <see cref="IWorkerUsageParser.TryParseIncrementalUsage"/>, the same per-vendor shape
/// <c>ExecutionUsageProjector</c> reads post-hoc, but read as each line arrives rather than only after
/// exit — and requests cancellation the moment any of three independent triggers fires: the running
/// Σ of billed tokens crosses <paramref name="budget"/>, the running tool-step count crosses
/// <paramref name="maxToolSteps"/>, or the Σ of billed tokens inside the trailing
/// <see cref="BilledRateWindow"/> crosses <paramref name="billedRateLimit"/> (#1691). Why each exists,
/// and what each catches that the others miss, is spec/baton.md §3's own evidence-backed case, not
/// restated here — including §3's measured finding that NO role ships a
/// <paramref name="billedRateLimit"/> default.
/// </summary>
/// <remarks>
/// Wired at <c>MutationInterface.DispatchAndRecordOutcomeAsync</c>; <c>spec/baton.md</c> §3 states the
/// composition rule this follows — in one clause, it wraps a caller's existing
/// <c>CoreDispatchTarget.OnStdoutLine</c> sink and never replaces one.
/// <see cref="OnStdoutLine"/> runs on <c>BatonTask</c>'s single event-delivery thread per
/// its own documented contract, but every member here is still locked — a monitor instance is
/// constructed once per execution and its snapshot methods are read from the awaiting async
/// continuation on a different thread once <see cref="ArrestRequested"/> fires, so this is a genuine
/// cross-thread handoff, not defensive-only locking.
/// </remarks>
public sealed class TokenBudgetMonitor
{
    /// <summary>
    /// How many of the most recent tool names <see cref="SnapshotLastToolNames"/> keeps — enough for a
    /// conductor to see the pattern (e.g. a poll loop) without the room fact growing unbounded over a
    /// long-running arrest.
    /// </summary>
    private const int MaxLastToolNames = 10;

    /// <summary>
    /// #1691: the width of the trailing window <paramref name="billedRateLimit"/> is measured over —
    /// fixed, not configurable, because the catalog field and the <c>--billed-rate-limit</c> override
    /// are both stated in this unit ("billed tokens per 5 minutes"). A second configurable dimension
    /// would make two roles' limits incomparable without buying a catch — spec/baton.md §3 records the
    /// width sweep behind that, and <c>tools/room-rate-sweep/sweep.py --window</c> re-runs it.
    /// </summary>
    public static readonly TimeSpan BilledRateWindow = TimeSpan.FromMinutes(5);

    private readonly long? _budget;
    private readonly int? _maxToolSteps;
    private readonly long? _billedRateLimit;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerUsageParser _usageParser;
    // #1691: (arrival timestamp, billed delta) for every counted usage sample still inside
    // BilledRateWindow, oldest first. Bounded by the window, not by the execution — a 5-minute window
    // over the fastest measured real room (spec/baton.md §3) holds a few hundred entries, so unlike
    // _seenMessageIds below this one shrinks on its own.
    private readonly Queue<(long TimestampTicks, long Billed)> _rateWindow = new();
    private readonly CancellationTokenSource _arrestSource = new();
    private readonly Lock _lock = new();
    private readonly List<string> _lastToolNames = [];
    // #1686 review F13: grows once per distinct claude message.id for the life of an execution and is
    // never trimmed -- bounded in practice by the role timeout (153 ids measured over a 65-minute
    // room, spec/baton.md §3; tens of KB at the extreme), unlike _lastToolNames above, which is
    // capped because a conductor reads it live. Not a leak; a deliberate exception to that cap.
    private readonly HashSet<string> _seenMessageIds = new(StringComparer.Ordinal);
    // #1686 review F5: nullable, never a fabricated 0 -- set only once a usage line actually parses,
    // same convention as _billedTokens/_cacheReadSum right below.
    private long? _inputLevel;
    // #1666: the parent conversation's and the sub-agent bucket's own levels, tracked SEPARATELY so a
    // sub-agent's smaller context (WorkerUsage.IsSubAgentTurn) can never replace a larger parent
    // reading -- _inputLevel above is reported as the max of these two, never either one alone.
    // _subAgentInputLevel is cleared the moment a parent line arrives (review F3) so the bucket stays
    // transient rather than a permanent high-water mark -- spec/baton.md §3 has the fuller statement
    // of what this buys and why.
    private long? _parentInputLevel;
    private long? _subAgentInputLevel;
    private long? _latestTokensIn;
    private long? _latestCacheRead;
    private long? _latestCacheCreation;
    private long? _tokensOut;
    private long? _billedTokens;
    private long? _cacheReadSum;
    // #1557: additive the same way _billedTokens is -- one per usage-bearing line that contributed a
    // billed delta, so a caller can tell "how many turns" from "how many tokens" independently. Never a
    // fabricated 0, same convention as every other Σ on this type. WorkerUsage.Turns is now populated
    // (previously always null); the only live consumer whose output changes is the arrest event's Usage
    // (MutationInterface's ExecutionArrested), and its current readers (StateProjector's arrest
    // descriptions) do not consume Turns today -- benign until something reads it.
    private int? _turns;
    // #1706: sticky. Set the first time any reading declares itself a floor (claude, always) and never
    // cleared -- a Σ over a stream where one component was structurally unreadable stays a floor no
    // matter how many complete readings follow it.
    private bool _billedIsFloor;
    private long _rateWindowSum;
    // #1709 review: nullable, following _billedTokens' own convention right above -- null until
    // AdmitRateSample runs at least once, so "the monitor watched and saw nothing" is distinguishable
    // from "the monitor watched and genuinely measured a zero peak."
    private long? _peakBilledInWindow;
    private int _toolStepCount;
    // #2002 rule 3: how many shell commands this stream announced, and how many of each SHAPE
    // (Status.CommandShape). Bounded by the number of DISTINCT shapes rather than by the step count --
    // the room this was measured on held 8 shapes across 207 run_command steps -- so the pathological
    // stream this exists to describe is the cheap one to hold.
    private readonly Dictionary<string, int> _commandShapeCounts = new(StringComparer.Ordinal);
    private int _shellCommandCount;
    private bool _arrested;
    private ArrestReason? _arrestReason;

    /// <param name="budget">
    /// #1682: the per-execution ceiling <see cref="WorkerUsage.BilledTokens"/> arrests on. Null enforces
    /// no token-side trigger (a role/dispatch with a tool-step cap but no budget still watches, unlike
    /// before this issue where a monitor required a budget to exist at all).
    /// </param>
    /// <param name="maxToolSteps">
    /// #1682: the per-execution ceiling on Σ<see cref="IWorkerUsageParser.CountToolSteps"/>. Fires the
    /// instant the running count exceeds it, regardless of whether usage ever parses on this stream at
    /// all. Null enforces no tool-step trigger.
    /// </param>
    /// <param name="billedRateLimit">
    /// #1691: the ceiling on Σ billed tokens inside the trailing <see cref="BilledRateWindow"/>. Null
    /// enforces no rate trigger, which is what every role ships; a value reaches here only from an
    /// operator's own <c>--billed-rate-limit</c>. Note the semantics before the window has elapsed: the
    /// trailing window covers the whole run, so this behaves as a second, tighter
    /// <paramref name="budget"/> over an execution's opening stretch, unsuppressed by any warm-up.
    /// spec/baton.md §3 has the reasoning for both.
    /// </param>
    /// <param name="timeProvider">
    /// #1691: the clock <paramref name="billedRateLimit"/>'s window is measured against — the ARRIVAL
    /// time of each usage line rather than anything on the line itself, for the reason spec/baton.md §3
    /// gives. Defaults to <see cref="TimeProvider.System"/>; a replay test supplies a fake so a captured
    /// stream can be re-run deterministically.
    /// </param>
    public TokenBudgetMonitor(
        long? budget,
        int? maxToolSteps,
        long? billedRateLimit,
        IWorkerUsageParser usageParser,
        TimeProvider? timeProvider = null)
    {
        _budget = budget;
        _maxToolSteps = maxToolSteps;
        _billedRateLimit = billedRateLimit;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _usageParser = usageParser ?? throw new ArgumentNullException(nameof(usageParser));
    }

    /// <summary>Cancelled exactly once, the instant either trigger first fires.</summary>
    public CancellationToken ArrestRequested => _arrestSource.Token;

    /// <summary>Whether this monitor itself requested the arrest — see this type's own remarks for why
    /// the caller must check this rather than inferring arrest from cancellation alone (an operator's
    /// own <c>dispatchCancellationToken</c> firing first must never be misread as a budget arrest).</summary>
    public bool Arrested
    {
        get { lock (_lock) { return _arrested; } }
    }

    /// <summary>Which trigger fired — null until <see cref="Arrested"/> is true.</summary>
    public ArrestReason? ArrestReasonValue
    {
        get { lock (_lock) { return _arrestReason; } }
    }

    /// <summary>
    /// Feeds one complete stdout line. Cheap for the overwhelming majority of lines (neither parse
    /// matches), and safe to call after <see cref="Arrested"/> is already true — a line or two can
    /// still arrive while the process is being torn down.
    /// </summary>
    public void OnStdoutLine(string line)
    {
        if (_usageParser.TryParseToolName(line) is { Length: > 0 } toolName)
        {
            lock (_lock)
            {
                _lastToolNames.Add(toolName);
                if (_lastToolNames.Count > MaxLastToolNames)
                {
                    _lastToolNames.RemoveAt(0);
                }
            }
        }

        // #1682: the tool-step count is read off EVERY line, independent of whether usage parses on
        // it — the cap's whole reason for existing is to arrest a stream with malformed or absent
        // usage lines, the same pattern the incremental usage parse cannot see at all.
        var toolStepDelta = _usageParser.CountToolSteps(line);
        var usageParsed = _usageParser.TryParseIncrementalUsage(line, out var usage) && usage is not null;
        // #2002: read off every line for the same reason the tool-step count above is, and outside the
        // lock because normalising is pure string work.
        var shellCommands = _usageParser.ShellCommandLines(line);

        ArrestReason? newlyArmed = null;
        lock (_lock)
        {
            if (toolStepDelta > 0)
            {
                _toolStepCount += toolStepDelta;
            }

            foreach (var commandLine in shellCommands)
            {
                _shellCommandCount++;
                var shape = Status.CommandShape.Normalize(commandLine);
                _commandShapeCounts[shape] = _commandShapeCounts.TryGetValue(shape, out var seen) ? seen + 1 : 1;
            }

            if (usageParsed)
            {
                if (usage!.TokensIn.HasValue || usage.CacheReadTokens.HasValue || usage.CacheCreationTokens.HasValue)
                {
                    _latestTokensIn = usage.TokensIn;
                    _latestCacheRead = usage.CacheReadTokens;
                    _latestCacheCreation = usage.CacheCreationTokens;
                    var level = (usage.TokensIn ?? 0) + (usage.CacheReadTokens ?? 0) + (usage.CacheCreationTokens ?? 0);
                    // #1666: replace only the bucket this line belongs to -- a sub-agent turn's own
                    // (typically much smaller) context never overwrites the parent's tracked level, and
                    // a parent turn never overwrites a sub-agent's. The reported level is the max of the
                    // two, so it can only rise or hold on a fan-out turn, never dip below what the
                    // parent already showed. Review F3: a parent line also CLEARS the sub-agent bucket,
                    // so a stale sub-agent high-water mark can never keep pinning the reported level
                    // above a genuine post-compaction parent drop once the parent speaks again.
                    if (usage.IsSubAgentTurn)
                    {
                        _subAgentInputLevel = level;
                    }
                    else
                    {
                        _parentInputLevel = level;
                        _subAgentInputLevel = null;
                    }

                    _inputLevel = Math.Max(_parentInputLevel ?? 0, _subAgentInputLevel ?? 0);
                }

                // #1686 review F6: claude can split one API response's usage across several
                // consecutive "type":"assistant" lines sharing the same message.id, each carrying an
                // identical message-level usage object -- summing every line double- (or N-times-)
                // counts that response. A line with no MessageId (agy; claude's terminal line is never
                // read here) always accumulates; a repeated MessageId accumulates only its first sighting.
                var alreadyCounted = usage.MessageId is { Length: > 0 } messageId && !_seenMessageIds.Add(messageId);
                if (!alreadyCounted)
                {
                    // #1706: each Σ stays null until a reading actually carries ITS OWN component --
                    // not merely until some usage line parses. claude's readings carry no output figure
                    // at all now (ClaudeUsageParser.TryParseIncrementalUsage's own doc has why), and
                    // reporting a Σ of 0 output tokens over a room that emitted tens of thousands is
                    // exactly the fabricated zero #1686 review F5/F7 removed from the two Σs below.
                    if (usage.TokensOut.HasValue)
                    {
                        _tokensOut = (_tokensOut ?? 0) + usage.TokensOut.Value;
                    }

                    // #1686 review F7: nullable, following BilledTokens' own convention right below.
                    if (usage.CacheReadTokens.HasValue)
                    {
                        _cacheReadSum = (_cacheReadSum ?? 0) + usage.CacheReadTokens.Value;
                    }

                    // #1682: per-line input + output + cache_creation, summed -- WorkerUsage.BilledTokens
                    // has the full arithmetic case for the shape and the thinking-tokens exclusion. Stays
                    // null (never a fabricated 0) until a reading reports at least one of the three.
                    if (usage.TokensIn.HasValue || usage.TokensOut.HasValue || usage.CacheCreationTokens.HasValue)
                    {
                        var billedDelta = (usage.TokensIn ?? 0) + (usage.TokensOut ?? 0) + (usage.CacheCreationTokens ?? 0);
                        _billedTokens = (_billedTokens ?? 0) + billedDelta;
                        _turns = (_turns ?? 0) + 1;
                        // #1691: the SAME deduped per-turn billed delta the running Σ above takes, admitted a
                        // second time into the trailing window -- deliberately reusing #1682's accounting
                        // rather than re-deriving a rate-specific one, so the two triggers can never disagree
                        // about what a billed token is. #1706 merge: inside the same guard as the Σ for that
                        // very reason -- a cache_read-only line contributes nothing to either, and admitting
                        // a 0 sample for it would put a window entry behind a reading that billed nothing.
                        AdmitRateSample(billedDelta);
                    }
                }

                // #1706: outside the dedupe guard on purpose -- a repeat contributes no tokens but is
                // still evidence about what this stream's readings can and cannot measure.
                _billedIsFloor |= usage.BilledIsFloor;
            }

            if (!_arrested && _budget is { } budget && _billedTokens is { } billedSoFar && billedSoFar >= budget)
            {
                newlyArmed = ArrestReason.TokenBudget;
            }
            else if (!_arrested && _maxToolSteps is { } cap && _toolStepCount > cap)
            {
                newlyArmed = ArrestReason.ToolStepCap;
            }
            else if (!_arrested && _billedRateLimit is { } rateLimit && _rateWindowSum >= rateLimit)
            {
                newlyArmed = ArrestReason.BilledRate;
            }

            if (newlyArmed is { } reason)
            {
                _arrested = true;
                _arrestReason = reason;
            }
        }

        if (newlyArmed is not null)
        {
            _arrestSource.Cancel();
        }
    }

    /// <summary>
    /// #1691: appends one counted billed delta to the trailing <see cref="BilledRateWindow"/>, evicts
    /// everything that has fallen out of it, and keeps the running Σ and its peak. Called under
    /// <see cref="_lock"/>.
    /// </summary>
    private void AdmitRateSample(long billedDelta)
    {
        var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
        _rateWindow.Enqueue((nowTicks, billedDelta));
        _rateWindowSum += billedDelta;

        var cutoff = nowTicks - BilledRateWindow.Ticks;
        while (_rateWindow.Count > 0 && _rateWindow.Peek().TimestampTicks < cutoff)
        {
            _rateWindowSum -= _rateWindow.Dequeue().Billed;
        }

        if (_peakBilledInWindow is not { } peakSoFar || _rateWindowSum > peakSoFar)
        {
            _peakBilledInWindow = _rateWindowSum;
        }
    }

    /// <summary>
    /// #1691: the largest Σ billed tokens this execution ever held inside one trailing
    /// <see cref="BilledRateWindow"/> — the quantity <c>--billed-rate-limit</c> is compared against,
    /// exposed so it is READABLE whether or not a limit was ever set — spec/baton.md §3 states what
    /// that measurement is for. #1709 review: null until <see cref="AdmitRateSample"/> has run at
    /// least once (never a fabricated zero, spec/baton.md §3's never-fabricate-a-zero convention);
    /// once a sample has been admitted, a genuine measured 0 stays 0.
    /// </summary>
    public long? SnapshotPeakBilledInWindow()
    {
        lock (_lock) { return _peakBilledInWindow; }
    }

    /// <summary>
    /// The measured usage at the moment of the snapshot — <see cref="FlowEvent.ExecutionArrested.Usage"/>.
    /// #1623 re-review N6: <see cref="WorkerUsage.TokensIn"/> stays the vendor-raw latest reading
    /// (never fabricated, per <see cref="WorkerUsage"/>'s own doc); the accumulated level this monitor
    /// displays (never arrests on, since #1682) goes on <see cref="WorkerUsage.ContextLevelTokens"/>
    /// instead, so a reader summing the three raw fields does not silently double-count it.
    /// <see cref="WorkerUsage.CacheReadTokens"/> here is the running Σ (display-only, #1682), not the
    /// latest reading; #1812 added <see cref="WorkerUsage.CacheReadLevelTokens"/> alongside it for a
    /// consumer that needs the latest line's reading instead (the daemon's fleet projection, which
    /// pusher.py's derive path treats as a level, not a Σ — same duality
    /// <see cref="WorkerUsage.ContextLevelTokens"/> already has for the input side).
    /// <see cref="WorkerUsage.BilledTokens"/> is the quantity actually compared to the
    /// budget, and <see cref="WorkerUsage.BilledIsFloor"/> (#1706) says whether that quantity is a
    /// measurement of this execution's billed tokens or only a lower bound on them — true for every
    /// claude stream, false for every agy one, per the two parsers' own measured shapes.
    /// </summary>
    public WorkerUsage SnapshotUsage()
    {
        lock (_lock)
        {
            return new WorkerUsage(
                TokensIn: _latestTokensIn,
                TokensOut: _tokensOut,
                Turns: _turns,
                CacheReadTokens: _cacheReadSum,
                CacheCreationTokens: _latestCacheCreation,
                ContextLevelTokens: _inputLevel,
                CacheReadLevelTokens: _latestCacheRead,
                BilledTokens: _billedTokens,
                BilledIsFloor: _billedIsFloor);
        }
    }

    /// <summary>The last few observed tool names — <see cref="FlowEvent.ExecutionArrested.LastToolNames"/>.</summary>
    public IReadOnlyList<string> SnapshotLastToolNames()
    {
        lock (_lock)
        {
            return [.. _lastToolNames];
        }
    }

    /// <summary>The tool-step count at snapshot time — <see cref="FlowEvent.ExecutionArrested.ToolStepCount"/>.</summary>
    public int SnapshotToolStepCount()
    {
        lock (_lock) { return _toolStepCount; }
    }

    /// <summary>
    /// #2002 rule 3: the one normalised command shape holding MORE THAN HALF of this stream's shell
    /// commands, with its share as a whole percent — or <see langword="null"/> when no shape does, when
    /// the stream announced no shell command at all, or when this vendor's stream does not carry
    /// command lines (codex; see <see cref="IWorkerUsageParser.ShellCommandLines"/>).
    /// <para>
    /// <b>Strictly more than half</b>, so the claim "the steps were spent on this" is true of a
    /// majority rather than merely of a plurality. The share is over SHELL commands, not over all tool
    /// steps: it answers "what were the commands", and mixing reads into the denominator would
    /// understate a room that polled with every command it issued. The measured #2002 arm-A agy room
    /// reads 53 % here.
    /// </para>
    /// </summary>
    public (string Shape, int Percent)? SnapshotDominantCommandShape()
    {
        lock (_lock)
        {
            if (_shellCommandCount == 0)
            {
                return null;
            }

            var dominant = _commandShapeCounts.OrderByDescending(pair => pair.Value).First();
            return dominant.Value * 2 > _shellCommandCount
                ? (dominant.Key, (int)Math.Round(dominant.Value * 100.0 / _shellCommandCount))
                : null;
        }
    }
}
