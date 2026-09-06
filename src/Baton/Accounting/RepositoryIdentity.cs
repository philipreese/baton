using System.Security.Cryptography;
using System.Text;

namespace Baton.Accounting;

/// <summary>
/// The canonical identity of the repository a piece of work belongs to — the key the cost ledger
/// (#1849) files rows under, and the same key #1852's store will reuse rather than re-deriving.
/// <b>Every worktree of one repository resolves to one identity</b>: the derivation reads the
/// <c>origin</c> remote URL when there is one, and the git <i>common</i> directory (never the
/// per-worktree <c>.git</c> file) when there is not — both of which a worktree shares with its main
/// checkout, which a checkout path does not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure string derivation, deliberately.</b> The engine stays git-agnostic (see
/// <c>Baton.Cli.WorkspaceHead</c>'s own remarks for that rule); this type is handed the two strings a
/// git probe produced and never runs git itself. <c>Baton.Cli.RepositoryIdentityResolver</c> is the
/// probe.
/// </para>
/// <para>
/// <b><see cref="Value"/> is not a filename.</b> A normalized origin identity carries <c>/</c>
/// separators and a common-dir fallback carries a drive letter and a colon, either of which would
/// break or silently nest <c>{BatonPaths.Root}/ledger/&lt;identity&gt;.jsonl</c>. <see cref="FileSlug"/>
/// is the on-disk spelling, and it ends in a digest of <see cref="Value"/> precisely so two distinct
/// identities that sanitize to the same readable prefix still get two files rather than silently
/// sharing one ledger.
/// </para>
/// </remarks>
public sealed record RepositoryIdentity
{
    private RepositoryIdentity(string value)
    {
        Value = value;
        FileSlug = BuildFileSlug(value);
    }

    /// <summary>
    /// The canonical identity recorded on every ledger row: <c>host/owner/repo</c>, case-folded and
    /// with any <c>.git</c> suffix stripped, when an <c>origin</c> remote was present — otherwise
    /// <c>gitdir:&lt;normalized common-dir path&gt;</c>, tagged so the two derivations can never be
    /// mistaken for each other by a reader.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// The filename stem <see cref="Value"/> is stored under. Readable prefix plus a digest suffix —
    /// see the type remarks for why the digest is not optional.
    /// </summary>
    public string FileSlug { get; }

    /// <summary>
    /// Derives an identity from a git probe's two answers. <paramref name="originUrl"/> wins when it
    /// is present and parseable; <paramref name="gitCommonDirectoryPath"/> is the fallback for a
    /// repository with no remote. Returns <see langword="null"/> when neither yields anything — a
    /// non-git directory has no repository identity, and inventing one (the checkout path, say) is
    /// exactly the per-worktree fragmentation this type exists to prevent.
    /// </summary>
    public static RepositoryIdentity? From(string? originUrl, string? gitCommonDirectoryPath)
    {
        if (TryNormalizeRemote(originUrl) is { } fromRemote)
        {
            return new RepositoryIdentity(fromRemote);
        }

        if (!string.IsNullOrWhiteSpace(gitCommonDirectoryPath))
        {
            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gitCommonDirectoryPath.Trim()));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            // Case-folded for the same reason BatonPaths.RecordKeyComparer is: two casings of one
            // directory must not become two ledgers. Slashes forward-normalized so a path spelled
            // with either separator hashes to one slug.
            return new RepositoryIdentity(GitDirectoryPrefix + full.Replace('\\', '/').ToLowerInvariant());
        }

        return null;
    }

    /// <summary>
    /// The canonical <see cref="Value"/> spelling of an identity an <b>operator typed</b>, or
    /// <see langword="null"/> when the string canonicalizes to nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately a function returning a string, not a second constructor</b>, exactly as
    /// <see cref="FileSlugFor"/> is: <see cref="From"/> stays the only way to make a
    /// <see cref="RepositoryIdentity"/>, so nothing can fabricate a half-parsed identity into a row's
    /// <c>repository</c> field. What this produces is the canonical string, which is then stored or
    /// slugged like any other.
    /// </para>
    /// <para>
    /// <b>It runs the same two normalisations <see cref="From"/> does, and adds no third.</b> A
    /// <c>gitdir:</c> value goes through the common-directory branch; anything else goes through
    /// <see cref="TryNormalizeRemote"/> — first as typed, so a pasted clone URL
    /// (<c>https://github.com/Owner/Repo.git</c>, <c>git@github.com:Owner/Repo.git</c>) canonicalizes,
    /// and then behind an <c>https://</c> so the bare <c>host/owner/repo</c> spelling an operator
    /// actually types is read as the host-and-path it is. That second attempt <b>supplies a scheme and
    /// nothing else</b>: it does not invent a host, so <c>owner/repo</c> is refused. Only a bare
    /// <c>host/owner/repo</c> (or a clone URL) reaches the remote parser — guessing a forge would
    /// file a repository under an identity no probe could ever reproduce.
    /// </para>
    /// <para>
    /// <b>Null is a refusal, not a fallback.</b> A caller that cannot canonicalize an operator's string
    /// must reject it rather than store it raw: a raw value differing from the probe's answer only in
    /// case or in a <c>.git</c> suffix is a SECOND store file for one repository, with every entry
    /// duplicated across the two and no error anywhere.
    /// </para>
    /// </remarks>
    public static string? TryCanonicalize(string? assertedValue)
    {
        if (string.IsNullOrWhiteSpace(assertedValue))
        {
            return null;
        }

        var raw = assertedValue.Trim();

        if (raw.StartsWith(GitDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return From(originUrl: null, gitCommonDirectoryPath: raw[GitDirectoryPrefix.Length..])?.Value;
        }

        if (TryNormalizeRemote(raw) is { } canonical)
        {
            return canonical;
        }

        // A scheme-less assertion needs a host plus a repository path. Without this guard,
        // "owner/repo" is parsed as host "owner" and path "repo" after adding https://.
        return raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length < 3
            ? null
            : TryNormalizeRemote("https://" + raw);
    }

    /// <summary>
    /// The tag <see cref="From"/>'s common-directory branch writes, and the one prefix
    /// <see cref="TryCanonicalize"/> must recognise before trying to read a value as a remote — the
    /// scp-like reading would otherwise take the tag's own colon for the separator and turn
    /// <c>gitdir:c:/repos/x</c> into <c>gitdir/c:/repos/x</c>.
    /// </summary>
    private const string GitDirectoryPrefix = "gitdir:";

    /// <summary>
    /// <c>host/owner/repo</c> from any of the remote spellings git accepts —
    /// <c>https://host/owner/repo.git</c>, <c>ssh://git@host:22/owner/repo/</c>,
    /// <c>git@host:owner/repo.git</c>, with or without userinfo, trailing slash, or <c>.git</c>.
    /// <see langword="null"/> when the string carries no host-and-path at all, which is what makes
    /// <see cref="From"/> fall through to the common-dir derivation rather than record a half-parsed
    /// identity.
    /// </summary>
    private static string? TryNormalizeRemote(string? originUrl)
    {
        if (string.IsNullOrWhiteSpace(originUrl))
        {
            return null;
        }

        var raw = originUrl.Trim();

        string host;
        string path;

        // scp-like syntax (git@host:owner/repo.git) is not a URI and Uri would misread the colon as a
        // port, so it is matched before any URI parse is attempted.
        var scpColon = IndexOfScpSeparator(raw);
        if (scpColon >= 0)
        {
            host = StripUserInfo(raw[..scpColon]);
            path = raw[(scpColon + 1)..];
        }
        else if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            return null;
        }

        path = path.Replace('\\', '/').Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        path = path.Trim('/');
        if (host.Length == 0 || path.Length == 0)
        {
            return null;
        }

        return $"{host}/{path}".ToLowerInvariant();
    }

    /// <summary>
    /// Position of the <c>:</c> in a scp-like remote (<c>[user@]host:path</c>), or -1 when the string
    /// is not one. A <c>://</c> anywhere means it is a URI, and a colon that is followed only by
    /// digits-then-slash is a port, not an scp separator.
    /// </summary>
    private static int IndexOfScpSeparator(string raw)
    {
        if (raw.Contains("://", StringComparison.Ordinal))
        {
            return -1;
        }

        var colon = raw.IndexOf(':');
        if (colon <= 0 || colon == raw.Length - 1)
        {
            return -1;
        }

        // A Windows path ("C:\repo") is not a remote.
        if (colon == 1 && char.IsLetter(raw[0]))
        {
            return -1;
        }

        return colon;
    }

    private static string StripUserInfo(string authority)
    {
        var at = authority.LastIndexOf('@');
        return at >= 0 ? authority[(at + 1)..] : authority;
    }

    /// <summary>
    /// The filename stem a given canonical <see cref="Value"/> is stored under — the same derivation
    /// <see cref="FileSlug"/> uses, exposed for the one caller that has an identity STRING and no
    /// repository to probe: <c>baton ledger --repo-identity &lt;key&gt;</c>, naming a repository other
    /// than the one the operator is standing in.
    /// <para>
    /// <b>Deliberately a function, not a second constructor.</b> <see cref="From"/> stays the only way
    /// to make a <see cref="RepositoryIdentity"/>, so nothing can fabricate a half-parsed identity into
    /// a row's <c>repository</c> field; this only ever computes a path to READ.
    /// </para>
    /// </summary>
    public static string FileSlugFor(string canonicalValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(canonicalValue);
        return BuildFileSlug(canonicalValue);
    }

    /// <summary>
    /// A readable, filesystem-safe prefix of <paramref name="value"/> plus a 12-hex-character SHA-256
    /// digest of the WHOLE value. The digest is what guarantees injectivity: <c>github.com/a/b</c> and
    /// <c>github.com/a:b</c> sanitize to the same prefix and must not share a ledger file.
    /// </summary>
    private static string BuildFileSlug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? char.ToLowerInvariant(c) : '-');
        }

        var readable = builder.ToString().Trim('-');
        if (readable.Length > 64)
        {
            readable = readable[..64].TrimEnd('-');
        }

        if (readable.Length == 0)
        {
            readable = "repo";
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
        return $"{readable}-{digest}";
    }
}
