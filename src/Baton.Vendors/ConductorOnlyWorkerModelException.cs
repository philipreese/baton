using Baton;

namespace Baton.Vendors;

/// <summary>
/// Raised at the binding boundary when a worker entry would invoke a model reserved for the
/// conductor.  This remains a typed flow refusal so hand-authored <c>baton run</c> configs do not
/// turn an admission problem into an unhandled vendor invocation.
/// </summary>
public sealed class ConductorOnlyWorkerModelException : BatonFlowException
{
    public ConductorOnlyWorkerModelException(string workerName, string? adapter, string refusal)
        : base($"Worker '{workerName}' {refusal}")
    {
        WorkerName = workerName;
        // Claude has a stable, adapter-owned alias list. The other adapters do not: Codex's
        // measured CLI default is itself conductor-only, and naming one currently available model
        // here would turn a safety remedy into a second model-policy register. Silence is safer than
        // telling a Codex or Agy lane to pass Claude-only aliases.
        TryInvocation = Baton.Domain.ConductorOnlyModelCatalog.WorkerRemedy(adapter);
    }

    public string WorkerName { get; }
}
