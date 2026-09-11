namespace Baton.Vendors;

/// <summary>
/// Compiles the deliberately small supported spelling of one standalone bare <c>gh pr create</c>
/// into argv. It is not a general shell parser: anything requiring shell interpretation is refused.
/// </summary>
internal static class DirectGhPullRequestCreate
{
    private static readonly HashSet<string> Switches = new(StringComparer.Ordinal)
    {
        "--draft", "--fill", "--fill-first", "--fill-verbose", "--maintainer-edit",
        "--no-maintainer-edit",
    };

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--base", "--body", "--body-file", "--head", "--repo", "-R", "--template", "--title",
    };

    internal sealed record Compilation(bool IsCreateCommand, IReadOnlyList<string>? Arguments, string? Refusal);

    public static Compilation Compile(string? commandLine, GhPullRequestCreateProvenance? provenance)
    {
        var classification = ShellCreateLexicalClassifier.Classify(commandLine);
        if (classification == ShellCreateLexicalClassifier.Result.Unsupported)
        {
            return Refuse("Baton cannot classify this shell wrapper's syntax without interpreting it. "
                + "Use simple literal commands, or run one standalone bare `gh pr create` with "
                + "literal options in a separate tool call; use direct commands for other work.");
        }
        if (classification == ShellCreateLexicalClassifier.Result.Ordinary)
        {
            return new Compilation(false, null, null);
        }

        if (!TryTokenize(commandLine!, out var tokens, out var tokenError))
        {
            return Refuse(tokenError!);
        }
        if (tokens.Count < 3 || tokens[0] != "gh" || tokens[1] != "pr" || tokens[2] != "create")
        {
            return Refuse("Only one standalone bare `gh pr create` command is supported; path-qualified "
                + "executables and chained commands are refused.");
        }

        if (provenance is null)
        {
            return Refuse("Baton could not establish a trusted GitHub CLI plus a conductor-verified "
                + "canonical repository and named head branch before this worker started. Install gh "
                + "as a native executable on the supervisor PATH outside the workspace and dispatch "
                + "fresh from a GitHub-backed named branch. A legacy or resumed binding without "
                + "PullRequestCreateIdentity is intentionally refused; supply that binding field from "
                + "conductor-verified input rather than trusting the workspace's current Git remote.");
        }

        var arguments = new List<string> { "pr", "create" };
        string? repository = null;
        string? head = null;
        for (var i = 3; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var equals = token.IndexOf('=');
            var option = equals > 0 ? token[..equals] : token;
            string? value = equals > 0 ? token[(equals + 1)..] : null;

            if (Switches.Contains(option) && value is null)
            {
                arguments.Add(option);
                continue;
            }
            if (!ValueOptions.Contains(option) || option == "-R" && value is not null)
            {
                return Refuse($"`{token}` is not in Baton's supported direct-create syntax. Supported "
                    + "options are title/body/body-file/template, base/head/repo, fill variants, draft, "
                    + "and maintainer-edit switches; labels, reviewers, projects, web mode, and positional "
                    + "arguments are outside this permission.");
            }
            if (value is null)
            {
                if (++i >= tokens.Count)
                {
                    return Refuse($"`{option}` requires one value.");
                }
                value = tokens[i];
            }
            if (value.Length == 0 || option == "--body-file" && value == "-")
            {
                return Refuse($"`{option}` has an unsupported empty or interactive value.");
            }

            if (option is "--repo" or "-R")
            {
                if (repository is not null)
                {
                    return Refuse("The repository selector may be specified only once.");
                }
                repository = GitHubRepository.TryCanonicalize(value);
                if (repository != provenance.Repository)
                {
                    return Refuse($"Repository `{value}` does not match the trusted repository "
                        + $"`{provenance.Repository}`.");
                }
                continue;
            }
            if (option == "--head")
            {
                if (head is not null)
                {
                    return Refuse("The head branch may be specified only once.");
                }
                head = value;
                if (!head.Equals(provenance.HeadBranch, StringComparison.Ordinal))
                {
                    return Refuse($"Head `{head}` does not match the trusted branch "
                        + $"`{provenance.HeadBranch}`.");
                }
                continue;
            }

            arguments.Add(option);
            arguments.Add(value);
        }

        arguments.Add("--repo");
        arguments.Add(provenance.Repository);
        arguments.Add("--head");
        arguments.Add(provenance.HeadBranch);
        return new Compilation(true, arguments, null);
    }

    private static Compilation Refuse(string reason) => new(true, null, reason);


    private static bool TryTokenize(string commandLine, out IReadOnlyList<string> tokens, out string? error)
    {
        var result = new List<string>();
        var i = 0;
        while (i < commandLine.Length)
        {
            while (i < commandLine.Length && char.IsWhiteSpace(commandLine[i])) i++;
            if (i == commandLine.Length) break;

            var quoted = commandLine[i] == '"';
            if (commandLine[i] == '\'' || (!quoted && IsRejected(commandLine[i])))
            {
                tokens = [];
                error = "Direct `gh pr create` accepts whitespace-separated bare tokens and whole "
                    + "double-quoted literal values only; shell substitutions, single quotes, control "
                    + "operators, redirection, and unquoted parentheses are refused.";
                return false;
            }

            var start = quoted ? ++i : i;
            while (i < commandLine.Length && (quoted ? commandLine[i] != '"' : !char.IsWhiteSpace(commandLine[i])))
            {
                if (quoted
                        ? commandLine[i] is '$' or '%' or '`' or '\r' or '\n'
                        : IsRejected(commandLine[i]) || commandLine[i] is '"' or '\'')
                {
                    tokens = [];
                    error = "Direct `gh pr create` contains ambiguous shell syntax; use only bare tokens "
                        + "and whole double-quoted literal values, with push and create in separate calls.";
                    return false;
                }
                i++;
            }
            if (quoted && (i >= commandLine.Length || commandLine[i] != '"'))
            {
                tokens = [];
                error = "Direct `gh pr create` contains an unterminated double-quoted value.";
                return false;
            }
            var value = commandLine[start..i];
            if (quoted)
            {
                i++;
                if (i < commandLine.Length && !char.IsWhiteSpace(commandLine[i]))
                {
                    tokens = [];
                    error = "A double-quoted direct-create value must occupy one whole argument.";
                    return false;
                }
            }
            result.Add(value);
        }

        tokens = result;
        error = null;
        return true;
    }

    private static bool IsRejected(char c) => c is '$' or '%' or '`' or ';' or '|' or '&' or '<' or '>'
        or '\r' or '\n' or '(' or ')';
}
