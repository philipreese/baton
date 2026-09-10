using System.Text.Json;

namespace Baton.Queue;

/// <summary>
/// One word for what a pull request's checks are doing, reduced from <c>gh pr view --json
/// statusCheckRollup</c> (#1912 slice 1). The board's PR row carries it; nothing gates on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two element shapes, measured 2026-09-07 against this repository's own PR #2035.</b> A
/// <c>CheckRun</c> carries <c>name</c>/<c>status</c>/<c>conclusion</c>; a <c>StatusContext</c> carries
/// <c>context</c>/<c>state</c>. Read in that order per element rather than assuming one shape, because
/// a repository with a classic status API integration produces the second and would otherwise reduce
/// to <see cref="None"/> — the word for "no checks are configured", which is the opposite claim.
/// </para>
/// <para>
/// <b>The newest entry per check name wins</b>, which is the non-obvious half. That same measurement
/// showed the rollup carrying BOTH a 10:17 <c>FAILURE</c> and a 13:42 <c>SUCCESS</c> for the
/// <c>diff-shape</c> check — a re-run leaves its predecessor in the array. Reducing over every element
/// would report a green PR as failing forever on the strength of a run somebody already replaced.
/// </para>
/// <para>
/// <b>Failing outranks pending outranks passing</b>, on the reading that the most actionable state is
/// the one worth a glance: a PR with one failed check and three still running is a PR someone has to
/// look at.
/// </para>
/// </remarks>
public static class PullRequestChecks
{
    /// <summary>At least one check settled unsuccessfully.</summary>
    public const string Failing = "failing";

    /// <summary>Nothing failed, and at least one check has not settled.</summary>
    public const string Pending = "pending";

    /// <summary>Every check settled successfully.</summary>
    public const string Passing = "passing";

    /// <summary>The rollup is empty — no checks are configured on this PR. <b>Not <see cref="Passing"/>:</b>
    /// "nothing ran" and "everything ran green" are different facts and a reader must not conflate them.</summary>
    public const string None = "none";

    /// <summary>
    /// Reduces a <c>statusCheckRollup</c> array. Null when <paramref name="rollup"/> is absent or is
    /// not an array — an unreadable rollup is no answer, never a fabricated one.
    /// </summary>
    public static string? Summarize(JsonElement? rollup)
    {
        if (rollup is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        // Latest entry per check name -- see the type remarks for the re-run this closes. Ordinal, not
        // ordinal-ignore-case: two GitHub checks differing only in case are two checks.
        var latest = new Dictionary<string, (DateTimeOffset At, string Verdict)>(StringComparer.Ordinal);
        var unnamed = 0;
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = Text(element, "name") ?? Text(element, "context") ?? $"unnamed-{unnamed++}";
            var at = Instant(element, "completedAt") ?? Instant(element, "startedAt") ?? DateTimeOffset.MinValue;
            var verdict = VerdictOf(element);
            if (!latest.TryGetValue(name, out var existing) || at >= existing.At)
            {
                latest[name] = (at, verdict);
            }
        }

        if (latest.Count == 0)
        {
            return None;
        }

        var verdicts = latest.Values.Select(v => v.Verdict).ToList();
        return verdicts.Contains(Failing) ? Failing
            : verdicts.Contains(Pending) ? Pending
            : Passing;
    }

    /// <summary>
    /// Reduces the JSON emitted by <c>gh pr checks --required --json bucket,...</c>. The command's
    /// exit code describes the check result, so callers deliberately parse its output even when that
    /// receipt is non-zero. Null means no trustworthy JSON evidence; an empty array means
    /// <see cref="None"/>, not passing.
    /// </summary>
    public static string? TrySummarizeRequired(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var verdicts = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var bucket = Text(element, "bucket");
                if (bucket is null)
                {
                    return null;
                }

                verdicts.Add(bucket.ToUpperInvariant() switch
                {
                    "PASS" or "SKIPPING" => Passing,
                    "PENDING" => Pending,
                    _ => Failing,
                });
            }

            return verdicts.Count == 0 ? None
                : verdicts.Contains(Failing) ? Failing
                : verdicts.Contains(Pending) ? Pending
                : Passing;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One element's verdict. <c>conclusion</c> is the settled answer and is null while a check run is
    /// still going, so its absence means <see cref="Pending"/> rather than success; a
    /// <c>StatusContext</c>'s <c>state</c> carries both meanings in one field.
    /// </summary>
    private static string VerdictOf(JsonElement element)
    {
        var word = Text(element, "conclusion") ?? Text(element, "state");
        if (word is null)
        {
            return Pending;
        }

        return word.ToUpperInvariant() switch
        {
            "SUCCESS" or "NEUTRAL" or "SKIPPED" => Passing,
            "PENDING" or "QUEUED" or "IN_PROGRESS" or "WAITING" or "REQUESTED" or "EXPECTED" => Pending,

            // FAILURE, ERROR, TIMED_OUT, CANCELLED, ACTION_REQUIRED, STARTUP_FAILURE, STALE, and
            // anything GitHub adds later. Fails toward "look at this": a word this code does not know
            // is not evidence of green.
            _ => Failing,
        };
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? Instant(JsonElement element, string property) =>
        Text(element, property) is { Length: > 0 } text
        && DateTimeOffset.TryParse(text, out var parsed)
            ? parsed
            : null;
}
