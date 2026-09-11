using Baton.Cli.Daemon;
using Baton.Cli.Mcp;
using Baton.Domain;
using Baton.Status;
using Baton.Store;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #734: <see cref="DeliveryPoller"/>'s room-level poll. Fixtures use the terminal-sentinel fast path
/// (<see cref="TerminalSentinelWriter.WriteAsync"/>) to declare a room's resolved outputs directly,
/// the same shortcut <c>FleetStatusToolTests.TerminalFastPath_...</c> already uses -- delivery polling
/// deliberately continues after a room's own workflow goes Terminal, so a terminal fixture is the
/// realistic shape, not a simplification.
/// </summary>
public sealed class DeliveryPollerTests : IDisposable
{
    private readonly string _tempHome;
    private readonly string _projectRoot;
    private readonly IDisposable _scope;

    public DeliveryPollerTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"baton-delivery-poller-home-{Guid.NewGuid():N}");
        _projectRoot = Path.Combine(Path.GetTempPath(), $"baton-delivery-poller-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        Directory.CreateDirectory(_projectRoot);
        _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = _tempHome });
    }

    public void Dispose()
    {
        _scope.Dispose();
        if (Directory.Exists(_tempHome))
        {
            DirectoryCleanup.DeleteRecursively(_tempHome);
        }

        if (Directory.Exists(_projectRoot))
        {
            DirectoryCleanup.DeleteRecursively(_projectRoot);
        }
    }

    private async Task<FleetStatusTool.DiscoveredRoom> CreateRoomAsync(
        string? prContent, string? branch = "734-lane", string? projectRoot = "__default__")
    {
        var roomDir = Path.Combine(_tempHome, "rooms", $"room-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDir);

        var outputs = new List<string>();
        if (prContent is not null)
        {
            var prPath = Path.Combine(roomDir, DeliveryReferenceOutputNames.PullRequest);
            File.WriteAllText(prPath, prContent);
            outputs.Add(prPath);
        }

        if (branch is not null)
        {
            var branchPath = Path.Combine(roomDir, DeliveryReferenceOutputNames.Branch);
            File.WriteAllText(branchPath, branch);
            outputs.Add(branchPath);
        }

        var sentinel = new WorkflowStatusView("Succeeded", [], outputs, null, null);
        await TerminalSentinelWriter.WriteAsync(roomDir, sentinel, TestContext.Current.CancellationToken);

        return new FleetStatusTool.DiscoveredRoom(roomDir, ReferenceEquals(projectRoot, "__default__") ? _projectRoot : projectRoot);
    }

    private static async Task<IReadOnlyList<FlowEvent>> ReadEventsAsync(FleetStatusTool.DiscoveredRoom room)
    {
        var logPath = Path.Combine(room.RoomDir, BatonPaths.FlowLogFileName);
        return await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
    }

    private const string OpenJson = """{"state":"OPEN","statusCheckRollup":[]}""";

    private const string ChecksRedJson = """
        {"state":"OPEN","statusCheckRollup":[
            {"status":"COMPLETED","conclusion":"SUCCESS"},
            {"status":"COMPLETED","conclusion":"FAILURE"}
        ]}
        """;

    private const string ChecksGreenJson = """
        {"state":"OPEN","statusCheckRollup":[
            {"status":"COMPLETED","conclusion":"SUCCESS"},
            {"status":"COMPLETED","conclusion":"SUCCESS"}
        ]}
        """;

    private const string MergedJson = """
        {"state":"MERGED","statusCheckRollup":[
            {"status":"COMPLETED","conclusion":"SUCCESS"}
        ]}
        """;

    private const string ClosedUnmergedJson = """{"state":"CLOSED","statusCheckRollup":[]}""";

    /// <summary>#734 review finding: fixtures for the second `statusCheckRollup` shape `DeliveryPoller.ParsePrView`'s own remarks name.</summary>
    private const string LegacyStatusContextPendingJson = """
        {"state":"OPEN","statusCheckRollup":[
            {"state":"PENDING","context":"ci/jenkins"}
        ]}
        """;

    private const string LegacyStatusContextFailureJson = """
        {"state":"OPEN","statusCheckRollup":[
            {"state":"FAILURE","context":"ci/jenkins"}
        ]}
        """;

    private const string LegacyStatusContextSuccessJson = """
        {"state":"OPEN","statusCheckRollup":[
            {"state":"SUCCESS","context":"ci/jenkins"}
        ]}
        """;

    [Fact]
    public async Task Open_then_checks_red_then_checks_green_then_merged_lands_each_fact_once_and_stops_polling()
    {
        var room = await CreateRoomAsync("1799");
        var gh = new FakeGhCliRunner();
        var poller = new DeliveryPoller(gh);
        var sink = new StringWriter();

        gh.NextResult = new GhCliResult(Started: true, ExitCode: 0, OpenJson, string.Empty);
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);
        var afterOpen = await ReadEventsAsync(room);
        Assert.Single(afterOpen);
        Assert.IsType<FlowEvent.DeliveryPrOpened>(afterOpen[0]);

        gh.NextResult = new GhCliResult(Started: true, ExitCode: 0, ChecksRedJson, string.Empty);
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);
        var afterRed = await ReadEventsAsync(room);
        Assert.Equal(2, afterRed.Count);
        Assert.IsType<FlowEvent.DeliveryChecksRed>(afterRed[1]);

        gh.NextResult = new GhCliResult(Started: true, ExitCode: 0, ChecksGreenJson, string.Empty);
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);
        var afterGreen = await ReadEventsAsync(room);
        Assert.Equal(3, afterGreen.Count);
        Assert.IsType<FlowEvent.DeliveryChecksGreen>(afterGreen[2]);

        gh.NextResult = new GhCliResult(Started: true, ExitCode: 0, MergedJson, string.Empty);
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);
        var afterMerged = await ReadEventsAsync(room);
        Assert.Equal(4, afterMerged.Count);
        var merged = Assert.IsType<FlowEvent.DeliveryMerged>(afterMerged[3]);
        Assert.True(merged.Merged);

        // Each of the four facts landed exactly once.
        Assert.Single(afterMerged.OfType<FlowEvent.DeliveryPrOpened>());
        Assert.Single(afterMerged.OfType<FlowEvent.DeliveryChecksRed>());
        Assert.Single(afterMerged.OfType<FlowEvent.DeliveryChecksGreen>());
        Assert.Single(afterMerged.OfType<FlowEvent.DeliveryMerged>());

        // Polling stops: a further tick never calls gh again and never appends another event.
        var callsBefore = gh.CallCount;
        gh.NextResult = new GhCliResult(Started: true, ExitCode: 0, MergedJson, string.Empty);
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);
        Assert.Equal(callsBefore, gh.CallCount);
        Assert.Equal(4, (await ReadEventsAsync(room)).Count);
    }

    [Fact]
    public async Task PR_closed_without_merging_is_recorded_once_as_DeliveryMerged_with_Merged_false()
    {
        var room = await CreateRoomAsync("55");
        var gh = new FakeGhCliRunner { NextResult = new GhCliResult(Started: true, ExitCode: 0, ClosedUnmergedJson, string.Empty) };
        var poller = new DeliveryPoller(gh);

        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, new StringWriter());

        var events = await ReadEventsAsync(room);
        var merged = Assert.Single(events.OfType<FlowEvent.DeliveryMerged>());
        Assert.False(merged.Merged);

        // Terminal: a further tick never calls gh again.
        var callsBefore = gh.CallCount;
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, new StringWriter());
        Assert.Equal(callsBefore, gh.CallCount);
    }

    /// <summary>The control for the green/red arms above: a legacy commit-status rollup that never actually reported must never be read as green.</summary>
    [Fact]
    public async Task A_pending_legacy_commit_status_check_is_not_read_as_green()
    {
        var room = await CreateRoomAsync("9");
        var gh = new FakeGhCliRunner { NextResult = new GhCliResult(Started: true, ExitCode: 0, LegacyStatusContextPendingJson, string.Empty) };
        var poller = new DeliveryPoller(gh);

        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, new StringWriter());

        var events = await ReadEventsAsync(room);
        Assert.Empty(events.OfType<FlowEvent.DeliveryChecksGreen>());
        Assert.Empty(events.OfType<FlowEvent.DeliveryChecksRed>());
    }

    [Fact]
    public async Task A_failing_legacy_commit_status_check_is_read_as_red()
    {
        var room = await CreateRoomAsync("9");
        var gh = new FakeGhCliRunner { NextResult = new GhCliResult(Started: true, ExitCode: 0, LegacyStatusContextFailureJson, string.Empty) };
        var poller = new DeliveryPoller(gh);

        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, new StringWriter());

        var events = await ReadEventsAsync(room);
        Assert.Single(events.OfType<FlowEvent.DeliveryChecksRed>());
    }

    [Fact]
    public async Task A_succeeded_legacy_commit_status_check_is_read_as_green()
    {
        var room = await CreateRoomAsync("9");
        var gh = new FakeGhCliRunner { NextResult = new GhCliResult(Started: true, ExitCode: 0, LegacyStatusContextSuccessJson, string.Empty) };
        var poller = new DeliveryPoller(gh);

        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, new StringWriter());

        var events = await ReadEventsAsync(room);
        Assert.Single(events.OfType<FlowEvent.DeliveryChecksGreen>());
    }

    [Fact]
    public async Task Missing_gh_logs_once_and_records_no_events()
    {
        var room = await CreateRoomAsync("7");
        var gh = new FakeGhCliRunner { NextResult = new GhCliResult(Started: false, ExitCode: -1, string.Empty, "gh was not found on PATH.") };
        var poller = new DeliveryPoller(gh);
        var sink = new StringWriter();

        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);

        var lines = sink.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);

        var logPath = Path.Combine(room.RoomDir, BatonPaths.FlowLogFileName);
        Assert.False(File.Exists(logPath));
    }

    /// <summary>
    /// #734 review finding: a per-room `gh` failure (bad PR number, not authenticated) must not share
    /// the process-wide "gh is missing" latch -- otherwise the first bad room permanently silences the
    /// genuine warning for every room that comes after it. Two different rooms, two different failure
    /// shapes, both logged.
    /// </summary>
    [Fact]
    public async Task A_per_room_gh_failure_does_not_silence_the_gh_missing_warning_for_a_different_room()
    {
        var badRoom = await CreateRoomAsync("404");
        var missingGhRoom = await CreateRoomAsync("7");
        var gh = new FakeGhCliRunner();
        var poller = new DeliveryPoller(gh);
        var sink = new StringWriter();

        gh.NextResult = new GhCliResult(Started: true, ExitCode: 1, string.Empty, "GraphQL: Could not resolve to a PullRequest");
        await poller.PollRoomAsync(badRoom, TestContext.Current.CancellationToken, sink);

        gh.NextResult = new GhCliResult(Started: false, ExitCode: -1, string.Empty, "gh was not found on PATH.");
        await poller.PollRoomAsync(missingGhRoom, TestContext.Current.CancellationToken, sink);

        var lines = sink.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, l => l.Contains("PullRequest", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("not found on PATH", StringComparison.Ordinal));

        // And the per-room failure logs again on the very next tick -- it is not latched.
        gh.NextResult = new GhCliResult(Started: true, ExitCode: 1, string.Empty, "GraphQL: Could not resolve to a PullRequest");
        await poller.PollRoomAsync(badRoom, TestContext.Current.CancellationToken, sink);
        var linesAfter = sink.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, linesAfter.Length);
    }

    [Fact]
    public async Task A_room_with_no_declared_delivery_output_is_never_polled()
    {
        var room = await CreateRoomAsync(prContent: null, branch: null);
        var gh = new FakeGhCliRunner();
        var poller = new DeliveryPoller(gh);

        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, new StringWriter());

        Assert.Equal(0, gh.CallCount);
        Assert.False(File.Exists(Path.Combine(room.RoomDir, BatonPaths.FlowLogFileName)));
    }

    /// <summary>The discriminating control: a branch declared with no PR number resolves a reference but still never starts polling -- distinct from declaring nothing at all.</summary>
    [Fact]
    public async Task A_room_declaring_only_a_branch_with_no_PR_number_is_never_polled()
    {
        var room = await CreateRoomAsync(prContent: null, branch: "wip-branch");
        var gh = new FakeGhCliRunner();
        var poller = new DeliveryPoller(gh);

        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, new StringWriter());

        Assert.Equal(0, gh.CallCount);
    }

    /// <summary>
    /// #734 review finding: a bare PR number has no repo context without the room's own spec/baton.md §8 project
    /// root, and must be skipped with a logged line rather than silently -- once per room.
    /// </summary>
    [Fact]
    public async Task A_bare_pr_number_with_no_registered_project_root_is_skipped_and_logged_once()
    {
        var room = await CreateRoomAsync("123", projectRoot: null);
        var gh = new FakeGhCliRunner();
        var poller = new DeliveryPoller(gh);
        var sink = new StringWriter();

        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);

        Assert.Equal(0, gh.CallCount);
        var lines = sink.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
    }

    /// <summary>#734 review finding: a URL reference needs no registered project root -- unlike a bare number, which the test above covers.</summary>
    [Fact]
    public async Task A_full_pr_url_reference_polls_successfully_with_no_registered_project_root()
    {
        var room = await CreateRoomAsync("https://github.com/philipreese/baton/pull/1799", projectRoot: null);
        var gh = new FakeGhCliRunner { NextResult = new GhCliResult(Started: true, ExitCode: 0, OpenJson, string.Empty) };
        var poller = new DeliveryPoller(gh);

        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, new StringWriter());

        Assert.Equal(1, gh.CallCount);
        Assert.Contains("https://github.com/philipreese/baton/pull/1799", gh.LastArgs);
        var events = await ReadEventsAsync(room);
        var opened = Assert.Single(events.OfType<FlowEvent.DeliveryPrOpened>());
        Assert.Equal(1799, opened.PullRequestNumber);
    }

    [Fact]
    public async Task A_malformed_queue_does_not_suppress_existing_room_reconciliation()
    {
        var room = await CreateRoomAsync("https://github.com/philipreese/baton/pull/1799", projectRoot: null);
        Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.QueueFile)!);
        File.WriteAllText(BatonPaths.QueueFile, "{");
        var gh = new FakeGhCliRunner { NextResult = new GhCliResult(Started: true, ExitCode: 0, OpenJson, string.Empty) };
        var poller = new DeliveryPoller(gh);

        await poller.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, gh.CallCount);
        var opened = Assert.Single((await ReadEventsAsync(room)).OfType<FlowEvent.DeliveryPrOpened>());
        Assert.Equal(1799, opened.PullRequestNumber);
    }

    private sealed class FakeGhCliRunner : IGhCliRunner
    {
        public GhCliResult NextResult { get; set; } = new(Started: true, ExitCode: 0, "{}", string.Empty);

        public int CallCount { get; private set; }

        public string? LastWorkingDirectory { get; private set; }

        public IReadOnlyList<string> LastArgs { get; private set; } = [];

        public Task<GhCliResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            CallCount++;
            LastWorkingDirectory = workingDirectory;
            LastArgs = args;
            return Task.FromResult(NextResult);
        }
    }
}
