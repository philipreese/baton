using System.Text.RegularExpressions;
using Baton.Cli.Daemon;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #1557 PR-A2: golden-value pins for <see cref="StdoutTailRenderer.ComputeTail"/> against
/// <c>tools/fleet-glass/pusher.py</c>'s <c>stdout_tail_for_room</c> — the Python function this ports.
/// The expected strings below were produced by running the REAL pusher.py against these exact fixture
/// lines (written to a temp room's <c>artifacts/execution_&lt;id&gt;/.stdout.log</c>) and calling
/// <c>pusher.stdout_tail_for_room(room, execution_id, patterns)</c> once, interactively, via:
///
/// <code>
/// python -c "
/// import sys, os, tempfile, json
/// sys.path.insert(0, 'tools/fleet-glass')
/// import pusher
/// root = tempfile.mkdtemp()
/// d = os.path.join(root, 'artifacts', 'execution_x')
/// os.makedirs(d)
/// open(os.path.join(d, '.stdout.log'), 'w', encoding='utf-8', newline='\n').write('\n'.join(LINES) + '\n')
/// print(json.dumps(pusher.stdout_tail_for_room(root, 'x', PATTERNS)))
/// "
/// </code>
///
/// with <c>LINES</c>/<c>PATTERNS</c> substituted per fixture below. The C# assertions were then pinned
/// from that command's stdout, not derived from reading the Python source a second time — the two
/// implementations were never cross-checked against each other's logic, only against the same recorded
/// output. Line fixtures reused from <c>RunCommandEchoTests.cs</c> (#1559's own captured stream-json
/// shapes), plus one secret-shaped and one blob-shaped line #1559 has no counterpart for.
/// </summary>
public sealed class StdoutTailRendererTests : IDisposable
{
    private readonly string _tempDir;

    public StdoutTailRendererTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"stdout-tail-renderer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            DirectoryCleanup.DeleteRecursively(_tempDir);
        }
    }

    private string WriteLog(string fileName, IEnumerable<string> lines)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        return path;
    }

    [Fact]
    public void ComputeTail_RendersClaudeStreamJsonLines_MatchingPusherPyOutput()
    {
        var path = WriteLog("claude.stdout.log", [
            """{"type":"system","subtype":"init","session_id":"s-123","tools":["Bash"]}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Drafting the plan now."}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"git status"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"abc","content":"ls output"}]}}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"all done"}""",
        ]);

        var tail = StdoutTailRenderer.ComputeTail(path, patterns: []);

        Assert.Equal(
            "[status: Session started]\n"
            + "Drafting the plan now.\n"
            + "[tool: Bash(command=git status)]\n"
            + "[tool_result: ls output]\n"
            + "[result: success]",
            tail);
    }

    [Fact]
    public void ComputeTail_RendersAgyStreamJsonLines_MatchingPusherPyOutput()
    {
        var path = WriteLog("agy.stdout.log", [
            """{"event":"init"}""",
            """{"event":"step_update","step_update":{"state":"DONE","step_type":"tool"}}""",
            """{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool"}}""",
            """{"event":"result","result":{"conversation_id":"eca57a30","status":"ERROR","response":"","error":"Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 1h39m10s."}}""",
        ]);

        var tail = StdoutTailRenderer.ComputeTail(path, patterns: []);

        Assert.Equal(
            "[status: Session started]\n"
            + "[tool: tool — done]\n"
            + "[result: error — Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 1h39m10s.]",
            tail);
    }

    /// <summary>
    /// #2020 review LOW, and a deliberate DIVERGENCE from pusher.py rather than a port of it (the class
    /// remark names why the C# renderer is now the shipped reader): the broker writes one
    /// <c>turn.usage</c> line per model round-trip, which is accounting, not anything an operator
    /// watching the glass can act on. It is dropped the way every other recognized-but-silent shape is
    /// — <see cref="StdoutTailRenderer"/>'s Noise arm — so a long codex lane does not spend its 40-line
    /// window on raw usage JSON. The codex agent message beside it must still render, which is what
    /// stops the arm from being a blanket codex filter.
    /// </summary>
    [Fact]
    public void ComputeTail_DropsCodexTurnUsageLines_AndKeepsTheRestOfTheStream()
    {
        var path = WriteLog("codex.stdout.log", [
            """{"type":"thread.started","thread_id":"thread-1"}""",
            """{"type":"turn.usage","usage":{"input_tokens":40000,"cached_input_tokens":38000,"output_tokens":300,"round_trip":1}}""",
            """{"type":"item.completed","item":{"id":"item-1","type":"agent_message","text":"done"}}""",
            """{"type":"turn.usage","usage":{"input_tokens":90000,"cached_input_tokens":88000,"output_tokens":450,"round_trip":2}}""",
        ]);

        var tail = StdoutTailRenderer.ComputeTail(path, patterns: []);

        Assert.DoesNotContain("turn.usage", tail);
        Assert.DoesNotContain("round_trip", tail);
        Assert.Contains("thread.started", tail);
        Assert.Contains("agent_message", tail);
    }

    /// <summary>
    /// One plain line carrying a credential-shaped token, one plain line carrying a 250-char
    /// whitespace-free blob, and one JSON tool_use line whose rendered summary embeds the same
    /// credential-shaped token — pins that the secret gate runs AFTER rendering (spec/baton.md §6) and
    /// that blob elision and the secret gate are independent passes.
    /// </summary>
    [Fact]
    public void ComputeTail_GatesSecretShapedLines_AndElidesBlobTokens_MatchingPusherPyOutput()
    {
        var blob = new string('Q', 250);
        var path = WriteLog("secret.stdout.log", [
            "Authorization: Bearer sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
            "payload: " + blob,
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"export KEY=sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"}}]}}""",
        ]);
        var patterns = new[] { new Regex("sk-[A-Za-z0-9]{20,}") };

        var tail = StdoutTailRenderer.ComputeTail(path, patterns);

        Assert.Equal(
            "[withheld]\n"
            + "payload: …[250 bytes elided]…\n"
            + "[withheld]",
            tail);
    }

    /// <summary>
    /// A tool_result body carrying CRLF (a Windows subprocess echoed back through the vendor): Python's
    /// <c>str.splitlines()</c> treats "\r\n" as ONE boundary, so the first prose line is "line1" with no
    /// stray '\r'. Second-reader review finding on PR-A2.
    /// </summary>
    [Fact]
    public void ComputeTail_FirstProseLine_StopsAtCarriageReturn_LikePythonSplitlines()
    {
        var path = WriteLog("crlf.stdout.log", [
            """{"type":"assistant","message":{"content":[{"type":"text","text":"first\r\nsecond"}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"abc","content":"dir out\r\nmore"}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"lone\rcr"}]}}""",
        ]);

        var tail = StdoutTailRenderer.ComputeTail(path, patterns: []);

        Assert.Equal("first\n[tool_result: dir out]\nlone", tail);
        Assert.DoesNotContain('\r', tail!);
    }

    /// <summary>
    /// Pins the blob elider's codepoint-vs-UTF-16-unit distinction
    /// (see that method's doc comment) against a real pusher.py run. Second-reader review finding.
    /// </summary>
    [Fact]
    public void ComputeTail_CountsBlobThresholdByCodepoint_NotByUtf16Unit()
    {
        var emojiRun = string.Concat(Enumerable.Repeat("😀", 150));
        var path = WriteLog("emoji.stdout.log", [emojiRun]);

        var tail = StdoutTailRenderer.ComputeTail(path, patterns: []);

        Assert.Equal(emojiRun, tail);
    }

    /// <summary>
    /// Agy's <c>step_update</c> arm requires only <c>isinstance(step_type, str)</c> in pusher.py — an
    /// empty string is a valid (if odd) step_type and still renders, it is not treated as absent.
    /// Second-reader review finding, pinned against a real pusher.py run.
    /// </summary>
    [Fact]
    public void ComputeTail_RendersAgyStepUpdate_WithEmptyStepType()
    {
        var path = WriteLog("step-empty.stdout.log", [
            """{"event":"step_update","step_update":{"state":"DONE","step_type":""}}""",
        ]);

        var tail = StdoutTailRenderer.ComputeTail(path, patterns: []);

        Assert.Equal("[tool:  — done]", tail);
    }

    /// <summary>
    /// Agy's <c>result</c> arm gates <c>response</c> on Python's <c>.strip()</c>-truthiness, not plain
    /// non-emptiness — a whitespace-only response must fall through to the status/error branch instead
    /// of rendering as an empty line and swallowing a real error. Second-reader review finding, pinned
    /// against a real pusher.py run.
    /// </summary>
    [Fact]
    public void ComputeTail_FallsThroughToError_WhenAgyResponseIsWhitespaceOnly()
    {
        var path = WriteLog("resp-whitespace.stdout.log", [
            """{"event":"result","result":{"response":" ","status":"FAILED","error":"boom"}}""",
        ]);

        var tail = StdoutTailRenderer.ComputeTail(path, patterns: []);

        Assert.Equal("[result: error — boom]", tail);
    }

    /// <summary>Fail-closed (spec/baton.md §6): a <c>null</c> patterns list — the
    /// <see cref="StdoutTailRenderer.LoadSecretPatterns"/> missing/unreadable-file sentinel — withholds
    /// EVERY line, not just the ones a present denylist would have caught.</summary>
    [Fact]
    public void ComputeTail_WithheldsEveryLine_WhenPatternsAreNull()
    {
        var blob = new string('Q', 250);
        var path = WriteLog("secret-failclosed.stdout.log", [
            "Authorization: Bearer sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
            "payload: " + blob,
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"export KEY=sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"}}]}}""",
        ]);

        var tail = StdoutTailRenderer.ComputeTail(path, patterns: null);

        Assert.Equal("[withheld]\n[withheld]\n[withheld]", tail);
    }

    [Fact]
    public void LoadSecretPatterns_ReturnsNull_WhenFileMissing()
    {
        var missingPath = Path.Combine(_tempDir, "does-not-exist.txt");

        Assert.Null(StdoutTailRenderer.LoadSecretPatterns(missingPath));
    }

    [Fact]
    public void LoadSecretPatterns_SkipsBlankLinesAndComments()
    {
        var path = Path.Combine(_tempDir, "patterns.txt");
        File.WriteAllText(path, "# a comment\n\nsk-[A-Za-z0-9]{20,}\n");

        var patterns = StdoutTailRenderer.LoadSecretPatterns(path);

        Assert.NotNull(patterns);
        var pattern = Assert.Single(patterns);
        var isMatch = pattern.IsMatch("sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
        Assert.True(isMatch);
    }

    /// <summary>Bound: a 10,000-line log yields a tail within spec/baton.md §6's own bound —
    /// <see cref="StdoutTailRenderer.StdoutTailMaxLines"/> lines, <see cref="StdoutTailRenderer.StdoutTailMaxBytes"/>
    /// bytes — and the surviving content is the NEWEST lines, never an arbitrary slice.</summary>
    [Fact]
    public void ComputeTail_BoundsA10000LineLog_ToSpecLimits()
    {
        // 150 chars/line * 40 lines ~= 6,000 bytes -- comfortably over STDOUT_TAIL_MAX_BYTES (4,000),
        // so this exercises the byte-cap path, not just the line-cap path.
        var lines = Enumerable.Range(0, 10_000).Select(i => $"line {i:D6} " + new string('x', 138)).ToList();
        var path = WriteLog("bound.stdout.log", lines);

        var tail = StdoutTailRenderer.ComputeTail(path, patterns: []);

        Assert.NotNull(tail);
        var encodedLength = System.Text.Encoding.UTF8.GetByteCount(tail!);
        Assert.True(encodedLength <= StdoutTailRenderer.StdoutTailMaxBytes,
            $"tail was {encodedLength} bytes, over the {StdoutTailRenderer.StdoutTailMaxBytes}-byte bound");

        var renderedLines = tail!.Split('\n');
        Assert.True(renderedLines.Length <= StdoutTailRenderer.StdoutTailMaxLines,
            $"tail carried {renderedLines.Length} lines, over the {StdoutTailRenderer.StdoutTailMaxLines}-line bound");

        // The newest line in the source log must survive somewhere in the tail (front-truncated, never
        // an arbitrary window) -- plain lines pass through RenderTailLine unchanged.
        Assert.Contains("line 009999", tail);
    }

    /// <summary>
    /// The OTHER branch of the byte-cap logic: when the single surviving line alone has no '\n' inside
    /// the truncation budget at all (a one-line tail, or -- as here -- every candidate boundary falls
    /// outside the kept window), <c>stdout_tail_for_room</c> keeps the newest line alone rather than
    /// collapsing to just the truncation mark (review rev1738 F3). The bound test above only exercises
    /// the "a newline WAS found" branch; this pins the other one, matching a real pusher.py run for the
    /// same fixture (a single long line -- word-repeated so no run is a 200+-char blob -- read with a
    /// deliberately tiny max_bytes).
    /// </summary>
    [Fact]
    public void ComputeTail_KeepsNewestLineAlone_WhenNoLineBoundaryFitsTheByteBudget()
    {
        var line = string.Join(' ', Enumerable.Repeat("word", 60));
        var path = WriteLog("no-boundary.stdout.log", [line]);

        var tail = StdoutTailRenderer.ComputeTail(path, patterns: [], maxBytes: 50);

        Assert.Equal("…ord word word word word word word word word …", tail);
        Assert.Equal(50, System.Text.Encoding.UTF8.GetByteCount(tail!));
    }

    /// <summary>
    /// #1793: <c>ComputeDoingNow</c>'s LAST assistant line has two content blocks (a text block, then a
    /// <c>tool_use</c> block) — the tool_use block wins because it's the LAST one, and its own
    /// <c>description</c> input field wins over the fallback name+argument summary. Reads the SAME
    /// checked-in fixture <c>tools/fleet-glass/pusher.py</c>'s selftest reads (see this repo's PR body
    /// for why the two implementations aren't a literal shared call) — a byte-identical fixture is what
    /// keeps the two independently-written ports honest against each other.
    /// </summary>
    [Fact]
    public void ComputeDoingNow_ReadsCheckedInFixture_MatchingPusherPySelftest()
    {
        var repoRoot = FindRepoRoot();
        var fixturePath = Path.Combine(repoRoot, "tests", "fixtures", "doing-now-sample.stdout.log");
        Assert.True(File.Exists(fixturePath), $"Fixture file must exist at {fixturePath}");

        var doingNow = StdoutTailRenderer.ComputeDoingNow(fixturePath, patterns: []);

        Assert.Equal("Running gates a second time before committing", doingNow);
    }

    [Fact]
    public void ComputeDoingNow_UsesLastAssistantTextLine_WhenTheLastBlockIsText()
    {
        var path = WriteLog("text-only.stdout.log", [
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"git status"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"abc","content":"nothing to commit"}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Reviewing the diff before I commit."}]}}""",
        ]);

        Assert.Equal("Reviewing the diff before I commit.", StdoutTailRenderer.ComputeDoingNow(path, patterns: []));
    }

    [Fact]
    public void ComputeDoingNow_FallsBackToToolNamePlusFirstArgument_WhenNoDescriptionField()
    {
        var path = WriteLog("no-description.stdout.log", [
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"git status"}}]}}""",
        ]);

        Assert.Equal("Bash git status", StdoutTailRenderer.ComputeDoingNow(path, patterns: []));
    }

    [Fact]
    public void ComputeDoingNow_ReturnsNull_WhenTheTailHasNoAssistantLine()
    {
        var path = WriteLog("no-assistant.stdout.log", [
            """{"type":"system","subtype":"init","session_id":"s-1"}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"all done"}""",
        ]);

        Assert.Null(StdoutTailRenderer.ComputeDoingNow(path, patterns: []));
    }

    /// <summary>#1818: a hit on the SAME denylist <c>ComputeTail</c> gates through withholds the derived doingNow line too.</summary>
    [Fact]
    public void ComputeDoingNow_Withholds_WhenTheDerivedLineMatchesASecretPattern()
    {
        var path = WriteLog("secret-doing-now.stdout.log", [
            """{"type":"assistant","message":{"content":[{"type":"text","text":"AKIA_FAKE_SECRET_TOKEN leaked here"}]}}""",
        ]);

        var doingNow = StdoutTailRenderer.ComputeDoingNow(path, patterns: [new Regex("AKIA_FAKE")]);

        Assert.Equal("[withheld]", doingNow);
    }

    /// <summary>#1818: <c>patterns</c> null is the fail-closed sentinel — every doingNow line is withheld, not just ones that happen to match.</summary>
    [Fact]
    public void ComputeDoingNow_WithholdsUnconditionally_WhenPatternsIsNull()
    {
        var path = WriteLog("null-patterns-doing-now.stdout.log", [
            """{"type":"assistant","message":{"content":[{"type":"text","text":"nothing secret here"}]}}""",
        ]);

        var doingNow = StdoutTailRenderer.ComputeDoingNow(path, patterns: null);

        Assert.Equal("[withheld]", doingNow);
    }

    /// <summary>Hard-truncates at <see cref="StdoutTailRenderer.DoingNowLimit"/> codepoints — <see cref="StdoutTailRenderer.DoingNowLimit"/>'s own doc comment is the canonical record of why this cap carries no trailing mark, unlike <c>ComputeTail</c>'s "…" suffix elsewhere in this file.</summary>
    [Fact]
    public void ComputeDoingNow_TruncatesAt140Codepoints_WithNoElisionMarker()
    {
        var longText = string.Join(' ', Enumerable.Repeat("word", 40));
        var line = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + longText + "\"}]}}";
        var path = WriteLog("long.stdout.log", [line]);

        var doingNow = StdoutTailRenderer.ComputeDoingNow(path, patterns: []);

        Assert.Equal(140, doingNow!.Length);
        Assert.DoesNotContain('…', doingNow);
        Assert.Equal(longText[..140], doingNow);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Baton.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root (Baton.slnx) from " + AppContext.BaseDirectory);
    }
}
