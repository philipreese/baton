namespace Baton.Cli;

/// <summary>Which <c>baton queue</c> sub-verb was typed.</summary>
public enum QueueVerb
{
    Add,
    List,
    Hold,
    Resume,
    Import,
}

/// <summary>
/// Parsed arguments for <c>baton queue</c> (#1934 slice 1). Just the inputs —
/// <see cref="QueueCommand"/> does the work, and <see cref="QueueOptionsParser"/> does every
/// validation that can be done without touching the filesystem.
/// </summary>
/// <param name="Verb">The sub-verb.</param>
/// <param name="Tag">The item tag, for <see cref="QueueVerb.Add"/>. Already
/// <c>Baton.Queue.QueueTag</c>-validated by the parser.</param>
/// <param name="Role">The worker role, for <see cref="QueueVerb.Add"/>.</param>
/// <param name="SpecFilePath">The operator's spec file, copied into baton's own specs directory at add time.</param>
/// <param name="Issue">
/// <c>--issue &lt;n&gt;</c>: provision a worktree from this GitHub issue at ADD time, not at launch
/// time. Deliberate — an operator queueing eight items at 23:00 finds out immediately that issue 1940
/// does not exist, rather than at 04:00 when the scheduler reaches it. Mutually exclusive with
/// <paramref name="WorkspaceDirectory"/>.
/// </param>
/// <param name="WorkspaceDirectory">An already-existing directory the worker runs in. Mutually exclusive with <paramref name="Issue"/>.</param>
/// <param name="ScopeClass">The tier table's scope class; validated against <c>QueueTierTable.ScopeClasses</c> by the parser.</param>
/// <param name="Adapter">Explicit adapter, overriding the tier's.</param>
/// <param name="Model">Explicit model, overriding the tier's. Never promoted or substituted — <c>QueueTierTable</c>'s remarks say why.</param>
/// <param name="Effort">Explicit effort, overriding the tier's.</param>
/// <param name="TimeoutMinutes">Forwarded as <c>baton dispatch --timeout</c>.</param>
/// <param name="MaxToolSteps">Forwarded as <c>baton dispatch --max-tool-steps</c>.</param>
/// <param name="TokenBudget">Forwarded as <c>baton dispatch --token-budget</c>.</param>
/// <param name="OverrideRunwayReason">Forwarded as <c>baton dispatch --override-runway</c>; the reason is mandatory when the flag is used.</param>
/// <param name="Reason">Why the item's axes differ from its tier. Mandatory when any of adapter/model/effort is set alongside a scope class.</param>
/// <param name="ImportFilePath">The scratchpad <c>queue.json</c> to import, for <see cref="QueueVerb.Import"/>.</param>
public sealed record QueueOptions(
    QueueVerb Verb,
    string? Tag = null,
    string? Role = null,
    string? SpecFilePath = null,
    int? Issue = null,
    string? WorkspaceDirectory = null,
    string? ScopeClass = null,
    string? Adapter = null,
    string? Model = null,
    string? Effort = null,
    int? TimeoutMinutes = null,
    int? MaxToolSteps = null,
    long? TokenBudget = null,
    string? OverrideRunwayReason = null,
    string? Reason = null,
    string? ImportFilePath = null);
