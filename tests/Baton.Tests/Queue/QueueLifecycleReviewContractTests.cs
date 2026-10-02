using System.Text;
using Baton.Domain;
using Baton.Queue;
using Baton.Tests.Shared;

namespace Baton.Tests.Queue;

public sealed class QueueLifecycleReviewContractTests
{
    private const string Head = "0123456789abcdef0123456789abcdef01234567";
    private const string ReadinessWording = "whether this PR is ready";

    private static QueueItem Item(WorkStage stage) => new()
    {
        Tag = "2569-review-contract",
        Role = stage is WorkStage.Review or WorkStage.ReReview ? "review" : "implement",
        Workspace = @"C:\repos\w2569",
        SpecFile = @"C:\baton\queue\specs\2569-review-contract.md",
        Issue = 2569,
        Branch = "2569-review-contract",
        Stage = stage,
        Round = stage == WorkStage.ReReview ? 2 : 1,
        LastVerdict = stage == WorkStage.ReReview
            ? @"C:\baton\rooms\queue-2569-review-contract\verdict.json"
            : null,
    };

    [Theory]
    [InlineData(WorkStage.Review, "fresh")]
    [InlineData(WorkStage.Review, "seeded")]
    [InlineData(WorkStage.Review, "custom")]
    [InlineData(WorkStage.ReReview, "fresh")]
    [InlineData(WorkStage.ReReview, "seeded")]
    [InlineData(WorkStage.ReReview, "custom")]
    public void Review_lifecycle_requirement_is_generated_after_operator_template(
        WorkStage stage, string templateKind)
    {
        using var templates = new TempDirectory("baton_lifecycle_review_");
        var templateName = stage == WorkStage.ReReview
            ? QueueBriefTemplates.ReReview
            : QueueBriefTemplates.Review;
        var templatePath = Path.Combine(templates.Path, $"{templateName}.md");
        var seededTemplate = "# Historical operator review\n\n"
            + "Decide " + ReadinessWording + " from the source evidence.\n";
        var customTemplate = "# Custom operator review\n\n"
            + "Decide " + ReadinessWording
            + " using independent checks. {{UNKNOWN_REVIEW_TOKEN}}\n";

        if (templateKind == "seeded")
            File.WriteAllText(templatePath, seededTemplate);
        else if (templateKind == "custom")
            File.WriteAllText(templatePath, customTemplate);

        var before = File.Exists(templatePath) ? File.ReadAllBytes(templatePath) : null;
        var brief = QueueBriefTemplates.Compose(
            stage,
            Item(stage),
            new QueueBriefTemplates.BriefContext(
                PullRequest: 2569,
                HeadSha: Head,
                Round: stage == WorkStage.ReReview ? 2 : 1,
                Findings: stage == WorkStage.ReReview ? "Previous-round finding." : null),
            templates.Path);

        Assert.Contains("source judgment, not merge readiness", brief, StringComparison.Ordinal);
        Assert.Contains("separate delivery gates", brief, StringComparison.Ordinal);
        Assert.Contains("baton-review", brief, StringComparison.Ordinal);
        Assert.Contains("A concrete defect exposed by a check remains reviewable", brief,
            StringComparison.Ordinal);
        Assert.Contains($"\u0060{Head}\u0060 exactly, with no PR label, branch, prefix, suffix, or whitespace.",
            brief, StringComparison.Ordinal);
        Assert.Contains("Set \u0060completion\u0060 to \u0060complete\u0060 only after all review work is finished",
            brief, StringComparison.Ordinal);
        Assert.Contains("\u0060in_progress\u0060", brief, StringComparison.Ordinal);

        var generatedRequirement = brief.IndexOf(
            "source judgment, not merge readiness", StringComparison.Ordinal);
        if (templateKind == "fresh")
        {
            Assert.Contains("## Lifecycle verdict requirement", brief, StringComparison.Ordinal);
            Assert.EndsWith(
                "machine-checked routing evidence.",
                brief.TrimEnd(),
                StringComparison.Ordinal);
        }
        else
        {
            var renderedTemplate = File.ReadAllText(templatePath).TrimEnd();
            var templateStart = brief.IndexOf(renderedTemplate, StringComparison.Ordinal);
            Assert.True(templateStart >= 0, "The operator template was not preserved in the brief.");
            Assert.True(
                generatedRequirement > templateStart + renderedTemplate.Length,
                "The generated lifecycle requirement must remain later than the complete operator template.");
            var readiness = brief.IndexOf(ReadinessWording, StringComparison.Ordinal);
            Assert.True(readiness >= 0, "The operator template's readiness wording was not rendered.");
            Assert.True(readiness < generatedRequirement);
        }

        if (templateKind == "custom")
            Assert.Contains("{{UNKNOWN_REVIEW_TOKEN}}", brief, StringComparison.Ordinal);

        if (before is not null)
        {
            Assert.Equal(before, File.ReadAllBytes(templatePath));
            Assert.Contains(
                Encoding.UTF8.GetString(before).TrimEnd(),
                brief,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.True(File.Exists(templatePath));
        }
    }

    [Theory]
    [InlineData(WorkStage.Implement)]
    [InlineData(WorkStage.Fix)]
    [InlineData(WorkStage.Continue)]
    public void Non_review_briefs_do_not_gain_review_lifecycle_source_judgment(
        WorkStage stage)
    {
        using var templates = new TempDirectory("baton_lifecycle_non_review_");

        var brief = QueueBriefTemplates.Compose(
            stage,
            Item(stage),
            new QueueBriefTemplates.BriefContext(
                Title: "Review contract fixture",
                Do: "Make the requested change.",
                PullRequest: 2569,
                HeadSha: Head,
                Round: stage == WorkStage.Fix ? 2 : 1),
            templates.Path);

        Assert.DoesNotContain("source judgment, not merge readiness", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("separate delivery gates", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("baton-review", brief, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "A concrete defect exposed by a check remains reviewable",
            brief,
            StringComparison.Ordinal);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => DirectoryCleanup.DeleteRecursively(Path);
    }
}
