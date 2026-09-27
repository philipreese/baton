using Baton.Cli;
using Baton.Cli.Daemon;
using Baton.Queue;
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

        var rows = await Log().ReadRetainedRepairingTornTails(Ct);
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
            (await Log().ReadRetainedRepairingTornTails(Ct)).Select(row => row.Kind).ToArray());
    }

    [Fact]
    public async Task Terminal_projection_survives_supporting_fact_rotation_and_preserves_result()
    {
        var store = Store();
        await store.EnqueueAsync(Request(), Ct);
        await store.SubmitAsync(
            "obligation-1",
            (_, _) => Task.FromResult(new ConductorTransportResult(true, "receipt-1")),
            Ct);
        var observed = await store.ObserveActionAsync("obligation-1", "execution-1-complete", Ct);

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
        var retained = await restarted.ReadAsync("obligation-1", Ct);
        Assert.NotNull(retained);
        Assert.Equal(observed, retained);
        Assert.Equal(observed, await restarted.EnqueueAsync(Request(), Ct));
        Assert.DoesNotContain(
            await rotatingLog.ReadRetainedRepairingTornTails(Ct),
            row => row.Kind == FleetEventKind.ConductorObligationActionObserved);

        var inspection = await restarted.InspectAsync(Ct);
        var inspectedTerminal = Assert.Single(inspection.Obligations);
        Assert.Equal(ConductorObligationStatus.ActionObserved, inspectedTerminal.Status);
        Assert.Equal("execution-1-complete", inspectedTerminal.ActionProof);
    }

    [Fact]
    public async Task Inspection_of_missing_state_returns_empty_without_creating_files()
    {
        var inspection = await Store().InspectAsync(Ct);

        Assert.Empty(inspection.Obligations);
        Assert.Empty(inspection.Quarantined);
        Assert.False(File.Exists(_events));
        Assert.False(File.Exists(_rollover));
        Assert.False(File.Exists(_projection));
    }

    [Fact]
    public async Task Inspection_returns_all_statuses_including_projection_only_terminals_after_rotation()
    {
        var store = Store();
        await store.EnqueueAsync(Request("pending"), Ct);
        await store.EnqueueAsync(Request("submitted"), Ct);
        await store.SubmitAsync("submitted", (_, _) => Task.FromResult(new ConductorTransportResult(false)), Ct);
        await store.EnqueueAsync(Request("acknowledged"), Ct);
        await store.SubmitAsync("acknowledged", (_, _) => Task.FromResult(new ConductorTransportResult(true, "receipt")), Ct);
        await store.EnqueueAsync(Request("observed"), Ct);
        await store.ObserveActionAsync("observed", "proof", Ct);
        await store.EnqueueAsync(Request("blocked"), Ct);
        await store.BlockAsync("blocked", "needs owner", Ct);
        await store.EnqueueAsync(Request("unsupported") with
        {
            AdapterSupported = false,
            UnsupportedReason = "not supported",
        }, Ct);

        var rotatingLog = Log(maxLiveBytes: 1);
        for (var index = 0; index < 3; index++)
        {
            await rotatingLog.Append(
                new FleetEventDraft(
                    FleetEventKind.DaemonStarted,
                    $"inspection-rotation:{index}",
                    DateTimeOffset.Parse("2026-09-18T12:00:00Z").AddMinutes(index)),
                Ct);
        }

        var inspection = await Store().InspectAsync(Ct);
        Assert.Equal(
            new Dictionary<string, ConductorObligationStatus>(StringComparer.Ordinal)
            {
                ["pending"] = ConductorObligationStatus.Pending,
                ["submitted"] = ConductorObligationStatus.Submitted,
                ["acknowledged"] = ConductorObligationStatus.TransportAcknowledged,
                ["observed"] = ConductorObligationStatus.ActionObserved,
                ["blocked"] = ConductorObligationStatus.Blocked,
                ["unsupported"] = ConductorObligationStatus.Unsupported,
            },
            inspection.Obligations.ToDictionary(item => item.IdempotencyKey, item => item.Status, StringComparer.Ordinal));
        Assert.Empty(inspection.Quarantined);
        Assert.Contains(await rotatingLog.ReadRetainedRepairingTornTails(Ct), row => row.Kind == FleetEventKind.DaemonStarted);
    }

    [Fact]
    public async Task Inspection_reconstructs_fact_only_state_without_writing_projection()
    {
        await Store().EnqueueAsync(Request(), Ct);
        File.Delete(_projection);

        var inspection = await Store().InspectAsync(Ct);

        Assert.Equal(ConductorObligationStatus.Pending, Assert.Single(inspection.Obligations).Status);
        Assert.False(File.Exists(_projection));
    }

    [Fact]
    public async Task Inspection_preserves_records_owned_by_other_conductors()
    {
        var store = Store();
        await store.EnqueueAsync(Request("owner-a"), Ct);
        await store.EnqueueAsync(Request("owner-b") with { Owner = "conductor-b" }, Ct);

        var inspection = await Store().InspectAsync(Ct);

        Assert.Equal(2, inspection.Obligations.Count);
        Assert.Equal("conductor-a", inspection.Obligations.Single(item => item.IdempotencyKey == "owner-a").Owner);
        Assert.Equal("conductor-b", inspection.Obligations.Single(item => item.IdempotencyKey == "owner-b").Owner);
    }

    [Fact]
    public async Task Inspection_exposes_contradictory_fact_as_keyed_quarantine()
    {
        await Store().EnqueueAsync(Request(), Ct);
        await Log().Append(ObligationDraft(
            FleetEventKind.ConductorObligationPending,
            "conductor-obligation:conductorObligationPending:contradiction",
            Request() with { TargetProject = "different-project" }), Ct);

        var inspection = await Store().InspectAsync(Ct);

        Assert.Contains("obligation-1", inspection.Quarantined.Keys);
        Assert.Contains("malformed", inspection.Quarantined["obligation-1"], StringComparison.Ordinal);
        Assert.Equal("project-a", Assert.Single(inspection.Obligations).TargetProject);
    }

    [Fact]
    public async Task Inspection_refuses_unkeyed_fact_with_source_location_without_writing()
    {
        await Log().Append(
            new FleetEventDraft(
                FleetEventKind.ConductorObligationPending,
                "conductor-obligation:pending:missing-key",
                DateTimeOffset.Parse("2026-09-18T12:00:00Z"),
                ObligationId: "missing-key-id",
                ObligationTargetProject: "project-a",
                ObligationTargetExecution: "execution-1",
                ObligationRequestedAction: "continue",
                ObligationOwner: "conductor-a",
                ObligationCreatedAt: DateTimeOffset.Parse("2026-09-18T11:00:00Z"),
                ObligationAdapter: "test-adapter",
                ObligationAdapterCapability: "conductor-submit",
                ObligationAdapterSupported: true),
            Ct);
        var eventBytes = await File.ReadAllBytesAsync(_events, Ct);

        var error = await Assert.ThrowsAsync<ConductorObligationStoreException>(() => Store().InspectAsync(Ct));

        Assert.Contains("no idempotency key", error.Message, StringComparison.Ordinal);
        Assert.Contains(_events, error.Message, StringComparison.Ordinal);
        Assert.Contains("line 1", error.Message, StringComparison.Ordinal);
        Assert.Equal(eventBytes, await File.ReadAllBytesAsync(_events, Ct));
        Assert.False(File.Exists(_projection));
    }

    [Theory]
    [InlineData("null-entry")]
    [InlineData("duplicate-key")]
    [InlineData("empty-owner")]
    [InlineData("invalid-status")]
    [InlineData("default-created-at")]
    [InlineData("action-observed-missing-proof")]
    [InlineData("terminal-in-open-partition")]
    public async Task Inspection_refuses_malformed_projection_without_changing_any_store_bytes(string shape)
    {
        var store = Store();
        await store.EnqueueAsync(Request(), Ct);
        var terminalCase = shape is "action-observed-missing-proof" or "terminal-in-open-partition";
        if (terminalCase)
        {
            await store.ObserveActionAsync("obligation-1", "execution-1-complete", Ct);
        }

        var projection = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(_projection, Ct))!;
        var open = projection["openObligations"]!.AsArray();
        var terminal = projection["terminalObligations"]!.AsArray();
        switch (shape)
        {
            case "null-entry":
                open[0] = null;
                break;
            case "duplicate-key":
                open.Add(open[0]!.DeepClone());
                break;
            case "empty-owner":
                open[0]!["owner"] = string.Empty;
                break;
            case "invalid-status":
                open[0]!["status"] = 999;
                break;
            case "default-created-at":
                open[0]!["createdAt"] = DateTimeOffset.MinValue.ToString("O");
                break;
            case "action-observed-missing-proof":
                terminal[0]!["actionProof"] = null;
                break;
            case "terminal-in-open-partition":
                open.Add(terminal[0]!.DeepClone());
                terminal.RemoveAt(0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown projection fixture shape.");
        }

        await File.WriteAllTextAsync(_projection, projection.ToJsonString(), Ct);
        var projectionBytes = await File.ReadAllBytesAsync(_projection, Ct);
        var eventsExisted = File.Exists(_events);
        var eventsBytes = eventsExisted ? await File.ReadAllBytesAsync(_events, Ct) : [];
        var rolloverExisted = File.Exists(_rollover);
        var rolloverBytes = rolloverExisted ? await File.ReadAllBytesAsync(_rollover, Ct) : [];

        var error = await Assert.ThrowsAsync<ConductorObligationStoreException>(() => Store().InspectAsync(Ct));

        Assert.Contains(_projection, error.Message, StringComparison.Ordinal);
        Assert.Equal(projectionBytes, await File.ReadAllBytesAsync(_projection, Ct));
        Assert.Equal(eventsExisted, File.Exists(_events));
        if (eventsExisted)
        {
            Assert.Equal(eventsBytes, await File.ReadAllBytesAsync(_events, Ct));
        }
        Assert.Equal(rolloverExisted, File.Exists(_rollover));
        if (rolloverExisted)
        {
            Assert.Equal(rolloverBytes, await File.ReadAllBytesAsync(_rollover, Ct));
        }
    }

    [Theory]
    [InlineData("missing-action-proof")]
    [InlineData("default-created-at")]
    [InlineData("missing-target")]
    public async Task Inspection_quarantines_malformed_fact_only_rows_and_projection_keeps_error_visible(string shape)
    {
        var store = Store();
        await store.EnqueueAsync(Request(), Ct);
        await store.ObserveActionAsync("obligation-1", "execution-1-complete", Ct);

        var rows = (await File.ReadAllLinesAsync(_events, Ct))
            .Select(line => System.Text.Json.Nodes.JsonNode.Parse(line)!)
            .ToArray();
        switch (shape)
        {
            case "missing-action-proof":
                Assert.Contains(rows, row => row["obligationActionProof"]?.GetValue<string>() == "execution-1-complete");
                rows.Single(row => row["obligationActionProof"]?.GetValue<string>() == "execution-1-complete")
                    ["obligationActionProof"] = null;
                break;
            case "default-created-at":
                foreach (var row in rows)
                {
                    row["obligationCreatedAt"] = DateTimeOffset.MinValue.ToString("O");
                }
                break;
            case "missing-target":
                foreach (var row in rows)
                {
                    row["obligationTargetRoom"] = null;
                    row["obligationTargetExecution"] = null;
                    row["obligationPullRequestHead"] = null;
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown malformed fact fixture shape.");
        }

        var rewrittenFacts = string.Join('\n', rows.Select(row => row.ToJsonString())) + "\n";
        await File.WriteAllTextAsync(_events, rewrittenFacts, Ct);
        File.Delete(_projection);
        var eventBytes = await File.ReadAllBytesAsync(_events, Ct);
        var rolloverExisted = File.Exists(_rollover);
        var rolloverBytes = rolloverExisted ? await File.ReadAllBytesAsync(_rollover, Ct) : [];

        var inspection = await Store().InspectAsync(Ct);

        Assert.Contains("obligation-1", inspection.Quarantined.Keys);
        Assert.Contains(_events, inspection.Quarantined["obligation-1"], StringComparison.Ordinal);
        Assert.Contains("line ", inspection.Quarantined["obligation-1"], StringComparison.Ordinal);
        var recoveryRow = Assert.Single(QueueRecoveryInspection.Project(QueueSnapshot.Empty, inspection, history: false));
        Assert.NotNull(recoveryRow.ObservationError);
        Assert.False(recoveryRow.Authoritative);
        Assert.Contains(_events, recoveryRow.ObservationError, StringComparison.Ordinal);
        Assert.Equal(eventBytes, await File.ReadAllBytesAsync(_events, Ct));
        Assert.Equal(rolloverExisted, File.Exists(_rollover));
        if (rolloverExisted)
        {
            Assert.Equal(rolloverBytes, await File.ReadAllBytesAsync(_rollover, Ct));
        }
        Assert.False(File.Exists(_projection));
    }

    [Theory]
    [InlineData("projection")]
    [InlineData("live")]
    [InlineData("rollover")]
    public async Task Inspection_refuses_directory_at_evidence_path_instead_of_returning_empty(string segment)
    {
        var path = segment switch
        {
            "projection" => _projection,
            "live" => _events,
            "rollover" => _rollover,
            _ => throw new ArgumentOutOfRangeException(nameof(segment), segment, "Unknown evidence segment."),
        };
        Directory.CreateDirectory(path);

        var error = await Assert.ThrowsAsync<ConductorObligationStoreException>(() => Store().InspectAsync(Ct));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(path));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Inspection_refuses_corrupt_or_torn_rows_in_either_segment_without_changing_bytes(
        bool rollover,
        bool torn)
    {
        await Store().EnqueueAsync(Request(), Ct);
        var path = rollover ? _rollover : _events;
        var original = torn ? "{\"id\":2,\"kind\":\"attemptSettled\"" : "not-json\n";
        await File.WriteAllTextAsync(path, original, Ct);
        var projectionBefore = await File.ReadAllTextAsync(_projection, Ct);

        var error = await Assert.ThrowsAsync<ConductorObligationStoreException>(() => Store().InspectAsync(Ct));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
        if (torn)
        {
            Assert.Contains("byte offset 0", error.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.IsType<FleetEventLogReadException>(error.InnerException);
        }
        Assert.Equal(original, await File.ReadAllTextAsync(path, Ct));
        Assert.Equal(projectionBefore, await File.ReadAllTextAsync(_projection, Ct));
    }

    [Fact]
    public async Task Repeated_inspection_leaves_projection_and_retained_obligation_facts_unchanged()
    {
        await Store().EnqueueAsync(Request(), Ct);
        var eventBytes = await File.ReadAllBytesAsync(_events, Ct);
        var projectionBytes = await File.ReadAllBytesAsync(_projection, Ct);

        await Store().InspectAsync(Ct);
        await Store().InspectAsync(Ct);

        Assert.Equal(eventBytes, await File.ReadAllBytesAsync(_events, Ct));
        Assert.Equal(projectionBytes, await File.ReadAllBytesAsync(_projection, Ct));
    }

    [Fact]
    public async Task Reconcile_repairs_a_torn_fleet_tail_before_reading()
    {
        var store = Store();
        await store.EnqueueAsync(Request(), Ct);
        await File.AppendAllTextAsync(_events, "{\"id\":2,\"kind\":\"attemptSettled\"", Ct);

        Assert.Single(await Store().ReconcileAsync(Ct));
        Assert.EndsWith("\n", await File.ReadAllTextAsync(_events, Ct), StringComparison.Ordinal);
        Assert.Single(await Log().ReadRetainedRepairingTornTails(Ct));
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

        var valid = Store();
        await valid.EnqueueAsync(Request("valid"), Ct);

        Assert.Equal("valid", Assert.Single(await valid.ReconcileAsync(Ct)).IdempotencyKey);
        var error = await Assert.ThrowsAsync<ConductorObligationStoreException>(
            () => Store().ReadAsync("broken", Ct));
        Assert.Contains("conductor-obligation:conductorObligationPending:broken", error.Message);
        Assert.Contains(_events, error.Message, StringComparison.Ordinal);
        Assert.Contains("line 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_complete_fleet_row_reports_pinned_operator_recovery()
    {
        await File.WriteAllTextAsync(_events, "not-json\n", Ct);

        var error = await Assert.ThrowsAsync<ConductorObligationStoreException>(
            () => Store().ReconcileAsync(Ct));
        Assert.Contains(
            "Operator recovery: restore or quarantine the identified malformed fleet-event row, then retry; "
            + "never delete the only surviving authoritative projection.",
            error.Message,
            StringComparison.Ordinal);
        var cause = Assert.IsType<FleetEventLogReadException>(error.InnerException);
        Assert.Equal(_events, cause.FilePath);
        Assert.Equal(1, cause.LineNumber);
    }

    [Fact]
    public async Task Projection_keeps_exact_terminal_rows_with_their_completion_evidence()
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
        var terminals = projection.RootElement.GetProperty("terminalObligations").EnumerateArray().ToArray();
        Assert.Equal(12, terminals.Length);
        Assert.All(terminals, terminal =>
        {
            Assert.Equal("actionObserved", terminal.GetProperty("status").GetString());
            Assert.Equal("receipt-" + terminal.GetProperty("idempotencyKey").GetString()!["obligation-".Length..],
                terminal.GetProperty("transportReceipt").GetString());
            Assert.StartsWith("proof-", terminal.GetProperty("actionProof").GetString(), StringComparison.Ordinal);
            Assert.True(terminal.TryGetProperty("actionObservedAt", out _));
        });
    }

    private FleetEventLog Log(long maxLiveBytes = 100_000) => new(_events, _rollover, maxLiveBytes);

    private static FleetEventDraft ObligationDraft(
        FleetEventKind kind,
        string dedupeKey,
        ConductorObligationRequest request) => new(
            kind,
            dedupeKey,
            DateTimeOffset.Parse("2026-09-18T12:00:00Z"),
            ObligationId: $"id-{request.IdempotencyKey}",
            ObligationIdempotencyKey: request.IdempotencyKey,
            ObligationTargetProject: request.TargetProject,
            ObligationTargetRoom: request.TargetRoom,
            ObligationTargetExecution: request.TargetExecution,
            ObligationPullRequestHead: request.PullRequestHead,
            ObligationRequestedAction: request.RequestedAction,
            ObligationOwner: request.Owner,
            ObligationCreatedAt: request.CreatedAt,
            ObligationAdapter: request.Adapter,
            ObligationAdapterCapability: request.AdapterCapability,
            ObligationAdapterSupported: request.AdapterSupported);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Advancing_clock_preserves_exact_transition_timestamps_and_terminal_replay(bool block)
    {
        var instant = DateTimeOffset.Parse("2026-09-18T12:00:00Z");
        DateTimeOffset Now() => instant = instant.AddTicks(1);
        var store = new ConductorObligationStore(Log(), _projection, Now);
        await store.EnqueueAsync(Request(), Ct);
        await store.SubmitAsync(
            "obligation-1",
            (_, _) => Task.FromResult(new ConductorTransportResult(true, "receipt-1")),
            Ct);

        var terminal = block
            ? await store.BlockAsync("obligation-1", "receiver unavailable", Ct)
            : await store.ObserveActionAsync("obligation-1", "execution-1-complete", Ct);
        var restarted = new ConductorObligationStore(Log(), _projection, Now);
        Assert.Equal(terminal, await restarted.ReadAsync("obligation-1", Ct));
        Assert.Empty(await restarted.ReconcileAsync(Ct));
        Assert.Equal(terminal, block
            ? await restarted.BlockAsync("obligation-1", "receiver unavailable", Ct)
            : await restarted.ObserveActionAsync("obligation-1", "execution-1-complete", Ct));
        Assert.Equal(terminal, await restarted.SubmitAsync(
            "obligation-1", (_, _) => throw new InvalidOperationException("completed transport must not repeat"), Ct));
        await Assert.ThrowsAsync<ConductorObligationConflictException>(() => block
            ? restarted.BlockAsync("obligation-1", "different reason", Ct)
            : restarted.ObserveActionAsync("obligation-1", "different proof", Ct));

        using var projection = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(_projection, Ct));
        var saved = Assert.Single(projection.RootElement.GetProperty("terminalObligations").EnumerateArray());
        var rows = await Log().ReadRetainedRepairingTornTails(Ct);
        Assert.Equal(terminal.SubmittedAt, saved.GetProperty("submittedAt").GetDateTimeOffset());
        Assert.Equal(terminal.TransportAcknowledgedAt, saved.GetProperty("transportAcknowledgedAt").GetDateTimeOffset());
        Assert.Equal(terminal.SubmittedAt, Assert.Single(rows, row => row.Kind == FleetEventKind.ConductorObligationSubmitted).At);
        Assert.Equal(terminal.TransportAcknowledgedAt,
            Assert.Single(rows, row => row.Kind == FleetEventKind.ConductorObligationTransportAcknowledged).At);
        if (!block)
        {
            Assert.Equal(terminal.ActionObservedAt, saved.GetProperty("actionObservedAt").GetDateTimeOffset());
            Assert.Equal(terminal.ActionObservedAt,
                Assert.Single(rows, row => row.Kind == FleetEventKind.ConductorObligationActionObserved).At);
        }
    }

    [Theory]
    [InlineData("actionObservedAt", "2026-09-18T12:00:00.0000001+00:00")]
    [InlineData("actionProof", "different proof")]
    public async Task Conflicting_persisted_terminal_evidence_fails_closed_without_rewriting(string property, string value)
    {
        var store = Store();
        await store.EnqueueAsync(Request(), Ct);
        await store.ObserveActionAsync("obligation-1", "execution-1-complete", Ct);
        var projection = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(_projection, Ct))!;
        projection["terminalObligations"]![0]![property] = value;
        var conflicting = projection.ToJsonString();
        await File.WriteAllTextAsync(_projection, conflicting, Ct);

        var error = await Assert.ThrowsAsync<ConductorObligationStoreException>(() => Store().ReadAsync("obligation-1", Ct));
        Assert.Contains("conflicting terminal results", error.Message, StringComparison.Ordinal);
        Assert.Equal(conflicting, await File.ReadAllTextAsync(_projection, Ct));
    }

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
