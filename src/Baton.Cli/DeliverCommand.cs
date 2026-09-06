using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Artifacts;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

public sealed record DeliverResult(
    string Title,
    string SourcePath,
    string DestinationPath,
    string Sha256,
    string DeliveredAt);

public sealed record ConductorManifestEntry(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("delivered_at")] string DeliveredAt,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("artifact_file")] string ArtifactFile);

/// <summary>
/// <c>baton deliver</c> (#1669): copies a conductor deliverable into a room's artifacts directory
/// with a manifest entry so pusher.py forwards it to the glass inbox.
/// </summary>
public static class DeliverCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static async Task<DeliverResult> ExecuteAsync(
        DeliverOptions options,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        if (!File.Exists(options.SourceFilePath))
        {
            throw new CliArgumentException($"Source file '{options.SourceFilePath}' does not exist.");
        }

        var sourceFullPath = Path.GetFullPath(options.SourceFilePath);
        var basename = Path.GetFileName(sourceFullPath);

        var roomDir = options.RoomDirectoryPath;
        var conductorArtifactsDir = Path.Combine(roomDir, "artifacts", "conductor");
        Directory.CreateDirectory(conductorArtifactsDir);

        var bindingsPath = BatonPaths.RoomBindingsFile(roomDir);
        if (!File.Exists(bindingsPath))
        {
            const string stubBindings = """
                {
                  "conductor": {
                    "Adapter": "none",
                    "Contract": {
                      "WorkerName": "conductor"
                    },
                    "PromptTemplate": "conductor",
                    "Timeout": "01:00:00"
                  }
                }
                """;
            File.WriteAllText(bindingsPath, stubBindings, Utf8NoBom);
        }

        // Found while fixing #1942, and fixed with it: this registration was the one registry write in
        // the tree that did NOT honour the store's fail-open contract (RoomRegistryStore's own remarks:
        // the registry only ever *adds* fleet_status coverage and must never be the reason a command
        // fails). An IOException here — a sibling process holding the registry lock past the whole wait
        // budget — propagated to Program.cs and failed the whole delivery, so the file the conductor
        // was delivering never got copied. Caught in the same shape RunCommand.RegisterRoomAsync
        // already uses: report on stderr, then deliver anyway.
        try
        {
            await RoomRegistryStore.AppendAsync(
                roomDir,
                BatonPaths.Root,
                BatonPaths.RoomRegistryFile,
                explicitRegister: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Console.Error.WriteLine(
                $"Could not update the room registry at '{BatonPaths.RoomRegistryFile}': {ex.Message}. "
                + "The delivery itself still lands in the room's artifacts.");
        }

        // F1 (2026-09-02 review): the destination filename must be unique per source_path, not just
        // per basename — two sources named 'notes.md' under different projects would otherwise
        // collide on one on-disk file and cross-contaminate each other's manifest entry. Hashed off
        // the source path itself (not its content) so re-delivering the same source with changed
        // bytes keeps landing on the same artifact_file rather than orphaning the old one.
        var sourcePathHashHex = Convert.ToHexStringLower(SHA256.HashData(Utf8NoBom.GetBytes(sourceFullPath)));
        var artifactFile = $"{sourcePathHashHex[..8]}-{basename}";

        var fileBytes = await File.ReadAllBytesAsync(sourceFullPath, cancellationToken).ConfigureAwait(false);
        var sha256Hex = Convert.ToHexStringLower(SHA256.HashData(fileBytes));

        // #496: routed through RoomArtifacts.Write rather than a raw File.Copy(overwrite: true) — a
        // re-delivery of the same source_path (the conductor re-delivering its own updated document)
        // now appends a version instead of silently discarding the prior bytes. No ExecutionId: a
        // conductor delivery is not tied to a flow execution.
        var writeResult = RoomArtifacts.Write(
            roomDir,
            Path.Combine("conductor", artifactFile),
            fileBytes,
            new ArtifactAttribution(ExecutionId: null, Role: "conductor", Adapter: null, Model: null));
        var destFilePath = writeResult.CurrentPath;

        string title;
        if (!string.IsNullOrWhiteSpace(options.Title))
        {
            title = options.Title;
        }
        else
        {
            var text = Utf8NoBom.GetString(fileBytes);
            title = ExtractTitle(text, basename);
        }

        var deliveredAt = DateTime.UtcNow.ToString("O");
        var entry = new ConductorManifestEntry(title, sourceFullPath, deliveredAt, sha256Hex, artifactFile);

        var manifestPath = Path.Combine(conductorArtifactsDir, "manifest.jsonl");
        UpdateManifest(manifestPath, entry);

        output.WriteLine($"Delivered '{title}' -> {destFilePath}");
        return new DeliverResult(title, sourceFullPath, destFilePath, sha256Hex, deliveredAt);
    }

    private static string ExtractTitle(string content, string fallback)
    {
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# ") && trimmed.Length > 2)
            {
                return trimmed[2..].Trim();
            }
        }

        return fallback;
    }

    private static void UpdateManifest(string manifestPath, ConductorManifestEntry entry)
    {
        var entries = new List<ConductorManifestEntry>();
        var replaced = false;

        if (File.Exists(manifestPath))
        {
            var lines = File.ReadAllLines(manifestPath, Utf8NoBom);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var existing = JsonSerializer.Deserialize<ConductorManifestEntry>(line, JsonOptions);
                    if (existing is null || string.IsNullOrWhiteSpace(existing.SourcePath))
                    {
                        continue;
                    }

                    if (string.Equals(existing.SourcePath, entry.SourcePath, StringComparison.OrdinalIgnoreCase))
                    {
                        entries.Add(entry);
                        replaced = true;
                    }
                    else
                    {
                        entries.Add(existing);
                    }
                }
                catch (JsonException)
                {
                    // Skip corrupt lines
                }
            }
        }

        if (!replaced)
        {
            entries.Add(entry);
        }

        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.Append(JsonSerializer.Serialize(e, JsonOptions)).Append('\n');
        }

        var tempPath = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, sb.ToString(), Utf8NoBom);
        File.Move(tempPath, manifestPath, overwrite: true);
    }
}
