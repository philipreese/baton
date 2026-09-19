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

    [Fact]
    public async Task Read_repairs_crash_window_before_rotation_and_terminal_guard_prevents_reopen()
    {
        var store = Store();
        await store.EnqueueAsync(Request(), Ct);
        await store.SubmitAsync(
            "obligation-1",
            (_, _) => Task.FromResult(new ConductorTransportResult(true, "receipt-1")),
            Ct);
        var staleProjection = await File.ReadAllTextAsync(_projection, Ct);

        await store.ObserveActionAsync("obligation-1", "execution-1-complete", Ct);
        await File.WriteAllTextAsync(_projection, staleProjection, Ct);

        Assert.Equal(
            ConductorObligationStatus.ActionObserved,
            (await Store().ReadAsync("obligation-1", Ct))!.Status);

        var rotatingLog = Log(maxLiveBytes: 1);
        for (var index = 0; index < 3; index++)
        {
            await rotatingLog.Append(
                new FleetEventDraft(
                    FleetEventKind.DaemonStarted,
                    $"rotation:{index}",
                    DateTimeOffset.Parse("2026-09-18T12:00:00Z").AddMinutes(index)),
                Ct);
        }

        var restarted = Store();
        Assert.Empty(await restarted.ReconcileAsync(Ct));
        var error = await Assert.ThrowsAsync<ConductorObligationStoreException>(
            () => restarted.EnqueueAsync(Request(), Ct));
        Assert.Equal(
            "Conductor obligation 'obligation-1' is retired and cannot be reopened. "
            + "Operator recovery: use a new idempotency key only for a genuinely new obligation.",
            error.Message);
        Assert.DoesNotContain(
            await rotatingLog.ReadRetained(Ct),
            row => row.Kind == FleetEventKind.ConductorObligationActionObserved);
    }

    [Fact]
    public async Task Reconcile_repairs_a_torn_fleet_tail_before_reading()
    {
        var store = Store();
        await store.EnqueueAsync(Request(), Ct);
        await File.AppendAllTextAsync(_events, "{\"id\":2,\"kind\":\"attemptSettled\"", Ct);

        Assert.Single(await Store().ReconcileAsync(Ct));
        Assert.EndsWith("\n", await File.ReadAllTextAsync(_events, Ct), StringComparison.Ordinal);
        Assert.Single(await Log().ReadRetained(Ct));
    }

    [Fact]
    public async Task Malformed_obligation_fact_reports_pinned_operator_recovery()
    {
        await Log().Append(
            new FleetEventDraft(
                FleetEventKind.ConductorObligationPending,
                "conductor-obligation:conductorObligationPending:broken",
                DateTimeOffset.Parse("2026-09-18T12:00:00Z"),
                ObligationId: "broken-id",
                ObligationIdempotencyKey: "broken",
                ObligationTargetProject: "project-a",
                ObligationTargetExecution: "execution-1",
                ObligationRequestedAction: "continue",
                ObligationCreatedAt: DateTimeOffset.Parse("2026-09-18T11:00:00Z"),
                ObligationAdapter: "test-adapter",
                ObligationAdapterCapability: "conductor-submit",
                ObligationAdapterSupported: true),
            Ct);

        var error = await Assert.ThrowsAsync<ConductorObligationStoreException>(
            () => Store().ReconcileAsync(Ct));
        Assert.Equal(
            "Conductor obligation fact 'conductor-obligation:conductorObligationPending:broken' "
            + "has an incomplete payload. Operator recovery: restore or remove the identified "
            + "incomplete conductor-obligation fact, then retry.",
            error.Message);
    }

    [Fact]
    public async Task Malformed_complete_fleet_row_reports_pinned_operator_recovery()
    {
        await File.WriteAllTextAsync(_events, "not-json\n", Ct);

        var error = await Assert.ThrowsAsync<ConductorObligationStoreException>(
            () => Store().ReconcileAsync(Ct));
        Assert.Contains(
            "Operator recovery: restore or remove the identified malformed fleet-event row, then retry.",
            error.Message,
            StringComparison.Ordinal);
        var cause = Assert.IsType<FleetEventLogReadException>(error.InnerException);
        Assert.Equal(_events, cause.FilePath);
        Assert.Equal(1, cause.LineNumber);
    }

    [Fact]
    public async Task Projection_keeps_only_open_rows_and_a_fixed_size_terminal_guard()
    {
        for (var index = 0; index < 12; index++)
        {
            var key = $"obligation-{index}";
            var store = Store();
            await store.EnqueueAsync(Request(key), Ct);
            await store.SubmitAsync(
                key,
                (_, _) => Task.FromResult(new ConductorTransportResult(true, $"receipt-{index}")),
                Ct);
            await store.ObserveActionAsync(key, $"proof-{index}", Ct);
        }

        using var projection = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllTextAsync(_projection, Ct));
        Assert.Empty(projection.RootElement.GetProperty("openObligations").EnumerateArray());
        Assert.Equal(
            131_072,
            Convert.FromBase64String(
                projection.RootElement.GetProperty("terminalKeyFilter").GetString()!).Length);
        Assert.InRange(new FileInfo(_projection).Length, 174_000, 176_000);
    }

    private FleetEventLog Log(long maxLiveBytes = 100_000) => new(_events, _rollover, maxLiveBytes);

    private ConductorObligationStore Store() => new(Log(), _projection, () => DateTimeOffset.Parse("2026-09-18T12:00:00Z"));

    private static ConductorObligationRequest Request(string idempotencyKey = "obligation-1") => new(
        idempotencyKey,
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
