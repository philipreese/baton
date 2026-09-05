using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Baton.Accounting;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// #1901 C1 items 1 and 3: resolves, at settle, what each of a room's workers actually delivered —
/// the issue its branch names, the PR open for that branch, and the shape of the diff it pushed —
/// and hands the result to <see cref="CostLedgerStore.BuildEntries"/> as plain values.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lives here, not in <c>Baton.Accounting</c>, for the same two reasons
/// <see cref="RunwayOverrideReasons"/> does:</b> the room's workspace is only knowable from a
/// <c>Baton.Vendors</c> bindings record the engine layer holds no reference to, and the engine stays
/// git-agnostic. <see cref="WorkspaceDelivery"/> is the value type that crosses the boundary.
/// </para>
/// <para>
/// <b>Fails open at every step, and absence is never zero.</b> A missing bindings file, a workspace
/// directory that no longer exists (a #669 worktree torn down before this runs — <c>Program.cs</c>
/// tears down on Terminal, which is before the cost-ledger append), a non-git workspace, a detached
/// HEAD, a <c>gh</c> that is missing or unauthenticated, a network that is down: each costs exactly
/// the facts it would have produced and nothing else. The row records what was resolved, and a
/// consumer reading an absent <c>pr</c> knows only that no PR was found — <see cref="CostLedgerEntry.PullRequest"/>'s
/// own doc states that reading.
/// </para>
/// <para>
/// <b>One probe per distinct workspace directory, not per worker.</b> A composed template's phases
/// usually share one workspace; spawning <c>git</c> and <c>gh</c> once per phase would multiply the
/// settle-time cost for identical answers.
/// </para>
/// </remarks>
public static class WorkspaceDeliveryProbe
{
    /// <summary>
    /// The base ref every diff shape is measured against. Hardcoded (#1901 C1's own scoping): this
    /// repo's trunk. A workspace whose work is not based on <c>origin/main</c> — a stacked branch, a
    /// fork with a differently-named default — measures against the wrong base, and the honest reading
    /// of its numbers is "diff from origin/main", which is what the field names say. Generalising the
    /// base ref is deliberately not in this phase.
    /// </summary>
    internal const string BaseRef = "origin/main";

    /// <summary>The path prefix a changed file must sit under to count towards <see cref="CostLedgerEntry.TestFilesChanged"/>.</summary>
    internal const string TestPathPrefix = "tests/";

    /// <summary>
    /// The spawn seam (#1901: "no network in unit tests — inject the lookup"). Shaped like
    /// <see cref="Daemon.IGhCliRunner"/>'s, widened by a <paramref name="program"/> so one seam covers
    /// both the <c>git</c> reads and the <c>gh</c> lookup; a test supplies canned output for both
    /// without a git repository, a <c>gh</c> install, or a network.
    /// </summary>
    internal delegate Task<Daemon.GhCliResult> CommandRunner(
        string program, string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken);

    /// <summary>
    /// Worker name to what that worker's workspace delivered. Empty when the room has no bindings file
    /// or none of them names a readable workspace — never an exception, per the fail-open contract above.
    /// </summary>
    public static Task<IReadOnlyDictionary<string, WorkspaceDelivery>> ReadForRoomAsync(
        string roomDirectoryPath, CancellationToken cancellationToken = default) =>
        ReadForRoomAsync(roomDirectoryPath, SpawnAsync, cancellationToken);

    /// <inheritdoc cref="ReadForRoomAsync(string, CancellationToken)"/>
    internal static async Task<IReadOnlyDictionary<string, WorkspaceDelivery>> ReadForRoomAsync(
        string roomDirectoryPath, CommandRunner runner, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(runner);

        var empty = new Dictionary<string, WorkspaceDelivery>(StringComparer.Ordinal);

        var bindingsFilePath = Path.Combine(roomDirectoryPath, "bindings.json");
        if (!File.Exists(bindingsFilePath))
        {
            return empty;
        }

        IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings;
        try
        {
            bindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsFilePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BatonFlowException)
        {
            Console.Error.WriteLine(
                $"Could not read '{bindingsFilePath}' for delivery attribution: {ex.Message} "
                + "The cost ledger rows for this room carry no issue, pr or diff shape.");
            return empty;
        }

        var byDirectory = new Dictionary<string, WorkspaceDelivery>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, WorkspaceDelivery>(StringComparer.Ordinal);

        foreach (var (worker, entry) in bindings)
        {
            if (entry.WorkingDirectory is not { Length: > 0 } directory || !Directory.Exists(directory))
            {
                continue;
            }

            if (!byDirectory.TryGetValue(directory, out var delivery))
            {
                delivery = await ProbeAsync(directory, runner, cancellationToken).ConfigureAwait(false);
                byDirectory[directory] = delivery;
            }

            result[worker] = delivery;
        }

        return result;
    }

    /// <summary>
    /// One workspace's facts. The branch is resolved first because everything else keys on it: with no
    /// named branch (a detached HEAD, or not a git repository at all) there is no issue to derive and
    /// no head to ask <c>gh</c> about, and the diff shape is skipped too rather than measured against
    /// whatever <c>HEAD</c> happens to be.
    /// </summary>
    private static async Task<WorkspaceDelivery> ProbeAsync(
        string directory, CommandRunner runner, CancellationToken cancellationToken)
    {
        var branchResult = await runner("git", directory, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken)
            .ConfigureAwait(false);
        if (!branchResult.Started || branchResult.ExitCode != 0)
        {
            return new WorkspaceDelivery();
        }

        var branch = branchResult.Stdout.Trim();
        if (branch.Length == 0 || string.Equals(branch, "HEAD", StringComparison.Ordinal))
        {
            return new WorkspaceDelivery();
        }

        var diff = await ReadDiffShapeAsync(directory, runner, cancellationToken).ConfigureAwait(false);

        return new WorkspaceDelivery(
            Issue: TryReadIssueNumber(branch),
            PullRequest: await ReadPullRequestNumberAsync(directory, branch, runner, cancellationToken).ConfigureAwait(false),
            FilesChanged: diff?.Files,
            Additions: diff?.Additions,
            Deletions: diff?.Deletions,
            TestFilesChanged: diff?.TestFiles);
    }

    /// <summary>
    /// The issue number a branch created by <c>gh issue develop &lt;n&gt;</c> carries as its leading
    /// <c>&lt;n&gt;-</c> — <c>1901-lane</c> is issue <c>1901</c>. Returns <see langword="null"/> for
    /// every other spelling, including a bare number with no separator (<c>1901</c> alone is far more
    /// likely a branch someone named after something else than an issue reference).
    /// </summary>
    internal static string? TryReadIssueNumber(string branch)
    {
        var separator = branch.IndexOf('-', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return null;
        }

        var candidate = branch[..separator];
        return candidate.All(char.IsAsciiDigit) ? candidate : null;
    }

    /// <summary>
    /// The number of the open PR for <paramref name="branch"/>, from the same
    /// <c>gh pr list --head &lt;branch&gt; --json number</c> <c>Baton.Mutation.DeliveryVerifier</c>
    /// already asks (one question, one spelling). <see langword="null"/> whenever <c>gh</c> did not run,
    /// did not succeed, returned no PR, or returned output that does not parse — never a fabricated
    /// number and never a fabricated absence beyond "none was found".
    /// </summary>
    private static async Task<string?> ReadPullRequestNumberAsync(
        string directory, string branch, CommandRunner runner, CancellationToken cancellationToken)
    {
        var result = await runner("gh", directory, ["pr", "list", "--head", branch, "--json", "number"], cancellationToken)
            .ConfigureAwait(false);
        if (!result.Started || result.ExitCode != 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("number", out var number)
                    && number.ValueKind == JsonValueKind.Number)
                {
                    // The first of several open PRs for one branch, which `gh` orders newest-first.
                    // More than one is not a shape this ledger tries to represent -- one row, one PR.
                    return number.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private readonly record struct DiffShape(int Files, long Additions, long Deletions, int TestFiles);

    /// <summary>
    /// <c>git diff --numstat origin/main...HEAD</c>, reduced. <b>One spawn, four numbers</b> — the
    /// per-file form already carries every figure <c>--shortstat</c> would summarise plus the paths
    /// <c>testFilesChanged</c> needs, so deriving the same totals twice buys nothing.
    /// <para>
    /// <see langword="null"/> when the command did not run or did not succeed — which is what a
    /// workspace that never pushed looks like, because <c>origin/main</c> is only present once the
    /// repository has fetched it. A binary file's row is <c>-\t-\tpath</c>: it counts towards
    /// <see cref="DiffShape.Files"/> and towards neither line total, because git reports no line counts
    /// for one and inventing zeros there would understate a real change.
    /// </para>
    /// </summary>
    private static async Task<DiffShape?> ReadDiffShapeAsync(
        string directory, CommandRunner runner, CancellationToken cancellationToken)
    {
        var result = await runner("git", directory, ["diff", "--numstat", $"{BaseRef}...HEAD"], cancellationToken)
            .ConfigureAwait(false);
        if (!result.Started || result.ExitCode != 0)
        {
            return null;
        }

        var files = 0;
        var testFiles = 0;
        long additions = 0;
        long deletions = 0;

        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split('\t');
            if (columns.Length < 3)
            {
                continue;
            }

            files++;
            if (long.TryParse(columns[0], out var added))
            {
                additions += added;
            }

            if (long.TryParse(columns[1], out var removed))
            {
                deletions += removed;
            }

            // Forward slashes always: --numstat writes repo-relative paths in git's own spelling, which
            // is POSIX-separated even on Windows.
            if (columns[2].StartsWith(TestPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                testFiles++;
            }
        }

        return new DiffShape(files, additions, deletions, testFiles);
    }

    /// <summary>
    /// The production spawn — the same shape <see cref="Daemon.GhCliRunner"/> uses (redirected output,
    /// no window, a <see cref="Win32Exception"/> catch for "not on PATH"), widened to any program.
    /// Credential Isolation: it shells out to whatever <c>git</c>/<c>gh</c> is already authenticated on
    /// the host and touches no credential of its own.
    /// </summary>
    private static async Task<Daemon.GhCliResult> SpawnAsync(
        string program, string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(program)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Non-interactive hardening, the same pair Baton.Mutation.DeliveryVerifier applies to its own
        // network-touching git spawns: without it a host whose credential helper needs a refresh can
        // block on an OS credential prompt that reads no stdin, turning an optional accounting read
        // into a hang at settle. `gh` reads its own non-interactive mode implicitly when stdout is not
        // a terminal, which is always true of a spawned child here.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "never";

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{program} did not start.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new Daemon.GhCliResult(Started: false, ExitCode: -1, Stdout: string.Empty, Stderr: $"{program} was not found on PATH.");
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new Daemon.GhCliResult(
                Started: true,
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
    }
}
