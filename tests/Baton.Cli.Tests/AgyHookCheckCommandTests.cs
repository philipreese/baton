using System.Text.Json;

namespace Baton.Cli.Tests;

/// <summary>
/// #554: <see cref="AgyHookCheckCommand"/> is the executable target <c>agy</c> spawns for every
/// matched <c>PreToolUse</c> event. These drive <see cref="AgyHookCheckCommand.Execute"/> against
/// the exact stdin shape the live CLI produces — captured by
/// <c>agy.hook-env-inherited</c> in <c>tools/vendor-verify/verify.py</c>, which logs the real
/// payload — rather than a hand-shaped fixture, so a regression in field handling surfaces here.
/// </summary>
/// <remarks>
/// <b>Every assertion below checks the parsed <c>decision</c> field, never the exit code.</b> On agy
/// the exit code carries no gating meaning; the verdict is a JSON object on stdout, and
/// <c>agy.hook-malformed-stdout-fails-open</c> measured that output agy cannot parse — or no output
/// at all — is read as an <b>allow</b>. A test asserting on an exit code would pass while the gate
/// silently let everything through, which is the failure this suite exists to catch.
/// <para>
/// The polarity pairs are deliberate (gate `v-and-v`): a denied tool blocked and a granted tool allowed, on
/// the same payload shape and the same denied list, so a mechanism that denies (or allows)
/// unconditionally cannot pass both.
/// </para>
/// </remarks>
public class AgyHookCheckCommandTests
{
    /// <summary>
    /// The real payload agy sends, from the live capture in <c>agy.hook-env-inherited</c>'s log.
    /// Note <c>toolCall.name</c> nested and camelCase — claude's is a root-level <c>tool_name</c> —
    /// and the undocumented <c>modelName</c> field (recorded in <c>docs/vendor-doc-audit.md</c>),
    /// present here so a parser that trips over unexpected fields fails in this suite.
    /// </summary>
    private static string Payload(string toolName) => $$"""
        {"artifactDirectoryPath":"C:/x/brain/abc","conversationId":"abc",
         "modelName":"gemini-3.6-flash-medium","stepIdx":3,
         "toolCall":{"args":{"CommandLine":"node --version","Cwd":"C:\\x","WaitMsBeforeAsync":5000},
                     "name":"{{toolName}}"},
         "transcriptPath":"C:/x/transcript_full.jsonl","workspacePaths":["C:/x"]}
        """;

    private static string Decide(
        string stdinText, string? denied, string? outbox = null, string? workspace = null,
        string? shellPatterns = "agy:", string? deniedShellPatterns = "agy:",
        string? deniedShellOptionTokens = "agy:")
    {
        using var stdin = new StringReader(stdinText);
        using var stdout = new StringWriter();

        var exitCode = AgyHookCheckCommand.Execute(
            stdin, stdout, denied, shellPatternsRaw: shellPatterns, outboxDirectory: outbox,
            workspaceDirectory: workspace, deniedShellPatternsRaw: deniedShellPatterns,
            deniedShellOptionTokensRaw: deniedShellOptionTokens);

        Assert.Equal(AgyHookCheckCommand.ExitCode, exitCode);

        // Parsing rather than substring-matching is the point: agy parses this, and output that
        // merely *contains* the word "deny" while being invalid JSON is an allow.
        var raw = stdout.ToString();
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("decision").GetString()!;
    }

    [Fact]
    public void A_run_command_payload_within_shell_patterns_is_allowed()
    {
        var payload = Payload("run_command"); // CommandLine: node --version
        Assert.Equal("allow", Decide(payload, "agy:", shellPatterns: "agy:node *"));
    }

    [Fact]
    public void A_run_command_payload_outside_shell_patterns_is_denied()
    {
        var payload = Payload("run_command"); // CommandLine: node --version
        Assert.Equal("deny", Decide(payload, "agy:", shellPatterns: "agy:git *"));
    }

    // #1920: agy's copy of the same append the claude hook gained — the refused command is told which
    // tools DO read here. Both arms, since the clause is suppressed when the read tools are withheld;
    // without the second arm a hardcoded clause would pass the first.
    [Fact]
    public void A_denied_run_command_names_agys_read_tools_only_when_they_are_granted()
    {
        var readsGranted = DenyReason(Payload("run_command"), "agy:", shellPatterns: "agy:git *");
        var readsWithheld = DenyReason(
            Payload("run_command"), "agy:view_file,grep_search,list_dir,find_by_name",
            shellPatterns: "agy:git *");

        Assert.Contains("read files with view_file and search them with grep_search",
            readsGranted, StringComparison.Ordinal);
        Assert.DoesNotContain("grep_search", readsWithheld, StringComparison.Ordinal);
    }

    private static string DenyReason(string stdinText, string? denied, string? shellPatterns)
    {
        using var stdin = new StringReader(stdinText);
        using var stdout = new StringWriter();

        AgyHookCheckCommand.Execute(
            stdin, stdout, denied, shellPatternsRaw: shellPatterns,
            deniedShellPatternsRaw: "agy:", deniedShellOptionTokensRaw: "agy:");

        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("deny", doc.RootElement.GetProperty("decision").GetString());
        return doc.RootElement.GetProperty("reason").GetString()!;
    }

    [Fact]
    public void A_non_run_command_tool_is_unaffected_by_shell_patterns()
    {
        var payload = Payload("view_file");
        Assert.Equal("allow", Decide(payload, "agy:", shellPatterns: "agy:git *"));
    }

    [Fact]
    public void Wrong_vendor_shell_patterns_are_denied_fail_closed()
    {
        var payload = Payload("run_command");
        Assert.Equal("deny", Decide(payload, "agy:", shellPatterns: "claude:git *"));
    }

    [Theory]
    [InlineData(null)] // the variable was never set
    [InlineData("")] // present but empty (whitespace-only collapses here too)
    public void Absent_shell_patterns_are_denied_fail_closed(string? shellPatterns)
    {
        // AgyWorkerAdapter always emits BATON_HOOK_SHELL_PATTERNS ("agy:" at minimum) alongside the
        // denied-tool list, so an absent value means the channel broke, not an unscoped grant — the
        // same fail-open #679 closed for denied tools. An unscoped grant is Present+empty ("agy:").
        var payload = Payload("run_command");
        Assert.Equal("deny", Decide(payload, "agy:", shellPatterns: shellPatterns));
    }

    [Fact]
    public void An_unscoped_present_but_empty_shell_pattern_list_allows_run_command()
    {
        // "agy:" parses to Present with no patterns — the deliberate unscoped-shell state, which must
        // still allow run_command (the deny above keys on Absent, not on an empty Present list).
        var payload = Payload("run_command");
        Assert.Equal("allow", Decide(payload, "agy:", shellPatterns: "agy:"));
    }

    // ---- DenyAlways channel (0022's standing "never" rung, #390): agy's only enforcement for it ----

    [Fact]
    public void A_run_command_matching_a_denied_pattern_is_refused_even_when_the_shell_is_unscoped()
    {
        // Deny beats allow: the shell is granted unscoped ("agy:" = allow anything), yet a standing
        // "never" on node refuses it. If this allowed, DenyAlways could be reopened by a wider grant.
        var payload = Payload("run_command"); // CommandLine: node --version
        Assert.Equal("deny", Decide(payload, "agy:", shellPatterns: "agy:", deniedShellPatterns: "agy:node *"));
    }

    [Fact]
    public void A_run_command_not_matching_the_denied_pattern_is_allowed()
    {
        // The discriminating control: a deny on a DIFFERENT family must not refuse this command, or the
        // deny channel would just be a blanket run_command block rather than a scoped "never".
        var payload = Payload("run_command"); // CommandLine: node --version
        Assert.Equal("allow", Decide(payload, "agy:", shellPatterns: "agy:node *", deniedShellPatterns: "agy:git *"));
    }

    [Theory]
    [InlineData(null)] // the variable was never set
    [InlineData("")] // present but empty of the vendor tag
    public void Absent_denied_shell_patterns_deny_run_command_fail_closed(string? deniedShellPatterns)
    {
        // AgyWorkerAdapter always emits BATON_HOOK_DENIED_SHELL_PATTERNS ("agy:" at minimum) alongside the
        // allow channel, so an absent value means the channel broke — not "no standing denies". Fail
        // closed, exactly as the allow channel does, rather than skip a "never" we cannot read.
        var payload = Payload("run_command");
        Assert.Equal("deny", Decide(payload, "agy:", shellPatterns: "agy:", deniedShellPatterns: deniedShellPatterns));
    }

    [Fact]
    public void Wrong_vendor_denied_shell_patterns_deny_run_command_fail_closed()
    {
        var payload = Payload("run_command");
        Assert.Equal("deny", Decide(payload, "agy:", shellPatterns: "agy:", deniedShellPatterns: "claude:node *"));
    }

    [Fact]
    public void A_non_run_command_tool_is_unaffected_by_denied_shell_patterns()
    {
        // The deny channel gates exactly run_command; a broken/again-Absent state must not leak a verdict
        // onto other tools (view_file is judged only by the denied-tool channel).
        var payload = Payload("view_file");
        Assert.Equal("allow", Decide(payload, "agy:", deniedShellPatterns: null));
    }

    // ---- Chained-command segmentation (#1685) ----
    //
    // The DenyAlways rung above was checked against the whole command line as one string. That scan
    // rejects any unquoted shell metacharacter outright (#659) -- including '&' -- so a chained line
    // like `git push --force && true` was denied by the metacharacter scan itself, before either
    // pattern list was ever consulted, and read as "cannot judge, deny" rather than "the deny pattern
    // matched". The DIFFERENCE that matters: that denial carried the wrong reason and, on a SCOPED
    // allow list, would have refused a chain none of whose segments a standing deny even names. The
    // fix routes agy through the same segmentation claude's Bash hook uses
    // (ShellCommandPatternMatcher.EvaluateChainedCommand) so every top-level segment is judged on its
    // own terms: deny-checked, then (only under a scoped grant) allow-checked.

    private static string CommandPayload(string command) => $$"""
        {"artifactDirectoryPath":"C:/x/brain/abc","conversationId":"abc",
         "modelName":"gemini-3.6-flash-medium","stepIdx":3,
         "toolCall":{"args":{"CommandLine":{{JsonSerializer.Serialize(command)}},"Cwd":"C:\\x","WaitMsBeforeAsync":5000},
                     "name":"run_command"},
         "transcriptPath":"C:/x/transcript_full.jsonl","workspacePaths":["C:/x"]}
        """;

    [Fact]
    public void An_unscoped_grant_still_denies_a_standing_deny_riding_a_chained_command()
    {
        // The shell is granted unscoped ("agy:" = allow anything), yet a standing 'never' on git push
        // must still catch it chained after a no-op tail -- the exact shape #1685 was filed on.
        var payload = CommandPayload("git push --force && true");
        Assert.Equal(
            "deny",
            Decide(payload, "agy:", shellPatterns: "agy:", deniedShellPatterns: "agy:git push*"));
    }

    [Fact]
    public void An_unscoped_grant_denies_a_standing_deny_riding_the_first_segment_of_a_chain()
    {
        // Same claim, the other position: the standing deny must be caught wherever it sits in the
        // chain, not only when it happens to lead.
        var payload = CommandPayload("true && git push --force");
        Assert.Equal(
            "deny",
            Decide(payload, "agy:", shellPatterns: "agy:", deniedShellPatterns: "agy:git push*"));
    }

    [Fact]
    public void A_scoped_grant_allows_a_chain_whose_every_segment_matches_the_allow_list()
    {
        // The discriminating control: segmentation must not degrade into a blanket chain refusal.
        // Both segments independently match the review role's own allowlist, so the chain is allowed.
        var review = Baton.Vendors.WorkerRoleCatalog.For("review");
        var payload = CommandPayload("git status && git log -1");
        Assert.Equal(
            "allow",
            Decide(
                payload, "agy:write_to_file,replace_file_content",
                shellPatterns: "agy:" + string.Join(",", review.Grant.ShellCommandPatterns!),
                deniedShellPatterns: "agy:" + string.Join(",", review.Grant.DeniedShellCommandPatterns!),
                deniedShellOptionTokens: "agy:" + string.Join(",", review.Grant.DeniedShellOptionTokens!)));
    }

    [Fact]
    public void An_unscoped_deny_only_grant_never_denies_as_unparseable_even_around_a_substitution()
    {
        // #1731 operator ruling (spec/baton.md §9) superseded this test's original claim: a command
        // substitution used to deny the whole line as Unparseable here too, same polarity as the
        // whole-line scan it replaced. `EvaluateChainedCommand`'s own remarks state the current
        // mechanism; "git push*" simply does not match this line's head tokens either way, so it
        // allows. A SCOPED grant (review) is unaffected -- see
        // HookCheckCommandTests.An_unparseable_command_fails_closed_under_a_scoped_grant.
        var payload = CommandPayload("git status && echo $(whoami)");
        Assert.Equal(
            "allow",
            Decide(payload, "agy:", shellPatterns: "agy:", deniedShellPatterns: "agy:git push*"));
    }

    [Fact]
    public void An_unscoped_grant_still_allows_a_command_the_standing_deny_does_not_name()
    {
        // The discriminating control for the relaxed empty-allow-list branch: with no allow list, the
        // deny half is the ONLY thing that may refuse. Without this arm, reverting that relaxation
        // turns every unscoped role with a standing deny into a blanket refusal, all-green.
        Assert.Equal(
            "allow",
            Decide(CommandPayload("git status"), "agy:",
                shellPatterns: "agy:", deniedShellPatterns: "agy:git push*"));
    }

    [Fact]
    public void The_shell_patterns_variable_matches_the_adapter_side_contract()
    {
        Assert.Equal("BATON_HOOK_SHELL_PATTERNS", AgyHookCheckCommand.ShellPatternsEnvironmentVariable);
    }

    [Fact]
    public void A_tool_named_in_the_denied_list_is_denied()
    {
        Assert.Equal("deny", Decide(Payload("run_command"), "agy:run_command,manage_task"));
    }

    [Fact]
    public void A_tool_not_named_in_the_denied_list_is_allowed()
    {
        // Same payload shape and same denied list as the deny case above — only the tool name
        // differs, so neither result can come from a mechanism that ignores the input.
        Assert.Equal("allow", Decide(Payload("view_file"), "agy:run_command,manage_task"));
    }

    [Fact]
    public void A_granted_write_outside_the_workspace_and_the_outbox_is_denied()
    {
        // #679 inverted, and re-keyed to names agy actually sends. This asserted the opposite until
        // the bound existed — but it could never have failed for the right reason, because it drove
        // the gate with `write_file` and `AbsolutePath`, neither of which agy produces.
        // `agy.hook-payload-carries-write-path` measured the real pair: `write_to_file` and
        // `toolCall.args.TargetFile`. A fabricated tool name is not in any write list, so that
        // payload was judged as an ordinary unknown tool and would have passed against a gate that
        // bounded real writes correctly.
        //
        // It matters here more than on claude: `agy.plan-mode-does-not-deny-writes` measured that
        // agy itself writes outside every directory it was given, so there is no second bound
        // underneath this one to fall back on.
        var payload = WritePayload("C:/somewhere/else/entirely.txt");

        Assert.Equal("deny", Decide(payload, "agy:run_command,manage_task", Outbox, Workspace));

        // The same two controls OutboxWriteExemptionTests' claude equivalent carries, for the same
        // reasons: an inside-the-workspace write that must still be allowed, and the same payload
        // with the tool withheld.
        Assert.Equal(
            "allow",
            Decide(WritePayload(Workspace + "/src/x.cs"), "agy:run_command,manage_task", Outbox, Workspace));
        Assert.Equal("deny", Decide(payload, "agy:run_command,write_to_file", Outbox, Workspace));
    }

    /// <summary>
    /// A granted write still reaches the outbox, which sits outside the workspace. Same claim as
    /// <c>OutboxWriteExemptionTests</c>' claude equivalent, which says what a workspace-only bound
    /// would cost.
    /// </summary>
    [Fact]
    public void A_granted_write_into_the_outbox_is_allowed()
    {
        Assert.Equal(
            "allow",
            Decide(WritePayload(Outbox + "/review.md"), "agy:run_command", Outbox, Workspace));

        // Same control as the claude equivalent, and it earns its place for the same reason there.
        Assert.False(OutboxPath.IsInside(Path.Combine(Outbox, "review.md"), Workspace));
    }

    /// <summary>
    /// A write whose target this gate cannot read is denied — the condition
    /// <c>OutboxWriteExemptionTests</c>' claude equivalent states, and the one agy's own payload check
    /// is recorded as non-sentinel on.
    /// </summary>
    [Fact]
    public void A_granted_write_whose_target_cannot_be_read_from_the_payload_is_denied()
    {
        // The measured key, replaced with the one the old test invented — so this also pins that a
        // fabricated field name is not silently accepted as a target.
        var payload = WritePayload("C:/somewhere/else.txt").Replace("TargetFile", "AbsolutePath");

        Assert.Equal("deny", Decide(payload, "agy:run_command", Outbox, Workspace));

        // The control: the identical payload for a non-write tool is still allowed, so the denial is
        // about an unreadable write target rather than about the unexpected key.
        Assert.Equal(
            "allow",
            Decide(Payload("list_dir").Replace("CommandLine", "AbsolutePath"), "agy:run_command", Outbox, Workspace));
    }

    // Real rooted paths from the host, not hardcoded literals: containment is answered against paths
    // the filesystem actually roots, so the allow arms cannot pass or fail for a reason that has
    // nothing to do with the gate.
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "baton-workspace");

    private static readonly string Outbox =
        Path.Combine(Path.GetTempPath(), "baton-task", "artifacts", "execution_1");

    /// <summary>
    /// A real <c>write_to_file</c> payload: the tool name and the <c>TargetFile</c> key are the ones
    /// <c>agy.hook-payload-carries-write-path</c> observed on a live call, not plausible-looking
    /// substitutes.
    /// </summary>
    /// <remarks>
    /// Serialised rather than string-spliced, so a Windows path's backslashes cannot produce JSON
    /// that happens to parse into something other than the path intended — the same reason
    /// <c>OutboxWriteExemptionTests</c> builds its payloads this way.
    /// </remarks>
    private static string WritePayload(string target) =>
        JsonSerializer.Serialize(new
        {
            artifactDirectoryPath = "C:/x/brain/abc",
            conversationId = "abc",
            modelName = "gemini-3.6-flash-medium",
            stepIdx = 3,
            toolCall = new { args = new { TargetFile = target }, name = "write_to_file" },
            transcriptPath = "C:/x/transcript_full.jsonl",
            workspacePaths = new[] { "C:/x" },
        });

    /// <summary>
    /// <c>generate_image</c> carries its target in <c>ImageName</c>, not <c>TargetFile</c> — and
    /// until #708 the gate read only the latter, so every call to it was denied even when the
    /// operator had granted writes.
    /// </summary>
    /// <remarks>
    /// See <see cref="AgyHookCheckCommand.WriteTargetFields"/> for why it failed and why it stayed
    /// hidden. This is the behavioural half — the allow arm below was impossible before the fix.
    /// </remarks>
    [Fact]
    public void A_granted_generate_image_inside_the_outbox_is_allowed_and_outside_it_is_denied()
    {
        // The arm that was impossible before: an ordinary granted image write into the outbox.
        Assert.Equal(
            "allow",
            Decide(ImagePayload(Outbox + "/diagram.png"), "agy:run_command", Outbox, Workspace));

        // Polarity, so this cannot pass by the gate having simply stopped bounding the tool.
        Assert.Equal(
            "deny",
            Decide(ImagePayload("C:/somewhere/else/entirely.png"), "agy:run_command", Outbox, Workspace));

        // And the withheld arm still wins over the path, as for every other write-family tool.
        Assert.Equal(
            "deny",
            Decide(ImagePayload(Outbox + "/diagram.png"), "agy:generate_image", Outbox, Workspace));
    }

    /// <summary>
    /// A <c>generate_image</c> payload carrying the argument names a REAL call was observed to
    /// carry, captured the same way <see cref="WritePayload"/>'s were — so neither fixture rests on
    /// documentation. What the observation was, and how it differed from the corpus, is recorded on
    /// <c>AgyHookCheckCommand.WriteTargetFields</c>.
    /// </summary>
    private static string ImagePayload(string imageName) =>
        JsonSerializer.Serialize(new
        {
            artifactDirectoryPath = "C:/x/brain/abc",
            conversationId = "abc",
            modelName = "gemini-3.6-flash-medium",
            stepIdx = 3,
            toolCall = new
            {
                args = new { Prompt = "a diagram", ImageName = imageName },
                name = "generate_image",
            },
            transcriptPath = "C:/x/transcript_full.jsonl",
            workspacePaths = new[] { "C:/x" },
        });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_or_blank_denied_list_now_denies_because_the_gate_cannot_know(string? denied)
    {
        // A known-empty grant withholds nothing, which is different from being unable to determine
        // what is withheld — the cases below deny for exactly that reason.
        // #600 inverted this deliberately. It used to allow, so "AER set the list and nothing is
        // withheld" and "the list never arrived" were the same observable outcome. On this vendor
        // there is no fail-closed backstop under --dangerously-skip-permissions, so a channel that
        // silently stopped arriving meant a fully ungated worker. An empty list AER actually sent
        // still allows; it now arrives tagged (`agy:`), which is what tells the two apart.
        Assert.Equal("deny", Decide(Payload("run_command"), denied));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"toolCall":{}}""")]
    [InlineData("""{"toolCall":"not-an-object"}""")]
    [InlineData("""{"tool_name":"run_command"}""")]
    [InlineData("""{"toolCall":{"name":""}}""")]
    [InlineData("""{"toolCall":{"name":7}}""")]
    [InlineData("""{"toolCall":{"name":{"tool":"run_command"}}}""")]
    public void Input_it_cannot_judge_is_denied_never_allowed(string stdinText)
    {
        // The core of #554 and the opposite of HookCheckCommand's claude-side posture. claude has
        // --disallowedTools independently covering the same names, so failing open there is "no
        // worse than what exists". agy has no such flag (agy.permissions-are-global-only, decision
        // 0029): this hook is the only per-worker gate, so anything it cannot judge must be denied.
        //
        // `{"tool_name":"run_command"}` is in this list deliberately: that is claude's payload
        // shape, and it must NOT be understood here. If a future refactor merged the two commands,
        // this case would start returning "allow" (claude's field, agy's fail-open) and this test
        // is what would catch it.
        Assert.Equal("deny", Decide(stdinText, "agy:run_command,manage_task"));
    }

    [Theory]
    [InlineData("""{"toolCall":{"name":7}}""")]
    [InlineData("""{"toolCall":{"name":{"tool":"run_command"}}}""")]
    public void A_non_string_tool_name_is_answered_by_the_guard_not_the_catch_all(string stdinText)
    {
        // The row above proves only that these deny, and BOTH paths deny -- so it cannot tell which
        // one ran. It matters: JsonElement.GetString throws InvalidOperationException on a non-string,
        // which `catch (JsonException)` does not catch, so before #679's review this shape escaped
        // Decide entirely and was caught by Execute's last-resort handler. Safe, but it made that
        // handler's "reaching here means a defect" comment false and gave the model a reason naming
        // an internal failure instead of the payload. Asserting the reason is what discriminates.
        using var stdin = new StringReader(stdinText);
        using var stdout = new StringWriter();

        AgyHookCheckCommand.Execute(stdin, stdout, "agy:run_command");

        using var doc = JsonDocument.Parse(stdout.ToString());
        var reason = doc.RootElement.GetProperty("reason").GetString();
        Assert.Equal("deny", doc.RootElement.GetProperty("decision").GetString());
        Assert.Contains("toolCall.name", reason);
        Assert.DoesNotContain("failed internally", reason);
    }

    [Fact]
    public void A_denial_reason_names_the_tool_so_the_model_is_told_what_was_withheld()
    {
        using var stdin = new StringReader(Payload("run_command"));
        using var stdout = new StringWriter();

        AgyHookCheckCommand.Execute(stdin, stdout, "agy:run_command");

        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Contains("run_command", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void A_failure_to_read_stdin_is_denied_rather_than_allowed()
    {
        // The one path that cannot be reached by feeding text in: a reader that throws. Without
        // this arm the IOException branch is untested, and it is precisely the branch where a
        // crash-to-allow would be invisible.
        using var stdout = new StringWriter();

        var exitCode = AgyHookCheckCommand.Execute(new ThrowingReader(), stdout, "agy:run_command");

        Assert.Equal(AgyHookCheckCommand.ExitCode, exitCode);
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("deny", doc.RootElement.GetProperty("decision").GetString());
    }

    [Theory]
    [InlineData("browser_navigate", "deny")]
    [InlineData("browser_click", "deny")]
    [InlineData("browser", "allow")]          // the bare prefix without the separator is not a match
    [InlineData("view_file", "allow")]
    public void A_trailing_star_entry_withholds_a_whole_tool_family(string toolName, string expected)
    {
        // agy's corpus offers `browser_.*` as a matcher example -- "Match any tool starting with
        // browser_" -- while enumerating no such tools, so the family cannot be listed by name. The
        // allow rows are the polarity control: a prefix matcher that matched everything would pass
        // the deny rows alone.
        Assert.Equal(expected, Decide(Payload(toolName), "agy:browser_*,search_web"));
    }

    [Fact]
    public void A_bare_star_does_not_deny_everything_by_accident()
    {
        // Guards the prefix implementation's edge: `entry.Length > 1` means a lone "*" is not
        // treated as a match-all prefix. If it ever were, an adapter bug emitting "*" would silently
        // withhold every tool and break every worker -- loudly, but for a baffling reason.
        Assert.Equal("allow", Decide(Payload("view_file"), "agy:*"));
    }

    [Fact]
    public void The_denied_tools_variable_matches_the_adapter_side_contract()
    {
        // Baton.Vendors cannot reference Baton.Cli, so the variable name is a plain string contract
        // mirrored on both sides. Each side asserts the literal in its own suite; if they drift,
        // the hook reads an empty list, treats it as "nothing withheld", and allows everything.
        Assert.Equal("BATON_HOOK_DENIED_TOOLS", AgyHookCheckCommand.DeniedToolsEnvironmentVariable);
    }

    [Theory]
    [InlineData("git merge-base --is-ancestor a b", "allow")]
    [InlineData("git diff --stat", "allow")]
    [InlineData("git status", "allow")]
    [InlineData("git difftool --extcmd=calc -y HEAD~1 HEAD", "deny")]
    [InlineData("git grep -Ocalc foo", "deny")]
    [InlineData("git grep --open-files-in-pager=calc foo", "deny")]
    [InlineData("git -c alias.x=!calc x", "deny")]
    [InlineData("git push --dry-run", "deny")]
    [InlineData("gh api repos/x", "deny")]
    [InlineData("gh pr view 1", "allow")]
    // #1683 F1 -- denied by absence once `git grep*` left the review allow list; F2 -- the --output
    // write escape, now closed by the option-token channel; F3 -- the respelled `git merge *` deny.
    // The parity requirement: agy's hook reaches the same verdict on every one of these as claude's.
    [InlineData("git grep -nOcalc foo", "deny")]
    [InlineData("git grep --ignore-case -Ocalc foo", "deny")]
    [InlineData("git grep --open-files=calc foo", "deny")]
    [InlineData("git grep  -Ocalc foo", "deny")]
    [InlineData("git log -1 --output=C:/x --format=format:y", "deny")]
    [InlineData("git show --output C:/x", "deny")]
    [InlineData("git log -1 --outpu\"t\"=C:/x --format=format:y", "deny")]
    [InlineData("git log --oneline -5", "allow")]
    [InlineData("git log --grep=\"--output\"", "allow")]
    [InlineData("git merge origin/main", "deny")]
    public void Review_role_command_allow_deny_polarities_from_catalog(string command, string expectedDecision)
    {
        var review = Baton.Vendors.WorkerRoleCatalog.For("review");
        var shellPatternsRaw = review.Grant.ShellCommandPatterns is { Count: > 0 }
            ? "agy:" + string.Join(",", review.Grant.ShellCommandPatterns)
            : "agy:";
        var deniedShellPatternsRaw = review.Grant.DeniedShellCommandPatterns is { Count: > 0 }
            ? "agy:" + string.Join(",", review.Grant.DeniedShellCommandPatterns)
            : "agy:";
        var deniedShellOptionTokensRaw = review.Grant.DeniedShellOptionTokens is { Count: > 0 }
            ? "agy:" + string.Join(",", review.Grant.DeniedShellOptionTokens)
            : "agy:";

        var payload = $$"""
            {"artifactDirectoryPath":"C:/x/brain/abc","conversationId":"abc",
             "modelName":"gemini-3.6-flash-medium","stepIdx":3,
             "toolCall":{"args":{"CommandLine":{{JsonSerializer.Serialize(command)}}, "Cwd":"C:\\x","WaitMsBeforeAsync":5000},
                         "name":"run_command"},
             "transcriptPath":"C:/x/transcript_full.jsonl","workspacePaths":["C:/x"]}
            """;
        using var stdin = new StringReader(payload);
        using var stdout = new StringWriter();

        var exitCode = AgyHookCheckCommand.Execute(
            stdin, stdout, "agy:write_to_file,replace_file_content",
            shellPatternsRaw: shellPatternsRaw,
            deniedShellPatternsRaw: deniedShellPatternsRaw,
            deniedShellOptionTokensRaw: deniedShellOptionTokensRaw);

        Assert.Equal(AgyHookCheckCommand.ExitCode, exitCode);
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(expectedDecision, doc.RootElement.GetProperty("decision").GetString());
    }

    [Theory]
    // #1731's grant shape, agy's side (spec/baton.md §9): #1725's segment-level deny check is not
    // nested under a non-empty allow list here, so this rung engages the same way on an unscoped
    // grant as on review's scoped one.
    [InlineData("implement", "gh pr create --title x --body-file y", "allow")]
    [InlineData("implement", "gh pr edit 1 --body-file y", "allow")]
    [InlineData("implement", "gh label create x", "deny")]
    [InlineData("implement", "gh pr edit 1 --add-label x", "deny")]
    [InlineData("implement", "gh pr edit 1 --remove-label x", "deny")]
    // Found-while-fixing, same PR: `--label` at creation time attaches a label too, and was never
    // covered by the issue's own token list.
    [InlineData("implement", "gh pr create --title x --label operator-merge", "deny")]
    [InlineData("implement", "gh issue create --title x --label operator-merge", "deny")]
    [InlineData("implement", "gh pr merge 1 --squash", "deny")]
    [InlineData("implement", "gh api repos/a/b", "deny")]
    [InlineData("implement", "true && gh label create x", "deny")]
    // #1731 found-while-fixing: adding a deny list to an unscoped agy role (the first one to carry
    // one) routes every run_command through EvaluateChainedCommand's segmenter for the first time.
    // The operator ruling recorded at spec/baton.md §9 is what makes these rows allow rather than
    // deny -- read there for the reasoning; this pins the resulting behaviour through the real catalog.
    [InlineData("implement", "dotnet test > out.txt", "allow")]
    [InlineData("implement", "echo $PATH", "allow")]
    [InlineData("janitor", "gh pr create --title x --body-file y", "allow")]
    [InlineData("janitor", "gh pr edit 1 --body-file y", "allow")]
    [InlineData("janitor", "gh label create x", "deny")]
    [InlineData("janitor", "gh pr edit 1 --add-label x", "deny")]
    [InlineData("janitor", "gh pr edit 1 --remove-label x", "deny")]
    [InlineData("janitor", "gh pr merge 1 --squash", "deny")]
    [InlineData("janitor", "gh api repos/a/b", "deny")]
    [InlineData("janitor", "true && gh label create x", "deny")]
    [InlineData("janitor", "dotnet test > out.txt", "allow")]
    [InlineData("janitor", "echo $PATH", "allow")]
    public void Unscoped_write_role_denies_label_merge_and_api_writes_from_the_catalog(
        string roleId, string command, string expectedDecision)
    {
        var role = Baton.Vendors.WorkerRoleCatalog.For(roleId);
        Assert.Null(role.Grant.ShellCommandPatterns); // stays unscoped -- item 1's "do NOT add" requirement
        var deniedShellPatternsRaw = role.Grant.DeniedShellCommandPatterns is { Count: > 0 }
            ? "agy:" + string.Join(",", role.Grant.DeniedShellCommandPatterns)
            : "agy:";
        var deniedShellOptionTokensRaw = role.Grant.DeniedShellOptionTokens is { Count: > 0 }
            ? "agy:" + string.Join(",", role.Grant.DeniedShellOptionTokens)
            : "agy:";

        var payload = $$"""
            {"artifactDirectoryPath":"C:/x/brain/abc","conversationId":"abc",
             "modelName":"gemini-3.6-flash-medium","stepIdx":3,
             "toolCall":{"args":{"CommandLine":{{JsonSerializer.Serialize(command)}}, "Cwd":"C:\\x","WaitMsBeforeAsync":5000},
                         "name":"run_command"},
             "transcriptPath":"C:/x/transcript_full.jsonl","workspacePaths":["C:/x"]}
            """;
        using var stdin = new StringReader(payload);
        using var stdout = new StringWriter();

        var exitCode = AgyHookCheckCommand.Execute(
            stdin, stdout, "agy:write_to_file,replace_file_content",
            shellPatternsRaw: "agy:", // unscoped: Present, empty pattern list
            deniedShellPatternsRaw: deniedShellPatternsRaw,
            deniedShellOptionTokensRaw: deniedShellOptionTokensRaw);

        Assert.Equal(AgyHookCheckCommand.ExitCode, exitCode);
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(expectedDecision, doc.RootElement.GetProperty("decision").GetString());
    }

    [Fact]
    public void The_option_token_channel_is_what_denies_the_output_write_not_the_pattern_lists()
    {
        // agy's copy of claude's discriminating control (#1683 F2): same command line, same allow/deny
        // pattern lists, option-token channel PRESENT but empty -> allowed. Without this arm the deny
        // row above would not distinguish the new channel from the lists that were already there.
        // Present-but-empty, not absent: #1683 F3 made an absent channel deny outright (fail-closed,
        // matching its two sibling channels), so an absent channel no longer isolates "the pattern
        // lists alone did not deny this" -- it deny for its own, unrelated reason.
        var review = Baton.Vendors.WorkerRoleCatalog.For("review");
        const string command = "git log -1 --output=C:/x --format=format:y";
        var payload = $$"""
            {"artifactDirectoryPath":"C:/x/brain/abc","conversationId":"abc",
             "modelName":"gemini-3.6-flash-medium","stepIdx":3,
             "toolCall":{"args":{"CommandLine":{{JsonSerializer.Serialize(command)}}, "Cwd":"C:\\x","WaitMsBeforeAsync":5000},
                         "name":"run_command"},
             "transcriptPath":"C:/x/transcript_full.jsonl","workspacePaths":["C:/x"]}
            """;
        using var stdin = new StringReader(payload);
        using var stdout = new StringWriter();

        AgyHookCheckCommand.Execute(
            stdin, stdout, "agy:write_to_file,replace_file_content",
            shellPatternsRaw: "agy:" + string.Join(",", review.Grant.ShellCommandPatterns!),
            deniedShellPatternsRaw: "agy:" + string.Join(",", review.Grant.DeniedShellCommandPatterns!),
            deniedShellOptionTokensRaw: "agy:");

        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("allow", doc.RootElement.GetProperty("decision").GetString());
    }

    [Fact]
    public void An_absent_denied_option_token_channel_now_fails_closed()
    {
        // #1683 F3: this channel used to skip silently on a non-Present status, unlike its two
        // sibling channels in the same branch (shellPatternList, deniedShellPatternList), which
        // already deny on Status != Present. A broken channel now denies with a reason naming it,
        // the same way the siblings already do.
        var review = Baton.Vendors.WorkerRoleCatalog.For("review");
        const string command = "git log --oneline -5";
        var payload = $$"""
            {"artifactDirectoryPath":"C:/x/brain/abc","conversationId":"abc",
             "modelName":"gemini-3.6-flash-medium","stepIdx":3,
             "toolCall":{"args":{"CommandLine":{{JsonSerializer.Serialize(command)}}, "Cwd":"C:\\x","WaitMsBeforeAsync":5000},
                         "name":"run_command"},
             "transcriptPath":"C:/x/transcript_full.jsonl","workspacePaths":["C:/x"]}
            """;
        using var stdin = new StringReader(payload);
        using var stdout = new StringWriter();

        AgyHookCheckCommand.Execute(
            stdin, stdout, "agy:write_to_file,replace_file_content",
            shellPatternsRaw: "agy:" + string.Join(",", review.Grant.ShellCommandPatterns!),
            deniedShellPatternsRaw: "agy:" + string.Join(",", review.Grant.DeniedShellCommandPatterns!),
            deniedShellOptionTokensRaw: null);

        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("deny", doc.RootElement.GetProperty("decision").GetString());
        Assert.Contains("denied option token", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void The_denied_option_token_variable_matches_the_adapter_side_contract()
    {
        Assert.Equal(
            "BATON_HOOK_DENIED_SHELL_OPTION_TOKENS",
            AgyHookCheckCommand.DeniedShellOptionTokensEnvironmentVariable);
    }

    // ---- #1680: the first-verdict canary's write side ----

    [Fact]
    public void Execute_appends_one_ledger_line_per_verdict_whether_allowed_or_denied()
    {
        var ledgerPath = Path.Combine(Path.GetTempPath(), $"agy-hook-verdicts-{Guid.NewGuid():N}.ndjson");
        try
        {
            using (var stdin = new StringReader(Payload("view_file")))
            using (var stdout = new StringWriter())
            {
                AgyHookCheckCommand.Execute(
                    stdin, stdout, deniedToolsRaw: "agy:", verdictLedgerPath: ledgerPath);
            }

            using (var stdin = new StringReader(Payload("view_file")))
            using (var stdout = new StringWriter())
            {
                AgyHookCheckCommand.Execute(
                    stdin, stdout, deniedToolsRaw: "agy:view_file", verdictLedgerPath: ledgerPath);
            }

            var lines = File.ReadAllLines(ledgerPath);
            Assert.Equal(2, lines.Length);
        }
        finally
        {
            FileCleanup.Delete(ledgerPath);
        }
    }

    [Fact]
    public void Execute_never_throws_when_the_ledger_path_is_unwritable()
    {
        // A directory that does not exist -- File.AppendAllText would throw DirectoryNotFoundException
        // (an IOException) if this were not swallowed. The verdict on stdout must still be correct;
        // only the ledger write is best-effort.
        var unwritablePath = Path.Combine(
            Path.GetTempPath(), $"agy-hook-ledger-missing-dir-{Guid.NewGuid():N}", "verdicts.ndjson");

        using var stdin = new StringReader(Payload("view_file"));
        using var stdout = new StringWriter();

        var exitCode = AgyHookCheckCommand.Execute(
            stdin, stdout, deniedToolsRaw: "agy:view_file", verdictLedgerPath: unwritablePath);

        Assert.Equal(AgyHookCheckCommand.ExitCode, exitCode);
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("deny", doc.RootElement.GetProperty("decision").GetString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(unwritablePath)));
    }

    [Fact]
    public void A_null_ledger_path_writes_no_ledger_and_does_not_throw()
    {
        using var stdin = new StringReader(Payload("view_file"));
        using var stdout = new StringWriter();

        var exitCode = AgyHookCheckCommand.Execute(
            stdin, stdout, deniedToolsRaw: "agy:", verdictLedgerPath: null);

        Assert.Equal(AgyHookCheckCommand.ExitCode, exitCode);
    }

    [Fact]
    public void The_verdict_ledger_variable_matches_the_adapter_side_contract()
    {
        Assert.Equal("BATON_HOOK_VERDICT_LEDGER", AgyHookCheckCommand.VerdictLedgerEnvironmentVariable);
    }

    /// <summary>
    /// #2002 rule 1 on agy's half — the vendor the polling was measured on, and the one whose native
    /// <c>run_command</c> never touches the codex broker, so this hook is the only place the rule can
    /// reach it. Driven on an UNSCOPED grant (Present, empty pattern list — `implement`'s real shape),
    /// where every other rung in this branch passes the line through, so a deny here can only be the
    /// backgrounding detector's.
    /// </summary>
    [Theory]
    [InlineData("Start-Process dotnet -ArgumentList 'build' -NoNewWindow -PassThru", "deny")]
    [InlineData("dotnet test &", "deny")]
    [InlineData("nohup pixi run gates-fast", "deny")]
    [InlineData("dotnet test", "allow")]
    [InlineData("Get-Process -Id 59340 -ErrorAction SilentlyContinue", "allow")]
    public void A_backgrounded_run_command_is_denied_on_an_unscoped_grant(string command, string expected)
    {
        var payload = $$"""
            {"artifactDirectoryPath":"C:/x/brain/abc","conversationId":"abc",
             "modelName":"gemini-3.6-flash-medium","stepIdx":3,
             "toolCall":{"args":{"CommandLine":{{JsonSerializer.Serialize(command)}}, "Cwd":"C:\\x","WaitMsBeforeAsync":5000},
                         "name":"run_command"},
             "transcriptPath":"C:/x/transcript_full.jsonl","workspacePaths":["C:/x"]}
            """;
        using var stdin = new StringReader(payload);
        using var stdout = new StringWriter();

        var exitCode = AgyHookCheckCommand.Execute(
            stdin, stdout, "agy:write_to_file", shellPatternsRaw: "agy:",
            deniedShellPatternsRaw: "agy:", deniedShellOptionTokensRaw: "agy:");

        Assert.Equal(AgyHookCheckCommand.ExitCode, exitCode);
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(expected, doc.RootElement.GetProperty("decision").GetString());
        if (expected == "deny")
        {
            var reason = doc.RootElement.GetProperty("reason").GetString();
            Assert.Contains("backgrounds the work", reason!, StringComparison.Ordinal);
            Assert.Contains("costs no tool step", reason!, StringComparison.Ordinal);
            Assert.Contains(Baton.Domain.GrantRefusal.Marker, reason!);
        }
    }

    /// <summary>
    /// The control that keeps the rule about BACKGROUNDING rather than about `run_command`: the
    /// measured room's own polling line is allowed through by rule 1 (it is the symptom, not the
    /// shape), which is why the poll itself is answered by rule 2 in the broker and by rule 3 in the
    /// arrest text instead. Stated as an arm above (`Get-Process …` → allow) so a detector that grew
    /// to refuse polls directly would turn this red rather than quietly changing what agy may run.
    /// </summary>
    /// <summary>
    /// #2002 review MEDIUM, both directions. An argument no measurement accounts for is refused,
    /// because it could be the backgrounding switch this gate cannot read. The control is the first
    /// arm, and it is the one that matters: <c>WaitMsBeforeAsync</c> IS a backgrounding parameter and
    /// agy sends it on every single call, so a rule that refused "anything but CommandLine" would deny
    /// every command this vendor runs. <c>AgyHookCheckCommand.MeasuredRunCommandArgs</c> carries that
    /// finding and its provenance.
    /// </summary>
    [Theory]
    [InlineData("""{"CommandLine":"dotnet build","Cwd":"C:\\x","WaitMsBeforeAsync":5000}""", "allow")]
    [InlineData("""{"CommandLine":"dotnet build"}""", "allow")]
    [InlineData("""{"CommandLine":"dotnet build","Async":true}""", "deny")]
    [InlineData("""{"CommandLine":"dotnet build","Cwd":"C:\\x","Detach":true}""", "deny")]
    public void An_unmeasured_run_command_argument_is_refused_and_the_measured_three_are_not(
        string argsJson, string expected)
    {
        var payload = $$"""
            {"artifactDirectoryPath":"C:/x/brain/abc","conversationId":"abc",
             "modelName":"gemini-3.6-flash-medium","stepIdx":3,
             "toolCall":{"args":{{argsJson}},"name":"run_command"},
             "transcriptPath":"C:/x/transcript_full.jsonl","workspacePaths":["C:/x"]}
            """;
        using var stdin = new StringReader(payload);
        using var stdout = new StringWriter();

        AgyHookCheckCommand.Execute(
            stdin, stdout, "agy:write_to_file", shellPatternsRaw: "agy:",
            deniedShellPatternsRaw: "agy:", deniedShellOptionTokensRaw: "agy:");

        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(expected, doc.RootElement.GetProperty("decision").GetString());
        if (expected == "deny")
        {
            Assert.Contains(
                "in the background instead of to completion",
                doc.RootElement.GetProperty("reason").GetString()!,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// #2002 rule 2 on agy, the same three arms the claude hook and the broker carry, through a ledger
    /// file this execution's output directory holds — each call is a separate
    /// <see cref="AgyHookCheckCommand.Execute"/>, standing in for the fresh subprocess agy spawns per
    /// tool call. The <c>view_file</c> arm is rule 2b: unchanged file denied, changed file allowed,
    /// which is the control that keeps it about the file rather than about the second read of
    /// anything.
    /// </summary>
    [Fact]
    public void Three_identical_run_commands_are_one_allow_and_two_denials_and_a_changed_file_rereads()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-agy-repeat-{Guid.NewGuid():N}");
        var outbox = Path.Combine(root, "outbox");
        Directory.CreateDirectory(outbox);
        try
        {
            var file = Path.Combine(root, "notes.md");
            File.WriteAllText(file, "one");

            var first = RepeatDecide(outbox, RunPayload("pixi run gates-fast"));
            var second = RepeatDecide(outbox, RunPayload("pixi run gates-fast"));
            var third = RepeatDecide(outbox, RunPayload("pixi run gates-fast"));

            var firstRead = RepeatDecide(outbox, ViewPayload(file));
            var secondRead = RepeatDecide(outbox, ViewPayload(file));
            File.WriteAllText(file, "one, and then rather more than one");
            var afterChange = RepeatDecide(outbox, ViewPayload(file));

            Assert.Equal("allow", Decision(first));
            Assert.Equal("deny", Decision(second));
            Assert.Contains("byte-identical to the command", Reason(second), StringComparison.Ordinal);
            Assert.Contains("above in your transcript", Reason(second), StringComparison.Ordinal);
            Assert.Contains(Baton.Domain.GrantRefusal.Marker, Reason(third));
            Assert.Equal("deny", Decision(third));
            Assert.Contains(
                Baton.Vendors.RepeatedToolCallLedger.CommandRepeatRefusal, Reason(third),
                StringComparison.Ordinal);

            Assert.Equal("allow", Decision(firstRead));
            Assert.Equal("deny", Decision(secondRead));
            Assert.Equal("allow", Decision(afterChange));
        }
        finally
        {
            Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// #2002 review HIGH on agy's hook path: a write evicts the remembered command, so the identical
    /// re-ask is allowed again. Polarity partner in the same test — no write, still denied.
    /// </summary>
    [Fact]
    public void A_write_makes_the_next_identical_run_command_allowed_again()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-agy-repeat-{Guid.NewGuid():N}");
        var outbox = Path.Combine(root, "outbox");
        Directory.CreateDirectory(outbox);
        try
        {
            RepeatDecide(outbox, RunPayload("dotnet build"));
            RepeatDecide(outbox, WriteToolPayload(Path.Combine(outbox, "report.md")), workspace: root);
            var afterWrite = RepeatDecide(outbox, RunPayload("dotnet build"));
            var withoutWrite = RepeatDecide(outbox, RunPayload("dotnet build"));

            Assert.Equal("allow", Decision(afterWrite));
            Assert.Equal("deny", Decision(withoutWrite));
        }
        finally
        {
            Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// Concatenated, for the same reason the claude suite's payload helper is: this JSON closes with
    /// more braces than a `$$"""…"""` will carry alongside an interpolation hole.
    /// </summary>
    private static string ToolPayload(string toolName, string argsJson) =>
        "{\"toolCall\":{\"args\":" + argsJson + ",\"name\":" +
        JsonSerializer.Serialize(toolName) + "}}";

    private static string RunPayload(string command) =>
        ToolPayload(
            "run_command",
            "{\"CommandLine\":" + JsonSerializer.Serialize(command) +
            ",\"Cwd\":\"C:\\\\x\",\"WaitMsBeforeAsync\":5000}");

    private static string ViewPayload(string path) =>
        ToolPayload("view_file", "{\"AbsolutePath\":" + JsonSerializer.Serialize(path) + "}");

    private static string WriteToolPayload(string path) =>
        ToolPayload("write_to_file", "{\"TargetFile\":" + JsonSerializer.Serialize(path) + "}");

    private static string RepeatDecide(string outbox, string payload, string? workspace = null)
    {
        using var stdin = new StringReader(payload);
        using var stdout = new StringWriter();
        AgyHookCheckCommand.Execute(
            stdin, stdout, "agy:", shellPatternsRaw: "agy:", outboxDirectory: outbox,
            workspaceDirectory: workspace, deniedShellPatternsRaw: "agy:",
            deniedShellOptionTokensRaw: "agy:");
        return stdout.ToString();
    }

    private static string Decision(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("decision").GetString()!;

    private static string Reason(string json) =>
        JsonDocument.Parse(json).RootElement.TryGetProperty("reason", out var reason)
            ? reason.GetString()!
            : string.Empty;

    [Fact]
    public void The_polling_line_itself_is_not_what_rule_one_refuses() =>
        Assert.Null(Baton.Vendors.BackgroundingShapeDetector.Detect(
            "Get-Process -Id 59340 -ErrorAction SilentlyContinue",
            Baton.Vendors.BackgroundingShapeDetector.NativeShell));

    private sealed class ThrowingReader : TextReader
    {
        public override string ReadToEnd() => throw new IOException("simulated pipe failure");
    }
}
