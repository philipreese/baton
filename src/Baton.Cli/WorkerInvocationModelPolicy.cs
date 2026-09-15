using Baton.Domain;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// Worker admission against the conductor-only catalog and recorded adapter candidates. This reads
/// the final invocation tuple; it neither resolves nor writes a model, and unknown tokens remain the
/// selected adapter's own concern rather than a brittle prefix-derived refusal.
/// </summary>
internal static class WorkerInvocationModelPolicy
{
    internal static string? RefusalMessage(string? adapter, string? requestedModel, string? resolvedModel = null)
    {
        if (ConductorOnlyModelCatalog.WorkerAdmissionRefusal(adapter, requestedModel, resolvedModel)
            is { } conductorRefusal) return conductorRefusal;

        var model = requestedModel ?? resolvedModel;
        if (string.IsNullOrWhiteSpace(adapter) || string.IsNullOrWhiteSpace(model)) return null;
        var candidates = WorkerModelCatalog.AdaptersFor(model);
        if (candidates.Count == 0 || candidates.Contains(adapter, StringComparer.OrdinalIgnoreCase)) return null;
        return $"--model '{model}' is known by {string.Join(", ", candidates)}, but the resolved "
            + $"{adapter} adapter cannot use it; specify a compatible --adapter or choose a {adapter} model.";
    }

    internal static string? TryInvocation(string? adapter) =>
        ConductorOnlyModelCatalog.WorkerRemedy(adapter);

    internal static CliArgumentException Refusal(string message, string? adapter) =>
        TryInvocation(adapter) is { } remedy
            ? new CliArgumentException(message, remedy)
            : new CliArgumentException(message);
}
