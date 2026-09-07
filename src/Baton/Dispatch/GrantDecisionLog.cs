using Baton.Domain;

namespace Baton.Dispatch;

/// <summary>
/// The hook-side sink for <see cref="GrantDecision"/> lines (#2009): one NDJSON file per execution,
/// in that execution's own output directory, appended to by the <c>PreToolUse</c> subprocess that took
/// the decision.
/// <para>
/// <b>Why a file rather than the room's captured stream.</b> The codex broker writes its decisions
/// straight into that stream because it is that stream's writer. A hook is a subprocess of the vendor
/// CLI: its stdout is the vendor's protocol channel and its stderr is the refusal text the model reads,
/// so neither can carry a Baton fact, and <c>.stdout.log</c> itself is
/// <see cref="ExecutionStreamLogger"/>'s alone — that file's whole invariant is that it is a byte
/// prefix of what the worker emitted, which a second writer would destroy. The only thing a hook shares
/// with the engine is the output directory it is handed in <c>BATON_OUTPUT_DIR</c>, which is exactly
/// how <c>RepeatedToolCallHook</c> already reaches a room.
/// </para>
/// <para>
/// <b>Not the agy verdict ledger, and that was checked rather than assumed.</b> The obvious candidate
/// was the file the engine already folds in, <c>.agy-hook-verdicts.ndjson</c> — but it exists on agy
/// alone (claude's hook writes no ledger at all), it is armed only when a grant asks for the #1680
/// canary, and <see cref="HookVerdictLedger.CountLines"/> counts EVERY non-blank line in it as one
/// canary verdict. Grant lines there would silently inflate that count. This file is the second
/// mechanism artifact a hook writes into a room, not a fourth channel: it is listed beside the others in
/// <see cref="ExecutionStreamLogger.IsStreamLogFileName"/>, and unlike them its name is declared HERE,
/// in the layer that filter lives in, so there is no second copy of the literal to drift.
/// </para>
/// <para>
/// <b>Every failure is silent, and that is the fail-safe direction.</b> This log observes decisions; it
/// does not take them. Both hooks wrap their decision in a catch that DENIES, so an exception escaping
/// this class would turn an unwritable diagnostic into a refused tool call — a strictly worse outcome
/// than a missing line. Same posture, and the same stated exception to the repo's rethrow rule, as
/// <c>AgyHookCheckCommand.AppendVerdictLedgerLine</c> and <c>RepeatedToolCallHook</c>.
/// </para>
/// </summary>
public static class GrantDecisionLog
{
    /// <summary>
    /// Dot-prefixed, like every other engine-owned artifact that lands in a worker's output directory,
    /// and registered in <see cref="ExecutionStreamLogger.IsStreamLogFileName"/> beside them.
    /// <para>
    /// <b>That registration is what a future listing will read, not something acting today</b> (#2009
    /// review LOW). No production caller applies that filter yet, so what keeps this file out of a
    /// deliverable listing today is that there is no such listing — the leading dot is what makes it
    /// undeclarable as an output meanwhile. <see cref="Baton.Domain.ReservedOutputNames"/> records the
    /// filter's caller-less state once (#1724); this comment does not restate it.
    /// </para>
    /// </summary>
    public const string FileName = ".baton-grants.ndjson";

    /// <summary>
    /// Where this execution's grant lines go, or null when this process cannot say. A non-rooted
    /// directory is the #668 failure: a relative path inside a hook subprocess resolves against a
    /// working directory nobody chose, so the rung is off rather than writing somewhere arbitrary.
    /// </summary>
    public static string? ResolvePath(string? outputDirectory) =>
        string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathRooted(outputDirectory)
            ? null
            : Path.Combine(outputDirectory, FileName);

    /// <summary>Appends one decision as one line. Never throws — see this type's own remarks.</summary>
    public static void Append(string? outputDirectory, GrantDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (ResolvePath(outputDirectory) is not { } path)
        {
            return;
        }

        try
        {
            File.AppendAllText(path, decision.ToJsonLine() + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
        }
    }
}

/// <summary>
/// One hook invocation's grant-decision context (#2009): the vendor and room are fixed for the life of
/// the subprocess, the tool and its input identity are learned once the payload is parsed, and the
/// decision itself is recorded from whichever rung decides.
/// <para>
/// It exists so a deny site inside a 600-line <c>Decide</c> can name its rule in one token —
/// <c>Deny(scribe, stderr, reason, GrantRules.ShellPattern)</c> — without threading the vendor, the
/// output directory, the tool name and the input through every one of them. <see cref="Tool"/> and
/// <see cref="Input"/> deliberately default to "we never got that far", because the fail-closed denials
/// that fire before the payload parses are real decisions and must still produce a line.
/// </para>
/// </summary>
public sealed class GrantDecisionScribe(string vendor, string? outputDirectory, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The tool this call names, once the gate has read it.</summary>
    public string Tool { get; set; } = GrantDecision.UnknownTool;

    /// <summary>
    /// The raw text identifying this call — the command line, the write target, the read request. Set
    /// by the gate as it parses; digested by <see cref="GrantDecision.Identify"/> when a line is
    /// written, never recorded verbatim.
    /// </summary>
    public string? Input { get; set; }

    public void Allow() => Write(true, GrantRules.Allowed, null);

    public void Deny(GrantRule rule, string reason) => Write(false, rule, reason);

    private void Write(bool allowed, GrantRule rule, string? reason) =>
        GrantDecisionLog.Append(outputDirectory, new GrantDecision(
            vendor, Tool, allowed, rule, reason, GrantDecision.Identify(Input), _clock.GetUtcNow()));
}
