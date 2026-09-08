namespace Baton.Core;

/// <summary>
/// Payload for <see cref="BatonTask.EventRaised"/>. One instance is created per lifecycle/observation
/// event delivered during a run; check <see cref="Kind"/> to determine which other members are
/// meaningful.
/// </summary>
public sealed class BatonEventArgs : EventArgs
{
    /// <summary>Which kind of event this is.</summary>
    public required BatonTaskEventKind Kind { get; init; }

    /// <summary>Process ID of the child. Meaningful when <see cref="Kind"/> is <see cref="BatonTaskEventKind.Started"/>.</summary>
    public uint Pid { get; init; }

    /// <summary>
    /// #2073: the child's OS start time (UTC), read immediately after spawn; the pid-recycling
    /// discriminator that lets a later process confirm <see cref="Pid"/> still names this child.
    /// Meaningful when <see cref="Kind"/> is <see cref="BatonTaskEventKind.Started"/>; <c>null</c> when
    /// the read failed (the child had already exited).
    /// </summary>
    public DateTime? ProcessStartTimeUtc { get; init; }

    /// <summary>Exit code of the child, or -1 if it was killed. Meaningful when <see cref="Kind"/> is <see cref="BatonTaskEventKind.Exited"/>.</summary>
    public int ExitCode { get; init; }

    /// <summary>Reason the child exited. Meaningful when <see cref="Kind"/> is <see cref="BatonTaskEventKind.Exited"/>.</summary>
    public BatonExitReason ExitReason { get; init; }

    /// <summary>
    /// Monotonically increasing sequence number, scoped per stream (stdout and stderr each have
    /// their own sequence). Meaningful when <see cref="Kind"/> is <see cref="BatonTaskEventKind.StdoutChunk"/>
    /// or <see cref="BatonTaskEventKind.StderrChunk"/>; use it to detect out-of-order delivery within a stream.
    /// </summary>
    public ulong Seq { get; init; }

    /// <summary>
    /// A defensive copy of the chunk bytes, safe to retain past the event handler's return.
    /// Meaningful when <see cref="Kind"/> is <see cref="BatonTaskEventKind.StdoutChunk"/> or
    /// <see cref="BatonTaskEventKind.StderrChunk"/>; <see langword="null"/> otherwise.
    /// </summary>
    public byte[]? Data { get; init; }
}
