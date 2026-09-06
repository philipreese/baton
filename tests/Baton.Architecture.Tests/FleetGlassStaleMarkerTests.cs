using System.Text.RegularExpressions;

namespace Baton.Architecture.Tests;

/// <summary>
/// #1656 / #1549: <c>glass.html</c>'s per-room age line used to key the stale marker (⚠) on
/// journal-event age alone, false-firing on a healthy long-running lane. Full measurement and
/// rationale: spec/baton.md §6, "The false Running ⚠".
/// <para>
/// There is no JS test runner in this repo for <c>tools/fleet-glass/glass.html</c> (a pre-existing
/// gap, not one this fix introduces) -- these are source-shape checks in the same style
/// <see cref="FleetGlassReadOnlyTests"/> already uses, not a behavioral execution of the script.
/// </para>
/// </summary>
public class FleetGlassStaleMarkerTests
{
    private static string GlassSource()
    {
        var root = RepoRoot();
        var glassPath = Path.Combine(root, "tools", "fleet-glass", "glass.html");
        Assert.True(File.Exists(glassPath), "glass.html must exist at tools/fleet-glass/glass.html");
        return File.ReadAllText(glassPath);
    }

    [Fact]
    public void AgeLine_keys_the_stale_marker_on_live_activity_before_journal_age()
    {
        var html = GlassSource();

        var isStaleMatch = Regex.Match(
            html,
            @"const\s+isStale\s*=\s*room\.state\s*===\s*""Running""\s*&&\s*stalenessBasis\s*&&\s*\(Date\.now\(\)\s*-\s*Date\.parse\(stalenessBasis\)\)\s*>\s*15\s*\*\s*60000;");
        Assert.True(isStaleMatch.Success,
            "glass.html's ageLine must gate the ⚠ on a `stalenessBasis` variable compared against a 15-minute threshold.");

        var basisMatch = Regex.Match(
            html,
            @"const\s+liveActivityAt\s*=\s*room\.live\s*&&\s*room\.live\.lastActivityAt;\s*\n\s*const\s+stalenessBasis\s*=\s*liveActivityAt\s*\|\|\s*t;");
        Assert.True(basisMatch.Success,
            "glass.html must derive `stalenessBasis` from `room.live.lastActivityAt` first, falling back to the journal-event age (`t`) only when `live` is absent.");
    }

    [Fact]
    public void AgeLine_no_longer_keys_the_stale_marker_on_journal_age_alone()
    {
        var html = GlassSource();

        // The pre-#1656 shape: `t && (Date.now()-Date.parse(t)) > 15*60000` directly inside the
        // ageLine ternary, with no live-activity fallback at all.
        var preFixShape = Regex.IsMatch(
            html,
            @"room\.state\s*===\s*""Running""\s*&&\s*t\s*&&\s*\(Date\.now\(\)-Date\.parse\(t\)\)\s*>\s*15\*60000\s*\?\s*""\s*⚠""");
        Assert.False(preFixShape,
            "glass.html must not have regressed to keying the ⚠ on journal-event age (`t`) alone.");
    }

    /// <summary>
    /// #1981 (2026-09-06 round-3 review): the <c>projection</c> banner must stay ranked BELOW the two
    /// other <c>derived_at</c>-keyed rows of <c>glass.html</c>'s banner chain — row 8 (the #1829
    /// neutral "cadence has widened" line) and row 9 ("derivation may be stuck"). The projection arm
    /// (a) fires at 90s on exactly the state those two need at ten minutes, so any higher rung makes
    /// both of them dead code and re-promotes the #1829 false positive that was deliberately demoted.
    /// <para>
    /// This property has been lost three times — #1613, #1829, and once inside #1981 itself — and it
    /// is silent until an operator misreads a fault mid-incident, so it gets a check that runs and
    /// fails rather than only the precedence table at the chain's head (which stays as the WHY: which
    /// state reaches each row is not source-order and is not checkable here).
    /// </para>
    /// <para>
    /// Each anchor is asserted to occur exactly ONCE before its index is used: the precedence table
    /// spells out the same branch conditions in prose a few lines above the chain, so an anchor that
    /// matched the comment instead of the code would report the comment's index and go on passing
    /// after a real reorder. The <c>} else if(</c> prefix is what the table cannot contain.
    /// </para>
    /// </summary>
    [Fact]
    public void ProjectionBanner_ranks_below_the_two_derived_at_banners_it_would_otherwise_shadow()
    {
        var html = GlassSource();

        var projectionIndex = SoleIndexOf(html, @"\}\s*else\s+if\(projection\)\{", "the #1981 projection branch");
        var row8Index = SoleIndexOf(
            html,
            @"\}\s*else\s+if\(isFinite\(hbMs\)\s*&&\s*isFinite\(derivedMs\)\s*&&\s*hbMs\s*>\s*RUNNING_SUSPICION_MS",
            "row 8, the #1829 neutral heartbeat/derivation-aging-together branch");
        var row9Index = SoleIndexOf(
            html,
            @"\}\s*else\s+if\(running\s*&&\s*isFinite\(derivedMs\)\s*&&\s*derivedMs\s*>\s*RUNNING_SUSPICION_MS\)\{",
            "row 9, the \"derivation may be stuck\" branch");

        Assert.True(projectionIndex > row8Index,
            "glass.html's `projection` banner must be checked AFTER the #1829 neutral line (row 8) — "
            + "above it, arm (a)'s 90s threshold makes that row dead code.");
        Assert.True(projectionIndex > row9Index,
            "glass.html's `projection` banner must be checked AFTER the \"derivation may be stuck\" "
            + "line (row 9) — above it, arm (a)'s 90s threshold makes that row dead code.");
    }

    /// <summary>Index of the one and only match of <paramref name="pattern"/>. Fails when the branch is
    /// absent (renamed/deleted) or ambiguous (matched the precedence comment as well as the code) —
    /// either way an index read off it would not mean what the ordering assertion claims.</summary>
    private static int SoleIndexOf(string html, string pattern, string what)
    {
        var matches = Regex.Matches(html, pattern);
        Assert.True(matches.Count == 1,
            $"glass.html must contain exactly one occurrence of {what} (found {matches.Count}) — "
            + "the banner-ordering assertions read its source index.");
        return matches[0].Index;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Baton.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate the repo root (Baton.slnx) by walking up from " + AppContext.BaseDirectory);
    }
}
