using Baton.Artifacts;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Mcp;

/// <summary>
/// The <c>baton mcp</c> verb (#1458: folded from the standalone Baton.Mcp.Host executable) — composes
/// the requested <see cref="IMcpTool"/> set onto <see cref="McpServerHost"/> and runs the stdio MCP
/// protocol over <see cref="Console.In"/>/<see cref="Console.Out"/>. Same argument surface and stdio
/// protocol as the old Baton.Mcp.Host.exe: a vendor CLI or fleet-glass's pusher.py spawning the old
/// binary directly now spawns the packed `baton` tool with `mcp` as its first argument instead.
/// </summary>
public static class McpCommand
{
    public const string Usage = McpOptionsParser.Usage;

    public static async Task<int> RunAsync(string[] args)
    {
        var options = McpOptionsParser.Parse(args);
        var captureFilePath = options.CaptureFilePath;
        // #833: no literal path arrives on the command line -- see Baton.Vendors.ClaudeWorkerAdapter's
        // EnsureMemoryProposalMcpConfig for why (canonical: the resolve-once-per-binding seam and the
        // env-inheritance mechanism this flag rests on). This flag only says whether to enable the tool.
        var enableMemoryProposalTool = options.EnableMemoryProposalTool;
        var enableFleetStatusTool = options.EnableFleetStatusTool;
        var enableRoomDetailTool = options.EnableRoomDetailTool;
        var enableExactFileRestoreTool = options.EnableExactFileRestoreTool;

        List<IMcpTool> tools = [];
        if (captureFilePath is not null)
        {
            tools.Add(new YieldTool(captureFilePath));
        }

        if (enableFleetStatusTool)
        {
            tools.Add(new FleetStatusTool());
        }

        if (enableRoomDetailTool)
        {
            tools.Add(new RoomDetailTool());
        }

        if (enableMemoryProposalTool)
        {
            var outputDirectory = BatonEnvironmentSnapshot.Current.McpOutputDirectory;
            if (string.IsNullOrEmpty(outputDirectory))
            {
                Console.Error.WriteLine(
                    "--memory-proposal-tool requires BATON_OUTPUT_DIR in this process's environment (set per " +
                    "execution and inherited from the spawning vendor CLI); none was found.");
                return 1;
            }

            tools.Add(new MemoryProposalTool(Path.Combine(outputDirectory, MemoryProposalTool.CaptureDirectoryName)));

            // #595: promote-artifact rides the same opt-in as memory-edit-proposal rather than a
            // second flag -- both are worker-side escalation tools composed onto this same host, and
            // neither needs a flag of its own the other doesn't already require. BATON_OUTPUT_DIR is
            // always `{roomDir}/artifacts/execution_{id}` (ArtifactManager.AllocateOutputDirectory) --
            // structural, not a worker's claim, the same reasoning MemoryProposalEscalation's own
            // remarks give for trusting an execution_* directory name -- so the room directory and the
            // execution id are both derived from it rather than read from a second env var.
            //
            // That trust only holds when the shape actually matches: this is parsed from an inherited
            // environment variable, not written by AER itself in this process, so a malformed value
            // (wrong middle segment, missing 'execution_' prefix, a room directory that does not exist)
            // must fail closed rather than silently derive a wrong or nonexistent room directory --
            // #1824 review finding 1, RoomArtifacts.Write must never be pointed somewhere unintended.
            var executionDirectory = Path.GetFullPath(outputDirectory);
            var artifactsRoot = Path.GetDirectoryName(executionDirectory);
            var roomDirectoryPath = artifactsRoot is null ? null : Path.GetDirectoryName(artifactsRoot);
            var artifactsSegmentName = artifactsRoot is null ? null : Path.GetFileName(artifactsRoot);
            var artifactsSegmentComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var artifactsSegmentMatches = artifactsSegmentName is not null
                && string.Equals(artifactsSegmentName, ArtifactManager.ArtifactsDirectoryName, artifactsSegmentComparison);

            var executionDirectoryName = Path.GetFileName(executionDirectory);
            var executionId = executionDirectoryName.StartsWith("execution_", StringComparison.Ordinal)
                && executionDirectoryName.Length > "execution_".Length
                ? executionDirectoryName["execution_".Length..]
                : null;

            if (roomDirectoryPath is null || !artifactsSegmentMatches || executionId is null
                || !Directory.Exists(roomDirectoryPath))
            {
                Console.Error.WriteLine(
                    "--memory-proposal-tool requires BATON_OUTPUT_DIR to be an existing room directory's " +
                    $"'{ArtifactManager.ArtifactsDirectoryName}{Path.DirectorySeparatorChar}execution_<id>' " +
                    $"directory; got '{outputDirectory}'.");
                return 1;
            }

            var attribution = new ArtifactAttribution(ExecutionId: executionId, Role: null, Adapter: null, Model: null);

            tools.Add(new PromoteArtifactTool(roomDirectoryPath, executionDirectory, attribution));
        }

        if (enableExactFileRestoreTool)
        {
            var exactOutputDirectory = Environment.GetEnvironmentVariable("BATON_OUTPUT_DIR");
            if (string.IsNullOrWhiteSpace(exactOutputDirectory))
            {
                Console.Error.WriteLine(
                    "--exact-file-restore-tool requires BATON_OUTPUT_DIR from the current execution.");
                return 1;
            }

            var workspaceDirectory = Environment.GetEnvironmentVariable(WorkerEnvironment.WorkspaceVariable);
            var baseRevision = Environment.GetEnvironmentVariable(WorkerEnvironment.ExactFileRestoreBaseVariable);
            if (string.IsNullOrWhiteSpace(workspaceDirectory) || string.IsNullOrWhiteSpace(baseRevision)
                || !Path.IsPathFullyQualified(workspaceDirectory))
            {
                Console.Error.WriteLine(
                    "--exact-file-restore-tool requires absolute BATON_WORKSPACE_DIR and "
                    + "BATON_EXACT_FILE_RESTORE_BASE values from the dispatch-captured binding.");
                return 1;
            }

            var exactExecutionDirectory = Path.GetFullPath(
                exactOutputDirectory);
            var exactArtifactsRoot = Path.GetDirectoryName(exactExecutionDirectory);
            var exactRoomDirectory = exactArtifactsRoot is null ? null : Path.GetDirectoryName(exactArtifactsRoot);
            var exactExecutionName = Path.GetFileName(exactExecutionDirectory);
            var exactArtifactsMatch = exactArtifactsRoot is not null
                && string.Equals(
                    Path.GetFileName(exactArtifactsRoot), ArtifactManager.ArtifactsDirectoryName,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            var exactExecutionId = exactExecutionName.StartsWith("execution_", StringComparison.Ordinal)
                && exactExecutionName.Length > "execution_".Length
                ? exactExecutionName["execution_".Length..]
                : null;
            if (!exactArtifactsMatch || exactRoomDirectory is null || exactExecutionId is null
                || !Directory.Exists(exactRoomDirectory))
            {
                Console.Error.WriteLine(
                    "--exact-file-restore-tool requires BATON_OUTPUT_DIR to be an existing room directory's "
                    + "'artifacts\\execution_<id>' directory.");
                return 1;
            }

            tools.Add(new ExactFileRestoreTool(
                workspaceDirectory, baseRevision, exactExecutionId, exactRoomDirectory));
        }

        if (tools.Count == 0)
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }

        var host = new McpServerHost("baton-mcp-host", "1.0.0", tools);
        await host.RunAsync(Console.In, Console.Out).ConfigureAwait(false);
        return 0;
    }
}
