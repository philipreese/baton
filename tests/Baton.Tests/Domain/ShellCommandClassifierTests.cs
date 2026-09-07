using Baton.Domain;
using Xunit;

namespace Baton.Tests.Domain;

/// <summary>
/// Coverage for <see cref="ShellCommandClassifier"/> and the ceilings it selects (#1998). Every shape
/// the operator's ruling names is asserted, and so are the two negatives that make the test
/// discriminating rather than a restatement of the table: <c>git push</c> inside a string literal, and
/// <c>git status</c>.
/// </summary>
public sealed class ShellCommandClassifierTests
{
    [Theory]
    [InlineData("git push")]
    [InlineData("git push origin 1998-lane")]
    [InlineData("gh pr create --fill")]
    public void Shipping_shapes_classify_as_shipping(string commandLine) =>
        Assert.Equal(ShellCommandClass.Shipping, ShellCommandClassifier.Classify(commandLine));

    [Theory]
    [InlineData("python tools/buildlock.py dotnet build -warnaserror")]
    [InlineData("python tools\\buildlock.py dotnet test")]
    [InlineData("pixi run audit-recordonce")]
    [InlineData("pixi run audit-completeness")]
    [InlineData("dotnet test")]
    [InlineData("dotnet build -warnaserror")]
    public void Gate_shapes_classify_as_gate(string commandLine) =>
        Assert.Equal(ShellCommandClass.Gate, ShellCommandClassifier.Classify(commandLine));

    /// <summary>
    /// The discriminating negatives. A substring search would call the first one shipping — which is why
    /// the table matches LEADING TOKENS — and the second shares only <c>git</c> with the two-token key.
    /// </summary>
    [Theory]
    [InlineData("echo \"git push\"")]
    [InlineData("echo 'gh pr create'")]
    [InlineData("git commit -m \"ready to git push\"")]
    [InlineData("git status")]
    [InlineData("git pushx")]
    [InlineData("pixi run test")]
    [InlineData("dotnet format --verify-no-changes")]
    [InlineData("")]
    public void Everything_else_classifies_as_other(string commandLine) =>
        Assert.Equal(ShellCommandClass.Other, ShellCommandClassifier.Classify(commandLine));

    /// <summary>
    /// A chained line takes the highest class any segment takes — the ceiling has to cover the whole
    /// line. The reverse order is asserted too, so this cannot pass by only ever reading the last
    /// segment.
    /// </summary>
    [Theory]
    [InlineData("git add -A && git push")]
    [InlineData("git push && gh pr create --fill")]
    [InlineData("git push | tee push.log")]
    [InlineData("dotnet build && git push")]
    public void A_chained_line_takes_its_highest_segment(string commandLine) =>
        Assert.Equal(ShellCommandClass.Shipping, ShellCommandClassifier.Classify(commandLine));

    [Fact]
    public void A_chain_of_gate_and_ordinary_segments_is_a_gate() =>
        Assert.Equal(ShellCommandClass.Gate, ShellCommandClassifier.Classify("git status && dotnet test"));

    /// <summary>
    /// The ordering the ceilings rest on, asserted rather than assumed: a gate gets more than the flat
    /// ceiling every command had before #1998, and a shipping command gets the gate's plus the transfer.
    /// The exact values live on <see cref="ShellCommandCeilings"/> and are deliberately not restated
    /// here — this test asserts the RELATIONS the ruling states, which is what would still be true after
    /// a re-measurement.
    /// </summary>
    [Fact]
    public void The_ceilings_are_ordered_and_derived_the_way_the_ruling_states()
    {
        Assert.Equal(ShellCommandCeilings.Other, ShellCommandCeilings.For(ShellCommandClass.Other));
        Assert.Equal(ShellCommandCeilings.MeasuredGatesFastWallClock * 1.5, ShellCommandCeilings.For(ShellCommandClass.Gate));
        Assert.Equal(
            ShellCommandCeilings.For(ShellCommandClass.Gate) + ShellCommandCeilings.PushTransferAllowance,
            ShellCommandCeilings.For(ShellCommandClass.Shipping));
        Assert.True(ShellCommandCeilings.For(ShellCommandClass.Gate) > ShellCommandCeilings.For(ShellCommandClass.Other));
        Assert.True(ShellCommandCeilings.For(ShellCommandClass.Shipping) > ShellCommandCeilings.For(ShellCommandClass.Gate));
    }

    /// <summary>
    /// Only a shipping-class timeout carries the marker the room reads back, and it names the class and
    /// the ceiling that actually applied — the injected one, not the table's, or the text would lie
    /// under any caller that bounds a command differently.
    /// </summary>
    [Fact]
    public void Only_a_shipping_timeout_carries_the_marker_and_the_text_names_the_effective_ceiling()
    {
        var shipping = ShellCommandCeilings.DescribeTimeout(ShellCommandClass.Shipping, TimeSpan.FromSeconds(42));
        var gate = ShellCommandCeilings.DescribeTimeout(ShellCommandClass.Gate, TimeSpan.FromSeconds(7));
        var other = ShellCommandCeilings.DescribeTimeout(ShellCommandClass.Other, TimeSpan.FromSeconds(1));

        Assert.True(ShellCommandCeilings.IsShippingCeilingTimeout(shipping));
        Assert.Contains("shipping command ceiling (42 s)", shipping, StringComparison.Ordinal);
        Assert.False(ShellCommandCeilings.IsShippingCeilingTimeout(gate));
        Assert.Contains("gate command ceiling (7 s)", gate, StringComparison.Ordinal);
        Assert.False(ShellCommandCeilings.IsShippingCeilingTimeout(other));
        Assert.Contains("default command ceiling (1 s)", other, StringComparison.Ordinal);
        Assert.False(ShellCommandCeilings.IsShippingCeilingTimeout(null));
    }
}
