using System.Text.Json;
using System.Text.RegularExpressions;
using Baton.Accounting;
using Baton.Queue;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// The single admission boundary for <c>queue add --issue n --workspace path --lifecycle</c>.
/// It observes an exact retained checkout and returns durable proof; it never provisions or trusts.
/// </summary>
internal static class RetainedIssueWorktreeValidator
{
    internal sealed record Proof(
        string Workspace,
        string Repository,
        string Branch,
        string Head,
        ProjectCeiling Ceiling,
        IReadOnlyList<string> TerminalPredecessorTags,
        RecordedProjectCeilingAdmission.Result Admission);

    internal static async Task<Proof> ValidateAsync(
        string workspace,
        int issue,
        string repository,
        string? worktreeRoot,
        WorkerRole role,
        bool requireDeclaredRequirements,
        IReadOnlyList<string> requirements,
        IReadOnlyList<QueueItem> queueItems,
        CancellationToken cancellationToken,
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<(int ExitCode, string Output)>>? runner = null,
        Func<string, CancellationToken, Task<RepositoryIdentity?>>? repositoryResolver = null,
        Func<string, string?, bool, string>? ghResolver = null,
        QueueWorktreeLivenessProbe? livenessProbe = null)
    {
        runner ??= RunAsync;
        repositoryResolver ??= RepositoryIdentityResolver.TryResolveAsync;
        ghResolver ??= OriginatingPullRequestVerifier.ResolveExecutable;
        var path = QueueWorktreeReport.TryFullPath(workspace)
            ?? throw Refusal("workspace-path", "the workspace path could not be normalized", workspace);
        var root = QueueWorktreeReport.TryFullPath(worktreeRoot)
            ?? throw Refusal("worktree-root", "the configured worktree root is unreadable", workspace);
        if (!IsStrictlyBeneath(path, root)) throw Refusal("worktree-root", "the workspace is not beneath the configured worktree root", path);
        if (!Directory.Exists(path)) throw Refusal("workspace-directory", "the exact workspace does not exist", path);

        var (listExit, listOutput) = await runner("git", ["worktree", "list", "--porcelain"], path, cancellationToken).ConfigureAwait(false);
        if (listExit != 0) throw Refusal("git-worktree", "git worktree registration could not be read", path);
        if (!TryRegistration(listOutput, path, out var head, out var branchRef) || head is null)
            throw Refusal("git-worktree", "the exact workspace is not a registered Git worktree", path);

        var branch = branchRef is { } value && value.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? value["refs/heads/".Length..] : null;
        if (branch is null || !Regex.IsMatch(branch, $"^{issue}-lane(?:-[1-9][0-9]*)?$", RegexOptions.CultureInvariant))
            throw Refusal("git-branch", $"the attached branch '{branchRef ?? "detached"}' is not {issue}-lane or a positive suffix", path);

        var identity = await repositoryResolver(path, cancellationToken).ConfigureAwait(false);
        if (identity?.Value != repository)
            throw Refusal("repository-identity", "the workspace repository identity does not match the issue repository", path);

        var (refExit, refOutput) = await runner("git", ["rev-parse", "--verify", "refs/heads/" + branch], path, cancellationToken).ConfigureAwait(false);
        if (refExit != 0 || !string.Equals(refOutput.Trim(), head, StringComparison.Ordinal))
            throw Refusal("git-head", "the attached branch and HEAD are inconsistent", path);

        var (statusExit, statusOutput) = await runner("git", ["status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none"], path, cancellationToken).ConfigureAwait(false);
        if (statusExit != 0) throw Refusal("git-status", "substantive cleanliness could not be read", path);
        if (!string.IsNullOrWhiteSpace(statusOutput)) throw Refusal("git-status", "the workspace has tracked or untracked source changes", path);

        var live = queueItems.Where(IsLiveOwner).ToList();
        RefuseIfLiveQueueOwnership(queueItems, path, branch);

        var referenceProbeItems = live.Append(new QueueItem
        {
            Tag = "retained-validation",
            Role = role.Id,
            Workspace = path,
            SpecFile = path,
            State = QueueItemState.Done,
            Retirement = new QueueRetirement(QueueRetirement.Operator, DateTimeOffset.UtcNow, "validation probe"),
        }).ToList();
        var references = await QueueWorktreeReferenceIndex.CreateAsync(
            referenceProbeItems, cancellationToken, livenessProbe).ConfigureAwait(false);
        if (!references.Complete) throw Refusal("room-lock", "room or build-lock liveness evidence could not be read", path);
        if (references.For(path).Count > 0 || references.ForBranch(branch).Count > 0)
            throw Refusal("room-lock", "a live Baton room or build lock owns this workspace or branch", path);

        string gh;
        try
        {
            gh = ghResolver(
                path, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());
        }
        catch (CliArgumentException ex)
        {
            throw Refusal("pull-request-authority", ex.Message, path);
        }
        var (prExit, prOutput) = await runner(gh, ["pr", "list", "--head", branch, "--state", "open", "--limit", "1", "--json", "number", "--repo", repository], path, cancellationToken).ConfigureAwait(false);
        if (prExit != 0) throw Refusal("pull-request", "the open-pull-request probe could not be read", path);
        try
        {
            using var document = JsonDocument.Parse(prOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
            if (document.RootElement.GetArrayLength() > 0)
                throw new CliArgumentException(
                    $"Retained-worktree validation refused '{path}': pull-request evidence shows an open PR for '{branch}'.",
                    "use the existing PR's explicit continuation path; retained-worktree retry is only for a pre-PR zero-step failure.");
        }
        catch (JsonException)
        {
            throw Refusal("pull-request", "the open-pull-request probe returned unreadable evidence", path);
        }

        var admissionItem = new QueueItem { Tag = "retained-validation", Role = role.Id, Workspace = path, SpecFile = path, Requirements = requirements };
        RecordedProjectCeilingAdmission.Result admission;
        ProjectCeiling? ceiling;
        try
        {
            admission = RecordedProjectCeilingAdmission.Evaluate(admissionItem, role, requireDeclaredRequirements);
            ceiling = ProjectCeilingStore.TryGetRecord(path, ProjectCeilingStore.DefaultPath);
        }
        catch (Exception ex) when (ex is ProjectCeilingStoreException or IOException or UnauthorizedAccessException)
        {
            throw Refusal("trust", $"the exact-path trust store could not be read: {ex.Message}", path);
        }
        if (ceiling is null || !admission.CeilingFound || admission.Admission.Result == TaskRequirementAdmission.Refused)
            throw new CliArgumentException(
                admission.CeilingFound ? admission.RefusalMessage(path, role.Id) : $"Retained-worktree validation refused '{path}': trust evidence for this exact path is missing.",
                $"trust this exact checkout, then retry: baton trust \"{path}\" --ceiling \"ReadFiles,WriteFiles,RunShellCommands,NetworkAccess\".");

        var predecessors = queueItems.Where(item => !IsLiveOwner(item)
                && (QueueWorktreeReport.PathComparer.Equals(QueueWorktreeReport.TryFullPath(item.Workspace), path)
                || string.Equals(item.Branch, branch, StringComparison.Ordinal))
                && item.State is QueueItemState.Done or QueueItemState.Failed or QueueItemState.Cancelled)
            .Select(item => item.Tag).Distinct(StringComparer.Ordinal).ToList();
        return new Proof(path, repository, branch, head, ceiling, predecessors, admission);
    }

    /// <summary>
    /// Refuses live ownership from the queue snapshot currently being observed. Queue add calls this
    /// both before its side effects and again inside the queue-store mutation, whose snapshot is held
    /// under the queue lock.
    /// </summary>
    internal static void RefuseIfLiveQueueOwnership(IReadOnlyList<QueueItem> queueItems, string path, string branch)
    {
        var live = queueItems.Where(IsLiveOwner);
        if (live.Any(item => QueueWorktreeReport.PathComparer.Equals(QueueWorktreeReport.TryFullPath(item.Workspace), path)
            || string.Equals(item.Branch, branch, StringComparison.Ordinal)))
            throw Refusal("queue-liveness", "an active queue item or lifecycle owns this path or branch", path);
    }

    private static bool IsLiveOwner(QueueItem item) =>
        item.State is QueueItemState.Queued or QueueItemState.Launched
        || QueueScheduler.IsActiveLifecycle(item);

    private static CliArgumentException Refusal(string probe, string detail, string path) => new(
        $"Retained-worktree validation refused '{path}': {detail} (failed probe: {probe}).",
        "repair the named evidence and retry; validation makes no trust, queue, or worktree changes.");

    internal static bool TryRegistration(string porcelain, string expectedPath, out string? head, out string? branch)
    {
        head = null;
        branch = null;
        string? current = null;
        var found = false;
        foreach (var line in porcelain.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                current = QueueWorktreeReport.TryFullPath(line["worktree ".Length..]);
                found |= QueueWorktreeReport.PathComparer.Equals(current, expectedPath);
                continue;
            }
            if (!QueueWorktreeReport.PathComparer.Equals(current, expectedPath)) continue;
            if (line.StartsWith("HEAD ", StringComparison.Ordinal)) head = line["HEAD ".Length..];
            if (line.StartsWith("branch ", StringComparison.Ordinal)) branch = line["branch ".Length..];
        }
        return found;
    }

    private static bool IsStrictlyBeneath(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> arguments, string directory, CancellationToken token) =>
        IssueWorktreeProvisioner.RunRetainedProbeAsync(file, arguments, directory, token);
}
