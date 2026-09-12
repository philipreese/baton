using System.Net;
using Baton.Cli.Daemon;
using Baton.Vendors;

namespace Baton.Cli.Tests.Daemon;

public sealed class GlassWriteGateTests
{
    [Fact]
    public void Allows_only_one_exact_login_on_loopback()
    {
        var settings = new GlassListenerSettings { OperatorLogin = "operator@example.com" };

        Assert.True(GlassWriteGate.Evaluate(
            settings, IPAddress.Loopback, ["operator@example.com"]).IsAllowed);
        Assert.False(GlassWriteGate.Evaluate(
            settings, IPAddress.Loopback, ["OPERATOR@example.com"]).IsAllowed);
        Assert.False(GlassWriteGate.Evaluate(
            settings, IPAddress.Loopback, [" operator@example.com "]).IsAllowed);
        Assert.False(GlassWriteGate.Evaluate(
            new GlassListenerSettings { OperatorLogin = " operator@example.com " },
            IPAddress.Loopback,
            ["operator@example.com"]).IsAllowed);
        Assert.False(GlassWriteGate.Evaluate(
            settings, IPAddress.Loopback, []).IsAllowed);
        Assert.False(GlassWriteGate.Evaluate(
            settings, IPAddress.Loopback, ["operator@example.com", "other@example.com"]).IsAllowed);
    }

    [Fact]
    public void Fails_closed_without_configuration_or_for_a_non_loopback_peer()
    {
        Assert.False(GlassWriteGate.Evaluate(
            new GlassListenerSettings(), IPAddress.Loopback, ["operator@example.com"]).IsAllowed);
        Assert.False(GlassWriteGate.Evaluate(
            new GlassListenerSettings { OperatorLogin = "operator@example.com" },
            IPAddress.Parse("100.64.0.10"),
            ["operator@example.com"]).IsAllowed);
    }
}
