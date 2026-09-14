using System.Diagnostics;
using System.Text.Json;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>Conductor verification for the sole originating pull-request authority a room can carry.</summary>
internal static class OriginatingPullRequestVerifier
{
    public static async Task<OriginatingPullRequestOwnership> VerifyAsync(string reference, string workspace, CancellationToken cancellationToken)
    {
        var separator = reference.LastIndexOf('#');
        var repository = separator > 0 ? CanonicalRepository(reference[..separator]) : null;
        if (repository is null || !int.TryParse(reference[(separator + 1)..], out var number) || number <= 0)
            throw new CliArgumentException("'--originating-pr' must be owner/repository#number.");
        var identity = GhPullRequestCreateProvenanceResolver.TryCaptureIdentity(workspace);
        var launchHead = await WorkspaceHead.TryCaptureAsync(workspace, cancellationToken).ConfigureAwait(false);
        if (identity is null || launchHead is null || identity.Repository != repository)
            throw new CliArgumentException("The workspace repository and launch HEAD must be readable before originating PR ownership can be granted.");
        var start = ChildProcessStartInfo.Create("gh", info => { info.WorkingDirectory = workspace; info.RedirectStandardOutput = true; info.RedirectStandardError = true; });
        foreach (var argument in new[] { "pr", "view", number.ToString(), "--repo", repository, "--json", "state,headRefName,headRefOid" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new CliArgumentException("Could not start gh to verify '--originating-pr'.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        _ = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new CliArgumentException("The originating pull request could not be read.");
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var state = root.GetProperty("state").GetString();
        var branch = root.GetProperty("headRefName").GetString();
        var head = root.GetProperty("headRefOid").GetString();
        if (!string.Equals(state, "OPEN", StringComparison.OrdinalIgnoreCase) || !string.Equals(branch, identity.HeadBranch, StringComparison.Ordinal) || !string.Equals(head, launchHead, StringComparison.OrdinalIgnoreCase))
            throw new CliArgumentException("The originating pull request must be open and match the workspace repository, branch, and pre-dispatch HEAD.");
        return new OriginatingPullRequestOwnership(repository, number, branch!, launchHead);
    }

    private static string? CanonicalRepository(string value)
    {
        var parts = value.Trim().Replace('\\', '/').Trim('/').Split('/');
        return parts.Length == 2 && parts.All(part => part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            ? string.Join('/', parts).ToLowerInvariant() : null;
    }
}
