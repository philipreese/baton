namespace Baton.Runway;

/// <summary>
/// One vendor-reported counter a runway decision was taken against — the window's own name as the
/// vendor spells it (<c>VendorUsageWindow.Name</c>) and its percent USED, or null when the vendor
/// reported no number for it. Carried onto the refusal message, onto the room record so an override is
/// auditable against what was actually on screen when it was taken, and onto every
/// <see cref="RunwayAdmissionEntry"/>.
/// </summary>
/// <remarks>
/// <b>Lives in the engine layer, not in <c>Baton.Vendors</c> where <c>RunwayGate</c> produces it</b>
/// (#1896). <see cref="RunwayAdmissionEntry"/> has to carry these, and the admission ledger is built on
/// <c>JsonLinesLedger&lt;T&gt;</c>, which is internal to <c>Baton</c> — so the row type must live here
/// too. Relocating the one record is what keeps a single counter shape across the gate, the binding
/// record, and the ledger instead of a second near-identical one. The JSON wire shape is unaffected: a
/// namespace is not serialized, so a <c>bindings.json</c> written by an older build still round-trips.
/// </remarks>
public sealed record RunwayCounter(string Window, int? PercentUsed);
