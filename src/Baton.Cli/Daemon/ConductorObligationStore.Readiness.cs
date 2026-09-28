using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Conductor;
using Baton.Status;

namespace Baton.Cli.Daemon;

public sealed partial class ConductorObligationStore
{
    private static readonly JsonSerializerOptions ReadinessJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>
    /// Launches at most once for an owned readiness obligation. The durable marker is written before
    /// invoking the adapter. A marker without a complete response means the provider may have been
    /// charged; only operator recovery may clear it. Queue transports retain their separate retry rule.
    /// </summary>
    public Task<(ConductorObligation Obligation, RetainedReadinessResponse Response)> DecideReadinessOnceAsync(
        string key,
        Func<ConductorObligation, CancellationToken, Task<RetainedReadinessResponse>> launch,
        CancellationToken cancellationToken = default) =>
        WithReadinessExclusiveAsync(key,
            () => DecideReadinessOnceCoreAsync(key, launch, cancellationToken), cancellationToken);

    private async Task<(ConductorObligation Obligation, RetainedReadinessResponse Response)> DecideReadinessOnceCoreAsync(
        string key,
        Func<ConductorObligation, CancellationToken, Task<RetainedReadinessResponse>> launch,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(launch);
        var obligation = await ReadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new ConductorObligationStoreException($"No conductor obligation exists for '{key}'.");
        if (obligation.RequestedAction != "readiness-decision" || obligation.TargetRevision is null
            || obligation.ContextSha256 is null || obligation.TargetWorkspace is null
            || obligation.Adapter != "codex-subscription-cli"
            || obligation.AdapterCapability != "one-shot-readiness" || !obligation.AdapterSupported)
        {
            throw new ConductorObligationStoreException($"Obligation '{key}' is not an owned readiness decision.");
        }

        var directory = ReadinessDirectory(key);
        var marker = Path.Combine(directory, "launch.json");
        var responsePath = Path.Combine(directory, "response.json");
        var receiptPath = Path.Combine(directory, "receipt.json");
        Directory.CreateDirectory(directory);

        if (obligation.Status == ConductorObligationStatus.Pending)
        {
            obligation = await SubmitAsync(key, (_, _) => Task.FromResult(new ConductorTransportResult(false)),
                cancellationToken).ConfigureAwait(false);
        }
        if (obligation.Status is not (ConductorObligationStatus.Submitted
            or ConductorObligationStatus.TransportAcknowledged))
        {
            throw new ConductorObligationStoreException(
                $"Readiness obligation '{key}' has ineligible status '{obligation.Status}'.");
        }

        // The one per-key mutex covers this admission and the launch through durable acknowledgement.
        // Block/ObserveAction take it too, so no terminal transition can slip between the check
        // and the provider spawn. Queue-owned keys never enter this lock.
        var current = await ReadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new ConductorObligationStoreException($"No conductor obligation exists for '{key}'.");
        RequireSamePayload(current, obligation);
        if (current.Status is not (ConductorObligationStatus.Submitted
            or ConductorObligationStatus.TransportAcknowledged))
        {
            throw new ConductorObligationStoreException(
                $"Readiness obligation '{key}' has ineligible status '{current.Status}'.");
        }
        var started = !File.Exists(marker);
        if (started)
        {
            if (current.Status == ConductorObligationStatus.TransportAcknowledged
                || File.Exists(responsePath) || File.Exists(receiptPath))
            {
                throw new ConductorObligationStoreException(
                    $"Readiness obligation '{key}' has response/acknowledgement without launch marker; operator recovery required.");
            }

            WriteNewDurable(marker, JsonSerializer.Serialize(new ReadinessLaunchMarker(
                obligation.ObligationId, obligation.TargetRevision!, obligation.ContextSha256!,
                _now().ToUniversalTime()), ReadinessJson));
        }

        ReadinessLaunchMarker? retainedMarker;
        try
        {
            retainedMarker = JsonSerializer.Deserialize<ReadinessLaunchMarker>(ReadBounded(marker, 1024), ReadinessJson);
        }
        catch (JsonException ex)
        {
            throw new ConductorObligationStoreException(
                $"Readiness launch marker for '{key}' is malformed; operator recovery required.", ex);
        }
        if (retainedMarker is null || retainedMarker.ObligationId != obligation.ObligationId
            || retainedMarker.Revision != obligation.TargetRevision
            || retainedMarker.ContextSha256 != obligation.ContextSha256)
        {
            throw new ConductorObligationStoreException(
                $"Readiness launch marker for '{key}' conflicts with obligation identity; operator recovery required.");
        }

        if (started)
        {
            try
            {
                var response = await launch(obligation, cancellationToken).ConfigureAwait(false);
                ValidateReadinessResponse(obligation, response);
                WriteNewDurable(responsePath, JsonSerializer.Serialize(response, ReadinessJson));
            }
            catch (Exception ex)
            {
                // The marker stays unresolved. A later call cannot guess whether the provider ran.
                var failure = Path.Combine(directory, "failure.txt");
                if (!File.Exists(failure))
                {
                    WriteNewDurable(failure, $"{_now():O}: {ex.GetType().Name}: {ex.Message}\n");
                }

                if (ex is OperationCanceledException) throw;
                throw new ConductorObligationStoreException(
                    $"Readiness launch for '{key}' failed after its durable marker: {ex.Message} "
                    + "Do not retry the model without operator recovery.", ex);
            }
        }

        if (!File.Exists(responsePath))
        {
            throw new ConductorObligationStoreException(
                $"Readiness launch for '{key}' is uncertain: launch marker exists without a complete "
                + "validated response. Do not retry the model; inspect the retained failure and provider evidence.");
        }

        var bytes = ReadBounded(responsePath, 64 * 1024);
        RetainedReadinessResponse retained;
        try
        {
            retained = JsonSerializer.Deserialize<RetainedReadinessResponse>(bytes, ReadinessJson)
                ?? throw new JsonException("Null readiness response.");
            ValidateReadinessResponse(obligation, retained);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or ConductorObligationStoreException)
        {
            throw new ConductorObligationStoreException(
                $"Retained readiness response for '{key}' is incomplete or invalid; operator recovery required.", ex);
        }

        var receipt = "readiness-sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (File.Exists(receiptPath))
        {
            var storedReceipt = Encoding.UTF8.GetString(ReadBounded(receiptPath, 256)).Trim();
            if (storedReceipt != receipt)
            {
                throw new ConductorObligationStoreException(
                    $"Readiness receipt for '{key}' conflicts with retained response; operator recovery required.");
            }
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
                $"Readiness obligation '{key}' did not retain its transport acknowledgement.");
        }

        return (acknowledged, retained);
    }

    private string ReadinessDirectory(string key)
    {
        var parent = Path.GetDirectoryName(_snapshotPath)
            ?? throw new ConductorObligationStoreException("Obligation projection has no parent directory.");
        var slug = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(parent, "readiness-decisions", slug);
    }

    internal string GetReadinessEvidenceDirectory(string key) => ReadinessDirectory(key);

    private static bool IsOwnedReadinessKey(string key) =>
        key is not null && key.StartsWith("owned-readiness:", StringComparison.Ordinal);

    private async Task<T> WithReadinessExclusiveAsync<T>(string key, Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var directory = ReadinessDirectory(key);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "admission.lock");
        // Mutex ownership is thread-affine; keep every await inside the synchronous callback's
        // blocked thread. The 4-minute wait exceeds the 3-minute provider bound plus teardown.
        return await Task.Run(() => MutexGuardedFileLock.RunUnderLock(path,
            "baton-readiness-admission", TimeSpan.FromMinutes(4),
            () => action().GetAwaiter().GetResult()), cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateReadinessResponse(ConductorObligation obligation, RetainedReadinessResponse response)
    {
        if (response.Decision is null || response.Decision.ObligationId != obligation.ObligationId
            || response.Decision.Repository != obligation.TargetProject
            || response.Decision.Revision != obligation.TargetRevision
            || response.Decision.ContextSha256 != obligation.ContextSha256
            || !Enum.IsDefined(response.Decision.Decision)
            || string.IsNullOrWhiteSpace(response.Decision.Explanation)
            || response.Decision.Explanation.Length > 4096
            || response.Adapter != "codex-subscription-cli"
            || response.Model != "gpt-5.6-luna" || response.Effort != "low")
        {
            throw new ConductorObligationStoreException("Readiness response identity, schema or pinned adapter is invalid.");
        }
    }

    private static byte[] ReadBounded(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maxBytes) throw new ConductorObligationStoreException($"Retained file '{path}' exceeds {maxBytes} bytes.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void WriteNewDurable(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private sealed record ReadinessLaunchMarker(string ObligationId, string Revision,
        string ContextSha256, DateTimeOffset StartedAt);
}
