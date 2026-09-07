using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Cli.Mcp;
using Baton.Status;
using Baton.Vendors;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// Covers <see cref="VendorUsageHarvester"/>'s per-tick decision through its internal test seam —
/// which #1869's review found had no caller at all, so the path that seam exists to reach (named on
/// <c>TickOnceAsync</c>'s own null-skip comment) had no red arm. Both halves are faked (the room scan and the vendor
/// source) so no process is spawned and no real room is fabricated; the cadence rules themselves are
/// <see cref="VendorUsageHarvestSchedulerTests"/>'s, not restated here.
/// </summary>
public sealed class VendorUsageHarvesterTests : IDisposable
{
    private readonly string _tempHome;
    private readonly IDisposable _scope;

    public VendorUsageHarvesterTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"baton-harvester-test-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = _tempHome });
    }

    public void Dispose()
    {
        _scope.Dispose();
        if (Directory.Exists(_tempHome))
        {
            DirectoryCleanup.DeleteRecursively(_tempHome);
        }
    }

    /// <summary>Every interval zero, so the very first tick is due to harvest — live lane or not, which
    /// since #1966 is the same answer either way.</summary>
    private static VendorUsageHarvestScheduler AlwaysDueScheduler() =>
        new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, jitterSource: () => 0);

    /// <summary>
    /// Every interval an hour, so no first tick is ever due. #1966 replaced the idle backoff with a
    /// floor cadence, so "no live lane" is no longer a way to make the scheduler say no — a control that
    /// needs the scheduler to refuse has to say so with the intervals instead.
    /// </summary>
    private static VendorUsageHarvestScheduler NeverDueScheduler() =>
        new(
            TimeSpan.FromHours(1),
            TimeSpan.Zero,
            TimeSpan.FromHours(1),
            TimeSpan.Zero,
            TimeSpan.FromHours(1),
            jitterSource: () => 0);

    private sealed class FakeSource(string vendor, VendorUsageSnapshot? result) : IVendorUsageSource
    {
        public string Vendor => vendor;

        public int Reads { get; private set; }

        public Task<VendorUsageSnapshot?> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(result);
        }
    }

    /// <summary>Counts how many sources are inside <c>ReadAsync</c> at once, and how many finished.</summary>
    private sealed class ConcurrencyTracker
    {
        private int _current;

        public int MaxConcurrent { get; private set; }

        public int Completed { get; private set; }

        public void Enter()
        {
            var now = Interlocked.Increment(ref _current);
            lock (this)
            {
                MaxConcurrent = Math.Max(MaxConcurrent, now);
            }
        }

        public void Exit()
        {
            Interlocked.Decrement(ref _current);
            lock (this)
            {
                Completed++;
            }
        }
    }

    /// <summary>
    /// A source that stays inside <c>ReadAsync</c> long enough for a second one to overlap it if the
    /// harvester ever stopped awaiting each in turn. The delay is the whole instrument: a source that
    /// returned synchronously would observe no overlap even under <c>Task.WhenAll</c>.
    /// </summary>
    private sealed class OverlapDetectingSource(string vendor, ConcurrencyTracker tracker) : IVendorUsageSource
    {
        public string Vendor => vendor;

        public async Task<VendorUsageSnapshot?> ReadAsync(CancellationToken cancellationToken)
        {
            tracker.Enter();
            try
            {
                // This delay is not a wait for anything -- it IS the instrument: the window during which
                // a concurrent harvester would be caught with two sources in flight.
                // wait-ok: the instrument itself, never a timeout -- a 60s floor buys the same assertion
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                return FreshSnapshot() with { Vendor = vendor };
            }
            finally
            {
                tracker.Exit();
            }
        }
    }

    private static VendorUsageSnapshot FreshSnapshot() => new(
        "claude",
        new DateTimeOffset(2026, 9, 4, 18, 0, 0, TimeSpan.Zero),
        Caveat: null,
        [new VendorUsageWindow("session", 8, null, "Current session: 8% used")]);

    [Fact]
    public async Task TickOnce_LiveLaneAndASnapshot_PersistsItWhereFleetStatusReadsIt()
    {
        var source = new FakeSource("claude", FreshSnapshot());
        var harvester = new VendorUsageHarvester(
            [source],
            AlwaysDueScheduler(),
            countLiveLanes: _ => Task.FromResult(new Dictionary<string, int>(StringComparer.Ordinal) { ["claude"] = 1 }));

        await harvester.TickOnceAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var path = BatonPaths.VendorUsageSnapshotFile("claude");
        Assert.True(File.Exists(path), $"expected a persisted snapshot at {path}");
        var persisted = JsonSerializer.Deserialize<VendorUsageSnapshot>(
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
        Assert.Equal("claude", persisted.Vendor);
        Assert.Equal(8, Assert.Single(persisted.Windows).PercentUsed);
    }

    [Fact]
    public async Task TickOnce_SourceReturnsNull_LeavesTheLastGoodSnapshotOnDisk()
    {
        // #1869 review, MEDIUM: the arm that had no instrument. What the null-skip in
        // VendorUsageHarvester.TickOnceAsync protects is stated at that skip; this asserts it, down
        // to the previous file being byte-for-byte untouched.
        var path = BatonPaths.VendorUsageSnapshotFile("claude");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lastGood = JsonSerializer.Serialize(FreshSnapshot());
        await File.WriteAllTextAsync(path, lastGood, TestContext.Current.CancellationToken);

        var source = new FakeSource("claude", result: null);
        var harvester = new VendorUsageHarvester(
            [source],
            AlwaysDueScheduler(),
            countLiveLanes: _ => Task.FromResult(new Dictionary<string, int>(StringComparer.Ordinal) { ["claude"] = 1 }));

        await harvester.TickOnceAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(1, source.Reads); // the harvest really was attempted -- not skipped by the scheduler
        Assert.Equal(lastGood, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TickOnce_SchedulerSaysNo_NeverReadsTheSourceAtAll()
    {
        // Control arm for both tests above: when the scheduler says no, the tick stops before any vendor
        // CLI is spawned, so "no file written" there cannot be explained by the harvester simply never
        // running. Before #1966 the refusal was bought with an empty live-lane map (the idle backoff);
        // that no longer refuses anything, so the intervals are what say no now.
        var source = new FakeSource("claude", FreshSnapshot());
        var harvester = new VendorUsageHarvester(
            [source],
            NeverDueScheduler(),
            countLiveLanes: _ => Task.FromResult(new Dictionary<string, int>(StringComparer.Ordinal)));

        await harvester.TickOnceAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(0, source.Reads);
        Assert.False(File.Exists(BatonPaths.VendorUsageSnapshotFile("claude")));
    }

    /// <summary>
    /// #1966's ask 2, at the level that matters to an operator: a vendor with NO live lane still gets a
    /// snapshot written on a tick. The measured failure it closes — no agy lane ran overnight, so no agy
    /// harvest fired, so the morning's first agy dispatch was refused on a 12.2 h-old counter. The
    /// live-lane map is deliberately empty, which before this change was exactly the condition that
    /// stopped the tick (see the control above).
    /// </summary>
    [Fact]
    public async Task TickOnce_NoLiveLaneForTheVendor_StillWritesItsSnapshot()
    {
        var source = new FakeSource("claude", FreshSnapshot());
        var harvester = new VendorUsageHarvester(
            [source],
            AlwaysDueScheduler(),
            countLiveLanes: _ => Task.FromResult(new Dictionary<string, int>(StringComparer.Ordinal)));

        await harvester.TickOnceAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(1, source.Reads);
        Assert.True(
            File.Exists(BatonPaths.VendorUsageSnapshotFile("claude")),
            "a vendor with no live lane must still be harvested on a tick");
    }

    /// <summary>
    /// The boundary trigger's WIRING, which the scheduler's own suite cannot reach: it is handed
    /// boundaries, while this asserts that the harvester reads them off the vendor's persisted snapshot
    /// and hands them over before harvesting — through the production read, since #1966's review deleted
    /// the constructor seam that would have let this bypass it. A <c>ReadLastSnapshot</c> that returned nothing
    /// would leave every scheduler arm green and silently disable the trigger in production. Both
    /// polarities on the one thing that decides it — a reset already past fires, one still ahead does
    /// not — driven under a scheduler that is never due on its own, so a harvest here can only be the
    /// boundary's doing.
    /// <para>
    /// The third arm is the OTHER half of the same read (#1966 review): the snapshot's own
    /// <c>HarvestedAt</c> is handed over beside the boundaries, so a reset the snapshot was already taken
    /// after buys no harvest. Only a production-level arm can see it — the scheduler's own suite is handed
    /// that instant directly, so a tick that passed <c>null</c> for it would leave every scheduler arm
    /// green while a daemon restart spent a <c>/usage</c> call per vendor for nothing.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(-1, -3, 1)] // reset an hour ago, snapshot three hours ago: the window turned over unread
    [InlineData(1, -3, 0)]  // reset an hour out: nothing has turned over yet
    [InlineData(-2, -1, 0)] // reset two hours ago but the snapshot was taken an hour ago: already read
    public async Task TickOnce_ReadsTheResetInstantsOffThePersistedSnapshot(
        int resetHoursFromNow, int harvestedHoursFromNow, int expectedReads)
    {
        var now = DateTimeOffset.UtcNow;
        VendorUsageHarvester.Persist(
            "claude",
            new VendorUsageSnapshot(
                "claude",
                now + TimeSpan.FromHours(harvestedHoursFromNow),
                Caveat: null,
                [
                    new VendorUsageWindow("session", 8, now + TimeSpan.FromHours(resetHoursFromNow), "Current session: 8% used"),
                    // A window whose reset did not parse contributes no boundary rather than a guessed
                    // one -- it must not fire a harvest of its own on the arm that expects none.
                    new VendorUsageWindow("week (all models)", 40, null, "Current week (all models): 40% used"),
                ]));

        var source = new FakeSource("claude", FreshSnapshot());
        var harvester = new VendorUsageHarvester(
            [source],
            NeverDueScheduler(),
            countLiveLanes: _ => Task.FromResult(new Dictionary<string, int>(StringComparer.Ordinal)));

        await harvester.TickOnceAsync(now, TestContext.Current.CancellationToken);

        Assert.Equal(expectedReads, source.Reads);
    }

    /// <summary>
    /// The cost side of harvesting every vendor rather than the live ones: two vendor CLIs must never be
    /// running at once on the operator's machine. Asserted on observed overlap, not on the shape of the
    /// loop — a <c>Task.WhenAll</c> refactor is the edit this exists to catch, and it would leave every
    /// other arm in this file green.
    /// </summary>
    [Fact]
    public async Task TickOnce_TwoVendors_NeverRunsTheirSourcesConcurrently()
    {
        var tracker = new ConcurrencyTracker();
        var harvester = new VendorUsageHarvester(
            [new OverlapDetectingSource("claude", tracker), new OverlapDetectingSource("agy", tracker)],
            AlwaysDueScheduler(),
            countLiveLanes: _ => Task.FromResult(new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["claude"] = 1,
                ["agy"] = 1,
            }));

        await harvester.TickOnceAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        // The discriminator: both sources really did run. A tick that harvested nothing would trivially
        // observe no overlap.
        Assert.Equal(2, tracker.Completed);
        Assert.Equal(1, tracker.MaxConcurrent);
    }

    /// <summary>
    /// #1904. Every arm above drives a snapshot whose windows carry a percentage, which is what EVERY
    /// #1391 source produces — so nothing here had ever pushed a null-percent window through
    /// <see cref="VendorUsageHarvester.Persist"/> and its <c>VendorUsageBurn.Advance</c> call.
    /// <see cref="CodexUsageSource"/> produces exactly that on any machine where the operator has
    /// declared no plan ceiling, which is every machine on day one: if <c>Advance</c> threw on the
    /// null, <c>Persist</c>'s catch (IOException/UnauthorizedAccessException only) would not hold it,
    /// the whole tick would fail, and codex would silently never be persisted at all while every unit
    /// test stayed green. This is the arm that makes that failure visible.
    /// </summary>
    [Fact]
    public async Task TickOnce_DerivedSnapshotWithNoPercentage_PersistsWithItsDerivedLabelAndNoFabricatedRing()
    {
        var derived = new VendorUsageSnapshot(
            "codex",
            new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero),
            CodexUsageSource.DerivedCaveat,
            [new VendorUsageWindow(CodexUsageSource.FiveHourWindowName, PercentUsed: null, ResetsAt: null, "derived: 0 billed tokens")],
            VendorUsageProvenance.Derived);

        var harvester = new VendorUsageHarvester(
            [new FakeSource("codex", derived)],
            AlwaysDueScheduler(),
            countLiveLanes: _ => Task.FromResult(new Dictionary<string, int>(StringComparer.Ordinal) { ["codex"] = 1 }));

        await harvester.TickOnceAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var path = BatonPaths.VendorUsageSnapshotFile("codex");
        Assert.True(File.Exists(path), $"expected a persisted codex snapshot at {path}");
        var persisted = JsonSerializer.Deserialize<PersistedVendorUsage>(
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;

        Assert.Equal(VendorUsageProvenance.Derived, persisted.Source);
        Assert.Null(Assert.Single(persisted.Windows).PercentUsed);
        // A derived snapshot keeps NO ring at all (VendorUsageBurn.Advance's first rule) -- and a null
        // percentage would contribute no sample either way. The alternative, a fabricated 0, would let
        // VendorUsageBurn.Derive publish a burn rate and an ETA built out of invented zeros, in the one
        // place this whole change says "never a number".
        Assert.True(persisted.Rings is null || persisted.Rings.Count == 0);

        // The STRING, on the path glass.html actually compares against. Everything above asserts the
        // enum survived the persisted file's round trip; glass.html tests `v.source === "derived"`
        // against the projection's JSON, so an enum serialized as a number (0/1) or as PascalCase
        // "Derived" would leave every arm above green while the glass silently labelled a derived
        // block as a vendor counter. Spacing included: FleetStatusTool.SerializerOptions is
        // WriteIndented, which is the same options the daemon's projection writer uses.
        var wire = JsonSerializer.Serialize(
            VendorUsageProjectionReader.ReadAll(new Dictionary<string, int>(StringComparer.Ordinal)),
            FleetStatusTool.SerializerOptions);
        Assert.Contains("\"source\": \"derived\"", wire, StringComparison.Ordinal);
    }
}
