using System.Text.RegularExpressions;
using Baton.Cli.Daemon;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #1946 — the page's two deliveries, checked without a browser. What CAN be established here is
/// that the daemon injects the marker, that the page's own branch reads exactly that marker, and
/// that each branch reaches the transport it is supposed to; what CANNOT is that the rendered board
/// looks right in a phone's browser, which no test in this tree covers and which the PR body says
/// plainly rather than implying coverage that does not exist.
/// </summary>
public sealed class GlassPageTests
{
    private static string RepoGlassHtml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Baton.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, "tools", "fleet-glass", "glass.html"));
    }

    [Fact]
    public void The_embedded_page_is_the_repos_own_glass_html()
    {
        // One page, two deliveries: a copy under src/ would satisfy every other assertion in this
        // file while silently drifting from the artifact, which is the failure this arm exists for.
        Assert.Equal(
            GlassPage.Inject(RepoGlassHtml()).ReplaceLineEndings(),
            GlassPage.Html().ReplaceLineEndings());
    }

    [Fact]
    public void The_served_page_carries_exactly_one_marker_element_before_title()
    {
        // #2053, measured on the tailnet: the served bytes were byte-identical to glass.html, so
        // DAEMON_SERVED was false and the board never rendered. The subject here is therefore
        // GlassPage.Html() -- the actual embedded resource the `/` handler writes -- not a fixture,
        // and not the disk file, because a fixture lacking the page's own two mentions of the name
        // cannot reproduce the failure at all. Markup, not bytes: GlassMarkup owns why.
        var served = GlassMarkup.Of(GlassPage.Html());

        Assert.Single(Regex.Matches(served, Regex.Escape(GlassPage.SourceMetaTag)));
        Assert.True(
            served.IndexOf(GlassPage.SourceMetaTag, StringComparison.Ordinal)
            < served.IndexOf("<title>", StringComparison.OrdinalIgnoreCase),
            "The marker must precede <title> so it lands in the implied <head>.");
    }

    [Fact]
    public void Injection_puts_the_marker_in_the_head_and_never_twice()
    {
        var injected = GlassPage.Inject(RepoGlassHtml());
        var markup = GlassMarkup.Of(injected);

        Assert.Single(Regex.Matches(markup, Regex.Escape(GlassPage.SourceMetaTag)));
        Assert.True(
            markup.IndexOf(GlassPage.SourceMetaTag, StringComparison.Ordinal)
            < markup.IndexOf("<title>", StringComparison.OrdinalIgnoreCase),
            "The marker must precede <title> so it lands in the implied <head>.");

        // Idempotent: serving twice, or a source file that one day carries its own marker, must not
        // produce two metas -- the page's querySelector would still work, but the injected copy would
        // then be unremovable by editing the source. Byte-identical, not merely marker-count-equal.
        Assert.Equal(injected, GlassPage.Inject(injected));
    }

    [Fact]
    public void Injection_still_works_on_a_page_with_no_title()
    {
        var injected = GlassPage.Inject("<div>no title here</div>");

        Assert.StartsWith(GlassPage.SourceMetaTag, injected, StringComparison.Ordinal);
        Assert.EndsWith("<div>no title here</div>", injected, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pages_branch_condition_reads_exactly_the_meta_the_daemon_injects()
    {
        var html = RepoGlassHtml();

        // POLARITY, both directions, on the one condition that decides the delivery. The two halves
        // are written in different files (a C# const and a JS querySelector) and nothing but this
        // assertion couples them: a rename on either side silently sends every daemon-served page
        // down the artifact path, where `claude.use` is undefined and the board never renders.
        var selector = Regex.Match(html, @"const DAEMON_SERVED = [^;]+;");
        Assert.True(selector.Success, "glass.html must declare DAEMON_SERVED.");
        Assert.Contains(GlassPage.SourceMetaName, selector.Value, StringComparison.Ordinal);
        Assert.Contains(GlassPage.DaemonSourceValue, selector.Value, StringComparison.Ordinal);

        // The daemon's own tag has to satisfy the page's selector as written -- name, attribute and
        // value, in the shape the querySelector matches on.
        Assert.Matches(
            $"""meta\[name="{GlassPage.SourceMetaName}"\]\[content="{GlassPage.DaemonSourceValue}"\]""",
            selector.Value);
        Assert.Matches(
            $"""<meta name="{GlassPage.SourceMetaName}" content="{GlassPage.DaemonSourceValue}">""",
            GlassPage.SourceMetaTag);
    }

    [Fact]
    public void Each_delivery_reaches_its_own_transport_and_only_its_own()
    {
        var html = RepoGlassHtml();

        // The daemon branch: same-origin read plus the change stream, and an early return so the
        // connector path is never entered (`claude` does not exist in a plain browser -- reaching
        // that line at all would throw before anything rendered).
        var daemonBranch = Regex.Match(
            html, @"if\(DAEMON_SERVED\)\{.*?\}", RegexOptions.Singleline);
        Assert.True(daemonBranch.Success, "glass.html must branch on DAEMON_SERVED before using the connector.");
        Assert.Contains("startDaemonFeed", daemonBranch.Value, StringComparison.Ordinal);
        Assert.Contains("return;", daemonBranch.Value, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("if(DAEMON_SERVED){", StringComparison.Ordinal)
            < html.IndexOf("await claude.use(\"mcp\")", StringComparison.Ordinal),
            "The daemon branch must return before `claude.use` is touched.");

        var feed = Regex.Match(html, @"function startDaemonFeed\(applySnapshot\)\{.*?\n\}", RegexOptions.Singleline);
        Assert.True(feed.Success, "glass.html must define startDaemonFeed.");
        Assert.Contains("fetch(\"/projection.json\"", feed.Value, StringComparison.Ordinal);
        Assert.Contains("""new EventSource("/events")""", feed.Value, StringComparison.Ordinal);
        Assert.Contains("applySnapshot", feed.Value, StringComparison.Ordinal);
        // Slice 1 is the fleet row only: the daemon feed must reach no drill-down route, because none
        // is served (C-11 assigns those to slice 2, on this plane alone).
        Assert.DoesNotContain("/stdout", feed.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("/rooms", feed.Value, StringComparison.Ordinal);

        // The artifact delivery is untouched: still the connector, still the same rendering entry
        // point, and there is exactly ONE of that entry point for both deliveries to share.
        Assert.Contains("""mcp.watchTool("baton", "fleet_status", {}, (ev) =>""", html, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(html, @"const applySnapshot = \(snap\) =>"));
        Assert.Contains("applySnapshot(snap);", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_heartbeat_banner_that_names_the_worker_is_gated_off_the_tailnet_delivery()
    {
        // `/projection.json` carries no heartbeat_at, so this row fires on EVERY tailnet render --
        // permanently telling an operator to redeploy a worker that is not in that delivery's path.
        // The one banner row that had to be gated, and the reason it had to be.
        Assert.Contains(
            "} else if(!DAEMON_SERVED && hbMs == null){", RepoGlassHtml(), StringComparison.Ordinal);
    }
}
