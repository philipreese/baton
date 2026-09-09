using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.VendorProbe;

/// <summary>
/// Re-runnable probe of what each vendor CLI can actually do (#504).
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/vendor-capabilities.md</c> has always said "re-run the probes before trusting this after a
/// vendor update — the CLIs self-update." There were no probes to re-run: they were ad-hoc shell,
/// written once and thrown away. This is them, and the point is that a negative result now has to
/// carry the list of surfaces it was established on.
/// </para>
/// <para>
/// Never runs in CI. It drives live authenticated CLIs, which is permanently a human action item
/// (CLAUDE.md). The goal is that one command produces a trustworthy matrix, not that a robot does it
/// nightly.
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>
    /// Every subscription-backed CLI covered by the probe and its free version-drift check.
    /// Kept public so the local architecture tripwire cannot silently omit a newly added vendor.
    /// </summary>
    public static IReadOnlyList<string> SupportedVendors { get; } = ["claude", "agy", "codex"];

    /// <summary>
    /// No byte-order mark. These outputs are read by other tools, and a BOM makes an otherwise valid
    /// JSON document fail to parse in several of them.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static int Main(string[] args)
    {
        var writeTo = Arg(args, "--out");
        var only = Arg(args, "--vendor");
        var lockPath = Arg(args, "--lock") ?? Staleness.DefaultLockPath;
        var driftPath = Arg(args, "--drift-lock") ?? DriftGrace.DefaultBookkeepingPath;

        if (only is not null && !SupportedVendors.Contains(only, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"unknown vendor '{only}'. Expected one of: {string.Join(", ", SupportedVendors)}");
            return 2;
        }

        IReadOnlyList<string> vendors = only is null ? SupportedVendors : [only.ToLowerInvariant()];

        if (args.Contains("--check"))
        {
            return Check(lockPath, driftPath, vendors);
        }

        var findings = new List<Finding>();

        foreach (var vendor in vendors)
        {
            Console.WriteLine($"probing {vendor} …");
            var installed = Cli.IsInstalled(vendor);
            if (!installed)
            {
                Console.WriteLine($"  {vendor} is not installed or not on PATH — recording that, not an absence of capabilities.");
            }

            foreach (var f in Probes.RunAll(vendor))
            {
                findings.Add(f);
                var mark = f.Evidence switch
                {
                    Evidence.Observed => "observed ",
                    Evidence.Inspected => "inspected",
                    _ => "NOT FOUND",
                };
                Console.WriteLine($"  [{mark}] {f.Capability}: {f.Value ?? "—"}");
                if (f.Evidence == Evidence.NotFound)
                {
                    Console.WriteLine($"              looked at: {string.Join(", ", f.SurfacesConsulted)}");
                }
            }
        }

        // #647: a narrowed run must not drop the vendor it did not look at. The lock file already
        // merged, so before this the free staleness check went on reporting the other vendor as
        // current while the evidence matrix no longer mentioned it.
        var carriedFrom = writeTo is not null ? Previous(writeTo) : [];
        var published = ProbeMerge.Carry(carriedFrom, findings, vendors);
        var carriedCount = published.Count - findings.Count;

        var json = JsonSerializer.Serialize(
            new ProbeRun(DateTimeOffset.Now, Environment.OSVersion.VersionString, published),
            new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });

        if (writeTo is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(writeTo))!);
            File.WriteAllText(writeTo, json, Utf8NoBom);
            var md = Path.ChangeExtension(writeTo, ".md");
            File.WriteAllText(md, Matrix(published, vendors), Utf8NoBom);
            Console.WriteLine($"\nwrote {writeTo}\nwrote {md}");
            if (carriedCount > 0)
            {
                Console.WriteLine(
                    $"carried {carriedCount} finding(s) forward for vendor(s) this run did not probe — "
                    + "their rows keep the version they were established against");
            }
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine(Matrix(published, vendors));
        }

        // Recorded whether or not --out was given: the versions these findings were established
        // against are what makes the free staleness check possible later.
        Staleness.Write(lockPath, findings, driftPath);
        Console.WriteLine($"recorded probed versions in {lockPath}");

        // Counts this run's own findings, never the published total — the published file may carry
        // rows from an earlier run, and a single number covering both would read as "12 established
        // today" on a run that established six.
        var negatives = findings.Count(f => f.Evidence == Evidence.NotFound);
        var scope = carriedCount > 0 ? $" (published file also holds {carriedCount} carried)" : "";
        Console.WriteLine(
            $"\n{findings.Count} findings established this run · {negatives} negative, each carrying "
            + $"the surfaces it was established on{scope}.");
        return 0;
    }

    /// <summary>
    /// The free half. Spends no usage, so it can run in the ordinary dev loop — which is the point:
    /// the expensive probe should be triggered by a vendor moving, not by a calendar or by someone
    /// remembering.
    /// </summary>
    private static int Check(string lockPath, string driftPath, IReadOnlyList<string> vendors)
    {
        var statuses = Staleness.Check(lockPath, vendors);

        foreach (var s in statuses)
        {
            var mark = s.Verdict switch
            {
                Staleness.Verdict.Current => "ok     ",
                Staleness.Verdict.Drifted => "STALE  ",
                Staleness.Verdict.NeverProbed => "UNPROBED",
                _ => "unknown",
            };
            Console.WriteLine($"[{mark}] {s.Explain()}");
        }

        var inspectable = statuses.Where(s => s.Verdict != Staleness.Verdict.Uninspectable).ToList();
        var needsProbe = inspectable.Where(s =>
            s.Verdict is Staleness.Verdict.Drifted or Staleness.Verdict.NeverProbed).ToList();

        // #1487: drift becomes deliberate. A vendor moving is no longer an immediate hard-fail —
        // DriftGrace records when it was first seen and only fails once it has sat past the grace
        // window. This is the layer that can print, so the WARN below is what makes drift visible in
        // `gates` output; the xunit tripwire (VendorProbeStalenessTests) shares this Evaluate call —
        // see its doc comment for why the WARN prints here and not there.
        //
        // Gated on `inspectable.Count > 0`, same as the xunit test's own Assert.Skip: a run where
        // NOTHING was inspectable (both vendors absent from PATH, or Cli.Version transiently failed
        // on one mid-update) must never touch the bookkeeping file at all. Calling Evaluate(false, …)
        // there would read as "confirmed clean" and delete a real, in-progress drift clock — turning
        // a flaky --version into an unlimited grace window that never actually expires.
        if (inspectable.Count > 0)
        {
            var grace = DriftGrace.Evaluate(driftPath, needsProbe.Count > 0, DateTimeOffset.Now, statuses);

            if (grace.Verdict == DriftGrace.Verdict.FreshWarn)
            {
                Console.WriteLine();
                Console.WriteLine($"WARN VENDOR-DRIFT: {grace.Message}");
                return 0;
            }

            if (grace.Fatal)
            {
                Console.WriteLine();
                Console.WriteLine($"FAIL VENDOR-DRIFT: {grace.Message}");
                return 1;
            }
        }

        if (statuses.All(s => s.Verdict == Staleness.Verdict.Uninspectable))
        {
            // Deliberately exit 0 while saying plainly that nothing was established. A machine with
            // no vendor CLIs cannot fail this check honestly, and it must not pass it silently
            // either — that combination is what makes CI the wrong place to run this at all.
            Console.WriteLine();
            Console.WriteLine(
                "No vendor CLI was inspectable on this machine, so nothing was verified. "
                + "This exit code means 'not applicable here', never 'up to date'.");
        }

        return 0;
    }

    private sealed record ProbeRun(DateTimeOffset RanAt, string Host, IReadOnlyList<Finding> Findings);

    /// <summary>
    /// The findings already on disk at <paramref name="writeTo"/>, or empty when there is no usable
    /// prior file.
    /// </summary>
    /// <remarks>
    /// Empty on a missing or unreadable file rather than throwing: a first run has nothing to carry,
    /// and a corrupt one must not block a probe the operator has already paid for. It says so on
    /// stderr — silently carrying nothing is how a merge stops merging without anyone noticing,
    /// which is the same shape as the truncation this exists to fix.
    /// </remarks>
    private static IReadOnlyList<Finding> Previous(string writeTo)
    {
        if (!File.Exists(writeTo))
        {
            return [];
        }

        try
        {
            var prior = JsonSerializer.Deserialize<ProbeRun>(
                File.ReadAllText(writeTo),
                new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
            return prior?.Findings ?? [];
        }
        catch (JsonException e)
        {
            Console.Error.WriteLine(
                $"warning: {writeTo} could not be read as a prior probe run ({e.Message}), so nothing "
                + "is carried forward. A narrowed run will publish only the vendor it probed.");
            return [];
        }
    }

    /// <summary>
    /// The matrix, generated. Kept close to <c>docs/vendor-capabilities.md</c>'s shape so the doc can
    /// be gate-checked against a real run rather than hand-maintained beside one.
    /// </summary>
    /// <param name="probedThisRun">
    /// The vendors this run actually probed (#647). Every other column is carried from a previous
    /// run and is marked as such — without the mark, a merged matrix is indistinguishable from a
    /// fully re-probed one, and a reader would take a carried row as freshly established.
    /// </param>
    private static string Matrix(IReadOnlyList<Finding> findings, IReadOnlyCollection<string> probedThisRun)
    {
        var vendors = findings.Select(f => f.Vendor).Distinct().ToList();
        var caps = findings.Select(f => f.Capability).Distinct().ToList();
        var sb = new StringBuilder();

        sb.AppendLine("| | " + string.Join(" | ", vendors.Select(v =>
        {
            var version = findings.First(f => f.Vendor == v).VendorVersion;
            var carried = probedThisRun.Contains(v) ? "" : " *(carried, not re-probed)*";
            return $"`{v}` {version ?? "(not installed)"}{carried}";
        })) + " |");
        sb.AppendLine("|---|" + string.Concat(vendors.Select(_ => "---|")));

        foreach (var cap in caps)
        {
            var cells = vendors.Select(v =>
                findings.FirstOrDefault(f => f.Vendor == v && f.Capability == cap)?.Rendered() ?? "—");
            sb.AppendLine($"| {cap} | {string.Join(" | ", cells)} |");
        }

        sb.AppendLine();
        sb.AppendLine("Every cell above is one of three things, and the difference matters: **observed** (a run");
        sb.AppendLine("demonstrated it), *inspected* (read from help or the binary, never executed), or **not found");
        sb.AppendLine("on** an explicit list of surfaces. A bare \"absent\" is not expressible — that is the whole");
        sb.AppendLine("point, because every wrong row this suite was built after was a negative from one surface.");
        sb.AppendLine();

        foreach (var f in findings.Where(f => f.Evidence != Evidence.Observed))
        {
            sb.AppendLine($"- **{f.Vendor} · {f.Capability}** — {f.Detail}");
        }

        return sb.ToString();
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
