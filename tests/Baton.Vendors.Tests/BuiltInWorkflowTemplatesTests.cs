namespace Baton.Vendors.Tests;

public class BuiltInWorkflowTemplatesTests
{
    [Fact]
    public void Catalog_ContainsSoloAndReviewRunTemplates()
    {
        var catalog = BuiltInWorkflowTemplates.Catalog;
        Assert.Equal(4, catalog.Count);
        Assert.Contains(catalog, t => t.Id == "chat-session");
        Assert.Contains(catalog, t => t.Id == "codebase-session");
        Assert.Contains(catalog, t => t.Id == "solo-run");
        Assert.Contains(catalog, t => t.Id == "review-run");
        // The dispatch roles deliberately stay OUT of Catalog (they'd land in the start
        // pickers) — GetRoleTemplates() is their export surface, asserted below.
        Assert.DoesNotContain(catalog, t => t.Id == "implement");
    }

    [Fact]
    public void GetRoleTemplates_ContainsEveryCatalogRole_WithValidFields()
    {
        var roles = BuiltInWorkflowTemplates.GetRoleTemplates();
        Assert.Equal(9, roles.Count);
        Assert.True(roles.ContainsKey("advise"));
        Assert.True(roles.ContainsKey("implement"));
        Assert.True(roles.ContainsKey("measure"));
        Assert.True(roles.ContainsKey("review"));
        Assert.True(roles.ContainsKey("fact-check"));
        Assert.True(roles.ContainsKey("janitor"));
        Assert.True(roles.ContainsKey("orchestrate"));
        Assert.True(roles.ContainsKey("patch"));
        Assert.True(roles.ContainsKey("consolidate"));

        foreach (var (id, role) in roles)
        {
            Assert.False(string.IsNullOrWhiteSpace(role.Adapter));
            Assert.False(string.IsNullOrWhiteSpace(role.Use));
            Assert.InRange(role.TimeoutMinutes, 1, 120);
            Assert.NotEmpty(role.Outputs);
        }
    }
}
