using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Concurrency;
using Baton.Status;

namespace Baton.Steering;

public sealed record CorrectionRequest(string MessageId, string ExecutionId, string PayloadSha256,
    string Adapter, string SessionId, string Target, int ProcessId, DateTime ProcessStartUtc,
    string OsPrincipal, DateTimeOffset RequestedUtc);

/// <summary>A native answer acknowledges transport, not worker consumption.</summary>
public sealed record CorrectionReceipt(CorrectionRequest Request, SteeringReceiptState State,
    string? Receipt = null, string? Reason = null);

/// <summary>
/// One immutable correction per execution, retained outside the worker outbox. Each newline-terminated
/// journal fact is fsynced before success. A crash after claim or admission never authorizes retry;
/// a crash during a write leaves evidence that fails closed, without truncation or repair. Losing an
/// answer leaves OutcomeUnknown. These tests can exercise interrupted writes, not power-loss hardware.
/// </summary>
public sealed class ExecutionCorrectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };
    private readonly string _room;

    public ExecutionCorrectionStore(string room)
    {
        _room = BatonPaths.RecordKey(room);
        if (!Directory.Exists(_room)) throw new DirectoryNotFoundException($"Correction room '{_room}' does not exist.");
    }

    public CorrectionReceipt? Query(string executionId) => UnderLock(() => Read(executionId)?.Receipt);

    public bool TryClaim(CorrectionRequest request) => UnderLock(() =>
    {
        Validate(request);
        if (Read(request.ExecutionId) is { } prior)
        {
            Match(prior.Receipt.Request, request);
            return false;
        }
        Directory.CreateDirectory(Path.Combine(_room, "steering"));
        Write(request.ExecutionId, new Fact("claimed", request, null, null, null), create: true);
        return true;
    });

    /// <summary>Durably claims the only native-tool admission before the caller invokes it.</summary>
    public bool TryAdmitTool(CorrectionRequest request) => UnderLock(() =>
    {
        var prior = Require(request);
        if (prior.Admitted) return false;
        Write(request.ExecutionId, new Fact("admitted", null, null, null, null), create: false);
        return true;
    });

    public CorrectionReceipt RecordAnswer(CorrectionRequest request, bool accepted, string? receipt, string? reason) => UnderLock(() =>
    {
        var prior = Require(request);
        if (!prior.Admitted) throw new InvalidOperationException("Correction answer has no durable tool admission.");
        var result = new CorrectionReceipt(prior.Receipt.Request,
            accepted ? SteeringReceiptState.TransportAcknowledged : SteeringReceiptState.Rejected, receipt, reason);
        if (prior.Receipt.State != SteeringReceiptState.OutcomeUnknown)
        {
            if (prior.Receipt != result) throw new InvalidOperationException("Correction answer conflicts with retained evidence.");
            return prior.Receipt;
        }
        Write(request.ExecutionId, new Fact("answered", null, accepted, receipt, reason), create: false);
        return result;
    });

    private Projection Require(CorrectionRequest request)
    {
        Validate(request);
        var prior = Read(request.ExecutionId) ?? throw new InvalidOperationException("Correction has no durable claim.");
        Match(prior.Receipt.Request, request);
        return prior;
    }

    private Projection? Read(string executionId)
    {
        var path = JournalPath(executionId);
        string text;
        // Only genuine absence means unclaimed. Access and IO failures must never become absence.
        try { text = File.ReadAllText(path, new UTF8Encoding(false, true)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (DecoderFallbackException ex) { throw new InvalidOperationException("Invalid correction journal encoding.", ex); }
        if (text.Length == 0 || !text.EndsWith('\n')) throw new InvalidOperationException("Incomplete correction journal.");
        Projection? result = null;
        foreach (var line in text[..^1].Split('\n'))
        {
            Fact fact;
            try { fact = JsonSerializer.Deserialize<Fact>(line, JsonOptions) ?? throw new JsonException("Null correction fact."); }
            catch (JsonException ex) { throw new InvalidOperationException("Corrupt correction journal.", ex); }
            switch (fact.Kind)
            {
                case "claimed" when result is null && fact.Request is not null && fact.Accepted is null && fact.Receipt is null && fact.Reason is null:
                    try { Validate(fact.Request); }
                    catch (ArgumentException ex) { throw new InvalidOperationException("Invalid correction claim.", ex); }
                    if (fact.Request.ExecutionId != executionId) throw new InvalidOperationException("Correction execution identity mismatch.");
                    result = new(new(fact.Request, SteeringReceiptState.OutcomeUnknown), false);
                    break;
                case "admitted" when result is { Admitted: false } && fact.Request is null && fact.Accepted is null && fact.Receipt is null && fact.Reason is null:
                    result = result with { Admitted = true };
                    break;
                case "answered" when result is { Admitted: true, Receipt.State: SteeringReceiptState.OutcomeUnknown } && fact.Request is null && fact.Accepted is not null:
                    result = result with
                    {
                        Receipt = new(result.Receipt.Request,
                        fact.Accepted.Value ? SteeringReceiptState.TransportAcknowledged : SteeringReceiptState.Rejected, fact.Receipt, fact.Reason)
                    };
                    break;
                default:
                    throw new InvalidOperationException("Unknown or invalid correction journal transition.");
            }
        }
        return result ?? throw new InvalidOperationException("Correction journal has no claim.");
    }

    private void Write(string executionId, Fact fact, bool create)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fact, JsonOptions) + "\n");
        using var stream = new FileStream(JournalPath(executionId), create ? FileMode.CreateNew : FileMode.Open,
            FileAccess.Write, FileShare.Read);
        if (!create) stream.Seek(0, SeekOrigin.End);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private string JournalPath(string executionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        return Path.Combine(_room, "steering", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executionId))) + ".correction.jsonl");
    }

    private T UnderLock<T>(Func<T> action)
    {
        using var guard = ConcurrencyGuard.AcquireRoomEventsWithin(_room, TimeSpan.FromSeconds(10), "execution correction");
        return action();
    }

    private static void Match(CorrectionRequest retained, CorrectionRequest candidate)
    {
        if (retained != candidate with { RequestedUtc = retained.RequestedUtc })
            throw new InvalidOperationException("Correction conflicts with the execution's immutable request.");
    }

    private static void Validate(CorrectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MessageId) || request.MessageId.Length > 128
            || string.IsNullOrWhiteSpace(request.ExecutionId) || string.IsNullOrWhiteSpace(request.PayloadSha256)
            || string.IsNullOrWhiteSpace(request.Adapter) || string.IsNullOrWhiteSpace(request.SessionId)
            || string.IsNullOrWhiteSpace(request.Target) || string.IsNullOrWhiteSpace(request.OsPrincipal)
            || request.ProcessId <= 0 || request.ProcessStartUtc.Kind != DateTimeKind.Utc
            || request.ProcessStartUtc == default || request.RequestedUtc == default)
            throw new ArgumentException("A correction requires a bounded message ID and exact process, session, and target identity.");
    }

    private sealed record Fact(string Kind, CorrectionRequest? Request, bool? Accepted, string? Receipt, string? Reason);
    private sealed record Projection(CorrectionReceipt Receipt, bool Admitted);
}
