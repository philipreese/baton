using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Domain;
using Baton.Status;

namespace Baton.Vendors;

/// <summary>
/// The volatile half of a worker role (#888): which vendor/model/effort actually runs it. Lives in
/// <c>WorkerTiers.json</c>, separate from the roles, so swapping a model is one edit that every role
/// on the tier inherits — and, because the catalog is read at runtime rather than embedded, that edit
/// needs no rebuild (drop a <c>worker-tiers.json</c> under <see cref="BatonPaths.Root"/>, or point
/// <see cref="WorkerRoleCatalog.TiersPathEnvironmentVariable"/> at one).
/// </summary>
public sealed record WorkerTier([property: JsonRequired] string Adapter, string? Model, string? Effort);

/// <summary>
/// A composable worker-role profile — the building block the front door (#887) composes into
/// workflows. The <b>stable</b> half (grant, timeout, verdict, purpose) is authored in
/// <c>WorkerRoles.json</c>; the <b>volatile</b> half (<see cref="Adapter"/>/<see cref="Model"/>/
/// <see cref="Effort"/>) is resolved from the role's <see cref="Tier"/> in <c>WorkerTiers.json</c>.
/// A role never names a vendor or model directly, so a model swap never touches a role's capability.
/// </summary>
/// <param name="VerifyPixiTask">
/// #1623 (contract: <c>spec/baton.md</c> §3, "Engine-run verify and the token budget", which states
/// the actual build-lock mechanism -- not restated here): the <c>pixi run &lt;task&gt;</c> task name
/// the ENGINE runs once, itself holding no lock across the run, once this role's worker has exited 0
/// with a satisfied output contract -- never the worker itself. Null (every role but
/// <c>implement</c>) means no verify step; the worker's own exit-0 + satisfied-contract IS the whole
/// story for that role, same as before this issue.
/// </param>
/// <param name="TokenBudget">
/// #1623 (contract: <c>spec/baton.md</c> §3): the default per-execution token ceiling for this role
/// (measured from the same usage the vendor's stream-json reports mid-execution, not just the
/// terminal line) -- crossing it arrests the execution. Null means no budget is enforced for a role
/// that declares none; <c>--token-budget</c> overrides this per dispatch
/// (<see cref="RoleDispatch.ToBinding"/>'s own parameter). #1745: see <see cref="TokenBudgetSpec"/> for
/// the two shapes this field can now take and how each resolves.
/// </param>
/// <param name="MaxToolSteps">
/// #1682 (contract: <c>spec/baton.md</c> §3): the default per-execution tool-step ceiling -- a second
/// arrest trigger, independent of <see cref="TokenBudget"/>, fired by <c>Mutation.TokenBudgetMonitor</c>
/// on tool-step LINE COUNT rather than token volume. Null means no cap for a role that declares none;
/// <c>--max-tool-steps</c> overrides this per dispatch (#1686 review F11,
/// <see cref="RoleDispatch.ToBinding"/>'s own parameter).
/// </param>
/// <param name="BilledRateLimit">
/// #1691 (contract: <c>spec/baton.md</c> §3): the default per-execution ceiling on Σ billed tokens
/// inside one trailing <c>Mutation.TokenBudgetMonitor.BilledRateWindow</c> — a third arrest trigger,
/// independent of <see cref="TokenBudget"/> and <see cref="MaxToolSteps"/>, on RATE rather than total
/// volume. EVERY role in <c>WorkerRoles.json</c> leaves this null on purpose, and that is a measurement
/// rather than an omission — <c>spec/baton.md</c> §3 is where the measurement lives, and
/// <c>tools/room-rate-sweep/sweep.py</c> is what re-runs it. <c>--billed-rate-limit</c> supplies one per
/// dispatch (<see cref="RoleDispatch.ToBinding"/>'s own parameter), the only way one is ever set today.
/// </param>
/// <param name="DeliversBranch">
/// #1788: whether <see cref="Mutation.DeliveryVerifier"/>'s own post-exit delivery check
/// (<c>spec/baton.md</c> §3) runs for this role after its worker exits 0. False for every
/// read-shaped role — stated as the rule rather than as a list, because the list went stale the first
/// time a role was added (#2043) — and <c>WorkerRoleCatalogTests</c>'
/// lockstep test pins the direction this DOES assert, every role with this true also has
/// <see cref="PermissionGrant.WriteFiles"/>. <c>janitor</c> writes and commits but stays false too: this
/// field has no independent PR-half switch of its own (<see cref="Mutation.WorkerBinding.Process.ExpectPr"/>
/// is derived entirely from it, per-dispatch override aside), so marking it here would force the PR half
/// on for every janitor dispatch with no per-role way to turn it off — a catalog schema gap, not a
/// judgement that janitor's own stranded-local-commits case doesn't matter. Only <c>implement</c> sets
/// this true today.
/// </param>
/// <param name="AllowsSubagents">
/// #1802: whether this role's worker keeps the vendor's own subagent/fan-out tool (claude's
/// <c>Agent</c>/<c>Task</c>, agy's <c>manage_task</c>/<c>invoke_subagent</c>/<c>define_subagent</c>/
/// <c>manage_subagents</c> trio). Motivation: under <c>baton dispatch</c> the conductor already
/// dispatches a dedicated <c>review</c> lane for every PR, so an implement (or review) worker launching
/// its own in-lane <c>Agent</c> "second reader" is a duplicate review — eight such lanes carried ~80% of
/// one night's cache-read tokens across all implement rooms (#1802). False (the default -- every role
/// but <c>advise</c> leaves this key omitted in <c>WorkerRoles.json</c>) withholds the tool; <c>advise</c>
/// sets it true because weighing options via fan-out is that role's whole point. CLAUDE.md rule 7 names
/// the replacement: under baton dispatch the second reader is the conductor's own review lane, never an
/// in-lane subagent.
/// </param>
/// <param name="VerifiesWorkspace">
/// #2029 (contract: <c>spec/baton.md</c> §3, "Verify command resolution"): whether the engine's
/// post-exit verify runs the WORKSPACE's own gate suite for this role — the <c>.baton/verify</c>
/// declaration and <see cref="VerifyPixiTask"/> arms. True for the two roles that change the tree
/// (<c>implement</c>, <c>janitor</c>): their own work is what the audits grade. False for every
/// role whose grant withholds <see cref="PermissionGrant.WriteFiles"/> — the rule rather than a list,
/// because the list went stale the first time a role was added (#2043); the shipped membership is
/// pinned by name in <c>WorkerRoleCatalogTests</c>, which is also why this comment does not restate
/// it. Such a role writes nothing to the workspace and so would be graded on someone
/// else's red tree — the measured defect. What a read-shaped role IS verified on is its own output
/// contract (<see cref="ProducesVerdict"/> → <c>Domain.ReviewVerdictSchema.TryParse</c> via
/// <c>Outcomes.ContractValidator</c>), which already gates the Succeeded classification this whole
/// step hangs off. <b>Does not gate <c>--verify</c></b>: an operator who types a command for this
/// dispatch gets it, whatever the role — spec/baton.md §3 states why arm 1 stays role-independent.
/// </param>
public sealed record WorkerRole(
    string Id,
    string Tier,
    string Adapter,
    string? Model,
    string? Effort,
    PermissionGrant Grant,
    TimeSpan Timeout,
    bool ProducesVerdict,
    string Purpose,
    IReadOnlyList<WorkerRoleOutput> Outputs,
    string? VerifyPixiTask = null,
    TokenBudgetSpec? TokenBudget = null,
    int? MaxToolSteps = null,
    long? BilledRateLimit = null,
    bool DeliversBranch = false,
    bool AllowsSubagents = false,
    bool VerifiesWorkspace = true);

/// <summary>
/// One file a role's dispatch produces in <c>BATON_OUTPUT_DIR</c> (#897) — the structured, per-role
/// form of what <c>tools/baton-agy-loop/dispatch.py</c> used to spell out inline, before #1759
/// retired it. The front door (#887) declares it as a <see cref="ProducedOutput"/> the engine's
/// <c>ContractValidator</c> checks, and
/// appends <see cref="Instruction"/> to the dispatch prompt so the worker is told to produce exactly
/// what the contract asserts — the name, the shape, and the instruction single-sourced here so a spec
/// prompt stays just the task.
/// </summary>
/// <param name="Schema">
/// The document shape the file must parse as — <see cref="OutputSchema.None"/> for existence-only, or
/// <see cref="OutputSchema.ReviewVerdict"/> for a schema-checked verdict (decision 0043, parse-only).
/// </param>
public sealed record WorkerRoleOutput(string Name, OutputSchema Schema, string Instruction);

/// <summary>
/// The single, shared worker-role catalog — the same <c>WorkerRoles.json</c>/<c>WorkerTiers.json</c>
/// <c>tools/baton-agy-loop/dispatch.py</c> read until #1759 retired it (#888, the #836 shared-source
/// pattern); <c>tools/audit-completeness/completeness.py</c> still reads <c>WorkerTiers.json</c>
/// directly today. Read at runtime, never embedded, so the operator can retune tiers without a rebuild.
/// </summary>
/// <remarks>
/// Resolution order per file:
/// <list type="number">
/// <item>the <c>BATON_WORKER_*_PATH</c> environment override, when set — for a one-off experiment.
///   Folded into <see cref="BatonEnvironmentSnapshot"/> (#1524): resolved once per process (or per
///   active <see cref="BatonEnvironmentSnapshot.BeginScope"/> in a test), not re-read on every
///   access;</item>
/// <item><c>{BatonPaths.Root}/worker-tiers.json</c> (or <c>worker-roles.json</c>) when it exists — the
///   operator's durable, rebuild-free override. <see cref="BatonPaths.Root"/> itself is frozen per
///   process since #1496, so this step no longer observes a <c>BATON_HOME</c> change made after the
///   first resolution anywhere in the process;</item>
/// <item>the default shipped next to the assembly (<see cref="AppContext.BaseDirectory"/>).</item>
/// </list>
/// Tiers and roles resolve independently, so overriding a model does not freeze the role definitions.
/// </remarks>
public static class WorkerRoleCatalog
{
    public const string TiersPathEnvironmentVariable = "BATON_WORKER_TIERS_PATH";
    public const string RolesPathEnvironmentVariable = "BATON_WORKER_ROLES_PATH";

    /// <summary>
    /// #1745: the only adapter names a role's per-adapter <c>token_budget</c> map may key on. Not
    /// <see cref="WorkerAdapterRegistry.Default"/>'s full key set -- that also carries the no-op,
    /// capture, and command test/composition adapters, none of which bill tokens or ever run a role
    /// dispatched through this catalog, so admitting them here would let a typo in a real vendor's name
    /// (e.g. "cluade") pass as a config for a fake one instead of failing loudly at load.
    /// </summary>
    public static readonly IReadOnlyCollection<string> KnownTokenBudgetAdapters = ["claude", "agy", "codex"];

    private const string TiersDefaultFileName = "WorkerTiers.json";
    private const string RolesDefaultFileName = "WorkerRoles.json";
    private const string TiersOverrideFileName = "worker-tiers.json";
    private const string RolesOverrideFileName = "worker-roles.json";

    // Plain JSON only — no comments, no trailing commas. #1759: verified by grep, the surviving
    // Python reader is `tools/audit-completeness/completeness.py`'s step 9 (WorkerTiers.json only,
    // via stdlib `json.load`, which tolerates neither) — dispatch.py read both files this way too
    // until #1759 retired it, and no other Python tool (fleet-glass's pusher included) reads either
    // file. Kept for WorkerRoles.json as well, both files sharing this one JsonOptions, rather than
    // splitting the constraint per file for a reader step 9 does not have today.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Every role in the catalog, resolved against the current tiers, in file order.</summary>
    public static IReadOnlyList<WorkerRole> All => Load();

    /// <summary>The role with <paramref name="id"/>, or throws if the catalog has no such role.</summary>
    public static WorkerRole For(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return All.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException(
                $"No worker role '{id}' in the catalog. Known roles: {string.Join(", ", All.Select(r => r.Id))}.");
    }

    /// <summary>
    /// The tier named <paramref name="tierName"/>, in the shape the conductor queue's tier table
    /// consumes, or null when the tier map defines no such tier. Case-sensitive, matching how a role's
    /// own <c>tier</c> key resolves in <see cref="Load"/> — a tier name is a catalog identifier, not
    /// something an operator types per item.
    /// </summary>
    /// <remarks>
    /// #1863: the bridge <c>Baton.Queue.QueueTierTable</c>'s <c>tooling</c> row crosses. That table
    /// lives in <c>Baton</c>, which does not reference this project, so it takes this method as a
    /// delegate rather than calling it — which is what lets the queue's tooling pin BE the
    /// <c>standard</c> tier instead of a second copy of it. Null is a fail-closed answer there, not a
    /// fallback.
    /// <para>
    /// A tier FILE that is absent or unreadable throws out of <see cref="ReadJson"/> instead of
    /// answering null, exactly as it does for a role lookup, because a queue that cannot read the
    /// register must not guess a tier is merely missing. Where that throw lands is worth being precise
    /// about, since "fail closed" invites the wrong reading: from <c>QueueSchedulerService</c> it
    /// escapes the whole evaluation, so <c>TickOnceAsync</c> ledgers the tick <c>failed</c> with the
    /// exception message and <c>ExecuteAsync</c> logs it and ticks again — nothing launches and the
    /// ledger says why, but the ITEM is not marked failed and is retried each tick. Only a tier the
    /// file genuinely does not define (null, here) reaches the per-item fail-closed path that marks it.
    /// </para>
    /// </remarks>
    public static Queue.QueueTierSettings? QueueTierFor(string tierName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tierName);
        return LoadTiers().TryGetValue(tierName, out var tier)
            ? new Queue.QueueTierSettings
            {
                Tier = tierName,
                Adapter = tier.Adapter,
                Model = tier.Model,
                Effort = tier.Effort,
            }
            : null;
    }

    private static Dictionary<string, WorkerTier> LoadTiers() =>
        ReadJson<Dictionary<string, WorkerTier>>(
            ResolvePath(
                BatonEnvironmentSnapshot.Current.WorkerTiersPathOverride,
                TiersOverrideFileName,
                TiersDefaultFileName),
            "tier map");

    private static IReadOnlyList<WorkerRole> Load()
    {
        var snapshot = BatonEnvironmentSnapshot.Current;
        var tiers = LoadTiers();
        var rawRoles = ReadJson<List<RawRole>>(
            ResolvePath(snapshot.WorkerRolesPathOverride, RolesOverrideFileName, RolesDefaultFileName), "role list");

        if (rawRoles.Count == 0)
        {
            throw new InvalidOperationException("The worker-role catalog is empty.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var roles = new List<WorkerRole>(rawRoles.Count);
        foreach (var raw in rawRoles)
        {
            if (!seen.Add(raw.Id))
            {
                throw new InvalidOperationException($"Duplicate worker role id '{raw.Id}' in the catalog.");
            }

            if (!tiers.TryGetValue(raw.Tier, out var tier))
            {
                throw new InvalidOperationException(
                    $"Worker role '{raw.Id}' names tier '{raw.Tier}', which is not defined in the tier map. " +
                    $"Known tiers: {string.Join(", ", tiers.Keys)}.");
            }

            // [JsonRequired] guarantees the `outputs` key is present, not that it is non-null or
            // non-empty. Both would ship a role that declares nothing — silently defeating the floor
            // every role's primary output exists to be (a worker that writes nothing fails loudly).
            // Named like every other guard here, unlike the bare ArgumentNullException a null Outputs
            // would otherwise throw out of the Select below.
            if (raw.Outputs is null || raw.Outputs.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Worker role '{raw.Id}' declares no outputs. Every role declares at least one output " +
                    "file the worker writes to BATON_OUTPUT_DIR — the floor that catches a silent no-op.");
            }

            roles.Add(new WorkerRole(
                Id: raw.Id,
                Tier: raw.Tier,
                Adapter: tier.Adapter,
                Model: tier.Model,
                Effort: tier.Effort,
                Grant: new PermissionGrant(
                    ReadFiles: raw.ReadFiles,
                    WriteFiles: raw.WriteFiles,
                    RunShellCommands: raw.RunShellCommands,
                    ShellCommandPatterns: raw.ShellCommandPatterns,
                    NetworkAccess: raw.NetworkAccess,
                    DeniedShellCommandPatterns: raw.DeniedShellCommandPatterns,
                    ShellCommandsAreReadOnly: raw.ShellCommandsAreReadOnly,
                    DeniedShellOptionTokens: raw.DeniedShellOptionTokens),
                Timeout: TimeSpan.FromMinutes(raw.TimeoutMinutes),
                ProducesVerdict: raw.VerdictSchema,
                Purpose: raw.Purpose,
                Outputs: raw.Outputs.Select(o => ResolveOutput(raw.Id, o)).ToList(),
                VerifyPixiTask: raw.VerifyPixiTask,
                TokenBudget: ParseTokenBudget(raw.Id, raw.TokenBudget),
                MaxToolSteps: raw.MaxToolSteps,
                BilledRateLimit: raw.BilledRateLimit,
                DeliversBranch: raw.DeliversBranch,
                AllowsSubagents: raw.AllowsSubagents,
                VerifiesWorkspace: raw.VerifiesWorkspace));
        }

        return roles;
    }

    private static WorkerRoleOutput ResolveOutput(string roleId, RawOutput raw)
    {
        // Reject a '.'-prefixed name at load, mirroring ProducedOutput's own constructor (see
        // ReservedOutputNames for why the namespace is reserved). Without this the invalid name would
        // sail through the catalog and only throw when the front door converts it to a ProducedOutput
        // at dispatch — the exact "fail at dispatch, not at load" this catalog exists to prevent.
        if (ReservedOutputNames.IsReserved(raw.Name))
        {
            throw new InvalidOperationException(
                $"Worker role '{roleId}' output name '{raw.Name}' is invalid: {ReservedOutputNames.RejectionClause}.");
        }

        if (ReservedOutputNames.IsPathTraversal(raw.Name))
        {
            throw new InvalidOperationException(
                $"Worker role '{roleId}' output name '{raw.Name}' is invalid: {ReservedOutputNames.PathTraversalRejectionClause}.");
        }

        // Mapped explicitly rather than deserialized straight into OutputSchema: the catalog's wire
        // form is snake_case (matching every other Python/JSON reader of this file), and an unknown
        // value must fail loudly at load — a silently-defaulted OutputSchema.None would drop a verdict's schema check and
        // pass a file that is not a verdict, the exact false capability the RawRole discipline forbids.
        var schema = raw.Schema switch
        {
            "none" => OutputSchema.None,
            "review_verdict" => OutputSchema.ReviewVerdict,
            "diff" => OutputSchema.Diff,
            "non_empty_text" => OutputSchema.NonEmptyText,
            _ => throw new InvalidOperationException(
                $"Worker role '{roleId}' output '{raw.Name}' declares unknown schema '{raw.Schema}'. " +
                "Known schemas: none, review_verdict, diff, non_empty_text."),
        };

        return new WorkerRoleOutput(raw.Name, schema, raw.Instruction);
    }

    // #1745: parsed separately from RawRole's plain deserialization, the same way ResolveOutput below
    // hand-validates a role's outputs, because the shape check ("int or adapter map?") AND the
    // per-key validation ("is this adapter name real, is this value a whole number?") both need to
    // name the OFFENDING role -- a JsonConverter attached to the type has no such context, only the
    // bare property value.
    private static TokenBudgetSpec? ParseTokenBudget(string roleId, JsonElement? raw)
    {
        if (raw is not { } element || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            if (!element.TryGetInt64(out var fixedValue))
            {
                throw new InvalidOperationException(
                    $"Worker role '{roleId}' declares 'token_budget' {element.GetRawText()}, which is not a whole number of tokens.");
            }

            return new TokenBudgetSpec.Fixed(fixedValue);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var byAdapter = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!KnownTokenBudgetAdapters.Contains(property.Name))
                {
                    throw new InvalidOperationException(
                        $"Worker role '{roleId}' 'token_budget' names unknown adapter '{property.Name}'. " +
                        $"Known adapters: {string.Join(", ", KnownTokenBudgetAdapters)}.");
                }

                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out var perAdapterValue))
                {
                    throw new InvalidOperationException(
                        $"Worker role '{roleId}' 'token_budget.{property.Name}' is not a whole number of tokens.");
                }

                byAdapter[property.Name] = perAdapterValue;
            }

            return new TokenBudgetSpec.PerAdapter(byAdapter);
        }

        throw new InvalidOperationException(
            $"Worker role '{roleId}' declares 'token_budget' as {element.ValueKind}, which is neither a " +
            "whole number nor an object mapping adapter name to a whole number of tokens.");
    }

    // record-once-ok: #1524 src/Baton/Status/BatonEnvironmentSnapshot.cs
    // #1524: folded into BatonEnvironmentSnapshot -- envOverride is BatonEnvironmentSnapshot.Current's
    // WorkerTiersPathOverride/WorkerRolesPathOverride, resolved once by the caller rather than a
    // per-access Environment.GetEnvironmentVariable here.
    private static string ResolvePath(string? envOverride, string overrideFileName, string defaultFileName)
    {
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            return envOverride;
        }

        var configOverride = Path.Combine(BatonPaths.Root, overrideFileName);
        return File.Exists(configOverride)
            ? configOverride
            : Path.Combine(AppContext.BaseDirectory, defaultFileName);
    }

    private static T ReadJson<T>(string path, string what)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The worker-role catalog's {what} was not found at '{path}'. The default ships next to " +
                "the engine; an override lives under BATON_HOME or the BATON_WORKER_*_PATH env var.", path);
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"The worker-role catalog's {what} at '{path}' parsed to null.");
    }

    // Every field is [JsonRequired]: a missing member would otherwise deserialize to its default
    // (false / 0 / null) and silently ship a role nobody authored — a false capability, an
    // instant-timeout, a dropped verdict schema. The catalog's contract is to fail loudly at load, so
    // a typo'd or omitted key throws here rather than surfacing at dispatch time (#888 finding).
    private sealed record RawRole(
        [property: JsonRequired] string Id,
        [property: JsonRequired] string Tier,
        [property: JsonRequired] bool ReadFiles,
        [property: JsonRequired] bool WriteFiles,
        [property: JsonRequired] bool RunShellCommands,
        [property: JsonRequired] bool NetworkAccess,
        [property: JsonRequired] int TimeoutMinutes,
        [property: JsonRequired] bool VerdictSchema,
        [property: JsonRequired] string Purpose,
        [property: JsonRequired] IReadOnlyList<RawOutput> Outputs,
        // Optional, unlike every field above: most roles never scope RunShellCommands and omitting
        // these three is exactly "no patterns / not asserted read-only" — the same default
        // PermissionGrant's own constructor already carries (#1456, spec/baton.md §9). Making them
        // [JsonRequired] would force every existing role in the catalog to grow dead keys for a
        // capability only `review` uses.
        IReadOnlyList<string>? ShellCommandPatterns = null,
        IReadOnlyList<string>? DeniedShellCommandPatterns = null,
        bool ShellCommandsAreReadOnly = false,
        // #1683 F2: optional for the same reason as the three above -- only `review` scopes a shell at
        // all, and omitting this key is exactly PermissionGrant's own "no option tokens denied".
        IReadOnlyList<string>? DeniedShellOptionTokens = null,
        // #1623: optional like the three above, for the same reason -- most roles declare neither and
        // omitting them is exactly "no engine-run verify, no token budget", the WorkerRole defaults.
        string? VerifyPixiTask = null,
        // #1745: raw JsonElement, not long? -- the wire shape is now `int | {adapter: int}`, and
        // ParseTokenBudget above is what turns this into a TokenBudgetSpec, naming the offending role
        // if the shape or a map entry is invalid.
        JsonElement? TokenBudget = null,
        // #1682: optional for the same reason -- most roles declare no tool-step cap.
        int? MaxToolSteps = null,
        // #1691: optional for the same reason, and here NO role declares one -- the key is readable
        // from the catalog so an operator can pin a rate limit durably in their own worker-roles.json
        // override, but spec/baton.md §3's calibration found no defensible shipped default.
        long? BilledRateLimit = null,
        // #1788: optional like the flags above -- omitting it is exactly "this role's brief does not
        // end in a push", the WorkerRole default.
        bool DeliversBranch = false,
        // #1802: optional like the flags above -- omitting it is exactly "withhold the vendor's
        // subagent/fan-out tool", the WorkerRole default. Only advise's entry sets this true.
        bool AllowsSubagents = false,
        // #2029: the ONE optional flag here whose omission default is TRUE rather than false, and
        // deliberately so -- a role that forgets the key is graded by the workspace's own gates
        // (loud) rather than silently settling unverified. Every shipped role states it explicitly
        // anyway; see WorkerRole.VerifiesWorkspace for the two sets and why.
        bool VerifiesWorkspace = true);

    private sealed record RawOutput(
        [property: JsonRequired] string Name,
        [property: JsonRequired] string Schema,
        [property: JsonRequired] string Instruction);
}
