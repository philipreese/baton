using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// The enforcement inputs recorded for one execution. Timeout is recorded whenever evidence
/// exists. Null monitor brakes mean unlimited only when MonitorInputsKnown is true; otherwise
/// those axes are unknown because their applied inputs were not established. A legacy recovery rebind
/// may record only an explicitly applied repeated-call cap while leaving this flag false and older
/// monitor axes unknown. A null evidence value on an older request means the journal predates limit
/// evidence, or that execution path did not record it.
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
    bool MonitorInputsKnown = false,
    [property: JsonPropertyName("maxRepeatedToolSteps")] int? MaxRepeatedToolSteps = null,
    [property: JsonPropertyName("maxRepeatedToolStepsSource")] string? MaxRepeatedToolStepsSource = null)
{
    /// <summary>The selected profile key when profile selection was available.</summary>
    [JsonIgnore]
    public string? SelectionProvenance => ChosenKey;

    /// <summary>
    /// Compares applied inputs and their availability. Repeat-cap source is included so a same-value
    /// profile-to-override rebind can durably preserve the operator-visible source; other provenance
    /// changes alone do not imply a new spawn.
    /// </summary>
    public bool HasDifferentEnforcementInputs(ExecutionLimitEvidence other) =>
        Timeout != other.Timeout
        || TokenBudget != other.TokenBudget
        || MaxToolSteps != other.MaxToolSteps
        || MaxRepeatedToolSteps != other.MaxRepeatedToolSteps
        || MaxRepeatedToolStepsSource != other.MaxRepeatedToolStepsSource
        || BilledRateLimit != other.BilledRateLimit
        || MonitorInputsKnown != other.MonitorInputsKnown;
}
