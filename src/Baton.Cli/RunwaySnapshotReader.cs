using System.Text.Json;
using Baton.Cli.Mcp;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// Reads one vendor's latest PERSISTED usage snapshot for <see cref="RunwayGate"/> (#1848) — the file
/// the daemon's <c>VendorUsageHarvester</c> writes (#1391/#1869), or, since #1923, the one the hold's
/// own <see cref="OnDemandRunwayHarvest"/> wrote through that same writer moments earlier. <b>This
/// type still only ever reads a file</b>; it makes no <c>/usage</c> call itself. Deserializes the same <see cref="PersistedVendorUsage"/> shape <see cref="VendorUsageProjectionReader"/>
/// already reads, so there is one on-disk snapshot format, not a second one for the gate.
/// </summary>
public static class RunwaySnapshotReader
{
    /// <summary>
    /// The vendor's snapshot, or null when the file is absent, unreadable, or does not parse.
    /// <b>Null is not "no usage"</b> — <see cref="RunwayGate.Evaluate"/> turns it into a Hold, which
    /// is the whole point of returning it rather than an empty snapshot that would read as 0% used.
    /// </summary>
    public static VendorUsageSnapshot? Read(string vendor) =>
        ReadFrom(BatonPaths.VendorUsageSnapshotFile(vendor));

    /// <summary>Path-taking arm, so a test drives every failure mode without writing under the real
    /// <c>~/.baton</c> root.</summary>
    public static VendorUsageSnapshot? ReadFrom(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PersistedVendorUsage>(File.ReadAllText(path))?.ToSnapshot();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
