using System.Text;
using System.Text.Json;
using Baton.Domain;

namespace Baton.Tests.Domain;

/// <summary>
/// The parse floor of the <c>ReviewVerdict</c> schema (#732, decision 0043): what must be
/// present, what casing is forgiven, and what extra content is tolerated. One definition of "valid
/// verdict" exists (<see cref="ReviewVerdictSchema.TryParse"/>); these pin its edges.
/// </summary>
public class ReviewVerdictSchemaTests
{
    [Fact]
    public void A_full_verdict_parses_with_lowercase_property_names_and_enum_values()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """
            {"reviewedRef": "740-branch @ 5f8813a",
             "summary": "one defect, two cosmetics",
             "findings": [
                {"severity": "high", "claim": "paused steps print no paths", "status": "confirmed",
                 "anchor": {"file": "src/Baton.Cli/FlowStateReporter.cs", "line": 76},
                 "detail": "the gate keys on Succeeded while a pause masks the status"},
                {"severity": "low", "claim": "redundant GetFullPath", "status": "unverified"}
             ]}
            """);

        Assert.True(ReviewVerdictSchema.TryParse(bytes, out var verdict, out var error));
        Assert.Null(error);
        Assert.NotNull(verdict);
        Assert.Equal("740-branch @ 5f8813a", verdict.ReviewedRef);
        Assert.Equal(2, verdict.Findings.Count);
        Assert.Equal(ReviewFindingSeverity.High, verdict.Findings[0].Severity);
        Assert.Equal(ReviewFindingStatus.Confirmed, verdict.Findings[0].Status);
        Assert.Equal(76, verdict.Findings[0].Anchor!.Line);
        Assert.Null(verdict.Findings[1].Anchor);
    }

    [Theory]
    [InlineData(@"""decision"": ""approve"",", ReviewDecision.Approve)]
    [InlineData(@"""decision"": ""BLOCK"",", ReviewDecision.Block)]
    // Everything a reviewer might write that is not one of the two words, each landing on null rather
    // than on an exception that would cost the whole document: a near-miss word, a shape that is not a
    // string at all, an explicit null, and the field simply absent.
    [InlineData(@"""decision"": ""approved"",", null)]
    [InlineData(@"""decision"": true,", null)]
    [InlineData(@"""decision"": {""value"": ""block""},", null)]
    [InlineData(@"""decision"": null,", null)]
    [InlineData("", null)]
    public void A_decision_this_enum_does_not_have_reads_as_absent_and_never_loses_the_document(
        string decisionField, ReviewDecision? expected)
    {
        var bytes = Encoding.UTF8.GetBytes($$"""{"reviewedRef": "main", {{decisionField}} "findings": []}""");

        Assert.True(ReviewVerdictSchema.TryParse(bytes, out var verdict, out var error));
        Assert.Null(error);
        Assert.Equal(expected, verdict!.Decision);
    }

    /// <summary>
    /// There is ONE parse, and a missing <c>decision</c> does not fail it (operator ruling,
    /// spec/baton.md §13). The field is optional on the wire and required only by the conductor
    /// queue's <c>WorkItemLifecycle</c>, which reads the null this leaves behind.
    /// </summary>
    /// <remarks>
    /// Asserted with its findings intact, because "the document still reads" is the whole reason the
    /// requirement does not live here: the cost ledger's counts and <c>baton watch</c>'s payload read
    /// every verdict written before this field existed. The control is the with-decision document
    /// below — same fixture, one field added — so the null above is about the absent field rather than
    /// about a parse that never populates it.
    /// </remarks>
    [Fact]
    public void A_verdict_with_no_decision_parses_with_its_findings_intact_and_a_null_decision()
    {
        var withoutDecision = Encoding.UTF8.GetBytes(
            """{"reviewedRef": "main", "findings": [{"severity": "high", "claim": "x", "status": "confirmed"}]}""");

        Assert.True(ReviewVerdictSchema.TryParse(withoutDecision, out var parsed, out var error));
        Assert.Null(error);
        Assert.Single(parsed!.Findings);
        Assert.Null(parsed.Decision);

        var withDecision = Encoding.UTF8.GetBytes(
            """
            {"reviewedRef": "main", "decision": "approve",
             "findings": [{"severity": "high", "claim": "x", "status": "confirmed"}]}
            """);

        Assert.True(ReviewVerdictSchema.TryParse(withDecision, out var accepted, out _));
        Assert.Equal(ReviewDecision.Approve, accepted!.Decision);
    }

    [Fact]
    public void An_empty_findings_array_is_a_valid_verdict_meaning_nothing_was_found()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"reviewedRef": "main", "findings": []}""");

        Assert.True(ReviewVerdictSchema.TryParse(bytes, out var verdict, out _));
        Assert.Empty(verdict!.Findings);
    }

    [Fact]
    public void Unknown_extra_fields_are_tolerated_at_every_level()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """
            {"reviewedRef": "main", "findings": [
                {"severity": "medium", "claim": "x", "status": "refuted", "confidence": 0.9}
             ], "model": "sonnet", "tokens": 12345}
            """);

        Assert.True(ReviewVerdictSchema.TryParse(bytes, out _, out var error));
        Assert.Null(error);
    }

    /// <summary>
    /// #1882 made <c>instruments</c> a DECLARED field, which is exactly how a previously-tolerated
    /// unknown field turns into a parse failure: the moment STJ has a type for a key, a worker writing
    /// that key in some other shape throws instead of being ignored. The regression this guards is a
    /// contract failure and a retried frontier review on a lane dispatched with no <c>--verify-cmd</c>
    /// at all — and naming the field in the review prompt makes a model writing it MORE likely, not
    /// less. The engine overwrites this key unconditionally, so nothing is lost by dropping a
    /// malformed one.
    /// </summary>
    [Theory]
    [InlineData(""""{"reviewedRef": "main", "findings": [], "instruments": "dotnet build"}"""")]
    [InlineData(""""{"reviewedRef": "main", "findings": [], "instruments": {"cmd": "dotnet build"}}"""")]
    [InlineData(""""{"reviewedRef": "main", "findings": [], "instruments": 7}"""")]
    [InlineData(""""{"reviewedRef": "main", "findings": [], "instruments": ["dotnet build"]}"""")]
    [InlineData(""""{"reviewedRef": "main", "findings": [], "instruments": [{"command": 3}]}"""")]
    public void A_model_written_instruments_field_of_the_wrong_shape_is_dropped_not_a_parse_failure(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);

        Assert.True(ReviewVerdictSchema.TryParse(bytes, out var verdict, out var error));
        Assert.Null(error);
        Assert.Null(verdict!.Instruments);
    }

    /// <summary>
    /// The discriminating other half of the arm above: dropping a malformed <c>instruments</c> must
    /// not mean never reading a well-formed one, or the field the engine stamps would be invisible to
    /// every reader of a verdict.
    /// </summary>
    [Fact]
    public void A_well_formed_instruments_field_still_parses()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """
            {"reviewedRef": "main", "findings": [],
             "instruments": [{"command": "dotnet build", "exitCode": 0, "wallClockMs": 34300},
                             {"command": "dotnet test", "exitCode": null, "wallClockMs": 600000}]}
            """);

        Assert.True(ReviewVerdictSchema.TryParse(bytes, out var verdict, out _));
        Assert.Equal(2, verdict!.Instruments!.Count);
        Assert.Equal("dotnet build", verdict.Instruments[0].Command);
        Assert.Equal(0, verdict.Instruments[0].ExitCode);
        Assert.Null(verdict.Instruments[1].ExitCode);
        Assert.Equal(600000, verdict.Instruments[1].WallClockMs);
    }

    /// <summary>
    /// Executes the tolerant converter's write half, which nothing in the tree reaches today (the
    /// engine's stamp edits the parsed JSON object instead). Without this arm it is unexecuted code,
    /// and the failure its hand-written form avoids — re-entering the converter from inside itself —
    /// is a StackOverflowException that cannot be caught or asserted on after the fact. Round-tripping
    /// is what forces it to run at all.
    /// </summary>
    [Fact]
    public void A_verdict_round_trips_through_the_serializer_with_its_instruments_intact()
    {
        var original = new ReviewVerdict(
            "main",
            [new ReviewFinding(ReviewFindingSeverity.Low, "x", ReviewFindingStatus.Confirmed)],
            Instruments: [new VerifyInstrument("dotnet build", 0, 34300), new VerifyInstrument("dotnet test", null, 91002)],
            Decision: ReviewDecision.Block);

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(original));

        Assert.True(ReviewVerdictSchema.TryParse(bytes, out var verdict, out _));
        Assert.Equal(original.Instruments, verdict!.Instruments);

        // The decision converter's write half, for the same reason: it is the only thing that renders
        // the enum as the lower-case word the contract reads back, and a round trip is what runs it.
        Assert.Equal(ReviewDecision.Block, verdict.Decision);
    }

    /// <summary>
    /// Pins the deserializer-leniency fact decision 0043's Rests-on table cites (the why lives on
    /// the null check inside <see cref="ReviewVerdictSchema.TryParse"/>): presence is enforced by
    /// the hand-written floor, not by STJ. If this arm ever starts failing on a JsonException
    /// instead of the floor message, STJ tightened and the hand checks can be revisited.
    /// </summary>
    [Fact]
    public void A_document_without_findings_is_refused_by_the_shape_floor_not_by_the_deserializer()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"reviewedRef": "main"}""");

        Assert.False(ReviewVerdictSchema.TryParse(bytes, out var verdict, out var error));
        Assert.Null(verdict);
        Assert.Contains("'findings' must be present", error);
    }

    [Theory]
    [InlineData("""{"findings": []}""", "reviewedRef")]
    [InlineData("""{"reviewedRef": "  ", "findings": []}""", "reviewedRef")]
    [InlineData("""{"reviewedRef": "main", "findings": [null]}""", "findings[0]")]
    [InlineData("""{"reviewedRef": "main", "findings": [{"severity": "high", "claim": " ", "status": "confirmed"}]}""", "claim")]
    // #1913 review finding 4: absent, not misspelled. STJ binds a missing VALUE-type parameter to
    // default(ReviewFindingSeverity), which is High, so this document used to parse as a confirmed
    // high-severity finding nobody wrote -- and #1901's cost-ledger row counts it into a durable
    // accounting field. Refused here, in the single reader, rather than papered over per consumer.
    [InlineData("""{"reviewedRef": "main", "findings": [{"claim": "x", "status": "confirmed"}]}""", "severity")]
    [InlineData("""{"reviewedRef": "main", "findings": [{"severity": "high", "claim": "x", "status": "confirmed", "anchor": {"file": "f", "line": 0}}]}""", "line")]
    [InlineData("""{"reviewedRef": "main", "findings": [{"severity": "high", "claim": "x", "status": "confirmed", "anchor": {"line": 3}}]}""", "anchor.file")]
    [InlineData("""{"reviewedRef": "main", "findings": [{"severity": "high", "claim": "x"}]}""", "status")]
    public void Documents_below_the_semantic_floor_are_refused_with_a_reason_naming_the_field(
        string json, string expectedInError)
    {
        Assert.False(ReviewVerdictSchema.TryParse(Encoding.UTF8.GetBytes(json), out _, out var error));
        Assert.Contains(expectedInError, error);
    }

    /// <summary>
    /// The discriminating other half of the status arm above (#1919): refusing an absent
    /// <c>status</c> must not cost the members a worker actually writes. Without this, a refusal
    /// that rejected every status would pass the arm above just as well.
    /// </summary>
    [Theory]
    [InlineData("confirmed", ReviewFindingStatus.Confirmed)]
    [InlineData("refuted", ReviewFindingStatus.Refuted)]
    [InlineData("unverified", ReviewFindingStatus.Unverified)]
    public void Every_explicit_status_member_still_parses(string written, ReviewFindingStatus expected)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $$"""{"reviewedRef": "main", "findings": [{"severity": "high", "claim": "x", "status": "{{written}}"}]}""");

        Assert.True(ReviewVerdictSchema.TryParse(bytes, out var verdict, out var error));
        Assert.Null(error);
        Assert.Equal(expected, verdict!.Findings[0].Status);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"reviewedRef": "main", "findings": [{"severity": "catastrophic", "claim": "x", "status": "confirmed"}]}""")]
    [InlineData("null")]
    public void Malformed_documents_are_refused_without_throwing(string content)
    {
        Assert.False(ReviewVerdictSchema.TryParse(Encoding.UTF8.GetBytes(content), out var verdict, out var error));
        Assert.Null(verdict);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>
    /// The declaration side's round trip: <see cref="OutputSchema"/> serializes as a string, and a
    /// default (<see cref="OutputSchema.None"/>) is omitted entirely — a contract written before
    /// the field existed and one written after it, with no schema declared, are the same bytes.
    /// </summary>
    [Fact]
    public void ProducedOutput_serializes_Schema_as_a_string_and_omits_the_default()
    {
        var schemad = JsonSerializer.Serialize(new ProducedOutput("verdict.json", Schema: OutputSchema.ReviewVerdict));
        Assert.Contains("\"ReviewVerdict\"", schemad);

        var plain = JsonSerializer.Serialize(new ProducedOutput("plan"));
        Assert.DoesNotContain("Schema", plain);

        var roundTripped = JsonSerializer.Deserialize<ProducedOutput>(schemad);
        Assert.Equal(OutputSchema.ReviewVerdict, roundTripped!.Schema);

        var caseInsensitive = JsonSerializer.Deserialize<ProducedOutput>(
            """{"Name": "verdict.json", "Schema": "reviewverdict"}""");
        Assert.Equal(OutputSchema.ReviewVerdict, caseInsensitive!.Schema);
    }
}
