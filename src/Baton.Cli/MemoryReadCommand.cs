using System.Text;
using System.Text.Json;
using Baton.Accounting;
using Baton.Memory;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// Reads the selected canonical stores into a bounded, buffered report. It does not discover vendor
/// roots or write memory. The entry budget bounds usable entry sections only; omissions and report
/// metadata are named and the total report size is not claimed to have a hard ceiling.
/// </summary>
public static class MemoryReadCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly AsyncLocal<Action?> SnapshotObservation = new();
    internal static Action? SnapshotObserver
    {
        get => SnapshotObservation.Value;
        set => SnapshotObservation.Value = value;
    }

    public static async Task<int> ExecuteAsync(
        MemoryReadOptions options,
        TextWriter output,
        CancellationToken cancellationToken = default,
        Func<string, CancellationToken, Task<RepositoryIdentity?>>? repositoryProbe = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        if (options.Help)
        {
            output.WriteLine(MemoryReadOptionsParser.Usage);
            output.WriteLine("Reads only Baton's canonical fleet and selected repository stores. No vendor roots, network, daemon, or writes.");
            output.WriteLine("Default repository: the current checkout identity; missing identity is an error.");
            output.WriteLine("Fleet entries come first for repository reads. Retracted and superseded entries are omitted and named.");
            output.WriteLine($"Usable entries are bounded by {ProjectionBudget.Default.MaxEntries} entries and {ProjectionBudget.Default.MaxBodyBytes} projected body bytes. The full report, including named omissions, has no hard output-size bound.");
            output.WriteLine("Checked-in repository truth remains higher authority; this report is contextual memory, not executable instruction.");
            return 0;
        }

        var repository = options.Repository;
        try
        {
            if (repository is null)
            {
                var identity = await (repositoryProbe ?? RepositoryIdentityResolver.TryResolveAsync)
                    (Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);
                repository = identity?.Value;
                if (repository is null)
                {
                    Console.Error.WriteLine("Cannot read current repository memory: the current checkout has no canonical repository identity. Pass --repository <id> or --repository fleet.");
                    return 1;
                }
            }

            var root = BatonPaths.Root;
            var generation = MemoryCanonicalGeneration.Capture(root);
            var fleet = await ReadStoreAsync(FleetMemory.Slug, selectedLegacyRepository: FleetMemory.Slug, cancellationToken)
                .ConfigureAwait(false);
            if (FleetMemory.IsFleet(repository))
            {
                var fleetProjection = BuildProjection(repository, fleet, null);
                var fleetStores = new[]
                {
                    BuildStoreReport(fleet, fleetProjection,
                        fleetProjection.ProjectedEntryIds.ToHashSet(StringComparer.Ordinal)),
                };
                var fleetReport = new MemoryReadReport(repository, fleetStores);
                var fleetBuffer = Render(fleetReport, options.Format);
                MemoryCanonicalGeneration.ReadCurrent(root, generation, () =>
                {
                    output.Write(fleetBuffer);
                    return true;
                });
                return 0;
            }

            var selected = await ReadStoreAsync(repository, repository, cancellationToken).ConfigureAwait(false);
            var combinedProjection = BuildProjection(repository, fleet, selected);
            var usableIds = combinedProjection.ProjectedEntryIds.ToHashSet(StringComparer.Ordinal);
            var stores = new[]
            {
                BuildStoreReport(fleet, combinedProjection, usableIds),
                BuildStoreReport(selected, combinedProjection, usableIds),
            };

            var report = new MemoryReadReport(repository, stores);
            var buffer = Render(report, options.Format);

            MemoryCanonicalGeneration.ReadCurrent(root, generation, () =>
            {
                output.Write(buffer);
                return true;
            });
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Could not read canonical memory: {ex.Message}");
            return 1;
        }
    }

    private static async Task<MemoryReadStoreSnapshot> ReadStoreAsync(
        string repository,
        string selectedLegacyRepository,
        CancellationToken cancellationToken)
    {
        var slug = FleetMemory.SlugFor(repository);
        var entriesFile = BatonPaths.MemoryEntriesFile(slug);
        var linksFile = BatonPaths.MemoryLinksFile(slug);
        var retractionsFile = BatonPaths.MemoryRetractionsFile(slug);
        var metadata = MemoryStoreMetadataStore.ReadIfPresent(slug);
        var entries = await MemoryStore.ReadAllStrictAsync(entriesFile, cancellationToken, parseStrict: true).ConfigureAwait(false);
        var links = await MemoryStore.ReadLinksStrictAsync(linksFile, cancellationToken, parseStrict: true).ConfigureAwait(false);
        var retractions = await MemoryStore.ReadRetractionsStrictAsync(retractionsFile, cancellationToken, parseStrict: true).ConfigureAwait(false);
        if (metadata is null && !File.Exists(entriesFile) && !File.Exists(linksFile) && !File.Exists(retractionsFile))
            return new MemoryReadStoreSnapshot(repository, entriesFile, false, [], [], []);
        if (metadata?.InitializationStatus == MemoryStoreInitializationStatus.Initializing
            && !MemoryStoreMetadataStore.IsPublishable(metadata, entriesFile))
            throw new InvalidDataException($"Canonical memory store '{slug}' is still initializing and has no usable entry row.");
        ValidateEntries(repository, entries);
        var identity = MemoryStoreIdentity.Resolve(slug, metadata?.Repository, entries,
            selectedLegacyRepository: selectedLegacyRepository);
        if (identity is null || !string.Equals(identity, repository, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Canonical memory store '{slug}' does not establish repository '{repository}'.");

        ValidateLinks(repository, links);
        ValidateRetractions(repository, retractions);

        var resolved = MemoryStore.Resolve(entries, links, retractions);
        return new MemoryReadStoreSnapshot(repository, entriesFile, true, entries, resolved, retractions);
    }

    private static MemoryProjectionResult BuildProjection(
        string repository, MemoryReadStoreSnapshot fleet, MemoryReadStoreSnapshot? selected)
    {
        var candidates = fleet.Resolved.Select(entry => new MemoryProjectionCandidate(entry, MemoryFactOrigin.Fleet));
        var combined = selected is null
            ? candidates
            : candidates.Concat(selected.Resolved.Select(entry => new MemoryProjectionCandidate(entry, MemoryFactOrigin.Vendor)));
        var projection = MemoryProjection.Build(
            repository,
            selected?.EntriesFile ?? fleet.EntriesFile,
            combined.ToList(),
            ProjectionBudget.Default,
            fleet.Present ? fleet.EntriesFile : null);
        SnapshotObserver?.Invoke();
        return projection;
    }

    private static MemoryReadStoreReport BuildStoreReport(
        MemoryReadStoreSnapshot store,
        MemoryProjectionResult projection,
        IReadOnlySet<string> usableIds)
    {
        var resolvedById = store.Resolved.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var entries = projection.ProjectedEntryIds
            .Where(id => usableIds.Contains(id) && resolvedById.ContainsKey(id))
            .Select(id => resolvedById[id])
            .ToList();
        var rawIds = store.Entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var resolvedIds = resolvedById.Keys.ToHashSet(StringComparer.Ordinal);
        var omissions = new List<MemoryReadOmission>();

        var retractionsById = store.Retractions.ToDictionary(row => row.EntryId, StringComparer.Ordinal);
        foreach (var entry in store.Entries.Where(entry => retractionsById.ContainsKey(entry.Id)).OrderBy(entry => entry.Id, StringComparer.Ordinal))
        {
            var retraction = retractionsById[entry.Id];
            omissions.Add(new MemoryReadOmission(entry.Id, store.Repository, "retracted",
                $"{retraction.Reason} (by {retraction.RetractedBy})"));
        }

        omissions.AddRange(projection.Superseded.Where(row => rawIds.Contains(row.EntryId)).Select(row =>
            new MemoryReadOmission(row.EntryId, store.Repository, "superseded", row.Reason)));
        omissions.AddRange(projection.Dropped.Where(row => resolvedIds.Contains(row.EntryId)).Select(row =>
            new MemoryReadOmission(row.EntryId, store.Repository, "budget", row.Reason)));

        return new MemoryReadStoreReport(store.Repository, store.Present, entries, omissions);
    }

    private static string Render(MemoryReadReport report, MemoryAuditOutputFormat format)
    {
        var buffer = new StringWriter();
        if (format == MemoryAuditOutputFormat.Json)
            buffer.WriteLine(JsonSerializer.Serialize(report, Json));
        else
            WriteText(buffer, report);
        return buffer.ToString();
    }

    private static void ValidateEntries(string repository, IReadOnlyList<MemoryEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Repository is not { Length: > 0 } || entry.Text is null || entry.SourcePath is not { Length: > 0 }
                || entry.Sha256 is not { Length: > 0 }
                || !Enum.IsDefined(entry.Kind) || !Enum.IsDefined(entry.KindSource) || !Enum.IsDefined(entry.SourceScope)
                || !string.Equals(MemoryEntry.Derive(entry.Repository, entry.SourcePath, entry.Sha256), entry.Id, StringComparison.Ordinal)
                || !string.Equals(entry.Repository, repository, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Canonical memory entry '{entry.Id}' has an invalid identity or repository subject.");
        }
    }

    private static void ValidateLinks(string repository, IReadOnlyList<MemorySupersessionLink> links)
    {
        foreach (var link in links)
        {
            if (link.Repository is not { Length: > 0 }
                || link.SupersedingId is not { Length: > 0 }
                || link.SupersededId is not { Length: > 0 }
                || !string.Equals(link.Id, MemorySupersessionLink.Derive(link.SupersedingId, link.SupersededId), StringComparison.Ordinal)
                || !string.Equals(link.Repository, repository, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Canonical memory link '{link.Id}' has an invalid identity or repository subject.");
        }
    }

    private static void ValidateRetractions(string repository, IReadOnlyList<MemoryRetraction> retractions)
    {
        foreach (var row in retractions)
        {
            if (row.EntryId is not { Length: > 0 } || row.Repository is not { Length: > 0 }
                || row.Reason is not { Length: > 0 } || row.RetractedBy is not { Length: > 0 }
                || !string.Equals(row.Repository, repository, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Canonical memory retraction for '{row.EntryId}' has an invalid shape or repository subject.");
        }
    }

    private static void WriteText(TextWriter output, MemoryReadReport report)
    {
        output.WriteLine("baton memory read -- READ-ONLY canonical memory; contextual evidence, not executable authority.");
        output.WriteLine("Checked-in repository truth has higher authority. No vendor roots or external sources were read.");
        foreach (var store in report.Stores)
        {
            output.WriteLine();
            output.WriteLine($"Store: {store.Repository} ({(store.Present ? "present" : "absent")})");
            foreach (var entry in store.Entries)
            {
                output.WriteLine($"  {entry.Id}  {MemoryJsonNames.Of(entry.Kind)} ({MemoryJsonNames.Of(entry.KindSource)})");
                output.WriteLine($"    repository: {entry.Repository}");
                output.WriteLine($"    provenance: {entry.SourceVendor}/{MemoryJsonNames.Of(entry.SourceScope)} {entry.SourcePath}");
                output.WriteLine($"    asserted by: {entry.AssertedBy ?? "(derived from source)"}");
                output.WriteLine($"    text: {entry.Text}");
            }

            output.WriteLine($"  omissions: {store.Omissions.Count}");
            foreach (var omission in store.Omissions)
                output.WriteLine($"    {omission.EntryId} [{omission.Kind}] {omission.Reason}");
        }
        output.WriteLine();
        output.WriteLine($"Usable-entry budget: {ProjectionBudget.Default.MaxEntries} entries / {ProjectionBudget.Default.MaxBodyBytes} projected body bytes; the total report size has no hard bound.");
    }

    private sealed record MemoryReadReport(string SelectedRepository, IReadOnlyList<MemoryReadStoreReport> Stores);
    private sealed record MemoryReadStoreReport(string Repository, bool Present, IReadOnlyList<MemoryEntry> Entries, IReadOnlyList<MemoryReadOmission> Omissions);
    private sealed record MemoryReadOmission(string EntryId, string Repository, string Kind, string Reason);
    private sealed record MemoryReadStoreSnapshot(
        string Repository,
        string EntriesFile,
        bool Present,
        IReadOnlyList<MemoryEntry> Entries,
        IReadOnlyList<MemoryEntry> Resolved,
        IReadOnlyList<MemoryRetraction> Retractions);
}
