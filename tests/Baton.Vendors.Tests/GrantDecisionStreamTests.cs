using System.Text.Json.Nodes;
using Baton.Domain;
using Baton.Tests.Shared;

namespace Baton.Vendors.Tests;

/// <summary>
/// #2009, the codex enforcement point (first of the three spec/baton.md §9 names): a grant decision the broker
/// takes is one structured line in the room's captured stream, not a sentence a later reader has to
/// find inside tool output.
/// <para>
/// Driven from the app-server transcript rather than from <see cref="CodexDynamicToolPolicy"/> alone,
/// which is the instrument the claim needs: the policy returning a refusal proves nothing about what
/// the room's <c>.stdout.log</c> ends up holding, and "the rule shipped in a class no caller reaches"
/// is exactly the half-shipped shape spec/baton.md §9 records against
/// <c>BackgroundingShapeDetector</c>.
/// </para>
/// </summary>
public sealed class GrantDecisionStreamTests
{
    /// <summary>
    /// One refused call and one allowed call in the same turn, so both arms are read off one stream:
    /// the deny line names the rule, the allow line exists and names none, and neither is inferred
    /// from the refusal sentence sitting in the <c>item.completed</c> beside it.
    /// </summary>
    [Fact]
    public async Task A_refused_and_an_allowed_dynamic_tool_call_each_write_one_grant_line()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-grant-stream-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(output);
        try
        {
            File.WriteAllText(Path.Combine(workspace, "notes.md"), "the readable file\n");

            // Reads granted, writes withheld: the write below is refused by the grant, the read above
            // it is not, and one transcript therefore carries both polarities.
            var grant = new PermissionGrant(ReadFiles: true);
            var configuration = new CodexBrokerConfiguration(
                workspace, "gpt-5.6-terra", "low", null, false, grant, ["changes.md"], false);
            var policy = new CodexDynamicToolPolicy(grant, workspace, output, [], ["changes.md"]);

            var lines = await RunTurnAsync(configuration, policy,
                Call(21, CodexDynamicToolPolicy.WriteTextTool,
                    new JsonObject { ["path"] = "notes.md", ["content"] = "rewritten" }),
                Call(22, CodexDynamicToolPolicy.ReadTextTool,
                    new JsonObject { ["path"] = "notes.md" }));

            var grants = GrantLines(lines);
            Assert.Equal(2, grants.Length);

            var denied = Assert.Single(grants, line => line["decision"]!.GetValue<string>() == "deny");
            Assert.Equal(CodexDynamicToolPolicy.WriteTextTool, denied["tool"]!.GetValue<string>());
            Assert.Equal(GrantRules.WithheldTool.Id, denied["rule"]!.GetValue<string>());
            Assert.Equal("codex", denied["vendor"]!.GetValue<string>());
            Assert.Contains(GrantRefusal.Marker, denied["reason"]!.GetValue<string>());

            var allowed = Assert.Single(grants, line => line["decision"]!.GetValue<string>() == "allow");
            Assert.Equal(CodexDynamicToolPolicy.ReadTextTool, allowed["tool"]!.GetValue<string>());
            Assert.Equal(GrantRules.Allowed.Id, allowed["rule"]!.GetValue<string>());
            // No reason on an allow: nothing refused it, and every byte here is a byte of the room's
            // stream (ExecutionStreamLogger's rollover bound).
            Assert.Null(allowed["reason"]);

            // The identity is the one the item.started line already carries for the same call, which is
            // what makes "this room retried the call it was refused" an equality rather than a search.
            var startedDigests = lines
                .Select(line => JsonNode.Parse(line)!)
                .Where(line => line["type"]!.GetValue<string>() == "item.started")
                .Select(line => line["item"]![Baton.Status.CodexUsageParser.ArgumentsDigestField]!.GetValue<string>())
                .ToArray();
            Assert.Contains(denied["input"]!.GetValue<string>(), startedDigests);
            Assert.Contains(allowed["input"]!.GetValue<string>(), startedDigests);

            // The refusal marker now appears TWICE in this stream for one refused call — in the
            // item.completed's aggregated_output, and again in the grant line's reason — so the reader
            // of the room's refusal count is asserted here rather than assumed. It anchors on the
            // completed-item node (see CodexUsageParser.CountRefusedToolSteps), which is what keeps
            // this at one; a reader widened to a whole-stream search would double it, and a refusal
            // count that silently doubles is a correctly-computed wrong answer.
            var parser = new Baton.Status.CodexUsageParser();
            Assert.Equal(1, lines.Sum(parser.CountRefusedToolSteps));

            // And the two readers that walk every line rather than partitioning by type ignore a line
            // whose type they do not know, rather than throwing or folding it in.
            foreach (var grantLine in grants)
            {
                Assert.False(parser.TryParseIncrementalUsage(grantLine.ToJsonString(), out _));
            }

            Assert.Null(parser.ParseExecutionUsage(lines));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// The control arm that makes the deny count above mean something. A command that Baton ALLOWED and
    /// that then failed on its own — the population <c>CodexDynamicToolResult.Failed</c> exists to keep
    /// separate — writes an <b>allow</b> line: if the grant line were derived from "did this call
    /// succeed" rather than from the rule that decided it, this is where the count would inflate, and
    /// over-counting failing allowed calls as refusals is the exact defect #1921 fixed once already.
    /// </summary>
    [Fact]
    public async Task A_read_of_a_missing_file_is_recorded_as_an_allow_because_no_rule_refused_it()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-grant-stream-failed-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(output);
        try
        {
            var grant = new PermissionGrant(ReadFiles: true);
            var configuration = new CodexBrokerConfiguration(
                workspace, "gpt-5.6-terra", "low", null, false, grant, ["changes.md"], false);
            var policy = new CodexDynamicToolPolicy(grant, workspace, output, [], ["changes.md"]);

            var lines = await RunTurnAsync(configuration, policy,
                Call(31, CodexDynamicToolPolicy.ReadTextTool,
                    new JsonObject { ["path"] = "absent.md" }));

            var line = Assert.Single(GrantLines(lines));
            Assert.Equal("allow", line["decision"]!.GetValue<string>());
            Assert.Equal(GrantRules.Allowed.Id, line["rule"]!.GetValue<string>());

            // And the call really did fail, so this is the discriminating case rather than a happy path
            // wearing its name.
            Assert.Contains(lines, raw => raw.Contains("\"status\":\"failed\"", StringComparison.Ordinal));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    private static JsonObject Call(int id, string tool, JsonObject arguments) => new()
    {
        ["id"] = id,
        ["method"] = "item/tool/call",
        ["params"] = new JsonObject
        {
            ["tool"] = tool,
            ["arguments"] = arguments,
            ["callId"] = $"call-{id}",
            ["threadId"] = "thread-1",
            ["turnId"] = "turn-1",
        },
    };

    private static async Task<string[]> RunTurnAsync(
        CodexBrokerConfiguration configuration, CodexDynamicToolPolicy policy, params JsonObject[] calls)
    {
        var transcript = string.Join('\n',
        [
            """{"id":1,"result":{"userAgent":"fixture"}}""",
            """{"id":2,"result":{"thread":{"id":"thread-1"}}}""",
            """{"id":3,"result":{"turn":{"id":"turn-1","status":"inProgress","items":[]}}}""",
            .. calls.Select(call => call.ToJsonString()),
            """{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[]}}}""",
        ]) + "\n";

        using var serverOutput = new StringReader(transcript);
        using var serverInput = new StringWriter();
        using var batonOutput = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await CodexAppServerBroker.RunProtocolAsync(
            configuration, "Do the work.", policy, serverInput, serverOutput,
            batonOutput, error, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        return batonOutput.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static JsonObject[] GrantLines(IEnumerable<string> lines) => lines
        .Select(line => JsonNode.Parse(line)!.AsObject())
        .Where(line => line["type"]!.GetValue<string>() == GrantDecision.EventType)
        .ToArray();
}
