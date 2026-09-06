using Baton.Memory;

namespace Baton.Cli;

/// <summary>
/// The one resolution of a Claude memory root to the checkout and repository it belongs to, shared by
/// <see cref="MemoryAuditCommand"/> and <see cref="MemoryImportCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared rather than copied, because both verbs make the claim that they agree.</b> The import's
/// help says <c>baton memory audit</c> is a preview of what the import will do, and the import's own
/// remarks said its resolution "is <see cref="MemoryAuditCommand"/>'s, unchanged" — while the code was
/// a line-for-line copy that both statements would have stopped describing the first time either site
/// was edited and the other was not. One method makes the claim structural. What the import adds on
/// top (an alias fallback where this produced nothing) stays at the import's own site: it is a
/// difference, and a difference belongs where a reader can see it.
/// </para>
/// <para>
/// <b>Session <c>cwd</c> is ground truth; the decoded name is a guess.</b> The decoder's tie-break is
/// handed <see cref="RepositoryIdentityResolver.IsWorkTreeRoot"/> rather than
/// <see cref="Directory.Exists(string)"/> — <see cref="MemoryRootPath.Resolve"/>'s own comment states
/// what each weaker predicate got wrong. The asymmetry only visible from here: a session <c>cwd</c> is
/// deliberately NOT filtered that way. It is the value the directory name was derived from, so a
/// session run from inside a checkout belongs to that checkout; the narrow predicate is for a GUESSED
/// reading, not a recorded one.
/// </para>
/// <para>
/// <b>The git probe runs only against a path that exists.</b> A probe of a vanished directory answers
/// nothing, and running one anyway would spend a process per gone root to learn that.
/// </para>
/// </remarks>
internal static class ClaudeMemoryRootResolver
{
    public static async Task<MemoryRootResolution> ResolveAsync(
        MemoryRoot root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);

        var resolution = MemoryRootPath.Resolve(
            root.DirectoryName,
            MemoryRootPath.ReadSessionWorkingDirectories(root.SessionDirectoryPath),
            RepositoryIdentityResolver.IsWorkTreeRoot);

        var checkoutExists = resolution.CheckoutPath is { Length: > 0 } path && Directory.Exists(path);

        var repository = checkoutExists
            ? await RepositoryIdentityResolver
                .TryResolveAsync(resolution.CheckoutPath!, cancellationToken).ConfigureAwait(false)
            : null;

        return new MemoryRootResolution(root, resolution, checkoutExists, repository?.Value);
    }
}
