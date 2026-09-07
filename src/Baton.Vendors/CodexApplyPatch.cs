namespace Baton.Vendors;

/// <summary>
/// Codex's native edit envelope (<c>*** Begin Patch</c> … <c>*** End Patch</c>), parsed into the file
/// operations <see cref="CodexDynamicToolPolicy"/> then applies through its own granted write path
/// (#1996). Parsing is separated from the policy so the format has one home and the grant has another:
/// nothing here resolves a path, opens a file, or knows what a workspace root is.
/// <para>
/// <b>Deliberately narrow.</b> Context matches are exact — no fuzzy match, no whitespace-tolerant
/// fallback, no positional guessing from a <c>@@</c> marker. A patch this parser cannot place is a
/// <see cref="ArgumentException"/> naming the line it could not find, which is a step the model
/// recovers from; a hunk applied at the wrong offset is a corrupted file that reports success. The
/// accepted subset is restated once for the model in <c>CodexDynamicToolPolicy</c>'s tool description,
/// which is its only spec for the format.
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
                current = New(CodexPatchOperationKind.Add, line[AddPrefix.Length..], operations);
                continue;
            }
            if (line.StartsWith(DeletePrefix, StringComparison.Ordinal))
            {
                current = New(CodexPatchOperationKind.Delete, line[DeletePrefix.Length..], operations);
                continue;
            }
            if (line.StartsWith(UpdatePrefix, StringComparison.Ordinal))
            {
                current = New(CodexPatchOperationKind.Update, line[UpdatePrefix.Length..], operations);
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
        foreach (var operation in operations)
        {
            Validate(operation);
        }
        return operations;
    }

    /// <summary>
    /// <paramref name="original"/> with every chunk applied in order, each placed at the first exact
    /// occurrence of its context at or after the previous chunk's end. The file's own line ending and
    /// its trailing-newline state are preserved, so a CRLF file stays CRLF.
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
            var at = IndexOfContext(lines, chunk.Before, cursor);
            if (at < 0)
            {
                throw new ArgumentException(
                    $"Patch context not found in '{operation.Path}': no line matches "
                    + $"'{chunk.Before[0]}' where the patch expects it. Context must match the file "
                    + "exactly; re-read the file and patch again, or replace it whole with "
                    + $"{CodexDynamicToolPolicy.WriteTextTool}.");
            }
            result.AddRange(lines.Skip(cursor).Take(at - cursor));
            result.AddRange(chunk.After);
            cursor = at + chunk.Before.Count;
        }
        result.AddRange(lines.Skip(cursor));
        return string.Join(newline, result) + (endsWithNewline ? newline : string.Empty);
    }

    /// <summary>The exact content an <c>*** Add File:</c> operation creates.</summary>
    public static string AddedContent(CodexPatchOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        // A file that did not exist has no line ending to preserve, so it takes the platform's — the
        // same one every other tool on the worker's machine writes.
        return string.Join(Environment.NewLine, operation.AddedLines) + Environment.NewLine;
    }

    private static CodexPatchOperation New(
        CodexPatchOperationKind kind, string path, List<CodexPatchOperation> operations)
    {
        var trimmed = path.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException($"A '{kind}' patch header names no file.");
        }
        var operation = new CodexPatchOperation(kind, trimmed);
        operations.Add(operation);
        return operation;
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
            // A navigation hint for a human reader. Baton places every chunk by exact context, so the
            // marker starts a new chunk and contributes nothing to the match.
            operation.Chunks.Add(new CodexPatchChunk());
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
                    // A pure-addition chunk names no place in the file, and this parser will not guess
                    // one from the @@ marker: the wrong offset is a silent corruption.
                    throw new ArgumentException(
                        $"A hunk of '{operation.Path}' has no context or removed lines, so Baton "
                        + "cannot tell where it goes. Include at least one unchanged context line.");
                }
                return;
            default:
                return;
        }
    }

    private static int IndexOfContext(IReadOnlyList<string> lines, IReadOnlyList<string> before, int from)
    {
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
                return start;
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

    public List<string> AddedLines { get; } = [];

    public List<CodexPatchChunk> Chunks { get; } = [];
}

/// <summary>
/// One hunk: the lines that must be found (<see cref="Before"/> — context plus removals) and what
/// replaces them (<see cref="After"/> — context plus additions).
/// </summary>
internal sealed class CodexPatchChunk
{
    public List<string> Before { get; } = [];

    public List<string> After { get; } = [];
}
