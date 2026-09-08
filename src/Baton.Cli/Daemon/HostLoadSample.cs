using System.Text.Json.Nodes;

namespace Baton.Cli.Daemon;

/// <summary>
/// #2082 — what the daemon's host looked like at one instant: the thread pool's backlog and size, the
/// managed heap, and the working set. <see cref="FleetProjectionWriter"/> puts one in each heartbeat
/// body it writes; <see cref="DaemonWatchdog"/> puts a fresh one in its verdict line (spec/baton.md
/// §7, "The daemon watches itself" — the schema is stated there, once).
/// <para>
/// The incident this exists for is the one <see cref="DaemonWatchdog"/>'s doc dates: the freeze of
/// 2026-09-08. Nothing recorded what the process was doing at the time — a starved pool, a paged-out
/// working set, or a GC under the box's memory pressure all fit the same silence. Each field here is
/// one of those hypotheses made readable after the fact; none of them is a verdict on its own.
/// </para>
/// <para>
/// Every reading is a cheap in-process counter (<see cref="ThreadPool.PendingWorkItemCount"/>,
/// <see cref="ThreadPool.ThreadCount"/>, <see cref="GC.GetTotalMemory(bool)"/> without a collection,
/// <see cref="Environment.WorkingSet"/>): no file, no P/Invoke, nothing that needs a pool thread — so
/// <see cref="DaemonWatchdog"/> can take one on its dedicated thread while the pool is exactly the
/// thing that has wedged.
/// </para>
/// </summary>
internal sealed record HostLoadSample(
    DateTimeOffset SampledAt,
    long ThreadPoolPendingWorkItems,
    int ThreadPoolThreads,
    long GcTotalMemoryBytes,
    long WorkingSetBytes)
{
    /// <summary>Read the live process counters. <paramref name="now"/> is the ledger's clock, not
    /// <see cref="DateTimeOffset.UtcNow"/>, so a fixture-driven ledger stamps fixture time.</summary>
    internal static HostLoadSample Capture(DateTimeOffset now) => new(
        now,
        ThreadPool.PendingWorkItemCount,
        ThreadPool.ThreadCount,
        GC.GetTotalMemory(forceFullCollection: false),
        Environment.WorkingSet);

    /// <summary>The <c>hostLoad</c> object of <c>heartbeat.json</c>. Field names are the schema
    /// spec/baton.md §7 states; a rename here is a spec change.</summary>
    internal JsonObject ToJson() => new()
    {
        ["sampledAt"] = SampledAt.ToString("O"),
        ["threadPoolPendingWorkItems"] = ThreadPoolPendingWorkItems,
        ["threadPoolThreads"] = ThreadPoolThreads,
        ["gcTotalMemoryBytes"] = GcTotalMemoryBytes,
        ["workingSetBytes"] = WorkingSetBytes,
    };

    /// <summary>The inverse of <see cref="ToJson"/>: null when <paramref name="node"/> is not an object
    /// carrying every field, so a reader of an older heartbeat (written before this object existed)
    /// gets "no sample" rather than a half-filled one.</summary>
    internal static HostLoadSample? FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        if (obj["sampledAt"]?.GetValue<string>() is not { } sampledAt
            || !DateTimeOffset.TryParse(sampledAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            || obj["threadPoolPendingWorkItems"] is not { } pending
            || obj["threadPoolThreads"] is not { } threads
            || obj["gcTotalMemoryBytes"] is not { } gc
            || obj["workingSetBytes"] is not { } workingSet)
        {
            return null;
        }

        return new HostLoadSample(
            at, pending.GetValue<long>(), threads.GetValue<int>(), gc.GetValue<long>(), workingSet.GetValue<long>());
    }

    /// <summary>One clause for a log line: the verdict names the pool's state beside the silence it is
    /// diagnosing, because a backlog of hundreds beside zero threads and a backlog of zero say
    /// different things about the same silence.</summary>
    internal string Describe() =>
        $"thread pool {ThreadPoolPendingWorkItems} pending on {ThreadPoolThreads} threads, "
        + $"GC heap {GcTotalMemoryBytes / (1024 * 1024)} MiB, working set {WorkingSetBytes / (1024 * 1024)} MiB";
}
