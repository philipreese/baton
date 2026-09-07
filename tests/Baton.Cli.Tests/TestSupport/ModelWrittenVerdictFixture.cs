namespace Baton.Cli.Tests.TestSupport;

/// <summary>
/// A model-written <c>verdict.json</c>, complete with an <c>instruments</c> array the worker invented
/// for itself — the fabricated test run <c>VerdictInstrumentStamp</c> exists to remove. Fed to a fake
/// worker (or to <c>baton supply</c>) as a fixture file so no JSON is assembled through a shell echo.
/// <para>
/// Shared rather than restated per test class (#1911): four verbs now stamp, and a fixture copied into
/// each of their test files is four places one shape can drift.
/// </para>
/// </summary>
internal static class ModelWrittenVerdictFixture
{
    internal const string Json =
        """
        {"reviewedRef": "1882-lane", "summary": "all good", "findings": [],
         "instruments": [{"command": "dotnet test", "exitCode": 0, "wallClockMs": 91002}]}
        """;

    /// <summary>Writes <see cref="Json"/> to <paramref name="path"/> and returns that path.</summary>
    internal static async Task<string> WriteAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, Json, cancellationToken);
        return path;
    }
}
