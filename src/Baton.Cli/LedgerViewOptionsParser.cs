using System.Globalization;
using System.Text.Json;
using Baton.Accounting;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// Parses the reading form of <c>baton ledger</c> (#1849 phase B). Same shape as
/// <see cref="StatusOptionsParser"/> — one <see cref="CliArgumentException"/> per malformed
/// invocation, never a bare <see cref="InvalidOperationException"/>, and the positional argument is
/// resolved at the boundary rather than deeper in.
/// </summary>
public static class LedgerViewOptionsParser
{
    public const string Usage =
        "Usage: baton ledger [<room-dir>] [--since <instant>] [--until <instant>] [--vendor <name>] " +
        "[--model <id>] [--role <name>] [--project <repo-identity>] [--outcome <token>] [--workflow <id>] " +
        "[--pr <n>] [--issue <n>] [--source-kind <kind>] [--resolution none|any|accept-capture|reject|close] " +
        "[--repo-identity <key>] [--format text|json|csv] [--drill] [--help]";

    /// <summary>
    /// What <c>--help</c> prints under <see cref="Usage"/>. Every line here is a place a reader's prior
    /// fills the gap wrongly if the negative is not stated (CLAUDE.md, "Writing documentation"): which
    /// of the two ledgers this reads, which instant the window is on and which end is open, what a
    /// local-date shorthand means, and which facet does nothing useful yet.
    /// </summary>
    public static IReadOnlyList<string> HelpLines { get; } =
    [
        "",
        "Reads the repository-keyed COST ledger (~/.baton/ledger/<repository>.jsonl): one row per settled",
        "execution attempt, with token dimensions and two labelled estimates. Both estimates are",
        "API-equivalent/plan-meter ESTIMATES -- never an invoice, never subscription spend, never a quota",
        "reading. 'baton ledger --rebuild' is a different command against a DIFFERENT file (the",
        "per-execution burn ledger, ~/.baton/quota-ledger.jsonl); it does not touch this one.",
        "'baton ledger backfill' writes rows into this file; 'baton ledger export' publishes it to a",
        "repository directory without writing it. Both have their own --help.",
        "",
        "  <room-dir>          That room's attempts and its total. Identical to the fleet view filtered to",
        "                      the room -- one projection, no second arithmetic.",
        "  --since/--until     Filter on each attempt's endedAt. --since is INCLUSIVE, --until is EXCLUSIVE,",
        "                      so two adjacent windows partition a range instead of double-counting its",
        "                      boundary row. Both accept ISO-8601 ('2026-09-04T14:00:00Z', or with no",
        "                      offset = this machine's local time) and the '2026-09-04' shorthand, which",
        "                      means local midnight. An attempt with no recorded endedAt is EXCLUDED by any",
        "                      window and counted as 'undatedExcluded', never assumed into it.",
        "  --project           Matches a row's repository identity. One ledger file holds one repository",
        "                      today, so this matches all rows or none; it is here for the cross-repository",
        "                      reads phase C and #1848 bring.",
        "  --source-kind       baton-execution | github-backfill | claude-code-session | codex-session |",
        "                      antigravity-session. Two have writers: baton-execution (a settle, or",
        "                      'baton ledger backfill' recovering one from a room still on disk) and",
        "                      github-backfill (a merged PR that backfill reconstructed). A",
        "                      github-backfill row carries NO token dimension and no estimate -- nothing",
        "                      ran -- so '--source-kind baton-execution' is the spend reading.",
        "  --resolution        A `baton resolve` appends a CORRECTING row rather than rewriting the row it",
        "                      corrects, so a conductor's intervention is one more row in every reading --",
        "                      counted in 'attempts' and in 'unread', carrying the corrected row's role,",
        "                      outcome, issue and pr. 'none' is execution attempts alone (the reading that",
        "                      excludes interventions); 'any' is the interventions alone; accept-capture |",
        "                      reject | close is one kind of them. Unset counts both, which is the file.",
        "  --repo-identity     Read another repository's ledger: its canonical identity",
        "                      ('github.com/owner/repo') or the ledger file's own stem.",
        "  --format json       One object: {query, vendors, total, rows?}. Field names are the ledger",
        "                      record's own. --format csv writes every matching row and no subtotals,",
        "                      LF-terminated, with or without --drill.",
        "  --format csv        REDACTED, and it is the only format that is (#1901 C3): room and parentRoom",
        "                      are reduced to their basename, and a cell that still looks like a filesystem",
        "                      path or carries this machine's OS account name refuses the write outright.",
        "                      CSV is the format that leaves the machine -- 'baton ledger export' commits",
        "                      it to a public repository -- while json and text stay local and unredacted.",
        "  --drill             Include the contributing rows in the text and JSON views, always after",
        "                      the subtotals they roll into.",
    ];

    public static LedgerViewOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? roomDirectoryPath = null;
        string? repositoryIdentityKey = null;
        var format = LedgerOutputFormat.Text;
        var drill = false;
        var help = false;
        var query = new LedgerQuery();

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                    help = true;
                    i++;
                    break;
                case "--drill":
                    drill = true;
                    i++;
                    break;
                case "--since":
                    query = query with { Since = ParseInstant(RequireValue(args, i), "--since") };
                    i += 2;
                    break;
                case "--until":
                    query = query with { Until = ParseInstant(RequireValue(args, i), "--until") };
                    i += 2;
                    break;
                case "--vendor":
                    query = query with { Vendor = RequireValue(args, i) };
                    i += 2;
                    break;
                case "--model":
                    query = query with { Model = RequireValue(args, i) };
                    i += 2;
                    break;
                case "--role":
                    query = query with { Role = RequireValue(args, i) };
                    i += 2;
                    break;
                case "--project":
                    query = query with { Project = RequireValue(args, i) };
                    i += 2;
                    break;
                case "--outcome":
                    query = query with { Outcome = RequireValue(args, i) };
                    i += 2;
                    break;
                case "--workflow":
                    query = query with { Workflow = RequireValue(args, i) };
                    i += 2;
                    break;
                case "--pr":
                    query = query with { PullRequest = RequireValue(args, i) };
                    i += 2;
                    break;
                case "--issue":
                    query = query with { Issue = RequireValue(args, i) };
                    i += 2;
                    break;
                case "--source-kind":
                    query = query with { SourceKind = ParseSourceKind(RequireValue(args, i)) };
                    i += 2;
                    break;
                case "--resolution":
                    query = ApplyResolution(query, RequireValue(args, i));
                    i += 2;
                    break;
                case "--repo-identity":
                    repositoryIdentityKey = RequireValue(args, i);
                    i += 2;
                    break;
                case "--format":
                    format = ParseFormat(RequireValue(args, i));
                    i += 2;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliArgumentException($"Unknown option '{arg}'. {Usage}");
                    }

                    if (roomDirectoryPath is not null)
                    {
                        throw new CliArgumentException($"Unexpected extra argument '{arg}'. {Usage}");
                    }

                    roomDirectoryPath = RoomDirectoryPath.Resolve(arg);
                    i++;
                    break;
            }
        }

        if (query is { Since: { } since, Until: { } until } && until <= since)
        {
            throw new CliArgumentException(
                $"'--until' ({until:O}) is not after '--since' ({since:O}), so the window is empty: --since is " +
                $"inclusive and --until is exclusive. {Usage}");
        }

        if (roomDirectoryPath is not null)
        {
            query = query with { Room = BatonPaths.RecordKey(roomDirectoryPath) };
        }

        return new LedgerViewOptions(roomDirectoryPath, query, repositoryIdentityKey, format, drill, help);
    }

    private static string RequireValue(IReadOnlyList<string> args, int index)
    {
        if (index + 1 >= args.Count)
        {
            throw new CliArgumentException(
                $"Option '{args[index]}' requires a value. {Usage}",
                $"pass a value after '{args[index]}'.");
        }

        return args[index + 1];
    }

    /// <summary>
    /// An instant in UTC, from either spelling the contract accepts. A bare <c>yyyy-MM-dd</c> is the
    /// operator's LOCAL midnight — the boundary a person means by "since the 4th" — and everything else
    /// goes through <see cref="DateTimeOffset"/>, which reads an explicit <c>Z</c>/offset as written and
    /// an offsetless timestamp as local. The ledger records UTC, so both land in the same frame here
    /// rather than at each comparison.
    /// </summary>
    /// <param name="zone">
    /// Which zone "local midnight" is in. <see langword="null"/> — the production default — is the
    /// machine's. Injectable ONLY so a test can assert the shorthand against a fixed non-zero offset:
    /// an expectation written as <c>SpecifyKind(…, Local).ToUniversalTime()</c> is this method's own
    /// expression, and at UTC+0 it cannot tell <c>Local</c> from <c>Utc</c> (#1893 review M3).
    /// </param>
    internal static DateTime ParseInstant(string value, string optionName, TimeZoneInfo? zone = null)
    {
        var trimmed = value.Trim();

        if (DateTime.TryParseExact(
                trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            // Subtracting the offset rather than TimeZoneInfo.ConvertTimeToUtc: that throws on a local
            // time a DST transition skipped, and refusing "--since 2018-11-04" in Sao Paulo would be a
            // worse answer than the standard-offset reading GetUtcOffset gives -- which is also what
            // DateTime.ToUniversalTime does, so the machine-zone default is unchanged.
            var local = zone ?? TimeZoneInfo.Local;
            return DateTime.SpecifyKind(date - local.GetUtcOffset(date), DateTimeKind.Utc);
        }

        if (DateTimeOffset.TryParse(
                trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var offset))
        {
            return offset.UtcDateTime;
        }

        throw new CliArgumentException(
            $"Option '{optionName}' needs an ISO-8601 instant or a 'yyyy-MM-dd' date, not '{value}'. {Usage}",
            $"{optionName} 2026-09-04   (local midnight)   or   {optionName} 2026-09-04T14:00:00Z");
    }

    /// <summary>
    /// <c>--resolution</c>, which is two facets behind one option (#1913 review finding 5):
    /// <c>none</c>/<c>any</c> select on the field's ABSENCE or presence, and a kind selects one value
    /// of it. Two spellings rather than a second option because an operator asking "which rows are
    /// interventions" and one asking "which rows are rejections" are asking about the same field.
    /// The kind vocabulary is the ledger's own enum spelling, deserialized rather than restated, for
    /// the same reason <see cref="ParseSourceKind"/> is.
    /// </summary>
    private static LedgerQuery ApplyResolution(LedgerQuery query, string value)
    {
        var trimmed = value.Trim();
        if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
        {
            return query with { HasResolution = false, Resolution = null };
        }

        if (string.Equals(trimmed, "any", StringComparison.OrdinalIgnoreCase))
        {
            return query with { HasResolution = true, Resolution = null };
        }

        try
        {
            return query with
            {
                HasResolution = null,
                Resolution = JsonSerializer.Deserialize<ConductorResolution>($"\"{JsonEncodedText.Encode(trimmed)}\""),
            };
        }
        catch (JsonException)
        {
            throw new CliArgumentException(
                $"Unknown --resolution '{value}'. Known values: none (execution attempts alone), any " +
                $"(interventions alone), accept-capture, reject, close. {Usage}");
        }
    }

    /// <summary>
    /// Deserialized through the enum's own <c>JsonStringEnumMemberName</c> spellings rather than a
    /// second table of names here: the CLI's vocabulary for a source kind is the ledger's, by
    /// construction, so a kind added there is accepted here without an edit.
    /// </summary>
    private static CostSourceKind ParseSourceKind(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<CostSourceKind>($"\"{JsonEncodedText.Encode(value.Trim())}\"");
        }
        catch (JsonException)
        {
            throw new CliArgumentException(
                $"Unknown --source-kind '{value}'. Known kinds: baton-execution, github-backfill, " +
                $"claude-code-session, codex-session, antigravity-session. {Usage}");
        }
    }

    private static LedgerOutputFormat ParseFormat(string value) => value.Trim().ToLowerInvariant() switch
    {
        "text" => LedgerOutputFormat.Text,
        "json" => LedgerOutputFormat.Json,
        "csv" => LedgerOutputFormat.Csv,
        _ => throw new CliArgumentException($"Unknown --format '{value}'. Known formats: text, json, csv. {Usage}"),
    };
}
