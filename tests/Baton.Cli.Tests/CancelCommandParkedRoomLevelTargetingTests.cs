using Baton.Cli.Tests.TestSupport;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Vendors;
using static Baton.Cli.Tests.TestSupport.ParkedStepFixture;

namespace Baton.Cli.Tests;

/// <summary>
/// #1607: proves the primary behaviour change — a bare <c>baton cancel &lt;room&gt;</c> (no
/// <c>--execution</c>) against a room whose only step is quota-parked no longer refuses with the
/// #1605 "a quota-parked step never shows as Running" hint. Drives the room-level path against a room
/// whose <c>flow.lock</c> is already held (simulating a live <c>baton run</c> pump) so this test stays
/// deterministic — the real cross-process live-pump case, and the parked-target resolution once the
/// poller re-resolves <c>latest</c>, is <see cref="CancelRequestPollerTests"/>'s own scope
/// (<c>A_latest_request_against_a_quota_parked_only_room_is_marked_on_the_registry</c>). The
/// no-live-pump case is <see cref="CancelCommandDeadHolderTests"/>'s since #2073.
/// </summary>
public class CancelCommandParkedRoomLevelTargetingTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Bare_cancel_against_a_live_pumps_quota_parked_room_writes_the_latest_request_file_instead_of_refusing()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            // A still-future RetryNotBefore (ParkedStepFixture's own 45-minute default): the exact
            // "genuinely still parked" shape that #1607 makes reachable via room-level targeting for
            // the first time. The lock IS genuinely OS-held below, so this takes the held-lock branch.
            await WriteParkedStepFixtureAsync(testRoot, roomDirectory);

            using (ConcurrencyGuard.Acquire(roomDirectory, "test holder simulating a live pump"))
            {
                var cancelOptions = new CancelOptions(roomDirectory, ExecutionId: null, BindingsFilePath: "ignored");

                // Before #1607: RunningExecutionResolver saw zero Running steps and this threw
                // CliArgumentException before ever reaching the held-lock branch below — the room's
                // only step being parked was invisible to it.
                var result = await CancelCommand.ExecuteAsync(
                    cancelOptions, Adapters, TestContext.Current.CancellationToken, pumpAnswerWindow: TimeSpan.FromMilliseconds(200));

                Assert.Equal(StepStatus.Failed, result.State.Steps.Single().Status);
                Assert.True(result.CancellationQueued, "the simulated pump never answered and never let go");

                var requestFilePath = Path.Combine(roomDirectory, CancelRequestFile.FileName);
                Assert.True(File.Exists(requestFilePath), "expected cancel.request to have been written");
                var content = await CancelRequestFile.TryReadAsync(requestFilePath, TestContext.Current.CancellationToken);
                Assert.NotNull(content);
                Assert.Equal(CancelRequestFile.LatestTarget, content.Target);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }
}
