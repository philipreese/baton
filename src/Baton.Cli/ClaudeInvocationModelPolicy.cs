using Baton.Domain;

namespace Baton.Cli;

/// <summary>
/// Worker admission against the conductor-only model catalog. This reads the final invocation tuple;
/// it neither resolves nor writes a model.
/// </summary>
internal static class ClaudeInvocationModelPolicy
{
    internal static string? RefusalMessage(string? adapter, string? requestedModel, string? resolvedModel = null)
        => ConductorOnlyModelCatalog.WorkerAdmissionRefusal(adapter, requestedModel, resolvedModel);

    internal static string? TryInvocation(string? adapter) =>
        ConductorOnlyModelCatalog.WorkerRemedy(adapter);

    internal static CliArgumentException Refusal(string message, string? adapter) =>
        TryInvocation(adapter) is { } remedy
            ? new CliArgumentException(message, remedy)
            : new CliArgumentException(message);
}
