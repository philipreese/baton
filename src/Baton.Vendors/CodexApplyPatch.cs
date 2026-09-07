namespace Baton.Vendors;

/// <summary>
/// Codex's native edit envelope (<c>*** Begin Patch</c> … <c>*** End Patch</c>), parsed into the file
/// operations <see cref="CodexDynamicToolPolicy"/> then applies through its own granted write path
/// (#1996). Parsing is separated from the policy so the format has one home and the grant has another:
/// nothing here resolves a path, opens a file, or knows what a workspace root is.
/// <para>
/// <b>Deliberately narrow.</b> Context matches are exact — no fuzzy match, no whitespace-tolerant
/// fallback — and it must match in exactly one place: a hunk whose context fits twice is refused
/// rather than placed at the first fit, and a <c>@@</c> locator moves where the search starts rather
/// than being guessed at or discarded. A patch this parser cannot place unambiguously is an
/// <see cref="ArgumentException"/> naming what it could not resolve, which is a step the model
/// recovers from; a hunk applied at the wrong offset is a corrupted file that reports success. The
/// accepted subset and the placement rules are stated once for the model in
/// <c>CodexDynamicToolPolicy</c>'s tool description, which is its only spec for the format.
/// </para>
/// </summary>
internal static class CodexApplyPatch
{
    private const string BeginMarker = "*** Begin Patch";
    private const string EndMarker = "*** End Patch";
    private const string AddPrefix = "*** Add File: ";
    private const string DeletePrefix = "*** Delete File: ";
    private const string UpdatePrefix = "*** Update File: ";
    private const string MovePrefix = "*** Move to:";
    private const string EndOfFileMarker = "*** End of File";
    private const string SectionMarker = "@@";

    /// <summary>
    /// The envelope's operations in the order they were written. Throws <see cref="ArgumentException"/>
    /// for anything malformed or outside the accepted subset — the policy maps that to a failed (not
    /// refused) tool result, because no grant decided it.
    /// </summary>
    public static IReadOnlyList<CodexPatchOperation> Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var lines = SplitLines(input, out _, out _);
        // #1996 re-review LOW: a file this patch CREATES has no line ending of its own to preserve, so
        // it takes the envelope's rather than the worker machine's — LF unless the patch itself came
        // through carrying CRLF. The platform default put CRLF into an all-LF repo, which surfaces as
        // a formatting failure on a later step rather than here.
        var carriageReturns = input.Contains("\r\n", StringComparison.Ordinal);
        var start = 0;
        while (start < lines.Count && lines[start].Length == 0)
        {
            start++;
        }
        if (start >= lines.Count || !lines[start].StartsWith(BeginMarker, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"A patch must start with '{BeginMarker}' and end with '{EndMarker}'.");
        }

        List<CodexPatchOperation> operations = [];
        CodexPatchOperation? current = null;
        var sawEnd = false;
        for (var index = start + 1; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.StartsWith(EndMarker, StringComparison.Ordinal))
            {
                sawEnd = true;
                break;
            }
            if (line.StartsWith(MovePrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "'*** Move to:' is not supported by Baton's apply_patch. Add the new file and "
                    + "delete the old one in the same patch instead.");
            }
            if (line.StartsWith(AddPrefix, StringComparison.Ordinal))
            {
                current = New(CodexPatchOperationKind.Add, line[AddPrefix.Length..], operations, index, carriageReturns);
                continue;
            }
            if (line.StartsWith(DeletePrefix, StringComparison.Ordinal))
            {
                current = New(CodexPatchOperationKind.Delete, line[DeletePrefix.Length..], operations, index, carriageReturns);
                continue;
            }
            if (line.StartsWith(UpdatePrefix, StringComparison.Ordinal))
            {
                current = New(CodexPatchOperationKind.Update, line[UpdatePrefix.Length..], operations, index, carriageReturns);
                continue;
            }
            if (line.StartsWith("*** ", StringComparison.Ordinal))
            {
                if (line.StartsWith(EndOfFileMarker, StringComparison.Ordinal))
                {
                    // A marker on the context, not an operation: the hunk above it runs to the end of
                    // the file. Exact matching already places it, so it carries no extra meaning here.
                    continue;
                }
                throw new ArgumentException($"Unsupported patch directive '{line}'.");
            }
            if (current is null)
            {
                throw new ArgumentException(
                    $"Patch line '{line}' appears before any '{AddPrefix.Trim()}', "
                    + $"'{DeletePrefix.Trim()}' or '{UpdatePrefix.Trim()}' header.");
            }
            AppendBodyLine(current, line);
        }

        if (!sawEnd)
        {
            throw new ArgumentException($"The patch is missing its '{EndMarker}' line.");
        }
        if (operations.Count == 0)
        {
            throw new ArgumentException("The patch declares no file operations.");
        }
        EnsureOnePathOneHeader(operations);
        foreach (var operation in operations)
        {
            Validate(operation);
        }
        return operations;
    }

    /// <summary>
    /// <paramref name="original"/> with every chunk applied in order, each placed at the ONE exact
    /// occurrence of its context at or after its search floor — the previous chunk's end, moved
    /// forward to the chunk's <c>@@</c> locator when it carries one. A context that fits in more than
    /// one place from that floor throws rather than taking the first fit. The file's own line ending
    /// and its trailing-newline state are preserved, so a CRLF file stays CRLF.
    /// </summary>
    public static string ApplyUpdate(string original, CodexPatchOperation operation)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(operation);

        var lines = SplitLines(original, out var newline, out var endsWithNewline);
        List<string> result = [];
        var cursor = 0;
        foreach (var chunk in operation.Chunks)
        {
            var floor = FloorFor(lines, chunk, operation, cursor);
            var matches = MatchPositions(lines, chunk.Before, floor);
            if (matches.Count == 0)
            {
                throw new ArgumentException(DescribeUnplaceable(lines, chunk, operation, floor));
            }
            if (matches.Count > 1)
            {
                // Never the first match: the model gave one description of a place and the file holds
                // several, so which one it meant is unknown. Saying so costs it one step; guessing
                // costs it a wrongly edited file reported as a success.
                throw new ArgumentException(
                    $"Patch context is ambiguous in '{operation.Path}': the hunk beginning "
                    + $"'{chunk.Before[0]}' matches at {matches.Count} places, first at lines "
                    + $"{matches[0] + 1} and {matches[1] + 1}. Add more context lines, or put a "
                    + $"'{SectionMarker} <line>' locator above the hunk reproducing exactly a line "
                    + "that precedes the one place you mean.");
            }
            var at = matches[0];
            result.AddRange(lines.Skip(cursor).Take(at - cursor));
            result.AddRange(chunk.After);
            cursor = at + chunk.Before.Count;
        }
        result.AddRange(lines.Skip(cursor));
        return string.Join(newline, result) + (endsWithNewline ? newline : string.Empty);
    }

    /// <summary>
    /// Where this chunk's context search starts: the previous chunk's end, or the chunk's <c>@@</c>
    /// locator line if it names one further down. Searching for the locator from <paramref name="cursor"/>
    /// rather than from the top is what keeps the floor monotonic — a floor behind the cursor would
    /// re-emit lines already written.
    /// <para>
    /// #1996 re-review LOW: forward-only is <b>Baton's</b> choice, not a measured copy of codex's own
    /// applier. Whether that applier searches backwards, tolerates whitespace, or retries fuzzily is
    /// unmeasured here — <c>docs/vendor-codex-probe-2026-09-04.md</c>'s "Known unknowns" records a
    /// live workspace-write role as never exercised on that host, and no live CLI was run for this
    /// change. Baton picks forward-only because it makes the ambiguity refusal deterministic: the
    /// floor only ever moves forward, so a hunk's match count depends on the patch alone rather than
    /// on where an earlier chunk happened to land, and the same envelope against the same file always
    /// gets the same answer. Any divergence from the vendor costs a refusal the model recovers from,
    /// never a misplaced write.
    /// </para>
    /// </summary>
    private static int FloorFor(
        IReadOnlyList<string> lines, CodexPatchChunk chunk, CodexPatchOperation operation, int cursor)
    {
        if (chunk.Anchor is not { } anchor)
        {
            return cursor;
        }
        var at = IndexOfAnchor(lines, anchor, cursor);
        if (at < 0)
        {
            throw new ArgumentException(
                $"Patch locator not found in '{operation.Path}': no line after the previous hunk "
                + $"matches '{anchor}'. A '{SectionMarker}' line must reproduce the text of a line of "
                + "the file; its indentation is ignored, but nothing else is.");
        }
        return at;
    }

    /// <summary>
    /// #1996 re-review LOW: names the first context line that is nowhere to be found, not simply the
    /// block's first line — which on a multi-line hunk is usually a line the model can see is present,
    /// so the message sent it looking for the wrong thing. When every line does exist separately, the
    /// defect is the order or the run, and it says that instead.
    /// </summary>
    private static string DescribeUnplaceable(
        IReadOnlyList<string> lines, CodexPatchChunk chunk, CodexPatchOperation operation, int floor)
    {
        var missing = chunk.Before.FirstOrDefault(line => IndexOfLine(lines, line, floor) < 0);
        var detail = missing is not null
            ? $"no line matches '{missing}' where the patch expects it"
            : $"the lines of the hunk beginning '{chunk.Before[0]}' all appear, but not consecutively "
                + "and in that order where the patch expects them";
        return $"Patch context not found in '{operation.Path}': {detail}. Context must match the file "
            + "exactly; re-read the file and patch again, or replace it whole with "
            + $"{CodexDynamicToolPolicy.WriteTextTool}.";
    }

    /// <summary>
    /// The exact content an <c>*** Add File:</c> operation creates. A file that did not exist has no
    /// line ending of its own to preserve, so it takes LF unless the patch envelope itself arrived
    /// with CRLF (#1996 re-review LOW): the platform default it used to take wrote CRLF into an all-LF
    /// repository, where it fails a formatting gate one step later rather than here, and there is
    /// nothing on this side of the parser boundary that knows the repository's convention.
    /// </summary>
    public static string AddedContent(CodexPatchOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var newline = operation.UsesCarriageReturns ? "\r\n" : "\n";
        return string.Join(newline, operation.AddedLines) + newline;
    }

    private static CodexPatchOperation New(
        CodexPatchOperationKind kind,
        string path,
        List<CodexPatchOperation> operations,
        int lineIndex,
        bool carriageReturns)
    {
        var trimmed = path.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException($"A '{kind}' patch header names no file.");
        }
        var operation = new CodexPatchOperation(kind, trimmed)
        {
            HeaderLine = lineIndex + 1,
            UsesCarriageReturns = carriageReturns,
        };
        operations.Add(operation);
        return operation;
    }

    /// <summary>
    /// #1996 re-review HIGH: two headers naming one path is refused whole, never applied in header
    /// order. Each operation is planned and written independently from what is on DISK, so a second
    /// header for the same path silently discards the first one's hunks (or, across kinds, re-creates
    /// the file the same patch just deleted) and still reports success — the corrupted-file-that-reports
    /// -success failure this parser exists to refuse, reached without a single ambiguous hunk.
    /// <para>
    /// Keyed on the path text with separators unified, because codex's measured habit is a Windows
    /// backslash (#1920). Nothing here resolves a path, so every alias that only a resolver can see
    /// gets past this check and is refused one layer out instead — <c>CodexDynamicToolPolicy.ApplyPatch</c>'s
    /// plan loop, which keys the resolved path before any byte is written and states which aliases
    /// those are (#1996 re-review LOW). Two checks rather than one because this one names both header
    /// LINES, which the parser knows and the policy does not.
    /// </para>
    /// </summary>
    private static void EnsureOnePathOneHeader(List<CodexPatchOperation> operations)
    {
        Dictionary<string, CodexPatchOperation> seen = [];
        foreach (var operation in operations)
        {
            var key = operation.Path.Replace('\\', '/');
            if (seen.TryGetValue(key, out var first))
            {
                throw new ArgumentException(
                    $"'{operation.Path}' has two patch headers, at line {first.HeaderLine} "
                    + $"({first.Kind}) and line {operation.HeaderLine} ({operation.Kind}): a path "
                    + "appears twice; merge the hunks into one Update File block.");
            }
            seen.Add(key, operation);
        }
    }

    private static void AppendBodyLine(CodexPatchOperation operation, string line)
    {
        switch (operation.Kind)
        {
            case CodexPatchOperationKind.Add:
                if (!line.StartsWith('+'))
                {
                    throw new ArgumentException(
                        $"Every body line of '{AddPrefix}{operation.Path}' must start with '+'.");
                }
                operation.AddedLines.Add(line[1..]);
                return;
            case CodexPatchOperationKind.Delete:
                if (line.Length == 0)
                {
                    return;
                }
                throw new ArgumentException(
                    $"'{DeletePrefix}{operation.Path}' takes no body lines.");
            default:
                AppendUpdateLine(operation, line);
                return;
        }
    }

    private static void AppendUpdateLine(CodexPatchOperation operation, string line)
    {
        if (line.StartsWith(SectionMarker, StringComparison.Ordinal))
        {
            // #1996 re-review HIGH: the marker starts a new chunk AND, when it carries text, anchors
            // where that chunk's context search begins — the disambiguating half of codex's own
            // dialect, which this parser used to discard while its description advertised support.
            var anchor = line[SectionMarker.Length..].Trim();
            if (operation.Chunks.Count > 0 && operation.Chunks[^1] is { Before.Count: 0, After.Count: 0 })
            {
                // Stacked locators (codex's nested-scope form) would silently lose the outer one to
                // Validate's empty-chunk sweep, which is a locator the model believes it supplied.
                throw new ArgumentException(
                    $"A hunk of '{operation.Path}' stacks two '{SectionMarker}' locator lines. Baton "
                    + "takes one locator per hunk: keep the innermost line and add context lines if "
                    + "it is still ambiguous.");
            }
            operation.Chunks.Add(new CodexPatchChunk { Anchor = anchor.Length == 0 ? null : anchor });
            return;
        }
        if (operation.Chunks.Count == 0)
        {
            operation.Chunks.Add(new CodexPatchChunk());
        }
        var chunk = operation.Chunks[^1];
        switch (line.Length == 0 ? ' ' : line[0])
        {
            case '+':
                chunk.After.Add(line[1..]);
                return;
            case '-':
                chunk.Before.Add(line[1..]);
                return;
            case ' ':
                var context = line.Length == 0 ? string.Empty : line[1..];
                chunk.Before.Add(context);
                chunk.After.Add(context);
                return;
            default:
                throw new ArgumentException(
                    $"Patch line '{line}' must start with ' ', '+', '-' or '{SectionMarker}'.");
        }
    }

    private static void Validate(CodexPatchOperation operation)
    {
        switch (operation.Kind)
        {
            case CodexPatchOperationKind.Add when operation.AddedLines.Count == 0:
                throw new ArgumentException($"'{AddPrefix}{operation.Path}' adds no lines.");
            case CodexPatchOperationKind.Update:
                operation.Chunks.RemoveAll(chunk => chunk.Before.Count == 0 && chunk.After.Count == 0);
                if (operation.Chunks.Count == 0)
                {
                    throw new ArgumentException($"'{UpdatePrefix}{operation.Path}' changes nothing.");
                }
                var contextless = operation.Chunks.FindIndex(chunk => chunk.Before.Count == 0);
                if (contextless >= 0)
                {
                    // A pure-addition chunk names no place in the file. Its @@ locator, if it has one,
                    // moves where a search STARTS; it does not name the line to insert at, so there is
                    // still nothing to place the hunk against, and a wrong offset is a silent corruption.
                    throw new ArgumentException(
                        $"A hunk of '{operation.Path}' has no context or removed lines, so Baton "
                        + "cannot tell where it goes. Include at least one unchanged context line.");
                }
                return;
            default:
                return;
        }
    }

    /// <summary>
    /// Every position at or after <paramref name="from"/> where <paramref name="before"/> occurs — all
    /// of them, not the first, because the count is the decision: one places the hunk, more than one
    /// refuses it. Overlapping occurrences count separately; each is a place the hunk would fit.
    /// </summary>
    private static List<int> MatchPositions(
        IReadOnlyList<string> lines, IReadOnlyList<string> before, int from)
    {
        List<int> matches = [];
        for (var start = from; start + before.Count <= lines.Count; start++)
        {
            var matched = true;
            for (var offset = 0; offset < before.Count; offset++)
            {
                if (!string.Equals(lines[start + offset], before[offset], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }
            if (matched)
            {
                matches.Add(start);
            }
        }
        return matches;
    }

    /// <summary>
    /// Where a <c>@@</c> locator's line is, compared trimmed on BOTH sides — the anchor is trimmed
    /// when it is parsed (<see cref="AppendUpdateLine"/>) while the file's line keeps its indentation,
    /// so an Ordinal comparison made a locator naming any line inside a class or a function match
    /// nothing at all, whatever spelling the model used (#1996 re-review HIGH). Context matching stays
    /// exact: widening only the anchor cannot misplace a hunk, because the anchor moves a search FLOOR
    /// and <see cref="MatchPositions"/>'s one-place rule still decides where the hunk lands. A looser
    /// anchor therefore changes which refusal is raised, never which offset is written.
    /// </summary>
    private static int IndexOfAnchor(IReadOnlyList<string> lines, string anchor, int from)
    {
        for (var index = from; index < lines.Count; index++)
        {
            if (string.Equals(lines[index].Trim(), anchor, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private static int IndexOfLine(IReadOnlyList<string> lines, string line, int from)
    {
        for (var index = from; index < lines.Count; index++)
        {
            if (string.Equals(lines[index], line, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    /// <summary>
    /// Splits on line feeds and reports the file's own ending, so a rewritten file keeps the one it
    /// had. A patch body's lines never carry a carriage return of their own — they arrive from JSON —
    /// so a CRLF file whose <c>\r</c> stayed on the line would match no context at all.
    /// </summary>
    private static List<string> SplitLines(string text, out string newline, out bool endsWithNewline)
    {
        newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        endsWithNewline = text.EndsWith('\n');
        var body = endsWithNewline ? text[..^1] : text;
        if (body.EndsWith('\r'))
        {
            body = body[..^1];
        }
        return body.Split('\n')
            .Select(line => line.EndsWith('\r') ? line[..^1] : line)
            .ToList();
    }
}

internal enum CodexPatchOperationKind
{
    Add,
    Delete,
    Update,
}

/// <summary>One file's operation inside a parsed patch envelope.</summary>
internal sealed class CodexPatchOperation(CodexPatchOperationKind kind, string path)
{
    public CodexPatchOperationKind Kind { get; } = kind;

    /// <summary>The path exactly as the patch wrote it — resolved and grant-checked by the policy.</summary>
    public string Path { get; } = path;

    /// <summary>1-based line of this operation's header inside the envelope, so a duplicate path can
    /// name both places rather than only the offending one.</summary>
    public int HeaderLine { get; init; }

    /// <summary>Whether the envelope this operation came from used CRLF; see
    /// <see cref="CodexApplyPatch.AddedContent"/>, its only reader.</summary>
    public bool UsesCarriageReturns { get; init; }

    public List<string> AddedLines { get; } = [];

    public List<CodexPatchChunk> Chunks { get; } = [];
}

/// <summary>
/// One hunk: the lines that must be found (<see cref="Before"/> — context plus removals) and what
/// replaces them (<see cref="After"/> — context plus additions).
/// </summary>
internal sealed class CodexPatchChunk
{
    /// <summary>
    /// The text after this hunk's <c>@@</c> marker, or null for a bare <c>@@</c> or no marker at all.
    /// A line of the file at or above the hunk: the context search starts AT that line rather than at
    /// the previous hunk's end, so a hunk whose own context begins with that line still places there.
    /// Stored trimmed and matched trimmed against the file's line, so a locator naming an indented
    /// line anchors on its text rather than on its column — see <c>CodexApplyPatch.IndexOfAnchor</c>
    /// for why widening the anchor alone cannot misplace a hunk.
    /// </summary>
    public string? Anchor { get; init; }

    public List<string> Before { get; } = [];

    public List<string> After { get; } = [];
}
