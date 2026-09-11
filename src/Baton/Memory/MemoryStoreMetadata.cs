using System.Text;
using System.Text.Json;
using Baton.Status;

namespace Baton.Memory;

/// <summary>
/// The durable identity of one canonical memory store. It is deliberately independent of entry rows
/// and projection obligations so an empty, fully projected store remains discoverable after restart.
/// </summary>
public sealed record MemoryStoreMetadata(int Version, string Repository, string RepositorySlug)
{
    public const int CurrentVersion = 1;
}

/// <summary>Owns the immutable <c>store.json</c> identity metadata for canonical memory stores.</summary>
public static class MemoryStoreMetadataStore
{
    private const string LockNamePrefix = "baton-memory-store-metadata";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Creates the metadata before a canonical mutation. Repeated calls for the same identity write
    /// nothing; a conflicting identity is refused rather than silently relabelling an existing store.
    /// </summary>
    public static Task EnsureAsync(
        string repository,
        string repositorySlug,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentException.ThrowIfNullOrEmpty(repositorySlug);
        if (!string.Equals(FleetMemory.SlugFor(repository), repositorySlug, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Repository '{repository}' does not belong to memory store slug '{repositorySlug}'.",
                nameof(repositorySlug));
        }

        var path = BatonPaths.MemoryStoreMetadataFile(repositorySlug);
        return Task.Run(
            () => MutexGuardedFileLock.RunUnderLock(
                path,
                LockNamePrefix,
                LockTimeout,
                () =>
                {
                    var existing = ReadUnlocked(path);
                    if (existing is not null)
                    {
                        if (!string.Equals(existing.Repository, repository, StringComparison.OrdinalIgnoreCase)
                            || !string.Equals(existing.RepositorySlug, repositorySlug, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                $"Memory store metadata '{path}' names '{existing.Repository}' / " +
                                $"'{existing.RepositorySlug}', not '{repository}' / '{repositorySlug}'.");
                        }

                        return;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var metadata = new MemoryStoreMetadata(
                        MemoryStoreMetadata.CurrentVersion, repository, repositorySlug);
                    var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
                    try
                    {
                        File.WriteAllText(
                            tempPath,
                            JsonSerializer.Serialize(metadata, Json) + "\n",
                            new UTF8Encoding(false));
                        File.Move(tempPath, path);
                    }
                    finally
                    {
                        File.Delete(tempPath);
                    }
                }),
            cancellationToken);
    }

    /// <summary>
    /// Reads metadata for inventory and recovery. Only a genuinely absent file identifies a legacy
    /// store; malformed, mismatched and inaccessible metadata fail closed so rows cannot relabel it.
    /// </summary>
    public static MemoryStoreMetadata? ReadIfPresent(string repositorySlug)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositorySlug);
        var path = BatonPaths.MemoryStoreMetadataFile(repositorySlug);
        return MutexGuardedFileLock.RunUnderLock(
            path, LockNamePrefix, LockTimeout, () => ReadUnlocked(path));
    }

    private static MemoryStoreMetadata? ReadUnlocked(string path)
    {
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        MemoryStoreMetadata metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<MemoryStoreMetadata>(text, Json)
                ?? throw new InvalidDataException($"Memory store metadata '{path}' contains JSON null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Memory store metadata '{path}' is malformed.", ex);
        }
        if (metadata.Version != MemoryStoreMetadata.CurrentVersion
            || metadata.Repository is not { Length: > 0 }
            || metadata.RepositorySlug is not { Length: > 0 }
            || !string.Equals(metadata.RepositorySlug, Path.GetFileName(
                Path.GetDirectoryName(Path.GetDirectoryName(path))!), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                FleetMemory.SlugFor(metadata.Repository), metadata.RepositorySlug, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Memory store metadata '{path}' has an unsupported or incomplete shape.");
        }

        return metadata;
    }
}

/// <summary>Resolves and validates the identity provenance of one canonical store.</summary>
public static class MemoryStoreIdentity
{
    /// <summary>
    /// Returns the store identity in provenance order: durable metadata, legacy rows, legacy
    /// obligation, then an explicitly selected legacy slug. Every supplied source must agree, and
    /// every identity must derive the directory slug; disagreement is corrupt state, never fallback.
    /// </summary>
    public static string? Resolve(
        string repositorySlug,
        string? metadataRepository,
        IReadOnlyList<MemoryEntry> entries,
        MemoryProjectionObligation? obligation = null,
        string? selectedLegacyRepository = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositorySlug);
        ArgumentNullException.ThrowIfNull(entries);

        var repository = metadataRepository
            ?? entries.FirstOrDefault()?.Repository
            ?? obligation?.Repository
            ?? selectedLegacyRepository
            ?? (FleetMemory.IsFleet(repositorySlug) ? FleetMemory.Slug : null);

        if (repository is null)
        {
            return null;
        }

        ValidateSource(repositorySlug, repository, "resolved store identity");

        if (metadataRepository is { Length: > 0 })
        {
            RequireMatch(repository, metadataRepository, "store metadata");
        }

        foreach (var entry in entries)
        {
            RequireMatch(repository, entry.Repository, $"entry '{entry.Id}'");
            ValidateSource(repositorySlug, entry.Repository, $"entry '{entry.Id}'");
        }

        if (obligation is not null)
        {
            if (!string.Equals(obligation.RepositorySlug, repositorySlug, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Projection obligation '{obligation.AttemptId}' names store slug " +
                    $"'{obligation.RepositorySlug}', not '{repositorySlug}'.");
            }

            RequireMatch(repository, obligation.Repository, $"projection obligation '{obligation.AttemptId}'");
            ValidateSource(repositorySlug, obligation.Repository, $"projection obligation '{obligation.AttemptId}'");
        }

        if (selectedLegacyRepository is { Length: > 0 })
        {
            RequireMatch(repository, selectedLegacyRepository, "selected repository");
        }

        return repository;
    }

    private static void RequireMatch(string repository, string candidate, string source)
    {
        if (!string.Equals(repository, candidate, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Canonical memory store identity '{repository}' conflicts with {source} identity '{candidate}'.");
        }
    }

    private static void ValidateSource(string repositorySlug, string repository, string source)
    {
        if (repository is not { Length: > 0 }
            || !string.Equals(FleetMemory.SlugFor(repository), repositorySlug, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Canonical memory store slug '{repositorySlug}' does not belong to {source} '{repository}'.");
        }
    }
}
