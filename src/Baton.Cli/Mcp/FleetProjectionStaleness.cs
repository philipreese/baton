using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Status;

namespace Baton.Cli.Mcp;

/// <summary>
/// #1981 — how old the daemon's fleet projection file is, read at <c>fleet_status</c> call time.
/// <para>
/// The daemon can hang with its process alive: on 2026-09-06 it stopped writing
/// <see cref="BatonPaths.FleetProjectionFile"/> for thirteen minutes while the scheduled task still
/// reported Running, and every consumer kept serving the frozen picture as if it were current. This
/// type is the programmatic half of the fix — <c>fleet_status</c> carries the projection's age and a
/// <c>stale</c> flag, so a conductor reading the tool sees the same fact the operator's banner shows.
/// </para>
/// <para>
/// It answers a question about the DAEMON, not about the rooms: <c>fleet_status</c> itself scans the
/// rooms directory live on every call, so its own <c>rooms[]</c> are always fresh. What can be stale
/// is the projection file every other consumer (the pusher, and through it the glass) reads instead.
/// </para>
/// <para>
/// <b>Absent, unreadable, or unparseable reads STALE, with no age.</b> Same fail-closed posture as
/// <c>pusher.py</c>'s own <c>read_projection_file</c> (spec/baton.md §6): "no evidence the daemon
/// wrote anything" is the same operational fact as "it last wrote an hour ago", and the alternative —
/// reporting a clean <c>stale: false</c> because the file is missing — is exactly the silence #1981
/// is about. The age is omitted rather than fabricated when it cannot be computed.
/// </para>
/// </summary>
internal static class FleetProjectionStaleness
{
    /// <summary>The reading for <see cref="BatonPaths.FleetProjectionFile"/> as of <paramref name="now"/>.</summary>
    internal static FleetProjectionStalenessReading Read(DateTimeOffset now) =>
        Read(BatonPaths.FleetProjectionFile, now, FleetProjectionWriter.StaleAfter());

    /// <param name="path">The projection file to read — parameterised for the tests, never for a
    /// second production location.</param>
    /// <param name="staleAfter"><see cref="FleetProjectionWriter.StaleAfter"/> in production; a test
    /// passes its own so the arms don't have to wait three real ticks.</param>
    internal static FleetProjectionStalenessReading Read(string path, DateTimeOffset now, TimeSpan staleAfter)
    {
        string text;
        try
        {
            // FileShare.ReadWrite | FileShare.Delete, never File.ReadAllText (spec/baton.md §7, #1782):
            // the daemon rewrites this file by writing a temp and MOVING it over the target, and a
            // reader holding it with the default FileShare.Read makes that move throw a sharing
            // violation. Two failures at once if this got it wrong -- a healthy daemon's own write
            // would be blocked by the very tool asking whether it is healthy, and the IOException on
            // this side would then be reported below as a hang.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FleetProjectionStalenessReading(null, true);
        }

        string? derivedAt;
        try
        {
            using var document = JsonDocument.Parse(text);
            derivedAt = document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("derived_at", out var derivedAtElement)
                        && derivedAtElement.ValueKind == JsonValueKind.String
                ? derivedAtElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return new FleetProjectionStalenessReading(null, true);
        }

        if (derivedAt is null || !DateTimeOffset.TryParse(
                derivedAt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var derived))
        {
            return new FleetProjectionStalenessReading(null, true);
        }

        // Negative ages (a file written by a machine whose clock is ahead of this one) floor at 0
        // rather than reporting a projection derived in the future -- which would also read as
        // fresh under any threshold, so the floor is not what decides staleness here.
        var age = Math.Max(0, (now - derived).TotalSeconds);
        return new FleetProjectionStalenessReading(Math.Round(age, 1), age > staleAfter.TotalSeconds);
    }
}

/// <summary>#1981 — <see cref="FleetProjectionStaleness"/>'s answer. <see cref="AgeSeconds"/> is null
/// exactly when no <c>derived_at</c> could be read, which is also always <see cref="Stale"/>.</summary>
internal readonly record struct FleetProjectionStalenessReading(double? AgeSeconds, bool Stale);
