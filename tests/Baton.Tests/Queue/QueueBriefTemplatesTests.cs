using Baton.Domain;
using Baton.Queue;
using Baton.Tests.Shared;

namespace Baton.Tests.Queue;

/// <summary>
/// The three shipped brief templates (#1934 slice 2): that a rendered brief carries the findings, that
/// it carries no room path, and that an operator's edit survives.
/// </summary>
public sealed class QueueBriefTemplatesTests
{
    private static QueueItem Item() => new()
    {
        Tag = "1934-lane",
        Role = "implement",
        Workspace = @"C:\repos\w1934",
        SpecFile = @"C:\baton\queue\specs\1934-lane.md",
        Issue = 1934,
        Branch = "1934-lane",
        Stage = WorkStage.Fix,
        Round = 1,
    };

    private static ReviewVerdict Verdict() => new(
        "PR #1941",
        [
            new(ReviewFindingSeverity.High, "the launched-tag refusal runs after the copy", ReviewFindingStatus.Confirmed,
                new ReviewFindingAnchor("src/Baton.Cli/QueueCommand.cs", 87), "File.Copy has already overwritten it"),
        ],
        "One blocking finding.");

    [Fact]
    public void A_fix_brief_carries_the_findings_verbatim_and_never_a_room_path()
    {
        using var templates = new TempDirectory("baton_templates_");

        var brief = QueueBriefTemplates.Compose(
            WorkStage.Fix, Item(),
            new QueueBriefTemplates.BriefContext(
                PullRequest: 1941, Round: 1, Findings: QueueBriefTemplates.RenderFindings(Verdict())),
            templates.Path);

        Assert.Contains("Fix round for PR #1941", brief, StringComparison.Ordinal);
        Assert.Contains("the launched-tag refusal runs after the copy", brief, StringComparison.Ordinal);
        Assert.Contains("File.Copy has already overwritten it", brief, StringComparison.Ordinal);
        Assert.Contains("src/Baton.Cli/QueueCommand.cs:87", brief, StringComparison.Ordinal);

        // The whole reason the findings are inlined: a lane's grant cannot read another room, so a
        // brief naming one is a brief its worker cannot follow (spec/baton.md §13).
        Assert.DoesNotContain(".baton", brief, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rooms", brief, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_negative_control_a_room_path_in_the_findings_text_would_be_caught()
    {
        using var templates = new TempDirectory("baton_templates_");

        // The discriminating control for the assertion above: the same render with a room path in the
        // findings DOES put one in the brief. Without this, "no room path" could be passing because the
        // template renders nothing at all.
        var brief = QueueBriefTemplates.Compose(
            WorkStage.Fix, Item(),
            new QueueBriefTemplates.BriefContext(
                PullRequest: 1941, Findings: @"see C:\Users\x\.baton\rooms\queue-1934-abcd\report.md"),
            templates.Path);

        Assert.Contains(@".baton\rooms", brief, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_re_review_brief_names_the_pr_and_the_head_it_reviews_at()
    {
        using var templates = new TempDirectory("baton_templates_");

        var brief = QueueBriefTemplates.Compose(
            WorkStage.ReReview, Item(),
            new QueueBriefTemplates.BriefContext(
                PullRequest: 1941, HeadSha: "deadbeef", Round: 2,
                Findings: QueueBriefTemplates.RenderFindings(Verdict())),
            templates.Path);

        Assert.Contains("Re-review PR #1941 at deadbeef", brief, StringComparison.Ordinal);
        Assert.Contains("verdict.json", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void An_implement_brief_carries_the_standing_rules_and_the_closes_line()
    {
        using var templates = new TempDirectory("baton_templates_");

        var brief = QueueBriefTemplates.Compose(
            WorkStage.Implement, Item() with { Stage = WorkStage.Implement },
            new QueueBriefTemplates.BriefContext(Title: "Do the thing", Do: "Build X."),
            templates.Path);

        Assert.Contains("tools/buildlock.py", brief, StringComparison.Ordinal);
        Assert.Contains("--no-verify", brief, StringComparison.Ordinal);
        Assert.Contains("BoundedProcessWait", brief, StringComparison.Ordinal);
        Assert.Contains("No AI attribution", brief, StringComparison.Ordinal);
        Assert.EndsWith("Closes #1934", brief.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_operator_edit_survives_and_the_shipped_default_is_not_stamped_back_over_it()
    {
        using var templates = new TempDirectory("baton_templates_");
        var written = QueueBriefTemplates.EnsureMaterialized(templates.Path);
        Assert.Equal(QueueBriefTemplates.Names.Count, written.Count);

        var path = Path.Combine(templates.Path, "fix.md");
        File.WriteAllText(path, "# My own fix brief for PR #{{PR}}");

        var writtenAgain = QueueBriefTemplates.EnsureMaterialized(templates.Path);
        var brief = QueueBriefTemplates.Compose(
            WorkStage.Fix, Item(), new QueueBriefTemplates.BriefContext(PullRequest: 7), templates.Path);

        Assert.Empty(writtenAgain);
        Assert.Equal("# My own fix brief for PR #7", brief);
    }

    [Fact]
    public void An_unknown_placeholder_is_left_alone_rather_than_blanked()
    {
        var rendered = QueueBriefTemplates.Render(
            "{{PR}} and {{SOMETHING_ELSE}}", new Dictionary<string, string> { ["PR"] = "9" });

        Assert.Equal("9 and {{SOMETHING_ELSE}}", rendered);
    }

    /// <summary>
    /// A throwaway templates directory, removed through <see cref="DirectoryCleanup"/> — the tripwire
    /// helper, so a leaked directory is this file's failure rather than the next run's.
    /// </summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => DirectoryCleanup.DeleteRecursively(Path);
    }
}
