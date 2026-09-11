using Baton.Memory;

namespace Baton.Cli;

/// <summary>
/// The one write-trigger entry point shared by <c>memory add</c>, <c>retract</c> and <c>import</c>
/// (#2138). Manual sync and the daemon sweep reach the same <see cref="MemorySyncCommand"/> apply
/// path; this type adds only the canonical-commit result wording.
/// </summary>
internal static class MemoryProjectionTrigger
{
    public static async Task ProjectAfterCanonicalWriteAsync(
        string repository,
        TextWriter output,
        string? claudeHomeOverride = null,
        string? userHomeOverride = null,
        Action<string, byte[]>? projectionWriterOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine("AUTOMATIC PROJECTION -- canonical memory is committed; updating Baton's owned caches now.");

        var syncOutput = new StringWriter();
        int exitCode;
        try
        {
            exitCode = await MemorySyncCommand.ExecuteAsync(
                new MemorySyncOptions(
                    FleetMemory.IsFleet(repository) ? null : repository,
                    Apply: true,
                    Check: false,
                    MemoryAuditOutputFormat.Text,
                    RepositoryFactsDirectory: null,
                    Help: false),
                syncOutput,
                claudeHomeOverride,
                cancellationToken,
                userHomeOverride,
                obligationsOverride: null,
                projectionWriterOverride).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            exitCode = 1;
            syncOutput.WriteLine($"COMMITTED BUT UNPROJECTED -- {repository}: {ex.GetType().Name}: {ex.Message}");
            syncOutput.WriteLine(
                "  Projection recovery state could not be confirmed. The canonical store and its durable " +
                "identity remain saved; repair the reported path and retry or allow the daemon to sweep it.");
        }

        output.Write(syncOutput.ToString());
        if (exitCode == 0)
        {
            output.WriteLine(
                "AUTOMATIC PROJECTION FINISHED -- Baton updated only its generated cache files. " +
                "This does not establish that a vendor loaded or consumed them.");
            return;
        }

        MemoryProjectionObligation? pending = null;
        try
        {
            pending = await MemoryProjectionObligationStore.ReadAsync(
                FleetMemory.SlugFor(repository), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The sync report above already carries the concrete storage failure. This read only
            // selects truthful summary wording and must not turn the committed mutation into a throw.
        }

        output.WriteLine(
            pending is not null
                ? "CANONICAL COMMIT SUCCEEDED; PROJECTION IS PENDING -- the durable obligation above " +
                  "will be retried by the daemon. No vendor loading or consumption is claimed."
                : "CANONICAL COMMIT SUCCEEDED; PROJECTION IS UNRECORDED -- projection did not complete " +
                  "and no durable pending promise is made. The store identity remains recoverable by " +
                  "inventory after the obligation path is repaired; no vendor loading or consumption is claimed.");
    }
}
