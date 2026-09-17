using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.TestSupport;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// Coverage for <see cref="DeliveryVerifier"/> (#1788) — see its own class doc for the contract and
/// spec/baton.md §3 for the "Post-exit delivery check" register entry. Real git against a local bare
/// "origin" (<see cref="TempGitRepository.InitBareRepository"/>'s own doc has why) plus a fake
/// <c>gh</c> stand-in script, mirroring <c>VerifyCommandResolverTests</c>' own "real git, fake pixi/gh
/// binary name" pattern.
/// </summary>
public sealed class DeliveryVerifierTests
{
    [Fact]
    public async Task Pushed_branch_with_an_open_PR_passes()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-a");
        try
        {
            var head = GitRevParseHead(workspace);
            var gh = WriteFakeGh(workspace, $$"""[{"number":42,"headRefOid":"{{head}}"}]""");

            var outcome = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: true, TestContext.Current.CancellationToken, ghProgram: gh);

            Assert.Equal(DeliveryCheckStatus.Passed, outcome.Status);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task A_remote_advanced_since_attempt_start_but_the_PR_head_is_stale_fails_delivery()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-pr-stale");
        try
        {
            var attemptStart = GitRevParseHead(workspace);
            TempGitRepository.CommitAll(workspace, "delivered revision");
            TempGitRepository.Push(workspace, "origin", "feature-pr-stale");
            var deliveredHead = GitRevParseHead(workspace);
            var gh = WriteFakeGh(workspace, $$"""[{"number":2362,"headRefOid":"{{attemptStart}}"}]""");

            var outcome = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: true, TestContext.Current.CancellationToken,
                ghProgram: gh, workspaceHeadShaAtStart: attemptStart);

            Assert.Equal(DeliveryCheckStatus.Failed, outcome.Status);
            Assert.Equal(["pr-not-open"], outcome.FailingMembers);
            Assert.Contains(deliveredHead[..12], outcome.Tail, StringComparison.Ordinal);
            Assert.Contains("PR", outcome.Tail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unchanged_attempt_HEAD_fails_delivery_with_workspace_state(bool leaveWorkspaceDirty)
    {
        var (workspace, origin) = CreatePushedWorkspace(leaveWorkspaceDirty ? "feature-dirty-unchanged" : "feature-clean-unchanged");
        try
        {
            var attemptStart = GitRevParseHead(workspace);
            if (leaveWorkspaceDirty)
            {
                File.AppendAllText(Path.Combine(workspace, "README.md"), "changed\n");
                File.WriteAllText(Path.Combine(workspace, "untracked.txt"), "stray\n");
            }

            var outcome = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: false, TestContext.Current.CancellationToken,
                workspaceHeadShaAtStart: attemptStart);

            Assert.Equal(DeliveryCheckStatus.Failed, outcome.Status);
            Assert.Equal(["revision-not-created"], outcome.FailingMembers);
            Assert.Contains(leaveWorkspaceDirty ? "tracked" : "clean", outcome.Tail,
                StringComparison.OrdinalIgnoreCase);
            if (leaveWorkspaceDirty)
            {
                Assert.Contains("untracked", outcome.Tail, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task A_never_pushed_dirty_unchanged_HEAD_reports_revision_not_created_before_remote_lookup()
    {
        var origin = TempGitRepository.InitBareRepository(TempPath("origin"));
        var workspace = TempPath("workspace");
        try
        {
            Directory.CreateDirectory(workspace);
            TempGitRepository.InitWithEverythingCommitted(workspace);
            TempGitRepository.AddRemote(workspace, "origin", origin);
            TempGitRepository.CreateAndCheckoutBranch(workspace, "never-pushed-unchanged");
            var attemptStart = GitRevParseHead(workspace);
            File.AppendAllText(Path.Combine(workspace, "README.md"), "changed\n");
            File.WriteAllText(Path.Combine(workspace, "untracked.txt"), "stray\n");

            var outcome = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: false, TestContext.Current.CancellationToken,
                workspaceHeadShaAtStart: attemptStart);

            Assert.Equal(DeliveryCheckStatus.Failed, outcome.Status);
            Assert.Equal(["revision-not-created"], outcome.FailingMembers);
            Assert.Contains("tracked", outcome.Tail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("untracked", outcome.Tail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task ExpectPr_false_skips_the_PR_check_even_with_no_gh_available()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-b");
        try
        {
            var outcome = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: false, TestContext.Current.CancellationToken,
                ghProgram: "this-is-not-a-real-gh-binary-12345");

            Assert.Equal(DeliveryCheckStatus.Passed, outcome.Status);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task Delivery_evidence_is_immutable_and_records_the_post_execution_HEAD()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-evidence");
        var outputDirectory = TempPath("evidence-output");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var prHead = GitRevParseHead(workspace);
            var gh = WriteFakeGh(workspace, $$"""[{"number":2309,"headRefOid":"{{prHead}}"}]""");
            var outcome = await DeliveryVerifier.CheckAsync(workspace, expectPr: true, TestContext.Current.CancellationToken, ghProgram: gh);
            await DeliveryVerifier.WriteEvidenceAsync(outputDirectory, workspace, expectPr: true, outcome, TestContext.Current.CancellationToken, ghProgram: gh);
            var evidencePath = Path.Combine(outputDirectory, DeliveryVerifier.DeliveryEvidenceFileName);
            var original = await File.ReadAllTextAsync(evidencePath, TestContext.Current.CancellationToken);

            await DeliveryVerifier.WriteEvidenceAsync(outputDirectory, workspace, expectPr: true,
                new DeliveryCheckOutcome(DeliveryCheckStatus.Failed), TestContext.Current.CancellationToken, ghProgram: gh);

            Assert.Contains(GitRevParseHead(workspace), original, StringComparison.Ordinal);
            Assert.Contains("\"pullRequestNumber\":2309", original, StringComparison.Ordinal);
            Assert.Contains($"\"pullRequestHead\":\"{prHead}\"", original, StringComparison.Ordinal);
            Assert.Equal(original, await File.ReadAllTextAsync(evidencePath, TestContext.Current.CancellationToken));

            TempGitRepository.CommitAll(workspace, "later local change must not rewrite delivery evidence");
            var recovered = await DeliveryVerifier.ReadEvidenceAsync(outputDirectory, TestContext.Current.CancellationToken);
            Assert.Equal(DeliveryCheckStatus.Passed, recovered.Evidence?.Verification);
            Assert.Equal(prHead, recovered.Evidence?.PullRequestHead);
            Assert.NotEqual(GitRevParseHead(workspace), recovered.Evidence?.LocalHead);
        }
        finally
        {
            Cleanup(workspace, origin);
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task A_passed_expected_PR_journal_event_missing_its_PR_head_fails_replay_closed()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-pr-journal-damage");
        try
        {
            var head = GitRevParseHead(workspace);
            var gh = WriteFakeGh(workspace, $$"""[{"number":2309,"headRefOid":"{{head}}"}]""");
            var check = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: true, TestContext.Current.CancellationToken, ghProgram: gh);
            Assert.Equal(DeliveryCheckStatus.Passed, check.Status);
            var observed = await DeliveryVerifier.ObserveAsync(
                workspace, expectPr: true, check, TestContext.Current.CancellationToken, ghProgram: gh);
            var recorded = observed.ToRecordedEvent(new ExecutionId("exec-pr-journal-damage"));
            Assert.Equal(DeliveryCheckStatus.Passed, DeliveryVerifier.ReadRecordedEvidence(recorded).Evidence?.Verification);

            var node = JsonNode.Parse(JsonSerializer.Serialize<FlowEvent>(recorded, FlowEventLogJson.Options))!.AsObject();
            Assert.True(node.Remove("PullRequestHead"));
            var damaged = Assert.IsType<FlowEvent.DeliveryObservationRecorded>(
                JsonSerializer.Deserialize<FlowEvent>(node.ToJsonString(), FlowEventLogJson.Options));
            var reading = DeliveryVerifier.ReadRecordedEvidence(damaged);
            Assert.Null(reading.Evidence);
            Assert.Contains("incomplete", reading.Problem, StringComparison.Ordinal);
        }
        finally { Cleanup(workspace, origin); }
    }

    [Fact]
    public async Task Incomplete_delivery_evidence_never_defaults_to_a_passing_observation()
    {
        var outputDirectory = TempPath("incomplete-evidence");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, DeliveryVerifier.DeliveryEvidenceFileName);
            await File.WriteAllTextAsync(path, """{"observedAt":"2026-09-15T00:00:00Z"}""", TestContext.Current.CancellationToken);

            var incomplete = await DeliveryVerifier.ReadEvidenceAsync(outputDirectory, TestContext.Current.CancellationToken);
            Assert.Null(incomplete.Evidence);
            Assert.Contains("incomplete", incomplete.Problem, StringComparison.Ordinal);

            await File.WriteAllTextAsync(path, """{"observedAt":"2026-09-15T00:00:00Z","verification":"Failed"}""", TestContext.Current.CancellationToken);
            var failed = await DeliveryVerifier.ReadEvidenceAsync(outputDirectory, TestContext.Current.CancellationToken);
            Assert.Equal(DeliveryCheckStatus.Failed, failed.Evidence?.Verification);

            await File.WriteAllTextAsync(path, """{"observedAt":"not-a-time","verification":"Passed","localHead":"a","branch":"lane","remoteHead":"a"}""", TestContext.Current.CancellationToken);
            var malformedTime = await DeliveryVerifier.ReadEvidenceAsync(outputDirectory, TestContext.Current.CancellationToken);
            Assert.Null(malformedTime.Evidence);
            Assert.Contains("incomplete", malformedTime.Problem, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task Unpushed_local_commits_on_top_of_a_pushed_branch_fail_branch_not_pushed()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-c");
        try
        {
            // A local commit made AFTER the push above -- origin has the branch, but HEAD has moved
            // past it, exactly #1788's own measured defect (2/3 commits ahead of origin, no PR).
            TempGitRepository.CommitAll(workspace, "one more change, never pushed");

            var outcome = await DeliveryVerifier.CheckAsync(workspace, expectPr: false, TestContext.Current.CancellationToken);

            Assert.Equal(DeliveryCheckStatus.Failed, outcome.Status);
            Assert.Equal(["branch-not-pushed"], outcome.FailingMembers);
            Assert.Contains("branch-not-pushed", outcome.Tail, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task A_branch_that_was_never_pushed_at_all_fails_branch_not_pushed()
    {
        // The `git ls-remote --exit-code` arm (exit 2, ref absent) rather than the `merge-base` arm
        // above -- the loudest version of #1788's defect: nothing was ever pushed, not merely a few
        // trailing commits.
        var origin = TempGitRepository.InitBareRepository(TempPath("origin"));
        var workspace = TempPath("workspace");
        try
        {
            Directory.CreateDirectory(workspace);
            TempGitRepository.InitWithEverythingCommitted(workspace);
            TempGitRepository.AddRemote(workspace, "origin", origin);
            TempGitRepository.CreateAndCheckoutBranch(workspace, "never-pushed");
            TempGitRepository.CommitAll(workspace, "local only");

            var outcome = await DeliveryVerifier.CheckAsync(workspace, expectPr: false, TestContext.Current.CancellationToken);

            Assert.Equal(DeliveryCheckStatus.Failed, outcome.Status);
            Assert.Equal(["branch-not-pushed"], outcome.FailingMembers);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    /// <summary>
    /// The discriminating control for the explicit-refspec fetch. Real git DOES opportunistically update
    /// <c>refs/remotes/origin/&lt;branch&gt;</c> on a plain <c>git fetch origin &lt;branch&gt;</c> when the
    /// remote's own <c>remote.origin.fetch</c> setting already covers that ref (measured; an earlier
    /// draft of this test/its docs claimed otherwise) — so this test removes that configuration entirely
    /// (<c>git config --unset-all remote.origin.fetch</c>) before also deleting the locally cached ref,
    /// reproducing a workspace with NO way to recover it except via an explicit refspec. Under the bare
    /// fetch form this would leave the ref absent and <c>merge-base --is-ancestor HEAD
    /// origin/&lt;branch&gt;</c> unable to resolve it (a non-0/1 exit this class reads as
    /// <see cref="DeliveryCheckStatus.NotRun"/>, never a fabricated pass or failure). Only the
    /// <c>+refs/heads/&lt;branch&gt;:refs/remotes/origin/&lt;branch&gt;</c> form recreates the ref
    /// regardless, and lets the check resolve to <see cref="DeliveryCheckStatus.Passed"/>.
    /// </summary>
    [Fact]
    public async Task A_workspace_with_no_locally_cached_tracking_ref_still_resolves_via_the_refspec_fetch()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-g");
        try
        {
            RunGit(workspace, "config", "--unset-all", "remote.origin.fetch");
            RunGit(workspace, "update-ref", "-d", "refs/remotes/origin/feature-g");

            var outcome = await DeliveryVerifier.CheckAsync(workspace, expectPr: false, TestContext.Current.CancellationToken);

            Assert.Equal(DeliveryCheckStatus.Passed, outcome.Status);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    /// <summary>
    /// The <c>--heads</c> scoping's own control (#1788 review): a TAG on origin sharing the branch's
    /// name must not make <c>ls-remote --exit-code</c> read as "the branch exists" — spec/baton.md §3
    /// states why an unscoped query would misread this as <see cref="DeliveryCheckStatus.NotRun"/>.
    /// </summary>
    [Fact]
    public async Task A_same_named_tag_on_origin_does_not_mask_a_branch_that_was_never_pushed()
    {
        var origin = TempGitRepository.InitBareRepository(TempPath("origin"));
        var workspace = TempPath("workspace");
        try
        {
            Directory.CreateDirectory(workspace);
            TempGitRepository.InitWithEverythingCommitted(workspace);
            TempGitRepository.AddRemote(workspace, "origin", origin);
            RunGit(workspace, "tag", "ghost");
            RunGit(workspace, "push", "origin", "ghost");
            TempGitRepository.CreateAndCheckoutBranch(workspace, "ghost");
            TempGitRepository.CommitAll(workspace, "local only, never pushed as a branch");

            var outcome = await DeliveryVerifier.CheckAsync(workspace, expectPr: false, TestContext.Current.CancellationToken);

            Assert.Equal(DeliveryCheckStatus.Failed, outcome.Status);
            Assert.Equal(["branch-not-pushed"], outcome.FailingMembers);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task Pushed_branch_with_no_open_PR_fails_pr_not_open()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-d");
        try
        {
            var gh = WriteFakeGh(workspace, "[]");

            var outcome = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: true, TestContext.Current.CancellationToken, ghProgram: gh);

            Assert.Equal(DeliveryCheckStatus.Failed, outcome.Status);
            Assert.Equal(["pr-not-open"], outcome.FailingMembers);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    /// <summary>
    /// #1788 review: an exit-0 <c>gh pr list</c> whose stdout does not parse as the JSON array it always
    /// emits on success must never fabricate "a PR exists" (nor "no PR" — the class doc's own refused
    /// fabrication in the other direction). A wrapper script or a truncated pipe is the realistic cause;
    /// this fixture just returns plain unparseable text with exit 0.
    /// </summary>
    [Fact]
    public async Task Unparseable_gh_output_on_a_successful_exit_reports_NotRun_rather_than_a_pass()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-h");
        try
        {
            var path = Path.Combine(workspace, $"fake-gh-{Guid.NewGuid():N}.cmd");
            File.WriteAllText(path, "@echo off\necho not-json-at-all\nexit /b 0\n");

            var outcome = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: true, TestContext.Current.CancellationToken, ghProgram: path);

            Assert.Equal(DeliveryCheckStatus.NotRun, outcome.Status);
            Assert.NotNull(outcome.NotRunReason);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task A_cancellation_requested_before_the_check_starts_reports_Cancelled()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-i");
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var outcome = await DeliveryVerifier.CheckAsync(workspace, expectPr: false, cts.Token);

            Assert.Equal(DeliveryCheckStatus.Cancelled, outcome.Status);
            Assert.Null(outcome.FailingMembers);
            Assert.Null(outcome.NotRunReason);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task A_missing_gh_binary_reports_NotRun_rather_than_a_pass_or_a_failure()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-e");
        try
        {
            var outcome = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: true, TestContext.Current.CancellationToken,
                ghProgram: "this-is-not-a-real-gh-binary-12345");

            Assert.Equal(DeliveryCheckStatus.NotRun, outcome.Status);
            Assert.NotNull(outcome.NotRunReason);
            Assert.Contains("gh", outcome.NotRunReason, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task A_missing_git_binary_reports_NotRun_rather_than_a_pass_or_a_failure()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-f");
        try
        {
            var outcome = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: false, TestContext.Current.CancellationToken,
                gitProgram: "this-is-not-a-real-git-binary-12345");

            Assert.Equal(DeliveryCheckStatus.NotRun, outcome.Status);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task A_detached_HEAD_fails_branch_not_pushed_rather_than_NotRun()
    {
        // #1788: checking out a raw commit (`git checkout <sha>`) leaves git DETACHED -- confirmed
        // directly against real git for this issue; spec/baton.md §3 states why that reads as Failed.
        var origin = TempGitRepository.InitBareRepository(TempPath("origin"));
        var workspace = TempPath("workspace");
        try
        {
            Directory.CreateDirectory(workspace);
            TempGitRepository.InitWithEverythingCommitted(workspace);
            TempGitRepository.AddRemote(workspace, "origin", origin);
            var sha = GitRevParseHead(workspace);
            GitCheckout(workspace, sha);

            var outcome = await DeliveryVerifier.CheckAsync(workspace, expectPr: false, TestContext.Current.CancellationToken);

            Assert.Equal(DeliveryCheckStatus.Failed, outcome.Status);
            Assert.Equal(["branch-not-pushed"], outcome.FailingMembers);
            Assert.Contains("detached", outcome.Tail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task No_working_directory_reports_NotRun()
    {
        var outcome = await DeliveryVerifier.CheckAsync(null, expectPr: true, TestContext.Current.CancellationToken);

        Assert.Equal(DeliveryCheckStatus.NotRun, outcome.Status);
    }

    // ---- fixtures ----

    private static (string Workspace, string Origin) CreatePushedWorkspace(string branch)
    {
        var origin = TempGitRepository.InitBareRepository(TempPath("origin"));
        var workspace = TempPath("workspace");
        Directory.CreateDirectory(workspace);
        TempGitRepository.InitWithEverythingCommitted(workspace);
        TempGitRepository.AddRemote(workspace, "origin", origin);
        TempGitRepository.CreateAndCheckoutBranch(workspace, branch);
        TempGitRepository.CommitAll(workspace, "lane work");
        TempGitRepository.Push(workspace, "origin", branch);
        return (workspace, origin);
    }

    private static string TempPath(string label) => Path.Combine(Path.GetTempPath(), $"dv-{label}-{Guid.NewGuid():N}");

    private static void Cleanup(string workspace, string origin)
    {
        DirectoryCleanup.DeleteRecursively(workspace);
        DirectoryCleanup.DeleteRecursively(origin);
    }

    // #1978: the same `gh pr list --head <branch> --json number` question, now also asked on its own so
    // the timeout summary can NAME the PR rather than only learn that one exists. Real git for the
    // branch resolution, the same fake `gh` the arms above use for the answer.

    [Fact]
    public async Task ReadOpenPullRequestAsync_names_the_open_PR_for_the_workspace_branch()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-pr-named");
        try
        {
            var prHead = GitRevParseHead(workspace);
            var gh = WriteFakeGh(workspace, $$"""[{"number":1974,"headRefOid":"{{prHead}}"}]""");

            var reading = await DeliveryVerifier.ReadOpenPullRequestAsync(
                workspace, TestContext.Current.CancellationToken, ghProgram: gh);

            Assert.True(reading.AnyOpen);
            Assert.Equal(1974, reading.Number);
            Assert.Equal(prHead, reading.Head);
            Assert.Null(reading.NotRunReason);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task An_arrest_after_PR_update_records_the_exact_PR_head_without_certifying_delivery()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-pr-updated-before-arrest");
        try
        {
            var head = GitRevParseHead(workspace);
            var gh = WriteFakeGh(workspace, $$"""[{"number":2309,"headRefOid":"{{head}}"}]""");
            var observation = await DeliveryVerifier.ObserveAsync(workspace, expectPr: true,
                new DeliveryCheckOutcome(DeliveryCheckStatus.NotRun,
                    NotRunReason: "delivery assertion not run: worker was arrested before a clean exit"),
                TestContext.Current.CancellationToken, ghProgram: gh);
            var recorded = observation.ToRecordedEvent(new Baton.Domain.ExecutionId("exec-pr-updated-arrest"));
            var reading = DeliveryVerifier.ReadRecordedEvidence(recorded);

            Assert.NotNull(reading.Evidence);
            Assert.Equal(head, recorded.LocalHead);
            Assert.Equal(head, recorded.RemoteHead);
            Assert.Equal(2309, recorded.PullRequestNumber);
            Assert.Equal(head, recorded.PullRequestHead);
            Assert.Equal("NotRun", recorded.Verification);
            Assert.Equal(DeliveryCheckStatus.NotRun, reading.Evidence!.ToOutcome().Status);
        }
        finally { Cleanup(workspace, origin); }
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("head-changed")]
    public async Task An_expected_PR_changed_after_a_passing_check_cannot_keep_the_old_delivery_pass(string change)
    {
        var (workspace, origin) = CreatePushedWorkspace($"feature-pr-race-{change}");
        try
        {
            var head = GitRevParseHead(workspace);
            var first = $$"""[{"number":2309,"headRefOid":"{{head}}"}]""";
            var second = change == "closed"
                ? "[]"
                : $$"""[{"number":2309,"headRefOid":"{{new string('b', 40)}}"}]""";
            var (gh, marker) = WriteSequentialFakeGh(workspace, first, second);

            var check = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: true, TestContext.Current.CancellationToken, ghProgram: gh);
            Assert.Equal(DeliveryCheckStatus.Passed, check.Status); // The first PR reading really admitted.

            var final = await DeliveryVerifier.ObserveAsync(
                workspace, expectPr: true, check, TestContext.Current.CancellationToken, ghProgram: gh);
            Assert.Equal(2, File.ReadAllLines(marker).Length); // Both fake-gh child invocations entered.
            Assert.Equal(head, final.LocalHead);
            Assert.Equal(head, final.RemoteHead); // Only the PR changed; Git provenance stayed valid.
            Assert.Equal(DeliveryCheckStatus.Failed, final.Verification);
            Assert.Equal(["pr-not-open"], final.FailingMembers);
            Assert.Equal(DeliveryCheckStatus.Failed,
                DeliveryVerifier.ReadRecordedEvidence(final.ToRecordedEvent(new Baton.Domain.ExecutionId("exec-pr-race")))
                    .Evidence?.ToOutcome().Status);
        }
        finally { Cleanup(workspace, origin); }
    }

    [Fact]
    public async Task An_unreadable_final_expected_PR_lookup_cannot_inherit_an_earlier_pass()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-pr-race-unreadable");
        try
        {
            var head = GitRevParseHead(workspace);
            var first = $$"""[{"number":2309,"headRefOid":"{{head}}"}]""";
            var (gh, marker) = WriteSequentialFakeGh(workspace, first, "not-json");
            var check = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: true, TestContext.Current.CancellationToken, ghProgram: gh);
            Assert.Equal(DeliveryCheckStatus.Passed, check.Status);

            var final = await DeliveryVerifier.ObserveAsync(
                workspace, expectPr: true, check, TestContext.Current.CancellationToken, ghProgram: gh);
            Assert.Equal(2, File.ReadAllLines(marker).Length);
            Assert.Equal(DeliveryCheckStatus.NotRun, final.Verification);
            Assert.Contains("final expected PR observation", final.VerificationReason, StringComparison.Ordinal);
            Assert.Equal(DeliveryCheckStatus.NotRun,
                DeliveryVerifier.ReadRecordedEvidence(final.ToRecordedEvent(new Baton.Domain.ExecutionId("exec-pr-unreadable")))
                    .Evidence?.ToOutcome().Status);
        }
        finally { Cleanup(workspace, origin); }
    }

    [Theory]
    [InlineData("missing-head")]
    [InlineData("unnameable")]
    [InlineData("malformed-head")]
    public async Task An_expected_PR_without_a_checked_exact_head_cannot_certify_a_later_head(string firstReading)
    {
        var (workspace, origin) = CreatePushedWorkspace($"feature-pr-first-unknown-{firstReading}");
        try
        {
            var head = GitRevParseHead(workspace);
            var first = firstReading switch
            {
                "missing-head" => """[{"number":2309}]""",
                "malformed-head" => """[{"number":2309,"headRefOid":"not-a-sha"}]""",
                _ => "[{}]",
            };
            var changed = $$"""[{"number":2309,"headRefOid":"{{new string('b', 40)}}"}]""";
            var (gh, marker) = WriteSequentialFakeGh(workspace, first, changed);

            var check = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: true, TestContext.Current.CancellationToken, ghProgram: gh);
            var final = await DeliveryVerifier.ObserveAsync(
                workspace, expectPr: true, check, TestContext.Current.CancellationToken, ghProgram: gh);
            Assert.Equal(2, File.ReadAllLines(marker).Length); // Both child PR probes actually ran.
            Assert.Equal(head, final.LocalHead);
            Assert.Equal(head, final.RemoteHead);
            Assert.Equal(DeliveryCheckStatus.NotRun, check.Status);
            Assert.Contains("exact PR", check.NotRunReason, StringComparison.Ordinal);
            Assert.Equal(DeliveryCheckStatus.NotRun, final.Verification);
            Assert.Equal(DeliveryCheckStatus.NotRun,
                DeliveryVerifier.ReadRecordedEvidence(final.ToRecordedEvent(new Baton.Domain.ExecutionId("exec-pr-first-unknown")))
                    .Evidence?.ToOutcome().Status);
        }
        finally { Cleanup(workspace, origin); }
    }

    [Fact]
    public async Task ReadOpenPullRequestAsync_reports_no_open_PR_without_fabricating_a_number()
    {
        // The discriminating control for the arm above: same workspace, same spawn, an empty array. The
        // consumer must be able to tell "none is open" from "the question went unanswered" -- which is
        // the arm below -- because only the first is evidence.
        var (workspace, origin) = CreatePushedWorkspace("feature-pr-none");
        try
        {
            var gh = WriteFakeGh(workspace, "[]");

            var reading = await DeliveryVerifier.ReadOpenPullRequestAsync(
                workspace, TestContext.Current.CancellationToken, ghProgram: gh);

            Assert.False(reading.AnyOpen);
            Assert.Null(reading.Number);
            Assert.Null(reading.NotRunReason);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task ReadOpenPullRequestAsync_reports_NotRun_when_gh_is_not_available()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-pr-nogh");
        try
        {
            var reading = await DeliveryVerifier.ReadOpenPullRequestAsync(
                workspace, TestContext.Current.CancellationToken,
                ghProgram: "this-is-not-a-real-gh-binary-12345");

            // Never `false`: an unanswered question read as "no PR is open" is exactly the fabricated
            // absence the tri-state exists to prevent.
            Assert.Null(reading.AnyOpen);
            Assert.Null(reading.Number);
            Assert.NotNull(reading.NotRunReason);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    private static string WriteFakeGh(string directory, string jsonOutput)
    {
        var path = Path.Combine(directory, $"fake-gh-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, $"@echo off\necho {jsonOutput}\nexit /b 0\n");
        return path;
    }

    private static (string Script, string Marker) WriteSequentialFakeGh(string directory, string first, string second)
    {
        var marker = Path.Combine(directory, $"fake-gh-entered-{Guid.NewGuid():N}.txt");
        var script = Path.Combine(directory, $"fake-gh-sequence-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(script,
            $"@echo off\nif exist \"{marker}\" goto second\necho first>\"{marker}\"\necho {first}\nexit /b 0\n"
            + $":second\necho second>>\"{marker}\"\necho {second}\nexit /b 0\n");
        return (script, marker);
    }

    private static string GitRevParseHead(string workspace) =>
        RunGit(workspace, "rev-parse", "HEAD").Trim();

    private static void GitCheckout(string workspace, string commitish) => RunGit(workspace, "checkout", commitish);

    private static string RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not spawn git {string.Join(' ', args)}.");
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} exited {process.ExitCode}: {process.StandardError.ReadToEnd()}");
        }

        return stdout;
    }
}
