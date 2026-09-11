using System.Globalization;
using Baton.Accounting;
using Baton.Memory;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// <c>baton memory add --text &lt;text&gt; --kind &lt;kind&gt; [--repository &lt;id&gt;] [--dry-run]</c>
/// (#2071, slice one of #1852's write path): append ONE authored memory to the canonical
/// per-repository store, refusing one that is byte-identical to an entry already there.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same store the importer writes, through the same type</b> — <see cref="MemoryStore"/>, whose
/// ledger and lock this does not go around. #2071's "record once: no second writer" is the whole point
/// of the slice: if <c>add</c> owned its own append, the canonical store would have two writers with
/// two ideas of idempotence, and the vendor files would be projections of whichever won.
/// </para>
/// <para>
/// <b>The manifest is <see cref="ImportManifest"/>, not a second reversal format.</b> An add is one
/// row appended to one store file, which is exactly the shape <c>import --undo</c> already replays, so
/// this writes a one-row manifest and an operator reverses an add with the verb they already have.
/// Every other population on that manifest is empty and means what it says: this run looked at no
/// files at all.
/// </para>
/// <para>
/// A successful append immediately runs the same idempotent projection as <c>memory sync --apply</c>
/// (#2138). Projection failure cannot roll back the canonical row: it leaves a durable pending
/// obligation for the daemon and is reported as committed-but-unprojected.
/// </para>
/// </remarks>
public static class MemoryAddCommand
{
    /// <param name="assertedByOverride">
    /// Test seam. Production callers pass nothing and the value is read from this process's own
    /// environment through <see cref="MemoryLaneAssertion.Resolve()"/> — a test needs the seam because
    /// the suite itself may be running inside a lane, so "unset" is not a state it can observe by
    /// simply not setting it.
    /// </param>
    /// <param name="repositoryProbe">
    /// Test seam for the git probe behind the default subject. Production callers pass nothing and get
    /// <see cref="RepositoryIdentityResolver.TryResolveAsync"/> at the working directory.
    /// <para>
    /// <b>A probe's answer is used as it comes, while <c>--repository</c> is held to a host refusal —
    /// and that asymmetry is deliberate, not an omission.</b>
    /// <see cref="MemoryImportOptionsParser.RequireAHostThatAProbeCouldAnswer"/>'s own remarks say why:
    /// a probe reading an intranet remote legitimately yields a dotless identity, so the refusal is a
    /// property of what an operator TYPED. Applying it here would refuse a store that git itself named.
    /// </para>
    /// </param>
    public static async Task<int> ExecuteAsync(
        MemoryAddOptions options,
        TextWriter output,
        string? assertedByOverride = null,
        Func<CancellationToken, Task<string?>>? repositoryProbe = null,
        CancellationToken cancellationToken = default,
        string? claudeHomeOverride = null,
        string? userHomeOverride = null,
        Action<string, byte[]>? projectionWriterOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        if (options.Help)
        {
            output.WriteLine(MemoryAddOptionsParser.Usage);
            foreach (var line in MemoryAddOptionsParser.HelpLines)
            {
                output.WriteLine(line);
            }

            return 0;
        }

        var repository = options.Repository
            ?? await ProbeRepositoryAsync(repositoryProbe, cancellationToken).ConfigureAwait(false)
            ?? throw new BatonMemoryException(
                "No repository to file this memory under: git answers for nothing in " +
                $"'{Environment.CurrentDirectory}' and no '--repository <id>' was passed. This verb " +
                "will not guess a subject — run it inside the checkout the memory is about, or name " +
                "the repository. " + MemoryAddOptionsParser.Usage);

        var slug = FleetMemory.SlugFor(repository);
        var entriesFile = BatonPaths.MemoryEntriesFile(slug);
        var entry = AuthoredMemory.Create(
            repository, options.Text, options.Kind, assertedByOverride ?? MemoryLaneAssertion.Resolve(),
            DateTime.UtcNow);

        // Read-then-append, and the ledger's own id check under its lock is the backstop: two adds of
        // one text racing would both pass this read and the second append would write nothing. What the
        // read buys is the REPORT — MemoryStore.AppendAsync returns no count, so without it a duplicate
        // add exits 0 saying it wrote a row that was already there.
        var stored = await MemoryStore.ReadAllAsync(entriesFile, cancellationToken).ConfigureAwait(false);
        if (stored.FirstOrDefault(e => string.Equals(e.Id, entry.Id, StringComparison.Ordinal)) is { } existing)
        {
            output.WriteLine(
                $"REFUSED  this memory is byte-identical to entry {existing.Id} " +
                $"({MemoryJsonNames.Of(existing.Kind)}, asserted by " +
                $"{existing.AssertedBy ?? "an import"}) already in {entriesFile}.");
            output.WriteLine(
                "         The store is append-only and an entry's id is derived from its subject and " +
                "its text, so re-adding the same words is not a second fact. Change the text to record " +
                "a different one.");
            return 1;
        }

        if (options.DryRun)
        {
            output.WriteLine($"DRY RUN  nothing was written — no store row, no manifest, no directory.");
            WriteWouldAdd(output, entry, entriesFile);
            return 0;
        }

        await MemoryStoreMetadataStore.EnsureAsync(repository, slug, cancellationToken).ConfigureAwait(false);

        // Metadata initialization is the canonical commit boundary. It re-checks cancellation after
        // acquiring its mutex; once it succeeds, finish the first append so metadata cannot describe
        // an empty store solely because cancellation arrived between these two durable writes.
        var appended = await MemoryStore.AppendAndGetAppendedAsync([entry], entriesFile, CancellationToken.None)
            .ConfigureAwait(false);
        if (appended.Count == 0)
        {
            output.WriteLine(
                $"REFUSED  this memory became entry {entry.Id}, but a concurrent writer appended it first.");
            output.WriteLine("         Nothing was appended by this call, so it did not create a manifest or a projection obligation.");
            return 1;
        }

        var manifest = new ImportManifest(
            ImportManifest.CurrentVersion,
            entry.ImportedAtUtc,
            BatonPaths.Root,
            [new ImportManifestRow(
                entry.SourcePath,
                entry.Sha256,
                entry.SourceMtimeUtc,
                // The text's own byte length: an authored entry has no file, so this is the size of what
                // was stored rather than of anything on disk.
                System.Text.Encoding.UTF8.GetByteCount(entry.Text),
                entry.SourceVendor,
                entry.SourceScope,
                entry.Id,
                entry.Repository,
                entriesFile)],
            Unfiled: [],
            Machinery: []);

        var manifestPath = BatonPaths.MemoryImportManifestFile(
            "add-" + entry.ImportedAtUtc.ToString("yyyyMMdd'T'HHmmss'.'fff'Z'", CultureInfo.InvariantCulture));
        manifest.Write(manifestPath);

        output.WriteLine($"ADDED    {entry.Id} to {entriesFile}");
        WriteWouldAdd(output, entry, entriesFile);
        output.WriteLine($"MANIFEST {manifestPath}");
        output.WriteLine($"         undo it with: baton memory import --undo \"{manifestPath}\"");
        await MemoryProjectionTrigger.ProjectAfterCanonicalWriteAsync(
            repository,
            output,
            claudeHomeOverride,
            userHomeOverride,
            projectionWriterOverride,
            cancellationToken).ConfigureAwait(false);

        return 0;
    }

    /// <summary>
    /// The three facts a reader needs to tell what was (or would be) filed, printed identically in
    /// both modes — a dry run that described the write differently would not be a preview of it.
    /// </summary>
    private static void WriteWouldAdd(TextWriter output, MemoryEntry entry, string entriesFile)
    {
        output.WriteLine($"         repository  {entry.Repository}");
        output.WriteLine(
            $"         kind        {MemoryJsonNames.Of(entry.Kind)} " +
            $"({MemoryJsonNames.Of(entry.KindSource)})");
        output.WriteLine($"         assertedBy  {entry.AssertedBy}");
        output.WriteLine($"         entry       {entry.Id} -> {entriesFile}");
    }

    private static async Task<string?> ProbeRepositoryAsync(
        Func<CancellationToken, Task<string?>>? probe, CancellationToken cancellationToken)
    {
        if (probe is not null)
        {
            return await probe(cancellationToken).ConfigureAwait(false);
        }

        var identity = await RepositoryIdentityResolver
            .TryResolveAsync(Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);

        return identity?.Value;
    }
}
