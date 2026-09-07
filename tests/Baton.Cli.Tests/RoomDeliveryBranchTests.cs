using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

/// <summary>
/// #1944: what a dispatch records as its room's delivery branch, and what it deliberately does not.
/// The write policy is unit-tested here without git; that a real dispatch actually reaches it with the
/// workspace's real branch is <see cref="DispatchDeliveryBranchTests"/>, which is the claim a file-level
/// test cannot make.
/// </summary>
public sealed class RoomDeliveryBranchTests : IDisposable
{
    private readonly string _room = Path.Combine(Path.GetTempPath(), $"baton-1944-{Guid.NewGuid():N}");

    public RoomDeliveryBranchTests() => Directory.CreateDirectory(_room);

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_room);

    /// <summary>
    /// The headline, and the round trip: a lane branch is recorded under the name the backfill reads,
    /// at the room root rather than under artifacts — the location that separates it from a step's own
    /// declared output.
    /// </summary>
    [Fact]
    public async Task A_lane_branch_is_recorded_at_the_room_root_and_reads_back()
    {
        await RoomDeliveryBranch.RecordAsync(_room, "1944-lane", TestContext.Current.CancellationToken);

        var path = Path.Combine(_room, DeliveryReferenceOutputNames.Branch);
        Assert.True(File.Exists(path));
        Assert.Equal(
            "1944-lane", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal("1944-lane", RoomDeliveryBranch.TryRead(_room));
        Assert.False(Directory.Exists(Path.Combine(_room, "artifacts")));
    }

    /// <summary>
    /// Polarity in both directions on the one condition #1944 states — non-default branch records,
    /// default branch does not. Trunk is checked case-insensitively because a checkout's branch name is
    /// git's spelling, not this repo's.
    /// </summary>
    [Theory]
    [InlineData("1944-lane", true)]
    [InlineData("feature/some-work", true)]
    [InlineData("mainline", true)]
    [InlineData("main", false)]
    [InlineData("Main", false)]
    [InlineData("master", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task Only_a_non_trunk_branch_is_recorded(string? branch, bool recorded)
    {
        Assert.Equal(recorded, RoomDeliveryBranch.IsDeliveryBranch(branch));

        await RoomDeliveryBranch.RecordAsync(_room, branch, TestContext.Current.CancellationToken);

        Assert.Equal(recorded, File.Exists(Path.Combine(_room, DeliveryReferenceOutputNames.Branch)));
        Assert.Equal(recorded ? branch : null, RoomDeliveryBranch.TryRead(_room));
    }

    /// <summary>
    /// A room with nothing recorded reads as absent rather than as an empty branch — every room
    /// dispatched before #1944 is this case, and an empty string would join to a PR with an empty head
    /// ref rather than to none.
    /// </summary>
    [Fact]
    public async Task A_room_with_no_record_and_a_room_with_a_blank_one_both_read_as_absent()
    {
        Assert.Null(RoomDeliveryBranch.TryRead(_room));

        await File.WriteAllTextAsync(
            Path.Combine(_room, DeliveryReferenceOutputNames.Branch),
            "   \n",
            TestContext.Current.CancellationToken);

        Assert.Null(RoomDeliveryBranch.TryRead(_room));
    }
}
