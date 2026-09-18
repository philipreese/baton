using System.Text;

namespace Baton.Memory;

/// <summary>
/// The bounded, vendor-loaded view of a <see cref="MemoryProjection"/> (#2139).
/// Baton owns the two markers and the explicitly named detail files; every byte outside the
/// markers remains owned by the vendor or operator and is copied without decoding or normalising.
/// </summary>
public static class MemoryVendorIndexProjection
{
    public const string SectionStart = "<!-- baton:memory-index:start -->";
    public const string SectionEnd = "<!-- baton:memory-index:end -->";
    public const string DetailPrefix = "baton-memory-";
    public const string DetailSuffix = ".md";

    private static readonly byte[] StartBytes = Encoding.UTF8.GetBytes(SectionStart);
    private static readonly byte[] EndBytes = Encoding.UTF8.GetBytes(SectionEnd);

    /// <summary>Builds the index and immutable per-entry detail files from the already-resolved projection.</summary>
    public static VendorIndexPublication Build(MemoryProjectionResult projection, byte[]? existingIndex)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var lines = projection.ProjectedEntries.Select(entry =>
        {
            var title = Title(entry.Entry);
            var description = Description(entry.Entry, title);
            return $"- [{title}]({DetailFileName(entry.Entry.Id)}) — {description}\n";
        });
        var section = Encoding.UTF8.GetBytes(SectionStart + "\n" + string.Concat(lines) + SectionEnd + "\n");
        var index = Merge(existingIndex ?? [], section);
        var details = projection.ProjectedEntries
            .Select(entry => new VendorDetailFile(
                DetailFileName(entry.Entry.Id),
                RenderDetail(entry)))
            .ToList();
        return new VendorIndexPublication(index, details);
    }

    public static string DetailFileName(string entryId)
    {
        if (!IsDerivedEntryId(entryId))
        {
            throw new InvalidDataException("A canonical memory entry id must be exactly 32 lower-case hexadecimal characters.");
        }

        return DetailPrefix + entryId + DetailSuffix;
    }

    /// <summary>Whether a file name has the exact derived-id detail-file grammar.</summary>
    public static bool IsOwnedDetailFile(string fileName) =>
        TryGetDetailEntryId(fileName, out _);

    /// <summary>
    /// Whether a syntactically valid detail file carries the content-backed ownership record Baton
    /// writes. A filename alone is never deletion authority.
    /// </summary>
    public static bool IsOwnedDetailFile(string fileName, ReadOnlySpan<byte> bytes) =>
        TryGetDetailEntryId(fileName, out var entryId)
        && bytes.StartsWith(Encoding.UTF8.GetBytes(
            MemoryProjection.FormatMarker + "\n<!-- baton:memory-detail id=" + entryId + " -->\n"));

    /// <summary>
    /// Returns only the exact detail links in a structurally valid owned section. This is the
    /// persistent ownership evidence stale cleanup consumes; a prefix-shaped vendor filename is not
    /// enough to make it deletable.
    /// </summary>
    public static IReadOnlySet<string> OwnedDetailFileNames(byte[] index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!TryFindOwnedSection(index, out var section))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bodyStart = section.Start + StartBytes.Length;
        var lines = Encoding.UTF8.GetString(index.AsSpan(bodyStart, section.End - bodyStart))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (!line.StartsWith("- [", StringComparison.Ordinal))
            {
                continue;
            }

            var close = ClosingLinkDelimiter(line);
            if (close < 0)
            {
                continue;
            }

            var fileName = line[(close + 2)..];
            var end = fileName.IndexOf(')');
            if (end >= 0 && TryGetDetailEntryId(fileName[..end], out var entryId))
            {
                names.Add(DetailFileName(entryId));
            }
        }

        return names;
    }

    private static int ClosingLinkDelimiter(string line)
    {
        for (var index = 3; index + 1 < line.Length; index++)
        {
            if (line[index] == ']' && line[index + 1] == '(' && (index == 0 || line[index - 1] != '\\'))
            {
                return index;
            }
        }

        return -1;
    }

    private static byte[] Merge(byte[] existing, byte[] renderedSection)
    {
        var starts = Matches(existing, StartBytes);
        var ends = Matches(existing, EndBytes);
        if (starts.Count == 0 && ends.Count == 0)
        {
            // A root that has never had a Baton section is the migration entry point. The old bytes
            // are retained as an exact prefix; the separator and section are newly Baton-owned bytes.
            var separator = existing.Length == 0 ? [] : new byte[] { (byte)'\n' };
            return [.. existing, .. separator, .. renderedSection];
        }

        if (!TryFindOwnedSection(existing, out var ownedSection))
        {
            throw new InvalidDataException(
                "Baton memory index markers are missing, duplicated, nested, reordered, or malformed; " +
                "Baton refused to rewrite the vendor index.");
        }

        var before = existing.AsSpan(0, ownedSection.Start);
        var afterOffset = ownedSection.End + EndBytes.Length;
        // The line ending immediately after our end marker is part of the section we generated.
        // Keeping it as surrounding content would add one newline on every regeneration.
        if (afterOffset < existing.Length && existing[afterOffset] == (byte)'\r')
        {
            afterOffset++;
        }

        if (afterOffset < existing.Length && existing[afterOffset] == (byte)'\n')
        {
            afterOffset++;
        }

        var after = existing.AsSpan(afterOffset);
        return [.. before, .. renderedSection, .. after];
    }

    private static List<int> Matches(byte[] bytes, byte[] marker)
    {
        var matches = new List<int>();
        for (var i = 0; i <= bytes.Length - marker.Length; i++)
        {
            if (bytes.AsSpan(i, marker.Length).SequenceEqual(marker))
            {
                matches.Add(i);
            }
        }

        return matches;
    }

    private static bool IsWholeLine(byte[] bytes, int offset, int length) =>
        (offset == 0 || bytes[offset - 1] is (byte)'\n' or (byte)'\r')
        && (offset + length == bytes.Length || bytes[offset + length] is (byte)'\n' or (byte)'\r');

    private static bool TryFindOwnedSection(byte[] bytes, out OwnedSection section)
    {
        var starts = Matches(bytes, StartBytes);
        var ends = Matches(bytes, EndBytes);
        if (starts.Count == 1 && ends.Count == 1 && starts[0] < ends[0]
            && IsWholeLine(bytes, starts[0], StartBytes.Length)
            && IsWholeLine(bytes, ends[0], EndBytes.Length))
        {
            section = new OwnedSection(starts[0], ends[0]);
            return true;
        }

        section = default;
        return false;
    }

    private static byte[] RenderDetail(MemoryProjectionCandidate candidate)
    {
        var entry = candidate.Entry;
        var text = "<!-- baton:projection v1 -->\n" +
                   "<!-- baton:memory-detail id=" + entry.Id + " -->\n\n" +
                   "# " + Title(entry) + "\n\n" + entry.Text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n') + "\n";
        return Encoding.UTF8.GetBytes(text);
    }

    private static string Title(MemoryEntry entry)
    {
        var heading = entry.Text.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("#", StringComparison.Ordinal));
        var title = heading is null ? Path.GetFileNameWithoutExtension(entry.SourcePath) : heading.TrimStart('#').Trim();
        return OneLine(string.IsNullOrWhiteSpace(title) ? entry.Id : title);
    }

    private static string Description(MemoryEntry entry, string title)
    {
        var line = entry.Text.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal));
        var description = OneLine(line ?? title);
        return description.Length <= 180 ? description : description[..177] + "...";
    }

    private static string OneLine(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Replace(SectionStart, "&lt;!-- baton:memory-index:start -->", StringComparison.Ordinal)
        .Replace(SectionEnd, "&lt;!-- baton:memory-index:end -->", StringComparison.Ordinal)
        .Replace("[", "\\[").Replace("]", "\\]");

    /// <summary>
    /// Removes a well-formed Baton section from text an importer has already decoded. This is the
    /// migration complement to detail-file marker detection: vendor-authored index bytes remain an
    /// import source while Baton cannot re-import its own catalog.
    /// </summary>
    public static bool TryStripOwnedSection(string text, out string outside)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (!TryFindOwnedSection(bytes, out var section))
        {
            outside = text;
            return false;
        }

        var prefixLength = section.Start;
        // Merge adds this separator as Baton-owned syntax, even when the vendor index already ended
        // in a newline, so stripping restores the exact vendor prefix in the ordinary UTF-8 case.
        if (prefixLength > 0 && text[prefixLength - 1] == '\n')
        {
            prefixLength--;
        }

        var suffixStart = section.End + EndBytes.Length;
        if (suffixStart < bytes.Length && bytes[suffixStart] == (byte)'\r')
        {
            suffixStart++;
        }

        if (suffixStart < bytes.Length && bytes[suffixStart] == (byte)'\n')
        {
            suffixStart++;
        }

        outside = Encoding.UTF8.GetString(bytes.AsSpan(0, prefixLength))
            + Encoding.UTF8.GetString(bytes.AsSpan(suffixStart));
        return true;
    }

    private static bool TryGetDetailEntryId(string fileName, out string entryId)
    {
        entryId = string.Empty;
        if (string.IsNullOrEmpty(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || !fileName.StartsWith(DetailPrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(DetailSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var idLength = fileName.Length - DetailPrefix.Length - DetailSuffix.Length;
        if (idLength != 32)
        {
            return false;
        }

        entryId = fileName.Substring(DetailPrefix.Length, idLength);
        return IsDerivedEntryId(entryId);
    }

    private static bool IsDerivedEntryId(string? entryId) =>
        entryId is { Length: 32 } && entryId.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private readonly record struct OwnedSection(int Start, int End);
}

public sealed record VendorDetailFile(string FileName, byte[] Bytes);

public sealed record VendorIndexPublication(byte[] IndexBytes, IReadOnlyList<VendorDetailFile> Details);
