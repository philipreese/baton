using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// The one pre-provision admission operation for a resolved worker tuple. The catalog and adapter
/// rules stay in their owning assemblies; queue admission and direct dispatch supply the same values.
/// </summary>
internal static class WorkerInvocationValidation
{
    internal static void Validate(
        string? adapter,
        string? requestedModel,
        string? resolvedModel,
        string? effort,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        bool includeModelPolicy = true)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        if (adapter is null)
        {
            return;
        }

        var worker = adapters.FirstOrDefault(pair =>
            string.Equals(pair.Key, adapter, StringComparison.OrdinalIgnoreCase)).Value;
        if (worker is null)
        {
            throw new CliArgumentException($"Unknown adapter '{adapter}'.");
        }

        try
        {
            worker.ValidateRequestedInvocation(requestedModel ?? resolvedModel, effort);
        }
        catch (BatonFlowException ex)
        {
            // Snapshot-unavailable errors retain their own evidence; they are not rewritten as an
            // invalid model/effort pair.
            throw WorkerInvocationModelPolicy.Refusal(ex.Message, adapter);
        }

        if (includeModelPolicy
            && WorkerInvocationModelPolicy.RefusalMessage(adapter, requestedModel, resolvedModel) is { } refusal)
        {
            throw WorkerInvocationModelPolicy.Refusal(refusal, adapter);
        }
    }
}
