namespace Baton.Cli.Mcp;

internal sealed record McpOptions(
    string? CaptureFilePath,
    bool EnableMemoryProposalTool,
    bool EnableFleetStatusTool,
    bool EnableRoomDetailTool,
    bool EnableExactFileRestoreTool);

internal static class McpOptionsParser
{
    public const string Usage =
        "Usage: baton mcp [--capture-file <path>] [--memory-proposal-tool] [--exact-file-restore-tool] [--fleet-status-tool] [--room-detail-tool]";

    public static McpOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? captureFilePath = null;
        var enableMemoryProposalTool = false;
        var enableFleetStatusTool = false;
        var enableRoomDetailTool = false;
        var enableExactFileRestoreTool = false;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--capture-file":
                    if (captureFilePath is not null)
                    {
                        throw new CliArgumentException($"Option '--capture-file' may only be specified once. {Usage}");
                    }

                    if (i + 1 >= args.Count)
                    {
                        throw new CliArgumentException($"Option '--capture-file' requires a value. {Usage}");
                    }

                    captureFilePath = args[i + 1];
                    i += 2;
                    break;
                case "--memory-proposal-tool":
                    if (enableMemoryProposalTool)
                    {
                        throw new CliArgumentException($"Option '--memory-proposal-tool' may only be specified once. {Usage}");
                    }

                    enableMemoryProposalTool = true;
                    i++;
                    break;
                case "--fleet-status-tool":
                    if (enableFleetStatusTool)
                    {
                        throw new CliArgumentException($"Option '--fleet-status-tool' may only be specified once. {Usage}");
                    }

                    enableFleetStatusTool = true;
                    i++;
                    break;
                case "--room-detail-tool":
                    if (enableRoomDetailTool)
                    {
                        throw new CliArgumentException($"Option '--room-detail-tool' may only be specified once. {Usage}");
                    }

                    enableRoomDetailTool = true;
                    i++;
                    break;
                case "--exact-file-restore-tool":
                    if (enableExactFileRestoreTool)
                    {
                        throw new CliArgumentException($"Option '--exact-file-restore-tool' may only be specified once. {Usage}");
                    }

                    enableExactFileRestoreTool = true;
                    i++;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliArgumentException($"Unknown option '{arg}'. {Usage}");
                    }

                    throw new CliArgumentException($"Unexpected argument '{arg}'. {Usage}");
            }
        }

        return new McpOptions(
            captureFilePath, enableMemoryProposalTool, enableFleetStatusTool, enableRoomDetailTool,
            enableExactFileRestoreTool);
    }
}
