using System.Globalization;
using Baton.Queue;

namespace Baton.Cli;

/// <summary>
/// Parses <c>baton queue</c>'s arguments (#1934 slice 1). Follows <see cref="TrustOptionsParser"/>'s
/// contract: every failure is a <see cref="CliArgumentException"/>, never a bare framework exception,
/// and nothing here touches the filesystem — <see cref="QueueCommand"/> owns every check that needs
/// to read a file, so the parser stays testable without a temp directory.
/// </summary>
public static class QueueOptionsParser
{
    public const string Usage =
        "Usage: baton queue add <tag> --role <role> --spec <file> (--issue <n> | --workspace <dir>) " +
        "[--scope engine|tooling|docs] [--adapter <a>] [--model <m>] [--effort <e>] [--timeout <minutes>] " +
        "[--max-tool-steps <n>] [--token-budget <n>] [--override-runway <reason>] [--reason <why>] | " +
        "baton queue list | baton queue hold | baton queue resume | baton queue import <file>";

    public static QueueOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
        {
            throw new CliArgumentException($"Missing 'baton queue' sub-verb. {Usage}");
        }

        return args[0] switch
        {
            "add" => ParseAdd(args),
            "list" => ParseBare(QueueVerb.List, args),
            "hold" => ParseBare(QueueVerb.Hold, args),
            "resume" => ParseBare(QueueVerb.Resume, args),
            "import" => ParseImport(args),
            _ => throw new CliArgumentException($"Unknown 'baton queue' sub-verb '{args[0]}'. {Usage}"),
        };
    }

    private static QueueOptions ParseBare(QueueVerb verb, IReadOnlyList<string> args)
    {
        if (args.Count > 1)
        {
            throw new CliArgumentException($"'baton queue {args[0]}' takes no arguments (got '{args[1]}'). {Usage}");
        }

        return new QueueOptions(verb);
    }

    private static QueueOptions ParseImport(IReadOnlyList<string> args)
    {
        if (args.Count != 2)
        {
            throw new CliArgumentException($"'baton queue import' takes exactly one file path. {Usage}");
        }

        return new QueueOptions(QueueVerb.Import, ImportFilePath: args[1]);
    }

    private static QueueOptions ParseAdd(IReadOnlyList<string> args)
    {
        string? tag = null;
        string? role = null, spec = null, workspace = null, scope = null;
        string? adapter = null, model = null, effort = null, overrideRunway = null, reason = null;
        int? issue = null, timeout = null, maxToolSteps = null;
        long? tokenBudget = null;

        var i = 1;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--role":
                    role = TakeValue(args, ref i, "--role");
                    continue;
                case "--spec":
                    spec = TakeValue(args, ref i, "--spec");
                    continue;
                case "--workspace":
                    workspace = TakeValue(args, ref i, "--workspace");
                    continue;
                case "--scope":
                    scope = TakeValue(args, ref i, "--scope");
                    continue;
                case "--adapter":
                    adapter = TakeValue(args, ref i, "--adapter");
                    continue;
                case "--model":
                    model = TakeValue(args, ref i, "--model");
                    continue;
                case "--effort":
                    effort = TakeValue(args, ref i, "--effort");
                    continue;
                case "--reason":
                    reason = TakeValue(args, ref i, "--reason");
                    continue;
                case "--override-runway":
                    overrideRunway = TakeValue(args, ref i, "--override-runway");
                    continue;
                case "--issue":
                    issue = TakeInt(args, ref i, "--issue");
                    continue;
                case "--timeout":
                    timeout = TakeInt(args, ref i, "--timeout");
                    continue;
                case "--max-tool-steps":
                    maxToolSteps = TakeInt(args, ref i, "--max-tool-steps");
                    continue;
                case "--token-budget":
                    tokenBudget = TakeLong(args, ref i, "--token-budget");
                    continue;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliArgumentException($"Unknown option '{arg}'. {Usage}");
                    }

                    if (tag is not null)
                    {
                        throw new CliArgumentException($"Unexpected extra argument '{arg}'. {Usage}");
                    }

                    tag = arg;
                    i++;
                    continue;
            }
        }

        if (tag is null)
        {
            throw new CliArgumentException($"Missing required <tag> argument. {Usage}");
        }

        if (!QueueTag.IsValid(tag))
        {
            throw new CliArgumentException(
                $"'{tag}' is not a usable queue tag ({QueueTag.Rule}). The tag names this item's spec file "
                + "under ~/.baton/queue/specs and labels its room, so it is constrained to a slug rather "
                + "than free text.",
                "pick a tag like '1934-queue' or 'fix_login'.");
        }

        if (string.IsNullOrWhiteSpace(role))
        {
            throw new CliArgumentException($"Missing required '--role <role>'. {Usage}");
        }

        if (string.IsNullOrWhiteSpace(spec))
        {
            throw new CliArgumentException($"Missing required '--spec <file>'. {Usage}");
        }

        if (issue is null && string.IsNullOrWhiteSpace(workspace))
        {
            throw new CliArgumentException(
                $"An item needs somewhere to run: pass '--issue <n>' to provision a worktree now, or "
                + $"'--workspace <dir>' to name one that already exists. {Usage}");
        }

        if (issue is not null && !string.IsNullOrWhiteSpace(workspace))
        {
            throw new CliArgumentException(
                "'--issue' and '--workspace' are two answers to the same question — '--issue' provisions the "
                + "worktree the item runs in, so a workspace passed alongside it would be silently discarded.",
                "drop one of them.");
        }

        if (scope is not null && !QueueTierTable.ScopeClasses.Contains(scope, StringComparer.OrdinalIgnoreCase))
        {
            throw new CliArgumentException(
                $"Unknown scope class '{scope}'. Pass one of: {string.Join(", ", QueueTierTable.ScopeClasses)}. "
                + "A scope class picks this item's tier (adapter, model, effort), so an unrecognised one is "
                + "refused rather than resolved to some other tier's model.");
        }

        // Q3's mandatory justification (spec/baton.md §13). Gated on `scope is not null` because
        // without a tier there is no departure to justify -- an item that simply names its axes is
        // not overriding anything.
        var overridesAnAxis = adapter is not null || model is not null || effort is not null;
        if (scope is not null && overridesAnAxis && string.IsNullOrWhiteSpace(reason))
        {
            throw new CliArgumentException(
                "An item that overrides its tier's adapter, model or effort must say why: pass "
                + "'--reason \"<why>\"'. The reason is recorded on the launch fact and on the room's bindings, "
                + "which is the whole point of having a tier table to depart from.");
        }

        if (overrideRunway is not null && string.IsNullOrWhiteSpace(overrideRunway))
        {
            throw new CliArgumentException(
                "'--override-runway' requires a non-blank reason — the same rule 'baton dispatch' applies, "
                + "because this value is forwarded to it verbatim.");
        }

        if (timeout is <= 0)
        {
            throw new CliArgumentException($"'--timeout' must be a positive number of minutes. {Usage}");
        }

        if (maxToolSteps is <= 0)
        {
            throw new CliArgumentException($"'--max-tool-steps' must be positive. {Usage}");
        }

        if (tokenBudget is <= 0)
        {
            throw new CliArgumentException($"'--token-budget' must be positive. {Usage}");
        }

        if (issue is <= 0)
        {
            throw new CliArgumentException($"'--issue' must be a positive issue number. {Usage}");
        }

        return new QueueOptions(
            QueueVerb.Add, tag, role, spec, issue, workspace, scope, adapter, model, effort,
            timeout, maxToolSteps, tokenBudget, overrideRunway, reason);
    }

    private static string TakeValue(IReadOnlyList<string> args, ref int i, string option)
    {
        if (i + 1 >= args.Count)
        {
            throw new CliArgumentException($"Option '{option}' requires a value. {Usage}");
        }

        var value = args[i + 1];
        i += 2;
        return value;
    }

    private static int TakeInt(IReadOnlyList<string> args, ref int i, string option)
    {
        var raw = TakeValue(args, ref i, option);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new CliArgumentException($"Option '{option}' expects a whole number, got '{raw}'. {Usage}");
        }

        return value;
    }

    private static long TakeLong(IReadOnlyList<string> args, ref int i, string option)
    {
        var raw = TakeValue(args, ref i, option);
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new CliArgumentException($"Option '{option}' expects a whole number, got '{raw}'. {Usage}");
        }

        return value;
    }
}
