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

    public MemoryProjectionSweep()
        : this(() => DateTime.UtcNow, null, null, null)
    {
    }

    internal MemoryProjectionSweep(
        Func<DateTime> utcNow,
        string? claudeHome,
        string? userHome,
        Action<string, byte[]>? projectionWriter)
    {
        _utcNow = utcNow;
        _claudeHome = claudeHome;
        _userHome = userHome;
        _projectionWriter = projectionWriter;
    }

    /// <summary>One bounded pass, exposed for fixture-only retry and restart tests.</summary>
    internal async Task SweepOnceAsync(TextWriter? diagnostics = null, CancellationToken cancellationToken = default)
    {
        diagnostics ??= Console.Error;
        var blockedByImport = await MemoryImportOperationStore
            .RecoverPendingAsync(diagnostics, cancellationToken).ConfigureAwait(false);
        foreach (var location in CanonicalStoreInventory.Scan(BatonPaths.Root))
        {
            if (location.OperationProblems is { Count: > 0 }
                || blockedByImport.Contains(location.Slug)
                || blockedByImport.Contains(FleetMemory.Slug))
            {
                continue;
            }

            var stored = await MemoryStore.ReadAllAsync(location.EntriesFile, cancellationToken).ConfigureAwait(false);
            var obligation = await MemoryProjectionObligationStore.ReadAsync(location.Slug, cancellationToken)
                .ConfigureAwait(false);
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

            var exitCode = await MemorySyncCommand.ExecuteAsync(
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

            if (exitCode == 0)
            {
                continue;
            }

            diagnostics.Write(output.ToString());
            var after = await MemoryProjectionObligationStore.ReadAsync(location.Slug, cancellationToken)
                .ConfigureAwait(false);
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
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await SweepOnceAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"MemoryProjectionSweep: sweep iteration failed: {ex.Message}");
            }

            DaemonTickLedger.Instance.RecordTick(
                nameof(MemoryProjectionSweep), Stopwatch.GetElapsedTime(started), Interval);

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
