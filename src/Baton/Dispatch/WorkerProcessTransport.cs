using System.Diagnostics;
using Baton.Domain;

namespace Baton.Dispatch;

/// <summary>Adapter-owned stdin and stream framing within Core's existing process containment.</summary>
public interface IWorkerProcessTransport : IDisposable
{
    void Start(Process child);
    byte[] Capture(byte[] nativeBytes);
    byte[] Finish();
    FinalExpectedTurnCompletion? Completion { get; }
}

/// <summary>Host-derived completion, retained with the Core exit rather than inferred from artifacts.</summary>
public sealed record FinalExpectedTurnCompletion(
    string ExecutionId, string Transport, string Incarnation, int ChildPid, DateTime ChildStartUtc,
    string ConversationId, int ExpectedTurn, int CompletedTurn, bool Successful, bool EvidenceValid, int Version = 1,
    bool CaptureIntegrityValid = false)
{
    public bool HasValidEvidence(string transport) => Version == 1 && EvidenceValid && Transport == transport
        && !string.IsNullOrWhiteSpace(ExecutionId) && Guid.TryParseExact(Incarnation, "N", out _)
        && ChildPid > 0 && ChildStartUtc.Kind == DateTimeKind.Utc && ChildStartUtc != default
        && !string.IsNullOrWhiteSpace(ConversationId) && ExpectedTurn is 1 or 2 && CompletedTurn == ExpectedTurn;

    public bool IsSuccessful(string transport) => Successful && HasValidEvidence(transport);
}

public sealed record WorkerProcessTransportContext(ExecutionRequest Request, string Room, string Prompt);
