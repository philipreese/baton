namespace Baton.Cli;

/// <summary>
/// Parser for <c>baton conductor</c> arguments (#2296).
/// </summary>
public static class ConductorOptionsParser
{
    public const string Usage = "Usage: baton conductor claim <holder> [--workspace <dir>]\n" +
                                "       baton conductor list [--json]\n" +
                                "       baton conductor release <holder> [--workspace <dir>] --reason <text>\n" +
                                "       baton conductor takeover <holder> [--workspace <dir>] --reason <text>\n" +
                                "       baton conductor prepare --request <file>\n" +
                                "       baton conductor decide --obligation <key> --context <file>\n" +
                                "       baton conductor act --obligation <key> --holder <holder> --action replace-review --expected-head <full-sha>\n" +
                                "       baton conductor follow --request <file>\n" +
                                "       baton conductor attach --request <file>\n" +
                                "       baton conductor detach --request <file>\n" +
                                "Hosted control and stopped-acquisition recovery: spec/baton.md §14.";

    public static ConductorOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            throw new CliArgumentException($"'baton conductor' requires a sub-verb.\n{Usage}");
        }

        var subverb = args[0].ToLowerInvariant();
        return subverb switch
        {
            "claim" => ParseClaim(args[1..]),
            "list" => ParseList(args[1..]),
            "release" => ParseRelease(args[1..]),
            "takeover" => ParseTakeover(args[1..]),
            "prepare" => ParseReadiness(args[1..], ConductorVerb.Prepare),
            "decide" => ParseReadiness(args[1..], ConductorVerb.Decide),
            "act" => ParseAct(args[1..]),
            "follow" => ParseFollow(args[1..]),
            "attach" => ParseFollow(args[1..]) with { Verb = ConductorVerb.Attach },
            "detach" => ParseFollow(args[1..]) with { Verb = ConductorVerb.Detach },
            _ => throw new CliArgumentException($"Unknown 'baton conductor' sub-verb '{args[0]}'.\n{Usage}"),
        };
    }

    private static ConductorOptions ParseFollow(string[] args)
    {
        if (args.Length != 2 || args[0] != "--request" || string.IsNullOrWhiteSpace(args[1])
            || args[1].StartsWith("--", StringComparison.Ordinal))
            throw new CliArgumentException($"Invalid conductor follow arguments.\n{Usage}");

        return new(ConductorVerb.Follow, FollowRequestFile: args[1]);
    }

    private static ConductorOptions ParseAct(string[] args)
    {
        string? obligation = null;
        string? holder = null;
        string? action = null;
        string? head = null;
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (option is not ("--obligation" or "--holder" or "--action" or "--expected-head")
                || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new CliArgumentException($"Invalid conductor action arguments.\n{Usage}");
            var value = args[++i];
            if (string.IsNullOrWhiteSpace(value))
                throw new CliArgumentException($"'{option}' requires a non-blank value.\n{Usage}");
            switch (option)
            {
                case "--obligation" when obligation is null: obligation = value; break;
                case "--holder" when holder is null: holder = value; break;
                case "--action" when action is null: action = value; break;
                case "--expected-head" when head is null: head = value; break;
                default: throw new CliArgumentException($"Duplicate option '{option}'.\n{Usage}");
            }
        }
        if (obligation is null || holder is null || action != "replace-review"
            || head is not { Length: 40 } || !head.All(Uri.IsHexDigit))
            throw new CliArgumentException($"Invalid conductor action arguments.\n{Usage}");
        return new(ConductorVerb.Act, Holder: holder, ObligationKey: obligation,
            Action: action, ExpectedHead: head);
    }

    private static ConductorOptions ParseReadiness(string[] args, ConductorVerb verb)
    {
        string? request = null;
        string? obligation = null;
        string? context = null;
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (option is not ("--request" or "--obligation" or "--context")
                || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliArgumentException($"Invalid conductor readiness arguments.\n{Usage}");
            }

            var value = args[++i];
            if (string.IsNullOrWhiteSpace(value))
                throw new CliArgumentException($"'{option}' requires a non-blank value.\n{Usage}");
            switch (option)
            {
                case "--request" when request is null: request = value; break;
                case "--obligation" when obligation is null: obligation = value; break;
                case "--context" when context is null: context = value; break;
                default: throw new CliArgumentException($"Duplicate option '{option}'.\n{Usage}");
            }
        }

        if (verb == ConductorVerb.Prepare && request is not null && obligation is null && context is null)
            return new(verb, RequestFile: request);
        if (verb == ConductorVerb.Decide && request is null && obligation is not null && context is not null)
            return new(verb, ObligationKey: obligation, ContextFile: context);
        throw new CliArgumentException($"Invalid conductor readiness arguments.\n{Usage}");
    }

    private static ConductorOptions ParseClaim(string[] args)
    {
        string? holder = null;
        string? workspace = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--workspace")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new CliArgumentException($"'--workspace' requires a directory argument.\n{Usage}");
                }

                workspace = args[++i];
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliArgumentException($"Unknown option '{arg}'.\n{Usage}");
            }
            else if (holder is null)
            {
                holder = arg;
            }
            else
            {
                throw new CliArgumentException($"Unexpected argument '{arg}'.\n{Usage}");
            }
        }

        if (string.IsNullOrWhiteSpace(holder))
        {
            throw new CliArgumentException($"'baton conductor claim' requires a <holder> argument.\n{Usage}");
        }

        return new ConductorOptions(ConductorVerb.Claim, Holder: holder, Workspace: workspace);
    }

    private static ConductorOptions ParseList(string[] args)
    {
        var json = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase))
            {
                json = true;
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliArgumentException($"Unknown option '{arg}'.\n{Usage}");
            }
            else
            {
                throw new CliArgumentException($"Unexpected argument '{arg}'.\n{Usage}");
            }
        }

        return new ConductorOptions(ConductorVerb.List, Json: json);
    }

    private static ConductorOptions ParseRelease(string[] args)
    {
        string? holder = null;
        string? workspace = null;
        string? reason = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--workspace")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new CliArgumentException($"'--workspace' requires a directory argument.\n{Usage}");
                }

                workspace = args[++i];
            }
            else if (arg == "--reason")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new CliArgumentException($"'--reason' requires an explanation text argument.\n{Usage}");
                }

                reason = args[++i];
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliArgumentException($"Unknown option '{arg}'.\n{Usage}");
            }
            else if (holder is null)
            {
                holder = arg;
            }
            else
            {
                throw new CliArgumentException($"Unexpected argument '{arg}'.\n{Usage}");
            }
        }

        if (string.IsNullOrWhiteSpace(holder))
        {
            throw new CliArgumentException($"'baton conductor release' requires a <holder> argument.\n{Usage}");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new CliArgumentException($"'baton conductor release' requires a non-blank --reason.\n{Usage}");
        }

        return new ConductorOptions(ConductorVerb.Release, Holder: holder, Workspace: workspace, Reason: reason);
    }

    private static ConductorOptions ParseTakeover(string[] args)
    {
        string? holder = null;
        string? workspace = null;
        string? reason = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--workspace")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new CliArgumentException($"'--workspace' requires a directory argument.\n{Usage}");
                }

                workspace = args[++i];
            }
            else if (arg == "--reason")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new CliArgumentException($"'--reason' requires an explanation text argument.\n{Usage}");
                }

                reason = args[++i];
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliArgumentException($"Unknown option '{arg}'.\n{Usage}");
            }
            else if (holder is null)
            {
                holder = arg;
            }
            else
            {
                throw new CliArgumentException($"Unexpected argument '{arg}'.\n{Usage}");
            }
        }

        if (string.IsNullOrWhiteSpace(holder))
        {
            throw new CliArgumentException($"'baton conductor takeover' requires a <holder> argument.\n{Usage}");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new CliArgumentException($"'baton conductor takeover' requires a non-blank --reason.\n{Usage}");
        }

        return new ConductorOptions(ConductorVerb.Takeover, Holder: holder, Workspace: workspace, Reason: reason);
    }
}
