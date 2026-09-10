using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Vendors;

/// <summary>
/// Production worker for bookkeeping-only DAG steps that do no real work (M24 Phase 5.2, #285):
/// <see cref="InteractiveSessionMaterializer"/>'s chat continuation needs a downstream anchor step
/// purely to give a repeated-turn <c>Supersede</c> a legal, non-self-referencing target
/// -- <c>Supersede</c>'s target must be a distinct transitive ancestor, and a single "chat"
/// step has none. This adapter ignores <see cref="WorkerInvocation.PromptTemplate"/> entirely and
/// writes its declared output instantly via a trivial shell invocation -- no vendor CLI, no network,
/// no meaningful cost or latency (Adapter Isolation, docs/agents/developing-baton.md: this is not a vendor, so it carries
/// none of a vendor's quirks) -- including no grant machinery (#1166): it does not implement
/// <see cref="IPermissionGrantTranslator"/>, so <see cref="ProjectCeilingGate"/> never runs against it.
/// </summary>
public sealed class NoOpWorkerAdapter : IWorkerAdapter
{
    public const string AdapterName = "noop";

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        var outputName = contract.ProducedOutputs.Count > 0
            ? contract.ProducedOutputs[0].Name
            : "noop-output";

        return OperatingSystem.IsWindows()
            ? new CoreDispatchTarget("cmd", ["/c", $"echo ok>%BATON_OUTPUT_DIR%\\{outputName}"])
            : new CoreDispatchTarget("sh", ["-c", $"echo ok > \"$BATON_OUTPUT_DIR/{outputName}\""]);
    }
}
