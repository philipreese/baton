using System.Globalization;

namespace Baton.Cli;

/// <summary>
/// Parses <c>baton audit lanes</c> (#1921). Same shape as <see cref="MemoryAuditOptionsParser"/> — one
/// <see cref="CliArgumentException"/> per malformed invocation, and <c>--dry-run</c> rejected by name
/// rather than as a generic unknown option.
/// </summary>
public static class AuditLanesOptionsParser
{
    public const string Usage =
        "Usage: baton audit lanes [--since <duration>] [--vendor <name>] [--rooms-root <dir>] "
        + "[--format text|json] [--help]";

    /// <summary>
    /// What <c>--help</c> prints under <see cref="Usage"/>. Every line states a negative a reader's
    /// prior would otherwise fill in wrongly (CLAUDE.md, "Writing documentation"): which zeros are
    /// measurements and which are not, which clock <c>--since</c> reads, and the one population this
    /// report cannot see.
    /// </summary>
    public static IReadOnlyList<string> HelpLines { get; } =
    [
        "",
        "Reads every room's captured worker stream and reports, per room and per vendor, how much of",
        "each lane's tool-step budget bought nothing (#1921). Read-only: it opens flow logs and",
        "stdout captures and writes nothing anywhere.",
        "",
        "  steps      Real tool calls the lane made, in the unit --max-tool-steps caps.",
        "  refused    Calls Baton's own permission grant declined -- billed, and they bought the",
        "             location of the boundary instead of information.",
        "  repeated   Calls that re-issued a tool+arguments pair the same execution had already",
        "             issued. NOT reported for a vendor whose stream carries no arguments to key on;",
        "             such a lane reports 0 because there was nothing to compare, not because it",
        "             repeated nothing. The per-vendor breakdown is where that reads correctly.",
        "  empty      Calls that returned an empty payload -- a search that matched nothing, a",
        "             command that printed nothing. Often the honest answer to a well-formed",
        "             question, which is why it is reported here and not on the cost-ledger row.",
        "",
        "  --since    Rooms whose flow log was written within this much of now: <n>m, <n>h or <n>d",
        "             (e.g. '36h'). A DURATION, not 'baton ledger --since's instant, and it reads a",
        "             file's last-write time rather than any recorded endedAt.",
        "  --vendor   One adapter name (claude, agy, codex), matched case-insensitively. A room whose",
        "             executions are all filtered out is absent, never reported at zero, and is",
        "             disclosed under its own excluded-by-vendor count rather than as a room whose",
        "             stream could not be read.",
        "  --rooms-root  Walk this directory's immediate children instead of ~/.baton/rooms.",
        "",
        "A refusal is counted by ONE marker Baton stamps where the refusal is produced, so a new",
        "refusal phrasing cannot escape the count. The cost, stated plainly: a room captured before",
        "that marker shipped carries real refusals and reports 'refused 0'. A room reporting no",
        "counts at all (--), other than one --vendor excluded entirely, is one whose stream carried",
        "no tool activity this reader could parse -- never a claim that the lane ran no tools.",
    ];

    public static AuditLanesOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        TimeSpan? since = null;
        string? vendor = null;
        string? roomsRoot = null;
        var format = AuditLanesOutputFormat.Text;
        var help = false;

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
                case "--since":
                    since = ParseDuration(RequireValue(args, i));
                    i += 2;
                    break;
                case "--vendor":
                    vendor = RequireValue(args, i).Trim();
                    if (vendor.Length == 0)
                    {
                        throw new CliArgumentException(
                            $"Option '--vendor' requires a non-empty adapter name. {Usage}");
                    }

                    i += 2;
                    break;
                case "--rooms-root":
                    roomsRoot = RequireValue(args, i);
                    i += 2;
                    break;
                case "--format":
                    format = ParseFormat(RequireValue(args, i));
                    i += 2;
                    break;
                // Named rather than left to the unknown-option branch below, for the reason
                // MemoryAuditOptionsParser's own --dry-run arm states.
                case "--dry-run":
                    throw new CliArgumentException(
                        "'baton audit lanes' has no '--dry-run': it is read-only by construction and "
                        + $"never writes anything. {Usage}",
                        "drop the flag and run the command.");
                default:
                    throw new CliArgumentException(
                        arg.StartsWith("--", StringComparison.Ordinal)
                            ? $"Unknown option '{arg}'. {Usage}"
                            : $"Unexpected argument '{arg}'. {Usage}");
            }
        }

        return new AuditLanesOptions(since, vendor, roomsRoot, format, help);
    }

    /// <summary>
    /// <c>&lt;n&gt;m</c>, <c>&lt;n&gt;h</c> or <c>&lt;n&gt;d</c>, whole numbers only.
    /// <para>
    /// <b>A bare number is refused rather than given a default unit</b> — a reader who means days and a
    /// reader who means hours would both write <c>7</c>, and one of them would get a window off by 24×
    /// with nothing saying so. Zero and negatives are refused for the same reason: a window that selects
    /// nothing is far more likely a typo than an intent, and reporting "no rooms" for it would look like
    /// a measurement.
    /// </para>
    /// </summary>
    private static TimeSpan ParseDuration(string value)
    {
        var trimmed = value.Trim();
        var unit = trimmed.Length > 0 ? char.ToLowerInvariant(trimmed[^1]) : '\0';
        if (unit is 'm' or 'h' or 'd'
            && int.TryParse(
                trimmed[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            && amount > 0)
        {
            return unit switch
            {
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                _ => TimeSpan.FromDays(amount),
            };
        }

        throw new CliArgumentException(
            $"Unreadable --since duration '{value}'. Expected a positive whole number followed by "
            + $"'m', 'h' or 'd' (e.g. '90m', '36h', '7d'). {Usage}");
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

    private static AuditLanesOutputFormat ParseFormat(string value) => value.Trim().ToLowerInvariant() switch
    {
        "text" => AuditLanesOutputFormat.Text,
        "json" => AuditLanesOutputFormat.Json,
        _ => throw new CliArgumentException(
            $"Unknown --format '{value}'. Known formats: text, json. {Usage}"),
    };
}
