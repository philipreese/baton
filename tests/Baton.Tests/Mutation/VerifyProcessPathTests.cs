using Baton.Mutation;
using Baton.Tests.Shared;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// Coverage for <see cref="VerifyProcessPath"/> (#2098) against a fake Git for Windows layout in a
/// temp directory — the contract it implements is spec/baton.md §3's, not restated here. The
/// discriminating arms are the ones where the PATH must stay untouched: <c>sh</c> already present,
/// no <c>git</c> at all, and a <c>git.exe</c> with no <c>usr\bin\sh.exe</c> above it.
/// </summary>
public sealed class VerifyProcessPathTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "baton-verify-path-" + Guid.NewGuid().ToString("N"));

    public VerifyProcessPathTests()
    {
        Directory.CreateDirectory(root);
    }

    public void Dispose() => DirectoryCleanup.DeleteRecursively(root);

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Touch(string directory, string fileName) => File.WriteAllText(Path.Combine(directory, fileName), string.Empty);

    [Fact]
    public void Prepends_Gits_usr_bin_when_sh_is_not_on_the_ambient_PATH()
    {
        // The measured daemon shape: `C:\Program Files\Git\cmd` is on PATH (the registry's machine
        // PATH), `C:\Program Files\Git\usr\bin` is not.
        var gitCmd = Dir("Git", "cmd");
        Touch(gitCmd, "git.exe");
        var usrBin = Dir("Git", "usr", "bin");
        Touch(usrBin, "sh.exe");
        var other = Dir("other");
        var ambient = string.Join(Path.PathSeparator, gitCmd, other);

        var composed = VerifyProcessPath.Compose(ambient);

        Assert.Equal(string.Join(Path.PathSeparator, usrBin, gitCmd, other), composed);
    }

    [Fact]
    public void Resolves_from_a_git_exe_two_levels_below_the_install_root()
    {
        // `mingw64\bin\git.exe` is the other real Git for Windows placement.
        var gitBin = Dir("Git", "mingw64", "bin");
        Touch(gitBin, "git.exe");
        var usrBin = Dir("Git", "usr", "bin");
        Touch(usrBin, "sh.exe");

        var composed = VerifyProcessPath.Compose(gitBin);

        Assert.Equal(string.Join(Path.PathSeparator, usrBin, gitBin), composed);
    }

    [Fact]
    public void Leaves_the_PATH_alone_when_sh_already_resolves()
    {
        var gitCmd = Dir("Git", "cmd");
        Touch(gitCmd, "git.exe");
        Touch(Dir("Git", "usr", "bin"), "sh.exe");
        var shellDir = Dir("shell");
        Touch(shellDir, "sh.exe");
        var ambient = string.Join(Path.PathSeparator, shellDir, gitCmd);

        Assert.Equal(ambient, VerifyProcessPath.Compose(ambient));
    }

    [Fact]
    public void Leaves_the_PATH_alone_when_no_git_is_on_it()
    {
        var ambient = string.Join(Path.PathSeparator, Dir("a"), Dir("b"));

        Assert.Equal(ambient, VerifyProcessPath.Compose(ambient));
    }

    [Fact]
    public void Leaves_the_PATH_alone_when_the_git_on_it_has_no_usr_bin_sh_above_it()
    {
        // Nothing to add here, and a directory that exists but holds no sh.exe must not be guessed in.
        var gitCmd = Dir("Git", "cmd");
        Touch(gitCmd, "git.exe");
        Dir("Git", "usr", "bin");

        Assert.Equal(gitCmd, VerifyProcessPath.Compose(gitCmd));
    }

    [Fact]
    public void A_relative_PATH_entry_never_counts_as_holding_git_or_sh()
    {
        // Relative entries resolve against the spawn's cwd -- the worker-writable workspace -- so
        // they are not evidence in either direction.
        Assert.Equal("Git\\cmd", VerifyProcessPath.Compose("Git\\cmd"));
        Assert.Equal(string.Empty, VerifyProcessPath.Compose(null));
    }

    /// <summary>
    /// The end-to-end claim the issue asks for: a command spawned through
    /// <see cref="VerifyRunner.RunProcessAsync"/> can resolve <c>sh</c>. Runs against the real host
    /// (Git for Windows is a prerequisite of this repo's own gates), whatever the test runner's own
    /// ambient PATH happened to carry — under the registry-only PATH a daemon inherits, this is the
    /// arm that was red.
    /// </summary>
    [Fact]
    public async Task The_verify_spawn_resolves_sh()
    {
        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/d", "/c", "where sh"], workingDirectory: null, CancellationToken.None);

        Assert.True(outcome.Passed, outcome.Tail);
    }
}
