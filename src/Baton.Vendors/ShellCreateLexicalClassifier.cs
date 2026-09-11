using System.Text;

namespace Baton.Vendors;

/// <summary>
/// Recognition only: literal simple commands, environment launchers, and the existing shell-wrapper
/// families. Never produces argv or evaluates expansions, scripts, or compound shell grammar.
/// Within wrapper bodies, dynamic words and compound constructs return Unsupported, as do unknown
/// launcher options and nesting beyond four levels. The caller supplies a direct-command alternative.
/// </summary>
internal static class ShellCreateLexicalClassifier
{
    internal enum Result { Ordinary, Create, Unsupported }
    private enum Shell { Posix, Cmd, PowerShell }
    private sealed record Word(string Value, string Raw, bool ConsumedEscape);

    public static Result Classify(string? commandLine) => Classify(commandLine, OperatingSystem.IsWindows());

    internal static Result Classify(string? commandLine, bool windows) => string.IsNullOrWhiteSpace(commandLine)
        ? Result.Ordinary : ClassifyList(commandLine, windows ? Shell.Cmd : Shell.Posix, 0, bounded: false);

    private static Result ClassifyList(string text, Shell shell, int depth, bool bounded)
    {
        if (depth > 4) return Result.Unsupported;
        if (!TryLex(text, shell, bounded, out var commands)) return Result.Unsupported;
        foreach (var words in commands)
        {
            var result = ClassifyCommand(words, shell, depth, bounded);
            if (result != Result.Ordinary) return result;
        }
        return Result.Ordinary;
    }

    private static Result ClassifyCommand(IReadOnlyList<Word> words, Shell shell, int depth, bool bounded)
    {
        if (depth > 4) return Result.Unsupported;
        var index = 0;
        while (index < words.Count && IsAssignment(words[index].Value)) index++;
        // Cmd's echo-suppression prefix may occupy its own word (or several).
        if (shell == Shell.Cmd)
            while (index < words.Count && words[index].Value.Length > 0
                && words[index].Value.All(c => c == '@')) index++;
        if (index == words.Count) return Result.Ordinary;
        var head = Name(shell == Shell.Cmd ? words[index].Value.TrimStart('@') : words[index].Value);
        if (head == "env")
        {
            index++;
            while (index < words.Count)
            {
                var option = words[index].Value;
                if (option == "--") { index++; break; }
                if (IsAssignment(option) || option is "-i" or "--ignore-environment") { index++; continue; }
                if (option is "-u" or "--unset" or "-C" or "--chdir")
                {
                    if (index + 1 == words.Count) return Result.Unsupported;
                    index += 2;
                    continue;
                }
                if (option.StartsWith('-')) return Result.Unsupported;
                break;
            }
            return index >= words.Count ? Result.Ordinary
                : ClassifyCommand(words.Skip(index).ToArray(), shell, depth + 1, bounded);
        }
        if (head == "gh" && index + 2 < words.Count
            && words[index + 1].Value.Equals("pr", StringComparison.OrdinalIgnoreCase)
            && words[index + 2].Value.Equals("create", StringComparison.OrdinalIgnoreCase))
            return Result.Create;

        // Reuse the standing policy's family register, but not its every-offset body scan:
        // argv data and command positions have opposite polarity here.
        if (ShellCommandPatternMatcher.TryReadShellWrapperBody(head, out _))
            return ClassifyWrapper(words.Skip(index).ToArray(), head, depth);

        if (bounded && head is "if" or "then" or "else" or "elif" or "fi"
            or "for" or "foreach" or "while" or "until" or "do" or "done" or "case" or "esac"
            or "function" or "time" or "coproc" or "eval" or "exec" or "command" or "call"
            or "start" or "invoke-expression" or "iex" or "." or "source" or "!"
            or "select" or "repeat" or "noglob" or "nocorrect" or "builtin")
            return Result.Unsupported;
        return Result.Ordinary;
    }

    private static Result ClassifyWrapper(IReadOnlyList<Word> words, string head, int depth)
    {
        var shell = head == "cmd" ? Shell.Cmd
            : head is "pwsh" or "powershell" ? Shell.PowerShell : Shell.Posix;
        for (var i = 1; i < words.Count; i++)
        {
            var option = shell == Shell.Posix ? words[i].Value : words[i].Value.ToLowerInvariant();
            var commandOption = shell switch
            {
                Shell.Cmd => option is "/c" or "/k",
                Shell.PowerShell => option is "-c" or "-command",
                _ => option.StartsWith('-') && option.EndsWith('c')
                    && option[1..].All(c => "ilabefhkmnptuvxBCEHPTc".Contains(c)),
            };
            if (commandOption)
            {
                if (++i == words.Count) return Result.Unsupported;
                // POSIX -c executes exactly one argument; following arguments bind $0/$1.
                // A multiword cmd/PowerShell tail needs quotes retained, but Raw can restore
                // escapes consumed by the outer shell. Refuse that ambiguous transport instead
                // of classifying text different from what the nested shell actually receives.
                if (shell != Shell.Posix && i < words.Count - 1
                    && words.Skip(i).Any(word => word.ConsumedEscape)) return Result.Unsupported;
                var body = shell == Shell.Posix || i == words.Count - 1
                    ? words[i].Value : string.Join(" ", words.Skip(i).Select(word => word.Raw));
                return ClassifyList(body, shell, depth + 1, bounded: true);
            }
            if (shell == Shell.PowerShell && option is "-file" or "-f") return Result.Ordinary;
            if (shell == Shell.Posix && !option.StartsWith('-')) return Result.Ordinary; // script path
            if (shell == Shell.Cmd && option is "/d" or "/s" or "/q") continue;
            if (shell == Shell.PowerShell && option is "-noprofile" or "-nop" or "-noninteractive"
                or "-noni" or "-nologo") continue;
            if (shell == Shell.Posix && option is "-l" or "-i" or "--noprofile" or "--norc") continue;
            return Result.Unsupported;
        }
        return Result.Unsupported;
    }

    // Shell selects native syntax at every layer; bounded applies only inside a readable wrapper.
    // Ordinary native commands retain the unscoped shell policy's permissive treatment of grammar
    // outside recognition. CodexDynamicToolPolicy spawns cmd on Windows and /bin/sh elsewhere.
    private static bool TryLex(string text, Shell shell, bool bounded, out List<List<Word>> commands)
    {
        commands = [];
        var words = new List<Word>();
        var value = new StringBuilder();
        var start = -1;
        char quote = '\0';
        var skipRedirectTarget = false;
        var consumedEscape = false;
        void EndWord(int end)
        {
            if (start < 0) return;
            if (!skipRedirectTarget) words.Add(new Word(value.ToString(), text[start..end], consumedEscape));
            skipRedirectTarget = false;
            consumedEscape = false;
            value.Clear();
            start = -1;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var singleLiteral = quote == '\'' && shell != Shell.Cmd;
            var escape = !singleLiteral && (shell == Shell.Posix && c == '\\'
                || shell == Shell.Cmd && quote == '\0' && c == '^'
                || shell == Shell.PowerShell && c == '`');
            if (escape)
            {
                if (i + 1 == text.Length) return false;
                if (shell == Shell.Posix && quote == '"' && text[i + 1] is not ('"' or '\\' or '$' or '`' or '\n'))
                {
                    if (start < 0) start = i;
                    value.Append(c);
                    continue;
                }
                if (start < 0) start = i;
                consumedEscape = true;
                c = text[++i];
                if (c == '\n') continue;
                // PowerShell's escape sequences can generate characters; refuse those instead
                // of treating `a (BEL), for example, as a literal a in an executable.
                if (shell == Shell.PowerShell && "0abefnrtuv".Contains(c)) return false;
                value.Append(c);
                continue;
            }
            if (!singleLiteral && bounded
                && (shell == Shell.Cmd ? c is '%' or '!' : c == '$' || shell == Shell.Posix && c == '`'))
                return false;
            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (shell == Shell.PowerShell && i + 1 < text.Length && text[i + 1] == quote)
                    { value.Append(c); i++; }
                    else quote = '\0';
                }
                else value.Append(c);
                continue;
            }
            if (c == '"' || c == '\'' && shell != Shell.Cmd)
            {
                if (start < 0) start = i;
                quote = c;
                continue;
            }
            if (c == '#' && start < 0 && shell is Shell.Posix or Shell.PowerShell)
            {
                while (i < text.Length && text[i] != '\n') i++;
                i--;
                continue;
            }
            if (c is '(' or ')' or '{' or '}' && bounded) return false;
            if (!bounded && c is '(' or ')')
            {
                EndWord(i);
                commands.Add(words);
                words = [];
                continue;
            }
            if (bounded && shell is Shell.Posix or Shell.PowerShell && c is '*' or '?' or '[') return false;
            if (shell == Shell.PowerShell && (c == '@' || text.AsSpan(i).StartsWith("--%"))) return false;
            if (c is '<' or '>')
            {
                if (!bounded)
                {
                    EndWord(i);
                    commands.Add(words);
                    words = [];
                    continue;
                }
                // Only simple file redirections: descriptor duplication and here documents
                // need more grammar and are explicitly outside this classifier.
                if (i + 1 < text.Length && (text[i + 1] == '&' || c == '<' && text[i + 1] == '<')) return false;
                if (start >= 0 && value.ToString().All(char.IsAsciiDigit)) { value.Clear(); start = -1; }
                EndWord(i);
                skipRedirectTarget = true;
                if (i + 1 < text.Length && text[i + 1] == c) i++;
                continue;
            }
            if (c is '&' or '|' or '\r' or '\n' || c == ';' && shell != Shell.Cmd)
            {
                EndWord(i);
                if (skipRedirectTarget) return false;
                commands.Add(words);
                words = [];
                continue;
            }
            if (char.IsWhiteSpace(c)) { EndWord(i); continue; }
            if (start < 0) start = i;
            value.Append(c);
        }
        EndWord(text.Length);
        if (bounded && quote != '\0' || skipRedirectTarget) return false;
        commands.Add(words);
        return true;
    }

    private static bool IsAssignment(string value)
    {
        var equals = value.IndexOf('=');
        return equals > 0 && (char.IsAsciiLetter(value[0]) || value[0] == '_')
            && value[..equals].All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    private static string Name(string value)
    {
        var name = value.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }
}
