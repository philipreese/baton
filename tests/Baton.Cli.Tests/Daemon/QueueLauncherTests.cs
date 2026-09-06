using Baton.Cli.Daemon;
using Baton.Queue;
using Baton.Status;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// The launcher's post-launch fault path (#1939 review) — three arms of
/// <see cref="QueueLauncher.RecordPostLaunchFaultAsync"/>, whose own remarks say what each is for.
/// Before it, the item this lane belonged to stayed launched forever with only a daemon stderr line
/// to say otherwise.
/// </summary>
public sealed class QueueLauncherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "baton_queue_launcher_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task A_lane_that_faults_after_launch_leaves_the_room_a_failed_sentinel_the_scheduler_can_read()
    {
        var root = CreateTempRoot();
        try
        {
            var room = Path.Combine(root, "queue-t1-abcd");
            Directory.CreateDirectory(room);

            await QueueLauncher.RecordPostLaunchFaultAsync("t1", room, "the pump threw BatonFlowException");

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);
            Assert.Equal(WorkflowOutcome.Failed, sentinel.State);
            Assert.Contains("faulted after launch", sentinel.Error!, StringComparison.Ordinal);
            Assert.Contains("BatonFlowException", sentinel.Error, StringComparison.Ordinal);

            // The whole point: the classifier the scheduler runs over this file now fails the item.
            Assert.Equal(QueueItemState.Failed, QueueSchedulerService.ClassifyTerminal(sentinel, room).State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task A_fault_before_the_room_was_provisioned_manufactures_no_room()
    {
        var root = CreateTempRoot();
        try
        {
            // The discriminating half of the pair above — QueueLauncher.RecordPostLaunchFaultAsync's
            // own remarks have the argument for why nothing is written here.
            var room = Path.Combine(root, "queue-t1-never-made");

            await QueueLauncher.RecordPostLaunchFaultAsync("t1", room, "refused before provisioning");

            Assert.False(Directory.Exists(room));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task A_room_that_already_recorded_its_own_verdict_keeps_it()
    {
        var root = CreateTempRoot();
        try
        {
            var room = Path.Combine(root, "queue-t1-refused");
            await TerminalSentinelWriter.WriteValidationRefusedAsync(
                room, "spec file 'x.md' does not exist.", Ct, tryInvocation: "pass an existing file to --spec");

            await QueueLauncher.RecordPostLaunchFaultAsync("t1", room, "some later throw");

            // Both fields of the dispatch's own record survive, which is the point of not replacing it.
            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.Equal("spec file 'x.md' does not exist.", sentinel!.Error);
            Assert.Equal("pass an existing file to --spec", sentinel.Try);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }
}
