using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Memory;

/// <summary>
/// One imported file: where it was read from, what it hashed to, and which entry in which store it
/// became. The three together are what make an import reversible <b>and</b> checkable — the entry id
/// says what to remove, and the digest says whether the source is still the file that was read.
/// </summary>
/// <param name="SourcePath">Absolute path of the file that was read.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of its bytes at import time.</param>
/// <param name="SourceMtimeUtc">Its last-write time at import time.</param>
/// <param name="SizeBytes">Its length at import time.</param>
/// <param name="SourceVendor">Which vendor's root it sat in.</param>
/// <param name="SourceScope">That root's <see cref="VendorMemoryScope"/>.</param>
/// <param name="EntryId">The <see cref="MemoryEntry.Id"/> it produced.</param>
/// <param name="Repository">The subject it was filed under.</param>
/// <param name="EntriesFilePath">The store file the entry was appended to.</param>
/// <param name="AlreadyPresent">
/// <see langword="true"/> when this entry's id was already in that store file, so the import appended
/// nothing for it. <b>An undo does not remove one of these</b>: the row was written by an earlier
/// import and removing it would reverse work this manifest did not do.
/// </param>
public sealed record ImportManifestRow(
    [property: JsonPropertyName("sourcePath")] string SourcePath,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("sourceMtimeUtc")] DateTime SourceMtimeUtc,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sourceVendor")] string SourceVendor,
    [property: JsonPropertyName("sourceScope")] VendorMemoryScope SourceScope,
    [property: JsonPropertyName("entryId")] string EntryId,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("entriesFile")] string EntriesFilePath,
    [property: JsonPropertyName("alreadyPresent")] bool AlreadyPresent = false);

/// <summary>
/// A file the import saw and did not turn into an entry, and why. Two populations land here and they
/// mean different things — see <see cref="ImportManifest.Unfiled"/> and
/// <see cref="ImportManifest.Machinery"/>.
/// </summary>
/// <param name="SourcePath">Absolute path of the file.</param>
/// <param name="Sha256">Its digest — recorded even here, because provenance is the point of the row.</param>
/// <param name="SourceMtimeUtc">Its last-write time.</param>
/// <param name="SizeBytes">Its length.</param>
/// <param name="Reason">Why it produced no entry, in one clause.</param>
public sealed record ImportSkippedRow(
    [property: JsonPropertyName("sourcePath")] string SourcePath,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("sourceMtimeUtc")] DateTime SourceMtimeUtc,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>
/// The complete, replayable record of one <c>baton memory import</c> — #1852's "migration leaves every
/// original memory file intact and emits a reversible import manifest".
/// </summary>
/// <remarks>
/// <para>
/// <b>It accounts for every file the import looked at, not only the ones it imported.</b> A manifest
/// listing only successes would leave an operator unable to tell a file that was skipped from one that
/// was never seen — and the prior migration this issue exists because of (63 files moved into
/// <c>~/.claude/memory-archive/2026-09-03</c> with no record at all) is what that gap costs.
/// </para>
/// <para>
/// <b>Undo is a replay of this file, not a guess.</b> <see cref="Entries"/> names the exact ids to
/// remove from the exact store files, and the rows marked <see cref="ImportManifestRow.AlreadyPresent"/>
/// are excluded from it — undoing an import must not delete entries an earlier import wrote. Nothing
/// in an undo touches a source file, because nothing in an import did.
/// </para>
/// </remarks>
/// <param name="Version">Schema version of this manifest, so a future reader can refuse an unknown one rather than half-read it.</param>
/// <param name="ImportedAtUtc">When the import ran.</param>
/// <param name="BatonRoot">The storage root the stores were written under — <c>BATON_HOME</c> can move it between runs.</param>
/// <param name="Entries">One row per file that became an entry.</param>
/// <param name="Unfiled">
/// Files in an imported root that could not be filed: no repository identity could be derived for
/// their root, so there is no store to put them in. <b>They are untouched, not lost</b> — the row
/// records the digest so a later run under a resolved identity can be seen to have imported the same
/// bytes.
/// </param>
/// <param name="Machinery">
/// Files recorded for provenance only and <b>never read as a memory source</b> — the Codex sqlite
/// stores, which the evening ruling of 2026-09-05 established are the pipeline that PRODUCES the
/// markdown memories rather than a store of them. Path, digest, mtime and size; not a byte of their
/// contents.
/// </param>
public sealed record ImportManifest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("importedAtUtc")] DateTime ImportedAtUtc,
    [property: JsonPropertyName("batonRoot")] string BatonRoot,
    [property: JsonPropertyName("entries")] IReadOnlyList<ImportManifestRow> Entries,
    [property: JsonPropertyName("unfiled")] IReadOnlyList<ImportSkippedRow> Unfiled,
    [property: JsonPropertyName("machinery")] IReadOnlyList<ImportSkippedRow> Machinery)
{
    /// <summary>The only version this build writes, and the only one <see cref="Read"/> accepts.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Indented and camel-cased, because a manifest is read by a person deciding whether to undo an
    /// import. Absent fields stay absent, matching every other Baton JSON view.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>The rows an undo removes: everything this import actually appended.</summary>
    public IEnumerable<ImportManifestRow> Appended => Entries.Where(e => !e.AlreadyPresent);

    /// <summary>
    /// Writes the manifest to <paramref name="manifestFilePath"/>, creating its directory. UTF-8
    /// without a byte-order mark, for the reason <c>MemoryStore</c>'s rewrite states.
    /// </summary>
    public void Write(string manifestFilePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestFilePath);

        var directory = Path.GetDirectoryName(manifestFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(manifestFilePath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, SerializerOptions)));
    }

    /// <summary>
    /// Reads a manifest back. Throws <see cref="BatonMemoryException"/> on a missing, malformed or
    /// unknown-version file rather than returning null — an undo that proceeded on a half-understood
    /// manifest is the one failure mode worse than an undo that refuses.
    /// </summary>
    public static ImportManifest Read(string manifestFilePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestFilePath);

        if (!File.Exists(manifestFilePath))
        {
            throw new BatonMemoryException($"No import manifest at '{manifestFilePath}'.");
        }

        ImportManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ImportManifest>(
                File.ReadAllBytes(manifestFilePath), SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new BatonMemoryException(
                $"The import manifest at '{manifestFilePath}' is not readable JSON: {ex.Message}", ex);
        }

        if (manifest is null)
        {
            throw new BatonMemoryException($"The import manifest at '{manifestFilePath}' is empty.");
        }

        if (manifest.Version != CurrentVersion)
        {
            throw new BatonMemoryException(
                $"The import manifest at '{manifestFilePath}' is version {manifest.Version}; this build " +
                $"understands version {CurrentVersion} only. Undo it with the build that wrote it.");
        }

        return manifest;
    }
}

/// <summary>
/// A domain-level failure in the memory subsystem — a manifest that cannot be trusted, an import that
/// cannot proceed. Typed rather than an <see cref="InvalidOperationException"/> so a caller can tell
/// this subsystem's refusals from a bug (CLAUDE.md, "Error handling rules").
/// </summary>
public sealed class BatonMemoryException : Exception
{
    public BatonMemoryException()
    {
    }

    public BatonMemoryException(string message)
        : base(message)
    {
    }

    public BatonMemoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
