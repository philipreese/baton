namespace Baton.Cli.Tests;

public sealed class SteerOptionsParserTests
{
    [Fact]
    public void A_file_request_parses_the_exact_execution_and_message_identity()
    {
        var options = SteerOptionsParser.Parse(
            ["room", "--execution", "exec-1", "--message-id", "msg-1", "--file", "correction.txt"]);

        Assert.Equal("room", options.Room);
        Assert.Equal("exec-1", options.ExecutionId);
        Assert.Equal("msg-1", options.MessageId);
        Assert.Equal("correction.txt", options.File);
        Assert.False(options.Receipt);
    }

    [Fact]
    public void A_receipt_lookup_parses_without_a_file()
    {
        var options = SteerOptionsParser.Parse(
            ["room", "--execution", "exec-1", "--message-id", "msg-1", "--receipt"]);

        Assert.Equal("exec-1", options.ExecutionId);
        Assert.Equal("msg-1", options.MessageId);
        Assert.Null(options.File);
        Assert.True(options.Receipt);
    }

    [Fact]
    public void Execution_and_message_id_are_both_required()
    {
        Assert.Throws<CliArgumentException>(() => SteerOptionsParser.Parse(
            ["room", "--message-id", "msg-1", "--file", "correction.txt"]));
        Assert.Throws<CliArgumentException>(() => SteerOptionsParser.Parse(
            ["room", "--execution", "exec-1", "--file", "correction.txt"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_or_whitespace_message_id_is_rejected(string messageId)
    {
        Assert.Throws<CliArgumentException>(() => SteerOptionsParser.Parse(
            ["room", "--execution", "exec-1", "--message-id", messageId, "--receipt"]));
    }

    [Fact]
    public void A_message_id_longer_than_128_characters_is_rejected()
    {
        Assert.Throws<CliArgumentException>(() => SteerOptionsParser.Parse(
            ["room", "--execution", "exec-1", "--message-id", new string('x', 129), "--receipt"]));
    }

    [Fact]
    public void File_and_receipt_modes_are_mutually_exclusive()
    {
        Assert.Throws<CliArgumentException>(() => SteerOptionsParser.Parse(
            ["room", "--execution", "exec-1", "--message-id", "msg-1", "--file", "correction.txt", "--receipt"]));
        Assert.Throws<CliArgumentException>(() => SteerOptionsParser.Parse(
            ["room", "--execution", "exec-1", "--message-id", "msg-1", "--receipt", "--file", "correction.txt"]));
    }

    [Fact]
    public void Unknown_flags_are_rejected()
    {
        Assert.Throws<CliArgumentException>(() => SteerOptionsParser.Parse(
            ["room", "--execution", "exec-1", "--message-id", "msg-1", "--receipt", "--unknown"]));
    }

    [Fact]
    public void Options_without_values_are_rejected()
    {
        Assert.Throws<CliArgumentException>(() => SteerOptionsParser.Parse(["room", "--execution"]));
        Assert.Throws<CliArgumentException>(() => SteerOptionsParser.Parse(["room", "--message-id"]));
        Assert.Throws<CliArgumentException>(() => SteerOptionsParser.Parse(
            ["room", "--execution", "exec-1", "--message-id", "msg-1", "--file"]));
    }

    [Theory]
    [InlineData("--execution", "exec-2")]
    [InlineData("--message-id", "msg-2")]
    [InlineData("--file", "second.txt")]
    public void Duplicate_identity_or_payload_flags_are_rejected(string flag, string value)
    {
        Assert.Throws<CliArgumentException>(() => SteerOptionsParser.Parse(
            ["room", "--execution", "exec-1", "--message-id", "msg-1", "--file", "correction.txt", flag, value]));
    }
}
