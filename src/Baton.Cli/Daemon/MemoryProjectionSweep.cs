using System.Diagnostics;
using Baton.Memory;
using Baton.Status;
using Microsoft.Extensions.Hosting;

namespace Baton.Cli.Daemon;

/// <summary>
/// #2138's daemon safety sweep: runs the same idempotent apply function as manual sync for every
/// canonical store, while honoring durable retry backoff and the escalation ceiling.
/// </summary>
public sealed class MemoryProjectionSweep : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    private readonly Func<DateTime> _utcNow;
    private readonly string? _claudeHome;
    private readonly string? _userHome;
    private readonly Action<string, byte[]>? _projectionWriter;
    private readonly DaemonLoopDriver _loopDriver;

    public MemoryProjectionSweep()
        : this(() => DateTime.UtcNow, null, null, null, null)
    {
    }

    internal MemoryProjectionSweep(
        Func<DateTime> utcNow,
        string? claudeHome,
        string? userHome,
        Action<string, byte[]>? projectionWriter,
        DaemonLoopDriver? loopDriver = null)
    {
        _utcNow = utcNow;
        _claudeHome = claudeHome;
        _userHome = userHome;
        _projectionWriter = projectionWriter;
        _loopDriver = loopDriver ?? new DaemonLoopDriver();
    }

    /// <summary>One bounded pass, exposed for fixture-only retry and restart tests.</summary>
    internal async Task SweepOnceAsync(TextWriter? diagnostics = null, CancellationToken cancellationToken = default)
    {
        diagnostics ??= Console.Error;
        IReadOnlySet<string> blockedByImport;
        IReadOnlyList<CanonicalStoreLocation> locations;
        using (DaemonLoopDriver.EnterPhase("memory-store"))
        {
            blockedByImport = await MemoryImportOperationStore
                .RecoverPendingAsync(diagnostics, cancellationToken).ConfigureAwait(false);
            locations = [.. CanonicalStoreInventory.Scan(BatonPaths.Root)];
        }

        foreach (var location in locations)
        {
            if (location.OperationProblems is { Count: > 0 }
                || blockedByImport.Contains(location.Slug)
                || blockedByImport.Contains(FleetMemory.Slug))
            {
                continue;
            }

            IReadOnlyList<MemoryEntry> stored;
            MemoryProjectionObligation? obligation;
            using (DaemonLoopDriver.EnterPhase("memory-store"))
            {
                stored = await MemoryStore.ReadAllAsync(location.EntriesFile, cancellationToken).ConfigureAwait(false);
                obligation = await MemoryProjectionObligationStore.ReadAsync(location.Slug, cancellationToken)
                    .ConfigureAwait(false);
            }
            var repository = MemoryStoreIdentity.Resolve(
                location.Slug, location.Repository, stored, obligation);
            if (repository is null)
            {
                continue;
            }

            if (obligation is not null && !MemoryProjectionObligationStore.IsDue(obligation, _utcNow()))
            {
                continue;
            }

            var output = new StringWriter();
            var claimed = obligation is null
                ? null
                : new Dictionary<string, MemoryProjectionObligation>(StringComparer.OrdinalIgnoreCase)
                {
                    [location.Slug] = obligation,
                };

            int exitCode;
            using (DaemonLoopDriver.EnterPhase("memory-projection"))
            {
                exitCode = await MemorySyncCommand.ExecuteAsync(
                    new MemorySyncOptions(
                        repository,
                        Apply: true,
                        Check: false,
                        MemoryAuditOutputFormat.Text,
                        RepositoryFactsDirectory: null,
                        Help: false),
                    output,
                    _claudeHome,
                    cancellationToken,
                    _userHome,
                    claimed,
                    _projectionWriter,
                    _utcNow).ConfigureAwait(false);
            }

            if (exitCode == 0)
            {
                continue;
            }

            diagnostics.Write(output.ToString());
            MemoryProjectionObligation? after;
            using (DaemonLoopDriver.EnterPhase("memory-store"))
            {
                after = await MemoryProjectionObligationStore.ReadAsync(location.Slug, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (after?.Status == MemoryProjectionObligationStatus.Escalated)
            {
                diagnostics.WriteLine(
                    $"MemoryProjectionSweep: ESCALATED '{after.Repository}' after " +
                    $"{after.FailedAttempts} failed projection attempts. {after.NextAction}");
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _loopDriver.RunAsync(
            nameof(MemoryProjectionSweep),
            async cancellationToken =>
            {
                await SweepOnceAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return Interval;
            },
            () => Interval,
            _ => Interval,
            ex => Console.Error.WriteLine($"MemoryProjectionSweep: sweep iteration failed: {ex.Message}"),
            stoppingToken).ConfigureAwait(false);
    }
}
