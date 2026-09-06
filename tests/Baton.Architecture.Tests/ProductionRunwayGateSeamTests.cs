namespace Baton.Architecture.Tests;

/// <summary>
/// #1848 review: <b>production dispatch must use the real runway evaluator, and the real reservation
/// policy.</b> <c>DispatchCommand.ExecuteAsync</c>'s <c>evaluateRunway</c> and (since #1896)
/// <c>reservationPolicy</c> parameters are public optional seams that exist so a test can drive the
/// gate's arms without a harvested snapshot or a cost ledger; four suites in <c>Baton.Cli.Tests</c> pass
/// their own — <c>DispatchAuditedWorktreeAcceptanceTests</c>, <c>DispatchContinueEndToEndTests</c>,
/// <c>RunwayHoldDispatchTests</c> and <c>RunwayReservationDispatchTests</c>. Nothing else made it
/// impossible for a production call site to pass one too — and either seam, silently admitting every
/// dispatch, is indistinguishable from a working gate until the month's usage bill says otherwise. This
/// is what makes that a build failure instead of a review's job.
/// </summary>
/// <remarks>
/// <para>
/// Pure file reading over <c>src/</c>, no project references, matching <see cref="VendorSpawnGateTests"/>
/// and <see cref="ReferenceDirectionTests"/>. The allowlist is empty today and is meant to stay that
/// way: a production caller that genuinely needs its own evaluator is a change to the gate's contract
/// (spec/baton.md §7, "Runway hold (#1848)"), not a line added here in passing.
/// </para>
/// <para>
/// <b>Its false negatives, named rather than left for someone to discover</b> — the same disclosure
/// <see cref="VendorSpawnGateTests"/> makes about its own scan:
/// </para>
/// <list type="number">
/// <item>A POSITIONAL argument — <c>ExecuteAsync(options, adapters, token, workspace, evaluator, policy)</c>
/// — carries neither seam's name and is invisible here. Every call site in the tree names the
/// argument; this scan cannot enforce that it keeps doing so.</item>
/// <item>An evaluator or a policy reaching the call through a variable declared elsewhere in the file, or
/// through a <c>DispatchOptions</c>-shaped indirection, is out of the statement this reads.</item>
/// <item>It says nothing about what the production evaluator or policy DOES. That
/// <c>CreateDiskRunwayEvaluator</c> reads the operator's thresholds and the harvested snapshot is
/// pinned by <c>Baton.Vendors.Tests.RunwayGateTests</c> and
/// <c>Baton.Vendors.Tests.RunwayHoldSettingsTests</c>; that the settings key selects the shipped policy is
/// pinned by <c>RunwayHoldSettingsTests</c>'s own end-to-end arm — not by a file scan.</item>
/// <item><see cref="ApprovedEvaluatorOverrides"/> is keyed by FILE, not by (file, seam): an entry would
/// allowlist that file for both seams at once. It is empty, and splitting the key is the change to make
/// before the first entry is ever added rather than after.</item>
/// </list>
/// </remarks>
public class ProductionRunwayGateSeamTests
{
    /// <summary>
    /// Production call sites permitted to pass their own <c>evaluateRunway</c> or <c>reservationPolicy</c>,
    /// with why. Empty by design — see the remarks above before adding one, including what the file-only
    /// key means.
    /// </summary>
    private static readonly Dictionary<string, string> ApprovedEvaluatorOverrides = new()
    {
        ["src/Baton.Cli/Daemon/QueueLauncher.cs"] = "#1934 slice 1, and a contract change made first in spec/baton.md §13, not a line added in passing. It does NOT substitute an evaluator: it awaits DispatchCommand.CreateDiskRunwayEvaluatorAsync — the same production evaluator a null seam would have built — and passes a wrapper that calls it, returns its verdict unchanged, and only REMEMBERS whether any vendor came back IsHold. The gate's thresholds, snapshot and decision are the production ones in full; nothing is admitted that a null seam would have held. The observation is load-bearing for the queue: DispatchCommand raises CliArgumentException for a hold AND for a drain marker, a missing spec, an unknown role and a non-git workspace, so a scheduler branching on the exception type would leave a permanently-broken item retrying every gap forever with 'runway-held' falsely recorded as the reason. QueueLauncher's own remarks state the same thing at the call site.",
    };

    private const string CallMarker = "DispatchCommand.ExecuteAsync(";
    private const string EvaluatorSeamMarker = "evaluateRunway";
    private const string PolicySeamMarker = "reservationPolicy";

    [Fact]
    public void No_production_call_site_supplies_its_own_runway_evaluator() =>
        AssertNoProductionCallSitePasses(
            EvaluatorSeamMarker,
            "dispatch reads the operator's thresholds and the harvested snapshot");

    /// <summary>
    /// #1896's second seam, in the same shape and for the same reason (#1932 review): a production call
    /// site passing <c>reservationPolicy: new NoReservationRunwayReservationPolicy()</c> would leave every
    /// recorded row well-formed and the whole reservation arm dead, with nothing red anywhere.
    /// </summary>
    [Fact]
    public void No_production_call_site_supplies_its_own_reservation_policy() =>
        AssertNoProductionCallSitePasses(
            PolicySeamMarker,
            "dispatch runs the policy named in the operator's settings.json");

    private static void AssertNoProductionCallSitePasses(string seamMarker, string whatProductionGets)
    {
        var root = RepoRoot();
        var callSites = ProductionCallSites(root).ToList();

        // The scan has to have found something, or a renamed method makes this test vacuously green
        // forever. Program.cs is the one production dispatch entry point; if it stops being one, that is
        // a real change and this assertion is where it gets noticed.
        Assert.Contains(callSites, site => site.File == "src/Baton.Cli/Program.cs");

        var offenders = callSites
            .Where(site => site.Statement.Contains(seamMarker, StringComparison.Ordinal))
            .Where(site => !ApprovedEvaluatorOverrides.ContainsKey(site.File))
            .Select(site => $"{site.File}:{site.Line}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"A production call to DispatchCommand.ExecuteAsync passes its own '{seamMarker}':\n  "
            + string.Join("\n  ", offenders)
            + $"\n\nThat seam exists for tests. Production must leave it null so {whatProductionGets} "
            + "(spec/baton.md §7, \"Runway hold (#1848)\"). If a production caller really needs its own, "
            + "that is a spec change first — then add it to ApprovedEvaluatorOverrides with the reason.");
    }

    /// <summary>Every <c>DispatchCommand.ExecuteAsync(</c> invocation in <c>src/</c>, as the text from
    /// the call up to the statement's terminating <c>;</c> — enough to see its arguments and no more.</summary>
    private static IEnumerable<(string File, int Line, string Statement)> ProductionCallSites(string root)
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            var text = File.ReadAllText(path);
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            for (var index = text.IndexOf(CallMarker, StringComparison.Ordinal);
                 index >= 0;
                 index = text.IndexOf(CallMarker, index + CallMarker.Length, StringComparison.Ordinal))
            {
                var end = text.IndexOf(';', index);
                var statement = end < 0 ? text[index..] : text[index..end];
                var line = text.Take(index).Count(c => c == '\n') + 1;
                yield return (relative, line, statement);
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pixi.toml")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
