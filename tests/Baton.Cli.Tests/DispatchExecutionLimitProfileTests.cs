using Baton.Cli.Daemon;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Mutation;
using Baton.Queue;
using Baton.Runway;
using Baton.Status;
using Baton.Store;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// Verifies dispatch-level execution limit profile resolution, override precedence, malformed settings
/// rejection before worker launch, and agreement between direct and queue-launched role dispatch (#2440).
/// </summary>
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class DispatchExecutionLimitProfileTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter>
        {
            ["fake"] = new ContractOutputWorkerAdapter(satisfyOutputs: true),
        };

    private readonly IsolatedBatonHome _batonHome = new();
    private readonly IDisposable _catalogScope;

    public DispatchExecutionLimitProfileTests()
    {
        _catalogScope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Current with
        {
            WorkerRolesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"),
            WorkerTiersPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"),
            WorkflowTemplatesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkflowTemplates.json"),
        });
    }

    public void Dispose()
    {
        _catalogScope.Dispose();
        _batonHome.Dispose();
    }

    private static async Task<string> WriteSpecAsync(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"spec-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }

    [Fact]
    public async Task Direct_dispatch_with_matching_profile_applies_profile_limits_and_records_provenance()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-profile-match-{Guid.NewGuid():N}");
        try
        {
            var settings = new DaemonSettings
            {
                ExecutionLimitProfiles =
                [
                    new ExecutionLimitProfile
                    {
                        Adapter = "fake",
                        Model = "test-model",
                        Role = "implement",
                        DeclaredTaskSize = "small",
                        Timeout = TimeSpan.FromMinutes(11),
                        TokenBudget = 12345,
                        MaxToolSteps = 22,
                    },
                ],
            };
            await DaemonSettingsStore.SaveAsync(settings, BatonPaths.SettingsFile, TestContext.Current.CancellationToken);

            var specPath = await WriteSpecAsync(testRoot, "Implement feature with profile.");
            var roomDir = Path.Combine(testRoot, "room");
            var options = new DispatchOptions(
                "implement", specPath, roomDir, Adapter: "fake", Model: "test-model",
                DeclaredTaskSize: TaskSizeDeclaration.Parse("small", "profile test"));

            var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDir, "bindings.json"), TestContext.Current.CancellationToken);
            var binding = bindings["implement"];

            Assert.Equal(TimeSpan.FromMinutes(11), binding.Timeout);
            Assert.Equal(12345, binding.TokenBudget);
            Assert.Equal(22, binding.MaxToolSteps);
            Assert.NotNull(binding.ExecutionLimitResolution);
            Assert.Equal("fake/test-model/implement/small", binding.ExecutionLimitResolution.ChosenKey);
            Assert.Equal(ExecutionLimitSource.Profile, binding.ExecutionLimitResolution.TimeoutSource);
            Assert.Equal(ExecutionLimitSource.Profile, binding.ExecutionLimitResolution.TokenBudgetSource);
            Assert.Equal(ExecutionLimitSource.Profile, binding.ExecutionLimitResolution.MaxToolStepsSource);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Direct_dispatch_with_camel_case_profile_section_applies_profile_limits()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-profile-camel-case-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(
                BatonPaths.SettingsFile,
                "{\"executionLimitProfiles\":[{\"adapter\":\"fake\",\"model\":\"test-model\",\"role\":\"implement\",\"declaredTaskSize\":\"small\",\"timeout\":\"00:11:00\",\"tokenBudget\":12345,\"maxToolSteps\":22}]}",
                TestContext.Current.CancellationToken);

            var specPath = await WriteSpecAsync(testRoot, "Implement feature with a camel-case profile.");
            var roomDir = Path.Combine(testRoot, "room");
            var options = new DispatchOptions(
                "implement", specPath, roomDir, Adapter: "fake", Model: "test-model",
                DeclaredTaskSize: TaskSizeDeclaration.Parse("small", "camel-case profile test"));

            var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDir, "bindings.json"), TestContext.Current.CancellationToken);
            var binding = bindings["implement"];

            Assert.Equal(TimeSpan.FromMinutes(11), binding.Timeout);
            Assert.Equal(12345, binding.TokenBudget);
            Assert.Equal(22, binding.MaxToolSteps);
            Assert.Equal(ExecutionLimitSource.Profile, binding.ExecutionLimitResolution?.TimeoutSource);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Direct_dispatch_with_explicit_overrides_wins_independently_per_brake()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-profile-override-{Guid.NewGuid():N}");
        try
        {
            var settings = new DaemonSettings
            {
                ExecutionLimitProfiles =
                [
                    new ExecutionLimitProfile
                    {
                        Adapter = "fake",
                        Model = "test-model",
                        Role = "implement",
                        DeclaredTaskSize = "small",
                        Timeout = TimeSpan.FromMinutes(15),
                        TokenBudget = 50000,
                        MaxToolSteps = 30,
                    },
                ],
            };
            await DaemonSettingsStore.SaveAsync(settings, BatonPaths.SettingsFile, TestContext.Current.CancellationToken);

            var specPath = await WriteSpecAsync(testRoot, "Implement feature with overrides.");
            var roomDir = Path.Combine(testRoot, "room");
            var options = new DispatchOptions(
                "implement", specPath, roomDir, Adapter: "fake", Model: "test-model",
                DeclaredTaskSize: TaskSizeDeclaration.Parse("small", "override test"),
                Timeout: TimeSpan.FromMinutes(8),
                TokenBudget: 80000);

            var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDir, "bindings.json"), TestContext.Current.CancellationToken);
            var binding = bindings["implement"];

            Assert.Equal(TimeSpan.FromMinutes(8), binding.Timeout);
            Assert.Equal(80000, binding.TokenBudget);
            Assert.Equal(30, binding.MaxToolSteps);
            Assert.NotNull(binding.ExecutionLimitResolution);
            Assert.Equal("fake/test-model/implement/small", binding.ExecutionLimitResolution.ChosenKey);
            Assert.Equal(ExecutionLimitSource.DispatchOverride, binding.ExecutionLimitResolution.TimeoutSource);
            Assert.Equal(ExecutionLimitSource.DispatchOverride, binding.ExecutionLimitResolution.TokenBudgetSource);
            Assert.Equal(ExecutionLimitSource.Profile, binding.ExecutionLimitResolution.MaxToolStepsSource);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Direct_dispatch_with_no_profile_match_preserves_role_defaults_and_records_role_default_source()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-profile-default-{Guid.NewGuid():N}");
        try
        {
            var settings = new DaemonSettings
            {
                ExecutionLimitProfiles =
                [
                    new ExecutionLimitProfile
                    {
                        Adapter = "fake",
                        Model = "other-model",
                        Role = "implement",
                        DeclaredTaskSize = "small",
                        Timeout = TimeSpan.FromMinutes(10),
                        TokenBudget = 1000,
                        MaxToolSteps = 10,
                    },
                ],
            };
            await DaemonSettingsStore.SaveAsync(settings, BatonPaths.SettingsFile, TestContext.Current.CancellationToken);

            var specPath = await WriteSpecAsync(testRoot, "Implement feature with no profile match.");
            var roomDir = Path.Combine(testRoot, "room");
            var options = new DispatchOptions(
                "implement", specPath, roomDir, Adapter: "fake", Model: "unmatched-model",
                DeclaredTaskSize: TaskSizeDeclaration.Parse("small", "no match"));

            var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDir, "bindings.json"), TestContext.Current.CancellationToken);
            var binding = bindings["implement"];
            var role = WorkerRoleCatalog.For("implement");

            Assert.Equal(role.Timeout, binding.Timeout);
            Assert.Equal(role.MaxToolSteps, binding.MaxToolSteps);
            Assert.NotNull(binding.ExecutionLimitResolution);
            var resolution = binding.ExecutionLimitResolution!;
            Assert.Null(resolution.ChosenKey);
            Assert.Equal("fake/unmatched-model/implement/small", resolution.OriginatingSelectionKey);
            Assert.Equal(ExecutionLimitSource.RoleDefault, resolution.TimeoutSource);
            Assert.Equal(ExecutionLimitSource.RoleDefault, resolution.TokenBudgetSource);
            Assert.Equal(ExecutionLimitSource.RoleDefault, resolution.MaxToolStepsSource);

            var resolved = Assert.IsType<WorkerBinding.Process>(WorkerBindingResolver.Resolve(
                new Dictionary<string, WorkerBindingConfigEntry> { ["implement"] = binding }, Adapters)["implement"]);
            var evidence = resolved.EffectiveLimitEvidence!;
            Assert.Equal(binding.Timeout, evidence.Timeout);
            Assert.Equal(ExecutionLimitSource.RoleDefault, evidence.TimeoutSource);
            var entries = await new FlowEventLogReader(
                Path.Combine(roomDir, BatonPaths.FlowLogFileName)).ReadAllEntriesWithTimestampsAsync(
                    TestContext.Current.CancellationToken);
            var accepted = Assert.Single(entries.OfType<LogEntry.FlowLogEntry>()
                .Select(entry => entry.Event)
                .OfType<FlowEvent.ExecutionRequestAccepted>());
            var limits = accepted!.Request.Limits!;
            Assert.Equal(binding.Timeout, limits.Timeout);
            Assert.Equal(ExecutionLimitSource.RoleDefault, limits.TimeoutSource);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Direct_dispatch_rejects_malformed_execution_limit_profiles_before_worker_launch()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-malformed-profiles-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(
                BatonPaths.SettingsFile,
                "{\"ExecutionLimitProfiles\": [ { \"Adapter\": \"fake\", ",
                TestContext.Current.CancellationToken);

            var specPath = await WriteSpecAsync(testRoot, "Implement with malformed settings.");
            var roomDir = Path.Combine(testRoot, "room");
            var options = new DispatchOptions(
                "implement", specPath, roomDir, Adapter: "fake", Model: "test-model");

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit));

            Assert.Contains("ExecutionLimitProfiles is malformed", ex.Message, StringComparison.Ordinal);
            Assert.Contains("fix or remove the malformed ExecutionLimitProfiles", ex.TryInvocation ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Direct_dispatch_without_declared_task_size_leaves_binding_declared_task_size_null()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-size-null-{Guid.NewGuid():N}");
        try
        {
            var settings = new DaemonSettings
            {
                ExecutionLimitProfiles =
                [
                    new ExecutionLimitProfile
                    {
                        Adapter = "fake",
                        Model = "test-model",
                        Role = "implement",
                        DeclaredTaskSize = "unknown",
                        Timeout = TimeSpan.FromMinutes(12),
                        TokenBudget = 30000,
                        MaxToolSteps = 25,
                    },
                ],
            };
            await DaemonSettingsStore.SaveAsync(settings, BatonPaths.SettingsFile, TestContext.Current.CancellationToken);

            var specPath = await WriteSpecAsync(testRoot, "Implement with unknown size.");
            var roomDir = Path.Combine(testRoot, "room");
            var options = new DispatchOptions(
                "implement", specPath, roomDir, Adapter: "fake", Model: "test-model");

            var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(roomDir, "bindings.json"), TestContext.Current.CancellationToken);
            var binding = bindings["implement"];

            // DeclaredTaskSize on binding must remain null, NOT mutated to TaskSizeDeclaration.Unknown
            Assert.Null(binding.DeclaredTaskSize);
            // But resolution matched the 'unknown' row in profiles
            Assert.Equal("fake/test-model/implement/unknown", binding.ExecutionLimitResolution?.ChosenKey);
            Assert.Equal(TimeSpan.FromMinutes(12), binding.Timeout);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Direct_and_queue_launched_role_dispatch_agree_on_limits_and_provenance()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-queue-agree-{Guid.NewGuid():N}");
        try
        {
            var settings = new DaemonSettings
            {
                ExecutionLimitProfiles =
                [
                    new ExecutionLimitProfile
                    {
                        Adapter = "fake",
                        Model = "other-model",
                        Role = "advise",
                        DeclaredTaskSize = "medium",
                        Timeout = TimeSpan.FromMinutes(16),
                        TokenBudget = 64000,
                        MaxToolSteps = 32,
                    },
                ],
            };
            await DaemonSettingsStore.SaveAsync(settings, BatonPaths.SettingsFile, TestContext.Current.CancellationToken);

            var specPath = await WriteSpecAsync(testRoot, "Advise task.");
            var directRoom = Path.Combine(testRoot, "direct-room");
            var taskSize = TaskSizeDeclaration.Parse("medium", "agreement test");
            var directOptions = new DispatchOptions(
                "advise", specPath, directRoom, Adapter: "fake", Model: "test-model",
                DeclaredTaskSize: taskSize);

            await DispatchCommand.ExecuteAsync(directOptions, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);

            var queueRoom = Path.Combine(testRoot, "queue-room");
            var item = new QueueItem
            {
                Tag = "q-agree-tag",
                Role = "advise",
                Workspace = testRoot,
                SpecFile = specPath,
                DeclaredTaskSize = taskSize,
            };
            var tier = new QueueTierResolution("engine", "fake", "test-model", "high", false, null);
            var queueOptions = QueueLauncher.BuildOptions(new QueueLaunchRequest(item, tier, queueRoom));
            var queueArgv = QueueLauncher.BuildArguments(queueOptions);
            var parsedFromArgv = DispatchOptionsParser.Parse(queueArgv.Skip(1).ToList());

            await DispatchCommand.ExecuteAsync(parsedFromArgv, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);

            var directBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(directRoom, "bindings.json"), TestContext.Current.CancellationToken);
            var queueBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(queueRoom, "bindings.json"), TestContext.Current.CancellationToken);

            var directEntry = directBindings["advise"];
            var queueEntry = queueBindings["advise"];

            Assert.Equal(directEntry.Timeout, queueEntry.Timeout);
            Assert.Equal(directEntry.TokenBudget, queueEntry.TokenBudget);
            Assert.Equal(directEntry.MaxToolSteps, queueEntry.MaxToolSteps);
            Assert.Equal(directEntry.ExecutionLimitResolution, queueEntry.ExecutionLimitResolution);
            var resolution = directEntry.ExecutionLimitResolution!;
            Assert.Null(resolution.ChosenKey);
            Assert.Equal("fake/test-model/advise/medium", resolution.OriginatingSelectionKey);
            Assert.Equal(ExecutionLimitSource.RoleDefault, resolution.TimeoutSource);
            Assert.Equal(ExecutionLimitSource.RoleDefault, resolution.TokenBudgetSource);
            Assert.Equal(ExecutionLimitSource.RoleDefault, resolution.MaxToolStepsSource);

            foreach (var room in new[] { directRoom, queueRoom })
            {
                var entries = await new FlowEventLogReader(
                    Path.Combine(room, BatonPaths.FlowLogFileName)).ReadAllEntriesWithTimestampsAsync(
                        TestContext.Current.CancellationToken);
                var accepted = Assert.Single(entries.OfType<LogEntry.FlowLogEntry>()
                    .Select(entry => entry.Event)
                    .OfType<FlowEvent.ExecutionRequestAccepted>());
                var limits = accepted!.Request.Limits!;
                Assert.Equal(directEntry.Timeout, limits.Timeout);
                Assert.Equal(ExecutionLimitSource.RoleDefault, limits.TimeoutSource);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Queue_launched_dispatch_forwards_explicit_overrides_which_win_over_profile()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"queue-profile-override-{Guid.NewGuid():N}");
        try
        {
            var settings = new DaemonSettings
            {
                ExecutionLimitProfiles =
                [
                    new ExecutionLimitProfile
                    {
                        Adapter = "fake",
                        Model = "test-model",
                        Role = "advise",
                        DeclaredTaskSize = "unknown",
                        Timeout = TimeSpan.FromMinutes(25),
                        TokenBudget = 50000,
                        MaxToolSteps = 50,
                    },
                ],
            };
            await DaemonSettingsStore.SaveAsync(settings, BatonPaths.SettingsFile, TestContext.Current.CancellationToken);

            var specPath = await WriteSpecAsync(testRoot, "Advise with queue overrides.");
            var queueRoom = Path.Combine(testRoot, "queue-room");
            var item = new QueueItem
            {
                Tag = "q-override-tag",
                Role = "advise",
                Workspace = testRoot,
                SpecFile = specPath,
                TimeoutMinutes = 10,
                TokenBudget = 99999,
            };
            var tier = new QueueTierResolution("engine", "fake", "test-model", "high", false, null);
            var queueOptions = QueueLauncher.BuildOptions(new QueueLaunchRequest(item, tier, queueRoom));
            var queueArgv = QueueLauncher.BuildArguments(queueOptions);

            Assert.Contains("--timeout", queueArgv);
            Assert.Contains("10", queueArgv);
            Assert.Contains("--token-budget", queueArgv);
            Assert.Contains("99999", queueArgv);
            Assert.DoesNotContain("--max-tool-steps", queueArgv);

            var parsedFromArgv = DispatchOptionsParser.Parse(queueArgv.Skip(1).ToList());
            await DispatchCommand.ExecuteAsync(parsedFromArgv, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);

            var queueBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(queueRoom, "bindings.json"), TestContext.Current.CancellationToken);
            var entry = queueBindings["advise"];

            Assert.Equal(TimeSpan.FromMinutes(10), entry.Timeout);
            Assert.Equal(99999, entry.TokenBudget);
            Assert.Equal(50, entry.MaxToolSteps);
            Assert.NotNull(entry.ExecutionLimitResolution);
            Assert.Equal("fake/test-model/advise/unknown", entry.ExecutionLimitResolution.ChosenKey);
            Assert.Equal(ExecutionLimitSource.DispatchOverride, entry.ExecutionLimitResolution.TimeoutSource);
            Assert.Equal(ExecutionLimitSource.DispatchOverride, entry.ExecutionLimitResolution.TokenBudgetSource);
            Assert.Equal(ExecutionLimitSource.Profile, entry.ExecutionLimitResolution.MaxToolStepsSource);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }
}
