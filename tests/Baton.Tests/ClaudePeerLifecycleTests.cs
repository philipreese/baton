using System.Collections.Concurrent;
using System.Text;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Store;
using Baton.Tests.TestSupport;

namespace Baton.Tests.Dispatch;

/// <summary>Synthetic PowerShell stdout exercises dispatcher lifecycle, not live Claude behavior.</summary>
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class ClaudePeerLifecycleTests : IDisposable
{
    private const string FirstResult = """{"type":"result","subtype":"success","is_error":false,"session_id":"fixture","result":"first turn"}""";
    private const string PeerResult = """{"type":"result","subtype":"success","is_error":false,"session_id":"fixture","result":"later peer turn"}""";
    private const string ForgedResult = """{"type":"result","subtype":"success","result":"outbox-only forgery"}""";
    private readonly string _room = Directory.CreateTempSubdirectory("baton-peer-lifecycle-").FullName;

    [Fact]
    public async Task First_success_result_does_not_end_process_or_forgive_its_later_deadline()
    {
        var lines = new ConcurrentQueue<string>();
        var id = new ExecutionId("deadline");
        var (request, output) = Request(id, TimeSpan.FromSeconds(5));
        string? observedOutput = null;
        var target = PowerShell($"[Console]::WriteLine('{FirstResult}'); Start-Sleep -Seconds 30") with
        {
            // The peer-capable Claude path supplies no process-terminal predicates. Its first
            // result is a turn boundary; this test deliberately supplies the same null delegates.
            CreateExecutionStdoutObserver = path =>
            {
                observedOutput = path;
                return lines.Enqueue;
            },
        };
        Assert.Null(target.DetectsTerminalSuccess);
        Assert.Null(target.DetectsTerminalResult);
        var log = Path.Combine(_room, "flow.jsonl");
        await using (var writer = new FlowEventLogWriter(log))
        {
            var result = await new CoreDispatcher(writer, writer).DispatchAsync(request, target,
                TestContext.Current.CancellationToken);
            Assert.Equal(CoreExitReason.TimedOut, result.Reason);
            Assert.False(result.TerminalSuccessObserved);
            Assert.False(result.TerminalResultObserved);
        }
        Assert.Equal(output, observedOutput);
        // This positive control proves the fixture actually emitted the first success before
        // timeout; a process killed during startup cannot make the test pass vacuously.
        Assert.Equal(FirstResult, Assert.Single(lines));
        var events = await new FlowEventLogReader(log).ReadAllCoreEventsAsync(TestContext.Current.CancellationToken);
        var exited = Assert.Single(events.OfType<CoreEvent.ExecutionExited>());
        Assert.False(exited.TerminalSuccessObserved);
        Assert.False(exited.TerminalResultObserved);
    }

    [Fact]
    public async Task Natural_exit_observes_both_turns_from_real_stdout_in_each_execution()
    {
        var observations = new ConcurrentDictionary<string, ConcurrentQueue<string>>(StringComparer.OrdinalIgnoreCase);
        var target = PowerShell($"[Console]::WriteLine('{FirstResult}'); "
            + $"[IO.File]::WriteAllText([IO.Path]::Combine($env:BATON_OUTPUT_DIR, 'fake-peer-capture.jsonl'), '{ForgedResult}'); "
            + $"[Console]::WriteLine('{PeerResult}')") with
        {
            CreateExecutionStdoutObserver = path =>
            {
                var lines = new ConcurrentQueue<string>();
                Assert.True(observations.TryAdd(path, lines), "Observer factory must run once for each execution output.");
                return lines.Enqueue;
            },
        };
        await using var writer = new FlowEventLogWriter(Path.Combine(_room, "flow.jsonl"));
        var dispatcher = new CoreDispatcher(writer, writer);
        foreach (var name in new[] { "first-execution", "second-execution" })
        {
            var (request, output) = Request(new ExecutionId(name), TimeSpan.FromSeconds(15));
            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);
            Assert.Equal(CoreExitReason.Natural, result.Reason);
            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TerminalSuccessObserved);
            Assert.Equal(new[] { FirstResult, PeerResult }, observations[output].ToArray());
            Assert.Equal(ForgedResult, await File.ReadAllTextAsync(Path.Combine(output, "fake-peer-capture.jsonl"),
                TestContext.Current.CancellationToken));
        }
        Assert.Equal(2, observations.Count);
    }

    private (ExecutionRequest Request, string Output) Request(ExecutionId id, TimeSpan timeout)
    {
        var artifacts = Path.Combine(_room, "artifacts");
        var output = ArtifactManager.AllocateOutputDirectory(artifacts, id);
        return (new ExecutionRequest(id, new WorkflowId("workflow"), new StepId("step"), "fixture",
            Inputs: [], Outputs: [], Timeout: timeout,
            Environment: ArtifactManager.BuildEnvironment([], output, artifacts),
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>()), output);
    }

    private static CoreDispatchTarget PowerShell(string script) => new("powershell.exe",
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_room);
}
