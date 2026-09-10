using System.Text.Json.Nodes;

namespace Baton.Vendors.Tests;

/// <summary>
/// #1848's admission gate. Every arm drives the REAL vendor parsers
/// (<see cref="ClaudeUsageSlashCommandSource.Parse"/>/<see cref="AgyUsageSlashCommandSource.Parse"/>)
/// rather than hand-built <see cref="VendorUsageWindow"/> values: the window-name table is the whole
/// coupling between #1869's harvest and this gate, and a test that constructs the window names itself
/// would keep passing while a parser renamed them out from under it.
/// </summary>
public class RunwayGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static VendorUsageSnapshot Claude(int weekAllModelsPct, int sessionPct, int weekFablePct = 3) =>
        ClaudeUsageSlashCommandSource.Parse(
            $"""
            Current session: {sessionPct}% used · resets Sep 5, 5:59pm (America/New_York)
            Current week (all models): {weekAllModelsPct}% used · resets Sep 9, 5:59am (America/New_York)
            Current week (Fable): {weekFablePct}% used
            Approximate, based on local sessions on this machine — does not include other devices or claude.ai.
            """,
            Now);

    private static VendorUsageSnapshot Agy(string weeklyRemaining, string fiveHourRemaining) =>
        AgyUsageSlashCommandSource.Parse(
            $"Gemini Models\tWeekly Limit Remaining\t{weeklyRemaining}\t2026-09-09T19:34:12Z\n"
            + $"Gemini Models\tFive Hour Limit Remaining\t{fiveHourRemaining}\t2026-09-05T19:34:12Z\n",
            Now);

    /// <summary>
    /// Synthetic app-server arm: unlike the measured fixture, it supplies both account windows so
    /// both runway thresholds can be exercised. Primary is deliberately weekly, matching the
    /// measured 0.153.2 identity and proving the gate maps by duration rather than property name.
    /// </summary>
    private static VendorUsageSnapshot Codex(int weeklyPct, int sessionPct, DateTimeOffset? harvestedAt = null) =>
        CodexUsageSource.Parse(
            new JsonObject
            {
                ["rateLimitsByLimitId"] = new JsonObject
                {
                    ["codex"] = new JsonObject
                    {
                        ["limitId"] = "codex",
                        ["primary"] = new JsonObject
                        {
                            ["usedPercent"] = weeklyPct,
                            ["windowDurationMins"] = CodexUsageSource.WeeklyDurationMins,
                            ["resetsAt"] = 1_800_000_000,
                        },
                        ["secondary"] = new JsonObject
                        {
                            ["usedPercent"] = sessionPct,
                            ["windowDurationMins"] = CodexUsageSource.FiveHourDurationMins,
                            ["resetsAt"] = 1_800_010_000,
                        },
                    },
                },
            },
            harvestedAt ?? Now);

    private static RunwayDecision Evaluate(string vendor, VendorUsageSnapshot? snapshot, RunwayThresholds? thresholds = null) =>
        RunwayGate.Evaluate(vendor, snapshot, thresholds ?? new RunwayThresholds(), Now);

    // ---- thresholds, both polarities, both axes -------------------------------------------------

    [Fact]
    public void Claude_week_one_below_the_threshold_admits()
    {
        var decision = Evaluate("claude", Claude(weekAllModelsPct: 84, sessionPct: 10));

        Assert.Equal(RunwayDisposition.Admit, decision.Disposition);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Claude_week_at_the_threshold_holds()
    {
        var decision = Evaluate("claude", Claude(weekAllModelsPct: 85, sessionPct: 10));

        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("week (all models)", decision.Reason);
        Assert.Contains("85%", decision.Reason);
    }

    [Fact]
    public void Claude_session_one_below_the_threshold_admits()
    {
        Assert.Equal(RunwayDisposition.Admit, Evaluate("claude", Claude(weekAllModelsPct: 10, sessionPct: 89)).Disposition);
    }

    [Fact]
    public void Claude_session_at_the_threshold_holds()
    {
        var decision = Evaluate("claude", Claude(weekAllModelsPct: 10, sessionPct: 90));

        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("'session'", decision.Reason);
    }

    /// <summary>
    /// The cross-axis control: both single-axis boundary tests above still pass if the week threshold
    /// were wired to the session window, so each axis is also driven with the OTHER one at zero.
    /// </summary>
    [Fact]
    public void Each_axis_holds_on_its_own_window()
    {
        var weekOnly = Evaluate("claude", Claude(weekAllModelsPct: 90, sessionPct: 0));
        var sessionOnly = Evaluate("claude", Claude(weekAllModelsPct: 0, sessionPct: 95));

        Assert.Contains("week (all models)", weekOnly.Reason);
        Assert.Contains("'session'", sessionOnly.Reason);
        Assert.Equal(RunwayDisposition.Hold, weekOnly.Disposition);
        Assert.Equal(RunwayDisposition.Hold, sessionOnly.Disposition);
    }

    /// <summary>
    /// The polarity arm for the excluded window (operator ruling, 2026-09-05; spec/baton.md §7). A
    /// prefix/contains match on "week" would silently pull it into the decision.
    /// </summary>
    [Fact]
    public void Claude_week_Fable_at_99_percent_does_not_hold()
    {
        var decision = Evaluate("claude", Claude(weekAllModelsPct: 10, sessionPct: 10, weekFablePct: 99));

        Assert.Equal(RunwayDisposition.Admit, decision.Disposition);
        Assert.DoesNotContain(decision.Counters, c => c.Window.Contains("Fable", StringComparison.Ordinal));
    }

    [Fact]
    public void Configured_thresholds_replace_the_defaults()
    {
        var thresholds = new RunwayThresholds(WeekHoldPct: 50, SessionHoldPct: 60);

        Assert.Equal(RunwayDisposition.Hold, Evaluate("claude", Claude(60, 10), thresholds).Disposition);
        Assert.Equal(RunwayDisposition.Admit, Evaluate("claude", Claude(49, 59), thresholds).Disposition);
    }

    // ---- agy, and per-vendor isolation ------------------------------------------------------------

    [Fact]
    public void Agy_windows_are_matched_by_their_own_composed_names()
    {
        // 12% remaining is 88% used -- past the 85% week threshold.
        var held = Evaluate("agy", Agy(weeklyRemaining: "12%", fiveHourRemaining: "80%"));
        var admitted = Evaluate("agy", Agy(weeklyRemaining: "80%", fiveHourRemaining: "80%"));

        Assert.Equal(RunwayDisposition.Hold, held.Disposition);
        Assert.Contains("Weekly Limit", held.Reason);
        Assert.Equal(RunwayDisposition.Admit, admitted.Disposition);
        Assert.Equal(2, admitted.Counters.Count);
    }

    [Fact]
    public void A_claude_hold_does_not_hold_agy()
    {
        Assert.Equal(RunwayDisposition.Hold, Evaluate("claude", Claude(99, 99)).Disposition);
        Assert.Equal(RunwayDisposition.Admit, Evaluate("agy", Agy("90%", "90%")).Disposition);
    }

    // ---- every unreadable shape holds -------------------------------------------------------------

    /// <summary>
    /// The no-attempt arm. Since #1923 the production evaluator always attempts a harvest before
    /// reaching here for a gated vendor, so this is the wording a caller that does NOT harvest gets —
    /// it is still the correct fail-closed answer, and it is deliberately not what a failed harvest
    /// says. <see cref="A_missing_snapshot_after_a_failed_harvest_holds_and_names_the_failure"/> is
    /// the arm production actually reaches.
    /// </summary>
    [Fact]
    public void A_missing_snapshot_holds()
    {
        var decision = Evaluate("claude", snapshot: null);

        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("no readable usage snapshot", decision.Reason);
        Assert.Empty(decision.Counters);
    }

    /// <summary>
    /// #1923: "never harvested" and "harvested and it failed" must be distinguishable in the refusal.
    /// Both are Holds — the disposition is asserted on both sides so a future change that admits on a
    /// failed harvest cannot pass by getting the wording right.
    /// </summary>
    [Fact]
    public void A_missing_snapshot_after_a_failed_harvest_holds_and_names_the_failure()
    {
        var attempt = new RunwayHarvestAttempt(
            new DateTimeOffset(2026, 9, 5, 15, 58, 0, TimeSpan.Zero), "agy exited 1: not logged in");

        var decision = RunwayGate.Evaluate("agy", snapshot: null, new RunwayThresholds(), Now, attempt);

        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Equal("harvest attempted at 15:58 and failed: agy exited 1: not logged in", decision.Reason);
        Assert.DoesNotContain("no readable usage snapshot", decision.Reason);
    }

    /// <summary>
    /// A harvest that reported success and still left nothing readable — a persist that failed, or a
    /// snapshot written and immediately unreadable. It is named as a harvest failure rather than
    /// falling back to the never-harvested wording, because a harvest DID run.
    /// </summary>
    [Fact]
    public void A_harvest_that_reported_success_but_left_no_snapshot_holds_as_a_failed_attempt()
    {
        var attempt = new RunwayHarvestAttempt(
            new DateTimeOffset(2026, 9, 5, 15, 58, 0, TimeSpan.Zero), FailureReason: null);

        var decision = RunwayGate.Evaluate("agy", snapshot: null, new RunwayThresholds(), Now, attempt);

        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("harvest attempted at 15:58 and failed", decision.Reason);
    }

    /// <summary>
    /// A harvest attempt changes the WORDING of the missing-snapshot hold and nothing else: handed a
    /// readable snapshot under the thresholds, the gate admits whether or not one was made. Without
    /// this, the parameter could be silently gating admission and every arm above would still pass.
    /// </summary>
    [Fact]
    public void A_harvest_attempt_does_not_change_a_decision_taken_on_real_counters()
    {
        var attempt = new RunwayHarvestAttempt(Now, FailureReason: null);

        var decision = RunwayGate.Evaluate("agy", Agy("50%", "50%"), new RunwayThresholds(), Now, attempt);

        var withoutAttempt = Evaluate("agy", Agy("50%", "50%"));
        Assert.Equal(RunwayDisposition.Admit, decision.Disposition);
        Assert.Equal(withoutAttempt.Disposition, decision.Disposition);
        Assert.Equal(withoutAttempt.Reason, decision.Reason);
        Assert.Equal(withoutAttempt.HeadroomPoints, decision.HeadroomPoints);
    }

    /// <summary>
    /// #1923's population guard: the on-demand harvest must only spend a usage read where counters can
    /// decide. Codex joined this table when #1904 replaced its interim estimate with account counters.
    /// </summary>
    [Theory]
    [InlineData("claude", true)]
    [InlineData("agy", true)]
    [InlineData("codex", true)]
    [InlineData("fake", false)]
    public void Only_the_window_table_vendors_are_gated(string vendor, bool gated) =>
        Assert.Equal(gated, RunwayGate.IsGated(vendor));

    [Fact]
    public void A_snapshot_whose_output_parsed_no_windows_holds()
    {
        var unrecognizable = ClaudeUsageSlashCommandSource.Parse("Usage is unavailable right now.", Now);

        var decision = Evaluate("claude", unrecognizable);

        Assert.Empty(unrecognizable.Windows);
        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("not readable", decision.Reason);
    }

    [Fact]
    public void A_recognized_window_with_no_percentage_holds()
    {
        // agy's percent is derived from its own "Remaining" column; a non-numeric column leaves it
        // null rather than zero (the "unparsed -> unknown, never a number" ruling), and unknown holds.
        var snapshot = Agy(weeklyRemaining: "n/a", fiveHourRemaining: "80%");

        var decision = Evaluate("agy", snapshot);

        Assert.Null(snapshot.Windows.Single(w => w.Name.Contains("Weekly", StringComparison.Ordinal)).PercentUsed);
        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("no percentage", decision.Reason);
    }

    [Fact]
    public void A_snapshot_older_than_the_age_limit_holds_however_low_the_counters_are()
    {
        var stale = ClaudeUsageSlashCommandSource.Parse(
            "Current session: 0% used\nCurrent week (all models): 0% used\n", Now.AddHours(-7));

        var decision = RunwayGate.Evaluate("claude", stale, new RunwayThresholds(), Now);

        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("stale counter", decision.Reason);

        // Control: the identical snapshot inside the age limit admits, so the arm above is about age.
        Assert.Equal(RunwayDisposition.Admit, RunwayGate.Evaluate("claude", stale, new RunwayThresholds(), Now.AddHours(-5)).Disposition);
    }

    /// <summary>
    /// #1966 review: the stale Hold's third wording, which nothing asserted — the arm
    /// <c>RunwayGate.DescribeFailedHarvest</c>'s own doc comment describes and this file had no
    /// instrument for. The control is the second half: the identical stale snapshot with no attempt must
    /// NOT carry the suffix, or a gate that appended it unconditionally would pass on the first half.
    /// </summary>
    [Fact]
    public void A_stale_snapshot_whose_harvest_succeeded_and_is_still_stale_says_so()
    {
        var stale = ClaudeUsageSlashCommandSource.Parse(
            "Current session: 0% used\nCurrent week (all models): 0% used\n", Now.AddHours(-7));
        var attempt = new RunwayHarvestAttempt(
            new DateTimeOffset(2026, 9, 5, 15, 58, 0, TimeSpan.Zero), FailureReason: null);

        var decision = RunwayGate.Evaluate("claude", stale, new RunwayThresholds(), Now, attempt);

        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("stale counter", decision.Reason!, StringComparison.Ordinal);
        Assert.Contains(
            "harvest attempted at 15:58 and the snapshot it wrote is older than the limit too",
            decision.Reason!,
            StringComparison.Ordinal);
        Assert.DoesNotContain("and failed", decision.Reason!, StringComparison.Ordinal);

        var withoutAttempt = RunwayGate.Evaluate("claude", stale, new RunwayThresholds(), Now);
        Assert.DoesNotContain("older than the limit too", withoutAttempt.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1848 review: the tripwire under the staleness message's formatting — why it is not an integer
    /// cast is stated once, beside the format string in <see cref="RunwayGate.Evaluate"/>.
    /// </summary>
    [Fact]
    public void The_staleness_refusal_prints_the_fractional_age_rather_than_truncating_it()
    {
        var stale = ClaudeUsageSlashCommandSource.Parse(
            "Current session: 0% used\nCurrent week (all models): 0% used\n", Now.AddHours(-6.5));

        var decision = RunwayGate.Evaluate("claude", stale, new RunwayThresholds(), Now);

        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("6.5h old (limit 6h)", decision.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("6h old", decision.Reason, StringComparison.Ordinal);
    }

    // ---- unmeasured vendor -------------------------------------------------------------------------

    [Fact]
    public void A_vendor_outside_the_window_table_is_admitted_as_unmeasured()
    {
        var decision = Evaluate("gpt-hypothetical", snapshot: null);

        Assert.Equal(RunwayDisposition.Admit, decision.Disposition);
        Assert.Equal(RunwayGate.UnmeasuredReason, decision.Reason);
    }

    /// <summary>
    /// #1904's gate-side wiring: codex has a source, a snapshot file, and duration-based selectors.
    /// With no snapshot it therefore fails closed like the other measured vendors.
    /// </summary>
    [Fact]
    public void Codex_is_on_the_measured_list_and_holds_without_a_snapshot()
    {
        Assert.Contains("codex", RunwayGate.MeasuredVendors);

        var decision = Evaluate("codex", snapshot: null);

        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("no readable usage snapshot", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Synthetic measured-shape arm: a per-model bucket has both durations, while the account bucket
    /// has the observed weekly primary and null secondary. The per-model 5h window must not satisfy
    /// the account selector, and null must not become zero.
    /// </summary>
    [Fact]
    public void Codex_per_model_windows_do_not_fill_a_missing_account_window()
    {
        var snapshot = CodexUsageSource.Parse(
            new JsonObject
            {
                ["rateLimitsByLimitId"] = new JsonObject
                {
                    ["codex_model"] = new JsonObject
                    {
                        ["limitId"] = "codex_model",
                        ["primary"] = new JsonObject
                        {
                            ["usedPercent"] = 0,
                            ["windowDurationMins"] = CodexUsageSource.FiveHourDurationMins,
                        },
                        ["secondary"] = new JsonObject
                        {
                            ["usedPercent"] = 0,
                            ["windowDurationMins"] = CodexUsageSource.WeeklyDurationMins,
                        },
                    },
                    ["codex"] = new JsonObject
                    {
                        ["limitId"] = "codex",
                        ["primary"] = new JsonObject
                        {
                            ["usedPercent"] = 48,
                            ["windowDurationMins"] = CodexUsageSource.WeeklyDurationMins,
                        },
                        ["secondary"] = null,
                    },
                },
            },
            Now);

        var decision = RunwayGate.Evaluate("codex", snapshot, new RunwayThresholds(), Now);

        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("codex 300-minute", decision.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(
            decision.Counters,
            counter => counter.Window.Contains("codex_model", StringComparison.Ordinal));
    }

    [Fact]
    public void Codex_account_windows_are_gated_by_duration_when_primary_is_weekly()
    {
        Assert.Equal(
            RunwayDisposition.Admit,
            Evaluate("codex", Codex(weeklyPct: 84, sessionPct: 89)).Disposition);

        var weekHold = Evaluate("codex", Codex(weeklyPct: 85, sessionPct: 0));
        var sessionHold = Evaluate("codex", Codex(weeklyPct: 0, sessionPct: 90));

        Assert.Equal(RunwayDisposition.Hold, weekHold.Disposition);
        Assert.Contains("7d (primary)", weekHold.Reason, StringComparison.Ordinal);
        Assert.Equal(RunwayDisposition.Hold, sessionHold.Disposition);
        Assert.Contains("5h (secondary)", sessionHold.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_codex_snapshot_holds_however_low_the_vendor_counters_are()
    {
        var stale = Codex(weeklyPct: 0, sessionPct: 0, harvestedAt: Now.AddHours(-48));

        var decision = RunwayGate.Evaluate("codex", stale, new RunwayThresholds(), Now);

        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("stale counter", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Persisted interim snapshots remain deserializable, but provenance grants no admission
    /// exemption. Their old windows have no measured limit identity or duration and therefore hold
    /// unreadable until the real source replaces them.
    /// </summary>
    [Fact]
    public void An_interim_derived_codex_snapshot_fails_closed_until_reharvested()
    {
        var snapshot = new VendorUsageSnapshot(
            "codex",
            Now,
            null,
            [new VendorUsageWindow("rolling 5h (derived)", 1, null, "derived")],
            VendorUsageProvenance.Derived);

        var decision = RunwayGate.Evaluate("codex", snapshot, new RunwayThresholds(), Now);

        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("not readable", decision.Reason, StringComparison.Ordinal);
    }
}
