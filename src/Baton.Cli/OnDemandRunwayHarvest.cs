using Baton.Cli.Daemon;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// #1923: the runway hold's own harvest, run once inline when a gated vendor has a usage source but
/// no persisted snapshot at all. <b>The bootstrap this closes:</b> the daemon's
/// <see cref="VendorUsageHarvester"/> only harvests a vendor while one of its lanes is live (or just
/// after one exits), so a vendor with no lane running never got a snapshot, and the hold read "no
/// snapshot" as halted — the first agy lane of a window could not start because no agy lane was
/// running. Measured 2026-09-05; spec/baton.md §7's "Runway hold (#1848)" is the register.
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
/// vendor has no snapshot on disk. A vendor whose snapshot exists — fresh or stale — is not harvested
/// here: stale is a state the daemon's cadence already refreshes, and re-harvesting it would put a
/// vendor spawn on the common path rather than the cold-start one.
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
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Harvests <paramref name="vendor"/> once and persists what it read, or returns null when no
    /// harvest was attempted — the vendor is not gated (its decision cannot turn on counters), no
    /// source ships for it, or a snapshot already exists. Null is the caller's signal to leave the
    /// refusal's existing "never harvested" wording alone.
    /// </summary>
    /// <param name="snapshotExists">
    /// Whether the vendor already has a readable persisted snapshot. Passed in rather than re-read
    /// here so the caller reads the file exactly once per decision.
    /// </param>
    public static async Task<RunwayHarvestAttempt?> TryHarvestAsync(
        string vendor,
        bool snapshotExists,
        IReadOnlyList<IVendorUsageSource> sources,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(vendor);
        ArgumentNullException.ThrowIfNull(sources);

        if (snapshotExists || !RunwayGate.IsGated(vendor))
        {
            return null;
        }

        var source = sources.FirstOrDefault(s => string.Equals(s.Vendor, vendor, StringComparison.Ordinal));
        if (source is null)
        {
            return null;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(Bound);

        try
        {
            var snapshot = await source.ReadAsync(bounded.Token).ConfigureAwait(false);
            if (snapshot is null)
            {
                // IVendorUsageSource.ReadAsync's own contract for null: not spawned, non-zero exit, or
                // no output at all. The source has already written the specifics to stderr; what the
                // refusal needs is that a harvest ran and produced nothing.
                return new RunwayHarvestAttempt(
                    DateTimeOffset.UtcNow,
                    $"the '{vendor}' CLI did not produce a readable usage report");
            }

            VendorUsageHarvester.Persist(vendor, snapshot);
            return new RunwayHarvestAttempt(DateTimeOffset.UtcNow, FailureReason: null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RunwayHarvestAttempt(
                DateTimeOffset.UtcNow,
                $"the '{vendor}' usage command did not finish within {Bound.TotalSeconds:0}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately broad, and it does not swallow: the reason is surfaced verbatim in the
            // operator's refusal text, which is the one place it is needed. Rethrowing would turn a
            // failed measurement into a crashed dispatch, and the fail-closed Hold below is already the
            // correct outcome for "the counters could not be read".
            return new RunwayHarvestAttempt(DateTimeOffset.UtcNow, ex.Message);
        }
    }
}
