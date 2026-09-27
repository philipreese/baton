using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// The enforcement inputs applied to one ordinary execution. A non-null value means the inputs are
/// known, including null brakes that were explicitly absent; a null value on an older request means
/// the journal predates limit evidence (or the execution is supplementary).
/// </summary>
public sealed record ExecutionLimitEvidence(
    TimeSpan? Timeout,
    long? TokenBudget,
    int? MaxToolSteps,
    long? BilledRateLimit,
    string? ChosenKey = null,
    string? TimeoutSource = null,
    string? TokenBudgetSource = null,
    string? MaxToolStepsSource = null)
{
    /// <summary>Whether the enforcement inputs, rather than legacy absence, are recorded.</summary>
    [JsonIgnore]
    public bool IsKnown => true;

    /// <summary>The selected profile key when profile selection was available.</summary>
    [JsonIgnore]
    public string? SelectionProvenance => ChosenKey;

    /// <summary>Compares enforcement inputs only; provenance changes do not imply a new spawn.</summary>
    public bool HasDifferentEnforcementInputs(ExecutionLimitEvidence other) =>
        Timeout != other.Timeout
        || TokenBudget != other.TokenBudget
        || MaxToolSteps != other.MaxToolSteps
        || BilledRateLimit != other.BilledRateLimit;
}
