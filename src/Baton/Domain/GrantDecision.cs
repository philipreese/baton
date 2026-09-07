using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baton.Domain;

/// <summary>
/// One allow/deny decision a Baton gate took about one tool call, as a structured room fact (#2009).
/// <para>
/// <b>Why it exists.</b> Until this landed, the only trace a refusal left in a room was the refusal
/// SENTENCE, carried inside the vendor's own tool result — so "how often does the broker refuse a call,
/// and does the worker retry the refused one" could only be asked as a text search over tool output.
/// The 2026-09-06 cross-vendor audit ran that search and got a claude median of 9 % and a max of 57 %,
/// every high scorer being a room doing Baton work ON the refusal system: rooms whose <c>Read</c>/
/// <c>Grep</c> results were this repository's own hook sources, and one whose 97 hits were its own PR
/// text. A decision is now a line of its own, so both questions are a filter on
/// <see cref="EventType"/> rather than an inference from prose.
/// </para>
/// <para>
/// <b>Where the line lands is per enforcement point, and there are three</b> (spec/baton.md §9, "Where
/// a tool rule is enforced"): the codex broker writes it into the room's captured stream beside its own
/// <c>item.completed</c>, because it IS that stream's writer; the two <c>PreToolUse</c> hooks are
/// subprocesses of the vendor CLI that never touch that file, so they append the same line to
/// <see cref="Baton.Dispatch.GrantDecisionLog"/>'s per-execution file. One schema — this record's
/// <see cref="ToJsonNode"/> — and two sinks, never two schemas.
/// </para>
/// </summary>
/// <param name="Vendor">
/// The gate's vendor, in the same lowercase spelling the hooks' own vendor tags use (<c>claude</c>,
/// <c>agy</c>, <c>codex</c>). A line is read out of a room whose vendor a reader would otherwise have to
/// look up elsewhere.
/// </param>
/// <param name="Tool">The tool name as the vendor sent it, or <see cref="UnknownTool"/> when the gate
/// refused before it could read one — which is a real population (an empty or malformed payload).</param>
/// <param name="Allowed">The decision itself. Rendered as <c>"allow"</c>/<c>"deny"</c>.</param>
/// <param name="Rule">
/// Which rule decided, from <see cref="GrantRules"/>. Several sites share an id on purpose: the id names
/// the RULE, not the line, and <paramref name="Reason"/> separates the members.
/// </param>
/// <param name="Reason">
/// The sentence the worker was given, on a deny; null on an allow, where there is no reason to carry and
/// every byte is a byte of the room's stream (see <see cref="Baton.Dispatch.ExecutionStreamLogger"/>'s
/// rollover bound).
/// </param>
/// <param name="Input">
/// The call's normalised input identity — <see cref="Identify"/>'s digest, never the arguments. Two
/// lines with the same vendor, tool and input are the same call, which is what makes "the worker retried
/// the refused call" answerable without carrying a file's contents into the stream twice.
/// </param>
/// <param name="At">When the gate decided.</param>
public sealed record GrantDecision(
    string Vendor,
    string Tool,
    bool Allowed,
    string Rule,
    string? Reason,
    string Input,
    DateTimeOffset At)
{
    /// <summary>
    /// The <c>type</c> every one of these lines carries. <c>type</c> rather than the <c>event</c> of
    /// #2009's illustrative sketch: every line already in a room's captured stream keys on <c>type</c>
    /// (<c>turn.started</c>, <c>item.completed</c>, <c>turn.usage</c>), and a second discriminator name
    /// for the same file would be a reader's problem forever to save one word here.
    /// </summary>
    public const string EventType = "baton.grant";

    /// <summary>The tool name recorded when the gate never got one out of the payload.</summary>
    public const string UnknownTool = "unknown";

    /// <summary><see cref="Input"/> for a call whose gate had no identifying argument to digest.</summary>
    public const string NoInput = "-";

    /// <summary>
    /// A short, stable fingerprint of whatever identifies one call — a command line, a path, or a
    /// vendor's raw arguments JSON. The same construction, and the same caveats, as the broker's
    /// <c>item.started</c> arguments digest: not reversible by design, not a security boundary, and no
    /// normalisation beyond what the caller does, so two spellings of one command key apart. SHA-256
    /// truncated to 16 hex characters, because the only question asked of it is equality.
    /// </summary>
    public static string Identify(string? raw) =>
        string.IsNullOrEmpty(raw)
            ? NoInput
            : Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];

    /// <summary>
    /// The single rendering of this schema, in the shape the codex broker's emitter takes. Every other
    /// sink goes through <see cref="ToJsonLine"/>, which is this plus a newline.
    /// </summary>
    public JsonObject ToJsonNode()
    {
        var line = new JsonObject
        {
            ["type"] = EventType,
            ["vendor"] = Vendor,
            ["tool"] = Tool,
            ["decision"] = Allowed ? "allow" : "deny",
            ["rule"] = Rule,
            ["input"] = Input,
            ["at"] = At.ToUniversalTime().ToString("O"),
        };
        if (!Allowed && Reason is { Length: > 0 })
        {
            line["reason"] = Reason;
        }

        return line;
    }

    /// <summary>One JSONL record: <see cref="ToJsonNode"/> on one line, no trailing newline.</summary>
    public string ToJsonLine() => ToJsonNode().ToJsonString(SerializerOptions);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// The rule ids a <see cref="GrantDecision"/> can name — the whole vocabulary, in one place, so a
/// reader grouping a room's denials is grouping over a closed set rather than over whatever string each
/// call site invented (#2009).
/// <para>
/// <b>Deliberately coarse.</b> An id names the rule that decided, not the sentence it produced: the
/// three shell rungs that refuse an unparseable line, a standing deny and a line outside a scoped grant
/// all report <see cref="ShellPattern"/>, because they are one rule with three members and the reason
/// text is what tells them apart. Splitting them here would make every count a count of message
/// wording.
/// </para>
/// </summary>
public static class GrantRules
{
    /// <summary>No rule refused the call. The only id an allow line carries.</summary>
    public const string Allowed = "allow";

    /// <summary>The tool, or the whole category it belongs to, is withheld by this role's grant.</summary>
    public const string WithheldTool = "withheld-tool";

    /// <summary>A read whose path is outside every root this grant makes readable.</summary>
    public const string PathOutsideRoots = "path-outside-roots";

    /// <summary>A granted write whose target is outside both the workspace and the outbox.</summary>
    public const string WriteOutsideBounds = "write-outside-bounds";

    /// <summary>A path that crosses a symbolic link or reparse point.</summary>
    public const string ReparsePoint = "reparse-point";

    /// <summary>A shell command line refused by this role's allow/deny pattern set.</summary>
    public const string ShellPattern = "shell-pattern";

    /// <summary>A shell command carrying an option token this role denies outright (#1683).</summary>
    public const string DeniedOptionToken = "denied-option-token";

    /// <summary>A command shaped to run in the background rather than to completion (#2002 rule 1).</summary>
    public const string Backgrounding = "backgrounding";

    /// <summary>A repeat of a call whose answer cannot have changed (#2002 rules 2/2b).</summary>
    public const string Repeat = "repeat";

    /// <summary>A <c>gh pr</c> read of a pull request this room did not open (#2001).</summary>
    public const string OwnPullRequestOnly = "own-pr-only";

    /// <summary>
    /// The fail-closed population: a call the gate could not judge and refused rather than allowed
    /// unchecked — unreadable or empty stdin, malformed JSON, a missing tool name, an absent or
    /// wrong-vendor channel, an unmeasured argument, an outbox path it cannot resolve.
    /// </summary>
    public const string UnjudgeableCall = "unjudgeable-call";

    /// <summary>The gate's own catch-all: a defect in the gate denied the call (#1921).</summary>
    public const string GateInternalFailure = "gate-internal-failure";
}
