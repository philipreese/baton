using System.Text.Json.Serialization;

namespace Baton.Queue;

/// <summary>
/// An explicit selection for one lifecycle stage. It is deliberately a value object rather than a
/// second tier table: <see cref="QueueTierTable"/> still supplies every axis left unset here.
/// </summary>
public sealed record QueueStageSelection
{
    public required WorkStage Stage { get; init; }

    public string? Adapter { get; init; }

    public string? Model { get; init; }

    public string? Effort { get; init; }

    /// <summary>Why this stage departs from its role/scope tier, when it does.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Where the effective launch axes came from. This is recorded with the resolved choice so a
/// ledger reader can tell an unset stage from an intentional pin without reconstructing old JSON.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<QueueSelectionSource>))]
public enum QueueSelectionSource
{
    /// <summary>No lifecycle selection supplied; the role/scope tier chose the axes.</summary>
    StageDefault,

    /// <summary>An explicit selection for this stage supplied one or more axes.</summary>
    StageOverride,

    /// <summary>An operator deliberately applied the item's axes to every lifecycle stage.</summary>
    LifecyclePin,

    /// <summary>
    /// A lifecycle item persisted before stage selections existed. Its old item axes retain their
    /// former whole-item meaning rather than silently changing a queued or running item.
    /// </summary>
    PersistedLifecycleCompatibility,
}
