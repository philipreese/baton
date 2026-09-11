using System.Text.RegularExpressions;

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
