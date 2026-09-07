using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// A review worker's structured findings (#732) — the artifact a review-shaped step declares as a
/// schema-checked <see cref="ProducedOutput"/>. Not to be confused with
/// <see cref="Outcomes.OutcomeVerdict"/>, which is Flow's own classification of how an execution
/// ended; a <see cref="ReviewVerdict"/> is content a worker wrote, and per decision 0043 the engine
/// only ever checks that it <i>parses</i> — severity and status are evidence surfaced to a person,
/// never inputs to routing (Architecture Rule 1, decision 0038) — a rule about Flow's routing, which
/// spec/baton.md §13 carves the conductor queue out of: <c>WorkItemLifecycle</c> reads
/// <see cref="Decision"/>, the field the reviewer wrote its own APPROVE/BLOCK into. That carve-out,
/// and what it does not license, is stated there — severity and status remain evidence for a person
/// and route nothing.
/// </summary>
/// <param name="ReviewedRef">
/// What was reviewed — a branch, commit, or PR reference. Required: an unanchored verdict cannot
/// answer "which code was this even about", which is the first question anyone reading one asks.
/// </param>
/// <param name="Findings">Empty is valid and meaningful: the reviewer looked and found nothing.</param>
/// <param name="Summary">Optional free-text overall assessment.</param>
/// <param name="Instruments">
/// #1882: the deterministic commands the ENGINE ran before the reviewer's first turn, copied onto the
/// verdict by <c>Mutation.VerifyStep.InjectInstrumentsAsync</c> after the worker wrote it. Additive
/// and optional: absent on every verdict from a review dispatched without <c>--verify-cmd</c>, and on
/// every verdict written before this field existed. Never a claim the model makes about itself — the
/// engine overwrites whatever the model put here, which is what makes "a reviewer cannot claim an
/// instrument it did not have" true rather than merely asked for.
/// </param>
/// <param name="Decision">
/// <b>The reviewer's own APPROVE/BLOCK, and the only thing the conductor queue routes on</b>
/// (operator ruling, spec/baton.md §13). Null when the document names no decision or names one this
/// enum does not have — the parse survives that, so a decision-less verdict is still readable by the
/// ledger and by a person, and <c>WorkItemLifecycle</c> can say "carries no decision" rather than
/// being handed a guess. What refuses it instead is
/// <see cref="ReviewVerdictSchema.TryParseForReviewContract"/>.
/// </param>
public sealed record ReviewVerdict(
    string ReviewedRef,
    IReadOnlyList<ReviewFinding> Findings,
    string? Summary = null,
    [property: JsonConverter(typeof(TolerantVerifyInstrumentListConverter))]
    IReadOnlyList<VerifyInstrument>? Instruments = null,
    [property: JsonConverter(typeof(TolerantReviewDecisionConverter))]
    ReviewDecision? Decision = null);

/// <summary>
/// What the reviewer decided the PR should do next. <b>Two values, and no third for "unsure"</b>: the
/// absence of a decision is already expressible — the field is null — and a reviewer who cannot decide
/// is exactly the case that has to reach a person rather than a routing arm.
/// </summary>
public enum ReviewDecision
{
    /// <summary>Nothing here blocks the PR; the conductor may merge it.</summary>
    Approve,

    /// <summary>The PR needs another round before it can merge.</summary>
    Block,
}

/// <summary>
/// Reads <c>decision</c> as <see langword="null"/> for anything that is not the string
/// <c>approve</c> or <c>block</c> (case-insensitively) — a missing field, a null, a number, an object,
/// or a word this enum does not have.
/// </summary>
/// <remarks>
/// <b>Tolerant on purpose, and the tolerance is what makes the requirement enforceable.</b> The plain
/// <see cref="JsonStringEnumConverter{T}"/> throws on an unknown value, which would make a reviewer's
/// typo ("approved") indistinguishable from a file that is not JSON at all: both would fail
/// <see cref="ReviewVerdictSchema.TryParse"/>, and every downstream reader — the cost ledger's finding
/// counts, <c>baton watch</c>'s payload, the conductor's own operator message — would lose the whole
/// document over one word. Null instead, refused one layer up by
/// <see cref="ReviewVerdictSchema.TryParseForReviewContract"/>, so the document still reads and the
/// refusal still happens.
/// </remarks>
internal sealed class TolerantReviewDecisionConverter : JsonConverter<ReviewDecision?>
{
    public override ReviewDecision? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Consumed whole, for the same reason the instrument converter does it: a partially-read token
        // corrupts the parse of every sibling field.
        var element = JsonElement.ParseValue(ref reader);
        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return element.GetString() switch
        {
            { } value when value.Equals("approve", StringComparison.OrdinalIgnoreCase) => ReviewDecision.Approve,
            { } value when value.Equals("block", StringComparison.OrdinalIgnoreCase) => ReviewDecision.Block,
            _ => null,
        };
    }

    /// <remarks>Lower-case, matching what the prompt asks a reviewer to write.</remarks>
    public override void Write(Utf8JsonWriter writer, ReviewDecision? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value == ReviewDecision.Approve ? "approve" : "block");
    }
}

/// <summary>
/// Reads <c>instruments</c> as null rather than throwing whenever it is not a well-formed array of
/// <see cref="VerifyInstrument"/> — including when it is a string, a number, an object, or an array
/// of any of those.
/// <para>
/// Why a converter rather than the plain binding: declaring the property at all is what would
/// otherwise turn a previously-TOLERATED unknown field into a hard parse failure, and
/// <see cref="ReviewVerdictSchema"/> is the single definition of "valid verdict" whose failure mode
/// is a contract-not-satisfied and a retried frontier review. Nothing in the prompt asks a model for
/// this field — the paragraph <c>RoleDispatch.VerifyResultsParagraph</c> adds never names it, and
/// <c>WorkerRoles.json</c>'s verdict example does not carry it — but a field a model invents unasked
/// is exactly the failure the engine's overwrite exists for, so the parse must survive it in whatever
/// shape it was guessed. Nothing is lost by dropping it here:
/// <c>Mutation.VerifyStep.InjectInstrumentsAsync</c> runs on every dispatch or redispatch (#1895)
/// that produced a verdict
/// and either writes the engine's rows or removes the key, so the model's version is never the one a
/// reader sees.
/// </para>
/// </summary>
internal sealed class TolerantVerifyInstrumentListConverter : JsonConverter<IReadOnlyList<VerifyInstrument>?>
{
    public override IReadOnlyList<VerifyInstrument>? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // The element is copied out first so a malformed one can be skipped whole: a partially-consumed
        // reader would corrupt the parse of every sibling field, which is the failure this exists to
        // prevent rather than relocate.
        var element = JsonElement.ParseValue(ref reader);
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        try
        {
            return element.Deserialize<List<VerifyInstrument>>(options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <remarks>
    /// Nothing in the tree serializes a <see cref="ReviewVerdict"/> through STJ today — the engine's
    /// own stamp edits the parsed <c>JsonObject</c> instead — so this is inert, and written by hand
    /// rather than as a delegating <c>Serialize(writer, value, options)</c> for that exact reason:
    /// handing the DECLARED type back to the serializer from inside its own converter is the standard
    /// re-entry trap, and its failure mode is an uncatchable StackOverflowException that no test can
    /// be written against. Serializing one ELEMENT cannot re-enter a converter registered for the
    /// list, so the question does not arise.
    /// </remarks>
    public override void Write(
        Utf8JsonWriter writer, IReadOnlyList<VerifyInstrument>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var instrument in value)
        {
            JsonSerializer.Serialize(writer, instrument, options);
        }

        writer.WriteEndArray();
    }
}

/// <summary>
/// One instrument a review's verdict rests on (#1882): the exact command line the engine ran, the
/// exit code it observed, and how long it took. <see cref="ExitCode"/> is null when the command was
/// killed at the verify step's wall-clock bound; spec/baton.md §9 states why that is absence rather
/// than a sentinel value. Deliberately narrower than <c>Mutation.VerifyCommandResult</c>: no output tail (the room's
/// <c>verify-results.md</c> holds that), so a verdict stays a verdict rather than a second log.
/// </summary>
public sealed record VerifyInstrument(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("exitCode")] int? ExitCode,
    [property: JsonPropertyName("wallClockMs")] long WallClockMs);

/// <summary>One thing a review claims (#732).</summary>
/// <param name="Severity">
/// How much it matters. <b>Nullable so that ABSENT is distinguishable from <c>high</c></b> (#1913
/// review finding 4): STJ binds a missing constructor parameter to its default, and for this value
/// type the default is <see cref="ReviewFindingSeverity.High"/> — so a finding written with no
/// <c>severity</c> silently arrived as the most severe one there is.
/// <see cref="ReviewVerdictSchema.TryParse"/> refuses that document rather than letting the null
/// travel, so every finding a reader ever sees has one; the nullability exists to make the refusal
/// possible, not to admit a severity-less finding downstream.
/// </param>
/// <param name="Claim">The one-line statement of the finding. Required and non-empty.</param>
/// <param name="Status">
/// How far the reviewer verified the claim. <b>Nullable so that ABSENT is distinguishable from
/// <c>confirmed</c></b> (#1919), for the same reason <paramref name="Severity"/> is: the default of
/// this value type is <see cref="ReviewFindingStatus.Confirmed"/>, so a finding written with no
/// <c>status</c> silently arrived as reproduced-and-proven, the one status that tells a reader the
/// claim was checked. <see cref="ReviewVerdictSchema.TryParse"/> refuses that document too.
/// </param>
/// <param name="Anchor">Where in the reviewed code the claim points, when it points anywhere.</param>
/// <param name="Detail">Free-text elaboration — evidence, reproduction, reasoning.</param>
public sealed record ReviewFinding(
    ReviewFindingSeverity? Severity,
    string Claim,
    ReviewFindingStatus? Status,
    ReviewFindingAnchor? Anchor = null,
    string? Detail = null);

/// <summary>A file (and optionally line) a <see cref="ReviewFinding"/> anchors to.</summary>
public sealed record ReviewFindingAnchor(string File, int? Line = null);

/// <summary>
/// How much a <see cref="ReviewFinding"/> matters, in the reviewer's judgment. Three levels on
/// purpose: every finer-grained scale this project has met collapsed to "act / read / skim" in use.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewFindingSeverity>))]
public enum ReviewFindingSeverity
{
    High,
    Medium,
    Low,
}

/// <summary>
/// How far the reviewer verified the claim: <see cref="Confirmed"/> means reproduced or proven
/// against the code, <see cref="Refuted"/> means investigated and found untrue (kept because a
/// refuted suspicion is evidence too), <see cref="Unverified"/> means stated but not checked.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewFindingStatus>))]
public enum ReviewFindingStatus
{
    Confirmed,
    Refuted,
    Unverified,
}

/// <summary>
/// The parse half of the verdict contract: turns bytes on disk into a <see cref="ReviewVerdict"/>
/// or one sentence saying why they aren't one. <c>ContractValidator</c> consults this at
/// execution-complete the same way it evaluates an <see cref="OutputCondition"/>; readers (CLI, UI,
/// tools) use the same method so there is exactly one definition of "valid verdict".
/// </summary>
public static class ReviewVerdictSchema
{
    /// <summary>
    /// Case-insensitive on property names and enum values — the writers are vendor CLI workers,
    /// and losing a verdict to <c>"high"</c> vs <c>"High"</c> would fail runs over nothing.
    /// Unknown extra fields are tolerated (a worker may annotate; the schema names what must be
    /// there, not all that may be).
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// True with a non-null <paramref name="verdict"/> when <paramref name="bytes"/> parse and
    /// pass the semantic floor; false with a human-readable <paramref name="error"/> otherwise.
    /// Never throws on bad content — a worker wrote these bytes, and worker-controlled content
    /// must land as a classified failure, not an escaped exception.
    /// </summary>
    public static bool TryParse(byte[] bytes, out ReviewVerdict? verdict, out string? error)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        verdict = null;

        ReviewVerdict? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ReviewVerdict>(bytes, Options);
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }

        if (parsed is null)
        {
            error = "The verdict document is JSON null.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.ReviewedRef))
        {
            error = "'reviewedRef' must name what was reviewed (a branch, commit, or PR).";
            return false;
        }

        // STJ binds an absent constructor parameter to its default — null here, despite the
        // non-nullable declaration — rather than throwing, so the shape floor is enforced by hand.
        if (parsed.Findings is null)
        {
            error = "'findings' must be present — an empty array when the review found nothing.";
            return false;
        }

        for (var i = 0; i < parsed.Findings.Count; i++)
        {
            var finding = parsed.Findings[i];
            if (finding is null)
            {
                error = $"findings[{i}] is null.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(finding.Claim))
            {
                error = $"findings[{i}].claim must be a non-empty one-line statement.";
                return false;
            }

            // The same deserializer leniency, on a VALUE type, where it is worse: an absent severity
            // binds to default(ReviewFindingSeverity) = High rather than to null, so a finding with
            // no severity would read as the most severe one there is -- and #1901's cost-ledger row
            // counts these into a durable accounting field where 0 is a measurement. Refused rather
            // than defaulted or dropped: an unknown severity ("critical") already fails this parse,
            // and a missing one is no more readable than a wrong one. The review prompt in
            // WorkerRoles.json names the field and gives it in the example, so this asks a worker for
            // nothing new (#1913 review finding 4).
            if (finding.Severity is null)
            {
                error = $"findings[{i}].severity must be one of high, medium or low.";
                return false;
            }

            // Status has the identical hazard: default(ReviewFindingStatus) = Confirmed, so a finding
            // nobody checked would read as reproduced-and-proven against the code -- the one status
            // that tells a reader the claim was verified (#1919).
            if (finding.Status is null)
            {
                error = $"findings[{i}].status must be one of confirmed, refuted or unverified.";
                return false;
            }

            if (finding.Anchor is { Line: < 1 })
            {
                error = $"findings[{i}].anchor.line must be 1 or greater when present.";
                return false;
            }

            // The same deserializer leniency the findings check above guards against: File is
            // declared non-nullable, and STJ will happily bind an anchor without one. Found by the
            // schema's own first live reviewer.
            if (finding.Anchor is not null && string.IsNullOrWhiteSpace(finding.Anchor.File))
            {
                error = $"findings[{i}].anchor.file must name a file when an anchor is present.";
                return false;
            }
        }

        verdict = parsed;
        error = null;
        return true;
    }

    /// <summary>
    /// <see cref="TryParse"/> plus the one thing the review ROLE requires beyond a readable document:
    /// a <see cref="ReviewVerdict.Decision"/>. This is what <c>ContractValidator</c> evaluates for
    /// <c>OutputSchema.ReviewVerdict</c>; spec/baton.md §13 is the ruling that makes the field
    /// mandatory and says what it costs.
    /// </summary>
    /// <remarks>
    /// <b>A second method rather than a check inside <see cref="TryParse"/>, and the split is the
    /// point.</b> "Valid verdict" is still defined once, here, in this class — this method is that
    /// definition plus a role requirement, not a second reader. What the extra layer buys the readers
    /// downstream is spec/baton.md §13's paragraph, not restated here. What it buys the code is the
    /// contrast with severity and status, which ARE refused inside <see cref="TryParse"/>: a null
    /// there is silently WRONG rather than absent, since their enum defaults read as <c>high</c> and
    /// <c>confirmed</c>. A null decision reads as exactly what it is.
    /// </remarks>
    public static bool TryParseForReviewContract(byte[] bytes, out ReviewVerdict? verdict, out string? error)
    {
        if (!TryParse(bytes, out verdict, out error))
        {
            return false;
        }

        if (verdict!.Decision is null)
        {
            verdict = null;
            error = "'decision' must be \"approve\" or \"block\" — the review's own routing decision, "
                + "which nothing derives from the findings.";
            return false;
        }

        return true;
    }
}
