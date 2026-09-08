using Baton;

namespace Baton.Vendors;

/// <summary>
/// Raised by <see cref="ProjectCeilingGate.Apply"/> when a worker invocation's
/// <see cref="WorkerInvocation.WorkingDirectory"/> carries no recorded entry in
/// <see cref="ProjectCeilingStore"/> — decision 0004's project ceiling, with no interactive prompt to
/// fall back on in a headless dispatch (#1166's scope ruling). Fails closed before any worker spawns,
/// the same "refuse rather than ask" posture <see cref="IncoherentPermissionGrantException"/> already
/// takes for a different 0004 rule.
/// </summary>
public sealed class ProjectNotTrustedException : BatonFlowException
{
    public string ProjectPath { get; }

    public ProjectNotTrustedException(string projectPath)
        : base(
            $"'{projectPath}' has no recorded permission ceiling. Decision 0004's project scope has no " +
            "interactive trust prompt in a headless dispatch — the operator verb is 'baton trust' — so " +
            "dispatching against an unseen project directory fails closed rather than silently spawning " +
            "under an unbounded role grant.")
    {
        ProjectPath = projectPath;
        TryInvocation =
            $"baton trust \"{projectPath}\" --ceiling all (or a comma-separated subset of " +
            "ReadFiles,WriteFiles,RunShellCommands,NetworkAccess), then re-run the dispatch.";
    }

    /// <summary>
    /// The provisioning-side refusal (#2076 re-review): a workspace whose ceiling would have been
    /// inherited, except that the repository-identity probe answered nothing. Refuses rather than
    /// taking the <c>all</c> fallback — spec/baton.md §13 states why the two are not the same case.
    /// </summary>
    /// <param name="projectPath">The workspace that was being trusted.</param>
    /// <param name="probeFailure">What the probe could not do, in the caller's own words.</param>
    public ProjectNotTrustedException(string projectPath, string probeFailure)
        : base(
            $"'{projectPath}' was not given a permission ceiling: {probeFailure} A ceiling that would " +
            "have been inherited cannot be, and the unrestricted fallback is refused rather than taken " +
            "on a probe failure, so the workspace fails closed.")
    {
        ProjectPath = projectPath;
        TryInvocation =
            $"check that 'git' is on PATH and that '{projectPath}' is a git checkout, then retry — or " +
            $"baton trust \"{projectPath}\" --ceiling all (or a comma-separated subset of " +
            "ReadFiles,WriteFiles,RunShellCommands,NetworkAccess) to record one by hand.";
    }

    /// <summary>
    /// The unidentifiable-candidate refusal (#2121): the workspace itself probed fine, but git could
    /// not identify <paramref name="candidatePath"/> and nothing live matched (the lookup's
    /// <c>CandidateUnknown</c> outcome; spec/baton.md §9 has the rule). The remedy names the candidate,
    /// not the workspace, because the workspace is not what failed: checking that it is a git checkout
    /// would send the operator to the wrong directory.
    /// </summary>
    /// <param name="projectPath">The workspace that was being trusted.</param>
    /// <param name="candidatePath">The recorded path the probe could not identify.</param>
    /// <param name="probeFailure">What that probe could not do, in the lookup's own words.</param>
    public ProjectNotTrustedException(string projectPath, string candidatePath, string probeFailure)
        : base(
            $"'{projectPath}' was not given a permission ceiling: recorded path '{candidatePath}' could not be " +
            $"identified ({probeFailure}), so whether this repository was never trusted or was revoked is unknown. " +
            "The unrestricted fallback is refused rather than taken on an unidentified record, so the workspace fails closed.")
    {
        ProjectPath = projectPath;
        CandidatePath = candidatePath;
        TryInvocation =
            $"repair '{candidatePath}' so 'git' can identify it, or baton trust \"{candidatePath}\" --forget to drop " +
            $"its record if that checkout is gone, then retry — or baton trust \"{projectPath}\" --ceiling all (or a " +
            "comma-separated subset of ReadFiles,WriteFiles,RunShellCommands,NetworkAccess) to record one by hand.";
    }

    /// <summary>The recorded path that could not be identified — set only by the unidentifiable-candidate refusal.</summary>
    public string? CandidatePath { get; }

    /// <summary>
    /// The revoked-repository refusal (#2121): <paramref name="projectPath"/> is, or is in the same
    /// repository as, a path whose ceiling <c>baton trust --revoke</c> withdrew, and no path of that
    /// repository has been trusted since. Refuses rather than falling back to <c>all</c> or reporting
    /// "never trusted" — spec/baton.md §9 states the revoked state once.
    /// </summary>
    /// <param name="projectPath">The workspace that was being trusted or dispatched into.</param>
    /// <param name="revokedPath">The tombstoned path that made the repository revoked — <paramref name="projectPath"/> itself, or a sibling.</param>
    /// <param name="revokedAt">When it was revoked.</param>
    public ProjectNotTrustedException(string projectPath, string revokedPath, DateTimeOffset revokedAt)
        : base(
            $"'{projectPath}' is in a repository whose ceiling was revoked: 'baton trust --revoke' withdrew " +
            $"'{revokedPath}' at {revokedAt:u} and no path of that repository has been trusted since. A revoked " +
            "repository is refused rather than treated as never trusted, so the unrestricted fallback is not taken.")
    {
        ProjectPath = projectPath;
        TryInvocation =
            $"baton trust \"{projectPath}\" --ceiling all (or a comma-separated subset of " +
            "ReadFiles,WriteFiles,RunShellCommands,NetworkAccess) to trust it again on purpose, then retry.";
    }
}
