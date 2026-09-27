using Baton.Domain;

namespace Baton.Status;

/// <summary>
/// The effective adapter/model binding per execution id, after every <see cref="FlowEvent.StepRebound"/>
/// override on top of the frozen <see cref="FlowEvent.ExecutionRequestAccepted"/> request (#1583). Two
/// independent readers needed this precedence — <see cref="ExecutionUsageProjector"/> (to pick which
/// vendor's parser to trust) and <see cref="QuotaLedgerStore"/> (to attribute a ledger line) — and had
/// each grown their own copy of the same loop before this extraction (PR #1781 review finding 1): one
/// tested via <c>ExecutionUsageProjectorTests</c>' rebind arms, the other not, which is exactly the
/// "silent mis-attribution" spec/baton.md §7 names as this design's own stated risk #1. One primitive
/// now; both callers ask it, neither re-derives it.
/// </summary>
public static class ExecutionBindingResolver
{
    /// <param name="Adapter">Null when neither the accepted request nor any rebind ever recorded one.</param>
    /// <param name="Model">Null when neither the accepted request nor any rebind ever recorded one.</param>
    /// <param name="Limits">Null for legacy/supplementary evidence that was never recorded.</param>
    public readonly record struct Binding(string? Adapter, string? Model, ExecutionLimitEvidence? Limits = null);

    /// <summary>
    /// Adapter and Model are tracked independently, matching <see cref="FlowEvent.ExecutionRequestAccepted"/>'s
    /// own shape: an accepted request naming an adapter but no model (or vice versa) only ever sets the
    /// field it actually carries. A <see cref="FlowEvent.StepRebound"/> with no <c>NewAdapter</c>/<c>NewModel</c>
    /// clears that field for the execution rather than leaving the pre-rebind value in place — the same
    /// "resubmit-path case where the guarantee doesn't hold" <see cref="ExecutionRequest.Adapter"/>'s own
    /// doc comment describes.
    /// </summary>
    public static IReadOnlyDictionary<string, Binding> Resolve(IReadOnlyList<LogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var adapterByExecutionId = new Dictionary<string, string>(StringComparer.Ordinal);
        var modelByExecutionId = new Dictionary<string, string>(StringComparer.Ordinal);
        var limitsByExecutionId = new Dictionary<string, ExecutionLimitEvidence?>(StringComparer.Ordinal);
        var executionIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry is LogEntry.FlowLogEntry { Event: FlowEvent.ExecutionRequestAccepted accepted })
            {
                var executionId = accepted.Request.ExecutionId.Value;
                executionIds.Add(executionId);
                if (accepted.Request.Adapter is { Length: > 0 } adapter)
                {
                    adapterByExecutionId[executionId] = adapter;
                }

                if (accepted.Request.Model is { Length: > 0 } model)
                {
                    modelByExecutionId[executionId] = model;
                }

                limitsByExecutionId[executionId] = accepted.Request.Limits;
            }
            else if (entry is LogEntry.FlowLogEntry { Event: FlowEvent.ArtifactCheckpointAttempted { Request: { } checkpointRequest } })
            {
                var executionId = checkpointRequest.ExecutionId.Value;
                executionIds.Add(executionId);
                if (checkpointRequest.Adapter is { Length: > 0 } adapter)
                {
                    adapterByExecutionId[executionId] = adapter;
                }

                if (checkpointRequest.Model is { Length: > 0 } model)
                {
                    modelByExecutionId[executionId] = model;
                }

                limitsByExecutionId[executionId] = checkpointRequest.Limits;
            }
            else if (entry is LogEntry.FlowLogEntry { Event: FlowEvent.StepRebound rebound })
            {
                var executionId = rebound.ForExecutionId.Value;
                executionIds.Add(executionId);
                if (rebound.NewAdapter is { Length: > 0 } newAdapter)
                {
                    adapterByExecutionId[executionId] = newAdapter;
                }
                else
                {
                    adapterByExecutionId.Remove(executionId);
                }

                if (rebound.NewModel is { Length: > 0 } newModel)
                {
                    modelByExecutionId[executionId] = newModel;
                }
                else
                {
                    modelByExecutionId.Remove(executionId);
                }

                if (rebound.NewLimits is { } newLimits)
                {
                    limitsByExecutionId[executionId] = newLimits;
                }
            }
        }

        var result = new Dictionary<string, Binding>(StringComparer.Ordinal);
        foreach (var executionId in executionIds)
        {
            adapterByExecutionId.TryGetValue(executionId, out var adapter);
            modelByExecutionId.TryGetValue(executionId, out var model);
            limitsByExecutionId.TryGetValue(executionId, out var limits);
            result[executionId] = new Binding(adapter, model, limits);
        }

        return result;
    }
}
