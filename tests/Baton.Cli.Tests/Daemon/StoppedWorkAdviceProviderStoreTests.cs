using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;

namespace Baton.Cli.Tests.Daemon;

public sealed class StoppedWorkAdviceProviderStoreTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Repository = "github.com/example/project";
    private const string Key = "stopped-judgment:" + Repository + ":tag:attempt:review";
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "baton-provider-store-" + Guid.NewGuid().ToString("N"));
    private static readonly StoppedWorkAdviceContext Context = new(Repository, "tag", new("attempt"),
        WorkStage.Review, Now, null, null, "Succeeded", true, "passing", Now,
        StoppedWorkHaltCause.MissingVerdict, "available", false, "passing");
    private ConductorObligationStore Store() => new(new FleetEventLog(Path.Combine(_root, "events.jsonl"),
        Path.Combine(_root, "rollover.jsonl"), 1_000_000), Path.Combine(_root, "obligations.json"), () => Now);

    private async Task<StoppedWorkAdviceRequest> SeedAsync(bool historical = false)
    {
        Directory.CreateDirectory(_root);
        var row = await Store().EnqueueAsync(new(Key, Repository, null, "tag", null, StoppedWorkJudgmentKey.Action,
            "holder", Now, historical ? StoppedWorkJudgmentKey.Adapter : StoppedWorkJudgmentKey.ProviderRoute,
            StoppedWorkJudgmentKey.Capability, true, ContextSha256: StoppedWorkAdviceEvidence.Hash(Context)), Ct);
        return new(row.ObligationId, Repository, "tag", new("attempt"), WorkStage.Review,
            row.ContextSha256!, Now, null, null, "holder", StoppedWorkHaltCause.MissingVerdict, "available", false, "passing");
    }
    private static RetainedStoppedWorkAdviceResponse Response(StoppedWorkAdviceRequest request,
        StoppedWorkAdviceProviderDescriptor provider) => new(new(request.ObligationId, Repository, "tag", "attempt",
        request.ContextSha256, StoppedWorkAdviceChoice.Hold, "Missing verdict requires independent action authority."),
        provider.Adapter, provider.Model, provider.Effort, Now);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Admission_freezes_actual_provider_and_restart_ignores_selection_drift(bool historical, bool claude)
    {
        var request = await SeedAsync(historical);
        var selected = claude ? StoppedWorkAdviceProviderDescriptor.Claude : StoppedWorkAdviceProviderDescriptor.Codex;
        var calls = 0;
        var store = Store();
        var first = await store.DecideStoppedWorkOnceAsync(Key, request, Context,
            (_, _) => Task.FromResult(selected), (frozen, _, _, _, _, _) =>
            {
                calls++;
                selected = claude ? StoppedWorkAdviceProviderDescriptor.Codex : StoppedWorkAdviceProviderDescriptor.Claude;
                return Task.FromResult(Response(request, frozen));
            }, Ct);
        var restarted = Store();
        var replay = await restarted.DecideStoppedWorkOnceAsync(Key, request, Context,
            (_, _) => throw new InvalidOperationException("Settings/preflight must not run on replay"),
            (_, _, _, _, _, _) => throw new InvalidOperationException("No second call"), Ct);
        Assert.Equal(1, calls);
        Assert.Equal(first.Response, replay.Response);
        Assert.Equal(historical ? StoppedWorkJudgmentKey.Adapter : StoppedWorkJudgmentKey.ProviderRoute, replay.Obligation.Adapter);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("null")]
    [InlineData("partial")]
    [InlineData("unknown")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    public async Task Only_absent_historical_descriptor_is_codex_and_malformed_markers_never_relaunch(string mutation)
    {
        var request = await SeedAsync(historical: true);
        var store = Store();
        await store.DecideStoppedWorkOnceAsync(Key, request, Context, (_, _) => Task.CompletedTask,
            (_, _, _, _, _) => Task.FromResult(Response(request, StoppedWorkAdviceProviderDescriptor.Codex)), Ct);
        var path = Path.Combine(store.GetStoppedWorkAdviceEvidenceDirectory(Key), "launch.json");
        var marker = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (mutation == "absent") marker.Remove("provider");
        else marker["provider"] = mutation switch
        {
            "null" => null,
            "partial" => JsonNode.Parse("{\"adapter\":\"codex-subscription-cli\"}"),
            "unknown" => JsonNode.Parse("{\"adapter\":\"codex-subscription-cli\",\"model\":\"other\",\"effort\":\"low\"}"),
            _ => marker["provider"]!.DeepClone(),
        };
        if (mutation == "extra") marker["provider"]!["extra"] = true;
        var json = marker.ToJsonString();
        if (mutation == "duplicate") json = json.Insert(1, "\"provider\":null,");
        File.WriteAllText(path, json);
        var operation = Store().DecideStoppedWorkOnceAsync(Key, request, Context,
            (_, _) => throw new InvalidOperationException("Never preflight replay"),
            (_, _, _, _, _, _) => throw new InvalidOperationException("Never retry a marked call"), Ct);
        if (mutation == "absent") Assert.Equal(StoppedWorkAdviceProviderDescriptor.Codex.Adapter, (await operation).Response.Adapter);
        else await Assert.ThrowsAsync<ConductorObligationStoreException>(() => operation);
    }

    [Fact]
    public async Task Mismatched_response_descriptor_is_uncertain_and_not_retried()
    {
        var request = await SeedAsync();
        var store = Store();
        var calls = 0;
        await Assert.ThrowsAsync<ConductorObligationStoreException>(() => store.DecideStoppedWorkOnceAsync(Key, request, Context,
            (_, _) => Task.FromResult(StoppedWorkAdviceProviderDescriptor.Claude), (_, _, _, _, _, _) =>
            {
                calls++;
                return Task.FromResult(Response(request, StoppedWorkAdviceProviderDescriptor.Codex));
            }, Ct));
        await Assert.ThrowsAsync<ConductorObligationStoreException>(() => Store().DecideStoppedWorkOnceAsync(Key, request, Context,
            (_, _) => throw new InvalidOperationException("No fallback admission"),
            (_, _, _, _, _, _) => throw new InvalidOperationException("No fallback call"), Ct));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task New_neutral_route_cannot_lose_its_descriptor_and_become_a_historical_codex_record()
    {
        var request = await SeedAsync();
        var store = Store();
        await store.DecideStoppedWorkOnceAsync(Key, request, Context,
            (_, _) => Task.FromResult(StoppedWorkAdviceProviderDescriptor.Codex),
            (frozen, _, _, _, _, _) => Task.FromResult(Response(request, frozen)), Ct);
        var path = Path.Combine(store.GetStoppedWorkAdviceEvidenceDirectory(Key), "launch.json");
        var marker = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        marker.Remove("provider");
        File.WriteAllText(path, marker.ToJsonString());
        await Assert.ThrowsAsync<ConductorObligationStoreException>(() => Store().DecideStoppedWorkOnceAsync(Key, request, Context,
            (_, _) => throw new InvalidOperationException("No historical fallback"),
            (_, _, _, _, _, _) => throw new InvalidOperationException("No second call"), Ct));
    }

    [Fact]
    public async Task Invalid_selection_refuses_before_marker_and_projection_validates_frozen_response()
    {
        var request = await SeedAsync();
        var store = Store();
        await Assert.ThrowsAsync<ConductorObligationStoreException>(() => store.DecideStoppedWorkOnceAsync(Key, request, Context,
            (_, _) => Task.FromResult(new StoppedWorkAdviceProviderDescriptor("unknown", "model", "low")),
            (_, _, _, _, _, _) => throw new InvalidOperationException("Refused before launch"), Ct));
        var directory = store.GetStoppedWorkAdviceEvidenceDirectory(Key);
        Assert.False(File.Exists(Path.Combine(directory, "launch.json")));
        await store.DecideStoppedWorkOnceAsync(Key, request, Context,
            (_, _) => Task.FromResult(StoppedWorkAdviceProviderDescriptor.Claude),
            (frozen, _, _, _, _, _) => Task.FromResult(Response(request, frozen)), Ct);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        };
        File.WriteAllText(Path.Combine(directory, "response.json"), JsonSerializer.Serialize(
            Response(request, StoppedWorkAdviceProviderDescriptor.Codex), json));
        var row = await Store().ReadAsync(Key, Ct);
        var view = await Store().ReadStoppedWorkAdviceViewAsync(row!, Ct);
        Assert.Equal(StoppedWorkJudgmentState.Uncertain, view!.State);
        Assert.Null(view.Response);
    }

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_root);
}
