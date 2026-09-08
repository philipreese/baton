namespace Baton.Vendors;

/// <summary>Answers which vendor capability records recognize a model token.</summary>
public static class WorkerModelCatalog
{
    /// <summary>
    /// The adapters whose recorded capabilities carry <paramref name="model"/>. The individual
    /// records remain the source of their model sets: Claude's aliases, agy's placed catalogue, and
    /// Codex's dated snapshot each state their own provenance.
    /// </summary>
    public static IReadOnlyList<string> AdaptersFor(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var adapters = new List<string>();
        if (ClaudeWorkerAdapter.ModelAliases.Contains(model, StringComparer.OrdinalIgnoreCase))
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
