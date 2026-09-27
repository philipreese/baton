namespace Baton.Domain;

/// <summary>Secret-free subset of a grace checkpoint suitable for durable replay.</summary>
public sealed record GraceCheckpointEvidence(
    string Head,
    string BranchRef,
    string Remote,
    string MergeRef,
    string RemoteTip,
    string EndpointDigest,
    string EndpointConfigurationDigest);

/// <summary>Original parent facts held pending while a grace child may still be running.</summary>
public sealed record GraceParentRecoveryEvidence(
    bool IsMonitorArrest,
    int ExitCode,
    CoreExitReason ExitReason,
    bool TerminalSuccessObserved,
    bool TerminalResultObserved,
    FlowEvent.ExecutionArrested? Arrested = null,
    string? StderrTail = null);
