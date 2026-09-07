using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Artifacts;
using Baton.Domain;

namespace Baton.Status;

/// <summary>
/// One execution's own share of a fleet-level burn ledger (issue #1570, quota-design S4b — full design
/// in the 2026-09-01 proposal comment on #802, section "Where usage is harvested, where the ledger
/// lives, and re-derivability"). Every field independently nullable and omitted
/// (never emitted as <c>null</c>, never fabricated as zero) when the writer had nothing to report for
/// it — the same doctrine <see cref="WorkerUsage"/> and <see cref="ExecutionUsageView"/> already keep,
/// extended to this type rather than re-derived. <see cref="Room"/> and <see cref="Execution"/> are
/// what makes an entry checkable against its source while that source survives (spec/baton.md §7):
/// re-derivability is in-principle, not in-practice, because <c>RoomRetentionSweep</c>
/// moves execution directories out of reach of a rebuild.
/// </summary>
public sealed record QuotaLedgerEntry(
    [property: JsonPropertyName("at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTime? At = null,
    [property: JsonPropertyName("room")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Room = null,
    [property: JsonPropertyName("execution")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Execution = null,
    [property: JsonPropertyName("adapter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Adapter = null,
    [property: JsonPropertyName("model")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Model = null,
    [property: JsonPropertyName("tokensIn")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TokensIn = null,
    [property: JsonPropertyName("tokensOut")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TokensOut = null,
    [property: JsonPropertyName("cacheRead")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CacheReadTokens = null,
    [property: JsonPropertyName("cacheCreation")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CacheCreationTokens = null,
    [property: JsonPropertyName("thinking")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? ThinkingTokens = null,
    [property: JsonPropertyName("turns")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Turns = null,
    [property: JsonPropertyName("wallClockMs")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? WallClockMs = null,
    // The closed token set: FailureClassification's four member names verbatim (Retryable, Permanent,
    // ExhaustedUntil, ToolDenied), or one of Succeeded/Failed/Cancelled/Indeterminate/Arrested for an
    // execution whose terminal event carries no classification -- see QuotaLedgerStore.BuildEntries.
    // Display/grouping only, like WorkflowStatusStepView.FailureKind; nothing parses it back.
    [property: JsonPropertyName("outcome")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Outcome = null);

/// <summary>
/// Reads and writes <see cref="BatonPaths.QuotaLedgerFile"/> — the spec/baton.md §7 fleet-level burn
/// ledger. Precedent, not a design question (issue #1570): same append-only JSONL shape, same
/// <see cref="MutexGuardedFileLock"/> mechanism, and the same fail-open contract
/// <see cref="RoomRegistryStore"/> already established for <c>room-registry.jsonl</c> — this type
/// shares the mechanism rather than copying it: the JSONL append/read half is
/// <see cref="JsonLinesLedger{TEntry}"/>, shared with <c>CostLedgerStore</c> (#1884), and what remains
/// here is this ledger's own <see cref="BuildEntries"/>, read-time fold and <see cref="RebuildAsync"/>.
/// </summary>
/// <remarks>
/// <b>Fails open, never gates.</b> Like <see cref="RoomRegistryStore"/>, this store only ever adds
/// accounting coverage — it must never be the reason a dispatch, resolve, or any other mutation
/// reports as failed. <see cref="AppendAsync"/> itself still throws
/// (<see cref="IOException"/>/<see cref="UnauthorizedAccessException"/>/<see cref="WaitHandleCannotBeOpenedException"/>)
/// rather than swallowing internally — the caller (<c>Program.cs</c>'s settle-time site) is where the
/// swallow-and-report-on-stderr happens, the same split <see cref="RoomRegistryStore.AppendAsync"/>'s
/// own remarks document and <c>RunCommand.RegisterRoomAsync</c> already performs for the registry. This
/// is the registry's own sanctioned exception to the repo's no-silent-swallow rule: logged on stderr,
/// additive only, and must never gate work that already completed.
/// </remarks>
public static class QuotaLedgerStore
{
    /// <summary>
    /// The append-only JSONL mechanism itself, shared with <c>CostLedgerStore</c> rather than copied
    /// (#1884) — see <see cref="JsonLinesLedger{TEntry}"/> for what it guarantees, and
    /// <see cref="MutexGuardedFileLock"/> for what renaming <c>baton-quota-ledger</c> would cost.
    /// </summary>
    internal static readonly JsonLinesLedger<QuotaLedgerEntry> Ledger =
        new("baton-quota-ledger", "quota ledger", entry => entry.Execution);

    /// <summary>
    /// Builds one <see cref="QuotaLedgerEntry"/> per execution in <paramref name="entries"/> that has
    /// both a recorded start and exit — the same population
    /// <see cref="ExecutionUsageProjector.BuildByExecutionId"/> yields, reused rather than re-derived
    /// (Architecture Rule 2: no second vendor-envelope reader). An execution missing either lifecycle
    /// event (still running, or Flow crashed before Core recorded one) is entirely absent, same as
    /// there: the accepted loss spec/baton.md §7 documents — "a lane that dies before settling" — is
    /// this same gap, not a second one.
    /// </summary>
    public static IReadOnlyList<QuotaLedgerEntry> BuildEntries(IReadOnlyList<LogEntry> entries, string roomDirectoryPath)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var artifactsRootPath = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
        var usageByExecutionId = ExecutionUsageProjector.BuildByExecutionId(entries, artifactsRootPath, roomDirectoryPath: roomDirectoryPath);

        // #1781: the recorded-adapter/model-with-StepRebound-override precedence is the one primitive
        // ExecutionUsageProjector already computes for its own parser choice -- see
        // ExecutionBindingResolver's own doc comment for why this used to be a second, untested copy.
        var resolvedBindings = ExecutionBindingResolver.Resolve(entries);
        var outcomeByExecutionId = new Dictionary<string, string>(StringComparer.Ordinal);
        var exitedAtByExecutionId = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry is LogEntry.CoreLogEntry { Event: CoreEvent.ExecutionExited exited, WriterUtcTimestamp: { } exitedAt })
            {
                exitedAtByExecutionId[exited.ExecutionId.Value] = exitedAt;
            }

            if (entry is not LogEntry.FlowLogEntry flowEntry)
            {
                continue;
            }

            switch (flowEntry.Event)
            {
                case FlowEvent.ExecutionSucceeded succeeded:
                    // #1945: kept identical to CostLedgerStore's own arm, whose remark states why the
                    // flag is read here at all.
                    outcomeByExecutionId[succeeded.ExecutionId.Value] = succeeded.FinishedDuringTeardown
                        ? WorkflowOutcome.FinishedDuringTeardown
                        : "Succeeded";
                    break;

                case FlowEvent.ExecutionFailed failed:
                    outcomeByExecutionId[failed.ExecutionId.Value] = failed.FailureClassification?.ToString() ?? "Failed";
                    break;

                case FlowEvent.ExecutionCancelled cancelled:
                    outcomeByExecutionId[cancelled.ExecutionId.Value] = "Cancelled";
                    break;

                case FlowEvent.ExecutionIndeterminate indeterminate:
                    outcomeByExecutionId[indeterminate.ExecutionId.Value] = "Indeterminate";
                    break;

                case FlowEvent.ExecutionArrested arrested:
                    outcomeByExecutionId[arrested.ExecutionId.Value] = "Arrested";
                    break;
            }
        }

        var recordedRoomPath = BatonPaths.RecordKey(roomDirectoryPath);
        var result = new List<QuotaLedgerEntry>(usageByExecutionId.Count);
        foreach (var (executionId, usage) in usageByExecutionId)
        {
            resolvedBindings.TryGetValue(executionId, out var resolvedBinding);
            outcomeByExecutionId.TryGetValue(executionId, out var outcome);
            var at = exitedAtByExecutionId.TryGetValue(executionId, out var exitedAt) ? exitedAt : (DateTime?)null;

            result.Add(new QuotaLedgerEntry(
                At: at,
                Room: recordedRoomPath,
                Execution: executionId,
                Adapter: resolvedBinding.Adapter,
                Model: resolvedBinding.Model,
                TokensIn: usage.TokensIn,
                TokensOut: usage.TokensOut,
                CacheReadTokens: usage.CacheReadTokens,
                CacheCreationTokens: usage.CacheCreationTokens,
                ThinkingTokens: usage.ThinkingTokens,
                Turns: usage.Turns,
                WallClockMs: usage.WallClockMs,
                Outcome: outcome));
        }

        return result;
    }

    /// <summary>
    /// Appends the subset of <paramref name="entries"/> whose <see cref="QuotaLedgerEntry.Execution"/>
    /// is not already present in <paramref name="ledgerFilePath"/> — a single read-check-then-append
    /// critical section, the same shape <see cref="RoomRegistryStore.AppendAsync"/>'s own
    /// <c>IsAlreadyRegistered</c> skip uses (#1657) and for the identical reason: <c>Program.cs</c>'s
    /// settle-time call site fires on every command that carries a room to Terminal, including a
    /// re-run of an already-terminal room, <c>supply</c>, and the <c>resolve --reject</c> → re-Terminal
    /// path <c>Program.cs</c>'s own remarks document — every one of which re-derives
    /// <see cref="QuotaLedgerStore.BuildEntries"/> over the WHOLE room, not just what changed since the
    /// last append. Without this check, a room settling twice writes the same execution twice, silently
    /// breaking the "one line per execution" shape every reader (<see cref="ReadDistinctByExecutionAsync"/>,
    /// <see cref="RebuildAsync"/>) otherwise relies on the read-time fold alone to restore. How the skip
    /// is performed is <see cref="JsonLinesLedger{TEntry}.AppendAsync"/>'s to state; why this ledger
    /// needs it is the paragraph above. Throws exactly as
    /// <see cref="RoomRegistryStore.AppendAsync"/> documents: the caller's job to log and swallow, not
    /// this method's.
    /// </summary>
    public static Task AppendAsync(
        IReadOnlyList<QuotaLedgerEntry> entries, string ledgerFilePath, CancellationToken cancellationToken = default) =>
        Ledger.AppendAsync(entries, ledgerFilePath, cancellationToken);

    /// <summary>
    /// Every parseable line in <paramref name="ledgerFilePath"/>, in file (= write) order — read
    /// tolerance, locking and the never-throws posture are
    /// <see cref="JsonLinesLedger{TEntry}.ReadAllAsync"/>'s, the same tolerance
    /// <see cref="RoomRegistryStore.ReadDistinctByRoomAsync"/> already documents.
    /// </summary>
    public static Task<IReadOnlyList<QuotaLedgerEntry>> ReadAllAsync(
        string ledgerFilePath, CancellationToken cancellationToken = default) =>
        Ledger.ReadAllAsync(ledgerFilePath, cancellationToken);

    /// <summary>
    /// <see cref="ReadAllAsync"/>, folded to the last line written for each distinct
    /// <see cref="QuotaLedgerEntry.Execution"/> (append order is write order, so the last occurrence in
    /// the file is the last-writer-wins value) — the same read-time fold
    /// <see cref="RoomRegistryStore.ReadDistinctByRoomAsync"/> applies for <see cref="QuotaLedgerEntry.Room"/>.
    /// An entry with no <see cref="QuotaLedgerEntry.Execution"/> at all cannot be deduplicated or
    /// merged by anything and is dropped — the doctrine every field is independently absent means this
    /// is reachable, not just defensive.
    /// </summary>
    public static async Task<IReadOnlyList<QuotaLedgerEntry>> ReadDistinctByExecutionAsync(
        string ledgerFilePath, CancellationToken cancellationToken = default)
    {
        var all = await ReadAllAsync(ledgerFilePath, cancellationToken).ConfigureAwait(false);
        var byExecution = new Dictionary<string, QuotaLedgerEntry>(StringComparer.Ordinal);
        foreach (var entry in all)
        {
            if (entry.Execution is { Length: > 0 } executionId)
            {
                byExecution[executionId] = entry;
            }
        }

        return byExecution.Values.ToList();
    }

    /// <summary>(Entries the ledger already held, total after the merge, how many were newly recovered by the walk.)</summary>
    public sealed record RebuildResult(int PreviousCount, int TotalCount, int RecoveredCount);

    /// <summary>
    /// Merges <paramref name="freshlyWalkedEntries"/> (a caller's fresh re-derivation from every still-
    /// live room's own <c>flow.jsonl</c>/<c>.stdout.log</c>) into whatever <paramref name="ledgerFilePath"/>
    /// already holds, by <see cref="QuotaLedgerEntry.Execution"/> id — <b>never sums</b>. Starts from
    /// the ledger's own content, not from the walk alone: an execution the ledger already recorded but
    /// whose room <c>RoomRetentionSweep</c> has since pruned is not in the walk, and dropping it would
    /// make a rebuild destroy exactly the past-retention coverage the ledger exists to hold
    /// (spec/baton.md §7). A freshly-walked entry for an execution the ledger already had overwrites
    /// that entry — freshly re-derived data from the still-live source beats whatever was durable
    /// before. Reads the existing ledger, merges, and rewrites the whole file in ONE acquisition of the
    /// <see cref="MutexGuardedFileLock"/> keyed on this file — read-then-write across two separate
    /// acquisitions would let a settle-time <see cref="AppendAsync"/> land in the gap between them and
    /// be silently truncated away by this method's own rewrite, which is exactly the loss a rebuild
    /// must never cause. Running this twice against an unchanged fleet produces byte-identical totals
    /// both times.
    /// </summary>
    public static Task<RebuildResult> RebuildAsync(
        IReadOnlyList<QuotaLedgerEntry> freshlyWalkedEntries, string ledgerFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(freshlyWalkedEntries);
        ArgumentException.ThrowIfNullOrEmpty(ledgerFilePath);

        JsonLinesLedger<QuotaLedgerEntry>.EnsureParentDirectory(ledgerFilePath);

        return Ledger.RunUnderLockAsync(
            ledgerFilePath,
            () =>
            {
                var merged = new Dictionary<string, QuotaLedgerEntry>(StringComparer.Ordinal);
                var previousCount = 0;
                foreach (var entry in Ledger.ReadAllUnlocked(ledgerFilePath))
                {
                    if (entry.Execution is { Length: > 0 } executionId)
                    {
                        merged[executionId] = entry;
                        previousCount++;
                    }
                }

                var recoveredCount = 0;
                foreach (var entry in freshlyWalkedEntries)
                {
                    if (entry.Execution is not { Length: > 0 } executionId)
                    {
                        continue;
                    }

                    if (!merged.ContainsKey(executionId))
                    {
                        recoveredCount++;
                    }

                    merged[executionId] = entry;
                }

                WriteAllUnlocked(ledgerFilePath, merged.Values.ToList());
                return new RebuildResult(previousCount, merged.Count, recoveredCount);
            },
            cancellationToken);
    }

    /// <summary>
    /// Replaces <paramref name="ledgerFilePath"/>'s entire contents with one JSON line per
    /// <paramref name="entries"/>, via a temp-file-then-move so a concurrent reader under the same
    /// <see cref="MutexGuardedFileLock"/> never observes a truncated file — the same atomic-replace
    /// discipline <see cref="RoomRegistryStore"/>'s own compaction uses. UTF-8 without a byte-order
    /// mark, matching the <see cref="Encoding.UTF8"/>-via-<see cref="FileStream"/> bytes
    /// <see cref="JsonLinesLedger{TEntry}.AppendAsync"/> writes (and serialized with that ledger's own
    /// <see cref="JsonLinesLedger{TEntry}.SerializerOptions"/>, so a rebuilt file and an appended one can
    /// never disagree about the wire format):
    /// <see cref="File.WriteAllText(string,string,Encoding)"/> would otherwise stamp a BOM onto
    /// a rebuilt file's first line that an appended-only one never carries, which a strict external
    /// JSONL reader can choke on. Callers must already hold the <see cref="MutexGuardedFileLock"/> on
    /// <paramref name="ledgerFilePath"/>; this method takes no lock of its own.
    /// </summary>
    private static void WriteAllUnlocked(string ledgerFilePath, IReadOnlyList<QuotaLedgerEntry> entries)
    {
        var tempPath = $"{ledgerFilePath}.{Guid.NewGuid():N}.tmp";
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append(JsonSerializer.Serialize(entry, Ledger.SerializerOptions)).Append('\n');
        }

        File.WriteAllBytes(tempPath, Encoding.UTF8.GetBytes(builder.ToString()));
        File.Move(tempPath, ledgerFilePath, overwrite: true);
    }
}
