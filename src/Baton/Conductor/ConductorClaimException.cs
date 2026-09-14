namespace Baton.Conductor;

/// <summary>
/// Domain-level error raised when a conductor claim, release, or takeover transition fails, or
/// when the durable claim record is corrupt or unreadable (#2296).
/// </summary>
public sealed class ConductorClaimException : BatonFlowException
{
    public ConductorClaimException(string message)
        : base(message)
    {
    }

    public ConductorClaimException(string message, string tryInvocation)
        : base(message)
    {
        TryInvocation = tryInvocation;
    }

    public ConductorClaimException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
