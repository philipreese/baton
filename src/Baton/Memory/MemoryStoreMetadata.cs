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
    /// Reads metadata for inventory and recovery. A malformed or inaccessible file is reported and
    /// treated as absent so a legacy non-empty entries file can still supply its row identity.
    /// </summary>
    public static MemoryStoreMetadata? TryRead(string repositorySlug)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositorySlug);
        var path = BatonPaths.MemoryStoreMetadataFile(repositorySlug);
        try
        {
            return MutexGuardedFileLock.RunUnderLock(
                path, LockNamePrefix, LockTimeout, () => ReadUnlocked(path));
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or WaitHandleCannotBeOpenedException
                                   or JsonException
                                   or InvalidDataException)
        {
            Console.Error.WriteLine($"Could not read memory store metadata at '{path}': {ex.Message}.");
            return null;
        }
    }

    private static MemoryStoreMetadata? ReadUnlocked(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var metadata = JsonSerializer.Deserialize<MemoryStoreMetadata>(File.ReadAllText(path, Encoding.UTF8), Json)
            ?? throw new InvalidDataException($"Memory store metadata '{path}' contains JSON null.");
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
