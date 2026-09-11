using Baton;

namespace Baton.Vendors;

/// <summary>
/// Raised at the binding boundary when a worker entry would invoke a model reserved for the
/// conductor.  This remains a typed flow refusal so hand-authored <c>baton run</c> configs do not
/// turn an admission problem into an unhandled vendor invocation.
/// </summary>
public sealed class ConductorOnlyWorkerModelException : BatonFlowException
{
    public ConductorOnlyWorkerModelException(string workerName, string refusal)
        : base($"Worker '{workerName}' {refusal}")
    {
        WorkerName = workerName;
        TryInvocation = "pass --model sonnet, --model opus, or --model haiku.";
    }

    public string WorkerName { get; }
}
