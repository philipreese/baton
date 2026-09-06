using System.Text.Json;
using Baton.Tests.Shared;

namespace Baton.Vendors.Tests;

public sealed class CodexDynamicToolPolicyTests
{
    [Fact]
    public void Read_only_role_gets_reads_and_declared_output_but_no_workspace_write_or_command()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);

        var names = ToolNames(fixture.Policy);

        Assert.Contains(CodexDynamicToolPolicy.ReadTextTool, names);
        Assert.Contains(CodexDynamicToolPolicy.ListFilesTool, names);
        Assert.Contains(CodexDynamicToolPolicy.SearchTextTool, names);
        Assert.Contains(CodexDynamicToolPolicy.WriteOutputTool, names);
        Assert.DoesNotContain(CodexDynamicToolPolicy.WriteTextTool, names);
        Assert.DoesNotContain(CodexDynamicToolPolicy.RunCommandTool, names);
    }

    [Fact]
    public void Implement_role_gets_workspace_write_and_command_tools()
    {
        using var fixture = new PolicyFixture(
            new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true), ["changes.md"]);

        var names = ToolNames(fixture.Policy);

        Assert.Contains(CodexDynamicToolPolicy.WriteTextTool, names);
        Assert.Contains(CodexDynamicToolPolicy.RunCommandTool, names);
    }

    // #1920: one row per refusal shape in the issue's measured table — the four Baton-produced ones
    // (row 1 a path outside the readable roots, rows 2-3 a backslash path, row 9 the standing deny
    // list, rows 11-15 a command matching no allow pattern) plus the audit comment's dominant
    // unknown-tool case, five apply_patch attempts. A shape with no row here is a shape that can
    // regress to teaching nothing, which is what the issue measured.
    [Theory]
    [InlineData("outside-readable-roots", "ask for its content quoted inline")]
    [InlineData("unsupported-backslash", "use forward slashes")]
    [InlineData("standing-deny", "permanently closed for this role")]
    [InlineData("ungranted-pattern",
        "this session's granted shell patterns are: git diff*, git log*, git show*, git status*")]
    [InlineData("write-shaped-unknown-tool", "cannot edit workspace files")]
    public async Task Each_refusal_shape_names_its_granted_alternative(
        string refusalShape, string expectedAlternative)
    {
        using var fixture = new PolicyFixture(ReviewShapedGrant, ["report.md"]);

        var result = await ExecuteRefusalShapeAsync(fixture, refusalShape);

        Assert.False(result.Success);
        Assert.Contains(expectedAlternative, result.Text, StringComparison.Ordinal);
    }

    // The polarity arm for the rows above: every SHELL refusal also names the granted read path,
    // which is #1920's literal ask (the measured loop was `rg` four times before baton_search_text).
    [Theory]
    [InlineData("unsupported-backslash")]
    [InlineData("standing-deny")]
    [InlineData("ungranted-pattern")]
    public async Task Every_shell_refusal_names_the_granted_read_tools(string refusalShape)
    {
        using var fixture = new PolicyFixture(ReviewShapedGrant, ["report.md"]);

        var result = await ExecuteRefusalShapeAsync(fixture, refusalShape);

        Assert.False(result.Success);
        Assert.Contains(CodexDynamicToolPolicy.ReadTextTool, result.Text, StringComparison.Ordinal);
        Assert.Contains(CodexDynamicToolPolicy.SearchTextTool, result.Text, StringComparison.Ordinal);
    }

    // The negative arm the rows above need to discriminate: the clause is derived from the tools this
    // role actually declared, so a grant that withholds reads is told nothing rather than pointed at
    // two tools it does not have. Without this, a hardcoded clause passes every row above.
    [Fact]
    public async Task A_shell_refusal_names_no_read_tool_when_the_role_declares_none()
    {
        var grant = ReviewShapedGrant with { ReadFiles = false };
        using var fixture = new PolicyFixture(grant, []);

        var result = await ExecuteRefusalShapeAsync(fixture, "ungranted-pattern");

        Assert.False(result.Success);
        Assert.DoesNotContain(CodexDynamicToolPolicy.ReadTextTool, result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(CodexDynamicToolPolicy.SearchTextTool, result.Text, StringComparison.Ordinal);
    }

    // The write half of #1920's ask 2, in both directions: the same apply_patch attempt is answered
    // with baton_write_text on a write-granted role and with the declared-output write on a review
    // role, never with the read tools.
    [Fact]
    public async Task A_write_shaped_unknown_tool_names_the_write_path_the_role_actually_has()
    {
        using var reviewFixture = new PolicyFixture(ReviewShapedGrant, ["report.md"]);
        using var implementFixture = new PolicyFixture(
            ReviewShapedGrant with { WriteFiles = true }, ["changes.md"]);

        var onReview = await ExecuteRefusalShapeAsync(reviewFixture, "write-shaped-unknown-tool");
        var onImplement = await ExecuteRefusalShapeAsync(implementFixture, "write-shaped-unknown-tool");

        Assert.Contains(CodexDynamicToolPolicy.WriteOutputTool, onReview.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(CodexDynamicToolPolicy.WriteTextTool, onReview.Text, StringComparison.Ordinal);
        Assert.Contains(CodexDynamicToolPolicy.WriteTextTool, onImplement.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot edit workspace files", onImplement.Text, StringComparison.Ordinal);
    }

    /// <summary>The shipped review role's shape: reads, a scoped read-only shell, no workspace write.</summary>
    private static PermissionGrant ReviewShapedGrant => new(
        ReadFiles: true,
        RunShellCommands: true,
        ShellCommandPatterns: ["git diff*", "git log*", "git show*", "git status*"],
        DeniedShellCommandPatterns: ["git remote*"],
        ShellCommandsAreReadOnly: true);

    private static Task<CodexDynamicToolResult> ExecuteRefusalShapeAsync(
        PolicyFixture fixture, string refusalShape) => refusalShape switch
        {
            "outside-readable-roots" => fixture.ExecuteAsync(
                CodexDynamicToolPolicy.ReadTextTool,
                new { path = Path.Combine(Path.GetTempPath(), "baton-another-room", "verdict.json") }),
            "unsupported-backslash" => fixture.ExecuteAsync(
                CodexDynamicToolPolicy.RunCommandTool, new { command = @"git status C:\repo" }),
            "standing-deny" => fixture.ExecuteAsync(
                CodexDynamicToolPolicy.RunCommandTool, new { command = "git remote -v" }),
            "ungranted-pattern" => fixture.ExecuteAsync(
                CodexDynamicToolPolicy.RunCommandTool, new { command = "rg needle" }),
            "write-shaped-unknown-tool" => fixture.ExecuteAsync(
                "apply_patch", new { path = "src/file.cs", content = "patch" }),
            _ => throw new InvalidOperationException($"Unknown refusal fixture '{refusalShape}'."),
        };

    [Fact]
    public async Task Withheld_workspace_write_can_still_write_only_an_exact_declared_output()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);

        var allowed = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.WriteOutputTool, new { name = "report.md", content = "review" });
        var denied = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.WriteOutputTool, new { name = "other.md", content = "escape" });

        Assert.True(allowed.Success);
        Assert.Equal("review", File.ReadAllText(Path.Combine(fixture.Output, "report.md")));
        Assert.False(denied.Success);
        Assert.False(File.Exists(Path.Combine(fixture.Output, "other.md")));
    }

    [Fact]
    public async Task Workspace_path_traversal_is_denied()
    {
        using var fixture = new PolicyFixture(
            new PermissionGrant(ReadFiles: true, WriteFiles: true), ["changes.md"]);
        var outside = Path.Combine(Path.GetDirectoryName(fixture.Workspace)!, "outside.txt");

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.WriteTextTool, new { path = outside, content = "escape" });

        Assert.False(result.Success);
        Assert.Contains("outside", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task Contract_input_is_readable_even_when_general_workspace_reads_are_withheld()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(), ["answer.md"], createInput: true);

        var input = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = fixture.Input });
        var workspace = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = Path.Combine(fixture.Workspace, "workspace.txt") });

        Assert.True(input.Success);
        Assert.Equal("input", input.Text);
        Assert.False(workspace.Success);
    }

    [Fact]
    public async Task Scoped_command_uses_canonical_allow_deny_and_option_token_checks()
    {
        var grant = new PermissionGrant(
            ReadFiles: true,
            RunShellCommands: true,
            ShellCommandPatterns: ["git *", "git log*"],
            DeniedShellCommandPatterns: ["git status --short*"],
            DeniedShellOptionTokens: ["--output"],
            ShellCommandsAreReadOnly: true);
        using var fixture = new PolicyFixture(grant, ["report.md"]);

        var allowed = await fixture.ExecuteAsync(CodexDynamicToolPolicy.RunCommandTool, new { command = "git --version" });
        var deniedFamily = await fixture.ExecuteAsync(CodexDynamicToolPolicy.RunCommandTool, new { command = "git status --short" });
        var deniedOption = await fixture.ExecuteAsync(CodexDynamicToolPolicy.RunCommandTool, new { command = "git log --output=escape" });
        var deniedChain = await fixture.ExecuteAsync(CodexDynamicToolPolicy.RunCommandTool, new { command = "git --version && echo escape" });

        Assert.True(allowed.Success);
        Assert.False(deniedFamily.Success);
        Assert.False(deniedOption.Success);
        Assert.False(deniedChain.Success);
    }

    [Fact]
    public async Task Reparse_point_escape_is_denied_when_the_platform_can_create_one()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);
        var outside = Path.Combine(fixture.Root, "outside");
        var link = Path.Combine(fixture.Workspace, "linked");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = Path.Combine(link, "secret.txt") });

        Assert.False(result.Success);
        Assert.Contains("reparse", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Existing_symlink_destination_cannot_redirect_a_granted_write_outside_its_root()
    {
        using var fixture = new PolicyFixture(
            new PermissionGrant(ReadFiles: true, WriteFiles: true), ["report.md"]);
        var outside = Path.Combine(fixture.Root, "outside.txt");
        var link = Path.Combine(fixture.Workspace, "linked.txt");
        File.WriteAllText(outside, "original");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.WriteTextTool, new { path = link, content = "escape" });

        Assert.False(result.Success);
        Assert.Contains("reparse", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original", File.ReadAllText(outside));
    }

    [Fact]
    public async Task Recursive_listing_excludes_git_object_database_content()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);
        var git = Path.Combine(fixture.Workspace, ".git", "objects");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "large-object"), "irrelevant");

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ListFilesTool, new { path = fixture.Workspace });

        Assert.True(result.Success);
        Assert.Contains("workspace.txt", result.Text);
        Assert.DoesNotContain("large-object", result.Text);
    }

    private static IReadOnlyList<string> ToolNames(CodexDynamicToolPolicy policy) =>
        policy.BuildToolDefinitions().Select(node => node!["name"]!.GetValue<string>()).ToArray();

    private sealed class PolicyFixture : IDisposable
    {
        public PolicyFixture(PermissionGrant grant, IReadOnlyList<string> outputs, bool createInput = false)
        {
            Root = Path.Combine(Path.GetTempPath(), $"baton-codex-policy-{Guid.NewGuid():N}");
            Workspace = Path.Combine(Root, "workspace");
            Output = Path.Combine(Root, "output");
            Input = Path.Combine(Root, "input.txt");
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Output);
            File.WriteAllText(Path.Combine(Workspace, "workspace.txt"), "workspace");
            if (createInput)
            {
                File.WriteAllText(Input, "input");
            }
            Policy = new CodexDynamicToolPolicy(
                grant, Workspace, Output, createInput ? [Input] : [], outputs);
        }

        public string Root { get; }
        public string Workspace { get; }
        public string Output { get; }
        public string Input { get; }
        public CodexDynamicToolPolicy Policy { get; }

        public async Task<CodexDynamicToolResult> ExecuteAsync(string toolName, object arguments)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
            return await Policy.ExecuteAsync(toolName, doc.RootElement, TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                DirectoryCleanup.DeleteRecursively(Root);
            }
        }
    }
}
