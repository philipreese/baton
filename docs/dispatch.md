# `baton dispatch` — the front door for driving a worker

`baton dispatch` runs **one worker role, one-shot**, against a vendor CLI that is already logged in on
this host, and drops the result into a room directory. It is the operator-facing front door to the
same engine `baton run` drives — a role is materialised into a single-step workflow, dispatched, and
its declared outputs are contract-checked exactly as a full run's are.

It is **not** the chat surface. A dispatch turn is non-interactive and runs to completion once; the
interactive session (chat) is a different path with a different prompt and a continuing turn.

```
baton dispatch <role> [--spec <file> | --spec - | --spec-text <text>] [--room-dir <dir>] [--adapter <vendor>] [--model <m>] [--effort <e>]
                    [--workspace <dir>] [--workflow-id <label>] [--output <path>] [--timeout <minutes>]
                    [--token-budget <n>] [--max-tool-steps <n>] [--verify <cmd>] [--expect-pr <true|false>] [--continue <room-dir>] [--label <text>]
                    [--workstream <slug>] [--attach <file>]...

baton dispatch --list-capabilities
```

## Flags

| Flag | Meaning |
|------|---------|
| `--spec <file>` | The task prompt for the worker — the file whose contents become the spec. Mutually exclusive with `--spec -` and `--spec-text` (#1518): pass exactly one of the three. |
| `--spec -` | Read the task prompt from stdin instead of a file (#1518) — refused outright if stdin is a terminal rather than a pipe/redirect, so a fat-fingered invocation fails loud instead of hanging on EOF that never comes. |
| `--spec-text <text>` | The task prompt given inline (#1518) — for a scout question that does not warrant a brief file, e.g. `baton dispatch advise --spec-text "what does baton cancel do today?"`. All three spec sources resolve to the same string and produce the same room record (the spec/grant lint below still runs, `--attach` still works, and a plain `baton redispatch <room-dir>` of the resulting room still reuses its already-built prompt) — there is no separate on-disk spec artifact any of the three lands in. Inline and stdin specs are visible in shell history and in process listings while the command runs; use `--spec <file>` for anything sensitive. |
| `--room-dir <dir>` | Where the run is recorded (created if absent) — this is the room. Optional: omitted, each invocation gets a fresh unique one at `$BATON_HOME/rooms/dispatch-<role>-<8 hex>` (`BATON_HOME` defaults to `~/.baton`, see `BatonPaths`) — outside any workspace a dispatch might audit (#1354/#1380), and fresh each time because a dispatch is one-shot and a stable derived directory would make the second `baton dispatch review` *resume* the first's terminal snapshot instead of running. |
| `--adapter <vendor>` | Run the role on a specific vendor (`claude` / `agy` / `codex`) instead of its tier's default. The `--adapter` escape hatch; a role never names a vendor itself. |
| `--model <m>` | The model axis, independent of the role ([0017]/[0023]). Omitted keeps the tier's model — except on a vendor swap, where the tier's vendor-specific model is dropped for the new vendor's default (#1082). |
| `--effort <e>` | The effort axis, independent of the role. Omitted keeps the tier's effort; dropped on a vendor swap. |
| `--workspace <dir>` | The repository the worker's read access is scoped to. Defaults to the current directory. For a role whose grant is enforced as declared, this is literally the directory the worker runs in. For a role whose write grant is audited rather than enforced (a withheld-write role on a vendor whose withheld writes do not reach the outbox — today, the write-withholding roles on `agy`), dispatch instead auto-provisions a **fresh git worktree of this directory at `HEAD`** and hands the worker that (#1354/#1380) — the worker never sees uncommitted or staged changes in that case, only what HEAD already had. Bound explicitly because `agy -p` ignores the process working directory (#491). Needs a `baton trust` ceiling recorded against this exact path first, in both cases — see `docs/agents/invoking-baton.md` §2/§6 (#1166). |
| `--workflow-id <label>` | A label forwarded to the run; defaults to the materialised template id. |
| `--output <path>` | Copy the role's primary declared output to `<path>` once the run reaches Terminal, in addition to leaving it under the room's own `artifacts/`. Role dispatch only — refused up front on a template dispatch, the same way `--spec` is. `<path>`'s filename is validated before anything is printed or written: it must name a file (not end in a separator), must not start with `.` (the engine's reserved namespace), must not collide with the engine's own `prompt.txt` capture, and must not collide with another output the same role already declares. Delivered whenever the worker actually wrote it, regardless of what the engine's own verify step (below) decides (#1702). |
| `--verify <cmd>` | Override the engine's own verify command for this dispatch (#1702), ahead of the workspace's own `.baton/verify` declaration and the role's `verify_pixi_task` default — spec/baton.md §3 has the full resolution order and the not-run outcome. Role dispatch only — refused up front on a template dispatch, the same way `--timeout`/`--token-budget` are. |
| `--verify-cmd <cmd>` | Repeatable (#1882), **review role only**. Has the engine run `<cmd>` — with no model involved — *before* the reviewer's first turn, and write the exact command line, exit code, wall clock and a 200-line output tail to `<room>/artifacts/verify-results.md`, which the review prompt then points at. Allowlisted shapes only, refused at parse time naming the offending command: `dotnet build …`, `dotnet test …`, `python <script under tools/ or benchmarks/> --check…/--selftest…`; no shell metacharacters, since each command is spawned directly rather than through `cmd /c`. Runs sequentially in the review workspace, wrapped in `python tools/buildlock.py`. A non-zero exit does **not** abort the review. The commands are copied onto `verdict.json`'s `instruments` by the engine, never by the model. Refused for a non-verdict-producing role and for a template. **Not `--verify`**, which is the post-exit gate above — spec/baton.md §9 has the full contract. |
| `--verify-timeout <minutes>` | The wall-clock bound on **each** `--verify-cmd` (#1882); default 10, same 24h ceiling as `--timeout`. Exceeding it kills that command's process tree and records it with no exit code (a killed tree has none). Refused on its own, with no `--verify-cmd` to bound. |
| `--expect-pr <true\|false>` | For a role whose catalog entry sets `delivers_branch` (#1788, today only `implement`), whether the engine's post-exit delivery check also requires an open PR for the pushed branch, in addition to the branch itself being reachable from `origin/<branch>` — spec/baton.md §3, "Post-exit delivery check" has the full contract. Defaults to the role's own `delivers_branch` value; meaningless (never checked) for a role that does not deliver a branch at all. Role dispatch only — refused up front on a template dispatch, the same way `--verify` is. |
| `--timeout <minutes>` | Override the dispatched role's own catalog timeout for just this dispatch — a role that legitimately needs longer than its fixed tier timebox (an orchestrator coordinating sub-lanes, say) does not have to die mid-flight. Role dispatch only — refused up front on a template dispatch, the same way `--output` is: each phase carries its own role's timeout, so there is no single one to override. Must be a positive whole number of minutes; rejected outright above a 24h ceiling (a non-interactive dispatch has no confirmation prompt to gate a larger value behind); merely flagged on stderr above 2h. |
| `--token-budget <n>` | Override the dispatched role's own default per-execution token ceiling (#1623) for just this dispatch — measured incrementally from the same usage the vendor's own `stream-json` output reports mid-execution, not merely the terminal line. Crossing it arrests the execution (cancels it, mid-flight) and settles the step `Indeterminate` for a conductor to resolve — never a silent retry. Role dispatch only, same refusal as `--timeout` on a template. Must be a positive whole number of tokens; no ceiling (raising your own budget is not the runaway-consumption failure mode this exists to arrest). Per-role defaults are listed in `spec/baton.md` §3; every other role runs unwatched unless this flag is passed. |
| `--max-tool-steps <n>` | Override the dispatched role's own default cap on tool steps (#1686) for just this dispatch — the second arrest trigger alongside `--token-budget`, armed independently of it (#1682), and settling the same way: the execution is arrested mid-flight and the step goes `Indeterminate`. Role dispatch only, same refusal as `--timeout` on a template. Must be a positive whole number; per-role defaults are in `spec/baton.md` §3. |
| `--continue <room-dir>` | Rehire the worker that ran in this terminal room for a follow-on brief, resuming its vendor session instead of starting cold (#1381) — the general manual counterpart to #1373's automatic retry-with-continuation. Supported for same-adapter `claude` and `codex` rooms; agy rehire is gated on its own resume measurement. Refuses loudly rather than silently going cold when the named room does not exist, dispatched more than one worker, changes adapters, has no vendor session id recorded (an ordinary dispatch mints none automatically — see `spec/baton.md` §3), or has not yet reached a terminal state. Role dispatch only — refused up front on a template dispatch, the same way `--verify` is. Full contract in `spec/baton.md` §3. |
| `--label <text>` | Display text only, e.g. `"the #1496 env-snapshot lane"` — so Fleet Glass shows something legible instead of the bare `dispatch-<role>-<8 hex>` directory name. Never part of the room directory's own name. Trimmed, newline-folded, capped at `DispatchOptionsParser.MaxLabelLength` chars; full contract in `spec/baton.md` §2. |
| `--workstream <slug>` | A grouping key, not a title — unlike `--label`, IS later used as a Windows directory name (`~/.baton/by-workstream/<slug>`), so it is refused rather than truncated/folded when it fails the slug grammar, and lowercased on success. Persisted onto the room's `bindings.json` the same way `--label` is; full contract (grammar, the by-workstream junction, `baton redispatch`'s inheritance) in `spec/baton.md` §2. |
| `--attach <file>` | Repeatable (#1500). Copies `<file>` into the room's `artifacts/attachments/` directory before the worker starts, and appends one line to the prompt naming every attached file and that directory. Keeps a brief short instead of pasting context documents inline. Role dispatch only — refused up front on a template dispatch, the same way `--output`/`--timeout` are. Content is operator-supplied and **inbound**: it is never scanned and never published, because the mailbox pusher reads only `terminal.json`'s declared step outputs and an attachment is never one of them (not the deliverable secret gate withholding it — there is nothing for that gate to see in the first place). Each named file must exist; a missing one is a typed argument error before the room is created. |
| `--override-runway <reason>` | Dispatch anyway when the vendor's runway hold would refuse this dispatch (#1848) — the only bypass, and the reason is mandatory (a blank one is a parse error). Applies to a role **and** a template dispatch, unlike `--timeout`/`--output` above: both admit new vendor spend. The reason, the vendor, and the counters the gate read are written to the room's `bindings.json` under the `"RunwayOverride"` key (PascalCase, like every other field in that file: `"Vendor"`, `"Reason"`, `"Used"`, `"Counters"` of `{"Window", "PercentUsed"}`, `"HoldReason"`), and — when a hold was actually bypassed — onto the cost ledger row as `runwayOverrideReason` (#1849; that file's fields are camelCase). Passed when the gate would have admitted anyway, it is still recorded, as `"Used": false`. Refused outright alongside `--continue`, which consults no gate. It is also the bypass for the cross-dispatch reservation hold (#1896), which refuses a dispatch whose estimated burn would take the fleet past the headroom already claimed by admissions the vendor's last harvest cannot have seen. Full contract in `spec/baton.md` §7, "Runway hold (#1848)". |
| `--list-capabilities` | Prints every adapter's supported models and effort values, plus each catalog role's timebox default, and exits — no `<role>` or room required (#1500). Refused if combined with a `<name>`, rather than silently discarding the dispatch and exiting 0. `WorkerRoleCatalog.All` is the same catalog `ModelAndEffortValidationTests` reads directly. The role and effort sections can never drift from what dispatch actually accepts, but that is single-source construction — this printer and `ClaudeWorkerAdapter.Resolve`/`AgyWorkerAdapter.Resolve` all read the same `EffortTierMapping` statics — not test coverage: that suite only exercises agy's raw effort values end to end; it never hands Claude an `--effort`, and no test passes a canonical word to either vendor. Claude's model aliases (`ClaudeWorkerAdapter.ModelAliases`) are read live too, but that specific list has no validation surface of its own and is not exercised by that suite either — every alias always resolves to a vendor-current model, so nothing dispatch-side rejects one. agy has no equivalent alias catalog — its model names are suffix-parametrized (`gemini-<version>-<flash\|pro>-<low\|medium\|high>`), so the printed agy model examples are illustrative text, not a sourced table. |

#1355's acceptance criterion "one output path" is about `--output`/the printed fact above naming one
destination — not about a role declaring only one output (`review` declares two: `report.md` AND
`verdict.json`). `DispatchCommandEndToEndTests.Without_output_the_printed_fact_names_the_artifacts_directory_not_a_fabricated_file_path`
(pre-existing, #1354/#1380) is what pins the reading actually shipped.

Vendor, model, and effort are **three independent axes** over a role's instructions ([0017]):
the role carries a default bundle (its tier), and each axis overrides on its own.

Model and effort are validated at the adapter boundary before dispatch (#1090): a dot-delimited claude
id (`claude-opus-4.8`, a typo for `claude-opus-4-8`) is refused with the correction rather than run;
and on agy, where the effort suffix in the model name and `--effort` are one control, a disagreeing
pair is refused up-front naming the real cause instead of failing after the run has started.

### The spec/grant mismatch lint

Before a role's spec is dispatched, it is heuristically scanned for shell- or network-implying
instructions and compared against the resolved role's grant (#1500). The heuristics themselves
live in `DispatchSpecLinter.Heuristics` — that list is the register, and it is deliberately not
restated here, so it cannot go stale in one place while the code moves on in the other. A line implying a capability the grant
withholds prints a warning to stderr naming the line and the missing category, e.g.:

```
Warning: Spec line 4 ('gh issue view 1500') implies shell instructions (gh), but role 'advise' has no-shell grant.
```

This is a **warning, never a refusal** — the heuristic is not a parser and cannot know a matched line
is inert prose ("pixie dust") or that the worker will route around it; it only shortens the loop from
"the lane discovers its instructions are unexecutable mid-flight" to "the operator sees it before the
room exists." That promise is a try/catch in `DispatchCommand` around the lint call, not just an
assertion on this page — see `DispatchSpecLinter`'s own class doc for why the wrapping is needed.

**Two known gaps, disclosed rather than fixed — the specifics of each, and why the shell one is
larger, live on `DispatchSpecLinter`'s own class doc; record-once, not restated here.** In short: the
shell check cannot tell an allowlisted command from a forbidden one, and the network check can miss an
unrelated command a scoped shell doesn't actually cover.

### The engine-run verify step (#1623)

`implement` declares an engine-run verify command (`pixi run gates-quiet`) — the ENGINE runs it once,
never itself holding a lock across the run (`spec/baton.md` §3 states the actual locking mechanism),
after the worker's own process exits 0 with its output contract satisfied; the worker itself is never
asked to run gates or tests and never sees the command. `review`/`advise` and
every other role declare none. A verify failure is never a blind retry: it settles the step
`Indeterminate`, with the failing gate members and a bounded output tail recorded as room facts
(`verifyStarted`/`verifyPassed`/`verifyFailed` in `flow.jsonl`) — a conductor resolves it, the same way
an ambiguous captured-response outcome does (spec/baton.md §3).

### The per-execution token budget (#1623, per-adapter default #1745)

`implement`/`review`/`advise` carry default budgets; every other role runs unwatched unless `--token-budget` is passed.
A role's catalog entry is either one figure that applies no matter which adapter runs it (today's
shape, and still what `implement`/`advise` use) or a map keyed by adapter name (`review`'s shape, both
values presently equal — spec/baton.md §3 has why and states the resolution rule for an
unconfigured adapter). Usage is read incrementally from the vendor's own `stream-json` output
as it arrives, not just the terminal line, so a poll loop or a runaway tool-call sequence is caught
mid-flight rather than after the fact. Crossing the budget arrests the execution (cancels it, never
lets it keep running) and settles the step `Indeterminate` — `executionArrested` in `flow.jsonl`
carries the measured usage, the last few tool names observed, and (#1745) the adapter the applied
budget was resolved for.

### The auto-provisioned worktree, and what it costs

An audited role's dispatch prints the consequence before the run starts:

```
Workspace: worktree of <repo> at HEAD (<short-sha>) — uncommitted changes are not visible to the worker
```

The provisioned tree is torn down once the room reaches Terminal — **except** when it carries
uncommitted changes (a worker's own output written but not committed) or a removal is blocked (a
still-held file), in which case it is deliberately kept rather than discarded, and a Ctrl-C or crash
mid-run leaves it in place too. A kept tree is one more entry in the *workspace repository's* own `git
worktree list`, not something the operator asked for per invocation — `baton run`'s own worktree teardown
reporting (`worktree <outcome> at <path>`, printed to stderr) is what surfaces it.

### The printed grant line

Every dispatch also prints the least-privilege grant profile actually in force, one line per bound
worker (just one line for an ordinary single-role dispatch), before the run starts (#1355 — least
privilege default grants per role):

```
Grant: read, no-write, no-shell, no-network
```

Read left to right: `ReadFiles`, then `WriteFiles` (an `AuditedNotEnforced` write — the shape
`--workspace`'s row above describes — prints as `write (workspace-wide inside an isolated worktree;
audited against declared outputs after the run)` rather than a bare `write`: the grant is NOT scoped to
the declared outputs while the worker runs — the vendor hook cannot path-scope it, only confine writes
to the provisioned worktree — and declared-output confinement is checked only afterward, by the
post-run cleanliness audit; see `GrantAuditMode.AuditedNotEnforced`'s own doc), `RunShellCommands`,
`NetworkAccess`. This is the same category vocabulary the fake adapters in the test suite already use
for a grant, not a second one invented for this line — read it as what the invoking agent can honestly
relay to its own permission layer, not as a hardening claim about a vendor that was never asked.

Only printed for a bound worker whose adapter actually consumes a structured grant (implements
`IPermissionGrantTranslator`, `src/Baton.Vendors/WorkerBindingResolver.cs`'s own rule for which
adapters a grant governs). A composed template's capture step, say, spawns `git` directly and never
reads a grant at all — its phase gets no line printed, never a placeholder one.

**Read-shaped roles** (`review`, `fact-check` — both `write_files: false`) default to `claude`, whose
withheld writes still reach the outbox through AER's own hook rather than the `AuditedNotEnforced`
path above (`IWorkerAdapter.WithheldWritesReachTheOutbox`, `docs/decisions/0004-permission-scopes.md`)
— that path is only entered on `--adapter agy`.

`fact-check` stays `no-shell`/`no-network` outright. `review` no longer does (#1456, reversing
#1355's flat refusal for this role specifically — see spec/baton.md §9 for the full reasoning and the
network-honesty caveat): it now carries a scoped read-only `git`/`gh` shell grant. The exact
allow/deny pattern lists and the three catalog fields expressing them live canonically in
spec/baton.md §9; this page does not restate them. Enforced on claude via
`--allowedTools`/`--disallowedTools` pattern matching — a measured same-tool ceiling, not mere
pre-approval — not a `PreToolUse` hook change. `agy`'s `IPermissionGrantTranslator` still refuses
`RunShellCommands` without `NetworkAccess` with no scoped exception, so this shell grant does not
reach `--adapter agy`: `review` there now refuses to dispatch (`PermissionGrantUnsupportedException`)
rather than falling back to its old no-shell shape.

`advise` and `patch` are the same shape by outcome (no unscoped shell or network, `write_files:
false`) but not by mechanism: `advise` (#1386) withholds the write and, on its default `agy` tier,
relies on #901's audited-write widening (`GrantAuditMode.AuditedNotEnforced`) to un-refuse once a
worktree is provisioned — the same shape `review`/`fact-check` take when forced onto `agy`. `patch`
never grants a write in the first place — its whole point is proposing a diff without mutating the
workspace.

### The printed skill roster

Every dispatch also prints the worker's discovered skill roster, one line per bound worker whose
adapter is registered (or `Skills (<worker>)` for composed templates), before the run starts (#1512).
Like the Grant line above, a composed template's capture step gets no line, for the same "spawns `git`
directly, nothing to report" reason as the Grant exclusion — but drawn independently, since skill
discovery does not depend on whether an adapter consumes a permission grant (`src/Baton.Cli/DispatchCommand.cs`).

```
Skills: none discovered
```

or, when skills exist in the worker's environment (e.g. `~/.claude/skills/` or `<workspace>/.claude/skills/`
for Claude — also `<CLAUDE_CONFIG_DIR>/skills` when `BATON_CLAUDE_CONFIG_ROOT` is set, replacing the
`~/.claude` arm rather than adding to it — or canonical skill packages `<workspace>/skills/<name>/SKILL.md`
realized per vendor, #1151):

```
Skills: artifact-design (to be projected), run-checks (to be projected, 1 file(s) to be kept)
```
or for agy:
```
Skills: artifact-design (inlined, 2.4 KB), run-checks (inlined, 380 B)
```

For a worktree-provisioned binding (an audited role), the roster scans the source repository rather
than the worker's not-yet-provisioned worktree, and says so —
`Skills (from <repo>; the worker runs in a fresh worktree at HEAD): …` — since an untracked skill it
finds there may not survive into the worker's actual checkout. Full rationale on the exclusion above
and this line: `src/Baton.Cli/DispatchCommand.cs`'s skill-roster block.

### Canonical skill packages: the floor realization only

**This is the floor slice of #1151, not its ratified slice 1.** A canonical package is a directory
`<workspace>/skills/<name>/` holding a `SKILL.md`; each vendor gets it in whatever shape that vendor
can actually consume, and nothing else about #1151 ships yet:

| vendor | realization | what the roster says |
|---|---|---|
| claude | the package's files are **projected** into `<workspace>/.claude/skills/<name>/`, where the CLI reads project skills — **inside the operator's own checkout**, see "Where the projection lands" below | `<name> (to be projected)` |
| agy | the `SKILL.md` body is **inlined** into the dispatch prompt under a `# Skill: <name>` header AER emits, since #1572 measured that agy does not read `.agents/skills` on its own | `<name> (inlined, <size>)` |
| codex | **none.** No codex path reads a canonical package, so a codex binding in a repository carrying `skills/` reports `Skills: none discovered` and receives nothing — a realization for it is unbuilt work under #1151, not an omission this doc glosses over | — |

**Both realizations are predictions at roster time**, and only claude's is written in the future tense.
Neither has happened when the line is printed: the projection is placed later, by the dispatcher, and
agy's inlining happens later still, at prompt build. Claude's is tensed because it is the one whose
prediction can come out *false* — a destination holding different bytes is kept, so a declared file may
not be placed (#1929 review). Agy's cannot: the size it reports is measured on the same string the
prompt gets (`AgyWorkerAdapter.InlinedSkillBody`), so `(inlined, <size>)` is a prediction that a
dispatch cannot contradict.

**When the projection happens, and what it will not do.** Nothing is written while a binding is merely
resolved: `baton decide`, `run` and `resume` all resolve bindings that may never dispatch, and the
working directory carries the constraint `ClaudeWorkerAdapter.Resolve` already states for launch config.
The files are placed by the dispatcher when an execution actually starts, and:

- **an existing file that does not already match the package is never overwritten.** It is kept, and the
  roster's suffix carries the count (the sample above shows the shape). That count is a snapshot taken
  when the roster is printed; the dispatcher re-measures the same predicate immediately before each copy,
  so the guarantee holds even when the number is stale.
- **nothing is ever pruned.** Renaming `skills/foo` to `skills/bar` leaves `.claude/skills/foo` on disk,
  where the CLI still loads it. Removing it is the operator's call.
- **the roster line is a prediction, not a record.** It is printed before anything is written, which is
  why its realization reads in the future tense. Two records follow the act: one stderr line from the
  dispatcher naming how many of the declared files were placed, which packages, and where; and one
  durable room fact, `FlowEvent.EngineFilesPlaced` (that type's own doc is the canonical description).

### Where the projection lands

**Inside the operator's own checkout, untracked.** `<workspace>/.claude/skills/<name>/` is a real path
in the working tree; this repository's `.gitignore` narrows its `.claude` exclusion to
`.claude/worktrees/` deliberately, so a projected `SKILL.md` appears in `git status` and is committable.
There is no AER-owned alternative to move it to, and the reasons are measured rather than assumed:

- `BatonPaths.WorkerLaunchConfig` is AER-owned but is reached by the `--settings` and `--mcp-config`
  flags. It is not a `CLAUDE_CONFIG_DIR`, so a `skills/` directory under it is read by nothing.
- AER injects `CLAUDE_CONFIG_DIR` only when the **operator** has set `BATON_CLAUDE_CONFIG_ROOT`
  (`docs/runbooks/claude-shared-config-root.md`), and cannot mint a root of its own: a fresh one starts
  without subscription credentials and every dispatch under it fails loudly — measured,
  `durability.config-dir-redirect-breaks-auth` in `tools/vendor-verify/verify.py`.
- `--add-dir` loads no configuration at all — measured, `gate.add-dir-loads-no-config`.

Because those files are AER's and not the worker's, the engine subtracts them from its own work-product
evidence and from its timeout-retry guard, on the live dispatch path and on crash recovery alike (#1933).
The rule and what it still counts are stated once, in `spec/baton.md` §3's #1373 paragraph; the mechanism
is `WorktreeProvisioner.ChangedPathsExcludingEnginePlaced`.

**Precedence, written down here because nothing has ratified it.** A canonical package shadows a
same-named native skill under `<workspace>/.claude/skills/` (that is where the projection lands) and
under `~/.claude/skills/` (project beats user). The one exception runs the other way: under
`BATON_CLAUDE_CONFIG_ROOT`, #1575 measured that the CLI resolves a collision to the **config-root** copy
and the project copy does not surface at all — so the config-root entry is reported unsuppressed and the
canonical one reads `<name> (to be projected, shadowed by the config root)`. This rule is a consequence of what
ships, not an operator ruling; #1151's Q3 deferred the precedence question along with the repo-local
overlay, and this floor slice reopened it by keying on the working directory.

**What slice 1 still owes, all of it tracked by #1151.** Read this list before assuming a named skill
does anything: there is no `skill.json` manifest, no `realization: native-preferred` field, and no
format lint. The three ratified resolver rungs (`BATON_SKILLS_PATH`, `{BatonPaths.Root}/skills/`, a
shipped default beside the assembly) do not exist — what ships reads `<workspace>/skills/` only, which is
the **repo-local overlay Q3 explicitly deferred**, not any of the rungs. There is no `--skill` flag on
`dispatch`/`redispatch`, no `Skills` field on `WorkerBindingConfigEntry` and so no requirement check at
bind time and no inheritance across a redispatch, no `spec/baton.md` §9 entry, and no amendment to
decision 0010. Activation — whether either vendor's model actually *invokes* a skill under `-p` — is
unmeasured on both vendors; #1151's S2 is the measurement, and the roster's
`(to be projected)`/`(inlined)` is a claim about **placement**, never about activation.

**Rule for briefs:** Dispatched workers run in their own process and do not inherit the conducting session's loaded skills. Briefs must inline what they need; a named skill only works if the worker's roster shows it. Skill forwarding is not performed by dispatch.

## Roles

Each role declares what it must produce; those declarations become the contract the engine enforces,
so a role that writes nothing fails loudly. The roles and their outputs are defined in
`src/Baton.Vendors/WorkerRoles.json` (authoritative); this table is a snapshot, pinned against that
catalog by `WorkerRoleCatalogTests`.

**A missing declared output is never silently filled in — the engine only ever captures and attaches,
never writes (#1594).** See `src/Baton/Outcomes/OutputMaterializer.cs`'s class remarks for the ruling
this implements and exactly what gets captured where. In short: if every unsatisfied output is missing
(never present-but-wrong) at settle time and the execution's own terminal result carried a usable
response, that response lands in an engine file beside the declared outputs, never under a declared
name, and a room fact records which declared names it stands in for — see
`docs/agents/invoking-baton.md` §3 for what that fact looks like to a harness reading the room. The
room settles `Indeterminate` (#1608 — the two-predicate model's disagreement case, carrying no
`FailureClassification` at all, spec/baton.md §3), not `Failed`; `RetryEngine.MayRetry` refuses it
unconditionally via its own explicit arm, independent of any classification — see spec/baton.md §3's
"Producers" section for every source that settles a step this way and which ones the ordinary retry
path stays open for. Only a conductor's own recorded resolution — `baton resolve <room-dir>
[--execution <id>] --accept-capture | --reject --reason <text>` — can turn a capture into a satisfied
contract, or explicitly refuse one.

The prose-safe/all-or-nothing rules that gate what the engine ever wrote also gate what a capture MAY
later be resolved into: a plain-text output (`.md`/`.txt`/no extension, no declared
`Schema`/`Condition` — `advice.md`, `changes.md`, `findings.md` above) can honestly be resolved from a
captured response; a structured output (`verdict.json`, `patch.diff`, `turn-actions.json` above) can
not — prose can't honestly stand in for a declared shape. `janitor`'s two outputs (`janitor.md`,
`branch.diff`) are a mixed pair under this rule: `branch.diff` is not prose-safe, so a capture never
fires while it is among the missing outputs (an all-or-nothing capture that could only ever resolve
`janitor.md` is refused entirely) — a capture for this role is only possible when `janitor.md` alone is
missing and `branch.diff` is already present and valid. See `src/Baton/Outcomes/OutputMaterializer.cs`
for the capture mechanism and `src/Baton/Mutation/MutationInterface.cs`'s
`RecordCaptureResolutionAsync` for the resolution: it does not re-derive prose-safety at resolution
time (see `OutputMaterializer`'s own class remarks for why that would be redundant).
`--accept-capture` strips the engine's own banner
(`OutputMaterializer.StripCapturedResponseHeader`) and writes the remaining body under each of those
declared name(s) — `baton resolve` is the one permitted writer here (spec/baton.md §3 rules why).
`--reject --reason <text>` writes nothing; the reason is the room fact's own justification.

| Role | Tier | Writes | For |
|------|------|--------|-----|
| `advise` | standard | `advice.md` | Weighing an open design question before building — a second opinion. |
| `implement` | standard | `changes.md` | A bounded change whose approach is already decided; exercises the write path. |
| `review` | frontier | `report.md`, `verdict.json` | Adversarial review of a claim; the default for a PR touching `src/` or asserting something in `docs/`. |
| `patch` | frontier | `patch.diff` | Proposing code changes as an applyable diff without mutating the workspace. |
| `fact-check` | minimal | `findings.md` | Confirming an exhaustive, supplied list of facts against the repo — not for noticing what the list omits. |
| `janitor` | cheap | `janitor.md`, `branch.diff` | Running named mechanical checkers to green after an implementer, without changing behaviour. |
| `orchestrate` | orchestrator | `turn-actions.json` | A resident room turn that reads room state and emits turn actions. |

Each tier pins one vendor, model and effort in
[`src/Baton.Vendors/WorkerTiers.json`](../src/Baton.Vendors/WorkerTiers.json). As of #1861 (2026-09-04)
`frontier` is claude opus at high effort and `standard` is claude opus at medium: in
[`benchmarks/deepswe/2026-09-04`](../benchmarks/deepswe/2026-09-04/README.md) Opus medium scored 69%
at 52 agent steps while Sonnet never exceeded 54% at any effort and took about twice the steps at
matched effort (108 vs 52 at medium, 147 vs 73 at high, 268 vs 99 at max), and
[`benchmarks/subscription-usage/2026-09-04`](../benchmarks/subscription-usage/2026-09-04/README.md)
shows Baton sessions re-reading roughly 10M cached tokens each at median; that snapshot attributes the
early weekly exhaustion to fleet volume with cache re-reads as the amplifier, and "steps rather than
output drain the plan" is this paragraph's inference from it. High buys review four more points for
about 40% more steps; xhigh holds that score for 22% more steps, and max adds one point for about
twice the cost. Moving `standard` onto claude also changes `advise`'s default shape: on agy its
withheld write ran audited in a fresh worktree, on claude it runs enforced against the caller's own
directory (a genuine read-only lane, see §4 of `docs/agents/invoking-baton.md`), which is the
better fit for a second opinion that must not touch the tree. One caveat the snapshot itself states: its dollar
column is tokens at API list price, so it carries the per-token price gap between models but not the
subscription meter's own weighting, which the vendor does not publish. Opus medium is better on
quality and steps for certain; cheaper on the plan only if that weighting is roughly proportional to
price, which the conductor's per-launch model/effort log is what will show. The `standard` pin is interim — `cheap` keeps agy's
flash-low for when that quota is idle, and the codex adapter (#1853) is expected to add a compact
route (Sol high: 69% at 37 steps in the same snapshot) worth comparing against Opus medium for
`implement` once it exists.

The prompt each worker receives is the spec followed by the role's own output instructions, so the
worker is told to produce exactly what the contract asserts. A dispatched worker is also told its turn
is one-shot (#1095): do the work to completion now and write the outputs before the turn ends — never
schedule background work or wait for a wake-up, because nothing resumes the turn.

## `baton redispatch` — rerunning a terminal room with an amended brief

```
baton redispatch <room-dir> [--spec <amended-brief>] [--attach <file>]... [--adapter <vendor>] [--model <m>]
                          [--effort <e>] [--workspace <dir>] [--output <path>] [--timeout <minutes>]
                          [--token-budget <n>] [--max-tool-steps <n>] [--verify <cmd>] [--label <text>]
                          [--workstream <slug>]
```

`<room-dir>` names the parent room to rerun. The full contract — what each flag inherits from that
room vs. overrides, the Terminal/single-role refusals, and where lineage is recorded — is
`spec/baton.md` §2; this page does not restate it.

**The spec/grant mismatch lint above and `--attach` now also run on `redispatch`'s `--spec` path
(#1576), the identical way `dispatch` already runs them.** Both go through `RoleSpecMaterializer`, the
seam `DispatchCommand`'s own role path and `RedispatchCommand`'s amended-spec path now share, so
neither can silently diverge from the other again — see `spec/baton.md` §2 for why an amended brief
is exactly the moment a grant/instruction mismatch is likeliest to appear. `--attach` is refused
outright when `--spec` is omitted: `spec/baton.md` §2 states why (record-once, not restated here).
<!-- record-once-ok: #1576 spec/baton.md -->

## What a dispatch leaves in the room

The room directory accumulates the materialised workflow definition and its worker bindings, the
`flow.jsonl` event ledger (the append-only record of what the engine did), and an `artifacts/` tree
holding each step's declared outputs. The authoritative room layout is `spec/baton.md` §2;
`baton status <dir>` — the room directory is positional there, not a flag — reads the ledger and
reports where each step stands.

## The vendor premise

AER spawns the vendor's **own** first-party CLI, which authenticates itself against a **subscription**
— AER never handles a credential, and there are no API keys anywhere in this path (Architecture Rule
4). So a role runs only on a vendor that is already logged in on this host, and *which* vendor that is
is a fact of the host, not something a dispatch can provision. Dispatching a role to a vendor whose
CLI is not authenticated fails at that vendor's own login check, not inside AER.

If the vendor reports quota exhaustion, the engine paces a retry to the reported reset instant
(decision 0026) rather than burning attempts on a doomed retry. A foreground `baton dispatch` surfaces
that park — `Parked on vendor quota — the run resumes automatically at <time>` — and can be stopped
with Ctrl-C, which records a resumable state; re-running resumes it (#1094).
