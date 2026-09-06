using Baton.Domain;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// The seam <c>baton dispatch</c>'s role path and <c>baton redispatch --spec</c>'s amended-spec path
/// both go through (#1576): <c>--attach</c> validation, the spec/grant lint (#1500), the
/// <see cref="RoleDispatch.Materialize"/> call itself, and the post-materialize attachment copy into the
/// room. Before #1576, <c>RedispatchCommand</c> called <see cref="RoleDispatch.Materialize"/> directly,
/// skipping both the lint and <c>--attach</c> entirely — the two commands cannot silently diverge on
/// either guarantee now that both route through here.
/// </summary>
internal static class RoleSpecMaterializer
{
    private const string AttachmentsDirectoryName = "attachments";

    /// <summary>
    /// Validates <c>--attach</c> arguments (every file exists; no two collide on the same destination
    /// file name), runs the spec/grant lint (WARN, never fail — see <see cref="DispatchSpecLinter"/>'s
    /// own class doc), and materializes the role through <see cref="RoleDispatch.Materialize"/>.
    /// </summary>
    public static (WorkflowDefinition Definition, IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings) Materialize(
        WorkerRole role,
        string spec,
        string? adapterOverride,
        string? workingDirectory,
        string? modelOverride,
        string? effortOverride,
        string? outputOverride,
        TimeSpan? timeoutOverride,
        IReadOnlyList<string>? attachments,
        string roomDirectoryPath,
        long? tokenBudgetOverride,
        int? maxToolStepsOverride,
        long? billedRateLimitOverride,
        string? verifyCommandOverride,
        bool? expectPrOverride = null,
        string? verifyResultsPath = null,
        IReadOnlyList<string>? skills = null)
    {
        ValidateAttachments(attachments);

        // #1500: Spec/grant mismatch lint (WARN, never fail). The guarantee is asserted on
        // DispatchSpecLinter's own class doc and in docs/dispatch.md; this try/catch is what actually
        // enforces it. Every heuristic today is a string Contains/StartsWith, but Heuristics is a
        // public list explicitly framed as the extension point — the first heuristic that throws (a
        // future regex, say) must degrade this advisory lint to "skipped", not refuse a dispatch it
        // was only ever supposed to warn about (#1500 second-reader MED-4).
        IReadOnlyList<SpecLintWarning> warnings;
        try
        {
            warnings = DispatchSpecLinter.Lint(spec, role.Grant, role.Id);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: spec/grant lint failed and was skipped ({ex.GetType().Name}: {ex.Message}).");
            warnings = [];
        }

        foreach (var warning in warnings)
        {
            Console.Error.WriteLine(warning.Format());
        }

        string? attachmentsDirectory = attachments is { Count: > 0 } ? ComputeAttachmentsDirectory(roomDirectoryPath) : null;

        return RoleDispatch.Materialize(
            role, spec, adapterOverride, workingDirectory: workingDirectory,
            modelOverride: modelOverride, effortOverride: effortOverride, outputOverride: outputOverride,
            timeoutOverride: timeoutOverride, attachments: attachments, attachmentsDirectory: attachmentsDirectory,
            tokenBudgetOverride: tokenBudgetOverride, maxToolStepsOverride: maxToolStepsOverride,
            billedRateLimitOverride: billedRateLimitOverride,
            verifyCommandOverride: verifyCommandOverride, expectPrOverride: expectPrOverride,
            verifyResultsPath: verifyResultsPath, skills: skills);
    }

    /// <summary>
    /// Copies <c>--attach</c> files into the room's attachments directory (#1500). A no-op when
    /// <paramref name="attachments"/> is null or empty. Called after the room directory exists —
    /// <see cref="Materialize"/> above only needs the destination path as a string, for the prompt text.
    /// <para>
    /// Deliberately not routed through <c>Baton.Artifacts.RoomArtifacts.Write</c> (#496, spec/baton.md
    /// §2's exemption list): an attachment predates every execution in the room, so it carries no
    /// producer to record. <see cref="ValidateAttachments"/> refuses a same-call basename clash;
    /// a clash across two separate dispatches into the same room still silently replaces the file,
    /// a narrower gap outside this issue's scope.
    /// </para>
    /// </summary>
    public static void CopyAttachmentsIntoRoom(IReadOnlyList<string>? attachments, string roomDirectoryPath)
    {
        if (attachments is not { Count: > 0 } attachmentsToCopy)
        {
            return;
        }

        var attachmentsDir = ComputeAttachmentsDirectory(roomDirectoryPath);
        Directory.CreateDirectory(attachmentsDir);
        foreach (var attachPath in attachmentsToCopy)
        {
            var fileName = Path.GetFileName(attachPath);
            var destPath = Path.Combine(attachmentsDir, fileName);
            try
            {
                File.Copy(attachPath, destPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Same R3 (#1354/#1380 finding 3) shape as CopyPrimaryOutputToOverride: a locked or
                // permission-denied source throws a type that does not derive from BatonFlowException,
                // which Program's typed catches would otherwise miss. Wrapped in CliArgumentException
                // (itself a BatonFlowException) so the failure is reported cleanly and the
                // terminal-sentinel path still runs, rather than an unhandled crash.
                throw new CliArgumentException($"Could not copy attached file '{attachPath}' to '{destPath}': {ex.Message}");
            }
        }
    }

    private static string ComputeAttachmentsDirectory(string roomDirectoryPath) =>
        Path.Combine(roomDirectoryPath, Baton.Artifacts.ArtifactManager.ArtifactsDirectoryName, AttachmentsDirectoryName);

    private static void ValidateAttachments(IReadOnlyList<string>? attachments)
    {
        if (attachments is not { Count: > 0 } attachmentsToValidate)
        {
            return;
        }

        var seenFileNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in attachmentsToValidate)
        {
            if (!File.Exists(file))
            {
                throw new CliArgumentException($"Attached file '{file}' does not exist.");
            }

            var fileName = Path.GetFileName(file);
            if (seenFileNames.TryGetValue(fileName, out var priorPath))
            {
                throw new CliArgumentException(
                    $"--attach '{priorPath}' and '{file}' both copy to the same file name '{fileName}' "
                    + "in the room's attachments directory — the second would silently overwrite the first.",
                    "rename one of the files, or pass only one of the two --attach flags.");
            }

            seenFileNames[fileName] = file;
        }
    }
}
