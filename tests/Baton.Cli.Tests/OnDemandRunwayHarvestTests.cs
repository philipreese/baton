using Baton.Cli.Daemon;
using Baton.Cli.Tests.TestSupport;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1923's bootstrap arm — the hold reads a vendor's counters for itself rather than refusing one
/// nothing has ever read. The measured failure — agy's weekly window had just reset, no agy
/// lane was live, so the daemon's harvester never fired, so every agy dispatch was refused for "no
/// readable usage snapshot" and the first lane of the window could never start.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both polarities of the same condition.</b> A source that returns a snapshot must move the
/// decision from Hold to Admit when its counters are under the thresholds; a source that throws must
/// leave it a Hold and name the failure. One without the other would pass with a gate that ignores
/// the harvest entirely, or with one that admits whenever a harvest was attempted.
/// </para>
/// <para>
/// The doubles here are <see cref="IVendorUsageSource"/> implementations, not vendor CLIs: this suite
/// spawns nothing and spends no subscription usage. The real sources' spawn is what
/// <c>Baton.Architecture.Tests.VendorSpawnGateTests</c> already reviews, and
/// <see cref="The_inline_harvest_adds_no_spawn_site_of_its_own"/> is the local statement that this
/// change reuses that reviewed spawn instead of adding one.
/// </para>
/// </remarks>
public sealed class OnDemandRunwayHarvestTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 16, 0, 0, TimeSpan.Zero);

    private readonly IsolatedBatonHome _home = new();

    public void Dispose() => _home.Dispose();

    /// <summary>agy's own <c>/usage</c> row shape, percent REMAINING, parsed by the real parser so a
    /// rename of the window names this gate keys on cannot pass unnoticed here.</summary>
    private static VendorUsageSnapshot AgySnapshot(int weeklyRemaining, int fiveHourRemaining) =>
        AgySnapshot(weeklyRemaining, fiveHourRemaining, Now);

    /// <summary>The same rows harvested at <paramref name="harvestedAt"/>, for the arms that decide on
    /// the production evaluator's own <c>DateTimeOffset.UtcNow</c> rather than this suite's fixed
    /// clock — a snapshot stamped in the past is stale, and stale holds for its own reason.</summary>
    private static VendorUsageSnapshot AgySnapshot(
        int weeklyRemaining, int fiveHourRemaining, DateTimeOffset harvestedAt) =>
        AgyUsageSlashCommandSource.Parse(
            $"Gemini Models\tWeekly Limit Remaining\t{weeklyRemaining}%\t2026-09-09T19:34:12Z\n"
            + $"Gemini Models\tFive Hour Limit Remaining\t{fiveHourRemaining}%\t2026-09-05T19:34:12Z\n",
            harvestedAt);

    private static RunwayDecision Decide(string vendor, RunwayHarvestAttempt? attempt) =>
        RunwayGate.Evaluate(
            vendor, RunwaySnapshotReader.Read(vendor), new RunwayThresholds(), Now, attempt);

    [Fact]
    public async Task A_gated_vendor_with_a_source_and_no_snapshot_is_harvested_and_then_admitted()
    {
        var source = new StubUsageSource("agy", () => AgySnapshot(weeklyRemaining: 95, fiveHourRemaining: 90));

        // The control the whole issue rests on: before the harvest, this vendor holds.
        Assert.Null(RunwaySnapshotReader.Read("agy"));
        Assert.True(Decide("agy", attempt: null).IsHold);

        var attempt = await OnDemandRunwayHarvest.TryHarvestAsync(
            "agy", snapshotExists: false, [source], TestContext.Current.CancellationToken);

        Assert.NotNull(attempt);
        Assert.Null(attempt.FailureReason);
        Assert.Equal(1, source.Reads);

        // Written through the harvester's own writer, so the gate's own reader can see it.
        Assert.NotNull(RunwaySnapshotReader.Read("agy"));

        var decision = Decide("agy", attempt);
        Assert.Equal(RunwayDisposition.Admit, decision.Disposition);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public async Task A_source_that_throws_still_holds_and_the_refusal_names_the_failure()
    {
        var source = new StubUsageSource("agy", () => throw new InvalidOperationException("agy exited 1"));

        var attempt = await OnDemandRunwayHarvest.TryHarvestAsync(
            "agy", snapshotExists: false, [source], TestContext.Current.CancellationToken);

        Assert.NotNull(attempt);
        Assert.Equal("agy exited 1", attempt.FailureReason);
        Assert.Null(RunwaySnapshotReader.Read("agy"));

        var decision = Decide("agy", attempt);
        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        // The clock in the message is the attempt's own instant, not this suite's fixed Now, so the
        // exact HH:MM is pinned in RunwayGateTests where the attempt is constructed; what matters here
        // is that a failed harvest reaches the refusal as a named failure and not as "never harvested".
        Assert.Contains("harvest attempted at", decision.Reason!, StringComparison.Ordinal);
        Assert.Contains("agy exited 1", decision.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("no readable usage snapshot", decision.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of "attempted and failed": a source returning null — every case
    /// <see cref="IVendorUsageSource.ReadAsync"/>'s own contract folds into that value — writes
    /// nothing and still holds, named as a harvest failure rather than as never having been harvested.
    /// </summary>
    [Fact]
    public async Task A_source_that_produces_nothing_still_holds_and_is_named_as_an_attempt()
    {
        var source = new StubUsageSource("agy", () => null);

        var attempt = await OnDemandRunwayHarvest.TryHarvestAsync(
            "agy", snapshotExists: false, [source], TestContext.Current.CancellationToken);

        Assert.NotNull(attempt);
        Assert.NotNull(attempt.FailureReason);
        Assert.Null(RunwaySnapshotReader.Read("agy"));

        var decision = Decide("agy", attempt);
        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("harvest attempted at", decision.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A vendor with no <see cref="IVendorUsageSource"/> is untouched: nothing is harvested, and the
    /// decision is the unmeasured Admit #1848 already shipped. Two arms in one, because the gate's own
    /// two populations differ — <c>codex</c> HAS a source and is still not gated, so it must not be
    /// harvested either, and a list check keyed on the wrong population would pass the first arm alone.
    /// </summary>
    [Theory]
    [InlineData("fake")]
    [InlineData("codex")]
    public async Task A_vendor_the_counters_do_not_gate_is_not_harvested_at_all(string vendor)
    {
        var source = new StubUsageSource(vendor, () => AgySnapshot(95, 90));

        var attempt = await OnDemandRunwayHarvest.TryHarvestAsync(
            vendor, snapshotExists: false, [source], TestContext.Current.CancellationToken);

        Assert.Null(attempt);
        Assert.Equal(0, source.Reads);
        Assert.Equal(RunwayGate.UnmeasuredReason, Decide(vendor, attempt).Reason);
    }

    /// <summary>A vendor that already has a snapshot pays nothing — the harvest is the cold-start path,
    /// not something on every dispatch.</summary>
    [Fact]
    public async Task A_vendor_that_already_has_a_snapshot_is_not_harvested()
    {
        var source = new StubUsageSource("agy", () => AgySnapshot(95, 90));

        var attempt = await OnDemandRunwayHarvest.TryHarvestAsync(
            "agy", snapshotExists: true, [source], TestContext.Current.CancellationToken);

        Assert.Null(attempt);
        Assert.Equal(0, source.Reads);
    }

    /// <summary>
    /// <b>The composition, not the parts.</b> Every arm above drives <c>TryHarvestAsync</c> and
    /// <c>RunwayGate.Evaluate</c> as two steps this suite joins itself; this one drives the production
    /// evaluator <c>baton dispatch</c> and (through <c>CreateDiskRunwayEvaluatorAsync</c>) the daemon
    /// queue actually call, so deleting the harvest from inside that delegate — the exact edit that
    /// silently restores the pre-#1923 refusal on both paths — turns this red. Both polarities, because
    /// an evaluator that harvests on EVERY dispatch and one that never harvests are each one inverted
    /// boolean away, and one arm alone passes for both.
    /// </summary>
    [Fact]
    public void The_production_evaluator_harvests_before_deciding_when_no_snapshot_exists()
    {
        var calls = new List<(string Vendor, bool SnapshotExists)>();
        Task<RunwayHarvestAttempt?> Harvest(string vendor, bool snapshotExists)
        {
            calls.Add((vendor, snapshotExists));
            VendorUsageHarvester.Persist(
                vendor, AgySnapshot(weeklyRemaining: 95, fiveHourRemaining: 90, DateTimeOffset.UtcNow));
            return Task.FromResult<RunwayHarvestAttempt?>(
                new RunwayHarvestAttempt(DateTimeOffset.Now, FailureReason: null));
        }

        // The control: the same evaluator whose harvest does nothing still holds, so the Admit below is
        // the harvest's doing and not the gate's default for a vendor it has never read.
        var held = DispatchCommand.CreateDiskRunwayEvaluator(
            new DaemonSettings(), (_, _) => Task.FromResult<RunwayHarvestAttempt?>(null))("agy");
        Assert.Equal(RunwayDisposition.Hold, held.Disposition);

        var decision = DispatchCommand.CreateDiskRunwayEvaluator(new DaemonSettings(), Harvest)("agy");

        Assert.Equal(RunwayDisposition.Admit, decision.Disposition);
        Assert.Equal(("agy", false), Assert.Single(calls));

        // The other polarity of the flag the evaluator computes: now that a snapshot is on disk, the
        // harvest is told so, which is what keeps this off the common path.
        calls.Clear();
        Assert.Equal(
            RunwayDisposition.Admit,
            DispatchCommand.CreateDiskRunwayEvaluator(new DaemonSettings(), Harvest)("agy").Disposition);
        Assert.Equal(("agy", true), Assert.Single(calls));
    }

    /// <summary>
    /// The bound's own arm, on the path both gated vendors actually take: their sources kill the CLI and
    /// report the killed run as a null (<c>VendorUsageCommandRun</c> folds <c>BatonCancelException</c>
    /// into one), so "did not finish within Ns" has to be recognised there and not only in the
    /// cancellation catch — which no shipped source can reach. Driven over the internal bound seam, so
    /// it costs milliseconds rather than <see cref="OnDemandRunwayHarvest.Bound"/>.
    /// </summary>
    [Fact]
    public async Task A_source_the_bound_kills_is_named_as_a_timeout_rather_than_as_empty_output()
    {
        var source = new TokenHonouringUsageSource("agy");

        var attempt = await OnDemandRunwayHarvest.TryHarvestAsync(
            "agy",
            snapshotExists: false,
            [source],
            TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken);

        Assert.NotNull(attempt);
        // The seconds figure is the test's own tiny bound, so what is pinned is which of the two null
        // wordings the operator gets — a wedged CLI must not read like one that printed nothing.
        Assert.Contains("did not finish within", attempt.FailureReason!, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "did not produce a readable usage report", attempt.FailureReason!, StringComparison.Ordinal);
        Assert.Null(RunwaySnapshotReader.Read("agy"));
        Assert.Equal(RunwayDisposition.Hold, Decide("agy", attempt).Disposition);
    }

    /// <summary>
    /// #1923 asked for the inline harvest to reuse the spawn sites #1391 already had reviewed rather
    /// than introduce one. <c>VendorSpawnGateTests</c> is the enforcement that no unreviewed site
    /// exists anywhere in <c>src/</c>; this states the specific claim that THIS change did not add an
    /// entry, which that scan cannot say on its own once an entry has been added.
    /// </summary>
    [Fact]
    public void The_inline_harvest_adds_no_spawn_site_of_its_own()
    {
        var root = RepoRoot();
        var harvest = File.ReadAllText(Path.Combine(root, "src", "Baton.Cli", "OnDemandRunwayHarvest.cs"));
        foreach (var marker in new[] { "new BatonTask", "Process.Start", "new ProcessStartInfo" })
        {
            Assert.DoesNotContain(marker, harvest, StringComparison.Ordinal);
        }

        var allowlist = File.ReadAllText(
            Path.Combine(root, "tests", "Baton.Architecture.Tests", "VendorSpawnGateTests.cs"));
        Assert.DoesNotContain("OnDemandRunwayHarvest.cs", allowlist, StringComparison.Ordinal);

        // The spawn the inline harvest DOES cause is each source's own, and those files are on the
        // reviewed list already. Asserted on the sources the production evaluator actually passes.
        foreach (var vendorSource in VendorUsageSources.Default.Where(s => RunwayGate.IsGated(s.Vendor)))
        {
            Assert.Contains($"{vendorSource.GetType().Name}.cs", allowlist, StringComparison.Ordinal);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pixi.toml")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }

    /// <summary>An <see cref="IVendorUsageSource"/> whose one read is scripted, counting its calls so
    /// "was it harvested at all" is asserted rather than inferred from the file system.</summary>
    private sealed class StubUsageSource(string vendor, Func<VendorUsageSnapshot?> read) : IVendorUsageSource
    {
        public string Vendor => vendor;

        public int Reads { get; private set; }

        public Task<VendorUsageSnapshot?> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(read());
        }
    }

    /// <summary>
    /// A source that behaves the way the shipped ones do when the bound fires: it waits for the token,
    /// then reports the killed run as a null rather than by throwing — which is exactly what
    /// <c>VendorUsageCommandRun</c> does after <c>BatonProcessRunner</c> raises
    /// <c>BatonCancelException</c>. A stub that threw instead would drive the cancellation catch, an arm
    /// neither gated vendor can reach, and certify a message no operator would ever see.
    /// </summary>
    private sealed class TokenHonouringUsageSource(string vendor) : IVendorUsageSource
    {
        public string Vendor => vendor;

        public async Task<VendorUsageSnapshot?> ReadAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Swallowed on purpose: see this type's own summary.
            }

            return null;
        }
    }
}
