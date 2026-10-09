using Baton.Cli.Daemon;
using Baton.Accounting;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using Baton.Vendors;

namespace Baton.Cli.Tests.Daemon;

public sealed class QueuedReviewLaunchTests
{
    private const string OldHead = "1111111111111111111111111111111111111111";
    private const string CurrentHead = "2222222222222222222222222222222222222222";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(WorkStage.Review)]
    [InlineData(WorkStage.ReReview)]
    public async Task A_review_queued_before_a_hold_uses_the_current_revision_at_launch(WorkStage stage)
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_review_launch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = new QueueItem
            {
                Tag = "held-review",
                Role = "review",
                Stage = stage,
                Repository = "github.com/philipreese/baton",
                Branch = "2566-lane",
                PullRequest = 2567,
                Issue = 2566,
                Workspace = home,
                SpecFile = Path.Combine(home, "queued-review.md"),
                Instructions = "Check the requested change against its issue.",
                LastVerdict = stage == WorkStage.ReReview ? Path.Combine(home, "prior-verdict.json") : null,
                Round = 3,
                AutomaticFixUsed = true,
            };
            if (item.LastVerdict is { } priorVerdict)
            {
                File.WriteAllText(priorVerdict, PriorVerdict);
            }
            var oldBrief = QueueBriefTemplates.Compose(stage, item, new(
                Do: item.Instructions, PullRequest: item.PullRequest, HeadSha: OldHead, Round: item.Round));
            File.WriteAllText(item.SpecFile, oldBrief);
            await QueueStore.MutateAsync(BatonPaths.QueueFile,
                snapshot => snapshot with { Held = true, Items = [item] }, Ct);

            string? received = null;
            var launches = 0;
            var service = new QueueSchedulerService(
                (request, _) =>
                {
                    launches++;
                    received = File.ReadAllText(request.Item.SpecFile);
                    return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
                },
                _ => Task.FromResult(0d), () => 16d, () => DateTimeOffset.UtcNow,
                advancer: new WorkItemAdvancer(new CurrentPullRequest(),
                    (_, _) => Task.FromResult<string?>(CurrentHead)),
                workspaceLocks: _ => []);

            await service.TickOnceAsync(Ct);
            Assert.Equal(0, launches);
            Assert.Equal(oldBrief, File.ReadAllText(item.SpecFile));
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with { Held = false }, Ct);
            await service.TickOnceAsync(Ct);

            Assert.Equal(1, launches);
            Assert.NotNull(received);
            Assert.Contains(CurrentHead, received, StringComparison.Ordinal);
            Assert.DoesNotContain(OldHead, received, StringComparison.Ordinal);
            var retained = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Launched, retained.State);
            Assert.Equal(item.Round, retained.Round);
            Assert.Equal(item.AutomaticFixUsed, retained.AutomaticFixUsed);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData(WorkStage.Review)]
    [InlineData(WorkStage.ReReview)]
    public async Task A_review_already_at_the_current_head_launches_with_that_same_head(WorkStage stage)
    {
        var home = CreateHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = ReviewItem(home, "unchanged-head", stage);
            var brief = QueueBriefTemplates.Compose(stage, item, new(
                Do: item.Instructions!, PullRequest: item.PullRequest, HeadSha: CurrentHead, Round: item.Round));
            File.WriteAllText(item.SpecFile, brief);
            await QueueStore.MutateAsync(BatonPaths.QueueFile,
                snapshot => snapshot with { Items = [item] }, Ct);

            QueueLaunchRequest? launch = null;
            var service = Service(home, new CurrentPullRequest(), (request, _) =>
            {
                return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
            }, request => launch = request);

            await service.TickOnceAsync(Ct);

            Assert.NotNull(launch);
            Assert.Contains(CurrentHead, File.ReadAllText(item.SpecFile), StringComparison.Ordinal);
            Assert.DoesNotContain(OldHead, File.ReadAllText(item.SpecFile), StringComparison.Ordinal);
            Assert.Equal(CurrentHead, ExtractRequiredHead(File.ReadAllText(launch.Item.SpecFile)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData(WorkStage.Review, "review")]
    [InlineData(WorkStage.ReReview, "re-review")]
    public async Task A_custom_review_template_is_preserved_and_unknown_tokens_reach_the_launched_brief(
        WorkStage stage, string templateName)
    {
        var home = CreateHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = ReviewItem(home, "custom-template", stage,
                stage == WorkStage.ReReview ? Path.Combine(home, "prior-verdict.json") : null);
            QueueBriefTemplates.EnsureMaterialized();
            var templatePath = Path.Combine(BatonPaths.QueueTemplatesDirectory, templateName + ".md");
            const string customTemplate = "Re-review PR #{{PR}} at {{SHA}}\nPrior={{FINDINGS}}\nUnknown={{UNKNOWN_REVIEW_TOKEN}}\n";
            File.WriteAllText(templatePath, customTemplate);
            var templateBytes = File.ReadAllBytes(templatePath);
            File.WriteAllText(item.SpecFile, QueueBriefTemplates.Compose(stage, item, new(
                Do: item.Instructions!, PullRequest: item.PullRequest, HeadSha: OldHead, Round: item.Round)));
            await QueueStore.MutateAsync(BatonPaths.QueueFile,
                snapshot => snapshot with { Items = [item] }, Ct);

            string? received = null;
            var service = Service(home, new CurrentPullRequest(), (request, _) =>
            {
                received = File.ReadAllText(request.Item.SpecFile);
                return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
            });

            await service.TickOnceAsync(Ct);

            Assert.NotNull(received);
            Assert.Contains(CurrentHead, received, StringComparison.Ordinal);
            Assert.DoesNotContain(OldHead, received, StringComparison.Ordinal);
            Assert.Contains("{{UNKNOWN_REVIEW_TOKEN}}", received, StringComparison.Ordinal);
            if (stage == WorkStage.ReReview)
            {
                Assert.Contains("Re-review PR #2567 at " + CurrentHead, received, StringComparison.Ordinal);
                Assert.Contains("prior", received, StringComparison.Ordinal);
            }
            Assert.Equal(templateBytes, File.ReadAllBytes(templatePath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("failing")]
    [InlineData("unknown")]
    public async Task Non_terminal_or_untrusted_check_evidence_does_not_refuse_review_admission(string checks)
    {
        var home = CreateHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = ReviewItem(home, "check-" + checks, WorkStage.Review);
            File.WriteAllText(item.SpecFile, QueueBriefTemplates.Compose(item.Stage!.Value, item, new(
                Do: item.Instructions!, PullRequest: item.PullRequest, HeadSha: OldHead, Round: item.Round)));
            await QueueStore.MutateAsync(BatonPaths.QueueFile,
                snapshot => snapshot with { Items = [item] }, Ct);

            var probe = new CurrentPullRequest(checks: checks);
            var reading = await new RequiredCheckEvidenceReader((args, token) => probe.RunAsync(home, args, token))
                .ReadAsync(item.Repository!, CurrentHead, Ct);
            if (checks == "unknown")
            {
                Assert.Null(reading.State);
                Assert.NotNull(reading.Error);
            }
            else
            {
                Assert.Equal(checks == "pending" ? PullRequestChecks.Pending : PullRequestChecks.Failing, reading.State);
                Assert.Null(reading.Error);
                Assert.NotNull(reading.Evidence);
            }

            QueueLaunchRequest? launch = null;
            var service = Service(home, probe, (request, _) =>
            {
                launch = request;
                return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
            });

            await service.TickOnceAsync(Ct);

            Assert.NotNull(launch);
            Assert.Equal(CurrentHead, ExtractRequiredHead(File.ReadAllText(item.SpecFile)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData("unknown-pr")]
    [InlineData("short-head")]
    [InlineData("workspace-mismatch")]
    public async Task Invalid_current_review_evidence_refuses_before_launch_without_changing_round_or_fix_budget(
        string evidence)
    {
        var home = CreateHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = ReviewItem(home, "refused-evidence", WorkStage.Review) with
            {
                Round = 8,
                AutomaticFixUsed = true,
            };
            var oldBrief = QueueBriefTemplates.Compose(item.Stage!.Value, item, new(
                Do: item.Instructions!, PullRequest: item.PullRequest, HeadSha: OldHead, Round: item.Round));
            File.WriteAllText(item.SpecFile, oldBrief);
            await QueueStore.MutateAsync(BatonPaths.QueueFile,
                snapshot => snapshot with { Items = [item] }, Ct);

            var probe = new CurrentPullRequest(evidence == "unknown-pr" ? "unknown" : null,
                evidence == "short-head" ? "short" : CurrentHead);
            var launches = 0;
            var service = Service(home, probe, (_, _) =>
            {
                launches++;
                return Task.FromResult(new QueueLaunchOutcome(home));
            }, workspaceHead: evidence == "workspace-mismatch" ? OldHead : CurrentHead);

            await service.TickOnceAsync(Ct);

            Assert.Equal(0, launches);
            Assert.Equal(oldBrief, File.ReadAllText(item.SpecFile));
            var retained = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.NotEqual(QueueItemState.Launched, retained.State);
            Assert.Equal(item.Round, retained.Round);
            Assert.Equal(item.AutomaticFixUsed, retained.AutomaticFixUsed);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData("retained", true)]
    [InlineData("replacement", true)]
    [InlineData("missing", true)]
    [InlineData("copy-io", true)]
    [InlineData("copy-access", true)]
    [InlineData("unrelated", false)]
    public async Task Corrupt_retained_review_handoff_halts_once_without_launching_or_spending_a_round(
        string refusalCase, bool handoffInvalid)
    {
        var home = CreateHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = ReviewItem(home, "corrupt-handoff", WorkStage.Review) with
            {
                Round = 7,
                AutomaticFixUsed = true,
                SettledWorkerAccount = new QueueWorkerAccount(
                    System.Text.Encoding.UTF8.GetBytes("corrupt"),
                    refusalCase != "retained"
                        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("corrupt"))).ToLowerInvariant()
                        : new string('0', 64),
                    new FleetAttemptId("source-attempt"),
                    "source-execution",
                    "github.com/philipreese/baton",
                    home),
            };
            File.WriteAllText(item.SpecFile, QueueBriefTemplates.Compose(item.Stage!.Value, item, new(
                Do: item.Instructions!, PullRequest: item.PullRequest, HeadSha: OldHead, Round: item.Round)));
            await QueueStore.MutateAsync(BatonPaths.QueueFile,
                snapshot => snapshot with { Items = [item] }, Ct);

            var launches = 0;
            var materializations = 0;
            var service = Service(home, new CurrentPullRequest(), async (request, _) =>
            {
                var options = QueueLauncher.BuildOptions(request);
                materializations++;
                var staging = Assert.Single(options.Attachments!);
                if (refusalCase == "replacement") File.WriteAllText(staging, "replacement bytes after validation");
                if (refusalCase is "missing" or "copy-io" or "copy-access") Baton.Tests.Shared.FileCleanup.EnsureDeleted(staging);
                if (refusalCase == "copy-access") Directory.CreateDirectory(staging);
                try
                {
                    if (refusalCase == "unrelated") throw new CliArgumentException("unrelated child refusal");
                    if (refusalCase == "missing")
                        RoleSpecMaterializer.Materialize(WorkerRoleCatalog.For("review"), "review", "codex",
                            request.Item.Workspace, "fixture-model", "medium", null, null, options.Attachments,
                            options.RoomDirectoryPath, null, null, null, null,
                            reviewHandoffSha256: options.ReviewHandoffSha256);
                    RoleSpecMaterializer.CopyAttachmentsIntoRoom(options.Attachments, options.RoomDirectoryPath, options.ReviewHandoffSha256);
                }
                catch (CliArgumentException ex)
                {
                    await TerminalSentinelWriter.TryWriteValidationRefusedAsync(options.RoomDirectoryPath,
                        ex.Message, Ct, reviewHandoffInvalid: ex.ReviewHandoffInvalid);
                    if (refusalCase is "missing" or "copy-io" or "copy-access" or "unrelated")
                        Assert.False(File.Exists(Path.Combine(options.RoomDirectoryPath, "artifacts", "attachments", "changes.md")));
                    return QueueLauncher.ClassifyReviewHandoffRefusal(
                        new QueueLaunchOutcome(null, Error: ex.Message), options.RoomDirectoryPath, options.ReviewHandoffSha256,
                        await TerminalSentinelWriter.TryReadAsync(options.RoomDirectoryPath, Ct));
                }
                launches++;
                return new QueueLaunchOutcome(request.RoomDirectory);
            });

            await service.TickOnceAsync(Ct);
            var halted = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(0, launches);
            Assert.Equal(handoffInvalid, halted.Halted);
            Assert.Equal(QueueItemState.Failed, halted.State);
            Assert.Equal(7, halted.Round);
            Assert.True(halted.AutomaticFixUsed);
            Assert.Equal(handoffInvalid, halted.Error!.Contains("review-handoff-invalid", StringComparison.Ordinal));

            await service.TickOnceAsync(Ct);
            Assert.Equal(0, launches);
            Assert.Equal(refusalCase != "retained" ? 1 : 0, materializations);
            var second = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(handoffInvalid, second.Halted);
            Assert.Equal(halted.State, second.State);
            Assert.Equal(halted.Round, second.Round);
            Assert.Equal(halted.AutomaticFixUsed, second.AutomaticFixUsed);
            Assert.Equal(halted.Error, second.Error);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_review_brief_write_failure_does_not_claim_or_launch_the_row()
    {
        var home = CreateHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = ReviewItem(home, "spec-write-failure", WorkStage.Review);
            Directory.CreateDirectory(item.SpecFile);
            await QueueStore.MutateAsync(BatonPaths.QueueFile,
                snapshot => snapshot with { Items = [item] }, Ct);
            var launches = 0;
            var service = Service(home, new CurrentPullRequest(), (request, _) =>
            {
                launches++;
                return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
            });

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.TickOnceAsync(Ct));

            Assert.Equal(0, launches);
            Assert.True(Directory.Exists(item.SpecFile));
            Assert.NotEqual(QueueItemState.Launched,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData("drift")]
    [InlineData("unavailable")]
    public async Task Missing_or_drifted_repository_identity_refuses_before_brief_replacement(string identityState)
    {
        var home = CreateHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = ReviewItem(home, "identity-" + identityState, WorkStage.Review);
            var oldBrief = QueueBriefTemplates.Compose(item.Stage!.Value, item, new(
                Do: item.Instructions!, PullRequest: item.PullRequest, HeadSha: OldHead, Round: item.Round));
            File.WriteAllText(item.SpecFile, oldBrief);
            await QueueStore.MutateAsync(BatonPaths.QueueFile,
                snapshot => snapshot with { Items = [item] }, Ct);
            var launches = 0;
            var currentIdentity = identityState == "drift"
                ? RepositoryIdentity.From("https://github.com/other-owner/other-repo.git", null)
                : null;
            var service = Service(home, new CurrentPullRequest(), (request, _) =>
            {
                launches++;
                return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
            }, repositoryIdentity: (_, _) => Task.FromResult(currentIdentity));

            await service.TickOnceAsync(Ct);

            Assert.Equal(0, launches);
            Assert.Equal(oldBrief, File.ReadAllText(item.SpecFile));
            Assert.NotEqual(QueueItemState.Launched,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_hold_or_row_replacement_before_claim_preserves_the_old_brief_and_prevents_launch(bool hold)
    {
        var home = CreateHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = ReviewItem(home, hold ? "claim-hold" : "claim-replacement", WorkStage.Review);
            var oldBrief = QueueBriefTemplates.Compose(item.Stage!.Value, item, new(
                Do: item.Instructions!, PullRequest: item.PullRequest, HeadSha: OldHead, Round: item.Round));
            File.WriteAllText(item.SpecFile, oldBrief);
            await QueueStore.MutateAsync(BatonPaths.QueueFile,
                snapshot => snapshot with { Items = [item] }, Ct);

            var launches = 0;
            var service = Service(home, new CurrentPullRequest(), (request, _) =>
            {
                launches++;
                return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
            }, beforeLaunchClaim: _ => QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Held = hold,
                Items = hold
                    ? snapshot.Items
                    : snapshot.Items.Select(current => current with
                    {
                        Instructions = "replacement row wins before the claim",
                        Round = current.Round + 1,
                        AutomaticFixUsed = false,
                    }).ToList(),
            }, Ct));

            await service.TickOnceAsync(Ct);

            Assert.Equal(0, launches);
            Assert.Equal(oldBrief, File.ReadAllText(item.SpecFile));
            var retained = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(hold, (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Held);
            if (hold)
            {
                Assert.Equal(item.Round, retained.Round);
                Assert.Equal(item.Instructions, retained.Instructions);
            }
            else
            {
                Assert.Equal(item.Round + 1, retained.Round);
                Assert.Equal("replacement row wins before the claim", retained.Instructions);
                Assert.False(retained.AutomaticFixUsed);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_retained_attempt_preserves_its_binding_and_only_an_unstarted_one_refreshes(bool mayHaveBegun)
    {
        var home = CreateHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = Path.Combine(home, "rooms", "retained-review");
            Directory.CreateDirectory(room);
            var attemptId = new FleetAttemptId("retained-review-attempt");
            var admission = new TaskRequirementAdmission(
                [TaskRequirements.RepositoryRead],
                [TaskRequirements.RepositoryRead, TaskRequirements.ArtifactPrefix + "changes.md"],
                TaskRequirementAdmission.Admitted,
                []);
            var envelope = new QueueAttemptEnvelope(
                attemptId, null, "retained-attempt", 2566, 2567, WorkStage.Review, "review",
                "codex", "gpt-5.6-terra", "high", admission.EffectiveGrant, admission.Requested,
                admission.Missing, admission.Result, room, BatonPaths.RecordKey(room), null,
                DateTimeOffset.UtcNow);
            var item = ReviewItem(home, "retained-attempt", WorkStage.Review) with
            {
                AttemptId = attemptId,
                AttemptEnvelope = envelope,
                AttemptAdmissionFactDurable = true,
                LastAdmission = admission,
                RoomDirectory = room,
                LaunchMayHaveBegunAt = mayHaveBegun ? envelope.FactTimestamp : null,
            };
            var oldBrief = QueueBriefTemplates.Compose(item.Stage!.Value, item, new(
                Do: item.Instructions!, PullRequest: item.PullRequest, HeadSha: OldHead, Round: item.Round));
            File.WriteAllText(item.SpecFile, oldBrief);
            await QueueStore.MutateAsync(BatonPaths.QueueFile,
                snapshot => snapshot with { Items = [item] }, Ct);

            QueueLaunchRequest? launch = null;
            var service = Service(home, new CurrentPullRequest(), (request, _) =>
            {
                launch = request;
                return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
            });

            await service.TickOnceAsync(Ct);

            if (mayHaveBegun)
            {
                Assert.Null(launch);
                Assert.Equal(oldBrief, File.ReadAllText(item.SpecFile));
                return;
            }

            Assert.NotNull(launch);
            Assert.Equal(CurrentHead, ExtractRequiredHead(File.ReadAllText(item.SpecFile)));
            Assert.Equal(("codex", "gpt-5.6-terra", "high"),
                (launch.Tier.Adapter, launch.Tier.Model, launch.Tier.Effort));
            Assert.Equal(attemptId, launch.Item.AttemptId);
            AssertSameAdmission(admission, launch.Item.LastAdmission);
            var retained = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(attemptId, retained.AttemptId);
            AssertSameAdmission(admission, retained.LastAdmission);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_limit_changed_during_admission_cannot_launch_using_the_old_limit()
    {
        var home = CreateHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = ReviewItem(home, "admission-limit-race", WorkStage.Review) with { TokenBudget = 1_000_000 };
            File.WriteAllText(item.SpecFile, "old saved brief");
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with { Items = [item] }, Ct);
            var launches = new List<QueueLaunchRequest>();
            var changed = false;
            var service = new QueueSchedulerService(
                (request, _) =>
                {
                    launches.Add(request);
                    return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
                }, _ => Task.FromResult(0d), () => 16d, () => DateTimeOffset.UtcNow,
                advancer: new WorkItemAdvancer(new CurrentPullRequest(), (_, _) => Task.FromResult<string?>(CurrentHead)),
                appendFleetEvent: async (fact, _) =>
                {
                    if (fact.Kind == FleetEventKind.AdmissionDecided && !changed)
                    {
                        changed = true;
                        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
                        {
                            Items = snapshot.Items.Select(row => row with { TokenBudget = 500_000 }).ToList(),
                        }, Ct);
                    }
                    return null;
                },
                workspaceLocks: _ => []);

            await service.TickOnceAsync(Ct);

            Assert.True(changed);
            Assert.Empty(launches);
            var retained = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(500_000, retained.TokenBudget);
            Assert.Equal(QueueItemState.Queued, retained.State);
            Assert.Null(retained.LaunchMayHaveBegunAt);
            Assert.Equal(item.Round, retained.Round);
            Assert.Equal(item.AutomaticFixUsed, retained.AutomaticFixUsed);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("branch")]
    [InlineData("pr")]
    public async Task Missing_lifecycle_lineage_cannot_fall_through_to_a_saved_review(string missing)
    {
        var home = CreateHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = ReviewItem(home, "missing-lineage", WorkStage.Review);
            item = missing switch
            {
                "repository" => item with { Repository = null },
                "branch" => item with { Branch = null },
                _ => item with { PullRequest = null },
            };
            File.WriteAllText(item.SpecFile, "saved brief must remain untouched");
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with { Items = [item] }, Ct);
            var launches = 0;
            var service = Service(home, new CurrentPullRequest(), (request, _) =>
            {
                launches++;
                return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
            });

            await service.TickOnceAsync(Ct);

            Assert.Equal(0, launches);
            Assert.Equal("saved brief must remain untouched", File.ReadAllText(item.SpecFile));
            var retained = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, retained.State);
            Assert.Null(retained.LaunchMayHaveBegunAt);
            Assert.Equal(item.Round, retained.Round);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    private static string CreateHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_review_launch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

    private static QueueItem ReviewItem(string home, string tag, WorkStage stage, string? lastVerdict = null)
    {
        lastVerdict ??= stage == WorkStage.ReReview ? Path.Combine(home, tag + "-prior-verdict.json") : null;
        if (lastVerdict is { } priorVerdict && !File.Exists(priorVerdict))
        {
            File.WriteAllText(priorVerdict, PriorVerdict);
        }

        return new QueueItem
        {
            Tag = tag,
            Role = "review",
            Stage = stage,
            Repository = "github.com/philipreese/baton",
            Branch = "2566-lane",
            PullRequest = 2567,
            Issue = 2566,
            Workspace = home,
            SpecFile = Path.Combine(home, tag + ".md"),
            Instructions = "Check the requested change against its issue.",
            LastVerdict = lastVerdict,
            Round = 3,
            AutomaticFixUsed = true,
        };
    }

    private static QueueSchedulerService Service(
        string home,
        IGhCliRunner probe,
        Func<QueueLaunchRequest, CancellationToken, Task<QueueLaunchOutcome>> launch,
        Action<QueueLaunchRequest>? observed = null,
        Func<CancellationToken, Task>? beforeLaunchClaim = null,
        string? workspaceHead = CurrentHead,
        Func<string, CancellationToken, Task<RepositoryIdentity?>>? repositoryIdentity = null)
    {
        return new QueueSchedulerService(
            (request, cancellationToken) =>
            {
                observed?.Invoke(request);
                return launch(request, cancellationToken);
            },
            _ => Task.FromResult(0d),
            () => 16d,
            () => DateTimeOffset.UtcNow,
            advancer: new WorkItemAdvancer(probe, (_, _) => Task.FromResult(workspaceHead), repositoryIdentity),
            beforeLaunchClaim: beforeLaunchClaim,
            workspaceLocks: _ => []);
    }

    private static string? ExtractRequiredHead(string brief)
    {
        var marker = "Set `reviewedRef` to";
        var start = brief.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start = brief.IndexOf('`', start + marker.Length);
        if (start < 0)
        {
            return null;
        }

        start++;
        var end = brief.IndexOf('`', start);
        return end < 0 ? null : brief[start..end];
    }

    private static void AssertSameAdmission(
        TaskRequirementAdmission expected, TaskRequirementAdmission? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Result, actual!.Result);
        Assert.Equal(expected.Requested?.ToArray(), actual.Requested?.ToArray());
        Assert.Equal(expected.EffectiveGrant.ToArray(), actual.EffectiveGrant.ToArray());
        Assert.Equal(expected.Missing?.ToArray(), actual.Missing?.ToArray());
    }

    private const string PriorVerdict = """
        {"reviewedRef":"1111111111111111111111111111111111111111","completion":"complete","decision":"block","summary":"prior","findings":[]}
        """;

    private sealed class CurrentPullRequest(
        string? mode = null, string head = CurrentHead, string checks = "unknown") : IGhCliRunner
    {
        public Task<GhCliResult> RunAsync(string workingDirectory, IReadOnlyList<string> args,
            CancellationToken cancellationToken)
        {
            if (mode == "unknown")
            {
                return Task.FromResult(new GhCliResult(false, 1, string.Empty, "unknown PR"));
            }

            if (args is ["api", "--hostname", "github.com", var endpoint])
            {
                if (checks == "unknown")
                    return Task.FromResult(new GhCliResult(false, 1, string.Empty, "unknown required policy"));

                var status = checks == "pending" ? "in_progress" : "completed";
                var conclusion = checks == "pending" ? "null" : "\"failure\"";
                var response = endpoint.EndsWith("/protection", StringComparison.Ordinal)
                    ? """{"required_status_checks":{"contexts":["ci"],"checks":[{"context":"ci","app_id":1}]}}"""
                    : endpoint.Contains("/check-runs?", StringComparison.Ordinal)
                        ? $$$"""
                            {"total_count":1,"check_runs":[{"id":1,"name":"ci","head_sha":"{{{head}}}",
                            "status":"{{{status}}}","conclusion":{{{conclusion}}},"started_at":"2026-10-02T16:00:00Z","app":{"id":1}}]}
                            """
                        : "[]";
                return Task.FromResult(new GhCliResult(true, 0, response, string.Empty));
            }

            var checkJson = checks switch
            {
                "pending" => "[{\"name\":\"ci\",\"status\":\"IN_PROGRESS\"}]",
                "failing" => "[{\"name\":\"ci\",\"status\":\"COMPLETED\",\"conclusion\":\"FAILURE\"}]",
                _ => "null",
            };
            var json = args.Contains("checks") ? "[]" : $$"""
                {"number":2567,"state":"OPEN","isDraft":true,"headRefOid":"{{head}}",
                 "headRefName":"2566-lane","baseRefName":"main","isCrossRepository":false,
                 "statusCheckRollup":{{checkJson}}}
                """;
            return Task.FromResult(new GhCliResult(true, 0, json, string.Empty));
        }
    }
}
