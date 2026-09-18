using Baton.Cli.Daemon;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests.Daemon;

public sealed class ConductorObligationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baton-conductor-obligations-{Guid.NewGuid():N}");
    private readonly string _events;
    private readonly string _rollover;
    private readonly string _projection;

    public ConductorObligationStoreTests()
    {
        Directory.CreateDirectory(_root);
        _events = Path.Combine(_root, "events.jsonl");
        _rollover = Path.Combine(_root, "events.1.jsonl");
        _projection = Path.Combine(_root, "conductor-obligations.json");
    }

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_root);

    [Fact]
    public async Task Accepted_transport_receipt_stays_open_until_independent_action_observation()
    {
        var store = Store();
        await store.EnqueueAsync(Request(), Ct);
        var calls = 0;

        var acknowledged = await store.SubmitAsync(
            "obligation-1",
            (_, _) =>
            {
                calls++;
                return Task.FromResult(new ConductorTransportResult(true, "receipt-1"));
            },
            Ct);

        Assert.Equal(ConductorObligationStatus.TransportAcknowledged, acknowledged.Status);
        Assert.Single(await store.ReconcileAsync(Ct));
        Assert.Equal(1, calls);

        var observed = await store.ObserveActionAsync("obligation-1", "execution-1-complete", Ct);
        Assert.Equal(ConductorObligationStatus.ActionObserved, observed.Status);
        Assert.Empty(await store.ReconcileAsync(Ct));
    }

    [Fact]
    public async Task Restart_replays_only_open_obligations_across_submission_phases()
    {
        var store = Store();
        await store.EnqueueAsync(Request(), Ct);

        var restartedBeforeSubmission = Store();
        Assert.Equal(
            ConductorObligationStatus.Pending,
            Assert.Single(await restartedBeforeSubmission.ReconcileAsync(Ct)).Status);

        var calls = 0;
        var crashedAfterReceipt = await Assert.ThrowsAsync<IOException>(() =>
            restartedBeforeSubmission.SubmitAsync(
                "obligation-1",
                (_, _) =>
                {
                    calls++;
                    throw new IOException("crash after external receipt");
                },
                Ct));
        Assert.Equal("crash after external receipt", crashedAfterReceipt.Message);
        Assert.Equal(ConductorObligationStatus.Submitted, (await Store().ReadAsync("obligation-1", Ct))!.Status);

        var restartedAfterReceipt = Store();
        var acknowledged = await restartedAfterReceipt.SubmitAsync(
            "obligation-1",
            (_, _) =>
            {
                calls++;
                return Task.FromResult(new ConductorTransportResult(true, "receipt-1"));
            },
            Ct);
        Assert.Equal(ConductorObligationStatus.TransportAcknowledged, acknowledged.Status);
        Assert.Equal(2, calls);

        var restartedAfterAcknowledgement = Store();
        var callsBeforeReplay = calls;
        var stillAcknowledged = await restartedAfterAcknowledgement.SubmitAsync(
            "obligation-1",
            (_, _) =>
            {
                calls++;
                return Task.FromResult(new ConductorTransportResult(true, "wrong"));
            },
            Ct);
        Assert.Equal(ConductorObligationStatus.TransportAcknowledged, stillAcknowledged.Status);
        Assert.Equal(callsBeforeReplay, calls);
        Assert.Single(await restartedAfterAcknowledgement.ReconcileAsync(Ct));
    }

    [Fact]
    public async Task Identical_duplicate_dedupes_but_changed_target_fails_before_transport()
    {
        var store = Store();
        var first = await store.EnqueueAsync(Request(), Ct);
        var duplicate = await store.EnqueueAsync(Request(), Ct);

        Assert.Equal(first.ObligationId, duplicate.ObligationId);
        await Assert.ThrowsAsync<ConductorObligationConflictException>(() =>
            store.EnqueueAsync(Request() with { TargetExecution = "execution-2" }, Ct));

        var rows = await Log().ReadRetained(Ct);
        Assert.Single(rows);
        Assert.Equal(FleetEventKind.ConductorObligationPending, rows[0].Kind);
    }

    [Fact]
    public async Task Unsupported_adapter_is_terminal_without_transport_or_action_fact()
    {
        var store = Store();
        var unsupported = await store.EnqueueAsync(
            Request() with
            {
                Adapter = "desktop-session",
                AdapterCapability = "session-wake",
                AdapterSupported = false,
                UnsupportedReason = "capability is not measured",
            },
            Ct);
        var called = false;

        var result = await store.SubmitAsync(
            "obligation-1",
            (_, _) =>
            {
                called = true;
                return Task.FromResult(new ConductorTransportResult(true, "must-not-exist"));
            },
            Ct);

        Assert.Equal(ConductorObligationStatus.Unsupported, unsupported.Status);
        Assert.Equal(ConductorObligationStatus.Unsupported, result.Status);
        Assert.False(called);
        Assert.Equal(
            [FleetEventKind.ConductorObligationPending, FleetEventKind.ConductorObligationUnsupported],
            (await Log().ReadRetained(Ct)).Select(row => row.Kind).ToArray());
    }

    private FleetEventLog Log() => new(_events, _rollover, maxLiveBytes: 100_000);

    private ConductorObligationStore Store() => new(Log(), _projection, () => DateTimeOffset.Parse("2026-09-18T12:00:00Z"));

    private static ConductorObligationRequest Request() => new(
        "obligation-1",
        "project-a",
        "room-a",
        "execution-1",
        null,
        "continue",
        "conductor-a",
        DateTimeOffset.Parse("2026-09-18T11:00:00Z"),
        "test-adapter",
        "conductor-submit",
        true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
