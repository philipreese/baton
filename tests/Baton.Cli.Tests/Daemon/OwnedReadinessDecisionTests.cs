using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Baton.Cli;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests.Daemon;

public sealed class OwnedReadinessDecisionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "baton-readiness-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public OwnedReadinessDecisionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_root);

    [Fact]
    public void Parser_accepts_only_the_two_exact_one_shot_shapes()
    {
        Assert.Equal(new ConductorOptions(ConductorVerb.Prepare, RequestFile: "request.json"),
            ConductorOptionsParser.Parse(["prepare", "--request", "request.json"]));
        Assert.Equal(new ConductorOptions(ConductorVerb.Decide, ObligationKey: "owned-readiness:one",
            ContextFile: "context.json"), ConductorOptionsParser.Parse(
            ["decide", "--obligation", "owned-readiness:one", "--context", "context.json"]));
        Assert.Throws<CliArgumentException>(() => ConductorOptionsParser.Parse(
            ["decide", "--obligation", "one"]));
        Assert.Throws<CliArgumentException>(() => ConductorOptionsParser.Parse(
            ["prepare", "--request", "one", "--request", "two"]));
        Assert.Throws<CliArgumentException>(() => ConductorOptionsParser.Parse(
            ["prepare", "--request", "one", "--context", "two"]));
    }

    [Fact]
    public async Task Two_callers_admit_one_launch_and_complete_response_replays_without_action_observation()
    {
        var store = Store();
        await store.EnqueueAsync(Request(), Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var first = store.DecideReadinessOnceAsync(Key, async (item, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            await release.Task;
            return Response(item);
        }, Ct);
        await entered.Task;
        var second = Store().DecideReadinessOnceAsync(Key, (_, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("must not launch");
            }, Ct);
        release.SetResult();
        var completed = await first;
        var serialized = await second;
        Assert.Equal(completed.Response, serialized.Response);
        Assert.Equal(ConductorObligationStatus.TransportAcknowledged, completed.Obligation.Status);
        Assert.Equal(1, calls);
        Assert.Null(completed.Obligation.ActionObservedAt);
        Assert.Null(completed.Obligation.ActionProof);

        var replay = await Store().DecideReadinessOnceAsync(Key, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("must not launch");
        }, Ct);
        Assert.Equal(completed.Response, replay.Response);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Marker_without_response_is_uncertain_and_does_not_retry()
    {
        var store = Store();
        await store.EnqueueAsync(Request(), Ct);
        var calls = 0;
        await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
            store.DecideReadinessOnceAsync(Key, (_, _) =>
            {
                calls++;
                throw new TimeoutException("injected after launch marker");
            }, Ct));
        var error = await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
            Store().DecideReadinessOnceAsync(Key, (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("must not launch");
            }, Ct));
        Assert.Contains("uncertain", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, calls);
        Assert.True(File.Exists(Path.Combine(EvidenceDirectory(), "failure.txt")));
    }

    [Fact]
    public async Task Terminal_transition_waits_for_owned_launch_to_finish()
    {
        var store = Store();
        await store.EnqueueAsync(Request(), Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decide = store.DecideReadinessOnceAsync(Key, async (item, _) =>
        {
            entered.SetResult();
            await release.Task;
            return Response(item);
        }, Ct);
        await entered.Task;
        var block = Store().BlockAsync(Key, "operator held after response", Ct);
        Assert.False(block.IsCompleted);
        release.SetResult();
        var completed = await decide;
        Assert.Equal(ConductorObligationStatus.TransportAcknowledged, completed.Obligation.Status);
        Assert.Equal(ConductorObligationStatus.Blocked, (await block).Status);
    }

    [Fact]
    public async Task Complete_response_before_acknowledgement_replays_after_controller_failure()
    {
        var store = Store();
        await store.EnqueueAsync(Request(), Ct);
        var calls = 0;
        await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
            store.DecideReadinessOnceAsync(Key, (item, _) =>
            {
                calls++;
                File.WriteAllText(Path.Combine(EvidenceDirectory(), "response.json"),
                    JsonSerializer.Serialize(Response(item), Json));
                throw new IOException("injected controller failure before receipt");
            }, Ct));
        var recovered = await Store().DecideReadinessOnceAsync(Key, (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("must not launch");
        }, Ct);
        Assert.Equal(1, calls);
        Assert.Equal(ConductorObligationStatus.TransportAcknowledged, recovered.Obligation.Status);
        Assert.True(File.Exists(Path.Combine(EvidenceDirectory(), "receipt.json")));
        Assert.Null(recovered.Obligation.ActionObservedAt);
    }

    [Fact]
    public async Task Blocked_obligation_never_writes_marker_or_launches()
    {
        var store = Store();
        await store.EnqueueAsync(Request(), Ct);
        await store.BlockAsync(Key, "operator held the request", Ct);
        var calls = 0;
        await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
            Store().DecideReadinessOnceAsync(Key, (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("must not launch");
            }, Ct));
        Assert.Equal(0, calls);
        Assert.False(File.Exists(Path.Combine(EvidenceDirectory(), "launch.json")));
    }

    [Fact]
    public async Task Invalid_response_identity_is_retained_as_uncertain_without_acknowledgement()
    {
        var store = Store();
        await store.EnqueueAsync(Request(), Ct);
        await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
            store.DecideReadinessOnceAsync(Key, (item, _) =>
                Task.FromResult(Response(item) with
                {
                    Decision = Response(item).Decision with { Revision = "bad" },
                }), Ct));
        var retained = await Store().ReadAsync(Key, Ct);
        Assert.Equal(ConductorObligationStatus.Submitted, retained!.Status);
        Assert.Null(retained.TransportReceipt);
        Assert.False(File.Exists(Path.Combine(EvidenceDirectory(), "response.json")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retained_response_without_real_completion_time_cannot_be_receipted_after_restart(bool omit)
    {
        var store = Store();
        await store.EnqueueAsync(Request(), Ct);
        await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
            store.DecideReadinessOnceAsync(Key, (item, _) =>
            {
                var node = JsonNode.Parse(JsonSerializer.Serialize(Response(item), Json))!.AsObject();
                if (omit) Assert.True(node.Remove("completedAt"));
                else node["completedAt"] = JsonValue.Create(DateTimeOffset.MinValue);
                File.WriteAllText(Path.Combine(EvidenceDirectory(), "response.json"), node.ToJsonString(Json));
                throw new IOException("injected controller crash after malformed response");
            }, Ct));

        var error = await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
            Store().DecideReadinessOnceAsync(Key, (_, _) =>
                throw new InvalidOperationException("must not launch"), Ct));
        Assert.Contains("incomplete or invalid", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(EvidenceDirectory(), "receipt.json")));
        Assert.Equal(ConductorObligationStatus.Submitted, (await Store().ReadAsync(Key, Ct))!.Status);
    }

    private string EvidenceDirectory() => Path.Combine(_root, "readiness-decisions",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Key))).ToLowerInvariant());

    private ConductorObligationStore Store() => new(new FleetEventLog(
        Path.Combine(_root, "events.jsonl"), Path.Combine(_root, "events.1.jsonl"), 1_000_000),
        Path.Combine(_root, "conductor-obligations.json"));

    private const string Key = "owned-readiness:test-one";
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static ConductorObligationRequest Request() => new(Key, "github.com/philipreese/baton",
        null, null, null, "readiness-decision", "owner-one", DateTimeOffset.UnixEpoch,
        "codex-subscription-cli", "one-shot-readiness", true,
        TargetWorkspace: Path.GetTempPath(), TargetRevision: Revision, ContextSha256: Digest);

    private static RetainedReadinessResponse Response(ConductorObligation item) => new(
        new ReadinessDecision(item.ObligationId, item.TargetProject, item.TargetRevision!,
            item.ContextSha256!, ReadinessChoice.Hold, "Required checks are not all green."),
        new ReadinessUsage(100, 25, 10), "codex-subscription-cli", "gpt-5.6-luna", "low",
        DateTimeOffset.UnixEpoch);
}
