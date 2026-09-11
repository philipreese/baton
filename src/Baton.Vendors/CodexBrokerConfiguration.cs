using Baton.Domain;

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
    GhPullRequestCreateProvenance? PullRequestCreateProvenance = null);
