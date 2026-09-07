# Vendor capabilities — what each worker CLI can actually do

> **Read [`vendor-doc-audit.md`](vendor-doc-audit.md) alongside this (#527, 2026-07-25).** That audit
> re-measured much of what is here against `claude` 2.1.220 / `agy` 1.1.8 and **changed two readings
> in this file**: the `--allowedTools` section below is no longer "a family ceiling", and the sandbox
> row's scope is Windows-only. Rows this file states as measured still hold; what changed is what
> they *mean*. Behaviours either file relies on are re-runnable with `pixi run vendor-verify`.

**Status: verified reference, with a version split that matters.** Where a claim rests on a live run,
the observation is quoted. Where it rests on inspecting help text or the shipped binary, it says so —
and where a row says something is *absent*, it names the surfaces that absence was established on.

| established | against | covers |
|---|---|---|
| 2026-09-04, `#1853` | `codex` **0.153.2**, desktop **26.901.4073** | First subscription-authenticated Codex probe and adapter evidence: native shell-less `codex exec --json`, resumable `thread_id`, per-turn token usage, typed terminal/error events, documented sandbox/config controls, and dynamic visible model/effort discovery through app-server `model/list`. Full evidence boundaries, measurements, unknowns, and sanitized fixtures: [`vendor-codex-probe-2026-09-04.md`](vendor-codex-probe-2026-09-04.md). |
| 2026-09-01, `#1613` review | `claude` **2.1.257** | **per-message usage shape, measured live (finding: Fleet Glass live tokens).** `claude -p "Reply with the single word ok." --output-format stream-json --verbose --max-turns 1` in a scratch dir. The `type=="assistant"` line's `message.usage` object carries, per message (not just on the terminal `result` line): `input_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`, `cache_creation` (nested `{ephemeral_5m_input_tokens, ephemeral_1h_input_tokens}`), `output_tokens`, `service_tier`, `inference_geo`. This settles the question the #1613 lane left unmeasured ("does a per-message usage figure exist at all, for anything") in the affirmative — all four token-count fields the design needed (`input_tokens`/`output_tokens`/`cache_read_input_tokens`/`cache_creation_input_tokens`) are present on the SAME line as each assistant turn, confirmed on a real turn (values were `2`/`4`/`15092`/`12066` on this trivial "ok" turn — the trivial prompt's own cache-read/write dominance mirrors the `total_cost_usd` finding below at line ~548). Consumer: `tools/fleet-glass/pusher.py`'s `extract_live_counts`, spec/baton.md §6's `rooms[].live` schema entry. *Evidence: live capture, this run.* |
| 2026-08-31, `#1540` | `claude` **2.1.252** | **stream-json flush cadence and volume, measured live (#1540).** Invoked headlessly with `-p --output-format stream-json --verbose`. Observed incremental flush per message/tool event: `system` init/hooks arrive at turn start, `assistant` content events flush as each message/tool block completes, and `rate_limit_event`/`result` arrive at turn completion. Event-level volume without `--include-partial-messages` is ~5–8 JSON lines per single-turn dispatch (a fraction of token-level deltas), safely preserving the 8 MiB `ExecutionStreamLogger` window against premature rollover. *Evidence:* `claude -p "Count to 5 slowly, one line per number" --output-format stream-json --verbose` (the #1540 lane's own recorded run): 4 `system` lines truncated in the capture, then two `assistant` events (a `thinking` block, then the `text` block `"1\n\n2\n\n3\n\n4\n\n5"`), then `{"type":"rate_limit_event","rate_limit_info":{"status":"allowed",...}}`, then the terminal line `{"duration_api_ms":2550,...,"is_error":false,...,"result":"1\n\n2\n\n3\n\n4\n\n5",...,"type":"result","duration_ms":3870,...}` — 8 JSON lines total for this single-turn dispatch (4 truncated `system` + 2 `assistant` + 1 `rate_limit_event` + 1 `result`), at the top of the ~5–8 range stated above. |
| 2026-08-31, `#1487` | `claude` **2.1.251**, `agy` **1.1.22** | **auto-update off-switches, measured for both CLIs (not flipped — see [`docs/runbooks/vendor-probe.md § Turning drift deliberate`](runbooks/vendor-probe.md#turning-drift-deliberate-1487)).** `claude`: `settings.json`'s `env.DISABLE_AUTOUPDATER: "1"` is the real mechanism — checked live against `code.claude.com/docs/en/setup.md` (§ "Disable auto-updates"), 2026-08-31. `autoUpdates: false` (the alternative the issue asked to disambiguate) is **not a real key**: `settings-reference.md`'s full key index has no such entry — the update-related keys are `autoUpdatesChannel`, `minimumVersion`, `requiredMinimumVersion`, `requiredMaximumVersion`, none of which is a kill switch. `DISABLE_AUTOUPDATER` stops only the background check; `claude update`/`claude install` still work (`DISABLE_UPDATES` blocks those too, not measured here — out of scope, the loop wants manual updates to keep working). *Evidence: vendor documentation.* `agy`: `AGY_CLI_DISABLE_AUTO_UPDATE` (env var) — **undocumented**: absent from `antigravity.google/docs/cli/settings`, `.../cli/reference`, and `.../cli/install` (all three checked, none mentions auto-update at all); found by inspecting the shipped binary (`agy.exe`, strings), sitting directly beside the `"failed to check for updates"` error string it evidently guards. *Evidence: shipped binary (strings), inspected not run — never toggled, per this run's scope.* |
| 2026-08-31, `#1461` | `claude` **2.1.251** | subcommand granularity and command-line matching extent of `Bash(pattern)` grants — the two gaps #1456's second reader flagged in the canonical ceiling measurement below. New subsection immediately after that table. |
| 2026-08-28, `#1397` | `claude` **2.1.247**, `agy` **1.1.22** | probe suite re-pinned after both CLIs updated (2.1.231→2.1.247, 1.1.13→1.1.22 per the staleness tripwire). New, surface-dependent finding: **`/usage` in plain print mode** — `agy -p "/usage"` emits the CLI's own tab-separated quota table (model family, window, % remaining, reset instant; measured twice same day, no model turn), and `claude -p "/usage"` reports session/week percentages likewise. The probe's stream-json instrument does NOT surface it (no percentage; conversational answer) — a per-surface present/absent split, not a contradiction; the probe.md agy row's "not found" is correct for the surfaces it names. Supersedes the 2026-07-26-era "not a built-in command" reading (accurate when taken; a CLI update changed behaviour). |
| 2026-08-10, `#1088` | `agy` **1.1.11** (`claude` carried) | **`structured output` CORRECTED — the negative in the rows below was a probe bug, not agy.** `Aer.VendorProbe` invoked agy with *claude's* stream-json grammar (`-p` boolean + positional prompt + `--verbose`), so agy read `--output-format` as the prompt and stream-json never engaged (it also rejects `--verbose`, exit 2). Fixed to agy's flag-value `-p`: agy **observably streams** `--output-format stream-json` on stdout (`init`→`step_update`→`result` with a `usage` token object). Per-turn *dollar* cost stays absent (no `total_cost_usd`); per-turn **token** usage is now recorded present. The "every reading unchanged" rows below were re-confirming a false negative. |
| 2026-08-07 | `agy` **1.1.11** (`claude` carried) | the six probe-suite rows re-established after `agy` self-updated 1.1.10→1.1.11: usage, per-turn cost, structured output, `--permission-prompt-tool`, effort, `--add-dir` — **every reading unchanged**; only the version and the RPC server's random port moved. *(structured-output reading later found false — see the #1088 row above.)* |
| 2026-08-03 | `agy` **1.1.10** (`claude` carried) | the six probe-suite rows re-established after `agy` self-updated 1.1.9→1.1.10 mid-session: usage, per-turn cost, structured output, `--permission-prompt-tool`, effort, `--add-dir` — **every reading unchanged**; only the version and the RPC server's random port moved. |
| 2026-08-01 | `agy` **1.1.9** (`claude` carried) | the six probe-suite rows re-established after `agy` self-updated 1.1.8→1.1.9: usage, per-turn cost, structured output, `--permission-prompt-tool`, effort, `--add-dir` — **every reading unchanged**; only the version and the RPC server's random port moved. The subcommand rows below are older (#472/#527) and were not in this run. |
| 2026-07-25, `#527` | `claude` **2.1.220**, `agy` **1.1.8** | the rows the probe suite regenerates: usage, per-turn cost, structured output, `--permission-prompt-tool`, effort, `--add-dir`, plus the subcommand findings below — **re-probed after claude self-updated mid-audit** |
| 2026-07-25, `#527` | `claude` **2.1.220**, `agy` **1.1.8** | everything in [`vendor-doc-audit.md § Verified by running it`](vendor-doc-audit.md), all 29 `pixi run vendor-verify` checks |
| 2026-07-24, `#472` | `claude` 2.1.219, `agy` **1.1.6** | everything else — the permission grammar, `--sandbox` enforcement, the cwd finding, `--remote-control`, the blocking-MCP proof |

**`agy` moved from 1.1.6 to 1.1.7 partway through that same day** — the superseded binary is still on
disk as `agy.exe.<timestamp>.old` — and nothing noticed until the probe suite recorded a version. The
`#472` rows have **not** been re-verified against 1.1.7. They are not thereby wrong; they are
unattributed to the CLI that is installed, which is a different and quieter problem. `pixi run
vendor-check` going green means only "no CLI has moved since the last probe" — never "every row here
is verified."

This exists because the M25 design assumed capabilities in several places, and design that assumes
wrongly is worse than design that knows its limits. Four assumptions were **wrong** and are corrected
below. Two of the four were this document's own rows, which is the honest reason the probes are now
[a program](../tools/Baton.VendorProbe/) rather than a habit: `pixi run vendor-probe` regenerates the
findings, and `pixi run vendor-check` (free — it only reads `--version`) tells you when a vendor has
moved out from under them.

## Why the probe method matters

A nested `claude` invoked from inside a Claude Code session inherits the parent's environment and
therefore its **tool set and MCP servers**, which no daemon-spawned worker ever has. An early probe
that stripped only `^CLAUDE_CODE_` missed `CLAUDECODE`, `CLAUDE_EFFORT`, `CLAUDE_PID` and
`CLAUDE_JOB_DIR`, and produced a result we nearly wrote down as fact.

Strip **every** `^CLAUDE` variable, and verify the strip worked by reading `permissionMode` and the
`tools` array out of the `system:init` event — not by trusting the flags you passed:

```sh
STRIP=$(env | grep -o '^CLAUDE[A-Z_]*' | sed 's/^/-u /' | tr '\n' ' ')
env $STRIP claude -p --output-format stream-json --verbose "..."
```

## Capability matrix

| | `claude` 2.1.220 | `agy` 1.1.11 |
|---|---|---|
| Headless flag | `-p` / `--print` | `-p` / `--print` |
| Prompt delivery | **stdin OR positional arg** — `-p` (boolean) reads the prompt from stdin when no positional is given (+ `--input-format` for streaming) | **`-p` flag value only** — the prompt is the *value* of `-p`/`--print`; stdin is read neither as prompt nor as context; no `--input-format`, no prompt-file flag |
| Effort | `--effort low\|medium\|high\|xhigh\|max` | `--effort low\|medium\|high` |
| Extra directories | `--add-dir` | `--add-dir` (repeatable) |
| MCP | `mcp` subcommand, `--mcp-config`, `--strict-mcp-config` | **config file only** — `~/.gemini/config/mcp_config.json` |
| Permission modes | `--permission-mode acceptEdits\|auto\|bypassPermissions\|manual\|dontAsk\|plan` | `--mode accept-edits\|plan` |
| Per-call tool grant | `--allowedTools` / `--disallowedTools`, **pattern-matched** | **not found on `--help`** — and `--help` is known incomplete (see below). Documented grants persist to settings |
| Family-shaped ceiling | **yes** — `Bash(git *)` minus `Bash(git push*)`, enforced | **no** — `command(...)` matches the whole line literally |
| `--permission-prompt-tool` | **honoured** — consults a named MCP tool (undocumented) | **rejected**: `flags provided but not defined` |
| Bypass permissions | `--permission-mode bypassPermissions`, `--dangerously-skip-permissions` | **`--dangerously-skip-permissions`** |
| Sandbox | referenced in help only | **`--sandbox`, and it enforces** |
| Resume | `--resume`, `-c` / `--continue` | `-c` / `--continue`, `--conversation <id>` |
| Structured output | `--output-format stream-json --verbose` | **`--output-format stream-json`** — structured events on stdout, observed (`init`/`step_update`/`result`, #1088). A local gRPC/HTTP server is a *separate*, still-unenumerated surface (§below) |
| Running-session registry | **`claude agents --json`** | not found on: `--help`, subcommand list |
| Permission policy engine | **`claude auto-mode`** — allow / soft_deny / hard_deny | not found on: `--help`, subcommand list |
| Always-fires gate hook | **`PreToolUse` hook — first of six evaluation steps, applies even in `bypassPermissions`** | **`PreToolUse` hook — the vendor mechanism denies; what AER shipped never ran on Windows until #710 (the only platform measured — `sh` would have stripped the quotes `cmd` chokes on)** |
| Model enumeration | not found on: `--help`, subcommand list | **`agy models`** |
| Plan usage & reset | **`/usage` (and `/cost`) — works headlessly, see below** | **`/usage` — works headlessly, per-family rows, measured 2026-08-28, see below** |
| Per-turn cost | **`total_cost_usd` in every `stream-json` result** | no **dollar** figure (no `total_cost_usd`) — but the `stream-json` `result` carries per-turn **token** usage (`usage`: input/output/thinking/cache_read/total), #1088 |
| Other | `--agents <json>` | `--remote-control`, `--agent`, `--project` |

### Codex capability addendum (`0.153.2`, 2026-09-04)

The matrix above predates the third adapter. Codex's equivalent headless surface is `codex exec`; it
accepts a positional prompt, emits JSONL with `--json`, resumes with `codex exec resume <thread-id>`,
and exposes visible models plus their model-specific effort sets through app-server `model/list`.
`turn.completed` carries per-turn input, cached-input, output, and reasoning-output tokens, but no
API-equivalent dollar cost was observed. The CLI documents `read-only` and `workspace-write`
sandboxes, explicit network configuration, extra writable roots, and `approval_policy="never"`.
Raw/manual dispatch refuses filesystem-read denial, read-without-command, command-pattern, and
denied-option grants because those ceilings are not equivalent to Codex's per-run controls. Codex
0.153's `execpolicy` is an ordered-prefix language and cannot express Baton's option-token-anywhere
denies. Structured built-in roles instead use an app-server broker whose only model-facing dynamic
tools are generated from Baton's grant and enforced by Baton's path and command matchers. A live
read-only/outbox-only role succeeded with native shell/file/network/app/browser/multi-agent surfaces
disabled; workspace-write remains fixture-tested rather than live-measured. See the dated probe linked
in the history row for the exact command grammar, event shapes, cache-miss resume evidence, and open
measurements.

## Prompt delivery splits the vendors (#932)

**Measured 2026-08-02, live, control-armed.** A worker's prompt is passed today as the `-p` argument,
so the Windows command-line length caps its size and `CoreDispatcher`'s ceiling guard (#598) refuses
an over-long one.
Whether the prompt can move **off** the command line — delivered via stdin — is what decides #932
(decision 0048), and the two CLIs differ:

- **`claude -p` reads the prompt from stdin** when no positional prompt is given, with
  `--output-format stream-json --verbose` intact. Guarded by
  `verify.py::lifecycle.claude-print-reads-prompt-from-stdin`.
- **`agy -p` does not** — print mode takes the prompt as the *value* of the `-p`/`--print` flag and
  reads nothing from stdin: not as a prompt (print mode cannot be entered without a `-p` value — an
  empty one errors `empty prompt`), and not as context (given a valid `-p` value that tells it to use
  a piped context block, agy reports it received nothing on stdin). No `--input-format`, no
  prompt-file flag. Guarded by `verify.py::lifecycle.agy-print-requires-prompt-argument`.

Consequence: stdin lifts the command-line ceiling for `claude`, but a large `agy` prompt stays
argv-bound until agy grows an off-argv path. Both checks carry a prompt-as-argument control arm that
must pass first, so each verdict reflects real stdin behaviour rather than a harness artifact.

## Corrections to earlier assumptions

**`claude -p` does not auto-approve.** The opposite. With a clean environment, in a neutral directory:

| invocation | wrote the file? | `permissionMode` reported |
|---|---|---|
| `claude -p` (no flags) | no — denied | `default` |
| `claude -p --permission-mode manual` | no — denied | **`default`** |
| `claude -p --permission-mode acceptEdits` | yes | — |
| `claude -p --allowedTools Write` | yes | — |

**Both vendors fail closed**, which is the safer asymmetry to have been wrong about. Note also that
**`manual` is a no-op headless** — the session still reports `default` and no prompt is ever issued.

**MCP is not Claude-only.** `agy` loads MCP servers from `~/.gemini/config/mcp_config.json`
(`mcpServers`, stdio via `command`/`args`/`env`, or remote via `serverUrl`), and plugins may ship
their own. Observed spawning our server and running `server/discover` → `initialize` → `tools/list`.
Permission-by-consultation is therefore **uniform across vendors**.

## Denials are machine-readable

`claude`'s final result event carries the whole denied call, replayable once a human answers:

```json
"permission_denials":[{"tool_name":"Write","tool_use_id":"toolu_01…","tool_input":{"file_path":"…","content":"BANANA"}}]
```

`agy` denies with prose on stderr naming the missing permission and the rule that would grant it
("a tool required the `mcp` permission that headless mode cannot prompt for, so it was auto-denied").
Less structured, but it names the remedy.

## `--permission-prompt-tool` — honoured by `claude`, and 0015 assumed it absent

**Corrected 2026-07-24.** This document recorded the flag as **absent on both vendors**, established
from `--help` alone. decision 0015 inverted its entire mechanism
to a blocking MCP tool on that premise. The premise does not hold for `claude`.

The flag is genuinely undocumented in `claude --help`, so the original reading was not careless — it
was *incomplete*, in the same way the `/usage` row was. What settles it is a **control flag**: pass
something that certainly does not exist, and see whether the CLI discriminates at all.

| invocation | exit | output |
|---|---|---|
| `claude --definitely-not-a-real-flag-xyz -p hi` | **1** | `error: unknown option '--definitely-not-a-real-flag-xyz'` |
| `claude --permission-prompt-tool noop -p hi` | **0** | the turn runs normally |
| `agy --definitely-not-a-real-flag-xyz -p hi` | **2** | `flags provided but not defined` |
| `agy --permission-prompt-tool noop -p hi` | **2** | `flags provided but not defined: -permission-prompt-tool` |

`claude` rejects unknown flags and accepts this one; `agy` rejects both, so *its* absence is real and
now rests on something firmer than help text. Without the control row, a zero exit is not evidence —
"accepted" and "silently ignored" are indistinguishable — which is why
[`FlagProbe`](../tools/Baton.VendorProbe/FlagProbe.cs) establishes the baseline before judging any flag.

### It is honoured, not merely parsed

Accepting a flag is not honouring it, and the table above only proves it *parses* — the prompt `hi`
triggers no tool call, so it can never reach a permission decision. The check that discriminates is a
turn that forces one, with a tool name that exists nowhere:

```
claude --permission-prompt-tool aer_probe_no_such_tool -p --output-format stream-json --verbose \
  "Use the Write tool to create a file named x.txt containing BANANA in the current directory."
```

```
Error calling tool (Write): Error: MCP tool aer_probe_no_such_tool
(passed via --permission-prompt-tool) not found. Available MCP tools: …
```

The CLI reached the permission path, looked for the tool **by the name we invented**, and said so.
A name that exists nowhere could not have come from anywhere but the flag, which is what makes this a
measurement rather than an inference. Without the flag, the identical prompt is simply denied and the
call lands in `permission_denials`.

**So `--permission-prompt-tool` routes permission decisions to an MCP tool** — the same mechanism
decision 0015 already chose, but as the vendor's designated entry
point, consulted for *every* decision, rather than a tool the model must elect to call. That
difference is not cosmetic: a gate the model chooses to invoke is discipline resting on model
behaviour, which is what Architecture Rule 1 exists to forbid. This one is structural.

0015 is therefore not wrong in its mechanism — MCP consultation is proven on both vendors (below) and
is the only path `agy` has at all. What is wrong is its stated justification, that no vendor offers a
permission callback. Whether the decision changes belongs in the decision, not in this reference.

### The full contract, measured

A stdio MCP server registered via `--mcp-config … --strict-mcp-config` and named as
`--permission-prompt-tool mcp__aerperm__approve` receives the whole call:

```json
{ "name": "approve",
  "arguments": {
    "tool_name": "Write",
    "input": { "file_path": "…\\x.txt", "content": "BANANA\n" },
    "tool_use_id": "toolu_01A6fPfyebEFF5judLv4Ug4S" },
  "_meta": { "claudecode/toolUseId": "toolu_01A6…", "progressToken": 2 } }
```

Both replies were exercised in a clean environment where `claude -p` otherwise denies an ungranted
`Write`:

| reply | observed |
|---|---|
| `{"behavior":"allow","updatedInput":{…}}` | call proceeded — **file written** |
| `{"behavior":"deny","message":"…"}` | **file not written**; the message reached the model verbatim, and the call still landed in `permission_denials` with its full `tool_input` |

Two properties worth designing around: **`updatedInput` lets an answer modify the call**, not merely
permit it; and **the denial message is acted on by the worker** — on deny it reported stopping *"rather
than routing around it with a shell write."* A denial can therefore carry a reason, which is what
decision 0022 means by "denial is an answer".

`agy` has no equivalent flag, so on that vendor the same MCP server must be reached by the model
electing to call it — a weaker guarantee, and one the surface should not hide.

### `--permission-mode auto` silently disables the callback

**The single most consequential interaction found so far, and it fails quietly.** Identical prompt,
identical MCP server, identical flags — only the mode differs:

| `--permission-mode` | our tool consulted | out-of-scope write |
|---|---|---|
| `default` | **yes** — one `tools/call` | proceeded, because *we* allowed it |
| `auto` | **no** — zero `tools/call` | proceeded, because the **classifier** allowed it |

Under `auto`, `--permission-prompt-tool` is **never consulted**. No error, no warning, no diagnostic:
the flag is accepted, the server starts, and the permission path simply routes elsewhere. A gate that
is configured, running, and never called looks exactly like a gate that is working.

Tested with a write *outside* the session's own directory, which the classifier's own `allow` text
calls scope escalation (*"wandering into ~/, ~/Library/, /etc, or other repos is scope escalation…
not a local operation"*). It was allowed anyway. So `auto` is also more permissive in practice than
its rule text reads — worth knowing before treating the classifier as a ceiling.

**Consequence: `auto` and a permission callback are mutually exclusive. Pick one.** AER must never
set `auto` on a worker whose gate it relies on, and if a user's own settings turn it on, AER has to
know its gate is dead rather than reporting a permission surface it no longer has. This is a
decision 0028 question as much as a mechanism one:
the more convenient mode is the one that removes the control.

**This callback is not the gate's primary mechanism — a `PreToolUse` hook is.** A hook resolves at
step 1 of `claude`'s six-step permission evaluation order and holds even in `bypassPermissions`;
`--permission-prompt-tool` is step 6 and is exactly the callback `auto` routes around above. `agy`'s
own `PreToolUse` hook is confirmed working and denies — **which is a fact about the vendor, not about
AER's gate, and reading it as the latter is how #710 stayed invisible.** What AER actually shipped
never ran on a Windows worker — the only platform measured; `sh -c` strips the quotes `cmd /c`
chokes on, so a Unix worker is expected to have been fine and has not been measured — and the
measurement and its consequences are in
[`vendor-doc-audit.md`](vendor-doc-audit.md). The full mechanism table, the measurements
behind it, and why the hook is mandatory on every spawned worker live in
decision 0029 — read that, not this section, before
building the gate.

## The subcommand surface — three `claude` subcommands nobody had opened

**Probed 2026-07-24.** Every capability above was probed on `--help` and, where relevant, the slash
commands. **`<subcommand> --help` is a third surface**, and three of `claude`'s subcommands turned out
to hold capabilities the M25 design was building from scratch.

### `claude agents --json` — a live registry of running sessions

Machine-readable, explicitly *"for scripting; does not require a TTY"*. Observed:

```json
[
  { "id": "6567d8cf", "cwd": "…\\source\\repos\\baton", "kind": "background",
    "startedAt": 1784902257007, "sessionId": "…", "name": "Reevaluate user experience from ground up",
    "state": "blocked" },
  { "pid": 18272, "cwd": "…\\source\\repos\\baton", "kind": "interactive",
    "startedAt": 1784925162327, "sessionId": "…", "name": "…", "status": "busy" }
]
```

Every field the room list needs: identity, working directory, a background/interactive distinction, a
start time, a human-readable name the vendor generated, and **a state**. Note `"state": "blocked"` —
the vendor already models *waiting on a human* as a first-class state, which is the distinction
0020's state machine draws and #462's queued-message problem lives inside.

`claude agents` also accepts `--permission-mode`, `--effort`, `--model`, `--mcp-config`, `--add-dir`
and `--settings` as **defaults for dispatched sessions**, plus `--allow-dangerously-skip-permissions`
("make bypass available without defaulting to it") — which is precisely
decision 0028's shape, already expressible.

This is the fan-out surface. It deserves a real feasibility read before AER builds its own.

### `claude auto-mode` — a three-rung permission classifier that already exists

`claude auto-mode defaults` prints ~62 KB of JSON with exactly four keys:

| key | rules | what it is |
|---|---|---|
| `allow` | 17 | carve-outs that are explicitly *not* violations |
| `soft_deny` | 65 | blocked, but overridable — each names what it must cite |
| `hard_deny` | 1 | Data Exfiltration. Never overridable |
| `environment` | 20 | questions about the operator's context that condition the rest |

The rules are **natural-language**, evaluated by a classifier, and user-overridable via an `autoMode`
section in the settings file (`auto-mode config` shows the effective merge, `auto-mode reset` removes
the override, `auto-mode critique` gives AI feedback on custom rules).

Two consequences worth sitting with:

- **A soft/hard denial ladder is not something AER has to invent.** 0022 designed one independently,
  and the vendor's `soft_deny` / `hard_deny` split is the same distinction — a denial you can answer
  versus one that is the end of the conversation.
- **This is content classification driving a permission decision**, which is exactly what
  Architecture Rule 1 forbids *Flow* from doing. It does not forbid Flow from **delegating** it to the
  worker's own classifier. That is a genuinely better answer than reimplementing it, and it is only
  available because the surface was looked at.

### `claude project purge`

Deletes all Claude Code state for a project — transcripts, tasks, file history, config entry. Relevant
to whatever AER does when a room is deleted, and to any claim we make about what "removing a room"
actually removes on disk.

## `agy models` — effort and model are not orthogonal

`agy models` enumerates what the CLI will actually accept. Since at least 2026-08-30 its live
output is one `id<TAB>display name` pair per line (a bare multi-column id grid when first captured
2026-07-28 — the format change broke `smoke-preflight`'s whole-line membership test, #1422). This
fence records the id column only, because the checkers that read it take bare whitespace-split ids:

```
gemini-3.8-flash-high     gemini-3.8-flash-medium   gemini-3.8-flash-low
gemini-3.7-flash-high     gemini-3.7-flash-medium   gemini-3.7-flash-low
gemini-3.6-flash-high     gemini-3.6-flash-medium   gemini-3.6-flash-low
gemini-3.1-pro-high       gemini-3.1-pro-low
claude-sonnet-4-6         claude-opus-4-6-thinking  gpt-oss-120b-medium
```

(Captured 2026-09-05; `gemini-3.5-flash-*` had left the catalogue and `gemini-3.8-flash-*` had joined it
since the 2026-08-30 capture.)

Two things the design assumed otherwise:

- **Effort is baked into the model name**, *and* `--effort low|medium|high` exists as a separate flag.
  Two overlapping controls. What is now known, split from what is not:
  - **They are ONE control with two spellings, and must AGREE. Measured 2026-07-28.**

    ```
    agy --model gemini-3.6-flash-low --effort high
    Error: invalid model selection (--model "gemini-3.6-flash-low" --effort "high"):
    --model gemini-3.6-flash-low conflicts with --effort=high

    agy --model gemini-3.1-pro-high --effort high
    PONG
    ```

    So there is **no precedence to establish** — a disagreement is refused at bind time, before any
    run. This replaces an "acceptance" bullet that stood here: it was measured on
    `gemini-3.1-pro-high --effort high`, an *agreeing* pair, and generalised to the combination.
    True as measured, wider as written.

    Guarded by the sentinel `effort.agy-effort-and-suffix-must-agree`; that check's own docstring
    carries why it earns sentinel status and what a UI must therefore not do.
  - **The rejection datum was never about `--effort` at all. Resolved 2026-07-28.** A real dispatch
    had failed with `Error: invalid model selection (--model "gemini-3-pro" --effort "high"):
    --effort is not supported for model "gemini-3-pro"` — recorded in
    [`OutcomeClassifierTests`](../tests/Baton.Tests/Outcomes/OutcomeClassifierTests.cs), which
    pins it as the stderr AER must surface. This page then read it as evidence that effort support is
    *per-model*, while flagging that reading as an inference and naming the control that would settle
    it: the same dispatch with `--effort` dropped.

    That control is `effort.agy-rejection-is-per-model`, and it has now been run:

    ```
    agy --model gemini-3-pro          (no --effort)
    Error: ... model gemini-3-pro is not recognized as a known model or custom model in settings
    ```

    So `gemini-3-pro` fails on its own. The original error's wording blamed the flag, but the cause
    was an unknown model, and **nothing here establishes per-model effort support.** Any design
    resting on "this model does not support effort" rests on a misread. The stderr pin in
    `OutcomeClassifierTests` is unaffected — it pins what AER must surface, not why agy said it.

  - **A second suffixed model reached effort-value validation**, which is further evidence for
    acceptance: the divergence recorded below on this page was measured while building
    `effort.agy-value-set`, whose probe passes an invalid `--effort` to `gemini-3.6-flash-low` and
    reads the valid set back out of the error. Getting a valid-set message means agy validated the
    *effort*, not the model.

  Corrected 2026-07-27: this bullet previously said the whole "interaction" was unprobed, while the
  rejection datum sat measured in a test's doc comment with no route back here — a fact with no
  canonical home, which is the drift gate `record-once` exists to stop.
- **The grid has holes.** `gemini-3.1-pro` has `high` and `low` but **no `medium`**. A UI offering
  model × effort as a matrix would offer combinations the CLI rejects. This sharpens
  decision 0023: naming by behaviour is right,
  but the available set is per-model, so it has to be *enumerated*, not assumed.
- `agy` serves **Anthropic and OpenAI models too**, not only Gemini. "The Gemini worker" is the wrong
  mental model for it.

## The canonical effort mapping (`quick`/`standard`/`careful`/`exhaustive`)

**Decided 2026-07-25, resting on the vendor's own documented value set, not a behavioural
distinguishability study.** 0023 requires this mapping to be measured before it is written; this
record deliberately narrows *what* gets measured. Whether `high` and `xhigh` produce a
distinguishably different run was judged not worth a live measurement campaign to answer — instead,
[`tools/vendor-verify`](../tools/vendor-verify/verify.py)'s two new sentinels
(`effort.claude-value-set`, `effort.agy-value-set`, `--sentinels`-covered) guard the one thing this
mapping actually depends on: that each vendor's *set* of accepted values hasn't moved. If a vendor
adds, removes, or renames a level, the sentinel fails the next time it's re-run — the "we'll know
when it changes" property is now real, not assumed.

| canonical | `claude` | `agy` | `codex` |
|---|---|---|---|
| `quick` | `low` | `low` | `low` |
| `standard` | `medium` | `medium` | `medium` |
| `careful` | `high` | `high` | `high` |
| `exhaustive` | `max` | `high` *(collapsed)* | `max` |

**Disclosed collapse, per 0023's own rule.** `agy` has no fourth level — `careful` and `exhaustive`
both resolve to `agy`'s `high`, and the UI must say so at the point of choosing rather than let two
visibly different canonical choices silently produce the same run. `claude`'s `xhigh` is not reached
by any canonical level; it remains available only as a raw, unvalidated escape hatch (the same path
`#566` already threads through `WorkerInvocation.Effort`), not through the canonical picker.
Codex likewise keeps `xhigh` and `ultra` on the raw path. Availability is model-specific, and Baton
rejects unknown models and unsupported pairs before a process starts. Note which `model/list` that
check reads (#1875): a **dated recording** of one, `src/Baton.Vendors/codex-model-list-2026-09-04.jsonl`
(codex-cli 0.153.2, 2026-09-04, `includeHidden:false`), kept as the raw app-server JSONL the CLI
wrote — initialize line included, which is where that CLI version comes from — embedded in
`Baton.Vendors`, and parsed into `CodexWorkerAdapter`'s validation table by the same parser live
discovery uses. Live `model/list` is asked separately — `DiscoverCapabilitiesAsync`, from the
real-dispatch preflight that prints a worker's skills (`DispatchCommand`; that preflight prints only
the skill items, so the models it discovers are asked for but never shown), not from `baton dispatch
--list-capabilities`, which prints static text and returns without starting a CLI — and is
**not** cross-checked against the recorded table at dispatch time, so a model the installed CLI has
gained since the recording is refused locally until the recording is replaced. That is the trade the
issue asked for: correcting an effort set is a data change with provenance, not a hand edit to a table
that merely claimed to be probed. Subagent feature switches are
enforced independently—an `ultra` effort label is not treated as evidence that delegation occurs.

**A genuine vendor divergence, measured while building the sentinel above, worth its own line: the
two vendors fail an unknown `--effort` value in opposite directions.** `agy` hard-errors (exit 1) and
refuses to run. `claude` does not error at all — it silently falls back to its **default** effort,
prints a warning on stderr, and still executes the turn (exit 0):

```
$ claude -p "..." --effort __not-a-real-value__
Warning: Unknown --effort value '__not-a-real-value__' — ignoring it and using the default effort. Valid values: low, medium, high, xhigh, max.
```

This matters operationally, not just as trivia: if a malformed or mismapped effort string ever
reached `claude` through the raw passthrough (`#566`), the run would **not** fail — it would quietly
run at a different effort than requested, with the only signal being a stderr line AER does not
currently surface anywhere. `agy` would at least abort loudly. Worth a defensive check wherever
`#566`'s raw string is consumed, so a typo degrades to a visible failure on `claude` too rather than
a silent, wrong-effort success.

## The canonical model-purpose mapping (`deep`/`balanced`/`fast`)

**Decided 2026-08-17 (#1330), resting on what this repo already records about each vendor's model
set, not a new live probe.** 0023 requires this mapping to be measured before it is written; per its
own constraint 3 — the same narrowing the effort mapping above already applies — that measurement is
the vendor's own model *set* and its own tier naming, not a behavioural distinguishability campaign
judging how "deep" one model's answers actually read next to another's.
[`tools/vendor-verify`](../tools/vendor-verify/verify.py)'s two new sentinels (`models.agy-value-set`,
`models.claude-alias-floor`, both `--sentinels`-covered) guard the one thing this mapping actually
depends on: that each vendor's model set hasn't moved out from under it.

**Not to be confused with** `src/Baton.Vendors/WorkerTiers.json`'s
frontier/standard/cheap/minimal/orchestrator vocabulary — that is role-dispatch's own internal
dispatch-tier system, unrelated to this one, and never rendered to a person.

| canonical | `claude` | `agy` | `codex` |
|---|---|---|---|
| `deep` | `opus` | `gemini-3.1-pro-high`, `claude-opus-4-6-thinking` | `gpt-6-astra`, `gpt-5.6-sol` |
| `balanced` | `sonnet` | `gemini-3.8-flash-high`, `gemini-3.7-flash-high`, `gemini-3.6-flash-high`, `gemini-3.1-pro-low`, `claude-sonnet-4-6` | `gpt-5.6-terra` |
| `fast` | `haiku` | `gemini-3.8/3.7/3.6-flash-medium`, `gemini-3.8/3.7/3.6-flash-low`, `gpt-oss-120b-medium` | `gpt-5.6-luna` |

**`claude` — fully placed, no collapse.** `claude` ships no model-list subcommand: `claude models` is
answered as a prompt and spends usage rather than enumerating anything
([`vendor-doc-audit.md`](vendor-doc-audit.md) item 2 under "Three behaviours found after this audit
closed", restated in decision 0023 §4). What the
CLI *does* expose, and what `ClaudeWorkerAdapter.ModelAliases` already commits to as the stable
interface, is three named aliases — `sonnet`, `opus`, `haiku` — each always resolving to that tier's
current model. Three aliases, three canonical purposes, and the assignment is not new: it is the
design corpus's own worked example for this exact vocabulary — `docs/design/*` was deleted, not
archived, in the spec v2.0 reset (spec/baton.md §11), so two-thirds of that example (`opus`/deep,
`haiku`/fast: *"Opus 4.8 · deep work"*, *"Haiku 4.5 · fast"*) survives only as the quote
[0023](decisions/0023-effort-and-models-are-named-by-behaviour.md) preserves verbatim; the
`Sonnet 5 · balanced` third is not otherwise recorded. Each canonical purpose lands on a distinct
alias — nothing here collapses.

**`agy` — placed by family and effort (operator-run, 2026-09-05, #1342).** `agy models` is a real,
machine-readable subcommand — already shelled out to by `AgyWorkerAdapter.DiscoverCapabilitiesAsync` —
and its fourteen-entry catalogue of that day is captured above in § "`agy models`". #1330 left this
column open because the design corpus's single label (*"Gemini 3 Flash · fast"*) could not be bridged
onto versioned, effort-suffixed ids; the operator ran the catalogue and placed it by one rule, stated
here once and applied mechanically in `DepthTierMapping.AgyByModel`:

- **deep** — the pro family at high effort and the opus-thinking entry (`gemini-3.1-pro-high`,
  `claude-opus-4-6-thinking`).
- **balanced** — every flash family at high effort, the pro family at low effort, and the sonnet entry.
  The 3.8 Flash placement has an empirical anchor: `benchmarks/deepswe/2026-09-04` has it at 74 % at
  high, level with Opus at medium (69 %), which is the Claude column's `balanced` operating point.
- **fast** — every flash family at medium or low effort, and `gpt-oss-120b-medium`.

Two things this placement does not claim. It does not rank the three flash generations against each
other (they share a tier; the catalogue, not this table, says which is current). And agy's own effort
suffix stays a separate axis (§ "`agy models`" above): an id's purpose is read from the whole string,
so `gemini-3.8-flash-high` and `gemini-3.8-flash-low` land in different tiers on purpose. An id the
table does not carry — a retired family such as `gemini-3.5-flash-*`, or a future one — resolves no tier
until it is placed here.

**`codex` — placed from the dated visible catalog.** The four current families whose product roles
were recorded by the 2026-09-04 host catalog map without collapse: Astra and Sol to deep, Terra to
balanced, and Luna to fast. Older visible models remain deliberately unplaced; a recognizable name
is not evidence of the model-purpose tier the current product assigns it.

## A blocking MCP tool holds a turn open — on both vendors

The mechanism decision 0015 depends on. A dependency-free stdio
MCP server exposed one tool whose handler did not reply until an out-of-band answer file appeared. A
watcher minted a random token **after** observing the call start, so a correct answer proves the turn
genuinely waited.

| vendor | blocked for | call metadata returned |
|---|---|---|
| `claude` | 10.9 s | `claudecode/toolUseId`, `progressToken` |
| `agy` | 10.3 s | `antigravity.google/conversation_id`, `artifacts_dir`, `progressToken` |

Two implementation constraints fall out:

- **The server is spawned twice by `claude`** — once to enumerate tools (killed straight after
  `tools/list`), then again for the real turn. It must be cheap to start and hold **no** in-memory
  state across spawns.
- **`agy` hands us the resume key at gate time.** `antigravity.google/conversation_id` is exactly what
  `agy --conversation <id>` resumes. A gate persisted with that id survives a host crash.

## Usage, cost and quota

**Probed 2026-07-24, re-measured 2026-08-28.** An earlier pass concluded *"neither vendor exposes
remaining quota or a reset time."* **That was wrong, and it was wrong for a methodological reason
worth recording: it probed the CLI's `--help` and subcommand list, not the in-session slash
commands.** Those are different surfaces, and the answer lives in the second one. The asymmetry this
section's title once named — `claude` headless, `agy` not — has since narrowed: a CLI update made
`agy -p "/usage"` answer headlessly too (see the `agy` section below), so both vendors now report
plan usage without a model turn.

> **Probe both surfaces.** A capability absent from `--help` may still exist as a slash command, and
> a slash command may still work under `-p`. Checking one and concluding about the other is how the
> first pass produced a confident wrong answer about the single number this product runs on.
>
> **On Windows, do not probe slash commands through Git Bash.** MSYS path conversion rewrites a
> leading `/usage` into `C:/Program Files/Git/usage` *before it reaches the CLI*, and the model then
> answers about that path — which reads exactly like "the command does not exist." Use PowerShell, or
> `MSYS_NO_PATHCONV=1`.

### `claude` — everything needed, headlessly

`claude -p "/usage"` and `claude -p "/cost"` both return the same live report:

```
Current session: 21% used · resets Jul 25, 12:09am (America/New_York)
Current week (all models): 67% used · resets Jul 27, 5:59am (America/New_York)
Current week (Fable): 0% used
Last 24h · 1811 requests · 21 sessions
  88% of your usage came from subagent-heavy sessions
  82% of your usage was at >150k context
```

So all four things decision 0026 and `#479` needed
are available: **percent consumed, a real reset instant, a per-model breakdown, and request
counts** — plus behavioural attribution (*what* is spending the plan), which nothing in the design
anticipated and which is more actionable than the percentage alone.

The corpus's mockup number — *"Claude plan · 72% of this week's limit"* — was **not** a designed
placeholder. It is the shape of a number the CLI already reports.

**One caveat the surface must carry**, in the CLI's own words: *"Approximate, based on local sessions
on this machine — does not include other devices or claude.ai."* The figure is **machine-local**, so
AER must not present it as an account-wide truth.

Separately, every `stream-json` result event carries `input_tokens`, `output_tokens`,
`cache_creation_input_tokens`, `cache_read_input_tokens`, `model`, `service_tier` and
**`total_cost_usd`** — the API-equivalent cost, computed by the CLI. No price table to maintain and no
drift to chase. Observed on a trivial *"reply with ok"* turn: **$0.2463**, of which essentially all was
24,619 cache-creation tokens. Cache writes dominate, which is worth knowing before designing a
per-turn cost display.

**The terminal `result` line is not the only place this lives.** A separate 2026-09-01 capture (this
file's history table, top row) confirms the same four keys that row names show up on
`message.usage`, mid-stream, on every `type=="assistant"` line, not just the terminal one.
`total_cost_usd`/`model` were not re-checked on that line and are not claimed there. This is what
makes a live, not-yet-terminal usage figure possible at all (`tools/fleet-glass/pusher.py`'s
`rooms[].live`, spec/baton.md §6) — a fact the #1613 lane's own audit of this document and
`vendor-doc-audit.md` missed. Both registers were quiet on the per-message shape specifically, and
that quiet got mistaken for a finished investigation instead of an open one.

**But only two of those four keys are that message's real figures (#1706, measured 2026-09-02).** The
paragraph above is true as a *shape* claim and was read as a *value* claim, which is the defect this
correction exists to close rather than leave to inference — the keys are present; two of them are
placeholders:

| Key on a mid-stream `assistant` line | Real? | Measured over two whole rooms |
|---|---|---|
| `input_tokens` | **no** | the literal constant `2` on all 153 and all 94 distinct `message.id`s |
| `output_tokens` | **no** | a 1–21 stub; Σ = 1,362 and 691 against real totals of 113,293 and 66,924 |
| `cache_creation_input_tokens` | yes | Σ over deduped ids reproduces the terminal whole-tree figure to 98.0% / 100.0% |
| `cache_read_input_tokens` | yes | same, 97.5% / 100.0% |

These are the values emitted when a message begins, never revised: across 93 and 82 repeat lines,
**zero** carry a `usage` object differing from that id's first sighting, so no "read the last one"
strategy recovers anything. Nor is there a streaming-protocol event to read them from instead —
**the shipped `stream-json --verbose` mode emits no `message_start`, `message_delta` or
`message_stop` event at all.** Its full event-type set, enumerated over both captures, is `assistant`,
`user`, `system` (subtypes `init`, `thinking_tokens`, `task_started`, `task_progress`,
`task_notification`, `task_updated`, `hook_started`, `hook_response`, `code_change_published`,
`vcs_state_changed`), `tool_progress`, `rate_limit_event`, and one terminal `result`.

**Scoped: what was NOT searched.** The statement above is about the MAIN THREAD's own per-message
figures. Other live lines in the same captures do carry real token counts, they were found but not
pursued, and naming them is cheaper than letting a later reader conclude from this section's silence
that the search was exhaustive:

| Line | Field | Seen |
|---|---|---|
| `system`/`task_progress` | `usage.total_tokens` (plus `tool_uses`, `duration_ms`) | cumulative per subagent task |
| `system`/`task_notification` | `usage.total_tokens` | 133,082 on one task |
| `user` | `tool_use_result.usage` | a FULL real usage object for a completed Task, including `output_tokens` and `output_tokens_details.thinking_tokens`, plus a `usage.iterations[]` array |
| terminal `result` | `usage.iterations[]`, `subagent_stats` | both unread by any parser here |

None of these is the main thread's own per-message output, so the engineering conclusion
(spec/baton.md §3: bill cache-creation, label the figure a floor) stands. But `tool_use_result.usage`
and `usage.iterations[]` are where an attempt to narrow the gap should start, and `subagent_stats` is
what a claim about subagent fan-out has to be checked against — §3 records a claim of exactly that
kind being falsified by it.

*Evidence: `dispatch-implement-3dc5e21a` and `dispatch-implement-5d9686dd`'s real `.stdout.log`
captures, claude 2.1.257 era, enumerated event-by-event rather than sampled.*

### `agy`'s terminal `result.usage` IS the cumulative Σ of its per-turn lines (#1706 review, measured 2026-09-02)

The claude finding above only has a meaning because the other vendor's incremental usage is a
*measurement* rather than a floor — spec/baton.md §3 leans on agy's under-read being **zero**. That was
an assumption, asserted from a hand-built fixture; here it is measured, on real captures, with the
discriminating alternative (terminal = the LAST turn, not the Σ) separated by three orders of magnitude.

Σ over every `state == "DONE"`, `step_type == "agent_response"` `step_update`'s `usage`, against the
same room's terminal `result.usage`, field for field:

| Room | `agent_response` usage lines | Σ vs terminal | Last turn's `input_tokens`, for contrast |
|---|---|---|---|
| `dispatch-implement-38c24d11` | 70 | **identical on all five fields** (`input 595,684`, `output 199,256`, `thinking 19,715`, `cache_read 8,459,818`, `total 794,940`) | 5,164 |
| `dispatch-implement-46d513e7` | 258 | **identical on all five fields** | — |
| `dispatch-implement-55aa75ae` | 263 | **identical on all five fields** | — |

So on agy the live Σ and the authoritative terminal figure are the SAME number, and
`billedUnderReadTokens` is a true measured zero rather than a fixture artifact. `AgyTerminalUsageIsCumulativeTests`
pins the relationship against a line set copied from `38c24d11`'s real capture, and
`ExecutionUsageProjectorTests`' zero-under-read control reads the same copied lines rather than a set
constructed to sum correctly.

**A trap worth naming, because it defeated an earlier attempt to measure this.** agy's terminal
`num_turns` is **1** on all three of these rooms — including the 263-`agent_response` one. It counts
USER turns, not agent responses, so filtering a corpus for `num_turns > 1` to find a "multi-turn" agy
room finds nothing and looks like evidence that no such capture exists. Count `agent_response`
`step_update` lines instead.

*Evidence: the three rooms' real `.stdout.log` captures under `~/.baton/rooms`, summed
line-by-line rather than sampled. Scoped to `agent_response` steps — `tool` steps carry no `usage`
object and are not part of either side of this equality.*

### `agy` — works headlessly too, as of a CLI update

**Superseded 2026-08-28.** The prior measurement on this row — `agy -p "/usage"` **not a built-in
command**, headless print mode denied it or produced only conversational prose — is superseded by a
CLI update, not corrected: `/usage` genuinely did not answer under `-p` when that measurement was
taken. **Measured live 2026-08-28** (both CLIs in print mode, no model turn spent):
`agy -p "/usage"` now returns structured, tab-separated rows, one per quota family:

```
Gemini Models	Weekly Limit Remaining	72%	2026-08-29T19:34:12Z
Gemini Models	Five Hour Limit Remaining	42%	2026-08-28T16:36:17Z
Claude and GPT models	Weekly Limit Remaining	<pct>	<reset instant>
Claude and GPT models	Five Hour Limit Remaining	<pct>	<reset instant>
```

Percent remaining and a real reset instant, per family, headlessly — the thing #479 needed and this
row previously recorded as absent on `agy`. Per-turn **token** usage separately lives in the
`--output-format stream-json` `result` event (#1088), unaffected by this update.

Not re-verified by this update: `--help`, the subcommand list, `--log-file`, and
`~/.gemini/antigravity-cli/cache/conversation_metadata.json` — the prior "not found" measurements on
those surfaces stand until someone re-probes them.

**One surface remains unchecked** — `agy`'s local RPC server (below), to which no usage query has been
put. It is no longer the *only* place structured data could live, though: stdout carries it (#1088).

### `agy` has a local RPC server — a *second* structured surface, still unenumerated

**Reframed 2026-08-10 (#1088).** The *primary* structured surface is agy's own CLI
`--output-format stream-json` — `init`/`step_update`/`result` on stdout, now observed (see the matrix
row and the history table). The "tried `--output-format` and it missed" below was the **same probe-grammar
bug**: `Aer.VendorProbe` handed agy claude's `-p`/`--verbose` argv, so stream-json never engaged and the
search moved to the RPC server. That server is real and still unenumerated, but it is a *second* surface
— not "the structured surface we recorded as absent."

**Corrected 2026-07-24.** "Structured output: not found" was established on `--help` and on trying
`--output-format`. It missed that **every `agy` run starts a local server and prints its ports**:

```
Starting language server process with pid 29564
Language server version: 1.1.7
Language server listening on random port at 50871 for HTTPS (gRPC)
Language server listening on random port at 50872 for HTTP
```

Confirmed live: an HTTP request to that port during a run returns a real Go HTTP response
(`404`, `Vary: Origin`, `X-Content-Type-Options: nosniff`) rather than a connection refusal. The
server is there, it is reachable, and the port is discoverable from `--log-file`.

**Not yet enumerated:** the service and method names. A guessed Connect RPC path 404s, and scanning
the 166 MB binary for `*.Service` paths found none, so the service surface is likely in the spawned
language-server process rather than the CLI binary. This is a partial, not an absence.

**Superseded, #508 → #525.** The Python SDK (`pip install google-antigravity`) was read and answers
what this RPC surface would have: structured events, per-turn usage, and a `deny()`/`allow()` gate.
But the SDK path — for both vendors — is foreclosed by auth policy, not capability: neither SDK
supports a subscription login, only API keys, which CLAUDE.md's premise rules out. **The integration
choice is CLI, both vendors, and the SDK question is closed** — see
[`vendor-doc-audit.md`](vendor-doc-audit.md#should-aer-drive-sdks-instead-of-clis-no-and-the-reason-is-contractual)
for the full reasoning. The RPC surface itself remains genuinely unenumerated, but no design decision
is waiting on it anymore.

### The design consequence

**Do not fake parity.** claude reports plan usage, reset times, and per-turn **dollar** cost; agy
reports per-turn **token** usage (the `stream-json` `result`'s `usage` object, #1088) but no dollar
figure and no plan-level quota/reset. The asymmetry is now token-vs-dollar and per-turn-vs-plan, not
"everything vs nothing" — but it still has to be visible in the interface rather than smoothed into a
single half-trustworthy number, the same rule
decision 0023 applies to effort levels, where a
collapse is disclosed rather than silently faked.

**And design the surface for "not measured", not for "does not exist" — the predicted figure arrived.**
This section used to say agy's usage number might surface from the local RPC server or the Python SDK
"next week"; instead it was on stdout all along, hidden by the probe bug (#1088). A UI that had
hard-coded *"agy has no usage"* would now be wrong. The honest element is one that can say *"no usage
data from this worker"* and carry a figure once one appears, without being redesigned — agy's is here.

### Baton's usage field, per adapter (#1360)

Baton's own consumer of the facts above, not a new vendor measurement — see
`docs/agents/invoking-baton.md`'s `usage`/`linkedFromUsage` entry for the field shape.
`ExecutionUsageProjector` reads whichever registered adapter's `TryParseFinalUsage` recognizes a line
in the execution's captured stdout; nothing here is fabricated when a line does not match.

| field | claude | agy | codex |
|---|---|---|---|
| `wallClockMs` | always, once Core has recorded both ends of the execution's lifetime — derived from the ledger, not from either vendor | same | same |
| `tokensIn` / `tokensOut` | `usage.input_tokens`/`output_tokens` off the `stream-json` `result` event — **measured (#1569) to exclude `cache_read_input_tokens`**, not include it: a captured envelope with `input_tokens: 2` alongside `cache_read_input_tokens: 38741` is only coherent if the two are disjoint | `result.usage.input_tokens`/`output_tokens` — **only when agy reports the split**; a run reporting a single combined `total_tokens` (both shapes are observed, above) leaves both fields absent rather than guessing a direction. Measured (#1569) on a captured envelope: `input_tokens + output_tokens == total_tokens` exactly, with `cache_read_tokens` outside that sum — same exclusion as claude | `turn.completed.usage.input_tokens` / `output_tokens`, observed per turn; Baton subtracts cached input from the inclusive input count before projecting `tokensIn` |
| `turns` | `num_turns` off the same `result` event | `result.num_turns` | `1` for each successful `turn.completed` event |
| `cacheReadTokens` / `cacheCreationTokens` (#1569) | `usage.cache_read_input_tokens` / `usage.cache_creation_input_tokens`, siblings of `input_tokens` on the same event | `result.usage.cache_read_tokens`; agy has never been observed reporting a cache-creation figure, so `cacheCreationTokens` is always absent on this vendor | `turn.completed.usage.cached_input_tokens` / absent |
| `thinkingTokens` (#1569) | **nested**, not a sibling: `usage.output_tokens_details.thinking_tokens` | `result.usage.thinking_tokens`, flat | `turn.completed.usage.reasoning_output_tokens` |
| dollar cost (`total_cost_usd`) | real, but has no field in `usage`'s additive shape (issue #1360 scoped it to tokens/turns/wall-clock; #1569 added the cache/thinking counts, not cost) | n/a (agy reports none, per this section) | not reported in the observed JSONL; any API-equivalent cost needs a dated price table and explicit estimate label |

**The gate every one of these rows sits behind: structured-output mode.** Every vendor-reported field
lives in the `stream-json` terminal line this whole document has been describing — if a dispatch runs
in plain-text mode instead (today's default for an ordinary `baton run`/`baton dispatch` lane; see
`RoleDispatch.ToBinding`'s own remarks on why claude stays text-mode there), stdout is prose with no
such line in it, and every field in this table is absent for that execution regardless of what the
vendor is otherwise capable of reporting. `wallClockMs` is unaffected either way.

**Provenance of the claude row's `#1569` measurements:** both come from a genuine `claude -p ...
--output-format stream-json --verbose` invocation's own `result` line, captured verbatim (the same
envelope is pinned as a test fixture in `tests/Baton.Vendors.Tests/ClaudeFinalUsageParsingTests.cs`)
— but that invocation ran as a `run_command` tool call inside an agy-orchestrated lane's own
transcript, not from a Baton-dispatched claude execution's own captured stdout. Same binary, same
flags Baton's own dispatch path uses, so the wire format claim holds; no dispatched claude lane on
this machine had yet written a top-level `"type":"result"` line to its own `.stdout.log` at the time
this was measured, so that narrower claim is not made here.

**On claude, `tokensOut` is a top-level count, not a whole-tree one (#479, above).** The dispatched
worker's own subagent fan-out spends tokens the `result` event's `usage` object does not carry — a
22% shortfall was measured against a single subagent, and it grows with the tree; `modelUsage` is
where the complete figure lives. AER's own depth-1 subagent cap makes one level of fan-out the normal
case, not an edge case, so treat a claude execution's `tokensOut` as a lower bound whenever that
execution's worker could have spawned a subagent.

## Neither `--mode` nor `--add-dir` stops `agy` writing a file

Read this before treating either flag as a safety boundary. Check:
`agy.plan-mode-does-not-deny-writes`.

One `agy -p --mode plan --add-dir A` run, asked for two absolute writes — one into `A`, one into a
directory `B` that was never named on the command line:

| target | named to agy | result |
|---|---|---|
| `A/probe-out/review.md` | yes, via `--add-dir` | written |
| `B/leaked.txt` | no | written |

No prompt, no refusal, exit 0, and both files present on disk. Identical across two runs.

The name suggests otherwise on both flags, which is the whole reason this is written down. `plan`
constrains what the model *sets out* to do, not what its tools are permitted to do; `--add-dir`
grants visibility rather than withholding it elsewhere.

The verdict is read against the file on disk rather than the CLI's report, and that is not
fastidiousness: the first attempt to establish this returned "workspace empty, nothing written"
three times and was wrong, because `agy -p` ignores the process working directory (see #472 above)
and a prompt naming "your current directory" pointed somewhere nobody was watching. A control that
cannot separate *refused* from *never attempted* measures nothing.

This is also the narrower reading of `agy.fails-closed-headless`. That check auto-denies an ungated
**shell command**; the write tool is a different tool and answers differently.

## An `agy` write hook is told which file the write targets

The counterpart to the section above: nothing agy offers bounds a write, so AER's own hook is the
only candidate — and it can only bound what the payload tells it. Check:
`agy.hook-payload-carries-write-path`.

A `PreToolUse` payload matched on `write_to_file` carries `toolCall.args`, and the target appears in
it as the **absolute** path the prompt named — not a basename, not a path relative to a cwd `agy -p`
ignores anyway (#472). The key is **`TargetFile`**, PascalCase. So a path-bounded gate is
implementable here, which is what #679 rests on.

**Scoped to `write_to_file`.** `AgyWorkerAdapter.WriteTools` names four tools, and this measured
one; the other three (`replace_file_content`, `multi_replace_file_content`, `generate_image`) may key
their target differently or not carry one. That is why the gate this feeds denies a write whose path
it cannot find rather than allowing it: an unmeasured key fails loudly instead of silently unbounded.

This was documented before it was measured, and that is the point of the check rather than a
formality: `agy__hooks.md` describes `toolCall.args`, and this file already records two places the
same documentation is wrong — `--cwd` is documented and does not exist, and `modelName` is present
and undocumented. `agy.hook-env-inherited` reports `toolCall.name` for `run_command`, a different
tool and a different field, and never touched this question.

The absolute form is the load-bearing half. `OutboxPath` refuses to resolve a relative candidate
against the hook process's inherited cwd, so a payload naming only `written.md` would leave nothing
to compare a boundary against — which is why the check fails on a basename rather than passing on
"the file is named somewhere".

## `agy` permission grammar

Rules live in `~/.gemini/antigravity-cli/settings.json` under `permissions.allow` / `.deny`. This is
the **only** settings path — there is no project-local override file. Prefixes (from vendor docs):
`read_file`, `write_file`, `read_url`, `execute_url`, `command`, `unsandboxed`, `mcp`.

MCP rules take `mcp(server/tool)`, `mcp(server/*)` or `mcp(*)`. Observed: `mcp(aerhuman)` — the bare
server name — **does not match**; `mcp(aerhuman/*)` does.

**Command rules are matched literally, against the whole command line.** The single most consequential
finding for the permission surface — and **re-confirmed against 1.1.7 on 2026-07-24**, deliberately,
because the vendor's documentation says the opposite:

> "Each whitespace-separated token is evaluated as an **anchored regular expression**."
> `command(npm run (build|lint|test))` matches `npm run build` and `npm run test`.

It does not. Tested with the docs' own alternation form, one rule at a time, against `node --version`:

| rule | if literal | if regex | **observed on 1.1.7** |
|---|---|---|---|
| `command(node)` | denied | denied | denied |
| `command(node .*)` | denied | *granted* | **denied** |
| `command(node (--version\|--help))` | denied | *granted* | **denied** |
| `command(node --version)` | granted | granted | **granted** |

Only the exact whole line was granted. The two rules that discriminate between the readings both
failed, including the documented example's own shape.

> **This is the one row where the documentation is wrong and our measurement was right.** Every other
> correction in this audit ran the other way. It is the reason documentation carries the evidence class
> *documented* rather than *verified*: a vendor's claim about its own product is still a claim. Only
> the run settles it — in both directions.

The original four runs, against 1.1.6, agreeing:

| rule | result |
|---|---|
| `command(node)` | **denied** — a bare binary name does not cover its invocations |
| `command(node .*)` | **denied** — so the match is not a regex, despite the docs' `command(npm run (build\|lint\|test))` example |
| `command(node --version)` | **granted, ran** |
| `command(node C:/…/escape.js)` (exact, separate run) | **granted** |

**Consequence: AER cannot pre-authorise a *family* of commands on `agy`, only enumerate exact command
lines.** A ceiling like "this room may run git, but not push" is not expressible as an `agy`
allow-rule. Where a family-shaped grant is needed there, the enforceable instrument is `--sandbox`
plus targeted `unsandboxed(…)` escapes, or the MCP consultation path — not `permissions.allow`.
Design the permission surface accordingly rather than assuming prefix semantics.

**On `claude` the same ceiling is expressible, and enforced** — see below. The limitation is `agy`'s,
not a property of the problem, and 0004's framing of the two vendors depends on the difference.

## `agy --help` is not a complete list of its flags, and we cannot yet enumerate them

**Established 2026-07-24, and it invalidates every `agy` negative that rests on `--help` alone.**

`--remote-control` is **accepted** by `agy` (it starts an OAuth login) and appears **nowhere** in
`agy --help`. So the help output is demonstrably not the full flag surface, and "not in `agy --help`"
is not a statement that a flag does not exist.

Two attempts to enumerate the real set, and why neither worked:

- **Guessing plausible names is not evidence.** Five invented candidates for a per-call grant flag
  (`--allowedTools`, `--allowed-tools`, `--allow-tool`, `--permission`, `--tools`) were each rejected
  exactly as an invented control flag is. That establishes only that those five names are not flags —
  a vanishing slice of an unbounded namespace. It was briefly written up here as though it firmed up
  the negative. It does not.
- **Binary string adjacency does not work either.** `sandbox`, `add-dir` and `project` sit packed
  together in the binary, which looks like a flag table until you notice `.gemini`, `plugins` and
  `install` are packed with them — **Go interns strings grouped by length**, so all six are neighbours
  only because they are seven characters long. Confirmed on the 8-, 12- and 14-character groups.
  Adjacency to a known flag carries no information.

**What would actually settle it:** vendor documentation, the public Python SDK (which likely mirrors
the CLI surface), or an exhaustive test of every flag-shaped string in the binary against a control —
tractable only if the candidate set is first narrowed by something better than shape.

Until then, every `agy` row reading "not found" is scoped to the surfaces named on it, and the flag
surface specifically is **known to be incompletely enumerated**. Do not design against those absences
as though they were established.

## `claude --allowedTools` is pattern-matched — but it is ~~a family ceiling~~ **not a ceiling at all**

> **Corrected 2026-07-25 (#527).** This section was headed *"expresses a family ceiling"*. Every
> measurement in it still holds, but **"ceiling" was a guarantee word the evidence had not earned**,
> and this document is where [#529](https://github.com/aer-works/baton/issues/529) came from.
>
> What was measured below is that the **pattern discriminates between commands of the same tool**.
> What was *not* measured is whether the model can reach the same goal through a **different tool** —
> and it can. With `--disallowedTools Write`, the model wrote the file using `Bash`; with
> `Edit,Write,NotebookEdit` withheld (the string `ClaudeWorkerAdapter` emitted for a withheld-write
> grant until #649 moved those names onto its `PreToolUse` hook) it used `Bash`
> and `Read`. `Bash` alone defeats withheld writes, withheld reads and withheld network.
>
> So the correct reading is: **`--allowedTools`/`--disallowedTools` bound which *tool* runs, never
> what the worker can *achieve*.** They are a pre-approval and routing mechanism, not a security
> boundary. The mechanisms measured to bound an *operation* are the four always-fires primitives in
> [the doc audit](vendor-doc-audit.md).
>
> Keeping the original heading struck through rather than deleting it, because the wrong reading is
> re-derivable from the evidence below if nobody says why it's wrong.

**Probed 2026-07-24.** `--allowedTools` takes **patterns**, stated in a help example nothing had acted
on: `Bash(git *) Edit`. Measured with both a control and a negative control, on `git --version`:

| grant | result |
|---|---|
| *(none — control)* | **denied** |
| `Bash(git *)` | **ran** |
| `Bash(git --version)` | ran |
| `Bash(npm *)` *(negative control)* | **denied** |

The negative control is what makes this evidence rather than a coincidence: the pattern discriminates
instead of waving everything through. Then the canonical ceiling, allow-family plus deny-subset —
`--allowedTools "Bash(git *)" --disallowedTools "Bash(git push*)"`:

| command | result |
|---|---|
| `git status` | **ran** |
| `git push` | **denied** — *"denied by the permission prompt, so nothing was pushed"* |

**Read that table for exactly what it says.** `git push` *via `Bash`* was denied. It does not
establish that the push could not happen — an unrestricted `PowerShell` tool, or a script written and
then executed, reaches the same outcome. Same-tool discrimination is real; cross-tool containment was
never tested and, per #527, does not hold.

### Subcommand granularity and command-line matching extent (#1461)

**Measured 2026-08-31, `claude` 2.1.251, Windows.** #1456's second reader flagged two gaps in the
canonical ceiling measurement above: it discriminates at the PROGRAM level (`npm` vs `git`), not the
subcommand level, and it never tested whether `Bash(pattern)` matches the whole invoked command line
or only its leading tokens. Same throwaway-git-dir method as above, `--allowedTools "Bash(git
diff*)"` (no blanket `git *`), `--output-format stream-json --verbose` so the actual Bash
`tool_use`/`tool_result` pairs are read directly rather than trusted from the model's prose summary
(a model can silently switch to a different tool, or answer from its own knowledge of the repo,
without the underlying Bash call ever running — the `json`-summary format alone cannot tell the
difference).

**Subcommand granularity: the pattern does not gate it at all for commands claude classifies as
read-only.**

| command | grant | result |
|---|---|---|
| `git log` | `Bash(git diff*)` | **ran**, no denial |
| `git status` | *(no `--allowedTools` at all)* | **ran**, no denial |
| `git log` | *(no `--allowedTools` at all)* | **ran**, no denial |
| `git rebase --abort` | `Bash(git diff*)` | **denied** — *"This command requires approval"* |
| `git log` | `Bash(git diff*)` **plus** `--disallowedTools "Bash(git log*)"` | **denied** — *"Permission to use Bash with command git log has been denied."* |

An unlisted git subcommand is **not** denied the way `Bash(npm *)` was denied under `Bash(git *)`
(the program-level control above): `git log`/`git status` ran identically whether the grant was
`Bash(git diff*)` or absent entirely, so it is claude's own internal command-risk classification —
not the `--allowedTools` pattern — that let them through. `git rebase --abort` (unlisted and
mutating) was denied, so the mutating/read-only split `review`'s grant relies on still holds in
practice — but it holds because claude classifies `git rebase` as needing approval, not because
`Bash(git diff*)` excludes it. An explicit `--disallowedTools` pattern still wins over the
auto-approve, consistent with the deny-over-allow precedence the canonical ceiling above already
established. This does not contradict that section's control row (`git --version` under *no grant* →
**denied**): the outcome under no grant is per-subcommand, decided by claude's risk classification —
`git --version`/`git rebase` land on the approval-required side, `git log`/`git status` on the
auto-approved side — not a single blanket answer for "no grant."

**Command-line matching extent: `Bash(pattern)` matches the whole invoked command line, and
non-file-mutating chained/piped commands execute.**

| command | grant | result |
|---|---|---|
| `git diff; echo escaped` | `Bash(git diff*)` | **ran as one command** — `tool_result` stdout was the diff followed by the literal `escaped` line |
| `git diff \| grep baseline` | `Bash(git diff*)` | **ran as one command** — `tool_result` stdout was the piped `grep` match, not the raw diff |
| `git diff > REDIRECT.marker` | `Bash(git diff*)` **and**, as a control, `Bash(git *)` | **denied, both arms** — *"Output redirection ... was blocked. For security, Claude Code may only write to files in the allowed working directories for this session"* |
| `git diff; echo escaped > ESCAPED.marker` | `Bash(git diff*)` **and**, as a control, `Bash(git *)` | **denied, both arms** — same redirection-blocked message |
| `git diff && touch MUTATED.marker` | `Bash(git diff*)` | **denied** — *"touch in '...' was blocked. For security, Claude Code may only create or modify files in the allowed working directories for this session"* |
| `git diff; touch MUTATED.marker` | `Bash(git diff*)` | **denied**, same message |

Two different mechanisms are visible here, not one. `--allowedTools`/`--disallowedTools` pattern
matching is confirmed to run against the **whole command line as typed**, not just its leading
tokens: `git diff; echo escaped` and `git diff | grep baseline` both executed in full under a grant
naming only `git diff*`, and the appended/piped part's own output is in the `tool_result` — not
merely asserted by the model. That is the escape #1456's second reader asked about, and it is real.
Separately, claude carries an **unconditional Bash-tool guard against file creation and
modification**: `>` redirection was blocked identically whether the grant was the narrow
`Bash(git diff*)` or the wide-open `Bash(git *)` control, and chained `touch` was blocked under the
narrow grant, so that guard sits outside the `--allowedTools` pattern match — a pattern cannot widen
or narrow it, and its presence is not evidence that `--allowedTools` itself bounds chaining. It is also silent on any chained command that
neither writes a local file nor is git/gh — `echo`, a `grep` of secrets already in context, a
network read — which is exactly the shape `git diff; echo escaped` and `git diff | grep baseline`
showed running unblocked.

So the two vendors are **not** "one enforcing, one advisory". They are strong in opposite places:

| | `claude` | `agy` |
|---|---|---|
| per-call grant | **yes**, pattern-matched | not found on `--help` (known incomplete) |
| family ceiling | **yes**, with deny-subsets | no — literal whole-line matching |
| grant lifetime | **the single run** | **persisted to a global settings file** |
| sandbox | **yes — OS-enforced, but not on native Windows** (see [the doc audit](vendor-doc-audit.md)) | **yes, and it enforces** |

**The sandbox row was wrong here until 2026-07-24**, and wrong in a way worth remembering: this
document recorded claude's sandbox as "referenced in help only" because the probe host is **Windows**,
where it genuinely does not run. Claude Code ships an OS-enforced sandbox (Seatbelt / `bubblewrap`)
on macOS, Linux and WSL2, documented across two pages. A single-platform observation was generalised
into a capability claim. **Every row in this document was established on Windows only** — treat any of
them as platform-scoped until re-checked elsewhere.

So the two vendors are closer than the earlier framing suggested, and differ mainly in *expressiveness*:
`claude` has per-run, pattern-matched policy that dies with the run; `agy` has literal-only rules that
persist to a global settings file. Both can contain a process, on the platforms where they run. A
room's ceiling should compile to whichever instrument the chosen worker actually has on the host it is
running on.

One asymmetry worth naming because it cuts the other way: **a `claude` grant dies with the run; an
`agy` grant does not.** Widening `permissions.allow` to complete one task leaves the operator
permanently wider. AER must never do that silently.

## `agy --sandbox` genuinely enforces

The only real enforcement primitive on either CLI. Same command, same allow-rule, sandbox the only
variable:

| | file write outside workspace | network |
|---|---|---|
| no `--sandbox` | `OK` (file created) | `OK status=200` |
| `--sandbox` | **blocked** | **blocked** |

Under `--sandbox` the run demanded a *separate* `unsandboxed(<target>)` grant on top of the already
granted `command(...)` — two independent gates, not one. Internally it is a `sandboxproxy` with a CEL
policy enforcer, blocked-request handling and OAuth2 credential brokering; vendor docs describe
`enableTerminalSandbox` as restricting execution to "OS containment rings".

This matters for decision 0004, though **not in the direction this
document originally claimed.** The earlier reading here — *"a project-level ceiling is enforceable on
`agy` and only advisory on `claude`"* — was drawn from the sandbox alone, before anyone tested
`claude`'s grant patterns. It is wrong as a summary: `claude` expresses and **enforces** a family
ceiling per run (above), which `agy` cannot express at all; `agy` contains a process, which `claude`
cannot do.

The honest statement is that each vendor enforces a *different kind* of ceiling, and neither
subsumes the other. Say which instrument is actually in play when a worker is chosen, rather than
ranking the two or implying a guarantee we cannot keep. Tracked in #515.

## Sharp edges

**`agy -p` ignores the working directory.** It runs the agent under its own install directory, not the
shell's cwd. Observed twice, including in **the case the adapter will actually hit** — launched from
`aer-flow`, which *is* listed in the settings' `trustedWorkspaces`, the emitted command still carried
`"Cwd":"C:\\Users\\pbree\\.gemini\\antigravity-cli"`. From an untrusted temp directory it used
`…\antigravity-cli\scratch` and, unable to find a file sitting in the launch directory, began a
recursive search of the entire home folder. Workspace trust does not change the behaviour.
**Bind the room's directory explicitly with `--add-dir`** — never rely on cwd. Any adapter that
assumes cwd is silently pointing the worker somewhere else.

**`agy` emits PowerShell on Windows**, not POSIX shell — its `run_command` steps carry PowerShell
command lines. Pre-authorisation rules must match what it actually emits.

**`run_command` backgrounds a long command, and the model then polls `manage_task status` in a tight
loop (#1623).** Measured from a real captured lane (`dispatch-implement-7d25642b`, #1618,
`gemini-3.7-flash`, effort high) by pairing each tool `step_update`'s `ACTIVE` line with its terminal
(`DONE`/`ERROR`) line per `step_index`: **35 `run_command` calls, 406 of 482 tool calls (84%) were
`manage_task` `Action:status` polls.** (The issue's own opening comment reports 70/812/934 — that
count is per `step_update` *line*, and every tool call emits exactly two, one `ACTIVE` and one
terminal; both figures describe the same lane, just at a different unit. The 84%/87% ratio itself is
what the fix responds to, and it is stable either way.) The single worst offender was not a gate
command at all: task-608, 166 of the 406 polls (41%), backgrounded a `git push` that ran slowly
because the repo's own pre-push hook runs `gates-fast` — the model saw `git push`, not a gate command,
which is why the shipped instruction (below) names no specific command rather than only "gates and
tests". The other two poll clusters (152 and 83 polls) backgrounded two separate `pixi run
gates-quiet` calls, measured at 260.9s and 460.7s respectively — multi-minute, but not a single "~8
minutes each" figure. Every `run_command` call actually observed in that lane
passed only a `CommandLine` parameter — no other field was ever used.

**Corrected 2026-09-06 (#2002): a parameter by that name has been seen — the STREAM is the wrong
surface to look for it on.** The paragraph below still describes what the stream can and cannot
answer, and that part stands: a `step_update`'s `tool_info.parameters` carries `CommandLine` alone
(414 of 414 `run_command` calls in `dispatch-implement-12f930d9`, re-counted 2026-09-06). The
**`PreToolUse` hook payload** is a different surface, and **one captured payload** carries three
arguments — `CommandLine`, `Cwd`, and **`WaitMsBeforeAsync`, at 5000**, a value agy supplies rather
than the model choosing it.

Scope and provenance, stated so none of this is overread: **n = 1.** The fixture in
`AgyHookCheckCommandTests.Payload` records it as "the real payload agy sends, from the live capture
in `agy.hook-env-inherited`'s log"; that check *reports* payload shape rather than asserting it, so
this is a second-hand reading of one real capture, not a gated measurement. Two things remain
**unmeasured**: whether agy sends these same three on every `run_command`, and whether
`WaitMsBeforeAsync` is in fact the wait-then-background switch its name suggests — the name and the
value are what was observed, the mechanism is inference. What this does settle is the earlier
sentence's premise: a blocking/wait-shaped field is not absent from the tool, it is absent from the
stream.

The consequence for #2002 is in `spec/baton.md` §9: a gate cannot refuse a parameter agy supplies
itself, so rule 1 is scoped on this vendor rather than claimed complete, and
`AgyHookCheckCommand.MeasuredRunCommandArgs` refuses only an argument beyond these three — a rung
whose own failure mode, against an argument set wider than this single capture, is refusing a
legitimate command.

The rest of this paragraph, unchanged: the `stream-json` `init` event's `tools` array lists tool
*names* only —
`run_command`, `manage_task`, `command_status`, `wait`, `wait_5_seconds`, plus the rest of the
roster — never parameter schemas, so a probe would need a live turn. Two were attempted 2026-09-01
(`agy -p "print your run_command tool's parameter schema, verbatim" --model gemini-3.7-flash-high` and
a plain `agy -p "say hi" --model gemini-3.6-flash-low` as a control), and both hit account-wide quota
exhaustion — `Error: Individual quota reached. Please upgrade your subscription to increase your
limits. Resets in 3h42m36s` — across every model tried, not only the one the lane used. Whether `agy`
honours a workspace rules file (an `.agy/rules` or `AGENTS.md`/`GEMINI.md`-equivalent convention) that
could carry a standing instruction once instead of on every prompt is likewise unmeasured: no such
convention is referenced anywhere in this repo's code or in this document as it stood before #1623.
Until either is measured, `AgyWorkerAdapter.BuildPrompt` carries a prompt-level instruction
(`ForegroundGateInstructionText`) telling the worker to run commands in the foreground and never poll
`manage_task` in a tight loop — deliberately not scoped to "gate/test commands" given the `git push`
finding above — cheapest lever available, effectiveness against a live `gemini-3.7-flash` run
unverified pending the quota reset.

**`agy` has no per-call grant flag.** Every grant is a persisted edit to a global settings file. AER
cannot scope a grant to one run the way `--allowedTools` does for `claude`, so a per-run ceiling has
to come from `--sandbox` or from the MCP consultation path, not from flags.

**`agy -p` has its own print-mode wait timeout, decoupled from anything AER configures unless the
flag is passed.** `agy --help`: `--print-timeout  Timeout for print mode wait (default 5m0s)`. Until
`#588`, `GeminiWorkerAdapter` never passed it, so a long read+reason+write job (measured with a
~39-file corpus audit dispatched via `Baton.Cli run`) exited 0 with no output file and no diagnostic
*from agy*, regardless of AER's own configured `Timeout` being far longer.

The operator was not told nothing — they were told the **wrong thing**. A clean exit 0 with the
contract unsatisfied classifies as a contract failure, so the reported reason named the missing output
(`'plan.md' is missing`) and pointed at the worker's prompt, when the actual cause was a five-minute
wall. That is the real argument for the margin direction below: it converts a *misclassification* into
a correct one (`"Execution timed out."`), which is stronger than "a better message".

**Fixed in `#588`:** the adapter now emits `--print-timeout` derived from the worker binding's own
`Timeout`, set deliberately *past* it so AER's enforcement is the binding constraint. That direction is
the point — whichever limit expires first decides the failure mode, and they are not equally good:
AER's yields `CoreExitReason.TimedOut` and a real diagnostic, agy's yields a clean exit 0 with no
output. agy's limit is left as a backstop that should never fire.

**The accepted duration syntax is a Go duration, and one obvious rendering is rejected.** Measured by
running each against the live CLI: `1200s` ✓, `20m0s` ✓, `20m` ✓, and `00:20:00` ✗ — the last exits **2**
with `invalid value "00:20:00" for flag -print-timeout: time: unknown unit ":" in duration`. That
rejected form is exactly what .NET's `TimeSpan.ToString()` produces, so interpolating a `TimeSpan`
directly breaks every dispatch at argument parsing rather than degrading quietly. The adapter emits
whole seconds.

**`claude` has no `--print-timeout` — measured, not inferred from `--help`.** Using the control-flag
instrument this file records above for `--permission-prompt-tool`, since `--help` is not authoritative
here:

| invocation | exit | output |
|---|---|---|
| `claude --definitely-not-a-real-flag-xyz -p hi` | **1** | `error: unknown option '--definitely-not-a-real-flag-xyz'` |
| `claude --print-timeout 60s -p hi` | **1** | `error: unknown option '--print-timeout'` |

The control is what makes the second row mean something: an accepted flag exits 0 and runs the turn, so
identical rejections prove the flag genuinely does not exist rather than that the probe was blind.
Scoped precisely: this settles that **flag**. Whether `claude` applies an internal print-mode limit
under some other name, or none at all, is still unmeasured — nothing here establishes an absence of
timeouts in general.

## `--remote-control` — not yet characterised

Present in the binary and undocumented publicly. Static reading only: it flips a **persisted** setting
(`_remote_control_enabled`, `_remote_control_hostname`), generates a default hostname, and maintains an
outbound WebChannel connection to a Google-hosted relay (`newWebChannelHandler` /
`…V2`, `startRemoteControlConnection` / `…V2`, `UpdateInstanceMetadata`), with a warning path about
binding to a public IP. Outbound-only, so it would traverse NAT without port forwarding.

**It cannot be enabled non-interactively.** `agy --remote-control -p …` reports *"No valid
authentication found"* and starts a **fresh OAuth login**, requesting scopes (`cloud-platform`,
`cclog`, `experimentsandconfigs`, `aicode`, userinfo) beyond what an ordinary authenticated session
holds — then fails with *"You are not logged into Antigravity"* without writing any state. An ordinary
`agy -p` still authenticates normally afterwards; the attempt does not disturb the existing token. So
remote control sits behind a **separate, interactive consent**, and **AER cannot turn it on for the
operator** — worth knowing before designing any flow that assumes it.

**Enabled by the owner 2026-07-24**, which revealed where the state lands and what identity it uses:

```jsonc
// ~/.gemini/config/config.json  — the shared config, not the CLI's settings.json
"remoteControlEnabled": true,
"remoteControlHostname": "compy-2-plasma-mars"
```

The auto-generated hostname is **speakable, not a UUID or an IP**. That is worth copying: AER's own
pairing identity is a token today, and #326 shows what a machine-shaped identity costs a user when it
goes wrong (a raw `401`). A device you can *name out loud* is easier to recognise, to confirm over the
phone, and to tell apart from another machine on the same account.

Vendor forum discussion as of the probe date describes no *official* mobile
remote control, while several third-party clients exist; one reportedly speaks **Connect RPC to the
Antigravity language server** directly. The public Python SDK (`pip install google-antigravity`)
exposing streamed strongly-typed `ToolCall` events was evaluated as an alternative integration path
and rejected — see the RPC section above and
[`vendor-doc-audit.md`](vendor-doc-audit.md#should-aer-drive-sdks-instead-of-clis-no-and-the-reason-is-contractual):
the SDK requires an API key, which forecloses it for both vendors regardless of what it exposes. No
feasibility spike is pending here; the adapter shape (shell out to the CLI) is decided.

## Keeping this current

Both CLIs self-update, so every row here has a shelf life. The suite splits along cost:

| | what it does | cost | where it runs |
|---|---|---|---|
| `pixi run vendor-probe` | drives the live CLIs, regenerates the findings | **real subscription usage**, a few minutes | a human, on a machine with both vendors authenticated |
| `pixi run vendor-check` | compares installed `--version` against the recorded one | **nothing** — no session, no tokens | the ordinary dev loop, and `pixi run test` |

The free check is the trigger for the paid one. `pixi run vendor-probe` writes
`docs/vendor-probe.lock.json` recording the versions its findings were established against;
`VendorProbeStalenessTests` compares that against what is installed and fails the moment a CLI moves.

**This deliberately does not run in CI**, and not only because the probe spends usage. No runner has
an authenticated `claude` or `agy` on PATH, so a CI job would find both vendors absent and go green
forever — a pass meaning only "the vendors were never here". That green would be worse than no check,
because it looks like coverage. The check therefore *skips* where it cannot know, and says so.

Related: `#472` (the first probe), `#504` (the probe suite), `#445` (the permission-request mechanism),
decision 0004, decision 0015.
