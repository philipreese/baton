using Baton.Cli;
using Baton.Cli.Daemon;

namespace Baton.Cli.Tests;

/// <summary>
/// #1901 C1 items 1 and 3: the settle-time probe that turns a room's bindings into the issue, PR and
/// diff shape its ledger rows carry. Every spawn goes through the injected
/// <see cref="WorkspaceDeliveryProbe.CommandRunner"/>, so nothing here runs <c>git</c>, installs
/// <c>gh</c>, or touches the network — which is also the point: the production path is one seam away
/// from these fixtures rather than a different code path.
/// </summary>
public sealed class WorkspaceDeliveryProbeTests
{
    private const string BindingsJson = """
        {
          "implement": {
            "Adapter": "claude",
            "PromptTemplate": "p",
            "Timeout": "00:30:00",
            "WorkingDirectory": "%WORKSPACE%",
            "Contract": { "DeclaredOutputs": [] }
          }
        }
        """;

    [Fact]
    public async Task A_branch_named_after_an_issue_records_the_issue_the_pr_and_the_diff_shape()
    {
        var (room, workspace) = NewRoomWithWorkspace();
        try
        {
            var delivery = await WorkspaceDeliveryProbe.ReadForRoomAsync(
                room,
                Canned(
                    branch: "1901-lane",
                    prListJson: """[{"number":1907}]""",
                    numstat: "10\t2\tsrc/Baton/Accounting/CostLedgerEntry.cs\n40\t1\ttests/Baton.Tests/Accounting/CostLedgerStoreTests.cs\n-\t-\tdocs/screenshot.png\n"),
                TestContext.Current.CancellationToken);

            var row = Assert.Single(delivery).Value;
            Assert.Equal("1901", row.Issue);
            Assert.Equal("1907", row.PullRequest);
            Assert.Equal(3, row.FilesChanged);

            // The binary row contributes a FILE and no line counts -- 10+40, not 10+40+0-with-a-throw.
            Assert.Equal(50, row.Additions);
            Assert.Equal(3, row.Deletions);
            Assert.Equal(1, row.TestFilesChanged);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// The control arm for the test above: the same room and the same runner shape, with every spawn
    /// failing the way an unpushed branch, a missing <c>gh</c> and an offline host actually fail. If
    /// this returned facts, the arm above would be measuring the fixture rather than the probe.
    /// </summary>
    [Fact]
    public async Task A_workspace_that_pushed_nothing_and_a_gh_that_cannot_answer_leave_every_fact_absent()
    {
        var (room, workspace) = NewRoomWithWorkspace();
        try
        {
            var delivery = await WorkspaceDeliveryProbe.ReadForRoomAsync(
                room,
                (program, _, args, _) => Task.FromResult(
                    program == "git" && args.Contains("rev-parse")
                        ? new GhCliResult(Started: true, 0, "lane-with-no-issue\n", string.Empty)
                        // `git diff origin/main...HEAD` on a repo that never fetched origin/main exits
                        // non-zero; `gh` missing from PATH never starts at all.
                        : program == "git"
                            ? new GhCliResult(Started: true, 128, string.Empty, "fatal: bad revision")
                            : new GhCliResult(Started: false, -1, string.Empty, "gh was not found on PATH.")),
                TestContext.Current.CancellationToken);

            var row = Assert.Single(delivery).Value;
            Assert.Null(row.Issue);
            Assert.Null(row.PullRequest);
            Assert.Null(row.FilesChanged);
            Assert.Null(row.Additions);
            Assert.Null(row.Deletions);
            Assert.Null(row.TestFilesChanged);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// A detached HEAD answers <c>rev-parse --abbrev-ref</c> with the literal <c>HEAD</c>, which names
    /// no branch — so there is no issue to derive, nothing to ask <c>gh</c> about, and no base to diff
    /// against. Absent rather than a diff measured against whatever HEAD happened to be.
    /// </summary>
    [Fact]
    public async Task A_detached_head_yields_nothing_rather_than_a_diff_against_an_unnamed_ref()
    {
        var (room, workspace) = NewRoomWithWorkspace();
        try
        {
            var delivery = await WorkspaceDeliveryProbe.ReadForRoomAsync(
                room,
                Canned(branch: "HEAD", prListJson: """[{"number":1907}]""", numstat: "9\t9\tsrc/x.cs\n"),
                TestContext.Current.CancellationToken);

            var row = Assert.Single(delivery).Value;
            Assert.Null(row.Issue);
            Assert.Null(row.PullRequest);
            Assert.Null(row.FilesChanged);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// The #669 worktree-teardown case (<c>Program.cs</c> runs that teardown BEFORE the cost-ledger
    /// append, so a delivered lane can reach here with nothing left on disk): skipped entirely rather
    /// than probed against a path that no longer exists. The spawn counter is what discriminates —
    /// without it, a probe that ran git anyway and swallowed the failure would look identical.
    /// </summary>
    [Fact]
    public async Task A_workspace_that_no_longer_exists_is_skipped_entirely()
    {
        var (room, workspace) = NewRoomWithWorkspace();
        DirectoryCleanup.DeleteRecursively(workspace);
        try
        {
            var spawned = 0;
            var delivery = await WorkspaceDeliveryProbe.ReadForRoomAsync(
                room,
                (_, _, _, _) =>
                {
                    spawned++;
                    return Task.FromResult(new GhCliResult(true, 0, "1901-lane\n", string.Empty));
                },
                TestContext.Current.CancellationToken);

            Assert.Empty(delivery);
            Assert.Equal(0, spawned);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    /// <summary>A room with no bindings file at all resolves to nothing, never an exception — the fail-open floor.</summary>
    [Fact]
    public async Task A_room_with_no_bindings_file_resolves_to_nothing()
    {
        var room = Path.Combine(Path.GetTempPath(), $"delivery-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(room);
        try
        {
            Assert.Empty(await WorkspaceDeliveryProbe.ReadForRoomAsync(
                room,
                (_, _, _, _) => throw new InvalidOperationException("must not spawn"),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Theory]
    [InlineData("1901-lane", "1901")]
    [InlineData("1901-populate-issue-pr", "1901")]
    [InlineData("main", null)]
    [InlineData("1901", null)]
    [InlineData("feature-1901", null)]
    [InlineData("-1901", null)]
    public void An_issue_is_read_only_from_a_leading_number_followed_by_a_separator(string branch, string? expected) =>
        Assert.Equal(expected, WorkspaceDeliveryProbe.TryReadIssueNumber(branch));

    private static WorkspaceDeliveryProbe.CommandRunner Canned(string branch, string prListJson, string numstat) =>
        (program, _, args, _) => Task.FromResult(
            program == "gh"
                ? new GhCliResult(Started: true, 0, prListJson, string.Empty)
                : args.Contains("rev-parse")
                    ? new GhCliResult(Started: true, 0, branch + "\n", string.Empty)
                    : new GhCliResult(Started: true, 0, numstat, string.Empty));

    /// <summary>A room directory holding a bindings file that points at a real (empty) workspace directory.</summary>
    private static (string Room, string Workspace) NewRoomWithWorkspace()
    {
        var room = Path.Combine(Path.GetTempPath(), $"delivery-probe-{Guid.NewGuid():N}");
        var workspace = Path.Combine(Path.GetTempPath(), $"delivery-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(room);
        Directory.CreateDirectory(workspace);
        File.WriteAllText(
            Path.Combine(room, "bindings.json"),
            BindingsJson.Replace("%WORKSPACE%", workspace.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal));
        return (room, workspace);
    }
}
