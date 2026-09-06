using Baton.Cli;

namespace Baton.Cli.Tests;

/// <summary>
/// #1934 slice 1, item 6: the verbs' parsers. Every refusal is a <see cref="CliArgumentException"/>
/// with a sentence, never a bare framework exception — the same contract
/// <c>TrustOptionsParserTests</c> pins for its own verb.
/// </summary>
public sealed class QueueOptionsParserTests
{
    [Fact]
    public void Add_parses_every_flag_the_runner_used()
    {
        var options = QueueOptionsParser.Parse([
            "add", "1934-queue",
            "--role", "implement",
            "--spec", "brief.md",
            "--workspace", @"C:\repos\w1934",
            "--scope", "engine",
            "--adapter", "codex",
            "--model", "gpt-5.6-sol",
            "--effort", "high",
            "--timeout", "95",
            "--max-tool-steps", "900",
            "--token-budget", "4000000",
            "--override-runway", "milestone night",
            "--reason", "engine scope, vendor swap measured",
        ]);

        Assert.Equal(QueueVerb.Add, options.Verb);
        Assert.Equal("1934-queue", options.Tag);
        Assert.Equal("implement", options.Role);
        Assert.Equal("brief.md", options.SpecFilePath);
        Assert.Equal(@"C:\repos\w1934", options.WorkspaceDirectory);
        Assert.Equal("engine", options.ScopeClass);
        Assert.Equal("codex", options.Adapter);
        Assert.Equal("gpt-5.6-sol", options.Model);
        Assert.Equal("high", options.Effort);
        Assert.Equal(95, options.TimeoutMinutes);
        Assert.Equal(900, options.MaxToolSteps);
        Assert.Equal(4_000_000L, options.TokenBudget);
        Assert.Equal("milestone night", options.OverrideRunwayReason);
        Assert.Equal("engine scope, vendor swap measured", options.Reason);
        Assert.Null(options.Issue);
    }

    [Fact]
    public void Add_accepts_an_issue_instead_of_a_workspace()
    {
        var options = QueueOptionsParser.Parse(["add", "t", "--role", "implement", "--spec", "b.md", "--issue", "1934"]);

        Assert.Equal(1934, options.Issue);
        Assert.Null(options.WorkspaceDirectory);
    }

    [Fact]
    public void Add_refuses_an_issue_and_a_workspace_together()
    {
        var ex = Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse(
            ["add", "t", "--role", "implement", "--spec", "b.md", "--issue", "1", "--workspace", "C:\\x"]));

        Assert.Contains("--issue", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_refuses_neither_an_issue_nor_a_workspace()
    {
        Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse(
            ["add", "t", "--role", "implement", "--spec", "b.md"]));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("Has Spaces")]
    [InlineData("UPPER")]
    [InlineData("dot.ted")]
    public void Add_refuses_a_tag_that_is_not_a_slug(string tag)
    {
        // The tag names a file under ~/.baton/queue/specs, so an unconstrained one is a write outside
        // that directory from whatever composed the queue.
        Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse(
            ["add", tag, "--role", "implement", "--spec", "b.md", "--workspace", "C:\\x"]));
    }

    [Fact]
    public void Add_accepts_the_slugs_a_tag_is_meant_to_be()
    {
        // Control for the refusals above: the rule admits what an operator actually types.
        Assert.Equal("1934-queue", QueueOptionsParser.Parse(
            ["add", "1934-queue", "--role", "implement", "--spec", "b.md", "--workspace", "C:\\x"]).Tag);
        Assert.Equal("fix_login", QueueOptionsParser.Parse(
            ["add", "fix_login", "--role", "implement", "--spec", "b.md", "--workspace", "C:\\x"]).Tag);
    }

    [Fact]
    public void Add_refuses_an_unknown_scope_class()
    {
        var ex = Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse(
            ["add", "t", "--role", "implement", "--spec", "b.md", "--workspace", "C:\\x", "--scope", "infra"]));

        Assert.Contains("engine", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_refuses_an_axis_override_with_no_reason_but_allows_the_same_item_with_one()
    {
        string[] withoutReason = ["add", "t", "--role", "implement", "--spec", "b.md", "--workspace", "C:\\x", "--scope", "engine", "--model", "sonnet"];

        Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse(withoutReason));
        // The control arm: the same command plus --reason parses, so the refusal is about the missing
        // reason and not about the override itself.
        Assert.Equal("sonnet", QueueOptionsParser.Parse([.. withoutReason, "--reason", "deliberate"]).Model);
    }

    [Fact]
    public void An_axis_named_without_a_scope_class_needs_no_reason()
    {
        // There is no tier to depart from, so there is nothing for a reason to be a departure FROM.
        var options = QueueOptionsParser.Parse(
            ["add", "t", "--role", "implement", "--spec", "b.md", "--workspace", "C:\\x", "--model", "sonnet"]);

        Assert.Equal("sonnet", options.Model);
        Assert.Null(options.ScopeClass);
    }

    [Theory]
    [InlineData("--timeout", "0")]
    [InlineData("--max-tool-steps", "-1")]
    [InlineData("--token-budget", "0")]
    [InlineData("--issue", "0")]
    public void Add_refuses_a_non_positive_numeric_flag(string flag, string value)
    {
        Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse(
            ["add", "t", "--role", "implement", "--spec", "b.md", "--workspace", "C:\\x", flag, value]));
    }

    [Fact]
    public void Add_refuses_a_numeric_flag_that_is_not_a_number()
    {
        var ex = Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse(
            ["add", "t", "--role", "implement", "--spec", "b.md", "--workspace", "C:\\x", "--timeout", "soon"]));

        Assert.Contains("soon", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_refuses_an_option_with_no_value_and_an_unknown_option()
    {
        Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse(["add", "t", "--role"]));
        Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse(
            ["add", "t", "--role", "implement", "--spec", "b.md", "--workspace", "C:\\x", "--nonsense", "1"]));
    }

    [Theory]
    [InlineData("list", QueueVerb.List)]
    [InlineData("hold", QueueVerb.Hold)]
    [InlineData("resume", QueueVerb.Resume)]
    public void The_bare_verbs_take_no_arguments(string word, QueueVerb verb)
    {
        Assert.Equal(verb, QueueOptionsParser.Parse([word]).Verb);
        Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse([word, "extra"]));
    }

    [Fact]
    public void Import_takes_exactly_one_path()
    {
        Assert.Equal("q.json", QueueOptionsParser.Parse(["import", "q.json"]).ImportFilePath);
        Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse(["import"]));
        Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse(["import", "a", "b"]));
    }

    [Fact]
    public void An_unknown_or_missing_sub_verb_is_refused_with_the_usage()
    {
        Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse([]));
        var ex = Assert.Throws<CliArgumentException>(() => QueueOptionsParser.Parse(["drain"]));
        Assert.Contains("drain", ex.Message, StringComparison.Ordinal);
    }
}
