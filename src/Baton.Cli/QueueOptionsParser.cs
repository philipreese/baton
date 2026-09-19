using System.Globalization;
using Baton.Domain;
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
        "[--lifecycle [--stage implement|review|fix|re-review|continue] | --lifecycle-pin] " +
        $"[--declared-size <{TaskSizeDeclaration.Usage}>] [--size-rationale <clause>] " +
        "[--scope engine|tooling|docs] [--adapter <a>] [--model <m>] [--effort <e>] " +
        "[--skill <name>] [--require repository-read|file-write|shell|network|github-read|github-write|artifact:<output-name>] " +
        "[--timeout <minutes>] " +
        "[--max-tool-steps <n>] [--token-budget <n>] [--override-runway <reason>] [--reason <why>] | " +
        "baton queue list [--active] | baton queue worktrees [--apply] [--format text|json] | baton queue hold | baton queue resume | baton queue cancel <tag> | baton queue retire <tag> --reason <text> [--merged-pr <n>] | baton queue restore <tag> --reason <text> | baton queue import <file>. " +
        "A worktree provisioned by --issue inherits its repository's recorded ceiling; " +
        "when no path in that repository is trusted it is recorded at 'all', and the add says which.";

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
            "list" => ParseList(args),
            "worktrees" => ParseWorktrees(args),
            "hold" => ParseBare(QueueVerb.Hold, args),
            "resume" => ParseBare(QueueVerb.Resume, args),
            "cancel" => ParseCancel(args),
            "retire" => ParseRetirement(QueueVerb.Retire, args),
            "restore" => ParseRetirement(QueueVerb.Restore, args),
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

    private static QueueOptions ParseList(IReadOnlyList<string> args)
    {
        if (args.Count == 1)
        {
            return new QueueOptions(QueueVerb.List);
        }

        if (args.Count == 2 && args[1] == "--active")
        {
            return new QueueOptions(QueueVerb.List, Active: true);
        }

        throw new CliArgumentException(
            $"'baton queue list' takes no arguments or '--active' (got '{args[1]}'). {Usage}");
    }

    private static QueueOptions ParseImport(IReadOnlyList<string> args)
    {
        if (args.Count != 2)
        {
            throw new CliArgumentException($"'baton queue import' takes exactly one file path. {Usage}");
        }

        return new QueueOptions(QueueVerb.Import, ImportFilePath: args[1]);
    }

    private static QueueOptions ParseWorktrees(IReadOnlyList<string> args)
    {
        if (args.Count == 1) return new QueueOptions(QueueVerb.Worktrees);
        var apply = false;
        var formatSpecified = false;
        var format = QueueWorktreesOutputFormat.Text;
        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--apply" when !apply:
                    apply = true;
                    break;
                case "--format" when !formatSpecified && i + 1 < args.Count:
                    formatSpecified = true;
                    format = args[++i] switch
                    {
                        "text" => QueueWorktreesOutputFormat.Text,
                        "json" => QueueWorktreesOutputFormat.Json,
                        _ => throw new CliArgumentException($"'--format' must be 'text' or 'json', got '{args[i]}'. {Usage}"),
                    };
                    break;
                default:
                    throw new CliArgumentException($"'baton queue worktrees' takes '--apply' and/or '--format text|json'. {Usage}");
            }
        }

        return new QueueOptions(QueueVerb.Worktrees, Format: format, Apply: apply);
    }

    private static QueueOptions ParseCancel(IReadOnlyList<string> args)
    {
        if (args.Count != 2)
        {
            throw new CliArgumentException($"'baton queue cancel' takes exactly one queue tag. {Usage}");
        }

        if (!QueueTag.IsValid(args[1]))
        {
            throw new CliArgumentException(
                $"'{args[1]}' is not a usable queue tag ({QueueTag.Rule}).",
                "pass the tag shown by 'baton queue list'.");
        }

        return new QueueOptions(QueueVerb.Cancel, Tag: args[1]);
    }

    private static QueueOptions ParseRetirement(QueueVerb verb, IReadOnlyList<string> args)
    {
        if (args.Count < 4)
        {
            throw new CliArgumentException($"'baton queue {args[0]}' takes a tag and nonblank '--reason <text>'. {Usage}");
        }
        if (!QueueTag.IsValid(args[1]))
        {
            throw new CliArgumentException($"'{args[1]}' is not a usable queue tag ({QueueTag.Rule}).");
        }

        string? reason = null;
        int? mergedPullRequest = null;
        var i = 2;
        while (i < args.Count)
        {
            switch (args[i])
            {
                case "--reason":
                    reason = TakeValue(args, ref i, "--reason");
                    break;
                case "--merged-pr":
                    if (verb != QueueVerb.Retire)
                    {
                        throw new CliArgumentException("'--merged-pr' applies only to 'baton queue retire'. " + Usage);
                    }

                    mergedPullRequest = TakeInt(args, ref i, "--merged-pr");
                    break;
                default:
                    throw new CliArgumentException(
                        $"Unexpected queue {args[0]} argument '{args[i]}'. {Usage}");
            }
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new CliArgumentException($"'baton queue {args[0]}' takes a tag and nonblank '--reason <text>'. {Usage}");
        }

        if (mergedPullRequest is <= 0)
        {
            throw new CliArgumentException("'--merged-pr' must be a positive pull-request number. " + Usage);
        }

        return new QueueOptions(
            verb, Tag: args[1], Reason: reason.Trim(), MergedPullRequest: mergedPullRequest);
    }

    private static QueueOptions ParseAdd(IReadOnlyList<string> args)
    {
        string? tag = null;
        string? role = null, spec = null, workspace = null, scope = null;
        string? adapter = null, model = null, effort = null, overrideRunway = null, reason = null;
        string? declaredSize = null, sizeRationale = null;
        int? issue = null, timeout = null, maxToolSteps = null;
        long? tokenBudget = null;
        var lifecycle = false;
        var lifecyclePin = false;
        var skills = new List<string>();
        var requirements = new List<string>();
        WorkStage? selectedStage = null;
        var stageSelections = new Dictionary<WorkStage, QueueStageSelection>();

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
                    SetAdapter(selectedStage, stageSelections, TakeValue(args, ref i, "--adapter"), ref adapter);
                    continue;
                case "--model":
                    SetModel(selectedStage, stageSelections, TakeValue(args, ref i, "--model"), ref model);
                    continue;
                case "--effort":
                    SetEffort(selectedStage, stageSelections, TakeValue(args, ref i, "--effort"), ref effort);
                    continue;
                case "--declared-size":
                    declaredSize = TakeValue(args, ref i, "--declared-size");
                    continue;
                case "--size-rationale":
                    sizeRationale = TakeValue(args, ref i, "--size-rationale");
                    continue;
                case "--reason":
                    SetReason(selectedStage, stageSelections, TakeValue(args, ref i, "--reason"), ref reason);
                    continue;
                case "--skill":
                    skills.Add(TakeValue(args, ref i, "--skill"));
                    continue;
                case "--require":
                    SetRequirement(
                        selectedStage,
                        stageSelections,
                        TakeValue(args, ref i, "--require"),
                        requirements);
                    continue;
                case "--stage":
                    selectedStage = ParseStage(TakeValue(args, ref i, "--stage"));
                    stageSelections.TryAdd(selectedStage.Value, new QueueStageSelection { Stage = selectedStage.Value });
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
                case "--lifecycle":
                    lifecycle = true;
                    i++;
                    continue;
                case "--lifecycle-pin":
                    lifecyclePin = true;
                    i++;
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

        // The lifecycle relaxations, all four in one place (#1934 slice 2, spec/baton.md §13 "Work
        // items"). A work item is anchored on an issue, so `--issue` is the one thing it cannot do
        // without; the STAGE picks its role, so naming one would be a second answer that the first
        // advance would silently overwrite; and the tag defaults to the branch's own name so the
        // ordinary invocation is `baton queue add --issue 1934 --lifecycle` and nothing more.
        if (lifecycle)
        {
            if (declaredSize is null || sizeRationale is null)
            {
                throw new CliArgumentException($"'--lifecycle' requires '--declared-size <{TaskSizeDeclaration.Usage}>' and '--size-rationale <clause>'.");
            }

            if (issue is null)
            {
                throw new CliArgumentException(
                    "'--lifecycle' adds an issue-anchored work item, so it needs '--issue <n>' — the issue is "
                    + "what its briefs are rendered from and what its PR is looked for on.",
                    "pass '--issue <n>', or drop '--lifecycle' to queue a single dispatch request.");
            }

            if (role is not null)
            {
                throw new CliArgumentException(
                    "'--role' and '--lifecycle' are two answers to the same question: a work item's role comes "
                    + "from its stage (implement, then review, then fix, then re-review), so a role named here "
                    + "would be replaced at the first advance.",
                    "drop '--role'.");
            }

            tag ??= $"{issue}-lane";
            role = WorkStages.RoleFor(WorkStage.Implement);
        }

        if (selectedStage is not null && !lifecycle)
        {
            throw new CliArgumentException("'--stage' selects lifecycle-stage axes, so it requires '--lifecycle'.");
        }

        if (lifecyclePin && !lifecycle)
        {
            throw new CliArgumentException("'--lifecycle-pin' applies only to a '--lifecycle' work item.");
        }

        if (lifecycle && skills.Count > 0)
        {
            throw new CliArgumentException(
                "Explicit '--skill <name>' declarations are not supported for '--lifecycle' work items yet: "
                + "the queue has no declared policy for whether skills apply to one stage or the whole lifecycle.",
                "drop '--skill', or queue an ordinary single-dispatch item with '--role'.");
        }

        if (lifecyclePin && selectedStage is not null)
        {
            throw new CliArgumentException(
                "'--lifecycle-pin' and '--stage' are different selection scopes; choose one so the effective choice is unambiguous.");
        }

        if (lifecyclePin && adapter is null && model is null && effort is null)
        {
            throw new CliArgumentException(
                "'--lifecycle-pin' needs at least one of '--adapter', '--model' or '--effort' to pin.");
        }

        if (lifecycle && !lifecyclePin)
        {
            // Bare axes on a new work item deliberately mean the initial implement stage only. The
            // list is written even when empty, which keeps an old persisted row distinguishable from
            // a new item that intentionally leaves every stage to its tier.
            if (adapter is not null || model is not null || effort is not null || reason is not null)
            {
                if (stageSelections.TryGetValue(WorkStage.Implement, out var existing)
                    && (existing.Adapter is not null || existing.Model is not null || existing.Effort is not null
                        || existing.Reason is not null))
                {
                    throw new CliArgumentException(
                        "Implement-stage axes were supplied both before and after '--stage implement'; state each axis once.");
                }

                stageSelections[WorkStage.Implement] = new QueueStageSelection
                {
                    Stage = WorkStage.Implement,
                    Adapter = adapter,
                    Model = model,
                    Effort = effort,
                    Reason = reason,
                };
            }

            adapter = null;
            model = null;
            effort = null;
            reason = null;
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

        // A lifecycle item with no --spec renders its implement brief from the issue body; one WITH a
        // --spec puts that file's text in the brief's "## Do" section. Either way the item launches a
        // rendered brief, so the flag is optional here and required everywhere else.
        if (string.IsNullOrWhiteSpace(spec) && !lifecycle)
        {
            throw new CliArgumentException($"Missing required '--spec <file>'. {Usage}");
        }

        if (issue is null && string.IsNullOrWhiteSpace(workspace))
        {
            throw new CliArgumentException(
                $"An item needs somewhere to run: pass '--issue <n>' to provision a worktree now, or "
                + $"'--workspace <dir>' to name one that already exists. {Usage}");
        }

        if (issue is not null && !string.IsNullOrWhiteSpace(workspace) && !lifecycle)
        {
            throw new CliArgumentException(
                "'--issue' and '--workspace' are two answers to the same question — '--issue' provisions the "
                + "worktree the item runs in, so a workspace passed alongside it would be silently discarded.",
                "drop one of them, or add '--lifecycle' to explicitly reuse the exact retained issue worktree.");
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

        var normalizedStageSelections = stageSelections.Values.Select(selection =>
        {
            if (selection.Requirements is null)
            {
                return selection;
            }

            try
            {
                return selection with { Requirements = TaskRequirements.Normalize(selection.Requirements) };
            }
            catch (ArgumentException ex)
            {
                throw new CliArgumentException(ex.Message, "pass a supported --require value, or remove the flag.");
            }
        }).ToList();

        foreach (var selection in normalizedStageSelections)
        {
            if (selection.Adapter is null && selection.Model is null && selection.Effort is null
                && selection.Requirements is null)
            {
                throw new CliArgumentException(
                    $"'--stage {WorkStages.Token(selection.Stage)}' needs at least one of '--adapter', '--model' or '--effort'.");
            }

            if (scope is not null && string.IsNullOrWhiteSpace(selection.Reason))
            {
                throw new CliArgumentException(
                    $"A '{WorkStages.Token(selection.Stage)}' stage selection that overrides its tier needs '--reason <why>'.");
            }
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

        if (maxToolSteps < 0)
        {
            throw new CliArgumentException($"'--max-tool-steps' must be a non-negative whole number. {Usage}");
        }

        if (tokenBudget is <= 0)
        {
            throw new CliArgumentException($"'--token-budget' must be positive. {Usage}");
        }

        if (issue is <= 0)
        {
            throw new CliArgumentException($"'--issue' must be a positive issue number. {Usage}");
        }

        IReadOnlyList<string> normalizedRequirements;
        try
        {
            normalizedRequirements = TaskRequirements.Normalize(requirements);
        }
        catch (ArgumentException ex)
        {
            throw new CliArgumentException(ex.Message, "pass a supported --require value, or remove the flag.");
        }

        return new QueueOptions(
            QueueVerb.Add, tag, role, spec, issue, workspace, scope, adapter, model, effort,
            timeout, maxToolSteps, tokenBudget, overrideRunway, reason, ImportFilePath: null, Lifecycle: lifecycle,
            StageSelections: lifecycle ? normalizedStageSelections : null, LifecyclePin: lifecyclePin,
            Skills: DispatchOptionsParser.NormalizeSkills(skills), Requirements: normalizedRequirements,
            DeclaredTaskSize: DispatchOptionsParser.ParseTaskSizeDeclaration(declaredSize, sizeRationale));
    }

    private static void SetRequirement(
        WorkStage? stage,
        IDictionary<WorkStage, QueueStageSelection> selections,
        string value,
        ICollection<string> ordinaryRequirements)
    {
        if (stage is not { } selected)
        {
            ordinaryRequirements.Add(value);
            return;
        }

        selections.TryGetValue(selected, out var existing);
        selections[selected] = (existing ?? new QueueStageSelection { Stage = selected }) with
        {
            Requirements = (existing?.Requirements ?? []).Append(value).ToList(),
        };
    }

    private static void SetAdapter(
        WorkStage? stage, IDictionary<WorkStage, QueueStageSelection> selections, string value, ref string? ordinaryValue)
    {
        if (stage is not { } selected)
        {
            ordinaryValue = value;
            return;
        }

        selections.TryGetValue(selected, out var existing);
        selections[selected] = (existing ?? new QueueStageSelection { Stage = selected }) with { Adapter = value };
    }

    private static void SetModel(
        WorkStage? stage, IDictionary<WorkStage, QueueStageSelection> selections, string value, ref string? ordinaryValue)
    {
        if (stage is not { } selected)
        {
            ordinaryValue = value;
            return;
        }

        selections.TryGetValue(selected, out var existing);
        selections[selected] = (existing ?? new QueueStageSelection { Stage = selected }) with { Model = value };
    }

    private static void SetEffort(
        WorkStage? stage, IDictionary<WorkStage, QueueStageSelection> selections, string value, ref string? ordinaryValue)
    {
        if (stage is not { } selected)
        {
            ordinaryValue = value;
            return;
        }

        selections.TryGetValue(selected, out var existing);
        selections[selected] = (existing ?? new QueueStageSelection { Stage = selected }) with { Effort = value };
    }

    private static void SetReason(
        WorkStage? stage, IDictionary<WorkStage, QueueStageSelection> selections, string value, ref string? ordinaryValue)
    {
        if (stage is not { } selected)
        {
            ordinaryValue = value;
            return;
        }

        selections.TryGetValue(selected, out var existing);
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
