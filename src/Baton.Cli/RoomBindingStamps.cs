using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// The per-worker stamps a room's own <c>bindings.json</c> carries for the cost ledger — the
/// <c>baton dispatch --label</c> each worker was dispatched under (#1499) and the runway-override
/// audit record <c>DispatchCommand</c> wrote (#1848) — read in ONE parse of that file.
/// </summary>
/// <remarks>
/// <para>
/// <b>One reader, two projections, one parse</b> (#1931 review LOW). These were two near-identical
/// types until the settle site wanted both, at which point every settled room parsed the same
/// <c>bindings.json</c> twice and any change to the read posture — a new exception type, a schema
/// migration — had to be made in two places or the two silently diverged.
/// </para>
/// <para>
/// Lives here rather than in <c>Baton.Accounting</c> for the reason <see cref="WorkspaceDeliveryProbe"/>
/// does: the binding record is a <c>Baton.Vendors</c> type the engine layer holds no reference to, so
/// the ledger takes the resolved stamps as arguments instead of learning a second copy of the bindings
/// schema.
/// </para>
/// </remarks>
/// <param name="LabelByWorker">
/// Worker name to the <c>--label</c> recorded for it. <c>CostLedgerEntry.Label</c>'s own doc states
/// what an absent entry means and does not mean.
/// </param>
/// <param name="RunwayOverrideReasonByWorker">
/// Worker name to the override reason recorded for it — only for overrides that actually bypassed a
/// Hold (<see cref="RunwayOverride.Used"/>), because a flag that bypassed nothing is not an override
/// of this row's spend. <c>CostLedgerEntry.RunwayOverrideReason</c>'s own doc states the same for its
/// absence.
/// </param>
public sealed record RoomBindingStamps(
    IReadOnlyDictionary<string, string> LabelByWorker,
    IReadOnlyDictionary<string, string> RunwayOverrideReasonByWorker)
{
    /// <summary>What a room with no readable bindings yields: both projections empty, never null.</summary>
    public static RoomBindingStamps None { get; } = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// Both stamps for one room. <see cref="None"/> when the room has no bindings file, it cannot be
    /// read, or no binding carries either: <b>fail open</b>, the same posture every other accounting
    /// read at the settle site takes — a missing stamp must never be the reason a settled run reports
    /// as failed.
    /// </summary>
    public static async Task<RoomBindingStamps> ReadForRoomAsync(
        string roomDirectoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var bindingsFilePath = Path.Combine(roomDirectoryPath, "bindings.json");
        if (!File.Exists(bindingsFilePath))
        {
            return None;
        }

        IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings;
        try
        {
            bindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsFilePath, cancellationToken)
                .ConfigureAwait(false);
        }
        // OperationCanceledException among them, for the reason WorkspaceDeliveryProbe's own read
        // states: a Ctrl-C during this read must cost the stamps and nothing else. The message names
        // BOTH of them, because one parse failure cannot be attributed to one projection.
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or BatonFlowException or OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"Could not read '{bindingsFilePath}' for label and runway-override attribution: {ex.Message} "
                + "The cost ledger rows for this room carry neither a label nor a runwayOverrideReason.");
            return None;
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (worker, entry) in bindings)
        {
            if (entry.Label is { Length: > 0 } label)
            {
                labels[worker] = label;
            }

            if (entry.RunwayOverride is { Used: true, Reason: { Length: > 0 } reason })
            {
                reasons[worker] = reason;
            }
        }

        return new RoomBindingStamps(labels, reasons);
    }
}
