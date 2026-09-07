namespace Baton.Architecture.Tests;

/// <summary>
/// #1912 fix round: no source or documentation file may carry a NUL byte. Git classifies such a file
/// as binary, and that is silent — the build, the formatter and every test still pass, while
/// <c>git diff</c> reports <c>Bin 0 -&gt; N bytes</c> instead of a patch and ripgrep/<c>git grep</c>
/// skip the file entirely.
/// <para>
/// The cost is paid by the two instruments this repo's gates actually run on: the
/// <c>second-reader</c> gate reads a diff, and a <c>blast-radius</c> trace is a search over the tree.
/// Both go quietly blind on the file rather than failing, so nothing announces the loss. Measured on
/// <c>src/Baton/Queue/PullRequestChecks.cs</c>, which shipped with one stray NUL at offset 3126 inside
/// a string literal: the whole file — the PR's central new file — produced no reviewable diff and
/// matched no search, and every check on the pull request was green.
/// </para>
/// <para>
/// Enumerates the working tree rather than shelling out to <c>git ls-files</c>, the same way
/// <see cref="FileDeleteHygieneTests"/> and <see cref="DirectoryDeleteHygieneTests"/> do: only one test
/// in this project spawns a process, and the generated <c>.cs</c> under every <c>obj/</c> is what the
/// skip list below is for.
/// </para>
/// </summary>
public class NulByteHygieneTests
{
    private static readonly string[] ScannedDirectories = ["src", "tools", "tests"];

    private static readonly string[] ScannedExtensions = [".cs", ".py", ".md", ".mjs"];

    // Build output and dependency trees: not authored here, and full of files that are legitimately
    // not text.
    private static readonly string[] SkippedSegments = ["bin", "obj", "node_modules", ".git"];

    [Fact]
    public void No_source_or_doc_file_carries_a_NUL_byte()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var directory in ScannedDirectories)
        {
            var path = Path.Combine(root, directory);
            Assert.True(Directory.Exists(path), $"Expected a '{directory}/' directory at the repo root ({root}).");

            foreach (var filePath in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (!ScannedExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Any(s => SkippedSegments.Contains(s, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                scanned++;
                var bytes = File.ReadAllBytes(filePath);
                var offset = Array.IndexOf(bytes, (byte)0);
                if (offset >= 0)
                {
                    offenders.Add($"{Path.GetRelativePath(root, filePath).Replace('\\', '/')}:{offset}");
                }
            }
        }

        // A control on the harness itself, not on the product: if the enumeration silently matched
        // nothing (a moved repo root, a skip list that swallowed the tree) the assertion below would
        // pass while checking no file at all.
        Assert.True(scanned > 100, $"Expected the scan to reach the repository's source tree; it read only {scanned} file(s).");

        Assert.True(
            offenders.Count == 0,
            "Found NUL byte(s) in source/doc file(s) at file:offset — "
            + string.Join(", ", offenders)
            + ". Git treats such a file as binary: it produces no reviewable diff and no search over "
            + "the tree can find a single line of it, silently, while every other check stays green. "
            + "Re-save the file as text with the byte removed.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Baton.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate the repo root (Baton.slnx) by walking up from " + AppContext.BaseDirectory);
    }
}
