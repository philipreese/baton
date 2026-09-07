using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Baton.Cli.Daemon;

/// <summary>
/// #1946 — the bytes <see cref="GlassHttpService"/> serves at <c>/</c>: the embedded page (the
/// csproj's <c>EmbeddedResource</c> comment names which file and carries why it is embedded rather
/// than pathed) with one marker injected so the page can tell which delivery it is running under.
/// </summary>
internal static class GlassPage
{
    internal const string ResourceName = "Baton.Cli.Daemon.glass.html";

    /// <summary>
    /// The <c>&lt;meta&gt;</c> name the page reads. Its mere presence means "served by the daemon"
    /// — an artifact never carries it, so the artifact copy needs no change to keep behaving exactly
    /// as it does today, and a stale artifact cannot accidentally opt itself into a same-origin fetch
    /// that has no origin to fetch from.
    /// </summary>
    internal const string SourceMetaName = "baton-glass-source";

    internal const string DaemonSourceValue = "daemon";

    internal const string SourceMetaTag =
        $"""<meta name="{SourceMetaName}" content="{DaemonSourceValue}">""";

    private static readonly Regex HtmlComment =
        new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    // Case-SENSITIVE on purpose: the page's own `querySelector('meta[name="baton-glass-source"]...')`
    // matches that attribute value case-sensitively, so a differently-cased meta would satisfy a
    // case-insensitive guard here while the page ignored it -- suppressing the injection and leaving
    // the board blank, which is #2053's failure again by another route. Erring the other way is
    // harmless: a second meta the selector still matches.
    private static readonly Regex ExistingSourceMeta = new(
        $@"<meta\s[^>]*name\s*=\s*""{SourceMetaName}""",
        RegexOptions.CultureInvariant);

    private static string? _cached;

    /// <summary>
    /// Injects <see cref="SourceMetaTag"/> immediately before the page's <c>&lt;title&gt;</c>, or at
    /// the very front when there is none. <c>glass.html</c> is a fragment-shaped document (a leading
    /// HTML comment, then <c>&lt;title&gt;</c>) with no explicit <c>&lt;head&gt;</c> to insert into,
    /// so the title is the stable anchor; a meta before it lands in the same implied head either way.
    /// Idempotent: a page that already carries the tag is returned unchanged, so this can never
    /// double-inject if the source file itself ever gains one.
    /// <para>
    /// #2053 — the idempotency guard matches a real <c>&lt;meta&gt;</c> ELEMENT outside HTML
    /// comments, never the name or the tag as text, because the page documents its own marker: the
    /// header comment quotes the literal tag and the script quotes the name in a
    /// <c>querySelector</c>. A guard that searched the raw bytes for either string matched that
    /// documentation, returned the page unmodified, and the daemon served an artifact-path page that
    /// rendered blank over the tailnet.
    /// </para>
    /// </summary>
    internal static string Inject(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (ExistingSourceMeta.IsMatch(HtmlComment.Replace(html, string.Empty)))
        {
            return html;
        }

        var titleIndex = html.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
        return titleIndex < 0
            ? SourceMetaTag + "\n" + html
            : html[..titleIndex] + SourceMetaTag + "\n" + html[titleIndex..];
    }

    /// <summary>The served page, read once per process from the embedded resource.</summary>
    internal static string Html()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        using var stream = typeof(GlassPage).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The glass page resource '{ResourceName}' is missing from {Assembly.GetExecutingAssembly().GetName().Name}. " +
                "It is embedded by Baton.Cli.csproj from tools/fleet-glass/glass.html.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        _cached = Inject(reader.ReadToEnd());
        return _cached;
    }
}
