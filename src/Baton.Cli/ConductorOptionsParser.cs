namespace Baton.Cli;

/// <summary>
/// Parser for <c>baton conductor</c> arguments (#2296).
/// </summary>
public static class ConductorOptionsParser
{
    public const string Usage = "Usage: baton conductor claim <holder> [--workspace <dir>]\n" +
                                "       baton conductor list [--json]\n" +
                                "       baton conductor release <holder> [--workspace <dir>] --reason <text>\n" +
                                "       baton conductor takeover <holder> [--workspace <dir>] --reason <text>";

    public static ConductorOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            throw new CliArgumentException($"'baton conductor' requires a sub-verb (claim, list, release, takeover).\n{Usage}");
        }

        var subverb = args[0].ToLowerInvariant();
        return subverb switch
        {
            "claim" => ParseClaim(args[1..]),
            "list" => ParseList(args[1..]),
            "release" => ParseRelease(args[1..]),
            "takeover" => ParseTakeover(args[1..]),
            _ => throw new CliArgumentException($"Unknown 'baton conductor' sub-verb '{args[0]}'.\n{Usage}"),
        };
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
