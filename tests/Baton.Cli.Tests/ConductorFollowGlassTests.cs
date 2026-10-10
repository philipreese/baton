using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Vendors;

namespace Baton.Cli.Tests;

public sealed partial class ConductorFollowDeliveryTests
{
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
