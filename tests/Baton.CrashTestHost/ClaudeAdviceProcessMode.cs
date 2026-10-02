using System.Diagnostics;
using System.Text.Json;

namespace Baton.CrashTestHost;

internal static class ClaudeAdviceProcessMode
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var preflightPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "preflight.txt");
        var preflight = File.Exists(preflightPath) ? File.ReadAllText(preflightPath).Trim() : null;
        if (args is ["--version"])
        {
            if (preflight == "slow-overflow")
                await Task.Delay(TimeSpan.FromSeconds(1)); // wait-ok: offline slow-start overflow fixture
            Console.WriteLine(preflight == "version" ? "2.1.282 (Claude Code)" : "2.1.283 (Claude Code)");
            return 0;
        }
        if (args is ["--help"])
        {
            if (preflight == "slow-overflow")
                await Task.Delay(TimeSpan.FromSeconds(1)); // wait-ok: offline slow-start overflow fixture
            Console.WriteLine((preflight == "flags" ? "" : "--tools ") + "--strict-mcp-config --mcp-config --setting-sources --disable-slash-commands "
                + "--settings --session-id --max-turns --max-budget-usd --output-format --verbose --model --effort");
            return 0;
        }
        if (args is ["auth", "status"])
        {
            if (preflight == "slow-overflow")
                await Task.Delay(TimeSpan.FromSeconds(1)); // wait-ok: offline slow-start overflow fixture
            Console.WriteLine(preflight == "auth" ? """{"loggedIn":true,"authMethod":"api-key"}"""
                : """{"loggedIn":true,"authMethod":"claude.ai"}""");
            return 0;
        }
        if (args.Length < 2 || args[0] != "-p") return 21;
        var evidence = args[1].Split('\n').First(line => line.StartsWith('{'));
        using var document = JsonDocument.Parse(evidence);
        var request = document.RootElement;
        var mode = request.GetProperty("tag").GetString();
        var sessionIndex = Array.IndexOf(args, "--session-id");
        var session = args[sessionIndex + 1];
        Console.Error.WriteLine("parent-pid:" + Environment.ProcessId);
        Console.Error.Flush();
        if (mode is "stdout-overflow" or "stderr-overflow" or "stdout-overflow-slow" or "stderr-overflow-slow")
        {
            if (preflight == "slow-overflow")
                await Task.Delay(TimeSpan.FromSeconds(2)); // wait-ok: offline slow-start overflow fixture
            var stream = mode.StartsWith("stdout", StringComparison.Ordinal) ? Console.Out : Console.Error;
            stream.Write(new string('x', 1048577));
            stream.Flush();
            await Task.Delay(TimeSpan.FromMinutes(1)); // wait-ok: owned overflow helper must be killed
            return 0;
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            type = "system",
            subtype = "init",
            session_id = session,
            model = "claude-haiku-4-5-20251001",
            tools = Array.Empty<string>(),
            mcp_servers = Array.Empty<string>(),
            skills = Array.Empty<string>(),
            slash_commands = Array.Empty<string>(),
        }));
        Console.Out.Flush();
        if (mode == "blocked-input")
        {
            if (Console.In.Read() != -1) return 22;
            await Task.Delay(TimeSpan.FromMinutes(1)); // wait-ok: EOF then wait until bounded cancellation
            return 0;
        }
        if (mode == "parent-exit")
        {
            var start = new ProcessStartInfo("ping.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-n", "9999", "127.0.0.1" }) start.ArgumentList.Add(argument);
            using var descendant = Process.Start(start)!;
            Console.Error.WriteLine("child-pid:" + descendant.Id);
            Console.Error.Flush();
        }
        var answer = JsonSerializer.Serialize(new
        {
            obligationId = request.GetProperty("obligationId").GetString(),
            repository = request.GetProperty("repository").GetString(),
            tag = mode,
            attemptId = request.GetProperty("attemptId").GetString(),
            contextSha256 = request.GetProperty("contextSha256").GetString(),
            choice = "hold",
            explanation = "Missing verdict requires separate action authority.",
        });
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            type = "result",
            subtype = mode == "terminal-error" ? "error_max_turns" : "success",
            is_error = mode == "terminal-error",
            session_id = session,
            result = "```json\n" + answer + "\n```",
            num_turns = 1,
            total_cost_usd = 0.01m,
            usage = new { input_tokens = 12, output_tokens = 4, cache_read_input_tokens = 2, cache_creation_input_tokens = 1 },
        }));
        Console.Out.Flush();
        return 0;
    }
}
