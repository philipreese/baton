using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton dispatch &lt;role&gt; --continue &lt;room&gt;</c> end to end (#1381): a terminal room's
/// worker is rehired for a follow-on brief instead of starting cold. Mirrors
/// <see cref="RedispatchCommandEndToEndTests"/>'s catalog-pinning/fake-adapter setup, keyed under
/// <c>"claude"</c>/<c>"codex"</c> rather than <c>"fake"</c> so the supported-adapter check can pass
/// on the happy-path arms without a live vendor CLI.
/// </summary>
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class DispatchContinueEndToEndTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter>
        {
            ["claude"] = new ContractOutputWorkerAdapter(satisfyOutputs: true),
            ["codex"] = new ContractOutputWorkerAdapter(satisfyOutputs: true),
            ["agy"] = new ContractOutputWorkerAdapter(satisfyOutputs: true),
        };

    private readonly IsolatedBatonHome _batonHome = new();
    private readonly IDisposable _catalogScope;

    public DispatchContinueEndToEndTests()
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

    [Fact]
    public async Task Continuing_a_room_with_a_recorded_session_id_dispatches_with_resume_set()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-continue-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentWithSessionAsync(testRoot, "Weigh the options for X.", "sess-abc-123");

            var followUpSpecPath = await WriteSpecAsync(testRoot, "Now weigh Y instead.");
            var childRoom = Path.Combine(testRoot, "child");
            var options = new DispatchOptions(
                "advise", followUpSpecPath, childRoom, Adapter: "claude", Model: "sonnet", ContinueFromRoomDirectoryPath: parentRoom);

            var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal("sess-abc-123", childBindings["advise"].SessionId);
            Assert.True(childBindings["advise"].ResumeSession);

            // The follow-on brief actually reached the worker, not the veteran's stale prompt.
            Assert.Contains("Now weigh Y instead.", childBindings["advise"].PromptTemplate, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Continuing_a_codex_room_dispatches_with_the_same_session_and_resume_set()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-continue-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentWithSessionAsync(
                testRoot, "Check X.", "codex-thread-123", adapter: "codex");
            var parentBindingsPath = Path.Combine(parentRoom, "bindings.json");
            var parentBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                parentBindingsPath, TestContext.Current.CancellationToken);
            var expectedIdentity = new GhPullRequestCreateIdentity(
                "aer-works/baton", "2190-verified-pr-ownership");
            await WorkerBindingConfigWriter.SaveToFileAsync(
                new Dictionary<string, WorkerBindingConfigEntry>
                {
                    ["advise"] = parentBindings["advise"] with
                    {
                        PullRequestCreateIdentity = expectedIdentity,
                    },
                },
                parentBindingsPath,
                TestContext.Current.CancellationToken);
            var followUpSpecPath = await WriteSpecAsync(testRoot, "Now check Y.");
            var childRoom = Path.Combine(testRoot, "child");
            var options = new DispatchOptions(
                "advise", followUpSpecPath, childRoom, Adapter: "codex", ContinueFromRoomDirectoryPath: parentRoom);

            var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal("codex-thread-123", childBindings["advise"].SessionId);
            Assert.True(childBindings["advise"].ResumeSession);
            Assert.Equal(expectedIdentity, childBindings["advise"].PullRequestCreateIdentity);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// Chaining: the child room's own bindings.json now carries the same session id (Claude's own
    /// <c>--resume</c> continues a session under its existing id rather than minting a new one), so a
    /// THIRD dispatch can <c>--continue</c> from the child — the mechanism is self-sustaining through
    /// bindings.json alone, with no separate ledger record (record-once).
    /// </summary>
    [Fact]
    public async Task A_continued_room_can_itself_be_continued_from()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-continue-e2e-{Guid.NewGuid():N}");
        try
        {
            var grandparentRoom = await DispatchTerminalParentWithSessionAsync(testRoot, "Weigh the options for X.", "sess-abc-123");

            var parentSpecPath = await WriteSpecAsync(testRoot, "Now weigh Y instead.");
            var parentRoom = Path.Combine(testRoot, "parent");
            var parentOptions = new DispatchOptions(
                "advise", parentSpecPath, parentRoom, Adapter: "claude", Model: "sonnet", ContinueFromRoomDirectoryPath: grandparentRoom);
            var parentResult = await DispatchCommand.ExecuteAsync(parentOptions, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);
            var parentView = WorkflowStatusProjector.Project(parentResult.State, parentResult.Snapshot, parentRoom);
            await TerminalSentinelWriter.WriteAsync(parentRoom, parentView, TestContext.Current.CancellationToken);

            var childSpecPath = await WriteSpecAsync(testRoot, "Now weigh Z instead.");
            var childRoom = Path.Combine(testRoot, "child");
            var childOptions = new DispatchOptions(
                "advise", childSpecPath, childRoom, Adapter: "claude", Model: "sonnet", ContinueFromRoomDirectoryPath: parentRoom);
            var childResult = await DispatchCommand.ExecuteAsync(childOptions, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);

            Assert.Equal(WorkflowStatus.Terminal, childResult.State.Status);
            var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal("sess-abc-123", childBindings["advise"].SessionId);
            Assert.True(childBindings["advise"].ResumeSession);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Provenance_names_the_veteran_room_execution_and_session()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-continue-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentWithSessionAsync(testRoot, "Weigh the options for X.", "sess-abc-123");
            var followUpSpecPath = await WriteSpecAsync(testRoot, "Now weigh Y instead.");
            var childRoom = Path.Combine(testRoot, "child");
            var options = new DispatchOptions(
                "advise", followUpSpecPath, childRoom, Adapter: "claude", Model: "sonnet", ContinueFromRoomDirectoryPath: parentRoom);

            await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);

            var markerPath = Path.Combine(childRoom, ".baton", BatonPaths.RoomMetadataFileName);
            Assert.True(File.Exists(markerPath));
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken));
            Assert.Equal(parentRoom, doc.RootElement.GetProperty("ParentRoomDirectoryPath").GetString());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("ParentExecutionId").GetString()));
            Assert.Equal("sess-abc-123", doc.RootElement.GetProperty("ContinuedSessionId").GetString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Continuing_a_room_with_no_recorded_session_id_is_refused_not_a_silent_cold_start()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-continue-e2e-{Guid.NewGuid():N}");
        try
        {
            // This fake adapter reports no session id; the field therefore remains absent.
            var parentRoom = await DispatchTerminalParentAsync(testRoot, "Weigh the options for X.");

            var followUpSpecPath = await WriteSpecAsync(testRoot, "Now weigh Y instead.");
            var childRoom = Path.Combine(testRoot, "child");
            var options = new DispatchOptions(
                "advise", followUpSpecPath, childRoom, Adapter: "claude", Model: "sonnet", ContinueFromRoomDirectoryPath: parentRoom);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit));

            Assert.Contains("no vendor session to resume", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(childRoom));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Continuing_across_an_adapter_swap_is_refused()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-continue-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentWithSessionAsync(testRoot, "Weigh the options for X.", "sess-abc-123");

            var followUpSpecPath = await WriteSpecAsync(testRoot, "Now weigh Y instead.");
            var childRoom = Path.Combine(testRoot, "child");
            // Swapping the continuation onto agy must refuse even though the veteran itself is on a
            // supported adapter.
            var options = new DispatchOptions(
                "advise", followUpSpecPath, childRoom, Adapter: "agy", ContinueFromRoomDirectoryPath: parentRoom);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit));

            Assert.Contains("same supported adapter", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(childRoom));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Continuing_a_non_terminal_room_is_refused()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-continue-e2e-{Guid.NewGuid():N}");
        try
        {
            // Dispatched but no terminal.json written -- the room looks mid-flight, exactly the
            // ambiguity RedispatchCommandEndToEndTests' own non-terminal-parent test exercises.
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var parentRoom = Path.Combine(testRoot, "parent");
            var parentResult = await DispatchCommand.ExecuteAsync(
                new DispatchOptions("advise", specPath, parentRoom, Adapter: "claude", Model: "sonnet"), Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);
            await SetSessionIdAsync(parentRoom, "advise", "sess-abc-123");

            var followUpSpecPath = await WriteSpecAsync(testRoot, "Now weigh Y instead.");
            var childRoom = Path.Combine(testRoot, "child");
            var options = new DispatchOptions(
                "advise", followUpSpecPath, childRoom, Adapter: "claude", Model: "sonnet", ContinueFromRoomDirectoryPath: parentRoom);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit));

            Assert.Contains("terminal", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(childRoom));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Continuing_a_room_that_dispatched_more_than_one_worker_is_refused()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-continue-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentWithSessionAsync(testRoot, "Weigh the options for X.", "sess-abc-123");

            // Widen the terminal parent's bindings.json to two entries, the same composed-template
            // shape RedispatchCommandEndToEndTests' own multi-worker refusal test builds by hand.
            var bindingsPath = Path.Combine(parentRoom, "bindings.json");
            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsPath, TestContext.Current.CancellationToken);
            var widened = new Dictionary<string, WorkerBindingConfigEntry>(bindings) { ["second"] = bindings["advise"] };
            await WorkerBindingConfigWriter.SaveToFileAsync(widened, bindingsPath, TestContext.Current.CancellationToken);

            var followUpSpecPath = await WriteSpecAsync(testRoot, "Now weigh Y instead.");
            var childRoom = Path.Combine(testRoot, "child");
            var options = new DispatchOptions(
                "advise", followUpSpecPath, childRoom, Adapter: "claude", Model: "sonnet", ContinueFromRoomDirectoryPath: parentRoom);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit));

            Assert.Contains("2 workers", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Continuing_a_missing_room_is_a_typed_argument_error()
    {
        var missingParent = Path.Combine(Path.GetTempPath(), $"dispatch-continue-missing-{Guid.NewGuid():N}");
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-continue-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var childRoom = Path.Combine(testRoot, "child");
            var options = new DispatchOptions(
                "advise", specPath, childRoom, Adapter: "claude", Model: "sonnet", ContinueFromRoomDirectoryPath: missingParent);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit));

            Assert.Contains("does not exist", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(childRoom));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Continuing_a_workflow_template_is_refused()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-continue-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentWithSessionAsync(testRoot, "Weigh the options for X.", "sess-abc-123");
            var childRoom = Path.Combine(testRoot, "child");
            var options = new DispatchOptions(
                "implement-review", SpecFilePath: null, childRoom, ContinueFromRoomDirectoryPath: parentRoom);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit));

            Assert.Contains("--continue", ex.Message, StringComparison.Ordinal);
            Assert.Contains("workflow template", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// Ceilings unchanged (#1802): a continued dispatch is still resolved through the ordinary
    /// role-to-binding path, so its permission grant/hook are the role's own -- the --continue override
    /// touches only SessionId/ResumeSession, never PermissionGrant/AllowsSubagents.
    /// </summary>
    [Fact]
    public async Task Continuing_a_room_still_carries_the_roles_grant_and_subagent_ceiling()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-continue-e2e-{Guid.NewGuid():N}");
        try
        {
            var parentRoom = await DispatchTerminalParentWithSessionAsync(testRoot, "Weigh the options for X.", "sess-abc-123");
            var parentBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(parentRoom, "bindings.json"), TestContext.Current.CancellationToken);

            var followUpSpecPath = await WriteSpecAsync(testRoot, "Now weigh Y instead.");
            var childRoom = Path.Combine(testRoot, "child");
            var options = new DispatchOptions(
                "advise", followUpSpecPath, childRoom, Adapter: "claude", Model: "sonnet", ContinueFromRoomDirectoryPath: parentRoom);

            await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);

            var childBindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(childRoom, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Equal(parentBindings["advise"].PermissionGrant, childBindings["advise"].PermissionGrant);
            Assert.Equal(parentBindings["advise"].AllowsSubagents, childBindings["advise"].AllowsSubagents);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<string> DispatchTerminalParentWithSessionAsync(
        string testRoot, string spec, string sessionId, string adapter = "claude")
    {
        var specPath = await WriteSpecAsync(testRoot, spec);
        var roomDirectory = Path.Combine(testRoot, "parent");
        var options = new DispatchOptions(
            "advise", specPath, roomDirectory, Adapter: adapter,
            Model: string.Equals(adapter, "claude", StringComparison.OrdinalIgnoreCase) ? "sonnet" : null);

        var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);
        var view = WorkflowStatusProjector.Project(result.State, result.Snapshot, roomDirectory);
        await TerminalSentinelWriter.WriteAsync(roomDirectory, view, TestContext.Current.CancellationToken);

        await SetSessionIdAsync(roomDirectory, "advise", sessionId);
        return roomDirectory;
    }

    private static async Task<string> DispatchTerminalParentAsync(string testRoot, string spec)
    {
        var specPath = await WriteSpecAsync(testRoot, spec);
        var roomDirectory = Path.Combine(testRoot, "parent");
        var options = new DispatchOptions("advise", specPath, roomDirectory, Adapter: "claude", Model: "sonnet");

        var result = await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);
        var view = WorkflowStatusProjector.Project(result.State, result.Snapshot, roomDirectory);
        await TerminalSentinelWriter.WriteAsync(roomDirectory, view, TestContext.Current.CancellationToken);

        return roomDirectory;
    }

    /// <summary>
    /// Stands in for a prior room with a recorded session id. These continuation tests use a simple
    /// fake that reports no stream id, so they place the same bindings field #1841 now fills for a
    /// real Claude dispatch.
    /// </summary>
    private static async Task SetSessionIdAsync(string roomDirectory, string workerName, string sessionId)
    {
        var bindingsPath = Path.Combine(roomDirectory, "bindings.json");
        var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsPath, TestContext.Current.CancellationToken);
        var updated = new Dictionary<string, WorkerBindingConfigEntry>(bindings)
        {
            [workerName] = bindings[workerName] with { SessionId = sessionId },
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(updated, bindingsPath, TestContext.Current.CancellationToken);
    }

    private static async Task<string> WriteSpecAsync(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"spec-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }
}
