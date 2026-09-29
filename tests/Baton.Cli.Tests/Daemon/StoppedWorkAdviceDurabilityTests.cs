using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests.Daemon;

public sealed class StoppedWorkAdviceDurabilityTests : IDisposable
{
    private const string Repository = "github.com/philipreese/baton";
    private const string Tag = "2499-lane";
    private const string Attempt = "attempt-1";
    private const string PullRequestHead = "0123456789abcdef0123456789abcdef01234567";
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "baton-stopped-advice-durability-" + Guid.NewGuid().ToString("N"));

    public StoppedWorkAdviceDurabilityTests() => Directory.CreateDirectory(_root);

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_root);

    [Fact]
    public async Task Concurrent_callers_admit_one_provider_call()
    {
        var fixture = await CreateFixtureAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task<RetainedStoppedWorkAdviceResponse> Launch(
            ConductorObligation obligation, StoppedWorkAdviceRequest input,
            StoppedWorkAdviceContext context, string evidenceDirectory, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            return CompleteAfterReleaseAsync();
        }

        async Task<RetainedStoppedWorkAdviceResponse> CompleteAfterReleaseAsync()
        {
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            return Response(fixture.Obligation);
        }

        var first = fixture.Store.DecideStoppedWorkOnceAsync(
            fixture.Key, fixture.Request, fixture.Context, PassPreflight, Launch, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var second = NewStore().DecideStoppedWorkOnceAsync(
            fixture.Key, fixture.Request, fixture.Context,
            PassPreflight,
            (_, _, _, _, _) => throw new InvalidOperationException("must not launch twice"), Ct);

        release.SetResult();
        var completed = await first;
        var replay = await second;

        Assert.Equal(1, calls);
        Assert.Equal(completed.Response, replay.Response);
        Assert.Equal(ConductorObligationStatus.TransportAcknowledged, completed.Obligation.Status);
    }

    [Fact]
    public async Task Acknowledged_response_replays_after_restart_and_event_log_rotation()
    {
        var fixture = await CreateFixtureAsync();
        var calls = 0;
        await fixture.Store.DecideStoppedWorkOnceAsync(
            fixture.Key, fixture.Request, fixture.Context, PassPreflight,
            (_, _, _, _, _) =>
            {
                calls++;
                return Task.FromResult(Response(fixture.Obligation));
            }, Ct);

        var rotatingLog = new FleetEventLog(
            fixture.Events, fixture.Rollover, maxLiveBytes: 1);
        await rotatingLog.Append(
            new FleetEventDraft(FleetEventKind.DaemonStarted, "rotation:after-stopped-advice", Now), Ct);
        File.Delete(fixture.Projection);

        var restarted = NewStore();
        var replay = await restarted.DecideStoppedWorkOnceAsync(
            fixture.Key, fixture.Request, fixture.Context,
            (_, _) => throw new InvalidOperationException("must not preflight a retained response"),
            (_, _, _, _, _) =>
                throw new InvalidOperationException("must not launch a retained response"), Ct);

        Assert.Equal(1, calls);
        Assert.Equal(ConductorObligationStatus.TransportAcknowledged, replay.Obligation.Status);
        Assert.Equal(StoppedWorkAdviceChoice.Hold, replay.Response.Decision.Choice);
        Assert.True(File.Exists(fixture.Rollover));
    }

    [Fact]
    public async Task Failure_after_launch_marker_is_uncertain_and_never_retries()
    {
        var fixture = await CreateFixtureAsync();
        var calls = 0;
        await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
            fixture.Store.DecideStoppedWorkOnceAsync(
                fixture.Key, fixture.Request, fixture.Context, PassPreflight,
                (_, _, _, _, _) =>
                {
                    calls++;
                    throw new IOException("provider stopped after launch marker");
                }, Ct));

        var error = await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
            NewStore().DecideStoppedWorkOnceAsync(
                fixture.Key, fixture.Request, fixture.Context, PassPreflight,
                (_, _, _, _, _) =>
                {
                    calls++;
                    throw new InvalidOperationException("must not retry an uncertain launch");
                }, Ct));

        Assert.Contains("uncertain", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, calls);
        Assert.True(File.Exists(Path.Combine(fixture.EvidenceDirectory, "launch.json")));
        Assert.True(File.Exists(Path.Combine(fixture.EvidenceDirectory, "failure.txt")));
    }

    [Fact]
    public async Task Preflight_failure_is_unlaunched_and_does_not_call_provider()
    {
        var fixture = await CreateFixtureAsync();
        var preflightCalls = 0;
        var launchCalls = 0;

        await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
            fixture.Store.DecideStoppedWorkOnceAsync(
                fixture.Key, fixture.Request, fixture.Context,
                (_, _) =>
                {
                    preflightCalls++;
                    throw new ConductorObligationStoreException("source drift before launch");
                },
                (_, _, _, _, _) =>
                {
                    launchCalls++;
                    return Task.FromResult(Response(fixture.Obligation));
                }, Ct));

        Assert.Equal(1, preflightCalls);
        Assert.Equal(0, launchCalls);
        Assert.False(File.Exists(Path.Combine(fixture.EvidenceDirectory, "launch.json")));
    }

    [Theory]
    [InlineData("marker", "{\"obligationId\":\"wrong\",\"contextSha256\":\"bad\",\"startedAt\":\"2026-09-29T12:00:00Z\"}")]
    [InlineData("response", "not-json")]
    [InlineData("receipt", "stopped-work-advice-sha256:wrong\n")]
    public async Task Malformed_or_conflicting_retained_artifacts_refuse_without_launching(
        string artifact, string contents)
    {
        var fixture = await CreateFixtureAsync();
        Directory.CreateDirectory(fixture.EvidenceDirectory);
        var path = Path.Combine(fixture.EvidenceDirectory, artifact switch
        {
            "marker" => "launch.json",
            "response" => "response.json",
            "receipt" => "receipt.json",
            _ => throw new ArgumentOutOfRangeException(nameof(artifact)),
        });
        File.WriteAllText(path, contents);
        if (artifact != "marker")
        {
            File.WriteAllText(Path.Combine(fixture.EvidenceDirectory, "launch.json"),
                $"{{\"obligationId\":\"{fixture.Obligation.ObligationId}\",\"contextSha256\":\"{fixture.Obligation.ContextSha256}\",\"startedAt\":\"{Now:O}\"}}");
        }
        if (artifact == "receipt")
        {
            File.WriteAllText(Path.Combine(fixture.EvidenceDirectory, "response.json"),
                JsonSerializer.Serialize(Response(fixture.Obligation), Json));
        }

        var calls = 0;
        var error = await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
            NewStore().DecideStoppedWorkOnceAsync(
                fixture.Key, fixture.Request, fixture.Context, PassPreflight,
                (_, _, _, _, _) =>
                {
                    calls++;
                    throw new InvalidOperationException("must refuse retained evidence");
                }, Ct));

        Assert.NotEmpty(error.Message);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("attempt")]
    [InlineData("digest")]
    public async Task Response_with_wrong_source_identity_is_rejected(string mismatch)
    {
        var fixture = await CreateFixtureAsync();
        var calls = 0;
        var error = await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
            fixture.Store.DecideStoppedWorkOnceAsync(
                fixture.Key, fixture.Request, fixture.Context, PassPreflight,
                (_, _, _, _, _) =>
                {
                    calls++;
                    var response = Response(fixture.Obligation);
                    var decision = response.Decision with
                    {
                        Tag = mismatch == "tag" ? "other-tag" : response.Decision.Tag,
                        AttemptId = mismatch == "attempt" ? "other-attempt" : response.Decision.AttemptId,
                        ContextSha256 = mismatch == "digest" ? Digest("other-context") : response.Decision.ContextSha256,
                    };
                    return Task.FromResult(response with { Decision = decision });
                }, Ct));

        Assert.NotEmpty(error.Message);
        Assert.Equal(1, calls);
        Assert.False(File.Exists(Path.Combine(fixture.EvidenceDirectory, "response.json")));
    }

    [Theory]
    [InlineData("readiness-decision", "owned-readiness:wrong")]
    [InlineData("continuation", "continuation:wrong")]
    public async Task Readiness_and_continuation_obligations_are_not_accepted(
        string action, string key)
    {
        var request = action == "readiness-decision"
            ? new ConductorObligationRequest(
                key, Repository, null, null, null, action, "owner-one", Now,
                "codex-subscription-cli", "one-shot-readiness", true,
                TargetWorkspace: Path.GetTempPath(), TargetRevision: PullRequestHead,
                ContextSha256: StoppedWorkAdviceEvidence.Hash(FixtureContext()))
            : new ConductorObligationRequest(
                key, Repository, "room-1", Attempt, null, action, StoppedWorkJudgmentKey.Owner, Now,
                StoppedWorkJudgmentKey.Adapter, "continuation", true);
        var store = NewStore();
        await store.EnqueueAsync(request, Ct);
        var calls = 0;

        await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
            store.DecideStoppedWorkOnceAsync(
                key, FixtureRequest(key), FixtureContext(), PassPreflight,
                (_, _, _, _, _) =>
                {
                    calls++;
                    return Task.FromResult(Response(new ConductorObligation(
                        "wrong", key, Repository, "room-1", null, null, action,
                        StoppedWorkJudgmentKey.Owner, Now, StoppedWorkJudgmentKey.Adapter,
                        StoppedWorkJudgmentKey.Capability, true, ConductorObligationStatus.Pending)));
                }, Ct));

        Assert.Equal(0, calls);
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        var store = NewStore();
        var obligation = await store.EnqueueAsync(new ConductorObligationRequest(
            Key, Repository, null, Tag, null, StoppedWorkJudgmentKey.Action,
            StoppedWorkJudgmentKey.Owner, Now, StoppedWorkJudgmentKey.Adapter,
            StoppedWorkJudgmentKey.Capability, true, TargetRevision: PullRequestHead,
            ContextSha256: StoppedWorkAdviceEvidence.Hash(FixtureContext())), Ct);
        var request = FixtureRequest(Key, obligation.ObligationId);
        return new Fixture(store, request, FixtureContext(), obligation, Key,
            Path.Combine(_root, "events.jsonl"), Path.Combine(_root, "events.1.jsonl"),
            Path.Combine(_root, "conductor-obligations.json"),
            Path.Combine(_root, "stopped-work-advice", Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(Key))).ToLowerInvariant()));
    }

    private ConductorObligationStore NewStore() => new(
        new FleetEventLog(Path.Combine(_root, "events.jsonl"), Path.Combine(_root, "events.1.jsonl"), 1_000_000),
        Path.Combine(_root, "conductor-obligations.json"), () => Now);

    private static Task PassPreflight(ConductorObligation _, CancellationToken __) => Task.CompletedTask;

    private static StoppedWorkAdviceRequest FixtureRequest(string key, string obligationId = "obligation-1") => new(
        obligationId, Repository, Tag, new FleetAttemptId(Attempt), WorkStage.Review,
        StoppedWorkAdviceEvidence.Hash(FixtureContext()), Now, PullRequestHead, null, StoppedWorkJudgmentKey.Owner,
        StoppedWorkHaltCause.MissingVerdict, "available", false, "passing");

    private static StoppedWorkAdviceContext FixtureContext() => new(
        Repository, Tag, new FleetAttemptId(Attempt), WorkStage.Review, Now,
        PullRequestHead, null, "Succeeded", true, "passing", Now,
        StoppedWorkHaltCause.MissingVerdict, "available", false, "passing");

    private static RetainedStoppedWorkAdviceResponse Response(ConductorObligation obligation) => new(
        new StoppedWorkAdviceDecision(
            obligation.ObligationId, Repository, Tag, Attempt, obligation.ContextSha256!,
            StoppedWorkAdviceChoice.Hold, "The verdict is incomplete."),
        StoppedWorkJudgmentKey.Adapter, "gpt-5.6-luna", "low", Now,
        new StoppedWorkAdviceUsage(null, null, null));

    private static string Digest(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private const string Key = "stopped-judgment:" + Repository + ":" + Tag + ":" + Attempt + ":review";

    private sealed record Fixture(
        ConductorObligationStore Store,
        StoppedWorkAdviceRequest Request,
        StoppedWorkAdviceContext Context,
        ConductorObligation Obligation,
        string Key,
        string Events,
        string Rollover,
        string Projection,
        string EvidenceDirectory);
}
