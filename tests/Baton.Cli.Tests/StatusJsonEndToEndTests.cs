using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Status;
using Baton.Templates;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton status --json</c>'s shape (#1356 point 1): one <see cref="WorkflowStatusView"/> object,
/// derived from the same <c>StateProjector.Project</c> result the human rendering uses, across the
/// three states an agent needs to tell apart without parsing prose — succeeded, failed, running.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public class StatusJsonEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public void Status_json_view_serializes_the_complete_declaration_and_legacy_unknown()
    {
        var declared = JsonSerializer.Serialize(new WorkflowStatusView(
            "Running", [], [], null, null, DeclaredTaskSize: new TaskSizeDeclaration(DeclaredTaskSize.Large, "multiple durable contracts")));
        var legacy = JsonSerializer.Serialize(new WorkflowStatusView("Running", [], [], null, null));

        Assert.Contains("\"declaredTaskSize\":{\"size\":\"large\",\"rationale\":\"multiple durable contracts\"}", declared, StringComparison.Ordinal);
        Assert.Contains("\"declaredTaskSize\":{\"size\":\"unknown\"", legacy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pre_ledger_terminal_status_uses_the_binding_declaration_and_keeps_legacy_unknown()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-status-json-sentinel-size-{Guid.NewGuid():N}");
        var declaredRoom = Path.Combine(testRoot, "declared");
        var legacyRoom = Path.Combine(testRoot, "legacy");
        try
        {
            var sentinel = new WorkflowStatusView(WorkflowOutcome.Failed, [], [], "pre-ledger refusal", null);
            await TerminalSentinelWriter.WriteAsync(declaredRoom, sentinel, TestContext.Current.CancellationToken);
            await WriteOneStepBindingsAsync(
                declaredRoom,
                WriteFileCommand("plan", "unused"),
                new TaskSizeDeclaration(DeclaredTaskSize.Medium, "one durable seam"));

            using var declaredOutput = new StringWriter();
            await StatusCommand.ExecuteAsync(
                new StatusOptions(declaredRoom, Json: true), declaredOutput, TestContext.Current.CancellationToken);
            var declared = ParseSingleObject(declaredOutput.ToString());
            Assert.Equal(DeclaredTaskSize.Medium, declared.DeclaredTaskSize.Size);
            Assert.Equal("one durable seam", declared.DeclaredTaskSize.Rationale);

            await TerminalSentinelWriter.WriteAsync(legacyRoom, sentinel, TestContext.Current.CancellationToken);
            await WriteOneStepBindingsAsync(legacyRoom, WriteFileCommand("plan", "unused"));

            using var legacyOutput = new StringWriter();
            await StatusCommand.ExecuteAsync(
                new StatusOptions(legacyRoom, Json: true), legacyOutput, TestContext.Current.CancellationToken);
            var legacy = ParseSingleObject(legacyOutput.ToString());
            Assert.Equal(DeclaredTaskSize.Unknown, legacy.DeclaredTaskSize.Size);
            Assert.Null(legacy.DeclaredTaskSize.Rationale);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_succeeded_room_reports_state_Succeeded_with_step_states_and_output_paths()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-status-json-ok-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot, "solo");
            var bindingsFilePath = await WriteOneStepBindingsAsync(testRoot, WriteFileCommand("plan", "the-plan"));
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            using var stdout = new StringWriter();
            await StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Json: true), stdout, TestContext.Current.CancellationToken);

            var view = ParseSingleObject(stdout.ToString());
            Assert.Equal("Succeeded", view.State);
            var step = Assert.Single(view.Steps);
            Assert.Equal("solo", step.Id);
            Assert.Equal("Succeeded", step.State);
            Assert.NotNull(step.Execution);
            var outputPath = Assert.Single(view.Outputs);
            Assert.True(File.Exists(outputPath));
            Assert.Null(view.Error);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_succeeded_execution_reports_wall_clock_with_no_token_fields_when_stdout_is_plain_text()
    {
        // #1360 (extended by #1569): a shell-stub worker's stdout is plain text, never a vendor's
        // structured usage line -- wallClockMs is still derivable (Core recorded both lifecycle
        // events), but tokensIn/tokensOut/turns/cacheReadTokens/cacheCreationTokens/thinkingTokens must
        // be OMITTED from the JSON entirely, never emitted as a fabricated zero or null.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-status-json-usage-plain-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot, "solo");
            var bindingsFilePath = await WriteOneStepBindingsAsync(testRoot, WriteFileCommand("plan", "the-plan"));
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            using var stdout = new StringWriter();
            await StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Json: true), stdout, TestContext.Current.CancellationToken);

            var rawJson = stdout.ToString();
            var view = ParseSingleObject(rawJson);
            var step = Assert.Single(view.Steps);
            Assert.NotNull(step.Usage);
            Assert.True(step.Usage!.WallClockMs >= 0);
            Assert.Null(step.Usage.TokensIn);
            Assert.Null(step.Usage.TokensOut);
            Assert.Null(step.Usage.Turns);
            Assert.Null(step.Usage.CacheReadTokens);
            Assert.Null(step.Usage.CacheCreationTokens);
            Assert.Null(step.Usage.ThinkingTokens);

            // The stronger claim: the keys themselves are absent from the wire format, not merely
            // null after deserialization -- JsonIgnoreCondition.WhenWritingNull is what #1360/#1569
            // require.
            Assert.DoesNotContain("tokensIn", rawJson);
            Assert.DoesNotContain("tokensOut", rawJson);
            Assert.DoesNotContain("\"turns\"", rawJson);
            Assert.DoesNotContain("cacheReadTokens", rawJson);
            Assert.DoesNotContain("cacheCreationTokens", rawJson);
            Assert.DoesNotContain("thinkingTokens", rawJson);
            Assert.Contains("wallClockMs", rawJson);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void ExecutionUsageView_serializes_all_fields_with_exact_camelCase_wire_format_names_when_present()
    {
        // #1569 MED-5: positive wire-format test pinning the exact JSON property names when all
        // fields are populated.
        var usage = new ExecutionUsageView(
            WallClockMs: 1234,
            TokensIn: 10,
            TokensOut: 20,
            Turns: 2,
            CacheReadTokens: 300,
            CacheCreationTokens: 400,
            ThinkingTokens: 50);

        var rawJson = JsonSerializer.Serialize(usage);

        Assert.Contains("\"wallClockMs\":1234", rawJson);
        Assert.Contains("\"tokensIn\":10", rawJson);
        Assert.Contains("\"tokensOut\":20", rawJson);
        Assert.Contains("\"turns\":2", rawJson);
        Assert.Contains("\"cacheReadTokens\":300", rawJson);
        Assert.Contains("\"cacheCreationTokens\":400", rawJson);
        Assert.Contains("\"thinkingTokens\":50", rawJson);
    }

    [Fact]
    public async Task A_succeeded_execution_with_a_vendor_shaped_stdout_line_reports_no_tokens_when_dispatched_through_a_different_adapter()
    {
        // #1360 F1 spoof regression: a claude-shaped stream-json result line in the captured stdout
        // must NOT be picked up when this room actually dispatched through the test-only "shell"
        // adapter (per bindings.json) -- attribution, not a content-sniff across every registered
        // adapter. "shell" is not itself a registered adapter in status's own WorkerAdapterRegistry,
        // so this also proves an execution whose adapter name cannot be resolved fails closed rather
        // than falling back to guessing from content.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-status-json-usage-vendor-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot, "solo");
            var bindingsFilePath = await WriteOneStepBindingsAsync(
                testRoot, WriteFileAndEchoClaudeResultCommand(testRoot, "plan", "the-plan"));
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            using var stdout = new StringWriter();
            await StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Json: true), stdout, TestContext.Current.CancellationToken);

            var view = ParseSingleObject(stdout.ToString());
            var step = Assert.Single(view.Steps);
            Assert.NotNull(step.Usage);
            Assert.True(step.Usage!.WallClockMs >= 0);
            Assert.Null(step.Usage.TokensIn);
            Assert.Null(step.Usage.TokensOut);
            Assert.Null(step.Usage.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_running_steps_usage_and_linkedFromUsage_keys_are_absent_from_the_wire_JSON_not_null()
    {
        // #1360 F3: a step with no recorded start/exit pair yet (still running) -- and, same as every
        // ordinary dispatch, no LinkedFrom -- must omit "usage"/"linkedFromUsage" from the wire format
        // entirely. The pre-fix code comment already claimed this; JsonIgnoreCondition.WhenWritingNull
        // was missing from both properties, so the actual bytes were `"usage":null,"linkedFromUsage":null`.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-status-json-usage-absent-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot, "solo");
            var bindingsFilePath = await WriteOneStepBindingsAsync(testRoot, SleepThenWriteCommand("plan", seconds: 5));
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var runTask = RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                string? rawJson = null;
                while (DateTime.UtcNow < deadline)
                {
                    if (Directory.Exists(roomDirectory))
                    {
                        using var stdout = new StringWriter();
                        try
                        {
                            await StatusCommand.ExecuteAsync(
                                new StatusOptions(roomDirectory, Json: true), stdout, TestContext.Current.CancellationToken);
                            var candidate = stdout.ToString();
                            if (candidate.Contains("\"Running\"", StringComparison.Ordinal))
                            {
                                rawJson = candidate;
                                break;
                            }
                        }
                        catch (SnapshotLoadException)
                        {
                            // Not persisted yet -- keep polling.
                        }
                    }

                    // wait-ok: re-check cadence while waiting for the step to show Running; capped by the 20s deadline above.
                    await Task.Delay(50, TestContext.Current.CancellationToken);
                }

                Assert.NotNull(rawJson);
                Assert.DoesNotContain("\"usage\"", rawJson, StringComparison.Ordinal);
                Assert.DoesNotContain("\"linkedFromUsage\"", rawJson, StringComparison.Ordinal);
            }
            finally
            {
                await runTask;
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_failed_room_reports_state_Failed_with_the_step_failure_reason_as_the_top_level_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-status-json-fail-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot, "solo");
            var bindingsFilePath = await WriteOneStepBindingsAsync(testRoot, "exit 1");
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            using var stdout = new StringWriter();
            await StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Json: true), stdout, TestContext.Current.CancellationToken);

            var view = ParseSingleObject(stdout.ToString());
            Assert.Equal("Failed", view.State);
            var step = Assert.Single(view.Steps);
            Assert.Equal("Failed", step.State);
            Assert.Empty(view.Outputs);
            Assert.NotNull(view.Error);
            Assert.Contains("non-zero code", view.Error);
            // #1377 polarity: an ordinary crash must NOT also read as a decision rejection.
            Assert.False(view.Rejected);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_rejected_gate_reports_rejected_true_with_error_still_null_distinct_from_a_crash()
    {
        // #1377: a human `baton decide reject` over a succeeded, paused step carries no failure event
        // (StateProjector never sets LatestFailureReason for a Reject), so `error` stays null exactly
        // as it would for a healthy room -- `rejected` is what makes the outcome branchable (the
        // contract's rationale lives on WorkflowStatusView.Rejected). `steps[].state` already reads
        // "Rejected", a distinct token from "Failed" -- pinned here too, so both halves of the
        // contract are proven together.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-status-json-rejected-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Paused, pausedResult.State.Status);
            var pausedExecutionId = pausedResult.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;

            var decideOptions = new DecideOptions(
                roomDirectory, pausedExecutionId.Value, DecisionType.Reject, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);
            await DecideCommand.ExecuteAsync(decideOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            using var stdout = new StringWriter();
            await StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Json: true), stdout, TestContext.Current.CancellationToken);

            var rawJson = stdout.ToString();
            var view = ParseSingleObject(rawJson);
            Assert.Equal("Failed", view.State);
            var step = Assert.Single(view.Steps);
            Assert.Equal("Rejected", step.State);
            Assert.Null(view.Error);
            Assert.True(view.Rejected);
            Assert.Contains("\"rejected\":true", rawJson, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_running_room_reports_state_Running_with_the_finished_step_already_Succeeded()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-status-json-running-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteTwoStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteTwoStepBindingsAsync(testRoot, SleepThenWriteCommand("out_b", seconds: 5));
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var runTask = RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                WorkflowStatusView? view = null;
                while (DateTime.UtcNow < deadline)
                {
                    if (Directory.Exists(roomDirectory))
                    {
                        using var stdout = new StringWriter();
                        try
                        {
                            await StatusCommand.ExecuteAsync(
                                new StatusOptions(roomDirectory, Json: true), stdout, TestContext.Current.CancellationToken);
                            var candidate = ParseSingleObject(stdout.ToString());
                            if (candidate.Steps.Any(s => s.Id == "b" && s.State == "Running"))
                            {
                                view = candidate;
                                break;
                            }
                        }
                        catch (SnapshotLoadException)
                        {
                            // Not persisted yet -- keep polling.
                        }
                    }

                    // wait-ok: re-check cadence while waiting for step 'b' to show Running; capped by the 20s deadline above.
                    await Task.Delay(50, TestContext.Current.CancellationToken);
                }

                Assert.NotNull(view);
                Assert.Equal("Running", view!.State);
                var stepA = view.Steps.Single(s => s.Id == "a");
                Assert.Equal("Succeeded", stepA.State);
                var stepB = view.Steps.Single(s => s.Id == "b");
                Assert.Equal("Running", stepB.State);
            }
            finally
            {
                await runTask;
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static WorkflowStatusView ParseSingleObject(string stdout)
    {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var singleLine = Assert.Single(lines);

        // Also proves #1356 point 1's "nothing else on stdout in json mode": one line, one object.
        var view = JsonSerializer.Deserialize<WorkflowStatusView>(singleLine);
        Assert.NotNull(view);
        return view!;
    }

    private static async Task<string> WriteOneStepWorkflowAsync(string directory, string stepId)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("one-step"), 1,
            [new WorkflowStepDefinition(new StepId(stepId), stepId, [], ["plan"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteOneStepBindingsAsync(
        string directory,
        string command,
        TaskSizeDeclaration? declaredTaskSize = null)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["solo"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("solo", [], [new ProducedOutput("plan")], []), command,
                TimeSpan.FromSeconds(30), DeclaredTaskSize: declaredTaskSize),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteApprovalGateWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("status-json-approval-gate"), 1,
            [new WorkflowStepDefinition(new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1), new PausePoint([]))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteApprovalGateBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                WriteFileCommand("out_a", "a-out"), TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteTwoStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("two-step-running"), 1,
            [
                new WorkflowStepDefinition(new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("b"), "b", [], ["out_b"], [new StepId("a")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteTwoStepBindingsAsync(string directory, string stepBCommand)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                WriteFileCommand("out_a", "a-done"), TimeSpan.FromSeconds(30)),
            ["b"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("b", [], [new ProducedOutput("out_b")], []),
                stepBCommand, TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) =>
        $"echo {content}>%BATON_OUTPUT_DIR%\\{outputName}";

    // #1360: writes the declared output AND echoes a claude-shaped stream-json result line to
    // stdout, which ExecutionStreamLogger captures verbatim -- proving ExecutionUsageProjector reads
    // real captured stdout, not a test-only seam.
    private const string ClaudeResultLine =
        """{"type":"result","num_turns":2,"usage":{"input_tokens":10,"output_tokens":5}}""";

    /// <summary>
    /// Writes a small script file to <paramref name="scriptDirectory"/> and returns a command that
    /// invokes it, rather than embedding <see cref="ClaudeResultLine"/>'s literal double quotes
    /// directly on the binding's own command line: <c>ShellCommandWorkerAdapter</c> passes that line
    /// through <c>cmd /c</c> as a single <c>ArgumentList</c> element, and on Windows .NET's own argv
    /// re-quoting for an argument containing embedded quotes escapes them as <c>\"</c> — bytes cmd
    /// then hands to <c>echo</c> literally, corrupting the JSON (and doing the same to a quoted path,
    /// which is why the invoking command below stays unquoted rather than only the JSON). A script
    /// file sidesteps that: its content is written directly by
    /// <see cref="File.WriteAllText(string, string)"/>, never round-tripped through command-line
    /// quoting at all. <see cref="Path.GetTempPath"/> is assumed space-free here, which holds for
    /// every CI/dev host this suite runs on.
    /// </summary>
    private static string WriteFileAndEchoClaudeResultCommand(string scriptDirectory, string outputName, string content)
    {
        var scriptPath = Path.Combine(scriptDirectory, "echo-claude-result.cmd");
        File.WriteAllText(
            scriptPath,
            $"@echo off\r\necho {ClaudeResultLine}\r\necho {content}>%BATON_OUTPUT_DIR%\\{outputName}\r\n");
        return $"call {scriptPath}";
    }

    private static string SleepThenWriteCommand(string outputName, int seconds) =>
        $"ping -n {seconds + 1} 127.0.0.1>nul & echo done>%BATON_OUTPUT_DIR%\\{outputName}";
}
