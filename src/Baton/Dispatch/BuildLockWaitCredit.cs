using System.Text.Json;

namespace Baton.Dispatch;

/// <summary>
/// The machine-wide build lock's queueing, measured by the lane that paid for it, and what it is
/// worth against that lane's box (#2019).
/// </summary>
/// <remarks>
/// <para>
/// The box is a wall clock on the worker process (<see cref="Core.BatonTask.WithTimeout"/>), so under
/// several live lanes it measures the MACHINE and not the worker: every <c>dotnet build</c>/<c>test</c>
/// in this repo runs through <c>tools/buildlock.py</c>, which is one MSBuild at a time across every
/// worktree (#1402), and the four rooms measured on 2026-09-06/07 spent ≈5–6 minutes each merely
/// QUEUED before their clock ran out mid-work — after the edits, before the commit. Crediting the
/// measured wait back means a lane pays for its own work and not for its neighbours'.
/// </para>
/// <para>
/// <c>tools/buildlock.py</c> is the writer: when <see cref="LogEnvironmentVariable"/> is set it
/// appends one <c>{"waitMs": n}</c> line per contended acquire to that path, which
/// <c>ArtifactManager.BuildEnvironment</c> points at <see cref="LogFileName"/> in the execution's own
/// artifact directory. That variable is in the worker's environment, so every command the worker
/// spawns — its own builds and the <c>gates --fast</c> its pre-push hook runs — records into the same
/// file. It is a SEPARATE sink from <c>BATON_BUILDLOCK_WAIT_LOG</c>, which <c>.githooks/pre-push</c>
/// still sets per push and which alone feeds the cost ledger's <c>pushWaitMs</c> (spec/baton.md §7):
/// the two measure different populations and neither reads the other's file.
/// </para>
/// <para>
/// <b>What this cannot see</b>, and the reason it under-credits rather than over-credits: buildlock
/// records a wait when the lock is finally OBTAINED (or when the acquire times out), so a lane sitting
/// eight minutes deep in a wait right now has recorded nothing yet. The credit therefore always trails
/// the true queueing by at most the in-flight wait.
/// </para>
/// </remarks>
public static class BuildLockWaitCredit
{
    /// <summary>
    /// The environment variable naming the per-execution wait log. Read by <c>tools/buildlock.py</c>'s
    /// <c>record_wait</c>; set by <c>ArtifactManager.BuildEnvironment</c> alongside
    /// <c>BATON_OUTPUT_DIR</c>.
    /// </summary>
    public const string LogEnvironmentVariable = "BATON_LOCK_WAIT_LOG";

    /// <summary>The wait log's file name inside the execution's artifact directory.</summary>
    public const string LogFileName = "lock-wait.jsonl";

    /// <summary>
    /// The ceiling, as a multiple of the configured box: a lane may be credited at most this much
    /// total wall clock however much queueing it recorded. The cap is what keeps a corrupt log, or a
    /// genuinely pathological machine, from making a lane immortal — the box stays a bound.
    /// </summary>
    /// <remarks>
    /// <b>Any other clock keyed to a lane's box must clear <see cref="MaxEffectiveTimeout"/>, not the
    /// configured box</b> (#2058 review). A second clock sized as "box + margin" was correct only while
    /// the engine killed at the box; under credit it can expire first and decide the failure mode
    /// instead. Its one live consumer outside this class is <c>Baton.Vendors.AgyWorkerAdapter</c>'s
    /// <c>--print-timeout</c> backstop, which cannot read the running credit at all — the flag is
    /// emitted at argument-resolution time, before the lane starts and so before a millisecond of
    /// queueing has been recorded — so the ceiling, which is knowable then, is what it is derived from.
    /// </remarks>
    public const int MaxBudgetMultiplier = 2;

    /// <summary>
    /// Saturation point for a sum of recorded waits. Well past
    /// <c>DispatchOptionsParser.MaxTimeoutMinutes</c>'s own 24h ceiling, so it can only ever be
    /// reached by a corrupt log — and <see cref="EffectiveTimeout"/> clamps that to the box's own
    /// multiple anyway.
    /// </summary>
    private static readonly long MaxRecordedMs = (long)TimeSpan.FromDays(1).TotalMilliseconds;

    /// <summary>
    /// The queueing recorded in <paramref name="logPath"/> so far, summed across every line.
    /// </summary>
    /// <remarks>
    /// Fails closed in every direction: a missing file (no command has queued yet), an unreadable one,
    /// a line that is not an object carrying a non-negative <c>waitMs</c> number, and a torn final line
    /// an appender is mid-write on all read as no credit rather than as an error. Nothing here may
    /// throw into the timeout monitor that calls it — a measurement that cannot be taken must not
    /// change when a lane is killed.
    /// </remarks>
    public static TimeSpan RecordedWait(string? logPath)
    {
        if (string.IsNullOrEmpty(logPath))
        {
            return TimeSpan.Zero;
        }

        long totalMs = 0;
        try
        {
            // FileShare.ReadWrite | Delete: buildlock.py appends to this file from another process
            // while the run is in flight, and an exclusive open here would fail (crediting nothing)
            // exactly when there is something to credit.
            using var stream = new FileStream(
                logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (TryReadWaitMs(line) is not { } waitMs)
                {
                    continue;
                }

                totalMs = waitMs > MaxRecordedMs - totalMs ? MaxRecordedMs : totalMs + waitMs;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromMilliseconds(totalMs);
    }

    /// <summary>
    /// The wall clock a run is actually allowed, given its configured box and the queueing it has
    /// recorded: <paramref name="box"/> plus <paramref name="credit"/>, capped at
    /// <see cref="MaxBudgetMultiplier"/>× the box, and never shorter than the box itself.
    /// </summary>
    public static TimeSpan EffectiveTimeout(TimeSpan box, TimeSpan credit)
    {
        if (box <= TimeSpan.Zero || credit <= TimeSpan.Zero)
        {
            return box;
        }

        var ceiling = MaxEffectiveTimeout(box);
        var extended = credit.Ticks > TimeSpan.MaxValue.Ticks - box.Ticks
            ? TimeSpan.MaxValue
            : box + credit;

        return extended > ceiling ? ceiling : extended;
    }

    /// <summary>
    /// The longest wall clock <paramref name="box"/> can ever become under credit —
    /// <see cref="MaxBudgetMultiplier"/>× the box, saturating rather than overflowing. This is the
    /// figure any clock outside the engine has to clear; see <see cref="MaxBudgetMultiplier"/>.
    /// </summary>
    public static TimeSpan MaxEffectiveTimeout(TimeSpan box) =>
        box <= TimeSpan.Zero
            ? box
            : box.Ticks > TimeSpan.MaxValue.Ticks / MaxBudgetMultiplier
                ? TimeSpan.MaxValue
                : box * MaxBudgetMultiplier;

    /// <summary>One line's <c>waitMs</c>, or null for anything this reader will not credit.</summary>
    private static long? TryReadWaitMs(string line)
    {
        if (line.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !document.RootElement.TryGetProperty("waitMs", out var value)
                || value.ValueKind is not JsonValueKind.Number
                || !value.TryGetInt64(out var waitMs)
                || waitMs <= 0)
            {
                return null;
            }

            return waitMs;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
