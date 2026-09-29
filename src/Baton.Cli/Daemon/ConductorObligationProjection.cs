using System.Text.Json.Nodes;
using Baton.Status;

namespace Baton.Cli.Daemon;

/// <summary>Public, bounded read-only view; never serialize the private obligation record.</summary>
internal static class ConductorObligationProjection
{
    internal const int RowLimit = 100;

    internal static async Task<JsonObject> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var inspection = await new ConductorObligationStore(FleetEventLog.OpenOperational())
                .InspectAsync(cancellationToken).ConfigureAwait(false);
            // Missing evidence is not proof that there are no obligations. Fact-only recovery is
            // valid, but absence of both snapshot and retained facts cannot establish an all-clear.
            if (!File.Exists(BatonPaths.ConductorObligationsFile)
                && inspection.Obligations.Count == 0 && inspection.Quarantined.Count == 0)
            {
                return Unavailable();
            }
            return Project(inspection);
        }
        catch (Exception ex) when (ex is ConductorObligationStoreException or IOException or UnauthorizedAccessException)
        {
            // Exception messages and quarantine reasons may contain local paths or secrets.
            Console.Error.WriteLine("Conductor obligation projection: evidence unavailable; inspection failed.");
            return Unavailable();
        }
    }

    private static JsonObject Unavailable() => new()
    {
        ["available"] = false,
        ["reason"] = "evidence-unavailable",
    };

    internal static JsonObject Project(ConductorObligationInspection inspection)
    {
        var eligible = inspection.Obligations
            .Where(row => !inspection.Quarantined.ContainsKey(row.IdempotencyKey))
            .OrderBy(row => row.Status == ConductorObligationStatus.ActionObserved)
            .ThenBy(row => row.CreatedAt)
            .ThenBy(row => row.ObligationId, StringComparer.Ordinal).ToArray();
        var rows = new JsonArray();
        foreach (var row in eligible.Take(RowLimit))
        {
            rows.Add(new JsonObject
            {
                ["requestedAction"] = row.RequestedAction switch
                {
                    "continue" => "Continue work",
                    "readiness-decision" => "Assess readiness",
                    _ => "Other action (details withheld)",
                },
                ["owner"] = row.Owner == ConductorContinuation.Owner
                    ? "queue-lifecycle" : "Other owner (details withheld)",
                ["status"] = row.Status.ToString(),
                ["createdAt"] = row.CreatedAt.ToString("O"),
                ["reason"] = row.Status switch
                {
                    ConductorObligationStatus.Pending => "Waiting for submission",
                    ConductorObligationStatus.Submitted => "Submitted; action not yet observed",
                    ConductorObligationStatus.TransportAcknowledged => "Receipt acknowledged; action not yet observed",
                    ConductorObligationStatus.ActionObserved => "Requested action independently observed",
                    ConductorObligationStatus.Blocked or ConductorObligationStatus.Unsupported => SafeCause(row.Reason),
                    _ => "Unknown state; inspect local evidence",
                },
            });
        }
        return new JsonObject
        {
            ["available"] = true,
            ["rows"] = rows,
            ["quarantinedCount"] = inspection.Quarantined.Count,
            ["omittedCount"] = Math.Max(0, eligible.Length - RowLimit),
            ["unresolvedCount"] = eligible.Count(row => row.Status != ConductorObligationStatus.ActionObserved),
            ["completedCount"] = eligible.Count(row => row.Status == ConductorObligationStatus.ActionObserved),
        };
    }

    // Exact matches only: never disclose free-text suffixes, paths, receipts or exception messages.
    private static string SafeCause(string? reason) => reason switch
    {
        "the open obligation is not a deterministic queue continuation" => "Invalid continuation identity",
        "durable queue evidence contains more than one continuation for the obligation" => "Conflicting continuation records",
        "the source attempt is no longer present and no continuation queue evidence exists" => "Source attempt missing; no continuation recorded",
        "the adapter does not support this obligation" => "Adapter capability unavailable",
        _ => "Cause withheld; inspect local evidence for details",
    };
}
