using System.Text.RegularExpressions;
using Baton.Core;
using Baton.Domain;

namespace Baton.Mutation;

/// <summary>
/// The result of one <see cref="VerifyRunner.RunProcessAsync"/> call (#1623). <see cref="Passed"/>
/// mirrors the verify command's own exit code EXCEPT when the caller's token is cancelled, which
/// always wins regardless of what the child's exit code happened to be (#1722) — a cancellation
/// observed after a fast child's natural exit is otherwise a fail-open race, not a flaky test.
/// <see cref="FailingMembers"/>/<see cref="Tail"/> are populated only when it fails, and only when the
/// summary line's shape is recognized — never fabricated. <see cref="Tail"/> is each failing member's
/// OWN output (#1701), noise-filtered and failure-line-promoted (#1971), not a blind tail of the whole
/// combined stream — see <see cref="VerifyRunner.BuildTail"/>.
/// <see cref="Kind"/> distinguishes gate breakage from timeouts, cancellations, or engine restarts (F3).
/// <see cref="NotRunReason"/> is populated only for <see cref="VerifyFailedKind.BuildLockBusy"/> (#1796) —
/// the one-line "build lock busy for Ns (holder: ...)" text the caller journals verbatim as
/// <see cref="FlowEvent.VerifyNotRun.Reason"/>, parsed from <see cref="Tail"/> and never fabricated: a
/// shape miss leaves it null rather than guessing at a holder.
/// </summary>
public sealed record VerifyOutcome(
    bool Passed,
    IReadOnlyList<string>? FailingMembers = null,
    string? Tail = null,
    VerifyFailedKind? Kind = null,
    string? NotRunReason = null)
{
    public static readonly VerifyOutcome Pass = new(true);
}

/// <summary>
/// The engine-run verify step's own primitive (#1623; contract: <c>spec/baton.md</c> §3):
/// spawns the resolved verify command (<see cref="Mutation.VerifyCommandResolver"/> since #1702 —
/// <c>pixi run &lt;task&gt;</c> for a role default, or the platform shell for a repo-declared/overridden
/// command line) once, under <paramref name="workingDirectory"/>, and reports pass/fail plus a bounded
/// tail. Never invoked from inside a worker's own turn — the entire point of this issue is that the
/// ENGINE runs this, not the model.
/// </summary>
/// <remarks>
/// <c>tools/gates/gates.py</c> is the one place a gate-run's overall
/// verdict text is assembled — its <c>summarise()</c> emits a deterministic
/// <c>"GATES: FAIL {n} of {m} -- name1, name2"</c> line on failure, which this class parses rather than
/// re-implementing gate aggregation. If that line's shape ever changes, <see cref="FailingMembers"/>
/// degrades to empty (never a fabricated guess) while <see cref="Passed"/> still reflects the real exit
/// code, so a shape drift downgrades diagnostic detail rather than the pass/fail verdict itself.
/// Registered in <c>tests/Baton.Architecture.Tests/VendorSpawnGateTests.cs</c>'s approved-spawn-sites
/// list, per that test's own requirement that every <see cref="BatonTask"/> construction site in
/// <c>src/</c> be named there.
/// </remarks>
public static class VerifyRunner
{
    private const string FailMarker = "GATES: FAIL";
    // #1796: gates.py's own BLOCKED_MARK (see FlowEvent.VerifyNotRun.BuildLockBusy for what this
    // reports). Checked AFTER FailMarker in ParseVerdict, mirroring gates.py's own summarise()
    // precedence: a real failure alongside this always wins the headline.
    private const string BlockedMarker = "GATES: BLOCKED";
    private const string FailingMembersSeparator = " -- ";

    /// <summary>
    /// The TOTAL bound on <see cref="VerifyOutcome.Tail"/> — the same "bounded tail, never a full log
    /// dump" shape <c>OutcomeClassifier.MaxStderrTailInReason</c> already applies to a worker's own
    /// stderr, scaled up because a gate run's own output is naturally longer than one process's
    /// stderr. #1701 changed WHICH bytes count toward it (each failing member's own block, keyed off
    /// its marker line, rather than a blind cut of the whole combined stream that could drop a failing
    /// member's diagnostic text once other members' one-line pass markers followed it) but not the
    /// total: <see cref="BuildTail"/> splits this budget evenly across however many members failed and
    /// clamps the joined result, so N failing members never yield N times this many characters.
    /// </summary>
    private const int MaxTailChars = 4000;

    /// <summary>
    /// #1971: the slice of <see cref="MaxTailChars"/> <see cref="BuildTail"/> may spend re-surfacing
    /// failure lines that the raw tail cut. Held well under half the total so the promoted excerpt can
    /// never crowd out the thing it exists to give context to — the command's own last lines.
    /// </summary>
    private const int MaxPromotedChars = 1200;

    /// <summary>
    /// #1971: the file <see cref="RunProcessAsync"/> writes the verify command's unfiltered combined
    /// stream to, in the execution's own artifacts directory, whenever the command fails and a
    /// directory was supplied. Dot-prefixed and always this one name, for the same reason
    /// <c>OutputMaterializer.CapturedResponseFileName</c> is: engine-owned, never a worker's declared
    /// output — and registered in <see cref="Baton.Dispatch.ExecutionStreamLogger.IsStreamLogFileName"/>
    /// beside the other engine-owned files that land in that directory (#1971 review), so the first
    /// filtered listing surface to exist does not present it as something the worker produced.
    /// This is what makes the filtering below safe to do at all — nothing the filter drops is lost, it
    /// is one file open away, up to this file's own <see cref="MaxRawOutputChars"/> cap.
    /// </summary>
    public const string RawOutputFileName = ".verify-output.log";

    /// <summary>
    /// #1971 review: the bound on what <see cref="RawOutputFileName"/> retains, so one runaway gate
    /// member cannot persist a multi-hundred-megabyte log into every failed execution's artifacts (and
    /// make the settle wait on writing it). Deliberately <see
    /// cref="Baton.Dispatch.ExecutionStreamLogger.DefaultMaxSizeBytes"/> rather than a second constant
    /// of its own: this file's siblings in the same directory roll over at exactly that size, and one
    /// engine-written file in a directory being bounded differently from the rest is the drift worth
    /// avoiding. Counted in CHARS against a byte-named constant, which is the honest description of
    /// what the code enforces — a gate run's output is overwhelmingly ASCII, where the two coincide,
    /// and this is a bound on what is retained rather than a byte-exact file-size guarantee.
    /// Over-cap text keeps its head and its tail with an explicit elision marker between them, never a
    /// silent truncation: <see cref="MaxRawOutputElisionReserve"/> is the room left for that marker.
    /// </summary>
    private const int MaxRawOutputChars = (int)Baton.Dispatch.ExecutionStreamLogger.DefaultMaxSizeBytes;

    private const int MaxRawOutputElisionReserve = 200;

    /// <summary>
    /// #1971: lines a gate member's own output carries that say nothing about why it failed. Exactly
    /// two shapes, and both are <see cref="Console.Error"/> writes from this repo's own <c>src/</c> —
    /// <c>Outcomes.OutcomeClassifier</c>'s <c>CAPTURED (#1594)</c> marker and
    /// <c>Dispatch.ExecutionStreamLogger</c>'s initialization warning. This file is the canonical
    /// account of the shapes themselves; <c>spec/baton.md</c> §3's engine-run verify section records
    /// what was measured and the rule as a whole, and points here rather than transcribing them
    /// (#1971 review — one home per fact, so adding a shape here cannot leave the register stale).
    /// <para>
    /// Anchored on each message's own opening, never on a substring that could appear in real
    /// diagnostic text — a genuine failure line QUOTING one of these messages (an assertion whose
    /// expected value is the <c>CAPTURED (#1594)</c> text) is kept, which is the mis-cut direction
    /// <c>VerifyRunnerTests</c> pins. Dropping a line is only ever a display choice: the unfiltered
    /// stream is written to <see cref="RawOutputFileName"/>.
    /// </para>
    /// </summary>
    private static readonly Regex FixtureNoiseLine = new(
        @"^\s*(CAPTURED \(#1594\):|Warning: Failed to initialize execution stream logger )",
        RegexOptions.Compiled);

    /// <summary>
    /// #1971: the test-runner failure shapes a reader of a failed verify is actually looking for —
    /// <c>dotnet test</c>'s per-test <c>Failed &lt;name&gt;</c> and its <c>Failed! - Failed: n, ...</c>
    /// summary, an xunit <c>[FAIL]</c> block, an <c>Assert.</c> line, and a compiler/MSBuild
    /// <c>error XXnnnn</c>. <see cref="BuildTail"/> promotes matching lines that the raw tail cut, so a
    /// failure summary sitting above thousands of trailing characters is still readable from
    /// <c>baton status --json</c>.
    /// <para>
    /// Every token is anchored to a real line shape rather than used as a bare substring: a plain
    /// <c>Contains("Failed")</c> would re-promote the very stream-logger warning
    /// <see cref="FixtureNoiseLine"/> just dropped ("Warning: Failed to initialize..."), and a bare
    /// <c>"error"</c> matches most of any test log. Belt and braces on the same point: this is only
    /// ever scanned over ALREADY-FILTERED text.
    /// </para>
    /// </summary>
    private static readonly Regex FailureSummaryLine = new(
        @"^\s*\[FAIL\]|^\s*Failed[\s!]|\bAssert\.|\berror [A-Z]{2,}\d+|: error ",
        RegexOptions.Compiled);

    /// <summary>
    /// The per-member summary line <c>tools/gates/gates.py</c>'s <c>run_gates</c>/<c>join_gates</c>
    /// print after EVERY member: <c>"  pass  name  (exit 0)"</c> / <c>"  FAIL  name (exit 1)"</c> /
    /// <c>"  BLOCKED  name  (exit 75)"</c> (#1796 — a buildlock-timeout member). The status word's
    /// length is no longer fixed at 4 characters (<c>BLOCKED</c> is 7), but the shape stays fixed
    /// two-space-delimited fields regardless — this is what lets #1701 key a failing member's own
    /// block out of the combined stream instead of guessing from position.
    /// Known narrow gap (#1701 review): <c>gates-selftest</c> is itself an OVERLAP member whose own
    /// selftest fabricates lines in exactly this shape (<c>tools/gates/gates.py</c>'s own
    /// <c>run_gates</c>/<c>join_gates</c> control-arm fixtures) to prove <c>join_gates</c> discriminates
    /// a failing gate. If <c>gates-selftest</c> itself fails under <c>gates-quiet</c>, those fabricated
    /// lines segment inside its own block, so this member's tail can start after its last fabricated
    /// marker rather than at the top of its real output. Affects only that one member's own diagnostic
    /// completeness, never <see cref="ParseVerdict"/>'s verdict; no clean fix without changing
    /// gates.py's fixture shape, so left as a known gap rather than a redesign.
    /// </summary>
    private static readonly Regex MemberMarkerLine = new(
        @"^  (?<status>pass|FAIL|BLOCKED)  (?<name>\S+)  \(exit (?<code>-?\d+)\) *\r?$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// #1796: <c>tools/buildlock.py</c>'s own machine-recognizable timeout line, e.g. <c>"buildlock:
    /// BLOCKED after 1800s waiting for the build lock held by PID 1234 (dotnet build) since
    /// 2026-09-04 12:00:00 -- raise BATON_BUILDLOCK_TIMEOUT_S or find out why the holder is
    /// stuck"</c>. Parsed out of a BLOCKED member's own tail so <see cref="VerifyOutcome.NotRunReason"/>
    /// can name the seconds waited and the holder without re-deriving either — a shape miss (the
    /// message text drifting) degrades to null rather than fabricating a holder. The holder capture runs
    /// to buildlock.py's fixed trailing sentinel, not to the first <c>" --"</c> it meets: the holder text
    /// embeds the wrapped command line verbatim, and nearly every buildlock-wrapped pixi task carries a
    /// <c>--</c> flag (<c>--no-incremental</c>, <c>--verify-no-changes</c>, <c>-- --check</c>), so a bare
    /// <c>" --"</c> terminator truncated the holder mid-command (#1813 review).
    /// </summary>
    private static readonly Regex BuildLockBlockedLine = new(
        @"buildlock: BLOCKED after (?<secs>\d+)s waiting for the build lock held by (?<holder>.+?) -- raise BATON_BUILDLOCK_TIMEOUT_S",
        RegexOptions.Compiled);

    /// <summary>
    /// The program/args-injecting form #1702 made the ONLY production entry point (MutationInterface
    /// spawns whatever <see cref="Mutation.VerifyCommandResolver.Resolve"/> resolved — <c>pixi</c> for a
    /// role default, <c>cmd.exe</c> for a repo-declared/overridden line — never a hardcoded <c>pixi</c>
    /// call). Internal so a test can also point this at a fake command (a shell one-liner that exits
    /// non-zero, or prints a synthetic <c>GATES: FAIL ...</c> line) rather than a real, minutes-long
    /// <c>pixi run gates-quiet</c> — the same seam <c>WorkerBindingConfigWriter</c>'s own
    /// budget-injecting internal overload exists for.
    /// </summary>
    /// <param name="rawOutputDirectory">
    /// #1971: where the unfiltered combined stream is written as
    /// <see cref="RawOutputFileName"/> when the command FAILS — the execution's own artifacts
    /// directory in production, a temp directory in a test. Null writes nothing, which is what every
    /// caller that has no artifacts directory (the resolver's probes, the older tests) passes.
    /// </param>
    internal static async Task<VerifyOutcome> RunProcessAsync(
        string program, IReadOnlyList<string> args, string? workingDirectory, CancellationToken cancellationToken,
        string? rawOutputDirectory = null)
    {
        // #1722: an already-cancelled token never launches at all -- a fast child could otherwise
        // exit before this method observes the cancellation (see the post-capture check below), and
        // there is no reason to spawn a process whose result is already decided.
        if (cancellationToken.IsCancellationRequested)
        {
            return new VerifyOutcome(false, FailingMembers: null, Tail: "Verify command cancelled before it was launched.", Kind: VerifyFailedKind.Cancelled);
        }

        int exitCode;
        string text;
        try
        {
            (exitCode, text) = await CaptureAsync(program, args, workingDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (BatonCancelException ex)
        {
            return new VerifyOutcome(false, FailingMembers: null, Tail: $"Verify command cancelled: {ex.Message}", Kind: VerifyFailedKind.Cancelled);
        }
        catch (BatonTimeoutException ex)
        {
            return new VerifyOutcome(false, FailingMembers: null, Tail: $"Verify command timed out: {ex.Message}", Kind: VerifyFailedKind.TimedOut);
        }
        catch (OperationCanceledException)
        {
            return new VerifyOutcome(false, FailingMembers: null, Tail: "Verify command was cancelled.", Kind: VerifyFailedKind.Cancelled);
        }
        catch (BatonException ex) when (ex.ErrorCode == BatonErrorCode.Cancelled || cancellationToken.IsCancellationRequested)
        {
            return new VerifyOutcome(false, FailingMembers: null, Tail: $"Verify command cancelled: {ex.Message}", Kind: VerifyFailedKind.Cancelled);
        }
        catch (BatonException ex) when (ex.ErrorCode == BatonErrorCode.TimedOut)
        {
            return new VerifyOutcome(false, FailingMembers: null, Tail: $"Verify command timed out: {ex.Message}", Kind: VerifyFailedKind.TimedOut);
        }
        catch (BatonException ex)
        {
            // The OS refusing to spawn `pixi` at all -- not the worker's fault, but the honest outcome
            // is still "verify did not confirm this execution", never a silent pass. Settles Indeterminate.
            return new VerifyOutcome(false, FailingMembers: null, Tail: $"Verify command failed to complete: {ex.Message}", Kind: VerifyFailedKind.GatesFailed);
        }

        // #1722: cancellation takes precedence over whatever exit code the child happened to produce.
        // BatonTask can observe a cancellation registered against an already-finished job as a no-op
        // (docs on BatonTask.RunAsync's cancellationToken param) -- a fast child that exits 0 in the
        // gap between the cancel firing and the kill landing must still report Cancelled, not Passed.
        if (cancellationToken.IsCancellationRequested)
        {
            return new VerifyOutcome(false, FailingMembers: null, Tail: "Verify command cancelled.", Kind: VerifyFailedKind.Cancelled);
        }

        if (exitCode == 0)
        {
            return VerifyOutcome.Pass;
        }

        var (kind, failingMembers) = ParseVerdict(text);
        var rawOutputFileName = await TryWriteRawOutputAsync(rawOutputDirectory, text).ConfigureAwait(false);
        var tail = BuildTail(text, failingMembers, rawOutputFileName);
        // #1971: read from the RAW stream, not from the tail. The tail is now filtered, budget-split
        // and clamped, so a buildlock line that survived the pre-#1971 verbatim tail could silently
        // fall out of it -- and a NotRunReason that degrades to null because a DISPLAY rule evicted its
        // source line is a settlement changed by a formatting change.
        var notRunReason = kind == VerifyFailedKind.BuildLockBusy ? ExtractBuildLockReason(text, failingMembers) : null;
        return new VerifyOutcome(false, failingMembers, tail, Kind: kind, NotRunReason: notRunReason);
    }

    /// <summary>
    /// #1702: the bare spawn-and-capture primitive <see cref="RunProcessAsync"/> wraps with verify's
    /// pass/fail semantics — factored out so <see cref="VerifyCommandResolver"/>'s pre-flight
    /// runnability probe (<c>pixi task list</c>) can reuse the identical <see cref="BatonTask"/>
    /// plumbing without inheriting a verify-specific interpretation of the exit code, which a probe has
    /// no use for. Exceptions propagate to the caller rather than degrading to a <see cref="VerifyOutcome"/>
    /// here — the probe's own caller decides what an unspawnable <c>pixi</c> means (not runnable, never
    /// a silent pass), which is a different mapping than <see cref="RunProcessAsync"/>'s.
    /// </summary>
    /// <param name="stdoutOnly">
    /// #1708 L3: drop stderr instead of interleaving it into the returned output. Off by default — the
    /// verify run and the <c>pixi task list</c> probe both WANT the combined stream, because for them the
    /// output is diagnostic text. On for the declaration read, whose output is PARSED; spec/baton.md §3
    /// states what an interleaved warning would cost there.
    /// </param>
    /// <param name="environmentAllowList">
    /// #1708 L3: when non-null, the child inherits ONLY these ambient variables (plus
    /// <paramref name="environmentOverrides"/>) instead of the whole environment. Null keeps the
    /// inherit-everything default described below.
    /// </param>
    /// <param name="environmentOverrides">Variables set explicitly on the child, whatever the allowlist says.</param>
    internal static async Task<(int ExitCode, string Output)> CaptureAsync(
        string program,
        IReadOnlyList<string> args,
        string? workingDirectory,
        CancellationToken cancellationToken,
        bool stdoutOnly = false,
        IReadOnlyList<string>? environmentAllowList = null,
        IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        var output = new System.Text.StringBuilder();
        // No WithClearEnv() by default: unlike a vendor worker dispatch, the default caller spawns the
        // engine's own trusted tool (`pixi`, which itself needs its host toolchain's PATH/CONDA_PREFIX/etc.
        // to resolve) rather than an adapter-sandboxed process, so it inherits the ambient environment the
        // same way a human running `pixi run gates-quiet` by hand would. A caller whose child's OUTPUT is
        // a trust boundary rather than a hint passes an allowlist instead (#1708 L3 --
        // VerifyCommandResolver's git spawns).
        // Process-level timeout is omitted (F3): buildlock's own loud timeout bounds each lock-competing
        // step instead of an arbitrary overall wall-clock ceiling causing spurious Indeterminate settlements.
        using var task = new BatonTask(program, [.. args])
            .WithCaptureOutput(true);

        if (environmentAllowList is not null)
        {
            task.WithClearEnv(true);
            foreach (var name in environmentAllowList)
            {
                if (Environment.GetEnvironmentVariable(name) is { } value)
                {
                    task.WithEnv(name, value);
                }
            }
        }

        if (environmentOverrides is not null)
        {
            foreach (var (name, value) in environmentOverrides)
            {
                task.WithEnv(name, value);
            }
        }

        if (workingDirectory is not null)
        {
            task.WithCwd(workingDirectory);
        }

        var exitCode = -1;
        task.EventRaised += (_, e) =>
        {
            switch (e.Kind)
            {
                case BatonTaskEventKind.StderrChunk when stdoutOnly:
                    break;
                case BatonTaskEventKind.StdoutChunk or BatonTaskEventKind.StderrChunk when e.Data is { } data:
                    output.Append(System.Text.Encoding.UTF8.GetString(data));
                    break;
                case BatonTaskEventKind.Exited:
                    exitCode = e.ExitCode;
                    break;
            }
        };

        await task.RunAsync(cancellationToken).ConfigureAwait(false);
        return (exitCode, output.ToString());
    }

    /// <summary>
    /// The failing (or, for #1796, blocked) member(s)' own output, not a blind tail of the whole
    /// combined stream (#1701), with the fixture noise dropped and any cut failure lines promoted back
    /// (#1971). Three steps, in this order — the order is what makes it work:
    /// <list type="number">
    /// <item>Segment <paramref name="output"/> on <see cref="MemberMarkerLine"/> — each segment is one
    /// member's own captured output followed by its summary line — and keep the segment(s) for members
    /// named in <paramref name="failingMembers"/> whose own marker is not <c>pass</c> (i.e. <c>FAIL</c>
    /// or <c>BLOCKED</c>). Falls back to the whole stream (the pre-#1701 behavior) when no marker line
    /// is recognized at all, matching <see cref="ParseVerdict"/>'s own shape-drift fallback: degrade
    /// detail, never fabricate structure that is not there.</item>
    /// <item>Drop every <see cref="FixtureNoiseLine"/> from each kept block. A block that is NOTHING
    /// but noise keeps its unfiltered text — an empty tail tells a reader strictly less than a noisy
    /// one.</item>
    /// <item>Take each block tail-first within its share of the budget, then promote any
    /// <see cref="FailureSummaryLine"/> that survived filtering but fell outside that cut, so the
    /// failing assertion is readable even when it sits above thousands of trailing characters.</item>
    /// </list>
    /// The whole result — promoted excerpt, raw-output pointer line and per-member tails together —
    /// stays bounded by <see cref="MaxTailChars"/>, never one-member-worth-of-bound times N members.
    /// Nothing dropped here is lost: <paramref name="rawOutputFileName"/> names the file holding the
    /// unfiltered stream, itself bounded by <see cref="MaxRawOutputChars"/>.
    /// </summary>
    /// <param name="rawOutputFileName">
    /// <see cref="RawOutputFileName"/> when it was written, null when no directory was supplied or the
    /// write failed. Non-null adds one pointer line at the top of the tail, inside the budget.
    /// </param>
    private static string BuildTail(string output, IReadOnlyList<string>? failingMembers, string? rawOutputFileName)
    {
        var pointer = rawOutputFileName is null
            ? null
            : $"[verify] full unfiltered output: '{rawOutputFileName}' in this execution's artifacts directory.";
        var budget = Math.Max(1, MaxTailChars - (pointer is null ? 0 : pointer.Length + 1));
        var body = BuildFilteredBody(SelectBlocks(output, failingMembers), budget);
        return pointer is null ? body : pointer + "\n" + body;
    }

    /// <summary>
    /// Step 1 and 2 above: the failing members' own blocks, noise-filtered — or a single whole-stream
    /// block on any of the three shape-drift fallbacks (no members named, no marker line at all, no
    /// marker matching a named member). Never empty.
    /// </summary>
    private static List<string> SelectBlocks(string output, IReadOnlyList<string>? failingMembers)
    {
        var blocks = SelectMemberBlocks(output, failingMembers) ?? [output];
        return [.. blocks.Select(DropFixtureNoise)];
    }

    private static List<string>? SelectMemberBlocks(string output, IReadOnlyList<string>? failingMembers)
    {
        if (failingMembers is not { Count: > 0 })
        {
            return null;
        }

        var matches = MemberMarkerLine.Matches(output);
        if (matches.Count == 0)
        {
            return null;
        }

        var wanted = new HashSet<string>(failingMembers, StringComparer.Ordinal);
        var blocks = new List<string>();
        var blockStart = 0;
        foreach (Match match in matches)
        {
            var blockEnd = match.Index + match.Length;
            if (match.Groups["status"].Value != "pass" && wanted.Contains(match.Groups["name"].Value))
            {
                blocks.Add(output[blockStart..blockEnd]);
            }

            blockStart = blockEnd;
        }

        return blocks.Count == 0 ? null : blocks;
    }

    /// <summary>
    /// #1971. A block whose every line is noise keeps its unfiltered text: the filter exists to stop
    /// noise CROWDING OUT the failure, not to blank a tail that has nothing else in it.
    /// </summary>
    private static string DropFixtureNoise(string block)
    {
        var kept = block.Split('\n').Where(line => !FixtureNoiseLine.IsMatch(line)).ToList();
        var filtered = string.Join("\n", kept);
        return filtered.Trim().Length == 0 ? block : filtered;
    }

    /// <summary>
    /// Step 3 above. The promoted excerpt is computed against a CONSERVATIVELY clamped tail (one that
    /// already gave up <see cref="MaxPromotedChars"/> AND the newline that joins the two halves), so
    /// the tail actually returned is never smaller than the one the excerpt was chosen against. The
    /// <c>- 1</c> is what makes that exact rather than off by one (#1971 review): the returned tail's
    /// own budget is <c>budget - promoted.Length - 1</c> and <c>promoted.Length</c> can equal
    /// <c>reserve</c>, so conceding only <c>reserve</c> here would leave the returned tail one
    /// character narrower than the one measured against.
    /// <para>
    /// What that buys, stated no wider than it is: no surviving failure line SHORT ENOUGH FOR THE
    /// RESERVE falls between the two halves — judged missing against a cut narrower than the one it is
    /// then measured against. It is not "exactly one half" in general, in either direction. A line too
    /// long for the reserve appears in neither (it is skipped by the promotion walk and sits above the
    /// tail — bounded by <see cref="RawOutputFileName"/>, which holds it either way), and a line inside
    /// the band between the two clamps can appear in both, since the returned tail is wider than
    /// <c>alreadyShown</c> by however much of the reserve the excerpt did not spend. A duplicate costs
    /// a reader nothing; a line falling through the gap costs them the failure.
    /// </para>
    /// </summary>
    private static string BuildFilteredBody(List<string> blocks, int budget)
    {
        var reserve = Math.Min(MaxPromotedChars, budget / 2);
        var conservativeTail = ClampBlocks(blocks, Math.Max(1, budget - reserve - 1));
        var promoted = PromoteFailureLines(blocks, conservativeTail, reserve);
        return promoted is null
            ? ClampBlocks(blocks, budget)
            : promoted + "\n" + ClampBlocks(blocks, Math.Max(1, budget - promoted.Length - 1));
    }

    /// <summary>
    /// Each block kept tail-first inside its even share of <paramref name="budget"/>, joined and then
    /// clamped once more overall — rounding (integer division) plus the join separators themselves can
    /// otherwise push the total slightly over, leaving the per-section cap as an approximation of the
    /// real bound rather than the bound.
    /// </summary>
    private static string ClampBlocks(List<string> blocks, int budget)
    {
        var perBlockBudget = Math.Max(1, budget / blocks.Count);
        var sections = blocks.Select(block => block.Length > perBlockBudget ? block[^perBlockBudget..] : block);
        var joined = string.Join("\n", sections);
        return joined.Length > budget ? joined[^budget..] : joined;
    }

    /// <summary>
    /// The <see cref="FailureSummaryLine"/> matches that are NOT already in <paramref name="alreadyShown"/>,
    /// most recent first-fitting, rendered as one labelled excerpt — or null when the tail already
    /// carries every one of them (the common case, and the one where a second copy would only spend
    /// budget). Scans the already-noise-filtered blocks, never the raw stream.
    /// </summary>
    private static string? PromoteFailureLines(List<string> blocks, string alreadyShown, int reserve)
    {
        const string header = "[verify] failure lines from earlier in this command's output:";
        if (reserve <= header.Length)
        {
            return null;
        }

        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in blocks.SelectMany(block => block.Split('\n')).Select(line => line.TrimEnd('\r')))
        {
            if (line.Trim().Length == 0 || !FailureSummaryLine.IsMatch(line))
            {
                continue;
            }

            if (alreadyShown.Contains(line, StringComparison.Ordinal) || !seen.Add(line))
            {
                continue;
            }

            missing.Add(line);
        }

        if (missing.Count == 0)
        {
            return null;
        }

        // Kept from the END backwards: the runner's own failure summary is the last thing it prints,
        // and a truncated excerpt that keeps the first N of a hundred failing tests is the least
        // useful cut available. A line too long for what is left is SKIPPED, not a stop (#1971
        // review): one 2000-char `Assert.Equal()` string-diff message must not suppress the short
        // `Failed <test>` line above it, which is the one a reader wants most.
        var taken = new List<string>();
        var spent = header.Length;
        for (var i = missing.Count - 1; i >= 0; i--)
        {
            if (spent + missing[i].Length + 1 > reserve)
            {
                continue;
            }

            spent += missing[i].Length + 1;
            taken.Add(missing[i]);
        }

        taken.Reverse();
        return taken.Count == 0 ? null : header + "\n" + string.Join("\n", taken);
    }

    /// <summary>
    /// #1971: the verify command's combined stream, unfiltered, in the execution's own artifacts
    /// directory — what makes <see cref="BuildTail"/>'s filtering a display choice rather than a loss.
    /// Bounded by <see cref="MaxRawOutputChars"/>, which is what "unfiltered" means here: every line is
    /// kept as it was printed, and only a run that exceeds that cap loses its middle, to a marker that
    /// says exactly how much.
    /// Returns the filename written, or null when there was no directory to write to or the write
    /// failed; a failure is reported on stderr and never thrown, mirroring
    /// <c>OutputMaterializer.TryCaptureFinalResponse</c>'s own rule that a diagnostic write must never
    /// take down the settle path it runs on — the same four exception types, and the same inner guard
    /// around the warning itself (#1971 review). The caller has already appended
    /// <c>FlowEvent.VerifyStarted</c> and its own <c>try</c> catches none of these, so an escape here
    /// is a room stuck at <c>VerifyStarted</c> with no terminal event: a diagnostic file failing to
    /// write must cost the file, never the settle.
    /// </summary>
    private static async Task<string?> TryWriteRawOutputAsync(string? outputDirectory, string text)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, RawOutputFileName), CapRawOutput(text))
                .ConfigureAwait(false);
            return RawOutputFileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            try
            {
                Console.Error.WriteLine(
                    $"Warning: #1971 could not write the verify command's raw output to '{RawOutputFileName}' "
                    + $"in '{outputDirectory}': {ex.Message}. The filtered tail is still recorded.");
            }
            catch (IOException)
            {
                // The engine's own stderr is unwritable (a closed or broken pipe) —
                // OutputMaterializer.TryCaptureFinalResponse's review-F6 guard, for the same reason:
                // the warning above is best-effort by construction, and the one thing this path must
                // not do is throw. Nothing further to do here.
            }

            return null;
        }
    }

    /// <summary>
    /// <see cref="MaxRawOutputChars"/> applied head-and-tail: the start of the run (which member ran,
    /// what it was doing) and its end (the failure summary) are the two halves worth keeping, and what
    /// went between them is named rather than silently dropped. Under the cap this returns the text
    /// unchanged, which is every ordinary run.
    /// </summary>
    private static string CapRawOutput(string text)
    {
        if (text.Length <= MaxRawOutputChars)
        {
            return text;
        }

        var keep = MaxRawOutputChars - MaxRawOutputElisionReserve;
        var head = keep / 2;
        var tail = keep - head;
        var marker = $"\n\n[verify] {text.Length - keep} characters elided here: this command's output "
            + $"exceeded the {MaxRawOutputChars}-character cap on '{RawOutputFileName}'.\n\n";
        return text[..head] + marker + text[^tail..];
    }

    /// <summary>
    /// #1796: which of gates.py's two verdict markers is present, and the member names it names.
    /// <see cref="FailMarker"/> is checked first — a mixed run (a real gate failure alongside a
    /// buildlock-blocked member) still prints <c>"GATES: FAIL ..."</c> naming only the real failures
    /// (gates.py's own <c>summarise()</c> precedence), so this returns <see
    /// cref="VerifyFailedKind.GatesFailed"/> for that run and <see cref="BlockedMarker"/> is never
    /// reached. Only when no <see cref="FailMarker"/> line is found at all does a <see
    /// cref="BlockedMarker"/> line yield <see cref="VerifyFailedKind.BuildLockBusy"/>. Neither marker
    /// found (a shape drift) falls back to <see cref="VerifyFailedKind.GatesFailed"/> with no member
    /// names — never a fabricated guess, matching this method's pre-#1796 fallback.
    /// </summary>
    private static (VerifyFailedKind Kind, IReadOnlyList<string>? Members) ParseVerdict(string output)
    {
        var failing = ParseNamesForMarker(output, FailMarker);
        if (failing is not null)
        {
            return (VerifyFailedKind.GatesFailed, failing);
        }

        var blocked = ParseNamesForMarker(output, BlockedMarker);
        return blocked is not null
            ? (VerifyFailedKind.BuildLockBusy, blocked)
            : (VerifyFailedKind.GatesFailed, null);
    }

    private static IReadOnlyList<string>? ParseNamesForMarker(string output, string marker)
    {
        foreach (var line in output.Split('\n'))
        {
            var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var separatorIndex = line.IndexOf(FailingMembersSeparator, markerIndex, StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                continue;
            }

            var names = line[(separatorIndex + FailingMembersSeparator.Length)..]
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return names.Length > 0 ? names : null;
        }

        return null;
    }

    /// <summary>
    /// #1796: pulls the seconds-waited and holder text out of <c>tools/buildlock.py</c>'s own BLOCKED
    /// line (<see cref="BuildLockBlockedLine"/>), and reformats it into the one-line reason
    /// <c>FlowEvent.VerifyNotRun.Reason</c> journals verbatim. Null on a shape miss — never a
    /// fabricated holder. Reads the RAW captured stream rather than <see cref="BuildTail"/>'s output
    /// (#1971): see the call site for why a settlement must not depend on a display rule.
    /// <para>
    /// #1971 review: the BLOCKED members' own blocks are searched FIRST, in order, so a run with two
    /// blocked members naming different holders reports the holder that actually blocked a named
    /// member rather than whichever line the whole stream happened to print earliest. The whole-stream
    /// scan stays as the fallback, and that is not belt-and-braces: buildlock's line can be printed
    /// outside any member's block (before the first marker, or by the harness rather than the wrapped
    /// member), and scoping alone would turn a reason that resolves today into null — the one
    /// direction this parse must never move in, since a settlement's journaled text is what a
    /// conductor reads to decide whether to retry.
    /// </para>
    /// </summary>
    private static string? ExtractBuildLockReason(string? output, IReadOnlyList<string>? blockedMembers)
    {
        if (output is null)
        {
            return null;
        }

        foreach (var block in SelectMemberBlocks(output, blockedMembers) ?? [])
        {
            if (BuildLockBlockedLine.Match(block) is { Success: true } scoped)
            {
                return Format(scoped);
            }
        }

        var match = BuildLockBlockedLine.Match(output);
        return match.Success ? Format(match) : null;

        static string Format(Match match) =>
            $"build lock busy for {match.Groups["secs"].Value}s (holder: {match.Groups["holder"].Value})";
    }
}
