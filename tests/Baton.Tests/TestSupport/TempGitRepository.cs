using System.Diagnostics;

namespace Baton.Tests.TestSupport;

/// <summary>
/// A throwaway git repository under a temp directory, for tests that need the distinction between what
/// a workspace has COMMITTED and what its working tree currently holds — #1708 H1's verify-declaration
/// boundary. Deliberately not a general git helper: it exists so a committed-vs-working-tree assertion
/// runs against real git rather than a fake, which is the only instrument that can falsify the claim.
/// </summary>
public static class TempGitRepository
{
    /// <summary>Initializes <paramref name="path"/> as a git repo and commits everything already in it.</summary>
    public static void InitWithEverythingCommitted(string path)
    {
        Run(path, "init");
        Run(path, "config", "user.name", "Test");
        Run(path, "config", "user.email", "test@test.com");
        Run(path, "config", "commit.gpgsign", "false");
        CommitAll(path, "initial");
    }

    public static void CommitAll(string path, string message)
    {
        Run(path, "add", "-A");
        Run(path, "commit", "--allow-empty", "-m", message);
    }

    /// <summary>
    /// Points <c>refs/remotes/origin/main</c> at the current <c>HEAD</c>, making everything committed so
    /// far the REVIEWED baseline #1708 M1 grades against and everything after it a lane's own,
    /// unreviewed branch work. Written as a ref rather than by adding a real remote and fetching: the
    /// resolver resolves <c>origin/main</c> through the ordinary ref lookup, so a real remote would add
    /// network and a second repository without changing what is under test.
    /// </summary>
    public static void SetReviewedBaselineAtHead(string path) =>
        Run(path, "update-ref", "refs/remotes/origin/main", "HEAD");

    /// <summary>
    /// #1718: tags <c>HEAD</c> with a ref name equal to its own full 40-hex object id, which makes real
    /// git print <c>warning: refname '&lt;sha&gt;' is ambiguous.</c> (<c>advice.objectNameWarning</c>) to
    /// stderr on any later <c>&lt;sha&gt;:&lt;path&gt;</c> revision spec resolving that same string — while
    /// still exiting 0; measured on this machine's git 2.54.0.windows.1. What this fixture is FOR is
    /// <see cref="Baton.Tests.Mutation.VerifyCommandResolverTests.ReadCommittedRepoDeclarationAsync_ignores_a_stderr_warning_from_a_git_that_exits_zero"/>'s
    /// own doc comment, not restated here. Returns the sha so a caller can build the same revision string.
    /// </summary>
    public static string TagHeadWithItsOwnSha(string path)
    {
        var sha = RunCapturingOutput(path, "rev-parse", "HEAD").Trim();
        Run(path, "tag", sha);
        return sha;
    }

    /// <summary>
    /// #1788: a bare repository standing in for a real GitHub remote -- <c>DeliveryVerifier</c>'s own
    /// tests need a real <c>origin</c> a workspace can actually push to and fetch from, not the
    /// <see cref="SetReviewedBaselineAtHead"/> ref-only stand-in (that shortcut is fine for a read that
    /// only ever resolves <c>origin/main</c> locally; a push/fetch round-trip needs a real remote).
    /// </summary>
    public static string InitBareRepository(string path)
    {
        Directory.CreateDirectory(path);
        Run(path, "init", "--bare");
        return path;
    }

    public static void AddRemote(string path, string name, string url) => Run(path, "remote", "add", name, url);

    public static void CreateAndCheckoutBranch(string path, string branch) => Run(path, "checkout", "-b", branch);

    public static void Push(string path, string remote, string refspec) => Run(path, "push", remote, refspec);

    public static string Head(string path) => RunCapturingOutput(path, "rev-parse", "HEAD").Trim();

    private static void Run(string workingDirectory, params string[] args) =>
        RunCapturingOutput(workingDirectory, args);

    private static string RunCapturingOutput(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not spawn git {string.Join(' ', args)}.");
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            // Never swallowed: a silently failed `git commit` would leave the "committed" side of every
            // assertion below empty, and the test would then pass for the wrong reason.
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} exited {process.ExitCode}: {process.StandardError.ReadToEnd()}");
        }

        return stdout;
    }
}
