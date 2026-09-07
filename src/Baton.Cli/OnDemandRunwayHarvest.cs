using Baton.Cli.Daemon;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// #1923: the runway hold's own harvest, run once inline for a vendor whose counters decide its
/// admission and whose snapshot cannot decide it — absent, or (since #1966) stale. <b>The bootstrap
/// this closes:</b> the daemon's <see cref="VendorUsageHarvester"/> used to harvest a vendor only while
/// one of its lanes was live (or just after one exited), so a vendor with no lane running never got a
/// snapshot, and the hold read "no snapshot" as halted — the first agy lane of a window could not start
/// because no agy lane was running. Measured 2026-09-05 and ruled in spec/baton.md §7, which is the
/// register for the contract. #1966 gave that harvester a live-lane-independent cadence
/// (<see cref="VendorUsageHarvester.IdleInterval"/>), so this path is now the exception it was always
/// meant to be rather than the only thing that harvests an idle vendor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fails closed, always.</b> Nothing here can turn a Hold into an Admit by failing: a source that
/// throws, times out, or produces nothing leaves the snapshot absent, and
/// <see cref="RunwayGate.Evaluate"/> holds on an absent snapshot exactly as it did before. What the
/// returned <see cref="RunwayHarvestAttempt"/> buys is the refusal's wording, so "never harvested" and
/// "harvested and it failed" stop being one message.
/// </para>
/// <para>
/// <b>Spends at most one <c>/usage</c> call per gated vendor per dispatch</b>, and only when that
/// vendor's snapshot cannot decide the admission — absent, or (since #1966) older than
/// <see cref="RunwayThresholds.EffectiveMaxSnapshotAge"/> — spec/baton.md §7 states that bound and what
/// the common path therefore costs. #1961 confined this to absent on the
/// reasoning that the daemon's cadence already refreshed a stale one; #1966 measured that it did not —
/// that cadence only fired while a lane of the same vendor was live, so an idle vendor's snapshot aged
/// past the limit and every dispatch was refused on a counter no harvest was going to replace. The
/// daemon tick now harvests regardless of live lanes (spec/baton.md §7), which is what keeps this arm
/// the exception rather than the rule.
/// </para>
/// <para>
/// <b>No new spawn site.</b> The vendor CLI is started by the <see cref="IVendorUsageSource"/>
/// implementations, which are already on <c>VendorSpawnGateTests.ApprovedSpawnSites</c> for #1391;
/// this type only calls <see cref="IVendorUsageSource.ReadAsync"/> and writes the result through the
/// harvester's own <see cref="VendorUsageHarvester.Persist"/>, so there is one on-disk writer, not a
/// second one for the gate.
/// </para>
/// </remarks>
public static class OnDemandRunwayHarvest
{
    /// <summary>
    /// The bound on one inline harvest. Deliberately far below each source's own command timeout
    /// (45 s) — this runs in front of an operator waiting on <c>baton dispatch</c>, and a vendor CLI
    /// that is wedged must cost a refusal quickly rather than a 45-second stall. Exceeding it is a
    /// harvest failure like any other, and holds.
    /// </summary>
    /// <remarks>
    /// <b>Two ways a source reports that the bound fired</b>, and both reach the operator with the same
    /// wording. The shipped sources kill the CLI on cancellation and fold the result into a null —
    /// <c>BatonProcessRunner</c> throws <c>BatonCancelException</c>, which is a <c>BatonException</c>
    /// and not an <see cref="OperationCanceledException"/>, and
    /// <c>VendorUsageCommandRun.CaptureStdoutOrNullAsync</c> catches it and returns null — so the null
    /// path below, not the cancellation catch, is the arm both gated vendors actually take. A source
    /// that instead honours the token by throwing, which <see cref="IVendorUsageSource.ReadAsync"/>'s
    /// contract equally permits, lands in the catch. Neither arm may be dropped: without the catch a
    /// bound-fired cancellation would escape into the caller's blocking wait and crash the dispatch
    /// rather than holding it.
    /// </remarks>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Harvests <paramref name="vendor"/> once and persists what it read, or returns null when no
    /// harvest was attempted — the vendor is not gated (its decision cannot turn on counters), no
    /// source ships for it, or a usable snapshot already exists. Null is the caller's signal to leave
    /// the refusal's existing wording alone.
    /// </summary>
    /// <param name="snapshotUsable">
    /// Whether the vendor already has a persisted snapshot this gate can decide on —
    /// <see cref="RunwayGate.IsUsable"/>, which is present AND within the staleness limit. Passed in
    /// rather than re-read here so the caller reads the file exactly once per decision, and expressed as
    /// "usable" rather than "exists" since #1966: a stale snapshot is not evidence, so it buys the same
    /// harvest an absent one does.
    /// </param>
    public static Task<RunwayHarvestAttempt?> TryHarvestAsync(
        string vendor,
        bool snapshotUsable,
        IReadOnlyList<IVendorUsageSource> sources,
        CancellationToken cancellationToken) =>
        TryHarvestAsync(vendor, snapshotUsable, sources, Bound, cancellationToken);

    /// <summary>
    /// Test-only seam (Baton.Cli.Tests, via <c>InternalsVisibleTo</c>): the same harvest over a
    /// <paramref name="bound"/> a test can make small, so the bound-fired arms are driven in
    /// milliseconds rather than by waiting out <see cref="Bound"/>. Production always passes
    /// <see cref="Bound"/>, which is the value spec/baton.md §7 states.
    /// </summary>
    internal static async Task<RunwayHarvestAttempt?> TryHarvestAsync(
        string vendor,
        bool snapshotUsable,
        IReadOnlyList<IVendorUsageSource> sources,
        TimeSpan bound,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(vendor);
        ArgumentNullException.ThrowIfNull(sources);

        if (snapshotUsable || !RunwayGate.IsGated(vendor))
        {
            return null;
        }

        var source = sources.FirstOrDefault(s => string.Equals(s.Vendor, vendor, StringComparison.Ordinal));
        if (source is null)
        {
            return null;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(bound);

        try
        {
            var snapshot = await source.ReadAsync(bounded.Token).ConfigureAwait(false);
            if (snapshot is null)
            {
                // Null is IVendorUsageSource.ReadAsync's contract for not spawned, non-zero exit, no
                // output at all -- AND, for the shipped sources, a run this bound killed (see Bound's
                // own remarks). The token is what tells those apart: the source has already written
                // the specifics to stderr, and what the refusal needs is which of the two it was.
                return new RunwayHarvestAttempt(
                    DateTimeOffset.Now,
                    bounded.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                        ? TimedOutReason(vendor, bound)
                        : $"the '{vendor}' CLI did not produce a readable usage report");
            }

            VendorUsageHarvester.Persist(vendor, snapshot);
            return new RunwayHarvestAttempt(DateTimeOffset.Now, FailureReason: null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RunwayHarvestAttempt(DateTimeOffset.Now, TimedOutReason(vendor, bound));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately broad, and it does not swallow: the reason is surfaced verbatim in the
            // operator's refusal text, which is the one place it is needed. Rethrowing would turn a
            // failed measurement into a crashed dispatch, and the fail-closed Hold below is already the
            // correct outcome for "the counters could not be read".
            return new RunwayHarvestAttempt(DateTimeOffset.Now, ex.Message);
        }
    }

    /// <summary>The one wording for "the bound fired", shared by both arms that can reach it so the two
    /// cannot drift into two different messages for one outcome.</summary>
    private static string TimedOutReason(string vendor, TimeSpan bound) =>
        $"the '{vendor}' usage command did not finish within {bound.TotalSeconds:0}s";
}
