using Baton.Cli.Mcp;
using Baton.Status;

namespace Baton.Cli.Tests.Mcp;

/// <summary>
/// #1824 review finding 1: <c>McpCommand</c> derives the room directory and execution id straight
/// off <c>BATON_OUTPUT_DIR</c> with only a null check, never validating the
/// <c>{room}/artifacts/execution_&lt;id&gt;</c> shape the PR body claims it enforces. These pin the
/// fail-closed behaviour at the <c>McpCommand</c> level, where the review found the gap was
/// untested.
/// </summary>
[Collection(ConsoleErrorCaptureCollection.Name)]
public sealed class McpCommandTests
{
    [Fact]
    public async Task MissingArtifactsSegment_FailsClosed()
    {
        var roomDir = TempDir();
        Directory.CreateDirectory(roomDir);
        // No 'artifacts' segment at all: BATON_OUTPUT_DIR points directly at a leaf under the room.
        var malformedOutputDir = Path.Combine(roomDir, "execution_5");
        Directory.CreateDirectory(malformedOutputDir);

        try
        {
            var (exitCode, stderr) = await RunWithOutputDirectory(malformedOutputDir);

            Assert.Equal(1, exitCode);
            Assert.Contains("BATON_OUTPUT_DIR", stderr);
            Assert.Contains("execution_", stderr);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task LeafWithoutExecutionPrefix_FailsClosed()
    {
        var roomDir = TempDir();
        var artifactsRoot = Path.Combine(roomDir, "artifacts");
        var malformedOutputDir = Path.Combine(artifactsRoot, "not-an-execution-dir");
        Directory.CreateDirectory(malformedOutputDir);

        try
        {
            var (exitCode, stderr) = await RunWithOutputDirectory(malformedOutputDir);

            Assert.Equal(1, exitCode);
            Assert.Contains("BATON_OUTPUT_DIR", stderr);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task NonexistentRoomDirectory_FailsClosed()
    {
        // The shape parses correctly, but nothing was actually allocated on disk at 'roomDir'.
        var roomDir = TempDir();
        var executionDir = Path.Combine(roomDir, "artifacts", "execution_1");
        Directory.CreateDirectory(executionDir);
        DirectoryCleanup.DeleteRecursively(roomDir);

        var (exitCode, stderr) = await RunWithOutputDirectory(executionDir);

        Assert.Equal(1, exitCode);
        Assert.Contains("BATON_OUTPUT_DIR", stderr);
    }

    [Fact]
    public async Task CorrectShape_RegistersTheToolAndExitsCleanly()
    {
        var roomDir = TempDir();
        var executionDir = Path.Combine(roomDir, "artifacts", "execution_1");
        Directory.CreateDirectory(executionDir);

        try
        {
            var (exitCode, stderr) = await RunWithOutputDirectory(executionDir);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("BATON_OUTPUT_DIR", stderr);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public void McpOptionsParser_EmptyArgs_ParsesDefaults()
    {
        var options = McpOptionsParser.Parse([]);
        Assert.Null(options.CaptureFilePath);
        Assert.False(options.EnableMemoryProposalTool);
        Assert.False(options.EnableFleetStatusTool);
        Assert.False(options.EnableRoomDetailTool);
        Assert.False(options.EnableExactFileRestoreTool);
    }

    [Fact]
    public void McpOptionsParser_ValidFlags_ParsesCorrectly()
    {
        var options = McpOptionsParser.Parse([
            "--capture-file", "capture.json",
            "--memory-proposal-tool",
            "--exact-file-restore-tool",
            "--fleet-status-tool",
            "--room-detail-tool",
        ]);
        Assert.Equal("capture.json", options.CaptureFilePath);
        Assert.True(options.EnableMemoryProposalTool);
        Assert.True(options.EnableExactFileRestoreTool);
        Assert.True(options.EnableFleetStatusTool);
        Assert.True(options.EnableRoomDetailTool);
    }

    [Fact]
    public void McpOptionsParser_UnexpectedPositionalArgument_RejectsWithUsage()
    {
        var ex = Assert.Throws<CliArgumentException>(() => McpOptionsParser.Parse(["status"]));
        Assert.Contains("Unexpected argument 'status'", ex.Message);
        Assert.Contains(McpOptionsParser.Usage, ex.Message);
    }

    [Fact]
    public void McpOptionsParser_UnknownOption_RejectsWithUsage()
    {
        var ex = Assert.Throws<CliArgumentException>(() => McpOptionsParser.Parse(["--unknown"]));
        Assert.Contains("Unknown option '--unknown'", ex.Message);
        Assert.Contains(McpOptionsParser.Usage, ex.Message);
    }

    [Fact]
    public void McpOptionsParser_MissingCaptureFileValue_RejectsWithUsage()
    {
        var ex = Assert.Throws<CliArgumentException>(() => McpOptionsParser.Parse(["--capture-file"]));
        Assert.Contains("requires a value", ex.Message);
        Assert.Contains(McpOptionsParser.Usage, ex.Message);
    }

    [Fact]
    public void McpOptionsParser_DuplicateCaptureFile_RejectsWithUsage()
    {
        var ex = Assert.Throws<CliArgumentException>(() => McpOptionsParser.Parse(["--capture-file", "a", "--capture-file", "b"]));
        Assert.Contains("may only be specified once", ex.Message);
        Assert.Contains(McpOptionsParser.Usage, ex.Message);
    }

    [Fact]
    public void McpOptionsParser_DuplicateToolFlag_RejectsWithUsage()
    {
        var ex = Assert.Throws<CliArgumentException>(() => McpOptionsParser.Parse(["--fleet-status-tool", "--fleet-status-tool"]));
        Assert.Contains("may only be specified once", ex.Message);
        Assert.Contains(McpOptionsParser.Usage, ex.Message);
    }

    private static async Task<(int ExitCode, string Stderr)> RunWithOutputDirectory(string outputDirectory)
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { McpOutputDirectory = outputDirectory });

        var priorIn = Console.In;
        var priorOut = Console.Out;
        var priorError = Console.Error;
        var stderrWriter = new StringWriter();
        try
        {
            // Empty stdin: the host reads until EOF, so a closed/empty stream lets RunAsync return
            // immediately in the "correct shape" arm instead of blocking on a real MCP client.
            Console.SetIn(new StringReader(string.Empty));
            Console.SetOut(new StringWriter());
            Console.SetError(stderrWriter);

            var exitCode = await McpCommand.RunAsync(["--memory-proposal-tool"]);
            return (exitCode, stderrWriter.ToString());
        }
        finally
        {
            Console.SetIn(priorIn);
            Console.SetOut(priorOut);
            Console.SetError(priorError);
        }
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), $"baton-mcp-command-test-{Guid.NewGuid():N}");
}
