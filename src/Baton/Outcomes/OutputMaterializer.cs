using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Outcomes;

/// <summary>
/// #1594's safety net, reworked to the conductor-writes shape (owner ruling, 2026-09-01, on #1606):
/// when a worker finished naturally, exit 0, but a declared output is simply absent — the measured
/// agy failure mode, worker completes the real work and never writes its contract file — this calls
/// <see cref="IWorkerResponseParser"/> (see that interface's own doc comment for what it recovers and
/// why) and <b>extracts and attaches</b> the response: it writes it into an engine-owned, dot-prefixed
/// file in the execution's own output directory (permitted — <see cref="ReservedOutputNames"/>'s dot
/// namespace is reserved against <em>declared</em> outputs, not against engine writes) and hands the
/// caller the name it used plus which declared outputs are still unsatisfied.
/// </summary>
/// <remarks>
/// <para>
/// This class itself never writes into a declared output name. An engine-written file sitting under
/// a declared name would satisfy the contract at the filesystem level — even a room later relabelled
/// indeterminate would look Succeeded to every directory-reading check, which is the exact detection
/// gap the original (pre-ruling) design reintroduced. The declared output directory stays untouched
/// by this class; its emptiness IS the honest state until a conductor's own recorded resolution
/// writes the real file. #1608: <c>baton resolve</c> (<c>Mutation.MutationInterface.RecordCaptureResolutionAsync</c>)
/// is that one permitted writer — it never re-implements this class's gating, since reaching an
/// unresolved capture at all already proves the gate below passed; it only reads
/// <see cref="StripCapturedResponseHeader"/>'s output and writes it under the declared name(s) this
/// method already recorded as unsatisfied.
/// </para>
/// <para>
/// Deliberately narrow, same eligibility as before the rework — only the effect changed, not when it
/// fires. Only fires when <b>every</b> unsatisfied output is
/// <see cref="UnsatisfiedOutputReason.Missing"/> — a present-but-wrong file
/// (<see cref="UnsatisfiedOutputReason.NotJson"/>, <see cref="UnsatisfiedOutputReason.SchemaViolation"/>,
/// a failed <see cref="UnsatisfiedOutputReason.ConditionFailed"/>) is a different failure than "never
/// written", and a capture alongside it would blur two distinct diagnoses into one fact — AND every
/// missing name looks like prose (see <see cref="IsProseSafeName"/>). Both checks now describe what
/// the captured response MAY later be resolved into (<c>docs/dispatch.md</c>'s prose-safe/all-or-
/// nothing rules), not what this method is allowed to write, but the predicate is unchanged: a
/// structured output can never be honestly satisfied by free text, so a contract mixing a structured
/// miss with a prose-safe one refuses the capture rather than attach a response that can only resolve
/// half the contract. Nothing here is silent: <see cref="OutcomeClassifier"/> logs a loud line for
/// every capture it accepts, and the captured file name plus the unsatisfied output names are carried
/// onto the durable <see cref="Domain.FlowEvent.ExecutionFailed"/> record — the room fact — so
/// "the worker's response was captured, awaiting resolution" stays falsifiable from the room record
/// alone, independent of which verdict the execution ultimately settles to.
/// </para>
/// </remarks>
public static class OutputMaterializer
{
    /// <summary>
    /// The engine-owned file a captured response lands in, dot-prefixed per
    /// <see cref="ReservedOutputNames"/>'s reserved namespace so it can never collide with a declared
    /// output name. One per execution output directory — the directory is already execution-scoped,
    /// so a fixed name is enough.
    /// </summary>
    public const string CapturedResponseFileName = ".captured-response.md";

    /// <summary>
    /// Prepended to the captured file, so a reader who opens it — or greps the room's output directory —
    /// can tell at a glance this is the engine's own extraction, not a worker-written deliverable, and
    /// that the declared output(s) it stands in for are still unwritten pending conductor resolution.
    /// </summary>
    public const string CapturedResponseHeader =
        "<!-- captured from the worker's terminal result envelope by baton (#1594); the worker did not "
        + "write its declared output(s), which remain unwritten in this directory; a conductor must "
        + "resolve this capture before the contract can be satisfied -->";

    /// <summary>
    /// Extensions a free-text response can honestly stand in for, once a conductor resolves this
    /// capture into a declared output. Deliberately a narrow allowlist rather than "anything without a
    /// declared <see cref="OutputSchema"/>/<see cref="OutputCondition"/>" (second-reader review,
    /// #1594): several shipped roles declare a structured-by-*extension*, not by-schema, output —
    /// <c>orchestrate</c>'s <c>turn-actions.json</c> and <c>janitor</c>'s <c>branch.diff</c> both carry
    /// <c>Schema: None</c> in <c>WorkerRoles.json</c> today, yet neither can honestly hold prose. A
    /// bare name (no extension) is treated as prose-safe.
    /// </summary>
    private static readonly HashSet<string> ProseSafeExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".txt" };

    /// <summary>
    /// Whether a declared <see cref="OutputSchema"/> is a shape free text can honestly BE. The test
    /// this half of the predicate is really asking (see the call site) is "could a captured response
    /// ever satisfy this output", and until #2043 the code asked "does it declare a schema at all",
    /// which was the same question while every schema was structured.
    /// <see cref="OutputSchema.NonEmptyText"/> broke that equivalence: it asserts nothing about shape
    /// beyond "the worker said something", and a capture only ever fires on a non-whitespace response
    /// (<see cref="TryCaptureFinalResponse"/>'s own guard), so a captured response satisfies it by
    /// construction.
    /// Reading it as structured would refuse a capture for exactly the read-shaped, prose-only roles
    /// the capture exists for.
    /// </summary>
    private static bool IsProseSatisfiable(OutputSchema schema) =>
        schema is OutputSchema.None or OutputSchema.NonEmptyText;

    private static bool IsProseSafeName(string outputName)
    {
        var extension = Path.GetExtension(outputName);
        return extension.Length == 0 || ProseSafeExtensions.Contains(extension);
    }

    /// <summary>
    /// #1608: strips <see cref="CapturedResponseHeader"/>'s leading engine banner (plus the blank
    /// line separating it from the response body) from a captured file's content, for
    /// <c>baton resolve --accept-capture</c> — the header's own wording ("the worker did not write its
    /// declared output(s), which remain unwritten in this directory") would be a false statement if it
    /// landed verbatim under a declared name once that name IS written. Idempotent: content that does
    /// not start with the header (already stripped, or never carried one) is returned unchanged rather
    /// than throwing, since a resolve verb refusing to accept a capture over a formatting mismatch
    /// would be a worse failure than writing the content as-is.
    /// </summary>
    public static string StripCapturedResponseHeader(string content)
    {
        var prefix = CapturedResponseHeader + "\n\n";
        return content.StartsWith(prefix, StringComparison.Ordinal) ? content[prefix.Length..] : content;
    }

    /// <summary>
    /// A successful capture. <see cref="FileName"/> is the engine-owned, dot-prefixed file the
    /// response landed in (<see cref="CapturedResponseFileName"/> today — always that one name, since
    /// the containing directory is already execution-scoped). <see cref="UnsatisfiedOutputNames"/> is
    /// the declared output name(s) <see cref="FileName"/> stands in for — the pre-capture
    /// <see cref="ContractValidator.Validate"/> result's <see cref="UnsatisfiedOutput.Name"/>s, never
    /// re-validated after the write, since the declared output directory never changes.
    /// <para>
    /// This is the one place that pairing is explained — <see cref="OutcomeClassifier.OutcomeClassification"/>,
    /// <see cref="Domain.FlowEvent.ExecutionFailed"/>, <see cref="Domain.StepState"/>, and
    /// <see cref="Status.WorkflowStatusStepView"/> each carry the same two facts one hop further
    /// downstream (classification → durable event → projected state → the JSON surface a conductor
    /// reads), and each is non-null on exactly the same condition this record's own existence encodes:
    /// a capture happened. None of those four restates why.
    /// </para>
    /// </summary>
    public sealed record CapturedResponse(string FileName, IReadOnlyList<string> UnsatisfiedOutputNames);

    /// <summary>
    /// Attempts to capture the worker's final response for every missing declared output in
    /// <paramref name="validation"/>, from the execution's captured <c>.stdout.log</c>. Returns null
    /// when nothing qualified (no parser, a mixed failure population, a declared
    /// <see cref="OutputSchema"/> or <see cref="OutputCondition"/> in the missing set, a missing name
    /// that isn't prose-safe, no stream log, the vendor's terminal line carried no usable response, or
    /// the write itself failed) — every null return leaves the output directory exactly as it was.
    /// </summary>
    public static CapturedResponse? TryCaptureFinalResponse(
        ContractValidationResult validation,
        WorkerContract contract,
        string outputDirectory,
        IWorkerResponseParser? responseParser)
    {
        if (validation.IsSatisfied || responseParser is null)
        {
            return null;
        }

        // Mixed failure population (some Missing, some NotJson/ConditionFailed/SchemaViolation/
        // MalformedCondition): a present-but-wrong output is a different, real failure this method
        // must never blur into a single capture, so nothing is captured rather than guessing which
        // half the response would resolve.
        if (validation.UnsatisfiedOutputs.Any(u => u.Reason != UnsatisfiedOutputReason.Missing))
        {
            return null;
        }

        // A missing output declaring a Schema or a Condition, or whose own name isn't prose-safe, can
        // never be honestly resolved from prose later -- capturing a response that can only ever
        // satisfy half a multi-output contract is not worth the room fact it would cost. (Second-reader
        // review, #1594: a multi-output contract, e.g. `review`'s report.md + verdict.json, would
        // otherwise record a capture that can never actually resolve verdict.json.) GroupBy/First
        // rather than ToDictionary: a duplicate declared output name is a pre-existing authoring defect
        // nothing upstream rejects (ContractValidator.cs's own FormatException precedent), and this
        // method must not be the one that turns it into a crash between the worker exiting and the
        // outcome being appended.
        var outputByName = contract.ProducedOutputs
            .GroupBy(o => o.Name)
            .ToDictionary(g => g.Key, g => g.First());
        if (validation.UnsatisfiedOutputs.Any(u =>
                !IsProseSafeName(u.Name)
                || (outputByName.TryGetValue(u.Name, out var declared)
                    && (!IsProseSatisfiable(declared.Schema) || declared.Condition is not null))))
        {
            return null;
        }

        var response = TryReadFinalResponse(outputDirectory, responseParser);
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var content = CapturedResponseHeader + "\n\n" + response;
        var path = Path.Combine(outputDirectory, CapturedResponseFileName);
        try
        {
            File.WriteAllText(path, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            // Best effort, same posture as ExecutionStreamLogger's own chunk writer: a transient
            // sharing failure must not corrupt the classification that follows -- falls through to
            // today's Missing failure, exactly as if capture had never been attempted. Logged, never
            // swallowed silently (CLAUDE.md's error-handling rule). The inner try/catch below is review
            // F6's guard -- see OutcomeClassifier.Classify's own F6 catch for why this path needs one.
            try
            {
                Console.Error.WriteLine(
                    $"Warning: #1594 capture failed to write '{CapturedResponseFileName}' in '{outputDirectory}': {ex.Message}.");
            }
            catch (IOException)
            {
                // Console itself is unwritable (review F6, see above) -- the warning above already
                // documents that this is best-effort; nothing further to do here except not throw.
            }

            return null;
        }

        return new CapturedResponse(CapturedResponseFileName, validation.UnsatisfiedOutputs.Select(u => u.Name).ToList());
    }

    /// <summary>
    /// Reads the execution's own <c>.stdout.log</c> (<see cref="ExecutionStreamLogger"/>) — the same,
    /// full-fidelity, per-execution capture <see cref="Status.ExecutionUsageProjector"/> already reads
    /// for token attribution — and reads the final non-blank line. A parser may explicitly recognize
    /// vendor-defined terminal trailers, and the scan walks back over as MANY consecutive trailers as
    /// it meets before requiring a response — one line on the shape codex had at #1594 (its final agent
    /// message precedes <c>turn.completed</c>), two or more since #2020 put a <c>turn.usage</c> line
    /// per model round-trip into the same stream. The walk is bounded by the trailer whitelist, not by
    /// a line count: any unrecognized line still refuses extraction rather than triggering an arbitrary
    /// backward search. What keeps a whitelist-bounded walk inside one turn is stated at
    /// <c>Status.CodexUsageParser.RoundTripField</c>, which records both the normal case and the one
    /// shape (a crash-recovery resubmit) where a capture file carries two invocations.
    /// Never <see cref="CoreDispatchResult.StdoutTail"/>: that field is capped at
    /// <see cref="CoreDispatcher.MaxRetainedStderrLength"/> (2000 characters), far short of a worker's
    /// real final report.
    /// </summary>
    private static string? TryReadFinalResponse(string outputDirectory, IWorkerResponseParser responseParser)
    {
        var stdoutPath = Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName);
        if (!File.Exists(stdoutPath))
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(stdoutPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            if (responseParser.TryParseFinalResponse(lines[i], out var response))
            {
                return response;
            }

            if (!responseParser.IsPostResponseTerminalLine(lines[i]))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The file read this shares with <see cref="OutcomeClassifier"/>'s substantial-work evidence
    /// (#1586 S1 review F5): same path, same missing-file/IO-exception bail, same "last non-blank
    /// line only" contract. Extracted so the two readers cannot drift apart on what counts as the
    /// worker's final line while still parsing it differently — <see cref="TryReadFinalResponse"/>
    /// above and <see cref="OutcomeClassifier"/>'s own call site each apply their own vendor-specific
    /// parser to the line this returns.
    /// </summary>
    internal static string? TryReadLastNonBlankLine(string outputDirectory)
    {
        var stdoutPath = Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName);
        if (!File.Exists(stdoutPath))
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(stdoutPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return null;
    }
}
