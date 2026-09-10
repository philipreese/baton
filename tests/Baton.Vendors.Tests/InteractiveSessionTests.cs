using Baton.Vendors.Tests.TestSupport;
using Baton.Vendors;
using Baton.Dispatch;
using Baton.Domain;
using Xunit;

namespace Baton.Vendors.Tests;

[Collection(LaunchConfigCollection.Name)]
public sealed class InteractiveSessionTests
{
    private readonly WorkerContract _contract = new(
        WorkerName: "chat-worker",
        RequiredInputs: [],
        ProducedOutputs: [new ProducedOutput("response.md")],
        OptionalMetadata: []);

    [Fact]
    public void ClaudeWorkerAdapter_ResolvesSessionFlags_FirstTurnAndResumed()
    {
        var adapter = new ClaudeWorkerAdapter();

        // Turn 1: new session ID -> --session-id <uuid>
        var invTurn1 = new WorkerInvocation(
            PromptTemplate: "Hello",
            SessionId: "session-123",
            ResumeSession: false,
            StreamJson: true);

        var target1 = adapter.Resolve(invTurn1, _contract);
        Assert.Contains("--session-id", target1.Args);
        Assert.Contains("session-123", target1.Args);
        // #521: this used to assert --bare IS emitted. Inverted deliberately, and kept rather than
        // deleted, because a silent regression here is invisible: --bare suppresses the mandatory
        // PreToolUse hook (0029/#543) even when the hook is passed explicitly via --settings, and a
        // worker with no gate looks exactly like a worker with a gate that allowed the call. This
        // only catches --bare re-added unconditionally -- it says nothing about --safe-mode, an
        // inherited CLAUDE_CODE_SIMPLE=1, or --bare reintroduced behind a new flag (see the fuller
        // comment in ClaudeWorkerAdapter.cs).
        Assert.DoesNotContain("--bare", target1.Args);
        Assert.Contains("stream-json", target1.Args);
        // #1540: regression coverage for ClaudeWorkerAdapter.Resolve omitting the partial-messages flag
        Assert.DoesNotContain("--include-partial-messages", target1.Args);
        // --print + --output-format=stream-json refuses to run at all without --verbose (confirmed
        // against the installed claude CLI) -- regression coverage for that failure mode.
        Assert.Contains("--verbose", target1.Args);
        Assert.DoesNotContain("--resume", target1.Args);

        // Turn 2: resume session -> --resume <uuid>
        var invTurn2 = new WorkerInvocation(
            PromptTemplate: "Next message",
            SessionId: "session-123",
            ResumeSession: true,
            StreamJson: true);

        var target2 = adapter.Resolve(invTurn2, _contract);
        Assert.Contains("--resume", target2.Args);
        Assert.Contains("session-123", target2.Args);
        Assert.DoesNotContain("--session-id", target2.Args);
    }

    [Fact]
    public void AgyWorkerAdapter_ResolvesSessionFlags_ConversationAndLogFile()
    {
        var adapter = new AgyWorkerAdapter();

        // Turn 1: initial -> --log-file
        var invTurn1 = new WorkerInvocation(
            PromptTemplate: "Hello",
            SessionId: null,
            ResumeSession: false,
            LogFilePath: "/tmp/agy-log.txt");

        var target1 = adapter.Resolve(invTurn1, _contract);
        Assert.Contains("--log-file", target1.Args);
        Assert.Contains("/tmp/agy-log.txt", target1.Args);
        Assert.DoesNotContain("--conversation", target1.Args);

        // Turn 2: resume -> --conversation <id>
        var invTurn2 = new WorkerInvocation(
            PromptTemplate: "Next message",
            SessionId: "conv-999",
            ResumeSession: true);

        var target2 = adapter.Resolve(invTurn2, _contract);
        Assert.Contains("--conversation", target2.Args);
        Assert.Contains("conv-999", target2.Args);
    }

    [Fact]
    public void SynthesizeContextSummary_FormatsHistoryCorrectly()
    {
        List<SessionTurn> turns =
        [
            new SessionTurn(1, "claude", "What is 2+2?", "4", DateTimeOffset.UtcNow, false, false),
            new SessionTurn(2, "claude", "What is 3+3?", "6", DateTimeOffset.UtcNow, true, false)
        ];

        var summary = InteractiveSessionMaterializer.SynthesizeContextSummary(turns, "What is 4+4?");
        Assert.Contains("User: What is 2+2?", summary);
        Assert.Contains("Assistant: 4", summary);
        Assert.Contains("User: What is 3+3?", summary);
        Assert.Contains("Now continue with the following user request:", summary);
        Assert.Contains("What is 4+4?", summary);
    }

    [Fact]
    public async Task ClaudeWorkerAdapter_DiscoverCapabilities_ReturnsModelAliasesAndCompactCommand()
    {
        // #1512 L2: the 2-arg overload's default userHomeDirectory falls through to the real
        // %USERPROFILE%, which would enumerate and fully read every SKILL.md the developer running
        // this suite actually has -- use the same emptyUserHome/configRootDirectory test seam
        // ClaudeSkillDiscoveryTests uses so this stays off the ambient home.
        var emptyUserHome = Path.Combine(Path.GetTempPath(), $"claude-empty-user-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyUserHome);
        try
        {
            var claudeAdapter = new ClaudeWorkerAdapter();
            var claudeCaps = await claudeAdapter.DiscoverCapabilitiesAsync(
                workingDirectory: null,
                userHomeDirectory: emptyUserHome,
                configRootDirectory: string.Empty,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("claude", claudeCaps.Vendor);
            // Claude Code has no "list models" subcommand — `--model` only documents alias examples in
            // --help, so this is a deliberately hardcoded, CLI-independent list (unlike Gemini below).
            Assert.Contains("sonnet", claudeCaps.Models);
            Assert.Contains(claudeCaps.Items, item => item.Name == "/compact");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(emptyUserHome);
        }
    }

    [Fact]
    public async Task AgyWorkerAdapter_DiscoverCapabilities_DoesNotFabricateDataWhenAgyUnavailable()
    {
        // agy is a real vendor CLI coincidentally present on some hosts (never assumed present in
        // CI — see docs/agents/developing-baton.md's live-vendor-smoke-test rule). This only asserts the parts that don't
        // depend on the CLI being installed: it must never throw, and it must never report a
        // model/agent/plugin list it didn't actually observe from `agy models`/`agy agent`/
        // `agy plugin list`.
        var geminiAdapter = new AgyWorkerAdapter();
        var geminiCaps = await geminiAdapter.DiscoverCapabilitiesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("agy", geminiCaps.Vendor);
        Assert.Contains(geminiCaps.Items, item => item.Name == "/compact");
        Assert.Contains(geminiCaps.Items, item => item.Name == "accept-edits" && item.Kind == "mode");
        Assert.DoesNotContain(geminiCaps.Models, m => string.IsNullOrWhiteSpace(m));
    }
}
