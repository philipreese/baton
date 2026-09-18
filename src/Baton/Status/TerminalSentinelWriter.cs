using System.Text.Json;

namespace Baton.Status;

/// <summary>
/// <c>terminal.json</c> (#1356): written into a room directory the moment its workflow reaches a
/// terminal state, so an agent can watch one file instead of polling <c>baton status</c> prose or
/// racing a process exit. A fifth room-identifying filename beside the four #1271 documents as having
/// no canonical home yet — this one gets a single constant from the day it is introduced rather than
/// repeating that drift, but does not attempt #1271's own broader cleanup (a separate, already-filed
/// concern with its own blast radius).
/// </summary>
public static class TerminalSentinelWriter
{
    public const string TerminalSentinelFileName = "terminal.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Writes <paramref name="view"/> as the room's terminal sentinel. Callers write this <b>last</b> —
    /// after every output an outcome could reference already exists on disk — so a watcher that wakes
    /// on this file's appearance never reads a room mid-write (#1356 point 4).
    /// </summary>
    /// <remarks>
    /// #1374 F2: serializes to a <c>.tmp</c> sibling then <see cref="File.Move(string, string, bool)"/>s
    /// it into place, rather than truncating <paramref name="roomDirectoryPath"/>'s
    /// <see cref="TerminalSentinelFileName"/> directly. <c>WriteIndented</c> makes the payload
    /// multi-line, so a direct truncate-then-write leaves a real window in which a concurrent reader
    /// (the watcher this file exists for) observes an empty or partial file -- made concrete by
    /// <c>--wait</c>, where the waiting <c>baton run</c> and a separate <c>baton decide</c> can both reach
    /// this call for the same room within one poll interval. A same-directory move is atomic-enough
    /// on both platforms this ships on: a reader ever sees either the old complete file or the new one,
    /// never a torn write. The temp name carries a per-call GUID (not a fixed <c>.tmp</c> suffix) so
    /// that same double-writer case can't have the two processes torn-write each other's temp file
    /// before either reaches its own rename.
    /// </remarks>
    public static async Task WriteAsync(string roomDirectoryPath, WorkflowStatusView view, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(view);

        Directory.CreateDirectory(roomDirectoryPath);
        var path = Path.Combine(roomDirectoryPath, TerminalSentinelFileName);
        var tempPath = Path.Combine(roomDirectoryPath, $"{TerminalSentinelFileName}.{Guid.NewGuid():N}.tmp");
        var json = JsonSerializer.Serialize(view, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// The pre-ledger case (#1356 point 3): provisioning/validation failed before <c>flow.jsonl</c> —
    /// and sometimes before <c>snapshot.json</c> — ever existed, so there is no <see cref="Baton.Domain.FlowState"/>
    /// to project a normal <see cref="WorkflowStatusView"/> from. Writes the coarse outcome directly:
    /// <see cref="WorkflowOutcome.Failed"/>, no steps, no outputs, <paramref name="reason"/> as the
    /// error — enough for "baton status on such a room says Failed and why" without inventing per-step
    /// detail for steps that were never reached. <paramref name="tryInvocation"/> (#1382 F3) carries the
    /// refusing <see cref="Baton.BatonFlowException.TryInvocation"/> through to the same field a
    /// file-watching agent reads instead of stderr — <c>null</c> when the refusal had no suggestion.
    /// </summary>
    public static Task WriteValidationRefusedAsync(
        string roomDirectoryPath, string reason, CancellationToken cancellationToken, string? tryInvocation = null)
    {
        var view = new WorkflowStatusView(WorkflowOutcome.Failed, [], [], reason, tryInvocation);
        return WriteAsync(roomDirectoryPath, view, cancellationToken);
    }

    /// <summary>
    /// Best-effort form for a caller that has already emitted the authoritative refusal. A denied
    /// room must not turn that refusal into a process crash merely because its optional
    /// queryability sentinel cannot be persisted (#2387).
    /// </summary>
    /// <returns><c>true</c> when the sentinel was written; <c>false</c> for ordinary filesystem access failures.</returns>
    public static async Task<bool> TryWriteValidationRefusedAsync(
        string roomDirectoryPath, string reason, CancellationToken cancellationToken, string? tryInvocation = null)
    {
        try
        {
            await WriteValidationRefusedAsync(roomDirectoryPath, reason, cancellationToken, tryInvocation).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes a stale sentinel from a prior pre-ledger failure, if any, before a fresh dispatch
    /// begins. Callers must only invoke this once they have confirmed the room is not already
    /// Terminal (#1374 F1) — see <c>RunCommand</c>'s own call site for why.
    /// </summary>
    /// <remarks>
    /// Without this, retrying a room that previously failed pre-ledger leaves the old
    /// <c>terminal.json</c> in place for the whole duration of the new, genuinely in-progress
    /// attempt — exactly the false "already done" signal a file-watcher (this file's whole reason to
    /// exist) must never see. <see cref="File.Delete"/> is already a silent no-op when the file is
    /// absent, but still throws for a locked file (a concurrent reader on Windows without
    /// <see cref="FileShare.Delete"/>), and the two call sites want opposite things from that throw
    /// (#1608 re-review finding 2):
    /// <list type="bullet">
    /// <item><description>
    /// <paramref name="bestEffort"/> <c>false</c> — the default, and what <c>RunCommand</c> uses
    /// before a fresh pump: a stale sentinel that could not be removed is precisely the false
    /// "already done" reading above, so the run must not start. The refusal is a typed
    /// <see cref="StaleSentinelDeletionException"/> rather than a raw <see cref="IOException"/>, so
    /// <c>Program.cs</c> prints it as a clean refusal instead of a stack trace.
    /// </description></item>
    /// <item><description>
    /// <paramref name="bestEffort"/> <c>true</c> — <c>Program.cs</c>'s post-<c>resolve</c> step,
    /// which runs AFTER a mutation is already durable: a delete failure there must not report a
    /// resolution as having failed when it in fact succeeded, so it warns on stderr and returns, the
    /// same shape <c>CancelRequestFile</c>'s own best-effort rename uses.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <exception cref="StaleSentinelDeletionException">
    /// The delete failed and <paramref name="bestEffort"/> is <c>false</c>. Not only a locked
    /// sentinel: an absent room directory raises <see cref="DirectoryNotFoundException"/> here too,
    /// which is unreachable from the shipped call site (<c>RunCommand</c> creates the directory
    /// first) but is part of this method's contract now that it has a public default.
    /// </exception>
    public static void DeleteStaleSentinel(string roomDirectoryPath, bool bestEffort = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        var path = Path.Combine(roomDirectoryPath, TerminalSentinelFileName);
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!bestEffort)
            {
                throw new StaleSentinelDeletionException(
                    $"Could not delete the stale terminal sentinel '{path}': {ex.Message}. Refusing to start: left in " +
                    "place it would read as 'already done' to anything watching this room for the whole duration of " +
                    "this attempt.",
                    ex)
                {
                    TryInvocation = $"close whatever holds '{path}' open, then re-run this command",
                };
            }

            try
            {
                Console.Error.WriteLine($"Could not delete stale sentinel '{path}': {ex.Message}");
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Reads a room's terminal sentinel, or <c>null</c> when the room has not reached one yet, its
    /// <c>terminal.json</c> is present but not valid JSON matching the shape (#1374 F2: a torn write
    /// caught mid-move, or a hand-edited/corrupted file), or the file is transiently unreadable
    /// because a concurrent <see cref="WriteAsync"/> is mid-<see cref="File.Move(string, string, bool)"/>.
    /// Either way this is a queryable "no answer yet", not a caller-visible crash — a malformed or
    /// momentarily-unreadable sentinel on a pre-ledger room has no ledger to fall back to, so letting
    /// any of these escape here would make that room permanently unqueryable rather than just
    /// not-yet-terminal.
    /// </summary>
    /// <remarks>
    /// Opens with <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/>, not the
    /// <see cref="FileShare.Read"/>-only default <see cref="File.OpenRead(string)"/> uses: on Windows,
    /// <see cref="WriteAsync"/>'s replace-in-place <c>File.Move</c> needs delete access on this same
    /// path, and a reader that denies it turns a routine concurrent read into an <see cref="IOException"/>
    /// on the WRITER's side instead — the exact torn-read window #1374 F2 exists to close, just moved
    /// to the other party.
    /// </remarks>
    public static async Task<WorkflowStatusView?> TryReadAsync(string roomDirectoryPath, CancellationToken cancellationToken)
    {
        var path = Path.Combine(roomDirectoryPath, TerminalSentinelFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<WorkflowStatusView>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
