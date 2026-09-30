using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baton.Artifacts;

namespace Baton.Vendors;

/// <summary>Non-secret native routing identity, independent of the worker-writable outbox.</summary>
public sealed record ClaudeCorrectionEndpoint(string SessionId, string Target, string OsPrincipal)
{
    public static void PublishAddress(string output, string sessionId, string socket, string principal)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(principal)
            || !socket.StartsWith(@"\\.\pipe\LOCAL\cc-msg-", StringComparison.Ordinal)
            || socket.Length > 512 || socket.Any(char.IsControl))
            throw new ArgumentException("A native Claude inbox, session, and OS owner are required.");
        var (room, execution) = Locate(output);
        WriteOnce(PathFor(room, execution, "address"), new ClaudeCorrectionEndpoint(sessionId, "uds:" + socket, principal));
    }

    /// <summary>The dispatcher feeds actual process stdout here, never a worker-writable capture file.</summary>
    public static Action<string> CreateObserver(string output)
    {
        var (room, execution) = Locate(output);
        var observed = false;
        return line =>
        {
            if (observed) return;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "system"
                    || !root.TryGetProperty("subtype", out var subtype) || subtype.ValueKind != JsonValueKind.String || subtype.GetString() != "init"
                    || !root.TryGetProperty("session_id", out var session)
                    || session.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(session.GetString())) return;
                observed = true;
                WriteOnce(PathFor(room, execution, "observed"), session.GetString());
            }
            catch (JsonException) { /* Non-JSON stdout is not native identity evidence. */ }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                observed = true;
                Console.Error.WriteLine($"Claude correction identity unavailable: {ex.Message}");
            }
        };
    }

    public static ClaudeCorrectionEndpoint? ReadVerified(string room, string execution, string principal)
    {
        try
        {
            var address = JsonSerializer.Deserialize<ClaudeCorrectionEndpoint>(File.ReadAllText(PathFor(room, execution, "address")));
            var observed = JsonSerializer.Deserialize<string>(File.ReadAllText(PathFor(room, execution, "observed")));
            return address is not null && !string.IsNullOrWhiteSpace(observed)
                && address.OsPrincipal == principal && address.SessionId == observed
                && address.Target is { Length: <= 516 } && !address.Target.Any(char.IsControl)
                && address.Target.StartsWith(@"uds:\\.\pipe\LOCAL\cc-msg-", StringComparison.Ordinal)
                ? address : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static string PathFor(string room, string execution, string suffix) => Path.Combine(room, "steering",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(execution))) + ".claude-" + suffix + ".json");

    private static (string Room, string Execution) Locate(string output)
    {
        if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("Absolute execution outbox required.");
        var directory = new DirectoryInfo(output);
        if (directory.Parent?.Name != ArtifactManager.ArtifactsDirectoryName || directory.Parent.Parent is null
            || !directory.Name.StartsWith("execution_", StringComparison.Ordinal) || directory.Name.Length <= 10)
            throw new ArgumentException("Expected an exact Baton execution outbox.");
        return (directory.Parent.Parent.FullName, directory.Name[10..]);
    }

    private static void WriteOnce<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A torn publication stays unusable, never overwritten with another execution's identity.
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, value);
        stream.Flush(flushToDisk: true);
    }
}
