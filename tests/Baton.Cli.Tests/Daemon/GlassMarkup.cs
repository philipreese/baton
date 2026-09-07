using System.Text.RegularExpressions;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #2053 — the glass page as a browser PARSES it, rather than as bytes. Why the two differ for this
/// page is stated once, in <c>GlassPage.Inject</c>'s remark; the consequence here is that an
/// assertion over the raw served bytes certifies an injection that never happened, which is how the
/// shipped bug passed both of this tree's marker checks. Every such assertion runs over this instead.
/// </summary>
internal static class GlassMarkup
{
    /// <summary>The page with its HTML comments removed, so only real markup remains.</summary>
    internal static string Of(string html) =>
        Regex.Replace(html, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
}
