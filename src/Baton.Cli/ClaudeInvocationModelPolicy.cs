using Baton.Domain;

namespace Baton.Cli;

/// <summary>
/// Worker admission against the conductor-only model catalog. This reads the final invocation tuple;
/// it neither resolves nor writes a model.
/// </summary>
internal static class ClaudeInvocationModelPolicy
{
    internal const string ExplicitModelRemedy = "pass --model sonnet, --model opus, or --model haiku";

    internal static string? RefusalMessage(string? adapter, string? requestedModel, string? resolvedModel = null)
        => ConductorOnlyModelCatalog.WorkerAdmissionRefusal(adapter, requestedModel, resolvedModel);
}
