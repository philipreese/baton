using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Artifacts;
using Baton.Core.Internal;
using Baton.Outcomes;
using Baton.Status;
using Baton.Steering;

namespace Baton.Vendors;

/// <summary>Routing data for one broker turn. The pipe name and incarnation are not credentials.</summary>
public sealed record CodexSteeringEndpoint(
    string Room, string ExecutionId, string BrokerIncarnation, string ThreadId, string TurnId,
    string PipeName, int BrokerPid, DateTime BrokerStartTimeUtc)
{
    public const string FileName = "steer.endpoint.json";

    public static string PathFor(string room, string executionId) => System.IO.Path.Combine(
        room, ArtifactManager.ArtifactsDirectoryName, $"execution_{executionId}", FileName);

    public static CodexSteeringEndpoint? TryRead(string room, string executionId)
    {
        try
        {
            var path = PathFor(room, executionId);
            var result = JsonSerializer.Deserialize<CodexSteeringEndpoint>(File.ReadAllText(path));
            return result is not null
                && BatonPaths.RecordKeyComparer.Equals(BatonPaths.RecordKey(room), BatonPaths.RecordKey(result.Room))
                && string.Equals(result.ExecutionId, executionId, StringComparison.Ordinal)
                    ? result : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public bool IsProcessAlive() =>
        EngineLivenessProbe.Probe(BrokerPid, BrokerStartTimeUtc).Status == EngineLivenessStatus.Alive;
}

public sealed record CodexSteeringPipeRequest(string Room, string ExecutionId, string BrokerIncarnation,
    string ThreadId, string TurnId, string MessageId, string Text);

public sealed record CodexSteeringPipeResponse(SteeringReceiptState State, string? Receipt = null,
    string? Reason = null);

/// <summary>One local same-user ingress inside the already-running Codex app-server broker.</summary>
internal sealed class CodexSteeringIngress : IAsyncDisposable
{
    private const int MaxTextLength = 16_384;
    private const int MaxRequestLineLength = 131_072;
    private readonly CodexSteeringEndpoint _endpoint;
    private readonly SteeringMessageStore _store;
    private readonly TextWriter _nativeInput;
    private readonly string _serverUserSid;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendAdmission = new(1, 1);
    private readonly object _pendingGate = new();
    private Task? _acceptLoop;
    private Pending? _pending;
    private int _nextRpcId = 1000;
    private volatile bool _active;

    private sealed record Pending(int RpcId, string MessageId,
        TaskCompletionSource<SteeringReceipt> Completion);

    private CodexSteeringIngress(CodexSteeringEndpoint endpoint, SteeringMessageStore store, TextWriter nativeInput,
        string serverUserSid)
    {
        _endpoint = endpoint;
        _store = store;
        _nativeInput = nativeInput;
        _serverUserSid = serverUserSid;
    }

    public static CodexSteeringIngress? Start(string? outputDirectory, string threadId, string turnId,
        TextWriter nativeInput)
    {
        if (outputDirectory is null) return null;
        if (!OperatingSystem.IsWindows()) return null; // The verified peer-identity boundary is Windows-only.
        var output = System.IO.Path.GetFullPath(outputDirectory);
        var artifacts = System.IO.Path.GetDirectoryName(output);
        var room = artifacts is null ? null : System.IO.Path.GetDirectoryName(artifacts);
        var executionName = System.IO.Path.GetFileName(output);
        if (room is null || artifacts is null
            || !string.Equals(System.IO.Path.GetFileName(artifacts), ArtifactManager.ArtifactsDirectoryName,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !executionName.StartsWith("execution_", StringComparison.Ordinal)
            || executionName.Length == "execution_".Length)
        {
            return null;
        }

        var executionId = executionName["execution_".Length..];
        using var process = Process.GetCurrentProcess();
        var endpoint = new CodexSteeringEndpoint(BatonPaths.RecordKey(room), executionId,
            Guid.NewGuid().ToString("N"), threadId, turnId, $"baton-steer-{Guid.NewGuid():N}",
            Environment.ProcessId, process.StartTime.ToUniversalTime());
        // A steering sidecar failure must never change the worker's own result. Construct the
        // listener before publishing a route, and leave no advertised endpoint on failure.
        NamedPipeServerStream? listener = null;
        string? temporaryPath = null;
        try
        {
            listener = NewListener(endpoint.PipeName);
            var serverUserSid = LocalNamedPipePeer.CurrentUserSid()
                ?? throw new InvalidOperationException("Steering server user SID is unavailable.");
            var ingress = new CodexSteeringIngress(endpoint, new SteeringMessageStore(room), nativeInput,
                serverUserSid);
            var descriptorPath = CodexSteeringEndpoint.PathFor(room, executionId);
            temporaryPath = descriptorPath + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(endpoint));
            File.Move(temporaryPath, descriptorPath, overwrite: true);
            ingress._active = true;
            ingress._acceptLoop = ingress.AcceptLoopAsync(listener);
            return ingress;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            listener?.Dispose();
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }
    }

    public async Task<bool> TryHandleNativeResponseAsync(JsonObject message)
    {
        Pending? pending;
        lock (_pendingGate)
        {
            pending = _pending;
        }
        // JSON-RPC IDs are independent in each direction. A server tool call may reuse our ID.
        if (pending is null || message["method"] is not null
            || (message["result"] is null && message["error"] is null) || message["id"] is null
            || !int.TryParse(message["id"]!.ToString(), out var responseId) || responseId != pending.RpcId)
        {
            return false;
        }

        try
        {
            var accepted = message["error"] is null
                && message["result"]?["turnId"]?.GetValue<string>() == _endpoint.TurnId;
            var reason = accepted ? null : message["error"]?["message"]?.GetValue<string>()
                ?? "Native steering response did not acknowledge the exact active turn.";
            var receipt = accepted ? _endpoint.TurnId : null;
            var retained = _store.RecordAnswer(pending.MessageId, accepted, receipt, reason);
            pending.Completion.TrySetResult(retained);
        }
        catch (Exception ex)
        {
            // A vendor ACK that could not be fsynced is never reported as accepted.
            pending.Completion.TrySetException(ex);
        }
        finally
        {
            lock (_pendingGate)
            {
                if (ReferenceEquals(_pending, pending)) _pending = null;
            }
        }
        return true;
    }

    public async Task MarkTerminalAsync()
    {
        _active = false;
        await _sendAdmission.WaitAsync().ConfigureAwait(false);
        try { RemoveOwnDescriptor(); }
        finally { _sendAdmission.Release(); }
    }

    private static NamedPipeServerStream NewListener(string pipeName) =>
        LocalNamedPipePeer.CreateServer(pipeName);

    private async Task AcceptLoopAsync(NamedPipeServerStream firstListener)
    {
        NamedPipeServerStream? listener = firstListener;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                {
                    await using var pipe = listener!;
                    listener = null;
                    try
                    {
                        await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
                        { AutoFlush = true };
                        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                        requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                        var line = await ReadBoundedLineAsync(reader, requestTimeout.Token).ConfigureAwait(false);
                        CodexSteeringPipeResponse response;
                        try
                        {
                            if (!LocalNamedPipePeer.IsSameUser(pipe, _serverUserSid))
                            {
                                response = new(SteeringReceiptState.Rejected,
                                    Reason: "Steering ingress requires a verified local client with the broker user SID.");
                            }
                            else
                            {
                                var request = JsonSerializer.Deserialize<CodexSteeringPipeRequest>(line ?? "");
                                response = request is null
                                    ? new(SteeringReceiptState.Rejected, Reason: "Missing steering request.")
                                    : await HandleRequestAsync(request).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex) when (ex is JsonException or ArgumentException)
                        {
                            response = new(SteeringReceiptState.Rejected, Reason: ex.Message);
                        }
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        // A stalled peer cannot monopolize the one-connection ingress.
                    }
                    catch (InvalidOperationException)
                    {
                        // Overlong/malformed framing or unavailable durable state closes the connection.
                    }
                    catch (IOException) when (!_lifetime.IsCancellationRequested)
                    {
                        // A client may disconnect; the durable room facts answer what happened.
                    }
                }
                if (!_lifetime.IsCancellationRequested) listener = NewListener(_endpoint.PipeName);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _active = false;
            RemoveOwnDescriptor();
        }
        finally
        {
            if (listener is not null) await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<CodexSteeringPipeResponse> HandleRequestAsync(CodexSteeringPipeRequest request)
    {
        if (!_active || string.IsNullOrEmpty(request.Text) || request.Text.Length > MaxTextLength
            || string.IsNullOrWhiteSpace(request.Room) || string.IsNullOrWhiteSpace(request.ExecutionId)
            || string.IsNullOrWhiteSpace(request.BrokerIncarnation)
            || string.IsNullOrWhiteSpace(request.ThreadId) || string.IsNullOrWhiteSpace(request.TurnId)
            || string.IsNullOrWhiteSpace(request.MessageId)
            || !BatonPaths.RecordKeyComparer.Equals(BatonPaths.RecordKey(request.Room), _endpoint.Room)
            || request.ExecutionId != _endpoint.ExecutionId
            || request.BrokerIncarnation != _endpoint.BrokerIncarnation
            || request.ThreadId != _endpoint.ThreadId || request.TurnId != _endpoint.TurnId)
        {
            return new(SteeringReceiptState.Rejected, Reason: "Steering target is stale or differs from this active broker turn.");
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Text)));
        var sendStarted = false;
        TaskCompletionSource<SteeringReceipt>? completion = null;
        try
        {
            await _sendAdmission.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                if (!_active)
                    return new(SteeringReceiptState.Rejected,
                        Reason: "The exact turn ended before this request began native send.");
                lock (_pendingGate)
                {
                    if (_pending is not null)
                        return new(SteeringReceiptState.Queued,
                            Reason: "Another native steering RPC is unresolved; this request has not been sent.");
                }
                if (!_store.TryStartSend(request.MessageId, digest, request.ExecutionId,
                        request.BrokerIncarnation, request.ThreadId, request.TurnId))
                {
                    var prior = _store.Query(request.MessageId, _active)!;
                    return new(prior.State, prior.Receipt, prior.Reason);
                }
                sendStarted = true;
                completion = new TaskCompletionSource<SteeringReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
                var rpcId = Interlocked.Increment(ref _nextRpcId);
                lock (_pendingGate) _pending = new Pending(rpcId, request.MessageId, completion);
                await CodexAppServerBroker.SendAsync(_nativeInput, new JsonObject
                {
                    ["id"] = rpcId,
                    ["method"] = "turn/steer",
                    ["params"] = new JsonObject
                    {
                        ["threadId"] = _endpoint.ThreadId,
                        ["expectedTurnId"] = _endpoint.TurnId,
                        ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = request.Text }),
                    },
                }, _lifetime.Token).ConfigureAwait(false);
            }
            finally
            {
                _sendAdmission.Release();
            }
            var retained = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), _lifetime.Token)
                .ConfigureAwait(false);
            return new(retained.State, retained.Receipt, retained.Reason);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!sendStarted) throw;
            // Send-started was fsynced before the native write. Transport/persistence failures after
            // that point cannot be called a rejection: vendor consumption may already have happened.
            try
            {
                var retained = _store.Query(request.MessageId, _active);
                return retained is null
                    ? new(SteeringReceiptState.OutcomeUnknown, Reason: "Send began but its durable receipt is unavailable.")
                    : new(retained.State, retained.Receipt, retained.Reason);
            }
            catch (Exception queryError) when (queryError is not OutOfMemoryException)
            {
                return new(SteeringReceiptState.OutcomeUnknown,
                    Reason: "Send began but the durable receipt could not be read.");
            }
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var one = new char[1];
        while (result.Length <= MaxRequestLineLength)
        {
            var count = await reader.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return result.Length == 0 ? null : result.ToString();
            if (one[0] == '\n') return result.ToString().TrimEnd('\r');
            result.Append(one[0]);
        }
        throw new InvalidOperationException("Steering request exceeds the bounded pipe frame.");
    }

    private void RemoveOwnDescriptor()
    {
        var path = CodexSteeringEndpoint.PathFor(_endpoint.Room, _endpoint.ExecutionId);
        try
        {
            if (CodexSteeringEndpoint.TryRead(_endpoint.Room, _endpoint.ExecutionId)?.BrokerIncarnation
                == _endpoint.BrokerIncarnation)
            {
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _active = false;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        RemoveOwnDescriptor();
        _lifetime.Dispose();
        _sendAdmission.Dispose();
    }
}
