using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Cli.Daemon;
using Baton.Cli.Mcp;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;
using static Baton.Cli.Tests.TestSupport.ProcessIdentityFixture;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #1557 PR-A: <see cref="FleetProjectionWriter"/> writes <see cref="BatonPaths.FleetProjectionFile"/>.
/// Mirrors <c>FleetStatusToolTests</c>' own per-test isolated <c>BATON_HOME</c> pattern.
/// </summary>
public sealed class FleetProjectionWriterTests : IDisposable
{
    private readonly string _tempHome;
    private readonly IDisposable _scope;

    public FleetProjectionWriterTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"baton-fleet-projection-test-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = _tempHome });
    }

    public void Dispose()
    {
        _scope.Dispose();
        if (Directory.Exists(_tempHome))
        {
            DirectoryCleanup.DeleteRecursively(_tempHome);
        }
    }

    [Fact]
    public void WriteAtomic_never_lets_a_concurrent_reader_see_a_torn_file()
    {
        var path = Path.Combine(_tempHome, "projection.json");
        var contentA = new string('a', 50_000);
        var contentB = new string('b', 80_000);
        FleetProjectionWriter.WriteAtomic(path, contentA);

        Exception? readerException = null;
        var stop = false;

        var reader = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    // FileShare.Delete: a well-behaved poller of a file it knows gets rewritten out from
                    // under it -- File.ReadAllText's own default share (Read only) would make the
                    // writer's rename fail with a sharing violation on Windows whenever this loop happens
                    // to hold the file open, which is a liveness question this single-writer, no-retry
                    // design (the next ~30s tick self-heals) accepts. What this test asserts is narrower
                    // and must hold regardless: a read that DOES land never observes torn or mixed content.
                    using var stream = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var streamReader = new StreamReader(stream);
                    var text = streamReader.ReadToEnd();
                    if (text.Length != contentA.Length && text.Length != contentB.Length)
                    {
                        throw new InvalidOperationException($"torn read of length {text.Length}");
                    }

                    if (text.Length > 0 && text[0] != text[^1])
                    {
                        throw new InvalidOperationException("read mixed content from two writes");
                    }
                }
            }
            catch (Exception ex)
            {
                readerException = ex;
            }
        });
        reader.Start();

        for (var i = 0; i < 200; i++)
        {
            // #1782: WriteAtomic now owns its own retry against a transient sharing violation, so this
            // tight back-to-back loop calls it directly rather than through a test-side wrapper -- the
            // property under test (a landed read is never torn) still gets exercised hundreds of times
            // against a genuinely concurrent reader.
            FleetProjectionWriter.WriteAtomic(path, i % 2 == 0 ? contentB : contentA);
        }

        Volatile.Write(ref stop, true);
        reader.Join();

        Assert.Null(readerException);
    }

    /// <summary>#1782: a reader that opens the file with <see cref="FileShare.Read"/> only (the
    /// hostile case -- e.g. a naive poller that did not opt into <see cref="FileShare.Delete"/>) holds
    /// the target open across a write. The writer's retry must either land once the reader closes, or
    /// log-and-skip without throwing if the reader never does -- WriteAtomic must never throw out of
    /// the hosted service's tick for a transient sharing violation.</summary>
    [Fact]
    public async Task WriteAtomic_RetriesPastAHostileReader_ThatEventuallyCloses()
    {
        var path = Path.Combine(_tempHome, "projection.json");
        var original = "original-content";
        FleetProjectionWriter.WriteAtomic(path, original);

        using var blockingStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var updated = "updated-content-after-reader-closes";
        var writerTask = Task.Run(() => FleetProjectionWriter.WriteAtomic(path, updated), TestContext.Current.CancellationToken);

        // Give the writer a chance to hit -- and retry past -- the sharing violation before the
        // reader releases its handle, so the assertion actually exercises the retry path rather than
        // racing a writer that never contended in the first place.
        // wait-ok: fixed local delay bounding an in-process race window, not a wait for external state.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        blockingStream.Dispose();

        // wait-ok: upper bound on WriteAtomic's own bounded retry budget (5 attempts, backoff capped at 200ms) -- not a wait for external state.
        var completed = await Task.WhenAny(writerTask, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(writerTask, completed);
        Assert.True(writerTask.IsCompletedSuccessfully);

        // Confirms the write actually landed post-close rather than the test passing vacuously on a
        // writer that silently gave up: the file must hold the NEW content, not the original.
        Assert.Equal(updated, File.ReadAllText(path));
    }

    /// <summary>Polarity arm: a reader that never releases its handle within the retry budget must not
    /// crash the writer -- WriteAtomic logs and skips, leaving the prior content in place, and the
    /// original file must still be intact and readable afterward.</summary>
    [Fact]
    public void WriteAtomic_LogsAndSkips_WhenAHostileReaderNeverCloses()
    {
        var path = Path.Combine(_tempHome, "projection.json");
        var original = "original-content";
        FleetProjectionWriter.WriteAtomic(path, original);

        using var blockingStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var exception = Record.Exception(() => FleetProjectionWriter.WriteAtomic(path, "content-that-never-lands"));

        Assert.Null(exception);
        blockingStream.Dispose();

        // The skipped write must not have corrupted the target: the reader's own view (still open
        // above) and a fresh read afterward both see the untouched original content.
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void GetInterval_ClampsPathologicalValue_InsteadOfOverflowing()
    {
        // Mirrors RoomRetentionSweepTests' identically-named test: a value whose seconds would
        // overflow TimeSpan.FromSeconds must collapse to MaxInterval, never throw.
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { FleetProjectionIntervalSecondsOverride = "1e300" });

        var interval = FleetProjectionWriter.GetInterval();
        Assert.Equal(FleetProjectionWriter.MaxInterval, interval);
    }

    [Fact]
    public void GetInterval_LiftsSubSecondValue_ToMinInterval()
    {
        // Mirrors RoomRetentionSweepTests' identically-named test: a value below one second must lift
        // to MinInterval rather than pass through near-zero.
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { FleetProjectionIntervalSecondsOverride = "1e-9" });

        Assert.Equal(FleetProjectionWriter.MinInterval, FleetProjectionWriter.GetInterval());
    }

    [Fact]
    public async Task BuildProjectionJson_deserializes_into_FleetRoomStatusView_and_carries_derived_at()
    {
        var room = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "terminal-room");
        Directory.CreateDirectory(room);
        var sentinel = new WorkflowStatusView("Succeeded", [], ["/tmp/out.txt"], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        // A pruned execution directory containing an engine-written stream log alongside a worker
        // output. pruned[].bytes sums the whole thing unfiltered (matching pusher.py's own sum), not
        // #1351's listing filter -- see FleetProjectionWriter.cs's ComputePrunedInfo comment.
        var prunedExecDir = Path.Combine(room, "artifacts", "pruned", "execution_exec-1");
        Directory.CreateDirectory(prunedExecDir);
        await File.WriteAllTextAsync(
            Path.Combine(prunedExecDir, ".stdout.log"), new string('x', 500), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(prunedExecDir, "output.txt"), new string('y', 300), TestContext.Current.CancellationToken);

        var writer = new FleetProjectionWriter();
        var json = await writer.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        Assert.True(root.ContainsKey("derived_at"));
        var roomsNode = root["rooms"]!.AsArray();
        var singleRoomNode = Assert.Single(roomsNode);

        var roomView = singleRoomNode.Deserialize<FleetRoomStatusView>(FleetStatusTool.SerializerOptions);
        Assert.NotNull(roomView);
        Assert.Equal("terminal-room", roomView!.Name);
        Assert.Equal("Succeeded", roomView.State);

        // A terminal room carries no Running execution, so none of the Running-only fields are present.
        var roomObject = singleRoomNode!.AsObject();
        Assert.False(roomObject.ContainsKey("live"));
        Assert.False(roomObject.ContainsKey("processAlive"));

        var prunedItem = Assert.Single(roomObject["pruned"]!["items"]!.AsArray());
        Assert.Equal(800, prunedItem!["bytes"]!.GetValue<long>());
    }

    /// <summary>Pins the `live`-vs-diagnostics gating split <see cref="FleetProjectionWriter.BuildProjectionJsonAsync"/>'s
    /// own remarks state -- see that method for why.</summary>
    [Fact]
    public async Task RunningRoom_WithDeadEngine_ReportsProcessAliveDeadButNoLiveSection()
    {
        var (room, execId) = await CreateRunningRoomAsync("dead-engine-room", DeadProcessIdentity());

        var projectionWriter = new FleetProjectionWriter();
        var json = await projectionWriter.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        var roomNode = Assert.Single(root["rooms"]!.AsArray())!.AsObject();

        Assert.Equal("Stalled", roomNode["state"]!.GetValue<string>());
        Assert.Equal("dead", roomNode["processAlive"]!.GetValue<string>());
        Assert.True(roomNode.ContainsKey("stdout_last_write_ago_sec"));
        Assert.False(roomNode.ContainsKey("live"));

        _ = execId;
        _ = room;
    }

    /// <summary>Polarity arm: a genuinely alive engine keeps the room "Running" and DOES carry `live`,
    /// accumulated from the captured stdout via a daemon-side <c>TokenBudgetMonitor</c>.</summary>
    [Fact]
    public async Task RunningRoom_WithAliveEngine_ReportsLiveUsageFromCapturedStdout()
    {
        var liveIdentity = (Environment.ProcessId, new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime).ToUniversalTime());
        var (_, _) = await CreateRunningRoomAsync("alive-engine-room", liveIdentity);

        var projectionWriter = new FleetProjectionWriter();
        var json = await projectionWriter.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        var roomNode = Assert.Single(root["rooms"]!.AsArray())!.AsObject();

        Assert.Equal("Running", roomNode["state"]!.GetValue<string>());
        Assert.Equal("alive", roomNode["processAlive"]!.GetValue<string>());
        Assert.True(roomNode.ContainsKey("live"));
        var live = roomNode["live"]!.AsObject();
        Assert.Equal(100, live["billedTokens"]!.GetValue<long>());
        Assert.True(live["billedIsFloor"]!.GetValue<bool>());
        Assert.Equal(10, live["cacheReadTokens"]!.GetValue<long>());
        Assert.True(roomNode.ContainsKey("stdout_last_write_ago_sec"));
    }

    /// <summary>#1812: `rooms[].live.cacheReadTokens` is the LATEST usage line's reading, not the
    /// running Σ `Mutation.TokenBudgetMonitor` also tracks for #1682's budget arrest -- two lines
    /// (cache reads 800 then 100) must project 100, never 900, or the daemon's file path disagrees
    /// with pusher.py's derive path (which replaces, never sums, per its own doc comment) on what the
    /// field even means.</summary>
    [Fact]
    public async Task RunningRoom_CacheReadTokens_ReportsLatestLineNotRunningSum()
    {
        var liveIdentity = (Environment.ProcessId, new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime).ToUniversalTime());
        var stdoutContent =
            """{"type":"assistant","message":{"id":"msg_1","usage":{"cache_creation_input_tokens":100,"cache_read_input_tokens":800}}}""" + "\n"
            + """{"type":"assistant","message":{"id":"msg_2","usage":{"cache_creation_input_tokens":100,"cache_read_input_tokens":100}}}""" + "\n";
        var (_, _) = await CreateRunningRoomAsync("cache-read-level-room", liveIdentity, stdoutContent);

        var projectionWriter = new FleetProjectionWriter();
        var json = await projectionWriter.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        var roomNode = Assert.Single(root["rooms"]!.AsArray())!.AsObject();
        var live = roomNode["live"]!.AsObject();

        Assert.Equal(100, live["cacheReadTokens"]!.GetValue<long>());
    }

    /// <summary>
    /// #1886: a codex execution's live block, read off a real captured stream
    /// (<c>Fixtures/codex-live-stream.jsonl</c> — a contiguous 261-line prefix of
    /// <c>dispatch-implement-72f3ea9d</c>'s own <c>.stdout.log</c>, with each item's
    /// <c>aggregated_output</c>/<c>text</c> body replaced by a placeholder for fixture size; every
    /// field either parser reads is verbatim, and the file's own real <c>turn.completed</c> is
    /// appended last).
    /// <para>
    /// A REGRESSION PIN, not the verification of a fix: <c>StandardWorkerUsageParsers.Default</c>
    /// already carries <c>["codex"]</c> (#1862) and <see cref="Baton.Mutation.TokenBudgetMonitor"/> is
    /// vendor-agnostic, so the daemon projection was never the reader that fed #1886's zero —
    /// <c>pusher.py</c>'s derive path was, and that is where that issue's fix lives. What this pins is
    /// that dropping the adapter tag from the registry (or renaming it) would silently take the whole
    /// live block away from this vendor again, which is invisible without an arm that names the tag.
    /// Its polarity partner is <see cref="RunningRoom_UnknownAdapter_OmitsLiveCountFields"/> below:
    /// one condition apart (does the tag resolve a parser), opposite expectations.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunningRoom_CodexAdapter_ProjectsLiveToolCallsTurnsAndBilledTokens()
    {
        var liveIdentity = (Environment.ProcessId, new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime).ToUniversalTime());
        var stdoutContent = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "codex-live-stream.jsonl"),
            TestContext.Current.CancellationToken);
        var (_, _) = await CreateRunningRoomAsync("codex-room", liveIdentity, stdoutContent, adapter: "codex");

        var projectionWriter = new FleetProjectionWriter();
        var json = await projectionWriter.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        var roomNode = Assert.Single(root["rooms"]!.AsArray())!.AsObject();
        var live = roomNode["live"]!.AsObject();

        // 128 `item.started` lines carrying a tool item type, counted in the fixture itself.
        Assert.Equal(128, live["toolCalls"]!.GetValue<int>());
        // The fixture's one turn.completed: input 127,806 (inclusive of 126,720 cached) + cache write 0
        // + output 689 -> fresh input 1,086, billed 1,086 + 689 + 0. reasoning_output_tokens (130) is a
        // breakdown already inside output_tokens and is read by nothing.
        Assert.Equal(1_775, live["billedTokens"]!.GetValue<long>());
        Assert.Equal(1, live["turns"]!.GetValue<int>());
        Assert.Equal(127_806, live["contextTokens"]!.GetValue<long>());
        Assert.Equal(126_720, live["cacheReadTokens"]!.GetValue<long>());
        // Absent, not merely false -- spec/baton.md §6's `rooms[].live` entry has why this vendor's
        // figure carries no floor marker.
        Assert.False(live.ContainsKey("billedIsFloor"));
        Assert.True(live.ContainsKey("lastActivityAt"));
    }

    /// <summary>
    /// #1886 (PR review, MEDIUM): the ABSENT-versus-REAL-ZERO boundary for a codex stream that has
    /// started tool items but has NOT yet completed a turn — the ordinary shape of a lane while it is
    /// still working, and the one the fixture's own trailing <c>turn.completed</c> hides. The same
    /// fixture minus its last line: <c>toolCalls</c> is a real, measured 128, while
    /// <c>billedTokens</c>/<c>turns</c>/<c>contextTokens</c>/<c>cacheReadTokens</c> are ABSENT — no
    /// usage line has been read, and a substituted 0 on any of them would render on the glass as a
    /// lane that has burned nothing (spec/baton.md §6's <c>rooms[].live</c> absent-never-zero rule).
    /// Asserting <c>toolCalls == 128</c> rather than merely present is the discriminating half: a
    /// present-but-wrong count is the drift this pins against.
    /// </summary>
    [Fact]
    public async Task RunningRoom_CodexAdapter_ToolItemsWithoutCompletedTurn_OmitsUsageFields()
    {
        var liveIdentity = (Environment.ProcessId, new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime).ToUniversalTime());
        var fixtureLines = await File.ReadAllLinesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "codex-live-stream.jsonl"),
            TestContext.Current.CancellationToken);
        // Built from the ONE shared fixture rather than a second file, so the oracle cannot drift:
        // its last line is the stream's only turn.completed, and dropping it is exactly the
        // "still running, no turn resolved yet" state.
        Assert.Contains("\"type\":\"turn.completed\"", fixtureLines[^1]);
        var stdoutContent = string.Join("\n", fixtureLines[..^1]) + "\n";
        Assert.DoesNotContain("\"type\":\"turn.completed\"", stdoutContent);
        var (_, _) = await CreateRunningRoomAsync("codex-midturn-room", liveIdentity, stdoutContent, adapter: "codex");

        var projectionWriter = new FleetProjectionWriter();
        var json = await projectionWriter.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        var roomNode = Assert.Single(root["rooms"]!.AsArray())!.AsObject();
        var live = roomNode["live"]!.AsObject();

        Assert.Equal(128, live["toolCalls"]!.GetValue<int>());
        Assert.False(live.ContainsKey("billedTokens"));
        Assert.False(live.ContainsKey("billedIsFloor"));
        Assert.False(live.ContainsKey("turns"));
        Assert.False(live.ContainsKey("contextTokens"));
        Assert.False(live.ContainsKey("cacheReadTokens"));
    }

    /// <summary>
    /// #1886 (PR review, MEDIUM): the other side of that boundary — a RESOLVED codex parser reaching
    /// a genuine zero. The stream carries only codex envelopes that are neither a tool item nor a
    /// completed turn, so <c>toolCalls</c> is <c>0</c> — a count actually taken — while every usage
    /// field stays absent.
    /// <para>
    /// Its whole value is the contrast with <see cref="RunningRoom_UnknownAdapter_OmitsLiveCountFields"/>:
    /// the two are one condition apart (does the adapter tag resolve a parser) with opposite
    /// expectations for <c>toolCalls</c> — resolved-and-idle is <c>0</c>, unresolved is ABSENT. Neither
    /// arm alone can tell the two apart, which is the confusion #1886 was filed about.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunningRoom_CodexAdapter_NoToolItems_ReportsRealZeroToolCalls()
    {
        var liveIdentity = (Environment.ProcessId, new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime).ToUniversalTime());
        var stdoutContent =
            """{"type":"thread.started","thread_id":"th_1"}""" + "\n"
            + """{"type":"turn.started"}""" + "\n";
        var (_, _) = await CreateRunningRoomAsync("codex-idle-room", liveIdentity, stdoutContent, adapter: "codex");

        var projectionWriter = new FleetProjectionWriter();
        var json = await projectionWriter.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        var roomNode = Assert.Single(root["rooms"]!.AsArray())!.AsObject();
        var live = roomNode["live"]!.AsObject();

        Assert.Equal(0, live["toolCalls"]!.GetValue<int>());
        Assert.False(live.ContainsKey("billedTokens"));
        Assert.False(live.ContainsKey("turns"));
        Assert.False(live.ContainsKey("contextTokens"));
        Assert.False(live.ContainsKey("cacheReadTokens"));
    }

    /// <summary>
    /// #1886 polarity arm, one condition from
    /// <see cref="RunningRoom_CodexAdapter_NoToolItems_ReportsRealZeroToolCalls"/> above (same idle
    /// stream, only the adapter tag differs): an adapter tag
    /// <c>StandardWorkerUsageParsers.Default</c> has no parser for leaves <c>Monitor</c> null, and the
    /// live block then carries NO count fields at all — not a zero. Without this arm, a "fix" that made
    /// the writer emit <c>toolCalls: 0</c> whenever no parser resolved would pass the codex arm above
    /// and reintroduce exactly the zero #1886 is about. <c>lastActivityAt</c> stays present because it
    /// is the stream file's own mtime, which needs no parser (and is why #1886's "lastActivityAt may be
    /// keyed off a claude-only item type" hypothesis was false on both readers).
    /// </summary>
    [Fact]
    public async Task RunningRoom_UnknownAdapter_OmitsLiveCountFields()
    {
        var liveIdentity = (Environment.ProcessId, new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime).ToUniversalTime());
        var stdoutContent = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "codex-live-stream.jsonl"),
            TestContext.Current.CancellationToken);
        Assert.False(StandardWorkerUsageParsers.Default.ContainsKey("not-a-vendor"));
        var (_, _) = await CreateRunningRoomAsync(
            "unknown-adapter-room", liveIdentity, stdoutContent, adapter: "not-a-vendor");

        var projectionWriter = new FleetProjectionWriter();
        var json = await projectionWriter.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        var roomNode = Assert.Single(root["rooms"]!.AsArray())!.AsObject();
        var live = roomNode["live"]!.AsObject();

        Assert.False(live.ContainsKey("toolCalls"));
        Assert.False(live.ContainsKey("billedTokens"));
        Assert.False(live.ContainsKey("turns"));
        Assert.False(live.ContainsKey("contextTokens"));
        Assert.False(live.ContainsKey("cacheReadTokens"));
        Assert.True(live.ContainsKey("lastActivityAt"));
    }

    /// <summary>
    /// #1557 PR-A2: end-to-end wiring for <c>live.stdoutTail</c> — <see cref="StdoutTailRendererTests"/>
    /// pins the renderer's own output against pusher.py; this pins that
    /// <see cref="FleetProjectionWriter"/> actually calls it with the room's real stdout path and a
    /// present secret-gate denylist (<see cref="BatonPaths.SecretPatternsFile"/> under this test's own
    /// isolated <c>BATON_HOME</c>), and that a hit is withheld exactly where the renderer's own tests
    /// say it should be.
    /// </summary>
    [Fact]
    public async Task RunningRoom_WithAliveEngine_ReportsStdoutTail_RenderedAndSecretGated()
    {
        var liveIdentity = (Environment.ProcessId, new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime).ToUniversalTime());
        var (room, execId) = await CreateRunningRoomAsync("tail-room", liveIdentity);

        var stdoutPath = Path.Combine(room, "artifacts", $"execution_{execId.Value}", ".stdout.log");
        await File.AppendAllTextAsync(
            stdoutPath,
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Drafting the plan now."}]}}""" + "\n"
            + "Authorization: Bearer sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789" + "\n",
            TestContext.Current.CancellationToken);

        // Present-but-non-empty patterns file (secretpatterns.example.txt's own first line) -- proves
        // the daemon actually loads BatonPaths.SecretPatternsFile and gates with it, without depending
        // on the operator's own gitignored denylist. #1816: placed at the pusher's own fleet-glass
        // convention, not directly under the root -- that is the whole point of this test. The path is
        // spelled out here rather than read back from BatonPaths.SecretPatternsFile so the test pins
        // the LOCATION: written through the property, it would pass against any path the property
        // happened to return, including the pre-#1816 flat one (#1820 review).
        var denylistPath = Path.Combine(BatonPaths.Root, "fleet-glass", "secretpatterns.local.txt");
        Assert.Equal(denylistPath, BatonPaths.SecretPatternsFile);
        Directory.CreateDirectory(Path.GetDirectoryName(denylistPath)!);
        await File.WriteAllTextAsync(
            denylistPath, "sk-[A-Za-z0-9]{20,}\n", TestContext.Current.CancellationToken);

        var projectionWriter = new FleetProjectionWriter();
        var json = await projectionWriter.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        var roomNode = Assert.Single(root["rooms"]!.AsArray())!.AsObject();
        var live = roomNode["live"]!.AsObject();

        Assert.Equal("Drafting the plan now.\n[withheld]", live["stdoutTail"]!.GetValue<string>());
        // #1793: same stdout, one more read off the same path -- StdoutTailRendererTests pins the
        // renderer's own output; this pins that FleetProjectionWriter actually calls it and attaches
        // the result under live.doingNow.
        Assert.Equal("Drafting the plan now.", live["doingNow"]!.GetValue<string>());
    }

    /// <summary>Fail-closed polarity arm: no <see cref="BatonPaths.SecretPatternsFile"/> under this
    /// test's isolated <c>BATON_HOME</c> at all -- every line withheld, matching pusher.py's own
    /// missing-denylist ruling (spec/baton.md §6), not merely a per-pattern miss. #1816: also asserts
    /// the one-line withhold-everything log the writer now emits instead of failing silently.</summary>
    [Fact]
    public async Task RunningRoom_WithAliveEngine_WithholdsStdoutTail_WhenPatternsFileMissing()
    {
        var liveIdentity = (Environment.ProcessId, new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime).ToUniversalTime());
        var (room, execId) = await CreateRunningRoomAsync("tail-failclosed-room", liveIdentity);

        var stdoutPath = Path.Combine(room, "artifacts", $"execution_{execId.Value}", ".stdout.log");
        await File.AppendAllTextAsync(
            stdoutPath,
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Drafting the plan now."}]}}""" + "\n",
            TestContext.Current.CancellationToken);

        Assert.False(File.Exists(BatonPaths.SecretPatternsFile));

        var projectionWriter = new FleetProjectionWriter();
        var diagnostics = new StringWriter();
        var json = await projectionWriter.BuildProjectionJsonAsync(TestContext.Current.CancellationToken, diagnostics);

        var root = JsonNode.Parse(json)!.AsObject();
        var roomNode = Assert.Single(root["rooms"]!.AsArray())!.AsObject();
        var live = roomNode["live"]!.AsObject();

        Assert.Equal("[withheld]", live["stdoutTail"]!.GetValue<string>());

        var logLine = Assert.Single(diagnostics.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Contains(BatonPaths.SecretPatternsFile, logLine, StringComparison.Ordinal);
        Assert.Contains("WITHHOLDING EVERY stdoutTail line", logLine, StringComparison.Ordinal);
    }

    /// <summary>Builds a room with one "architect" step Running under a real captured `.stdout.log`,
    /// recorded engine identity <paramref name="identity"/>, and a claude bindings.json entry.</summary>
    private Task<(string RoomDir, ExecutionId ExecutionId)> CreateRunningRoomAsync(
        string roomName, (int Pid, DateTimeOffset StartTime) identity)
        => CreateRunningRoomAsync(roomName, identity, stdoutContent: null);

    /// <summary>Same as the overload above, but with the captured `.stdout.log`'s content overridable
    /// -- #1812: lets a test feed several usage-bearing lines rather than the single-line default --
    /// and (#1886) the room's adapter tag, which is what selects the parser the daemon's live block is
    /// accumulated through.</summary>
    private async Task<(string RoomDir, ExecutionId ExecutionId)> CreateRunningRoomAsync(
        string roomName, (int Pid, DateTimeOffset StartTime) identity, string? stdoutContent,
        string adapter = "claude")
    {
        var room = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, roomName);
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-a"), "architect", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), TestContext.Current.CancellationToken);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                adapter,
                new WorkerContract("architect", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Draft a plan.",
                TimeSpan.FromMinutes(5)),
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(
            bindings, BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken);

        var execId = new ExecutionId($"exec-{roomName}");
        var req = new ExecutionRequest(
            execId, new WorkflowId("wf"), stepDef.StepId, stepDef.Worker,
            [], [], TimeSpan.FromMinutes(5), [], new Dictionary<StepId, ExecutionId>(), Adapter: adapter);

        var logWriter = new FlowEventLogWriter(Path.Combine(room, "flow.jsonl"));
        await logWriter.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(req, EnginePid: identity.Pid, EngineStartTime: identity.StartTime),
            TestContext.Current.CancellationToken);
        await logWriter.DisposeAsync();

        // The Running execution's own captured stdout, at the exact path ArtifactManager resolves.
        var stdoutDir = Path.Combine(room, "artifacts", $"execution_{execId.Value}");
        Directory.CreateDirectory(stdoutDir);
        await File.WriteAllTextAsync(
            Path.Combine(stdoutDir, ".stdout.log"),
            stdoutContent ??
            """{"type":"assistant","message":{"id":"msg_1","usage":{"cache_creation_input_tokens":100,"cache_read_input_tokens":10}}}""" + "\n",
            TestContext.Current.CancellationToken);

        return (room, execId);
    }

    /// <summary>
    /// Issue #1391 round-trip: a persisted claude snapshot plus one Running claude-bound room projects
    /// as one <c>vendors[]</c> entry with <c>liveLanes: 1</c> -- the same file
    /// <see cref="Baton.Cli.Daemon.VendorUsageHarvester"/> writes, read back through
    /// <see cref="Baton.Cli.Mcp.VendorUsageProjectionReader"/>.
    /// </summary>
    [Fact]
    public async Task BuildProjectionJson_PersistedClaudeSnapshotPlusRunningRoom_ProjectsVendorsEntry()
    {
        var liveIdentity = (Environment.ProcessId, new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime).ToUniversalTime());
        await CreateRunningRoomAsync("vendors-block-room", liveIdentity);

        var snapshot = new VendorUsageSnapshot(
            "claude",
            new DateTimeOffset(2026, 9, 4, 18, 0, 0, TimeSpan.Zero),
            "Approximate, based on local sessions on this machine — does not include other devices or claude.ai.",
            [new VendorUsageWindow("session", 8, new DateTimeOffset(2026, 9, 4, 21, 19, 0, TimeSpan.Zero), "Current session: 8% used")]);
        var snapshotPath = BatonPaths.VendorUsageSnapshotFile("claude");
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        await File.WriteAllTextAsync(snapshotPath, JsonSerializer.Serialize(snapshot), TestContext.Current.CancellationToken);

        var projectionWriter = new FleetProjectionWriter();
        var json = await projectionWriter.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        var vendorsArray = root["vendors"]!.AsArray();
        var claudeEntry = Assert.Single(vendorsArray)!.AsObject();

        Assert.Equal("claude", claudeEntry["adapter"]!.GetValue<string>());
        Assert.Equal(1, claudeEntry["liveLanes"]!.GetValue<int>());
        var window = Assert.Single(claudeEntry["windows"]!.AsArray())!.AsObject();
        Assert.Equal("session", window["name"]!.GetValue<string>());
        Assert.Equal(8, window["percentUsed"]!.GetValue<int>());
    }

    /// <summary>No snapshot has ever been harvested -- `vendors` is absent entirely, never an empty
    /// array, matching every other optional field's omit-when-absent convention on this shape.</summary>
    [Fact]
    public async Task BuildProjectionJson_NoHarvestedSnapshot_OmitsVendorsKey()
    {
        var writer = new FleetProjectionWriter();
        var json = await writer.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        Assert.False(root.ContainsKey("vendors"));
    }

    /// <summary>
    /// #1902: writes per-room timelines into projection.json; running rooms are re-read every cycle
    /// and terminal rooms are read once and served from memory after.
    /// </summary>
    [Fact]
    public async Task BuildProjectionJson_RunningAndTerminalRooms_ProduceExpectedTimelinesAndServeTerminalFromMemory()
    {
        var liveIdentity = (Environment.ProcessId, new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime).ToUniversalTime());
        var (runningRoom, runningExecId) = await CreateRunningRoomAsync("running-timeline-room", liveIdentity);

        var runLogWriter = new FlowEventLogWriter(Path.Combine(runningRoom, BatonPaths.FlowLogFileName));
        await runLogWriter.AppendAsync(
            new CoreEvent.ExecutionStarted(runningExecId, (uint)liveIdentity.Item1),
            TestContext.Current.CancellationToken);
        await runLogWriter.DisposeAsync();

        var terminalRoom = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "terminal-timeline-room");
        Directory.CreateDirectory(terminalRoom);
        var sentinel = new WorkflowStatusView("Succeeded", [], ["/tmp/out.txt"], null, null);
        await TerminalSentinelWriter.WriteAsync(terminalRoom, sentinel, TestContext.Current.CancellationToken);

        var termExecId = new ExecutionId("exec-term-1");
        var termLogWriter = new FlowEventLogWriter(Path.Combine(terminalRoom, BatonPaths.FlowLogFileName));
        await termLogWriter.AppendAsync(
            new CoreEvent.ExecutionStarted(termExecId, 1234),
            TestContext.Current.CancellationToken);
        await termLogWriter.AppendAsync(
            new CoreEvent.ExecutionExited(termExecId, ExitCode: 0, CoreExitReason.Natural),
            TestContext.Current.CancellationToken);
        await termLogWriter.DisposeAsync();

        var writer = new FleetProjectionWriter();
        var json = await writer.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        var timelines = root["timelines"]!.AsObject();

        Assert.True(timelines.ContainsKey(runningRoom));
        Assert.True(timelines.ContainsKey(terminalRoom));

        var runningTimeline = timelines[runningRoom]!.AsArray();
        Assert.Equal(2, runningTimeline.Count);
        Assert.Equal("flow.executionRequestAccepted", runningTimeline[0]!["type"]!.GetValue<string>());
        Assert.Equal("core.executionStarted", runningTimeline[1]!["type"]!.GetValue<string>());

        var terminalTimeline = timelines[terminalRoom]!.AsArray();
        Assert.Equal(2, terminalTimeline.Count);
        Assert.Equal("core.executionStarted", terminalTimeline[0]!["type"]!.GetValue<string>());
        Assert.Equal("core.executionExited", terminalTimeline[1]!["type"]!.GetValue<string>());
        Assert.Equal(0, terminalTimeline[1]!["exitCode"]!.GetValue<int>());

        // Serve terminal from memory: delete terminal room's flow.jsonl from disk.
        File.Delete(Path.Combine(terminalRoom, BatonPaths.FlowLogFileName));

        // Append a new event to the running room's flow.jsonl to prove it is re-read.
        var runLogWriter2 = new FlowEventLogWriter(Path.Combine(runningRoom, BatonPaths.FlowLogFileName));
        await runLogWriter2.AppendAsync(
            new CoreEvent.ExecutionExited(runningExecId, ExitCode: 42, CoreExitReason.Natural),
            TestContext.Current.CancellationToken);
        await runLogWriter2.DisposeAsync();

        var json2 = await writer.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);
        var root2 = JsonNode.Parse(json2)!.AsObject();
        var timelines2 = root2["timelines"]!.AsObject();

        // Terminal room served from memory despite missing flow.jsonl on disk
        var terminalTimeline2 = timelines2[terminalRoom]!.AsArray();
        Assert.Equal(2, terminalTimeline2.Count);
        Assert.Equal("core.executionExited", terminalTimeline2[1]!["type"]!.GetValue<string>());

        // Running room re-read and updated with the new event
        var runningTimeline2 = timelines2[runningRoom]!.AsArray();
        Assert.Equal(3, runningTimeline2.Count);
        Assert.Equal("core.executionExited", runningTimeline2[2]!["type"]!.GetValue<string>());
        Assert.Equal(42, runningTimeline2[2]!["exitCode"]!.GetValue<int>());
    }

    /// <summary>
    /// #1902: timeline entries are capped at <see cref="FleetProjectionWriter.TimelineCap"/> (30),
    /// keeping the newest tail.
    /// </summary>
    [Fact]
    public async Task BuildProjectionJson_TimelineHonoursCap()
    {
        var room = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "capped-timeline-room");
        Directory.CreateDirectory(room);
        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var logWriter = new FlowEventLogWriter(Path.Combine(room, BatonPaths.FlowLogFileName));
        var total = FleetProjectionWriter.TimelineCap + 5;
        for (var i = 0; i < total; i++)
        {
            await logWriter.AppendAsync(
                new CoreEvent.ExecutionExited(new ExecutionId($"exec-{i}"), ExitCode: i, CoreExitReason.Natural),
                TestContext.Current.CancellationToken);
        }
        await logWriter.DisposeAsync();

        var writer = new FleetProjectionWriter();
        var json = await writer.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);
        var root = JsonNode.Parse(json)!.AsObject();
        var timelines = root["timelines"]!.AsObject();
        var roomTimeline = timelines[room]!.AsArray();

        Assert.Equal(FleetProjectionWriter.TimelineCap, roomTimeline.Count);
        Assert.Equal(5, roomTimeline[0]!["exitCode"]!.GetValue<int>());
        Assert.Equal(total - 1, roomTimeline[^1]!["exitCode"]!.GetValue<int>());
    }

    /// <summary>
    /// Corrupt flow events exclude the room from the timelines map without throwing.
    /// </summary>
    [Fact]
    public async Task BuildProjectionJson_UnreadableFlowLog_YieldsNoTimelinesEntryRatherThanThrow()
    {
        var room = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "unreadable-flow-room");
        Directory.CreateDirectory(room);
        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(room, BatonPaths.FlowLogFileName),
            "{ not valid json at all\n",
            TestContext.Current.CancellationToken);

        var writer = new FleetProjectionWriter();
        var json = await writer.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);
        var root = JsonNode.Parse(json)!.AsObject();
        var timelines = root["timelines"]!.AsObject();

        Assert.False(timelines.ContainsKey(room));
    }
}
