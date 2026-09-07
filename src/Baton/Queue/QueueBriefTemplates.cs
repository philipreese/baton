using System.Text;
using Baton.Domain;
using Baton.Status;

namespace Baton.Queue;

/// <summary>
/// The four brief templates a work item's spec is rendered from (#1934 slice 2). The operator ruling
/// that makes these the product rather than a convenience is spec/baton.md §13's, with the specimen it
/// was made against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shipped compiled-in, materialized only when absent.</b> <see cref="EnsureMaterialized"/> writes a
/// default to <c>BatonPaths.QueueTemplateFile</c> only if no file is there, and
/// <see cref="Load"/> then reads whatever is on disk. An operator who edits a template keeps the edit
/// through every later <c>baton queue add</c> — which is the difference between a template that is the
/// product and one that is a fixture the tool keeps stamping back over.
/// </para>
/// <para>
/// <b>No template ever carries a room path</b> — spec/baton.md §13 has why that is a mechanism rather
/// than a preference. Here it means: the findings travel as TEXT, inlined verbatim by
/// <see cref="RenderFindings"/>, and <c>QueueItem.LastVerdict</c> keeps the path for the operator's own
/// trace with nothing rendering it.
/// </para>
/// <para>
/// <b>Four templates, five stages, and the two sharings are not the same kind.</b>
/// <see cref="WorkStage.Continue"/> renders from <see cref="Implement"/> with one generated
/// continuation paragraph prepended: a continuation IS the implement brief — same issue, same standing
/// rules — plus the sentence saying what the previous lane left behind. A file of its own would be that
/// same text with one paragraph different, and the two would drift the first time the standing rules
/// changed.
/// </para>
/// <para>
/// <b><see cref="WorkStage.Review"/> and <see cref="WorkStage.ReReview"/> do NOT share one</b>, which
/// they did until #2004's review. The re-review brief asserts things about a round that has happened —
/// "Re-review PR #N", "What the previous round found", "say whether the new head actually closes it" —
/// so rendering it for the first review handed the reviewer a brief about findings nobody had written
/// (spec/baton.md §13 has what that cost). What separates them is not the
/// stage but whether there IS a prior verdict to carry: <see cref="Compose"/> asks that — the item's
/// <c>lastVerdict</c>, or findings passed for this render — and spec/baton.md §13 has the rule with the
/// case it settles.
/// </para>
/// </remarks>
public static class QueueBriefTemplates
{
    /// <summary>The plain implement brief, rendered from an issue body. Also the base for
    /// <see cref="WorkStage.Continue"/>.</summary>
    public const string Implement = "implement";

    /// <summary>The first review of a PR: no prior round, so no findings section.</summary>
    public const string Review = "review";

    /// <summary>The fix round: the conductor's "Fix round for PR #N" header plus the findings.</summary>
    public const string Fix = "fix";

    /// <summary>The re-review: "Re-review PR #N at &lt;sha&gt;" plus the prior findings.</summary>
    public const string ReReview = "re-review";

    /// <summary>Every shipped template name, in the order <c>baton queue</c> materializes them.</summary>
    public static readonly IReadOnlyList<string> Names = [Implement, Review, Fix, ReReview];

    /// <summary>
    /// The template a stage renders from. <paramref name="hasPriorRound"/> is the review split — see the
    /// type remarks for why it is a prior <em>verdict</em> rather than the stage or the round number
    /// (with rounds now counted per dispatch, the first review arrives at round 1, so a round test would
    /// answer "re-review" for it).
    /// </summary>
    public static string NameFor(WorkStage stage, bool hasPriorRound) => stage switch
    {
        WorkStage.Implement or WorkStage.Continue => Implement,
        WorkStage.Fix => Fix,
        WorkStage.Review or WorkStage.ReReview => hasPriorRound ? ReReview : Review,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "A 'ready' item renders no brief."),
    };

    /// <summary>
    /// Writes any shipped default that is not already on disk, and returns the names it wrote.
    /// Idempotent, and deliberately never an overwrite.
    /// </summary>
    public static IReadOnlyList<string> EnsureMaterialized(string? templatesDirectory = null)
    {
        var directory = templatesDirectory ?? BatonPaths.QueueTemplatesDirectory;
        Directory.CreateDirectory(directory);

        var written = new List<string>();
        foreach (var name in Names)
        {
            var path = Path.Combine(directory, $"{name}.md");
            if (File.Exists(path))
            {
                continue;
            }

            File.WriteAllText(path, ShippedDefault(name));
            written.Add(name);
        }

        return written;
    }

    /// <summary>
    /// The template's text: the operator's copy when one exists, otherwise the shipped default. Reading
    /// through <see cref="EnsureMaterialized"/> first means the two can never answer differently.
    /// </summary>
    public static string Load(string name, string? templatesDirectory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var path = Path.Combine(templatesDirectory ?? BatonPaths.QueueTemplatesDirectory, $"{name}.md");
        return File.Exists(path) ? File.ReadAllText(path) : ShippedDefault(name);
    }

    /// <summary>
    /// Substitutes <c>{{TOKEN}}</c> placeholders. <b>Unknown tokens are left alone</b>, not blanked: an
    /// operator's own template may carry text this renderer has never heard of, and silently deleting it
    /// would be a brief with a hole in it that nobody can see is missing.
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        var rendered = new StringBuilder(template);
        foreach (var (token, value) in values)
        {
            rendered.Replace($"{{{{{token}}}}}", value);
        }

        return rendered.ToString();
    }

    /// <summary>
    /// A verdict's findings as the conductor copied them by hand: the summary, then one
    /// <c>severity status file:line — claim</c> line per finding with its detail indented under it.
    /// </summary>
    /// <remarks>
    /// <b>Every finding, not only the ones the reviewer would call blocking.</b> The verdict's own
    /// <c>decision</c> is what opens a fix round (<c>WorkItemLifecycle</c>); what the fixer then reads
    /// is the whole review, because a medium finding beside the high one is context for the same
    /// change — and nothing here can tell which findings the reviewer decided on anyway. Verbatim
    /// text, no summarizing — the engine does not paraphrase what a worker wrote.
    /// </remarks>
    public static string RenderFindings(ReviewVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        var builder = new StringBuilder();
        if (verdict.Summary is { Length: > 0 } summary)
        {
            builder.AppendLine(summary.Trim()).AppendLine();
        }

        if (verdict.Findings is not { Count: > 0 })
        {
            builder.AppendLine("(the review recorded no findings)");
            return builder.ToString().TrimEnd() + Environment.NewLine;
        }

        foreach (var finding in verdict.Findings)
        {
            var severity = finding.Severity?.ToString().ToLowerInvariant() ?? "unstated";
            var status = finding.Status?.ToString().ToLowerInvariant() ?? "unstated";
            var anchor = finding.Anchor is { } a
                ? $" {a.File}{(a.Line is { } line ? $":{line}" : string.Empty)}"
                : string.Empty;
            builder.AppendLine($"- **{severity} / {status}**{anchor} — {finding.Claim.Trim()}");
            if (finding.Detail is { Length: > 0 } detail)
            {
                foreach (var detailLine in detail.Trim().ReplaceLineEndings("\n").Split('\n'))
                {
                    builder.AppendLine($"  {detailLine}");
                }
            }
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>
    /// The whole brief for one stage: the template on disk (materializing the shipped default first if
    /// there is none), with every placeholder filled from <paramref name="item"/> and
    /// <paramref name="context"/>.
    /// </summary>
    /// <remarks>
    /// <b><see cref="WorkStage.Continue"/> gets one generated paragraph and the implement brief</b> —
    /// the type's remarks say why that is not a fourth template. The paragraph names what the previous
    /// lane left behind in the item's own terms (the branch and the PR), never a room path.
    /// </remarks>
    public static string Compose(
        WorkStage stage, QueueItem item, BriefContext context, string? templatesDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(context);

        EnsureMaterialized(templatesDirectory);

        // "Has a previous round left findings to carry", asked of the two things that can carry them:
        // the findings rendered for THIS brief, and the verdict path already on the item from an
        // earlier review. Neither is set for a first review, which is the whole split.
        var hasPriorRound = context.Findings is { Length: > 0 } || item.LastVerdict is { Length: > 0 };

        var body = Render(Load(NameFor(stage, hasPriorRound), templatesDirectory), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TITLE"] = context.Title,
            ["DO"] = context.Do,
            ["ISSUE"] = item.Issue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?",
            ["BRANCH"] = item.Branch ?? "(unrecorded)",
            ["WORKTREE"] = item.Workspace,
            ["PR"] = context.PullRequest?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?",
            ["SHA"] = context.HeadSha ?? "the current head",
            ["ROUND"] = context.Round.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["FINDINGS"] = context.Findings ?? "(no findings were recorded)",
        });

        return stage == WorkStage.Continue
            ? $"""
               # Continue the work on `{item.Branch}`

               The previous lane stopped before its work reached the pull request. Finish it and push it
               to `{item.Branch}` — the change is already partly made in this worktree, so read
               `git status` and `git log` first and continue from there rather than starting over.

               The brief that lane was working from follows unchanged.

               ---

               {body}
               """
            : body;
    }

    /// <summary>What a rendered brief needs that the item itself does not carry.</summary>
    /// <param name="Title">The issue's title, for the implement brief's header.</param>
    /// <param name="Do">The "## Do" section — the operator's <c>--spec</c> text, or the issue body.</param>
    /// <param name="PullRequest">The PR a fix or re-review is about.</param>
    /// <param name="HeadSha">The head a re-review reviews at.</param>
    /// <param name="Round">The fix round.</param>
    /// <param name="Findings">
    /// <see cref="RenderFindings"/>'s text — the findings themselves, never the path they came from.
    /// </param>
    public sealed record BriefContext(
        string Title = "",
        string Do = "",
        int? PullRequest = null,
        string? HeadSha = null,
        int Round = 0,
        string? Findings = null);

    /// <summary>The compiled-in default for <paramref name="name"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A name this type does not ship.</exception>
    /// <remarks>
    /// The standing-rules block is spliced in HERE, not left as a placeholder in the file: the
    /// materialized template is the operator's to edit, and a template whose rules were a token the
    /// renderer expanded later would be one whose rules the operator cannot see, let alone change.
    /// </remarks>
    public static string ShippedDefault(string name) => (name switch
    {
        Implement => ImplementDefault,
        Review => ReviewDefault,
        Fix => FixDefault,
        ReReview => ReReviewDefault,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No shipped template by that name."),
    }).Replace(RulesPlaceholder, StandingRules, StringComparison.Ordinal);

    /// <summary>The token <see cref="ShippedDefault"/> splices <see cref="StandingRules"/> into. Never
    /// seen by <see cref="Render"/> — it is gone by the time a template is on disk.</summary>
    private const string RulesPlaceholder = "<<STANDING-RULES>>";

    /// <summary>
    /// The standing lane rules, once. Every mutating brief carries this block, which is the half of
    /// each hand-written brief that never varied — and the half that was silently dropped when a brief
    /// was written in a hurry.
    /// </summary>
    private const string StandingRules = """
        ## Standing rules for this lane

        - Build and test through `python tools/buildlock.py <cmd>` (no `--`).
        - Do NOT run `pixi run gates`, launch a sub-agent, or invoke a live vendor CLI.
        - Do NOT file issues.
        - Never push with `--no-verify`. Commit a checkpoint after each step.
        - Test hygiene: temp files and directories go through `FileCleanup`/`DirectoryCleanup`, and any
          process wait through `BoundedProcessWait`. Operator-recovery strings are pinned by test.
        - Record once: a fact lives in one place. `spec/baton.md` is the register; everything else links
          to it with at most a one-clause gloss.

        ## Ship

        `dotnet build -warnaserror`; the touched test classes with `--minimum-expected-tests 1`;
        `dotnet format --verify-no-changes`; `pixi run audit-recordonce`. Conventional-commit messages.
        Name any protected file the change touches in the PR body. No AI attribution anywhere — no
        `Co-Authored-By`, no "Generated with", no session links.
        """;

    private const string ImplementDefault = """
        # {{TITLE}}

        Worktree `{{WORKTREE}}`, branch `{{BRANCH}}`, issue #{{ISSUE}}.

        ## Do

        {{DO}}

        <<STANDING-RULES>>

        Closes #{{ISSUE}}
        """;

    private const string FixDefault = """
        # Fix round for PR #{{PR}}

        Worktree `{{WORKTREE}}`, branch `{{BRANCH}}`, issue #{{ISSUE}}, round {{ROUND}}.

        The review below BLOCKED this PR. Fix what it found, on this branch, and push.

        ## The reviewer's findings, verbatim

        {{FINDINGS}}

        ## Do

        Address each finding above. A finding you believe is wrong is answered in the PR body with the
        evidence, not silently skipped — and if it is right about a defect the fix does not cover, say so.

        <<STANDING-RULES>>
        """;

    private const string ReviewDefault = """
        # Review PR #{{PR}} at {{SHA}}

        Issue #{{ISSUE}}, round {{ROUND}}. This is the FIRST review of this PR — there is no previous
        round, and no findings to carry.

        ## Do

        Review PR #{{PR}} at `{{SHA}}` independently against issue #{{ISSUE}}: does the change do what
        the issue asked, and does it do it correctly? Read the diff, not the commit messages' account of
        it.

        Write your findings as a ReviewVerdict to `$BATON_OUTPUT_DIR/verdict.json` — severity
        high/medium/low, status confirmed/refuted/unverified, and a claim you have not verified is
        `unverified` rather than `confirmed`.

        ## Verdict

        Write `"decision": "approve"` or `"decision": "block"` in `verdict.json`: it is YOUR call on
        whether this PR is ready, nothing derives it from the findings, and a verdict without it fails
        this room's contract.
        """;

    private const string ReReviewDefault = """
        # Re-review PR #{{PR}} at {{SHA}}

        Issue #{{ISSUE}}, round {{ROUND}}. Review the PR as it stands now.

        ## What the previous round found, verbatim

        {{FINDINGS}}

        ## Do

        Review PR #{{PR}} at `{{SHA}}` independently. For each finding above, say whether the new head
        actually closes it — a claim that it does is checked against the diff, not against the fix
        commit's own message. Then review the change as a whole: a fix round is where new defects are
        introduced.

        Write your findings as a ReviewVerdict to `$BATON_OUTPUT_DIR/verdict.json` — severity
        high/medium/low, status confirmed/refuted/unverified, and a claim you have not verified is
        `unverified` rather than `confirmed`.

        ## Verdict

        Write `"decision": "approve"` or `"decision": "block"` in `verdict.json`: it is YOUR call on
        whether this PR is ready, nothing derives it from the findings, and a verdict without it fails
        this room's contract.
        """;
}
