# Invoking Baton against another repo

For the **cold invoking agent**: you have been told to run a Baton lane over some repository, you
have no prior session context, and your job is to get one worker to produce one file. This page is
the working invocation and the edges around it, as they actually are today.

It is **not** for developing Baton — that is [`CLAUDE.md`](../../CLAUDE.md) — and it is not the
reference for `baton dispatch`, which is [`docs/dispatch.md`](../dispatch.md). Where those own a fact,
this links rather than restates.

This assumes `baton` is already installed on PATH. If `baton dispatch`/`baton status` print a
`WARN: installed baton ... is behind this checkout's ...` line, the installed tool has drifted from
the repo it is dispatching against (#1645) — refresh it with `pixi run tool-refresh` (README's
*Installing `baton`* section) before trusting anything below.

If instead a dispatch, redispatch or resume **refuses to start** citing an operator drain marker,
that indicates an operator-invoked stop. The exit code is `2` (`ValidationRefused`), the same one a
malformed invocation gets, so branch on the message rather than the code alone. For `dispatch`/`redispatch`
the room directory is created to hold a `terminal.json` recording the refusal, while `resume` leaves the
room untouched. An operator can clear a manual marker with `pixi run tool-refresh --abort`. The full
specification for tool installation, launcher resolution, and drain markers is in [`spec/baton.md`](../../spec/baton.md) §8.

Everything below is the state of the tree on the day it was written. Dispatch ergonomics
([#1354](https://github.com/aer-works/baton/issues/1354)), the machine completion contract
([#1356](https://github.com/aer-works/baton/issues/1356)), and validation errors carrying a
corrected-invocation `Try:` line ([#1357](https://github.com/aer-works/baton/issues/1357)) have all
landed — §3, §5, and §6 below describe what they actually do rather than what they were tracked to
add.

---

## 1. The one invocation that works today

```
baton run <workflow-file> --bindings <bindings-file> --room-dir <fresh-dir> --echo-worker
```

Two files you author, one directory you name. `--echo-worker` streams the worker's stdout so you can
see it is alive; drop it and you see nothing until the run settles.

`baton dispatch` is the intended front door and needs no JSON from you. Read §6 before choosing it —
an audited role/adapter pair now auto-provisions its own worktree rather than refusing, which is a
real consequence (uncommitted changes become invisible to the worker), not a formality. `baton run`
remains the path that works uniformly, including for a composed template's audited phase, which §6
still refuses at bind time.

**The first argument is a file path.** `baton templates` lists template *ids*, and `baton run` does not
resolve them — it opens the argument as a file and fails with `Template file '<name>' does not
exist.` That the two are different namespaces now shows up in the error itself, as a `Try:` line:
`'baton run' takes a workflow FILE; built-in templates are used via 'baton dispatch <role>'`
([#1357](https://github.com/aer-works/baton/issues/1357)).

---

## 2. A complete minimal pair — one review step, agy, no network, explicit output path

Both files below are derived from this repo's own live-vendor smoke fixtures, which are the only
worker-binding JSON in the tree that a real vendor run has ever accepted:

- shape of the single-step workflow, and of a `PermissionGrant` entry —
  [`tests/Baton.Cli.SmokeTests/Fixtures/readonly-reviewer-workflow.json`](../../tests/Baton.Cli.SmokeTests/Fixtures/readonly-reviewer-workflow.json)
  and [`readonly-reviewer-bindings.json`](../../tests/Baton.Cli.SmokeTests/Fixtures/readonly-reviewer-bindings.json)
- an `agy` binding entry — [`draft-review-paused-bindings.json`](../../tests/Baton.Cli.SmokeTests/Fixtures/draft-review-paused-bindings.json)
- the authoritative field list, defaults, and what each field means —
  [`src/Baton.Vendors/WorkerBindingConfigEntry.cs`](../../src/Baton.Vendors/WorkerBindingConfigEntry.cs)

### `review-workflow.json`

```json
{
  "WorkflowTemplateId": "repo-review",
  "WorkflowTemplateVersion": 1,
  "Steps": [
    {
      "StepId": "review",
      "Worker": "reviewer",
      "Inputs": [],
      "Outputs": ["report.md"],
      "DependsOn": [],
      "RetryPolicy": { "MaxAttempts": 1 }
    }
  ]
}
```

### `review-bindings.json`

```json
{
  "reviewer": {
    "Adapter": "agy",
    "Model": "gemini-3.6-flash-low",
    "Timeout": "00:25:00",
    "WorkingDirectory": "C:\\absolute\\path\\to\\the\\repo\\under\\review",
    "Contract": {
      "WorkerName": "reviewer",
      "RequiredInputs": [],
      "ProducedOutputs": [{ "Name": "report.md" }],
      "OptionalMetadata": []
    },
    "PromptTemplate": "Review the repository you have been given for <the specific claim>. Cite file:line evidence for every finding.",
    "PermissionGrant": {
      "ReadFiles": true,
      "WriteFiles": true,
      "RunShellCommands": false,
      "ShellCommandPatterns": [],
      "NetworkAccess": false
    }
  }
}
```

Then:

```
baton run review-workflow.json --bindings review-bindings.json --room-dir /tmp/review-001 --echo-worker
```

Four couplings hold this together, and getting any of them wrong is a run you pay for and throw away:

| This | must equal | that |
|---|---|---|
| the top-level key in bindings (`"reviewer"`) | | the step's `Worker` |
| `Contract.WorkerName` | | the same worker name |
| every `Contract.ProducedOutputs[].Name` | | the step's `Outputs` |
| every `Contract.RequiredInputs` entry | | an upstream step's declared output |

`WorkingDirectory` must be an **absolute** path (or a bare name registered in this machine's profile
mapping — see the field's own docs in `WorkerBindingConfigEntry.cs`). On `agy` this is what the
worker can actually see: `agy -p` ignores the process working directory, so the adapter passes the
directory explicitly, and a wrong value produces a confident review of the wrong tree rather than an
error. On Windows, double every backslash — it is a JSON string, and a lone `C:\Users\…` is rejected
as an invalid escape before anything else is checked.

**A `WorkingDirectory` also needs a recorded project ceiling, or `baton run` above refuses before
anything spawns** (#1166, spec/baton.md §9's project scope: `ProjectNotTrustedException`,
`ValidationRefused`). Run `baton trust /absolute/path/to/the/repo/under/review --ceiling
ReadFiles,WriteFiles` once per machine per project before dispatching against it — the categories
must be a superset of whatever the binding's own `PermissionGrant` asks for, since the effective
grant is the intersection of the two, never wider than either. `baton trust <path> --ceiling all`
trusts a project without narrowing anything; `baton trust --list` shows every project this machine
has a recorded ceiling for, and `baton trust <path> --revoke` undoes one. A binding with no
`WorkingDirectory` at all is unaffected — there is no project scope to enforce.

### Why this is the read-lane profile

`NetworkAccess: false` is not a hardening choice you could relax — on `agy` it is the only
grant shape that resolves. That vendor's only auto-approve flag is all-or-nothing
(`--dangerously-skip-permissions`), so the adapter refuses a grant asking for network *or* shell
without the other rather than silently over-granting the one you did not request
(`AgyWorkerAdapter.TryTranslatePermissionGrant`). A reviewer does not need the network: its
deliverable is a file at a path AER hands it, not something it fetches.

`WriteFiles: true` in a *read* lane looks wrong and is not. On `agy`, a withheld write does not
reach the outbox — the worker simply cannot produce its report, and you get a paid-for run that
fails the contract. The grant above resolves to `--mode accept-edits`, and the adapter then seeds a
least-privilege `write_file($BATON_OUTPUT_DIR/report.md)` allow rule — one per declared output — into
the AER-owned home it runs that worker under. Writes are still bounded by AER's own `PreToolUse`
hook; the grant is not the boundary.

---

## 3. Where the output lands, and how you find it

At settle, `baton run` prints one line per produced output of each succeeded step:

```
Workflow status: Terminal
  review: Succeeded
  report.md -> <room-dir>\artifacts\execution_<id>\report.md
```

**Read that line rather than reconstructing the path.** The `execution_<id>` segment is allocated per
execution, so a retry writes to a different directory and the previous one is still on disk.

That prose is for a person watching. For a machine caller (#1356), the same information is
available two other ways, and both give you the same set of paths without parsing a sentence:

- **`baton status <room-dir> --json`** — one JSON object to stdout, nothing else:
  <!-- record-once-ok: #1359 src/Baton/Status/WorkflowStatusView.cs -->
  `{state, steps:[{id, state, execution, linkedFrom, usage, linkedFromUsage, liveness?}], outputs:[...], error, try, rejected}`
  — full schema, including `liveness`/`rejected`'s exact semantics, at spec/baton.md §3. `outputs` is the
  flat list of absolute paths every succeeded step's declared outputs resolved to — the same paths
  the human line above prints, derived from the same read. Works on a running room too
  (`state: "Running"`), not only a settled one. `try` (#1357) is the same corrected-invocation text a
  validation refusal's `Try:` stderr line carries, kept as its own field rather than folded into
  `error` — `null` when the refusal had none. Only ever populated on a pre-ledger `Failed` room (§5's
  exit-code-2 case); a settled or running room's ledger projection has no exception to carry one.
  `linkedFrom` (#1359) names the predecessor execution when the step's current one was started by
  `baton resume`; anything that was dispatched or retried normally shows `null` there. `rejected`
  (#1377, widened by #1622) is `true` when a human `baton decide reject` or a non-accepting
  `baton resolve --reject`/`--close` settled some step, so `state: "Failed"`/`error: null` never gets
  misread as an unrecorded crash — full rule at spec/baton.md §3. `liveness` (#1375/#1513) is present on a step reading
  `"Running"`, or a `"Failed"` step still carrying a pending `RetryNotBefore` — `"alive" | "dead" |
  "unknown"` from the same probe the human `baton status` line already uses — so a SIGKILLed `baton
  run` stops reading as indefinitely `"Running"` (or as an ordinary parked retry) to a polling agent.
- **`usage`/`linkedFromUsage` (#1360, extended by #1569)** cost per execution — the second field is
  the linked-from execution's own separate figure, present exactly when `linkedFrom` is. Canonical
  shape at spec/baton.md §3, not restated here. The clock figure lands the moment Core has recorded
  both ends of an execution's lifetime, no matter which vendor ran it. Every other field is a
  different kind of fact — pulled from whatever the vendor's own CLI put on stdout — so treat a
  missing key as "not reported for this run", never as zero: §4 spells out per-vendor which counts
  that actually is today, and it hinges on running in structured-output mode in the first place (a
  plain-text dispatch, which is most of them right now, carries none of them).
  **Narrower population than the human line** (#1360 F4, review): `--json` exposes only each step's
  current and linked-from executions, never a failed attempt a retry superseded or a step-less
  supplementary execution (§17.3) — those are in the human roll-up's total but have no home here. Sum
  `usage`/`linkedFromUsage` across steps for a machine-computed total that is a lower bound, not the
  room's full cost.
- **`<room-dir>/terminal.json`** — written once, the moment the workflow FIRST reaches a terminal
  state, in the identical shape `status --json` prints. Written *last*, after every output it could
  reference already exists on disk, specifically so you can watch this one file with a file monitor
  instead of polling `baton status` or babysitting the `baton run` process — the async
  task-notification parity the issue asked for. Its absence means "not terminal yet", not "never
  started"; see §5 for the one case where it is the *only* record a room has. **`baton resume` rewrites
  it** on that step's own settle, but does NOT invalidate it the moment the resume starts — a watcher
  polling this file sees the FIRST run's terminal state for the resume's whole duration and cannot
  tell the room is busy again from this file alone; check `baton status <room-dir>` (no `--json`) for
  that, or the exit code of the `baton resume` process itself.

The room directory also holds `snapshot.json` (the workflow this room is bound to), `flow.jsonl` (the
append-only event ledger), and `flow.lock`. The authoritative room layout is
[`spec/baton.md`](../../spec/baton.md) §2–§3.

**Don't hand-roll the file-watching the paragraph above describes — `baton watch` (#1488) is the
built-in version.** `baton watch <room-dir> --notify <command|url>` registers a one-shot notification
and returns immediately (never a poll loop of its own); once the room reaches Terminal, the command is
spawned (a small JSON object — `room, state, verdict, outputs, terminalAt`, not the full `terminal.json`
shape — on stdin and in `BATON_WATCH_EVENT`) or the URL is POSTed the same JSON, exactly once. An already-terminal room at registration fires right away — no lost wake-up. This is the
dispatch → `baton watch --notify <wake command>` → end-turn pattern: a harness ends its own turn right
after registering rather than blocking on `baton status --follow` or a hand-rolled sentinel-file/poll
loop, and gets woken back up by the notify command instead. **Firing after registration depends on
`baton daemon` running** — it is what actually polls pending watches; `baton watch` warns on stderr at
registration if it can't find one for the current user. `baton watch --list` /
`baton watch --clear-fired` are the visibility/cleanup pair — full contract, including what each
prints, is `spec/baton.md` §2, not restated here.

**A path in `outputs` IS the worker's own write (#1594/#1608, conductor-writes shape).** Baton never
writes into a declared output itself except through the one verb below. That step's own **room**
settles the top-level `state` `Indeterminate`, not `Failed`, whenever journal facts alone cannot
decide success vs. failure — a bare `baton redispatch` refuses an `Indeterminate` parent outright, so
read `state` before assuming an Indeterminate room is an ordinary retryable failure. `spec/baton.md`
§3's "Four producers" table is the register for what raises it and readable from
`status --json`/`terminal.json`; per-step `steps[].indeterminateProducer` names WHICH producer, and it
is the field to switch on, not `steps[].capturedResponseFile`'s presence — most Indeterminate steps
today carry no captured response at all (an exit-0 worker whose declared outputs are simply absent,
with nothing recoverable to capture), and driving `--accept-capture` off the wrong read throws. Only
the `CapturedResponse` producer names an engine-owned file (in the execution's own output directory,
never a declared name) on `steps[].capturedResponseFile`, alongside `steps[].unsatisfiedOutputs` naming
which declared outputs are still unwritten. `baton resolve <room-dir> [--execution <id>]
--accept-capture | --reject --reason <text> | --close --reason <text>` is the one resolution verb, and
which of its three verbs a step admits depends on `indeterminateProducer` — spec/baton.md §3's "Consumer
obligations" section (and its settle-shape table) is the full per-producer register, summarized without
restating it below: `CapturedResponse` admits either `--accept-capture` or `--reject`, and
`--accept-capture` writes the capture's body under each declared name it stands in for, settling the
step `Succeeded`; `ContractFailure` admits only `--reject --reason <text>`, recording a rejection and
leaving the step resolved-but-`Failed`; `VerifyFailed`/`ExecutionArrested` admit only
`--close --reason <text>` (see spec/baton.md §3 for why those two never admit the other verbs),
settling the step resolved-but-`Failed` through the identical room fact `--reject` uses. A `--reject`
is **terminal** since #1877 — it forecloses retry rather than leaving the step retry-eligible, so
redoing the work is a fresh `baton dispatch`/`baton redispatch`, never a re-run of the same room; a
step already rejected under the pre-#1877 rule and still dangling is closable with
`--close --reason <text>`, which is how an existing stuck room settles. Of
those two, `VerifyFailed` carries the failing member(s)' own
output on `steps[].verifyTail`, bounded — `spec/baton.md` §3 is the canonical account of the field
and its whole-stream fallback. See `docs/dispatch.md`'s "Roles" section for exactly which outputs a
capture can and can't ever resolve into. `baton resolve` never re-drives the DAG itself, either way —
in a multi-step lane, check its stdout / the returned `state` for whether the room reached Terminal; if
not (a downstream step just became deliverable — never a rejected step, which is terminal), re-run
`baton run --room-dir <room-dir>` — except on a room left `Paused`, where `baton decide` is the verb
that moves it and `baton run` cannot. `baton resolve` names whichever of the two applies on its own
stdout; follow that rather than the general rule (spec/baton.md §3).

**`Succeeded` does not by itself mean the engine's gate ran (#1702).** After a step exits 0 with its
outputs written, the engine runs that workspace's own verify command — but when the workspace does not
define one (a role's baked-in `pixi` task absent from a foreign workspace), the step still settles
`Succeeded`, and the room still exits 0, with the gate never having fired. `steps[].verify` is where
that is said: it reads `"not-run"` exactly in that case and is **absent otherwise**, with
`steps[].verifyReason` naming what was missing. So exit 0 plus `verify: "not-run"` means "the worker's
own work looks clean and nothing checked it" — read the field before reporting a run as gated.
`spec/baton.md` §3 is the register for the resolution order, for how to declare a verify command for
your own workspace (`.baton/verify`, which must be **committed** to take effect), and for the
`--verify <cmd>` override.

**Giving a review lane real instruments instead of prose (`--verify-cmd`, #1882).** A `review`
worker's shell grant is a read-only `git`/`gh` allowlist, so every runtime claim it might make ("3765
passed", "selftest exit 0") otherwise reaches it as prose in a PR body. `baton dispatch review …
--verify-cmd "<command>"` — repeatable — makes the **engine** run those commands before the worker's
first turn, with no model involved. Nothing runs unless you pass the flag; a brief never triggers one.

```
baton dispatch review --spec brief.md --workspace <worktree> \
  --verify-cmd "dotnet build -warnaserror" \
  --verify-cmd "dotnet test" \
  --verify-timeout 10
```

(The second command's own arguments are yours — a narrowed `dotnet test` naming a filter and a
minimum expected test count is the usual shape; it is elided above only so this example's flags read
as `baton dispatch`'s own.)

- **A fixed set of command shapes.** A `dotnet build`; a `dotnet test`; or a `python` script that
  lives beneath `tools/`/`benchmarks/` *and* carries a `--check…`/`--selftest…` flag. Everything else,
  shell metacharacters included, is rejected before the room is created.
  `Mutation.VerifyStepCommandParser` is the grammar; spec/baton.md §9 says why it is drawn this way.
- Each runs sequentially, wrapped in `python tools/buildlock.py`, with the review workspace as its
  cwd and `--verify-timeout` minutes (default 10) of wall clock. A timeout kills the process tree and
  records no exit code. If your `--workspace` is not a Baton checkout — no `tools/buildlock.py` under
  it — the step is refused with that reason recorded and the review still runs.
- Results land in `<room>/artifacts/verify-results.md` — one section per command with the exact
  command line, exit code, wall clock and a 200-line output tail. **A failing command is evidence,
  not a stop signal** (spec/baton.md §9 has the rule; the review prompt states it too).
- `verdict.json` gains `instruments: [{command, exitCode, wallClockMs}]`, written by the engine from
  what actually ran, never by the model — so a verdict citing a number absent from the results file
  is a finding a second reader can raise. Additive and optional: absent without the flag, including
  when the worker wrote its own (the engine strips it).
- `steps[].usage.verifyStepMs` / `.verifyResultsBytes` carry the step's cost, on the room's first
  execution only.
- **Not `--verify`.** That flag overrides the *post-exit* verify command (a role's `verify_pixi_task`,
  e.g. `implement`'s) that decides whether a mutating execution settles. `--verify-cmd` runs *before*
  the worker and decides nothing; it is accepted on `review` only, and refused elsewhere.

Once a room is genuinely done with, `baton room delete <room-dir>` (or its batch form,
`baton rooms prune --terminal --yes`) actually removes it — the directory, its `room-registry.jsonl`
line(s), and (best-effort) a deliverables tombstone — refusing a non-terminal room unless `--force`;
`spec/baton.md` §8 has the full contract, including what it cannot reach.

**Delivering orchestrator deliverables (`baton deliver`).** A conductor or orchestrator delivering artifacts (such as its decision queue at the end of an unattended window) delivers them directly to the standing conductor room so they reach the Fleet Glass inbox:

```
baton deliver <file> [--title <text>] [--room <room-dir>]
```

`--room-dir` is also accepted as an alias for `--room`. This copies the file into `<room>/artifacts/conductor/` under a filename unique to the source path (recorded as `artifact_file` in the manifest, defaulting the room to `~/.baton/rooms/conductor/`) and records it in `manifest.jsonl`, which `pusher.py` forwards to the inbox with a `CONDUCTOR` chip. Re-delivering the same source path updates the file and replaces the existing inbox item in place.

**What a room cost (`baton ledger`).** After a room settles, its per-attempt accounting rows are
readable without opening any file:

```
baton ledger [<room-dir>] [--since <instant>] [--until <instant>] [--vendor <name>] [--model <id>]
             [--role <name>] [--outcome <token>] [--workflow <id>] [--pr <n>] [--issue <n>]
             [--source-kind <kind>] [--format text|json|csv] [--drill]
```

With a `<room-dir>` you get that room's attempts and its total; without one, the whole repository's.
`--format json` is the machine contract — one object `{query, vendors, total, rows?}`, whose field
names are the ledger record's own (`spec/baton.md` §7 has the schema; `rows` is present only with
`--drill`). The window filters on each attempt's `endedAt`, `--since` inclusive and `--until`
exclusive, and an attempt with no recorded `endedAt` is excluded and counted as `undatedExcluded`
rather than assumed into the window. Every dollar figure is a labelled **estimate** — never an
invoice, subscription spend, or a quota reading — and an attempt whose cost could not be estimated is
still counted as an attempt and reported under the reason it produced none, never as `0`. Pass
`--help` for the rest —
including the warning about this verb's `--rebuild` form, which maintains an entirely separate file
(`spec/baton.md` §7's burn ledger) and leaves these rows alone.

**What memory is on this machine (`baton memory audit`).** A read-only inventory of every Claude
memory root — the live `~/.claude/projects/<encoded-path>/memory` roots *and* the archived
`~/.claude/memory-archive/<label>/` ones — each mapped to a canonical repository identity:

```
baton memory audit [--format text|json] [--help]
```

It writes nothing, moves nothing and deletes nothing, which is why it has **no `--dry-run`**; it
reads a memory file's bytes only to digest them and never reports what one says. Findings are
`duplicate`, `orphan`, `stale`, `no-provenance` and `ambiguous`, and **none of them is a ruling** —
an `ambiguous` root prints both candidates and picks neither, because deciding whose memory it is
needs the entries' text. `--format json` is the machine contract: one object
`{claudeHome, roots, findings, counts}`. See `spec/baton.md` §12.

---

## 4. Adapter notes

### agy

| Grant | Resolves to |
|---|---|
| read + write, no shell, no network | `--mode accept-edits` |
| read only | `--mode plan` |
| neither | `--mode default` |
| shell **and** network together | `--dangerously-skip-permissions` |
| shell without network, or network without shell | **refused before dispatch**, with the reason |

Model and effort are separate fields (`Model`, `Effort`) and are separate axes from the adapter.
Two agy-specific traps:

- On agy, effort is also encoded in the model name's suffix. `Model: "gemini-3.6-flash-low"` plus
  `Effort: "high"` is refused up front — pass one, or make them agree.
- `Effort` accepts either a raw vendor value (`low`/`medium`/`high`) or a canonical effort word
  (`quick`/`standard`/`careful`/`exhaustive`); anything else is refused before the run starts rather
  than forwarded blind.

Model names are pinned per tier in
[`src/Baton.Vendors/WorkerTiers.json`](../../src/Baton.Vendors/WorkerTiers.json), and `agy models` is
what the repo's own audit checks those pins against. `gemini-3.6-flash-low` above is the value
`draft-review-paused-bindings.json` uses; take a current one from those two sources rather than from
this sentence.

**Usage (#1360, extended by #1569):** agy's structured-output mode reports token counts (including
cache-read and thinking breakdowns `status --json` now surfaces; spec/baton.md §3 has the canonical
field list), and separately a turn count —
[`docs/vendor-capabilities.md`](../vendor-capabilities.md#usage-cost-and-quota--the-asymmetry-that-matters-most)
is the register for the underlying vendor facts. One shape quirk this repo does not paper over: when
that report collapses the input/output split into a single combined figure, `status --json`'s
`tokensIn`/`tokensOut` both come back absent rather than guessing a direction for it.

### claude

`claude` is the other registered adapter and takes the same binding shape (see
`readonly-reviewer-bindings.json`, which is a claude entry). One difference matters when choosing:
on claude a **withheld** write still reaches the outbox, so `WriteFiles: false` there is a genuine
read-only lane that still produces its report. That asymmetry is why §6's table splits by adapter.

**Usage (#1360, extended by #1569):** claude's structured-output mode reports the same token/turn
shape agy does, plus a cache-read/cache-creation/thinking breakdown `status --json` now surfaces too
(spec/baton.md §3 has the canonical field list) — same register,
[`docs/vendor-capabilities.md`](../vendor-capabilities.md#usage-cost-and-quota--the-asymmetry-that-matters-most).
It additionally computes a per-turn dollar cost, which the additive shape still has no field for and
therefore does not surface. One more thing worth knowing before reading `tokensOut` (or its new
siblings) as a lane's whole cost: each is a top-level count that a worker's own subagent fan-out is
not folded into — see
[`docs/vendor-capabilities.md`](../vendor-capabilities.md#batons-usage-field-per-adapter-1360)
for the measured shortfall.

Both adapters spawn the vendor's own already-authenticated CLI. Baton never handles a credential, so
a lane only runs on a vendor that is already logged in on this host — see the README's *Vendor
authentication* section.

---

## 5. Sharp edges

**A room directory is bound to one workflow, and re-running resumes it.** `baton run` against a
`--room-dir` that already holds a snapshot runs *that* workflow rather than the file you named, and
refuses outright if the two are different templates. Against an already-terminal room it reports the
prior run's status, writes nothing, and exits non-zero — which looks exactly like a fresh failure
except for the `Resumed the snapshot already bound in this room directory` line above it. **Use a
fresh directory for every new piece of work.** Omit `--room-dir` entirely and you get one derived
from the workflow file's name, in `./.baton/` — stable, therefore resuming, which is usually not what
an orchestrator wants.

**Never pass a relative `--room-dir`.** It is resolved to absolute at the CLI boundary now, but the
failure it caused is worth knowing: the worker is a different process with a different working
directory, so it resolved the relative output path against its own cwd and wrote the report where AER
never looked — reported as `Contract not satisfied`, after the run was paid for in full.

**`baton status` takes the room directory positionally**: `baton status <room-dir> [--follow]`. There is
no `--room-dir` flag on it, and passing one is an `Unknown option` error.

**Exit codes are a contract, and a dead room from provisioning is no longer indistinguishable from a
slow one (#1356, #1374).** `baton run`/`baton dispatch`/`baton resume` (#1359) return one of six codes.
**`baton resume` continues ONE worker** — it hands an already-dispatched step's vendor session your
follow-up message, reusing the workspace and grant that step already had, and the ledger gains a
fresh execution pointing back at its predecessor (`baton resume <room-dir> --worker <role>
--message <text> --bindings <file>`). Its exit code is still the WHOLE
ROOM's outcome, same table, not "did the resumed step itself succeed" — if some other step had
already Failed, even a perfectly good resume exits 1; read the resumed step's own status via
`baton status --json`'s `steps[].state`/`linkedFrom` for that:

| Code | Meaning |
|---|---|
| 0 | `Succeeded` — every step Succeeded. **Not the same as "the gates passed" (#1702).** A step can succeed with the engine's own verify command never having run — see `steps[].verify` below |
| 1 | `Failed` — a step ran and failed for an ordinary reason (also the bucket a still-Running or still-Paused process falls into if it returns short of Terminal, e.g. no `--wait`) |
| 2 | `ValidationRefused` — refused **before anything was dispatched**. Two causes: bindings/workflow validation or an unresolvable worker binding (bad adapter name, an incoherent grant, an unprovisioned worktree an `AuditedNotEnforced` grant needed, a project directory with no recorded `baton trust` ceiling — #1166, spec/baton.md §9), typically against a room with no ledger yet; or (#1608) a stale `terminal.json` from a prior attempt that could not be deleted — that one fires against a ledgered room too, and its message names the locked file, so read the message before assuming the bindings are at fault |
| 3 | `Timeout` — the step(s) that failed did so because a dispatch hit its binding's `Timeout`, not because the worker ran and failed on its own; or (#1378) `baton run --wait --wait-timeout <minutes>` hit that bound before the room reached Terminal — the room itself is still Paused/Running in that case, check `baton status`. **Narrower since #1373:** a dispatch timeout whose workspace carries work exits **1**, because such a room settles `Indeterminate` rather than `Failed` — this code now means a timeout with nothing to salvage. Read `state`, not the exit code, to tell them apart |
| 4 | `Cancelled` — the workflow settled via cancellation, not failure |
| 5 | `RoomHeld` — another Flow instance already holds this room (a live pump, or a background component's brief lock). Not a terminal outcome and not written to `terminal.json`: the room may be perfectly healthy, so nothing here overwrites its real state. Retry later, or check `baton status`/the sentinel for what the room actually is |

A room whose provisioning fails before `flow.jsonl` ever exists — the GrantAuditMode case above is
one way to reach this, a malformed bindings/workflow file is another — no longer sits at "Running /
no ledger yet" forever: it is left in a queryable `Failed` state (`baton status`, or the
`terminal.json` sentinel §3 describes, which such a room gets even though it has no ledger at all)
that names why, and the process that hit it exits 2. **That queryable-`Failed` treatment is reserved
for a genuinely pre-ledger room** (#1374): a later invocation that fails against a room whose
`flow.jsonl` already exists — a re-run with a typo'd `--bindings` against an already-completed room,
say — still exits 2 for that invocation, but leaves the room's own ledger/sentinel untouched rather
than overwriting a real terminal record with a fabricated one.

**`--wait` on `baton run`** only matters at a pause point — its full contract is
[`RunOptions.Wait`](../../src/Baton.Cli/RunOptions.cs)'s own doc comment; in short, omitting it hands
control back to you the moment a workflow pauses (as today, leaving `baton decide` to carry it
forward later), while passing it keeps that same invocation attached, watching the room until the
pause is resolved from elsewhere and the workflow settles, or you interrupt it. One thing it does
not cover: an `baton run` that already crashed in an earlier invocation is not something a later
`--wait` call reattaches to — **that gap (crash-orphaning) is still open.** For a room you did not
start yourself, the only completion signals stay the process's own exit or the `terminal.json`
sentinel §3 describes. **Do not background an `baton run` and poll `baton status` for a state word —
wait on the process, or watch `terminal.json`.**

**`--wait-timeout <minutes>` (#1378)** bounds how long `--wait` is willing to sit on an undecided
pause — without `--wait` the flag is accepted but does nothing. Without it, `--wait`
still waits forever for a separate `baton decide` (from anywhere — another process, another
operator); a lane with nobody watching for that decision hangs the invocation indefinitely. When the
bound elapses first, the call stops waiting and exits 3 (`Timeout`, above) rather than 1 — the room
itself is untouched (still Paused, no sentinel written), so a later `baton decide` against the same
room still works normally; only this particular `--wait` call gave up on it.

**Budget the wall clock in minutes, not seconds.** A repo-scale agy review ran roughly 3–5 minutes in
the 2026-08-26 session that prompted [#1358](https://github.com/aer-works/baton/issues/1358) — one
observation, an order of magnitude rather than a measurement. What is exact is the ceiling: the
binding's `Timeout` field, which the example above sets to 25 minutes to match what the `review` role
declares in [`src/Baton.Vendors/WorkerRoles.json`](../../src/Baton.Vendors/WorkerRoles.json). A timeout
shorter than the work kills a run you have already paid for.

**Most validation/refusal errors now carry a `Try:` line naming a corrected invocation**, printed
directly under the error and echoed on the pre-ledger `terminal.json`/`status --json` sentinel's
`try` field (§3) — [#1357](https://github.com/aer-works/baton/issues/1357). Two you are most likely
to meet are the template-file error in §1 and the worktree error in §6. Not every refusal gets one:
an unknown option or an extra positional argument has no way to infer what you meant, so those are
left without a suggestion rather than a guessed one.

---

## 6. Per-role dispatch, and which roles it completes for today

[`docs/dispatch.md`](../dispatch.md) is the reference for `baton dispatch` — its flags, the seven roles,
what each writes, and the three independent vendor/model/effort axes. Read it there. What follows is
only the part a cold agent needs before choosing that path: it does not complete for every
role/adapter pair.

```
baton dispatch <role> --spec <spec-file> --room-dir <fresh-dir> [--adapter agy|claude] [--workspace <dir>]
                    [--output <path>] [--override-runway "<reason>"]
```

**A dispatch can be refused for a short vendor runway** (#1848). When the vendor's own `/usage`
counters say its week (all models) window is at or above 85% or its session window at or above 90% —
or when those counters cannot be read at all — `baton dispatch` starts nothing and exits `2`
(`ValidationRefused`), printing the counters and the flag. Work already running is untouched, and
holds are per vendor: a held `claude` does not hold an `agy` dispatch. **"Cannot be read" includes
"never harvested"** — on a machine whose daemon has not harvested in the last six hours, a `claude` or
`agy` dispatch is held until it does, or until you override. The only bypass is
`--override-runway "<reason>"`, with a mandatory reason that is written to the room record and the
cost ledger. `baton dispatch --continue`, `baton redispatch`, and `baton resolve` are not gated —
they continue work the fleet already admitted; passing `--override-runway` with `--continue` is a
typed argument error rather than a no-op, since there is no gate there to override. Contract: `spec/baton.md` §7, "Runway hold (#1848)".

A quick read-only scoping question doesn't need a brief file at all: `--spec-text <text>` (or
`--spec -` to pipe the prompt in over stdin) is a drop-in alternative to `--spec <spec-file>` — same
room record, same lint, same everything downstream (#1518). One line, no file:

```
baton dispatch advise --spec-text "what does baton cancel actually do today?" --workspace <repo> --output <report>
```

**`--workspace <dir>` needs a recorded project ceiling too** (#1166, §2 above has the full contract) —
`baton trust <dir> --ceiling …` once, before the first `baton dispatch` against it. This holds even
for a role the table below auto-provisions a worktree for: the ceiling keys on `--workspace`'s own
value (the source repository), never the auto-provisioned worktree path, which is why the operator
never has to (and never could) trust a fresh directory `dispatch` only allocates after this refusal
would already have fired.

**Several manually-created worktrees of the same repository each need their own `baton trust`.** The
ceiling key is the literal `--workspace` directory, canonicalised (absolute, trailing separator
trimmed, case-insensitive) — there is no git-aware dereferencing to a shared `git-common-dir` or
`origin`, so trusting one checkout does not trust a sibling one dispatch never auto-provisioned.
That dereferencing was considered and not done: the operator's explicit act should name the path
they actually typed, not a repository identity `dispatch` infers on their behalf.

A role whose grant withholds writes, dispatched to an adapter whose withheld writes do **not** reach
the outbox, is bound as `AuditedNotEnforced` — which needs a provisioned git worktree, or
`WorkerBindingResolver` refuses it at bind time. `dispatch` now provisions that worktree itself,
automatically, for every such role/adapter pair (#1354/#1380) — no flag needed, and none exists to
suppress it. Today that is exactly the write-withholding roles on `agy`:

| Role | Writes | `--adapter claude` | `--adapter agy` |
|---|---|---|---|
| `advise` | `advice.md` | works | works |
| `implement` | `changes.md` | works | works |
| `janitor` | `janitor.md`, `branch.diff` | works | works |
| `review` | `report.md`, `verdict.json` | works | works — auto-provisioned worktree |
| `patch` | `patch.diff` | works | works — auto-provisioned worktree |
| `fact-check` | `findings.md` | works | works — auto-provisioned worktree |
| `orchestrate` | `turn-actions.json` | works | works — auto-provisioned worktree |

**`advise`'s "works" on `agy` is not the same shape as `review`'s.** Unlike the other read-shaped
roles, `advise` keeps an explicit `write_files: true` grant (pinned reason in `WorkerRoles.json`'s
`advise` entry), so it never enters the `AuditedNotEnforced`/auto-provisioned-worktree path this
table otherwise describes: on `agy` its write stays `Enforced` against your live `--workspace`
directory, not a disposable worktree. See `docs/dispatch.md`'s printed-grant-line section for what a
dispatch actually discloses before it runs.

**"Auto-provisioned worktree" is a real consequence, not a formality.** The worker is handed a fresh
worktree of `--workspace` (or the cwd) at `HEAD` — never the caller's own directory, whether or not
that directory already happens to be a worktree itself, because the post-run audit's whole premise is
that the tree started clean *because this run made it*. Concretely: **uncommitted and staged changes
in the workspace are invisible to the worker.** Dispatch discloses this before the run starts —

```
Workspace: worktree of <repo> at HEAD (<short-sha>) — uncommitted changes are not visible to the worker
```

— and the tree's eventual teardown follows the same kept-vs-removed rule as any other provisioned
worktree. See `docs/dispatch.md`'s `--workspace` row and its "auto-provisioned worktree" section for
what that rule is and where the disclosure comes from.

This still only reaches the composed **role** dispatch above — a template phase's audited grant (`baton
dispatch <template>`) is unchanged and refuses at bind time exactly as it always has;
`WorkflowTemplateComposer` deliberately does not auto-provision (see its own `autoProvisionWorktree:
false` call site for why). **Workaround for a template phase: use `baton run` with a hand-authored
pair**, the same way §2's example runs `review` on `agy` directly — it clears the refusal because it
asks for the write in the first place instead of having one flipped on for it, so `GrantAuditMode`
stays at its `Enforced` default. Hand-editing generated bindings to claim `IsWorktree: true` is still
not a workaround for that case: that field is a stamp the provisioner leaves, and a hand-authored
`true` claims an isolation that does not exist.

Cells marked *works* still require that vendor's CLI to be logged in on this host, and `--adapter`
without `--model`/`--effort` drops the role tier's vendor-specific model.

A role's declared outputs — the `Outputs`/`ProducedOutputs` pair you need for the `baton run` shape — are
in `WorkerRoles.json`, and the table above lists them. `--output <path>` copies the primary one out to
`<path>` once the room reaches Terminal; see `docs/dispatch.md` for its validation rules.
