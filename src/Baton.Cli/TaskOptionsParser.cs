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
    string? Reason = null,
    IReadOnlyList<QueueStageSelection>? StageSelections = null,
    int? TimeoutMinutes = null,
    int? MaxToolSteps = null,
    long? TokenBudget = null);

public static class TaskOptionsParser
{
    public const string Usage =
        "baton task submit --issue <number> --project <repository-directory> "
        + "--declared-size <small|medium|large|unknown> --size-rationale <clause> [--spec <file>] "
        + "[--scope <engine|tooling|docs>] [--adapter <name>] [--model <name>] [--effort <name>] [--reason <why>] "
        + "[--stage <implement|review|fix|re-review|continue>] [--timeout <minutes>] "
        + "[--max-tool-steps <n>] [--token-budget <n>]\n"
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
        int? timeoutMinutes = null, maxToolSteps = null;
        long? tokenBudget = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        WorkStage? selectedStage = null;
        var stageSelections = new Dictionary<WorkStage, QueueStageSelection>();
        var stageMarkerSeen = false;
        var stageSectionHasInput = false;
        for (var i = 1; i < args.Count; i++)
        {
            var flag = args[i];
            if (flag == "--stage")
            {
                if (i + 1 == args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
                    throw new CliArgumentException(Usage);
                if (selectedStage is not null && !stageSectionHasInput)
                    throw new CliArgumentException("A '--stage' section must select at least one axis or reason.");

                var rawStage = args[++i];
                selectedStage = ParseStage(rawStage);
                stageMarkerSeen = true;
                stageSectionHasInput = false;
                stageSelections.TryAdd(selectedStage.Value, new QueueStageSelection { Stage = selectedStage.Value });
                continue;
            }

            if (flag is "--adapter" or "--model" or "--effort" or "--reason")
            {
                if (i + 1 == args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
                    throw new CliArgumentException(Usage);
                var value = args[++i];
                switch (flag)
                {
                    case "--adapter": SetAdapter(selectedStage, stageSelections, value, ref adapter); break;
                    case "--model": SetModel(selectedStage, stageSelections, value, ref model); break;
                    case "--effort": SetEffort(selectedStage, stageSelections, value, ref effort); break;
                    case "--reason": SetReason(selectedStage, stageSelections, value, ref reason); break;
                }

                if (selectedStage is not null)
                    stageSectionHasInput = true;
                continue;
            }

            if (flag is not ("--issue" or "--project" or "--declared-size" or "--size-rationale" or "--spec"
                    or "--scope" or "--timeout" or "--max-tool-steps" or "--token-budget")
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
                case "--timeout":
                    if (!int.TryParse(args[i], out var parsedTimeout) || parsedTimeout <= 0)
                        throw new CliArgumentException("'--timeout' must be positive.");
                    timeoutMinutes = parsedTimeout;
                    break;
                case "--max-tool-steps":
                    if (!int.TryParse(args[i], out var parsedMaxToolSteps) || parsedMaxToolSteps <= 0)
                        throw new CliArgumentException("'--max-tool-steps' must be positive.");
                    maxToolSteps = parsedMaxToolSteps;
                    break;
                case "--token-budget":
                    if (!long.TryParse(args[i], out var parsedTokenBudget) || parsedTokenBudget <= 0)
                        throw new CliArgumentException("'--token-budget' must be positive.");
                    tokenBudget = parsedTokenBudget;
                    break;
            }
        }

        if (issue is null || project is null || size is null || rationale is null)
            throw new CliArgumentException(Usage);

        if (selectedStage is not null && !stageSectionHasInput)
            throw new CliArgumentException("A '--stage' section must select at least one axis or reason.");

        // Bare axes before the first marker retain their historical implement meaning. If the
        // caller also names implement, merge the two maps before queue admission can infer an
        // adapter; equivalent forms are one selection, conflicting forms are refused.
        if (stageMarkerSeen)
        {
            if (adapter is not null || model is not null || effort is not null || reason is not null)
            {
                stageSelections.TryGetValue(WorkStage.Implement, out var existing);
                stageSelections[WorkStage.Implement] = MergeImplement(
                    existing, adapter, model, effort, reason);
            }

            adapter = null;
            model = null;
            effort = null;
            reason = null;
        }

        scope = TaskSubmissionInput.NormalizeScopeClass(scope);
        if (stageMarkerSeen)
            TaskSubmissionInput.ValidateStageSelections(scope, stageSelections.Values);
        else
            TaskSubmissionInput.ValidateScopeAndReason(scope, adapter, model, effort, reason);

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
            Adapter: adapter, Model: model, Effort: effort, ScopeClass: scope, Reason: reason,
            StageSelections: stageMarkerSeen ? stageSelections.Values.ToList() : null,
            TimeoutMinutes: timeoutMinutes, MaxToolSteps: maxToolSteps, TokenBudget: tokenBudget);
    }

    private static QueueStageSelection MergeImplement(
        QueueStageSelection? existing, string? adapter, string? model, string? effort, string? reason)
    {
        existing ??= new QueueStageSelection { Stage = WorkStage.Implement };
        return existing with
        {
            Adapter = MergeAxis("adapter", existing.Adapter, adapter),
            Model = MergeAxis("model", existing.Model, model),
            Effort = MergeAxis("effort", existing.Effort, effort),
            Reason = MergeAxis("reason", existing.Reason, reason),
        };
    }

    private static string? MergeAxis(string axis, string? first, string? second)
    {
        if (first is not null && second is not null && !string.Equals(first, second, StringComparison.Ordinal))
            throw new CliArgumentException($"Implement-stage {axis} values conflict between bare and named input.");
        return first ?? second;
    }

    private static void SetAdapter(
        WorkStage? stage, IDictionary<WorkStage, QueueStageSelection> selections, string value, ref string? ordinaryValue)
    {
        if (stage is not { } selected)
        {
            if (ordinaryValue is not null) throw new CliArgumentException("'--adapter' may be supplied only once.");
            ordinaryValue = value;
            return;
        }

        selections.TryGetValue(selected, out var existing);
        if (existing?.Adapter is not null)
            throw new CliArgumentException($"'--adapter' may be supplied only once for stage '{WorkStages.Token(selected)}'.");
        selections[selected] = (existing ?? new QueueStageSelection { Stage = selected }) with { Adapter = value };
    }

    private static void SetModel(
        WorkStage? stage, IDictionary<WorkStage, QueueStageSelection> selections, string value, ref string? ordinaryValue)
    {
        if (stage is not { } selected)
        {
            if (ordinaryValue is not null) throw new CliArgumentException("'--model' may be supplied only once.");
            ordinaryValue = value;
            return;
        }

        selections.TryGetValue(selected, out var existing);
        if (existing?.Model is not null)
            throw new CliArgumentException($"'--model' may be supplied only once for stage '{WorkStages.Token(selected)}'.");
        selections[selected] = (existing ?? new QueueStageSelection { Stage = selected }) with { Model = value };
    }

    private static void SetEffort(
        WorkStage? stage, IDictionary<WorkStage, QueueStageSelection> selections, string value, ref string? ordinaryValue)
    {
        if (stage is not { } selected)
        {
            if (ordinaryValue is not null) throw new CliArgumentException("'--effort' may be supplied only once.");
            ordinaryValue = value;
            return;
        }

        selections.TryGetValue(selected, out var existing);
        if (existing?.Effort is not null)
            throw new CliArgumentException($"'--effort' may be supplied only once for stage '{WorkStages.Token(selected)}'.");
        selections[selected] = (existing ?? new QueueStageSelection { Stage = selected }) with { Effort = value };
    }

    private static void SetReason(
        WorkStage? stage, IDictionary<WorkStage, QueueStageSelection> selections, string value, ref string? ordinaryValue)
    {
        if (stage is not { } selected)
        {
            if (ordinaryValue is not null) throw new CliArgumentException("'--reason' may be supplied only once.");
            ordinaryValue = value;
            return;
        }

        selections.TryGetValue(selected, out var existing);
        if (existing?.Reason is not null)
            throw new CliArgumentException($"'--reason' may be supplied only once for stage '{WorkStages.Token(selected)}'.");
        selections[selected] = (existing ?? new QueueStageSelection { Stage = selected }) with { Reason = value };
    }

    private static WorkStage ParseStage(string value) => value switch
    {
        "implement" => WorkStage.Implement,
        "review" => WorkStage.Review,
        "fix" => WorkStage.Fix,
        "re-review" => WorkStage.ReReview,
        "continue" => WorkStage.Continue,
        "ready" => throw new CliArgumentException("'--stage ready' is invalid because ready never dispatches."),
        _ => throw new CliArgumentException(
            $"Unknown lifecycle stage '{value}'. Pass implement, review, fix, re-review or continue."),
    };
}

internal static class TaskSubmissionInput
{
    internal static string? NormalizeScopeClass(string? scopeClass)
    {
        if (scopeClass is null)
            return null;

        var normalized = scopeClass.Trim().ToLowerInvariant();
        if (!QueueTierTable.ScopeClasses.Contains(normalized, StringComparer.Ordinal))
            throw new CliArgumentException(
                $"Unknown scope class '{scopeClass}'. Pass one of: {string.Join(", ", QueueTierTable.ScopeClasses)}.");
        return normalized;
    }

    internal static void ValidateScopeAndReason(
        string? scopeClass, string? adapter, string? model, string? effort, string? reason)
    {
        var hasImplementationAxis = adapter is not null || model is not null || effort is not null;
        if (reason is not null && (scopeClass is null || !hasImplementationAxis))
            throw new CliArgumentException("'--reason' requires '--scope' and at least one explicit implement axis.");
        if (scopeClass is not null && hasImplementationAxis && string.IsNullOrWhiteSpace(reason))
            throw new CliArgumentException(
                "An explicit adapter, model or effort combined with '--scope' requires a non-blank '--reason'.");
    }

    internal static void ValidateStageSelections(
        string? scopeClass, IEnumerable<QueueStageSelection> selections)
    {
        var seen = new HashSet<WorkStage>();
        foreach (var selection in selections)
        {
            if (!seen.Add(selection.Stage) || WorkStages.IsTerminal(selection.Stage))
                throw new CliArgumentException($"Stage '{WorkStages.Token(selection.Stage)}' was selected more than once.");

            var hasAxis = selection.Adapter is not null || selection.Model is not null || selection.Effort is not null;
            if (!hasAxis)
                throw new CliArgumentException(
                    $"'--stage {WorkStages.Token(selection.Stage)}' needs at least one of '--adapter', '--model' or '--effort'.");
            ValidateScopeAndReason(scopeClass, selection.Adapter, selection.Model, selection.Effort, selection.Reason);
        }
    }
}
