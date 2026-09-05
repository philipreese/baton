using System.Globalization;
using System.Text;
using Baton.Status;

namespace Baton.Runway;

/// <summary>
/// What the caller knows before the reservation arm runs: the gate's own verdict, the thresholds and
/// counters it took it against, and what this dispatch is estimated to burn.
/// </summary>
/// <param name="GateHeld"><c>RunwayDecision.IsHold</c> — the vendor's counters already refused.</param>
/// <param name="Unmeasured">The vendor has no usage source at all (<c>RunwayGate.UnmeasuredReason</c>).</param>
/// <param name="HeadroomPoints">
/// <c>RunwayDecision.HeadroomPoints</c>: percentage points to the nearer threshold. Null whenever the
/// gate did not admit on readable counters, which is what makes the reservation arm a no-op there.
/// </param>
public sealed record RunwayAdmissionRequest(
    string Vendor,
    bool GateHeld,
    bool Unmeasured,
    string? GateReason,
    IReadOnlyList<RunwayCounter> Counters,
    int WeekHoldPct,
    int SessionHoldPct,
    double MaxSnapshotAgeHours,
    DateTimeOffset? SnapshotHarvestedAt,
    double? HeadroomPoints,
    RunwayBurnEstimate Estimate,
    string? Room,
    string? Role,
    string? OverrideReason,
    DateTimeOffset At);

/// <summary>
/// <b>The runway admission ledger (#1896)</b> at <see cref="BatonPaths.RunwayAdmissionLedgerFile"/>: one
/// append-only row per evaluation of the hold, and the only state #1896's cross-dispatch arithmetic
/// reads. Wraps <see cref="JsonLinesLedger{TEntry}"/> under its own lock name, the same way
/// <c>QuotaLedgerStore</c> and <c>CostLedgerStore</c> do, rather than introducing a third copy of the
/// append-only-JSONL mechanism.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reserving and recording are ONE critical section</b>, which is the whole reason this store exists
/// rather than a plain <c>AppendAsync</c> call. Two concurrent <c>baton dispatch</c> processes are two
/// processes: if each reads the ledger, computes "nothing outstanding", decides to admit, and only then
/// appends, both admit — which is exactly the race #1896 was filed for. Read, decide and append
/// therefore happen inside one <see cref="MutexGuardedFileLock"/> acquisition
/// (<see cref="JsonLinesLedger{TEntry}.RunUnderLockAsync{T}"/>), and that body stays synchronous because
/// <see cref="Mutex"/> ownership is thread-affine. Anything slow the decision needs — reading the cost
/// ledger for a burn estimate — is done by the CALLER, before it gets here.
/// </para>
/// <para>
/// <b>Reservations are reconciled by the next harvest, with no reconciliation state.</b> A row counts as
/// outstanding only while its timestamp is at or after the snapshot the current reader is deciding
/// against; a fresh harvest moves that instant forward and the older rows fall out on their own, because
/// the new counters already include whatever they burned. <b>This under-reserves at a harvest
/// boundary</b>: a lane admitted ninety seconds before a harvest has barely burned anything, yet its
/// reservation is dropped as though the counters had absorbed it. That is the "reconciled on the next
/// harvest" mechanism doing exactly what it says, not a guarantee that reservations are ever exact.
/// </para>
/// <para>
/// <b>Fails open, never gates.</b> Same posture as the two ledgers above: this store only ever adds
/// evidence and a refusal the counters were already close to making, so a write that throws is the
/// caller's to log on stderr and swallow. A dispatch must never fail because its audit row could not be
/// written.
/// </para>
/// </remarks>
public static class RunwayAdmissionLedgerStore
{
    /// <summary>
    /// This ledger's shared store. The lock prefix is deliberately unlike <c>baton-quota-ledger</c>'s,
    /// <c>baton-cost-ledger</c>'s and <c>RoomRegistryStore</c>'s so the four files never contend — and,
    /// per <see cref="JsonLinesLedger{TEntry}"/>'s own remarks, is not free to rename once shipped.
    /// </summary>
    /// <remarks>
    /// The execution-id selector is <c>_ => null</c> on purpose: <b>every evaluation is its own fact</b>.
    /// There is no dedupe key here and none is wanted — two dispatches of the same role against the same
    /// snapshot in the same second are two decisions, and collapsing them would erase precisely the
    /// concurrency this ledger was built to measure.
    /// </remarks>
    internal static readonly JsonLinesLedger<RunwayAdmissionEntry> Ledger =
        new("baton-runway-admissions", "runway admission ledger", _ => null);

    /// <summary>
    /// Decides this dispatch's admission against everything already reserved on the same vendor, appends
    /// the resulting fact, and returns it — all inside one lock acquisition.
    /// </summary>
    /// <remarks>
    /// <b>The reservation arm can only ever turn an Admit into a Hold, and only when the counters were
    /// readable.</b> A gate Hold is recorded as it stands, and an
    /// <see cref="RunwayAdmissionRequest.Unmeasured"/> vendor is never touched by the arithmetic at all —
    /// spec/baton.md §7 states why those two are excluded and what excluding them protects.
    /// </remarks>
    /// <exception cref="IOException">Propagated, exactly as the other two ledgers do — the caller logs and swallows.</exception>
    public static Task<RunwayAdmissionEntry> ReserveAndRecordAsync(
        RunwayAdmissionRequest request, string ledgerFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(ledgerFilePath);

        JsonLinesLedger<RunwayAdmissionEntry>.EnsureParentDirectory(ledgerFilePath);

        return Ledger.RunUnderLockAsync(
            ledgerFilePath,
            () =>
            {
                var entry = Decide(request, Ledger.ReadAllUnlocked(ledgerFilePath));
                Append(entry, ledgerFilePath);
                return entry;
            },
            cancellationToken);
    }

    /// <summary>This ledger's rows, oldest first. Delegated whole to <see cref="JsonLinesLedger{TEntry}.ReadAllAsync"/>.</summary>
    public static Task<IReadOnlyList<RunwayAdmissionEntry>> ReadAllAsync(
        string ledgerFilePath, CancellationToken cancellationToken = default) =>
        Ledger.ReadAllAsync(ledgerFilePath, cancellationToken);

    /// <summary>
    /// The pure decision half — <paramref name="existingRows"/> is the ledger as already written, and
    /// nothing here touches a file. Public because it has a production caller besides
    /// <see cref="ReserveAndRecordAsync"/>: the dispatch path's fail-open arm builds the same fact from an
    /// empty row list when the ledger could not be written at all, so a fleet with an unwritable ledger
    /// still gets the counters' own verdict rather than a crash. It is also what lets the reconciliation
    /// rule be pinned by a test without a file lock.
    /// </summary>
    public static RunwayAdmissionEntry Decide(
        RunwayAdmissionRequest request, IReadOnlyList<RunwayAdmissionEntry> existingRows)
    {
        var baseEntry = new RunwayAdmissionEntry(
            At: request.At,
            Vendor: request.Vendor,
            Decision: RunwayAdmissionDecisions.Admitted,
            DecidedBy: RunwayAdmissionDecidedBy.Counters,
            Reason: request.GateReason,
            OverrideReason: request.OverrideReason,
            Room: request.Room,
            Role: request.Role,
            Counters: request.Counters,
            WeekHoldPct: request.WeekHoldPct,
            SessionHoldPct: request.SessionHoldPct,
            MaxSnapshotAgeHours: request.MaxSnapshotAgeHours,
            SnapshotHarvestedAt: request.SnapshotHarvestedAt,
            HeadroomPoints: request.HeadroomPoints,
            EstimatedBurnPoints: request.Estimate.Points,
            EstimateSource: request.Estimate.Source);

        if (request.Unmeasured)
        {
            return baseEntry with
            {
                Decision = RunwayAdmissionDecisions.Unmeasured,
                DecidedBy = RunwayAdmissionDecidedBy.Unmeasured,
            };
        }

        if (request.GateHeld)
        {
            return baseEntry with { Decision = HoldToken(request) };
        }

        // The counters admitted. Everything below is #1896's own arm; it runs only here, so it can
        // never be what refuses a vendor whose snapshot was unreadable or absent.
        if (request.SnapshotHarvestedAt is not { } harvestedAt || request.HeadroomPoints is not { } headroom)
        {
            return baseEntry;
        }

        var outstanding = OutstandingPoints(existingRows, request.Vendor, harvestedAt);
        var withThisOne = outstanding + request.Estimate.Points;
        var reserved = baseEntry with { OutstandingReservationPoints = outstanding };

        if (withThisOne <= headroom)
        {
            return reserved;
        }

        return reserved with
        {
            Decision = HoldToken(request),
            DecidedBy = RunwayAdmissionDecidedBy.Reservation,
            Reason =
                $"{Points(withThisOne)} points of runway are reserved by dispatches this vendor's last harvest "
                + $"cannot have seen yet ({Points(outstanding)} already outstanding, {Points(request.Estimate.Points)} "
                + $"estimated for this one, source '{request.Estimate.Source}'), against {Points(headroom)} points of headroom",
        };
    }

    /// <summary>
    /// The spend on <paramref name="vendor"/> that the snapshot harvested at <paramref name="harvestedAt"/>
    /// cannot have counted: every row recorded at or after that instant whose work actually proceeded.
    /// A plain <see cref="RunwayAdmissionDecisions.Held"/> row is excluded because that dispatch never
    /// ran, and an <see cref="RunwayAdmissionDecisions.Unmeasured"/> one because it is on a vendor with no
    /// counters to reserve against.
    /// </summary>
    private static double OutstandingPoints(
        IReadOnlyList<RunwayAdmissionEntry> rows, string vendor, DateTimeOffset harvestedAt)
    {
        double total = 0;
        foreach (var row in rows)
        {
            if (!string.Equals(row.Vendor, vendor, StringComparison.OrdinalIgnoreCase)
                || row.At < harvestedAt
                || row.EstimatedBurnPoints is not { } points
                || points <= 0)
            {
                continue;
            }

            if (row.Decision is RunwayAdmissionDecisions.Admitted or RunwayAdmissionDecisions.HeldOverridden)
            {
                total += points;
            }
        }

        return total;
    }

    private static string HoldToken(RunwayAdmissionRequest request) =>
        request.OverrideReason is { Length: > 0 }
            ? RunwayAdmissionDecisions.HeldOverridden
            : RunwayAdmissionDecisions.Held;

    /// <summary>Invariant, two decimals at most: the refusal text is asserted on, and a comma decimal
    /// separator is not what a message contract should turn on — <c>RunwayGate</c>'s own staleness
    /// message states the same rationale.</summary>
    private static string Points(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Appends one row. Caller must already hold this ledger's <see cref="MutexGuardedFileLock"/>;
    /// serialized through <see cref="JsonLinesLedger{TEntry}.SerializerOptions"/> so this write and
    /// <see cref="ReadAllAsync"/> can never disagree about the wire format.
    /// </summary>
    private static void Append(RunwayAdmissionEntry entry, string ledgerFilePath)
    {
        var line = System.Text.Json.JsonSerializer.Serialize(entry, Ledger.SerializerOptions) + "\n";
        using var stream = new FileStream(
            ledgerFilePath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: false);
        stream.Write(Encoding.UTF8.GetBytes(line));
        stream.Flush();
    }
}
