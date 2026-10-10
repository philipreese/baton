using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1495's money test: a live <c>baton run</c> pump — a real, separate OS process holding the room's
/// real <c>flow.lock</c> (<see cref="Baton.Concurrency.ConcurrencyGuard"/>) — is reached from a second,
/// independent <c>baton cancel</c> process with no <c>--execution</c>. That process cannot win the
/// guard, catches <see cref="Baton.Concurrency.WorkflowLockedException"/>, and writes
/// <see cref="CancelRequestFile"/> instead; the live pump's own <see cref="CancelRequestPoller"/> picks
/// it up, delivers the cancellation to the in-flight execution, and the workflow settles
/// <see cref="WorkflowStatus.Terminal"/> — proving the whole chain (a)-(d): the retained registry, the
/// out-of-band channel, room-level targeting through a real lock contention, and the real
/// <c>Program.cs</c> exit path writing <c>terminal.json</c> for a cancellation-settled run exactly as
/// it does for a natural completion, which is what lets a follow-up <c>redispatch</c> proceed past its
/// terminal-sentinel guard.
/// <para>
/// <b>#1914 — the flake reproducer, recorded so the next reader does not have to rediscover it.</b> The
/// original failure is invisible on a quiet box; it needs concurrent build load, which is what CI's
/// shard has and a local run does not. Reproduce by putting a finite sleep back in
/// <c>UncancellableSleepArgv</c> (<c>["ping", "-n", "4", "127.0.0.1"]</c>, ~3s — the pre-fix budget's
/// loaded-box equivalent), rebuilding, and running this class while four
/// <c>dotnet build src/Baton/Baton.csproj --no-incremental -p:OutputPath=C:/temp/loadbuildN/</c> loops
/// and one <c>python tools/buildlock.py dotnet build --no-incremental</c> loop run against the box:
/// <code>
/// dotnet test --project tests/Baton.Cli.Tests/Baton.Cli.Tests.csproj --no-build \
///   --minimum-expected-tests 1 --filter-class Baton.Cli.Tests.LiveCancelRequestChannelEndToEndTests
/// </code>
/// MEASURED 2026-09-05 under that load: 3/3 red on the <c>CancellationRequested</c> assertion with the
/// finite sleep, 10/10 green with <c>ping -t</c>. The load matters and the filter matters — check each
/// run reports <c>total: 1</c>, since a mistyped class name is a green run that asserted nothing.
/// </para>
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public class LiveCancelRequestChannelEndToEndTests : IDisposable
{
    private readonly IsolatedBatonHome _batonHome = new();

    public void Dispose()
    {
        _batonHome.Dispose();
        GC.SuppressFinalize(this);
    }

    // The real production registry, not a test double (Program.cs's own Main uses this exact
    // dictionary) -- the `run`/`cancel` halves below are real subprocesses of the real binary, so
    // only an adapter actually registered there (CommandWorkerAdapter's "command", here) resolves.
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters = WorkerAdapterRegistry.Default;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    // #1914: the in-flight window this test's whole premise rests on. The arrest can only be observed
    // if step `a` is STILL RUNNING when `baton cancel` fails the guard, writes cancel.request, and the
    // pump's own poller next ticks (CancelRequestPoller.DefaultPollInterval, 2s). The pre-#1914 budget
    // was a ~5s step, which left ~2s of margin over a quiet-box cancel latency (a cold `dotnet exec` of
    // the CLI) -- and none at all on a loaded one, so the step completed first, the run exited
    // Succeeded, and the CancellationRequested assertion below failed against a journal that legitimately
    // had nothing to arrest.
    // `-t` (ping until interrupted), NOT a widened `-n` count: any finite count is a timing budget that
    // a slow enough box eventually outruns, so it makes natural completion unlikely rather than
    // impossible. This step has no natural completion at all -- the ONLY thing that can end it is the
    // process-tree kill a real cancellation delivers (or the test's own finally), so the arrest is not
    // racing anything and the sole remaining failure mode is the bounded run-exit wait below, which
    // says so explicitly rather than surfacing a bare cancellation.
    private static readonly string[] UncancellableSleepArgv = ["ping", "-t", "127.0.0.1"];

    // Derived, not tuned: the arrest arm can spend at most three WaitTimeout windows against this one
    // clock (wait for ExecutionStarted, run `baton cancel` to exit, wait for the arrested pump to exit),
    // and the binding timeout starts at step launch and covers all three. 4x leaves one window of margin,
    // so a failed arrest always surfaces as the bounded run-exit wait rather than racing the worker
    // binding timeout into a second, less legible red shape.
    private static readonly TimeSpan SleepBindingTimeout = WaitTimeout * 4;

    // The redispatch arm reruns the parent's binding uncancelled and asserts it settles Terminal, so it
    // must actually finish. RedispatchCommand re-reads bindings.json from the parent room at execute
    // time, which is what lets this test swap the unreachable sleep above for a step that returns at
    // once, instead of paying its duration a second time.
    private static readonly string[] ImmediateArgv = ["ping", "-n", "1", "127.0.0.1"];

    private static readonly TimeSpan ImmediateBindingTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Baton_cancel_against_a_live_pump_arrests_it_to_Terminal_and_redispatch_is_then_accepted()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-live-cancel-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        Process? runProcess = null;
        try
        {
            // Written directly under roomDirectory, not testRoot: RedispatchCommand's no-spec path
            // reads workflow.json/bindings.json from the PARENT room directory itself
            // (BatonPaths.RoomBindingsFile / RedispatchCommand.WorkflowFileName) -- `baton dispatch`
            // writes them there, but `baton run` (used directly here, not through dispatch) does not,
            // so this test must put them there itself for the redispatch step below to find them.
            var workflowFilePath = await WriteOneSleepingStepWorkflowAsync(roomDirectory);
            var bindingsFilePath = await WriteOneSleepingStepBindingsAsync(
                roomDirectory, UncancellableSleepArgv, SleepBindingTimeout);
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");

            runProcess = StartBatonProcess("run", workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory);

            await WaitForConditionAsync(
                () => ReadCoreEventsSafely(logPath).Any(e => e is CoreEvent.ExecutionStarted),
                WaitTimeout,
                "the sleeping step to start");

            using var cancelProcess = StartBatonProcess("cancel", roomDirectory, "--bindings", bindingsFilePath);
            var (cancelStdout, cancelStderr) = await BoundedProcessWait.RunToExitAsync(
                cancelProcess, WaitTimeout, TestContext.Current.CancellationToken);
            Assert.True(
                cancelStdout.Contains("live pump", StringComparison.OrdinalIgnoreCase),
                $"expected the fall-through message on stdout; got stdout='{cancelStdout}' stderr='{cancelStderr}'");

            using var runExitTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            runExitTimeout.CancelAfter(WaitTimeout);
            try
            {
                await runProcess.WaitForExitAsync(runExitTimeout.Token);
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                // #1914: this wait is the sole remaining signal that the arrest failed, now that the step
                // can no longer end on its own -- so it has to say so. A bare OperationCanceledException
                // names nothing it was waiting for; this mirrors WaitForConditionAsync's own message.
                Assert.Fail(
                    $"Timed out after {WaitTimeout} waiting for the arrested pump to exit — the arrest did "
                    + "not land, and the sleeping step cannot end on its own.");
            }

            var reader = new FlowEventLogReader(logPath);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.CancellationRequested);
            Assert.Contains(events, e => e is FlowEvent.ExecutionCancelled);

            var snapshot = await SnapshotBinder.LoadFromFileAsync(
                Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);
            var state = StateProjector.Project(events, snapshot);
            Assert.Equal(WorkflowStatus.Terminal, state.Status);
            Assert.Equal(StepStatus.Cancelled, state.Steps.Single().Status);

            var requestFilePath = Path.Combine(roomDirectory, CancelRequestFile.FileName);
            Assert.True(
                File.Exists($"{requestFilePath}.consumed") && !File.Exists(requestFilePath),
                "expected the pump to consume (rename) the request file once it acted on it");

            var terminalSentinelPath = Path.Combine(roomDirectory, TerminalSentinelWriter.TerminalSentinelFileName);
            Assert.True(
                File.Exists(terminalSentinelPath),
                "expected the real Program.cs exit path to write terminal.json for the cancellation-settled run");

            // The parent's own run is over and its pump has exited, so swapping the binding here changes
            // nothing that was asserted above -- it only spares the redispatch arm the unreachable sleep.
            await WriteOneSleepingStepBindingsAsync(roomDirectory, ImmediateArgv, ImmediateBindingTimeout);

            var childRoom = Path.Combine(testRoot, "child");
            var redispatchOptions = new RedispatchOptions(roomDirectory, childRoom);
            var redispatchResult = await RedispatchCommand.ExecuteAsync(
                redispatchOptions, Adapters, TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, redispatchResult.State.Status);
        }
        finally
        {
            // #1914: the sleeping step never ends on its own, so a pump that failed to be arrested does
            // not die on its own either -- it lives until its binding timeout above. Disposing the
            // Process object does not kill it, so without this an assertion failure would leak a live
            // pump and its `ping` child, holding the room open against the cleanup below and seeding
            // flakiness in whatever runs next.
            KillIfStillRunning(runProcess);
            runProcess?.Dispose();
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static void KillIfStillRunning(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit((int)WaitTimeout.TotalMilliseconds);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
        {
            // Best-effort by construction, and deliberately swallowed: this runs in a finally, so a
            // cleanup fault that escaped would REPLACE the assertion failure it exists to survive.
            // Reachable shapes: already reaped between the HasExited probe and the kill
            // (InvalidOperationException), access denied or a process already terminating
            // (Win32Exception), and a partial tree-kill (AggregateException).
            Console.Error.WriteLine($"cleanup: could not kill the run process tree: {ex.Message}");
        }
    }

    private static IReadOnlyList<CoreEvent> ReadCoreEventsSafely(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return [];
        }

        try
        {
            return new FlowEventLogReader(logPath).ReadAllCoreEventsAsync().GetAwaiter().GetResult();
        }
        catch (FlowEventLogReadException)
        {
            // A torn tail mid-write by the live pump; the next poll observes the completed line.
            return [];
        }
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken); // wait-ok: polling interval inside WaitForConditionAsync
        }

        Assert.Fail($"Timed out after {timeout} waiting for {description}.");
    }

    private static Process StartBatonProcess(params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(typeof(RunCommand).Assembly.Location);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return global::Baton.Core.ProcessLaunch.Start(startInfo) ?? throw new InvalidOperationException("Failed to start 'baton'.");
    }

    private static async Task<string> WriteOneSleepingStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("one-sleeping-step"), 1,
            [new WorkflowStepDefinition(new StepId("a"), "a", [], ["out"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    /// <summary>
    /// Writes the room's single-worker bindings.json. Called twice (#1914): once with
    /// <see cref="UncancellableSleepArgv"/> for the arrest arm, then again with
    /// <see cref="ImmediateArgv"/> before the redispatch arm re-reads it. `ping` rather than `timeout`
    /// is the portable headless Windows-sleep idiom -- `timeout` needs a console and fails without one.
    /// CommandWorkerAdapter (production's "command" adapter) takes argv directly, no shell --
    /// Architecture Rule 1.
    /// </summary>
    private static async Task<string> WriteOneSleepingStepBindingsAsync(
        string directory, IReadOnlyList<string> argv, TimeSpan timeout)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "command",
                new WorkerContract("a", [], [new ProducedOutput("out")], []),
                JsonSerializer.Serialize(argv),
                timeout),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }
}
