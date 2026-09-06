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
        var machinery = new List<ImportSkippedRow>();

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
                machinery.AddRange(root.Files.Select(f => new ImportSkippedRow(
                    f.Path, f.Sha256, f.ModifiedUtc, f.SizeBytes,
                    "Codex sqlite store: the pipeline that produces the markdown memories, not a memory " +
                    "source. Recorded for provenance; nothing was read out of it.")));
            }
        }

        sources = ApplyRootFilter(sources, options.Roots);

        var withFiles = sources
            .Select(source => source with { Files = ReadSourceFiles(source) })
            .ToList();

        var plan = MemoryImportPlan.Build(withFiles, DateTime.UtcNow);

        var sizeByPath = withFiles
            .SelectMany(s => s.Files)
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().SizeBytes, StringComparer.OrdinalIgnoreCase);

        var rows = new List<ImportManifestRow>();
        foreach (var group in plan.Entries.GroupBy(e => e.Repository, StringComparer.OrdinalIgnoreCase))
        {
            var entriesFile = BatonPaths.MemoryEntriesFile(RepositoryIdentity.FileSlugFor(group.Key));
            var entries = group.ToList();

            // Read first so the manifest can say which rows THIS run appended: an undo must not remove
            // an entry an earlier import wrote. The append itself re-checks under its own lock, so this
            // read is a report input and never the thing that keeps the file free of duplicates.
            var existing = (await MemoryStore.ReadAllAsync(entriesFile, cancellationToken).ConfigureAwait(false))
                .Select(e => e.Id)
                .ToHashSet(StringComparer.Ordinal);

            if (!options.DryRun)
            {
                await MemoryStore.AppendAsync(entries, entriesFile, cancellationToken).ConfigureAwait(false);
            }

            rows.AddRange(entries.Select(e => new ImportManifestRow(
                e.SourcePath, e.Sha256, e.SourceMtimeUtc,
                sizeByPath.TryGetValue(e.SourcePath, out var size) ? size : 0,
                e.SourceVendor, e.SourceScope, e.Id, e.Repository, entriesFile,
                AlreadyPresent: existing.Contains(e.Id))));
        }

        var manifest = new ImportManifest(
            ImportManifest.CurrentVersion,
            DateTime.UtcNow,
            batonRoot,
            rows.OrderBy(r => r.EntriesFilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.SourcePath, StringComparer.OrdinalIgnoreCase).ToList(),
            plan.Unfiled,
            machinery);

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
                BatonPaths.RecordKey(a.Path), a.Repository, assertedBy, DateTime.UtcNow))
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
    /// The path resolution is <see cref="MemoryAuditCommand"/>'s, unchanged and for the same reasons —
    /// session <c>cwd</c> is ground truth, the decoder's tie-break is offered only fully-qualified
    /// readings that are a work tree's own root. What is added here is the alias fallback, which fires
    /// only where the probe produced nothing: <see cref="MemoryAliasStore"/>'s own remarks state why an
    /// assertion may never displace a measurement, and why this is not the mechanism for the
    /// subject-versus-origin question.
    /// </para>
    /// <para>
    /// <b>The fallback tries two keys, and the second is the one that makes the archive importable.</b>
    /// An archived root has no session transcript and its name is a flattening of the memory directory
    /// rather than of a checkout (<c>c--Users-…-repos-baton-memory</c>), so no reading of it is a work
    /// tree's own root and the resolution carries no checkout path at all — an alias keyed on a
    /// checkout could never match one. The root's OWN directory is always known and never ambiguous, so
    /// an assertion may be keyed on it directly: "the memories in this directory belong to this
    /// repository", which is the fact an operator actually has about an archive their own migration
    /// created.
    /// </para>
    /// </remarks>
    private static async Task<MemoryImportSource> ResolveClaudeRootAsync(
        MemoryRoot root, IReadOnlyList<MemoryAliasEntry> aliases, CancellationToken cancellationToken)
    {
        var resolution = MemoryRootPath.Resolve(
            root.DirectoryName,
            MemoryRootPath.ReadSessionWorkingDirectories(root.SessionDirectoryPath),
            RepositoryIdentityResolver.IsWorkTreeRoot);

        var checkoutExists = resolution.CheckoutPath is { Length: > 0 } path && Directory.Exists(path);
        var repository = checkoutExists
            ? (await RepositoryIdentityResolver
                .TryResolveAsync(resolution.CheckoutPath!, cancellationToken).ConfigureAwait(false))?.Value
            : null;

        var asserted = repository is null
            ? MemoryAliasStore.Resolve(aliases, resolution.CheckoutPath)
                ?? MemoryAliasStore.Resolve(aliases, root.DirectoryPath)
            : null;

        return new MemoryImportSource(
            root.DirectoryPath,
            MemoryRootInventory.ClaudeVendor,
            VendorMemoryScope.Vendor,
            root.Kind == MemoryRootKind.Archive,
            repository ?? asserted,
            UnfiledReason: DescribeUnresolvedClaudeRoot(resolution, checkoutExists),
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
    /// <paramref name="sources"/> narrowed to <paramref name="roots"/>, or all of them when none was
    /// named. A named path that matches no discovered root throws rather than being ignored: the
    /// operator asked for something that is not there, and an import that silently did less than it
    /// was asked would look identical to one that found nothing to do.
    /// </summary>
    private static List<MemoryImportSource> ApplyRootFilter(
        List<MemoryImportSource> sources, IReadOnlyList<string> roots)
    {
        if (roots.Count == 0)
        {
            return sources;
        }

        var selected = new List<MemoryImportSource>();
        foreach (var requested in roots)
        {
            var key = BatonPaths.RecordKey(requested);
            var match = sources.FirstOrDefault(
                s => BatonPaths.RecordKeyComparer.Equals(BatonPaths.RecordKey(s.RootDirectoryPath), key));

            if (match is null)
            {
                throw new CliArgumentException(
                    $"'--root {requested}' matches no discovered memory root. This option selects from " +
                    "what discovery found; it cannot add a directory. Run 'baton memory audit' to see " +
                    "the roots that exist.");
            }

            if (!selected.Contains(match))
            {
                selected.Add(match);
            }
        }

        return selected;
    }

    /// <summary>
    /// The same file rows with their text read in. <b>The one place a memory's contents are read</b>,
    /// and they are read for copying rather than for meaning — opened with
    /// <see cref="FileAccess.Read"/>, and never written back.
    /// </summary>
    /// <remarks>
    /// A file that vanished or became unreadable between the inventory and this read is dropped rather
    /// than throwing, matching the inventory's own posture: one lost row in a walk that otherwise
    /// finished. It cannot silently become an empty entry — a dropped file contributes neither an
    /// entry nor a manifest row, so the import's own accounting shows it was not carried.
    /// </remarks>
    private static IReadOnlyList<MemoryImportFile> ReadSourceFiles(MemoryImportSource source)
    {
        var files = new List<MemoryImportFile>(source.Files.Count);
        foreach (var file in source.Files)
        {
            try
            {
                using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                files.Add(file with { Text = reader.ReadToEnd() });
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
    /// <b>It touches no source file, because the import touched none either.</b> "Undo" here means the
    /// canonical store returns to what it held before the import — there is nothing to restore on the
    /// vendor's side, which is the whole point of a non-destructive import and is stated in the output
    /// so an operator does not go looking for a restore that would have no work to do.
    /// </remarks>
    private static async Task<int> UndoAsync(
        string manifestPath, TextWriter output, CancellationToken cancellationToken)
    {
        var manifest = ImportManifest.Read(manifestPath);

        var removed = 0;
        foreach (var group in manifest.Appended.GroupBy(r => r.EntriesFilePath, StringComparer.OrdinalIgnoreCase))
        {
            removed += await MemoryStore
                .RemoveAsync(group.Select(r => r.EntryId).ToList(), group.Key, cancellationToken)
                .ConfigureAwait(false);
        }

        output.WriteLine($"baton memory import --undo {manifestPath}");
        output.WriteLine(
            $"Removed {removed} canonical entr{(removed == 1 ? "y" : "ies")} across " +
            $"{manifest.Appended.Select(r => r.EntriesFilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()} store file(s).");
        output.WriteLine(
            "No source memory file was touched -- the import never wrote to one, so there is nothing on " +
            "the vendors' side to restore.");
        return 0;
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
        output.WriteLine($"Unfiled: {manifest.Unfiled.Count}   machinery recorded: {manifest.Machinery.Count}");

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

        if (manifestPath is { Length: > 0 })
        {
            output.WriteLine();
            output.WriteLine($"Manifest: {manifestPath}");
            output.WriteLine($"Undo it with: baton memory import --undo {manifestPath}");
        }
    }
}
