using Baton.Domain;
using Baton.Outcomes;
using Baton.Status;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// #2020 review HIGH, over the seam the emitter put at risk: the real <see cref="CodexWorkerAdapter"/>
/// driven through <see cref="OutputMaterializer.TryCaptureFinalResponse"/> against a
/// <c>.stdout.log</c> carrying the broker's new <c>turn.usage</c> line. The sibling
/// <see cref="AgyOutputMaterializationEndToEndTests"/> pins the same #1594 path for agy; this one pins
/// that codex's rescue survives a usage line landing between the final agent message and
/// <c>turn.completed</c> — an app-server ordering nothing in this tree measures either way, which is
/// exactly why the capture must not depend on it.
/// <para>
/// The usage line is written in the shape <c>CodexAppServerBroker</c> emits (see its
/// <c>thread/tokenUsage/updated</c> arm), <c>round_trip</c> included, and names the type through
/// <see cref="CodexUsageParser.TurnUsageEventType"/> so a rename cannot silently orphan these arms.
/// </para>
/// </summary>
public sealed class CodexOutputMaterializationEndToEndTests
{
    private const string AgentMessageLine =
        """{"type":"item.completed","item":{"id":"item-1","type":"agent_message","text":"the real terminal answer"}}""";

    // The type is substituted from the constant rather than typed out, so a rename cannot leave these
    // arms passing against a string the broker no longer writes.
    private static readonly string TurnUsageLine =
        """{"type":"USAGE_TYPE","usage":{"input_tokens":100,"cached_input_tokens":60,"cache_write_input_tokens":5,"output_tokens":20,"reasoning_output_tokens":4,"round_trip":2}}"""
            .Replace("USAGE_TYPE", CodexUsageParser.TurnUsageEventType, StringComparison.Ordinal);

    private const string TurnCompletedLine =
        """{"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":60,"cache_write_input_tokens":5,"output_tokens":20,"reasoning_output_tokens":4,"round_trip":2}}""";

    /// <summary>
    /// The discriminating arm. <c>TryReadFinalResponse</c> scans backward and stops at the first line
    /// that is neither a final response nor a declared trailer, so with the usage line sitting BETWEEN
    /// the agent message and <c>turn.completed</c> the scan reaches it before the response — red until
    /// <c>turn.usage</c> is in <c>CodexWorkerAdapter.IsPostResponseTerminalLine</c>'s whitelist, and
    /// red silently, since a null capture is indistinguishable from a worker with nothing to say.
    /// </summary>
    [Fact]
    public void TryCaptureFinalResponse_UsageLineBetweenTheResponseAndTurnCompleted_StillCapturesTheResponse()
    {
        RunCapture([AgentMessageLine, TurnUsageLine, TurnCompletedLine], expectCapture: true);
    }

    /// <summary>
    /// The polarity control, and honestly labelled: it passes BEFORE the fix as well as after. With the
    /// usage line ahead of the agent message the backward scan meets the response first and never reads
    /// a usage line at all — so this arm cannot discriminate the fix, and exists to pin that widening
    /// the whitelist did not break the ordering that already worked.
    /// </summary>
    [Fact]
    public void TryCaptureFinalResponse_UsageLineBeforeTheResponse_StillCapturesTheResponse()
    {
        RunCapture([TurnUsageLine, AgentMessageLine, TurnCompletedLine], expectCapture: true);
    }

    /// <summary>
    /// The refusal polarity: a trailing line that is neither response nor trailer still stops the scan.
    /// Without it, "capture succeeded on both orderings" would be equally true of a parser that
    /// whitelisted everything.
    /// </summary>
    [Fact]
    public void TryCaptureFinalResponse_StrayTrailingLineAfterTheUsageLine_RefusesExtraction()
    {
        RunCapture(
            [AgentMessageLine, TurnUsageLine, TurnCompletedLine, "not json at all, a stray trailing write"],
            expectCapture: false);
    }

    private static void RunCapture(string[] lines, bool expectCapture)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-materialize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, ".stdout.log"), string.Join('\n', lines) + "\n");

            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);
            var validation = ContractValidator.Validate(contract, directory);
            Assert.False(validation.IsSatisfied);

            var captured = OutputMaterializer.TryCaptureFinalResponse(
                validation, contract, directory, new CodexWorkerAdapter());

            if (!expectCapture)
            {
                Assert.Null(captured);
                Assert.False(File.Exists(Path.Combine(directory, OutputMaterializer.CapturedResponseFileName)));
                return;
            }

            Assert.NotNull(captured);
            Assert.Equal(OutputMaterializer.CapturedResponseFileName, captured.FileName);
            Assert.Equal(["advice.md"], captured.UnsatisfiedOutputNames);

            var contents = File.ReadAllText(Path.Combine(directory, OutputMaterializer.CapturedResponseFileName));
            Assert.StartsWith(OutputMaterializer.CapturedResponseHeader, contents);
            Assert.Contains("the real terminal answer", contents);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }
}
