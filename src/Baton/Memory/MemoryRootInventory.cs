using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Baton.Memory;

/// <summary>Which population a root came from — see <see cref="MemoryRootInventory"/> for the two.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemoryRootKind>))]
public enum MemoryRootKind
{
    /// <summary>A live vendor root: <c>{claude-home}/projects/&lt;encoded-path&gt;/memory</c>.</summary>
    [JsonStringEnumMemberName("live")] Live,

    /// <summary>An archived root: <c>{claude-home}/memory-archive/&lt;label&gt;/&lt;name&gt;</c>.</summary>
    [JsonStringEnumMemberName("archive")] Archive,
}

/// <summary>
/// One file inside a memory root, recorded by <b>measurement only</b>: path, size, modification time,
/// and a content digest. <see cref="Sha256"/> is computed by streaming the bytes, but the bytes
/// themselves are never retained, returned, or printed anywhere — <c>baton memory audit</c> reports
/// counts and paths, and the digest exists so two copies of one file can be recognised as one fact
/// without anything having to read either of them.
/// </summary>
/// <param name="Path">Absolute path of the file.</param>
/// <param name="RelativePath">Path relative to the root's own directory, forward-slash separated.</param>
/// <param name="SizeBytes">Length in bytes.</param>
/// <param name="ModifiedUtc">Last write time, in UTC.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the file's bytes.</param>
public sealed record MemoryFile(
    string Path,
    string RelativePath,
    long SizeBytes,
    DateTime ModifiedUtc,
    string Sha256);

/// <summary>
/// One memory root and every file under it.
/// </summary>
/// <param name="DirectoryPath">Absolute path of the root directory itself.</param>
/// <param name="DirectoryName">
/// The encoded directory name this root is keyed by — for a live root the <c>C--Users-…</c> project
/// directory name (the parent of <c>memory/</c>), for an archived root the archived directory's own
/// name. <see cref="MemoryRootPath"/> is what turns it back into a checkout path.
/// </param>
/// <param name="Kind">Live or archived.</param>
/// <param name="ArchiveLabel">The archive generation (<c>2026-09-03</c>), or null for a live root.</param>
/// <param name="SessionDirectoryPath">
/// Where this root's session <c>.jsonl</c> files live, when it has any — the live project directory.
/// Null for an archived root, which is a directory of memory files with no session index beside it,
/// and so has nothing but its own name to be decoded from.
/// </param>
/// <param name="Files">Every file under the root, recursively, ordered by relative path.</param>
public sealed record MemoryRoot(
    string DirectoryPath,
    string DirectoryName,
    MemoryRootKind Kind,
    string? ArchiveLabel,
    string? SessionDirectoryPath,
    IReadOnlyList<MemoryFile> Files);

/// <summary>
/// Enumerates the memory roots on a machine — #1852's population, and nothing but a population: this
/// type reads directory entries and file bytes (to digest them) and produces no judgement about any
/// root at all. <see cref="MemoryAuditReport"/> is where findings about the Claude roots are decided,
/// and <c>Baton.Cli.MemoryAuditCommand</c> is what maps one to a repository.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two populations, deliberately, and the second is not optional.</b> The live roots under
/// <c>projects/*/memory</c> are the obvious half; <c>memory-archive/&lt;label&gt;/&lt;name&gt;</c> holds
/// roots a prior migration moved aside with no manifest, and the live roots it drained are empty
/// <i>because</i> of it. An inventory that walked only the live half would report the truth about a
/// machine whose memory had already been moved and call it a machine with no memory.
/// </para>
/// <para>
/// <b>The non-Claude roots are a THIRD population, scanned separately and reported separately</b>
/// (#1852 phase A2, <see cref="ScanVendorRoots"/>). They are not folded into <see cref="Scan"/>'s
/// result, because everything downstream of it maps a root to one repository: a Claude root encodes a
/// checkout path in its own name, while <c>~/.codex/memories</c> and <c>~/.gemini/antigravity/brain</c>
/// are per-machine and encode nothing. Passed through the same pipeline every one of them would
/// report <c>no-provenance</c> — a finding that means "a root that should map to a repository does
/// not", which for a per-machine root is definitionally true and therefore says nothing.
/// </para>
/// <para>
/// <b>Both homes are parameters, not <c>BatonPaths</c> lookups.</b> A vendor's own configuration
/// directory is deliberately not routed through <c>BatonPaths</c> (that type's own remarks state why),
/// so the defaults are resolved here and every test supplies a fixture root instead.
/// </para>
/// </remarks>
public static class MemoryRootInventory
{
    /// <summary>Directory under the Claude home holding one directory per project, each with an optional <c>memory/</c>.</summary>
    public const string ProjectsDirectoryName = "projects";

    /// <summary>The <c>memory</c> subdirectory of a project directory — the live root itself.</summary>
    public const string MemoryDirectoryName = "memory";

    /// <summary>Directory under the Claude home holding one directory per archive generation.</summary>
    public const string ArchiveDirectoryName = "memory-archive";

    /// <summary>
    /// <c>{UserProfile}/.claude</c> — where Claude Code keeps its projects and memory. Resolved fresh
    /// rather than captured, and never written to by anything in this namespace.
    /// </summary>
    public static string DefaultClaudeHome =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>
    /// The user's home directory — what <see cref="VendorMemoryRootTable.Families"/>' relative paths
    /// hang off. Resolved fresh rather than captured, and never written to by anything here.
    /// </summary>
    public static string DefaultUserHome =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Every root under <paramref name="claudeHomePath"/>, live population first, each ordered by
    /// directory path. A root with no files is still a root: an emptied memory directory is exactly
    /// what a prior migration leaves behind, and omitting it would hide the evidence of one.
    /// </summary>
    /// <remarks>
    /// A missing Claude home, or a missing half of the population, is an empty result rather than a
    /// throw — a machine that has never run Claude Code has no memory roots, which is an answer.
    /// </remarks>
    public static IReadOnlyList<MemoryRoot> Scan(string claudeHomePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(claudeHomePath);

        var roots = new List<MemoryRoot>();

        var projectsPath = Path.Combine(claudeHomePath, ProjectsDirectoryName);
        if (Directory.Exists(projectsPath))
        {
            foreach (var projectDirectory in Directory.GetDirectories(projectsPath).OrderBy(p => p, StringComparer.Ordinal))
            {
                var memoryDirectory = Path.Combine(projectDirectory, MemoryDirectoryName);
                if (!Directory.Exists(memoryDirectory))
                {
                    continue;
                }

                roots.Add(new MemoryRoot(
                    memoryDirectory,
                    Path.GetFileName(projectDirectory),
                    MemoryRootKind.Live,
                    ArchiveLabel: null,
                    SessionDirectoryPath: projectDirectory,
                    ReadFiles(memoryDirectory)));
            }
        }

        var archivePath = Path.Combine(claudeHomePath, ArchiveDirectoryName);
        if (Directory.Exists(archivePath))
        {
            foreach (var generation in Directory.GetDirectories(archivePath).OrderBy(p => p, StringComparer.Ordinal))
            {
                foreach (var archivedRoot in Directory.GetDirectories(generation).OrderBy(p => p, StringComparer.Ordinal))
                {
                    roots.Add(new MemoryRoot(
                        archivedRoot,
                        Path.GetFileName(archivedRoot),
                        MemoryRootKind.Archive,
                        ArchiveLabel: Path.GetFileName(generation),
                        SessionDirectoryPath: null,
                        ReadFiles(archivedRoot)));
                }
            }
        }

        return roots;
    }

    /// <summary>
    /// Every non-Claude memory root under <paramref name="userHomePath"/>, one row per
    /// <see cref="VendorMemoryRootTable.Families"/> entry, in table order. #1852 phase A2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same phase-A contract, unchanged: path, size, mtime, sha256.</b> Nothing here opens a
    /// sqlite database, parses a protobuf, or reads a line of markdown — a <c>.sqlite</c> file is
    /// digested exactly as a <c>.md</c> file is, as bytes. This method therefore knows nothing about
    /// any format it inventories, cannot judge one, and does not try; <c>spec/baton.md</c> §12 says
    /// where the formats were judged instead.
    /// </para>
    /// <para>
    /// <b>A row is produced for every family, including absent ones.</b> A family missing from the
    /// report and a family present-but-empty would otherwise be indistinguishable to a reader, and
    /// <see cref="VendorMemoryPresence"/>'s own remarks say why that distinction carries the ruling.
    /// </para>
    /// <para>
    /// Sqlite sidecars (<c>-wal</c>, <c>-shm</c>) are outside every selector, so a digest here is of
    /// the main database file alone and does not cover uncheckpointed state sitting in a write-ahead
    /// log. Said rather than left to inference: the digest is stable enough to recognise a copy, and
    /// is not a statement about the store's full contents.
    /// </para>
    /// </remarks>
    /// <param name="userHomePath">Where the third-party vendor homes live.</param>
    /// <param name="batonRootPath">
    /// Baton's own root — <c>BatonPaths.Root</c> in production, which <c>BATON_HOME</c> can move.
    /// A Baton-managed family hangs off this rather than off <paramref name="userHomePath"/>; see
    /// <see cref="VendorMemoryFamily.RelativeDirectory"/> for what resolving it the other way costs.
    /// </param>
    public static IReadOnlyList<VendorMemoryRoot> ScanVendorRoots(string userHomePath, string batonRootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(userHomePath);
        ArgumentException.ThrowIfNullOrEmpty(batonRootPath);

        var roots = new List<VendorMemoryRoot>(VendorMemoryRootTable.Families.Count);

        foreach (var family in VendorMemoryRootTable.Families)
        {
            var basePath = family.SourceScope == VendorMemoryScope.BatonManaged ? batonRootPath : userHomePath;
            var directory = Path.Combine(
                basePath, family.RelativeDirectory.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(directory))
            {
                roots.Add(new VendorMemoryRoot(
                    family.Family, family.SourceVendor, family.SourceScope, directory,
                    VendorMemoryPresence.Absent, FileCount: 0, TotalBytes: 0, NewestModifiedUtc: null,
                    Files: [], family.Inventoried));
                continue;
            }

            var selected = SelectFiles(directory, family.FilePattern, family.Recursive);
            var files = family.Inventoried ? ReadFiles(directory, selected) : [];

            roots.Add(new VendorMemoryRoot(
                family.Family,
                family.SourceVendor,
                family.SourceScope,
                directory,
                selected.Count == 0 ? VendorMemoryPresence.Empty : VendorMemoryPresence.Populated,
                selected.Count,
                selected.Sum(f => f.Length),
                selected.Count == 0 ? null : selected.Max(f => f.LastWriteTimeUtc),
                files,
                family.Inventoried));
        }

        return roots;
    }

    /// <summary>
    /// The files a family's selector matches, as <see cref="FileInfo"/> — size and mtime only, so a
    /// family that is counted rather than inventoried never has a byte of its files read.
    /// </summary>
    /// <remarks>
    /// A file that vanishes between the listing and the stat is skipped, for the reason
    /// <see cref="ReadFiles(string, IReadOnlyList{FileInfo})"/> gives. An unreadable directory yields
    /// nothing rather than throwing: these are third-party trees a live vendor process is writing.
    /// </remarks>
    private static IReadOnlyList<FileInfo> SelectFiles(string directoryPath, string pattern, bool recursive)
    {
        var depth = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var selected = new List<FileInfo>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(directoryPath, pattern, depth))
            {
                try
                {
                    var info = new FileInfo(path);
                    _ = info.Length;
                    selected.Add(info);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Gone or locked between the listing and the stat. Nothing true to record.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The whole walk failed. An empty selection is the honest answer; the presence of the
            // directory itself is already recorded by the caller.
        }

        return selected;
    }

    /// <summary>
    /// Every file under <paramref name="rootDirectoryPath"/>, recursively. A file that vanishes or
    /// cannot be opened between the directory listing and the digest is skipped rather than throwing:
    /// an inventory of a live directory races whatever else is writing there, and losing one row is a
    /// smaller loss than losing the whole report.
    /// </summary>
    private static IReadOnlyList<MemoryFile> ReadFiles(string rootDirectoryPath)
    {
        var files = new List<MemoryFile>();

        foreach (var path in Directory.EnumerateFiles(rootDirectoryPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(path);
                files.Add(new MemoryFile(
                    path,
                    Path.GetRelativePath(rootDirectoryPath, path).Replace('\\', '/'),
                    info.Length,
                    info.LastWriteTimeUtc,
                    HashFile(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file went away or is locked. Nothing to report about it that would be true.
            }
        }

        return files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The same rows as <see cref="ReadFiles(string)"/>, but over an already-selected set rather than
    /// a whole directory walk — the bound <see cref="VendorMemoryFamily"/> exists to impose.
    /// </summary>
    private static IReadOnlyList<MemoryFile> ReadFiles(
        string rootDirectoryPath, IReadOnlyList<FileInfo> selected)
    {
        var files = new List<MemoryFile>(selected.Count);

        foreach (var info in selected)
        {
            try
            {
                files.Add(new MemoryFile(
                    info.FullName,
                    Path.GetRelativePath(rootDirectoryPath, info.FullName).Replace('\\', '/'),
                    info.Length,
                    info.LastWriteTimeUtc,
                    HashFile(info.FullName)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file went away or is locked. Nothing to report about it that would be true.
            }
        }

        return files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Lower-case hex SHA-256 of a file, streamed. The only place in this namespace that opens a
    /// memory file's bytes, and the bytes leave this method only as a digest.
    /// </summary>
    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
