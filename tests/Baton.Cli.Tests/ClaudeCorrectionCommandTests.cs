using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Baton.Domain;
using Baton.Status;
using Baton.Steering;
using Baton.Store;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests;

public sealed class ClaudeCorrectionCommandTests : IDisposable
{
    private const string Execution = "execution-claude";
    private const string Message = "correction-1";
    private const string Text = "Correct only this exact execution.";
    private const string Socket = @"\\.\pipe\LOCAL\cc-msg-fixture";
    private readonly string _room = Directory.CreateTempSubdirectory("baton-claude-command-").FullName;
    private ExecutionCorrectionStore Store => new(_room);
    private string ContractPath => Path.Combine(_room, "fixture-contract.json");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Routed_receipt_reads_retained_evidence_without_live_journal(bool acknowledged)
    {
        var request = Request();
        Store.TryClaim(request);
        if (acknowledged)
        {
            Store.TryAdmitTool(request);
            Store.RecordAnswer(request, true, "native-ack", null);
        }
        using var output = new StringWriter();
        var result = await SteerCommand.ExecuteAsync(new(_room, Execution, Message, null, true), output,
            TestContext.Current.CancellationToken);
        Assert.Equal(acknowledged ? 0 : 1, result);
        Assert.Equal(acknowledged ? "transportAcknowledged" : "outcomeUnknown", State(output));
        Assert.False(File.Exists(Path.Combine(_room, BatonPaths.FlowLogFileName)));
        AssertNoSender();
    }

    [Fact]
    public async Task Wrong_receipt_message_is_not_found_without_requiring_live_journal()
    {
        Store.TryClaim(Request());
        using var output = new StringWriter();
        Assert.Equal(1, await SteerCommand.ExecuteAsync(new(_room, Execution, "wrong", null, true), output,
            TestContext.Current.CancellationToken));
        Assert.Equal("notFound", State(output));
        AssertNoSender();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Conflicting_message_or_text_fails_before_launch(bool wrongMessage)
    {
        if (!OperatingSystem.IsWindows()) return;
        Store.TryClaim(Request());
        var textPath = Path.Combine(_room, "correction.txt");
        await File.WriteAllTextAsync(textPath, wrongMessage ? Text : "changed", TestContext.Current.CancellationToken);
        using var output = new StringWriter();
        await Assert.ThrowsAsync<InvalidOperationException>(() => SteerCommand.ExecuteAsync(
            new(_room, Execution, wrongMessage ? "other" : Message, textPath, false), output,
            TestContext.Current.CancellationToken));
        AssertNoSender();
    }

    [Fact]
    public async Task Guard_admits_exact_tool_once_and_only_once()
    {
        if (!OperatingSystem.IsWindows()) return;
        var request = await SetUpLiveAsync();
        await WriteContractAsync(request);
        Assert.Equal(0, await GuardAsync(request.Target, Text, "SendMessage", "allow"));
        Assert.Equal(2, await GuardAsync(request.Target, Text, "SendMessage", "deny"));
        Assert.False(Store.TryAdmitTool(request));
        Assert.Equal(SteeringReceiptState.OutcomeUnknown, Store.Query(Execution)!.State);
        AssertNoSender();
    }

    [Theory]
    [InlineData("target")]
    [InlineData("text")]
    [InlineData("tool")]
    public async Task Guard_rejects_wrong_tool_input_without_consuming_admission(string mismatch)
    {
        if (!OperatingSystem.IsWindows()) return;
        var request = await SetUpLiveAsync();
        await WriteContractAsync(request);
        Assert.Equal(2, await GuardAsync(mismatch == "target" ? "other" : request.Target,
            mismatch == "text" ? "other" : Text, mismatch == "tool" ? "Bash" : "SendMessage", "deny"));
        Assert.True(Store.TryAdmitTool(request));
        AssertNoSender();
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("exited")]
    [InlineData("missing-endpoint")]
    [InlineData("unobserved-endpoint")]
    public async Task Guard_rejects_stale_or_unverified_receiver_without_admission(string defect)
    {
        if (!OperatingSystem.IsWindows()) return;
        var request = Request();
        if (defect == "pid") request = request with { ProcessId = int.MaxValue };
        if (defect == "start") request = request with { ProcessStartUtc = request.ProcessStartUtc.AddDays(-1) };
        await SetUpLiveAsync(request, endpoint: defect != "missing-endpoint",
            observe: defect != "unobserved-endpoint", exited: defect == "exited");
        await WriteContractAsync(request);
        Assert.Equal(2, await GuardAsync(request.Target, Text, "SendMessage", "deny"));
        Assert.True(Store.TryAdmitTool(request));
        AssertNoSender();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("dead-pid")]
    [InlineData("stale-start")]
    [InlineData("exited")]
    public async Task Execute_rejects_nonexistent_or_dead_receiver_before_claim_or_sender(string defect)
    {
        if (!OperatingSystem.IsWindows()) return;
        var request = Request();
        if (defect == "dead-pid") request = request with { ProcessId = int.MaxValue };
        if (defect == "stale-start") request = request with { ProcessStartUtc = request.ProcessStartUtc.AddDays(-1) };
        if (defect != "missing") await SetUpLiveAsync(request, claim: false, exited: defect == "exited");
        var textPath = Path.Combine(_room, "correction.txt");
        await File.WriteAllTextAsync(textPath, Text, TestContext.Current.CancellationToken);
        using var output = new StringWriter();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ClaudeCorrectionCommand.ExecuteAsync(
            new(_room, Execution, Message, textPath, false), output, TestContext.Current.CancellationToken));
        Assert.Null(Store.Query(Execution));
        AssertNoSender();
    }

    private async Task<CorrectionRequest> SetUpLiveAsync(CorrectionRequest? request = null,
        bool endpoint = true, bool observe = true, bool exited = false, bool claim = true)
    {
        request ??= Request();
        var output = Path.Combine(_room, "artifacts", "execution_" + Execution);
        Directory.CreateDirectory(output);
        var id = new ExecutionId(Execution);
        await using (var writer = new FlowEventLogWriter(Path.Combine(_room, BatonPaths.FlowLogFileName)))
        {
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(id,
                new WorkflowId("workflow"), new StepId("step"), "implement", [], [], null, [],
                new Dictionary<StepId, ExecutionId>(), Adapter: "claude")), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(id, (uint)request.ProcessId, request.ProcessStartUtc),
                TestContext.Current.CancellationToken);
            if (exited) await writer.AppendAsync(new CoreEvent.ExecutionExited(id, 0, CoreExitReason.Natural),
                TestContext.Current.CancellationToken);
        }
        if (endpoint) ClaudeCorrectionEndpoint.PublishAddress(output, request.SessionId, Socket, request.OsPrincipal);
        if (observe) ClaudeCorrectionEndpoint.CreateObserver(output)(JsonSerializer.Serialize(new
        {
            type = "system",
            subtype = "init",
            session_id = request.SessionId,
        }));
        if (claim) Store.TryClaim(request);
        return request;
    }

    private Task WriteContractAsync(CorrectionRequest request) => File.WriteAllTextAsync(ContractPath,
        JsonSerializer.Serialize(new ClaudeCorrectionContract(_room, request, Text)), TestContext.Current.CancellationToken);

    private async Task<int> GuardAsync(string target, string text, string tool, string expectedDecision)
    {
        using var input = new StringReader(JsonSerializer.Serialize(new
        {
            tool_name = tool,
            tool_input = new { to = target, message = text },
        }));
        using var output = new StringWriter();
        var result = await ClaudeCorrectionCommand.GuardAsync(ContractPath, input, output);
        using var parsed = JsonDocument.Parse(output.ToString());
        Assert.Equal(expectedDecision, parsed.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());
        return result;
    }

    private static CorrectionRequest Request()
    {
        using var process = Process.GetCurrentProcess();
        var principal = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User!.Value : "fixture-user";
        return new(Message, Execution, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Text))),
            "claude", "fixture-session", "uds:" + Socket, process.Id, process.StartTime.ToUniversalTime(),
            principal, DateTimeOffset.UtcNow);
    }

    private static string? State(StringWriter output)
    {
        using var parsed = JsonDocument.Parse(output.ToString());
        return parsed.RootElement.GetProperty("state").GetString();
    }

    private void AssertNoSender() => Assert.Empty(Directory.Exists(Path.Combine(_room, "steering"))
        ? Directory.GetDirectories(Path.Combine(_room, "steering"), "sender-*") : []);

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_room);
}
