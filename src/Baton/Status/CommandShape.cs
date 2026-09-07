using System.Text;

namespace Baton.Status;

/// <summary>
/// #2002 rule 3: collapses a shell command line to its SHAPE, so a hundred polls that differ only in
/// a process id read as one thing. <c>Get-Process -Id 59340 -ErrorAction SilentlyContinue</c> and
/// <c>Get-Process -Id 17056 -ErrorAction SilentlyContinue</c> are the same question asked twice, and
/// an arrest that names them separately says nothing.
/// </summary>
/// <remarks>
/// <b>Two collapses only</b> — digit runs and hex-looking words — because those are what actually
/// vary between two asks of the same shape: pids, ports, line numbers, issue numbers, commit shas.
/// Anything more aggressive starts merging genuinely different commands, and this string is shown to
/// an operator deciding whether an arrest was a runaway or a long job. Measured on the #2002 arm-A agy
/// room: these two collapses take 207 <c>run_command</c> steps down to a dominant shape holding
/// 53.6 % of them, which is the reading the arrest text exists to surface.
/// </remarks>
public static class CommandShape
{
    /// <summary>The longest shape reported, so an arrest message stays one line. Truncation is marked.</summary>
    public const int MaxShapeLength = 80;

    /// <summary>
    /// <paramref name="commandLine"/> with every hex-looking word replaced by <c>&lt;hash&gt;</c> and
    /// every remaining digit run by <c>&lt;n&gt;</c>, whitespace-collapsed and length-capped. Hashes
    /// first: a sha is also a digit run in part, and collapsing digits first would leave
    /// <c>a&lt;n&gt;bc&lt;n&gt;</c> behind instead of one token.
    /// </summary>
    public static string Normalize(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        var collapsed = new StringBuilder(commandLine.Length);
        var i = 0;
        while (i < commandLine.Length)
        {
            var c = commandLine[i];

            if (char.IsWhiteSpace(c))
            {
                if (collapsed.Length > 0 && collapsed[^1] != ' ')
                {
                    collapsed.Append(' ');
                }

                i++;
                continue;
            }

            if (IsHexDigit(c))
            {
                var start = i;
                while (i < commandLine.Length && IsHexDigit(commandLine[i]))
                {
                    i++;
                }

                var run = commandLine.AsSpan(start, i - start);
                var boundedBefore = start == 0 || !char.IsLetterOrDigit(commandLine[start - 1]);
                var boundedAfter = i >= commandLine.Length || !char.IsLetterOrDigit(commandLine[i]);

                // A sha is a standalone word of 7+ hex characters carrying at least one letter; a bare
                // digit run of any length is a number. A hex-looking run glued to other letters
                // (`Win32_Process`) is neither -- its digits still collapse below, as part of the word.
                if (boundedBefore && boundedAfter && run.Length >= 7 && ContainsHexLetter(run))
                {
                    collapsed.Append("<hash>");
                    continue;
                }

                i = start;
            }

            if (char.IsAsciiDigit(c))
            {
                while (i < commandLine.Length && char.IsAsciiDigit(commandLine[i]))
                {
                    i++;
                }

                collapsed.Append("<n>");
                continue;
            }

            collapsed.Append(c);
            i++;
        }

        var shape = collapsed.ToString().Trim();
        return shape.Length > MaxShapeLength ? shape[..MaxShapeLength] + "…" : shape;
    }

    private static bool IsHexDigit(char c) =>
        char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool ContainsHexLetter(ReadOnlySpan<char> run)
    {
        foreach (var c in run)
        {
            if (!char.IsAsciiDigit(c))
            {
                return true;
            }
        }

        return false;
    }
}
