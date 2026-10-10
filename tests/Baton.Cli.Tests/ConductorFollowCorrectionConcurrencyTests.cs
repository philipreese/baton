using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Accounting;
using Baton.Conductor;
using Baton.Queue;
using Baton.Runway;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests;

public sealed partial class ConductorFollowDeliveryTests
{
    private static async Task<(RepositoryIdentity Identity, string Workspace, ConductorFollowAttachment Attachment)> SecondHostedAsync(Fixture fixture)
    {
        var identity = RepositoryIdentity.From("https://github.com/test/independent-correction", null)!;
        var workspace = Path.Combine(fixture.Root, "workspace-b");
        Directory.CreateDirectory(workspace);
        ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, fixture.CeilingPath);
        await ConductorClaimStore.ClaimAsync(identity, "holder", fixture.Root, cancellationToken: Ct);
        var path = Path.Combine(fixture.Root, "request-b.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ConductorFollowRequest(1, identity.Value,
            workspace, "holder", "codex", "gpt-5.6-luna", "low", 30, "Independent instructions",
            new PermissionGrant(ReadFiles: true)), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var output = new StringWriter();
        await ConductorFollowSession.SetAttachmentAsync(path, true, output, fixture.Root,
            (_, _) => Task.FromResult<RepositoryIdentity?>(identity), Ct, fixture.CorrectionBroker);
        var registration = Path.Combine(fixture.Root, "conductor-follow", identity.FileSlug, "registration.json");
        return (identity, workspace, JsonSerializer.Deserialize<ConductorFollowAttachment>(File.ReadAllText(registration),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
    }

    private static string SecondCorrectionBody(ConductorFollowAttachment attachment, string id) =>
        JsonSerializer.Serialize(new
        {
            repository = attachment.Repository,
            holder = attachment.Holder,
            claimGeneration = attachment.ClaimGeneration,
            attachmentId = attachment.Id,
            requestId = id,
            text = "Read this explicit retained correction.",
        });

    private static JsonElement CorrectionRegistration(Fixture fixture)
    {
        var registration = JsonNode.Parse(File.ReadAllText(fixture.RegistrationPath))!.AsObject();
        registration["attachmentId"] = registration["id"]!.DeepClone();
        return JsonSerializer.SerializeToElement(registration);
    }

    [Fact]
    public async Task Issue2666_A_blocked_broker_does_not_occupy_B_acquisition_delivery_capacity()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        var second = await SecondHostedAsync(fixture);
        await using var glass = await GlassFixture.StartAsync(fixture);
        // Glass may show two claims; derive A's exact registration without relying on card ordering.
        var a = CorrectionRegistration(fixture);
        var bodyA = CorrectionBody(a, "A");
        await ConductorFollowSession.CorrectFromGlassAsync(bodyA, fixture.Root, Operator, Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        fixture.WaitInBroker = async token =>
        {
            if (Interlocked.Increment(ref first) != 1) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var scheduler = fixture.Scheduler();
        scheduler.FollowRepositoryResolver = (workspace, _) => Task.FromResult<RepositoryIdentity?>(
            workspace == second.Workspace ? second.Identity : Identity);
        await scheduler.TickOnceAsync(Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); // wait-ok: Offline acquisition-A rendezvous.
            var acceptedB = await ConductorFollowSession.CorrectFromGlassAsync(SecondCorrectionBody(second.Attachment, "B"),
                fixture.Root, Operator, Ct);
            Assert.Equal("accepted", acceptedB.Outcome);
            await scheduler.TickOnceAsync(Ct);
            var watch = Stopwatch.StartNew();
            while ((await LookupCorrectionAsync(fixture, "B")).Receipt?.State != "delivered")
            {
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "B must deliver while A remains blocked.");
                await Task.Delay(10, Ct); // wait-ok: Bounded independent-acquisition result observation.
            }
            Assert.False(release.Task.IsCompleted);
            Assert.Equal("issued", (await LookupCorrectionAsync(fixture, "A")).Receipt!.State);
            Assert.Equal(2, fixture.Calls.Count);
        }
        finally { release.TrySetResult(); }
        await WaitForAdvicePassAsync(scheduler);
        Assert.Equal("delivered", (await LookupCorrectionAsync(fixture, "A")).Receipt!.State);
        await scheduler.DrainStoppedWorkAdviceAsync();
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("malformed")]
    [InlineData("empty")]
    [InlineData("readable")]
    public async Task Issue2673_Retained_claim_enumeration_refuses_and_recovers_on_existing_tick(string authority)
    {
        using var fixture = await Fixture.CreateAsync();
        await DaemonSettingsStore.SaveAsync(new DaemonSettings { RunwayHold = new RunwayHoldSettings { ReservationPolicy = "off" } }, BatonPaths.SettingsFile, Ct);
        await fixture.CommandAsync("attach");
        var accepted = await ConductorFollowSession.CorrectFromGlassAsync(
            CorrectionBody(CorrectionRegistration(fixture), "enumeration-recovery"), fixture.Root, Operator, Ct);
        Assert.Equal("queued", accepted.Receipt!.State);
        var slot = JsonNode.Parse(File.ReadAllText(Path.Combine(SessionDirectory(fixture), "correction.json")))!;
        var sourcePath = Path.Combine(SessionDirectory(fixture), "correction-inputs", slot["eventId"]!.GetValue<string>() + ".json");
        var source = await File.ReadAllBytesAsync(sourcePath, Ct);
        // An unrelated claim isolates global enumeration from the correction target's own fences.
        var other = RepositoryIdentity.From("https://github.com/test/enumeration-refusal", null)!;
        await ConductorClaimStore.ClaimAsync(other, "holder", fixture.Root, cancellationToken: Ct);
        var claimPath = Path.Combine(fixture.Root, other.FileSlug, BatonPaths.ConductorClaimFileName);
        var original = await File.ReadAllBytesAsync(claimPath, Ct);
        if (authority is "malformed" or "empty")
            await File.WriteAllTextAsync(claimPath, authority == "malformed" ? "{" : "", Ct);
        var refusedBytes = authority == "locked" ? original : await File.ReadAllBytesAsync(claimPath, Ct);
        using var scheduler = fixture.Scheduler();
        FileStream? claimLock = authority == "locked"
            ? new FileStream(claimPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        try
        {
            await scheduler.TickOnceAsync(Ct);
            if (authority == "readable")
            {
                await WaitForAdvicePassAsync(scheduler);
                Assert.Single(fixture.Calls);
                Assert.Equal("delivered", (await LookupCorrectionAsync(fixture, "enumeration-recovery")).Receipt!.State);
            }
            else
            {
                await scheduler.TickOnceAsync(Ct);
                Assert.Empty(await scheduler.SnapshotHostedTasksAsync());
                Assert.Empty(fixture.Calls);
                Assert.Equal("queued", (await LookupCorrectionAsync(fixture, "enumeration-recovery")).Receipt!.State);
                Assert.False(File.Exists(CorrectionEvidence(fixture, "launch.json")));
                Assert.Equal(source, await File.ReadAllBytesAsync(sourcePath, Ct));
            }
        }
        finally { claimLock?.Dispose(); }
        Assert.Equal(refusedBytes, await File.ReadAllBytesAsync(claimPath, Ct));
        await File.WriteAllBytesAsync(claimPath, original, Ct);
        await scheduler.TickOnceAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        Assert.Equal("delivered", (await LookupCorrectionAsync(fixture, "enumeration-recovery")).Receipt!.State);
        await scheduler.TickOnceAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        Assert.Single(fixture.Calls);
        Assert.Equal(source, await File.ReadAllBytesAsync(sourcePath, Ct));
        Assert.False(File.Exists(CorrectionEvidence(fixture, "decision.json")));
        await scheduler.DrainStoppedWorkAdviceAsync();
    }

    [Theory]
    [InlineData("source", false)]
    [InlineData("slot", false)]
    [InlineData("acceptance", true)]
    [InlineData("marker", true)]
    [InlineData("response", true)]
    [InlineData("delivery", true)]
    public async Task Issue2666_Process_exit_at_each_publication_cut_retains_exact_disposition(string stage, bool committed)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        var registration = CorrectionRegistration(fixture);
        var body = CorrectionBody(registration, "process-cut");
        File.WriteAllText(Path.Combine(fixture.Root, "correction-body.json"), body);
        if (stage is "marker" or "response" or "delivery")
            await ConductorFollowSession.CorrectFromGlassAsync(body, fixture.Root, Operator, Ct);
        using var process = StartCorrectionFixtureProcess(fixture.Root, stage);
        var (stdout, stderr) = await BoundedProcessWait.RunToExitAsync(process, TimeSpan.FromSeconds(30), Ct);
        Assert.True(process.ExitCode == 71, stdout + stderr);
        var lookup = await LookupCorrectionAsync(fixture, "process-cut");
        Assert.Equal(committed ? "accepted" : "absent", lookup.Outcome);
        if (!committed)
        {
            Assert.Null(await fixture.DeliverCorrectionAsync());
            await ConductorFollowSession.CorrectFromGlassAsync(body, fixture.Root, Operator, Ct);
        }
        var delivered = await fixture.DeliverCorrectionAsync();
        if (stage == "marker")
        {
            Assert.Equal("uncertain", delivered!.Status);
            Assert.Equal("uncertain", (await LookupCorrectionAsync(fixture, "process-cut")).Receipt!.State);
            Assert.Empty(fixture.Calls);
        }
        else
        {
            Assert.Contains(delivered!.Status, new[] { "delivered", "replayed" });
            Assert.Equal("delivered", (await LookupCorrectionAsync(fixture, "process-cut")).Receipt!.State);
            Assert.Equal(stage is "response" or "delivery" ? 0 : 1, fixture.Calls.Count);
        }
        Assert.False(File.Exists(CorrectionEvidence(fixture, "decision.json")));
    }

    private static Process StartCorrectionFixtureProcess(string root, string mode)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(ConductorFollowDeliveryTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add("*Issue2666_Correction_process_fixture");
        start.Environment["BATON_CORRECTION_FIXTURE_ROOT"] = root;
        start.Environment["BATON_CORRECTION_FIXTURE_MODE"] = mode;
        return global::Baton.Core.ProcessLaunch.Start(start)!;
    }

    [Fact]
    public async Task Issue2666_Correction_process_fixture()
    {
        var root = Environment.GetEnvironmentVariable("BATON_CORRECTION_FIXTURE_ROOT");
        if (root is null) return;
        using var fixture = new Fixture(root);
        var stage = Environment.GetEnvironmentVariable("BATON_CORRECTION_FIXTURE_MODE")!;
        ConductorFollowSession.AfterCorrectionPersistence = current =>
        {
            if (current == stage) Environment.Exit(71); // No finally/cleanup runs at the injected process cut.
        };
        var body = File.ReadAllText(Path.Combine(root, "correction-body.json"));
        if (stage is "marker" or "response" or "delivery") await fixture.DeliverCorrectionAsync();
        else await ConductorFollowSession.CorrectFromGlassAsync(body, root, Operator, Ct);
        Assert.Fail("The selected durable publication cut was not reached.");
    }

    [Fact]
    public async Task Issue2666_Cross_target_publishers_share_issuer_request_ID_serialization()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        var second = await SecondHostedAsync(fixture);
        var firstRegistration = CorrectionRegistration(fixture);
        var firstBody = CorrectionBody(firstRegistration, "shared-request");
        var secondBody = SecondCorrectionBody(second.Attachment, "shared-request");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slotCount = 0;
        ConductorFollowSession.AfterCorrectionPersistence = stage =>
        {
            if (stage == "slot" && Interlocked.Increment(ref slotCount) == 1)
            {
                entered.TrySetResult();
                release.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct).GetAwaiter().GetResult(); // wait-ok: Cross-target publication rendezvous.
            }
        };
        try
        {
            var first = ConductorFollowSession.CorrectFromGlassAsync(firstBody, fixture.Root, Operator, Ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); // wait-ok: First publisher holds the queue serialization.
            var secondPublish = ConductorFollowSession.CorrectFromGlassAsync(secondBody, fixture.Root, Operator, Ct);
            release.TrySetResult();
            Assert.Equal("accepted", (await first).Outcome);
            await Assert.ThrowsAsync<CliArgumentException>(() => secondPublish);
            var replay = await ConductorFollowSession.CorrectFromGlassAsync(firstBody, fixture.Root, Operator, Ct);
            Assert.True(replay.Replayed);
            Assert.Equal(Repository, replay.Receipt!.Repository);
            Assert.Equal(1, slotCount);
        }
        finally { release.TrySetResult(); ConductorFollowSession.AfterCorrectionPersistence = null; }
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task Issue2666_Receipt_lookup_remains_exact_beyond_100_events_and_after_takeover()
    {
        using var fixture = await Fixture.CreateAsync();
        await DaemonSettingsStore.SaveAsync(new DaemonSettings { RunwayHold = new RunwayHoldSettings { ReservationPolicy = "off" } }, BatonPaths.SettingsFile, Ct);
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var original = await AcceptCorrectionAsync(fixture, glass, "first-history");
        Assert.Null(ConductorFollowSession.AfterCorrectionPersistence);
        Assert.Equal("delivered", (await fixture.DeliverCorrectionAsync())!.Status);
        var registration = CorrectionRegistration(fixture);
        for (var index = 0; index < 101; index++)
        {
            Assert.Equal("accepted", (await ConductorFollowSession.CorrectFromGlassAsync(CorrectionBody(registration, "history-" + index), fixture.Root, Operator, Ct)).Outcome);
            var delivered = (await fixture.DeliverCorrectionAsync())!;
            Assert.True(delivered.Status == "delivered", "History index " + index + ": " + delivered.Status + "; " + delivered.Diagnostic + "; calls " + fixture.Calls.Count);
        }
        using var takeover = await glass.ControlAsync("takeover", ControlBody(await glass.StatusAsync(), "takeover", "history-owner"));
        Assert.Equal(System.Net.HttpStatusCode.OK, takeover.StatusCode);
        var found = await LookupCorrectionAsync(fixture, "first-history");
        Assert.Equal(original.Receipt!.AcceptedAt, found.Receipt!.AcceptedAt);
        Assert.Equal("delivered", found.Receipt.State);
        Assert.Equal(102, fixture.Calls.Count);
    }
}
