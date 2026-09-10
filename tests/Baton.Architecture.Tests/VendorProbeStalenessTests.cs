using Baton.VendorProbe;

namespace Baton.Architecture.Tests;

/// <summary>
/// #504's trigger: when a vendor CLI updates itself, the recorded capability findings stop being
/// about the CLI that is installed — and something has to notice.
/// </summary>
/// <remarks>
/// <para>
/// The probe itself drives live authenticated CLIs and spends real subscription usage, so it is
/// requires an authenticated host and prior spend disclosure (docs/agents/developing-baton.md) and must never run unattended. But the trigger for
/// running it does not have to be expensive. <c>--version</c> starts no session and burns no quota,
/// so this test asks the only cheap question that matters: <em>is what is installed still the thing
/// the findings were established against?</em>
/// </para>
/// <para>
/// record-once-ok: #1853 tools/Baton.VendorProbe/Staleness.cs
/// <b>Why this lives in the local test suite rather than in CI.</b> No CI runner has an
/// authenticated vendor CLIs on PATH, so a CI job would find the vendors absent and
/// go green forever — a pass that means only "the vendors were never here". That green would be
/// worse than no check at all, because it looks like coverage. The machine that can answer the
/// question is the operator's, which is also the only machine where the CLIs self-update, so the
/// check belongs in the suite that runs there. It skips where it cannot know, which is the same
/// discipline the probe enforces on its own findings: absence of a surface to look at is never a
/// finding of absence.
/// </para>
/// </remarks>
public class VendorProbeStalenessTests
{
    private static IReadOnlyList<string> Vendors => Program.SupportedVendors;

    /// <summary>
    /// #1487: drift is no longer an immediate hard-fail. The verdict — grace-window pass, or a
    /// hard-fail past it — comes from <see cref="DriftGrace.Evaluate"/>, the same call
    /// <c>Program.Check</c> (wired into <c>gates</c> as the <c>vendor-check</c> task) makes, so the
    /// two can never disagree about today's verdict. This test only consumes
    /// <see cref="DriftGrace.Result.Fatal"/> — printing the WARN loudly is <c>vendor-check</c>'s job,
    /// not this test's: a passing xunit test's <c>ITestOutputHelper</c> output does not surface
    /// through `gates` (dotnet test only prints output for a test that fails), so a test-layer WARN
    /// would be invisible on the fresh-drift path this exists to make loud.
    /// </summary>
    [Fact]
    public void RecordedVendorFindingsAreAboutTheCliThatIsInstalled()
    {
        var lockPath = Path.Combine(RepositoryRoot(), Staleness.DefaultLockPath);
        var driftPath = Path.Combine(RepositoryRoot(), DriftGrace.DefaultBookkeepingPath);
        var statuses = Staleness.Check(lockPath, Vendors);

        var inspectable = statuses.Where(s => s.Verdict != Staleness.Verdict.Uninspectable).ToList();
        if (inspectable.Count == 0)
        {
            Assert.Skip(
                "No vendor CLI is on PATH, so this machine cannot tell whether the recorded findings "
                + "still hold. Skipped rather than passed — a green here would only mean the vendors "
                + "were never present, which is exactly the false negative the probe suite exists to "
                + "stop us reporting as fact.");
        }

        var stale = inspectable
            .Where(s => s.Verdict is Staleness.Verdict.Drifted or Staleness.Verdict.NeverProbed)
            .ToList();

        var grace = DriftGrace.Evaluate(driftPath, stale.Count > 0, DateTimeOffset.Now, statuses);

        Assert.True(
            !grace.Fatal,
            $"""
            {grace.Message}

            {string.Join("\n\n", stale.Select(s => s.Explain()))}

            The findings in docs/vendor-capabilities.md are attributed to specific vendor versions.
            When a CLI moves, those rows are unverified — not disproven, unverified — and the only
            way to restore them is to look again:

                pixi run vendor-probe

            That run spends real subscription usage and takes a couple of minutes, which is precisely
            why it is triggered by this check rather than run on a schedule.
            """);
    }

    /// <summary>
    /// The lock file is what makes the cheap check possible, so its absence is a real gap rather
    /// than a fresh-clone inconvenience.
    /// </summary>
    [Fact]
    public void AProbeRunHasBeenRecorded()
    {
        var lockPath = Path.Combine(RepositoryRoot(), Staleness.DefaultLockPath);

        Assert.True(
            File.Exists(lockPath),
            $"""
            {Staleness.DefaultLockPath} is missing, so nothing can be compared against and the
            staleness check above degrades to silence on every machine.

            Run `pixi run vendor-probe` on a machine with the vendor CLIs authenticated and commit
            the result.
            """);
    }

    private static string RepositoryRoot()
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

        throw new InvalidOperationException("Could not locate the repository root (no Baton.slnx found) from " + AppContext.BaseDirectory);
    }
}
