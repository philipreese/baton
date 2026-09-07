using System.Text.RegularExpressions;

namespace Baton.Architecture.Tests;

/// <summary>
/// #1589: <c>pixi run audit-commentspecrefs</c> (<c>tools/audit-completeness/commentspecrefs.py</c>)
/// only proves a <c>spec/baton.md §N</c> citation *resolves* — that the numbered section is a real
/// heading. It cannot prove the citation is *apt* (that the section actually contains what the
/// citation claims), which is exactly how PR #1582's `StatusCommand.cs` string sent an operator to
/// section 7 ("The daemon, narrowed") for a `--room-dir` recovery procedure that only ever lived in
/// section 3. That check's own docstring says so
/// ("What this can verify is resolution, not topic-truth") and is not touched here — this is the
/// narrower pin the issue's own "what would actually help" recommended instead of widening the
/// checker: pin the population that actually costs an operator something (a spec citation sitting
/// next to a recovery imperative — "see"/"run"/"read") to a stable phrase drawn from the section body,
/// so a citation drifting off its procedure (source edit) or the procedure moving to a different
/// section (spec edit) both fail here, not silently in production.
///
/// Every pin below is a raw substring copied out of the cited source file, matched after collapsing
/// whitespace so a line-wrapped C# string concatenation (or a wrapped spec paragraph) still matches
/// verbatim. This is deliberately independent of `RecoveryGuidance`/other constants — a rename or
/// reformat of the shared string is exactly the kind of drift a byte-identical pin should notice
/// rather than silently keep passing through a shared symbol.
/// </summary>
public sealed class OperatorRecoveryCitationsPinToSpecSectionsTests
{
    public sealed record Pin(string RelativeFilePath, string SourceAnchor, int Section, string SpecAnchor);

    private const string RoomDirRecoveryProcedure =
        "a fresh `baton run` against the room's own `workflow.json`/`bindings.json`, `--room-dir` " +
        "pointed at the room";

    private static readonly Pin[] Pins =
    [
        // CancelCommand.cs: a confirmed-Dead holder is pointed straight at the section 3 --room-dir recovery.
        new(
            "src/Baton.Cli/CancelCommand.cs",
            "$\"{RecoveryGuidance.RunRoomDirInstruction} (see spec/baton.md §3).\"",
            Section: 3,
            RoomDirRecoveryProcedure),
        // CancelCommand.cs: an Unknown-liveness holder gets the same section 3 recovery, conditioned on the
        // operator's own confirmation that no pump is actually running.
        new(
            "src/Baton.Cli/CancelCommand.cs",
            "{RecoveryGuidance.RunRoomDirInstruction} (see \" + \"spec/baton.md §3); if a pump IS confirmed",
            Section: 3,
            RoomDirRecoveryProcedure),
        // CancelCommand.cs: the same Unknown-liveness branch also cites section 2 for *why* no verb exists
        // for a still-alive, unconfirmable pump -- not a recovery, but still an operator-facing cite.
        new(
            "src/Baton.Cli/CancelCommand.cs",
            "holder record can't be confirmed (see spec/baton.md §2).",
            Section: 2,
            "There is currently no verb that reaches a still-alive pump whose holder record can't be " +
            "confirmed"),
        // ResolveCommand.cs: a ContractFailure Indeterminate is pointed at `baton resolve --reject`.
        new(
            "src/Baton.Cli/ResolveCommand.cs",
            "naming the conductor's own judgement after inspecting the \" + \"workspace. See spec/baton.md §3.\");",
            Section: 3,
            "the conductor's own judgement after inspecting the workspace IS something to reject"),
        // ResolveCommand.cs: a VerifyFailed/Arrested/BuildLockBusy Indeterminate is pointed at
        // `--close --reason` (#1622 (d)/#1700, widened by #1796) or at fixing the underlying cause and
        // re-dispatching.
        new(
            "src/Baton.Cli/ResolveCommand.cs",
            "and re-dispatch — a fresh execution reopens the step. See spec/baton.md §3.\");",
            Section: 3,
            "**`--close --reason <text>` (#1622 (d)/#1700, widened by #1796) is the verb for the other three producers**"),
        // RedispatchCommand.cs: a parent room with no terminal.json (engine died mid-wait) is pointed
        // at the same section 3 --room-dir recovery.
        new(
            "src/Baton.Cli/RedispatchCommand.cs",
            "if it's genuinely running, wait for it or cancel it first; if the engine died, \" + $\"{RecoveryGuidance.RunRoomDirInstruction} (see spec/baton.md §3).\");",
            Section: 3,
            RoomDirRecoveryProcedure),
        // RedispatchCommand.cs: an Indeterminate parent with a CapturedResponse producer is pointed at
        // `baton resolve --accept-capture | --reject`.
        new(
            "src/Baton.Cli/RedispatchCommand.cs",
            "--accept-capture | --reject --reason <text>` first, then redispatch — see spec/baton.md §3.",
            Section: 3,
            "`CapturedResponse` admits both `--accept-capture` and `--reject --reason <text>`"),
        // RedispatchCommand.cs: an Indeterminate parent with a ContractFailure producer is pointed at
        // `baton resolve --reject` only (nothing captured to accept).
        new(
            "src/Baton.Cli/RedispatchCommand.cs",
            "redispatch the \" + \"resulting room, or, once resolved, redispatch this one — see spec/baton.md §3.",
            Section: 3,
            "`ContractFailure` has no captured body to accept, so only `--reject --reason <text>` admits it"),
        // RedispatchCommand.cs: an Indeterminate parent with a VerifyFailed/Arrested/unknown producer
        // is pointed at `--close --reason` (which lifts the refusal, #1622 (d)/#1700) or a fresh dispatch.
        new(
            "src/Baton.Cli/RedispatchCommand.cs",
            "fix the underlying cause first — a fresh room is \" + $\"{DescribeFreshDispatchRemedy(workerName, parentEntry)}. See spec/baton.md §3.\",",
            Section: 3,
            "short of `--close` or a brand-new `baton dispatch`"),
        // StatusCommand.cs: the #1582 fix itself -- a parked step whose scheduling engine died is
        // pointed at the section 3 --room-dir recovery (was section 7 before #1582).
        new(
            "src/Baton.Cli/StatusCommand.cs",
            "intervention — {RecoveryGuidance.RunRoomDirInstruction}, and leave it running until \" + $\"{localRetryTime} or nothing fires (see spec/baton.md §3)\";",
            Section: 3,
            RoomDirRecoveryProcedure),
        // MemorySyncCommand.cs (#1852 phase C): a repository whose canonical store holds memories but
        // whose machine holds no vendor root to project them into is pointed at section 12 -- for the
        // ruling that no target is reported rather than one being created, and for the two verbs that
        // give it one.
        new(
            "src/Baton.Cli/MemorySyncCommand.cs",
            "then assert a per-machine Codex root's repository with 'baton memory import --assert \" + $\"<root>={repository}' (see spec/baton.md §12).",
            Section: 12,
            "A repository with no discovered root gets no target and is reported as having none"),
    ];

    [Theory]
    [MemberData(nameof(PinCases))]
    public void Operator_facing_recovery_citation_still_points_at_its_procedure(Pin pin)
    {
        var root = RepoRoot();
        var filePath = Path.Combine(root, pin.RelativeFilePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(filePath), $"{pin.RelativeFilePath}: file not found");
        var sourceText = File.ReadAllText(filePath);

        Assert.True(
            Normalize(sourceText).Contains(Normalize(pin.SourceAnchor), StringComparison.Ordinal),
            $"{pin.RelativeFilePath}: the pinned citation text has moved or changed -- update this " +
            $"test's SourceAnchor to match, and re-check whether the §{pin.Section} it cites is still " +
            "apt for the new wording.");

        var specPath = Path.Combine(root, "spec", "baton.md");
        var sectionBody = ReadSectionBody(File.ReadAllText(specPath), pin.Section);
        Assert.True(
            sectionBody is not null,
            $"{pin.RelativeFilePath}: cites spec/baton.md §{pin.Section}, which is not a live " +
            "top-level heading (## §N) in spec/baton.md -- the section was renumbered or removed.");

        Assert.True(
            Normalize(sectionBody!).Contains(Normalize(pin.SpecAnchor), StringComparison.Ordinal),
            $"{pin.RelativeFilePath}: cites spec/baton.md §{pin.Section}, but that section's body no " +
            "longer contains the procedure/reasoning this string tells the operator to go read -- " +
            "either the citation drifted off its section (fix the §N in the source) or the spec moved " +
            "the material elsewhere (fix this test's SpecAnchor, and the source citation, to match).");
    }

    public static TheoryData<Pin> PinCases()
    {
        var data = new TheoryData<Pin>();
        foreach (var pin in Pins)
        {
            data.Add(pin);
        }

        return data;
    }

    /// <summary>Collapses all whitespace runs to a single space so a citation or spec passage that
    /// wraps differently across a source-file reformat or a markdown line-wrap still compares equal.</summary>
    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>The body of the live top-level <c>## §N</c> heading in <paramref name="specText"/>, up
    /// to (not including) the next top-level heading -- <c>null</c> if §N is not a live heading.</summary>
    private static string? ReadSectionBody(string specText, int section)
    {
        var headings = Regex.Matches(specText, @"^## .*$", RegexOptions.Multiline)
            .Select(m => (Index: m.Index, Text: m.Value))
            .ToList();

        var target = headings.FindIndex(h => Regex.IsMatch(h.Text, $@"^## §{section}\b"));
        if (target < 0)
        {
            return null;
        }

        var start = headings[target].Index;
        var end = target + 1 < headings.Count ? headings[target + 1].Index : specText.Length;
        return specText[start..end];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pixi.toml")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
