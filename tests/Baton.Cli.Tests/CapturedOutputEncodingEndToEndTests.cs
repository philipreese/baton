using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Store;
using Baton.Templates;
using Xunit;

namespace Baton.Cli.Tests;

public class CapturedOutputEncodingEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Captured_stdout_preserves_raw_utf8_bytes_without_codepage_mangling()
    {
        if (!IsPythonAvailable())
        {
            Assert.Skip("python is not on PATH");
            return;
        }

        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-utf8-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);

            // UTF-8 non-ASCII byte sequence containing characters reported in issue #466 (em-dash, arrows, etc.)
            byte[] fixtureBytes = "Me ↔ you: — Ⅵåæ 🚀 non-ascii utf8 sample"u8.ToArray();
            var fixturePath = Path.Combine(testRoot, "fixture.bin");
            await File.WriteAllBytesAsync(fixturePath, fixtureBytes, TestContext.Current.CancellationToken);

            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteOneStepBindingsAsync(testRoot, fixturePath);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var capturedStdoutLines = new List<string>();
            Action<string, string> onWorkerStdoutLine = (worker, line) =>
            {
                capturedStdoutLines.Add(line);
            };

            var finalState = (await RunCommand.ExecuteAsync(options, Adapters, onWorkerStdoutLine: onWorkerStdoutLine, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.All(finalState.Steps, FlowAssert.Succeeded);

            // Verify output artifact was produced
            var stepState = finalState.Steps.First(s => s.StepId.Value == "step1");
            var outputPath = Path.Combine(roomDirectory, "artifacts", $"execution_{stepState.LatestExecutionId}", "output1");
            Assert.True(File.Exists(outputPath), $"Expected output artifact at {outputPath}");

            // The claim under test is capture fidelity: fixture bytes -> python stdout -> BatonTask
            // capture -> CoreDispatcher's UTF-8 decode -> this callback's strings. Re-encoding the
            // captured strings must reproduce the exact original bytes; any codepage transcoding
            // anywhere in that pipeline breaks the round trip and fails here. Asserted on the
            // callback directly — never by writing anything into flow.jsonl ourselves, which would
            // fabricate events and prove only our own append (this test's first draft did exactly
            // that; kept as a warning).
            byte[] recapturedBytes = Encoding.UTF8.GetBytes(string.Join("\n", capturedStdoutLines));
            bool containsExactBytes = ContainsSequence(recapturedBytes, fixtureBytes);

            Assert.True(
                containsExactBytes,
                $"The captured stdout did not round-trip the exact UTF-8 byte sequence python emitted — " +
                $"the engine-side capture transcoded it (#466's engine half CONFIRMED). " +
                $"Fixture bytes ({fixtureBytes.Length}): {BitConverter.ToString(fixtureBytes)}");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static bool IsPythonAvailable()
    {
        try
        {
            using var process = global::Baton.Core.ProcessLaunch.Start(new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return false;
            process.WaitForExit(2000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return true;
        if (haystack.Length < needle.Length) return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }

    private static async Task<string> WriteOneStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("one-step-linear"),
            1,
            [
                new WorkflowStepDefinition(new StepId("step1"), "step1", [], ["output1"], [], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition), TestContext.Current.CancellationToken);
        return path;
    }

    private static async Task<string> WriteOneStepBindingsAsync(string directory, string fixturePath)
    {
        Directory.CreateDirectory(directory);
        var fixturePathEscaped = fixturePath.Replace("\\", "/");
        var pythonCode = $"""
            import sys, os
            sys.stdout.buffer.write(open('{fixturePathEscaped}', 'rb').read())
            sys.stdout.buffer.flush()
            output_dir = os.environ['BATON_OUTPUT_DIR']
            with open(os.path.join(output_dir, 'output1'), 'wb') as f:
                f.write(b'done')
            """;
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(pythonCode));
        // The base64 payload's spacelessness is load-bearing: cmd re-splits the template, so the
        // spaceless exec(...) expression survives BARE as one token — adding quotes breaks it (the
        // managed spawn path's own Windows argument quoting, via ProcessStartInfo.ArgumentList,
        // escapes them into literal characters in python's -c payload; measured red).
        var command = $"python -c exec(__import__('base64').b64decode('{b64}'))";

        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["step1"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("step1", [], [new ProducedOutput("output1")], []),
                command,
                TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config), TestContext.Current.CancellationToken);
        return path;
    }
}
