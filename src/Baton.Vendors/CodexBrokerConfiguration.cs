using Baton.Domain;
using Baton.Queue;

namespace Baton.Vendors;

/// <summary>
/// Static, non-secret launch configuration for Baton's Codex app-server broker. The adapter writes
/// this beside an execution's outputs; execution-specific input/output paths remain in Baton's
/// existing environment variables and are resolved only by the broker process.
/// </summary>
public sealed record CodexBrokerConfiguration(
    string? WorkingDirectory,
    string? Model,
    string? Effort,
    string? SessionId,
    bool ResumeSession,
    PermissionGrant PermissionGrant,
    IReadOnlyList<string> ProducedOutputNames,
    bool AllowsSubagents,
    GhPullRequestCreateProvenance? PullRequestCreateProvenance = null,
    OriginatingPullRequestOwnership? OriginatingPullRequestOwnership = null,
    CodexMemoryAddHostAuthority? MemoryAddAuthority = null,
    CodexExactFileRestoreHostAuthority? ExactFileRestoreAuthority = null,
    IReadOnlyList<string>? AttachmentPaths = null);

/// <summary>Host-materialized identity for the one brokered worker memory write.</summary>
public sealed record CodexMemoryAddHostAuthority(
    string RoomDirectory,
    string ArtifactsRoot,
    string OutputDirectory,
    MemoryAddDispatchGrant Grant);

/// <summary>Host-materialized authority for Codex's exact-file restore dynamic tool.</summary>
public sealed record CodexExactFileRestoreHostAuthority(
    string WorkspaceDirectory,
    string BaseRevision,
    string OutputDirectory);

public sealed record CodexExactFileRestoreInvocation(
    string Path,
    bool AcknowledgeDirtyFile,
    CodexExactFileRestoreHostAuthority HostAuthority);

public sealed record CodexExactFileRestoreExecution(bool Success, string Output);
