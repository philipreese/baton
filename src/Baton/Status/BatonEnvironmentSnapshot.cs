namespace Baton.Status;

/// <summary>
/// The frozen read of every baton-config environment variable a production hot path used to
/// re-read on every access (#1496). One process-wide read, taken once and never mutated, replaces
/// the "resolve, never capture" discipline <see cref="BatonPaths"/> used to document — that
/// discipline is what forced #1491's <c>SerializedEnvironmentCollection</c>: any test that flipped
/// one of these variables could race a production reader re-deriving the same value mid-process.
/// Freezing removes the race at its root; <see cref="BeginScope"/> gives tests an explicit,
/// non-mutating way to supply a different set of values instead.
/// </summary>
/// <remarks>
/// <para>
/// Every field here is the raw string exactly as <c>Environment.GetEnvironmentVariable</c> would
/// return it — <c>null</c> when unset. Parsing (bounds-clamping, bool coercion, blank-as-unset) stays
/// at each consumer, unchanged; this type's only job is freezing the read, not the interpretation.
/// </para>
/// <para>
/// <b>Only two fields from the original #1496 fold, not the six the task named — this is the
/// canonical record of why.</b> <see cref="BatonPaths"/>'s <c>BATON_HOME</c> read and
/// <c>McpCommand.cs</c>'s <c>BATON_OUTPUT_DIR</c> read are folded here. (A third field,
/// <see cref="RepoOverride"/>, was added later by #1645 for a brand-new reader —
/// <c>Baton.Cli.InstalledVersionDrift</c> — with no such history to fight; it never needed the
/// fold/revert dance below.)
/// The other four direct readers were tried against this snapshot and reverted in #1496, each
/// because its own test suite set an env var per <c>[Fact]</c> and expected the very next call to
/// observe it — the resolution behaviour IS the subject under test there, which a
/// frozen-at-first-access snapshot cannot support on its own:
/// <list type="bullet">
/// <item><c>RoomRetentionSweep.cs</c> (<c>IsEnabled</c>/<c>IsPruneEnabled</c>/<c>GetInterval</c>/
///   <c>GetThresholdBytes</c>/<c>GetPruneGrace</c>): 5 of 12 <c>RoomRetentionSweepTests</c> failed
///   under the fold;</item>
/// <item><c>ClaudeWorkerAdapter.cs</c>'s <c>BatonClaudeConfigRootVariable</c> read:
///   <c>ClaudeWorkerAdapterTests.Claude_config_root_set_injects_CLAUDE_CONFIG_DIR_for_batch_and_gate</c>
///   failed under the fold;</item>
/// <item><c>WorkerRoleCatalog.cs</c>'s <c>ResolvePath</c>: 13 of 20 <c>WorkerRoleCatalogTests</c>
///   (and <c>CatalogNamespaceTests</c>) failed under the fold;</item>
/// <item><c>WorkflowTemplateCatalog.cs</c>'s <c>ResolvePath</c>: 20 of 21 tests in that assembly
///   failed under the fold — same shape as <c>WorkerRoleCatalog</c>'s.</item>
/// </list>
/// #1524 folds all four anyway, closing the gap the sentence above names rather than working around
/// it: each reader's own test suite moved off <c>Environment.SetEnvironmentVariable</c> onto
/// <see cref="BeginScope"/> in the same change that added its field here (the way #1496 already
/// migrated <c>FleetStatusToolTests</c>/<c>RoomDetailToolTests</c>/<c>DaemonHostTests</c>). A scope
/// is an explicit per-async-flow override, not a process mutation the frozen snapshot could ever
/// see — so "the very next call observes it" holds again, just through a different mechanism than
/// the one the #1496 test suites assumed. Nothing here relies on re-reading the environment after
/// process start.
/// </para>
/// <para>
/// <b>What is deliberately absent for a different reason.</b> Two families of direct env read stay
/// on <c>Environment.GetEnvironmentVariable</c> and are never candidates for this type: the
/// genuinely-once reads at the top of <c>Program.cs</c> (read a single time before any work starts,
/// never re-read, so there is no per-access race to remove), and <c>InheritedEnvironment.cs</c>'s
/// child-process allowlist (that reader is about the *live* environment a spawned worker should
/// inherit, not AER's own config — freezing it would silently stop a worker from inheriting a
/// variable an operator exports mid-session).
/// </para>
/// </remarks>
public sealed record BatonEnvironmentSnapshot(
    string? HomeOverride,
    string? McpOutputDirectory,
    string? RepoOverride = null,
    string? WorkerTiersPathOverride = null,
    string? WorkerRolesPathOverride = null,
    string? WorkflowTemplatesPathOverride = null,
    string? ClaudeConfigRootOverride = null,
    string? RetentionSweepEnabledOverride = null,
    string? RetentionSweepIntervalSecondsOverride = null,
    string? RetentionSweepThresholdBytesOverride = null,
    string? RetentionPruneEnabledOverride = null,
    string? RetentionPruneGraceSecondsOverride = null,
    string? WatchReaperRetentionHoursOverride = null,
    string? FleetProjectionIntervalSecondsOverride = null,
    string? ExecutionProgressIntervalSecondsOverride = null,
    string? DeliveryPollIntervalSecondsOverride = null,
    string? SkillsPathOverride = null)
{
    private static readonly Lazy<BatonEnvironmentSnapshot> ProcessSnapshot = new(CaptureFromEnvironment);

    private static readonly AsyncLocal<BatonEnvironmentSnapshot?> AmbientOverride = new();

    /// <summary>
    /// Every field null (nothing overridden) — a base for a test's <c>with</c> expression that only
    /// cares about one or two fields and wants to be explicit about the rest, rather than inheriting
    /// whatever <see cref="Current"/> happens to hold on the machine running it.
    /// </summary>
    /// <remarks>
    /// <b>Wrong base for a partial override whose code path also touches an unrelated field this
    /// snapshot carries</b> (#1524's own hazard: <c>ClaudeWorkerAdapterTests</c>'s two config-root
    /// tests built their scope from <c>Blank with { ClaudeConfigRootOverride = … }</c>, which — because
    /// <see cref="BeginScope"/> replaces the whole ambient snapshot, not just the named field — also
    /// blanked <see cref="HomeOverride"/> back to null for the scope's lifetime. <c>BatonPaths.Root</c>
    /// reads <c>HomeOverride</c> too, so inside that scope it fell through to the real
    /// <c>{UserProfile}/.baton</c> instead of the test assembly's redirected home
    /// (<c>tests/Shared/BatonHomeRedirect.cs</c>), and <c>ClaudeWorkerAdapter.Resolve</c> writes its
    /// launch config there on every call — leaking into the real <c>~/.baton</c>). A scope that only
    /// means to override one field must build from <see cref="Current"/>, carrying every other ambient
    /// field — including any redirect already in force — forward unchanged; reach for <see cref="Blank"/>
    /// only when the code path under test provably never resolves a field left unset.
    /// </remarks>
    public static readonly BatonEnvironmentSnapshot Blank = new(
        HomeOverride: null,
        McpOutputDirectory: null,
        RepoOverride: null,
        WorkerTiersPathOverride: null,
        WorkerRolesPathOverride: null,
        WorkflowTemplatesPathOverride: null,
        ClaudeConfigRootOverride: null,
        RetentionSweepEnabledOverride: null,
        RetentionSweepIntervalSecondsOverride: null,
        RetentionSweepThresholdBytesOverride: null,
        RetentionPruneEnabledOverride: null,
        RetentionPruneGraceSecondsOverride: null,
        WatchReaperRetentionHoursOverride: null,
        FleetProjectionIntervalSecondsOverride: null,
        ExecutionProgressIntervalSecondsOverride: null,
        DeliveryPollIntervalSecondsOverride: null,
        SkillsPathOverride: null);

    /// <summary>
    /// The snapshot every reader resolves against: an explicit <see cref="BeginScope"/> override on
    /// the calling async flow when one is active, otherwise the one process snapshot captured on
    /// first access. Never re-reads the environment after that first capture.
    /// </summary>
    public static BatonEnvironmentSnapshot Current => AmbientOverride.Value ?? ProcessSnapshot.Value;

    private static BatonEnvironmentSnapshot CaptureFromEnvironment() => new(
        HomeOverride: Environment.GetEnvironmentVariable(BatonPaths.HomeEnvironmentVariable),
        // "BATON_OUTPUT_DIR" -- mirrors the literal McpCommand.cs reads. Program.cs reads the same
        // variable name for the hook-check commands; that read stays direct (see the type remarks) --
        // this field only covers the McpCommand.cs per-access read.
        McpOutputDirectory: Environment.GetEnvironmentVariable("BATON_OUTPUT_DIR"),
        // "BATON_REPO" -- mirrors the literal Baton.Cli.InstalledVersionDrift.RepoEnvironmentVariable
        // (#1645). That type lives downstream of this project (Baton.Cli depends on Baton, not the
        // reverse), the same reason McpOutputDirectory's name above is a duplicated literal rather than
        // a shared const.
        RepoOverride: Environment.GetEnvironmentVariable("BATON_REPO"),
        // #1524: the remaining eight fields mirror literal consts owned by downstream projects
        // (Baton.Vendors, Baton.Cli) for the same reason RepoOverride's does -- this project is the
        // base of the dependency graph (Baton.Vendors -> Baton, Baton.Cli -> Baton.Vendors -> Baton),
        // so it cannot reference those consts without a cycle. Each downstream reader's own doc comment
        // names the matching field here; this is the one place that owns the literal string.
        // "BATON_WORKER_TIERS_PATH" -- Baton.Vendors.WorkerRoleCatalog.TiersPathEnvironmentVariable.
        WorkerTiersPathOverride: Environment.GetEnvironmentVariable("BATON_WORKER_TIERS_PATH"),
        // "BATON_WORKER_ROLES_PATH" -- Baton.Vendors.WorkerRoleCatalog.RolesPathEnvironmentVariable.
        WorkerRolesPathOverride: Environment.GetEnvironmentVariable("BATON_WORKER_ROLES_PATH"),
        // "BATON_WORKFLOW_TEMPLATES_PATH" --
        // Baton.Vendors.WorkflowTemplateCatalog.TemplatesPathEnvironmentVariable.
        WorkflowTemplatesPathOverride: Environment.GetEnvironmentVariable("BATON_WORKFLOW_TEMPLATES_PATH"),
        // "BATON_CLAUDE_CONFIG_ROOT" -- Baton.Vendors.ClaudeWorkerAdapter.BatonClaudeConfigRootVariable.
        ClaudeConfigRootOverride: Environment.GetEnvironmentVariable("BATON_CLAUDE_CONFIG_ROOT"),
        // The five BATON_RETENTION_* variables -- Baton.Cli.Daemon.RoomRetentionSweep's own
        // *EnvironmentVariable consts.
        RetentionSweepEnabledOverride: Environment.GetEnvironmentVariable("BATON_RETENTION_SWEEP_ENABLED"),
        RetentionSweepIntervalSecondsOverride: Environment.GetEnvironmentVariable("BATON_RETENTION_SWEEP_INTERVAL_SECONDS"),
        RetentionSweepThresholdBytesOverride: Environment.GetEnvironmentVariable("BATON_RETENTION_SWEEP_THRESHOLD_BYTES"),
        RetentionPruneEnabledOverride: Environment.GetEnvironmentVariable("BATON_RETENTION_PRUNE_ENABLED"),
        RetentionPruneGraceSecondsOverride: Environment.GetEnvironmentVariable("BATON_RETENTION_PRUNE_GRACE_SECONDS"),
        // "BATON_WATCH_REAPER_RETENTION_HOURS" -- Baton.Cli.Daemon.WatchSweep's own
        // ReaperRetentionHoursEnvironmentVariable (#1488 fix round, spec/baton.md §2).
        WatchReaperRetentionHoursOverride: Environment.GetEnvironmentVariable("BATON_WATCH_REAPER_RETENTION_HOURS"),
        // "BATON_FLEET_PROJECTION_INTERVAL_SECONDS" -- Baton.Cli.Daemon.FleetProjectionWriter's own
        // IntervalSecondsEnvironmentVariable (#1557), same fold reason as the five BATON_RETENTION_*
        // fields above.
        FleetProjectionIntervalSecondsOverride: Environment.GetEnvironmentVariable("BATON_FLEET_PROJECTION_INTERVAL_SECONDS"),
        // "BATON_EXECUTION_PROGRESS_INTERVAL_SECONDS" -- #1549's heartbeat cadence,
        // Baton.Cli.ExecutionProgressHeartbeat.IntervalSecondsEnvironmentVariable.
        ExecutionProgressIntervalSecondsOverride: Environment.GetEnvironmentVariable("BATON_EXECUTION_PROGRESS_INTERVAL_SECONDS"),
        // "BATON_DELIVERY_POLL_INTERVAL_SECONDS" -- #734's delivery poll cadence,
        // Baton.Cli.Daemon.DeliveryPoller.IntervalSecondsEnvironmentVariable.
        DeliveryPollIntervalSecondsOverride: Environment.GetEnvironmentVariable("BATON_DELIVERY_POLL_INTERVAL_SECONDS"),
        // "BATON_SKILLS_PATH" -- #1151's canonical skill package resolver, rung 1
        // (Baton.Vendors.SkillPackageResolver.SkillsPathEnvironmentVariable). Same duplicated-literal
        // reason as every field above it: this project is the base of the dependency graph.
        SkillsPathOverride: Environment.GetEnvironmentVariable("BATON_SKILLS_PATH"));

    /// <summary>
    /// Test-only seam (via <c>InternalsVisibleTo</c>): makes <paramref name="snapshot"/> the ambient
    /// override for every <see cref="Current"/> read on the calling async flow until the returned
    /// scope is disposed, restoring whatever was ambient before. Never mutates process environment
    /// variables, so a scoped test needs no <c>SerializedEnvironmentCollection</c> enrollment and runs
    /// parallel-safe with everything else.
    /// </summary>
    /// <remarks>
    /// <b>Flows further than it might look.</b> <see cref="AsyncLocal{T}"/> flows through
    /// <c>async</c>/<c>await</c>, <c>Task.Run</c>, <em>and</em> a manually-created
    /// <see cref="System.Threading.Thread"/> — <c>Thread.Start</c> captures and runs on the calling
    /// thread's <see cref="System.Threading.ExecutionContext"/> by default, so code on a raw thread
    /// still sees an active scope. The real boundary is
    /// <see cref="System.Threading.ExecutionContext.SuppressFlow"/> (and its sibling opt-out,
    /// <see cref="System.Threading.Thread.UnsafeStart()"/>): code started from inside a suppressed
    /// region — whatever kind of thread it runs on — sees the process snapshot regardless of an
    /// active scope on the thread that suppressed flow. See <c>BatonEnvironmentSnapshotTests</c> for
    /// the tripwire documentation of both facts.
    /// </remarks>
    internal static IDisposable BeginScope(BatonEnvironmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new Scope(snapshot);
    }

    private sealed class Scope : IDisposable
    {
        private readonly BatonEnvironmentSnapshot? _prior;
        private bool _disposed;

        public Scope(BatonEnvironmentSnapshot snapshot)
        {
            _prior = AmbientOverride.Value;
            AmbientOverride.Value = snapshot;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            AmbientOverride.Value = _prior;
        }
    }
}
