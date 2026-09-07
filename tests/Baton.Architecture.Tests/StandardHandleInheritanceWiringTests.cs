namespace Baton.Architecture.Tests;

/// <summary>
/// #2030's wiring half. <c>Baton.Tests.Core.StandardHandleInheritanceTests</c> proves the
/// mechanism — an inherited handle keeps a wrapper shell's redirected stream from ever reaching EOF,
/// and clearing the inherit flag ends it. This asserts the far cheaper, far more deletable thing:
/// that the CLI actually calls it, on the verbs that run a lane, before anything is spawned.
/// </summary>
/// <remarks>
/// <para>
/// A source scan, the shape <see cref="VendorSpawnGateTests"/> already uses, and with the same kind
/// of false negative: it reads text, so a call that compiles to nothing (commented out at a later
/// line, wrapped in a condition that is never true) still satisfies it. What it does buy is that
/// deleting the call, moving it below the command handlers, or quietly widening the verb set to
/// <c>watch</c> fails the build rather than waiting for another lane to wedge for an hour.
/// </para>
/// <para>
/// <b>What "before the first spawn" is measured as, and what it excludes.</b> <c>Program.cs</c> never
/// spawns anything itself; every lane verb's handler runs inside the one top-level <c>try</c> block,
/// so "the call precedes that <c>try</c>" is the honest positional expression of "before this process
/// starts a child". It says nothing about the vendor-subprocess verbs that return ABOVE the guard —
/// <c>hook-check</c>, <c>agy-hook-check</c>, <c>codex-broker</c>, <c>mcp</c>, <c>daemon</c> — which
/// are outside the lane-verb set on purpose and are not covered by any assertion here.
/// </para>
/// </remarks>
public class StandardHandleInheritanceWiringTests
{
    private const string EntryPoint = "src/Baton.Cli/Program.cs";
    private const string Call = "StandardHandleInheritance.Disable()";

    // The named set the guard reads (#2030 review, LOW): before it existed the verb tuple was written
    // out at four call sites, and this test could only ever read the one it happened to anchor on.
    // Pointing at the definition is what makes "the verb set cannot drift" true rather than
    // "the copy next to the call cannot drift".
    private const string LaneVerbPredicate = "static bool IsLaneVerb(string verb)";

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

        // The guard immediately above it, reading the named set rather than a copy of the tuple.
        int guardLine = Array.FindLastIndex(
            lines, callLine, line => line.Contains("IsLaneVerb(", StringComparison.Ordinal));
        Assert.True(
            guardLine >= 0,
            $"the {Call} call in {EntryPoint} has no IsLaneVerb guard above it. If the guard was rewritten "
            + "as a literal verb tuple, put it back on the named set -- that is the only thing keeping this "
            + "test's verb assertions below pointed at the set the rest of the file actually branches on.");

        // The set itself, so a fifth lane verb cannot be added without this test seeing it, and so a
        // guard reformatted across two lines cannot evade the check by moving the literals.
        int definitionLine = Array.FindIndex(
            lines, line => line.Contains(LaneVerbPredicate, StringComparison.Ordinal));
        Assert.True(
            definitionLine >= 0,
            $"{EntryPoint} no longer defines `{LaneVerbPredicate}`, so nothing in this file states once "
            + "which verbs run a lane (#2030 review, record-once).");

        string definition = lines[definitionLine];
        Assert.Contains("\"dispatch\"", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("\"watch\"", definition, StringComparison.Ordinal);
    }

    [Fact]
    public void The_clear_runs_before_the_command_handlers_that_spawn()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepoRoot(), EntryPoint));

        int callLine = Array.FindIndex(lines, line => line.Contains(Call, StringComparison.Ordinal));
        Assert.True(callLine >= 0, $"{EntryPoint} no longer calls {Call} (#2030).");

        // Every lane verb's handler runs inside this block -- see the class remarks for why that is
        // the positional stand-in for "before the first spawn", and for what it does not cover.
        int handlerBlockLine = Array.FindIndex(
            lines, line => line.TrimEnd().Equals("try", StringComparison.Ordinal));
        Assert.True(
            handlerBlockLine >= 0,
            $"{EntryPoint} no longer has a top-level `try` block wrapping its command handlers, so this "
            + "test can no longer locate the point after which a spawn becomes possible. Re-anchor it "
            + "rather than deleting it (#2030).");

        Assert.True(
            callLine < handlerBlockLine,
            $"{Call} is at {EntryPoint}:{callLine + 1}, at or after the command-handler block at line "
            + $"{handlerBlockLine + 1}. Clearing HANDLE_FLAG_INHERIT only helps children spawned AFTER the "
            + "clear: .NET duplicates every inheritable handle in this process's table into each child at "
            + "spawn time, so a child started first keeps its duplicate for its whole life and holds the "
            + "wrapper shell's redirected stream open exactly as before the fix (#2030).");
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
