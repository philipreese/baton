using System.Text.Json;
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

    private const string TrustedSourceReviewInstructions =
        "For a MissingVerdict halt at Review or ReReview, the recommendation target is only whether "
        + "considering one independent source review of the known pull-request head is warranted to recover "
        + "a usable source verdict. That target is conditional, not source approval, check success, merge "
        + "readiness, or permission to launch anything. A pending check or unknown attempt base alone does "
        + "not forbid considering that review when the PR head is known. A readable verdict is only an "
        + "observation; it does not establish completion, a decision, a reviewed head, or a usable verdict. "
        + "The MissingVerdict halt supplies the missing-usable-verdict observation. Other halt causes and "
        + "stages retain their general advisory meaning. Keep ordinary complete-block handling and separate "
        + "exact-head recovered-review proof distinct. Hold when source facts are insufficient; never force "
        + "recommend.";

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

    [Theory]
    [InlineData(WorkStage.Review)]
    [InlineData(WorkStage.ReReview)]
    public void Real_builder_clarifies_conditional_source_review_target_and_preserves_evidence(WorkStage stage)
    {
        var (request, context) = SourceReviewEvidence(stage);
        var prompt = CodexReadinessDecisionAdapter.BuildStoppedWorkPrompt(request, context);

        Assert.Contains(TrustedSourceReviewInstructions, prompt, StringComparison.Ordinal);
        Assert.Contains("review of the known pull-request head", prompt, StringComparison.Ordinal);
        Assert.Contains("not source approval, check success, merge readiness", prompt, StringComparison.Ordinal);
        Assert.Contains("unknown attempt base alone does not forbid", prompt, StringComparison.Ordinal);
        Assert.Contains("readable verdict is only an observation", prompt, StringComparison.Ordinal);
        Assert.Contains("ordinary complete-block handling", prompt, StringComparison.Ordinal);
        Assert.Contains("exact-head recovered-review proof", prompt, StringComparison.Ordinal);

        using var evidence = ParseGeneratedEvidence(prompt);
        Assert.Equal(request.ObligationId, evidence.RootElement.GetProperty("obligationId").GetString());
        Assert.Equal(request.Repository, evidence.RootElement.GetProperty("repository").GetString());
        Assert.Equal(request.Tag, evidence.RootElement.GetProperty("tag").GetString());
        Assert.Equal(request.AttemptId.Value, evidence.RootElement.GetProperty("attemptId").GetString());
        Assert.Equal(request.ContextSha256, evidence.RootElement.GetProperty("contextSha256").GetString());
        Assert.Equal(WorkStages.Token(request.Stage), evidence.RootElement.GetProperty("stage").GetString());
        Assert.Equal(request.ObservedAt, evidence.RootElement.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal("known-head-2602", evidence.RootElement.GetProperty("pullRequestHead").GetString());
        Assert.Equal("unknown", evidence.RootElement.GetProperty("attemptBaseRevision").GetString());
        Assert.Equal("MissingVerdict", evidence.RootElement.GetProperty("haltCause").GetString());
        Assert.Equal("available", evidence.RootElement.GetProperty("repairAllowance").GetString());
        Assert.Equal("True", evidence.RootElement.GetProperty("verdictAvailable").GetString());
        Assert.Equal("pending", evidence.RootElement.GetProperty("requiredChecks").GetString());
        Assert.Equal("Succeeded", evidence.RootElement.GetProperty("terminalOutcome").GetString());
        Assert.Equal("True", evidence.RootElement.GetProperty("terminalEvidenceAvailable").GetString());
        Assert.Equal("pending", evidence.RootElement.GetProperty("checks").GetString());
        Assert.Equal(context.ChecksObservedAt, evidence.RootElement.GetProperty("checksObservedAt").GetDateTimeOffset());
        Assert.Equal(context.ObservedAt, evidence.RootElement.GetProperty("evidenceObservedAt").GetDateTimeOffset());
    }

    [Fact]
    public void Removing_the_changed_trusted_instructions_breaks_the_real_builder_contract()
    {
        var (request, context) = SourceReviewEvidence(WorkStage.Review);
        var prompt = CodexReadinessDecisionAdapter.BuildStoppedWorkPrompt(request, context);
        var withoutClarification = prompt.Replace(TrustedSourceReviewInstructions, string.Empty, StringComparison.Ordinal);

        var failure = Record.Exception(() => Assert.Contains(
            TrustedSourceReviewInstructions, withoutClarification, StringComparison.Ordinal));

        Assert.NotNull(failure);
    }

    [Theory]
    [InlineData(WorkStage.Review, StoppedWorkHaltCause.Other, "known-head-2602", true)]
    [InlineData(WorkStage.Fix, StoppedWorkHaltCause.MissingVerdict, "known-head-2602", true)]
    public void Other_halts_and_stages_keep_general_advice_meaning(WorkStage stage,
        StoppedWorkHaltCause haltCause, string head, bool terminalEvidenceAvailable)
    {
        var context = SourceReviewContext(stage, haltCause, head, terminalEvidenceAvailable);
        var request = SourceReviewRequest(context);
        var prompt = CodexReadinessDecisionAdapter.BuildStoppedWorkPrompt(request, context);

        Assert.Contains("Other halt causes and stages retain their general advisory meaning.", prompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain("A replacement review is warranted.", prompt, StringComparison.Ordinal);
        using var evidence = ParseGeneratedEvidence(prompt);
        Assert.Equal(WorkStages.Token(stage), evidence.RootElement.GetProperty("stage").GetString());
        Assert.Equal(haltCause.ToString(), evidence.RootElement.GetProperty("haltCause").GetString());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("known-head-2602", false)]
    public void Unknown_head_or_unavailable_terminal_evidence_preserves_insufficient_facts(
        string? pullRequestHead, bool terminalEvidenceAvailable)
    {
        var context = SourceReviewContext(WorkStage.Review, StoppedWorkHaltCause.MissingVerdict,
            pullRequestHead, terminalEvidenceAvailable);
        var request = SourceReviewRequest(context);
        var prompt = CodexReadinessDecisionAdapter.BuildStoppedWorkPrompt(request, context);

        Assert.Contains("Hold when source facts are insufficient; never force recommend.", prompt,
            StringComparison.Ordinal);
        using var evidence = ParseGeneratedEvidence(prompt);
        Assert.Equal(pullRequestHead ?? "unknown", evidence.RootElement.GetProperty("pullRequestHead").GetString());
        Assert.Equal(terminalEvidenceAvailable.ToString(),
            evidence.RootElement.GetProperty("terminalEvidenceAvailable").GetString());
    }

    [Fact]
    public void Typed_context_serialization_and_sha256_remain_unchanged()
    {
        Assert.Equal("16653333a4cb29f884058e9af8e164125549fc74fed3bb80376358f5275b34eb",
            StoppedWorkAdviceEvidence.Hash(Context));
        Assert.Equal(Request.ContextSha256, StoppedWorkAdviceEvidence.Hash(Context));
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

    private static (StoppedWorkAdviceRequest Request, StoppedWorkAdviceContext Context) SourceReviewEvidence(
        WorkStage stage)
    {
        var context = SourceReviewContext(stage, StoppedWorkHaltCause.MissingVerdict, "known-head-2602", true);
        return (SourceReviewRequest(context), context);
    }

    private static StoppedWorkAdviceContext SourceReviewContext(WorkStage stage,
        StoppedWorkHaltCause haltCause, string? pullRequestHead, bool terminalEvidenceAvailable) => new(
        Repository, "2602-source-review", new("attempt-2602"), stage, ObservedAt, pullRequestHead, null,
        "Succeeded", terminalEvidenceAvailable, "pending", ObservedAt, haltCause, "available", true, "pending");

    private static StoppedWorkAdviceRequest SourceReviewRequest(StoppedWorkAdviceContext context) => new(
        "obligation-2602", context.Repository, context.Tag, context.AttemptId, context.Stage,
        StoppedWorkAdviceEvidence.Hash(context), context.ObservedAt, context.PullRequestHead,
        context.AttemptBaseRevision, "repository-conductor", context.HaltCause, context.RepairAllowance,
        context.VerdictAvailable, context.RequiredChecks, context.State);

    private static JsonDocument ParseGeneratedEvidence(string prompt) =>
        JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]);
}
