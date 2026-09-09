using System.Security.Cryptography;
using System.Text;
using Baton.Status;

namespace Baton.Memory;

/// <summary>
/// One memory a caller <b>authored</b> rather than one the importer copied out of a vendor file
/// (#2071, slice one of #1852's write path): the pure part of <c>baton memory add</c> — the digest,
/// the id, and the <see cref="MemoryEntry"/> those two produce.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same row type and the same store as the import, deliberately.</b> #2071's ruling — which
/// spec/baton.md §12 states, rather than this file — puts new memories through this verb, so an
/// authored entry has to be readable by everything that already reads the store —
/// <c>MemoryStore.ReadResolvedAsync</c>, <c>MemoryProjection</c>, an undo. A second row shape would
/// have made <c>add</c> a second store wearing the first one's name.
/// </para>
/// <para>
/// <b><see cref="SourcePathFor"/> is a KEY, not a location, and no file is ever written there.</b>
/// <see cref="MemoryEntry.Derive"/> hashes (subject, source path, content digest), and an authored
/// memory has no file it was read out of — so this supplies a path-shaped, content-addressed stand-in
/// whose only job is to make that derivation total. Two consequences are the whole reason it is shaped
/// this way rather than picked for looks: the id becomes a function of <b>(subject, text)</b> alone,
/// which is what makes spec/baton.md §12's duplicate refusal a property of the id rather than a
/// comparison pass of its own; and two <i>different</i> texts never collide on a
/// filename, which keeps <c>MemoryImportPlan.LinkSupersession</c>'s same-subject-same-filename rule
/// from minting supersession links between unrelated authored entries and an archived file. It is
/// absolute — <see cref="MemoryEntry.Derive"/> resolves its path argument against the process's
/// working directory, so a relative stand-in would give one text two ids depending on where
/// <c>baton</c> ran.
/// </para>
/// <para>
/// <b>What that costs, stated because a reader's prior fills it in wrongly.</b> The path names a file
/// that does not exist and will not be created, so it is not provenance an operator can open — which
/// is why <see cref="Vendor"/> exists as the readable test for "this row was authored"
/// (<see cref="IsAuthored"/>) and why <c>MemoryProjection</c> renders an authored entry's origin as
/// its <see cref="MemoryEntry.AssertedBy"/> instead of printing this path at a reader.
/// </para>
/// </remarks>
public static class AuthoredMemory
{
    /// <summary>
    /// <see cref="MemoryEntry.SourceVendor"/> for an authored entry — Baton itself, since no vendor
    /// file is behind it. The one test a reader has for telling an authored row from an imported one;
    /// see <see cref="IsAuthored"/>.
    /// </summary>
    public const string Vendor = "baton";

    /// <summary>
    /// Directory component of <see cref="SourcePathFor"/>. <b>Never created</b> — see the type remarks
    /// for why this is a key rather than a place.
    /// </summary>
    public const string SourceDirectoryName = "authored";

    /// <summary><see cref="MemoryEntry.AssertedBy"/> for an add run outside any lane.</summary>
    /// <remarks>
    /// <b>It means "no lane context was present", and nothing else.</b> <c>MemoryLaneAssertion</c> is
    /// where the resolution lives and where the cost of widening this value past that meaning is
    /// stated; this constant is only its outside-a-lane answer.
    /// </remarks>
    public const string Operator = "operator";

    /// <summary>
    /// The entry <paramref name="text"/> becomes for <paramref name="repository"/>: kind
    /// <b>declared</b> (a caller of <c>add</c> states it, so <see cref="MemoryKindSource.Declared"/> is
    /// the honest source), digest over the text's UTF-8 bytes, and the id those two derive.
    /// </summary>
    /// <remarks>
    /// Pure, and it takes its clock as an argument for the reason <c>MemoryImportPlan.Build</c> does:
    /// <c>--dry-run</c> must be the same computation as the real run with the write left off, not a
    /// second implementation of it. The clock is deliberately not part of the id
    /// (<see cref="MemoryEntry.Derive"/>), which is what makes adding the same text twice a refusal
    /// rather than a second row.
    /// </remarks>
    /// <param name="repository">The subject — a canonical <c>RepositoryIdentity.Value</c>.</param>
    /// <param name="text">The memory itself, stored verbatim and never parsed for meaning.</param>
    /// <param name="kind">Declared by the caller. Nothing here reads <paramref name="text"/> to guess one.</param>
    /// <param name="assertedBy">
    /// Who is asserting this entry — <see cref="Operator"/>, or a lane's <c>role/vendor/room</c>. Always
    /// recorded, because an authored entry's subject is asserted by whoever ran the verb rather than
    /// derived from a root the way an imported one's is.
    /// </param>
    /// <param name="addedAtUtc">Stamped on the row as <see cref="MemoryEntry.ImportedAtUtc"/>.</param>
    public static MemoryEntry Create(
        string repository, string text, MemoryKind kind, string assertedBy, DateTime addedAtUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentException.ThrowIfNullOrEmpty(assertedBy);

        var sha256 = Digest(text);
        var sourcePath = SourcePathFor(repository, sha256);

        return new MemoryEntry(
            MemoryEntry.Derive(repository, sourcePath, sha256),
            repository,
            kind,
            MemoryKindSource.Declared,
            text,
            sha256,
            sourcePath,
            Vendor,
            VendorMemoryScope.BatonManaged,
            addedAtUtc,
            addedAtUtc,
            AssertedBy: assertedBy);
    }

    /// <summary>Lower-case hex SHA-256 of <paramref name="text"/>'s UTF-8 bytes, BOM-free.</summary>
    /// <remarks>
    /// The text IS the authority for an authored entry, which is the one place it differs from an
    /// imported one: there the digest describes a file's bytes and <see cref="MemoryEntry.Text"/> is a
    /// decode of them that provably need not reproduce it (see that field's own doc). Here the two are
    /// the same bytes by construction.
    /// </remarks>
    public static string Digest(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>
    /// <c>{Root}/&lt;repo-slug&gt;/memory/authored/&lt;sha256&gt;.md</c> — the content-addressed
    /// stand-in this type's remarks describe. Nothing creates it.
    /// </summary>
    public static string SourcePathFor(string repository, string sha256)
    {
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentException.ThrowIfNullOrEmpty(sha256);

        return Path.Combine(
            BatonPaths.MemoryDirectory(FleetMemory.SlugFor(repository)),
            SourceDirectoryName,
            $"{sha256}.md");
    }

    /// <summary>
    /// Whether <paramref name="entry"/> was authored through <c>baton memory add</c> rather than
    /// imported from a vendor file — read off <see cref="MemoryEntry.SourceVendor"/>, which is a
    /// recorded fact about the row, never off the stand-in path, which is a derived key.
    /// </summary>
    public static bool IsAuthored(MemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return string.Equals(entry.SourceVendor, Vendor, StringComparison.OrdinalIgnoreCase);
    }
}
