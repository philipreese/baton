using System.Text.Json.Nodes;

namespace Baton.Vendors.Tests;

public sealed class CodexAppServerBrokerFollowTranscriptTests
{
    [Fact]
    public async Task Resumed_follow_uses_the_retained_thread_and_persists_identity_before_turn()
    {
        var configuration = new CodexBrokerConfiguration(
            "C:\\workspace", "gpt-5.6-luna", "low", "thread-1", true,
            new PermissionGrant(ReadFiles: true), [], false);
        using var serverOutput = new StringReader(string.Join('\n',
        [
            "{\"id\":1,\"result\":{}}",
            "{\"id\":2,\"result\":{\"thread\":{\"id\":\"thread-1\"}}}",
            "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\"}}}",
            "{\"method\":\"turn/completed\",\"params\":{\"turn\":{\"status\":\"completed\"}}}",
        ]) + "\n");
        using var serverInput = new StringWriter();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var persisted = string.Empty;
        var policy = new CodexDynamicToolPolicy(configuration.PermissionGrant, configuration.WorkingDirectory!, "C:\\out", [], []);

        var exitCode = await CodexAppServerBroker.RunProtocolAsync(
            configuration, "event", policy, null, serverInput, serverOutput, output, error,
            TestContext.Current.CancellationToken, threadStarted: (id, _) =>
            {
                persisted = id;
                Assert.DoesNotContain("turn/start", serverInput.ToString());
                return Task.CompletedTask;
            });

        Assert.Equal(0, exitCode);
        Assert.Equal("thread-1", persisted);
        var requests = serverInput.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal("thread/resume", requests[2]["method"]!.GetValue<string>());
        Assert.Equal("thread-1", requests[3]["params"]!["threadId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Failed_thread_identity_persistence_prevents_turn_start()
    {
        var configuration = new CodexBrokerConfiguration(
            "C:\\workspace", "gpt-5.6-luna", "low", null, false,
            new PermissionGrant(ReadFiles: true), [], false);
        using var serverOutput = new StringReader(string.Join('\n',
        [
            "{\"id\":1,\"result\":{}}",
            "{\"id\":2,\"result\":{\"thread\":{\"id\":\"thread-1\"}}}",
        ]) + "\n");
        using var serverInput = new StringWriter();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var policy = new CodexDynamicToolPolicy(configuration.PermissionGrant, configuration.WorkingDirectory!, "C:\\out", [], []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CodexAppServerBroker.RunProtocolAsync(
            configuration, "event", policy, null, serverInput, serverOutput, output, error,
            TestContext.Current.CancellationToken, threadStarted: (_, _) =>
                throw new InvalidOperationException("identity persistence failed")));

        var requests = serverInput.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.DoesNotContain(requests, request => request["method"]?.GetValue<string>() == "turn/start");
    }

    [Fact]
    public async Task Mismatched_resumed_thread_prevents_turn_start()
    {
        var configuration = new CodexBrokerConfiguration(
            "C:\\workspace", "gpt-5.6-luna", "low", "expected", true,
            new PermissionGrant(ReadFiles: true), [], false);
        using var serverOutput = new StringReader(string.Join('\n',
        [
            "{\"id\":1,\"result\":{}}",
            "{\"id\":2,\"result\":{\"thread\":{\"id\":\"other\"}}}",
        ]) + "\n");
        using var serverInput = new StringWriter();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var policy = new CodexDynamicToolPolicy(configuration.PermissionGrant, configuration.WorkingDirectory!, "C:\\out", [], []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CodexAppServerBroker.RunProtocolAsync(
            configuration, "event", policy, null, serverInput, serverOutput, output, error,
            TestContext.Current.CancellationToken));

        var requests = serverInput.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.DoesNotContain(requests, request => request["method"]?.GetValue<string>() == "turn/start");
    }
}
