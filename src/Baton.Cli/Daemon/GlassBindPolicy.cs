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
/// <b>Two signals, and one of them alone is not enough.</b> A tailnet address is an address in
/// Tailscale's range <i>that the Tailscale adapter owns</i>. The range half is
/// <c>100.64.0.0/10</c> (RFC 6598) plus the <c>fd7a:115c:a1e0::/48</c> ULA Tailscale documents as
/// the pools every node address comes from — but 100.64/10's actual purpose is carrier-grade NAT,
/// so a cellular modem, a Starlink-style gateway, a hotel or a campus network can hand this machine
/// an address out of it directly. Binding on the range alone would offer the glass — which has no
/// authentication of its own — to every other client on that segment. The interface half
/// (<see cref="NamesTheTailscaleInterface"/>) is equally insufficient on its own, for the mirror
/// reason: adapter names are not contractual, so a name match could hit an adapter that is not the
/// tailnet. Requiring both is what makes the invariant in <c>spec/baton.md</c> §11 C-11 — "the
/// tailnet/loopback interface only" — true of the code rather than of the range's usual case.
/// </para>
/// <para>
/// <b>The residual, stated.</b> Both signals are heuristics over what the OS reports; neither is a
/// cryptographic identity. An adapter deliberately named "Tailscale" and given a 100.64/10 address
/// by something other than Tailscale would still pass. Closing that would mean cross-checking
/// <c>tailscale status --json</c> at bind time, which buys little here: the residual requires
/// local control of this machine's adapter configuration, which is strictly more access than
/// reading the page.
/// </para>
/// <para>
/// <b>Fail closed.</b> Anything that leaves the policy without an interface to attribute an address
/// to — enumeration unavailable, an unnamed adapter, a platform that reports neither — yields
/// loopback only, never a wider bind.
/// </para>
/// <para>
/// <b>This is an allowlist, and there is no setting that widens it</b> (see
/// <see cref="Baton.Vendors.GlassListenerSettings"/>): a wildcard bind is not a configuration
/// mistake this refuses, it is a shape the code cannot express.
/// </para>
/// </remarks>
internal static class GlassBindPolicy
{
    /// <summary>One address the machine carries, together with the interface that owns it. The pair
    /// is the unit the policy decides on: an address on its own cannot be judged, which is the whole
    /// correction #2028's review forced.</summary>
    internal sealed record CandidateAddress(IPAddress Address, string? InterfaceName, string? InterfaceDescription);

    /// <summary>What <see cref="SelectBindAddresses(IEnumerable{CandidateAddress})"/> decided: the
    /// addresses to bind, plus one line per in-range address refused for the interface that owns it,
    /// which the caller logs — a silent refusal here reads to an operator exactly like Tailscale
    /// being down.</summary>
    internal sealed record BindSelection(IReadOnlyList<IPAddress> Addresses, IReadOnlyList<string> Refusals);

    /// <summary>
    /// Whether <paramref name="address"/> is in one of Tailscale's documented pools:
    /// <c>100.64.0.0/10</c> (RFC 6598 shared space — first octet 100, second octet 64–127) or the
    /// <c>fd7a:115c:a1e0::/48</c> ULA. <b>Necessary, never sufficient</b> — the range is shared with
    /// carrier-grade NAT, so <see cref="IsBindable(CandidateAddress)"/> is what decides a bind.
    /// </summary>
    internal static bool IsTailnetRange(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var octets = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127,
            AddressFamily.InterNetworkV6 => octets[0] == 0xFD && octets[1] == 0x7A && octets[2] == 0x11
                                            && octets[3] == 0x5C && octets[4] == 0xA1 && octets[5] == 0xE0,
            _ => false,
        };
    }

    /// <summary>Whether the owning interface is Tailscale's. <c>tailscale0</c> on Linux/macOS and
    /// "Tailscale" in the Windows adapter description; matched case-insensitively on either field,
    /// because which of the two carries the string differs by platform. A null or empty pair is a
    /// refusal, not a pass.</summary>
    internal static bool NamesTheTailscaleInterface(string? interfaceName, string? interfaceDescription) =>
        (interfaceName?.Contains("tailscale", StringComparison.OrdinalIgnoreCase) ?? false)
        || (interfaceDescription?.Contains("tailscale", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Whether this address, on this interface, may be bound. Loopback always; anything else only
    /// when it is in range <i>and</i> the Tailscale adapter owns it. <see cref="IPAddress.Any"/> /
    /// <see cref="IPAddress.IPv6Any"/> fail this by construction — they are neither loopback nor in
    /// either tailnet pool — which is the point: the wildcard is refused by the same predicate that
    /// refuses a LAN address, not by a special case that could be edited away on its own.
    /// </summary>
    internal static bool IsBindable(CandidateAddress candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return IPAddress.IsLoopback(candidate.Address)
               || (IsTailnetRange(candidate.Address)
                   && NamesTheTailscaleInterface(candidate.InterfaceName, candidate.InterfaceDescription));
    }

    /// <summary>The address half of <see cref="IsBindable(CandidateAddress)"/>, for callers holding a
    /// bound prefix rather than an interface — a post-hoc check that nothing outside loopback and the
    /// tailnet pools was bound. <b>Necessary, never sufficient</b>: it cannot see the interface, so it
    /// must not be used to decide a bind.</summary>
    internal static bool IsPermittedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return IPAddress.IsLoopback(address) || IsTailnetRange(address);
    }

    /// <summary>
    /// The addresses to bind, given every address the machine's interfaces carry: loopback always
    /// (so the page is reachable on the fleet machine itself even with Tailscale down), plus each
    /// distinct tailnet address found. Never empty, and never anything
    /// <see cref="IsBindable(CandidateAddress)"/> refuses.
    /// </summary>
    internal static BindSelection SelectBindAddresses(IEnumerable<CandidateAddress> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var selected = new List<IPAddress> { IPAddress.Loopback };
        var refusals = new List<string>();
        foreach (var candidate in candidates)
        {
            if (candidate?.Address is null || IPAddress.IsLoopback(candidate.Address))
            {
                continue;
            }

            if (!IsTailnetRange(candidate.Address))
            {
                continue;
            }

            if (!NamesTheTailscaleInterface(candidate.InterfaceName, candidate.InterfaceDescription))
            {
                // Named, not swallowed: this is the CGNAT case, and an operator who sees only "no
                // tailnet prefix" would reasonably conclude Tailscale was down.
                refusals.Add(
                    $"GlassHttpService: not binding {candidate.Address} — it is in Tailscale's range but is " +
                    $"owned by interface '{candidate.InterfaceName ?? "(unnamed)"}' " +
                    $"({candidate.InterfaceDescription ?? "no description"}), not a Tailscale adapter. " +
                    "That range is also carrier-grade NAT space, so this is a cellular/hotel/campus " +
                    "uplink rather than the tailnet.");
                continue;
            }

            if (!selected.Contains(candidate.Address))
            {
                selected.Add(candidate.Address);
            }
        }

        return new BindSelection(selected, refusals);
    }

    /// <summary>The machine's own unicast addresses and the interfaces owning them, for the
    /// production call of <see cref="SelectBindAddresses(IEnumerable{CandidateAddress})"/>. Split from
    /// the pure overload so the policy above is tested against fixture candidates rather than against
    /// whatever the test machine's adapters happen to be — a test that passed only on a host with
    /// Tailscale installed would be measuring the host.</summary>
    internal static BindSelection SelectBindAddresses()
    {
        var candidates = new List<CandidateAddress>();
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
                    candidates.Add(new CandidateAddress(unicast.Address, nic.Name, nic.Description));
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            // Fail closed, and keep the daemon up: with no interface to attribute an address to, no
            // address can pass the second signal, so this yields loopback only.
            candidates.Clear();
        }

        return SelectBindAddresses(candidates);
    }

    /// <summary>The <see cref="HttpListener"/> prefix for one bound address and port. An IPv6 literal
    /// is bracketed — <c>http://fd7a:...:8420/</c> is not a prefix the listener will accept.</summary>
    internal static string PrefixFor(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        var host = address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
        return $"http://{host}:{port}/";
    }
}
