using Baton.Memory;

namespace Baton.Cli;

/// <summary>
/// Parsed <c>baton memory add</c> arguments (#2071). A record for the reason every other options type
/// in this directory is one — the parser decides, the command reads.
/// </summary>
/// <param name="Text">
/// The memory itself, stored verbatim. Never parsed for meaning, here or anywhere downstream
/// (Architecture Rule 1).
/// </param>
/// <param name="Kind">
/// What the caller declares this entry is. <b>Required, and <c>unknown</c> is not accepted</b> — see
/// <see cref="MemoryAddOptionsParser"/> for why an absence is refused rather than defaulted.
/// </param>
/// <param name="Repository">
/// The canonical subject, or <see langword="null"/> to probe git at the working directory. Canonicalized
/// by the parser through the same write-path refusal <c>--assert</c> uses, so a value here always names
/// a store file a probe could also reach.
/// </param>
/// <param name="DryRun">
/// Whether this run writes nothing at all — no entry, no manifest, and no directory. It still refuses a
/// duplicate, or it would be a preview of a different run than the one it previews.
/// </param>
/// <param name="Help">Whether <c>--help</c> was passed.</param>
public sealed record MemoryAddOptions(
    string Text,
    MemoryKind Kind,
    string? Repository,
    bool DryRun,
    bool Help);
