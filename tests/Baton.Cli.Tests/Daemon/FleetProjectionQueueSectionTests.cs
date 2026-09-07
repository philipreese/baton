using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Queue;
using Baton.Status;
using Baton.Store;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #1912 slice 1: <see cref="FleetProjectionWriter"/> carries the conductor's rows under the
/// projection's <c>queue</c> key. What <see cref="Baton.Tests"/>' <c>QueueBoardTests</c> covers is the
/// projection's own arms; this covers the I/O around it — that a fixture queue on disk reaches the
/// file, and that each way there is no board gets its own answer rather than one blank space.
/// </summary>
/// <remarks>
/// Same per-test isolated <c>BATON_HOME</c> pattern as <c>FleetProjectionWriterTests</c>, which is what
/// makes <see cref="BatonPaths.QueueFile"/> point at the fixture instead of the operator's own queue.
/// </remarks>
public sealed class FleetProjectionQueueSectionTests : IDisposable
{
    private readonly string _tempHome;
    private readonly IDisposable _scope;

    public FleetProjectionQueueSectionTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"baton-queue-section-test-home-{Guid.NewGuid():N}");
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

    private static QueueItem Item(string tag, WorkStage stage, int issue, int? pr = null) => new()
    {
        Tag = tag,
        Role = WorkStages.IsTerminal(stage) ? "implement" : WorkStages.RoleFor(stage),
        Stage = stage,
        Issue = issue,
        PullRequest = pr,
        Workspace = BatonPaths.Root,
        SpecFile = Path.Combine(BatonPaths.QueueSpecsDirectory, $"{tag}.md"),
    };

    /// <summary>
    /// Writes the fixture queue AND the brief each item names. Both, because they are one fact from the
    /// board's point of view: an item whose brief is gone reports <c>brief-missing</c> ahead of any
    /// scheduling reason, so a fixture that skipped the file would be testing that arm by accident.
    /// </summary>
    private static async Task WriteQueueAsync(params QueueItem[] items)
    {
        Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
        foreach (var item in items)
        {
            await File.WriteAllTextAsync(item.SpecFile, $"# {item.Tag}");
        }

        await QueueStore.MutateAsync(
            BatonPaths.QueueFile, s => s with { Items = items }, CancellationToken.None);
    }

    private static async Task<JsonElement> BuildAsync(double? freeGb = 5.0)
    {
        var json = await new FleetProjectionWriter(() => freeGb)
            .BuildProjectionJsonAsync(CancellationToken.None, TextWriter.Null);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public async Task A_fixture_queue_reaches_the_projections_queue_section()
    {
        await WriteQueueAsync(
            Item("1912-a", WorkStage.Implement, 1912),
            Item("1912-b", WorkStage.Review, 1912, pr: 2028),
            Item("other", WorkStage.Fix, 1600, pr: 2030));

        var root = await BuildAsync();

        var queue = root.GetProperty("queue");
        Assert.Equal(QueueSettings.DefaultMaxLiveWeight, queue.GetProperty("slots").GetProperty("cap").GetDouble());
        Assert.Equal(5.0, queue.GetProperty("slots").GetProperty("freeGb").GetDouble());

        var pending = queue.GetProperty("pending").EnumerateArray().Select(p => p.GetProperty("tag").GetString()).ToList();
        Assert.Equal(["1912-a", "1912-b", "other"], pending);

        // The two items on issue 1912 are the twins; the third is not marked.
        var rows = queue.GetProperty("pending").EnumerateArray().ToList();
        Assert.Equal(1912, rows[0].GetProperty("twinIssue").GetInt32());
        Assert.Equal(1912, rows[1].GetProperty("twinIssue").GetInt32());
        Assert.False(rows[2].TryGetProperty("twinIssue", out _));

        var prs = queue.GetProperty("pullRequests").EnumerateArray().Select(p => p.GetProperty("pr").GetInt32()).ToList();
        Assert.Equal([2028, 2030], prs);
    }

    [Fact]
    public async Task An_unmeasured_free_memory_reading_leaves_the_field_absent_rather_than_zero()
    {
        await WriteQueueAsync(Item("a", WorkStage.Implement, 1));

        var slots = (await BuildAsync(freeGb: null)).GetProperty("queue").GetProperty("slots");

        Assert.False(slots.TryGetProperty("freeGb", out _));

        // Control: the floor is still reported, so the assertion above is about the READING being
        // absent and not about the whole slots block dropping out.
        Assert.True(slots.TryGetProperty("floorGb", out _));
    }

    /// <summary>
    /// #1912 fix round, state 1 of three: no queue file. The <c>queue</c> key stays absent for the
    /// reason <c>FleetProjectionWriter.BuildQueueSectionAsync</c>'s remarks give; what is new is that
    /// the reason key now says WHICH absence this is.
    /// </summary>
    [Fact]
    public async Task A_machine_that_has_never_used_the_queue_gets_no_queue_key_and_says_so()
    {
        Assert.False(File.Exists(BatonPaths.QueueFile));

        var root = await BuildAsync();
        Assert.False(root.TryGetProperty("queue", out _));
        Assert.Equal(
            FleetProjectionWriter.QueueUnavailableNoQueueFile,
            root.GetProperty(FleetProjectionWriter.QueueUnavailableReasonKey).GetString());

        // Control, opposite polarity: once a queue file exists the key appears and the reason goes
        // away -- so the absence above is the missing-file arm and not the section never being written.
        await WriteQueueAsync(Item("a", WorkStage.Implement, 1));
        var withQueue = await BuildAsync();
        Assert.True(withQueue.TryGetProperty("queue", out _));
        Assert.False(withQueue.TryGetProperty(FleetProjectionWriter.QueueUnavailableReasonKey, out _));
    }

    /// <summary>
    /// #1912 fix round, state 2 of three: the section threw. This is the arm that used to be
    /// indistinguishable from state 1 above, and the one an operator has to act on — what that cost is
    /// <c>FleetProjectionWriter.BuildQueueSectionAsync</c>'s remarks to say.
    /// <para>
    /// State 3 — the mailbox delivery, where <c>pusher.py</c> composes the payload key by key and
    /// carries NEITHER key — is not reachable from this side and is covered on the page:
    /// <c>tools/fleet-glass/glass.selftest.mjs</c>, "the mailbox delivery ... says the rows are
    /// daemon-page-only".
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_queue_section_that_throws_reports_the_reason_rather_than_looking_like_no_queue()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.QueueFile)!);
        await File.WriteAllTextAsync(
            BatonPaths.QueueFile, "{ this is not a queue", TestContext.Current.CancellationToken);

        var diagnostics = new StringWriter();
        var json = await new FleetProjectionWriter(() => 5.0)
            .BuildProjectionJsonAsync(CancellationToken.None, diagnostics);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.False(root.TryGetProperty("queue", out _));

        var reason = root.GetProperty(FleetProjectionWriter.QueueUnavailableReasonKey).GetString();
        Assert.NotNull(reason);
        Assert.NotEqual(FleetProjectionWriter.QueueUnavailableNoQueueFile, reason);

        // The tick survives the section, which is the whole point of the catch this splits -- the
        // writer's remarks are where that trade is argued.
        Assert.True(root.TryGetProperty("rooms", out _));
        Assert.Contains("queue section skipped this tick", diagnostics.ToString());
    }

    [Fact]
    public async Task The_schedulers_own_recorded_wait_reason_is_what_the_candidate_row_carries()
    {
        await WriteQueueAsync(Item("head", WorkStage.Implement, 1));
        await QueueDecisionLedgerStore.AppendAsync(
            new QueueDecisionEntry(
                DateTimeOffset.UtcNow, "head", QueueDecisionEntry.Waited,
                QueueWaitReasons.Token(QueueWaitReason.Memory), LiveWeight: 0, FreeGb: 0.5, FloorGb: 2),
            null, BatonPaths.QueueDecisionLedgerFile, CancellationToken.None);

        var queue = (await BuildAsync()).GetProperty("queue");

        Assert.Equal("memory", queue.GetProperty("pending")[0].GetProperty("reason").GetString());
        Assert.True(queue.TryGetProperty("lastDecisionAt", out _));
    }
}
