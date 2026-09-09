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

    /// <summary>
    /// Vendors owns each clock. FirstDetectedAt is their oldest instant for existing file readers.
    /// A legacy file without Vendors conservatively assigns its instant to every supported vendor
    /// until that vendor is confirmed Current or re-pinned; a partial run cannot infer its owner.
    /// </summary>
    public sealed record Bookkeeping(
        DateTimeOffset FirstDetectedAt,
        IReadOnlyDictionary<string, DateTimeOffset>? Vendors = null);

    public sealed record Result(Verdict Verdict, string Message)
    {
        public bool Fatal => Verdict is Verdict.StaleFail or Verdict.CorruptFail;
    }

    /// <summary>Clears only the clocks covered by a successfully recorded probe run.</summary>
    internal static void ClearRepinned(string path, IEnumerable<string> vendors)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var clocks = ReadClocks(path);
        var changed = false;
        foreach (var vendor in vendors)
        {
            changed |= clocks.Remove(vendor);
        }

        if (changed)
        {
            WriteClocks(path, clocks);
        }
    }

    private static Dictionary<string, DateTimeOffset> ReadClocks(string path)
    {
        var recorded = JsonSerializer.Deserialize<Bookkeeping>(File.ReadAllText(path), Json);
        if (recorded is null || recorded.FirstDetectedAt == default
            || recorded.Vendors is { Count: 0 }
            || recorded.Vendors?.Any(v => string.IsNullOrWhiteSpace(v.Key) || v.Value == default) == true)
        {
            throw new JsonException("Missing or invalid drift instants.");
        }

        return recorded.Vendors is null
            ? Program.SupportedVendors.ToDictionary(v => v, _ => recorded.FirstDetectedAt, StringComparer.Ordinal)
            : new Dictionary<string, DateTimeOffset>(recorded.Vendors, StringComparer.Ordinal);
    }

    private static void WriteClocks(string path, Dictionary<string, DateTimeOffset> clocks)
    {
        if (clocks.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new Bookkeeping(clocks.Values.Min(), clocks), Json));
    }

    /// <summary>
    /// Shared verdict for vendor-check and its architecture tripwire. Only the supplied statuses
    /// can clear or restart clocks; an omitted or Uninspectable vendor retains its recorded instant.
    /// Without statuses, driftDetected describes all supported vendors (the legacy calling form).
    /// </summary>
    /// <remarks>
    /// #2123: a newer RecordedAt clears only that vendor's older drift. This also recovers when
    /// the probe wrote its lock but could not update bookkeeping, even if the same vendor has
    /// already drifted again before the next check.
    /// </remarks>
    public static Result Evaluate(
        string bookkeepingPath,
        bool driftDetected,
        DateTimeOffset now,
        IReadOnlyList<Staleness.Status>? statuses = null)
    {
        statuses ??= Program.SupportedVendors.Select(v => new Staleness.Status(
            v, driftDetected ? Staleness.Verdict.Drifted : Staleness.Verdict.Current,
            null, null, null)).ToList();

        var active = new List<DateTimeOffset>();
        try
        {
            var clocks = File.Exists(bookkeepingPath)
                ? ReadClocks(bookkeepingPath)
                : new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            var changed = false;
            foreach (var status in statuses)
            {
                if (status.Verdict == Staleness.Verdict.Current)
                {
                    changed |= clocks.Remove(status.Vendor);
                }
                else if (status.Verdict is Staleness.Verdict.Drifted or Staleness.Verdict.NeverProbed)
                {
                    if (!clocks.TryGetValue(status.Vendor, out var firstDetectedAt)
                        || status.RecordedAt > firstDetectedAt)
                    {
                        firstDetectedAt = now;
                        clocks[status.Vendor] = firstDetectedAt;
                        changed = true;
                    }

                    active.Add(firstDetectedAt);
                }
            }

            if (changed)
            {
                WriteClocks(bookkeepingPath, clocks);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Result(
                Verdict.CorruptFail,
                $"{bookkeepingPath} could not be read or updated ({ex.Message}). Failing closed rather "
                + "than reopening the grace window. Restore the bookkeeping or delete the file and re-run.");
        }

        if (active.Count == 0)
        {
            return new Result(Verdict.NoDrift, "no checked vendor CLI is drifted; unchecked clocks are retained");
        }

        var recorded = new Bookkeeping(active.Min());
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
