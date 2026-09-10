using Baton.Domain;
using Baton.Status;

namespace Baton.Vendors.Tests;

/// <summary>
/// Cross-layer contract: the parsers' settle-time write vocabulary must cover the write tools the
/// actual adapters expose or gate. These inventories come from production declarations, not literals
/// repeated in the test, so adding a vendor write tool without teaching the parser fails here.
/// </summary>
public sealed class WriteToolUsageContractTests
{
    private static PermissionGrant AllExceptWrites => new(
        ReadFiles: true,
        WriteFiles: false,
        RunShellCommands: true,
        NetworkAccess: true);

    private static PermissionGrant AllPermissions => new(
        ReadFiles: true,
        WriteFiles: true,
        RunShellCommands: true,
        NetworkAccess: true);

    [Fact]
    public void Claude_parser_covers_the_adapters_actual_hook_gated_write_family()
    {
        var actualWriteTools = ClaudeWorkerAdapter.BuildHookDeniedTools(AllExceptWrites)
            .Split(',', StringSplitOptions.RemoveEmptyEntries);
        var parser = new ClaudeUsageParser();

        Assert.True(parser.SupportsWriteToolStepCounting);
        Assert.NotEmpty(actualWriteTools);
        foreach (var tool in actualWriteTools)
        {
            var line = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"TOOL\"}]}}"
                .Replace("TOOL", tool, StringComparison.Ordinal);
            Assert.Equal(1, parser.CountWriteToolSteps(line));
        }
    }

    [Fact]
    public void Agy_parser_covers_the_adapters_actual_write_family()
    {
        var deniedWithoutWrites = AgyWorkerAdapter.BuildDeniedTools(AllExceptWrites, allowsSubagents: true)
            .Split(',', StringSplitOptions.RemoveEmptyEntries);
        var subagentFamily = AgyWorkerAdapter.BuildDeniedTools(AllPermissions, allowsSubagents: false)
            .Split(',', StringSplitOptions.RemoveEmptyEntries);
        var actualWriteTools = deniedWithoutWrites.Except(subagentFamily, StringComparer.Ordinal).ToArray();
        var parser = new AgyUsageParser();

        Assert.True(parser.SupportsWriteToolStepCounting);
        Assert.NotEmpty(actualWriteTools);
        foreach (var tool in actualWriteTools)
        {
            var line = "{\"event\":\"step_update\",\"step_update\":{\"state\":\"DONE\",\"step_type\":\"tool\",\"tool_name\":\"TOOL\",\"tool_info\":{\"name\":\"TOOL\"}}}"
                .Replace("TOOL", tool, StringComparison.Ordinal);
            Assert.Equal(1, parser.CountWriteToolSteps(line));
        }
    }

    [Fact]
    public void Codex_parser_covers_the_policys_actual_write_tool_definitions()
    {
        var root = Path.Combine(Path.GetTempPath(), "baton-write-contract-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(output);
        try
        {
            var enabled = new CodexDynamicToolPolicy(
                AllPermissions, workspace, output, [], ["changes.md"])
                .BuildToolDefinitions()
                .Select(node => node!["name"]!.GetValue<string>());
            var withoutWrites = new CodexDynamicToolPolicy(
                AllExceptWrites, workspace, output, [], [])
                .BuildToolDefinitions()
                .Select(node => node!["name"]!.GetValue<string>());
            var actualWriteTools = enabled.Except(withoutWrites, StringComparer.Ordinal).ToArray();
            var parser = new CodexUsageParser();

            Assert.True(parser.SupportsWriteToolStepCounting);
            Assert.NotEmpty(actualWriteTools);
            foreach (var tool in actualWriteTools)
            {
                var line = "{\"type\":\"item.started\",\"item\":{\"type\":\"mcp_tool_call\",\"tool\":\"TOOL\"}}"
                    .Replace("TOOL", tool, StringComparison.Ordinal);
                Assert.Equal(1, parser.CountWriteToolSteps(line));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
