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

    [Theory]
    [InlineData("agy", null, "gemini-3.8-flash-high")]
    [InlineData("claude", null, "role default model")]
    [InlineData("codex", null, "role default model")]
    [InlineData("claude", "claude-opus-4-8", "claude-opus-4-8")]
    [InlineData("claude", "sonnet[1m]", "sonnet[1m]")]
    [InlineData("agy", "future-agy-model", "future-agy-model")]
    [InlineData("claude", "claude-sonnet-4-6", "claude-sonnet-4-6")]
    [InlineData("agy", "claude-sonnet-4-6", "claude-sonnet-4-6")]
    public async Task Add_honours_an_unscoped_adapters_own_model_rules(
        string adapter, string? model, string displayedModel)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var brief = Path.Combine(home, "brief.md");
            await File.WriteAllTextAsync(brief, "implement this", Ct);
            var output = new StringWriter();

            var exit = await QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Add, Tag: "explicit", Role: "implement",
                    SpecFilePath: brief, WorkspaceDirectory: home, Adapter: adapter, Model: model),
                output, Ct);

            Assert.Equal(0, exit);
            Assert.Contains($"tier: {adapter} / {displayedModel} / role default effort",
                output.ToString(), StringComparison.Ordinal);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(adapter, item.Adapter);
            Assert.Equal(model, item.Model);
            Assert.Null(item.Effort);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData(null, "claude-sonnet-4-6", "multiple candidate adapters: claude, agy; specify --adapter")]
    [InlineData("codex", "opus", "absent from the recorded Codex capability snapshot")]
    [InlineData("claude", "claude-opus-4.8", "cannot use the requested --model")]
    [InlineData("agy", "gpt-5.6-sol", "cannot use it")]
    public async Task Add_refuses_ambiguous_or_invalid_models_before_any_queue_side_effect(
        string? adapter, string model, string message)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var brief = Path.Combine(home, "brief.md");
            await File.WriteAllTextAsync(brief, "implement this", Ct);
            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Add, Tag: "refused", Role: "implement",
                    SpecFilePath: brief, Issue: 2077, Adapter: adapter, Model: model),
                TextWriter.Null, Ct));

            Assert.Contains(message, refusal.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(BatonPaths.QueueFile));
            Assert.False(Directory.Exists(BatonPaths.QueueSpecsDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }


    private static string CreateTempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_queue_cmd_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        return home;
    }

    [Fact]
    public async Task Add_resolves_an_unscoped_models_unique_adapter_before_writing_the_item()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "workspace");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(brief, "implement this", Ct);

            var output = new StringWriter();
            var exit = await QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "model-route", Role: "implement", SpecFilePath: brief,
                    WorkspaceDirectory: workspace, Model: "opus", Effort: "high"),
                output,
                Ct);

            Assert.Equal(0, exit);
            Assert.Contains("tier: claude (from --model opus) / opus / high", output.ToString(), StringComparison.Ordinal);
            Assert.Equal("claude", (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single().Adapter);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_refuses_an_unknown_model_before_creating_a_queue_row()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "workspace");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(brief, "implement this", Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "unknown-model", Role: "implement", SpecFilePath: brief,
                    WorkspaceDirectory: workspace, Model: "not-a-model"),
                TextWriter.Null,
                Ct));

            Assert.Contains("no recorded adapter candidate", refusal.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(BatonPaths.QueueFile));
            Assert.False(Directory.Exists(BatonPaths.QueueSpecsDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_refuses_a_model_the_scopes_resolved_adapter_cannot_use_before_creating_a_queue_row()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "workspace");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(brief, "implement this", Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "model-mismatch", Role: "implement", SpecFilePath: brief,
                    WorkspaceDirectory: workspace, ScopeClass: "tooling", Model: "opus", Reason: "deliberate"),
                TextWriter.Null,
                Ct));

            Assert.Contains("resolved codex adapter cannot use it", refusal.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(BatonPaths.QueueFile));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_prints_an_unscoped_roles_resolved_adapter_without_a_model()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "workspace");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(brief, "implement this", Ct);

            var output = new StringWriter();
            await QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "role-route", Role: "implement", SpecFilePath: brief,
                    WorkspaceDirectory: workspace),
                output,
                Ct);

            Assert.Contains("tier: codex / gpt-6-astra / medium", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("role default adapter", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
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
    public async Task Re_adding_a_cancelled_tag_leaves_its_retained_brief_unchanged()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "w2159");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            var spec = BatonPaths.QueueSpecFile("2159-lane");
            await File.WriteAllTextAsync(spec, "the brief the operator cancelled", Ct);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items =
                    [
                        new QueueItem
                        {
                            Tag = "2159-lane",
                            Role = "implement",
                            Workspace = workspace,
                            SpecFile = spec,
                            State = QueueItemState.Cancelled,
                            CancelledAt = DateTimeOffset.UtcNow,
                        },
                    ],
                },
                Ct);

            var replacement = Path.Combine(home, "replacement.md");
            await File.WriteAllTextAsync(replacement, "a replacement brief", Ct);

            await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "2159-lane", Role: "implement", SpecFilePath: replacement,
                    WorkspaceDirectory: workspace),
                TextWriter.Null,
                Ct));

            // The locked add check protects the same canonical file cancellation retained. This is the
            // cancellation-wins half of the add/cancel race; the old early read alone could not prove it.
            Assert.Equal("the brief the operator cancelled", await File.ReadAllTextAsync(spec, Ct));
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

    /// <summary>
    /// A halted item is marked, and one that merely failed its lane is not — the distinction
    /// <c>QueueCommand.ListAsync</c>'s comment states this token exists for.
    /// </summary>
    /// <remarks>
    /// Both items are in ONE listing and both are <c>failed</c>, so the token cannot be tracking the
    /// state word: a listing that printed <c>halted</c> for every failure would fail the second
    /// assertion, and one that printed it for none would fail the first.
    /// </remarks>
    [Fact]
    public async Task List_marks_the_item_the_queue_has_given_up_on()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items =
                    [
                        new QueueItem
                        {
                            Tag = "given-up",
                            Role = "implement",
                            Workspace = Path.Combine(home, "w1"),
                            SpecFile = BatonPaths.QueueSpecFile("given-up"),
                            State = QueueItemState.Failed,
                            Halted = true,
                        },
                        new QueueItem
                        {
                            Tag = "retryable",
                            Role = "implement",
                            Workspace = Path.Combine(home, "w2"),
                            SpecFile = BatonPaths.QueueSpecFile("retryable"),
                            State = QueueItemState.Failed,
                        },
                    ],
                },
                Ct);

            var output = new StringWriter();
            await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List), output, Ct);

            var lines = output.ToString().ReplaceLineEndings("\n").Split('\n');
            Assert.Contains(lines, line => line.StartsWith("given-up  failed halted", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.StartsWith("retryable  failed  ", StringComparison.Ordinal));
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

    [Fact]
    public async Task Cancel_retains_a_queued_items_history_and_records_the_cancellation()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "w2159");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            var spec = BatonPaths.QueueSpecFile("2159-lane");
            await File.WriteAllTextAsync(spec, "the queued brief", Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [new QueueItem
                {
                    Tag = "2159-lane",
                    Role = "implement",
                    Workspace = workspace,
                    SpecFile = spec,
                }],
            }, Ct);

            var output = new StringWriter();
            Assert.Equal(0, await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.Cancel, Tag: "2159-lane"), output, Ct));

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Cancelled, item.State);
            Assert.NotNull(item.CancelledAt);
            Assert.True(Directory.Exists(workspace));
            Assert.Equal("the queued brief", await File.ReadAllTextAsync(spec, Ct));
            Assert.Contains("retained", output.ToString(), StringComparison.Ordinal);
            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal(QueueDecisionEntry.Cancelled, fact.Decision);
            Assert.Equal("2159-lane", fact.Tag);

            var repeat = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Cancel, Tag: "2159-lane"), TextWriter.Null, Ct));
            Assert.Contains("already cancelled", repeat.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Cancel_retry_backfills_a_ledger_fact_that_failed_after_the_queue_write()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "w2159");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items =
                [
                    new QueueItem
                    {
                        Tag = "2159-lane",
                        Role = "implement",
                        Workspace = workspace,
                        SpecFile = BatonPaths.QueueSpecFile("2159-lane"),
                    },
                ],
            }, Ct);

            // A directory at the ledger's filename makes its append fail after the queue mutation,
            // without depending on the machine's permissions.
            Directory.CreateDirectory(BatonPaths.QueueDecisionLedgerFile);
            var failure = await Record.ExceptionAsync(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Cancel, Tag: "2159-lane"), TextWriter.Null, Ct));
            Assert.True(failure is IOException or UnauthorizedAccessException);

            var cancelled = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single();
            Assert.Equal(QueueItemState.Cancelled, cancelled.State);
            Assert.NotNull(cancelled.CancelledAt);

            Directory.Delete(BatonPaths.QueueDecisionLedgerFile);
            var repeat = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Cancel, Tag: "2159-lane"), TextWriter.Null, Ct));
            Assert.Contains("already cancelled", repeat.Message, StringComparison.Ordinal);

            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal(QueueDecisionEntry.Cancelled, fact.Decision);
            Assert.Equal("2159-lane", fact.Tag);
            Assert.Equal(cancelled.CancelledAt, fact.At);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Import_refuses_a_collision_with_a_retained_cancelled_item()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "w2159");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            var spec = BatonPaths.QueueSpecFile("2159-lane");
            var cancelledAt = new DateTimeOffset(2026, 9, 9, 20, 0, 0, TimeSpan.Zero);
            await File.WriteAllTextAsync(spec, "the cancelled brief", Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items =
                [
                    new QueueItem
                    {
                        Tag = "2159-lane",
                        Role = "implement",
                        Workspace = workspace,
                        SpecFile = spec,
                        State = QueueItemState.Cancelled,
                        CancelledAt = cancelledAt,
                    },
                ],
            }, Ct);
            await QueueDecisionLedgerStore.AppendCancellationAsync(
                cancelledAt, "2159-lane", BatonPaths.QueueDecisionLedgerFile, Ct);

            var import = Path.Combine(home, "scratchpad.json");
            await File.WriteAllTextAsync(
                import,
                """[{ "tag": "2159-lane", "role": "implement", "workspace": "C:\\scratch\\w2159", "launched": false }]""",
                Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Import, ImportFilePath: import), TextWriter.Null, Ct));
            Assert.Contains("cancelled before launch", refusal.Message, StringComparison.Ordinal);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Cancelled, item.State);
            Assert.Equal(cancelledAt, item.CancelledAt);
            Assert.Equal("the cancelled brief", await File.ReadAllTextAsync(spec, Ct));
            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal(QueueDecisionEntry.Cancelled, fact.Decision);
            Assert.Equal(cancelledAt, fact.At);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Cancel_reports_a_missing_tag_without_recording_a_false_cancellation()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Cancel, Tag: "missing"), TextWriter.Null, Ct));

            Assert.Contains("does not exist", refusal.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(BatonPaths.QueueDecisionLedgerFile));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Cancel_refuses_a_launched_item_with_the_room_cancel_remedy()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = Path.Combine(home, "rooms", "queue-2159-lane-abcd1234");
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [new QueueItem
                {
                    Tag = "2159-lane",
                    Role = "implement",
                    Workspace = Path.Combine(home, "w2159"),
                    SpecFile = BatonPaths.QueueSpecFile("2159-lane"),
                    State = QueueItemState.Launched,
                    RoomDirectory = room,
                }],
            }, Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Cancel, Tag: "2159-lane"), TextWriter.Null, Ct));

            Assert.Equal("cancel the launched lane with 'baton cancel " + room + "'.", refusal.TryInvocation);
            Assert.Equal(QueueItemState.Launched, (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single().State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }
}
