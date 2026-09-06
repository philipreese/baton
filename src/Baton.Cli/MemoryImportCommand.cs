using System.Globalization;
using Baton.Accounting;
using Baton.Memory;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// <c>baton memory import [--dry-run] [--root &lt;dir&gt;]… | --undo &lt;manifest&gt;</c> (#1852 phase B):
/// copy every discovered memory file into the canonical per-repository store, keyed by repository
/// identity, leaving every source file exactly as it was and emitting a manifest that replays to a
/// full undo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non-destructive by construction, not by care.</b> The only file-opening this command does on a
/// source is a read: <see cref="MemoryRootInventory"/> streams a digest, and
/// <see cref="ReadSourceFiles"/> reads text through <see cref="FileAccess.Read"/>. There is no code
/// path here that opens a source for writing, moves one, or deletes one — the destructive verbs do
/// not exist to be reached by a bug. Everything this command writes lives under
/// <see cref="BatonPaths.Root"/>.
/// </para>
/// <para>
/// <b>Discovery is not re-implemented.</b> The population is exactly what <c>baton memory audit</c>
/// reports — <see cref="MemoryRootInventory.Scan"/> for the Claude roots and
/// <see cref="MemoryRootInventory.ScanVendorRoots"/> for the non-Claude ones — and
/// <see cref="MemoryImportOptions.Roots"/> filters that set rather than adding to it. A second
/// enumeration would be a second definition of "a memory root", and the audit's report would stop
/// being a preview of what the import will do.
/// </para>
/// <para>
/// <b>This command resolves and writes; it decides nothing.</b> Which entries a plan contains, what
/// kind each is, and which supersede which are all <see cref="MemoryImportPlan.Build"/>'s, which is
/// pure. What lives here is the part that cannot be: the filesystem, the git probe
/// (<see cref="RepositoryIdentityResolver"/>, up here because the engine stays git-agnostic), and the
/// append.
/// </para>
/// </remarks>
public static class MemoryImportCommand
{
    /// <param name="claudeHomeOverride">
    /// Test seam — production callers always use <see cref="MemoryRootInventory.DefaultClaudeHome"/>.
    /// A vendor's own config directory is deliberately not routed through <see cref="BatonPaths"/>.
    /// </param>
    /// <param name="userHomeOverride">
    /// Test seam for the third-party vendor roots, separate from <paramref name="claudeHomeOverride"/>
    /// for the reason <see cref="MemoryAuditCommand"/>'s own parameter states.
    /// </param>
    /// <remarks>
    /// There is deliberately no override for Baton's own root: both the store this writes and the
    /// Baton-managed vendor family hang off <see cref="BatonPaths.Root"/>, and a test isolates them
    /// together with <c>BatonEnvironmentSnapshot.BeginScope</c>. A third seam here could point the two
    /// at different roots, which no production configuration can do.
    /// </remarks>
    public static async Task<int> ExecuteAsync(
        MemoryImportOptions options,
        TextWriter output,
        string? claudeHomeOverride = null,
        CancellationToken cancellationToken = default,
        string? userHomeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        if (options.Help)
        {
            output.WriteLine(MemoryImportOptionsParser.Usage);
            foreach (var line in MemoryImportOptionsParser.HelpLines)
            {
                output.WriteLine(line);
            }

            return 0;
        }

        return options.UndoManifestPath is { Length: > 0 } manifestPath
            ? await UndoAsync(manifestPath, output, cancellationToken).ConfigureAwait(false)
            : await ImportAsync(options, output, claudeHomeOverride, userHomeOverride, cancellationToken)
                .ConfigureAwait(false);
    }

    private static async Task<int> ImportAsync(
        MemoryImportOptions options,
        TextWriter output,
        string? claudeHomeOverride,
        string? userHomeOverride,
        CancellationToken cancellationToken)
    {
        var claudeHome = claudeHomeOverride ?? MemoryRootInventory.DefaultClaudeHome;
        var userHome = userHomeOverride ?? MemoryRootInventory.DefaultUserHome;
        var batonRoot = BatonPaths.Root;

        var aliases = await ResolveAliasesAsync(options, cancellationToken).ConfigureAwait(false);

        var sources = new List<MemoryImportSource>();
        var machineryRoots = new List<MachineryRoot>();

        foreach (var root in MemoryRootInventory.Scan(claudeHome, cancellationToken))
        {
            sources.Add(await ResolveClaudeRootAsync(root, aliases, cancellationToken).ConfigureAwait(false));
        }

        foreach (var root in MemoryRootInventory.ScanVendorRoots(userHome, batonRoot, limits: null, cancellationToken))
        {
            if (root.Family == VendorMemoryRootTable.CodexMarkdownFamily)
            {
                sources.Add(ResolveVendorRoot(root, aliases));
            }
            else if (root.Family == VendorMemoryRootTable.CodexSqliteFamily)
            {
                // Provenance only. The evening ruling of 2026-09-05 and docs/vendor-doc-audit.md
                // §"#1852 phase A2" are why: this store PRODUCES the markdown above rather than
                // holding memories, so reading it as a memory source would import the machinery's
                // intermediate state as though it were durable fact. Its bytes are digested (by the
                // inventory) and never opened here.
                machineryRoots.Add(new MachineryRoot(
                    root.DirectoryPath,
                    root.Files.Select(f => new ImportSkippedRow(
                        f.Path, f.Sha256, f.ModifiedUtc, f.SizeBytes,
                        "Codex sqlite store: the pipeline that produces the markdown memories, not a memory " +
                        "source. Recorded for provenance; nothing was read out of it.")).ToList()));
            }
        }

        var selection = ApplyRootFilter(sources, machineryRoots, options.Roots);
        sources = selection.Sources;

        // Filtered by the SAME selection, so a manifest accounts for the roots this run looked at and
        // no others: `--root <one Claude root>` used to record every Codex sqlite file on the machine.
        var machinery = selection.MachineryRoots.SelectMany(m => m.Rows).ToList();

        var withFiles = sources
            .Select(source => source with { Files = ReadSourceFiles(source) })
            .ToList();

        var importedAtUtc = DateTime.UtcNow;
        var plan = MemoryImportPlan.Build(withFiles, importedAtUtc);

        var sizeByPath = withFiles
            .SelectMany(s => s.Files)
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().SizeBytes, StringComparer.OrdinalIgnoreCase);

        var rows = new List<ImportManifestRow>();
        var linkRows = new List<ImportLinkRow>();
        foreach (var group in plan.Entries.GroupBy(e => e.Repository, StringComparer.OrdinalIgnoreCase))
        {
            var slug = RepositoryIdentity.FileSlugFor(group.Key);
            var entriesFile = BatonPaths.MemoryEntriesFile(slug);
            var linksFile = BatonPaths.MemoryLinksFile(slug);
            var entries = group.ToList();

            // Read first so the manifest can say which rows THIS run appended: an undo must not remove
            // an entry an earlier import wrote. The append itself re-checks under its own lock, so this
            // read is a report input and never the thing that keeps the file free of duplicates.
            var stored = await MemoryStore.ReadAllAsync(entriesFile, cancellationToken).ConfigureAwait(false);
            var existing = stored.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

            // The link population is the STORE plus this run, never this run alone — MemoryImportPlan
            // .LinkSupersession's own remarks carry why, and it is the whole of the incremental-import
            // defect: importing the live roots and the archive in separate runs used to land no link at
            // all, because each run could only see its own half of every pair.
            var links = MemoryImportPlan.LinkSupersession(
                [.. stored, .. entries.Where(e => !existing.Contains(e.Id))], importedAtUtc);
            var existingLinks = (await MemoryStore.ReadLinksAsync(linksFile, cancellationToken).ConfigureAwait(false))
                .Select(l => l.Id)
                .ToHashSet(StringComparer.Ordinal);

            if (!options.DryRun)
            {
                await MemoryStore.AppendAsync(entries, entriesFile, cancellationToken).ConfigureAwait(false);
                await MemoryStore.AppendLinksAsync(links, linksFile, cancellationToken).ConfigureAwait(false);
            }

            rows.AddRange(entries.Select(e => new ImportManifestRow(
                e.SourcePath, e.Sha256, e.SourceMtimeUtc,
                sizeByPath.TryGetValue(e.SourcePath, out var size) ? size : 0,
                e.SourceVendor, e.SourceScope, e.Id, e.Repository, entriesFile,
                AlreadyPresent: existing.Contains(e.Id))));

            linkRows.AddRange(links.Select(l => new ImportLinkRow(
                l.Id, l.Repository, linksFile, AlreadyPresent: existingLinks.Contains(l.Id))));
        }

        var manifest = new ImportManifest(
            ImportManifest.CurrentVersion,
            DateTime.UtcNow,
            batonRoot,
            rows.OrderBy(r => r.EntriesFilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.SourcePath, StringComparer.OrdinalIgnoreCase).ToList(),
            plan.Unfiled,
            machinery,
            linkRows.OrderBy(l => l.LinksFilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.LinkId, StringComparer.Ordinal).ToList(),
            plan.ProjectionsSkipped);

        string? manifestPath = null;
        if (!options.DryRun)
        {
            manifestPath = BatonPaths.MemoryImportManifestFile(
                "import-" + manifest.ImportedAtUtc.ToString("yyyyMMdd'T'HHmmss'.'fff'Z'", CultureInfo.InvariantCulture));
            manifest.Write(manifestPath);
        }

        WriteReport(output, options, manifest, manifestPath);
        return 0;
    }

    /// <summary>
    /// The alias store as this run sees it: what is already recorded, plus anything <c>--assert</c>
    /// added. The new rows are persisted first (so a later run reuses them without the flag) and
    /// returned either way — under <c>--dry-run</c> they apply to the computed plan and are not
    /// written, which is what makes a dry run a preview of the real thing rather than a preview of a
    /// different one.
    /// </summary>
    private static async Task<IReadOnlyList<MemoryAliasEntry>> ResolveAliasesAsync(
        MemoryImportOptions options, CancellationToken cancellationToken)
    {
        var recorded = await MemoryAliasStore
            .ReadAllAsync(BatonPaths.MemoryAliasFile, cancellationToken).ConfigureAwait(false);

        if (options.Assertions.Count == 0)
        {
            return recorded;
        }

        var assertedBy = options.AssertedBy is { Length: > 0 } who ? who : Environment.UserName;
        var asserted = options.Assertions
            .Select(a => new MemoryAliasEntry(
                RequirePathKey(a.Path, $"--assert {a.Path}={a.Repository}"),
                a.Repository,
                assertedBy,
                DateTime.UtcNow))
            .ToList();

        if (!options.DryRun)
        {
            await MemoryAliasStore
                .AppendAsync(asserted, BatonPaths.MemoryAliasFile, cancellationToken).ConfigureAwait(false);
        }

        return [.. recorded, .. asserted];
    }

    /// <summary>
    /// One Claude root's subject: the git probe at its resolved checkout, then an operator assertion
    /// for a path git cannot answer for, then nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The path resolution is <see cref="ClaudeMemoryRootResolver"/>'s — literally the same method
    /// <see cref="MemoryAuditCommand"/> calls, which is what makes the help text's preview claim a
    /// property of the code rather than a sentence in two doc comments. What is added
    /// here is the alias fallback, which fires only where the probe produced nothing:
    /// <see cref="MemoryAliasStore"/>'s own remarks state why an assertion may never displace a
    /// measurement, and why this is not the mechanism for the subject-versus-origin question.
    /// </para>
    /// <para>
    /// <b>The fallback tries two keys — the checkout, then the root's own directory.</b> The second is
    /// what makes an archived root reachable at all: it has no session transcript and its name flattens
    /// the memory directory rather than a checkout (<c>c--Users-…-repos-baton-memory</c>), so the
    /// resolution carries no checkout path for an alias to match on. <see cref="MemoryAliasStore"/>'s
    /// own remarks state why the root directory is a legitimate key rather than a second-best one.
    /// </para>
    /// </remarks>
    private static async Task<MemoryImportSource> ResolveClaudeRootAsync(
        MemoryRoot root, IReadOnlyList<MemoryAliasEntry> aliases, CancellationToken cancellationToken)
    {
        var resolved = await ClaudeMemoryRootResolver.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
        var repository = resolved.RepositoryValue;

        var asserted = repository is null
            ? MemoryAliasStore.Resolve(aliases, resolved.Path.CheckoutPath)
                ?? MemoryAliasStore.Resolve(aliases, root.DirectoryPath)
            : null;

        return new MemoryImportSource(
            root.DirectoryPath,
            MemoryRootInventory.ClaudeVendor,
            VendorMemoryScope.Vendor,
            root.Kind == MemoryRootKind.Archive,
            repository ?? asserted,
            UnfiledReason: DescribeUnresolvedClaudeRoot(resolved.Path, resolved.CheckoutExists),
            root.Files.Select(f => new MemoryImportFile(f.Path, Path.GetFileName(f.Path), string.Empty, f.Sha256, f.ModifiedUtc, f.SizeBytes)).ToList());
    }

    /// <summary>
    /// Why a Claude root produced no subject, in the operator's terms — and, in every branch, the
    /// exact flag that resolves it. A reason with no remedy reads as a refusal rather than as the
    /// question it is.
    /// </summary>
    private static string DescribeUnresolvedClaudeRoot(MemoryRootPathResolution resolution, bool checkoutExists) =>
        resolution.CheckoutPath is not { Length: > 0 } checkoutPath
            ? $"the root's name decodes to no single checkout path ({MemoryJsonNames.Of(resolution.Source)}), " +
              "so no repository could be probed. Assert one with " +
              "'--assert <this root>=<repository>' to import it."
            : checkoutExists
                ? $"'{checkoutPath}' exists but yields no repository identity, so there is no store to file " +
                  "this under. Assert one with '--assert <this root>=<repository>' to import it."
                : $"the checkout this memory belongs to is gone ('{checkoutPath}'), so nothing can be probed. " +
                  "Assert its repository with '--assert <this root>=<repository>' to import it.";

    /// <summary>
    /// One non-Claude root's subject. These are <b>per-machine</b> roots — they encode no checkout, so
    /// there is nothing to probe and nothing to decode; an operator assertion keyed on the root's own
    /// directory is the only thing that can file them, and its absence leaves them unfiled rather than
    /// filed under a guess (the working directory's repository being the obvious wrong guess: it would
    /// key one machine's shared memories to whichever checkout the import happened to run in).
    /// </summary>
    private static MemoryImportSource ResolveVendorRoot(
        VendorMemoryRoot root, IReadOnlyList<MemoryAliasEntry> aliases) =>
        new(root.DirectoryPath,
            root.SourceVendor,
            root.SourceScope,
            Archived: false,
            MemoryAliasStore.Resolve(aliases, root.DirectoryPath),
            UnfiledReason:
                $"'{root.DirectoryPath}' is a per-machine root: it encodes no checkout, so no repository " +
                "can be derived from it. Assert one with '--assert <this root>=<repository>' to import it.",
            root.Files.Select(f => new MemoryImportFile(
                f.Path, Path.GetFileName(f.Path), string.Empty, f.Sha256, f.ModifiedUtc, f.SizeBytes)).ToList());

    /// <summary>
    /// One machinery root's rows, kept with the directory they came from so <c>--root</c> can narrow
    /// them the same way it narrows the importable sources.
    /// </summary>
    private sealed record MachineryRoot(string RootDirectoryPath, IReadOnlyList<ImportSkippedRow> Rows);

    /// <summary>What survived <see cref="ApplyRootFilter"/>: both populations, narrowed together.</summary>
    private sealed record RootSelection(List<MemoryImportSource> Sources, List<MachineryRoot> MachineryRoots);

    /// <summary>
    /// Both populations narrowed to <paramref name="roots"/>, or all of them when none was named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Machinery is narrowed too.</b> Its rows are collected during discovery, before this runs, so
    /// leaving them unfiltered made <c>--root &lt;one Claude root&gt;</c> write every Codex sqlite file
    /// on the machine into that run's manifest — files the run was not asked to look at, accounted for
    /// as though it had.
    /// </para>
    /// <para>
    /// <b>A named path that matches neither population throws rather than being ignored</b>: the
    /// operator asked for something that is not there, and an import that silently did less than it was
    /// asked would look identical to one that found nothing to do. The refusal names the two families
    /// that <c>baton memory audit</c> reports and this verb cannot select — the audit's population is
    /// deliberately the larger of the two, and pointing an operator at a listing that includes rows
    /// this option rejects is what made the old message a dead end.
    /// </para>
    /// </remarks>
    private static RootSelection ApplyRootFilter(
        List<MemoryImportSource> sources, List<MachineryRoot> machineryRoots, IReadOnlyList<string> roots)
    {
        if (roots.Count == 0)
        {
            return new RootSelection(sources, machineryRoots);
        }

        var selectedSources = new List<MemoryImportSource>();
        var selectedMachinery = new List<MachineryRoot>();
        foreach (var requested in roots)
        {
            var key = RequirePathKey(requested, $"--root {requested}");

            var source = sources.FirstOrDefault(
                s => BatonPaths.RecordKeyComparer.Equals(BatonPaths.RecordKey(s.RootDirectoryPath), key));
            if (source is not null)
            {
                if (!selectedSources.Contains(source))
                {
                    selectedSources.Add(source);
                }

                continue;
            }

            var machinery = machineryRoots.FirstOrDefault(
                m => BatonPaths.RecordKeyComparer.Equals(BatonPaths.RecordKey(m.RootDirectoryPath), key));
            if (machinery is null)
            {
                throw new CliArgumentException(
                    $"'--root {requested}' matches no memory root this verb can import. This option " +
                    "selects from what discovery found; it cannot add a directory. Run 'baton memory " +
                    "audit' to see the roots that exist -- and note that its listing is WIDER than what " +
                    "can be named here: an Antigravity root is audited but never imported and cannot be " +
                    "selected at all.");
            }

            if (!selectedMachinery.Contains(machinery))
            {
                selectedMachinery.Add(machinery);
            }
        }

        return new RootSelection(selectedSources, selectedMachinery);
    }

    /// <summary>
    /// <c>BatonPaths.RecordKey</c> for an operator-supplied path, with an unusable one surfaced as this
    /// verb's own refusal.
    /// </summary>
    /// <remarks>
    /// <c>RecordKey</c> is <c>Path.GetFullPath</c> underneath, which throws a bare
    /// <see cref="ArgumentException"/> on a path holding an invalid character.
    /// <c>MemoryImportOptionsParser</c> states that every malformed invocation of this verb produces a
    /// <see cref="CliArgumentException"/>, and <c>MemoryAliasStore.Resolve</c> guards the identical call
    /// for the identical reason; without this, <c>--assert</c> and <c>--root</c> were the two ways to
    /// get a stack trace out of a typo.
    /// </remarks>
    private static string RequirePathKey(string path, string option)
    {
        try
        {
            return BatonPaths.RecordKey(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CliArgumentException(
                $"'{option}' does not name a usable path: {ex.Message} {MemoryImportOptionsParser.Usage}",
                "check the path for invalid characters.");
        }
    }

    /// <summary>
    /// The same file rows with their text read in, and <b>re-digested, re-sized and re-stamped from the
    /// very bytes that text was decoded from</b>. <b>The one place a memory's contents are read</b>, and
    /// they are read for copying rather than for meaning — opened with <see cref="FileAccess.Read"/>,
    /// and never written back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One read, so text, digest, size and mtime describe the same file.</b> The inventory walk
    /// digests and stats every file it finds, minutes or milliseconds before this runs; taking any of
    /// the four from there and the rest from here means a file edited in between is stored with text
    /// that does not match its recorded <see cref="MemoryEntry.Sha256"/>, under an id derived from a
    /// version of it nobody kept, or with a <see cref="MemoryEntry.SourceMtimeUtc"/> that belongs to a
    /// different version than the digest beside it (#1948). Re-hashing the bytes in hand costs one pass
    /// over a file already in memory; the mtime comes from <b>the same open handle</b> those bytes came
    /// from, read after them, rather than from a second <c>stat</c> a rename-over could answer for a
    /// different file. The inventory's digest and mtime still stand for the machinery rows, which are
    /// never opened.
    /// </para>
    /// <para>
    /// <b>What that does not claim.</b> The handle is opened <see cref="FileShare.ReadWrite"/>, so a
    /// writer active <i>during</i> the read is not excluded and the four values are not an atomic
    /// snapshot: the recorded mtime is taken after the last byte, which makes it an upper bound on what
    /// was hashed rather than a guarantee that nothing moved underneath. What is closed is the
    /// inventory-to-read window, which is the one measured in minutes.
    /// </para>
    /// <para>
    /// <b>The text is a UTF-8 decode, not the bytes</b>, and <see cref="MemoryEntry"/>'s own doc says so
    /// rather than claiming otherwise: BOM detection is left ON (as it was) because
    /// <see cref="MemoryKindInference"/> reads front-matter anchored at the start of the text, and a
    /// U+FEFF preserved there would silently demote a declared kind to an inferred one. The digest
    /// beside it is over the bytes and is the authority on what the file held.
    /// </para>
    /// <para>
    /// A file that vanished or became unreadable between the inventory and this read is dropped rather
    /// than throwing, matching the inventory's own posture: one lost row in a walk that otherwise
    /// finished. It cannot silently become an empty entry — a dropped file contributes neither an
    /// entry nor a manifest row, so the import's own accounting shows it was not carried.
    /// </para>
    /// <para>
    /// <c>internal</c> rather than <c>private</c> only as a test seam (Baton.Cli.Tests, via
    /// <c>InternalsVisibleTo</c>): the walk and this read happen back to back inside
    /// <see cref="ImportAsync"/> with nothing between them to interpose on, so the arm that proves a
    /// stale inventory row cannot survive the read has to hand one in directly.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<MemoryImportFile> ReadSourceFiles(MemoryImportSource source)
    {
        var files = new List<MemoryImportFile>(source.Files.Count);
        foreach (var file in source.Files)
        {
            try
            {
                byte[] bytes;
                DateTime modifiedUtc;
                using (var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    bytes = buffer.ToArray();
                    modifiedUtc = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
                }

                using var reader = new StreamReader(
                    new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                files.Add(file with
                {
                    Text = reader.ReadToEnd(),
                    Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                    SizeBytes = bytes.Length,
                    ModifiedUtc = modifiedUtc,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone or locked since the inventory digested it. Nothing true to import.
            }
        }

        return files;
    }

    /// <summary>
    /// Replays <paramref name="manifestPath"/> backwards: removes exactly the entries that run
    /// appended, from exactly the store files it wrote them to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It touches no source file, because the import touched none either.</b> "Undo" here means the
    /// canonical store returns to what it held before the import — there is nothing to restore on the
    /// vendor's side, which is the whole point of a non-destructive import and is stated in the output
    /// so an operator does not go looking for a restore that would have no work to do.
    /// </para>
    /// <para>
    /// <b>It exits non-zero unless it removed exactly what the manifest says the import appended.</b>
    /// <see cref="MemoryStore.RemoveAsync"/> answers 0 rather than throwing for a store file that is
    /// not there, which is right for that method (its own remarks give the reason) and wrong for the
    /// report built on top: "Removed 0 canonical entries" printed with exit 0 told an operator the undo
    /// had run when nothing had been reversed at all. The count is compared per store file, so the
    /// report names which one came up short rather than only that a total did.
    /// </para>
    /// <para>
    /// <b>And it refuses a manifest written under a different storage root outright.</b> Every path in
    /// a manifest is absolute under <see cref="ImportManifest.BatonRoot"/>, so replaying one after
    /// <c>BATON_HOME</c> moved would look for store files that do not exist here and — even with the
    /// count check above — could only report a failure it cannot explain. The root is the explanation,
    /// and it was being written on every manifest and read by nothing.
    /// </para>
    /// </remarks>
    private static async Task<int> UndoAsync(
        string manifestPath, TextWriter output, CancellationToken cancellationToken)
    {
        var manifest = ImportManifest.Read(manifestPath);

        output.WriteLine($"baton memory import --undo {manifestPath}");

        var currentRoot = BatonPaths.Root;
        if (!IsSameRoot(manifest.BatonRoot, currentRoot))
        {
            output.WriteLine(
                $"REFUSED -- this manifest was written against the storage root '{manifest.BatonRoot}', " +
                $"and this process is using '{currentRoot}'. Every store path in the manifest is " +
                "absolute under the first one, so replaying it here would remove nothing and report " +
                "having done so. Nothing was changed.");
            output.WriteLine(
                $"Set BATON_HOME to '{manifest.BatonRoot}' and run the same command again.");
            return 1;
        }

        var shortfalls = new List<string>();
        var removed = 0;
        foreach (var group in manifest.Appended.GroupBy(r => r.EntriesFilePath, StringComparer.OrdinalIgnoreCase))
        {
            var expected = group.Select(r => r.EntryId).Distinct(StringComparer.Ordinal).ToList();
            var count = await MemoryStore.RemoveAsync(expected, group.Key, cancellationToken).ConfigureAwait(false);
            removed += count;

            if (count != expected.Count)
            {
                shortfalls.Add($"  {group.Key}: expected {expected.Count}, removed {count}");
            }
        }

        var removedLinks = 0;
        foreach (var group in manifest.AppendedLinks.GroupBy(l => l.LinksFilePath, StringComparer.OrdinalIgnoreCase))
        {
            var expected = group.Select(l => l.LinkId).Distinct(StringComparer.Ordinal).ToList();
            var count = await MemoryStore.RemoveLinksAsync(expected, group.Key, cancellationToken).ConfigureAwait(false);
            removedLinks += count;

            if (count != expected.Count)
            {
                shortfalls.Add($"  {group.Key}: expected {expected.Count} link(s), removed {count}");
            }
        }

        output.WriteLine(
            $"Removed {removed} canonical entr{(removed == 1 ? "y" : "ies")} and {removedLinks} " +
            $"supersession link(s) across " +
            $"{manifest.Appended.Select(r => r.EntriesFilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()} store file(s).");
        output.WriteLine(
            "No source memory file was touched -- the import never wrote to one, so there is nothing on " +
            "the vendors' side to restore.");

        if (shortfalls.Count == 0)
        {
            return 0;
        }

        output.WriteLine();
        output.WriteLine(
            "INCOMPLETE -- the undo removed less than this manifest says the import appended. A store " +
            "file that is missing, moved, or already partly undone reads exactly like this; nothing " +
            "here was removed twice, and re-running this undo is safe.");
        foreach (var shortfall in shortfalls)
        {
            output.WriteLine(shortfall);
        }

        return 1;
    }

    /// <summary>
    /// Whether two storage-root spellings name one directory. A manifest root that is not a usable path
    /// at all answers <see langword="false"/> — it certainly is not this process's root, and the undo's
    /// refusal is the right outcome for a manifest that cannot be trusted about where it wrote.
    /// </summary>
    private static bool IsSameRoot(string manifestRoot, string currentRoot)
    {
        try
        {
            return BatonPaths.RecordKeyComparer.Equals(
                BatonPaths.RecordKey(manifestRoot), BatonPaths.RecordKey(currentRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void WriteReport(
        TextWriter output, MemoryImportOptions options, ImportManifest manifest, string? manifestPath)
    {
        output.WriteLine(
            options.DryRun
                ? "baton memory import --dry-run -- NOTHING WAS WRITTEN. No entry, and no manifest either."
                : "baton memory import -- source files were opened READ-ONLY and are unchanged.");
        output.WriteLine();

        var appended = manifest.Appended.Count();
        output.WriteLine(
            $"Entries: {manifest.Entries.Count}   {(options.DryRun ? "would append" : "appended")}: {appended}   " +
            $"already present: {manifest.Entries.Count - appended}");
        var projectionsSkipped = manifest.ProjectionsSkipped ?? [];
        output.WriteLine(
            $"Unfiled: {manifest.Unfiled.Count}   machinery recorded: {manifest.Machinery.Count}   " +
            $"projection-skipped: {projectionsSkipped.Count}");

        var links = manifest.Links ?? [];
        var appendedLinks = manifest.AppendedLinks.Count();
        output.WriteLine(
            $"Supersession links: {links.Count}   {(options.DryRun ? "would record" : "recorded")}: " +
            $"{appendedLinks}   already recorded: {links.Count - appendedLinks}");

        foreach (var group in manifest.Entries
                     .GroupBy(r => r.EntriesFilePath, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            output.WriteLine();
            output.WriteLine($"  {group.Key}");
            output.WriteLine($"    repository={group.First().Repository} entries={group.Count()}");
        }

        if (manifest.Unfiled.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Unfiled -- read, digested, and imported NOWHERE. Every one is untouched:");
            foreach (var reason in manifest.Unfiled.GroupBy(u => u.Reason).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                output.WriteLine($"  [{reason.Count()} file(s)] {reason.Key}");
            }
        }

        if (projectionsSkipped.Count > 0)
        {
            // Named, not counted, and named separately from the unfiled: these are Baton's own caches,
            // and an operator who saw them only as a total would have no way to tell a projection this
            // verb correctly refused from a memory it failed to file.
            output.WriteLine();
            output.WriteLine(
                "Projection-skipped -- Baton's own generated caches, recognised by their format marker " +
                "and imported NOWHERE. Re-importing one would feed the store its own contents:");
            foreach (var row in projectionsSkipped.OrderBy(r => r.SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                output.WriteLine($"  {row.SourcePath}");
            }
        }

        if (manifestPath is { Length: > 0 })
        {
            output.WriteLine();
            output.WriteLine($"Manifest: {manifestPath}");
            output.WriteLine($"Undo it with: baton memory import --undo {manifestPath}");
        }
    }
}
