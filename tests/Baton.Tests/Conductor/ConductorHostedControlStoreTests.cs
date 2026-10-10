using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Accounting;
using Baton.Conductor;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Tests.Conductor;

public sealed class ConductorHostedControlStoreTests
{
    private static readonly RepositoryIdentity Repo = RepositoryIdentity.From("https://github.com/test/controls", null)!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_atomic_control_replays_history_after_release_and_reacquisition(bool takeover)
    {
        var root = Path.Combine(Path.GetTempPath(), "baton-controls-" + Guid.NewGuid().ToString("N"));
        try
        {
            var claim = await ConductorClaimStore.ClaimAsync(Repo, "old", root, cancellationToken: Ct);
            var request = new ConductorHostedControlRequest(Repo.Value, "old", ConductorClaimStore.GetClaimGeneration(claim),
                new string('a', 32), "request", "operator reason", takeover ? "new" : null, takeover ? "human address" : null);
            var validations = 0;
            var receipt = ConductorClaimStore.ApplyHostedControl(Repo, root, request, "operator", takeover, _ => validations++);
            var controlled = (await ConductorClaimStore.GetClaimAsync(Repo, root, Ct))!;
            Assert.Equal(takeover ? "new" : "old", controlled.Holder);
            Assert.Equal(!takeover, controlled.Stopped);
            Assert.Equal(receipt.ResultGeneration, ConductorClaimStore.GetClaimGeneration(controlled));
            Assert.Equal(takeover ? "human address" : null, controlled.DestinationAddress);
            if (!takeover)
                await Assert.ThrowsAsync<ConductorClaimException>(() => ConductorClaimStore.ClaimAsync(Repo, "old", root, cancellationToken: Ct));
            await ConductorClaimStore.ReleaseAsync(Repo, controlled.Holder!, "release", root, cancellationToken: Ct);
            var fresh = await ConductorClaimStore.ClaimAsync(Repo, "old", root, cancellationToken: Ct);
            Assert.NotEqual(request.ClaimGeneration, ConductorClaimStore.GetClaimGeneration(fresh));
            var replay = ConductorClaimStore.ApplyHostedControl(Repo, root, request, "operator", takeover, _ => validations++);
            Assert.Equal(receipt, replay);
            Assert.Equal(1, validations);
            Assert.Throws<ConductorClaimException>(() => ConductorClaimStore.ApplyHostedControl(Repo, root,
                request with { Reason = "different" }, "operator", takeover, _ => { }));
            Assert.Throws<ConductorClaimException>(() => ConductorClaimStore.ApplyHostedControl(Repo, root,
                request with { RequestId = "stale" }, "operator", takeover, _ => { }));
            Assert.Equal(JsonSerializer.Serialize(fresh), JsonSerializer.Serialize(await ConductorClaimStore.GetClaimAsync(Repo, root, Ct)));
        }
        finally { DirectoryCleanup.DeleteRecursively(root); }
    }

    [Fact]
    public async Task Legacy_stop_materializes_original_derived_generation_before_appending_receipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "baton-controls-" + Guid.NewGuid().ToString("N"));
        try
        {
            var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var legacy = new ConductorClaimRecord(Repo.Value, Repo.FileSlug, "old", timestamp,
                Transitions: [new(ConductorClaimTransitionKind.Claim, "old", null, null, timestamp)]);
            var directory = Path.Combine(root, Repo.FileSlug);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, BatonPaths.ConductorClaimFileName), JsonSerializer.Serialize(legacy));
            var generation = ConductorClaimStore.GetClaimGeneration(legacy);
            var request = new ConductorHostedControlRequest(Repo.Value, "old", generation, new string('a', 32), "stop", "reason");
            ConductorClaimStore.ApplyHostedControl(Repo, root, request, "operator", false, _ => { });
            var stopped = (await ConductorClaimStore.GetClaimAsync(Repo, root, Ct))!;
            Assert.True(stopped.Stopped);
            Assert.Equal(generation, stopped.ClaimGeneration);
            Assert.Equal(generation, ConductorClaimStore.GetClaimGeneration(stopped));
            Assert.False(ConductorClaimStore.IsCurrentHostedAuthority(stopped, "old", generation));
            Assert.Equal(generation, stopped.Transitions![0].Generation);
        }
        finally { DirectoryCleanup.DeleteRecursively(root); }
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("takeover-missing-request")]
    [InlineData("receipt-generation")]
    [InlineData("target-generation")]
    [InlineData("missing-request")]
    [InlineData("destination")]
    [InlineData("transition-kind")]
    public async Task Incompatible_stopped_projection_or_receipt_fails_closed_preserving_bytes(string corruption)
    {
        var root = Path.Combine(Path.GetTempPath(), "baton-controls-" + Guid.NewGuid().ToString("N"));
        try
        {
            var claim = await ConductorClaimStore.ClaimAsync(Repo, "old", root, cancellationToken: Ct);
            var takeover = corruption == "takeover-missing-request";
            var request = new ConductorHostedControlRequest(Repo.Value, "old", ConductorClaimStore.GetClaimGeneration(claim),
                new string('a', 32), "stop", "reason", takeover ? "new" : null, takeover ? "human address" : null);
            ConductorClaimStore.ApplyHostedControl(Repo, root, request, "operator", takeover, _ => { });
            var path = Path.Combine(root, Repo.FileSlug, BatonPaths.ConductorClaimFileName);
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            if (corruption == "stopped") document["stopped"] = false;
            if (corruption == "destination") document["destinationAddress"] = "invented";
            var transition = document["transitions"]![1]!;
            if (corruption == "transition-kind") transition["kind"] = "UnknownStop";
            if (corruption == "receipt-generation") transition["control"]!["ResultGeneration"] = "foreign";
            if (corruption == "target-generation") transition["control"]!["Request"]!["ClaimGeneration"] = "foreign";
            if (corruption == "missing-request") transition["control"]!["Request"] = null;
            if (corruption == "takeover-missing-request") transition["control"]!["Request"] = null;
            File.WriteAllText(path, document.ToJsonString());
            var bytes = File.ReadAllBytes(path);
            await Assert.ThrowsAsync<ConductorClaimException>(() => ConductorClaimStore.GetClaimAsync(Repo, root, Ct));
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { DirectoryCleanup.DeleteRecursively(root); }
    }
}
