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
        var exitCode = await MemorySyncCommand.ExecuteAsync(
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

        output.Write(syncOutput.ToString());
        output.WriteLine(
            exitCode == 0
                ? "AUTOMATIC PROJECTION FINISHED -- Baton updated only its generated cache files. " +
                  "This does not establish that a vendor loaded or consumed them."
                : "CANONICAL COMMIT SUCCEEDED; PROJECTION IS PENDING -- the durable obligation above " +
                  "will be retried by the daemon. No vendor loading or consumption is claimed.");
    }
}
