namespace Baton.Vendors.Tests;

/// <summary>
/// #2079: the on-disk fixture package <c>tools/skill-probe/probe.py</c> dispatches. Its measurement is
/// what #2074's "reachable without acting, PROVEN" floor was decided on, so the package has to stay
/// loadable and lint-clean or a re-run measures a refusal instead of an activation — a failure that is
/// otherwise invisible until someone spends six live vendor dispatches finding it.
/// </summary>
/// <remarks>
/// The description coupling is asserted rather than assumed. The probe's brief asks for a workspace
/// orientation note and never names the marker file; a marker can only appear if the model judged the
/// skill relevant, which it can only do from the description. A manifest whose description drifted away
/// from the brief's task would turn a null result into an artifact of the fixture rather than evidence
/// about projection — so the two words the coupling rests on are pinned here.
/// </remarks>
public sealed class MarkerProbeFixtureTests
{
    private static string FixtureDirectory =>
        Path.Combine(RepoRoot(), "tools", "skill-probe", "skills", "marker-probe");

    [Fact]
    public void The_marker_probe_fixture_loads_and_passes_the_format_lint()
    {
        var package = SkillPackageReader.LoadPackage(FixtureDirectory);

        Assert.Equal("marker-probe", package.Name);
        Assert.Null(SkillPackageLint.Check(package));
    }

    [Fact]
    public void The_marker_probe_fixture_body_asks_for_the_marker_and_its_description_matches_the_brief()
    {
        var package = SkillPackageReader.LoadPackage(FixtureDirectory);

        Assert.Contains("PROBE-ACTIVATED.txt", package.Content, StringComparison.Ordinal);
        // The brief in probe.py asks for a "workspace orientation note"; see this class's remarks.
        Assert.Contains("workspace orientation note", package.Description, StringComparison.Ordinal);
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
