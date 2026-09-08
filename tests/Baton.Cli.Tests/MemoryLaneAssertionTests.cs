using Baton.Memory;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

/// <summary>
/// Who <c>baton memory add</c> records as having asserted an entry (#2071) — and, above all, the two
/// states it must never collapse: "no lane" and "a lane I could not read".
/// </summary>
public sealed class MemoryLaneAssertionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baton-2071-lane-{Guid.NewGuid():N}");

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_root);

    private string RoomWithBindings(string room, string? bindingsJson)
    {
        var roomDirectory = Path.Combine(_root, "rooms", room);
        Directory.CreateDirectory(Path.Combine(roomDirectory, "artifacts"));
        if (bindingsJson is not null)
        {
            File.WriteAllText(BatonPaths.RoomBindingsFile(roomDirectory), bindingsJson);
        }

        return Path.Combine(roomDirectory, "artifacts");
    }

    [Fact]
    public void No_artifacts_root_is_the_operator()
    {
        Assert.Equal(AuthoredMemory.Operator, MemoryLaneAssertion.Resolve(null));
        Assert.Equal(AuthoredMemory.Operator, MemoryLaneAssertion.Resolve(string.Empty));
    }

    [Fact]
    public void A_lane_with_one_binding_asserts_role_vendor_room()
    {
        var artifacts = RoomWithBindings(
            "queue-2071",
            """
            {
              "implement": {
                "Adapter": "claude",
                "Timeout": "00:25:00",
                "Contract": {
                  "WorkerName": "implement",
                  "RequiredInputs": [],
                  "ProducedOutputs": [{ "Name": "report.md" }],
                  "OptionalMetadata": []
                },
                "PromptTemplate": "do the thing"
              }
            }
            """);

        Assert.Equal("implement/claude/queue-2071", MemoryLaneAssertion.Resolve(artifacts));
    }

    /// <summary>
    /// The polarity arm that matters: a lane whose bindings cannot be read is still a LANE. Reporting
    /// the operator here would file a worker's assertion under a person's name, in an append-only
    /// store, which is the one mislabelling #2071's deferred grant slice cannot undo.
    /// </summary>
    [Fact]
    public void A_lane_with_unreadable_bindings_is_never_the_operator()
    {
        var unparseable = MemoryLaneAssertion.Resolve(RoomWithBindings("queue-broken", "{ not json"));
        Assert.NotEqual(AuthoredMemory.Operator, unparseable);
        Assert.Equal("lane/queue-broken", unparseable);

        var absent = MemoryLaneAssertion.Resolve(RoomWithBindings("queue-bare", bindingsJson: null));
        Assert.NotEqual(AuthoredMemory.Operator, absent);
        Assert.Equal("lane/queue-bare", absent);
    }

    [Fact]
    public void An_artifacts_root_that_is_not_shaped_like_one_is_an_unidentified_lane()
    {
        Assert.Equal(
            MemoryLaneAssertion.UnidentifiedLane,
            MemoryLaneAssertion.Resolve(Path.Combine(_root, "rooms", "queue-x", "not-artifacts")));

        // A relative value names no directory this process can resolve the same way the lane's did.
        Assert.Equal(MemoryLaneAssertion.UnidentifiedLane, MemoryLaneAssertion.Resolve("artifacts"));
        Assert.NotEqual(AuthoredMemory.Operator, MemoryLaneAssertion.Resolve("artifacts"));
    }

    /// <summary>
    /// A swept room still identifies the lane that ran there: the reading is a shape test, not an
    /// existence test.
    /// </summary>
    [Fact]
    public void A_room_directory_that_no_longer_exists_still_names_the_lane()
    {
        var artifacts = Path.Combine(_root, "rooms", "queue-gone", "artifacts");

        Assert.Equal("lane/queue-gone", MemoryLaneAssertion.Resolve(artifacts));
    }
}
