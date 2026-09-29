using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;

namespace Baton.Vendors.Tests;

public sealed class StoppedWorkAdviceAdapterTests
{
    private static readonly FleetAttemptId Attempt = new("attempt-2499");
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 29, 15, 30, 0, TimeSpan.Zero);
    private const string Repository = "philipreese/baton";
    private const string Tag = "2499-stopped-work-advice";

    private static readonly StoppedWorkAdviceContext Context = new(
        Repository, Tag, Attempt, WorkStage.Review, ObservedAt, null,
        "abcdef0123456789abcdef0123456789abcdef01", "failed", false, "failing", ObservedAt,
        StoppedWorkHaltCause.MissingVerdict, "exhausted", false, "failing", StoppedWorkJudgmentState.Pending);

    private static readonly StoppedWorkAdviceRequest Request = new(
        "obligation-2499", Repository, Tag, Attempt, WorkStage.Review,
        StoppedWorkAdviceEvidence.Hash(Context), ObservedAt, null,
        "abcdef0123456789abcdef0123456789abcdef01", "repository-conductor",
        StoppedWorkHaltCause.MissingVerdict, "exhausted", false, "failing", StoppedWorkJudgmentState.Pending);

    [Fact]
    public void Prelaunch_accepts_an_explicitly_unknown_pull_request_head()
    {
        CodexReadinessDecisionAdapter.ValidateStoppedWorkPrelaunch(Request, Context);
    }

    [Fact]
    public void Prelaunch_rejects_context_identity_drift()
    {
        var drifted = Context with { Tag = "other-tag" };

        var error = Assert.Throws<InvalidOperationException>(() =>
            CodexReadinessDecisionAdapter.ValidateStoppedWorkPrelaunch(Request, drifted));

        Assert.Contains("identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prelaunch_rejects_unbounded_typed_evidence()
    {
        var unbounded = Context with { Checks = new string('x', 4097) };

        var error = Assert.Throws<InvalidOperationException>(() =>
            CodexReadinessDecisionAdapter.ValidateStoppedWorkPrelaunch(Request, unbounded));

        Assert.Contains("bounded", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prelaunch_rejects_a_non_sha_context_digest()
    {
        var invalid = Request with { ContextSha256 = "unknown" };

        var error = Assert.Throws<InvalidOperationException>(() =>
            CodexReadinessDecisionAdapter.ValidateStoppedWorkPrelaunch(invalid, Context));

        Assert.Contains("digest", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_prompt_excludes_holder_and_private_evidence_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "baton-stopped-advice-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "mode.txt"), "tool");
        string? prompt = null;
        var hostDirectory = Path.GetDirectoryName(typeof(Baton.CrashTestHost.Scenarios).Assembly.Location)!;
        var executable = Path.Combine(hostDirectory,
            "Baton.CrashTestHost" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        try
        {
            var adapter = new CodexReadinessDecisionAdapter(executable, TimeSpan.FromSeconds(15),
                promptObserverForTests: captured => prompt = captured);
            await Assert.ThrowsAnyAsync<Exception>(() => adapter.DecideStoppedWorkAsync(
                Request, Context, root, TestContext.Current.CancellationToken));
            Assert.NotNull(prompt);
            Assert.DoesNotContain(Request.Holder, prompt, StringComparison.Ordinal);
            Assert.DoesNotContain(root, prompt, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }
}
