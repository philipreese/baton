using System.Diagnostics;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.CrashTestHost;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests.Daemon;

public sealed class ReadinessCrashRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "baton-readiness-crash-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Key = "owned-readiness:crash-window";
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public ReadinessCrashRecoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_root);

    [Theory]
    [InlineData("BeforeLaunchMarker", false, false, false, 0, true)]
    [InlineData("AfterLaunchMarker", true, false, false, 0, false)]
    [InlineData("AfterResponse", true, true, false, 1, true)]
    [InlineData("AfterAcknowledgement", true, true, true, 1, true)]
    public async Task Killed_controller_recovers_at_each_durable_boundary_without_duplicate_provider_call(
        string cut, bool markerExists, bool responseExists, bool receiptExists,
        int priorLaunches, bool recoverySucceeds)
    {
        await Store().EnqueueAsync(Request(), Ct);
        var signal = Path.Combine(_root, "cut.signal");
        var release = Path.Combine(_root, "cut.release");
        var launches = Path.Combine(_root, "provider-launches.bin");
        using (var child = StartHost(cut, signal, release, launches))
        {
            try
            {
                await WaitForFileAsync(signal);
                Assert.False(child.HasExited);
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(Ct);
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
            }
        }

        var evidence = Store().GetReadinessEvidenceDirectory(Key);
        Assert.Equal(markerExists, File.Exists(Path.Combine(evidence, "launch.json")));
        Assert.Equal(responseExists, File.Exists(Path.Combine(evidence, "response.json")));
        Assert.Equal(receiptExists, File.Exists(Path.Combine(evidence, "receipt.json")));
        Assert.Equal(priorLaunches, LaunchCount(launches));

        var recoveryLaunches = 0;
        Task<RetainedReadinessResponse> Recover(ConductorObligation obligation, CancellationToken _)
        {
            recoveryLaunches++;
            return Task.FromResult(Response(obligation));
        }

        if (recoverySucceeds)
        {
            var recovered = await Store().DecideReadinessOnceAsync(Key, Recover, Ct);
            Assert.Equal(ConductorObligationStatus.TransportAcknowledged, recovered.Obligation.Status);
            Assert.True(File.Exists(Path.Combine(evidence, "receipt.json")));
            Assert.Equal(cut == "BeforeLaunchMarker" ? 1 : 0, recoveryLaunches);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
                Store().DecideReadinessOnceAsync(Key, Recover, Ct));
            Assert.Contains("uncertain", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, recoveryLaunches);
        }

        Assert.Equal(priorLaunches, LaunchCount(launches));
    }

    [Fact]
    public async Task Two_controller_processes_admit_one_provider_launch_and_both_get_same_receipt()
    {
        await Store().EnqueueAsync(Request(), Ct);
        var firstSignal = Path.Combine(_root, "first.signal");
        var firstRelease = Path.Combine(_root, "first.release");
        var secondSignal = Path.Combine(_root, "second.signal");
        var launches = Path.Combine(_root, "provider-launches.bin");
        using var first = StartHost("AfterLaunchMarker", firstSignal, firstRelease, launches);
        try
        {
            await WaitForFileAsync(firstSignal);
            using var second = StartHost("None", secondSignal, Path.Combine(_root, "unused.release"), launches);
            try
            {
                await WaitForFileAsync(secondSignal + ".ready");
                // wait-ok: prove the second OS process cannot finish while the first owns admission.
                Assert.False(second.WaitForExit(500));
                Assert.Equal(0, LaunchCount(launches));
                await File.WriteAllTextAsync(firstRelease, "release", Ct);
                await WaitForExitAsync(first);
                await WaitForExitAsync(second);
                Assert.Equal(0, first.ExitCode);
                Assert.Equal(0, second.ExitCode);
            }
            finally
            {
                if (!second.HasExited) second.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            if (!first.HasExited) first.Kill(entireProcessTree: true);
        }

        Assert.Equal(1, LaunchCount(launches));
        var acknowledged = await Store().ReadAsync(Key, Ct);
        Assert.Equal(ConductorObligationStatus.TransportAcknowledged, acknowledged!.Status);
        Assert.StartsWith("readiness-sha256:", acknowledged.TransportReceipt, StringComparison.Ordinal);
        var replay = await Store().DecideReadinessOnceAsync(Key,
            (_, _) => throw new InvalidOperationException("A third launch would be a duplicate."), Ct);
        Assert.Equal(acknowledged.TransportReceipt, replay.Obligation.TransportReceipt);
        Assert.Equal(1, LaunchCount(launches));
    }

    private Process StartHost(string cut, string signal, string release, string launches)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { typeof(Scenarios).Assembly.Location, "readiness-decide",
            _root, Key, cut, signal, release, launches })
            start.ArgumentList.Add(argument);
        return global::Baton.Core.ProcessLaunch.Start(start) ?? throw new InvalidOperationException("Could not start readiness crash host.");
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
            await Task.Delay(20, Ct); // wait-ok: bounded process rendezvous with a 20-second failure ceiling
        Assert.True(File.Exists(path), $"Crash host did not reach '{path}'.");
    }

    private static async Task WaitForExitAsync(Process process)
    {
        // wait-ok: a hung crash probe fails the test rather than hanging the suite.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        var stderr = await process.StandardError.ReadToEndAsync(Ct);
        Assert.True(process.ExitCode == 0, $"Crash host exited {process.ExitCode}: {stderr}");
    }

    private static int LaunchCount(string path) => File.Exists(path) ? checked((int)new FileInfo(path).Length) : 0;

    private ConductorObligationStore Store() => new(new FleetEventLog(
        Path.Combine(_root, "events.jsonl"), Path.Combine(_root, "events.1.jsonl"), 1_000_000),
        Path.Combine(_root, "conductor-obligations.json"));

    private static ConductorObligationRequest Request() => new(Key, "github.com/philipreese/baton",
        null, null, null, "readiness-decision", "owner-one", DateTimeOffset.UnixEpoch,
        "codex-subscription-cli", "one-shot-readiness", true,
        TargetWorkspace: Path.GetTempPath(), TargetRevision: Revision, ContextSha256: Digest);

    private static RetainedReadinessResponse Response(ConductorObligation item) => new(
        new ReadinessDecision(item.ObligationId, item.TargetProject, item.TargetRevision!, item.ContextSha256!,
            ReadinessChoice.Hold, "Fake provider held for crash-window proof."),
        new ReadinessUsage(1, 1, 0), "codex-subscription-cli", "gpt-5.6-luna", "low",
        DateTimeOffset.UtcNow);
}
