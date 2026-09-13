using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Baton.Memory;

/// <summary>Renders the deliberately body-free memory context attachment used by dispatch.</summary>
public static class MemoryContextIndex
{
    public const string FormatMarker = "# baton-memory-context-index v1";
    /// <summary>
    /// The omission manifest is metadata rather than a row body, but it is still an attachment sent to
    /// a worker. Keep it independently bounded: if every omitted id cannot be named within this limit,
    /// refuse the projection rather than silently dropping ids or making the attachment unbounded.
    /// </summary>
    public const int MaxOmissionMetadataBytes = 65_536;

    /// <summary>Renders resolved candidates in the same fleet-first total order as <see cref="MemoryProjection"/>.</summary>
    public static MemoryContextIndexResult Build(string repository, IReadOnlyList<MemoryProjectionCandidate> candidates, ProjectionBudget budget)
    {
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(budget);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = candidates.Where(c => seen.Add(c.Entry.Id))
            .OrderBy(c => c.Origin == MemoryFactOrigin.Fleet ? 0 : 1)
            .ThenBy(c => c.Entry.Repository, StringComparer.Ordinal)
            .ThenBy(c => MemoryJsonNames.Of(c.Entry.Kind), StringComparer.Ordinal)
            .ThenBy(c => c.Entry.Id, StringComparer.Ordinal)
            .Where(c => c.Entry.SupersededBy is not { Count: > 0 }).ToList();
        var repositoryKeys = ordered.Where(c => c.Origin == MemoryFactOrigin.Repository)
            .Select(c => c.Entry.Repository.ToLowerInvariant() + "\n" + Path.GetFileName(c.Entry.SourcePath).ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ordered = ordered.Where(c => c.Origin == MemoryFactOrigin.Repository ||
            !repositoryKeys.Contains(c.Entry.Repository.ToLowerInvariant() + "\n" + Path.GetFileName(c.Entry.SourcePath).ToLowerInvariant())).ToList();
        var rows = new List<string>();
        var omitted = new List<MemoryContextIndexOmission>();
        var bytes = 0;
        var stopped = false;
        foreach (var candidate in ordered)
        {
            var entry = candidate.Entry;
            // Canonical entries have no title/description schema.  A source filename or authored-by
            // marker is not a safe substitute: either would make a row look like a summary.  Keep the
            // cue explicit and deliberately non-semantic until such a schema is separately designed.
            var row = string.Create(CultureInfo.InvariantCulture, $"- id={entry.Id} kind={MemoryJsonNames.Of(entry.Kind)} kind-source={MemoryJsonNames.Of(entry.KindSource)} origin={MemoryJsonNames.Of(candidate.Origin)} provenance=unlabelled-non-semantic\n");
            var size = Encoding.UTF8.GetByteCount(row);
            if (stopped || rows.Count >= budget.MaxEntries || bytes + size > budget.MaxBodyBytes)
            {
                stopped = true;
                omitted.Add(new(entry.Id, "beyond the memory-context budget (" + budget.Describe() + ")"));
                continue;
            }
            rows.Add(row); bytes += size;
        }
        var body = string.Concat(rows);
        // Omission detail is deliberately metadata-only: a caller can identify exactly what was
        // excluded without receiving any entry text. It follows the resolved total order above.
        var omittedSuffix = omitted.Count == 0
            ? string.Empty
            : "\nomitted-entries:\n" + string.Concat(omitted.Select(o => $"- id={o.EntryId} reason={o.Reason}\n"));
        if (Encoding.UTF8.GetByteCount(omittedSuffix) > MaxOmissionMetadataBytes)
        {
            throw new InvalidOperationException(
                $"Memory-context omission metadata exceeds its {MaxOmissionMetadataBytes}-byte limit; "
                + "refusing to omit an entry id from the generated index.");
        }
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var header = string.Create(CultureInfo.InvariantCulture, $"{FormatMarker}\nrepository={repository}\ncontent-sha256={digest}\nentries={rows.Count}\nomitted={omitted.Count}\n\n");
        return new MemoryContextIndexResult(Encoding.UTF8.GetBytes(header + body + omittedSuffix), digest, rows.Count, omitted);
    }
}
public sealed record MemoryContextIndexResult(byte[] Bytes, string ContentSha256, int EntryCount, IReadOnlyList<MemoryContextIndexOmission> Omitted);
public sealed record MemoryContextIndexOmission(string EntryId, string Reason);
