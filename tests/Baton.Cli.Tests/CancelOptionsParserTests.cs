namespace Baton.Cli.Tests;

[Collection(WorkingDirectoryCollection.Name)]
public class CancelOptionsParserTests
{
    [Fact]
    public void A_room_directory_execution_id_and_bindings_option_parse_with_null_workflow_id()
    {
        var options = CancelOptionsParser.Parse(["task", "--execution", "exec-1", "--bindings", "bindings.json"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
        Assert.Equal("exec-1", options.ExecutionId);
        Assert.Equal("bindings.json", options.BindingsFilePath);
        Assert.Null(options.WorkflowId);
    }

    [Fact]
    public void An_explicit_workflow_id_option_overrides_the_null_default()
    {
        var options = CancelOptionsParser.Parse(
            ["task", "--execution", "exec-1", "--bindings", "bindings.json", "--workflow-id", "wf-1"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
        Assert.Equal("exec-1", options.ExecutionId);
        Assert.Equal("bindings.json", options.BindingsFilePath);
        Assert.Equal("wf-1", options.WorkflowId);
    }

    [Fact]
    public void Options_may_precede_the_positional_room_directory()
    {
        var options = CancelOptionsParser.Parse(["--execution", "exec-1", "--bindings", "bindings.json", "task"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
        Assert.Equal("exec-1", options.ExecutionId);
        Assert.Equal("bindings.json", options.BindingsFilePath);
    }

    [Fact]
    public void A_missing_room_directory_throws()
    {
        Assert.Throws<CliArgumentException>(() => CancelOptionsParser.Parse(["--execution", "exec-1", "--bindings", "bindings.json"]));
    }

    [Fact]
    public void An_omitted_execution_option_parses_as_null_room_level_targeting_1495()
    {
        var options = CancelOptionsParser.Parse(["task", "--bindings", "bindings.json"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
        Assert.Null(options.ExecutionId);
        Assert.Equal("bindings.json", options.BindingsFilePath);
    }

    [Fact]
    public void An_omitted_bindings_option_defaults_to_the_room_s_own_bindings_json_1607()
    {
        var options = CancelOptionsParser.Parse(["task", "--execution", "exec-1"]);

        Assert.Equal(Path.Combine(Path.GetFullPath("task"), "bindings.json"), options.BindingsFilePath);
    }

    [Fact]
    public void A_reason_option_is_carried_verbatim_and_defaults_to_null_2073()
    {
        Assert.Null(CancelOptionsParser.Parse(["task"]).Reason);
        Assert.Equal("lane is looping", CancelOptionsParser.Parse(["task", "--reason", "lane is looping"]).Reason);
        Assert.Throws<CliArgumentException>(() => CancelOptionsParser.Parse(["task", "--reason"]));
    }

    [Fact]
    public void An_option_missing_its_value_throws()
    {
        Assert.Throws<CliArgumentException>(() => CancelOptionsParser.Parse(["task", "--execution", "exec-1", "--bindings"]));
    }

    [Fact]
    public void An_unknown_option_throws()
    {
        Assert.Throws<CliArgumentException>(() => CancelOptionsParser.Parse(["task", "--execution", "exec-1", "--bindings", "bindings.json", "--nope"]));
    }

    [Fact]
    public void A_second_positional_argument_throws()
    {
        Assert.Throws<CliArgumentException>(() => CancelOptionsParser.Parse(["task", "extra", "--execution", "exec-1", "--bindings", "bindings.json"]));
    }
}
