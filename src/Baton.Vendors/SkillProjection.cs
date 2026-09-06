using Baton.Dispatch;

namespace Baton.Vendors;

/// <summary>
/// One file a canonical skill package contributes to a projection: where it is read from, and where the
/// vendor CLI expects to find it (#1151).
/// </summary>
public sealed record SkillProjectionFile(string SourcePath, string DestinationPath);

/// <summary>
/// What projecting one canonical skill package would do: the files that will be placed, and how many
/// destinations already hold <em>different</em> content and are therefore left alone (#1151).
/// </summary>
/// <param name="Package">The canonical package this entry realizes.</param>
/// <param name="TargetDirectory">The directory the package is projected into.</param>
/// <param name="Files">The files to place. Excludes every destination counted in <paramref name="KeptFileCount"/>.</param>
/// <param name="KeptFileCount">
/// How many of the package's files already exist at their destination with different bytes. Those are
/// never overwritten — the operator put them there, and this realization has no way to tell a
/// hand-authored file from one an earlier projection placed and the operator then edited.
/// </param>
public sealed record SkillProjectionEntry(
    SkillPackage Package,
    string TargetDirectory,
    IReadOnlyList<SkillProjectionFile> Files,
    int KeptFileCount);

/// <summary>
/// The plan for projecting every canonical skill package found under a working directory into the place
/// a vendor CLI reads them from (#1151).
/// </summary>
public sealed record SkillProjectionPlan(string TargetBaseDirectory, IReadOnlyList<SkillProjectionEntry> Entries);

/// <summary>
/// Computes a <see cref="SkillProjectionPlan"/> — the one predicate behind both readers of a projection
/// (#1929 review). <c>ClaudeWorkerAdapter.Resolve</c> turns a plan into
/// <see cref="CoreDispatchSeedCopy"/> entries the dispatcher writes when an execution starts;
/// <c>ClaudeWorkerAdapter.DiscoverCapabilitiesAsync</c> turns the same plan into the roster's
/// <c>(to be projected, N file(s) to be kept)</c> suffix. Both read the same bytes through
/// <see cref="CoreDispatcher.FilesHaveIdenticalBytes"/>, so the roster cannot claim something the write
/// path then contradicts.
/// </summary>
/// <remarks>
/// <b>Planning writes nothing.</b> It is a read of two directory trees, safe on any path a binding is
/// merely resolved or a roster merely printed for — which is the whole point: the write it plans happens
/// once, later, on the dispatch path that knows an execution is actually starting.
/// <para>
/// The kept-count is a <em>snapshot at planning time</em>. The dispatcher re-measures the identical
/// predicate immediately before each copy, so a file the operator edits between the roster and the
/// dispatch is still kept — the roster's number can be stale, the guarantee cannot.
/// </para>
/// </remarks>
public static class SkillProjection
{
    public static SkillProjectionPlan Plan(string? workingDirectory, string targetBaseDirectory) =>
        PlanFor(SkillPackageReader.DiscoverPackages(workingDirectory), targetBaseDirectory);

    /// <summary>
    /// The same plan over an ALREADY-RESOLVED package set (#1151 S1) rather than over whatever a
    /// directory scan turns up. This is the form a binding that named its skills goes through: the
    /// resolver has already decided which rung each name came from, so this method never asks where a
    /// package lives.
    /// </summary>
    public static SkillProjectionPlan PlanFor(IReadOnlyList<SkillPackage> packages, string targetBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBaseDirectory);

        if (packages.Count == 0)
        {
            return new SkillProjectionPlan(targetBaseDirectory, Array.Empty<SkillProjectionEntry>());
        }

        var entries = new List<SkillProjectionEntry>(packages.Count);
        foreach (var package in packages)
        {
            var targetDirectory = Path.Combine(targetBaseDirectory, package.Name);
            var files = new List<SkillProjectionFile>();
            var kept = 0;

            foreach (var relativePath in EnumerateProjectableFiles(package.DirectoryPath))
            {
                var sourcePath = Path.Combine(package.DirectoryPath, relativePath);
                var destinationPath = Path.Combine(targetDirectory, relativePath);
                if (File.Exists(destinationPath) && !CoreDispatcher.FilesHaveIdenticalBytes(sourcePath, destinationPath))
                {
                    kept++;
                    continue;
                }

                files.Add(new SkillProjectionFile(sourcePath, destinationPath));
            }

            entries.Add(new SkillProjectionEntry(package, targetDirectory, files, kept));
        }

        return new SkillProjectionPlan(targetBaseDirectory, entries);
    }

    /// <summary>
    /// Every file under <paramref name="packageDirectory"/>, as paths relative to it, skipping
    /// <b>both</b> a symlinked or junctioned subdirectory and a symlinked file (#1929 review LOW, and
    /// its re-review LOW).
    /// </summary>
    /// <remarks>
    /// A recursive <see cref="Directory.GetFiles(string, string, SearchOption)"/> follows directory
    /// links, so a link inside a package would pull an unrelated tree into the operator's repository
    /// under the vendor's own skills directory, where the CLI then reads it. Skipping linked
    /// subdirectories closes the recursive-descent half; skipping linked FILES closes the rest, because
    /// <see cref="File.Copy(string, string, bool)"/> copies the link target's bytes — so
    /// <c>skills/foo/notes.md</c> pointing outside the repository would otherwise land inside it
    /// verbatim. Together those are the source side; the destination side was already pinned (every path
    /// is composed from a relative path under the package). Links are skipped silently rather than
    /// refused: the package is still usable without them, and refusing the dispatch over a link would be
    /// a larger behaviour than the defect warrants.
    /// </remarks>
    private static IReadOnlyList<string> EnumerateProjectableFiles(string packageDirectory)
    {
        var relativePaths = new List<string>();
        Walk(packageDirectory, string.Empty);
        relativePaths.Sort(StringComparer.Ordinal);
        return relativePaths;

        void Walk(string directory, string relativePrefix)
        {
            try
            {
                foreach (var file in Directory.GetFiles(directory))
                {
                    if (new FileInfo(file).LinkTarget is not null)
                    {
                        continue;
                    }

                    relativePaths.Add(Path.Combine(relativePrefix, Path.GetFileName(file)));
                }

                foreach (var subDirectory in Directory.GetDirectories(directory))
                {
                    if (new DirectoryInfo(subDirectory).LinkTarget is not null)
                    {
                        continue;
                    }

                    Walk(subDirectory, Path.Combine(relativePrefix, Path.GetFileName(subDirectory)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Same fail-open-but-not-silently rule as SkillScanner's own catch: one unreadable
                // subdirectory costs its own files, not the whole package.
                Console.Error.WriteLine($"Warning: could not read skill package directory '{directory}': {ex.Message}");
            }
        }
    }
}
