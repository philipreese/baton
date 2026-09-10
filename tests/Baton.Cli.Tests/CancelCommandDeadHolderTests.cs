using System.Text.Json;
using Baton.Cli.Tests.TestSupport;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Store;
using Baton.Vendors;
using static Baton.Cli.Tests.TestSupport.ParkedStepFixture;
using static Baton.Cli.Tests.TestSupport.ProcessIdentityFixture;

namespace Baton.Cli.Tests;

/// <summary>
/// The dead-holder shape (#1586/#1607): a quota-parked room whose pump died mid-park, leaving a
/// stale <c>flow.lock.holder</c> beside a FREE lock. Until #2073 <c>baton cancel</c> REFUSED this
/// room, because acting on it meant running a pump that would hang on the park, and no verb could
/// settle it ("#1586's tracked 'baton settle' design"). #2073 is that verb: the free lock is settled
/// with slice one's parked-arm fact (<see cref="FlowEvent.StepRetryForeclosed"/>), attributed to
/// <see cref="CancelCommand.DiagnosticName"/>, and the holder record the old gate protected by
/// refusing is carried into the fact's reason instead.
/// </summary>
public class CancelCommandDeadHolderTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task A_parked_room_with_a_dead_lock_holder_is_settled_by_foreclosure_naming_the_dead_holder()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var (_, logPath, parkedExecutionId, _) = await WriteParkedStepFixtureAsync(testRoot, roomDirectory);

            var (deadPid, deadStartTime) = DeadProcessIdentity();
            var holderPath = Path.Combine(roomDirectory, ConcurrencyGuard.FlowHolderFileName);
            await File.WriteAllTextAsync(
                holderPath,
                JsonSerializer.Serialize(new
                {
                    HolderDescription = $"baton run pump (pid {deadPid})",
                    Pid = deadPid,
                    AcquiredAtUtc = deadStartTime.UtcDateTime.AddMinutes(10),
                    ProcessStartTimeUtc = deadStartTime.UtcDateTime,
                }),
                TestContext.Current.CancellationToken);
            Assert.False(ConcurrencyGuard.IsHeld(roomDirectory), "the lock must read as free -- only a stale sidecar is being simulated");

            var result = await CancelCommand.ExecuteAsync(
                new CancelOptions(roomDirectory, ExecutionId: null, BindingsFilePath: "ignored", Reason: "engine died mid-park"),
                Adapters,
                TestContext.Current.CancellationToken,
                pumpAnswerWindow: TimeSpan.FromMilliseconds(200));

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.False(result.CancellationQueued);

            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            var foreclosed = Assert.Single(events.OfType<FlowEvent.StepRetryForeclosed>());
            Assert.Equal(parkedExecutionId, foreclosed.ForExecutionId);
            Assert.Equal(CancelCommand.DiagnosticName, foreclosed.ForeclosedBy);
            Assert.StartsWith(CancelCommand.ArrestReasonPrefix, foreclosed.Reason, StringComparison.Ordinal);
            Assert.Contains("engine died mid-park", foreclosed.Reason, StringComparison.Ordinal);
            Assert.Contains($"baton run pump (pid {deadPid})", foreclosed.Reason, StringComparison.Ordinal);

            // Slice one's split, polarity: the parked arm gets a foreclosure, never a second
            // ExecutionFailed (which would leave RetryNotBefore set and the room reading Running).
            Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.DoesNotContain(events, e => e is FlowEvent.CancellationRequested);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// No sidecar at all — the <see cref="Baton.Outcomes.EngineLivenessProbe"/> Unknown case that
    /// #1607 widened the old gate to refuse. The lock try answers the question the sidecar could not:
    /// free means no pump, and the room settles the same way, with the reason saying no holder record
    /// existed rather than naming one.
    /// </summary>
    [Fact]
    public async Task A_parked_room_with_no_holder_record_at_all_is_settled_the_same_way()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var (_, logPath, parkedExecutionId, _) = await WriteParkedStepFixtureAsync(testRoot, roomDirectory);
            Assert.False(File.Exists(Path.Combine(roomDirectory, ConcurrencyGuard.FlowHolderFileName)));

            var result = await CancelCommand.ExecuteAsync(
                new CancelOptions(roomDirectory, parkedExecutionId.Value, BindingsFilePath: "ignored"),
                Adapters,
                TestContext.Current.CancellationToken,
                pumpAnswerWindow: TimeSpan.FromMilliseconds(200));

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var foreclosed = Assert.Single((await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken)).OfType<FlowEvent.StepRetryForeclosed>());
            Assert.Equal(CancelCommand.DiagnosticName, foreclosed.ForeclosedBy);
            Assert.DoesNotContain("last recorded", foreclosed.Reason, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Explicit_cancel_forecloses_an_unknown_reset_park_makes_it_terminal_and_is_idempotent()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var (_, logPath, executionId) = await WriteUnknownResetParkedStepFixtureAsync(testRoot, roomDirectory);

            var first = await CancelCommand.ExecuteAsync(
                new CancelOptions(roomDirectory, executionId.Value, BindingsFilePath: "ignored"),
                Adapters,
                TestContext.Current.CancellationToken,
                pumpAnswerWindow: TimeSpan.FromMilliseconds(200));

            Assert.Equal(WorkflowStatus.Terminal, first.State.Status);
            var step = Assert.Single(first.State.Steps);
            Assert.True(step.RetryForeclosed);
            Assert.Null(step.RetryNotBefore);
            Assert.Equal(FailureClassification.ExhaustedUntil, step.LatestFailureClassification);
            var eventsAfterFirstCancel = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(eventsAfterFirstCancel.OfType<FlowEvent.StepRetryForeclosed>());

            var repeat = await CancelCommand.ExecuteAsync(
                new CancelOptions(roomDirectory, executionId.Value, BindingsFilePath: "ignored"),
                Adapters,
                TestContext.Current.CancellationToken,
                pumpAnswerWindow: TimeSpan.FromMilliseconds(200));

            Assert.True(repeat.CancelWasNoOp);
            Assert.Equal(WorkflowStatus.Terminal, repeat.State.Status);
            Assert.Single((await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken))
                .OfType<FlowEvent.StepRetryForeclosed>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }
}
