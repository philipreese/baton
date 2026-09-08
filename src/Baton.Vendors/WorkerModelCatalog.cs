namespace Baton.Vendors;

/// <summary>Finds adapter candidates for a model token when no adapter was named.</summary>
public static class WorkerModelCatalog
{
    /// <summary>
    /// Candidate hints, not an allowlist: Claude's aliases and full-id prefix, agy's display
    /// catalogue, and Codex's dated snapshot. Explicit adapter choices use the adapter's validation.
    /// </summary>
    public static IReadOnlyList<string> AdaptersFor(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var adapters = new List<string>();
        if (ClaudeWorkerAdapter.ModelAliases.Contains(model, StringComparer.OrdinalIgnoreCase)
            || model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            adapters.Add("claude");
        }

        if (DepthTierMapping.TryResolve("agy", model, out _))
        {
            adapters.Add("agy");
        }

        if (CodexWorkerAdapter.KnowsRecordedModel(model))
        {
            adapters.Add("codex");
        }

        return adapters;
    }
}
