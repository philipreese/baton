namespace Baton.Memory;

/// <summary>Exact removal evidence from one serialized reversal attempt.</summary>
public sealed record MemoryImportReversal(
    ImportManifest Manifest,
    int RemovedEntries,
    int RemovedLinks,
    IReadOnlySet<string> ChangedRepositories,
    IReadOnlyList<string> Shortfalls);
