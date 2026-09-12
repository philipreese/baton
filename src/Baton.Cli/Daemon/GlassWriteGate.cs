using System.Net;
using Baton.Vendors;

namespace Baton.Cli.Daemon;

/// <summary>
/// The single authorization rule for daemon-served Fleet Glass writes. The backend is deliberately
/// loopback-only: Tailscale Serve authenticates the tailnet user and forwards that identity to this
/// trusted-host boundary. A missing setting or identity never degrades into local unauthenticated
/// access.
/// </summary>
internal static class GlassWriteGate
{
    internal const string IdentityHeader = "Tailscale-User-Login";

    internal static GlassWriteDecision Evaluate(
        GlassListenerSettings settings,
        IPAddress? remoteAddress,
        IReadOnlyList<string>? identityHeaders)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        {
            return GlassWriteDecision.Refused("Glass writes require the loopback Serve backend.", LoginSeen(identityHeaders));
        }

        if (string.IsNullOrWhiteSpace(settings.OperatorLogin))
        {
            return GlassWriteDecision.Refused("Glass writes are disabled until Glass.OperatorLogin is configured.", LoginSeen(identityHeaders));
        }

        if (identityHeaders is not { Count: 1 } || string.IsNullOrWhiteSpace(identityHeaders[0]))
        {
            return GlassWriteDecision.Refused("Glass writes require one authenticated Tailscale user login.", false);
        }

        var login = identityHeaders[0].Trim();
        if (!string.Equals(login, settings.OperatorLogin.Trim(), StringComparison.Ordinal))
        {
            return GlassWriteDecision.Refused("This Tailscale user is not the configured Glass operator.", true);
        }

        return GlassWriteDecision.Allowed;
    }

    private static bool LoginSeen(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } && values.Any(value => !string.IsNullOrWhiteSpace(value));
}

internal sealed record GlassWriteDecision(bool IsAllowed, string? Refusal, bool LoginSeen)
{
    internal static GlassWriteDecision Allowed { get; } = new(true, null, true);

    internal static GlassWriteDecision Refused(string reason, bool loginSeen) => new(false, reason, loginSeen);
}
