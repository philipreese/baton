using Baton.Queue;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// Compares a queue task's declared needs with the live role catalog before the scheduler claims a
/// room or starts a vendor process. It deliberately reads capabilities from the effective role grant;
/// requirements describe demand and never widen that grant.
/// </summary>
internal static class TaskRequirementPreflight
{
    private const string GitHubReadProbe = "gh issue view 1";
    private const string GitHubWriteProbe = "gh issue comment 1 --body admission";

    internal static TaskRequirementAdmission Evaluate(
        QueueItem item, WorkerRole role, bool requireDeclaredRequirements)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(role);

        var effectiveGrant = EffectiveGrant(role.Grant, role.Outputs.Select(output => output.Name));
        if (item.Requirements is null)
        {
            var executionBearing = TaskRequirements.IsExecutionBearing(
                role.Grant.WriteFiles,
                role.Grant.RunShellCommands && !role.Grant.ShellCommandsAreReadOnly,
                role.Grant.NetworkAccess);
            return requireDeclaredRequirements && executionBearing
                ? new TaskRequirementAdmission(
                    null, effectiveGrant, TaskRequirementAdmission.Refused,
                    ["declared requirements"], VendorUsage: 0)
                : new TaskRequirementAdmission(null, effectiveGrant, TaskRequirementAdmission.Unknown);
        }

        IReadOnlyList<string> requested;
        try
        {
            requested = TaskRequirements.Normalize(item.Requirements);
        }
        catch (ArgumentException ex)
        {
            return new TaskRequirementAdmission(
                item.Requirements, effectiveGrant, TaskRequirementAdmission.Refused,
                [ex.Message], VendorUsage: 0);
        }

        var granted = effectiveGrant.ToHashSet(StringComparer.Ordinal);
        var missing = requested.Where(requirement => !granted.Contains(requirement)).ToList();
        return missing.Count == 0
            ? new TaskRequirementAdmission(requested, effectiveGrant, TaskRequirementAdmission.Admitted)
            : new TaskRequirementAdmission(requested, effectiveGrant, TaskRequirementAdmission.Refused, missing, VendorUsage: 0);
    }

    /// <summary>
    /// Direct dispatch has already materialized a binding, whose grant can be narrower than the raw
    /// role declaration. Compare against that actual runtime grant, not a second reconstruction.
    /// </summary>
    internal static TaskRequirementAdmission Evaluate(
        IReadOnlyList<string> requested, PermissionGrant? grant, IEnumerable<string> declaredOutputs)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(declaredOutputs);

        var effectiveGrant = EffectiveGrant(grant ?? new PermissionGrant(), declaredOutputs);
        try
        {
            requested = TaskRequirements.Normalize(requested);
        }
        catch (ArgumentException ex)
        {
            return new TaskRequirementAdmission(
                requested, effectiveGrant, TaskRequirementAdmission.Refused, [ex.Message], VendorUsage: 0);
        }

        var granted = effectiveGrant.ToHashSet(StringComparer.Ordinal);
        var missing = requested.Where(requirement => !granted.Contains(requirement)).ToList();
        return missing.Count == 0
            ? new TaskRequirementAdmission(requested, effectiveGrant, TaskRequirementAdmission.Admitted)
            : new TaskRequirementAdmission(requested, effectiveGrant, TaskRequirementAdmission.Refused, missing, VendorUsage: 0);
    }

    private static IReadOnlyList<string> EffectiveGrant(PermissionGrant grant, IEnumerable<string> declaredOutputs)
    {
        var capabilities = new List<string>();
        if (grant.ReadFiles)
        {
            capabilities.Add(TaskRequirements.RepositoryRead);
        }

        if (grant.WriteFiles)
        {
            capabilities.Add(TaskRequirements.FileWrite);
        }

        if (grant.RunShellCommands)
        {
            capabilities.Add(TaskRequirements.Shell);
        }

        // General network access is intentionally not inferred from an allowlisted `gh` command. A
        // reviewer can read one GitHub page under a narrow shell grant without being entitled to web
        // fetch/search or arbitrary network access.
        if (grant.NetworkAccess)
        {
            capabilities.Add(TaskRequirements.Network);
        }

        if (Allows(grant, GitHubReadProbe))
        {
            capabilities.Add(TaskRequirements.GitHubRead);
        }

        if (Allows(grant, GitHubWriteProbe))
        {
            capabilities.Add(TaskRequirements.GitHubWrite);
        }

        capabilities.AddRange(declaredOutputs
            .Select(output => TaskRequirements.ArtifactPrefix + output.Trim().ToLowerInvariant()));
        return capabilities;
    }

    private static bool Allows(PermissionGrant grant, string command) =>
        grant.RunShellCommands
        && ShellCommandPatternMatcher.EvaluateChainedCommand(
            command, grant.ShellCommandPatterns, grant.DeniedShellCommandPatterns,
            grant.DeniedShellCommandExceptions).IsAllowed
        && !ShellCommandPatternMatcher.IsDeniedByOptionToken(command, grant.DeniedShellOptionTokens);
}
