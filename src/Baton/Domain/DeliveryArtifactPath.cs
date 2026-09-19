using System.Text;
using System.Text.Json;

namespace Baton.Domain;

/// <summary>
/// A repository-relative path Baton generated or requested while preparing one delivery attempt.
/// The provenance is retained so a delivery refusal can identify the owning producer rather than
/// reducing the rule to a global filename blacklist.
/// </summary>
public sealed record DeliveryArtifactPath(string Path, string Provenance);

/// <summary>
/// Durable producer-owned delivery artifacts for one execution attempt. The file lives in the
/// engine-owned output directory, whose identity is already bound to the attempt in the flow ledger.
/// </summary>
public static class DeliveryArtifactLedger
{
    public const string FileName = "delivery-artifacts.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    public static void Append(string outputDirectory, IReadOnlyList<DeliveryArtifactPath> artifacts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(artifacts);
        if (artifacts.Count == 0)
        {
            return;
        }

        var path = Path.Combine(outputDirectory, FileName);
        var existing = ReadFile(path);
        if (existing.Problem is { } problem)
        {
            throw new InvalidDataException(problem);
        }
        var combined = existing.Artifacts.Concat(artifacts)
            .Where(artifact => !string.IsNullOrWhiteSpace(artifact.Path)
                && !string.IsNullOrWhiteSpace(artifact.Provenance))
            .Distinct()
            .ToArray();
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(combined, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static DeliveryArtifactLedgerReading Read(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        return ReadFile(Path.Combine(outputDirectory, FileName));
    }

    private static DeliveryArtifactLedgerReading ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return new([]);
        }

        try
        {
            var artifacts = JsonSerializer.Deserialize<DeliveryArtifactPath[]>(
                File.ReadAllText(path, Encoding.UTF8), JsonOptions) ?? [];
            if (artifacts.Any(artifact => artifact is null
                || string.IsNullOrWhiteSpace(artifact.Path)
                || string.IsNullOrWhiteSpace(artifact.Provenance)))
            {
                return new([], "delivery artifact provenance ledger contains an incomplete artifact");
            }
            return new(artifacts, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new([], $"delivery artifact provenance ledger could not be read: {ex.Message}");
        }
    }
}

public sealed record DeliveryArtifactLedgerReading(
    IReadOnlyList<DeliveryArtifactPath> Artifacts,
    string? Problem = null);
