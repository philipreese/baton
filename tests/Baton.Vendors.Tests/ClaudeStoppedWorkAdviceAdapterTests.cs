using System.Text.Json;
using Baton.Conductor;
using Baton.CrashTestHost;
using Baton.Domain;
using Baton.Queue;

namespace Baton.Vendors.Tests;

public sealed class ClaudeStoppedWorkAdviceAdapterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static StoppedWorkAdviceContext Context(string tag = "valid") => new(
        "github.com/example/project", tag, new("attempt-1"), WorkStage.Review, Now, null, null,
        "Succeeded", true, "passing", Now, StoppedWorkHaltCause.MissingVerdict, "available", false, "passing");
    private static StoppedWorkAdviceRequest Request(string tag = "valid") => new(
        "obligation-1", "github.com/example/project", tag, new("attempt-1"), WorkStage.Review,
        StoppedWorkAdviceEvidence.Hash(Context(tag)), Now, null, null, "private-holder",
        StoppedWorkHaltCause.MissingVerdict, "available", false, "passing");
    private static string Answer(StoppedWorkAdviceRequest request) => JsonSerializer.Serialize(new
    {
        obligationId = request.ObligationId,
        repository = request.Repository,
        tag = request.Tag,
        attemptId = request.AttemptId.Value,
        contextSha256 = request.ContextSha256,
        choice = "hold",
        explanation = "A missing verdict needs separate action authority.",
    });
    private static string Events(string answer, string init = "session-1", string result = "session-1",
        string tools = "[]", string mcp = "[]", string error = "false", string subtype = "success") =>
        "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":" + JsonSerializer.Serialize(init)
        + ",\"model\":\"claude-haiku-4-5-20251001\",\"tools\":" + tools + ",\"mcp_servers\":" + mcp + ",\"skills\":[],\"slash_commands\":[]}\n"
        + "{\"type\":\"result\",\"subtype\":" + JsonSerializer.Serialize(subtype) + ",\"is_error\":" + error
        + ",\"session_id\":" + JsonSerializer.Serialize(result) + ",\"result\":" + JsonSerializer.Serialize(answer) + "}\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void One_bare_or_whole_json_fence_binds_the_real_stopped_work_identity(bool fenced)
    {
        var request = Request();
        var answer = Answer(request);
        if (fenced) answer = "```json\r\n" + answer + "\r\n```";
        var response = ClaudeStoppedWorkAdviceAdapter.ParseEvents(Events(answer), "session-1", request);
        Assert.Equal(StoppedWorkAdviceChoice.Hold, response.Decision.Choice);
        Assert.Equal(StoppedWorkAdviceProviderDescriptor.Claude.Adapter, response.Adapter);
        Assert.Null(response.Usage);
    }

    [Theory]
    [InlineData("prose")]
    [InlineData("trailing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("enum")]
    [InlineData("enum-case")]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("fences")]
    [InlineData("array")]
    [InlineData("malformed")]
    [InlineData("obligationId")]
    [InlineData("repository")]
    [InlineData("tag")]
    [InlineData("attemptId")]
    [InlineData("contextSha256")]
    public void Invalid_declared_answers_refuse_applicability(string mutation)
    {
        var request = Request();
        var answer = Answer(request);
        answer = mutation switch
        {
            "prose" => "Here is the decision: " + answer,
            "trailing" => answer + answer,
            "duplicate" => answer.Insert(1, "\"choice\":\"hold\","),
            "extra" => answer.Insert(1, "\"execute\":true,"),
            "enum" => answer.Replace("\"hold\"", "\"approve\"", StringComparison.Ordinal),
            "enum-case" => answer.Replace("\"hold\"", "\"Hold\"", StringComparison.Ordinal),
            "empty" => answer.Replace("A missing verdict needs separate action authority.", " ", StringComparison.Ordinal),
            "oversized" => answer.Replace("A missing verdict needs separate action authority.", new string('x', 4097), StringComparison.Ordinal),
            "fences" => "```json\n```json\n" + answer + "\n```\n```",
            "array" => "[]",
            "malformed" => "{",
            _ => JsonSerializer.Serialize(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(answer)!
                .ToDictionary(entry => entry.Key, entry => entry.Key == mutation
                    ? JsonSerializer.SerializeToElement("wrong") : entry.Value)),
        };
        Assert.NotNull(Record.Exception(() => ClaudeStoppedWorkAdviceAdapter.ParseEvents(Events(answer), "session-1", request)));
    }

    [Theory]
    [InlineData("tools")]
    [InlineData("mcp")]
    [InlineData("init-id")]
    [InlineData("result-id")]
    [InlineData("error")]
    [InlineData("terminal")]
    [InlineData("tool-event")]
    [InlineData("missing-init")]
    [InlineData("missing-result")]
    [InlineData("duplicate-result")]
    [InlineData("duplicate-key")]
    [InlineData("skills")]
    [InlineData("commands")]
    public void Native_envelope_controls_discriminate_from_valid_answer_syntax(string mutation)
    {
        var request = Request();
        var stream = Events(Answer(request));
        stream = mutation switch
        {
            "tools" => Events(Answer(request), tools: "[\"Bash\"]"),
            "mcp" => Events(Answer(request), mcp: "[{}]"),
            "init-id" => Events(Answer(request), init: ""),
            "result-id" => Events(Answer(request), result: "wrong"),
            "error" => Events(Answer(request), error: "true"),
            "terminal" => Events(Answer(request), subtype: "error_max_turns"),
            "tool-event" => stream.Replace("\n{\"type\":\"result\"", "\n{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\"}]}}\n{\"type\":\"result\"", StringComparison.Ordinal),
            "missing-init" => stream.Split('\n')[1],
            "missing-result" => stream.Split('\n')[0],
            "duplicate-result" => stream + stream.Split('\n')[1],
            "duplicate-key" => stream.Replace("\"tools\":[]", "\"tools\":[],\"tools\":[]", StringComparison.Ordinal),
            "skills" => stream.Replace("\"skills\":[]", "\"skills\":[\"unexpected\"]", StringComparison.Ordinal),
            "commands" => stream.Replace("\"slash_commands\":[]", "\"slash_commands\":[\"unexpected\"]", StringComparison.Ordinal),
            _ => throw new InvalidOperationException(),
        };
        Assert.NotNull(Record.Exception(() => ClaudeStoppedWorkAdviceAdapter.ParseEvents(stream, "session-1", request)));
    }

    [Fact]
    public void Invocation_controls_preserve_authentication_and_select_managed_only_instructions()
    {
        var arguments = ClaudeStoppedWorkAdviceAdapter.BuildArguments("bounded prompt", "session-1");
        Assert.Equal("bounded prompt", arguments[1]); // before the variadic --tools flag
        Assert.DoesNotContain("--bare", arguments);
        Assert.Contains("--disable-slash-commands", arguments);
        Assert.Equal("", arguments[Array.IndexOf(arguments, "--tools") + 1]);
        Assert.Equal("", arguments[Array.IndexOf(arguments, "--setting-sources") + 1]);
        ClaudeStoppedWorkAdviceAdapter.ValidateIsolationSettings(arguments[Array.IndexOf(arguments, "--settings") + 1]);
        Assert.Throws<InvalidOperationException>(() => ClaudeStoppedWorkAdviceAdapter.ValidateIsolationSettings(
            ClaudeStoppedWorkAdviceAdapter.IsolationSettings.Replace("managed-only", "all", StringComparison.Ordinal)));
    }

    [Fact]
    public void Controlled_ancestor_instructions_are_refused_without_reading_contents()
    {
        using var fixture = new HelperRun();
        var child = Path.Combine(fixture.Root, "child");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"), "Controlled fixture only.");
        Assert.Throws<InvalidOperationException>(() => ClaudeStoppedWorkAdviceAdapter.ValidateWorkingDirectory(child));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("flags")]
    [InlineData("auth")]
    public async Task Free_preflight_contract_refusals_never_start_a_decision(string refusal)
    {
        using var run = new HelperRun();
        File.WriteAllText(Path.Combine(run.Root, "preflight.txt"), refusal);
        var adapter = new ClaudeStoppedWorkAdviceAdapter(run.Executable, TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.PreflightAsync(Ct));
        Assert.Empty(Directory.GetFiles(run.Evidence));
    }

    [Fact]
    public void Native_usage_retains_measured_cache_fields_and_unknown_fields_remain_unknown()
    {
        var request = Request();
        var stream = Events(Answer(request)).Replace("\"is_error\":false", "\"is_error\":false,\"usage\":{\"input_tokens\":12,\"cache_read_input_tokens\":3,\"cache_creation_input_tokens\":4},\"total_cost_usd\":0.01", StringComparison.Ordinal);
        var response = ClaudeStoppedWorkAdviceAdapter.ParseEvents(stream, "session-1", request);
        Assert.Equal(12, response.Usage!.InputTokens);
        Assert.Null(response.Usage.OutputTokens);
        Assert.Equal(3, response.Usage.CachedInputTokens);
        Assert.Equal(4, response.Usage.CacheCreationInputTokens);
        Assert.Equal(0.01m, response.Usage.ApiEquivalentCostUsd);
        Assert.Throws<InvalidOperationException>(() => ClaudeStoppedWorkAdviceAdapter.ParseEvents(
            stream.Replace("0.01", "0.21", StringComparison.Ordinal), "session-1", request));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("terminal-error")]
    [InlineData("stdout-overflow")]
    [InlineData("stderr-overflow")]
    [InlineData("stdout-overflow-slow")]
    [InlineData("stderr-overflow-slow")]
    [InlineData("blocked-input")]
    [InlineData("parent-exit")]
    public async Task Real_contained_helpers_cover_transport_success_refusal_caps_and_descendants(string mode)
    {
        using var run = new HelperRun();
        if (mode.EndsWith("-slow", StringComparison.Ordinal))
            File.WriteAllText(Path.Combine(run.Root, "preflight.txt"), "slow-overflow");
        // wait-ok: only blocked-input is a deliberate 4s cancellation control; all other helper cases are normal synchronization.
        var adapter = new ClaudeStoppedWorkAdviceAdapter(run.Executable,
            mode == "blocked-input" ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(60));
        var failure = await Record.ExceptionAsync(() => adapter.DecideAsync(Request(mode), Context(mode), run.Evidence, Ct));
        if (mode == "valid") Assert.Null(failure);
        else Assert.NotNull(failure);
        if (mode is "stdout-overflow" or "stderr-overflow" or "stdout-overflow-slow" or "stderr-overflow-slow")
            Assert.Contains("exceeded 1 MiB", failure!.Message, StringComparison.Ordinal);
        var diagnostics = File.ReadAllText(Path.Combine(run.Evidence, "claude.stderr.txt"));
        var parent = int.Parse(diagnostics.Split('\n').First(line => line.StartsWith("parent-pid:", StringComparison.Ordinal))[11..]);
        Assert.False(FakeCodexRun.IsAlive(parent));
        if (mode == "parent-exit")
        {
            var descendant = int.Parse(diagnostics.Split('\n').First(line => line.StartsWith("child-pid:", StringComparison.Ordinal))[10..]);
            Assert.False(FakeCodexRun.IsAlive(descendant));
        }
        foreach (var path in Directory.GetFiles(run.Evidence)) Assert.True(new FileInfo(path).Length <= ClaudeStoppedWorkAdviceAdapter.MaxStreamBytes);
    }

    [Fact]
    public async Task Capture_delivery_failure_and_shutdown_cancel_the_owned_process()
    {
        foreach (var failCapture in new[] { false, true })
        {
            using var run = new HelperRun();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            Task Observe(FileStream _, CancellationToken __)
            {
                if (failCapture) throw new IOException("controlled evidence write failure");
                cancellation.Cancel();
                return Task.CompletedTask;
            }
            var adapter = new ClaudeStoppedWorkAdviceAdapter(run.Executable, TimeSpan.FromSeconds(10), Observe);
            Assert.NotNull(await Record.ExceptionAsync(() => adapter.DecideAsync(Request("blocked-input"),
                Context("blocked-input"), run.Evidence, cancellation.Token)));
            var diagnostics = File.ReadAllText(Path.Combine(run.Evidence, "claude.stderr.txt"));
            if (diagnostics.Contains("parent-pid:", StringComparison.Ordinal))
            {
                var parent = int.Parse(diagnostics.Split('\n').First(line => line.StartsWith("parent-pid:", StringComparison.Ordinal))[11..]);
                Assert.False(FakeCodexRun.IsAlive(parent));
            }
        }
    }

    private sealed class HelperRun : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "baton-claude-helper-" + Guid.NewGuid().ToString("N"));
        public string Evidence => Path.Combine(Root, "evidence");
        public string Executable => Path.Combine(Root, "claude-advice-helper.exe");
        public HelperRun()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Evidence);
            var source = Path.GetDirectoryName(typeof(Scenarios).Assembly.Location)!;
            foreach (var path in Directory.GetFiles(source)) File.Copy(path, Path.Combine(Root, Path.GetFileName(path)));
            File.Copy(Path.Combine(source, "Baton.CrashTestHost.exe"), Executable);
        }
        public void Dispose() => DirectoryCleanup.DeleteRecursively(Root);
    }
}
