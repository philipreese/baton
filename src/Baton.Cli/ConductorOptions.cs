namespace Baton.Cli;

/// <summary>
/// Sub-verbs for <c>baton conductor</c> (#2296).
/// </summary>
public enum ConductorVerb
{
    Claim,
    List,
    Release,
    Takeover,
}

/// <summary>
/// Parsed options for <c>baton conductor</c> commands (#2296).
/// </summary>
public sealed record ConductorOptions(
    ConductorVerb Verb,
    string? Holder = null,
    string? Workspace = null,
    string? Reason = null,
    bool Json = false);
