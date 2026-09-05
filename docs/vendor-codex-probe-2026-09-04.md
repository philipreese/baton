# Codex CLI probe — 2026-09-04

This is the dated evidence record for the first Baton Codex adapter. It records observations made on
one Windows Codex desktop host, against Codex CLI `0.153.2` and desktop app `26.901.4073`. It does not
claim that host policy, the model catalog, or undocumented wire details are stable across releases.
The final reusable-probe refresh confirmed ChatGPT authentication, spent two deliberately tiny
Luna/low subscription turns (initial and resume), and queried model/rate-limit app-server methods. API
credentials were scrubbed before launch and no API-key-billed request was permitted.

A later Baton role-mediation acceptance used Luna/low again. It is recorded separately below because
it exercised Baton's app-server broker and grant-generated dynamic tools rather than bare `codex exec`.

## Evidence boundary

The evidence has three distinct sources:

- **Observed** means a live subscription-authenticated CLI or app-server probe on this host.
- **Documented** means the first-party [non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode)
  or [app-server](https://learn.chatgpt.com/docs/app-server) documentation.
- **Schema** means the JSON Schema generated locally by `codex app-server generate-json-schema` from
  CLI `0.153.2`.

The JSONL files under `tests/Baton.Vendors.Tests/Fixtures/codex/` are sanitized, minimal test vectors.
They preserve the observed values and relevant wire shapes but are not represented as byte-for-byte
transcripts. Prompts, filesystem paths, installation identifiers, account identifiers, and credentials
are absent.

## Authentication and executable

`codex doctor --json` reported ChatGPT-token authentication and no API key. The authenticated desktop
subscription was therefore the authority used by `codex exec`; Baton does not need, receive, or store a
vendor credential. `--ignore-user-config` skipped user configuration during the probes without removing
that authentication.

Structured-grant dispatches use a persistent, otherwise-empty Codex home under
`~/.baton/codex-home`. Baton deliberately does **not** copy the operator's `~/.codex/auth.json` there.
The operator establishes subscription authentication once, using Codex's own login process:

```powershell
$batonRoot = if ([string]::IsNullOrWhiteSpace($env:BATON_HOME)) {
  Join-Path $env:USERPROFILE ".baton"
} else {
  $env:BATON_HOME
}
$env:CODEX_HOME = Join-Path $batonRoot "codex-home"
codex login --device-auth
```

Codex then owns and refreshes the credential in that root. This preserves Architecture Rule 4 while
preventing the broker from inheriting the operator's config, `AGENTS.md`, skills, plugins, or MCP
servers. Raw/manual Codex scopes continue to use `codex exec --ignore-user-config` and the operator's
normal first-party CLI authentication.

This host has two installations. In the operator's PowerShell, `Get-Command codex` resolves the npm
`codex.ps1` shim for CLI `0.153.2`; later on PATH, the desktop app exposes a native `codex.exe` for
CLI `0.153.1`. A bare shell-less `ProcessStartInfo("codex")` selected the older desktop executable,
so copying the interactive shell's version claim into the adapter would have been wrong. Baton now
walks PATH in order and, when it encounters the npm shim, resolves the package's platform-specific
native `codex.exe` under `node_modules/@openai` without executing PowerShell or `cmd.exe`. A direct
native install still resolves normally. Missing or incomplete installations fall back to the literal
program name so the process boundary reports its ordinary program-not-found error.

## `codex exec` command surface

CLI `0.153.2` exposed non-interactive execution in this shape:

```text
codex exec -s <read-only|workspace-write> -m <model> -C <directory> \
  --json --ignore-user-config --skip-git-repo-check <prompt>

codex exec resume --json --skip-git-repo-check <session-id> <prompt>
```

Configuration overrides can select reasoning effort and approval policy, for example
`-c model_reasoning_effort="low"` and `-c approval_policy="never"`. The probe also established that
feature switches are available through `--disable <feature>`; the stable feature list included shell,
multi-agent, browser-use, and app capabilities. This record does not assert that disabling one feature
is a complete permission boundary for every future CLI version.

## JSONL event grammar

With `--json`, stdout is one JSON object per line. The documented event families relevant to Baton are:

| Event | Meaning for an adapter |
|---|---|
| `thread.started` | Start of the persistent Codex thread; its `thread_id` is the resumable session id. |
| `turn.started` | Start of one model turn. |
| `item.started`, `item.updated`, `item.completed` | Progress for an item. Known item types include `agent_message`, `reasoning`, `command_execution`, `file_change`, `mcp_tool_call`, `web_search`, and plan updates. |
| `turn.completed` | Successful terminal event for the turn; carries usage when reported. |
| `turn.failed` | Failed terminal event for the turn. |
| `error` | Top-level error event. |

Observed successful streams had this ordering:

```text
thread.started
turn.started
item.completed (agent_message)
turn.completed (usage)
```

The final user-facing response is the completed `agent_message`, not the last physical JSONL line.
Consequently, a consumer must retain recognized message items while continuing through the terminal
event; reading only the final nonblank line loses the response.

Command execution can add `item.started` and `item.completed` records before the agent message. A failed
tool item does not necessarily fail the model turn: one observed command denial was followed by an
agent message, `turn.completed`, and process exit code `0`. The structured stream is therefore the
authoritative outcome; process exit status alone is insufficient.

## Session and resume semantics

The session id came from `thread.started.thread_id`. Resuming with that id emitted the same thread id.
The resume turn then reported its own usage rather than a cumulative thread total:

| Probe | Input | Cached input | Output | Reasoning output |
|---|---:|---:|---:|---:|
| Initial small turn | 14,750 | 8,960 | 11 | 0 |
| Read-only command-attempt turn | 46,632 | 40,192 | 210 | 48 |
| Resume of the same thread | 19,579 | 11,008 | 9 | 0 |

The resumed turn's counters being lower than the preceding turn's counters establishes per-turn, not
cumulative, reporting for this stream. Baton can append one usage observation per terminal turn. To
avoid counting cached input twice, its accounting projection should separate uncached input as
`max(input_tokens - cached_input_tokens, 0)` and cache reads as `cached_input_tokens`; this is Baton's
interpretation of the fields, not a claim about subscription debiting.

The later broker acceptance found an operationally important cache boundary while preserving those
per-turn semantics:

| Brokered Baton turn | Input | Cached input | Uncached input | Output | Dynamic tool steps |
|---|---:|---:|---:|---:|---:|
| Initial read-only role | 23,875 | 23,296 | 579 | 11 | 3 |
| Same thread resumed by a new broker process | 26,379 | 0 | 26,379 | 11 | 1 |

Both turns emitted the same persisted thread id and each emitted exactly one `turn.completed` usage
object. The second value is therefore not the first and second turns added together. It is a current
turn whose context was fully uncached after the app-server process boundary. The requested output was
written before Baton's deliberately low 10,000-token acceptance ceiling arrested the resumed
execution. This distinguishes a real expensive replay from accounting double-counting and means a
continuation budget cannot assume the preceding process's prompt cache will survive.

## Model and effort discovery

The app-server protocol is newline-delimited JSON-RPC without a `jsonrpc` member. A client first sends
`initialize`, then an `initialized` notification, and can request `model/list`. The generated schema
defines each model's id, visibility, default status, default reasoning effort, supported reasoning
efforts, and optional multi-agent runtime. The model list should be discovered rather than frozen into
the adapter because it is an account- and release-sensitive catalog.

The observed visible catalog on 2026-09-04 included:

| Model | Supported effort | Default effort | Multi-agent runtime |
|---|---|---|---|
| `gpt-6-astra` | low, medium, high, xhigh, max, ultra | medium | v2 |
| `gpt-5.6-sol` | low, medium, high, xhigh, max, ultra | low | v2 |
| `gpt-5.6-terra` | low, medium, high, xhigh, max, ultra | medium | v2 |
| `gpt-5.6-luna` | low, medium, high, xhigh, max | medium | v1 |
| `gpt-5.5` | low, medium, high, xhigh | not retained in the probe notes | not retained in the probe notes |
| `gpt-5.4` | low, medium, high, xhigh | not retained in the probe notes | not retained in the probe notes |
| `gpt-5.4-mini` | low, medium, high, xhigh | not retained in the probe notes | not retained in the probe notes |
| `gpt-5.3-codex-spark` | low, medium, high, xhigh | not retained in the probe notes | not retained in the probe notes |

`gpt-6-astra` was marked as the catalog default. The response the table above was transcribed from
also contained hidden entries, while the retained recording below was taken with `includeHidden:false`
and carries visible models only; the probe notes do not record whether those were one call or two, so
this document does not claim either. Visibility must be honored rather than inferred from a model-name
allowlist.

**Superseded in part by the retained recording (#1875).** The table above was transcribed from probe
notes, not from a kept response — which is why four of its rows say "not retained in the probe notes".
A raw `model/list` answer from the same day and CLI version is now kept, shipped, and used for
validation; [`vendor-capabilities.md`](vendor-capabilities.md) records where it lives and what it is
for. Where the two disagree, the recording is the record: it carries seven visible models and **no
`gpt-5.4`**, whose row above is therefore a transcription artifact rather than evidence. Every effort
set the two do share is identical, `gpt-6-astra`'s `ultra` included.

## Sandbox and approval behavior

The CLI documents `read-only` and `workspace-write` sandbox modes. Its configuration surface also
includes `sandbox_workspace_write.network_access` and writable-root settings.

The observed host behavior was narrower than the requested CLI mode:

- In a synthetic temporary directory, read-only turns that attempted shell commands were rejected by
  host policy.
- Repeating the synthetic write probe with `-s workspace-write` and
  `-c approval_policy="never"` still reported a read-only sandbox and rejected the write.
- Despite that failed tool item, the stream ended with an agent message and `turn.completed`, and the
  process exited `0`.

This is evidence of a **managed-host override on this desktop host**, not evidence that
`workspace-write` is universally broken. An adapter must not promise write capability solely because it
requested the flag. Its smoke test must verify an actual file mutation in the intended writable root,
and a denial must remain visible in the structured result.

## Baton role mediation through app-server

The structured role path no longer relies on Codex's native filesystem, shell, network, browser, app,
or multi-agent tools. Baton starts app-server in the isolated Codex home and declares only dynamic
tools derived from the role's `PermissionGrant`: bounded text reads/search/listing, declared-output
writes, permitted workspace writes, and commands accepted by Baton's canonical command matcher and
standing option-token deny. Paths are rooted and traversal/reparse escapes are rejected.

App-server's Code Mode host must remain enabled on CLI 0.153.x: a live control with it disabled reached
the model but logged `code-mode host is disabled` twice and produced no declared output. With Code Mode
enabled, its nested tool inventory contained only Baton's grant-generated dynamic tools. Native shell,
unified execution, apps, browser/computer use, image generation, and both multi-agent feature families
remained disabled. On the successful read-only run Codex invoked only `baton_list_files`,
`baton_read_text`, and `baton_write_output`; no workspace-write tool was declared or called. It wrote
the exact `findings.md` contract and the turn settled successfully.

The first protocol attempt also exposed a transport defect before any model turn: a UTF-8 byte-order
mark on app-server stdin caused `expected value at line 1 column 1`. The broker now uses UTF-8 without
a BOM, pinned by a protocol test. A sanitized success stream and the cache-miss resume stream are
fixtures so usage and tool-step monitoring replay the same event shapes without another subscription
turn.

## Terminal and error handling

For `codex exec`, `turn.completed` is the observed successful terminal event. `turn.failed` and `error`
are documented failure events. A parser should prefer these typed events over prose and should preserve
the last completed agent message separately from terminal status.

The app-server `0.153.2` schema additionally exposes structured `codexErrorInfo` categories including
`usageLimitExceeded`, `rateLimitExceeded`, `unauthorized`, `badRequest`, and `sandboxError`. These are
stronger classification evidence than message matching when app-server notifications are available.
The included error fixture is schema-derived rather than a live quota-wall capture.

## Known unknowns

- No live Codex quota exhaustion was induced, so the exact `codex exec --json` quota-wall payload,
  reset-time availability, retry behavior, and process exit code remain unmeasured.
- The reusable probe observed three app-server rate-limit windows with used percentages and reset
  instants. It did not establish human-facing names or how each window maps to ChatGPT product limits,
  so they must not be relabelled or presented as an inferred token allowance. It also did not record
  the response's own **payload shape** — `CodexProbe.CollectRateLimitWindows` walks the result
  recursively and discards the property path, and no response fixture is in the index below. A parser
  written against it today would be a guess, which is why `#1904` ships a **derived** codex usage
  source (`CodexUsageSource`, aggregating Baton's own burn ledger and labelled `source: derived`)
  rather than a reader of this surface. **One authenticated `account/rateLimits/read` capture is what
  unblocks the real thing** — a `CodexRateLimitsSource` through `CodexAppServerBroker`, which already
  speaks app-server JSON-RPC and is already an approved spawn site — and retires the derivation.
- App-server dynamic tools have enforced a read-only/outbox-only Baton grant on this host. A live
  workspace-write role, extra writable roots, network access, and subprocess cancellation during an
  active tool call remain unmeasured.
- Raw `codex exec` still cannot exactly express Baton's read-without-command or option-token-anywhere
  ceilings. Structured built-in roles therefore use Baton's app-server broker; raw/manual scopes fail
  closed whenever their grant cannot be translated exactly.
- The effect of every feature disable switch on spawned tools and subagents has not been exhaustively
  measured. Effort labels are therefore validated only as model-supported reasoning settings; delegation
  remains controlled independently by the multi-agent feature switches.
- Model availability, supported efforts, defaults, visibility, and multi-agent versions can change;
  the dated table is evidence for this host and date, not a permanent registry.
- Token fields were observed per turn, but how subscription limits debit cached input, reasoning tokens,
  tool activity, and retries is not exposed by these streams.
- Cancellation, timeout, malformed JSONL, CLI crash, and resume of an expired or missing thread still
  need dedicated probes.

## Sanitized fixture index

- `codex-exec-success.jsonl` — observed successful event ordering and first-turn usage.
- `codex-exec-resume-success.jsonl` — same sanitized thread id with per-turn resume usage.
- `codex-exec-tool-denied-completes.jsonl` — failed command item followed by a successful turn.
- `codex-exec-turn-failed.jsonl` — documented failed terminal event, represented synthetically.
- `codex-exec-error.jsonl` — documented top-level error event, represented synthetically.
- (removed in #1875) `codex-app-server-model-list.jsonl` — a fully sanitized model/effort discovery
  response, replaced by the unsanitized recording described under "Model and effort discovery" above.
- `codex-app-server-errors.jsonl` — schema-derived structured limit notifications.
- `codex-app-server-broker-readonly-success.jsonl` — live-shaped grant-tool success with exact per-turn usage.
- `codex-app-server-broker-resume-cache-miss.jsonl` — same thread resumed across a broker process with zero cached input.
