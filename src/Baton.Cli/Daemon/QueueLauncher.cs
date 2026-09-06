using Baton.Queue;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Daemon;

/// <summary>What the scheduler asks the launcher to start.</summary>
/// <param name="Item">The queued item.</param>
/// <param name="Tier">Its resolved adapter/model/effort — <see cref="QueueTierTable.Resolve"/>'s answer, never re-derived here.</param>
/// <param name="RoomDirectory">
/// The room to dispatch into — <see cref="QueueLauncher.RoomDirectoryFor"/>'s answer, chosen by the
/// scheduler rather than here because the scheduler writes it onto the item BEFORE the launch starts
/// (<c>QueueSchedulerService.EvaluateAsync</c> states why). Passed rather than re-derived: two
/// generators would put the item's recorded room and the dispatch's actual room one GUID apart.
/// </param>
public sealed record QueueLaunchRequest(QueueItem Item, QueueTierResolution Tier, string RoomDirectory);

/// <summary>
/// How a launch attempt ended.
/// </summary>
/// <param name="RoomDirectory">The room the dispatch provisioned; present even for a failure that got that far.</param>
/// <param name="RunwayHeld">
/// True when <c>baton dispatch</c>'s runway gate refused. Distinct from <paramref name="Error"/>
/// because the two have opposite consequences for the item's state — spec/baton.md §13 names them.
/// </param>
/// <param name="Error">Why the launch failed, or null when it started.</param>
public sealed record QueueLaunchOutcome(string? RoomDirectory, bool RunwayHeld = false, string? Error = null);

/// <summary>
/// Turns a queued item into a running lane, through the SAME code path <c>baton dispatch</c> uses
/// (#1934 slice 1, item 2) — <see cref="DispatchCommand.ExecuteAsync"/> in-process, not a shell-out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not a shell-out</b> — spec/baton.md §13 has the argument. Concretely, in-process is what
/// gives this method a typed exception and a live evaluator to observe, where a child process would
/// have offered one integer.
/// </para>
/// <para>
/// <b>A hold is read off the evaluator, never off the exception</b> — spec/baton.md §13 has the
/// argument. Mechanically: this method wraps
/// <see cref="DispatchCommand.CreateDiskRunwayEvaluatorAsync"/> in <c>Observe</c>, which returns each
/// <see cref="RunwayDecision"/> unchanged and sets a local flag when one is
/// <see cref="RunwayDecision.IsHold"/>; the two <c>catch</c> arms below then differ only by that flag.
/// </para>
/// <para>
/// <b>The lane is not awaited to completion.</b> This method returns as soon as the outcome is known;
/// the pump keeps running on its own task. <see cref="QueueSchedulerService"/>'s remarks have the
/// shutdown posture that follows.
/// </para>
/// </remarks>
public static class QueueLauncher
{
    /// <summary>
    /// Starts <paramref name="request"/> and returns as soon as the outcome is known: a refusal
    /// (hold or error) or a provisioned room whose pump is now running detached.
    /// </summary>
    /// <param name="cancellationToken">
    /// Bounds only the work BEFORE the outcome is known — the evaluator read and the refusal poll. The
    /// scheduler hands in <see cref="CancellationToken.None"/> (#1939 review): the dispatch below runs
    /// on that token by design, and cancelling the observation of a launch that is still starting is
    /// what left an item recorded as not-launched while its worker ran. The poll has its own deadline,
    /// so nothing here is unbounded.
    /// </param>
    public static async Task<QueueLaunchOutcome> LaunchAsync(QueueLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = request.Item;
        if (!File.Exists(item.SpecFile))
        {
            return new QueueLaunchOutcome(null, Error: $"spec file '{item.SpecFile}' is gone");
        }

        var roomDirectory = request.RoomDirectory;
        var options = BuildOptions(request);

        var held = false;
        var evaluator = await DispatchCommand.CreateDiskRunwayEvaluatorAsync(cancellationToken).ConfigureAwait(false);
        RunwayDecision Observe(string vendor)
        {
            var decision = evaluator(vendor);
            held |= decision.IsHold;
            return decision;
        }

        // The dispatch runs to Terminal, which for an implement lane is tens of minutes. It is started
        // here and NOT awaited: CancellationToken.None, deliberately, so stopping the daemon does not
        // arrest a lane it launched (QueueSchedulerService's own remarks state that posture and its
        // cost). The continuation is what observes the task's exception -- an unobserved faulted task
        // would surface, if at all, as a process-level UnobservedTaskException far from here.
        var pump = Task.Run(
            () => DispatchCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, CancellationToken.None, evaluateRunway: Observe),
            CancellationToken.None);

        // One bounded wait for a refusal. Every pre-provision refusal DispatchCommand can make -- drain
        // marker, bad spec, unknown role, runway hold -- happens before it creates the room directory,
        // so "the room now exists" is the discriminator between "refused" and "running", and it is the
        // engine's own ordering rather than a timing guess. The timeout is the backstop for a dispatch
        // that is neither: it reports launched, which is true, and the room's own record takes over.
        var deadline = DateTimeOffset.UtcNow + RefusalWindow;
        while (!pump.IsCompleted && !Directory.Exists(roomDirectory) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(RefusalPollInterval, cancellationToken).ConfigureAwait(false);
        }

        if (pump.IsCompleted)
        {
            try
            {
                var result = await pump.ConfigureAwait(false);
                await TerminalSettleRecorder.RecordAsync(result, CancellationToken.None).ConfigureAwait(false);
                return new QueueLaunchOutcome(roomDirectory);
            }
            catch (Exception ex) when (ex is BatonFlowException or CliArgumentException)
            {
                return held
                    ? new QueueLaunchOutcome(null, RunwayHeld: true)
                    : new QueueLaunchOutcome(Directory.Exists(roomDirectory) ? roomDirectory : null, Error: ex.Message);
            }
        }

        // Still running: settle it when it finishes, so a queue-launched room gets the same
        // terminal.json and ledger rows a `baton dispatch` from a terminal would (TerminalSettleRecorder
        // is that block, shared rather than copied).
        _ = pump.ContinueWith(
            async completed =>
            {
                if (completed.IsFaulted)
                {
                    var reason = completed.Exception?.GetBaseException().Message ?? "the lane faulted after launch";
                    Console.Error.WriteLine(
                        $"QueueLauncher: lane '{item.Tag}' in '{roomDirectory}' failed after launch: {reason}");
                    await RecordPostLaunchFaultAsync(item.Tag, roomDirectory, reason).ConfigureAwait(false);
                    return;
                }

                try
                {
                    await TerminalSettleRecorder.RecordAsync(completed.Result, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"QueueLauncher: could not record the settle for '{item.Tag}': {ex.Message}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        return new QueueLaunchOutcome(roomDirectory);
    }

    /// <summary>
    /// The verdict a lane that faulted <em>after</em> launch would otherwise never leave behind
    /// (#1939 review). A dispatch that throws twenty minutes in reaches no Terminal state, so
    /// <see cref="TerminalSettleRecorder"/> never runs and the room carries no <c>terminal.json</c> —
    /// and <c>QueueSchedulerService.ResolveFinishedItemsAsync</c>, which reads exactly that file,
    /// leaves the item <see cref="QueueItemState.Launched"/> forever. This writes the room's own
    /// failure so the item resolves the same way every other settled room does: through the sentinel,
    /// not through a second channel.
    /// </summary>
    /// <remarks>
    /// Two things it deliberately does NOT do — both rulings, stated in spec/baton.md §13 with the
    /// argument for each: it never creates the room directory (the scheduler's own sweep is what
    /// resolves an item whose room was never provisioned), and it never replaces a sentinel the room
    /// already carries.
    /// </remarks>
    internal static async Task RecordPostLaunchFaultAsync(string tag, string roomDirectory, string reason)
    {
        try
        {
            if (!Directory.Exists(roomDirectory)
                || await TerminalSentinelWriter.TryReadAsync(roomDirectory, CancellationToken.None)
                    .ConfigureAwait(false) is not null)
            {
                return;
            }

            await TerminalSentinelWriter.WriteAsync(
                roomDirectory,
                new WorkflowStatusView(
                    WorkflowOutcome.Failed, [], [],
                    $"the queue-launched lane '{tag}' faulted after launch: {reason}"),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"QueueLauncher: could not record the post-launch fault for '{tag}' in '{roomDirectory}': {ex.Message}");
        }
    }

    /// <summary>
    /// The room a queued item dispatches into: <c>queue-&lt;tag&gt;-&lt;8 hex&gt;</c> under
    /// <see cref="BatonPaths.Rooms"/>. Here rather than in the scheduler that calls it because the
    /// naming is this launcher's convention — the tag leads so a room is traceable to its item by
    /// eye, and the suffix keeps a re-added tag from colliding with its own earlier room.
    /// </summary>
    internal static string RoomDirectoryFor(QueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Path.Combine(BatonPaths.Rooms, $"queue-{item.Tag}-{Guid.NewGuid().ToString("N")[..8]}");
    }

    /// <summary>How long to wait for a pre-provision refusal before reporting the lane launched.
    /// Every refusal happens before the room directory is created, so this is a backstop, not the
    /// mechanism.</summary>
    public static readonly TimeSpan RefusalWindow = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RefusalPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The item plus its tier, as <c>baton dispatch</c>'s own options. Kept separate from
    /// <see cref="LaunchAsync"/> so a test can assert what the queue forwards without running a
    /// dispatch.
    /// </summary>
    internal static DispatchOptions BuildOptions(QueueLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = request.Item;
        var tier = request.Tier;

        return new DispatchOptions(
            Name: item.Role,
            SpecFilePath: item.SpecFile,
            RoomDirectoryPath: request.RoomDirectory,
            Adapter: tier.Adapter,
            WorkspaceDirectory: item.Workspace,
            Model: tier.Model,
            Effort: tier.Effort,
            Timeout: item.TimeoutMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
            // WorkerBindingConfigEntry.Label is the bindings field spec/baton.md §13 requires the
            // override's justification to reach; it doubles as the tag-to-room trace, so one field
            // carries both rather than a new one carrying half. Sanitized through the same
            // SanitizeLabel the CLI flag uses -- including its 60-character cap, which is why the tag
            // leads: a truncated justification still leaves the room identifiable.
            Label: DispatchOptionsParser.SanitizeLabel(
                tier.IsOverride && tier.OverrideReason is { Length: > 0 } reason
                    ? $"{item.Tag} — tier override: {reason}"
                    : item.Tag),
            TokenBudget: item.TokenBudget,
            MaxToolSteps: item.MaxToolSteps,
            OverrideRunwayReason: item.OverrideRunwayReason);
    }
}
