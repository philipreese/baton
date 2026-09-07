using System.Net;
using Baton.Cli.Daemon;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #1946 — the bind rule spec/baton.md §11 C-11 states: loopback and the tailnet interface only,
/// never <c>0.0.0.0</c>. Driven entirely by fixture candidates, never by the test machine's own
/// adapters: a test that only refused a LAN bind on a host that happens to have a LAN would be
/// measuring the host rather than the policy.
/// </summary>
public sealed class GlassBindPolicyTests
{
    /// <summary>An address on the Tailscale adapter, as Windows reports it (the string is in the
    /// description) — the shape the policy is supposed to accept.</summary>
    private static GlassBindPolicy.CandidateAddress OnTailscale(string address) =>
        new(IPAddress.Parse(address), "Tailscale", "Tailscale Tunnel");

    private static GlassBindPolicy.CandidateAddress OnWiFi(string address) =>
        new(IPAddress.Parse(address), "Wi-Fi", "Intel(R) Wi-Fi 6E AX211 160MHz");

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
    // Just outside the fd7a:115c:a1e0::/48 ULA on its last pinned byte.
    [InlineData("fd7a:115c:a1e1::1")]
    public void Refuses_every_address_that_is_neither_loopback_nor_tailnet(string address)
    {
        // Refused even when the Tailscale adapter itself claims to own it: out of range is out of
        // range, so neither signal alone can carry a bind.
        var candidate = OnTailscale(address);

        Assert.False(GlassBindPolicy.IsBindable(candidate));
        Assert.DoesNotContain(candidate.Address, GlassBindPolicy.SelectBindAddresses([candidate]).Addresses);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("::1")]
    public void Accepts_loopback_whatever_interface_owns_it(string address) =>
        Assert.True(GlassBindPolicy.IsBindable(OnWiFi(address)));

    [Theory]
    // The operator's own tailnet address, plus both ends of 100.64.0.0/10 and the IPv6 ULA
    // Tailscale assigns alongside it.
    [InlineData("100.101.139.26")]
    [InlineData("100.64.0.0")]
    [InlineData("100.127.255.255")]
    [InlineData("fd7a:115c:a1e0::1")]
    public void Accepts_a_tailnet_address_on_the_tailscale_adapter_and_binds_it_alongside_loopback(string address)
    {
        var candidate = OnTailscale(address);

        Assert.True(GlassBindPolicy.IsBindable(candidate));

        var selection = GlassBindPolicy.SelectBindAddresses([OnWiFi("192.168.1.72"), candidate]);
        Assert.Equal([IPAddress.Loopback, candidate.Address], selection.Addresses);
        Assert.Empty(selection.Refusals);
    }

    [Theory]
    // THE CGNAT CASE -- the reason the policy takes two signals rather than one, stated once in
    // GlassBindPolicy's own remarks.
    [InlineData("100.101.139.26")]
    [InlineData("fd7a:115c:a1e0::1")]
    public void Refuses_an_in_range_address_that_a_non_tailscale_interface_owns(string address)
    {
        var candidate = OnWiFi(address);

        Assert.False(GlassBindPolicy.IsBindable(candidate));

        var selection = GlassBindPolicy.SelectBindAddresses([candidate]);
        Assert.Equal([IPAddress.Loopback], selection.Addresses);

        // Refused out loud, and the line names the interface -- see BindSelection for why silence
        // would be the wrong answer here.
        var refusal = Assert.Single(selection.Refusals);
        Assert.Contains(address, refusal, StringComparison.Ordinal);
        Assert.Contains("Wi-Fi", refusal, StringComparison.Ordinal);
    }

    [Theory]
    // tailscale0 on Linux/macOS, the description on Windows, and the name on a renamed adapter --
    // whichever field carries the string, either is enough.
    [InlineData("tailscale0", null)]
    [InlineData("Tailnet", "Tailscale Tunnel")]
    [InlineData("TAILSCALE", null)]
    public void Reads_the_tailscale_interface_off_either_field(string? name, string? description) =>
        Assert.True(GlassBindPolicy.NamesTheTailscaleInterface(name, description));

    [Theory]
    // Fail closed: an address the policy cannot attribute to a named interface is not bindable.
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("Ethernet", "Realtek Gaming 2.5GbE Family Controller")]
    public void Refuses_an_interface_it_cannot_read_as_tailscales(string? name, string? description)
    {
        Assert.False(GlassBindPolicy.NamesTheTailscaleInterface(name, description));
        Assert.False(
            GlassBindPolicy.IsBindable(
                new GlassBindPolicy.CandidateAddress(IPAddress.Parse("100.101.139.26"), name, description)));
    }

    [Fact]
    public void Binds_loopback_even_when_the_machine_has_no_tailnet_address()
    {
        // Tailscale down, or never installed: the page stays reachable on the fleet machine itself.
        var selection = GlassBindPolicy.SelectBindAddresses(
            [OnWiFi("0.0.0.0"), OnWiFi("192.168.1.72"), OnWiFi("169.254.1.1")]);

        Assert.Equal([IPAddress.Loopback], selection.Addresses);
        Assert.Empty(selection.Refusals);
    }

    [Fact]
    public void Binds_loopback_only_when_the_interfaces_cannot_be_enumerated_at_all()
    {
        // Fail closed, at the outer edge: no candidates is what a failed enumeration hands the
        // policy, and it must not widen anything.
        var selection = GlassBindPolicy.SelectBindAddresses([]);

        Assert.Equal([IPAddress.Loopback], selection.Addresses);
    }

    [Fact]
    public void Never_binds_one_address_twice()
    {
        var tailnet = OnTailscale("100.101.139.26");

        Assert.Equal(
            [IPAddress.Loopback, tailnet.Address],
            GlassBindPolicy.SelectBindAddresses([tailnet, tailnet, OnWiFi("127.0.0.1")]).Addresses);
    }

    [Fact]
    public void Renders_a_prefix_the_listener_can_bind()
    {
        Assert.Equal("http://127.0.0.1:8420/", GlassBindPolicy.PrefixFor(IPAddress.Loopback, 8420));
        Assert.Equal(
            "http://100.101.139.26:8420/", GlassBindPolicy.PrefixFor(IPAddress.Parse("100.101.139.26"), 8420));

        // An IPv6 literal has to be bracketed or HttpListener refuses the prefix outright.
        Assert.Equal("http://[::1]:8420/", GlassBindPolicy.PrefixFor(IPAddress.IPv6Loopback, 8420));
        Assert.Equal(
            "http://[fd7a:115c:a1e0::1]:8420/",
            GlassBindPolicy.PrefixFor(IPAddress.Parse("fd7a:115c:a1e0::1"), 8420));
    }

    [Fact]
    public void The_machines_own_addresses_never_produce_a_refused_bind()
    {
        // The one arm that DOES touch this host -- not to assert what it finds (a CI runner has no
        // tailnet), but to prove the production enumeration can only ever hand the listener
        // addresses the fixture-driven policy above already approves.
        var selection = GlassBindPolicy.SelectBindAddresses();

        Assert.NotEmpty(selection.Addresses);
        Assert.All(selection.Addresses, address => Assert.True(GlassBindPolicy.IsPermittedAddress(address)));
    }
}
