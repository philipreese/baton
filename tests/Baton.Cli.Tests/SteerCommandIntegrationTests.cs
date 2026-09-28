using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Baton.Cli;
using Baton.Domain;
using Baton.Status;
using Baton.Steering;
using Baton.Store;
using Baton.Vendors;

namespace Baton.Cli.Tests;

public sealed class SteerCommandIntegrationTests
{
    [Fact]
    public async Task Actual_broker_protocol_routes_cli_correction_and_semantic_response()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (room, executionOutput, textFile) = await SetUpRoomAsync("codex");
        try
        {
            var grant = new PermissionGrant(ReadFiles: true);
            var configuration = new CodexBrokerConfiguration(room, "gpt-5.6-luna", "low",
                null, false, grant, [], false);
            var policy = new CodexDynamicToolPolicy(grant, room, executionOutput, [], []);
            using var appServerOutput = new ChannelTextReader();
            using var appServerInput = new ChannelTextWriter();
            using var batonOutput = new StringWriter();
            using var errors = new StringWriter();
            appServerOutput.Enqueue("{\"id\":1,\"result\":{\"userAgent\":\"fixture\"}}");
            appServerOutput.Enqueue("{\"id\":2,\"result\":{\"thread\":{\"id\":\"thread-1\"}}}");
            appServerOutput.Enqueue("{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\"}}}");

            var broker = CodexAppServerBroker.RunProtocolAsync(configuration, "Original brief.", policy,
                executionOutput, appServerInput, appServerOutput, batonOutput, errors,
                TestContext.Current.CancellationToken, enableSteering: true);
            await WaitForAsync(() => CodexSteeringEndpoint.TryRead(room, "execution-1") is not null);

            using var result = new StringWriter();
            var sending = SteerCommand.ExecuteAsync(
                new SteerOptions(room, "execution-1", "message-broker", textFile, false),
                result, TestContext.Current.CancellationToken);
            JsonNode nativeRequest;
            do
            {
                nativeRequest = JsonNode.Parse(await appServerInput.ReadLineAsync(TestContext.Current.CancellationToken))!;
            } while (nativeRequest["method"]?.ToString() != "turn/steer");
            Assert.Equal("turn-1", nativeRequest["params"]!["expectedTurnId"]!.ToString());
            appServerOutput.Enqueue(new JsonObject
            {
                ["id"] = nativeRequest["id"]!.GetValue<int>(),
                ["result"] = new JsonObject { ["turnId"] = "turn-1" },
            }.ToJsonString());
            Assert.Equal(0, await sending);
            Assert.Contains("transportAcknowledged", result.ToString());

            appServerOutput.Enqueue("{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"status\":\"completed\",\"items\":[]}}}");
            Assert.Equal(0, await broker);
            Assert.Null(CodexSteeringEndpoint.TryRead(room, "execution-1"));
        }
        finally { Directory.Delete(room, recursive: true); }
    }

    [Fact]
    public async Task Exact_running_codex_turn_receives_one_native_request_and_retains_semantic_ack()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (room, output, textFile) = await SetUpRoomAsync("codex");
        try
        {
            using var nativeInput = new ConcurrentTextWriter();
            await using var ingress = CodexSteeringIngress.Start(output, "thread-1", "turn-1", nativeInput)!;
            var endpoint = CodexSteeringEndpoint.TryRead(room, "execution-1")!;
            using (var refused = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut,
                       PipeOptions.Asynchronous, TokenImpersonationLevel.None))
            {
                await refused.ConnectAsync(TestContext.Current.CancellationToken);
                using var refusedReader = new StreamReader(refused, Encoding.UTF8, leaveOpen: true);
                await using var refusedWriter = new StreamWriter(refused, new UTF8Encoding(false), leaveOpen: true)
                { AutoFlush = true };
                await refusedWriter.WriteLineAsync("{}");
                var refusal = await refusedReader.ReadLineAsync(TestContext.Current.CancellationToken);
                Assert.Equal((int)SteeringReceiptState.Rejected,
                    JsonNode.Parse(refusal!)!["State"]!.GetValue<int>());
            }
            using var result = new StringWriter();
            var options = new SteerOptions(room, "execution-1", "message-1", textFile, Receipt: false);
            var send = SteerCommand.ExecuteAsync(options, result, TestContext.Current.CancellationToken);
            await WaitForAsync(() => nativeInput.Snapshot().Contains("turn/steer", StringComparison.Ordinal));
            var nativeRequest = nativeInput.Snapshot().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonNode.Parse(line)).Single(node => node?["method"]?.ToString() == "turn/steer")!;
            Assert.Equal("thread-1", nativeRequest["params"]!["threadId"]!.ToString());
            Assert.Equal("turn-1", nativeRequest["params"]!["expectedTurnId"]!.ToString());
            Assert.Equal("Correct this exact turn.", nativeRequest["params"]!["input"]![0]!["text"]!.ToString());

            // An app-server tool request with the same numeric id is a different JSON-RPC direction.
            var id = nativeRequest["id"]!.GetValue<int>();
            Assert.False(await ingress.TryHandleNativeResponseAsync(new JsonObject
            {
                ["id"] = id,
                ["method"] = "item/tool/call",
                ["params"] = new JsonObject(),
            }));
            Assert.True(await ingress.TryHandleNativeResponseAsync(new JsonObject
            {
                ["id"] = id,
                ["result"] = new JsonObject { ["turnId"] = "turn-1" },
            }));
            Assert.Equal(0, await send);
            Assert.Contains("transportAcknowledged", result.ToString(), StringComparison.OrdinalIgnoreCase);

            // A second valid client must use a newly created listener, not the disposed first one.
            using var secondResult = new StringWriter();
            var secondSend = SteerCommand.ExecuteAsync(options with { MessageId = "message-2" },
                secondResult, TestContext.Current.CancellationToken);
            await WaitForAsync(() => nativeInput.Snapshot().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Count(line => line.Contains("turn/steer", StringComparison.Ordinal)) == 2);
            var secondNative = nativeInput.Snapshot().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonNode.Parse(line)).Last(node => node?["method"]?.ToString() == "turn/steer")!;
            Assert.True(await ingress.TryHandleNativeResponseAsync(new JsonObject
            {
                ["id"] = secondNative["id"]!.GetValue<int>(),
                ["result"] = new JsonObject { ["turnId"] = "turn-1" },
            }));
            Assert.Equal(0, await secondSend);

            await ingress.MarkTerminalAsync();
            using var queried = new StringWriter();
            Assert.Equal(0, await SteerCommand.ExecuteAsync(
                options with { File = null, Receipt = true }, queried, TestContext.Current.CancellationToken));
            Assert.Contains("transportAcknowledged", queried.ToString(), StringComparison.OrdinalIgnoreCase);
            using var duplicate = new StringWriter();
            Assert.Equal(0, await SteerCommand.ExecuteAsync(options, duplicate, TestContext.Current.CancellationToken));
            Assert.Equal(2, nativeInput.Snapshot().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Count(line => line.Contains("turn/steer", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(room, recursive: true);
        }
    }

    [Fact]
    public async Task Unsupported_adapter_and_stale_turn_never_send_native_bytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (unsupportedRoom, unsupportedOutput, textFile) = await SetUpRoomAsync("claude");
        try
        {
            using var nativeInput = new ConcurrentTextWriter();
            await using var ingress = CodexSteeringIngress.Start(unsupportedOutput, "thread-1", "turn-1", nativeInput)!;
            using var result = new StringWriter();
            Assert.Equal(1, await SteerCommand.ExecuteAsync(
                new SteerOptions(unsupportedRoom, "execution-1", "message-1", textFile, false),
                result, TestContext.Current.CancellationToken));
            Assert.Contains("unsupported", result.ToString());
            Assert.Empty(nativeInput.Snapshot());
        }
        finally { Directory.Delete(unsupportedRoom, recursive: true); }

        var (room, output, file) = await SetUpRoomAsync("codex");
        try
        {
            using var nativeInput = new ConcurrentTextWriter();
            await using var ingress = CodexSteeringIngress.Start(output, "thread-1", "turn-1", nativeInput)!;
            await ingress.MarkTerminalAsync();
            using var result = new StringWriter();
            Assert.Equal(1, await SteerCommand.ExecuteAsync(
                new SteerOptions(room, "execution-1", "message-1", file, false),
                result, TestContext.Current.CancellationToken));
            Assert.Contains("rejected", result.ToString());
            Assert.Empty(nativeInput.Snapshot());
        }
        finally { Directory.Delete(room, recursive: true); }
    }

    [Fact]
    public async Task Native_write_failure_after_fsynced_send_start_is_not_a_rejection_or_retry()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (room, output, textFile) = await SetUpRoomAsync("codex");
        try
        {
            using var nativeInput = new ThrowingTextWriter();
            await using var ingress = CodexSteeringIngress.Start(output, "thread-1", "turn-1", nativeInput)!;
            var options = new SteerOptions(room, "execution-1", "failed-write", textFile, false);
            using var result = new StringWriter();
            Assert.Equal(1, await SteerCommand.ExecuteAsync(options, result, TestContext.Current.CancellationToken));
            Assert.Contains("inFlight", result.ToString());
            Assert.Equal(1, nativeInput.Attempts);
            await ingress.MarkTerminalAsync();
            using var after = new StringWriter();
            Assert.Equal(1, await SteerCommand.ExecuteAsync(
                options with { Receipt = true, File = null }, after, TestContext.Current.CancellationToken));
            Assert.Contains("outcomeUnknown", after.ToString());
            using var duplicate = new StringWriter();
            Assert.Equal(1, await SteerCommand.ExecuteAsync(options, duplicate, TestContext.Current.CancellationToken));
            Assert.Equal(1, nativeInput.Attempts);
        }
        finally { Directory.Delete(room, recursive: true); }
    }

    [Fact]
    public async Task Request_only_after_a_pre_send_failure_can_retry_same_id_and_live_tuple()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (room, output, textFile) = await SetUpRoomAsync("codex");
        try
        {
            using var nativeInput = new ConcurrentTextWriter();
            await using var ingress = CodexSteeringIngress.Start(output, "thread-1", "turn-1", nativeInput)!;
            var endpoint = CodexSteeringEndpoint.TryRead(room, "execution-1")!;
            var text = await File.ReadAllTextAsync(textFile, TestContext.Current.CancellationToken);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            var principal = WindowsIdentity.GetCurrent().User!.Value;
            var store = new SteeringMessageStore(room);
            store.Reserve(new RoomEvent.SteeringRequested("pre-send", "execution-1", digest,
                endpoint.BrokerIncarnation, endpoint.ThreadId, endpoint.TurnId, principal,
                DateTimeOffset.UtcNow), targetLive: true);
            // This request-only fact is what a disconnected CLI leaves before TryStartSend.
            using var result = new StringWriter();
            var send = SteerCommand.ExecuteAsync(
                new SteerOptions(room, "execution-1", "pre-send", textFile, false),
                result, TestContext.Current.CancellationToken);
            await WaitForAsync(() => nativeInput.Snapshot().Contains("turn/steer", StringComparison.Ordinal));
            var native = JsonNode.Parse(nativeInput.Snapshot().Split('\n',
                StringSplitOptions.RemoveEmptyEntries).Single())!;
            Assert.True(await ingress.TryHandleNativeResponseAsync(new JsonObject
            {
                ["id"] = native["id"]!.GetValue<int>(),
                ["result"] = new JsonObject { ["turnId"] = "turn-1" },
            }));
            Assert.Equal(0, await send);
            Assert.Contains("transportAcknowledged", result.ToString());
        }
        finally { Directory.Delete(room, recursive: true); }
    }

    private static async Task<(string Room, string Output, string TextFile)> SetUpRoomAsync(string adapter)
    {
        var room = Path.Combine(Path.GetTempPath(), $"baton-steer-{Guid.NewGuid():N}");
        var output = Path.Combine(room, "artifacts", "execution_execution-1");
        Directory.CreateDirectory(output);
        var textFile = Path.Combine(room, "correction.txt");
        await File.WriteAllTextAsync(textFile, "Correct this exact turn.", TestContext.Current.CancellationToken);
        using var process = Process.GetCurrentProcess();
        var executionId = new ExecutionId("execution-1");
        await using var writer = new FlowEventLogWriter(Path.Combine(room, BatonPaths.FlowLogFileName));
        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
            executionId, new WorkflowId("workflow-1"), new StepId("step-1"), "implement",
            [], [], null, [], new Dictionary<StepId, ExecutionId>(), Adapter: adapter)),
            TestContext.Current.CancellationToken);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, (uint)process.Id,
            process.StartTime.ToUniversalTime()), TestContext.Current.CancellationToken);
        return (room, output, textFile);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(condition(), "Expected native steering request was not written.");
    }

    private sealed class ChannelTextReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        public void Enqueue(string line) => _lines.Writer.TryWrite(line);
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await _lines.Reader.ReadAsync(cancellationToken);
    }

    private sealed class ChannelTextWriter : TextWriter
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken)
        {
            _lines.Writer.TryWrite(value.ToString());
            return Task.CompletedTask;
        }
        public async Task<string> ReadLineAsync(CancellationToken cancellationToken) =>
            await _lines.Reader.ReadAsync(cancellationToken);
    }

    private sealed class ConcurrentTextWriter : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        private readonly object _gate = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken)
        {
            lock (_gate) _buffer.AppendLine(value.ToString());
            return Task.CompletedTask;
        }
        public string Snapshot()
        {
            lock (_gate) return _buffer.ToString();
        }
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public int Attempts { get; private set; }
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken)
        {
            Attempts++;
            throw new IOException("Simulated native writer failure after durable send-start.");
        }
    }
}
