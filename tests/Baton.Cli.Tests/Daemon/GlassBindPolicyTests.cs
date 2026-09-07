using System.Net;
using Baton.Cli.Daemon;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #1946 — the bind rule spec/baton.md §11 C-11 states: loopback and the tailnet interface only,
/// never <c>0.0.0.0</c>. Driven entirely by fixture addresses, never by the test machine's own
/// adapters: a test that only refused a LAN bind on a host that happens to have a LAN would be
/// measuring the host rather than the policy.
/// </summary>
public sealed class GlassBindPolicyTests
{
    [Theory]
    // The refusal the decision names by address, first.
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    // Every other shape of "reachable from off the tailnet" -- a LAN address, a routable public
    // address, a Hyper-V/WSL host address (which this machine really carries), and link-local.
    [InlineData("192.168.1.72")]
    [InlineData("172.29.176.1")]
    [InlineData("10.0.0.5")]
    [InlineData("203.0.113.10")]
    [InlineData("169.254.185.155")]
    // Just outside 100.64.0.0/10 on either side -- the two off-by-one boundaries of the CGNAT range.
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    // A Tailscale IPv6 ULA: refused deliberately, see GlassBindPolicy's own remarks.
    [InlineData("fd7a:115c:a1e0::1")]
    public void Refuses_every_address_that_is_neither_loopback_nor_tailnet(string address)
    {
        var parsed = IPAddress.Parse(address);

        Assert.False(GlassBindPolicy.IsBindable(parsed));
        Assert.DoesNotContain(parsed, GlassBindPolicy.SelectBindAddresses([parsed]));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("::1")]
    public void Accepts_loopback(string address) =>
        Assert.True(GlassBindPolicy.IsBindable(IPAddress.Parse(address)));

    [Theory]
    // The operator's own tailnet address, plus both ends of 100.64.0.0/10.
    [InlineData("100.101.139.26")]
    [InlineData("100.64.0.0")]
    [InlineData("100.127.255.255")]
    public void Accepts_a_tailnet_address_and_binds_it_alongside_loopback(string address)
    {
        var parsed = IPAddress.Parse(address);

        Assert.True(GlassBindPolicy.IsBindable(parsed));

        var selected = GlassBindPolicy.SelectBindAddresses([IPAddress.Parse("192.168.1.72"), parsed]);
        Assert.Equal([IPAddress.Loopback, parsed], selected);
    }

    [Fact]
    public void Binds_loopback_even_when_the_machine_has_no_tailnet_address()
    {
        // Tailscale down, or never installed: the page stays reachable on the fleet machine itself.
        var selected = GlassBindPolicy.SelectBindAddresses(
            [IPAddress.Any, IPAddress.Parse("192.168.1.72"), IPAddress.Parse("169.254.1.1")]);

        Assert.Equal([IPAddress.Loopback], selected);
    }

    [Fact]
    public void Never_binds_one_address_twice()
    {
        var tailnet = IPAddress.Parse("100.101.139.26");

        Assert.Equal(
            [IPAddress.Loopback, tailnet],
            GlassBindPolicy.SelectBindAddresses([tailnet, tailnet, IPAddress.Loopback]));
    }

    [Fact]
    public void Renders_a_prefix_the_listener_can_bind()
    {
        Assert.Equal("http://127.0.0.1:8420/", GlassBindPolicy.PrefixFor(IPAddress.Loopback, 8420));
        Assert.Equal(
            "http://100.101.139.26:8420/", GlassBindPolicy.PrefixFor(IPAddress.Parse("100.101.139.26"), 8420));
    }

    [Fact]
    public void The_machines_own_addresses_never_produce_a_refused_bind()
    {
        // The one arm that DOES touch this host -- not to assert what it finds (a CI runner has no
        // tailnet), but to prove the production enumeration can only ever hand the listener
        // addresses the fixture-driven policy above already approves.
        var selected = GlassBindPolicy.SelectBindAddresses();

        Assert.NotEmpty(selected);
        Assert.All(selected, address => Assert.True(GlassBindPolicy.IsBindable(address)));
    }
}
