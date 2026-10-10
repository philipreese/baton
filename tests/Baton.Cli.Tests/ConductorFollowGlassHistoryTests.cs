using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;

namespace Baton.Cli.Tests;

public sealed partial class ConductorFollowDeliveryTests
{
    [Fact]
    public async Task Glass_completed_hold_survives_pending_clear_replay_and_later_task_transition()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        var launches = 0;
        using var scheduler = fixture.Scheduler(launch: (_, _) =>
        {
            launches++;
            throw new InvalidOperationException("Hold cannot launch a replacement.");
        });
        await fixture.HaltAsync("history", scheduler: scheduler);
        Assert.False((await fixture.RowAsync("history")).StoppedWorkJudgment!.FollowContinuationPending);
        await scheduler.TickOnceAsync(Ct);
        await fixture.FollowAsync("history");
        await scheduler.RecoverAttachedFollowAsync(Ct);
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with
        {
            Items = queue.Items.Select(item => item with { Halted = false, State = QueueItemState.Done }).ToList(),
        }, Ct);
        await using var glass = await GlassFixture.StartAsync(fixture);
        var status = await glass.StatusAsync();
        var judgment = Assert.Single(status.GetProperty("history").GetProperty("judgments").EnumerateArray());
        Assert.Equal("Hold", judgment.GetProperty("decision").GetString());
        Assert.Equal("history", judgment.GetProperty("tag").GetString());
        Assert.Equal(Head, judgment.GetProperty("sourceHeadSha").GetString());
        var source = JsonNode.Parse(File.ReadAllText(fixture.EventEvidencePath("history", "source.json")))!;
        Assert.Equal(source["context"]!["observedAt"]!.GetValue<string>(), judgment.GetProperty("sourceObservedAt").GetString());
        Assert.True(status.GetProperty("stopEligible").GetBoolean());
        Assert.True(status.GetProperty("holdEligible").GetBoolean());
        Assert.Empty(status.GetProperty("actions").EnumerateArray());
        Assert.Single(fixture.Calls);
        Assert.Equal(0, launches);
        Assert.DoesNotContain(fixture.Root, judgment.GetRawText());
        Assert.DoesNotContain("retained-thread", judgment.GetRawText());
        Assert.False(judgment.TryGetProperty("state", out _));
        Assert.False(judgment.TryGetProperty("rationale", out _));
        Assert.False(judgment.TryGetProperty("completedAt", out _));
    }

    [Theory]
    [InlineData("identity.json", "claimGeneration")]
    [InlineData("identity.json", "configurationSha256")]
    [InlineData("identity.json", "obligationId")]
    [InlineData("identity.json", "obligationKey")]
    [InlineData("identity.json", "sessionId")]
    [InlineData("identity.json", "sourceCapability")]
    [InlineData("source.json", "owner")]
    [InlineData("source.json", "idempotencyKey")]
    [InlineData("source.json", "adapter")]
    [InlineData("source.json", "adapterCapability")]
    [InlineData("source.json", "context")]
    [InlineData("launch.json", "obligationId")]
    [InlineData("launch.json", "expectedSessionId")]
    [InlineData("response.json", "obligationKey")]
    [InlineData("response.json", "sessionId")]
    [InlineData("response.json", "exitCode")]
    [InlineData("response.json", "outputLines")]
    [InlineData("decision.json", "sourceDigest")]
    [InlineData("decision.json", "requestSha256")]
    [InlineData("decision.json", "sourceHeadSha")]
    [InlineData("decision.json", "decision")]
    [InlineData("decision.json", "responseSha256")]
    [InlineData("decision.json", "receipt")]
    [InlineData("receipt.txt", "bytes")]
    [InlineData("delivery.jsonl", "obligationKey")]
    [InlineData("delivery.jsonl", "duplicate")]
    [InlineData("partial-transcript.jsonl", "bytes")]
    [InlineData("directory", "name")]
    public async Task Glass_history_rejects_each_corrupt_event_link_without_repair_or_disabling_controls(string file, string field)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        await fixture.HaltAsync("verified");
        await fixture.HaltAsync("corrupt");
        var directory = Path.GetDirectoryName(fixture.EventEvidencePath("corrupt", "identity.json"))!;
        if (file == "directory") Directory.Move(directory, directory + "-malformed");
        else if (file == "receipt.txt" || file == "partial-transcript.jsonl") File.WriteAllText(Path.Combine(directory, file), "private corrupted evidence");
        else if (file == "delivery.jsonl")
        {
            var path = Path.Combine(SessionDirectory(fixture), file);
            var lines = File.ReadAllLines(path);
            var entries = lines.Select(line => JsonNode.Parse(line)!).ToList();
            var bad = entries.Single(entry => entry["obligationKey"]!.GetValue<string>() == fixture.Key("corrupt"));
            if (field == "duplicate") entries.Add(bad.DeepClone());
            else bad[field] = "foreign";
            File.WriteAllText(path, string.Join('\n', entries.Select(entry => entry.ToJsonString())) + "\n");
        }
        else
        {
            var path = Path.Combine(directory, file);
            var node = JsonNode.Parse(File.ReadAllText(path))!;
            if (field == "context") node[field]!["attemptId"] = JsonSerializer.SerializeToNode(new FleetAttemptId("foreign"));
            else if (field == "exitCode") node[field] = 1;
            else if (field == "outputLines") node[field] = new JsonArray("private prose without completion");
            else node[field] = "foreign";
            File.WriteAllText(path, node.ToJsonString());
        }
        var before = RetainedHistoryBytes(fixture);
        await using var glass = await GlassFixture.StartAsync(fixture);
        for (var read = 0; read < 2; read++)
        {
            var status = await glass.StatusAsync();
            var history = status.GetProperty("history");
            Assert.Equal("verified", Assert.Single(history.GetProperty("judgments").EnumerateArray()).GetProperty("tag").GetString());
            Assert.Equal(1, history.GetProperty("invalidEvents").GetInt32());
            Assert.True(status.GetProperty("stopEligible").GetBoolean());
            Assert.True(status.GetProperty("holdEligible").GetBoolean());
            Assert.DoesNotContain("private corrupted evidence", status.GetRawText());
        }
        AssertRetainedHistoryBytes(fixture, before);
    }

    [Theory]
    [InlineData("identity.json")]
    [InlineData("source.json")]
    [InlineData("launch.json")]
    [InlineData("response.json")]
    [InlineData("receipt.txt")]
    [InlineData("decision.json")]
    [InlineData("delivery.jsonl")]
    public async Task Glass_history_missing_artifacts_are_never_repaired(string file)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        await fixture.HaltAsync("missing");
        var path = file == "delivery.jsonl" ? Path.Combine(SessionDirectory(fixture), file) : fixture.EventEvidencePath("missing", file);
        File.Delete(path);
        var before = RetainedHistoryBytes(fixture);
        await using var glass = await GlassFixture.StartAsync(fixture);
        Assert.Empty((await glass.StatusAsync()).GetProperty("history").GetProperty("judgments").EnumerateArray());
        Assert.False(File.Exists(path));
        AssertRetainedHistoryBytes(fixture, before);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("configuration")]
    [InlineData("acquisition")]
    public async Task Glass_history_refuses_drifted_request_configuration_and_acquisition(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        await fixture.HaltAsync("saved");
        var path = Path.Combine(SessionDirectory(fixture), change == "request" ? "request.json" : "session.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node[change == "request" ? "initialInstructions" : change == "configuration" ? "model" : "claimGeneration"] = "foreign";
        File.WriteAllText(path, node.ToJsonString());
        var before = RetainedHistoryBytes(fixture);
        var history = ConductorFollowSession.ReadGlassHistory(Identity, fixture.Root, token: Ct);
        Assert.Empty(history.Judgments);
        Assert.Equal("Retained judgment history could not be verified.", history.Diagnostic);
        AssertRetainedHistoryBytes(fixture, before);
    }

    [Theory]
    [InlineData("missing", true)]
    [InlineData("null", false)]
    [InlineData("unknown", false)]
    public async Task Glass_history_uses_authoritative_kind_classifier_and_missing_legacy_default(string kind, bool valid)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        await fixture.HaltAsync("kind");
        var path = fixture.EventEvidencePath("kind", "identity.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (kind == "missing") node.Remove("kind");
        else node["kind"] = kind == "null" ? null : "unknown";
        File.WriteAllText(path, node.ToJsonString());
        await using var glass = await GlassFixture.StartAsync(fixture);
        var history = (await glass.StatusAsync()).GetProperty("history");
        Assert.Equal(valid ? 1 : 0, history.GetProperty("judgments").GetArrayLength());
        Assert.Equal(valid ? 0 : 1, history.GetProperty("invalidEvents").GetInt32());
    }

    [Fact]
    public async Task Glass_history_preserves_verified_rows_amid_correction_prose_and_unrelated_busy_turn()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        await fixture.HaltAsync("verified");
        await using var glass = await GlassFixture.StartAsync(fixture);
        await AcceptCorrectionAsync(fixture, glass);
        await fixture.DeliverCorrectionAsync();
        fixture.Reply = "prose";
        await fixture.HaltAsync("prose");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.WaitInBroker = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var busy = fixture.HaltAsync("busy");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); // wait-ok: Offline broker rendezvous.
            var before = RetainedHistoryBytes(fixture);
            var status = await glass.StatusAsync();
            var history = status.GetProperty("history");
            Assert.Equal("verified", Assert.Single(history.GetProperty("judgments").EnumerateArray()).GetProperty("tag").GetString());
            Assert.Equal(2, history.GetProperty("excludedEvents").GetInt32());
            Assert.Equal(1, history.GetProperty("invalidEvents").GetInt32());
            Assert.True(status.GetProperty("stopEligible").GetBoolean());
            Assert.True(status.GetProperty("holdEligible").GetBoolean());
            AssertRetainedHistoryBytes(fixture, before);
        }
        finally { release.TrySetResult(); }
        await busy;
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("attempt", false)]
    [InlineData("head", false)]
    [InlineData("key", false)]
    [InlineData("digest", false)]
    [InlineData("directory", false)]
    [InlineData("holder", false)]
    [InlineData("provenance", false)]
    public async Task Glass_history_replacement_action_join_requires_complete_retained_provenance(string change, bool match)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await fixture.HaltAsync("same-tag");
        var action = (await fixture.RowAsync("same-tag")).ReplacementReviewAction!;
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with
        {
            Items = queue.Items.Select(item => item with
            {
                ReplacementReviewAction = change switch
                {
                    "attempt" => action with { SourceAttemptId = new FleetAttemptId("different-attempt") },
                    "head" => action with { HeadSha = new string('b', 40) },
                    "key" => action with { ObligationKey = "foreign" },
                    "digest" => action with { EvidenceDigest = new string('b', 64) },
                    "directory" => action with { EvidenceDirectory = Path.Combine(fixture.Root, "foreign") },
                    "holder" => action with { Holder = "foreign" },
                    "provenance" => action with { EvidenceProvenance = ReplacementReviewEvidenceProvenance.LegacyAdvice },
                    _ => action,
                },
            }).ToList(),
        }, Ct);
        await using var glass = await GlassFixture.StartAsync(fixture);
        var judgment = Assert.Single((await glass.StatusAsync()).GetProperty("history").GetProperty("judgments").EnumerateArray());
        Assert.Equal("ReplaceReview", judgment.GetProperty("decision").GetString());
        Assert.Equal(match ? "retained-unlaunched" : null, judgment.GetProperty("actionDisposition").GetString());
    }

    [Fact]
    public async Task Glass_history_twenty_row_limit_reports_omissions_in_identity_order()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        for (var index = 0; index < 21; index++) await fixture.HaltAsync("history-" + index);
        await using var glass = await GlassFixture.StartAsync(fixture);
        var history = (await glass.StatusAsync()).GetProperty("history");
        var judgments = history.GetProperty("judgments").EnumerateArray().ToArray();
        Assert.Equal(20, judgments.Length);
        Assert.Equal(1, history.GetProperty("omittedJudgments").GetInt32());
        Assert.Equal(JsonValueKind.Null, history.GetProperty("diagnostic").ValueKind);
        var expected = Directory.GetDirectories(Path.Combine(SessionDirectory(fixture), "events"))
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).Take(20).ToArray();
        Assert.Equal(expected, judgments.Select(judgment => judgment.GetProperty("eventIdentity").GetString()));
        Assert.NotEqual(judgments.Select(judgment => judgment.GetProperty("sourceObservedAt").GetString()).Order(),
            judgments.Select(judgment => judgment.GetProperty("sourceObservedAt").GetString()));
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("generation", false)]
    [InlineData("attachment", false)]
    [InlineData("request", false)]
    [InlineData("configuration", false)]
    [InlineData("response", false)]
    [InlineData("session", false)]
    [InlineData("issued-attempt", false)]
    [InlineData("issued-room", false)]
    public async Task Glass_history_issued_action_join_binds_acquisition_and_all_issued_decision_provenance(string change, bool match)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        using var scheduler = fixture.Scheduler(launch: (request, _) => Task.FromResult(new Baton.Cli.Daemon.QueueLaunchOutcome(request.RoomDirectory)));
        await fixture.HaltAsync("issued", scheduler: scheduler);
        await scheduler.TickOnceAsync(Ct);
        var action = (await fixture.RowAsync("issued")).ReplacementReviewAction!;
        Assert.NotNull(action.IssuedAuthority);
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with
        {
            Items = queue.Items.Select(item => item with
            {
                ReplacementReviewAction = action with
                {
                    IssuedAuthority = change switch
                    {
                        "generation" => action.IssuedAuthority! with { ClaimGeneration = "foreign" },
                        "attachment" => action.IssuedAuthority! with { AttachmentId = "foreign" },
                        "request" => action.IssuedAuthority! with { RequestSha256 = "foreign" },
                        "configuration" => action.IssuedAuthority! with { ConfigurationSha256 = "foreign" },
                        "response" => action.IssuedAuthority! with { ResponseSha256 = "foreign" },
                        "session" => action.IssuedAuthority! with { SessionId = "foreign" },
                        "issued-attempt" => action.IssuedAuthority! with { AttemptId = new FleetAttemptId("foreign") },
                        "issued-room" => action.IssuedAuthority! with { RoomDirectory = Path.Combine(fixture.Root, "foreign") },
                        _ => action.IssuedAuthority,
                    },
                },
            }).ToList(),
        }, Ct);
        await using var glass = await GlassFixture.StartAsync(fixture);
        var judgment = Assert.Single((await glass.StatusAsync()).GetProperty("history").GetProperty("judgments").EnumerateArray());
        Assert.Equal(match ? "issued" : null, judgment.GetProperty("actionDisposition").GetString());
    }

    [Fact]
    public async Task Glass_history_hundred_directory_limit_is_explicit_and_independent_of_controls()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        await fixture.HaltAsync("verified");
        var events = Path.Combine(SessionDirectory(fixture), "events");
        for (var index = 0; index < 99; index++) Directory.CreateDirectory(Path.Combine(events, "malformed-" + index));
        await using var glass = await GlassFixture.StartAsync(fixture);
        var status = await glass.StatusAsync();
        Assert.Single(status.GetProperty("history").GetProperty("judgments").EnumerateArray());
        Assert.Equal(99, status.GetProperty("history").GetProperty("invalidEvents").GetInt32());
        Directory.CreateDirectory(Path.Combine(events, "overflow"));
        status = await glass.StatusAsync();
        Assert.Empty(status.GetProperty("history").GetProperty("judgments").EnumerateArray());
        Assert.Equal("history inspection limit reached", status.GetProperty("history").GetProperty("diagnostic").GetString());
        Assert.True(status.GetProperty("stopEligible").GetBoolean());
        Assert.True(status.GetProperty("holdEligible").GetBoolean());
    }

    private static Dictionary<string, byte[]> RetainedHistoryBytes(Fixture fixture) =>
        Directory.EnumerateFiles(Path.Combine(fixture.Root, "conductor-follow"), "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);

    [Fact]
    public async Task Glass_history_eight_mebibyte_budget_keeps_verified_rows_and_reports_unchecked_history()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        for (var index = 0; index < 3; index++)
        {
            var tag = "large-" + index;
            await fixture.HaltAsync(tag);
            var response = fixture.EventEvidencePath(tag, "response.json");
            File.WriteAllText(response, File.ReadAllText(response) + new string(' ', 900_000));
            RebindHistoryResponse(fixture, tag);
        }
        var before = RetainedHistoryBytes(fixture);
        await using var glass = await GlassFixture.StartAsync(fixture);
        var status = await glass.StatusAsync();
        var history = status.GetProperty("history");
        Assert.Equal(2, history.GetProperty("judgments").GetArrayLength());
        Assert.Equal("inspection incomplete; additional history not checked", history.GetProperty("diagnostic").GetString());
        Assert.True(status.GetProperty("stopEligible").GetBoolean());
        Assert.True(status.GetProperty("holdEligible").GetBoolean());
        AssertRetainedHistoryBytes(fixture, before);
    }

    [Fact]
    public async Task Glass_history_one_second_budget_keeps_verified_rows_and_empty_budget_never_fabricates_empty_history()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        await fixture.HaltAsync("time-a");
        var checks = 0;
        var control = ConductorFollowSession.ReadGlassHistory(Identity, fixture.Root,
            elapsed: () => { checks++; return TimeSpan.Zero; }, token: Ct);
        Assert.Single(control.Judgments);
        var oneEventChecks = checks;
        await fixture.HaltAsync("time-b");
        checks = 0;
        // One additional check reads the second journal entry. Expire at the next event boundary.
        var history = ConductorFollowSession.ReadGlassHistory(Identity, fixture.Root,
            elapsed: () => ++checks > oneEventChecks + 1 ? TimeSpan.FromSeconds(1) : TimeSpan.Zero, token: Ct);
        Assert.Single(history.Judgments);
        Assert.Equal("inspection incomplete; additional history not checked", history.Diagnostic);
        var byteEmpty = ConductorFollowSession.ReadGlassHistory(Identity, fixture.Root, byteLimit: 0, token: Ct);
        Assert.Empty(byteEmpty.Judgments);
        Assert.Equal("inspection incomplete; additional history not checked", byteEmpty.Diagnostic);
        var timeEmpty = ConductorFollowSession.ReadGlassHistory(Identity, fixture.Root, elapsed: () => TimeSpan.FromSeconds(1), token: Ct);
        Assert.Empty(timeEmpty.Judgments);
        Assert.Equal("inspection incomplete; additional history not checked", timeEmpty.Diagnostic);
        var getBudgetEmpty = ConductorFollowSession.ReadGlassHistory(Identity, fixture.Root, token: new CancellationToken(canceled: true));
        Assert.Empty(getBudgetEmpty.Judgments);
        Assert.Equal("inspection incomplete; additional history not checked", getBudgetEmpty.Diagnostic);
    }

    [Fact]
    public async Task Glass_history_projects_allowlisted_identity_without_private_source_or_response_fields()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        await fixture.HaltAsync("private");
        var sourcePath = fixture.EventEvidencePath("private", "source.json");
        var source = JsonNode.Parse(File.ReadAllText(sourcePath))!;
        source["context"]!["checks"] = "PRIVATE-SOURCE " + fixture.Root;
        source["context"]!["repairAllowance"] = "PRIVATE-RATIONALE";
        File.WriteAllText(sourcePath, source.ToJsonString());
        var context = source["context"]!.Deserialize<StoppedWorkAdviceContext>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var hash = StoppedWorkAdviceEvidence.Hash(context);
        var identityPath = fixture.EventEvidencePath("private", "identity.json");
        var identity = JsonNode.Parse(File.ReadAllText(identityPath))!;
        identity["contextSha256"] = hash;
        File.WriteAllText(identityPath, identity.ToJsonString());
        var decisionPath = fixture.EventEvidencePath("private", "decision.json");
        var decision = JsonNode.Parse(File.ReadAllText(decisionPath))!;
        decision["sourceContextSha256"] = hash;
        decision["sourceDigest"] = HistoryFileHash(sourcePath);
        File.WriteAllText(decisionPath, decision.ToJsonString());
        var responsePath = fixture.EventEvidencePath("private", "response.json");
        var response = JsonNode.Parse(File.ReadAllText(responsePath))!;
        response["outputLines"]!.AsArray().Insert(2, JsonValue.Create("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"PRIVATE-PROSE\"}}"));
        File.WriteAllText(responsePath, response.ToJsonString());
        RebindHistoryResponse(fixture, "private");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var history = (await glass.StatusAsync()).GetProperty("history");
        var row = Assert.Single(history.GetProperty("judgments").EnumerateArray());
        Assert.DoesNotContain("PRIVATE", history.GetRawText());
        Assert.DoesNotContain(fixture.Root, history.GetRawText());
        Assert.DoesNotContain("retained-thread", history.GetRawText());
        Assert.Equal(new[] { "actionDisposition", "claimGeneration", "decision", "eventIdentity", "obligationId", "obligationKey",
            "sourceAttempt", "sourceHeadSha", "sourceObservedAt", "sourceRepository", "tag" }, row.EnumerateObject().Select(property => property.Name).Order());
    }

    private static string HistoryFileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void RebindHistoryResponse(Fixture fixture, string tag)
    {
        var responseHash = HistoryFileHash(fixture.EventEvidencePath(tag, "response.json"));
        var receipt = "follow-sha256:" + responseHash;
        File.WriteAllText(fixture.EventEvidencePath(tag, "receipt.txt"), receipt + "\n");
        var decisionPath = fixture.EventEvidencePath(tag, "decision.json");
        var decision = JsonNode.Parse(File.ReadAllText(decisionPath))!;
        decision["responseSha256"] = responseHash;
        decision["receipt"] = receipt;
        File.WriteAllText(decisionPath, decision.ToJsonString());
        var journalPath = Path.Combine(SessionDirectory(fixture), "delivery.jsonl");
        var entries = File.ReadAllLines(journalPath).Select(line => JsonNode.Parse(line)!).ToArray();
        var entry = entries.Single(node => node["obligationKey"]!.GetValue<string>() == fixture.Key(tag));
        entry["responseSha256"] = responseHash;
        entry["receipt"] = receipt;
        File.WriteAllText(journalPath, string.Join('\n', entries.Select(node => node.ToJsonString())) + "\n");
    }

    private static void AssertRetainedHistoryBytes(Fixture fixture, Dictionary<string, byte[]> before)
    {
        var after = RetainedHistoryBytes(fixture);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var file in before) Assert.Equal(file.Value, after[file.Key]);
    }
}
