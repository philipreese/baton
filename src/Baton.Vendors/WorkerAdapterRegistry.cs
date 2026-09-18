namespace Baton.Vendors;

/// <summary>
/// The production adapter registry <c>Baton.Cli</c> wires <see cref="WorkerBindingResolver.Resolve"/>
/// with (M11 Phase 3) — the concrete answer to Phase 1's deferred "no adapter-registration
/// mechanism was built" note, now that a real caller (the CLI) needs one. Each entry is keyed by the
/// capability name rather than a binary name (<c>"claude"</c> invokes the <c>claude</c> binary
/// directly; <c>"agy"</c> invokes the <c>agy</c> binary — the registry key names the capability
/// you're dispatching to, not what you type to reach it). A fourth, <see cref="NoOpWorkerAdapter.AdapterName"/>, was added for M24 Phase
/// 5.2 (#285) — not a vendor at all, but the same registration seam serves it identically.
/// </summary>
public static class WorkerAdapterRegistry
{
    public static IReadOnlyDictionary<string, IWorkerAdapter> Default { get; } = new Dictionary<string, IWorkerAdapter>
    {
        ["claude"] = new ClaudeWorkerAdapter(),
        ["agy"] = new AgyWorkerAdapter(),
        ["codex"] = new CodexWorkerAdapter(),
        [NoOpWorkerAdapter.AdapterName] = new NoOpWorkerAdapter(),
        [WorkflowTemplateComposer.CaptureAdapter] = new CaptureWorkerAdapter(),
        [CommandWorkerAdapter.AdapterName] = new CommandWorkerAdapter(),
    };

    /// <summary>
    /// The registry-owned admission predicate for capabilities whose execution must remain in the
    /// Baton host. Adapters opt in through <see cref="IWorkerAdapter.HasHostMediatedExecutor"/>;
    /// an unknown or ordinary direct-command adapter is deliberately not eligible.
    /// </summary>
    public static bool ProvidesHostMediatedExecution(string? adapter) =>
        adapter is not null
        && Default.TryGetValue(adapter, out var registered)
        && registered.HasHostMediatedExecutor;
}
