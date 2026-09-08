using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Vendors;

/// <summary>
/// One thing a vendor CLI supports — a skill, a slash command, or a mode — surfaced by
/// <see cref="IWorkerAdapter.DiscoverCapabilitiesAsync"/> (M24 Phase 2) so a user can invoke it
/// instead of only typing plain prose.
/// </summary>
public sealed record WorkerCapabilityItem(
    string Name,
    string Kind,
    string Description,
    string? ParameterHint = null)
{
    /// <summary>
    /// Whether a user can pick this item and send/insert it — <c>"command"</c>/<c>"skill"</c>/<c>"agent"</c>
    /// are actions; a <c>"mode"</c> is a permission-scope label and a <c>"plugin"</c> is something
    /// already imported into the vendor CLI, so both are informational only. Vendor-kind semantics,
    /// so the classification lives here with the kinds themselves (0020 clause 1, #615) rather than
    /// in whichever surface happens to render the picker.
    /// </summary>
    public bool IsInvokable => Kind is "command" or "skill" or "agent";
}

/// <summary>
/// The full set of skills/commands/modes and selectable models a vendor CLI reports for a given
/// (optional) working directory, as returned by <see cref="IWorkerAdapter.DiscoverCapabilitiesAsync"/>.
/// </summary>
public sealed record WorkerCapabilities(
    string Vendor,
    IReadOnlyList<WorkerCapabilityItem> Items,
    IReadOnlyList<string> Models);

/// <summary>
/// One canonical, vendor-agnostic fact about an in-flight turn, recovered from a raw stdout line by
/// <see cref="IWorkerAdapter.TryParseProgressEvent"/> (M24 Phase 1's live in-turn streaming). This is
/// the seam Adapter Isolation actually requires here: a vendor's streaming JSON envelope (e.g.
/// Claude's <c>stream-json</c> shape) is interpreted once, inside the adapter that understands it,
/// and everything downstream (the daemon's WebSocket push, a future UI) only ever sees this shape.
/// </summary>
/// <param name="Kind">
/// <c>"status"</c> (a lifecycle marker — session started, waiting on the vendor), <c>"text"</c> (a
/// chunk of the assistant's reply, complete or partial per <paramref name="IsPartial"/>),
/// <c>"tool"</c> (the assistant is invoking a tool — <paramref name="Text"/> names it),
/// <c>"result"</c> (issue #1561 — a turn's completion status/error summary, e.g. why a lane failed),
/// or <c>"ignore"</c> (issue #1561 — the adapter recognized this envelope and deliberately decided it
/// carries no signal worth rendering, e.g. a claude `thinking`-only content block or an agy
/// <c>step_update</c> ACTIVE edge; a consumer stays quiet on this Kind, distinct from <c>false</c>
/// from <see cref="IWorkerAdapter.TryParseProgressEvent"/> itself, which means the adapter did not
/// recognize the envelope at all — a consumer that never swallows should echo verbatim on <c>false</c>
/// but stay quiet on <c>"ignore"</c>). Deliberately a small closed-ish vocabulary a UI can switch on,
/// not the vendor's own raw event-type string.
/// </param>
/// <param name="Text">Human-readable text for this event — the delta/message text for <c>"text"</c>, the tool name for <c>"tool"</c>, or a short status/result label. Never null.</param>
/// <param name="IsPartial">
/// True for a token-level delta that will be followed by more text in the same turn (Claude's
/// <c>--include-partial-messages</c> stream events); false for a complete, already-whole unit (a
/// full assistant message block, a status marker). A renderer appends partial text in place and
/// starts a new line on a non-partial one.
/// </param>
public sealed record WorkerProgressEvent(string Kind, string Text, bool IsPartial = false);

/// <summary>
/// Maps a <see cref="WorkerInvocation"/> and its paired <see cref="WorkerContract"/> to a
/// <see cref="CoreDispatchTarget"/> — the seam CLAUDE.md's Adapter Isolation rule requires. Every
/// vendor quirk (flag vocabulary, cwd handling, stdin redirection, shell-wrapping to reference
/// <c>BATON_INPUT_&lt;n&gt;</c>/<c>BATON_OUTPUT_DIR</c>) lives behind an implementation of this
/// interface; <c>Baton</c> never learns a vendor exists.
/// </summary>
public interface IWorkerAdapter : Baton.Outcomes.IFailureClassifier, Baton.Status.IWorkerUsageParser, Baton.Outcomes.IWorkerResponseParser
{
    /// <summary>
    /// Resolves <paramref name="invocation"/> and <paramref name="contract"/> into the concrete
    /// command <c>Baton.Dispatch.CoreDispatcher</c> spawns. Called once per worker-binding config
    /// entry, not per execution — see <see cref="WorkerInvocation"/>'s remarks for why the result
    /// must not embed a resolved, execution-specific file path.
    /// </summary>
    CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract);

    /// <summary>
    /// Checks an explicit model using this adapter's offline rules, throwing a
    /// <see cref="BatonFlowException"/> on refusal. The default validates nothing; adapters without
    /// model rules inherit it. See <c>spec/baton.md</c> §13 for queue add's candidate checks.
    /// </summary>
    void ValidateRequestedModel(string model) { }

    /// <summary>
    /// Discovers the capabilities (skills, commands, models) this vendor's CLI actually supports
    /// (M24 Phase 2). Implementations that need to shell out to the CLI itself (e.g. Gemini's
    /// <c>agy models</c>) must do so here, not on the caller's thread — this is async precisely so
    /// an ASP.NET request thread never blocks on process I/O.
    /// </summary>
    Task<WorkerCapabilities> DiscoverCapabilitiesAsync(string? workingDirectory = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new WorkerCapabilities("unknown", Array.Empty<WorkerCapabilityItem>(), Array.Empty<string>()));

    /// <summary>
    /// Attempts to interpret one raw stdout line from a live dispatch as a <see cref="WorkerProgressEvent"/>
    /// (M24 Phase 1's live in-turn streaming) — only ever called for a line captured via the
    /// <see cref="CoreDispatchTarget.OnStdoutLine"/> seam, itself only wired up when the invocation
    /// requested a structured streaming output format. An adapter with no such format (or a line that
    /// doesn't match the expected shape — a stray log line, a partial JSON fragment split across a
    /// buffer boundary) returns false: the default here, since most adapters never produce anything
    /// to parse in the first place.
    /// </summary>
    bool TryParseProgressEvent(string rawLine, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        return false;
    }

    /// <summary>
    /// Attempts to recover this vendor's own session id from one raw stdout line of a completed
    /// dispatch (#1841) — the read-side capture, never a client-minted id (see
    /// <c>ClaudeWorkerAdapter.TryParseSessionId</c> for why minting one at bind time would break a
    /// #1373 retry). False (and a null <paramref name="sessionId"/>) for a line this vendor doesn't
    /// recognize or that carries no session id — the default here, since most adapters have never had
    /// this capability measured.
    /// </summary>
    bool TryParseSessionId(string rawLine, out string? sessionId)
    {
        sessionId = null;
        return false;
    }


    /// <summary>
    /// True when a worker this adapter spawns with <see cref="PermissionGrant.WriteFiles"/> withheld
    /// can nonetheless write its declared <see cref="WorkerContract.ProducedOutputs"/> into
    /// <c>BATON_OUTPUT_DIR</c> (#649). "Withhold writes" means <em>do not modify the workspace</em>; it
    /// was never meant to mean <em>do not produce the artifact you were dispatched for</em>, and a
    /// read-only reviewer is the shape that needs both at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the canonical question, asked once by <c>WorkerBindingResolver</c>; each adapter
    /// answers it in its own vendor's terms, which is what Adapter Isolation requires here. The
    /// mechanisms do not resemble each other: on Claude the write tools stay pre-approved on
    /// <c>--allowedTools</c> and AER's <c>PreToolUse</c> hook confines them to the outbox; <c>agy</c>
    /// answers no for the reason recorded in #670. <c>Baton</c> learns neither mechanism — it learns
    /// only whether a declared output is reachable.
    /// </para>
    /// <para>
    /// <b>Defaults to false, and the direction is deliberate.</b> An adapter that has not been
    /// measured against the outbox path answers "no", so the binding is refused before the run is
    /// paid for (#629) rather than after. A wrong "no" costs a refusal an operator can see and work
    /// around; a wrong "yes" costs a full frontier-model run that returns nothing.
    /// </para>
    /// </remarks>
    bool WithheldWritesReachTheOutbox => false;

    /// <summary>
    /// True when this adapter binds the workspace a lane was <em>dispatched against</em>
    /// (<see cref="WorkerInvocation.WorktreeSourceRepository"/>) readable to the worker, so a
    /// worktree-provisioned lane can still read that directory by absolute path (#1987) — its
    /// uncommitted and staged content included, since the lane's own worktree is only that
    /// repository at <c>HEAD</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked once, by <c>DispatchCommand</c>'s pre-run workspace disclosure, so the sentence an
    /// operator reads before the run is true per adapter rather than per hardcoded vendor list —
    /// the disclosure is the only consumer, and each adapter answers in its own vendor's terms
    /// (Adapter Isolation). Each answering adapter carries its own mechanism and the ruling behind
    /// it; see <c>AgyWorkerAdapter</c>'s override for the one that answers
    /// <see langword="true"/> today.
    /// </para>
    /// <para>
    /// <b>Defaults to false.</b> An adapter that binds nothing but the directory the worker runs in
    /// is the ordinary shape, and the fail-direction is deliberate: a wrong "no" only under-promises
    /// what a lane can see, while a wrong "yes" would tell an operator their uncommitted work was in
    /// scope for a worker that cannot in fact read it.
    /// </para>
    /// </remarks>
    bool BindsDispatchedWorkspaceReadable => false;

    /// <summary>
    /// True when a path component of <paramref name="roomDirectoryPath"/> is one this adapter's own
    /// vendor CLI treats as sensitive and refuses to write under, regardless of the grant AER hands
    /// it — in which case <paramref name="offendingComponent"/> names the literal matching component.
    /// Answers <see langword="false"/> (and a <see langword="null"/> component) when the adapter has
    /// no such rule (#599).
    /// </summary>
    /// <remarks>
    /// Measured on claude only, corrected by #1827/#1834 from an earlier (2.1.220) config-root-value
    /// reading: the refusal keys on a path component literally named <c>.claude</c> appearing anywhere
    /// in the target path, not on the value of <c>CLAUDE_CONFIG_DIR</c>. A <c>CLAUDE_CONFIG_DIR</c>
    /// override pointed at an arbitrarily-named directory let the write through with no refusal on the
    /// same CLI build, and the refusal fired identically with <c>CLAUDE_CONFIG_DIR</c> unset entirely
    /// so long as some ancestor component was named <c>.claude</c> — even though
    /// <see cref="WithheldWritesReachTheOutbox"/> is <see langword="true"/> and the tool is otherwise
    /// pre-approved. Comparison is case-insensitive on Windows, ordinal elsewhere; a component such as
    /// <c>.claude-foo</c> or <c>.claudex</c> does not match. On CLI 2.1.258 the refused write's own
    /// signature is "no artifact plus an ask-for-approval sentence" under headless <c>-p</c>, not a
    /// named "which is a sensitive file" string — silent from AER's own exit code (0, natural exit);
    /// only the worker's own transcript names it. Every <c>agy</c> dispatch from the same scratch root
    /// in the session that first measured this succeeded, so this is deliberately per-adapter rather
    /// than a general room-directory constraint — an adapter that has not been measured against its own
    /// vendor CLI answers <see langword="false"/>, and <see langword="false"/> means the CLI-side
    /// refusal does NOT fire for it. That is the opposite fail-direction from
    /// <see cref="WithheldWritesReachTheOutbox"/>'s <see langword="false"/> default, and deliberately so:
    /// this member names a vendor-native refusal that was measured to exist for one vendor, not a
    /// capability AER must prove before trusting; refusing every unmeasured adapter here would block
    /// agy dispatches that were measured to succeed (#1823 review).
    /// </remarks>
    bool HasSensitiveOutputPathComponent(string roomDirectoryPath, out string? offendingComponent)
    {
        offendingComponent = null;
        return false;
    }
}
