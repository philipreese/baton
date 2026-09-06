# Vendor documentation audit — every documented capability, and whether we verified it

Measured against the #527 audit versions, pinned once in
[`vendor-capabilities.md`](vendor-capabilities.md)'s dated history table (#952 — one pin, pointed
at, never restated). Read the docs, then verify each claim against the
live CLI — `vendor-capabilities.md` was built the other way (probe binaries and help text first) and
several rows were wrong as a result, all the same shape: **a capability was recorded as absent
because the surface checked did not mention it.** The documentation mentions it.

Claude Code's documentation index is machine-readable: `https://code.claude.com/docs/llms.txt` —
~170 pages, each fetchable as `.md`.

Every row below carries an **evidence class**, same discipline as `vendor-capabilities.md`:

- **documented** — the vendor says so. A claim, not a measurement.
- **verified** — we ran it and observed the documented behaviour.
- **contradicted** — we ran it and the documentation does not hold here.
- **unverifiable here** — cannot be tested on this host/platform/plan.

---

## Corrections this audit forces

### 1. `claude` HAS an OS-enforced sandbox — but not on Windows

Not "referenced in help only," as `vendor-capabilities.md` once recorded. **Two full documentation
pages describe Claude Code sandboxing:**

| | documented |
|---|---|
| enforcement | **OS-level** — Seatbelt on macOS, `bubblewrap` on Linux/WSL2. Applies to all child processes |
| enable | `/sandbox` panel, or `sandbox.enabled` in settings; managed settings can force it |
| filesystem | `sandbox.filesystem.allowWrite` / `denyWrite` / `denyRead` / `allowRead` / `disabled` |
| network | proxy outside the sandbox; `network.allowedDomains` / `deniedDomains` / `tlsTerminate` / `httpProxyPort` / `socksProxyPort` |
| credentials | `sandbox.credentials.files` and `.envVars`, each `"mode": "deny"`, envVars also `"mask"` (sentinel substituted by the proxy for `injectHosts`) |
| hard-fail | `sandbox.failIfUnavailable: true` — refuse to start rather than silently running unsandboxed |
| escape hatch | model may retry with `dangerouslyDisableSandbox`; disable via `allowUnsandboxedCommands: false` ("Strict sandbox mode") |
| org lockdown | `allowManagedDomainsOnly`, `allowManagedReadPathsOnly`; settings files are write-denied inside the sandbox at every scope |

*"The sandbox is built into Claude Code and runs on macOS, Linux, and WSL2. **Native Windows is not
supported.**"* The probe host is Windows 11, so claude's sandbox ceiling is OS-enforced on
macOS/Linux/WSL2 and unavailable on native Windows here. **Every row in `vendor-capabilities.md` is
established on Windows only** — a single-platform observation is not a cross-platform capability
claim.

### 2. `--help` is officially incomplete on `claude` too

On channels: *"Neither `--channels` nor `--dangerously-load-development-channels` appears in
`claude --help` while the feature is in preview. **The flags work even though they aren't listed.**"*
So "not in `--help`" is not evidence of absence on **either** vendor — the vendor says so directly.

### 3. `--permission-prompt-tool` is documented in the CLI reference, just absent from `--help`

Those are different claims. The documentation states two constraints not otherwise measured:

> "Claude Code waits for that tool's MCP server to connect before running the first turn, up to the
> `MCP_TIMEOUT` startup timeout of **30 seconds**. The prompt tool **can't approve an MCP tool marked
> as requiring user interaction**: Claude Code converts an `allow` result for one to a deny."

### 4. `--permission-mode manual` is an alias for `default`, by design

Recorded as "a no-op headless". The documentation: *"`manual` as an alias for `default`… the mode the
UI labels Manual… `claude --help` lists it in place of `default`, and both values work."* Our
observation (session reports `default`) was correct; the interpretation was not.

### 5. `claude --model` REJECTS an unrecognized id — it does not silently degrade

Reported (#1090) as: a dotted id like `claude-opus-4.8` silently degrades to a retired Opus 4, so
every dispatch quietly runs the wrong model. **Measured 2026-08-10, verbatim, `claude` 2.1.226:** it
does not degrade — it fails. The `system:init` event echoes whatever `--model` string was passed (so
init is *not* a resolved-model signal), but the assistant turn resolves to `model:"<synthetic>"`,
`is_error:true`, exit 1 — byte-identical to a plainly bogus id (`claude-bogus-nonexistent-zzz`), while
the dash form `claude-opus-4-8` runs (real model, exit 0). So `claude` validates model ids; it just
does so *after* spinning up the turn rather than pre-flight, and the failure is loud, not silent.

The consequence is still worth catching up-front: on the dispatch path an exit-1 turn is
`RetryPolicy(3)`-retried with the identical bad argv before surfacing that cryptic `<synthetic>`
error. `ClaudeWorkerAdapter` (#1090, `MalformedVendorModelException`) refuses the dash→dot typo at
resolution — the one malformed form distinguishable without a model list, which `claude` does not
ship (`ClaudeWorkerAdapter.ModelAliases`). This is not general model-id validation.

---

## Capabilities the design predates and should be measured against

### Dynamic workflows — the vendor ships an orchestrator with the same shape as AER's engine

A **JavaScript script that orchestrates subagents at scale**, executed by a runtime in the background
while the session stays responsive. This is close enough to what AER Flow does that it must be
compared deliberately rather than discovered later.

```javascript
export const meta = { name: 'audit-routes', description: '…' }
const found = await agent('List every .ts file under src/routes/.', { schema: { … } })
const audits = await pipeline(found.files, file => agent(`Audit ${file}…`, { label: file }))
return audits.filter(Boolean)
```

| documented property | value |
|---|---|
| primitives | `agent()` spawns one subagent; `pipeline()` runs one per item |
| concurrency | **up to 16 concurrent agents**, fewer on low-core machines |
| total cap | **1,000 agents per run** |
| user input mid-run | **none** — *"Only agent permission prompts can pause a run. For sign-off between stages, run each stage as its own workflow"* |
| filesystem/shell | the script has none; only its agents act |
| resumability | resumable **within the same session**; completed agents return cached results. Exiting Claude Code loses the run |
| storage | `.claude/workflows/` (project) or `~/.claude/workflows/` (personal); saved runs become `/<name>` commands; distributable in plugins as `/<plugin>:<name>` |
| input | an `args` global |
| subagent permissions | **always `acceptEdits`**, inheriting the tool allowlist, *regardless of the session's mode* |
| approval | prompted per run except in bypass / `-p` / SDK, where *"the run starts immediately"* |
| disable | `disableWorkflows` setting, `CLAUDE_CODE_DISABLE_WORKFLOWS=1`, or `/config` |

**Directly relevant to our decisions:**

- *"No mid-run user input… For sign-off between stages, run each stage as its own workflow"* is the
  vendor hitting the same wall decision 0015 and the gate model
  are built around — and choosing the opposite trade-off. Worth understanding before we commit.
- The four-way comparison table (subagents / skills / agent teams / workflows) is organised around
  **who holds the plan**, which is precisely the axis the fan-out decision (#503 items 4–5) argues
  about.
- `agent()` + `pipeline()` is a fan-out primitive with an explicit concurrency cap. Our blockers model
  should be compared against it.

### Agent teams

*"A lead agent supervising peer sessions"*, coordinating through **a shared task list**, where
*"teammates keep running"* through an interruption. A third fan-out primitive, distinct from both
subagents and workflows.

### Channels — events pushed into a running session, and remote permission relay

An MCP server that **pushes events into a live session**, two-way, so the session reacts to things
that happen while nobody is at the terminal. Enabled per session with `--channels plugin:<name>@<mkt>`.

Two properties that land directly on our open work:

- **Permission relay.** *"If Claude hits a permission prompt while you're away from the terminal, the
  session pauses until you respond. Channel servers that declare the permission relay capability can
  forward these prompts to you so you can approve or deny remotely."* That is the remote-answer half
  of 0015's gate, already specified by the vendor.
- **Non-interactive safety.** *"When you run channels in non-interactive mode with `-p`, tools that
  need terminal input… are disabled so the session never stalls waiting for input."*

Gated: research preview, requires claude.ai or Console auth, Team/Enterprise must enable
`channelsEnabled`; `allowedChannelPlugins` restricts which plugins may register.

---

## To verify

Nothing in the sections above has been run yet except where `vendor-capabilities.md` already records a
measurement. The verification pass is tracked in #515 and the issues it references.

Priority, by how much design leans on it:

1. Sandbox on a non-Windows host — the 0004 claim cannot be tested on this machine at all.
2. Workflow `agent()`/`pipeline()` semantics vs. the blockers model.
3. Channels permission relay vs. 0015's gate.
4. `--permission-prompt-tool`'s 30 s `MCP_TIMEOUT` and the `requiresUserInteraction` allow→deny conversion.
5. `--max-budget-usd` enforcement (#479).
6. `agy`'s documented surface — not yet located; see below.

---

## `agy` — the documentation exists, and it overturns four of our rows

Docs live at `https://antigravity.google/docs/cli/...` (`overview`, `reference`, `permissions`,
`sandbox`, `modes`, `subagents`, `projects`, and `commands/*`). None of it had been read.

### 1. `command(...)` rules are documented as **regex**; measured as literal

`0004`'s consequence — a command *family* cannot be pre-authorised on `agy` at all — depends on
this. The documentation says the opposite of what was measured:

> "Each whitespace-separated token is evaluated as an **anchored regular expression**."
> `command(npm run (build|lint|test))` matches `npm run build` and `npm run test`.

Re-run against 1.1.7, one rule at a time, restoring the operator's real settings file byte-exactly
after every case (SHA-256 verified unchanged before/after):

| rule | if literal | if regex | **observed** |
|---|---|---|---|
| `command(node)` | denied | denied | denied |
| `command(node .*)` | denied | *granted* | **denied** |
| `command(node (--version\|--help))` | denied | *granted* | **denied** |
| `command(node --version)` | granted | granted | **granted** |

Both discriminating rules failed, including the documentation's own alternation form. **Matching is
literal, and 0004's consequence stands: AER cannot pre-authorise a family of commands on `agy`**,
only enumerate exact command lines (#515). **The only row in this audit where the documentation is
wrong and the measurement was right** — every other correction ran the other way, which is why
documentation is *documented*, never *verified*, until a run settles it.

### 2. There is an `ask` list, and the precedence is a three-rung ladder

We recorded `permissions.allow` / `.deny`. There are **three** lists — `allow`, `deny`, **`ask`** —
and:

> "Conflicting rules are strictly evaluated in priority order: **Deny > Ask > Allow**."

So `agy` has the same allow / ask / deny shape as `claude`'s `auto-mode` classifier and as
decision 0022's ladder. Three independent
designs, one shape. 0022 should be reconciled against both rather than either.

Also documented and not recorded by us:
- **Implicit rules** — writing a file grants read on the same path; denying read blocks write.
- **Defaults** — workspace files auto-allowed, web browsing asks, *unconfigured actions default to ask*.
- **Interactive scope editing** — a user may edit the target string to widen scope before approving,
  "except for terminal commands".
- **Windows path normalisation** — paths are normalised before rule evaluation "by stripping drive
  letters and converting all backslashes to forward slashes". Directly relevant to AER on Windows.

### 3. `agy` sandboxes on Windows; `claude` does not

| OS | `agy` mechanism | `claude` mechanism |
|---|---|---|
| Linux | `nsjail` (namespaces + cgroups) | `bubblewrap` |
| macOS | `sandbox-exec` | Seatbelt |
| **Windows** | **`AppContainer`** | **not supported** |

Enabled by `enableTerminalSandbox` in settings (default `false`), restricting shell execution,
filesystem, network, and CPU/memory. Per-execution override both ways: *"Yes, and run without sandbox
restrictions"* when enabled, *"Yes, and run in sandbox"* when disabled.

**On the operator's Windows host, `agy` can contain a process and `claude` cannot.** That is the real
asymmetry, it is platform-dependent, and neither of the two previous versions of the 0004 claim said
so.

### 4. `agy` does report quota, headlessly as of a CLI update (superseded 2026-08-28)

- **`/usage`** (alias **`/quota`**) — "Display model quota usage"; shows "your usage limits and
  remaining requests/tokens for each supported model (e.g. Gemini 3.5 Flash, Gemini 3.1 Pro)", and
  triggers "a fresh check of your quotas on disk and from the backend service".
- **`/credits`** — "View remaining G1 credits and purchase links", with a `useG1Credits` setting to
  spend personal credits once quotas are exhausted.

**Both once opened only an interactive TUI panel; `/usage` no longer does.** This section originally
read "`agy -p "/usage"` genuinely produces no report headless, but the data exists and reaches a
backend — what's missing is a non-interactive path to it" — true when measured, superseded by a CLI
update rather than corrected, per the live 2026-08-28 re-measurement `vendor-capabilities.md`'s
"Usage, cost and quota" section records — see it for the exact shape.
`/credits` was not re-probed by this measurement.

### 5. `toolPermission` has four values

`toolPermission`: `request-review` (default) · `proceed-in-sandbox` · `always-proceed` · `strict` —
settings values, not command-line flags.

### 6. Slash commands `agy` has that our records never mentioned

| command | documented as |
|---|---|
| `/btw <query>` | "Ask a side question in the background **without interrupting the main conversation**" |
| `/fork` / `/branch` | "Clone the current conversation thread into a **new parallel session**" |
| `/agents` | "Agent Manager Panel to switch custom agents and **monitor background subagents**" |
| `/tasks` | "Task Manager Panel to monitor background shell execution logs" |
| `/rewind` / `/undo` | "Roll back your conversation history to a previous message" |
| `/context` | "context usage visualization panel" |
| `/permissions` | "interactive tool permissions manager panel" |
| `/diff` | "Interactive Diff Viewer to view changes, turns, and commits" |
| `/planning` | "multi-turn plan generation mode" |
| `/hooks`, `/skills`, `/mcp`, `/model`, `/statusline`, `/keybindings`, `/artifact` | — |

Two land directly on open work:

- **`/btw` is a documented answer to the queued-message problem (#462)** — a side question that does
  not interrupt the running turn. Worth studying before we design ours.
- **`Alt+J` "switches focus to the next subagent awaiting confirmation"** and **`Ctrl+K` "approves the
  pending subagent action"**. `agy` already models *a queue of gates across parallel subagents* with
  keyboard affordances — which is close to what the room list is being designed to do.

### 7. Settings keys we had not recorded

`allowNonWorkspaceAccess` (default `false`) — "Permit agent file access outside workspace", which is
almost certainly the mechanism behind the cwd sharp edge in `vendor-capabilities.md`.
Plus `artifactReviewPolicy` (`asks-for-review` / `agent-decides` / `always-proceed`), `colorScheme`,
`altScreenMode`, `notifications`, `verbosity`, `enableTelemetry`, `editor`, `runningLightSpeed`.

---

## Verification pass

Documented claims run against the live CLIs. **Verified** means observed, not read.

### `--bg` background sessions — **verified**

```
$ claude --bg --name aer-probe-bg "Write a file called hello.txt containing BANANA, then stop."
backgrounded · 330a655f · aer-probe-bg
  claude agents             list sessions
  claude attach 330a655f    open in this terminal
  claude logs 330a655f      show recent output
  claude stop 330a655f      stop this session
```

It appears in the registry, with its own working directory and the name we set:

```
background | blocked | id 330a655f | aer-probe-bg | …\scratchpad\bgtest
```

**So #506's original conclusion was wrong and its correction holds:** `claude agents --json` sees
`--bg` sessions, and a `-p` run simply is not one. If AER spawns workers as `--bg` sessions it
inherits the whole lifecycle — `attach`, `logs`, `stop`, `rm`, `respawn`, and a supervisor that
survives its own restart.

Two observations worth more than the flag itself:

- **The probe session's state was `blocked`, because it wanted to `Write` and was waiting on
  permission.** That is a background worker sitting on a gate, surfaced in a machine-readable
  registry — the exact object 0015's durable-gate section and the room list are designed around,
  already modelled by the vendor.
- **State vocabulary, observed:** `working` · `idle` · `blocked` · `stopped`. Four values, from
  `--json` and `--json --all`. Not established as exhaustive.

`claude daemon status` also works and reports pid, version, uptime, origin (`transient — started
on-demand by claude (pid …)`), config path and log path. Useful for #478 readiness.

Cleanup: `claude stop <id>` then `claude rm <id>` removed only the probe session; the operator's own
sessions were untouched.

### Both vendors self-updated during this one session — **verified, unprompted**

The staleness trigger built in #504 fired on its own within hours:

```
[STALE  ] claude has moved: findings were recorded against 2.1.219 on 2026-07-24,
          but 2.1.220 is installed. Every row for this vendor is now unverified.
[ok     ] agy 1.1.7 — findings recorded against this exact version on 2026-07-24.
```

Combined with `agy` moving 1.1.6 → 1.1.7 earlier the same day, **both CLIs shipped a new version
inside a single working session.** Vendor drift is not a quarterly concern to design around; it is
hours-scale. Anything derived from a probe needs a version attached or it is already decaying.

### `PreToolUse` hook fires where the permission-prompt tool does not — **verified**

The claim that decides AER's gate mechanism. A hook was installed via `--settings` **inline JSON**, so
nothing was written to the operator's configuration, and asked to deny a `Write`:

```json
{"hooks":{"PreToolUse":[{"matcher":"*","hooks":[{"type":"command","command":"python hook.py"}]}]}}
```

```json
{"hookSpecificOutput":{"hookEventName":"PreToolUse",
 "permissionDecision":"deny","permissionDecisionReason":"blocked by baton probe hook"}}
```

| `--permission-mode` | hook fired | write blocked |
|---|---|---|
| `auto` — where `--permission-prompt-tool` is silently skipped (#514) | **yes** | **yes** |
| `bypassPermissions` — the most permissive mode there is | **yes** | **yes** |

The worker reported: *"Blocked — a hook named 'baton probe hook' rejected the Write to `x.txt`. I'm not
going to route around it."*

**So a `PreToolUse` hook is the only gate instrument observed to hold in every mode.** It is what AER
should install on a `claude` worker. `--permission-prompt-tool` remains useful, but as a step-6
callback it is bypassed by `auto`, by `acceptEdits`, by `bypassPermissions`, and by any allow rule.

The payload is **richer than the permission-prompt tool's**:

```json
{ "session_id": "4e75b9d5-…", "prompt_id": "230ecffc-…",
  "transcript_path": "C:\\Users\\…\\<session>.jsonl",
  "cwd": "…\\hooktest",
  "permission_mode": "bypassPermissions",
  "effort": { "level": "high" },
  "hook_event_name": "PreToolUse",
  "tool_name": "Write",
  "tool_input": { "file_path": "…\\x.txt", "content": "BANANA\n" },
  "tool_use_id": "toolu_01HS7fN4VsAy6Egjdu2fYtLk" }
```

Three fields matter beyond the call itself:

- **`permission_mode`** — the gate is told what regime it is running under, so it can refuse to
  present itself as a control when the session is in a mode that would otherwise skip it.
- **`transcript_path`** — the conversation on disk. Structured access without parsing stdout.
- **`effort.level`** — not in the hooks reference we read; noted as undocumented-but-present.

Decision values are `allow` / `deny` / `ask` / `defer`, plus `updatedInput` to rewrite the call and
`additionalContext` to inject text without blocking. Exit 2 is an alternative deny path with stderr as
the reason.

### `defer` ends the query, and the session resumes — **verified end to end**

**This is 0015's durable gate, shipping.** The conflict below is resolved: the SDK reading is right.

A `PreToolUse` hook returning `defer`:

```json
{"hookSpecificOutput":{"hookEventName":"PreToolUse",
 "permissionDecision":"defer","permissionDecisionReason":"baton probe defer"}}
```

```
exit=0
subtype : success
reason  : tool_deferred        ← a distinct terminal reason
result  : (empty)
file written: NO
```

The query **ends cleanly** — not an error, not a denial — with `terminal_reason: "tool_deferred"`.
The process is then free to exit. Resuming that session with a hook that allows:

```
claude --resume ce6eea58-… --settings <hook that allows> -p "continue"
→ reason: completed
→ "Done — x.txt created in the working directory with the contents BANANA."
→ file written: YES
```

**So the pending work survives the process that was holding it, on disk, and completes when the gate
opens.** That is exactly the requirement 0015 states — *"the room records the pause when the question
is asked, not when it is answered"* — and it is available rather than needing to be built.

**Stated precisely, because the distinction matters:** what is verified is that the session persists
across process exit and the work completes once the gate allows it. Whether the *identical*
`tool_use_id` is replayed, versus the model re-attempting the same work, is **not** established. That
difference decides whether AER can promise "the exact call you approved is the one that ran", which is
a claim 0022's answer semantics may depend on.

Also documented, and worth having: when several hooks or rules apply, the precedence is
**`deny` > `defer` > `ask` > `allow`**. And `updatedInput` is ignored on a `defer`.

**Also unmeasured (issue #1359, `baton resume`): only ONE `--resume` hop is verified above.** Whether a
*second* `--resume` — passing the session id a first resume already continued — reaches the first
resume's own turn, or forks back to before it, has never been run. `baton resume` itself refuses nothing
based on this (a resume-of-a-resume dispatches the same way any resume does), but its own doc scopes
the continuation claim to this one measured hop rather than asserting it chains indefinitely.

### The conflict this resolves

The SDK's user-input page says `defer` is how a gate outlives its process:

> "If a user might take longer to respond than your process can reasonably stay running, return the
> **`defer` hook decision**, which lets the process **exit and resume later from the persisted
> session**."

A summary of the CLI hooks reference read `defer` as *"proceed with normal permission flow (same as
exiting 0 with no output)"*. **The run above shows that is wrong**, and the SDK hooks page states the
correct behaviour outright: *"Returning `"defer"` **ends the query** so you can resume it later."*

**The lesson is about the reading, not the docs.** That misreading came from a *summarised extraction*
of a long page, not from the page itself — a lossy step between the source and the claim, which is the
same failure mode as trusting `--help` over the reference. When a documentation claim is load-bearing,
go to the passage, and then run it.

### Hook events the design predates

The SDK hooks page lists events nothing in our records mentions. Four land directly on open work:

| event | why it matters |
|---|---|
| **`PermissionDenied`** | *"The auto mode classifier denies a tool call"* — a hook for exactly the #514 hole, so AER can observe classifier denials it would otherwise never see |
| **`Notification`** | fires with `permission_prompt` when Claude needs permission and `idle_prompt` when it is waiting for input — decision 0018's attention signal, as an event |
| **`Elicitation`** / `ElicitationResult` | *"An MCP server requests user input mid-task"* — a second, MCP-side path for "needs you" |
| **`SubagentStart`** / `SubagentStop` | carries `agent_transcript_path`, so fan-out progress is readable without parsing output |

Others present: `PostToolUse` (with `updatedToolOutput` to rewrite results before Claude sees them),
`PostToolUseFailure`, `PostToolBatch`, `UserPromptSubmit`, `UserPromptExpansion`, `MessageDisplay`,
`Stop`, `StopFailure`, `PreCompact`, `PostCompact`, `SessionStart`, `SessionEnd`, `Setup`,
`TeammateIdle`, `TaskCreated`, `TaskCompleted`, `ConfigChange`, `InstructionsLoaded`,
`WorktreeCreate`, `WorktreeRemove`, `CwdChanged`, `FileChanged`.

Two operational notes worth carrying into any gate we build: multiple hooks on one event **run in
parallel** and the most restrictive result wins; and a `PreToolUse` callback that exceeds its timeout
**blocks** the call (v2.1.210+), where earlier versions reported it as a user rejection and stalled
unattended sessions.

### Not verifiable from an agent session

- **`claude`'s sandbox** — not supported on native Windows, which is the only host available here.
- **`agy`'s `command(...)` regex-vs-literal conflict** — requires writing rules into the operator's
  real `~/.gemini/antigravity-cli/settings.json`. Needs an explicit decision about how to test safely.
- **Channels, workflows** — gated on plan/preview availability, and channels need a plugin install.

---

## The Agent SDK docs specify most of what 0015 is designing

The single highest-value page in the sweep. It also **explains the `auto`-mode finding mechanically**
rather than leaving it as an observation.

### The permission evaluation order — six steps, in this order

1. **Hooks** (`PreToolUse`) — "A hook can deny the call outright." A hook deny applies **even in
   `bypassPermissions`**.
2. **Deny rules** — block "even in `bypassPermissions` mode".
3. **Ask rules** — fall through to the approval callback "even in `bypassPermissions` mode".
4. **Permission mode** — `auto` is here: "A model classifier approves or denies permission prompts."
5. **Allow rules**.
6. **`canUseTool` callback** — only reached if nothing above resolved the call.

**This is why `--permission-mode auto` silently bypassed our gate.** `auto` resolves at step 4;
`--permission-prompt-tool` is the step-6 callback. The measurement was right and the mechanism is
documented:

> "**Auto-approved tools never reach `canUseTool`.** A tool call approved at any earlier step… skips
> your `canUseTool` callback, so permission checks you put there are **silently bypassed** for that
> tool. For checks that must run on every tool call, use a **`PreToolUse` hook**: hooks run before
> every other step, and a hook deny applies even in `bypassPermissions` mode."

**So AER's gate should be a `PreToolUse` hook, not only a permission-prompt tool.** That is a
materially better mechanism than the one decision 0015 chose, it
closes the hole recorded in #514, and it was documented the whole time. An `ask` rule is the second
always-fires instrument.

### `defer` — the durable-gate problem, already solved

0015 devotes a section to a pause outliving the process holding it, because "the process holding it
open is the one a crash kills". The SDK has a name for this:

> "The callback can stay pending indefinitely… If a user might take longer to respond than your
> process can reasonably stay running, return the **`defer` hook decision**, which lets the process
> exit and resume later from the **persisted session**."

That is the exact requirement 0015 states, with a shipped mechanism. It must be evaluated before we
build our own.

### `PermissionRequest` hook — the notify half of "needs you"

> "You can also use the `PermissionRequest` hook to send external notifications (Slack, email, push)
> when Claude is waiting for approval."

decision 0018's attention signal has a vendor-side hook.

### `AskUserQuestion` — the "decision" kind, with a wire format

0015's three kinds are permission / decision / approval. The **decision** kind is a shipped tool with
a defined schema: a `questions` array of `{ question, header (≤12 chars), options[{label, description}],
multiSelect }`, answered by returning `answers` keyed by question text, plus an optional freeform
`response`. TypeScript can request `preview` per option (`markdown` or `html`) via
`toolConfig.askUserQuestion.previewFormat`.

It **always reaches the callback even when an allow rule matches** — as do MCP tools marked
`_meta["anthropic/requiresUserInteraction"]` and connector tools an org set to `ask`. In `dontAsk`
mode all three are denied instead.

Limits: 1–4 questions, 2–4 options each; **not available in subagents**.

### The answer shapes we measured are the full documented set

`{ behavior: "allow", updatedInput }` / `{ behavior: "deny", message }`, plus a third we had not
found: **`updatedPermissions`**, echoing back a suggested `PermissionUpdate` from the callback's
`suggestions` argument. A suggestion with the `localSettings` destination writes the rule to
`.claude/settings.local.json` so future sessions skip the prompt.

That is **"approve and remember"** as a first-class concept, and the documented response vocabulary is
richer than approve/reject: *approve · approve with changes · approve and remember · reject · suggest
alternative · redirect entirely*. Worth comparing against
decision 0022 before designing our own set.

## Agent teams — the blockers model, shipping

The fan-out design settled in #503 items 4–5 on GitHub-style blockers, with parallelism emergent.
`CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1` enables a feature that already works this way:

> "Tasks can also **depend on other tasks**: a pending task with unresolved dependencies **cannot be
> claimed** until those dependencies are completed."
> "When a teammate completes a task that other tasks depend on, **blocked tasks unblock without
> manual intervention**."

Also: three task states (pending / in progress / completed), **file locking** on claim to prevent two
teammates taking the same task, self-claim as well as lead-assign, and a mailbox per agent at
`~/.claude/teams/{team}/inboxes/{agent}.json`. Task lists persist at `~/.claude/tasks/{team}/` and
survive resumption; team config does not.

**The owner's blockers design is independently validated by a shipping implementation.** That is a
good outcome for #503 items 4–5 and removes the concern that it was novel.

Security detail worth copying: a teammate **cannot approve a permission prompt on your behalf**, and
"in auto mode, the classifier treats an approval claim relayed from another agent as **untrusted
input** rather than confirmation from you." Teammate prompts surface in the **lead** session.

Hooks that gate the loop: `TeammateIdle`, `TaskCreated`, `TaskCompleted` — each blocks with exit
code 2 and sends feedback. Limits: no nested teams, one team per session, lead is fixed, permissions
set at spawn, no session resumption with in-process teammates.

---

## `agy` has hooks, and an SDK — documented, then confirmed working

`agy` documents five hook events:

| event | fires |
|---|---|
| `PreToolUse` | before a tool is executed |
| `PostToolUse` | after a tool completes |
| `PreInvocation` | before the model is called |
| `PostInvocation` | after tool calls finish |
| `Stop` | when the execution loop terminates |

`PreToolUse` returns a **`decision`** of `allow` · `deny` · `ask` · **`force_ask`**, plus `reason` and
`permissionOverrides`. `force_ask` is documented as *"always prompts, ignoring cached permissions"* —
a **stronger** always-fires guarantee than anything claude documents, if it holds. `Stop` can return
`continue` to prevent termination. Hooks are configured in `hooks.json` under `.agents/` or
`~/.gemini/config/`, receive `conversationId`, `workspacePaths`, `transcriptPath` and
`artifactDirectoryPath` on stdin, and reply on stdout.

**Confirmed working, not just documented** — see [§5 below](#5-agys-cli-hooks-work--and-the-gate-is-symmetric):
they load from `<workspace>/.agents/hooks.json` and from `~/.gemini/config/hooks.json`, fire
`PreToolUse`, and enforce `deny`.

**That is a fact about agy's mechanism, and from the day #603 shipped AER's gate it was read as a
fact about that gate. It is not** — see the section below. The sentence that used to end this
paragraph, *"The gate is symmetric across vendors"*, is true of what the two vendors offer and was
false of what AER shipped.

### The hook spec is inside the binary, and it settles the command-string question (#710)

`agy.exe` embeds its own complete `hooks.json` specification as plaintext. Extracted verbatim to
`.vendor-survey/corpus/agy__hooks-embedded.md` (`agy/hooks-embedded` in the audit register) — a spec taken out
of the shipped binary cannot be stale relative to that binary, which is a stronger guarantee than any
mirrored web page here carries. Re-extract after an agy upgrade.

The line that matters, and that no web page states:

> **`command`** (string, required): The shell command to execute (run via `sh -c` on Unix, **`cmd /c`
> on Windows**). `~` is expanded to the home directory. **The working directory is set to the
> directory containing `hooks.json`.**

Consequences, each measured end to end against the live CLI rather than reasoned from the sentence:

| | |
|---|---|
| `cmd` does not treat `'` as quoting | the single-quoted path AER shipped never resolved, so the handler never ran, and `agy.hook-malformed-stdout-fails-open` made that an **allow** |
| a quoted **program token** is never unquoted | not in either quote style — a `.cmd` at a quoted path does not run at all |
| a **bare** path with a space resolves alone and fails once an argument follows | and the real command always has `agy-hook-check` after it |
| `GetShortPathName` over the whole path yields `AERCLI~1.DLL` | .NET's assembly resolution is name-based, so it then hunts `AERCLI~1.deps.json` and dies `0x80008083`; shortening the **directory** and keeping the file name works |
| a **relative** name does not resolve under `cmd /c` | even with the file in the working directory, and the working-directory claim above is itself verified true |

Also in the embedded spec and absent from our records — read, not measured, and each needs its own
check before anything rests on it:

- **`overwrite`** — an object shallow-merged into a tool call's arguments *before it runs*. A gate
  could rewrite an out-of-bounds write target instead of denying it.
- `decision` also accepts `ask` and `force_ask`; the binary's own JSON schema additionally carries
  `deny_unless_prior_grant`.
- Named hooks **merge**, and `"enabled": false` disables one — so AER cannot assume its gate is the
  only hook present in a workspace.
- `PostToolUse` expects `{}`; `PostInvocation` can force continuation via `terminationBehavior`.
- Stated limitation: hooks run synchronously and block the agent loop.

Separately measured and documented nowhere: **agy reads hook stdout to EOF**, so a handler that
prints a valid verdict and leaves any process holding that handle fails open. Reproduced with
`sleep 6 &` and no .NET involved, and fixed by closing the handle. It is not what broke production —
that was the quoting above — but it is a live trap for any future handler that backgrounds anything.

**Proving the gate fired is asymmetric across the two vendors, and both halves are now measured
(2026-08-03, #948).** [#532](https://github.com/aer-works/baton/issues/532) proposes proving the
mandatory hook can execute by probing on claude's `SessionStart` "at zero model cost". Three things
the documented surface above settles, and two that live measurement has now settled too:

- **Settled.** None of the five events is *session-level* — every one sits inside an invocation. There
  is no `SessionStart` here to probe.
- **Settled.** `PreInvocation` fires before the model is called, but its only documented output field
  is `injectSteps`: **nothing in its contract can stop the invocation it precedes.** (`decision` is
  `PreToolUse`'s; `terminationBehavior` is `PostInvocation`'s.)
- **Settled, live (`gate.agy-toolcall-injection-does-not-work`, FAIL).** The `toolCall` injected-step
  kind — *"a tool call to execute"* — is documented but **not implemented** in the installed CLI
  (measured on 1.1.9, then re-confirmed on 1.1.10 after agy auto-updated mid-session). The checked-in
  check measures `PreInvocation` only: injecting `{"toolCall": {"name": "list_dir", "args": {...}}}`
  there kills the run outright (`error in pre-invocation hook: ... unknown injected step type:
  <nil>`). A one-time manual run against `PostInvocation` — which the schema documents as sharing the
  identical `injectSteps` contract — hit the same internal log line, non-fatally; that observation is
  disclosed in the check's own docstring but is not re-verified on every run, so treat it as a strong
  corroborating data point rather than an equally-measured claim. Either way, no changelog entry
  exists for `toolCall` injection ever working — this is not an unlucky invocation shape, it is the
  vendor's own code not recognising a field its own docs list. **agy has no free way to
  prove the gate fired.** Any proof costs at least one real, model-driven tool call.
- **Settled, live (`gate.sessionstart-without-a-turn`, PASS-with-scope).** No claude invocation shape
  tested produced a *successful* run reporting `num_turns: 0` — every shape that completes far enough
  to emit valid JSON also took a turn. But `SessionStart` fired on every zero-content invocation tried
  (empty prompt, bare `-p`, no `-p` at all), and the last of those is structurally conclusive: omitting
  `-p` entirely means the CLI has no prompt to send a model in the first place, and it still fires
  `SessionStart` before rejecting the invocation ("Input must be provided either through stdin or as a
  prompt argument when using --print"). `SessionStart` reliably precedes any invocation that could
  possibly take a turn. A resumed session remains untested.

An earlier version of this section asserted that "`PreToolUse` needs a tool call, which needs a turn"
and concluded the premise was false on agy **"whatever the claude measurement returns"**. That
reasoning was withdrawn as an unearned absolutism — but the live measurement above lands the same
practical place by a different, evidenced route: agy still has no free path, just not for the reason
originally claimed.

An `OnSessionStartHook` does appear once in the vendor's changelog, in a note about routing lifecycle
hooks through their Python-side `HookRouter`. That is product internals, not a `hooks.json` event —
recorded here so the next reader who greps for it does not read it as a configurable surface.

### The Python SDK answers all three of #508's open questions

`pip install google-antigravity` — a Python framework, documented as exposing:

| what | why it matters |
|---|---|
| **per-turn and cumulative token usage** | the usage/cost data for **#479**. Per-turn token usage turned out to be available on `agy`'s CLI after all — the `--output-format stream-json` `result`'s `usage` object (#1088; we had recorded it unavailable on the strength of a probe-grammar bug). *Cumulative* usage and a dollar figure remain SDK-only. |
| **streamed structured events**, "live model reasoning and output chunks", Pydantic-typed results | routing on structured events instead of parsed stdout — **Architecture Rule 1, structurally** |
| **`deny()` / `allow()` / `ask_user()`**, "declarative deny-by-default policy" | a permission surface with a human-approval primitive |
| nine lifecycle hook points, "Inspect / Decide / Transform" | the same gate shape, in-process |
| headless by design | usable the way AER would use it |

**That is #508's three unknowns — structured events, usage data, and permission control — answered by
one page, on the surface the audit had already nominated as the most promising and never read.**

It also reframes the `agy` half of the design: everything recorded so far assumes AER shells out to a
CLI. A Python SDK is a different integration shape entirely, with different guarantees, and the choice
between them was never made because one option was invisible.

---

## Should AER drive SDKs instead of CLIs? — **No, and the reason is contractual**

Both vendors ship an SDK, and on the technical merits the SDK path looks strictly better than shelling
out to a CLI. It was worth evaluating properly rather than assuming. **It is foreclosed, by policy
rather than capability**, and the constraint is one sentence in the setup steps:

> "**Unless previously approved, Anthropic does not allow third party developers to offer claude.ai
> login or rate limits for their products, including agents built on the Claude Agent SDK. Please use
> the API key authentication methods described in this document instead.**"

The Agent SDK authenticates with `ANTHROPIC_API_KEY`, Bedrock, Vertex, or Foundry. **AER Flow's stated
premise is subscriptions, not API keys** (CLAUDE.md: *"the project's whole point is working against
**subscriptions**, not API keys"*, and *"dropping in an API key to make a gate pass would test a
different auth path than the one the project exists to support"*).

So the existing architecture is **correct, and for the right reason**. Shelling out to an
already-authenticated CLI is not a compromise forced by ignorance of the SDK — it is the only path
consistent with the product's premise. That is worth stating plainly, because every other section of
this audit corrects something; this one confirms a decision that was already right.

### What the SDK would have bought, so the cost of the constraint is visible

| | SDK | CLI (what we have) |
|---|---|---|
| gate | in-process `canUseTool` + hook callbacks | hook via `--settings`, or an MCP server |
| `--bare` conflict (#521) | does not exist | real, and forecloses the gate |
| routing signal | typed events, Pydantic/TS types | `stream-json` parsed from stdout |
| session persistence | **`SessionStore` interface** — `append`/`load`, with S3/Redis/Postgres reference adapters | `~/.claude/projects/*.jsonl` on the local disk |
| cost/usage | per-turn and cumulative, exposed | `total_cost_usd` in the stream; `/usage` headless |
| language | Python / TypeScript | any |

`SessionStore` is the sharpest loss: it is a pluggable durable transcript store with a published
conformance suite, dual-write semantics, and a `mirror_error` event — very close to what the room model
needs, and unavailable on the terms we operate under.

### `agy`'s SDK — same answer, and this one is enforced in code

`agy` ships a Python SDK (`pip install google-antigravity`, 0.1.8). It was checked rather than
assumed, and it does not offer an escape from the constraint — it is **stricter**, because the
restriction is not a policy sentence but a code path.

The package was downloaded and **read without being run** (`pip download --no-deps`).
`google/antigravity/models.py` defines exactly two endpoint types, and validation *raises* without a
key:

```python
class GeminiAPIEndpoint(ModelEndpoint):
  def validate_endpoint(self) -> None:
    if not (self.api_key or os.environ.get("GEMINI_API_KEY")):
      raise ValueError(
          "A Gemini API key is required. Set it via"
          " GEMINI_API_KEY environment variable or via"
          " LocalAgentConfig(api_key=...)."
      )

class VertexEndpoint(ModelEndpoint):        # project + location, i.e. GCP ADC
  def validate_endpoint(self) -> None:
    if not (self.project and self.location):
      raise ValueError("For Vertex AI, a GCP project and location must be set.")
```

A search of every `.py` in the package for `keyring`, `oauth`, `keychain`, `secret_service`, or the
CLI's credential store returns **nothing**. The CLI stores its login in the OS keyring (Windows
Credential Manager / Keychain / Secret Service); the SDK never reads it. There is no subscription
path to find, so `Evidence.Absent` is safe here in a way it usually is not — the surface list is the
package's entire source, not a guessed flag name.

Both SDKs bundle a native binary (`google/antigravity/bin/localharness.exe`, 120 MB; the claude SDKs
"bundle a native Claude Code binary for your platform"). The SDK is not a different transport — it is
the same local harness with a typed wrapper and a **different auth requirement**. That is the whole
of the difference, and it is the part that disqualifies it.

**Conclusion: CLI for both vendors.** Not a compromise — the only subscription-compatible path
either vendor offers. The SDK question is closed.

### One thing to take from the SDK regardless

The SDK's `SessionStore` contract is a good design to copy even while implementing it ourselves:
required `append`/`load` keyed by `{projectKey, sessionId, subpath}`, optional `listSessions` /
`delete` / `listSubkeys`, entries treated as opaque ordered JSON, mirror writes best-effort with the
local copy authoritative, and a distinct event when mirroring fails. That is a well-worn shape for
exactly the problem 0015's durable gate and the room store both have.

---

## The Tier 1 + Tier 2 read (#527)

`pixi run vendor-survey` mirrors both corpora from their published indexes (claude `llms.txt` → 172
raw `.md` pages; agy `llms.txt` + `sitemap.xml` → 77 server-rendered pages) and harvests sentences
matching a fixed grammar (*skips, only, cannot, must, requires, before v, will become*) corpus-wide:
**249 pages / 7.0 MB → 1,475 unique constraint sentences**, tagged against AER's open questions with
page:line provenance; the per-page dispositions live in `docs/vendor-coverage.md`. 100% page coverage at ~1%
of the bytes — several findings below come from `glossary`, `channels`, `chrome`, `context-window`
and `desktop-scheduled-tasks`, pages a depth-first read would not have reached.

### 1. The strongest gate primitive is one we were not considering

`_meta["anthropic/requiresUserInteraction"]` — which **AER's own MCP server can set** — survives
every mechanism that defeats the alternatives: allow rules, *all* permission modes including
`bypassPermissions`, the auto-mode classifier, and a permissive hook. It offers no "don't ask again".
That is 0015's "needs you" semantic enforced by the vendor rather than by our discipline.

**The catch is architectural:** headless converts it to a hard deny (`MCP tool requires user
interaction; not supported via --permission-prompt-tool`), and `dontAsk` denies it too. A perfect
gate where a human surface exists; a hard block where none does. AER must decide which invocations
carry a human surface.

Second primitive, for non-MCP tools: **a hook's `"ask"` forces a prompt even in auto mode** — "the
classifier can still deny the tool call, but it can't approve the call silently."

### 2. `defer` cannot carry the durable gate

> "`defer` only works when Claude makes a single tool call in the turn."

The earlier end-to-end verification used a single-tool turn, so it passed. `defer` **silently does
not apply** when the model batches tool calls, which is the common case.

### 3. The blocking-MCP gate can be reaped mid-wait

An MCP call that sends **no response and no progress notification** for the idle window aborts. A
per-server `timeout` ≥ 1000 acts as a floor, or the server must emit progress. Without one of those,
a slow human produces an error instead of a gate. (Real permission *prompts*, by contrast, never
auto-resolve on idle.)

### 4. An API key silently disables the features AER is built on

> "Remote Control, `/schedule`, claude.ai MCP connectors, and notification preferences are **disabled
> when `ANTHROPIC_API_KEY` / `apiKeyHelper` / `ANTHROPIC_AUTH_TOKEN` is set, even if a Claude.ai
> login also exists**."

This upgrades **Credential Isolation (architecture rule 4)** from a premise to a *functional*
invariant: a stray key does not degrade gracefully, it removes AER's entire remote story.

### 5. agy's CLI hooks work — and the gate **is** symmetric

| location | result |
|---|---|
| `<workspace>/.agents/hooks.json` (workspace registered via `--add-dir`) | **loads and fires** |
| `~/.gemini/config/hooks.json` | **loads, fires, `deny` enforced** |
| `~/.gemini/antigravity-cli/hooks.json` | does not load — see [antigravity-cli#49](https://github.com/google-antigravity/antigravity-cli/issues/49) |

Verified positive: `Tool execution was blocked by the system gate policy (AER global gate test).`

That last row matches an open upstream issue: the CLI's own `/hooks` command *writes* to
`antigravity-cli/` while the loader *reads* `config/`, so anyone configuring hooks through the CLI
gets a file that is never read.

So agy's control surface is real and CLI-reachable: `allow`/`deny`/`ask`/`force_ask`,
`permissionOverrides`, `PreInvocation.injectSteps`, `PostInvocation.terminationBehavior`,
`Stop.decision:"continue"`, `Stop.fullyIdle`. **On several axes it is stronger than claude's.**

The root cause (a quote-escaping bug made the hook command exit 127 every time, silently) is the
"check the vendor's own logs before concluding absence" method rule. `agy` had written the answer to
disk the whole time: `hooks_manager.go:53] loaded 0 named hooks from 0 hooks.json file(s)`.

The CLI has a public issue tracker and changelog (`google-antigravity/antigravity-cli`), now on the
permanent source list.

### 6. Fan-out limits are now concrete (#503 items 4–5)

> **Two of these were later measured false — read the correction before using this paragraph.**
> Nesting is **not** off by default (one level runs with nothing configured), and the cap's
> documented default of 20 remains unverified. See
> [Group B](#group-b--fan-out-2026-07-25). Kept unedited because it is the record of what the
> vendor *documents*, which is still what a reader of the vendor's docs will believe.

Concurrent subagents cap **20** (`CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS`); nested subagents **off by
default**; nested teams **impossible**; per-teammate modes **cannot be set at spawn**; a parent's
`bypassPermissions`/`acceptEdits`/`auto` **applies to every subagent and cannot be overridden**.

Decisive for the design: **a teammate's background work cannot outlive the lead's process**, so
teammates make the lead a single point of failure — while `--bg` sessions survive detach entirely.

### 7. The vendor's changelog is a test plan for AER's own supervisor

This applied to `Baton.Daemon` while it supervised workers and survived restarts; #1420 deleted that
surface, and today no resident process supervises a worker turn — the harness (`baton` CLI, via
`baton run`) dispatches and drives each turn directly, with no daemon to restart. The lessons below
stay live as a test plan for whenever a resident process is built again (spec/baton.md §7/§8's
room-watcher): the vendor built the same thing and shipped the bugs. Stale lock **whose PID the OS
reused**; auto-upgrade **silently killing all live sessions**;
sleep/wake needing **clock-jump detection** rather than elapsed-idle; workers inheriting a **stale
`PATH`** and a **stale model** from the daemon rather than the dispatching shell and `settings.json`;
daemon handover judged by **embedded build timestamp**, not version string.

### 8. Smaller corrections worth carrying

- **`--add-dir` grants file access, not configuration.** Hooks load only from the process's own cwd
  `.claude/`, with **no parent-directory fallback** — so AER must control the worker's working
  directory or pass `--settings`. With #521 (`--bare` disables hooks even via `--settings`), the
  viable gate combinations are narrow.
- **Hooks on Windows run through Git Bash** and historically failed *silently* there. Windows is the
  primary development host.
- `PreToolUse`'s top-level `decision`/`reason` are **deprecated**; `PermissionRequest` fires **only in
  auto mode**; `PreToolUse` never fires for `EndConversation`, and typing `/skillname` bypasses it.
- **Two processes cannot write one transcript** — AER must serialise per session.
- **`usage.output_tokens` excludes subagent tokens**; whole-tree accounting needs `modelUsage`.
- **`errorCode: "credits_required"`** is a typed signal for *subscription* quota exhaustion (#479).
- **Transcript parsing is explicitly unsupported** — verified that AER does not do it.
- agy: **credentials are coupled to its background daemon** ("If the background daemon is locked or
  headless, the CLI cannot read credentials") — a headless agy daemon is an *auth* failure.
- Two more agy doc inaccuracies: `--cwd` is documented and **does not exist** (the real flag is
  `--add-dir`), and the live `PreToolUse` payload carries an **undocumented `modelName`** field with
  `transcriptPath` pointing at `transcript_full.jsonl`, not the documented `transcript.jsonl`.

---

## Verified by running it (#527, 2026-07-25)

Everything above this section is **documented** — a vendor claim. This section is what survived
being run, on `claude` 2.1.220 / `agy` 1.1.8 / Windows 11, all under `-p`.

Two rules held throughout: **one variable per test**, and **execution proven by a side effect**
(a sentinel file the tool writes) rather than by the model's account of what it did. Both exist
because this audit twice recorded a negative from an instrument that could not distinguish
"never fired" from "fired and failed".

These runs are **repeatable**, not disposable: `pixi run vendor-verify` (see
`tools/vendor-verify/`) re-runs them against whatever CLI versions are installed, carrying both
rules. Re-run it on every vendor version bump — the pinned versions above are what these results
were established against, and nothing here transfers to a later one for free.

#### The short version, for decision 0015

Five statements carry most of the weight below. Everything else is support or detail.

1. **Gate on the operation, never on the tool.** `--allowedTools` only pre-approves;
   `--disallowedTools` removes the named tool and the model **substitutes another and still
   succeeds**. The four mechanisms that actually stop an action — hook exit-2, an explicit `ask`
   rule, a hook's `permissionDecision: "ask"`, and `requiresUserInteraction` on MCP tools — all
   gate the operation, which is exactly why substitution doesn't defeat them. This is already a
   live defect in `src/` ([#529](https://github.com/aer-works/baton/issues/529)).
2. **Isolation has exactly three levers, and one of them is better than first recorded.**
   `--add-dir` carries no config at all; `--bare` disables hooks outright and has no remedy; but
   `CLAUDE_CONFIG_DIR` gives a worker a genuinely separate root, and a fresh root's `Not logged in`
   is fixed by one interactive `claude auth login` — **not** by copying credentials, so it stays
   Rule-4-clean. Per-worker config roots are on the table, priced at one sign-in each.
3. **Nothing about a fan-out is bounded by default.** Nesting is on, subagents inherit the
   parent's mode, and the top-level token count under-reports the tree. The concurrency cap and
   `--max-budget-usd` are real and are AER's to set.
4. **Headless removes the vendor's notification events — so AER must be its own notifier.** Neither
   `PermissionRequest` nor `Notification` fires under `-p`. Ten events do, including `PreToolUse`,
   `Stop` and the `Subagent*` pair. Since AER's gate is its own MCP tool, it already knows a
   decision is pending and needs no vendor event to tell it. 0018 should be written that way.
5. **The vendors are asymmetric in opposite directions.** claude has an uncircumventable consent
   primitive and per-project settings; agy has loop control and *only* project-scoped hooks. Neither
   is strictly better, and the design has to use each for what it actually provides.

### claude has two gate primitives, and both hold

| primitive | scope | result |
|---|---|---|
| **`_meta["anthropic/requiresUserInteraction"]`** | MCP tools | ✅ **survives `--allowedTools`, `acceptEdits`, and `bypassPermissions`** |
| **`PreToolUse` hook exit code 2** | any tool | ✅ **blocks even with an explicit `permissions.allow` entry for that tool** |

Together they cover the whole surface: the annotation gates AER's own MCP tools, exit-2 gates
everything else (`Bash`, `Write`, `Edit`). **0015 can be written on measured behaviour.**

For `requiresUserInteraction`, two tools were exposed that differed *only* in that field; in every
arm where the plain tool executed, the annotated one did not. For exit-2, the same tool and the
same allow rule were used in both arms, with only the exit code differing.

### `--json-schema` makes Architecture Rule 1 practical

Rule 1 says Flow must never parse conversation content to route. That is only workable if a worker
can be *made* to return a structure rather than asked nicely.

| arm | result |
|---|---|
| `--json-schema '{…verdict, confidence…}'` | ✅ `{"confidence": 99, "verdict": "yes"}` — exactly the declared shape, no extra keys |
| same prompt, no schema (the control) | prose; not JSON at all |

So the structure is the flag's doing, not the model's cooperation. **A worker's return can be a
typed record, which is what Rule 1's "explicit tool returns" needs.**

**Implementation note for `Baton.Vendors`:** `--json-schema` takes the schema **inline**, not a file
path. Passing a filename fails with `--json-schema is not valid JSON: Unexpected identifier "C"` —
which reads like a malformed schema rather than the wrong *kind* of argument, and cost a
debugging cycle here.

**Also worth knowing:** the CLI waits **3 seconds for piped stdin** on every invocation that does
not close it, then warns. Anything spawning it programmatically should redirect stdin from the
null device — three seconds per worker launch is real latency in a fan-out.

### Tool restriction is not a capability boundary — the model routes around it

Started as a loose end (a subagent used `Write` when its parent was launched with
`--allowedTools Task`) and turned out to be the most consequential gate result here. Same prompt in
all three arms; a `PreToolUse` hook matching `.*` records **which tool actually ran**, not merely
whether the file appeared.

| arm | file created | tools actually invoked |
|---|---|---|
| `--allowedTools Write` | ✅ | `Write` |
| `--allowedTools Task` + `acceptEdits` | ✅ | **`Write`** — a tool the list omits |
| `--disallowedTools Write` + `acceptEdits` | ✅ | **`Bash`, `ToolSearch`** — never `Write` |

Two distinct facts, and they point the same way:

1. **`--allowedTools` is a pre-approval list, not a ceiling.** A permissive mode reaches tools the
   list omits. It cannot bound what a worker may do.
2. **`--disallowedTools` genuinely removes the tool — and the goal is reached anyway.** `Write`
   was never invoked. The model substituted `Bash` and created the file regardless.

**Consequence: AER must never treat a tool list as a security boundary.** Restricting `Write`
does not prevent writing; it only changes which tool does the writing. The only mechanisms
measured to actually stop an action are the four always-fires primitives — they gate on the
*operation*, which is why substitution does not defeat them.

The first version of this check looked only for the file and returned INCONCLUSIVE. It could not
tell "`Write` was permitted" from "the model used `Bash` instead" — and the substitution was the
finding, not noise. Third time this audit that the instrument, not the vendor, was the thing
that needed fixing.

#### This is a live defect in `src/`, not a hypothetical — [#529](https://github.com/aer-works/baton/issues/529)

`ClaudeWorkerAdapter.BuildDisallowedTools` maps each withheld grant category to the tools that
reach it. The categories are independent, so a grant with `WriteFiles = false` and
`RunShellCommands = true` emitted exactly `--disallowedTools Edit,Write,NotebookEdit` and left
`Bash` available. (Since [#649](https://github.com/aer-works/baton/issues/649) those three names
travel on the `PreToolUse` hook instead, so the hook can allow the write landing in
`BATON_OUTPUT_DIR` — the substitution below is unaffected, since it is about what `Bash` reaches.)
Running **that exact string**:

```
--permission-mode acceptEdits --disallowedTools "Edit,Write,NotebookEdit"
  file created : YES
  tools invoked: Bash, Read
```

**`Bash` alone defeats three of the four categories** — it writes files, reads files (`cat`), and
reaches the network (`curl`). The method's own XML doc already warns that denial is "by enumeration,
not default-deny", but scopes that to tools *outside* the four categories. The hole is **inside**
them.

`AgyWorkerAdapter` is **not** affected, and for a reason this audit independently verified:
`agy -p` fails closed, and the adapter refuses shell and network grants outright rather than
approximating them — so there is no substitute tool to reach for. The asymmetry the adapter's
comments describe is real.

**The fix direction follows from the primitives, not from a longer list.** A longer deny list is a
treadmill: any new filesystem-touching tool reopens it. The four always-fires primitives gate on
the *operation*, which is exactly why substitution does not defeat them.

### The vendor asymmetry runs the other way

An earlier section argued agy's control surface is "on several axes stronger than claude's".
**Measurement reverses it:**

| | claude | agy |
|---|---|---|
| uncircumventable consent | ✅ `requiresUserInteraction` | ❌ `force_ask` is defeated by `--dangerously-skip-permissions` |
| blocks despite allow rules | ✅ hook exit-2 | ✅ hook `deny` (and it surfaces the hook's reason) |
| grant permission from a hook | — | ❌ `permissionOverrides` did not grant under `-p` |
| inject into the trajectory | — | ❌ a `toolCall` step is documented but not implemented (`gate.agy-toolcall-injection-does-not-work`, §"Proving the gate fired") |
| refuse to let the loop end | — | ✅ **`Stop.decision:"continue"` works** |

agy's distinctive, working contribution is **loop control**, not permission control.

**Both directions of that loop control are now measured.** The `terminate` redo used a task that
*cannot* finish in one invocation — three files created one at a time, each proven by its own
presence on disk — with the identical task under `force_continue` as the control:

| `terminationBehavior` | invocations | files created | reached `FINISHED` |
|---|---|---|---|
| `force_continue` | **7** | **3 / 3** | ✅ |
| `terminate` | **1** | **1 / 3** | ❌ |

So agy can both refuse to let a loop end (`Stop.decision: "continue"`) and cut one short
mid-task. **claude has no equivalent**, and this is the one axis where agy is genuinely the
stronger tool — worth remembering, because the permission-surface comparison runs the other way.

### Other claims that held

| claim | result |
|---|---|
| `agy -p` fails closed on an ungated tool | ✅ auto-denies, with a structured remedy naming the rule to add |
| `agy` `permissions.allow` honoured under `-p` ([#548](https://github.com/google-antigravity/antigravity-cli/issues/548)) | ✅ **does not reproduce** — the rule was honoured |
| `agy` hook `deny` on `invoke_subagent` ([#640](https://github.com/google-antigravity/antigravity-cli/issues/640)) | ✅ **does not reproduce** — the deny held |
| claude `defer` under `-p` | ✅ `stop_reason: tool_deferred`, and no file contents leaked, so the tool truly did not run |
| claude subagent inherits the parent allowlist ([#28584](https://github.com/anthropics/claude-code/issues/28584)) | ✅ current docs hold; the subagent read the file |
| a silent blocking MCP tool is reaped mid-wait | ✅ **survived 200s** and returned its result — the blocking-gate design is viable |
| `--bare` breaks subscription auth | ✅ re-confirmed against 2.1.220 |
| undocumented `modelName` in agy hook payloads | ✅ present in `PreToolUse`, `PreInvocation`, `Stop` |


### Group A results (2026-07-25)

| claim | result |
|---|---|
| `--add-dir` grants file access but loads **no** hooks config | ✅ **confirmed** — a hook in the added dir's `.claude/settings.json` fired **0 times** and the write proceeded |
| an explicit `ask` rule gates even in `bypassPermissions` | ✅ **confirmed** — the tool did not execute |
| `usage.output_tokens` excludes subagent tokens | ✅ **confirmed** — top level **882** vs `modelUsage` summed **1130** (a 22% shortfall on one subagent) |
| agy `PostInvocation.terminationBehavior: "terminate"` | ✅ **confirmed on the redo** — see below. (The first attempt was inconclusive: the task finished inside one invocation, so terminating after it was indistinguishable from normal completion.) |
| a hook's `permissionDecision: "ask"` forces a prompt in `auto` mode | ✅ **confirmed** — the same hook returning `allow` wrote the file; returning `ask` did not. A **fourth** always-fires path, and the polite one: unlike exit-2 it is a request, not a hard block. |
| `PermissionRequest` fires **only** in auto mode | ❌ **the claim itself was mis-transcribed, and the corrected version is worse.** See below. |

#### `PermissionRequest` does not fire under `-p` — and this bounds decision 0018

The backlog row said "fires only in auto mode". The docs say no such thing: `PermissionRequest`
fires **"when a permission dialog appears"**, and it is `PermissionDenied` that is tied to the auto
classifier. Under `-p` **no dialog ever appears**, so the corrected reading predicts it never fires
headless at all — which is what was measured.

The **discovery control** is what makes this a result rather than another silent negative. The same
hook command was registered on `PreToolUse` in the *same* settings file:

| mode | `PreToolUse` | `PermissionRequest` | `PermissionDenied` |
|---|---|---|---|
| `auto` | **1** | 0 | 0 |
| `acceptEdits` | **1** | 0 | 0 |

`PreToolUse` firing proves the settings file was loaded and the `Bash` matcher was right, so the
zero is the event genuinely not occurring — not a wrong matcher. Without that arm the two are
indistinguishable, which is the same instrument failure that produced the wrong agy-hooks
conclusion earlier in this audit.

**Consequence for 0018:** its notification hook cannot be `PermissionRequest` if AER spawns the
vendor CLI headless, because the event does not exist on that path. The verified always-fires
primitives below are the surface that *does* fire under `-p`, and the gate has to be built on those.

#### The whole headless event surface, measured in one run

Leaving 0018 with "not `PermissionRequest`, then what?" would have been half a finding. So all 23
documented hook events were registered with the same logging command in one settings file, against
one task that writes a file, reads it back, runs a shell command and spawns a subagent.
`PreToolUse` and `Stop` are the built-in controls — if neither fired, every zero would be
meaningless.

**Fires under `-p` (10):** `SessionStart` · `UserPromptSubmit` · `InstructionsLoaded` ·
`PreToolUse` · `PostToolUse` · `PostToolBatch` · `MessageDisplay` · `SubagentStart` ·
`SubagentStop` · `Stop`

**Silent, and the condition genuinely arose:** `PermissionRequest` · `PermissionDenied` ·
`Notification`

**Silent only because this task never created the condition** — untested, *not* absent:
`PostToolUseFailure` (nothing failed) · `PreCompact` / `PostCompact` (no compaction) ·
`StopFailure` (no API error) · `TaskCreated` / `TaskCompleted` (no `TaskCreate`) · `Elicitation`
(no MCP server) · `CwdChanged` · `ConfigChange` · `UserPromptExpansion` (no slash command)

That split is the point. A single "did not fire" list would have quietly asserted eleven things the
run never tested — the same conflation that produced the wrong agy-hooks conclusion.

**`Notification` is silent too**, so **both** obvious notify paths for 0018 are unavailable
headless. The resolution is architectural rather than a third event: `PreToolUse` fires reliably,
and AER's gate is *its own MCP tool*. **AER is the notifier.** It does not need the vendor to tell
it that a decision is pending, because AER is the thing creating the decision. 0018 should be
written that way rather than hunting for a vendor event that fires headless.

**`PermissionDenied` was left open here, and is now resolved.** It also logged zero, but nothing in
this run established that a denial ever occurred — `node --version` may simply have been allowed.
That was an unresolved arm, not a second finding, and it stayed that way until a check was built
that could tell the two apart (below).

#### `Elicitation` **does** fire headless — the one positive in the group

`--only gate.elicitation-hook-event-fires`. The `Elicitation` row above sat on the untested list
because the measuring run registered no MCP server: nothing could elicit, so its zero was about the
task, not the vendor. Re-run with the probe elicitation server attached and the server's **own**
issued-sentinel as the control — independent of both the hook and the model — it fires.

| control | value |
|---|---|
| `PreToolUse` fired | **3** — the settings file loaded |
| server issued `elicitation/create` | **true** — per the server's sentinel file |
| `Elicitation` hook fired | **1** |

**It changes nothing, and that is worth saying plainly.** AER *is* the MCP server behind its own
gate, so it already holds the pause before any hook could report it — the event is a second view of
something AER authored. It would matter for an elicitation AER did *not* author, from a server the
operator configured, but **whether such a server is even loaded alongside `--mcp-config` is
unresolved**: this run tried to read the session's server list off the stream-json init event and
did not get one, which is recorded as *not observed* rather than as "no other servers." That
distinction is the whole point — `mcp_servers` is absent from the `--output-format json` result
object entirely, so the obvious read would have returned an empty list that looks exactly like the
answer being hunted.

#### `PermissionDenied` does not fire headless either — measured against real denials

`--only gate.permission-denied-fires`. Two arms, one variable: `permissions.allow` versus
`permissions.deny` on `Write`, with both `PreToolUse` and `PermissionDenied` registered to the same
logging command in the same file.

| arm | `PreToolUse` | `PermissionDenied` | `permission_denials` | file written |
|---|---|---|---|---|
| **control** — `allow` | **1** | 0 | 0 | **yes** |
| `deny` | **3** | **0** | **3** | no |

The allow arm carries the whole check: it proves the settings loaded, the registration is right, and
the model does reach for `Write` on this prompt. The deny arm then shows the denial genuinely
happened — the CLI's own `permission_denials` records three of them — and `PermissionDenied` still
logged nothing. **So the zero is a result, and the third notification candidate is gone.**

Worth recording *how* this check first failed, because it is the suite's own rule catching the
suite: the initial version registered the hooks with `matcher: ".*"` and reported `PreToolUse
fired=0`. It returned **INCONCLUSIVE**, not "PermissionDenied does not fire" — the control refused
to let a harness bug be published as a vendor finding. The fix was to drop the matcher entirely,
the form `gate.headless-event-surface` had already measured firing.

### Group B — fan-out (2026-07-25)

| claim | result |
|---|---|
| the parent's permission mode covers its subagents | ✅ **confirmed** — under `--permission-mode acceptEdits` the subagent's write landed; under `default`, with the prompt, tools and target identical, it did not. The mode reaches the child. |
| nested subagents are **off by default** | ❌ **contradicted** — see below |
| `CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS` bounds concurrent subagents | ✅ **confirmed** — peak overlap tracked the cap exactly (2 and 6) with eight started in both arms. The documented **default of 20 is still unverified**; neither arm ran uncapped. |
| `--max-budget-usd` stops a session rather than only reporting overrun | ✅ **confirmed** — `$0.001` exits **1** with `subtype: error_max_budget_usd`; the unbudgeted control finished the same task on exit 0 |

#### `CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS` sets a real ceiling

Measured as **peak overlap**, not subagent count: `SubagentStart` and `SubagentStop` each append a
timestamp, and the two are interleaved into a timeline. Both arms asked for eight subagents in one
parallel batch and both **started all eight** — so fan-out pressure was identical and the cap is
the only variable.

| `CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS` | started | peak concurrent |
|---|---|---|
| `2` | 8 | **2** |
| `6` | 8 | **6** |

Peak tracks the cap exactly. **AER can bound worker fan-out through this variable** rather than
having to serialise dispatch itself.

**Scope note — the documented default of 20 is NOT verified by this.** Neither arm ran uncapped.
A first attempt compared cap=2 against no cap and could not conclude anything: the capped arm
started only two subagents in total, which is equally consistent with the cap holding and with the
model simply not fanning out. Two capped arms fixed that; the default remains an open row.

#### Nesting is *not* off by default — one level is already allowed

Measured by counting what the CLI actually started: a `SubagentStart` hook appends one line per
spawn. Same prompt in all three arms, only `CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH` differing.
**Two independent runs produced identical numbers.**

| `CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH` | subagents actually started |
|---|---|
| unset (the default) | **2** |
| `1` | **1** |
| `2` | **2** |

The variable works — capping at 1 really does prevent the nested spawn. **The default is not 1.**
A subagent can spawn its own subagent with nothing configured.

Getting here took three instruments, and the two discarded ones are the useful part of the record.
Reading the model's prose proves nothing: it will describe a nested spawn it never performed. A
sentinel file written by the innermost agent is better but still ambiguous — **the middle subagent
can simply write that file itself**, producing a byte-identical result. Only counting spawns
separates the cases. Each redo came from asking what *else* could produce the same observation.

**Consequence for #503 items 4–5:** a fan-out is not one level deep unless AER makes it so.
Concurrency budgets, cost attribution, and the gate all have to hold for a tree of unknown depth,
or AER must set `CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH=1` explicitly and stop assuming the default.

**On budgets:** `error_max_budget_usd` is a structured, machine-readable stop, so **AER can
delegate per-session budget enforcement to the vendor** rather than implementing its own. That
pairs with the `usage.output_tokens` shortfall above: the vendor is the reliable source for spend,
and AER's own arithmetic over the top-level field is not.

**Consequence for #503 items 4–5:** a fan-out inherits the lead's mode, so **AER cannot rely on a
subagent being more constrained than the session that spawned it**. Whatever gate the lead runs
under is the gate the whole tree runs under.

### Group D — agy permissions are global-only, so hooks are its only project-scoped gate

Another mis-transcribed backlog row. It read "three permission scopes (Project / Shared / Global)
and their merge order". The docs describe three access **lists** — `deny`, `ask`, `allow`,
precedence **Deny > Ask > Allow** — inside **one** file, `~/.gemini/antigravity-cli/settings.json`.
No project scope is documented. Measurement agrees:

| where the identical rule was placed | honoured under `-p`? |
|---|---|
| `~/.gemini/antigravity-cli/settings.json` (global) — **the control** | ✅ |
| `<project>/.agents/settings.json` | ❌ |
| `<project>/.gemini/antigravity-cli/settings.json` | ❌ |

The global arm is what makes the two negatives mean anything: it uses the same rule string, so
"not honoured" cannot be "the rule was malformed".

**Put beside the earlier hooks result, this is the actionable shape of agy's control surface:**

| mechanism | project-scoped? |
|---|---|
| hooks (`.agents/hooks.json`) | ✅ **yes** — verified earlier |
| permission rules | ❌ **no** — global settings only |

So **AER cannot give an agy worker its own permission rules without editing the operator's global
file**, which Architecture Rule 4 and ordinary hygiene both forbid. For agy, **the hook is the only
gate AER can install per-worker.** For claude the position is the opposite — `--settings` carries
both, and `--add-dir` carries neither.

*(The operator's `settings.json` was backed up byte-exact and restored; sha256 verified identical
before and after, by the check and again independently.)*

### `--session-id` is guarded by an existence check, not a lock

The register said "two processes cannot write one transcript". Measured, that is **not what
protects the transcript** — and the difference matters, because AER was going to rely on it.

Three arms, run twice with identical results:

| arm | outcome |
|---|---|
| two processes, two different fresh ids (flakiness control) | both succeeded |
| **two processes, the same id, concurrently** | **both succeeded** |
| the same id twice, **sequentially** | first succeeded, **second refused** |

So the guard is an **existence check on a persisted session**, and a concurrent pair races straight
past it: neither has committed the session yet, so both see the id as free and both proceed. Reuse
is refused only once the session exists.

The third arm is what makes this readable. Without it, "the concurrent pair was refused" and "an id
cannot be reused at all" are the same observation — and here the concurrent pair *wasn't* refused,
which only means something because sequential reuse *was*.

**Consequence: there is no vendor-side mutex.** Two workers handed the same session id will both
run and both write. This was `Baton.Daemon`'s obligation to enforce while it hosted interactive
sessions; that surface is deleted (#1420), and no component in this repo runs a session against
this vendor behavior today. If a future resident process needs single-writer semantics on a
session, it has to enforce them itself — the vendor's guard will not lose loudly, it will lose
silently, and only under the concurrency that makes it matter.

### Group C — `CLAUDE_CONFIG_DIR` costs the subscription login (2026-07-25)

Architecture Rule 4 forbids redirecting a vendor CLI's config directory. That was a design
position; it is now a measurement.

| arm | result |
|---|---|
| control (variable unset) | ✅ answered, exit 0 |
| `CLAUDE_CONFIG_DIR=<fresh temp dir>` | ❌ exit 1, `terminal_reason: api_error`, result **`"Not logged in · Please run /login"`** |

The variable is fully honoured — the redirected directory was populated with `.claude.json`,
`sessions/`, `projects/` and `backups/`, so the CLI really did adopt it as its config root. The
**credentials are what live under that root**, and a fresh root has none.

**This is not the same mechanism as `--bare`** (#262, #521), which actively *skips* OAuth and
keychain reads. `--bare` has no remedy. A redirected config root does.

#### Correction: a redirected root can simply be logged in, and that is Rule-4-clean

This section first concluded "AER cannot give a worker its own config root". **That was wrong**,
and the error was assuming a fresh root's `Not logged in` is a property of the flag rather than of
the root being new. The docs say so plainly, in the credential-management section:

> On Windows, credentials are stored in `%USERPROFILE%\.claude\.credentials.json` … **If you've set
> the `CLAUDE_CONFIG_DIR` environment variable on Linux or Windows, the `.credentials.json` file
> lives under that directory instead.**

Verified non-interactively with `claude auth status`, which reports per-root and **starts no
session**:

| config root | `loggedIn` | `authMethod` |
|---|---|---|
| the operator's real root | `true` | `claude.ai` |
| a fresh redirected root | `false` | `none` |

`claude auth login` is a real CLI subcommand, not only a TUI slash command, so a root is made
usable with a one-time interactive sign-in:

```
$env:CLAUDE_CONFIG_DIR = "<root>"; claude auth login
```

**This is compatible with Architecture Rule 4, not a violation of it.** Rule 4 forbids *AER*
reading, copying, forwarding or storing a credential. A human signing in to a root themselves is
none of those — the vendor CLI authenticates itself, exactly as Rule 4 describes. What Rule 4 rules
out is AER *copying* credentials into a new root, which remains true and remains forbidden.

**So per-worker config roots are an available design option**, priced at one interactive login per
root. The vendor documents this as the supported pattern: setting `CLAUDE_CONFIG_DIR` makes an
instance "run as a separate instance with its own sessions", and the hosting guidance recommends a
per-tenant config directory.

**Resolved below, same day — a real attempt was made.** This was written before the "Worker identity"
measurement further down this document: a second, interactive login into a fresh `CLAUDE_CONFIG_DIR`
root did not displace the first (`loggedIn: true` held on both), both roots reported the same account,
and two concurrent `-p` runs — one per root — both succeeded. A second concurrent login against the
same subscription **is** permitted, at least under this test's shape. What still isn't established is
how it interacts with any parallel-session limit at higher concurrency than two.

**`claude auth status` is independently useful to AER**: a structured, non-interactive readiness
probe that spends no subscription usage, so a worker's root can be checked *before* dispatch rather
than discovering the problem as a failed run.

**Consequences.**

**AER must own the worker's working directory, or pass `--settings`.** `--add-dir` cannot carry a
gate. With #521 (`--bare` disables hooks even via explicit `--settings`), the viable combinations
are now measured rather than inferred: **no `--bare`, and either cwd control or `--settings`.**

**claude has four verified always-fires primitives**, not one: `requiresUserInteraction` for MCP
tools, hook exit-code-2 for any tool, an explicit `ask` rule for any tool, and a hook returning
`permissionDecision: "ask"`. Independent mechanisms with independent failure modes, which is what a
gate design wants. All four were verified **under `-p`** — which matters, because
`PermissionRequest`, the event 0018 assumed it could notify on, does *not* fire there.

**Any cost surface that sums top-level `usage.output_tokens` under-reports every fan-out.** The gap
was 22% for a single subagent and grows with the tree. `modelUsage` is the whole-tree figure (#479).

### MCP `elicitation` — portable, uncircumventable, and **still not a channel to a human**

`claude` declares the MCP-standard `elicitation` capability (`{'roots': {'listChanged': True},
'elicitation': {}}`) and honours an `elicitation/create` sent from a server mid-`tools/call`. Three
arms, one variable — the permission mode:

| arm | client's answer | gated tool body ran |
|---|---|---|
| `--allowedTools` (tool pre-approved) | `cancel` | **no** |
| `--permission-mode bypassPermissions` | `cancel` | **no** |
| `--dangerously-skip-permissions` | `cancel` | **no** |

`pixi run vendor-verify -- --only gate.elicitation`. The server writes `ELICITED.json` when it
issues the request and `CALLED_elicit_tool` only if the answer approves, so "the body did not run"
is a file that does not exist, not the model's account.

**So it is uncircumventable — no permission mode approves it.** That is the property
`requiresUserInteraction` has, achieved through a mechanism that is in the MCP specification rather
than a vendor's `_meta` namespace.

#### And it is portable — **measured on agy, not inferred from claude**

Writing "so it holds for any spec-conformant worker" off the claude run would have been an
inference, and the neighbouring mechanism already falsifies that exact inference: `force_ask`
survives `--dangerously-skip-permissions` on claude and **collapses** on agy
(`agy.force-ask-defeated-by-skip`). Two vendors disagreeing about what a bypass flag bypasses is
the norm in this audit, so portability had to be run.

`pixi run vendor-verify -- --only agy.elicitation`. agy has no `--mcp-config`; the server is
registered workspace-locally in `.agents/mcp_config.json` (`agy__mcp.md:73`), so nothing global
was touched.

| arm | client's answer | gated tool body ran |
|---|---|---|
| `--dangerously-skip-permissions` | `cancel` | **no** |
| `--mode accept-edits` + skip-permissions | `cancel` | **no** |

agy declares the capability with *more* sub-structure than claude —
`'elicitation': {'form': {}, 'url': {}}` against claude's `'elicitation': {}` — and honours it in
the mode that defeats its own `force_ask`. **Elicitation is the one gate primitive measured
uncircumventable on both vendors.**

A note on how the first run of this check failed, because it is the same lesson a third time: both
arms are deliberately *permissive*. A default `agy -p` arm returned INCONCLUSIVE — the control tool
never ran, because agy auto-denies an ungated tool headless (`agy.fails-closed-headless`). That arm
would have measured agy's headless deny, which was already known, and said nothing whatever about
elicitation. `CAPS.json` is what made the difference diagnosable: written at `initialize`, before
any tool call, it separates *the server never loaded* from *the server loaded and agy declined the
tool*. Without it the run looks like a broken instrument.

**Read the answer column before designing on it.** Every arm answered `cancel` — because under `-p`
there is no human to ask, and the client says no on their behalf. Elicitation headless is a
**fail-closed deny**, not a way to reach a person. It is a *stronger deny* than
`requiresUserInteraction` (structured `action: "cancel"` rather than an error string) and a
portable one, but it does not by itself carry a gate that a human later opens.

**What this means for 0015, stated plainly so it is not mis-read later:** neither
`requiresUserInteraction` nor `elicitation` is the durable gate. Both are *refusals* that no mode
can override. The thing that actually holds a worker while a human decides is the third mechanism —
**AER's own MCP server declining to respond to `tools/call` until AER's UI returns an answer**,
which is the one measured to survive 200s and the one that needs a `timeout` floor or progress
notifications so it is not reaped mid-wait. Elicitation's role is to make the refusal *portable and
unbypassable*; the blocking response is what makes the gate *durable*.

### SEP-1036 URL-mode elicitation — the non-blocking gate, standardized, and agy honours it

[SEP-1036](https://modelcontextprotocol.io/community/seps/1036-url-mode-elicitation-for-secure-out-of-band-interactions)
(**Final**, Standards Track) adds `mode: "url"` to elicitation. The server hands the client a URL,
the user opens it in a browser, and the interaction happens **out of band — bypassing the MCP client
entirely**. The SEP is explicit about the property that matters here:

> *"Why doesn't the server block (wait) on the elicitation to complete? URL mode elicitation
> requests are asynchronous or 'disconnected' flows by design… Payment flows, external
> authorization, etc. can take minutes or more to complete, and in some cases never complete at
> all."*

Completion is reported by a `notifications/elicitation/complete` notification carrying the
`elicitationId`; clients **MAY** auto-retry on it, and **SHOULD** offer a manual way to continue if
it never arrives. A server may also return `URLElicitationRequiredError` (code `-32042`), which the
spec says the client must treat as equivalent to an `elicitation/create`.

**Why this matters more than any other page in the corpus.** The durable gate this audit derived —
*persist the pause, release the call, let a human answer later* — is not an AER invention that needs
building from nothing. It is a Final SEP with a wire format. It also removes the constraint that
made the blocking gate awkward: no idle reaper, no `timeout` floor, no 200 s ceiling, because
nothing is being held open.

**The vendor split is spec-defined, not decorative.** Per the SEP's backwards-compatibility clause,
a bare `elicitation: {}` means **form mode only**:

| vendor | declared | modes |
|---|---|---|
| `claude` | `'elicitation': {}` | **form only** |
| `agy` | `'elicitation': {'form': {}, 'url': {}}` | **form and url** |

This retro-explains a difference recorded earlier in this document as agy having "more
sub-structure". It is not richer decoration; it is a different capability set, and the earlier
phrasing under-described it.

**Measured, because declaring is not honouring** — `pixi run vendor-verify -- --only agy.url-mode-elicitation`:

| what | result |
|---|---|
| agy declares `elicitation.url` | **yes** |
| agy accepts a `mode: "url"` `elicitation/create` mid-`tools/call` | **yes** — routed through the same three-action model |
| the client's answer, headless | `cancel` |
| the gated tool body ran | **no** |

**The limit of this measurement, stated plainly.** Headless there is no human, so the client
cancels. What is established is that agy **accepts and routes** a url-mode request and that the gate
holds when the answer is not `accept`. What is **not** established: that an interactive agy surfaces
the URL to a person, that `notifications/elicitation/complete` triggers a retry, or that the
out-of-band round trip works end to end. Those need a human and are the natural next live-smoke
item — not something an agent session can close.

#### How page-read-state is actually computed

`vendor_survey.py`'s `PENDING-DEPTH` disposition is a **recommendation** (score ≥ 10), not an
attestation that someone read the page — a script that runs before anyone reads anything cannot know
that. `tools/audit-completeness`'s `step2_corpus` computes the real read-state instead, by checking
whether each depth-flagged page is actually cited in this audit's prose — the current split is
printed on every run, relevance-ordered (run it rather than trusting a number transcribed here;
#952 removed the snapshot this sentence used to carry). Citation is weaker evidence than an
attestation, but it is recomputed every time rather than recorded once and trusted.

### The remaining 30 depth-flagged pages, each with a disposition

The join above left 30 depth-flagged pages uncited. Each is dispositioned here so the population is
closed rather than trailing off. Two produced constraints that change how AER launches a worker; the
rest are recorded as out of scope **with the reason**, because "we didn't read it" and "we read it
and it does not apply" are different claims and only one of them is finishable.

**`claude/mcp` — two launch constraints, both in the same family as the hook-discovery one.**

> *"For security reasons, Claude Code prompts for approval before using project-scoped servers from
> `.mcp.json` files."*

Project-scoped servers sit at `⏸ Pending approval (run claude to approve)` until somebody approves
them **interactively**. So **AER must never register its gate server via `.mcp.json`** — a
headless-spawned worker would start with the gate silently not loaded, which is precisely the
"configured, running, never consulted" failure 0015 already names as the most dangerous shape. The
`--mcp-config` path this audit's own checks use loads without approval and is the correct one; every
elicitation measurement above is incidental evidence that it works headless.

The three MCP scopes are also worth stating because only one of them is AER's: `local` (default,
`~/.claude.json`, per-project, private), `project` (`.mcp.json`, shared, **approval-gated**), `user`
(`~/.claude.json`, all projects). AER wants none of them persistently — it wants `--mcp-config`, per
spawn, touching no file the operator owns.

**`mcp/seps__1686-tasks` + `mcp/seps__2663-tasks-extension` (both Final) — a second non-blocking
pattern.** The `tasks` primitive returns a task handle instead of a result, for "call-now,
fetch-later" execution, queryable up to a server-defined duration after completion. Same shape as
SEP-1036 for the durable gate. **Neither vendor declares a `tasks` capability today** (measured —
`CAPS.json` in both elicitation checks shows `roots` and `elicitation` only), so it is a migration
target, not an option. It reinforces 0029's "build it to migrate" clause rather than changing it.

The other 26, by group:

| pages | disposition | why |
|---|---|---|
| `claude/llm-gateway-connect`, `llm-gateway-rollout`, `llm-gateway-protocol`, `claude-apps-gateway-config`, `claude-apps-gateway-deploy` | **out of scope — Rule 4** | All describe routing Claude Code through an API gateway with a credential variable. AER never holds a vendor credential and works against subscriptions, so this whole surface is the thing the product exists not to do. Read far enough to confirm that, which is also finding 4's territory: setting a key silently disables Remote Control. |
| `mcp/docs__tutorials__security__security_best_practices` | **read — no change** | Confused-deputy and token-passthrough attacks, both concerning servers that proxy third-party auth. AER's gate server proxies no credential, so the mitigations do not bind it. Relevant if AER ever adds a connector; noted, not actioned. |
| `mcp/seps__2567-sessionless-mcp`, `seps__2575-stateless-mcp`, `seps__2243-http-standardization` | **read — no change** | Transport and state-handle proposals for HTTP servers. AER's gate is stdio and already required to hold no state across spawns (claude starts it twice), so these prescribe what AER does anyway. |
| `mcp/specification__2025-11-25__schema`, `mcp/docs__learn__client-concepts`, `mcp/docs__develop__clients__client-best-practices` | **read — no change** | Client-side material (progressive tool discovery, client concepts) and the raw schema. AER is an MCP **server**, not a client; the schema was consulted for the elicitation shapes and matched what was measured. |
| `mcp/seps__1577--sampling-with-tools`, `mcp/seps__2596-spec-feature-lifecycle-and-deprecation`, `mcp/seps__2148-contributor-ladder` | **out of scope** | Process and sampling proposals. `2148` is the MCP project's own governance ladder and touches no product surface. |
| `claude/mcp-quickstart`, `claude/agent-sdk/mcp`, `claude/agent-sdk__modifying-system-prompts` | **read — no change** | Restatements of material already covered by `claude/mcp` and the SDK pages the audit does cite. The SDK route stays rejected for the contractual reason recorded above. |
| `claude/whats-new__index`, `claude/whats-new/2026-w19`, `claude/whats-new/2026-w23`, `claude/whats-new/2026-w26`, `claude/whats-new/2026-w27`, `claude/whats-new/2026-w28` | **superseded by a better source** | Weekly digests of the changelog. `claude/changelog` is cited and depth-read, is the authoritative version, and scored 1543 against these pages' 8–17. |
| `agy/github-CHANGELOG`, `issues/trackers` | **standing sources, re-read per bump** | Not one-time reads: both are moving feeds already on the permanent source list, and both have already produced findings (agy's hook-loader bug, upstream #548/#640). Their disposition is "monitored", which no single read closes. |

### A hook that cannot run fails **open**, and says nothing (#530)

Decision 0029 makes a `PreToolUse` hook mandatory on every worker AER spawns, and its `Rests on`
table carried this as **assumed**: *hooks on Windows run through Git Bash and have historically
failed silently there.* It is now **measured**, and the answer is the unwanted one — though not for
the documented reason.

`pixi run vendor-verify -- --only gate.broken-hook-fails-open`. Six arms, one variable each, same allow rule
and same target throughout. The two working-hook arms are the discovery control: without them, a
broken arm's result cannot be told from a settings file that never loaded.

| arm | tool proceeded | CLI reported anything |
|---|---|---|
| **control** — working hook, `exit 2` | **no**, blocked | yes |
| **control** — working hook, `exit 0` | yes | — |
| hook script path does not exist | **yes** | **no** |
| interpreter does not exist | **yes** | **no** |
| script written **CRLF**, in a path containing a **space** | **no**, blocked | yes |
| `exit 1` — non-zero, non-2 | yes | — |

**Two findings, and the second corrects this project's own expectation.**

1. **A hook whose command cannot execute fails open, silently.** No error, no warning, no mention in
   `--output-format json`. The tool runs. This is precisely the shape 0015 names as the most
   dangerous vendor behaviour to miss — *a gate that is configured, running, and never consulted is
   indistinguishable from a working one* — and it is now demonstrated rather than feared.
2. **CRLF line endings and a space in the path both survive.** The vendor's documented Git Bash
   failure mode is not what bites. The assumption AER was carrying named the wrong cause, which
   would have sent a mitigation at the wrong target: normalising line endings would have felt like
   a fix and prevented nothing.

`exit 1` allowing the tool is correct and documented — only `exit 2` blocks.

**What this settles for 0029.** The startup self-check that record calls for is not prudence, it is
the only thing standing between AER and a permanently open gate: the failure mode is *silent*, so
nothing short of proving the hook fires can detect it. Two properties follow for the
implementation — the self-check must assert a **side effect the hook produced**, never that the
settings file was written; and it must run **per worker spawn**, because the failure is per-process
(a wrong path, a missing interpreter on that host) rather than per-configuration.

#### The same question, asked of `agy` — because the answer could not be carried over

`--only agy.broken-hook-fails-open`. Four arms, same shape, `.agents/hooks.json` and the
`run_command` matcher.

| arm | command proceeded | CLI said anything about a hook |
|---|---|---|
| **control** — working hook returning `deny` | **no**, blocked | no |
| **control** — working hook, `exit 0` | yes | no |
| hook script path does not exist | **yes** | no |
| interpreter does not exist | **yes** | no |

**`agy` fails open too.** Same answer, and it had to be measured rather than inferred:
`agy.force-ask-defeated-by-skip` is the same gate mechanism behaving in *opposite* directions on the
two vendors, so "claude does X" is not evidence about agy for anything in this family.

**The consequence is worse here.** `agy.permissions-are-global-only` means the workspace hook is the
only per-worker gate an agy worker has. On `claude`, a dead hook still leaves the MCP callback and
elicitation covering AER's own tools; on `agy` it leaves nothing at all. So 0029's startup
self-check is load-bearing on both vendors, and *sole* cover on one.

**The right-hand column is not a finding — it is an unmeasured column, and it is labelled that way
deliberately.** Every arm reads *no*, including the deny control, whose reason `agy` does not
surface under `-p`. So the detector was never shown capable of a *yes*, and its zeros cannot
distinguish "agy said nothing" from "this instrument cannot see what agy says." agy's hooks
documentation describes no channel that would report a broken hook command either, so there is
nothing to aim a better detector at. **Whether agy fails open *silently* is therefore unresolved**,
and the check declares fail-open only. The claude table above is different: its deny control does
report, which is what makes its `no` column a result.

That distinction matters because "silently" is the word that makes the startup self-check the *only*
possible detector. It is earned on `claude` and not on `agy` — and the design is unaffected, because
one measured-silent vendor already requires the self-check and AER runs one per worker either way.

Recording why this arm was written late: 0029's mandatory self-check was justified from a
claude-only run, while the sentence it supports — "the workspace hook is the only way to gate an agy
worker" — is an agy claim. That is the same shape as the elicitation-portability gap earlier in this
audit: measure one vendor, write the consequence as though it generalises.

A second thing surfaced on the way: `agy.hook-deny-honoured` reports `reason surfaced=<bool>` in its
detail but does not gate its PASS on it, and this run shows that value has been **False**. The deny
is honoured — that check's actual claim — but the reason is not reaching the CLI's output under
`-p`. Recorded here rather than quietly fixed, since it is a live example of a detail field nobody
was reading.

### Worker identity: per-worker roots are a `claude`-only option (2026-07-25)

Decision 0029 carried this as **assumed** and as an owner action item. It is now measured, and the
two vendors diverge completely — which is why it was asked twice rather than once.

**`claude`: two config roots, both live.** The operator signed a fresh `CLAUDE_CONFIG_DIR` in
interactively. The decisive observation is the *pre-existing* root: it kept `loggedIn: true`
throughout, so the second sign-in did not displace the first. Both roots then reported the same
account and org, and two concurrent `-p` runs — one per root — both returned successfully with
distinct session ids.

| root | `loggedIn` before | `loggedIn` after | concurrent run |
|---|---|---|---|
| pre-existing | true | **true** | `ROOT_A_OK`, session `201d2470` |
| fresh | false | **true** | `ROOT_B_OK`, session `325be984` |

**`agy`: no such mechanism exists.** It documents no config-root variable, and `agy --help` lists
**no `auth` subcommand at all** — so there is neither a per-worker root nor a free readiness probe
(`claude auth status` is both structured and free; agy has no counterpart). The only candidate left
was environment redirection, and it does not work: a run with `HOME`, `USERPROFILE`, `LOCALAPPDATA`
**and** `APPDATA` all pointed at empty directories still authenticated, while creating a fresh
`.gemini` tree it evidently did not need. Config and credential are in different places, and the
credential follows none of those variables.

**Where the credential actually lives was deliberately not investigated.** Architecture Rule 4 puts
that off-limits, and the architectural question — *can AER give a worker a separate identity* — is
already answered without it. Recording the boundary because the temptation to keep digging is the
thing the rule exists to stop.

**What this settles.** Per-worker config roots are a **`claude`-only** design option. Any AER
feature that depends on distinct worker identities cannot be built for `agy` today. That is a
constraint to design around, not a defect to file.

### SEP-1036 url mode: `agy` declares it and does not implement it (#531, 2026-07-25)

Measured with a **person at a real terminal**, which is what closed it — every automated arm answers
`cancel` identically whether the vendor refuses or the harness suppresses, and nothing in a session
can tell those apart. See [`runbooks/sep-1036-url-elicitation.md`](runbooks/sep-1036-url-elicitation.md).

`agy` 1.1.7, protocol `2025-11-25`, declaring `elicitation: {'form': {}, 'url': {}}`:

| arm | client's answer | latency | shown to the person |
|---|---|---|---|
| form mode (the control) | `cancel` | **2.7 ms** | no |
| url mode | `cancel` | **0.6 ms** | no |

**Sub-millisecond means no UI was ever attempted.** The control that makes this a vendor finding
rather than a harness artefact came free: `agy` prompted the operator twice for *tool permission* in
the same session, and they answered both. So "agy will not prompt here" is excluded by agy's own
behaviour, not by an argument.

**The form-mode arm is why this is trustworthy at all.** Without it, "agy declines url mode" could not
be told from "this harness declines every elicitation" — the finding would have been about the driver.

**Evidence class: contradicted.** The capability is declared and not honoured.

#### But the blocking `tools/call` round trip works, and is now measured

The same run, `hold` flow:

```
elicitation cancelled   t+0.0006s      (played no part in what followed)
operator opened the URL t+162.7s
server completed the call
agy: "Executed both control_tool and elicit_tool successfully"
```

**A tool call held open 162 seconds, answered out of band in a browser, and the late result accepted.**
Worker asks, call stays open, human answers somewhere else entirely, worker resumes — the shape M28's
demonstration needs, with a real person and a real browser.

This promotes decision 0029's central claim from reasoned to
measured. Its two `Rests on` rows were amended to match in `fd1aa00` (#528), not here — the
url-surfacing row resolves **measured false**, with exactly the consequence it predicted.

#### One hazard for the gate surfaces

The model was told *"elicit_tool was refused (not approved)"* when the **client had refused on the
user's behalf without asking them.** Any AER surface reporting a gate as declined must not present a
vendor's auto-refusal as a person's answer — they are different facts and only one of them is
somebody's decision.

### Three behaviours found after this audit closed (#538, 2026-07-25)

Recorded here because they existed only in code comments and commit messages, which is not the
register.

**1. An unknown `--model` fails closed on `claude` and accepts this unlisted name (`gemini-3-flash`) on `agy`.**

| | behaviour |
|---|---|
| `claude` | `is_error: true` — a stale pin self-reports |
| `agy` | accepts this unlisted name (`gemini-3-flash`), `rc=0`, output produced; no warning observed on the captured stream |

Measured: `agy -p … --model gemini-3-flash`, a model `agy models` does not list, returned `rc=0` with
output. That name had been sitting in a binding fixture, two dialogue participants and two runbooks,
pinning nothing. The `claude` arm is measured by `tools/smoke-preflight/preflight.py` and by #536's
live run, not by this probe.

**Scope — two claims, two evidence classes.** *Accept vs. reject* is **verified (both directions)**:
claude rejects the unlisted name, agy accepts this unlisted name (`gemini-3-flash`). *Which model then served the request* is
**inferred, not measured** — AER has no attribution surface, which is the same reason it cannot be
checked here. Likewise "no warning" is an absence with **no positive control**: nothing establishes
that this capture would have shown a warning had one been emitted. Read it as "none observed", not
as "none emitted".

**Why it matters past the tests.** AER pins a model per worker. On `agy` a pin that goes stale is
accepted rather than rejected, so any AER cost or model-attribution surface would report the pinned
model with no way to confirm what ran — and agy's catalogue includes `claude-opus-4-6-thinking`, so
the drift is not necessarily downward. `pixi run smoke-preflight` guards the test fixtures.

**Update (#547).** A second, independent probe (`effort.agy-rejection-is-per-model`, 2026-07-28)
measured a DIFFERENT unlisted name — `gemini-3-pro` — and found the opposite outcome: rejected, by
name. The two data points read as a contradiction because nothing ran them under one shared control
until now. `tools/vendor-verify/verify.py` carries the sentinel
`agy.unlisted-model-acceptance-is-per-name`, written to settle it: control arm a catalogued model,
then both unlisted names under the same invocation shape. **Not yet run** — this entry states what
the check tests, not a result; see
[`architecture-impact.md`](architecture-impact.md) § agy, whose row is explicit that nothing here is
measured until the operator runs it. The product-side half — nothing validates a model name a
worker binding or a room actually carries, before it reaches `agy` — is filed as its own issue,
#726, rather than built inside #547.

**2. `claude` has no model-catalogue command, and `claude models` spends usage.** There is no such
subcommand — the words are taken as a *prompt* and answered, costing a turn. So claude's valid model
set cannot be enumerated for free, which is why `smoke-preflight` checks claude pins by shape and
relies on claude's own rejection. Evidence class: **verified**.

**3. `agy` needs `--add-dir` to load a workspace `.agents/mcp_config.json`.** Setting cwd to the
workspace is not enough. Without it `agy` reports *"the requested MCP tools … are not available in the
active environment or tool set"* — a message that reads like a model declining a tool rather than a
config that never loaded. This is the `agy` counterpart of claude's `.mcp.json`/`--mcp-config`
constraint: **an agy worker spawned without `--add-dir` has no gate loaded and says nothing about it.**
Evidence class: **verified**.

Note the direct contradiction with `gate.add-dir-loads-no-config`, which measured that on `claude`
`--add-dir` loads **no** configuration. Both are true; neither is general. #533's constraint 1 stated
the claude behaviour unscoped, and is [cross-referenced there](https://github.com/aer-works/baton/issues/533#issuecomment-5079218678)
so the spawn path carries the asymmetry rather than one rule.

### Environment starvation: `agy` needs `USERPROFILE`, `claude` needs nothing (#549, 2026-07-29)

Measured on Windows while building the environment allowlist, because an advisory pass asserted that
both CLIs need `HOME`/`USERPROFILE`/`APPDATA`/`LOCALAPPDATA`/`XDG_*` to find their credentials. They
do not, and the two vendors differ in the direction that decides how tight the allowlist can be.

| | under `env -i` (only `SYSTEMROOT`, `WINDIR`, `MSYSTEM` present) |
|---|---|
| `claude auth status` | **succeeds** — `loggedIn: true`, exit 0, full org/subscription payload |
| `agy models` | **fails** — `%userprofile% is not defined`, dying in startup before any work |

`USERPROFILE` **alone** is sufficient for `agy`; adding `SYSTEMROOT`, `PATH`, `APPDATA` and
`LOCALAPPDATA` changed nothing. `claude` resolves its config through Win32 APIs off the process
token rather than from the environment.

**Why it is worth recording rather than leaving in a commit message.** It makes `agy` the only
**discriminating control** available for anything about the spawned environment. `claude` cannot go
red on env starvation at all, so an allowlist validated against `claude` alone would be a green from
an instrument with no failure mode — the trap this audit's own §"prove the instrument works before
believing a zero" describes. Anything later claiming "the worker environment is sufficient" has to
say which vendor it measured.

**Scope.** Windows only, `claude` 2.x / `agy` 1.1.8, `auth status` and `models` specifically — not a
claim about a full turn, and not measured on Linux or macOS. `env -i` was verified to actually clear
(three surviving variables, all MSYS-injected) rather than assumed.

### `agy --log-file` trails the conversation id with a comma, and `--conversation` tolerates the damage (#837, 2026-07-31)

Measured live while verifying #586's session continuation. The log line agy writes is
`conversation=<uuid>, ...` — the id is followed immediately by a comma and further fields, and
nothing in agy's documentation states the line's format at all. Two scrape regexes in this repo
were written against an assumed "id runs to whitespace" shape (`[^\s\r\n]+`), which captures the
comma into the stored id.

Both arms were measured directly: `agy -p "Remember BANANA-42" --log-file …`, then
`agy --conversation <id> -p "what codeword?"` with a **clean** id and with a **comma-tailed** id —
both recalled the codeword. So agy currently accepts a malformed conversation id, which is the only
reason the captured comma was latent rather than a live defect: the resume worked by resting on
undocumented vendor tolerance, not on a correct id. The scrape class is now `[\w-]+` at every site
(the dialogue worker's under #586, the daemon's two under #837), scoped to the observed UUID
alphabet.

**Scope.** `agy` 1.1.8, Windows, non-interactive `-p` runs with `--log-file`; the tolerance claim
is about `--conversation` specifically and could be withdrawn by any agy release without notice —
which is exactly why no design should rest on it again.

### claude sub-agent turns DO appear as top-level `"type":"assistant"` usage lines on stdout (#1623 re-review N5, 2026-09-02)

Measured against real, already-captured `implement` lanes' `.stdout.log` files under `~/.baton/rooms`
(read-only; no new run) rather than a fresh fan-out prompt, since the fleet already carries dozens of
them. Filtering each room's stream-json for `.type=="assistant" and .parent_tool_use_id != null and
.message.usage != null` returns a non-zero count in every `implement` room checked that used a
sub-agent tool call — one room alone (`dispatch-implement-5f92cb84`) had 96 such lines. So a
sub-agent's own turns are **not** invisible to `Baton.Mutation.TokenBudgetMonitor`'s mid-stream read
(`StandardWorkerUsageParsers.cs`'s claude parser matches every `"type":"assistant"` line with no
`parent_tool_use_id` discrimination) — this settles the first branch of N5's "either answer is wrong"
pair, and rules out simple under-counting from absence.

**What this measurement does not settle: the second branch is live instead.** A sub-agent turn's own
`message.usage.input_tokens` reflects that sub-agent's own (typically much smaller) context, not the
parent conversation's. `TokenBudgetMonitor.OnStdoutLine` replaces `_inputLevel` unconditionally on
every matching line (`TokenBudgetMonitor.cs`), so a sub-agent turn arriving after a large main-loop
turn can **lower** the tracked level rather than raise it — the old sum-based read could only ever grow;
this level-based read can shrink on exactly the turns that mean the most work is happening. Neither
this capture nor the PR distinguishes claude's own turns from a sub-agent's by `parent_tool_use_id`, so
the budget's replace-on-every-line behaviour is measurably wrong in the fan-out shape, not merely
unmeasured. `spec/baton.md` §3 records this as an open gap rather than a silent one.

### The hook route expresses `agy`'s missing scoped-shell-without-network grant (#1387, second probe, 2026-09-02)

The first #1387 probe measured that agy's own vendor-native flags (`--sandbox`, `--mode plan`, and
the combination) do not express `review`'s grant — every restrictive shape hard-stopped the whole
turn on the first shell call headless mode could not auto-approve, so `git status`/`git log`, which
must stay allowed, were never reached. That probe's own conclusion pointed at a different route
instead: launch under `--dangerously-skip-permissions` and let AER's own `PreToolUse` hook narrow it.

This second probe measured that route directly rather than leaving it inferred. Setup: a scratch git
repo (one commit, local bare `origin` remote), `--add-dir <repo>`. The hook config and env vars were
generated by reflecting into `AgyWorkerAdapter`'s own internal `BuildDeniedTools`/`BuildShellPatterns`/
`BuildDeniedShellPatterns`/`HookAssemblyToken` methods against a freshly built `Baton.Vendors.dll`, for
the `review` role's real grant shape (`ReadFiles=true, WriteFiles=false, RunShellCommands=true,
NetworkAccess=false`, `WorkerRoles.json`'s actual allow/deny shell pattern lists) — not a hand-written
approximation. `TryTranslatePermissionGrant`/`Resolve()` were not called (they refused this exact
grant shape until this PR); only the hook-installation half of the adapter ran, with the real,
currently-shipped `AgyHookCheckCommand` binary wired as the hook handler exactly as `BuildHooksJson`
spells the command. Launched twice, identically:
`agy -p <prompt> --output-format stream-json --print-timeout 3m --dangerously-skip-permissions
--add-dir <repo> --add-dir <agy-workspace>`, asking for six probes in one turn with an explicit
"don't stop on a denial" instruction.

**Results, identical across both runs:** write denied by the hook (no file appeared on disk either
run); `git push --dry-run` denied by the DenyAlways channel (`BATON_HOOK_DENIED_SHELL_PATTERNS`), not
merely an allow-list miss; `curl https://example.com` denied (not a git/gh command); a read of
`%USERPROFILE%\.gitconfig` denied (the model chose `Get-Content $env:USERPROFILE\.gitconfig`,
denied for the same "not a git/gh command" reason — the review role's allowlist is git/gh-only, so a
non-git/gh command is denied by construction, independent of whether its target happens to sit
outside the workspace); `git status` and `git log -1` both allowed, output returned to the model.
**A hook deny does not cancel the turn**: both runs executed all six probes in sequence and produced
a final `status: SUCCESS` result — the opposite of the vendor-native refusals the first probe found,
which hard-stopped on the first denial.

So the gap the first probe left open is closed on the channel this probe measured: `agy` can express
`review`'s grant through the hook route on the `run_command` channel, even though it still cannot
express it through any vendor-native flag combination. That is narrower than "end to end" —
five of the six probes are `run_command` calls, so the measurement is overwhelmingly about one
channel. Not probed here: the subagent trio and `manage_task` (`invoke_subagent`, `define_subagent`,
`manage_subagents` — denied outright by name rather than narrowed by pattern, since none of the four
is bounded by `AgyHookCheckCommand`'s pattern channel; #1387 review F1), the read tools' absent path
bound (`view_file`/`grep_search` are granted whole, not path-checked; F2), any agy tool outside the
four `BuildDeniedTools` categories (#623), and the allow/deny lists' own prefix-collision defects —
`git diff*`/`git grep*` over-admit, `git merge*` shadows the allowed `git merge-base*` — which six
probed commands could not have surfaced either way (#1679).
`AgyWorkerAdapter.TryTranslatePermissionGrant` now defers a grant with `RunShellCommands=true,
NetworkAccess=false` and a non-empty `ShellCommandPatterns` to `--dangerously-skip-permissions` plus
the hook, rather than refusing outright; an unscoped shell grant (no patterns) still refuses, since
nothing would bound it. `spec/baton.md` §9 and `WorkerRoles.json`'s `review` entry are updated to
match, both scoped to what this probe actually measured; the probe run above (reproducible via the
same reflected-hook-config approach) is that narrowing's acceptance test for the `run_command`
channel, not for the tool surface as a whole. Scratch directory deleted after the run; nothing
persists outside the `#1387` issue comment recording the same table.

Sentinel: `agy.tools-classified` (`tools/vendor-verify/verify.py`) — pins agy's live tool catalogue to `AgyWorkerAdapter`'s tool-name lists (#623).

### agy's sub-agent turns leave NO usage-bearing line in the parent's stream at all (#1742, capture `dispatch-implement-2807af38`, 2026-09-03)

Measured against a real agy lane's already-captured `.stdout.log` (copied into a second worktree as
`CAPTURE-agy-fanout.stdout.log.tmp`; read-only, no new run — agy was quota-exhausted at measurement
time). The lane's own prompt asked it to invoke a sub-agent (`invoke_subagent`) to count
`README.md`'s lines. 22 lines total, by `step_type`:

- `user_input` (×1, no usage) — `agent_response` (×7, at `step_index` 1, 3, 5, 7, 9, 11, 14; six of
  those (all but 9, the one immediately after the `write_to_file` `ERROR`) carry a `usage` object) —
  `tool` (×4: `find_by_name`, `view_file`, one `write_to_file` that errored, one `run_command`) —
  `subagent` (×1) — `error_message` (×1) — `system_message` (×1) — terminal `result` (×1, cumulative
  usage for the whole lane).

The verbatim `tool`/`subagent` pair for the `invoke_subagent` call (redacted: none of these fields are
secrets, so nothing is elided):

```
{"event":"step_update","step_update":{"conversation_id":"ce9f959c-81eb-4b84-8293-216eef7ebc6d","step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"invoke_subagent","tool_info":{"name":"invoke_subagent","parameters":{"Subagents":[{"Model":"inherit","Prompt":"Please count the total number of lines in README.md in the repository and report the exact number back to me.","Role":"Line Counter","TypeName":"research"}]}}}}
{"event":"step_update","step_update":{"conversation_id":"ce9f959c-81eb-4b84-8293-216eef7ebc6d","step_index":2,"state":"DONE","step_type":"subagent","tool_name":"invoke_subagent","duration_seconds":0.1586236,"subagent_info":{"subagents":[{"type_name":"research","role":"Line Counter","initial_prompt":"Please count the total number of lines in README.md in the repository and report the exact number back to me.","conversation_id":"57868f17-abb7-4ffa-99a9-1c1bda8d9929","log_uri":"file:///C:/Users/pbree/.gemini/antigravity-cli/brain/57868f17-abb7-4ffa-99a9-1c1bda8d9929/.system_generated/logs/transcript.jsonl","workspace_uris":["file:///C:/Users/pbree/.baton/rooms/dispatch-implement-2807af38/artifacts","file:///C:/Users/pbree/.baton/worker-launch/agy-workspace","file:///C:/Users/pbree/source/repos/w1742"]}]}}}
```

and the parent's next `agent_response`, which does carry ordinary usage:

```
{"event":"step_update","step_update":{"conversation_id":"ce9f959c-81eb-4b84-8293-216eef7ebc6d","step_index":3,"state":"DONE","step_type":"agent_response","duration_seconds":0.6402331,"usage":{"input_tokens":3979,"output_tokens":63,"thinking_tokens":0,"cache_read_tokens":12223,"total_tokens":4042}}}
```

The terminal `subagent` line carries a `subagent_info` object (its own `conversation_id`
`57868f17-…`, a `log_uri` pointing at a *separate* transcript file this lane's `.stdout.log` never
includes, and the sub-agent's prompt/role) but **no `usage` key at all** — not a smaller or aggregated
figure, absent. The sub-agent's actual turns (it went on to call `find_by_name`/`view_file` per the
lane's final answer) are written entirely to that other transcript, under a different
`conversation_id`, which this dispatch never captures.

**Conclusion: NO SEPARATE LINES, and more precisely than #1666's own two-way framing anticipated — the
sub-agent's usage is not folded into the parent's stream with an unmarked line; it never reaches the
parent's stream as a usage-bearing line at all.** `AgyUsageParser.TryParseIncrementalUsage`
(`src/Baton/Status/StandardWorkerUsageParsers.cs`) only reads usage off a `state:"DONE"`,
`step_type:"agent_response"` line; the `subagent` step_type line it would need to discriminate has no
`usage` object for that reader to ever see, marked or not. `AgyWorkerAdapter.TryParseProgressEvent`
and `tools/fleet-glass/pusher.py`'s `extract_live_counts` agree: both gate agy's `step_update` usage
read on `step_type == "agent_response"` (`AgyWorkerAdapter.cs` line ~1292; `pusher.py`'s `elif
isinstance(step, dict) and step.get("state") == "DONE": if step.get("step_type") ==
"agent_response"`), so neither treats a `subagent` step_type line as usage-bearing either.

**Consequence for #1666: the level-dip that fix closes on claude cannot occur on agy through this
path, because there is nothing for it to occur on.** #1666's claude fix exists because a sub-agent's
own smaller context WAS visible mid-stream and could replace the parent's tracked level; on agy, per
this capture, the sub-agent's context is never visible mid-stream in the first place — one
`step_type: "subagent"` line with zero usage fields is the entirety of what the parent's stream shows
for the whole sub-agent call. `IsSubAgentTurn` therefore has no line to key off on this vendor: not
because the marker is unmeasured, but because the shape that fix marks (a usage-bearing line
attributable to a sub-agent) does not exist in agy's parent-stream envelope. A single fan-out capture
cannot rule out some *other* agy shape (e.g. a future CLI version streaming a sub-agent's turns
inline) — this measurement is scoped to the one `invoke_subagent` capture available while agy is
quota-exhausted, not to every agy build.

### Skill roster: config-root skills load flat and shadow a same-named project skill (#1575, 2026-09-03)

Measured 2026-09-03 21:27 ET, Claude Code 2.1.258, `claude -p --model haiku --output-format json --max-turns 1`, run from a scratch project dir with `CLAUDE_CONFIG_DIR` pointed at a fresh root holding only the operator's copied `.credentials.json` (deleted after). Skills planted, each with a distinct description word the model was asked to echo back (list every skill starting with `zebra` and its description, no invocation):

- `<root>/skills/zebra-root-flat/SKILL.md` (FLATROOT)
- `<root>/.claude/skills/zebra-root-dotdir/SKILL.md` (DOTDIRROOT)
- `<root>/skills/zebra-shared/SKILL.md` (ROOTCOPY) and `<project>/.claude/skills/zebra-shared/SKILL.md` (PROJECTCOPY) — same name, precedence probe
- `<project>/.claude/skills/zebra-project/SKILL.md` (PROJECTONLY)

| invocation | skills the CLI listed |
|---|---|
| `--setting-sources project` | zebra-project: PROJECTONLY, zebra-shared: PROJECTCOPY |
| no `--setting-sources` flag | zebra-root-flat: FLATROOT, zebra-shared: **ROOTCOPY**, zebra-project: PROJECTONLY |

| # | fact | verdict |
|---|---|---|
| 1 | `CLAUDE_CONFIG_DIR` relocates skill lookup | confirmed — a skill under the redirected root loads |
| 2 | Skill directory shape under a redirected root | flat, `<root>/skills/` — `<root>/.claude/skills/` never loaded (confirms #1566) |
| 3 | Precedence on a same-named skill in both project and config-root | the config-root (user-scope) copy wins — the model saw ROOTCOPY, not PROJECTCOPY |
| 4 | `--setting-sources` | governs whether user-scope (config-root) skills load at all; `ClaudeWorkerAdapter` passes no `--setting-sources` flag in its spawn argv, so the default (project + user-scope both load, config-root wins a collision) is what every dispatched worker actually gets |

Fact 3 is the opposite of `ClaudeWorkerAdapter`'s prior project-first `GroupBy(...).First()` dedup ordering for skills, which named the project copy on a collision — the roster printed the wrong file. Fixed in #1575 by scanning the config-root skills directory ahead of the project one (commands keep their prior project-first ordering; unmeasured either way, left unchanged). Sentinel: `claude.skills-follow-config-dir-flat-and-shadow-project` (`tools/vendor-verify/verify.py`).

### `--allowedTools Bash(...)` clause parsing: comma-lists, case, and whitespace before the paren (#1515, #1514, CLI 2.1.258, 2026-09-03)

Measured `claude -p --model haiku --setting-sources ""`, probe command `git tag probe-tag` in a
scratch git repo, truth read two ways (`permission_denials` in the JSON result and whether the tag
landed on disk). Two probe runs each, same result both times.

| fact | measured | sentinel |
|---|---|---|
| `Bash(a, b)` is read as ONE literal pattern containing a comma, not two patterns | `Bash(git diff*, git tag*)` denies `git tag probe-tag` | `claude.allowedtools-comma-list-is-one-literal` |
| lowercase `bash(pattern)` is NOT a grant | `bash(git tag*)` denies | `claude.allowedtools-space-before-paren-is-a-grant` (negative control) |
| `Bash (pattern)` — whitespace before the opening paren — IS a grant | `Bash (git tag*)` allows | `claude.allowedtools-space-before-paren-is-a-grant` |
| a read-only `git status`/`git diff`-style command runs with no grant under `-p` at all | every arm allowed it, granted or not — never usable as a permission-probe command | none (a rig-design finding, not a design AER rests on) |

Consequence: `ClaudeWorkerAdapter.BuildShellPatternsFromRawScope`'s existing refusal of a comma-list
inside one raw-scope `Bash(...)` clause (#1506) already matches the CLI's own parser and needed no
change. Its `StartsWith("Bash(")` check, though, read `Bash (pattern)` as ordinary non-`Bash` text and
silently dropped it — the exact #1459 layer drift the method exists to close, since the CLI honours
that shape as a shell grant the hook channel never scoped. Fixed to REFUSE (throw) on `Bash\s+\(`
rather than drop it; lowercase `bash(` stays dropped as text, matching the negative-control row above.

### The config-root write refusal (#1823, #599) re-pinned as a sentinel — and it is narrower than the code comment reads (#1827, CLI 2.1.258, 2026-09-04)

Measured `claude -p --model haiku --effort low`, two arms, one variable: whether a `Write`-tool
target sits under `CLAUDE_CONFIG_DIR`. Both arms ran under the SAME override, seeded only with a
copy of the operator's own `.credentials.json` (never the real `~/.claude`, never generated); only
the write target's location relative to that override differed.

| arm | `CLAUDE_CONFIG_DIR` value | target | result |
|---|---|---|---|
| in-root | `<tmp>/.claude` | `<tmp>/.claude/<subdir>/out.txt` | no artifact — "I need permission to create/write to this file. Approve the Write tool call to proceed." (rc 0) |
| control | `<tmp>/.claude` (same) | `<tmp>/<other-tmp>/out.txt` | wrote `OK` |

The refusal reproduces on this build, but its signature moved since the 2.1.220 measurement `IWorkerAdapter.SensitiveOutputRoot`'s doc comment cites: no artifact and an ask-for-approval sentence, not an exit and a named "which is a sensitive file" string — `-p` (headless) mode cannot answer the ask, so the write is silently withheld rather than refused loudly. The issue's own acceptance criterion ("'sensitive file' or no artifact") anticipated exactly this drift.

A second, unanticipated fact came out of getting the in-root arm to reproduce at all: an override
pointed at an *arbitrarily-named* temp directory (`<tmp>/v-sensroot-cfg-xxxx`, not `.claude`) let the
write through with no refusal, on the same CLI build. The refusal keys off a literal `.claude` path
segment in the target, not off whatever `CLAUDE_CONFIG_DIR` is actually pointed at — it fired
identically with the env var unset entirely, so long as some ancestor of the target was named
`.claude`. `SensitiveOutputRoot`'s doc comment reads as config-root-value-aware ("the directory
claude's CLI itself treats as sensitive"); measured behaviour is closer to "any `.claude`-named path
segment, CLAUDE_CONFIG_DIR notwithstanding." `ClaudeConfigRootOverride` (`BatonEnvironmentSnapshot`)
is operator-authored and has no reason to be named anything but `.claude` in practice, so this does
not contradict any dispatch this refusal has actually gated — recorded here because it narrows the
claim, not because anything currently depends on the wider one. Sentinel:
`claude.sensitive-root-write-refused` (`tools/vendor-verify/verify.py`). #1834 realigned the code to
this measurement: `ClaudeWorkerAdapter.HasSensitiveOutputPathComponent` now matches on the literal
`.claude` path component rather than the config-root value, so this finding is closed, not merely
recorded.

### Still not settled — recorded as untested, not refuted

- **`defer`'s single-tool-call limit.** Three attempts failed to make the model batch tool calls
  (`[1]`, `[1,1,1]`, `[1,1,1,1,1,1]` blocks per assistant message) even under an explicit
  instruction to emit them together. The documented limit was therefore never exercised. It
  matters because the documented failure mode is the tool **proceeding**, so if real, the gate
  opens silently under a condition the model chooses.
- **The MCP idle window's upper bound.** 200s survived; the ceiling is unknown. **Deliberately not
  pursued**: 0029 releases the call rather than holding it, so no design depends on the bound.
- **Whether `--mcp-config` merges with the operator's configured servers or replaces them.**
  Attempted for free inside `gate.elicitation-hook-event-fires`; the session's server list did not
  read, and a not-observed is not a zero. Matters only for observing a pause AER did not author.
- **An anomaly:** six sequential deferred calls occurred inside one process run ending
  `tool_deferred`, which does not match the documented "the process exits" on first defer.
- **agy [#548](https://github.com/google-antigravity/antigravity-cli/issues/548) / [#640](https://github.com/google-antigravity/antigravity-cli/issues/640) not reproducing** is scoped to 1.1.7, Windows, `-p`. A reporter saw something; the
  route they took was not the route tested here.

## Sources

- Claude Code docs index — https://code.claude.com/docs/llms.txt
- [CLI reference](https://code.claude.com/docs/en/cli-reference) ·
  [Sandboxing](https://code.claude.com/docs/en/sandboxing) ·
  [Permissions](https://code.claude.com/docs/en/permissions) ·
  [Workflows](https://code.claude.com/docs/en/workflows) ·
  [Channels](https://code.claude.com/docs/en/channels) ·
  [Agent teams](https://code.claude.com/docs/en/agent-teams) ·
  [SDK permissions](https://code.claude.com/docs/en/agent-sdk/permissions) ·
  [SDK user input](https://code.claude.com/docs/en/agent-sdk/user-input) ·
  [SDK overview](https://code.claude.com/docs/en/agent-sdk/overview) ·
  [SDK session storage](https://code.claude.com/docs/en/agent-sdk/session-storage)
- Antigravity CLI — [overview](https://antigravity.google/docs/cli/overview) ·
  [reference](https://antigravity.google/docs/cli/reference) ·
  [permissions](https://antigravity.google/docs/cli/permissions) ·
  [sandbox](https://antigravity.google/docs/cli/sandbox) ·
  [usage](https://antigravity.google/docs/cli/commands/usage) ·
  [install & auth](https://antigravity.google/docs/cli/install)
- Antigravity SDK / terms — [SDK overview](https://antigravity.google/docs/sdk/overview) ·
  [plans](https://antigravity.google/docs/plans) ·
  [FAQ](https://antigravity.google/docs/faq) ·
  [terms](https://antigravity.google/terms)
- `google-antigravity` 0.1.8 wheel, read via `pip download --no-deps`
- **Machine-readable indexes** (the whole corpus, mirrored by `pixi run vendor-survey`) —
  [claude `llms.txt`](https://code.claude.com/docs/llms.txt) ·
  [agy `llms.txt`](https://antigravity.google/llms.txt) ·
  [agy `sitemap.xml`](https://antigravity.google/sitemap.xml)
- **`google-antigravity/antigravity-cli`** — the CLI's public
  [issue tracker](https://github.com/google-antigravity/antigravity-cli/issues) and
  [changelog](https://github.com/google-antigravity/antigravity-cli/blob/main/CHANGELOG.md).
  Never consulted before 2026-07-25; [#49](https://github.com/google-antigravity/antigravity-cli/issues/49)
  documents the hooks path misalignment independently. **Check it before recording any agy negative.**
- **`agy`'s own logs** — `~/.gemini/antigravity-cli/log/cli-*.log`. Reports hook discovery
  (`hooks_manager.go`), hook execution failures (`command_hook_executor.go`), and the resolved
  workspace on every start. The fastest disproof of an "it isn't there" claim.
