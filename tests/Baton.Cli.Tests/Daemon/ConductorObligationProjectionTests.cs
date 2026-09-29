using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Cli.Daemon;
using Baton.Status;

namespace Baton.Cli.Tests.Daemon;

public sealed class ConductorObligationProjectionTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"baton-obligation-view-{Guid.NewGuid():N}");
    private readonly IDisposable _scope;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public ConductorObligationProjectionTests()
    {
        Directory.CreateDirectory(_home);
        _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = _home });
    }

    public void Dispose()
    {
        _scope.Dispose();
        DirectoryCleanup.DeleteRecursively(_home);
    }

    private static ConductorObligation Row(ConductorObligationStatus status) => new(
        "id", "key", "private-project", "private-room", null, null, "continue", "queue-lifecycle",
        DateTimeOffset.Parse("2026-09-29T12:00:00Z"), "adapter", "capability", true, status,
        ActionObservedAt: status == ConductorObligationStatus.ActionObserved ? DateTimeOffset.UtcNow : null,
        TransportReceipt: "secret-receipt", Reason: "C:\\secret\\cause", ActionProof: "secret-proof",
        TargetWorkspace: "C:\\private", ContextSha256: "secret-context");

    private void Snapshot(params ConductorObligation[] rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.ConductorObligationsFile)!);
        File.WriteAllText(BatonPaths.ConductorObligationsFile, JsonSerializer.Serialize(new
        {
            version = 2,
            openObligations = rows.Where(row => row.Status is not (ConductorObligationStatus.ActionObserved
                or ConductorObligationStatus.Blocked or ConductorObligationStatus.Unsupported)),
            terminalObligations = rows.Where(row => row.Status is ConductorObligationStatus.ActionObserved
                or ConductorObligationStatus.Blocked or ConductorObligationStatus.Unsupported),
        }, Json));
    }

    [Theory]
    [InlineData(ConductorObligationStatus.Pending)]
    [InlineData(ConductorObligationStatus.Submitted)]
    [InlineData(ConductorObligationStatus.TransportAcknowledged)]
    [InlineData(ConductorObligationStatus.ActionObserved)]
    [InlineData(ConductorObligationStatus.Blocked)]
    [InlineData(ConductorObligationStatus.Unsupported)]
    public async Task Each_state_is_projected_without_private_payload_or_writes(ConductorObligationStatus status)
    {
        Snapshot(Row(status));
        var before = File.ReadAllBytes(BatonPaths.ConductorObligationsFile);
        var view = await ConductorObligationProjection.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(view["available"]!.GetValue<bool>());
        Assert.Equal(status.ToString(), view["rows"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(status == ConductorObligationStatus.ActionObserved ? 0 : 1,
            view["unresolvedCount"]!.GetValue<int>());
        Assert.DoesNotContain("secret", view.ToJsonString());
        Assert.DoesNotContain("private", view.ToJsonString());
        Assert.Equal(before, File.ReadAllBytes(BatonPaths.ConductorObligationsFile));
        Assert.False(File.Exists(BatonPaths.FleetEventsFile));
    }

    [Fact]
    public async Task Missing_invalid_and_unreadable_are_not_empty_success()
    {
        Assert.False((await ConductorObligationProjection.ReadAsync(TestContext.Current.CancellationToken))["available"]!.GetValue<bool>());
        Snapshot();
        Assert.True((await ConductorObligationProjection.ReadAsync(TestContext.Current.CancellationToken))["available"]!.GetValue<bool>());
        File.WriteAllText(BatonPaths.ConductorObligationsFile, "{broken");
        Assert.False((await ConductorObligationProjection.ReadAsync(TestContext.Current.CancellationToken))["available"]!.GetValue<bool>());
        Assert.Equal("{broken", File.ReadAllText(BatonPaths.ConductorObligationsFile));
        File.Delete(BatonPaths.ConductorObligationsFile);
        Directory.CreateDirectory(BatonPaths.ConductorObligationsFile);
        Assert.False((await ConductorObligationProjection.ReadAsync(TestContext.Current.CancellationToken))["available"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Actual_quarantined_facts_are_visible_without_repairing_any_files()
    {
        Snapshot(Row(ConductorObligationStatus.ActionObserved));
        var log = FleetEventLog.OpenOperational();
        await log.Append(new FleetEventDraft(FleetEventKind.ConductorObligationPending, "malformed",
            DateTimeOffset.UtcNow, ObligationIdempotencyKey: "key"), TestContext.Current.CancellationToken);
        var snapshotBefore = File.ReadAllBytes(BatonPaths.ConductorObligationsFile);
        var eventsBefore = File.ReadAllBytes(BatonPaths.FleetEventsFile);
        var view = await ConductorObligationProjection.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(view["available"]!.GetValue<bool>());
        Assert.Equal(1, view["quarantinedCount"]!.GetValue<int>());
        Assert.Empty(view["rows"]!.AsArray());
        Assert.Equal(snapshotBefore, File.ReadAllBytes(BatonPaths.ConductorObligationsFile));
        Assert.Equal(eventsBefore, File.ReadAllBytes(BatonPaths.FleetEventsFile));
    }

    [Fact]
    public async Task Fleet_writer_attaches_section_to_real_projection()
    {
        Snapshot(Row(ConductorObligationStatus.TransportAcknowledged));
        using var document = JsonDocument.Parse(await new FleetProjectionWriter()
            .BuildProjectionJsonAsync(TestContext.Current.CancellationToken));
        Assert.Equal("TransportAcknowledged", document.RootElement.GetProperty("conductorObligations")
            .GetProperty("rows")[0].GetProperty("status").GetString());
    }

    [Fact]
    public void Quarantine_cannot_publish_completed_row_or_private_diagnostics()
    {
        var view = ConductorObligationProjection.Project(new([Row(ConductorObligationStatus.ActionObserved)],
            new Dictionary<string, string> { ["key"] = "C:\\secret\\corrupt" }));
        Assert.Empty(view["rows"]!.AsArray());
        Assert.Equal(1, view["quarantinedCount"]!.GetValue<int>());
        Assert.Equal(0, view["completedCount"]!.GetValue<int>());
        Assert.DoesNotContain("secret", view.ToJsonString());
    }

    [Fact]
    public void Row_cap_prioritizes_unresolved_and_retains_total_counts()
    {
        var rows = Enumerable.Range(0, 105).Select(index => Row(ConductorObligationStatus.ActionObserved) with
        { ObligationId = index.ToString(), IdempotencyKey = index.ToString() }).ToList();
        rows.Add(Row(ConductorObligationStatus.Blocked) with { Owner = "secret-owner", RequestedAction = "secret-action" });
        var view = ConductorObligationProjection.Project(new(rows, new Dictionary<string, string>()));
        Assert.Equal(100, view["rows"]!.AsArray().Count);
        Assert.Equal("Blocked", view["rows"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(105, view["completedCount"]!.GetValue<int>());
        Assert.Equal(6, view["omittedCount"]!.GetValue<int>());
        Assert.DoesNotContain("secret", view.ToJsonString());
    }

    [Theory]
    [InlineData(ConductorObligationStatus.Blocked, "the open obligation is not a deterministic queue continuation", "Invalid continuation identity")]
    [InlineData(ConductorObligationStatus.Blocked, "durable queue evidence contains more than one continuation for the obligation", "Conflicting continuation records")]
    [InlineData(ConductorObligationStatus.Blocked, "the source attempt is no longer present and no continuation queue evidence exists", "Source attempt missing; no continuation recorded")]
    [InlineData(ConductorObligationStatus.Unsupported, "the adapter does not support this obligation", "Adapter capability unavailable")]
    [InlineData(ConductorObligationStatus.Blocked, "C:\\secret\\reason", "Cause withheld; inspect local evidence for details")]
    [InlineData(ConductorObligationStatus.Unsupported, "the adapter does not support this obligation secret-token", "Cause withheld; inspect local evidence for details")]
    public void Known_causes_are_distinct_but_unknown_or_suffixed_causes_are_withheld(
        ConductorObligationStatus status, string cause, string expected)
    {
        var view = ConductorObligationProjection.Project(new([Row(status) with { Reason = cause }],
            new Dictionary<string, string>()));
        Assert.Equal(expected, view["rows"]![0]!["reason"]!.GetValue<string>());
        Assert.DoesNotContain("secret", view.ToJsonString());
    }
}
