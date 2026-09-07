using System.Text;

namespace Baton.Vendors;

/// <summary>
/// #2002 rule 1: recognises a command line that starts work in the BACKGROUND and hands the caller
/// back a handle instead of the work's output. Vendor-neutral by construction — it reads a raw
/// command line and nothing else — because all three shell paths reach it: the codex broker's
/// <c>baton_run_command</c> (<see cref="CodexDynamicToolPolicy"/>), claude's <c>Bash</c> hook and
/// agy's <c>run_command</c> hook. The measured offender was agy, whose native shell tool never
/// touches the broker at all, so a broker-only rule would not have reached it.
/// <para>
/// <b>Why backgrounding is refused rather than merely discouraged</b> is the spec's ruling
/// (<c>spec/baton.md</c> §9, "Polling is not progress"), not restated here. This type owns the
/// detection and the sentence; the ceiling clause is composed by each call site, because the ceiling
/// that applies differs per path and only the broker enforces one of its own.
/// </para>
/// </summary>
/// <remarks>
/// <b>Quote- and comment-aware, which is the whole difficulty.</b> <c>echo "run Start-Process later"</c>
/// and <c># Start-Process is banned</c> both contain the token and neither backgrounds anything, so
/// the scan runs over a MASKED copy of the line in which every quoted span and every comment tail has
/// been blanked to spaces. Blanked rather than removed so offsets and word boundaries survive.
/// <para>
/// Deliberately NOT built on <see cref="ShellCommandPatternMatcher"/>'s segmenter: that scanner's job
/// is to decide whether a line is parseable enough to match against a glob allowlist, and it REFUSES
/// on an unquoted <c>&amp;</c> outright under a scoped grant. This rule has to fire on the unscoped
/// grants (<c>implement</c>, <c>janitor</c>) that the segmenter never narrows, so it needs its own
/// mask. The two answer different questions about the same bytes.
/// </para>
/// <para>
/// <b>What this table would have refused in the room #2002 was built on: nothing.</b> Read
/// 2026-09-06 from <c>~/.baton/rooms/dispatch-implement-12f930d9</c>'s stream, the whole
/// <c>run_command</c> population was 414 calls carrying only a <c>CommandLine</c>, of which the
/// dominant shapes are <c>Get-Process -Id 59340 -ErrorAction SilentlyContinue</c> (48) and
/// <c>Get-CimInstance Win32_Process -Filter "ParentProcessId=29008" | Select-Object ProcessId,
/// CommandLine</c> (70 across pids), and the longest foreground calls are plain
/// <c>python tools/buildlock.py dotnet build -warnaserror</c> and
/// <c>python tools/buildlock.py dotnet test …</c>. No <c>Start-Process</c>, no <c>nohup</c>, no
/// <c>&amp;</c>, anywhere in the room. The polled pids came out of buildlock's own stdout
/// (<c>buildlock: waiting for the build lock held by PID 15892 (dotnet format --verify-no-changes)</c>),
/// so that lane was polling OTHER worktrees' builds it never started. This rule is therefore a
/// prospective one against model-authored backgrounding, not the fix for the measured histogram —
/// rule 2 is what would have bitten there. <c>spec/baton.md</c> §9 states that scoping; do not read
/// this table as covering the measurement.
/// </para>
/// </remarks>
public static class BackgroundingShapeDetector
{
    /// <summary>
    /// Which shell will interpret the line, because a bare <c>&amp;</c> means opposite things in the
    /// two families — see the <c>&amp;</c> branch in <see cref="Detect"/>. Chosen by the call site,
    /// which is the only place that knows what it is about to spawn.
    /// </summary>
    public enum ShellFamily
    {
        /// <summary><c>cmd.exe</c> or PowerShell, where a bare <c>&amp;</c> mid-line separates commands.</summary>
        Windows,

        /// <summary><c>/bin/sh</c>, bash, or claude's <c>Bash</c> tool, where a bare <c>&amp;</c> backgrounds.</summary>
        Posix,
    }

    /// <summary>
    /// The shape <paramref name="commandLine"/> backgrounds with, or <see langword="null"/> when it
    /// runs to completion in the foreground. The returned string is the literal a refusal names, so a
    /// worker reads back the thing it wrote.
    /// </summary>
    /// <param name="shell">
    /// Which shell will run the line. Only the bare-<c>&amp;</c> branch reads it, and it is not
    /// defaulted: every call site knows which shell it is about to spawn, and the wrong default is a
    /// silent miss rather than a compile error.
    /// </param>
    public static string? Detect(string? commandLine, ShellFamily shell)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var masked = MaskQuotedAndCommented(commandLine);

        // Order matters only for which name a line carrying two shapes reports; every branch refuses.
        if (ContainsWord(masked, "Start-Process") || ContainsWord(masked, "saps"))
        {
            return "Start-Process";
        }

        if (ContainsWord(masked, "Start-Job") || ContainsWord(masked, "Start-ThreadJob") ||
            ContainsWord(masked, "sajb"))
        {
            return "Start-Job";
        }

        // `start` and the cmd builtin behind it. Segment-anchored — first token of a top-level segment
        // only — because a bare word match would refuse `npm start`, `pixi run start` and
        // `dotnet run -- start`, none of which background anything. `saps`/`sajb` above need no such
        // anchoring: neither is a word any build tool takes as a subcommand.
        if (IsSegmentLeadingToken(masked, "start"))
        {
            return "start";
        }

        if (ContainsPhrase(masked, "cmd /c start") || ContainsPhrase(masked, "cmd /k start"))
        {
            return "cmd /c start";
        }

        // -AsJob is the flag that makes Invoke-Command asynchronous; Invoke-Command without it runs
        // to completion and is not this rule's business.
        if (ContainsWord(masked, "Invoke-Command") && ContainsWord(masked, "-AsJob"))
        {
            return "Invoke-Command -AsJob";
        }

        if (ContainsWord(masked, "nohup"))
        {
            return "nohup";
        }

        if (ContainsWord(masked, "setsid"))
        {
            return "setsid";
        }

        // The bare `&`, and the one rule that genuinely differs between the two shell families
        // (#2002 review LOW). On cmd.exe and PowerShell `a & b` is a plain command SEPARATOR and runs
        // both in the foreground, so only a trailing `&` backgrounds and refusing a mid-line one would
        // break rooms this rule must leave alone. Under a POSIX shell `a & b` really does background
        // `a`, so any unquoted `&` that is not part of `&&` and not part of a redirection (`2>&1`,
        // `>&2`, `&>file`) is the shape. Applying the Windows rule to a POSIX call site was a MISS,
        // never a false refusal — but the comment here used to read as though the semantics were
        // settled for all three sites, which is the defect.
        if (shell == ShellFamily.Posix)
        {
            return HasPosixBackgroundAmpersand(masked) ? "a background &" : null;
        }

        var trimmed = masked.TrimEnd();
        if (trimmed.EndsWith('&') && !trimmed.EndsWith("&&", StringComparison.Ordinal))
        {
            return "a trailing &";
        }

        return null;
    }

    /// <summary>
    /// The shell family a call site running on this OS should pass for a shell it spawns itself
    /// (<c>cmd.exe</c> on Windows, <c>/bin/sh</c> elsewhere) — the codex broker's branch, and agy's,
    /// whose <c>run_command</c> emits PowerShell on Windows (<c>docs/vendor-capabilities.md</c>,
    /// "Sharp edges"). claude's <c>Bash</c> tool does NOT use this: it is a POSIX shell on every
    /// platform, so that hook names <see cref="ShellFamily.Posix"/> outright.
    /// </summary>
    public static ShellFamily NativeShell =>
        OperatingSystem.IsWindows() ? ShellFamily.Windows : ShellFamily.Posix;

    /// <summary>
    /// Whether <paramref name="masked"/> carries an unquoted <c>&amp;</c> that backgrounds under a
    /// POSIX shell: not one half of <c>&amp;&amp;</c>, and not the <c>&amp;</c> of a redirection
    /// (<c>2&gt;&amp;1</c>, <c>&gt;&amp;2</c>, <c>&amp;&gt;file</c>).
    /// </summary>
    private static bool HasPosixBackgroundAmpersand(string masked)
    {
        for (var i = 0; i < masked.Length; i++)
        {
            if (masked[i] != '&')
            {
                continue;
            }

            if (i + 1 < masked.Length && masked[i + 1] == '&')
            {
                i++;
                continue;
            }

            // `&>` (redirect both streams) and `>&` / `2>&1` (duplicate a descriptor). Neither
            // backgrounds; both are ordinary in a build command's output plumbing.
            if (i + 1 < masked.Length && masked[i + 1] == '>')
            {
                continue;
            }

            if (i > 0 && masked[i - 1] == '>')
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="token"/> is the FIRST token of any top-level segment of
    /// <paramref name="masked"/> — segments being what <c>;</c>, <c>|</c>, <c>&amp;</c> and a newline
    /// separate. This is what makes <c>start notepad</c> a match and <c>npm start</c> not one.
    /// </summary>
    private static bool IsSegmentLeadingToken(string masked, string token)
    {
        foreach (var segment in masked.Split([';', '|', '&', '\n', '\r']))
        {
            var trimmed = segment.TrimStart();
            var end = trimmed.IndexOfAny([' ', '\t']);
            var first = end < 0 ? trimmed : trimmed[..end];
            if (string.Equals(first, token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="masked"/> contains <paramref name="phrase"/> at word boundaries, with
    /// any run of whitespace in the phrase matching any run in the text — so <c>cmd  /c   start</c>
    /// matches <c>cmd /c start</c>.
    /// </summary>
    private static bool ContainsPhrase(string masked, string phrase)
    {
        var collapsed = string.Join(' ', masked.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return ContainsWord(collapsed, phrase);
    }

    /// <summary>
    /// The one refusal sentence, composed once so the three call sites cannot drift into three
    /// different explanations of the same rule (record-once).
    /// </summary>
    /// <param name="shape">What <see cref="Detect"/> matched.</param>
    /// <param name="ceilingClause">
    /// The ceiling that applies on THIS path, already worded, or <see langword="null"/> when the path
    /// enforces none. Passed in rather than read here because only the broker has a per-command
    /// ceiling to name, and its value lives on the field that enforces it — never transcribed.
    /// </param>
    public static string Refusal(string shape, string? ceilingClause) =>
        $"Baton refused this command because it backgrounds the work ({shape}). Re-issue it without "
        + "that: the command runs to completion synchronously and its output comes back whole, and "
        + "waiting for it costs no tool step at all — whereas every poll of a backgrounded process "
        + "costs one step and one whole model turn. "
        + (ceilingClause ?? "This path enforces no Baton per-command ceiling; what is bounded is the "
                          + "number of tool steps, which polling is what exhausts.");

    /// <summary>
    /// <paramref name="line"/> with every quoted span and comment tail replaced by spaces. Handles
    /// single quotes (literal in both POSIX shells and PowerShell), double quotes, and the two escape
    /// characters that can hide a closing double quote — POSIX <c>\</c> and PowerShell <c>`</c>.
    /// An unterminated quote masks to end of line, which is the conservative direction: it can only
    /// cause this detector to MISS a shape on a line no shell would have run anyway.
    /// <para>
    /// <b>Invariant: <c>masked.Length == i</c> at the top of every iteration</b> — every branch appends
    /// exactly one character per character consumed. The comment test depends on it: it asks
    /// <c>masked.Length == 0</c> for "at the start of the line" while indexing <c>line</c> for the
    /// preceding character, and those two only agree because the lengths stay in step.
    /// </para>
    /// </summary>
    private static string MaskQuotedAndCommented(string line)
    {
        var masked = new StringBuilder(line.Length);
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inSingle)
            {
                masked.Append(c == '\'' ? '\'' : ' ');
                if (c == '\'')
                {
                    inSingle = false;
                }

                continue;
            }

            if (inDouble)
            {
                if ((c == '\\' || c == '`') && i + 1 < line.Length)
                {
                    masked.Append("  ");
                    i++;
                    continue;
                }

                masked.Append(c == '"' ? '"' : ' ');
                if (c == '"')
                {
                    inDouble = false;
                }

                continue;
            }

            switch (c)
            {
                case '\'':
                    inSingle = true;
                    masked.Append(c);
                    continue;
                case '"':
                    inDouble = true;
                    masked.Append(c);
                    continue;
                // A comment only starts at a token boundary in both shells: `#` mid-token is an
                // ordinary character (a branch name, a fragment, an issue reference like #2002).
                case '#' when masked.Length == 0 || char.IsWhiteSpace(line[i - 1]):
                    while (i < line.Length && line[i] is not ('\n' or '\r'))
                    {
                        masked.Append(' ');
                        i++;
                    }

                    i--;
                    continue;
                default:
                    masked.Append(c);
                    continue;
            }
        }

        return masked.ToString();
    }

    /// <summary>
    /// Whether <paramref name="token"/> occurs in <paramref name="text"/> as a whole word,
    /// case-insensitively (PowerShell cmdlet names and parameters are case-insensitive; the POSIX
    /// tokens here have no capitalised spelling to collide with). A word boundary is anything that is
    /// not a letter, digit, <c>_</c> or <c>-</c>, so <c>Start-Process</c> does not match inside
    /// <c>My-Start-Process-Wrapper</c> and <c>-AsJob</c> does not match <c>-AsJobName</c>.
    /// </summary>
    private static bool ContainsWord(string text, string token)
    {
        var from = 0;
        while (true)
        {
            var at = text.IndexOf(token, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return false;
            }

            var beforeOk = at == 0 || !IsWordCharacter(text[at - 1]);
            var afterAt = at + token.Length;
            var afterOk = afterAt >= text.Length || !IsWordCharacter(text[afterAt]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            from = at + 1;
        }
    }

    // '-' counts as a word character so a cmdlet name is one word, which is what makes the
    // `-AsJob` / `-AsJobName` and `Start-Process` / `Start-Processes` distinctions hold.
    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';
}
