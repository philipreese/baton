using System.Text.Json.Serialization;

namespace Baton.Vendors;

/// <summary>
/// One vendor's own reported usage window — e.g. claude's "Current session" or agy's "Gemini Models
/// · Weekly Limit". <see cref="PercentUsed"/>/<see cref="ResetsAt"/> are null whenever the vendor's
/// own line does not carry that half (decision: "unparsed → unknown, never a number", issue #1391) —
/// never a guessed or zero-filled value. <see cref="RawLine"/> is the vendor's own line verbatim, kept
/// so a reader can show the vendor's own reset text even when <see cref="ResetsAt"/> failed to parse
/// into a real instant.
/// </summary>
public sealed record VendorUsageWindow(
    string Name,
    int? PercentUsed,
    DateTimeOffset? ResetsAt,
    string RawLine);

/// <summary>
/// One harvest of a single vendor's headless <c>/usage</c> report (issue #1391, reporting slice only
/// — spec/baton.md §6). <see cref="Caveat"/> is the vendor's own machine-local disclaimer, verbatim,
/// when the harvested output carried one; never fabricated. <see cref="Windows"/> is empty (never
/// null) rather than the whole snapshot being null when a harvest ran but nothing recognizable
/// parsed — see each <see cref="IVendorUsageSource"/> implementation's own doc comment for exactly
/// what shape it recognizes.
/// </summary>
/// <param name="Source">
/// #1904: this snapshot's provenance — see <see cref="VendorUsageProvenance"/> for the two values and
/// what each asserts. Trailing with a <see cref="VendorUsageProvenance.Vendor"/> default so the two #1391 sources keep
/// their existing shape; <see cref="CodexUsageSource"/> is the only thing that sets
/// <see cref="VendorUsageProvenance.Derived"/>. Carried all the way onto the fleet projection's
/// <c>vendors[].source</c> field so no reader can mistake one for the other.
/// </param>
public sealed record VendorUsageSnapshot(
    string Vendor,
    DateTimeOffset HarvestedAt,
    string? Caveat,
    IReadOnlyList<VendorUsageWindow> Windows,
    VendorUsageProvenance Source = VendorUsageProvenance.Vendor);

/// <summary>
/// Where a <see cref="VendorUsageSnapshot"/>'s numbers came from (#1904). A closed set of two, so
/// "is this the vendor's own counter?" is a field read rather than an inference from which adapter
/// tag happens to be on the snapshot.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<VendorUsageProvenance>))]
public enum VendorUsageProvenance
{
    /// <summary>The vendor's own CLI reported these numbers — claude's and agy's <c>/usage</c>.</summary>
    [JsonStringEnumMemberName("vendor")] Vendor,

    /// <summary>
    /// Baton derived these numbers from its own records because the vendor exposes no plan counter
    /// Baton has measured. Never presentable as the vendor's own reading — see
    /// <see cref="CodexUsageSource"/> for the one implementation and exactly what it derives from.
    /// </summary>
    [JsonStringEnumMemberName("derived")] Derived,
}

/// <summary>
/// One vendor's plan usage as the harvester reads it. One implementation per adapter;
/// <see cref="Vendor"/> matches the adapter tag the rest of the codebase already uses
/// (<c>ClaudeWorkerAdapter.DeniedToolsVendorTag</c> / <c>AgyWorkerAdapter</c>'s own tag).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two kinds of source, distinguished on the wire (#1904).</b> This interface used to rule out a
/// derived source outright (#1391). #1904 narrowed that ruling rather than deleting it — its exact
/// terms, the clause it quotes, and what still stands unchanged are spec/baton.md §6's
/// <c>source: vendor|derived</c> paragraph, which is the register and is not restated here.
/// <see cref="CodexUsageSource"/> is the one implementation the narrowing admits.
/// </para>
/// </remarks>
public interface IVendorUsageSource
{
    /// <summary>The adapter tag this source harvests, e.g. <c>"claude"</c> or <c>"agy"</c>.</summary>
    string Vendor { get; }

    /// <summary>
    /// Runs the vendor's own headless usage command once and parses its output. Returns null when the
    /// CLI could not be spawned, exited non-zero, or exited zero having written nothing at all —
    /// never a snapshot with fabricated content, and a null tells the harvester to leave the last
    /// persisted snapshot alone rather than blank it (<see cref="VendorUsageCommandRun"/> is where
    /// all three cases are decided, and its doc comment has the #1869 defect they close). Output that
    /// was written but is unrecognizable still returns a snapshot, with
    /// <see cref="VendorUsageSnapshot.Windows"/> empty, so a caller can tell "harvested, nothing
    /// parsed" apart from "did not harvest at all".
    /// </summary>
    Task<VendorUsageSnapshot?> ReadAsync(CancellationToken cancellationToken);
}
