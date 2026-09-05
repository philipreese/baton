using System.Globalization;
using System.Text.Json;
using Baton.Accounting;

namespace Baton.Cli;

/// <summary>
/// Turns one element of <c>gh pr list --json …</c>'s array into a <see cref="MergedPullRequest"/>
/// (#1901 C2). Lives in the CLI because <c>gh</c>'s envelope is a vendor-tool detail the engine layer
/// does not model — <see cref="MergedPullRequest"/> is the value that crosses the boundary, the same
/// split <see cref="WorkspaceDeliveryProbe"/>/<c>WorkspaceDelivery</c> already keeps.
/// </summary>
/// <remarks>
/// <b>Every field is independently optional except the number, and nothing is inferred.</b> A field
/// <c>gh</c> did not report, or reported in a shape this does not recognise, is absent on the row
/// rather than defaulted to zero — a PR with no <c>reviews</c> array is not a PR nobody reviewed.
/// A missing or non-numeric <c>number</c> yields <see langword="null"/> for the whole element, because
/// the number is the row's dedupe key — <see cref="CostLedgerStore.GithubBackfillExecutionId"/>'s own
/// doc states what a row without one costs, and it is why this refuses rather than synthesising one.
/// </remarks>
public static class MergedPullRequestReader
{
    public static MergedPullRequest? TryRead(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("number", out var number)
            || number.ValueKind != JsonValueKind.Number
            || !number.TryGetInt32(out var pullRequestNumber))
        {
            return null;
        }

        return new MergedPullRequest(
            Number: pullRequestNumber,
            HeadRefName: Text(element, "headRefName"),
            MergedAt: Instant(element, "mergedAt"),
            FilesChanged: Int32(element, "changedFiles"),
            Additions: Int64(element, "additions"),
            Deletions: Int64(element, "deletions"),
            Commits: ArrayLength(element, "commits"),
            ReviewCount: ArrayLength(element, "reviews"),
            Issue: FirstClosedIssue(element));
    }

    /// <summary>
    /// The first issue GitHub says this PR closes, as a bare decimal — the same spelling
    /// <see cref="CostLedgerEntry.Issue"/> records at settle, so a reading joins across both writers
    /// without normalising. <b>The first, not all of them</b>: the row holds one issue, and a PR
    /// closing several is a shape this ledger does not try to represent — same rule
    /// <see cref="WorkspaceDeliveryProbe"/> applies to a branch with several open PRs.
    /// </summary>
    private static string? FirstClosedIssue(JsonElement element)
    {
        if (!element.TryGetProperty("closingIssuesReferences", out var references)
            || references.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var reference in references.EnumerateArray())
        {
            if (reference.ValueKind == JsonValueKind.Object
                && reference.TryGetProperty("number", out var issueNumber)
                && issueNumber.ValueKind == JsonValueKind.Number
                && issueNumber.TryGetInt64(out var parsed))
            {
                return parsed.ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text : null
            : null;

    /// <summary>
    /// A <c>gh</c> timestamp, normalised to UTC the way every instant this ledger writes is
    /// (<c>LedgerQuery.ToUtc</c>'s own rule). <c>gh</c> emits RFC 3339 with an explicit offset, so this
    /// never has to guess a zone; an unparseable value is absent rather than "now".
    /// </summary>
    private static DateTime? Instant(JsonElement element, string name) =>
        Text(element, name) is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset)
                ? offset.UtcDateTime
                : null;

    private static int? Int32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
                ? parsed
                : null;

    private static long? Int64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed)
                ? parsed
                : null;

    /// <summary>
    /// How many entries an array-valued field holds — which is how <c>gh</c> reports both the commit
    /// count and the review count (it returns the objects, not a tally). Absent, never <c>0</c>, when
    /// the field is missing or is not an array.
    /// </summary>
    private static int? ArrayLength(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : null;
}
