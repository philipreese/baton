using System.Text.Json.Serialization;
using Baton.Domain;

namespace Baton.Vendors;

public sealed record RoleTemplateOutputExport(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("instruction")] string Instruction);

public sealed record RoleTemplateExport(
    [property: JsonPropertyName("adapter")] string Adapter,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("effort")] string? Effort,
    [property: JsonPropertyName("read_files")] bool ReadFiles,
    [property: JsonPropertyName("write_files")] bool WriteFiles,
    [property: JsonPropertyName("run_shell_commands")] bool RunShellCommands,
    [property: JsonPropertyName("network_access")] bool NetworkAccess,
    [property: JsonPropertyName("timeout_minutes")] int TimeoutMinutes,
    [property: JsonPropertyName("verdict_schema")] bool VerdictSchema,
    [property: JsonPropertyName("_use")] string Use,
    [property: JsonPropertyName("_outputs")] IReadOnlyList<RoleTemplateOutputExport> Outputs,
    // #1456: exported so the catalog's other reader (the #836 shared-source loop tool) sees the
    // scoped-shell shape too — otherwise `baton templates --json` under-reports review's grant.
    // Field semantics: spec/baton.md §9.
    [property: JsonPropertyName("shell_command_patterns")] IReadOnlyList<string>? ShellCommandPatterns = null,
    [property: JsonPropertyName("denied_shell_command_patterns")] IReadOnlyList<string>? DeniedShellCommandPatterns = null,
    [property: JsonPropertyName("shell_commands_are_read_only")] bool ShellCommandsAreReadOnly = false,
    // #1683 F2: exported for the same reason as the two lists above — until #1759 retired it,
    // dispatch.py built its PermissionGrant from this export, so a field missing here was a deny
    // silently absent from every lane dispatched through that tool. `baton templates --json`
    // (this export's own surface) is still exercised today by tool-refresh/refresh.py's install
    // smoke check.
    [property: JsonPropertyName("denied_shell_option_tokens")] IReadOnlyList<string>? DeniedShellOptionTokens = null);

/// <summary>
/// Information describing a built-in workflow template (M22 Phase 1).
/// </summary>
public sealed record BuiltInTemplateInfo(
    string Id,
    string Title,
    string Description,
    bool RequiresSecondaryVendor);

/// <summary>
/// Pre-authored workflow template catalog (M22 Phase 1): the built-in template and dispatch-role
/// metadata <c>baton templates</c> reports.
/// </summary>
public static class BuiltInWorkflowTemplates
{
    public static readonly BuiltInTemplateInfo ChatSession = new(
        Id: "chat-session",
        Title: "Chat (Interactive Session)",
        Description: "Interactive 1-on-1 session with an AI worker (Claude or agy) with live turn streaming and session resumption.",
        RequiresSecondaryVendor: false);

    public static readonly BuiltInTemplateInfo CodebaseSession = new(
        Id: "codebase-session",
        Title: "Codebase Session",
        Description: "Interactive AI agent session bound to a project directory with conservative file/command permissions.",
        RequiresSecondaryVendor: false);

    public static readonly BuiltInTemplateInfo SoloRun = new(
        Id: "solo-run",
        Title: "Solo Run (Advanced)",
        Description: "Single-step execution using an installed AI worker (Claude or agy).",
        RequiresSecondaryVendor: false);

    public static readonly BuiltInTemplateInfo ReviewRun = new(
        Id: "review-run",
        Title: "Review Run (Advanced)",
        Description: "Two-step workflow where one AI worker drafts content and another AI worker reviews it with human sign-off.",
        RequiresSecondaryVendor: true);

    // The dispatch roles (advise/implement/review/fact-check/janitor) are deliberately NOT
    // BuiltInTemplateInfo entries: Catalog feeds the daemon's /api/templates and from there the
    // desktop and mobile start pickers, and putting roles in front of a person is a UI-arc
    // decision, not a #887-stage-1 side effect. They are exported to machine consumers via
    // GetRoleTemplates() below.
    public static IReadOnlyList<BuiltInTemplateInfo> Catalog { get; } = [ChatSession, CodebaseSession, SoloRun, ReviewRun];

    public static IReadOnlyDictionary<string, RoleTemplateExport> GetRoleTemplates()
    {
        var roles = WorkerRoleCatalog.All;
        var dict = new Dictionary<string, RoleTemplateExport>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            dict[role.Id] = new RoleTemplateExport(
                Adapter: role.Adapter,
                Model: role.Model,
                Effort: role.Effort,
                ReadFiles: role.Grant.ReadFiles,
                WriteFiles: role.Grant.WriteFiles,
                RunShellCommands: role.Grant.RunShellCommands,
                NetworkAccess: role.Grant.NetworkAccess,
                TimeoutMinutes: (int)role.Timeout.TotalMinutes,
                VerdictSchema: role.ProducesVerdict,
                Use: role.Purpose,
                Outputs: role.Outputs.Select(o => new RoleTemplateOutputExport(
                    Name: o.Name,
                    Schema: o.Schema switch
                    {
                        OutputSchema.ReviewVerdict => "review_verdict",
                        OutputSchema.Diff => "diff",
                        OutputSchema.NonEmptyText => "non_empty_text",
                        _ => "none",
                    },
                    Instruction: o.Instruction)).ToList(),
                ShellCommandPatterns: role.Grant.ShellCommandPatterns,
                DeniedShellCommandPatterns: role.Grant.DeniedShellCommandPatterns,
                ShellCommandsAreReadOnly: role.Grant.ShellCommandsAreReadOnly,
                DeniedShellOptionTokens: role.Grant.DeniedShellOptionTokens);
        }
        return dict;
    }
}
