using Baton.Cli.Tests.TestSupport;
using System.Security.Cryptography;
using System.Text;
using Baton.CrashTestHost;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using Baton.Vendors;

namespace Baton.Cli.Tests;

[Collection(SerializedEnvironmentCollection.Name)]
public sealed class OriginatingPullRequestVerifierTests
{
    private const string Head = "0123456789abcdef0123456789abcdef01234567";
    private const string PreservedHead = "fedcba9876543210fedcba9876543210fedcba98";
    private const string RecoveryAttemptId = "2178continuationattempt000000000000";
    private static readonly GhPullRequestCreateIdentity Identity = new("aer-works/baton", "2178-lane");

    [Theory]
    [InlineData("aer-works/baton#2304", "aer-works/baton", 2304)]
    [InlineData("AER-WORKS/BATON#1", "aer-works/baton", 1)]
    public void A_repository_qualified_reference_is_parsed_canonically(
        string raw, string expectedRepository, int expectedNumber)
    {
        var parsed = OriginatingPullRequestVerifier.ParseReference(raw);

        Assert.Equal(expectedRepository, parsed.Repository);
        Assert.Equal(expectedNumber, parsed.Number);
    }

    [Theory]
    [InlineData("2304")]
    [InlineData("aer-works/baton")]
    [InlineData("aer-works/baton#0")]
    [InlineData("aer-works/baton#nope")]
    [InlineData("https://github.com/aer-works/baton#2304")]
    public void An_ambiguous_or_malformed_reference_is_refused(string raw) =>
        Assert.Throws<CliArgumentException>(() => OriginatingPullRequestVerifier.ParseReference(raw));

    [Fact]
    public void A_queue_repository_identity_is_narrowed_to_the_GitHub_owner_repo_reference()
    {
        Assert.Equal(
            "aer-works/baton#2304",
            OriginatingPullRequestVerifier.CanonicalReference("github.com/AER-WORKS/BATON", 2304));
        Assert.Throws<CliArgumentException>(() =>
            OriginatingPullRequestVerifier.CanonicalReference("gitlab.com/aer-works/baton", 2304));
    }

    [Fact]
    public void An_open_PR_matching_the_verified_repository_branch_and_launch_HEAD_is_bound()
    {
        var ownership = OriginatingPullRequestVerifier.ValidateResponse(
            "aer-works/baton", 2304, Identity, Head, 0,
            $$"""{"state":"OPEN","headRefName":"2178-lane","headRefOid":"{{Head.ToUpperInvariant()}}"}""");

        Assert.Equal(new OriginatingPullRequestOwnership("aer-works/baton", 2304, "2178-lane", Head), ownership);
    }

    [Fact]
    public void A_recovery_PR_may_remain_at_the_retained_head_when_workspace_HEAD_is_a_descendant()
    {
        var ownership = OriginatingPullRequestVerifier.ValidateResponse(
            "aer-works/baton", 2304, Identity, Head, 0,
            $$"""{"state":"OPEN","headRefName":"2178-lane","headRefOid":"{{PreservedHead}}"}""",
            PreservedHead);

        Assert.Equal(new OriginatingPullRequestOwnership("aer-works/baton", 2304, "2178-lane", Head), ownership);
    }

    [Fact]
    public void A_malformed_retained_head_is_refused()
    {
        Assert.Throws<CliArgumentException>(() => OriginatingPullRequestVerifier.ValidateResponse(
            "aer-works/baton", 2304, Identity, Head, 0,
            $$"""{"state":"OPEN","headRefName":"2178-lane","headRefOid":"{{PreservedHead}}"}""",
            "not-a-sha"));
    }

    [Fact]
    public void A_direct_caller_cannot_forge_recovery_identity_without_a_durable_queue_row()
    {
        var options = RecoveryOptions();

        Assert.Throws<CliArgumentException>(() => OriginatingPullRequestVerifier.ValidateRecoveryEvidence(
            options, Path.GetTempPath(), new QueueSnapshot([])));
    }

    [Fact]
    public void A_queued_recovery_attempt_is_refused()
    {
        var workspace = Path.GetTempPath();
        var item = new QueueItem
        {
            Tag = "2178-lane",
            Role = "implement",
            Workspace = workspace,
            SpecFile = "2178.md",
            State = QueueItemState.Queued,
            Stage = WorkStage.Continue,
            Repository = "github.com/aer-works/baton",
            PullRequest = 2304,
            Branch = "2178-lane",
            ExpectedOriginatingPullRequestHead = PreservedHead,
            AttemptId = new FleetAttemptId(RecoveryAttemptId),
            AttemptEnvelope = new QueueAttemptEnvelope(
                new FleetAttemptId(RecoveryAttemptId), null, "2178", null, 2304, WorkStage.Continue,
                "implement", null, null, null, [], null, null, "admitted", null, null, null,
                DateTimeOffset.UnixEpoch),
        };

        Assert.Throws<CliArgumentException>(() => OriginatingPullRequestVerifier.ValidateRecoveryEvidence(
            RecoveryOptions(), workspace, new QueueSnapshot([item])));
    }

    [Fact]
    public async Task A_launched_recovery_claims_once_while_its_assigned_room_is_still_absent()
    {
        using var home = new IsolatedBatonHome();
        var workspace = Directory.CreateDirectory(Path.Combine(home.Path, "workspace")).FullName;
        var room = Path.Combine(home.Path, "rooms", "queue-2178-lane-future");
        var attemptId = new FleetAttemptId(RecoveryAttemptId);
        const string proof = "one-shot-proof";
        var item = new QueueItem
        {
            Tag = "2178-lane",
            Role = "implement",
            Workspace = workspace,
            SpecFile = "2178.md",
            State = QueueItemState.Launched,
            Stage = WorkStage.Continue,
            Repository = "github.com/aer-works/baton",
            PullRequest = 2304,
            Branch = "2178-lane",
            RoomDirectory = room,
            ExpectedOriginatingPullRequestHead = PreservedHead,
            OriginatingPullRequestRecoveryProofDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proof))),
            AttemptId = attemptId,
            AttemptEnvelope = new QueueAttemptEnvelope(
                attemptId, null, "2178", null, 2304, WorkStage.Continue,
                "implement", null, null, null, [], null, null, "admitted", room,
                BatonPaths.RecordKey(room), null, DateTimeOffset.UnixEpoch),
        };
        await QueueStore.MutateAsync(
            BatonPaths.QueueFile, snapshot => snapshot with { Items = [item] },
            TestContext.Current.CancellationToken);
        var options = RecoveryOptions(room);

        Assert.False(Directory.Exists(room));
        var originalInput = Console.In;
        try
        {
            Console.SetIn(new StringReader(proof + Environment.NewLine));
            Assert.Equal(
                PreservedHead,
                await OriginatingPullRequestVerifier.ResolveRecoveryExpectedHeadAsync(
                    options, workspace, TestContext.Current.CancellationToken));
            Assert.Null(await OriginatingPullRequestVerifier.ResolveRecoveryExpectedHeadAsync(
                options, workspace, TestContext.Current.CancellationToken));
        }
        finally
        {
            Console.SetIn(originalInput);
        }

        var claimed = Assert.Single((await QueueStore.LoadAsync(
            BatonPaths.QueueFile, TestContext.Current.CancellationToken)).Items);
        Assert.Equal(attemptId, claimed.OriginatingPullRequestRecoveryClaim);
        Assert.Null(claimed.OriginatingPullRequestRecoveryProofDigest);
        Assert.False(Directory.Exists(room));
    }

    [Fact]
    public void A_future_path_compares_lexically_but_an_existing_link_ancestor_is_refused()
    {
        var root = Directory.CreateTempSubdirectory("baton-origin-room-").FullName;
        try
        {
            var rooms = Directory.CreateDirectory(Path.Combine(root, "rooms")).FullName;
            var future = Path.Combine(rooms, "queue-future");
            Assert.True(OriginatingPullRequestVerifier.SameWorkspace(
                future, Path.Combine(rooms, ".", "queue-future")));
            Assert.Equal(
                OperatingSystem.IsWindows(),
                OriginatingPullRequestVerifier.SameWorkspace(future, future.ToUpperInvariant()));

            var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
            var alias = Path.Combine(root, "rooms-alias");
            try
            {
                Directory.CreateSymbolicLink(alias, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            var aliasedFuture = Path.Combine(alias, "queue-future");
            Assert.False(OriginatingPullRequestVerifier.SameWorkspace(aliasedFuture, aliasedFuture));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Theory]
    [InlineData("CLOSED", "2178-lane", Head)]
    [InlineData("MERGED", "2178-lane", Head)]
    [InlineData("OPEN", "another-branch", Head)]
    [InlineData("OPEN", "2178-lane", "ffffffffffffffffffffffffffffffffffffffff")]
    public void A_closed_merged_or_workspace_mismatched_PR_is_refused(
        string state, string branch, string head)
    {
        var response = $$"""{"state":"{{state}}","headRefName":"{{branch}}","headRefOid":"{{head}}"}""";

        Assert.Throws<CliArgumentException>(() => OriginatingPullRequestVerifier.ValidateResponse(
            "aer-works/baton", 2304, Identity, Head, 0, response));
    }

    [Theory]
    [InlineData(1, "{}")]
    [InlineData(0, "not json")]
    [InlineData(0, "{}")]
    public void An_unreadable_or_malformed_forge_response_is_refused(int exitCode, string response) =>
        Assert.Throws<CliArgumentException>(() => OriginatingPullRequestVerifier.ValidateResponse(
            "aer-works/baton", 2304, Identity, Head, exitCode, response));

    [Fact]
    public void A_queue_recorded_branch_must_match_before_the_forge_is_spawned()
    {
        OriginatingPullRequestVerifier.ValidateExpectedBranch(Identity, "2178-lane");
        Assert.Throws<CliArgumentException>(() =>
            OriginatingPullRequestVerifier.ValidateExpectedBranch(Identity, "worker-changed-branch"));
    }

    [Fact]
    public void Gh_resolution_skips_a_workspace_local_executable_and_requires_an_outside_absolute_one()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-origin-gh-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var outside = Path.Combine(root, "trusted-bin");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(workspace, "gh.exe"), "worker fake");
        File.WriteAllText(Path.Combine(outside, "gh.exe"), "conductor fake");
        try
        {
            var search = string.Join(Path.PathSeparator, workspace, outside);
            Assert.Equal(
                Path.Combine(outside, "gh.exe"),
                OriginatingPullRequestVerifier.ResolveExecutable(workspace, search, isWindows: true),
                ignoreCase: true);
            Assert.Throws<CliArgumentException>(() =>
                OriginatingPullRequestVerifier.ResolveExecutable(workspace, workspace, isWindows: true));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Verification_spawns_the_external_gh_and_never_the_workspace_fake()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-origin-gh-spawn-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var outside = Path.Combine(root, "trusted-bin");
        var marker = Path.Combine(root, "launched-gh.txt");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        CopyHermeticProbeHost(outside);
        var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var appHost = Path.Combine(outside, "Baton.CrashTestHost" + suffix);
        var externalGit = Path.Combine(outside, "git" + suffix);
        var externalGh = Path.Combine(outside, "gh" + suffix);
        File.Copy(appHost, externalGit);
        File.Copy(appHost, externalGh);
        MakeExecutable(externalGit);
        MakeExecutable(externalGh);

        // If VerifyAsync ever regresses to Process.Start("gh"), PATH selects this invalid worker
        // executable first: the spawn fails and the external marker never lands.
        var workspaceGh = Path.Combine(workspace, "gh" + suffix);
        File.WriteAllText(
            workspaceGh,
            OperatingSystem.IsWindows()
                ? "worker-controlled fake; must not launch"
                : "#!/bin/sh\nexit 99\n");
        MakeExecutable(workspaceGh);

        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldMarker = Environment.GetEnvironmentVariable("BATON_CRASH_TEST_GH_MARKER");
        try
        {
            Environment.SetEnvironmentVariable(
                "PATH", string.Join(Path.PathSeparator, workspace, outside, oldPath));
            Environment.SetEnvironmentVariable("BATON_CRASH_TEST_GH_MARKER", marker);

            var ownership = await OriginatingPullRequestVerifier.VerifyAsync(
                "aer-works/baton#2304", workspace, TestContext.Current.CancellationToken,
                "2190-verified-pr-ownership");

            Assert.Equal(new OriginatingPullRequestOwnership(
                "aer-works/baton", 2304, "2190-verified-pr-ownership", Head), ownership);
            Assert.Equal(
                Path.GetFullPath(externalGh),
                Path.GetFullPath(await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken)),
                ignoreCase: OperatingSystem.IsWindows());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("BATON_CRASH_TEST_GH_MARKER", oldMarker);
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    private static void CopyHermeticProbeHost(string destination)
    {
        var sourceDirectory = Path.GetDirectoryName(typeof(Scenarios).Assembly.Location)!;
        const string hostPrefix = "Baton.CrashTestHost";
        foreach (var source in Directory.EnumerateFiles(sourceDirectory))
        {
            var name = Path.GetFileName(source);
            if (name.StartsWith(hostPrefix, StringComparison.Ordinal)
                || name.Equals("Baton.dll", StringComparison.Ordinal))
            {
                File.Copy(source, Path.Combine(destination, name));
            }
        }
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
        }
    }

    private static DispatchOptions RecoveryOptions(string room = "room") => new(
        "implement", "2178.md", room,
        OriginatingPullRequest: "aer-works/baton#2304",
        OriginatingPullRequestBranch: "2178-lane");
}
