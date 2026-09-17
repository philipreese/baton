using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;
using System.Text.Json;

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
            { "implement": { "Adapter": "claude", "Timeout": "00:25:00", "Contract": { "WorkerName": "implement", "RequiredInputs": [], "ProducedOutputs": [{ "Name": "report.md" }], "OptionalMetadata": [] }, "PromptTemplate": "do the thing", "Model": "gpt-fixture", "MemoryAddGrant": { "DispatchId": "{{grant.DispatchId}}", "Repository": "{{Repository}}" } } }
            """, TestContext.Current.CancellationToken);
        await QueueStore.MutateAsync(
            BatonPaths.QueueFile,
            _ => new QueueSnapshot(
            [new QueueItem
            {
                Tag = "2100-memory",
                Role = "implement",
                Adapter = "claude",
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
        Assert.Contains("adapter=claude", authorization.AssertedBy, StringComparison.Ordinal);
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
                MemoryAddGrant = grant,
            }]), TestContext.Current.CancellationToken);

        var previousArtifacts = Environment.GetEnvironmentVariable(MemoryLaneAssertion.ArtifactsRootVariable);
        Environment.SetEnvironmentVariable(MemoryLaneAssertion.ArtifactsRootVariable, artifacts);
        try
        {
            var permission = MemoryAddCommandPermission.Add(
                new PermissionGrant(RunShellCommands: true, DeniedShellCommandPatterns: ["baton memory*"]), grant);
            var policy = new CodexDynamicToolPolicy(
                permission, _root, output, [], ["changes.md"],
                memoryAddExecutor: async (invocation, token) =>
                {
                    var writer = new StringWriter();
                    var code = await MemoryAddCommand.ExecuteAsync(
                        MemoryAddOptionsParser.Parse([
                            "--text", invocation.Text, "--kind", invocation.Kind, "--repository", invocation.Repository]),
                        writer, cancellationToken: token, brokerOutputDirectory: output);
                    return new MemoryAddCommandExecution(code == 0, writer.ToString());
                });
            using var arguments = JsonDocument.Parse(
                $$"""{"command":"baton memory add --text brokered-fact --kind durable-fact --repository {{Repository}}"}""");

            var allowed = await policy.ExecuteAsync(
                "baton_run_command", arguments.RootElement, TestContext.Current.CancellationToken);

            Assert.True(allowed.Success);
            Assert.Contains("ADDED", allowed.Text, StringComparison.Ordinal);

            var direct = new StringWriter();
            var directCode = await MemoryAddCommand.ExecuteAsync(
                MemoryAddOptionsParser.Parse([
                    "--text", "forged-root", "--kind", "durable-fact", "--repository", Repository]),
                direct, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, directCode);
            Assert.Contains("REFUSED", direct.ToString(), StringComparison.Ordinal);

            using var malformed = JsonDocument.Parse(
                $$"""{"command":"baton memory add --repository {{Repository}} --text x --kind durable-fact"}""");
            var refused = await policy.ExecuteAsync(
                "baton_run_command", malformed.RootElement, TestContext.Current.CancellationToken);
            Assert.False(refused.Success);
            Assert.Contains("grant-refused", refused.Text, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MemoryLaneAssertion.ArtifactsRootVariable, previousArtifacts);
        }
    }
}
