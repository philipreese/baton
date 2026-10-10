using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Queue;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

public sealed partial class ConductorFollowDeliveryTests
{
    [Fact]
    public async Task Glass_status_projects_exact_nonretired_owned_tasks_as_recorded_evidence()
    {
        using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        QueueItem Owned(string tag, string id, string taskRepository, QueueItemState state, QueueIssuePreparation? preparation = null) => new()
        {
            Tag = tag,
            Role = "implement",
            Workspace = fixture.Workspace,
            SpecFile = Path.Combine(fixture.Root, tag + ".md"),
            Repository = Repository,
            Issue = 2683,
            Stage = WorkStage.Implement,
            State = state,
            IssuePreparation = preparation,
            OwnedTask = new OwnedTaskSubmission(id, taskRepository, 2683, "digest", "prior-holder", now,
                Ready: new TaskReadyReceipt("ready-ref", id, taskRepository, 2683, 42, new string('a', 40),
                    "attempt", "digest", "checks", "observation", now, now)),
        };
        var blocked = Owned("blocked", "task-a", Repository, QueueItemState.Queued,
            new QueueIssuePreparation(TaskPreparationState.Blocked, now, "private detail"));
        var cancelled = Owned("cancelled", "task-b", Repository, QueueItemState.Cancelled);
        var launched = Owned("launched", "task-c", Repository, QueueItemState.Launched,
            new QueueIssuePreparation(TaskPreparationState.Prepared, now));
        var retired = Owned("retired", "task-retired", Repository, QueueItemState.Queued) with
        {
            Retirement = new QueueRetirement(QueueRetirement.Operator, now, "retained history"),
        };
        var mismatch = Owned("mismatch", "task-c", "github.com/other/repository", QueueItemState.Queued);
        var overflow = Enumerable.Range(0, 18)
            .Select(index => Owned("overflow-" + index, "task-" + (char)('d' + index), Repository, QueueItemState.Queued))
            .ToArray();
        var legacy = new QueueItem
        {
            Tag = "legacy",
            Role = "implement",
            Workspace = fixture.Workspace,
            SpecFile = Path.Combine(fixture.Root, "legacy.md"),
            Repository = Repository,
            Issue = 2683,
        };
        await QueueStore.MutateAsync(BatonPaths.QueueFile,
            queue => queue with { Items = [blocked, cancelled, launched, retired, mismatch, legacy, .. overflow] }, Ct);

        await using var glass = await GlassFixture.StartAsync(fixture);
        var status = await glass.StatusAsync();
        var summary = status.GetProperty("recordedTasks");
        Assert.Equal("available", summary.GetProperty("availability").GetString());
        Assert.True(summary.GetProperty("observedAt").TryGetDateTimeOffset(out _));
        var rows = summary.GetProperty("items").EnumerateArray().ToArray();
        Assert.DoesNotContain(rows, row => row.GetProperty("task").GetProperty("id").GetString() == "task-retired");
        Assert.Equal(1, summary.GetProperty("omitted").GetInt32());
        Assert.Equal(20, rows.Length);
        Assert.Equal(new[] { "task-a", "task-b" },
            rows.Take(2).Select(row => row.GetProperty("task").GetProperty("id").GetString()));
        Assert.All(rows, row => Assert.Equal("prior-holder", row.GetProperty("task").GetProperty("conductorHolder").GetString()));
        Assert.Equal("Queued", rows[0].GetProperty("state").GetString());
        Assert.Equal("blocked", rows[0].GetProperty("task").GetProperty("preparation").GetString());
        Assert.Equal("ready-ref", rows[0].GetProperty("task").GetProperty("readyReceiptId").GetString());
        Assert.True(rows[1].GetProperty("cancelled").GetBoolean());
        Assert.Equal("Launched", rows[2].GetProperty("state").GetString());
        Assert.DoesNotContain("private detail", status.ToString());
    }

    [Fact]
    public async Task Glass_malformed_queue_marks_task_summary_unavailable_without_losing_hosted_controls()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await File.WriteAllTextAsync(BatonPaths.QueueFile, "{", Ct);
        await using var glass = await GlassFixture.StartAsync(fixture);

        var status = await glass.StatusAsync();

        Assert.Equal("unavailable", status.GetProperty("recordedTasks").GetProperty("availability").GetString());
        Assert.True(status.GetProperty("stopEligible").GetBoolean());
        Assert.True(status.GetProperty("takeoverEligible").GetBoolean());
    }

    [Fact]
    public async Task Glass_incomplete_chunked_detach_returns_timeout_without_acknowledging_or_mutating()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var before = File.ReadAllBytes(fixture.RegistrationPath);
        Assert.Contains(" 408 ", await glass.SlowDetachStatusLineAsync());
        Assert.Equal(before, File.ReadAllBytes(fixture.RegistrationPath));
    }

    [Fact]
    public async Task Glass_status_read_handle_allows_actual_session_writer_to_replace_evidence()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.CreateAsync();
        var path = Path.Combine(fixture.Root, "held-read.json");
        ConductorFollowSession.WriteAtomic(path, "before");
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, ConductorFollowSession.RetainedReadShare))
        {
            ConductorFollowSession.WriteAtomic(path, "after");
            using var text = new StreamReader(reader);
            Assert.Equal("before", text.ReadToEnd());
            Assert.Equal("after", File.ReadAllText(path));
        }
        // Negative control: without delete sharing the same writer must propagate refusal,
        // preserve the prior bytes, and clean up its uncommitted temporary file.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.ThrowsAny<IOException>(() => ConductorFollowSession.WriteAtomic(path, "refused"));
        Assert.Equal("after", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(fixture.Root, "held-read.json.tmp.*"));
    }

    [Fact]
    public async Task Glass_exact_detach_preserves_evidence_and_refuses_subsequent_launch_and_action()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        await fixture.HaltAsync("saved", notify: false);
        await fixture.Scheduler().ReconcileStoppedWorkAdviceAsync(Ct);
        fixture.Reply = "ReplaceReview";
        await fixture.FollowAsync("saved");
        var evidence = Directory.EnumerateFiles(Path.Combine(fixture.Root, "conductor-follow"), "*", SearchOption.AllDirectories)
            .Where(path => path != fixture.RegistrationPath).ToDictionary(path => path, File.ReadAllBytes);
        await using var glass = await GlassFixture.StartAsync(fixture);
        var status = await glass.StatusAsync();
        Assert.Equal("attached", status.GetProperty("state").GetString());
        Assert.Equal("holder", status.GetProperty("holder").GetString());
        Assert.Equal("gpt-5.6-luna", status.GetProperty("model").GetString());
        Assert.Contains("File reads only", status.GetProperty("permissions").GetString());
        var body = DetachBody(status);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await glass.DetachAsync(body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("not cancelled", await response.Content.ReadAsStringAsync(Ct));
        }
        Assert.Equal("detached", (await glass.StatusAsync()).GetProperty("state").GetString());
        foreach (var file in evidence) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        await fixture.Scheduler().RecoverAttachedFollowAsync(Ct);
        await fixture.HaltAsync("later");
        Assert.Single(fixture.Calls);
        Assert.Null((await fixture.RowAsync("saved")).ReplacementReviewAction);
        Assert.Null((await fixture.RowAsync("later")).ReplacementReviewAction);
        Assert.NotNull((await ConductorClaimStore.GetClaimAsync(Identity, fixture.Root, Ct))!.Holder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("someone-else@example.test")]
    public async Task Glass_denied_detach_never_changes_registration(string? login)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var before = File.ReadAllBytes(fixture.RegistrationPath);
        using var response = await glass.DetachAsync(DetachBody(await glass.StatusAsync()), login);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(fixture.RegistrationPath));
    }

    [Theory]
    [InlineData("claimGeneration")]
    [InlineData("attachmentId")]
    [InlineData("holder")]
    [InlineData("repository")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public async Task Glass_stale_or_malformed_exact_identity_refuses_without_mutation(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = JsonNode.Parse(DetachBody(await glass.StatusAsync()))!.AsObject();
        if (change == "unknown") body["workspace"] = fixture.Workspace;
        else if (change != "duplicate") body[change] = "stale";
        var text = body.ToJsonString();
        if (change == "duplicate") text = text[..^1] + ",\"holder\":\"holder\"}";
        var before = File.ReadAllBytes(fixture.RegistrationPath);
        using var response = await glass.DetachAsync(text);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(fixture.RegistrationPath));
        Assert.DoesNotContain(fixture.Root, await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("missing-attached")]
    [InlineData("generation")]
    [InlineData("claim")]
    [InlineData("session")]
    [InlineData("trust")]
    public async Task Glass_unknown_registration_is_explicit_and_cannot_detach(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var request = DetachBody(await glass.StatusAsync());
        var registration = JsonNode.Parse(File.ReadAllText(fixture.RegistrationPath))!.AsObject();
        if (change == "malformed") File.WriteAllText(fixture.RegistrationPath, "{");
        if (change == "missing-attached")
        {
            registration.Remove("attached");
            File.WriteAllText(fixture.RegistrationPath, registration.ToJsonString());
        }
        if (change == "generation")
        {
            registration["claimGeneration"] = "foreign";
            File.WriteAllText(fixture.RegistrationPath, registration.ToJsonString());
        }
        if (change == "claim") await ConductorClaimStore.TakeoverAsync(Identity, "replacement", "test", fixture.Root, cancellationToken: Ct);
        if (change == "session") File.WriteAllText(Path.Combine(registration["sessionDirectory"]!.GetValue<string>(), "session.json"), "{}");
        if (change == "trust") File.WriteAllText(fixture.CeilingPath, "{}");
        Assert.Equal("unavailable", (await glass.StatusAsync()).GetProperty("state").GetString());
        var before = File.ReadAllBytes(fixture.RegistrationPath);
        using var response = await glass.DetachAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(fixture.RegistrationPath));
    }

    [Fact]
    public async Task Glass_detach_acknowledges_while_turn_is_running_and_retains_its_completed_response()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = DetachBody(await glass.StatusAsync());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Reply = "ReplaceReview";
        fixture.WaitInBroker = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var halt = fixture.HaltAsync("running");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct);
            using var response = await glass.DetachAsync(body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(halt.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await halt;
        Assert.Single(fixture.Calls);
        Assert.NotNull(fixture.Receipt("running"));
        Assert.Null((await fixture.RowAsync("running")).ReplacementReviewAction);
        await fixture.HaltAsync("after-running");
        Assert.Single(fixture.Calls);
    }

    private static string DetachBody(JsonElement status) => JsonSerializer.Serialize(new
    {
        repository = status.GetProperty("repository").GetString(),
        holder = status.GetProperty("holder").GetString(),
        claimGeneration = status.GetProperty("claimGeneration").GetString(),
        attachmentId = status.GetProperty("attachmentId").GetString(),
    });

    private sealed class GlassFixture(GlassHttpService service, HttpClient client) : IAsyncDisposable
    {
        public void DropControlAcknowledgement() => service.DropHostedControlAcknowledgementForTest = true;
        public async Task<string> SlowDetachStatusLineAsync(string verb = "detach")
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, client.BaseAddress!.Port, Ct);
            await using var stream = socket.GetStream();
            var headers = $"POST /conductor/{verb} HTTP/1.1\r\nHost: 127.0.0.1:{client.BaseAddress.Port}\r\n"
                + "Tailscale-User-Login: operator@example.test\r\nTransfer-Encoding: chunked\r\n"
                + "Content-Type: application/json\r\n\r\n1\r\n{\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(headers), Ct);
            using var reader = new StreamReader(stream);
            return await reader.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromMinutes(1), Ct) ?? "";
        }

        public static async Task<GlassFixture> StartAsync(Fixture fixture,
            Func<string, CancellationToken, Task<RepositoryIdentity?>>? resolver = null)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var log = new GlassStartupLog();
            var service = new GlassHttpService(new DaemonSettings
            {
                Glass = new GlassListenerSettings { Listen = true, Port = port, OperatorLogin = "operator@example.test" },
            }, Path.Combine(fixture.Root, "fleet", "projection.json"), null, log,
                conductorRoot: fixture.Root, conductorResolver: resolver ?? ((_, _) => Task.FromResult<RepositoryIdentity?>(Identity)));
            await service.StartAsync(Ct);
            await log.Started.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct);
            Assert.Contains($"http://127.0.0.1:{port}/", service.BoundPrefixes);
            return new(service, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") });
        }

        public async Task<JsonElement> StatusAsync()
            => Assert.Single((await SnapshotAsync()).EnumerateArray()).Clone();

        public async Task<JsonElement> SnapshotAsync()
        {
            var text = await client.GetStringAsync("conductors", Ct);
            Assert.DoesNotContain("initialInstructions", text);
            Assert.DoesNotContain("sessionDirectory", text);
            using var document = JsonDocument.Parse(text);
            Assert.True(document.RootElement.GetProperty("observedAt").TryGetDateTimeOffset(out _));
            return document.RootElement.GetProperty("conductors").Clone();
        }

        public async Task<HttpResponseMessage> DetachAsync(string body, string? login = "operator@example.test")
            => await WriteAsync("detach", body, login);

        public async Task<HttpResponseMessage> ResumeAsync(string body, string? login = "operator@example.test")
            => await WriteAsync("resume", body, login);

        public async Task<HttpResponseMessage> ControlAsync(string verb, string body, string? login = "operator@example.test")
            => await WriteAsync(verb, body, login);

        public bool LoseCorrectionReply { set => service.DropCorrectionAcknowledgementForTest = value; }

        public async Task<HttpResponseMessage> CorrectionReceiptAsync(string requestId, string? login = "operator@example.test")
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "conductor/corrections/receipt?requestId=" + Uri.EscapeDataString(requestId));
            if (login is not null) request.Headers.Add(GlassWriteGate.IdentityHeader, login);
            return await client.SendAsync(request, Ct);
        }

        private async Task<HttpResponseMessage> WriteAsync(string verb, string body, string? login)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "conductor/" + verb)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (login is not null) request.Headers.Add(GlassWriteGate.IdentityHeader, login);
            return await client.SendAsync(request, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await service.StopAsync(Ct);
            service.Dispose();
        }
    }

    private sealed class GlassStartupLog : StringWriter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.StartsWith("GlassHttpService: serving", StringComparison.Ordinal) == true
                || value?.Contains("no address could be bound", StringComparison.Ordinal) == true) Started.TrySetResult();
        }
    }
}
