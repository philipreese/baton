using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// Parses <c>baton trust</c>'s arguments: <c>baton trust &lt;project-path&gt; --ceiling
/// all|none|&lt;comma-separated categories&gt;</c>, <c>baton trust --list</c>,
/// <c>baton trust &lt;project-path&gt; --revoke</c>, or <c>baton trust &lt;project-path&gt; --forget</c>.
/// Follows <see cref="WatchOptionsParser"/>'s own structure and error-handling contract — every
/// failure is a <see cref="CliArgumentException"/>, never a bare framework exception.
/// </summary>
public static class TrustOptionsParser
{
    public const string Usage =
        "Usage: baton trust <project-path> --ceiling all|none|<comma-separated categories> | " +
        "baton trust --list | baton trust <project-path> --revoke | baton trust <project-path> --forget";

    /// <summary>
    /// The category vocabulary <c>--ceiling</c> accepts, one token per <see cref="PermissionGrant"/>
    /// category — #1166's scope ruling, stated in full on <c>ProjectCeiling</c>'s own doc comment
    /// (spec/baton.md §9 carries the same fact canonically).
    /// </summary>
    public const string CategoryTokens = "ReadFiles,WriteFiles,RunShellCommands,NetworkAccess";

    public static TrustOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 1 && args[0] == "--list")
        {
            return new TrustOptions(TrustMode.List, null, null);
        }

        string? projectPath = null;
        string? ceilingText = null;
        var revoke = false;
        var forget = false;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--ceiling":
                    if (i + 1 >= args.Count)
                    {
                        throw new CliArgumentException(
                            $"Option '--ceiling' requires a value. {Usage}",
                            $"pass a value after '--ceiling', e.g. --ceiling all or --ceiling {CategoryTokens}.");
                    }

                    ceilingText = args[i + 1];
                    i += 2;
                    continue;
                case "--revoke":
                    revoke = true;
                    i++;
                    continue;
                case "--forget":
                    forget = true;
                    i++;
                    continue;
                case "--list":
                    throw new CliArgumentException(
                        $"'--list' cannot be combined with a project path or other options. {Usage}");
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliArgumentException($"Unknown option '{arg}'. {Usage}");
                    }

                    if (projectPath is not null)
                    {
                        throw new CliArgumentException($"Unexpected extra argument '{arg}'. {Usage}");
                    }

                    projectPath = arg;
                    i++;
                    continue;
            }
        }

        if (projectPath is null)
        {
            throw new CliArgumentException($"Missing required <project-path> argument. {Usage}");
        }

        if (revoke && forget)
        {
            throw new CliArgumentException($"'--revoke' cannot be combined with '--forget': one withdraws a grant and leaves a tombstone, the other deletes the record. {Usage}");
        }

        if ((revoke || forget) && ceilingText is not null)
        {
            throw new CliArgumentException($"'{(revoke ? "--revoke" : "--forget")}' cannot be combined with '--ceiling'. {Usage}");
        }

        if (revoke)
        {
            return new TrustOptions(TrustMode.Revoke, projectPath, null);
        }

        if (forget)
        {
            return new TrustOptions(TrustMode.Forget, projectPath, null);
        }

        if (ceilingText is null)
        {
            throw new CliArgumentException($"Missing required '--ceiling <categories>' (or '--revoke' / '--forget'). {Usage}");
        }

        return new TrustOptions(TrustMode.Register, projectPath, ParseCeiling(ceilingText));
    }

    /// <summary>
    /// <c>none</c> parses to every category closed — <b>not</b> a guarantee the worker does nothing.
    /// A raw <see cref="WorkerInvocation.PermissionScope"/> escape hatch bypasses this ceiling entirely
    /// unless <see cref="ProjectCeiling.IsUnrestricted"/> (see <see cref="ProjectCeilingGate"/>'s own
    /// doc), and even a fully-capped structured grant still resolves to *something* runnable on both
    /// vendors (claude pre-approves Edit/Write/NotebookEdit regardless of <c>WriteFiles</c>, #649; agy
    /// resolves <c>--mode default</c>) — "none" caps every category a structured grant could ask for,
    /// it does not itself refuse the dispatch.
    /// </summary>
    private static ProjectCeiling ParseCeiling(string text)
    {
        if (string.Equals(text, "all", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectCeiling.Unrestricted;
        }

        if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new ProjectCeiling(false, false, false, false);
        }

        bool readFiles = false, writeFiles = false, runShellCommands = false, networkAccess = false;
        foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Case-insensitive, matching the 'all'/'none' keywords above -- an operator typing
            // --ceiling readfiles should not be refused over casing alone.
            switch (token)
            {
                case var t when string.Equals(t, "ReadFiles", StringComparison.OrdinalIgnoreCase):
                    readFiles = true;
                    break;
                case var t when string.Equals(t, "WriteFiles", StringComparison.OrdinalIgnoreCase):
                    writeFiles = true;
                    break;
                case var t when string.Equals(t, "RunShellCommands", StringComparison.OrdinalIgnoreCase):
                    runShellCommands = true;
                    break;
                case var t when string.Equals(t, "NetworkAccess", StringComparison.OrdinalIgnoreCase):
                    networkAccess = true;
                    break;
                default:
                    throw new CliArgumentException(
                        $"Unknown ceiling category '{token}'. Pass 'all', 'none', or a comma-separated " +
                        $"subset of {CategoryTokens}. {Usage}");
            }
        }

        return new ProjectCeiling(readFiles, writeFiles, runShellCommands, networkAccess);
    }
}
