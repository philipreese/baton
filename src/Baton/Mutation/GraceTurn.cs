using Baton.Domain;
using Baton.Dispatch;

namespace Baton.Mutation;

/// <summary>
/// #2134: the caps and prompt for the one bounded extra turn a workspace-verifying execution gets on a
/// budget arrest or a dirty wall-clock timeout — <c>spec/baton.md</c> §3's "The grace turn" is the
/// canonical account of why and what it does and does not change; not restated here. Wired at
/// <c>MutationInterface.RunGraceTurnAsync</c>. Fixed constants, not per-role configuration.
/// </summary>
public static class GraceTurn
{
    public const string LimitSource = "grace-turn";

    /// <summary>
    /// The grace dispatch's own <see cref="TokenBudgetMonitor"/> ceiling — independent of, and far
    /// below, whatever budget the arrested execution itself just crossed. One bounded reply on an
    /// fresh checkpoint context, not a second attempt at the original task. Cache reuse is not assumed.
    /// </summary>
    public const long TokenBudget = 30_000;

    /// <summary>The grace dispatch's own tool-step ceiling — enough for one bounded handoff, not a loop.</summary>
    public const int MaxToolSteps = 20;

    /// <summary>The grace dispatch's own wall-clock ceiling.</summary>
    public static readonly TimeSpan WallClockTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Records only fixed grace policy and whether its actual usage monitor exists.</summary>
    public static ExecutionLimitEvidence CreateLimitEvidence(bool monitorInputsKnown) =>
        new(
            WallClockTimeout,
            monitorInputsKnown ? TokenBudget : null,
            monitorInputsKnown ? MaxToolSteps : null,
            BilledRateLimit: null,
            TimeoutSource: LimitSource,
            TokenBudgetSource: monitorInputsKnown ? LimitSource : null,
            MaxToolStepsSource: monitorInputsKnown ? LimitSource : null,
            MonitorInputsKnown: monitorInputsKnown);

    /// <summary>
    /// Builds the bounded checkpoint/handoff instruction. The original task and both execution-scoped
    /// outboxes are supplied by the engine; the engine placement inventory is included only when its
    /// recorded digest still proves the bytes are unchanged at prompt construction time.
    /// </summary>
    public static string BuildPrompt(
        string originalTaskContext,
        string parentOutputDirectory,
        string childOutputDirectory,
        IReadOnlyCollection<EnginePlacedFile> enginePlacedFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalTaskContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentOutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(childOutputDirectory);
        ArgumentNullException.ThrowIfNull(enginePlacedFiles);

        var unchangedEngineFiles = enginePlacedFiles
            .Where(file => file is not null && file.StillMatchesPlacedBytes())
            .Select(file => $"- {file.Path} (sha256: {file.Sha256})")
            .ToArray();
        var engineFiles = unchangedEngineFiles.Length == 0
            ? "(No engine placement is currently hash-proven unchanged.)"
            : string.Join(Environment.NewLine, unchangedEngineFiles);

        return $"""
You are the one bounded grace checkpoint for an arrested task. This is a fresh handoff pass, not a new implementation grant and not a vendor-session resume. Inspect the committed source, current branch history, and the original handoff before judging what remains.

Original task context:
--- BEGIN ORIGINAL TASK CONTEXT ---
{originalTaskContext}
--- END ORIGINAL TASK CONTEXT ---

The parent's exact declared outbox is:
{parentOutputDirectory}
The child's exact declared outbox is:
{childOutputDirectory}
Read the parent outbox before judging the task. Preserve the parent's files and write this child's `changes.md` handoff only inside the child's outbox. Distinguish work already completed by the parent from work that is genuinely unfinished.

The following engine-placed paths are hash-proven unchanged at this prompt and are engine scaffolding, not task work:
{engineFiles}

Preserve existing commits and the current branch. Create and push one new commit only when genuine task work is still uncommitted; never stage unchanged engine files listed above. Never amend, reset, rebase, force-push, or rewrite existing history. Write no handoff outside the child's outbox. If no genuine task work remains, leave the source and commits alone and record an accurate handoff under the child's outbox, then stop.
""";
    }
}
