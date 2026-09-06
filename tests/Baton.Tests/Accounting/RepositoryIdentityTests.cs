using Baton.Accounting;

namespace Baton.Tests.Accounting;

/// <summary>
/// The claim under test for <see cref="RepositoryIdentity"/> (that type's own remarks say what it is
/// for): convergence — every spelling of one repository's remote, and every worktree of it, must
/// produce ONE identity — plus injectivity of the on-disk slug, which the string derivation alone does
/// not give.
/// </summary>
public sealed class RepositoryIdentityTests
{
    private const string Canonical = "github.com/aer-works/baton";

    [Theory]
    [InlineData("https://github.com/aer-works/baton.git")]
    [InlineData("https://github.com/aer-works/baton")]
    [InlineData("https://github.com/aer-works/baton/")]
    [InlineData("https://GitHub.com/AER-Works/Baton.git")]
    [InlineData("https://someone@github.com/aer-works/baton.git")]
    [InlineData("git@github.com:aer-works/baton.git")]
    [InlineData("git@github.com:aer-works/baton")]
    [InlineData("ssh://git@github.com/aer-works/baton.git")]
    [InlineData("ssh://git@github.com:22/aer-works/baton/")]
    public void Every_remote_spelling_of_one_repository_converges_on_one_identity(string originUrl)
    {
        var identity = RepositoryIdentity.From(originUrl, gitCommonDirectoryPath: null);

        Assert.NotNull(identity);
        Assert.Equal(Canonical, identity.Value);
    }

    [Fact]
    public void A_different_repository_on_the_same_host_is_a_different_identity()
    {
        // The control for the theory above: if the derivation collapsed everything to its host, every
        // arm there would pass while the ledger merged two projects into one file.
        var mine = RepositoryIdentity.From("https://github.com/aer-works/baton.git", null);
        var other = RepositoryIdentity.From("https://github.com/aer-works/other.git", null);

        Assert.NotEqual(mine!.Value, other!.Value);
        Assert.NotEqual(mine.FileSlug, other.FileSlug);
    }

    [Fact]
    public void The_origin_remote_wins_over_the_common_directory()
    {
        // Two checkouts of one repository report the same origin and DIFFERENT `.git` locations. The
        // remote is what has to decide, or the ledger splits per checkout.
        var main = RepositoryIdentity.From("https://github.com/aer-works/baton.git", @"C:\src\baton\.git");
        var worktree = RepositoryIdentity.From("https://github.com/aer-works/baton.git", @"C:\src\w1849\.git");

        Assert.Equal(main!.Value, worktree!.Value);
        Assert.Equal(Canonical, main.Value);
    }

    [Fact]
    public void With_no_remote_the_shared_common_directory_is_the_identity_and_spelling_does_not_split_it()
    {
        // `git rev-parse --git-common-dir` answers with the MAIN checkout's .git from inside a linked
        // worktree, so a remote-less repository's worktrees still converge -- provided separator and
        // case spellings normalize, which is what this pins.
        var a = RepositoryIdentity.From(originUrl: null, @"C:\src\baton\.git");
        var b = RepositoryIdentity.From(originUrl: "   ", @"C:/SRC/Baton/.git/");

        Assert.NotNull(a);
        Assert.Equal(a.Value, b!.Value);
        Assert.StartsWith("gitdir:", a.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_common_directory_identity_is_never_confused_with_a_remote_identity()
    {
        var fromRemote = RepositoryIdentity.From("https://github.com/aer-works/baton.git", null);
        var fromPath = RepositoryIdentity.From(null, @"C:\src\github.com\aer-works\baton");

        Assert.NotEqual(fromRemote!.Value, fromPath!.Value);
    }

    [Fact]
    public void Neither_a_remote_nor_a_common_directory_yields_no_identity_at_all()
    {
        // Null rather than a guess -- RepositoryIdentity.From's own doc states what inventing an
        // identity for a directory git knows nothing about would cost.
        Assert.Null(RepositoryIdentity.From(null, null));
        Assert.Null(RepositoryIdentity.From("   ", "  "));
    }

    [Fact]
    public void A_windows_path_handed_in_as_a_remote_is_not_read_as_an_scp_remote()
    {
        // `C:\src\baton` has a colon in scp position. Misreading it would make host "C" and produce a
        // silently wrong identity rather than falling through to the common-dir derivation.
        var identity = RepositoryIdentity.From(@"C:\src\baton", @"C:\src\baton\.git");

        Assert.NotNull(identity);
        Assert.StartsWith("gitdir:", identity.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_slug_is_filesystem_safe_and_still_distinguishes_identities_that_sanitize_alike()
    {
        // Value carries '/' and ':'; a slug that only sanitized would map these two onto one filename
        // and silently merge two repositories' ledgers. The digest suffix is what forbids that.
        var a = RepositoryIdentity.From("https://github.com/aer-works/baton.git", null)!;
        var b = RepositoryIdentity.From(null, "/github.com/aer-works/baton")!;

        Assert.NotEqual(a.Value, b.Value);
        Assert.NotEqual(a.FileSlug, b.FileSlug);
        foreach (var slug in new[] { a.FileSlug, b.FileSlug })
        {
            Assert.All(
                slug,
                c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_', $"'{c}' is not filename-safe."));
            Assert.Equal(-1, slug.IndexOfAny(Path.GetInvalidFileNameChars()));
        }
    }

    /// <summary>
    /// The bare, scheme-less spelling an operator types is only an identity when it carries three
    /// components — <c>host/owner/repo</c>. With two, the first is not a host at all, and reading
    /// <c>owner/repo</c> as host <c>owner</c> files a store under a string no git probe can produce.
    /// </summary>
    [Theory]
    [InlineData("owner/repo")]
    [InlineData("aer-works/baton")]
    [InlineData("aer-works/baton.git")]
    [InlineData(" owner/repo/ ")]
    [InlineData("owner")]
    [InlineData("hello world")]
    public void A_bare_spelling_with_no_host_canonicalizes_to_nothing(string typed) =>
        Assert.Null(RepositoryIdentity.TryCanonicalize(typed));

    /// <summary>
    /// The control for the refusal above: the three-component bare spelling, which is the one the
    /// <c>--assert</c> help names, still canonicalizes. A refusal that also swallowed this would make
    /// the flag unusable rather than strict.
    /// </summary>
    [Theory]
    [InlineData("github.com/aer-works/baton")]
    [InlineData("GitHub.com/AER-Works/Baton.git")]
    [InlineData(" github.com/aer-works/baton/ ")]
    [InlineData("https://GitHub.com/AER-Works/Baton.git")]
    [InlineData("git@github.com:AER-Works/Baton.git")]
    public void A_typed_identity_that_carries_a_host_canonicalizes_unchanged(string typed) =>
        Assert.Equal(Canonical, RepositoryIdentity.TryCanonicalize(typed));

    /// <summary>
    /// The second control: the three-component rule is on the BARE spelling only. A remote that states
    /// its host explicitly — scheme or scp — is taken at its word, so a two-component identity from a
    /// host-rooted server survives, and <see cref="RepositoryIdentity.From"/>'s derivation from such a
    /// remote is untouched.
    /// </summary>
    [Theory]
    [InlineData("https://internal/repo")]
    [InlineData("ssh://git@internal/repo.git")]
    [InlineData("git@internal:repo.git")]
    public void An_explicit_host_needs_no_third_component(string typed)
    {
        Assert.Equal("internal/repo", RepositoryIdentity.TryCanonicalize(typed));
        Assert.Equal("internal/repo", RepositoryIdentity.From(typed, gitCommonDirectoryPath: null)?.Value);
    }

    [Fact]
    public void The_file_slug_is_stable_for_one_identity()
    {
        var a = RepositoryIdentity.From("https://github.com/aer-works/baton.git", null)!;
        var b = RepositoryIdentity.From("git@github.com:AER-Works/Baton.git", null)!;

        Assert.Equal(a.FileSlug, b.FileSlug);
    }
}
