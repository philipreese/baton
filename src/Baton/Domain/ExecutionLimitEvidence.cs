using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// The enforcement inputs recorded for one execution. Timeout is recorded whenever evidence
/// exists. Null monitor brakes mean unlimited only when MonitorInputsKnown is true; otherwise
/// they are unknown because no parser could supply the monitor. A null evidence value on an
/// older request means the journal predates limit evidence, or that execution path did not record it.
/// </summary>
public sealed record ExecutionLimitEvidence(
    TimeSpan? Timeout,
    long? TokenBudget,
    int? MaxToolSteps,
    long? BilledRateLimit,
    string? ChosenKey = null,
    string? TimeoutSource = null,
    string? TokenBudgetSource = null,
    string? MaxToolStepsSource = null,
    bool MonitorInputsKnown = false)
{
    /// <summary>The selected profile key when profile selection was available.</summary>
    [JsonIgnore]
    public string? SelectionProvenance => ChosenKey;

    /// <summary>Compares applied inputs and their availability; provenance changes alone do not imply a new spawn.</summary>
    public bool HasDifferentEnforcementInputs(ExecutionLimitEvidence other) =>
        Timeout != other.Timeout
        || TokenBudget != other.TokenBudget
        || MaxToolSteps != other.MaxToolSteps
        || BilledRateLimit != other.BilledRateLimit
        || MonitorInputsKnown != other.MonitorInputsKnown;
}
