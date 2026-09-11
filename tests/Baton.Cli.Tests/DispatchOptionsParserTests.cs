namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton dispatch</c>'s argument parsing: the name is positional and <c>--spec</c> is optional at parse
/// time (<see cref="DispatchOptionsParser"/> has the why). These pin the parse-level shapes — the
/// positional name, an optional spec, and a typed error on every malformed invocation.
/// </summary>
public class DispatchOptionsParserTests
{
    [Fact]
    public void Parses_the_name_spec_adapter_room_dir_and_workflow_id()
    {
        var options = DispatchOptionsParser.Parse(
            ["review", "--spec", "task.md", "--adapter", "agy", "--room-dir", "out", "--workflow-id", "wf"]);

        Assert.Equal("review", options.Name);
        Assert.Equal("task.md", options.SpecFilePath);
        Assert.Equal("agy", options.Adapter);
        Assert.Equal("wf", options.WorkflowId);
        Assert.EndsWith("out", options.RoomDirectoryPath);
    }

    [Fact]
    public void A_name_without_a_spec_parses_because_a_template_takes_none()
    {
        // The parser no longer requires --spec: a template dispatch has none, and rejecting it here
        // would refuse a valid invocation before the catalog is even consulted.
        var options = DispatchOptionsParser.Parse(["implement-review"]);

        Assert.Equal("implement-review", options.Name);
        Assert.Null(options.SpecFilePath);
    }

    [Fact]
    public void Parses_the_independent_model_effort_and_workspace_axes()
    {
        // Vendor/model/effort are three independent axes (0017/0033); the CLI exposes each, plus the
        // workspace the worker reads (#1082/#1083).
        var options = DispatchOptionsParser.Parse(
            ["advise", "--spec", "t.md", "--adapter", "claude", "--model", "opus", "--effort", "careful", "--workspace", "."]);

        Assert.Equal("claude", options.Adapter);
        Assert.Equal("opus", options.Model);
        Assert.Equal("careful", options.Effort);
        Assert.Equal(System.IO.Path.GetFullPath("."), options.WorkspaceDirectory);
    }

    [Fact]
    public void Parses_the_output_path_axis()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--output", "custom-report.md"]);
        Assert.Equal(System.IO.Path.GetFullPath("custom-report.md"), options.OutputPath);
    }

    [Fact]
    public void The_new_axis_flags_default_to_null_when_absent()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md"]);
        Assert.Null(options.Model);
        Assert.Null(options.Effort);
        Assert.Null(options.WorkspaceDirectory);
        Assert.Null(options.Timeout);
    }

    [Fact]
    public void Parses_the_timeout_override_as_minutes()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--timeout", "90"]);
        Assert.Equal(TimeSpan.FromMinutes(90), options.Timeout);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("nope")]
    [InlineData("12.5")]
    public void A_non_positive_or_unparseable_timeout_is_a_typed_argument_error(string rawValue)
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--timeout", rawValue]));
        Assert.Contains("--timeout", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timeout_above_the_24h_ceiling_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--timeout", "1441"]));
        Assert.Contains("ceiling", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timeout_at_exactly_the_24h_ceiling_is_accepted()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--timeout", "1440"]);
        Assert.Equal(TimeSpan.FromHours(24), options.Timeout);
    }

    [Fact]
    public void A_missing_name_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(["--spec", "task.md"]));
        Assert.Contains("<name>", ex.Message);
        Assert.Equal("run 'baton templates' to see available role and template names.", ex.TryInvocation);
    }

    [Fact]
    public void An_option_missing_its_value_names_the_option_in_the_Try_line()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(["review", "--spec"]));
        Assert.NotNull(ex.TryInvocation);
        Assert.Contains("--spec", ex.TryInvocation, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1382 F10.1: DispatchCommand's missing-<c>--spec</c> refusal suggests
    /// <c>baton dispatch &lt;role&gt; --spec &lt;spec-file&gt;</c> -- feed that shape back through the
    /// real parser rather than only pinning that the string was set (this is what would have caught
    /// F5's stale worktree suggestion).
    /// </summary>
    [Fact]
    public void The_suggested_missing_spec_invocation_round_trips_through_this_parser()
    {
        var options = DispatchOptionsParser.Parse(["review", "--spec", "<spec-file>"]);

        Assert.Equal("review", options.Name);
        Assert.Equal("<spec-file>", options.SpecFilePath);
    }

    [Fact]
    public void An_unknown_option_is_a_typed_argument_error()
    {
        Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(["review", "--spec", "t.md", "--nope", "x"]));
    }

    [Fact]
    public void A_second_positional_argument_is_a_typed_argument_error()
    {
        Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(["review", "extra", "--spec", "t.md"]));
    }

    [Fact]
    public void Parses_the_label_axis()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--label", "env-snapshot lane"]);
        Assert.Equal("env-snapshot lane", options.Label);
    }

    [Fact]
    public void A_label_is_absent_when_never_passed()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md"]);
        Assert.Null(options.Label);
    }

    [Fact]
    public void A_label_is_trimmed_and_internal_newlines_are_folded_to_spaces()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--label", "  line one\nline two  "]);
        Assert.Equal("line one line two", options.Label);
    }

    [Fact]
    public void A_label_longer_than_60_characters_is_capped()
    {
        var raw = new string('x', 90);
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--label", raw]);
        Assert.Equal(60, options.Label!.Length);
        Assert.Equal(new string('x', 60), options.Label);
    }

    [Fact]
    public void A_blank_label_is_treated_as_absent_rather_than_refused()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--label", "   "]);
        Assert.Null(options.Label);
    }

    [Fact]
    public void A_label_cut_does_not_split_a_surrogate_pair()
    {
        var raw = new string('x', 59) + "\U0001F680";
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--label", raw]);
        Assert.Equal(new string('x', 59), options.Label);
        Assert.False(char.IsHighSurrogate(options.Label![^1]));
    }

    [Fact]
    public void The_default_room_directory_is_unique_per_invocation_so_a_redispatch_does_not_resume()
    {
        // A one-shot dispatch must run anew each time; two default directories that collided would
        // make the second invocation resume — and replay — the first's terminal snapshot.
        var first = DispatchOptionsParser.Parse(["review", "--spec", "t.md"]).RoomDirectoryPath;
        var second = DispatchOptionsParser.Parse(["review", "--spec", "t.md"]).RoomDirectoryPath;
        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// R2 (#1354/#1380, finding 2) -- see the parser's own comment for why the default must sit outside
    /// any workspace a dispatch might audit. <c>Baton.Status.BatonPaths.Rooms</c> is the one place that
    /// root is resolved from (honouring <c>BATON_HOME</c>); this pins that the default is built from it,
    /// not re-derives it.
    /// </summary>
    [Fact]
    public void The_default_room_directory_lives_under_BatonPaths_Rooms_not_the_current_directory()
    {
        var options = DispatchOptionsParser.Parse(["review", "--spec", "t.md"]);

        Assert.StartsWith(
            Path.GetFullPath(Baton.Status.BatonPaths.Rooms), options.RoomDirectoryPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Path.GetFullPath(Directory.GetCurrentDirectory()), options.RoomDirectoryPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parses_a_single_attach_flag()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--attach", "context.txt"]);
        Assert.NotNull(options.Attachments);
        var attached = Assert.Single(options.Attachments);
        Assert.Equal("context.txt", attached);
    }

    [Fact]
    public void Parses_repeatable_attach_flags_in_order()
    {
        var options = DispatchOptionsParser.Parse(
            ["advise", "--spec", "t.md", "--attach", "context.txt", "--attach", "notes.md"]);
        Assert.NotNull(options.Attachments);
        Assert.Equal(new[] { "context.txt", "notes.md" }, options.Attachments);
    }

    [Fact]
    public void Attach_without_value_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--attach"]));
        Assert.NotNull(ex.TryInvocation);
        Assert.Contains("--attach", ex.TryInvocation);
    }

    [Fact]
    public void Parses_list_capabilities_flag_without_name()
    {
        var options = DispatchOptionsParser.Parse(["--list-capabilities"]);
        Assert.True(options.ListCapabilities);
    }

    [Fact]
    public void List_capabilities_flag_with_a_name_is_refused()
    {
        // #1500 second-reader MED-5: this combination used to parse silently, dispatch nothing, and
        // exit 0 — the one axis where a silent success is most expensive. Pinning refusal instead.
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["review", "--list-capabilities"]));
        Assert.Contains("--list-capabilities", ex.Message);
        Assert.Contains("review", ex.TryInvocation);
    }

    [Fact]
    public void Parses_the_workstream_axis()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--workstream", "1619"]);
        Assert.Equal("1619", options.Workstream);
    }

    [Fact]
    public void A_workstream_is_absent_when_never_passed()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md"]);
        Assert.Null(options.Workstream);
    }

    [Fact]
    public void A_blank_workstream_is_treated_as_absent_rather_than_refused()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--workstream", "   "]);
        Assert.Null(options.Workstream);
    }

    [Fact]
    public void A_workstream_is_trimmed_but_not_otherwise_folded()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--workstream", "  w1619  "]);
        Assert.Equal("w1619", options.Workstream);
    }

    [Theory]
    [InlineData("w1619")]
    [InlineData("1619")]
    [InlineData("review-worktree")]
    [InlineData("a.b_c-9")]
    public void A_path_safe_slug_is_accepted_verbatim(string slug)
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--workstream", slug]);
        Assert.Equal(slug, options.Workstream);
    }

    /// <summary>
    /// #1619: a valid slug is folded to lowercase -- <see cref="DispatchOptionsParser.SanitizeWorkstream"/>
    /// has the pointer to the rationale.
    /// </summary>
    [Theory]
    [InlineData("W1619", "w1619")]
    [InlineData("Review-Worktree", "review-worktree")]
    [InlineData("A.B_C-9", "a.b_c-9")]
    public void A_valid_slug_is_folded_to_lowercase(string rawValue, string expected)
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--workstream", rawValue]);
        Assert.Equal(expected, options.Workstream);
    }

    /// <summary>
    /// #1619: unlike --label, a workstream slug is later used (lowercase-folded) as a Windows
    /// directory name (WorkstreamJunctionLinker) -- so it is refused outright, never folded, when it
    /// carries a character unsafe as one path segment.
    /// </summary>
    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a&b")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("-leading-dash")]
    [InlineData(" has space")]
    public void A_path_unsafe_workstream_is_a_typed_argument_error(string rawValue)
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--workstream", rawValue]));
        Assert.Contains("--workstream", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_workstream_longer_than_60_characters_is_a_typed_argument_error()
    {
        var raw = new string('x', 61);
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--workstream", raw]));
        Assert.Contains("--workstream", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_workstream_at_exactly_60_characters_is_accepted()
    {
        var raw = new string('x', 60);
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--workstream", raw]);
        Assert.Equal(raw, options.Workstream);
    }

    [Fact]
    public void The_repo_option_parses_to_an_absolute_path()
    {
        var options = DispatchOptionsParser.Parse(["review", "--spec", "t.md", "--repo", "."]);

        Assert.Equal(Path.GetFullPath("."), options.RepoPath);
    }

    [Fact]
    public void Omitting_repo_leaves_it_null()
    {
        var options = DispatchOptionsParser.Parse(["review", "--spec", "t.md"]);

        Assert.Null(options.RepoPath);
    }

    /// <summary>
    /// #1653 review F5: the parity arm for <c>StatusOptionsParserTests.A_repo_option_with_no_value_throws</c>.
    /// The two parsers reach the same refusal by different routes — Status validates inline, Dispatch
    /// goes through the shared <c>RequireValue</c> helper — so "the other one is covered" is not
    /// coverage of this one.
    /// </summary>
    [Fact]
    public void A_repo_option_with_no_value_throws()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["review", "--spec", "t.md", "--repo"]));

        Assert.Contains("--repo", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>#1381: --continue mirrors --repo's own resolve-to-absolute-path rule.</summary>
    [Fact]
    public void The_continue_option_parses_to_an_absolute_path()
    {
        var options = DispatchOptionsParser.Parse(["review", "--spec", "t.md", "--continue", "."]);

        Assert.Equal(Path.GetFullPath("."), options.ContinueFromRoomDirectoryPath);
    }

    [Fact]
    public void Omitting_continue_leaves_it_null()
    {
        var options = DispatchOptionsParser.Parse(["review", "--spec", "t.md"]);

        Assert.Null(options.ContinueFromRoomDirectoryPath);
    }

    [Fact]
    public void A_continue_option_with_no_value_throws()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["review", "--spec", "t.md", "--continue"]));

        Assert.Contains("--continue", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>#1686 review F11: --max-tool-steps mirrors --token-budget end to end.</summary>
    [Fact]
    public void The_max_tool_steps_option_parses_to_a_positive_int()
    {
        var options = DispatchOptionsParser.Parse(["implement", "--spec", "t.md", "--max-tool-steps", "100"]);

        Assert.Equal(100, options.MaxToolSteps);
    }

    [Fact]
    public void Omitting_max_tool_steps_leaves_it_null()
    {
        var options = DispatchOptionsParser.Parse(["implement", "--spec", "t.md"]);

        Assert.Null(options.MaxToolSteps);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    public void A_non_positive_or_non_numeric_max_tool_steps_throws(string rawValue)
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["implement", "--spec", "t.md", "--max-tool-steps", rawValue]));

        Assert.Contains("--max-tool-steps", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>#1702: --verify mirrors --token-budget's own escape-hatch shape end to end.</summary>
    [Fact]
    public void The_verify_option_parses_verbatim()
    {
        var options = DispatchOptionsParser.Parse(["implement", "--spec", "t.md", "--verify", "python -m pytest"]);

        Assert.Equal("python -m pytest", options.VerifyCommand);
    }

    [Fact]
    public void Omitting_verify_leaves_it_null()
    {
        var options = DispatchOptionsParser.Parse(["implement", "--spec", "t.md"]);

        Assert.Null(options.VerifyCommand);
    }

    /// <summary>#1691: --billed-rate-limit mirrors --token-budget end to end.</summary>
    [Fact]
    public void The_billed_rate_limit_option_parses_to_a_positive_long()
    {
        var options = DispatchOptionsParser.Parse(["implement", "--spec", "t.md", "--billed-rate-limit", "250000"]);

        Assert.Equal(250_000, options.BilledRateLimit);
    }

    [Fact]
    public void Omitting_billed_rate_limit_leaves_it_null()
    {
        var options = DispatchOptionsParser.Parse(["implement", "--spec", "t.md"]);

        Assert.Null(options.BilledRateLimit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    public void A_non_positive_or_non_numeric_billed_rate_limit_throws(string rawValue)
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["implement", "--spec", "t.md", "--billed-rate-limit", rawValue]));

        Assert.Contains("--billed-rate-limit", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1691: the flag appears in the usage line every parse error prints. A flag that exists and is
    /// undiscoverable is the documentation defect docs/agents/developing-baton.md's writing rule names, and the usage string
    /// is the only place a CLI reader looks.
    /// </summary>
    [Fact]
    public void The_usage_line_advertises_billed_rate_limit()
    {
        Assert.Contains("--billed-rate-limit <n>", DispatchOptionsParser.Usage, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1702: the usage line advertises --verify for the same reason the billed-rate-limit arm above
    /// asserts its own — one flag added without the other's usage entry is the discoverability defect,
    /// not a cosmetic omission.
    /// </summary>
    [Fact]
    public void The_usage_line_advertises_verify()
    {
        Assert.Contains("--verify <cmd>", DispatchOptionsParser.Usage, StringComparison.Ordinal);
    }

    // #1788: --expect-pr mirrors --verify's own escape-hatch shape (a single explicit value, not a
    // bare flag), except its domain is a literal true/false rather than free text.

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("True", true)]
    [InlineData("FALSE", false)]
    public void The_expect_pr_option_parses_a_literal_bool_case_insensitively(string rawValue, bool expected)
    {
        var options = DispatchOptionsParser.Parse(["implement", "--spec", "t.md", "--expect-pr", rawValue]);

        Assert.Equal(expected, options.ExpectPr);
    }

    [Fact]
    public void Omitting_expect_pr_leaves_it_null()
    {
        var options = DispatchOptionsParser.Parse(["implement", "--spec", "t.md"]);

        Assert.Null(options.ExpectPr);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("")]
    public void A_non_boolean_expect_pr_value_throws(string rawValue)
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["implement", "--spec", "t.md", "--expect-pr", rawValue]));

        Assert.Contains("--expect-pr", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_usage_line_advertises_expect_pr()
    {
        Assert.Contains("--expect-pr <true|false>", DispatchOptionsParser.Usage, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1518: a scout question's inline source — parsed the same way every other free-text flag value
    /// is, no sanitization (unlike <c>--label</c>) because this string becomes the task prompt itself,
    /// not display text.
    /// </summary>
    [Fact]
    public void Parses_spec_text_verbatim()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec-text", "What does baton cancel do today?"]);

        Assert.Equal("What does baton cancel do today?", options.SpecText);
        Assert.Null(options.SpecFilePath);
        Assert.False(options.SpecFromStdin);
    }

    /// <summary>#1518: <c>--spec -</c> reuses the existing <c>--spec</c> flag rather than adding a fourth one.</summary>
    [Fact]
    public void A_dash_spec_value_selects_stdin_and_clears_the_file_path()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "-"]);

        Assert.True(options.SpecFromStdin);
        Assert.Null(options.SpecFilePath);
        Assert.Null(options.SpecText);
    }

    /// <summary>
    /// A blank <c>--spec-text</c> has no sane invocation to correct into ("did you mean to pass
    /// nothing?") the way an empty <c>--label</c> does — unlike a label, this string becomes the whole
    /// task prompt, so a silent no-op dispatch is the wrong failure mode. Refused at parse time, like
    /// every other malformed <c>baton dispatch</c> argument.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_spec_text_is_a_typed_argument_error(string blank)
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["advise", "--spec-text", blank]));

        Assert.Contains("--spec-text", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1518: <see cref="DispatchOptionsParser"/>'s own remarks above the exclusivity check have the
    /// rationale (record-once, not restated here). Whether at least one source is required at all is a
    /// catalog question the parser does not answer (a template dispatch takes none) —
    /// <see cref="A_name_without_a_spec_parses_because_a_template_takes_none"/> already pins that this
    /// parser stays permissive on the "none" side.
    /// </summary>
    [Fact]
    public void A_spec_file_and_spec_text_together_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--spec-text", "inline text"]));

        Assert.Contains("--spec-text", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Same refusal as the file-plus-text combination, for the stdin form of <c>--spec</c>.</summary>
    [Fact]
    public void A_dash_spec_and_spec_text_together_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["advise", "--spec", "-", "--spec-text", "inline text"]));

        Assert.Contains("--spec-text", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Repeating the SAME flag is last-wins, same convention every other repeatable-in-practice flag in
    /// this parser follows — only naming two DIFFERENT sources is refused.
    /// </summary>
    [Fact]
    public void A_repeated_spec_flag_is_last_wins()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "first.md", "--spec", "second.md"]);

        Assert.Equal("second.md", options.SpecFilePath);
    }

    [Fact]
    public void A_repeated_spec_text_flag_is_last_wins()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec-text", "first", "--spec-text", "second"]);

        Assert.Equal("second", options.SpecText);
    }

    [Fact]
    public void The_usage_line_advertises_spec_text_and_the_dash_stdin_form()
    {
        Assert.Contains("--spec-text <text>", DispatchOptionsParser.Usage, StringComparison.Ordinal);
        Assert.Contains("--spec -", DispatchOptionsParser.Usage, StringComparison.Ordinal);
    }

    /// <summary>#1848: the reason is the audit record, so the flag cannot be passed without one.</summary>
    [Fact]
    public void The_override_runway_flag_carries_its_reason()
    {
        var options = DispatchOptionsParser.Parse(
            ["review", "--spec", "t.md", "--override-runway", "  conductor lane, week resets in 2h  "]);

        Assert.Equal("conductor lane, week resets in 2h", options.OverrideRunwayReason);
    }

    [Fact]
    public void Omitting_override_runway_leaves_it_null()
    {
        Assert.Null(DispatchOptionsParser.Parse(["review", "--spec", "t.md"]).OverrideRunwayReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_override_runway_reason_is_a_parse_error(string reason)
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["review", "--spec", "t.md", "--override-runway", reason]));

        Assert.Contains("--override-runway", ex.Message, StringComparison.Ordinal);
        Assert.Contains("needs a reason", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_override_runway_flag_with_no_value_throws()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["review", "--spec", "t.md", "--override-runway"]));

        Assert.Contains("--override-runway", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_usage_line_advertises_the_override_runway_flag()
    {
        Assert.Contains("--override-runway <reason>", DispatchOptionsParser.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void It_normalizes_direct_dispatch_requirement_declarations()
    {
        var options = DispatchOptionsParser.Parse([
            "advise", "--spec", "brief.md", "--require", " File-Write ", "--require", "artifact:Gate-Receipt.json",
        ]);

        Assert.Equal(["file-write", "artifact:gate-receipt.json"], options.Requirements);
        Assert.Contains("--require <capability>", DispatchOptionsParser.Usage, StringComparison.Ordinal);
    }
}
