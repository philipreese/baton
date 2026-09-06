using System.Security.Cryptography;

namespace Baton.Domain;

/// <summary>
/// One file AER itself wrote into a worker's working directory immediately before spawning it
/// (#1151's canonical-skill projection today), recorded as <b>where</b> AER wrote AND <b>what</b> it
/// wrote there — see <see cref="FlowEvent.EngineFilesPlaced"/> for the durable fact this is the
/// element of.
/// </summary>
/// <remarks>
/// The digest is the whole reason this is a record rather than a bare path (#1929 review round 3,
/// MEDIUM). The workspace readers subtract AER's own writes from the work-product evidence
/// (<c>Workspaces.WorktreeProvisioner.ChangedPathsExcludingEnginePlaced</c>), and a path alone cannot
/// tell "still the bytes AER placed" from "AER placed it and the worker then edited it". The second is
/// the worker's work product, and erasing it is the expensive direction spec/baton.md §3's #1373
/// paragraph prices. With the digest recorded, the subtraction is conditional on the bytes still
/// matching.
/// </remarks>
/// <param name="Path">The absolute destination path that was written.</param>
/// <param name="Sha256">
/// Lower-case hex SHA-256 of the bytes as placed, or <see langword="null"/> when the digest could not
/// be taken (an IO failure reading back what was just copied) or when the fact predates this record
/// carrying one. Null means <b>do not subtract</b>: an unattributable path is counted as the worker's,
/// the same fail-toward-counting direction every other unknown on this path takes.
/// </param>
public sealed record EnginePlacedFile(string Path, string? Sha256)
{
    /// <summary>
    /// The digest of <paramref name="path"/>'s current bytes, or <see langword="null"/> when the file
    /// is missing or unreadable. The single function on both sides of the comparison — the dispatcher
    /// takes it at placement time, the workspace readers take it again at classification time — for the
    /// same reason <see cref="Dispatch.CoreDispatcher.FilesHaveIdenticalBytes"/> is public: two
    /// spellings of "the same bytes" can disagree.
    /// </summary>
    public static string? TryDigest(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            // Never throws out of either caller: a digest that cannot be taken reads as "unattributable",
            // which both sides already handle by counting the path rather than subtracting it.
            return null;
        }
    }

    /// <summary>
    /// Whether <see cref="Path"/> still holds exactly the bytes recorded in <see cref="Sha256"/>.
    /// False when no digest was recorded, when the file is gone, or when it was changed — all three
    /// mean the same thing to a caller: this path is not AER's to subtract.
    /// </summary>
    public bool StillMatchesPlacedBytes() =>
        Sha256 is { Length: > 0 } placed
        && string.Equals(TryDigest(Path), placed, StringComparison.OrdinalIgnoreCase);
}
