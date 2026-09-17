using Baton.Accounting;

namespace Baton.Queue;

/// <summary>
/// Durable, repository-scoped authority for one queue dispatch to create canonical memory entries.
/// </summary>
/// <param name="DispatchId">Opaque immutable identity of the admitted dispatch.</param>
/// <param name="Repository">Canonical repository identity this grant may write to.</param>
public sealed record MemoryAddDispatchGrant(string DispatchId, string Repository)
{
    public const string Capability = "memory-add";

    public bool IsWellFormed =>
        !string.IsNullOrWhiteSpace(DispatchId) &&
        !string.Equals(Repository, "fleet", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(RepositoryIdentity.TryCanonicalize(Repository), Repository, StringComparison.Ordinal);
}
