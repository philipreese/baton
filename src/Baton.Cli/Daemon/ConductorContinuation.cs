using System.Globalization;
using Baton;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;

namespace Baton.Cli.Daemon;

/// <summary>The narrow durable identity and evidence rules for queue-owned continuations.</summary>
internal static class ConductorContinuation
{
    internal const string Action = "continue";
    internal const string Owner = "queue-lifecycle";
    internal const string Adapter = "baton-queue";
    internal const string AdapterCapability = "continue";

    internal static string IdempotencyKey(string tag, FleetAttemptId sourceAttemptId, int nextRound) =>
        $"{Action}:{tag}:{sourceAttemptId.Value}:{nextRound.ToString(CultureInfo.InvariantCulture)}";

    internal static ConductorObligationRequest? TryRequest(
        QueueItem item,
        string room,
        WorkflowStatusView sentinel,
        string? pullRequestHead,
        WorkItemTransition transition,
        DateTimeOffset createdAt)
    {
        if (item.AttemptId is not { } sourceAttemptId || string.IsNullOrWhiteSpace(item.Repository))
        {
            return null;
        }

        var execution = UniqueExecution(sentinel);
        if (execution is null)
        {
            return null;
        }

        return new ConductorObligationRequest(
            IdempotencyKey(item.Tag, sourceAttemptId, transition.Round),
            item.Repository,
            BatonPaths.RecordKey(room),
            execution,
            pullRequestHead,
            Action,
            Owner,
            createdAt,
            Adapter,
            AdapterCapability,
            AdapterSupported: true);
    }

    internal static string? UniqueExecution(WorkflowStatusView sentinel)
    {
        var executions = sentinel.Steps
            .Select(step => step.Execution)
            .Where(execution => execution is { Length: > 0 })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return executions.Length == 1 ? executions[0] : null;
    }

    internal static bool TryParseKey(
        string key,
        out string tag,
        out FleetAttemptId sourceAttemptId,
        out int nextRound)
    {
        tag = string.Empty;
        sourceAttemptId = default;
        nextRound = 0;
        var parts = key.Split(':');
        if (parts.Length != 4
            || !string.Equals(parts[0], Action, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(parts[1])
            || string.IsNullOrWhiteSpace(parts[2])
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out nextRound)
            || nextRound <= 0)
        {
            return false;
        }

        tag = parts[1];
        sourceAttemptId = new FleetAttemptId(parts[2]);
        return true;
    }

    internal static string ActionProof(QueueItem item) =>
        $"queue-continuation:{item.Tag}:{item.ParentAttemptId!.Value.Value}:{item.Round.ToString(CultureInfo.InvariantCulture)}";
}
