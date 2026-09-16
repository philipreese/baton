using System.Diagnostics;
using Baton.Accounting;
using Baton.Queue;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// What <c>baton queue add --issue &lt;n&gt;</c> does before the item is queued: the three steps the
/// scratchpad runner did by hand — <c>gh issue develop &lt;n&gt; --name &lt;n&gt;-lane</c>,
/// <c>git worktree add &lt;root&gt;/w&lt;n&gt;</c>, then trust the workspace (#1934 slice 1, item 1) —
/// with its repository's own ceiling since #2076, <c>all</c> only as the fallback the remarks below
/// bound.
/// </summary>
/// <remarks>
/// <para>
/// <b>At add time, not launch time.</b> The operator who queues eight items at 23:00 learns
/// immediately that issue 1940 does not exist, rather than at 04:00 when the scheduler reaches it —
/// and the daemon stays free of <c>gh</c>/<c>git</c> spawning, so the recorded scheduling fact stays a
/// fact about scheduling.
/// </para>
/// <para>
/// <b>Trust is a store write, not a shell-out.</b> The issue's wording is "<c>baton trust &lt;ws&gt;
/// --ceiling all</c> before dispatch", and that verb is nothing but
/// <see cref="ProjectCeilingStore.Set"/> (<see cref="TrustCommand"/>) — calling it in-process is the
/// same effect with no fourth process, and it cannot drift from what the verb does because it is what
/// the verb does.
/// </para>
/// <para>
/// <b>The ceiling is the SOURCE REPOSITORY'S, falling back to <c>all</c> (#2076).</b> It used to be
/// <c>all</c> unconditionally — what the runner did, and a real widening — which silently re-opened
/// every category on a worktree of a repository whose ceiling the operator had deliberately narrowed.
/// A worktree of a trusted repository now inherits that repository's own ceiling
/// (<see cref="InheritedProjectCeiling"/>), and only a workspace whose repository was <b>never
/// trusted</b> falls back to unrestricted, which is the pre-#2076 behaviour kept for exactly that
/// repository: <c>queue add --issue n</c> provisions a worktree of the checkout the operator is
/// standing in, so refusing there would break the verb rather than protect anything. The fallback is
/// <b>announced on the verb's own output</b>, the same line the inheritance is, so the widening is
/// never silent. <c>ProjectCeiling</c>'s own doc has what a ceiling does and does not bound.
/// </para>
/// <para>
/// <b>The fallback is NOT taken on a probe failure, and NOT on a revoked repository.</b> "Never
/// trusted" is a fact git established against a store with no trace of the repository
/// (<see cref="InheritanceOutcome.NoTrustedSource"/> says what the scan had to identify to reach it,
/// once). "Git answered nothing" for
/// the workspace (missing, timed out, exited non-zero) is not that fact; neither is a recorded path
/// git could not identify (<see cref="InheritanceOutcome.CandidateUnknown"/>, #2121 — it might be the
/// tombstone); and neither is "every path of this repository carries a tombstone"
/// (<see cref="InheritanceOutcome.Revoked"/>): the first two are transients the operator did not
/// decide, the third is a decision the operator did make. All three throw
/// <see cref="ProjectNotTrustedException"/> — naming the probe failure (and the path it failed on) or
/// the revocation — and the add is refused before anything is queued. spec/baton.md §13 states the
/// populations; §9 has the revoked state itself.
/// </para>
/// </remarks>
public static class IssueWorktreeProvisioner
{
    // ProjectCeilingStore is a read-modify-write file store. Concurrent issue provisions may reach
    // the trust step after claiming distinct branch/worktree pairs, so serialize that store update.
    private static readonly SemaphoreSlim TrustGate = new(1, 1);

    /// <summary>The exact workspace and branch selected while provisioning an issue lane.</summary>
    public sealed record ProvisionedIssueWorktree(string Workspace, string Branch);

    /// <summary>
    /// The branch <c>gh issue develop</c> is asked to create for <paramref name="issue"/>. <b>Derived,
    /// never queried</b> — the name is this method's ruling (spec/baton.md §13), so a later reader that
    /// needs it (a work item's <c>Branch</c>, the PR lookup) computes it here rather than asking
    /// <c>gh</c> a question whose answer this file already fixed.
    /// </summary>
    public static string BranchNameFor(int issue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issue);
        return $"{issue}-lane";
    }

    /// <summary>
    /// Resolves the configured root with the same sibling-checkout default used by issue provisioning.
    /// Retained-worktree validation consumes this value too, so its root boundary cannot differ from
    /// the provisioner that originally selected the checkout.
    /// </summary>
    internal static string ResolveWorktreeRoot(string? configuredRoot, string repositoryDirectory) =>
        configuredRoot ?? Path.GetDirectoryName(Path.GetFullPath(repositoryDirectory))
            ?? throw new CliArgumentException(
                $"Cannot derive a worktree root from '{repositoryDirectory}' — it has no parent directory.",
                "set Queue.WorktreeRoot in ~/.baton/settings.json to say where w<n> worktrees belong.");

    /// <summary>
    /// The issue's title and body, for the implement brief <c>baton queue add --lifecycle</c> renders
    /// when no <c>--spec</c> is given. Through the SAME runner every other spawn here uses, so this adds
    /// no process-spawn site of its own.
    /// </summary>
    /// <param name="repository">The canonical repository captured before provisioning; passed to
    /// <c>gh --repo</c> so ambient CLI context cannot redirect the issue read.</param>
    /// <returns>Title and body; the body is empty when the issue has none.</returns>
    /// <exception cref="CliArgumentException"><c>gh</c> refused — the issue does not exist, or <c>gh</c> is not authenticated.</exception>
    public static async Task<(string Title, string Body)> FetchIssueAsync(
        int issue,
        string repositoryDirectory,
        string repository,
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<(int ExitCode, string Output)>>? runner = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issue);
        ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);
        ArgumentException.ThrowIfNullOrEmpty(repository);

        runner ??= RunRetainedProbeAsync;
        var (exit, output) = await runner(
            "gh",
            ["issue", "view", issue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--json", "title,body", "--repo", repository],
            repositoryDirectory, cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new CliArgumentException(
                $"'gh issue view {issue} --json title,body' failed (exit {exit}): {output.Trim()}",
                "check that the issue exists and that 'gh' is authenticated, or pass '--spec <file>' to write "
                + "the brief's \"## Do\" section yourself.");
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(output);
            var root = document.RootElement;
            return (
                root.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new CliArgumentException(
                $"'gh issue view {issue}' returned output this could not read as JSON: {ex.Message}",
                "run the command by hand to see what it printed, or pass '--spec <file>'.");
        }
    }

    /// <summary>Each spawn's wall-clock bound. <c>gh issue develop</c> touches the network, so the
    /// hang safety here is the time bound, not the environment — the same posture
    /// <see cref="WorkspaceDeliveryProbe"/> documents for its own <c>gh</c> spawns.</summary>
    public static readonly TimeSpan SpawnTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Provisions and trusts the worktree for <paramref name="issue"/>, returning the exact path and branch selected.
    /// </summary>
    /// <param name="issue">The GitHub issue number.</param>
    /// <param name="repositoryDirectory">The checkout <c>gh</c> and <c>git</c> are run in.</param>
    /// <param name="worktreeRoot">
    /// Where <c>w&lt;n&gt;</c> is created. Null resolves to <paramref name="repositoryDirectory"/>'s
    /// parent — the sibling-repos layout the runner assumed, which the issue's own
    /// <c>&lt;repos&gt;</c> never defined. <c>QueueSettings.WorktreeRoot</c> is what an operator with
    /// a different layout sets.
    /// </param>
    /// <param name="repository">The canonical repository captured before provisioning; passed to
    /// <c>gh --repo</c> so ambient CLI context cannot redirect branch creation.</param>
    /// <param name="runner">Test seam: runs one command and returns (exit code, stdout+stderr).</param>
    /// <param name="probe">
    /// Test seam for the trust step's repository-identity lookup (#2076) — the same injected-probe shape
    /// <see cref="InheritedProjectCeiling.TryRecordAsync"/> takes. Null uses git.
    /// </param>
    /// <param name="output">
    /// Where the inheritance line goes when the worktree picks a ceiling up (#2076) — the <c>queue
    /// add</c>'s own writer. Null is <see cref="Console.Out"/>. See <see cref="TrustAsync"/> for why the
    /// line cannot be left to the later dispatch.
    /// </param>
    /// <exception cref="CliArgumentException">Any of the three steps failed, with the tool's own output in the message.</exception>
    /// <exception cref="ProjectNotTrustedException">The trust step's identity probe answered nothing (for the workspace or for a recorded path), or the repository is revoked (#2121), so no ceiling was recorded — see the type remarks.</exception>
    public static async Task<ProvisionedIssueWorktree> ProvisionAsync(
        int issue,
        string repositoryDirectory,
        string? worktreeRoot,
        string repository,
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<(int ExitCode, string Output)>>? runner = null,
        Func<string, CancellationToken, Task<RepositoryIdentity?>>? probe = null,
        TextWriter? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issue);
        ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);
        ArgumentException.ThrowIfNullOrEmpty(repository);

        runner ??= RunRetainedProbeAsync;

        var root = ResolveWorktreeRoot(worktreeRoot, repositoryDirectory);

        var firstWorkspace = Path.Combine(root, $"w{issue}");
        var firstBranch = BranchNameFor(issue);

        if (Directory.Exists(firstWorkspace))
        {
            // A directory named w<n> is not evidence that it is the live lane. Only an exact Git
            // registration on the first-lane branch permits reuse. A positively different attached
            // branch plus a proven canonical branch collision leaves the old directory untouched and
            // selects a free suffix; an unreadable or unregistered path still refuses fail-closed.
            if (await CanReuseCanonicalWorktreeAsync(firstWorkspace, firstBranch, repositoryDirectory, runner, cancellationToken)
                    .ConfigureAwait(false))
            {
                await TrustAsync(firstWorkspace, repositoryDirectory, probe, output: output, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new ProvisionedIssueWorktree(firstWorkspace, firstBranch);
            }
        }

        for (var suffix = 1; suffix <= 100; suffix++)
        {
            var branch = suffix == 1 ? firstBranch : $"{firstBranch}-{suffix}";
            var workspace = suffix == 1 ? firstWorkspace : Path.Combine(root, $"w{issue}-{suffix}");
            if (Directory.Exists(workspace))
            {
                continue;
            }

            // Check every candidate before asking GitHub to create it. In particular, the canonical
            // name may have survived a merged PR even when its w<n> worktree did not.
            if (await BranchExistsAsync(branch, repositoryDirectory, runner, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var (developExit, developOutput) = await runner(
                "gh", ["issue", "develop", issue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--name", branch, "--repo", repository],
                repositoryDirectory, cancellationToken).ConfigureAwait(false);
            if (developExit != 0)
            {
                if (await BranchExistsAsync(branch, repositoryDirectory, runner, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                throw new CliArgumentException(
                    $"'gh issue develop {issue} --name {branch}' failed (exit {developExit}): {developOutput.Trim()}",
                    "check that the issue exists and that 'gh' is authenticated for this repository.");
            }

            var (worktreeExit, worktreeOutput) = await runner(
                "git", ["worktree", "add", workspace, branch], repositoryDirectory, cancellationToken).ConfigureAwait(false);
            if (worktreeExit == 0)
            {
                await TrustAsync(workspace, repositoryDirectory, probe, output: output, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new ProvisionedIssueWorktree(workspace, branch);
            }

            if (Directory.Exists(workspace))
            {
                continue;
            }

            throw new CliArgumentException(
                $"'git worktree add {workspace} {branch}' failed (exit {worktreeExit}): {worktreeOutput.Trim()}",
                $"the branch '{branch}' could not be attached at '{workspace}'; inspect the diagnostic and retry.");
        }

        throw new CliArgumentException(
            $"Could not select a free branch and workspace for issue {issue} after 100 deterministic attempts.",
            "remove stale issue worktrees or retry after concurrent queue adds finish.");
    }

    private static async Task<bool> BranchExistsAsync(
        string branch,
        string repositoryDirectory,
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<(int ExitCode, string Output)>> runner,
        CancellationToken cancellationToken)
    {
        var (localExit, localOutput) = await runner(
            "git", ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], repositoryDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (localExit == 0)
        {
            return true;
        }
        if (localExit != 1)
        {
            throw new CliArgumentException(
                $"Could not determine whether local branch '{branch}' exists (exit {localExit}): {localOutput.Trim()}",
                "resolve the git error and retry; a branch suffix is selected only after a proven collision.");
        }

        var (remoteExit, remoteOutput) = await runner(
            "git", ["ls-remote", "--heads", "origin", $"refs/heads/{branch}"], repositoryDirectory, cancellationToken).ConfigureAwait(false);
        if (remoteExit != 0)
        {
            throw new CliArgumentException(
                $"Could not determine whether remote branch '{branch}' exists (exit {remoteExit}): {remoteOutput.Trim()}",
                "resolve the git error and retry; a branch suffix is selected only after a proven collision.");
        }

        return remoteOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line =>
            line.EndsWith($"\trefs/heads/{branch}", StringComparison.Ordinal));
    }

    private static async Task<bool> CanReuseCanonicalWorktreeAsync(
        string workspace,
        string branch,
        string repositoryDirectory,
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<(int ExitCode, string Output)>> runner,
        CancellationToken cancellationToken)
    {
        var (exit, output) = await runner(
            "git", ["worktree", "list", "--porcelain"], repositoryDirectory, cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new CliArgumentException(
                $"Could not verify existing workspace '{workspace}' as a git worktree (exit {exit}): {output.Trim()}",
                "resolve the git error or move the unexpected directory before retrying.");
        }

        var expectedPath = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? foundBranch = null;
        string? listedPath = null;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("worktree ", StringComparison.Ordinal))
            {
                listedPath = trimmed["worktree ".Length..];
                continue;
            }

            if (trimmed.StartsWith("branch ", StringComparison.Ordinal)
                && listedPath is not null
                && string.Equals(
                    Path.GetFullPath(listedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                foundBranch = trimmed["branch ".Length..];
                break;
            }
        }

        var expectedBranch = $"refs/heads/{branch}";
        if (string.Equals(foundBranch, expectedBranch, StringComparison.Ordinal))
        {
            return true;
        }

        // A registered worktree on another branch is occupied, not orphaned. The spec's reopened
        // issue suffix route is reachable only when Git positively proves the canonical ref already
        // exists; a failed/empty probe must not silently make an unexpected directory look reusable.
        if (foundBranch is not null && await BranchExistsAsync(branch, repositoryDirectory, runner, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        var detail = foundBranch is null ? "is not registered as a git worktree" : $"checks out '{foundBranch}'";
        throw new CliArgumentException(
            $"Existing workspace '{workspace}' {detail}; expected branch '{expectedBranch}'.",
            "move or remove the unexpected directory, or re-add the issue from the worktree that owns its recorded branch.");
    }

    /// <summary>
    /// Records <paramref name="workspace"/>'s ceiling: the source repository's own, when a path in that
    /// repository is already trusted, and unrestricted when the repository was never trusted — see the
    /// type remarks for why that fallback is a widening rather than a refusal, and why a probe that
    /// answers nothing or a revoked repository is a refusal rather than the fallback. Writes nothing when the workspace already
    /// carries an entry (<see cref="InheritanceOutcome.AlreadyTrusted"/>), which is what makes a re-add
    /// of a live lane leave its ceiling as the operator last set it rather than resetting it to <c>all</c>.
    /// </summary>
    /// <remarks>
    /// <b>The inheritance is announced HERE, not by the later dispatch</b>, under the print-adjacent
    /// rule <see cref="InheritedProjectCeiling"/> states. This site is the one where dropping the line
    /// is least obviously fatal and most actually is: the lane provisioned here goes on to dispatch, so
    /// it reads as though the dispatch could say it instead — and it cannot, because that dispatch finds
    /// the workspace already trusted.
    /// </remarks>
    internal static async Task TrustAsync(
        string workspace,
        string sourceRepository,
        Func<string, CancellationToken, Task<RepositoryIdentity?>>? probe = null,
        string? storePath = null,
        TextWriter? output = null,
        CancellationToken cancellationToken = default)
    {
        storePath ??= ProjectCeilingStore.DefaultPath;
        probe ??= RepositoryIdentityResolver.TryResolveAsync;

        await TrustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var own = ProjectCeilingStore.TryGetRecord(workspace, storePath);
            if (own is { IsRevoked: false }) return;
            // Audit the whole repository identity without writing. The exact invocation checkout is
            // the only source we will copy, but unknown/revoked sibling evidence still blocks the
            // never-trusted `all` fallback.
            var observation = await InheritedProjectCeiling.InspectAsync(
                workspace, storePath, probe, cancellationToken, unknownOutranksSource: true).ConfigureAwait(false);
            switch (observation.Outcome)
            {
                case InheritanceOutcome.NoIdentity:
                    throw new ProjectNotTrustedException(workspace,
                        "the repository-identity probe answered nothing (git missing, timed out, or exited non-zero).");
                case InheritanceOutcome.CandidateUnknown:
                    throw new ProjectNotTrustedException(
                        workspace, observation.CandidatePath!, observation.ProbeFailure ?? "repository identity is unreadable");
                case InheritanceOutcome.Revoked:
                    throw new ProjectNotTrustedException(workspace, observation.RevokedPath!, observation.RevokedAt!.Value);
                case InheritanceOutcome.AlreadyTrusted:
                    return;
            }

            RepositoryIdentity? sourceIdentity;
            try
            {
                sourceIdentity = await probe(sourceRepository, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ProjectNotTrustedException(workspace,
                    $"the exact source checkout repository probe threw: {ex.Message}.");
            }
            if (sourceIdentity is null || observation.RepositoryIdentity is null
                || !string.Equals(sourceIdentity.Value, observation.RepositoryIdentity, StringComparison.Ordinal))
                throw new ProjectNotTrustedException(workspace,
                    "the exact source checkout could not be proven to be the same repository as the new worktree.");

            // #2333: the invocation checkout is the sole deterministic bootstrap authority. A
            // temporary review sibling can block an unsafe fallback, but can never donate its grant.
            var source = ProjectCeilingStore.TryGetRecord(sourceRepository, storePath);
            if (source is { IsRevoked: true })
                throw new ProjectNotTrustedException(workspace, ProjectCeilingStore.CanonicalKey(sourceRepository), source.RevokedAt!.Value);
            if (source is not null)
            {
                var inherited = source with { InheritedFrom = ProjectCeilingStore.CanonicalKey(sourceRepository) };
                ProjectCeilingStore.Set(workspace, inherited, storePath);
                (output ?? Console.Out).WriteLine(
                    $"workspace {ProjectCeilingStore.CanonicalKey(workspace)}: inherited ceiling from source repository {ProjectCeilingStore.CanonicalKey(sourceRepository)}");
                return;
            }

            var sourceKey = ProjectCeilingStore.CanonicalKey(sourceRepository);
            if (observation.Outcome == InheritanceOutcome.Inherited
                && observation.SourceCeiling is { } derived
                && string.Equals(derived.InheritedFrom, sourceKey, StringComparison.OrdinalIgnoreCase))
            {
                ProjectCeilingStore.Set(workspace, derived with { InheritedFrom = sourceKey }, storePath);
                (output ?? Console.Out).WriteLine(
                    $"workspace {ProjectCeilingStore.CanonicalKey(workspace)}: inherited ceiling from deterministic source bootstrap {sourceKey}");
                return;
            }
            if (observation.Outcome == InheritanceOutcome.Inherited)
                throw new ProjectNotTrustedException(workspace,
                    $"the exact source checkout '{sourceKey}' has no recorded ceiling, while another checkout of this repository does; sibling trust is not a deterministic bootstrap authority.");

            ProjectCeilingStore.Set(
                workspace, ProjectCeiling.Unrestricted with { InheritedFrom = sourceKey }, storePath);
            (output ?? Console.Out).WriteLine(
                $"workspace {ProjectCeilingStore.CanonicalKey(workspace)}: source repository has no recorded ceiling; recorded ceiling all");
        }
        finally
        {
            TrustGate.Release();
        }
    }

    /// <summary>
    /// The production runner. Spawns <c>gh</c>/<c>git</c> — read-and-write forge and repo commands,
    /// never a vendor CLI — with stdout and stderr merged, bounded by <see cref="SpawnTimeout"/> and
    /// by the caller's token. A timeout kills the child and surfaces as a non-zero exit with the
    /// output collected so far, so the refusal message above still names something.
    /// </summary>
    internal static async Task<(int ExitCode, string Output)> RunRetainedProbeAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = ChildProcessStartInfo.Create(fileName, startInfo =>
        {
            startInfo.WorkingDirectory = workingDirectory;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            // Pinned rather than inherited: the console code page decides otherwise, and a gh error
            // message carrying a non-ASCII issue title would come back mojibake in the refusal the
            // operator reads. RedirectedProcessEncodingTests is what makes this non-optional.
            startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
            startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
        });
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Same two variables WorkspaceDeliveryProbe sets, and with the same disclosed limit: they make
        // a credential prompt less likely, they do not stop an OS credential manager. The time bound
        // below is what this actually rests on.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "never";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (127, $"could not start '{fileName}': {ex.Message}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SpawnTimeout);

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode, $"{await stdout.ConfigureAwait(false)}{await stderr.ConfigureAwait(false)}");
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone. Nothing to clean up, and reporting the kill failure would replace the
                // real answer (the command did not finish in time) with a less useful one.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return (124, $"'{fileName}' did not finish within {SpawnTimeout.TotalMinutes:0} minutes and was killed.");
        }
    }
}
