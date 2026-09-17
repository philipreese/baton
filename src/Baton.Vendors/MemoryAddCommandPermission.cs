using Baton.Queue;

namespace Baton.Vendors;

/// <summary>Grant-conditioned admission for one repository-scoped memory-add command.</summary>
public static class MemoryAddCommandPermission
{
    private const string Prefix = "baton memory add --text <memory-add-text> --kind <memory-add-kind> --repository ";

    public static PermissionGrant Add(PermissionGrant grant, MemoryAddDispatchGrant dispatchGrant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(dispatchGrant);
        if (!dispatchGrant.IsWellFormed)
        {
            throw new ArgumentException("Memory-add dispatch grant is malformed.", nameof(dispatchGrant));
        }

        var marker = Prefix + dispatchGrant.Repository;
        var exceptions = (grant.DeniedShellCommandExceptions ?? [])
            .Append(marker)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return grant with { DeniedShellCommandExceptions = exceptions };
    }

    internal static bool Allows(string commandLine, IReadOnlyList<string>? exceptions) =>
        exceptions is { Count: > 0 }
        && exceptions.Any(marker => marker.StartsWith(Prefix, StringComparison.Ordinal)
            && TryParse(commandLine, marker[Prefix.Length..]));

    private static bool TryParse(string commandLine, string repository)
    {
        if (!TryTokenize(commandLine, out var args)
            || args.Count != 9
            || !string.Equals(args[0], "baton", StringComparison.Ordinal)
            || !string.Equals(args[1], "memory", StringComparison.Ordinal)
            || !string.Equals(args[2], "add", StringComparison.Ordinal)
            || !string.Equals(args[3], "--text", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(args[4])
            || !string.Equals(args[5], "--kind", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(args[6])
            || !string.Equals(args[7], "--repository", StringComparison.Ordinal)
            || !string.Equals(args[8], repository, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool TryTokenize(string commandLine, out List<string> args)
    {
        args = [];
        var token = new System.Text.StringBuilder();
        char quote = '\0';
        foreach (var character in commandLine)
        {
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    token.Append(character);
                }
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (token.Length > 0)
                {
                    args.Add(token.ToString());
                    token.Clear();
                }
            }
            else if (character is ';' or '&' or '|' or '`' or '$' or '<' or '>' or '(' or ')' or '\\')
            {
                return false;
            }
            else
            {
                token.Append(character);
            }
        }

        if (quote != '\0')
        {
            return false;
        }
        if (token.Length > 0)
        {
            args.Add(token.ToString());
        }
        return true;
    }
}
