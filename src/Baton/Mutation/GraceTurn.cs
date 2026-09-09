namespace Baton.Mutation;

/// <summary>
/// #2134: the caps and prompt for the one bounded extra turn a workspace-verifying execution gets on a
/// budget arrest or a dirty wall-clock timeout — <c>spec/baton.md</c> §3's "The grace turn" is the
/// canonical account of why and what it does and does not change; not restated here. Wired at
/// <c>MutationInterface.RunGraceTurnAsync</c>. Fixed constants, not per-role configuration.
/// </summary>
public static class GraceTurn
{
    /// <summary>
    /// The grace dispatch's own <see cref="TokenBudgetMonitor"/> ceiling — independent of, and far
    /// below, whatever budget the arrested execution itself just crossed. One bounded reply on an
    /// already-cached context, not a second attempt at the original task.
    /// </summary>
    public const long TokenBudget = 30_000;

    /// <summary>The grace dispatch's own tool-step ceiling — enough for a `git add`/`commit`/`push`/`changes.md` sequence, not a loop.</summary>
    public const int MaxToolSteps = 20;

    /// <summary>The grace dispatch's own wall-clock ceiling.</summary>
    public static readonly TimeSpan WallClockTimeout = TimeSpan.FromMinutes(3);

    /// <summary>The exact instruction the grace dispatch is spawned with — see `spec/baton.md` §3 for why it needs no session resume.</summary>
    public const string PromptText =
        "Budget reached. Commit everything staged and unstaged on the current branch with a " +
        "conventional subject that says the work is incomplete, push the branch, write `changes.md` " +
        "naming what is done and what is not, then stop. No other action.";
}
