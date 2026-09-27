using Baton.Domain;

namespace Baton.Vendors.Tests;

public sealed class WorkerModelCatalogTests
{
    [Theory]
    [InlineData("gpt-6-sol")]
    [InlineData("gpt-6-luna")]
    [InlineData("gpt-5.6-sol")]
    [InlineData("gpt-5.6-luna")]
    public void Recorded_codex_models_are_derived_as_codex_candidates(string model)
    {
        Assert.Equal(["codex"], WorkerModelCatalog.AdaptersFor(model));
    }

    [Fact]
    public void Full_claude_ids_and_aliases_are_derived_as_claude_candidates()
    {
        Assert.Equal(["claude"], WorkerModelCatalog.AdaptersFor("claude-opus-5-5"));
        Assert.Equal(["claude"], WorkerModelCatalog.AdaptersFor("opus"));
    }

    [Fact]
    public void Conductor_only_families_remain_classified_separately_from_candidate_derivation()
    {
        Assert.Equal("Astra", ConductorOnlyModelCatalog.ConductorFamily("gpt-6-astra"));
        Assert.Equal("Fable", ConductorOnlyModelCatalog.ConductorFamily("claude-fable-5-5"));
    }
}
