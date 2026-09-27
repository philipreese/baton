using Baton;
using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

public sealed class QueueRecoveryInspectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-09-20T12:00:00Z");

    [Fact]
    public void Unresolved_selection_excludes_only_action_observed_and_history_keeps_all_six_statuses()
    {
        var statuses = Enum.GetValues<ConductorObligationStatus>();
        var inspection = new ConductorObligationInspection(
            statuses.Select((status, index) => Obligation($"status-{index}", status)).ToArray(),
            new Dictionary<string, string>(StringComparer.Ordinal));

        var unresolved = QueueRecoveryInspection.Project(QueueSnapshot.Empty, inspection, history: false);
        var history = QueueRecoveryInspection.Project(QueueSnapshot.Empty, inspection, history: true);

        Assert.Equal(statuses.Length - 1, unresolved.Count);
        Assert.DoesNotContain(unresolved, row => row.Obligation!.Status == ConductorObligationStatus.ActionObserved);
        Assert.Equal(statuses.Length, history.Count);
        Assert.Equal(statuses.ToHashSet(), history.Select(row => row.Obligation!.Status).ToHashSet());
    }

    [Fact]
    public void A_transport_receipt_is_not_action_proof_and_saved_head_is_only_an_observation()
    {
        var key = ContinuationKey("lane", "source-attempt", 1);
        var obligation = Obligation(key, ConductorObligationStatus.TransportAcknowledged) with
        {
            TargetProject = "project-a",
            PullRequestHead = "retained-head",
            TransportReceipt = "accepted-receipt",
            ActionProof = null,
        };
        var queue = new QueueSnapshot(
            [Continuation("lane", "source-attempt", "continue-attempt", 1, pullRequest: 481)],
            PullRequestObservations: [new QueuePullRequestObservation(
                "project-a", 481, "open", "saved-head", CreatedAt, CreatedAt, "cached observation")]);

        var row = Assert.Single(QueueRecoveryInspection.Project(queue, Inspect(obligation), history: false));

        Assert.Equal("accepted-receipt", row.Obligation!.TransportReceipt);
        Assert.Null(row.Obligation.ActionProof);
        Assert.Equal(ConductorObligationStatus.TransportAcknowledged, row.Obligation.Status);
        Assert.Equal("saved-head", Assert.Single(row.SavedPullRequestObservations).HeadSha);
        Assert.Contains("queue scheduler reconciliation", row.NextTrigger, StringComparison.Ordinal);
    }

    [Fact]
    public void Foreign_owner_cannot_join_a_canonical_queue_continuation_or_claim_a_known_trigger()
    {
        var key = ContinuationKey("lane", "source-attempt", 1);
        var obligation = Obligation(key, ConductorObligationStatus.Pending) with
        {
            Owner = "another-conductor",
            TargetProject = "project-a",
        };
        var queue = new QueueSnapshot([Continuation("lane", "source-attempt", "continue-attempt", 1)]);

        var row = Assert.Single(QueueRecoveryInspection.Project(queue, Inspect(obligation), history: false));

        Assert.Empty(row.QueueEvidence);
        Assert.Contains("owner-controlled trigger: another-conductor", row.NextTrigger, StringComparison.Ordinal);
        Assert.Contains("unknown to queue scheduler", row.NextTrigger, StringComparison.Ordinal);
    }

    [Fact]
    public void Orphaned_obligation_is_preserved_and_duplicate_related_queue_evidence_is_not_collapsed()
    {
        var orphanKey = ContinuationKey("orphan", "orphan-source", 1);
        var duplicateKey = ContinuationKey("duplicate", "duplicate-source", 2);
        var obligations = new[]
        {
            Obligation(orphanKey, ConductorObligationStatus.Pending) with { TargetProject = "project-a" },
            Obligation(duplicateKey, ConductorObligationStatus.TransportAcknowledged) with { TargetProject = "project-a" },
        };
        var queue = new QueueSnapshot([
            Continuation("duplicate", "duplicate-source", "continue-one", 2),
            Continuation("duplicate", "duplicate-source", "continue-two", 2),
        ]);

        var rows = QueueRecoveryInspection.Project(queue, Inspect(obligations), history: false)
            .ToDictionary(row => row.IdempotencyKey, StringComparer.Ordinal);

        Assert.Contains(orphanKey, rows.Keys);
        Assert.Empty(rows[orphanKey].QueueEvidence);
        Assert.Contains("source queue evidence is missing", rows[orphanKey].NextTrigger, StringComparison.Ordinal);
        Assert.Equal(2, rows[duplicateKey].QueueEvidence.Count(evidence => evidence.Continuation));
        Assert.Contains("ambiguous continuation evidence", rows[duplicateKey].NextTrigger, StringComparison.Ordinal);
    }

    [Fact]
    public void Quarantine_only_key_remains_visible_and_non_authoritative()
    {
        const string key = "damaged-retained-key";
        var inspection = new ConductorObligationInspection(
            [], new Dictionary<string, string>(StringComparer.Ordinal) { [key] = "bad retained row" });

        var row = Assert.Single(QueueRecoveryInspection.Project(QueueSnapshot.Empty, inspection, history: false));

        Assert.Equal(key, row.IdempotencyKey);
        Assert.Equal("bad retained row", row.ObservationError);
        Assert.False(row.Authoritative);
        Assert.Contains("repair quarantined retained evidence", row.NextTrigger, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_tracks_queue_obligation_and_quarantine_changes_but_not_wall_clock_time()
    {
        var snapshot = new QueueSnapshot([BasicItem("lane")]);
        var obligation = Obligation("stable-key", ConductorObligationStatus.Pending);
        var inspection = Inspect(obligation);
        var original = QueueRecoveryInspection.Fingerprint(snapshot, inspection);

        Assert.Equal(original, QueueRecoveryInspection.Fingerprint(snapshot, inspection));
        Assert.NotEqual(original, QueueRecoveryInspection.Fingerprint(snapshot with { Held = true }, inspection));
        Assert.NotEqual(original, QueueRecoveryInspection.Fingerprint(snapshot,
            Inspect(obligation with { Reason = "changed fact" })));
        Assert.NotEqual(original, QueueRecoveryInspection.Fingerprint(snapshot,
            new ConductorObligationInspection([obligation], new Dictionary<string, string> { ["broken"] = "changed error" })));
    }

    [Fact]
    public void Recovery_cursor_is_stable_and_refuses_normal_mode_or_changed_facts()
    {
        var obligations = new[]
        {
            Obligation("first", ConductorObligationStatus.Pending),
            Obligation("second", ConductorObligationStatus.Submitted),
        };
        var snapshot = QueueSnapshot.Empty;
        var inspection = Inspect(obligations);
        var fingerprint = QueueRecoveryInspection.Fingerprint(snapshot, inspection);
        var rows = QueueRecoveryInspection.Project(snapshot, inspection, history: false);
        var first = QueueInspectionProjection.PageItems(rows, fingerprint, active: false,
            includeRetained: false, pageSize: 1, cursor: null, recovery: true);

        Assert.Single(first.Items);
        Assert.NotNull(first.NextCursor);
        var second = QueueInspectionProjection.PageItems(rows, fingerprint, active: false,
            includeRetained: false, pageSize: 1, cursor: first.NextCursor, recovery: true);
        Assert.Equal("second", Assert.Single(second.Items).IdempotencyKey);
        Assert.Equal(first.NextCursor, QueueInspectionProjection.PageItems(rows, fingerprint, active: false,
            includeRetained: false, pageSize: 1, cursor: null, recovery: true).NextCursor);

        Assert.Contains("selection", Assert.Throws<CliArgumentException>(() =>
            QueueInspectionProjection.PageItems(rows, fingerprint, active: false,
                includeRetained: false, pageSize: 1, cursor: first.NextCursor, recovery: false)).Message,
            StringComparison.Ordinal);
        var changedFingerprint = QueueRecoveryInspection.Fingerprint(snapshot, Inspect(obligations[0] with { Reason = "changed" }, obligations[1]));
        Assert.Contains("changed", Assert.Throws<CliArgumentException>(() =>
            QueueInspectionProjection.PageItems(rows, changedFingerprint, active: false,
                includeRetained: false, pageSize: 1, cursor: first.NextCursor, recovery: true)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_commands_are_read_only_and_missing_state_stays_missing()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            foreach (var options in new[]
            {
                new QueueOptions(QueueVerb.List, Recovery: true),
                new QueueOptions(QueueVerb.List, Recovery: true, ListFormat: QueueListOutputFormat.Json),
            })
            {
                Assert.Equal(0, await QueueCommand.ExecuteAsync(options, new StringWriter(), Ct));
            }

            Assert.False(File.Exists(BatonPaths.QueueFile));
            Assert.False(File.Exists(BatonPaths.FleetEventsFile));
            Assert.False(File.Exists(BatonPaths.FleetEventsRolloverFile));
            Assert.False(File.Exists(BatonPaths.ConductorObligationsFile));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Text_and_json_repeated_inspection_preserve_queue_fleet_and_projection_bytes()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var key = ContinuationKey("lane", "source-attempt", 1);
            var secondKey = ContinuationKey("z-lane", "source-attempt-two", 1);
            var store = OperationalStore();
            await store.EnqueueAsync(Request(key), Ct);
            await store.SubmitAsync(key,
                (_, _) => Task.FromResult(new ConductorTransportResult(true, "receipt-only")), Ct);
            await store.EnqueueAsync(Request(secondKey), Ct);
            await store.SubmitAsync(secondKey,
                (_, _) => Task.FromResult(new ConductorTransportResult(true, "receipt-two")), Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile,
                state => state with
                {
                    Items =
                    [
                        Continuation("lane", "source-attempt", "continue-attempt", 1),
                        Continuation("z-lane", "source-attempt-two", "continue-attempt-two", 1),
                    ],
                }, Ct);

            var paths = new[] { BatonPaths.QueueFile, BatonPaths.FleetEventsFile, BatonPaths.ConductorObligationsFile };
            var before = paths.ToDictionary(path => path, path => File.ReadAllBytes(path), StringComparer.Ordinal);
            foreach (var options in new[]
            {
                new QueueOptions(QueueVerb.List, Recovery: true),
                new QueueOptions(QueueVerb.List, Recovery: true),
            })
            {
                var output = new StringWriter();
                Assert.Equal(0, await QueueCommand.ExecuteAsync(options, output, Ct));
                Assert.Contains("receipt-only", output.ToString(), StringComparison.Ordinal);
            }

            var recoveryOptions = new QueueOptions(QueueVerb.List, Recovery: true,
                ListFormat: QueueListOutputFormat.Json, PageSize: 1);
            using var recoveryFirst = await RunCommand(recoveryOptions);
            using var recoveryAgain = await RunCommand(recoveryOptions);
            Assert.Equal("recovery-unresolved", recoveryFirst.RootElement.GetProperty("selection").GetString());
            Assert.Equal(recoveryFirst.RootElement.GetProperty("snapshotFingerprint").GetString(),
                recoveryAgain.RootElement.GetProperty("snapshotFingerprint").GetString());
            Assert.Equal(recoveryFirst.RootElement.GetProperty("nextCursor").GetString(),
                recoveryAgain.RootElement.GetProperty("nextCursor").GetString());
            var serializedObligation = Assert.Single(recoveryFirst.RootElement.GetProperty("items").EnumerateArray())
                .GetProperty("obligation");
            Assert.Equal("receipt-only", serializedObligation.GetProperty("transportReceipt").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, serializedObligation.GetProperty("actionProof").ValueKind);
            Assert.True(recoveryFirst.RootElement.GetProperty("hasMore").GetBoolean());

            using var normalDocument = await RunCommand(new QueueOptions(QueueVerb.List,
                ListFormat: QueueListOutputFormat.Json, PageSize: 1));
            var normalCursor = normalDocument.RootElement.GetProperty("nextCursor").GetString();
            var recoveryCursor = recoveryFirst.RootElement.GetProperty("nextCursor").GetString();
            Assert.NotNull(normalCursor);
            Assert.NotNull(recoveryCursor);
            await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                recoveryOptions with { Cursor = normalCursor }, TextWriter.Null, Ct));
            await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.List, ListFormat: QueueListOutputFormat.Json, PageSize: 1, Cursor: recoveryCursor),
                TextWriter.Null, Ct));

            foreach (var path in paths)
            {
                Assert.Equal(before[path], await File.ReadAllBytesAsync(path, Ct));
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Recovery_command_refuses_corrupt_retained_evidence_without_rewriting_it()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.FleetEventsFile)!);
            var damaged = "{not-json\n"u8.ToArray();
            await File.WriteAllBytesAsync(BatonPaths.FleetEventsFile, damaged, Ct);

            var refusal = await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
                QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List, Recovery: true), TextWriter.Null, Ct));

            Assert.Contains("Operator recovery", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(damaged, await File.ReadAllBytesAsync(BatonPaths.FleetEventsFile, Ct));
            Assert.False(File.Exists(BatonPaths.FleetEventsRolloverFile));
            Assert.False(File.Exists(BatonPaths.ConductorObligationsFile));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    private static ConductorObligation Obligation(string key, ConductorObligationStatus status) => new(
        $"id-{key}", key, "project-a", "room-a", "execution-a", null, "continue",
        ConductorContinuation.Owner, CreatedAt, ConductorContinuation.Adapter,
        ConductorContinuation.AdapterCapability, true, status,
        TransportReceipt: status is ConductorObligationStatus.TransportAcknowledged or ConductorObligationStatus.ActionObserved
            ? $"receipt-{key}" : null,
        ActionProof: status == ConductorObligationStatus.ActionObserved ? $"proof-{key}" : null);

    private static ConductorObligationInspection Inspect(params ConductorObligation[] obligations) =>
        new(obligations, new Dictionary<string, string>(StringComparer.Ordinal));

    private static ConductorObligationRequest Request(string key) => new(
        key, "project-a", "room-a", "execution-a", "retained-head", ConductorContinuation.Action,
        ConductorContinuation.Owner, CreatedAt, ConductorContinuation.Adapter,
        ConductorContinuation.AdapterCapability, AdapterSupported: true);

    private static string ContinuationKey(string tag, string sourceAttempt, int round) =>
        ConductorContinuation.IdempotencyKey(tag, new FleetAttemptId(sourceAttempt), round);

    private static QueueItem Continuation(
        string tag, string sourceAttempt, string continuationAttempt, int round, int? pullRequest = null) =>
        BasicItem(tag) with
        {
            Repository = "project-a",
            AttemptId = new FleetAttemptId(continuationAttempt),
            ParentAttemptId = new FleetAttemptId(sourceAttempt),
            Stage = WorkStage.Continue,
            Round = round,
            PullRequest = pullRequest,
        };

    private static QueueItem BasicItem(string tag) => new()
    {
        Tag = tag,
        Role = "implement",
        Workspace = Path.GetTempPath(),
        SpecFile = "brief.md",
    };

    private static ConductorObligationStore OperationalStore() => new(new FleetEventLog(
        BatonPaths.FleetEventsFile, BatonPaths.FleetEventsRolloverFile, maxLiveBytes: 100_000));

    private static async Task<System.Text.Json.JsonDocument> RunCommand(QueueOptions options)
    {
        var output = new StringWriter();
        Assert.Equal(0, await QueueCommand.ExecuteAsync(options, output, Ct));
        return System.Text.Json.JsonDocument.Parse(output.ToString());
    }

    private static string CreateTempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), $"baton-queue-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        return home;
    }
}
