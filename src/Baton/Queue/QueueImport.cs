using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Queue;

/// <summary>
/// Reads the scratchpad runner's own <c>queue.json</c> (#1934 Q7: ship, then cut over at a no-lane gap
/// by importing the live queue file and retiring the runner the same night). This is a one-way
/// migration reader, not a second queue format — nothing writes this shape.
/// </summary>
/// <remarks>
/// <para>
/// Both a bare array and an <c>items</c>-wrapped object are accepted: the live file has been
/// hand-edited eight times in one evening (#1934 body), and refusing a shape over its outermost
/// bracket at cutover time would be a refusal at the worst possible moment. The refusals this DOES
/// make, and why they are total rather than per-item, are spec/baton.md §13's.
/// </para>
/// <para>
/// <b>An imported item carries no room directory</b>, because the runner never recorded one. The
/// consequence, said here rather than left to be discovered: a launched item imported this way can
/// never be closed out by the daemon's done detection — which reads the room — so the operator clears
/// it by hand.
/// </para>
/// </remarks>
public static class QueueImport
{
    private static readonly JsonSerializerOptions ReaderOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Parses <paramref name="json"/> into queue items.
    /// </summary>
    /// <param name="specFileResolver">
    /// Maps a tag to the spec path the imported item should carry. The runner kept its briefs beside
    /// itself under names baton does not know, so the caller — <c>baton queue import</c> — decides
    /// whether that is a copied spec under <c>BatonPaths.QueueSpecsDirectory</c> or a placeholder for
    /// an already-launched item that will never need one.
    /// </param>
    /// <param name="now">Stamped as each item's <c>AddedAt</c>; the runner recorded no add time.</param>
    /// <exception cref="QueueStoreException">
    /// <paramref name="json"/> is not the runner's shape, or an item is missing a tag, a role, or a
    /// workspace. Nothing is imported when this throws — see spec/baton.md §13 for why that is the
    /// posture at cutover.
    /// </exception>
    public static IReadOnlyList<QueueItem> Parse(
        string json, Func<string, string> specFileResolver, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(specFileResolver);

        List<ScratchpadItem?>? raw;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var root = document.RootElement;
            var arrayElement = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object when root.TryGetProperty("items", out var wrapped) => wrapped,
                _ => default,
            };

            if (arrayElement.ValueKind != JsonValueKind.Array)
            {
                throw new QueueStoreException(
                    "The file to import is neither a JSON array of items nor an object with an 'items' array — "
                    + "that is the shape the scratchpad runner writes.");
            }

            raw = JsonSerializer.Deserialize<List<ScratchpadItem?>>(arrayElement.GetRawText(), ReaderOptions);
        }
        catch (JsonException ex)
        {
            throw new QueueStoreException($"Could not parse the file to import: {ex.Message}", ex);
        }

        var items = new List<QueueItem>();
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in raw ?? [])
        {
            if (entry is null)
            {
                continue;
            }

            if (!QueueTag.IsValid(entry.Tag))
            {
                throw new QueueStoreException(
                    $"Import refused: '{entry.Tag}' is not a usable tag ({QueueTag.Rule}). Fix it in the file "
                    + "being imported and re-run — importing the rest would silently drop this item.");
            }

            // The file is checked against ITSELF, not only against what is already queued: a tag is an
            // identity, so two rows sharing one point at one spec file under the queue's specs
            // directory and would launch as two lanes off one brief — the same collision `baton queue
            // add` refuses. The runner's file was hand-edited eight times in one evening (#1934 body),
            // which is exactly how a tag ends up in it twice.
            if (!tags.Add(entry.Tag!))
            {
                throw new QueueStoreException(
                    $"Import refused: the file lists the tag '{entry.Tag}' more than once. A tag names one spec "
                    + "file, so the two rows would share a brief. Rename one and re-run.");
            }

            if (string.IsNullOrWhiteSpace(entry.Role))
            {
                throw new QueueStoreException($"Import refused: item '{entry.Tag}' names no role.");
            }

            var workspace = entry.Workspace;
            if (string.IsNullOrWhiteSpace(workspace))
            {
                throw new QueueStoreException(
                    $"Import refused: item '{entry.Tag}' names no workspace. An item imported from the runner "
                    + "must already carry the directory its worker runs in — 'baton queue import' provisions "
                    + "nothing, because a worktree for an item the runner already launched exists already.");
            }

            items.Add(new QueueItem
            {
                Tag = entry.Tag!,
                Role = entry.Role!,
                Workspace = workspace!,
                SpecFile = specFileResolver(entry.Tag!),
                Adapter = Trimmed(entry.Adapter),
                Model = Trimmed(entry.Model),
                Effort = Trimmed(entry.Effort),
                TimeoutMinutes = entry.Timeout,
                MaxToolSteps = entry.MaxToolSteps,
                TokenBudget = entry.TokenBudget,
                OverrideRunwayReason = Trimmed(entry.OverrideRunway),
                Reason = Trimmed(entry.Reason),
                Issue = entry.Issue,
                PinModel = entry.PinModel,
                External = entry.External,
                State = entry.Launched ? QueueItemState.Launched : QueueItemState.Queued,
                LaunchedAt = entry.Launched ? now : null,
                AddedAt = now,
            });
        }

        return items;
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The runner's own row. Every field optional — this is a hand-edited file, and the
    /// validation above is what turns absence into a refusal where absence matters.</summary>
    private sealed record ScratchpadItem
    {
        public string? Tag { get; init; }
        public string? Role { get; init; }
        public string? Model { get; init; }
        public string? Effort { get; init; }

        /// <summary>The runner's <c>timeout</c>, in minutes — the same unit <c>baton dispatch
        /// --timeout</c> takes, so it is carried across unconverted.</summary>
        public int? Timeout { get; init; }

        public string? Workspace { get; init; }
        public int? Issue { get; init; }
        public string? Adapter { get; init; }
        public int? MaxToolSteps { get; init; }
        public long? TokenBudget { get; init; }
        public string? OverrideRunway { get; init; }
        public string? Reason { get; init; }
        public bool PinModel { get; init; }
        public bool External { get; init; }

        /// <summary>The runner's launched stamp. Absent on an unlaunched row; see the type remarks
        /// for why a launched row is not reset.</summary>
        [JsonPropertyName("launched")]
        public bool Launched { get; init; }
    }
}
