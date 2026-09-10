using Baton.Dispatch;
using Baton.Mutation;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Tests.Mutation;

public sealed class WriteToolCallMeasurementTests
{
    private sealed class UnsupportedUsageParser : IWorkerUsageParser;

    [Theory]
    [InlineData(ExecutionStreamLogger.StdoutTruncationMarkerFileName)]
    [InlineData(ExecutionStreamLogger.StdoutWriteFailureMarkerFileName)]
    public void A_loss_marker_makes_a_surviving_zero_write_stdout_unmeasurable(string markerFileName)
    {
        var outputDirectory = NewOutputDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName),
                "{\"event\":\"step_update\",\"step_update\":{\"state\":\"DONE\",\"step_type\":\"tool\",\"tool_name\":\"view_file\",\"tool_info\":{\"name\":\"view_file\"}}}\n");
            File.WriteAllText(Path.Combine(outputDirectory, markerFileName), string.Empty);

            Assert.Null(MutationInterface.CountWriteToolCallsFromStdoutLog(
                new AgyUsageParser(), outputDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public void An_unreadable_stdout_segment_is_not_reported_as_zero_writes()
    {
        var outputDirectory = NewOutputDirectory();
        try
        {
            var stdout = Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName);
            File.WriteAllText(stdout, "not a write\n");
            using var locked = new FileStream(stdout, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            Assert.Null(MutationInterface.CountWriteToolCallsFromStdoutLog(
                new CodexUsageParser(), outputDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public void A_parser_without_a_write_signal_cannot_turn_an_empty_capture_into_measured_zero()
    {
        var outputDirectory = NewOutputDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName), string.Empty);
            IWorkerUsageParser parser = new UnsupportedUsageParser();

            Assert.False(parser.SupportsWriteToolStepCounting);
            Assert.Null(parser.CountWriteToolSteps("anything"));
            Assert.Null(MutationInterface.CountWriteToolCallsFromStdoutLog(parser, outputDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public void A_complete_empty_capture_from_a_supported_parser_is_measured_zero()
    {
        var outputDirectory = NewOutputDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName), string.Empty);

            Assert.Equal(0, MutationInterface.CountWriteToolCallsFromStdoutLog(
                new CodexUsageParser(), outputDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    private static string NewOutputDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "baton-write-measurement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
