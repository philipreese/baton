namespace Baton.Domain;

/// <summary>Secret-free subset of a grace checkpoint suitable for durable replay.</summary>
public sealed record GraceCheckpointEvidence(
    string Head,
    string BranchRef,
    string Remote,
    string MergeRef,
    string RemoteTip,
    string EndpointDigest,
    string EndpointConfigurationDigest,
    string WorkspaceIdentityDigest);

/// <summary>Original parent facts held pending while a grace child may still be running.</summary>
public sealed record GraceParentRecoveryEvidence(
    bool IsMonitorArrest,
    int ExitCode,
    CoreExitReason ExitReason,
    bool TerminalSuccessObserved,
    bool TerminalResultObserved,
    FlowEvent.ExecutionArrested? Arrested = null,
    string? StderrTail = null,
    GraceTimeoutGradingEvidence? TimeoutGrading = null);

/// <summary>
/// Secret-free grading inputs captured for timeout-parent replay after a grace child.
/// Paths identify the local workspace that was graded; remote endpoints and git configuration are
/// represented only by <see cref="GraceCheckpointEvidence"/> digests.
/// </summary>
public sealed record GraceTimeoutGradingEvidence(
    IReadOnlyList<ProducedOutput> ProducedOutputs,
    IReadOnlyList<string> OptionalMetadata,
    bool ChangesTree,
    bool VerifiesWorkspace,
    string WorkspacePath,
    bool IsWorktree,
    string? WorktreeBaseRef,
    GraceTimeoutFailureEvidence? TimeoutFailure = null);

/// <summary>
/// Derived, secret-free adapter failure classification for the already-observed timeout. A present
/// record with <see cref="Matched"/> false means the original classifier was available but found no
/// quota/failure signal; null means no original classifier was available.
/// </summary>
public sealed record GraceTimeoutFailureEvidence(
    bool Matched,
    FailureClassification? Classification,
    DateTimeOffset? RetryNotBefore);
