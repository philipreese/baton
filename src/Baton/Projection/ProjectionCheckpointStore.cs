using System.Text.Json;
using Baton.Store;

namespace Baton.Projection;

/// <summary>
/// Persistence store for room projection checkpoints (#903 Scope 1).
/// Checkpoints are saved under <c>.baton/checkpoint.json</c> inside the room directory.
/// </summary>
public static class ProjectionCheckpointStore
{
    private const string BatonDirectoryName = ".baton";
    private const string CheckpointFileName = "checkpoint.json";

    /// <summary>
    /// #2069: which checkpoint files have already had a rejection reported, and how many identical
    /// repeats have been swallowed since. Process-lifetime only, keyed per file — a daemon restart
    /// re-reports, deliberately, for the same reason
    /// <c>RoomRetentionSweep.LegacyTerminalInstantWarnedRooms</c> gives: the operator reading the new
    /// process's stderr has not necessarily seen the old one's. That field's remarks are where the
    /// noise rule itself is recorded; this is a second site obeying it, not a second statement of it.
    /// <para>
    /// <b>The reason is part of the value, not just the key's path.</b> A checkpoint whose rejection
    /// reason CHANGES is a different fallback, and suppressing it would hide the next real one — which
    /// is the harm the memo exists to prevent, not to cause. That re-emission is also the only place a
    /// suppressed count is surfaced: there is no daemon flush or shutdown hook to print a census from,
    /// so a room that is rejected identically forever costs exactly one line and its repeat count is
    /// never printed. Deliberate — see this issue's PR body.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, (string Reason, int Suppressed)> ReportedRejections =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Guards <see cref="ReportedRejections"/>. A lock rather than a <c>ConcurrentDictionary</c>
    /// because the update is read-modify-write on a counter, which
    /// <c>ConcurrentDictionary.AddOrUpdate</c>'s factory may re-run under contention; <see cref="Load"/>
    /// runs once per room per projection tick, so the lock costs nothing worth measuring.
    /// </summary>
    private static readonly object ReportedRejectionsLock = new();

    public static string GetCheckpointFilePath(string roomDirectoryPath)
        => Path.Combine(roomDirectoryPath, BatonDirectoryName, CheckpointFileName);

    /// <summary>
    /// Loads the projection checkpoint from <paramref name="roomDirectoryPath"/> if present and valid.
    /// If the file is missing, corrupt, or unparseable, logs LOUDLY to <see cref="Console.Error"/>
    /// and returns <c>null</c> to trigger full event log replay.
    /// </summary>
    public static ProjectionCheckpoint? Load(string roomDirectoryPath)
    {
        if (string.IsNullOrEmpty(roomDirectoryPath))
        {
            return null;
        }

        var filePath = GetCheckpointFilePath(roomDirectoryPath);
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var checkpoint = JsonSerializer.Deserialize<ProjectionCheckpoint>(json, FlowEventLogJson.Options);
            // N3 (#1664 re-review), bumped again by #1877: < 5, not < 4 — see
            // ProjectionCheckpoint.Version's own remarks for why each older file must force a full
            // replay rather than deserialize as usable.
            if (checkpoint is null || checkpoint.Version < ProjectionCheckpoint.CurrentVersion || checkpoint.EventOffset < 0 || checkpoint.ByteOffset < 0 ||
                checkpoint.State is null || checkpoint.State.SucceededExecutionIds is null || checkpoint.State.AcceptedRequestByExecutionId is null ||
                checkpoint.State.CoreStartedExecutionIds is null || checkpoint.State.CoreExitedByExecutionId is null)
            {
                ReportFallbackLoudly(filePath, $"Checkpoint file '{filePath}' is missing version/aggregates or has negative offset.");
                return null;
            }

            return checkpoint;
        }
        catch (Exception ex)
        {
            ReportFallbackLoudly(filePath, $"Failed to load checkpoint from '{filePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reports one rejection of <paramref name="filePath"/> on stderr, at most once per
    /// (file, <paramref name="reason"/>) per process — see <see cref="ReportedRejections"/> for why
    /// the reason is part of that pair and what is deliberately never printed. The fallback itself is
    /// unconditional: <see cref="Load"/> returns <c>null</c> and the caller replays in full on every
    /// call, reported or suppressed. Only the line is rate-limited.
    /// </summary>
    private static void ReportFallbackLoudly(string filePath, string reason)
    {
        int suppressedBeforeReasonChanged;
        bool firstReportForThisFile;

        lock (ReportedRejectionsLock)
        {
            if (ReportedRejections.TryGetValue(filePath, out var prior))
            {
                if (string.Equals(prior.Reason, reason, StringComparison.Ordinal))
                {
                    ReportedRejections[filePath] = (prior.Reason, prior.Suppressed + 1);
                    return;
                }

                suppressedBeforeReasonChanged = prior.Suppressed;
                firstReportForThisFile = false;
            }
            else
            {
                suppressedBeforeReasonChanged = 0;
                firstReportForThisFile = true;
            }

            ReportedRejections[filePath] = (reason, 0);
        }

        // Stated on the line itself rather than left to a runbook: an operator who sees one line and
        // knows nothing of this memo would otherwise read it as "this happened once". "Process", not
        // "daemon process": Load also runs under `baton status` and `baton run`, and a person reading
        // this line there must not conclude the rate limit is somebody else's concern.
        var scope = firstReportForThisFile
            ? "Reported once per checkpoint file per process; identical repeats are suppressed."
            : $"Reported once per checkpoint file per process; this file's previous rejection was "
              + $"reported once and suppressed {suppressedBeforeReasonChanged} further time(s) before the reason changed to this one.";

        Console.Error.WriteLine($"[ProjectionCheckpoint] Fallback to full replay LOUDLY: {reason} {scope}");
    }

    /// <summary>
    /// Persists <paramref name="checkpoint"/> to <c>.baton/checkpoint.json</c> within <paramref name="roomDirectoryPath"/>.
    /// Assumes the caller holds the room directory's concurrency guard.
    /// </summary>
    public static void Save(string roomDirectoryPath, ProjectionCheckpoint checkpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(checkpoint);

        var batonDir = Path.Combine(roomDirectoryPath, BatonDirectoryName);
        Directory.CreateDirectory(batonDir);

        var filePath = GetCheckpointFilePath(roomDirectoryPath);
        var json = JsonSerializer.Serialize(checkpoint, FlowEventLogJson.Options);

        var tempFilePath = filePath + ".tmp." + Guid.NewGuid().ToString("n");
        File.WriteAllText(tempFilePath, json);
        RetryingFileMove.Move(tempFilePath, filePath, overwrite: true);
    }

    /// <summary>
    /// Deletes the checkpoint file if present. Used for determinism testing and forced full replays.
    /// </summary>
    public static void Delete(string roomDirectoryPath)
    {
        if (string.IsNullOrEmpty(roomDirectoryPath))
        {
            return;
        }

        var filePath = GetCheckpointFilePath(roomDirectoryPath);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}
