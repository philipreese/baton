using System.Text.Json.Serialization;

namespace Baton.Vendors;

/// <summary>
/// #1166 — decision 0004's project scope ("the ceiling"): the operator's own outer bound on what any
/// worker-binding config can grant against a given project path, expressed in the same four-category
/// vocabulary <see cref="PermissionGrant"/> already carries. 0004 names the ceiling's existence and
/// where it composes (project ∩ room ∩ step, always narrowing) but not a closed set of named levels —
/// this record is the fallback the issue's own scope ruling calls for: reuse the vocabulary
/// <see cref="ClaudeWorkerAdapter.TryTranslatePermissionGrant"/> already maps, rather than inventing a
/// second one.
/// </summary>
public sealed record ProjectCeiling(
    bool ReadFiles,
    bool WriteFiles,
    bool RunShellCommands,
    bool NetworkAccess)
{
    /// <summary>
    /// The ceiling a first-use "anything goes" trust decision produces — every category open, so it
    /// caps nothing. The only shape under which a role binding's raw <see cref="WorkerInvocation.PermissionScope"/>
    /// escape hatch (no structured <see cref="PermissionGrant"/> to intersect against) is still
    /// dispatchable — see <see cref="ProjectCeilingGate"/>.
    /// </summary>
    public static readonly ProjectCeiling Unrestricted = new(true, true, true, true);

    /// <summary>True when every category is open — the ceiling caps nothing a role grant could ask for.</summary>
    public bool IsUnrestricted => ReadFiles && WriteFiles && RunShellCommands && NetworkAccess;

    /// <summary>
    /// #2076: the already-trusted path this ceiling was <b>copied from</b> when a worktree or clone of
    /// the same repository inherited it (<c>Baton.Cli.InheritedProjectCeiling</c>), or
    /// <see langword="null"/> for one an operator recorded with <c>baton trust</c> directly.
    /// </summary>
    /// <remarks>
    /// <b>Provenance, never permission.</b> <see cref="Cap"/> and <see cref="IsUnrestricted"/> read the
    /// four booleans and nothing else, so this field can never widen or narrow what a worker may do —
    /// it exists so <c>baton trust --list</c> can say which entries the operator typed and which Baton
    /// derived. What it does change is record equality: an inherited "all" is a different
    /// <see cref="ProjectCeiling"/> value from <see cref="Unrestricted"/>, so compare the booleans, not
    /// the record, when the question is what a ceiling permits.
    /// <para>
    /// Omitted from the JSON when null, so recording one inherited entry does not stamp
    /// <c>"InheritedFrom": null</c> onto every other entry in the map (<see cref="ProjectCeilingStore.Save"/>
    /// rewrites the whole file).
    /// </para>
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InheritedFrom { get; init; }

    /// <summary>
    /// The ceiling in <c>baton trust --ceiling</c>'s own vocabulary — <c>all</c>, <c>none</c>, or the
    /// comma-separated open categories. The spelling <c>Baton.Cli.TrustOptionsParser</c> accepts,
    /// stated once here because two readers print it: <c>baton trust --list</c> and the inheritance note
    /// <c>baton dispatch</c> writes when a worktree or clone picks a ceiling up (#2076).
    /// </summary>
    public string Describe()
    {
        if (IsUnrestricted)
        {
            return "all";
        }

        List<string> categories = [];
        if (ReadFiles)
        {
            categories.Add(nameof(ReadFiles));
        }

        if (WriteFiles)
        {
            categories.Add(nameof(WriteFiles));
        }

        if (RunShellCommands)
        {
            categories.Add(nameof(RunShellCommands));
        }

        if (NetworkAccess)
        {
            categories.Add(nameof(NetworkAccess));
        }

        return categories.Count == 0 ? "none" : string.Join(',', categories);
    }

    /// <summary>
    /// Decision 0004's intersection rule (spec/baton.md §9 states it canonically) applied as a
    /// per-category logical AND, boolean by boolean below.
    /// </summary>
    public PermissionGrant Cap(PermissionGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return grant with
        {
            ReadFiles = grant.ReadFiles && ReadFiles,
            WriteFiles = grant.WriteFiles && WriteFiles,
            RunShellCommands = grant.RunShellCommands && RunShellCommands,
            NetworkAccess = grant.NetworkAccess && NetworkAccess,
        };
    }
}
