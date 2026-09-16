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
    public static async Task<QueueWorktreeReport> CreateAsync(
        IReadOnlyList<QueueItem> items,
        string? root,
        CancellationToken cancellationToken)
    {
        var resolvedRoot = TryFullPath(root);
        var activeReferences = await QueueWorktreeReferenceIndex.CreateAsync(items, cancellationToken).ConfigureAwait(false);
        var groups = items.GroupBy(item => TryFullPath(item.Workspace) ?? item.Workspace, PathComparer)
            .OrderBy(group => group.Key, PathComparer);
        var entries = new List<QueueWorktreeEntry>();
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(await QueueWorktreeEntry.CreateAsync(
                    group.Key, resolvedRoot, group.ToList(), activeReferences, cancellationToken)
                .ConfigureAwait(false));
        }

        return new QueueWorktreeReport(resolvedRoot, entries);
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public string ToText() => string.Join(Environment.NewLine, new[]
    {
        $"Configured worktree root: {WorktreeRoot ?? "unknown"}",
        "Static candidate is not deletion authorization.",
    }.Concat(Workspaces.Select(entry =>
        $"{entry.Path}: {entry.Classification} ({string.Join(", ", entry.ReasonCodes)})\n"
        + $"  origin: {entry.Origin}; beneath root: {entry.BeneathConfiguredRoot}; directory: {entry.Directory}\n"
        + $"  rows: {string.Join(", ", entry.Rows.Select(row => $"{row.Tag}/{row.State}/{row.Stage ?? "none"}"))}\n"
        + $"  git: {entry.Git.Registration}; head: {entry.Git.Head ?? "unknown"}; expected branch: {entry.Git.ExpectedBranch ?? "unknown"}; raw status: {entry.Git.RawStatus ?? "unknown"}\n"
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
    string? ExpectedBranch,
    string? RawStatus,
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
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public static async Task<QueueWorktreeEntry> CreateAsync(
        string path,
        string? root,
        IReadOnlyList<QueueItem> rows,
        QueueWorktreeReferenceIndex activeReferenceIndex,
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

        var size = TrySize(path, out var sizeReason);
        var git = exists
            ? await ObserveGitAsync(path, repositories.Count == 1 ? repositories[0] : null,
                branches.Count == 1 ? branches[0] : null, cancellationToken).ConfigureAwait(false)
            : new QueueWorktreeGit(
                "unavailable", null, branches.Count == 1 ? branches[0] : null, null, "unknown", null,
                ["git-worktree-probe-unavailable"]);

        reasons.AddRange(git.ReasonCodes);
        if (git.SubstantiveCleanliness == "dirty") reasons.Add("substantive-uncommitted-content");
        if (size is null) reasons.Add("size-observation-unavailable");

        var candidate = reasons.Count == 0;
        var unavailable = !exists
            || origins.Contains(WorkspaceOrigins.Unknown, StringComparer.Ordinal)
            || !referenceObservation.Complete
            || git.Registration == "unavailable"
            || git.SubstantiveCleanliness == "unknown"
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
        CancellationToken cancellationToken)
    {
        var worktrees = await RunGitAsync(path, ["worktree", "list", "--porcelain"], cancellationToken).ConfigureAwait(false);
        if (!worktrees.Success)
        {
            return new QueueWorktreeGit(
                "unavailable", null, expectedBranch, null, "unknown", null,
                ["git-worktree-probe-unavailable"]);
        }

        var reasons = new List<string>();
        var registered = ParseRegistration(worktrees.Stdout, path, out var head, out var attachedBranch);
        if (!registered) reasons.Add("stale-registration");
        var identity = await RepositoryIdentityResolver.TryResolveAsync(path, cancellationToken).ConfigureAwait(false);
        var repositoryMatches = expectedRepository is not null && identity?.Value == expectedRepository;
        if (!repositoryMatches) reasons.Add(identity is null ? "repository-probe-unavailable" : "wrong-repository");
        var branchMatches = expectedBranch is not null
            && string.Equals(attachedBranch, "refs/heads/" + expectedBranch, StringComparison.Ordinal);
        if (!branchMatches) reasons.Add(attachedBranch is null ? "detached-head" : "wrong-attached-branch");
        var refHead = expectedBranch is not null
            ? await RunGitAsync(path, ["rev-parse", "--verify", "refs/heads/" + expectedBranch], cancellationToken).ConfigureAwait(false)
            : GitResult.Unavailable;
        if (expectedBranch is not null && !refHead.Success) reasons.Add("expected-branch-ref-missing");
        if (refHead.Success && !string.Equals(refHead.Stdout.Trim(), head, StringComparison.Ordinal))
            reasons.Add("expected-branch-head-mismatch");
        var status = await RunGitAsync(
            path, ["status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none"], cancellationToken)
            .ConfigureAwait(false);
        var cleanliness = status.Success ? string.IsNullOrWhiteSpace(status.Stdout) ? "clean" : "dirty" : "unknown";
        if (!status.Success) reasons.Add("git-status-unavailable");

        var exact = registered && repositoryMatches && branchMatches && refHead.Success
            && string.Equals(refHead.Stdout.Trim(), head, StringComparison.Ordinal);

        return new QueueWorktreeGit(
            exact ? "exact" : "mismatched",
            head,
            expectedBranch,
            status.Success ? status.Stdout.TrimEnd() : null,
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

    private static async Task<GitResult> RunGitAsync(string path, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return GitResult.Unavailable;
            }

            return process.ExitCode == 0
                ? new GitResult(true, await stdout.ConfigureAwait(false))
                : GitResult.Unavailable;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return GitResult.Unavailable;
        }
    }

    private static long? TrySize(string path, out string? reason)
    {
        reason = null;
        if (!System.IO.Directory.Exists(path)) { reason = "directory-missing"; return null; }
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
                if (++directories > MaxDirectoriesMeasured) { reason = "directory-limit"; return null; }
                if (started.Elapsed > ProbeTimeout) { reason = "time-limit"; return null; }
                foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(directory))
                {
                    if (started.Elapsed > ProbeTimeout) { reason = "time-limit"; return null; }
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        reason = "reparse-point";
                        return null;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    if (++files > MaxFilesMeasured) { reason = "file-limit"; return null; }
                    total = checked(total + new FileInfo(entry).Length);
                    if (total > MaxBytesMeasured) { reason = "byte-limit"; return null; }
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException or OverflowException)
        {
            reason = "probe-failed";
            return null;
        }
    }

    private sealed record GitResult(bool Success, string Stdout)
    {
        public static readonly GitResult Unavailable = new(false, string.Empty);
    }

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
    bool Complete)
{
    private const int MaxRoomsObserved = 10_000;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public IReadOnlyList<string> For(string path) =>
        References.TryGetValue(path, out var values) ? values : [];

    public static async Task<QueueWorktreeReferenceIndex> CreateAsync(
        IReadOnlyList<QueueItem> items,
        CancellationToken cancellationToken)
    {
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
        var started = Stopwatch.StartNew();

        try
        {
            var roomCount = 0;
            if (Directory.Exists(BatonPaths.Rooms))
            {
                foreach (var room in Directory.EnumerateDirectories(BatonPaths.Rooms))
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

                    var continuation = IsContinuation(room, out var markerComplete);
                    complete &= markerComplete;
                    foreach (var workspace in workspaces.Where(workspace => paths.Contains(workspace, QueueWorktreeReport.PathComparer)))
                    {
                        references[workspace].Add("room:" + Path.GetFileName(room));
                        if (continuation) references[workspace].Add("continuation:" + Path.GetFileName(room));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            complete = false;
        }

        ObserveBuildLock(workspaces, references, ref complete);
        return new QueueWorktreeReferenceIndex(
            references.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.Ordinal).ToList(),
                QueueWorktreeReport.PathComparer),
            complete);
    }

    private static bool IsContinuation(string room, out bool complete)
    {
        complete = true;
        var marker = Path.Combine(room, ".baton", BatonPaths.RoomMetadataFileName);
        if (!File.Exists(marker)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
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

    private static void ObserveBuildLock(
        IReadOnlyList<string> workspaces,
        Dictionary<string, HashSet<string>> references,
        ref bool complete)
    {
        var lockPath = Environment.GetEnvironmentVariable("BATON_BUILDLOCK_FILE");
        if (string.IsNullOrWhiteSpace(lockPath)) lockPath = Path.Combine(Path.GetTempPath(), "baton-build.lock");
        var infoPath = lockPath + ".info";
        if (!File.Exists(infoPath)) return;

        try
        {
            var info = JsonSerializer.Deserialize<BuildLockInfo>(File.ReadAllText(infoPath));
            if (info is null || !IsLiveProcess(info.Pid)) return;
            var cwd = QueueWorktreeReport.TryFullPath(info.Cwd);
            if (cwd is null)
            {
                complete = false;
                return;
            }

            foreach (var workspace in workspaces.Where(workspace => IsSameOrBeneath(cwd, workspace)))
                references[workspace].Add("build-lock");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            complete = false;
        }
    }

    private static bool IsLiveProcess(int pid)
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
}
