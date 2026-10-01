using Baton.Status;

namespace Baton.Cli.Tests.TestSupport;

/// <summary>
/// A throwaway <see cref="BatonPaths.Root"/> for a test class, scoped through
/// <see cref="BatonEnvironmentSnapshot.BeginScope"/> — never a process environment variable, so it is
/// parallel-safe and needs no <c>SerializedEnvironmentCollection</c> enrollment.
/// </summary>
/// <remarks>
/// <para>
/// Added by #1645's drain marker: <c>baton dispatch</c>/<c>redispatch</c>/<c>resume</c> now read
/// <see cref="BatonPaths.DrainMarkerFile"/> before they start, so every suite that drives one of those
/// verbs would otherwise consult the real <c>~/.baton</c> of whatever machine runs the suite — and go
/// red for an unrelated reason the moment a refresh was interrupted and left a marker behind. It also
/// keeps those suites from appending to the real <c>room-registry.jsonl</c>, which they did before.
/// </para>
/// <para>
/// Held as a field so the scope opens during construction and covers the whole test body.
/// <c>DrainMarkerCtorScopeControlTests</c> is the control that a constructor-opened scope is actually
/// visible to the test body — without it, this type could silently do nothing.
/// </para>
/// <para>
/// Isolates this process only. A test that SPAWNS a <c>baton</c> child process must also put
/// <see cref="Path"/> into that child's <c>BATON_HOME</c> (see
/// <c>ResumeCommandEndToEndTests.StartBatonProcess</c>): an <see cref="System.Threading.AsyncLocal{T}"/>
/// does not cross a process boundary.
/// </para>
/// </remarks>
public sealed class IsolatedBatonHome : IDisposable
{
    private readonly IDisposable _scope;

    public IsolatedBatonHome(string? baseDirectory = null)
    {
        Path = System.IO.Path.Combine(baseDirectory ?? System.IO.Path.GetTempPath(), $"baton-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = Path });
    }

    /// <summary>The temp directory standing in for <c>~/.baton</c>.</summary>
    public string Path { get; }

    public void Dispose()
    {
        _scope.Dispose();
        DirectoryCleanup.DeleteRecursively(Path);
    }
}
