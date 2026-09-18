using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;
using Baton.Memory;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baton.Cli.Tests;

[Collection(SerializedEnvironmentCollection.Name)]
public sealed class MemoryAddLaneGrantGateTests : IDisposable
{
    private const string Repository = "github.com/owner/repo";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baton-2100-{Guid.NewGuid():N}");
    private readonly IDisposable _scope;

    public MemoryAddLaneGrantGateTests() =>
        _scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = Path.Combine(_root, "baton-root") });

    public void Dispose()
    {
        _scope.Dispose();
        DirectoryCleanup.DeleteRecursively(_root);
    }

    [Fact]
    public async Task Exact_queue_and_room_grant_authorizes_only_its_repository_with_complete_provenance()
    {
        var grant = new MemoryAddDispatchGrant("a" + new string('1', 31), Repository);
        var room = Path.Combine(_root, "rooms", "queue-2100");
        Directory.CreateDirectory(Path.Combine(room, "artifacts"));
        var output = Path.Combine(room, "artifacts", "execution_2100a");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(
            BatonPaths.RoomBindingsFile(room),
            $$"""
            { "implement": { "Adapter": "codex", "Timeout": "00:25:00", "Contract": { "WorkerName": "implement", "RequiredInputs": [], "ProducedOutputs": [{ "Name": "report.md" }], "OptionalMetadata": [] }, "PromptTemplate": "do the thing", "Model": "gpt-fixture", "MemoryAddGrant": { "DispatchId": "{{grant.DispatchId}}", "Repository": "{{Repository}}" } } }
            """, TestContext.Current.CancellationToken);
        await QueueStore.MutateAsync(
            BatonPaths.QueueFile,
            _ => new QueueSnapshot(
            [new QueueItem
            {
                Tag = "2100-memory",
                Role = "implement",
                Adapter = "codex",
                Model = "gpt-fixture",
                Workspace = _root,
                SpecFile = Path.Combine(_root, "brief.md"),
                Issue = 2100,
                Repository = Repository,
                RoomDirectory = room,
                Requirements = [TaskRequirements.MemoryAdd],
                MemoryAddGrant = grant,
            }]), TestContext.Current.CancellationToken);

        var authorization = await MemoryAddLaneGrantGate.TryAuthorizeAsync(
            Path.Combine(room, "artifacts"), TestContext.Current.CancellationToken, output);

        Assert.NotNull(authorization);
        Assert.Equal(Repository, authorization.Repository);
        Assert.Equal(grant.DispatchId, authorization.DispatchId);
        Assert.Equal(2100, authorization.Issue);
        Assert.Contains("role=implement", authorization.AssertedBy, StringComparison.Ordinal);
        Assert.Contains("adapter=codex", authorization.AssertedBy, StringComparison.Ordinal);
        Assert.Contains("model=gpt-fixture", authorization.AssertedBy, StringComparison.Ordinal);

        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
        {
            Items = [snapshot.Items.Single() with { Requirements = [] }],
        }, TestContext.Current.CancellationToken);
        Assert.Null(await MemoryAddLaneGrantGate.TryAuthorizeAsync(
            Path.Combine(room, "artifacts"), TestContext.Current.CancellationToken, output));

        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
        {
            Items = [snapshot.Items.Single() with
            {
                Requirements = [TaskRequirements.MemoryAdd],
                Adapter = "agy",
            }],
        }, TestContext.Current.CancellationToken);
        Assert.Null(await MemoryAddLaneGrantGate.TryAuthorizeAsync(
            Path.Combine(room, "artifacts"), TestContext.Current.CancellationToken, output));

        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
        {
            Items = [snapshot.Items.Single() with { Adapter = "claude" }],
        }, TestContext.Current.CancellationToken);
        Assert.Null(await MemoryAddLaneGrantGate.TryAuthorizeAsync(
            Path.Combine(room, "artifacts"), TestContext.Current.CancellationToken, output));
    }

    [Fact]
    public async Task Forged_or_stale_binding_grant_fails_closed()
    {
        var room = Path.Combine(_root, "rooms", "queue-2100");
        Directory.CreateDirectory(Path.Combine(room, "artifacts"));
        await File.WriteAllTextAsync(
            BatonPaths.RoomBindingsFile(room),
            """
            { "implement": { "Adapter": "claude", "Timeout": "00:25:00", "Contract": { "WorkerName": "implement", "RequiredInputs": [], "ProducedOutputs": [{ "Name": "report.md" }], "OptionalMetadata": [] }, "PromptTemplate": "do the thing", "MemoryAddGrant": { "DispatchId": "forged", "Repository": "github.com/owner/repo" } } }
            """, TestContext.Current.CancellationToken);

        Assert.Null(await MemoryAddLaneGrantGate.TryAuthorizeAsync(
            Path.Combine(room, "artifacts"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Brokered_memory_add_uses_its_running_execution_not_the_callers_artifacts_root()
    {
        var grant = new MemoryAddDispatchGrant("b" + new string('2', 31), Repository);
        var room = Path.Combine(_root, "rooms", "queue-2100");
        var artifacts = Path.Combine(room, "artifacts");
        var output = Path.Combine(artifacts, "execution_brokered");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(BatonPaths.RoomBindingsFile(room),
            $$"""
            { "implement": { "Adapter": "codex", "Timeout": "00:25:00", "Contract": { "WorkerName": "implement", "RequiredInputs": [], "ProducedOutputs": [{ "Name": "changes.md" }], "OptionalMetadata": [] }, "PromptTemplate": "do the thing", "Model": "gpt-fixture", "MemoryAddGrant": { "DispatchId": "{{grant.DispatchId}}", "Repository": "{{Repository}}" } } }
            """, TestContext.Current.CancellationToken);
        await QueueStore.MutateAsync(BatonPaths.QueueFile, _ => new QueueSnapshot(
            [new QueueItem
            {
                Tag = "2100-memory", Role = "implement", Adapter = "codex", Model = "gpt-fixture",
                Workspace = _root, SpecFile = Path.Combine(_root, "brief.md"), Issue = 2100,
                Repository = Repository, RoomDirectory = room, Requirements = [TaskRequirements.MemoryAdd],
                State = QueueItemState.Launched,
                MemoryAddGrant = grant,
            }]), TestContext.Current.CancellationToken);

        var previousArtifacts = Environment.GetEnvironmentVariable(MemoryLaneAssertion.ArtifactsRootVariable);
        var previousOutput = Environment.GetEnvironmentVariable("BATON_OUTPUT_DIR");
        Environment.SetEnvironmentVariable(MemoryLaneAssertion.ArtifactsRootVariable, artifacts);
        Environment.SetEnvironmentVariable("BATON_OUTPUT_DIR", output);
        try
        {
            var permission = MemoryAddCommandPermission.Add(
                new PermissionGrant(RunShellCommands: true, DeniedShellCommandPatterns: ["baton memory*"]), grant);
            var configuration = new CodexBrokerConfiguration(
                _root, "gpt-fixture", null, null, false, permission, ["changes.md"], false,
                MemoryAddAuthority: new CodexMemoryAddHostAuthority(room, artifacts, output, grant));
            var transcript = string.Join('\n',
            [
                "{\"id\":1,\"result\":{\"userAgent\":\"fixture\"}}",
                "{\"id\":2,\"result\":{\"thread\":{\"id\":\"thread-1\"}}}",
                "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\",\"items\":[]}}}",
                JsonSerializer.Serialize(new
                {
                    id = 99,
                    method = "item/tool/call",
                    @params = new
                    {
                        tool = "baton_run_command",
                        arguments = new { command = $"baton memory add --text brokered-fact --kind durable-fact --repository {Repository}" },
                        callId = "call-1", threadId = "thread-1", turnId = "turn-1",
                    },
                }),
                JsonSerializer.Serialize(new
                {
                    id = 100,
                    method = "item/tool/call",
                    @params = new
                    {
                        tool = "baton_run_command",
                        arguments = new { command = $"baton memory add --text brokered-fact --kind durable-fact --repository {Repository}" },
                        callId = "call-2", threadId = "thread-1", turnId = "turn-1",
                    },
                }),
                JsonSerializer.Serialize(new
                {
                    id = 101,
                    method = "item/tool/call",
                    @params = new
                    {
                        tool = "baton_run_command",
                        arguments = new { command = $"baton memory add --text conflicting-fact --kind durable-fact --repository {Repository}" },
                        callId = "call-3", threadId = "thread-1", turnId = "turn-1",
                    },
                }),
                JsonSerializer.Serialize(new
                {
                    id = 102,
                    method = "item/tool/call",
                    @params = new
                    {
                        tool = "baton_run_command",
                        arguments = new { command = $"baton memory add --repository {Repository} --text malformed --kind durable-fact" },
                        callId = "call-4", threadId = "thread-1", turnId = "turn-1",
                    },
                }),
                "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"status\":\"completed\",\"items\":[]}}}",
            ]) + "\n";
            using var serverOutput = new StringReader(transcript);
            using var serverInput = new StringWriter();
            using var batonOutput = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await CodexAppServerBroker.RunProtocolAsync(
                configuration, "record the fact", output, [], null, CodexBrokerCommand.ExecuteMemoryAddAsync,
                serverInput, serverOutput, batonOutput, error, TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            var responses = serverInput.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonNode.Parse(line)!).Where(node => node["id"] is not null).ToDictionary(
                    node => node!["id"]!.GetValue<int>(), node => node!["result"]!);
            Assert.True(responses[99]["success"]!.GetValue<bool>());
            Assert.True(responses[100]["success"]!.GetValue<bool>());
            Assert.False(responses[101]["success"]!.GetValue<bool>());
            Assert.False(responses[102]["success"]!.GetValue<bool>());
            Assert.Contains("ADDED", responses[100].ToJsonString(), StringComparison.Ordinal);
            Assert.Contains("different normalized payload", responses[101].ToJsonString(), StringComparison.Ordinal);
            Assert.Contains("grant-refused", responses[102].ToJsonString(), StringComparison.Ordinal);

            var retry = await CodexBrokerCommand.ExecuteMemoryAddAsync(
                new MemoryAddCommandInvocation(
                    "brokered-fact", "durable-fact", Repository,
                    new CodexMemoryAddHostAuthority(room, artifacts, output, grant)),
                TestContext.Current.CancellationToken);
            Assert.True(retry.Success);
            Assert.Contains("retry returned the canonical entry", retry.Output, StringComparison.Ordinal);

            var conflictingRetry = await CodexBrokerCommand.ExecuteMemoryAddAsync(
                new MemoryAddCommandInvocation(
                    "conflicting-fact", "durable-fact", Repository,
                    new CodexMemoryAddHostAuthority(room, artifacts, output, grant)),
                TestContext.Current.CancellationToken);
            Assert.False(conflictingRetry.Success);
            Assert.Contains("different normalized payload", conflictingRetry.Output, StringComparison.Ordinal);

            var kindOnlyConflict = await CodexBrokerCommand.ExecuteMemoryAddAsync(
                new MemoryAddCommandInvocation(
                    "brokered-fact", "hypothesis", Repository,
                    new CodexMemoryAddHostAuthority(room, artifacts, output, grant)),
                TestContext.Current.CancellationToken);
            Assert.False(kindOnlyConflict.Success);
            Assert.Contains("different normalized payload", kindOnlyConflict.Output, StringComparison.Ordinal);

            var concurrentGrant = new MemoryAddDispatchGrant("c" + new string('3', 31), Repository);
            await File.WriteAllTextAsync(BatonPaths.RoomBindingsFile(room),
                $$"""
                { "implement": { "Adapter": "codex", "Timeout": "00:25:00", "Contract": { "WorkerName": "implement", "RequiredInputs": [], "ProducedOutputs": [{ "Name": "changes.md" }], "OptionalMetadata": [] }, "PromptTemplate": "do the thing", "Model": "gpt-fixture", "MemoryAddGrant": { "DispatchId": "{{concurrentGrant.DispatchId}}", "Repository": "{{Repository}}" } } }
                """, TestContext.Current.CancellationToken);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [snapshot.Items.Single() with { MemoryAddGrant = concurrentGrant }],
            }, TestContext.Current.CancellationToken);
            var concurrentAuthority = new CodexMemoryAddHostAuthority(room, artifacts, output, concurrentGrant);
            var concurrentWrites = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
                CodexBrokerCommand.ExecuteMemoryAddAsync(
                    new MemoryAddCommandInvocation("concurrent fact", "durable-fact", Repository, concurrentAuthority),
                    TestContext.Current.CancellationToken)));
            Assert.All(concurrentWrites, execution => Assert.True(execution.Success));
            Assert.Single(await MemoryStore.ReadAllAsync(BatonPaths.MemoryEntriesFile(FleetMemory.SlugFor(Repository)),
                TestContext.Current.CancellationToken), entry => entry.MemoryAddDispatchId == concurrentGrant.DispatchId);
            Assert.Equal(string.Empty, error.ToString());

            var direct = new StringWriter();
            var directCode = await MemoryAddCommand.ExecuteAsync(
                MemoryAddOptionsParser.Parse([
                    "--text", "forged-root", "--kind", "durable-fact", "--repository", Repository]),
                direct, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, directCode);
            Assert.Contains("REFUSED", direct.ToString(), StringComparison.Ordinal);

        }
        finally
        {
            Environment.SetEnvironmentVariable(MemoryLaneAssertion.ArtifactsRootVariable, previousArtifacts);
            Environment.SetEnvironmentVariable("BATON_OUTPUT_DIR", previousOutput);
        }
    }

    [Fact]
    public async Task An_invalid_host_authority_is_not_an_operator_bypass()
    {
        var output = new StringWriter();
        var exitCode = await MemoryAddCommand.ExecuteAsync(
            MemoryAddOptionsParser.Parse([
                "--text", "forged host authority", "--kind", "durable-fact", "--repository", Repository]),
            output,
            brokerAuthority: new CodexMemoryAddHostAuthority(
                Path.Combine(_root, "forged-room"),
                Path.Combine(_root, "forged-room", "artifacts"),
                Path.Combine(_root, "forged-room", "artifacts", "execution_forged"),
                new MemoryAddDispatchGrant("f" + new string('3', 31), Repository)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("REFUSED", output.ToString(), StringComparison.Ordinal);
    }
}
