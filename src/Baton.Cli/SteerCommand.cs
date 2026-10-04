using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Domain;
using Baton.Status;
using Baton.Steering;
using Baton.Store;
using Baton.Vendors;

namespace Baton.Cli;

public sealed record SteerOptions(string Room, string ExecutionId, string MessageId, string? File, bool Receipt);

public static class SteerOptionsParser
{
    public const string Usage = "Usage: baton steer <room-dir> --execution <id> --message-id <id> " +
        "(--file <text-file> | --receipt)\nExact-running correction boundaries: spec/baton.md §10.";

    public static SteerOptions Parse(string[] args)
    {
        if (args.Length < 1 || args[0].StartsWith('-')) throw new CliArgumentException(Usage);
        string? execution = null, messageId = null, file = null;
        var receipt = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--execution" when i + 1 < args.Length && execution is null: execution = args[++i]; break;
                case "--message-id" when i + 1 < args.Length && messageId is null: messageId = args[++i]; break;
                case "--file" when i + 1 < args.Length && file is null: file = args[++i]; break;
                case "--receipt" when !receipt: receipt = true; break;
                default: throw new CliArgumentException(Usage);
            }
        }
        if (string.IsNullOrWhiteSpace(execution) || string.IsNullOrWhiteSpace(messageId)
            || messageId.Length > 128 || receipt == (file is not null))
            throw new CliArgumentException(Usage);
        return new(args[0], execution, messageId, file, receipt);
    }
}

/// <summary>One-shot exact-execution steering and durable receipt lookup.</summary>
public static class SteerCommand
{
    public static async Task<int> ExecuteAsync(SteerOptions options, TextWriter output,
        CancellationToken cancellationToken = default)
    {
        var room = BatonPaths.RecordKey(options.Room);
        if (new AgyCorrectionStore(room).Query(options.ExecutionId) is not null)
            return await ExecuteAgyAsync(options, output, cancellationToken);
        if (new ExecutionCorrectionStore(room).Query(options.ExecutionId) is not null)
            return await ClaudeCorrectionCommand.ExecuteAsync(options, output, cancellationToken);
        if (!options.Receipt)
        {
            var adapterIdentity = await InspectTargetAsync(room, options.ExecutionId, cancellationToken);
            if (string.Equals(adapterIdentity.Adapter, "claude", StringComparison.OrdinalIgnoreCase))
                return await ClaudeCorrectionCommand.ExecuteAsync(options, output, cancellationToken);
            if (string.Equals(adapterIdentity.Adapter, "agy", StringComparison.OrdinalIgnoreCase))
                return await ExecuteAgyAsync(options, output, cancellationToken);
        }
        var store = new SteeringMessageStore(room);
        var endpoint = CodexSteeringEndpoint.TryRead(room, options.ExecutionId);

        if (options.Receipt)
        {
            var known = store.Query(options.MessageId, exactTurnLive: false);
            var receiptTurnLive = known is not null && Matches(known.Request, endpoint)
                && endpoint is not null
                && await IsExactTurnLiveAsync(room, options.ExecutionId, endpoint, cancellationToken)
                    .ConfigureAwait(false);
            var retained = known is null ? null : store.Query(options.MessageId,
                receiptTurnLive);
            if (retained is null || retained.Request.ExecutionId != options.ExecutionId)
            {
                await WriteAsync(output, new { state = "notFound", messageId = options.MessageId }).ConfigureAwait(false);
                return 1;
            }
            await WriteAsync(output, retained).ConfigureAwait(false);
            return retained.State == SteeringReceiptState.TransportAcknowledged ? 0 : 1;
        }

        var target = await InspectTargetAsync(room, options.ExecutionId, cancellationToken).ConfigureAwait(false);
        var exactTurnLive = target.Live && endpoint is not null && endpoint.IsProcessAlive()
            && target.Pid == (uint)endpoint.BrokerPid
            && target.StartUtc is not null
            && Math.Abs((target.StartUtc.Value - endpoint.BrokerStartTimeUtc).TotalSeconds) <= 1;

        if (!string.Equals(target.Adapter, "codex", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync(output, new
            {
                state = "unsupported",
                executionId = options.ExecutionId,
                adapter = target.Adapter,
                reason = "Local steering supports running Codex broker turns, verified Claude executions, and opted-in AGY executions; this adapter is unsupported."
            })
                .ConfigureAwait(false);
            return 1;
        }
        if (!OperatingSystem.IsWindows())
        {
            await WriteAsync(output, new
            {
                state = "unsupported",
                executionId = options.ExecutionId,
                reason = "Verified local Codex steering ingress is Windows-only in this slice."
            })
                .ConfigureAwait(false);
            return 1;
        }
        var text = await File.ReadAllTextAsync(options.File!, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(text) || text.Length > 16_384)
            throw new CliArgumentException("Steering text must contain 1 to 16384 characters.");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var osPrincipal = OperatingSystem.IsWindows()
            ? WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("OS principal unavailable.")
            : Environment.UserName;
        var knownPrior = store.Query(options.MessageId, exactTurnLive: false);
        var prior = knownPrior is null ? null : store.Query(options.MessageId,
            exactTurnLive && Matches(knownPrior.Request, endpoint));
        if (prior is not null)
        {
            if (prior.Request.ExecutionId != options.ExecutionId || prior.Request.PayloadSha256 != digest
                || prior.Request.OsPrincipal != osPrincipal)
                throw new InvalidOperationException("Steering message ID conflicts with its immutable request.");
            if (prior.State != SteeringReceiptState.Queued)
            {
                await WriteAsync(output, prior).ConfigureAwait(false);
                return prior.State == SteeringReceiptState.TransportAcknowledged ? 0 : 1;
            }
            // A request-only fact has not crossed the native send boundary. The same immutable
            // request may try the same verified live tuple again after a pre-send pipe failure.
        }
        if (!exactTurnLive || endpoint is null)
        {
            await WriteAsync(output, new
            {
                state = "rejected",
                executionId = options.ExecutionId,
                reason = "The exact Codex broker turn is not live."
            }).ConfigureAwait(false);
            return 1;
        }
        var request = new RoomEvent.SteeringRequested(
            MessageId: options.MessageId, ExecutionId: options.ExecutionId, PayloadSha256: digest,
            BrokerIncarnation: endpoint.BrokerIncarnation, ThreadId: endpoint.ThreadId,
            TurnId: endpoint.TurnId, OsPrincipal: osPrincipal, RequestedAtUtc: DateTimeOffset.UtcNow);
        var reserved = store.Reserve(request, exactTurnLive);
        if (reserved.State != SteeringReceiptState.Queued)
        {
            await WriteAsync(output, reserved).ConfigureAwait(false);
            return reserved.State == SteeringReceiptState.TransportAcknowledged ? 0 : 1;
        }

        // The descriptor routes to the broker; it is not a credential. The broker independently
        // checks the full tuple and native turn, and the pipe admits only this OS user.
        try
        {
            using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Impersonation);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            { AutoFlush = true };
            var wire = new CodexSteeringPipeRequest(room, options.ExecutionId, endpoint.BrokerIncarnation,
                endpoint.ThreadId, endpoint.TurnId, options.MessageId, text);
            await writer.WriteLineAsync(JsonSerializer.Serialize(wire)).ConfigureAwait(false);
            using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            responseTimeout.CancelAfter(TimeSpan.FromSeconds(35));
            var line = await ReadBoundedLineAsync(reader, responseTimeout.Token).ConfigureAwait(false);
            if (line is not null)
            {
                var answer = JsonSerializer.Deserialize<CodexSteeringPipeResponse>(line);
                if (answer is not null)
                {
                    // The durable projection wins if an ACK was retained just before disconnect.
                    var retained = store.Query(options.MessageId,
                        await IsExactTurnLiveAsync(room, options.ExecutionId, endpoint, cancellationToken)
                            .ConfigureAwait(false));
                    if (retained is not null)
                    {
                        await WriteAsync(output, retained).ConfigureAwait(false);
                        return retained.State == SteeringReceiptState.TransportAcknowledged ? 0 : 1;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            // A lost pipe is not evidence that the broker did not send the native request.
        }
        var latest = store.Query(options.MessageId,
            await IsExactTurnLiveAsync(room, options.ExecutionId, endpoint, cancellationToken)
                .ConfigureAwait(false))!;
        await WriteAsync(output, latest).ConfigureAwait(false);
        return latest.State == SteeringReceiptState.TransportAcknowledged ? 0 : 1;
    }

    private static async Task<bool> IsExactTurnLiveAsync(string room, string executionId,
        CodexSteeringEndpoint endpoint, CancellationToken cancellationToken)
    {
        var current = CodexSteeringEndpoint.TryRead(room, executionId);
        if (current is null || current != endpoint || !current.IsProcessAlive()) return false;
        try
        {
            var target = await InspectTargetAsync(room, executionId, cancellationToken).ConfigureAwait(false);
            return target.Live && target.Pid == (uint)current.BrokerPid
                && target.StartUtc is not null
                && Math.Abs((target.StartUtc.Value - current.BrokerStartTimeUtc).TotalSeconds) <= 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or FlowEventLogReadException)
        {
            return false;
        }
    }

    private static bool Matches(RoomEvent.SteeringRequested request, CodexSteeringEndpoint? endpoint) =>
        endpoint is not null && request.ExecutionId == endpoint.ExecutionId
        && request.BrokerIncarnation == endpoint.BrokerIncarnation
        && request.ThreadId == endpoint.ThreadId && request.TurnId == endpoint.TurnId;

    internal static async Task<(bool Live, string? Adapter, uint Pid, DateTime? StartUtc)> InspectTargetAsync(
        string room, string executionId, CancellationToken cancellationToken)
    {
        var journal = await new FlowEventLogReader(Path.Combine(room, BatonPaths.FlowLogFileName))
            .ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (journal.HasUnterminatedTail || journal.UnknownEventCount != 0)
            throw new InvalidOperationException("Execution identity cannot be trusted from an incomplete or newer room journal.");
        var bindings = await RoomAdapterLookup.TryLoadBindingsAsync(room, cancellationToken).ConfigureAwait(false);
        var adapters = RoomAdapterLookup.BuildAdapterNameByExecutionId(journal.FlowEvents, bindings);
        adapters.TryGetValue(executionId, out var adapter);
        var started = journal.CoreEvents.OfType<CoreEvent.ExecutionStarted>()
            .LastOrDefault(e => e.ExecutionId.Value == executionId);
        var exited = journal.CoreEvents.OfType<CoreEvent.ExecutionExited>()
            .Any(e => e.ExecutionId.Value == executionId);
        return (started is not null && !exited, adapter, started?.Pid ?? 0, started?.ProcessStartTimeUtc);
    }

    private static async Task<int> ExecuteAgyAsync(SteerOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        var room = BatonPaths.RecordKey(options.Room);
        var journal = await new FlowEventLogReader(Path.Combine(room, BatonPaths.FlowLogFileName)).ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var accepted = journal.FlowEvents.OfType<FlowEvent.ExecutionRequestAccepted>()
            .Where(item => item.Request.ExecutionId.Value == options.ExecutionId).ToArray();
        if (!OperatingSystem.IsWindows() || accepted.Length != 1 || accepted[0].Request.Adapter != "agy"
            || accepted[0].Request.ExactRunningTransport != AgyCorrectionClient.Transport)
        {
            await WriteAsync(output, new
            {
                state = "unsupported",
                executionId = options.ExecutionId,
                reason = "AGY correction requires an opted-in Windows execution; see spec/baton.md §10."
            }).ConfigureAwait(false);
            return 1;
        }
        var text = options.Receipt ? null : await File.ReadAllTextAsync(options.File!, new UTF8Encoding(false, true), cancellationToken).ConfigureAwait(false);
        var receipt = await AgyCorrectionClient.ExecuteAsync(BatonPaths.RecordKey(options.Room), options.ExecutionId,
            options.MessageId, text, cancellationToken).ConfigureAwait(false);
        await WriteAsync(output, receipt is null ? new { state = "notFound", messageId = options.MessageId } : receipt).ConfigureAwait(false);
        return 1;
    }

    private static Task WriteAsync(TextWriter output, object value) =>
        output.WriteLineAsync(JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        }));

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int maxResponseLength = 32_768;
        var result = new StringBuilder();
        var one = new char[1];
        while (result.Length <= maxResponseLength)
        {
            var count = await reader.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return result.Length == 0 ? null : result.ToString();
            if (one[0] == '\n') return result.ToString().TrimEnd('\r');
            result.Append(one[0]);
        }
        throw new IOException("Steering broker response exceeded its bounded frame.");
    }
}
