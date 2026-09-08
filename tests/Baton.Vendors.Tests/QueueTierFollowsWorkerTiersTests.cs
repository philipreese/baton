using Baton.Queue;
using Baton.Status;
using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// #1863: the queue's <c>tooling</c> pin and <c>WorkerTiers.json</c>'s <c>standard</c> tier are ONE
/// register, driven end to end through the production wiring —
/// <see cref="WorkerRoleCatalog.QueueTierFor"/> reading a real tier file, handed to
/// <see cref="QueueTierTable.Resolve"/> exactly as <c>QueueCommand</c> and
/// <c>QueueSchedulerService</c> hand it.
/// </summary>
/// <remarks>
/// This lives here rather than beside <c>QueueTierTableTests</c> because <c>Baton.Tests</c> does not
/// reference <c>Baton.Vendors</c>, so it can only drive a lambda — and a lambda cannot discriminate
/// whether the CLI's own delegate reads the tier file at all. That file asserts the per-axis
/// flattening rule; this one asserts the wiring. Every override is an isolated
/// <see cref="BatonEnvironmentSnapshot.BeginScope"/> (#1524), so the class is parallel-safe.
/// </remarks>
public sealed class QueueTierFollowsWorkerTiersTests
{
    private sealed class TempTiers : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"qtf-{Guid.NewGuid():N}");

        public TempTiers() => Directory.CreateDirectory(Dir);

        public IDisposable PointAt(string tiersJson) =>
            BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with
            {
                WorkerTiersPathOverride = Write(tiersJson),
            });

        private string Write(string content)
        {
            var path = Path.Combine(Dir, "tiers.json");
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose() => DirectoryCleanup.DeleteRecursively(Dir);
    }

    private static QueueItem ToolingItem() =>
        new()
        {
            Tag = "t1",
            Role = "implement",
            ScopeClass = "tooling",
            Workspace = @"C:\repos\w1",
            SpecFile = @"C:\baton\queue\specs\t1.md",
        };

    /// <summary>
    /// The arm that matters: a fixture tier file naming a triple NOBODY has ever shipped, so a pass
    /// cannot come from the queue happening to hard-code the same values. Before #1863 the queue's
    /// <c>tooling</c> row carried claude/opus/medium and this assertion could not have passed.
    /// </summary>
    [Fact]
    public void A_tooling_item_launches_on_whatever_the_standard_tier_names()
    {
        using var tiers = new TempTiers();
        using var env = tiers.PointAt(
            """
            {
              "frontier": { "adapter": "claude", "model": "opus", "effort": "high" },
              "standard": { "adapter": "agy", "model": "gemini-3.8-flash-low", "effort": "low" }
            }
            """);

        var resolved = QueueTierTable.Resolve(
            ToolingItem(), new QueueSettings(), WorkerRoleCatalog.QueueTierFor, WorkerRoleCatalog.QueueTierForRole);

        Assert.Equal("tooling", resolved.TierKey);
        Assert.Equal("agy", resolved.Adapter);
        Assert.Equal("gemini-3.8-flash-low", resolved.Model);
        Assert.Equal("low", resolved.Effort);
        Assert.False(resolved.IsOverride);
    }

    /// <summary>
    /// The control, in both directions. A tier file with no <c>standard</c> must make the tooling key
    /// look UNCONFIGURED — which is what <c>QueueSchedulerService</c>'s fail-closed check reads before
    /// it fails the item — while a row that names its own triple (<c>engine</c>) is unaffected by the
    /// same file. Without this arm the test above could pass on a lookup that silently fell back.
    /// </summary>
    [Fact]
    public void A_tier_file_without_standard_fails_the_tooling_key_closed_and_leaves_engine_alone()
    {
        using var tiers = new TempTiers();
        using var env = tiers.PointAt("""{ "frontier": { "adapter": "claude", "model": "opus", "effort": "high" } }""");

        Assert.Null(
            QueueTierTable.LookupTier("tooling", new QueueSettings(), WorkerRoleCatalog.QueueTierFor));
        Assert.NotNull(
            QueueTierTable.LookupTier("engine", new QueueSettings(), WorkerRoleCatalog.QueueTierFor));
    }

    /// <summary>
    /// The shipped file itself: <c>tooling</c> resolves to the 2026-09-06 ruling's <c>standard</c> pin.
    /// Hermetic against an operator's runtime <c>worker-tiers.json</c> for the reason
    /// <c>WorkerRoleCatalogTests.ShippedDefault</c> states — point the snapshot at the copy under
    /// <see cref="AppContext.BaseDirectory"/> rather than letting <c>ResolvePath</c> fall through.
    /// </summary>
    [Fact]
    public void The_shipped_tier_file_puts_tooling_on_codex_gpt_6_astra_medium()
    {
        using var env = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with
        {
            WorkerTiersPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"),
        });

        var resolved = QueueTierTable.Resolve(
            ToolingItem(), new QueueSettings(), WorkerRoleCatalog.QueueTierFor, WorkerRoleCatalog.QueueTierForRole);

        Assert.Equal("codex", resolved.Adapter);
        Assert.Equal("gpt-6-astra", resolved.Model);
        Assert.Equal("medium", resolved.Effort);
    }
}
