using System.ComponentModel;
using System.Diagnostics;
using Baton.Accounting;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// Probes git for the two strings <see cref="RepositoryIdentity.From"/> derives an identity from — the
/// <c>origin</c> remote URL and the git <i>common</i> directory. Kept in the CLI, not in the engine,
/// for the same reason <see cref="WorkspaceHead"/> is: the engine stays git-agnostic, and
/// <see cref="RepositoryIdentity"/> itself is pure string work with no process in it.
/// </summary>
/// <remarks>
/// <b>Never throws, never blocks.</b> A missing git, a non-repository directory, or a hung probe all
/// resolve to <see langword="null"/>, which the settle site reads as "this work has no repository
/// identity" and records nothing — the same fail-open posture <see cref="CostLedgerStore"/>'s own
/// remarks state for the write itself, not restated here.
/// </remarks>
internal static class RepositoryIdentityResolver
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The identity of the repository <paramref name="roomDirectoryPath"/>'s work was done in, resolved
    /// from that room's own recorded <see cref="RoomRegistryEntry.ProjectRoot"/>.
    /// </summary>
    /// <remarks>
    /// <b>Not the process's working directory, deliberately.</b> A room lives under
    /// <c>{BatonPaths.Root}/rooms</c> and carries no git of its own, so the workspace has to come from
    /// somewhere else — and the ambient CWD is the wrong somewhere twice over, in ways that produce no
    /// signal. A <c>baton</c> invoked from outside any repository (a conductor, the daemon, an install
    /// directory) would resolve to nothing and silently contribute no rows at all; a session sitting in
    /// one checkout while dispatching work into another would key every row to the wrong repository,
    /// which is a well-formed row with a wrong join key rather than a visible failure. The registry
    /// records the project root at registration time, per room, which is the fact this needs.
    /// Registration is itself fail-open (<c>RunCommand.RegisterRoomAsync</c>), so a room with no entry
    /// is reachable and falls back to the working directory rather than losing the row.
    /// <para>
    /// <b>The fallback is narrower here than in <c>LedgerBackfillCommand.ResolveRoomRepositoryAsync</c>,
    /// on purpose</b>, and the two are deliberately not one method: this one reaches for the working
    /// directory only when the room has NO recorded root at all. One that is recorded but unresolvable
    /// stops here with no identity, so the settle costs itself a row rather than keying one to whatever
    /// checkout the process happened to be started in. Why a backfill takes the opposite side of that
    /// trade is on <see cref="RepositoryIdentitySource.WorkingDirectory"/> and in the register it cites.
    /// </para>
    /// <para>
    /// <b>Returns which rung answered</b> (#1931 re-review MEDIUM), so the row this keys can carry
    /// <see cref="CostLedgerEntry.IdentitySource"/> rather than leaving the ledger's dominant population
    /// unable to state its own provenance. The <see cref="RepositoryIdentitySource"/> is what the lookup
    /// USED, so it is meaningful even when the identity is <see langword="null"/> and no row follows.
    /// </para>
    /// </remarks>
    public static async Task<(RepositoryIdentity? Identity, RepositoryIdentitySource Source)> TryResolveForRoomAsync(
        string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var registrations = await RoomRegistryStore
            .ReadDistinctByRoomAsync(BatonPaths.RoomRegistryFile, cancellationToken).ConfigureAwait(false);

        return await TryResolveForRoomAsync(roomDirectoryPath, registrations, TryResolveAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc cref="TryResolveForRoomAsync(string, CancellationToken)"/>
    /// <remarks>
    /// The seam the public overload above is a thin wrapper over: it takes the registry rows and the git
    /// probe as arguments so both rungs can be exercised without a registry file on disk or a git spawn
    /// — the same reason <c>LedgerBackfillCommand</c> owns a <c>RepositoryProbe</c> delegate. A test that
    /// stubbed only the probe would still read the operator's real registry.
    /// </remarks>
    internal static async Task<(RepositoryIdentity? Identity, RepositoryIdentitySource Source)> TryResolveForRoomAsync(
        string roomDirectoryPath,
        IReadOnlyList<RoomRegistryEntry> registrations,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> probe,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var recordedRoomPath = BatonPaths.RecordKey(roomDirectoryPath);
        var projectRoot = registrations
            .FirstOrDefault(entry => BatonPaths.RecordKeyComparer.Equals(entry.RoomPath, recordedRoomPath))
            ?.ProjectRoot;

        return projectRoot is { Length: > 0 }
            ? (await probe(projectRoot, cancellationToken).ConfigureAwait(false), RepositoryIdentitySource.RecordedRoot)
            : (await probe(Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false),
                RepositoryIdentitySource.WorkingDirectory);
    }

    /// <summary>
    /// The canonical identity of the repository at <paramref name="workingDirectory"/>, or
    /// <see langword="null"/> when there is none to be had.
    /// </summary>
    public static async Task<RepositoryIdentity?> TryResolveAsync(
        string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        var originUrl = await RunGitAsync(workingDirectory, cancellationToken, "config", "--get", "remote.origin.url")
            .ConfigureAwait(false);

        // --git-common-dir is what makes every worktree of one repository share one identity: a linked
        // worktree's own `.git` is a file pointing back here, and `--git-dir` would give the
        // per-worktree path instead, fragmenting the ledger one file per checkout.
        var commonDir = await RunGitAsync(workingDirectory, cancellationToken, "rev-parse", "--path-format=absolute", "--git-common-dir")
            .ConfigureAwait(false);

        return RepositoryIdentity.From(originUrl, commonDir);
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="path"/> is an existing directory that is <b>itself</b>
    /// the root of a git work tree — not merely a directory somewhere inside one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a filesystem check and not <c>git rev-parse --show-toplevel</c>.</b> The predicate
    /// is equivalent: <c>--show-toplevel</c> walks up from its working directory to the first directory
    /// holding a <c>.git</c> entry, so "git's discovered root is <paramref name="path"/>" and
    /// "<paramref name="path"/> holds a <c>.git</c> entry" are the same statement. The difference is
    /// cost, and it decides the design — <see cref="Baton.Memory.MemoryRootPath.Resolve"/> applies this
    /// to each reading of a decoded directory name, an enumeration capped at
    /// <see cref="Baton.Memory.MemoryRootPath.MaxReadingsEnumerated"/>, so a process spawn per candidate would put
    /// thousands of ten-second-timeout git invocations behind one audited root.
    /// </para>
    /// <para>
    /// <b>What it actually checks, stated narrowly</b> (#1908 re-review low 1, correcting a remark that
    /// claimed more): the entry named <c>.git</c> is not validated at all — not opened, not parsed, not
    /// followed. A <c>.git</c> file whose <c>gitdir:</c> pointer is broken and a stray non-repository
    /// directory named <c>.git</c> both satisfy this predicate. <b>A bare repository does not</b>: it
    /// has no <c>.git</c> entry, so it fails here rather than passing and being rejected later.
    /// </para>
    /// <para>
    /// <b>What that costs, and why it is still the right predicate.</b> The value this buys is the
    /// direction it was added for: a decoded reading that is merely a directory <i>inside</i> a checkout
    /// no longer counts as having found that checkout (see
    /// <see cref="Baton.Memory.MemoryRootPath.Resolve"/>'s tie-break comment). What it does not buy is a
    /// guarantee about what happens to the two false positives above — <see cref="TryResolveAsync"/>
    /// probes git at that path, and git discovers a repository by walking <b>up</b>, so a stray
    /// <c>.git</c> directory planted inside a real checkout resolves to that checkout's identity rather
    /// than to nothing. Nothing here has measured which of those it does in each case, so nothing here
    /// claims it.
    /// </para>
    /// </remarks>
    public static bool IsWorkTreeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        var gitEntry = Path.Combine(path, ".git");
        return Directory.Exists(gitEntry) || File.Exists(gitEntry);
    }

    /// <summary>Stdout of a git invocation, trimmed — or null on any non-zero exit, missing git, or timeout.</summary>
    private static async Task<string?> RunGitAsync(
        string workingDirectory, CancellationToken cancellationToken, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // #1883 review F11: `using` disposes the HANDLE without ending the child, so a git that
                // outran the probe timeout would outlive this call. Kill the tree -- git shells out to
                // its own helpers (credential, ssh) -- then report "no identity" as the catch below does.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
                {
                    // Already exited between the timeout and the kill, or the OS refused: nothing left to do.
                }

                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return null;
            }

            var trimmed = stdout.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
        {
            // Every one of these means "no identity available", which is a legitimate answer here, not
            // a failure to report: git absent, the directory gone, or the probe outran its timeout.
            return null;
        }
    }
}
