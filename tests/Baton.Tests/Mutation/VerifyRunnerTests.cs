using Baton.Mutation;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// Coverage for <see cref="VerifyRunner"/> (#1623) against a fake command, via its internal
/// program/args seam — never a real, minutes-long <c>pixi run gates-quiet</c>.
/// </summary>
public sealed class VerifyRunnerTests
{
    [Fact]
    public async Task An_exit_zero_command_reports_Passed()
    {
        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", "exit 0"], workingDirectory: null, CancellationToken.None);

        Assert.True(outcome.Passed);
        Assert.Null(outcome.FailingMembers);
        Assert.Null(outcome.Tail);
    }

    [Fact]
    public async Task A_nonzero_exit_reports_Failed_with_the_captured_output_as_the_tail()
    {
        var outcome = await VerifyRunner.RunProcessAsync(
            "cmd", ["/c", "echo something went wrong & exit 1"], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.GatesFailed, outcome.Kind);
        Assert.NotNull(outcome.Tail);
        Assert.Contains("something went wrong", outcome.Tail);
    }

    [Fact]
    public async Task A_GATES_FAIL_summary_line_yields_the_named_failing_members()
    {
        // The exact shape tools/gates/gates.py's summarise() emits.
        var outcome = await VerifyRunner.RunProcessAsync(
            "cmd", ["/c", "echo GATES: FAIL 2 of 25 -- fmt-check, lint & exit 1"], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.GatesFailed, outcome.Kind);
        Assert.NotNull(outcome.FailingMembers);
        Assert.Equal(["fmt-check", "lint"], outcome.FailingMembers);
    }

    [Fact]
    public async Task A_failure_with_no_recognizable_summary_line_leaves_FailingMembers_null_not_fabricated()
    {
        var outcome = await VerifyRunner.RunProcessAsync(
            "cmd", ["/c", "echo unstructured failure output & exit 1"], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.GatesFailed, outcome.Kind);
        Assert.Null(outcome.FailingMembers);
    }

    [Fact]
    public async Task A_long_failure_tail_is_bounded_not_a_full_log_dump()
    {
        // "echo" on cmd.exe fits comfortably under the 4000-char tail bound in one line, so build a
        // multi-line failure via several chained echoes instead.
        var chunk = new string('x', 500);
        var command = string.Join(" & ", Enumerable.Repeat($"echo {chunk}", 12)) + " & exit 1";

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", command], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.GatesFailed, outcome.Kind);
        Assert.NotNull(outcome.Tail);
        Assert.True(outcome.Tail!.Length <= 4000);
    }

    [Fact]
    public async Task The_tail_is_the_failing_members_own_block_not_a_blind_whole_stream_tail()
    {
        // Mirrors tools/gates/gates.py's own per-member marker line shape: "  pass  name  (exit 0)"
        // / "  FAIL  name  (exit 1)", one after each member's own output. gate-b's own diagnostic
        // text ("GATE_B_UNIQUE_FAILURE_TEXT") sits well before a long run of trailing pass markers
        // and padding -- a blind tail of the whole stream (the pre-#1701 behavior) would have cut
        // it, since the padding after gate-b's marker line alone exceeds the 4000-char tail bound.
        var padding = new string('p', 200);
        var trailingPadding = string.Join(" & ", Enumerable.Repeat($"echo {padding}", 25));
        var command = string.Join(" & ", new[]
        {
            "echo   pass  gate-a  (exit 0)",
            "echo GATE_B_UNIQUE_FAILURE_TEXT",
            "echo   FAIL  gate-b  (exit 1)",
            trailingPadding,
            "echo   pass  gate-c  (exit 0)",
            "echo GATES: FAIL 1 of 3 -- gate-b",
        }) + " & exit 1";

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", command], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(["gate-b"], outcome.FailingMembers);
        Assert.NotNull(outcome.Tail);
        Assert.Contains("GATE_B_UNIQUE_FAILURE_TEXT", outcome.Tail);
        Assert.DoesNotContain(padding, outcome.Tail);
    }

    [Fact]
    public async Task Two_failing_members_own_blocks_are_both_present_and_the_joined_total_still_bounded()
    {
        // #1701 review: a naive per-member cap (each independently allowed the full MaxTailChars)
        // would let N failing members yield N times the intended bound. Two large failing blocks must
        // still fit inside one MaxTailChars-sized joined result, and each contributes its OWN tail
        // text, not just whichever member happened to be captured last.
        var bigBlock = new string('x', 3000);
        var command = string.Join(" & ", new[]
        {
            $"echo {bigBlock}",
            "echo GATE_A_TEXT",
            "echo   FAIL  gate-a  (exit 1)",
            $"echo {bigBlock}",
            "echo GATE_B_TEXT",
            "echo   FAIL  gate-b  (exit 1)",
            "echo GATES: FAIL 2 of 2 -- gate-a, gate-b",
        }) + " & exit 1";

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", command], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(["gate-a", "gate-b"], outcome.FailingMembers);
        Assert.NotNull(outcome.Tail);
        Assert.True(outcome.Tail!.Length <= 4000, $"joined tail was {outcome.Tail.Length} chars, want <= 4000");
        Assert.Contains("GATE_A_TEXT", outcome.Tail);
        Assert.Contains("GATE_B_TEXT", outcome.Tail);
    }

    [Fact]
    public async Task A_marker_line_present_with_no_matching_FAIL_entry_falls_back_to_the_whole_stream_tail()
    {
        // Shape drift: the summary line named a failing member, but no per-member marker line for
        // that name is anywhere in the output (e.g. gates.py changed its own summary vocabulary).
        // Must degrade to the pre-#1701 whole-stream tail, never silently return an empty Tail.
        var command = "echo   pass  gate-a  (exit 0) & echo GATES: FAIL 1 of 2 -- gate-missing & exit 1";

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", command], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(["gate-missing"], outcome.FailingMembers);
        Assert.NotNull(outcome.Tail);
        Assert.Contains("GATES: FAIL 1 of 2 -- gate-missing", outcome.Tail);
    }

    [Fact]
    public async Task A_GATES_BLOCKED_summary_line_yields_BuildLockBusy_and_the_blocked_members()
    {
        // #1796: gates.py's summarise() shape for an all-blocked run.
        var outcome = await VerifyRunner.RunProcessAsync(
            "cmd", ["/c", "echo GATES: BLOCKED 1 of 25 -- lint & exit 3"], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.BuildLockBusy, outcome.Kind);
        Assert.Equal(["lint"], outcome.FailingMembers);
    }

    [Fact]
    public async Task A_mixed_BLOCKED_and_FAIL_run_reports_GatesFailed_naming_only_the_real_failures()
    {
        // gates.py's own precedence: a real failure alongside a blocked member still headlines
        // "GATES: FAIL ..." naming only the real failure, never the blocked one -- the mixed case
        // must settle VerifyFailed, not VerifyNotRun/BuildLockBusy.
        var command = string.Join(" & ", new[]
        {
            "echo   BLOCKED  lint  (exit 75)",
            "echo   FAIL  fmt-check  (exit 2)",
            "echo GATES: FAIL 1 of 25 -- fmt-check",
        }) + " & exit 1";

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", command], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.GatesFailed, outcome.Kind);
        Assert.Equal(["fmt-check"], outcome.FailingMembers);
    }

    [Fact]
    public async Task A_BLOCKED_member_s_own_buildlock_line_yields_the_NotRunReason_text()
    {
        // #1796: what that member actually prints to stdout, verbatim.
        var command = string.Join(" & ", new[]
        {
            "echo buildlock: BLOCKED after 1800s waiting for the build lock held by PID 1234 (dotnet build) since 2026-09-04 01:00:00 -- raise BATON_BUILDLOCK_TIMEOUT_S or find out why the holder is stuck",
            "echo   BLOCKED  lint  (exit 75)",
            "echo GATES: BLOCKED 1 of 25 -- lint",
        }) + " & exit 3";

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", command], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.BuildLockBusy, outcome.Kind);
        Assert.NotNull(outcome.NotRunReason);
        Assert.Contains("build lock busy for 1800s", outcome.NotRunReason);
        Assert.Contains("PID 1234 (dotnet build) since 2026-09-04 01:00:00", outcome.NotRunReason);
    }

    [Fact]
    public async Task A_holder_whose_own_command_line_carries_double_dash_flags_is_named_whole_not_truncated()
    {
        // #1813 review: the holder text embeds the wrapped command verbatim, and vendor-check's is the
        // worst case -- a standalone `--` token AND a `--check` flag. The capture must run to
        // buildlock.py's trailing sentinel, never stop at the first ` --` inside the command.
        var command = string.Join(" & ", new[]
        {
            "echo buildlock: BLOCKED after 1800s waiting for the build lock held by PID 4242 (dotnet run --project tools/Baton.VendorProbe -- --check) since 2026-09-04 02:00:00 -- raise BATON_BUILDLOCK_TIMEOUT_S or find out why the holder is stuck",
            "echo   BLOCKED  vendor-check  (exit 75)",
            "echo GATES: BLOCKED 1 of 25 -- vendor-check",
        }) + " & exit 3";

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", command], workingDirectory: null, CancellationToken.None);

        Assert.Equal(Baton.Domain.VerifyFailedKind.BuildLockBusy, outcome.Kind);
        Assert.Equal(
            "build lock busy for 1800s (holder: PID 4242 (dotnet run --project tools/Baton.VendorProbe -- --check) since 2026-09-04 02:00:00)",
            outcome.NotRunReason);
    }

    [Fact]
    public async Task A_BLOCKED_verdict_with_no_recognizable_buildlock_line_leaves_NotRunReason_null_not_fabricated()
    {
        var outcome = await VerifyRunner.RunProcessAsync(
            "cmd", ["/c", "echo GATES: BLOCKED 1 of 25 -- lint & exit 3"], workingDirectory: null, CancellationToken.None);

        Assert.Equal(Baton.Domain.VerifyFailedKind.BuildLockBusy, outcome.Kind);
        Assert.Null(outcome.NotRunReason);
    }

    [Fact]
    public async Task An_unspawnable_program_reports_Failed_rather_than_throwing()
    {
        var outcome = await VerifyRunner.RunProcessAsync(
            "this-program-does-not-exist-anywhere", [], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.NotNull(outcome.Tail);
    }

    [Fact]
    public async Task Cancellation_during_verify_reports_Cancelled_kind()
    {
        // Found while fixing #1706 (its own issue, fixed here per docs/agents/developing-baton.md's found-while-fixing rule):
        // this raced `cmd /c exit 0` against an already-cancelled token and asserted the token won.
        // On an idle machine it does; inside `pixi run gates-quiet`, with five test assemblies and a
        // build competing for cores, the child sometimes exited 0 first and the run reported Passed --
        // a flake that fails the whole gate for a reason unrelated to whatever change is being gated.
        // A command that cannot finish first removes the race rather than widening a timing window:
        // the outcome is now decided by the cancellation alone, which is the only thing this test is
        // about. Windows-only shape, matching every other `cmd` fixture in this file and CI's
        // Windows-only posture (#1405).
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await VerifyRunner.RunProcessAsync(
            "cmd", ["/c", "ping -n 30 127.0.0.1 >nul"], workingDirectory: null, cts.Token);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.Cancelled, outcome.Kind);
    }

    [Fact]
    public async Task A_pre_cancelled_token_never_launches_the_process()
    {
        // #1722: the actual production race was a FAST child (`cmd /c exit 0`) exiting before the
        // cancellation was observed, so a marker file written by that same child is not a reliable
        // instrument here -- even on unpatched code the child is USUALLY killed fast enough that the
        // marker is never written, even though the process still launched. A wall-clock threshold is
        // not the instrument either: this file already records one load flake of that shape (see the
        // comment on Cancellation_during_verify_reports_Cancelled_kind). The pre-spawn guard is the only
        // arm that can produce this exact Tail, and it textually precedes RunProcessAsync's sole
        // CaptureAsync call, so pinning the Tail pins "never launched" by construction, deterministically:
        // on unpatched code the outcome is either Pass (Tail null) or the BatonCancelException arm's
        // "Verify command cancelled: ..." text, never this string.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", "exit 0"], workingDirectory: null, cts.Token);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.Cancelled, outcome.Kind);
        Assert.Equal("Verify command cancelled before it was launched.", outcome.Tail);
    }

    [Fact]
    public async Task Cancellation_during_a_still_running_child_reports_Cancelled_and_kills_the_tree()
    {
        // #1722: cancel WHILE a long-running child is mid-flight (rather than pre-cancelling), and
        // prove the tree is actually killed rather than orphaned -- a second command chained after
        // the long-running one would create a marker file if the child were left to finish, which
        // must never happen once the token fires.
        var markerPath = Path.Combine(Path.GetTempPath(), $"baton-1722-{Guid.NewGuid():N}.marker");
        try
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(300));

            var outcome = await VerifyRunner.RunProcessAsync(
                "cmd",
                ["/c", $"ping -n 10 127.0.0.1 >nul & echo marker > \"{markerPath}\" & exit 0"],
                workingDirectory: null,
                cts.Token);

            Assert.False(outcome.Passed);
            Assert.Equal(Baton.Domain.VerifyFailedKind.Cancelled, outcome.Kind);
            Assert.False(File.Exists(markerPath), "the marker file exists, so the child tree kept running (and exited 0) past cancellation instead of being killed");
        }
        finally
        {
            Baton.Tests.Shared.FileCleanup.Delete(markerPath);
        }
    }

    /// <summary>
    /// #1971, the measured failure: on <c>dispatch-implement-1b79802b</c> and <c>-311af7d4</c> the
    /// <c>test-no-build</c> member's tail was <c>CAPTURED (#1594)</c> markers and stream-logger
    /// access-denied warnings — noise Baton's OWN test fixtures print to stderr as they exercise those
    /// paths — end to end, with the failing assertion nowhere in it. The fixture below reproduces that
    /// exact shape: a real failure summary near the top of the member's block, then more than a full
    /// tail's worth of fixture noise after it.
    /// <para>
    /// The discriminating assertion is <c>DoesNotContain</c> on the STREAM-LOGGER line, not on the
    /// CAPTURED one: that warning literally reads "Warning: Failed to initialize execution stream
    /// logger", so an implementation that promotes failure lines with a bare <c>Contains("Failed")</c>
    /// re-surfaces the very line the filter just dropped, and a test that only excludes CAPTURED passes
    /// against it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_tail_drops_fixture_noise_carries_the_failure_summary_and_leaves_the_raw_stream_on_disk()
    {
        var fixtureDirectory = Path.Combine(Path.GetTempPath(), $"baton-1971-{Guid.NewGuid():N}");
        var rawDirectory = Path.Combine(fixtureDirectory, "artifacts");
        try
        {
            Directory.CreateDirectory(fixtureDirectory);
            var noise = string.Join("\n", Enumerable.Range(0, 40).Select(index =>
                $"CAPTURED (#1594): the worker's declared output(s) 'report-{index}.md' were never written by the worker "
                + "itself. baton captured its terminal response to '.captured-response.md' -- the declared output(s) were "
                + "NOT written, and this execution settles Indeterminate pending conductor resolution ('baton resolve').\n"
                + $"Warning: Failed to initialize execution stream logger for 'C:\\rooms\\execution_{index}': Access to the "
                + "path 'C:\\rooms\\stdout.log' is denied. Stream logging disabled for this execution."));

            var stream = string.Join("\n",
                "  pass  fmt-check  (exit 0)",
                noise,
                "  Failed Baton.Tests.Rooms.RoomStateTests.The_room_settles_Failed [24 ms]",
                "  Error Message:",
                "   Assert.Equal() Failure: Values differ",
                noise,
                "  FAIL  test-no-build  (exit 1)",
                "GATES: FAIL 1 of 25 -- test-no-build",
                string.Empty);
            await File.WriteAllTextAsync(
                Path.Combine(fixtureDirectory, "stream.txt"), stream, TestContext.Current.CancellationToken);

            // Relative filename against an explicit working directory: no quoting inside the `cmd /c`
            // argument, so the fixture path can never be mangled by the shell.
            var outcome = await VerifyRunner.RunProcessAsync(
                "cmd", ["/c", "type stream.txt & exit 1"], fixtureDirectory, TestContext.Current.CancellationToken,
                rawOutputDirectory: rawDirectory);

            Assert.False(outcome.Passed);
            Assert.Equal(["test-no-build"], outcome.FailingMembers);
            Assert.NotNull(outcome.Tail);
            Assert.True(outcome.Tail!.Length <= 4000, $"tail was {outcome.Tail.Length} chars, want <= 4000");

            Assert.Contains("Assert.Equal() Failure: Values differ", outcome.Tail, StringComparison.Ordinal);
            Assert.Contains("Failed Baton.Tests.Rooms.RoomStateTests", outcome.Tail, StringComparison.Ordinal);
            Assert.DoesNotContain("Failed to initialize execution stream logger", outcome.Tail, StringComparison.Ordinal);
            Assert.DoesNotContain("CAPTURED (#1594)", outcome.Tail, StringComparison.Ordinal);

            // Nothing is lost: the filter is a display rule, and the whole unfiltered stream sits one
            // file open away, in the same directory as the execution's other artifacts.
            var rawPath = Path.Combine(rawDirectory, VerifyRunner.RawOutputFileName);
            Assert.True(File.Exists(rawPath), $"the raw verify output was not written to '{rawPath}'");
            var raw = await File.ReadAllTextAsync(rawPath, TestContext.Current.CancellationToken);
            Assert.Contains("Failed to initialize execution stream logger", raw, StringComparison.Ordinal);
            Assert.Contains("CAPTURED (#1594)", raw, StringComparison.Ordinal);
            Assert.Contains(VerifyRunner.RawOutputFileName, outcome.Tail, StringComparison.Ordinal);
        }
        finally
        {
            Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively(fixtureDirectory);
        }
    }

    /// <summary>
    /// #1971, the half the noise filter alone does not buy: a failure summary can be pushed out of the
    /// tail by ordinary, non-noise output too (a long build log after the failing test). The promoted
    /// excerpt is what keeps it readable — nothing here matches <c>FixtureNoiseLine</c>, so a filter
    /// with no promotion step fails this and passes the test above.
    /// </summary>
    [Fact]
    public async Task A_failure_summary_above_a_full_tails_worth_of_ordinary_output_is_still_promoted_into_the_tail()
    {
        var fixtureDirectory = Path.Combine(Path.GetTempPath(), $"baton-1971-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(fixtureDirectory);
            var padding = string.Join("\n", Enumerable.Range(0, 60).Select(index => $"  ordinary build chatter line {index} {new string('y', 90)}"));
            var stream = string.Join("\n",
                "   Assert.True() Failure: the projection was not rebuilt",
                padding,
                "  FAIL  test-no-build  (exit 1)",
                "GATES: FAIL 1 of 25 -- test-no-build",
                string.Empty);
            await File.WriteAllTextAsync(
                Path.Combine(fixtureDirectory, "stream.txt"), stream, TestContext.Current.CancellationToken);

            var outcome = await VerifyRunner.RunProcessAsync(
                "cmd", ["/c", "type stream.txt & exit 1"], fixtureDirectory, TestContext.Current.CancellationToken);

            Assert.False(outcome.Passed);
            Assert.NotNull(outcome.Tail);
            Assert.True(outcome.Tail!.Length <= 4000, $"tail was {outcome.Tail.Length} chars, want <= 4000");
            Assert.Contains("Assert.True() Failure: the projection was not rebuilt", outcome.Tail, StringComparison.Ordinal);
            // Polarity: the tail is still the command's own LAST lines, not only the promoted excerpt.
            Assert.Contains("ordinary build chatter line 59", outcome.Tail, StringComparison.Ordinal);
            // No directory was supplied, so no raw file is written and no pointer line is fabricated.
            Assert.DoesNotContain(VerifyRunner.RawOutputFileName, outcome.Tail, StringComparison.Ordinal);
        }
        finally
        {
            Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively(fixtureDirectory);
        }
    }

    /// <summary>
    /// #1971 review: the noise filter's MIS-CUT direction, which the two arms above do not pin — they
    /// assert the noise goes and the failure stays, never that a genuine failure line QUOTING one of
    /// the two noise messages survives. It has to: a failing test about the #1594 capture marker, or
    /// about the stream logger's own warning, prints that text inside its assertion output, and a
    /// filter that dropped it would hand the reader a tail with the failure's own lines removed while
    /// looking, from the outside, exactly like a clean filter. Both anchors are exercised, and the
    /// second is the one a substring filter fails: <c>Assert.Contains() Failure: 'Warning: Failed to
    /// initialize...'</c> is a failure line whose text contains a whole noise message.
    /// </summary>
    [Fact]
    public async Task A_failure_line_that_quotes_a_noise_message_is_kept_while_the_noise_itself_is_dropped()
    {
        var fixtureDirectory = Path.Combine(Path.GetTempPath(), $"baton-1971-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(fixtureDirectory);
            var stream = string.Join("\n",
                "  pass  fmt-check  (exit 0)",
                "  Failed Baton.Tests.Outcomes.CapturedResponseTests.The_marker_names_the_issue [7 ms]",
                "   Assert.Equal() Failure: expected the marker line 'CAPTURED (#1594): report.md was never written'",
                "   Assert.Contains() Failure: 'Warning: Failed to initialize execution stream logger for room X' not found",
                "CAPTURED (#1594): the worker's declared output 'report.md' was never written by the worker itself.",
                @"Warning: Failed to initialize execution stream logger for 'C:\rooms\x': Access to the path is denied.",
                "  FAIL  test-no-build  (exit 1)",
                "GATES: FAIL 1 of 25 -- test-no-build",
                string.Empty);
            await File.WriteAllTextAsync(
                Path.Combine(fixtureDirectory, "stream.txt"), stream, TestContext.Current.CancellationToken);

            var outcome = await VerifyRunner.RunProcessAsync(
                "cmd", ["/c", "type stream.txt & exit 1"], fixtureDirectory, TestContext.Current.CancellationToken);

            Assert.False(outcome.Passed);
            Assert.NotNull(outcome.Tail);
            // Kept: both quoting lines are the failure's own diagnostic text, not the noise.
            Assert.Contains(
                "expected the marker line 'CAPTURED (#1594): report.md was never written'",
                outcome.Tail!, StringComparison.Ordinal);
            Assert.Contains(
                "'Warning: Failed to initialize execution stream logger for room X' not found",
                outcome.Tail, StringComparison.Ordinal);
            // Polarity, same fixture: the real noise lines, which differ only in where the message
            // sits on the line, are still gone.
            Assert.DoesNotContain("was never written by the worker itself", outcome.Tail, StringComparison.Ordinal);
            Assert.DoesNotContain("Access to the path is denied", outcome.Tail, StringComparison.Ordinal);
        }
        finally
        {
            Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively(fixtureDirectory);
        }
    }

    /// <summary>
    /// #1971 review: an over-long failure line must not suppress the shorter ones ABOVE it. Promotion
    /// walks the missing failure lines from the end backwards, and a line that does not fit what is
    /// left of the reserve is skipped rather than ending the walk — otherwise the 2000-character
    /// <c>Assert.Equal()</c> string diff in the middle of this fixture would cost the reader the
    /// short <c>Failed &lt;test&gt;</c> line above it, which is the one naming what broke.
    /// </summary>
    [Fact]
    public async Task An_over_long_failure_line_does_not_suppress_the_shorter_ones_above_it()
    {
        var fixtureDirectory = Path.Combine(Path.GetTempPath(), $"baton-1971-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(fixtureDirectory);
            // Longer than the promotion reserve (1200 chars) on its own, so it can never be taken.
            var overLong = "   Assert.Equal() Failure: " + new string('z', 2000);
            // Wider than the whole tail budget, so all three failure lines above it fall outside both
            // the conservative cut the excerpt is chosen against and the returned tail itself.
            var padding = string.Join("\n", Enumerable.Range(0, 50).Select(i => $"  ordinary build chatter line {i} {new string('y', 90)}"));
            var stream = string.Join("\n",
                "  Failed Baton.Tests.Rooms.RoomStateTests.TestA [3 ms]",
                overLong,
                "Failed! - Failed: 3, Passed: 0, Skipped: 0, Total: 3",
                padding,
                "  FAIL  test-no-build  (exit 1)",
                "GATES: FAIL 1 of 25 -- test-no-build",
                string.Empty);
            await File.WriteAllTextAsync(
                Path.Combine(fixtureDirectory, "stream.txt"), stream, TestContext.Current.CancellationToken);

            var outcome = await VerifyRunner.RunProcessAsync(
                "cmd", ["/c", "type stream.txt & exit 1"], fixtureDirectory, TestContext.Current.CancellationToken);

            Assert.False(outcome.Passed);
            Assert.NotNull(outcome.Tail);
            Assert.True(outcome.Tail!.Length <= 4000, $"tail was {outcome.Tail.Length} chars, want <= 4000");
            // The summary fits and is taken first (walking backwards); the over-long line is then
            // skipped, and this line -- above it -- is still promoted.
            Assert.Contains("Failed! - Failed: 3, Passed: 0", outcome.Tail, StringComparison.Ordinal);
            Assert.Contains("Failed Baton.Tests.Rooms.RoomStateTests.TestA", outcome.Tail, StringComparison.Ordinal);
            // Polarity: skipping it is not the same as spending the reserve on it.
            Assert.DoesNotContain(new string('z', 2000), outcome.Tail, StringComparison.Ordinal);
        }
        finally
        {
            Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively(fixtureDirectory);
        }
    }

    /// <summary>
    /// #1971 review: <c>ExtractBuildLockReason</c> reads the raw stream (a display rule must not decide
    /// a settlement), and <c>Regex.Match</c> over the whole stream returns the FIRST match in it — so
    /// two blocked members with different holders journaled whichever holder printed earliest, not the
    /// one that blocked the member gates.py actually named. The reason is now scoped to the named
    /// members' own blocks first. The pre-existing arms above are the control: one blocked member,
    /// line inside its own block, unchanged.
    /// </summary>
    [Fact]
    public async Task The_reason_names_the_holder_from_the_named_member_s_own_block_not_the_earliest_in_the_stream()
    {
        var command = string.Join(" & ", new[]
        {
            "echo buildlock: BLOCKED after 60s waiting for the build lock held by PID 111 (dotnet format) since 2026-09-04 01:00:00 -- raise BATON_BUILDLOCK_TIMEOUT_S or find out why the holder is stuck",
            "echo   BLOCKED  fmt-check  (exit 75)",
            "echo buildlock: BLOCKED after 1800s waiting for the build lock held by PID 222 (dotnet build) since 2026-09-04 01:30:00 -- raise BATON_BUILDLOCK_TIMEOUT_S or find out why the holder is stuck",
            "echo   BLOCKED  lint  (exit 75)",
            "echo GATES: BLOCKED 1 of 25 -- lint",
        }) + " & exit 3";

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", command], workingDirectory: null, CancellationToken.None);

        Assert.Equal(Baton.Domain.VerifyFailedKind.BuildLockBusy, outcome.Kind);
        Assert.Equal(
            "build lock busy for 1800s (holder: PID 222 (dotnet build) since 2026-09-04 01:30:00)",
            outcome.NotRunReason);
    }

    /// <summary>
    /// #1971 review, the direction the scoping above must never move in: a buildlock line printed
    /// OUTSIDE any member's block (here, after the last marker line — gates.py's own trailing summary
    /// region) still yields a reason. <c>ExtractBuildLockReason</c>'s own remarks say why the fallback
    /// is load-bearing rather than belt-and-braces; this arm is what holds it there.
    /// </summary>
    [Fact]
    public async Task A_buildlock_line_outside_every_member_block_still_yields_the_reason()
    {
        var command = string.Join(" & ", new[]
        {
            "echo   BLOCKED  lint  (exit 75)",
            "echo GATES: BLOCKED 1 of 25 -- lint",
            "echo buildlock: BLOCKED after 900s waiting for the build lock held by PID 333 (dotnet build) since 2026-09-04 03:00:00 -- raise BATON_BUILDLOCK_TIMEOUT_S or find out why the holder is stuck",
        }) + " & exit 3";

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", command], workingDirectory: null, CancellationToken.None);

        Assert.Equal(Baton.Domain.VerifyFailedKind.BuildLockBusy, outcome.Kind);
        Assert.Equal(
            "build lock busy for 900s (holder: PID 333 (dotnet build) since 2026-09-04 03:00:00)",
            outcome.NotRunReason);
    }

    /// <summary>
    /// #1971 review: <c>.verify-output.log</c> lands in an execution's OUTPUT directory, so it is
    /// registered beside the other engine-owned files there — the convention
    /// <c>Dispatch.GrantDecisionLog</c> states and #2009 followed. Without it, the first filtered
    /// listing surface to exist presents the engine's own capture as a worker deliverable (#1351).
    /// </summary>
    [Fact]
    public void The_raw_verify_output_file_is_registered_as_an_engine_owned_name()
        => Assert.True(Baton.Dispatch.ExecutionStreamLogger.IsStreamLogFileName(VerifyRunner.RawOutputFileName));
}
