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
    /// Decoded from the directory name, which admitted several readings, exactly one of which is an
    /// absolute path naming a checkout that is present on this machine. Disk broke the tie, not the
    /// decoder — and only a <b>fully qualified</b> reading is ever offered to disk, so this source
    /// never depends on where the process happened to be running from.
    /// </summary>
    [JsonStringEnumMemberName("decoded-existing")] DecodedExisting,

    /// <summary>Several readings survive and nothing available here picks one. Reported, never guessed.</summary>
    [JsonStringEnumMemberName("ambiguous")] Ambiguous,

    /// <summary>
    /// The name yields no usable path at all — either no reading survives the decoder, or the single
    /// reading it produced is not a fully qualified path and so names nothing on this machine.
    /// </summary>
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
/// <b>Ground truth first.</b> A session transcript under a project directory records the absolute
/// <c>cwd</c> it ran in — the value the directory name was derived from. That the field is present is
/// an observation about this vendor's transcripts, not a guarantee it publishes, so the read scans for
/// it rather than assuming where it sits (<see cref="ReadSessionWorkingDirectories"/>) and a transcript
/// that carries none costs nothing but its own read. When the transcripts agree on one, that is the
/// answer and the decoder is not consulted. When they disagree, or there are none (every archived
/// root), the decoder runs and its ambiguity is reported as ambiguity.
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
    /// Lines scanned per session transcript while looking for a <c>cwd</c>. A transcript is a JSONL
    /// stream whose records vary, so the field is looked for rather than assumed to sit on line 1 — but
    /// a transcript can be tens of megabytes, and reading one whole to learn one string is what this
    /// bound refuses. A transcript that carries no <c>cwd</c> in its first
    /// <see cref="MaxSessionLinesScanned"/> lines contributes no ground truth and the decoder runs.
    /// </summary>
    public const int MaxSessionLinesScanned = 64;

    /// <summary>
    /// Hard ceiling on readings <see cref="DecodeCandidates"/> will produce for one name. A name with
    /// <i>n</i> interior hyphens has 2^<i>n</i> of them, and every caller here short-circuits long
    /// before that — but a caller filtering by disk existence walks the whole set when none exists, so
    /// this is what keeps a pathologically long name from stalling an audit rather than reporting one.
    /// <para>
    /// <b>What truncation can and cannot change.</b> It cannot change an "is there a second reading?"
    /// answer — <see cref="IsAmbiguousByName"/> and the fallback in <see cref="Resolve"/> both
    /// short-circuit at the second hit, and a truncated enumeration still has one if the full one did.
    /// It <i>can</i> change the disk tie-break: that filter walks the enumeration, so a checkout whose
    /// only matching reading sits past this cap is never seen, and the root reports
    /// <see cref="MemoryPathSource.Ambiguous"/> instead of <see cref="MemoryPathSource.DecodedExisting"/>.
    /// The degradation is one-way — a missed reading loses a resolution, it never invents one — which is
    /// why the cap is stated here rather than raised.
    /// </para>
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
    /// <para>
    /// Scans each <c>*.jsonl</c> under <paramref name="sessionDirectoryPath"/>, newest file first, up to
    /// <see cref="MaxSessionFilesRead"/> of them, stopping at the first line of each that carries a
    /// <c>cwd</c> and never reading past <see cref="MaxSessionLinesScanned"/> lines: a project with 149
    /// transcripts must not cost 149 full file reads to audit. <b>The record shape is looked for, not
    /// assumed</b> — a blank, non-JSON, or <c>cwd</c>-less leading line is skipped rather than ending the
    /// file, because "the opening record carries the <c>cwd</c>" is a claim about a vendor's format that
    /// nothing here has measured. It is registered as unmeasured in <c>docs/vendor-doc-audit.md</c>
    /// (§"Still not settled", #1908 re-review low 3), which is also where what it would take to settle it
    /// is recorded; a scan is right whether or not it holds, which is why the claim is registered rather
    /// than pursued.
    /// </para>
    /// <para>
    /// <b>Only <c>cwd</c> is read.</b> No other field of a transcript reaches a report, a log or an
    /// exception message — a parse failure is swallowed rather than surfaced, precisely so no fragment of
    /// a transcript can leave through one. <b>These are session transcripts, not memory files</b> — the
    /// audit never opens a memory file for anything but its digest.
    /// </para>
    /// <para>
    /// <b>A relative <c>cwd</c> is discarded, not trusted.</b> A path that is not fully qualified would
    /// resolve against whatever directory the audit happens to run from, which is the same
    /// working-directory-dependent wrong answer the decoder's own tie-break refuses (see
    /// <see cref="Resolve"/>). Dropping it degrades the root to the decoder's ambiguity.
    /// </para>
    /// <para>
    /// The file bound is why a project whose newest 50 transcripts agree reads as unanimous even if a
    /// 51st disagreed; that is a smaller error than an audit nobody runs.
    /// </para>
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
            if (TryReadWorkingDirectory(file) is { Length: > 0 } cwd &&
                Path.IsPathFullyQualified(cwd) && seen.Add(cwd))
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

            for (var scanned = 0; scanned < MaxSessionLinesScanned; scanned++)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    return null;
                }

                if (ReadWorkingDirectory(line) is { Length: > 0 } cwd)
                {
                    return cwd;
                }
            }

            // The bound ran out before a cwd turned up: no ground truth here, decoder runs.
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable transcript contributes no ground truth; the decoder still runs.
            return null;
        }
    }

    /// <summary>
    /// The <c>cwd</c> one transcript line carries, or <see langword="null"/> when it carries none.
    /// </summary>
    /// <remarks>
    /// The <see cref="JsonException"/> catch is scoped to ONE line on purpose: caught around the whole
    /// file it would make a blank or non-JSON leading line fatal to the scan, which is the very
    /// assumption the scan exists to stop depending on.
    /// </remarks>
    private static string? ReadWorkingDirectory(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("cwd", out var cwd) &&
                   cwd.ValueKind == JsonValueKind.String
                ? cwd.GetString()
                : null;
        }
        catch (JsonException)
        {
            // Not a JSON record: skip this line. Nothing from it is surfaced — the exception carries a
            // fragment of the transcript and is deliberately dropped rather than logged or wrapped.
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
    /// <param name="isCheckoutRoot">
    /// <b>Not a bare existence probe.</b> The narrow question it must answer is the one
    /// <c>RepositoryIdentityResolver.IsWorkTreeRoot</c> defines and production callers pass — never
    /// "is there a directory here", and never "is this path somewhere inside a repository". See the
    /// tie-break's own comment below for the wrong answer each weaker predicate produced. Injected so
    /// this stays testable without a fixture tree, and so the engine holds no notion of git.
    /// </param>
    public static MemoryRootPathResolution Resolve(
        string directoryName,
        IReadOnlyList<string> sessionWorkingDirectories,
        Func<string, bool> isCheckoutRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryName);
        ArgumentNullException.ThrowIfNull(sessionWorkingDirectories);
        ArgumentNullException.ThrowIfNull(isCheckoutRoot);

        switch (sessionWorkingDirectories.Count)
        {
            case 1:
                return new MemoryRootPathResolution(
                    sessionWorkingDirectories[0], MemoryPathSource.SessionCwd, [sessionWorkingDirectories[0]]);
            case > 1:
                return new MemoryRootPathResolution(
                    null, MemoryPathSource.Ambiguous, sessionWorkingDirectories.Take(MaxReportedCandidates).ToList());
        }

        // No ground truth. Disk breaks a decoder tie when exactly one reading names a real checkout —
        // under two filters, each of which is a defect this got wrong first (#1908 review F1):
        //
        //   Path.IsPathFullyQualified: a name with no "X--" drive prefix decodes to RELATIVE readings
        //     (every archived root is keyed by its bare directory name, so `alpaca-agent-bot` yields
        //     `alpaca\agent\bot` and three siblings). Probing one resolves it against the process
        //     working directory, so the SAME machine answers differently depending on where `audit` was
        //     run from, and the answer it gives is a confident wrong checkout. A relative reading names
        //     nothing here; it is never offered to the probe and never becomes a CheckoutPath below.
        //
        //   isCheckoutRoot, not Directory.Exists: git discovers a repository by walking UP, so the
        //     probe that follows this one (RepositoryIdentityResolver) answers with full confidence for
        //     any directory INSIDE a checkout. A reading landing on `...\baton\memory` would therefore
        //     be filed under `baton` as though the decoder had found the checkout. Requiring the reading
        //     to be the work tree's own root is what makes "disk broke the tie" mean the tie was broken
        //     by the thing being looked for.
        //
        // Both failures degrade the same safe way now: to Ambiguous with candidates and no chosen path.
        var existing = DecodeCandidates(directoryName)
            .Where(Path.IsPathFullyQualified)
            .Where(isCheckoutRoot)
            .Take(MaxReportedCandidates + 1)
            .ToList();
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
        // that is left to report. Candidates are still listed as decoded, relative ones included, since
        // they are what an operator reads — but only a fully qualified one may become a CheckoutPath.
        var candidates = DecodeCandidates(directoryName).Take(MaxReportedCandidates + 1).ToList();
        return candidates.Count switch
        {
            0 => new MemoryRootPathResolution(null, MemoryPathSource.Unresolvable, []),
            1 when Path.IsPathFullyQualified(candidates[0]) =>
                new MemoryRootPathResolution(candidates[0], MemoryPathSource.DecodedUnique, candidates),
            1 => new MemoryRootPathResolution(null, MemoryPathSource.Unresolvable, candidates),
            _ => new MemoryRootPathResolution(
                null, MemoryPathSource.Ambiguous, candidates.Take(MaxReportedCandidates).ToList()),
        };
    }
}
