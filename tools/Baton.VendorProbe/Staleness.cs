using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.VendorProbe;

/// <summary>
/// The cheap half of the suite: has a vendor CLI moved since the findings were recorded?
/// </summary>
/// <remarks>
/// <para>
/// The probe spends real subscription usage; its execution policy lives at
/// record-once-ok: #2204 docs/agents/developing-baton.md
/// <c>docs/agents/developing-baton.md#live-vendor-smoke-tests</c>. The question "are these findings
/// still about the CLI that is installed?" costs nothing to ask, because
/// <c>--version</c> is a local string that starts no session and burns no quota. So the free check
/// gates the expensive one: the probe records the versions it ran against, and this compares them
/// against what is installed now.
/// </para>
/// <para>
/// <b>CI cannot answer this question, and pretending otherwise would be the trap.</b> No runner has
/// authenticated vendor CLIs on PATH, so a CI job would see the vendors absent
/// and report "nothing has changed" forever — a green check that means only that the vendors were
/// never there. That is precisely the shape of the false negative this whole suite was built after.
/// A check with no vendor to inspect therefore reports <see cref="Verdict.Uninspectable"/>, which is
/// not a pass.
/// </para>
/// </remarks>
public static class Staleness
{
    public const string DefaultLockPath = "docs/vendor-probe.lock.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public enum Verdict
    {
        /// <summary>Installed version matches what the findings were recorded against.</summary>
        Current,

        /// <summary>The CLI moved. Every finding for this vendor is now unverified.</summary>
        Drifted,

        /// <summary>Installed here, but never probed on any machine.</summary>
        NeverProbed,

        /// <summary>Not on PATH, so this machine cannot say anything either way.</summary>
        Uninspectable,
    }

    /// <param name="Vendor">The CLI.</param>
    /// <param name="RecordedVersion">The version the findings were established against.</param>
    /// <param name="InstalledVersion">What is on PATH right now, or null if nothing is.</param>
    /// <param name="RecordedAt">When the probe that produced those findings ran.</param>
    public sealed record Lock(string Vendor, string RecordedVersion, DateTimeOffset RecordedAt);

    public sealed record LockFile(DateTimeOffset WrittenAt, IReadOnlyList<Lock> Vendors);

    public sealed record Status(string Vendor, Verdict Verdict, string? RecordedVersion, string? InstalledVersion, DateTimeOffset? RecordedAt)
    {
        public string Explain() => Verdict switch
        {
            Verdict.Current =>
                $"{Vendor} {InstalledVersion} — findings recorded against this exact version on "
                + $"{RecordedAt:yyyy-MM-dd}.",

            Verdict.Drifted =>
                $"{Vendor} has moved: findings were recorded against {RecordedVersion} on "
                + $"{RecordedAt:yyyy-MM-dd}, but {InstalledVersion} is installed. Every row for this "
                + "vendor is now unverified — not wrong, unverified. Re-run `pixi run vendor-probe`.",

            Verdict.NeverProbed =>
                $"{Vendor} {InstalledVersion} is installed but appears in no probe run. "
                + "Run `pixi run vendor-probe` to record what it can actually do.",

            _ =>
                $"{Vendor} is not on PATH here, so this machine cannot tell whether the recorded "
                + $"findings ({RecordedVersion ?? "none"}) still hold. This is not a pass.",
        };
    }

    /// <summary>
    /// Records the versions a probe run was established against, merging into whatever is already
    /// recorded.
    /// </summary>
    /// <remarks>
    /// Merging rather than replacing, because <c>--vendor claude</c> is a normal thing to run and a
    /// wholesale write would silently drop every other vendor's entry — turning them from "recorded"
    /// into "never probed" without anyone touching them. A partial run is partial evidence; it
    /// updates what it saw and leaves the rest alone.
    /// </remarks>
    /// <param name="path">The lock file path.</param>
    /// <param name="findings">The findings established in this probe run.</param>
    /// <param name="driftPath">
    /// Optional path to the drift bookkeeping file. Clears only the vendors re-pinned by these
    /// findings (#2123), preserving every other vendor's grace clock.
    /// </param>
    public static void Write(string path, IReadOnlyList<Finding> findings, string? driftPath = null)
    {
        var probed = findings
            .Where(f => f.VendorVersion is not null)
            .GroupBy(f => f.Vendor)
            .ToDictionary(g => g.Key, g => new Lock(g.Key, g.First().VendorVersion!, DateTimeOffset.Now));

        var vendors = Read(path)
            .Where(existing => !probed.ContainsKey(existing.Vendor))
            .Concat(probed.Values)
            .OrderBy(v => v.Vendor, StringComparer.Ordinal)
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new LockFile(DateTimeOffset.Now, vendors), Json));

        if (!string.IsNullOrEmpty(driftPath) && probed.Count > 0)
        {
            try
            {
                DriftGrace.ClearRepinned(driftPath, probed.Keys);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // The lock is already durable; Evaluate can reconcile each surviving clock against
                // its own vendor's RecordedAt. Broken bookkeeping still fails closed there.
                Console.Error.WriteLine($"Could not clear re-pinned vendor drift in {driftPath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Compares installed versions against recorded ones. Runs <c>--version</c> only — no session,
    /// no tokens, no quota.
    /// </summary>
    public static IReadOnlyList<Status> Check(string path, IReadOnlyList<string> vendors)
    {
        var recorded = Read(path);
        var results = new List<Status>();

        foreach (var vendor in vendors)
        {
            var known = recorded.FirstOrDefault(v => v.Vendor == vendor);
            var installed = Cli.Version(Probes.ProgramName(vendor));

            var verdict = (known, installed) switch
            {
                (null, null) => Verdict.Uninspectable,
                (null, not null) => Verdict.NeverProbed,
                (not null, null) => Verdict.Uninspectable,
                var (k, i) when string.Equals(k.RecordedVersion, i, StringComparison.Ordinal) => Verdict.Current,
                _ => Verdict.Drifted,
            };

            results.Add(new Status(vendor, verdict, known?.RecordedVersion, installed, known?.RecordedAt));
        }

        return results;
    }

    private static IReadOnlyList<Lock> Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        // A malformed lock file must read as "nothing recorded" rather than take the check down —
        // but never silently: the caller reports NeverProbed, which asks for a probe run.
        var file = JsonSerializer.Deserialize<LockFile>(File.ReadAllText(path), Json);
        return file?.Vendors ?? [];
    }
}
