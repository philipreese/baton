using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// #1944: the branch a dispatch's workspace was checked out on, recorded as a room fact at launch so a
/// runner lane's merged PR joins to its room in <c>baton ledger backfill</c> without the lane having
/// declared anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same file name as a worker's declaration, in a place a worker's declaration can never
/// reach.</b> A step DECLARES a delivery branch by producing
/// <see cref="DeliveryReferenceOutputNames.Branch"/> as one of its outputs, which lands under the
/// room's <c>artifacts/execution_&lt;id&gt;/</c> tree (spec/baton.md §2's delivery-state convention).
/// This writes the same name at the ROOM ROOT, beside <c>bindings.json</c>, where no step output can
/// land — so the two never collide and the location is what says which one a reader is holding: a
/// lane's own declaration, or what dispatch merely observed. Which of the two the backfill prefers is
/// recorded once, in spec/baton.md §7's backfill section, and is not restated here.
/// </para>
/// <para>
/// <b>Fails open, and its only failure mode is silence.</b> Nothing downstream of a dispatch reads
/// this file — it exists for an accounting join run days later — so a room root that cannot be written
/// to costs that join and must never cost the run. Recording it is deliberately placed after
/// <c>Directory.CreateDirectory</c> at both call sites, where a refusal is no longer possible.
/// </para>
/// </remarks>
public static class RoomDeliveryBranch
{
    /// <summary>
    /// Branch names a dispatch records nothing for. #1944's wanted behaviour is "a git worktree, or any
    /// checkout on a NON-DEFAULT branch", and this list is how the default half is decided.
    /// <para>
    /// <b>Named rather than asked, and the cost of the list being wrong for a repository is nil.</b>
    /// Git has no local, spawn-free answer for "what is this repository's default branch" —
    /// <c>origin/HEAD</c> is unset in most clones and worktrees — so the alternative is a second spawn
    /// that usually fails. What a wrong entry buys is a room recording a trunk name as its delivery
    /// branch; the join it feeds is against merged pull requests' HEAD branches, and a PR is never
    /// merged from the branch it merges into, so such a record matches no PR rather than matching a
    /// wrong one. Excluding trunk keeps every room dispatched on it from crowding one join key with an
    /// entry that is false about the room and useless to the reader.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> TrunkBranchNames = ["main", "master"];

    /// <summary>Where <see cref="RecordAsync"/> writes and <see cref="TryRead"/> reads.</summary>
    public static string PathFor(string roomDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        return Path.Combine(roomDirectoryPath, DeliveryReferenceOutputNames.Branch);
    }

    /// <summary>
    /// Whether <paramref name="branch"/> is worth recording as a room's delivery branch: a named branch
    /// that is not a trunk (<see cref="TrunkBranchNames"/>). <see langword="false"/> for null and empty,
    /// which is what <see cref="WorkspaceHead.TryReadBranchAsync"/> answers for a detached <c>HEAD</c>,
    /// a non-git workspace, and a git that would not run.
    /// </summary>
    public static bool IsDeliveryBranch(string? branch) =>
        branch is { Length: > 0 }
        && !TrunkBranchNames.Contains(branch.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records <paramref name="branch"/> as <paramref name="roomDirectoryPath"/>'s delivery branch when
    /// <see cref="IsDeliveryBranch"/> admits it. A no-op otherwise, and a no-op — with the reason said
    /// out loud on stderr rather than swallowed — when the write itself fails.
    /// </summary>
    public static async Task RecordAsync(
        string roomDirectoryPath, string? branch, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        if (!IsDeliveryBranch(branch))
        {
            return;
        }

        var path = PathFor(roomDirectoryPath);
        try
        {
            await File.WriteAllTextAsync(path, branch!.Trim(), cancellationToken).ConfigureAwait(false);
        }
        // OperationCanceledException among them for the reason RoomBindingStamps' own read states: a
        // Ctrl-C here must cost this fact and nothing else, never an exception escaping into a dispatch
        // that has already provisioned a room.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"Could not record the delivery branch '{branch}' in '{path}': {ex.Message} "
                + "A merged pull request from this branch will not join to this room in `baton ledger backfill`.");
        }
    }

    /// <summary>
    /// The branch a dispatch recorded for this room, or <see langword="null"/> when it recorded none —
    /// which is every room dispatched before #1944, every room whose workspace was on a trunk, and
    /// every room whose workspace was not a git checkout. Fails open on an unreadable file for the same
    /// reason <see cref="RecordAsync"/> does.
    /// </summary>
    public static string? TryRead(string roomDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var path = PathFor(roomDirectoryPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
