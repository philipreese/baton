using Baton.Queue;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// Add and daemon admission must describe the same project-capped grant. The vendor binding gate
/// still owns the final launch-time enforcement; this early read never widens a ceiling.
/// </summary>
internal static class RecordedProjectCeilingAdmission
{
    internal static Result Evaluate(QueueItem item, WorkerRole role, bool requireDeclaredRequirements)
    {
        var roleAdmission = TaskRequirementPreflight.Evaluate(item, role, requireDeclaredRequirements);
        var ceiling = ProjectCeilingStore.TryGetRecord(item.Workspace, ProjectCeilingStore.DefaultPath);
        if (ceiling is null)
        {
            // An unrecorded workspace is still refused by the binding gate. Do not invent trust at
            // queue-add time or pretend that the role grant is a project trust decision.
            return new Result(roleAdmission, false, false, [], []);
        }

        var cappedGrant = ceiling.Cap(role.Grant);
        var admission = TaskRequirementPreflight.Evaluate(
            item, role, requireDeclaredRequirements, cappedGrant);
        var withheld = WithheldCategories(role.Grant, ceiling);
        if (ceiling.IsRevoked)
        {
            return new Result(
                admission with
                {
                    Result = TaskRequirementAdmission.Refused,
                    Missing = ["revoked project ceiling"],
                    VendorUsage = 0,
                },
                true, true, withheld, []);
        }

        // The shell predicate and its strict-category input are the same ones the vendor gate uses.
        // A grant that defeats a withheld category is not admitted merely because the brief omitted
        // a corresponding --require declaration.
        var defeated = cappedGrant.CategoriesDefeatedByTheShell(
            strictCategories: ceiling.StrictShellCategories());
        if (defeated.Count > 0 && admission.Result != TaskRequirementAdmission.Refused)
        {
            admission = admission with
            {
                Result = TaskRequirementAdmission.Refused,
                Missing = defeated.Select(category => $"shell defeats {category}").ToList(),
                VendorUsage = 0,
            };
        }

        return new Result(admission, true, false, withheld, defeated);
    }

    private static IReadOnlyList<string> WithheldCategories(PermissionGrant grant, ProjectCeiling ceiling)
    {
        var withheld = new List<string>();
        if (grant.ReadFiles && !ceiling.ReadFiles) withheld.Add(nameof(PermissionGrant.ReadFiles));
        if (grant.WriteFiles && !ceiling.WriteFiles) withheld.Add(nameof(PermissionGrant.WriteFiles));
        if (grant.RunShellCommands && !ceiling.RunShellCommands) withheld.Add(nameof(PermissionGrant.RunShellCommands));
        if (grant.NetworkAccess && !ceiling.NetworkAccess) withheld.Add(nameof(PermissionGrant.NetworkAccess));
        return withheld;
    }

    internal sealed record Result(
        TaskRequirementAdmission Admission,
        bool CeilingFound,
        bool Revoked,
        IReadOnlyList<string> WithheldCategories,
        IReadOnlyList<string> ShellDefeats)
    {
        internal string RefusalMessage(string workspace, string role) => Revoked
            ? $"Workspace '{workspace}' has a revoked project ceiling and cannot admit role '{role}'."
            : $"Workspace '{workspace}' project ceiling cannot admit role '{role}': "
              + $"missing task requirements {string.Join(", ", Admission.Missing ?? [])}; "
              + $"withheld categories {string.Join(", ", WithheldCategories)}; "
              + $"shell defeats {string.Join(", ", ShellDefeats)}.";
    }
}
