namespace Baton.Architecture.Tests;

/// <summary>
/// #2030's wiring half. <c>Baton.Tests.Core.StandardHandleInheritanceTests</c> proves the
/// mechanism — an inherited handle keeps a wrapper shell's redirected stream from ever reaching EOF,
/// and clearing the inherit flag ends it. This asserts the far cheaper, far more deletable thing:
/// that the CLI actually calls it, on the verbs that run a lane, before anything is spawned.
/// </summary>
/// <remarks>
/// A source scan, the shape <see cref="VendorSpawnGateTests"/> already uses, and with the same kind
/// of false negative: it reads text, so a call that compiles to nothing (commented out at a later
/// line, wrapped in a condition that is never true) still satisfies it. What it does buy is that
/// deleting the call, or quietly widening the guard to the <c>watch</c> verb, fails the build rather
/// than waiting for another lane to wedge for an hour.
/// </remarks>
public class StandardHandleInheritanceWiringTests
{
    private const string EntryPoint = "src/Baton.Cli/Program.cs";
    private const string Call = "StandardHandleInheritance.Disable()";

    [Fact]
    public void The_cli_clears_its_own_standard_handle_inheritance_on_the_lane_running_verbs()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepoRoot(), EntryPoint));

        int callLine = Array.FindIndex(lines, line => line.Contains(Call, StringComparison.Ordinal));
        Assert.True(
            callLine >= 0,
            $"{EntryPoint} no longer calls {Call}. Without it every child of `baton dispatch` inherits a "
            + "duplicate of the lane's redirected stdout, and one straggler holds the wrapper shell open "
            + "until something kills it (#2030).");

        // The guard immediately above it, so the verb set cannot drift silently in either direction.
        int guardLine = Array.FindLastIndex(
            lines, callLine, line => line.Contains("args[0] is", StringComparison.Ordinal));
        Assert.True(guardLine >= 0, $"the {Call} call in {EntryPoint} has no verb guard above it");

        string guard = lines[guardLine];
        Assert.Contains("\"dispatch\"", guard, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"watch\"",
            guard,
            StringComparison.Ordinal);
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
