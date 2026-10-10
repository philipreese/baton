using System.Diagnostics;
using Baton.Mutation;

namespace Baton.Tests.TestSupport;

/// <summary>
/// Temp-workspace plumbing shared by the <c>.baton/verify</c> declaration tests (#1702/#1708) — the
/// resolver's own unit tests and the non-parallel environment-scrub test, which live in different
/// classes because only one of them may mutate process-wide state.
/// </summary>
public static class VerifyDeclarationWorkspace
{
    public static string CreateTemp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"verify-resolver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static void WriteDeclaration(string workspace, string content)
    {
        var batonDir = Path.Combine(workspace, ".baton");
        Directory.CreateDirectory(batonDir);
        File.WriteAllText(Path.Combine(batonDir, "verify"), content);
    }

    /// <summary>
    /// What a PLAIN <c>git show HEAD:./.baton/verify</c> returns from <paramref name="workspace"/>, run
    /// straight through <see cref="Process"/> rather than through the code under test. This is the
    /// control arm for #1708 M1's tests: it establishes that the branch tip really does hold the line the
    /// resolver is being asserted NOT to return, so a resolver that read nothing at all could not pass.
    /// </summary>
    public static string? ShowAtHead(string workspace)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("show");
        startInfo.ArgumentList.Add($"HEAD:./{VerifyCommandResolver.RepoDeclarationRelativePath}");

        using var process = global::Baton.Core.ProcessLaunch.Start(startInfo)
            ?? throw new InvalidOperationException("Could not spawn git show.");
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? stdout.Trim() : null;
    }
}
