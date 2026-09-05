# Baton: the worker-room layer — spec v2.0

**Status: settled, revision cycle 2 of #1396.** Target reader: an agent-harness author integrating
against Baton — dispatching lanes, polling completion, reading structured output. Not the Baton
team, not a human app user. This document is the top-level statement of what the system is.

Every claim below either cites a real path in this tree or is marked **(new build)** — settled
direction not yet implemented — or `UNVERIFIED — fill from code`, where I could not confirm a shape
by reading the file. There are no open decision boxes left in this document: the two the prior
draft carried (a surviving dev diagnostic UI; whether remote dispatch is in scope) are both closed
below, in §6 and §10.

**On citations.** Per the register posture (§11), this document cites only code paths and its own
sections. It does not cite decision records, design docs, or the old room/behavioral specs — those
are being deleted, not archived, and a spec that outlives its sources cannot depend on them. Every
rule the previous register held is restated here, in full, as this document's own rule.

---

## §1 Identity

Baton is a headless, vendor-neutral engine that runs vendor CLI agents — Claude Code, `agy` — as
**workers** inside **rooms**, under a durable, replayable journal. It is not an interactive product.
It has no chat surface, no daily-driver UI, no resident conversational partner. The thing that drives
it is an **agent harness**: a program that calls `baton dispatch`, polls for a completion sentinel, and
reads structured output. The harness is the user this spec is written for.

Two invariants govern everything below:

- **Routing never reads conversation content.** Flow's scheduling logic reads structured outcomes —
  exit codes, declared outputs, explicit tool returns — never the meaning of what a worker said. This
  is a design invariant held by review, not a gated property: `Baton.Architecture.Tests` (kept, per the
  Appendix) pins the reference-direction half, and its own header states that no static test can
  honestly assert the no-content-reads half — do not cite it as enforcement of this bullet.
- **The journal is the system of record.** Every state a room can be in is a projection of recorded
  events; the system cannot be in a state it has not recorded. §2 states plainly that this is now
  true of *two* journals, not one, and what each one is for.

What Baton is **not**, stated as exclusions (§10 expands each):

- Not a chat product. Chat is one internal *workflow shape* a room can run, not a product surface a
  person opens.
- Not session-parity with a phone or desktop app. There is no daily-driver client this spec assumes
  exists, and none of `Baton.Ui`, `Baton.Ui.Core`, `Baton.Mobile`, or `Baton.Sidecar` survives this reset
  (Appendix).
- Not an orchestrator that decides on a human's behalf by default. §5 states the harness-facing gate
  contract directly: exactly one gate, closed exactly one way.
- Not a UI product. Fleet Glass (§6) is the entire observability surface, full stop — not "at most a
  dev diagnostic surface pending a decision." That decision is made: Fleet Glass, extended with a
  two-level drill-down, is a diagnostic surface built as **(new build)** levels of the MCP tool
  itself, never a second application.

---

## §2 The dispatch unit

A **room** is one working directory: `~/.baton/rooms/<room>/` (`BatonPaths.Rooms`,
`src/Baton/Status/BatonPaths.cs`). One directory may contain several repositories; the room does
not know or care.

A room holds, at minimum: `room.json` (the room-kind marker — `BatonPaths.RoomMetadataFileName`,
`BatonPaths.cs`; absence reads as a workflow room), `bindings.json` (the standing worker grant —
`BatonPaths.RoomBindingsFileName`, `BatonPaths.cs`), `flow.jsonl` (the workflow event log —
§3), `artifacts/`, and, once terminal, `terminal.json` (§3). `snapshot.json` is present for any
room that has been dispatched at least once — `fleet_status` treats its absence as "no bound
snapshot" and reports it as an error entry rather than a state (`src/Baton.Cli/Mcp/FleetStatusTool.cs`).

**A named artifact under `artifacts/` is versioned and attributed, never silently overwritten
(#496, decision 0021 — trimmed to engine scope by the 2026-09-01 triage: no diff-and-choose UI,
§1 forecloses that surface).** `artifacts/<name>` stays the CURRENT version, so every pre-#496
reader keeps working unchanged. `RoomArtifacts.Write` (`src/Baton/Artifacts/RoomArtifacts.cs`) is
the one primitive that writes a named artifact: absent → version 1; present and byte-identical →
nothing new; present and different → version `n+1`, written to a sidecar
`artifacts/.versions/<name>/<n>` with one `artifacts/.versions/<name>/index.jsonl` line
(`n`, `producedAt`, `producedBy` — `executionId`/`role`/`adapter`/`model`, supplied by the caller;
`baton deliver` writes role `conductor` with no execution — `sha256`, `bytes`) appended *before*
`artifacts/<name>` is atomically replaced (temp file + move). A dot-directory, per #1351's filter
convention: version history is engine mechanism, not a document a worker or harness should see
enumerated. JSONL, for the same append-only crash-safety shape `flow.jsonl` already relies on. The
index is authoritative over the version files themselves — a version file can land on disk before
the index line that commits it, and every reader (`Versions`, `Read`) treats a version absent from
the index as never having happened, orphan file or not. Only three writers land outside an
execution's own `artifacts/execution_<id>/` scratch directory (decision 0021 point 2's "plumbing,
never surfaced"), so only those route through this primitive: `baton deliver` (`DeliverCommand.cs`)
— a re-delivery of the same `source_path` now versions rather than replacing the prior bytes — and
the `promote-artifact` MCP tool (`PromoteArtifactTool.cs`, #595), a worker-side counterpart: named
bytes a worker points at land in `artifacts/` the same versioned way, with `producedBy` carrying
the calling execution's id (derived from its `BATON_OUTPUT_DIR`, the same structural trust
`MemoryProposalEscalation`'s remarks give an `execution_*` directory name) and `role`/`adapter`/
`model` left null — nothing in the MCP host today knows a calling worker's role or vendor to record
there. `sourcePath` is refused (a structured tool error, nothing written) unless it resolves —
links followed component by component, the same `OutboxPath.IsInside` containment `--fleet-status`
and the workspace-write gates use — inside that calling execution's own scratch tree; a reparse
point pointing outside is denied the same way a plain path outside would be, never followed.
`artifactName` is refused the same way for a path separator, a `..` segment, or a Windows reserved
device-name stem (`CON`/`PRN`/`AUX`/`NUL`/`COM1`-`9`/`LPT1`-`9`, case-insensitive, with or without an
extension) — the last of those throws deep inside `RoomArtifacts.Write`'s temp-file path otherwise.
Composed onto the same host and gated by the same `--memory-proposal-tool` opt-in `MemoryProposalTool`
already uses (`McpCommand.cs`) rather than a second flag, since both are worker-side escalation tools
with the identical `BATON_OUTPUT_DIR` precondition — a precondition `McpCommand` now validates rather
than assumes: `BATON_OUTPUT_DIR` must resolve to an existing room directory's
`artifacts/execution_<id>` (the literal `artifacts` segment, a non-empty id after the `execution_`
prefix, the room directory present on disk), and a value that does not match fails closed with one
line naming the expected shape, never registering the tool.
`RoleSpecMaterializer.CopyAttachmentsIntoRoom`'s `--attach` copy does not: it is harness-supplied
input copied in before any execution exists, not something a worker produced (0021 point 1), so it
has no `ExecutionBindingResolver`-derivable attribution to record — see that method's own remarks.
Everything else that writes under `artifacts/` — `.stdout.log`/`.stderr.log` and their rollovers
(#1351), the `.captured-response.md` engine capture (`OutputMaterializer`), a `baton resolve`-written
declared output, and the `yield`/`memory-edit-proposal` MCP tools' per-execution capture files —
lives inside one execution's own scratch directory, addressed uniquely per execution, so no name is
ever reused across writes and there is nothing for this primitive to version. `RoomRetentionSweep`
/`ArtifactPruner` (`PruneTaskArtifactsAsync`) prune a terminal, non-kept room's `.versions/` the same
pass it prunes `execution_*` directories, down to the current version only — the current file
already holds that version's bytes in full, so nothing is lost, only the history behind it.

**There are two independent event logs, not one, and this spec states both honestly.**
`flow.jsonl` is the workflow ledger — steps, executions, decisions — and everything in §3–§9 below
reads and writes only this one. A **second** ledger, `room.jsonl`, exists in the same engine
(`src/Baton/Domain/RoomEvent.cs`, `src/Baton/Store/RoomEventLogReader.cs`,
`RoomEventLogWriter.cs`, `src/Baton/Projection/RoomProjector.cs`,
`src/Baton/Mutation/RoomMutationInterface.cs`) and its full event vocabulary is: held-work
dispatch/escalation/resolution, grant record/amend/revoke, ask-time escalation, turn-host dormancy
entered/cleared, mid-turn permission ask/answer/revoke, standing-permission revocation, the
workflow on/off switch, worker join/rename, and orchestrator (re)assignment
(`RoomEvent.cs`).

State it plainly: **every one of those event kinds is written only by code this reset deleted.**
The mid-turn permission ask/answer/revoke triad is the deleted ask mechanism (#1417, §5). Held work,
escalation, dormancy, and orchestrator assignment are the resident-orchestrator/wake-loop model
`Baton.Daemon`'s `RoomTurnHost`/`RoomWakeBridge` implement, and that model has no referent left once
the harness — not a resident presence — is the decider (§7). Worker join/rename and the workflow
on/off switch belong to the interactive multi-participant chat room product `Baton.Ui`/`Baton.Mobile`
served. I checked: `src/Baton.Cli` (including its folded-in `Mcp/` verb, the former standalone
Baton.Mcp.Host project, #1458) references none of `RoomMutationInterface`, `RoomEventLogReader`, or `RoomEventLogWriter` — the harness-facing
surface this spec describes has never touched `room.jsonl`, and `fleet_status` reads only the
terminal sentinel, `snapshot.json`, and `flow.jsonl`
(`FleetStatusTool.cs`) — never `room.jsonl`. Its type definitions stay in `Baton` because
Architecture Rule 1 keeps the journal engine-owned regardless of who reads it, and deleting dead
infrastructure is a separate cleanup this document does not scope — but a harness author should read
`room.jsonl` as **inert**: nothing in the dispatch/decide/status/fleet_status surface this spec
describes writes to it or reads from it.

**A `FlowEvent`/`LogEntry` kind this binary has never heard of reads as a newer writer having reached
the journal first, not as damage (#1779).** `FlowEventLogReader` skips such a line and counts it,
reporting the count once per read (first unknown kind named, on the same stderr channel the checkpoint
fallback already uses) instead of failing the whole read, while a KNOWN kind with a lost or renamed
member still throws `FlowEventLogReadException` exactly as before.

**The progress heartbeat and the coarse lifecycle events (#1549).** §7's "false Running ⚠" entry
below records the symptom this closes: journal-event age was useless as a liveness signal because a
healthy lane could go a long stretch without writing to `flow.jsonl` at all. Three additions to the
`FlowEvent` vocabulary fix that at the source. Each is content-free (an `ExecutionId` and nothing
else — the wire timestamp every journal line already carries covers "when"), and each carries its
full producer-side contract as a doc comment on its own case in `FlowEvent.cs` — cited here by name,
not restated:

- **`ExecutionProgress`**. Producer: `Baton.Cli.ExecutionProgressHeartbeat`, a poller
  `RunCommand.ExecuteAsync` starts alongside `CancelRequestPoller` for every `run`/`dispatch`/
  `redispatch` invocation (all three funnel through that one method) but not `resume`/`supply` — the
  same command split `CancelRequestPoller` itself already has. `ExecutionProgressHeartbeat`'s own
  remarks are the canonical description of its cadence, its `.stdout.log`-mtime gate, and why silence
  under a wedge is the intended behaviour rather than a gap. Cadence is env-configurable
  (`BATON_EXECUTION_PROGRESS_INTERVAL_SECONDS` through `BatonEnvironmentSnapshot`, default 5 minutes)
  the same way `RoomRetentionSweep`'s interval knobs already are. Write-budget arithmetic: KV writes
  are governed by the pusher's per-producer daily ledger (§7's #1690 entry — snapshot pushes draw on
  their own fixed daily sub-budget with adaptive pacing, never one write per journal event), so a
  heartbeat changes a coalesced push's *contents*, never its *count*, and spends nothing extra against
  the 1,000/day cap.
- **`CancellationDelivered`** and **`CancellationRejected`**, both scoped to the operator
  `cancel.request` path only (the host-stop wind-down stays as it was — deliberately, to keep an
  ordinary shutdown quiet). `FlowEvent.cs`'s own doc comments on each case are the canonical statement
  of exactly which branch of `InFlightExecutionRegistry`/`CancelRequestPoller` produces them and why
  each is a distinct fact from the pre-existing `CancellationRequested`.
- **Retry scheduled and artifact collection, the other two of the four moments #1537's enumeration
  (PR #1564's body) named, needed nothing new.** Checked against `FlowEvent.cs` directly rather than
  assumed (`common-sense`): `StepRetryScheduled` already exists and is already emitted. An engine-side
  "artifact collection" moment does not exist as a step distinct from the terminal outcome
  classification itself, so a fourth event would fire at the same instant as an existing one — pure
  duplication, the exact journal noise the issue's own per-gate-evaluation exclusion already guards
  against. Deliberately not added; this sentence is that decision's durable record.

All three new cases project as explicit no-ops in `StateProjector`, and — per PR #1564's own audit —
reach every downstream mailbox surface unfiltered without any widening of their own, since nothing
in that pipeline (`extract_timeline`, `RoomDetailTool`'s tag map, `room_detail`'s schema in §6)
filters by event type in the first place.

**Delivery state facts (#734).** A workflow whose deliverable is a PR is "done" in Baton the moment
its final step succeeds — but the deliverable's actual forge state (opened → checks green/red →
merged, or closed unmerged) lives only on GitHub until something records it. Four more `FlowEvent`
cases close that gap, all content-free beyond a PR number and (for the first) the declared branch
name:

- **`DeliveryPrOpened(PullRequestNumber, Branch?)`** — the poller (§7) confirmed, via `gh`, that the
  PR a room's declared delivery reference names exists. Recorded once per room.
- **`DeliveryChecksGreen(PullRequestNumber)`** / **`DeliveryChecksRed(PullRequestNumber)`** — every
  required check on the tracked PR completed non-failing / at least one concluded failing. Neither is
  terminal: a later push can flip the state again, recorded as a fresh fact each time it does.
- **`DeliveryMerged(PullRequestNumber, Merged = false)`** — the PR reached a terminal forge state.
  `Merged: false` is also the closed-unmerged case, recorded once the same way rather than adding a
  fifth event kind for it — and the deliberate default direction: a ledger line that lost this field
  replays as the unremarkable outcome, never as a fabricated merge. Once a room's journal carries one
  of these, the poller never polls that room again.

**Facts only, never actions.** Nothing reads any of the four to make a routing, retry, or merge
decision (Architecture Rule 1) — a red check is recorded, never acted on. All four project as
explicit no-ops in `StateProjector`, the identical shape the three #1549 cases above already
establish, proven the same way (`StateProjectorTests.The_734_delivery_events_never_change_projected_state`).
The declaration half — how a step names a delivery reference in the first place — is
`Status.DeliveryReferenceOutputNames`: two well-known declared-output names (`delivery-branch.txt`,
`delivery-pr.txt`) `Status.DeliveryReferenceResolver` reads back off a room's already-resolved
outputs, the same `verdict.json`-by-name lookup `WatchFireService` already uses for its own payload.
`delivery-pr.txt`'s content may be a bare number or a full PR URL (what `gh pr create` itself prints);
either way `DeliveryReferenceResolver` reads off the trailing digits for `PullRequestNumber`, and the
poller (§7) hands the content on to `gh pr view` verbatim (minus a leading `#`) rather than
reconstructing a reference of its own.

A harness invokes work two ways, both in `src/Baton.Cli/Program.cs`:

- **`baton run <workflow-file> --bindings <bindings-file> [--room-dir <dir>] [--workflow-id <id>]
  [--echo-worker] [--wait] [--wait-timeout <minutes>]`** — runs an authored `WorkflowDefinition` to a
  terminal state or a pause (`src/Baton.Cli/RunOptionsParser.cs`). `--wait-timeout` (#1378) bounds how
  long `--wait`'s poll loop sits on an undecided pause: ignored without `--wait`, and once it elapses
  the call stops waiting and reports exit code 3 (`Timeout`, below) rather than blocking forever on a
  workflow nobody has decided.
- **`baton dispatch <name> [--spec <spec-file> | --spec - | --spec-text <text>] [--adapter <name>] [--model <name>] [--effort <name>]
  [--room-dir <dir>] [--workspace <dir>] [--workflow-id <id>] [--output <path>] [--timeout <minutes>]
  [--token-budget <n>] [--max-tool-steps <n>] [--billed-rate-limit <n>] [--label <text>] [--workstream <slug>]`**
  — the one-shot form: `<name>` resolves to either a worker role (needs a spec) or a built-in
  template (`src/Baton.Cli/DispatchOptionsParser.cs`). A role's task prompt has three mutually exclusive
  sources (#1518): a file (`--spec`), stdin (`--spec -`, refused outright on a non-redirected terminal
  rather than hanging on EOF that never comes), or an inline string (`--spec-text`) — a scout question
  ("what does `baton cancel` actually do today?") no longer needs a brief file first. All three resolve
  to the same `spec` string `RoleDispatch.Materialize` takes, so the room record (the spec/grant lint,
  `--attach`, the built prompt persisted into `bindings.json`) is identical in shape regardless of
  source — there is no separate on-disk spec artifact for any of the three to land in. Left unset, `--room-dir` derives a fresh, unique
  directory under `BatonPaths.Rooms` per invocation — never a stable name derived from `<name>`, so a
  second `baton dispatch review` reruns rather than resuming the first's terminal snapshot. Bindings are
  written into the room directory by `DispatchCommand.ExecuteAsync`
  (`src/Baton.Cli/DispatchCommand.cs`, via `WorkerBindingConfigWriter.SaveToFileAsync`) before
  `RunCommand` is invoked underneath it. `--timeout` (#1442) overrides the dispatched role's own
  catalog timeout for just this dispatch, recorded into that same `bindings.json` (never
  `workflow.json` — a worker's timeout has always been kept off the frozen `WorkflowDefinitionSnapshot`,
  the M7 Phase 7 split `WorkerBindingConfigEntry`'s own doc states). It is the escape hatch for a role
  that legitimately needs longer than its fixed tier timebox — an orchestrator coordinating sub-lanes,
  say — so such a lane does not die mid-flight. Role dispatch only, rejected for a template: a
  template's phases each carry their own role's timeout, so there is no single one to override. Values
  are whole minutes, rejected outright above a 24h ceiling (no interactive confirmation exists for a
  non-interactive CLI) and merely flagged on stderr above 2h. `--label` (#1499) is display text only —
  a short human-readable name (e.g. "the #1496 env-snapshot lane") so Fleet Glass (§6) can show
  something legible instead of a bare `dispatch-<role>-<hex8>` directory name; it is never part of the
  room directory's own name, which stays the generated hex identity above. Sanitized at parse time
  (`DispatchOptionsParser.SanitizeLabel`): trimmed, embedded newlines folded to spaces, capped at
  `DispatchOptionsParser.MaxLabelLength` chars; a blank result is treated as omitted rather than refused.
  Persisted onto every entry of that
  room's own `bindings.json` (`WorkerBindingConfigEntry.Label`) rather than a new file, since bindings
  already exists for every room regardless of terminal state — see §6 schema for how `fleet_status`
  reads it back. `--token-budget` (#1623) overrides the dispatched role's own default per-execution
  token ceiling — §3's "Engine-run verify and the token budget" subsection is the full contract; this
  entry only names the flag. `--workstream` (#1619, rung 1 of #1614's ruling) is a **grouping key, not a title** —
  a room keeps its generated hex identity on disk; the slug only makes several rooms (e.g. an
  implement lane and its review redispatch) read as one workstream in Fleet Glass. Do not conflate it
  with `--label`: a label is 60-char free display text never written into a path
  (`DispatchOptionsParser.SanitizeLabel`); a workstream slug IS later used as a Windows directory name
  (below), so it is validated rather than truncated —
  `DispatchOptionsParser.SanitizeWorkstream` trims it, then refuses (never truncates) anything
  over `MaxWorkstreamLength` (60) chars or outside the grammar `^[A-Za-z0-9][A-Za-z0-9._-]*$` — a
  blank result after trimming is treated as omitted, the same as `--label`. A value that passes the
  grammar check is then folded to lowercase, per the #1614 design record's own slug wording
  ("path-safe, lowercase, short"): NTFS resolves `BatonPaths.ByWorkstream` directory names
  case-insensitively while Fleet Glass's grouping (below) keys on the exact string in a
  case-sensitive JS `Map`, so `--workstream W1619` and `--workstream w1619` fold to the same slug
  rather than sharing one junction directory while rendering as two glass groups. Persisted the same
  way as `--label`, onto every entry of the room's own `bindings.json`
  (`WorkerBindingConfigEntry.Workstream`) — see §6 schema for how `fleet_status` reads it back, and
  the paragraph immediately below for the navigation half.

  **The by-workstream junction directory.** When `--workstream` is passed, `DispatchCommand` also
  creates a Windows directory junction (`mklink /J` via `WorkstreamJunctionLinker`, no elevation
  required) at `BatonPaths.ByWorkstream/<slug>/<room-name>-<hash>` pointing at the room's real
  directory under `BatonPaths.Rooms` — so `cd ~/.baton/by-workstream/<slug>` lists every room in
  that workstream without moving a single file on disk. The `<hash>` suffix (`WorkstreamJunctionLinker.ResolveLinkPath`,
  eight hex characters of the room's own full path) exists because `<room-name>` alone is not unique:
  an explicit `--room-dir` — the pattern every invoking harness uses — is passed through verbatim
  rather than minted fresh, so two rooms with different parents can share a leaf. `BatonPaths.ByWorkstream`
  is **deliberately a sibling of `BatonPaths.Rooms`, never a child**: `FleetStatusTool`, `RoomRetentionSweep`,
  and the fleet-glass pusher (`pusher.py`) all walk `rooms/` exactly one level deep, and a workstream
  directory nested under it would be picked up by every one of those scans and reported as a phantom
  room with no bound snapshot — the same reason `fleet_status`'s caller-supplied `roots` refuses to
  walk `BatonPaths.ByWorkstream` itself (it would double-count a room already found by its real path).
  A failed junction (a machine policy refusing `mklink`, an occupied name that resolves to a
  different room) degrades to a stderr warning naming the existing target — it never fails the
  dispatch, since the room itself is already fully functional without the shortcut.

A room's model is always pinned in `bindings.json` at dispatch time — there is no runtime model
choice a harness makes mid-lane; §9 covers the bindings contract. `baton resume`, `baton decide`, `baton
cancel`, and `baton supply` continue an already-dispatched room; §5 covers `decide` specifically.
`baton resolve` (#1608, §3 below) also targets an already-dispatched room, but never drives it
forward — it settles one execution's `Indeterminate` verdict and stops.

**`baton redispatch <room-dir> [--spec <amended-brief>] [--adapter <name>] [--model <name>] [--effort
<name>] [--workspace <dir>] [--output <path>] [--timeout <minutes>] [--token-budget <n>]
[--max-tool-steps <n>] [--billed-rate-limit <n>] [--label <text>] [--workstream <slug>]`** (#1441) reruns
a single-role `baton dispatch` room into a fresh one, once the operator finds the brief was wrong or
incomplete — without hand-retyping the adapter/model/effort/workspace/timeout flags a from-scratch
`baton dispatch` would otherwise force. Unlike `baton dispatch`'s `--spec` (#1518), `redispatch`'s
`--spec` is file-only — no `--spec -`/`--spec-text` here; the amendment is a deliberate write, not a
scout question, so the asymmetry is by design, not an oversight. `<room-dir>` names the parent room; like `baton dispatch`, the
new room's own directory is always freshly generated (`RedispatchOptionsParser.cs`) — a redispatch is
never a resume, same rule as §2's dispatch entry above. Every flag inherits the parent room's recorded
`bindings.json` entry as its default — adapter, model, effort, workspace, timeout, token budget (#1623),
tool-step cap (#1686 review F2), and (#1499) label —
and is overridden by whichever flag the operator actually passes (`RedispatchCommand.InheritBinding`);
`--output` is the one exception, never inherited, because a prior `--output`'s destination copy path is
not persisted anywhere in the room (only the produced output's customized *name* is, on the bindings
entry's contract) — a redispatch's own `--output`, when given, works exactly like dispatch's own.
`--label` inherits unlike `--output` does: the parent's label IS a persisted, durable room-level fact
(`WorkerBindingConfigEntry.Label`), not a process-local copy target, so a redispatched lane keeps
reading as the same human-named thing — absent inherits the parent's label, specified-and-blank
(`--label ""`) clears it, and specified-and-nonblank overrides it (`RedispatchCommand.InheritBinding`).
`--workstream` (#1619) inherits the identical way, via its own `WorkstreamSpecified` mirror of
`LabelSpecified` (`RedispatchOptionsParser.cs`, `RedispatchOptions.WorkstreamSpecified`) — absent
inherits the parent's workstream, specified-and-blank clears it, specified-and-nonblank overrides it
— so a redispatch chain keeps grouping as one workstream in Fleet Glass without the operator
re-passing the slug on every hop, and can still deliberately break a lane out of its workstream by
passing `--workstream ""`. `RedispatchCommand` also (re-)creates that redispatched room's
by-workstream junction against whichever slug `InheritBinding` just resolved — inherited, cleared, or
overridden — never the raw `--workstream` flag alone, since a bare `baton redispatch` with no
`--workstream` flag at all must still link into the parent's workstream directory. `--spec`
omitted reuses the parent's already-built prompt verbatim — but for #1882's verify-results paragraph,
stripped (#1895) because it names the parent room's results file for a step that did not run in the
child room, the one exception and the reason §9 states; given, the amended brief is rebuilt through
the same `RoleSpecMaterializer` seam a fresh dispatch uses, with the parent's recorded axes as defaults
— including the inherited-unless-overridden label, applied after that rebuild since
`RoleDispatch.Materialize` itself knows nothing of it (`RedispatchCommand.ExecuteAsync`). That seam is
what makes the spec/grant mismatch lint and `--attach` (#1500, `docs/dispatch.md`) apply identically on
this `--spec` path (#1576) — `--attach` is refused outright when `--spec` is omitted, since a verbatim
prompt has nothing left to append an attachment listing to.
The parent must be Terminal (`terminal.json` present) — a still-running or never-dispatched parent is
refused with a typed `CliArgumentException` naming `baton status` as the retry (no interactive
confirmation exists for a non-interactive CLI, the same doctrine `--timeout`'s ceiling above rests on);
a Terminal-but-not-`Succeeded` parent is redispatched anyway, with a stderr note rather than a silent
rerun of a failed or cancelled lane. A parent whose `bindings.json` binds more than one worker (a
composed template, never a single role) is refused — redispatch supports a single-role dispatch only.
The parent's own artifacts are never copied into the child room — the child's `--spec` can cite paths
under the parent room if it needs to, but copying would blur which run produced what. Lineage is
recorded on the new room's own `.baton/room.json` marker (`RoomMetadataFileName`, `BatonPaths.cs`) — the
parent room directory, and the parent's own execution id when cheaply known from its terminal
sentinel — rather than a new parallel file, since that marker is already this room's metadata home.
One inheritance rule differs from what `--adapter`'s name implies: on the `--spec`-omitted path, an
adapter swap re-derives only the adapter-scoped axes (model and effort drop to the new vendor's
defaults per the vendor-swap rule above; `StreamJson` is recomputed for the new adapter). The
parent's resolved `PermissionGrant`, `GrantAuditMode`, and worktree-provisioning intent are carried
across the swap **unchanged**, because the role's *declared* grant intent — what
`RoleDispatch.ToBinding` derives those from per adapter — is not recoverable from `bindings.json`
(only the already-resolved grant is persisted). So a redispatched worker can run under a grant shape
a fresh dispatch of the same role+adapter would never produce; the command prints a stderr note on
every such swap, and an operator who needs the grant re-derived passes `--spec`, which rebuilds
through `RoleDispatch.Materialize` against the real role catalog.

### §2 schema — the CLI argument table

| Verb | Usage | Source |
|---|---|---|
| `run` | `baton run <workflow-file> --bindings <bindings-file> [--room-dir <dir>] [--workflow-id <id>] [--echo-worker] [--register] [--wait] [--wait-timeout <minutes>]` | `RunOptionsParser.cs` |
| `dispatch` | `baton dispatch <name> [--spec <spec-file> \| --spec - \| --spec-text <text>] [--attach <file>] [--adapter <name>] [--model <name>] [--effort <name>] [--room-dir <dir>] [--workspace <dir>] [--workflow-id <id>] [--output <path>] [--timeout <minutes>] [--token-budget <n>] [--max-tool-steps <n>] [--billed-rate-limit <n>] [--verify <cmd>] [--verify-cmd <cmd>] [--verify-timeout <minutes>] [--expect-pr <true\|false>] [--continue <room-dir>] [--override-runway <reason>] [--label <text>] [--workstream <slug>] [--repo <checkout-dir>] [--list-capabilities]` | `DispatchOptionsParser.cs` |
| `redispatch` | `baton redispatch <room-dir> [--spec <amended-brief>] [--attach <file>] [--adapter <name>] [--model <name>] [--effort <name>] [--workspace <dir>] [--output <path>] [--timeout <minutes>] [--token-budget <n>] [--max-tool-steps <n>] [--billed-rate-limit <n>] [--verify <cmd>] [--label <text>] [--workstream <slug>]` | `RedispatchOptionsParser.cs` |
| `resume` | `baton resume <room-dir> --worker <role> (--message <text> \| --message-file <path>) --bindings <bindings-file> [--workflow-id <id>]` | `ResumeOptionsParser.cs` |
| `decide` | `baton decide <room-dir> --execution <execution-id> --type resume\|reject\|retry-with-revision\|supersede [--target-step <step-id>] [--supplementary <execution-id>] --bindings <bindings-file> [--workflow-id <id>]` | `DecideOptionsParser.cs` |
| `resolve` | `baton resolve <room-dir> [--execution <execution-id>] --accept-capture \| --reject --reason <text> \| --close --reason <text>` | `ResolveOptionsParser.cs` |
| `supply` | `baton supply <room-dir> --worker <role> --output <name> --file <source-path> --bindings <bindings-file> [--workflow-id <id>]` | `SupplyOptionsParser.cs` |
| `cancel` | `baton cancel <room-dir> [--execution <execution-id>] [--bindings <bindings-file>] [--workflow-id <id>]` | `CancelOptionsParser.cs` |
| `status` | `baton status <room-dir> [--follow] [--json] [--repo <checkout-dir>]` | `StatusOptionsParser.cs` |
| `watch` | `baton watch <room-dir> --notify <command\|url>` \| `baton watch --list` \| `baton watch --clear-fired` | `WatchOptionsParser.cs` |
| `templates` | `baton templates [--json]` | `Program.cs` |
| `keep` | `baton keep <room-dir>` | `KeepOptionsParser.cs` |
| `unkeep` | `baton unkeep <room-dir>` | `UnkeepOptionsParser.cs` |
| `memory` | `baton memory audit [--format text\|json] [--help]` | `MemoryAuditOptionsParser.cs` |

`templates` narrows to the built-in catalog only (`Baton.Vendors`'s `BuiltInWorkflowTemplates`) —
there is no authoring UI to browse a saved-template library visually against (Appendix, R7 in the
old numbering — dropped here, since there is no longer a separate register to number rulings
against).

**`watch` (#1488): one-shot, block-free registration, never a poll loop.** `baton status --follow`
(above) blocks its own process until the room reaches Terminal; `watch` is the opposite shape a
harness needs to end its turn immediately after dispatch — it writes one file under
`{BatonPaths.Watches}` (`{BATON_HOME}/watches/<watch-id>.json`, `WatchStore.cs`) and returns. Terminal
detection is the identical predicate `FleetStatusTool`, `rooms prune --terminal`, and `room delete`
already read a room's terminal state through — `TerminalSentinelWriter.TryReadAsync(room-dir)` returns
non-null — never a second definition. An already-terminal room at registration fires immediately, in
the registering process, before it returns (no lost wake-up); a room that reaches Terminal afterward
fires from `WatchSweep`, a `baton daemon`-hosted `BackgroundService` (§7) polling every pending watch
on a 15-second cadence — the same host `RoomRetentionSweep` already runs on, not a second long-running
process. **Firing is exactly-once-or-lost, never double.** `WatchStore.TryClaimAsync` marks a watch's
`firedAt` under a per-file named `Mutex` every access to that file takes (mirroring
`RoomRegistryStore`'s own `RunUnderLock`, §8), atomically checking-and-setting in one critical section
— so a registration's own immediate check and a concurrent `WatchSweep` iteration can never both
observe an unclaimed watch and both notify: exactly one claims, the other is a no-op. A process that
crashes after a successful claim but before the notify send completes loses that notification rather
than risking a duplicate — nothing re-tries a claimed watch. `--notify <target>` is either an absolute
`http`/`https` URL (POSTed the notification as its JSON body) or a command line, spawned once with the
identical JSON on stdin and in the `BATON_WATCH_EVENT` environment variable — never interpolated into
the command string itself. The JSON carries `{room, state, verdict, outputs, terminalAt}`: `verdict` is
the parsed content of a file literally named `verdict.json` among the room's declared outputs, when one
exists (omitted otherwise — most workflows have none). `baton watch --list` prints every registered
watch, pending and fired; `baton watch --clear-fired` deletes the fired ones. **Depends on `baton
daemon` running for any transition after registration** — an already-terminal room at registration
time is the only case this feature guarantees without one; `baton watch`'s own registration warns on
stderr when no daemon mutex (`Global\BatonDaemonMutex_{user}`) is found for the current user, though a
daemon started with `--no-mutex` is invisible to that check and reads as running regardless.

**The stdin write is bounded by the same 30 s timeout as the command's own exit** (fix round,
`WatchNotifier.cs`): the timeout is armed *before* the write starts and the write runs under it, so a
command that never reads stdin — the documented `curl -X POST …` shape above is exactly this — cannot
wedge the sweep once the payload exceeds the OS pipe buffer (~4 KB on Windows). A write that has not
drained by the deadline gets the process tree killed and the failure logged, since a command that never
consumed the first byte was never going to finish reading the rest; a command that exits after its own
work is left to keep running past the timeout unkilled, as before. This is a deliberate decision *not*
to gate the stdin write on payload size or fall back to a temp file: `BATON_WATCH_EVENT` already
carries the identical payload with no blocking risk (set at spawn time, not written to a stream), so a
command that only wants the common case can read from there and skip stdin entirely — the timeout is
what makes attempting the stdin write unconditionally safe rather than something that needs a size
threshold to avoid.

**The watches directory is reaped, not just fired-in-place.** `WatchSweep`'s sweep pass also deletes a
fired watch whose `firedAt` is older than a retention window (default 24 h,
`BATON_WATCH_REAPER_RETENTION_HOURS`-configurable, the same env-override shape `RoomRetentionSweep`'s
own intervals use) and any watch — fired or still pending — whose room directory no longer exists,
logged once at the moment of removal. Without this the directory only ever grows: `baton watch
--clear-fired` remains the manual, immediate path, but nothing previously reclaimed a watch an operator
forgot to clear, and `WatchStore.ListAsync`'s per-sweep scan is O(n) in whatever accumulated there.

**Trust model.** A watch file is an unauthenticated instruction to run its `--notify` target under the
daemon's own identity, at an arbitrary later time, outside any lane's Job Object containment and after
the lane that registered it is gone. That is acceptable only because nothing narrower can reach
`{BatonPaths.Watches}` today: a write-granted worker's `Edit`/`Write`/`NotebookEdit` calls are bounded
to its workspace or outbox by the `PreToolUse` hook (`HookCheckCommand.cs`,
`AgyHookCheckCommand.cs`, `OutboxPath.cs`), so only a role already holding an unscoped shell grant
(`run_shell_commands: true` with no pattern list, e.g. `implement` in `WorkerRoles.json`) can write a
watch file directly — and that grant already defeats every withheld category (#529,
`PermissionGrant.cs`'s `CategoriesDefeatedByTheShell`), so a watch adds deferred, post-lane execution
rather than new privilege. If a narrower write path into `~/.baton` is ever added, or `OutboxPath`'s
containment loosened, this paragraph is what it would be breaking.

**`cancel`'s `--execution` is now optional** (#1495): omitted, it targets "the target lane" —
exactly one candidate's latest execution, refused (naming every candidate) on zero or more than one
(`RunningExecutionResolver.cs`). A candidate is a currently-`Running` step, or (#1607) a quota-parked
one — `Failed` with a scheduled `RetryNotBefore`, the identical shape `MutationInterface`'s
`IsParkedRetryTarget` and `CancelRequestPoller`'s own `isParked` check already use. A parked
candidate is not delivered the same way a running one is: it is settled through the dedicated path
#1605 built and #1556 PR 2 generalized (`InFlightExecutionRegistry.MarkArrestIntent` /
`MutationInterface.SettleArrestIntentsAsync`), never through `CoreEventAggregation` or
`NonProcessCancellationDetector`'s own Running-only filters, which stay unmodified and unconsulted
for a parked target.

**#1556 PR 1 collapsed the three-place predicate into one register.** `RunningExecutionResolver.cs`'s
step-tied candidate list above, `CancelRequestPoller`'s settle re-check, and
`NonProcessCancellationDetector`'s two Running/step-less arms each used to restate this "is this
execution still arrestable" shape independently (PR #1528 review finding F10). `Baton.Projection.ArrestableExecutions`
is now the single reader: `ResolveSingleStepLane` is the step-tied-only list `RunningExecutionResolver`
shims over unchanged; `Find` is the settle re-check, now step-less-aware — `ArrestableExecutions.cs`'s
own remarks state the D2 fix this buys; `All` is what `NonProcessCancellationDetector` filters down
to its own Running-only, `NonProcess`-binding arm.
A quota-parked target is unaffected: `All` still yields one (so `Find` still recognizes it), but
`NonProcessCancellationDetector` still filters it back out — that arrest path stayed a dedicated one
(pre-#1556 PR 2: `SettleParkedCancelIntentsAsync`'s alone; #1556 PR 2 on: the ledger-read
`parkedCancelExecutionIds` block below, `IsParkedRetryTarget`-guarded), exactly as before the
collapse.

**#1556 PR 2 folded #1563's narrower quota-parked-only mark into one general seam.** The gap PR 1 left
open: a poller tick that finds a target `ArrestableExecutions.Find` still admits, but with no live
process registered for it, only had a real delivery point for the quota-parked shape
(`MarkParkedCancelIntent`) — a genuinely `Running` non-process step or a still-pending step-less
execution fell through to the bounded 5-tick retry with no mark at all, eventually rejected as
"likely non-process work" even though the seam existed right next to it. `InFlightExecutionRegistry`'s
mark/drain/wake latch is now general-purpose (`MarkArrestIntent(executionId, reason)` /
`DrainArrestIntents()` — `reason` is diagnostic only, surfaced verbatim if the intent cannot be
settled), and `CancelRequestPoller` marks unconditionally whenever `Find` still admits the target,
not only when it is parked. `MutationInterface.PumpToFixedPointAsync` gained one derived-obligation
block, `SettleArrestIntentsAsync`, placed after the completion detector and before the cancellation
detector (Q2: completion still beats arrest within a round) — it appends only
`FlowEvent.CancellationRequested`, the SAME journal shape the direct (idle-room) `RequestCancellationAsync`
path already writes, then lets the round's own pre-existing derived obligations finalize it on a
later round: `NonProcessCancellationDetector` for a Running non-process or step-less target, the
ledger-read `parkedCancelExecutionIds` block for a parked one. One settle shape, not a second one
bolted on beside it. A step-tied `Running` target is admitted only once its resolved `WorkerBinding`
proves `NonProcess` — the fail-closed gate that stops the seam from ever recording an intent for a
live `Process` target that simply has not registered with `InFlightExecutionRegistry` yet (a real
race the poller itself cannot resolve, since it has no `workerBindings` in scope). A target `Find` no
longer admits by the time its mark drains (redispatched, already terminal, or never a real id at all)
is dropped with one diagnostic line naming why — "already settled" or "unknown execution id" — never
silently. The pump's own fixed-point return additionally rechecks for an undrained mark immediately
before returning: a mark landing after a round's own drain but before that exact instant would
otherwise be silently lost, since returning releases `flow.lock` and (via `RunCommand`'s poller
lifetime) cancels the very poller that could have re-offered it.

**Behaviour change from the widening, not just an addition:** a room with one
`Running` step and a sibling sitting in ordinary retry backoff — previously an unambiguous single
`Running` candidate — is now ambiguous and refuses/rejects, since the sibling's `RetryNotBefore` makes
it a second candidate. Deliberately pinned
(`RunningExecutionResolverTests.A_Running_step_and_a_quota_parked_step_together_are_ambiguous`): the
resolver cannot tell "the operator means the one that's actually running" from "the operator means
the one closest to being retried" without guessing, and guessing is exactly what this resolver exists
to refuse to do. Against a room whose `baton run` pump is still live, the direct
mutation call cannot win `flow.lock` — `cancel` catches that specific `WorkflowLockedException` and
writes a room-scoped `cancel.request` file instead (`CancelRequestFile.cs`), which the pump itself
polls at a modest cadence without ever contending the lock (`CancelRequestPoller.cs`) and delivers
through the same `FlowEvent.CancellationRequested` path `MutationInterface` already uses. The
fall-through path re-resolves `latest` at poll time (arresting whatever is running or parked then),
whereas the direct path cancels the execution resolved at command time; on the fall-through path, zero
or more than one candidate at act time lands as a `.rejected` record in the room (with the diagnostic
reason written in its body), rather than a terminal command-line refusal. This is the arrest half of
§10's "only cancellation-then-restart" ruling, not a reopening of it: nothing here reaches into a
running worker to redirect it — it only makes the existing stop-then-`redispatch` sequence reachable
from outside the lane's own process. **Ordering guarantee (#1649):** `RunCommand`'s own startup sweep
of a leftover `cancel.request` cannot claim a live write from a `cancel` racing that same startup
window — the discriminating rule lives on `CancelRequestFile.DeleteStalePendingRequestAsync` itself,
not restated here.

A parked candidate reached through the **direct** path (no live pump contending the lock) is
reachable only when its `RetryNotBefore` has already elapsed AND a live pump is confirmed — a
genuinely still-future park is refused outright by the dead-holder check below before the resolver
ever runs, since that check scans every step for a future deferral, not just the one being targeted.
That check itself was widened in the same change (#1607) from firing only on a confirmed-`Dead`
holder to firing on anything but a confirmed-`Alive` one — see `CancelCommand.cs`'s own dead-holder
gate comment for which `EngineLivenessProbe.Unknown` cases motivate this and why leaving it at
`Dead`-only would have reopened #1586's hang from a new entry point. An already-overdue park raced
against a poller-less pump used to lose to `MutationInterface`'s own retry-obligation check, which
redispatched it before the parked-cancel-intent wait (armed only by `CancelRequestPoller.TickAsync`,
which a poller-less pump never runs) was ever reached — the same outcome explicit `--execution`
targeting an overdue park already had; #1607 did not introduce it. **Fixed by #1634**: before
`GetRetryObligations` can schedule, or `DependencyResolver.GetReadySteps` can redispatch, a parked
step's retry, `MutationInterface`'s pump loop now checks the raw ledger — every `ExecutionId` a
`CancellationRequested` has named in a round this pump call has read, accumulated across the call's
own rounds — not `FlowState.CancellationRequestedExecutionIds`, which excludes a target with a
terminal event, and a parked target's `Failed` outcome is exactly that — for the step's
`LatestExecutionId` and, if found, appends `ExecutionCancelled` instead of letting either mechanism
redispatch: the ledger-read rule.

**The scope of "a round this pump call has read" is the checkpoint window, not this call's own
writes.** `ReadSnapshotFromOffsetAsync` reads from the loaded `ProjectionCheckpoint`'s byte offset
forward; with no checkpoint — a fresh pump, a corrupt or pre-v4 checkpoint file, or a full replay —
that window is the *entire ledger from byte 0*, including every `CancellationRequested` any prior
process ever journalled to this room. A `CancellationRequested` a *previous* process wrote is
therefore just as visible to this rule as one this call wrote itself.

That mattered because `InFlightExecutionRegistry.RequestStopAsync` (a Ctrl-C wind-down) journals
`CancellationRequested` for *every* still-registered execution, unconditionally — not an operator
naming that step. Gating the ledger-read rule on the pump's own `!hostStopRequested` flag stops that
misread only *within* the process that received the stop: the flag lives in memory, but the window
the rule reads from spans process boundaries. A step that failed and re-parked in the same round a
host stop landed, with no clean checkpoint saved past that point (a second Ctrl-C, a kill, a crash, a
closed terminal), would replay `CancellationRequested` on the *next* `baton run` — in a process where
`hostStopRequested` starts `false` — and read as an operator cancel, settling the step terminally
`Cancelled` and out of `RetryWithRevision`'s reach.

**Fixed by #1762, before #1634 ever shipped separately — the two land in one PR, never released
apart.** The distinction is made durable, not a flag: `FlowEvent.CancellationRequested` carries
`Origin` (`CancellationOrigin`: `Operator` or `HostStop`), nullable, defaulting to `null` on replay of
any line written before this field existed. `RequestStopAsync` writes `HostStop`. Every other
appender — `CancelCommand`'s direct path (`MutationInterface.RequestCancellationAsync`), the poller's
in-process live delivery (`InFlightExecutionRegistry.RequestCancellationAsync`), and the pump's own
arrest-intent settle (`SettleArrestIntentsAsync`, #1556 PR 2 — the poller's marked intents drain
through it) — writes `Operator`. The ledger-read rule now
accumulates only `Origin == Operator` lines, so a `HostStop` line is excluded regardless of which
process, or how many rounds later, reads it. A `null` `Origin` (a line written before #1762 shipped)
is likewise never accumulated. Because the `Origin` field and the ledger-read rule that consults it
ship in the same PR, no released build ever ran the rule without `Origin` — there is no window in
which a real, already-deployed ledger's `HostStop` line was ever read as an operator cancel by this
mechanism, so this addition cannot make any existing ledger *worse*; it simply closes the
cross-process leak before the rule that has the leak ever reaches an operator. The `!hostStopRequested` gate
stays too, alongside the `Origin` filter — cheap, and it stops the same-process case one round
earlier than waiting for the accumulator to simply never contain a `HostStop` id.

**The dead-holder gate applies to both targeting modes, deliberately, with a real cost on the
explicit one.** The gate runs before `--execution` is even inspected, so `cancel <room> --execution
<id>` against a still-future park is refused on Unknown liveness exactly like the bare `cancel
<room>` form — not because the two paths share reasoning about *which* candidate to pick (they
don't), but because the hang the gate prevents follows from the room holding any pending future
`RetryNotBefore` once `flow.lock` is won, regardless of which execution the caller named. Scoping the
refusal to room-level targeting only would leave the explicit path free to reopen #1586's hang from
the one entry point #1607 widened this gate to close, which would defeat the point of widening it at
all. The accepted cost: before #1607, `Dead` was the only liveness value this gate refused on, so a
genuinely-alive pump with a failed or missing sidecar write (`Unknown`, not `Dead`) still had a
working path — `--execution <id>` would proceed, lose the lock race to the real pump, and fall
through to the `WorkflowLockedException` handling that writes `cancel.request`. Since #1607 widened
`Dead`-only to "anything but confirmed `Alive`," that fall-through is no longer reachable either: an
`Unknown` verdict now refuses both paths up front, even when the pump is genuinely alive. There is
currently no verb that reaches a still-alive pump whose holder record can't be confirmed — the
refusal's own hint (`CancelCommand.cs`) says so rather than pointing at a recovery that does not
exist; `baton status` is not offered as one, since it consults the identical `EngineLivenessProbe`
and would report the same `Unknown`.

**`cancel`'s `--bindings` is now optional too** (#1607 friction fix): omitted, it defaults to
`<room-dir>/bindings.json` — the file a room dispatched via `dispatch`/`redispatch` already holds,
since both write one there (`CancelOptionsParser.cs`). A room started via bare `baton run --bindings
<elsewhere>` never gets one copied in, so the default there simply won't exist; a nonexistent default
surfaces through the same "file not found" `WorkerBindingConfigException` `WorkerBindingConfigParser`
already raises for a bad explicit path — no new failure mode, and the operator falls back to passing
`--bindings` explicitly as before. One fewer argument to retype for the common (dispatched-room) case.
`CancelCommand` augments that exception's message for exactly this default-path case (never for
run/decide/supply, whose `--bindings` is required rather than defaulted) — naming the defaulted path
as a default rather than a mistyped explicit argument, and saying `--bindings` is still available for
a room whose bindings file lives elsewhere.

---

## §3 The lane protocol (completion contract)

`terminal.json` is written into a room directory the moment its workflow reaches a terminal state —
the completion signal a harness should watch instead of polling `baton status` prose or racing the
`baton run`/`baton dispatch` process's own exit
(`src/Baton/Status/TerminalSentinelWriter.cs`). It is written **last** — after every output an
outcome could reference already exists on disk — via a temp-file-then-atomic-move sequence, so a
file-watching harness never observes a partial write (`TerminalSentinelWriter.cs`). It is the
identical shape `baton status --json` prints (`WorkflowStatusView`), so a file-watcher and a polling
`status --json` caller read one contract for that pair specifically
(`src/Baton/Status/WorkflowStatusView.cs`) — `fleet_status` is a **third, related** shape;
see §6.

**Its absence does not always mean "not terminal yet."** Two exceptions, both real:

1. `TerminalSentinelWriter.WriteValidationRefusedAsync` — the pre-ledger refusal path — is only
   invoked when `RoomLedgerProbe.HasLedger` is false (`src/Baton.Cli/Program.cs`,
   `src/Baton.Cli/RoomLedgerProbe.cs`: a `flow.jsonl` that exists and is non-empty). A room that
   already has a real ledger — e.g. a paused room re-dispatched with a bad `--spec` — returns exit code
   2 (`ValidationRefused`) with **no sentinel written**, because the room's ledger (or a still-live
   pump) is its real terminal record and a fresh refusal must not overwrite it with a fabricated
   `Failed`/no-outputs sentinel. `baton resume`'s own refusal path (`Program.cs`) never writes
   a sentinel at all — a resume always targets an already-ledgered room.
2. `RoomHeld` (exit code 5, below) also writes no sentinel: the room may be perfectly healthy (a live
   pump, or a background sweep's brief lock), and writing `Failed` here would tell a file-watcher a
   running room just died while `baton status --json` reads the same room as `Running` at the same
   moment (`Program.cs`).

So: absence means "not terminal yet, **or** refused against an already-ledgered room, **or** another
Flow instance currently holds it" — never simply "never started." A harness that needs to
distinguish these reads `baton status`/`flow.jsonl` directly rather than inferring from the sentinel's
absence alone.

**The sentinel can also disappear.** `TerminalSentinelWriter.DeleteStaleSentinel`
(`TerminalSentinelWriter.cs`) removes a prior sentinel when a room is re-run, so that retrying a
room that previously failed pre-ledger does not leave the old `terminal.json` in place for the whole
duration of a new, genuinely in-progress attempt. A file-watching harness must expect `terminal.json`
to vanish and reappear across a re-dispatch of the same room directory, not treat its disappearance
as an error. **And that delete can refuse the run (#1608 re-review).** When the stale sentinel cannot
be deleted — held open by a reader without `FileShare.Delete` — `baton run`/`dispatch`/`redispatch`
refuse before the pump starts, exiting `ValidationRefused` with a message naming the locked file,
rather than pumping behind a record that reads "already done"; only the post-`resolve` delete
(`Program.cs`, which runs after a durable mutation) swallows that failure and warns instead. So the
mirror of the absence list above holds too: **presence** can mean "a previous attempt's sentinel this
attempt refused to start behind", and a harness keyed on the file existing must read the exit code
before treating it as the current dispatch's result.

`baton status` is read-only, produces no `CommandResult`, and always exits 0 when it manages to print a
status at all (`Program.cs`) — it cannot complete a room or substitute for watching the
sentinel.

**Three defects this contract used to carry, now closed (#1375, #1377, #1513) — cited so a harness
author who read an older version of this section knows what changed:** a dead engine's `Running` step
now also reports `steps[].liveness: "dead"` (§3 schema below), computed by the identical
`EngineLivenessProbe` the human `baton status` rendering already used — one probe, two renderings,
never two that can disagree; and a decision-rejected step now sets the top-level `rejected: true`
(§3 schema below) alongside `state: "Failed"`/`error: null`, so an absent `error` no longer implies an
absent cause — it can mean "a person said no" as well as "not yet recorded". Neither fix invents a
value the ledger cannot actually support: there is still no operator-supplied rejection *reason* to
surface on this path (`FlowEvent.ExternalDecisionRecorded` carries none), so `rejected` stays a
boolean, not a `reason` field that would always read `null`. #1622 (d) later gave `rejected` a second
producer — `baton resolve --reject` — which *does* take a reason; that one is folded into `error`
rather than into a new field (the ruling below), so the boolean shape survives both.

**#1513 closes a gap #1462 left, not a choice it made.** #1462 added `liveness` as an additive
per-step signal and did not address room-level `state` at all — its issue body frames the change as
extending two fleet views with the same two fields, never weighing whether to fold liveness into
`state`, and neither its own spec text nor the test doc comment it wrote states a reason for leaving
`state` alone. #1513 is the PR that first considers the room-level question. Issue
#1513 was filed against a room (`dispatch-implement-2c5dcd8d`) whose `flow.jsonl` appeared stalled on
`executionRequestAccepted` with its deliverable already on disk — but that room's engine was in fact
still alive and finished naturally minutes later (`terminal.json`: `Succeeded`); the reproducible
defect is a distinct, confirmed live signature — four sibling rooms
(`a0c38801`/`b161e85a`/`d1fb0d42`/`e5d1747c`) each `Failed` with a still-pending `RetryNotBefore`
whose pump process an operator killed, none of which will ever go terminal on their own (§7). That
shape — `Running` (a pending retry is not yet terminal) with **nothing left alive to act on it** —
is exactly the case an operator scanning `fleet_status` most needs protecting from.
`FleetStatusTool.ProcessRoomAsync` now overrides its **own returned `FleetRoomStatusView.State`**
(never `WorkflowStatusView.State`/`WorkflowOutcome`/`state.Status` itself — `RunExitCodeResolver` and
`TerminalSentinelWriter` are unaffected, and `status --json` keeps reporting its own `state`
unchanged, though it now also carries `liveness` on the widened set of steps described below) to
`"Stalled"` — a `fleet_status`-only display string never folded into `WorkflowOutcome` itself —
whenever the room reads
`Running` and every step whose `liveness` this projection computes reads `"dead"` with none reading
`"alive"`. The condition `liveness` is computed under
(`WorkflowStatusProjector.Project`, `src/Baton/Status/WorkflowStatusView.cs`) also widened: previously only a `Running` step was probed;
now a `Failed` step still carrying a `RetryNotBefore` is too (no expiry check — a step keeps this
gate as long as `RetryNotBefore` is set at all, since a stale-but-still-set value is itself part of
the bug this closes), since that step's own promise ("this will retry") rests on the identical fact —
the pump that recorded `StepRetryScheduled` staying alive long enough to act on it (§7: there is no
daemon reaper; `MutationInterface`'s scheduling loop `Task.Delay`s the wait **in-process**).

**`baton resume` does not recover these rooms.** `MutationInterface.RecordResumeAsync` does dispatch a
fresh linked execution off the step's `LatestExecutionId` regardless of `RetryNotBefore`, gated only
on the target not being a multi-step worker or a `NonProcess` binding — but `ResumeCommand.ExecuteAsync`
refuses before that method is ever reached, the moment the bindings entry has no `SessionId`
recorded, and nothing in this codebase writes a non-null one today (adapters do not yet capture a
vendor session id into the room ledger on their own — that capture is #1381, open). Every room this
section describes reaches that refusal. `--message`/`--message-file` is also mandatory on `baton
resume` (`ResumeOptions.cs`) — exactly one is required, so an operator recovering a stalled room has
to invent one even where it applies.

`baton redispatch` is not a substitute either, for an unrelated reason: it refuses any parent room
with no `terminal.json`, and a room in this shape has none by definition (`RedispatchCommand.cs`).

**The verb that actually recovers a room in this shape, verified by running it against a copy of one
of #1513's own four stalled rooms rather than assumed: a fresh `baton run` against the room's own
`workflow.json`/`bindings.json`, `--room-dir` pointed at the room.** `RunCommand` recognizes the
existing `snapshot.json`, accepts the room instead of refusing it, and re-enters the same in-process
wait the original pump was doing — nothing dispatches again until `RetryNotBefore` elapses, and the
process driving that wait has to stay alive for it to fire, exactly as the mechanism above describes.
This is the same re-drive the "known limitation" paragraph below already assumes exists; that
paragraph's own caveat (briefly misreported as still `"Stalled"` while a live pump is in fact waiting)
is the accurate scoping of what this recovers and what it does not.

**`baton cancel` was also checked rather than assumed, and originally left the room worse than it
found it — closed by #1586.** Without `--execution`, a room with no `Running` step used to refuse
outright (`RunningExecutionResolver` had no notion of a parked candidate) — #1607 widened the
resolver so a genuinely-still-parked room now targets that step the same way an explicit
`--execution` always could (§2 above). With the parked execution's id (explicit or resolved), it
used to take the room's lock, clobber the one artifact
naming which engine died, and never come back — `CancelCommand`'s own dead-holder-check comment is
the canonical account of that old failure and today's guard against it, not restated here. #1586's fix
runs before any acquire: `CancelCommand` reuses the same `EngineLivenessProbe` arbiter this section's
`baton status` line already relies on — the two verbs share the probe, not the recorded identity it
probes. `baton status` probes the event-recorded engine identity (`ExecutionRequestAccepted`'s
`EnginePid`/`EngineStartTime`); `CancelCommand` probes the lock-holder sidecar's recorded pid and
process start time instead, since a dead-mid-park room's own `flow.lock.holder` is the only place
that identity survives. So a dead holder with a step still owed a future retry is refused outright,
pointed at the `baton run --room-dir` recovery above, sidecar untouched. A holder the lock is still
genuinely OS-held by (a live pump) falls through unchanged to the pre-existing behaviour.

**#1586 also closed the discoverability half: `baton redispatch`'s own missing-`terminal.json`
refusal, and `baton status`'s dead-engine parked line, now cite the identical `baton run --room-dir`
wording (`Baton.Cli.RecoveryGuidance`) — one string, not three independently drifting phrasings of
the same recovery.** `baton resume`'s refusal is not included: it fires for an unrelated reason (no
`SessionId` recorded, above) that `baton run --room-dir` does not fix either, and #1381 — not #1586 —
is what would let it.

So `"Stalled"` reads as "nothing is currently making progress, but this is not done, and recovering
it needs the operator to start a fresh `baton run` pointed at the room" — never as a `Failed` room a
caller might reasonably discard, and never as a room `baton resume` will quietly fix on its own.

**Closed by #1577: a re-drive mid-backoff no longer misreports for the whole wait.** A fresh `baton
run` reviving a room while it is still inside a retry backoff wait re-enters the same idle-deferral
wait without ever admitting a fresh execution (no new `ExecutionRequestAccepted` — ordinary backoff
is not a fresh dispatch), so `liveness`'s original `ExecutionRequestAccepted`-only read would keep
probing the dead prior pump for the whole wait. `PumpToFixedPointAsync` (`MutationInterface.cs`) now
renews a `FlowEvent.StepRetryScheduled` carrying this pump's own `EnginePid`/`EngineStartTime` the
moment it re-enters a pending deferral it did not itself schedule (once per step per call, not once
per `MaxParkWaitChunk` re-arm); `liveness`'s engine-identity read (`WorkflowStatusView.cs`,
`StatusCommand.FormatParkedStatus`) takes whichever of `ExecutionRequestAccepted` or
`StepRetryScheduled` stamped a given execution most recently, in log order.

### The terminal vocabulary, and the two-predicate model (#1586 S1)

`WorkflowOutcome` (`src/Baton/Status/WorkflowOutcome.cs`) has **six** members today:

| Value | Meaning |
|---|---|
| `Running` | At least one step's latest attempt is still in flight, or Flow crashed before recording its outcome |
| `Paused` | Nothing running; at least one step idle at a decision point |
| `Succeeded` | Every step succeeded |
| `Failed` | At least one step failed or was rejected, and the room did not settle any other terminal way |
| `Cancelled` | At least one step was cancelled and nothing failed |
| `Indeterminate` | Journal facts alone could not decide success vs failure — see below |

**The two-predicate model.** A room's completion has always actually been two separate questions:
*execution outcome* (did the worker's process finish, crash, or get cancelled — `OutcomeVerdict` /
`FailureClassification`, Flow's own observation) and *contract completion* (did the declared outputs
end up satisfied — `ContractValidator`, a fact about the filesystem). Every value above except
`Indeterminate` is a case where the two predicates agree, or where one alone is enough to decide
(`Cancelled` short-circuits contract completion entirely). `Indeterminate` is what the schema had
never had a word for: the two predicates *disagree* — most concretely, #1594's shape, where the
worker plainly did substantial work (a response-bearing envelope) but the contract's declared
output(s) are simply absent, so "did this succeed" cannot be read off the journal alone. #1608 closed
that one live exception: `OutcomeClassifier.Classify`'s captured-response arm settles
`OutcomeVerdict.Indeterminate` (carrying no `FailureClassification` at all — that vocabulary answers
"why did a genuine failure happen", not "why can this not yet be read off the journal"), never
`Failed(Permanent)`. A worktree fingerprint that fails to reconcile at settle time is the same shape
from a different source, still unimplemented (`baton settle`, S2, tracked on #1586). This is a
**single added enum value, not a two-field split** — the schema keeps its one `state` string; the two
predicates live in code (`OutcomeClassification`/`ContractValidator`), not as two parallel top-level
fields. `StepStatus` itself stays untouched by this ruling too: a step whose latest execution is
`Indeterminate` still projects `StepStatus.Failed` (`Domain.FlowEvent.ExecutionIndeterminate`,
`Projection.StateProjector`); the room-level word is what changes, driven by
`Domain.StepState.IndeterminateAwaitingResolution` (`Status.WorkflowOutcome.DescribeTerminal`, checked
ahead of the ordinary `Failed`/`Rejected` read).

**Six sources, five producer values, since #1608, #1593, #1623, #1373 and #1796.** S1 added only the
vocabulary, its consumer obligations below, and the missing retry-foreclosure primitive (next
paragraph) — nothing in `src/` wrote `Indeterminate` from that slice alone. `Domain.IndeterminateProducer`
now has **five** values: #1373's timeout arm is a *source* that settles onto the existing
`ContractFailure` value, because it admits exactly that value's verbs and a value distinguishable
only by name is vocabulary the resolve grammar cannot act on; #1796's build-lock-busy arm, by
contrast, gets its own value because it admits `VerifyFailed`/`Arrested`'s verbs (`--close --reason`),
not `ContractFailure`'s. What writes it now:

| Producer | Event | `Domain.IndeterminateProducer` | Landed |
|---|---|---|---|
| `OutcomeClassifier.Classify`'s #1594 captured-response arm — declared output(s) missing, but a terminal response was recoverable | `FlowEvent.ExecutionIndeterminate` (non-null `CapturedResponseFile`) | `CapturedResponse` | #1608 |
| `OutcomeClassifier.Classify`'s #1593 uncaptured contract-failure arm — declared outputs simply absent or failed validation, or a dead worker (stream-json ending without a `result` record) on a mutated workspace, with no response to capture; also #1680's first-verdict canary (a natural, contract-satisfied exit whose caller reports ≥1 tool call and zero agy `PreToolUse` hook verdicts — the hook may never have run, and this vendor reads that silence as an ALLOW rather than an error) | `FlowEvent.ExecutionIndeterminate` (null `CapturedResponseFile`) | `ContractFailure` | #1593, #1680 |
| The role's engine-run verify command exited non-zero after a clean, contract-satisfied worker exit | `FlowEvent.VerifyFailed` | `VerifyFailed` | #1623 |
| A live execution crossed its role's token budget and was arrested | `FlowEvent.ExecutionArrested` | `Arrested` | #1623 |
| `OutcomeClassifier.Classify`'s #1373 timeout arm — Flow's own dispatch timeout killed the execution and the workspace it was killed in carries work (see the paragraph below) | `FlowEvent.ExecutionIndeterminate` (null `CapturedResponseFile`) | `ContractFailure` | #1373 |
| Verify actually started, but its only failing member(s) were blocked on `tools/buildlock.py`'s build lock, not genuinely broken — contention, never a gate defect | `FlowEvent.VerifyNotRun` (`BuildLockBusy: true`) | `BuildLockBusy` | #1796 |

Every other Failed/Cancelled/Succeeded path is unchanged. All six raise the **one** flag
`Domain.StepState.IndeterminateAwaitingResolution` (`Projection.StateProjector`), which is the single
predicate `Status.WorkflowOutcome.DescribeTerminal` and `Scheduling.RetryEngine.MayRetry` each read —
one arm apiece, never one check per producer. Alongside it, `Domain.StepState.IndeterminateProducer`
(F1, #1593 review) records which of the five `Domain.IndeterminateProducer` values raised it — the
discriminant `baton resolve`'s admission
test reads (Consumer obligations, below), replacing an earlier `LatestCapturedResponseFile` null/not-null
read that could not tell `ContractFailure` (which DOES have something to reject: the conductor's
judgement after inspecting the workspace) from `VerifyFailed`/`Arrested` (which never do).
`VerifyFailed`/`Arrested` additionally carry human-readable diagnostic text on
`Domain.StepState.IndeterminateReason`; that field is **display only and never a gate**
(`WorkflowOutcomeAndExitCodeTests.An_IndeterminateReason_without_the_flag_describes_as_Failed_not_Indeterminate`
is the discriminating control for that claim). A `ContractFailure` step is never automatically retried
either: re-running blind on a potentially mutated workspace is refused the same way, via the one
`IndeterminateAwaitingResolution` arm — and a `--reject` of it stays retry-foreclosed afterward, the
same as a rejected `CapturedResponse` since the #1877 ruling below made rejection terminal for every
producer. `baton settle` (S2, tracked on #1586) is expected to
be able to settle a room *to* `Indeterminate` for the worktree-fingerprint shape; until it lands, that
fifth source is reachable only by a test fabricating a `terminal.json`/status-view shape directly.

**Behaviour change (#1593 F3):** the bounded self-iteration pattern (a worker exits 0 having written a
declared output whose `OutputCondition` is unsatisfied, gets retried, and eventually satisfies it) no
longer retries. `ContractValidator.Validate` reports `UnsatisfiedOutputReason.ConditionFailed` the same
way it reports `Missing`, and `OutcomeClassifier.Classify`'s uncaptured-contract-failure arm does not
distinguish the two — both settle `ContractFailure` Indeterminate. This is the #1593 ruling's own
reasoning applied to a second shape, not a separate decision: an exit-0 worker that fails its output
contract has done unknown work on the workspace, whether the contract violation is a missing file or a
failed condition, so re-running it blind is wrong either way. A worker relying on the old
retry-until-satisfied pattern now settles `Indeterminate` on its first unsatisfied attempt and needs an
explicit `baton resolve --reject --reason <text>` before a fresh dispatch can try again.

**A timeout on a mutated workspace (#1373, operator ruling 2026-09-01).** Same class as #1593's
exit-0/no-output ruling, reached from the other exit reason. A `CoreExitReason.TimedOut` kill keeps
today's retryable `Failed` **only while the workspace has nothing to lose**: the failed attempt's
`WorkerBinding` — and therefore its workspace — is reused across attempts within a step, so attempt
N+1 starting from zero re-does or clobbers whatever attempt N left on disk. Measured 2026-09-01 (four
implement lanes, 60-minute timeout each): all four hit the timeout, all four were auto-retried onto a
workspace attempt 1 had already mutated — two carrying a finished commit, two carrying 18 and 2
uncommitted files — and the conductor cancelled all four by hand to salvage the trees. `Workspaces.
WorktreeProvisioner.ReadWorkspaceMutation` is the reading (its fourth entry point, per #1720 F2's
own reasoning about consumers with opposite safe defaults); it is asked about the provisioned worktree
when there is one and a tree-changing role's own working directory otherwise. Its safe default is
**mutated**: an unreadable workspace is one whose surviving work cannot be ruled out.

**This RELAXES F4 (#1593 review), deliberately, and the cost is real.** F4 keeps
`WorkerBinding.Target.WorkingDirectory` away from a *retry* decision because the operator's own
repository is routinely dirty for reasons unrelated to the execution; `changesTreeWorkingDirectory`
was the narrow exemption from it, safe precisely because #1622/#1390's evidence string decides
nothing. This makes it decide something — and it has to, because a write grant never gets an
auto-provisioned worktree (`RoleDispatch.ToBinding`), so **every** `ChangesTree` binding, `implement`
and `janitor` included, is the operator's own repository and F4's rule would exempt exactly the
population the ruling is about. Two consequences to know rather than discover: the commit half is a
delta against the sha read at spawn, while the **changed-path half is absolute** — `git status` counts
whatever is there, with no baseline taken — so a lane dispatched into an already-dirty tree settles
Indeterminate on a timeout even if its worker wrote nothing. The ruling accepts that: its own wording
is "uncommitted changes, untracked files" without a baseline, and the failure it prices is a clobbered
tree (unrecoverable) against a spurious conductor resolution (a minute of someone's time). A null path — an execution with nowhere to leave work — keeps the retry, and so
does #1089's finished-then-hung guard, which sits upstream of this branch and is unchanged (a
tree-changing worker that satisfied its contract has a mutated tree by construction; that is what
finishing looks like). The reason text opens with `OutcomeClassifier.TimeoutSentence`, so
`Status.WorkflowOutcome.IsTimeoutFailure` still reads a mutated timeout as a timeout. **Consequence
for a surface:** such a room now describes `Indeterminate`, so `baton run` no longer exits
`RunExitCode.Timeout` for it — that exit code stays for a timeout with nothing to salvage.

**The per-attempt start sha is journaled, not only held in memory (#1373 follow-up).** The commit half
of the reading above is a delta against the sha read immediately before Core is asked to run — durable
now as `FlowEvent.ExecutionAttemptStarted`, appended right after that read and before dispatch, so a
pump that crashes and recovers mid-attempt classifies a recorded timeout against **that attempt's own**
start commit rather than `WorkerBinding.Process.WorktreeBaseSha` (the worktree's one-time provisioning
base, which never moves across attempts and would otherwise misattribute an earlier attempt's own
commits to the one being classified). Absent on a pre-existing journal line or a dispatch with no
mutation-probe path, in which case classification falls back to `WorktreeBaseSha` exactly as before.

**The retry that does run carries a continuation brief (#1373, same ruling).** The other half: an
unmutated timeout is still retried, but `Scheduling.ContinuationBrief.ForRetryAfterTimeout` prepends
attempt N of M, the predecessor's budget, the kill cause, and the instruction to inspect the workspace
and finish rather than restart. Prepended to the prompt handed to the adapter
(`Dispatch.CoreDispatchTarget.WithPromptPreamble` — **both** `PromptText` and the argument equal to it,
since only the latter reaches the worker and the former is the archival `prompt.txt`), never to any
spec file on disk, and carried in memory rather than journalled: it is derived wholly from facts the
journal already holds. Scoped to a retry after a **timeout** — an ordinary failure's retry is unchanged
— and to the pump's own dispatch path: `baton resume` mints its request elsewhere and carries the
operator's own message instead. **Out of scope, deliberately:** extending a RUNNING lane's budget
mid-flight (the 2026-08-31 comment on #1373); that stays open on the issue.

**`baton dispatch <role> --continue <room-dir>` rehires a veteran into a NEW room for a follow-on
brief (#1381, 2026-09-01 design ratification), the general manual counterpart to #1373's automatic
retry-with-continuation above — same underlying mechanism (`WorkerInvocation.SessionId`/
`ResumeSession`, claude's `--resume` or Codex's `thread/resume`, M24), different trigger.** The named room's own single-worker
`bindings.json` entry is the one record of a vendor session id (`WorkerBindingConfigEntry.SessionId`
— the same field `baton resume`'s same-room continuation already reads, #1359 F6); `--continue`
carries it forward onto the new room's binding (`SessionId` + `ResumeSession: true`) rather than
minting a second record. **Supported adapters are claude and codex, same-adapter only** — Codex's
per-turn resume was measured by the 2026-09-04 probe; agy rehire is gated on its own headless
conversation-id-resume measurement, not yet run. An unsupported adapter or adapter change is refused
rather than silently falling back to a cold start.
**Fails the dispatch loudly** (Q4) on every check performable before the vendor spawns: the named room
missing, its bindings.json unreadable or naming more than one worker, an adapter mismatch, no
`SessionId` recorded (an adapter that cannot recover an id, or a worker stream that reported none), or the veteran room
not yet terminal (a still-running worker resumed concurrently races the vendor's own session-id guard,
which is an existence check, not a lock — `durability.session-id-guard-is-not-a-lock`,
docs/vendor-doc-audit.md). **Not detectable, and not claimed:** the vendor silently minting a fresh
session under the resumed id instead of truly continuing it — closing that gap needs the #546
existence-check-not-lock measurement to distinguish resume-refused from silently-started-fresh, a
separate, unshipped prerequisite pin.
<br><br>
**Why an ordinary `baton dispatch` captures rather than mints a session id:**
`WorkerInvocation`/the resolved `CoreDispatchTarget` argv
is built once per binding and reused verbatim across every #1373 retry of that binding. Minting a
client-side `--session-id` at bind time would bake it into that frozen argv — and claude's own
`--session-id` reuse is existence-guarded (sequential reuse refused, same sentinel as above), so a
retry after a timeout would fail outright rather than merely restart cold, a live regression on the
default dispatch path for the sake of a `--continue` chain's own root. `--continue` sidesteps this
entirely by only ever ferrying a session id FORWARD from an explicit prior dispatch, using the
adapter's resume operation (not existence-guarded the identical way) — so a chain's second, third, … link works exactly like the
first, and the ordinary no-`--continue` path is untouched. While a supported execution's stdout flows,
Baton parses Claude's `stream-json` init event or Codex's `thread.started` event through the adapter and records the reported id on the
room's own binding after the run settles (#1841); it records nothing when no id was reported. A retry
keeps the resolved argv unchanged and the newest attempt's reported id wins, so any terminal
Claude or Codex room with a reported id can seed a later `--continue` chain.
<br><br>
Provenance is journaled on the NEW room's own `.baton/room.json` marker — the identical
`ParentRoomDirectoryPath`/`ParentExecutionId` fields #1441's redispatch lineage already writes (both
verbs mean "the antecedent room/execution this one derives from"; sharing them is record-once, not
a coincidence), plus one field neither verb wrote before: `ContinuedSessionId`, non-null exactly when
this lineage came from `--continue` rather than `baton redispatch`. `fleet_status` (§6) surfaces it
under `continuedSessionId` on the identical `parentRoomPath`/`parentExecutionId` read path, so the
glass can render "continued from `<room>`" apart from "redispatched from `<room>`" by that field's
presence alone.

**The exit-0 quota veto (#1622 (a)).** A satisfied exit-0 run still settles `Failed`/`ExhaustedUntil`
— parked by `RetryEngine` exactly as an exit-1 quota failure is — when the vendor's own
quota-exhaustion signal is in the stream: a vendor can deny a tool or run a subscription dry mid-turn
while the worker goes on to write its contract output and exit 0, and nothing else here would catch
it. The evidence must be **vendor-controlled**, which is why this path asks the adapter a different
question than the exit-1 path does — `Outcomes.IFailureClassifier.TryClassifySatisfiedRunFailure`,
whose own doc states why (F1, #1720 review; the shape agy's matcher had). claude reads a typed
`errorCode`; agy reads its own terminal `result` envelope with a non-`SUCCESS` `status`. Stderr keeps
the fuller matcher on both vendors: it is the CLI's diagnostics, not the model's answer.
**Scope: the live dispatch path only.**
Crash recovery rebuilds the result without either tail (they are not written to the Event Store), so a
room whose engine crashed and recovered never gets this veto on either vendor — pre-existing, and the
same on the exit-1 path.

**Workspace evidence in the reason (#1593 F2).** #1593's acceptance criteria include: "a room that ends
`Failed` with uncommitted work in its workspace says so somewhere a person will see, rather than
reporting `outputs: []` and leaving the evidence to `git status`." The `ContractFailure` reason text
appends `Workspaces.WorktreeProvisioner.DescribeWorkspaceEvidence`'s bounded account (stray-path count
plus a commits-over-base count, reusing `Audit`'s own git-status read) whenever a worktree path is
available — a room that carries real, uncommitted work reads differently from one that carries nothing,
without a new mechanism. Null (no worktree, or genuinely nothing to report) leaves the reason
byte-identical to before this fix, which is why the fixed no-worktree case stays byte-pinned in
`OutcomeClassifierTests`.

**The resolved base (N2/P4, #1664 review) is meaningful only for a commit-ish ref.** `WorktreeBaseSha`
is `WorktreeProvisioner.ResolveBaseCommit`'s resolution of the worktree spec's ref against the source
repository, re-resolved on every `Walk`/`ReuseForResume` rather than persisted — safe for
`RoleDispatch`'s own `"HEAD"`, since a symbolic `HEAD` always names the commit the source repo was at
when the worker was dispatched, unaffected by anything the worker does inside its own detached
worktree. An operator-authored binding naming a **branch** does not get that guarantee: `git worktree
add` checks the branch out, so a worker's own commit advances it, and the next invocation re-resolves
the same branch ref to the worker's own commit — reporting a workspace that did real work as untouched.



**The dead-worker predicate reads a terminal RESULT, not a terminal SUCCESS (#1593 F6).**
`OutcomeClassifier.Classify`'s `isDeadWorkerWithoutResult` keys on
`CoreDispatchResult.TerminalResultObserved` — true when the worker emitted a terminal `result` record of
ANY status (success or self-reported failure), via `CoreDispatchTarget.DetectsTerminalResult` (agy's own
`IsTerminalResultLine`, wired the same way `DetectsTerminalSuccess`/`IsTerminalSuccessLine` already are).
`TerminalSuccessObserved` cannot answer this question by itself: it reads false both when no result
arrived at all (a dead worker) AND when one arrived reporting `is_error`/`FAILURE` (a worker that
finished and self-reported non-success — a contract failure, not a death, by #1622's own vocabulary).

**The claude adapter wires no terminal-result detector (N6, #1664 review) — a live asymmetry, not a
gap in this fix.** `DetectsTerminalResult`/`DetectsTerminalSuccess` are agy-only (a `Grep` for
`DetectsTerminalSuccess` scoped to `src/Baton.Vendors` returns `AgyWorkerAdapter.cs` alone — `git
grep` is outside the `review` role's ceiling as of #1683, so this is the harness Grep-tool
equivalent of the same search rather than a runnable shell command); a
claude-adapter worker's `CoreDispatchResult.TerminalResultObserved` is therefore always `false`, so
`isDeadWorkerWithoutResult` is unconditionally `true` for that vendor and the untouched-workspace read
(`Workspaces.WorktreeProvisioner.IsWorkspaceUntouched`) alone decides whether a claude worker's dead
exit stays retryable `Failed` or settles `Indeterminate` — agy gets the extra terminal-result
discrimination this section describes, claude does not. Pre-existing (predates #1593), not narrowed or
widened by it; recorded here because #1664's review found it undocumented outside a response report.

**Consumer obligations, ratified with the value itself.** `baton redispatch` refuses a bare
`Indeterminate` parent outright, with a diagnosis naming the resolution verb
(`RedispatchCommand.cs`) — unlike an ordinary `Failed`/`Cancelled` parent, which redispatches with a
stderr warning. The fleet glass renders a distinct `INDETERMINATE` chip and its own always-visible
section, the same placement `"Stalled"` earned in #1513/#1582 (`tools/fleet-glass/glass.html`).
**Nothing settles FROM `Indeterminate` except an explicit, recorded conductor resolution** — never
silently, never by default. `baton resolve` (#1608, `src/Baton.Cli/ResolveCommand.cs` +
`Mutation.MutationInterface.RecordCaptureResolutionAsync`) is that resolution verb **for all five
producers**, split across three verbs — see §2's table for the grammar and the table just below for
which verb each producer admits.
`RecordCaptureResolutionAsync` admits a target on `Domain.IndeterminateProducer` (F1, #1593 review), not
a bare `LatestCapturedResponseFile` null/not-null read: `CapturedResponse` admits both
`--accept-capture` and `--reject --reason <text>`; `ContractFailure` has no captured body to accept, so
only `--reject --reason <text>` admits it — the conductor's own judgement after inspecting the
workspace IS something to reject, even with nothing captured.

**`--close --reason <text>` (#1622 (d)/#1700, widened by #1796) is the verb for the other three
producers** — `VerifyFailed`/`ExecutionArrested`/`BuildLockBusy`, and a step Indeterminate for no
recorded producer at all (the legacy pre-#1593 shape) — none of which ever carried a captured response
for `--accept-capture`/`--reject` to act on. Before #1622 the first three of these had NO resolve path
at all: measured 9/2 on room `dispatch-implement-d898ff0f`, `baton resolve --reject` answered "settled
Indeterminate without a captured response … nothing for 'baton resolve' to accept or reject", and the
only way to stop the room reading "awaiting conductor resolution" on the glass was `baton room delete`.
`--close` settles the step `Failed` through the identical `Domain.FlowEvent.CaptureResolved(Accepted:
false)` room fact `--reject` uses — same journal shape, same downstream reading, admitted for a
different producer set. **The settle-shape table, stated once:**

| Producer | `--accept-capture` | `--reject --reason` | `--close --reason` |
|---|---|---|---|
| `CapturedResponse` | admits — writes the capture, settles `Succeeded` | admits — settles resolved-`Failed`, terminal | refused |
| `ContractFailure` | refused (nothing captured to accept) | admits — settles resolved-`Failed`, terminal | refused |
| `VerifyFailed` | refused | refused | admits — settles resolved-`Failed`, terminal |
| `ExecutionArrested` | refused | refused | admits — settles resolved-`Failed`, terminal |
| `BuildLockBusy` (#1796) | refused | refused | admits — settles resolved-`Failed`, terminal |
| no producer recorded (legacy) | refused | refused | admits — settles resolved-`Failed`, terminal |
| already resolved-rejected, still the step's latest (#1877) | refused | refused | admits — records `StepRetryForeclosed`, re-projects terminal |

**Ruled (#1877): rejecting a captured response with `baton resolve --reject --reason <text>` leaves the
step FAILED-TERMINAL, the workflow settled (never `Running`), `retryEligible: false`, and
`resolvedBy: conductor` with `rejected: true` and the conductor's reason recorded — an operator who
wants the work redone says so explicitly with a fresh `baton dispatch`/`baton redispatch`, which the
resolution itself unblocks (the room no longer reads `Indeterminate`, so `RedispatchCommand`'s
Indeterminate-parent refusal no longer bites).** Before this, a `CapturedResponse` reject deliberately
left the step retry-eligible: measured 9/4 on room `codex-1853-readonly-20260904-02`, where `baton
resolve --reject` recorded `rejected: true`/`resolvedBy: conductor` and the projection still read
`Running` with `retryEligible: true`, no worker and no pump alive, `--close` refusing ("no unresolved
indeterminate capture") and `cancel` refusing (nothing `Running` to target) — a room whose only
remaining closes were redispatching real vendor work, deleting the evidence, or hand-editing the
ledger. `Projection.StateProjector`'s `CaptureResolved(Accepted: false)` arm now forecloses retry for
**every** producer, and `ProjectionCheckpoint.Version` is bumped to 5 so an already-checkpointed room
replays under the new rule instead of serving the stale non-foreclosed state. **Follow-up:** there is
deliberately no `--reject --redispatch` flag — the retry intent is expressed by the separate
`redispatch`/`dispatch` verb, and adding one to `resolve` would need its own ruling, since `resolve`
never dispatches (§3, this section's opening claim).

**`--close` also closes a capture already rejected under the pre-#1877 rule.** An existing room whose
step is left dangling — a rejected `CaptureResolved` for what is still the step's latest execution,
no unresolved capture left to target — is admitted by `--close` and settled with a
`Domain.FlowEvent.StepRetryForeclosed(ForeclosedBy: "resolve --close")`, not a second
`CaptureResolved` (that event is recorded exactly once per `ExecutionIndeterminate`). The admission
predicate keys on that durable journal fact rather than on the step's retry-eligibility, which the
projector fix above has already removed by the time the verb runs — keyed the other way it would be
dead code, and a test of it would pass while asserting nothing. `StateProjector`'s
`StepRetryForeclosed` arm applies an event naming the step's latest execution when no retry is
scheduled for it, in addition to the pre-existing "matches the scheduled retry" arm; a stale name
still no-ops both ways.

**Both `--reject` and `--close` clear the "awaiting conductor resolution" text (#1622 (c)/(d)).** Before
this fix a resolved step's `error`/`LatestFailureReason` kept the pre-resolution sentence verbatim, and
`terminal.json`'s `rejected` stayed `false` — measured 9/2 on room `dispatch-implement-f7f9b614`: after
`baton resolve --reject --reason …` on an exit-0 contract failure, `status --json` still read `state:
Failed`, `rejected: false`, and `error` ending "awaiting conductor resolution". `StateProjector`'s
`CaptureResolved(Accepted: false)` arm now replaces the reason with a sentence naming the conductor and
carrying the reason (`Projection.StateProjector.BuildConductorResolvedReason`), and marks the step so
`resolvedBy` (§3 schema above) reads `"conductor"` — for either verb, since both are a recorded
conductor ruling. **`rejected` is narrower: `--reject` only** (F11, #1720 review). A `--close` is an
administrative settlement whose own CLI remedy text says the work already landed, so reporting it as
`rejected` would tell a harness branching on that field that a person refused work that in fact
shipped. The two verbs are told apart in `StateProjector`'s `CaptureResolved` arm — the only place the
producer that distinguishes them is still in scope, since the same arm clears it — via the admission
table above: `--reject` is admitted only for `CapturedResponse`/`ContractFailure`, `--close` only for
the other three. `--accept-capture` is unaffected: an accepted step settles `Succeeded` and carries no
failure reason to clear.

Those three producers reopen only through a fresh dispatch (in addition to `--close`) —
`ExecutionRequestAccepted` clears the flag, per `StateProjector`. `baton redispatch` against the same
parent room is not that fresh dispatch: its Indeterminate-parent gate refuses unconditionally and
nothing ever clears it for these three producers short of `--close` or a brand-new `baton dispatch`
room, which `RedispatchCommand`'s own refusal names by producer
(`Status.WorkflowStatusStepView.IndeterminateProducerKind`) rather than offering a verb guaranteed to
throw. `baton resolve` reads the step's
`LatestCapturedResponseFile`/`LatestUnsatisfiedOutputNames`
(already surfaced on `WorkflowStatusView`/`terminal.json`/`status --json`, per the schema below);
`--accept-capture` writes the captured response (header stripped,
`Outcomes.OutputMaterializer.StripCapturedResponseHeader`) under each declared output name and settles
the step `Succeeded` — the one path ever allowed to write under a declared name from a capture,
per `OutputMaterializer`'s own ruling — while `--reject --reason <text>` writes nothing and leaves the
step resolved-but-`Failed`. Either way a `Domain.FlowEvent.CaptureResolved` room fact records which,
carrying the conductor's own justification (required for `--reject`; the accept/reject choice already
speaks for itself for `--accept-capture`). **Fact then files, not files then fact (#1608 review finding
5).** `--accept-capture` journals `CaptureResolved` *before* writing the declared output(s) it names —
the fact is durable first, deliberately accepting that a crash between the two can leave the ledger
reading `Succeeded` with an output still missing, rather than the opposite gap the reverse order left
open (a declared output honestly on disk with the room still reading `Indeterminate` and the step still
resolvable, so a later `--reject` could record a rejection while the earlier file silently stayed put).
That gap self-heals: an explicit `baton resolve --execution <id>` naming an execution already accepted
for this exact id is treated as a repair request, not an invalid target, and re-materializes any missing
declared output(s) from the still-durable captured response — a no-op if nothing is missing (the
ordinary exactly-once refusal still applies then), and a fail-closed `InvalidCaptureResolutionException`
if the captured response itself is also gone, with nothing left to re-derive from. The prose-safe/all-or-nothing rule
(`docs/dispatch.md`'s "Roles" section) is not re-derived at resolution time: reaching an unresolved
capture at all already proves `OutputMaterializer.TryCaptureFinalResponse`'s gate passed for every name
in that list, at capture time. `RetryEngine.MayRetry` refuses an unresolved capture unconditionally,
via its own explicit arm on `StepState.IndeterminateAwaitingResolution` — deliberately not by reusing
`FailureClassification.Permanent`'s semantics, since `Indeterminate` carries no classification at all;
once resolved by an **accept**, the step's ordinary retry eligibility applies again. A **reject** or
**close** forecloses retry instead (#1877, the ruling above), so the resolved step itself is terminal.
`baton resolve` never re-drives the DAG itself, in either direction — an accepted step that leaves a
downstream step newly deliverable in a multi-step room, or any other still-deliverable step in the
same DAG, needs a follow-up `baton run --room-dir` to dispatch again, the same recovery
§7 already describes for a stalled room (F4, #1608 review — the acceptance case was previously
undocumented, reading as though only rejection needed it). `baton resolve` names that follow-up
invocation on stdout whenever the state it returns is not `WorkflowStatus.Terminal`, so a harness never
has to infer it — naming `baton decide` instead when that state is `Paused` (the pause-point case below,
where `baton run` re-enters the same unfulfilled obligation and cannot move the room), and
`baton run --room-dir` otherwise. See "Consumer obligations" above for the sentinel side of the same
non-Terminal case.

**Unless the step declares a `PausePoint`.** Every claim above about `baton resolve` being the *only*
path to an unresolved `Indeterminate` step assumes the step is not also a pause point.
`Scheduling.PauseEngine.GetPauseObligations` reaches a `Failed` step with `RetryEngine.MayRetry` false
through the same round-settled check regardless of *why* retry is refused, so a step that both declares
`PausePoint` and settles `ExecutionIndeterminate` becomes `StepStatus.Paused` with
`IndeterminateAwaitingResolution` still set. Both are pre-existing shapes of the pause path (the same
step read `Failed(Permanent)` with `MayRetry` false before #1608, with an identical `PauseEngine`
interaction) — #1608 changed what the eventual terminal word *is*, not whether a pause point can
intercept it first.

**Ruled (#1655, owner, 2026-09-02): option 1.** `ExternalDecisionValidator` refuses a `baton decide`
against a `Paused` step whose `IndeterminateAwaitingResolution` is still set — "resolve first, then
decide". The refusal names the room, the step, and the recovery verb (`baton resolve <room>
[--execution <id>] --accept-capture | --reject --reason <text> | --close --reason <text>`); only `FlowEvent.CaptureResolved`
ever clears the flag, matching every other producer's own rule above. A recorded external decision
never outranks the flag: admitting one anyway would leave `IndeterminateAwaitingResolution` set with no
`CaptureResolved` appended, so a later Terminal read of the room would still report `Indeterminate`
even though a conductor already decided its fate — exactly the silent default #1608 exists to forbid,
reached through `baton decide` instead of `baton resolve`. The room still reads `Paused`, not
`Indeterminate`, while it waits (`WorkflowOutcome.Describe` checks `Status` before `DescribeTerminal` is
ever reached — expected, since `Paused` is not itself a terminal word); the operator resolves the
capture via `baton resolve` first, which then makes the ordinary `baton decide` admissible again.

**`FlowEvent.StepRetryForeclosed`** (`src/Baton/Domain/FlowEvent.cs`) is the missing primitive the
quota-park symptom this section opened with rests on: before this slice, three events could clear a
step's `RetryNotBefore`/`RetryDelayMs`/`RetryScheduledForExecutionId` — `ExecutionRequestAccepted` (a
fresh dispatch), a `RetryWithRevision`-carrying `WorkflowResumed`, and `ExecutionCancelled`'s own
park-abort clear (#1563) — but none of them voids a scheduled retry *without* either dispatching a new
attempt or cancelling the execution outright. Clearing the fields alone would be wrong: an
`ExhaustedUntil` step bypasses `RetryPolicy.MaxAttempts` by design, so a cleared `RetryNotBefore` with
nothing else changed re-arms the step for immediate re-dispatch against a still-exhausted quota.
`StepRetryForeclosed` instead records the foreclosure as its own fact (`StepState.RetryForeclosed`),
which `RetryEngine.MayRetry` checks unconditionally ahead of every other bypass. Only the first two of
the three events above reopen a foreclosed step (`ExecutionCancelled` terminates the execution rather
than reopening it, so it does not clear `RetryForeclosed`) — a foreclosure is never permanent, but only
a fresh dispatch or a deliberate revision lifts one. A `Supersede` decision's own consequence dispatch is
not a third lifting path: it reopens through the same `ExecutionRequestAccepted` the first clause already
names, and a foreclosed step can never actually be the target of one in the first place —
`ExternalDecisionValidator` refuses any `Supersede` whose target's `StepStatus` is not `Succeeded`
(#271), and `StepState.RetryForeclosed` cannot be true for a step whose status IS `Succeeded`: reaching
`Succeeded` requires the `ExecutionRequestAccepted` that set the step's latest execution, and that same
event unconditionally clears `RetryForeclosedStepIds` for the step (`StateProjector`'s
`ExecutionRequestAccepted` case), independent of which retry it was dispatching. No verb in `src/`
appends this event yet either; S1 ships the primitive and its projection, replay-tested (including
the checkpoint `DeepCopy` hazard #1606 hit first for
`LatestCapturedResponseFileByStepId`/`LatestUnsatisfiedOutputNamesByStepId`), for S2's `baton settle`
to call.

**`FlowEvent.ZeroOutputsDespiteSubstantialWork`** (`src/Baton/Domain/FlowEvent.cs`) is the unconditional
tripwire the #1594 ruling's amendment 3 names: recorded independent of `OutcomeVerdict`/
`FailureClassification`, so it fires whether or not `OutputMaterializer`'s response capture
alongside it succeeded. Unlike the two vocabulary members above, **this one has a live producer in
S1** — `OutcomeClassifier.Classify` computes the evidence (`OutcomeClassification.SubstantialWorkNoOutputsEvidence`)
whenever a worker's own final usage line (read via the resolved adapter's `IWorkerUsageParser`, the
same seam `ExecutionUsageProjector` uses) reports real turns/tokens while every one of the contract's
declared outputs reads `Missing`, and `MutationInterface` appends the event from both classification
call sites — the live dispatch path and the crash-recovery `ToClassify` branch — right alongside the
outcome event, plus a loud `Console.Error` line. Scoped deliberately to the natural-exit-0,
contract-unsatisfied shape (#1594's own): a non-zero exit or a timeout never computes the evidence,
since those failures are already self-explaining and this tripwire targets specifically the case
where nothing else says why the work vanished. Diagnostic only — `StateProjector` records it durably
but it drives no `StepState`/`FlowState` consequence; it exists to be loud, not to change scheduling.

<!-- record-once-ok: #1583 src/Baton/Domain/FlowEvent.cs -->
**`FlowEvent.StepRebound`** (`src/Baton/Domain/FlowEvent.cs`) records that a step's execution was rebound
to a different adapter/model binding (#802 §3.3 / #1583). When crash-recovery resubmission encounters a
binding in `bindings.json` that diverges from the accepted request's recorded `Adapter`/`Model`, Flow
journals `StepRebound` (naming `PreviousAdapter`/`PreviousModel` → `NewAdapter`/`NewModel`) before
dispatching; `StateProjector` applies it as an override on the accepted request's `Adapter`/`Model` so
the rebind survives replay, and `ExecutionUsageProjector` re-attributes the execution's usage to the new
binding rather than silently misattributing it to the pre-crash binding. S6 extends this event (adding
`Effort` and a closed-token `Reason`, per #802 §3.3) rather than introducing a second one.

**`settledAt`/`settledBy` remain unimplemented — S2 scope, not S1's.** The proposal on #1586 §2
names two additive `terminal.json` fields (`settledAt`: ISO-8601 UTC, `settledBy`:
`"pump"`/`"settle"`/`"validation-refused"`) that let a reader tell "this room finished" from "this
room was declared finished after its pump died". Reserved here as a forward pointer only — no field
exists on `WorkflowStatusView` yet, and none should until S2 has a real writer for it.

### Engine-run verify and the three arrest triggers (#1623, #1682, #1691)

Two more producers, both ratified together (operator ruling, 2026-09-01 night, "option 3 ratified",
plus the same night's addendum on token consumption).

**The engine-run verify step.** A role may declare a `pixi run <task>` verify command (`implement` →
`gates-quiet`; `review`/`advise`/every other role → none, `WorkerRole.VerifyPixiTask`). On worker exit
0 with its output contract satisfied, the ENGINE — never the worker — runs the declared command once,
serialized against other lanes by the build lock each gate member takes for itself
(`tools/buildlock.py`); the engine holds no lock across the run (see N1 below). It runs via
`Baton.Mutation.VerifyRunner`, at the live-dispatch call site only (`MutationInterface`'s
`DispatchAndRecordOutcomeAsync`, between `OutcomeClassifier.Classify` returning `Succeeded` and the
outcome event append; deliberately not inside `Classify` itself, which also runs on the crash-recovery
replay branch against a possibly-defunct workspace). `FlowEvent.VerifyStarted`/`VerifyPassed` are
diagnostic-only; `FlowEvent.VerifyFailed` (`FailingMembers`, parsed from `tools/gates/gates.py`'s own
deterministic `summarise()` line) settles the step `Indeterminate` — never a blind retry, the ruling's
own wording — via the same `StateProjector.ApplyIndeterminate` helper the budget arrest below shares.
`Tail` (#1701) is each named failing member's OWN captured output, keyed off `gates.py`'s own
per-member `"  pass/FAIL  name  (exit code)"` summary lines — never a blind cut of the whole combined
`gates-quiet` run (`Baton.Mutation.VerifyRunner`'s own remarks are the canonical account of why, and
of its whole-stream fallback when a `gates.py` shape drift leaves no marker line to key off). This is
also what `baton status --json` now surfaces per step as `verifyTail`, so a flake is diagnosable from
the room without reconstructing it by hand. An operator cancel landing inside the verify window is the one exception: `VerifyFailedKind.Cancelled`
observed together with the caller's own cancellation token already firing means the journal *can*
decide (it holds the cancel), so `MutationInterface` appends `FlowEvent.ExecutionCancelled` instead —
room reads `Cancelled`, retry stays open, `VerifyStarted` survives as the diagnostic record that verify
was entered — not that a child necessarily existed: when the cancel precedes the spawn, the runner's
pre-spawn check returns `Cancelled` and no process ever ran. A verify *timeout* still settles
`Indeterminate` through the ordinary `VerifyFailed` path.
Cancellation dominates the child's exit code, whatever it was (#1722): `VerifyRunner.RunProcessAsync`
checks the caller's token both before spawning and after the child returns, so a fast child that
happens to exit 0 in the gap before a cancellation kill lands is still reported `Cancelled`, never
`Passed` — a cancelled verify reported as passed is a fail-open gate result, not a flaky race to
tolerate.
Worker briefs no longer ask for the full gate suite themselves; the prompt-level foreground instruction
from #1625 (`AgyWorkerAdapter.ForegroundGateInstructionText`) stays as belt (any slow command, not just
gates, should run in the foreground) now that this is the braces.

**Verify command resolution, and the not-run outcome (#1702).** The verify command run above is a
property of the WORKSPACE being worked on, not of the role — a role's `verify_pixi_task` bakes in an
assumption (that the workspace is this repo, or shares its task names) which fails by construction
against a foreign workspace (measured 2026-09-02: an `implement` lane dispatched with `--workspace`
pointing at a different, non-baton repo ran `pixi run gates-quiet` there, got "command not found",
and settled `Indeterminate` even though the worker's own exit and output contract were already clean).
`Baton.Mutation.VerifyCommandResolver.Resolve` resolves the command actually run, in precedence order:

1. `--verify <cmd>` (`DispatchOptions.VerifyCommand` → `RoleDispatch.ToBinding`'s `verifyCommandOverride`
   → `WorkerBindingConfigEntry.VerifyCommandOverride` → `WorkerBinding.Process.VerifyCommandOverride`) —
   mirrors `--token-budget`'s override plumbing end to end, including through `baton redispatch`.
2. The workspace's own declaration: a `.baton/verify` file directly under the dispatched workspace
   directory, whose first non-blank, non-`#`-comment line is the command line to run. The one
   repo-level declaration mechanism this issue picks (never also a `[tool.baton]` table in
   `pixi.toml`/`pyproject.toml`) — a plain-text file works for any workspace, pixi-based or not, which
   is the whole point since a foreign workspace's own task runner is unknown to this engine.
   **Read from the workspace's REVIEWED tree — `git show <merge-base of HEAD and origin/main>:.baton/verify`
   — at dispatch time, before the worker is spawned (#1708).** Both halves are load-bearing: a worker
   with write access to its own workspace must never be able to author the command that grades it, and
   a worker with shell access could commit one, so a read taken *after* the worker ran, or taken from
   the branch tip, would be no boundary at all. The merge-base is what makes the boundary hold across
   *executions* and not only within one: an `implement` lane committing and pushing is its ordinary
   designed behaviour, so `HEAD` on a lane branch contains the lane's own work, and a second dispatch
   or a `baton redispatch` into the same worktree would otherwise be graded by a file the previous
   worker wrote. Nothing a lane commits on its own branch changes what grades it. A fresh dispatch
   still takes a fresh snapshot, so a `baton redispatch` against a workspace whose *reviewed* declaration
   changed never runs a stale command.

   **Scope, precisely — the one shape where that wider claim does not hold.** When no merge-base can be
   computed (no remote at all, a default branch that is not `main`, unrelated histories) the read falls
   back to `HEAD` and the engine appends the diagnostic-only
   `FlowEvent.VerifyDeclarationUnreviewed(ExecutionId, Digest)`. On such a workspace the boundary is the
   narrower, per-execution one: this execution still cannot author what grades it, but an earlier lane's
   commit on the current branch is inside the baseline. That is announced rather than silent, which is
   the whole point of the event. `origin/main` is one fixed ref on purpose and is never discovered from
   `refs/remotes/origin/HEAD` or `remote.origin.*`: discovery would read repository config a worker can
   write, which is the boundary this read exists to hold — so a repo whose default branch is not `main`
   takes the loud fallback rather than a quiet, steerable answer.

   Two deliberate costs, both fail-closed: an **uncommitted** `.baton/verify` does not take effect (and
   neither does one committed only on the lane's branch), and a workspace that is not a git repository
   (or whose `git` cannot spawn) declares nothing, falling through to the role default rather than to a
   worker-writable file. When the working-tree file differs from what was read, the engine appends the
   diagnostic-only `FlowEvent.VerifyDeclarationIgnored(ExecutionId, CommittedDigest, WorkingTreeDigest)`
   — **on every execution that drift is observed, whatever the verdict**, because "did anything touch my
   verify declaration?" is a question an operator asks after a failed, arrested or cancelled run just as
   often as after a clean one. No `StepState` field, no status surface, no verdict consequence for
   either event: the journal is the whole record, and it is what tells an operator that the file in
   their workspace is not what graded the run.

   **The `git` spawns that produce this value are hardened (#1708), because their stdout decides what
   command grades the run** — a stricter job than `pixi task list`'s, whose output only chooses between
   running a gate and not. They run with a scrubbed environment (an allowlist, so no ambient `GIT_*`
   redirects the read and no `~/.gitconfig` is consulted), a `PATH` with every relative or
   workspace-rooted entry removed (so a `git.exe` dropped into the dispatched workspace cannot answer
   the question), `-c core.hooksPath=` and `--no-textconv` (so nothing written into the workspace's
   `.git/config` or `.gitattributes` filters — or executes against — the bytes), `--no-pager`, and
   **stdout only**, so a warning git writes to stderr can never be taken for the declaration's own first
   non-comment line. `Baton.Mutation.VerifyCommandResolver`'s single hardened-spawn helper is where all
   of that is applied; there is no second, unhardened path to this value.
3. `WorkerRole.VerifyPixiTask` (`implement` → `gates-quiet`; every other shipped role → none) — today's
   only source, run as `pixi run <task>`, unchanged. Baton's own repo keeps working unchanged under
   this arm: no `.baton/verify` file here and no `--verify` on baton's own dispatches.

An override/repo-declared command line runs through the platform shell (`cmd.exe /d /c <line>`, this
project ships Windows-only per #1405) rather than hand-tokenized; the role default stays a direct
`pixi run <task>` spawn, unchanged from #1623.

**A verify command is now a property of the workspace, not gated on the role declaring one.** Unlike
pre-#1702, where no `VerifyPixiTask` meant no verify step full stop, `Resolve` can still produce an
override or repo-declared command for a role that declares none (`review`/`advise`/every other
non-`implement` shipped role) — a workspace's own `.baton/verify` speaks for that workspace regardless
of which role is dispatched against it, the same way `--verify` does. This is deliberate, not a gap:
the whole point of #1702 is that verify answers "does this workspace's own gate suite pass", which a
role has no authority to opt a workspace out of. A red run through this arm settles `Indeterminate`
exactly like any other running-and-red verify.

**Before running, the engine checks the resolved command is runnable** (`VerifyCommandResolver.CheckRunnableAsync`) —
and after #1708 that probe exists for the `pixi run <task>` shape ONLY: a role-default task name checked
against that workspace's own `pixi task list` output, the #1702 measured shape. **An override or
repo-declared command line is not pre-probed at all.** It runs through `cmd.exe /d /c`, where a cmd
intrinsic (`echo`, `call`, `exit`, `cd`) is perfectly runnable while resolving to no file on PATH — so
the filesystem lookup that used to run here answered a question the shell was never going to ask, and
answering it wrong skipped a real gate. The exit code decides instead: a command line that cannot run
fails, and a failing verify is a `VerifyFailed` with output, never a silent pass. **The pre-flight
(`BuildLockBusy: false`) shape of `VerifyNotRun` has exactly two producers, and both are POSITIVE
evidence that `pixi run <task>` does not exist in this workspace** — never an inference from something
failing (a THIRD, `BuildLockBusy: true` shape exists since #1796 for a verify run that DID start; see
below, after this pre-flight probe's own two producers):

- **The workspace is not a pixi project** (#1708): no `pixi.toml`, and no `pyproject.toml` carrying a
  `[tool.pixi]` table, in the dispatched directory or any ancestor. A filesystem read, taken **before**
  any spawn. Reason: `"no pixi project: gates-quiet"`. The ancestor walk mirrors pixi's own manifest
  discovery and is load-bearing: a monorepo package dispatched with `--workspace` is a real pixi
  workspace whose manifest sits at the repo root, and calling that "not a pixi project" would skip a
  gate that plainly exists. Every uncertain answer (an unreadable manifest, an unresolvable path) is
  read as "it is a pixi project", which defers to the probe and then to the real run.
- **A SUCCESSFUL `pixi task list` whose combined output (`pixi` prints its listing, header or not, and
  every task name to STDERR, not stdout) positively names at least one other task while omitting the
  role's.** Reason: `"task absent: gates-quiet"` — #1702's own measured shape. "Positively names" means
  the output echoes at least one task the workspace's OWN manifest declares (`pixi.toml` `[tasks]` or
  `pyproject.toml` `[tool.pixi.tasks]`, read directly) — evidence that survives a pixi version bump,
  where pixi's header prose does not (#1797/#1836: 0.68 prints a header, 0.79 does not). Exit 0 whose
  output names no declared task (a degraded or short-circuited run under contention, indistinguishable
  from a genuine listing by exit code alone), or a manifest that declares no tasks or cannot be read, is
  not this producer — it falls into the "probe failed" paragraph below instead and defers to the real run.

A probe that fails — non-zero exit from a stale lockfile, a failed solve, an unparseable
manifest, a concurrent lock, `pixi` refusing to spawn at all, or an exit-0 run whose output names none
of the tasks the workspace's manifest declares (#1797) — is an engine-environment problem and
is never read as absence; it reports runnable and lets the real run decide, which fails closed. **The
ordering between the two is what keeps those compatible**: the manifest check runs first, so a
non-pixi workspace on a host with no `pixi` at all is answered by the filesystem, while a workspace
that *is* a pixi project and whose `pixi` will not spawn stays "the engine's own tool is broken" and
never softens into a not-run. If not runnable, the engine appends
`FlowEvent.VerifyNotRun(ExecutionId, Reason)` and stops: **no
`VerifyStarted` (never started), no `VerifyFailed`, and no
`Indeterminate` settle.** The execution's own already-`Succeeded` classification (this branch is only
reached when it is) decides `StepStatus`/`WorkflowOutcome` unassisted, exactly as if the role declared
no verify command at all — the same "ENGINE, never the worker" ownership as an ordinary verify run, just
never fired. `StateProjector` records the reason on `StepState.VerifyNotRunReason` (cleared on the
step's next `ExecutionRequestAccepted`, same as `IndeterminateReason`); `WorkflowStatusProjector`
surfaces it as `verify: "not-run"` / `verifyReason` on `WorkflowStatusStepView` (§3 schema above) and
`fleet_status`'s `FleetStepStatusView` copies it verbatim, so `baton status --json`/Fleet Glass can
render "unverified" instead of a bare `Succeeded` — Fleet Glass's own `UNVERIFIED` chip. **A verify
command that actually STARTS and then fails still settles `Indeterminate` exactly as before** — #1702
only changes the "never ran at all" case; a genuinely broken gate is not softened into a pass. A
pre-flight probe cancelled by the operator's own cancellation token is never read as "not runnable" —
it falls through as if runnable, so the real (already-cancelled) attempt below resolves the SAME
cancellation the ordinary verify-window handling above already covers, rather than a second, divergent
cancellation path.

**A build-lock wait timeout is `BLOCKED`, not `FAIL` (#1796).** `tools/buildlock.py` serializes every
MSBuild-owning `pixi` task on this machine (see that file's own docstring); a verify run whose gate
suite includes `lint`/`fmt-check`/`test-no-build` can lose the race for that lock to an unrelated
concurrent lane and time out waiting for it — contention, not a broken gate. `buildlock.py` exits its
own distinct code (75, `BUILDLOCK_BLOCKED_EXIT`) on that timeout rather than the generic 1 a wrapped
command's own failure would use, and prints one machine-recognizable line starting `buildlock:
BLOCKED`. `tools/gates/gates.py` reads that exit code alone (never the message text — the module's own
"exit code, not text, is the contract" rule extends here) and reports that member `BLOCKED` rather than
`FAIL`; its own summary line reads `GATES: BLOCKED n of m -- <names>` when every non-passing member is
blocked, but still `GATES: FAIL n of m -- <names>` (naming only the real failures) when a genuine
failure sits alongside a blocked member — a real failure always wins the headline. A blocked-only run
is not a pass either: `gates.py` deletes rather than writes its own local gate receipt for one, so a
following `pre-push` hook cannot read a blocked run as "gates already ran".

`Baton.Mutation.VerifyRunner` parses gates.py's summary line the same way it already parses `GATES:
FAIL` (§ above): a `GATES: BLOCKED` line with no `GATES: FAIL` line present yields
`VerifyFailedKind.BuildLockBusy` (a `VerifyRunner`-internal discriminator, never written onto
`FlowEvent.VerifyFailed` itself) and a reason string of the shape `"build lock busy for Ns (holder:
<cmd> since <t>)"`, parsed out of buildlock's own BLOCKED line inside that member's tail — a parse miss
degrades to a bare `"build lock busy"` rather than fabricating a holder. `MutationInterface` appends
`FlowEvent.VerifyNotRun(ExecutionId, Reason, BuildLockBusy: true)` for this outcome, **not**
`VerifyFailed` — unlike the pre-flight shape above, `VerifyStarted` for this execution DID fire (the
run genuinely started), so this is a distinct shape of the same event, not a repeat of the "never
started at all" case. `StateProjector`'s `VerifyNotRun` arm treats `BuildLockBusy: true` as its own
branch: it settles the room `Indeterminate` (`IndeterminateProducer.BuildLockBusy`, admitting `baton
resolve --close --reason <text>` the same way `VerifyFailed`/`Arrested` do — see the producer table
above and "Consumer obligations" below) rather than the diagnostic-only, still-`Succeeded` reading the
pre-flight shape gets — see `FlowEvent.VerifyNotRun.BuildLockBusy`'s own doc for why a lock-contention
timeout may not be silently read as a pass. `fleet_status`/`baton status --json` surface it exactly
as any other `Indeterminate` reason (`firstFailureReason`, `indeterminateProducerKind`), not through the
`verify: "not-run"` field the pre-flight shape uses — that field stays reserved for a step still read
as ordinarily `Succeeded`.

**Post-exit delivery check (#1788).** For a role whose catalog entry sets `WorkerRole.DeliversBranch`
(today, only `implement`) — a role whose brief convention ends in a push — `MutationInterface` runs one
more read-only assertion after the worker exits 0 AND the ordinary engine-run verify above has already
passed or did not run (never instead of it, and never before it): (1) the workspace's `HEAD` is
reachable from `origin/<branch>` (`git ls-remote --exit-code --heads origin <branch>`, then `git fetch
origin +refs/heads/<branch>:refs/remotes/origin/<branch>` and `git merge-base --is-ancestor HEAD
origin/<branch>`), and (2) when a PR is expected (`--expect-pr`, defaulting to
`role.DeliversBranch`, overridable per dispatch — `Baton.Vendors.RoleDispatch.ToBinding` resolves the
effective bool there rather than leaving it null, so a plain-`bool` default trap on
`WorkerBindingConfigEntry.ExpectPr` can never silently disable the check for a role the catalog does
mark), an open PR exists for that branch (`gh pr list --head <branch> --json number`). Two lanes shipped
`implement: Succeeded` reports describing a push and a PR while their branch sat only local — the
motivating measurement.

A failure appends `FlowEvent.VerifyFailed` with `VerifyFailedKind.DeliveryFailed` and `FailingMembers`
naming exactly which of the two is missing — `branch-not-pushed`, `pr-not-open`, or both — settling
`Indeterminate` via the same `IndeterminateProducer.VerifyFailed` path an ordinary gate failure uses, so
`baton resolve`'s admission rules and `verifyTail` (the `Tail` field, carrying a short human-readable
line per failing member) apply unchanged. `--heads` scopes the `ls-remote` query to branch refs only —
measured against real git, a same-named TAG on origin would otherwise make the query exit 0 and defer to
a fetch that then fails to resolve `refs/heads/<branch>`, downgrading a real "never pushed" into a
misleading `NotRun`. The `git fetch` step's explicit refspec form makes the `origin/<branch>` ref the
`merge-base` read below compares against independent of this workspace's own `remote.origin.fetch`
configuration — git DOES opportunistically update that ref on a plain `git fetch origin <branch>` when a
standard fetch refspec is already configured (measured; an earlier draft of this paragraph claimed
otherwise), but this check has no way to assume every workspace it runs against carries one, and the
explicit form costs nothing to also cover the case where it doesn't.

**`NotRun` is reserved for positive evidence the check itself could not run** — `git`/`gh` missing from
PATH, or a spawn that ran but could not reach the remote (network, auth, or a blocked credential prompt —
the two network-touching spawns override `GIT_TERMINAL_PROMPT`/`credential.interactive`/`GCM_INTERACTIVE`
so a host needing a credential refresh reports `NotRun` rather than hanging the engine's pump on a prompt
nothing can answer) — mirroring `VerifyCommandResolver.CheckRunnableAsync`'s own "never an inference from
something failing" rule exactly. A branch that plainly never existed on origin at all (`git ls-remote
--exit-code --heads` exiting `2`) is the OPPOSITE of unmeasurable — it is the loudest form of this issue's
own defect — so it settles `branch-not-pushed`, never `NotRun`. A detached `HEAD` (the literal string
`HEAD` from `git rev-parse --abbrev-ref HEAD` — a workspace state that can arise however the workspace
was prepared, not tied to any one provisioning code path) is read the same deliberate way: a worker that
exits 0 without ever checking out a branch has delivered nothing pushable, which is a real failure, not
merely unmeasurable. Both push and PR checks are independent once a branch name resolves — a `git`/network
hiccup on one never suppresses a real failure already found on the other, and a real failure on either
always wins over any `NotRun` from the other side of the same call, the same "a failure is stronger
evidence than an inconclusive read" precedence `VerifyRunner`'s own not-run/failed distinction already
rests on. An operator cancellation landing inside this check's own window settles `ExecutionCancelled`,
mirroring the ordinary verify window's identical carve-out — never a `VerifyFailed`/`VerifyNotRun`
misreporting an execution the operator asked to stop.

**Known gap, stated rather than closed here.** This check asks only "is the workspace's CURRENT `HEAD`
pushed, with an open PR" — never "did THIS execution move `HEAD` or open that PR". A redispatch into an
already-pushed, already-PR'd workspace that changes nothing still settles `Succeeded`. Distinguishing that
would mean comparing against a pre-dispatch SHA, or gating on `OutcomeClassification.WorkspaceChanged`
(already computed for `ChangesTree` roles) — a materially different assertion than the two the issue
named, left for a follow-up rather than folded in here.

**Withholding the vendor subagent tool (#1802).** A role's catalog entry may set
`WorkerRole.AllowsSubagents` (`allows_subagents` in `WorkerRoles.json`; that member's own doc carries
the motivating measurement) — `false` (the default; every role but `advise` leaves the key omitted)
makes `Baton.Vendors.RoleDispatch.ToBinding` carry that value onto the resolved
`WorkerBindingConfigEntry`/`WorkerInvocation` unchanged, and each adapter withholds its vendor's own
fan-out tool: `ClaudeWorkerAdapter` appends `Agent,Task` to `--disallowedTools` (both names — `Task` is
`Agent`'s older one, still honoured by the CLI), and `AgyWorkerAdapter` adds its own subagent tool names
(`AgyWorkerAdapter.SubagentAndTaskTools`) to the denied-tools list — independent of, and in addition to,
whatever each already withholds from the role's own `PermissionGrant` (that same list was already
withheld on agy whenever writes or shell were withheld, #1387;
this is a second, independent trigger, since `implement`'s grant keeps both true). `advise` is the one
shipped role that sets `allows_subagents: true` (`WorkerRole.AllowsSubagents`'s own doc has why); on agy
that role's own grant already withholds writes, so its subagent trio stays denied there regardless —
this flag only ever narrows a vendor's reach, never widens beyond what the grant's own categories
already permit.

**`--output` delivery is unconditional on the worker's own write, never on verify's verdict (#1702).**
Before this fix, `DispatchCommand.CopyPrimaryOutputToOverride` only copied a produced output when its
step's terminal `Status` read `Succeeded` — but a verify failure (or, pre-#1702, the not-run case
misread as a failure) settles the step `Failed`/`Indeterminate` even though the worker already wrote
its declared output before the engine's own (later) verify step ran at all. The measured cost: a
foreign-workspace `implement` lane's report sat unseen in the room's artifacts while `--output` was
never written. The copy is now keyed on the step actually having executed (`LatestExecutionId is not
null`) and the declared output file existing on disk at that execution's artifact path — the real,
unconditional gate — regardless of what verify (running-and-green, running-and-red, or #1702's
not-run) decided.

**That gate is deliberately broader than the verify case that motivated it (#1708).** Keying on
"the step executed and the file exists" also delivers from an execution that never exited naturally —
an `ExecutionArrested` (token/tool-step budget), an operator `ExecutionCancelled`, or a timeout — where
the worker was killed *during* the write and the file on disk may be **partial**. That is the intended
behaviour, not an oversight: a partial report is better evidence than none, it is the only account of
what the arrested worker had done, and nothing about delivering it reads as success — the room word and
the process exit code both still say arrested/cancelled/failed, and `--output` has never been a claim
about the verdict. A caller that needs "this file is complete" reads the terminal state, never the mere
existence of the file.

**The per-execution token budget (#1682: arrests on billed, not context level).** #1623's own review
recorded the ceiling as "not shown reachable" from `600,000 − 200,000(context) = 400,000 needed from
Σoutput` — that derivation described a monitor tracking `context_level + Σoutput_tokens`, where the
input side was a *level* (each new turn's reading REPLACED the running total, never added to it).
That is the defect #1682 fixes: vendors bill INPUT per turn, not once for a whole conversation, so a
worker making many tool calls over a large, mostly-unchanging context bills far more than a level-based
read ever shows. Measured directly against #1682's own evidence rooms
(`dispatch-implement-38c24d11`/`f7b24a80`, real `.stdout.log` captures): room `38c24d11` finished at
794,940 vendor-reported `total_tokens` while the OLD level-based reading never exceeded 258,160 at any
point in the same 70-turn replay (`TokenBudgetReplayTests.RED_the_same_replay_does_NOT_arrest_at_any_point_under_the_pre_1682_level_based_reading`,
which reproduces the pre-#1682 formula turn-by-turn against real per-turn data and pins that peak).

`Baton.Mutation.TokenBudgetMonitor` now accumulates `WorkerUsage.BilledTokens` — a running Σ, across
every incremental usage line, of whichever of `input_tokens`/`output_tokens`/
`cache_creation_input_tokens` that line's vendor actually reports for real. On agy that is
`input_tokens + output_tokens`; on claude it is `cache_creation_input_tokens` alone, the other two
being placeholders (#1706, below — this paragraph originally read `input_tokens + output_tokens [+
cache_creation_input_tokens, on claude]`, which described what the code summed and not what the
vendor meant by it). Deliberately excludes `thinking_tokens`: verified against
every usage line in room `38c24d11`'s real capture, `Σinput_tokens + Σoutput_tokens` reproduces the
vendor's own `Σtotal_tokens` exactly on every sampled line (e.g. one real line: `input_tokens: 14205,
output_tokens: 443, thinking_tokens: 349, total_tokens: 14648` — `14205 + 443 = 14648`, and adding
`thinking_tokens` would overshoot) — `thinking_tokens` is a breakdown already counted inside
`output_tokens`, not a separate billed component. This corrects an arithmetic claim in #1682's own
issue body and evidence comment ("Σ input+output+thinking, which equals it here" — it does not; found
while fixing, corrected in this same change rather than filed separately). `ContextLevelTokens` keeps
its pre-#1682 meaning unchanged — the latest `input_tokens + cache_read_input_tokens +
cache_creation_input_tokens` reading, a level, DISPLAY-only now, never what a budget arrests on. One
term of that sum is gone on claude as of #1706: with `input_tokens` no longer read there, the engine's
level is the two cache counters alone. Numerically it is a 2-token difference, but it is a difference
in *definition*, and it makes the engine's level and glass's own `contextTokens` (§6 below, which still
includes the placeholder) subtly different quantities rather than the same one.
`CacheReadTokens` on the monitor's own snapshot changes from a level to a running Σ (display-only, same
convention).

**Fixed (#1666): the level no longer dips on a fan-out parent's sub-agent turns.** docs/vendor-doc-audit.md's
#1623 re-review N5 measured that a sub-agent's own turns appear as ordinary top-level
`"type":"assistant"` lines in the SAME stream, marked only by a non-null `parent_tool_use_id` at the
root of the line — and that `TokenBudgetMonitor.OnStdoutLine` replaced `_inputLevel` unconditionally
on every matching line regardless of which bucket produced it, so a sub-agent's typically much smaller
context could lower the tracked level on exactly the turns where a fan-out parent was doing the most
work — a systematic under-count in the runaway-fan-out shape the budget exists to catch. `WorkerUsage`
now carries `IsSubAgentTurn` (set by `ClaudeUsageParser.TryParseIncrementalUsage` off that same root
field); `TokenBudgetMonitor` tracks the parent's and the sub-agent bucket's levels SEPARATELY and
reports their max, so a smaller sub-agent reading can never overwrite a larger parent one (or the
reverse) — only a genuine same-bucket change still moves the reported level. Review F3: the sub-agent
bucket is also CLEARED the moment a parent line arrives, so it is transient rather than a permanent
cross-bucket high-water mark — a genuine drop in the parent's own context (e.g. after compaction)
always shows again as soon as the parent speaks, even if some earlier sub-agent turn reported a larger
figure than either the parent's old or new reading. `BilledTokens` itself was
never affected: it already sums every line's delta, parent or sub-agent, deduped only by `message.id`
(above) — this fix is scoped to the DISPLAY-only level, not the arrest predicate. **This fix is
claude-only, and #1742 measured why the gap it fixes cannot occur on agy through the same path.**
`AgyUsageParser.TryParseIncrementalUsage` sets no `IsSubAgentTurn`; a real agy fan-out capture
(`invoke_subagent`, docs/vendor-doc-audit.md's #1742 entry, capture `dispatch-implement-2807af38`)
shows why that is not an omission to close: unlike claude's `parent_tool_use_id`-marked
`"type":"assistant"` lines, agy's own `step_type:"subagent"` line — the only line the parent's stream
carries for the whole sub-agent call — has no `usage` object at all (that entry has the exact reader
gate this trips and why). The sub-agent's own turns are written to an entirely separate transcript
(its own `conversation_id`, a `log_uri` outside this dispatch's `.stdout.log`), never entering the
parent's stream. So every agy line the engine reads usage from is, by construction, a
parent-conversation line — agy's level keeps the pre-#1666 replace-on-every-line behaviour, but that
behaviour cannot dip from a visible sub-agent reading the way claude's could, because no such reading
is visible to replace it with. Scoped to the one capture available while agy was quota-exhausted, not
to every agy build. `tools/fleet-glass/pusher.py`'s
`extract_live_counts` built its own `contextTokens`/`cacheReadTokens` the same "latest line, no
`parent_tool_use_id` filter" way — mirrored the same defect, and now applies the same rule (review
F5, `tools/fleet-glass/pusher.py`'s `extract_live_counts`): a sub-agent line (`parent_tool_use_id` a string) is skipped for the
`context` assignment entirely, so only a parent line ever updates glass's reported figure — the same
outcome the engine reaches via its two-bucket clear above, by a simpler route (no bucket to clear,
because the sub-agent reading was never recorded in the first place). Engine and glass now agree;
stated once here.

**Cache-read tokens are excluded from `BilledTokens` (#1686 review F5, stated here for the first time —
the exclusion was previously implicit in the formula, never justified).** `cache_read_input_tokens`
(claude) / `cache_read_tokens` (agy) are read onto `WorkerUsage.CacheReadTokens` but never added into
`BilledTokens`, for the same reason `thinking_tokens` is excluded but with a different shape: a cache
read is vendor-reconcilable — agy's own `total_tokens` already excludes it, so `BilledTokens` stays
comparable to that vendor-reported figure — and it is a genuinely cheaper token (billed at a discount
against a fresh input token on every vendor this project targets), so `BilledTokens` is a **token
count**, not a **cost proxy**: it never claims to weight a cache-discounted token differently from a
full-price one. The consequence is real and worth stating rather than leaving to inference: room
`38c24d11`'s real capture reports 8,459,818 cache-read tokens over the same 70-turn stream whose
`BilledTokens` total is 794,940 — more than ten times the counted figure, all of it invisible to the
budget by design. A cache-heavy claude lane's actual context-carrying cost is therefore materially
larger than `BilledTokens` alone suggests; an operator reading this figure as "the whole bill" rather
than "the budget-relevant, vendor-reconcilable token count" would be misled, which is exactly why this
paragraph exists.

**On claude, the live billed figure is a FLOOR, not a measurement (#1706).** The vendor fact this
rests on — which columns of a mid-stream `assistant` line are real, whether a repeat line ever
revises them, and whether any other event in the shipped mode carries them — is
`docs/vendor-capabilities.md`'s, under this issue's correction to that file's per-message usage-shape
finding — measured there and not restated here. Two consequences follow for this engine, and they are
what this section rules on.

The live-measurable billed quantity on this vendor is Σ`cache_creation_input_tokens` alone;
`ClaudeUsageParser.TryParseIncrementalUsage` reads that and nothing else, and every reading it
produces carries `WorkerUsage.BilledIsFloor`, sticky through `TokenBudgetMonitor`'s Σ, surfaced in
`StateProjector.DescribeArrest`'s text and as glass's trailing `+`.

Per room, billed tokens (`input + output + cache_creation`), live versus the terminal line's
authoritative whole-tree `modelUsage`:

| Room | pre-#1706 live | post-#1706 live | terminal (authoritative) | under-read | subagent messages |
|---|---|---|---|---|---|
| `dispatch-implement-3dc5e21a` | 344,225 | 342,557 | 884,568 | 542,011 (38.7% seen) | 37 of 153 |
| `dispatch-implement-5d9686dd` | 228,536 | 227,657 | 294,769 | 67,112 (77.2% seen) | 0 of 94 |

**The magnitude does not move; only the label and the reconciliation do.** 344,225 → 342,557 is 0.5%.
Saying otherwise would be the second wrong number #1706 warned about. What the fix buys is that the
figure no longer claims to be complete, and that the terminal reconciliation is now derivable per
room (`ExecutionUsageView`'s `billedTokens`/`liveBilledTokens`/`billedUnderReadTokens`, from
`baton status --json`).

**The shortfall is room-dependent, and its mechanism is UNMEASURED.** Swept over all 127 claude rooms
under `~/.baton/rooms` carrying a terminal `modelUsage` (126 excluding the one whose stream had rolled
over), the live-seen fraction runs **min 0.282, p10 0.312, p25 0.444, median 0.768, p75 0.817, max
0.911**. So no single correction factor exists and the figure is labelled rather than scaled.

**A first attempt at the mechanism was wrong and is retracted here rather than filed.** This section
briefly claimed the spread was driven by subagent fan-out — a subagent's first request being uncached
and therefore billing real `input_tokens`. It was inferred from the two evidence rooms, where it fit
exactly, and the sweep falsifies it: of the 113 rooms that spawned no subagent at all (the terminal
line's own `subagent_stats.spawned`), **35 still show `modelUsage` differing from top-level `usage`**,
including `dispatch-review-c012659f` at 374,918 whole-tree input tokens against a top-level 86. Rooms
that *did* spawn subagents sit at the *high*-seen end (0.79–0.80). Whatever drives the gap, it is not
fan-out, and this register does not name a mechanism it has not measured. #1709's normal-room
population is where a real answer would come from.

**The product consequence, stated rather than left to inference.** A budget is compared against the
floor, so `implement`'s shipped 1,200,000 is an effective ceiling of `1,200,000 ÷ seen` in real tokens:
~1.55M on the best-seen of the two evidence rooms, ~3.1M on the worse, and **~4.26M** on the worst room
in the whole 126-room sweep (`dispatch-review-b4f33edb`, seen 0.282).
`TokenBudgetReplayTests.The_live_floor_widens_the_effective_claude_ceiling_by_the_room_s_own_under_read_factor`
pins the two evidence rooms' figures so this paragraph and the code cannot drift; the 4.26M is the
sweep's, and the sweep is not a committed instrument (see the note on `tools/room-rate-sweep` below).
The token trigger is therefore materially looser on claude than on agy, whose incremental usage IS its
real usage; closing that asymmetry needs a live figure this vendor does not emit, so it is a bound to
know about, not a defect left open.

**The terminal read is `modelUsage`, which is ≥ top-level `usage` (#1706).**
`ClaudeUsageParser.TryParseFinalUsage` now sums `modelUsage` (camelCase keys, one entry per model)
and falls back to the main-thread-only top-level `usage` object only when no `modelUsage` is present.
The prior ruling left `modelUsage` unread on the grounds that summing it needs a per-model breakdown —
true of COST, which this shape carries no field for, and false of a TOKEN COUNT. The discriminating
control is that this reads a different field rather than rescaling every figure: room `5d9686dd`, whose
`modelUsage` equals its top-level `usage` field for field, does not move at all (294,769 either way)
while `3dc5e21a` moves from 298,095 to 884,568. Across the 127-room sweep 78 rooms are in `5d9686dd`'s
position and 49 in `3dc5e21a`'s, so both arms of that control are well populated — but which room falls
where is NOT predicted by subagent fan-out, per the retraction above, and the control must not be read
as evidence about scope.

**Claude's incremental usage line is deduped by `message.id` (#1686 review F6).** Measured against
real `.stdout.log` captures (`dispatch-implement-3dc5e21a`: 246 usage-bearing `"type":"assistant"`
lines, only 153 distinct `message.id`s; `dispatch-implement-5d9686dd`: 176 lines, 94 distinct ids;
`dispatch-review-9ef0b9c3`: 85 lines, 33 distinct ids) — claude's stream-json splits a single API
response's content blocks across several consecutive `assistant` events that each repeat the SAME
`message.id` and an IDENTICAL `message.usage` object. That identity is measured, not assumed, and is
what settles #1686 review F4: `docs/vendor-capabilities.md` records zero repeat lines differing from
their id's first sighting, so first-sighting dedupe is correct and no "read the last one" rule would
recover anything. Summing every line's usage without deduping would
have over-counted `BilledTokens` by roughly 40-60% on these three real rooms alone.
`ClaudeUsageParser.TryParseIncrementalUsage` now also reads `message.id` onto
`WorkerUsage.MessageId`, and `TokenBudgetMonitor` tracks the set of ids already accumulated for the
execution, skipping any repeat. agy has no analogous id in its shape and is unaffected —
`TokenBudgetMonitorTests.TryParseFinalUsage`'s own terminal-line polarity test already established that
a terminal `"type":"result"`/`"event":"result"` line is never re-summed on either vendor; this closes
the SAME class of defect on claude's mid-stream line, which had no discriminating control before this
change.

`IWorkerUsageParser.TryParseIncrementalUsage` reads claude's mid-stream `"type":"assistant"`
`message.usage` and agy's DONE-state, `step_type: "agent_response"` `"step_update"` `usage` (both
measured against real captures, `docs/vendor-capabilities.md` and this PR's own test fixtures
respectively) — composed onto `CoreDispatchTarget.OnStdoutLine` the same way `CoreDispatcher`'s own
`DetectsTerminalSuccess` composes onto an existing sink, never replacing one. **The engine's agy gate
now matches glass's own `tools/fleet-glass/pusher.py`'s `extract_live_counts` exactly (#1686 review
F4)**: both require `state == "DONE"` AND `step_type == "agent_response"` before reading a `usage`
object — previously the engine read any DONE step_update carrying a `usage` object regardless of
`step_type`, which would have double-counted against glass's own count had a DONE/`step_type: "tool"`
line ever carried one (measured against the real `38c24d11` capture: it never does, so this closes a
gap the evidence set has not yet exercised, not one observed firing). A shared-fixture test
(`AgyEngineAndPusherUsageGateTests`) pins that the two implementations agree on the same real captured
line. **The same DONE-or-ERROR unification applies to the tool-COUNT gate too (#1686 round-two review
F3), reopened one field over from the usage gate above**: `pusher.py`'s `extract_live_counts` counted a
`step_type: "tool"` step only at `state == "DONE"`, while the engine's own
`ClaudeUsageParser.CountToolSteps`/`AgyUsageParser.CountToolSteps` count at either terminal state
(`DONE` or `ERROR`) — so a failed agy tool call incremented the cap the engine arrests on without
incrementing the count an operator sees on the lane card. `pusher.py` now counts `state in ("DONE",
"ERROR")` for the tool branch too, and `AgyEngineAndPusherUsageGateTests` was extended to cover the
tool-count gate alongside the usage gate it already pinned. The monitor reads every top-level
`"type":"assistant"` line with no discrimination by
`parent_tool_use_id` for the billed Σ or the tool count — whole-tree, including subagent turns, the
SAME completeness property `docs/vendor-doc-audit.md` (#1623 re-review N5) measured missing from the
terminal line's own cumulative figure (undercounts by ~22% with a single subagent in the tree). (The
tracked context LEVEL does discriminate by `parent_tool_use_id`, as of #1666 above — this paragraph is
about the billed Σ and the tool-step count only.)

Crossing the budget cancels the execution via a linked `CancellationTokenSource` (never the
operator-facing `CancellationRequested`/`ExecutionCancelled` pair — that's intent; this is the engine's
own) and appends `FlowEvent.ExecutionArrested` (`Usage`, `LastToolNames` — the last few tool calls
observed, from the same incremental read — plus `Reason`/`ToolStepCount`, below) instead of an ordinary
outcome. Settles `Indeterminate`, same as a verify failure. A role with no budget and no
`--token-budget` override, and no `MaxToolSteps`, runs unwatched, same as before this issue; a role
whose resolved adapter has no registered `IWorkerUsageParser` also runs unwatched rather than refusing
to dispatch.

**The tool-step cap (#1682, second producer, independent of usage parsing) — unit fixed and
false-positive floor measured (#1686 review F1/F2).** `WorkerRole` carries `MaxToolSteps`
(`implement` 610, `review` 100, `advise` unset; every other role none) — a second, independent arrest
trigger on the running COUNT of tool-step lines, entirely apart from whether usage ever parses on the
stream at all (a stream with malformed or absent usage lines still gets the tool-step protection;
`TokenBudgetMonitorTests.The_tool_step_cap_fires_at_cap_plus_one_with_zero_usage_lines` proves this).
The cap arrests at cap+1 (the first line whose running count exceeds `MaxToolSteps`) with
`FlowEvent.ExecutionArrested.Reason = ArrestReason.ToolStepCap` — independent of, and can fire before,
the token-budget trigger; whichever fires first wins and the monitor never re-arms.

`IWorkerUsageParser.CountToolSteps` counts ONE REAL TOOL CALL, in the same unit on both vendors, stated
once here: claude counts every `tool_use` content block in a `"type":"assistant"` message (not just the
first, unlike `TryParseToolName`'s single display name, which would undercount a multi-tool turn); agy
counts a `step_update` with `step_type: "tool"` and a non-empty `tool_name` ONLY at its terminal
lifecycle state (`DONE` or `ERROR`), not its `ACTIVE` heartbeat. Before this fix agy counted BOTH lines
per real call — the same catalog number bought half as many real tool calls on agy as on claude, and
`implement`'s prior 80 was calibrated against that doubled count (the issue's own "138 tool steps" for
room `38c24d11`, which is 69 real calls × 2 lifecycle lines each). Measured against both real evidence
captures — `38c24d11` (69 `ACTIVE`, 69 terminal) and `f7b24a80` (86 `ACTIVE`, 85 terminal; the one-line
gap is a call still `ACTIVE` when the room was cancelled) — every real call's `ACTIVE` and terminal
lines pair up 1:1, so counting terminal-only exactly halves the old scalar without losing or
double-counting a call.

**The false-positive floor, measured the same way the token budget's was (#1686 review F1).** Per-room
real-tool-call counts, this fixed unit, from the rooms actually available:

| Room | Role | Adapter | Real tool calls |
|---|---|---|---|
| `dispatch-implement-3dc5e21a` | implement | claude (override) | 161 |
| `dispatch-implement-5d9686dd` | implement | claude (override) | 99 |
| `dispatch-review-9ef0b9c3` | review | claude (tier default) | 50 |
| `dispatch-review-00f716a7` | review | claude (tier default) | 47 |
| `dispatch-implement-38c24d11` (evidence, not normal) | implement | agy (tier default) | 69 |
| `dispatch-implement-f7b24a80` (evidence, not normal) | implement | agy (tier default) | 85 |

No `advise` room in `~/.baton/rooms` carries a real vendor JSON stream at all — every `.stdout.log`
under a completed `advise` room is a short plain-text echo of the final report, not a captured
`stream-json`/agy-envelope log — so `advise`'s cap stays unset (null) rather than a guess; it was
already unset before this change. `implement`'s and `review`'s two named rooms are the SAME ones
already read for the token budget below; both happen to have run on the `claude` adapter override
rather than `implement`'s/`review`'s own tier default, which this fixed unit makes safe to compare
directly against agy-native rooms for the first time. `review`'s cap is set to `100` (≈2× 50) — the
reviewer's own room (`9ef0b9c3`, reviewing this PR) made 50 real calls and would have been
false-arrested under the OLD `40` cap.

**`implement`'s cap is set from the 26-room agy-native sweep instead (#1686 review F7), not from the
two claude-adapter rooms above.** The first round's `322` (≈2× 161, the higher of the two claude-adapter
rooms) was knowingly below a population this PR itself had already measured: a sweep of 26
`Succeeded`, agy-native `implement` rooms under `~/.baton/rooms` (real-tool-call counts, this fixed
unit, recounted directly against each room's own `.stdout.log` with the shipped `CountToolSteps`):

```
47, 58, 58, 58, 64, 67, 90, 96, 110, 117, 144, 169, 181, 186, 188, 189, 220, 233, 234, 257, 262, 269,
278, 286, 407, 482
```

(`dispatch-implement-e9516da2` at 407, `dispatch-implement-7d25642b` at 482 — the same two outliers
the first round's text named, confirming this is the same 26-room population; the "0" the first round's
range cited does not reproduce under a direct recount and is dropped here as unverified — see the F7
recount below for the corrected floor, 47.) `p95` (nearest-rank, n=26) is `407` — the same
`dispatch-implement-e9516da2` room. Applying the same 2×-style safety multiplier this PR already uses
elsewhere, at 1.5× rather than 2× (p95 is already a tail figure, not a typical-room figure the way the
161/50 medians were): `round(407 × 1.5, nearest 10)` = `610`. All 26 measured rooms sit at or under 482,
comfortably under 610, so the measured false-arrest rate on this population at the shipped cap is `0/26`
(0%); the residual risk is in the unmeasured tail past the 95th percentile of a 26-room sample, which
`--max-tool-steps` (below) remains the escape hatch for.

**Honest replay result: under this measured, false-positive-safe cap, neither evidence room is caught
by the tool-step trigger, and neither is caught by the token trigger at the shipped 1,200,000 budget
either.** `38c24d11` made only 69 real tool calls in its whole captured stream and `f7b24a80` only 85 —
both well under any cap wide enough to avoid false-arresting the population above (`implement`'s normal
range alone reaches 482). This is not a case of "raise the cap until it stops firing" being wrong in
principle — the SAME 2×-normal method the token budget already used — it is that this specific pair of
runaway rooms burned an enormous number of tokens per real tool call rather than making an enormous
NUMBER of calls, which a call-count cap cannot see by construction. `TokenBudgetReplayTests` (below)
replays the real interleaved stream through the shipped configuration and asserts this directly: no
arrest, on either trigger, for `38c24d11`. The tool-step cap's real, provable value is bounding a
DIFFERENT failure shape — a poll loop or a call-count runaway — not this one; §3's prior text claiming
the cap "is what actually arrests both evidence rooms" no longer holds under the corrected unit and is
retracted here rather than left standing. **The uncaught failure shape — burning tokens per real tool
call rather than making an unusual number of calls — was taken up as #1691**, whose measured answer is
the next block.

**The billed-rate trigger (#1691): the mechanism ships, no role arms it, and the premise it was opened
on is refuted.** #1691 proposed that room `38c24d11` is a RATE anomaly where #1682 proved it is not a
TOTAL anomaly, and that a windowed billed-rate limit (proposal: 250,000 billed tokens in any trailing
5 minutes) could therefore ship as a role default. Measured over the whole room corpus rather than the
three rooms the issue named — `python tools/room-rate-sweep/sweep.py --sweep`, which is the register
for these numbers and is re-runnable as the corpus grows — **no such value exists.** Six
`dispatch-implement` rooms that PRODUCED THEIR WORK burned billed tokens FASTER than `38c24d11`'s
68,240/minute, topping out at 123,531/minute (1.81×), and the closest is still 1.07× — the populations
do not merely overlap, they interleave with no gap to put a threshold in. Those figures involve no
modelling at all: each room's billed total and its `executionStarted`..`executionExited` span are both
measured exactly. "Produced their work" is the load-bearing filter and is stricter than it looks — a
`Natural` uncancelled exit says only that the PROCESS ended cleanly, so the sweep reads the outcome
events instead (at least one `executionSucceeded`, no `executionFailed`); `dispatch-implement-e5567544`
passes the weaker test while journalling `executionFailed: Contract not satisfied` three times and is
excluded (#1707 review, which caught it counted). **Corrected 2026-09-02 (#1707 review §1c): the answer
does not rest on any reconstruction, and the prior text here — a windowed sweep across
`--offsets uniform|duration` — should never have been the load-bearing leg, because both
reconstructions rescale onto the same measured `executionStarted`..`executionExited` span and so
cannot in principle represent burstiness the span does not already imply.** A reconstruction-free bound
follows instead from the `separation` block alone, by pigeonhole over disjoint 5-minute windows:
`dispatch-implement-46d513e7` billed 1,754,518 over a 14.2-minute span, so across `⌈14.2/5⌉ = 3`
disjoint windows some one of them held at least `1,754,518 / 3 = 584,839` — no offset, no modelling, no
reconstruction. The runaway's own true peak is bounded above by its entire per-execution total, 794,940
(single-execution arithmetic: `794,940 / 698.948 s = 68,235/min`, matching the recorded 68,240/min). Any
limit that arrests the runaway while sparing `46d513e7` must therefore sit in `(584,839, 794,940]` —
which requires the runaway to have concentrated at least 73.6% of its whole burn into 5 of its 11.65
minutes, against reconstructed peaks of 464,238 (uniform) and 372,774 (duration), kept here only as
illustration of how far short of that band both land. The same bound applies to the rest of the
delivered population: `dispatch-implement-55aa75ae` floors at 473,318, `dispatch-implement-46e842cd` at
401,799, `dispatch-implement-6142bd07` at 399,600, `dispatch-implement-17d325bf` at 349,769,
`dispatch-implement-7d25642b` at 336,908 — so the proposed 250,000 provably false-arrests all six
delivered rooms, not only `6142bd07`. The concrete cost is pinned in `BilledRateReplayTests`: the proposed 250,000
arrests `38c24d11` at usage line 27 of 70 (278,565 billed, 65% of the burn saved) **and arrests
`dispatch-implement-6142bd07` at usage line 30 of 221** — an `implement` lane that journalled
`executionSucceeded` with no failure, at 1,198,800 billed, under the shipped budget and cap, which
nothing else would have touched. Arrest forecloses retry, so that is a permanently killed legitimate
lane.

Two things follow, and neither is a hedge. First, **`WorkerRole.BilledRateLimit` is null for every role
in `WorkerRoles.json`, and that null is the finding** — pinned by
`WorkerRoleCatalogTests.The_shipped_catalog_arms_no_billed_rate_trigger_on_any_role` over EVERY role,
not the three carrying a token budget, because #1686 review F1 was exactly a fourth role quietly holding
an unmeasured cap while three documents said it held none. Second, the mechanism ships anyway, complete
and tested, because it is what makes a future calibration possible: `TokenBudgetMonitor` now takes an
injected `TimeProvider` and keeps a trailing-window Σ of the SAME deduped per-turn billed samples
#1682's total already takes, and exposes the largest window it ever held
(`SnapshotPeakBilledInWindow`, accumulated whether or not a limit is armed). **Corrected 2026-09-03
(#1709): an earlier draft of this paragraph said that reading was recorded only onto
`FlowEvent.ExecutionArrested`, which inverted the population the calibration actually needs — a
normally-completed execution journalled no `ExecutionArrested` at all, so the ledger carried a peak for
exactly the lanes that DIDN'T need one and none for the false-positive side that does.** The peak is now
journalled once on whichever terminal outcome event an execution actually reaches:
`FlowEvent.ExecutionSucceeded`/`FlowEvent.ExecutionFailed` carry the identical `PeakBilledInWindow`
field `ExecutionArrested` already did — `FlowEvent.cs`'s own doc comment on the field states exactly
when it is stamped versus left null. `ExecutionUsageProjector` surfaces it as `peakBilledInWindow` in
`terminal.json`/`status --json`'s per-execution usage object — `ExecutionUsageView.cs`'s own doc
comment on that field states how it differs from the same view's `liveBilledTokens`. #1686 review F14's
phase 1 is fully landed by this: the live measurement exists, is exposed, and now reaches every terminal
outcome, not only an arrest — a sweep can read journalled measurements across a normal-room population
instead of reconstructing per-line arrival times from `.stdout.log`.

Mechanics, stated once. The window is fixed at 5 minutes (`TokenBudgetMonitor.BilledRateWindow`) and
only the ceiling is configurable, so two roles' limits stay comparable; it is closed at both ends, so a
sample sitting exactly on the edge still counts. Arrival time is the clock, not anything on the line:
only claude stamps a WALL-CLOCK time (`timestamp`) on its usage line, and reading one vendor's stamp
while timing the other by arrival would make the trigger mean two different things. **Corrected
2026-09-02 (#1707 review): an earlier draft of this paragraph said agy "carries no time field at all",
which is false — every agy `step_update` carries `duration_seconds`, that step's own elapsed time.** It
is not a wall-clock stamp and cannot be used live, but it gives the sweep a second, independent
reconstruction (`--offsets duration`) that cross-checks the uniform one. **Corrected 2026-09-02 (#1707
review F2): the two reconstructions do NOT reproduce every replay result exactly, and the prior claim
here was unfalsifiable from the tree.** `BilledRateReplayTests` is fixtured exclusively under
`--offsets uniform` — the sweep's own `--offsets` argparse default — so its exact assertions
(`464,238` for the runaway's disabled-trigger peak, `278,565`/`255,121` at arrest) are uniform-only
numbers; no test arm runs `--offsets duration`, and none of this repository's checked-in fixtures cover
it. What is true and checkable is narrower: both reconstructions preserve the same ordering and the
same arrest/no-arrest outcome on every case `BilledRateReplayTests` exercises. They do not agree on the
runaway's own peak — the duration reconstruction puts it at 372,774 against uniform's 464,238 (and the
disabled-cap comparison at 623,222 against 538,687) — so a claim that hinges on the exact figure, not
merely the ordering, must say which reconstruction it used. No timestamped billed history exists
anywhere on disk for a completed room, so a reconstruction of some kind is unavoidable when reading
history, and the sweep says which one it used at every use. **There is deliberately no warm-up**: for an execution's first 5 minutes
the trailing window covers the whole run, so an armed limit behaves as a second, tighter budget over
that opening stretch. A warm-up would blind the trigger to exactly the opening burst it exists to see —
the runaway's own crossing lands between 2.6 and 4.5 minutes depending on which reconstruction is used,
i.e. inside the warm-up any plausible one would have imposed.

Ordering: the token budget wins over the tool-step cap wins over the rate limit when one line crosses
more than one, so a ledger written before #1691 and one written after describe the same failure the
same way. `ArrestReason.BilledRate` is the third member, and `StateProjector.DescribeArrest`'s switch
over that enum is now driven-tested against `Enum.GetValues<ArrestReason>()`
(`ExecutionArrested_DescribeArrest_covers_every_ArrestReason_member`) rather than relying on its
throwing default arm to surface a missing case in production.

**`--billed-rate-limit <n>` (#1691)** is the only way a rate trigger is ever armed today. It mirrors
`--token-budget` end to end — a positive whole number of billed tokens per 5-minute window, refused the
same way on a non-positive value, rejected on a workflow-template dispatch the same way
`--timeout`/`--token-budget`/`--max-tool-steps` are — and `baton redispatch` carries AND overrides it on
**both** its paths, the specific hole #1686 review F2 found in `--max-tool-steps`'s own threading, with
`RedispatchBindingTests` pinning both polarities.

**A cross-vendor caveat that is load-bearing for all three triggers, not just this one (#1706).**
`BilledTokens` is not the same quantity on the two vendors: on claude it is a FLOOR, on agy a
measurement. The measurement, the per-room table and the consequences are stated once, above, under
"On claude, the live billed figure is a FLOOR, not a measurement (#1706)" — not restated here. What it
means for THIS trigger is the one clause that belongs here: every token-side threshold is tight on agy
and loose on claude, and #1691's premise is a direct consequence, since it compared an agy runaway
against two claude reference rooms.

**`--max-tool-steps <n>` (#1686 review F11)** is `baton dispatch`'s override for this axis, mirroring
`--token-budget` end to end — a positive whole number of real tool calls (this fixed unit), or refused
the same way `--token-budget` refuses a non-positive value; rejected on a workflow template dispatch
the same way `--timeout`/`--token-budget` are, since a template's phases each carry their own role's
cap. `baton redispatch` also carries it (#1686 review F2): `RedispatchCommand`'s amended-spec path
previously dropped `MaxToolSteps` on the floor when rebuilding through `RoleDispatch.Materialize`, so
an operator who dispatched with `--max-tool-steps` and then redispatched with an amended brief got the
role's default back with no warning; both redispatch paths now pass
`options.MaxToolSteps ?? parentEntry.MaxToolSteps` the same way the token-budget axis already did, and
`RedispatchOptionsParser` gained its own `--max-tool-steps` flag mirroring `--token-budget`'s. Given the
measured population above, this is not merely symmetry with the token axis: even a cap set from p95×1.5
covers only the 26-room sample it was measured against, so an operator whose legitimate lane sits past
that sample's tail still has the same dispatch-time (and now redispatch-time) escape hatch the token
axis already had.

**Defaults, re-derived (#1682: `implement`'s token budget, in billed tokens).** `implement`'s
`TokenBudget` moves from 600,000 to 1,200,000, measured from two recent, normally-completed (never
arrested) `implement` rooms under `~/.baton/rooms` — `dispatch-implement-3dc5e21a` (~65 minutes,
628,302 billed tokens) and `dispatch-implement-5d9686dd` (~55 minutes, 507,402 billed tokens).
**Corrected 2026-09-02 (#1691): those two figures are the NON-deduped sums, taken before #1686 review
F6's `message.id` dedupe landed in the same PR that stated them. Under the accounting that actually
shipped they are 344,225 and 228,536** (recompute with `python tools/room-rate-sweep/sweep.py --sweep`),
so 1,200,000 is ~3.5× the higher measured normal rather than the ~2× this paragraph derives it as.
**Corrected 2026-09-02 (#1707 review F4): this budget does NOT "err loose, not tight" — the PR's own
`fasterAndDelivered` corpus shows it arresting four delivered agy lanes in a single execution, each
over the shipped 1,200,000 ceiling and each carrying `produced_work: true`: `dispatch-implement-7d25642b`
(2,358,353, ~2× the budget), `dispatch-implement-46d513e7` (1,754,518), `dispatch-implement-55aa75ae`
(1,419,955), and `dispatch-implement-46e842cd` (1,205,398). Only `dispatch-implement-6142bd07`
(1,198,800) and `dispatch-implement-17d325bf` (1,049,306) sit under it. The budget is left unchanged in
THIS PR regardless — not because it is loose, but because the fix is blocked on an operator ruling this
PR does not make: whether `implement`'s ceiling should move per-vendor or the scalar should simply rise,
and the number that would settle either question is itself under-read on claude by #1706 above, so
re-deriving it belongs with that fix rather than here.** The OLD
600,000 default, read under the NEW billed-token arithmetic rather than the OLD level-based one it was
tuned for, would already false-arrest the FIRST of those two ordinary, successful lanes mid-run: that
part of the case stands.

**The re-derivation #1707 deferred here (#1706), taken up and answered in halves.** #1707 deferred
re-deriving this number to #1706 on the ground that the claude side of it was under-read. #1706 bounds
that under-read but does **not** close the derivation, and the reason is `claim-scope`: the two
populations disagree, and a single cross-vendor scalar cannot be sized from either alone.

- **claude (this issue's population).** The old stated method — "roughly 2× the higher of the two
  measured normal totals" — was applied to 628,302 and 507,402, sums taken under pre-#1686 accounting,
  before the `message.id` dedupe. Deduped they are 344,225 and 228,536; corrected for the mid-stream
  placeholder columns and read against the terminal whole-tree line (#1706's table above) they are
  **884,568 and 294,769**. Applying "2× the higher" to the corrected pair would give ~1,769,000, not
  1,200,000. On this population 1,200,000 false-arrests nothing: 884,568 sits under it with ~26% margin
  (`TokenBudgetReplayTests.HONEST_neither_delivered_claude_room_arrests_at_the_shipped_implement_budget_live_or_terminal`),
  making the shipped value **~1.36× the higher corrected normal room** — TIGHTER than the "2×" the old
  text claimed, not looser. Tighter in intent than in effect, since what a live claude budget is
  actually compared against is the floor, not these corrected figures (see the effective-ceiling
  paragraph above).
- **agy (#1707's population, above).** On the same shipped ceiling, four delivered agy lanes are over
  it — `7d25642b` (2,358,353), `46d513e7` (1,754,518), `55aa75ae` (1,419,955), `46e842cd` (1,205,398).
  There the value is not loose, it is already false-arresting.

**So the two halves point in opposite directions, and a scalar cannot satisfy both.** ~26% of headroom
on the higher of two delivered claude rooms and four delivered agy rooms over the same line is not a
number that is "a bit off" — it is evidence that **`implement`'s ceiling is not a single-vendor-sizable
quantity**, because `BilledTokens` does not mean the same thing on the two vendors (the FLOOR paragraph
above). Raising the scalar to cover agy's 2.36M would make it ~2.7× the higher claude room and, read
against claude's own live floor, an effective real ceiling past 8M on the worst-seen room in the sweep;
holding it at 1,200,000 keeps arresting delivered agy work. **`WorkerRoles.json` is therefore left
untouched by this PR, and the open item is named rather than passed on again: whether `implement`
carries a per-vendor budget or a single raised scalar is an OPERATOR ruling, not an engineering one, and
it is the last thing blocking the derivation.** #1706 does not defer it to #1709 — what #1709 collects
(a normal-room population) would inform the *value* chosen under either shape, but it cannot choose the
shape. Neither can a further measurement.

**On the sweep instrument.** `tools/room-rate-sweep/sweep.py` is on `main` (#1707) and is the
re-runnable instrument for the room figures above — `python tools/room-rate-sweep/sweep.py --sweep`.
#1706 extends its claude arm to this issue's accounting (cache-creation only, the mid-stream
input/output columns dropped) so the tool and `TokenBudgetMonitor` cannot disagree about what a claude
billed token is, and marks the vendor asymmetry in its output. The 126-room seen-fraction figures above
predate that extension and were produced by a throwaway script over `~/.baton/rooms`; the per-room
figures a claim rests on are pinned in `TokenBudgetReplayTests`, which is committed.

`review`/`advise` keep their pre-#1682 token-budget figures (250,000/150,000) unchanged — no comparable "two normal completed
rooms" measurement exists for those roles in this issue's evidence set, so their ceilings stay
carried-over-unverified in the same sense #1623's re-review already flagged, not freshly justified.

**Per-adapter token budgets — the SHAPE ships, the VALUE question stays open (#1745, operator ruling
2026-09-03, "I'm fine with per vendor token budgets").** The open item the paragraph above left
unresolved — "whether `implement` carries a per-vendor budget or a single raised scalar is an OPERATOR
ruling" — is answered on the shape only: `WorkerRole.TokenBudget` (`WorkerRoles.json`'s `token_budget`
key) is now a `TokenBudgetSpec` — either `Fixed` (one figure for every adapter, unchanged from before
this issue) or `PerAdapter` (a map keyed by adapter name). `WorkerRoleCatalog` parses the wire shape at
load and `TokenBudgetSpec.Resolve` (called from `RoleDispatch.ToBinding`, against the actually-
dispatched adapter, never earlier) states the exact fail-closed-vs-unwatched split for a map entry
that's missing; both carry their own reasoning and are not restated here. `--token-budget` still
overrides either shape outright.

**`implement`'s own value is deliberately UNCHANGED by this issue.** The 1,200,000 scalar this section
already found to be simultaneously ~26% loose against the best claude evidence and already
false-arresting four delivered agy lanes is not re-derived here: doing so needs a value decision this
issue's own acceptance criteria do not ask for ("depends on nothing; pairs with the burn ledger #1570
and the live burn view"), and the claude side of any such derivation is still bounded rather than
closed (the live-floor paragraph above). Only `review` demonstrates the new map shape, and its two
values (`{"claude": 250000, "agy": 250000}`) are deliberately equal to the pre-#1745 single figure —
the shape is exercised on a real shipped role with no behavioural change, not a fresh per-vendor
calibration. A future issue that DOES re-derive `implement`'s value now has a shape to put it in.

**The shared mechanism.** All four producers (engine-run verify, the token budget, #1682's tool-step
cap, and #1691's billed-rate limit) route through the one `StateProjector.ApplyIndeterminate` helper — flag, reason text,
foreclosure; the `IndeterminateAwaitingResolution` flag is what `WorkflowOutcome.DescribeTerminal` and
`RetryEngine.MayRetry` each check (one arm apiece), per the producer table above; `StepState.IndeterminateReason`
stays display-only, never itself a gate. `StateProjector.DescribeArrest` is the one place
`FlowEvent.ExecutionArrested.Reason` is switched on — a `null` `Reason` (a ledger line written before
#1682) reads the same as `ArrestReason.TokenBudget`, since every arrest recorded before #1682 was one
(the tool-step cap did not exist yet); the switch is total over
`TokenBudget`/`ToolStepCap`/`BilledRate`/`null`, and since #1691 that totality is a test over
`Enum.GetValues<ArrestReason>()` rather than a claim.

### Exit codes

`RunExitCode` (`src/Baton.Cli/RunExitCodeResolver.cs`), returned by `run`, `dispatch`, and
`resume` only — `cancel`/`decide`/`resolve`/`supply` keep the unchanged binary success/failure code
(`Program.cs`):

| Code | Name | Meaning |
|---|---|---|
| 0 | `Succeeded` | Every step succeeded |
| 1 | `Failed` | **Not** exclusively terminal-and-failed — see below |
| 2 | `ValidationRefused` | Provisioning/validation refused, independent of ledger state; the **sentinel write** (not the exit code) is what is conditional on `RoomLedgerProbe.HasLedger` (above) |
| 3 | `Timeout` | At least one step's failure is a timeout and none is a hard failure (`RunExitCodeResolver.ResolveFailed`) — **or** (#1378) `--wait --wait-timeout <minutes>`'s poll loop hit that bound before the room reached Terminal (`CommandResult.WaitTimedOut`); the room itself is still Paused/Running in that second case, not Terminal-and-failed — read `baton status` to tell the two apart |
| 4 | `Cancelled` | — |
| 5 | `RoomHeld` | Another Flow instance already holds this room — retry later, not a terminal outcome; no sentinel is written (`Program.cs`) |

<!-- record-once-ok: #1378 src/Baton.Cli/RunExitCodeResolver.cs -->
**Exit code 1 is not "terminal, a step failed."** `RunExitCodeResolver.Resolve` falls through to
`Failed` for **`Running` and `Paused` too** — any outcome that is not `Succeeded`, `Cancelled`, or the
resolved `Failed`/`Timeout` split (`RunExitCodeResolver.cs`, comment verbatim: *"Running or
Paused: the pump returned short of Terminal (no `--wait`, or `--wait`'s poll loop was cancelled --
e.g. Ctrl-C -- before the room settled; a `--wait-timeout` expiry is handled ahead of this and never
reaches here)... a caller that cares about 'still going' reads `status --json`'s `state` field
instead."*). Concretely: a harness runs `baton dispatch` without `--wait`, the lane reaches a gate and
pauses — the process exits **1**. Reading that as "a step failed" and abandoning a healthy, paused
room is the single most consequential misreading this table can produce, because §5's entire gate
contract depends on that paused room still being there to `baton decide` against. `Indeterminate`
(#1586 S1, above) also folds into exit code 1 — reachable since all three of §3's producers landed
(#1608's captured-response settle, #1623's `VerifyFailed` and `ExecutionArrested`), and named here
rather than left to an unlabelled wildcard, the same discipline the rest of this switch already
follows. A caller's `$?`/`%ERRORLEVEL%` branch sees `Failed`; read `state` (below) to tell it apart
from an ordinary `Failed`. What a harness reaches for once it does depends on which producer settled
it — `baton resolve` (§2) for a captured response, a fresh dispatch for a verify failure or an
arrest. The step's own failure reason (`StepState.IndeterminateReason`, mirrored onto
`LatestFailureReason` and so onto the schema's step `reason`) is what names which. **The rule: exit code
1 alone never tells you whether the room is done. Read `state` from `terminal.json` or `baton status
--json` to distinguish `Failed` from `Running`/`Paused`.** `--wait` makes `run`/`dispatch` block until
the room reaches Terminal or the wait is itself cancelled; `run`'s own `--wait-timeout` (#1378) bounds
that block and reports exit code 3 instead when it elapses first. Without `--wait`, a non-1/0 exit
code is the only signal a lane is even still going, and it is unreliable for that purpose by design.

### §3 schema — `terminal.json` / `baton status --json`

```
{
  "state": string,                     // WorkflowOutcome, e.g. "Succeeded" | "Failed" | ...
  "steps": [
    {
      "id": string,
      "state": string,                 // StepStatus token
      "execution"?: string,
      "linkedFrom"?: string,           // set when this step's latest execution is an `baton resume`
      "usage"?: ExecutionUsageView,
      "linkedFromUsage"?: ExecutionUsageView,
      "liveness"?: "alive" | "dead" | "unknown",  // #1375/#1513: present while this step reads "Running", or "Failed" with a RetryNotBefore still pending
      "exhaustedUntil"?: string,  // #1551: the ExhaustedUntil park's reset instant (ISO-8601, UTC) -- gating rule at §6 schema below
      "verifyTail"?: string,      // #1701: the failing gate member(s)' OWN captured output for a VerifyFailed Indeterminate -- see "Engine-run verify" below. Distinct from "verify"/"verifyReason": that pair says verify never ran, this says it ran and went red.
      "resolvedByConductor"?: boolean,  // #1622 (c)/(d): true iff this step's terminal state was set by an explicit, non-accepting `baton resolve` ruling (--reject or --close); omitted when false
      "workspaceChanged"?: boolean,     // #1622 (b)/#1390: present ONLY for a tree-changing role's (implement/janitor) Succeeded settle -- see the paragraph below the table
      "hollow"?: boolean,               // #1622 (b)/#1390: present under the identical gate as workspaceChanged, true only when workspaceChanged is false AND the contract declares zero outputs
      "hollowReason"?: string,          // #1622 (b)/#1390: present only when hollow is true
      "verify"?: "not-run",       // #1702: present iff the latest attempt's resolved verify command failed its pre-flight runnability check -- an ordinarily-Succeeded step, never a gate. See "Verify command resolution" below.
      "verifyReason"?: string     // #1702: the pre-flight verdict -- "task absent: <task>", the only shape #1708 leaves reachable -- present only alongside "verify"
    }
  ],
  "outputs": [string],                 // resolved output paths
  "error": string | null,
  "try": string | null,                // corrected-invocation text; only set on a pre-ledger refusal
  "rejected": boolean,                 // #1377, widened by #1622 (c) and re-scoped by F11 (#1720 review): true iff some step settled via `DecisionType.Reject` OR `baton resolve --reject` -- NOT `--close`, which is an administrative settlement rather than a refusal
  "resolvedBy"?: string,               // #1622 (d)/#1700: "conductor" when some step settled via a non-accepting `baton resolve` ruling (--reject OR --close); omitted otherwise. The signal for a `--close`, which sets this without setting `rejected`
  "terminalAt"?: string                // #1157: when this run ENDED (ISO-8601, UTC) -- absence rules in "The terminal instant" below
}
```

**The terminal instant (#1157).** `terminalAt` is when the run ended, and it is a real record rather
than a stand-in for one: it is the writer timestamp already stamped onto the terminal event's own
journal envelope (`LogEntry.FlowLogEntry.WriterUtcTimestamp`, written by `FlowEventLogWriter`).
No `FlowEvent` field was added for it and no workflow-terminal event exists to add one to —
`FlowEvent`'s own remarks say why workflow-level status is a projection and never a stored event.
`Projection.TerminalInstantResolver` is the sole derivation; `Store.WorkflowTerminalProbe` (the
terminal authority) and `Status.WorkflowStatusProjector` (the view) both call it rather than each
answering for themselves.

Specifically it is the **last** transition into `WorkflowStatus.Terminal`, not the last line's stamp
and not `flow.jsonl`'s mtime. Both of those move when anything is appended after a run ended — a
`captureResolved` settlement, a late Core lifecycle line, a copy that touches the file — and a grace
window keyed on a value a later append can move is the defect this closes, not a smaller version of
it. Last rather than first because terminality is not monotone: a fresh `executionRequestAccepted`
reopens a foreclosed or indeterminate step, so a room can end, be re-driven, and end again. (`baton
resolve --reject` used to be a second way in — it left a rejected `CapturedResponse` retry-eligible.
Since #1877 it does not; the conclusion rests on the dispatch clause alone.)

**What a restart may assume, and what it may not.** A room whose `flow.jsonl` carries no terminal
event **is not terminal** — that is the whole of the crash-window rule, and nothing may synthesise an
instant for such a room from what did land. Concretely: the retention sweep does not prune it
(`RoomRetentionSweep.PruneRoomAsync`), and `fleet_status` omits `terminalAt` rather than substituting
a file's mtime. A terminal event that was only half-written is the same case and needs no rule of its
own — `FlowEventLogWriter` writes each entry as one complete newline-terminated line and
`FlowEventLogReader` returns only `\n`-terminated ones, so a torn final line is not yet observable
and the journal simply reads as one event shorter.

`terminalAt` is **absent, never fabricated**, in four cases, which a reader must not collapse — the
first three are `TerminalInstantAbsence`'s own members, carried on the resolver's answer rather than
left to a caller to infer:

1. `NotTerminal` — the run has not ended, including the crash window above.
2. `NoTransitionEntry` — the run is terminal but no line made it so. A zero-step workflow projects
   terminal off its empty journal, so this is a real shape and not a defensive branch.
3. `TransitionEntryUnstamped` — the transition line predates writer stamping (#745). **The only one
   of the four that says anything about a room's journal being old**, which is why it is the only one
   an operator is told about by name.
4. The pre-ledger sentinel — `TerminalSentinelWriter.WriteValidationRefusedAsync` writes a
   `terminal.json` for a refusal that happened before `flow.jsonl` existed, so there is no journal to
   resolve an instant from at all. Not a `TerminalInstantAbsence` member because the resolver is never
   reached on that path.

The two consumers answer the terminal-but-no-instant cases differently, on purpose. The retention
sweep has a destructive decision to make, so it falls back to `flow.jsonl`'s mtime and names which
cause it is falling back for, once per room per daemon process. `fleet_status` only displays, so it
omits the field — including for a `terminal.json` frozen before this field existed, which
`TerminalSentinelWriter` never re-derives (#1522 review finding 4).

**One durability consequence worth stating.** `Program.cs` writes the sentinel from a
`WorkflowStatusView` projected against a fresh full journal read, and a sentinel is never re-derived
once written — so whatever `terminalAt` that projection produced is what the room carries forever. It
rests on a `ProjectionCheckpoint` being a faithful prefix fold of an append-only journal, which is a
property this engine already depends on everywhere; #1157 is the first time it decides a *durable*
field rather than a recomputed one.

Two nearby timestamps are deliberately **not** this one. `baton status`'s own `Log updated at` is
`flow.jsonl`'s mtime at the whole-log grain — "when the last event landed", which is a different
question and its own honest answer (`StatusCommand.ResolveLogUpdatedAt`). And `baton rooms prune
--older-than` still ages a room by `terminal.json`'s mtime (`RoomsPruneCommand`), a proxy against a
different file with a different weakness (a copy or restore moves it); #1157 scoped itself to the
`flow.jsonl`-mtime chain and left that one standing.

**`workspaceChanged`/`hollow`/`hollowReason` (#1622 (b), the engine-side half of #1390).** A worker
whose contract is "change the tree" can exit 0 with a satisfied — often zero-output — contract and
settle `Succeeded` having produced nothing durable: #1390's own measured cases are a permission-blocked
worker whose declared report was trivially satisfied, and a lane that stopped one commit short (room
`dispatch-implement-24995b88`, #1500). `workspaceChanged` is present **only** for a role whose CATALOG
grant is both `write_files` and `run_shell_commands` — `implement`/`janitor` in the shipped
`WorkerRoles.json` today (`RoleDispatch.ToBinding` derives the bit once, from the role's own grant,
never re-derived downstream — see `Baton.Vendors.WorkerBindingConfigEntry.ChangesTree`'s own remarks for
why a downstream re-derivation from a resolved binding's possibly-widened grant would misclassify a
read-only role). Absent for every other role (`review`, `patch`, `fact-check`, `advise`, `orchestrate`)
and for every non-`Succeeded` step — the field's mere absence is the signal, not a fabricated `false`.
`true`: the worktree carries commits over base or uncommitted changes; `false`: it measurably carries
neither; **absent when the probe could not measure** (F2, #1720 review) — a working directory that is
not a git checkout, a plain checkout whose branch has no `@{upstream}` to compare HEAD against, or any
git failure. Read through `Workspaces.WorktreeProvisioner.TryReadWorkspaceChanged`, a TRI-STATE reading
deliberately not the negation of the fail-closed `IsWorkspaceUntouched` the retry carve-out uses: that
helper's `false` means "could not measure OR is touched", so negating it fabricated `workspaceChanged:
true` on no evidence and pinned `hollow` false exactly where the probe is blind. Both fields are omitted
together in the unmeasurable case, per the same absence-is-the-signal rule above. It reads the same
git status/commit-count pair
`OutcomeClassifier`'s own dead-worker-on-an-untouched-workspace arm already reads elsewhere in this
document, not the narrower `WorktreeProvisioner.Audit` the grant-audit branch uses, which only reads
dirty-tree: #1390's second occurrence measured a workspace that was dirty with real, substantial changes
yet nothing ever left it — no commit, no push, no PR — so "untouched" has to mean commits-over-base too).
Fed the binding's real `WorkingDirectory` whenever `changesTree` is true, deliberately never gated on
whether Baton itself auto-provisioned isolation for the run — a tree-changing role's `write_files` grant
means `RoleDispatch.ToBinding` never auto-provisions one (see `WorkerBindingConfigEntry.ChangesTree`'s
own remarks), so gating this the way the retry-veto path's `worktreePath` is gated would leave
`workspaceChanged` unable to read anything but `true` for every real `implement`/`janitor` dispatch
(second-reader finding, #1622). `Classify`'s own `changesTreeWorkingDirectory` parameter is the
deliberately-separate, wider path this reads from.
`hollow` is present under the identical gate, `true` only when `workspaceChanged` reads `false` **and**
the contract declares zero `ProducedOutputs` — narrower than `workspaceChanged: false` alone, since every
shipped catalog role that is tree-changing declares at least one output (the catalog's own load-time
floor); `hollow: true` fires in practice only for a bespoke zero-output contract dispatched directly
against the engine, not a real `implement`/`janitor` lane, where `workspaceChanged: false` alone is the
primary signal a harness reads. **This does not reclassify the room's own `state`/`error` — a hollow
success still reads `state: "Succeeded"`.** Whether it should read `Failed` instead is the design call
#1390 itself leaves to the operator; this fix surfaces the evidence, it does not rule on it.
`baton status`'s human rendering appends `— hollow: <reason>` to an otherwise-plain `"Succeeded"` line
when `hollow` is true (`StatusCommand.FormatStepStatus`); `fleet_status`/`FleetStepStatusView` copies all
three fields verbatim off the same projection, never a second worktree probe, so the glass badge #1502
already tracks reads the identical value a `status --json` caller would.

where `ExecutionUsageView` is
```
{ "wallClockMs": number, "tokensIn"?: number, "tokensOut"?: number, "turns"?: number,
  "cacheReadTokens"?: number, "cacheCreationTokens"?: number, "thinkingTokens"?: number,
  "billedTokens"?: number, "liveBilledTokens"?: number, "billedUnderReadTokens"?: number,
  "billedReconciliationUnavailable"?: string, "peakBilledInWindow"?: number,
  "modelsObserved"?: string[],
  "verifyStepMs"?: number, "verifyResultsBytes"?: number }
```
(`src/Baton/Status/ExecutionUsageView.cs` declares the C# record; `WorkflowStatusView.cs` projects it). `wallClockMs` is
always present when the object is present at all — derived from recorded start/exit timestamps. The
three added by #1569 follow one vendor's own field split, not a Baton-invented one: `cacheReadTokens` is a
real field on both measured vendors' envelopes (claude: `cache_read_input_tokens`; agy:
`cache_read_tokens`); `cacheCreationTokens` is claude-only (`cache_creation_input_tokens`) — agy has
never been observed reporting one; and `thinkingTokens` (claude: nested
`usage.output_tokens_details.thinking_tokens`; agy: flat `thinking_tokens`) — each independently
absent when its vendor's line does not carry it, same doctrine as the original three.

**Source, corrected by #1706 — on claude these six now come from `modelUsage`, not the snake_case
top-level `usage` object the paragraph above names.** The key names above describe agy's shape and
claude's *fallback* shape (a terminal line carrying no `modelUsage`); claude's normal path reads the
camelCase `modelUsage` siblings instead, per §3's terminal-read ruling. This is a real change to the
existing fields' VALUES, not only to the three added below: on room `dispatch-implement-3dc5e21a`
`tokensIn` moves 236 → 421,821, `tokensOut` 76,050 → 113,293, `cacheReadTokens` 18,306,867 →
21,764,631, `cacheCreationTokens` 221,809 → 349,454, `thinkingTokens` 15,370 → 26,232 — and on a room
whose `modelUsage` equals its top-level `usage`, nothing moves at all. Anything reading these figures
reads the larger scope now, including `OutcomeClassifier`'s ZeroOutputsDespiteSubstantialWork tripwire
text (its `> 0` polarity is unchanged; the number it shows an operator is not).

**The three added by #1706 are the per-room reconciliation** — the durable answer to "how much of this
room's spend did the live budget actually see". `billedTokens` is the AUTHORITATIVE billed total,
`tokensIn + tokensOut + cacheCreationTokens` off the terminal line (whole-tree on claude, per that
issue's `modelUsage` change above). `liveBilledTokens` is what `TokenBudgetMonitor` — the real class,
replayed over the same captured stream rather than a second implementation of its Σ — saw while the
execution ran, i.e. the quantity a budget arrested on. `billedUnderReadTokens` is their difference.
The three appear together or not at all, and the difference is emitted even at zero, because a
measured zero under-read is a finding: it is what agy produces (measured over three real rooms —
`docs/vendor-capabilities.md`'s finding on that vendor's terminal usage line, not restated here), and
it is the control that makes claude's non-zero one meaningful. Derived on read, never a
ledger event — the same derive-over-record-twice preference `ExecutionUsageProjector` was built on.

**A fourth field carries WHY they are absent, and the all-or-nothing rule is enforced rather than
merely stated (#1706 review).** Where the triple cannot be completed, the view carries
`billedReconciliationUnavailable` and withholds all three rather than serving whichever half it has.
An earlier revision of this contract was prose only, and the code did serve that half — so a consumer
obeying the register was handed a partial answer in precisely the case the guard exists to flag, which
is why the rule is now enforced in the projector and pinned in both polarities by
`ExecutionUsageProjectorTests`. The permitted reason values, and when the reason itself is absent, are
on `ExecutionUsageView.BilledReconciliationUnavailable`, which since #1883 declares them as constants
the projector emits and the cost ledger maps — one vocabulary, not a copy of one.

**`modelsObserved` (#1883) discloses the attribution the six figures above already depended on** — which
models the whole-tree read summed over, rather than only the total. `WorkerUsage.ModelsObserved` defines
the field, including what its absence does and does not say; §7's cost ledger is the consumer whose
behaviour turns on it. `peakBilledInWindow` (#1709) was already on the record and missing from the shape
above; both are listed now.

**The two added by #1882 are not token figures at all, and are attributed to ONE execution.**
`verifyStepMs` and `verifyResultsBytes` are the wall clock of the room's pre-turn verify step (the
review-role feature below) and the size of the `verify-results.md` it wrote. They carry no tokens
because the step spends no model — that is its whole point — but it does spend time, and the reviewer
does spend a read on the file, so #1849's ledger row needs both visible rather than free. Present
together or not at all, and only on the room's first execution by start time **that also exited**:
the step ran once, before the first turn, so reporting it on a retried step's second execution too
would double it. The exit condition is not a second rule — this view is only ever built for an
execution that recorded an exit (`wallClockMs` is unconditional on it), so an execution still
running, arrested, or dead without an `ExecutionExited` event has no row to carry the figures, and
the next one that does is where they land rather than nowhere.
Derived on read from the step's own sidecar (`artifacts/verify-step.json`), never a ledger event —
the same derive-over-record-twice preference the rest of this view rests on; an absent, unreadable or
malformed sidecar reads as "no verify step ran", which is what is true for every room dispatched
without `--verify-cmd`.

One of them is worth a ruling rather than a field doc, because the bound behind it reads like a
guarantee and is not. **`ExecutionStreamLogger`'s 8 MiB-plus-one-rollover ceiling is a RETENTION bound,
never a completeness one.** Each roll overwrites the single `.stdout.log.1`, so a stream past ~16 MiB
has permanently discarded its earliest segments and any replay over what survives is not even a floor
of the real live Σ. The engine's response is fail-closed: the logger records the roll that destroys a
segment, and a reader that sees that record withholds the reconciliation instead of reporting a
partial replay as a measurement. Fail-closed only *forward* — a room that rolled twice before this
landed carries no such record and still reports a figure. Not reachable on today's corpus (largest
room measured ~9 MB, one rolled room in 127), which is why this is a bound to know about rather than a
live wrong number.

**A second, unrelated way the capture can stop being the whole stream, and the one place a promise had
to become a mechanism (#1876).** A chunk whose write fails — on Windows, most often a sharing conflict
with a reader, an AV hold, or a delete-pending handle — is now **held and retried with the next
successful write, in arrival order**, rather than skipped. It has to be: the pre-#1876 logger printed
"Continuing to retry on subsequent chunks" while retrying only the *sink*, so the failed chunk's bytes
left a silent interior hole in a file a reconciler later summed over, and a vendor's terminal usage
record is the last chunk of the stream — exactly the one with no successor to carry it. `MarkTerminal`
drains the queue before latching, because that is the last chance those bytes get.

The queue is **bounded** (`ExecutionStreamLogger.DefaultMaxPendingBytes` — that constant is the value,
not restated here) because the obstruction can also be permanent, and an unbounded retry queue
on a multi-hour lane ends the dispatch rather than degrading it. Past the bound, or still queued at
terminal, the bytes are surrendered — and *that* is what gets announced, with its own warning and its
own marker file, distinct from the rollover marker. So there are two announced gaps and two reason
strings, `stream-truncated-by-rollover` and `stream-truncated-by-write-failure`, kept apart because
their remedies are: a rollover gap is at the head of the retained window and is the expected cost of
the ceiling above, while a write-failure gap is at an unknown offset and means the host obstructed the
writer.

**A retry has to be idempotent on disk, and the announcement has to outlive its first attempt**
(#1879 review). A write can throw *after* persisting some of its bytes, so each attempt is rolled back
to the file's pre-append length before the chunk is retried; an append that cannot be rolled back is
surrendered as a declared loss rather than replayed on top of its own prefix, because a duplicated
record is a corruption no marker announces and a reader cannot see. Surrendering avoids the duplicate,
not the half-written prefix: the bytes that landed stay on disk and the next chunk is appended onto them,
so the reader sees one fused line, announced by the write-failure marker (for JSONL that costs the
following record too, which is why the marker, not the file, is the authority). The loss itself is
latched **in memory** and the marker is retried after every later successful append and again at
terminal — the marker file is created in the same directory whose writes just failed, so treating its
first refusal as final is how a real gap goes unannounced while later chunks land around it. When the
marker still cannot be written, that fact goes to stderr; the engine's guarantee is that it never stops
trying. An initialization failure — the logger that never opened, whose capture is therefore empty
rather than partial — declares the same loss for both streams.

**A declared loss is announced on TWO channels, and a reader takes the event first (#1885).** Retrying
the marker is not enough on its own: it is created in the very directory whose writes are failing, so a
host that refuses this logger every file create for the whole run leaves the loss unannounced there for
good, and a reader of those files alone then reports a reconciliation over a holed stream as complete.
So the same in-memory latch is also journalled, as `FlowEvent.StreamLogLossDeclared`, through the room's
own event ledger — a different writer, into the room directory rather than the obstructed execution
output directory, and already the projector's first input. The two channels announce **one fact and
carry the same reason string**, and neither replaces the other: the marker stays, and stays the only
channel available to a reader holding just an execution's output directory (a pruned or hand-copied
one), while the event is the only channel that survives the refusal above. `ExecutionUsageView` reads
the event first, and a journalled loss withholds the reconciliation triple exactly as the marker does —
a Σ replayed over a holed stream is a fabricated under-read whether or not the hole was announced on
disk. When both are present they must agree; a **disagreement is reported on stderr rather than
resolved in silence**, because two announcements of one fact that differ mean something upstream is
wrong that picking a winner would hide. **Both markers can be on disk at once** — a stream that rolled
twice *and* lost a chunk the retry buffer could not hold — and a reader that reports one reason must
therefore rank them: the **write-failure reason wins**, because its remedy is the host obstructing the
writer while a rollover gap is the expected cost of the retention ceiling above. That ranking is also
what keeps the agreement rule honest, since the journalled channel only ever carries the write-failure
literal: ranking rollover first made the one room where both channels are *right* report itself as a
disagreement (#1888). A rollover marker standing alone against a journalled write-failure loss — the
write-failure marker refused, the rollover one written — still warns, and correctly: those are two gaps
rather than two accounts of one, and the file channel is describing a different gap than the writer
declared. The event is emitted by `CoreDispatcher`, never by the stream
logger itself — the logger is a core-layer file writer and owns no journal; it reports the latch out in
primitives and the dispatcher is the only party that names a flow event. It is emitted once when the
loss is latched and again at terminal if the marker still has not landed, that second event carrying
`MarkerLanded: false` as the durable record that the file channel never carried this loss at all — and
`TerminalReannouncement: true` to say *which* event it is, because `MarkerLanded: false` does not
distinguish the two (the first event carries `false` too whenever the marker had not landed yet, and on
an initialization failure the pair is otherwise identical on the wire). A line written before #1888
carries no such field, and its absence reads as unknown rather than as the declaration.
Diagnostic-only in `StateProjector`: durable in the ledger, no `StepState`/`FlowState` consequence.
Only a `stdout` loss reaches `billedReconciliationUnavailable` — the projector's terminal read and its
replay both consume `.stdout.log` and nothing else — so a `stderr` loss is durable and correctly
consequence-free there.

**When the bytes are gone, the token counts are not — for an arrest.** A declared gap withholds the
*reconciliation*, never the *reading*: the per-dimension fields fall back to the usage the live monitor
observed in memory and journalled on `FlowEvent.ExecutionArrested`, which never went near the disk. That
fallback stops at the dimensions and never reaches `billedTokens` — a live Σ is a floor, and standing it
in for the authoritative terminal figure would fabricate the very under-read the triple exists to
expose. So the shape of a lost stream is: dimensions present, triple absent, reason set. Never
zero-filled, never silent.

**And the limit of that, which is the arrest** (#1879 review): `ExecutionArrested` is the only outcome
event carrying a `WorkerUsage` at all — `ExecutionSucceeded`/`ExecutionFailed` carry
`PeakBilledInWindow` and nothing else — so an execution that reached a normal terminal outcome and whose
capture was surrendered has no in-memory reading to fall back to, and its shape is dimensions **absent**,
triple absent, reason set. The engine omits rather than fabricates, which is the right failure, but a
consumer (#1849's ledger included) must read the fallback as "the arrest population keeps its
dimensions", not as "a declared gap always keeps them".

**A failure the buffer absorbed gets no reason string at all**, and that silence is the contract rather
than an omission: nothing was lost, so the triple is PRESENT, and this field's whole job is to explain
the triple's *absence*. Whether retries happened is an operator-facing fact carried by the warning on
stderr, not a property of the reconciliation. This is the lower-level attempt record #1849's ledger
consumes: the ledger is handed a stream that is either whole or self-declared incomplete, and is never
made responsible for detecting a hole in one.

**Scoping what this does and does not recover, because the room that prompted #1876 recovers nothing.**
In `codex-1870-patch-sol-high-20260904-01` the missing token dimensions were not lost to the write
failure at all: that room's stream genuinely rolled three times (its `.stdout.log.truncated` marker is
real), and separately it carried **no usage-bearing record to lose** — codex reports usage only on a
`turn.completed` event, and the arrest at tool step 41 preempted the turn, which is why the journalled
`ExecutionArrested.Usage` is all-null while `ToolStepCount` is 41 and `LastToolNames` is populated (the
same parser, over the same bytes, in memory as they arrived). Reconciliation already consumes the
vendor's events from memory as they arrive; that path was not the defect. **On codex, an arrested
attempt has no token figure to report** — recorded here rather than in `docs/vendor-doc-audit.md`
because that register has no codex section at all yet; opening one is worth its own change.

**Not all fields are addends — on claude, `thinkingTokens` is a breakdown of `tokensOut`, not a
sibling count; on agy, the containment relationship is unmeasured.** Measured (#1569): on claude,
`thinkingTokens` is reached by descending *into* `usage.output_tokens_details`, an object nested inside
`usage.output_tokens`, so it is structurally a detail of `tokensOut`; on agy, `thinking_tokens` is
reported flat alongside `input_tokens`, `output_tokens`, `cache_read_tokens`, and `total_tokens` (where
`input_tokens + output_tokens == total_tokens`), which cannot arithmetically discriminate whether
`thinking_tokens` is a subset of `output_tokens` or disjoint from it and excluded from `total_tokens`.
Do not assume containment across vendors.

**Summation rules per vendor.** For claude, `cacheReadTokens`/`cacheCreationTokens` are true siblings
of `tokensIn`/`tokensOut` (excluded from both, per measurement), while `thinkingTokens` is a breakdown
of `tokensOut` — so `tokensIn + tokensOut + cacheReadTokens + cacheCreationTokens` is the honest burn
sum, and adding `thinkingTokens` would double-count. For agy, `cacheReadTokens` is excluded from
`total_tokens` (and `input_tokens < cache_read_tokens` rules out inclusion in `tokensIn`); because
`thinkingTokens`'s relationship to `output_tokens` is unmeasured, the exact burn sum cannot be fixed
without an additional vendor measurement (a consumer computing a lower bound sums `tokensIn +
tokensOut + cacheReadTokens`).

**This is attribution, not a complete burn figure.** §7 below rules that lane-log accumulation —
which is what every field here is — is never the reset-time source of truth; the `/usage` poll is.
Separately, `tokensOut` (and now its cache/thinking siblings) is a top-level per-execution figure that
excludes any subagent the dispatched worker itself fans out to, measured at a 22% shortfall on a
single subagent (`ClaudeUsageParser`'s own doc comment,
`src/Baton/Status/StandardWorkerUsageParsers.cs`) and growing with the tree — a gap this schema cannot close
without a field nobody has asked for.

**Notation and a real divergence.** `usage`/`linkedFromUsage` are correctly optional-and-omitted —
write it `"field"?: Type`, not `Type | null` with a comment contradicting itself. But `linkedFrom`
is **not** uniformly optional: `WorkflowStatusView` emits it as JSON `null` when absent (no
`JsonIgnore` attribute, `WorkflowStatusView.cs`), while the `fleet_status` variant omits it
entirely (`JsonIgnoreCondition.WhenWritingNull`, `FleetStepStatusView`,
`src/Baton.Cli/Mcp/FleetStatusTool.cs`), and the fleet variant additionally carries a
`timestamp` field the terminal-sentinel shape does not have. `terminal.json` and `status --json` are
one contract; `fleet_status` is a third, related shape with its own null-handling — see §6's schema.

**`liveness`/`rejected` (#1375/#1377) round-trip through `fleet_status` too (#1462).** `FleetStatusTool`
builds `FleetStepStatusView`/`FleetRoomStatusView` by copying named fields off the same
`WorkflowStatusView`/`WorkflowStatusStepView` projection — never a second probe or a second
computation — so `FleetStepStatusView.Liveness` and `FleetRoomStatusView.Rejected`/`.ResolvedBy`
(#1622 (d) mirrored the latter across, so the narrower `rejected` never leaves a conductor-closed room
looking like an unattended crash) are the identical
values `status --json` would report for the same room (`FleetStatusTool.cs`; the terminal-sentinel
path copies `sentinel.Liveness`/`sentinel.Rejected`/`sentinel.ResolvedBy` since the sentinel already **is** a
`WorkflowStatusView`). A fleet_status caller can now tell a dead engine or a rejection apart from an
ordinary `Failed`/`Running` room without a second `status --json` call per room.
`liveness` is present on a step this same projection calls `"Running"`, and (#1513) a `"Failed"` step
still carrying a `RetryNotBefore` — the identical gate `StatusCommand.FormatStepStatus` uses before
probing (a `Paused` step's engine has legitimately exited; a step with no execution yet has nothing
to probe; a `Failed` step with no pending retry has no future engine action to question) — so its
mere presence in the JSON already answers "does liveness apply here" before a caller reads its value.
`fleet_status` invents no `reason` field beside `rejected`, on either of the two branches §3's
`rejected` entry enumerates; which step rejected, if that
matters, is `steps[].state == "Rejected"` — already a token distinct from `"Failed"`.

---

## §4 Workers and vendor adapters

Vendor-specific behavior is isolated inside `Baton.Vendors`; `Baton` understands only a single
canonical message protocol. Adapters live behind `IWorkerAdapter`, resolved via
`WorkerAdapterRegistry.Default` (`src/Baton.Vendors/WorkerAdapterRegistry.cs`) — the registry is the
authority on what is registered; this document deliberately does not count them. The two production
vendor adapters whose enforcement mechanics §9 measures are `ClaudeWorkerAdapter` and
`AgyWorkerAdapter`. Baton never reads, copies, forwards, or stores a vendor credential; it spawns the
vendor's own already-authenticated CLI. The `PreToolUse`/`agy-hook-check` enforcement below (§9) runs
as a fast, dependency-free stdin round trip, spawned directly by the vendor CLI on every tool call —
deliberately outside the workflow-execution pipeline, because `PreToolUse` blocks the model's own
turn until it returns.

What "vendor-neutral" guarantees, concretely: a harness author writing against `terminal.json`,
`fleet_status`, and the CLI verb table never needs vendor-specific branches — those seams are
adapter-internal. What it does not guarantee: enforcement mechanics, which genuinely diverge between
vendors and are stated exactly, not smoothed over, in §9.

---

## §5 Gates — exactly one, closed exactly one way

**There is exactly one gate type in this spec's surface: the workflow pause, `PausePoint`, closed
only by `baton decide`.** The harness answers it programmatically via `baton decide` (§2's `decide` row
for the verb shape). The decision vocabulary (`resume|reject|retry-with-revision|supersede`) is the
whole of it.

**The mid-lane permission-ask mechanism is deleted (#1417).** `PermissionGateTool.cs`
(the `aer_permission_ask` MCP tool, formerly `Baton.Mcp.Host` — wrote an `ask-<id>.json` file and blocked
up to 180s for an `answer-<id>.json` to appear, denying via a `revoked-<id>.json` on timeout) and
`PermissionReturnShape.cs` are gone, along with the daemon's `/api/rooms/permissions/answer` REST
answerer and its own
crash-reconciliation heal path (both previously in `DaemonHost.cs`, now `src/Baton.Cli/Daemon/DaemonHost.cs`
after #1458 folded the standalone Baton.Daemon project's `Program.cs` into it) — the two places that
ever wrote an `answer-<id>.json` file; `Baton.Cli` wrote none. Under this spec's harness-only surface,
that tool had no answerer left; keeping it would have meant a worker blocking on a rendezvous file no
code writes. **A lane is dispatched fully pre-cleared**: every capability a worker will need is
granted in `bindings.json` before `baton run`/`baton dispatch` is called (§9). There is no mid-lane ask.

**A worker that hits a capability it was not pre-cleared for is denied, fail-closed, by the
`PreToolUse`/`agy-hook-check` enforcement in §9** — the same mechanism that already exists for every
other denial, not a new one. The denial surfaces legibly: `FailureClassification.ToolDenied`
(`src/Baton/Domain/FailureClassification.cs`, one of the enum's four values — see §7 for the
other three) is the vocabulary a harness reads off the failed step in `terminal.json`. A harness that
sees `ToolDenied` re-dispatches — with a widened grant in a fresh `bindings.json`, or a narrowed task
that does not need the capability. That is the whole of the recovery path; there is no live channel to
answer the denial in place.

**The second ledger, honestly.** §2 already states this in full: `room.jsonl` carried the
mid-turn ask/answer/revoke triad this section retires, plus held-work/escalation/dormancy/orchestrator
machinery §7 retires for an unrelated reason (no resident orchestrator). `fleet_status` never reads
`room.jsonl` (`FleetStatusTool.cs`) — it only ever read `flow.jsonl`, the terminal sentinel,
and `snapshot.json`. So a room paused on a `PausePoint` shows up correctly in Fleet Glass (§6); a
room that — under the *prior* draft's design — was waiting on a mid-lane permission ask would not
have. That gap is now moot rather than fixed, because the mechanism it was a gap in no longer exists.

---

## §6 Fleet Glass — observability

This is the entire user-facing surface, unconditionally. `fleet_status`
(`src/Baton.Cli/Mcp/FleetStatusTool.cs`) is a read-only MCP tool that scans rooms across the fleet: it
leverages the terminal-sentinel fast path for terminal rooms and projects active rooms from bound
snapshots plus `flow.jsonl` when no sentinel exists yet (`FleetStatusTool.cs`). It reads
`BatonPaths.Rooms` plus any caller-supplied extra `roots` and does not itself depend on a running daemon
process — it opens files directly (`FleetStatusTool.cs`).

**Two-level drill-down, both levels of `fleet_status`'s MCP host, never a second application:** the
tool's per-room summary (level one, `fleet_status` itself) is what exists today. Level two — a room's
own `stdout` tail and `flow.jsonl` timeline, for debugging a specific lane — is now `room_detail`
(`src/Baton.Cli/Mcp/RoomDetailTool.cs`, #1427): a sibling tool in the same MCP host, gated by its own
`--room-detail-tool` flag in `Program.cs`, resolving a room by name or absolute path and returning a
bounded (64 KiB) tail of an execution's `.stdout.log` plus a bounded (500-entry tail) projection of
`flow.jsonl` (event type and writer-stamped timestamp per line, never the raw event payloads — both
halves are capped for the same reason `fleet_status`'s own output stays MCP-friendly). Which
execution's stdout: the most recently written one by default (a heuristic that can name the wrong
lane after a retry, since the newest write is not necessarily the one being debugged), or a
caller-pinned `execution` id to bypass the heuristic. Same direct-file-read posture as level one — no
daemon dependency, and a missing or still-running room, a held-open ledger, or a malformed line all
degrade to a partial view plus a `note`/`unreadable` marker, never a throw. This settles the prior
draft's open question: there is no separate diagnostic UI, dev or otherwise. Fleet Glass **is** the
diagnostic story, and its second level is scoped work against the same MCP tool surface, not a new
one. One deliberate exception exists on a different plane entirely: C-11 (§11) rules in a
daemon-served, tailnet-bound, read-only drill-down page for the payloads this ruling's plane cannot
carry — the reasoning lives in that entry, and this ruling continues to govern everything reachable
from a conversation.

The outbound push mailbox — the mechanism that would notify a harness of a state-change event without
polling — is **(new build)**. There is no `push`, `mailbox`, or outbound-webhook-shaped
component anywhere under `src/Baton.Cli/Mcp` or `src/Baton.Cli/Daemon` at HEAD — nothing broadcast-shaped
survives the daemon narrowing (`DaemonBroadcast` and `DoorbellMonitor` both died with it, #1417/#1420),
so the "unbuilt" ruling stands with no surviving near-miss to distinguish it from. Quota data (§7)
and gate-pending visibility both ride this mailbox once it exists; its transport (webhook,
log-append, something else) is unspecified here — that is design work for the build.

**Current reality, stated so this section cannot overclaim:** a transitional status page exists
today *outside this repo* — a pushed snapshot rendered remotely for the operator. #1413 tracks
folding its pipeline into `tools/`; it is the mailbox's display end and a prototype of the push
loop, not a product surface this spec endorses. "Never a second application" constrains what Baton
*builds and ships* — the MCP tool is the surface — and stays honest only while that page remains a
disposable prototype rather than a maintained app.

### §6 schema — `fleet_status`

Input:
```
{
  "roots"?: [string],             // extra directories containing rooms to scan
  "include_terminal"?: boolean    // default true
}
```
Output (#1391: wraps the previously-bare room array in an object, per the `vendors[]` schema below —
`FleetStatusResponse`'s own doc comment (`Baton.Cli.Mcp`) records which pusher.py functions already
anticipated this shape):
```
{
  "rooms": [ /* one entry per room, same order DiscoverRoomsAsync produces */
    {
  "name": string,
  "path": string,
  "project"?: string,             // §8 registry: the project root this room was dispatched for
  "state"?: string,               // WorkflowOutcome, PLUS #1513's "Stalled" -- see the paragraph below
  "steps"?: [
    { "id": string, "state": string, "execution"?: string, "linkedFrom"?: string,
      "timestamp"?: string, "usage"?: ExecutionUsageView, "linkedFromUsage"?: ExecutionUsageView,
      "liveness"?: string, "attempt"?: number, "maxAttempts"?: number, "failureKind"?: string,
      "retryEligible"?: boolean, "exhaustedUntil"?: string }
  ],
  "outputs"?: [string],
  "error"?: string,
  "try"?: string,
  "rejected"?: boolean,
  "resolvedBy"?: string,  // F10 (#1720 review): the room-level WorkflowStatusView.ResolvedBy, copied like `rejected`. The glass's only signal for a conductor `baton resolve --close`, which sets this WITHOUT setting `rejected` (§3)
  "role"?: string,        // bindings.json's own key for the Running step's worker
  "adapter"?: string,     // that role's WorkerBindingConfigEntry.Adapter
  "model"?: string,       // that role's WorkerBindingConfigEntry.Model
  "effort"?: string,      // that role's WorkerBindingConfigEntry.Effort
  "timeoutMs"?: number,   // that role's WorkerBindingConfigEntry.Timeout, in milliseconds
  "label"?: string,       // #1499: the room's --label, WorkerBindingConfigEntry.Label
  "workstream"?: string,  // #1619: the room's --workstream, WorkerBindingConfigEntry.Workstream
  "parentRoomPath"?: string,   // #1441/#1620: redispatch lineage -- the parent room this one was redispatched from
  "parentExecutionId"?: string, // #1441/#1620: the parent room's own execution id at redispatch time
  "continuedSessionId"?: string, // #1381: non-null exactly when parentRoomPath/parentExecutionId name a room `--continue` rehired rather than `baton redispatch` reran -- see §3's dispatch entry
  "terminalAt"?: string,        // #1157: the room-level WorkflowStatusView.TerminalAt, copied like `rejected`/`resolvedBy`. Present only for a terminal room whose journal (or sentinel) carries the instant -- §3's "The terminal instant" has the absence rules, including why a pre-#1157 terminal.json omits it rather than falling back to that file's mtime
  "delivery"?: { "pr": number, "state": string } // #734: the room's latest recorded delivery fact (§2, §7) -- "state" is one of Opened/ChecksGreen/ChecksRed/Merged/Closed. Absent until the poller has recorded a first fact, including for a room whose outputs resolved no PR number at all (no delivery reference, or a branch-only one). Read from `flow.jsonl` directly, even on the terminal-sentinel fast path -- delivery facts keep appending after a room's own workflow goes Terminal
    }
  ],
  "vendors"?: [ /* #1391: advisory per-vendor usage runway -- absent entirely (never an empty array)
                   until at least one vendor has ever been harvested */
    {
      "adapter": string,             // "claude" | "agy" -- Codex is explicitly out of scope (#1391's own "Decisions already made")
      "harvestedAt": string,         // ISO-8601 UTC instant of the harvest this entry reflects
      "caveat"?: string,             // the vendor's own machine-local disclaimer, verbatim -- absent when the harvest carried none (agy: never documented, so always absent)
      "windows": [
        {
          "name": string,            // vendor's own wording, e.g. "session", "week (Fable)", "Gemini Models · Weekly Limit" -- agy's own "Remaining" is stripped so the label cannot contradict the percent-USED number rendered beside it (#1869 review); "rawLine" keeps the vendor's wording verbatim
          "percentUsed"?: number,    // ALWAYS percent USED (agy's own "percent remaining" is converted before this field is populated) -- absent, never a guessed number, when unparsed
          "resetsAt"?: string,       // ISO-8601 UTC instant -- absent when the vendor's own line carried no reset clause, or claude's non-ISO "Jul 25, 12:09am (America/New_York)" format (minutes optional) failed to resolve; "rawLine" still carries the vendor's own text either way
          "rawLine": string,         // the vendor's own line, verbatim, for a reader that wants to show what parsing dropped
          "ratePctPerHour"?: number, // #1746: advisory burn, percentage points of this window consumed per hour, derived over the persisted sample ring (oldest to newest). ABSENT under two samples -- never 0, which would read as "idle" when the truth is "not yet known" -- and absent when the ring spans no time at all. Present as 0 when two or more samples show no movement the two-decimal rounding can see; the ring keeps at most twelve samples and none older than three hours before the newest, so a rate is never averaged across an idle gap the harvester's backoff created. **Always absent on a `source: derived` entry (#1904)**, and so is `minutesToExhaustion` beneath it: no ring is kept for such an entry at all, because a derived percentage is not the monotonic counter every rule above assumes — `CodexUsageSource`'s own doc comment owns why, and `VendorUsageBurn.Advance` is where the skip happens
          "minutesToExhaustion"?: number // #1746: (100 - percentUsed) / ratePctPerHour, in minutes, at that rate. Absent whenever the rate is absent or not positive (nothing is being consumed to run out) and whenever percentUsed itself is absent
        }
      ],
      "liveLanes": number            // count of currently-Running rooms bound to this adapter, computed fresh at read time -- not part of the harvested snapshot itself
    }
  ]
}
```
(`FleetStatusTool.cs`, `Baton.Cli.Mcp.VendorUsageProjectionReader`). Optional fields are omitted, never emitted `null`
(`JsonIgnoreCondition.WhenWritingNull` throughout `FleetRoomStatusView`/`FleetStepStatusView`);
`rejected` follows the same omit-when-uninformative convention via
`JsonIgnoreCondition.WhenWritingDefault`, so it is absent rather than emitted `false`. This is a
**third shape**, related to but not identical with `terminal.json`/`status --json` — see §3's note on
`linkedFrom` and `timestamp` for the concrete divergence; `liveness`/`rejected` themselves are
identical values across all three shapes (§3). `state` is the one field that is NOT: #1513 overrides
`fleet_status`'s own `FleetRoomStatusView.State` to `"Stalled"` under the condition in §3's #1513
paragraph above, a display word `terminal.json`/`status --json` never emit — a caller reading `state`
identically across all three shapes must special-case this one divergence, the same way it already
special-cases `linkedFrom`/`timestamp`.

**`vendors[]` (#1391, reporting slice — #1848 owns dispatch-time enforcement on top of it, #1849 owns
the durable accounting substrate both can read).** Harvested from each vendor CLI's own headless
`/usage` report by one `IVendorUsageSource` implementation per adapter (`Baton.Vendors` —
`ClaudeUsageSlashCommandSource`/`AgyUsageSlashCommandSource`; each type's own doc comment has the exact
invocation and why it bypasses the adapter's gated worker dispatch). `Baton.Cli.Daemon.
VendorUsageHarvester`/`VendorUsageHarvestScheduler` (beside `FleetProjectionWriter`) own the harvest
cadence and the operator-approved rules behind it (its own doc comment states them; issue #1391's
"Decisions already made" is the ruling). `BatonPaths.VendorUsageSnapshotFile`/
`VendorUsageProjectionReader.ReadAll` own the persisted-snapshot round trip both `FleetStatusTool` and
`FleetProjectionWriter` read through, pairing each snapshot with a `liveLanes` count computed fresh
from that call's own room scan — `vendors[]` is never itself a live vendor spawn. **Advisory only**:
nothing in this slice reads `vendors[]` to hold, warn, or reject a dispatch.

**`source: vendor|derived` (#1904).** Every entry declares whether its windows are the vendor's own
counter or Baton's own derivation, and no reader may infer it from the adapter tag. #1391 settled that
a source reads the vendor CLI's own report and is *"never a `QuotaLedgerStore`-derived estimate, never
an operator-declared ceiling"*; #1904 **narrows** that clause rather than deleting it — a derivation is
admitted only for a vendor with no plan counter Baton has measured, and only while it carries
`source: derived` all the way out to the glass. For a vendor whose own counter is readable, #1391
stands unchanged. `codex` is the one derived entry today (`CodexUsageSource`, aggregating the
`quota-ledger.jsonl` burn rows over a **rolling** 5h and 7d lookback — that type's own doc comment owns
the window rule, what the total misses, and why `percentUsed` is absent unless
`DaemonSettings.CodexPlanCeiling` declares one). It is interim: codex-cli *does* expose an app-server
`account/rateLimits/read` surface, whose payload shape is unrecorded —
`docs/vendor-codex-probe-2026-09-04.md`'s known-unknowns is the register for that, and for what
retires the derivation. Being on `RunwayGate.MeasuredVendors` is what gives codex a snapshot file and
a glass block; it is **not** what gates it, and codex is deliberately absent from
`RunwayGate`'s window-name table (that list's own doc comment states why). A derived entry also never
carries the two burn fields — the `windows[]` table above says so on `ratePctPerHour`, and that is the
fourth thing `derived` costs, alongside the rolling boundary, the lower bound, and the absent percentage.

**Burn rate and minutes-to-exhaustion (#1746).** A single harvest carries no rate, so the persisted
snapshot file keeps a bounded ring of the last `VendorUsageBurn.RingCapacity` readings per window
(`Baton.Cli.Mcp.PersistedVendorUsage` — the file gained the ring flat beside #1869's four snapshot
fields, so a pre-#1746 file still reads, with no history). `VendorUsageBurn.Advance` owns the ring
rules — including what a rolled-over window does to the ring, which is why no rate ever spans a reset —
and `VendorUsageBurn.Derive` owns the two fields' arithmetic and the absence rules the `windows[]`
table above states. Both fields are **derived at read time, in the projection** — `pusher.py` forwards them
untouched and `glass.html` renders them without recomputing anything, so there is exactly one
implementation of the arithmetic. Still advisory: a straight-line extrapolation of the last few
harvests is not a prediction, and #1848 remains the only thing that may ever gate on any of this.

**`role`/`adapter`/`model`/`effort`/`timeoutMs` (#1503, extended by #1584 and #1613 item 3)** are read from the
room's own `bindings.json` (`WorkerBindingConfigWriter`/`WorkerBindingConfigParser`,
`Baton.Vendors`). On the active-room path, scoped to whichever step this same projection currently
calls `"Running"` — never a separate probe, and never one entry per worker role the room happens to
define; `adapter`/`model` prefer the running step's recorded `ExecutionRequest.Adapter`/`.Model` values
(#1584, matching `ExecutionUsageProjector` since #1567), falling back to `bindings.json` only when no
execution has recorded them yet (pre-#1567 journals or non-process dispatches). On the **terminal-sentinel fast path** (#1613 item 3 — pre-#1613 this fast path never read
`bindings.json` for these five fields at all, so they silently vanished the moment a room went
terminal, even though the same `bindings.json` a live room reads from is still sitting right next to
`terminal.json`), the resolution is different because there is no "Running" step left to key off:
`TryResolveSoleBinding` (`FleetStatusTool.cs`) reads them only when `bindings.json` names **exactly
one** role. This is a **stated coverage limit, not an impossibility** (corrected 2026-09-01 by
review of #1613's PR): the real answer for a multi-role room lives in `flow.jsonl` — the last
`ExecutionRequestAccepted`'s `Request.Worker`, exactly what the active-room path above already
reads. The terminal-sentinel fast path exists specifically to **avoid opening the ledger at all**,
and resolving a multi-role room's binding would require doing exactly that; `Dictionary` enumeration
order also is not a contract, so even a `bindings.json`-only guess among several roles would be
arbitrary. The trade is real and worth keeping — a multi-role terminal room omits the five fields
rather than pay the ledger-read cost the fast path exists to avoid — the same fail-open-to-absent
posture the rest of this paragraph already establishes, now named as a cost rather than described
as answerless. Both
paths funnel their resolved `(role, entry)` pair through one shared projection
(`ProjectBindingFields`), so the wire shape of the five fields is identical regardless of which path
resolved them.

All five are absent together whenever no step is Running and no sole terminal binding resolves
(pending, paused between steps, or a terminal room with zero or multiple bindings.json roles),
whenever `bindings.json` is missing (a room predating bindings files) or fails to parse, or — on the
active-room path only — whenever a valid bindings file simply lacks the Running step's worker role
as a key (where `resume` treats that as a hard error, this display path degrades): fail-open for
display metadata, so one unreadable bindings file degrades this row, never the whole `fleet_status`
call. `timeoutMs` is deliberately the raw configured timeout, not a countdown — a "remaining" figure
would already be stale by the time a caller reads it. A renderer wanting remaining time pairs it
with the same Running step's own `steps[].timestamp` above, which this shape already emits;
`timeoutMs` is not duplicated there (the terminal path has no live "remaining" concept to pair it
with at all).

**`label` (#1499) is read from the same `bindings.json`, but deliberately NOT gated the way the
quartet above is.** A room's `--label` is a room-level fact stamped onto every entry at dispatch time
(`DispatchCommand.ExecuteAsync`), not scoped to one worker's Running step — so `FleetStatusTool`
reads it off the first entry whose Label is non-null regardless of whether any step is Running, on **both**
`ProcessRoomAsync` paths, including the terminal-sentinel fast path that never reads `bindings.json`
for `role`/`adapter`/`model`/`effort`/`timeoutMs` at all. Absent when never supplied, when
`bindings.json` is missing or fails to parse, or on a pre-#1499 room whose `bindings.json` predates
this field — the same fail-open-for-display-metadata convention the quartet above uses. `redispatch`
carries a room's label into its child unless overridden (§2), so a lineage of redispatches keeps
reading as the same human-named lane.

**`workstream` (#1619) is read from the same `bindings.json`, on the identical shape and gating as
`label` immediately above** — a room-level fact stamped onto every entry at dispatch time, read off
the first entry whose `Workstream` is non-null on both `ProcessRoomAsync` paths, absent under the same
conditions `label` is absent under. `redispatch` carries a room's workstream into its child unless
overridden (§2), so a lineage of redispatches keeps grouping as one workstream. Pre-#1678, Fleet
Glass (`tools/fleet-glass/glass.html`, `groupLanesHtml`) grouped each state bucket's rendered lanes
by this field with a group heading spanning the lane grid; the #1678 board redesign (below) replaced
that section-per-state layout with compact board cards that have no room for a group heading, so a
card with a workstream instead carries it as a small line under the title (`boardCardHtml`'s own
`wsLine`) — the field is still surfaced, just not still grouped. Rooms with no workstream render with
no such line, the same fail-open-to-absent contract `label`'s own absence already has.

**`parentRoomPath`/`parentExecutionId` (#1441/#1620)** are read back off `.baton/room.json`'s
`ParentRoomDirectoryPath`/`ParentExecutionId` fields — the redispatch lineage `RedispatchCommand.cs`
already writes at redispatch time (`InteractiveSessionMaterializer.WriteWorkflowRoomMarkerAsync`) and,
until #1620, nothing read back. `InteractiveSessionMaterializer.ReadLineageAsync` is the read side and
the canonical account of its own file-open strategy (own doc comment) and fail-open rules; `FleetStatusTool`'s
`TryReadLineageAsync` wraps it with one more fail-open layer for an I/O fault at that call site, the
same posture `TryLoadBindingsAsync` already applies to `bindings.json` — one unreadable or corrupt
marker degrades this room's lineage fields, never the whole `fleet_status` call. Both absent together
for an ordinary `baton dispatch` room, which writes no lineage marker at all. Read on **both**
`ProcessRoomAsync` paths, the same reasoning that puts `label` on both paths above (own remarks).
Fleet Glass renders these as a "supersedes"/"superseded by" chain on the room's detail
pane (`tools/fleet-glass/glass.html`, `roomDetailHtml`/`lineageLineHtml`) — the #1678 board redesign
replaced the old per-state lane grid this feature originally targeted, so the chain reads off
`detailPaneHtml`'s own `roomsByPath` map (a straight lookup for "supersedes", a reverse scan for
"superseded by") rather than the state-bucket grouping `workstream` above used to have. Independent
of `workstream`'s own grouping — a chain renders on any room's detail pane whether or not that room
carries a workstream.

**`attempt`/`maxAttempts`/`failureKind`/`retryEligible` (#1509/#1510/#1522)** are copied verbatim from
`WorkflowStatusStepView`, never re-derived here — see that record's own remarks for the gating
rules (`src/Baton/Status/WorkflowStatusView.cs`). Same presence-gated, never-fabricated convention
as `role`/`adapter`/`model`/`effort`/`timeoutMs` above: a step with no execution history omits
`attempt`/`maxAttempts` entirely, and a step that hasn't failed omits `failureKind`/`retryEligible`.
The two failure fields are gated independently of each other, not as a pair: `retryEligible` (the
scheduler's verdict) can be present while `failureKind` is absent, for a Failed step whose worker
hasn't reported a classification yet.

**`exhaustedUntil` (#1551)** is the same `StepState.RetryNotBefore` `FormatVendorQuotaParkNotice`
prints at dispatch time ("the run resumes automatically at 21:59") and `StatusCommand.FormatParkedStatus`
renders on the human `baton status` path — copied verbatim, never re-derived, ISO-8601 UTC. Gated
narrower than `attempt`/`failureKind` above: present only for a `"Failed"` step whose `failureKind`
is exactly `"ExhaustedUntil"` **and** whose reset instant was actually recorded — an un-obligated
park (`RetryNotBefore` null, the human path's "reset unknown") stays absent rather than fabricate
one, and an ordinary `Retryable` backoff never emits this field despite scheduling a
`RetryNotBefore` of its own. Nothing re-derives or clears the value once (#1513) liveness confirms
the scheduling engine dead — a Stalled room keeps reporting the exact same, now-past instant; the
glass chip (`tools/fleet-glass/glass.html`) is what renders that honestly (a relative "was due 3d
ago — no scheduler" rather than a live countdown), never this field. A far-future or already-past
reset instant (#1183, fixed) never reaches this field wholesale: `MutationInterface.GetRetryObligations`
caps an instant more than `MaxExhaustionParkHorizon` (14 days) out to that horizon, and paces an
instant less than `PastResetInstantRetryFloor` (1 second) away — already past, or legitimately
future but imminent — up to that floor, before the obligation is ever recorded as a `RetryNotBefore`
— the crash-on-dispatch bug this closes was `Task.Delay` throwing past its ~49.7-day ceiling on the
raw instant. `exhaustedUntil` is still copied verbatim from
`RetryNotBefore` per the paragraph above, but for a degenerate vendor instant `RetryNotBefore` itself
is now this engine-computed cap or floor, not the raw value the vendor reported — "copied verbatim,
never re-derived" describes this projection step, not a guarantee that `RetryNotBefore` always equals
the vendor's own instant. In practice two vendor paths record an obligation to gate on: the agy
duration-parse path (`Resets in …` → `AgyWorkerAdapter`) sets `RetryNotBefore` on an `ExhaustedUntil`
park, and so does claude's `rate_limit` assistant-line envelope
(`ClaudeWorkerAdapter.TryClassifyQuotaExhaustion`, #1609) — claude's typed `credits_required` park
still records none. **Corrected (#1609), narrowing #1631's sentence:** the claim that the CLI carries
no typed reset instant on a quota-park line was itself too broad. A zero-spend read of the installed
CLI bundle's minified strings (2.1.258, 2026-09-03, no quota burned) found that a 429 makes the CLI
emit a synthetic `assistant` stream-json line with `error == "rate_limit"` and a sibling `quotaLimits`
object carrying `resetsAt` (epoch seconds) and, for the overage window, `overageResetsAt` — a real
typed instant, not prose. The parser now reads `quotaLimits.resetsAt`, falling back to
`overageResetsAt`, then to the envelope's human-readable "… · resets 3am" content-text suffix, before
giving up. Two things this still does **not** settle: exactly where `quotaLimits` sits in the envelope
(`TryClassifyRateLimitEnvelope`'s doc comment in `ClaudeWorkerAdapter.cs` records the open placement
question and why both are checked), and whether any of this matches a real capture at all — every
fixture this adapter is tested against remains bundle-derived (`claude-rate-limit.bundle-derived.jsonl`),
and #1115's own record still calls a real cap hit "unprovokable without a real cap" (an operator spend
decision, `CLAUDE.md` "Cost and reversibility are the operator's call", not a default action). What
#1631 got right and #1609 leaves unchanged: `claude -p "/usage"`/`/cost` still reliably report real,
headless reset instants for the session and weekly windows (decision 0026,
`docs/vendor-capabilities.md`) — but neither the typed `credits_required` shape nor the CLI's own
interactive 5-hour-window limit message has ever been captured, live, carrying a reset field, so a
`credits_required` park still surfaces the `"ExhaustedUntil"` `failureKind` with `exhaustedUntil`
absent and the chip showing no time — #1115 still forbids fabricating an instant nobody has actually
observed, live or bundle-derived. **Runway is advisory (#1391).** The `exhaustedUntil` fields above are
a *post-hoc* record of a wall this step already hit; the fleet projection's `vendors[]` block (this
section's own schema, below) is the *before-the-wall* signal — the same headless `/usage`/`/cost`
report, harvested by the daemon on a slow cadence and shown fleet-wide rather than per-step. Neither
gates dispatch in this slice (#1848 owns dispatch-time enforcement on top of it); both read from the
vendor CLI's own report, never an operator-declared ceiling.

**Live capture closed one more gap (#1857).** The weekly-limit wall does not arrive on the synthetic
`assistant`-line envelope above at all — it lands on the terminal stream-json `result` event, with no
`error`/`quotaLimits` fields, only `is_error: true`, `api_error_status: 429`, and the human reset text
in the plain string `result` field (`resets Sep 7, 6am (America/New_York)`, a date-prefixed suffix
distinct from the bare-clock-time one above and resolved against the named IANA zone); the parser
now recognizes this shape too and still classifies `ExhaustedUntil` with a real reset instant.

**Declared vendor-exhaustion fallback (#802, shipped).** A `WorkerBindingConfigEntry` may declare
`FallbackOnExhaustion: {Adapter, Model?, Effort?}` — that field's own doc comment has the scope
ruling and the resolution/ceiling guarantee, not restated here. When a step's latest attempt
classifies `ExhaustedUntil` and its role declares a not-yet-tried fallback (`WorkerBindingResolver.
ResolveFallbacks`'s output), `MutationInterface.GetRetryObligations` paces the retry to now instead of
the vendor's own reset instant (known or not — redispatching elsewhere needs no reset instant to pace
against), and the pump dispatches on that binding rather than the primary — one round, no wait. The
one room fact this records is `FlowEvent.StepRebound` (the same event #1583 introduced for
crash-recovery binding divergence, reused here for its stated §3.3 purpose), carrying the previous
adapter/model, the new adapter/model, and the reset instant it rescued the step from waiting out (in
`Reason`). The retry/attempt counters (`ConsecutiveFailureCount`, `ExecutionCount`) need no
special-casing: a fallback dispatch is an ordinary `ExecutionRequestAccepted`, so they already count
it as a fresh attempt of the same step. Chaining stops at one hop: once a step's `LatestExecutionId`
already ran on the fallback, a further exhaustion parks like any undeclared one — see
`ResolveVendorExhaustionFallback`'s own remarks for the exact check. With no declared fallback, the
step parks exactly as the two paragraphs above describe, and `baton status`'s human rendering
(`StatusCommand.FormatStepStatus`/`FormatParkedStatus`) now names the operator's own escape hatch
rather than only the clock. `FormatParkedStatus` picks between two verbs depending on the same
`EngineLivenessProbe` read `FormatStepStatus` already computes (#1838): a confirmed-`Alive` owning
engine has not written the terminal sentinel `baton redispatch` requires, so the rendering names
`RecoveryGuidance.CancelThenRedispatchAdapterInstruction` — `baton cancel <room-dir>`, then
`baton redispatch <room-dir> --adapter <vendor>`; a `Dead` or `Unknown` read (or no recorded engine
identity) names `RecoveryGuidance.RedispatchAdapterInstruction` — the already-shipped
`baton redispatch <room-dir> --adapter <vendor>` alone — rather than restating either string.

The scan itself is a **single-level** `Directory.GetDirectories` per root
(`FleetStatusTool.cs`) — it does not recurse, so project-grouped nesting is not found by the scan
alone. §8 depends on this fact directly, and closes it by unioning the scan with a registry rather
than by making the scan recurse.

### §6 schema — `room_detail`

Input:
```
{
  "room": string,                 // room name (resolved under BatonPaths.Rooms + roots) or an absolute path
  "roots"?: [string],             // extra directories to search when 'room' is a name
  "execution"?: string            // pin a specific execution id's stdout; default: most recently written
}
```
Output:
```
{
  "name": string,
  "path"?: string,
  "stdout"?: { "text": string, "truncated": boolean, "totalBytes": number, "source": string, "readError"?: string },
  "timeline"?: { "entries": [ { "type": string, "timestamp"?: string, "stepId"?: string, "exitCode"?: number, "detail"?: string } ],
                 "truncated": boolean, "totalEntries": number },
  "error"?: string,
  "note"?: string
}
```
(`RoomDetailTool.cs`). Optional fields are omitted, never emitted `null`, the same convention as
`fleet_status`'s shapes. `stdout` is absent (not an error) for a room with no captured output yet;
`timeline` is absent for a room with no `flow.jsonl` yet (pre-ledger). A held-open ledger or a
malformed line surfaces as a single `timeline.entries` item with `"type": "unreadable"` and a
`detail` message, rather than failing the call. `error` is set only when `room` itself does not
resolve to a directory.

**`stepId`/`exitCode` (#1613 item 4) are ids/counts, populated only where the underlying event
carries one DIRECTLY** — `DescribeEntry`'s `FlowEventStepId` reads `ExecutionRequestAccepted`'s
`Request.StepId`, `WorkflowPaused`/`StepRetryScheduled`/`ExternalDecisionRecorded`'s own `StepId`
fields, and `RuntimePermissionAsked`'s `StepId`; `exitCode` reads only
`CoreEvent.ExecutionExited.ExitCode`. Deliberately NOT a cross-referenced lookup through an
execution-id → step-id map built from an earlier `ExecutionRequestAccepted` line (the way
`ExecutionUsageProjector` resolves a worker name for usage attribution) — that would need a first
pass over every entry before this per-entry describe step runs; this stays narrow and on-the-record
only. An entry whose event carries neither omits both fields, same never-fabricated convention as
every other optional field in this shape.

**The operator's 2026-09-01 ruling on content (issue #1613), which governs both fields above and
the mailbox additions below:** the fleet_status/room_detail surface's original content-free
construction (§6 above, `extract_timeline`'s own doc comment) is amended to **COUNTS AND IDS, NEVER
CONTENT** — step ids, exit codes, event detail counts, and live token/tool-call counts are in;
stdout text, prompts, and any other worker-output-derived string stay banned. The secret-gate
boundary this amends nothing about: `tools/fleet-glass/pusher.py`'s `extract_timeline` still reads
exactly the fields it enumerates as KEPT (now `type`/`timestamp`/`stepId`/`exitCode`) off each
`room_detail` entry and nothing else — a future `room_detail` field still never leaks through by
accident of that function failing to name it.

**The pushed mailbox payload carries three fields `fleet_status`/`room_detail` do not (#1613 items 1
and 2, plus #1155's `rooms[].pruned` below) — pusher-computed, not part of either MCP tool's own C#
output above.** The first two are read
directly off the room's already-captured `.stdout.log` or wall-clock, python-side
(`tools/fleet-glass/pusher.py`), because `Status.ExecutionUsageProjector`'s engine-side seam only
ever populates an execution that has recorded BOTH a `CoreEvent.ExecutionStarted` AND
`ExecutionExited`, and its parser contract (`IWorkerUsageParser.TryParseFinalUsage`) reads exactly
the last non-blank line of the captured stream — neither fits a still-running execution, which by
definition has no exit event yet and needs every line scanned, not just the last:
- **`rooms[].live` (item 1, extended by a 2026-09-01 review of #1613's PR, and by #1682)**, present
  only for a room whose pusher-displayed `state` is exactly `"Running"`:
  `{ "toolCalls"?: number, "billedTokens"?: number, "billedIsFloor"?: true, "turns"?: number,
    "contextTokens"?: number, "cacheReadTokens"?: number, "lastActivityAt"?: string }`.

  `toolCalls` counts `tool_use` blocks in claude's `assistant` stream events, DONE/`tool`
  `step_update` heartbeats in agy's, and (#1886) `item.started` events carrying a tool item type in
  codex's — claude's and agy's shapes measured (docs/vendor-capabilities.md's `#1559`/`#1088` rows,
  `tests/Baton.Cli.Tests/RunCommandEchoTests.cs`, `AgyWorkerAdapter.TryParseProgressEvent`'s own doc
  comment). **Scope of the codex measurement, stated because the set is wider than the evidence:**
  `Baton.Status.CodexUsageParser.TryParseToolName` is the canonical home of WHICH item types count;
  the default `file` source reads it directly, while `pusher.py`'s stale-fallback derivation
  deliberately RESTATES the set (and the `turn.completed` usage arithmetic) in Python, because that
  reader cannot call into the engine and the fallback still runs whenever the daemon's file is stale
  or absent (#1557 PR-B2's `derive_snapshot_and_timelines` removal condition) — so dropping the copy
  in favour of absent-not-zero would take codex's live block off the glass on every such cycle,
  which is #1886's own symptom; the copy is pinned to the parser by the shared fixture below and is
  deleted with the rest of the derive block by PR-C. The real capture both are pinned against
  (`tests/Baton.Cli.Tests/Fixtures/codex-live-stream.jsonl`) carries only `mcp_tool_call`
  items, so the other members of that set are inherited from the parser, not measured by #1886's own
  evidence. This is three different things under one field name, disclosed rather than left to be
  inferred: claude counts tool *requests*, agy counts DONE tool *steps*, codex counts *started*
  items (its `item.completed` twin is deliberately not counted — every started item also completes,
  and counting both doubles). Both claude's and agy's are whole-tree, including subagent turns —
  claude's `assistant` events for a subagent carry `parent_tool_use_id` but are never filtered out,
  deliberately (the mirror image of `billedTokens`'s own subagent completeness below).

  **#1886, the absent-not-zero rule made structural on the derive side.** Every field here has always
  been "absent, never a substituted zero" (restated at the end of this entry), but `toolCalls` alone
  was exempt in practice: `pusher.py`'s `extract_live_counts` returned it unconditionally and
  `live_telemetry_for_room` pre-seeded its running count at 0, so a stream envelope that reader did
  not know rendered on the glass as a measured `0 calls`. Both halves are now gated on the batch
  having matched a KNOWN envelope — a zero from that reader means "a stream it understands, which has
  so far called no tool", and a shape it does not know reports nothing at all. The daemon's own
  projection has the same property by construction (no parser resolved for the room's adapter tag
  means no count fields at all, `FleetProjectionWriter.GetOrCreateLiveState`).

  **The two sides are NOT symmetric about when the count first appears, and a reader will otherwise
  assume they are.** The daemon keys on the room's ADAPTER TAG, so a resolved parser emits
  `toolCalls: 0` on the very first tick, before a line has been read; the derive path keys on
  per-batch ENVELOPE EVIDENCE, so it emits nothing until some line actually matches. For a Running
  room whose `.stdout.log` exists but has yielded no complete line yet (empty, or the first line
  still mid-flush) the daemon therefore says `0` where the derive path says nothing — a seconds-wide
  window that closes on the first recognized line. `--compare-projection` treats a
  present-on-one-side field as a hard difference with no tolerance, so it can flake red inside that
  window; that is the honest reading of two readers with different evidence, not a defect to
  suppress with an exclusion.

  **Live tokens, both vendors (#1682).** The original ruling — "token counts are deliberately never
  emitted… an absent field is honest, a summed one would re-count each turn's whole context" — was
  right about the trap and wrong about the conclusion: it correctly noted neither
  `docs/vendor-doc-audit.md` nor `python tools/vendor-verify/verify.py --list` records a
  per-assistant-message (mid-stream) usage figure, but treated that silence as a verdict rather than
  an open question still worth checking. A live capture on 2026-09-01 settled claude's own shape; a
  second live capture during #1682's own evidence gathering (2026-09-02, `dispatch-implement-38c24d11`'s
  real `.stdout.log`) found the SAME thing true of agy — a prior version of this section's claim that
  "agy emits none of the three: its `step_update` heartbeat carries no `usage` field at all" was
  wrong (it checked the `tool` step_type's heartbeat, not the `agent_response` one, which does carry
  `usage`) and is corrected here rather than left standing. `billedTokens` is the SAME quantity the
  engine's own `Baton.Mutation.TokenBudgetMonitor` arrests on (§3 below), read by the same per-vendor
  rule: `Σ(input_tokens + output_tokens)` per agy usage line, `Σ(cache_creation_input_tokens)` per
  claude one (#1706 — that vendor's other two columns are placeholders; §3 has the measurement).
  Additive, whole-tree on claude (a subagent's `assistant` events carry `parent_tool_use_id` and are
  never filtered out), never `thinking_tokens` (a breakdown already counted inside `output_tokens` on
  both vendors — measured against real #1682 captures: Σinput + Σoutput reproduces agy's own
  Σ`total_tokens` exactly). **`billedIsFloor` (#1706)** is `true`, and absent otherwise, once any
  batch contributed a claude figure: `billedTokens` is then a LOWER BOUND on real spend rather than a
  measurement of it, and the glass renders a trailing `+` on the number rather than showing a
  complete-looking figure. Sticky across batches — one incomplete batch makes the accumulated total
  incomplete — matching `TokenBudgetMonitor`'s own sticky flag. `turns` is the count of usage-bearing
  lines contributing to `billedTokens`, additive the same way. `contextTokens` (the sum of the message's
  fresh-input count and both its cache counters) and `cacheReadTokens` (the cache-read counter
  alone) stay claude-only — read off the LATEST `assistant` line only, a LEVEL, replaced every turn,
  never summed: the trap the original ruling correctly named applies to the fresh-input count
  specifically (summing it across turns re-counts each turn's whole repeated context), not to
  `billedTokens` or to a single turn's own level. agy's `step_update.usage` carries no
  cache-creation figure, so there is no comparable trio to build `contextTokens`/`cacheReadTokens`
  from on that vendor — a claude-only measurement stays a claude-only pair of fields. Every field
  here is absent, never a substituted zero, when a batch's lines don't carry what is needed.

  **Live tokens on codex (#1886).** `turn.completed.usage` — one usage object per completed turn, and
  `Baton.Status.CodexUsageParser` is the canonical statement of its arithmetic, including that this
  vendor reports `input_tokens` INCLUSIVE of `cached_input_tokens` (so the fresh-input component is
  the floored remainder, keeping Baton's additive dimensions disjoint). `billedTokens` is fresh input
  + `output_tokens` + `cache_write_input_tokens`, the same three components `TokenBudgetMonitor` sums
  off that parser's readings; `reasoning_output_tokens` is read by nothing, the same
  breakdown-already-inside-output exclusion the other two vendors get. This vendor reports its real
  input and output figures, so — unlike claude — `billedIsFloor` is absent and the number is a
  measurement. `contextTokens`/`cacheReadTokens` ARE available here (this pair is no longer
  claude-only): the level is fresh input + cached + cache write, with `cacheReadTokens` the cached
  component alone, replaced per turn and never summed. **`turns` is structurally sparse on this
  vendor and is not comparable to claude's**: codex emits one `turn.completed` per agent turn, so a
  single turn can sit behind hundreds of tool calls (the #1886 evidence room read 266 tool calls
  against 1 turn), where claude's per-message counting tracks tool calls closely. A codex lane
  reading `1 turn · 266 calls` beside a claude lane's `93 turns · 93 calls` is both readers working.

  `lastActivityAt` is the stdout log's own last-write instant (a real filesystem fact, not `now()`),
  quantized to a ~90s bucket before it enters the pushed payload (2026-09-01 review finding) — see
  `pusher.py`'s `LAST_ACTIVITY_BUCKET_SECONDS` for the write-budget reasoning this closes. Quantized,
  not excluded the way `derived_at` is excluded below: a prose-only turn with no tool call in it
  would leave every OTHER field in `live` unchanged too, so excluding this one as well would freeze
  glass's rendered age on an old instant while the lane is, in fact, still going.
- **`rooms[].pruned` (#1155)**, present only for a room whose `artifacts/pruned/` directory (`ArtifactPruner.PruneAsync`'s grace-window destination, #1027 Option B/#1041) is non-empty:
  `{ "count": number, "items": [{ "name": string, "bytes": number, "prunedAt": string }, capped at the 20 newest by `prunedAt`] }`.
  This is the observability half of the grace window's own design rationale (#1027 Option B: prune,
  don't delete, so an operator can still find what moved) — before this, nothing read `pruned/` at
  all, so a completed run's artifacts silently vanished from every surface the moment the retention
  sweep ran. `prunedAt` is the pruned directory's own filesystem mtime (`ArtifactPruner` leaves no
  manifest — `RetryingFileMove.MoveDirectory` is a bare rename), the same real-timestamp-not-`now()`
  convention `live.lastActivityAt` above already follows. Read-only: Fleet Glass shows what moved,
  it does not restore it.
- **`derived_at` (item 2)**, beside `heartbeat_at` (#1486) at the top level of the pushed snapshot:
  when this pusher process's OWN `derive_snapshot_and_timelines` call last completed successfully,
  regardless of whether that cycle's content changed enough to push. `pushed_at` (worker.js's own
  receipt time) is legitimately stale on a quiet-but-healthy fleet — the #1457 change-gate skips an
  unchanged snapshot on purpose — so Fleet Glass's "Snapshot derivation may be stuck" banner
  (`tools/fleet-glass/glass.html`) keys on `derived_at` instead: a fleet that stays quiet because
  nothing changed still reads healthy, while a derivation that has been raising every cycle for
  hours (the real failure mode this exists to catch — a hung `dotnet mcp` subprocess starves
  `derived_at` too, on the same timescale it starves `heartbeat_at`, since both live in the same
  loop iteration) still alarms. Reaches the mailbox by two routes that share one KV write budget
  rather than add to it: riding inside an actual snapshot push's own body (excluded from
  `snapshot_hash` so it can never itself force a push), or via a dedicated ping on the same
  `/heartbeat` endpoint whenever a push hasn't landed one recently (`should_send_derived_ping`) —
  see `pusher.py`'s own module docstring for the write-budget arithmetic this is built around. A
  missing `derived_at` (a pusher not yet redeployed for #1613) now gets its own explicit banner
  rather than silently falling through to a clean one (2026-09-01 review finding) — mirrors the
  sibling `heartbeat_at`-absent message.

  **`pending_push_age_s`, a second field this same review pass added to the SAME `/heartbeat` ping
  body `derived_at` rides:** the gap `derived_at` alone leaves open is `pusher.py`'s own comment
  above this field's definition — a healthy derivation loop sitting behind a POST that will not
  land, for any of the ordinary transport reasons a mailbox POST can fail. Dropping the pre-#1613
  `pushed_at` staleness check removed the only signal that used to catch that; this PR's own
  terminal-timeline addition also raised the odds of hitting it, by growing the typical payload
  toward the push route's own size cap. `pending_push_age_s` is seconds since the pusher's last
  SUCCESSFUL push, present only while `should_push_snapshot` says content is actually waiting to go
  out — absent on a healthy, nothing-changed fleet, so a legitimately quiet lane never false-fires.
  Fleet Glass alarms "Push failing" once it exceeds a threshold on the same order as the
  derivation-stuck check above, independent of whether any room is Running (a failing push is not
  scoped to active lanes the way the derivation-stuck check is).

  **`stdoutTail` (#1710, rendered as prose and blob-elided by #1723).** A bounded live tail of the
  Running execution's own `.stdout.log`: the last ~40 lines (`STDOUT_TAIL_MAX_LINES`), hard-capped at
  ~4 KB per room (`STDOUT_TAIL_MAX_BYTES`, `pusher.py`'s `stdout_tail_for_room`), read straight off
  disk the same bounded-tail-window way `live`'s other fields are — no engine change, no on-demand
  path, since the glass's `baton` MCP connector is the Cloudflare worker serving KV, not the fleet
  machine (the constraint that decides this design, #1710's own issue body). Over-cap content drops
  the OLDEST lines first, on a real line boundary — never mid-character (`stdout_tail_for_room`'s own
  docstring is the canonical record of the truncation direction and its leading `…` marker, not
  restated here). Each raw line is rendered before it ships: a stream-json object becomes one short
  prose line (`_render_stream_json_prose`, a Python sibling of `Baton.Cli`'s
  `WorkerStreamLineRenderer`/`RunCommand.EchoStreamJsonLine`) or is dropped as noise, a whitespace-free
  token of 200+ characters (base64, a data URI, a hex dump) is elided to a byte-count marker
  (`_elide_blob_tokens`), and a non-JSON line passes through unchanged — see those functions' own
  docstrings for the field-by-field rules, not restated here. Every surviving line then passes the
  SAME secret-gate patterns the deliverables path uses
  (`secret_hit_index`) — a matching line becomes `[withheld]`, never dropping the whole tail the way
  a deliverable's whole-content withholding does; `_gate_tail_lines`'s own docstring in `pusher.py`
  is the canonical record of the missing-patterns-file fallback, not restated here. Absent, never a
  fabricated empty string, on a terminal room (terminal rooms carry no `live` section at all — their
  report is the record) and on a Running room whose execution has no captured stdout yet.

  **Costs bytes and churn, never a write.** This rides the existing snapshot push the change-gate
  already gates (#1457), so it never adds a KV write of its own.
  Unlike `toolCalls`/`outputTokens`/`lastActivityAt`, `quantize_live_for_hash` does NOT quantize
  `stdoutTail` for hashing — a Running room's tail changing (new stdout since the last push) already
  flips the hash the same cycle its OTHER `live` fields would, since a Running room already changes
  the hash on essentially every push while it is actively producing output; quantizing the tail text
  itself would buy nothing the value-quantization above doesn't already buy for the fields that
  actually caused #1690's incident (a churn source with no real-content correlate). The write-budget
  ledger (below) counts writes, never bytes, so this field's only cost against it is the same
  snapshot-push accounting every other `live` field already pays into — `pusher.py --selftest`'s own
  arithmetic-gate arm proves the worst-day write total is identical whether or not any Running room
  carries a `stdoutTail`.

**The fleet projection file (#1557 PR-A) carries four more fields the mailbox payload above does
not — a local file has no KV-write-budget or payload-size ceiling to weigh against them, so this is
new surface, not a relocation of anything above.** `BatonPaths.FleetProjectionFile`
(`{Root}/fleet/projection.json`, §7) wraps the same per-room shape this section already describes —
`fleet_status`'s own fields, plus `live`/`pruned` gated exactly as above — under one more top-level
`derived_at` (the daemon's own wall-clock at successful write completion; unrelated to the
mailbox-payload `derived_at` above, which is the pusher's cycle time). Per room, alongside `live`,
whenever that room's steps carry a Running execution (present even when #1513 has downgraded the
room's own displayed `state` to `"Stalled"` — these three exist specifically to diagnose that case,
so gating them on `state == "Running"` the way `live` itself is gated would hide them from exactly
the room they are for):
- **`processAlive`**: `"alive"` / `"dead"` / `"unknown"`, from `EngineLivenessProbe.Probe` against the
  Running step's own recorded `FlowEvent.ExecutionRequestAccepted.EnginePid`/`.EngineStartTime` — the
  same probe and identity source `StatusCommand`/`MutationInterface` already read, never a second
  liveness mechanism.
- **`stdout_last_write_ago_sec`**: seconds since the Running execution's `.stdout.log` last-write
  time — a byproduct of the same incremental read `live.toolCalls`/`live.billedTokens` are computed
  from, not a second file open.
- **`elapsed`**: seconds since the Running step's own `timestamp` (already emitted, this schema
  above) — paired arithmetic this section's `timeoutMs` remarks already noted a renderer could do,
  now actually done. `timeoutMs` itself is not duplicated here; it already rides the base shape.

A fifth field the operator's own ask named — pending-outputs status — has no clean source:
`StepOutputResolver` resolves a Succeeded (or Paused-then-Succeeded) step's already-produced output
paths, not a verdict for a step that has not reached that state yet, so PR-A omits it rather than
inventing a projection nothing in `src/` computes today. `stdoutTail` was also absent from this file
in PR-A, deferred to PR-A2.

**PR-A2 (#1557) adds `live.stdoutTail` to this file**, gated identically to the mailbox-payload field
above. `Baton.Cli.Daemon.StdoutTailRenderer`'s own doc comment is the port record (which `pusher.py`
functions it mirrors), not restated here; `BatonPaths.SecretPatternsFile`'s own doc comment names where
its secret-gate denylist path resolves and why (#1816). This makes
`pusher.py`'s own stdout-tail rendering block (`_read_tail_text` through `stdout_tail_for_room`) dead
code once PR-B reads this file instead of deriving its own snapshot — PR-B removes it.

**#1793 adds `live.doingNow`**: one plain-words line (≤ 140 chars, no elision marker) naming what the
Running room's worker is doing right now, derived from the SAME stdout tail window — the last
`assistant` line's own last content block, either its trailing text (the model's own one-line intent)
or a trailing tool call's `description` input field (falling back to the tool name plus its first
argument) — `StdoutTailRenderer.ComputeDoingNow`'s doc comment is the canonical port record, mirrored
independently in `pusher.py`'s `doing_now_for_room` (kept in lock-step by a shared fixture,
`tests/fixtures/doing-now-sample.stdout.log`, rather than a literal shared implementation across the
C#/Python boundary). `doingNow` is gated by the SAME secret denylist as `stdoutTail` above, including
its withhold-everything fallback when the denylist itself fails to load — a hit, or a missing/
unreadable pattern file, withholds the derived line as `[withheld]` rather than skipping the gate
because the line is short. It is compared exactly on settled rooms by `--compare-projection`
(`_compare_volatile_live`), same tolerance shape as `stdoutTail` above. `glass.html` renders it above
the STDOUT block on a Running room's detail card; the timeline separately collapses consecutive
`executionProgress` heartbeat rows into one "alive · N heartbeats · <first> → <last>" row (any other
event breaks the run) — pure client-side rendering, no projection change.

**PR-B1 (#1557) gives `pusher.py` a second, opt-in source for the SAME body it has always derived
itself.** `FLEET_GLASS_PROJECTION_SOURCE=file` (env; **`file` is the default as of PR-B2 below** — PR-B1 shipped
it as opt-in, with `derive` as the default) switches `main()`'s loop to `json.load`
`BatonPaths.FleetProjectionFile` instead of spawning `dotnet mcp` and running its own
`attach_live_telemetry`/`attach_pruned_info`; `derive` keeps doing exactly what it always has. This
PR deletes nothing from the `derive` path — PR-C does that under its removal condition; PR-B2's
default switch is gated on `compare-projection` reading
green on ≥ 3 settled rooms plus every static field on every room (#1807; `compare_projection`'s own
`_MIN_SETTLED_ROOMS_FOR_GREEN` in `pusher.py`, so "green on 0" can't pass). Because no scheduled task runs `baton daemon` today, the fallback
below is load-bearing from day one, not a defensive edge case: a file older than 3 coalescing
windows (900s) or absent/unreadable/malformed falls back to `derive` for that one cycle and logs one
line; on that fallback cycle only, the pushed body carries one more optional top-level field,
`staleness: {daemon_derived_at, age_s, stale}` (absent on every ordinary push, same convention as
`pusher.writeBudgetExhaustedUntil`), and `glass.html` renders it as one more absent-safe banner,
checked at the same priority rung as the write-budget one, never a new banner mechanism.
`python pusher.py --compare-projection` runs BOTH sources once against the live rooms and diffs them
field-by-field after canonical (sorted-keys) JSON serialization, exiting 0 identical / 1 with the
diff printed on mismatch — `derived_at` (differs by construction), `rooms[].live.lastActivityAt`
(the two sources bucket the same underlying mtime at different instants), and
`processAlive`/`stdout_last_write_ago_sec`/`elapsed` (the `derive` path never emitted these at all)
are excluded from strict equality for the reasons named inline at each check; everything else must
match exactly.

**#1807: `rooms[].live` on a Running room needed a second exclusion tier, not more strict-equality
exceptions.** A live compare run on this machine's own overnight fleet (34+ Running rooms) reads RED
on `billedTokens`/`cacheReadTokens`/`contextTokens`/`toolCalls`/`turns`/`stdoutTail` on every one of
them, for the reason and design `_compare_volatile_live`'s own doc comment in `pusher.py` gives
(canonical — not restated here): `billedTokens`/`toolCalls`/`turns` tolerate forward motion only, in
whichever direction each side's own `derived_at` says is actually later (#1812 — never assumed from
call order); `contextTokens`/`cacheReadTokens`/`stdoutTail` tolerate a moving value only on a room
that is still Running, going back to exact comparison once `_room_is_settled` finds EVIDENCE a
room's counters can no longer move — a terminal `state` or the room's own `terminal.json`, never
quiet time alone, since a Running room mid-tool-call can go quiet far longer than one daemon write
interval without settling (#1814). (#1812 — the tolerance was masking a genuine `cacheReadTokens`
sum-vs-level derivation bug, `WorkerUsage.CacheReadLevelTokens` in `src/Baton/Domain/WorkerUsage.cs`.)
A clean diff still is not enough on its own to gate PR-B2 if it happened to run against zero or few
settled rooms — the `_MIN_SETTLED_ROOMS_FOR_GREEN` floor above exists so "green because nothing live
was actually checked" can't pass. See `_compare_volatile_live`/`_room_is_settled` in `pusher.py` for
the mechanics.

**PR-B2 (#1557) makes `file` the default source.** Source order per cycle: read
`BatonPaths.FleetProjectionFile`; use it when present, well-formed and fresher than
`PROJECTION_STALE_AFTER_S` (900s); otherwise fall back to `derive_snapshot_and_timelines` for that
one cycle and carry `staleness` on the pushed body, exactly as PR-B1 defined.
`FLEET_GLASS_PROJECTION_SOURCE=derive` pins the pre-PR-B2 always-derive behavior for one release;
an unrecognized value resolves to the default rather than raising. The `derive` path is **kept**,
not deleted — PR-C deletes it, under the removal condition recorded on
`derive_snapshot_and_timelines`'s own docstring (canonical; not restated here). Two consequences
this section states rather than leaves to be discovered:
- **`rooms[].live`/`rooms[].pruned`/`vendors` come from the file verbatim in `file` mode** — never
  recomputed by `pusher.py`, which is the per-cycle duplicate work the switch exists to remove
  (#1886 is the standing reason a second arithmetic is not merely redundant but can be *wrong*).
- **`timelines` is the one field the file cannot supply yet**, so `file` mode pushes `timelines: {}`
  until `FleetProjectionWriter` writes per-room timeline entries (#1902). This is also what blocks
  PR-C: a non-terminal room's timeline needs a `room_detail` call per cycle, i.e. the very `dotnet
  mcp` spawn PR-C exists to delete. `glass.html` accumulates timelines in `localStorage` across
  pushes, so entries already seen persist; a room first seen after the cutover shows none.
`pusher.py --selftest` carries the acceptance instrument — `snapshot_identity_diffs` compares the
finished pushed snapshot from both sources over one frozen fixture and asserts `timelines` is the
only difference, with a planted-difference control on each side. It is a different instrument from
`--compare-projection` above (frozen fixture vs. two live samples, whole snapshot vs. one room);
that function's own comment says why both exist.

**Board + detail-pane IA (#1678, operator ruling 2026-09-02, Combo C+E).** `glass.html`'s Fleet tab
is a three-column state board — Needs You (the conductor pinned first, then Stalled + Indeterminate
rooms) / Running / Done (Failed + Succeeded, dismissible) — with a detail pane that opens on
selecting a card: docked beside the board on desktop, and (≤480px) a slide-in second screen replacing
the board, reached via carousel pills that pick one column at a time and a Back control that returns
to it. A card carries only a label, one state chip, and one telemetry line (below) — no path, no
timeline, no copy verbs; those live in the pane once a card is selected (`roomDetailHtml`), which
also carries the same state-appropriate copy verbs the pre-#1678 per-lane card used to show directly
(`copyButtonsHtml`, unchanged — read-only, copies text only, never executes, spec/baton.md §10).
Superseded the pre-#1678 layout of one `<h2>`-headed, dismissible section per state bucket
(`groupLanesHtml`/`laneHtml`); everything that layout did that this one does not explicitly replace —
the freshness/pusher-alive strip, the empty-state, dark/light theming, the deliverables inbox reader,
the conductor's `deliverables →` link (#1681), the terminal "copy delete"/"copy prune" verbs, and
"Unreadable entries" as its own collapsible list below the board — is unchanged.

**Telemetry on every card, not just Running (the one deliberate deviation from the reference mock).**
Pre-#1678 only a Running room showed a telemetry line at all (`rooms[].live`, above). The ruling
widens that to every card: a Running card still reads `rooms[].live`'s bits (`out`/`ctx`/`N
calls`/`active … ago`); a terminal or Needs-You card instead reads its last step's own `usage`/
`linkedFromUsage` (`ExecutionUsageView`, §3 schema — no pusher or engine change needed, since
`fleet_status` already forwards `steps[].usage` verbatim for every room, not only Running ones) and
renders `out <tokensOut> · <turns> turns · <wallClockMs as Nm Ss>` (`cardTelemetryText`, using
`durFine` for the fine-grained wall-clock a card wants — distinct from the coarser `dur()` this page
already used elsewhere, which rounds to whole minutes). A room with no usage on any step (never
executed far enough to record one) renders the
literal `—`, never a fabricated or blank figure — the same never-invent convention every other
absent-safe field on this page already follows. The detail pane's own Telemetry section
(`fullTelemetryHtml`) shows every step's usage line, not just the last, for the same terminal/
Needs-You rooms.

**Paging and the terminal hot-set cap (#1656).** Measured 2026-09-02: `deliverables_list` returned
292 items / 160,539 bytes in one body, big enough that the operator's MCP connector reported
"Inbox feed unavailable (upstream_error)"; `fleet_status` was 265,193 bytes / 234 rooms per push.
Both mailbox tools (`tools/fleet-glass/worker.js`'s `handleMcp`) now page:
- **`deliverables_list`** takes `limit` (default 50, max 200) and an opaque `cursor` — base64 of the
  next item's own `(pushed_at, id)` identity, so a caller round-trips it verbatim with no
  server-side per-cursor state. Response carries `items`, `count` (the total after any `room`
  filter), and `next_cursor` (`null` once exhausted). A malformed or foreign cursor degrades to the
  start rather than throwing, same posture as every other optional-field convention in this module.
  The list's order is delivery order, not a `pushed_at` sort — `handleDeliver` builds the index
  purely via `index.unshift(...)` per delivered item (`worker.js`), so "newest first" means "most
  recently delivered to the worker," not "newest `pushed_at` first." The cursor is identity-based
  (matched by `(id, pushed_at)`, not by position), so it tolerates a `/deliver` POST landing between
  two `deliverables_list` calls rather than skipping or repeating items.
- **`fleet_status`** stays a single tool (no `rooms_list` sibling — `FleetGlassReadOnlyTests` pins
  the mailbox's `TOOLS` array to exactly `fleet_status`/`deliverables_list`/`deliverable_read`) but
  grows a `page`/`limit` argument pair. With neither argument, `rooms` carries every non-terminal
  room plus only the newest `HOT_TERMINAL_CAP` (40, `tools/fleet-glass/pusher.py`) terminal ones,
  and the response gains `terminal_total` (the full terminal count). Passing `page` (0-based) pages
  over the REST of the terminal population instead. `terminal_archive` rides inside the SAME
  `"snapshot"` KV value as everything else (folded in by #1690 item 2 — previously its own KV key,
  written by a second `env.FLEET.put` on every push that had one; see "Fleet Glass write budget"
  below for why that second write mattered); a plain `fleet_status` call's response size still stays
  independent of the fleet's all-time terminal-room count because `handleMcp`'s `fleet_status`
  branch strips `terminal_archive` back out on the READ side instead of it never having been
  written together. `pusher.py`'s `split_hot_and_archive` computes the hot set
  and archive from the SAME `newest_timestamp` measure `drop_stale_rooms` already uses, so "newest"
  means the same thing everywhere in this module; `timelines` in the pushed body is filtered to the
  hot set's own paths, never the wider surviving-room set, so an archived-only terminal room's
  timeline never rides the hot push either. `tools/fleet-glass/glass.html`'s Done column (the
  pre-#1678 Terminal section's successor, below) fetches additional pages on demand (a "load older"
  link, wired to a one-shot `fleet_status(page, limit)` call through the same `watchTool` the
  periodic poll already uses) and merges them into the rendered Failed/Succeeded buckets, deduped by
  room path against whatever the hot set already showed.

  The cap bounds only the terminal bucket. `non_terminal` rooms — Running, Stalled, Indeterminate —
  ride the plain (no `page`) `fleet_status` response in full, uncapped; `split_hot_and_archive` never
  slices that list, and `glass.html` never pages it either. The 265 KB / 234-room measurement above
  was terminal-room-dominated; a fleet with many concurrently *active* rooms at once (an incident
  storm) can still produce an unbounded default payload, and nothing in this module measures or caps
  that case. `pusher.py` logs one line via `HOT_NONTERMINAL_WARN` (60) when the non-terminal count
  exceeds it on a push — a signal for an operator to notice, not a cap.

**`heartbeat_at` now advances on every successful push (#1656), not just on the hourly
`/heartbeat` ping.** Measured 2026-09-02: `heartbeat_at` stayed at `07:11:28Z` across pushes at
`07:32` and `07:34` even though both succeeded. Root cause: `should_send_derived_ping` (above)
deliberately skips the dedicated `/heartbeat` POST whenever an actual snapshot push already landed
a fresh `derived_at` within its own 5-minute window — correct for `derived_at` itself, but
`heartbeat_at`'s own `at` value is ONLY ever stamped by that same POST, so a fleet pushing
continuously (never idle long enough to need a dedicated ping, never quiet long enough to hit the
hourly cadence) could see `heartbeat_at` sit stale for up to an hour despite every push succeeding.
Fixed in `worker.js`'s `handleMcp` (`fleet_status`'s DISPLAYED `heartbeat_at`, not the stored KV
value) by merging in the snapshot's own `pushed_at` — the same `maxIsoOrNull` merge `derived_at`
already uses, and the same reasoning applies: `pushed_at` is stamped by this Worker's own receipt
clock (`/push`'s handler, never the pusher host's clock), the identical clock-source property
`heartbeat_at`'s `at` already has, so folding it in costs zero extra KV writes and never weakens the
"quiet fleet apart from dead pusher" distinction §7's heading above this one describes — on a quiet
fleet `pushed_at` is exactly as stale as `heartbeat_at` already was, so the merge is a no-op there.

**The false Running ⚠ (#1549, fixed by #1656).** `glass.html`'s per-room age line marked a Running
room ⚠ whenever its last JOURNAL event was more than 15 minutes old — but a healthy 30-minute lane
can have zero journal events between `executionStarted` and `executionExited` (#1549's own
measurement: 6 false STALL-shaped flags out of 6 live rooms), so every long-running tool call read
as stale. `ageLine` now keys the ⚠ on `room.live.lastActivityAt` (the `rooms[].live` field above,
itself a real `.stdout.log` mtime) when the room carries a `live` section at all, and falls back to
the journal-event age only for a Running room `live` was never attached to. §2's `ExecutionProgress`
entry is the engine half this glass-only stopgap was always waiting on: the journal-event age this
paragraph falls back to is now honest too, not just `room.live.lastActivityAt`.

**Fleet Glass write budget (#1690).** Cloudflare's free-tier KV namespace caps at 1,000 writes/day;
the mailbox blew it TWICE (2026-09-02) because the pre-#1690 design budgeted one write per snapshot
push and sized its coalescing floor to ~960/day — i.e. it sat AT the cap before deliveries and
heartbeats were even added, and did not know `worker.js`'s `/push` handler wrote `terminal_archive`
as a SECOND, unconditional `env.FLEET.put` alongside `"snapshot"` whenever a terminal room existed.
Measured that day (`pusher.log`, 00:00–16:50 UTC): 783–1,252 writes from snapshot pushes (469, each
1–2 writes), deliver batches (120 batches, K+1 writes each), and heartbeats (17) combined. This is
the canonical record for the fix that replaced that arithmetic; `tools/fleet-glass/pusher.py`'s own
module docstring and section comments cite this entry rather than restating it.

The fix is a hard, pusher-owned daily write-budget LEDGER, not a smaller fixed interval: a
per-UTC-day counter of KV writes by producer, persisted in its own file
(`DEFAULT_BUDGET_STATE_FILE`, `write-budget.local.json`, F4 below), with a real cost per producer
that matches what `worker.js` now actually writes (the folding below is what makes these costs
small and flat, not scaling with content):
- **Snapshot push** — `SNAPSHOT_KV_WRITE_COST` (1). `terminal_archive` now rides inside the SAME
  `"snapshot"` KV value (folded in by item 2 below) instead of a second, unconditional
  `env.FLEET.put` — one write per push, full stop, never two.
- **Deliver batch** — `DELIVER_BATCH_KV_WRITE_COST` (3), flat, no matter how many items a single
  `/deliver` POST carries — down from a cost that scaled with item count before item 2's fold (see
  "Item 2" below for exactly what changed on `worker.js`'s side). The 3rd write is a conservative
  charge for the delete path (F3(a)/F5 below), not a third `put`.
- **Heartbeat or derived-freshness ping** — `HEARTBEAT_KV_WRITE_COST` (1) — unchanged; the two
  cadences already shared one write per POST (#1613 item 2) and stay mutually exclusive per cycle.

**Per-producer sub-budgets and pacing, not a shared pool (F1, 2026-09-02 review).** The FIRST shipped
version of this ledger (`KV_DAILY_WRITE_TARGET` 700 with a single `DELIVER_RESERVE` of 100 carved
out for deliverables/heartbeats) passed its own arithmetic gate and was still a worse operator
experience than the incident it replaced: because only the snapshot half had adaptive pacing, deliver
(by far the fastest producer) could spend the ENTIRE shared pool before the reserve even mattered —
23 snapshots crammed into the first two hours of the day, then 21h45m of total silence (zero
snapshots, zero deliverables, zero heartbeats) until UTC midnight. A reserve sized as a flat write
count, not a share of the day, is not a reserve against a faster producer at all. The fix: each
producer gets its OWN daily sub-budget, gated independently —
- `SNAPSHOT_DAILY_WRITES` (300), `DELIVER_DAILY_WRITES` (320), `HEARTBEAT_DAILY_WRITES` (60) — sum
  680, under `KV_DAILY_WRITE_TARGET` (700), which stays as the overall sanity ceiling the arithmetic
  gate checks the ledger's grand total against; it no longer gates any individual write.
- `snapshot_pushes_allowed`/`deliver_allowed`/`heartbeat_allowed` each check ONLY their own
  producer's counter against its own sub-budget (`pusher.py`), never `budget_left` of the combined
  total — the write that would cross a producer's own line is simply never attempted for THAT
  producer, with no effect on the other two.
- **AND its own adaptive pacing**, not just a sub-budget: `adaptive_producer_interval_s` (one shared
  formula, `adaptive_snapshot_interval_s`/`adaptive_deliver_interval_s`/
  `adaptive_heartbeat_interval_s` as its three per-producer names) widens each producer's own
  interval as ITS OWN remaining sub-budget for the rest of the day shrinks:
  `interval = max(producer_min_interval_s, seconds_left_in_day / max(1, producer_writes_left /
  producer_cost))`. A bare sub-budget without this would still let deliver (or the
  derived-freshness ping, once snapshot's own throttling stopped suppressing it via
  `LAST_PUSH_TS_KEY`) burn its whole share in the first couple of hours and go dark for the rest of
  the day — the same failure shape at a smaller scale. Deliver's own last-sent timestamp
  (`LAST_DELIVER_TS_KEY`) and `should_coalesce_producer` give it the same coalescing-floor mechanism
  snapshot already had (`should_coalesce_push`, #1538).

**The gate asserts DISTRIBUTION, not just a total (F1/F2).** A total-only check can only ever report
`<= budget` — any arithmetic that routes every write through its own enforcement functions can never
report otherwise — which says nothing about WHEN in the day those writes land, and is exactly what
let the shared-pool design pass its own gate while still going dark for 21h45m.
`simulate_worst_case_daily_writes` now returns per-producer write TIMESTAMP lists (not just the final
ledger), and the selftest asserts, for the snapshot producer at max cadence: the largest gap between
consecutive writes never exceeds 1800s (never a half-hour blind spot), and the day's last write lands
within 1800s of midnight (the day ends still serving). Both assertions are proven to discriminate: a
frozen, hardcoded reproduction of the shared-pool design this PR replaces (`pusher.py`'s
`_legacy_shared_pool_worst_case` selftest helper) passes the old total-only check (700 used == 700
target) but FAILS both distribution assertions (max snapshot gap ≈79,000s; last write ≈8,300s) — the
red half of red-then-green, committed rather than thrown away (the #1690 postmortem's own complaint
about the PRIOR gate's control arm, per F2 below). Against the shipped per-producer design both
assertions pass (max gap 300s; last write within the final 300s of the day).

**F2: a control that actually discriminates.** The first version's "(control) an impossibly low
target fails" assertion was `<= 1` — it PASSED, for the same reason the real arm passed (the ledger
clamps), exercising no path a genuine overrun would take. `simulate_worst_case_daily_writes` now
takes a `ledger_enabled=False` parameter that bypasses every gating check, plus a configurable
`snapshot_cost`/`deliver_cost` (the latter accepting a callable for a per-item shape) — feeding it
the pre-#1690 shape (`ledger_enabled=False, snapshot_cost=2, deliver_cost=lambda k: k + 1`) produces
39,768 writes/day, comfortably over 1,000, proving the gate can fail when fed a genuine overrun.

**F7: the gate now runs the config the fleet actually runs.** The prior selftest only ever drove
`simulate_worst_case_daily_writes` at its default `min_push_interval_s` (90), while the deployed
pusher runs 300 (the operator's own #1690 mitigation) — the printed "23 snapshots" described a
config nothing was running. The selftest now runs both 90 and 300 and prints each; the deployed
number is 300.

**Adaptive snapshot cadence** is `adaptive_snapshot_interval_s` (`pusher.py`'s own docstring says why
it keeps its own name rather than being called generically). Once `snapshot_pushes_allowed` goes
false (snapshot's own sub-budget is spent), the pusher sends exactly ONE more snapshot — carrying a
`pusher` block,
`{"writeBudgetExhaustedUntil": <ISO of the next 00:00 UTC>}` — and then stops snapshot pushes for the
rest of the day; deliverables and heartbeats/pings are unaffected, since each spends from its own
sub-budget. `writeBudgetExhaustedUntil` is absent on every ordinary push, same optional-field
convention as `conductor`; `glass.html`'s freshness strip reads it absent-safe and shows it ahead of
every other staleness banner. **F11 (2026-09-02 review):** the exhaustion-notice push clears
`SNAPSHOT_HASH_KEY` rather than persisting the notice body's own hash under it — the notice's content
(`notice_wrapped`, carrying the `pusher` block) differs from the ordinary snapshot hash
(`current_hash`, computed from `wrapped`), so persisting `current_hash` there left a stale
`writeBudgetExhaustedUntil` banner able to survive past the instant it named on an all-terminal, quiet
fleet at the next UTC-day rollover, suppressing every real banner beneath it. Clearing it means the
first cycle of the new day always re-pushes, regardless of content match. Never a silent stop:
`pusher.log` gets one line per hour naming the ledger regardless of which producers are still
spending (`format_budget_log_line`): `budget: used N/700 (snap a, deliver b, beat c), interval now
Xs`.

**F3(b): the ledger is charged BEFORE the POST, in all three producers.** `worker.js` returns 200
only after its `env.FLEET.put` has already committed, so a client-side timeout or a dropped
connection after that commit is a real KV write the ledger would otherwise never see if it only
counted on success — the exact silent-overshoot mode this ledger exists to close, on a flaky link
that repeats every cycle. A client cannot distinguish "the worker never wrote" from "the worker wrote
and the response was lost", so for a hard external cap the only safe posture is to charge first: this
does over-charge a genuine failure where nothing happened (DNS failure, connection refused, a 413),
and that cost is real, but under-charging costs the cap itself. This is deliberately the OPPOSITE
ordering from the hash/dedupe persistence discipline (`push_snapshot_and_record`,
`send_heartbeat_and_record`, deliver's own `mark_pushed`/`LAST_DELIVER_TS_KEY`) — those still persist
only after a successful POST, since the hash governs correctness of CONTENT while the ledger governs
a hard external LIMIT; they are different things sharing what used to be one branch.

**F4: the ledger lives in its own file, written atomically.** `write-budget.local.json`
(`DEFAULT_BUDGET_STATE_FILE`), separate from `push-state.local.json` — so a lost or reset
deliverables-dedupe state file does not also zero the day's spent budget, and vice versa. Both files
are now written via a sibling temp file plus `os.replace` (`save_push_state`) rather than
`write_text`'s truncate-then-write, since a process killed mid-write (the deploy path's
terminate-and-replace SIGTERMs the incumbent pusher on every deploy, and this file is rewritten
several times per cycle) could otherwise leave a truncated file `load_push_state` cannot distinguish
from "no file" — silently resetting the ledger to zero and re-arming the exhaustion notice.
`os.replace` is atomic on both Windows and POSIX.

**F10: a monotonic rollover guard.** `load_budget_ledger` previously keyed purely on
`utc_day_str(now_ts)`, so an NTP correction moving the clock backward across midnight (or a repeated
forward/back correction) handed the same real day a second full budget the moment the stored date no
longer matched. It now refuses to roll back: a stored date strictly LATER than what `now_ts` claims,
with real usage already recorded, is served as-is; a backward jump against an all-zero stored ledger
is harmless and re-keys onto the earlier day (nothing to double-count yet).

**The arithmetic is now a gate, not a claim, scoped honestly (F9).** `pusher.py --selftest` computes
the worst-case daily write distribution with every producer at its own maximum cadence, driven
through the SAME `snapshot_pushes_allowed`/`deliver_allowed`/`heartbeat_allowed`/
`adaptive_producer_interval_s` functions `main()` itself uses (`simulate_worst_case_daily_writes`),
and fails the selftest if the total exceeds `KV_DAILY_WRITE_TARGET` or either distribution assertion
above fails. This property holds for the three GATED producers (snapshot, deliver, heartbeat/ping)
and the costs named above — it does NOT hold for two paths outside the ledger entirely: (a)
`env.FLEET.delete` on legacy per-item eviction (`worker.js`, uncounted before F3(a); now covered by
`DELIVER_BATCH_KV_WRITE_COST`'s conservative +1, but the physical delete itself is still ungated by
any pusher-side check — it happens on the WORKER side, driven by the index's own size, not by
anything the ledger throttles), and (b) F5's refcounted orphaned-batch reclaim (same file, same
conservative +1, same caveat). Whether Cloudflare's KV free tier counts a delete against this same
1,000/day write limit, or a separate delete limit, is unverified from here (no network access to the
current limits page) — treated as a write, the conservative reading.

**Item 2, the worker-side fold, in one place:** `worker.js`'s storage-key docstring (this file's own
header) is the canonical record of exactly which KV keys exist post-fold
(`"snapshot"`/`"inbox:batch:<id>"`/legacy `"inbox:item:<id>"`) and the read-side fallback for
deliverables delivered before this change; not restated a third time here.

**F5: refcounted batch blobs.** Pre-fix, a batched entry's `inbox:batch:<id>` blob was deliberately
left orphaned once its last index reference was gone (eviction, or a re-delivery re-stamping the
same id under a new batch id) — unbounded KV storage growth with no reaper, no metric, no alarm,
whose eventual failure mode (the namespace filling, `env.FLEET.put` starting to fail) looks like
nothing in this PR months later. `worker.core.mjs`'s `computeDeliverBatch` now also returns
`orphanedBatchIds` — batch ids no remaining index entry references after this POST's eviction or
re-delivery — and `worker.js`'s `handleDeliver` deletes those blobs. Amortised to roughly one delete
per batch in steady state, which is what `DELIVER_BATCH_KV_WRITE_COST`'s conservative +1 budgets for.

**F8: `deliverable_read` distinguishes "known but not replicated" from "no such id".** Post-item-2,
resolving an id spans two reads (`inbox:index`, then `inbox:batch:<id>`) instead of one — KV is
eventually consistent across colos, so there is a real window where the index has propagated (how an
operator sees an id in `deliverables_list` at all) while its batch blob has not. `worker.core.mjs`'s
`deliverableReadOutcome` (pure, selftest-covered) tells that apart from a genuinely nonexistent id:
when the index itself names a `batch_id` for the id but neither the blob nor the legacy key resolves,
`deliverable_read` returns a distinct "known but not yet replicated — retry in a minute" message
instead of asserting non-existence for something the same request's own index says exists.

**F13: the deliver batch cap is BYTES, not item count.** Post-item-2, a `/deliver` POST costs the
SAME flat `DELIVER_BATCH_KV_WRITE_COST` regardless of item count K, so the old fixed
`DEFAULT_DELIVER_BATCH_CAP` (10 items, sized when cost scaled with K) bought nothing once the fold
landed and cost a lot: a 210-item backlog that could ship as 1 batch instead cost 21
write-amplifying ones. `gather_deliverables`/`gather_conductor_deliverables` now cap by cumulative
content bytes (`DEFAULT_DELIVER_BATCH_BYTES`, ~4MB, safely under `worker.js`'s 5,000,000-char
`/deliver` body cap) with a generous item-count ceiling (`DEFAULT_DELIVER_BATCH_COUNT_CEILING`, 2000)
as a backstop only — at least one item is always admitted even if it alone exceeds the byte budget
(fail toward one oversized batch, never toward silently dropping the only thing to show).

**Item 3, the telemetry churn gate, quantizing VALUES not the clock (F6, 2026-09-02 review).** A
Running room's `live` section (item 1 above) changes almost every cycle by construction —
`toolCalls` incrementing, tokens accumulating, `lastActivityAt`'s own 90s bucket advancing — which
would otherwise re-trigger the #1457 change-gate every `interval_seconds` regardless of the
write-budget ledger's own throttling. The FIRST shipped version of `quantize_live_for_hash` bucketed
`now_ts` (the wall clock at evaluation time) into a `LIVE_TELEMETRY_HASH_BUCKET_SECONDS` (300s)
index — which meant ANY Running room with a `live` section forced the hash to flip every 300s
regardless of whether the room's own telemetry had moved at all, guaranteeing the snapshot half
always drew its full ~288/day on any active fleet. This is what made F1 unavoidable rather than
load-dependent: the snapshot half never had a quiet-fleet case that gave the budget back. The fix
quantizes the telemetry VALUES themselves: `lastActivityAt`'s own parsed instant is bucketed to the
same 300s grain, and `toolCalls`/`outputTokens` are coarsened to their own grain
(`LIVE_TELEMETRY_TOOLCALLS_GRAIN` 5, `LIVE_TELEMETRY_TOKENS_GRAIN` 10,000) — an unchanged lane now
hashes unchanged forever, since the function takes no wall-clock argument at all; an advancing one
flips at most once per bucket/grain of REAL progress. A structural change (a different room set, a
state transition, a new or changed deliverable, error text) lives in fields this quantization never
touches, so it still changes the hash — and triggers a push, budget permitting — on the very next
cycle.

**#1712: the KV daily write cap is a vendor error, not a ledger event.** Measured 2026-09-02
(`wrangler tail` on `baton-fleet`): once Cloudflare's free-tier KV namespace hits its hard daily
write cap, every `env.FLEET.put` throws `Error: KV put() limit exceeded for the day.`, which
`worker.js` was letting through as a bare 500 -- the pusher logged 136 `HTTP Error 500` lines in 20
minutes and kept retrying every cycle, and because the heartbeat write fails right alongside the
snapshot/derivation write, `heartbeat_at` and `derived_at` go stale together, which the pre-fix
`glass.html` banner misread as "derivation may be stuck" rather than "the worker itself can't
write." This is the HARD Cloudflare limit underneath the #1690 ledger's own SOFT exhaustion above --
that ledger stops the pusher voluntarily before it ever spends 700/day; this is what happens if the
real cap is hit anyway (a day with more real activity than the ledger's own arithmetic modeled, or
a cap lower than assumed) -- discovered live via a 429, never counted in advance. Fixed at all three
layers: `worker.core.mjs`'s `classifyKvError` recognizes the exact message on every KV put path
(push, heartbeat, and deliver's index/batch/eviction writes) and `worker.js` answers `429
{"reason": "kv-write-cap", "resets_at": <next 00:00 UTC>}` instead of a 500. `pusher.py`'s
`post_json` raises `KvWriteCapError` on that specific 429; every producer that catches it
(`mark_kv_write_cap_exhausted`) forces all three write-budget sub-budgets to their daily ceiling and
records the 429 body's `resets_at` on the ledger (#1829: no longer forcing `exhausted_notice_sent`
true itself -- that leaves the #1690 exhausted-notice snapshot to make its own one attempt next
cycle, now carrying this real `resets_at` as `pusher.kvWriteCapResetsAt`) -- and logs one line, `kv
write cap hit at <t>; no writes until <resets_at>`. `glass.html`'s cap banner keys ONLY on that
field (`pusher.kvWriteCapResetsAt`), checked ahead of `writeBudgetExhaustedUntil` and before
"Snapshot derivation may be stuck".

**Why not an inferred signal (#1712 → #1829).** #1712's first cut inferred a cap from `heartbeat_at`
and `derived_at` being both stale and within one push interval of each other ("a shared write failure
ages both together"). That is a false positive: `pusher.py`'s derived-freshness ping writes `at` and
`derived_at` into the SAME `heartbeat_at` KV value in one POST, so the two ages track together by
construction, and on an idle fleet the ping's own adaptive pacing widens past the suspicion window
with no cap involved at all (measured 2026-09-04: the banner appeared and cleared hours before the
00:00 UTC reset a real cap needs). That arm is demoted to a neutral "no fresh write since \<t\>"
line naming the ping cadence; only the Worker's own 429 body is evidence of a cap.

**Outbound ntfy notifications for terminal/attention-worthy fleet events (#1558, ratified as #1502's
menu items 31/32/33, one bundle — #31 alone trains an operator to ignore their phone within a
week).** Independent of the Cloudflare mailbox above — a separate outbound POST to an ntfy topic
(`tools/fleet-glass/pusher.py`'s "NTFY PUSH" section, `ntfy_topic`/`ntfy_quiet_hours` in
`pusher.config.json`, an optional `ntfy_token` in `secrets.local.json`), no write-budget ledger, and
no secret gate needed — that section's own comment states why. Four event types map to three ntfy
priorities —
`ntfy_priority_for_event`/`NTFY_EVENT_TIERS` is the one table, never restated: lane failed →
urgent, zombie/stalled detected → high, a pusher-level anomaly (an uncaught exception in the
snapshot/heartbeat/deliver loop) → high, lane succeeded-with-warnings → default. Quiet hours
(`in_quiet_hours`) suppress every tier below urgent inside an operator-local window (default
`America/New_York`, config-driven, injectable clock for tests) — urgent always sends regardless.
Dedup (`ntfy_dedup_decision`) gives a standing condition (a lane still Failed, a room still
Stalled) the first-occurrence/fold/magnitude-increase shape #1558's brief specified (citing basis
#922's anomaly dedup as the reference shape): the first occurrence alerts, an unchanged repeat
folds, and a magnitude increase (a failed lane's retry count climbing) re-alerts; a room leaving
its notifiable state clears its dedup entry so a later recurrence reads as fresh rather than
folding forever. `pusher.py` itself carried no prior anomaly-dedup code to reuse at the time this
landed (checked: no `anomaly`/standing-condition-dedup function existed anywhere in the file) — this
is a fresh implementation of that shape, not a reuse of one. An unconfigured `ntfy_topic` never
raises — `main()` logs one line and every notification call is a no-op from there on.

---

## §7 The daemon, narrowed

**The harness is the orchestrator.** There is no resident conversational presence a room maintains
between harness invocations. `RoomTurnHost`/`RoomWakeBridge` (deleted, #1420) and the daemon's
reassignment/pairing/broadcast REST surface went with the daemon narrowing below.

What the daemon narrows **to**: a **room-watcher serving the §8 registry** (`fleet_status` itself
needs no daemon, §6 — the watcher serves the registry the tool will consult, never the tool's own
file reads), the **snapshot push loop** feeding the mailbox (§6),
and the **quota-runway ledger** (below). Two more live responsibilities need a stated home rather
than silently dropping out with the rest of the deleted daemon surface. All of the above assumes
`baton daemon` is actually running persistently; it is kept running by the `baton-daemon` scheduled
task (`tools/tool-refresh/register-daemon-task.ps1`, #1557), cycled onto each newly refreshed tool
head the same way `tools/tool-refresh/refresh.py` already cycles `fleet-glass-pusher`. That script
registers unelevated, so its only trigger is a logon trigger scoped to the registering user; a boot
trigger is not registrable by a standard user and is not used (#1770).

- **`RoomRetentionSweep`** (`Program.cs`, a hosted service) — it prunes execution directories, and
  `ExecutionUsageProjector` has an explicit pruned-path fallback specifically because the sweep moves
  them (`src/Baton/Status/ExecutionUsageView.cs`). It is engine-adjacent housekeeping, not a UI
  concern, and belongs in the narrowed daemon's kept surface alongside the room-watcher.
- **`WatchSweep`** (`Program.cs`, a hosted service, #1488) — fires pending `baton watch`
  registrations once their room reaches Terminal; the full contract (exactly-once claim, notify
  shapes, the daemon dependency) is §2's, under `watch`, not restated here.
- **Fleet-wide concurrency caps** — `DaemonSettingsStore` (`src/Baton.Vendors/DaemonSettingsStore.cs`,
  reading/writing `BatonPaths.SettingsFile`, i.e. `{Root}/settings.json`) plus `ConcurrencySlotGate.SetCaps`,
  applied at daemon startup (`Program.cs`). At HEAD this settings file holds only
  `GlobalConcurrencyCap`/`PerVendorConcurrencyCap` (`DaemonSettingsStore.cs`) — it is machine-wide,
  not per-room, so it belongs in the narrowed daemon too.
- **The fleet projection file (#1557 PR-A)** — `FleetProjectionWriter` (`src/Baton.Cli/Daemon/`, a
  hosted service registered beside `RoomRetentionSweep`) rewrites `BatonPaths.FleetProjectionFile`
  (`{Root}/fleet/projection.json`) atomically roughly every 30s (env-var-configurable, clamped, same
  pattern as `RoomRetentionSweep`'s own interval), calling `FleetStatusTool`'s room-processing logic
  in-process and adding the §6 `live`/`pruned` fields plus `processAlive`/`stdout_last_write_ago_sec`/
  `elapsed` (§6 schema). A fourth kept responsibility under the same outbound-only ceiling the rest of
  this section states: the daemon only ever writes this file, never serves it over a listener. No
  pusher.py change rides with PR-A — both paths run side by side until a later PR retires the pusher's
  own derivation. A reader of this file opens it with `FileShare.ReadWrite | FileShare.Delete` in C#,
  or copies then parses in Python (#1782 — `open()` cannot express `FILE_SHARE_DELETE`), so an
  in-flight atomic rewrite never surfaces a sharing violation or a torn read to it.
- **`DeliveryPoller`** (`Baton.Cli.Daemon`, a hosted service, #734) — a fifth kept responsibility, the
  same outbound-only ceiling as the fleet projection file above: a slow-cadence (default 5 min,
  `BATON_DELIVERY_POLL_INTERVAL_SECONDS`-configurable through `BatonEnvironmentSnapshot`, matching the
  other hosted services' own pattern) `gh`-backed poll of every room whose declared outputs resolve a
  delivery reference (above), recording the four delivery facts once each transition is observed and
  never acting on them. `gh` is invoked the same way workers already invoke it — no credential
  handling of its own (Credential Isolation). A PR URL reference needs no working directory of its own
  (the URL names its own repo); a bare-number reference runs from the room's registered §8 project
  root, and a room declaring one with no such root is skipped, logged once per room rather than
  silently. `gh` absent from PATH entirely is a daemon-wide fact, logged once per daemon process, never
  a daemon failure; a `gh` that ran but refused for one room (not authenticated, a stale PR number) is
  logged per occurrence rather than sharing that same one-shot latch — one bad room must never silence
  the daemon-wide warning for every other room. Polls/day per open-PR room at the default cadence: 288
  (one every 5 minutes). The poller itself never writes through the snapshot push mailbox's coalesced
  KV path (§6) — it talks to GitHub and appends to `flow.jsonl` directly. `delivery` eventually rides
  that mailbox too, as one more field on a room row `FleetProjectionWriter` already emits every ~30s
  regardless (above) — the same "changes a push's contents, never its count" accounting the #1549
  heartbeat entry (§2) already gives for its own field, at at most four transitions per room's entire
  lifetime. Polling for a room stops the moment its own journal already carries a `DeliveryMerged` fact.
- **The singleton mutex is per-home, not per-user** — `DaemonHost.MutexName` (#1773) owns why.

Explicitly **not** kept: pairing (`PairedClientsStore`), WebSocket broadcast (`/api/ws`,
`/api/ws/progress`), sidecar/Tailscale supervision, a desktop-owner-only auth tier, template-picker
endpoints, orchestrator reassignment, and the permission REST answerer (§5) — all of that existed to
serve `Baton.Ui`/`Baton.Mobile` and dies with them (Appendix).

**No daemon reaper (#1513).** None of the kept surface above — the room-watcher, `RoomRetentionSweep`,
or the concurrency-cap apply — ever re-drives a room's own pending retry or reaps a room whose pump
has died. `MutationInterface`'s scheduling loop is the only thing that ever acts on a
`StepRetryScheduled`/`RetryNotBefore` wait: it `Task.Delay`s that wait **in-process**, inside the same
`baton run`/`baton dispatch` invocation that recorded it. If that process exits or is killed, nothing
else in the system will ever complete the room — it does not go terminal on its own. Recovery is
`baton resume`, an operator-driven action, never automatic.

### The quota ledger — what is new build, stated correctly

Polls vendor CLIs' print-mode `/usage`; accumulation from lane logs is attribution only, never the
reset-time source of truth. Quota data rides the push mailbox (§6). I could not find a `/usage`-polling
implementation, a runway projection, or push delivery for quota anywhere in `src/` at HEAD — that part
is genuinely **(new build)**.

What is **not** new build, and must not be re-derived: `FailureClassification`
(`src/Baton/Domain/FailureClassification.cs`) has **four** values —
`Retryable, Permanent, ExhaustedUntil, ToolDenied` — not two. `ExhaustedUntil` is load-bearing
throughout the scheduler, not a stub: it appears across `Baton/Scheduling/RetryEngine.cs`,
`Baton/Mutation/MutationInterface.cs`, `Baton/Outcomes/OutcomeClassifier.cs`,
`Baton/Status/WorkflowOutcome.cs`, and both adapters. Concretely, `AgyWorkerAdapter` already parses
a vendor-reported reset time into an `ExhaustedUntil` classification and a `retryNotBefore` instant
(`src/Baton.Vendors/AgyWorkerAdapter.cs`). So: the classification vocabulary, the retry/
dependency handling built on top of it, and at least one adapter's refusal-message parse into
`ExhaustedUntil` all exist today. What is missing is specifically the proactive `/usage` poll, the
runway projection, and the push delivery — build against that gap, not against a two-value enum that
does not exist.

**Both vendors' `/usage` support.** Both `agy -p "/usage"` and `claude -p "/usage"` answer
structured usage data without a model turn — measured live, with a dated primary-source transcript
for the `agy` half recorded in `docs/vendor-capabilities.md` (the vendor register, which outranks
this paragraph on vendor facts). Nothing in `src/` at HEAD implements a `/usage` poll for either
vendor yet — the measurement is the settled basis the quota ledger is built against, not a shipped
code path. Both vendors participate in the ledger.

### Runway hold (#1848) — shipped

The **enforcement** half of the runway work, and the only place in the tree that gates on a vendor's
own `/usage` counters. It consumes the per-vendor projection #1391/#1869 already persists and adds no
second harvest path: the daemon harvests, `baton dispatch` reads the persisted snapshot
(`BatonPaths.VendorUsageSnapshotFile`). A gate check therefore spends no subscription usage and can
never itself be what exhausts the runway it protects. The harvester's own `/usage` call is exempt from
everything below — it is how the counters are measured.

**Hold new admissions; never arrest for fleet reasons** (operator ruling, 2026-09-04). A dispatch that
would start new vendor spend is refused before the room is provisioned; work already running always
finishes. This gate arrests nothing, throttles nothing mid-flight, and reserves nothing across rooms.

**Which entry points consult it.** `baton dispatch` from cold, per distinct adapter it would spawn on
— and nothing else:

| Entry point | Gated? | Why |
|---|---|---|
| `baton dispatch <role\|template>` | **yes** | The one entry point that admits new vendor spend from cold. |
| `baton dispatch --continue <room>` | no | Rehires a worker the fleet already admitted; continuation, not a new admission. `--override-runway` alongside it is refused (below), never silently dropped. |
| `baton redispatch` / `baton resolve` | no | Both continue work an earlier admission already started. |
| `baton run` / `baton resume` / `baton decide` / `baton supply` | no | Drive an already-provisioned room's own bindings; the admission decision was taken at dispatch. |
| A composed template's later phases | no | Admitted once, at the dispatch that materialised the whole DAG — a phase boundary is not a new admission. |
| Retry / retry-with-continuation (#1373) | no | Same execution's own recovery inside an admitted lane, not new work. |
| Fallback-on-exhaustion rebind (#802) | no | Rebinding an admitted step onto a declared fallback vendor; the lane is already running. |
| Daemon `/usage` harvest | **exempt** | It is the measurement this gate reads. |

**The decision.** `RunwayGate.Evaluate(vendor, snapshot, thresholds, now)` → Admit or Hold, per vendor
and independently: a claude hold never holds an agy or codex dispatch (operator ruling, 2026-09-05).
It holds when the vendor's **week (all models)** window is at or above `weekHoldPct` (default 85), when
its **session / five-hour** window is at or above `sessionHoldPct` (default 90), or when the counters
are not readable at all — a missing or corrupt snapshot file, a snapshot carrying no window this
gate's table recognises, a recognised window whose percentage did not parse, or a snapshot older than
`maxSnapshotAgeHours` (default 6). Stale holds for the same reason unreadable does, stated once on
`RunwayThresholds.EffectiveMaxSnapshotAge`'s own doc comment: a stale counter is not evidence of
headroom. claude's **`week (Fable)`** counter is deliberately not a gate while no worker runs on Fable,
which is why the window table matches vendor window names exactly rather than by prefix. A vendor
outside that window table is admitted, recorded as `runway: unmeasured` — unmeasured is a different
claim from unreadable, and holding on a vendor Baton has never been able to read would block work the
counters say nothing about. **`codex` is still that vendor after #1904**, and deliberately: it now has
an `IVendorUsageSource` and a snapshot file, but a *derived* one whose `percentUsed` is absent unless
the operator declared a plan ceiling (§6, `source: vendor|derived`). Putting its window names in the
table would route every ceiling-less codex dispatch down the "recognised window, no percentage" Hold
arm — holding the newest vendor on the fleet for the same absence this paragraph chose to admit.
Gating on the derived counters is a separate decision, not a consequence of the source existing.

**The harvest is a prerequisite, and its absence is a Hold.** On a machine where the daemon has never
run (or has not harvested within `maxSnapshotAgeHours`), every `claude` and `agy` dispatch is refused
until one harvests or the operator passes `--override-runway`. That is the ruling's own consequence,
not an oversight: unreadable holds, and "no snapshot yet" is the most common way counters are
unreadable. A vendor with no usage source at all is unaffected — it is admitted as unmeasured.

**Thresholds are operator config**, in the settings file baton already has —
`{BatonPaths.Root}/settings.json`, `DaemonSettings.RunwayHold` — fleet-wide with per-vendor overrides
keyed by adapter tag. There is no second config file. A malformed settings file, and any out-of-range
value in a well-formed one, leaves the shipped defaults in force rather than disabling the gate.

**`--override-runway "<reason>"` is the only bypass.** Per dispatch, reason mandatory (blank is a parse
error), no global switch and no silent config bump. On a Hold the dispatch proceeds and the room's
`bindings.json` carries `"RunwayOverride": {"Vendor", "Reason", "Used": true, "Counters":
[{"Window", "PercentUsed"}], "HoldReason"}` — PascalCase, because `WorkerBindingConfigWriter`
serializes that file with no naming policy and every other binding field is spelled the same way; the
`#1849` cost-ledger row for each of that room's executions carries `runwayOverrideReason` (camelCase
there, and deliberately: every cost-ledger field declares its own `JsonPropertyName`). On an Admit
the flag is still recorded, as `"Used": false` — "offered and not needed" stays distinguishable from
"never offered", and no ledger row is stamped, because that override bypassed nothing. Passed together
with `--continue` the flag is **refused**, not discarded: that dispatch consults no gate, so there is
nothing to bypass and no decision its reason would annotate.

**Without the flag, a Hold exits non-zero** (`ValidationRefused`, the same code every other pre-run
refusal uses), printing the counters and the exact flag to use, once. **No flow event is emitted for
any of the three decisions**: a Hold refuses before the room's journal exists, so there is no ledger to
write to. All three surface as a stdout status line, plus the durable `bindings.json` record for an
override.

### The burn ledger — shipped (#1570)

Distinct from the runway ledger above (the `/usage` poll, still unbuilt): this is the *burn* half —
which lane spent what, on which vendor — cross-room, append-only JSONL at `BatonPaths.QuotaLedgerFile`
(`{BatonPaths.Root}/quota-ledger.jsonl`), guarded by the identical named-`Mutex` mechanism §8 documents
for `room-registry.jsonl`. That mechanism was extracted to `MutexGuardedFileLock`
(`src/Baton/Status/MutexGuardedFileLock.cs`) so this store shares it rather than copying it —
`RoomRegistryStore`'s own `RunUnderLock` is now a thin wrapper over the same primitive, name-preserving,
so an older and a newer `baton` build still contend on the one lock. The append/read half above that
lock — the JSONL file format, the dedupe-on-execution-id skip, and the skip-malformed-line read
tolerance — is likewise one shared primitive, `JsonLinesLedger<T>`
(`src/Baton/Status/JsonLinesLedger.cs`, #1884), which this store and the cost ledger below both wrap
under their own lock names; its own remarks state what it guarantees. `QuotaLedgerStore.BuildEntries`
harvests engine-side, at settle — `Program.cs`'s own terminal-sentinel write site — from the terminal
usage `ExecutionUsageProjector` already has in hand for every execution with a recorded start and exit:
one ledger line per execution — `AppendAsync`'s own doc comment states why it skips an execution id
the file already holds, and against what repeated-settle shapes. `Adapter`/`Model` come from
`ExecutionBindingResolver` (`src/Baton/Status/ExecutionBindingResolver.cs`) — the one primitive that
resolves the frozen `ExecutionRequest` fields (#1567) with the `StepRebound` override for the
crash-recovery resubmit divergence (#1583), shared with `ExecutionUsageProjector`'s own parser choice
rather than each store re-deriving the precedence — never re-derived from *today's* `bindings.json` at
read time, which is the read-time re-attribution #802's proposal (§0.5) warns a failover rebind would
otherwise cause on an unfrozen record. Fails open exactly
like the registry: `IOException`/`UnauthorizedAccessException`/
`WaitHandleCannotBeOpenedException` are reported on stderr and swallowed by the caller, never surfacing
as a run failure — the registry's own sanctioned exception to the no-silent-swallow rule, applied to a
second store sharing its mechanism.

**Accepted losses, stated rather than hidden.** A lane killed before it ever settles writes nothing
here, on either path — the settle-time appender's structural gap, not a bug. `baton ledger --rebuild`
re-walks every still-live room's own `flow.jsonl` and merges the result into whatever the ledger
already holds, by execution id — never summing, so running it twice against an unchanged fleet is
idempotent. It recovers strictly LESS than the ledger can hold: `RoomRetentionSweep` (above) moves
execution directories out of a live room's reach on its own schedule, so a room already pruned is
invisible to the walk — but an execution the ledger already recorded for that room survives a rebuild
regardless: `QuotaLedgerStore.RebuildAsync`'s own remarks state the merge rule this rests on, not
restated here. Cite the ruling above — "accumulation from lane logs is attribution only, never the
reset-time source of truth" — rather than restating it: this is that doctrine's burn half, not a
second one.

### The cost ledger (#1849) — phase A, shipped (#1883)

The burn ledger above stays exactly what it is: the per-execution source, keyed by machine, pruned by
nothing. The **cost ledger** consumes it and adds three things it does not have — a *repository* key,
*versioned price provenance*, and durability past the vendors' own history windows. Append-only JSONL
at `BatonPaths.CostLedgerFile(slug)` (`{BatonPaths.Root}/ledger/<repository-slug>.jsonl`), one file per
canonical repository identity, written through the same `JsonLinesLedger<T>` the burn ledger above
wraps (and so the same `MutexGuardedFileLock`) under its own lock name. Written at the same settle site as `quota-ledger.jsonl` (`Program.cs`'s terminal-sentinel write),
from the same `terminalEntries` already in hand, in its own `try`/`catch` so neither ledger's failure
loses the other's. Fails open identically: logged on stderr, never a reason a settled run reports as
failed.

**One row per settled execution attempt.** `CostLedgerStore.BuildEntries` reuses
`ExecutionUsageProjector.BuildByExecutionId` and `ExecutionBindingResolver.Resolve` — the same two
primitives the burn ledger reads — `CostLedgerStore`'s own remarks state what sharing them buys over
a second reader. A retry or redispatch mints a fresh
`ExecutionId`, so it is a fresh row with no extra machinery; a cancellation, failure, arrest or
indeterminate settle is a row carrying that outcome, from the same closed token set
`QuotaLedgerEntry.Outcome` documents; a capture the stream reader could not establish as whole is a row
with `completeness: "partial"` and the reason string `ExecutionUsageView.BilledReconciliationUnavailable`
already emits.
`AppendAsync` skips an execution id the file already holds — its own doc comment states against which
repeated-settle shapes, and what inflated totals that skip is buying, not restated here.

**Schema** (JSON names exactly; every field except `sourceKind`, `estimateStatus` and
`planMeterEstimateStatus` is independently absent when unavailable — omitted, never zero, never
`null`):

| Field | Meaning |
|---|---|
| `sourceKind` | Closed set: `baton-execution` (the only writer today), `claude-code-session`, `codex-session`, `antigravity-session` reserved for phase C. Makes "Baton-only rows" a filter, not an inference. |
| `repository` | `RepositoryIdentity.Value` — see below. |
| `room`, `workflow`, `step`, `execution`, `role` | Identity, off the accepted `ExecutionRequest`. `role` is Baton's worker name; Baton has no second role concept. |
| `adapter`, `model`, `outcome`, `startedAt`, `endedAt` | Route and lifecycle. **`model` is the model the step was REQUESTED at** (`ExecutionBindingResolver`, i.e. the accepted request plus any `StepRebound`), never the model the CLI echoed back — a substitution or a quota-driven downgrade is invisible in it, so grouping rows by `model` groups by intent, not by what ran. |
| `modelsObserved` | The models this row's token dimensions were summed ACROSS, off the vendor's own per-model breakdown (§3). Absent = the vendor reported no breakdown, which is *unknown*, never *one model*. |
| `modelEchoed` | **Reserved, no phase-A writer** — the model as the CLI itself echoed it, which is the one `model` is not. Named now so phase C fills it rather than inventing a competitor. |
| `tokensIn`, `tokensOut`, `cacheRead`, `cacheCreation`, `thinking`, `turns`, `wallClockMs` | The dimensions `QuotaLedgerEntry` carries, same names and same nullability. Cache-read is first-class here, never folded into a billed figure. |
| `verifyStepMs`, `verifyResultsBytes` | #1882's two non-token dimensions, carried through from `ExecutionUsageView` under the same names and by the same attribution — §3 above states which execution gets them and why they are present together or not at all. Neither enters either estimate: a zero-token step changes no price. |
| `billedTokens`, `liveBilledTokens`, `billedUnderReadTokens`, `peakBilledInWindow` | #1706/#1709's vendor-derived figures, carried through under the names `ExecutionUsageView` already defines. |
| `completeness`, `completenessReason` | `complete` / `partial` / **absent**, plus the stream reader's own reason string. `complete` requires a terminal line to have parsed AND the replay over the same bytes to have reconciled against it (§3's #1706 triple present) — it is *not* the default. **Every** value of `billedReconciliationUnavailable` maps to `partial` — including the two that describe no truncation at all, whose ambiguity `ExecutionUsageView`'s reason constants state — because an undecidable case takes the weaker label. **Absent** is the third state, for an attempt whose usage was never read (no parser registered for its adapter, no captured `.stdout.log`): neither label is true of a row nothing was read for, and calling it `complete` is what put an empty row in #1848's trustworthy set. `CostLedgerStore.ResolveCompleteness` is the one decision point. |
| `apiEquivalentUsd`, `estimateStatus` | List-price estimate and its status (`estimated` / `unpriced`). |
| `planMeterEstimateUsd`, `planMeterEstimateStatus` | Plan-meter estimate and its status (`estimated` / `unpriced` / `unknown` / `unmeasured`). |
| `estimateReason` | Why BOTH estimates are `unpriced` for a reason other than a missing rate: `multi-model-usage` or `model-mismatch` (below). Absent when pricing was attempted at all — absent never means *priced*. |
| `priceCatalogId`, `priceCatalogVersion`, `planFactorTableId`, `planFactorTableVersion` | The four provenance stamps that make an estimate reproducible. |
| `issue`, `pr` | #1901 C1. `issue` is the leading `<n>-` of the workspace's checked-out branch — the only source Baton has, since no room record carries an issue number; `pr` is `gh pr list --head <branch>`'s answer at settle. Both bare decimal numbers with no `#` (`LedgerQuery` normalizes both spellings on the filter side). **Absent means "not resolvable at settle", never "none exists"**: a lane that settles before its PR is opened, a branch not created by `gh issue develop`, a `gh` that is missing or unauthenticated, and a workspace directory already gone (a #669 worktree torn down before the append — that teardown runs first) all read the same way. Phase C2's backfill is what fills those in. |
| `filesChanged`, `additions`, `deletions`, `testFilesChanged` | #1901 C1. The shape of `git diff --numstat origin/main...HEAD` in that worker's workspace, from **one** spawn — the per-file form already carries every figure `--shortstat` would summarise plus the paths `testFilesChanged` counts (`tests/` prefix), so nothing is derived twice. Present together or absent together. **It measures the workspace's LOCAL `HEAD`, pushed or not** (#1913 review finding 3): `origin/main` is a remote-tracking ref an ordinary clone already has, and `...` diffs the merge base against local `HEAD`, so a branch that committed and never pushed still records its full shape — these are evidence of work done, never evidence of delivery, which is what `pr` answers. Absence means the diff command did not run or did not succeed: not a git repository, no `origin/main` ref, a workspace torn down before settle, or a spawn abandoned at its time bound. The base ref is hardcoded `origin/main`: a workspace whose work is not based on trunk measures against the wrong base, and generalising it is not this phase. Rename detection and `core.quotePath` are turned OFF, so every path is a plain path the `tests/` prefix can be tested against — a moved file therefore reads as one delete plus one add. A binary file counts towards `filesChanged` and towards neither line total, because git reports no line counts for one. |
| `reviewedRef`, `reviewedPr`, `reviewedHead`, `findingsHigh`, `findingsMedium`, `findingsLow` | #1901 C1. Parsed from the review execution's own `verdict.json` (the artifact #1889 stamps), through `ReviewVerdictSchema.TryParse` and no second reader — a file that is not a valid verdict yields none of the five rather than a partial set. **A finding with no `severity` makes the file invalid** (#1913 review finding 4): the deserializer binds an absent one to `high`, which would have counted a severity nobody wrote into the one field below where `0` is a measurement, so the single reader refuses it — the same answer it already gives an unknown severity, and the review prompt names the field and shows it in its example. `reviewedRef` is verbatim; `reviewedPr`/`reviewedHead` are only what a POSITIVE parse of it extracted (a `#n`/`.../pull/n` reference, or a 7–40 character hex SHA), absent for a branch-name ref. **The three counts are the one place on a row where `0` is a measurement**: `ReviewVerdict.Findings` says an empty array means the reviewer looked and found nothing, so a verdict that exists writes all three including zeros, and no verdict writes none of them. Three flat fields rather than one nested object, because a nested value renders in the CSV view as a quoted JSON blob and stops being summable. |
| `resolution`, `resolutionReason` | #1901 C1. `accept-capture` / `reject` / `close` and the conductor's own `--reason`, on a **correcting row** — see below. Never on an execution row. |
| `attempt`, `effort`, `parentRoom`, `workstream`, `raw` | **Reserved, no writer** — none is derivable from the events a settle has in hand (`modelEchoed`, above, is reserved for the same reason). Named now so a later phase fills a reserved field rather than inventing a competing one. |

**A `baton resolve` appends a correcting row; it never rewrites the row it corrects.** This ledger is
append-only and its rows are immutable, which is what #1849's reproducibility guarantee rests on — so
`CostLedgerStore.BuildResolutionRow` returns a NEW row carrying the room's last execution row's
identity (repository, room, workflow, step, role, issue, PR) and **none of its dimensions**: no tokens,
no wall clock, no estimate. It is a fact about a person's intervention, not about spend. Its execution
id is `<execution>#resolution-<kind>`, and that suffix is load-bearing rather than cosmetic on **both**
of its halves. `CostLedgerStore.AppendAsync` dedupes on `execution`, so reusing the settled row's id
verbatim would make the correcting row **silently vanish** into the skip that exists to stop
double-counting, while a row with no id at all would be appended once per invocation instead of once
per resolution. And the KIND has to be in the key because two resolutions of *different* kinds on one
room is a supported sequence — `ResolveCommand` admits `--close` against a capture already
resolved-`--reject`ed (#1877) — and both chain off the EXECUTION row, never off the first correcting
row (a resolution is never itself corrected). A kind-free suffix would dedupe the second one away: the
ledger would say `reject` forever and the close would be invisible, the same silent-swallow failure one
level up. Keying on (execution, kind) keeps idempotence per kind while letting a genuinely different
resolution land. Written for both shapes `resolve` can leave a room in — a `--reject` that reaches
Terminal and an `--accept-capture` that does not — because the fact is the same either way.

**The correcting row is dated, and by its own instant.** `endedAt` carries the resolution's, never
the corrected attempt's — `CostLedgerEntry.EndedAt`'s own doc states that reading — and `startedAt`
stays absent, for the reason `CostLedgerEntry.StartedAt`'s does. Undated — which is how #1913 found it — the row failed
`LedgerQuery.TimeMatches` on every windowed reading and was counted into `undatedExcluded` instead,
so it was missing from exactly the look-back #1901 asks for. It carries no token or money dimension,
so no windowed *sum* changes; what moves is `attempts`, which the paragraph below already discloses.

**What it costs a reading, in five places.** `LedgerRollup` counts rows, so a resolution row adds one
to `attempts` **and one to `unread`** — the latter is a second meaning for a field documented as "an
attempt nothing was read for", and a resolution row is not an attempt at all. It groups under the
unknown-vendor subtotal, since it names no adapter. It adds one to `apiEquivalentByStatus.unpriced`
and one to `planMeterByStatus.unpriced`, because a row with no estimate takes `EstimateStatus`'s
default — so "how many attempts went unpriced" counts interventions. And it carries the corrected
row's `issue`/`pr` **and its `role` and `outcome`**, so a `--pr <n>` reading — #1901's own acceptance
surface — counts one extra attempt per intervention on that PR, and an `--outcome Failed` or `--role
implement` reading gains an attempt wearing the corrected row's answer (#1913 review finding 6). Token
sums, money sums and `reportedBy` are the safe half, and stated as such: `BuildResolutionRow` sets
none of those fields and `SumPresent` skips nulls, so no double-counting is possible. **All five are
the same remedy, and it is expressible:** `baton ledger --resolution none` is execution attempts
alone, facet views included; `--resolution any` (or one of `accept-capture`/`reject`/`close`) is the
interventions alone. Excluding them from the arithmetic by default instead would hide the
interventions this row exists to make countable. The default — both, counted together — is the file.

**No `verdict` field, and that is a finding rather than an omission.** #1901's phase-C1 text asks a
review row to carry a `verdict` of `APPROVE`/`BLOCK`. **No such value exists anywhere in the product**:
`ReviewVerdict` is `reviewedRef` + `findings` + optional `summary`/`instruments`, and decision 0043's
ruling — severity and status are evidence surfaced to a person, never inputs to routing (Architecture
Rule 1) — is why the schema deliberately makes no overall approve/block judgment. Synthesising one from
the severity counts would be the ledger inventing a judgment the reviewed artifact declines to make. The
counts, the ref and its parsed PR/head are what a verdict actually contains, and they are what the row
records. A future issue that wants an approve/block reading has to change what a reviewer WRITES first.

**Two estimates, both labelled, neither an invoice.** `apiEquivalentUsd` comes from `PriceCatalog`
(vendor → model → dimension → effective ranges, each with a source); `planMeterEstimateUsd` re-weights
the same dimensions through `PlanFactorTable`. An unknown model is `unpriced` on both and **never
borrows a neighbouring model's price**; a reported dimension with no rate in force makes the whole
estimate unpriced rather than a smaller number.

**A price can be borrowed on the token side as well as the rate side, and #1883 closes that half.** An
earlier revision of the sentence above was true of the catalog lookup and false of the token
attribution: claude's terminal figures are a *whole-tree* sum across every model the execution's
subagents used, while `model` is only what the step requested, so one rate applied to that sum prices
another model's tokens at the requested model's rate. Phase A therefore **prices only when
`modelsObserved` is absent, or names exactly the requested `model`**. More than one model is
`unpriced` with `estimateReason: "multi-model-usage"`; a single model that is not the requested one is
`unpriced` with `"model-mismatch"` — the "unpriced beats guessed" ruling applied to attribution rather
than only to rates. Expect `model-mismatch` to be the common case once prices are seeded, because a
requested `claude-opus-5` and an echoed `claude-opus-5-20260101` are different strings: that is a
disclosure of what Baton does not know, not a regression. Pricing each model's own share at its own
rate needs per-model token dimensions on the row, which is **phase B's** schema, not phase A's. The plan-factor table resolves to one of three states
and **has no 1.0 fallback anywhere**: a live promotional window whose percent nobody has measured
resolves `unknown`, and a vendor whose meter has never been measured resolves `unmeasured`.
`PlanFactorStatus`'s own remarks state what a 1.0 default would produce instead, and why that is the
failure this design is shaped around. Nothing on a row says invoice, quota, or spend.

**The shipped catalog prices nothing, and that is a finding rather than an omission.** `PriceCatalog`'s
own remarks record what was searched for and not found here, name the one candidate source, and state
the arithmetic that would have to be invented to use it.
The issue's own "unpriced beats guessed" ruling therefore leaves `apiEquivalentUsd` absent on every
phase-A row, until prices with real sources are added there. What phase A ships is the
schema, the version stamp and the reproducibility guarantee. The **plan-factor table is seeded**,
because its three facts are operator-supplied and citable: Anthropic
cache-read weighting 0.10 ("operator measurement 2026-09-04, unverified"), a Sonnet 5 window to
2026-09-18 with its percent deliberately absent, and agy marked unmeasured.

**Canonical repository identity.** `RepositoryIdentity` (`src/Baton/Accounting/RepositoryIdentity.cs`)
is the one derivation, and #1852 reuses it rather than growing a second: the normalized `origin` remote
(`host/owner/repo`, case-folded, `.git` stripped, every https/ssh/scp/trailing-slash spelling
converging) when there is one, else the git **common** directory. Both are shared by every worktree of
one repository, which a checkout path is not — that is the whole point of the key. `Value` is what a
row records; `FileSlug` is the on-disk spelling, a sanitized prefix plus a digest of `Value`, because
sanitizing alone would let two distinct identities share one ledger file. The engine stays git-agnostic:
this type is pure string work, and `Baton.Cli.RepositoryIdentityResolver` is the probe that runs git and
resolves to nothing rather than throwing.

**Views — shipped (phase B's CLI half).** `baton ledger [<room-dir>] [filters] [--format
text|json|csv] [--drill]` reads the file above; `baton ledger --rebuild` remains the burn ledger's
rebuild, against a different file, and neither touches the other's. No `<room-dir>` is the fleet
reading over the repository the operator is standing in (`--repo-identity <key>` names another); with
one, it is that room's attempts and total — **literally the fleet reading with the room facet set**,
because `LedgerRollup` is the one accounting projection and the CLI formats what it returns rather
than summing anything itself (#1849's "one accounting projection, not divergent arithmetic"). Facets:
vendor, model, role, project, outcome, workflow, PR, issue, source kind. The **window is on
`endedAt`** — `--since` inclusive, `--until` exclusive, so two adjacent windows partition a range.
An attempt whose `endedAt` was never recorded cannot be placed in any window at all: it is left out
and reported as `undatedExcluded` rather than quietly assumed in. **Output order is fixed in every
format that has one:** per-vendor subtotals, then the labelled all-vendor estimate, then the
contributing rows under `--drill`. Determinism is `LedgerRollup`'s promise rather than each caller's —
its own remarks state the three sort keys and why the third is not redundant.

`--format json` is the machine contract Fleet Glass (#1746) and enforcement (#1848) read — one object
`{query, vendors, total, rows?}`, `WhenWritingNull`, with the ledger record's own field names inside
it (the row schema above, including #1901 C1's `issue`/`pr`, the diff shape, the verdict fields and
`resolution`), `rows` present only with `--drill` (absent, not empty, so "not asked for" and "none
matched" stay distinguishable). **`pr` is a facet, not a grouping dimension** — the only grouping
`LedgerRollup` does is per-vendor, so "group by PR" is `--pr <n>` narrowing the whole reading to one,
with `query.pr` echoed back saying what the total is a total OF. `--format csv` writes the rows only, header = the record's field names,
LF-terminated, and that column set is pinned against the record by test rather than by review.
A subtotal keeps the row-level doctrine through the addition, and **discloses what its own arithmetic
cannot preserve**: a token dimension **no** row reported is absent rather than `0` (agy reports no
cache-creation at all); a dimension only *some* of the attempts reported is a partial sum, and
`reportedBy` carries one contributor count per dimension so that a total cannot be read as more
complete than it is (`LedgerReportedBy`'s remarks state the reading it prevents). A row that produced
no estimate
is counted in `attempts` and disclosed under **its own recorded status name** —
`apiEquivalentByStatus`/`planMeterByStatus` carry one count per `EstimateStatus` value, so agy's
never-measured plan meter reads `unmeasured` and a missing rate reads `unpriced`, rather than three
states sharing one bucket named after one of them. Per-model token dimensions — the schema change that would let a multi-model tree be priced
instead of refused — remain phase B's *unshipped* half; nothing above depends on them.

**Phase plan.** A is the record, the catalog, the factor table, the identity key and the settle-time
writer. **B** is the CLI views — room and fleet, time-range and facet filters, JSON/CSV export. **C** is
the import of the vendors' own native session logs under the three reserved `sourceKind` values (and is
where `raw` gets a writer, since only a whole session log carries the vendor's fields verbatim). **D**
is backfill of retained rooms plus compaction, at the 90-day window the native-retention survey on
#1849 settles on. Nothing in B–D requires a schema migration: the source-kind label and the repository
key exist from day one.

---

## §8 Multi-project room registry

**Shipped (#1426).** Name the invariant: **`fleet_status` coverage never shrinks when daemon surfaces
are deleted** — a room that `fleet_status` could find before a given daemon endpoint was removed must
still be findable after. Regression-tested directly:
`FleetStatusToolTests.RegistryEntry_OutsideEveryScannedRoot_IsStillFoundByFleetStatus` registers a room
under a project directory passed as no `roots` entry and asserts `fleet_status` still returns it.

**The true reason this is a prerequisite, stated correctly:** it is not that deleting daemon surfaces
*shrinks* `fleet_status`'s coverage — `fleet_status` derives coverage from `BatonPaths.Rooms` plus
caller-supplied `roots` and nothing else at the scan layer (`FleetStatusTool.cs`); it does not depend
on any daemon surface, so deleting one cannot regress it. The real risk is narrower and still real:
the scan itself is **single-level** (`Directory.GetDirectories`, one call per root, §6) — it has no
notion of "every room across every project a harness might dispatch into," only "every room directly
under whichever roots I was told about." A harness that dispatches into a fresh project directory the
operator never passed as a `roots` entry was invisible to `fleet_status` until someone remembered to
add it. The registry closes *that* gap.

**The mechanism.** `RoomRegistryStore` (`src/Baton/Status/RoomRegistryStore.cs`, namespace
`Baton.Vendors` for the same reason `BatonPaths` lives there — `fleet_status` reads it with no
`Baton.Vendors` project reference) reads and writes `BatonPaths.RoomRegistryFile`
(`{BATON_HOME}/room-registry.jsonl`), one JSON line per registration: room directory path, project root,
created-at.

- **Writer.** `RunCommand.ExecuteAsync` — the one pump both `baton run` and `baton dispatch` share —
  registers the room right after creating its directory, on every call through that pump (a fresh
  dispatch, or a repeated `baton run` against a room this pump already started), so a registration lost
  to a crash between directory creation and the write is repaired the next time this pump runs against
  the same room. `baton dispatch` passes its own resolved workspace (honouring `--workspace`) as the
  project root; a bare `baton run` has no separate workspace concept and uses the process cwd. This does
  *not* cover the separate `baton resume`/`decide`/`supply` mutation verbs — they only ever act against a
  room `baton run`/`dispatch` already created and never re-register it, so a room whose very first
  registration attempt failed and is thereafter driven only through one of those verbs stays
  unregistered until the next plain `baton run`/`dispatch` against it. The write is fire-and-forget with
  respect to the run itself — an `IOException`/`UnauthorizedAccessException`/`WaitHandleCannotBeOpenedException`
  is reported on stderr and swallowed, never surfaced as a run failure, because the registry only ever
  *adds* `fleet_status` coverage and must never gate a dispatch.
- **#1657: throwaway repro rooms are excluded, not registered then pruned.** `RoomRegistryStore.AppendAsync`
  skips writing a room that looks like a repro rather than fleet work (one stderr line names it) —
  `IsThrowawayReproPath`'s doc comment on that type is the one place the exact rule is stated. This is
  wider than the manually-created `%TEMP%\...` repros the issue reported: a **bare `baton run` with no
  `--room-dir`** defaults to `{cwd}/.baton/{workflow}` (`RunOptionsParser`) and is caught by the same
  `.baton`-segment rule, so an ad hoc `baton run` against a workflow file is unregistered by default too,
  not only an explicit temp-dir repro. `baton run`'s `--register` flag (`RunOptions.Register`) opts a
  given room back in; `baton dispatch`/`redispatch` always pass it, since a resolved dispatch/redispatch
  room is fleet work by construction — the flag only ever matters there for an explicit `--room-dir`
  override outside `BatonPaths.Rooms`. `AppendAsync` is also a no-op when a line for the exact same (room
  path, project root) pair is already present, so re-registering an unchanged room on every pump call no
  longer grows the file — a genuine project-root change for the same room path still appends, preserving
  the last-writer-wins fold below.
- **Format: append-only JSONL, not a rewritten JSON map, guarded by a named `Mutex`.** Every dispatch
  that creates a room is a separate, potentially concurrent `baton` process — that concurrency is the
  reason a fleet-wide registry exists at all. A last-writer-wins map would need a read-modify-write
  cycle on every registration; append avoids that. `FileMode.Append` alone is **not** atomic across
  processes on Windows — measured with no lock and no `FileShare` restriction at all: six concurrent
  processes appending under `FileMode.Append`/`FileShare.ReadWrite` lost roughly a fifth of their
  lines, some to two JSON objects concatenated with no newline between them. The shipped writer
  additionally opens with the narrower `FileShare.Read` (the same choice `FlowEventLogWriter` makes
  for `flow.jsonl`), which stops that byte-level interleaving on its own — but not losses: without a
  lock, a second concurrent writer gets a sharing-violation `IOException` instead, which the registry's
  fail-open contract requires swallowing, i.e. a dropped registration rather than corrupted bytes.
  `RoomRegistryStore` closes that gap by serializing every access, read or write, behind one named
  `Mutex` keyed on the registry file path, so a concurrent writer waits and then succeeds rather than
  losing its registration to a sharing violation
  (`RoomRegistryStoreTests.Concurrent_appends_from_many_tasks_lose_no_entries` drives fifty concurrent
  writers at the store's public API and asserts none are lost). "Last-writer-wins per room" is the
  *read-time* semantic on top of that — `RoomRegistryStore.ReadDistinctByRoomAsync` folds repeated
  lines for one room path down to the last one written.
- **Reader.** `FleetStatusTool` unions the registry's entries with its existing `BatonPaths.Rooms` +
  caller `roots` scan. A registry entry whose room directory no longer exists is skipped (not pruned
  from the file yet — see below). Every room `fleet_status` returns, whether found by the scan or the
  registry, carries a `project` field (§6 schema) when a registry entry names one, so callers can
  group the level-one summary by project without enumerating project directories themselves.
- **Malformed/missing tolerated.** A missing registry file reads as no entries; a malformed line is
  skipped without failing the read or hiding the well-formed lines around it — the registry degrades
  to exactly what the directory scan alone would have returned, never fewer.

**Compaction shipped (#1659), closing the paragraph above.** `RoomRegistryStore.CompactAsync` runs the
exact rewrite this paragraph used to describe as undone — fold to one line per room, drop entries
whose directory no longer exists, replace the file under the same `Mutex` every other access takes —
and `baton rooms prune` (below) calls it unconditionally, on every invocation, independent of its own
`--terminal` batch-delete filter. `PreviewCompactionAsync` is the read-only counterpart `--dry-run`
(the default, without `--yes`) calls instead, so the listing's reported counts never come from a write
the dry-run promised not to make.

**Deletion is the only path that removes a room (#1659).** Operator ruling, 2026-09-02: "we definitely
need a way to actually delete stuff, not just hide it from the glass." Fleet Glass's dismiss (§6) is a
per-browser `localStorage` hide — the room directory, its registry lines here, and its pushed
deliverables all persist regardless, reappearing in any other browser and in every `fleet_status`
payload. `baton room delete <room-dir>` and its batch form `baton rooms prune --terminal` are the only
verbs that actually remove a room: the directory, every matching registry line (`RemoveByRoomPathAsync`),
and — best-effort, since the CLI has no reach into the Cloudflare Worker's KV deliverables index
(`tools/fleet-glass/worker.js`'s `/deliver` route accepts no removal verb today) — a
`deleted-rooms.jsonl` tombstone (`DeletedRoomsTombstoneStore`) for the pusher to eventually forward as
a removal, unbuilt as of this paragraph. Both verbs refuse a non-terminal room (no `terminal.json`)
unless `--force`, since a live engine may still hold the room's files open — the same holder-liveness
read (`ConcurrencyGuard.ReadHolderInfo` + `EngineLivenessProbe`) `baton cancel` already uses, never a
second mechanism. `RoomRetentionSweep` (§7) may call the batch form automatically, gated behind
`DaemonSettings.RoomsRetentionDays` (default `null`, i.e. off — the ruling's "operator opts in"). A
retention prune with no `--state` filter deletes `Indeterminate` rooms too — the operator who opts
into `RoomsRetentionDays` accepts that, and `--state Indeterminate` selects them explicitly (or any
other `--state` value excludes them) if that default is unwanted.

**Standing conductor room and `baton deliver` (#1669).** A standing orchestrator room under `{BATON_HOME}/rooms/conductor/` (`role: conductor` in its `bindings.json` stub) holds deliverables authored directly by an orchestrator rather than a worker subprocess. `baton deliver <file> [--title <text>] [--room <room-dir>]` (`--room-dir` also accepted as an alias for `--room`) copies the file to `<room>/artifacts/conductor/<hash-of-source-path>-<basename>` — the destination filename, hashed off the absolute source path rather than the basename alone so two sources sharing a basename never collide on one on-disk file — and appends/replaces an entry in `<room>/artifacts/conductor/manifest.jsonl` keyed on the absolute `source_path` (`title`, `source_path`, `delivered_at`, `sha256`, `artifact_file`). The manifest is encoded as UTF-8 without BOM; readers tolerate a BOM. Re-delivery replaces the entry and updates the file in place. `pusher.py` reads the destination filename from the manifest's `artifact_file` field, never re-deriving it from the basename. The conductor room is never terminal (has no `terminal.json`), is explicitly excluded from `rooms prune --terminal` candidate discovery, from `room delete` (including `--force`), and from the stall detector — one shared check (`ConductorRoomDetector`, `src/Baton.Cli/ConductorRoomDetector.cs`) decides role for all three call sites, the same resolution `fleet_status` already used, so the definition cannot drift between them. `fleet_status` carries the conductor room's `artifacts_path` so it is visible in the Fleet Glass fleet tab with copyable text, and `pusher.py` scans `manifest.jsonl` to push items to `/deliver` with `kind: conductor` and upsert identity on `source_path`, surfacing them in the Glass inbox with a `CONDUCTOR` chip (newest first). The Fleet Glass conductor card renders a `deliverables →` link filtered to the conductor room along with the count of conductor items in the inbox index (#1677).

---

## §9 Bindings and permissions

**`bindings.json` is the room's standing permission for the room ∩ step scopes.** For a harness, "answer
once" means: the bindings file is the pre-answered ladder, written once at dispatch/run time and
consulted on every subsequent decision against that room. **Re-prompting a headless lane for a
permission it already carries in its bindings is a spec violation**, not a defensible conservative
default. `DispatchCommand.ExecuteAsync` writes bindings into the room directory
(`src/Baton.Cli/DispatchCommand.cs`) before `RunCommand` runs; `baton decide` requires `--bindings`
explicitly on every call (`DecideOptionsParser.cs`: *"pass --bindings <path-to-bindings.json>
naming the same bindings the paused room was dispatched with"*) — there is no separate global
last-used-file fallback the CLI path is ever subject to.

**The three-scope model survives: project ceiling ∩ room ∩ step, always narrowing, never widening.**
`bindings.json` is only the **room ∩ step** half of that intersection. The **project ceiling** — the
owner's own control on what any harness-authored `bindings.json` can grant in the first place — lives
in Baton's own app-level config, never inside the project tree, so a compromised or over-permissive
project cannot author its own way past it. Built (#1166): `ProjectCeilingStore` (`src/Baton.Vendors/`),
a flat JSON map at `{BatonPaths.Root}/project-ceilings.json`, canonical project path →
`ProjectCeiling` (`ReadFiles`/`WriteFiles`/`RunShellCommands`/`NetworkAccess` — decision 0004 names no
closed set of ceiling levels, so this reuses the category vocabulary `ClaudeWorkerAdapter.TryTranslatePermissionGrant`
already maps rather than inventing a second one). Decision 0004's "first presented as a trust prompt"
has no interactive shape in a headless dispatch, so `baton trust <project-path> --ceiling
all|none|<categories>` (list/revoke: `baton trust --list`, `baton trust <path> --revoke`) is the
explicit operator verb instead — the PR that built this states that reading as the assumption, not a
correction to 0004's text. `ProjectCeilingGate` (`src/Baton.Vendors/`) is the one choke point both
`ClaudeWorkerAdapter.Resolve` and `AgyWorkerAdapter.Resolve` call at the top of `Resolve`, before
either reads `WorkerInvocation.PermissionGrant`: a `WorkingDirectory` with no recorded ceiling refuses
before any worker spawns (`ProjectNotTrustedException`, naming the `baton trust` verb and the path);
otherwise the effective grant is `ceiling.Cap(roleGrant)` — each category survives only when both the
role's own grant and the ceiling carry it, re-checked against
`PermissionGrant.CategoriesDefeatedByTheShell` so a coherent role grant that becomes incoherent once
narrowed (writes capped away while an unscoped shell stays granted) still refuses rather than shipping
the #529 shape silently. Read fresh on every `Resolve` call, never cached across a process's own
lifetime — `baton trust` may revise the store mid-fleet, and each `baton run`/`dispatch` is its own
process. `CommandWorkerAdapter`/`CaptureWorkerAdapter`/`NoOpWorkerAdapter` are engine-deterministic,
not vendor workers with a grant to cap, so the gate does not apply to them (each says so on its own
type).

**Operator ruling (2026-09-03, #1784): a project ceiling that withholds `NetworkAccess`/`WriteFiles`
closes that category outright — STRICT, even through a shell pattern the binding's own author vouches
as read-only.** `ShellCommandsAreReadOnly` (#1456, below) answers the grant AUTHOR's own coherence
question ("are my named patterns internally consistent?"); it never answers the OPERATOR's project-
ceiling question ("is this category closed?"). Rationale: nothing enforces a scoped shell pattern but
the vendor's own `--allowedTools` matcher — and under agy's `--dangerously-skip-permissions`, nothing
but its tool-name list (#623) — so baton cannot guarantee e.g. `gh pr view*` reaches only github.com,
and the operator's outer bound must not lean on that assertion. A role that needs `gh` under a
network-denied ceiling is supposed to stop working; that is the ceiling doing its job. Built:
`PermissionGrant.CategoriesDefeatedByTheShell(bool honorReadOnlyAssertion = true, IReadOnlySet<string>?
strictCategories = null)` takes a caller-supplied "honour the author's read-only assertion?" input,
defaulting `true` (#1456's behaviour everywhere else — `WorkerBindingResolver`, `DispatchSpecLinter`
via `NetworkReachable`), plus a per-category override: `strictCategories` names the subset of
`WriteFiles`/`NetworkAccess` for which the exemption is withheld regardless of that default.
`ProjectCeilingGate.Apply` is the one caller that populates it — with the categories the CEILING itself
sets `false`, not the post-cap grant — so a category the ceiling actually closes is refused exactly as
an unscoped shell would be, while a category the ceiling leaves open stays exempt even when the role's
own grant already declares it `false` (the #1456 canonical shape, e.g. the built-in `review` role:
`WriteFiles:false` + `NetworkAccess:false` + `ShellCommandsAreReadOnly`) — the ceiling's cap is a
boolean AND of both sources, so the capped value alone cannot tell which one closed it.

**A vendor-native sensitive-path refusal sits ahead of grant enforcement entirely** — `IWorkerAdapter.HasSensitiveOutputPathComponent`/`RunCommand`'s dispatch-time refusal of a room directory with a path component literally named `.claude` (#1823, #599, corrected to a component match by #1834), pinned live by the `claude.sensitive-root-write-refused` sentinel in `tools/vendor-verify/verify.py` (`docs/vendor-doc-audit.md`, #1827).

**Grants fail closed — as a dispatch-time obligation, not a measured runtime property.** The rule:
if a denial cannot be enforced for the chosen vendor, the run must not start. Read it together with
the broken-hook paragraph below, which this rule would otherwise contradict: a hook that fails to
*load* fails **open** at runtime, on both vendors — that measured fact is precisely *why*
enforceability must be established before dispatch rather than trusted at runtime. What exists today
is the measurement (`gate.broken-hook-fails-open` and its `agy` sibling in
`tools/vendor-verify/verify.py` characterize the hazard), not an enforcement of the rule itself. A
dispatch-time probe that a *fresh environment's* hook actually loads is
**(new build)** — until it exists, this guarantee is only as strong as the environment's hook
installation, and a harness author dispatching into an unfamiliar environment should treat it as
such.

**The `PreToolUse`/`agy-hook-check` hook stays the enforcement mechanism** — the only enforcement
point over the toolset a worker actually has, since `--allowedTools` pre-approves rather than
restricting (measured directly: `PermissionGrant.cs`, citing the
`gate.allowedtools-is-preapproval-not-ceiling` sentinel check in `tools/vendor-verify/verify.py`).
Baton ships one on every
spawned worker, on both vendors, via `hook-check`/`agy-hook-check`
(`Program.cs`, `src/Baton.Cli/HookCheckCommand.cs`, `src/Baton.Cli/AgyHookCheckCommand.cs`).

**The hook is binary: allow / deny, nothing else.** The ask band that once made it ternary
(`BATON_HOOK_ASK_TOOLS`, the `permissionDecision: "ask"` STDOUT envelope) was part of the mid-lane
ask machinery and is DELETED (#1417) — lanes are fully pre-cleared, so an ungranted capability
fails closed (the hook's own exit code 2 inside claude's `PreToolUse` protocol — a vendor-internal
convention, unrelated to §3's `ValidationRefused` CLI exit code that happens to share the number)
with no human routing, and a tool on the denied list is denied regardless of anything else. A denial
surfaces as `FailureClassification.ToolDenied` (§5, §7) — that is the vocabulary a harness reads.
(#1390 tracked a measured hollow-success defect against this: a denied worker that exits 0 anyway can
read as `Succeeded` — the classification is the contract. #1622 (b) closed the engine-side half: a
tree-changing role's Succeeded settle now carries `workspaceChanged`/`hollow` evidence — see §3 schema
above — so the classification stays `Succeeded` but is no longer indistinguishable from a real one.
Reclassifying hollow success as a different `state` is a design call left to the operator, not settled
by this fix; #1390's own second half — a worker's un-answerable permission question surfacing as
Paused-with-a-question rather than Succeeded-with-prose — remains open.)

**"Denied" at runtime means:** the hook exits non-zero on claude, or returns a `decision` field
refusing the call on agy — the worker is told it was refused and continues rather than dying.

**A broken hook fails open on both vendors — and its silence is measured on `claude` only.**
`tools/vendor-verify/verify.py`'s `gate.broken-hook-fails-open` check measures a claude `PreToolUse`
hook that cannot execute (missing script, bad interpreter, CRLF-plus-space path) as an **allow**, and
separately measures whether the CLI says anything about the failure at all — distinguishing "fails
open loudly" (detectable at startup) from "fails open silently" (not). Its `agy` sibling,
`agy.broken-hook-fails-open`, is written to claim **fail-open only** — its own description states
plainly: *"whether agy REPORTS the failure is not claimed."* A harness author dispatching into a
fresh config directory or a containerized environment must not assume a hook that failed to load will
announce itself on `agy` — that half is genuinely unmeasured, not merely undocumented.

**#1680: for the one shape where that silent fail-open is a total ungating rather than a partial one** —
an agy grant whose only narrowing IS the hook (`AgyWorkerAdapter.RequiresHookAsSoleNarrowing`, widened
by the #1732 review's F5 to also cover a fully-granted role carrying a shell allow/deny pattern list,
since the hook is the sole enforcer of those too) — two live guards: a resolve-time probe in
`AgyWorkerAdapter.Resolve`, and a first-verdict canary wired into `OutcomeClassifier.Classify` at both
of `MutationInterface.cs`'s production call sites — live dispatch and the crash-recovery replay
(#1732 review N3 closed the gap the first review round left there). **First**, the resolve-time
liveness probe (`ProcessAgyHookLivenessProbe`): a synthetic denied call, sent through the SAME shell
hop (`cmd /c`/`sh -c`) and the identical command string `AgyWorkerAdapter.BuildHookCommand` builds for
both agy's own `hooks.json` and the probe itself (#1732 review N1: one shared function, not two
independent interpolations of the same string) — not a structural respawn of the assembly, which could
not have caught #710's actual failure mode — must come back `deny`, or dispatch is refused outright
before the worker ever starts; the same resolve refuses outright, before probing, an agy grant under
this narrowing whose binding is `StreamJson: false` (#1732 review N5), since the canary below cannot
be reached for it. **Second**, the first-verdict canary settles a run `Indeterminate` rather than
`Succeeded` when a naturally-exited, contract-satisfied, non-quota-vetoed execution reports at least
one tool call but the hook's own **per-execution** verdict ledger recorded none for it (the
`ContractFailure` producer row in §3) — per-execution, not per-room: the ledger's path is an unresolved
`BATON_OUTPUT_DIR` environment reference `AgyWorkerAdapter.Resolve` emits (the same per-dispatch-expansion
mechanism `BATON_ARTIFACTS_ROOT` already uses), only resolving to a real file inside
`CoreDispatcher.AssembleChildEnvironment` at actual dispatch time, so no two executions — same room,
same role or not — ever share one; an earlier design that derived the path once per binding entry
(room-scoped, effectively write-once) would have let a single healthy execution anywhere in a room
permanently disarm the canary for every later one, which is why this is per-execution rather than
per-room or per-role. The tool-call count is summed over BOTH the execution's rolled `.stdout.log.1`
segment (read first, when `ExecutionStreamLogger`'s single 8 MiB rollover has produced one) and its
current `.stdout.log` tail (#1732 review N4), so a long run's earliest tool steps are not missed by
reading only the tail. Wired at both call sites, with the crash-recovery replay arming from the
recorded request rather than from today's binding (#1741): `ExecutionRequest.HookCanaryArmed` and
`HookVerdictLedgerFileName` are journaled at dispatch time, from the same `CoreDispatchTarget
.CountHookVerdicts != null` fact the live-dispatch site already reads, so the replay counts tool
calls (the recorded adapter's stream parser) and verdicts (the ledger file the recorded execution
wrote into the artifacts output directory, read directly through `Baton.Dispatch.HookVerdictLedger
.CountLines` — the one reader `AgyHookVerdictLedger.CountVerdicts` also delegates to, since #1760)
from that recorded fact alone — never by re-resolving today's `bindings.json`. A
binding that refuses to resolve on restart (the probe finds the hook dead now — the persistent #710
shape, not a transient one — or the entry was widened or moved off agy since the crash) no longer
disarms a canary the dispatch-time fact says was actually live. A pre-#1741 journal line carries
neither field (`null`), and the replay falls back to re-deriving from today's binding exactly as it
did before this fix, for that older history only. agy's own fail-open behaviour is otherwise
unchanged.

**#532: claude's counterpart resolve-time probe.** `ClaudeWorkerAdapter.Resolve` now runs
`IClaudeHookLivenessProbe` (`ProcessClaudeHookLivenessProbe`,
`src/Baton.Vendors/ClaudeHookLivenessProbe.cs` — its own doc comment has the "why", record-once
canonical) unconditionally before every dispatch, refusing with `ClaudeHookUnverifiedException`
unless the real hook answers deny to a synthetic call. Same cached-per-process shape as
`ProcessAgyHookLivenessProbe` above, for the same reason.

**What a harness author must configure before dispatch does anything:** a `bindings.json` naming
each worker role's adapter, **model** (§2: always pinned at dispatch time, never a mid-lane choice),
and permission grant, resolvable at both dispatch time (writes the room's copy) and decide time
(reads only the room's copy, per this section's own rule above). `baton resume` is bound by the same
rule as `decide`: the bindings passed continue the room's own standing permissions — the
composition never widens mid-room through any verb.

<!-- record-once-ok: #1679 src/Baton.Vendors/ShellCommandPatternMatcher.cs -->
**The `review` role's ceiling: read-only `git`/`gh`, enforced, not a flat shell refusal (#1456,
operator-approved reversal of #1355).** `WorkerRoles.json`'s `review` entry now carries
`run_shell_commands: true` scoped by `shell_command_patterns` to exactly: `git diff`, `log`, `show`,
`blame`, `status`, `rev-parse`, `merge-base`, `ls-files`, and `git branch --list`; and `gh pr
view`/`diff`/`checks`, `gh issue view`. `denied_shell_command_patterns` closes the named mutating
families (`commit`, `push`, `merge `, `checkout`, `switch`, `reset`, `clean`, `gh pr
comment`/`edit`/`merge`, `gh issue comment`/`edit`, `gh label`, `gh extension`) as a standing, subtractive "never"
(0022's DenyAlways) on top of the allowlist. Trailing-`*` shell patterns are matched on word boundaries. **The full accepting set is two branches on whether `P` itself ends in whitespace, five conditions total, not one** (#1683 F1 second round — the prior "three cases" wording silently assumed `P` never ends in whitespace, so it mis-described the branch that `git merge *` and `git -c *` actually take, the same class of defect F4 raised, restated in the correction). `ShellCommandPatternMatcher`'s own class comment is canonical for this rule, states the two branches and five conditions in full, and is what a change to it edits first — not restated here. So `git diff*` matches `git diff --stat` and `git diff`, never `git difftool` or `git diff-index`; `git merge *` matches bare `git merge` and `git merge origin/main` but never `git merge-base`; `git merge*` (no space), unlike `git merge *`, never matches `git merge origin/main` either; and `git log*` does **not** match `git log=x`, the ungated `=` widening #1683 F6 closed.

**That ceiling is why a review's runtime claims arrive as prose — and #1882's answer is a zero-token
verify step, not a wider grant (operator ruling, 2026-09-04; trigger ruling, 2026-09-05).** The
allowlist above is correct and stays exactly as it is: a frontier model should not be on the meter
watching a test runner scroll, and widening the grant reopens the mutation door the allowlist closed.
But it means "3765 passed" or "selftest exit 0" reaches the reviewer as somebody's sentence, which is
how four reviews on 2026-09-04 each ended with "nothing was executed here". **The token cost of a test
run is not the test, it is a model reading the output, so the cheap version is no model, not a cheaper
one.** `baton dispatch review … --verify-cmd "<command>"` (repeatable) has the ENGINE run the named
commands before the worker's first turn, sequentially, and the contract is:

- **Explicit, never implicit.** Nothing runs unless the flag is passed; a brief stays prose, and the
  queue runner passes the flags from a per-item list. There is no inference from the spec text.
- **Allowlisted shapes only**, refused at parse time with the offending command named: `dotnet
  build*`, `dotnet test*`, and `python <script under tools/ or benchmarks/>` carrying a `--check*` or
  `--selftest*` flag (`Mutation.VerifyStepCommandParser`, which is canonical for the grammar). Shell
  metacharacters are refused rather than passed through, because nothing here would interpret one:
  each command is spawned through the shell-less launcher, never `cmd /c`, with the review workspace
  as its cwd and wrapped in `python tools/buildlock.py`. That wrapping is not optional — two
  concurrent MSBuild runs on one machine kill each other (#1402), and this step launches beside lanes
  that are already building. **Every argument of every shape must also name something inside the
  workspace** — the "no escape" rule the python arm applies to its script path, applied to all of them,
  so `dotnet build ../../elsewhere/Evil.csproj` is refused too. What the gate decides is location, not
  intent: a script's own handling of the flags after `--check`/`--selftest` stays that script's
  business, and the grammar's own comment is canonical for the exact spellings refused.
- **A missing wrapper refuses the step, not the review.** `--workspace` is an arbitrary directory and
  need not be a Baton checkout at all, so `tools/buildlock.py`'s absence under it is checked before
  anything is spawned and recorded as the reason on every requested command
  (`VerifyStepRunner.MissingBuildLockReason`). Spawning anyway put `python`'s own "can't open file"
  exit under a heading naming the operator's command, which a reviewer told to treat a non-zero exit
  as evidence reads as a failing build.
- **A non-zero exit does not abort the review.** It is what the reviewer reads first. Each command
  gets its own wall-clock bound (`--verify-timeout`, 10 minutes by default); a command that exceeds it
  has its process tree killed and is recorded with NO exit code, because a killed tree has none and a
  fabricated `-1` would be indistinguishable from a command that really exited -1. **An absent exit
  code means only that none was observed** — a timeout, a spawn the OS refused, or a command never run
  at all — and the three are told apart in `verify-results.md` rather than by the field. Two other
  outcomes are likewise not command failures and are labelled as such where they appear: a timeout can
  mean a slow command or a long wait for the build lock, and exit 75 is `tools/buildlock.py`'s own
  BLOCKED code, meaning the wrapper gave up waiting and never ran the command — the same reading
  `VerifyRunner` already gives 75 (`VerifyFailedKind.BuildLockBusy`), so the two consumers of that
  wrapper cannot disagree about what it said.
- **`<room>/artifacts/verify-results.md`** carries one section per command — the exact command line,
  the exit code, the wall clock and a 200-line output tail — and the review prompt gains one paragraph
  pointing at it and requiring the verdict's runtime claims to cite it. The prompt says nothing at all
  when no step ran; it must not name a file that does not exist. The worker can actually read it
  because the room's artifacts root is already granted to every worker — the same `BATON_ARTIFACTS_ROOT`
  grant §3 describes for inputs and outputs, which is why the results file goes there rather than into
  the execution's own directory: that one is not addressable before the execution exists.
- **`verdict.json` gains `instruments: [{command, exitCode, wallClockMs}]`,** copied on by the engine
  after the worker exits — never written by the model, and OVERWRITING anything the model wrote under
  that key. That overwrite is the whole mechanism: without it the field is a claim rather than a
  record, and a reviewer could assert an instrument it never had. The bump is additive and optional,
  and the stamp edits the parsed JSON object in place rather than round-tripping the `ReviewVerdict`
  record, which would silently delete the unknown extra fields that schema deliberately tolerates.
  Declaring the field must not narrow that tolerance: a model-written `instruments` of any other shape
  reads as absent rather than failing the parse, since a declared key that throws where an unknown one
  was ignored would turn a review dispatched with no `--verify-cmd` at all into a contract failure and
  a retried frontier run. Nothing is lost — the engine overwrites the key regardless. **"Regardless"
  is literal, and includes the no-step case**: the stamp runs on every `verdict.json` a `baton
  dispatch` produced, and when no verify step ran it REMOVES the key rather than leaving what the
  model wrote. Removal, not an empty array, so that absent keeps its single meaning — no step ran —
  which is what the field's own doc, `docs/agents/invoking-baton.md` and this bullet all already say.
  Skipping the stamp when no step ran is what made the field a claim rather than a record on the
  majority of review lanes: nothing removed a model-written array, and `--notify` carries
  `verdict.json` verbatim off disk. **`baton redispatch` stamps too (#1895)**, always on the removal
  arm: no verify step can run on that path — `--verify-cmd` is a `DispatchOptions` field with no
  binding to inherit, so a redispatched review has no instruments of its own and the key is removed
  rather than left carrying whatever the model wrote. (Not `--verify`, the post-exit flag, which *is*
  inherited as `WorkerBindingConfigEntry.VerifyCommandOverride` — §3's "Verify command resolution";
  conflating the two is what the earlier wording of this sentence did.) The prompt half of the same
  door is closed with it: the bare (`--spec`-less) redispatch reuses the parent's already-built
  prompt, so the paragraph below rode across naming the PARENT room's `verify-results.md` — a run
  that did not happen in the child room, possibly against a different tree, since `--workspace` is
  overridable on redispatch. It is stripped from the inherited prompt
  (`RoleDispatch.WithoutVerifyResultsParagraph`, applied in `RedispatchCommand.InheritBinding`), which
  is the same rule as "the prompt says nothing at all when no step ran" rather than an exception to it.
- **The role's shell grant is unchanged**, and `WorkerRoles.json` is untouched. `--verify-cmd` is
  accepted only for a verdict-producing role (today, `review` alone) and refused for a workflow
  template. It is **not** `--verify`, which overrides the *post-exit* verify command a mutating role
  settles on (`WorkerRole.VerifyPixiTask`, §3's "Verify command resolution"); this one runs before the
  worker and decides nothing. The refusal message says so, because conflating the two is the likeliest
  reason someone reaches for it on the wrong role.

**A deny pattern can bound a command family; it cannot bound an *option* — #1683 F1/F2, and the reason is `ShellCommandPatternMatcher`'s own, canonical there rather than restated here.** Two things changed in this ceiling as a result. `git grep` **left the allowlist**, taking with it the two deny entries #1679 had added for `-O`/`--open-files-in-pager` (a reviewing harness has its own Grep tool, and three spellings were measured spawning a pager past those entries). And a third deny rung was added alongside the two lists above: **`denied_shell_option_tokens`**, whose matching rule and deliberate over-match are stated on `ShellCommandPatternMatcher.IsDeniedByOptionToken`. `review` carries `--output`, because `git log`/`show`/`diff` all accept `--output=<file>` with `--format=format:<bytes>` — an arbitrary file write, invisible to #659's metacharacter scan because no redirection is involved, under this role's own `shell_commands_are_read_only: true` assertion. That assertion is the author's claim rather than a derived fact (`PermissionGrant.ShellCommandsAreReadOnly` says so), and this is what it took to make it true. **The rung is enforced by the two `PreToolUse` hooks and by nothing else, on either vendor** — see `ClaudeWorkerAdapter.StandingShellDenials` for why no vendor flag carries it, including what stays unmeasured. `gh api` is deliberately **not** granted: its HTTP
method is a runtime flag/field (`-X`, `-f`), not something `ShellCommandPatternMatcher`'s glob
prefix-match can bind to GET-only, so admitting it would be an unenforced hole wearing a scoped
label rather than an actually-scoped grant.

**The enforcement is claude's `--allowedTools`/`--disallowedTools`, and it is real for this shape —
correcting this section's own earlier framing where it over-generalised.** §9 above (and
`PermissionGrant.CategoriesDefeatedByTheShell`'s prior doc comment) said flatly that `--allowedTools`
"pre-approves rather than restricts." That is accurate for **cross-tool substitution** — a withheld
`Write` reached through a granted `Bash` (#529) — and for a **wholly omitted** tool name (#331: a
`Bash` absent from both lists still ran). It is not the full picture for **same-tool Bash pattern
discrimination**: `docs/vendor-capabilities.md`'s "canonical ceiling" measurement shows
`--disallowedTools Bash(pattern)` enforced, with precedence over `--allowedTools`, and a Bash pattern
*not* on the allow list denied outright (`Bash(npm *)` refused when only `Bash(git *)` was granted —
the negative control that makes it a ceiling rather than a coincidence). Two granularity limits of
that measurement, stated rather than assumed (#1456 second reader), are now measured and recorded —
full rows, method, and wording in `docs/vendor-capabilities.md`'s "Subcommand granularity and
command-line matching extent (#1461)" subsection, cited rather than restated here. Both came back
against the read-only assertion resting on `--allowedTools` alone: an unlisted read-classified `git`
subcommand is not denied by the pattern at all (claude's own command-risk classification is what
gates it, not the grant — an unlisted *mutating* subcommand is still denied, so the practical split
holds today but not for the reason previously assumed), and the pattern is matched against the whole
command line, so a chained/piped command riding an allowed prefix does execute when it does not
itself create or modify a local file. The deny list above enumerates every known-mutating `git`/`gh`
subcommand family explicitly rather than relying on allowlist-omission alone, and the read-only
assertion rests on that explicit deny-subset plus claude's own read/mutate classification — not on
`--allowedTools` excluding unlisted reads, which it does not do. `review`'s grant relies on exactly
that: only the enumerated `Bash(git …*)`/`Bash(gh …*)` patterns are pre-approved (no bare `Bash`), and
the deny-subset above is belt-and-braces on top. What the #1461 measurement actually leaves standing
against a chained command is a *separate*, unconditional claude guard against local file writes — not
`--allowedTools`/`--disallowedTools`, whose behavior against a denied subcommand riding a chain is
unmeasured and, given the whole-command-line matching above, plausibly weaker rather than stronger.
So a non-file-mutating command chained after an allowed prefix (`git diff; echo …`) would have
executed under `--allowedTools`/`--disallowedTools` alone — the deny-subset was not, on that
evidence, what bounded chaining.

**#1459 closed that hole with a hook-side second layer, wired onto the same `PreToolUse` channel
`HookCheckCommand` already runs (#543, #649).** `ClaudeWorkerAdapter` now sets
`BATON_HOOK_SHELL_PATTERNS`/`BATON_HOOK_DENIED_SHELL_PATTERNS` — declared since #659, left unset
until now (the issue's own "dead code" finding). For a `Bash` call under a scoped grant, the hook
itself now parses the command claude actually received rather than trusting claude's own whole-line
match: see `ShellCommandPatternMatcher.EvaluateChainedCommand`'s doc comment for the exact
segmentation rule and its fail-closed set, and `docs/vendor-capabilities.md`'s #1461 subsection for
the two measured rows this closes. Both rows are regression arms in `ShellCommandPatternMatcherTests`
and `HookCheckCommandTests`.

**#1459's own PR (#1506) shipped that layer wired to only one of the two ways a shell gets scoped —
fixed in the same issue, from #1506's adversarial security review.** `ClaudeWorkerAdapter.Resolve`
derived `BATON_HOOK_SHELL_PATTERNS` exclusively from a structured `PermissionGrant`; a binding
scoping its shell through the raw `PermissionScope` escape hatch instead (`PermissionScope:
"Write,Bash(git diff*)"`, `PermissionGrant: null` — the bindings editor's "Advanced" string field)
fed `Bash(git diff*)` to `--allowedTools` as before, but the hook channel came out tagged-and-empty
(`AgyWorkerAdapter.BuildShellPatterns(null)` is empty), which `HookCheckCommand.Decide` reads as the
deliberate unscoped-shell no-op — so the #1461 chaining escape (`git diff; echo escaped`) still ran
under a raw-scope dispatch, unblocked, exactly as before this section's fix. `Resolve` now derives the
channel from whichever string actually reaches `--allowedTools` — the translated `PermissionGrant`
when one exists, otherwise `Bash(<pattern>)` clauses parsed directly out of the raw `PermissionScope`
(`ClaudeWorkerAdapter.BuildShellPatternsFromRawScope`) — so both paths populate the channel from one
source and cannot drift apart. A bare `Bash` clause (no pattern) still yields an empty channel, same
deliberate unscoped-shell reading as the structured path's empty pattern list. The raw path still
carries no denied-pattern concept (it feeds `--allowedTools` alone), so
`BATON_HOOK_DENIED_SHELL_PATTERNS` stays empty there — not a gap, since the allow-list-and-segment
check above already denies anything not explicitly allowed. With this fix, **both** ways of scoping a
claude worker's shell — the structured `PermissionGrant` and the raw `PermissionScope` string — now
populate the second enforcement layer; the opening sentence's "closed that hole" is accurate against
that full population as of this fix, not only the structured path #1459's original PR measured against.

**#1506's re-review found a second way to reach that same tagged-and-empty shape — fixed in the same
issue.** The naive top-level split this section's fix used could itself be defeated by a
plausible-looking advanced scope, silently reopening the just-closed bypass. `BuildShellPatternsFromRawScope`
now parses that shape correctly and refuses (`PermissionGrantUnsupportedException` at `Resolve`) rather
than degrading to empty; its own doc comment is the canonical record of the parsing rule and what
distinguishes a genuinely-absent grant from a malformed one.

**Round 4 of that same re-review tightened the rule further, to categorically fail-closed** (a
whole-scope balance gate ahead of a fifth swallowed-grant shape, and refusing rather than honoring a
comma-list inside one clause once #1514 found that reading unmeasured against claude's own parser) —
`BuildShellPatternsFromRawScope`'s doc comment is again the canonical record; nothing here restates it.

**Round 5 found that "categorically" had a gap: a balanced fusion of a `Bash(` grant into a
clause the loop would drop.** The balance gate cannot see it, because the string balances. A fusion
gate closes it with a conservation count — `BuildShellPatternsFromRawScope`'s own "Fusion gate:"
inline comment is the canonical record of the count and what it refuses.

**Round 5's re-review found one gap left even past the fusion gate: an explicit but empty `Bash()`
clause still cleared both gates and only degenerated to the same no-op shape at the per-clause
trim.** `BuildShellPatternsFromRawScope`'s own per-clause throw is the canonical record of the
refusal and why it applies only to an explicit-but-empty grant, not to the bare-`Bash`/no-`Bash(`
no-ops above.

**One asymmetry against the denied-tools channel is worth flagging here rather than only in code:**
`HookCheckCommand.Decide` reads an absent or wrong-vendor pattern channel as an unscoped grant, not a
denial — the opposite of how it reads a missing denied-tools list (#600). See that method's own
remarks for the full reasoning; in short, `--allowedTools`/`--disallowedTools` already ran and settled
whether `Bash` is reachable at all before this check is even reached, so a hard denial on its own
absence would have broken every already-shipped unscoped shell role the moment this landed. An explicitly
unscoped grant reads the same way, matching `AgyHookCheckCommand`'s existing treatment of an empty
pattern list on that vendor. **`AgyHookCheckCommand` now routes through the same
`EvaluateChainedCommand` segmentation (#1685), evaluating the DenyAlways channel on every top-level
segment even when the allow list is empty.** Two user-visible behaviour changes follow from this: a
scoped agy grant now permits chains it used to refuse outright (a segment riding after an allowed
prefix on a `;`/`&&`/`||`/`|` boundary is judged on its own terms rather than failing the old
whole-line scan), and an unscoped-with-deny grant now refuses an unparseable line — one
`TrySegmentChainedCommand` will not guess a boundary for — that it used to allow, since the segmenter's
own fail-closed verdict applies before either pattern list is consulted. **#1733 corrected this
paragraph's own prior claim that claude's deny check could stay nested under a non-empty allow list
because `--disallowedTools` was its own primary enforcement of the DenyAlways rung.** That reasoning
held only for an unchained command: `--disallowedTools Bash(pattern)` matches the whole command line
as typed (#1461, above), so it never caught a denied family riding a chain after an allowed or
unscoped prefix. #1731 gave `implement`/`janitor` (both unscoped) a standing deny list, which is what
exposed the gap — `true && gh label create x` reached neither claude backstop. `HookCheckCommand.Decide`
now runs `EvaluateChainedCommand`'s segmented deny check whenever either the allow or the deny pattern
list is non-empty — `deniedShellPatterns.Count > 0 || shellPatternList.Patterns.Count > 0` — matching
`AgyHookCheckCommand`'s condition above exactly; neither is nested under the other's allow list. That
parity holds when both channels are Present — an Absent or WrongVendor deny channel still reads
oppositely on the two vendors, same as the allow channel already does above: claude collapses it to
`Array.Empty` and skips the deny half, agy denies `run_command` outright. Not new to #1731 and not
exploitable today (both adapters emit both channels unconditionally), but it is the one place the
parity claim does not extend, and #1731 is not what closed it.
**#1731 found-while-fixing, same PR: `--label` at PR/issue *creation* time was missing from the
deny list.** `gh pr create --label operator-merge`/`gh issue create --label operator-merge` attach a
label exactly as `gh pr edit --add-label` does, and neither the issue's own token list nor
`denied_shell_option_tokens` covered it until `--label` was added alongside `--add-label`/
`--remove-label`. The short form `-l` is deliberately not covered — `IsDeniedByOptionToken` matches
any whitespace-split token anywhere on the line, and `-l` would deny unrelated commands like `ls -l`.
`PermissionGrant.ShellCommandsAreReadOnly`
(new, #1456) is the named, author-asserted escape hatch that lets a grant like this one compose
without widening `WriteFiles`/`NetworkAccess` just to satisfy `CategoriesDefeatedByTheShell`'s
coherence check — it only counts when a non-empty pattern list backs it (an unscoped shell claiming
read-only is refused as incoherent); see that type's own doc comment for exactly what the assertion
claims and does not derive.

**#1731: `implement`/`janitor` stay unscoped but now carry a standing deny list — a write role may not
create or apply a label, merge a PR, or call `gh api`, on either vendor; the label itself is applied
by the operator, per C-15 (#1730), not restated here.**

**#1731 found-while-fixing, same PR: `EvaluateChainedCommand`'s fail-closed metacharacter set was
never exercised against a broad, unscoped grant before this issue, and (before the operator ruling
below) it denied ordinary commands outright.** `implement`/`janitor` are the first unscoped roles with
a deny list, so adding one routed every command through `TrySegmentChainedCommand`'s segmentation for
the first time (on both vendors — agy already ran it unconditionally since #1725, claude as of #1733
above). That segmenter's fail-closed character set (`` ` ``, `$`, `<`, `>`, `(`, `)`, `\`) was tuned
for `review`'s narrow, scoped, read-only allow list, where nothing outside the enumerated patterns
should execute at all — it had never run against a role trusted with arbitrary shell commands. A
second-reader review measured the result: `dotnet test > out.txt`, `echo $PATH`, and any command
containing a literal `\` (including a Windows absolute path, e.g. `git add C:\Users\...\x`,
pre-existing since #659 and now inherited by these two roles for the first time) — all routine, denied
every one outright as unparseable, not merely the specific named commands (`gh label`/`gh pr merge`/
`gh api`). What follows is that finding's resolution, not the current behaviour.
<br><br>
A first relaxation attempt (`permissiveMetacharacters`, treating bare `$`/`<`/`>` as ordinary unless
immediately followed by a character that opens real execution) was reverted in the same PR after a
second adversarial review measured that it opened new, silent deny-list bypasses a prefix-anchored
matcher cannot safely close: leading or mid-command shell redirection (`>out.txt gh label create x`,
`gh 2>/dev/null label create x`) moves the denied command's literal text out from under a
start-anchored prefix match, and unquoted `${...}`-based word splitting (`gh${IFS}label create x`)
makes the scanned source text never contain the denied substring even though real bash execution
collapses it back to the denied command.

**Operator ruling (same PR, superseding the paragraph above): on an UNSCOPED grant that carries a
non-empty deny list, `$`, `<`, `>` and `\` are ordinary characters, not fatal ones — the over-block was
the wrong call, not the bypasses.** `implement`/`janitor` already hold `WriteFiles`/`NetworkAccess`;
their deny list exists to catch a *cooperative* worker drifting into `gh label`/`gh pr merge`/`gh api`,
not to hold a line against an adversary who can already read, write and reach the network by simply
not routing through this one channel. Denying `dotnet test > out.txt` and `echo $PATH` outright bought
no containment that combination of grants does not already forfeit — it only made the roles harder to
use. A `review`-shaped SCOPED grant (a non-empty allow list) is a real security boundary — its allow
list *is* what stands between the worker and everything else — and stays exactly as fail-closed as
before; nothing here changes for it.

The mechanism: `EvaluateChainedCommand` still splits an unscoped-with-deny command at the same
`&&`/`||`/`;`/`|` boundaries as any other grant, and (#1748 F1) an unquoted top-level newline is
now one of those boundaries too on this scope rather than a fatal character — a multi-line
`Bash`/`run_command` payload (heredoc, scripted step) is routine cooperative-worker shape, not
adversarial evasion, so folding it into one segment was over-permissive in the wrong direction. Each
segment's deny check matches a deny pattern against the segment's whitespace-tokenized *head* rather
than a substring/prefix scan (`gh label*` against `gh label create x` compares `["gh","label"]` to
the segment's first two tokens, not the raw text) — and a segment `TrySegmentChainedCommand` still
cannot find a boundary for (a backtick, `$(`, a subshell, an unterminated quote) is evaluated as one
unsplit whole-line segment instead of refused as `Unparseable`; that verdict no longer fires at all
on this scope, except the pre-existing empty/whitespace-only-command-line guard, which still fires
before either pattern list is consulted (harmless in effect, but not "no longer fires at all"). On
that whole-line fold, the deny match (#1748 F2) scans every token offset in the folded segment, not
only its head, and strips a leading backtick/`$(`/`(`/quote off each compared token — a denied
command riding inside a hiding construct, or sitting in a genuine segment elsewhere on a line an
unrelated construct folded, still denies. **This reopens one family of bypasses on purpose, and
accepts it — anything that moves `gh` off a segment's head without folding the line**:
`>out.txt gh label create x` (leading redirection), `gh${IFS}label create x` and the escaped-space
form `gh\ label create x` (neither tokenizes to a leading `gh`), and `gh $'\''; gh label create x #'`
(a balanced quote span hides the `;`, so segmentation succeeds and the fold's every-offset scan never
runs). Closing them for real needs actual shell argv reconstruction, which is a
different, larger project than a glob matcher; the operator judged that project not worth building
for a channel that is a drift guard rather than a boundary. See
`ShellCommandPatternMatcher.EvaluateChainedCommand`'s own remarks for the mechanism, scoped to what
applies on a SCOPED vs. an UNSCOPED-with-deny grant; this paragraph is the ruling, not restated
there.

**Network honesty: `review`'s `network_access` stays `false`, and `gh` reaches github.com anyway.**
The categorical `NetworkAccess` grant (claude's `WebFetch`/`WebSearch`, arbitrary URLs) is
deliberately **not** granted — that would be a materially larger surface than this role needs. But
the allowed `gh pr view`/`diff`/`checks`/`gh issue view` patterns genuinely talk to github.com as
part of doing their job. So `review`'s "no network" posture is true of the categorical grant and
false of the worker's actual reach: state it that way rather than letting the flag imply a stronger
guarantee than it gives. `ShellCommandsAreReadOnly` is what lets this narrow, command-scoped network
reach coexist with `NetworkAccess: false` in the coherence check — see the field's own doc comment.
That coexistence is scoped to the grant AUTHOR's own coherence question; it does not survive against
an OPERATOR's project ceiling denying `NetworkAccess`/`WriteFiles` — see the #1784 operator ruling
above.

**`agy` now expresses this too, by deferring to the hook rather than refusing (#1387).**
`AgyWorkerAdapter.TryTranslatePermissionGrant` used to refuse `RunShellCommands` without
`NetworkAccess` outright, reasoning that agy has no scoped-shell-without-network flag. That reasoning
still holds for the *vendor flag* — `--dangerously-skip-permissions` is still all-or-nothing — but
#1387's second probe measured that AER's own `PreToolUse` hook (`AgyHookCheckCommand`, the same one
that already enforces the pattern allow/deny lists on the wire) narrows the `run_command` channel
correctly on six probed commands: launched under `--dangerously-skip-permissions` with
`BATON_HOOK_SHELL_PATTERNS`/`BATON_HOOK_DENIED_SHELL_PATTERNS` set to `review`'s own allow/deny
lists, a write was denied, a push was denied (the DenyAlways channel), `curl` was denied, a
non-git/gh read was denied by the same allowlist-shape mechanism as the write —
`docs/vendor-doc-audit.md`'s dated entry states the precise reason and the qualifier it carries, not
restated here — `git status`/
`git log` were allowed, and a hook deny did not cancel the run. Reads are bounded by tool grant, not
by path: `view_file` is granted whole for this role (`ReadFiles: true`), the hook only bounds a path
for the write-family tools, and `HOME`/`USERPROFILE` are not redirected for shell-granted workers, so
a granted read tool can reach the operator's real home — this is pre-existing and identical on claude
and `advise`, not something this probe measured or bounded. Unprobed: the subagent/`manage_task`
tools (denied outright rather than narrowed, #1387 review F1). The allow/deny lists' own prefix-collision
defects #1679 found (`git diff*` admitting `difftool --extcmd`, `git merge*` shadowing the allowed
`git merge-base*`) are fixed, not merely tracked — word-boundary matching landed in #1683
(`ShellCommandPatternMatcher.cs`); `docs/vendor-doc-audit.md`'s dated entry names the population that
predated the fix, not restated here. Note that the six probed commands were run against the lists **as they stood then**,
so #1679 and #1683 changed the lists under that measurement: the mechanism it measured is unaffected
(nothing about how the hook narrows changed), but no probe covers the current `git grep`-free
allowlist or the `denied_shell_option_tokens` rung, which reaches agy only through this same hook. So a grant with `RunShellCommands`, `NetworkAccess: false`, and a non-empty
`ShellCommandPatterns` now resolves to `--dangerously-skip-permissions` and lets the hook do the
narrowing; a grant with shell but no patterns still refuses, because nothing would bound it. A hook
that cannot start reads as an allow on this vendor, so for `review` specifically a broken hook widens
the role to an unscoped shell rather than merely losing narrowing — guards for that are tracked in
#1680, not built here. `review`'s tier still defaults to `claude`
(`WorkerTiers.json`'s `frontier` entry), so a default dispatch is unaffected; an operator who
overrides `--adapter agy` on `review` now starts rather than hitting
`PermissionGrantUnsupportedException` at bind time. This is the same #529 coherence rule §9 already
enforces everywhere else, applied to a grant that #1355 had previously kept flat specifically to avoid
it; #1456 shipped claude's real scoped shell first and accepted the then-open agy-side refusal as the
honest cost of not declining both vendors to keep their capability artificially identical — #1387 is
what closed that gap on the agy side, so the two vendors converge on the same grant shape rather than
staying deliberately unequal.

Until #1759 retired it, `tools/baton-agy-loop/dispatch.py` mirrored this same coherence rule in its
own `grant_refusal()`/`build_bindings()` rather than calling the engine's. #1759 ported the one
assertion that mirror still carried — that every catalog role actually dispatches, on every real
adapter — onto the production path itself (`RoleDispatch.ToBinding` /
`WorkerBindingResolver`, `tests/Baton.Vendors.Tests/TemplateDispatchabilityTests.cs`), so there is now
exactly one implementation of this rule rather than two kept in step by hand.

---

## §10 What is explicitly out of scope

- **Chat as a product surface.** Chat is one internal workflow shape a harness can dispatch (§2); it
  is not a thing a person opens and drives turn by turn.
- **Session-parity UI (desktop/phone daily-driver use).** Nothing here promises feature parity with
  either vendor's own app.
- **Interactive mid-run steering.** Reaching into a running worker mid-generation to redirect it
  without stopping it first is out of scope; only cancellation-then-restart and between-step
  pause/decide (§5) exist.
- **Phone pairing and remote *viewing* infrastructure built for a paired client.** `PairedClientsStore`
  and WebSocket broadcast (§7) are archived; the mailbox (§6) is the harness-era replacement for
  "something remote learns what happened," not a client-pairing model. The tailnet drill-down page
  (C-11, §11) is not this: no pairing state, no client registry — that entry records the distinction
  and the narrow listener it prices back in.
- **A resident orchestrator that decides on a human's behalf.** There is no room-resident presence;
  the harness is the decider, always (§5, §7).
- **Remote *dispatch* triggering — closed, orchestrator-only.** Settled, not open: remote dispatch
  already exists as "talk to your harness from the phone" — a Claude Code mobile session (or any
  other agent that can run CLI verbs and read `terminal.json`/`fleet_status`) driving `baton dispatch`,
  which keeps one set of hands on the workers. A direct phone-to-worker control path would be a
  second interaction surface outside the orchestrator, which the one-surface design retires. C-11
  (§11) does not reopen this: the page it rules in may eventually **arrest** (cancel,
  redispatch-unchanged) but never originate — the originate/arrest line is drawn in that entry.
  `Baton.Sidecar` — the Go tsnet component that existed solely to give a paired remote client
  zero-config Tailscale reach to the daemon's REST/WS API — is DELETED, done (#1420): it was a real,
  tracked Go module (an earlier draft claimed otherwise; a lane verified it existed — corrected), and
  it went with the pairing surface it served, along with `Baton.Daemon.csproj`'s optional copy step for
  its binary.
  **The harness seam is vendor-neutral, deliberately:** any agent that can run `baton` CLI verbs and
  read `terminal.json`/`fleet_status` can be the orchestrator. Claude Code is the current occupant of
  that seam, not a requirement of it.

---

## §11 Register

This document and the code it cites are the **only** registers. `docs/design/*` and the prior
`spec/*` files are deleted, not archived — there is nothing left to supersede or cross-reference,
and a future reader will not find them. `docs/decisions/*` was deleted the same way, then
**partially restored** (#1431): the records still cited by live code are back verbatim under
`docs/decisions/` as read-only history — never edited, resolved mechanically by the
comment-citation gate — and the uncited rest remain reachable only through git history. A restored
record is a citation target, not a register: this document still owns what is settled. Every rule
this document states was
previously justified by a decision record; that justification is now stated inline, in the section
the rule belongs to, and the supersession apparatus (numbered decisions, "supersedes 0049"-style
prose) is dropped entirely.

New decision records are created **fresh**, only when a genuinely new decision is made after this
document ships — never retroactively, and never to re-derive something this document already states
as settled. If a future change needs to record its own reasoning, it gets its own record; it does not
reach backward to reconstruct a numbering scheme that no longer exists.

### C-10 — Windows-only build, test, CI, and packaging

The owner runs everything on one Windows machine. Build, test, CI, and packaging are Windows-only:
no ubuntu/macos CI legs, no non-Windows pixi platforms shipped as a support target, and no per-OS
conditional kept alive for a platform that no longer builds (#1405). This is a statement about what
this repo ships and is verified on. #1458 folded aer-core into this repo as `native/core`, a Rust
crate built and tested Windows-only through this repo's own CI like everything else; #1474 then
ported that engine into plain C# and deleted `native/core` outright, so there is no longer a second
toolchain or a second CI leg to say this about — the whole engine is .NET, verified the one way this
entry already describes. This was never a statement about the archived `aer-works/aer-core` repo
`native/core` was imported from (a separate, now-frozen repo whose own historical CI is out of this
decision's scope) or about a vendor CLI's own OS support (`docs/vendor-doc-audit.md`,
`docs/vendor-capabilities.md`).

**Carve-out, so this entry and `pixi.toml` never contradict each other:** `pixi.toml`'s
`platforms` list keeps `linux-64` alongside `win-64`. That is a dev-sandbox accommodation — a Claude
Code cloud session doing *development* work on this repo from a Linux sandbox — not a second support
target; nothing is built, tested, or packaged for it, and `osx-arm64` is dropped outright.

**Installation and versioning (#1645, side-by-side per-commit installs #1668).** `baton` ships as a self-built,
unpublished `dotnet tool` — README's *Installing `baton`* section owns the user-facing command index. Its release
version is one value, `Directory.Build.props`'s `<Version>` under `src/Baton.Cli`, read by `VersionInfo` at build time
(`baton --version`) and by `InstalledVersionDrift` at dispatch time; nothing else in the tree carries a second copy of it.

**Layout and launcher (#1668).** Installs sit side-by-side under `{BATON_HOME}/tools/<sha>` (one directory per commit SHA,
installed via `dotnet tool install baton --tool-path {BATON_HOME}/tools/<sha> --add-source bin/pack`), so refreshing the
tool never touches a directory a running lane loaded from. The currently active version is named by `{BATON_HOME}/tools/current`
(a one-line pointer file holding the commit SHA, written atomically via temporary file and replace). `baton` on PATH is a thin
launcher shim pair in `~/.dotnet/tools` (`baton.cmd` + `baton.ps1`, alongside a POSIX `baton` wrapper) that resolves `current` at
process start and executes that directory's `baton.exe` with the original arguments and exit code. A missing, empty, or garbled
pointer fails closed with exit code 1, printing an error naming `pixi run tool-refresh`.

**Pruning (#1668).** After a successful pointer flip, `tool-refresh` prunes `{BATON_HOME}/tools/<sha>` directories beyond the
newest 3 that no live room references. A room is live when it has no `terminal.json`; dispatch records `ToolSha` in each room's
`bindings.json` so the pruner preserves any directory a running lane was dispatched from even if it falls outside the top 3. A
live room with no recorded `ToolSha` protects nothing under this check — for such a room the newest-3 cushion is the only guard
against pruning the directory it actually runs from.

**Tool refresh.** Refreshing is `pixi run tool-refresh` (`tools/tool-refresh/refresh.py`): packs the checkout, installs into the
new `{BATON_HOME}/tools/<sha>`, verifies `--version` and `templates --json` directly from that directory's binary, flips `current`
atomically, installs/updates the launcher (uninstalling any legacy global tool in `~/.dotnet/tools` to prevent executable
collision), rebuilds `src/Baton.Cli` Debug for the Fleet Glass pusher, restarts the `fleet-glass-pusher` scheduled task, and prunes
old unreferenced tool directories. It requires no drain wait and writes no drain marker.

**Manual drain marker.** Draining is retained solely as an operator-invoked stop: an explicit `{BATON_HOME}/draining.json`
marker causes `baton dispatch`, `baton redispatch`, and `baton resume` to refuse with `ValidationRefused` (2) fail-closed;
`pixi run tool-refresh --abort` clears it. `InstalledVersionDrift` continues to warn on stderr when the installed version is behind
a discoverable checkout.

### C-11 — The tailnet drill-down plane (glass v2.5)

Ratified with the operator 2026-08-31, out of the glass v2 design session (#1502). This entry is the
record §6's own tripwire demanded before any page could be built — written first, as the epic
requires, and written honestly: the thing being ruled in **is** a maintained page, and this entry
amends the ruling's reach rather than pretending the page slips under it.

**The decision.** Observability splits into two planes by what the bytes are, not by preference. The
**mailbox plane** (pusher → Worker KV/MCP → artifact, §6) owns the fleet row: small, curated,
change-gated, secret-gated, reachable from a Claude conversation, working while the machine sleeps.
The **tailnet plane** — a page served by the existing daemon (§7), bound to the tailnet/loopback
interface only, never `0.0.0.0` — owns drill-down: live stdout tail, full timeline, room artifacts.
Neither is a fallback for the other.

**Why the mailbox cannot carry drill-down — the constraint that forced a second plane.** Two hard
walls, not taste. The secret gate: the deliverables path exists to guarantee the mailbox never
carries `prompt.txt` or `.stdout.log` — only declared outputs through a fail-closed denylist — and a
live stdout tail is precisely the uncurated stream that design refuses, on a public repo. (#1351: the
invariant this rests on holds fleet-wide, not just at this denylist — a room's file list is what the
worker produced, and engine capture files such as `ExecutionStreamLogger`'s stream logs are filtered
at the single listing seam, never restated per consumer; `ExecutionOutputDirectoryListingTests` pins
that no second, unfiltered listing seam exists in `src/`.) The write
quota: Cloudflare's free KV tier caps at 1,000 writes/day; a live tail at the pusher's cadence is
~3,456/day — the #1457 change-gate exists because even the *fleet row* brushes this ceiling. On the
operator's own tailnet both walls vanish: the bytes never leave the network, and no third-party
quota is in the path.

**What §6's "never a second application" still governs, and what it no longer does.** That ruling
stands, un-softened, for the mailbox plane: drill-down reachable from a conversation is `room_detail`
in the same MCP host, and no page grows there. This entry rules in exactly one additional surface —
a read-only diagnostic page on the private plane — because the mailbox physically cannot carry its
payload. The tripwire this entry inherits from §6 is restated for the new plane: the page is a
**diagnostic**, not an application. It renders what the room record already says; v2.5 ships it
read-only. The only interactions it may ever gain are the two **arresting** reflexes — cancel and
redispatch-unchanged — behind confirm, executed through the same engine verbs as any terminal and
recorded as room facts, so every observer sees the transition through the room record. The
conductor/orchestrator remains the only **originator** of work: dispatch-new-lane, amended re-briefs,
and gate approvals stay closed from the page (§10's remote-dispatch ruling, unamended). If the page
grows an origination affordance, this entry has been violated, not extended.

**Why this is not the pairing infrastructure §10 archived.** `PairedClientsStore`, the WebSocket
broadcast, and the tsnet sidecar existed to give a *paired remote client* a registry, reassignment,
and zero-config reach. A bookmark on a tailnet holds no pairing state, has no client registry, and
needs no reassignment — the network is the authenticator. What #1420 deleted is not what this entry
adds; what it adds back is one HTTP listener on the daemon, priced and narrowed to this purpose.

**Transport: SSE out, plain HTTP `POST` for the eventual arrest verbs — WebSocket considered and
rejected.** The live view is one-directional; `EventSource` gives reconnect and `Last-Event-ID`
resume for free, which matters because the primary client is a phone that sleeps constantly. The
arrest verbs, when they arrive, are rare, discrete, and want request/response semantics — a status
code, per-request auth, a log line — not a frame on a stream; routing them over `POST` is the better
design even where a socket already exists. A bidirectional channel earns its machinery only under
chatty two-way traffic, and the steering model settled alongside this entry (arrest + rehire;
corrections travel as briefs through `redispatch --spec`, #1495/#1381) guarantees there is none.
Revisit only if a genuinely interactive surface is ever ruled in — which §10's mid-run-steering
ruling currently forbids.

### C-12 — Gate receipts: one passing run per tree, CI is the independent one

Measured 2026-09-01: `.githooks/pre-push` ran `gates-fast` under the shared build lock
(`tools/buildlock.py`) on every push, even seconds after a dispatched lane had already run
`gates`/`gates-quiet` — a strict superset — on the identical tree. With several lanes queued on the
lock, a push could sit for tens of minutes redoing work already done, and CI then ran everything a
third time regardless. `tools/gates/gates.py` now writes a receipt (`<git-dir>/baton-gate-receipt`,
one per worktree) on every PASS, recording the tree hash, a hash of the uncommitted diff, which mode
passed, and a timestamp; a FAIL deletes it. The pre-push hook (`pixi run gates-check-receipt`) skips
its own run only when the receipt's tree hash and dirty-hash still match `HEAD^{tree}` and it is
under six hours old — any mismatch falls through to a real `gates-fast` run. This narrows what the
hook re-verifies, not what CI verifies: CI remains the one platform-independent run and is never
skipped by a local receipt.

**The pre-push hook is a fast local mirror; CI is the authority (#1676).** `.github/workflows/ci.yml`
runs `pixi run gates-ci-quiet` (`gates.py --ci`) as its own required job — the same tracked member
list the hook and a dispatched lane run, never a hand-picked subset of individual `pixi run <member>`
steps that could drift from it. `--ci` excludes any `CI_SKIP`-marked member (each entry needs a
reason, ratcheted the way `sabotage.py`'s `ALLOWLIST` is) and asserts the executed member list
matches the tracked one, so a member silently dropped from either side fails loudly instead of
passing quiet.

**Scope, stated plainly: tracked content only.** The dirty-hash is `git diff HEAD`, which does not
see untracked files. A tree that was already dirty when its receipt was written, and then gains an
untracked file before the next push, still matches -- the receipt does not re-verify content `git
diff HEAD` cannot see. A clean tree gaining any file `git status --porcelain` reports (tracked or
untracked) is still caught, because that flips the dirty bool itself -- a `.gitignore`d file is not
reported by `--porcelain` either, so it is not caught by that path or any other.

**Measured 2026-09-02 (#1648):** git exports `GIT_DIR`/`GIT_INDEX_FILE`/etc. to every hook, and
`gates.py`'s own selftest fixture spawned `git init` in a temp dir without scrubbing them, so a
push under `.githooks/pre-push` re-initialized the pushing repo itself instead of the fixture's
temp dir -- `.githooks/pre-push` now `unset`s the `GIT_*` keys before invoking anything, and
`gates.py` scrubs them from its own process environment and passes an explicit scrubbed `env=` to
every git subprocess its fixtures spawn.

### C-13 — Build fan-out is bounded; gates records what it costs (#1671)

Measured 2026-09-01/02 on the 15.7 GB fleet box: lane concurrency is memory-bound, not CPU-bound.
Each worker (`claude`/`agy`) costs ~300 MB irreducibly, but every concurrent `gates`/`gates-fast`/
`dotnet test` run added a persistent `VBCSCompiler.exe` (~400 MB) plus MSBuild worker nodes kept
alive by node reuse, and `dotnet test` at solution scope ran up to 3 `testhost.dll` processes
concurrently (5 xUnit test projects; MEASURED via `Get-CimInstance Win32_Process` mid-run, command
lines naming three distinct test projects at once). Two workers died mid-tool-call at 6 lanes / 2.2 GB
free (#1622); three lanes now hit a ~1.4 GB floor with the build fan-out accounting for the
difference between that and physical pressure. Four changes narrow the fan-out this repo controls:

**No MSBuild node reuse, no shared Roslyn compiler server, for every pixi-run build.**
`pixi.toml`'s `[activation.env]` already set `MSBUILDDISABLENODEREUSE=1` (#909, concurrent-worktree
node-pool collisions); `UseSharedCompilation=false` joins it there for the same reason and the same
scope -- an ordinary MSBuild property, overridden by an environment variable exactly like
`MSBUILDDISABLENODEREUSE` (`Microsoft.Managed.Core.targets` only defaults it to `true` when unset).
Chosen over a `Directory.Build.props` env-gated condition: a second mechanism for the same class of
setting is a second place to look, and `MSBUILDDISABLENODEREUSE` already established this repo's
answer. Scope, stated once: every `pixi run` build -- lane and gates alike -- inherits both settings;
a `dotnet build` run directly, outside pixi's activation, does not, which is what "the interactive
developer build is unchanged" means here.

**`gates.py` shuts down the MSBuild build servers pass or fail.** `dotnet build-server shutdown`
runs after every gate whose name starts with `test` (where the testhost fan-out actually
accumulates) and again in an outer `finally` around the whole run (`run_gates_and_shutdown`), so a
`--fast` run with no test leg, or a crash before one is reached, still frees the nodes for the next
lane queued on `tools/buildlock.py`. Proven red-first: `gates.py --selftest` injects a counting fake
in place of the real shutdown and asserts it fires on a FAILING test-shaped gate and when the inner
run raises, not only on a passing run. Trade-off, stated once: `dotnet build-server shutdown` (no
target argument) is scoped to the current user session, not to the invoking repo or lane, so a gates
run also kills any build server a concurrent *non*-pixi build on this box is using -- an interactive
IDE/hand `dotnet build`, which deliberately keeps node reuse and the shared compiler on (the "the
interactive developer build is unchanged" scope stated above). Accepted rather than narrowed: every
tool path this repo tells its own tooling to use goes through pixi (CLAUDE.md, "never invoke `dotnet`
directly"), where both are already off, so a concurrent lane's own in-flight `pixi run` build has no
server process to lose to another lane's shutdown call; `--vbcscompiler`/`--msbuild` targets narrow
*which* servers die, not the session-wide scope, so there is no narrower target that fixes this.

**`dotnet test` serializes across the five xUnit projects, in-assembly parallelism untouched.**
`test-no-build` (the leg `gates` runs) now passes `-m:1`, MSBuild's own max-node-count. MEASURED
which of the two candidate knobs actually owns this: a VSTest runsettings
`RunConfiguration.MaxCpuCount=1` left 3 concurrent `testhost.dll` processes running (that knob
governs parallelism inside a single `vstest.console` invocation given an explicit assembly list,
which a solution-scope `dotnet test` does not go through); `-m:1` left exactly one `testhost.dll`
process running at a time, and the run's own "Test run for X" headers -- which VSTest prints only
once a project's host has actually started -- appeared one project at a time rather than all five up
front. Neither knob touches xunit's own in-assembly test-collection parallelism. Cost: the five
projects' durations now sum rather than overlap (measured this box: ~2m parallel baseline vs ~3m17s
serialized for the test leg alone) -- accepted, since the acceptance bound is on the whole `gates`
run's wall time, most of which is `fmt-check`/`lint`, not this leg.

**Amended #1833.** `-m:1` stopped serializing anything once `dotnet test` moved onto Microsoft.Testing.Platform
(root `global.json`, required by the xunit v3 4.0.0 bump #1833 took to close #984): MEASURED being forwarded to
each test module as an unrecognised argument, so every module reported "Zero tests ran" (exit 5) instead of
running serialized. MTP's own equivalent, `--max-parallel-test-modules 1`, was MEASURED to reproduce the same
one-project-at-a-time shape this entry describes (durations summed, not overlapped) — `pixi.toml`'s `test-no-build`
carries it now; the measurement and the trade-off accepted above are otherwise unchanged.

**`buildlock` already covered `dotnet test`/`test-no-build`.** `tools/buildlock.py` (#1402) wraps
every MSBuild-owning pixi task; `test`, `test-no-build`, `test-flow`, and `test-other` were all
already invoked through it before this issue, so at most one MSBuild tree exists machine-wide
regardless of how many lanes are running `gates` concurrently. Confirmed, not changed.

**Telemetry: `gates.py` records what a run cost, in a sidecar the receipt never reads.** Free
physical MB (`GlobalMemoryStatusEx`) and the system-wide MSBuild/VBCSCompiler/testhost process count,
sampled once at the start and once at the end of every run, land in
`<git-dir>/baton-gate-receipt.telemetry` -- a file separate from `baton-gate-receipt` itself, the
same shape as `buildlock`'s own `.info` sidecar, so it can never become part of what
`--check-receipt` matches (tree/dirty/diff_hash/timestamp). The process count reads `Get-CimInstance
Win32_Process` (name + command line), not `tasklist`: VSTest's per-project hosts run as `dotnet.exe
exec ...testhost.dll`, never as a process literally named `testhost.exe` (measured, report-1671.md --
`Get-Process -Name testhost` read 0 throughout both a baseline and a `-m:1` run), so a testhost is
only visible by matching `dotnet.exe`'s command line, which `tasklist` does not expose. Both readers
are best-effort and `None` off Windows (pixi.toml's `linux-64` dev-sandbox leg carries no
`GlobalMemoryStatusEx`/`Get-CimInstance`). This is what a future "measured `<N> MB free` → no new
lane" conductor rule would read instead of the fixed `<2 GB free` guess it replaces -- that rule
itself is not part of this change.

### C-14 — Fleet Glass board redesign: Combo C+E, telemetry on every card

Operator ruling, 2026-09-02, after reviewing an eight-layout options page (C, D, E, C+A, C+B, C+E
among them, `docs/agents/...` scratch artifact, not itself a register): **Combo C+E** — a
three-column state board (Needs You / Running / Done) with compact cards, plus a detail pane that
opens on selecting a card (docked on desktop, a slide-in second screen on the phone at 390px, reached
via carousel pills). Copy verbs, the full path, the step timeline, and the full per-step telemetry
breakdown all moved into the pane; a card itself carries only a label, one state chip, and one
telemetry line. **One deliberate change from the reference mock:** every card carries that telemetry
line, not only Running ones — the operator wants burn visible fleet-wide at a glance, not only while
something is actively running. §6's "Board + detail-pane IA" and "Telemetry on every card" entries
are this decision's full technical contract (schema, field provenance, the `—` no-fabrication rule);
this entry records only the decision itself and why it deviates from the mock it was ruled from.

### C-15 — Diff-shape CI gate: test-only PR self-weakening and protected tooling (#1603)

<!-- record-once-ok: #1744 tools/diff-shape/diff_shape.py -->
Ratified design (operator, 2026-09-01) — closes the "a conductor can relax the bounds on its own authority" hole. A required CI check (`diff-shape`, `.github/workflows/diff-shape.yml`, `tools/diff-shape/diff_shape.py`) that fails when either holds:
1. **Test-only PR weakening:** the PR touches no `src/` code AND a pre-existing test file's diff hits at least one of four criteria: (a) an unpaired removed line matches an assertion/test-declaration pattern — paired against added lines in the *same hunk only* when the deleted line itself matches the pattern (#1758 F3; a non-assertion-matching deleted line still pairs whole-file, tolerating a moved/reindented line anywhere in the file); (b) a test file is deleted (including renamed away, since the check reads `--no-renames`); (c) a single file's diff goes net-negative in lines, regardless of pairing; (d) an ADDED line matches a test-neutering pattern, independent of what was deleted (#1758 F1) — criteria (a)-(c) are all deletion-triggered and would otherwise never see a test disabled by pure addition (e.g. a `Skip = "..."` inserted into an already-parenthesized multi-line `[Fact(...)]`, or an assertion wrapped in `#if false ... #endif`). Mixed engine+test PRs touching `src/` are exempt from all four. Narrowed by #1758 (operator ruling, 2026-09-03) from #1603's original "any deleted or changed line in a test file" after that wider rule false-positived on net-additive helper/fixture refactors (#1757's shape: a private method's signature changed, no assertion touched); `tools/diff-shape/diff_shape.py`'s `_ASSERTION_PATTERNS`/`_MJS_ASSERTION_PATTERNS`/`_PY_ASSERTION_PATTERNS`/`_THROW_PATTERN` (criteria a/b/c's patterns) and `_NEUTERING_PATTERNS`/`_MJS_NEUTERING_PATTERNS`/`_PY_NEUTERING_PATTERNS` (criterion d's patterns) are the sole enumeration of the pattern lists, not restated here. `Should`/`Expect(` were dropped from the universal assertion table by #1758 F4 (a repo-wide grep of `tests/` and `tools/` found no real usage of either as an assertion idiom); conversely no `.mjs` or `.py` file exists anywhere under a `tests/` directory in this repo today, so both the `.mjs`/`.py` assertion tables and the `.mjs`/`.py` neutering tables are forward-looking, exercised only by the selftest's synthetic arms rather than by real content. **Accepted gap (#1758):** an added bare `return;` as the first statement of a `[Fact]`/`[Theory]` method's body — disabling the test without touching its attribute, an assertion, or matching any criterion-(d) pattern — is not detected. Catching it needs tracking which lines precede a changed line (this gate reads `git diff -U0`, zero context, precisely so unrelated unchanged lines never enter the deleted/added sets), which was judged not worth building alongside the rest of this narrowing; a `[Fact(Skip = ...)]` addition, an `#if false` wrap, or an outright attribute/assertion deletion — the more common real-world neutering shapes — all still catch. **Criterion (d)'s trip surface, fixed round 2 (#1758, operator ruling, 2026-09-03):** a brand-new `[Fact(Skip = ...)]` method added to an existing, otherwise-unchanged test file trips (d) the same as an existing test being newly skipped — every skip addition gets a human look, deliberately not distinguished from the file's other status — and `Assert\.Skip\(` fires on the call form while `Assert.SkipUnless(...)`/`Assert.SkipWhen(...)` (xUnit v3's sanctioned conditional-skip API, already used across this repo's test suite) do not; a bare `.Skip(` entry was tried and dropped after it false-positived on this repo's own LINQ `Enumerable.Skip(n)` idiom (`ChannelPopulationTests`, `ConcurrencySlotGateTests`, `ModelAndEffortValidationTests`, `AgyWorkerAdapterTests`), which needs no denylist entry of its own since `Skip\s*=` and `Assert\.Skip\(` already cover the real neutering shapes.
2. **Protected tooling edit:** any file under the protected-tooling set is edited (additions included). Record-once: `tools/diff-shape/diff_shape.py`'s `PROTECTED_TOOLING_PATHS` tuple is the sole enumeration of the whole-file/directory half, and `PIXI_PROTECTED_TASK_RULE` the sole enumeration of the pixi.toml task-name patterns below — this paragraph states the rule, never the list. Widened from #1603's original four-member set by #1744 (ruled 2026-09-03), then corrected by #1754: #1744's ruling had excluded `tools/tool-refresh/`, `tools/fleet-glass/`, and `tools/baton-agy-loop/` as "not enforcement", which `tools/gates/gates.py`'s own `OVERLAP`/`AFTER_BUILD_FAST` membership contradicted — each hosts a gates-wired selftest body (plus `tests/Launcher.Tests.ps1`, missed from #1744's candidate list entirely, and `vendor-check`'s actual body under `tools/Baton.VendorProbe/`). #1754 protects those specific files rather than the whole directories, so a genuinely unwired sibling in the same directory (e.g. `tools/fleet-glass/pusher.py`, deliberately UNWIRED from `pixi run gates` per that task's own pixi.toml comment) stays unprotected. `pixi.toml` is protected at LINE level, not whole-file (#1744 narrowing of #1603's original whole-file rule): a hunk trips the gate only when it touches a `gates*`, `gate-sabotage`, `diff-shape*`, `audit-*`, `*-selftest`, `vendor-check`, `vendor-verify`, `lint`, `fmt-check`, or `test-no-build` task's own definition, parsed by `[tasks]` key/sub-table boundaries rather than fixed line numbers — an ordinary pixi task addition or edit elsewhere in the file passes. `.githooks/` is deliberately excluded — ruled local convenience, not enforcement (#1744).

Both failures are lifted by the `operator-merge` PR label, applied by the operator. Self-application — a conductor or worker adding the label to its own PR — is a forbidden act; the mechanism does not prevent it (both PR author and label-applier can be the same shared operator credential), but it is permanently visible in PR history, which is the property the design relies on instead of a technical block.

### C-16 — Every CI job carries `timeout-minutes` (#1743)

A stalled runner (not a code failure) held release PR #1652's `windows-shard-other` job `in_progress` for two hours against GitHub's six-hour default. Every job in `.github/workflows/ci.yml`, `diff-shape.yml`, and `pr-body-lint.yml` now sets `timeout-minutes` to roughly twice its observed maximum duration, rounded up (`flake-watch.yml`'s job already carried one, sized on its own reasoning); the ceilings themselves live in those workflow files, not restated here.

### C-17 — `dotnet test` runs on Microsoft.Testing.Platform; every leg asserts a nonzero test count (#1833, closes #984)

`dotnet test` moved off VSTest onto Microsoft.Testing.Platform (MTP) behind a root `global.json`
(`"test": {"runner": "Microsoft.Testing.Platform"}`), taking `xunit.v3` and `xunit.runner.
visualstudio` to `4.0.0` across all six test projects in the same change. This closes #984 at its
root cause rather than mitigating it: the "Test process did not return valid JSON" flake was
xunit v3 3.2.2's own in-process console runner printing a foreground-thread shutdown warning onto
the same stdout stream its `GetAssemblyInfo` IPC handshake parses as JSON (upstream
xunit/xunit#3576), fixed only in 4.0.0 — the runner that printed it no longer exists in this tree.

**The zero-test-leg rule.** `tools/buildlock.py`'s docstring names a zero-test leg (a filter or a
broken build silently running nothing, reported as green) as a known silent-failure mode. Every
`dotnet test` invocation in `pixi.toml`'s test tasks now carries `--minimum-expected-tests 1`, MTP's
own floor: a leg that runs zero tests exits 9 (a hard failure) instead of 0. This replaced a
different problem MTP introduced by being *stricter* than VSTest, not looser: a `--filter`-based
solution-wide run where one assembly in the set matches zero tests now exits 8 for the **whole**
invocation even when every other assembly ran cleanly — MEASURED breaking the pre-#1833
`test-flow`/`test-other` shards, which relied on exactly the opposite (VSTest-era) tolerance. The
fix was not a switch to suppress that: `--minimum-expected-tests` does not supersede a per-module
exit 8 inside a multi-module run (MEASURED), and xUnit v3 does not accept `--filter`'s VSTest syntax
under MTP at all (needs `--filter-namespace` et al. instead, still per-module-zero-exposed the same
way). `test-flow`/`test-other` invoke their member projects directly by `--project` instead — no
filter, so no assembly can ever be a zero-match; `pixi.toml` owns the exact project lists.

Both are `pixi.toml`'s member facts, not restated further here; `-m:1`'s replacement is C-13's own
amendment, not this entry's.

**Coverage exclusions (#1846, amended in place).** `pixi run coverage` moved to MTP's
`Microsoft.Testing.Extensions.CodeCoverage`, dropping the prior `coverage.runsettings`'s
attribute-based exclusion of source-generated code — a gap #1843 shipped rather than blocking on.
#1846 restored it: `--coverage-settings coverage.settings.xml` (a schema-conformant
`<Configuration><CodeCoverage><Attributes><Exclude>` file at the repo root) MEASURED passing
command-line validation against Microsoft.Testing.Extensions.CodeCoverage 18.11.0 on SDK 10.0.204
and excluding `GeneratedCodeAttribute`/`CompilerGeneratedAttribute`/
`ExcludeFromCodeCoverageAttribute`-marked types from the cobertura report. In the measured control,
the generated `QuotaResetDurationRegex_0.RunnerFactory.Runner` contributed 197 coverable lines;
with the settings file, that type and every `RegexGenerator.g.cs` class were absent.

---

## §12 Memory: vendor memory roots and Baton's canonical store (#1852)

**Not the room-memory sense of the word.** §2 and §5's "memory" is a room's own fact files and the
`MemoryProposalTool` that edits them (`RoomMemoryDocument`, `MemoryProposalApplier`) — per room, inside
a room's directory, and untouched by anything below. This section's referent is different and
unrelated: the **vendor memory roots** a coding agent keeps per machine (`~/.claude/projects/*/memory`
and `~/.claude/memory-archive/*/*`), and the **canonical store** Baton is consolidating them into. The
two senses share a noun and nothing else; where one file names both — the allowlist in
`ExecutionOutputDirectoryListingTests` now does — each entry says which it means.

**The authority model is ratified on #1852 (operator, 2026-09-04) and is not restated here** — read
the issue for it, and for the phased plan and the Q1–Q5 rulings of 2026-09-05. What this section
owns is the part that is now code: phase A's verb and the reading it produces.

The one thing worth stating in the register, because every phase below rests on it: **the canonical
store is keyed by `RepositoryIdentity`, not by a checkout path.** That is the same key §7's cost
ledger already files under, reused rather than re-derived — see the "Canonical repository identity"
paragraph there for the derivation and the worktree-convergence property it buys.

**The store's layout is ruled and unbuilt.** Q3 (operator, 2026-09-05) settled per-repository
directories with the memory store **inside** each — `~/.baton/<repo-slug>/memory/…`, explicitly *not*
`~/.baton/memory/<repo-slug>` — because the repository directory is the unit the operator intends to
work in. Phase B builds it; **phase A writes no canonical store at all**, so nothing under `~/.baton`
has this shape today. The same ruling carries a follow-up for the cost ledger's own path (§7) — moving
under the same per-repo root, with a reader that accepts both during the transition — which is a later
phase's work, not a correction to what §7 states now.

**`baton memory audit [--format text|json]` — phase A, shipped.** Read-only, and read-only *by
construction* rather than by flag: nothing on the path opens a file for writing, so there is
deliberately **no `--dry-run`** (a flag with no off position is noise, and offering one would imply a
writing mode that does not exist). It reports counts, paths, sizes, mtimes and SHA-256 digests. **It
never reads what a memory file says** — bytes enter a digest and are discarded — and it never edits,
moves or deletes one.

**Population: both halves.** Every live root at `{claude-home}/projects/<encoded-path>/memory`, and
every archived root at `{claude-home}/memory-archive/<label>/<name>`. The archive is not optional: a
live root can be empty precisely *because* an earlier undocumented migration drained it into one, and
an inventory of the live half alone would report such a machine as having no memory at all.

**Mapping a root to a repository is ordered, and reports rather than guesses.** A project directory's
name is a lossy flattening of the path it came from, so decoding it is not a function — several
checkouts encode identically, and some characters cannot be recovered at all. `MemoryRootPath`'s own
remarks enumerate which, with the live example. The ordering is what the register owns: session
transcript `cwd` (ground truth — the value the name was derived *from*) beats a decoded reading that
is a **work tree's own root** on this machine, which beats reporting `ambiguous` with candidates and
**no** selected path. `RepositoryIdentityResolver` then probes git at that checkout, and only when it
exists.

**Two constraints on that middle rung, because it is a guess being confirmed rather than a fact being
read** (#1908 review F1): a reading is only offered to disk when it is a **fully qualified** path —
a relative one would be resolved against whatever directory `audit` happened to run from, so the same
machine would answer differently per shell — and it only counts when it is the work tree's own root,
because git discovers a repository by walking **up** and would otherwise file a root under whatever
checkout a guessed subdirectory happened to sit inside. Both refusals degrade to `ambiguous`, which is
the direction that matters: a lost resolution, never an invented one.

**Five finding kinds**, defined on `MemoryFindingKind` and partitioned on `MemoryAuditReport`:
`duplicate`, `orphan`, `stale`, `no-provenance`, `ambiguous`. The ruling here is the one thing a
reader would otherwise assume wrongly: **not one of them decides anything.** Each names something
left open for the import to settle with the entries in hand. `stale` accordingly attaches to a whole
archived root rather than to matched filenames — the per-file question is answerable only from the
entries' text, and phase B's import is where it is answered.

**Provenance and subject are two facts, not one.** Where a memory file came from is observable;
whose memory it *is* is not, without reading it. #1852's `alpaca-agent-bot` root is the live case, and
Q1's ruling settles it at **import** — a write, therefore phase B's. What phase A does is refuse to
pre-empt that: both candidates on the row, neither selected. `MemorySubjectVocabulary` carries the
constraint that makes the read reproducible (its own remarks say why it is a fixed table and what
that costs).

**What phase A does not do**, so a reader's prior does not fill the gap: it inventories Claude roots
only (Codex and Antigravity — third-party surfaces whose formats nothing here has yet opened — are
phase A2's probe), it writes no canonical store, it projects nothing, and it deletes nothing anywhere,
ever.

**`~/.baton/codex-home` is not a vendor surface**, however much its `memories_1.sqlite` looks like
one. Q5 (operator, 2026-09-05) ruled it **Baton's own first beta** of this memory system, so it does
not belong with the third-party roots A2 probes: its memories are part of **phase B's import
population** and must be preserved into the canonical store. Phase A does not read it — a store keyed
by repository identity is the thing phase B builds, and importing into one that does not exist yet is
not a read.

---

## Appendix: full subsystem ruling table

One vocabulary note, so this table and §11 never diverge: code is **DELETED** or **NARROWED** —
git history is the archive; "ARCHIVE" as a distinct ruling applied to nothing and is not used here.

| Project / verb | Ruling | Note |
|---|---|---|
| `Baton` | **KEEP** | Engine core; vendor/UI-agnostic; untouched by this reset except that `room.jsonl`'s machinery (§2, §5) is now dead code from the harness surface's perspective — kept in place, not exercised. |
| `Baton.Vendors` (incl. `BuiltInWorkflowTemplates`) | **KEEP** | The cross-vendor seam; the template catalog narrows to built-in only. |
| `Baton.Cli` | **KEEP**, verb set narrows | `run`/`dispatch`/`decide`/`cancel`/`supply`/`resume`/`status` stay; `templates` narrows to the built-in catalog. |
| `Baton.Mcp` / `Baton.Mcp.Host` | **KEEP**, grows | `fleet_status` is the anchor and gains the §6 drill-down levels; `YieldTool`, `MemoryProposalTool` stay, orthogonal to this reset. `PermissionGateTool` and `PermissionReturnShape` — the ask machinery — are **DELETED** (#1417, §5); confirmed `PermissionReturnShape` had no other consumer in the tree. |
| `Baton.Daemon` | **NARROWED — done (#1420)** | Every REST/WS route, pairing, WebSocket broadcast, sidecar supervision, template-picker endpoints, and orchestrator reassignment are deleted; the permission REST answerer (`/api/rooms/permissions/answer`) and its `DoorbellMonitor`/`PendingGateRegistry`/crash-reconciliation plumbing were already **DELETED** (#1417). `Baton.RoomSession` (the room-reading path `RoomClient`/`MainWindowViewModel` were replaced with, #1412) is deleted too, #1420 — no caller of it survived once every route was gone. What remains is a bare hosted-service runner: mutex, settings load, fleet-wide concurrency-cap apply (`DaemonSettingsStore`/`ConcurrencySlotGate`), and `RoomRetentionSweep`. The room-watcher (serving `fleet_status`/the registry, §8), the snapshot push loop (§6), and the quota-runway ledger (§7) are unbuilt new work for a later PR, not something this narrowing preserved — homes stated in §7. |
| `Baton.Ui` | **DELETED** (#1412 Part 2) | Not a description of the existing Avalonia app with features removed — a full archive, then deletion. Fleet Glass (§6) is the diagnostic surface, built as MCP-tool levels, never a UI app. |
| `Baton.Ui.Core` | **DELETED** (#1412 Part 2) | `RoomClient` and `MainWindowViewModel` were named explicitly here because `Baton.Daemon`'s PORT row above depended on both and the narrowing had to break that dependency, not carry it forward silently — resolved by extracting the salvageable read-model surface into `Baton.RoomSession` (#1412 Part 1) before deleting the rest. The bulk (`ChatViewModel`, `RoomsViewModel`, `RemoteViewModel`, `TemplateEditorViewModel`, `StandingPermissionsViewModel`) was UI-surface logic for the retired product and is gone with it. `RoomProjection.cs`, `RoomFilesProjector.cs`/`RoomFilesViewModels.cs`, and `ExecutionHistoryProjector.cs`'s equivalents lived on in `Baton.RoomSession` — itself deleted in full, #1420, once `RoomClient` and every daemon route were gone and nothing called them. |
| `Baton.Mobile` | **DELETED** (#1407) | No harness-driven use case; deleted along with its dedicated build machinery (CI job, pixi tasks, scripts) rather than left archived. |
| `Baton.Sidecar` | **DELETED** — done (#1420) | The tracked Go module and `Baton.Daemon.csproj`'s optional binary copy step both went. Remote dispatch is closed, orchestrator-only (§10); no resurrection case remains. (An earlier draft claimed the project was absent from the tree; corrected — it existed and was deleted deliberately.) |
| `Baton.Workers.Dialogue` | **DELETED** (#1408) | Vendor-neutral multi-model machinery that served the retired interactive/chat product; no harness-facing use case survives this reset. |
| `Baton.CrashTestHost`, `Baton.Architecture.Tests` | **KEEP** | The gate mechanisms stay untouched. |
| `Baton.Journeys.Tests`, `Baton.Plan.Tests` | **DELETED** (by this spec's own landing PR) | Both existed solely to cross-check `docs/plan.md` and `spec/journeys.md`, deleted with them; harness-facing journeys are future work that brings its own checks when it exists. |
| `docs/design/*` | **DELETE** | Per §11 — not archived, deleted. Its methodology (settle definition before screens) is worth reusing as a technique; its content does not survive and there is nowhere left for it to live. |

---

## Uncertain

Claims I could not verify by reading the tree, or that rest on something outside this session's
reach:

- **The exact shape of the outbound push mailbox (§6).** Unbuilt; I could not verify anything about
  its intended transport beyond "quota data rides it" and "gate-pending visibility rides it," both
  stated as rulings rather than measured facts.
- **Resolved by #1570.** The room registry's (§8) registration mechanism and the quota burn ledger's
  (§7) do share an implementation — `MutexGuardedFileLock`, §7's own burn-ledger subsection names it.
  Left here rather than deleted so this appendix stays an honest record of what this document's
  original author could not verify at the time, not a claim about the tree today.
- **Whether `Baton`/`Baton.Vendors` have silently accreted a human-watching assumption anywhere
  outside the paths this document cites directly** (terminal sentinel, status projection, hook
  enforcement, `FailureClassification`, `PermissionGrant`). I did not do a full pass of scheduling
  code; `Baton.Architecture.Tests` is the stated defense and I did not verify its actual coverage.
- **`YieldTool`/`MemoryProposalTool` in `Baton.Mcp.Host`.** I confirmed they exist and are distinct from
  the archived `PermissionGateTool`/`PermissionReturnShape`, but did not read their implementations —
  the Appendix's "orthogonal to this reset" call is a structural inference (they are not part of the
  ask machinery, the daemon, or the UI), not a read-through verification of their own content.

---

## Naming note

The product converged on **Baton everywhere** (#1458): the CLI binary is `baton`, namespaces are `Baton.*`, state lives at `~/.baton`, and the tree is the one-binary, five-project shape this document describes throughout (`src/Baton` engine — including the managed process-execution core since #1474 — `src/Baton.Vendors`, `src/Baton.Cli` with `baton mcp` and `baton daemon` as verbs, two test projects). Every `Baton.*`/`baton` citation in this document refers to the current tree.

