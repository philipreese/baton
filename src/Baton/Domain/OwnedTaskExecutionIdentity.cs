using System.Security.Cryptography;
using System.Text;

namespace Baton.Domain;

/// <summary>
/// Producer-known identity carried from an accepted queue attempt to each accepted execution.
/// Null or malformed values are evidence of unknown ownership, never permission to infer an issue.
/// </summary>
public sealed record OwnedTaskExecutionIdentity(
    string? TaskId,
    string? Repository,
    int? Issue,
    string? AttemptId,
    string? RoomDirectory,
    string? ExecutionId = null)
{
    public static string TaskIdFor(string repository, int issue)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{repository}\0{issue}"));
        return "task-" + Convert.ToHexString(bytes).ToLowerInvariant()[..58];
    }

    public bool HasExpectedTaskId()
    {
        if (Repository is not { Length: > 0 } repository || Issue is not > 0 || TaskId is not { Length: > 0 } taskId)
        {
            return false;
        }

        return string.Equals(taskId, TaskIdFor(repository, Issue.Value), StringComparison.Ordinal);
    }

    public bool IsAdmissionShape()
        => HasExpectedTaskId()
            && AttemptId is { Length: > 0 }
            && RoomDirectory is { Length: > 0 }
            && ExecutionId is null;
}
