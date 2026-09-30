using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baton.Steering;

namespace Baton.Vendors.Tests;

public sealed class ClaudeCorrectionSenderTests
{
    private const string Text = "Keep this exact text.\nIncluding its newline.";
    private const string Target = @"uds:\\.\pipe\LOCAL\cc-msg-target";
    private static CorrectionRequest Request => new("message", "execution",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Text))), "claude", "session", Target,
        123, DateTime.UtcNow, "principal", DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exact_target_and_message_match_with_optional_at_prefix(bool atPrefix)
    {
        using var hook = Hook("SendMessage", atPrefix ? "@" + Target : Target, Text);
        Assert.True(ClaudeCorrectionSender.MatchesTool(hook.RootElement, Request, Text));
    }

    [Fact]
    public void Native_recipient_content_aliases_match()
    {
        using var hook = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            tool_name = "SendMessage",
            tool_input = new { recipient = Target, content = Text },
        }));
        Assert.True(ClaudeCorrectionSender.MatchesTool(hook.RootElement, Request, Text));
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("target")]
    [InlineData("hash")]
    [InlineData("message")]
    public void Wrong_tool_target_digest_or_text_never_matches(string mismatch)
    {
        using var hook = Hook(mismatch == "tool" ? "Bash" : "SendMessage",
            mismatch == "target" ? "other" : Target, mismatch == "message" ? "changed" : Text);
        var request = mismatch == "hash" ? Request with { PayloadSha256 = "wrong" } : Request;
        Assert.False(ClaudeCorrectionSender.MatchesTool(hook.RootElement, request, Text));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tool_name\":17,\"tool_input\":{}}")]
    public void Malformed_tool_shape_does_not_match(string json)
    {
        using var hook = JsonDocument.Parse(json);
        Assert.False(ClaudeCorrectionSender.MatchesTool(hook.RootElement, Request, Text));
    }

    [Fact]
    public void Native_correlated_success_retains_message_id_and_transport_reason()
    {
        var answer = ClaudeCorrectionSender.ParseAnswer(Call() + "\n" + Result());
        Assert.NotNull(answer);
        Assert.True(answer.Value.Accepted);
        Assert.Equal("native-message", answer.Value.Receipt);
        Assert.Equal("delivered", answer.Value.Reason);
    }

    [Theory]
    [InlineData("no-call")]
    [InlineData("wrong-id")]
    [InlineData("wrong-tool")]
    [InlineData("result-before-call")]
    public void Success_requires_a_preceding_matching_SendMessage_call(string mismatch)
    {
        var output = mismatch switch
        {
            "no-call" => Result(),
            "wrong-id" => Call() + "\n" + Result(id: "another"),
            "wrong-tool" => Call(tool: "Bash") + "\n" + Result(),
            _ => Result() + "\n" + Call(),
        };
        Assert.Null(ClaudeCorrectionSender.ParseAnswer(output));
    }

    [Fact]
    public void Assistant_text_containing_fake_receipt_json_is_not_native_evidence()
    {
        var fake = JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new { content = new[] { new { type = "text", text = Result() } } },
        });
        Assert.Null(ClaudeCorrectionSender.ParseAnswer(Call() + "\n" + fake));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Success_without_native_message_id_is_unknown(string? receipt)
    {
        Assert.Null(ClaudeCorrectionSender.ParseAnswer(Call() + "\n" + Result(receipt: receipt)));
    }

    [Fact]
    public void Correlated_native_failure_is_a_rejection()
    {
        var answer = ClaudeCorrectionSender.ParseAnswer(Call() + "\n" + Result(success: false, receipt: null, reason: "Inbox unavailable"));
        Assert.NotNull(answer);
        Assert.False(answer.Value.Accepted);
        Assert.Null(answer.Value.Receipt);
        Assert.Equal("Inbox unavailable", answer.Value.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("{\"type\":\"user\"")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"message\":null}")]
    public void Missing_truncated_or_wrong_shape_output_is_unknown(string output)
    {
        Assert.Null(ClaudeCorrectionSender.ParseAnswer(output));
    }

    private static JsonDocument Hook(string tool, string target, string message) => JsonDocument.Parse(
        JsonSerializer.Serialize(new { tool_name = tool, tool_input = new { to = target, message } }));

    private static string Call(string tool = "SendMessage") => JsonSerializer.Serialize(new
    {
        type = "assistant",
        message = new { content = new[] { new { type = "tool_use", name = tool, id = "call-1" } } },
    });

    private static string Result(bool success = true, string? receipt = "native-message", string reason = "delivered", string id = "call-1") =>
        JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { content = new[] { new { type = "tool_result", tool_use_id = id } } },
            tool_use_result = new { success, msg_id = receipt, message = reason },
        });
}
