using System.Security.Cryptography;
using System.Text;
using Baton.Vendors;
using Baton.Concurrency;
using Baton.Status;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Baton.Cli.Daemon;

public static class DaemonHost
{
    public static Task RunDaemonAsync(string[] args) => RunDaemonAsync(args, onHostBuilt: null);

    /// <summary>
    /// The daemon singleton mutex's name, scoped by the resolved storage root (<see cref="BatonPaths.Root"/>)
    /// rather than just <see cref="Environment.UserName"/> (#1773). Two daemons under two different homes on
    /// the same account — e.g. the operator's real <c>~/.baton</c> and a test's temp home via
    /// <see cref="BatonEnvironmentSnapshot.BeginScope"/> — must never contend for the same OS mutex; a
    /// username-only name made every test that skipped <c>--no-mutex</c> collide with (or seize) whatever
    /// the operator's own daemon held. The root is hashed rather than embedded verbatim so the name stays a
    /// bounded, filesystem-path-free token regardless of how long or unusual the root is.
    /// </summary>
    internal static string MutexName(string root)
    {
        // Lower-cased before hashing: BatonPaths.RecordKeyComparer is OrdinalIgnoreCase precisely
        // because Windows paths are case-insensitive (BatonPaths.cs remarks) -- hashing the
        // case-preserving RecordKey would let two casings of the same home resolve to two mutex
        // names, i.e. two daemons, which is the exact under-locking that comparer exists to prevent.
        var key = BatonPaths.RecordKey(root).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return $"Global\\BatonDaemonMutex_{Environment.UserName}_{hash}";
    }

    /// <summary>Test-only seam (Baton.Cli.Tests, via <c>InternalsVisibleTo</c>): <paramref name="onHostBuilt"/>
    /// runs after the host is built but before <c>RunAsync</c>, so a test can inspect DI registrations and/or
    /// register a stop trigger. Without it, <c>RunAsync</c> blocks until an external process signal that never
    /// arrives in-process, so a test calling this method directly would hang forever.</summary>
    internal static async Task RunDaemonAsync(string[] args, Action<IHost>? onHostBuilt)
    {
        var noMutex = args.Contains("--no-mutex");
        Mutex? mutex = null;
        if (!noMutex)
        {
            mutex = new Mutex(true, MutexName(BatonPaths.Root), out var createdNew);
            if (!createdNew)
            {
                Console.WriteLine("Another instance of the Baton daemon is already running.");
                mutex.Dispose();
                return;
            }
        }

        // Setup local data directory ~/.baton
        var batonDir = BatonPaths.Root;
        Directory.CreateDirectory(batonDir);

        // #1298: daemon-wide settings (currently just the concurrency caps) apply from the moment
        // the daemon comes up, before any room can dispatch a turn.
        var daemonSettings = await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile);
        ConcurrencySlotGate.SetCaps(daemonSettings.GlobalConcurrencyCap, daemonSettings.PerVendorConcurrencyCap);

        var builder = Host.CreateApplicationBuilder(args);

        // #1659: DI-injected into RoomRetentionSweep below so it can read RoomsRetentionDays without a
        // second settings load — the same daemonSettings already loaded for the concurrency caps above.
        builder.Services.AddSingleton(daemonSettings);

        // #1025: room retention sweep (journal compaction)
        builder.Services.AddHostedService<RoomRetentionSweep>();

        // #1488: WatchSweep -- baton watch's firing half. Contract: spec/baton.md §2.
        builder.Services.AddHostedService<WatchSweep>();
        // #1557: writes BatonPaths.FleetProjectionFile every ~30s -- spec/baton.md §7's fourth kept
        // daemon responsibility, outbound-only (no listener added).
        builder.Services.AddHostedService<FleetProjectionWriter>();

        // #1391: per-vendor /usage harvester -- cadence-gated, outbound-only, persists to
        // BatonPaths.VendorUsageSnapshotFile for FleetProjectionWriter/FleetStatusTool to read back.
        builder.Services.AddHostedService<VendorUsageHarvester>();

        // #734: gh-backed delivery poll (branch/PR -> checks -> merged), spec/baton.md §7's fifth
        // kept daemon responsibility, outbound-only (reads GitHub via gh, writes flow.jsonl, never
        // acts on what it observes).
        builder.Services.AddHostedService<DeliveryPoller>();

        // #1934 slice 1: the conductor queue's scheduler -- the only thing that launches a queued item,
        // hosted here beside the usage harvester and the projection writer (Q1 answer (b)). Reads
        // settings.json's `Queue` block on every tick rather than the daemonSettings captured above, so
        // a policy change (a floor, the cap, the tier table) takes effect without a daemon restart.
        builder.Services.AddHostedService<QueueSchedulerService>();

        // #1981: the self-watchdog, registered LAST so its supervision thread starts after every
        // service it watches has had its StartAsync run -- DaemonWatchdog's own doc comment carries
        // what it trips on, what it deliberately does not, and why it does not run on the thread pool.
        builder.Services.AddHostedService<DaemonWatchdog>();

        var host = builder.Build();
        onHostBuilt?.Invoke(host);
        await host.RunAsync();
        mutex?.Dispose();
    }
}
