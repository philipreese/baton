using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Cli.Daemon;
using Baton.Cli.Mcp;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests.Mcp;

/// <summary>
/// #1981: the daemon hung for thirteen minutes on 2026-09-06 with its process alive and nothing —
/// not the glass, not <c>fleet_status</c> — saying so. These arms cover the reading itself
/// (<see cref="FleetProjectionStaleness"/>) and its arrival on the tool's wire shape, both
/// polarities: a projection written just now must NOT read stale, or the flag says nothing.
/// </summary>
public sealed class FleetProjectionStalenessTests : IDisposable
{
    private static readonly TimeSpan ThreeTicks = TimeSpan.FromSeconds(90);

    private readonly string _tempHome;
    private readonly IDisposable _scope;

    public FleetProjectionStalenessTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"baton-projection-staleness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = _tempHome });
    }

    public void Dispose()
    {
        _scope.Dispose();
        if (Directory.Exists(_tempHome))
        {
            DirectoryCleanup.DeleteRecursively(_tempHome);
        }
    }

    private string WriteProjection(DateTimeOffset derivedAt)
    {
        var path = Path.Combine(_tempHome, "projection.json");
        File.WriteAllText(path, $$"""{"derived_at": "{{derivedAt:O}}", "rooms": []}""");
        return path;
    }

    [Fact]
    public void AProjectionOlderThanThreeTicks_ReadsStale_WithItsAge()
    {
        var now = DateTimeOffset.UtcNow;
        var path = WriteProjection(now.AddSeconds(-600));

        var reading = FleetProjectionStaleness.Read(path, now, ThreeTicks);

        Assert.True(reading.Stale);
        Assert.Equal(600, reading.AgeSeconds!.Value, precision: 0);
    }

    /// <summary>Control arm — without it, a reading that answered "stale" unconditionally would pass
    /// the test above and tell an operator nothing.</summary>
    [Fact]
    public void AProjectionWrittenThisTick_ReadsFresh()
    {
        var now = DateTimeOffset.UtcNow;
        var path = WriteProjection(now.AddSeconds(-5));

        var reading = FleetProjectionStaleness.Read(path, now, ThreeTicks);

        Assert.False(reading.Stale);
        Assert.Equal(5, reading.AgeSeconds!.Value, precision: 0);
    }

    [Fact]
    public void TheThresholdIsExclusive_OneSecondEitherSideOfIt()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.False(FleetProjectionStaleness.Read(WriteProjection(now - ThreeTicks), now, ThreeTicks).Stale);
        Assert.True(FleetProjectionStaleness.Read(
            WriteProjection(now - ThreeTicks - TimeSpan.FromSeconds(1)), now, ThreeTicks).Stale);
    }

    /// <summary>Fail closed, and never fabricate an age: an absent, unreadable, or malformed
    /// projection is the same operational fact as a very old one — the daemon is not writing.</summary>
    [Fact]
    public void AbsentMalformedOrUndatedProjections_ReadStale_WithNoAge()
    {
        var now = DateTimeOffset.UtcNow;
        var absent = Path.Combine(_tempHome, "does-not-exist.json");

        var malformed = Path.Combine(_tempHome, "malformed.json");
        File.WriteAllText(malformed, "{ this is not json");

        var undated = Path.Combine(_tempHome, "undated.json");
        File.WriteAllText(undated, """{"rooms": []}""");

        var unparseableDate = Path.Combine(_tempHome, "unparseable-date.json");
        File.WriteAllText(unparseableDate, """{"derived_at": "yesterday-ish", "rooms": []}""");

        foreach (var path in new[] { absent, malformed, undated, unparseableDate })
        {
            var reading = FleetProjectionStaleness.Read(path, now, ThreeTicks);
            Assert.True(reading.Stale, $"expected {Path.GetFileName(path)} to read stale");
            Assert.Null(reading.AgeSeconds);
        }
    }

    /// <summary>
    /// A read taken WHILE the daemon holds the file open for writing still reads it, and does not
    /// block that write — spec/baton.md §7's <c>FileShare.ReadWrite | FileShare.Delete</c> rule
    /// (#1782). Without it a conductor polling <c>fleet_status</c> would intermittently report a hang
    /// on a perfectly healthy daemon, and would itself be the sharing violation
    /// <c>WriteAtomic</c>'s retry loop exists to absorb — both on the same tick, and neither visible
    /// to an arm that writes the file with nothing else holding it.
    /// </summary>
    [Fact]
    public void AReadWhileTheDaemonHoldsTheFileOpen_StillReadsIt()
    {
        var now = DateTimeOffset.UtcNow;
        var path = WriteProjection(now.AddSeconds(-5));

        using (var heldByTheDaemon = new FileStream(
                   path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            var reading = FleetProjectionStaleness.Read(path, now, ThreeTicks);

            Assert.False(reading.Stale);
            Assert.NotNull(reading.AgeSeconds);
        }
    }

    /// <summary>The threshold tracks whatever interval the daemon is actually running at, rather than
    /// pinning 90 seconds: widening the tick must widen the staleness bound with it.</summary>
    [Fact]
    public void StaleAfter_IsThreeTicksOfTheIntervalInEffect()
    {
        Assert.Equal(FleetProjectionWriter.DefaultInterval * 3, FleetProjectionWriter.StaleAfter());

        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { FleetProjectionIntervalSecondsOverride = "120" });
        Assert.Equal(TimeSpan.FromSeconds(360), FleetProjectionWriter.StaleAfter());
    }

    [Fact]
    public async Task FleetStatus_CarriesTheAge_AndFlagsStaleOnlyWhenItIs()
    {
        var projectionPath = BatonPaths.FleetProjectionFile;
        Directory.CreateDirectory(Path.GetDirectoryName(projectionPath)!);

        // Fresh: `stale` is omitted entirely (JsonIgnore WhenWritingDefault), so a healthy response
        // stays byte-identical to a pre-#1981 one apart from the age.
        await File.WriteAllTextAsync(
            projectionPath,
            $$"""{"derived_at": "{{DateTimeOffset.UtcNow:O}}", "rooms": []}""",
            TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var fresh = await tool.CallAsync(default, TestContext.Current.CancellationToken);
        var freshRoot = JsonNode.Parse(fresh.Text)!.AsObject();
        Assert.False(freshRoot.ContainsKey("stale"));
        Assert.True(freshRoot["projectionAgeSeconds"]!.GetValue<double>() < 90);

        // Stale: both fields present, and the deserialized response carries them.
        await File.WriteAllTextAsync(
            projectionPath,
            $$"""{"derived_at": "{{DateTimeOffset.UtcNow.AddMinutes(-13):O}}", "rooms": []}""",
            TestContext.Current.CancellationToken);

        var stale = await tool.CallAsync(default, TestContext.Current.CancellationToken);
        var staleResponse = JsonSerializer.Deserialize<FleetStatusResponse>(stale.Text)!;
        Assert.True(staleResponse.Stale);
        Assert.True(staleResponse.ProjectionAgeSeconds > 13 * 60 - 5);
    }
}
