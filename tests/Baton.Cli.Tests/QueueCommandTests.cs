using Baton.Queue;
using Baton.Status;
using Xunit;

namespace Baton.Cli.Tests;

/// <summary>
/// The two <c>baton queue</c> verbs whose ORDER of operations is the behaviour (#1939 review): what
/// <c>add</c> is allowed to have touched by the time it refuses, and whether <c>list</c> answers the
/// question spec/baton.md §13 sends a reader to it with.
/// </summary>
/// <remarks>
/// Isolated the same way <c>QueueSchedulerServiceTests</c> is, and for the reason stated there.
/// </remarks>
public sealed class QueueCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CreateTempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_queue_cmd_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        return home;
    }

    [Fact]
    public async Task Re_adding_a_launched_tag_is_refused_before_the_spec_copy_is_overwritten()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "w1");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);

            // What the running lane was queued with.
            await File.WriteAllTextAsync(BatonPaths.QueueSpecFile("t1"), "the brief the lane is running", Ct);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items =
                    [
                        new QueueItem
                        {
                            Tag = "t1",
                            Role = "implement",
                            Workspace = workspace,
                            SpecFile = BatonPaths.QueueSpecFile("t1"),
                            State = QueueItemState.Launched,
                            RoomDirectory = Path.Combine(home, "rooms", "queue-t1-abcd"),
                        },
                    ],
                },
                Ct);

            var newBrief = Path.Combine(home, "new-brief.md");
            await File.WriteAllTextAsync(newBrief, "a different brief entirely", Ct);

            await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "t1", Role: "implement", SpecFilePath: newBrief,
                    WorkspaceDirectory: workspace),
                TextWriter.Null,
                Ct));

            // The refusal's own reason is that the running lane's record would be overwritten, so the
            // copy must not already have happened by the time it is raised.
            Assert.Equal(
                "the brief the lane is running",
                await File.ReadAllTextAsync(BatonPaths.QueueSpecFile("t1"), Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Re_adding_a_work_item_past_implement_is_refused_before_its_brief_is_overwritten()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // A work item between rounds is QUEUED, not launched, so slice 1's launched-tag refusal
            // does not cover it — and a `--lifecycle` tag defaults to `<n>-lane`, so re-typing the same
            // add is an ordinary thing to do. Without the refusal this resets fix round 2 to implement
            // round 0 and overwrites the brief carrying the reviewer's findings.
            var workspace = Path.Combine(home, "w1934");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            await File.WriteAllTextAsync(BatonPaths.QueueSpecFile("1934-lane"), "the fix brief with the findings", Ct);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items =
                    [
                        new QueueItem
                        {
                            Tag = "1934-lane",
                            Role = "implement",
                            Workspace = workspace,
                            SpecFile = BatonPaths.QueueSpecFile("1934-lane"),
                            Issue = 1934,
                            Branch = "1934-lane",
                            Stage = WorkStage.Fix,
                            Round = 2,
                            State = QueueItemState.Queued,
                        },
                    ],
                },
                Ct);

            var newBrief = Path.Combine(home, "new-brief.md");
            await File.WriteAllTextAsync(newBrief, "an implement brief", Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "1934-lane", Role: "implement", SpecFilePath: newBrief,
                    WorkspaceDirectory: workspace),
                TextWriter.Null,
                Ct));

            Assert.Contains("stage 'fix'", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(
                "the fix brief with the findings",
                await File.ReadAllTextAsync(BatonPaths.QueueSpecFile("1934-lane"), Ct));

            var item = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single();
            Assert.Equal(WorkStage.Fix, item.Stage);
            Assert.Equal(2, item.Round);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_work_item_still_at_implement_is_replaceable_like_any_other_queued_item()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // The polarity control for the refusal above: the same re-add, one stage earlier, must
            // still go through — there is no round history to lose at implement, and refusing here
            // would make correcting a just-queued item impossible.
            var workspace = Path.Combine(home, "w1934");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            await File.WriteAllTextAsync(BatonPaths.QueueSpecFile("1934-lane"), "the first brief", Ct);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items =
                    [
                        new QueueItem
                        {
                            Tag = "1934-lane",
                            Role = "implement",
                            Workspace = workspace,
                            SpecFile = BatonPaths.QueueSpecFile("1934-lane"),
                            Issue = 1934,
                            Stage = WorkStage.Implement,
                            State = QueueItemState.Queued,
                        },
                    ],
                },
                Ct);

            var newBrief = Path.Combine(home, "new-brief.md");
            await File.WriteAllTextAsync(newBrief, "a corrected brief", Ct);

            var exit = await QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "1934-lane", Role: "implement", SpecFilePath: newBrief,
                    WorkspaceDirectory: workspace),
                TextWriter.Null,
                Ct);

            Assert.Equal(0, exit);
            Assert.Equal("a corrected brief", await File.ReadAllTextAsync(BatonPaths.QueueSpecFile("1934-lane"), Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Re_adding_a_queued_tag_still_replaces_it_and_its_spec()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // The control for the refusal above: a QUEUED tag is the operator editing their list, and
            // that path must still copy. Without this, moving the copy later could have disabled it.
            var workspace = Path.Combine(home, "w1");
            Directory.CreateDirectory(workspace);
            var brief = Path.Combine(home, "brief.md");
            await File.WriteAllTextAsync(brief, "the first brief", Ct);

            var options = new QueueOptions(
                QueueVerb.Add, Tag: "t1", Role: "implement", SpecFilePath: brief, WorkspaceDirectory: workspace);
            Assert.Equal(0, await QueueCommand.ExecuteAsync(options, TextWriter.Null, Ct));

            await File.WriteAllTextAsync(brief, "the rewritten brief", Ct);
            Assert.Equal(0, await QueueCommand.ExecuteAsync(options, TextWriter.Null, Ct));

            Assert.Equal("the rewritten brief", await File.ReadAllTextAsync(BatonPaths.QueueSpecFile("t1"), Ct));
            Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task List_prints_the_wait_the_ledger_last_recorded()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var at = new DateTimeOffset(2026, 9, 6, 1, 30, 0, TimeSpan.Zero);
            await QueueDecisionLedgerStore.AppendAsync(
                new QueueDecisionEntry(at, "t1", QueueDecisionEntry.Waited, "memory", 2.0, 1.4, 2.0),
                previousVerdictKey: null, BatonPaths.QueueDecisionLedgerFile, Ct);

            var output = new StringWriter();
            await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List), output, Ct);

            var printed = output.ToString();
            Assert.Contains("Waiting on memory", printed, StringComparison.Ordinal);
            Assert.Contains("1.4 GiB against a 2 GiB floor", printed, StringComparison.Ordinal);
            Assert.Contains("candidate 't1'", printed, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    /// <summary>
    /// The two tokens the line is suppressed for, and why — <c>QueueCommand.PrintWaitAsync</c>'s own
    /// remarks. Both are things this listing already says, one of them on the line directly above.
    /// </summary>
    [Theory]
    [InlineData(QueueWaitReason.NoItems)]
    [InlineData(QueueWaitReason.Hold)]
    public async Task List_suppresses_the_wait_line_for_a_reason_the_listing_already_states(QueueWaitReason reason)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueDecisionLedgerStore.AppendAsync(
                new QueueDecisionEntry(
                    new DateTimeOffset(2026, 9, 6, 1, 30, 0, TimeSpan.Zero), null, QueueDecisionEntry.Waited,
                    QueueWaitReasons.Token(reason), 0, 6.4, 2.0),
                previousVerdictKey: null, BatonPaths.QueueDecisionLedgerFile, Ct);
            if (reason == QueueWaitReason.Hold)
            {
                await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Held = true }, Ct);
            }

            var output = new StringWriter();
            await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List), output, Ct);

            Assert.DoesNotContain("Waiting on", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task List_says_nothing_about_waiting_when_the_last_decision_was_a_launch()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // The discriminating half: the line reports the CURRENT verdict, so a stale wait from
            // before a launch must not be printed as if the queue were still waiting on it.
            var at = new DateTimeOffset(2026, 9, 6, 1, 30, 0, TimeSpan.Zero);
            var key = await QueueDecisionLedgerStore.AppendAsync(
                new QueueDecisionEntry(at, "t1", QueueDecisionEntry.Waited, "memory", 2.0, 1.4, 2.0),
                previousVerdictKey: null, BatonPaths.QueueDecisionLedgerFile, Ct);
            await QueueDecisionLedgerStore.AppendAsync(
                new QueueDecisionEntry(at.AddMinutes(1), "t1", QueueDecisionEntry.Launched, null, 2.0, 3.4, 2.0),
                key, BatonPaths.QueueDecisionLedgerFile, Ct);

            var output = new StringWriter();
            await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List), output, Ct);

            Assert.DoesNotContain("Waiting on", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }
}
