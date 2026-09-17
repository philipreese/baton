using Baton.Cli;

namespace Baton.Cli.Tests;

public class CliOperationalFailureClassifierTests
{
    [Theory]
    [InlineData("io")]
    [InlineData("unauthorized")]
    public void Classifies_operational_file_failures(string kind)
    {
        Exception exception = kind switch
        {
            "io" => new IOException("fixture"),
            "unauthorized" => new UnauthorizedAccessException("fixture"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Assert.True(CliOperationalFailureClassifier.IsOperational(exception));
    }

    [Fact]
    public void Does_not_classify_unrelated_failures_as_operational()
    {
        Assert.False(
            CliOperationalFailureClassifier.IsOperational(new InvalidOperationException("fixture")));
    }
}
