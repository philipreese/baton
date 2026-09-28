using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// Why <see cref="FlowEvent.ExecutionArrested"/> fired (#1682, extended #1691) — the three producers
/// <c>Mutation.TokenBudgetMonitor</c> arms, independently of each other, over the same stdout stream.
/// <see cref="StateProjector"/>'s <c>DescribeArrest</c> is the one place this is switched on; keep that
/// switch total when adding a member here. New (no pre-#1682 journal line carries this field at all),
/// so it carries <see cref="JsonStringEnumConverter"/> directly — same pattern as
/// <see cref="GrantAuditMode"/> — rather than needing an ordinal-stability pin: there is no legacy
/// ordinal to stay compatible with.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArrestReason
{
    /// <summary>The running Σ of billed tokens (<see cref="WorkerUsage.BilledTokens"/>) crossed the role's <c>TokenBudget</c>.</summary>
    TokenBudget,

    /// <summary>The running count of tool-step lines crossed the role's <c>MaxToolSteps</c>, independent of whether usage parsed at all.</summary>
    ToolStepCap,

    /// <summary>The count of repeated tool+arguments calls exceeded the explicitly selected per-execution cap.</summary>
    RepeatedToolCallCap,

    /// <summary>
    /// #1691: Σ billed tokens inside the trailing <c>TokenBudgetMonitor.BilledRateWindow</c> crossed
    /// the execution's <c>BilledRateLimit</c>. No role sets one (spec/baton.md §3 — no measured value
    /// separates a runaway from normal traffic), so in practice this reason only ever appears from an
    /// operator's own <c>--billed-rate-limit</c>.
    /// </summary>
    BilledRate,
}
