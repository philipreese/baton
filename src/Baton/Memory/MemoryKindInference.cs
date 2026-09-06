namespace Baton.Memory;

/// <summary>
/// How an imported file's <see cref="MemoryKind"/> is decided: a declaration in the file's own
/// front-matter first, a pinned filename-prefix table second, and <see cref="MemoryKind.Unknown"/>
/// when neither answers. <b>Never the body text</b> — see <see cref="MemoryKind"/>'s remarks for why
/// that ordering is the whole point rather than an implementation detail.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two declaration spellings are accepted, and neither is a measured vendor guarantee.</b> Claude
/// Code's own memory files carry a YAML front-matter block whose vocabulary is
/// <c>metadata.type: user|feedback|project|reference</c>; a <c>kind:</c> key is what Baton's own
/// writers will declare (spec/baton.md §12's five kinds). Both are read, because reading only one of
/// them would leave the <see cref="MemoryKindSource.Declared"/> branch dead on one of the two
/// populations this import walks. That the front-matter is there at all is an observation about files
/// on a machine, not a format either vendor publishes — which is exactly why its absence degrades to
/// the prefix table below instead of failing the file.
/// </para>
/// <para>
/// <b>The prefix table is pinned and incomplete on purpose.</b> Four prefixes were observed across
/// #1852's survey population; nothing in that vocabulary corresponds to
/// <see cref="MemoryKind.Hypothesis"/> or <see cref="MemoryKind.ExecutionDerivedSummary"/>, and a
/// mapping invented to fill those two rows would be a claim about the memories rather than about
/// their names. A prefix outside the table yields <see cref="MemoryKind.Unknown"/> — recorded as an
/// absence, never rounded to the nearest kind.
/// </para>
/// </remarks>
public static class MemoryKindInference
{
    /// <summary>Lines of a front-matter block scanned before giving up. A declaration sits at the top or nowhere.</summary>
    public const int MaxFrontMatterLines = 32;

    /// <summary>
    /// Leading filename token to the kind it names. Whole tokens, case-insensitively, split on the
    /// same separators the observed names use. <c>user_</c> is a durable fact about the operator
    /// rather than a preference of theirs; <c>feedback_</c> is the one that is a preference.
    /// </summary>
    public static IReadOnlyDictionary<string, MemoryKind> KindByFilenamePrefix { get; } =
        new Dictionary<string, MemoryKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["feedback"] = MemoryKind.OperatorPreference,
            ["user"] = MemoryKind.DurableFact,
            ["project"] = MemoryKind.DurableFact,
            ["reference"] = MemoryKind.DurableFact,
        };

    /// <summary>
    /// Declaration token (either spelling) to the kind it names. The four Claude-vocabulary tokens map
    /// exactly as <see cref="KindByFilenamePrefix"/> does — one vocabulary, two places it can be
    /// written — and the five canonical kind slugs are accepted verbatim so a Baton-written file
    /// declares its kind in the store's own words.
    /// </summary>
    public static IReadOnlyDictionary<string, MemoryKind> KindByDeclarationToken { get; } =
        new Dictionary<string, MemoryKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["feedback"] = MemoryKind.OperatorPreference,
            ["user"] = MemoryKind.DurableFact,
            ["project"] = MemoryKind.DurableFact,
            ["reference"] = MemoryKind.DurableFact,
            ["durable-fact"] = MemoryKind.DurableFact,
            ["operator-preference"] = MemoryKind.OperatorPreference,
            ["hypothesis"] = MemoryKind.Hypothesis,
            ["historical-note"] = MemoryKind.HistoricalNote,
            ["execution-derived-summary"] = MemoryKind.ExecutionDerivedSummary,
        };

    /// <summary>
    /// The kind <paramref name="text"/> declares and <paramref name="fileName"/> suggests, with the
    /// source of the answer. Ordered: declaration, then prefix, then unknown.
    /// </summary>
    /// <param name="fileName">The source file's name, not its path.</param>
    /// <param name="text">The source file's whole text.</param>
    public static (MemoryKind Kind, MemoryKindSource Source) Infer(string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(text);

        if (TryReadDeclaredKind(text) is { } declared)
        {
            return (declared, MemoryKindSource.Declared);
        }

        var prefix = fileName.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return prefix is { Length: > 0 } && KindByFilenamePrefix.TryGetValue(prefix, out var inferred)
            ? (inferred, MemoryKindSource.InferredFromPrefix)
            : (MemoryKind.Unknown, MemoryKindSource.Unknown);
    }

    /// <summary>
    /// The kind a leading <c>---</c> front-matter block declares, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// A deliberately minimal reader rather than a YAML parser: it recognises a top-level
    /// <c>kind:</c> and a <c>type:</c> nested under <c>metadata:</c>, and understands nothing else. A
    /// front-matter block this does not recognise is not an error — it degrades to the prefix table,
    /// which is the same outcome a file with no block at all gets. Taking a YAML dependency to read
    /// one scalar out of a format neither vendor guarantees would be the larger claim.
    /// </remarks>
    private static MemoryKind? TryReadDeclaredKind(string text)
    {
        using var reader = new StringReader(text);
        if (reader.ReadLine()?.Trim() is not "---")
        {
            return null;
        }

        var inMetadata = false;
        for (var scanned = 0; scanned < MaxFrontMatterLines; scanned++)
        {
            var line = reader.ReadLine();
            if (line is null || line.Trim() == "---")
            {
                return null;
            }

            var indented = line.Length > 0 && char.IsWhiteSpace(line[0]);
            var trimmed = line.Trim();

            if (!indented)
            {
                inMetadata = trimmed.StartsWith("metadata:", StringComparison.OrdinalIgnoreCase);
            }

            var isKindLine = (!indented && trimmed.StartsWith("kind:", StringComparison.OrdinalIgnoreCase))
                || (indented && inMetadata && trimmed.StartsWith("type:", StringComparison.OrdinalIgnoreCase));
            if (!isKindLine)
            {
                continue;
            }

            var value = trimmed[(trimmed.IndexOf(':') + 1)..].Trim().Trim('"', '\'');
            if (KindByDeclarationToken.TryGetValue(value, out var kind))
            {
                return kind;
            }
        }

        return null;
    }
}
