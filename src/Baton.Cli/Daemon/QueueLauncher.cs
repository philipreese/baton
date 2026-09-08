using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Baton.Core;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Projection;
using Baton.Queue;
using Baton.Runway;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
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
/// What re-adopting one <see cref="QueueItemState.Launched"/> row on daemon start found (#2082):
/// the engine's recorded identity, what the one liveness probe said about it, and — for
/// <see cref="EngineLivenessStatus.Alive"/> — that this daemon is now supervising it again.
/// </summary>
/// <param name="Tag">The item's tag.</param>
/// <param name="RoomDirectory">The room the item launched into.</param>
/// <param name="Status">
/// <see cref="EngineLivenessProbe"/>'s answer for the room's recorded engine. <c>Alive</c> means
/// supervision resumed; <c>Dead</c> means the row was left for the dead-pump probe; <c>Unknown</c>
/// means the room recorded no probeable identity and was left as it was.
/// </param>
/// <param name="EnginePid">The pid the room's journal recorded, when it recorded one.</param>
/// <param name="Why">The probe's own reason for anything other than <c>Alive</c>.</param>
public sealed record QueueLaneAdoption(
    string Tag, string RoomDirectory, EngineLivenessStatus Status, int? EnginePid, string? Why);

/// <summary>
/// Turns a queued item into a running lane: a <b>separate <c>baton dispatch</c> process</b>, started
/// through <see cref="DetachedProcess"/> so the daemon's own exit does not take the lane with it
/// (#2082 finding 2), and re-adopted by the next daemon from the room's own journal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a process, not the in-process pump this used to be</b> — spec/baton.md §13's
/// launch/adopt contract has the argument and the measurement. The one-line version: an in-process
/// <c>Task</c> dies with its host, and the workers it spawned sat in a job that host owned, so both
/// live lanes went down with the daemon's exit 70 on 2026-09-08. The child runs the exact
/// <c>baton dispatch</c> the CLI runs, including <see cref="TerminalSettleRecorder"/> at its end.
/// </para>
/// <para>
/// <b>How a refusal is told from a launch without an exit code that could.</b> Every pre-provision
/// refusal <c>baton dispatch</c> can make — drain marker, bad spec, unknown role, runway hold —
/// happens before it creates the room directory, so "the room now exists" is the discriminator
/// between refused and running (the engine's own ordering, not a timing guess;
/// <c>DispatchPreProvisionOrderingTests</c> pins it). A hold and a bad spec both exit
/// <see cref="RunExitCode.ValidationRefused"/>, which is why the exit code alone was never enough:
/// the hold is instead read off the <b>runway admission ledger</b>, where the child already recorded
/// it against this room before refusing (<see cref="ClassifyPreProvisionExit"/>). That is the fact
/// where it exists, not a second channel.
/// </para>
/// <para>
/// <b>The lane is not awaited to completion.</b> <see cref="LaunchAsync"/> returns as soon as the
/// outcome is known; a detached continuation (<see cref="SuperviseAsync"/>) waits for the process
/// and settles what it left behind, and <see cref="AdoptLaunchedLanesAsync"/> is how a later daemon
/// picks that continuation up again.
/// </para>
/// </remarks>
public static class QueueLauncher
{
    /// <summary>
    /// Starts <paramref name="request"/> and returns as soon as the outcome is known: a refusal
    /// (hold or error) or a provisioned room whose lane process is now running detached.
    /// </summary>
    /// <param name="cancellationToken">
    /// Bounds only the refusal poll. The scheduler hands in <see cref="CancellationToken.None"/>
    /// (#1939 review): cancelling the observation of a launch that is still starting is what left an
    /// item recorded as not-launched while its worker ran. The poll has its own deadline, so nothing
    /// here is unbounded.
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
        var arguments = BuildArguments(BuildOptions(request));
        var (fileName, leadingArguments) = ResolveLaneCommand();

        Process process;
        try
        {
            // Built here rather than inside DetachedProcess: the per-file spawn scans
            // (SpawnOutputRedirectionTests, RedirectedProcessEncodingTests) read redirects and decode
            // off whichever file sets them, and DetachedProcess only refuses what they would refuse.
            process = DetachedProcess.Start(
                ChildProcessStartInfo.Create(fileName, startInfo => ConfigureLaneStartInfo(startInfo, leadingArguments, arguments)));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new QueueLaunchOutcome(null, Error: $"could not start the lane process '{fileName}': {ex.Message}");
        }

        var relay = new LaneOutputRelay(item.Tag, process);

        // One bounded wait for a refusal -- the class remarks state the discriminator. The timeout
        // is the backstop for a dispatch that is neither exited nor provisioned: it reports launched,
        // which is true, and the room's own record takes over.
        var deadline = DateTimeOffset.UtcNow + RefusalWindow;
        while (!process.HasExited && !Directory.Exists(roomDirectory) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(RefusalPollInterval, cancellationToken).ConfigureAwait(false);
        }

        if (process.HasExited)
        {
            var exitCode = await ExitCodeAsync(process).ConfigureAwait(false);
            process.Dispose();
            if (Directory.Exists(roomDirectory))
            {
                // Provisioned and already over: the child wrote its own sentinel (or, faulting, did
                // not), and done detection reads the room either way.
                await SettleExitedLaneAsync(exitCode, item.Tag, roomDirectory).ConfigureAwait(false);
                return new QueueLaunchOutcome(roomDirectory);
            }

            return ClassifyPreProvisionExit(
                exitCode, roomDirectory, await ReadAdmissionRowsAsync().ConfigureAwait(false), relay.StderrTail);
        }

        // Still running: settle it when it finishes, so a queue-launched room that faults after
        // launch gets the sentinel nothing else will write. The child does its own settle on the
        // ordinary path; this only covers the one it never reached.
        _ = SuperviseAsync(process, item.Tag, roomDirectory);

        return new QueueLaunchOutcome(roomDirectory);
    }

    /// <summary>
    /// Walks every <see cref="QueueItemState.Launched"/> row that names a room and re-adopts it
    /// (#2082): the room's journal already records the engine's pid and start time, the one liveness
    /// probe says whether that engine is still running, and an alive one gets this daemon's
    /// supervision back. Called once, on scheduler start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing here changes a row.</b> A dead engine is left exactly as found for the dead-pump
    /// probe (<c>DeadPumpProbe</c>, #2094) to record, and for done detection to resolve once a
    /// sentinel exists. A room with no probeable identity — a lane still in pre-provision when the
    /// last daemon died, or a journal this cannot read — is left as found too, and what that costs
    /// is stated rather than assumed: a lane that goes on to write its ledger and snapshot is seen by
    /// the same probe on a later tick, but a room that exists and never gets a ledger has NO closer
    /// (the roomless sweep needs the room absent, the probe needs a ledger, adoption runs once per
    /// daemon start) and is resolved only by the operator. spec/baton.md §13's adopt clause is the
    /// one home for that ruling and for why no new <see cref="QueueItemState"/> is needed.
    /// </para>
    /// <para>
    /// One line per row on the daemon's stderr, whatever the answer, so <c>daemon.log</c> says on
    /// restart what became of each lane the previous daemon left running.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<QueueLaneAdoption>> AdoptLaunchedLanesAsync(CancellationToken cancellationToken)
    {
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var adoptions = new List<QueueLaneAdoption>();
        foreach (var item in snapshot.Items)
        {
            if (item.State != QueueItemState.Launched || item.RoomDirectory is not { Length: > 0 })
            {
                continue;
            }

            var adoption = await AdoptAsync(item, cancellationToken).ConfigureAwait(false);
            adoptions.Add(adoption);
            Console.Error.WriteLine(adoption.Status switch
            {
                EngineLivenessStatus.Alive =>
                    $"QueueLauncher: re-adopted lane '{adoption.Tag}' in '{adoption.RoomDirectory}' (engine pid "
                    + $"{adoption.EnginePid}) — supervising it again",
                EngineLivenessStatus.Dead =>
                    $"QueueLauncher: lane '{adoption.Tag}' in '{adoption.RoomDirectory}' has a dead engine (pid "
                    + $"{adoption.EnginePid}); left launched for the dead-pump probe to record",
                _ =>
                    $"QueueLauncher: lane '{adoption.Tag}' in '{adoption.RoomDirectory}' has no probeable engine "
                    + $"identity ({adoption.Why}); left launched — if the room never gets a ledger nothing closes "
                    + "it, and only the operator can resolve it",
            });
        }

        return adoptions;
    }

    /// <summary>
    /// One row's re-adoption — <see cref="AdoptLaunchedLanesAsync"/>'s per-item half, internal so a
    /// test can drive a single fixture room. Probes the journal's recorded engine identity with
    /// <see cref="EngineLivenessProbe"/> and, for an alive one, resumes <see cref="SuperviseAsync"/>
    /// on that process.
    /// </summary>
    /// <remarks>
    /// The probe answers by pid and start time, but the attach that follows opens a fresh handle by
    /// pid number alone, and a pid is a number rather than an identity (<c>WorkerProcessArrest</c>'s
    /// own doc): in the microseconds between the two the lane can exit and the OS can hand its pid
    /// to an unrelated process, which this daemon would then supervise and, when it exited, settle
    /// the room off. So the start time is read again off the handle actually held and compared to
    /// the journal's, with the probe's own tolerance; a mismatch is the lane having exited, reported
    /// as such. The window is not steerable from a test, so this is asserted by construction rather
    /// than measured.
    /// </remarks>
    internal static async Task<QueueLaneAdoption> AdoptAsync(QueueItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var roomDirectory = item.RoomDirectory ?? throw new ArgumentException("the item names no room", nameof(item));

        var (pid, startTime, readFailure) = await ReadEngineIdentityAsync(roomDirectory, cancellationToken).ConfigureAwait(false);
        if (readFailure is not null)
        {
            return new QueueLaneAdoption(item.Tag, roomDirectory, EngineLivenessStatus.Unknown, pid, readFailure);
        }

        var probe = EngineLivenessProbe.Probe(pid, startTime);
        if (probe.Status != EngineLivenessStatus.Alive)
        {
            return new QueueLaneAdoption(item.Tag, roomDirectory, probe.Status, pid, probe.Why);
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid!.Value);
        }
        catch (ArgumentException)
        {
            // Exited between the probe and the attach: the same answer the probe would have given a
            // moment later.
            return new QueueLaneAdoption(item.Tag, roomDirectory, EngineLivenessStatus.Dead, pid, "exited while being adopted");
        }

        if (!StartTimeMatches(process, startTime!.Value))
        {
            process.Dispose();
            return new QueueLaneAdoption(
                item.Tag, roomDirectory, EngineLivenessStatus.Dead, pid,
                "exited while being adopted; its pid now belongs to another process");
        }

        _ = SuperviseAsync(process, item.Tag, roomDirectory);
        return new QueueLaneAdoption(item.Tag, roomDirectory, EngineLivenessStatus.Alive, pid, null);
    }

    /// <summary>
    /// Whether the handle actually held is the process the journal recorded — its start time within
    /// the same one-second tolerance <see cref="EngineLivenessProbe"/> applies. False for a handle
    /// whose start time cannot be read at all: that is a process already gone, or one this daemon may
    /// not inspect, and neither is something to supervise as if it were the lane.
    /// </summary>
    private static bool StartTimeMatches(Process process, DateTimeOffset recordedStart)
    {
        try
        {
            var actual = new DateTimeOffset(process.StartTime).ToUniversalTime();
            return Math.Abs((actual - recordedStart.ToUniversalTime()).TotalMilliseconds) <= 1000;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The engine identity the room's journal recorded — the (pid, start time) pair on its newest
    /// <see cref="FlowEvent.ExecutionRequestAccepted"/> that carries one — or a reason none could be
    /// read. Newest rather than first: every execution a queue lane accepts is stamped by the same
    /// process, and the newest is the one whose stamp is least likely to predate a resume.
    /// </summary>
    private static async Task<(int? Pid, DateTimeOffset? StartTime, string? ReadFailure)> ReadEngineIdentityAsync(
        string roomDirectory, CancellationToken cancellationToken)
    {
        if (!RoomLedgerProbe.HasLedger(roomDirectory))
        {
            return (null, null, "the room carries no ledger yet");
        }

        try
        {
            var events = await new FlowEventLogReader(Path.Combine(roomDirectory, BatonPaths.FlowLogFileName))
                .ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var accepted = events.OfType<FlowEvent.ExecutionRequestAccepted>()
                .LastOrDefault(e => e.EnginePid is not null);
            return accepted is null
                ? (null, null, "no accepted execution recorded an engine pid")
                : (accepted.EnginePid, accepted.EngineStartTime, null);
        }
        catch (Exception ex) when (ex is BatonFlowException or IOException or UnauthorizedAccessException)
        {
            return (null, null, $"the room's ledger could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// The detached continuation behind a launched or adopted lane: wait for the process, then settle
    /// whatever it left. Never throws out — it runs on nobody's await, so a throw here would surface,
    /// if at all, as a process-level unobserved exception far from the lane it concerned.
    /// </summary>
    /// <remarks>
    /// Two waits, deliberately of different kinds. The first (<see cref="WaitForOsExitAsync"/>) is
    /// unbounded in TIME — a lane runs for as long as its own <c>--timeout</c> allows, enforced inside
    /// the lane, and nothing daemon-side should end it sooner — but it waits on the OS exit signal
    /// only, never on the redirected streams. <see cref="Process.WaitForExitAsync(CancellationToken)"/>
    /// would have waited for both: for a process with async readers attached it does not return until
    /// the streams reach EOF, which a straggler holding a duplicated pipe end can postpone forever,
    /// and that made <see cref="StreamDrainBound"/> unreachable on this path (#2117 review, finding 4).
    /// The second wait, inside <see cref="ExitCodeAsync"/>, is the stream drain, and it is the one
    /// that is bounded.
    /// </remarks>
    private static async Task SuperviseAsync(Process process, string tag, string roomDirectory)
    {
        try
        {
            using (process)
            {
                await WaitForOsExitAsync(process).ConfigureAwait(false);
                var exitCode = await ExitCodeAsync(process).ConfigureAwait(false);
                await SettleExitedLaneAsync(exitCode, tag, roomDirectory).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine(
                $"QueueLauncher: supervising lane '{tag}' in '{roomDirectory}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Completes when the OS reports <paramref name="process"/> exited — the <see cref="Process.Exited"/>
    /// signal off the process handle — and never waits for a redirected stream. Works for an adopted
    /// process as well as a launched one: both are handles, and the exit signal is a property of the
    /// handle, not of who spawned it. <see cref="SuperviseAsync"/>'s remarks say why this is not
    /// <see cref="Process.WaitForExitAsync(CancellationToken)"/>.
    /// </summary>
    private static Task WaitForOsExitAsync(Process process)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exited.TrySetResult();

        // Enabling the event on a process that already exited still raises it (the registered wait
        // fires on an already-signalled handle), but the check costs nothing and does not depend on
        // that: whichever of the two runs second is a no-op.
        if (process.HasExited)
        {
            exited.TrySetResult();
        }

        return exited.Task;
    }

    /// <summary>
    /// The exit code once <see cref="Process.HasExited"/> is true, after the redirected streams (if
    /// any) have drained. Bounded, because a straggler holding a duplicated pipe end could otherwise
    /// keep the drain open indefinitely — the same failure #2030 fixed for the wrapper shell. Null
    /// when the code cannot be read, which <see cref="SettleExitedLaneAsync"/> reports rather than
    /// hides.
    /// </summary>
    private static async Task<int?> ExitCodeAsync(Process process)
    {
        try
        {
            using var drainBound = new CancellationTokenSource(StreamDrainBound);
            await process.WaitForExitAsync(drainBound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The process is gone; only its output relay is still waiting on a handle somebody else
            // inherited. The exit code is readable regardless.
        }

        try
        {
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// What a lane's exit leaves for the queue: nothing, when the room already carries the sentinel
    /// the child's own settle wrote (the ordinary ending, and every refusal after provisioning); or
    /// the room's own post-launch failure record (<see cref="RecordPostLaunchFaultAsync"/>) when it
    /// does not — killed, crashed, or thrown out of mid-lane.
    /// </summary>
    /// <remarks>
    /// Split out as a test seam in the same sense the pump-shaped predecessor was (#1939 review round
    /// 2): the process is built inside <see cref="LaunchAsync"/> and is not otherwise drivable.
    /// </remarks>
    internal static async Task SettleExitedLaneAsync(int? exitCode, string tag, string roomDirectory)
    {
        if (await TerminalSentinelWriter.TryReadAsync(roomDirectory, CancellationToken.None).ConfigureAwait(false) is not null)
        {
            return;
        }

        var code = exitCode is { } value ? value.ToString(CultureInfo.InvariantCulture) : "an unreadable code";
        var reason = $"the lane process exited with {code} without settling the room";
        Console.Error.WriteLine($"QueueLauncher: lane '{tag}' in '{roomDirectory}' did not complete after launch: {reason}");
        await RecordPostLaunchFaultAsync(tag, roomDirectory, reason).ConfigureAwait(false);
    }

    /// <summary>
    /// The verdict for a child that exited before it ever created the room. A runway hold is read off
    /// the admission ledger — the row the child wrote against this very room before refusing — and
    /// everything else is the child's own last words on stderr. Pure, so the two answers are pinned
    /// without a spawn.
    /// </summary>
    /// <remarks>
    /// Only <see cref="RunwayAdmissionDecisions.Held"/> counts. A <c>held-overridden</c> row is a
    /// dispatch that proceeded, and a row against another room is another dispatch's fact — every
    /// launch attempt gets a fresh room name, so the room is the attempt's key. The ledger fails open
    /// on the writing side (a hold whose row could not be written still refuses), and that one
    /// degrades here to <see cref="QueueLaunchOutcome.Error"/> with the child's own message naming
    /// the hold: the item fails rather than waits, which the message makes recoverable by hand.
    /// </remarks>
    internal static QueueLaunchOutcome ClassifyPreProvisionExit(
        int? exitCode, string roomDirectory, IReadOnlyList<RunwayAdmissionEntry> admissionRows, IReadOnlyList<string> stderrTail)
    {
        ArgumentNullException.ThrowIfNull(admissionRows);
        ArgumentNullException.ThrowIfNull(stderrTail);

        var key = BatonPaths.RecordKey(roomDirectory);
        var held = admissionRows.Any(row =>
            string.Equals(row.Decision, RunwayAdmissionDecisions.Held, StringComparison.Ordinal)
            && row.Room is { Length: > 0 } room
            && BatonPaths.RecordKeyComparer.Equals(BatonPaths.RecordKey(room), key));
        if (held)
        {
            return new QueueLaunchOutcome(null, RunwayHeld: true);
        }

        var code = exitCode is { } value ? value.ToString(CultureInfo.InvariantCulture) : "an unreadable code";
        var detail = stderrTail.Count > 0 ? string.Join(" | ", stderrTail) : "it wrote nothing to stderr";
        return new QueueLaunchOutcome(null, Error: $"baton dispatch exited {code} before provisioning the room: {detail}");
    }

    private static async Task<IReadOnlyList<RunwayAdmissionEntry>> ReadAdmissionRowsAsync()
    {
        try
        {
            return await RunwayAdmissionLedgerStore.ReadAllAsync(BatonPaths.RunwayAdmissionLedgerFile, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Read-side fail-open, mirroring the ledger's own write-side posture: an unreadable ledger
            // must not be the reason a refused launch is misfiled, so the child's own words decide.
            Console.Error.WriteLine(
                $"QueueLauncher: could not read '{BatonPaths.RunwayAdmissionLedgerFile}' to tell a hold from a refusal: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// The executable and leading arguments that re-enter this same binary: the apphost
    /// (<c>baton.exe</c>, what the installed tool runs as) directly, or <c>dotnet &lt;Baton.Cli.dll&gt;</c>
    /// when the daemon was itself started through the muxer. Read off the process, never configured:
    /// the lane must run the code the daemon is running.
    /// </summary>
    internal static (string FileName, IReadOnlyList<string> LeadingArguments) ResolveLaneCommand()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("this process has no readable executable path, so it cannot start a lane process");
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        var viaMuxer = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            && entryAssembly is { Length: > 0 }
            && entryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        return viaMuxer ? (processPath, [entryAssembly!]) : (processPath, []);
    }

    /// <summary>
    /// Shapes the lane process's start info: the argv, and both output streams redirected with their
    /// decode pinned to UTF-8. Internal so <c>QueueLauncherTests</c> can assert the shape without a
    /// spawn — the source-scan tripwire (<c>RedirectedProcessEncodingTests</c>) fails on the same
    /// revert, but only by counting text; this is the same claim read off the object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The streams are relayed into <c>daemon.log</c> with the tag in front, so a queue lane's own
    /// lines land where the operator already reads. The redirect is safe to outlive: <see cref="DetachedProcess"/>
    /// makes the daemon's own handles non-inheritable first, and a child whose reader has gone
    /// writes into a broken pipe the console layer drops rather than throws on. Both streams, never
    /// one: <see cref="DetachedProcess.Start(ProcessStartInfo)"/> refuses the mixed shape.
    /// </para>
    /// <para>
    /// <b>The decode is pinned on the start info because that is where the reader takes it from</b>:
    /// the async readers <see cref="LaneOutputRelay"/> starts decode with
    /// <see cref="ProcessStartInfo.StandardOutputEncoding"/>/<see cref="ProcessStartInfo.StandardErrorEncoding"/>,
    /// and with those null .NET decodes the pipe with whatever console code page the daemon has —
    /// OEM under the Task Scheduler wrapper — which is the nondeterminism #466 removed from every
    /// other redirecting site in <c>src/</c>. What this pins is the daemon's half. The lane's own
    /// write encoding is the child's, a lane verb's console setting rather than anything this start
    /// info can reach, and the same bytes the #2030 wrapper shell reads; it is not changed here.
    /// </para>
    /// </remarks>
    internal static void ConfigureLaneStartInfo(
        ProcessStartInfo startInfo, IReadOnlyList<string> leadingArguments, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(leadingArguments);
        ArgumentNullException.ThrowIfNull(arguments);

        foreach (var argument in leadingArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
    }

    /// <summary>
    /// <paramref name="options"/> as the argv <c>baton dispatch</c> parses — the inverse of
    /// <see cref="DispatchOptionsParser.Parse"/> for exactly the fields <see cref="BuildOptions"/>
    /// sets. A field added there must be added here; <c>QueueLauncherTests</c> pins the round trip so
    /// the omission fails a test rather than silently dropping a flag from every queue lane.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(DispatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new List<string> { "dispatch", options.Name, "--room-dir", options.RoomDirectoryPath };
        if (options.SpecFilePath is { Length: > 0 } spec)
        {
            arguments.Add("--spec");
            arguments.Add(spec);
        }

        Add("--adapter", options.Adapter);
        Add("--workspace", options.WorkspaceDirectory);
        Add("--model", options.Model);
        Add("--effort", options.Effort);
        Add("--timeout", options.Timeout is { } timeout
            ? ((int)Math.Ceiling(timeout.TotalMinutes)).ToString(CultureInfo.InvariantCulture)
            : null);
        Add("--label", options.Label);
        Add("--token-budget", options.TokenBudget?.ToString(CultureInfo.InvariantCulture));
        Add("--max-tool-steps", options.MaxToolSteps?.ToString(CultureInfo.InvariantCulture));
        Add("--override-runway", options.OverrideRunwayReason);
        return arguments;

        void Add(string flag, string? value)
        {
            if (value is { Length: > 0 })
            {
                arguments.Add(flag);
                arguments.Add(value);
            }
        }
    }

    /// <summary>
    /// The verdict a lane that faulted — or was killed — <em>after</em> launch would otherwise never
    /// leave behind (#1939 review). A dispatch that dies twenty minutes in reaches no Terminal state, so
    /// <see cref="TerminalSettleRecorder"/> never runs and the room carries no <c>terminal.json</c> —
    /// and <c>QueueSchedulerService.ResolveFinishedItemsAsync</c>, which reads exactly that file,
    /// leaves the item <see cref="QueueItemState.Launched"/> forever. This writes the room's own
    /// failure so the item resolves the same way every other settled room does: through the sentinel,
    /// not through a second channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things it deliberately does NOT do — both rulings, stated in spec/baton.md §13 with the
    /// argument for each: it never creates the room directory (the scheduler's own sweep is what
    /// resolves an item whose room was never provisioned), and it never replaces a sentinel the room
    /// already carries.
    /// </para>
    /// <para>
    /// <b>What it writes is the room's own record, not a fabricated blank</b> (#1939 review round 2) —
    /// spec/baton.md §13's post-launch bullet has the argument, and the three consequences it states
    /// (the outcome word is always <c>Failed</c>, the fault is the terminal reason with the room's own
    /// recorded failure folded in beside it, and liveness is dropped) are what the code below and
    /// <see cref="TryProjectRoomAsync"/> implement. The one mechanical thing worth pointing at from
    /// here: the write below is unconditional, taken even when the projection comes back null — the
    /// register says why that room in particular cannot be skipped.
    /// </para>
    /// <para>
    /// <b>A HELD file is retried; a corrupt or missing one is not</b> (#1951) — spec/baton.md §13's
    /// post-launch bullet has the argument, <see cref="HeldLedgerRetryDelay"/> the bound. Mechanically:
    /// <see cref="TryProjectRoomAsync"/> reports WHY it could not project, and this loop re-reads only
    /// while that answer is <see cref="RoomProjectionFailure.Held"/>.
    /// </para>
    /// </remarks>
    /// <param name="tag">The queued item's tag.</param>
    /// <param name="roomDirectory">The room the lane was dispatched into.</param>
    /// <param name="reason">Why the lane did not complete.</param>
    /// <param name="heldAttempts">
    /// How many projection attempts a held ledger gets in total, including the first. A test seam in
    /// the same sense as <see cref="SettleExitedLaneAsync"/>: a holder released on a real machine's
    /// timing cannot be steered from outside, so the two arms that measure this path set their own
    /// bound rather than sleeping against the default.
    /// </param>
    /// <param name="heldRetryDelay">The pause between those attempts; defaults to <see cref="HeldLedgerRetryDelay"/>.</param>
    internal static async Task RecordPostLaunchFaultAsync(
        string tag,
        string roomDirectory,
        string reason,
        int heldAttempts = HeldLedgerAttempts,
        TimeSpan? heldRetryDelay = null)
    {
        try
        {
            if (!Directory.Exists(roomDirectory)
                || await TerminalSentinelWriter.TryReadAsync(roomDirectory, CancellationToken.None)
                    .ConfigureAwait(false) is not null)
            {
                return;
            }

            var delay = heldRetryDelay ?? HeldLedgerRetryDelay;
            var attempt = await TryProjectRoomAsync(roomDirectory).ConfigureAwait(false);
            for (var remaining = heldAttempts - 1; attempt.Failure == RoomProjectionFailure.Held && remaining > 0; remaining--)
            {
                await Task.Delay(delay).ConfigureAwait(false);

                // The backoff put seconds between the guard above and the write below, so a verdict
                // landing mid-wait would be overwritten by a Failed one — spec/baton.md §13's
                // never-replace rule at a window this retry opened. This read is the EARLY-OUT half of
                // closing it: it abandons the remaining attempts as soon as the room settles. The half
                // that closes it is the read immediately before the write, which covers the iteration
                // that exits the loop as well.
                if (await TerminalSentinelWriter.TryReadAsync(roomDirectory, CancellationToken.None)
                        .ConfigureAwait(false) is not null)
                {
                    return;
                }

                attempt = await TryProjectRoomAsync(roomDirectory).ConfigureAwait(false);
            }

            var projected = attempt.View;
            var error = $"the queue-launched lane '{tag}' did not complete after launch: {reason}";
            if (projected?.Error is { Length: > 0 } recordedFailure)
            {
                error += $" — the room's own last recorded failure: {recordedFailure}";
            }
            else if (projected is null && attempt.Reason is { Length: > 0 } degraded)
            {
                var stillHeld = attempt.Failure == RoomProjectionFailure.Held
                    ? $" (still held after {heldAttempts} attempt(s))"
                    : string.Empty;
                error += $" — this record carries no steps or outputs because {degraded}{stillHeld}";
            }

            var view = (projected ?? new WorkflowStatusView(WorkflowOutcome.Failed, [], [], null)) with
            {
                State = WorkflowOutcome.Failed,
                Error = error,
            };

            // The last read before the replace, because TerminalSentinelWriter.WriteAsync replaces
            // unconditionally: without it the one iteration with no guard after it is the iteration in
            // which the holder released — the very moment a settle is most likely to land.
            if (await TerminalSentinelWriter.TryReadAsync(roomDirectory, CancellationToken.None)
                    .ConfigureAwait(false) is not null)
            {
                return;
            }

            await TerminalSentinelWriter.WriteAsync(roomDirectory, view, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"QueueLauncher: could not record the post-launch fault for '{tag}' in '{roomDirectory}': {ex.Message}");
        }
    }

    /// <summary>
    /// Why <see cref="TryProjectRoomAsync"/> came back without a view. Only
    /// <see cref="Held"/> is worth waiting on — the other two are as true a second later as they are now.
    /// </summary>
    private enum RoomProjectionFailure
    {
        /// <summary>It projected.</summary>
        None,

        /// <summary>
        /// A file this projection needs is not there — nothing to project, and nothing arriving. Which
        /// one is missing is in the reason, never folded into a disjunction: a room with no ledger never
        /// started, where a room with a ledger and no snapshot ran and lost its binding.
        /// </summary>
        Absent,

        /// <summary>
        /// Another process holds <c>flow.jsonl</c> or <c>snapshot.json</c> with a conflicting share; a
        /// release makes this projectable.
        /// </summary>
        Held,

        /// <summary>The ledger or the snapshot is there and could not be read or parsed.</summary>
        Unreadable,
    }

    /// <summary>One projection attempt: the view, or why there is none in the words the sentinel carries.</summary>
    private readonly record struct RoomProjection(WorkflowStatusView? View, RoomProjectionFailure Failure, string? Reason);

    /// <summary>
    /// Total projection attempts a held file gets, including the first. Sized with — and only with —
    /// <see cref="HeldLedgerRetryDelay"/>, whose remark carries the whole derivation.
    /// </summary>
    private const int HeldLedgerAttempts = 5;

    /// <summary>
    /// The pause between those attempts; four of them bound the wait at ~1.2s. This is the one home for
    /// that bound's derivation — spec/baton.md §13's post-launch bullet points here rather than
    /// restating it.
    /// <para>
    /// <b>Sized against the population that can actually reach this arm</b>, which spec/baton.md §13
    /// names and <see cref="FlowJournalHeldException"/>'s doc argues: not a sibling <c>baton</c> command
    /// and not this room's own engine — both of those admit a reader — but a holder outside Baton that
    /// shares nothing. Nothing here measures how long one of those keeps a file, and no bound would be
    /// safe if it did, so this one is <b>defensive rather than fitted</b>: long enough to ride out a
    /// hand-off that is already ending, short enough that a room nobody releases still resolves
    /// promptly. Degrading is correct for the long holder — the sentinel names the hold and the item
    /// resolves either way. Change this number if a real hold is ever measured; do not change it to
    /// chase one that has not been.
    /// </para>
    /// </summary>
    private static readonly TimeSpan HeldLedgerRetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How long an exited lane's redirected streams get to drain before its exit code is read anyway.
    /// The child is already gone by then; only a straggler holding a duplicated pipe end can extend
    /// this, and <c>baton dispatch</c> clears its own inheritable handles before spawning anything.
    /// </summary>
    private static readonly TimeSpan StreamDrainBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The room's <em>actual</em> state, projected the way <see cref="TerminalSettleRecorder"/> and
    /// <c>StatusCommand</c> already project one: bound snapshot + <c>flow.jsonl</c> +
    /// <c>ProjectionCheckpointStore</c>, through the same <c>StateProjector</c>/
    /// <see cref="WorkflowStatusProjector"/> pair, so the steps and outputs a post-launch fault freezes
    /// are the room's own and never a second derivation of what an event log means. Inlined here rather
    /// than shared with <c>FleetStatusTool.ProcessRoomAsync</c>'s identical block, which is a seam worth
    /// extracting on its own rather than inside this fix.
    /// <para>
    /// Returns a null view — never throws — for a room this cannot project: no real ledger yet
    /// (<see cref="RoomLedgerProbe"/>, which is also why the ledger-less room in
    /// <c>QueueLauncherTests</c> still gets the bare view), no bound snapshot, or a read/parse failure.
    /// The caller writes the bare <c>Failed</c> sentinel in that case: a degraded record still resolves
    /// the item, where a throw out of the discarded continuation this runs in would resolve nothing.
    /// </para>
    /// <para>
    /// <b>A sharing violation is <see cref="RoomProjectionFailure.Held"/>, not
    /// <see cref="RoomProjectionFailure.Unreadable"/></b> (#1951), on <em>either</em> file this reads.
    /// <see cref="FlowJournalHeldException"/> derives from <see cref="BatonFlowException"/>, so the
    /// single catch below used to fold a held file into the same answer as a truncated one — and the
    /// caller, seeing one answer, could neither wait out the first nor say which had happened. The
    /// narrow arms come first for that reason; the ORDER is the fix.
    /// </para>
    /// <para>
    /// <b><see cref="WorkflowStatusStepView.Liveness"/> is dropped from every step</b>, while each
    /// step's recorded <c>state</c> is kept as projected, a mid-lane <c>Running</c> included —
    /// spec/baton.md §13's post-launch bullet has why the two are treated differently.
    /// </para>
    /// </summary>
    private static async Task<RoomProjection> TryProjectRoomAsync(string roomDirectory)
    {
        var snapshotPath = Path.Combine(roomDirectory, BatonPaths.SnapshotFileName);
        // Two ifs rather than one disjunction: both facts are known here, and an operator reading the
        // degraded sentinel cannot otherwise tell a room that never started from one whose binding went
        // missing. A zero-length flow.jsonl is the first of the two, not a third state — see
        // RoomLedgerProbe for why the writer leaves one behind on a refusal.
        if (!RoomLedgerProbe.HasLedger(roomDirectory))
        {
            return new RoomProjection(
                null, RoomProjectionFailure.Absent, "the room carries no ledger of its own");
        }

        if (!File.Exists(snapshotPath))
        {
            return new RoomProjection(
                null,
                RoomProjectionFailure.Absent,
                "the room has a ledger but no bound snapshot to project it against");
        }

        try
        {
            var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, CancellationToken.None).ConfigureAwait(false);
            var entries = await new FlowEventLogReader(Path.Combine(roomDirectory, BatonPaths.FlowLogFileName))
                .ReadAllEntriesWithTimestampsAsync(CancellationToken.None).ConfigureAwait(false);

            var events = new List<FlowEvent>(entries.Count);
            foreach (var entry in entries)
            {
                if (entry is LogEntry.FlowLogEntry flowLogEntry)
                {
                    events.Add(flowLogEntry.Event);
                }
            }

            var state = StateProjector.Project(events, snapshot, ProjectionCheckpointStore.Load(roomDirectory));
            var view = WorkflowStatusProjector.Project(
                state, snapshot, roomDirectory, entries, WorkerAdapterRegistry.Default);

            return new RoomProjection(
                view with { Steps = [.. view.Steps.Select(step => step with { Liveness = null })] },
                RoomProjectionFailure.None,
                null);
        }
        catch (FlowJournalHeldException ex)
        {
            // Must precede the BatonFlowException arm below, which it would otherwise be swallowed by
            // — see this method's own remarks for why the two answers cannot share one.
            Console.Error.WriteLine(
                $"QueueLauncher: '{roomDirectory}' could not be projected for its post-launch fault "
                + $"record because its ledger is held: {ex.Message}");
            return new RoomProjection(
                null, RoomProjectionFailure.Held, $"the room's ledger is held open by another process: {ex.Message}");
        }
        catch (IOException ex) when (FileHolderProbe.IsSharingViolation(ex))
        {
            // The same condition on the OTHER file this reads: SnapshotBinder translates only a missing
            // file, so a sharing violation on snapshot.json arrives raw and would otherwise be reported
            // as unreadable and degraded on sight. One holder class, one answer, one remedy.
            Console.Error.WriteLine(
                $"QueueLauncher: '{roomDirectory}' could not be projected for its post-launch fault "
                + $"record because a file it reads is held: {ex.Message}");
            return new RoomProjection(
                null,
                RoomProjectionFailure.Held,
                $"the room's snapshot is held open by another process: {ex.Message}");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A ledger that vanishes between the HasLedger probe above and FlowEventLogReader's own
            // File.Exists — or between that check and its open. Missing is not corrupt, and saying
            // "could not be read" of a file that is simply gone sends an operator looking for damage.
            // A race with no test: the window is microseconds wide and not steerable from outside.
            return new RoomProjection(
                null, RoomProjectionFailure.Absent, $"the room's ledger is no longer there: {ex.Message}");
        }
        catch (Exception ex) when (ex is BatonFlowException or IOException or UnauthorizedAccessException)
        {
            // SnapshotLoadException and FlowEventLogReadException are both BatonFlowException. Named
            // rather than swallowed: the sentinel this degrades to says the lane failed but not what it
            // had done, and the difference is otherwise invisible.
            Console.Error.WriteLine(
                $"QueueLauncher: could not project '{roomDirectory}' for its post-launch fault record, "
                + $"so its sentinel carries no steps or outputs: {ex.Message}");
            return new RoomProjection(
                null,
                RoomProjectionFailure.Unreadable,
                $"the room's ledger or snapshot could not be read: {ex.Message}");
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
    /// mechanism.
    /// <para>
    /// <b>Coupled to <see cref="OnDemandRunwayHarvest.Bound"/> since #1923</b>, which is why that is
    /// named here rather than left for the next reader to find: the runway hold's inline harvest runs
    /// inside this same pre-provision phase, once per gated vendor with no snapshot, so a cold
    /// mixed-vendor dispatch can spend that bound twice before anything else in the phase starts. The
    /// headroom this window leaves the rest of pre-provision is therefore this value minus up to two
    /// bounds, not the whole of it. Changing either constant is a change to both.
    /// </para>
    /// </summary>
    public static readonly TimeSpan RefusalWindow = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RefusalPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The item plus its tier, as <c>baton dispatch</c>'s own options. Kept separate from
    /// <see cref="LaunchAsync"/> so a test can assert what the queue forwards without running a
    /// dispatch; <see cref="BuildArguments"/> is what turns them into the child's argv.
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

    /// <summary>
    /// Drains a lane process's redirected streams into the daemon's own console, each line prefixed
    /// with the lane's tag, and keeps the newest few stderr lines for the refusal message. Without a
    /// drain the child blocks the moment a pipe buffer fills.
    /// </summary>
    private sealed class LaneOutputRelay
    {
        private const int TailLength = 8;
        private readonly Queue<string> _stderrTail = new();
        private readonly object _gate = new();

        public LaneOutputRelay(string tag, Process process)
        {
            var prefix = $"[lane {tag}] ";
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    Console.Out.WriteLine(prefix + e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (_gate)
                {
                    _stderrTail.Enqueue(e.Data);
                    while (_stderrTail.Count > TailLength)
                    {
                        _stderrTail.Dequeue();
                    }
                }

                Console.Error.WriteLine(prefix + e.Data);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public IReadOnlyList<string> StderrTail
        {
            get
            {
                lock (_gate)
                {
                    return [.. _stderrTail];
                }
            }
        }
    }
}
