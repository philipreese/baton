using Baton.Accounting;
using Baton.Memory;

namespace Baton.Cli;

public static class MemoryReadOptionsParser
{
    public const string Usage = "Usage: baton memory read [--repository <id>|fleet] [--format text|json] [--help]";

    public static MemoryReadOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? repository = null;
        var format = MemoryAuditOutputFormat.Text;
        var help = false;
        for (var i = 0; i < args.Count;)
        {
            switch (args[i])
            {
                case "--help":
                case "-h":
                    help = true;
                    i++;
                    break;
                case "--repository":
                    if (i + 1 >= args.Count) throw new CliArgumentException($"Option '--repository' requires a value. {Usage}");
                    var value = args[i + 1];
                    repository = FleetMemory.IsFleet(value)
                        ? FleetMemory.Slug
                        : RepositoryIdentity.TryCanonicalize(value)
                            ?? throw new CliArgumentException($"'{value}' is not a repository identity or 'fleet'. {Usage}");
                    i += 2;
                    break;
                case "--format":
                    if (i + 1 >= args.Count) throw new CliArgumentException($"Option '--format' requires a value. {Usage}");
                    format = args[i + 1].Trim().ToLowerInvariant() switch
                    {
                        "text" => MemoryAuditOutputFormat.Text,
                        "json" => MemoryAuditOutputFormat.Json,
                        _ => throw new CliArgumentException($"Unknown --format '{args[i + 1]}'. {Usage}"),
                    };
                    i += 2;
                    break;
                default:
                    throw new CliArgumentException(args[i].StartsWith("--", StringComparison.Ordinal)
                        ? $"Unknown option '{args[i]}'. {Usage}"
                        : $"Unexpected argument '{args[i]}'. {Usage}");
            }
        }

        return new MemoryReadOptions(repository, format, help);
    }
}
