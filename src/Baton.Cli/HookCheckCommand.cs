using System.Text.Json;
using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Cli;

/// <summary>
/// <c>baton hook-check</c> (#543): the executable target of the <c>PreToolUse</c> hook
/// <see cref="Baton.Vendors.ClaudeWorkerAdapter"/> writes into every spawned worker's
/// <c>claude-settings.json</c>. Not an operator-facing subcommand — Claude Code invokes this
/// itself, on every tool call, spawned directly with no shell (exec form: <c>args</c> is set on
/// the hook handler), so it receives the event JSON on stdin exactly as documented in
/// <c>.vendor-survey/corpus/claude__hooks.md</c>.
/// </summary>
/// <remarks>
/// This enforces the category denial <see cref="Baton.Vendors.ClaudeWorkerAdapter.BuildHookDeniedTools"/>
/// computes. For reads, shell and network it is a second mechanism reaching the same names
/// <c>--disallowedTools</c> already carries; for <b>writes it is the only one</b>, since #649 moved
/// those names off that flag so this hook can allow the write landing in <c>BATON_OUTPUT_DIR</c>. It
/// does not attempt to close the <c>Bash</c>-substitution gap #529 measured (a withheld write
/// category is still reachable through a granted shell) — that is explicitly out of scope here; see
/// #529's own doc comment on <c>BuildDisallowedTools</c>. What this buys is the mechanism 0029
/// requires — a <c>PreToolUse</c> hook that can exit 2 — wired up and independently verifiable,
/// which #532 needs a real positive control to check.
/// <para>
/// <b>Fails closed on every input it cannot judge</b> — unreadable stdin, empty stdin, malformed
/// JSON, a missing or empty <c>tool_name</c>, and any unhandled defect. Until #649 each of those
/// allowed, on the argument that <c>--disallowedTools</c> covered the same names anyway; once writes
/// ride this hook alone that argument is void, and a parse failure would be an ungated write.
/// <c>HookCheckCommandTests.Shapeless_stdin_fails_closed_because_writes_ride_this_hook_alone</c>
/// holds every one of those paths to exit 2, with a well-formed control beside it.
/// </para>
/// <para>
/// What it still does not bound: the tool the model <em>substitutes</em>. A granted <c>Bash</c>
/// defeats a withheld write/read/network category regardless of what this decides, so this remains
/// a category gate rather than a security boundary — and the one failure it cannot reach at all is
/// its own command failing to start, which is measured to fail open on both vendors
/// (<c>gate.broken-hook-fails-open</c>) and is #532's.
/// </para>
/// </remarks>
public static class HookCheckCommand
{
    /// <summary>
    /// The environment variable this command reads for the current invocation's denied-tool list,
    /// comma-joined tool names exactly as <c>BuildHookDeniedTools</c> emits them, vendor-tagged (e.g.
    /// <c>"claude:Edit,Write,NotebookEdit"</c>). <see cref="Baton.Vendors"/> cannot reference
    /// <see cref="Baton.Cli"/> (the CLI depends on the adapters, never the reverse), so this name is a
    /// plain string contract mirrored on <c>ClaudeWorkerAdapter.DeniedToolsVariable</c> — both sides
    /// assert the literal value in their own test suite, and the two must agree.
    /// </summary>
    public const string DeniedToolsEnvironmentVariable = "BATON_HOOK_DENIED_TOOLS";

    /// <summary>
    /// The environment variable carried for pattern-scoped shell grants (#659). Wired into a claude
    /// worker's environment since #1459 (<c>ClaudeWorkerAdapter.ShellPatternsVariable</c>, same
    /// literal) — before that this name was declared on the adapter side and never set, so this
    /// channel always read <see cref="ShellPatternListStatus.Absent"/>.
    /// </summary>
    public const string ShellPatternsEnvironmentVariable = "BATON_HOOK_SHELL_PATTERNS";

    /// <summary>
    /// The environment variable carrying this invocation's standing-deny shell patterns (0022's
    /// DenyAlways rung, #390) — same literal as <c>AgyHookCheckCommand.DeniedShellPatternsEnvironmentVariable</c>
    /// and <c>ClaudeWorkerAdapter.DeniedShellPatternsVariable</c> (record-once). Belt-and-braces here:
    /// claude's primary enforcement of this rung is <c>--disallowedTools Bash(pattern)</c>, which
    /// survives a silently-dead hook (#530); this channel lets the segment-level check below reach
    /// what that flag's own whole-line matching cannot provably reach on its own (spec/baton.md §9).
    /// </summary>
    public const string DeniedShellPatternsEnvironmentVariable = "BATON_HOOK_DENIED_SHELL_PATTERNS";

    /// <summary>
    /// #1683 F2's channel — same literal as
    /// <c>ClaudeWorkerAdapter.DeniedShellOptionTokensVariable</c>, which owns the canonical "why"
    /// (record-once). NOT belt-and-braces the way the channel above is; that adapter member says why.
    /// </summary>
    public const string DeniedShellOptionTokensEnvironmentVariable =
        "BATON_HOOK_DENIED_SHELL_OPTION_TOKENS";

    /// <summary>
    /// Exit code 2, fed back to Claude Code as a blocking <c>PreToolUse</c> error (stderr becomes
    /// the reason shown to the model) — the only exit code that mechanism treats as a denial.
    /// </summary>
    public const int DeniedExitCode = 2;

    public const int AllowedExitCode = 0;

    /// <summary>
    /// Runs the check. Takes <paramref name="stdin"/>/<paramref name="stderr"/> and the raw env var
    /// value as parameters, rather than reading <see cref="Console"/>/<see cref="Environment"/>
    /// directly, so the decision logic is testable without a real subprocess.
    /// </summary>
    /// <param name="outboxDirectory">
    /// This execution's <c>BATON_OUTPUT_DIR</c> (#649). A withheld write whose target resolves inside it
    /// is allowed: that directory is AER's own, outside the workspace, and withholding "modify the
    /// workspace" was never meant to withhold "write your report". <see langword="null"/> disables the
    /// exemption entirely, so a hook that cannot tell where the outbox is denies as before.
    /// </param>
    /// <param name="workspaceDirectory">
    /// This worker's <c>BATON_WORKSPACE_DIR</c> (#679) — the <c>WorkingDirectory</c> it was dispatched
    /// against. A <b>granted</b> write is bounded to it or to the outbox; before #679 a granted write
    /// was bounded by nothing. <see langword="null"/> means no workspace was declared, which narrows a
    /// granted write to the outbox rather than widening it to the disk.
    /// </param>
    public static int Execute(
        TextReader stdin, TextWriter stderr, string? deniedToolsRaw, string? outboxDirectory = null,
        string? workspaceDirectory = null, string? shellPatternsRaw = null,
        string? deniedShellPatternsRaw = null, string? deniedShellOptionTokensRaw = null)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stderr);

        // #2009: one structured line per decision, into this room's grant log. Constructed here rather
        // than inside Decide so the catch below — a denial that never reaches any rung — is recorded
        // too. Reaching the room at all needs an absolute BATON_OUTPUT_DIR, exactly as the repeat
        // ledger does; without one the log is off and the decision is still taken.
        var scribe = new GrantDecisionScribe(VendorTag, outboxDirectory);

        try
        {
            return Decide(
                scribe, stdin, stderr, deniedToolsRaw, outboxDirectory, workspaceDirectory,
                shellPatternsRaw, deniedShellPatternsRaw, deniedShellOptionTokensRaw);
        }
        catch (Exception ex)
        {
            // A defect in Decide must not widen the grant it was installed to narrow. Claude Code
            // treats exit 2 as a blocking denial and *every other* non-zero code as a non-blocking
            // error it reports and then proceeds past -- so an unhandled exception's own exit code is
            // an allow. Naming DeniedExitCode here is what makes the failure closed.
            //
            // The write is itself guarded: a handler whose closure depends on a write succeeding is
            // not closed. Losing the reason costs the model an explanation; letting the exception
            // escape costs the denial itself, because Program.cs deliberately runs this branch
            // outside the BatonFlowException boundary and the process would die with an exit code
            // claude reads as an allow.
            try
            {
                var reason = GrantRefusal.Stamp(
                    $"AER: the permission gate failed internally ({ex.GetType().Name}) and denied this " +
                    "call rather than allowing it unchecked.");
                stderr.WriteLine(reason);
                // #2009: recorded inside the same guard as the write above, and after it, for the same
                // reason that write is guarded — nothing here may prevent the return below being
                // reached, and a denial the gate forced on itself is still a decision this room took.
                scribe.Deny(GrantRules.GateInternalFailure, reason);
            }
            catch
            {
                // Deliberately swallowed, and the only place in this file that is acceptable: the
                // return below is the decision, and nothing here may prevent it being reached.
            }

            return DeniedExitCode;
        }
    }

    private static int Decide(
        GrantDecisionScribe scribe, TextReader stdin, TextWriter stderr, string? deniedToolsRaw,
        string? outboxDirectory, string? workspaceDirectory, string? shellPatternsRaw,
        string? deniedShellPatternsRaw, string? deniedShellOptionTokensRaw)
    {
        // Always drain stdin before deciding anything, even when there is nothing to check
        // against below: Claude Code is the writer on the other end of this pipe, and exiting
        // before reading its full payload risks a broken-pipe/blocked-write on its side for any
        // tool_input large enough to fill the pipe buffer (a real Edit/Write payload can be).
        string input;
        try
        {
            input = stdin.ReadToEnd();
        }
        catch (IOException)
        {
            return Deny(scribe, stderr, "could not read the hook payload", GrantRules.UnjudgeableCall);
        }

        var deniedList = DeniedToolList.Parse(deniedToolsRaw, VendorTag);
        if (deniedList.Status != DeniedToolListStatus.Present)
        {
            // #600: absent, or another vendor's list. Either way this gate cannot say what is
            // withheld, and the old behaviour — allow — made a broken channel look like a working one.
            // #1921: it now says so. This was the one refusal path that denied in silence, which cost
            // the model any explanation and cost the count a refusal it could not see.
            return Refuse(scribe, stderr,
                "AER: the permission gate received no list of withheld tools for this " +
                "vendor and denied this call rather than allowing it unchecked.",
                GrantRules.UnjudgeableCall);
        }

        // #679 removed the early allow that used to sit here for an empty list. It read the empty
        // case as "nothing withheld, nothing to do", and that is exactly the shape the issue is
        // about: `implement` grants every category, so its list is empty and its writes were bounded
        // by nothing at all. A write is now bounded whether or not anything was withheld, so the
        // payload has to be parsed either way.
        //
        // An empty list still means one of two things this gate cannot tell apart -- a grant that
        // withholds nothing, or `PermissionGrant is null`, the raw PermissionScope escape hatch --
        // and both are bounded, deliberately. Distinguishing them would need a three-state
        // environment protocol whose empty-versus-absent case is unreliable across platforms, which
        // is a new way to fail open in the gate that exists to fail closed.
        var denied = deniedList.Tools;

        if (string.IsNullOrWhiteSpace(input))
        {
            return Deny(scribe, stderr, "received an empty hook payload", GrantRules.UnjudgeableCall);
        }

        string? toolName;
        string? writeTarget = null;
        string? shellCommandLine = null;
        (string Path, string Request)? readTarget = null;
        try
        {
            using var doc = JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("tool_name", out var toolNameProp))
            {
                return Deny(scribe, stderr, "could not find tool_name in the hook payload",
                    GrantRules.UnjudgeableCall);
            }

            toolName = toolNameProp.GetString();
            writeTarget = ReadWriteTarget(doc.RootElement, toolName);
            shellCommandLine = ReadShellCommandLine(doc.RootElement, toolName);
            readTarget = ReadReadTarget(doc.RootElement, toolName);
        }
        catch (JsonException)
        {
            return Deny(scribe, stderr, "could not parse the hook payload", GrantRules.UnjudgeableCall);
        }

        if (string.IsNullOrEmpty(toolName))
        {
            return Deny(scribe, stderr, "read an empty tool name from the hook payload",
                GrantRules.UnjudgeableCall);
        }

        // #2009: what the grant line says this call WAS, now that the payload has been read. The input
        // identity is whichever argument this gate judges the call on — the command line, the write
        // target, the read path — digested by GrantDecisionScribe, so "the worker retried the refused
        // call" is an equality over two lines rather than a search through prose.
        scribe.Tool = toolName;
        scribe.Input = shellCommandLine ?? writeTarget ?? readTarget?.Path;

        // #649: a write-family tool landing in the outbox is the worker's declared output, and it is
        // allowed before the denied-tools branch below can reclassify it -- a withheld write is not a
        // policy question once its target is the outbox. Hoisted here so that branch never has to
        // special-case it. Fires only on a TRUE IsInside (a rooted outbox); a non-rooted one still
        // falls through to the denied branch's #668 message. The denied branch keeps its own copy as
        // belt-and-braces defence in depth.
        if (WriteFamilyTools.Contains(toolName) && OutboxPath.IsInside(writeTarget, outboxDirectory))
        {
            Baton.Vendors.RepeatedToolCallHook.NoteWrite(outboxDirectory, writeTarget);
            return Allow(scribe);
        }

        if (denied.Contains(toolName))
        {
            // #649: the outbox is not the workspace. A withheld write landing in BATON_OUTPUT_DIR is the
            // worker producing its declared output, which is the whole reason it was dispatched --
            // denying it is what forced every reviewing template to grant a workspace write it never
            // needed. Anything outside stays denied, and OutboxPath resolves both sides so neither a
            // traversal nor a link can walk back into the repo.
            if (OutboxPath.IsInside(writeTarget, outboxDirectory))
            {
                Baton.Vendors.RepeatedToolCallHook.NoteWrite(outboxDirectory, writeTarget);
                return Allow(scribe);
            }

            // Name the cause when the exemption was unusable rather than the target being outside it.
            // A non-rooted BATON_OUTPUT_DIR denies every outbox write (OutboxPath refuses to resolve one
            // against this process's inherited cwd), and the generic message above would send an
            // operator looking at their permission grant for a fault that is in their --room-dir. The
            // run still fails its contract; it no longer fails without saying why. #668 is the root
            // cause -- AER emitting a relative path at all.
            if (outboxDirectory is not null && !Path.IsPathRooted(outboxDirectory))
            {
                return Refuse(scribe, stderr,
                    $"AER: the '{toolName}' tool is withheld, and its outbox exemption is unavailable " +
                    $"because BATON_OUTPUT_DIR ('{outboxDirectory}') is not an absolute path — this gate " +
                    "cannot tell where the outbox is. Re-run with an absolute --room-dir (#668).",
                    GrantRules.UnjudgeableCall);
            }

            return Refuse(scribe, stderr,
                $"AER: the '{toolName}' tool is withheld by this session's permission grant.",
                GrantRules.WithheldTool);
        }

        // #679: the tool is granted, which decides WHETHER it may write, never WHERE. Until this
        // existed the grant was a boolean while the risk was a path, and a granted write reached any
        // location the worker's own process could.
        if (WriteFamilyTools.Contains(toolName))
        {
            if (OutboxPath.IsInside(writeTarget, workspaceDirectory) ||
                OutboxPath.IsInside(writeTarget, outboxDirectory))
            {
                // #2002 review HIGH, hook half: the room is about to change the tree, so no remembered
                // command output is the answer any more. RepeatedToolCallHook.NoteWrite states the
                // rule; this is the only point a PreToolUse hook learns a write is coming.
                Baton.Vendors.RepeatedToolCallHook.NoteWrite(outboxDirectory, writeTarget);
                return Allow(scribe);
            }

            // Reached with a null writeTarget too, and that is the intended reading: a write-family
            // tool whose target this gate could not find is a write it cannot bound. Denying is what
            // keeps a future payload change loud instead of silently unbounded.
            return Refuse(scribe, stderr,
                $"AER: the '{toolName}' tool is granted, but its target " +
                $"({writeTarget ?? "unreadable from the payload"}) resolves outside both this " +
                "worker's workspace and its outbox. A grant decides whether a worker may write, not " +
                "where.",
                GrantRules.WriteOutsideBounds);
        }

        // #1459: the second enforcement layer for a scoped shell grant. Bash's own presence/absence
        // in `denied` above is the category gate; this evaluates the ACTUAL command claude granted
        // Bash for. See ShellCommandPatternMatcher.EvaluateChainedCommand for the measured hole this
        // closes and the segmentation rule that closes it (spec/baton.md §9).
        if (toolName == "Bash")
        {
            // #2002 rule 1 — spec/baton.md §9 states the rule, the measurement, and why every vendor's
            // own shell path carries it rather than the broker alone. Ahead of the pattern rungs below
            // because it is unconditional: it engages on an unscoped grant (`implement`, `janitor`),
            // which is precisely the population that backgrounds a build and then polls it. No ceiling
            // clause: this path enforces none, and naming the broker's would be a false claim about
            // what applies here.
            // ShellFamily.Posix, named outright rather than read off this OS: claude's Bash tool is a
            // POSIX shell on every platform it runs on, so a mid-line `&` really does background here
            // (#2002 review LOW).
            if (Baton.Vendors.BackgroundingShapeDetector.Detect(
                    shellCommandLine, Baton.Vendors.BackgroundingShapeDetector.ShellFamily.Posix)
                is { } backgrounding)
            {
                return Refuse(scribe, stderr,
                    Baton.Vendors.BackgroundingShapeDetector.Refusal(backgrounding, null),
                    GrantRules.Backgrounding);
            }

            var shellPatternList = ShellPatternList.Parse(shellPatternsRaw, VendorTag);

            // Absent or another vendor's list reads OPPOSITE to how the denied-tool channel above
            // reads its own absence. That channel denies on Absent because it is the sole record of
            // what Bash-adjacent categories were withheld, and a silently-dead copy of it would look
            // exactly like nothing withheld. This channel is a SECOND layer on top of claude's own
            // --allowedTools/--disallowedTools (which still ran and already decided Bash is reachable
            // at all) -- so a channel that never arrived here is read as "no scoped pattern list was
            // ever wired for this dispatch", i.e. an unscoped shell grant, not a broken deny. Making it
            // deny-on-absent would fail every existing unscoped `RunShellCommands: true` claude role
            // (`implement`, `janitor`) the moment this shipped, which is exactly what #1459's own issue
            // body flags as the reason this was deferred out of #1456.
            var deniedShellPatternList = ShellPatternList.Parse(deniedShellPatternsRaw, VendorTag);
            var deniedShellPatterns = deniedShellPatternList.Status == ShellPatternListStatus.Present
                ? deniedShellPatternList.Patterns
                : Array.Empty<string>();

            // #1731: NOT nested under shellPatternList.Patterns.Count > 0 alone, matching
            // AgyHookCheckCommand's condition (`deniedShellPatternList.Patterns.Count > 0 ||
            // shellPatternList.Patterns.Count > 0`, #1725) -- see spec/baton.md §9 for why claude's
            // own --disallowedTools flag is not a sufficient backstop on its own for this rung.
            if (deniedShellPatterns.Count > 0 || shellPatternList.Patterns.Count > 0)
            {
                var result = Baton.Vendors.ShellCommandPatternMatcher.EvaluateChainedCommand(
                    shellCommandLine, shellPatternList.Patterns, deniedShellPatterns);

                if (!result.IsAllowed)
                {
                    // "scoped" would misstate this for implement/janitor (#1731): this rung now also
                    // engages on an unscoped grant that carries a deny list, not only a scoped allow.
                    //
                    // #1920: the matcher is vendor-agnostic and cannot name a claude tool, so the
                    // granted read path is named here, from this session's own withheld-tool list —
                    // the measured claude review lane spent 46 refusals rediscovering Read/Grep.
                    //
                    // Scoped grants only, which is the population #1920 measured. On an UNSCOPED
                    // grant this rung fires for a standing deny instead (implement's and janitor's
                    // are `gh label*`, `gh pr merge*`, `gh api*`; `git push*`/`git commit*`/
                    // `git rebase*` are the SCOPED review role's — WorkerRoles.json is the register
                    // for both), and answering a write-shaped attempt with two read tools is the same
                    // non-responsive guidance this issue exists to remove.
                    var alternative = shellPatternList.Patterns.Count > 0
                        ? Baton.Vendors.GrantedReadToolHint.ForClaude(denied.Contains)
                        : null;
                    // #1921: result.Reason already carries the marker (ScopedShellResult stamps every
                    // refusal it produces), so Refuse's own Stamp is a no-op here by design.
                    return Refuse(scribe, stderr,
                        $"AER: the 'Bash' command is denied under this session's shell " +
                        $"grant — {result.Reason}." +
                        (alternative is null ? string.Empty : $" To proceed, {alternative}."),
                        GrantRules.ShellPattern);
                }
            }

            // #1683 F2: at the toolName == "Bash" level, matching where agy's own copy of this rung
            // sits (AgyHookCheckCommand), not nested under shellPatternList.Patterns.Count > 0. Nested
            // there, a role carrying denied_shell_option_tokens on top of a deliberate UNSCOPED shell
            // grant (Present, empty Patterns) would have the rung enforced on agy and silently skipped
            // on claude — no role ships that shape today (IsDeniedByOptionToken has nothing to bind an
            // empty-Patterns command to besides its own token list, so this is a no-op against every
            // current role), but the two hooks agreeing on when the rung engages is the point, not the
            // shape of today's catalog. Whole-line tokenization, not per-segment: the line reaching
            // here already passed the metacharacter scan and pattern pass above whenever those ran, and
            // every segment's tokens are the line's tokens.
            var deniedOptionTokenList = ShellPatternList.Parse(deniedShellOptionTokensRaw, VendorTag);
            if (deniedOptionTokenList.Status == ShellPatternListStatus.Present &&
                Baton.Vendors.ShellCommandPatternMatcher.IsDeniedByOptionToken(
                    shellCommandLine, deniedOptionTokenList.Patterns))
            {
                return Refuse(scribe, stderr,
                    "AER: the 'Bash' command carries an option this session's grant denies outright " +
                    "(a standing option-token 'never', matched anywhere on the line rather than at " +
                    "its start) and was refused.",
                    GrantRules.DeniedOptionToken);
            }

            // #2001: the sibling-pull-request rule, on the path that judges claude. Last of the
            // grant rungs, because it is the narrowest; only the #2002 repeat rung below follows it,
            // so a refused sibling read never enters the ledger. The broker's copy of this call passes the room's own PR number, which
            // it learns from `gh pr create`'s stdout; a PreToolUse hook decides before the command
            // runs and never sees that stdout, so it takes the entry point that needs no number —
            // a governed `gh pr` verb is allowed only with no selector, which `gh` resolves from the
            // branch this room is standing on. See OwnPullRequestOnlyRule for both entry points and
            // for the routes neither of them covers.
            //
            // The allow-pattern list is the whole of what this rung needs to know about the grant:
            // reaching here means Bash is granted, and a list that allowlists a `gh pr` read is the
            // review role opting out. What an ABSENT or wrong-vendor channel means here, and why it
            // reads opposite to the pattern rung above, is stated once on
            // OwnPullRequestOnlyRule.AppliesToShellPatterns.
            if (Baton.Vendors.OwnPullRequestOnlyRule.AppliesToShellPatterns(shellPatternList.Patterns))
            {
                if (shellCommandLine is null)
                {
                    // Belt-and-braces: both roles this rung governs today carry a deny list, so a
                    // Bash payload with no readable `command` is already denied above. A future
                    // governed role with neither list would reach here, and a command line this gate
                    // cannot read is one it cannot judge.
                    return Refuse(scribe, stderr,
                        "AER: the permission gate could not read the 'Bash' command line from the hook " +
                        "payload and denied this call rather than allowing it unchecked.",
                        GrantRules.UnjudgeableCall);
                }

                if (Baton.Vendors.OwnPullRequestOnlyRule.RefusalForOwnBranchOnly(shellCommandLine)
                    is { } siblingPullRequestRefusal)
                {
                    return Refuse(scribe, stderr, $"AER: {siblingPullRequestRefusal}",
                        GrantRules.OwnPullRequestOnly);
                }
            }

            // #2002 rule 2, hook half — LAST in this branch, so a command the grant would refuse
            // anyway is answered with the grant's reason and never enters the ledger as a command
            // that ran. See RepeatedToolCallHook for why every failure of this rung is an allow, and
            // spec/baton.md §9 for what shape this vendor's denial takes and why it cannot replay.
            if (Baton.Vendors.RepeatedToolCallHook.JudgeCommand(outboxDirectory, shellCommandLine)
                is { } repeatedCommand)
            {
                return Refuse(scribe, stderr, repeatedCommand, GrantRules.Repeat);
            }
        }

        // #2002 rule 2b, hook half. After the withheld-tool branch above, so a withheld read is
        // answered with the grant's reason rather than this one. The request, not the path alone —
        // see ReadReadTarget for the narrowed re-request this keys apart from a repeat.
        if (toolName == ReadToolName && readTarget is { } read &&
            Baton.Vendors.RepeatedToolCallHook.JudgeRead(outboxDirectory, read.Path, read.Request)
                is { } repeatedRead)
        {
            return Refuse(scribe, stderr, repeatedRead, GrantRules.Repeat);
        }

        return Allow(scribe);
    }

    /// <summary>
    /// The single allow exit (#2009), for the same reason <see cref="Refuse"/> is the single refusal
    /// one: this gate allows on four separate paths, and a fifth added without a line would make the
    /// room's refusal SHARE quietly wrong while its refusal count stayed right.
    /// </summary>
    private static int Allow(GrantDecisionScribe scribe)
    {
        scribe.Allow();
        return AllowedExitCode;
    }

    /// <summary>
    /// The fail-closed exits. Every one of these was an <see cref="AllowedExitCode"/> until #649, on
    /// the argument that <c>--disallowedTools</c> independently covered the same tool names — which
    /// #649 made false for writes by moving them off that flag onto this hook alone.
    /// </summary>
    private static int Deny(GrantDecisionScribe scribe, TextWriter stderr, string what, GrantRule rule) =>
        Refuse(scribe, stderr, $"AER: the permission gate {what} and denied this call rather than " +
                               "allowing it unchecked.", rule);

    /// <summary>
    /// <b>The single writer of every refusal this gate emits</b> (#1921), and the reason it exists as a
    /// method at all: this file refuses on eight distinct paths, and a ninth added without the marker
    /// would go uncounted with nothing failing. Claude Code copies this hook's stderr into the blocked
    /// call's <c>tool_result</c> text; <c>Status.ClaudeUsageParser.CountRefusedToolSteps</c> is the
    /// reader on the other end of that path and cites the room it was measured on.
    /// <para>
    /// The exit code is returned rather than written by the caller, so "wrote a refusal" and "denied"
    /// cannot come apart. The one refusal NOT routed through here is the internal-failure catch, which
    /// must be able to return <see cref="DeniedExitCode"/> even when the write itself throws; it stamps
    /// the marker inline for the same reason and its own comment says why the write is guarded.
    /// </para>
    /// </summary>
    /// <param name="rule">
    /// #2009: which <see cref="GrantRules"/> member decided, recorded on the room's grant line beside
    /// the reason. Required rather than defaulted, and taken here rather than derived at the funnel,
    /// for the reason this method exists at all: a rung added without naming its rule would otherwise
    /// be counted under whichever id the funnel happened to guess. A <see cref="GrantRule"/> rather
    /// than a string, so the id it names is one that exists.
    /// </param>
    private static int Refuse(GrantDecisionScribe scribe, TextWriter stderr, string reason, GrantRule rule)
    {
        var stamped = GrantRefusal.Stamp(reason);
        stderr.WriteLine(stamped);
        scribe.Deny(rule, stamped);
        return DeniedExitCode;
    }

    /// <summary>Mirrors <c>ClaudeWorkerAdapter.DeniedToolsVendorTag</c>; see it for why (#600).</summary>
    private const string VendorTag = "claude";

    /// <summary>
    /// Mirrors <c>WorkerEnvironment.WorkspaceVariable</c> — the workspace a granted write is bounded
    /// to (#679). See that member for why the name is written out on both sides.
    /// </summary>
    public const string WorkspaceEnvironmentVariable = "BATON_WORKSPACE_DIR";

    /// <summary>
    /// The filesystem path a write-family tool is targeting, or <see langword="null"/> for any other
    /// tool. Claude Code names it <c>file_path</c> on <c>Write</c>/<c>Edit</c> and
    /// <c>notebook_path</c> on <c>NotebookEdit</c>.
    /// </summary>
    /// <remarks>
    /// Gated on <paramref name="toolName"/>, not on the presence of the property: <c>Read</c> carries
    /// a <c>file_path</c> too, so keying off the field alone exempted reads inside the outbox from a
    /// withheld <c>ReadFiles</c> — a category #649 never meant to touch. The exemption exists because
    /// a withheld *write* still owes its declared output; nothing else claims it.
    /// </remarks>
    private static string? ReadWriteTarget(JsonElement root, string? toolName)
    {
        if (toolName is null || !WriteFamilyTools.Contains(toolName))
        {
            return null;
        }

        if (!root.TryGetProperty("tool_input", out var toolInput) ||
            toolInput.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in WriteTargetProperties)
        {
            if (toolInput.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static readonly string[] WriteTargetProperties = ["file_path", "notebook_path"];

    /// <summary>
    /// claude's file-read tool. Named as a constant because #2002 rule 2b keys on it and nothing else
    /// in this file did — <c>Grep</c> and <c>Glob</c> are searches whose answer legitimately moves,
    /// and neither is in scope for the unchanged-file rule.
    /// </summary>
    private const string ReadToolName = "Read";

    /// <summary>
    /// The <c>Read</c> call rule 2b judges — its path and the normalised spelling of the arguments
    /// that narrow what comes back — or <see langword="null"/> for any other tool, an unreadable
    /// payload, <b>or a payload carrying an argument this gate does not account for</b>. The path key
    /// is the same <c>file_path</c> the write family uses (claude Code names it that on both), read
    /// under its own gate for the reason <see cref="ReadWriteTarget"/>'s remarks give for keying off
    /// the tool name rather than the field.
    /// </summary>
    /// <remarks>
    /// <b>What this gate accounts for is <see cref="ReadArgumentNames"/> and nothing else, and the
    /// third exit above is the whole point (#2002 re-review HIGH).</b>
    /// <c>RepeatedToolCallLedger.ReadKey</c> owns the rule this implements — that keying past an
    /// argument which narrows the result makes the denial's own sentence false — so a payload naming
    /// an argument this gate cannot normalise disables the rung for that call rather than denying on
    /// a key that ignored it.
    /// <para>
    /// <b>What claude's <c>Read</c> payload actually carries is unmeasured here, and both registers
    /// were checked rather than assumed silent</b> (CLAUDE.md `common-sense`):
    /// <c>tools/vendor-verify/verify.py --list</c> has no check on any claude <c>tool_input</c>
    /// shape, and <c>docs/vendor-doc-audit.md</c> records one captured claude <c>PreToolUse</c>
    /// payload — for <c>Write</c> — in which <c>tool_input</c> holds the tool's own arguments
    /// (<c>file_path</c>, <c>content</c>) and nothing else, every session field sitting at the root
    /// beside it. That bounds this construction's cost but does not measure <c>Read</c>: if that tool
    /// sends an argument outside <see cref="ReadArgumentNames"/> on EVERY call, this rung is off for
    /// claude reads entirely and no denial is ever emitted. That is the fail-open direction every
    /// other failure of this rung takes, and it is the disclosed cost of not needing the
    /// measurement.
    /// </para>
    /// <para>
    /// A property present but <c>null</c> is treated as absent, so an explicit
    /// <c>"offset": null</c> keys the same as a whole-file read. Two spellings of one window
    /// (<c>offset: 0</c> against no offset) key differently and therefore both execute, which costs a
    /// read and never a false denial.
    /// </para>
    /// </remarks>
    private static (string Path, string Request)? ReadReadTarget(JsonElement root, string? toolName)
    {
        if (toolName != ReadToolName ||
            !root.TryGetProperty("tool_input", out var toolInput) ||
            toolInput.ValueKind != JsonValueKind.Object ||
            !toolInput.TryGetProperty("file_path", out var value) ||
            value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } path)
        {
            return null;
        }

        foreach (var property in toolInput.EnumerateObject())
        {
            if (!ReadArgumentNames.Contains(property.Name))
            {
                return null;
            }
        }

        // Fixed order, and raw JSON text so 10 and 1e1 are not silently one window — the ledger keys
        // on this string, and two spellings keying apart only ever costs a read.
        var request = string.Join(
            ";",
            ReadRangeArgumentNames
                .Where(name => toolInput.TryGetProperty(name, out var argument) &&
                               argument.ValueKind != JsonValueKind.Null)
                .Select(name => $"{name}={toolInput.GetProperty(name).GetRawText()}"));
        return (path, request);
    }

    /// <summary>
    /// The <c>Read</c> arguments beyond <c>file_path</c> that go into the read key, in the order they
    /// are spelled into it. <see cref="ReadReadTarget"/>'s remarks state what this list is for and what
    /// happens to a payload carrying anything outside it.
    /// </summary>
    private static readonly string[] ReadRangeArgumentNames = ["offset", "limit"];

    private static readonly IReadOnlySet<string> ReadArgumentNames =
        new HashSet<string>(["file_path", .. ReadRangeArgumentNames], StringComparer.Ordinal);

    /// <summary>
    /// The raw shell command line a <c>Bash</c> call carries, or <see langword="null"/> for any other
    /// tool or an unreadable payload. claude's Bash tool_input key is <c>command</c> (#1459) — the
    /// same key <see cref="Baton.Vendors.ShellCommandPatternMatcher.TryReadCommandLine"/> reads, not
    /// reused here directly because that helper wants the raw tool_input JSON text rather than an
    /// already-parsed <see cref="JsonElement"/>, and re-serializing one back to text just to re-parse
    /// it is wasted work this hook runs on every single tool call.
    /// </summary>
    private static string? ReadShellCommandLine(JsonElement root, string? toolName)
    {
        if (toolName != "Bash")
        {
            return null;
        }

        if (!root.TryGetProperty("tool_input", out var toolInput) ||
            toolInput.ValueKind != JsonValueKind.Object ||
            !toolInput.TryGetProperty("command", out var commandProp) ||
            commandProp.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return commandProp.GetString();
    }

    /// <summary>
    /// The write family this gate judges: the tools the outbox exemption applies to when a write is
    /// withheld (#649), and since #679 the tools whose target is bounded when one is granted. The
    /// same names <c>ClaudeWorkerAdapter</c> moves off <c>--disallowedTools</c> onto this hook.
    /// </summary>
    /// <remarks>
    /// Public so one test can see both sides. A mirror contract of the same kind as
    /// <see cref="DeniedToolsEnvironmentVariable"/>: <c>Baton.Vendors</c> cannot reference
    /// <c>Baton.Cli</c>, so nothing but a test holds the two in agreement, and
    /// <c>WriteFamilyContractTests</c> derives the adapter's side from a real <c>Resolve</c> rather
    /// than restating it.
    /// <para>
    /// <b>#679 reversed which way a missing name fails.</b> It used to be a withheld write unable to
    /// reach its own outbox — a broken run, paid for and failed. It is now also a <em>granted</em>
    /// write of that tool going unbounded, which is a hole. Same polarity as the agy sibling, which
    /// was always this way round.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> WriteFamilyTools =
        new HashSet<string>(StringComparer.Ordinal) { "Edit", "Write", "NotebookEdit" };
}
