using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// Reads back the <c>baton dispatch --label</c> (#1499) each of a room's workers was dispatched under,
/// off that room's own <c>bindings.json</c>, so the settle site and #1901 C2's backfill can stamp
/// <see cref="Baton.Accounting.CostLedgerEntry.Label"/> on its rows. Lives here rather than in
/// <c>Baton.Accounting</c> for the same reason <see cref="RunwayOverrideReasons"/> does — the binding
/// record is a <c>Baton.Vendors</c> type the engine layer holds no reference to, so the ledger takes
/// the resolved labels as an argument instead of learning a second copy of the bindings schema.
/// </summary>
public static class DispatchLabels
{
    /// <summary>
    /// Worker name to the label recorded for it. Empty when the room has no bindings file, it cannot be
    /// read, or no binding carries a label: <b>fail open</b>, the same posture every other accounting
    /// read at the settle site takes — a missing label must never be the reason a settled run reports
    /// as failed, and it is exactly the absence
    /// <see cref="Baton.Accounting.CostLedgerEntry.Label"/>'s own doc describes.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ReadForRoomAsync(
        string roomDirectoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var bindingsFilePath = Path.Combine(roomDirectoryPath, "bindings.json");
        if (!File.Exists(bindingsFilePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings;
        try
        {
            bindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsFilePath, cancellationToken)
                .ConfigureAwait(false);
        }
        // OperationCanceledException among them, for the reason WorkspaceDeliveryProbe's own read
        // states: a Ctrl-C during this read must cost the stamp and nothing else.
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or BatonFlowException or OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"Could not read '{bindingsFilePath}' for label attribution: {ex.Message} "
                + "The cost ledger rows for this room carry no label.");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (worker, entry) in bindings)
        {
            if (entry.Label is { Length: > 0 } label)
            {
                labels[worker] = label;
            }
        }

        return labels;
    }
}
