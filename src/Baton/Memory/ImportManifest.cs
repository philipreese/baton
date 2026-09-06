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
/// One supersession link the import recorded — the same reversal accounting
/// <see cref="ImportManifestRow"/> carries, for the second file a store is made of.
/// </summary>
/// <param name="LinkId">The <see cref="MemorySupersessionLink.Id"/> — the ordered pair of entry ids.</param>
/// <param name="Repository">The subject both linked entries are filed under.</param>
/// <param name="LinksFilePath">The links file the row was appended to.</param>
/// <param name="AlreadyPresent">
/// <see langword="true"/> when the link was already recorded, so this import appended nothing for it.
/// <b>An undo does not remove one of these</b>, for the reason
/// <see cref="ImportManifestRow.AlreadyPresent"/> gives: an earlier run recorded it, and a link that
/// still describes two entries that are still present is still true.
/// </param>
public sealed record ImportLinkRow(
    [property: JsonPropertyName("linkId")] string LinkId,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("linksFile")] string LinksFilePath,
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
/// remove from the exact store files and <see cref="Links"/> does the same for the supersession rows,
/// and the rows marked <see cref="ImportManifestRow.AlreadyPresent"/> are excluded from both — undoing
/// an import must not delete entries an earlier import wrote. Nothing in an undo touches a source
/// file, because nothing in an import did.
/// </para>
/// <para>
/// <b><see cref="BatonRoot"/> is read, not merely recorded.</b> Every path in here is absolute under
/// the root the import ran against, so replaying a manifest under a different <c>BATON_HOME</c> would
/// find no store file, remove nothing, and — before this was checked — report success.
/// <c>MemoryImportCommand</c>'s undo refuses that case outright.
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
/// <param name="Links">
/// One row per Q2 supersession link this import computed, so an undo reverses <b>both</b> files a
/// store is made of. Absent (null) on a manifest written before the field existed; see
/// <see cref="CurrentVersion"/> for why that did not need a version bump.
/// </param>
public sealed record ImportManifest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("importedAtUtc")] DateTime ImportedAtUtc,
    [property: JsonPropertyName("batonRoot")] string BatonRoot,
    [property: JsonPropertyName("entries")] IReadOnlyList<ImportManifestRow> Entries,
    [property: JsonPropertyName("unfiled")] IReadOnlyList<ImportSkippedRow> Unfiled,
    [property: JsonPropertyName("machinery")] IReadOnlyList<ImportSkippedRow> Machinery,
    [property: JsonPropertyName("links")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ImportLinkRow>? Links = null)
{
    /// <summary>
    /// The only version this build writes, and the only one <see cref="Read"/> accepts.
    /// <para>
    /// <b>Not bumped when <see cref="Links"/> was added</b> (#1940 review round): the verb has never
    /// shipped, so no version-1 manifest written by any released build exists to be misread, and the
    /// field is optional in both directions — an older manifest reads back with no links and undoes its
    /// entries exactly as before. The next change to this schema after the verb ships is a bump,
    /// because from then on a reader could genuinely meet a manifest an older build wrote.
    /// </para>
    /// </summary>
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

    /// <summary>The link rows an undo removes. Empty on a manifest written before <see cref="Links"/> existed.</summary>
    public IEnumerable<ImportLinkRow> AppendedLinks => (Links ?? []).Where(l => !l.AlreadyPresent);

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
/// <remarks>
/// <b>A <see cref="BatonFlowException"/>, so it surfaces as a message rather than a stack trace.</b>
/// <c>Program</c>'s typed-exception boundary catches that base and turns it into a clean CLI failure
/// with exit code 1; a subsystem exception deriving straight from <see cref="Exception"/> would fall
/// past it, and "the manifest you named does not exist" would reach an operator as an unhandled crash
/// — the same success-shaped-or-crashing undo the refusals in <c>MemoryImportCommand</c> exist to
/// remove.
/// </remarks>
public sealed class BatonMemoryException : BatonFlowException
{
    public BatonMemoryException(string message)
        : base(message)
    {
    }

    public BatonMemoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
