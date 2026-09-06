using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Baton.Cli.Mcp;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Microsoft.Extensions.Hosting;

namespace Baton.Cli.Daemon;

/// <summary>#734: see spec/baton.md §7's "DeliveryPoller" bullet for the design this implements — not restated here.</summary>
public sealed class DeliveryPoller : BackgroundService
{
    public const string IntervalSecondsEnvironmentVariable = "BATON_DELIVERY_POLL_INTERVAL_SECONDS";

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    // Same overflow/hot-loop rationale as RoomRetentionSweep.MinInterval/MaxInterval: the upper bound
    // keeps a pathological env value from overflowing TimeSpan.FromSeconds, the lower bound keeps a
    // typo from hot-looping a poll that hits the network on every iteration.
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(1);

    private static readonly HashSet<string> FailingConclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "FAILURE", "CANCELLED", "TIMED_OUT", "ACTION_REQUIRED", "STARTUP_FAILURE",
    };

    // `statusCheckRollup` is a GraphQL union of CheckRun (status/conclusion) and the legacy commit-status
    // StatusContext shape (state/context) -- gh's own JSON flattens both into one object per entry with
    // the inapplicable fields absent. A CI provider that posts commit statuses (Jenkins, CircleCI classic,
    // Codecov) surfaces only the second shape; treating an entry with neither known field as complete would
    // fabricate green before anything actually reported (the bug this pair of sets closes).
    private static readonly HashSet<string> FailingStates = new(StringComparer.OrdinalIgnoreCase) { "ERROR", "FAILURE" };
    private static readonly HashSet<string> PendingStates = new(StringComparer.OrdinalIgnoreCase) { "PENDING", "EXPECTED" };

    private readonly IGhCliRunner _gh;
    private bool _ghMissingWarned;
    private readonly HashSet<string> _missingProjectRootWarnedRooms = new(StringComparer.Ordinal);

    public DeliveryPoller()
        : this(new GhCliRunner())
    {
    }

    public DeliveryPoller(IGhCliRunner gh)
    {
        _gh = gh;
    }

    public static TimeSpan GetInterval()
    {
        var val = BatonEnvironmentSnapshot.Current.DeliveryPollIntervalSecondsOverride;
        if (!string.IsNullOrWhiteSpace(val) &&
            double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0)
        {
            return TimeSpan.FromSeconds(Math.Clamp(seconds, MinInterval.TotalSeconds, MaxInterval.TotalSeconds));
        }

        return DefaultInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"DeliveryPoller: sweep iteration failed: {ex.Message}");
            }

            // #1981: see DaemonTickLedger for why every service reports its tick here.
            DaemonTickLedger.Instance.RecordTick(
                nameof(DeliveryPoller), Stopwatch.GetElapsedTime(started), GetInterval());

            try
            {
                await Task.Delay(GetInterval(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One tick's worth of work over every discovered room — public entry point for tests.</summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        var discovered = await FleetStatusTool.DiscoverRoomsAsync([], cancellationToken).ConfigureAwait(false);
        foreach (var room in discovered)
        {
            try
            {
                await PollRoomAsync(room, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"DeliveryPoller: room '{room.RoomDir}' failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// One room's worth of work — internal so tests can drive a single fixture directly rather than
    /// standing up a full fleet scan. <paramref name="warningSink"/> defaults to <see cref="Console.Error"/>;
    /// a test supplies its own to avoid the shared-stream race a global <c>Console.Error</c> capture
    /// invites across parallel tests (the same reason <c>RoomRetentionSweep.PruneRoomAsync</c> takes
    /// one).
    /// </summary>
    internal async Task PollRoomAsync(
        FleetStatusTool.DiscoveredRoom room, CancellationToken cancellationToken, TextWriter? warningSink = null)
    {
        var view = await FleetStatusTool.ProcessRoomAsync(room.RoomDir, includeTerminal: true, cancellationToken)
            .ConfigureAwait(false);
        if (view is null)
        {
            return;
        }

        var reference = DeliveryReferenceResolver.Resolve(view.Outputs);
        if (reference?.PullRequestNumber is not { } pullRequestNumber || reference.PullRequestReference is not { } prArgument)
        {
            // No declared delivery output resolved yet (or only a branch, no PR number yet) -- the
            // poller never starts for this room until a PR number is there to poll against.
            return;
        }

        var logPath = Path.Combine(room.RoomDir, BatonPaths.FlowLogFileName);
        var events = await new FlowEventLogReader(logPath).ReadAllAsync(cancellationToken).ConfigureAwait(false);

        if (events.Any(e => e is FlowEvent.DeliveryMerged))
        {
            // Terminal: merged or closed-unmerged already recorded once. Never polled again.
            return;
        }

        // A URL reference pins its own repo (spec/baton.md §2), so `gh pr view <url>` needs no cwd
        // inside that repo's checkout. A bare number relies on `gh` resolving the repo from the cwd it
        // runs in, so that shape falls back to the room's own spec/baton.md §8 registry project root, and is skipped
        // (logged once per room) when the room has none.
        var isUrlReference = prArgument.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || prArgument.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var workingDirectory = isUrlReference ? room.RoomDir : room.Project;
        if (workingDirectory is null)
        {
            if (_missingProjectRootWarnedRooms.Add(room.RoomDir))
            {
                (warningSink ?? Console.Error).WriteLine(
                    $"DeliveryPoller: '{room.RoomDir}' declares a bare PR number with no registered spec/baton.md §8 "
                    + "project root, so 'gh' has no repo context to run in -- skipped. Reported once per room.");
            }

            return;
        }

        var result = await _gh.RunAsync(
                workingDirectory,
                ["pr", "view", prArgument, "--json", "state,statusCheckRollup"],
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Started)
        {
            // Unambiguous and permanent for this process: `gh` is not on PATH at all. Every other room
            // will hit the identical failure, so one line for the whole daemon process is enough.
            if (!_ghMissingWarned)
            {
                _ghMissingWarned = true;
                (warningSink ?? Console.Error).WriteLine(
                    "DeliveryPoller: 'gh' was not found on PATH -- a missing forge must not fail the "
                    + "daemon, so delivery polling records nothing until it is. Reported once per daemon process.");
            }

            return;
        }

        if (result.ExitCode != 0)
        {
            // `gh` ran but refused -- unauthenticated, a stale/renumbered PR, a transient API error.
            // Per-room and per-tick, not latched: a bad PR number on one room must never permanently
            // silence a genuine problem on every other room the way one shared flag would.
            (warningSink ?? Console.Error).WriteLine(
                $"DeliveryPoller: 'gh pr view' failed for '{room.RoomDir}' (PR {prArgument}): {result.Stderr.Trim()}");
            return;
        }

        var observed = ParsePrView(result.Stdout);
        if (observed is null)
        {
            return;
        }

        var alreadyOpened = events.Any(
            e => e is FlowEvent.DeliveryPrOpened opened && opened.PullRequestNumber == pullRequestNumber);
        var lastChecksState = events.LastOrDefault(
            e => (e is FlowEvent.DeliveryChecksGreen green && green.PullRequestNumber == pullRequestNumber)
                || (e is FlowEvent.DeliveryChecksRed red && red.PullRequestNumber == pullRequestNumber));

        var toAppend = new List<FlowEvent>();
        if (!alreadyOpened)
        {
            toAppend.Add(new FlowEvent.DeliveryPrOpened(pullRequestNumber, reference.Branch));
        }

        if (observed.Checks == DeliveryCheckState.Green && lastChecksState is not FlowEvent.DeliveryChecksGreen)
        {
            toAppend.Add(new FlowEvent.DeliveryChecksGreen(pullRequestNumber));
        }
        else if (observed.Checks == DeliveryCheckState.Red && lastChecksState is not FlowEvent.DeliveryChecksRed)
        {
            toAppend.Add(new FlowEvent.DeliveryChecksRed(pullRequestNumber));
        }

        if (observed.Merged is { } merged)
        {
            toAppend.Add(new FlowEvent.DeliveryMerged(pullRequestNumber, merged));
        }

        if (toAppend.Count == 0)
        {
            return;
        }

        await using var writer = new FlowEventLogWriter(logPath);
        foreach (var flowEvent in toAppend)
        {
            await writer.AppendAsync(flowEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private enum DeliveryCheckState { Pending, Green, Red }

    private sealed record ObservedPrState(DeliveryCheckState Checks, bool? Merged);

    /// <summary>
    /// Parses <c>gh pr view --json state,statusCheckRollup</c>'s stdout. Lenient by design: an
    /// unrecognized or empty shape reads as Pending/not-terminal rather than throwing, since a
    /// malformed response this tick is retried next tick regardless. Never reads an unrecognized check
    /// entry as complete-and-passing -- see <see cref="FailingStates"/>/<see cref="PendingStates"/>'
    /// own remarks for the shape this guards against.
    /// </summary>
    private static ObservedPrState? ParsePrView(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            bool? merged = null;
            if (root.TryGetProperty("state", out var stateElem) && stateElem.ValueKind == JsonValueKind.String)
            {
                var state = stateElem.GetString();
                if (string.Equals(state, "MERGED", StringComparison.OrdinalIgnoreCase))
                {
                    merged = true;
                }
                else if (string.Equals(state, "CLOSED", StringComparison.OrdinalIgnoreCase))
                {
                    merged = false;
                }
            }

            var checks = DeliveryCheckState.Pending;
            if (root.TryGetProperty("statusCheckRollup", out var rollup)
                && rollup.ValueKind == JsonValueKind.Array
                && rollup.GetArrayLength() > 0)
            {
                var sawFailure = false;
                var allComplete = true;
                foreach (var check in rollup.EnumerateArray())
                {
                    var conclusion = check.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    var status = check.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    var legacyState = check.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;

                    if (conclusion is not null || status is not null)
                    {
                        // The CheckRun shape.
                        if (conclusion is not null && FailingConclusions.Contains(conclusion))
                        {
                            sawFailure = true;
                        }

                        if (!string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
                        {
                            allComplete = false;
                        }
                    }
                    else if (legacyState is not null)
                    {
                        // The legacy commit-status (StatusContext) shape.
                        if (FailingStates.Contains(legacyState))
                        {
                            sawFailure = true;
                        }

                        if (PendingStates.Contains(legacyState))
                        {
                            allComplete = false;
                        }
                    }
                    else
                    {
                        // Neither shape recognized -- never fabricate green off a check this cannot read.
                        allComplete = false;
                    }
                }

                checks = sawFailure ? DeliveryCheckState.Red
                    : allComplete ? DeliveryCheckState.Green
                    : DeliveryCheckState.Pending;
            }

            return new ObservedPrState(checks, merged);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
