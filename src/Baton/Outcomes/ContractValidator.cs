using System.Globalization;
using System.Text.Json;
using Baton.Domain;

namespace Baton.Outcomes;

/// <summary>
/// Walks a <see cref="WorkerContract"/>'s <c>ProducedOutputs</c> and asserts each is satisfied on
/// disk: the file must exist and, if it declares an <see cref="OutputCondition"/>,
/// the JSON Pointer in that condition must resolve to a value equal to the condition's literal.
/// Exit code 0 is necessary but not sufficient for <c>ExecutionSucceeded</c> — this is the
/// "sufficient" half the <see cref="OutcomeClassifier"/> consults.
/// </summary>
public static class ContractValidator
{
    /// <summary>
    /// How much of a single resolved JSON Pointer target a diagnostic will render. Small on purpose:
    /// the point is which output failed and roughly how, not the value's full text — that is in the
    /// artifact, which the diagnostic names.
    /// </summary>
    private const int MaxRenderedValueLength = 60;

    /// <summary>
    /// True when every entry in <paramref name="contract"/>'s <c>ProducedOutputs</c> exists at
    /// <paramref name="outputDirectory"/> and satisfies its declared <see cref="OutputCondition"/>,
    /// if any.
    /// </summary>
    /// <remarks>
    /// Stops at the first unsatisfied output, which <see cref="Validate"/> deliberately does not.
    /// That is not an optimisation, it is the pre-existing contract of this method and dropping it
    /// changed behaviour: <c>NonProcessCompletionDetector</c> calls this on every scheduling round
    /// for every unfinalized non-process execution, so evaluating outputs past the first failure
    /// both re-reads and re-parses files needlessly and — the real defect — reaches
    /// <c>TryResolvePointer</c> on outputs it never used to touch, which <b>throws</b>
    /// <see cref="FormatException"/> for a pointer not starting with <c>/</c>. A malformed pointer on
    /// a later output would have escaped into the pump on every round. Caught by an independent
    /// reviewer, who flagged it as unverifiable without the pre-change file and was right.
    /// </remarks>
    public static bool IsSatisfied(WorkerContract contract, string outputDirectory) =>
        Validate(contract, outputDirectory, stopAtFirstFailure: true).IsSatisfied;

    /// <summary>
    /// Same check as <see cref="IsSatisfied"/>, but reports which outputs are unsatisfied and why —
    /// missing, unparseable JSON, or a condition that didn't hold — instead of collapsing all three
    /// into a single <c>false</c>. Reports every unsatisfied output, since the delta is the
    /// diagnostic value.
    /// </summary>
    public static ContractValidationResult Validate(WorkerContract contract, string outputDirectory) =>
        Validate(contract, outputDirectory, stopAtFirstFailure: false);

    private static ContractValidationResult Validate(
        WorkerContract contract,
        string outputDirectory,
        bool stopAtFirstFailure)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        List<UnsatisfiedOutput>? unsatisfied = null;

        foreach (var output in contract.ProducedOutputs)
        {
            var path = Path.Combine(outputDirectory, output.Name);
            if (!File.Exists(path))
            {
                (unsatisfied ??= []).Add(new UnsatisfiedOutput(output.Name, UnsatisfiedOutputReason.Missing));
                if (stopAtFirstFailure)
                {
                    break;
                }

                continue;
            }

            if (output.Schema != OutputSchema.None)
            {
                var schemaFailure = TryGetSchemaViolation(output.Name, output.Schema, path);
                if (schemaFailure is not null)
                {
                    // One report per output, like Missing: a file that is not its declared shape is
                    // already condemned, and also evaluating its condition would name the same
                    // output twice in one diagnostic.
                    (unsatisfied ??= []).Add(schemaFailure);
                    if (stopAtFirstFailure)
                    {
                        break;
                    }

                    continue;
                }
            }

            if (output.Condition is null)
            {
                continue;
            }

            var conditionFailure = TryGetUnsatisfiedCondition(output.Name, output.Condition, path);
            if (conditionFailure is not null)
            {
                (unsatisfied ??= []).Add(conditionFailure);
                if (stopAtFirstFailure)
                {
                    break;
                }
            }
        }

        return unsatisfied is null
            ? ContractValidationResult.Satisfied
            : new ContractValidationResult(unsatisfied);
    }

    /// <summary>
    /// Evaluates a declared <see cref="OutputSchema"/>: parse-only, per decision 0043 —
    /// the file either is the declared shape or the output is unsatisfied with the parser's own
    /// sentence as detail. Content is never interpreted beyond parsing.
    /// </summary>
    private static UnsatisfiedOutput? TryGetSchemaViolation(string outputName, OutputSchema schema, string path)
    {
        var error = schema switch
        {
            // The bare parse. `decision` is deliberately not checked here — what it would cost every
            // review room engine-wide is on ReviewVerdictSchema, and the ruling is spec/baton.md §13's.
            OutputSchema.ReviewVerdict =>
                ReviewVerdictSchema.TryParse(File.ReadAllBytes(path), out _, out var parseError) ? null : parseError,
            OutputSchema.Diff =>
                UnifiedDiffSchema.TryParse(File.ReadAllBytes(path), out _, out var parseError) ? null : parseError,
            _ => throw new ArgumentOutOfRangeException(nameof(schema), schema, "Unknown OutputSchema case."),
        };

        return error is null
            ? null
            : new UnsatisfiedOutput(outputName, UnsatisfiedOutputReason.SchemaViolation, Detail: error);
    }

    private static UnsatisfiedOutput? TryGetUnsatisfiedCondition(string outputName, OutputCondition condition, string path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (JsonException)
        {
            // A condition may only be declared on a JSON output. A file that fails
            // to parse as JSON fails the condition, exactly like a missing file — but distinguishably so.
            return new UnsatisfiedOutput(outputName, UnsatisfiedOutputReason.NotJson);
        }

        using (document)
        {
            var expected = DescribeScalar(condition.EqualsValue);

            // A pointer not starting with '/' makes TryResolvePointer throw, and nothing validates
            // OutputCondition.Path when a workflow is parsed — it is a plain record. Before #597 the
            // classifier short-circuited on the first unsatisfied output, so a malformed pointer on a
            // *later* output was often never evaluated; listing every output removed that accidental
            // shielding and turned an authoring mistake into a FormatException escaping
            // OutcomeClassifier.Classify — after the process ran, before its outcome was appended,
            // i.e. a crash-recovery orphan on every run. Reporting it is also just the right answer:
            // naming a condition AER cannot evaluate is exactly what this issue is about.
            JsonElement resolved;
            bool didResolve;
            try
            {
                didResolve = TryResolvePointer(document.RootElement, condition.Path, out resolved);
            }
            catch (FormatException ex)
            {
                return new UnsatisfiedOutput(
                    outputName,
                    UnsatisfiedOutputReason.MalformedCondition,
                    condition.Path,
                    ActualValue: null,
                    ExpectedValue: expected,
                    Detail: ex.Message);
            }

            if (!didResolve)
            {
                return new UnsatisfiedOutput(
                    outputName,
                    UnsatisfiedOutputReason.ConditionFailed,
                    condition.Path,
                    ActualValue: null,
                    ExpectedValue: expected);
            }

            if (ScalarEquals(resolved, condition.EqualsValue))
            {
                return null;
            }

            return new UnsatisfiedOutput(
                outputName,
                UnsatisfiedOutputReason.ConditionFailed,
                condition.Path,
                ActualValue: DescribeElement(resolved),
                ExpectedValue: expected);
        }
    }

    /// <summary>Resolves an RFC 6901 JSON Pointer against a parsed document.</summary>
    private static bool TryResolvePointer(JsonElement root, string pointer, out JsonElement resolved)
    {
        resolved = root;

        if (pointer.Length == 0)
        {
            return true;
        }

        if (pointer[0] != '/')
        {
            throw new FormatException($"JSON Pointer '{pointer}' must start with '/' (RFC 6901).");
        }

        var current = root;
        foreach (var rawToken in pointer[1..].Split('/'))
        {
            var token = rawToken.Replace("~1", "/").Replace("~0", "~");

            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(token, out var property))
            {
                current = property;
            }
            else if (current.ValueKind == JsonValueKind.Array &&
                     int.TryParse(token, out var index) &&
                     index >= 0 &&
                     index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                return false;
            }
        }

        resolved = current;
        return true;
    }

    private static bool ScalarEquals(JsonElement resolved, JsonScalar expected) => expected switch
    {
        JsonScalar.String s => resolved.ValueKind == JsonValueKind.String && resolved.GetString() == s.Value,
        JsonScalar.Number n => resolved.ValueKind == JsonValueKind.Number && resolved.GetDouble() == n.Value,
        JsonScalar.Boolean b => resolved.ValueKind is JsonValueKind.True or JsonValueKind.False && resolved.GetBoolean() == b.Value,
        JsonScalar.Null => resolved.ValueKind == JsonValueKind.Null,
        _ => throw new ArgumentOutOfRangeException(nameof(expected), expected, "Unknown JsonScalar case."),
    };

    /// <summary>Renders a condition's expected literal for a human-readable diagnostic.</summary>
    private static string DescribeScalar(JsonScalar scalar) => scalar switch
    {
        JsonScalar.String s => $"\"{s.Value}\"",
        JsonScalar.Number n => n.Value.ToString(CultureInfo.InvariantCulture),
        JsonScalar.Boolean b => b.Value ? "true" : "false",
        JsonScalar.Null => "null",
        _ => throw new ArgumentOutOfRangeException(nameof(scalar), scalar, "Unknown JsonScalar case."),
    };

    /// <summary>
    /// Renders a resolved JSON Pointer target for a human-readable diagnostic, clamped so that one
    /// value cannot consume the whole reason.
    /// </summary>
    /// <remarks>
    /// A JSON Pointer may resolve to an object or array, and <see cref="JsonElement.GetRawText"/> on
    /// one is unbounded. Clamping <b>here</b>, per value, rather than only on the assembled reason is
    /// what keeps the diagnostic honest: a single 5 KB value truncated at the end would silently drop
    /// every other unsatisfied output from the list, and would render <c>123456789012</c> as
    /// <c>1234</c> — a confident wrong answer where there used to be no answer. The marker is
    /// attached to the value it belongs to, so a shortened value can never read as a complete one.
    /// </remarks>
    private static string DescribeElement(JsonElement element)
    {
        var rendered = element.ValueKind switch
        {
            JsonValueKind.String => $"\"{element.GetString()}\"",
            _ => element.GetRawText(),
        };

        return ClampRenderedValue(rendered);
    }

    /// <summary>
    /// N1 (#1664 re-review): the same per-value clamp <see cref="DescribeElement"/> uses, exposed so
    /// <see cref="Workspaces.WorktreeProvisioner.DescribeStrayPaths"/> can bound each stray path the
    /// identical way rather than growing a second copy of the "one long value must not eat the whole
    /// reason" rule.
    /// </summary>
    internal static string ClampRenderedValue(string value) =>
        value.Length <= MaxRenderedValueLength
            ? value
            : string.Concat(TrimWithoutSplittingSurrogatePair(value, MaxRenderedValueLength), "…(truncated)");

    /// <summary>
    /// Keeps the last <paramref name="length"/> characters of <paramref name="value"/>, cutting from
    /// the front. The mirror of <see cref="TrimWithoutSplittingSurrogatePair"/>, and here for the
    /// reason that one's own doc gives: the surrogate-safe truncation rule lives in one file so two
    /// copies cannot drift.
    /// </summary>
    internal static string KeepLastWithoutSplittingSurrogatePair(string value, int length)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length <= length)
        {
            return value;
        }

        var start = value.Length - length;

        // If the first surviving char is a low surrogate, its high half is inside the removed
        // prefix — drop the orphan rather than emitting half a pair. The `start < value.Length`
        // guard mirrors the `cut > 0` guard in TrimWithoutSplittingSurrogatePair and exists for the
        // same reason: it is unreachable while the callers' caps are positive, but lowering a
        // display cap to zero is an ordinary later edit and an unguarded index would throw out of
        // outcome classification — while recording a failure, which is the worst place to throw.
        if (start < value.Length && char.IsLowSurrogate(value[start]))
        {
            start++;
        }

        return value[start..];
    }

    /// <summary>
    /// Cuts to at most <paramref name="length"/> chars without leaving a lone high surrogate — a
    /// non-BMP character is two UTF-16 chars and splitting one produces malformed UTF-16, which here
    /// would be written into an append-only journal. Worker-controlled JSON reaches this. Public (not
    /// internal) because <c>Baton.Cli</c>'s <c>DispatchOptionsParser.SanitizeLabel</c> (#1499) needs
    /// the identical surrogate-safe cut for <c>--label</c> — the rule stays in this one file either
    /// way, which is the point; only its accessibility widened.
    /// </summary>
    public static string TrimWithoutSplittingSurrogatePair(string value, int length)
    {
        // N1 (#1664 re-review): a caller-computed length (a reason's remaining budget minus an
        // already-assembled suffix) can go negative when the suffix alone overruns the cap. Clamping
        // here, not just in each caller, is what keeps `value[..cut]` from throwing regardless of how
        // many callers this gains.
        if (length <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= length)
        {
            return value;
        }

        var cut = length;
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return value[..cut];
    }
}

/// <summary>
/// Which of a <see cref="WorkerContract"/>'s <c>ProducedOutputs</c> failed <see cref="ContractValidator.Validate"/>,
/// and why — the detail <see cref="ContractValidator.IsSatisfied"/> discards by collapsing to <c>bool</c>.
/// </summary>
/// <remarks>
/// <see cref="IsSatisfied"/> is derived rather than a constructor parameter so the two cannot
/// disagree. As a parameter, <c>new ContractValidationResult(true, [somethingUnsatisfied])</c>
/// compiled and lied — on a public type, where a caller outside this assembly could build it.
/// </remarks>
public sealed record ContractValidationResult(IReadOnlyList<UnsatisfiedOutput> UnsatisfiedOutputs)
{
    public static readonly ContractValidationResult Satisfied = new([]);

    public bool IsSatisfied => UnsatisfiedOutputs.Count == 0;
}

/// <summary>The three genuinely different ways a declared <see cref="ProducedOutput"/> can go unsatisfied.</summary>
public enum UnsatisfiedOutputReason
{
    /// <summary>No file exists at the output's declared path.</summary>
    Missing,

    /// <summary>The file exists but does not parse as JSON, so its <see cref="OutputCondition"/> (if any) cannot be evaluated.</summary>
    NotJson,

    /// <summary>The file parsed as JSON, but its <see cref="OutputCondition"/>'s JSON Pointer either did not resolve or resolved to a value other than the expected one.</summary>
    ConditionFailed,

    /// <summary>
    /// The condition itself is not evaluable — its JSON Pointer is malformed, so the output can be
    /// neither satisfied nor meaningfully compared. A workflow-authoring fault rather than a worker
    /// one, and reported rather than thrown so it names itself instead of escaping the classifier.
    /// </summary>
    MalformedCondition,

    /// <summary>
    /// The file exists but does not parse as its declared <see cref="OutputSchema"/>.
    /// <see cref="UnsatisfiedOutput.Detail"/> carries the parser's one-sentence why.
    /// </summary>
    SchemaViolation,
}

/// <summary>
/// One <see cref="ProducedOutput"/> that failed validation. <see cref="ConditionPath"/>,
/// <see cref="ActualValue"/>, and <see cref="ExpectedValue"/> are populated only for
/// <see cref="UnsatisfiedOutputReason.ConditionFailed"/>; <see cref="ActualValue"/> is null within
/// that case when the pointer didn't resolve at all, as distinct from resolving to a mismatched value.
/// </summary>
/// <param name="Detail">
/// Extra explanation for a reason whose cause is not implied by the other fields — currently only
/// <see cref="UnsatisfiedOutputReason.MalformedCondition"/>, where it carries why the pointer could
/// not be parsed. Null otherwise.
/// </param>
public sealed record UnsatisfiedOutput(
    string Name,
    UnsatisfiedOutputReason Reason,
    string? ConditionPath = null,
    string? ActualValue = null,
    string? ExpectedValue = null,
    string? Detail = null);
