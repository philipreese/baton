using System.Text.Json;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;

namespace Baton.Vendors.Tests;

public sealed class AgyHostDecoderTests
{
    [Fact]
    public void Opt_in_uses_native_stdin_and_preserves_the_resolved_hooks_grant_and_prompt_replacement()
    {
        var contract = new WorkerContract("worker", [], [new ProducedOutput("report.md")], []);
        var adapter = new AgyWorkerAdapter();
        var invocation = new WorkerInvocation("actual prompt", StreamJson: true);
        var oneShot = adapter.Resolve(invocation, contract);
        Assert.Null(oneShot.ExactRunningTransport);
        Assert.Null(oneShot.CreateProcessTransport);
        var stream = adapter.Resolve(invocation with { EnableAgyCorrection = true }, contract);
        Assert.Equal("agy-stream-v1", stream.ExactRunningTransport);
        Assert.NotNull(stream.CreateProcessTransport);
        Assert.Equal(new[] { "--input-format", "stream-json" }, stream.Args.Take(2));
        Assert.Equal(oneShot.Args.Skip(2), stream.Args.Skip(2));
        Assert.Equal(oneShot.Environment, stream.Environment);
        Assert.Equal(oneShot.PromptText, stream.PromptText);
        Assert.Equal("replacement", stream.WithReplacedPrompt("replacement").PromptText);
        Assert.DoesNotContain("replacement", stream.WithReplacedPrompt("replacement").Args);
    }

    [Fact]
    public void Incoherent_opt_in_is_refused_by_every_adapter()
    {
        var contract = new WorkerContract("worker", [], [], []);
        var invocation = new WorkerInvocation("prompt", EnableAgyCorrection: true);
        Assert.Throws<InvalidOperationException>(() => new AgyWorkerAdapter().Resolve(invocation, contract));
        Assert.Throws<InvalidOperationException>(() => new AgyWorkerAdapter().Resolve(invocation with { StreamJson = true, ResumeSession = true }, contract));
        Assert.Throws<InvalidOperationException>(() => new ClaudeWorkerAdapter().Resolve(invocation, contract));
        Assert.Throws<InvalidOperationException>(() => new CodexWorkerAdapter().Resolve(invocation, contract));
    }

    private const string Execution = "fixture";
    private const string Incarnation = "0123456789abcdef0123456789abcdef";
    private const string Success = """{"event":"result","result":{"conversation_id":"conversation","status":"SUCCESS","response":"done","usage":{"input_tokens":99,"output_tokens":88}}}""";
    private const string Failure = """{"event":"result","result":{"conversation_id":"conversation","status":"ERROR","error":"Individual quota reached. Resets in 1h","usage":{"input_tokens":99,"output_tokens":88}}}""";
    private static FinalExpectedTurnCompletion Completion(bool success = true) =>
        new(Execution, "agy-stream-v1", Incarnation, 123, new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc), "conversation", 2, 2, success, true);

    private static string Envelope(string kind, string native, FinalExpectedTurnCompletion? completion = null)
    {
        using var document = JsonDocument.Parse(native);
        return JsonSerializer.Serialize(new AgyHostEnvelope(1, kind, Execution, Incarnation, 2, document.RootElement, completion));
    }

    [Fact]
    public void First_result_is_hidden_and_valid_final_failure_reaches_all_result_readers()
    {
        var first = Envelope("native", Success);
        var final = Envelope("completion", Failure, Completion(false));
        var adapter = new AgyWorkerAdapter();
        Assert.False(AgyWorkerAdapter.IsTerminalSuccessLine(first));
        Assert.False(AgyWorkerAdapter.IsTerminalResultLine(first));
        Assert.False(adapter.TryParseFinalResponse(first, out _));
        Assert.False(adapter.TryParseFinalUsage(first, out _));
        Assert.False(AgyWorkerAdapter.IsTerminalSuccessLine(final));
        Assert.True(AgyWorkerAdapter.IsTerminalResultLine(final));
        Assert.True(adapter.TryParseProgressEvent(final, out var progress));
        Assert.Equal("result", progress!.Kind);
        Assert.True(adapter.TryParseFinalUsage(final, out var usage));
        Assert.Equal(99, usage!.TokensIn);
        Assert.True(AgyWorkerAdapter.TryClassifyQuotaExhaustionFromResultEnvelope(first + "\n" + final,
            TimeProvider.System, out _, out _));
        Assert.True(AgyWorkerAdapter.TryClassifyQuotaExhaustionFromResultEnvelope("clipped prefix} " + first + " " + final,
            TimeProvider.System, out _, out _));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("conversation")]
    [InlineData("turn")]
    [InlineData("execution")]
    [InlineData("incarnation")]
    [InlineData("child")]
    [InlineData("birth")]
    [InlineData("version")]
    [InlineData("invalid")]
    public void Inconsistent_footer_cannot_supply_terminal_success(string corruption)
    {
        var completion = Completion();
        completion = corruption switch
        {
            "status" => completion with { Successful = false },
            "conversation" => completion with { ConversationId = "wrong" },
            "turn" => completion with { ExpectedTurn = 1 },
            "execution" => completion with { ExecutionId = "wrong" },
            "incarnation" => completion with { Incarnation = "wrong" },
            "child" => completion with { ChildPid = 0 },
            "birth" => completion with { ChildStartUtc = default },
            "version" => completion with { Version = 2 },
            "invalid" => completion with { EvidenceValid = false },
            _ => throw new InvalidOperationException(corruption),
        };
        Assert.False(AgyWorkerAdapter.IsTerminalSuccessLine(Envelope("completion", Success, completion)));
    }

    [Fact]
    public void Torn_outer_envelope_cannot_promote_its_nested_native_success()
    {
        var outer = Envelope("completion", Success, Completion());
        var torn = outer[..^1];
        Assert.DoesNotContain("SUCCESS", AgyHostDecoder.DecodeTail(torn));
        Assert.Null(AgyTerminalStreamRecoveryDetector.Detect(torn));
    }

    [Fact]
    public void Ordinary_raw_result_preserves_its_original_parsing()
    {
        Assert.Equal(Success, AgyHostDecoder.Decode(Success));
        Assert.Equal(Success, AgyHostDecoder.DecodeTail(Success));
        Assert.True(AgyWorkerAdapter.IsTerminalSuccessLine(Success));
        Assert.True(new AgyWorkerAdapter().TryParseFinalResponse(Success, out var response));
        Assert.Equal("done", response);
    }

    [Fact]
    public void Session_progress_tool_and_final_response_use_the_same_decoder()
    {
        IWorkerAdapter adapter = new AgyWorkerAdapter();
        const string init = """{"event":"init","conversation_id":"conversation"}""";
        Assert.False(adapter.TryParseSessionId(init, out _)); // unchanged one-shot session capability
        Assert.True(adapter.TryParseSessionId(Envelope("native", init), out var session));
        Assert.Equal("conversation", session);
        Assert.True(adapter.TryParseProgressEvent(Envelope("native", init), out _));
        var parser = new AgyUsageParser();
        var tool = Envelope("native", """{"event":"step_update","step_update":{"conversation_id":"conversation","step_index":4,"state":"DONE","step_type":"tool","tool_name":"view_file"}}""");
        Assert.Equal(1, parser.CountToolSteps(tool));
        Assert.Equal("view_file", parser.TryParseToolName(tool));
        var final = Envelope("completion", Success, Completion());
        Assert.True(adapter.TryParseFinalResponse(final, out var response));
        Assert.Equal("done", response);
    }
}
