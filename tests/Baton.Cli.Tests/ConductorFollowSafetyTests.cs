using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests;

public sealed class ConductorFollowSafetyTests
{
    [Theory]
    [InlineData("owner")]
    [InlineData("repo")]
    [InlineData("adapter")]
    [InlineData("capability")]
    [InlineData("action")]
    [InlineData("unsupported")]
    public async Task Follow_rejects_original_obligation_drift(string change)
    {
        using var fixture = await Fixture.CreateAsync(change);
        var result = await fixture.RunAsync();
        Assert.Equal("refused", result.GetProperty("status").GetString());
        Assert.Equal(fixture.Key, result.GetProperty("obligationKey").GetString());
        Assert.Equal(0, fixture.Calls);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("effort")]
    [InlineData("timeoutSeconds")]
    [InlineData("adapter")]
    [InlineData("schemaVersion")]
    public async Task Follow_requires_every_explicit_axis(string axis)
    {
        using var fixture = await Fixture.CreateAsync();
        var request = JsonNode.Parse(File.ReadAllText(fixture.RequestFile))!.AsObject();
        request.Remove(axis);
        File.WriteAllText(fixture.RequestFile, request.ToJsonString());
        Assert.NotNull(await Record.ExceptionAsync(() => fixture.RunAsync()));
        Assert.Equal(0, fixture.Calls);
    }

    [Fact]
    public async Task Follow_duplicate_request_properties_refuse()
    {
        using var fixture = await Fixture.CreateAsync();
        var text = File.ReadAllText(fixture.RequestFile);
        File.WriteAllText(fixture.RequestFile, text[..^1] + ",\"model\":\"gpt-6-sol\"}");
        Assert.NotNull(await Record.ExceptionAsync(() => fixture.RunAsync()));
        Assert.Equal(0, fixture.Calls);
    }

    [Fact]
    public async Task Follow_unknown_grant_fields_refuse()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = JsonNode.Parse(File.ReadAllText(fixture.RequestFile))!;
        request["permissionGrant"]!["memoryWrites"] = true;
        File.WriteAllText(fixture.RequestFile, request.ToJsonString());
        Assert.NotNull(await Record.ExceptionAsync(() => fixture.RunAsync()));
        Assert.Equal(0, fixture.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Follow_native_identity_failure_prevents_command_turn_start(bool mismatch)
    {
        using var fixture = await Fixture.CreateAsync();
        if (mismatch) await fixture.RunAsync();
        var result = await fixture.RunAsync(async (configuration, prompt, directory, inputs, output, error, token, started) =>
        {
            using var nativeOutput = new StringReader(
                "{\"id\":1,\"result\":{}}\n{\"id\":2,\"result\":{\"thread\":{\"id\":\"unexpected\"}}}\n");
            using var nativeInput = new StringWriter();
            if (!mismatch)
            {
                // The actual controller callback must persist to this path before turn/start.
                var statePath = Directory.GetFiles(Path.Combine(fixture.Root, "conductor-follow"), "session.json", SearchOption.AllDirectories).Single();
                FileCleanup.EnsureDeleted(statePath);
                Directory.CreateDirectory(statePath);
            }
            var failure = await Record.ExceptionAsync(() => CodexAppServerBroker.RunProtocolAsync(
                configuration, prompt, CodexAppServerBroker.CreateDynamicToolPolicy(configuration, directory, inputs, null),
                null, nativeInput, nativeOutput, output, error, token, threadStarted: started));
            Assert.NotNull(failure);
            Assert.DoesNotContain("turn/start", nativeInput.ToString());
            throw new IOException("native identity fixture failed closed", failure);
        }, second: mismatch);
        Assert.Equal("uncertain", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Follow_linked_state_refuses_without_following_or_launching()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.RunAsync();
        var events = Path.Combine(Path.GetDirectoryName(fixture.StatePath)!, "events");
        var retained = events + "-retained";
        Directory.Move(events, retained);
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(events);
        start.ArgumentList.Add(retained);
        using var process = global::Baton.Core.ProcessLaunch.Start(start)!;
        await BoundedProcessWait.RunToExitAsync(process, TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.Contains((await fixture.RunAsync(second: true)).GetProperty("status").GetString(), new[] { "refused", "uncertain" });
            Assert.Equal(1, fixture.Calls);
        }
        finally { Directory.Delete(events); }
    }

    [Fact]
    public async Task Follow_idle_stdin_remains_blocked_without_a_model_turn()
    {
        using var fixture = await Fixture.CreateAsync();
        var reader = new IdleReader();
        using var output = new StringWriter();
        var task = ConductorFollowCommand.ExecuteAsync(fixture.RequestFile, output, fixture.Root,
            (_, _) => Task.FromResult<RepositoryIdentity?>(fixture.Repository), reader,
            TestContext.Current.CancellationToken, fixture.Broker);
        await reader.Waiting.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);
        Assert.Equal(0, fixture.Calls);
        reader.Eof.SetResult();
        Assert.Equal(0, await task);
        Assert.Equal("", output.ToString());
    }

    private sealed class IdleReader : TextReader
    {
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Eof { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            Waiting.TrySetResult();
            await Eof.Task.WaitAsync(cancellationToken);
            return 0;
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "baton-follow-controls-" + Guid.NewGuid().ToString("N"));
        internal string Workspace => Path.Combine(Root, "workspace");
        internal string RequestFile => Path.Combine(Root, "request.json");
        internal string StatePath => Directory.GetFiles(Path.Combine(Root, "conductor-follow"), "session.json", SearchOption.AllDirectories).Single();
        internal RepositoryIdentity Repository { get; } = RepositoryIdentity.From("https://github.com/philipreese/follow-controls.git", null)!;
        internal string Key => StoppedWorkJudgmentKey.For(Repository.Value, "source", new FleetAttemptId("attempt"), WorkStage.Review);
        internal int Calls { get; private set; }

        internal static async Task<Fixture> CreateAsync(string? change = null)
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.Workspace);
            await ConductorClaimStore.ClaimAsync(f.Repository, "holder", f.Root);
            ProjectCeilingStore.Set(f.Workspace, ProjectCeiling.Unrestricted, Path.Combine(f.Root, "project-ceilings.json"));
            var intent = new StoppedWorkJudgment(f.Key, f.Repository.Value, "source", new FleetAttemptId("attempt"), WorkStage.Review,
                DateTimeOffset.UtcNow, "holder", null, null, "base", "failed", true, null, null, "", StoppedWorkHaltCause.MissingVerdict);
            intent = intent with { ContextSha256 = StoppedWorkAdviceEvidence.Hash(StoppedWorkAdviceEvidence.Context(intent)) };
            var secondKey = StoppedWorkJudgmentKey.For(f.Repository.Value, "second", new FleetAttemptId("second-attempt"), WorkStage.Review);
            var secondIntent = intent with { Key = secondKey, Tag = "second", AttemptId = new FleetAttemptId("second-attempt") };
            secondIntent = secondIntent with { ContextSha256 = StoppedWorkAdviceEvidence.Hash(StoppedWorkAdviceEvidence.Context(secondIntent)) };
            QueueItem Item(StoppedWorkJudgment source) => new()
            {
                Tag = source.Tag,
                Role = "review",
                SpecFile = "fixture.md",
                Workspace = f.Workspace,
                Repository = f.Repository.Value,
                Stage = source.Stage,
                State = QueueItemState.Failed,
                Halted = true,
                AttemptId = source.AttemptId,
                AttemptBaseRevision = source.AttemptBaseRevision,
                StoppedWorkJudgment = source,
                OwnedTask = new("task-" + source.Tag, f.Repository.Value, 2628, "digest", "holder", source.ObservedAt,
                    Blocked: new("missingverdict", "halt", source.ObservedAt, source.Key)),
            };
            await QueueStore.MutateAsync(Path.Combine(f.Root, "queue", "queue.json"), snapshot => snapshot with
            { Items = [Item(intent), Item(secondIntent)] });
            var fleet = Path.Combine(f.Root, "fleet");
            Directory.CreateDirectory(fleet);
            var store = new ConductorObligationStore(new FleetEventLog(Path.Combine(fleet, "events.jsonl"),
                Path.Combine(fleet, "events.1.jsonl"), 16 * 1024 * 1024), Path.Combine(fleet, BatonPaths.ConductorObligationsFileName));
            foreach (var source in new[] { intent, secondIntent })
                await store.EnqueueAsync(new(source.Key!, change == "repo" ? "github.com/foreign/repo" : f.Repository.Value,
                    null, source.Tag, null, change == "action" ? "invented" : StoppedWorkJudgmentKey.Action,
                    change == "owner" ? "foreign" : "holder", source.ObservedAt,
                    change == "adapter" ? "invented" : StoppedWorkJudgmentKey.ProviderRoute,
                    change == "capability" ? "invented" : StoppedWorkJudgmentKey.Capability, change != "unsupported",
                    UnsupportedReason: change == "unsupported" ? "fixture unsupported" : null,
                    ContextSha256: source.ContextSha256));
            File.WriteAllText(f.RequestFile, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                repository = f.Repository.Value,
                workspace = f.Workspace,
                holder = "holder",
                adapter = "codex",
                model = "gpt-5.6-luna",
                effort = "low",
                timeoutSeconds = 30,
                initialInstructions = "Fixed instructions",
                permissionGrant = new PermissionGrant(ReadFiles: true),
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return f;
        }

        internal ConductorFollowBroker Broker => async (_, _, _, _, output, _, token, started) =>
        {
            Calls++;
            await started!("thread-fixed", token);
            await output.WriteLineAsync("{\"type\":\"thread.started\",\"thread_id\":\"thread-fixed\"}");
            await output.WriteLineAsync("{\"type\":\"turn.started\"}");
            await output.WriteLineAsync("{\"type\":\"turn.completed\"}");
            return 0;
        };

        internal async Task<JsonElement> RunAsync(ConductorFollowBroker? broker = null, bool second = false)
        {
            var key = second ? StoppedWorkJudgmentKey.For(Repository.Value, "second", new FleetAttemptId("second-attempt"), WorkStage.Review) : Key;
            using var output = new StringWriter();
            await ConductorCommand.ExecuteAsync(ConductorOptionsParser.Parse(["follow", "--request", RequestFile]), output, Root,
                (_, _) => Task.FromResult<RepositoryIdentity?>(Repository), new StringReader(JsonSerializer.Serialize(new { obligationKey = key }) + "\n"),
                TestContext.Current.CancellationToken, broker ?? Broker);
            return JsonDocument.Parse(output.ToString()).RootElement.Clone();
        }

        public void Dispose() => DirectoryCleanup.DeleteRecursively(Root);
    }
}
