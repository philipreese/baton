namespace Baton.Cli.Tests;

/// <summary>
/// Isolates fixtures that temporarily install a <c>MemoryStore.Ledger</c> observer. The
/// observer is process-global, so a disabled-parallelization collection prevents another test's
/// append from consuming the fixture's staged callback.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MemoryStoreObserverCollection
{
    public const string Name = "memory-store-observer";
}
