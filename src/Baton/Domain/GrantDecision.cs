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
/// <b>Where the line lands follows the enforcement point that took the decision</b>, of which
/// spec/baton.md §9 names three. A broker decision goes into the room's captured stream beside the
/// <c>item.completed</c> for the same call, since the broker is that stream's own writer; a
/// <c>PreToolUse</c> hook is a subprocess of the vendor CLI that never touches that file, so it
/// appends to <see cref="Baton.Dispatch.GrantDecisionLog"/>'s per-execution file instead. One
/// schema — this record's <see cref="ToJsonNode"/> — and two sinks, never two schemas.
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
/// Which rule decided — a <see cref="GrantRule"/>, so the only ids that exist are the ones
/// <see cref="GrantRules"/> declares. Several sites share one on purpose: the id names the RULE, not
/// the line, and <paramref name="Reason"/> separates the members.
/// </param>
/// <param name="Reason">
/// The sentence the worker was given, on a deny; null on an allow, where there is no reason to carry and
/// every byte is a byte of the room's stream (see <see cref="Baton.Dispatch.ExecutionStreamLogger"/>'s
/// rollover bound).
/// </param>
/// <param name="Input">
/// The call's input identity — <see cref="Identify"/>'s digest, never the text itself, so "the worker
/// retried the refused call" is answerable without carrying a file's contents into the stream twice.
/// <para>
/// <b>What is digested is NOT the same on every enforcement point, and the name does not say so
/// (#2009 review LOW).</b> The two hooks digest the ONE field that identifies the call, the one
/// <see cref="Baton.Dispatch.GrantDecisionScribe.Input"/> names per tool; the codex broker digests the
/// whole arguments object as codex serialized it, file content and all. That is deliberate: its line must key
/// equal to the <c>item.started</c> digest it already emitted for the same call, which is
/// arguments-wide, and two copies of one call under two identities would be worse than one identity
/// that is wider on one vendor. The consequence a reader has to carry is that equality is STRICTER on
/// codex: a write re-issued with one byte changed keys as a different call there and as the same call
/// on claude/agy, so "did it re-issue the refused call" is comparable within a vendor and not across
/// them. <c>docs/dispatch.md</c> states that limit where a reader meets the query.
/// </para>
/// </param>
/// <param name="At">When the gate decided.</param>
public sealed record GrantDecision(
    string Vendor,
    string Tool,
    bool Allowed,
    GrantRule Rule,
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
            ["rule"] = Rule.Id,
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
/// One member of the rule vocabulary <see cref="GrantRules"/> declares — the id a
/// <see cref="GrantDecision"/> carries in its <c>rule</c> field, as a type rather than a bare string.
/// <para>
/// <b>Why a type.</b> The vocabulary is only closed if a call site cannot name an id that is not in it.
/// A <c>string rule</c> parameter makes every refusal site name SOMETHING — which is what the three
/// enforcement points already required — but a typo or an invented spelling compiles, and lands in a
/// bucket nobody groups on. The constructor is <c>internal</c> and there is no public way to mint or
/// derive a value (<see cref="Id"/> has no setter, so <c>with</c> cannot reach it), so
/// <see cref="GrantRules"/>' members are the only instances the vendor and CLI layers can pass. This is
/// the ONE enforcement of that closure: no test greps for rule literals, because the compiler is the
/// stronger instrument and two enforcements of one rule is the drift this repo's record-once gate names.
/// </para>
/// </summary>
public sealed record GrantRule
{
    internal GrantRule(string id) => Id = id;

    /// <summary>The id as it is written on a grant line, and as a reader groups on it.</summary>
    public string Id { get; }

    /// <summary>The id, so a rule interpolates into a message as the reader will see it.</summary>
    public override string ToString() => Id;
}

/// <summary>
/// The rule ids a <see cref="GrantDecision"/> can name — the whole vocabulary, in one place, so a
/// reader grouping a room's denials is grouping over a closed set rather than over whatever string each
/// call site invented (#2009). <see cref="GrantRule"/>'s own remarks state what makes the set closed
/// rather than merely conventional.
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
    public static readonly GrantRule Allowed = new("allow");

    /// <summary>The tool, or the whole category it belongs to, is withheld by this role's grant.</summary>
    public static readonly GrantRule WithheldTool = new("withheld-tool");

    /// <summary>A read whose path is outside every root this grant makes readable.</summary>
    public static readonly GrantRule PathOutsideRoots = new("path-outside-roots");

    /// <summary>A granted write whose target is outside both the workspace and the outbox.</summary>
    public static readonly GrantRule WriteOutsideBounds = new("write-outside-bounds");

    /// <summary>A path that crosses a symbolic link or reparse point.</summary>
    public static readonly GrantRule ReparsePoint = new("reparse-point");

    /// <summary>A shell command line refused by this role's allow/deny pattern set.</summary>
    public static readonly GrantRule ShellPattern = new("shell-pattern");

    /// <summary>A shell command carrying an option token this role denies outright (#1683).</summary>
    public static readonly GrantRule DeniedOptionToken = new("denied-option-token");

    /// <summary>A command shaped to run in the background rather than to completion (#2002 rule 1).</summary>
    public static readonly GrantRule Backgrounding = new("backgrounding");

    /// <summary>A repeat of a call whose answer cannot have changed (#2002 rules 2/2b).</summary>
    public static readonly GrantRule Repeat = new("repeat");

    /// <summary>A <c>gh pr</c> read of a pull request this room did not open (#2001).</summary>
    public static readonly GrantRule OwnPullRequestOnly = new("own-pr-only");

    /// <summary>
    /// The fail-closed population: a call the gate could not judge and refused rather than allowed
    /// unchecked — unreadable or empty stdin, malformed JSON, a missing tool name, an absent or
    /// wrong-vendor channel, an unmeasured argument, an outbox path it cannot resolve.
    /// </summary>
    public static readonly GrantRule UnjudgeableCall = new("unjudgeable-call");

    /// <summary>The gate's own catch-all: a defect in the gate denied the call (#1921).</summary>
    public static readonly GrantRule GateInternalFailure = new("gate-internal-failure");
}
