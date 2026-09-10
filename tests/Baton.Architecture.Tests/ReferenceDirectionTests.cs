using System.Xml.Linq;

namespace Baton.Architecture.Tests;

/// <summary>
/// #370: docs/agents/developing-baton.md's reference-direction invariant is prose the compiler can't check on its own, and
/// the room-model churn (#333/#335) is exactly when it could silently erode — a stray
/// <c>ProjectReference</c> added mid-refactor, and nothing fails. These tests read the project graph
/// and fail the build the moment a forbidden dependency appears, a seam gate in decision 0005's
/// rhythm (like #317/#318 were). A guard that lands after the refactor it guards is worthless, so
/// this lands first.
///
/// <para>Scope: the <em>structurally checkable</em> invariants — who may reference whom. "Flow never
/// parses worker content to make routing decisions" (docs/agents/developing-baton.md rule 1) is a property of logic, not
/// of the reference graph, so it stays a review-time invariant no static test can honestly assert.</para>
///
/// <para>Pure file reading over the repo — no project references, no network — so it runs identically
/// on every CI platform, the same shape as <c>Baton.Plan.Tests</c>.</para>
/// </summary>
public class ReferenceDirectionTests
{
    // Baton is the pure engine (docs/agents/developing-baton.md rule 2: the core layer understands only the single,
    // unified canonical protocol). It may depend on the managed BatonTask engine and the framework —
    // never on a vendor adapter or a client. This is the load-bearing invariant #335 rides: the
    // engine needs no changes for multi-task precisely because nothing above it reaches back in.
    // #1458: Baton.Daemon dropped from the forbidden list -- it is no longer a project (folded into
    // Baton.Cli as a verb), so it can never appear as a ProjectReference to begin with.
    [Fact]
    public void Baton_depends_on_nothing_above_the_engine()
        => AssertNoForbiddenReferences(
            project: "Baton",
            forbiddenProjects: ["Baton.Vendors", "Baton.Cli"],
            forbiddenPackagePrefixes: ["Avalonia", "Microsoft.AspNetCore"]);

    // Adapter isolation: vendor quirks live in Baton.Vendors, which depends only
    // downward on the engine (<see href="../../../docs/agents/developing-baton.md"/>) — never up into a client.
    [Fact]
    public void Baton_Vendors_does_not_depend_on_clients()
        => AssertNoForbiddenReferences(
            project: "Baton.Vendors",
            forbiddenProjects: ["Baton.Cli"],
            forbiddenPackagePrefixes: []);

    // #543 added a positive-inclusion check here: Baton.Daemon transitively reached Baton.Cli (through
    // Baton.RoomSession) so ClaudeWorkerAdapter.BuildSettingsJson's PreToolUse hook path resolved
    // inside the daemon's own output directory. #1420 deleted Baton.RoomSession and, with it, every
    // worker-turn-running surface the daemon had (RoomTurnHost, RoomClient, the session-turn
    // endpoints) — the daemon no longer spawns a worker or calls BuildSettingsJson, so it has no
    // remaining reason to carry Baton.Cli.dll in its output, and the check above removed with this
    // comment (it would now fail truthfully: the daemon's build graph no longer reaches Baton.Cli).

    private static void AssertNoForbiddenReferences(
        string project, string[] forbiddenProjects, string[] forbiddenPackagePrefixes)
    {
        var (projectRefs, packageRefs) = ReadReferences(project);

        var projectHits = projectRefs.Intersect(forbiddenProjects, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(
            projectHits.Count == 0,
            $"{project} must not reference project(s) [{string.Join(", ", projectHits)}] — " +
            "reference-direction invariant (docs/agents/developing-baton.md architecture rules, #370).");

        var packageHits = packageRefs
            .Where(pkg => forbiddenPackagePrefixes.Any(prefix => pkg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.True(
            packageHits.Count == 0,
            $"{project} must not reference package(s) [{string.Join(", ", packageHits)}] — " +
            "reference-direction invariant (docs/agents/developing-baton.md architecture rules, #370).");
    }

    private static (IReadOnlyCollection<string> ProjectRefs, IReadOnlyCollection<string> PackageRefs) ReadReferences(string project)
    {
        var path = Path.Combine(RepoRoot.Locate(), "src", project, project + ".csproj");
        var doc = XDocument.Load(path);

        var projectRefs = doc.Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            // Normalize Windows separators to '/' first so GetFileNameWithoutExtension resolves the
            // project name on Unix CI too (a bare '\' is not a separator there).
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var packageRefs = doc.Descendants("PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => include!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (projectRefs, packageRefs);
    }
}
