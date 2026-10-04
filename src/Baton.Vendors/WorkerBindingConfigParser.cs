using System.Text.Json;
using Baton.Domain;

namespace Baton.Vendors;

/// <summary>
/// Loads a worker-binding config from a file (M11 Phase 1's open question: "where worker-binding
/// config lives" — a run-time sidecar, not the frozen workflow template).
/// <para>
/// <b>File format convention:</b> a single JSON object keyed by worker role name, each value a
/// <see cref="WorkerBindingConfigEntry"/> — deserialized through the same <see cref="JsonSerializer"/>
/// defaults <c>Baton.Templates.WorkflowDefinitionParser</c> uses for templates (case-sensitive,
/// PascalCase property names matching the record shapes exactly, no custom naming policy).
/// </para>
/// </summary>
public static class WorkerBindingConfigParser
{
    /// <summary>Parses a worker-binding config from a JSON string.</summary>
    /// <param name="json">The config document.</param>
    /// <param name="sourcePath">
    /// Same contract as <see cref="Baton.Templates.WorkflowDefinitionParser.Parse"/>'s
    /// <c>sourcePath</c> (#562).
    /// </param>
    /// <exception cref="WorkerBindingConfigException">The JSON is malformed or empty.</exception>
    public static IReadOnlyDictionary<string, WorkerBindingConfigEntry> Parse(string json, string? sourcePath = null)
    {
        var location = sourcePath is null ? string.Empty : $" in '{sourcePath}'";
        Dictionary<string, WorkerBindingConfigEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<Dictionary<string, WorkerBindingConfigEntry>>(json);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            const string shape =
                "A valid worker-binding config looks like: "
                + "{ \"<workerName>\": { \"Adapter\": \"<string>\", \"Contract\": { ... }, "
                + "\"PromptTemplate\": \"<string>\", \"Timeout\": \"<hh:mm:ss>\" } }.";
            throw new WorkerBindingConfigException($"Malformed worker-binding config JSON{location}: {ex.Message} {shape}", ex);
        }

        if (entries is null)
        {
            var fileLocation = sourcePath is null ? string.Empty : $" '{sourcePath}'";
            throw new WorkerBindingConfigException($"Worker-binding config file{fileLocation} did not contain a JSON object.");
        }

        foreach (var (workerName, entry) in entries)
        {
            if (entry is null)
            {
                throw new WorkerBindingConfigException($"Worker-binding config entry for '{workerName}'{location} is null.");
            }

            if (string.IsNullOrWhiteSpace(entry.Adapter))
            {
                throw new WorkerBindingConfigException($"Worker-binding config entry for '{workerName}'{location} is missing 'Adapter'.");
            }

            AttachmentReadInputs.ValidateNames(entry.AttachmentNames);

            if (entry.EnableAgyCorrection && (entry.Adapter != "agy" || !OperatingSystem.IsWindows()))
                throw new WorkerBindingConfigException("EnableAgyCorrection requires the Windows AGY adapter.");

            if (entry.Contract is null)
            {
                throw new WorkerBindingConfigException($"Worker-binding config entry for '{workerName}'{location} is missing 'Contract'.");
            }

            if (entry.Contract.ProducedOutputs is not null)
            {
                foreach (var output in entry.Contract.ProducedOutputs)
                {
                    if (ReservedOutputNames.IsReserved(output.Name))
                    {
                        throw new WorkerBindingConfigException(
                            $"Worker-binding config entry for '{workerName}'{location} declares ProducedOutput '{output.Name}' — "
                            + $"{ReservedOutputNames.RejectionClause}.");
                    }

                    if (ReservedOutputNames.IsPathTraversal(output.Name))
                    {
                        throw new WorkerBindingConfigException(
                            $"Worker-binding config entry for '{workerName}'{location} declares ProducedOutput '{output.Name}' — "
                            + $"{ReservedOutputNames.PathTraversalRejectionClause}.");
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(entry.PromptTemplate))
            {
                throw new WorkerBindingConfigException($"Worker-binding config entry for '{workerName}'{location} is missing 'PromptTemplate'.");
            }

            if (entry.WorkingDirectory is not null && string.IsNullOrWhiteSpace(entry.WorkingDirectory))
            {
                throw new WorkerBindingConfigException(
                    $"Worker-binding config entry for '{workerName}'{location} has a blank 'WorkingDirectory' — omit the field entirely instead.");
            }

            // A non-positive Timeout is not a slow worker, it is an unrunnable one, and nothing
            // downstream treats it as an error: it reaches BatonTask.WithTimeout as
            // Duration::from_millis(0), whose monitor thread kills the process tree immediately. An
            // *omitted* Timeout deserializes to default(TimeSpan) and lands in the same place, so the
            // most likely way to hit this is forgetting the field rather than typing a silly value.
            // Rejecting here also bounds what AgyWorkerAdapter's --print-timeout can be derived
            // from (#588): a negative timeout would otherwise floor that flag at 1s while AER's own
            // limit misbehaves, inverting the very ordering that flag exists to establish.
            if (entry.Timeout <= TimeSpan.Zero)
            {
                throw new WorkerBindingConfigException(
                    $"Worker-binding config entry for '{workerName}'{location} has a 'Timeout' of "
                    + $"'{entry.Timeout}' — it must be positive. Omitting the field leaves it zero, "
                    + "which would kill the worker the moment it starts.");
            }

            // #802: the self-fallback refusal FallbackBinding's own doc names -- enforced here,
            // at parse time, rather than left to silently loop back onto the vendor it was declared
            // to escape.
            if (entry.FallbackOnExhaustion is { } fallback
                && string.Equals(fallback.Adapter, entry.Adapter, StringComparison.Ordinal))
            {
                throw new WorkerBindingConfigException(
                    $"Worker-binding config entry for '{workerName}'{location} declares "
                    + $"'FallbackOnExhaustion.Adapter' equal to its own 'Adapter' ('{entry.Adapter}') — "
                    + "a vendor cannot fall back to itself.");
            }
        }

        return entries;
    }

    /// <summary>Reads <paramref name="path"/> and parses it as a worker-binding config.</summary>
    /// <exception cref="WorkerBindingConfigException">
    /// A missing file (or missing parent directory), or malformed/invalid contents. A missing file
    /// surfaces as this typed exception, not the raw <see cref="FileNotFoundException"/> the CLI boundary
    /// cannot catch — the same translation <c>WorkflowDefinitionParser.LoadFromFileAsync</c> documents.
    /// Every command that resumes a room (run/decide/supply/cancel) loads its <c>--bindings</c> through
    /// here without a prior existence check, so this is the single place the missing-file case is caught
    /// for all of them.
    /// </exception>
    public static async Task<IReadOnlyDictionary<string, WorkerBindingConfigEntry>> LoadFromFileAsync(
        string path, CancellationToken cancellationToken = default)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new WorkerBindingConfigException($"Worker-binding config file '{path}' does not exist.", ex);
        }

        return Parse(json, path);
    }
}
