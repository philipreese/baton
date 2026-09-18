namespace Baton.Memory;

/// <summary>A durable dispatch key already committed a different normalized memory payload.</summary>
public sealed class MemoryAddDispatchConflictException(string dispatchId) : Exception(
    $"Memory-add dispatch '{dispatchId}' was already committed with a different payload.")
{
}
