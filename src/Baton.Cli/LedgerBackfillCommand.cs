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
    /// How many merged PRs one run will collect in total, across every page. Bounded because this is a
    /// network call whose answer grows without limit as the repository ages: an unbounded walk would
    /// page through years of history to write rows a <c>--since</c> window then discards. A run that
    /// hits the cap says so on stdout rather than silently reporting a partial answer as a complete one.
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

        await WriteAsync(options.DryRun, pending, report, cancellationToken).ConfigureAwait(false);

        report.Print(output);
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

            var (repository, fromWorkingDirectory) = await ResolveRoomRepositoryAsync(
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

            if (fromWorkingDirectory)
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

            var labels = await DispatchLabels.ReadForRoomAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false);
            var rows = CostLedgerStore
                .BuildEntries(entries, roomDirectoryPath, repository, labelByWorker: labels)
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
    /// directory. Expressed here rather than called there so the <see cref="RepositoryProbe"/> seam
    /// covers BOTH lookups — a test that stubbed only one would still spawn git for the other.
    /// <para>
    /// <b>The fallback is deliberately wider here than at a settle site</b>, and spec/baton.md §7's
    /// backfill section is the record of exactly how much wider, the measurement that forced it, and
    /// the exposure it accepts — which is the one
    /// <see cref="RepositoryIdentityResolver.TryResolveForRoomAsync"/>'s own remarks warn about, so read
    /// those two together rather than this comment alone. What this method owns is the flag it returns,
    /// which is how the run reports the exposure instead of hiding it.
    /// </para>
    /// </summary>
    private static async Task<(RepositoryIdentity? Identity, bool FromWorkingDirectory)> ResolveRoomRepositoryAsync(
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
                return (fromProjectRoot, false);
            }
        }

        return (await probe(Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false), true);
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
            report.GithubSkippedReason =
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
                report.GithubSkippedReason = $"gh could not be run: {ex.Message}";
                return;
            }

            if (!result.Started || result.ExitCode != 0)
            {
                // A page that fails after earlier pages succeeded still loses only what it would have
                // held: the PRs already collected are written, and the reason is reported.
                report.GithubSkippedReason =
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

            rows.Add(CostLedgerStore.BuildGithubBackfillRow(pullRequest with { Room = room }, repository));
        }

        Accumulate(pending, LedgerFilePathFor(repository, ledgerDirectoryOverride), rows);
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
            report.GithubSkippedReason = $"gh's output did not parse as JSON: {ex.Message}";
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                report.GithubSkippedReason = "gh returned something other than a JSON array of pull requests";
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
    /// Splits <paramref name="pending"/> into "already there" and "to write", and writes the second
    /// half unless this is a dry run.
    /// <para>
    /// <b>The read here is for the REPORT, not for correctness.</b>
    /// <see cref="CostLedgerStore.AppendAsync"/> re-checks the same ids inside its own lock, so a
    /// concurrent settle landing between this read and that append cannot produce a duplicate — what
    /// this read buys is a count a dry run can print without holding a write lock.
    /// </para>
    /// </summary>
    private static async Task WriteAsync(
        bool dryRun,
        Dictionary<string, List<CostLedgerEntry>> pending,
        BackfillReport report,
        CancellationToken cancellationToken)
    {
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

            if (dryRun || fresh.Count == 0)
            {
                continue;
            }

            await CostLedgerStore.AppendAsync(fresh, ledgerFilePath, cancellationToken).ConfigureAwait(false);
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

        public string? GithubSkippedReason { get; set; }

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

            if (GithubSkippedReason is { Length: > 0 } skipped)
            {
                Write(output, $"  Merged PRs: the GitHub half was skipped -- {skipped}");
            }
            else
            {
                var since = GithubSince ?? DefaultGithubSince;
                Write(output, $"  Merged PRs since {since:yyyy-MM-dd}: {PullRequestsSeen}");
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
            Write(output, dryRun ? $"  Rows this would write: {RowsToWrite}" : $"  Rows written: {RowsToWrite}");
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
