using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Baton.Accounting;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Outcomes;
using Baton.Status;
using Baton.Store;
using Baton.Vendors;
using Baton.Workspaces;

namespace Baton.Cli.Tests;

public sealed class AgyExactRunningTests
{
    [Fact]
    public Task Capacity_wait_is_observed_and_bounded_at_live_and_restart_seams() =>
        Actual_live_and_restart_success_arms_require_the_final_turn("terminal-timeout", "capacity");

    [Theory]
    [InlineData("ordinary", "missing")]
    [InlineData("late-failure", "capacity")]
    [InlineData("terminal-timeout", "missing")]
    [InlineData("finished-timeout", "missing")]
    [InlineData("pre-cap", "failure")]
    public Task Success_gate_wiring_controls(string arm, string mode) =>
        Actual_live_and_restart_success_arms_require_the_final_turn(arm, mode);

    [Fact]
    public Task Selected_post_cap_checkpoint_exclusion_is_reachable() =>
        Actual_live_and_restart_success_arms_require_the_final_turn("post-cap", "success");

    [Theory]
    [InlineData("ordinary", "success")]
    [InlineData("late-failure", "capacity")]
    [InlineData("terminal-timeout", "success")]
    [InlineData("finished-timeout", "success")]
    public async Task Actual_one_shot_success_arms_have_positive_launch_controls(string arm, string mode)
    {
        await using var fixture = await Fixture.CreateAsync(mode, git: true, live: true, arm: arm, selected: false);
        var result = await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await fixture.WaitSettlementAsync();
        var events = await new FlowEventLogReader(fixture.Log).ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.True(events.OfType<FlowEvent.ExecutionSucceeded>().Any() || events.OfType<FlowEvent.ExecutionSucceededWithLateFailure>().Any(),
            $"Core result: {result.Reason}/{result.ExitCode}, stderr: {result.StderrTail}; journal: {fixture.ReadJournal()}");
        if (arm == "late-failure") Assert.Single(events.OfType<FlowEvent.ExecutionSucceededWithLateFailure>());
        if (arm.Contains("timeout", StringComparison.Ordinal)) Assert.Equal(CoreExitReason.TimedOut, result.Reason);
        Assert.Equal(arm == "terminal-timeout", result.Reason == CoreExitReason.TimedOut && result.TerminalSuccessObserved);
        Assert.Equal(fixture.Prompt, File.ReadAllText(Path.Combine(fixture.Output, "first.txt")));
        // Raw quota evidence is stdout-only and Core's immutable exit does not persist it.
        // Live late-failure salvage is reachable; Core-only restart cannot reconstruct that hint.
        Assert.Equal(arm != "late-failure", await fixture.ReconstructAsync());
        if (arm == "late-failure")
        {
            var restarted = await new FlowEventLogReader(Path.Combine(fixture.Room, "restart", BatonPaths.FlowLogFileName))
                .ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(restarted.OfType<FlowEvent.ExecutionFailed>());
            Assert.Empty(restarted.OfType<FlowEvent.ExecutionSucceededWithLateFailure>());
        }
    }

    public static TheoryData<string, string> RawAuthorityCases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var authority in new[] { "accept", "start", "exit" })
                foreach (var variant in new[] { "conflict", "reordered", "identical", "valid-reordered" }) data.Add(authority, variant);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RawAuthorityCases))]
    public async Task Raw_completion_authority_is_unambiguous_at_live_and_restart_settlement(string authority, string variant)
    {
        await using var fixture = await Fixture.CreateAsync("success", live: true, beforeSettlement: f =>
        {
            var lines = f.ReadJournal().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var kind = authority switch { "accept" => "executionRequestAccepted", "start" => "executionStarted", _ => "executionExited" };
            var index = Array.FindIndex(lines, line => line.Contains("\"eventType\":\"" + kind + "\"", StringComparison.Ordinal));
            Assert.True(index >= 0);
            var property = authority switch { "accept" => "ExactRunningTransport", "start" => "Pid", _ => "Successful" };
            var pattern = "\"" + property + "\":(?<value>\"[^\"]*\"|true|[0-9]+)";
            lines[index] = System.Text.RegularExpressions.Regex.Replace(lines[index], pattern, match =>
            {
                var value = match.Groups["value"].Value;
                if (variant == "valid-reordered") return match.Value;
                var other = variant == "identical" ? value : authority switch { "accept" => "null", "start" => "0", _ => "false" };
                return variant == "reordered"
                    ? $"\"{property}\":{value},\"{property}\":{other}"
                    : $"\"{property}\":{other},\"{property}\":{value}";
            });
            if (variant == "valid-reordered")
            {
                using var document = JsonDocument.Parse(lines[index]);
                // Keep polymorphic metadata first; reverse the other root properties.
                lines[index] = "{" + string.Join(",", document.RootElement.EnumerateObject()
                    .OrderBy(p => p.Name == "owner" ? 0 : 1).ThenByDescending(p => p.Name)
                    .Select(p => JsonSerializer.Serialize(p.Name) + ":" + p.Value.GetRawText())) + "}";
            }
            using var stream = new FileStream(f.Log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.Write(string.Join("\n", lines) + "\n");
            writer.Flush();
            f.AlignJournalWriter();
        });
        var identity = await fixture.WaitEndpointAsync();
        await AgyCorrectionClient.ExecuteAsync(fixture.Room, identity.ExecutionId, "raw", "second", TestContext.Current.CancellationToken);
        await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(fixture.Output, "report.md")));
        if (variant == "valid-reordered")
        {
            await fixture.WaitSettlementAsync();
            Assert.Single((await new FlowEventLogReader(fixture.Log).ReadAllAsync(TestContext.Current.CancellationToken)).OfType<FlowEvent.ExecutionSucceeded>());
            Assert.True(await fixture.ReconstructAsync());
        }
        else
        {
            var live = await Record.ExceptionAsync(async () => { await fixture.WaitSettlementAsync(); });
            var restart = await Record.ExceptionAsync(async () => { await fixture.ReconstructAsync(); });
            Assert.Multiple(
                () => Assert.IsType<FlowEventLogReadException>(live),
                () => Assert.IsType<FlowEventLogReadException>(restart));
        }
    }

    [Theory]
    [InlineData("success", false)]
    [InlineData("success", true)]
    [InlineData("negative", false)]
    [InlineData("negative", true)]
    [InlineData("overflow", false)]
    [InlineData("overflow", true)]
    [InlineData("cumulative", false)]
    [InlineData("cumulative", true)]
    public async Task Live_positive_token_and_rate_caps_never_subtract_or_wrap_usage(string mode, bool rate)
    {
        var cap = mode == "cumulative" ? long.MaxValue : 20L;
        await using var fixture = await Fixture.CreateAsync(mode, live: true, budget: rate ? null : cap, rate: rate ? cap : null);
        var identity = await fixture.WaitEndpointAsync();
        await AgyCorrectionClient.ExecuteAsync(fixture.Room, identity.ExecutionId, "limit", "second", TestContext.Current.CancellationToken);
        var result = await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await fixture.WaitSettlementAsync();
        var events = await new FlowEventLogReader(fixture.Log).ReadAllAsync(TestContext.Current.CancellationToken);
        if (mode == "success")
        {
            var arrest = Assert.Single(events.OfType<FlowEvent.ExecutionArrested>());
            Assert.Equal(rate ? ArrestReason.BilledRate : ArrestReason.TokenBudget, arrest.Reason);
            Assert.Equal(35, arrest.Usage?.BilledTokens);
            // A fully recorded successful final may finish the read-shaped contract during
            // cap teardown. Preserve that salvage while retaining the truthful arrest account.
            Assert.Equal(result.FinalExpectedTurn?.IsSuccessful(AgyStreamingHost.Transport) == true,
                events.OfType<FlowEvent.ExecutionSucceeded>().Any());
        }
        else
        {
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.False(result.FinalExpectedTurn?.IsSuccessful(AgyStreamingHost.Transport));
            var failure = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(mode == "cumulative" ? long.MaxValue - 5 : 12, failure.PeakBilledInWindow);
        }
        Assert.Empty(events.OfType<FlowEvent.ArtifactCheckpointAttempted>());
        Assert.Empty(events.OfType<FlowEvent.GraceTurnClaimed>());
    }

    [Theory]
    [InlineData("malformed-missing", false)]
    [InlineData("malformed-missing", true)]
    [InlineData("malformed-null", false)]
    [InlineData("malformed-null", true)]
    [InlineData("malformed-bool", false)]
    [InlineData("malformed-bool", true)]
    [InlineData("malformed-object", false)]
    [InlineData("malformed-object", true)]
    [InlineData("malformed-array", false)]
    [InlineData("malformed-array", true)]
    [InlineData("valid-string", false)]
    [InlineData("valid-string", true)]
    public async Task Terminal_step_identity_is_selected_before_live_usage_admission(string mode, bool rate)
    {
        var malformed = mode.StartsWith("malformed-", StringComparison.Ordinal);
        var cap = malformed ? 20L : 100L;
        await using var fixture = await Fixture.CreateAsync(mode, live: true, budget: rate ? null : cap, rate: rate ? cap : null);
        var identity = await fixture.WaitEndpointAsync();
        await AgyCorrectionClient.ExecuteAsync(fixture.Room, identity.ExecutionId, "identity", "second", TestContext.Current.CancellationToken);
        var result = await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await fixture.WaitSettlementAsync();
        var events = await new FlowEventLogReader(fixture.Log).ReadAllAsync(TestContext.Current.CancellationToken);
        var entries = await new FlowEventLogReader(fixture.Log).ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);
        var usage = Assert.Single(ExecutionUsageProjector.BuildByExecutionId(entries, fixture.Artifacts)).Value;

        if (malformed)
        {
            Assert.False(result.FinalExpectedTurn?.IsSuccessful(AgyStreamingHost.Transport));
            Assert.Empty(events.OfType<FlowEvent.ExecutionArrested>());
            Assert.Empty(events.OfType<FlowEvent.GraceTurnClaimed>());
            Assert.Empty(events.OfType<FlowEvent.ArtifactCheckpointAttempted>());
            Assert.Equal(10, usage.TokensIn);
            Assert.Equal(2, usage.TokensOut);
            Assert.Equal(12, usage.ObservedBilledTokenFloor?.Tokens);
            Assert.NotNull(usage.BilledReconciliationUnavailable);
            Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
        }
        else
        {
            Assert.True(result.FinalExpectedTurn?.IsSuccessful(AgyStreamingHost.Transport));
            Assert.Equal(35, usage.ObservedBilledTokenFloor?.Tokens);
            Assert.Equal(35, usage.TokensIn.GetValueOrDefault() + usage.TokensOut.GetValueOrDefault());
        }
    }

    public static TheoryData<string, string> SuccessArmCases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var arm in new[] { "ordinary", "late-failure", "terminal-timeout", "finished-timeout", "pre-cap", "post-cap" })
                foreach (var mode in new[] { "failure", "capacity", "missing", "timeout", "success" }) data.Add(arm, mode);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SuccessArmCases))]
    public async Task Actual_live_and_restart_success_arms_require_the_final_turn(string arm, string mode)
    {
        var capArm = arm is "pre-cap" or "post-cap";
        await using var fixture = await Fixture.CreateAsync(mode, git: !capArm, live: true, budget: capArm ? 20 : null, arm: arm);
        var identity = await fixture.WaitEndpointAsync();
        await AgyCorrectionClient.ExecuteAsync(fixture.Room, identity.ExecutionId, "matrix", "second", TestContext.Current.CancellationToken);
        var result = await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var state = await fixture.WaitSettlementAsync();
        var events = await new FlowEventLogReader(fixture.Log).ReadAllAsync(TestContext.Current.CancellationToken);
        var succeeds = mode == "success" && arm != "post-cap";
        // Before Flow's arrest append, restart has only Core's immutable exit. A natural
        // valid final can settle ordinarily; a cancellation never invents a cap success.
        var reconstructed = await fixture.ReconstructAsync();
        Assert.Multiple(
            () => Assert.Equal(succeeds, events.OfType<FlowEvent.ExecutionSucceeded>().Any()
                || events.OfType<FlowEvent.ExecutionSucceededWithLateFailure>().Any()),
            () => Assert.Equal(succeeds ? StepStatus.Succeeded : StepStatus.Failed, Assert.Single(state.Steps).Status),
            () => Assert.Equal(succeeds && result.Reason != CoreExitReason.CancelRequested, reconstructed));
        if (mode == "capacity" && !capArm)
        {
            // Observe the actual quota wait before stopping this fixture's pump.
            Assert.NotNull(fixture.LiveQuotaRetryNotBefore);
            Assert.Equal(WorkflowStatus.Running, state.Status);
            Assert.True(Assert.Single(state.Steps).RetryNotBefore > DateTimeOffset.UtcNow);
            var failure = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(FailureClassification.ExhaustedUntil, failure.FailureClassification);
            Assert.True(failure.RetryNotBefore > DateTimeOffset.UtcNow);
            Assert.Single(events.OfType<FlowEvent.StepRetryScheduled>());
        }
        Assert.Empty(events.OfType<FlowEvent.GraceTurnClaimed>());
        Assert.Empty(events.OfType<FlowEvent.ArtifactCheckpointAttempted>());
        Assert.Single((await new FlowEventLogReader(fixture.Log).ReadSnapshotAsync(TestContext.Current.CancellationToken))
            .CoreEvents.OfType<CoreEvent.ExecutionStarted>());
        Assert.False(File.Exists(Path.Combine(fixture.Output, "checkpoint.txt")));
        Assert.Equal("agy:write_to_file", File.ReadAllText(Path.Combine(fixture.Output, "grant.txt")));
        Assert.Equal(fixture.Prompt, File.ReadAllText(Path.Combine(fixture.Output, "first.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(fixture.Output, "second.txt")));
        if (!capArm) Assert.True(WorktreeProvisioner.Audit(fixture.Workspace).IsClean);
        if (arm.Contains("timeout", StringComparison.Ordinal)) Assert.Equal(CoreExitReason.TimedOut, result.Reason);
        if (arm == "terminal-timeout") Assert.Equal(mode == "success", result.TerminalSuccessObserved);
        if (arm == "finished-timeout") Assert.False(result.TerminalSuccessObserved);
        if (arm == "post-cap") Assert.False(File.Exists(Path.Combine(fixture.Output, "report.md")));
        if (capArm) Assert.Single(events.OfType<FlowEvent.ExecutionArrested>());
        if (mode == "capacity" && !capArm)
        {
            // Core's immutable exit does not persist StdoutTail, so that checkpoint fails
            // closed without reconstructing the stdout-only quota reset. Once Flow records
            // ExhaustedUntil and its retry, restart must preserve and observe the real wait.
            Assert.Null(fixture.RestartQuotaRetryNotBefore);
            Assert.False(await fixture.ReconstructAsync(quotaWait: true));
            Assert.NotNull(fixture.RestartQuotaRetryNotBefore);
        }
    }

    [Fact]
    public async Task Durable_claim_without_send_requires_the_second_turn_at_exit_and_restart()
    {
        await using var fixture = await Fixture.CreateAsync("claim-only");
        var identity = await fixture.WaitEndpointAsync();
        var store = new AgyCorrectionStore(fixture.Room);
        Assert.True(store.TryClaim(new(identity, "claim", AgyCorrectionStore.Digest("second"))));
        var retained = await AgyCorrectionClient.ExecuteAsync(fixture.Room, identity.ExecutionId, "claim", "second",
            TestContext.Current.CancellationToken);
        Assert.Equal("claimedBeforeSend", retained!.State);
        File.WriteAllText(Path.Combine(fixture.Output, "release"), "release");
        var result = await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.False(result.FinalExpectedTurn?.IsSuccessful(AgyStreamingHost.Transport));
        Assert.Equal(2, result.FinalExpectedTurn?.ExpectedTurn);
        Assert.Equal(OutcomeVerdict.Failed, OutcomeClassifier.Classify(result, fixture.Contract, fixture.Output).Verdict);
        await MutationInterface.StartWorkflowAsync(fixture.Request.WorkflowId, fixture.Room, fixture.Snapshot,
            fixture.Bindings, fixture.Artifacts, new FlowEventLogReader(fixture.Log), fixture.Writer, new NoLaunchDispatcher(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty((await new FlowEventLogReader(fixture.Log).ReadAllAsync(TestContext.Current.CancellationToken))
            .OfType<FlowEvent.ExecutionSucceeded>());
    }

    [Fact]
    public async Task Rejected_pipe_request_leaves_the_endpoint_available_for_a_valid_correction()
    {
        await using var fixture = await Fixture.CreateAsync("success");
        var identity = await fixture.WaitEndpointAsync();
        using (var pipe = new NamedPipeClientStream(".", identity.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Impersonation))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await pipe.ConnectAsync(deadline.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new AgyCorrectionWireRequest(
                identity with { ConversationId = "wrong" }, "invalid", "wrong")) + "\n"), deadline.Token);
            await pipe.FlushAsync(deadline.Token);
            Assert.Null(await reader.ReadLineAsync(deadline.Token));
        }
        var receipt = await AgyCorrectionClient.ExecuteAsync(fixture.Room, identity.ExecutionId, "valid", "second",
            TestContext.Current.CancellationToken);
        Assert.NotNull(receipt);
        var result = await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.True(result.FinalExpectedTurn?.IsSuccessful(AgyStreamingHost.Transport));
    }

    [Theory]
    [InlineData("failure", false)]
    [InlineData("capacity", false)]
    [InlineData("missing", false)]
    [InlineData("timeout", false)]
    [InlineData("forged", false)]
    [InlineData("corrupt", false)]
    [InlineData("conflict", false)]
    [InlineData("success", true)]
    [InlineData("duplicate", true)]
    public async Task First_turn_commits_pushes_and_outputs_cannot_hide_the_final_turn(string mode, bool succeeds)
    {
        await using var fixture = await Fixture.CreateAsync(mode, git: true);
        var endpoint = await fixture.WaitEndpointAsync();
        Assert.True(endpoint.ProcessesLive());
        Assert.True(WorktreeProvisioner.Audit(fixture.Workspace).IsClean);
        var payload = "correction\r\nkeep the final LF\n";
        var path = Path.Combine(fixture.Room, "correction.txt");
        await File.WriteAllTextAsync(path, payload, TestContext.Current.CancellationToken);
        var output = new StringWriter();
        Assert.Equal(1, await SteerCommand.ExecuteAsync(new(fixture.Room, fixture.Request.ExecutionId.Value, "message", path, false),
            output, TestContext.Current.CancellationToken));
        Assert.Contains("outcomeUnknown", output.ToString());
        var result = await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(payload, File.ReadAllText(Path.Combine(fixture.Output, "second.txt")));
        Assert.Equal(fixture.Prompt, File.ReadAllText(Path.Combine(fixture.Output, "first.txt")));
        Assert.Equal("agy:write_to_file", File.ReadAllText(Path.Combine(fixture.Output, "grant.txt")));
        var classified = OutcomeClassifier.Classify(result, fixture.Contract, fixture.Output,
            failureClassifier: new AgyWorkerAdapter(),
            changesTree: true, changesTreeWorkingDirectory: fixture.Workspace, verifiesWorkspace: false);
        Assert.Equal(succeeds ? OutcomeVerdict.Succeeded : OutcomeVerdict.Failed, classified.Verdict);
        if (!succeeds) Assert.Contains("final expected turn", classified.Reason);
        if (mode == "capacity")
        {
            Assert.True(result.FinalExpectedTurn?.EvidenceValid);
            Assert.Contains("Individual quota reached", result.StdoutTail);
            Assert.True(AgyWorkerAdapter.TryClassifyQuotaExhaustionFromResultEnvelope(result.StdoutTail, TimeProvider.System, out _, out _), result.StdoutTail);
            Assert.Equal(FailureClassification.ExhaustedUntil, classified.FailureClassification);
        }

        // The real restart classifier consumes the durable Core exit, never the mutable stdout footer.
        var reader = new FlowEventLogReader(fixture.Log);
        var exited = Assert.Single((await reader.ReadSnapshotAsync(TestContext.Current.CancellationToken)).CoreEvents.OfType<CoreEvent.ExecutionExited>());
        Assert.Equal(result.FinalExpectedTurn, exited.FinalExpectedTurn);
        Assert.Equal(AgyStreamingHost.Transport, exited.ExactRunningTransport);
        await MutationInterface.StartWorkflowAsync(fixture.Request.WorkflowId, fixture.Room, fixture.Snapshot,
            fixture.Bindings, fixture.Artifacts, reader, fixture.Writer, new NoLaunchDispatcher(),
            cancellationToken: TestContext.Current.CancellationToken);
        var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(succeeds, events.OfType<FlowEvent.ExecutionSucceeded>().Any());
        Assert.Equal(!succeeds, events.OfType<FlowEvent.ExecutionFailed>().Any());
        Assert.Empty(events.OfType<FlowEvent.GraceTurnClaimed>());
        Assert.Empty(events.OfType<FlowEvent.ArtifactCheckpointAttempted>());
        var receipt = await AgyCorrectionClient.ExecuteAsync(fixture.Room, fixture.Request.ExecutionId.Value, "message", null,
            TestContext.Current.CancellationToken);
        Assert.NotNull(receipt);
        Assert.Equal("outcomeUnknown", receipt.State);
        Assert.True(receipt.SendStarted);
        Assert.NotNull(receipt.ConsumptionEvidence);
        Assert.Equal(receipt, await AgyCorrectionClient.ExecuteAsync(fixture.Room, fixture.Request.ExecutionId.Value, "message", payload,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgyCorrectionClient.ExecuteAsync(fixture.Room,
            fixture.Request.ExecutionId.Value, "message", "changed", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("one")]
    [InlineData("descendants")]
    public async Task Result_wins_or_final_result_closes_the_owned_tree(string mode)
    {
        await using var fixture = await Fixture.CreateAsync(mode);
        if (mode != "one")
        {
            await fixture.WaitEndpointAsync();
            await AgyCorrectionClient.ExecuteAsync(fixture.Room, fixture.Request.ExecutionId.Value, "message", "second",
                TestContext.Current.CancellationToken);
        }
        var result = await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.True(result.FinalExpectedTurn?.IsSuccessful(AgyStreamingHost.Transport));
        Assert.Null(AgyStreamingHost.ReadEndpoint(fixture.Room, fixture.Request.ExecutionId.Value));
        if (mode == "one")
        {
            Assert.False(File.Exists(Path.Combine(fixture.Output, "second.txt")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => AgyCorrectionClient.ExecuteAsync(fixture.Room,
                fixture.Request.ExecutionId.Value, "late", "late correction", TestContext.Current.CancellationToken));
        }
        else
        {
            using var descendant = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output, "descendant.json")));
            Assert.NotEqual(EngineLivenessStatus.Alive, EngineLivenessProbe.Probe(descendant.RootElement.GetProperty("pid").GetInt32(),
                descendant.RootElement.GetProperty("birth").GetDateTime()).Status);
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r\nfinal\n")]
    public async Task Streaming_usage_is_an_observed_floor_and_final_report_is_separate(string suffix)
    {
        await using var fixture = await Fixture.CreateAsync("duplicate");
        await fixture.WaitEndpointAsync();
        var text = "é🙂" + suffix;
        var receipt = await AgyCorrectionClient.ExecuteAsync(fixture.Room, fixture.Request.ExecutionId.Value, "usage", text,
            TestContext.Current.CancellationToken);
        Assert.Equal(AgyCorrectionStore.Digest(text), receipt!.Request.PayloadSha256);
        await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(text, File.ReadAllText(Path.Combine(fixture.Output, "second.txt")));
        var entries = await new FlowEventLogReader(fixture.Log).ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);
        var usage = Assert.Single(ExecutionUsageProjector.BuildByExecutionId(entries, fixture.Artifacts)).Value;
        Assert.Equal(30, usage.TokensIn);
        Assert.Equal(5, usage.TokensOut);
        Assert.Equal(35, usage.ObservedBilledTokenFloor?.Tokens);
        Assert.Equal("incomplete", usage.ObservedBilledTokenFloor?.Completeness);
        var summary = StatusCommand.FormatUsageSummary(new Dictionary<string, ExecutionUsageView> { ["selected"] = usage });
        Assert.Contains("35 observed billed token floor", summary);
        Assert.Contains("observed incomplete contribution(s)", summary);
        Assert.Contains("execution totals unavailable", summary);
        Assert.Equal(999, usage.ReportedFinalTurnUsage?.TokensIn);
        Assert.Equal(888, usage.ReportedFinalTurnUsage?.TokensOut);
        Assert.Null(usage.BilledTokens);
        Assert.Null(usage.LiveBilledTokens);
        Assert.Null(usage.BilledUnderReadTokens);
        Assert.Equal(ExecutionUsageView.StreamingExecutionCompletenessUnavailable, usage.BilledReconciliationUnavailable);
        var cost = Assert.Single(CostLedgerStore.BuildEntries(entries, fixture.Room, null));
        Assert.Equal(CostCompleteness.Partial, cost.Completeness);
        Assert.Equal(usage.ReportedFinalTurnUsage, cost.ReportedFinalTurnUsage);
        Assert.Null(cost.ApiEquivalentUsd);
        var quota = Assert.Single(QuotaLedgerStore.BuildEntries(entries, fixture.Room));
        Assert.Equal(30, quota.TokensIn);
        Assert.Equal("unavailable", quota.UsageCompleteness);
        Assert.Equal(usage.ReportedFinalTurnUsage, quota.ReportedFinalTurnUsage);
    }

    [Theory]
    [InlineData("Room")]
    [InlineData("Execution")]
    [InlineData("Adapter")]
    [InlineData("Provenance")]
    [InlineData("HostPid")]
    [InlineData("HostBirth")]
    [InlineData("ChildPid")]
    [InlineData("ChildBirth")]
    [InlineData("Conversation")]
    [InlineData("Incarnation")]
    [InlineData("Pipe")]
    [InlineData("Principal")]
    public async Task Every_identity_component_is_refused_before_claim_without_disabling_the_endpoint(string component)
    {
        await using var fixture = await Fixture.CreateAsync("success");
        var identity = await fixture.WaitEndpointAsync();
        var wrong = component switch
        {
            "Room" => identity with { Room = identity.Room + "-wrong" },
            "Execution" => identity with { ExecutionId = "wrong" },
            "Adapter" => identity with { Adapter = "claude" },
            "Provenance" => identity with { ProvenanceSha256 = new string('0', 64) },
            "HostPid" => identity with { HostPid = int.MaxValue },
            "HostBirth" => identity with { HostStartUtc = identity.HostStartUtc.AddSeconds(-1) },
            "ChildPid" => identity with { ChildPid = int.MaxValue },
            "ChildBirth" => identity with { ChildStartUtc = identity.ChildStartUtc.AddSeconds(-1) },
            "Conversation" => identity with { ConversationId = "wrong" },
            "Incarnation" => identity with { Incarnation = new string('0', 32), PipeName = "baton-agy-" + new string('0', 32) },
            "Pipe" => identity with { PipeName = "wrong" },
            "Principal" => identity with { OsPrincipal = "wrong" },
            _ => throw new InvalidOperationException(component),
        };
        using var pipe = new NamedPipeClientStream(".", identity.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Impersonation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await pipe.ConnectAsync(deadline.Token);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new AgyCorrectionWireRequest(wrong, "wrong", "wrong")) + "\n"), deadline.Token);
        Assert.Null(await reader.ReadLineAsync(deadline.Token));
        Assert.Null(new AgyCorrectionStore(fixture.Room).Query(identity.ExecutionId));
        Assert.False(File.Exists(Path.Combine(fixture.Output, "second.txt")));
        Assert.NotNull(await AgyCorrectionClient.ExecuteAsync(fixture.Room, identity.ExecutionId, "valid", "second",
            TestContext.Current.CancellationToken));
        Assert.True((await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken))
            .FinalExpectedTurn?.IsSuccessful(AgyStreamingHost.Transport));
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("overflow")]
    public async Task Invalid_step_usage_cannot_produce_reconciliation_or_complete_price(string mode)
    {
        await using var fixture = await Fixture.CreateAsync(mode);
        var identity = await fixture.WaitEndpointAsync();
        await AgyCorrectionClient.ExecuteAsync(fixture.Room, identity.ExecutionId, "usage", "second", TestContext.Current.CancellationToken);
        await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var entries = await new FlowEventLogReader(fixture.Log).ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);
        var usage = Assert.Single(ExecutionUsageProjector.BuildByExecutionId(entries, fixture.Artifacts)).Value;
        Assert.Null(usage.TokensIn);
        Assert.Null(usage.ObservedBilledTokenFloor);
        Assert.Null(usage.BilledUnderReadTokens);
        var cost = Assert.Single(CostLedgerStore.BuildEntries(entries, fixture.Room, null));
        Assert.Null(cost.ApiEquivalentUsd);
        Assert.NotEqual(CostCompleteness.Complete, cost.Completeness);
        Assert.Equal("unavailable", Assert.Single(QuotaLedgerStore.BuildEntries(entries, fixture.Room)).UsageCompleteness);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("timeout")]
    public async Task Incomplete_final_turn_retains_only_the_observed_execution_floor(string mode)
    {
        await using var fixture = await Fixture.CreateAsync(mode);
        var identity = await fixture.WaitEndpointAsync();
        await AgyCorrectionClient.ExecuteAsync(fixture.Room, identity.ExecutionId, "usage", "second", TestContext.Current.CancellationToken);
        await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var entries = await new FlowEventLogReader(fixture.Log).ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);
        var usage = Assert.Single(ExecutionUsageProjector.BuildByExecutionId(entries, fixture.Artifacts)).Value;
        Assert.Equal(30, usage.TokensIn);
        Assert.Equal(35, usage.ObservedBilledTokenFloor?.Tokens);
        Assert.Null(usage.ReportedFinalTurnUsage);
        Assert.Null(usage.CacheReadTokens);
        Assert.Null(usage.CacheCreationTokens);
        Assert.Null(usage.BilledTokens);
        Assert.Equal("unavailable", usage.UsageCompleteness);
        Assert.Null(Assert.Single(CostLedgerStore.BuildEntries(entries, fixture.Room, null)).ApiEquivalentUsd);
    }

    [Fact]
    public async Task Cancellation_reaps_the_child_and_descendant_and_removes_the_endpoint()
    {
        await using var fixture = await Fixture.CreateAsync("timeout");
        var identity = await fixture.WaitEndpointAsync();
        await AgyCorrectionClient.ExecuteAsync(fixture.Room, identity.ExecutionId, "cancel", "second", TestContext.Current.CancellationToken);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!File.Exists(Path.Combine(fixture.Output, "descendant.json")))
            await Task.Delay(20, deadline.Token); // wait-ok: bounded descendant rendezvous
        fixture.Cancel();
        var result = await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(CoreExitReason.CancelRequested, result.Reason);
        Assert.False(result.FinalExpectedTurn?.IsSuccessful(AgyStreamingHost.Transport));
        Assert.False(identity.ProcessesLive());
        Assert.Null(AgyStreamingHost.ReadEndpoint(fixture.Room, identity.ExecutionId));
        using var descendant = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output, "descendant.json")));
        Assert.NotEqual(EngineLivenessStatus.Alive, EngineLivenessProbe.Probe(descendant.RootElement.GetProperty("pid").GetInt32(),
            descendant.RootElement.GetProperty("birth").GetDateTime()).Status);
    }

    [Theory]
    [InlineData("child")]
    [InlineData("birth")]
    [InlineData("missing")]
    public async Task Newer_or_inconsistent_exit_evidence_cannot_reconstruct_success(string corruption)
    {
        await using var fixture = await Fixture.CreateAsync("success");
        var identity = await fixture.WaitEndpointAsync();
        await AgyCorrectionClient.ExecuteAsync(fixture.Room, identity.ExecutionId, "second", "second", TestContext.Current.CancellationToken);
        await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var reader = new FlowEventLogReader(fixture.Log);
        var exit = Assert.Single((await reader.ReadSnapshotAsync(TestContext.Current.CancellationToken)).CoreEvents.OfType<CoreEvent.ExecutionExited>());
        var changed = corruption switch
        {
            "child" => exit with { FinalExpectedTurn = exit.FinalExpectedTurn! with { ChildPid = int.MaxValue } },
            "birth" => exit with { FinalExpectedTurn = exit.FinalExpectedTurn! with { ChildStartUtc = identity.ChildStartUtc.AddSeconds(-1) } },
            "missing" => exit with { FinalExpectedTurn = null },
            _ => throw new InvalidOperationException(corruption),
        };
        await fixture.Writer.AppendAsync(changed, TestContext.Current.CancellationToken);
        await MutationInterface.StartWorkflowAsync(fixture.Request.WorkflowId, fixture.Room, fixture.Snapshot,
            fixture.Bindings, fixture.Artifacts, reader, fixture.Writer, new NoLaunchDispatcher(),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty((await reader.ReadAllAsync(TestContext.Current.CancellationToken)).OfType<FlowEvent.ExecutionSucceeded>());
    }

    [Theory]
    [InlineData("torn")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("duplicate-property")]
    public async Task Corrupt_side_record_is_neither_replay_authority_nor_final_success(string corruption)
    {
        await using var fixture = await Fixture.CreateAsync("claim-only");
        var identity = await fixture.WaitEndpointAsync();
        var store = new AgyCorrectionStore(fixture.Room);
        var request = new AgyCorrectionRequest(identity, "claim", AgyCorrectionStore.Digest("second"));
        Assert.True(store.TryClaim(request));
        var path = AgyCorrectionStore.PathFor(fixture.Room, identity.ExecutionId, "correction.jsonl");
        var original = File.ReadAllText(path);
        File.WriteAllText(path, corruption switch
        {
            "torn" => original[..^1],
            "unknown" => original + "{\"Kind\":\"unknown\",\"Request\":null,\"Evidence\":null}\n",
            "duplicate" => original + original,
            "duplicate-property" => original.Replace("\"Kind\":\"claimed\"", "\"Kind\":\"claimed\",\"Kind\":\"claimed\"", StringComparison.Ordinal),
            _ => throw new InvalidOperationException(corruption),
        });
        Assert.Throws<InvalidOperationException>(() => store.Query(identity.ExecutionId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgyCorrectionClient.ExecuteAsync(fixture.Room,
            identity.ExecutionId, "claim", "second", TestContext.Current.CancellationToken));
        File.WriteAllText(Path.Combine(fixture.Output, "release"), "release");
        Assert.False((await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken))
            .FinalExpectedTurn?.IsSuccessful(AgyStreamingHost.Transport));
        Assert.False(File.Exists(Path.Combine(fixture.Output, "second.txt")));
    }

    [Fact]
    public async Task Ambiguous_send_started_only_returns_retained_evidence_without_native_bytes()
    {
        await using var fixture = await Fixture.CreateAsync("claim-only");
        var identity = await fixture.WaitEndpointAsync();
        var store = new AgyCorrectionStore(fixture.Room);
        var request = new AgyCorrectionRequest(identity, "claim", AgyCorrectionStore.Digest("second"));
        Assert.True(store.TryClaim(request));
        Assert.True(store.TryStartSend(request));
        Assert.False(store.TryStartSend(request));
        var retained = await AgyCorrectionClient.ExecuteAsync(fixture.Room, identity.ExecutionId, "claim", "second", TestContext.Current.CancellationToken);
        Assert.Equal("outcomeUnknown", retained!.State);
        Assert.Null(retained.InputEvidence);
        Assert.Equal(retained, await AgyCorrectionClient.ExecuteAsync(fixture.Room, identity.ExecutionId, "claim", null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgyCorrectionClient.ExecuteAsync(fixture.Room,
            identity.ExecutionId, "different", "second", TestContext.Current.CancellationToken));
        File.WriteAllText(Path.Combine(fixture.Output, "release"), "release");
        Assert.False((await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken))
            .FinalExpectedTurn?.IsSuccessful(AgyStreamingHost.Transport));
        Assert.False(File.Exists(Path.Combine(fixture.Output, "second.txt")));
    }

    [Fact]
    public async Task Concurrent_first_result_and_correction_have_one_serialized_winner()
    {
        await using var fixture = await Fixture.CreateAsync("race");
        var identity = await fixture.WaitEndpointAsync();
        var correction = Task.Run(async () =>
        {
            try { return await AgyCorrectionClient.ExecuteAsync(fixture.Room, identity.ExecutionId, "race", "second", TestContext.Current.CancellationToken); }
            catch (InvalidOperationException) { return null; } // the first result may already have closed admission
        }, TestContext.Current.CancellationToken);
        var release = Task.Run(() => File.WriteAllText(Path.Combine(fixture.Output, "release"), "release"), TestContext.Current.CancellationToken);
        await release;
        var receipt = await correction;
        var result = await fixture.Dispatch.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.True(result.FinalExpectedTurn?.IsSuccessful(AgyStreamingHost.Transport));
        Assert.NotNull(result.FinalExpectedTurn);
        Assert.Equal(receipt is null ? 1 : 2, result.FinalExpectedTurn.ExpectedTurn);
        Assert.Equal(receipt is not null, File.Exists(Path.Combine(fixture.Output, "second.txt")));
        if (receipt is not null) Assert.Equal("outcomeUnknown", receipt.State);
    }

    private sealed class ObservingDispatcher(ICoreDispatcher inner, Action<CoreDispatchResult> returned) : ICoreDispatcher
    {
        public async Task<CoreDispatchResult> DispatchAsync(ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            var result = await inner.DispatchAsync(request, target, cancellationToken);
            returned(result);
            return result;
        }
    }

    private sealed class NoLaunchDispatcher : ICoreDispatcher
    {
        public Task<CoreDispatchResult> DispatchAsync(ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Recovery must not launch another model.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Room { get; } = Path.Combine(Path.GetTempPath(), $"agy-exact-{Guid.NewGuid():N}");
        public string Artifacts => Path.Combine(Room, "artifacts");
        public string Log => Path.Combine(Room, BatonPaths.FlowLogFileName);
        public string Output => ArtifactManager.ResolveOutputDirectory(Artifacts, Request.ExecutionId);
        public string Workspace => Path.Combine(Room, "workspace");
        private bool _selected = true;
        public string Prompt => "resolved first prompt " + Output + "\n" + new string('p', _selected ? 33000 : 100);
        public WorkerContract Contract { get; } = new("worker", [], [new ProducedOutput("report.md", Schema: OutputSchema.NonEmptyText)], []);
        public ExecutionRequest Request { get; private set; } = null!;
        public FlowEventLogWriter Writer { get; private set; } = null!;
        private FileStream _journalStream = null!;
        public Task<CoreDispatchResult> Dispatch { get; private set; } = null!;
        public Task<FlowState>? Settlement { get; private set; }
        private string? _coreExitJournal;
        private string _caseName = "";
        public DateTimeOffset? LiveQuotaRetryNotBefore { get; private set; }
        public DateTimeOffset? RestartQuotaRetryNotBefore { get; private set; }
        public WorkflowDefinitionSnapshot Snapshot { get; private set; } = null!;
        public Dictionary<string, WorkerBinding> Bindings { get; private set; } = null!;
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        public void Cancel() => _stop.Cancel();

        public static async Task<Fixture> CreateAsync(string mode, bool git = false, bool live = false,
            long? budget = null, long? rate = null, string arm = "ordinary", Action<Fixture>? beforeSettlement = null, bool selected = true)
        {
            WorkerAdapterRegistry.InitializeStreamReaders();
            var f = new Fixture();
            f._selected = selected;
            f._caseName = arm + "/" + mode;
            Directory.CreateDirectory(f.Room);
            if (git && live) await Baton.CrashTestHost.AgyStreamProcessMode.PrepareWorkspaceAsync(f.Workspace);
            var execution = new ExecutionId("fixture-execution");
            ArtifactManager.AllocateOutputDirectory(f.Artifacts, execution);
            f.Request = new(execution, new WorkflowId("fixture-workflow"), new StepId("work"), "worker", [], ["report.md"],
                TimeSpan.FromSeconds(mode == "timeout" || arm.Contains("timeout", StringComparison.Ordinal) ? 10 : 20), ArtifactManager.BuildEnvironment([], ArtifactManager.ResolveOutputDirectory(f.Artifacts, execution), f.Artifacts),
                new Dictionary<StepId, ExecutionId>(), Adapter: "agy", Model: "gemini-3-flash", ProducedOutputs: f.Contract.ProducedOutputs,
                ExactRunningTransport: selected ? AgyStreamingHost.Transport : null);
            f._journalStream = new(f.Log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 1, useAsync: true);
            f.Writer = new(f._journalStream);
            using var host = Process.GetCurrentProcess();
            await f.Writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(f.Request, host.Id, new DateTimeOffset(host.StartTime).ToUniversalTime()));
            var target = new CoreDispatchTarget("dotnet", ["exec", typeof(Baton.CrashTestHost.AgyStreamProcessMode).Assembly.Location,
                "agy-stream-fixture", (selected ? "" : "raw-") + mode + ":" + arm, f.Output, git ? f.Workspace : "none"], WorkingDirectory: git ? null : f.Room,
                PromptText: selected ? f.Prompt.Replace(f.Output, "%BATON_OUTPUT_DIR%", StringComparison.Ordinal)
                    : JsonSerializer.Serialize(new { @event = "user", message = new { content = f.Prompt } }),
                Environment: [("BATON_HOOK_DENIED_TOOLS", "agy:write_to_file")],
                ExactRunningTransport: selected ? AgyStreamingHost.Transport : null,
                CreateProcessTransport: selected ? context => new AgyStreamingHost(context) : null,
                TryGetSessionId: selected ? line => new AgyWorkerAdapter().TryParseSessionId(line, out var session) ? session : null : null,
                ResumeArgs: selected && (arm is "pre-cap" or "post-cap") ? (_, _) =>
                    ["exec", typeof(Baton.CrashTestHost.AgyStreamProcessMode).Assembly.Location,
                     "agy-stream-fixture", "checkpoint", f.Output, "none"] : null,
                DetectsTerminalSuccess: arm == "finished-timeout" ? null : AgyWorkerAdapter.IsTerminalSuccessLine,
                DetectsTerminalResult: arm == "finished-timeout" ? null : AgyWorkerAdapter.IsTerminalResultLine);
            if (!selected) target = target with { Args = [.. target.Args, target.PromptText!] };
            target = target.WithReplacedPrompt(target.PromptText!);
            f.Snapshot = new(new WorkflowDefinitionSnapshotId("fixture-snapshot"), new WorkflowTemplateId("fixture"), 1,
                [new WorkflowStepDefinition(new StepId("work"), "worker", [], ["report.md"], [], new RetryPolicy(1))]);
            f.Bindings = new()
            {
                ["worker"] = new WorkerBinding.Process(f.Contract, target with { WorkingDirectory = git ? f.Workspace : f.Room }, f.Request.Timeout!.Value,
                    Adapter: "agy", Model: "gemini-3-flash", ChangesTree: git, VerifiesWorkspace: false, TokenBudget: budget, BilledRateLimit: rate,
                    FailureClassifier: new AgyWorkerAdapter())
            };
            if (live)
            {
                f._stop.CancelAfter(TimeSpan.FromSeconds(45));
                var completion = new TaskCompletionSource<CoreDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Dispatch = completion.Task;
                var dispatcher = new ObservingDispatcher(new CoreDispatcher(f.Writer, f.Writer), result =>
                {
                    beforeSettlement?.Invoke(f);
                    f._coreExitJournal = f.ReadJournal();
                    completion.TrySetResult(result);
                });
                f.Settlement = MutationInterface.StartWorkflowAsync(f.Request.WorkflowId, f.Room, f.Snapshot,
                    f.Bindings, f.Artifacts, new FlowEventLogReader(f.Log), f.Writer, dispatcher, cancellationToken: f._stop.Token,
                    onVendorQuotaPark: reset => { f.LiveQuotaRetryNotBefore = reset; f._stop.Cancel(); });
            }
            else f.Dispatch = new CoreDispatcher(f.Writer, f.Writer).DispatchAsync(f.Request, target, f._stop.Token);
            return f;
        }

        public async Task<FlowState> WaitSettlementAsync()
        {
            try { return await Settlement!.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken); }
            catch (TimeoutException ex)
            {
                _stop.Cancel();
                throw new TimeoutException($"Live settlement case '{_caseName}' exceeded its fixture bound. Journal: {ReadJournal()}", ex);
            }
        }

        public string ReadJournal()
        {
            using var stream = new FileStream(Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public void AlignJournalWriter() => _journalStream.Position = _journalStream.Length;

        public async Task<bool> ReconstructAsync(bool quotaWait = false)
        {
            var room = Path.Combine(Room, quotaWait ? "restart-quota" : "restart");
            Directory.CreateDirectory(room);
            var path = Path.Combine(room, BatonPaths.FlowLogFileName);
            File.WriteAllText(path, quotaWait ? ReadJournal() : _coreExitJournal!);
            await using var writer = new FlowEventLogWriter(path);
            var reader = new FlowEventLogReader(path);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            stop.CancelAfter(TimeSpan.FromSeconds(45));
            var reconstruction = MutationInterface.StartWorkflowAsync(Request.WorkflowId, room, Snapshot, Bindings, Artifacts,
                reader, writer, new NoLaunchDispatcher(), cancellationToken: stop.Token,
                onVendorQuotaPark: reset => { RestartQuotaRetryNotBefore = reset; stop.Cancel(); });
            FlowState state;
            try { state = await reconstruction.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken); }
            catch (TimeoutException ex)
            {
                stop.Cancel();
                throw new TimeoutException($"Restart settlement case '{_caseName}' exceeded its fixture bound.", ex);
            }
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            if (RestartQuotaRetryNotBefore is not null)
            {
                Assert.Equal(WorkflowStatus.Running, state.Status);
                Assert.True(Assert.Single(state.Steps).RetryNotBefore > DateTimeOffset.UtcNow);
                // The new pump renews the existing engine stamp without moving its deadline.
                var scheduled = events.OfType<FlowEvent.StepRetryScheduled>().ToArray();
                Assert.Equal(quotaWait ? 2 : 1, scheduled.Length);
                Assert.All(scheduled, retry => Assert.Equal(Assert.Single(state.Steps).RetryNotBefore, retry.RetryNotBefore));
                var failure = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
                Assert.Equal(FailureClassification.ExhaustedUntil, failure.FailureClassification);
                Assert.True(failure.RetryNotBefore > DateTimeOffset.UtcNow);
                Assert.Empty(events.OfType<FlowEvent.CancellationRequested>());
            }
            else if (LiveQuotaRetryNotBefore is not null && !quotaWait)
            {
                var failure = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
                Assert.Null(failure.FailureClassification);
                Assert.Null(failure.RetryNotBefore);
                Assert.Empty(events.OfType<FlowEvent.StepRetryScheduled>());
            }
            return events.OfType<FlowEvent.ExecutionSucceeded>().Any() || events.OfType<FlowEvent.ExecutionSucceededWithLateFailure>().Any();
        }

        public async Task<AgyExecutionIdentity> WaitEndpointAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                var endpoint = AgyStreamingHost.ReadEndpoint(Room, Request.ExecutionId.Value);
                if (endpoint is not null) return endpoint;
                if (Settlement is { IsCompleted: true })
                {
                    await Settlement;
                    throw new InvalidOperationException("Fixture settlement finished before endpoint publication.");
                }
                if (Dispatch.IsCompleted) throw new InvalidOperationException("Fixture exited before endpoint: " + (await Dispatch).StderrTail);
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: bounded fixture rendezvous
            }
            throw new TimeoutException("Fixture endpoint did not publish.");
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try
            {
                if (Dispatch is not null && !(Settlement is { IsCompleted: true } && !Dispatch.IsCompleted))
                    await Dispatch.WaitAsync(TimeSpan.FromSeconds(60));
            }
            finally
            {
                if (Settlement is not null)
                {
                    try { await Settlement.WaitAsync(TimeSpan.FromSeconds(60)); }
                    catch (FlowEventLogReadException) { /* expected raw-evidence refusal */ }
                }
                if (Writer is not null) await Writer.DisposeAsync();
                _stop.Dispose();
                DirectoryCleanup.DeleteRecursively(Room);
            }
        }
    }
}
