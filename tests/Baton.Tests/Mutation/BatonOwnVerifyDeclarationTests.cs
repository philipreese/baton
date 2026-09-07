using System;
using System.IO;
using System.Linq;
using Baton.Mutation;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// #1958: pins Baton's OWN <c>.baton/verify</c> — the repo-level declaration that makes the engine's
/// post-exit verify run the receipted fast subset instead of a third full gates run. spec/baton.md
/// C-12's <c>#1958</c> paragraph is the record for why and for what it costs; this class only holds
/// the wiring to that decision, which is the half that can rot silently.
/// <para>
/// The two behaviours the decision actually rests on — a receipted tree running only the uncovered
/// members, and an unreceipted one running the whole fast set — are <c>gates.py</c>'s, and are
/// already proven by its own <c>--selftest</c> (the <c>--fast --skip-covered</c> arms, including the
/// no-<c>--skip-covered</c> polarity arm beside them). That selftest is an <c>OVERLAP</c> gate member,
/// so it runs on every gates run rather than sitting as a fixture nobody executes; restating either
/// arm here would be a second, drifting copy of a claim that already has a running check.
/// </para>
/// <para>
/// Read from the WORKING TREE, deliberately, via
/// <see cref="VerifyCommandResolver.ReadWorkingTreeRepoDeclaration"/> rather than through the
/// merge-base read the engine uses: the reviewed-tree read (spec/baton.md §3) resolves to whatever
/// <c>origin/main</c> holds, so a test built on it would be red on the branch that introduces this
/// file and green only after merge — a test whose verdict depends on the reviewer, not the code.
/// </para>
/// </summary>
public sealed class BatonOwnVerifyDeclarationTests
{
    /// <summary>
    /// The two flags that make the declared command the receipted fast subset rather than a full run.
    /// Both, not either: <c>--skip-covered</c> alone still runs <c>test-no-build</c>, and <c>--fast</c>
    /// alone re-runs every member the lane already receipted.
    /// </summary>
    private static readonly string[] RequiredFlags = ["--fast", "--skip-covered"];

    [Fact]
    public void Baton_declares_a_verify_command_that_is_a_pixi_task()
    {
        var declared = VerifyCommandResolver.ReadWorkingTreeRepoDeclaration(RepoRoot());

        Assert.False(
            string.IsNullOrWhiteSpace(declared),
            $"{VerifyCommandResolver.RepoDeclarationRelativePath} is missing or has no command line. It is the " +
            "one tracked file under .baton/ (.gitignore names it) and it is what stops the engine verify from " +
            "being a third full gates run over an already-gated tree -- spec/baton.md C-12, #1958.");

        Assert.StartsWith("pixi run ", declared, StringComparison.Ordinal);
    }

    [Fact]
    public void The_declared_pixi_task_exists_and_runs_the_receipted_fast_subset()
    {
        var declared = VerifyCommandResolver.ReadWorkingTreeRepoDeclaration(RepoRoot())!;
        var task = declared["pixi run ".Length..].Trim();

        var commandLine = PixiTaskCommand(task);
        Assert.False(
            commandLine is null,
            $"{VerifyCommandResolver.RepoDeclarationRelativePath} declares `pixi run {task}`, but pixi.toml declares " +
            "no such task. A renamed task would leave the engine verify failing to spawn rather than gating anything.");

        var missing = RequiredFlags.Where(flag => !commandLine!.Contains(flag, StringComparison.Ordinal)).ToArray();
        Assert.True(
            missing.Length == 0,
            $"pixi task `{task}` is `{commandLine}`, which is missing {string.Join(" and ", missing)}. " +
            "spec/baton.md C-12's #1958 paragraph states what the engine verify is supposed to run and what the " +
            "narrowing costs; a declaration that quietly widened back to the full set would make that paragraph a lie.");
    }

    /// <summary>
    /// The control arm. The same reader, pointed at the task this declaration REPLACED
    /// (<c>gates-quiet</c>, the <c>implement</c> role's baked-in default and a full run), must fail the
    /// flag check above — otherwise the assertion would pass on the very command #1958 exists to stop
    /// running, and would be measuring nothing.
    /// </summary>
    [Fact]
    public void The_full_gates_task_would_fail_the_same_check()
    {
        var full = PixiTaskCommand("gates-quiet");
        Assert.False(full is null, "pixi.toml no longer declares `gates-quiet`, so this control arm proves nothing.");

        Assert.DoesNotContain("--skip-covered", full!, StringComparison.Ordinal);
        Assert.DoesNotContain("--fast", full!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>cmd = "..."</c> of a <c>name = { cmd = "..." }</c> task line in pixi.toml's
    /// <c>[tasks]</c> table, or <see langword="null"/> when no line declares that name. Deliberately a
    /// line match rather than a TOML parse: every task this needs to read is written on one line in that
    /// file, and a miss reports "task absent", which fails the assertions above rather than passing them.
    /// </summary>
    private static string? PixiTaskCommand(string task)
    {
        var prefix = task + " = ";
        foreach (var raw in File.ReadLines(Path.Combine(RepoRoot(), "pixi.toml")))
        {
            var line = raw.Trim();
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..];
            }
        }

        return null;
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
