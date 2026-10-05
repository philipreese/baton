using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Baton.CrashTestHost;

/// <summary>Offline native-pipe fixture. Every git remote and output belongs to its private test root.</summary>
public static class AgyStreamProcessMode
{
    public static async Task<int> RunAsync(string mode, string output, string workspace, string? rawPrompt = null)
    {
        var parts = mode.Split(':');
        mode = parts[0];
        var arm = parts.Length > 1 ? parts[1] : "ordinary";
        var raw = mode.StartsWith("raw-", StringComparison.Ordinal);
        if (raw) mode = mode[4..];
        if (mode == "descendant")
        {
            File.WriteAllText(Path.Combine(output, "descendant.json"), JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                birth = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            }));
            await Task.Delay(TimeSpan.FromMinutes(2));
            return 0;
        }
        var first = raw ? DecodeInput(rawPrompt ?? throw new InvalidOperationException("Missing raw argv prompt.")) : await ReadInputAsync();
        File.WriteAllText(Path.Combine(output, "first.txt"), first, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "grant.txt"), Environment.GetEnvironmentVariable("BATON_HOOK_DENIED_TOOLS"));
        if (workspace != "none")
        {
            if (!Directory.Exists(Path.Combine(workspace, ".git"))) await PrepareWorkspaceAsync(workspace);
            File.WriteAllText(Path.Combine(workspace, "first-turn.txt"), "first turn work");
            await GitAsync(workspace, "add", "first-turn.txt");
            await GitAsync(workspace, "commit", "-m", "first turn");
            await GitAsync(workspace, "push", "--set-upstream", "origin", "lane");
        }
        File.WriteAllText(Path.Combine(output, "report.md"), "First turn produced the complete declared output.");
        Emit(new { @event = "init", conversation_id = "fixture-conversation" });
        Step(0, "user_input");
        Step(1, "agent_response", 10, 2);
        if (mode == "checkpoint")
        {
            File.WriteAllText(Path.Combine(output, "checkpoint.txt"), "checkpoint helper launched");
            Result("SUCCESS", 10, 2);
            return 0;
        }
        if (mode == "claim-only")
        {
            using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (!File.Exists(Path.Combine(output, "release")))
                await Task.Delay(20, releaseTimeout.Token); // wait-ok: bounded claim/result rendezvous
            Result("SUCCESS", 10, 2);
            return 0;
        }
        if (mode == "one")
        {
            Result("SUCCESS", 10, 2);
            return await Console.In.ReadLineAsync() is null ? 0 : 7;
        }
        string second;
        if (raw) second = "";
        else if (mode == "race")
        {
            var firstResult = Task.Run(async () =>
            {
                using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                while (!File.Exists(Path.Combine(output, "release")))
                    await Task.Delay(20, releaseTimeout.Token); // wait-ok: bounded first-result race rendezvous
                Result("SUCCESS", 10, 2);
            });
            var line = await Console.In.ReadLineAsync();
            await firstResult;
            if (line is null) return 0;
            second = DecodeInput(line);
        }
        else second = await ReadInputAsync();
        File.WriteAllText(Path.Combine(output, "second.txt"), second, new UTF8Encoding(false));
        if (!raw && mode != "race") Result("SUCCESS", 10, 2);
        Step(2, "user_input");
        if (arm == "post-cap") File.Delete(Path.Combine(output, "report.md"));
        if (mode == "timeout" && arm is "pre-cap" or "post-cap") Step(3, "agent_response", 20, 3);
        if (arm is not ("pre-cap" or "post-cap"))
            Step(3, "agent_response", mode switch
            {
                "overflow" => long.MaxValue,
                "negative" => -20,
                "cumulative" => long.MaxValue - 20,
                _ => 20,
            }, 3);
        if (mode == "cumulative") Step(4, "agent_response", 10, 0);
        if (mode == "duplicate") Step(3, "agent_response", 20, 3);
        if (mode == "conflict") Step(3, "agent_response", 21, 3);
        if (mode is "timeout" or "descendants")
        {
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "exec", typeof(AgyStreamProcessMode).Assembly.Location, "agy-stream-fixture", "descendant", output, "none" })
                info.ArgumentList.Add(arg);
            using var descendant = Process.Start(info)!;
            using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (!File.Exists(Path.Combine(output, "descendant.json")))
                await Task.Delay(20, readyTimeout.Token); // wait-ok: bounded descendant rendezvous
            if (mode == "timeout") await Task.Delay(TimeSpan.FromMinutes(2));
        }
        if (mode == "missing")
        {
            if (arm is "pre-cap" or "post-cap") Step(3, "agent_response", 20, 3);
            if (arm.Contains("timeout", StringComparison.Ordinal)) await Task.Delay(TimeSpan.FromMinutes(2));
            return 0;
        }
        if (mode == "corrupt") Console.WriteLine("{torn-host-looking-output");
        if (mode == "forged") Emit(new { Version = 1, Kind = "completion", Completion = new { Successful = true } });
        var status = mode is "failure" or "capacity" ? "ERROR" : "SUCCESS";
        Result(status, 999, 888, mode == "capacity" ? "Individual quota reached. Resets in 1h" : "final turn failed");
        if (arm is "pre-cap" or "post-cap") Step(3, "agent_response", 20, 3);
        if (arm.Contains("timeout", StringComparison.Ordinal)) await Task.Delay(TimeSpan.FromMinutes(2));
        return mode == "failure" ? 7 : 0;
    }

    private static async Task<string> ReadInputAsync()
    {
        var line = await Console.In.ReadLineAsync() ?? throw new InvalidOperationException("Missing native input.");
        return DecodeInput(line);
    }

    public static async Task PrepareWorkspaceAsync(string workspace)
    {
        Directory.CreateDirectory(workspace);
        await GitAsync(workspace, "init", "--initial-branch=lane");
        await GitAsync(workspace, "config", "user.email", "fixture@example.com");
        await GitAsync(workspace, "config", "user.name", "Fixture");
        File.WriteAllText(Path.Combine(workspace, "base.txt"), "base");
        await GitAsync(workspace, "add", "base.txt");
        await GitAsync(workspace, "commit", "-m", "base");
        await GitAsync(workspace, "init", "--bare", workspace + "-remote");
        await GitAsync(workspace, "remote", "add", "origin", workspace + "-remote");
    }

    private static string DecodeInput(string line)
    {
        using var document = JsonDocument.Parse(line);
        if (document.RootElement.GetProperty("event").GetString() != "user") throw new InvalidOperationException("Wrong input event.");
        return document.RootElement.GetProperty("message").GetProperty("content").GetString()!;
    }

    private static void Step(int index, string type, long? input = null, long? output = null) => Emit(new
    {
        @event = "step_update",
        step_update = new
        {
            conversation_id = "fixture-conversation",
            step_index = index,
            state = "DONE",
            step_type = type,
            usage = input is null ? null : new { input_tokens = input, output_tokens = output }
        },
    });

    private static void Result(string status, long input, long output, string? error = null) => Emit(new
    {
        @event = "result",
        result = new
        {
            conversation_id = "fixture-conversation",
            status,
            error,
            response = status == "SUCCESS" ? "complete response" : "",
            usage = new { input_tokens = input, output_tokens = output }
        },
    });

    private static void Emit(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value));
        Console.Out.Flush();
    }

    private static async Task GitAsync(string cwd, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var (_, error) = await BoundedProcessWait.RunToExitAsync(process, TimeSpan.FromSeconds(60));
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
    }
}
