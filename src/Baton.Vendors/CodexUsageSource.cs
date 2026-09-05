using System.Globalization;
using Baton.Status;

namespace Baton.Vendors;

/// <summary>
/// #1904's third <see cref="IVendorUsageSource"/>. Unlike <see cref="ClaudeUsageSlashCommandSource"/>
/// and <see cref="AgyUsageSlashCommandSource"/>, this one <b>spawns nothing</b> and reads no vendor
/// report: it derives codex's plan-relative burn from Baton's own fleet burn ledger
/// (<see cref="BatonPaths.QuotaLedgerFile"/>, spec/baton.md §7), and labels every snapshot it produces
/// <see cref="VendorUsageProvenance.Derived"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a derivation, when a vendor surface demonstrably exists.</b> codex-cli 0.153.2's app-server
/// answers <c>account/rateLimits/read</c> with three windows carrying <c>usedPercent</c> and
/// <c>resetsAt</c> — measured live on 2026-09-04 and summarised in
/// <c>docs/vendor-codex-probe-2026-09-04.md</c>. What that probe did <i>not</i> record is the payload's
/// own shape: <c>tools/Baton.VendorProbe/CodexProbe.CollectRateLimitWindows</c> walks the response
/// recursively and throws the property path away, no response fixture is in the probe's fixture index,
/// and the probe's own known-unknowns forbid naming the windows or mapping them to ChatGPT product
/// limits. A parser written against that today would be a guess. <b>This source is therefore interim</b>
/// — the replacement is a <c>CodexRateLimitsSource</c> reading the real response through
/// <see cref="CodexAppServerBroker"/> (already an approved spawn site, already speaking app-server
/// JSON-RPC) once one authenticated probe run records the payload.
/// <c>docs/vendor-codex-probe-2026-09-04.md</c> — its measured findings and its known-unknowns — is
/// that finding's register, and the one place to correct it; this comment is not.
/// </para>
/// <para>
/// <b>Rolling windows, not the vendor's own boundaries.</b> When ChatGPT's 5-hour and weekly windows
/// actually begin is unmeasured, so both windows here are a ROLLING lookback from the harvest instant
/// — the last 5 hours and the last 7 days. <see cref="VendorUsageWindow.ResetsAt"/> is therefore always
/// null: a rolling window has no reset instant, and inventing one would be exactly the relabelling the
/// probe's known-unknowns rule out. <see cref="VendorUsageWindow.Name"/> says "rolling" and "derived"
/// in the vendor's place, for the same reason.
/// </para>
/// <para>
/// <b>A rolling total is NOT monotonic, so these windows carry no burn ring.</b> A vendor's counter
/// only falls when its window resets, which is the assumption #1746's ring rests on — a percent below
/// the previous sample means "rolled over, start a new ring". A rolling lookback falls whenever an old
/// row ages out of the back of the window with nothing new at the front, with no reset having happened,
/// so feeding these readings to that ring would produce a burn rate and a minutes-to-exhaustion built
/// on a boundary that never occurred. Making the figure monotonic instead (cumulative since the
/// window's start, reset at its boundary) would require naming when a ChatGPT window begins — the exact
/// thing the paragraph above says is unmeasured. So the ring is skipped for every
/// <see cref="VendorUsageProvenance.Derived"/> snapshot instead: <c>VendorUsageBurn.Advance</c> owns
/// that rule, spec/baton.md §6's <c>windows[]</c> table states it canonically, and the effect on the
/// glass is that a derived window's rate and ETA read "unknown" rather than reading as a number.
/// </para>
/// <para>
/// <b>What the number covers, and what it misses.</b> The ledger holds one row per <i>settled</i>
/// Baton execution (<c>QuotaLedgerStore.BuildEntries</c>: both a recorded start and exit). So the total
/// is a LOWER BOUND on the plan's real burn — it excludes an in-flight codex lane, every codex turn the
/// operator ran outside Baton, and any lane that died before settling. That is a floor, never a
/// ceiling, so it can never make an exhausted plan look fresh by more than these known omissions;
/// <see cref="Caveat"/> says so verbatim on every snapshot.
/// </para>
/// <para>
/// <b>Percent needs a ceiling the operator declares.</b> No Codex token allowance is measured anywhere
/// in this tree, so <see cref="VendorUsageWindow.PercentUsed"/> is null unless
/// <see cref="CodexPlanCeilingSettings"/> carries one — "unparsed → unknown, never a number" (#1391)
/// applied to a quantity nobody has measured. With a ceiling declared, the percentage is
/// tokens/ceiling capped at 100: a burn past the declared ceiling still reads as 100% rather than
/// wrapping past it, and 100% is at or above every threshold a gate could compare it to.
/// </para>
/// <para>
/// <b>Not on <c>RunwayGate.WindowNames</c>, deliberately.</b> Adding these names there would make the
/// gate's own "a recognized window whose percentage did not parse → Hold" arm fire on every codex
/// dispatch of an operator who has declared no ceiling — holding the newest vendor on the fleet for
/// having no counter, which is precisely the case #1848 admits as unmeasured instead. codex IS on
/// <see cref="RunwayGate.MeasuredVendors"/>, so the glass gets its block and a snapshot file exists;
/// wiring the gate's table is a follow-up that needs the conditional-entry decision taken first.
/// </para>
/// </remarks>
public sealed class CodexUsageSource : IVendorUsageSource
{
    public string Vendor => "codex";

    /// <summary>The rolling short window's length. Named after the ChatGPT limit it stands in for; the
    /// boundary is Baton's own (rolling from the harvest instant), not the vendor's.</summary>
    public static readonly TimeSpan FiveHourWindow = TimeSpan.FromHours(5);

    /// <summary>The rolling long window's length.</summary>
    public static readonly TimeSpan WeeklyWindow = TimeSpan.FromDays(7);

    public const string FiveHourWindowName = "rolling 5h (derived)";
    public const string WeeklyWindowName = "rolling 7d (derived)";

    /// <summary>
    /// The machine-local disclaimer every snapshot from this source carries — the counterpart to
    /// claude's own "Approximate, based on local sessions on this machine", except that this one is
    /// Baton's, not a vendor's, and says so.
    /// </summary>
    public const string DerivedCaveat =
        "Derived by Baton from its own burn ledger, not a Codex counter: settled Baton-launched codex "
        + "executions on this machine only, over a rolling window. A lower bound — in-flight lanes and "
        + "any Codex use outside Baton are not counted.";

    private readonly string _ledgerFilePath;
    private readonly Func<CancellationToken, Task<DaemonSettings>> _loadSettings;

    public CodexUsageSource()
        : this(BatonPaths.QuotaLedgerFile, ct => DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, ct))
    {
    }

    /// <summary>
    /// Test-only seam (Baton.Vendors.Tests, via <c>InternalsVisibleTo</c>): substitutes the ledger path
    /// and the settings load so both the aggregation and the ceiling-declared/ceiling-absent polarity
    /// can be driven from a fixture ledger without touching <c>~/.baton</c>.
    /// </summary>
    internal CodexUsageSource(string ledgerFilePath, Func<CancellationToken, Task<DaemonSettings>> loadSettings)
    {
        _ledgerFilePath = ledgerFilePath;
        _loadSettings = loadSettings;
    }

    public async Task<VendorUsageSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (!File.Exists(_ledgerFilePath))
        {
            return null;
        }

        var entries = await QuotaLedgerStore.ReadDistinctByExecutionAsync(_ledgerFilePath, cancellationToken)
            .ConfigureAwait(false);
        var settings = await _loadSettings(cancellationToken).ConfigureAwait(false);

        return Aggregate(entries, settings.CodexPlanCeiling, now);
    }

    /// <summary>
    /// Pure aggregation over already-read ledger rows — no file, no clock beyond the caller-supplied
    /// <paramref name="now"/>, matching the other two sources' <c>Parse</c> split so every fixture in
    /// <c>tests/Baton.Vendors.Tests</c> exercises this directly.
    /// <para>
    /// Returns <b>null</b>, not an empty snapshot, when no codex row has ever been recorded — the
    /// <see cref="IVendorUsageSource.ReadAsync"/> contract's "did not harvest at all" case, which is
    /// what tells <c>VendorUsageHarvester</c> to leave any last-good snapshot alone (#1869) and what
    /// keeps a codex block out of the glass on a fleet that has never run one. A window whose rolling
    /// lookback happens to hold no rows is a different thing entirely: that IS a harvest, and it
    /// reports zero.
    /// </para>
    /// </summary>
    public static VendorUsageSnapshot? Aggregate(
        IReadOnlyList<QuotaLedgerEntry> entries,
        CodexPlanCeilingSettings? ceiling,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);

        List<(DateTimeOffset At, long Tokens)> codexRows = [];
        var sawCodexRow = false;

        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Adapter, "codex", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.At is not { } at)
            {
                // A codex row with no timestamp cannot be placed in ANY window, so it is not evidence
                // that codex has been seen either: counting it would turn a ledger of undatable rows
                // into a harvest reporting a measured zero across both windows, which is a stronger
                // claim than "nothing is known about when this ran". Deliberately BEFORE sawCodexRow.
                continue;
            }

            sawCodexRow = true;
            if (BilledTokens(entry) is not { } tokens)
            {
                // A datable row reporting none of the three billed dimensions IS evidence codex ran --
                // it just contributes no tokens and no execution count to the window it lands in.
                continue;
            }

            codexRows.Add((ToUtcOffset(at), tokens));
        }

        if (!sawCodexRow)
        {
            return null;
        }

        return new VendorUsageSnapshot(
            "codex",
            now,
            DerivedCaveat,
            [
                Window(FiveHourWindowName, codexRows, now, FiveHourWindow, ceiling?.EffectiveFiveHourTokens),
                Window(WeeklyWindowName, codexRows, now, WeeklyWindow, ceiling?.EffectiveWeeklyTokens),
            ],
            VendorUsageProvenance.Derived);
    }

    private static VendorUsageWindow Window(
        string name,
        IReadOnlyList<(DateTimeOffset At, long Tokens)> rows,
        DateTimeOffset now,
        TimeSpan length,
        long? ceilingTokens)
    {
        var since = now - length;
        long tokens = 0;
        var executions = 0;
        foreach (var row in rows)
        {
            // Closed on both ends: a row exactly on the trailing edge is IN, so a row cannot fall out
            // of and back into the same window across two harvests a tick apart.
            //
            // A ledger row carries ONE instant -- the execution's settle instant (QuotaLedgerEntry.At,
            // written when the execution finished) -- so an execution's whole burn lands at that single
            // point rather than being spread across the wall-clock time it actually ran. A six-hour
            // codex lane therefore contributes all of its tokens to the five-hour window it settled in
            // and none to the earlier one it spent most of its time in. That is a placement
            // approximation on top of the lower-bound caveat, not a second undercount: no tokens are
            // lost, they are attributed to the settle instant.
            if (row.At >= since && row.At <= now)
            {
                tokens += row.Tokens;
                executions++;
            }
        }

        int? percentUsed = null;
        if (ceilingTokens is { } ceiling)
        {
            percentUsed = (int)Math.Min(100, Math.Round(tokens * 100.0 / ceiling, MidpointRounding.AwayFromZero));
        }

        var against = ceilingTokens is { } declared
            ? $" of the operator-declared {declared.ToString("N0", CultureInfo.InvariantCulture)}-token ceiling"
            : " (no plan ceiling declared, so no percentage)";

        var rawLine =
            $"derived: {tokens.ToString("N0", CultureInfo.InvariantCulture)} billed tokens across "
            + $"{executions} settled codex execution{(executions == 1 ? string.Empty : "s")} in the "
            + $"{name} window ending {now.ToString("O", CultureInfo.InvariantCulture)}{against}";

        return new VendorUsageWindow(name, percentUsed, ResetsAt: null, rawLine);
    }

    /// <summary>
    /// The additive quantity a plan burn is counted in — <c>tokensIn + tokensOut + cacheCreation</c>,
    /// deliberately excluding thinking tokens and cache reads. Not a new definition:
    /// <c>WorkerUsage.BilledTokens</c> (#1682, spec/baton.md §3) already settled this exact sum as what
    /// a token budget arrests on, and this reuses it rather than inventing a second one. Null when the
    /// row reported none of the three, so "the vendor reported nothing" stays distinguishable from
    /// "the vendor reported zero".
    /// </summary>
    private static long? BilledTokens(QuotaLedgerEntry entry)
    {
        if (entry.TokensIn is null && entry.TokensOut is null && entry.CacheCreationTokens is null)
        {
            return null;
        }

        return (entry.TokensIn ?? 0) + (entry.TokensOut ?? 0) + (entry.CacheCreationTokens ?? 0);
    }

    /// <summary>
    /// <see cref="QuotaLedgerEntry.At"/> is a <see cref="DateTime"/> written from Core's own
    /// <c>WriterUtcTimestamp</c>, but a JSON round-trip can hand it back as
    /// <see cref="DateTimeKind.Unspecified"/>. Treating an unspecified kind as LOCAL — which
    /// <see cref="DateTimeOffset"/>'s own implicit conversion does — would shift every row by the
    /// machine's offset and silently move rows in and out of a five-hour window.
    /// </summary>
    private static DateTimeOffset ToUtcOffset(DateTime at) =>
        at.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(at, TimeSpan.Zero),
            DateTimeKind.Unspecified => new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc), TimeSpan.Zero),
            _ => new DateTimeOffset(at.ToUniversalTime(), TimeSpan.Zero),
        };
}
