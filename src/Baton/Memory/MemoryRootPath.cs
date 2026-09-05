using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Memory;

/// <summary>How a root's checkout path was arrived at — the provenance of the path, not of the memory.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemoryPathSource>))]
public enum MemoryPathSource
{
    /// <summary>
    /// Read from the <c>cwd</c> field of this project's own session transcripts, which record the
    /// absolute path verbatim. Ground truth: it is the value the encoding was derived FROM.
    /// </summary>
    [JsonStringEnumMemberName("session-cwd")] SessionCwd,

    /// <summary>Decoded from the directory name, which admitted exactly one reading.</summary>
    [JsonStringEnumMemberName("decoded-unique")] DecodedUnique,

    /// <summary>
    /// Decoded from the directory name, which admitted several readings, exactly one of which names a
    /// directory that exists. Disk broke the tie, not the decoder.
    /// </summary>
    [JsonStringEnumMemberName("decoded-existing")] DecodedExisting,

    /// <summary>Several readings survive and nothing available here picks one. Reported, never guessed.</summary>
    [JsonStringEnumMemberName("ambiguous")] Ambiguous,

    /// <summary>The name yields no usable path at all.</summary>
    [JsonStringEnumMemberName("unresolvable")] Unresolvable,
}

/// <summary>
/// Which checkout a memory root belongs to, and how confidently.
/// </summary>
/// <param name="CheckoutPath">
/// The single path this root resolved to, or <see langword="null"/> when <paramref name="Source"/> is
/// <see cref="MemoryPathSource.Ambiguous"/> or <see cref="MemoryPathSource.Unresolvable"/> — an
/// ambiguous resolution deliberately carries no chosen path, so no caller can accidentally treat one
/// candidate as the answer.
/// </param>
/// <param name="Source">How the path was arrived at.</param>
/// <param name="Candidates">
/// The readings under consideration, for an operator's eyes. One entry when the resolution is
/// certain; several when it is ambiguous, capped at <see cref="MemoryRootPath.MaxReportedCandidates"/>
/// because the point is to show that more than one reading survives, not to enumerate them all.
/// </param>
public sealed record MemoryRootPathResolution(
    string? CheckoutPath,
    MemoryPathSource Source,
    IReadOnlyList<string> Candidates);

/// <summary>
/// Turns a Claude project directory name (<c>C--Users-pbree-source-repos-baton</c>) back into the
/// checkout path it was encoded from — and, far more importantly, reports honestly when it cannot.
/// </summary>
/// <remarks>
/// <para>
/// <b>The encoding is lossy, and the decoder alone is therefore ambiguous.</b> Claude Code flattens a
/// path into a directory name by replacing several distinct characters with <c>-</c>, so a name like
/// <c>C--Users-pbree-source-repos-aer-aer-flow</c> reads equally well as
/// <c>C:\Users\pbree\source\repos\aer\aer-flow</c> and as
/// <c>C:\Users\pbree\source\repos\aer-aer-flow</c>. This decoder inverts only the two commonest
/// readings of a <c>-</c>: a directory separator, or a literal hyphen. <b>It cannot recover a
/// <c>.</c> or a <c>_</c> at all</b> — <c>C--Users-pbree--baton</c> was
/// <c>C:\Users\pbree\.baton</c>, and no reading here produces that. State that negative plainly
/// rather than let a reader assume the decoder is merely imprecise: for those names the decoder has
/// no right answer among its candidates, and the session-<c>cwd</c> ground truth below is the only
/// thing that resolves them.
/// </para>
/// <para>
/// <b>Ground truth first.</b> Every session transcript under a project directory records the absolute
/// <c>cwd</c> it ran in — the value the directory name was derived from. When the transcripts agree on
/// one, that is the answer and the decoder is not consulted. When they disagree, or there are none
/// (every archived root), the decoder runs and its ambiguity is reported as ambiguity.
/// </para>
/// <para>
/// <b>Candidates are enumerated lazily and never counted.</b> A name with <i>n</i> interior hyphens
/// admits 2^<i>n</i> readings; nothing here needs that number. Every question this type answers is
/// "is there a second one?", so every enumeration is short-circuited at the second hit.
/// </para>
/// </remarks>
public static class MemoryRootPath
{
    /// <summary>How many candidate readings an ambiguous resolution carries for display.</summary>
    public const int MaxReportedCandidates = 4;

    /// <summary>Session transcripts read per project directory, newest first. A bound, not a sample size — see <see cref="ReadSessionWorkingDirectories"/>.</summary>
    public const int MaxSessionFilesRead = 50;

    /// <summary>
    /// Hard ceiling on readings <see cref="DecodeCandidates"/> will produce for one name. A name with
    /// <i>n</i> interior hyphens has 2^<i>n</i> of them, and every caller here short-circuits long
    /// before that — but a caller filtering by disk existence walks the whole set when none exists, so
    /// this is what keeps a pathologically long name from stalling an audit rather than reporting one.
    /// Truncation is invisible to every answer this type gives: each is "is there a second reading?",
    /// and a truncated enumeration still has one if the full one did.
    /// </summary>
    public const int MaxReadingsEnumerated = 4096;

    /// <summary>
    /// Every checkout path <paramref name="directoryName"/> could have been encoded from, most-separators
    /// reading first, lazily. A reading containing an empty path segment (two separators in a row) is
    /// skipped — no such directory can exist — which is why a name may yield fewer readings than its
    /// hyphen count suggests, and occasionally none.
    /// </summary>
    public static IEnumerable<string> DecodeCandidates(string directoryName)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryName);

        // A leading "X--" is the drive: "C:\" encodes to "C" + ':' -> '-' + '\' -> '-'. Everything
        // after it is the path body, whose hyphens are what this enumerates.
        string prefix;
        string body;
        if (directoryName.Length >= 3 && char.IsAsciiLetter(directoryName[0]) && directoryName[1] == '-' && directoryName[2] == '-')
        {
            prefix = $"{char.ToUpperInvariant(directoryName[0])}:{Path.DirectorySeparatorChar}";
            body = directoryName[3..];
        }
        else
        {
            prefix = string.Empty;
            body = directoryName;
        }

        if (body.Length == 0)
        {
            yield break;
        }

        var tokens = body.Split('-');
        var emitted = 0;
        foreach (var reading in Expand(tokens, 0))
        {
            // An empty segment means two separators landed together, which no real path has.
            if (reading.Length == 0 || reading.StartsWith(Path.DirectorySeparatorChar) ||
                reading.EndsWith(Path.DirectorySeparatorChar) ||
                reading.Contains($"{Path.DirectorySeparatorChar}{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            yield return prefix + reading;

            if (++emitted >= MaxReadingsEnumerated)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Every way of joining <paramref name="tokens"/> from <paramref name="index"/> onward, each gap
    /// being either a separator or a literal hyphen. Separator first, so the commonest reading of a
    /// project directory name is the first candidate offered.
    /// </summary>
    private static IEnumerable<string> Expand(string[] tokens, int index)
    {
        if (index == tokens.Length - 1)
        {
            yield return tokens[index];
            yield break;
        }

        foreach (var tail in Expand(tokens, index + 1))
        {
            yield return tokens[index] + Path.DirectorySeparatorChar + tail;
            yield return tokens[index] + "-" + tail;
        }
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="directoryName"/> admits more than one reading — the
    /// control this type's own tests read: it must be true for a name whose true path is only knowable
    /// from a session <c>cwd</c>, or the session-first ordering below is proving nothing.
    /// </summary>
    public static bool IsAmbiguousByName(string directoryName) =>
        DecodeCandidates(directoryName).Take(2).Count() > 1;

    /// <summary>
    /// The distinct working directories this project's session transcripts recorded, in first-seen
    /// order.
    /// </summary>
    /// <remarks>
    /// Reads the FIRST line of each <c>*.jsonl</c> under <paramref name="sessionDirectoryPath"/>,
    /// newest file first, up to <see cref="MaxSessionFilesRead"/> of them: a session's opening record
    /// carries its <c>cwd</c>, and a project with 149 transcripts must not cost 149 full file reads to
    /// audit. <b>These are session transcripts, not memory files</b> — the audit never opens a memory
    /// file for anything but its digest. The bound is why a project whose first 50 transcripts agree
    /// reads as unanimous even if a 51st disagreed; that is a smaller error than an audit nobody runs.
    /// </remarks>
    public static IReadOnlyList<string> ReadSessionWorkingDirectories(string? sessionDirectoryPath)
    {
        if (string.IsNullOrEmpty(sessionDirectoryPath) || !Directory.Exists(sessionDirectoryPath))
        {
            return [];
        }

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> sessionFiles;
        try
        {
            sessionFiles = new DirectoryInfo(sessionDirectoryPath)
                .EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxSessionFilesRead)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var file in sessionFiles)
        {
            if (TryReadWorkingDirectory(file) is { Length: > 0 } cwd && seen.Add(cwd))
            {
                found.Add(cwd);
            }
        }

        return found;
    }

    private static string? TryReadWorkingDirectory(string sessionFilePath)
    {
        try
        {
            using var reader = new StreamReader(
                new FileStream(sessionFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                Encoding.UTF8);

            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("cwd", out var cwd) &&
                   cwd.ValueKind == JsonValueKind.String
                ? cwd.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable or non-JSON transcript contributes no ground truth; the decoder still runs.
            return null;
        }
    }

    /// <summary>
    /// Which checkout <paramref name="directoryName"/> belongs to, preferring ground truth over
    /// decoding and reporting ambiguity rather than picking.
    /// </summary>
    /// <param name="directoryName">The encoded root directory name.</param>
    /// <param name="sessionWorkingDirectories">
    /// What <see cref="ReadSessionWorkingDirectories"/> found. Exactly one value is ground truth; more
    /// than one is a genuine ambiguity (a project directory that two different cwds encoded onto), and
    /// none sends this to the decoder.
    /// </param>
    /// <param name="directoryExists">
    /// Existence probe, injected so this stays testable against a fixture tree without one. Production
    /// callers pass <see cref="Directory.Exists(string)"/>.
    /// </param>
    public static MemoryRootPathResolution Resolve(
        string directoryName,
        IReadOnlyList<string> sessionWorkingDirectories,
        Func<string, bool> directoryExists)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryName);
        ArgumentNullException.ThrowIfNull(sessionWorkingDirectories);
        ArgumentNullException.ThrowIfNull(directoryExists);

        switch (sessionWorkingDirectories.Count)
        {
            case 1:
                return new MemoryRootPathResolution(
                    sessionWorkingDirectories[0], MemoryPathSource.SessionCwd, [sessionWorkingDirectories[0]]);
            case > 1:
                return new MemoryRootPathResolution(
                    null, MemoryPathSource.Ambiguous, sessionWorkingDirectories.Take(MaxReportedCandidates).ToList());
        }

        // No ground truth. Disk breaks a decoder tie when exactly one reading names a real directory.
        var existing = DecodeCandidates(directoryName).Where(directoryExists).Take(MaxReportedCandidates + 1).ToList();
        if (existing.Count == 1)
        {
            return new MemoryRootPathResolution(existing[0], MemoryPathSource.DecodedExisting, existing);
        }

        if (existing.Count > 1)
        {
            return new MemoryRootPathResolution(
                null, MemoryPathSource.Ambiguous, existing.Take(MaxReportedCandidates).ToList());
        }

        // Nothing on disk: the path is gone (or never was here). The decoder's own ambiguity is all
        // that is left to report.
        var candidates = DecodeCandidates(directoryName).Take(MaxReportedCandidates + 1).ToList();
        return candidates.Count switch
        {
            0 => new MemoryRootPathResolution(null, MemoryPathSource.Unresolvable, []),
            1 => new MemoryRootPathResolution(candidates[0], MemoryPathSource.DecodedUnique, candidates),
            _ => new MemoryRootPathResolution(
                null, MemoryPathSource.Ambiguous, candidates.Take(MaxReportedCandidates).ToList()),
        };
    }
}
