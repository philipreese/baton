using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Baton.Tests.TestSupport;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// #1998's own end of the change: a lane whose final <c>baton_run_command</c> was a <c>git push</c>
/// killed at the shipping ceiling settles with a reason naming that, instead of the bare
/// <c>branch-not-pushed</c> a conductor then has to reconstruct. Drives the two halves the room drives —
/// <see cref="ShippingCeilingStreamReader"/> over a fixture stream, then
/// <see cref="DeliveryVerifier.CheckAsync"/> over a real workspace whose branch really is not on
/// origin — and asserts the tail text that becomes the room's <c>verifyTail</c>.
/// </summary>
public sealed class ShippingCeilingSettleReasonTests
{
    [Fact]
    public async Task A_final_push_killed_at_the_shipping_ceiling_names_that_as_the_settle_reason()
    {
        var stream = TempPath("stream");
        var origin = TempGitRepository.InitBareRepository(TempPath("origin"));
        var workspace = TempPath("workspace");
        try
        {
            Directory.CreateDirectory(stream);
            WriteStream(
                stream,
                RunCommandCompleted("Build succeeded."),
                RunCommandCompleted(
                    ShellCommandCeilings.DescribeTimeout(ShellCommandClass.Shipping, ShellCommandCeilings.Shipping)));

            Assert.True(ShippingCeilingStreamReader.FinalRunCommandHitShippingCeiling(new CodexUsageParser(), stream));

            var outcome = await CheckUnpushedWorkspaceAsync(origin, workspace, "1998-lane", shippingCeilingExceeded: true);

            Assert.Equal(DeliveryCheckStatus.Failed, outcome.Status);
            Assert.Equal(["branch-not-pushed"], outcome.FailingMembers);
            Assert.Contains(
                $"the push exceeded the shipping ceiling ({ShellCommandCeilings.Shipping.TotalSeconds:0.##} s) during the pre-push gate",
                outcome.Tail,
                StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(stream);
            Cleanup(workspace, origin);
        }
    }

    /// <summary>
    /// The polarity arm, and the one that makes the tri-state read of the stream load-bearing: the push
    /// timed out and then the lane ran something else that completed. The FINAL run-command is no longer
    /// the kill, so the room says what it always said — the ceiling is not offered as the cause of a
    /// branch that is missing for some other reason.
    /// </summary>
    [Fact]
    public async Task A_push_timeout_followed_by_another_command_settles_with_the_ordinary_reason()
    {
        var stream = TempPath("stream");
        var origin = TempGitRepository.InitBareRepository(TempPath("origin"));
        var workspace = TempPath("workspace");
        try
        {
            Directory.CreateDirectory(stream);
            WriteStream(
                stream,
                RunCommandCompleted(
                    ShellCommandCeilings.DescribeTimeout(ShellCommandClass.Shipping, ShellCommandCeilings.Shipping)),
                RunCommandCompleted("On branch 1998-lane"));

            var reader = new CodexUsageParser();
            Assert.False(ShippingCeilingStreamReader.FinalRunCommandHitShippingCeiling(reader, stream));

            var outcome = await CheckUnpushedWorkspaceAsync(origin, workspace, "1998-lane", shippingCeilingExceeded: false);

            Assert.Equal(DeliveryCheckStatus.Failed, outcome.Status);
            Assert.Equal(["branch-not-pushed"], outcome.FailingMembers);
            Assert.DoesNotContain("shipping ceiling", outcome.Tail, StringComparison.Ordinal);
            Assert.Contains("it has never been pushed", outcome.Tail, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(stream);
            Cleanup(workspace, origin);
        }
    }

    /// <summary>
    /// The control for the anchoring rule
    /// <see cref="IWorkerUsageParser.ReportsShippingCeilingTimeout"/> states — the false positive that
    /// rule exists to stop, driven rather than described.
    /// </summary>
    [Fact]
    public void A_read_whose_content_merely_contains_the_marker_is_not_a_timed_out_push()
    {
        var stream = TempPath("stream");
        try
        {
            Directory.CreateDirectory(stream);
            WriteStream(
                stream,
                RunCommandCompleted("Everything up-to-date"),
                ReadTextCompleted($"public const string ShippingCeilingMarker = \"{ShellCommandCeilings.ShippingCeilingMarker}\";"));

            Assert.False(ShippingCeilingStreamReader.FinalRunCommandHitShippingCeiling(new CodexUsageParser(), stream));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(stream);
        }
    }

    [Fact]
    public void A_stream_with_no_run_command_at_all_reports_nothing()
    {
        var stream = TempPath("stream");
        try
        {
            Directory.CreateDirectory(stream);
            WriteStream(stream, ReadTextCompleted("ordinary file content"));

            Assert.False(ShippingCeilingStreamReader.FinalRunCommandHitShippingCeiling(new CodexUsageParser(), stream));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(stream);
        }
    }

    // ---- fixtures ----

    private static async Task<DeliveryCheckOutcome> CheckUnpushedWorkspaceAsync(
        string origin, string workspace, string branch, bool shippingCeilingExceeded)
    {
        Directory.CreateDirectory(workspace);
        TempGitRepository.InitWithEverythingCommitted(workspace);
        TempGitRepository.AddRemote(workspace, "origin", origin);
        TempGitRepository.CreateAndCheckoutBranch(workspace, branch);
        TempGitRepository.CommitAll(workspace, "lane work, never pushed");

        return await DeliveryVerifier.CheckAsync(
            workspace, expectPr: false, TestContext.Current.CancellationToken,
            shippingCeilingExceeded: shippingCeilingExceeded);
    }

    /// <summary>
    /// The captured-stream shape <c>Baton.Vendors.CodexAppServerBroker</c> writes for one dynamic-tool
    /// result: an <c>item.completed</c> <c>mcp_tool_call</c> whose <c>aggregated_output</c> is
    /// <c>CodexDynamicToolResult.Text</c> verbatim.
    /// </summary>
    /// <remarks>
    /// The command line itself is deliberately absent: the broker emits an arguments DIGEST and never
    /// the line, so a fixture carrying one would not be the stream a room actually holds. Which command
    /// produced the result is legible from the result text at each call site.
    /// </remarks>
    private static string RunCommandCompleted(string output) =>
        CompletedItem(CodexUsageParser.RunCommandToolName, output);

    private static string ReadTextCompleted(string output) => CompletedItem("baton_read_text", output);

    private static string CompletedItem(string tool, string output)
    {
        var item = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "item.completed",
            ["item"] = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "mcp_tool_call",
                ["tool"] = tool,
                ["status"] = "failed",
                ["aggregated_output"] = output,
            },
        };
        return item.ToJsonString();
    }

    private static void WriteStream(string outputDirectory, params string[] lines) =>
        File.WriteAllLines(Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName), lines);

    private static string TempPath(string label) => Path.Combine(Path.GetTempPath(), $"sc-{label}-{Guid.NewGuid():N}");

    private static void Cleanup(string workspace, string origin)
    {
        DirectoryCleanup.DeleteRecursively(workspace);
        DirectoryCleanup.DeleteRecursively(origin);
    }
}
