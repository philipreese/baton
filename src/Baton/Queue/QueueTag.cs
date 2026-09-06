using System.Text.RegularExpressions;

namespace Baton.Queue;

/// <summary>
/// What a queue tag may be. A tag is not just a label: it names a file
/// (<c>BatonPaths.QueueSpecFile</c>) and reaches the room as a <c>--label</c>, so an unconstrained
/// one is a path-traversal write into <c>~/.baton</c> from whatever composed the queue.
/// </summary>
public static partial class QueueTag
{
    /// <summary>Lower-case letters, digits, hyphen and underscore; 1–64 characters. Deliberately
    /// narrower than "filename-safe": no dot, so a tag can never produce a second extension or a
    /// leading-dot hidden file, and no upper case, so two tags cannot collide on a case-insensitive
    /// filesystem while reading as distinct in <c>baton queue list</c>.</summary>
    [GeneratedRegex("^[a-z0-9_-]{1,64}$")]
    public static partial Regex Pattern { get; }

    /// <summary>The rule, in the words an error message needs.</summary>
    public const string Rule = "1-64 characters of lower-case letters, digits, '-' or '_'";

    public static bool IsValid(string? tag) => tag is not null && Pattern.IsMatch(tag);
}
