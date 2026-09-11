using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// The deliberately small catalog of models reserved for a conductor session. Worker admission and
/// telemetry both ask this one catalog so a model cannot be refused before launch but disappear from
/// the post-run account of what actually happened.
/// </summary>
public static class ConductorOnlyModelCatalog
{
    /// <summary>The durable telemetry token emitted when a worker stream reports a conductor model.</summary>
    public const string ObservedAnomalyName = "conductor-only-model-observed";

    /// <summary>
    /// Returns the conductor-model family for <paramref name="model"/>, or null for a worker model
    /// (including a model this catalog does not know). Claude's dated Fable ids share a prefix; Astra
    /// is one recorded Codex id. This is classification, not a general model allowlist.
    /// </summary>
    public static string? ConductorFamily(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var candidate = model.Trim();
        if (candidate.StartsWith("claude-fable-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, "fable", StringComparison.OrdinalIgnoreCase))
        {
            return "Fable";
        }

        return string.Equals(candidate, "gpt-6-astra", StringComparison.OrdinalIgnoreCase)
            ? "Astra"
            : null;
    }

    public static bool IsConductorOnly(string? model) => ConductorFamily(model) is not null;

    /// <summary>
    /// Returns the worker-lane admission refusal for an invocation tuple, if any.  The requested
    /// model wins because it is what reaches the vendor; a resolved stamp is a bind-time default for
    /// an omitted request, and the shipped adapter default is the final fallback.  This is deliberately
    /// shared by the early room-creation paths and the resolver that every real invocation crosses.
    /// </summary>
    public static string? ConductorOnlyWorkerRefusal(
        string? adapter, string? requestedModel, string? resolvedModel = null)
    {
        var effectiveModel = !string.IsNullOrWhiteSpace(requestedModel)
            ? requestedModel
            : !string.IsNullOrWhiteSpace(resolvedModel)
                ? resolvedModel
                : AdapterDefaultModels.For(adapter);
        if (ConductorFamily(effectiveModel) is { } family)
        {
            return $"{family} is conductor-only and cannot run a worker lane.";
        }

        return null;
    }

    /// <summary>
    /// The command-level admission rule, including the long-standing Claude requirement for an
    /// explicit model. Resolver callers use <see cref="ConductorOnlyWorkerRefusal"/> instead so
    /// their established validation order for ordinary Claude bindings remains unchanged.
    /// </summary>
    public static string? WorkerAdmissionRefusal(
        string? adapter, string? requestedModel, string? resolvedModel = null)
    {
        if (ConductorOnlyWorkerRefusal(adapter, requestedModel, resolvedModel) is { } refusal)
        {
            return refusal;
        }

        var effectiveModel = !string.IsNullOrWhiteSpace(requestedModel)
            ? requestedModel
            : !string.IsNullOrWhiteSpace(resolvedModel)
                ? resolvedModel
                : AdapterDefaultModels.For(adapter);

        return string.Equals(adapter?.Trim(), "claude", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(effectiveModel)
            ? "Claude has no proven safe implicit invocation model: the standing model policy forbids deferring to the vendor CLI default."
            : null;
    }

    /// <summary>
    /// Creates the durable anomaly for an observed model. Requested and resolved remain separate:
    /// the first is intent, while the second is the bind-time default that supplied intent when the
    /// request omitted a model.
    /// </summary>
    public static ConductorOnlyModelAnomaly? ObservedAnomaly(
        string executionId, string? requestedModel, string? resolvedModel, string? observedModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        return ConductorFamily(observedModel) is { } family
            ? new ConductorOnlyModelAnomaly(
                ObservedAnomalyName, family, executionId, requestedModel, resolvedModel, observedModel)
            : null;
    }
}

/// <summary>
/// A named observation that a worker vendor reported a conductor-only model. It is additive telemetry,
/// not a retry or authorization mechanism: an already-running execution cannot be unspent.
/// </summary>
public sealed record ConductorOnlyModelAnomaly(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("family")] string Family,
    [property: JsonPropertyName("execution")] string Execution,
    [property: JsonPropertyName("requestedModel")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RequestedModel,
    [property: JsonPropertyName("resolvedModel")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ResolvedModel,
    [property: JsonPropertyName("observedModel")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ObservedModel);
