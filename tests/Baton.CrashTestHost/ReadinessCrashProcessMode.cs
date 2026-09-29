using Baton.Cli.Daemon;
using Baton.Conductor;

namespace Baton.CrashTestHost;

/// <summary>
/// Readiness-only crash mode, intentionally outside the apphost's top-level Main method. Older
/// hermetic git/gh fixture copies omit Baton.Cli.dll; they must not load this method at startup.
/// </summary>
internal static class ReadinessCrashProcessMode
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not ["readiness-decide", var root, var key, var cutName,
            var signal, var release, var launchesFile])
            throw new ArgumentException("Invalid readiness crash probe arguments.", nameof(args));

        var store = new ConductorObligationStore(new FleetEventLog(
            Path.Combine(root, "events.jsonl"), Path.Combine(root, "events.1.jsonl"), 1_000_000),
            Path.Combine(root, "conductor-obligations.json"));
        if (cutName != "None")
        {
            var cut = Enum.Parse<ReadinessDurabilityPoint>(cutName);
            store.ReadinessDurabilityObserver = point =>
            {
                if (point != cut) return;
                File.WriteAllText(signal, point.ToString());
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (!File.Exists(release) && DateTime.UtcNow < deadline)
                    Thread.Sleep(10); // wait-ok: parent releases or kills this bounded crash probe
                if (!File.Exists(release)) throw new TimeoutException("Readiness crash cut was not released.");
            };
        }

        File.WriteAllText(signal + ".ready", "ready");
        var result = await store.DecideReadinessOnceAsync(key, (obligation, _) =>
        {
            using (var launchRecord = new FileStream(launchesFile, FileMode.Append, FileAccess.Write,
                FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                launchRecord.WriteByte((byte)'x');
                launchRecord.Flush(flushToDisk: true);
            }

            return Task.FromResult(new RetainedReadinessResponse(
                new ReadinessDecision(obligation.ObligationId, obligation.TargetProject,
                    obligation.TargetRevision!, obligation.ContextSha256!, ReadinessChoice.Hold,
                    "Fake provider held for crash-window proof."),
                new ReadinessUsage(1, 1, 0), "codex-subscription-cli", "gpt-5.6-luna", "low",
                DateTimeOffset.UtcNow));
        });
        await Console.Out.WriteLineAsync(result.Obligation.Status.ToString());
        return 0;
    }
}
