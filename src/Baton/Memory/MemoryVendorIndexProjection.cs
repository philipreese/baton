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
        ArgumentException.ThrowIfNullOrEmpty(entryId);
        return DetailPrefix + entryId + DetailSuffix;
    }

    /// <summary>Whether a root file is an explicitly Baton-named per-entry detail file.</summary>
    public static bool IsOwnedDetailFile(string fileName) =>
        fileName.StartsWith(DetailPrefix, StringComparison.OrdinalIgnoreCase)
        && fileName.EndsWith(DetailSuffix, StringComparison.OrdinalIgnoreCase);

    private static byte[] Merge(byte[] existing, byte[] section)
    {
        var starts = Matches(existing, StartBytes);
        var ends = Matches(existing, EndBytes);
        if (starts.Count == 0 && ends.Count == 0)
        {
            // A root that has never had a Baton section is the migration entry point. The old bytes
            // are retained as an exact prefix; the separator and section are newly Baton-owned bytes.
            var separator = existing.Length == 0 ? [] : new byte[] { (byte)'\n' };
            return [.. existing, .. separator, .. section];
        }

        if (starts.Count != 1 || ends.Count != 1 || starts[0] >= ends[0]
            || !IsWholeLine(existing, starts[0], StartBytes.Length)
            || !IsWholeLine(existing, ends[0], EndBytes.Length))
        {
            throw new InvalidDataException(
                "Baton memory index markers are missing, duplicated, nested, reordered, or malformed; " +
                "Baton refused to rewrite the vendor index.");
        }

        var before = existing.AsSpan(0, starts[0]);
        var afterOffset = ends[0] + EndBytes.Length;
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
        return [.. before, .. section, .. after];
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
        .Replace("[", "\\[").Replace("]", "\\]");

    /// <summary>
    /// Removes a well-formed Baton section from text an importer has already decoded. This is the
    /// migration complement to detail-file marker detection: vendor-authored index bytes remain an
    /// import source while Baton cannot re-import its own catalog.
    /// </summary>
    public static bool TryStripOwnedSection(string text, out string outside)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = text.IndexOf(SectionStart, StringComparison.Ordinal);
        var end = text.IndexOf(SectionEnd, StringComparison.Ordinal);
        if (start < 0 || end < start
            || text.IndexOf(SectionStart, start + SectionStart.Length, StringComparison.Ordinal) >= 0
            || text.IndexOf(SectionEnd, end + SectionEnd.Length, StringComparison.Ordinal) >= 0)
        {
            outside = text;
            return false;
        }

        var prefixLength = start;
        // Merge adds this separator as Baton-owned syntax, even when the vendor index already ended
        // in a newline, so stripping restores the exact vendor prefix in the ordinary UTF-8 case.
        if (prefixLength > 0 && text[prefixLength - 1] == '\n')
        {
            prefixLength--;
        }

        var suffixStart = end + SectionEnd.Length;
        if (suffixStart < text.Length && text[suffixStart] == '\r')
        {
            suffixStart++;
        }

        if (suffixStart < text.Length && text[suffixStart] == '\n')
        {
            suffixStart++;
        }

        outside = text[..prefixLength] + text[suffixStart..];
        return true;
    }
}

public sealed record VendorDetailFile(string FileName, byte[] Bytes);

public sealed record VendorIndexPublication(byte[] IndexBytes, IReadOnlyList<VendorDetailFile> Details);
