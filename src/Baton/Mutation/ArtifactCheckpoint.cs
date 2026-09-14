namespace Baton.Mutation;

/// <summary>The fixed, smaller budget for the one artifact-only recovery turn after a normal cap arrest.</summary>
public static class ArtifactCheckpoint
{
    public const long TokenBudget = 8_000;
    public const int MaxToolSteps = 3;
    public static readonly TimeSpan WallClockTimeout = TimeSpan.FromMinutes(1);
    public const string PromptText =
        "The execution cap was reached. Write only the missing declared artifacts using baton_write_output. "
        + "Synthesize honestly from the context already available; do not claim unobserved evidence. "
        + "Mark required uncertainty or incompleteness in the artifact, then stop. Do not read, run commands, "
        + "edit the workspace, commit, push, or open a pull request.";
}
