using System.Diagnostics;
using Baton.Cli.Daemon;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Mutation;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #2225 acceptance at the public seams: the conductor selects <c>measure</c> in a durable queue row,
/// the launched room records that role in bindings and history, and the role's non-empty artifact
/// contract settles without applying implement's workspace/delivery checks. All workers are local
/// fakes; the git origin is a temporary local bare repository and the PR result is a script.
/// </summary>
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class MeasurementCompletionContractTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IsolatedBatonHome _batonHome = new();
    private readonly IDisposable _catalogScope;

    public MeasurementCompletionContractTests()
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

    /// <summary>
    /// The two concrete false-failure populations from #2190 and #2150. Each fixture first proves the
    /// old implement delivery question answers <c>pr-not-open</c>, then runs the same pushed/no-PR
    /// workspace through the conductor-selected measurement role and requires a successful report.
    /// </summary>
    [Theory]
    [InlineData("2190-ownership-prototype", "Hermetic ownership prototype", "quoted-title matrix passed")]
    [InlineData("2150-budget-headroom", "Budget headroom extraction", "outcome-stratified sample recorded")]
    public async Task Prior_measurement_only_shapes_settle_from_their_report_without_a_PR(
        string branch, string spec, string reportText)
    {
        var root = TempPath("prior-shape");
        var (workspace, origin) = await CreatePushedWorkspaceAsync(root, branch);
        try
        {
            var noPr = WriteFakeGh(root, "[]");
            var oldImplementVerdict = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: true, Ct, ghProgram: noPr);
            Assert.Equal(DeliveryCheckStatus.Failed, oldImplementVerdict.Status);
            Assert.Equal(["pr-not-open"], oldImplementVerdict.FailingMembers);

            var reportFixture = Path.Combine(root, "report-fixture.md");
            await File.WriteAllTextAsync(reportFixture, reportText, Ct);
            var stdoutFixture = Path.Combine(root, "codex-shell-call.jsonl");
            await File.WriteAllTextAsync(
                stdoutFixture,
                "{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\",\"command\":\"copy report fixture\"}}",
                Ct);
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                // Local fake execution, real shipped parser identity. Codex counts this shell call as
                // a tool step but correctly reports zero write-family calls; that measured zero is the
                // live-parser shape which exposed the implementation self-check collision.
                ["codex"] = new ContractOutputWorkerAdapter(
                    satisfyOutputs: true,
                    outputFixtures: new Dictionary<string, string> { ["report.md"] = reportFixture },
                    stdoutFixture: stdoutFixture),
            };
            var specPath = await WriteSpecAsync(root, spec);
            var room = Path.Combine(root, "room");

            var result = await DispatchCommand.ExecuteAsync(
                new DispatchOptions("measure", specPath, room, Adapter: "codex", WorkspaceDirectory: workspace),
                adapters,
                Ct,
                evaluateRunway: RunwayTestGate.Admit);

            var step = Assert.Single(result.State.Steps);
            Assert.Equal("measure", step.StepId.Value);
            FlowAssert.Succeeded(step);

            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(room, BatonPaths.RoomBindingsFileName), Ct);
            var binding = Assert.Contains("measure", bindings);
            Assert.False(binding.DeliversBranch);
            Assert.False(binding.ExpectPr);
            Assert.False(binding.VerifiesWorkspace);
            Assert.Equal("codex", binding.Adapter);
            Assert.Equal(OutputSchema.NonEmptyText, Assert.Single(binding.Contract.ProducedOutputs).Schema);

            var events = await new FlowEventLogReader(Path.Combine(room, BatonPaths.FlowLogFileName)).ReadAllAsync(Ct);
            Assert.Equal("measure", Assert.Single(events.OfType<FlowEvent.ExecutionRequestAccepted>()).Request.Worker);
            Assert.Empty(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Empty(events.OfType<FlowEvent.VerifyNotRun>());

            var executionArtifacts = Path.Combine(room, "artifacts", $"execution_{step.LatestExecutionId}");
            Assert.Equal(
                0,
                MutationInterface.CountWriteToolCallsFromStdoutLog(
                    new CodexUsageParser(), executionArtifacts));
            var delivered = Path.Combine(executionArtifacts, "report.md");
            Assert.Equal(reportText, (await File.ReadAllTextAsync(delivered, Ct)).Trim());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
            if (Directory.Exists(origin))
            {
                DirectoryCleanup.DeleteRecursively(origin);
            }
        }
    }

    [Fact]
    public async Task Ordinary_implementation_with_the_same_live_parser_zero_write_metadata_still_fails()
    {
        var root = TempPath("implement-zero-write");
        var (workspace, origin) = await CreatePushedWorkspaceAsync(root, "implement-zero-write");
        try
        {
            var stdoutFixture = Path.Combine(root, "codex-shell-call.jsonl");
            await File.WriteAllTextAsync(
                stdoutFixture,
                "{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\",\"command\":\"echo claimed completion\"}}",
                Ct);
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["codex"] = new ContractOutputWorkerAdapter(
                    satisfyOutputs: true,
                    stdoutFixture: stdoutFixture),
            };
            var specPath = await WriteSpecAsync(root, "Implement without changing the repository.");
            var room = Path.Combine(root, "room");

            var result = await DispatchCommand.ExecuteAsync(
                new DispatchOptions("implement", specPath, room, Adapter: "codex", WorkspaceDirectory: workspace),
                adapters,
                Ct,
                evaluateRunway: RunwayTestGate.Admit);

            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Failed, step.Status);
            Assert.Equal(FailureClassification.Permanent, step.LatestFailureClassification);
            Assert.Contains("zero write-tool calls", step.LatestFailureReason!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
            if (Directory.Exists(origin))
            {
                DirectoryCleanup.DeleteRecursively(origin);
            }
        }
    }

    /// <summary>
    /// Missing and present-but-invalid are separate controls. <c>non_empty_text</c> makes a blank
    /// report an honest schema violation instead of treating file existence as completed measurement.
    /// </summary>
    [Theory]
    [InlineData(false, "is missing")]
    [InlineData(true, "no non-whitespace content")]
    public async Task Missing_or_blank_measurement_report_cannot_settle_successfully(
        bool writeBlankReport, string expectedReason)
    {
        var root = TempPath("bad-output");
        try
        {
            var specPath = await WriteSpecAsync(root, "Measure the isolated fixture.");
            var room = Path.Combine(root, "room");
            IReadOnlyDictionary<string, string>? fixtures = null;
            if (writeBlankReport)
            {
                var blank = Path.Combine(root, "blank.md");
                await File.WriteAllTextAsync(blank, " \r\n\t", Ct);
                fixtures = new Dictionary<string, string> { ["report.md"] = blank };
            }

            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["fake"] = new ContractOutputWorkerAdapter(
                    satisfyOutputs: writeBlankReport,
                    outputFixtures: fixtures),
            };

            var result = await DispatchCommand.ExecuteAsync(
                new DispatchOptions("measure", specPath, room, Adapter: "fake", WorkspaceDirectory: root),
                adapters,
                Ct);

            var step = Assert.Single(result.State.Steps);
            Assert.NotEqual(StepStatus.Succeeded, step.Status);
            Assert.Contains("report.md", step.LatestFailureReason!, StringComparison.Ordinal);
            Assert.Contains(expectedReason, step.LatestFailureReason!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// Queue add is the authority boundary. A store reload stands in for daemon restart; the reloaded
    /// role survives through QueueLauncher's child argv and the dispatch parser, while <c>queue list</c>
    /// exposes the selection before anything launches.
    /// </summary>
    [Fact]
    public async Task Queued_measurement_selection_is_inspectable_and_survives_restart_launch_round_trip()
    {
        var root = TempPath("queue");
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            var specPath = await WriteSpecAsync(root, "Measure without shipping.");

            var addOutput = new StringWriter();
            var exit = await QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add,
                    Tag: "2225-measure",
                    Role: "measure",
                    SpecFilePath: specPath,
                    WorkspaceDirectory: workspace),
                addOutput,
                Ct);
            Assert.Equal(0, exit);
            Assert.Contains("Queued '2225-measure' (measure)", addOutput.ToString(), StringComparison.Ordinal);

            var listOutput = new StringWriter();
            await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List), listOutput, Ct);
            Assert.Contains("2225-measure  queued  measure", listOutput.ToString(), StringComparison.Ordinal);

            // Reload from queue.json rather than carrying the object returned by add: this is the
            // durable fact a restarted scheduler reads.
            var reloaded = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal("measure", reloaded.Role);

            var tier = QueueTierTable.Resolve(
                reloaded,
                new QueueSettings(),
                WorkerRoleCatalog.QueueTierFor,
                WorkerRoleCatalog.QueueTierForRole);
            var room = Path.Combine(root, "room-after-restart");
            var argv = QueueLauncher.BuildArguments(
                QueueLauncher.BuildOptions(new QueueLaunchRequest(reloaded, tier, room)));
            var parsed = DispatchOptionsParser.Parse(argv.Skip(1).ToList());

            Assert.Equal("measure", parsed.Name);
            Assert.Equal(reloaded.SpecFile, parsed.SpecFilePath);
            Assert.Equal(workspace, parsed.WorkspaceDirectory);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    private static async Task<string> WriteSpecAsync(string root, string text)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"spec-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(path, text, Ct);
        return path;
    }

    private static async Task<(string Workspace, string Origin)> CreatePushedWorkspaceAsync(string root, string branch)
    {
        Directory.CreateDirectory(root);
        var origin = Path.Combine(root, "origin.git");
        await RunGitAsync(root, "init", "--bare", origin);
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        await RunGitAsync(workspace, "init", "--initial-branch", "main");
        await RunGitAsync(workspace, "config", "user.name", "Baton Test");
        await RunGitAsync(workspace, "config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(workspace, "README.md"), "fixture");
        await RunGitAsync(workspace, "add", "README.md");
        await RunGitAsync(workspace, "commit", "-m", "fixture");
        await RunGitAsync(workspace, "remote", "add", "origin", origin);
        await RunGitAsync(workspace, "checkout", "-b", branch);
        File.WriteAllText(Path.Combine(workspace, "measurement.txt"), branch);
        await RunGitAsync(workspace, "add", "measurement.txt");
        await RunGitAsync(workspace, "commit", "-m", "measurement fixture");
        await RunGitAsync(workspace, "push", "--set-upstream", "origin", branch);
        return (workspace, origin);
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start git {string.Join(' ', arguments)}.");
        var (stdout, stderr) = await BoundedProcessWait.RunToExitAsync(
            process, TimeSpan.FromSeconds(30), Ct);
        Assert.True(process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed with {process.ExitCode}: {stderr}{stdout}");
    }

    private static string WriteFakeGh(string root, string json)
    {
        var path = Path.Combine(root, $"fake-gh-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, $"@echo off\necho {json}\nexit /b 0\n");
        return path;
    }

    private static string TempPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"baton-2225-{label}-{Guid.NewGuid():N}");
}

