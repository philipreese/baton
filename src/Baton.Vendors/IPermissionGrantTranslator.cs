namespace Baton.Vendors;

/// <summary>
/// Opt-in capability an <see cref="IWorkerAdapter"/> implements when its vendor CLI's permission
/// vocabulary can be driven from a structured <see cref="PermissionGrant"/> — the M21 Phase 1
/// builder UI in Baton.Ui's bindings editor (deleted, #1412) keyed two things on it: the inline gap
/// warning (<c>'{adapter}' has no structured permission builder support</c>) and the Save guard
/// that blocked persisting a grant the adapter refused. It does <em>not</em> gate the checkbox builder itself —
/// the checkboxes render for every adapter, and a grant built on one that does not implement this
/// (e.g. <see cref="CommandWorkerAdapter"/>, which never shells out to a permission-gated vendor
/// CLI at all) is persisted with only that advisory warning. Corrected after measurement: the
/// earlier wording here claimed the interface gated the builder, and was copied onward on the
/// strength of it. Kept separate from <see cref="IWorkerAdapter"/> itself rather
/// than added to that interface, so every existing/future adapter with no vendor permission
/// vocabulary to translate is never forced to implement a no-op method.
/// </summary>
public interface IPermissionGrantTranslator
{
    /// <summary>
    /// Attempts to translate <paramref name="grant"/> into this adapter's vendor-native permission
    /// flag value. <see cref="IWorkerAdapter.Resolve"/> calls this same logic internally when an
    /// invocation carries a <see cref="WorkerInvocation.PermissionGrant"/> — this method also exists
    /// standalone so the builder UI can validate a grant before Save, without needing a full
    /// <see cref="Baton.Domain.WorkerContract"/> to call <c>Resolve</c> with.
    /// <para>
    /// Must refuse (return <see langword="false"/>) rather than approximate whenever the requested
    /// grant cannot be expressed exactly — granting more than requested is as much a bug here as
    /// granting less (Adapter Isolation cuts both ways; <see href="../../../docs/agents/developing-baton.md"/>).
    /// </para>
    /// </summary>
    bool TryTranslatePermissionGrant(PermissionGrant grant, out string? resolvedValue, out string? gapReason);
}
