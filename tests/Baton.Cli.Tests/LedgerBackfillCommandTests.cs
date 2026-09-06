using System.Text.Json;
using Baton.Accounting;
using Baton.Artifacts;
using Baton.Cli.Daemon;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1901 C2: <c>baton ledger backfill</c>, driven over real fixture rooms and a <c>gh</c> double.
/// </summary>
/// <remarks>
/// <para>
/// The fixture rooms are written the way production writes them — a real <c>flow.jsonl</c> through
/// <see cref="FlowEventLogWriter"/> and a real captured <c>.stdout.log</c> under
/// <see cref="ArtifactManager.ResolveOutputDirectory"/> — so these tests exercise the same read path a
/// settle does rather than a hand-built <see cref="LogEntry"/> list. What they own is the BACKFILL's
/// own contract: which rooms yield rows, which are reported unattributed and why, that a second run
/// writes nothing, and that a dry run writes nothing at all.
/// </para>
/// <para>
/// <b>Two seams, both mandatory here.</b> The repository probe is stubbed because a fixture room under
/// <c>%TEMP%</c> has no git of its own and the real probe would fall back to the test host's working
/// directory — keying every fixture row to whatever repository the tests happen to run inside. The
/// ledger directory is overridden for the reason that seam's own parameter doc gives.
/// </para>
/// </remarks>
public sealed class LedgerBackfillCommandTests : IDisposable
{
    private static readonly RepositoryIdentity Repository =
        RepositoryIdentity.From("https://github.com/aer-works/baton.git", null)!;

    /// <summary>A claude terminal line plus the mid-stream usage line the reconciliation needs — the shape <c>CostLedgerStoreTests</c> pins as a clean stream.</summary>
    private const string ClaudeAssistantUsageLine =
        """{"type":"assistant","message":{"id":"msg_1","usage":{"input_tokens":2,"cache_creation_input_tokens":10,"cache_read_input_tokens":5,"output_tokens":3}}}""";

    private const string ClaudeTerminalLine =
        """{"type":"result","subtype":"success","is_error":false,"duration_ms":1234,"num_turns":3,"result":"done","session_id":"s","usage":{"input_tokens":100,"output_tokens":50,"cache_creation_input_tokens":10,"cache_read_input_tokens":5,"output_tokens_details":{"thinking_tokens":7}}}""";

    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), $"baton-1901c2-{Guid.NewGuid():N}");

    private string RoomsRoot => Path.Combine(_sandbox, "rooms");

    private string LedgerDirectory => Path.Combine(_sandbox, "ledger");

    private string LedgerFilePath => Path.Combine(LedgerDirectory, $"{Repository.FileSlug}.jsonl");

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_sandbox);

    /// <summary>
    /// The headline: a settled room with no ledger row of its own becomes one row per settled
    /// execution, carrying the tokens the projector read out of the captured stream — the same row
    /// builder a settle uses, which is what the issue's "do not write a second one" asks for.
    /// </summary>
    [Fact]
    public async Task A_settled_room_with_no_ledger_row_yet_becomes_one_row_per_execution()
    {
        await WriteSettledRoomAsync("settled", "exec-settled");

        var output = await RunAsync();

        var row = Assert.Single(await ReadLedgerAsync());
        Assert.Equal("exec-settled", row.Execution);
        Assert.Equal(CostSourceKind.BatonExecution, row.SourceKind);
        Assert.Equal(Repository.Value, row.Repository);
        Assert.Equal(100, row.TokensIn);
        Assert.Equal(50, row.TokensOut);
        Assert.Equal(CostCompleteness.Complete, row.Completeness);
        Assert.Contains("Rows written: 1", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1901 C2's own words: <c>completeness: partial</c> where the stream is truncated. The control is
    /// the arm above, over the identical stream WITHOUT the truncation marker, which reads
    /// <c>complete</c> — without it this assertion would pass against a backfill that labelled
    /// everything partial.
    /// </summary>
    [Fact]
    public async Task A_room_whose_captured_stream_is_truncated_is_recovered_as_partial()
    {
        await WriteSettledRoomAsync("truncated", "exec-truncated", truncated: true);

        await RunAsync();

        var row = Assert.Single(await ReadLedgerAsync());
        Assert.Equal(CostCompleteness.Partial, row.Completeness);
        Assert.NotNull(row.CompletenessReason);
    }

    /// <summary>
    /// A review execution's <c>verdict.json</c> is recovered onto its row, and a room without one
    /// leaves all five verdict fields absent rather than zero — the polarity, in one test, so a
    /// backfill that stamped zeros could not pass either half.
    /// </summary>
    [Fact]
    public async Task A_verdict_on_disk_is_recovered_and_a_room_without_one_carries_none()
    {
        await WriteSettledRoomAsync("reviewed", "exec-reviewed", worker: "review");
        WriteVerdict(
            Path.Combine(RoomsRoot, "reviewed"),
            "exec-reviewed",
            """{"reviewedRef": "#1913", "findings": [{"severity":"high","claim":"c","status":"confirmed"}]}""");
        await WriteSettledRoomAsync("plain", "exec-plain");

        await RunAsync();

        var rows = await ReadLedgerAsync();
        var reviewed = Assert.Single(rows, r => r.Execution == "exec-reviewed");
        Assert.Equal("#1913", reviewed.ReviewedRef);
        Assert.Equal("1913", reviewed.ReviewedPr);
        Assert.Equal(1, reviewed.FindingsHigh);
        Assert.Equal(0, reviewed.FindingsLow);

        var plain = Assert.Single(rows, r => r.Execution == "exec-plain");
        Assert.Null(plain.ReviewedRef);
        Assert.Null(plain.FindingsHigh);
    }

    /// <summary>
    /// #1901 C2's idempotence criterion, asserted as "the file is unchanged" rather than as a count the
    /// command reports about itself: a second run reading its own first run's output must write nothing.
    /// </summary>
    [Fact]
    public async Task A_second_run_writes_nothing()
    {
        await WriteSettledRoomAsync("settled", "exec-settled");

        await RunAsync();
        var afterFirst = await File.ReadAllBytesAsync(LedgerFilePath, TestContext.Current.CancellationToken);

        var second = await RunAsync();
        var afterSecond = await File.ReadAllBytesAsync(LedgerFilePath, TestContext.Current.CancellationToken);

        Assert.Equal(afterFirst, afterSecond);
        Assert.Contains("Rows already in the ledger: 1", second, StringComparison.Ordinal);
        Assert.Contains("Rows written: 0", second, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row already in the ledger is reported, never rewritten — and when it predates the verdict its
    /// execution wrote, the run says so out loud rather than silently leaving the gap. The append-only
    /// guarantee is what makes this a disclosure instead of a repair.
    /// </summary>
    [Fact]
    public async Task An_already_ledgered_execution_whose_row_predates_its_verdict_is_disclosed()
    {
        await WriteSettledRoomAsync("reviewed", "exec-reviewed", worker: "review");

        // The row as a pre-#1901-C1 settle wrote it: right execution id, no verdict fields.
        await RunAsync();
        WriteVerdict(
            Path.Combine(RoomsRoot, "reviewed"),
            "exec-reviewed",
            """{"reviewedRef": "#1913", "findings": []}""");

        var output = await RunAsync();

        var row = Assert.Single(await ReadLedgerAsync());
        Assert.Null(row.ReviewedRef);
        Assert.Contains("wrote a verdict.json their row predates", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--dry-run</c> writes nothing — asserted on the FILE, not on the report, because a report that
    /// says "would write" while the file grew is exactly the failure this flag exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_dry_run_writes_nothing_and_says_what_it_would_have_written()
    {
        await WriteSettledRoomAsync("settled", "exec-settled");

        var output = await RunAsync(new LedgerBackfillOptions(DryRun: true, RoomsRoot: RoomsRoot));

        Assert.False(File.Exists(LedgerFilePath));
        Assert.Contains("DRY RUN -- nothing was written", output, StringComparison.Ordinal);
        Assert.Contains("Rows this would write: 1", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A room that yields no row is reported with its own reason rather than vanishing into a count —
    /// the half of <c>--dry-run</c> <see cref="LedgerBackfillCommand"/>'s report exists for.
    /// </summary>
    [Fact]
    public async Task A_room_with_no_flow_log_is_reported_unattributed_with_its_reason()
    {
        Directory.CreateDirectory(Path.Combine(RoomsRoot, "empty"));

        var output = await RunAsync(new LedgerBackfillOptions(DryRun: true, RoomsRoot: RoomsRoot));

        Assert.Contains("Rooms walked: 1", output, StringComparison.Ordinal);
        Assert.Contains("Rooms not attributed: 1", output, StringComparison.Ordinal);
        Assert.Contains("no flow.jsonl", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The GitHub half: a merged PR whose head branch a room declares joins to that room; one that no
    /// room declares still becomes a row and is reported as unattributed. The two arms are in one test
    /// because the second is only meaningful against a first that DID join — a backfill that joined
    /// nothing would otherwise pass the unattributed half alone.
    /// </summary>
    [Fact]
    public async Task A_merged_pr_joins_to_the_room_that_declares_its_branch_and_an_unjoinable_one_is_reported()
    {
        await WriteSettledRoomAsync("settled", "exec-settled");
        WriteDeliveryBranch(Path.Combine(RoomsRoot, "settled"), "exec-settled", "1901-lane");

        var output = await RunAsync(gh: new StubGh("""
            [
              {"number":1913,"headRefName":"1901-lane","mergedAt":"2026-09-05T12:00:00Z","additions":420,
               "deletions":17,"changedFiles":9,"commits":[{},{}],"reviews":[{}],
               "closingIssuesReferences":[{"number":1901}]},
              {"number":1925,"headRefName":"someone-elses-branch","mergedAt":"2026-09-05T13:00:00Z",
               "additions":1,"deletions":1,"changedFiles":1,"commits":[{}],"reviews":[],
               "closingIssuesReferences":[]}
            ]
            """));

        var rows = await ReadLedgerAsync();

        var joined = Assert.Single(rows, r => r.PullRequest == "1913");
        Assert.Equal(CostSourceKind.GithubBackfill, joined.SourceKind);
        Assert.Equal(CostLedgerStore.GithubBackfillExecutionId(1913), joined.Execution);
        Assert.Equal("1901", joined.Issue);
        Assert.Equal(BatonPaths.RecordKey(Path.Combine(RoomsRoot, "settled")), joined.Room);
        Assert.Equal(9, joined.FilesChanged);
        Assert.Equal(420, joined.Additions);
        Assert.Equal(2, joined.Commits);
        Assert.Equal(1, joined.ReviewCount);
        Assert.Equal(new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc), joined.EndedAt);

        // No tokens and no estimate: nothing ran, so there is nothing to price.
        Assert.Null(joined.TokensIn);
        Assert.Null(joined.ApiEquivalentUsd);

        var orphan = Assert.Single(rows, r => r.PullRequest == "1925");
        Assert.Null(orphan.Room);
        Assert.Equal(0, orphan.ReviewCount);
        Assert.Contains("PRs not joined to a room: 1", output, StringComparison.Ordinal);
        Assert.Contains("#1925 (someone-elses-branch)", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// One malformed entry costs its own row and nothing else — the fail-open-per-PR rule, with the
    /// well-formed sibling beside it as the control.
    /// </summary>
    [Fact]
    public async Task A_pull_request_with_no_usable_number_is_counted_and_skipped_rather_than_fatal()
    {
        var output = await RunAsync(gh: new StubGh("""
            [
              {"headRefName":"no-number-here","mergedAt":"2026-09-05T12:00:00Z"},
              {"number":1913,"headRefName":"1901-lane","mergedAt":"2026-09-05T12:00:00Z"}
            ]
            """));

        var row = Assert.Single(await ReadLedgerAsync());
        Assert.Equal("1913", row.PullRequest);
        Assert.Contains("1 carried no usable number", output, StringComparison.Ordinal);
    }

    /// <summary>A <c>gh</c> that is missing or unauthenticated costs the GitHub half and nothing else — the room rows still land.</summary>
    [Fact]
    public async Task A_gh_that_does_not_answer_costs_the_github_half_and_not_the_rooms()
    {
        await WriteSettledRoomAsync("settled", "exec-settled");

        var output = await RunAsync(gh: new StubGh(string.Empty, started: false, exitCode: -1, stderr: "gh was not found on PATH."));

        Assert.Single(await ReadLedgerAsync());
        Assert.Contains("the GitHub half was skipped", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Re-running the GitHub half writes nothing a second time: the PR row's own
    /// <c>github-pr-&lt;n&gt;</c> id is what the ledger dedupes on, and a row with no id would be
    /// appended again on every run (<see cref="Baton.Status.JsonLinesLedger{TEntry}"/> always appends a
    /// keyless row).
    /// </summary>
    [Fact]
    public async Task A_second_run_over_the_same_merged_prs_writes_nothing()
    {
        var gh = new StubGh("""[{"number":1913,"headRefName":"1901-lane","mergedAt":"2026-09-05T12:00:00Z"}]""");

        await RunAsync(gh: gh);
        var afterFirst = await File.ReadAllBytesAsync(LedgerFilePath, TestContext.Current.CancellationToken);
        await RunAsync(gh: gh);

        Assert.Equal(afterFirst, await File.ReadAllBytesAsync(LedgerFilePath, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// #1901 C2's <c>--since</c>: the window is <c>LedgerQuery</c>'s, so a room whose only attempt ended
    /// before it is left out. The control is the same room with the window open, which yields the row —
    /// without it, a backfill that recovered nothing at all would pass.
    /// </summary>
    [Fact]
    public async Task Since_excludes_a_room_whose_attempts_all_ended_before_the_window()
    {
        await WriteSettledRoomAsync("settled", "exec-settled");

        var excluded = await RunAsync(new LedgerBackfillOptions(
            DryRun: true, RoomsRoot: RoomsRoot, Since: DateTime.UtcNow.AddDays(1)));
        Assert.Contains("Rows this would write: 0", excluded, StringComparison.Ordinal);
        Assert.Contains("no settled execution attempt inside the --since window", excluded, StringComparison.Ordinal);

        var included = await RunAsync(new LedgerBackfillOptions(
            DryRun: true, RoomsRoot: RoomsRoot, Since: DateTime.UtcNow.AddDays(-1)));
        Assert.Contains("Rows this would write: 1", included, StringComparison.Ordinal);
    }

    /// <summary>#1901 C2 item 2: the dispatch <c>--label</c> off the room's own bindings lands on the row the backfill writes.</summary>
    [Fact]
    public async Task The_dispatch_label_on_a_rooms_bindings_lands_on_its_recovered_rows()
    {
        await WriteSettledRoomAsync("labelled", "exec-labelled");
        WriteBindings(Path.Combine(RoomsRoot, "labelled"), "implement", "arm-b");

        await RunAsync();

        Assert.Equal("arm-b", Assert.Single(await ReadLedgerAsync()).Label);
    }

    /// <summary>
    /// The merged-PR walk PAGES, and terminates. <c>gh pr list</c> cannot ask for
    /// <see cref="LedgerBackfillCommand.MaxPullRequests"/> PRs in one call — that constant's own doc
    /// carries the measured GraphQL node ceiling that forces
    /// <see cref="LedgerBackfillCommand.PullRequestPageSize"/> — so a full page must advance a
    /// <c>merged:&lt;=</c> cursor and ask again, and the pages OVERLAP on their boundary PR.
    /// <para>
    /// Three things are pinned here, and each is a way the loop could go wrong: the second call carries
    /// a cursor (without it the same page repeats forever), the boundary PR yields ONE row rather than
    /// two (the overlap is deduplicated), and a short page ends the walk (without that it would spin
    /// until the cap on a repository with fifty PRs).
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_merged_pr_walk_pages_with_a_cursor_and_deduplicates_the_overlapping_boundary()
    {
        // Newest first, as gh orders them. 500 down to 461 is a FULL page; the second call repeats 461
        // (the inclusive `merged:<=` bound) and adds two older ones.
        var gh = new ScriptedGh(
            Page(Enumerable.Range(0, LedgerBackfillCommand.PullRequestPageSize).Select(i => 500 - i)),
            Page([461, 460, 459]));

        await RunAsync(gh: gh);

        Assert.Equal(2, gh.Calls.Count);
        Assert.DoesNotContain(gh.Calls[0], a => a.StartsWith("merged:>=2026-08-28 merged:<=", StringComparison.Ordinal));
        Assert.Contains(gh.Calls[1], a => a.StartsWith("merged:>=2026-08-28 merged:<=", StringComparison.Ordinal));

        var rows = await ReadLedgerAsync();
        Assert.Equal(LedgerBackfillCommand.PullRequestPageSize + 2, rows.Count);
        Assert.Single(rows, r => r.PullRequest == "461");
    }

    /// <summary>
    /// The cap stops the walk, and the walk stops AT A PAGE BOUNDARY rather than mid-page — which is
    /// why the run's own report says "at or past" the cap rather than naming it as a limit. Six full
    /// pages are scripted and only five are asked for.
    /// <para>
    /// The paging test above cannot see this: two pages never reach
    /// <see cref="LedgerBackfillCommand.MaxPullRequests"/>, so an edit to the loop's bound — <c>&lt;=</c>
    /// instead of <c>&lt;</c>, or the check moved after the fetch — would leave it green while the walk
    /// ran on forever against a busy repository.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_merged_pr_walk_stops_at_the_first_page_boundary_past_the_cap()
    {
        var pageCount = (LedgerBackfillCommand.MaxPullRequests / LedgerBackfillCommand.PullRequestPageSize) + 1;
        var gh = new ScriptedGh(
            [.. Enumerable.Range(0, pageCount).Select(page => Page(
                Enumerable.Range(0, LedgerBackfillCommand.PullRequestPageSize)
                    .Select(i => 1000 - ((page * LedgerBackfillCommand.PullRequestPageSize) + i))))]);

        await RunAsync(gh: gh);

        Assert.Equal(pageCount - 1, gh.Calls.Count);
        Assert.Equal(LedgerBackfillCommand.MaxPullRequests, (await ReadLedgerAsync()).Count);
    }

    /// <summary>
    /// One page of merged PRs, numbered as given and merged one hour apart descending — so the oldest
    /// entry of a page is a real, distinct <c>mergedAt</c> the cursor can advance to.
    /// </summary>
    private static string Page(IEnumerable<int> numbers) =>
        "[" + string.Join(
            ',',
            numbers.Select(n =>
                $$"""{"number":{{n}},"headRefName":"branch-{{n}}","mergedAt":"{{new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(n):yyyy-MM-ddTHH:mm:ssZ}}"}""")) + "]";

    private async Task<string> RunAsync(LedgerBackfillOptions? options = null, IGhCliRunner? gh = null)
    {
        var writer = new StringWriter();
        var exitCode = await LedgerBackfillCommand.ExecuteAsync(
            options ?? new LedgerBackfillOptions(RoomsRoot: RoomsRoot),
            writer,
            gh ?? new StubGh("[]"),
            LedgerDirectory,
            (_, _) => Task.FromResult<RepositoryIdentity?>(Repository),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        return writer.ToString();
    }

    private Task<IReadOnlyList<CostLedgerEntry>> ReadLedgerAsync() =>
        CostLedgerStore.ReadAllAsync(LedgerFilePath, TestContext.Current.CancellationToken);

    /// <summary>
    /// A room exactly as production leaves one: a real <c>flow.jsonl</c> carrying the accepted request
    /// and the start/exit pair <c>BuildEntries</c> needs, plus the captured stdout its tokens come from.
    /// </summary>
    private async Task WriteSettledRoomAsync(
        string roomName, string executionId, string worker = "implement", bool truncated = false)
    {
        var roomDirectoryPath = Path.Combine(RoomsRoot, roomName);
        Directory.CreateDirectory(roomDirectoryPath);

        var id = new ExecutionId(executionId);
        await using (var writer = new FlowEventLogWriter(Path.Combine(roomDirectoryPath, BatonPaths.FlowLogFileName)))
        {
            await writer.AppendAsync(
                new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                    id,
                    new WorkflowId("wf-backfill"),
                    new StepId(worker),
                    worker,
                    Inputs: [],
                    Outputs: [],
                    Timeout: TimeSpan.FromSeconds(30),
                    Environment: [],
                    UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
                    Adapter: "claude",
                    Model: "claude-opus-5")),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(id, Pid: 1), TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new CoreEvent.ExecutionExited(id, 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(id), TestContext.Current.CancellationToken);
        }

        var outputDirectory = ArtifactManager.ResolveOutputDirectory(
            Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName), id);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName),
            ClaudeAssistantUsageLine + "\n" + ClaudeTerminalLine + "\n",
            TestContext.Current.CancellationToken);
        if (truncated)
        {
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutTruncationMarkerFileName),
                "rolled over",
                TestContext.Current.CancellationToken);
        }
    }

    private static void WriteVerdict(string roomDirectoryPath, string executionId, string json) =>
        WriteArtifact(roomDirectoryPath, executionId, CostLedgerStore.VerdictOutputName, json);

    private static void WriteDeliveryBranch(string roomDirectoryPath, string executionId, string branch) =>
        WriteArtifact(roomDirectoryPath, executionId, DeliveryReferenceOutputNames.Branch, branch);

    private static void WriteArtifact(string roomDirectoryPath, string executionId, string fileName, string content)
    {
        var outputDirectory = ArtifactManager.ResolveOutputDirectory(
            Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName), new ExecutionId(executionId));
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, fileName), content);
    }

    /// <summary>
    /// A <c>bindings.json</c> carrying one worker's <c>--label</c>, serialized from the real
    /// <see cref="WorkerBindingConfigEntry"/> rather than hand-written JSON — so a rename on that record
    /// breaks this fixture at compile time instead of leaving it silently unparseable and the assertion
    /// passing against an empty label dictionary.
    /// </summary>
    private static void WriteBindings(string roomDirectoryPath, string worker, string label) =>
        File.WriteAllText(
            Path.Combine(roomDirectoryPath, "bindings.json"),
            JsonSerializer.Serialize(new Dictionary<string, WorkerBindingConfigEntry>
            {
                [worker] = new WorkerBindingConfigEntry(
                    "claude",
                    new WorkerContract(worker, [], [new ProducedOutput("out")], []),
                    "echo unused",
                    TimeSpan.FromSeconds(30),
                    Label: label),
            }));

    /// <summary>
    /// The <c>gh</c> double (#734's own seam). It answers every invocation with the same canned result:
    /// this command makes exactly one <c>gh</c> call, so a per-argument script would assert nothing the
    /// tests above do not already assert about the rows it produced.
    /// </summary>
    private sealed class StubGh(string stdout, bool started = true, int exitCode = 0, string stderr = "") : IGhCliRunner
    {
        public Task<GhCliResult> RunAsync(
            string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken) =>
            Task.FromResult(new GhCliResult(started, exitCode, stdout, stderr));
    }

    /// <summary>
    /// A <c>gh</c> double that answers each successive call with the next scripted page and records the
    /// arguments it was given — which is what makes the cursor assertable. Past the last page it answers
    /// with an empty array, so a walk that failed to terminate would run out of script rather than hang
    /// the test.
    /// </summary>
    private sealed class ScriptedGh(params string[] pages) : IGhCliRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<GhCliResult> RunAsync(
            string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            var index = Calls.Count;
            Calls.Add(args);
            return Task.FromResult(new GhCliResult(
                Started: true, ExitCode: 0, index < pages.Length ? pages[index] : "[]", string.Empty));
        }
    }
}
