using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// A logical execution target (e.g. <c>claude</c>, <c>agy</c>, <c>git</c>) bound to a typed
/// contract, not a vendor name. A <see cref="WorkflowStepDefinition"/> declares which
/// contract it requires; the concrete binary is resolved via configuration external to the
/// workflow.
/// </summary>
public sealed record WorkerContract(
    string WorkerName,
    IReadOnlyList<string> RequiredInputs,
    IReadOnlyList<ProducedOutput> ProducedOutputs,
    IReadOnlyList<string> OptionalMetadata);

/// <summary>
/// The one statement of why a declared output name may not begin with a dot, and the one wording
/// every rejection uses (#1345).
/// <para>
/// Four production sites reject the same thing — <see cref="ProducedOutput"/>'s constructor,
/// <c>WorkflowDefinitionValidator</c>, <c>WorkerBindingConfigParser</c> and
/// <c>WorkerRoleCatalog</c> — and each had written the sentence out again, in three different
/// phrasings, with three test files asserting a substring of it. All four also said the namespace
/// was "reserved for engine stream logs", which undersells the rule: the whole leading-dot namespace
/// is reserved, not just the names
/// <see cref="Baton.Dispatch.ExecutionStreamLogger.IsStreamLogFileName"/> happens to use today.
/// </para>
/// <para>
/// The reservation covers <em>declaring</em> an output. A worker may still write an undeclared
/// dot-named file into its output directory, and that file is a document like any other — which is
/// why the stream-log filter is four exact names rather than a prefix test.
/// </para>
/// <para>
/// #1724 review LOW: <see cref="Baton.Dispatch.ExecutionStreamLogger.IsStreamLogFileName"/> itself has
/// no production caller today — it is declared for the directory-read surfaces that would need to
/// filter it out and do not exist yet, since nothing in <c>src/</c> enumerates an execution's output
/// directory today (<c>deliverables_list</c> is a pushed index, not a directory read).
/// </para>
/// </summary>
public static class ReservedOutputNames
{
    public const string LeadingDot = ".";

    public static bool IsReserved(string? name) => name is not null && name.StartsWith(LeadingDot, StringComparison.Ordinal);

    /// <summary>The shared rejection clause. Callers prefix it with their own context.</summary>
    public const string RejectionClause =
        "a declared output cannot start with '.' — that namespace is reserved for engine-written files, such as ExecutionStreamLogger's stream logs";

    /// <summary>
    /// #1594 review F8: a declared output name is combined with an execution's own output directory
    /// with no further sanitization, by every reader today (<c>ContractValidator.Validate</c>,
    /// <c>StepOutputResolver.Resolve</c>) and by any future writer against the same directory — the
    /// conductor resolution verb included, once it exists. A name carrying a directory separator or a
    /// <c>..</c> segment could steer that combined path outside the room the contract was validated
    /// against; <see cref="Path.GetFileName"/> returning anything other than <paramref name="name"/>
    /// unchanged is exactly that case. This ships on Windows CI only (#1405) and the check is
    /// evaluated against <see cref="Path.GetFileName(string)"/>'s own platform behaviour, so it
    /// recognizes both <c>/</c> and <c>\</c> as separators there; it is not verified against a POSIX
    /// runtime, where <see cref="Path.GetFileName(string)"/> treats <c>\</c> as an ordinary filename
    /// character rather than a separator and a <c>\</c>-escaped traversal would not be caught.
    /// </summary>
    public static bool IsPathTraversal(string? name) =>
        !string.IsNullOrEmpty(name) && Path.GetFileName(name) != name;

    /// <summary>The shared rejection clause for <see cref="IsPathTraversal"/>. Callers prefix it with their own context.</summary>
    public const string PathTraversalRejectionClause =
        "a declared output name must be a bare file name — no directory separators or '..' segments, which could steer a write outside the execution's output directory";
}

/// <summary>A named output file role a <see cref="WorkerContract"/> requires.</summary>
/// <param name="Schema">
/// A declared document shape the file must parse as (decision 0043) — the structural
/// sibling of <paramref name="Condition"/>. Serialized only when set, so contracts that predate
/// the field round-trip byte-identically.
/// </param>
public sealed record ProducedOutput
{
    public string Name { get; init; } = string.Empty;
    public OutputCondition? Condition { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OutputSchema Schema { get; init; }

    [JsonConstructor]
    public ProducedOutput(string Name, OutputCondition? Condition = null, OutputSchema Schema = OutputSchema.None)
    {
        if (ReservedOutputNames.IsReserved(Name))
        {
            throw new ArgumentException(
                $"ProducedOutput name '{Name}' is invalid: {ReservedOutputNames.RejectionClause}.",
                nameof(Name));
        }

        if (ReservedOutputNames.IsPathTraversal(Name))
        {
            throw new ArgumentException(
                $"ProducedOutput name '{Name}' is invalid: {ReservedOutputNames.PathTraversalRejectionClause}.",
                nameof(Name));
        }

        this.Name = Name ?? string.Empty;
        this.Condition = Condition;
        this.Schema = Schema;
    }
}

/// <summary>
/// The closed set of shapes a <see cref="ProducedOutput"/> can declare. Validation is
/// parse-only in every case: the engine checks the file <i>is</i> the shape, and never reads its
/// content to route (Architecture Rule 1; decision 0043's boundary).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OutputSchema>))]
public enum OutputSchema
{
    /// <summary>No declared shape — existence (plus any <see cref="OutputCondition"/>) suffices.</summary>
    None,

    /// <summary>The output must parse per <see cref="ReviewVerdictSchema.TryParse"/>.</summary>
    ReviewVerdict,

    /// <summary>The output must parse per <see cref="UnifiedDiffSchema.TryParse"/>.</summary>
    Diff,

    /// <summary>
    /// #2043: the output must exist AND carry at least one non-whitespace character. The floor
    /// <see cref="None"/> does not provide — a zero-byte or whitespace-only file satisfies
    /// existence, so a role whose whole deliverable is prose (a consolidation, a report) settles
    /// <c>Succeeded</c> having said nothing. <c>spec/baton.md</c> §9 rules why this stays inside the
    /// parse-only boundary the type remarks above draw, and what it costs the capture path.
    /// </summary>
    NonEmptyText,
}

/// <summary>
/// Extends a <see cref="ProducedOutput"/>'s contract from "this file must exist" to "this file
/// must exist and say this". Satisfied only when the file exists, parses as JSON, the
/// <paramref name="Path"/> JSON Pointer resolves, and the resolved value equals
/// <paramref name="EqualsValue"/>.
/// </summary>
/// <param name="EqualsValue">
/// Named <c>EqualsValue</c> rather than <c>Equals</c> — a record positional parameter named
/// <c>Equals</c> collides with the record's synthesized <c>Equals</c> method (CS0102). Serializes
/// under the spec's own field name, <c>equals</c>.
/// </param>
public sealed record OutputCondition(string Path, [property: JsonPropertyName("equals")] JsonScalar EqualsValue);
