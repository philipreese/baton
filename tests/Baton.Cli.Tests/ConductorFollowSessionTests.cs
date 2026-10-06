using System.Diagnostics;
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

public sealed class ConductorFollowSessionTests
{
    private static readonly RepositoryIdentity Repository =
        RepositoryIdentity.From("https://github.com/philipreese/follow-fixture.git", null)!;

    [Fact]
    public async Task Follow_real_acknowledged_halts_restart_resume_and_replay_without_spend()
    {
        using var fixture = await Fixture.CreateAsync(acknowledged: true);
        var original = await fixture.Store.ReadAsync(fixture.Key("one"), TestContext.Current.CancellationToken);
        Assert.Empty(await fixture.RunAsync(""));
        Assert.Empty(fixture.Calls);
        Assert.Equal("delivered", Assert.Single(await fixture.RunAsync(fixture.Input("one"))).GetProperty("status").GetString());
        Assert.Equal("delivered", Assert.Single(await fixture.RunAsync(fixture.Input("two"))).GetProperty("status").GetString());
        Assert.Equal("replayed", Assert.Single(await fixture.RunAsync(fixture.Input("one"))).GetProperty("status").GetString());
        Assert.Equal(2, fixture.Calls.Count);
        Assert.False(fixture.Calls[0].ResumeSession);
        Assert.True(fixture.Calls[1].ResumeSession);
        Assert.Equal("thread-fixed", fixture.Calls[1].SessionId);
        Assert.All(fixture.Calls, call =>
        {
            Assert.Equal(new PermissionGrant(ReadFiles: true), call.PermissionGrant);
            Assert.Equal("gpt-5.6-luna", call.Model);
            Assert.Equal("low", call.Effort);
            Assert.False(call.AllowsSubagents);
        });
        Assert.Equal(original, await fixture.Store.ReadAsync(fixture.Key("one"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("narrow")]
    [InlineData("forget")]
    public async Task Follow_revalidates_trust_in_the_same_controller(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        var complete = fixture.Broker;
        ConductorFollowBroker broker = async (configuration, prompt, directory, inputs, output, error, token, started) =>
        {
            var result = await complete(configuration, prompt, directory, inputs, output, error, token, started);
            if (change == "revoke") ProjectCeilingStore.Revoke(fixture.Workspace, fixture.CeilingPath);
            if (change == "forget") ProjectCeilingStore.Forget(fixture.Workspace, fixture.CeilingPath);
            if (change == "narrow") ProjectCeilingStore.Set(fixture.Workspace, new(false, false, false, false), fixture.CeilingPath);
            return result;
        };
        var results = await fixture.RunAsync(fixture.Input("one") + fixture.Input("two"), broker);
        Assert.Equal("delivered", results[0].GetProperty("status").GetString());
        Assert.Equal("refused", results[1].GetProperty("status").GetString());
        Assert.Equal(fixture.Key("two"), results[1].GetProperty("obligationKey").GetString());
        Assert.Single(fixture.Calls);
    }

    [Fact]
    public async Task Follow_uncertain_launch_freezes_different_keys_across_restart()
    {
        using var fixture = await Fixture.CreateAsync();
        ConductorFollowBroker interrupted = async (configuration, _, directory, _, _, _, token, started) =>
        {
            fixture.Calls.Add(configuration);
            fixture.EventDirectories.Add(directory);
            await started!("thread-fixed", token);
            throw new IOException("fixture crash after thread identity");
        };
        Assert.Equal("uncertain", Assert.Single(await fixture.RunAsync(fixture.Input("one"), interrupted)).GetProperty("status").GetString());
        Assert.Equal("uncertain", Assert.Single(await fixture.RunAsync(fixture.Input("two"))).GetProperty("status").GetString());
        Assert.Single(fixture.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Follow_complete_response_without_receipt_replays_but_partial_response_freezes(bool partial)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.RunAsync(fixture.Input("one"));
        var directory = Assert.Single(fixture.EventDirectories);
        File.Delete(Path.Combine(directory, "receipt.txt"));
        File.Delete(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(directory))!, "delivery.jsonl"));
        if (partial)
        {
            var path = Path.Combine(directory, "response.json");
            var response = JsonNode.Parse(File.ReadAllText(path))!;
            response["outputLines"] = new JsonArray("{\"type\":\"thread.started\",\"thread_id\":\"thread-fixed\"}");
            File.WriteAllText(path, response.ToJsonString());
        }
        var result = Assert.Single(await fixture.RunAsync(fixture.Input("one")));
        Assert.Equal(partial ? "uncertain" : "replayed", result.GetProperty("status").GetString());
        Assert.Single(fixture.Calls);
        if (partial)
        {
            Assert.Equal("uncertain", Assert.Single(await fixture.RunAsync(fixture.Input("two"))).GetProperty("status").GetString());
            Assert.Single(fixture.Calls);
        }
        else
        {
            Assert.True(File.Exists(Path.Combine(directory, "receipt.txt")));
            Assert.Contains("turn.completed", result.GetProperty("response").GetRawText());
            await fixture.RunAsync(fixture.Input("two"));
            Assert.Equal(2, fixture.Calls.Count);
        }
    }

    [Theory]
    [InlineData("write")]
    [InlineData("grantField")]
    [InlineData("shell")]
    [InlineData("network")]
    [InlineData("restore")]
    [InlineData("missing")]
    [InlineData("model")]
    [InlineData("effort")]
    [InlineData("adapter")]
    [InlineData("alias")]
    [InlineData("unknown")]
    [InlineData("timeout")]
    [InlineData("relative")]
    public async Task Follow_refuses_invalid_explicit_request_before_broker(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        var request = JsonNode.Parse(File.ReadAllText(fixture.RequestFile))!;
        switch (change)
        {
            case "write": request["permissionGrant"]!["writeFiles"] = true; break;
            case "grantField": request["permissionGrant"]!["allowsSubagents"] = true; break;
            case "shell": request["permissionGrant"]!["runShellCommands"] = true; break;
            case "network": request["permissionGrant"]!["networkAccess"] = true; break;
            case "restore": request["permissionGrant"]!["exactFileRestore"] = true; break;
            case "missing": request.AsObject().Remove("permissionGrant"); break;
            case "model": request["model"] = "unknown"; break;
            case "effort": request["effort"] = "invented"; break;
            case "adapter": request["adapter"] = "codex-app-server"; break;
            case "alias": request["owner"] = "holder-1"; request.AsObject().Remove("holder"); break;
            case "unknown": request["stateDirectory"] = fixture.Root; break;
            case "timeout": request["timeoutSeconds"] = 0; break;
            case "relative": request["workspace"] = "relative"; break;
        }
        File.WriteAllText(fixture.RequestFile, request.ToJsonString());
        Assert.NotNull(await Record.ExceptionAsync(() => fixture.RunAsync(fixture.Input("one"))));
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData("repo")]
    [InlineData("owner")]
    [InlineData("capability")]
    [InlineData("attempt")]
    [InlineData("stage")]
    [InlineData("head")]
    [InlineData("hash")]
    [InlineData("halt")]
    [InlineData("owned")]
    [InlineData("terminal")]
    public async Task Follow_refuses_inconsistent_real_source_before_broker(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await QueueStore.MutateAsync(fixture.QueuePath, snapshot => snapshot with
        {
            Items = snapshot.Items.Select(item => item.Tag != "one" ? item : change switch
            {
                "repo" => item with { Repository = "github.com/foreign/repo" },
                "owner" => item with { OwnedTask = item.OwnedTask! with { ConductorHolder = "foreign" } },
                "capability" => item with { StoppedWorkJudgment = item.StoppedWorkJudgment! with { Key = "foreign" } },
                "attempt" => item with { AttemptId = new FleetAttemptId("foreign") },
                "stage" => item with { Stage = WorkStage.Implement },
                "head" => item with { StoppedWorkJudgment = item.StoppedWorkJudgment! with { PullRequestHead = new string('b', 40) } },
                "hash" => item with { StoppedWorkJudgment = item.StoppedWorkJudgment! with { ContextSha256 = "invalid" } },
                "halt" => item with { Halted = false },
                "owned" => item with { OwnedTask = null },
                "terminal" => item with { State = QueueItemState.Done },
                _ => item,
            }).ToArray(),
        }, TestContext.Current.CancellationToken);
        var result = Assert.Single(await fixture.RunAsync(fixture.Input("one")));
        Assert.Equal("refused", result.GetProperty("status").GetString());
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Follow_claim_change_or_same_holder_reacquisition_refuses_next_delivery(bool reacquire)
    {
        using var fixture = await Fixture.CreateAsync();
        var complete = fixture.Broker;
        ConductorFollowBroker broker = async (configuration, prompt, directory, inputs, output, error, token, started) =>
        {
            var result = await complete(configuration, prompt, directory, inputs, output, error, token, started);
            if (reacquire)
            {
                await ConductorClaimStore.ReleaseAsync(Repository, "holder-1", "fixture", fixture.Root, cancellationToken: token);
                await ConductorClaimStore.ClaimAsync(Repository, "holder-1", fixture.Root, cancellationToken: token);
            }
            else
                await ConductorClaimStore.TakeoverAsync(Repository, "holder-2", "fixture", fixture.Root, cancellationToken: token);
            return result;
        };
        var results = await fixture.RunAsync(fixture.Input("one") + fixture.Input("two"), broker);
        Assert.Equal("refused", results[1].GetProperty("status").GetString());
        Assert.Single(fixture.Calls);
        Assert.NotNull(await Record.ExceptionAsync(() => fixture.RunAsync(fixture.Input("two"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain-key")]
    [InlineData("{}")]
    [InlineData("{\"obligationKey\":\"\"}")]
    [InlineData("{\"obligationKey\":\"foreign\"}")]
    [InlineData("{\"obligationKey\":\"foreign\",\"extra\":true}")]
    [InlineData("{\"obligationKey\":\"foreign\",\"obligationKey\":\"again\"}")]
    public async Task Follow_malformed_or_foreign_event_returns_one_json_refusal(string line)
    {
        using var fixture = await Fixture.CreateAsync();
        var result = Assert.Single(await fixture.RunAsync(line + "\n"));
        Assert.Equal("refused", result.GetProperty("status").GetString());
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task Follow_overlong_event_is_bounded_and_refused()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.Equal("refused", Assert.Single(await fixture.RunAsync(new string('x', 9000) + "\n")).GetProperty("status").GetString());
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("instructions")]
    [InlineData("state")]
    [InlineData("identity")]
    public async Task Follow_configuration_or_retained_evidence_drift_refuses_new_turn(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.RunAsync(fixture.Input("one"));
        if (change is "model" or "instructions")
        {
            var request = JsonNode.Parse(File.ReadAllText(fixture.RequestFile))!;
            request[change == "model" ? "model" : "initialInstructions"] = change == "model" ? "gpt-6-sol" : "changed";
            File.WriteAllText(fixture.RequestFile, request.ToJsonString());
        }
        else
        {
            var path = change == "state" ? fixture.StatePath : Path.Combine(fixture.EventDirectories[0], "identity.json");
            File.WriteAllText(path, "{}");
        }
        var results = await fixture.RunAsync(fixture.Input("two"));
        Assert.Contains(results[0].GetProperty("status").GetString(), new[] { "refused", "uncertain" });
        Assert.Single(fixture.Calls);
    }

    [Fact]
    public async Task Follow_competing_processes_with_distinct_keys_serialize_one_native_thread()
    {
        using var fixture = await Fixture.CreateAsync();
        var processes = new[] { "one", "two" }.Select(tag =>
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(ConductorFollowSessionTests).Assembly.Location);
            start.ArgumentList.Add("-method");
            start.ArgumentList.Add("*Follow_controller_process_fixture");
            start.ArgumentList.Add("-failSkips");
            start.ArgumentList.Add("-failWarns");
            start.Environment["BATON_FOLLOW_FIXTURE_ROOT"] = fixture.Root;
            start.Environment["BATON_FOLLOW_FIXTURE_TAG"] = tag;
            start.Environment["BATON_INPUT_FOREIGN"] = Path.Combine(fixture.Root, "ambient-input.txt");
            start.Environment["BATON_OUTPUT_DIR"] = Path.Combine(fixture.Root, "ambient-output");
            return Process.Start(start)!;
        }).ToArray();
        try
        {
            foreach (var process in processes)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.True(process.ExitCode == 0, await stdout + await stderr);
            }
            var calls = File.ReadAllLines(Path.Combine(fixture.Root, "native-calls.jsonl"))
                .Select(line => JsonNode.Parse(line)!).ToArray();
            Assert.Equal(2, calls.Length);
            Assert.False(calls[0]["resumeSession"]!.GetValue<bool>());
            Assert.True(calls[1]["resumeSession"]!.GetValue<bool>());
            Assert.Equal("thread-fixed", calls[1]["sessionId"]!.GetValue<string>());
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    [Fact]
    public async Task Follow_controller_process_fixture()
    {
        var root = Environment.GetEnvironmentVariable("BATON_FOLLOW_FIXTURE_ROOT");
        if (root is null) return;
        using var fixture = new Fixture(root, ownsRoot: false);
        var ambientInput = Environment.GetEnvironmentVariable("BATON_INPUT_FOREIGN");
        var ambientOutput = Environment.GetEnvironmentVariable("BATON_OUTPUT_DIR");
        var complete = fixture.Broker;
        ConductorFollowBroker broker = async (configuration, prompt, directory, inputs, output, error, token, started) =>
        {
            File.AppendAllText(Path.Combine(root, "native-calls.jsonl"),
                JsonSerializer.Serialize(configuration, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n");
            await Task.Delay(150, token); // wait-ok: hold the fixture broker open for a competing OS controller; not an assertion deadline
            return await complete(configuration, prompt, directory, inputs, output, error, token, started);
        };
        Assert.Equal("delivered", Assert.Single(await fixture.RunAsync(fixture.Input(
            Environment.GetEnvironmentVariable("BATON_FOLLOW_FIXTURE_TAG")!), broker)).GetProperty("status").GetString());
        Assert.Equal(ambientInput, Environment.GetEnvironmentVariable("BATON_INPUT_FOREIGN"));
        Assert.Equal(ambientOutput, Environment.GetEnvironmentVariable("BATON_OUTPUT_DIR"));
        Assert.False(Directory.Exists(ambientOutput));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly bool _ownsRoot;
        public string Root { get; }
        public string Workspace => Path.Combine(Root, "workspace");
        public string RequestFile => Path.Combine(Root, "request.json");
        public string CeilingPath => Path.Combine(Root, "project-ceilings.json");
        public string QueuePath => Path.Combine(Root, "queue", "queue.json");
        public string StatePath => Assert.Single(Directory.EnumerateFiles(Path.Combine(Root, "conductor-follow"), "session.json", SearchOption.AllDirectories));
        public List<CodexBrokerConfiguration> Calls { get; } = [];
        public List<string> EventDirectories { get; } = [];
        public ConductorObligationStore Store { get; }

        public Fixture(string? root = null, bool ownsRoot = true)
        {
            Root = root ?? Path.Combine(Path.GetTempPath(), $"baton-follow-{Guid.NewGuid():N}");
            _ownsRoot = ownsRoot;
            Directory.CreateDirectory(Workspace);
            var fleet = Path.Combine(Root, "fleet");
            Directory.CreateDirectory(fleet);
            Store = new(new FleetEventLog(Path.Combine(fleet, "events.jsonl"), Path.Combine(fleet, "events.1.jsonl"), 16 * 1024 * 1024),
                Path.Combine(fleet, BatonPaths.ConductorObligationsFileName));
        }

        public string Key(string tag) => StoppedWorkJudgmentKey.For(Repository.Value, tag, new FleetAttemptId("attempt-" + tag), WorkStage.Review);
        public string Input(string tag) => JsonSerializer.Serialize(new { obligationKey = Key(tag) }) + "\n";

        public static async Task<Fixture> CreateAsync(bool acknowledged = false)
        {
            var fixture = new Fixture();
            await ConductorClaimStore.ClaimAsync(Repository, "holder-1", fixture.Root);
            ProjectCeilingStore.Set(fixture.Workspace, ProjectCeiling.Unrestricted, fixture.CeilingPath);
            var items = new List<QueueItem>();
            foreach (var tag in new[] { "one", "two" })
            {
                var intent = new StoppedWorkJudgment(fixture.Key(tag), Repository.Value, tag, new FleetAttemptId("attempt-" + tag),
                    WorkStage.Review, DateTimeOffset.UtcNow, "holder-1", 42, new string('a', 40), new string('c', 40),
                    "failed", true, "checks", DateTimeOffset.UtcNow, "", StoppedWorkHaltCause.MissingVerdict, "available", false, "required");
                intent = intent with { ContextSha256 = StoppedWorkAdviceEvidence.Hash(StoppedWorkAdviceEvidence.Context(intent)) };
                items.Add(new QueueItem
                {
                    Tag = tag,
                    Role = "review",
                    SpecFile = "fixture.md",
                    Workspace = fixture.Workspace,
                    Repository = Repository.Value,
                    Stage = WorkStage.Review,
                    State = QueueItemState.Failed,
                    Halted = true,
                    AttemptId = intent.AttemptId,
                    AttemptBaseRevision = intent.AttemptBaseRevision,
                    PullRequest = intent.PullRequest,
                    StoppedWorkJudgment = intent,
                    OwnedTask = new("task-" + tag, Repository.Value, 2628, "digest", "holder-1", intent.ObservedAt,
                        Blocked: new("missingverdict", "fixture halt", intent.ObservedAt, intent.Key)),
                });
                await fixture.Store.EnqueueAsync(new(intent.Key!, intent.Repository, null, tag, intent.PullRequestHead,
                    StoppedWorkJudgmentKey.Action, "holder-1", intent.ObservedAt, StoppedWorkJudgmentKey.ProviderRoute,
                    StoppedWorkJudgmentKey.Capability, true, TargetRevision: intent.PullRequestHead, ContextSha256: intent.ContextSha256));
                if (acknowledged)
                    await fixture.Store.SubmitAsync(intent.Key!, (_, _) => Task.FromResult(new ConductorTransportResult(true, "prior-advice")));
            }
            await QueueStore.MutateAsync(fixture.QueuePath, snapshot => snapshot with { Items = items });
            File.WriteAllText(fixture.RequestFile, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                repository = Repository.Value,
                workspace = fixture.Workspace,
                holder = "holder-1",
                adapter = "codex",
                model = "gpt-5.6-luna",
                effort = "low",
                timeoutSeconds = 30,
                initialInstructions = "Fixed conductor instructions",
                permissionGrant = new PermissionGrant(ReadFiles: true),
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return fixture;
        }

        public ConductorFollowBroker Broker => async (configuration, prompt, directory, inputs, output, error, token, started) =>
        {
            Calls.Add(configuration);
            EventDirectories.Add(Path.GetDirectoryName(directory)!);
            var source = Assert.Single(inputs);
            Assert.True(File.Exists(source));
            Assert.StartsWith(Path.Combine(Root, "conductor-follow"), source, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Path.Combine(Root, "conductor-follow"), directory, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("untrusted", prompt, StringComparison.OrdinalIgnoreCase);
            using var serverOutput = new StringReader(
                "{\"id\":1,\"result\":{}}\n" +
                "{\"id\":2,\"result\":{\"thread\":{\"id\":\"thread-fixed\"}}}\n" +
                "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn-fixed\",\"status\":\"inProgress\"}}}\n" +
                "{\"method\":\"turn/completed\",\"params\":{\"turn\":{\"status\":\"completed\"}}}\n");
            using var serverInput = new StringWriter();
            var result = await CodexAppServerBroker.RunProtocolAsync(configuration, prompt,
                CodexAppServerBroker.CreateDynamicToolPolicy(configuration, directory, inputs, null), null,
                serverInput, serverOutput, output, error, token, threadStarted: started);
            var requests = serverInput.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
            Assert.Equal(configuration.ResumeSession ? "thread/resume" : "thread/start", requests[2]["method"]!.GetValue<string>());
            Assert.Equal("thread-fixed", requests[3]["params"]!["threadId"]!.GetValue<string>());
            return result;
        };

        public async Task<JsonElement[]> RunAsync(string input, ConductorFollowBroker? broker = null)
        {
            using var output = new StringWriter();
            await ConductorCommand.ExecuteAsync(ConductorOptionsParser.Parse(["follow", "--request", RequestFile]), output, Root,
                (_, _) => Task.FromResult<RepositoryIdentity?>(Repository), new StringReader(input),
                TestContext.Current.CancellationToken, broker ?? Broker);
            return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        }

        public void Dispose() { if (_ownsRoot) DirectoryCleanup.DeleteRecursively(Root); }
    }
}
