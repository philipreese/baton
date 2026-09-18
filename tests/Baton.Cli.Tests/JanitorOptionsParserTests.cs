namespace Baton.Cli.Tests;

public sealed class JanitorOptionsParserTests
{
    [Fact]
    public void Now_is_the_only_supported_route_and_help_names_the_exact_command()
    {
        Assert.Equal("Usage: baton janitor now", JanitorOptionsParser.Usage);
        Assert.IsType<JanitorOptions>(JanitorOptionsParser.Parse(["now"]));
        Assert.Throws<CliArgumentException>(() => JanitorOptionsParser.Parse([]));
        Assert.Throws<CliArgumentException>(() => JanitorOptionsParser.Parse(["now", "extra"]));
        Assert.Throws<CliArgumentException>(() => JanitorOptionsParser.Parse(["run"]));
    }
}
