using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Baton.Cli.Daemon;

/// <summary>
/// #1557 PR-A2: <c>rooms[].live.stdoutTail</c> (spec/baton.md §6, #1710/#1723) — a faithful C# port of
/// <c>tools/fleet-glass/pusher.py</c>'s <c>_quantized_activity_iso</c> (already ported as
/// <see cref="FleetProjectionWriter"/>'s own <c>QuantizeActivity</c>, not restated here),
/// <c>_read_tail_text</c>, <c>_elide_blob_tokens</c>, <c>_render_stream_json_prose</c>,
/// <c>_render_tail_line</c>, <c>_gate_tail_lines</c>, and <c>stdout_tail_for_room</c>
/// (pusher.py:687-1085 at the plan's own reading). PR-B's byte-identical diff between this file's
/// output and the pusher's depends on every rule here matching exactly — see each method's own remarks
/// for where it does.
/// <para>
/// <b>One deliberate divergence since #2020</b>: <see cref="RenderStreamJsonProse"/> drops codex's
/// <c>turn.usage</c> line, which pusher.py has no arm for. Parity was the constraint while pusher.py
/// was the shipped reader; since #2028 serves the glass from the daemon, this file IS the shipped
/// reader and pusher.py the legacy one — so the two now agree everywhere except that arm, which its
/// own comment states. A future rule added here needs the same treatment or the parity claim above
/// silently becomes false.
/// </para>
/// </summary>
/// <remarks>
/// Deliberately NOT built on <c>WorkerStreamRendering.cs</c>/<c>RunCommand.EchoStreamJsonLine</c>: those
/// render for a human watching <c>baton run</c> live, dispatching through <c>IWorkerAdapter</c>'s own
/// per-vendor <c>TryParseProgressEvent</c>; pusher.py's tail renderer dispatches directly on the
/// envelope's own <c>type</c>/<c>event</c> key and disagrees with the C# terminal renderer in three
/// places pusher.py's own docstring names (a tool-input summary the terminal renderer never prints, a
/// claude <c>user</c>/<c>tool_result</c> arm the terminal renderer has no counterpart for at all, and
/// the DONE/ERROR agy <c>step_update</c> wording) — porting pusher.py's own logic here, rather than
/// reusing the terminal renderer, is what keeps this tail matching the Python it replaces instead of
/// matching a sibling that was never meant to agree with it byte-for-byte.
/// </remarks>
internal static class StdoutTailRenderer
{
    /// <summary>#1710: "last ~40 lines" per the issue's own design.</summary>
    internal const int StdoutTailMaxLines = 40;

    /// <summary>#1710: hard cap per room, ~4 KB.</summary>
    internal const int StdoutTailMaxBytes = 4_000;

    /// <summary>
    /// Generous headroom read from EOF — a run of unusually long lines still yields
    /// <see cref="StdoutTailMaxLines"/> candidates before the byte cap trims them. Never a whole-file
    /// read of a log that can run to megabytes.
    /// </summary>
    private const int StdoutTailReadWindowBytes = 65_536;

    private const string TruncationMark = "…";

    /// <summary>
    /// #1723: a whitespace-free token this long (base64, a data URI, a hex dump) reads as noise, never
    /// as prose.
    /// </summary>
    private const int BlobElisionThreshold = 200;

    /// <summary>
    /// #1723: one prose line stays short even when the source field (an assistant message, a
    /// tool_result body) runs long.
    /// </summary>
    private const int ProseFieldLimit = 200;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Port of pusher.py's <c>load_secret_patterns</c>: one .NET regex per line (blank lines and '#'
    /// comments skipped), <c>null</c> — the fail-closed sentinel — when the file is missing, unreadable,
    /// or any line fails to compile. An empty-but-present file returns an empty (non-null) list: a
    /// deliberate "nothing to withhold on" choice, distinct from "couldn't load the denylist at all".
    /// </summary>
    /// <remarks>
    /// Patterns are Python <c>re</c> source compiled here as .NET <see cref="Regex"/> — the two dialects
    /// agree on the plain character-class/quantifier syntax pusher.py's own denylist uses
    /// (<c>secretpatterns.example.txt</c>), but are not identical grammars in general; a denylist author
    /// relying on a Python-only construct would behave differently under this port. Not reconciled here
    /// (out of this PR's scope), and not measured to matter for any pattern this repo ships.
    /// </remarks>
    internal static IReadOnlyList<Regex>? LoadSecretPatterns(string path)
    {
        string raw;
        try
        {
            raw = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var patterns = new List<Regex>();
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            try
            {
                patterns.Add(new Regex(line));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        return patterns;
    }

    /// <summary>Port of pusher.py's <c>secret_hit_index</c>: index of the first pattern (in file order) that matches anywhere in <paramref name="text"/>, else <c>null</c>.</summary>
    private static int? SecretHitIndex(string text, IReadOnlyList<Regex> patterns)
    {
        for (var i = 0; i < patterns.Count; i++)
        {
            if (patterns[i].IsMatch(text))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// Port of pusher.py's <c>_gate_tail_lines</c>: per-LINE secret gate — a matching line becomes
    /// <c>[withheld]</c>, every other line rides through untouched, so one hit never blanks the whole
    /// tail (contrast the deliverables path's whole-content withholding). <paramref name="patterns"/>
    /// <c>null</c> (the <see cref="LoadSecretPatterns"/> fail-closed sentinel) withholds every line.
    /// </summary>
    private static List<string> GateTailLines(List<string> lines, IReadOnlyList<Regex>? patterns)
    {
        if (patterns is null)
        {
            return [.. lines.Select(_ => "[withheld]")];
        }

        return [.. lines.Select(line => SecretHitIndex(line, patterns) is not null ? "[withheld]" : line)];
    }

    /// <summary>
    /// Port of pusher.py's <c>_elide_blob_tokens</c> (#1723): replaces any whitespace-free run of at
    /// least <see cref="BlobElisionThreshold"/> characters with a byte-count marker. Applies to every
    /// surviving tail line, JSON-rendered or plain-text alike.
    ///
    /// Scans <paramref name="text"/> rune by rune (not UTF-16 code unit by code unit) so a
    /// whitespace-free run is counted the same way Python's <c>\S{200,}</c> counts it: by codepoint.
    /// A .NET <see cref="Regex"/> quantifier over <c>char</c> counts UTF-16 code UNITS, which
    /// double-counts every astral-plane character (emoji, some CJK extensions) relative to Python's
    /// codepoint-native <c>re</c> — a 150-emoji run is 150 codepoints in Python (under the 200
    /// threshold, never elided) but 300 UTF-16 units in a naive C# count (elided). Elides on a rune
    /// boundary, never splitting a surrogate pair.
    /// </summary>
    private static string ElideBlobTokens(string text)
    {
        var sb = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                sb.Append(text[index]);
                index++;
                continue;
            }

            var runStart = index;
            var runeCount = 0;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index += Rune.TryGetRuneAt(text, index, out var rune) ? rune.Utf16SequenceLength : 1;
                runeCount++;
            }

            var run = text[runStart..index];
            sb.Append(runeCount >= BlobElisionThreshold
                ? $"{TruncationMark}[{Encoding.UTF8.GetByteCount(run)} bytes elided]{TruncationMark}"
                : run);
        }

        return sb.ToString();
    }

    /// <summary>Number of Unicode codepoints (runes) in <paramref name="text"/> — Python's <c>len()</c> on a str counts codepoints, not UTF-16 code units.</summary>
    private static int CodepointLength(string text)
    {
        var count = 0;
        var index = 0;
        while (index < text.Length)
        {
            index += Rune.TryGetRuneAt(text, index, out var rune) ? rune.Utf16SequenceLength : 1;
            count++;
        }

        return count;
    }

    /// <summary>
    /// Truncates <paramref name="text"/> to its first <paramref name="limit"/> CODEPOINTS (matching
    /// Python's <c>text[:limit]</c> on a str, which slices by codepoint) plus <see cref="TruncationMark"/>
    /// — never mid-surrogate-pair, unlike a naive <c>text[..limit]</c> UTF-16 slice.
    /// </summary>
    private static string CapToCodepoints(string text, int limit)
    {
        if (CodepointLength(text) <= limit)
        {
            return text;
        }

        var count = 0;
        var index = 0;
        while (index < text.Length && count < limit)
        {
            index += Rune.TryGetRuneAt(text, index, out var rune) ? rune.Utf16SequenceLength : 1;
            count++;
        }

        return text[..index] + TruncationMark;
    }

    /// <summary>Port of pusher.py's <c>_cap_plain_line</c> (review rev1738 F3): caps to <paramref name="limit"/> codepoints.</summary>
    private static string CapPlainLine(string line, int limit = ProseFieldLimit) => CapToCodepoints(line, limit);

    /// <summary>#1793: <c>rooms[].live.doingNow</c>'s own cap — one line, no elision marker (unlike
    /// every other cap in this file, a truncated <c>doingNow</c> is cut hard rather than getting a
    /// trailing <see cref="TruncationMark"/>, per the issue's own "no elision markers" wording).</summary>
    internal const int DoingNowLimit = 140;

    /// <summary>
    /// #1793: same first-property summary <see cref="ProseSummarizeToolInput"/> renders as
    /// <c>key=value</c>, but the VALUE alone (no <c>key=</c> label) — <c>doingNow</c>'s fallback arm
    /// wants "Bash git status", not "Bash command=git status".
    /// </summary>
    private static string? FirstArgumentValue(JsonElement? toolInput)
    {
        if (toolInput is not { ValueKind: JsonValueKind.Object } obj)
        {
            return null;
        }

        foreach (var property in obj.EnumerateObject())
        {
            return property.Value.ValueKind == JsonValueKind.String
                ? (property.Value.GetString() ?? "").Replace('\n', ' ')
                : property.Value.GetRawText().Replace('\n', ' ');
        }

        return null;
    }

    /// <summary>Truncates to <paramref name="limit"/> codepoints with NO trailing marker — see <see cref="DoingNowLimit"/>'s own remark on why this differs from <see cref="CapToCodepoints"/>.</summary>
    private static string TruncatePlainNoMarker(string text, int limit)
    {
        if (CodepointLength(text) <= limit)
        {
            return text;
        }

        var count = 0;
        var index = 0;
        while (index < text.Length && count < limit)
        {
            index += Rune.TryGetRuneAt(text, index, out var rune) ? rune.Utf16SequenceLength : 1;
            count++;
        }

        return text[..index];
    }

    /// <summary>First line of <paramref name="text"/>, untruncated — the line-boundary half of <see cref="ProseFirstLine"/> without its codepoint cap, since <c>doingNow</c> caps with <see cref="TruncatePlainNoMarker"/> instead.</summary>
    private static string FirstLineOnly(string text)
    {
        var stripped = text.Trim();
        var cut = stripped.IndexOfAny(PythonLineBoundaries);
        return cut < 0 ? stripped : stripped[..cut];
    }

    /// <summary>
    /// spec/baton.md §6's <c>rooms[].live.doingNow</c> (#1793) — that entry is the canonical record of
    /// the field's shape and both derivation cases, not restated here. Scans the SAME tail read window
    /// <see cref="ComputeTail"/> reads, from the newest line backward, skipping every line whose own
    /// <c>type</c> isn't <c>assistant</c> until one is found. <c>null</c>
    /// when no `assistant` line exists in the window, or that line's content array is empty/carries no
    /// recognized block — never a fabricated reading, matching <see cref="ComputeTail"/>'s own
    /// never-fabricated convention. Deliberately independent of <see cref="RenderStreamJsonProse"/>: that
    /// function renders EVERY tail line to bracketed prose for a scrolling log; this reads only the raw
    /// shape of the single most recent relevant line. This repo's PR body has the note on why
    /// `pusher.py`'s own port (`doing_now_for_room`) is a second, independently-written implementation
    /// rather than a shared call.
    /// </summary>
    internal static string? ComputeDoingNow(string stdoutPath, IReadOnlyList<Regex>? patterns)
    {
        var line = ComputeDoingNowLine(stdoutPath);
        return line is null ? null : GateTailLines([line], patterns)[0];
    }

    private static string? ComputeDoingNowLine(string stdoutPath)
    {
        var text = ReadTailText(stdoutPath);
        if (text.Length == 0)
        {
            return null;
        }

        var lines = ReadTailLines(text, StdoutTailMaxLines);
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var raw = lines[i].Trim();
            if (raw.Length == 0)
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(raw);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("type", out var typeEl)
                    || typeEl.ValueKind != JsonValueKind.String || typeEl.GetString() != "assistant")
                {
                    continue;
                }

                if (!doc.RootElement.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                JsonElement? lastBlock = null;
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.Object)
                    {
                        lastBlock = block;
                    }
                }

                if (lastBlock is not { } finalBlock)
                {
                    return null;
                }

                var blockType = TryGetNonEmptyString(finalBlock, "type");
                if (blockType == "text" && finalBlock.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String && textEl.GetString() is { } t && t.Trim().Length > 0)
                {
                    return TruncatePlainNoMarker(FirstLineOnly(t), DoingNowLimit);
                }

                if (blockType == "tool_use" && TryGetNonEmptyString(finalBlock, "name") is { } name)
                {
                    var input = finalBlock.TryGetProperty("input", out var inputEl) ? inputEl : (JsonElement?)null;
                    var description = input is { ValueKind: JsonValueKind.Object } inputObj
                        && inputObj.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String
                        ? descEl.GetString()
                        : null;

                    if (description is { Length: > 0 })
                    {
                        return TruncatePlainNoMarker(FirstLineOnly(description), DoingNowLimit);
                    }

                    var mainArgument = FirstArgumentValue(input);
                    var line = mainArgument is { Length: > 0 }
                        ? $"{name} {TruncatePlainNoMarker(FirstLineOnly(mainArgument), 80)}"
                        : name;
                    return TruncatePlainNoMarker(line, DoingNowLimit);
                }

                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Port of pusher.py's splitlines-based line scanner, shared by <see cref="ProseFirstLine"/> (first
    /// element) and <see cref="ReadTailLines"/> (last N elements). Splits on '\n' alone: Python's
    /// <c>str.splitlines()</c> recognizes a wider boundary set (bare '\r', vertical tab, form feed, and a
    /// few rarer separators), but <c>ExecutionStreamLogger</c> writes each execution's raw captured
    /// bytes verbatim and both vendor CLIs emit '\n'-delimited stream-json/JSONL regardless of host OS,
    /// so '\n' is the only boundary this content actually produces — mirroring
    /// <see cref="FleetProjectionWriter"/>'s own <c>ReadNewLines</c> convention for the identical data
    /// source rather than porting separators this input never contains. Drops the trailing empty
    /// element a plain <see cref="string.Split(char)"/> produces when the text ends exactly on a
    /// boundary, matching Python's <c>splitlines()</c> there.
    /// </summary>
    private static List<string> SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var parts = text.Split('\n');
        return parts[^1].Length == 0 ? [.. parts[..^1]] : [.. parts];
    }

    /// <summary>
    /// Port of pusher.py's <c>_prose_first_line</c>: first line of <paramref name="text"/> (a
    /// multi-line assistant message or tool result renders as ONE prose line), truncated to
    /// <paramref name="limit"/> chars.
    /// </summary>
    private static string ProseFirstLine(string text, int limit = ProseFieldLimit)
    {
        // This input is a parsed JSON string field, not the '\n'-only raw log SplitLines is scoped to:
        // a tool_result echoing a Windows subprocess can carry CRLF. Python's str.splitlines() treats
        // every universal-newline boundary as one break, so the first line ends at the first such char
        // (Trim() has already removed any leading ones).
        var stripped = text.Trim();
        var cut = stripped.IndexOfAny(PythonLineBoundaries);
        var first = cut < 0 ? stripped : stripped[..cut];
        return CapToCodepoints(first, limit);
    }

    /// <summary>The boundary set of Python's <c>str.splitlines()</c>.</summary>
    private static readonly char[] PythonLineBoundaries =
        ['\n', '\r', '\v', '\f', '\x1c', '\x1d', '\x1e', '\x85', '\u2028', '\u2029'];

    /// <summary>
    /// Port of pusher.py's <c>_prose_summarize_tool_input</c>: a <c>key=value, ...</c> one-liner off a
    /// <c>tool_use</c> block's <c>input</c> object, truncated. A string value is used raw; anything
    /// else is JSON-encoded (compact, no property-name reordering — <see cref="JsonElement"/>
    /// enumeration preserves source order) with embedded newlines flattened to spaces.
    /// </summary>
    private static string ProseSummarizeToolInput(JsonElement? toolInput, int limit = 120)
    {
        if (toolInput is not { ValueKind: JsonValueKind.Object } obj)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var property in obj.EnumerateObject())
        {
            var rendered = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? ""
                : property.Value.GetRawText();
            parts.Add($"{property.Name}={rendered.Replace('\n', ' ')}");
        }

        if (parts.Count == 0)
        {
            return "";
        }

        var summary = string.Join(", ", parts);
        return CapToCodepoints(summary, limit);
    }

    /// <summary>Python truthiness for a JSON-decoded value — used only where pusher.py itself tests truthiness rather than <c>isinstance(x, bool)</c> (the claude <c>tool_result</c> block's <c>is_error</c>).</summary>
    private static bool IsTruthy(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => false,
        JsonValueKind.Number => element.TryGetDouble(out var d) && d != 0,
        JsonValueKind.String => element.GetString() is { Length: > 0 },
        JsonValueKind.Array => element.GetArrayLength() > 0,
        JsonValueKind.Object => element.EnumerateObject().Any(),
        _ => false,
    };

    private enum ProseKind
    {
        Rendered,
        Noise,
        Unrecognized,
    }

    private readonly record struct ProseResult(ProseKind Kind, string? Text)
    {
        internal static readonly ProseResult Noise = new(ProseKind.Noise, null);
        internal static readonly ProseResult Unrecognized = new(ProseKind.Unrecognized, null);
        internal static ProseResult Rendered(string text) => new(ProseKind.Rendered, text);
    }

    /// <summary>A string field's raw value regardless of emptiness, or <c>null</c> if absent/not a string — Python's bare <c>isinstance(x, str)</c>, with no truthiness test at all.</summary>
    private static string? TryGetString(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Python's <c>isinstance(x, str) and x</c> — a non-empty string is truthy regardless of whether it is all whitespace.</summary>
    private static string? TryGetNonEmptyString(JsonElement obj, string property) =>
        TryGetString(obj, property) is { Length: > 0 } s ? s : null;

    /// <summary>
    /// Python's <c>isinstance(x, str) and x.strip()</c> — the RAW (unstripped) string is truthy only
    /// when it has non-whitespace content, distinct from <see cref="TryGetNonEmptyString"/>'s plain
    /// non-empty check: a whitespace-only string is truthy under the plain check but not this one.
    /// </summary>
    private static string? TryGetStrippedNonEmptyString(JsonElement obj, string property)
    {
        var s = TryGetString(obj, property);
        return s is not null && s.Trim().Length > 0 ? s : null;
    }

    /// <summary>
    /// Port of pusher.py's <c>_render_stream_json_prose</c>: one prose line for a parsed stream-json
    /// object, <see cref="ProseKind.Noise"/> if the shape IS recognized but #1723 judged it
    /// deliberately silent, or <see cref="ProseKind.Unrecognized"/> if the envelope's own top-level
    /// <c>type</c>/<c>event</c> value has no arm here at all (in which case <see cref="RenderTailLine"/>
    /// echoes the raw line rather than dropping it). This only covers the TOP-LEVEL dispatch — a
    /// recognized <c>type</c>/<c>event</c> whose nested sub-shape doesn't match any arm still falls
    /// through to that branch's own Noise, not Unrecognized, mirroring pusher.py's own narrower,
    /// pre-existing fail-silent gap (not closed by this port either).
    /// </summary>
    private static ProseResult RenderStreamJsonProse(JsonElement evt)
    {
        if (evt.TryGetProperty("type", out var typeEl))
        {
            var evtType = typeEl.ValueKind == JsonValueKind.String ? typeEl.GetString() : null;

            if (evtType == "assistant")
            {
                if (!evt.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    return ProseResult.Noise;
                }

                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var blockType = TryGetNonEmptyString(block, "type");
                    if (blockType == "text")
                    {
                        if (block.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                            && textEl.GetString() is { } text && text.Trim().Length > 0)
                        {
                            return ProseResult.Rendered(ProseFirstLine(text));
                        }
                    }
                    else if (blockType == "tool_use")
                    {
                        if (TryGetNonEmptyString(block, "name") is { } name)
                        {
                            var input = block.TryGetProperty("input", out var inputEl) ? inputEl : (JsonElement?)null;
                            var summary = ProseSummarizeToolInput(input);
                            return ProseResult.Rendered(summary.Length > 0 ? $"[tool: {name}({summary})]" : $"[tool: {name}]");
                        }
                    }
                }

                return ProseResult.Noise;
            }

            if (evtType == "user")
            {
                if (!evt.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    return ProseResult.Noise;
                }

                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object || TryGetNonEmptyString(block, "type") != "tool_result")
                    {
                        continue;
                    }

                    string? body = null;
                    if (block.TryGetProperty("content", out var bodyEl))
                    {
                        if (bodyEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var c in bodyEl.EnumerateArray())
                            {
                                if (c.ValueKind == JsonValueKind.Object
                                    && c.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                                {
                                    body = t.GetString();
                                    break;
                                }
                            }
                        }
                        else if (bodyEl.ValueKind == JsonValueKind.String)
                        {
                            body = bodyEl.GetString();
                        }
                    }

                    if (body is { Length: > 0 } && body.Trim().Length > 0)
                    {
                        var isError = block.TryGetProperty("is_error", out var ie) && IsTruthy(ie);
                        var prefix = isError ? "[tool_result error: " : "[tool_result: ";
                        return ProseResult.Rendered(prefix + ProseFirstLine(body) + "]");
                    }
                }

                return ProseResult.Noise;
            }

            if (evtType == "result")
            {
                if (!evt.TryGetProperty("is_error", out var isErrorEl)
                    || (isErrorEl.ValueKind != JsonValueKind.True && isErrorEl.ValueKind != JsonValueKind.False))
                {
                    return ProseResult.Noise;
                }

                if (isErrorEl.ValueKind == JsonValueKind.True)
                {
                    var text = TryGetNonEmptyString(evt, "result") ?? "no error detail in the result envelope";
                    return ProseResult.Rendered($"[result: error — {ProseFirstLine(text)}]");
                }

                return ProseResult.Rendered("[result: success]");
            }

            // #2020 review LOW: codex's per-model-round-trip usage line is accounting an operator
            // cannot act on, and one lands per round-trip -- left raw it crowds a 40-line window on a
            // long lane. Dropped as Noise, the same arm every other recognized-but-silent shape takes.
            // Deliberately NOT the whole vendor: codex's other lines still pass through raw, so an
            // agent message stays visible. This is the file's one divergence from pusher.py (the class
            // remark names it), which has no codex arm at all.
            if (evtType == Baton.Status.CodexUsageParser.TurnUsageEventType)
            {
                return ProseResult.Noise;
            }

            if (evtType == "system")
            {
                var subtype = TryGetNonEmptyString(evt, "subtype");
                if (subtype == "init")
                {
                    return ProseResult.Rendered("[status: Session started]");
                }

                if (subtype == "status" && TryGetNonEmptyString(evt, "status") is { } status)
                {
                    return ProseResult.Rendered($"[status: {status}]");
                }

                return ProseResult.Noise;
            }

            return ProseResult.Unrecognized;
        }

        if (evt.TryGetProperty("event", out var eventEl))
        {
            var eventType = eventEl.ValueKind == JsonValueKind.String ? eventEl.GetString() : null;

            if (eventType == "init")
            {
                return ProseResult.Rendered("[status: Session started]");
            }

            if (eventType == "step_update")
            {
                if (!evt.TryGetProperty("step_update", out var step) || step.ValueKind != JsonValueKind.Object)
                {
                    return ProseResult.Noise;
                }

                var state = TryGetNonEmptyString(step, "state");
                var stepType = TryGetString(step, "step_type");

                if ((state == "DONE" || state == "ERROR") && stepType is not null
                    && stepType is not ("unknown" or "checkpoint" or "user_input"))
                {
                    var marker = state == "DONE" ? "done" : "error";
                    return ProseResult.Rendered($"[tool: {stepType} — {marker}]");
                }

                return ProseResult.Noise;
            }

            if (eventType == "result")
            {
                if (!evt.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                {
                    return ProseResult.Noise;
                }

                if (TryGetStrippedNonEmptyString(result, "response") is { } response)
                {
                    return ProseResult.Rendered(ProseFirstLine(response));
                }

                var status = TryGetNonEmptyString(result, "status");
                if (status is not null && status != "SUCCESS")
                {
                    var error = TryGetNonEmptyString(result, "error") ?? "no error detail in the result envelope";
                    return ProseResult.Rendered($"[result: error — {ProseFirstLine(error)}]");
                }

                return ProseResult.Noise;
            }

            return ProseResult.Unrecognized;
        }

        return ProseResult.Unrecognized;
    }

    /// <summary>
    /// Port of pusher.py's <c>_render_tail_line</c>: a line that parses as a JSON object routes through
    /// <see cref="RenderStreamJsonProse"/>; anything else — malformed JSON, valid JSON that is not an
    /// object, or an object whose <c>type</c>/<c>event</c> is unrecognized — passes through unchanged.
    /// Returns <c>null</c> when the line should be dropped entirely.
    /// </summary>
    private static string? RenderTailLine(string rawLine)
    {
        var stripped = rawLine.Trim();
        if (stripped.Length == 0)
        {
            return rawLine;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stripped);
        }
        catch (JsonException)
        {
            return rawLine;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return rawLine;
            }

            var result = RenderStreamJsonProse(doc.RootElement);
            return result.Kind switch
            {
                ProseKind.Unrecognized => rawLine,
                ProseKind.Noise => null,
                _ => result.Text,
            };
        }
    }

    /// <summary>
    /// Strict UTF-8 decode, falling back to lossy replacement only for bytes that are genuinely invalid
    /// — port of pusher.py's <c>_decode_utf8_boundary_safe</c>. Callers are expected to have already
    /// trimmed to a real line boundary.
    /// </summary>
    private static string DecodeUtf8BoundarySafe(byte[] data)
    {
        try
        {
            return StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.UTF8.GetString(data);
        }
    }

    /// <summary>
    /// Port of pusher.py's <c>_read_tail_text</c>: last <paramref name="windowBytes"/> bytes of
    /// <paramref name="path"/>, decoded, with a possibly-torn leading line dropped (the seek landed
    /// mid-line unless it started at byte 0). Bounds the read against a multi-megabyte log.
    /// </summary>
    private static string ReadTailText(string path, int windowBytes = StdoutTailReadWindowBytes)
    {
        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }

        var start = Math.Max(0, size - windowBytes);
        byte[] chunk;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(start, SeekOrigin.Begin);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            chunk = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }

        if (start > 0)
        {
            var nl = Array.IndexOf(chunk, (byte)'\n');
            chunk = nl != -1 ? chunk[(nl + 1)..] : [];
        }

        return chunk.Length > 0 ? DecodeUtf8BoundarySafe(chunk) : "";
    }

    private static List<string> ReadTailLines(string text, int maxLines)
    {
        var lines = SplitLines(text);
        return lines.Count <= maxLines ? lines : lines[^maxLines..];
    }

    /// <summary>
    /// Port of pusher.py's <c>stdout_tail_for_room</c>, minus its own path resolution: the caller passes
    /// the already-resolved <paramref name="stdoutPath"/> (<see cref="FleetProjectionWriter"/>'s own
    /// <c>FindStdoutPaths</c> — the SAME file-discovery <c>live</c>'s other fields already use, never a
    /// second way of finding it). The last <paramref name="maxLines"/> RAW lines of
    /// <paramref name="stdoutPath"/>, each rendered to prose, blob-elided, then secret-gated per
    /// surviving line, hard-capped at <paramref name="maxBytes"/> by truncating from the FRONT (the
    /// newest lines are what a live tail is for) on a line boundary — never mid-character. <c>null</c>
    /// when there is no captured stdout yet, matching pusher.py's own never-fabricated convention.
    /// </summary>
    internal static string? ComputeTail(
        string stdoutPath,
        IReadOnlyList<Regex>? patterns,
        int maxLines = StdoutTailMaxLines,
        int maxBytes = StdoutTailMaxBytes)
    {
        var text = ReadTailText(stdoutPath);
        if (text.Length == 0)
        {
            return null;
        }

        var rawLines = ReadTailLines(text, maxLines);
        var rendered = new List<string>();
        foreach (var raw in rawLines)
        {
            var line = RenderTailLine(raw);
            if (line is not null)
            {
                // #1723: elide FIRST, off the full (possibly long) rendered/raw line, so a blob's
                // reported byte count is the real one; THEN cap (review rev1738 F3) so no single
                // surviving line can alone exceed the max_bytes budget below and get dropped whole by
                // the forward boundary search.
                rendered.Add(CapPlainLine(ElideBlobTokens(line)));
            }
        }

        var gated = GateTailLines(rendered, patterns);
        var tail = string.Join('\n', gated);
        if (tail.Length == 0)
        {
            return null;
        }

        var encoded = Encoding.UTF8.GetBytes(tail);
        if (encoded.Length > maxBytes)
        {
            // Reserve the marker's own bytes out of the budget UP FRONT so the final, marker-prefixed
            // tail never exceeds max_bytes.
            var markerBytes = Encoding.UTF8.GetBytes(TruncationMark);
            var contentBudget = Math.Max(0, maxBytes - markerBytes.Length);
            var cutStart = encoded.Length - contentBudget;
            var nlIndex = Array.IndexOf(encoded, (byte)'\n', Math.Max(0, cutStart));
            byte[] bodyBytes;
            if (nlIndex != -1)
            {
                bodyBytes = encoded[(nlIndex + 1)..];
            }
            else
            {
                // No line boundary inside the budget at all -- rather than drop every surviving line,
                // keep the newest line alone (review rev1738 F3), trimmed from the end on a UTF-8
                // lead-byte boundary to keep the hard max_bytes contract.
                bodyBytes = gated.Count > 0 ? Encoding.UTF8.GetBytes(gated[^1]) : [];
                if (bodyBytes.Length > contentBudget)
                {
                    bodyBytes = bodyBytes[(bodyBytes.Length - contentBudget)..];
                    var skip = 0;
                    while (skip < bodyBytes.Length && (bodyBytes[skip] & 0xC0) == 0x80)
                    {
                        skip++;
                    }

                    bodyBytes = bodyBytes[skip..];
                }
            }

            tail = TruncationMark + DecodeUtf8BoundarySafe(bodyBytes);
        }

        return tail;
    }
}
