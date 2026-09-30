using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Steering;
using Baton.Outcomes;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

public static class ClaudeCorrectionCommand
{
    public static async Task<int> ExecuteAsync(SteerOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        var room = BatonPaths.RecordKey(options.Room);
        var store = new ExecutionCorrectionStore(room);
        var prior = store.Query(options.ExecutionId);
        if (options.Receipt)
            return await WriteAsync(output, prior?.Request.MessageId == options.MessageId ? prior : null);
        if (!OperatingSystem.IsWindows()) throw new CliArgumentException("Claude correction is Windows-only.");
        var principal = Principal();
        var text = await File.ReadAllTextAsync(options.File!, cancellationToken);
        if (string.IsNullOrEmpty(text) || text.Length > 16_384)
            throw new CliArgumentException("Steering text must contain 1 to 16384 characters.");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (prior is not null)
        {
            if (prior.Request.MessageId != options.MessageId || prior.Request.PayloadSha256 != digest
                || prior.Request.OsPrincipal != principal)
                throw new InvalidOperationException("This execution already has a different immutable correction. Only one is permitted.");
            return await WriteAsync(output, prior);
        }
        var identity = await SteerCommand.InspectTargetAsync(room, options.ExecutionId, cancellationToken);
        var endpoint = ClaudeCorrectionEndpoint.ReadVerified(room, options.ExecutionId, principal);
        if (endpoint is null || !identity.Live || identity.Adapter != "claude" || identity.StartUtc is null
            || EngineLivenessProbe.Probe((int)identity.Pid, identity.StartUtc.Value).Status != EngineLivenessStatus.Alive)
            throw new InvalidOperationException("The exact Claude execution has no verified live inbox. Nothing was sent.");
        var request = new CorrectionRequest(options.MessageId, options.ExecutionId, digest, "claude", endpoint.SessionId,
            endpoint.Target, (int)identity.Pid, identity.StartUtc.Value, principal, DateTimeOffset.UtcNow);
        if (store.TryClaim(request))
            await ClaudeCorrectionSender.SendAsync(room, request, text, cancellationToken);
        return await WriteAsync(output, store.Query(options.ExecutionId));
    }

    public static int PublishAddress(TextReader input, TextWriter error)
    {
        try
        {
            using var document = JsonDocument.Parse(input.ReadToEnd());
            ClaudeCorrectionEndpoint.PublishAddress(
                Environment.GetEnvironmentVariable("BATON_OUTPUT_DIR") ?? "",
                document.RootElement.GetProperty("session_id").GetString() ?? "",
                Environment.GetEnvironmentVariable("CLAUDE_CODE_MESSAGING_SOCKET") ?? "", Principal());
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error.WriteLine($"Claude correction is unavailable: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> GuardAsync(string contractPath, TextReader input, TextWriter output)
    {
        var allowed = false;
        try
        {
            var contract = JsonSerializer.Deserialize<ClaudeCorrectionContract>(await File.ReadAllTextAsync(contractPath))
                ?? throw new InvalidOperationException("Missing correction contract.");
            using var document = JsonDocument.Parse(await input.ReadToEndAsync());
            var request = contract.Request;
            var identity = await SteerCommand.InspectTargetAsync(contract.Room, request.ExecutionId, CancellationToken.None);
            var endpoint = ClaudeCorrectionEndpoint.ReadVerified(contract.Room, request.ExecutionId, Principal());
            allowed = identity.Live && identity.Adapter == "claude" && identity.Pid == request.ProcessId
                && identity.StartUtc == request.ProcessStartUtc && request.OsPrincipal == Principal()
                && endpoint?.SessionId == request.SessionId && endpoint.Target == request.Target
                && EngineLivenessProbe.Probe(request.ProcessId, request.ProcessStartUtc).Status == EngineLivenessStatus.Alive
                && ClaudeCorrectionSender.MatchesTool(document.RootElement, request, contract.Text)
                && new ExecutionCorrectionStore(contract.Room).TryAdmitTool(request);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Correction tool denied: {ex.Message}");
        }
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            hookSpecificOutput = new
            {
                hookEventName = "PreToolUse",
                permissionDecision = allowed ? "allow" : "deny",
                permissionDecisionReason = "Only one exact correction to the verified running execution is allowed."
            },
        }));
        return allowed ? 0 : 2;
    }

    private static string Principal() => OperatingSystem.IsWindows()
        ? WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("OS principal unavailable.")
        : throw new PlatformNotSupportedException();

    private static async Task<int> WriteAsync(TextWriter output, CorrectionReceipt? receipt)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize<object>(receipt is null ? new { state = "notFound" } : receipt,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            }));
        return receipt?.State == SteeringReceiptState.TransportAcknowledged ? 0 : 1;
    }
}
