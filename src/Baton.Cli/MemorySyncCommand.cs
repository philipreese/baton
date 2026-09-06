using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Accounting;
using Baton.Memory;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// <c>baton memory sync [--repository &lt;id&gt;] [--apply] [--format text|json] [--repository-facts &lt;dir&gt;]</c>
/// (#1852 phase C): project the canonical memory store into the vendor memory roots that already
/// exist, as a self-identifying cache.
/// </summary>
/// <remarks>
/// <para>
/// <b>The projection itself is not here.</b> <see cref="MemoryProjection.Build"/> is pure and owns
/// every decision that determines the bytes — the order, the header, supersession, conflict
/// precedence, the budget. What lives here is the part that cannot be pure: reading the store, running
/// the same discovery <c>audit</c> and <c>import</c> run, and writing files. That split is what makes
/// the byte-identity claim testable without a filesystem, and it is the same split
/// <see cref="MemoryImportCommand"/> already draws against <c>MemoryImportPlan</c>.
/// </para>
/// <para>
/// <b>Without <c>--apply</c> nothing is written and no directory is created.</b> Not "no memory is
/// changed" — no byte reaches the disk at all, including the <c>mkdir</c> that a dry run which
/// prepared its output directory would leave behind. The report is computed from the same bytes the
/// apply path would write, so a dry run previews the real thing rather than a similar one.
/// </para>
/// <para>
/// <b>Targets are discovered, never constructed</b>, for the reason
/// <see cref="ClaudeProjectionTarget"/>'s remarks give — the project-directory encoding is lossy, so
/// minting one would assert a mapping nothing can confirm. A repository whose store holds memories but
/// whose machine holds no matching root is reported with <b>no target</b>, which is the honest answer
/// and the one an operator can act on.
/// </para>
/// <para>
/// <b>Reads and writes happen under the canonical store's own lock</b>
/// (<see cref="MemoryStore.RunUnderEntriesLockAsync"/>), so a concurrent <c>baton memory import</c>
/// cannot append between the read that fed a projection and the write that lands it.
/// </para>
/// </remarks>
public static class MemorySyncCommand
{
    /// <param name="claudeHomeOverride">Test seam — see <see cref="MemoryImportCommand.ExecuteAsync"/>'s own parameter doc.</param>
    /// <param name="userHomeOverride">Test seam for the non-Claude vendor roots, separate for the same reason.</param>
    public static async Task<int> ExecuteAsync(
        MemorySyncOptions options,
        TextWriter output,
        string? claudeHomeOverride = null,
        CancellationToken cancellationToken = default,
        string? userHomeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        if (options.Help)
        {
            output.WriteLine(MemorySyncOptionsParser.Usage);
            foreach (var line in MemorySyncOptionsParser.HelpLines)
            {
                output.WriteLine(line);
            }

            return 0;
        }

        var claudeHome = claudeHomeOverride ?? MemoryRootInventory.DefaultClaudeHome;
        var userHome = userHomeOverride ?? MemoryRootInventory.DefaultUserHome;

        var repositoryFacts = ReadRepositoryFacts(options);
        var discovery = await DiscoverTargetsAsync(claudeHome, userHome, cancellationToken)
            .ConfigureAwait(false);

        var reports = new List<SyncRepositoryReport>();
        foreach (var slug in StoredRepositorySlugs(options.Repository))
        {
            var report = await SyncOneAsync(
                slug, options, repositoryFacts, discovery.ByRepository, cancellationToken).ConfigureAwait(false);
            if (report is not null)
            {
                reports.Add(report);
            }
        }

        var syncReport = new SyncReport(
            options.Apply,
            options.RepositoryFactsDirectory,
            repositoryFacts.Count,
            reports.OrderBy(r => r.Repository, StringComparer.Ordinal).ToList(),
            discovery.NonTargets);

        if (options.Format == MemoryAuditOutputFormat.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(syncReport, ReportJson));
        }
        else
        {
            WriteText(output, syncReport);
        }

        return 0;
    }

    /// <summary>
    /// One repository's projection, computed and — under <c>--apply</c> — written, with the store's
    /// entries read inside its own lock.
    /// </summary>
    /// <remarks>
    /// The links file is read <b>before</b> the entries lock is taken, never inside it:
    /// <see cref="MemoryStore.ReadResolvedAsync"/>'s remarks carry the rule (the two locks are never
    /// nested), and a link appended in the gap is picked up by the next run rather than deadlocking
    /// this one.
    /// </remarks>
    private static async Task<SyncRepositoryReport?> SyncOneAsync(
        string slug,
        MemorySyncOptions options,
        IReadOnlyList<MemoryProjectionCandidate> repositoryFacts,
        IReadOnlyDictionary<string, List<ProjectionTarget>> targetsByRepository,
        CancellationToken cancellationToken)
    {
        var entriesFile = BatonPaths.MemoryEntriesFile(slug);

        // Checked BEFORE the lock. `--repository <id>` names a slug rather than selecting a directory
        // that exists, so an identity with no store reaches here -- a typo is enough. Measured, so the
        // comment does not overclaim: removing this guard does NOT break
        // `A_repository_with_no_store_creates_nothing_under_the_baton_root`, because
        // MutexGuardedFileLock does not create the path it locks. It stays because it makes the
        // no-store answer independent of that behaviour rather than resting on it, and because taking a
        // named mutex to discover a file is absent is work with no result. The store-is-empty case is
        // still handled below: an existing but empty file is a different state from an absent one.
        if (!File.Exists(entriesFile))
        {
            return null;
        }

        var links = await MemoryStore
            .ReadLinksAsync(BatonPaths.MemoryLinksFile(slug), cancellationToken).ConfigureAwait(false);

        return await MemoryStore.RunUnderEntriesLockAsync(
            entriesFile,
            stored =>
            {
                if (stored.Count == 0)
                {
                    return null;
                }

                var resolved = MemoryStore.Resolve(stored, links);
                var repository = resolved[0].Repository;
                var candidates = resolved
                    .Select(e => new MemoryProjectionCandidate(e, MemoryFactOrigin.Vendor))
                    .Concat(repositoryFacts.Where(f => string.Equals(
                        f.Entry.Repository, repository, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                var projection = MemoryProjection.Build(
                    repository, entriesFile, candidates, ProjectionBudget.Default);

                var targets = targetsByRepository.TryGetValue(repository, out var found)
                    ? found.OrderBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase).ToList()
                    : [];

                var writes = new List<SyncTargetReport>();
                foreach (var target in targets)
                {
                    writes.Add(WriteOrPreview(target, projection.Bytes, options.Apply));
                }

                return new SyncRepositoryReport(
                    repository,
                    entriesFile,
                    projection.BodySha256,
                    projection.ProjectedEntryIds.Count,
                    writes,
                    targets.Count == 0 ? NoTargetGuidance(repository) : null,
                    projection.Superseded,
                    projection.Overridden,
                    projection.Dropped);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What an operator does about a repository whose memories have nowhere to go. A reason with no
    /// remedy reads as a refusal rather than as the question it is —
    /// <see cref="MemoryImportCommand"/>'s unfiled reasons take the same shape.
    /// </summary>
    private static string NoTargetGuidance(string repository) =>
        $"no vendor memory root on this machine resolves to '{repository}', so there is nothing to " +
        "project into and nothing was created -- run 'baton memory audit' to see which roots exist, " +
        "then assert a per-machine Codex root's repository with 'baton memory import --assert " +
        $"<root>={repository}' (see spec/baton.md §12).";

    /// <summary>
    /// The one place a projection reaches the disk, and the one place a dry run is proven not to.
    /// </summary>
    /// <remarks>
    /// <b>The comparison is over bytes, not over a timestamp or a length.</b> "Unchanged" here means
    /// the existing file is byte-for-byte what this run would write, which is the same claim
    /// <c>MemoryProjectionTests</c> asserts and the same one the acceptance line makes. The target
    /// directory is never created: every target is a root discovery already found, so the directory
    /// exists by construction, and a missing one is a root that vanished mid-run rather than a
    /// directory this verb should mint.
    /// </remarks>
    private static SyncTargetReport WriteOrPreview(ProjectionTarget target, byte[] bytes, bool apply)
    {
        var existing = File.Exists(target.FilePath) ? File.ReadAllBytes(target.FilePath) : null;
        var unchanged = existing is not null && existing.AsSpan().SequenceEqual(bytes);

        if (apply && !unchanged)
        {
            var tempPath = $"{target.FilePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, target.FilePath, overwrite: true);
        }

        return new SyncTargetReport(
            target.Vendor,
            target.RootDirectoryPath,
            target.FilePath,
            unchanged ? "unchanged" : existing is null ? (apply ? "created" : "would create") : (apply ? "rewritten" : "would rewrite"),
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    /// <summary>
    /// Every repository slug that has a canonical store, or just the one <c>--repository</c> named.
    /// </summary>
    /// <remarks>
    /// The enumeration is over <c>{BATON_HOME}/&lt;slug&gt;/memory/entries.jsonl</c> rather than over a
    /// registry, because there is no registry: Q3's layout makes the repository directory the unit, so
    /// the directories on disk ARE the list. A directory with no store file is not a repository this
    /// verb knows about — <c>rooms/</c>, <c>ledger/</c> and the rest of <c>{BATON_HOME}</c> sit beside
    /// them and are skipped by exactly that test.
    /// </remarks>
    private static IEnumerable<string> StoredRepositorySlugs(string? repository)
    {
        if (repository is { Length: > 0 })
        {
            yield return RepositoryIdentity.FileSlugFor(repository);
            yield break;
        }

        if (!Directory.Exists(BatonPaths.Root))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(BatonPaths.Root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var slug = Path.GetFileName(directory);
            if (File.Exists(BatonPaths.MemoryEntriesFile(slug)))
            {
                yield return slug;
            }
        }
    }

    /// <summary>
    /// Every vendor memory root this machine holds that a projection could be written into, grouped by
    /// the repository it resolves to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same discovery <c>audit</c> and <c>import</c> use, filtered — never a second
    /// enumeration.</b> <see cref="MemoryRootInventory"/> stays the single definition of "a memory
    /// root" (spec/baton.md §12), which is what keeps <c>audit</c> a preview of what the other two
    /// verbs will touch. What is filtered out here is everything that is not a live markdown surface:
    /// the Codex sqlite family and every Antigravity family are discovered and then are not targets, by
    /// Q4's ruling, and an <b>archived</b> Claude root is refused by construction — see
    /// <see cref="NonTargetRoot"/> for why an archive is never written into even when an operator has
    /// asserted a repository for it, which is precisely the workflow Q2 recommends.
    /// </para>
    /// <para>
    /// <b>Every discovered root that is not a target comes back with a reason.</b> A root silently
    /// absent from the report is indistinguishable from one that does not exist, and "discovered, not
    /// written" is a claim four documents make (<see cref="MemorySyncOptionsParser.HelpLines"/>,
    /// spec/baton.md §12, README.md, docs/agents/invoking-baton.md) that nothing used to print.
    /// </para>
    /// </remarks>
    private static async Task<TargetDiscovery> DiscoverTargetsAsync(
        string claudeHome, string userHome, CancellationToken cancellationToken)
    {
        var byRepository = new Dictionary<string, List<ProjectionTarget>>(StringComparer.OrdinalIgnoreCase);
        var nonTargets = new List<NonTargetRoot>();

        var aliases = await MemoryAliasStore
            .ReadAllAsync(BatonPaths.MemoryAliasFile, cancellationToken).ConfigureAwait(false);

        foreach (var root in MemoryRootInventory.Scan(claudeHome, cancellationToken))
        {
            // Checked BEFORE any resolution, so an asserted archive cannot become a target by another
            // route. The alias fallback below is exactly how an archived root acquires a repository
            // (its flattened name decodes to no checkout), so ordering these the other way would leave
            // the refusal resting on a resolution that is designed to succeed here.
            if (root.Kind != MemoryRootKind.Live)
            {
                nonTargets.Add(new NonTargetRoot(
                    root.DirectoryPath,
                    MemoryRootInventory.ClaudeVendor,
                    $"READ-ONLY historical root (memory-archive/{root.ArchiveLabel}). A projection is the " +
                    "CURRENT reading of a repository's memory and an archive is a record of what was, so " +
                    "this is never a write target -- including when an operator has asserted a repository " +
                    "for it, which is how an archive is imported. Nothing was written here."));
                continue;
            }

            var resolved = await ClaudeMemoryRootResolver.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
            var repository = resolved.RepositoryValue
                ?? MemoryAliasStore.Resolve(aliases, resolved.Path.CheckoutPath)
                ?? MemoryAliasStore.Resolve(aliases, root.DirectoryPath);
            if (repository is { Length: > 0 })
            {
                Add(byRepository, repository, ClaudeProjectionTarget.For(root.DirectoryPath));
            }
            else
            {
                nonTargets.Add(new NonTargetRoot(
                    root.DirectoryPath,
                    MemoryRootInventory.ClaudeVendor,
                    "no repository identity resolves for this root, so there is no store to project from. " +
                    "Assert one with 'baton memory import --assert <this root>=<repository>'."));
            }
        }

        foreach (var root in MemoryRootInventory.ScanVendorRoots(userHome, BatonPaths.Root, limits: null, cancellationToken))
        {
            if (!Directory.Exists(root.DirectoryPath))
            {
                continue;
            }

            if (root.Family != VendorMemoryRootTable.CodexMarkdownFamily)
            {
                nonTargets.Add(new NonTargetRoot(
                    root.DirectoryPath,
                    root.SourceVendor,
                    $"family '{root.Family}' is not a markdown surface, so it is inventoried and NEVER " +
                    "written: Q4 (operator, 2026-09-05) confined this phase to markdown, and byte-identical " +
                    "idempotence over sqlite+WAL is a different problem with a different instrument."));
                continue;
            }

            if (MemoryAliasStore.Resolve(aliases, root.DirectoryPath) is { Length: > 0 } repository)
            {
                Add(byRepository, repository, CodexProjectionTarget.For(root.DirectoryPath, root.SourceScope));
            }
            else
            {
                nonTargets.Add(new NonTargetRoot(
                    root.DirectoryPath,
                    root.SourceVendor,
                    "a per-machine root that encodes no checkout, and no operator has asserted a repository " +
                    "for it -- so it is UNASSIGNED rather than handed whichever repository this run named. " +
                    "Assert one with 'baton memory import --assert <this root>=<repository>'."));
            }
        }

        return new TargetDiscovery(
            byRepository,
            nonTargets.OrderBy(r => r.RootDirectoryPath, StringComparer.OrdinalIgnoreCase).ToList());

        static void Add(Dictionary<string, List<ProjectionTarget>> map, string repository, ProjectionTarget target)
        {
            if (!map.TryGetValue(repository, out var list))
            {
                list = [];
                map[repository] = list;
            }

            list.Add(target);
        }
    }

    /// <summary>
    /// The checked-in repository facts <c>--repository-facts</c> points at, as projection candidates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Baton mints no convention for where these live.</b> Phase B's scope is explicit that the
    /// canonical store is under <c>~/.baton</c> and never in a checkout; putting a Baton-owned
    /// directory inside every consumer's repository is a decision of a different weight than this
    /// phase carries, so the operator names the directory and Baton creates nothing. Absent the flag
    /// the conflict rule has an empty population, and the report says so in those words rather than
    /// printing a zero an operator would read as "no conflicts found".
    /// </para>
    /// <para>
    /// <b>These carry the same id derivation as an imported entry</b>
    /// (<see cref="MemoryEntry.Derive"/>), so a repository fact's id is stable across runs and appears
    /// in the projection's back-pointers exactly as a vendor entry's does.
    /// <see cref="MemoryEntry.ImportedAtUtc"/> is left at its default: nothing reads it here, and
    /// putting a clock on a value that feeds a byte-identical projection is the failure the projector's
    /// remarks exist to prevent.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<MemoryProjectionCandidate> ReadRepositoryFacts(MemorySyncOptions options)
    {
        if (options.RepositoryFactsDirectory is not { Length: > 0 } directory
            || options.Repository is not { Length: > 0 } repository)
        {
            return [];
        }

        if (!Directory.Exists(directory))
        {
            throw new CliArgumentException(
                $"'--repository-facts {directory}' is not a directory that exists. This verb reads " +
                "checked-in repository facts from a directory you name; it never creates one.",
                "point it at a directory of *.md facts inside the checkout.");
        }

        var facts = new List<MemoryProjectionCandidate>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = File.ReadAllBytes(path);
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var (kind, kindSource) = MemoryKindInference.Infer(Path.GetFileName(path), text);

            facts.Add(new MemoryProjectionCandidate(
                new MemoryEntry(
                    MemoryEntry.Derive(repository, path, sha256),
                    repository,
                    kind,
                    kindSource,
                    text,
                    sha256,
                    path,
                    SourceVendor: "repository",
                    VendorMemoryScope.Vendor,
                    File.GetLastWriteTimeUtc(path),
                    ImportedAtUtc: default),
                MemoryFactOrigin.Repository));
        }

        return facts;
    }

    private static void WriteText(TextWriter output, SyncReport report)
    {
        output.WriteLine(
            report.Apply
                ? "baton memory sync --apply -- projections written. The canonical store was NOT changed."
                : "baton memory sync -- NOTHING WAS WRITTEN. No file, and no directory either.");
        output.WriteLine();
        output.WriteLine(
            report.RepositoryFactsDirectory is { Length: > 0 } directory
                ? $"Repository facts considered: {report.RepositoryFactsConsidered} (from {directory})"
                : "Repository facts considered: 0 -- none were supplied, so the conflict rule had an " +
                  "empty population on this run. Pass '--repository-facts <dir>' with '--repository <id>' " +
                  "to weigh checked-in facts against the vendor-sourced ones.");

        if (report.Repositories.Count == 0)
        {
            output.WriteLine();
            output.WriteLine("No repository has a canonical memory store yet. Run 'baton memory import' first.");
            WriteNonTargets(output, report.NonTargetRoots);
            return;
        }

        foreach (var repository in report.Repositories)
        {
            output.WriteLine();
            output.WriteLine($"  {repository.Repository}");
            output.WriteLine($"    store={repository.CanonicalStorePath}");
            output.WriteLine($"    projected={repository.ProjectedEntries} body-sha256={repository.BodySha256}");

            if (repository.NoTargetReason is { Length: > 0 } reason)
            {
                output.WriteLine($"    NO TARGET -- {reason}");
            }

            foreach (var target in repository.Targets)
            {
                output.WriteLine($"    [{target.Disposition}] {target.Vendor}: {target.FilePath} ({target.Bytes} bytes)");
            }

            WriteOmissions(output, "omitted as superseded", repository.Superseded);
            WriteOmissions(output, "OVERRIDDEN by checked-in repository truth", repository.Overridden);
            WriteOmissions(output, "dropped by the projection budget", repository.Dropped);
        }

        WriteNonTargets(output, report.NonTargetRoots);
    }

    /// <summary>
    /// Every root this run discovered and did not write into, named with its reason — the same "never
    /// silently" posture <see cref="WriteOmissions"/> applies to entries, applied to roots.
    /// </summary>
    private static void WriteNonTargets(TextWriter output, IReadOnlyList<NonTargetRoot> roots)
    {
        if (roots.Count == 0)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine($"{roots.Count} discovered root(s) are NOT projection targets and were not written:");
        foreach (var root in roots)
        {
            output.WriteLine($"  [{root.Vendor}] {root.RootDirectoryPath}");
            output.WriteLine($"    {root.Reason}");
        }
    }

    /// <summary>
    /// Every omission printed by name. A count with no names is the silent drop this whole surface
    /// refuses — see <see cref="ProjectionOmission"/>.
    /// </summary>
    private static void WriteOmissions(TextWriter output, string label, IReadOnlyList<ProjectionOmission> omissions)
    {
        if (omissions.Count == 0)
        {
            return;
        }

        output.WriteLine($"    {omissions.Count} {label}:");
        foreach (var omission in omissions)
        {
            output.WriteLine($"      {omission.EntryId} {omission.SourceFileName} -- {omission.Reason}");
        }
    }

    /// <summary>
    /// The JSON contract, with the same <c>WhenWritingNull</c> posture
    /// <see cref="LedgerViewCommand"/> uses: an absent field is an absence, never a null to be read as
    /// a value.
    /// </summary>
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// What <see cref="DiscoverTargetsAsync"/> found: the write targets by repository, and every root
    /// it discovered and will not write into, with why.
    /// </summary>
    private sealed record TargetDiscovery(
        IReadOnlyDictionary<string, List<ProjectionTarget>> ByRepository,
        IReadOnlyList<NonTargetRoot> NonTargets);

    /// <summary>
    /// One discovered memory root that is not a projection target, and the reason in the operator's
    /// terms. Machine-level rather than per-repository: an archived root or a sqlite store is not a
    /// target for <b>any</b> subject, so filing it under one would suggest another subject might have
    /// written into it.
    /// </summary>
    /// <param name="RootDirectoryPath">The root that exists and was not written into.</param>
    /// <param name="Vendor">Whose root it is, in <see cref="MemoryEntry.SourceVendor"/>'s vocabulary.</param>
    /// <param name="Reason">Why it is not a target, and what (if anything) an operator does about it.</param>
    private sealed record NonTargetRoot(
        [property: JsonPropertyName("rootDirectoryPath")] string RootDirectoryPath,
        [property: JsonPropertyName("vendor")] string Vendor,
        [property: JsonPropertyName("reason")] string Reason);

    private sealed record SyncReport(
        [property: JsonPropertyName("apply")] bool Apply,
        [property: JsonPropertyName("repositoryFactsDirectory")] string? RepositoryFactsDirectory,
        [property: JsonPropertyName("repositoryFactsConsidered")] int RepositoryFactsConsidered,
        [property: JsonPropertyName("repositories")] IReadOnlyList<SyncRepositoryReport> Repositories,
        [property: JsonPropertyName("nonTargetRoots")] IReadOnlyList<NonTargetRoot> NonTargetRoots);

    private sealed record SyncRepositoryReport(
        [property: JsonPropertyName("repository")] string Repository,
        [property: JsonPropertyName("canonicalStorePath")] string CanonicalStorePath,
        [property: JsonPropertyName("bodySha256")] string BodySha256,
        [property: JsonPropertyName("projectedEntries")] int ProjectedEntries,
        [property: JsonPropertyName("targets")] IReadOnlyList<SyncTargetReport> Targets,
        [property: JsonPropertyName("noTargetReason")] string? NoTargetReason,
        [property: JsonPropertyName("superseded")] IReadOnlyList<ProjectionOmission> Superseded,
        [property: JsonPropertyName("overridden")] IReadOnlyList<ProjectionOmission> Overridden,
        [property: JsonPropertyName("dropped")] IReadOnlyList<ProjectionOmission> Dropped);

    private sealed record SyncTargetReport(
        [property: JsonPropertyName("vendor")] string Vendor,
        [property: JsonPropertyName("rootDirectoryPath")] string RootDirectoryPath,
        [property: JsonPropertyName("filePath")] string FilePath,
        [property: JsonPropertyName("disposition")] string Disposition,
        [property: JsonPropertyName("bytes")] int Bytes,
        [property: JsonPropertyName("sha256")] string Sha256);
}
