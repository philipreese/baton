using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baton.Accounting;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;

namespace Baton.Cli.Daemon;

internal enum StoppedWorkAdviceDurabilityPoint
{
    BeforeLaunchMarker,
    AfterLaunchMarker,
    AfterResponse,
    AfterAcknowledgement,
}

internal sealed record StoppedWorkAdviceView(
    StoppedWorkJudgmentState State,
    DateTimeOffset ObservedAt,
    int? Issue = null,
    WorkStage? Stage = null,
    RetainedStoppedWorkAdviceResponse? Response = null);

public sealed partial class ConductorObligationStore
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ActiveStoppedWorkAdvice =
        new(StringComparer.OrdinalIgnoreCase);

    internal bool IsStoppedWorkAdviceActive(string key) =>
        ActiveStoppedWorkAdvice.ContainsKey(StoppedWorkAdviceDirectory(key));

    internal Action<StoppedWorkAdviceDurabilityPoint>? StoppedWorkAdviceDurabilityObserver { get; set; }

    /// <summary>
    /// Runs one stopped-work advice call under a distinct durable protocol. It never accepts a
    /// readiness or continuation obligation, and a marker without a complete response is uncertain
    /// rather than retryable.
    /// </summary>
    public Task<(ConductorObligation Obligation, RetainedStoppedWorkAdviceResponse Response)>
        DecideStoppedWorkOnceAsync(
            string key,
            StoppedWorkAdviceRequest request,
            StoppedWorkAdviceContext context,
            Func<ConductorObligation, CancellationToken, Task> preflight,
            Func<ConductorObligation, StoppedWorkAdviceRequest, StoppedWorkAdviceContext,
                string, CancellationToken, Task<RetainedStoppedWorkAdviceResponse>> launch,
            CancellationToken cancellationToken = default) =>
        WithReadinessExclusiveAsync(key,
            () => DecideStoppedWorkOnceCoreAsync(key, request, context, preflight, launch, cancellationToken),
            cancellationToken);

    private async Task<(ConductorObligation Obligation, RetainedStoppedWorkAdviceResponse Response)>
        DecideStoppedWorkOnceCoreAsync(
            string key,
            StoppedWorkAdviceRequest request,
            StoppedWorkAdviceContext context,
            Func<ConductorObligation, CancellationToken, Task> preflight,
            Func<ConductorObligation, StoppedWorkAdviceRequest, StoppedWorkAdviceContext,
                string, CancellationToken, Task<RetainedStoppedWorkAdviceResponse>> launch,
            CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(launch);
        var obligation = await ReadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new ConductorObligationStoreException($"No conductor obligation exists for '{key}'.");
        if (!StoppedWorkJudgmentKey.TryParse(key, out var repository, out var tag, out var attempt, out var stage)
            || request.ObligationId != obligation.ObligationId || request.Repository != repository
            || request.Tag != tag || request.AttemptId != attempt || request.Stage != stage
            || StoppedWorkAdviceEvidence.Hash(context) != request.ContextSha256
            || request.Holder != obligation.Owner
            || !string.Equals(obligation.RequestedAction, StoppedWorkJudgmentKey.Action, StringComparison.Ordinal)
            || !string.Equals(obligation.Adapter, StoppedWorkJudgmentKey.Adapter, StringComparison.Ordinal)
            || !string.Equals(obligation.AdapterCapability, StoppedWorkJudgmentKey.Capability, StringComparison.Ordinal)
            || !obligation.AdapterSupported
            || obligation.ContextSha256 is null
            || !string.Equals(obligation.ContextSha256, request.ContextSha256, StringComparison.Ordinal)
            || !string.Equals(obligation.TargetProject, request.Repository, StringComparison.Ordinal)
            || !string.Equals(obligation.TargetExecution, request.Tag, StringComparison.Ordinal)
            || !string.Equals(obligation.TargetRevision, request.PullRequestHead, StringComparison.Ordinal))
        {
            throw new ConductorObligationStoreException($"Obligation '{key}' is not an owned stopped-work judgment.");
        }

        if (obligation.Status == ConductorObligationStatus.Pending)
        {
            obligation = await SubmitAsync(key,
                (_, _) => Task.FromResult(new ConductorTransportResult(false)), cancellationToken)
                .ConfigureAwait(false);
        }
        if (obligation.Status is not (ConductorObligationStatus.Submitted
            or ConductorObligationStatus.TransportAcknowledged))
        {
            throw new ConductorObligationStoreException(
                $"Stopped-work advice obligation '{key}' has ineligible status '{obligation.Status}'.");
        }

        var directory = StoppedWorkAdviceDirectory(key);
        var marker = Path.Combine(directory, "launch.json");
        var responsePath = Path.Combine(directory, "response.json");
        var receiptPath = Path.Combine(directory, "receipt.json");
        Directory.CreateDirectory(directory);
        var started = !File.Exists(marker);
        if (started)
        {
            if (obligation.Status == ConductorObligationStatus.TransportAcknowledged
                || File.Exists(responsePath) || File.Exists(receiptPath))
            {
                throw new ConductorObligationStoreException(
                    $"Stopped-work advice obligation '{key}' has response/acknowledgement without launch marker; operator recovery required.");
            }

            // A deterministic refusal is NOT a charged/uncertain call. Keep these checks inside
            // the same per-key lock as launch, before its irreversible at-most-once marker.
            await preflight(obligation, cancellationToken).ConfigureAwait(false);
            var current = await ReadAsync(key, cancellationToken).ConfigureAwait(false)
                ?? throw new ConductorObligationStoreException("Stopped-work obligation disappeared before launch.");
            RequireSamePayload(current, obligation);
            if (current.Status != ConductorObligationStatus.Submitted)
                throw new ConductorObligationStoreException("Stopped-work obligation is no longer eligible for launch.");
            cancellationToken.ThrowIfCancellationRequested();
            StoppedWorkAdviceDurabilityObserver?.Invoke(StoppedWorkAdviceDurabilityPoint.BeforeLaunchMarker);
            WriteNewDurable(marker, JsonSerializer.Serialize(new StoppedWorkAdviceLaunchMarker(
                obligation.ObligationId, obligation.ContextSha256!, _now().ToUniversalTime()), ReadinessJson));
        }

        ValidateStoppedWorkMarker(marker, obligation);

        if (started)
        {
            StoppedWorkAdviceDurabilityObserver?.Invoke(StoppedWorkAdviceDurabilityPoint.AfterLaunchMarker);
            ActiveStoppedWorkAdvice.TryAdd(directory, 0);
            try
            {
                var response = await launch(obligation, request, context, directory, cancellationToken)
                    .ConfigureAwait(false);
                ValidateStoppedWorkAdviceResponse(obligation, response);
                WriteNewDurable(responsePath, JsonSerializer.Serialize(response, ReadinessJson));
                StoppedWorkAdviceDurabilityObserver?.Invoke(StoppedWorkAdviceDurabilityPoint.AfterResponse);
            }
            catch (Exception ex)
            {
                var failure = Path.Combine(directory, "failure.txt");
                if (!File.Exists(failure))
                    WriteNewDurable(failure, $"{_now():O}: {ex.GetType().Name}: {ex.Message}\n");
                if (ex is OperationCanceledException) throw;
                throw new ConductorObligationStoreException(
                    $"Stopped-work advice launch for '{key}' failed after its durable marker; operator recovery required.", ex);
            }
            finally
            {
                ActiveStoppedWorkAdvice.TryRemove(directory, out _);
            }
        }

        if (!File.Exists(responsePath))
            throw new ConductorObligationStoreException(
                $"Stopped-work advice launch for '{key}' is uncertain: launch marker exists without a complete validated response.");

        RetainedStoppedWorkAdviceResponse retained;
        try
        {
            retained = JsonSerializer.Deserialize<RetainedStoppedWorkAdviceResponse>(
                ReadBounded(responsePath, 64 * 1024), ReadinessJson)
                ?? throw new JsonException("Null stopped-work advice response.");
            ValidateStoppedWorkAdviceResponse(obligation, retained);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or ConductorObligationStoreException)
        {
            throw new ConductorObligationStoreException(
                $"Retained stopped-work advice response for '{key}' is incomplete or invalid; operator recovery required.", ex);
        }

        var receipt = "stopped-work-advice-sha256:" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                ReadBounded(responsePath, 64 * 1024))).ToLowerInvariant();
        if (File.Exists(receiptPath))
        {
            var stored = System.Text.Encoding.UTF8.GetString(ReadBounded(receiptPath, 256)).Trim();
            if (!string.Equals(stored, receipt, StringComparison.Ordinal))
                throw new ConductorObligationStoreException(
                    $"Stopped-work advice receipt for '{key}' conflicts with retained response; operator recovery required.");
        }
        else
        {
            WriteNewDurable(receiptPath, receipt + "\n");
        }

        var acknowledged = await SubmitAsync(key,
            (_, _) => Task.FromResult(new ConductorTransportResult(true, receipt)), cancellationToken)
            .ConfigureAwait(false);
        if (acknowledged.Status != ConductorObligationStatus.TransportAcknowledged
            || acknowledged.TransportReceipt != receipt)
        {
            throw new ConductorObligationStoreException(
                $"Stopped-work advice obligation '{key}' did not retain its transport acknowledgement.");
        }

        StoppedWorkAdviceDurabilityObserver?.Invoke(StoppedWorkAdviceDurabilityPoint.AfterAcknowledgement);
        return (acknowledged, retained);
    }

    internal string GetStoppedWorkAdviceEvidenceDirectory(string key) => StoppedWorkAdviceDirectory(key);

    internal void MarkStoppedWorkAdviceStale(string key)
    {
        var path = Path.Combine(StoppedWorkAdviceDirectory(key), "source-stale");
        if (!File.Exists(path)) WriteNewDurable(path, "Source evidence changed or could not be verified after advice.\n");
    }

    internal void MarkStoppedWorkAdviceSourceChecked(string key)
    {
        var path = Path.Combine(StoppedWorkAdviceDirectory(key), "source-checked");
        if (!File.Exists(path)) WriteNewDurable(path, "Source evidence verified after advice.\n");
    }

    /// <summary>
    /// Read-only projection input for a stopped-work row. Only a complete response that passes the
    /// same identity validator used after launch is exposed; private paths, receipts and failures
    /// never cross this seam.
    /// </summary>
    internal async Task<StoppedWorkAdviceView?> ReadStoppedWorkAdviceViewAsync(
        ConductorObligation row, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(row.RequestedAction, StoppedWorkJudgmentKey.Action, StringComparison.Ordinal)
            || !string.Equals(row.AdapterCapability, StoppedWorkJudgmentKey.Capability, StringComparison.Ordinal))
        {
            return null;
        }

        var observedAt = row.CreatedAt.ToUniversalTime();
        WorkStage? stage = StoppedWorkJudgmentKey.TryParse(row.IdempotencyKey, out _, out _, out _, out var parsedStage)
            ? parsedStage : null;
        if (row.Status == ConductorObligationStatus.Unsupported)
            return new(StoppedWorkJudgmentState.Unsupported, observedAt, Stage: stage);
        if (row.Status == ConductorObligationStatus.Blocked)
            return new(StoppedWorkJudgmentState.Blocked, observedAt, Stage: stage);

        var responsePath = Path.Combine(StoppedWorkAdviceDirectory(row.IdempotencyKey), "response.json");
        var markerPath = Path.Combine(StoppedWorkAdviceDirectory(row.IdempotencyKey), "launch.json");
        if (!File.Exists(responsePath))
        {
            return new(
                IsStoppedWorkAdviceActive(row.IdempotencyKey) ? StoppedWorkJudgmentState.Running :
                File.Exists(markerPath) ? StoppedWorkJudgmentState.Uncertain :
                    StoppedWorkJudgmentState.Pending,
                observedAt,
                Stage: stage);
        }

        try
        {
            var responseBytes = ReadBounded(responsePath, 64 * 1024);
            var response = JsonSerializer.Deserialize<RetainedStoppedWorkAdviceResponse>(
                responseBytes, ReadinessJson)
                ?? throw new JsonException("Null response.");
            ValidateStoppedWorkAdviceResponse(row, response);
            ValidateStoppedWorkMarker(markerPath, row);
            if (!HasValidStoppedWorkReceipt(row, responseBytes))
                return new(StoppedWorkJudgmentState.Uncertain, observedAt, Stage: stage);
            var queue = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
            var sources = queue.Items.Where(item => item.StoppedWorkJudgment?.Key == row.IdempotencyKey).ToArray();
            var source = sources.Length == 1 ? sources[0] : null;
            var identity = RepositoryIdentity.From("https://" + row.TargetProject, null);
            var holder = identity is null ? null : await ConductorClaimStore.GetClaimAsync(identity,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var current = source is { Halted: true, StoppedWorkJudgment: { } intent }
                && intent.ContextSha256 == row.ContextSha256 && source.AttemptId == intent.AttemptId
                && source.Stage == intent.Stage && holder?.Holder == row.Owner
                && File.Exists(Path.Combine(StoppedWorkAdviceDirectory(row.IdempotencyKey), "source-checked"))
                && !File.Exists(Path.Combine(StoppedWorkAdviceDirectory(row.IdempotencyKey), "source-stale"));
            return new(current ? StoppedWorkJudgmentState.Available : StoppedWorkJudgmentState.Stale,
                observedAt, Issue: source?.Issue, Stage: stage, Response: response);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
            or ConductorObligationStoreException or ConductorClaimException)
        {
            // Projection callers need a safe state token, not a local path or provider diagnostic.
            return new(StoppedWorkJudgmentState.Uncertain, observedAt, Stage: stage);
        }
    }

    private bool HasValidStoppedWorkReceipt(ConductorObligation row, byte[] responseBytes)
    {
        try
        {
            var receiptPath = Path.Combine(StoppedWorkAdviceDirectory(row.IdempotencyKey), "receipt.json");
            if (!File.Exists(receiptPath) || string.IsNullOrWhiteSpace(row.TransportReceipt)) return false;
            var expected = "stopped-work-advice-sha256:"
                + Convert.ToHexString(SHA256.HashData(responseBytes)).ToLowerInvariant();
            var stored = Encoding.UTF8.GetString(ReadBounded(receiptPath, 256)).Trim();
            return string.Equals(stored, expected, StringComparison.Ordinal)
                && string.Equals(row.TransportReceipt, expected, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ConductorObligationStoreException)
        {
            return false;
        }
    }

    private string StoppedWorkAdviceDirectory(string key)
    {
        var parent = Path.GetDirectoryName(_snapshotPath)
            ?? throw new ConductorObligationStoreException("Obligation projection has no parent directory.");
        var slug = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(parent, "stopped-work-advice", slug);
    }

    private static void ValidateStoppedWorkAdviceResponse(
        ConductorObligation obligation, RetainedStoppedWorkAdviceResponse response)
    {
        if (!StoppedWorkJudgmentKey.TryParse(obligation.IdempotencyKey, out var repository,
                out var tag, out var attempt, out _)
            || response.Decision is null
            || response.Decision.ObligationId != obligation.ObligationId
            || response.Decision.Repository != obligation.TargetProject
            || response.Decision.Repository != repository
            || response.Decision.Tag != tag || response.Decision.Tag != obligation.TargetExecution
            || response.Decision.AttemptId != attempt.Value
            || response.Decision.ContextSha256 != obligation.ContextSha256
            || !Enum.IsDefined(response.Decision.Choice)
            || string.IsNullOrWhiteSpace(response.Decision.Explanation)
            || response.Decision.Explanation.Length > 4096
            || response.Adapter != StoppedWorkJudgmentKey.Adapter
            || response.Model != Baton.Vendors.CodexReadinessDecisionAdapter.Model
            || response.Effort != Baton.Vendors.CodexReadinessDecisionAdapter.Effort
            || response.CompletedAt == default)
        {
            throw new ConductorObligationStoreException(
                "Stopped-work advice response identity, schema or pinned adapter is invalid.");
        }
    }

    private static void ValidateStoppedWorkMarker(string path, ConductorObligation obligation)
    {
        try
        {
            var marker = JsonSerializer.Deserialize<StoppedWorkAdviceLaunchMarker>(ReadBounded(path, 1024), ReadinessJson);
            if (marker is null || marker.ObligationId != obligation.ObligationId
                || marker.ContextSha256 != obligation.ContextSha256 || marker.StartedAt == default)
                throw new JsonException("Marker identity mismatch.");
        }
        catch (JsonException ex)
        {
            throw new ConductorObligationStoreException("Stopped-work launch marker is invalid; operator recovery required.", ex);
        }
    }

    private sealed record StoppedWorkAdviceLaunchMarker(
        string ObligationId, string ContextSha256, DateTimeOffset StartedAt);
}
