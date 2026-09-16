using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;
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
    private const string BaseHead = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ReviewHead = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string FixedHead = "cccccccccccccccccccccccccccccccccccccccc";
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

    private sealed class MergedObservationGh : IGhCliRunner
    {
        public Task<GhCliResult> RunAsync(
            string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            var number = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(new GhCliResult(true, 0,
                $$"""{"number":{{number}},"state":"MERGED","headRefOid":"head-{{number}}"}""",
                string.Empty));
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
            Assert.Equal(("codex", "gpt-5.6-sol", "medium"),
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

    [Theory]
    [InlineData("codex", "gpt-6-astra", "Astra is conductor-only")]
    [InlineData("claude", "gpt-5.6-terra", "known by codex")]
    public async Task A_persisted_invalid_adapter_model_item_fails_without_claiming_a_room_or_launching(
        string adapter, string model, string error)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // QueueStore is the persisted/imported seam: old queue snapshots can bypass queue add.
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item("imported-model", scope: null) with { Adapter = adapter, Model = model }],
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
            Assert.Contains(error, item.Error!, StringComparison.OrdinalIgnoreCase);
            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal(QueueDecisionEntry.Failed, fact.Decision);
            Assert.Equal("imported-model", fact.Tag);
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
    public async Task A_recorded_ceiling_narrows_the_admitted_grant_in_queue_and_decision_ledger()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "capped-workspace");
            Directory.CreateDirectory(workspace);
            ProjectCeilingStore.Set(workspace,
                new ProjectCeiling(ReadFiles: true, WriteFiles: true,
                    RunShellCommands: false, NetworkAccess: true), ProjectCeilingStore.DefaultPath);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item("capped") with
                {
                    Workspace = workspace,
                    Requirements = ["repository-read", "file-write", "network"],
                }],
            }, Ct);
            var service = Service((request, _) => Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory)));

            await service.TickOnceAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Launched, item.State);
            Assert.Equal(TaskRequirementAdmission.Admitted, item.LastAdmission!.Result);
            Assert.DoesNotContain("shell", item.LastAdmission.EffectiveGrant);
            Assert.DoesNotContain("github-write", item.LastAdmission.EffectiveGrant);
            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal(item.LastAdmission.Result, fact.Admission?.Result);
            Assert.Equal(item.LastAdmission.EffectiveGrant, fact.Admission?.EffectiveGrant);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_ceiling_narrowed_after_add_refuses_before_a_room_or_vendor_launch()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "narrowed-workspace");
            Directory.CreateDirectory(workspace);
            ProjectCeilingStore.Set(workspace,
                new ProjectCeiling(ReadFiles: true, WriteFiles: false,
                    RunShellCommands: true, NetworkAccess: false), ProjectCeilingStore.DefaultPath);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item("narrowed") with
                {
                    Workspace = workspace,
                    Requirements = ["file-write", "network", "github-write"],
                }],
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
            Assert.Contains("file-write", item.LastAdmission.Missing!);
            Assert.Contains("WriteFiles", item.Error!, StringComparison.Ordinal);
            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal(item.LastAdmission.Result, fact.Admission?.Result);
            Assert.Equal(item.LastAdmission.EffectiveGrant, fact.Admission?.EffectiveGrant);
            Assert.Equal(item.LastAdmission.Missing, fact.Admission?.Missing);
            Assert.Equal(0, fact.Admission?.VendorUsage);
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
    public async Task A_capped_legacy_implement_row_still_fails_closed_when_requirements_are_mandatory()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await File.WriteAllTextAsync(BatonPaths.SettingsFile, "{\"Queue\":{\"RequireDeclaredRequirements\":true}}", Ct);
            var workspace = Path.Combine(home, "capped-legacy-workspace");
            Directory.CreateDirectory(workspace);
            ProjectCeilingStore.Set(workspace,
                new ProjectCeiling(ReadFiles: true, WriteFiles: false,
                    RunShellCommands: false, NetworkAccess: false), ProjectCeilingStore.DefaultPath);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item("legacy-implement") with { Workspace = workspace, Requirements = null }],
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
            Assert.Equal(["declared requirements"], item.LastAdmission.Missing);
            Assert.Equal(["repository-read", "artifact:changes.md"], item.LastAdmission.EffectiveGrant);
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
    public async Task A_replaced_task_size_declaration_rejects_the_stale_claim_and_launches_the_reevaluated_rationale()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item() with { DeclaredTaskSize = new(DeclaredTaskSize.Medium, "one durable seam") }],
            }, Ct);
            var launches = new List<QueueLaunchRequest>();
            var service = new QueueSchedulerService(
                (request, _) =>
                {
                    launches.Add(request);
                    return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
                },
                _ => Task.FromResult(0.0),
                () => 16.0,
                () => DateTimeOffset.UtcNow,
                beforeLaunchClaim: _ => QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
                {
                    Items = snapshot.Items.Select(item => item with
                    {
                        DeclaredTaskSize = new(DeclaredTaskSize.Medium, "a replacement rationale"),
                    }).ToList(),
                }, Ct));

            await service.TickOnceAsync(Ct);

            var launch = Assert.Single(launches);
            Assert.Equal("a replacement rationale", launch.Item.DeclaredTaskSize.Rationale);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Launched, item.State);
            Assert.Equal("a replacement rationale", item.DeclaredTaskSize.Rationale);
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

    [Fact]
    public async Task A_git_lock_observed_at_the_final_production_prelaunch_check_refuses_without_starting_a_lane()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "workspace");
            var gitDirectory = Directory.CreateDirectory(Path.Combine(workspace, ".git")).FullName;
            var lockPath = Path.Combine(gitDirectory, "index.lock");
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with { Items = [Item("locked") with { Workspace = workspace }] }, Ct);
            var launched = false;
            var service = new QueueSchedulerService(
                (_, _) =>
                {
                    launched = true;
                    return Task.FromResult(new QueueLaunchOutcome(null));
                },
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UtcNow,
                workspaceHead: (_, _) => Task.FromResult<string?>("89abcdef"),
                workspaceLocks: candidateWorkspace =>
                {
                    File.WriteAllText(lockPath, "held");
                    return GitWorkspaceLockProbe.FindExisting(candidateWorkspace);
                });

            await service.TickOnceAsync(Ct);

            Assert.False(launched);
            Assert.True(File.Exists(lockPath));
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Null(item.RoomDirectory);
            Assert.Null(item.LaunchedAt);
            Assert.Contains(lockPath, item.Error!, StringComparison.Ordinal);
            Assert.Contains("Baton did not remove", item.Error, StringComparison.Ordinal);
            Assert.Contains("re-add", item.Error, StringComparison.Ordinal);
            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal(QueueDecisionEntry.Failed, fact.Decision);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public void Git_lock_detection_resolves_a_linked_worktrees_git_directory_and_reports_only_existing_locks()
    {
        var root = CreateTempHome();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var linkedGitDirectory = Directory.CreateDirectory(Path.Combine(root, "main", ".git", "worktrees", "workspace")).FullName;
            File.WriteAllText(Path.Combine(workspace, ".git"), "gitdir: ../main/.git/worktrees/workspace\n");
            var headLock = Path.Combine(linkedGitDirectory, "HEAD.lock");
            File.WriteAllText(headLock, "held");

            var locks = GitWorkspaceLockProbe.FindExisting(workspace);

            Assert.Equal([headLock], locks);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Git_lock_detection_returns_empty_for_a_workspace_without_known_locks()
    {
        var root = CreateTempHome();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            Directory.CreateDirectory(Path.Combine(workspace, ".git"));

            Assert.Empty(GitWorkspaceLockProbe.FindExisting(workspace));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task An_unreadable_git_directory_declaration_fails_closed_without_starting_a_lane()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(home, "workspace")).FullName;
            File.WriteAllText(Path.Combine(workspace, ".git"), "not a gitdir declaration");
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with { Items = [Item("malformed") with { Workspace = workspace }] }, Ct);
            var launched = false;
            var service = new QueueSchedulerService(
                (_, _) =>
                {
                    launched = true;
                    return Task.FromResult(new QueueLaunchOutcome(null));
                },
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UtcNow,
                workspaceHead: (_, _) => Task.FromResult<string?>("89abcdef"));

            await service.TickOnceAsync(Ct);

            Assert.False(launched);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Contains("could not inspect Git lock state", item.Error!, StringComparison.Ordinal);
            Assert.Contains("No lane was started", item.Error, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_git_lock_inspection_access_error_fails_closed_without_starting_a_lane()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(home, "workspace")).FullName;
            Directory.CreateDirectory(Path.Combine(workspace, ".git"));
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with { Items = [Item("denied") with { Workspace = workspace }] }, Ct);
            var launched = false;
            var service = new QueueSchedulerService(
                (_, _) =>
                {
                    launched = true;
                    return Task.FromResult(new QueueLaunchOutcome(null));
                },
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UtcNow,
                workspaceHead: (_, _) => Task.FromResult<string?>("89abcdef"),
                workspaceLocks: candidateWorkspace => GitWorkspaceLockProbe.FindExisting(
                    candidateWorkspace,
                    path => path.EndsWith(".git", StringComparison.Ordinal)
                        ? FileAttributes.Directory
                        : throw new UnauthorizedAccessException("fixture denied")));

            await service.TickOnceAsync(Ct);

            Assert.False(launched);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Contains("could not inspect Git lock state", item.Error!, StringComparison.Ordinal);
            Assert.Contains("fixture denied", item.Error, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task An_unreadable_workspace_git_metadata_path_fails_closed_without_starting_a_lane()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(home, "workspace")).FullName;
            var dotGit = Directory.CreateDirectory(Path.Combine(workspace, ".git")).FullName;
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with { Items = [Item("metadata-denied") with { Workspace = workspace }] }, Ct);
            var launched = false;
            var service = new QueueSchedulerService(
                (_, _) =>
                {
                    launched = true;
                    return Task.FromResult(new QueueLaunchOutcome(null));
                },
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UtcNow,
                workspaceHead: (_, _) => Task.FromResult<string?>("89abcdef"),
                workspaceLocks: candidateWorkspace => GitWorkspaceLockProbe.FindExisting(
                    candidateWorkspace,
                    path => path == dotGit
                        ? throw new UnauthorizedAccessException("workspace metadata denied")
                        : File.GetAttributes(path)));

            await service.TickOnceAsync(Ct);

            Assert.False(launched);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Null(item.RoomDirectory);
            Assert.Null(item.LaunchedAt);
            Assert.Contains("could not inspect Git lock state", item.Error!, StringComparison.Ordinal);
            Assert.Contains("workspace metadata denied", item.Error, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task An_unreadable_linked_worktree_gitdir_target_fails_closed_without_starting_a_lane()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(home, "workspace")).FullName;
            var dotGit = Path.Combine(workspace, ".git");
            var gitDirectory = Path.Combine(home, "main", ".git", "worktrees", "workspace");
            File.WriteAllText(dotGit, "gitdir: ../main/.git/worktrees/workspace\n");
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with { Items = [Item("gitdir-denied") with { Workspace = workspace }] }, Ct);
            var launched = false;
            var service = new QueueSchedulerService(
                (_, _) =>
                {
                    launched = true;
                    return Task.FromResult(new QueueLaunchOutcome(null));
                },
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UtcNow,
                workspaceHead: (_, _) => Task.FromResult<string?>("89abcdef"),
                workspaceLocks: candidateWorkspace => GitWorkspaceLockProbe.FindExisting(
                    candidateWorkspace,
                    path => path == gitDirectory
                        ? throw new UnauthorizedAccessException("linked gitdir metadata denied")
                        : File.GetAttributes(path)));

            await service.TickOnceAsync(Ct);

            Assert.False(launched);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Null(item.RoomDirectory);
            Assert.Null(item.LaunchedAt);
            Assert.Contains("could not inspect Git lock state", item.Error!, StringComparison.Ordinal);
            Assert.Contains("linked gitdir metadata denied", item.Error, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_linked_worktree_whose_gitdir_target_is_missing_fails_closed_without_starting_a_lane()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(home, "workspace")).FullName;
            File.WriteAllText(Path.Combine(workspace, ".git"), "gitdir: ../missing/workspace\n");
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with { Items = [Item("dangling") with { Workspace = workspace }] }, Ct);
            var launched = false;
            var service = new QueueSchedulerService(
                (_, _) =>
                {
                    launched = true;
                    return Task.FromResult(new QueueLaunchOutcome(null));
                },
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UtcNow,
                workspaceHead: (_, _) => Task.FromResult<string?>("89abcdef"));

            await service.TickOnceAsync(Ct);

            Assert.False(launched);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Contains("could not inspect Git lock state", item.Error!, StringComparison.Ordinal);
            Assert.Contains("missing", item.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_cancel_that_wins_while_a_lock_is_inspected_remains_cancelled_and_launches_the_next_item()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var lockPath = Path.Combine(home, "index.lock");
            File.WriteAllText(lockPath, "held");
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with { Items = [Item("locked"), Item("next")] }, Ct);
            var probeReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var launches = new List<string>();
            var probeCount = 0;
            var service = new QueueSchedulerService(
                (request, _) =>
                {
                    launches.Add(request.Item.Tag);
                    return Task.FromResult(new QueueLaunchOutcome(null));
                },
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UtcNow,
                workspaceLocks: _ =>
                {
                    if (Interlocked.Increment(ref probeCount) == 1)
                    {
                        probeReached.TrySetResult(true);
                        releaseProbe.Task.GetAwaiter().GetResult();
                        return [lockPath];
                    }

                    return [];
                });

            var tick = Task.Run(() => service.TickOnceAsync(Ct), Ct);
            await probeReached.Task.WaitAsync(Ct);
            await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.Cancel, Tag: "locked"), TextWriter.Null, Ct);
            releaseProbe.TrySetResult(true);
            await tick;

            Assert.Equal(["next"], launches);
            var items = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items;
            Assert.Equal(QueueItemState.Cancelled, items[0].State);
            Assert.Equal(QueueItemState.Launched, items[1].State);
            var facts = await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct);
            Assert.Contains(facts, fact => fact.Decision == QueueDecisionEntry.Cancelled && fact.Tag == "locked");
            Assert.DoesNotContain(facts, fact => fact.Decision == QueueDecisionEntry.Failed && fact.Tag == "locked");
        }
        finally
        {
            Cleanup(home);
        }
    }

    private static QueueSchedulerService Service(
        Func<QueueLaunchRequest, CancellationToken, Task<QueueLaunchOutcome>> launch,
        double liveWeight = 0,
        double? freeGb = 16.0,
        DateTimeOffset? now = null) =>
        new(launch, _ => Task.FromResult(liveWeight), () => freeGb, () => now ?? DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_merged_retirement_blocks_a_queued_lifecycle_launch_regardless_of_lock_order(bool retireBeforeSelection)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string tag = "merged-review";
            Directory.CreateDirectory(BatonPaths.Queue);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, state => state with
            {
                Items = [Item(tag, role: "review") with
                {
                    Stage = WorkStage.Review,
                    Repository = "github.com/aer-works/baton",
                    PullRequest = 2307,
                    Workspace = home,
                }],
            }, Ct);

            var advancer = new WorkItemAdvancer(
                new MergedObservationGh(),
                (_, _) => Task.FromResult<string?>(null));
            Task RetireAsync() => advancer.RefreshPullRequestObservationsAsync(DateTimeOffset.UtcNow, Ct);

            var launches = new List<QueueLaunchRequest>();
            var claimReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowClaim = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new QueueSchedulerService(
                (request, _) =>
                {
                    launches.Add(request);
                    return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
                },
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UtcNow,
                beforeLaunchClaim: _ =>
                {
                    claimReached.TrySetResult(true);
                    return allowClaim.Task;
                });

            if (retireBeforeSelection)
            {
                await RetireAsync();
                allowClaim.TrySetResult(true);
                await service.TickOnceAsync(Ct);
            }
            else
            {
                var tick = service.TickOnceAsync(Ct);
                await claimReached.Task.WaitAsync(Ct);
                await RetireAsync();
                allowClaim.TrySetResult(true);
                await tick;
            }

            Assert.Empty(launches);
            var retained = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Queued, retained.State);
            Assert.Equal(QueueRetirement.Merged, retained.Retirement?.Kind);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_merged_retirement_that_wins_before_a_pre_launch_failure_preserves_the_lifecycle_row()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string tag = "retired-before-lock-failure";
            var lifecycle = Item(tag, role: "review") with
            {
                Stage = WorkStage.Review,
                Repository = "github.com/aer-works/baton",
                PullRequest = 2307,
                Workspace = home,
                Round = 2,
                LastVerdict = "C:\\fixtures\\verdict.json",
            };
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with { Items = [lifecycle] },
                Ct);

            var advancer = new WorkItemAdvancer(
                new MergedObservationGh(),
                (_, _) => Task.FromResult<string?>(null));
            var lockProbeReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLockProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var launches = new List<QueueLaunchRequest>();
            var service = new QueueSchedulerService(
                (request, _) =>
                {
                    launches.Add(request);
                    return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
                },
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UtcNow,
                workspaceLocks: _ =>
                {
                    lockProbeReached.TrySetResult(true);
                    releaseLockProbe.Task.GetAwaiter().GetResult();
                    return [Path.Combine(home, ".git", "index.lock")];
                });

            var tick = service.TickOnceAsync(Ct);
            await lockProbeReached.Task.WaitAsync(Ct);
            await advancer.RefreshPullRequestObservationsAsync(DateTimeOffset.UtcNow, Ct);
            releaseLockProbe.TrySetResult(true);
            await tick;

            Assert.Empty(launches);
            var retained = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueRetirement.Merged, retained.Retirement?.Kind);
            Assert.Equal(
                lifecycle with
                {
                    Retirement = retained.Retirement,
                    DispositionOperations = retained.DispositionOperations,
                },
                retained);
            var decisions = await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct);
            Assert.DoesNotContain(decisions, decision =>
                decision.Tag == tag && decision.Decision == QueueDecisionEntry.Failed);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_merged_observation_after_a_pre_launch_failure_without_refused_admission_preserves_the_failure()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string tag = "failure-before-merged-observation";
            var lifecycle = Item(tag, role: "review") with
            {
                Stage = WorkStage.Review,
                Repository = "github.com/aer-works/baton",
                PullRequest = 2307,
                Workspace = home,
                Round = 2,
                LastVerdict = "C:\\fixtures\\verdict.json",
            };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, state => state with { Items = [lifecycle] }, Ct);

            var failureCommitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var appendFailure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new QueueSchedulerService(
                (_, _) => throw new InvalidOperationException("the pre-launch failure must prevent launch"),
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UtcNow,
                afterFailureMutation: _ =>
                {
                    failureCommitted.TrySetResult(true);
                    return appendFailure.Task;
                },
                workspaceLocks: _ => [Path.Combine(home, ".git", "index.lock")]);
            var advancer = new WorkItemAdvancer(new MergedObservationGh(), (_, _) => Task.FromResult<string?>(null));

            var tick = service.TickOnceAsync(Ct);
            await failureCommitted.Task.WaitAsync(Ct);
            await advancer.RefreshPullRequestObservationsAsync(DateTimeOffset.UtcNow, Ct);
            appendFailure.TrySetResult(true);
            await tick;

            var retained = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Null(retained.Retirement);
            Assert.Equal(WorkStage.Review, retained.Stage);
            Assert.Equal(2, retained.Round);
            Assert.Equal(lifecycle.LastVerdict, retained.LastVerdict);
            Assert.Equal(QueueItemState.Failed, retained.State);
            Assert.Null(retained.RoomDirectory);
            Assert.Equal(TaskRequirementAdmission.Unknown, retained.LastAdmission?.Result);
            Assert.Contains("Git lock file", retained.Error!, StringComparison.Ordinal);
            var decisions = await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct);
            Assert.Contains(decisions, entry => entry.Tag == tag && entry.Decision == QueueDecisionEntry.Failed);
            Assert.DoesNotContain(decisions, entry => entry.Tag == tag
                && entry.Decision is QueueDecisionEntry.Retired or QueueDecisionEntry.Launched);
        }
        finally
        {
            Cleanup(home);
        }
    }

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
    public async Task A_code_attempt_persists_its_pre_launch_revision_before_the_worker_starts()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string baseRevision = "89abcdef0123456789abcdef0123456789abcdef";
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with { Items = [Item() with { Stage = WorkStage.Implement }] }, Ct);
            var headObserved = false;
            QueueLaunchRequest? launched = null;
            var service = new QueueSchedulerService(
                (request, _) =>
                {
                    Assert.True(headObserved);
                    launched = request;
                    return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
                },
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UtcNow,
                workspaceHead: (_, _) =>
                {
                    headObserved = true;
                    return Task.FromResult<string?>(baseRevision);
                });

            await service.TickOnceAsync(Ct);

            var persisted = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(baseRevision, persisted.AttemptBaseRevision);
            Assert.Equal(baseRevision, launched!.Item.AttemptBaseRevision);
            Assert.NotNull(persisted.AttemptId);
            Assert.Equal(persisted.AttemptId, launched.Item.AttemptId);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task First_lifecycle_launch_records_post_claim_wip_without_changing_selection_context()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile,
                state => state with { Items = [Item("first") with { Stage = WorkStage.Implement }] }, Ct);
            var service = Service((request, _) => Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory)));

            await service.TickOnceAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Launched, item.State);
            Assert.NotNull(item.AttemptId);
            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal(QueueDecisionEntry.Launched, fact.Decision);
            Assert.Equal(1, fact.ActiveLifecycles);
            Assert.Equal(1, fact.PrePullRequestLifecycles);
            Assert.Equal(0, fact.LiveReviews);
            Assert.Equal(["first"], fact.ConsumingLifecycles);
            Assert.Equal("first", fact.OldestOccupyingLifecycle);
            Assert.Equal("newwork", fact.PriorityBand);
            Assert.False(fact.PassedNewWorkHead);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task Admission_and_attempt_start_share_the_producer_owned_attempt_id()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()] }, Ct);
            var events = new List<FleetEventDraft>();
            var service = new QueueSchedulerService(
                (request, _) => Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory)),
                _ => Task.FromResult(0d),
                () => 16d,
                () => new DateTimeOffset(2026, 9, 11, 18, 0, 0, TimeSpan.Zero),
                appendFleetEvent: (draft, _) =>
                {
                    events.Add(draft);
                    return Task.FromResult<FleetEvent?>(null);
                });

            await service.TickOnceAsync(Ct);

            Assert.Collection(
                events,
                admission => Assert.Equal(FleetEventKind.AdmissionDecided, admission.Kind),
                started => Assert.Equal(FleetEventKind.AttemptStarted, started.Kind));
            Assert.NotNull(events[0].AttemptId);
            Assert.Equal(events[0].AttemptId, events[1].AttemptId);
            Assert.Equal(
                events[0].AttemptId,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).AttemptId);
            Assert.Null(events[1].ExecutionId);
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
    public async Task Graph_versioned_runway_hold_reuses_one_durable_unstarted_plan_on_the_next_tick()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var now = DateTimeOffset.Parse("2026-09-16T12:00:00Z");
            var graphItem = Item("graph-held") with
            {
                Stage = WorkStage.Implement,
                LifecycleGraphVersion = LifecycleAttemptGraph.Version,
                Workspace = home,
                AutomaticFixUsed = false,
                Requirements = [],
            };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [graphItem] }, Ct);

            var events = new List<FleetEvent>();
            Task<FleetEvent?> Append(FleetEventDraft draft, CancellationToken _)
            {
                if (events.Any(entry => entry.DedupeKey == draft.DedupeKey))
                {
                    return Task.FromResult<FleetEvent?>(null);
                }
                var entry = FleetEvent.From(events.Count + 1, draft);
                events.Add(entry);
                return Task.FromResult<FleetEvent?>(entry);
            }
            var launches = new List<QueueLaunchRequest>();
            var service = new QueueSchedulerService(
                (request, _) =>
                {
                    launches.Add(request);
                    return Task.FromResult(launches.Count == 1
                        ? new QueueLaunchOutcome(null, RunwayHeld: true)
                        : new QueueLaunchOutcome(request.RoomDirectory));
                },
                _ => Task.FromResult(0d), () => 16d, () => now,
                appendFleetEvent: Append,
                readFleetEvents: _ => Task.FromResult<IReadOnlyList<FleetEvent>>(events.ToList()),
                workspaceHead: (_, _) => Task.FromResult<string?>(new string('a', 40)),
                workspaceLocks: _ => []);

            await service.TickOnceAsync(Ct);
            var afterHold = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Queued, afterHold.State);
            var planned = Assert.Single(events, e => e.Kind == FleetEventKind.AttemptPlanned);
            Assert.DoesNotContain(events, e => e.Kind == FleetEventKind.AttemptStarted);

            now += TimeSpan.FromMinutes(4);
            await service.TickOnceAsync(Ct);

            var decisions = await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct);
            Assert.True(launches.Count == 2,
                $"launches={launches.Count}; decisions={string.Join(" | ", decisions.Select(d => $"{d.Decision}:{d.Reason}"))}; "
                + $"row={System.Text.Json.JsonSerializer.Serialize(Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items))}");
            Assert.Equal(planned.AttemptId, launches[0].Item.AttemptId);
            Assert.Equal(planned.AttemptId, launches[1].Item.AttemptId);
            Assert.Single(events, e => e.Kind == FleetEventKind.AttemptPlanned);
            Assert.Equal(planned.AttemptId, Assert.Single(events, e => e.Kind == FleetEventKind.AttemptStarted).AttemptId);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task Graph_versioned_attempt_without_declared_requirements_fails_before_vendor_spend()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = Item("graph-unknown-admission") with
            {
                Stage = WorkStage.Implement,
                LifecycleGraphVersion = LifecycleAttemptGraph.Version,
                Workspace = home,
            };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, state => state with { Items = [item] }, Ct);
            var events = new List<FleetEvent>();
            Task<FleetEvent?> Append(FleetEventDraft draft, CancellationToken _)
            {
                var entry = FleetEvent.From(events.Count + 1, draft);
                events.Add(entry);
                return Task.FromResult<FleetEvent?>(entry);
            }
            var launches = 0;
            var service = new QueueSchedulerService(
                (_, _) =>
                {
                    launches++;
                    return Task.FromResult(new QueueLaunchOutcome(null));
                },
                _ => Task.FromResult(0d), () => 16d, () => DateTimeOffset.UtcNow,
                appendFleetEvent: Append,
                readFleetEvents: _ => Task.FromResult<IReadOnlyList<FleetEvent>>(events.ToList()),
                workspaceHead: (_, _) => Task.FromResult<string?>(BaseHead),
                workspaceLocks: _ => []);

            await service.TickOnceAsync(Ct);

            Assert.Equal(0, launches);
            var failed = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, failed.State);
            Assert.Contains("require an explicit admitted requirements decision", failed.Error!, StringComparison.Ordinal);
            Assert.Contains(events, entry => entry.Kind == FleetEventKind.AdmissionDecided
                && entry.AdmissionDecision == TaskRequirementAdmission.Unknown);
            Assert.Contains(events, entry => entry.Kind == FleetEventKind.AttemptRefused);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_concurrent_conflicting_admission_winner_refuses_the_runway_held_frontier_before_launch()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = Item("admission-race") with
            {
                Stage = WorkStage.Implement,
                LifecycleGraphVersion = LifecycleAttemptGraph.Version,
                Workspace = home,
                Requirements = [],
            };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, state => state with { Items = [item] }, Ct);
            var events = new List<FleetEvent>();
            Task<FleetEvent?> Append(FleetEventDraft draft, CancellationToken _)
            {
                if (events.Any(entry => entry.DedupeKey == draft.DedupeKey))
                {
                    return Task.FromResult<FleetEvent?>(null);
                }
                // The other scheduler wins the admission dedupe key after this scheduler resolved
                // its tuple. The retained fact, not the local tuple, is now authoritative.
                var winner = draft.Kind == FleetEventKind.AdmissionDecided
                    ? draft with { Vendor = "conflicting-vendor" }
                    : draft;
                var entry = FleetEvent.From(events.Count + 1, winner);
                events.Add(entry);
                return Task.FromResult<FleetEvent?>(draft.Kind == FleetEventKind.AdmissionDecided ? null : entry);
            }
            var launches = 0;
            var service = new QueueSchedulerService(
                (_, _) =>
                {
                    launches++;
                    return Task.FromResult(new QueueLaunchOutcome(null, RunwayHeld: true));
                },
                _ => Task.FromResult(0d), () => 16d, () => DateTimeOffset.UtcNow,
                appendFleetEvent: Append,
                readFleetEvents: _ => Task.FromResult<IReadOnlyList<FleetEvent>>(events.ToList()),
                workspaceHead: (_, _) => Task.FromResult<string?>(BaseHead),
                workspaceLocks: _ => []);

            await service.TickOnceAsync(Ct);

            Assert.Equal(0, launches);
            var retained = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, retained.State);
            Assert.Contains("durable admission", retained.Error!, StringComparison.Ordinal);
            Assert.Contains(events, entry => entry.Kind == FleetEventKind.AdmissionDecided
                && entry.Vendor == "conflicting-vendor");
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task Graph_versioned_prelaunch_refusal_is_durable_and_is_not_retried_next_tick()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var now = DateTimeOffset.Parse("2026-09-16T12:00:00Z");
            var graphItem = Item("graph-refused") with
            {
                Stage = WorkStage.Implement,
                LifecycleGraphVersion = LifecycleAttemptGraph.Version,
                Workspace = home,
                AutomaticFixUsed = false,
                Requirements = [],
                Skills = [null!],
            };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [graphItem] }, Ct);

            var events = new List<FleetEvent>();
            Task<FleetEvent?> Append(FleetEventDraft draft, CancellationToken _)
            {
                var existing = events.SingleOrDefault(entry => entry.DedupeKey == draft.DedupeKey);
                if (existing is not null)
                {
                    return Task.FromResult<FleetEvent?>(null);
                }
                var entry = FleetEvent.From(events.Count + 1, draft);
                events.Add(entry);
                return Task.FromResult<FleetEvent?>(entry);
            }
            var launches = 0;
            var service = new QueueSchedulerService(
                (_, _) =>
                {
                    launches++;
                    return Task.FromResult(new QueueLaunchOutcome(null));
                },
                _ => Task.FromResult(0d), () => 16d, () => now,
                appendFleetEvent: Append,
                readFleetEvents: _ => Task.FromResult<IReadOnlyList<FleetEvent>>(events.ToList()),
                workspaceHead: (_, _) => Task.FromResult<string?>(new string('a', 40)),
                workspaceLocks: _ => []);

            await service.TickOnceAsync(Ct);
            now += TimeSpan.FromMinutes(4);
            await service.TickOnceAsync(Ct);

            Assert.Equal(0, launches);
            Assert.Single(events, entry => entry.Kind == FleetEventKind.AttemptPlanned);
            Assert.Single(events, entry => entry.Kind == FleetEventKind.AttemptRefused);
            Assert.DoesNotContain(events, entry => entry.Kind == FleetEventKind.AttemptStarted);
            var refused = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, refused.State);
            Assert.True(refused.Halted);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task Production_scheduler_materializes_blocked_review_as_exact_fix_frontier()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var now = DateTimeOffset.Parse("2026-09-16T12:00:00Z");
            var item = GraphItem("graph-fix", home, ReviewHead, now);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, state => state with { Items = [item] }, Ct);
            var events = BlockingReviewHistory(item, now);
            var launches = new List<QueueLaunchRequest>();
            var service = GraphService(events, now, request =>
            {
                launches.Add(request);
                return new QueueLaunchOutcome(request.RoomDirectory);
            });

            await service.TickOnceAsync(Ct);

            Assert.True(launches.Count == 1,
                $"events={System.Text.Json.JsonSerializer.Serialize(events)}; "
                + $"row={System.Text.Json.JsonSerializer.Serialize(Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items))}");
            var launch = launches[0];
            Assert.Equal(WorkStage.Fix, launch.Item.Stage);
            var plan = Assert.Single(events, entry =>
                entry.Kind == FleetEventKind.AttemptPlanned && entry.LifecycleStage == WorkStage.Fix);
            Assert.Equal(plan.AttemptId, launch.Item.AttemptId);
            Assert.Equal(new FleetRevisionId(ReviewHead), plan.InputRevisionId);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task Production_scheduler_materializes_delivered_fix_as_exact_rereview_frontier()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var now = DateTimeOffset.Parse("2026-09-16T12:00:00Z");
            var item = GraphItem("graph-rereview", home, FixedHead, now);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, state => state with { Items = [item] }, Ct);
            var events = DeliveredFixHistory(item, now);
            var launches = new List<QueueLaunchRequest>();
            var service = GraphService(events, now, request =>
            {
                launches.Add(request);
                return new QueueLaunchOutcome(request.RoomDirectory);
            });

            await service.TickOnceAsync(Ct);

            Assert.True(launches.Count == 1,
                $"events={System.Text.Json.JsonSerializer.Serialize(events)}; "
                + $"row={System.Text.Json.JsonSerializer.Serialize(Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items))}");
            var launch = launches[0];
            Assert.Equal(WorkStage.ReReview, launch.Item.Stage);
            var plan = Assert.Single(events, entry =>
                entry.Kind == FleetEventKind.AttemptPlanned && entry.LifecycleStage == WorkStage.ReReview);
            Assert.Equal(plan.AttemptId, launch.Item.AttemptId);
            Assert.Equal(new FleetRevisionId(FixedHead), plan.InputRevisionId);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task Concurrent_scheduler_ticks_converge_on_one_plan_and_one_launch()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var now = DateTimeOffset.Parse("2026-09-16T12:00:00Z");
            var item = Item("graph-concurrent") with
            {
                Stage = WorkStage.Implement,
                LifecycleGraphVersion = LifecycleAttemptGraph.Version,
                Workspace = home,
                AutomaticFixUsed = false,
                Requirements = [],
            };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, state => state with { Items = [item] }, Ct);

            var events = new List<FleetEvent>();
            var eventLock = new object();
            var initialReads = 0;
            var bothInitialReads = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<IReadOnlyList<FleetEvent>> Read(CancellationToken _)
            {
                var ordinal = Interlocked.Increment(ref initialReads);
                if (ordinal <= 2)
                {
                    if (ordinal == 2)
                    {
                        bothInitialReads.TrySetResult(true);
                    }
                    await bothInitialReads.Task;
                }
                lock (eventLock)
                {
                    return events.ToList();
                }
            }
            Task<FleetEvent?> Append(FleetEventDraft draft, CancellationToken _)
            {
                lock (eventLock)
                {
                    if (events.Any(entry => entry.DedupeKey == draft.DedupeKey))
                    {
                        return Task.FromResult<FleetEvent?>(null);
                    }
                    var entry = FleetEvent.From(events.Count + 1, draft);
                    events.Add(entry);
                    return Task.FromResult<FleetEvent?>(entry);
                }
            }
            var launches = new List<QueueLaunchRequest>();
            var launchLock = new object();
            QueueSchedulerService Service() => new(
                (request, _) =>
                {
                    lock (launchLock)
                    {
                        launches.Add(request);
                    }
                    return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
                },
                _ => Task.FromResult(0d), () => 16d, () => now,
                appendFleetEvent: Append,
                readFleetEvents: Read,
                workspaceHead: (_, _) => Task.FromResult<string?>(BaseHead),
                workspaceLocks: _ => []);

            await Task.WhenAll(Service().TickOnceAsync(Ct), Service().TickOnceAsync(Ct));

            Assert.Single(events, entry => entry.Kind == FleetEventKind.AttemptPlanned);
            Assert.Single(events, entry => entry.Kind == FleetEventKind.AttemptStarted);
            Assert.Single(launches);
            Assert.Equal(events.Single(entry => entry.Kind == FleetEventKind.AttemptPlanned).AttemptId,
                launches[0].Item.AttemptId);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_stale_scheduler_claim_cannot_replace_a_same_stage_successor_frontier()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var now = DateTimeOffset.Parse("2026-09-16T12:00:00Z");
            var item = Item("graph-frontier-race") with
            {
                Stage = WorkStage.Continue,
                LifecycleGraphVersion = LifecycleAttemptGraph.Version,
                Workspace = home,
                AutomaticFixUsed = false,
                Requirements = [],
            };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, state => state with { Items = [item] }, Ct);

            var events = new List<FleetEvent>();
            var implement = new LifecycleNextAttempt(WorkStage.Implement, new FleetRevisionId(BaseHead), [], "root");
            var implementId = LifecycleAttemptGraph.PlanAttemptId(item, implement);
            var continuation = new LifecycleNextAttempt(WorkStage.Continue, new FleetRevisionId(BaseHead),
                [new FleetAttemptEdge(implementId, FleetAttemptEdgeKind.Continues)], "continue");
            var continuationId = LifecycleAttemptGraph.PlanAttemptId(item, continuation);
            events.Add(FleetEvent.From(1, LifecycleAttemptGraph.PlanEvent(item, implementId, implement, now)));
            AddCompleteExecution(events, item, implementId, WorkStage.Implement, BaseHead, now,
                WorkflowOutcome.Failed, workspaceChanged: true);
            events.Add(FleetEvent.From(events.Count + 1,
                LifecycleAttemptGraph.PlanEvent(item, continuationId, continuation, now)));
            Task<FleetEvent?> Append(FleetEventDraft draft, CancellationToken _)
            {
                if (events.Any(entry => entry.DedupeKey == draft.DedupeKey))
                {
                    return Task.FromResult<FleetEvent?>(null);
                }
                var entry = FleetEvent.From(events.Count + 1, draft);
                events.Add(entry);
                return Task.FromResult<FleetEvent?>(entry);
            }
            var launches = 0;
            var service = new QueueSchedulerService(
                (_, _) =>
                {
                    launches++;
                    return Task.FromResult(new QueueLaunchOutcome(null));
                },
                _ => Task.FromResult(0d), () => 16d, () => now,
                appendFleetEvent: Append,
                readFleetEvents: _ => Task.FromResult<IReadOnlyList<FleetEvent>>(events.ToList()),
                workspaceHead: (_, _) => Task.FromResult<string?>(BaseHead),
                workspaceLocks: _ => [],
                beforeLaunchClaim: async _ =>
                {
                    // Another scheduler advanced N to N+1 while retaining the same `continue`
                    // stage. Tag/state/declaration still match; only the immutable frontier does not.
                    await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
                    {
                        Items = snapshot.Items.Select(current => current.Tag == item.Tag
                            ? current with
                            {
                                AttemptId = new FleetAttemptId("continue-n-plus-one"),
                                AttemptBaseRevision = "cccccccccccccccccccccccccccccccccccccccc",
                            }
                            : current).ToList(),
                    }, Ct);
                });

            await service.TickOnceAsync(Ct);

            Assert.Equal(0, launches);
            var current = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(new FleetAttemptId("continue-n-plus-one"), current.AttemptId);
            Assert.Equal("cccccccccccccccccccccccccccccccccccccccc", current.AttemptBaseRevision);
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

    /// <summary>
    /// #2248: <see cref="DeadPumpProbe"/> deliberately records only the room's terminal journal fact,
    /// never <c>terminal.json</c>. Done detection must still consume that authoritative projection or
    /// an adopted lane whose engine died stays launched forever.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Done_detection_only_reconciles_a_terminal_journal_without_a_sentinel_for_the_dead_pump(
        bool deadPumpDiagnostic)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = Path.Combine(home, "rooms", "queue-dead-pump");
            Directory.CreateDirectory(room);
            var stepId = new StepId("implement");
            var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
                new WorkflowTemplateId("dead-pump-queue-reconciliation"),
                1,
                [new WorkflowStepDefinition(stepId, "implement", [], ["changes.md"], [], new RetryPolicy(1))]));
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, BatonPaths.SnapshotFileName), Ct);

            var executionId = new ExecutionId("execution-dead-pump");
            await using (var writer = new FlowEventLogWriter(Path.Combine(room, BatonPaths.FlowLogFileName)))
            {
                await writer.AppendAsync(
                    new FlowEvent.ExecutionRequestAccepted(
                        new ExecutionRequest(
                            executionId,
                            new WorkflowId("workflow-dead-pump"),
                            stepId,
                            "implement",
                            Inputs: [],
                            Outputs: ["changes.md"],
                            Timeout: TimeSpan.FromMinutes(30),
                            Environment: [],
                            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>()),
                        EnginePid: 999_999,
                        EngineStartTime: null),
                    Ct);
                await writer.AppendAsync(
                    new FlowEvent.ExecutionFailed(
                        executionId,
                        FailureClassification.Permanent,
                        deadPumpDiagnostic
                            ? $"{DeadPumpProbe.FailureReasonPrefix} recorded by dead-pump probe"
                            : "ordinary permanent failure still completing terminal finalization"),
                    Ct);
            }

            var attemptId = new FleetAttemptId("attempt-dead-pump");
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items = [Item() with
                    {
                        State = QueueItemState.Launched,
                        RoomDirectory = room,
                        AttemptId = attemptId,
                    }],
                },
                Ct);
            var events = new List<FleetEventDraft>();
            var service = new QueueSchedulerService(
                (_, _) => Task.FromResult(new QueueLaunchOutcome(null)),
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UtcNow,
                appendFleetEvent: (draft, _) =>
                {
                    events.Add(draft);
                    return Task.FromResult<FleetEvent?>(null);
                });

            await service.ResolveFinishedItemsAsync(Ct);

            Assert.False(File.Exists(Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName)));
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            if (deadPumpDiagnostic)
            {
                Assert.Equal(QueueItemState.Failed, item.State);
                Assert.Contains("settled Failed", item.Error, StringComparison.Ordinal);
                var settled = Assert.Single(events, e => e.Kind == FleetEventKind.AttemptSettled);
                Assert.Equal(attemptId, settled.AttemptId);
                Assert.Equal(executionId, settled.ExecutionId);
            }
            else
            {
                Assert.Equal(QueueItemState.Launched, item.State);
                Assert.DoesNotContain(events, e => e.Kind == FleetEventKind.AttemptSettled);
            }
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task Terminal_settlement_records_only_status_owned_usage_and_identifiers_before_closing_the_item()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = Path.Combine(home, "rooms", "queue-t1-events");
            Directory.CreateDirectory(room);
            await File.WriteAllTextAsync(
                Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName),
                """{"state":"Succeeded","steps":[{"id":"implement","state":"Succeeded","execution":"execution-1","usage":{"wallClockMs":1234,"tokensIn":17,"toolSteps":4,"refusedToolSteps":1,"repeatedToolSteps":2}}],"outputs":["artifact.md"],"error":"terminal detail"}""",
                Ct);
            var attemptId = new FleetAttemptId("attempt-1");
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items =
                    [
                        Item() with
                        {
                            State = QueueItemState.Launched,
                            RoomDirectory = room,
                            LaunchedAt = new DateTimeOffset(2026, 9, 11, 17, 0, 0, TimeSpan.Zero),
                            AttemptId = attemptId,
                            LastAdmission = new TaskRequirementAdmission([], ["file-write"], TaskRequirementAdmission.Admitted),
                        },
                    ],
                },
                Ct);
            var events = new List<FleetEventDraft>();
            var service = new QueueSchedulerService(
                (_, _) => Task.FromResult(new QueueLaunchOutcome(null)),
                _ => Task.FromResult(0d),
                () => 16d,
                () => new DateTimeOffset(2026, 9, 11, 18, 0, 0, TimeSpan.Zero),
                appendFleetEvent: (draft, _) =>
                {
                    events.Add(draft);
                    return Task.FromResult<FleetEvent?>(null);
                });

            await service.ResolveFinishedItemsAsync(Ct);

            var settled = Assert.Single(events, e => e.Kind == FleetEventKind.AttemptSettled);
            Assert.Equal(attemptId, settled.AttemptId);
            Assert.Equal(new ExecutionId("execution-1"), settled.ExecutionId);
            Assert.Equal(1234, settled.ElapsedMilliseconds);
            Assert.Equal(17, settled.Usage!.InputTokens);
            Assert.Equal(4, settled.Usage.ToolSteps);
            Assert.Equal(1, settled.Usage.RefusedToolSteps);
            Assert.Equal(2, settled.Usage.RepeatedToolSteps);
            Assert.Equal(["artifact.md"], settled.ArtifactReferences);
            Assert.Equal("terminal detail", settled.OutcomeDetail);
            Assert.Equal(
                QueueItemState.Done,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).State);
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

    private static QueueItem GraphItem(string tag, string workspace, string head, DateTimeOffset observedAt) =>
        Item(tag) with
        {
            Stage = WorkStage.Review,
            State = QueueItemState.Queued,
            LifecycleGraphVersion = LifecycleAttemptGraph.Version,
            Workspace = workspace,
            PullRequest = 42,
            Checks = PullRequestChecks.Passing,
            ChecksHeadSha = head,
            ChecksObservedAt = observedAt,
            LifecyclePullRequestEvidence = new(42, head, true, true, true, observedAt),
            AutomaticFixUsed = false,
            Requirements = [],
        };

    private static List<FleetEvent> BlockingReviewHistory(QueueItem item, DateTimeOffset at)
    {
        var events = new List<FleetEvent>();
        void Add(FleetEventDraft draft) => events.Add(FleetEvent.From(events.Count + 1, draft));
        var work = new FleetWorkId(item.Tag);
        var implement = new FleetAttemptId("implement");
        var review = new FleetAttemptId("review");
        Add(LifecycleAttemptGraph.PlanEvent(item, implement,
            new(WorkStage.Implement, new FleetRevisionId(BaseHead), [], "initial"), at));
        AddCompleteExecution(events, item, implement, WorkStage.Implement, BaseHead, at);
        Add(new(FleetEventKind.RevisionProduced, "revision:implement", at,
            AttemptId: implement, WorkId: work, RevisionId: new FleetRevisionId(ReviewHead)));
        Add(LifecycleAttemptGraph.PlanEvent(item, review,
            new(WorkStage.Review, new FleetRevisionId(ReviewHead),
                [new FleetAttemptEdge(implement, FleetAttemptEdgeKind.Reviews)], "review"), at));
        AddCompleteExecution(events, item, review, WorkStage.Review, ReviewHead, at);
        Add(new(FleetEventKind.ReviewVerdictObserved, "verdict:review", at,
            AttemptId: review, WorkId: work, RevisionId: new FleetRevisionId(ReviewHead),
            ReviewVerdict: "block"));
        return events;
    }

    private static List<FleetEvent> DeliveredFixHistory(QueueItem item, DateTimeOffset at)
    {
        var events = BlockingReviewHistory(item, at);
        var work = new FleetWorkId(item.Tag);
        var review = new FleetAttemptId("review");
        var fix = new FleetAttemptId("fix");
        void Add(FleetEventDraft draft) => events.Add(FleetEvent.From(events.Count + 1, draft));
        Add(LifecycleAttemptGraph.PlanEvent(item, fix,
            new(WorkStage.Fix, new FleetRevisionId(ReviewHead),
                [new FleetAttemptEdge(review, FleetAttemptEdgeKind.Repairs)], "fix"), at));
        AddCompleteExecution(events, item, fix, WorkStage.Fix, ReviewHead, at);
        Add(new(FleetEventKind.RevisionProduced, "revision:fix", at,
            AttemptId: fix, WorkId: work, RevisionId: new FleetRevisionId(FixedHead)));
        return events;
    }

    private static void AddCompleteExecution(
        List<FleetEvent> events,
        QueueItem item,
        FleetAttemptId attempt,
        WorkStage stage,
        string input,
        DateTimeOffset at,
        string outcome = WorkflowOutcome.Succeeded,
        bool? workspaceChanged = null)
    {
        void Add(FleetEventDraft draft) => events.Add(FleetEvent.From(events.Count + 1, draft));
        var work = new FleetWorkId(item.Tag);
        IReadOnlyList<string> grant = ["file-write"];
        var room = new FleetRoomId($"room:{attempt.Value}");
        Add(new(FleetEventKind.AdmissionDecided, $"admission:{attempt.Value}", at,
            AttemptId: attempt, WorkId: work, LifecycleStage: stage, InputRevisionId: new FleetRevisionId(input),
            Vendor: "claude", Model: "opus", Effort: "high", DeclaredRole: "implement",
            EffectiveGrant: grant, AdmissionDecision: TaskRequirementAdmission.Admitted));
        Add(new(FleetEventKind.AttemptStarted, $"started:{attempt.Value}", at,
            AttemptId: attempt, WorkId: work, LifecycleStage: stage, InputRevisionId: new FleetRevisionId(input),
            RoomId: room, Vendor: "claude", Model: "opus", Effort: "high", DeclaredRole: "implement",
            EffectiveGrant: grant));
        Add(new(FleetEventKind.AttemptSettled, $"settled:{attempt.Value}", at,
            AttemptId: attempt, WorkId: work, LifecycleStage: stage, InputRevisionId: new FleetRevisionId(input),
            RoomId: room, Vendor: "claude", Model: "opus", Effort: "high", DeclaredRole: "implement",
            EffectiveGrant: grant, Outcome: outcome, WorkspaceChanged: workspaceChanged));
    }

    private static QueueSchedulerService GraphService(
        List<FleetEvent> events,
        DateTimeOffset now,
        Func<QueueLaunchRequest, QueueLaunchOutcome> launch)
    {
        Task<FleetEvent?> Append(FleetEventDraft draft, CancellationToken _)
        {
            if (events.Any(entry => entry.DedupeKey == draft.DedupeKey))
            {
                return Task.FromResult<FleetEvent?>(null);
            }
            var entry = FleetEvent.From(events.Count + 1, draft);
            events.Add(entry);
            return Task.FromResult<FleetEvent?>(entry);
        }

        return new QueueSchedulerService(
            (request, _) => Task.FromResult(launch(request)),
            _ => Task.FromResult(0d), () => 16d, () => now,
            appendFleetEvent: Append,
            readFleetEvents: _ => Task.FromResult<IReadOnlyList<FleetEvent>>(events.ToList()),
            workspaceHead: (_, _) => Task.FromResult<string?>(BaseHead),
            workspaceLocks: _ => []);
    }

    private static void Cleanup(string home) => DirectoryCleanup.DeleteRecursively(home);
}
