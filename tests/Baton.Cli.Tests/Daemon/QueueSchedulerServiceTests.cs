using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #1934 slice 1, item 6: the daemon service's own arms — launch, the runway-held retry, failure, and
/// done detection from a fixture room. Every source of nondeterminism is injected, so nothing here
/// spawns a process or waits on a real clock.
/// </summary>
/// <remarks>
/// Each test takes its own <see cref="BatonEnvironmentSnapshot.BeginScope"/> temp home, so the queue
/// file, the decision ledger and the rooms directory are this test's own and never the operator's.
/// </remarks>
public sealed class QueueSchedulerServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class HungGh(TaskCompletionSource<bool> started) : IGhCliRunner
    {
        private readonly TaskCompletionSource<GhCliResult> _never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<GhCliResult> RunAsync(
            string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            started.TrySetResult(true);
            return _never.Task;
        }
    }

    [Fact]
    public async Task An_unknown_unscoped_role_fails_one_item_and_the_next_unscoped_item_launches()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item("bad", role: "unknown-role", scope: null), Item("good", scope: null)],
            }, Ct);
            var launches = new List<QueueLaunchRequest>();
            var service = Service((request, _) =>
            {
                launches.Add(request);
                return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
            });

            await service.TickOnceAsync(Ct);
            Assert.Empty(launches);
            var failed = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items[0];
            Assert.Equal(QueueItemState.Failed, failed.State);
            Assert.Contains("unknown-role", failed.Error!, StringComparison.Ordinal);
            Assert.Null(failed.RoomDirectory);
            Assert.Null(failed.LaunchedAt);

            await service.TickOnceAsync(Ct);
            var launch = Assert.Single(launches);
            Assert.Equal("good", launch.Item.Tag);
            Assert.Equal(("codex", "gpt-6-astra", "medium"),
                (launch.Tier.Adapter, launch.Tier.Model, launch.Tier.Effort));
            var items = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items;
            Assert.Equal(QueueItemState.Launched, items[1].State);
            var facts = await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct);
            Assert.Collection(facts,
                fact => Assert.Equal(QueueDecisionEntry.Failed, fact.Decision),
                fact => Assert.Equal(QueueDecisionEntry.Launched, fact.Decision));
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_legacy_unpinned_claude_item_fails_without_claiming_a_room_or_launching()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item("legacy", scope: null) with { Adapter = "claude", Model = null }],
            }, Ct);
            var launched = false;
            var service = Service((_, _) =>
            {
                launched = true;
                return Task.FromResult(new QueueLaunchOutcome(null));
            });

            await service.TickOnceAsync(Ct);

            Assert.False(launched);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Null(item.RoomDirectory);
            Assert.Null(item.LaunchedAt);
            Assert.Contains("--model sonnet", item.Error!, StringComparison.Ordinal);
            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal(QueueDecisionEntry.Failed, fact.Decision);
            Assert.Equal("legacy", fact.Tag);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_saved_lifecycle_item_with_explicit_skills_fails_once_before_launch()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item(role: "review") with { Stage = WorkStage.Review, Skills = ["house-style"] }],
            }, Ct);
            var launchCount = 0;
            var service = Service((_, _) =>
            {
                launchCount++;
                return Task.FromResult(new QueueLaunchOutcome(null));
            });

            await service.TickOnceAsync(Ct);
            await service.TickOnceAsync(Ct);

            Assert.Equal(0, launchCount);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Null(item.RoomDirectory);
            Assert.Null(item.LaunchedAt);
            Assert.Contains("lifecycle item", item.Error!, StringComparison.Ordinal);
            Assert.Contains("remove the persisted Skills field", item.Error, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_saved_null_skill_entry_fails_with_a_remedy_instead_of_throwing_from_the_tick()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item() with { Skills = [null!] }],
            }, Ct);
            var launched = false;
            var service = Service((_, _) =>
            {
                launched = true;
                return Task.FromResult(new QueueLaunchOutcome(null));
            });

            await service.TickOnceAsync(Ct);

            Assert.False(launched);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Contains("cannot be null", item.Error!, StringComparison.Ordinal);
            Assert.Contains("remove null skill entries", item.Error, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_write_and_shell_brief_under_advise_is_refused_before_a_room_or_vendor_launch()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item(role: "advise") with { Requirements = ["file-write", "shell"] }],
            }, Ct);
            var launched = false;
            var service = Service((_, _) =>
            {
                launched = true;
                return Task.FromResult(new QueueLaunchOutcome(null));
            });

            await service.TickOnceAsync(Ct);

            Assert.False(launched);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Null(item.RoomDirectory);
            Assert.Equal(TaskRequirementAdmission.Refused, item.LastAdmission!.Result);
            Assert.Equal(["file-write", "shell"], item.LastAdmission.Missing);
            Assert.Equal(0, item.LastAdmission.VendorUsage);
            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal(TaskRequirementAdmission.Refused, fact.Admission!.Result);
            Assert.Equal(0, fact.Admission.VendorUsage);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task Compatible_implement_requirements_are_admitted_and_recorded_before_launch()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item() with { Requirements = ["file-write", "shell", "github-write"] }],
            }, Ct);
            var service = Service((request, _) => Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory)));

            await service.TickOnceAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Launched, item.State);
            Assert.Equal(TaskRequirementAdmission.Admitted, item.LastAdmission!.Result);
            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal(QueueDecisionEntry.Launched, fact.Decision);
            Assert.Equal(TaskRequirementAdmission.Admitted, fact.Admission!.Result);
            Assert.Contains("github-write", fact.Admission.EffectiveGrant);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task GitHub_read_and_write_requirements_are_distinguished_by_the_role_grant()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await File.WriteAllTextAsync(BatonPaths.SettingsFile, "{\"Queue\":{\"GapSeconds\":0}}", Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [
                    Item("read", role: "review") with { Requirements = ["github-read"] },
                    Item("write", role: "review") with { Requirements = ["github-write"] },
                ],
            }, Ct);
            var launches = new List<QueueLaunchRequest>();
            var service = Service((request, _) =>
            {
                launches.Add(request);
                return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
            });

            await service.TickOnceAsync(Ct);
            await service.TickOnceAsync(Ct);

            Assert.Equal("read", Assert.Single(launches).Item.Tag);
            var items = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items;
            var rejected = Assert.Single(items, item => item.Tag == "write");
            Assert.Equal(QueueItemState.Failed, rejected.State);
            Assert.Equal(["github-write"], rejected.LastAdmission!.Missing);
            Assert.Contains("github-read", items.Single(item => item.Tag == "read").LastAdmission!.EffectiveGrant);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_required_gate_receipt_is_refused_when_the_role_does_not_declare_that_artifact()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item(role: "review") with { Requirements = ["artifact:gate-receipt.json"] }],
            }, Ct);
            var service = Service((_, _) => throw new InvalidOperationException("mismatched role must not launch"));

            await service.TickOnceAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Equal(["artifact:gate-receipt.json"], item.LastAdmission!.Missing);
            Assert.Contains("artifact:verdict.json", item.LastAdmission.EffectiveGrant);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task Missing_requirements_fail_closed_for_execution_rows_after_migration_but_read_only_legacy_rows_remain_unknown()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await File.WriteAllTextAsync(BatonPaths.SettingsFile, "{\"Queue\":{\"RequireDeclaredRequirements\":true}}", Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item("execution"), Item("legacy-read", role: "advise")],
            }, Ct);
            var launched = new List<string>();
            var service = Service((request, _) =>
            {
                launched.Add(request.Item.Tag);
                return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
            });

            await service.TickOnceAsync(Ct);
            await service.TickOnceAsync(Ct);

            var items = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items;
            var execution = items.Single(item => item.Tag == "execution");
            Assert.Equal(QueueItemState.Failed, execution.State);
            Assert.Equal(TaskRequirementAdmission.Refused, execution.LastAdmission!.Result);
            Assert.Equal(["declared requirements"], execution.LastAdmission.Missing);
            Assert.Equal(["legacy-read"], launched);
            Assert.Equal(TaskRequirementAdmission.Unknown, items.Single(item => item.Tag == "legacy-read").LastAdmission!.Result);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_role_catalog_change_is_rechecked_at_launch_instead_of_trusting_queue_time_assumptions()
    {
        var home = CreateTempHome();
        var roles = Path.Combine(home, "roles.json");
        await File.WriteAllTextAsync(roles, """
            [{"id":"implement","tier":"standard","read_files":true,"write_files":false,"run_shell_commands":true,"network_access":true,"timeout_minutes":10,"verdict_schema":false,"purpose":"test","outputs":[{"name":"changes.md","schema":"none","instruction":"test"}]}]
            """, Ct);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with
        {
            HomeOverride = home,
            WorkerRolesPathOverride = roles,
            WorkerTiersPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"),
        });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item() with { Requirements = ["file-write"] }],
            }, Ct);
            var service = Service((_, _) => throw new InvalidOperationException("changed grant must be rechecked"));

            await service.TickOnceAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Equal(["file-write"], item.LastAdmission!.Missing);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_replaced_requirement_declaration_is_revalidated_before_the_launch_claim()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item(role: "advise") with { Requirements = [] }],
            }, Ct);
            var launchCount = 0;
            var replaced = false;
            var service = new QueueSchedulerService(
                (_, _) =>
                {
                    launchCount++;
                    return Task.FromResult(new QueueLaunchOutcome(null));
                },
                _ => Task.FromResult(0.0),
                () => 16.0,
                () => DateTimeOffset.UtcNow,
                beforeLaunchClaim: async _ =>
                {
                    if (replaced)
                    {
                        return;
                    }

                    replaced = true;
                    await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
                    {
                        Items = snapshot.Items.Select(item => item with { Requirements = ["file-write"] }).ToList(),
                    }, Ct);
                });

            await service.TickOnceAsync(Ct);

            Assert.True(replaced);
            Assert.Equal(0, launchCount);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Equal(["file-write"], item.LastAdmission!.Missing);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_legacy_read_only_review_row_remains_unknown_after_requirement_migration()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await File.WriteAllTextAsync(BatonPaths.SettingsFile, "{\"Queue\":{\"RequireDeclaredRequirements\":true}}", Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item(role: "review")],
            }, Ct);
            var service = Service((request, _) => Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory)));

            await service.TickOnceAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Launched, item.State);
            Assert.Equal(TaskRequirementAdmission.Unknown, item.LastAdmission!.Result);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_saved_blank_and_named_skill_pair_fails_instead_of_dropping_the_blank()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item() with { Skills = ["", "house-style"] }],
            }, Ct);
            var launched = false;
            var service = Service((_, _) =>
            {
                launched = true;
                return Task.FromResult(new QueueLaunchOutcome(null));
            });

            await service.TickOnceAsync(Ct);

            Assert.False(launched);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Contains("contradictory", item.Error!, StringComparison.Ordinal);
            Assert.Contains("house-style", item.Error, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_saved_valid_skill_list_is_normalized_before_launch()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item() with { Skills = [" house-style ", "house-style", "thorough-review"] }],
            }, Ct);
            QueueLaunchRequest? seen = null;
            var service = Service((request, _) =>
            {
                seen = request;
                return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
            });

            await service.TickOnceAsync(Ct);

            Assert.Equal(["house-style", "thorough-review"], seen!.Item.Skills);
            Assert.Equal(
                QueueItemState.Launched,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).State);
        }
        finally
        {
            Cleanup(home);
        }
    }


    private static string CreateTempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_queue_svc_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        return home;
    }

    private static QueueItem Item(string tag = "t1", string role = "implement", string? scope = "engine") => new()
    {
        Tag = tag,
        Role = role,
        ScopeClass = scope,
        Workspace = @"C:\repos\w1",
        SpecFile = Path.Combine(Path.GetTempPath(), "never-read.md"),
    };

    private static QueueSchedulerService Service(
        Func<QueueLaunchRequest, CancellationToken, Task<QueueLaunchOutcome>> launch,
        double liveWeight = 0,
        double? freeGb = 16.0,
        DateTimeOffset? now = null) =>
        new(launch, _ => Task.FromResult(liveWeight), () => freeGb, () => now ?? DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_launchable_item_is_marked_launched_with_its_room_and_recorded_as_launched()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()] }, Ct);
            QueueLaunchRequest? seen = null;
            var service = Service((request, _) =>
            {
                seen = request;
                return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
            });

            await service.TickOnceAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Launched, item.State);

            // The scheduler picks the room and hands it to the launch, rather than learning it back
            // afterwards — that is what lets the item be marked before the dispatch starts.
            Assert.Equal(Path.Combine(BatonPaths.Rooms, "queue-t1-" + seen!.RoomDirectory[^8..]), item.RoomDirectory);
            Assert.NotNull(item.LaunchedAt);

            // The launcher gets the RESOLVED tier, not the raw item — the queue resolves once and the
            // launch does not re-derive it.
            Assert.Equal("claude", seen!.Tier.Adapter);
            Assert.Equal("opus", seen.Tier.Model);
            Assert.Null(seen.Item.Skills);

            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal("launched", fact.Decision);
            Assert.Equal("t1", fact.Tag);
            Assert.Equal("engine", fact.Tier);
            Assert.Equal(item.RoomDirectory, fact.Room);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_hung_board_forge_refresh_does_not_block_an_unrelated_queue_launch()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        try
        {
            var observed = Item("observed") with
            {
                Role = "review",
                Stage = WorkStage.Review,
                State = QueueItemState.Cancelled,
                Repository = "github.com/aer-works/baton",
                PullRequest = 77,
                Workspace = home,
            };
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with { Items = [observed, Item("unrelated")] },
                Ct);
            var forgeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gh = new HungGh(forgeStarted);
            var advancer = new WorkItemAdvancer(
                gh,
                (_, _) => Task.FromResult<string?>(null),
                repositoryIdentity: null,
                boardObservationTimeout: TimeSpan.FromMinutes(1));
            var poll = new DeliveryPoller(gh, advancer).PollOnceAsync(refreshCancellation.Token);
            await forgeStarted.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct);

            var launched = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var scheduler = new QueueSchedulerService(
                (request, _) =>
                {
                    launched.TrySetResult(request.Item.Tag);
                    return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
                },
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UtcNow,
                advancer);

            await scheduler.TickOnceAsync(Ct).WaitAsync(TimeSpan.FromMinutes(1), Ct);

            Assert.Equal("unrelated", await launched.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct));
            Assert.False(poll.IsCompleted);
            Assert.Equal(
                QueueItemState.Launched,
                (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single(i => i.Tag == "unrelated").State);

            refreshCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
        }
        finally
        {
            refreshCancellation.Cancel();
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_cancel_that_wins_the_queue_mutation_lock_re_evaluates_and_launches_the_next_item()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item("2159-lane"), Item("next")] }, Ct);
            var claimReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowClaim = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var launched = new List<string>();
            var claimCount = 0;
            var service = new QueueSchedulerService(
                (request, _) =>
                {
                    launched.Add(request.Item.Tag);
                    return Task.FromResult(new QueueLaunchOutcome(null));
                },
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UtcNow,
                beforeLaunchClaim: _ =>
                {
                    if (Interlocked.Increment(ref claimCount) == 1)
                    {
                        claimReached.TrySetResult(true);
                        return allowClaim.Task;
                    }

                    return Task.CompletedTask;
                });

            var tick = service.TickOnceAsync(Ct);
            await claimReached.Task.WaitAsync(Ct);

            await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.Cancel, Tag: "2159-lane"), TextWriter.Null, Ct);
            allowClaim.TrySetResult(true);
            await tick;

            Assert.Equal(["next"], launched);
            var items = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items;
            Assert.Equal(QueueItemState.Cancelled, items[0].State);
            Assert.Equal(QueueItemState.Launched, items[1].State);
            var facts = await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct);
            Assert.Contains(facts, fact => fact.Decision == QueueDecisionEntry.Cancelled && fact.Tag == "2159-lane");
            Assert.Contains(facts, fact => fact.Decision == QueueDecisionEntry.Launched && fact.Tag == "next");
            Assert.DoesNotContain(facts, fact => fact.Decision == QueueDecisionEntry.Waited && fact.Reason == "no-items");
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_cancelled_follower_makes_the_heads_unchanged_slots_wait_current_again()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item("head"), Item("follower")] }, Ct);
            var service = Service(
                (_, _) => throw new InvalidOperationException("a full queue must not launch"),
                liveWeight: QueueSettings.DefaultMaxLiveWeight);

            await service.TickOnceAsync(Ct);
            await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.Cancel, Tag: "follower"), TextWriter.Null, Ct);
            await service.TickOnceAsync(Ct);

            var facts = await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct);
            var current = facts[^1];
            Assert.Equal(QueueDecisionEntry.Waited, current.Decision);
            Assert.Equal("head", current.Tag);
            Assert.Equal(QueueWaitReasons.Token(QueueWaitReason.Slots), current.Reason);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task Sustained_lost_claims_take_only_one_fresh_scheduling_pass_per_tick()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile, s => s with { Items = [Item("first"), Item("second"), Item("third")] }, Ct);
            var claims = 0;
            var launches = new List<string>();
            var service = new QueueSchedulerService(
                (request, _) =>
                {
                    launches.Add(request.Item.Tag);
                    return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
                },
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UtcNow,
                beforeLaunchClaim: async _ =>
                {
                    var tag = Interlocked.Increment(ref claims) switch
                    {
                        1 => "first",
                        2 => "second",
                        _ => "third",
                    };
                    await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.Cancel, Tag: tag), TextWriter.Null, Ct);
                });

            await service.TickOnceAsync(Ct);

            Assert.Equal(2, claims);
            Assert.Empty(launches);
            var items = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items;
            Assert.Equal(QueueItemState.Cancelled, items[0].State);
            Assert.Equal(QueueItemState.Cancelled, items[1].State);
            Assert.Equal(QueueItemState.Queued, items[2].State);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_restart_tick_backfills_a_committed_cancellation_once_when_its_ledger_fact_is_missing()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var cancelledAt = new DateTimeOffset(2026, 9, 9, 20, 0, 0, TimeSpan.Zero);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item("2159-lane") with { State = QueueItemState.Cancelled, CancelledAt = cancelledAt }],
            }, Ct);

            // This is the committed-queue/missing-ledger crash window. A fresh service models the
            // daemon after restart; no operator repeats the cancel command.
            await Service((_, _) => throw new InvalidOperationException("a cancelled item must not launch"))
                .TickOnceAsync(Ct);

            // A later tick is a duplicate replay. The cancellation key must retain exactly one fact.
            await Service((_, _) => throw new InvalidOperationException("a cancelled item must not launch"))
                .TickOnceAsync(Ct);

            var facts = await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct);
            var fact = Assert.Single(facts, entry => entry.Decision == QueueDecisionEntry.Cancelled);
            Assert.Equal("2159-lane", fact.Tag);
            Assert.Equal(cancelledAt, fact.At);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task The_item_is_marked_launched_before_the_launch_starts_so_a_shutdown_mid_launch_cannot_relaunch_it()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()] }, Ct);

            // Read the queue from INSIDE the launch, which is the window a daemon shutdown lands in:
            // the dispatch is running detached on CancellationToken.None, so whatever the file says
            // here is what the next daemon start reads.
            QueueItem? duringLaunch = null;
            CancellationToken tokenLaunchSaw = default;
            var service = Service(async (request, ct) =>
            {
                tokenLaunchSaw = ct;
                duringLaunch = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single();
                return new QueueLaunchOutcome(request.RoomDirectory);
            });

            await service.TickOnceAsync(Ct);

            Assert.Equal(QueueItemState.Launched, duringLaunch!.State);
            Assert.NotNull(duringLaunch.RoomDirectory);
            Assert.NotNull(duringLaunch.LaunchedAt);

            // Recorded under the same token the launch runs under: a cancelled token would mean
            // QueueStore.MutateAsync never ran its delegate at all.
            Assert.False(tokenLaunchSaw.CanBeCanceled);

            // And the room the item was marked with is the room the dispatch was handed.
            var after = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(duringLaunch.RoomDirectory, after.RoomDirectory);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_shutdown_raised_out_of_the_launch_fails_the_item_rather_than_leaving_it_launched()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()] }, Ct);
            var service = Service((_, _) => throw new OperationCanceledException());

            await service.TickOnceAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Contains("shut down", item.Error!, StringComparison.Ordinal);

            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal("failed", fact.Decision);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_throw_the_launcher_does_not_model_is_recorded_as_a_failure_not_swallowed_by_the_loop()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()] }, Ct);
            var service = Service((_, _) => throw new IOException("the settle write failed"));

            await service.TickOnceAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Contains("the settle write failed", item.Error!, StringComparison.Ordinal);

            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal("failed", fact.Decision);
            Assert.Equal("t1", fact.Tag);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task An_evaluation_that_throws_before_any_decision_still_writes_a_fact()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // A malformed queue file is refused rather than read as empty (spec/baton.md §13), which
            // throws before the tick reaches a decision. The ledger must still carry a row: the hole
            // this closes is an evaluation that happened and left nothing behind.
            Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.QueueFile)!);
            await File.WriteAllTextAsync(BatonPaths.QueueFile, "{ not json", Ct);
            var launched = false;
            var service = Service((_, _) =>
            {
                launched = true;
                return Task.FromResult(new QueueLaunchOutcome(null));
            });

            await Assert.ThrowsAnyAsync<Exception>(() => service.TickOnceAsync(Ct));

            Assert.False(launched);
            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal("failed", fact.Decision);
            Assert.Null(fact.Tag);
            // Absent, never a fabricated zero -- the reading was never taken.
            Assert.Null(fact.FreeGb);
            Assert.Contains("recorded no counters", fact.Reason!, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_runway_hold_leaves_the_item_queued_and_records_runway_held()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()] }, Ct);
            var service = Service((_, _) => Task.FromResult(new QueueLaunchOutcome(null, RunwayHeld: true)));

            await service.TickOnceAsync(Ct);

            // Q5's arm, and now also the undo of the pre-launch mark: nothing was dispatched, so the
            // item is back exactly as it was and the next tick considers it again.
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Queued, item.State);
            Assert.Null(item.RoomDirectory);
            Assert.Null(item.LaunchedAt);

            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal("waited", fact.Decision);
            Assert.Equal("runway-held", fact.Reason);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_hold_and_a_launch_are_told_apart_by_the_outcome_not_by_an_exception()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()] }, Ct);
            // A refusal that is NOT a hold — a missing spec, an unknown role, a drain marker. It must
            // fail the item OUT of the queue rather than retry it forever with a false reason, which is
            // exactly what branching on the exception type would have done.
            var service = Service((_, _) => Task.FromResult(new QueueLaunchOutcome(null, Error: "no such role 'implment'")));

            await service.TickOnceAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Contains("implment", item.Error!, StringComparison.Ordinal);

            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal("failed", fact.Decision);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_scope_class_with_no_configured_tier_fails_the_item_rather_than_launching_it()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // 'review' + 'infra' is a key in neither the shipped table nor a configured one. Written
            // straight into the store here, bypassing the verb — which is the only way this state can
            // arise, and therefore the only way the daemon's own check can be exercised at all.
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile, s => s with { Items = [Item(role: "review", scope: "infra")] }, Ct);
            var launched = false;
            var service = Service((_, _) =>
            {
                launched = true;
                return Task.FromResult(new QueueLaunchOutcome(@"C:\rooms\x"));
            });

            await service.TickOnceAsync(Ct);

            // The launcher must not be reached at all — a refusal recorded after a dispatch started
            // would be a lane already spending on some other tier's model.
            Assert.False(launched);
            Assert.Equal(
                QueueItemState.Failed,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).State);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_held_queue_records_the_hold_and_never_calls_the_launcher()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()], Held = true }, Ct);
            var launched = false;
            var service = Service((_, _) =>
            {
                launched = true;
                return Task.FromResult(new QueueLaunchOutcome(@"C:\rooms\x"));
            });

            await service.TickOnceAsync(Ct);

            Assert.False(launched);
            Assert.Equal(
                "hold",
                Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct)).Reason);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task Done_detection_reads_the_room_and_marks_a_clean_settle_done()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = Path.Combine(home, "rooms", "queue-t1-abcd");
            Directory.CreateDirectory(room);
            await File.WriteAllTextAsync(
                Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName),
                // "Succeeded" is the room-level word WorkflowOutcome.Describe writes into a sentinel;
                // "Terminal" — what this fixture carried until #1939's review — is a WorkflowStatus
                // value no projector ever puts in this field, so the assertion below passed vacuously.
                """{"state":"Succeeded","steps":[{"id":"implement","state":"Succeeded","execution":"e1"}],"outputs":[],"error":null}""",
                Ct);

            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with { Items = [Item() with { State = QueueItemState.Launched, RoomDirectory = room }] },
                Ct);

            await Service((_, _) => Task.FromResult(new QueueLaunchOutcome(null))).ResolveFinishedItemsAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Done, item.State);
            Assert.Null(item.Error);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_launched_item_whose_room_is_not_terminal_yet_stays_launched()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // The control arm for the two classification tests: no sentinel means no verdict yet, not
            // "done". Without it, a test suite that only ever wrote sentinels could not tell the
            // detection from an unconditional mark.
            var room = Path.Combine(home, "rooms", "queue-t1-live");
            Directory.CreateDirectory(room);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with { Items = [Item() with { State = QueueItemState.Launched, RoomDirectory = room }] },
                Ct);

            await Service((_, _) => Task.FromResult(new QueueLaunchOutcome(null))).ResolveFinishedItemsAsync(Ct);

            Assert.Equal(
                QueueItemState.Launched,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).State);
        }
        finally
        {
            Cleanup(home);
        }
    }

    /// <summary>
    /// The roomless sweep (#1939 review): a launch whose room was never created can never produce a
    /// sentinel, so without this the item sits in <see cref="QueueItemState.Launched"/> forever. The
    /// two cases are one clock apart, which is what makes the grace period the discriminator rather
    /// than "the directory is missing".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_launched_item_whose_room_was_never_created_fails_only_once_the_grace_period_has_passed(bool pastGrace)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var now = DateTimeOffset.UtcNow;
            var launchedAt = pastGrace
                ? now - QueueSchedulerService.NoRoomGrace - TimeSpan.FromMinutes(1)
                : now - TimeSpan.FromSeconds(5);
            var room = Path.Combine(home, "rooms", "queue-t1-never-made");

            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items = [Item() with
                    {
                        State = QueueItemState.Launched, RoomDirectory = room, LaunchedAt = launchedAt,
                    }],
                },
                Ct);

            await Service((_, _) => Task.FromResult(new QueueLaunchOutcome(null)), now: now)
                .ResolveFinishedItemsAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(pastGrace ? QueueItemState.Failed : QueueItemState.Launched, item.State);
            if (pastGrace)
            {
                Assert.Contains(room, item.Error!, StringComparison.Ordinal);
            }
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task An_imported_launched_item_carrying_no_room_is_never_swept()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // QueueImport's own remarks: the runner recorded no room, so the operator clears these by
            // hand. Sweeping them as "no-room" would fail lanes that are in fact running.
            var now = DateTimeOffset.UtcNow;
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items = [Item() with
                    {
                        State = QueueItemState.Launched,
                        RoomDirectory = null,
                        LaunchedAt = now - TimeSpan.FromDays(1),
                    }],
                },
                Ct);

            await Service((_, _) => Task.FromResult(new QueueLaunchOutcome(null)), now: now)
                .ResolveFinishedItemsAsync(Ct);

            Assert.Equal(
                QueueItemState.Launched,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).State);
        }
        finally
        {
            Cleanup(home);
        }
    }

    /// <summary>
    /// Every non-success word the projector emits, with the word SOURCED from
    /// <see cref="WorkflowOutcome.Describe"/> over a hand-built terminal state rather than typed as a
    /// literal — the first assertion in each case is the control, and it is what the previous version
    /// of these tests lacked: they fed <c>"Terminal"</c>, a <see cref="WorkflowStatus"/> value no
    /// projector writes into that field, so they discriminated nothing (#1939 review, HIGH).
    /// </summary>
    /// <remarks>
    /// The three cases are the review's three failure scenarios: <c>baton cancel</c> on a launched
    /// lane, an approval-gate reject or <c>baton resolve --reject</c>, and a Terminal room left with an
    /// unreachable step. None of them sets <see cref="WorkflowStatusView.Error"/>, which is why each
    /// one read as Done before.
    /// </remarks>
    [Theory]
    [InlineData(StepStatus.Cancelled, WorkflowOutcome.Cancelled)]
    [InlineData(StepStatus.Rejected, WorkflowOutcome.Failed)]
    [InlineData(StepStatus.Pending, WorkflowOutcome.Failed)]
    public void A_room_that_did_not_settle_succeeded_is_failed_and_carries_its_own_outcome_word(
        StepStatus status, string expectedWord)
    {
        var word = WorkflowOutcome.Describe(TerminalState([Step("implement", status)]));
        Assert.Equal(expectedWord, word);

        var sentinel = new WorkflowStatusView(
            word, [new WorkflowStatusStepView("implement", status.ToString(), "e1")], [], null);

        var (state, error) = QueueSchedulerService.ClassifyTerminal(sentinel, @"C:\rooms\r1");

        Assert.Equal(QueueItemState.Failed, state);
        Assert.Contains(@"C:\rooms\r1", error!, StringComparison.Ordinal);
        Assert.Contains(word, error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_indeterminate_room_is_failed_and_names_the_resolve_remedy()
    {
        // The word is room-level, never a step state — QueueSchedulerService.ClassifyTerminal's own
        // remarks cite the #1608 ruling that makes it so. The control below is what pins it: a
        // predicate over step states, which is what this classifier used to run, could not have
        // produced this word from this step.
        var word = WorkflowOutcome.Describe(
            TerminalState([Step("implement", StepStatus.Failed) with { IndeterminateAwaitingResolution = true }]));
        Assert.Equal(WorkflowOutcome.Indeterminate, word);

        var sentinel = new WorkflowStatusView(
            word, [new WorkflowStatusStepView("implement", "Failed", "e1")], [], null);

        var (state, error) = QueueSchedulerService.ClassifyTerminal(sentinel, @"C:\rooms\r1");

        Assert.Equal(QueueItemState.Failed, state);
        Assert.Contains(@"C:\rooms\r1", error!, StringComparison.Ordinal);
        Assert.Contains("baton resolve", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_settled_success_is_done_and_keeps_no_error()
    {
        var word = WorkflowOutcome.Describe(TerminalState([Step("implement", StepStatus.Succeeded)]));
        Assert.Equal(WorkflowOutcome.Succeeded, word);

        var (state, error) = QueueSchedulerService.ClassifyTerminal(
            new WorkflowStatusView(word, [new WorkflowStatusStepView("implement", "Succeeded", "e1")], [], null),
            @"C:\rooms\r1");

        Assert.Equal(QueueItemState.Done, state);
        Assert.Null(error);
    }

    /// <summary>
    /// #1945: the second succeeded-shaped word. Read against the theory above, which is the control —
    /// every non-succeeded word there lands Failed through the same call, so this arm cannot be
    /// passing because <see cref="QueueSchedulerService.ClassifyTerminal"/> says Done to everything.
    /// </summary>
    [Fact]
    public void A_room_that_finished_during_teardown_is_done_and_keeps_no_error()
    {
        // Derived through Describe, not hand-written: the step is Succeeded and carries the flag, so
        // this pins the projector-to-classifier hop too, not merely the switch arm below.
        var word = WorkflowOutcome.Describe(TerminalState(
            [Step("implement", StepStatus.Succeeded) with { FinishedDuringTeardown = true }]));
        Assert.Equal(WorkflowOutcome.FinishedDuringTeardown, word);

        var (state, error) = QueueSchedulerService.ClassifyTerminal(
            new WorkflowStatusView(word, [new WorkflowStatusStepView("implement", "Succeeded", "e1")], [], null),
            @"C:\rooms\r1");

        Assert.Equal(QueueItemState.Done, state);
        Assert.Null(error);
    }

    /// <summary>
    /// The fail-closed arm <see cref="QueueSchedulerService.ClassifyTerminal"/>'s remarks describe: a
    /// hand-edited sentinel, one with no <c>state</c> field at all, or one written by a future
    /// <see cref="WorkflowOutcome"/> member nobody swept.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Terminal")]
    public void A_sentinel_carrying_no_outcome_word_this_assembly_knows_is_failed(string? word)
    {
        var (state, error) = QueueSchedulerService.ClassifyTerminal(
            new WorkflowStatusView(word!, [], [], null), @"C:\rooms\r1");

        Assert.Equal(QueueItemState.Failed, state);
        Assert.Contains(@"C:\rooms\r1", error!, StringComparison.Ordinal);
    }

    private static readonly WorkflowDefinitionSnapshotId SnapshotId = new(Guid.NewGuid().ToString("N"));

    private static FlowState TerminalState(IReadOnlyList<StepState> steps) =>
        new(SnapshotId, steps, WorkflowStatus.Terminal);

    private static StepState Step(string stepId, StepStatus status) =>
        new(new StepId(stepId), status, new ExecutionId(Guid.NewGuid().ToString("N")), new Dictionary<StepId, ExecutionId>());

    private static void Cleanup(string home) => DirectoryCleanup.DeleteRecursively(home);
}
