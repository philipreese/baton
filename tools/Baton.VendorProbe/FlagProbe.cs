namespace Baton.VendorProbe;

/// <summary>
/// Establishing whether a CLI <em>accepts</em> a flag, rather than whether its help mentions one.
/// </summary>
/// <remarks>
/// <para>
/// The naive test — pass the flag, check the exit code — is worthless on its own, because a zero exit
/// could mean "accepted" or "unknown flags are ignored". The technique is to establish the CLI's own
/// rejection behaviour first, using a flag that certainly does not exist, and then compare.
/// </para>
/// <para>
/// This is not hypothetical. <c>docs/vendor-capabilities.md</c> recorded
/// <c>--permission-prompt-tool</c> as <b>absent on both vendors</b>, from help text alone, and
/// decision 0015's entire mechanism inverted to a blocking MCP tool on that premise. Measured with a
/// control: <c>claude --definitely-not-a-real-flag-xyz</c> exits 1 with
/// <c>error: unknown option</c>, while <c>claude --permission-prompt-tool noop</c> exits 0 and runs
/// the turn normally. The flag is accepted; it was undocumented in <c>--help</c> through 2.1.258 (see docs/vendor-capabilities.md#probe-2026-09-08).
/// </para>
/// </remarks>
public static class FlagProbe
{
    private const string ControlFlag = "--definitely-not-a-real-flag-xyz";

    public sealed record Behaviour(bool RejectsUnknownFlags, int ControlExitCode, string ControlMessage);

    /// <summary>How this CLI reacts to a flag it has never heard of. Run once per vendor.</summary>
    public static Behaviour Baseline(string vendor)
    {
        var run = Cli.Invoke(vendor, [ControlFlag, "-p", "hi"], TimeSpan.FromSeconds(90));
        var message = FirstLine(run.All);
        // A CLI that rejects unknown flags gives us a usable signal; one that silently ignores them
        // does not, and this probe must say so rather than guess.
        var rejects = run.ExitCode != 0;
        return new Behaviour(rejects, run.ExitCode, message);
    }

    /// <summary>
    /// Whether <paramref name="flag"/> is accepted, judged against the CLI's own rejection behaviour.
    /// </summary>
    public static (bool? Accepted, string Detail) IsAccepted(
        string vendor, Behaviour baseline, string flag, params string[] valueThenPrompt)
    {
        if (!baseline.RejectsUnknownFlags)
        {
            return (null,
                $"Cannot tell: `{vendor}` exits {baseline.ControlExitCode} even for a flag that does not exist "
                + $"(\"{baseline.ControlMessage}\"), so an exit code carries no information about `{flag}`. "
                + "Establish this another way before recording anything.");
        }

        var run = Cli.Invoke(vendor, [flag, .. valueThenPrompt], TimeSpan.FromMinutes(2));
        var message = FirstLine(run.All);

        if (run.ExitCode == 0)
        {
            return (true,
                $"Accepted. `{vendor}` rejects unknown flags (control exits {baseline.ControlExitCode}: "
                + $"\"{baseline.ControlMessage}\"), but `{flag}` exits 0 and the turn runs. "
                + "Undocumented in `--help` is not the same as absent.");
        }

        return (false,
            $"Rejected: exit {run.ExitCode}, \"{message}\". The control flag exits "
            + $"{baseline.ControlExitCode}, so this CLI does discriminate — the rejection is real.");
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "(no output)";
}
