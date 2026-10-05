using Baton.Domain;

namespace Baton.Status;

public sealed record ObservedStreamUsage(WorkerUsage? Observed, WorkerUsage? ReportedFinalTurn, string? IntegrityUnavailable,
    string? ExecutionId = null);
