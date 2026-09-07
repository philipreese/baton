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
/// </remarks>
public static class BackgroundingShapeDetector
{
    /// <summary>
    /// The shape <paramref name="commandLine"/> backgrounds with, or <see langword="null"/> when it
    /// runs to completion in the foreground. The returned string is the literal a refusal names, so a
    /// worker reads back the thing it wrote.
    /// </summary>
    public static string? Detect(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var masked = MaskQuotedAndCommented(commandLine);

        // Order matters only for which name a line carrying two shapes reports; every branch refuses.
        if (ContainsWord(masked, "Start-Process"))
        {
            return "Start-Process";
        }

        if (ContainsWord(masked, "Start-Job") || ContainsWord(masked, "Start-ThreadJob"))
        {
            return "Start-Job";
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

        // A trailing unquoted `&`, which is also what a `2>&1 &` tail reduces to once the redirection
        // is consumed as ordinary text. Trailing only, never mid-line: cmd.exe uses a bare `&` as a
        // plain command separator, so `a & b` runs both in the foreground and refusing it would break
        // the claude and codex rooms this rule must leave alone.
        var trimmed = masked.TrimEnd();
        if (trimmed.EndsWith('&') && !trimmed.EndsWith("&&", StringComparison.Ordinal))
        {
            return "a trailing &";
        }

        return null;
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
