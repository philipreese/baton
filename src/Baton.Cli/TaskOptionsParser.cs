using Baton.Domain;
using Baton.Queue;

namespace Baton.Cli;

public enum TaskVerb { Submit, Status }

public sealed record TaskOptions(
    TaskVerb Verb,
    int? Issue = null,
    string? Project = null,
    TaskSizeDeclaration? Size = null,
    string? Spec = null,
    string? Id = null,
    bool Json = false,
    string? Adapter = null,
    string? Model = null,
    string? Effort = null,
    string? ScopeClass = null,
    string? Reason = null);

public static class TaskOptionsParser
{
    public const string Usage =
        "baton task submit --issue <number> --project <repository-directory> "
        + "--declared-size <small|medium|large|unknown> --size-rationale <clause> [--spec <file>] "
        + "[--scope <engine|tooling|docs>] [--adapter <name>] [--model <name>] [--effort <name>] [--reason <why>]\n"
        + "       baton task status <task-id> [--json]";

    public static TaskOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) throw new CliArgumentException(Usage);
        if (args[0] == "status")
        {
            if (args.Count == 2 && !string.IsNullOrWhiteSpace(args[1]))
                return new TaskOptions(TaskVerb.Status, Id: args[1]);
            if (args.Count == 3 && args[2] == "--json" && !string.IsNullOrWhiteSpace(args[1]))
                return new TaskOptions(TaskVerb.Status, Id: args[1], Json: true);
            throw new CliArgumentException(Usage);
        }

        if (args[0] != "submit") throw new CliArgumentException(Usage);
        int? issue = null;
        string? project = null, size = null, rationale = null, spec = null, adapter = null, model = null, effort = null,
            scope = null, reason = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i++)
        {
            var flag = args[i];
            if (flag is not ("--issue" or "--project" or "--declared-size" or "--size-rationale" or "--spec"
                    or "--scope" or "--adapter" or "--model" or "--effort" or "--reason")
                || !seen.Add(flag) || ++i == args.Count || string.IsNullOrWhiteSpace(args[i]))
                throw new CliArgumentException(Usage);
            switch (flag)
            {
                case "--issue":
                    if (!int.TryParse(args[i], out var parsed) || parsed <= 0)
                        throw new CliArgumentException("'--issue' must be a positive issue number.");
                    issue = parsed;
                    break;
                case "--project": project = args[i]; break;
                case "--declared-size": size = args[i]; break;
                case "--size-rationale": rationale = args[i]; break;
                case "--spec": spec = args[i]; break;
                case "--scope": scope = args[i]; break;
                case "--adapter": adapter = args[i]; break;
                case "--model": model = args[i]; break;
                case "--effort": effort = args[i]; break;
                case "--reason": reason = args[i]; break;
            }
        }

        if (issue is null || project is null || size is null || rationale is null)
            throw new CliArgumentException(Usage);

        if (scope is not null)
        {
            if (!QueueTierTable.ScopeClasses.Contains(scope, StringComparer.OrdinalIgnoreCase))
                throw new CliArgumentException(
                    $"Unknown scope class '{scope}'. Pass one of: {string.Join(", ", QueueTierTable.ScopeClasses)}.");
            scope = scope.ToLowerInvariant();
        }

        var hasImplementationAxis = adapter is not null || model is not null || effort is not null;
        if (reason is not null && (scope is null || !hasImplementationAxis))
            throw new CliArgumentException("'--reason' requires '--scope' and at least one explicit implement axis.");
        if (scope is not null && hasImplementationAxis && string.IsNullOrWhiteSpace(reason))
            throw new CliArgumentException(
                "An explicit adapter, model or effort combined with '--scope' requires a non-blank '--reason'.");

        TaskSizeDeclaration declaration;
        if (string.Equals(size, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            // Explicit unknown is a caller declaration, distinct from absent legacy metadata.
            declaration = new TaskSizeDeclaration(DeclaredTaskSize.Unknown, rationale.Trim());
        }
        else
        {
            try { declaration = TaskSizeDeclaration.Parse(size, rationale); }
            catch (ArgumentException ex) { throw new CliArgumentException(ex.Message); }
        }
        return new TaskOptions(TaskVerb.Submit, issue, project, declaration, spec,
            Adapter: adapter, Model: model, Effort: effort, ScopeClass: scope, Reason: reason);
    }
}
