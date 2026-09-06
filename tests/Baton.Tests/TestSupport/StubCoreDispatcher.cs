using System.Threading.Channels;
using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Tests.TestSupport;

/// <summary>
/// An <see cref="ICoreDispatcher"/> whose completion order tests control explicitly via
/// <see cref="TaskCompletionSource{TResult}"/>, in place of spawning real processes (M8 Phase 3).
/// Each call to <see cref="DispatchAsync"/> consumes the next queued result for that
/// <see cref="StepId"/> — queue rather than single-slot so a step's retries can each be armed with
/// their own outcome ahead of time — and reports the call on <see cref="DispatchStarted"/> so a
/// test can await "dispatch N has begun" without polling or sleeping.
/// </summary>
internal sealed class StubCoreDispatcher : ICoreDispatcher
{
    private readonly Lock _lock = new();
    private readonly Dictionary<StepId, Queue<TaskCompletionSource<CoreDispatchResult>>> _pendingResults = new();
    private readonly Channel<StepId> _dispatchStarted = Channel.CreateUnbounded<StepId>();

    /// <summary>Yields each <see cref="StepId"/> the moment its dispatch begins, in call order.</summary>
    public ChannelReader<StepId> DispatchStarted => _dispatchStarted.Reader;

    /// <summary>The full request most recently handed to <see cref="DispatchAsync"/> — lets a test
    /// inspect what actually went to Core (e.g. a resubmit's rebound Adapter/Model), not just which
    /// StepId dispatched.</summary>
    public ExecutionRequest? LastDispatchedRequest { get; private set; }

    /// <summary>
    /// #1929 review round 3: when set, this stub raises <c>CoreDispatchTarget.OnEngineFilesPlaced</c>
    /// with these files BEFORE it completes — standing in for the real dispatcher's placement loop,
    /// which raises the same callback before spawning. Left null, the stub places nothing and raises
    /// nothing, which is what every other test's dispatch does.
    /// </summary>
    public IReadOnlyList<Baton.Domain.EnginePlacedFile>? RaisePlacementOnDispatch { get; set; }

    /// <summary>The group labels raised alongside <see cref="RaisePlacementOnDispatch"/>.</summary>
    public IReadOnlyList<string> RaisePlacementGroups { get; set; } = [];

    /// <summary>
    /// Arms the next <see cref="DispatchAsync"/> call for <paramref name="stepId"/> to await the
    /// returned <see cref="TaskCompletionSource{TResult}"/> instead of completing immediately — the
    /// test decides when (and with what result) that dispatch finishes.
    /// </summary>
    public TaskCompletionSource<CoreDispatchResult> EnqueueResult(StepId stepId)
    {
        var completionSource = new TaskCompletionSource<CoreDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock)
        {
            if (!_pendingResults.TryGetValue(stepId, out var queue))
            {
                queue = new Queue<TaskCompletionSource<CoreDispatchResult>>();
                _pendingResults[stepId] = queue;
            }

            queue.Enqueue(completionSource);
        }

        return completionSource;
    }

    public async Task<CoreDispatchResult> DispatchAsync(
        ExecutionRequest request,
        CoreDispatchTarget target,
        CancellationToken cancellationToken = default)
    {
        // Only ever dispatched for a step-tied, process-bound request — StepId is always
        // set here.
        var stepId = request.StepId!.Value;
        LastDispatchedRequest = request;

        TaskCompletionSource<CoreDispatchResult> completionSource;
        lock (_lock)
        {
            if (!_pendingResults.TryGetValue(stepId, out var queue) || queue.Count == 0)
            {
                throw new InvalidOperationException(
                    $"StubCoreDispatcher: no result enqueued for step '{stepId}' (attempt count exceeds test setup).");
            }

            completionSource = queue.Dequeue();
        }

        _dispatchStarted.Writer.TryWrite(stepId);

        if (RaisePlacementOnDispatch is { Count: > 0 } placed && target.OnEngineFilesPlaced is { } onPlaced)
        {
            await onPlaced(placed, RaisePlacementGroups).ConfigureAwait(false);
        }

        // Mirrors CoreDispatcher's real contract (M10 Phase 2): a cancelled dispatch token never
        // throws, it resolves to a Cancelled result, exactly like BatonCancelException does for a real
        // BatonTask.RunAsync. Not observed at all when the caller passes a token that can never fire
        // (the pre-Phase-2 default), so every earlier test's un-cancellable stub calls behave
        // identically to before.
        if (!cancellationToken.CanBeCanceled)
        {
            return await completionSource.Task.ConfigureAwait(false);
        }

        var cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);
        var winner = await Task.WhenAny(completionSource.Task, cancellationTask).ConfigureAwait(false);
        if (winner == cancellationTask)
        {
            return new CoreDispatchResult(0, CoreExitReason.CancelRequested);
        }

        return await completionSource.Task.ConfigureAwait(false);
    }
}
