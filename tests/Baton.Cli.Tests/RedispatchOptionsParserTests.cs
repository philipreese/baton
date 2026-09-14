using Baton.Domain;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton redispatch</c>'s argument parsing (#1441): parse-level shapes only, mirroring
/// <see cref="DispatchOptionsParserTests"/>'s own pin for <c>baton dispatch</c> — what each override
/// flag means once applied is <see cref="RedispatchBindingTests"/>'s job, not this file's.
/// </summary>
public class RedispatchOptionsParserTests
{
    [Fact]
    public void Parses_the_parent_room_dir_and_every_override_flag()
    {
        var options = RedispatchOptionsParser.Parse(
            [
                "parent-room", "--spec", "amended.md", "--adapter", "agy", "--model", "opus",
                "--effort", "careful", "--workspace", ".", "--output", "custom.md", "--timeout", "90",
                "--label", "env-snapshot lane",
            ]);

        Assert.EndsWith("parent-room", options.ParentRoomDirectoryPath);
        Assert.Equal("amended.md", options.SpecFilePath);
        Assert.Equal("agy", options.Adapter);
        Assert.Equal("opus", options.Model);
        Assert.Equal("careful", options.Effort);
        Assert.Equal(Path.GetFullPath("."), options.WorkspaceDirectory);
        Assert.Equal(Path.GetFullPath("custom.md"), options.OutputPath);
        Assert.Equal(TimeSpan.FromMinutes(90), options.Timeout);
        Assert.Equal("env-snapshot lane", options.Label);
    }

    [Fact]
    public void Every_override_flag_defaults_to_null_when_absent()
    {
        var options = RedispatchOptionsParser.Parse(["parent-room"]);

        Assert.Null(options.SpecFilePath);
        Assert.Null(options.Adapter);
        Assert.Null(options.Model);
        Assert.Null(options.Effort);
        Assert.Null(options.WorkspaceDirectory);
        Assert.Null(options.OutputPath);
        Assert.Null(options.Timeout);
        Assert.Null(options.Label);
        Assert.False(options.LabelSpecified);
        Assert.Null(options.Attachments);
    }

    /// <summary>#1576: mirrors <c>DispatchOptionsParserTests.Parses_repeatable_attach_flags_in_order</c>.</summary>
    [Fact]
    public void Parses_repeatable_attach_flags_in_order()
    {
        var options = RedispatchOptionsParser.Parse(
            ["parent-room", "--spec", "amended.md", "--attach", "context.txt", "--attach", "notes.md"]);

        Assert.NotNull(options.Attachments);
        Assert.Equal(new[] { "context.txt", "notes.md" }, options.Attachments);
    }

    /// <summary>
    /// Merge of #1576 (--attach), #1686 review F2 (--max-tool-steps), #1691 (--billed-rate-limit) and
    /// #1702 (--verify): all five parse together alongside --spec. Pins the seam #1704, #1691 and #1702
    /// all landed in — a positional slip in <see cref="RedispatchOptions"/>'s own parameter list (four
    /// trailing optionals, all defaulted) would silently drop one of them rather than fail to compile.
    /// </summary>
    [Fact]
    public void Parses_spec_attach_max_tool_steps_billed_rate_limit_and_verify_together()
    {
        var options = RedispatchOptionsParser.Parse(
            [
                "parent-room", "--spec", "amended.md", "--attach", "context.txt",
                "--max-tool-steps", "200", "--billed-rate-limit", "250000",
                "--verify", "pixi run gates-quiet",
            ]);

        Assert.Equal("amended.md", options.SpecFilePath);
        Assert.Equal(new[] { "context.txt" }, options.Attachments);
        Assert.Equal(200, options.MaxToolSteps);
        Assert.Equal(250_000, options.BilledRateLimit);
        Assert.Equal("pixi run gates-quiet", options.VerifyCommand);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("200", 200)]
    public void Max_tool_steps_accepts_non_negative_whole_numbers(string rawValue, int expected)
    {
        var options = RedispatchOptionsParser.Parse(["parent-room", "--max-tool-steps", rawValue]);

        Assert.Equal(expected, options.MaxToolSteps);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void Max_tool_steps_refuses_negative_or_non_numeric_values(string rawValue)
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => RedispatchOptionsParser.Parse(["parent-room", "--max-tool-steps", rawValue]));

        Assert.Contains("non-negative whole number", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Attach_without_value_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => RedispatchOptionsParser.Parse(["parent-room", "--spec", "amended.md", "--attach"]));

        Assert.Contains("--attach", ex.TryInvocation);
    }

    [Fact]
    public void A_label_is_sanitized_the_same_way_dispatchs_own_is()
    {
        // Shared sanitizer (DispatchOptionsParser.SanitizeLabel) -- one cap/trim/newline-fold rule for
        // both verbs, not a second implementation that could drift.
        var raw = new string('x', 90);
        var options = RedispatchOptionsParser.Parse(["parent-room", "--label", raw]);
        Assert.Equal(60, options.Label!.Length);
        Assert.True(options.LabelSpecified);
    }

    [Fact]
    public void A_blank_label_sets_LabelSpecified_true_and_Label_null()
    {
        var options = RedispatchOptionsParser.Parse(["parent-room", "--label", "   "]);
        Assert.Null(options.Label);
        Assert.True(options.LabelSpecified);
    }

    [Fact]
    public void A_missing_room_dir_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(() => RedispatchOptionsParser.Parse(["--spec", "amended.md"]));
        Assert.Contains("<room-dir>", ex.Message);
    }

    [Fact]
    public void A_second_positional_argument_is_a_typed_argument_error()
    {
        Assert.Throws<CliArgumentException>(() => RedispatchOptionsParser.Parse(["parent-room", "extra"]));
    }

    [Fact]
    public void An_unknown_option_is_a_typed_argument_error()
    {
        Assert.Throws<CliArgumentException>(() => RedispatchOptionsParser.Parse(["parent-room", "--nope", "x"]));
    }

    [Fact]
    public void An_option_missing_its_value_names_the_option_in_the_Try_line()
    {
        var ex = Assert.Throws<CliArgumentException>(() => RedispatchOptionsParser.Parse(["parent-room", "--spec"]));
        Assert.Contains("--spec", ex.TryInvocation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("nope")]
    public void A_non_positive_or_unparseable_timeout_is_a_typed_argument_error(string rawValue)
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => RedispatchOptionsParser.Parse(["parent-room", "--timeout", rawValue]));
        Assert.Contains("--timeout", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timeout_above_the_24h_ceiling_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => RedispatchOptionsParser.Parse(["parent-room", "--timeout", "1441"]));
        Assert.Contains("ceiling", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void There_is_no_room_dir_flag_a_fresh_room_directory_is_always_generated()
    {
        Assert.Throws<CliArgumentException>(() => RedispatchOptionsParser.Parse(["parent-room", "--room-dir", "x"]));
    }

    [Fact]
    public void The_new_room_directory_is_unique_per_invocation_and_never_equals_the_parent()
    {
        var first = RedispatchOptionsParser.Parse(["parent-room"]);
        var second = RedispatchOptionsParser.Parse(["parent-room"]);

        Assert.NotEqual(first.RoomDirectoryPath, second.RoomDirectoryPath);
        Assert.NotEqual(first.ParentRoomDirectoryPath, first.RoomDirectoryPath);
    }

    [Fact]
    public void The_new_room_directory_lives_under_BatonPaths_Rooms()
    {
        var options = RedispatchOptionsParser.Parse(["parent-room"]);

        Assert.StartsWith(
            Path.GetFullPath(Baton.Status.BatonPaths.Rooms), options.RoomDirectoryPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_workstream_is_sanitized_the_same_way_dispatchs_own_is()
    {
        // Shared sanitizer (DispatchOptionsParser.SanitizeWorkstream) -- one grammar/cap rule for both
        // verbs, not a second implementation that could drift.
        var options = RedispatchOptionsParser.Parse(["parent-room", "--workstream", "w1619"]);
        Assert.Equal("w1619", options.Workstream);
        Assert.True(options.WorkstreamSpecified);
    }

    [Fact]
    public void A_blank_workstream_sets_WorkstreamSpecified_true_and_Workstream_null()
    {
        var options = RedispatchOptionsParser.Parse(["parent-room", "--workstream", "   "]);
        Assert.Null(options.Workstream);
        Assert.True(options.WorkstreamSpecified);
    }

    [Fact]
    public void A_path_unsafe_workstream_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => RedispatchOptionsParser.Parse(["parent-room", "--workstream", "a/b"]));
        Assert.Contains("--workstream", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Workstream_is_absent_and_unspecified_when_never_passed()
    {
        var options = RedispatchOptionsParser.Parse(["parent-room"]);
        Assert.Null(options.Workstream);
        Assert.False(options.WorkstreamSpecified);
    }

    /// <summary>
    /// #1691: <c>redispatch</c> takes the flag too. #1686 review F2 found <c>--max-tool-steps</c>
    /// shipped on <c>dispatch</c> only, so an operator's escape hatch silently evaporated the moment
    /// they redispatched with an amended brief; this axis does not repeat that.
    /// </summary>
    [Fact]
    public void The_billed_rate_limit_option_parses_and_defaults_to_null()
    {
        Assert.Equal(250_000, RedispatchOptionsParser.Parse(["parent-room", "--billed-rate-limit", "250000"]).BilledRateLimit);
        Assert.Null(RedispatchOptionsParser.Parse(["parent-room"]).BilledRateLimit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("nonsense")]
    public void A_non_positive_or_non_numeric_billed_rate_limit_is_a_typed_argument_error(string rawValue)
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => RedispatchOptionsParser.Parse(["parent-room", "--billed-rate-limit", rawValue]));

        Assert.Contains("--billed-rate-limit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_usage_line_advertises_billed_rate_limit()
    {
        Assert.Contains("--billed-rate-limit <n>", RedispatchOptionsParser.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_a_complete_declared_task_size()
    {
        var options = RedispatchOptionsParser.Parse(
            ["parent-room", "--declared-size", "medium", "--size-rationale", "one durable seam"]);

        Assert.Equal(DeclaredTaskSize.Medium, options.DeclaredTaskSize!.Size);
        Assert.Equal("one durable seam", options.DeclaredTaskSize.Rationale);
    }
}
