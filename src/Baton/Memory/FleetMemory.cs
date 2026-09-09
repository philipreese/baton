using Baton.Accounting;
using Baton.Status;

namespace Baton.Memory;

/// <summary>
/// The reserved <c>fleet</c> memory slug (#2112): the one canonical store that belongs to no
/// repository, for operator preferences, devices, standing lane rules and machine facts. Its store
/// sits at <c>{Root}/fleet/memory/</c> beside the per-repository ones.
/// </summary>
/// <remarks>
/// <para>
/// <b>The word is the slug, the subject, and the directory name, and it is spelled here once.</b> A
/// repository's slug is <see cref="RepositoryIdentity.FileSlugFor"/> — a readable prefix plus a
/// digest — so no git identity can ever slug to the bare word <c>fleet</c>, which is what makes
/// reserving it safe: <see cref="SlugFor"/> is the only place the two derivations meet, and every
/// memory verb goes through it rather than through <see cref="RepositoryIdentity.FileSlugFor"/>
/// directly.
/// </para>
/// <para>
/// <b>It is accepted where a memory subject is asked for and refused where a git identity is.</b>
/// <c>baton memory add/sync/import --assert</c> take it as a subject; <c>baton ledger --repo-identity</c>
/// and <c>--repository-facts</c> refuse it (<see cref="RefuseAsGitIdentity"/>), because a cost ledger
/// is keyed by what git answered and a checked-in fact is derivable from a repository — which is the
/// whole test for what may NOT be fleet-scoped. spec/baton.md §12 states which kinds may.
/// </para>
/// </remarks>
public static class FleetMemory
{
    /// <summary>The reserved word: subject value, store slug and directory name, all one spelling.</summary>
    public const string Slug = "fleet";

    /// <summary>Whether <paramref name="repository"/> names the fleet store rather than a repository.</summary>
    public static bool IsFleet(string? repository) =>
        string.Equals(repository?.Trim(), Slug, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The store slug for a memory subject: <see cref="Slug"/> itself for the fleet, otherwise
    /// <see cref="RepositoryIdentity.FileSlugFor"/>. The one seam between the two derivations.
    /// </summary>
    public static string SlugFor(string repository)
    {
        ArgumentException.ThrowIfNullOrEmpty(repository);
        return IsFleet(repository) ? Slug : RepositoryIdentity.FileSlugFor(repository);
    }

    /// <summary>The fleet store's <c>entries.jsonl</c> under the current <see cref="BatonPaths.Root"/>.</summary>
    public static string EntriesFile => BatonPaths.MemoryEntriesFile(Slug);

    /// <summary>The fleet store's <c>links.jsonl</c> under the current <see cref="BatonPaths.Root"/>.</summary>
    public static string LinksFile => BatonPaths.MemoryLinksFile(Slug);

    /// <summary>The fleet store's <c>retractions.jsonl</c> under the current <see cref="BatonPaths.Root"/>.</summary>
    public static string RetractionsFile => BatonPaths.MemoryRetractionsFile(Slug);

    /// <summary>
    /// The refusal a parser prints when the reserved word arrives where a git identity is required.
    /// A message rather than a throw, because the parsers own their exception type
    /// (<c>Baton.Cli.CliArgumentException</c>) and this layer does not reference it.
    /// </summary>
    /// <param name="option">The flag it arrived on.</param>
    /// <param name="why">One sentence on why this position cannot take the fleet, in the operator's terms.</param>
    public static string GitIdentityRefusal(string option, string why) =>
        $"'{option} {Slug}' is refused: '{Slug}' is Baton's reserved memory slug for operator and machine " +
        $"facts, not a repository, and {why} Pass a canonical git identity such as 'github.com/owner/repo'.";
}
