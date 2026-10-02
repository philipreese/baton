using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Baton.Queue;

namespace Baton.Cli.Tests;

/// <summary>
/// Test-only passive capture for #2515: when one of the two named fixtures' own
/// <see cref="QueueStore"/> call fails with a real IOException/UnauthorizedAccessException-backed
/// <see cref="QueueStoreException"/>, retain a bounded, best-effort copy of the caller-supplied queue
/// paths before the fixture's own <c>finally</c> deletes its disposable home — then the caller
/// rethrows the identical original exception. This is not a production diagnostic, not an atomic
/// failure-time snapshot, and not a cause finding: <see cref="QueueStore"/> has already attempted and
/// either completed or abandoned its own staged-temp cleanup by the time this runs, so deleted staged
/// bytes, the file's exact failure-time attributes, and any competing actor remain unknown and are
/// recorded as such.
/// </summary>
internal static class QueueFailureEvidence
{
    internal const int MaxRetainedFiles = 8;
    internal const long MaxTotalBytes = 1024 * 1024;
    internal const long ManifestReserveBytes = 64 * 1024;
    private const int MaxEnumeratedEntries = 64;
    private const int MaxFieldLength = 2048;
    private const int MaxSkipRecords = 32;

    private static readonly Regex OperationPattern = new(
        @"Could not perform (?<op>.+?) for the queue at", RegexOptions.Compiled);

    /// <summary>
    /// The real held-destination family this slice captures. A validation or malformed-JSON
    /// <see cref="QueueStoreException"/> carries no <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> inner and is unrelated: it must create no artifact.
    /// </summary>
    internal static bool IsCapturable(QueueStoreException exception) =>
        exception.InnerException is IOException or UnauthorizedAccessException;

    /// <summary>
    /// Retains a bounded, best-effort evidence set for <paramref name="original"/> under a fresh
    /// directory inside <paramref name="retentionRoot"/> (default: the machine temp root), and returns
    /// that directory, or <see langword="null"/> if nothing was retained. Never throws: a capture or
    /// output failure here must never replace the original <see cref="QueueStoreException"/> the
    /// caller is unwinding — the caller always rethrows that same original failure regardless of this
    /// method's result.
    /// </summary>
    internal static string? Retain(
        QueueStoreException original,
        string testName,
        string fixtureRoot,
        IReadOnlyList<string> queuePaths,
        string? retentionRoot = null)
    {
        try
        {
            return RetainCore(original, testName, fixtureRoot, queuePaths, retentionRoot ?? Path.GetTempPath());
        }
        catch (Exception)
        {
            // Capture is strictly best-effort; any failure here must leave the original exception the
            // caller is about to rethrow completely unaffected.
            return null;
        }
    }

    /// <summary>
    /// Logs <paramref name="retained"/> (if non-null) to the active test's output, best-effort. The
    /// caller invokes this from inside the same catch block that is about to rethrow the original
    /// <see cref="QueueStoreException"/>: a diagnostic-output failure here must not replace it either.
    /// </summary>
    internal static void ReportRetained(string? retained, Action<string>? write = null)
    {
        if (retained is null)
        {
            return;
        }

        try
        {
            (write ?? DefaultReport)(retained);
        }
        catch (Exception)
        {
            // Best-effort diagnostic output only; the caller's original exception still rethrows.
        }
    }

    private static void DefaultReport(string retained) =>
        TestContext.Current.TestOutputHelper?.WriteLine($"Retained post-unwind queue evidence at '{retained}'.");

    private static string? RetainCore(
        QueueStoreException original,
        string testName,
        string fixtureRoot,
        IReadOnlyList<string> queuePaths,
        string retentionRoot)
    {
        var fixtureFull = Path.GetFullPath(fixtureRoot);
        var tempFull = Path.GetFullPath(Path.GetTempPath());
        if (!Directory.Exists(fixtureFull) || IsReparsePoint(fixtureFull))
        {
            return null;
        }

        // The issue's explicit bound is "reject ... user/vendor/production homes", not just "the
        // bare temp directory". Both real callers hand this an owned fixture they created themselves
        // under the machine temp root, so requiring a STRICT descendant of temp (never temp itself,
        // never anything outside it) rejects a real home outright without narrowing what either
        // caller actually does.
        if (!IsWithin(fixtureFull, tempFull) || PathsEqual(fixtureFull, tempFull))
        {
            return null;
        }

        // A reparse-point ancestor between temp and the fixture root would let a lexically-contained
        // fixture resolve somewhere else entirely; components above temp itself are never checked,
        // since profile redirection there is ordinary and out of this capture's concern.
        if (HasReparsePointAncestor(fixtureFull, tempFull))
        {
            return null;
        }

        var retentionFull = Path.GetFullPath(retentionRoot);
        var destination = Path.Combine(retentionFull,
            "baton_queue_evidence_" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)
            + "_" + Guid.NewGuid().ToString("N"));
        if (IsWithin(destination, fixtureFull))
        {
            // The retained copy must outlive the fixture's own DirectoryCleanup; a destination nested
            // inside the fixture root would be deleted along with everything else.
            return null;
        }

        var candidates = new List<(string Label, string Path)>();
        var skipped = new List<string>();
        var enumerationBudget = MaxEnumeratedEntries;

        // Caller paths need their own prefix bound: limiting only sibling enumeration still lets
        // an arbitrarily long caller list grow candidates and diagnostic metadata without limit.
        foreach (var queuePath in queuePaths.Take(MaxEnumeratedEntries))
        {
            var full = Path.GetFullPath(queuePath);
            if (!IsWithin(full, fixtureFull))
            {
                AddSkip(skipped, $"escaped-root: {Bound(queuePath)}");
                continue;
            }

            // The lexical IsWithin check above only looks at the string, not what the entry actually
            // resolves to: a reparse point (symlink/junction) lexically under the fixture root can
            // still target bytes outside it. File.GetAttributes reads the reparse point's own
            // metadata rather than following it, so this rejects the link itself without ever opening
            // whatever it points to. An ancestor directory between the fixture root and this path can
            // be the reparse point instead of the leaf itself, so the whole chain is checked, not just
            // the leaf.
            if ((File.Exists(full) && IsReparsePoint(full)) || HasReparsePointAncestor(full, fixtureFull))
            {
                AddSkip(skipped, $"reparse-point: {Bound(queuePath)}");
                continue;
            }

            candidates.Add(("queue-path", full));

            var directory = Path.GetDirectoryName(full);
            if (directory is null || !Directory.Exists(directory) || enumerationBudget <= 0)
            {
                continue;
            }

            // Non-recursive sibling scan only — this is how a surviving `*.tmp` staged-write sibling is
            // found. It is never a recursive copy of the fixture home.
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddSkip(skipped, $"sibling-directory-unreadable: {Bound(directory)}");
                continue;
            }

            foreach (var entry in entries)
            {
                if (enumerationBudget-- <= 0)
                {
                    AddSkip(skipped, "enumeration-exhausted");
                    break;
                }

                var entryFull = Path.GetFullPath(entry);
                if (PathsEqual(entryFull, full) || Directory.Exists(entryFull))
                {
                    continue;
                }

                // A directory-typed reparse point is already filtered out above by Directory.Exists
                // (its own entry carries the directory bit even unresolved). A file-typed reparse
                // point is not, so it must be rejected here before CaptureOne ever opens it — opening
                // it follows the link and copies whatever it actually points to.
                if (IsReparsePoint(entryFull))
                {
                    AddSkip(skipped, $"sibling-reparse-point: {Bound(entryFull)}");
                    continue;
                }

                candidates.Add(("sibling", entryFull));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        Directory.CreateDirectory(destination);

        var remainingBudget = MaxTotalBytes - ManifestReserveBytes;
        var manifest = new StringBuilder();
        manifest.AppendLine("Baton test-only queue failure evidence (#2515) — post-unwind capture.");
        manifest.AppendLine(
            "QueueStore has already attempted its own staged-temp cleanup before this ran. This is NOT " +
            "an atomic failure-time snapshot and does NOT prove what was deleted, the file's exact " +
            "attributes at failure time, or any competing actor — those remain unknown.");
        manifest.AppendLine($"Test: {Bound(testName)}");
        manifest.AppendLine($"Observed (UTC): {DateTimeOffset.UtcNow:O}");
        manifest.AppendLine($"Outer exception: {original.GetType().FullName}");
        manifest.AppendLine($"Outer message: {Bound(original.Message)}");
        manifest.AppendLine($"Operation: {DescribeOperation(original.Message)}");
        var inner = original.InnerException;
        manifest.AppendLine(inner is null
            ? "Inner: none"
            : $"Inner: {inner.GetType().FullName} HResult=0x{inner.HResult:X8}");
        manifest.AppendLine("Unknown: deleted staged bytes, failure-time attributes, blocking actor.");
        if (queuePaths.Count > MaxEnumeratedEntries)
        {
            manifest.AppendLine($"caller-path-limit-exhausted: inspected={MaxEnumeratedEntries}; supplied={queuePaths.Count}");
        }

        var retainedCount = 0;
        foreach (var (label, path) in candidates)
        {
            if (retainedCount >= MaxRetainedFiles)
            {
                AddSkip(skipped, $"file-limit: {Bound(path)}");
                continue;
            }

            var record = CaptureOne(label, path, destination, retainedCount, ref remainingBudget);
            manifest.AppendLine(record);
            if (record.Contains("status=retained", StringComparison.Ordinal))
            {
                retainedCount++;
            }
        }

        if (skipped.Count > 0)
        {
            manifest.AppendLine("Skipped/bounded entries:");
            foreach (var line in skipped.Take(MaxSkipRecords))
            {
                manifest.AppendLine("  " + line);
            }

            if (skipped.Count > MaxSkipRecords)
            {
                manifest.AppendLine($"  ...and {skipped.Count - MaxSkipRecords} more (bounded).");
            }
        }

        // ManifestReserveBytes bounds encoded BYTES, not characters: slicing the string by character
        // count can both exceed that byte bound (non-ASCII content encodes wider than its character
        // count) and throw (a byte-sized character slice can run past the string's own length when
        // characters are multi-byte). Truncate the encoded bytes themselves instead.
        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToString());
        if (manifestBytes.Length > ManifestReserveBytes)
        {
            var marker = Encoding.UTF8.GetBytes("\nmanifest-truncated: UTF-8 byte limit; later metadata omitted.\n");
            var prefix = TruncateUtf8(manifestBytes, checked((int)ManifestReserveBytes - marker.Length));
            manifestBytes = [.. prefix, .. marker];
        }

        File.WriteAllBytes(Path.Combine(destination, "manifest.txt"), manifestBytes);
        return destination;
    }

    private static string CaptureOne(string label, string path, string destination, int index, ref long remainingBudget)
    {
        FileInfo before;
        try
        {
            before = new FileInfo(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"- {label} '{Bound(path)}': status=unreadable stat-failed {ex.GetType().Name}";
        }

        if (!before.Exists)
        {
            return $"- {label} '{Bound(path)}': status=absent";
        }

        if (remainingBudget <= 0)
        {
            return $"- {label} '{Bound(path)}': status=skipped reason=over-limit length={before.Length}";
        }

        var toRead = (int)Math.Min(before.Length, remainingBudget);
        byte[] buffer;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            buffer = new byte[toRead];
            var readTotal = 0;
            while (readTotal < toRead)
            {
                var read = stream.Read(buffer, readTotal, toRead - readTotal);
                if (read <= 0) break;
                readTotal += read;
            }

            if (readTotal != buffer.Length)
            {
                Array.Resize(ref buffer, readTotal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"- {label} '{Bound(path)}': status=unreadable {ex.GetType().Name} HResult=0x{ex.HResult:X8}";
        }

        var destinationFile = Path.Combine(destination, $"{index}_{SanitizeFileName(Path.GetFileName(path))}");
        File.WriteAllBytes(destinationFile, buffer);
        remainingBudget -= buffer.Length;

        var hash = Convert.ToHexString(SHA256.HashData(buffer)).ToLowerInvariant();
        var partial = buffer.Length < before.Length;

        var after = new FileInfo(path);
        var changed = after.Exists
            && (after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc);

        return $"- {label} '{Bound(path)}': status=retained bytes={buffer.Length}"
            + $" original-length={before.Length} partial-prefix={partial} changed-during-capture={changed}"
            + $" attributes={before.Attributes} created-utc={before.CreationTimeUtc:O}"
            + $" written-utc={before.LastWriteTimeUtc:O} sha256={hash}";
    }

    private static string DescribeOperation(string message)
    {
        var match = OperationPattern.Match(message ?? string.Empty);
        if (match.Success)
        {
            return Bound(match.Groups["op"].Value);
        }

        return message is not null && message.Contains("Could not read the queue at", StringComparison.Ordinal)
            ? "read"
            : "unavailable";
    }

    private static void AddSkip(List<string> skipped, string reason)
    {
        if (skipped.Count < MaxSkipRecords)
        {
            skipped.Add(reason);
        }
    }

    private static string Bound(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value[..Math.Min(value.Length, MaxFieldLength)];

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }

    /// <summary>
    /// Truncates already-UTF8-encoded <paramref name="bytes"/> to at most <paramref name="maxBytes"/>
    /// bytes, backing the cut off whatever multi-byte character it would otherwise split. A
    /// continuation byte (<c>10xxxxxx</c>) sitting right at the cut point means the boundary falls
    /// inside a character that started earlier in the kept prefix.
    /// </summary>
    internal static byte[] TruncateUtf8(byte[] bytes, int maxBytes)
    {
        if (bytes.Length <= maxBytes)
        {
            return bytes;
        }

        var cut = maxBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
        {
            cut--;
        }

        return bytes[..cut];
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Walks every directory strictly between <paramref name="path"/> and <paramref name="stopAt"/>
    /// (exclusive of both), rejecting as a whole if any component is itself a reparse point. A
    /// reparse point lexically under <paramref name="stopAt"/> can resolve anywhere; checking only
    /// <paramref name="path"/>'s own leaf would miss one sitting at an intermediate directory.
    /// </summary>
    private static bool HasReparsePointAncestor(string path, string stopAt)
    {
        var current = Path.GetDirectoryName(path);
        while (current is not null && !PathsEqual(current, stopAt))
        {
            // A missing ancestor is not itself a reparse point; IsReparsePoint fails closed (treats
            // an unreadable path as one), which would otherwise mislabel a merely-absent directory.
            if (Directory.Exists(current) && IsReparsePoint(current))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || PathsEqual(parent, current))
            {
                break;
            }

            current = parent;
        }

        return false;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsWithin(string candidate, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.StartsWith(normalizedRoot, comparison) || PathsEqual(candidate, root);
    }
}
