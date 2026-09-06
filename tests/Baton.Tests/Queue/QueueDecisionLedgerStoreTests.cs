using System.Text.Json;
using Baton.Queue;

namespace Baton.Tests.Queue;

/// <summary>
/// #1934 slice 1, item 6: every decision is recorded, and the ledger records TRANSITIONS rather than
/// a per-tick heartbeat.
/// </summary>
public sealed class QueueDecisionLedgerStoreTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 5, 23, 30, 0, TimeSpan.Zero);

    private static string TempLedgerPath() =>
        Path.Combine(Path.GetTempPath(), $"baton-queue-ledger-{Guid.NewGuid():N}.jsonl");

    private static QueueDecisionEntry Wait(string? tag, QueueWaitReason reason, double liveWeight = 2.0) =>
        new(At, tag, QueueDecisionEntry.Waited, QueueWaitReasons.Token(reason), liveWeight, 5.5, 2.0);

    [Fact]
    public async Task A_launch_records_the_tier_the_override_and_the_room()
    {
        var path = TempLedgerPath();
        try
        {
            var entry = new QueueDecisionEntry(
                At, "1934-queue", QueueDecisionEntry.Launched, null, 1.5, 6.25, 1.2,
                "engine", "claude", "sonnet", "high", TierOverride: true,
                OverrideReason: "cheap sweep", Room: @"C:\baton\rooms\queue-1934-queue-abcd1234");

            await QueueDecisionLedgerStore.AppendAsync(entry, null, path);
            var read = await QueueDecisionLedgerStore.ReadAllAsync(path);

            var row = Assert.Single(read);
            Assert.Equal("launched", row.Decision);
            Assert.Equal("engine", row.Tier);
            Assert.Equal("sonnet", row.Model);
            Assert.True(row.TierOverride);
            Assert.Equal("cheap sweep", row.OverrideReason);
            Assert.Equal(@"C:\baton\rooms\queue-1934-queue-abcd1234", row.Room);
            Assert.Equal(6.25, row.FreeGb);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task An_unchanged_verdict_is_not_re_appended_but_a_changed_one_is()
    {
        var path = TempLedgerPath();
        try
        {
            var key = await QueueDecisionLedgerStore.AppendAsync(Wait("a", QueueWaitReason.Memory), null, path);
            // Same verdict, different counters: still the same standing wait, so it collapses.
            key = await QueueDecisionLedgerStore.AppendAsync(Wait("a", QueueWaitReason.Memory, liveWeight: 3.0), key, path);
            Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(path));

            // Control: a DIFFERENT reason for the same tag is a transition and does append. Without
            // this arm the test could not tell "collapsed" from "never writes anything".
            await QueueDecisionLedgerStore.AppendAsync(Wait("a", QueueWaitReason.Slots), key, path);
            Assert.Equal(2, (await QueueDecisionLedgerStore.ReadAllAsync(path)).Count);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task A_different_tag_with_the_same_reason_is_a_new_fact()
    {
        var path = TempLedgerPath();
        try
        {
            var key = await QueueDecisionLedgerStore.AppendAsync(Wait("a", QueueWaitReason.Slots), null, path);
            await QueueDecisionLedgerStore.AppendAsync(Wait("b", QueueWaitReason.Slots), key, path);

            Assert.Equal(2, (await QueueDecisionLedgerStore.ReadAllAsync(path)).Count);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task An_absent_reading_is_written_absent_rather_than_as_zero()
    {
        var path = TempLedgerPath();
        try
        {
            await QueueDecisionLedgerStore.AppendAsync(
                new QueueDecisionEntry(At, "a", QueueDecisionEntry.Waited, "slots", 1.0, null, 2.0), null, path);

            var line = (await File.ReadAllLinesAsync(path))[0];
            using var document = JsonDocument.Parse(line);
            // A fabricated 0 would read as "no memory free", which is the opposite of "unmeasured".
            Assert.False(document.RootElement.TryGetProperty("freeGb", out _));
            Assert.Equal(2.0, document.RootElement.GetProperty("floorGb").GetDouble());
        }
        finally
        {
            Delete(path);
        }
    }

    [Theory]
    [InlineData(QueueWaitReason.NoItems, "no-items")]
    [InlineData(QueueWaitReason.Hold, "hold")]
    [InlineData(QueueWaitReason.Gap, "gap")]
    [InlineData(QueueWaitReason.Memory, "memory")]
    [InlineData(QueueWaitReason.Slots, "slots")]
    [InlineData(QueueWaitReason.RunwayHeld, "runway-held")]
    public void Every_wait_reason_has_the_ledger_token_the_issue_fixed(QueueWaitReason reason, string token) =>
        Assert.Equal(token, QueueWaitReasons.Token(reason));

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not this test's subject.
        }
    }
}
