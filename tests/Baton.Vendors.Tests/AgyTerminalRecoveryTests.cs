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

    [Fact]
    public void The_incremental_observer_retains_active_state_past_the_stdout_tail_boundary()
    {
        var observe = AgyTerminalStreamRecoveryDetector.CreateObserver();

        Assert.Null(observe("""
            {"event":"step_update","step_update":{"step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"parameters":{"CommandLine":"dotnet build -warnaserror"}}}}
            """));

        foreach (var _ in Enumerable.Range(0, 250))
        {
            Assert.Null(observe(new string('x', 10)));
        }

        var fact = observe("""
            {"event":"result","result":{"status":"SUCCESS","response":"done"}}
            """);

        Assert.NotNull(fact);
        Assert.Equal("run_command", fact.ToolName);
        Assert.Equal("dotnet build -warnaserror", fact.CommandLine);
    }

    [Fact]
    public void An_unmatched_completion_does_not_erase_concurrent_same_tool_steps()
    {
        const string capturedStream = """
            {"event":"step_update","step_update":{"step_index":1,"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"parameters":{"CommandLine":"first"}}}}
            {"event":"step_update","step_update":{"step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"parameters":{"CommandLine":"second"}}}}
            {"event":"step_update","step_update":{"step_index":99,"state":"DONE","step_type":"tool","tool_name":"run_command","tool_info":{}}}
            {"event":"result","result":{"status":"SUCCESS","response":"done"}}
            """;

        var fact = AgyTerminalStreamRecoveryDetector.Detect(capturedStream);

        Assert.NotNull(fact);
        Assert.Equal("run_command", fact.ToolName);
        Assert.Null(fact.CommandLine);
    }

    [Fact]
    public void An_unmatched_completion_closes_the_sole_same_tool_candidate()
    {
        const string capturedStream = """
            {"event":"step_update","step_update":{"step_index":1,"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"parameters":{"CommandLine":"first"}}}}
            {"event":"step_update","step_update":{"step_index":99,"state":"DONE","step_type":"tool","tool_name":"run_command","tool_info":{}}}
            {"event":"result","result":{"status":"SUCCESS","response":"done"}}
            """;

        Assert.Null(AgyTerminalStreamRecoveryDetector.Detect(capturedStream));
    }
}
