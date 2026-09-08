using Baton.Vendors.Tests.TestSupport;

namespace Baton.Vendors.Tests;

/// <summary>
/// #1166: <see cref="ProjectCeilingStore"/>'s load/save round trip, its "missing file is empty,
/// malformed file throws" distinction (mirroring <see cref="BatonProfileStore"/>), and the path
/// canonicalisation every per-directory record in this tree shares
/// (<see cref="Baton.Status.BatonPaths.RecordKey"/>/<see cref="Baton.Status.BatonPaths.RecordKeyComparer"/>).
/// </summary>
public class ProjectCeilingStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"baton-ceilings-{Guid.NewGuid():N}.json");

    [Fact]
    public void Loading_a_missing_file_resolves_to_an_empty_map()
    {
        var path = TempPath();

        var ceilings = ProjectCeilingStore.Load(path);

        Assert.Empty(ceilings);
    }

    [Fact]
    public void Setting_then_getting_round_trips_the_ceiling()
    {
        var path = TempPath();
        var projectPath = Path.Combine(Path.GetTempPath(), $"baton-ceiling-project-{Guid.NewGuid():N}");
        try
        {
            var ceiling = new ProjectCeiling(ReadFiles: true, WriteFiles: true, RunShellCommands: false, NetworkAccess: false);

            ProjectCeilingStore.Set(projectPath, ceiling, path);
            var loaded = ProjectCeilingStore.TryGet(projectPath, path);

            Assert.Equal(ceiling, loaded);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public void TryGet_resolves_to_null_for_a_project_never_trusted()
    {
        var path = TempPath();
        try
        {
            ProjectCeilingStore.Set(
                Path.Combine(Path.GetTempPath(), "one-project"), ProjectCeiling.Unrestricted, path);

            var result = ProjectCeilingStore.TryGet(Path.Combine(Path.GetTempPath(), "a-different-project"), path);

            Assert.Null(result);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// #1166's own canonicalisation requirement: a trailing separator must resolve to the same key —
    /// <see cref="Baton.Status.BatonPaths.RecordKey"/>'s absolute-path-plus-trim rule, not a second
    /// implementation.
    /// </summary>
    [Fact]
    public void A_trailing_separator_resolves_to_the_same_key_as_the_bare_path()
    {
        var path = TempPath();
        var projectPath = Path.Combine(Path.GetTempPath(), $"baton-ceiling-trail-{Guid.NewGuid():N}");
        try
        {
            ProjectCeilingStore.Set(projectPath, ProjectCeiling.Unrestricted, path);

            var withTrailingSeparator = ProjectCeilingStore.TryGet(
                projectPath + Path.DirectorySeparatorChar, path);

            Assert.Equal(ProjectCeiling.Unrestricted, withTrailingSeparator);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// The comparer half, asserted separately from the trailing-separator case above — a store that
    /// forgot to carry <see cref="Baton.Status.BatonPaths.RecordKeyComparer"/> through
    /// <see cref="ProjectCeilingStore.Load"/>'s deserialization would still pass the trailing-separator
    /// test (that one is <see cref="Baton.Status.BatonPaths.RecordKey"/> alone), so this is the arm
    /// that actually exercises the comparer.
    /// </summary>
    [Fact]
    public void A_different_case_resolves_to_the_same_key_on_a_case_insensitive_comparer()
    {
        var path = TempPath();
        var projectPath = Path.Combine(Path.GetTempPath(), $"baton-ceiling-case-{Guid.NewGuid():N}");
        try
        {
            ProjectCeilingStore.Set(projectPath, ProjectCeiling.Unrestricted, path);

            var upperCased = ProjectCeilingStore.TryGet(projectPath.ToUpperInvariant(), path);

            Assert.Equal(ProjectCeiling.Unrestricted, upperCased);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public void Revoke_removes_a_recorded_ceiling_and_returns_true()
    {
        var path = TempPath();
        var projectPath = Path.Combine(Path.GetTempPath(), $"baton-ceiling-revoke-{Guid.NewGuid():N}");
        try
        {
            ProjectCeilingStore.Set(projectPath, ProjectCeiling.Unrestricted, path);

            var revoked = ProjectCeilingStore.Revoke(projectPath, path);

            Assert.True(revoked.Revoked);
            Assert.Empty(revoked.CascadedPaths);
            Assert.Null(ProjectCeilingStore.TryGet(projectPath, path));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// #2121: revoke leaves a tombstone, not an absence. <see cref="ProjectCeilingStore.TryGet"/> still
    /// answers null (a tombstone is not a ceiling), but the raw record says revoked, when, and where the
    /// entry had come from — and a tombstone permits nothing even to a reader that ignores the flag.
    /// </summary>
    [Fact]
    public void Revoke_leaves_a_tombstone_that_TryGet_hides_and_TryGetRecord_shows()
    {
        var path = TempPath();
        var source = Path.Combine(Path.GetTempPath(), $"baton-ceiling-tomb-src-{Guid.NewGuid():N}");
        var projectPath = Path.Combine(Path.GetTempPath(), $"baton-ceiling-tomb-{Guid.NewGuid():N}");
        var at = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        try
        {
            ProjectCeilingStore.Set(projectPath, ProjectCeiling.Unrestricted with { InheritedFrom = source }, path);

            var revoked = ProjectCeilingStore.Revoke(projectPath, path, at);

            Assert.True(revoked.Revoked);
            Assert.Null(ProjectCeilingStore.TryGet(projectPath, path));
            var record = ProjectCeilingStore.TryGetRecord(projectPath, path);
            Assert.NotNull(record);
            Assert.True(record.IsRevoked);
            Assert.Equal(at, record.RevokedAt);
            Assert.Equal(source, record.InheritedFrom);
            Assert.False(record.ReadFiles || record.WriteFiles || record.RunShellCommands || record.NetworkAccess);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public void Revoking_a_tombstone_again_reports_nothing_to_revoke_and_keeps_the_original_timestamp()
    {
        var path = TempPath();
        var projectPath = Path.Combine(Path.GetTempPath(), $"baton-ceiling-tomb-twice-{Guid.NewGuid():N}");
        var first = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        try
        {
            ProjectCeilingStore.Set(projectPath, ProjectCeiling.Unrestricted, path);
            ProjectCeilingStore.Revoke(projectPath, path, first);

            var again = ProjectCeilingStore.Revoke(projectPath, path, first.AddDays(1));

            Assert.False(again.Revoked);
            Assert.Equal(first, ProjectCeilingStore.TryGetRecord(projectPath, path)?.RevokedAt);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>The cascade tombstones the copies too — they are listed as revoked, not gone, and the copy's provenance survives on its tombstone.</summary>
    [Fact]
    public void Revoke_tombstones_the_inherited_copies_along_with_the_source()
    {
        var path = TempPath();
        var source = Path.Combine(Path.GetTempPath(), $"baton-ceiling-cascade-src-{Guid.NewGuid():N}");
        var copy = Path.Combine(Path.GetTempPath(), $"baton-ceiling-cascade-copy-{Guid.NewGuid():N}");
        try
        {
            ProjectCeilingStore.Set(source, ProjectCeiling.Unrestricted, path);
            ProjectCeilingStore.Set(copy, ProjectCeiling.Unrestricted with { InheritedFrom = ProjectCeilingStore.CanonicalKey(source) }, path);

            var revoked = ProjectCeilingStore.Revoke(source, path);

            Assert.Equal([ProjectCeilingStore.CanonicalKey(copy)], revoked.CascadedPaths);
            var copyRecord = ProjectCeilingStore.TryGetRecord(copy, path);
            Assert.NotNull(copyRecord);
            Assert.True(copyRecord.IsRevoked);
            Assert.Equal(ProjectCeilingStore.CanonicalKey(source), copyRecord.InheritedFrom);
            Assert.Null(ProjectCeilingStore.TryGet(copy, path));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public void Set_replaces_a_tombstone_with_a_live_ceiling()
    {
        var path = TempPath();
        var projectPath = Path.Combine(Path.GetTempPath(), $"baton-ceiling-retrust-{Guid.NewGuid():N}");
        try
        {
            ProjectCeilingStore.Set(projectPath, ProjectCeiling.Unrestricted, path);
            ProjectCeilingStore.Revoke(projectPath, path);

            ProjectCeilingStore.Set(projectPath, ProjectCeiling.Unrestricted, path);

            var record = ProjectCeilingStore.TryGetRecord(projectPath, path);
            Assert.NotNull(record);
            Assert.False(record.IsRevoked);
            Assert.Equal(ProjectCeiling.Unrestricted, ProjectCeilingStore.TryGet(projectPath, path));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>Both polarities: a tombstone is removed, a live ceiling given to the same call is left exactly as it was, and a never-recorded path is a no-op.</summary>
    [Fact]
    public void ClearRevocations_removes_tombstones_only()
    {
        var path = TempPath();
        var tombstoned = Path.Combine(Path.GetTempPath(), $"baton-ceiling-clear-tomb-{Guid.NewGuid():N}");
        var live = Path.Combine(Path.GetTempPath(), $"baton-ceiling-clear-live-{Guid.NewGuid():N}");
        var never = Path.Combine(Path.GetTempPath(), $"baton-ceiling-clear-never-{Guid.NewGuid():N}");
        try
        {
            ProjectCeilingStore.Set(tombstoned, ProjectCeiling.Unrestricted, path);
            ProjectCeilingStore.Revoke(tombstoned, path);
            var narrowed = new ProjectCeiling(ReadFiles: true, WriteFiles: false, RunShellCommands: false, NetworkAccess: false);
            ProjectCeilingStore.Set(live, narrowed, path);

            var cleared = ProjectCeilingStore.ClearRevocations([tombstoned, live, never], path);

            Assert.Equal([ProjectCeilingStore.CanonicalKey(tombstoned)], cleared);
            Assert.Null(ProjectCeilingStore.TryGetRecord(tombstoned, path));
            Assert.Equal(narrowed, ProjectCeilingStore.TryGet(live, path));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public void Revoke_returns_false_for_a_project_never_trusted()
    {
        var path = TempPath();

        var revoked = ProjectCeilingStore.Revoke(Path.Combine(Path.GetTempPath(), "never-trusted"), path);

        Assert.False(revoked.Revoked);
        Assert.Empty(revoked.CascadedPaths);
    }

    [Fact]
    public void Loading_a_malformed_file_throws_rather_than_silently_resolving_to_empty()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ not valid json");

            var ex = Assert.Throws<ProjectCeilingStoreException>(() => ProjectCeilingStore.Load(path));
            Assert.Contains(path, ex.Message);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public void Saving_creates_the_parent_directory_if_it_does_not_exist_yet()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"baton-ceilings-dir-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "project-ceilings.json");
        try
        {
            ProjectCeilingStore.Set(Path.Combine(Path.GetTempPath(), "p"), ProjectCeiling.Unrestricted, path);

            Assert.True(File.Exists(path));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void DefaultPath_lives_under_a_dot_baton_directory_in_the_user_profile()
    {
        Assert.EndsWith(Path.Combine(".baton", "project-ceilings.json"), ProjectCeilingStore.DefaultPath);
    }
}
