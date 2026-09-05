using System.Text.RegularExpressions;
using Baton.Artifacts;
using Baton.Domain;
using Baton.Status;

namespace Baton.Accounting;

/// <summary>
/// Reads and writes the repository-keyed cost ledger (#1849 phase A) —
/// <c>{BatonPaths.Root}/ledger/&lt;repo-slug&gt;.jsonl</c>, one immutable append-only row per settled
/// execution attempt. Shares the whole append-only JSONL store — <see cref="JsonLinesLedger{TEntry}"/>,
/// and through it <see cref="MutexGuardedFileLock"/> — with <c>QuotaLedgerStore</c> (#1884) rather than
/// introducing a second copy of it or a third concurrency mechanism, under its own lock name prefix so
/// the three files never contend with each other.
/// </summary>
/// <remarks>
/// <para>
/// <b>Consumes <c>quota-ledger.jsonl</c>'s source; never replaces it.</b> Token dimensions come from
/// <see cref="ExecutionUsageProjector.BuildByExecutionId"/> and adapter/model from
/// <see cref="ExecutionBindingResolver.Resolve"/> — the same two primitives <c>QuotaLedgerStore</c>
/// reads, so there is exactly one vendor-envelope reader in the tree (Architecture Rule 2) and the two
/// ledgers can never disagree about what an execution spent. What this ledger adds is durability past
/// a room's retention, a repository-level key, and versioned price provenance.
/// </para>
/// <para>
/// <b>Fails open, never gates</b> — identical posture to <c>QuotaLedgerStore</c>: this store only ever
/// adds accounting coverage, so <see cref="AppendAsync"/> throwing is the settle-site caller's to log
/// on stderr and swallow, never a reason a run that already reached Terminal reports as failed.
/// </para>
/// </remarks>
public static partial class CostLedgerStore
{
    /// <summary>
    /// This ledger's shared store — <see cref="JsonLinesLedger{TEntry}"/>, whose own remarks state what
    /// it guarantees and why the prefix handed to it here is not free to rename. <c>baton-cost-ledger</c>
    /// is deliberately unlike <c>QuotaLedgerStore</c>'s and <c>RoomRegistryStore</c>'s, so the three
    /// files never contend.
    /// </summary>
    internal static readonly JsonLinesLedger<CostLedgerEntry> Ledger =
        new("baton-cost-ledger", "cost ledger", entry => entry.Execution);

    /// <summary><c>estimateReason</c> when the tokens are a sum across more than one model, so no single rate applies to them.</summary>
    internal const string MultiModelUsageReason = "multi-model-usage";

    /// <summary><c>estimateReason</c> when the breakdown names one model and the binding asked for a different one.</summary>
    internal const string ModelMismatchReason = "model-mismatch";

    /// <summary>
    /// Builds one <see cref="CostLedgerEntry"/> per execution in <paramref name="entries"/> that has
    /// both a recorded start and exit — the same population <c>QuotaLedgerStore.BuildEntries</c> yields,
    /// for the same stated reason: an execution missing a lifecycle event has no wall-clock to derive
    /// and is absent rather than reported as zero (spec/baton.md §7's accepted loss, not a second one).
    /// <b>A retry or redispatch is a separate row</b>, with no extra machinery: every dispatch mints a
    /// fresh <c>ExecutionId</c>, so two attempts of one step are two executions here.
    /// </summary>
    /// <param name="repository">
    /// The canonical repository identity this room's work belongs to. Never derived from the room path:
    /// worktrees of one repository must share one ledger (<see cref="RepositoryIdentity"/>).
    /// </param>
    /// <param name="catalog">Defaults to <see cref="PriceCatalog.Default"/>. Its id/version is stamped on every row it prices.</param>
    /// <param name="planFactors">Defaults to <see cref="PlanFactorTable.Default"/>. Same stamping rule.</param>
    /// <param name="runwayOverrideReasonByWorker">
    /// #1848: worker name to the runway-override reason recorded on that worker's binding at dispatch,
    /// for overrides that actually bypassed a Hold. Supplied by the settle site (which can read
    /// <c>bindings.json</c>; this layer cannot see <c>Baton.Vendors</c>), null everywhere else —
    /// <see cref="CostLedgerEntry.RunwayOverrideReason"/>'s own doc states what an absent value means
    /// and does not mean.
    /// </param>
    /// <param name="deliveryByWorker">
    /// #1901 C1: worker name to what that worker's WORKSPACE says about the work it delivered — issue,
    /// PR, diff shape. Supplied by the settle site for the same two reasons
    /// <paramref name="runwayOverrideReasonByWorker"/> is (<see cref="WorkspaceDelivery"/>'s own
    /// remarks), null everywhere else. Absent entries leave the row's fields absent; nothing here
    /// guesses one from another.
    /// </param>
    public static IReadOnlyList<CostLedgerEntry> BuildEntries(
        IReadOnlyList<LogEntry> entries,
        string roomDirectoryPath,
        RepositoryIdentity? repository,
        PriceCatalog? catalog = null,
        PlanFactorTable? planFactors = null,
        IReadOnlyDictionary<string, string>? runwayOverrideReasonByWorker = null,
        IReadOnlyDictionary<string, WorkspaceDelivery>? deliveryByWorker = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        catalog ??= PriceCatalog.Default;
        planFactors ??= PlanFactorTable.Default;

        var artifactsRootPath = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
        var usageByExecutionId = ExecutionUsageProjector.BuildByExecutionId(entries, artifactsRootPath, roomDirectoryPath: roomDirectoryPath);
        var resolvedBindings = ExecutionBindingResolver.Resolve(entries);

        var outcomeByExecutionId = new Dictionary<string, string>(StringComparer.Ordinal);
        var startedAtByExecutionId = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var exitedAtByExecutionId = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var requestByExecutionId = new Dictionary<string, ExecutionRequest>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry is LogEntry.CoreLogEntry { WriterUtcTimestamp: { } timestamp } coreEntry)
            {
                switch (coreEntry.Event)
                {
                    case CoreEvent.ExecutionStarted started:
                        startedAtByExecutionId[started.ExecutionId.Value] = timestamp;
                        break;
                    case CoreEvent.ExecutionExited exited:
                        exitedAtByExecutionId[exited.ExecutionId.Value] = timestamp;
                        break;
                }
            }

            if (entry is not LogEntry.FlowLogEntry flowEntry)
            {
                continue;
            }

            switch (flowEntry.Event)
            {
                case FlowEvent.ExecutionRequestAccepted accepted:
                    requestByExecutionId[accepted.Request.ExecutionId.Value] = accepted.Request;
                    break;

                // The same closed outcome token set QuotaLedgerEntry.Outcome documents -- one
                // vocabulary across both ledgers, so a filter written against one works on the other.
                case FlowEvent.ExecutionSucceeded succeeded:
                    outcomeByExecutionId[succeeded.ExecutionId.Value] = "Succeeded";
                    break;

                case FlowEvent.ExecutionFailed failed:
                    outcomeByExecutionId[failed.ExecutionId.Value] = failed.FailureClassification?.ToString() ?? "Failed";
                    break;

                case FlowEvent.ExecutionCancelled cancelled:
                    outcomeByExecutionId[cancelled.ExecutionId.Value] = "Cancelled";
                    break;

                case FlowEvent.ExecutionIndeterminate indeterminate:
                    outcomeByExecutionId[indeterminate.ExecutionId.Value] = "Indeterminate";
                    break;

                case FlowEvent.ExecutionArrested arrested:
                    outcomeByExecutionId[arrested.ExecutionId.Value] = "Arrested";
                    break;
            }
        }

        var recordedRoomPath = BatonPaths.RecordKey(roomDirectoryPath);
        var result = new List<CostLedgerEntry>(usageByExecutionId.Count);

        foreach (var (executionId, usage) in usageByExecutionId)
        {
            resolvedBindings.TryGetValue(executionId, out var binding);
            outcomeByExecutionId.TryGetValue(executionId, out var outcome);
            requestByExecutionId.TryGetValue(executionId, out var request);
            var startedAt = startedAtByExecutionId.TryGetValue(executionId, out var s) ? s : (DateTime?)null;
            var endedAt = exitedAtByExecutionId.TryGetValue(executionId, out var e) ? e : (DateTime?)null;

            // Priced as of when the attempt ENDED -- the instant the work is attributed to, so a
            // catalog range that opened mid-attempt does not silently reprice it on the next read.
            var pricedAt = endedAt ?? startedAt ?? DateTime.UtcNow;

            var tokens = new TokenDimensions(
                Input: usage.TokensIn,
                Output: usage.TokensOut,
                CacheRead: usage.CacheReadTokens,
                CacheCreation: usage.CacheCreationTokens,
                Thinking: usage.ThinkingTokens);

            var (apiUsd, apiStatus, planUsd, planStatus, estimateReason) =
                Estimate(catalog, planFactors, binding.Adapter, binding.Model, tokens, usage.ModelsObserved, pricedAt);

            var unavailableReason = usage.BilledReconciliationUnavailable;
            var completeness = ResolveCompleteness(unavailableReason, usage.BilledTokens);

            // #1901 C1 item 1/3: the workspace facts the settle site resolved for THIS row's worker.
            // Keyed on the worker name for the same reason the runway override is: bindings are
            // per-worker, and a composed template's phases can sit in different workspaces.
            var delivery = request?.Worker is { } deliveryWorker && deliveryByWorker is not null
                && deliveryByWorker.TryGetValue(deliveryWorker, out var resolvedDelivery)
                    ? resolvedDelivery
                    : null;

            // #1901 C1 item 2: read straight off this execution's own artifact directory. No injection
            // needed and none wanted -- verdict.json is a file the engine already owns the path of
            // (VerdictInstrumentStamp writes to the same one), so this reads the room it was handed
            // rather than reaching anywhere new.
            var verdict = ReadVerdictFacts(artifactsRootPath, executionId);

            result.Add(new CostLedgerEntry(
                SourceKind: CostSourceKind.BatonExecution,
                Repository: repository?.Value,
                Room: recordedRoomPath,
                Workflow: request?.WorkflowId.Value,
                Step: request?.StepId?.Value,
                Execution: executionId,
                Role: request?.Worker,
                Adapter: binding.Adapter,
                Model: binding.Model,
                ModelsObserved: usage.ModelsObserved,
                Outcome: outcome,
                Issue: delivery?.Issue,
                PullRequest: delivery?.PullRequest,
                StartedAt: startedAt,
                EndedAt: endedAt,
                TokensIn: usage.TokensIn,
                TokensOut: usage.TokensOut,
                CacheReadTokens: usage.CacheReadTokens,
                CacheCreationTokens: usage.CacheCreationTokens,
                ThinkingTokens: usage.ThinkingTokens,
                Turns: usage.Turns,
                WallClockMs: usage.WallClockMs,
                // #1882: carried through as the projector attributed them -- one execution's row gets
                // both, every other row gets neither. No arithmetic here on purpose.
                VerifyStepMs: usage.VerifyStepMs,
                VerifyResultsBytes: usage.VerifyResultsBytes,
                BilledTokens: usage.BilledTokens,
                LiveBilledTokens: usage.LiveBilledTokens,
                BilledUnderReadTokens: usage.BilledUnderReadTokens,
                PeakBilledInWindow: usage.PeakBilledInWindow,
                Completeness: completeness,
                CompletenessReason: unavailableReason,
                ApiEquivalentUsd: apiUsd,
                EstimateStatus: apiStatus,
                PlanMeterEstimateUsd: planUsd,
                PlanMeterEstimateStatus: planStatus,
                EstimateReason: estimateReason,
                PriceCatalogId: catalog.Id,
                PriceCatalogVersion: catalog.Version,
                PlanFactorTableId: planFactors.Id,
                PlanFactorTableVersion: planFactors.Version,
                RunwayOverrideReason: request?.Worker is { } worker && runwayOverrideReasonByWorker is not null
                    && runwayOverrideReasonByWorker.TryGetValue(worker, out var runwayReason)
                        ? runwayReason
                        : null,
                FilesChanged: delivery?.FilesChanged,
                Additions: delivery?.Additions,
                Deletions: delivery?.Deletions,
                TestFilesChanged: delivery?.TestFilesChanged,
                ReviewedRef: verdict?.ReviewedRef,
                ReviewedPr: verdict?.ReviewedPr,
                ReviewedHead: verdict?.ReviewedHead,
                FindingsHigh: verdict?.High,
                FindingsMedium: verdict?.Medium,
                FindingsLow: verdict?.Low));
        }

        return result;
    }

    /// <summary>
    /// The five verdict-derived facts of one review row (#1901 C1 item 2). Internal and nullable as a
    /// unit: they come from one file, so "no verdict" is one absence rather than five.
    /// </summary>
    internal readonly record struct VerdictFacts(
        string ReviewedRef, string? ReviewedPr, string? ReviewedHead, int High, int Medium, int Low);

    /// <summary>
    /// The output name a verdict-producing role writes — the same literal
    /// <c>Baton.Cli.VerdictInstrumentStamp.VerdictOutputName</c> stamps, kept as a constant here rather
    /// than reached for across the project boundary (this layer cannot see <c>Baton.Cli</c>).
    /// </summary>
    internal const string VerdictOutputName = "verdict.json";

    /// <summary>
    /// This execution's <c>verdict.json</c> reduced to the row's fields, or <see langword="null"/> when
    /// it wrote none — which is every non-review execution, so absence here is the overwhelmingly
    /// common case rather than a failure.
    /// <para>
    /// <b>Parsed through <see cref="ReviewVerdictSchema.TryParse"/>, never through a second reader.</b>
    /// A file that does not satisfy that one definition of "valid verdict" yields nothing at all rather
    /// than a partially-populated row: the counts and the ref would then disagree with what every other
    /// consumer of the same file sees. An unreadable file is the same absence — an accounting read
    /// fails open, never past the settle site it was called from.
    /// </para>
    /// </summary>
    private static VerdictFacts? ReadVerdictFacts(string artifactsRootPath, string executionId)
    {
        // ArtifactManager's own derivation, never a second spelling of the same path: it is what the
        // engine used to CREATE this directory, so a rename there must reach this read too.
        var verdictPath = Path.Combine(
            ArtifactManager.ResolveOutputDirectory(artifactsRootPath, new ExecutionId(executionId)), VerdictOutputName);
        if (!File.Exists(verdictPath))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(verdictPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (!ReviewVerdictSchema.TryParse(bytes, out var verdict, out _) || verdict is null)
        {
            return null;
        }

        var reviewedRef = verdict.ReviewedRef.Trim();
        var (reviewedPr, reviewedHead) = SplitReviewedRef(reviewedRef);
        return new VerdictFacts(
            ReviewedRef: reviewedRef,
            ReviewedPr: reviewedPr,
            ReviewedHead: reviewedHead,
            High: verdict.Findings.Count(f => f.Severity == ReviewFindingSeverity.High),
            Medium: verdict.Findings.Count(f => f.Severity == ReviewFindingSeverity.Medium),
            Low: verdict.Findings.Count(f => f.Severity == ReviewFindingSeverity.Low));
    }

    /// <summary>
    /// <paramref name="reviewedRef"/> split into the PR it names and the commit it names — <b>at most
    /// one of the two, never both</b>, which is what <see cref="CostLedgerEntry.ReviewedPr"/> and
    /// <see cref="CostLedgerEntry.ReviewedHead"/> promise a reader.
    /// <para>
    /// A PR is a bare number, a <c>#</c>-prefixed one, or a <c>.../pull/&lt;n&gt;</c> URL as
    /// <c>gh pr create</c> prints it. A commit is a bare hex SHA of 7–40 characters — git's own
    /// abbreviation floor, without which a branch literally named <c>abc</c> would be recorded as a
    /// commit.
    /// </para>
    /// <para>
    /// <b>The two overlap, and an overlap yields NEITHER.</b> An all-digit string 7–40 characters long
    /// (<c>1234567</c>) is both a well-formed PR number and a well-formed abbreviated SHA — roughly one
    /// abbreviated SHA in 27 is all digits — and there is nothing in the ref itself that decides which
    /// it is. Recording both would put a PR number that does not exist into a per-PR reading; picking
    /// one would be the guess this whole method refuses. The unambiguous spellings are unaffected: a
    /// <c>#</c> or a <c>/pull/</c> prefix cannot be a SHA, and a hex string containing any letter cannot
    /// be a number. <see cref="CostLedgerEntry.ReviewedRef"/> keeps the text verbatim either way, so an
    /// ambiguous ref loses nothing but the derived field it could not have earned.
    /// </para>
    /// </summary>
    internal static (string? Pr, string? Head) SplitReviewedRef(string reviewedRef)
    {
        var prMatch = PullRequestReference().Match(reviewedRef);
        var pr = prMatch.Success ? prMatch.Groups["n"].Value : null;
        var head = CommitSha().IsMatch(reviewedRef) ? reviewedRef : null;

        return pr is not null && head is not null ? (null, null) : (pr, head);
    }

    [GeneratedRegex(@"^(?:#|(?:.*/pull/))?(?<n>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex PullRequestReference();

    [GeneratedRegex("^[0-9a-fA-F]{7,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitSha();

    /// <summary>
    /// The correcting row a <c>baton resolve</c> appends to <paramref name="existingRows"/> for
    /// <paramref name="recordedRoomKey"/> (#1901 C1 item 4), or <see langword="null"/> when that room
    /// has no row to correct yet.
    /// <para>
    /// <b>spec/baton.md §7 is the record</b> — why this is a new row rather than a stamp on the one it
    /// corrects, why the suffixed execution id below is load-bearing rather than cosmetic, and what a
    /// resolution row costs a <c>LedgerRollup</c> reading, are stated there and not restated here.
    /// </para>
    /// <para>
    /// The mechanics this method owns: which of the room's rows is copied (its LAST one that is not
    /// itself a correcting row), which fields are copied from it and which are deliberately left off,
    /// and the id <see cref="ResolutionExecutionSuffix"/> builds — which is what
    /// <see cref="AppendAsync"/>'s dedupe on <see cref="CostLedgerEntry.Execution"/> then sees.
    /// </para>
    /// </summary>
    /// <param name="existingRows">The ledger file's rows as already written — this method appends nothing itself.</param>
    /// <param name="recordedRoomKey">A <see cref="BatonPaths.RecordKey"/>, matched with <see cref="BatonPaths.RecordKeyComparer"/>.</param>
    public static CostLedgerEntry? BuildResolutionRow(
        IReadOnlyList<CostLedgerEntry> existingRows,
        string recordedRoomKey,
        ConductorResolution resolution,
        string? reason)
    {
        ArgumentNullException.ThrowIfNull(existingRows);
        ArgumentException.ThrowIfNullOrEmpty(recordedRoomKey);

        // Last in file order, and never another resolution row: two resolutions on one room must both
        // describe the EXECUTION they followed, not chain off each other's stripped-down copy.
        CostLedgerEntry? last = null;
        foreach (var row in existingRows)
        {
            if (row.Resolution is null
                && BatonPaths.RecordKeyComparer.Equals(row.Room ?? string.Empty, recordedRoomKey))
            {
                last = row;
            }
        }

        if (last?.Execution is not { Length: > 0 } execution)
        {
            return null;
        }

        return new CostLedgerEntry(
            SourceKind: last.SourceKind,
            Repository: last.Repository,
            Room: last.Room,
            Workflow: last.Workflow,
            Step: last.Step,
            Execution: execution + ResolutionExecutionSuffix(resolution),
            Role: last.Role,
            Outcome: last.Outcome,
            Issue: last.Issue,
            PullRequest: last.PullRequest,
            Resolution: resolution,
            ResolutionReason: reason is { Length: > 0 } ? reason : null);
    }

    /// <summary>
    /// What <see cref="BuildResolutionRow"/> appends to the settled execution's id. <c>#</c> cannot
    /// occur in a <c>Guid</c>, which is what every <c>ExecutionId</c> is, so a suffixed id can never
    /// collide with a real one.
    /// <para>
    /// <b>The KIND is part of the suffix, and that is the whole point of the parameter</b> — spec/baton.md
    /// §7 states the sequence that makes it necessary and what a kind-free suffix would silently lose.
    /// </para>
    /// </summary>
    public static string ResolutionExecutionSuffix(ConductorResolution resolution) => resolution switch
    {
        ConductorResolution.AcceptCapture => "#resolution-accept-capture",
        ConductorResolution.Reject => "#resolution-reject",
        ConductorResolution.Close => "#resolution-close",
        _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
    };

    /// <summary>
    /// How much of an attempt a row accounts for, from the two things the stream reader reports about
    /// it (#1883 review F2). Three states, and these two arguments decide between them exhaustively:
    /// <c>ExecutionUsageView</c>'s reconciliation triple is all-present-or-none, so a null
    /// <paramref name="unavailableReason"/> holds in exactly two cases and
    /// <paramref name="billedTokens"/> separates them.
    /// <list type="bullet">
    /// <item>A reason — ANY of <c>ExecutionUsageView.KnownUnavailableReasons</c> — is
    /// <see cref="CostCompleteness.Partial"/>. That deliberately includes the two that are not about
    /// truncation at all: <c>ExecutionUsageView.NoTerminalBilledFigureReason</c> (whose own doc has the
    /// two cases it conflates) and <c>ExecutionUsageView.NoLiveBilledFigureReason</c>, where the
    /// terminal line parsed but the replay over the same bytes read no usage line. Neither is provably
    /// whole, and #1849's own doctrine is that an undecidable case reads as the weaker claim. Mapping
    /// every reason rather than an enumerated subset is also what stops a reason added to the producer
    /// from silently landing here as <see cref="CostCompleteness.Complete"/>.</item>
    /// <item>No reason and a billed figure means reconciled — a terminal line parsed AND the replay
    /// completed — which is the only <see cref="CostCompleteness.Complete"/>.</item>
    /// <item>No reason and no billed figure means the usage was never read at all: no parser registered
    /// for the adapter, or no captured <c>.stdout.log</c>. <see langword="null"/>, i.e. the field is
    /// ABSENT on the row. Labelling that <c>complete</c> put an attempt carrying no dimensions into the
    /// same trustworthy set as a fully-captured one, which is the defect this replaces.</item>
    /// </list>
    /// </summary>
    internal static CostCompleteness? ResolveCompleteness(string? unavailableReason, long? billedTokens) =>
        unavailableReason is not null
            ? CostCompleteness.Partial
            : billedTokens is not null
                ? CostCompleteness.Complete
                : null;

    /// <summary>
    /// The two labelled estimates and their statuses. The plan-meter half resolves its FACTOR status
    /// first, so an unmeasured vendor reads <c>unmeasured</c> and a live discount window of unknown
    /// size reads <c>unknown</c> — both of which say more than the <c>unpriced</c> an empty catalog
    /// would otherwise flatten them into. There is no 1.0 fallback anywhere in this method: an
    /// unresolvable factor yields no number at all.
    /// <para>
    /// <b>#1883 review F1: nothing is priced unless <paramref name="tokens"/> is attributable to
    /// <paramref name="model"/>.</b> spec/baton.md §7 carries the ruling and what it costs; the
    /// mechanics are that <paramref name="modelsObserved"/> is the vendor's own breakdown of the very
    /// figures in <paramref name="tokens"/> (see <see cref="Domain.WorkerUsage.ModelsObserved"/>) while
    /// <paramref name="model"/> is <see cref="ExecutionBindingResolver"/>'s, i.e. what was ASKED FOR —
    /// so anything other than "one model, and it is that one" leaves both estimates absent with a
    /// reason. A <see langword="null"/> <paramref name="modelsObserved"/> is not a refusal: it is the
    /// no-breakdown-reported case this ledger has always priced.
    /// </para>
    /// </summary>
    private static (decimal? ApiUsd, EstimateStatus ApiStatus, decimal? PlanUsd, EstimateStatus PlanStatus, string? Reason) Estimate(
        PriceCatalog catalog,
        PlanFactorTable planFactors,
        string? adapter,
        string? model,
        TokenDimensions tokens,
        IReadOnlyList<string>? modelsObserved,
        DateTime at)
    {
        if (modelsObserved is { Count: > 0 } observed)
        {
            if (observed.Count > 1)
            {
                return (null, EstimateStatus.Unpriced, null, EstimateStatus.Unpriced, MultiModelUsageReason);
            }

            if (model is not { Length: > 0 } || !string.Equals(observed[0], model, StringComparison.OrdinalIgnoreCase))
            {
                return (null, EstimateStatus.Unpriced, null, EstimateStatus.Unpriced, ModelMismatchReason);
            }
        }

        var apiUsd = catalog.TryEstimateUsd(adapter, model, tokens, at);
        var apiStatus = apiUsd is null ? EstimateStatus.Unpriced : EstimateStatus.Estimated;

        var resolution = planFactors.Resolve(adapter, model, at);
        switch (resolution.Status)
        {
            case PlanFactorStatus.Unmeasured:
                return (apiUsd, apiStatus, null, EstimateStatus.Unmeasured, null);
            case PlanFactorStatus.Unknown:
                return (apiUsd, apiStatus, null, EstimateStatus.Unknown, null);
        }

        decimal weighted = 0m;
        var priced = false;
        foreach (var (dimension, count) in tokens.Present())
        {
            if (catalog.TryRate(adapter, model, dimension, at) is not { } rate)
            {
                return (apiUsd, apiStatus, null, EstimateStatus.Unpriced, null);
            }

            var weight = resolution.Weights.TryGetValue(dimension, out var w) ? w : 1m;
            weighted += rate * weight * count / 1_000_000m;
            priced = true;
        }

        return priced
            ? (apiUsd, apiStatus, weighted * resolution.DiscountMultiplier, EstimateStatus.Estimated, null)
            : (apiUsd, apiStatus, null, EstimateStatus.Unpriced, null);
    }

    /// <summary>
    /// Appends the subset of <paramref name="entries"/> whose <see cref="CostLedgerEntry.Execution"/>
    /// is not already present in <paramref name="ledgerFilePath"/>, in one read-check-then-append
    /// critical section.
    /// <b>Why the skip exists, and what it is not.</b> <c>Program.cs</c>'s settle-time call site fires
    /// on every command that carries a room to Terminal — including a re-run of an already-terminal
    /// room, <c>supply</c>, and the <c>resolve --reject</c> → re-Terminal path — and each of those
    /// re-derives <see cref="BuildEntries"/> over the WHOLE room rather than only what changed. Without
    /// this check a room settling twice writes every one of its executions twice, and an append-only
    /// accounting ledger that double-counts is worse than one that is missing rows: the totals every
    /// consumer (#1391's drill-down, #1848's enforcement) reads would silently inflate. It does NOT
    /// collapse retries: a retry is a different <c>ExecutionId</c> and therefore a different row.
    /// The filter itself, and every mechanical guarantee around it, belongs to
    /// <see cref="JsonLinesLedger{TEntry}.AppendAsync"/>. Throws exactly as
    /// <c>QuotaLedgerStore.AppendAsync</c> documents — the caller logs and swallows.
    /// </summary>
    public static Task AppendAsync(
        IReadOnlyList<CostLedgerEntry> entries, string ledgerFilePath, CancellationToken cancellationToken = default) =>
        Ledger.AppendAsync(entries, ledgerFilePath, cancellationToken);

    /// <summary>
    /// This ledger's rows, oldest first. Delegated whole to
    /// <see cref="JsonLinesLedger{TEntry}.ReadAllAsync"/>.
    /// </summary>
    public static Task<IReadOnlyList<CostLedgerEntry>> ReadAllAsync(
        string ledgerFilePath, CancellationToken cancellationToken = default) =>
        Ledger.ReadAllAsync(ledgerFilePath, cancellationToken);
}
