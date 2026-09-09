using System.Text.Json;

namespace Baton.VendorProbe;

/// <summary>
/// #1487's fix: a vendor CLI self-updating used to hard-fail every local <c>gates</c> run the moment
/// <see cref="Staleness"/> saw drift — correct pressure, wrong tempo (the motivating incident is in
/// <c>docs/runbooks/vendor-probe.md</c>). This turns that into a grace window: drift is recorded the
/// instant it is first seen, warned about loudly, and only hard-fails once it has sat unaddressed
/// past <see cref="Window"/>.
/// </summary>
/// <remarks>
/// The bookkeeping file is machine-local and gitignored (<c>docs/vendor-probe.drift.local.json</c>,
/// beside the tracked <see cref="Staleness.DefaultLockPath"/> lock file it deliberately is not); the
/// runbook explains why the clock is a machine-local fact. <b>Fails closed on broken bookkeeping,
/// never on fresh drift:</b> a missing file on first drift is the normal case and starts the clock,
/// but a <em>present but unreadable</em> file hard-fails until a human clears it rather than reading
/// as "no clock recorded" — which would silently reopen the grace window forever. This is the
/// opposite polarity from <see cref="Staleness.Read"/>, where an unreadable lock reads as "nothing
/// recorded" — safe only because that check's failure mode is the loud one being escaped here.
/// </remarks>
public static class DriftGrace
{
    public const string DefaultBookkeepingPath = "docs/vendor-probe.drift.local.json";

    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public enum Verdict
    {
        /// <summary>Nothing has drifted. Ordinary green.</summary>
        NoDrift,

        /// <summary>Drift is within the grace window. Passes, but says so loudly.</summary>
        FreshWarn,

        /// <summary>Drift has outlived the grace window.</summary>
        StaleFail,

        /// <summary>The bookkeeping file exists but could not be read. Fails closed.</summary>
        CorruptFail,
    }

    public sealed record Bookkeeping(DateTimeOffset FirstDetectedAt);

    public sealed record Result(Verdict Verdict, string Message)
    {
        public bool Fatal => Verdict is Verdict.StaleFail or Verdict.CorruptFail;
    }

    /// <summary>
    /// One call, consumed by both the loud checker layer (<c>vendor-check</c>, which prints
    /// <see cref="Result.Message"/> so it lands in <c>gates</c> output) and the xunit tripwire
    /// (which only needs <see cref="Result.Fatal"/>) — so the two never disagree about what today's
    /// verdict is.
    /// </summary>
    /// <remarks>
    /// #2123: If <paramref name="bookkeepingPath"/> carries a <see cref="Bookkeeping.FirstDetectedAt"/>
    /// that predates the lock's newest <see cref="Staleness.Status.RecordedAt"/> for a vendor that is now
    /// <see cref="Staleness.Verdict.Current"/> (<paramref name="newestOkRecordedAt"/> or derived from
    /// <paramref name="statuses"/>), that previous drift cleared when the probe re-pinned (even if no
    /// check ran in the interim to delete the file). It is treated as cleared and a fresh grace window
    /// starts for the newly detected drift.
    /// </remarks>
    public static Result Evaluate(
        string bookkeepingPath,
        bool driftDetected,
        DateTimeOffset now,
        IReadOnlyList<Staleness.Status>? statuses = null,
        DateTimeOffset? newestOkRecordedAt = null)
    {
        if (newestOkRecordedAt is null && statuses is not null)
        {
            var okRecordedAts = statuses
                .Where(s => s.Verdict == Staleness.Verdict.Current && s.RecordedAt.HasValue)
                .Select(s => s.RecordedAt!.Value)
                .ToList();
            if (okRecordedAts.Count > 0)
            {
                newestOkRecordedAt = okRecordedAts.Max();
            }
        }

        if (!driftDetected)
        {
            if (File.Exists(bookkeepingPath))
            {
                try
                {
                    File.Delete(bookkeepingPath);
                }
                catch (IOException ex)
                {
                    // Fail closed, same as an unreadable file: a clock this run could not actually
                    // clear must not be reported as cleared.
                    return new Result(
                        Verdict.CorruptFail,
                        $"{bookkeepingPath} records cleared drift but could not be deleted ({ex.Message}). "
                        + "Failing closed rather than reporting a clock as cleared when it was not.");
                }

                return new Result(
                    Verdict.NoDrift,
                    "no vendor CLI is drifted; a previously recorded drift instant was cleared");
            }

            return new Result(Verdict.NoDrift, "no vendor CLI is drifted");
        }

        Bookkeeping recorded;
        if (File.Exists(bookkeepingPath))
        {
            Bookkeeping? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<Bookkeeping>(File.ReadAllText(bookkeepingPath), Json);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return new Result(
                    Verdict.CorruptFail,
                    $"{bookkeepingPath} exists but cannot be read ({ex.Message}). Failing closed rather than "
                    + "treating broken bookkeeping as if no drift had ever been recorded — that would quietly "
                    + "reopen a fresh 7-day window every run. Delete the file (or restore it from a backup) "
                    + "and re-run.");
            }

            if (parsed is null)
            {
                return new Result(
                    Verdict.CorruptFail,
                    $"{bookkeepingPath} exists but deserialized to nothing. Failing closed — delete the file "
                    + "and re-run.");
            }

            // #2123: an earlier drift was recorded before a vendor was re-pinned and confirmed ok.
            // That drift cleared when the re-pin happened, even if vendor-check did not run in the interim.
            // Treat it as cleared and start a fresh window for this newly detected drift.
            if (newestOkRecordedAt.HasValue && parsed.FirstDetectedAt < newestOkRecordedAt.Value)
            {
                recorded = new Bookkeeping(now);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(bookkeepingPath))!);
                File.WriteAllText(bookkeepingPath, JsonSerializer.Serialize(recorded, Json));
            }
            else
            {
                recorded = parsed;
            }
        }
        else
        {
            recorded = new Bookkeeping(now);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(bookkeepingPath))!);
            File.WriteAllText(bookkeepingPath, JsonSerializer.Serialize(recorded, Json));
        }

        var age = now - recorded.FirstDetectedAt;
        if (age > Window)
        {
            return new Result(
                Verdict.StaleFail,
                $"vendor CLI drift was first detected {recorded.FirstDetectedAt:yyyy-MM-dd} "
                + $"({age.TotalDays:F1} days ago), past the {Window.TotalDays:F0}-day grace window. Run "
                + "`pixi run vendor-probe` and commit the refreshed pins.");
        }

        return new Result(
            Verdict.FreshWarn,
            $"vendor CLI drift was first detected {recorded.FirstDetectedAt:yyyy-MM-dd} "
            + $"({age.TotalDays:F1} days ago), within the {Window.TotalDays:F0}-day grace window "
            + $"({(Window - age).TotalDays:F1} day(s) left). This is a deliberate pass, not a clean bill of "
            + "health — `pixi run vendor-probe` is owed.");
    }
}
