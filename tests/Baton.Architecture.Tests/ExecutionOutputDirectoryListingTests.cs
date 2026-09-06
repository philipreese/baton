using System.Text.RegularExpressions;

namespace Baton.Architecture.Tests;

/// <summary>
/// #1351: <see cref="Baton.Dispatch.ExecutionStreamLogger"/> writes <c>.stdout.log</c>/<c>.stderr.log</c>
/// (and their rollovers) into the execution's <em>output</em> directory — the same directory whose
/// contents are the room's file list. Any raw file-listing call in <c>src/</c> is a candidate to leak
/// those engine-written files onto a surface that presents them as if a worker had produced them,
/// unless it is filtered (<see cref="Baton.Dispatch.ExecutionStreamLogger.IsStreamLogFileName"/>) or
/// demonstrably reads a directory that is not an execution's output directory.
/// <para>
/// As of #1351, <c>src/</c> has zero call sites that enumerate an execution's own output directory —
/// the sites below each list an unrelated directory, never <c>{artifactsRoot}/execution_{id}</c>
/// itself. Which directory is said per entry in the allowlist rather than summarised here: this
/// sentence used to carry both a count and a list of directory kinds, and both went stale the moment
/// the allowlist grew, which is what an entry's own comment cannot do. This test pins that fact
/// structurally: the allowlist below is the complete, named set of raw file-listing calls in <c>src/</c>,
/// each with a comment saying why it does not need the filter. The next author who adds a new one must
/// either route it through a filtered listing that excludes <see cref="Baton.Dispatch.ExecutionStreamLogger.IsStreamLogFileName"/>
/// entries, or add the site to this allowlist WITH a one-line justification that it is not reading an
/// execution's output directory. A silent addition fails this test instead of shipping unreviewed.
/// </para>
/// <para>
/// Red-first: a throwaway <c>Directory.GetFiles(outputDirectory)</c> call was added to
/// <c>ArtifactManager.ResolveOutputDirectory</c>'s caller shape, observed to fail this test, then
/// removed — see <c>changes.md</c> for the transcript. A regex allowlist was built rather than a
/// structural <c>ExecutionOutputListing</c> helper (the issue's preferred shape) because there is no
/// current caller to route through such a helper — introducing one now would add an abstraction with
/// zero real callers, which this repo's own standards call out as premature.
/// </para>
/// </summary>
public class ExecutionOutputDirectoryListingTests
{
    // Matches a raw file-listing call: Directory.GetFiles/EnumerateFiles/EnumerateFileSystemEntries/
    // GetFileSystemEntries/GetFileSystemInfos, whether invoked as a static Directory member or an
    // instance method on a DirectoryInfo. The (?<!\w) lookbehind keeps a variable merely ending in one
    // of these words (there are none today, but the fixture race is cheap to guard) from matching.
    private static readonly Regex RawFileListingCall = new(
        @"(?<!\w)(?:Directory\.)?(?:GetFiles|EnumerateFiles|EnumerateFileSystemEntries|GetFileSystemEntries|GetFileSystemInfos)\s*\(",
        RegexOptions.Singleline);

    // Each entry: relative path (from src/) -> the directory argument that call site lists, with why
    // it is not an execution's output directory. Keep this list exhaustive with the actual call sites
    // in src/ — this test enforces that equality, not just "no more than these".
    private static readonly IReadOnlyDictionary<string, string> Allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Lists a room's memory root (fact files), never an execution's own output directory.
        ["Baton/Domain/RoomMemoryDocument.cs"] = "lists memoryRoot (room memory), not an execution output directory",
        ["Baton/Mutation/MemoryProposalApplier.cs"] = "lists memoryRoot (room memory), not an execution output directory",
        // Lists a memory-proposal capture directory, never an execution's own output directory.
        ["Baton/Mutation/MemoryProposalEscalation.cs"] = "lists captureDirectoryPath (proposal capture), not an execution output directory",
        // Lists a slash-command definitions directory shipped alongside the adapter, never an
        // execution's own output directory.
        ["Baton.Vendors/ClaudeWorkerAdapter.cs"] = "lists commandsDir (bundled slash-command defs), not an execution output directory",
        // #1853: both recursive calls first pass through ResolveWithinWorkspace. The separate output
        // root is never accepted by that method, even when the grant permits writing declared outputs.
        ["Baton.Vendors/CodexDynamicToolPolicy.cs"] =
            "lists only a ResolveWithinWorkspace-validated path for the broker's list/search tools, never the separate execution output root",
        // #1901 C2: this one DOES walk a room's artifacts tree, execution output directories included,
        // and the filter is not needed because the enumeration is by EXACT FILENAME --
        // DeliveryReferenceOutputNames.Branch, a declared worker output. No stream log can match a name
        // that is not its own, so there is nothing for IsStreamLogFileName to exclude; the same
        // filename-matched read DeliveryReferenceResolver already does off resolved output paths.
        ["Baton.Cli/LedgerBackfillCommand.cs"] =
            "EnumerateFiles matches one exact declared-output filename (delivery-branch.txt) rather than listing a directory, so no stream log is reachable by it",
        // #1488: lists {BATON_HOME}/watches -- the baton watch registry directory -- never an
        // execution's own output directory.
        ["Baton.Cli/WatchStore.cs"] = "lists watchesDirectoryPath (the baton watch registry), not an execution output directory",
        // #1557: GetFileSystemEntries lists artifacts/pruned/ itself (the room-level pruned root, not
        // an execution's own output directory) to find pruned execution dirs. The nested
        // EnumerateFiles DOES walk into a pruned execution's own former output directory, but sums it
        // unfiltered on purpose -- see that call site's own comment in FleetProjectionWriter.cs for why.
        ["Baton.Cli/Daemon/FleetProjectionWriter.cs"] =
            "GetFileSystemEntries lists the pruned room root, not an execution output directory; the nested EnumerateFiles walks a pruned execution's own former output directory but deliberately does not filter ExecutionStreamLogger.IsStreamLogFileName -- see #1557 comment above",
        // #1852: both list a VENDOR's memory root under ~/.claude (projects/*/memory and
        // memory-archive/<label>/*) and a Claude project directory's session transcripts. Neither is
        // under a room's artifacts root at all, so an execution output directory is unreachable from
        // here — and `baton memory audit` writes nothing, so nothing it lists can be presented as a
        // worker's output.
        ["Baton/Memory/MemoryRootInventory.cs"] =
            "EnumerateFiles lists a ~/.claude memory root (live or archived), not an execution output directory",
        ["Baton/Memory/MemoryRootPath.cs"] =
            "EnumerateFiles lists a Claude project directory's session transcripts, not an execution output directory",
        // #1151: walks a canonical skill package directory to plan which of its files a vendor
        // realization would place. Since S1 that directory is package.DirectoryPath — whichever
        // SkillPackageResolver rung a declared name matched, not only <workspace>/skills/<name>/ — so
        // this reads the same population as the lint entry below, and the caveat stated there about the
        // operator-set rung applies here identically (#1941 review LOW: the justification here still
        // said "under the dispatch workspace" after the change that broadened it).
        ["Baton.Vendors/SkillProjection.cs"] =
            "GetFiles walks one resolved canonical skill package's own directory, not an execution output directory",
        // #1151 S1: the same population as SkillProjection above, read for a different reason — the
        // executable-asset lint enumerates one canonical skill package's own files to decide whether it
        // bundles a script. Three of SkillPackageResolver's four rungs ({BatonPaths.Root}/skills, beside
        // the assembly, <workspace>/skills) cannot be a room's artifacts root by construction. The
        // fourth, the BATON_SKILLS_PATH override, is operator-set and unconstrained: an operator who
        // pointed it inside a room could make this enumerate a path under one. What it still would not
        // do is list an execution's OUTPUT directory as such — it walks a <name>/ package subdirectory
        // it was handed and reports the files as package assets, never as a worker's outputs — so this
        // allowlist entry is a claim about what is listed, not a guarantee about where the override may
        // point (#1941 review LOW).
        ["Baton.Vendors/SkillPackageLint.cs"] =
            "GetFiles enumerates one resolved skill package's own assets, not an execution output directory",
        // #496: lists artifacts/.versions/ (a named artifact's own version-history sidecar, one
        // index.jsonl per name), never an execution's own output directory.
        ["Baton/Artifacts/RoomArtifacts.cs"] =
            "GetFiles lists artifacts/.versions/ for index.jsonl files (named-artifact version history), not an execution output directory",
    };

    [Fact]
    public void Every_raw_file_listing_call_site_in_src_is_a_named_non_output_directory_or_filters_stream_logs()
    {
        var srcDir = Path.Combine(RepoRoot(), "src");
        var offenders = new List<string>();
        var seenAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(srcDir, filePath).Replace('\\', '/');
            var content = File.ReadAllText(filePath);
            if (!RawFileListingCall.IsMatch(content))
            {
                continue;
            }

            if (Allowed.ContainsKey(relativePath))
            {
                seenAllowed.Add(relativePath);
            }
            else
            {
                offenders.Add(relativePath);
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Found raw file-listing call(s) not on the reviewed allowlist: {string.Join(", ", offenders)}. " +
            "A directory listing under src/ that reads an execution's output directory must filter out " +
            "ExecutionStreamLogger.IsStreamLogFileName entries (#1351) — engine-written stream logs live in " +
            "the same directory as a worker's declared outputs. Either route the new call through a filtered " +
            "listing, or add it to ExecutionOutputDirectoryListingTests.Allowed with a one-line justification " +
            "that it does not read an execution's output directory.");

        var missing = Allowed.Keys.Except(seenAllowed, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(
            missing.Count == 0,
            $"Allowlisted call site(s) no longer found: {string.Join(", ", missing)}. Remove the stale " +
            "entry so the allowlist stays exact, not a superset of what actually exists.");
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
