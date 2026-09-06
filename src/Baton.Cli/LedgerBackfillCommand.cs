using System.Globalization;
using System.Text.Json;
using Baton.Accounting;
using Baton.Artifacts;
using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// <c>baton ledger backfill</c> (#1901 C2): recovers cost-ledger rows for work that settled before the
/// writer existed, or that never reached the writer at all. Two halves, one exit code.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rooms half reuses <see cref="CostLedgerStore.BuildEntries"/> and nothing else</b>;
/// spec/baton.md §7's backfill section states what sharing that method with the settle site buys.
/// Writing a second builder here is what the issue explicitly rules out, and
/// it is also what would let the two drift. What this half deliberately does NOT recover — the
/// settle-time workspace delivery probe's issue, PR and diff shape — and why, is stated in that same
/// spec section rather than argued twice.
/// </para>
/// <para>
/// <b>Append-only means this can only ADD rows</b>, never repair one — the ledger's own immutability
/// guarantee rather than a limitation of this walk. What that costs in practice, and the one case where
/// it bites, is the same spec section; here it shows up as the counter this command reports instead.
/// </para>
/// <para>
/// <b>Fail-open per unit, never fatal.</b> The posture and the cases it covers are spec/baton.md §7's
/// backfill section; what this type owns is that the report never loses one silently. Exit code 0
/// unless the invocation itself was malformed.
/// </para>
/// <para>
/// Not a <see cref="CommandResult"/>/<see cref="FlowStateReporter"/> command, for the same reason
/// <see cref="LedgerCommand"/> and <see cref="LedgerViewCommand"/> are not: there is no workflow pump
/// here to report on.
/// </para>
/// </remarks>
public static class LedgerBackfillCommand
{
    /// <summary>
    /// The date the merged-PR search starts from when <c>--since</c> is unset — the 2026-08-28 reset
    /// #1901 names, which is the point before which a merged PR belongs to a differently-shaped repo.
    /// A floor exists here and nowhere else because <c>gh</c>'s search qualifier requires one;
    /// spec/baton.md §7's backfill section states what that asymmetry means for the two halves.
    /// </summary>
    public static readonly DateTime DefaultGithubSince = new(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The count at which one run stops asking <c>gh</c> for more merged PRs. Bounded because this is a
    /// network call whose answer grows without limit as the repository ages: an unbounded walk would
    /// page through years of history to write rows a <c>--since</c> window then discards. A run that
    /// hits the cap says so on stdout rather than silently reporting a partial answer as a complete one.
    /// <para>
    /// <b>Not a total, and the difference is up to a page.</b> The check is at the top of
    /// <see cref="CollectMergedPullRequestsAsync"/>'s loop, i.e. at a page boundary, so a run ends
    /// holding as many as <c>MaxPullRequests + PullRequestPageSize - 1</c> — the last page is kept
    /// whole rather than truncated, since dropping part of a page would lose PRs the walk has already
    /// paid for. "At or past the cap" is what the report says for that reason, and pinned by
    /// <c>LedgerBackfillCommandTests.The_merged_pr_walk_stops_at_the_first_page_boundary_past_the_cap</c>.
    /// </para>
    /// </summary>
    internal const int MaxPullRequests = 200;

    /// <summary>
    /// How many PRs one <c>gh pr list</c> asks for. <b>Forty, and it is a measured ceiling rather than
    /// a taste</b> — spec/baton.md §7's backfill section carries the measurement, which is about what
    /// <see cref="PullRequestJsonFields"/>'s <c>commits</c> costs GitHub's GraphQL node budget. Above
    /// this the API refuses the query outright, so the run pages instead (see
    /// <see cref="CollectMergedPullRequestsAsync"/>'s cursor) rather than dropping the commit count or
    /// silently collecting one page and calling it the answer.
    /// </summary>
    internal const int PullRequestPageSize = 40;

    /// <summary>
    /// The <c>gh</c> fields one <c>pr list</c> asks for. Named here rather than inline so
    /// <see cref="MergedPullRequestReader"/> and the request can never ask for different things.
    /// </summary>
    internal const string PullRequestJsonFields =
        "number,headRefName,mergedAt,additions,deletions,changedFiles,commits,reviews,closingIssuesReferences";

    /// <summary>
    /// The repository-identity seam. Shaped like <see cref="RepositoryIdentityResolver"/>'s own methods
    /// so production passes them directly; injectable only so a test can attribute a fixture room
    /// without a git repository under it — resolving for real would key the fixture's rows to whatever
    /// repository the test host happens to be standing in.
    /// </summary>
    internal delegate Task<RepositoryIdentity?> RepositoryProbe(string directoryPath, CancellationToken cancellationToken);

    /// <inheritdoc cref="ExecuteAsync(LedgerBackfillOptions, TextWriter, IGhCliRunner?, string?, RepositoryProbe?, CancellationToken)"/>
    public static Task<int> ExecuteAsync(
        LedgerBackfillOptions options, TextWriter output, CancellationToken cancellationToken = default) =>
        ExecuteAsync(options, output, ghRunner: null, ledgerDirectoryOverride: null, repositoryProbe: null, cancellationToken);

    /// <param name="ghRunner">Defaults to the real <see cref="GhCliRunner"/> — the one seam #734 already owns.</param>
    /// <param name="ledgerDirectoryOverride">
    /// Test seam — production always writes under <c>BatonPaths.CostLedgerFile</c>. A test must never be
    /// one mis-resolved identity away from appending to the operator's real ledger.
    /// </param>
    /// <param name="repositoryProbe">Test seam — see <see cref="RepositoryProbe"/>.</param>
    internal static async Task<int> ExecuteAsync(
        LedgerBackfillOptions options,
        TextWriter output,
        IGhCliRunner? ghRunner,
        string? ledgerDirectoryOverride,
        RepositoryProbe? repositoryProbe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        if (options.Help)
        {
            Write(output, LedgerBackfillOptionsParser.Usage);
            foreach (var line in LedgerBackfillOptionsParser.HelpLines)
            {
                Write(output, line);
            }

            return 0;
        }

        var probe = repositoryProbe ?? RepositoryIdentityResolver.TryResolveAsync;
        var report = new BackfillReport(options.DryRun);

        var branchByRoom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Dictionary<string, List<CostLedgerEntry>>(StringComparer.OrdinalIgnoreCase);

        await WalkRoomsAsync(options, probe, ledgerDirectoryOverride, pending, branchByRoom, report, cancellationToken)
            .ConfigureAwait(false);
        await CollectMergedPullRequestsAsync(
                options, probe, ghRunner ?? new GhCliRunner(), ledgerDirectoryOverride, pending, branchByRoom, report,
                cancellationToken)
            .ConfigureAwait(false);

        // PLAN, then DISCLOSE, then WRITE -- and that order is the operator's ruling of 2026-09-05
        // (#1931 review HIGH), not an implementation detail. Why the order is load-bearing rather than
        // arbitrary -- what an after-the-append disclosure would be describing, and why nothing can
        // repair it -- is spec/baton.md §7's backfill section, under the working-directory fallback.
        var planned = await PlanAsync(pending, report, cancellationToken).ConfigureAwait(false);

        report.Print(output);

        if (!options.DryRun)
        {
            await CommitAsync(planned, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// Every room this run will read. With no <c>--rooms-root</c> this is
    /// <c>BatonPaths.Rooms</c> UNIONED with the room registry, exactly as
    /// <c>LedgerCommand</c>'s own rebuild walk does. The union is not belt-and-braces: spec/baton.md §8
    /// is the record of why a registry exists at all, and listing only the default root would silently
    /// miss precisely the rooms it was built for. An explicit <c>--rooms-root</c> is the operator saying
    /// "these and only these", so it does not union.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ResolveRoomDirectoriesAsync(
        string? roomsRoot, CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(BatonPaths.RecordKeyComparer);

        var root = roomsRoot ?? BatonPaths.Rooms;
        if (Directory.Exists(root))
        {
            foreach (var directory in Directory.GetDirectories(root))
            {
                paths.Add(BatonPaths.RecordKey(directory));
            }
        }

        if (roomsRoot is null)
        {
            var registryEntries = await RoomRegistryStore
                .ReadDistinctByRoomAsync(BatonPaths.RoomRegistryFile, cancellationToken).ConfigureAwait(false);
            foreach (var entry in registryEntries)
            {
                paths.Add(entry.RoomPath);
            }
        }

        return paths.Where(Directory.Exists).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task WalkRoomsAsync(
        LedgerBackfillOptions options,
        RepositoryProbe probe,
        string? ledgerDirectoryOverride,
        Dictionary<string, List<CostLedgerEntry>> pending,
        Dictionary<string, string> branchByRoom,
        BackfillReport report,
        CancellationToken cancellationToken)
    {
        // The room half's window is the view command's, not a second one: LedgerQuery already decides
        // what --since means for a row, including that an undated row is excluded rather than assumed in.
        var window = new LedgerQuery(Since: options.Since);

        // Read ONCE for the whole walk, not once per room: this is the same file
        // RepositoryIdentityResolver.TryResolveForRoomAsync opens per call, and a fleet with hundreds of
        // rooms would otherwise take the registry's lock hundreds of times for one unchanging answer.
        var registrations = await RoomRegistryStore
            .ReadDistinctByRoomAsync(BatonPaths.RoomRegistryFile, cancellationToken).ConfigureAwait(false);

        foreach (var roomDirectoryPath in await ResolveRoomDirectoriesAsync(options.RoomsRoot, cancellationToken)
            .ConfigureAwait(false))
        {
            report.RoomsWalked++;

            var flowLogPath = Path.Combine(roomDirectoryPath, BatonPaths.FlowLogFileName);
            if (!File.Exists(flowLogPath))
            {
                report.UnattributedRoom(
                    roomDirectoryPath, $"no {BatonPaths.FlowLogFileName} -- nothing was ever recorded here");
                continue;
            }

            // The room's declared delivery branch, read whether or not the room yields a row: the
            // GitHub half joins on it, and a room whose executions are all already ledgered is still
            // the room that produced the PR.
            if (TryReadDeliveryBranch(roomDirectoryPath) is { Length: > 0 } branch)
            {
                branchByRoom[branch] = BatonPaths.RecordKey(roomDirectoryPath);
            }

            var (repository, identitySource) = await ResolveRoomRepositoryAsync(
                roomDirectoryPath, registrations, probe, cancellationToken).ConfigureAwait(false);
            if (repository is null)
            {
                report.UnattributedRoom(
                    roomDirectoryPath,
                    "no repository identity (git found no origin remote or repository for its recorded project root, "
                        + "nor for this working directory) -- the ledger is keyed by repository, so there is no file "
                        + "to write to");
                continue;
            }

            if (identitySource == RepositoryIdentitySource.WorkingDirectory)
            {
                report.RoomsKeyedByWorkingDirectory++;
            }

            IReadOnlyList<LogEntry> entries;
            try
            {
                entries = await new FlowEventLogReader(flowLogPath)
                    .ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BatonFlowException)
            {
                report.UnattributedRoom(
                    roomDirectoryPath, $"its {BatonPaths.FlowLogFileName} could not be read: {ex.Message}");
                continue;
            }

            // Both bindings stamps in one parse -- the label (#1499) and #1848's runway-override
            // reason, which is a pure read of the same file rather than the settle-time workspace
            // probe this half genuinely cannot recover (spec/baton.md §7's backfill section states
            // which is which). Fail-open: an unreadable bindings file costs the stamps, not the rows.
            var stamps = await RoomBindingStamps.ReadForRoomAsync(roomDirectoryPath, cancellationToken)
                .ConfigureAwait(false);
            var rows = CostLedgerStore
                .BuildEntries(
                    entries,
                    roomDirectoryPath,
                    repository,
                    runwayOverrideReasonByWorker: stamps.RunwayOverrideReasonByWorker,
                    labelByWorker: stamps.LabelByWorker,
                    identitySource: identitySource,
                    modelResolvedByWorker: stamps.ModelResolvedByWorker)
                .Where(window.TimeMatches)
                .ToList();

            if (rows.Count == 0)
            {
                report.UnattributedRoom(
                    roomDirectoryPath,
                    options.Since is null
                        ? "no settled execution attempt (an execution missing a start or an exit has no wall clock "
                            + "to derive and is absent rather than reported as zero)"
                        : "no settled execution attempt inside the --since window");
                continue;
            }

            Accumulate(pending, LedgerFilePathFor(repository, ledgerDirectoryOverride), rows);
        }
    }

    /// <summary>
    /// A room's repository identity: its recorded project root first (which is what
    /// <see cref="RepositoryIdentityResolver.TryResolveForRoomAsync"/> reads, and the only fact that
    /// keys a room to the repository the work was actually done in), falling back to the working
    /// directory. Separate from that method rather than a call to it because the two fall back at
    /// different widths, which its own remarks set out — this one also takes the fallback when a
    /// recorded root RESOLVES to nothing, and a settle deliberately does not.
    /// <para>
    /// <b>The fallback is deliberately wider here than at a settle site</b>, and spec/baton.md §7's
    /// backfill section is the record of exactly how much wider, the measurement that forced it, and
    /// the exposure it accepts — which is the one
    /// <see cref="RepositoryIdentityResolver.TryResolveForRoomAsync"/>'s own remarks warn about, so read
    /// those two together rather than this comment alone. What this method owns is the
    /// <see cref="RepositoryIdentitySource"/> it returns, which is how the run reports the exposure
    /// instead of hiding it — on stdout as a count, and on every row it produces as
    /// <see cref="CostLedgerEntry.IdentitySource"/>, which is the half that survives the run.
    /// </para>
    /// </summary>
    private static async Task<(RepositoryIdentity? Identity, RepositoryIdentitySource Source)> ResolveRoomRepositoryAsync(
        string roomDirectoryPath,
        IReadOnlyList<RoomRegistryEntry> registrations,
        RepositoryProbe probe,
        CancellationToken cancellationToken)
    {
        var recordedRoomPath = BatonPaths.RecordKey(roomDirectoryPath);
        var projectRoot = registrations
            .FirstOrDefault(entry => BatonPaths.RecordKeyComparer.Equals(entry.RoomPath, recordedRoomPath))
            ?.ProjectRoot;

        if (projectRoot is { Length: > 0 })
        {
            if (await probe(projectRoot, cancellationToken).ConfigureAwait(false) is { } fromProjectRoot)
            {
                return (fromProjectRoot, RepositoryIdentitySource.RecordedRoot);
            }
        }

        return (
            await probe(Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false),
            RepositoryIdentitySource.WorkingDirectory);
    }

    /// <summary>
    /// The branch a room declared it delivered, from the <c>delivery-branch.txt</c> output #734's
    /// poller already recognises (<see cref="DeliveryReferenceOutputNames.Branch"/>) — the one durable
    /// key spec/baton.md §7's backfill section settles on — including how often it currently finds
    /// anything, and why it stays this key rather than a host-specific substitute. <see langword="null"/>
    /// when the room's workflow declares no such output, which is the ordinary case for a lane that is
    /// not a delivery lane; those PRs simply land unattributed and are reported as such.
    /// </summary>
    private static string? TryReadDeliveryBranch(string roomDirectoryPath)
    {
        var artifactsRoot = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
        if (!Directory.Exists(artifactsRoot))
        {
            return null;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                artifactsRoot, DeliveryReferenceOutputNames.Branch, SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(path).Trim();
                if (text.Length > 0)
                {
                    return text;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fail open, per this type's contract: the room loses its join key and nothing else.
            return null;
        }

        return null;
    }

    private static async Task CollectMergedPullRequestsAsync(
        LedgerBackfillOptions options,
        RepositoryProbe probe,
        IGhCliRunner ghRunner,
        string? ledgerDirectoryOverride,
        Dictionary<string, List<CostLedgerEntry>> pending,
        Dictionary<string, string> branchByRoom,
        BackfillReport report,
        CancellationToken cancellationToken)
    {
        var workingDirectory = Environment.CurrentDirectory;
        var repository = await probe(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (repository is null)
        {
            report.GithubIncompleteReason =
                $"no repository identity for '{workingDirectory}', so there is no ledger file to write PR rows to";
            return;
        }

        var since = options.Since ?? DefaultGithubSince;
        report.GithubSince = since;

        // Keyed by number, because the pages OVERLAP by construction: the cursor below is a `merged:<=`
        // bound, which is inclusive, so the oldest PR of one page is the newest of the next. Why the
        // bound is inclusive and the overlap is deduped here, rather than the other way round, is
        // spec/baton.md §7's backfill section.
        var byNumber = new Dictionary<int, MergedPullRequest>();
        DateTime? cursor = null;

        while (byNumber.Count < MaxPullRequests)
        {
            var search = cursor is { } bound
                ? $"merged:>={since:yyyy-MM-dd} merged:<={bound:yyyy-MM-ddTHH:mm:ssZ}"
                : $"merged:>={since:yyyy-MM-dd}";

            GhCliResult result;
            try
            {
                result = await ghRunner.RunAsync(
                    workingDirectory,
                    [
                        "pr", "list",
                        "--state", "merged",
                        "--limit", PullRequestPageSize.ToString(CultureInfo.InvariantCulture),
                        "--search", search,
                        "--json", PullRequestJsonFields,
                    ],
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                report.GithubIncompleteReason = $"gh could not be run: {ex.Message}";
                break;
            }

            if (!result.Started || result.ExitCode != 0)
            {
                // A page that fails after earlier pages succeeded still loses only what it would have
                // held: the PRs already collected are written, and the reason is reported. That the
                // report then says "stopped early" rather than "skipped" is the whole point (#1931
                // review LOW) -- the old wording told an operator the half wrote nothing while its
                // rows were being appended.
                report.GithubIncompleteReason =
                    $"gh did not answer (started={result.Started}, exit={result.ExitCode}): {result.Stderr.Trim()}";
                break;
            }

            if (TryReadPage(result.Stdout, report) is not { } page)
            {
                break;
            }

            var before = byNumber.Count;
            DateTime? oldest = null;
            foreach (var pullRequest in page.Items)
            {
                byNumber.TryAdd(pullRequest.Number, pullRequest);
                if (pullRequest.MergedAt is { } mergedAt && (oldest is null || mergedAt < oldest))
                {
                    oldest = mergedAt;
                }
            }

            // Three independent stops, because any one of them alone can loop forever: a short page is
            // the last page; a page that added nothing new cannot be advanced past; and a page whose PRs
            // carry no mergedAt gives the cursor nothing to move to.
            // RawCount, not Items.Count: a full page carrying one unreadable entry is still a full page,
            // and stopping on the reduced count would abandon every older PR behind it.
            if (page.RawCount < PullRequestPageSize || byNumber.Count == before || oldest is null)
            {
                break;
            }

            // A FOURTH stop, and the one that is about someone else's behaviour rather than this
            // loop's: the cursor advance below is only sound while each page is the newest of what
            // remains. spec/baton.md §7's backfill section has why that cannot be pinned with a
            // `sort:` qualifier and is checked here instead.
            //
            // What this check DETECTS is narrower than that assumption, and saying so is the point
            // (#1931 re-review LOW): it reads a page in isolation and can only see that the page is
            // internally out of merge order. A page that is internally ordered but is not the newest of
            // what remains passes here and still advances the cursor past PRs the walk never saw. It is
            // a strong proxy rather than a proof -- any non-merge ordering shows up across 40 items --
            // and the walk's other disclosure (`GithubIncompleteReason`) is what an operator reads when
            // it fires.
            if (FirstOutOfMergeOrder(page.Items) is { } outOfOrder)
            {
                report.GithubIncompleteReason =
                    $"gh returned a page out of merge order (#{outOfOrder} is newer than the entry before it), "
                        + "and this walk's merged:<= cursor assumes newest-first";
                break;
            }

            cursor = oldest;
        }

        report.PullRequestsSeen = byNumber.Count;
        report.GithubHitTheCap = byNumber.Count >= MaxPullRequests;

        var rows = new List<CostLedgerEntry>();
        foreach (var pullRequest in byNumber.Values.OrderByDescending(p => p.Number))
        {
            var room = pullRequest.HeadRefName is { Length: > 0 } head
                && branchByRoom.TryGetValue(head, out var recordedRoom)
                    ? recordedRoom
                    : null;
            if (room is null)
            {
                report.UnattributedPullRequest(
                    pullRequest.Number,
                    pullRequest.HeadRefName,
                    "no room on disk declares this branch as its delivery-branch.txt (already swept by "
                        + "retention, opened by hand, or a lane that declares no delivery branch)");
            }

            // WorkingDirectory, and it is a statement of fact rather than a fallback: the repository
            // these PRs came from is by construction the one `gh` was run in.
            rows.Add(CostLedgerStore.BuildGithubBackfillRow(
                pullRequest with { Room = room }, repository, RepositoryIdentitySource.WorkingDirectory));
        }

        Accumulate(pending, LedgerFilePathFor(repository, ledgerDirectoryOverride), rows);
    }

    /// <summary>
    /// The number of the first pull request in <paramref name="items"/> that is NEWER than the one
    /// before it, or <see langword="null"/> when the page is ordered newest-merge-first as the cursor
    /// walk requires. Entries carrying no <c>mergedAt</c> are skipped rather than treated as a break in
    /// the order: they are already excluded from the cursor for the same reason.
    /// </summary>
    private static int? FirstOutOfMergeOrder(List<MergedPullRequest> items)
    {
        DateTime? previous = null;
        foreach (var item in items)
        {
            if (item.MergedAt is not { } mergedAt)
            {
                continue;
            }

            if (previous is { } bound && mergedAt > bound)
            {
                return item.Number;
            }

            previous = mergedAt;
        }

        return null;
    }

    /// <summary>
    /// One page of <c>gh pr list</c> output, or <see langword="null"/> when the page itself was
    /// unusable (which stops the walk and is reported). An individual entry carrying no usable number
    /// is counted and skipped rather than fatal — one malformed PR must not cost its page the rest.
    /// </summary>
    /// <param name="RawCount">
    /// How many entries the page held before the unreadable ones were dropped — what the "was this the
    /// last page" test has to compare against, since a full page with one bad entry is still full.
    /// </param>
    private readonly record struct PullRequestPage(List<MergedPullRequest> Items, int RawCount);

    /// <inheritdoc cref="TryReadPage(string, BackfillReport)"/>
    private static PullRequestPage? TryReadPage(string stdout, BackfillReport report)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stdout);
        }
        catch (JsonException ex)
        {
            report.GithubIncompleteReason = $"gh's output did not parse as JSON: {ex.Message}";
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                report.GithubIncompleteReason = "gh returned something other than a JSON array of pull requests";
                return null;
            }

            var items = new List<MergedPullRequest>();
            var rawCount = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                rawCount++;
                if (MergedPullRequestReader.TryRead(element) is { } pullRequest)
                {
                    items.Add(pullRequest);
                }
                else
                {
                    // Counted per OCCURRENCE, not per distinct PR: an entry with no number cannot be
                    // deduplicated against the overlapping page boundary, so a malformed PR sitting on
                    // one can be counted twice. The count is a disclosure, never an inventory.
                    report.UnreadablePullRequests++;
                }
            }

            return new PullRequestPage(items, rawCount);
        }
    }

    /// <summary>
    /// Splits <paramref name="pending"/> into "already there" and "to write", fills the report's
    /// counters, and returns the second half for <see cref="CommitAsync"/> — <b>writing nothing</b>, so
    /// the whole plan exists before a word of the report is printed and before a byte is appended.
    /// <para>
    /// <b>The read here is for the REPORT, not for correctness.</b>
    /// <see cref="CostLedgerStore.AppendAsync"/> re-checks the same ids inside its own lock, so a
    /// concurrent settle landing between this read and that append cannot produce a duplicate — what
    /// this read buys is a count a dry run can print without holding a write lock.
    /// </para>
    /// </summary>
    private static async Task<List<(string LedgerFilePath, List<CostLedgerEntry> Rows)>> PlanAsync(
        Dictionary<string, List<CostLedgerEntry>> pending,
        BackfillReport report,
        CancellationToken cancellationToken)
    {
        var planned = new List<(string, List<CostLedgerEntry>)>();
        foreach (var (ledgerFilePath, rows) in pending.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var existing = await CostLedgerStore.ReadAllAsync(ledgerFilePath, cancellationToken).ConfigureAwait(false);
            var existingByExecution = new Dictionary<string, CostLedgerEntry>(StringComparer.Ordinal);
            foreach (var row in existing)
            {
                if (row.Execution is { Length: > 0 } id)
                {
                    existingByExecution[id] = row;
                }
            }

            var fresh = new List<CostLedgerEntry>();
            foreach (var row in rows)
            {
                if (row.Execution is { Length: > 0 } id && existingByExecution.TryGetValue(id, out var already))
                {
                    report.AlreadyLedgered++;

                    // The one thing append-only immutability costs, said out loud rather than repaired:
                    // this execution's row was written before its verdict.json could be read onto it,
                    // and no row this command can write will change that.
                    if (row.ReviewedRef is not null && already.ReviewedRef is null)
                    {
                        report.VerdictsThatPredateTheirRow++;
                    }

                    continue;
                }

                fresh.Add(row);
            }

            report.RowsToWrite += fresh.Count;
            report.Files[ledgerFilePath] = fresh.Count;

            if (fresh.Count > 0)
            {
                planned.Add((ledgerFilePath, fresh));
            }
        }

        return planned;
    }

    /// <summary>
    /// The append half, run only after <see cref="BackfillReport.Print"/> has said what it is about to
    /// do — see <see cref="ExecuteAsync(LedgerBackfillOptions, TextWriter, IGhCliRunner?, string?, RepositoryProbe?, CancellationToken)"/>
    /// for why that order is the ruling rather than a preference. Never called on a dry run.
    /// </summary>
    private static async Task CommitAsync(
        List<(string LedgerFilePath, List<CostLedgerEntry> Rows)> planned, CancellationToken cancellationToken)
    {
        foreach (var (ledgerFilePath, rows) in planned)
        {
            await CostLedgerStore.AppendAsync(rows, ledgerFilePath, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string LedgerFilePathFor(RepositoryIdentity repository, string? ledgerDirectoryOverride) =>
        ledgerDirectoryOverride is { Length: > 0 } directory
            ? Path.Combine(directory, $"{repository.FileSlug}.jsonl")
            : BatonPaths.CostLedgerFile(repository.FileSlug);

    private static void Accumulate(
        Dictionary<string, List<CostLedgerEntry>> pending, string ledgerFilePath, IReadOnlyList<CostLedgerEntry> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        if (!pending.TryGetValue(ledgerFilePath, out var list))
        {
            list = [];
            pending[ledgerFilePath] = list;
        }

        list.AddRange(rows);
    }

    /// <summary>
    /// What the run did, and — the half a dry run exists for — what it could NOT attribute and why.
    /// Every unattributed unit is named with its own reason rather than rolled into a count, because a
    /// count alone cannot tell an operator whether the gap is theirs to close.
    /// </summary>
    private sealed class BackfillReport(bool dryRun)
    {
        /// <summary>How many distinct unattributed rooms/PRs are named individually before the rest are summarised.</summary>
        private const int NamedLimit = 20;

        private readonly List<string> _unattributedRooms = [];
        private readonly List<string> _unattributedPullRequests = [];

        public int RoomsWalked { get; set; }

        /// <summary>How many rooms took <c>ResolveRoomRepositoryAsync</c>'s working-directory fallback — the disclosure that method's remarks require.</summary>
        public int RoomsKeyedByWorkingDirectory { get; set; }

        public int RowsToWrite { get; set; }

        public int AlreadyLedgered { get; set; }

        public int VerdictsThatPredateTheirRow { get; set; }

        public int PullRequestsSeen { get; set; }

        public int UnreadablePullRequests { get; set; }

        public bool GithubHitTheCap { get; set; }

        public DateTime? GithubSince { get; set; }

        /// <summary>
        /// Why the GitHub half did not walk the whole window — <b>whether or not it collected
        /// anything first</b>. <see cref="Print"/> is what tells those two apart: with no PR collected
        /// the half was skipped; with some collected the walk stopped early and those rows ARE
        /// written, which is the sentence #1931's review found missing.
        /// </summary>
        public string? GithubIncompleteReason { get; set; }

        public Dictionary<string, int> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        private int _unattributedRoomCount;

        private int _unattributedPullRequestCount;

        public void UnattributedRoom(string roomDirectoryPath, string reason)
        {
            _unattributedRoomCount++;
            if (_unattributedRooms.Count < NamedLimit)
            {
                _unattributedRooms.Add($"    {roomDirectoryPath} -- {reason}");
            }
        }

        public void UnattributedPullRequest(int number, string? branch, string reason)
        {
            _unattributedPullRequestCount++;
            if (_unattributedPullRequests.Count < NamedLimit)
            {
                _unattributedPullRequests.Add($"    #{number} ({branch ?? "no head branch reported"}) -- {reason}");
            }
        }

        public void Print(TextWriter output)
        {
            Write(output, dryRun ? "Ledger backfill (DRY RUN -- nothing was written):" : "Ledger backfill:");
            Write(output, $"  Rooms walked: {RoomsWalked}");
            if (RoomsKeyedByWorkingDirectory > 0)
            {
                Write(
                    output,
                    $"    {RoomsKeyedByWorkingDirectory} of them recorded a project root that no longer resolves (an "
                        + "auto-provisioned worktree, torn down on Terminal) and were keyed to THIS working "
                        + "directory's repository instead. Run this from the checkout those rooms belong to.");
                Write(
                    output,
                    "    Every row from those rooms carries identitySource: working-directory, so a wrong key "
                        + "stays identifiable afterwards -- but this ledger is append-only and a later run from "
                        + "the right checkout writes a DIFFERENT repository's file, so it cannot repair them.");
            }

            Write(output, $"  Rooms not attributed: {_unattributedRoomCount}");
            foreach (var line in _unattributedRooms)
            {
                Write(output, line);
            }

            if (_unattributedRoomCount > _unattributedRooms.Count)
            {
                Write(output, $"    ... and {_unattributedRoomCount - _unattributedRooms.Count} more");
            }

            if (GithubIncompleteReason is { Length: > 0 } skipped && PullRequestsSeen == 0)
            {
                Write(output, $"  Merged PRs: the GitHub half was skipped -- {skipped}");
            }
            else
            {
                var since = GithubSince ?? DefaultGithubSince;
                Write(output, $"  Merged PRs since {since:yyyy-MM-dd}: {PullRequestsSeen}");
                if (GithubIncompleteReason is { Length: > 0 } stoppedEarly)
                {
                    Write(
                        output,
                        $"    (the walk STOPPED EARLY after {PullRequestsSeen} PR(s) -- {stoppedEarly}. Those rows are "
                            + "written; there may be older merged PRs inside the window this run never saw.)");
                }

                if (GithubHitTheCap)
                {
                    Write(
                        output,
                        $"    (at or past the {MaxPullRequests}-PR cap -- the walk stops at the first page boundary "
                            + "past it, so there may be older merged PRs this run did not see. Narrow the window.)");
                }

                if (UnreadablePullRequests > 0)
                {
                    Write(
                        output,
                        $"    {UnreadablePullRequests} carried no usable number in gh's output and were skipped");
                }

                Write(output, $"  PRs not joined to a room: {_unattributedPullRequestCount}");
                foreach (var line in _unattributedPullRequests)
                {
                    Write(output, line);
                }

                if (_unattributedPullRequestCount > _unattributedPullRequests.Count)
                {
                    Write(output, $"    ... and {_unattributedPullRequestCount - _unattributedPullRequests.Count} more");
                }
            }

            Write(output, $"  Rows already in the ledger: {AlreadyLedgered}");

            // Future tense in BOTH modes, because this report is printed before the append (see
            // ExecuteAsync). "Rows written" here would be a claim about something that has not
            // happened yet, and an append that then throws would have made it false.
            Write(
                output,
                dryRun
                    ? $"  Rows this would write: {RowsToWrite}"
                    : $"  Rows to write, appended after this report: {RowsToWrite}");
            foreach (var (path, count) in Files.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                Write(output, $"    {path}: {count}");
            }

            if (VerdictsThatPredateTheirRow > 0)
            {
                Write(
                    output,
                    $"  {VerdictsThatPredateTheirRow} already-ledgered execution(s) wrote a verdict.json their row "
                        + "predates. This ledger is append-only and its rows are immutable, so those rows keep their "
                        + "absent verdict fields -- nothing here rewrites them.");
            }
        }
    }

    /// <summary>
    /// LF explicitly, not <see cref="TextWriter.WriteLine()"/> — the same reason
    /// <see cref="LedgerViewCommand"/> does it: this repo runs on Windows, and the same run over the
    /// same rooms has to produce the same bytes wherever it is compared.
    /// </summary>
    private static void Write(TextWriter output, string line)
    {
        output.Write(line);
        output.Write('\n');
    }
}
