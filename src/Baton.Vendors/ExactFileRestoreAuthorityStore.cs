using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baton.Status;

namespace Baton.Vendors;

/// <summary>
/// Baton-owned proof that a room binding's exact-file base was captured by a conductor boundary.
/// A bindings.json value alone is configuration, not authority.
/// </summary>
public static class ExactFileRestoreAuthorityStore
{
    private const string DirectoryName = "exact-file-restore-authority";
    private sealed record AuthorityRecord(
        string RoomDirectory,
        IReadOnlyDictionary<string, string> BaseShaByWorker);

    public static string? Read(string? roomDirectory, string workerName)
    {
        var identity = RoomIdentity(roomDirectory);
        if (identity is null || string.IsNullOrWhiteSpace(workerName))
        {
            return null;
        }

        try
        {
            var path = RecordPath(identity);
            var recorded = File.Exists(path)
                ? JsonSerializer.Deserialize<AuthorityRecord>(File.ReadAllText(path))
                : null;
            return recorded is not null
                && BatonPaths.RecordKeyComparer.Equals(recorded.RoomDirectory, identity)
                && recorded.BaseShaByWorker.TryGetValue(workerName, out var baseSha)
                    ? baseSha
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static async Task WriteAsync(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings,
        string roomDirectory,
        CancellationToken cancellationToken)
    {
        var identity = RoomIdentity(roomDirectory)
            ?? throw new ArgumentException("The room directory must be an absolute path.", nameof(roomDirectory));
        var baseShas = bindings
            .Where(pair => pair.Value.PermissionGrant?.ExactFileRestore == true
                && pair.Value.ExactFileRestoreBaseSha is { Length: > 0 })
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ExactFileRestoreBaseSha!,
                StringComparer.Ordinal);
        var path = RecordPath(identity);
        if (baseShas.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(new AuthorityRecord(identity, baseShas)),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string? RoomIdentity(string? roomDirectory)
    {
        if (string.IsNullOrWhiteSpace(roomDirectory) || !Path.IsPathFullyQualified(roomDirectory))
        {
            return null;
        }

        try
        {
            return BatonPaths.RecordKey(Path.GetFullPath(roomDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static string RecordPath(string roomIdentity)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(roomIdentity)))
            .ToLowerInvariant();
        return Path.Combine(BatonPaths.Root, DirectoryName, $"{digest}.json");
    }
}
