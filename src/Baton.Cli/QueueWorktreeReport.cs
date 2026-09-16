using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Baton.Accounting;
using Baton.Concurrency;
using Baton.Queue;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>One read-only projection shared by <c>queue worktrees</c> text and JSON output.</summary>
internal sealed record QueueWorktreeReport(string? WorktreeRoot, IReadOnlyList<QueueWorktreeEntry> Workspaces)
{
    private const int MaxParallelWorkspaceProbes = 8;

    public static async Task<QueueWorktreeReport> CreateAsync(
        IReadOnlyList<QueueItem> items,
        string? root,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<RepositoryIdentity?>>? repositoryResolver = null)
    {
        repositoryResolver ??= RepositoryIdentityResolver.TryResolveAsync;
        var resolvedRoot = TryFullPath(root);
        var activeReferences = await QueueWorktreeReferenceIndex.CreateAsync(items, cancellationToken).ConfigureAwait(false);
        var groups = items.GroupBy(item => TryFullPath(item.Workspace) ?? item.Workspace, PathComparer)
            .OrderBy(group => group.Key, PathComparer)
            .Select((group, index) => (Index: index, Path: group.Key, Rows: (IReadOnlyList<QueueItem>)group.ToList()))
            .ToList();
        using var gate = new SemaphoreSlim(Math.Min(MaxParallelWorkspaceProbes, Math.Max(1, Environment.ProcessorCount)));
        var entryTasks = groups.Select(async group =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return (group.Index, Entry: await QueueWorktreeEntry.CreateAsync(
                        group.Path, resolvedRoot, group.Rows, activeReferences, repositoryResolver, cancellationToken)
                    .ConfigureAwait(false));
            }
            finally
            {
                gate.Release();
            }
        });
        var entries = (await Task.WhenAll(entryTasks).ConfigureAwait(false))
            .OrderBy(result => result.Index)
            .Select(result => result.Entry)
            .ToList();

        return new QueueWorktreeReport(resolvedRoot, entries);
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    public string ToText() => string.Join(Environment.NewLine, new[]
    {
        $"Configured worktree root: {WorktreeRoot ?? "unknown"}",
        "Static candidate is not deletion authorization.",
    }.Concat(Workspaces.Select(entry =>
        $"{entry.Path}: {entry.Classification} ({string.Join(", ", entry.ReasonCodes)})\n"
        + $"  origin: {entry.Origin}; beneath root: {entry.BeneathConfiguredRoot}; directory: {entry.Directory}\n"
        + $"  rows: {string.Join(", ", entry.Rows.Select(row => $"{row.Tag}/{row.State}/{row.Stage ?? "none"}; origin={row.Origin}; retired={row.Retired}; repository={row.Repository ?? "unknown"}; branch={row.Branch ?? "unknown"}"))}\n"
        + $"  git: {entry.Git.Registration}; head: {entry.Git.Head ?? "unknown"}; expected repository: {entry.Git.ExpectedRepository ?? "unknown"}; observed repository: {entry.Git.Repository ?? "unknown"}; expected branch: {entry.Git.ExpectedBranch ?? "unknown"}; raw status: {entry.Git.RawStatus ?? "unknown"}; truncated: {entry.Git.RawStatusTruncated}\n"
        + $"  substantive cleanliness: {entry.Git.SubstantiveCleanliness}; active references: {entry.ActiveReferences}; reference observation: {(entry.ActiveReferencesComplete ? "complete" : "unknown")}; size: {entry.SizeBytes?.ToString() ?? "unknown"} ({entry.SizeReason ?? "observed"})")));

    internal static string? TryFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

internal sealed record QueueWorktreeRow(
    string Tag,
    string State,
    string? Stage,
    string Origin,
    string? Repository,
    string? Branch,
    bool Retired);

internal sealed record QueueWorktreeGit(
    string Registration,
    string? Head,
    string? ExpectedRepository,
    string? ExpectedBranch,
    string? RawStatus,
    bool RawStatusTruncated,
    string SubstantiveCleanliness,
    string? Repository,
    IReadOnlyList<string> ReasonCodes);

internal sealed record QueueWorktreeEntry(
    string Path,
    string Origin,
    bool BeneathConfiguredRoot,
    string Directory,
    QueueWorktreeGit Git,
    long? SizeBytes,
    string? SizeReason,
    string ActiveReferences,
    bool ActiveReferencesComplete,
    string Classification,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<QueueWorktreeRow> Rows)
{
    private const int MaxFilesMeasured = 10_000;
    private const int MaxDirectoriesMeasured = 10_000;
    private const long MaxBytesMeasured = 1L << 30;
    private const int MaxRawStatusChars = 16_384;
    private const int MaxGitProbeChars = 1 << 20;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public static async Task<QueueWorktreeEntry> CreateAsync(
        string path,
        string? root,
        IReadOnlyList<QueueItem> rows,
        QueueWorktreeReferenceIndex activeReferenceIndex,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> repositoryResolver,
        CancellationToken cancellationToken)
    {
        var origins = rows.Select(row => row.WorkspaceOrigin ?? WorkspaceOrigins.Unknown)
            .Distinct(StringComparer.Ordinal).OrderBy(origin => origin, StringComparer.Ordinal).ToList();
        var repositories = rows.Select(row => row.Repository).Distinct(StringComparer.Ordinal).ToList();
        var branches = rows.Select(row => row.Branch).Distinct(StringComparer.Ordinal).ToList();
        var reasons = new List<string>();
        var beneath = root is not null && IsStrictlyBeneath(path, root);
        var exists = System.IO.Directory.Exists(path);
        var eligibleRows = rows.All(IsDurablyInactive);
        var referenceObservation = DescribeReferences(path, rows, activeReferenceIndex);
        var references = string.Join(",", referenceObservation.References.DefaultIfEmpty("none-known"));

        if (origins.Count != 1 || origins[0] != WorkspaceOrigins.IssueProvisioned) reasons.Add("origin-not-owned");
        if (!eligibleRows) reasons.Add("active-or-unretired-row");
        if (rows.Any(row => row.State == QueueItemState.Failed || row.Halted)) reasons.Add("failed-or-halted-row");
        if (!beneath) reasons.Add("outside-configured-root");
        if (!exists) reasons.Add("missing-directory");
        if (repositories.Count != 1 || repositories[0] is null) reasons.Add("conflicting-or-missing-repository");
        if (branches.Count != 1 || string.IsNullOrWhiteSpace(branches[0])) reasons.Add("conflicting-or-missing-branch");
        if (referenceObservation.References.Count > 0) reasons.Add("active-baton-reference");
        if (!referenceObservation.Complete) reasons.Add("active-reference-observation-unavailable");

        var sizeTask = Task.Run(() => ObserveSize(path), cancellationToken);
        var git = exists
            ? await ObserveGitAsync(path, repositories.Count == 1 ? repositories[0] : null,
                branches.Count == 1 ? branches[0] : null, repositoryResolver, cancellationToken).ConfigureAwait(false)
            : new QueueWorktreeGit(
                "unavailable", null, repositories.Count == 1 ? repositories[0] : null,
                branches.Count == 1 ? branches[0] : null, null, false, "unknown", null,
                ["git-worktree-probe-unavailable"]);
        var sizeObservation = await sizeTask.ConfigureAwait(false);
        var size = sizeObservation.Bytes;
        var sizeReason = sizeObservation.Reason;

        reasons.AddRange(git.ReasonCodes);
        if (git.SubstantiveCleanliness == "dirty") reasons.Add("substantive-uncommitted-content");
        if (size is null) reasons.Add("size-observation-unavailable");

        var candidate = reasons.Count == 0;
        var unavailable = !exists
            || origins.Contains(WorkspaceOrigins.Unknown, StringComparer.Ordinal)
            || !referenceObservation.Complete
            || git.Registration == "unavailable"
            || git.SubstantiveCleanliness == "unknown"
            || git.ReasonCodes.Contains("repository-probe-unavailable", StringComparer.Ordinal)
            || git.ReasonCodes.Contains("expected-branch-probe-unavailable", StringComparer.Ordinal)
            || size is null;
        var classification = candidate ? "candidate" : unavailable ? "unknown" : "retain";
        return new QueueWorktreeEntry(
            path,
            string.Join(",", origins),
            beneath,
            exists ? "present" : "missing",
            git,
            size,
            sizeReason,
            references,
            referenceObservation.Complete,
            classification,
            reasons,
            rows.Select(row => new QueueWorktreeRow(
                row.Tag,
                row.State.ToString().ToLowerInvariant(),
                row.Stage is { } stage ? WorkStages.Token(stage) : null,
                row.WorkspaceOrigin ?? WorkspaceOrigins.Unknown,
                row.Repository,
                row.Branch,
                row.Retirement is not null)).ToList());
    }

    private static bool IsDurablyInactive(QueueItem row) =>
        row.Retirement is not null || row.State == QueueItemState.Cancelled && row.LaunchedAt is null;

    private static QueueWorktreeReferenceObservation DescribeReferences(
        string path,
        IReadOnlyList<QueueItem> rows,
        QueueWorktreeReferenceIndex index)
    {
        var references = new HashSet<string>(index.For(path), StringComparer.Ordinal);
        foreach (var row in rows.Where(row => !IsDurablyInactive(row)))
        {
            references.Add("queue:" + row.Tag);
            if (row.Stage == WorkStage.Continue) references.Add("continuation:" + row.Tag);
        }

        foreach (var row in rows.Where(row => row.RoomDirectory is not null))
        {
            try
            {
                if (ConcurrencyGuard.IsHeld(row.RoomDirectory!)) references.Add("room:" + row.Tag);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new QueueWorktreeReferenceObservation(references.Order(StringComparer.Ordinal).ToList(), false);
            }
        }

        return new QueueWorktreeReferenceObservation(references.Order(StringComparer.Ordinal).ToList(), index.Complete);
    }

    private static async Task<QueueWorktreeGit> ObserveGitAsync(
        string path,
        string? expectedRepository,
        string? expectedBranch,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> repositoryResolver,
        CancellationToken cancellationToken)
    {
        var worktrees = await RunGitAsync(path, ["worktree", "list", "--porcelain"], cancellationToken).ConfigureAwait(false);
        if (!worktrees.Success)
        {
            return new QueueWorktreeGit(
                "unavailable", null, expectedRepository, expectedBranch, null, false, "unknown", null,
                ["git-worktree-probe-unavailable"]);
        }

        var reasons = new List<string>();
        var registered = ParseRegistration(worktrees.Stdout, path, out var head, out var attachedBranch);
        if (!registered) reasons.Add("stale-registration");
        var identity = await repositoryResolver(path, cancellationToken).ConfigureAwait(false);
        var repositoryMatches = expectedRepository is not null && identity?.Value == expectedRepository;
        if (!repositoryMatches) reasons.Add(identity is null ? "repository-probe-unavailable" : "wrong-repository");
        var branchMatches = expectedBranch is not null
            && string.Equals(attachedBranch, "refs/heads/" + expectedBranch, StringComparison.Ordinal);
        if (!branchMatches) reasons.Add(attachedBranch is null ? "detached-head" : "wrong-attached-branch");
        var refHead = expectedBranch is not null
            ? await RunGitAsync(path, ["rev-parse", "--verify", "refs/heads/" + expectedBranch], cancellationToken).ConfigureAwait(false)
            : GitResult.Unavailable;
        if (expectedBranch is not null && !refHead.Success) reasons.Add("expected-branch-probe-unavailable");
        if (refHead.Success && !string.Equals(refHead.Stdout.Trim(), head, StringComparison.Ordinal))
            reasons.Add("expected-branch-head-mismatch");
        var status = await RunGitAsync(
            path, ["status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none"], cancellationToken,
            MaxRawStatusChars)
            .ConfigureAwait(false);
        var cleanliness = status.Success ? string.IsNullOrWhiteSpace(status.Stdout) ? "clean" : "dirty" : "unknown";
        if (!status.Success) reasons.Add("git-status-unavailable");

        var exact = registered && repositoryMatches && branchMatches && refHead.Success
            && string.Equals(refHead.Stdout.Trim(), head, StringComparison.Ordinal);

        return new QueueWorktreeGit(
            exact ? "exact" : "mismatched",
            head,
            expectedRepository,
            expectedBranch,
            status.Success ? status.Stdout.TrimEnd() : null,
            status.Truncated,
            cleanliness,
            identity?.Value,
            reasons);
    }

    private static bool ParseRegistration(string porcelain, string expectedPath, out string? head, out string? branch)
    {
        head = null;
        branch = null;
        string? path = null;
        foreach (var line in porcelain.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                if (path is not null && QueueWorktreeReport.PathComparer.Equals(path, expectedPath)) return head is not null;
                var reportedPath = line["worktree ".Length..];
                path = QueueWorktreeReport.TryFullPath(reportedPath) ?? reportedPath;
                head = null;
                branch = null;
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal)) head = line["HEAD ".Length..];
            else if (line.StartsWith("branch ", StringComparison.Ordinal)) branch = line["branch ".Length..];
        }

        return path is not null && QueueWorktreeReport.PathComparer.Equals(path, expectedPath) && head is not null;
    }

    private static async Task<GitResult> RunGitAsync(
        string path,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        int maxOutputChars = MaxGitProbeChars)
    {
        try
        {
            var start = ChildProcessStartInfo.Create("git", info =>
            {
                info.WorkingDirectory = path;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.StandardOutputEncoding = Encoding.UTF8;
                info.StandardErrorEncoding = Encoding.UTF8;
                // This command promises byte-for-byte read-only Git observation. In particular,
                // `git status` must not refresh the index merely because we asked a question.
                info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            });
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return GitResult.Unavailable;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            var stdout = ReadBoundedAsync(process.StandardOutput, maxOutputChars, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError, 4096, timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return GitResult.Unavailable;
            }

            var output = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            return process.ExitCode == 0 ? new GitResult(true, output.Text, output.Truncated) : GitResult.Unavailable;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return GitResult.Unavailable;
        }
    }

    private static async Task<BoundedRead> ReadBoundedAsync(
        StreamReader reader,
        int maxChars,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder(Math.Min(maxChars, 4096));
        var buffer = new char[4096];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var remaining = maxChars - text.Length;
            if (remaining > 0) text.Append(buffer, 0, Math.Min(remaining, read));
            if (read > remaining) truncated = true;
        }

        return new BoundedRead(text.ToString(), truncated);
    }

    private static SizeObservation ObserveSize(string path)
    {
        if (!System.IO.Directory.Exists(path)) return new SizeObservation(null, "directory-missing");
        try
        {
            long total = 0;
            var files = 0;
            var directories = 0;
            var started = Stopwatch.StartNew();
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.TryPop(out var directory))
            {
                if (++directories > MaxDirectoriesMeasured) return new SizeObservation(null, "directory-limit");
                if (started.Elapsed > ProbeTimeout) return new SizeObservation(null, "time-limit");
                foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(directory))
                {
                    if (started.Elapsed > ProbeTimeout) return new SizeObservation(null, "time-limit");
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        return new SizeObservation(null, "reparse-point");

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    if (++files > MaxFilesMeasured) return new SizeObservation(null, "file-limit");
                    total = checked(total + new FileInfo(entry).Length);
                    if (total > MaxBytesMeasured) return new SizeObservation(null, "byte-limit");
                }
            }

            return new SizeObservation(total, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException or OverflowException)
        {
            return new SizeObservation(null, "probe-failed");
        }
    }

    private sealed record GitResult(bool Success, string Stdout, bool Truncated = false)
    {
        public static readonly GitResult Unavailable = new(false, string.Empty);
    }

    private sealed record BoundedRead(string Text, bool Truncated);
    private sealed record SizeObservation(long? Bytes, string? Reason);

    private static bool IsStrictlyBeneath(string path, string root)
    {
        var relative = System.IO.Path.GetRelativePath(root, path);
        return relative != "."
            && relative != ".."
            && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !System.IO.Path.IsPathRooted(relative);
    }
}

internal sealed record QueueWorktreeReferenceObservation(IReadOnlyList<string> References, bool Complete);

/// <summary>
/// Bounded, read-only snapshot of active room, continuation, and build-lock references. An
/// unreadable active room makes the observation incomplete for every workspace: it might name any
/// one of them, and deletion-adjacent reporting must not turn that uncertainty into a candidate.
/// </summary>
internal sealed record QueueWorktreeReferenceIndex(
    IReadOnlyDictionary<string, IReadOnlyList<string>> References,
    IReadOnlyDictionary<string, IReadOnlyList<string>> BranchReferences,
    bool Complete)
{
    private const int MaxRoomsObserved = 10_000;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public IReadOnlyList<string> For(string path) =>
        References.TryGetValue(path, out var values) ? values : [];

    public IReadOnlyList<string> ForBranch(string branch) =>
        BranchReferences.TryGetValue(branch, out var values) ? values : [];

    public static async Task<QueueWorktreeReferenceIndex> CreateAsync(
        IReadOnlyList<QueueItem> items,
        CancellationToken cancellationToken,
        QueueWorktreeLivenessProbe? probe = null)
    {
        probe ??= QueueWorktreeLivenessProbe.Default;
        var workspaces = items.Select(item => QueueWorktreeReport.TryFullPath(item.Workspace))
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(QueueWorktreeReport.PathComparer)
            .ToList();
        var references = workspaces.ToDictionary(
            path => path,
            _ => new HashSet<string>(StringComparer.Ordinal),
            QueueWorktreeReport.PathComparer);
        var complete = true;
        var branchReferences = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var started = Stopwatch.StartNew();

        try
        {
            var roomCount = 0;
            foreach (var room in probe.EnumerateDirectories(BatonPaths.Rooms))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++roomCount > MaxRoomsObserved || started.Elapsed > ProbeTimeout)
                {
                    complete = false;
                    break;
                }

                bool held;
                try { held = ConcurrencyGuard.IsHeld(room); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    complete = false;
                    continue;
                }
                if (!held) continue;

                IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings;
                try
                {
                    bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                        BatonPaths.RoomBindingsFile(room), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WorkerBindingConfigException or IOException or UnauthorizedAccessException)
                {
                    complete = false;
                    continue;
                }

                var paths = bindings.Values.Select(binding => QueueWorktreeReport.TryFullPath(binding.WorkingDirectory))
                    .Where(path => path is not null)
                    .Cast<string>()
                    .Distinct(QueueWorktreeReport.PathComparer)
                    .ToList();
                if (paths.Count == 0)
                {
                    complete = false;
                    continue;
                }

                var roomReference = "room:" + Path.GetFileName(room);
                var branches = new HashSet<string>(StringComparer.Ordinal);
                string? recordedBranch = null;
                var branchRead = ReadOptionalText(RoomDeliveryBranch.PathFor(room), probe.ReadAllText);
                complete &= branchRead.Complete;
                if (branchRead.Content is not null)
                {
                    recordedBranch = branchRead.Content.Trim();
                    if (recordedBranch.Length > 0) branches.Add(recordedBranch);
                }
                foreach (var bindingPath in paths)
                {
                    var observed = await probe.ReadBranchAsync(bindingPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (observed is null)
                    {
                        complete = false;
                        continue;
                    }
                    branches.Add(observed);
                    if (!string.IsNullOrEmpty(recordedBranch)
                        && !string.Equals(recordedBranch, observed, StringComparison.Ordinal))
                        complete = false;
                }
                foreach (var branch in branches)
                {
                    if (!branchReferences.TryGetValue(branch, out var branchRooms))
                    {
                        branchRooms = new HashSet<string>(StringComparer.Ordinal);
                        branchReferences.Add(branch, branchRooms);
                    }
                    branchRooms.Add(roomReference);
                }

                var continuation = IsContinuation(room, probe.ReadAllText, out var markerComplete);
                complete &= markerComplete;
                foreach (var workspace in workspaces.Where(workspace => paths.Contains(workspace, QueueWorktreeReport.PathComparer)))
                {
                    references[workspace].Add(roomReference);
                    if (continuation) references[workspace].Add("continuation:" + Path.GetFileName(room));
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // A fresh Baton home legitimately has no rooms root yet.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            complete = false;
        }

        complete &= await ObserveBuildLockAsync(
            workspaces, references, branchReferences, probe, cancellationToken).ConfigureAwait(false);
        return new QueueWorktreeReferenceIndex(
            references.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.Ordinal).ToList(),
                QueueWorktreeReport.PathComparer),
            branchReferences.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal),
            complete);
    }

    private static bool IsContinuation(string room, Func<string, string> readAllText, out bool complete)
    {
        var marker = Path.Combine(room, ".baton", BatonPaths.RoomMetadataFileName);
        var markerRead = ReadOptionalText(marker, readAllText);
        complete = markerRead.Complete;
        if (markerRead.Content is null) return false;
        try
        {
            using var document = JsonDocument.Parse(markerRead.Content);
            return document.RootElement.TryGetProperty("ContinuedSessionId", out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            complete = false;
            return false;
        }
    }

    private static async Task<bool> ObserveBuildLockAsync(
        IReadOnlyList<string> workspaces,
        Dictionary<string, HashSet<string>> references,
        Dictionary<string, HashSet<string>> branchReferences,
        QueueWorktreeLivenessProbe probe,
        CancellationToken cancellationToken)
    {
        var lockPath = probe.BuildLockPath ?? Environment.GetEnvironmentVariable("BATON_BUILDLOCK_FILE");
        if (string.IsNullOrWhiteSpace(lockPath)) lockPath = Path.Combine(Path.GetTempPath(), "baton-build.lock");
        var infoPath = lockPath + ".info";
        var infoRead = ReadOptionalText(infoPath, probe.ReadAllText);
        if (infoRead.Content is null) return infoRead.Complete;

        try
        {
            var info = JsonSerializer.Deserialize<BuildLockInfo>(infoRead.Content);
            if (info is null || !probe.IsLiveProcess(info.Pid)) return true;
            var cwd = QueueWorktreeReport.TryFullPath(info.Cwd);
            if (cwd is null)
            {
                return false;
            }

            foreach (var workspace in workspaces.Where(workspace => IsSameOrBeneath(cwd, workspace)))
                references[workspace].Add("build-lock");

            var branch = await probe.ReadBranchAsync(cwd, cancellationToken).ConfigureAwait(false);
            if (branch is null) return false;
            if (!branchReferences.TryGetValue(branch, out var branchLocks))
            {
                branchLocks = new HashSet<string>(StringComparer.Ordinal);
                branchReferences.Add(branch, branchLocks);
            }
            branchLocks.Add("build-lock");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static OptionalTextRead ReadOptionalText(string path, Func<string, string> readAllText)
    {
        try
        {
            return new OptionalTextRead(readAllText(path), Complete: true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new OptionalTextRead(Content: null, Complete: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new OptionalTextRead(Content: null, Complete: false);
        }
    }

    internal static bool IsLiveProcessForProbe(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool IsSameOrBeneath(string path, string root)
    {
        if (QueueWorktreeReport.PathComparer.Equals(path, root)) return true;
        var relative = Path.GetRelativePath(root, path);
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private sealed record BuildLockInfo(
        [property: System.Text.Json.Serialization.JsonPropertyName("pid")] int Pid,
        [property: System.Text.Json.Serialization.JsonPropertyName("cwd")] string? Cwd);

    private sealed record OptionalTextRead(string? Content, bool Complete);
}

internal sealed record QueueWorktreeLivenessProbe(
    Func<string, IEnumerable<string>> EnumerateDirectories,
    Func<string, string> ReadAllText,
    Func<int, bool> IsLiveProcess,
    Func<string, CancellationToken, Task<string?>> ReadBranchAsync,
    string? BuildLockPath = null)
{
    public static QueueWorktreeLivenessProbe Default { get; } = new(
        Directory.EnumerateDirectories,
        File.ReadAllText,
        QueueWorktreeReferenceIndex.IsLiveProcessForProbe,
        WorkspaceHead.TryReadBranchAsync);
}
