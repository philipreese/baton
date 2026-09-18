using Baton.Vendors;

namespace Baton.Vendors.Tests;

public sealed class AgyTerminalRecoveryTests
{
    [Fact]
    public void Captured_backgrounded_command_stream_reports_the_unmatched_active_tool()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "agy-backgrounded-run-command.jsonl");
        var capturedStream = string.Join(' ', File.ReadLines(fixture));

        var fact = AgyTerminalStreamRecoveryDetector.Detect(capturedStream);

        Assert.NotNull(fact);
        Assert.Equal("run_command", fact.ToolName);
        Assert.Equal("dotnet build -warnaserror", fact.CommandLine);
    }

    [Fact]
    public void A_completed_tool_step_is_not_reported_as_outstanding()
    {
        const string capturedStream = """
            {"event":"step_update","step_update":{"step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"parameters":{"CommandLine":"dotnet build -warnaserror"}}}}
            {"event":"step_update","step_update":{"step_index":2,"state":"DONE","step_type":"tool","tool_name":"run_command","tool_info":{}}}
            {"event":"result","result":{"status":"SUCCESS","response":"done"}}
            """;

        var fact = AgyTerminalStreamRecoveryDetector.Detect(capturedStream);

        Assert.Null(fact);
    }
}
