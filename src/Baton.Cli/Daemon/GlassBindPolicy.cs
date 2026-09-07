using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Baton.Cli.Daemon;

/// <summary>
/// #1946 — which addresses <see cref="GlassHttpService"/> is allowed to bind, decided in one pure,
/// testable place. spec/baton.md §11 C-11 states the rule this enforces: the tailnet plane is
/// "bound to the tailnet/loopback interface only, never <c>0.0.0.0</c>".
/// </summary>
/// <remarks>
/// <para>
/// <b>Detected by address range, not by adapter name.</b> Tailscale's interface is
/// <c>tailscale0</c> on Linux/macOS and "Tailscale" on Windows, and neither string is contractual;
/// the CGNAT range it assigns from is (<c>100.64.0.0/10</c>, RFC 6598, which Tailscale documents as
/// the pool every node address comes from). A name match would silently bind nothing on a machine
/// whose adapter was renamed, and — worse — could match an adapter that is not the tailnet at all.
/// </para>
/// <para>
/// <b>IPv4 only, deliberately.</b> Tailscale also assigns a <c>fd7a:115c:a1e0::/48</c> ULA address
/// per node, and binding it would be a second prefix per machine with its own URL-ACL requirement
/// for no reachability this does not already have — every Tailscale node has the 100.64/10 address.
/// An IPv6 tailnet address is therefore *refused* like any other non-loopback address rather than
/// silently treated as bindable; ::1 is loopback and is allowed.
/// </para>
/// <para>
/// <b>This is an allowlist, and there is no setting that widens it</b> (see
/// <see cref="Baton.Vendors.GlassListenerSettings"/>): a wildcard bind is not a configuration
/// mistake this refuses, it is a shape the code cannot express.
/// </para>
/// </remarks>
internal static class GlassBindPolicy
{
    /// <summary>
    /// The RFC 6598 shared-address (CGNAT) range Tailscale assigns node addresses from:
    /// <c>100.64.0.0/10</c> — i.e. first octet 100, second octet 64–127.
    /// </summary>
    internal static bool IsTailnetAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var octets = address.GetAddressBytes();
        return octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127;
    }

    /// <summary>
    /// Whether <paramref name="address"/> may be bound at all. <see cref="IPAddress.Any"/> /
    /// <see cref="IPAddress.IPv6Any"/> fail this by construction — they are neither loopback nor in
    /// 100.64/10 — which is the point: the wildcard is refused by the same predicate that refuses a
    /// LAN address, not by a special case that could be edited away on its own.
    /// </summary>
    internal static bool IsBindable(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return IPAddress.IsLoopback(address) || IsTailnetAddress(address);
    }

    /// <summary>
    /// The addresses to bind, given every address the machine's interfaces carry: loopback always
    /// (so the page is reachable on the fleet machine itself even with Tailscale down), plus each
    /// distinct tailnet address found. Never empty, and never anything <see cref="IsBindable"/>
    /// refuses.
    /// </summary>
    internal static IReadOnlyList<IPAddress> SelectBindAddresses(IEnumerable<IPAddress> machineAddresses)
    {
        ArgumentNullException.ThrowIfNull(machineAddresses);

        var selected = new List<IPAddress> { IPAddress.Loopback };
        foreach (var address in machineAddresses)
        {
            if (address is not null && IsTailnetAddress(address) && !selected.Contains(address))
            {
                selected.Add(address);
            }
        }

        return selected;
    }

    /// <summary>The machine's own unicast addresses, for the production call of
    /// <see cref="SelectBindAddresses(IEnumerable{IPAddress})"/>. Split from the pure overload so the
    /// policy above is tested against fixture addresses rather than against whatever the test
    /// machine's adapters happen to be — a test that passed only on a host with Tailscale installed
    /// would be measuring the host.</summary>
    internal static IReadOnlyList<IPAddress> SelectBindAddresses()
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    addresses.Add(unicast.Address);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Enumeration failing must not stop the loopback listener from coming up; the operator
            // then simply has no tailnet prefix, which the bound-URL log line makes visible.
        }

        return SelectBindAddresses(addresses);
    }

    /// <summary>The <see cref="HttpListener"/> prefix for one bound address and port.</summary>
    internal static string PrefixFor(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        return $"http://{address}:{port}/";
    }
}
