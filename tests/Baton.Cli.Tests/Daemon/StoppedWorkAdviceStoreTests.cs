using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests.Daemon;

public sealed class StoppedWorkAdviceStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Repository = "github.com/aer-works/baton";
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Complete_stopped_advice_response_is_retained_and_not_launched_again()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_stopped_store_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var store = new ConductorObligationStore(new FleetEventLog(
                BatonPaths.FleetEventsFile, BatonPaths.FleetEventsRolloverFile, 100_000),
                BatonPaths.ConductorObligationsFile, () => Now);
            var key = "stopped-judgment:" + Repository + ":1934-lane:attempt-1:review";
            var context = new StoppedWorkAdviceContext(
                Repository, "1934-lane", new FleetAttemptId("attempt-1"), WorkStage.Review, Now, "head", null,
                "Succeeded", true, "passing", Now, StoppedWorkHaltCause.MissingVerdict,
                "available", false, "passing");
            var digest = StoppedWorkAdviceEvidence.Hash(context);
            var obligation = await store.EnqueueAsync(new ConductorObligationRequest(
                key, Repository, null, "1934-lane", "head", StoppedWorkJudgmentKey.Action,
                "repository-conductor", Now, StoppedWorkJudgmentKey.Adapter,
                StoppedWorkJudgmentKey.Capability, true, TargetRevision: "head",
                ContextSha256: digest), Ct);
            var request = new StoppedWorkAdviceRequest(
                obligation.ObligationId, Repository, "1934-lane", new FleetAttemptId("attempt-1"),
                WorkStage.Review, digest, Now, "head", null, "repository-conductor",
                StoppedWorkHaltCause.MissingVerdict, "available", false, "passing");
            var calls = 0;
            Task<RetainedStoppedWorkAdviceResponse> Launch(
                ConductorObligation _, StoppedWorkAdviceRequest input,
                StoppedWorkAdviceContext adviceContext, string evidence, CancellationToken __)
            {
                calls++;
                Assert.True(Directory.Exists(evidence));
                Assert.Equal(request, input);
                return Task.FromResult(new RetainedStoppedWorkAdviceResponse(
                    new StoppedWorkAdviceDecision(
                        obligation.ObligationId, Repository, "1934-lane", "attempt-1", digest,
                        StoppedWorkAdviceChoice.Hold, "The verdict is incomplete."),
                    StoppedWorkJudgmentKey.Adapter, "gpt-5.6-luna", "low", Now,
                    new StoppedWorkAdviceUsage(null, null, null)));
            }

            var first = await store.DecideStoppedWorkOnceAsync(
                key, request, context, (_, _) => Task.CompletedTask, Launch, Ct);
            var second = await store.DecideStoppedWorkOnceAsync(
                key, request, context, (_, _) => Task.CompletedTask, Launch, Ct);
            Assert.Equal(1, calls);
            Assert.Equal(ConductorObligationStatus.TransportAcknowledged, first.Obligation.Status);
            Assert.Equal(first.Response, second.Response);
            Assert.Equal(StoppedWorkAdviceChoice.Hold, second.Response.Decision.Choice);
            Assert.Null(second.Response.Usage!.InputTokens);
            Assert.Null(second.Response.Usage.OutputTokens);
            Assert.Null(second.Response.Usage.CachedInputTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }
}
