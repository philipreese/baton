using Baton.Artifacts;
using Baton.Cli.Daemon;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// <c>baton rooms prune</c> (#1659): the batch form of <see cref="RoomDeleteCommand"/>, plus the
/// registry compaction spec/baton.md §8 named as "left undone" (dedupe + drop entries whose room
/// directory no longer exists) — that half runs on every invocation, unconditionally, since it is
/// registry hygiene rather than a room-deleting decision.
/// </summary>
/// <remarks>
/// <b>Dry-run is the default.</b> Without <c>--yes</c> this only ever lists what
/// <see cref="RoomsPruneOptions.Terminal"/>'s filters select — mutates nothing beyond the
/// unconditional compaction above, which is itself non-destructive (it only removes duplicate/
/// already-gone registry lines, never a room a caller could still want). <c>--dry-run</c> is accepted
/// as an explicit spelling of that same default, never required to get it.
/// <para>
/// Not a <see cref="CommandResult"/>/<see cref="FlowStateReporter"/> command — same carve-out as
/// <see cref="RoomDeleteCommand"/>.
/// </para>
/// </remarks>
public static class RoomsPruneCommand
{
    /// <summary>One terminal room <see cref="RoomsPruneOptions.Terminal"/>'s filters selected.</summary>
    public sealed record Candidate(
        string RoomDirectoryPath,
        string State,
        DateTime TerminalAtUtc,
        bool SelectionRefused = false,
        string? SelectionRefusalReason = null);

    public sealed record Result(
        int DedupedRegistryLines,
        int MissingDirectoryRegistryLines,
        IReadOnlyList<Candidate> Candidates,
        bool Executed,
        IReadOnlyList<RoomDeleteCommand.Result> Deleted);

    public static async Task<Result> ExecuteAsync(
        RoomsPruneOptions options,
        TextWriter output,
        string? registryFilePathOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        var registryFilePath = registryFilePathOverride ?? BatonPaths.RoomRegistryFile;

        // #1659: dry-run (the default, no --yes) must mutate nothing at all — including the registry
        // compaction, which CompactAsync itself would otherwise rewrite unconditionally. PreviewCompactionAsync
        // computes the identical counts without writing; only --yes reaches the mutating CompactAsync.
        var (dedupedCount, missingDirectoryCount) = options.Yes
            ? await RoomRegistryStore.CompactAsync(registryFilePath, cancellationToken).ConfigureAwait(false)
            : await RoomRegistryStore.PreviewCompactionAsync(registryFilePath, cancellationToken).ConfigureAwait(false);
        output.WriteLine(options.Yes
            ? $"Registry: deduped {dedupedCount} line(s), dropped {missingDirectoryCount} line(s) with a missing directory."
            : $"Registry: would dedupe {dedupedCount} line(s), would drop {missingDirectoryCount} line(s) with a missing directory.");

        var candidates = options.Terminal
            ? await FindCandidatesAsync(registryFilePath, options, cancellationToken).ConfigureAwait(false)
            : [];

        if (candidates.Count == 0)
        {
            output.WriteLine("No terminal rooms match --terminal's filters.");
            return new Result(dedupedCount, missingDirectoryCount, candidates, options.Yes, []);
        }

        if (!options.Yes)
        {
            output.WriteLine($"Would delete {candidates.Count} room(s) (pass --yes to actually delete):");
            foreach (var candidate in candidates)
            {
                output.WriteLine($"  {candidate.RoomDirectoryPath} — {candidate.State}, terminal since {candidate.TerminalAtUtc:O}");
            }

            return new Result(dedupedCount, missingDirectoryCount, candidates, Executed: false, []);
        }

        var deleted = new List<RoomDeleteCommand.Result>();
        foreach (var candidate in candidates)
        {
            // Already known terminal (this same pass just confirmed terminal.json) — go straight to
            // the shared delete, not through RoomDeleteCommand.ExecuteAsync's refusal gate, which would
            // just re-read the same file this loop already read.
            var deleteResult = await RoomDeleteCommand
                .DeleteAsync(candidate.RoomDirectoryPath, cancellationToken)
                .ConfigureAwait(false);
            RoomDeleteCommand.Print(deleteResult, output);
            deleted.Add(deleteResult);
        }

        output.WriteLine($"Deleted {deleted.Count} room(s).");
        return new Result(dedupedCount, missingDirectoryCount, candidates, Executed: true, deleted);
    }

    private static async Task<IReadOnlyList<Candidate>> FindCandidatesAsync(
        string registryFilePath, RoomsPruneOptions options, CancellationToken cancellationToken)
    {
        var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(registryFilePath, cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var candidates = new List<Candidate>();

        foreach (var entry in entries)
        {
            if (!Directory.Exists(entry.RoomPath))
            {
                continue;
            }

            if (ConductorRoomDetector.IsConductorRoom(entry.RoomPath))
            {
                continue;
            }

            // #2111: baton keep must exempt a room from the batch delete this feeds (RoomRetentionSweep's
            // automatic prune goes through this exact method), not only from ArtifactPruner's separate
            // recoverable-move path. baton room delete <room-dir> still removes a kept room directly —
            // this is candidate-discovery only, the escape hatch stays one target at a time.
            if (KeepMarker.IsKept(entry.RoomPath))
            {
                continue;
            }

            var view = await TerminalSentinelWriter.TryReadAsync(entry.RoomPath, cancellationToken).ConfigureAwait(false);
            if (view is null)
            {
                continue;
            }

            if (options.State is not null && !string.Equals(view.State, options.State, StringComparison.Ordinal))
            {
                continue;
            }

            var terminalSentinelPath = Path.Combine(entry.RoomPath, TerminalSentinelWriter.TerminalSentinelFileName);
            var terminalAtUtc = File.GetLastWriteTimeUtc(terminalSentinelPath);
            if (options.OlderThanDays is { } days && now - terminalAtUtc < TimeSpan.FromDays(days))
            {
                continue;
            }

            candidates.Add(new Candidate(entry.RoomPath, view.State, terminalAtUtc));
        }

        return candidates;
    }

    /// <summary>
    /// Read-only candidate discovery shared by the daemon's retention-evidence preparation. This
    /// intentionally uses the same sentinel/bindings/metadata selection vocabulary as manual prune;
    /// it does not inspect or replay workflow journals.
    /// </summary>
    internal static Task<IReadOnlyList<Candidate>> DiscoverCandidatesAsync(
        string registryFilePath, RoomsPruneOptions options, CancellationToken cancellationToken) =>
        FindCandidatesAsync(registryFilePath, options, cancellationToken);

    /// <summary>
    /// Retention-only discovery. It preserves the manual candidate contract while rejecting oversized
    /// selection inputs before the existing unbounded helpers are reached. Full workflow journals are
    /// never opened here; the capture store owns that bounded work after selection.
    /// </summary>
    internal static async Task<IReadOnlyList<Candidate>> DiscoverRetentionCandidatesAsync(
        string registryFilePath, RoomsPruneOptions options, CancellationToken cancellationToken)
    {
        var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(registryFilePath, cancellationToken)
            .ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var candidates = new List<Candidate>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(entry.RoomPath)) continue;

            var selectionFiles = new[]
            {
                Path.Combine(entry.RoomPath, TerminalSentinelWriter.TerminalSentinelFileName),
                BatonPaths.RoomBindingsFile(entry.RoomPath),
                Path.Combine(entry.RoomPath, BatonPaths.RoomMetadataFileName),
            };
            var oversized = selectionFiles.FirstOrDefault(path =>
            {
                try { return File.Exists(path) && new FileInfo(path).Length > RoomRetentionEvidenceLimits.MaxSelectionFileBytes; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
            });

            var terminalPath = Path.Combine(entry.RoomPath, TerminalSentinelWriter.TerminalSentinelFileName);
            DateTime terminalAtUtc;
            try
            {
                if (!File.Exists(terminalPath)) continue;
                terminalAtUtc = File.GetLastWriteTimeUtc(terminalPath);
            }
            catch (IOException) { continue; }

            if (options.OlderThanDays is { } days && now - terminalAtUtc < TimeSpan.FromDays(days)) continue;
            if (oversized is not null)
            {
                candidates.Add(new Candidate(entry.RoomPath, "retention-input-refused", terminalAtUtc, true,
                    $"selection input '{Path.GetFileName(oversized)}' exceeds the bounded discovery size"));
                continue;
            }

            if (ConductorRoomDetector.IsConductorRoom(entry.RoomPath) || KeepMarker.IsKept(entry.RoomPath)) continue;
            var view = await TerminalSentinelWriter.TryReadAsync(entry.RoomPath, cancellationToken).ConfigureAwait(false);
            if (view is null) continue;
            if (options.State is not null && !string.Equals(view.State, options.State, StringComparison.Ordinal)) continue;
            candidates.Add(new Candidate(entry.RoomPath, view.State, terminalAtUtc));
        }

        return candidates;
    }
}
