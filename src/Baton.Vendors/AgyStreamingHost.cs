using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Baton.Core.Internal;
using Baton.Dispatch;

namespace Baton.Vendors;

public sealed record AgyCorrectionWireRequest(AgyExecutionIdentity Identity, string MessageId, string Text);

/// <summary>The vendor's streaming stdin owner inside Core's original process tree and clock.</summary>
internal sealed class AgyStreamingHost(WorkerProcessTransportContext context) : IWorkerProcessTransport
{
    public const string Transport = AgyCorrectionClient.Transport;
    private const int MaxLine = 1_048_576;
    private readonly object _gate = new();
    private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AgyCorrectionStore _store = new(context.Room);
    private readonly Dictionary<string, string> _steps = new(StringComparer.Ordinal);
    private readonly AgyHostUsage _usage = new();
    private readonly string _incarnation = Guid.NewGuid().ToString("N");
    private Process? _child;
    private StreamWriter? _input;
    private AgyExecutionIdentity? _identity;
    private AgyCorrectionRequest? _correction;
    private NamedPipeServerStream? _listener;
    private Task? _acceptTask;
    private Task? _firstSend;
    private int _expected = 1;
    private int _results;
    private int _userInputs;
    private bool _valid = true;
    private bool _captureIntegrityValid = true;
    private bool _closed;
    private bool _finished;
    private JsonElement? _finalResult;

    public FinalExpectedTurnCompletion? Completion { get; private set; }

    public void Start(Process child)
    {
        _child = child;
        _input = child.StandardInput;
        // Capture starts immediately; the original runner has already armed cancellation and timeout.
        _firstSend = Task.Run(() =>
        {
            lock (_gate)
            {
                try { Send(context.Prompt); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                { Invalidate(); Console.Error.WriteLine($"AGY first input failed: {ex.Message}"); }
            }
        });
    }

    private void Send(string text)
    {
        _input!.WriteLine(JsonSerializer.Serialize(new { @event = "user", message = new { content = text } }));
        _input.Flush();
    }

    public byte[] Capture(byte[] nativeBytes)
    {
        lock (_gate)
        {
            var output = new StringBuilder();
            try
            {
                var chars = new char[Encoding.UTF8.GetMaxCharCount(nativeBytes.Length)];
                var count = _decoder.GetChars(nativeBytes, chars, false);
                foreach (var c in chars.AsSpan(0, count))
                {
                    if (c == '\n')
                    {
                        Observe(_line.ToString().TrimEnd('\r'), output);
                        _line.Clear();
                    }
                    else if (_line.Length < MaxLine) _line.Append(c);
                    else { _valid = false; _captureIntegrityValid = false; }
                }
            }
            catch (DecoderFallbackException) { _valid = false; _captureIntegrityValid = false; }
            return Encoding.UTF8.GetBytes(output.ToString());
        }
    }

    private void Observe(string line, StringBuilder output)
    {
        JsonElement native;
        try
        {
            using var document = JsonDocument.Parse(line);
            native = document.RootElement.Clone();
            if (native.ValueKind != JsonValueKind.Object || !AgyHostDecoder.UniqueProperties(native) || !native.TryGetProperty("event", out var eventName)
                || eventName.ValueKind != JsonValueKind.String) { Invalidate(); return; }
            var kind = eventName.GetString();
            if (kind == "init")
            {
                if (_identity is not null || !native.TryGetProperty("conversation_id", out var conversation)
                    || conversation.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(conversation.GetString()))
                { Invalidate(); return; }
                using var host = Process.GetCurrentProcess();
                _identity = new(Baton.Status.BatonPaths.RecordKey(context.Room), context.Request.ExecutionId.Value, "agy",
                    AgyCorrectionStore.Digest(JsonSerializer.Serialize(context.Request)), host.Id, host.StartTime.ToUniversalTime(),
                    _child!.Id, _child.StartTime.ToUniversalTime(), conversation.GetString()!, _incarnation,
                    "baton-agy-" + _incarnation, LocalNamedPipePeer.CurrentUserSid() ?? "");
                _identity.Validate();
                _listener = LocalNamedPipePeer.CreateServer(_identity.PipeName);
                Publish(_identity);
                _acceptTask = AcceptAsync(_listener);
            }
            else if (_identity is null) Invalidate();

            foreach (var node in new[] { native, Property(native, "step_update"), Property(native, "result") })
            {
                if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("conversation_id", out var conversation)
                    && (conversation.ValueKind != JsonValueKind.String || conversation.GetString() != _identity?.ConversationId))
                    Invalidate();
            }

            if (!AgyHostUsage.TryReadTerminalStepIdentity(native, out _))
            {
                _valid = false;
                return;
            }

            var step = Property(native, "step_update");
            if (kind == "step_update" && step.ValueKind == JsonValueKind.Object)
            {
                var state = StringProperty(step, "state");
                var type = StringProperty(step, "step_type");
                if (state is "DONE" or "ERROR")
                {
                    var index = step.GetProperty("step_index");
                    var key = _identity?.ConversationId + ":" + index;
                    var hash = AgyCorrectionStore.Digest(step.GetRawText());
                    if (_steps.TryGetValue(key, out var prior))
                    {
                        if (prior != hash) Invalidate();
                        return; // identical terminal restatements feed neither monitor nor tally again
                    }
                    _steps.Add(key, hash);
                    if (type == "user_input" && state == "DONE")
                    {
                        _userInputs++;
                        if (_userInputs > _expected) Invalidate();
                        if (_userInputs == 2 && _correction is not null)
                            _store.RecordEvidence(_correction, "consumption", "native DONE user_input in the bound conversation; payload not acknowledged");
                    }
                }
            }
            if (!_usage.Admit(native)) { Invalidate(); return; }
            if (kind == "result")
            {
                ReconcileClaim();
                _results++;
                var result = Property(native, "result");
                if (_results > _expected || result.ValueKind != JsonValueKind.Object
                    || StringProperty(result, "conversation_id") != _identity?.ConversationId
                    || StringProperty(result, "status") is null) Invalidate();
                if (_results == _expected)
                {
                    _finalResult = native;
                    CloseInput();
                }
            }
            output.AppendLine(JsonSerializer.Serialize(new AgyHostEnvelope(1, "native", context.Request.ExecutionId.Value,
                _incarnation, Math.Min(_results + (kind == "result" ? 0 : 1), _expected), native, null)));
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { _valid = false; _captureIntegrityValid = false; Console.Error.WriteLine($"AGY host evidence unavailable: {ex.Message}"); }
    }

    public byte[] Finish()
    {
        lock (_gate)
        {
            if (_finished) throw new InvalidOperationException("AGY host already finished.");
            _finished = true;
            var output = new StringBuilder();
            ReconcileClaim();
            try
            {
                var remainder = new char[4];
                if (_decoder.GetChars([], remainder, true) != 0) Invalidate();
            }
            catch (DecoderFallbackException) { _valid = false; _captureIntegrityValid = false; }
            if (_line.Length > 0) Observe(_line.ToString().TrimEnd('\r'), output);
            if (_identity is { } identity)
                Completion = new(identity.ExecutionId, Transport, identity.Incarnation, identity.ChildPid,
                    identity.ChildStartUtc, identity.ConversationId, _expected, _results,
                    _finalResult is { } final && StringProperty(Property(final, "result"), "status") == "SUCCESS",
                    _valid && _results == _expected && _userInputs == _expected, CaptureIntegrityValid: _captureIntegrityValid);
            CloseInput();
            output.AppendLine(JsonSerializer.Serialize(new AgyHostEnvelope(1, "completion", context.Request.ExecutionId.Value,
                _incarnation, _expected, _finalResult, Completion)));
            return Encoding.UTF8.GetBytes(output.ToString());
        }
    }

    private async Task AcceptAsync(NamedPipeServerStream first)
    {
        var listener = first;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await using (listener)
                {
                    await listener.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                    try
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(5));
                        using var reader = new StreamReader(listener, new UTF8Encoding(false, true), leaveOpen: true);
                        var line = await ReadFrameAsync(reader, deadline.Token).ConfigureAwait(false);
                        using var frame = JsonDocument.Parse(line);
                        if (!AgyHostDecoder.UniqueProperties(frame.RootElement))
                            throw new InvalidOperationException("Duplicate AGY correction fields.");
                        var request = JsonSerializer.Deserialize<AgyCorrectionWireRequest>(line, AgyCorrectionStore.JsonOptions)
                            ?? throw new InvalidOperationException("Missing AGY correction frame.");
                        if (!LocalNamedPipePeer.IsSameUser(listener, _identity!.OsPrincipal))
                            throw new InvalidOperationException("AGY correction peer identity refused.");
                        var receipt = await AdmitAsync(request, deadline.Token).ConfigureAwait(false);
                        await listener.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receipt) + "\n"), deadline.Token).ConfigureAwait(false);
                        await listener.FlushAsync(deadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException
                        or JsonException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    {
                        // A refused or disconnected caller owns only this connection, never the endpoint lifetime.
                        if (!_lifetime.IsCancellationRequested) Console.Error.WriteLine($"AGY correction request refused: {ex.Message}");
                    }
                }
                if (!_lifetime.IsCancellationRequested) listener = LocalNamedPipePeer.CreateServer(_identity!.PipeName);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException
            or JsonException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (!_lifetime.IsCancellationRequested) Console.Error.WriteLine($"AGY correction endpoint stopped: {ex.Message}");
        }
        finally { listener.Dispose(); }
    }

    internal async Task<AgyCorrectionReceipt> AdmitAsync(AgyCorrectionWireRequest wire, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(wire.Text) || wire.Text.Length > 16_384)
            throw new InvalidOperationException("AGY correction text must contain 1 to 16384 characters.");
        var request = new AgyCorrectionRequest(wire.Identity, wire.MessageId, AgyCorrectionStore.Digest(wire.Text));
        if (wire.Identity != _identity || !await wire.Identity.MatchesJournalAsync(true, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The exact AGY execution identity is not live.");
        lock (_gate)
        {
            if (ReadEndpoint(context.Room, context.Request.ExecutionId.Value) != wire.Identity)
                throw new InvalidOperationException("AGY endpoint incarnation changed.");
            if (_store.Query(wire.Identity.ExecutionId) is { } retained)
            {
                ReconcileClaim();
                if (retained.Request != request) throw new InvalidOperationException("AGY correction conflicts with the immutable request.");
                return retained; // even a claimed-before-send record never authorizes another send
            }
            if (_closed || _results != 0 || _expected != 1 || !_valid)
                throw new InvalidOperationException("The first AGY result already won correction admission.");
            // The same serialization gate covers admission and observation of the first result.
            if (!wire.Identity.MatchesJournalAsync(true, cancellationToken).GetAwaiter().GetResult())
                throw new InvalidOperationException("AGY identity changed before claim.");
            // A partial durable claim or flush failure must never let turn one satisfy completion.
            _expected = 2;
            _correction = request;
            try
            {
                if (!_store.TryClaim(request)) return _store.Query(wire.Identity.ExecutionId)!;
            }
            catch { Invalidate(); throw; }
            if (ReadEndpoint(context.Room, wire.Identity.ExecutionId) != wire.Identity
                || !wire.Identity.MatchesJournalAsync(true, cancellationToken).GetAwaiter().GetResult())
                throw new InvalidOperationException("AGY identity changed before send.");
            if (_store.TryStartSend(request))
            {
                try
                {
                    Send(wire.Text);
                    _store.RecordEvidence(request, "input", "native user message written and flushed; payload not acknowledged");
                }
                catch (IOException) { Invalidate(); }
            }
            return _store.Query(wire.Identity.ExecutionId)!;
        }
    }

    private void ReconcileClaim()
    {
        try
        {
            if (_store.Query(context.Request.ExecutionId.Value) is not { } retained) return;
            _expected = 2;
            if (retained.Request.Identity != _identity) { Invalidate(); return; }
            _correction = retained.Request;
            if (!retained.SendStarted) Invalidate();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { _expected = 2; Invalidate(); }
    }

    private void CloseInput()
    {
        _closed = true;
        _lifetime.Cancel();
        _input?.Close();
        if (_identity is not null)
        {
            var path = AgyCorrectionStore.PathFor(context.Room, context.Request.ExecutionId.Value, "endpoint.json");
            if (ReadEndpoint(context.Room, context.Request.ExecutionId.Value) == _identity) File.Delete(path);
        }
    }

    private static void Publish(AgyExecutionIdentity identity)
    {
        Directory.CreateDirectory(Path.Combine(identity.Room, "steering"));
        var path = AgyCorrectionStore.PathFor(identity.Room, identity.ExecutionId, "endpoint.json");
        var temporary = path + "." + identity.Incarnation + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, identity, AgyCorrectionStore.JsonOptions);
            stream.Flush(true);
        }
        File.Move(temporary, path);
    }

    public static AgyExecutionIdentity? ReadEndpoint(string room, string execution)
    {
        try
        {
            using var stream = new FileStream(AgyCorrectionStore.PathFor(room, execution, "endpoint.json"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (!AgyHostDecoder.UniqueProperties(document.RootElement)) throw new InvalidOperationException("Duplicate AGY endpoint fields.");
            var identity = document.RootElement.Deserialize<AgyExecutionIdentity>(AgyCorrectionStore.JsonOptions);
            identity?.Validate();
            return identity?.Room == Baton.Status.BatonPaths.RecordKey(room) && identity.ExecutionId == execution ? identity : null;
        }
        catch (IOException) { return null; } // unavailable publication/teardown is never endpoint authority
    }

    public static async Task<string> ReadFrameAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var one = new char[1];
        while (result.Length <= 131_072)
        {
            if (await reader.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false) == 0)
                throw new IOException("Incomplete AGY correction frame.");
            if (one[0] == '\n') return result.ToString().TrimEnd('\r');
            result.Append(one[0]);
        }
        throw new IOException("AGY correction frame exceeds its bound.");
    }

    private static JsonElement Property(JsonElement node, string name) => node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty(name, out var value) ? value : default;
    private static string? StringProperty(JsonElement node, string name) => Property(node, name) is { ValueKind: JsonValueKind.String } value
        ? value.GetString() : null;

    private void Invalidate()
    {
        _valid = false;
        _captureIntegrityValid = false;
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _listener?.Dispose();
        // The runner has already reaped the native tree, so any blocked stdin write now settles.
        _firstSend?.GetAwaiter().GetResult();
        _acceptTask?.GetAwaiter().GetResult();
        _lifetime.Dispose();
    }
}
