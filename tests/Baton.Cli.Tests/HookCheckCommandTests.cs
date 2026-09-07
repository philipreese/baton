namespace Baton.Cli.Tests;

/// <summary>
/// #543: <see cref="HookCheckCommand"/> is the executable target Claude Code spawns directly (exec
/// form, no shell) for every <c>PreToolUse</c> event. These drive <see cref="HookCheckCommand.Execute"/>
/// directly against the exact stdin shape <c>.vendor-survey/corpus/claude__hooks.md</c> documents
/// (<c>{"tool_name": "...", ...}</c>), rather than only asserting against pre-shaped fixtures, so a
/// regression in field-name handling shows up here.
/// </summary>
public class HookCheckCommandTests
{
    [Fact]
    public void A_tool_named_in_the_denied_list_is_blocked_with_exit_code_2()
    {
        using var stdin = new StringReader("""{"tool_name": "Bash", "tool_input": {"command": "ls"}}""");
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, "claude:Edit,Write,Bash");

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains("Bash", stderr.ToString());
    }

    [Fact]
    public void A_tool_not_named_in_the_denied_list_is_allowed()
    {
        using var stdin = new StringReader("""{"tool_name": "Read", "tool_input": {"file_path": "x"}}""");
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, "claude:Edit,Write,Bash");

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_or_blank_denied_list_now_denies_because_the_gate_cannot_know(string? deniedToolsRaw)
    {
        using var stdin = new StringReader("""{"tool_name": "Bash"}""");
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, deniedToolsRaw);

        // #600 inverted this deliberately. It used to allow, which meant "AER set the list and nothing
        // is withheld" and "the list never arrived" were the same observable outcome — so a channel
        // that had stopped working looked exactly like one that was. An empty list AER actually sent
        // still allows; it now arrives tagged (`claude:`), which is what makes the two tellable apart.
        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
    }

    [Fact]
    public void Matching_is_exact_not_a_substring_or_prefix_match()
    {
        // "Bash" denied must not accidentally deny "BashOutput" or match on a scoped
        // "Bash(rm *)"-shaped tool_input; BuildDisallowedTools never emits scoped entries, so
        // hook-check has no reason to parse them, but an accidental substring match would silently
        // widen the denial beyond what was actually withheld.
        using var stdin = new StringReader("""{"tool_name": "BashOutput"}""");
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, "claude:Bash");

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"tool_name": null}""")]
    [InlineData("""{"tool_name": ""}""")]
    [InlineData("[]")]
    [InlineData("""{"tool_name": "Write", "tool_input": {"file_path":""")] // truncated mid-payload
    public void Shapeless_stdin_fails_closed_because_writes_ride_this_hook_alone(string stdinContent)
    {
        // Every one of these allowed until #649, on the argument that --disallowedTools covered the
        // same names anyway. #649 moved the write tools off that flag so this hook could allow the
        // one write landing in BATON_OUTPUT_DIR — which makes a parse failure here an ungated write,
        // not a duplicate of an enforcement that still exists elsewhere.
        using var stdin = new StringReader(stdinContent);
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, "claude:Bash,Edit,Write");

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains("rather than allowing it unchecked", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_well_formed_payload_still_decides_on_the_grant_rather_than_denying_everything()
    {
        // The control for the theory above. Without it, a change that denied unconditionally would
        // pass every fail-closed assertion while making the gate useless — the worker cannot call a
        // single tool, and the reason string would be identical in both worlds.
        using var denied = new StringReader("""{"tool_name": "Bash"}""");
        using var allowed = new StringReader("""{"tool_name": "Read"}""");
        using var stderr = new StringWriter();

        Assert.Equal(
            HookCheckCommand.DeniedExitCode,
            HookCheckCommand.Execute(denied, stderr, "claude:Bash,Edit,Write"));
        Assert.Equal(
            HookCheckCommand.AllowedExitCode,
            HookCheckCommand.Execute(allowed, stderr, "claude:Bash,Edit,Write"));
    }

    [Fact]
    public void An_unreadable_stdin_denies_rather_than_allowing()
    {
        // The IOException arm, which no shaped-input case can reach.
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(new ThrowingReader(), stderr, "claude:Bash,Edit,Write");

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
    }

    private sealed class ThrowingReader : TextReader
    {
        public override string ReadToEnd() => throw new IOException("pipe closed");
    }

    [Fact]
    public void A_null_stdin_reader_throws_rather_than_silently_allowing()
    {
        using var stderr = new StringWriter();

        Assert.Throws<ArgumentNullException>(() => HookCheckCommand.Execute(null!, stderr, "claude:Bash"));
    }

    // --- #1459: the scoped-shell second layer -------------------------------------------------------

    private static int RunBash(
        string command, string? shellPatternsRaw, string? deniedShellPatternsRaw = null,
        TextWriter? stderr = null)
    {
        var payload = """{"tool_name": "Bash", "tool_input": {"command": COMMAND_JSON}}"""
            .Replace("COMMAND_JSON", System.Text.Json.JsonSerializer.Serialize(command));
        using var stdin = new StringReader(payload);
        // "claude:Read" -- Bash is granted (absent from the denied-tool list), which is what lets
        // execution reach the shell-pattern check under test.
        return HookCheckCommand.Execute(
            stdin, stderr ?? new StringWriter(), "claude:Read", shellPatternsRaw: shellPatternsRaw,
            deniedShellPatternsRaw: deniedShellPatternsRaw);
    }

    [Theory]
    [InlineData("git diff; echo escaped")] // #1461's measured escape row 1
    [InlineData("git diff | grep baseline")] // #1461's measured escape row 2
    public void Regression_the_measured_chaining_escapes_are_denied_by_the_hook(string command)
    {
        // See ShellCommandPatternMatcherTests for why these ran unblocked before #1459. This is the
        // same regression asserted end-to-end through the hook rather than the evaluator directly.
        using var stderr = new StringWriter();

        var exitCode = RunBash(command, "claude:git diff*", stderr: stderr);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains("shell grant", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_command_matching_the_scoped_pattern_is_allowed()
    {
        var exitCode = RunBash("git diff", "claude:git diff*");

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);
    }

    [Fact]
    public void A_segment_outside_the_scoped_patterns_denies_naming_the_segment()
    {
        using var stderr = new StringWriter();

        var exitCode = RunBash("git diff && npm install", "claude:git diff*", stderr: stderr);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains("npm install", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_segment_matching_the_standing_deny_list_denies_even_when_the_allow_list_would_admit_it()
    {
        using var stderr = new StringWriter();

        var exitCode = RunBash(
            "git diff && git push", "claude:git diff*,git push*", "claude:git push*", stderr);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains("git push", stderr.ToString(), StringComparison.Ordinal);
    }

    // #1920: the claude half of naming the granted alternative, appended here because the matcher is
    // vendor-agnostic. Both arms, because the clause is conditional — GrantedReadToolHint carries why.
    [Fact]
    public void A_denied_shell_command_names_claudes_read_tools_only_when_reads_are_granted()
    {
        using var readsGranted = new StringWriter();
        using var readsWithheld = new StringWriter();

        // Same refused command both times; only the withheld-tool list differs.
        RunBashWithDeniedTools("cat report.md", "claude:Edit", readsGranted);
        RunBashWithDeniedTools("cat report.md", "claude:Read", readsWithheld);

        Assert.Contains("read files with Read and search them with Grep",
            readsGranted.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Grep", readsWithheld.ToString(), StringComparison.Ordinal);
    }

    // The other false arm, whose reasoning lives beside the condition in HookCheckCommand: a denied
    // `git push --force` under implement's unscoped grant is told nothing about reading.
    [Fact]
    public void An_unscoped_grants_standing_deny_is_not_answered_with_the_read_tools()
    {
        using var stderr = new StringWriter();

        var exitCode = RunBashWithDeniedTools(
            "git push --force", "claude:Edit", stderr, shellPatternsRaw: "claude:",
            deniedShellPatternsRaw: "claude:git push*");

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.DoesNotContain("Grep", stderr.ToString(), StringComparison.Ordinal);
    }

    private static int RunBashWithDeniedTools(
        string command, string deniedToolsRaw, TextWriter stderr,
        string? shellPatternsRaw = "claude:git diff*", string? deniedShellPatternsRaw = null)
    {
        var payload = """{"tool_name": "Bash", "tool_input": {"command": COMMAND_JSON}}"""
            .Replace("COMMAND_JSON", System.Text.Json.JsonSerializer.Serialize(command));
        using var stdin = new StringReader(payload);
        return HookCheckCommand.Execute(
            stdin, stderr, deniedToolsRaw, shellPatternsRaw: shellPatternsRaw,
            deniedShellPatternsRaw: deniedShellPatternsRaw);
    }

    [Theory]
    [InlineData("git diff $(whoami)")]
    [InlineData("git diff `whoami`")]
    [InlineData("git diff > out.txt")]
    public void An_unparseable_command_fails_closed_under_a_scoped_grant(string command)
    {
        using var stderr = new StringWriter();

        var exitCode = RunBash(command, "claude:git diff*", stderr: stderr);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains("unparseable under scoped grant", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude:")] // Present, explicitly unscoped (empty pattern list)
    [InlineData(null)] // Absent -- the channel never arrived (an older AER, or a role never updated)
    [InlineData("agy:git diff*")] // WrongVendor
    public void An_unscoped_or_absent_shell_pattern_channel_leaves_the_second_layer_untouched(
        string? shellPatternsRaw)
    {
        // Point 4 of #1459's design. See HookCheckCommand.Decide's own comment on this branch for why
        // Absent/WrongVendor here reads opposite to the denied-tools channel above.
        var exitCode = RunBash("git diff; echo escaped", shellPatternsRaw);

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);
    }

    [Theory]
    [InlineData("git merge-base --is-ancestor a b", HookCheckCommand.AllowedExitCode)]
    [InlineData("git diff --stat", HookCheckCommand.AllowedExitCode)]
    [InlineData("git status", HookCheckCommand.AllowedExitCode)]
    [InlineData("git difftool --extcmd=calc -y HEAD~1 HEAD", HookCheckCommand.DeniedExitCode)]
    [InlineData("git grep -Ocalc foo", HookCheckCommand.DeniedExitCode)]
    [InlineData("git grep --open-files-in-pager=calc foo", HookCheckCommand.DeniedExitCode)]
    [InlineData("git -c alias.x=!calc x", HookCheckCommand.DeniedExitCode)]
    [InlineData("git push --dry-run", HookCheckCommand.DeniedExitCode)]
    [InlineData("gh api repos/x", HookCheckCommand.DeniedExitCode)]
    [InlineData("gh pr view 1", HookCheckCommand.AllowedExitCode)]
    // #1683 F1: denied by ABSENCE now -- `git grep*` left the review allow list (the harness's own
    // Grep tool covers a reviewer's need), which is what closes the four spellings that walked past
    // the anchored `git grep -O*` deny. Three of them were measured spawning a pager.
    [InlineData("git grep -nOcalc foo", HookCheckCommand.DeniedExitCode)]
    [InlineData("git grep --ignore-case -Ocalc foo", HookCheckCommand.DeniedExitCode)]
    [InlineData("git grep --open-files=calc foo", HookCheckCommand.DeniedExitCode)]
    [InlineData("git grep  -Ocalc foo", HookCheckCommand.DeniedExitCode)]
    // #1683 F2: the arbitrary file write `shell_commands_are_read_only: true` was asserting away.
    [InlineData("git log -1 --output=C:/x --format=format:y", HookCheckCommand.DeniedExitCode)]
    [InlineData("git show --output C:/x", HookCheckCommand.DeniedExitCode)]
    // A quote inside the option name: the shell splits words before removing quotes, so git still
    // receives `--output=C:/x`. Second-reader finding on this PR; see IsDeniedByOptionToken's remarks.
    [InlineData("git log -1 --outpu\"t\"=C:/x --format=format:y", HookCheckCommand.DeniedExitCode)]
    [InlineData("git log --oneline -5", HookCheckCommand.AllowedExitCode)] // the near-miss control
    [InlineData("git log --grep=\"--output\"", HookCheckCommand.AllowedExitCode)] // quoted VALUE, allowed
    // #1683 F3: both polarities of the respelled `git merge *` deny, one condition apart.
    [InlineData("git merge origin/main", HookCheckCommand.DeniedExitCode)]
    public void Review_role_command_allow_deny_polarities_from_catalog(string command, int expectedExitCode)
    {
        var review = Baton.Vendors.WorkerRoleCatalog.For("review");
        var shellPatternsRaw = review.Grant.ShellCommandPatterns is { Count: > 0 }
            ? "claude:" + string.Join(",", review.Grant.ShellCommandPatterns)
            : "claude:";
        var deniedShellPatternsRaw = review.Grant.DeniedShellCommandPatterns is { Count: > 0 }
            ? "claude:" + string.Join(",", review.Grant.DeniedShellCommandPatterns)
            : "claude:";
        var deniedShellOptionTokensRaw = review.Grant.DeniedShellOptionTokens is { Count: > 0 }
            ? "claude:" + string.Join(",", review.Grant.DeniedShellOptionTokens)
            : "claude:";

        var payload = """{"tool_name": "Bash", "tool_input": {"command": COMMAND_JSON}}"""
            .Replace("COMMAND_JSON", System.Text.Json.JsonSerializer.Serialize(command));
        using var stdin = new StringReader(payload);
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(
            stdin, stderr, "claude:Edit,Write",
            shellPatternsRaw: shellPatternsRaw,
            deniedShellPatternsRaw: deniedShellPatternsRaw,
            deniedShellOptionTokensRaw: deniedShellOptionTokensRaw);

        Assert.Equal(expectedExitCode, exitCode);
    }

    [Theory]
    // #1731: the write roles may not create/apply labels, merge a PR, or call the API on their own --
    // spec/baton.md §9 records the shape of the grant these rows exercise through the real catalog.
    [InlineData("implement", "gh pr create --title x --body-file y", HookCheckCommand.AllowedExitCode)]
    [InlineData("implement", "gh pr edit 1 --body-file y", HookCheckCommand.AllowedExitCode)]
    [InlineData("implement", "gh label create x", HookCheckCommand.DeniedExitCode)]
    [InlineData("implement", "gh pr edit 1 --add-label x", HookCheckCommand.DeniedExitCode)]
    [InlineData("implement", "gh pr edit 1 --remove-label x", HookCheckCommand.DeniedExitCode)]
    // Found-while-fixing, same PR (spec/baton.md §9 has the full "why"): `--label` at PR/issue
    // creation time attaches a label too, closed by adding it to the token list alongside
    // `--add-label`/`--remove-label`.
    [InlineData("implement", "gh pr create --title x --label operator-merge", HookCheckCommand.DeniedExitCode)]
    [InlineData("implement", "gh issue create --title x --label operator-merge", HookCheckCommand.DeniedExitCode)]
    [InlineData("implement", "gh pr merge 1 --squash", HookCheckCommand.DeniedExitCode)]
    [InlineData("implement", "gh api repos/a/b", HookCheckCommand.DeniedExitCode)]
    [InlineData("implement", "true && gh label create x", HookCheckCommand.DeniedExitCode)]
    // #1748 F1: the incident command riding a routine multi-line payload (heredoc, scripted step),
    // see spec/baton.md §9 for the mechanism.
    [InlineData("implement", "git status\ngh label create operator-merge", HookCheckCommand.DeniedExitCode)]
    // Operator ruling (spec/baton.md §9, this PR): on an UNSCOPED grant with a deny list, `$`/`<`/`>`/
    // `\` are ordinary characters, not fatal ones -- routine build-tooling syntax must not deny
    // outright. The earlier lane's permissive-metacharacter attempt (found-while-fixing #1733) was
    // itself reverted (found-while-fixing #1735 comment) over a different implementation
    // (substring/prefix matching with a character-class carve-out); this lane's token-head match is
    // the ruling's replacement mechanism, spelled out at `EvaluateChainedCommand`, not restated here.
    [InlineData("implement", "dotnet test > out.txt", HookCheckCommand.AllowedExitCode)]
    [InlineData("implement", "echo $PATH", HookCheckCommand.AllowedExitCode)]
    [InlineData("janitor", "gh pr create --title x --body-file y", HookCheckCommand.AllowedExitCode)]
    [InlineData("janitor", "gh pr edit 1 --body-file y", HookCheckCommand.AllowedExitCode)]
    [InlineData("janitor", "gh label create x", HookCheckCommand.DeniedExitCode)]
    [InlineData("janitor", "gh pr edit 1 --add-label x", HookCheckCommand.DeniedExitCode)]
    [InlineData("janitor", "gh pr edit 1 --remove-label x", HookCheckCommand.DeniedExitCode)]
    [InlineData("janitor", "gh pr merge 1 --squash", HookCheckCommand.DeniedExitCode)]
    [InlineData("janitor", "gh api repos/a/b", HookCheckCommand.DeniedExitCode)]
    [InlineData("janitor", "true && gh label create x", HookCheckCommand.DeniedExitCode)]
    [InlineData("janitor", "dotnet test > out.txt", HookCheckCommand.AllowedExitCode)]
    [InlineData("janitor", "echo $PATH", HookCheckCommand.AllowedExitCode)]
    public void Unscoped_write_role_denies_label_merge_and_api_writes_from_the_catalog(
        string roleId, string command, int expectedExitCode)
    {
        var role = Baton.Vendors.WorkerRoleCatalog.For(roleId);
        Assert.Null(role.Grant.ShellCommandPatterns); // stays unscoped -- item 1's "do NOT add" requirement
        var deniedShellPatternsRaw = role.Grant.DeniedShellCommandPatterns is { Count: > 0 }
            ? "claude:" + string.Join(",", role.Grant.DeniedShellCommandPatterns)
            : "claude:";
        var deniedShellOptionTokensRaw = role.Grant.DeniedShellOptionTokens is { Count: > 0 }
            ? "claude:" + string.Join(",", role.Grant.DeniedShellOptionTokens)
            : "claude:";

        var payload = """{"tool_name": "Bash", "tool_input": {"command": COMMAND_JSON}}"""
            .Replace("COMMAND_JSON", System.Text.Json.JsonSerializer.Serialize(command));
        using var stdin = new StringReader(payload);
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(
            stdin, stderr, "claude:Edit,Write",
            shellPatternsRaw: "claude:", // unscoped: Present, empty pattern list
            deniedShellPatternsRaw: deniedShellPatternsRaw,
            deniedShellOptionTokensRaw: deniedShellOptionTokensRaw);

        Assert.Equal(expectedExitCode, exitCode);
    }

    [Fact]
    public void The_option_token_channel_is_what_denies_the_output_write_not_the_pattern_lists()
    {
        // The discriminating control for the row above (#1683 F2, gate `v-and-v`): with the option-token
        // channel absent, the SAME command line under the SAME allow/deny pattern lists is allowed --
        // `git log*` admits it, no deny pattern is anchored where the option sits, and #659's
        // metacharacter scan never sees it because no redirection is involved. So the deny above is
        // this channel's doing, not something the pattern lists were already covering.
        var review = Baton.Vendors.WorkerRoleCatalog.For("review");
        var shellPatternsRaw = "claude:" + string.Join(",", review.Grant.ShellCommandPatterns!);
        var deniedShellPatternsRaw = "claude:" + string.Join(",", review.Grant.DeniedShellCommandPatterns!);
        const string command = "git log -1 --output=C:/x --format=format:y";

        var payload = """{"tool_name": "Bash", "tool_input": {"command": COMMAND_JSON}}"""
            .Replace("COMMAND_JSON", System.Text.Json.JsonSerializer.Serialize(command));
        using var stdin = new StringReader(payload);
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(
            stdin, stderr, "claude:Edit,Write",
            shellPatternsRaw: shellPatternsRaw,
            deniedShellPatternsRaw: deniedShellPatternsRaw,
            deniedShellOptionTokensRaw: null);

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);
    }

    [Fact]
    public void An_unscoped_shell_grant_still_enforces_a_denied_option_token()
    {
        // #1683 F2: the shape that used to fall through the nesting under
        // shellPatternList.Patterns.Count > 0 -- see HookCheckCommand's own comment at the
        // toolName == "Bash" check for the per-vendor divergence this closes.
        using var stdin = new StringReader(
            """{"tool_name": "Bash", "tool_input": {"command": "git log --output=C:/x"}}""");
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(
            stdin, stderr, "claude:Edit,Write",
            shellPatternsRaw: "claude:",
            deniedShellPatternsRaw: "claude:",
            deniedShellOptionTokensRaw: "claude:--output");

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
    }

    [Fact]
    public void The_denied_option_token_variable_matches_the_adapter_side_contract()
    {
        // Baton.Vendors cannot reference Baton.Cli, so the name is a plain string contract mirrored on
        // both sides; each asserts the literal in its own suite. If they drift, this channel reads
        // absent and the option-token rung silently stops enforcing -- and unlike the pattern channels
        // there is no --disallowedTools half to catch it.
        Assert.Equal(
            "BATON_HOOK_DENIED_SHELL_OPTION_TOKENS",
            HookCheckCommand.DeniedShellOptionTokensEnvironmentVariable);
        Assert.Equal(
            Baton.Vendors.ClaudeWorkerAdapter.DeniedShellOptionTokensVariable,
            HookCheckCommand.DeniedShellOptionTokensEnvironmentVariable);
    }

    /// <summary>
    /// #2002 rule 1 on claude's half. Both arms drive an UNSCOPED grant (no shell pattern list at
    /// all), which is `implement`'s and `janitor`'s actual shape and the one this rung has to reach:
    /// every other rung in this branch is skipped there, so a denial arriving under those conditions
    /// can only have come from the backgrounding detector.
    /// </summary>
    [Theory]
    [InlineData("Start-Process dotnet -ArgumentList 'build'", true)]
    [InlineData("dotnet build &", true)]
    [InlineData("dotnet build", false)]
    [InlineData("git push -u origin 2002-lane", false)]
    public void A_backgrounded_command_is_denied_on_an_unscoped_grant(string command, bool expectDenied)
    {
        using var stderr = new StringWriter();

        var exitCode = RunBash(command, shellPatternsRaw: null, stderr: stderr);

        Assert.Equal(
            expectDenied ? HookCheckCommand.DeniedExitCode : HookCheckCommand.AllowedExitCode, exitCode);
        if (expectDenied)
        {
            Assert.Contains("backgrounds the work", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("runs to completion synchronously", stderr.ToString(), StringComparison.Ordinal);
            // This path enforces no Baton per-command ceiling, so naming the broker's five minutes here
            // would be a claim about a mechanism that does not apply to a claude worker.
            Assert.Contains("no Baton per-command ceiling", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains(Baton.Domain.GrantRefusal.Marker, stderr.ToString());
        }
    }

    /// <summary>
    /// #2002 rule 2 on claude, which the first cut scoped out as impossible here. It is possible: the
    /// ledger persists under this execution's output directory, so a fresh hook subprocess per tool
    /// call still sees what the last one did. Three identical asks — allow, deny naming how long ago,
    /// deny plainly — matching the broker's three arms except that the middle one denies rather than
    /// replaying, because a PreToolUse hook cannot return a substitute result.
    /// </summary>
    [Fact]
    public void Three_identical_bash_commands_are_one_allow_and_two_denials()
    {
        using var room = new RepeatLedgerRoom();

        var first = room.RunBash("dotnet build -warnaserror", out var firstText);
        var second = room.RunBash("dotnet build -warnaserror", out var secondText);
        var third = room.RunBash("dotnet build -warnaserror", out var thirdText);

        Assert.Equal(HookCheckCommand.AllowedExitCode, first);
        Assert.Equal(string.Empty, firstText);

        Assert.Equal(HookCheckCommand.DeniedExitCode, second);
        Assert.Contains("byte-identical to the command", secondText, StringComparison.Ordinal);
        Assert.Contains("above in your transcript", secondText, StringComparison.Ordinal);
        Assert.Contains(Baton.Domain.GrantRefusal.Marker, secondText);

        Assert.Equal(HookCheckCommand.DeniedExitCode, third);
        Assert.Contains(
            Baton.Vendors.RepeatedToolCallLedger.CommandRepeatRefusal, thirdText, StringComparison.Ordinal);
    }

    /// <summary>
    /// #2002 review HIGH on the hook path: the same eviction the broker performs, at the only point a
    /// PreToolUse hook learns a write is coming. Both directions — the write arm executes, the no-write
    /// arm still denies — because an eviction on every tool call passes the first and fails the second.
    /// </summary>
    [Fact]
    public void A_write_makes_the_next_identical_command_allowed_again()
    {
        using var room = new RepeatLedgerRoom();

        room.RunBash("dotnet build", out _);
        room.Write(Path.Combine(room.Outbox, "report.md"));
        var afterWrite = room.RunBash("dotnet build", out var afterWriteText);

        // Polarity partner: no write in between, so this one is still a repeat.
        var withoutWrite = room.RunBash("dotnet build", out _);

        Assert.Equal(HookCheckCommand.AllowedExitCode, afterWrite);
        Assert.Equal(string.Empty, afterWriteText);
        Assert.Equal(HookCheckCommand.DeniedExitCode, withoutWrite);
    }

    /// <summary>
    /// #2002 rule 2b on claude: an unchanged file is denied on the re-read and then denied plainly, and
    /// the control is the file that CHANGED between reads — without it, a gate that denied every second
    /// Read would pass.
    /// </summary>
    [Fact]
    public void An_unchanged_reread_is_denied_and_a_changed_one_is_not()
    {
        using var room = new RepeatLedgerRoom();
        var path = Path.Combine(room.Root, "notes.md");
        File.WriteAllText(path, "one");

        var first = room.Read(path, out _);
        var second = room.Read(path, out var secondText);
        var third = room.Read(path, out var thirdText);

        File.WriteAllText(path, "one, then rather more than one");
        var afterChange = room.Read(path, out _);

        Assert.Equal(HookCheckCommand.AllowedExitCode, first);
        Assert.Equal(HookCheckCommand.DeniedExitCode, second);
        Assert.Contains("has not changed since you last read it", secondText, StringComparison.Ordinal);
        Assert.Equal(HookCheckCommand.DeniedExitCode, third);
        Assert.Contains(
            Baton.Vendors.RepeatedToolCallLedger.ReadRepeatRefusal, thirdText, StringComparison.Ordinal);
        Assert.Equal(HookCheckCommand.AllowedExitCode, afterChange);
    }

    /// <summary>
    /// The fail-open arm, and the one that matters most: this rung removes waste, and both hooks wrap
    /// their decision in a catch that DENIES, so a garbage ledger file must never reach that catch. A
    /// half-written or hand-corrupted file allows.
    /// </summary>
    [Fact]
    public void A_corrupt_ledger_file_allows_rather_than_denying()
    {
        using var room = new RepeatLedgerRoom();
        room.RunBash("dotnet build", out _);
        File.WriteAllText(
            Path.Combine(room.Outbox, Baton.Vendors.RepeatedToolCallLedger.FileName), "{\"Entries\": [ttt");

        var exitCode = room.RunBash("dotnet build", out var text);

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);
        Assert.Equal(string.Empty, text);
    }

    /// <summary>
    /// A room on disk: an outbox for the ledger to live in, and the three payload shapes the rungs
    /// above need. Every call is a separate <see cref="HookCheckCommand.Execute"/>, which is what
    /// makes it a stand-in for the fresh subprocess claude actually spawns per tool call.
    /// </summary>
    private sealed class RepeatLedgerRoom : IDisposable
    {
        public RepeatLedgerRoom()
        {
            Root = Path.Combine(Path.GetTempPath(), $"baton-hook-repeat-{Guid.NewGuid():N}");
            Outbox = Path.Combine(Root, "outbox");
            Directory.CreateDirectory(Outbox);
        }

        public string Root { get; }

        public string Outbox { get; }

        public int RunBash(string command, out string stderrText) =>
            Run(Payload("Bash", "command", command), out stderrText);

        public int Read(string path, out string stderrText) =>
            Run(Payload("Read", "file_path", path), out stderrText);

        public int Write(string path) => Run(Payload("Write", "file_path", path), out _);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private int Run(string payload, out string stderrText)
        {
            using var stdin = new StringReader(payload);
            using var stderr = new StringWriter();
            // "claude:" -- nothing withheld, which is `implement`'s shape and the population #2002
            // measured. Bash and Read both reach their own rungs from here.
            var exitCode = HookCheckCommand.Execute(
                stdin, stderr, "claude:", outboxDirectory: Outbox, workspaceDirectory: Root);
            stderrText = stderr.ToString();
            return exitCode;
        }

        /// <summary>
        /// Built by concatenation rather than a raw interpolated literal: the payload ends in two
        /// closing braces of its own, which a `$$"""…"""` cannot carry beside an interpolation hole.
        /// </summary>
        private static string Payload(string toolName, string inputKey, string inputValue) =>
            "{\"tool_name\": " + Json(toolName) + ", \"tool_input\": {" + Json(inputKey) + ": " +
            Json(inputValue) + "}}";

        private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);
    }
}
