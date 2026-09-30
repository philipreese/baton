namespace Baton.Cli;

/// <summary>Arguments for the read-only canonical memory consumer.</summary>
public sealed record MemoryReadOptions(string? Repository, MemoryAuditOutputFormat Format, bool Help);
