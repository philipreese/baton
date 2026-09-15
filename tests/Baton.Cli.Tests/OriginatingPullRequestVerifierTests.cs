using Baton.Vendors;

namespace Baton.Cli.Tests;

public sealed class OriginatingPullRequestVerifierTests
{
    private const string Head = "0123456789abcdef0123456789abcdef01234567";
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
}
