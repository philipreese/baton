namespace Baton.Tests.Fixtures;

/// <summary>
/// Representative fixture for wait-ceiling diagnostic guidance (#2460).
/// Validates that the recommended block-bodied lambda wait annotation compiles
/// and survives repository formatting (<c>dotnet format --verify-no-changes</c>).
/// </summary>
internal static class WaitCeilingFixture
{
    public static async Task ExampleAsync()
    {
        await RunAsync(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5)); // wait-ok: formatter-safe fixture timeout
        });
    }

    private static async Task RunAsync(Func<Task> action) => await action();
}
