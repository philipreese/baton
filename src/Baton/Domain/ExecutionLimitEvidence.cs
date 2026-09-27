using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// The enforcement inputs applied to one ordinary execution. A non-null value means the inputs are
/// known. Null monitor brakes mean unlimited only when MonitorInputsKnown is true; otherwise
/// they are unknown because no parser could supply the monitor. A null evidence value on an
/// older request means the journal predates limit evidence (or the execution is supplementary).
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
    /// <summary>Whether the enforcement inputs, rather than legacy absence, are recorded.</summary>
    [JsonIgnore]
    public bool IsKnown => true;

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
