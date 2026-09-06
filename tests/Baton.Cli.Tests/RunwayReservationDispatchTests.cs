using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Runway;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1896 at the entry point it guards (spec/baton.md §7 is the register for the ruling). These arms pin
/// the wiring: a second concurrent dispatch against one snapshot is held, every evaluation is recorded
/// whichever way it went, a later harvest reconciles, and the policy doing the arithmetic is swappable.
/// </summary>
/// <remarks>
/// <b>Every arm drives an INJECTED policy, never the shipped constants.</b> Retuning
/// <c>LedgerMedianRunwayReservationPolicy</c>'s default — which the operator direction of 2026-09-05
/// explicitly expects to happen once these rows have been read — must not break a test about the
/// arithmetic. The shipped default is pinned separately, in <c>Baton.Tests</c>.
/// </remarks>
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class RunwayReservationDispatchTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["fake"] = new ContractOutputWorkerAdapter(satisfyOutputs: true) };

    /// <summary>The issue's own scenario: a week window at 84 % against the shipped 85 % threshold, so the
    /// gate admits and exactly one percentage point of headroom is left for the reservation arm to spend.</summary>
    private static readonly IReadOnlyList<RunwayCounter> At84Percent =
        [new("week (all models)", 84), new("session", 12)];

    private const double HeadroomPoints = 1.0;

    private readonly IsolatedBatonHome _batonHome = new();
    private readonly IDisposable _catalogScope;

    public RunwayReservationDispatchTests()
    {
        _catalogScope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Current with
        {
            WorkerRolesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"),
            WorkerTiersPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"),
            WorkflowTemplatesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkflowTemplates.json"),
        });
    }

    public void Dispose()
    {
        _catalogScope.Dispose();
        _batonHome.Dispose();
    }

    /// <summary>
    /// A policy with a known estimate, so the arithmetic under test is the store's and not the shipped
    /// constants'. <see cref="FixedReservationPolicy"/> with zero points is the control arm below.
    /// </summary>
    private sealed class FixedReservationPolicy(double points) : IRunwayReservationPolicy
    {
        public string Name => "test-fixed";

        public bool UsesCostLedger => false;

        public RunwayBurnEstimate Estimate(RunwayEstimateContext context) => new(points, "test-fixed");
    }

    [Fact]
    public async Task The_second_dispatch_against_one_snapshot_is_held_once_reservations_exceed_headroom()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"runway-reserve-{Guid.NewGuid():N}");
        try
        {
            var harvestedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            var policy = new FixedReservationPolicy(HeadroomPoints);

            // First: nothing outstanding, one point estimated, one point of headroom -- admitted.
            var first = await DispatchAsync(testRoot, "first", harvestedAt, policy);
            Assert.Equal(WorkflowStatus.Terminal, first.State.Status);

            // Second, against the SAME snapshot: the first dispatch's point is now outstanding, so this
            // one's would take the total past the headroom the counters left.
            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchAsync(testRoot, "second", harvestedAt, policy));

            Assert.Contains("Runway hold", ex.Message, StringComparison.Ordinal);
            Assert.Contains("reserved by dispatches", ex.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(testRoot, "second", "bindings.json")));

            var rows = await ReadLedgerAsync();
            Assert.Equal(RunwayAdmissionDecisions.Admitted, rows[0].Decision);
            Assert.Equal(RunwayAdmissionDecidedBy.Counters, rows[0].DecidedBy);
            Assert.Equal(RunwayAdmissionDecisions.Held, rows[1].Decision);
            Assert.Equal(RunwayAdmissionDecidedBy.Reservation, rows[1].DecidedBy);
            Assert.Equal(HeadroomPoints, rows[1].OutstandingReservationPoints);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// <b>The control arm.</b> The same two back-to-back dispatches against the same snapshot, with a
    /// policy that reserves nothing. Both admit — which is what makes the hold above a result about the
    /// reservation arithmetic rather than about anything else in the gate, the room provisioning, or the
    /// test harness.
    /// </summary>
    [Fact]
    public async Task A_zero_estimate_policy_admits_every_dispatch_against_the_same_snapshot()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"runway-reserve-control-{Guid.NewGuid():N}");
        try
        {
            var harvestedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            var policy = new FixedReservationPolicy(0);

            foreach (var room in new[] { "first", "second", "third" })
            {
                var result = await DispatchAsync(testRoot, room, harvestedAt, policy);
                Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            }

            var rows = await ReadLedgerAsync();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, row => Assert.Equal(RunwayAdmissionDecisions.Admitted, row.Decision));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// The reconciliation rule <c>RunwayAdmissionLedgerStore</c>'s remarks state, driven end to end
    /// through dispatch: a snapshot harvested after the recorded rows leaves nothing outstanding, so the
    /// third dispatch admits where the second was refused.
    /// </summary>
    [Fact]
    public async Task A_later_harvest_reconciles_the_outstanding_reservations_away()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"runway-reserve-harvest-{Guid.NewGuid():N}");
        try
        {
            var policy = new FixedReservationPolicy(HeadroomPoints);
            var firstHarvest = DateTimeOffset.UtcNow.AddMinutes(-1);

            await DispatchAsync(testRoot, "first", firstHarvest, policy);
            await Assert.ThrowsAsync<CliArgumentException>(() => DispatchAsync(testRoot, "second", firstHarvest, policy));

            // A harvest strictly later than both recorded rows. (Its counters still read 84 % here: what
            // this arm pins is the reconciliation rule, not what a real re-read would have found.)
            var result = await DispatchAsync(testRoot, "third", DateTimeOffset.UtcNow.AddMinutes(5), policy);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var rows = await ReadLedgerAsync();
            Assert.Equal(RunwayAdmissionDecisions.Admitted, rows[^1].Decision);
            Assert.Equal(0, rows[^1].OutstandingReservationPoints);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// The recording half, which the operator direction puts first: every evaluation lands as a row
    /// carrying what the gate saw, and the admitted one also lands on the room's own
    /// <c>bindings.json</c> for <c>baton status</c>/<c>fleet_status</c> to read.
    /// </summary>
    [Fact]
    public async Task Every_evaluation_is_recorded_with_the_counters_and_thresholds_it_saw()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"runway-reserve-record-{Guid.NewGuid():N}");
        try
        {
            var harvestedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            var options = await BuildDispatchAsync(testRoot, "only");
            await DispatchCommand.ExecuteAsync(
                options,
                Adapters,
                TestContext.Current.CancellationToken,
                evaluateRunway: vendor => AdmitAt84(vendor, harvestedAt),
                reservationPolicy: new FixedReservationPolicy(0.25));

            var row = Assert.Single(await ReadLedgerAsync());
            Assert.Equal("fake", row.Vendor);
            Assert.Equal(RunwayAdmissionDecisions.Admitted, row.Decision);
            Assert.Equal(RunwayThresholds.DefaultWeekHoldPct, row.WeekHoldPct);
            Assert.Equal(RunwayThresholds.DefaultSessionHoldPct, row.SessionHoldPct);
            Assert.Equal(harvestedAt, row.SnapshotHarvestedAt);
            Assert.Equal(HeadroomPoints, row.HeadroomPoints);
            Assert.Equal(0.25, row.EstimatedBurnPoints);
            Assert.Equal("test-fixed", row.EstimateSource);
            Assert.Equal("advise", row.Role);
            Assert.Contains(row.Counters!, c => c.Window == "week (all models)" && c.PercentUsed == 84);

            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(options.RoomDirectoryPath, "bindings.json"), TestContext.Current.CancellationToken);
            var admission = Assert.IsType<RunwayAdmission>(bindings.Values.Single().RunwayAdmission);
            Assert.Equal(RunwayAdmissionDecisions.Admitted, admission.Decision);
            Assert.Equal(0.25, admission.EstimatedBurnPoints);
            Assert.Contains(admission.Counters!, c => c.Window == "week (all models)" && c.PercentUsed == 84);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1923's guard, in the arm that could make it worse: a vendor with no usage source at all is
    /// admitted as <c>unmeasured</c>, and the reservation arm must never convert that into a hold —
    /// there are no counters to reserve against, so refusing would gate work the counters say nothing
    /// about. Asserted with an estimate far larger than any headroom would be.
    /// </summary>
    [Fact]
    public async Task An_unmeasured_vendor_is_never_held_by_the_reservation_arm()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"runway-reserve-unmeasured-{Guid.NewGuid():N}");
        try
        {
            var policy = new FixedReservationPolicy(1000);

            foreach (var room in new[] { "first", "second" })
            {
                var options = await BuildDispatchAsync(testRoot, room);
                var result = await DispatchCommand.ExecuteAsync(
                    options,
                    Adapters,
                    TestContext.Current.CancellationToken,
                    evaluateRunway: vendor => new RunwayDecision(
                        vendor, RunwayDisposition.Admit, RunwayGate.UnmeasuredReason, []),
                    reservationPolicy: policy);
                Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            }

            var rows = await ReadLedgerAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.Equal(RunwayAdmissionDecisions.Unmeasured, row.Decision));
            Assert.All(rows, row => Assert.Null(row.OutstandingReservationPoints));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// A composed template binds two vendors (<c>implement-review</c> runs its janitor on the cheap tier,
    /// which is <c>agy</c>, and the rest on <c>claude</c>) and the refusal is all-or-nothing. When one of
    /// them holds, the other's row must not read as spend that happened: the dispatch it belongs to never
    /// ran, so it reserves nothing against the <b>next</b> dispatch on that vendor. Both halves are
    /// asserted, and the second is what the control arm below discriminates against.
    /// </summary>
    [Fact]
    public async Task A_vendor_admitted_beside_a_held_sibling_reserves_nothing_against_the_next_dispatch()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"runway-reserve-sibling-{Guid.NewGuid():N}");
        try
        {
            var harvestedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            var policy = new FixedReservationPolicy(HeadroomPoints);

            // A template carries its phases' own instructions, so it takes no --spec.
            Directory.CreateDirectory(testRoot);
            var composed = new DispatchOptions(
                "implement-review", SpecFilePath: null, Path.Combine(testRoot, "composed"));
            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                composed,
                MixedVendorAdapters,
                TestContext.Current.CancellationToken,
                evaluateRunway: vendor => vendor == "agy"
                    ? new RunwayDecision(vendor, RunwayDisposition.Hold, "'week (all models)' is at 91% (holds at 85%)", At84Percent)
                    : AdmitAt84(vendor, harvestedAt),
                reservationPolicy: policy));
            Assert.Contains("Runway hold", refusal.Message, StringComparison.Ordinal);

            var refused = await ReadLedgerAsync();
            var admitted = Assert.Single(refused, row => row.Vendor == "claude");
            Assert.Equal(RunwayAdmissionDecisions.Admitted, admitted.Decision);
            Assert.False(admitted.Dispatched);

            // The next dispatch on claude sees one point of headroom and nothing legitimately outstanding.
            var next = await DispatchAsync(testRoot, "next", harvestedAt, policy, adapter: "claude");
            Assert.Equal(WorkflowStatus.Terminal, next.State.Status);
            Assert.Equal(0, (await ReadLedgerAsync())[^1].OutstandingReservationPoints);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> MixedVendorAdapters =
        new Dictionary<string, IWorkerAdapter>
        {
            ["claude"] = new ContractOutputWorkerAdapter(satisfyOutputs: true),
            ["agy"] = new ContractOutputWorkerAdapter(satisfyOutputs: true),
        };

    private static RunwayDecision AdmitAt84(string vendor, DateTimeOffset harvestedAt) =>
        new(vendor, RunwayDisposition.Admit, Reason: null, At84Percent, HeadroomPoints, harvestedAt);

    private static Task<IReadOnlyList<RunwayAdmissionEntry>> ReadLedgerAsync() =>
        RunwayAdmissionLedgerStore.ReadAllAsync(
            BatonPaths.RunwayAdmissionLedgerFile, TestContext.Current.CancellationToken);

    private static async Task<CommandResult> DispatchAsync(
        string testRoot, string room, DateTimeOffset harvestedAt, IRunwayReservationPolicy policy,
        string? adapter = null) =>
        await DispatchCommand.ExecuteAsync(
            await BuildDispatchAsync(testRoot, room, adapter),
            adapter is null ? Adapters : MixedVendorAdapters,
            TestContext.Current.CancellationToken,
            evaluateRunway: vendor => AdmitAt84(vendor, harvestedAt),
            reservationPolicy: policy);

    private static async Task<DispatchOptions> BuildDispatchAsync(
        string testRoot, string room, string? adapter = null) =>
        new("advise", await WriteSpecAsync(testRoot), Path.Combine(testRoot, room), Adapter: adapter ?? "fake");

    private static async Task<string> WriteSpecAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        var specPath = Path.Combine(testRoot, "spec.md");
        await File.WriteAllTextAsync(specPath, "Weigh the options for X.", TestContext.Current.CancellationToken);
        return specPath;
    }
}
