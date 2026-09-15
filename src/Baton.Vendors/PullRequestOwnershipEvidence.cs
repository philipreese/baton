using System.Text.RegularExpressions;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Baton.Status;

namespace Baton.Vendors;

/// <summary>
/// Immutable proof that this execution owns one pull request in one repository. Producers must
/// establish both values from trusted provenance; a pull-request number alone is never evidence.
/// </summary>
public sealed record PullRequestOwnershipEvidence
{
    private static readonly Regex PullRequestUrl = new(
        @"https://github\.com/(?<owner>[\w.-]+)/(?<repo>[\w.-]+)/pull/(?<number>\d+)/?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private PullRequestOwnershipEvidence(string repository, int number)
    {
        Repository = repository;
        Number = number;
    }

    public string Repository { get; }

    public int Number { get; }

    /// <summary>
    /// Builds evidence from an already-verified repository/number pair. This is the compatible
    /// producer seam for later lineage work; callers, rather than this value, prove provenance.
    /// </summary>
    public static PullRequestOwnershipEvidence? FromVerified(string? repository, int number) =>
        GitHubRepository.TryCanonicalize(repository) is { } canonical && number > 0
            ? new PullRequestOwnershipEvidence(canonical, number)
            : null;

    /// <summary>
    /// Reads output attributed to one successful direct <c>gh pr create</c>. Every pull-request URL
    /// in the output must agree on repository and number; mixed or foreign output fails closed.
    /// </summary>
    internal static PullRequestOwnershipEvidence? FromAttributedCreateOutput(
        string expectedRepository, string? output)
    {
        var canonicalExpected = GitHubRepository.TryCanonicalize(expectedRepository);
        if (canonicalExpected is null || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        PullRequestOwnershipEvidence? evidence = null;
        foreach (Match match in PullRequestUrl.Matches(output))
        {
            var repository = GitHubRepository.TryCanonicalize(
                $"{match.Groups["owner"].Value}/{match.Groups["repo"].Value}");
            if (repository != canonicalExpected
                || !int.TryParse(match.Groups["number"].Value, out var number)
                || number <= 0)
            {
                return null;
            }

            evidence ??= new PullRequestOwnershipEvidence(repository, number);
            if (evidence.Number != number || evidence.Repository != repository)
            {
                return null;
            }
        }

        return evidence;
    }
}

/// <summary>
/// Conductor-authored ownership of the pull request a follow-on room continues. This is binding
/// metadata, not worker input: the conductor verifies repository, branch, state, and launch head
/// before writing it. Created and continued PRs reduce to the same enforcement evidence.
/// </summary>
public sealed record OriginatingPullRequestOwnership(
    string Repository,
    int Number,
    string HeadBranch,
    string LaunchHead)
{
    public PullRequestOwnershipEvidence? ToEvidence() =>
        string.IsNullOrWhiteSpace(HeadBranch) || string.IsNullOrWhiteSpace(LaunchHead)
            ? null
            : PullRequestOwnershipEvidence.FromVerified(Repository, Number);

    public static PullRequestOwnershipEvidence? FromHookValue(string? value)
    {
        var separator = value?.LastIndexOf('#') ?? -1;
        return separator > 0 && int.TryParse(value![(separator + 1)..], out var number)
            ? PullRequestOwnershipEvidence.FromVerified(value[..separator], number)
            : null;
    }

}

/// <summary>
/// Baton-owned authority for originating-PR grants. The binding is the worker-facing projection;
/// this record lives under <see cref="BatonPaths.Root"/>, outside the room and workspace, so editing
/// room files cannot mint or alter the authority consumed at launch. This follows the same trust
/// boundary as Baton's project ceiling: roles are cooperative processes rather than an OS sandbox,
/// but worker-authored room state is never itself an authority source.
/// </summary>
public static class OriginatingPullRequestAuthorityStore
{
    private const string DirectoryName = "originating-pr-authority";
    private sealed record AuthorityRecord(string RoomDirectory, OriginatingPullRequestOwnership Ownership);

    public static OriginatingPullRequestOwnership? Read(string? roomDirectory)
    {
        var identity = RoomIdentity(roomDirectory);
        if (identity is null) return null;
        try
        {
            var path = RecordPath(identity);
            var recorded = File.Exists(path)
                ? JsonSerializer.Deserialize<AuthorityRecord>(File.ReadAllText(path))
                : null;
            return recorded is not null
                && BatonPaths.RecordKeyComparer.Equals(recorded.RoomDirectory, identity)
                    ? recorded.Ownership
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static async Task WriteAsync(
        OriginatingPullRequestOwnership? ownership, string roomDirectory, CancellationToken cancellationToken)
    {
        var identity = RoomIdentity(roomDirectory)
            ?? throw new ArgumentException("The room directory must be an absolute path.", nameof(roomDirectory));
        var path = RecordPath(identity);
        if (ownership is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary, JsonSerializer.Serialize(new AuthorityRecord(identity, ownership)), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string? RoomIdentity(string? roomDirectory)
    {
        if (string.IsNullOrWhiteSpace(roomDirectory) || !Path.IsPathFullyQualified(roomDirectory)) return null;
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
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(roomIdentity))).ToLowerInvariant();
        return Path.Combine(BatonPaths.Root, DirectoryName, $"{digest}.json");
    }
}

internal static class GitHubRepository
{
    public static string? TryCanonicalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var raw = value.Trim().Replace('\\', '/');
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            raw = uri.AbsolutePath.Trim('/');
        }
        else if (raw.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw["github.com/".Length..];
        }

        if (raw.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[..^4];
        }

        var parts = raw.Trim('/').Split('/');
        return parts.Length == 2
            && parts.All(part => part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
                ? $"{parts[0]}/{parts[1]}".ToLowerInvariant()
                : null;
    }
}
