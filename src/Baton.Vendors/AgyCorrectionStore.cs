using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Concurrency;
using Baton.Core.Internal;
using Baton.Domain;
using Baton.Status;
using Baton.Store;

namespace Baton.Vendors;

public sealed record AgyExecutionIdentity(string Room, string ExecutionId, string Adapter, string ProvenanceSha256,
    int HostPid, DateTime HostStartUtc, int ChildPid, DateTime ChildStartUtc,
    string ConversationId, string Incarnation, string PipeName, string OsPrincipal)
{
    public void Validate()
    {
        if (Adapter != "agy" || Room != BatonPaths.RecordKey(Room) || string.IsNullOrWhiteSpace(ExecutionId)
            || !AgyCorrectionStore.IsDigest(ProvenanceSha256) || HostPid <= 0 || ChildPid <= 0
            || HostStartUtc.Kind != DateTimeKind.Utc || ChildStartUtc.Kind != DateTimeKind.Utc
            || HostStartUtc == default || ChildStartUtc == default || string.IsNullOrWhiteSpace(ConversationId)
            || !Guid.TryParseExact(Incarnation, "N", out _) || PipeName != "baton-agy-" + Incarnation
            || string.IsNullOrWhiteSpace(OsPrincipal))
            throw new InvalidOperationException("Invalid exact AGY execution identity.");
    }

    public bool ProcessesLive() => IsExactProcess(HostPid, HostStartUtc) && IsExactProcess(ChildPid, ChildStartUtc);

    private static bool IsExactProcess(int pid, DateTime birth)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime() == birth;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    public async Task<bool> MatchesJournalAsync(bool requireLive, CancellationToken cancellationToken)
    {
        Validate();
        var journal = await new FlowEventLogReader(Path.Combine(Room, BatonPaths.FlowLogFileName))
            .ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (journal.HasUnterminatedTail || journal.UnknownEventCount != 0) return false;
        using var stream = new FileStream(Path.Combine(Room, BatonPaths.FlowLogFileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            if (!AgyHostDecoder.UniqueProperties(document.RootElement)) return false;
        }
        var accepted = journal.FlowEvents.OfType<FlowEvent.ExecutionRequestAccepted>()
            .Where(item => item.Request.ExecutionId.Value == ExecutionId).ToArray();
        var started = journal.CoreEvents.OfType<CoreEvent.ExecutionStarted>()
            .Where(item => item.ExecutionId.Value == ExecutionId).ToArray();
        if (accepted.Length != 1 || started.Length != 1) return false;
        var request = accepted[0].Request;
        if (request.Adapter != Adapter || request.ExactRunningTransport != AgyStreamingHost.Transport
            || AgyCorrectionStore.Digest(JsonSerializer.Serialize(request)) != ProvenanceSha256
            || accepted[0].EnginePid != HostPid || accepted[0].EngineStartTime?.UtcDateTime != HostStartUtc
            || started[0].Pid != ChildPid || started[0].ProcessStartTimeUtc != ChildStartUtc) return false;
        return !requireLive || (ProcessesLive()
            && !journal.CoreEvents.OfType<CoreEvent.ExecutionExited>().Any(item => item.ExecutionId.Value == ExecutionId)
            && !journal.FlowEvents.OfType<FlowEvent.StepRebound>().Any(item => item.ForExecutionId.Value == ExecutionId));
    }
}

public sealed record AgyCorrectionRequest(AgyExecutionIdentity Identity, string MessageId, string PayloadSha256);
public sealed record AgyCorrectionReceipt(AgyCorrectionRequest Request, string State, bool SendStarted,
    string? InputEvidence = null, string? ConsumptionEvidence = null);

/// <summary>Strict AGY-only write-ahead facts under the existing cross-process room lock.</summary>
public sealed class AgyCorrectionStore(string room)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };
    private readonly string _room = BatonPaths.RecordKey(room);

    internal static string Digest(string text) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(text)));
    internal static bool IsDigest(string digest) => digest is { Length: 64 } && digest.All(char.IsAsciiHexDigit);
    internal static string PathFor(string room, string execution, string suffix) => Path.Combine(room, "steering", Digest(execution) + ".agy-" + suffix);

    public AgyCorrectionReceipt? Query(string execution) => UnderLock(() => Read(execution));

    public bool TryClaim(AgyCorrectionRequest request) => UnderLock(() =>
    {
        Validate(request);
        if (Read(request.Identity.ExecutionId) is { } prior)
        {
            Match(prior.Request, request);
            return false;
        }
        Write(request.Identity.ExecutionId, new("claimed", request, null), true);
        return true;
    });

    public bool TryStartSend(AgyCorrectionRequest request) => UnderLock(() =>
    {
        var prior = Read(request.Identity.ExecutionId) ?? throw new InvalidOperationException("AGY send has no claim.");
        Match(prior.Request, request);
        if (prior.SendStarted) return false;
        Write(request.Identity.ExecutionId, new("send-started", null, null), false);
        return true;
    });

    public void RecordEvidence(AgyCorrectionRequest request, string kind, string value) => UnderLock(() =>
    {
        var prior = Read(request.Identity.ExecutionId) ?? throw new InvalidOperationException("AGY evidence has no claim.");
        Match(prior.Request, request);
        if (!prior.SendStarted || kind is not ("input" or "consumption") || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Invalid descriptive AGY evidence.");
        var retained = kind == "input" ? prior.InputEvidence : prior.ConsumptionEvidence;
        if (retained is not null)
        {
            if (retained != value) throw new InvalidOperationException("Conflicting AGY evidence.");
            return false;
        }
        Write(request.Identity.ExecutionId, new(kind, null, value), false);
        return true;
    });

    private AgyCorrectionReceipt? Read(string execution)
    {
        string text;
        try { text = File.ReadAllText(PathFor(_room, execution, "correction.jsonl"), new UTF8Encoding(false, true)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (DecoderFallbackException ex) { throw new InvalidOperationException("Invalid AGY correction journal encoding.", ex); }
        if (text.Length == 0 || !text.EndsWith('\n')) throw new InvalidOperationException("Incomplete AGY correction journal.");
        AgyCorrectionReceipt? result = null;
        foreach (var line in text[..^1].Split('\n'))
        {
            Fact fact;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (!AgyHostDecoder.UniqueProperties(document.RootElement)) throw new JsonException();
                fact = JsonSerializer.Deserialize<Fact>(line, JsonOptions) ?? throw new JsonException();
            }
            catch (JsonException ex) { throw new InvalidOperationException("Corrupt AGY correction journal.", ex); }
            switch (fact.Kind)
            {
                case "claimed" when result is null && fact.Request is not null && fact.Evidence is null:
                    Validate(fact.Request);
                    if (fact.Request.Identity.ExecutionId != execution) throw new InvalidOperationException("AGY execution mismatch.");
                    result = new(fact.Request, "claimedBeforeSend", false);
                    break;
                case "send-started" when result is { SendStarted: false } && fact.Request is null && fact.Evidence is null:
                    result = result with { SendStarted = true, State = "outcomeUnknown" };
                    break;
                case "input" when result is { SendStarted: true, InputEvidence: null } && fact.Request is null && !string.IsNullOrWhiteSpace(fact.Evidence):
                    result = result with { InputEvidence = fact.Evidence };
                    break;
                case "consumption" when result is { SendStarted: true, ConsumptionEvidence: null } && fact.Request is null && !string.IsNullOrWhiteSpace(fact.Evidence):
                    result = result with { ConsumptionEvidence = fact.Evidence };
                    break;
                default: throw new InvalidOperationException("Invalid AGY correction transition.");
            }
        }
        return result ?? throw new InvalidOperationException("Missing AGY correction claim.");
    }

    private void Validate(AgyCorrectionRequest request)
    {
        request.Identity.Validate();
        if (request.Identity.Room != _room || string.IsNullOrWhiteSpace(request.MessageId)
            || request.MessageId.Length > 128 || !IsDigest(request.PayloadSha256))
            throw new InvalidOperationException("Invalid AGY correction request.");
    }

    private static void Match(AgyCorrectionRequest prior, AgyCorrectionRequest request)
    {
        if (prior != request) throw new InvalidOperationException("AGY correction conflicts with the immutable request.");
    }

    private void Write(string execution, Fact fact, bool create)
    {
        Directory.CreateDirectory(Path.Combine(_room, "steering"));
        using var stream = new FileStream(PathFor(_room, execution, "correction.jsonl"),
            create ? FileMode.CreateNew : FileMode.Open, FileAccess.Write, FileShare.Read);
        if (!create) stream.Seek(0, SeekOrigin.End);
        stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fact, JsonOptions) + "\n"));
        stream.Flush(true);
    }

    private T UnderLock<T>(Func<T> action)
    {
        using var guard = ConcurrencyGuard.AcquireRoomEventsWithin(_room, TimeSpan.FromSeconds(10), "AGY correction");
        return action();
    }

    private sealed record Fact(string Kind, AgyCorrectionRequest? Request, string? Evidence);
}
