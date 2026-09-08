using Baton.Vendors;

namespace Baton.Cli;

/// <summary>Which of <c>baton trust</c>'s four shapes (#1166, <c>--forget</c> since #2121) this invocation is.</summary>
public enum TrustMode
{
    /// <summary><c>baton trust &lt;project-path&gt; --ceiling &lt;categories&gt;</c>.</summary>
    Register,

    /// <summary><c>baton trust --list</c>.</summary>
    List,

    /// <summary><c>baton trust &lt;project-path&gt; --revoke</c> — withdraws the grant and leaves a tombstone.</summary>
    Revoke,

    /// <summary><c>baton trust &lt;project-path&gt; --forget</c> — deletes the record outright, tombstone or live (spec/baton.md §9 has the two verbs side by side).</summary>
    Forget,
}

/// <summary>
/// Parsed arguments for <c>baton trust</c>. <see cref="ProjectPath"/> is non-null for
/// <see cref="TrustMode.Register"/>/<see cref="TrustMode.Revoke"/>/<see cref="TrustMode.Forget"/> and <see cref="Ceiling"/> is
/// non-null exactly for <see cref="TrustMode.Register"/> — <see cref="TrustOptionsParser"/> is what
/// enforces that. Deliberately not named <c>RoomDirectoryPath</c>: a project path is not a room
/// directory, and <c>RoomDirectoryIsResolvedAtTheBoundaryTests</c> discovers its population by that
/// exact property name.
/// </summary>
public sealed record TrustOptions(TrustMode Mode, string? ProjectPath, ProjectCeiling? Ceiling);
