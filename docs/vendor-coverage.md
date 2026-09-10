# Vendor coverage register — what we have read, what we have verified, what we have not

**Purpose: mark every gap explicitly**, so "we didn't check" is never mistaken for "it isn't there."

Companion to [`vendor-doc-audit.md`](vendor-doc-audit.md) (the findings) and
[`vendor-capabilities.md`](vendor-capabilities.md) (the reference), against the audit versions
pinned once in the reference's dated history table (#952).

## How coverage is established

Both vendors publish a machine-readable index, mirrored locally and read from source rather than
page-at-a-time (a summarizing fetch is lossy):

- **`claude`** — `https://code.claude.com/docs/llms.txt`, **172 pages**, each fetchable as raw `.md`.
- **`agy`** — `https://antigravity.google/llms.txt` + `sitemap.xml`, **77 doc pages**, server-rendered
  (`<main>` extraction preserves headings, code, tables).

`pixi run vendor-survey` (see `tools/vendor-survey/`) rebuilds this: **249 pages / 7.0 MB →
1,475 unique constraint sentences**, tagged against AER's open questions with page+line provenance,
plus an audit register giving **every page a disposition** so coverage is checkable rather than
asserted.

| disposition | pages | meaning |
|---|---|---|
| `PENDING-DEPTH` | 119 | constraints cluster here; depth-read as decisions require |
| `SCAN-ONLY` | 123 | touches an open question but thin; constraints harvested |
| `NO-SIGNAL` | 7 | no open-question vocabulary at all |

**The per-page `·` tables below are superseded by the audit register** and are kept only for the pages whose
*contents* are summarized here.

**A doc page changing is a reason to re-verify, not a reason to believe the new page.** Every **V**
below rests on a run, not on a sentence. Those runs are re-runnable: `pixi run vendor-verify` (see
`tools/vendor-verify/`) re-runs them, each with a control arm, asserting on a sentinel file rather
than a model's account of what it did. A `FAIL` means a behaviour a decision rests on has moved.

## Status legend

| mark | meaning |
|---|---|
| **R** | read |
| **V** | verified by a run on this host |
| **·** | **not read — a gap, not an absence** |
| **X** | cannot be established from an agent session here (reason given) |

---

## A. `claude` — documentation coverage

Index: `https://code.claude.com/docs/llms.txt` — **172 pages, all mirrored and swept** (`pixi run vendor-survey`). The tier lists below are a manual triage and do not track the audit register's `PENDING-DEPTH` scoring exactly — **trust the audit register, not the tiers**, when the two disagree.

### Read

| | page | what we took from it |
|---|---|---|
| R | `cli-reference` | full flag/subcommand surface; `--bg`, `--max-budget-usd`, `--json-schema`, remote control |
| R | `sandboxing` | OS-enforced sandbox; **not on native Windows** |
| R | `permissions` | fetched, **59 KB persisted to a file that was never read** — counts as unread below |
| R | `workflows` | `agent()`/`pipeline()`, 16 concurrent / 1000 per run, no mid-run input |
| R | `channels` | events pushed into a live session; **permission relay** |
| R | `agent-teams` | shared task list with **dependencies**, file locking, mailboxes |
| R | `agent-sdk/permissions` | **the six-step evaluation order** |
| R | `agent-sdk/user-input` | `canUseTool`, `AskUserQuestion`, `updatedPermissions` |
| R | `agent-sdk/hooks` | `defer` ends the query; full hook event list; precedence |

### Not read — grouped by how much design rests on them

**Tier 1 — load-bearing, unread:**

`·` `settings` · `permissions` (re-read properly) · `hooks` (full reference — matcher patterns, every
event schema) · `hooks-guide` · `permission-modes` · `auto-mode-config` · `mcp` (the
`requiresUserInteraction` annotation) · `managed-mcp` · `sessions` · `agent-view` · `agents` ·
`sub-agents` · `remote-control` · `headless` · `costs` · `monitoring-usage` · `env-vars` ·
`tools-reference` · `errors` · `model-config` · `context-window` · `checkpointing` ·
`sandbox-environments` · `security` · `server-managed-settings`

**Tier 2 — likely relevant:**

`·` `agent-sdk/overview` · `agent-sdk/sessions` · `agent-sdk/session-storage` ·
`agent-sdk/cost-tracking` · `agent-sdk/structured-outputs` · `agent-sdk/streaming-output` ·
`agent-sdk/streaming-vs-single-mode` · `agent-sdk/custom-tools` · `agent-sdk/subagents` ·
`agent-sdk/todo-tracking` · `agent-sdk/file-checkpointing` · `agent-sdk/observability` ·
`agent-sdk/typescript` · `agent-sdk/python` · `agent-sdk/mcp` · `agent-sdk/agent-loop` ·
`agent-sdk/secure-deployment` · `agent-sdk/tool-search` · `agent-sdk/plugins` ·
`agent-sdk/slash-commands` · `agent-sdk/skills` · `agent-sdk/claude-code-features` ·
`goal` · `routines` · `scheduled-tasks` · `worktrees` · `deep-links` · `artifacts` ·
`channels-reference` · `claude-directory` · `commands` · `interactive-mode` · `memory` ·
`output-styles` · `skills` · `statusline` · `plugins` · `plugins-reference` · `prompt-caching` ·
`fast-mode` · `feature-availability` · `how-claude-code-works` · `glossary` · `data-usage`

**Tier 3 — probably not relevant to AER, listed so the list is complete:**

`·` `accessibility` · `admin-setup` · `advisor` · `amazon-bedrock` · `analytics` · `authentication` ·
`best-practices` · `champion-kit` · `changelog` · `chrome` · `claude-apps-gateway*` (5 pages) ·
`claude-code-on-the-web` · `claude-platform-on-aws` · `claude-security` · `code-review` ·
`common-workflows` · `communications-kit` · `computer-use` · `corporate-launcher` ·
`debug-your-config` · `desktop*` (6 pages) · `devcontainer` · `discover-plugins` ·
`features-overview` · `fullscreen` · `gateways` · `github-actions` · `github-enterprise-server` ·
`gitlab-ci-cd` · `google-vertex-ai` · `jetbrains` · `keybindings` · `large-codebases` ·
`legal-and-compliance` · `llm-gateway*` (4 pages) · `microsoft-foundry` · `mobile` ·
`network-config` · `overview` · `platforms` · `plugin-dependencies` · `plugin-hints` ·
`plugin-marketplaces` · `plugin-relevance` · `prompt-library` · `quickstart` · `security-guidance` ·
`setup` · `slack` · `terminal-config` · `third-party-integrations` · `troubleshoot-install` ·
`troubleshooting` · `ultraplan` · `ultrareview` · `voice-dictation` · `vs-code` · `web-quickstart` ·
`whats-new/*` (18 pages) · `zero-data-retention`

---

## B. `agy` — documentation coverage

Index: `https://antigravity.google/llms.txt` + `sitemap.xml` — **77 doc pages, all mirrored and swept.** The asymmetry warning below still holds in *volume* (7.0 MB claude vs 310 KB agy), but it is no longer an asymmetry of coverage: both corpora are swept identically.

### Read

| | page | what we took from it |
|---|---|---|
| R | `cli/overview` | nav structure |
| R | `cli/reference` | slash commands, keybindings, `settings.json` keys |
| R | `cli/permissions` | `action(target)`, **three lists incl. `ask`**, `Deny > Ask > Allow`, regex claim |
| R | `cli/sandbox` | `enableTerminalSandbox`; **AppContainer on Windows** |
| R | `cli/commands/usage` | `/usage`, `/quota` — TUI only per this page; superseded by a CLI update measured live 2026-08-28 (headless now works, see `vendor-capabilities.md`) — the page itself was not re-read |

### Not read — Tier 1, load-bearing

`R` **`/docs/hooks`** — `agy` documents `PreToolUse` with `allow`/`deny`/`ask`/`force_ask`, five
events, `hooks.json` in `.agents/` or `~/.gemini/config/`. The two vendors offer equivalent gate
mechanisms; **that is not the same as AER's gate working on both**, and reading it as such is how
#710 went unseen. See `vendor-doc-audit.md`, and the binary's own embedded spec extracted beside it.
`R` **`/docs/sdk/overview`** — `pip install google-antigravity`. Per-turn and cumulative token
usage, streamed structured events, Pydantic-typed results, `deny()`/`allow()`/`ask_user()`, headless.
Evaluated and rejected as an integration path — API-key-only, see `vendor-doc-audit.md` § SDK.
`·` `cli/settings` — full settings reference · `cli/modes` — execution modes ·
`cli/subagents` · `cli/projects` · `cli/credits` · `cli/conversations` · `cli/artifacts` ·
`cli/using` · `cli/features` · `docs/permissions` (product-level) · `docs/agent-settings` ·
`docs/mcp` · `docs/subagents` · `docs/sidecars` · `docs/hooks`

### Not read — Tier 2

`·` `cli/install` · `cli/getting-started` · `cli/tutorial` · `cli/prompting` · `cli/plugins` ·
`cli/statusline` · `cli/title` · `cli/gcli-migration` · `cli/best-practices` · `cli/troubleshooting` ·
`cli/commands/{agents,codesearch,credits,diff,permissions,resume,statusline,title}` ·
`docs/{models,projects,settings,skills,rules-workflows,plugins,artifacts,implementation-plan}` ·
`docs/{plans,enterprise,faq}`

### Not read — IDE surface (~18 pages)

`·` `docs/ide/*` — not obviously relevant to a CLI worker, listed for completeness. One exception
worth a look: `ide/allowlist-denylist`, which may document the same permission grammar from the other
side.

---

## C. Claims we hold, and their evidence class

### Verified by a run on this host

| claim | where |
|---|---|
| `--permission-prompt-tool` is accepted **and honoured**; full request/response contract | #509, #512 |
| `--permission-mode auto` **silently bypasses** it | #514 |
| `PreToolUse` hook fires under `auto` **and** `bypassPermissions` | #519 |
| `defer` ends the query (`terminal_reason: tool_deferred`); `--resume` completes the work | #520 |
| **`--bare` disables hooks even when passed via `--settings`** | #521 |
| `--bg` sessions appear in `claude agents --json`; states `working`/`idle`/`blocked`/`stopped` | #516 |
| `claude -p "/usage"` reports percent + reset instants; `total_cost_usd` per turn | #472 |
| `--allowedTools` patterns enforce; `Bash(git *)` minus `Bash(git push*)` works | #515 |
| Both vendors fail closed headless | #472 |
| Blocking MCP tool holds a turn open on both vendors | #472 |
| `agy --sandbox` enforces (file write + network blocked) | #472 |
| `agy -p` ignores cwd | #472 |

### Documented but **not verified** — the verification backlog

Reading generates claims faster than verification consumes them — anything here is a vendor
assertion, not yet run. Verified items move to the "Verified by running it" section of
[`vendor-doc-audit.md`](vendor-doc-audit.md). Nothing is deleted from here without either a run or a
reason it cannot be run. **Struck rows are re-runnable via `pixi run vendor-verify`** — that is what
makes striking one safe across a vendor version bump.

**Two rows turned out to be wrong as written**, not merely unverified. Both are corrected in place
rather than deleted, so the wrong version does not get re-derived by a later reader.

#### A. Shapes a decision currently in flight

| claim | vendor | status |
|---|---|---|
| ~~`--add-dir` grants file access but loads **no** hooks/settings config~~ | claude | ✅ verified |
| ~~`usage.output_tokens` excludes subagent tokens~~ | claude | ✅ verified — 882 vs 1130 |
| ~~a hook's `"ask"` forces a prompt in `auto` mode~~ | claude | ✅ verified |
| ~~explicit `ask` rules force a prompt even in `bypassPermissions`~~ | claude | ✅ verified |
| ~~`requiresUserInteraction` allow→deny under `--permission-prompt-tool`~~ | claude | ✅ verified |
| ~~`PostInvocation.terminationBehavior`~~ | agy | ✅ verified on the redo — 7 invocations vs 1 |
| ~~`PermissionRequest` fires **only** in auto mode~~ — **the row was wrong.** The docs say it fires "when a permission dialog appears"; `PermissionDenied` is the auto-classifier event. **Verified: `PermissionRequest` never fires under `-p`.** | claude | ⚠️ corrected + verified — 0018's notify hook has no event to hang on headless |
| an API key disables Remote Control, `/schedule`, connectors, notifications | claude | **open** — and it will stay open: AER holds no API key by Rule 4, so establishing this would require provisioning the exact credential the design forbids. Moved to F in spirit. |
| does `PermissionDenied` fire under `-p`? | claude | **open** — logged zero, but nothing established a denial ever occurred |

#### B. Fan-out

**#503 items 4–5 rested on these. Two are now measured, and one of the two was false.**

| claim | status |
|---|---|
| ~~`CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS` bounds concurrency~~ | ✅ verified — peak overlap tracked the cap (2, 6) with 8 started in both arms |
| **nested subagents off by default** | ❌ **contradicted** — default permits one level; explicit cap of 1 does not |
| ~~parent mode overrides every subagent~~ | ✅ verified against a `default` arm |
| the documented **default of 20** concurrent | **open** — the cap is verified, the default value is not |

`·` Still documented-only: nested teams impossible · a teammate's background work cannot outlive
the lead · per-teammate modes cannot be set at spawn · workflows `agent()`/`pipeline()`, 16
concurrent / 1,000 per run, no mid-run input · agent-teams task dependencies and file locking ·
`attach` / `respawn` (`logs`, `stop`, `rm` and the state vocabulary are verified) ·
`daemon stop --keep-workers` reconnecting to live workers

#### C. Durability and sessions

| claim | status |
|---|---|
| ~~`CLAUDE_CONFIG_DIR` isolating a supervisor instance~~ | ✅ verified — **and a first, wrong conclusion corrected.** The variable is honoured and a *fresh* root is un-logged-in, but credentials live under the config root, so `claude auth login` makes it usable. **Per-worker config roots are an available option**, priced at one interactive sign-in each, and Rule-4-clean because the human signs in, not AER. |
| ~~`claude auth status` as a readiness probe~~ | ✅ verified — reports per config root, structured, and **spends no subscription usage**; usable before dispatch |
| ~~whether a second concurrent login on one subscription is permitted~~ | ✅ verified — a fresh root's interactive login did not displace the pre-existing root's, both reported the same account, and two concurrent `-p` runs (one per root) both succeeded (`vendor-doc-audit.md` § Worker identity; 0029's Rests-on table). Not yet tested above two concurrent roots. |

| ~~two processes cannot write one transcript~~ — **not what protects it.** `--session-id` is an existence check, not a lock: sequential reuse is refused, but a concurrent pair races past and **both run**. | ⚠️ corrected + verified twice — this was `Baton.Daemon`'s obligation to enforce while it ran interactive sessions; that daemon-hosted session surface is deleted (#1420), and no component in this repo runs a session against this vendor behavior today |

`·` Still documented-only: `--fork-session` starts
without session grants while `/branch` carries them · credential expiry stalls a long-running
background session unrecoverably · `cleanupPeriodDays` retention · `--no-session-persistence` ·
`--session-id`

#### D. `agy`

| claim | status |
|---|---|
| ~~three permission scopes (Project / Shared / Global) and their merge order~~ — **the row was wrong.** The docs describe three access *lists* (`deny`/`ask`/`allow`, precedence Deny > Ask > Allow) in **one** global file. | ⚠️ corrected + verified — **permissions are global-only**; no project-scoped location is honoured, so **hooks are the only gate AER can install per-worker for agy** |

`·` Still documented-only: the four `toolPermission` presets (`request-review`,
`proceed-in-sandbox`, `always-proceed`, `strict`) · "permission rules govern `run_command` across
**all** execution modes" · subagents starting from a clean slate and being unre-awakenable ·
AppContainer sandbox on Windows · the daemon↔credential coupling ·
`/usage` TUI-only per the docs page (superseded by a CLI update, measured 2026-08-28 —
`vendor-capabilities.md`) · implicit read-on-write · Windows path normalisation

#### E. Flags

| claim | status |
|---|---|
| ~~`--max-budget-usd`~~ | ✅ verified **enforcing**, not reporting — `subtype: error_max_budget_usd` |
| ~~`--json-schema`~~ | ✅ verified conforming — and it takes the schema **inline, not a path** |
| ~~`--allowedTools` / `--disallowedTools` as a toolset bound~~ | ❌ **contradicted in effect** — `--allowedTools` is pre-approval only, and `--disallowedTools` removes the tool while the model **substitutes another and still reaches the goal**. Neither is a boundary. |

`·` Still never exercised: `--tools` · `--include-partial-messages` · `--forward-subagent-text` ·
`--replay-user-messages` · `--input-format stream-json` queueing (#462) · `Notification` hook
(`permission_prompt` / `idle_prompt`) · `Elicitation` hooks · 30 s `MCP_TIMEOUT` ·
`updatedPermissions` / `localSettings` persistence · channels permission relay

#### F. Cannot be established from here — stated so they stop looking pending

| claim | why not |
|---|---|
| claude's OS-enforced sandbox | **does not exist on native Windows**; needs macOS or Linux |
| anything cross-platform | every observation in this repo is Windows-only |
| managed / org settings, connector `ask` policy | requires an organisation |
| Remote Control mobile push, Trusted Devices | requires the mobile app and a paired device |
| `defer`'s single-tool-call limit | three attempts could not make the model batch its calls; **untested, not refuted** |
| the MCP idle window's upper bound | 200 s survived; the ceiling is unknown |

### Contradicted or unresolved

| | |
|---|---|
| ~~`agy` `command()`: regex or literal?~~ | **RESOLVED 2026-07-24 — literal.** Re-run on 1.1.7 with the operator's authorisation; both discriminating rules denied, including the docs' own alternation form. **The documentation is wrong** — the only such case in this audit. |
| **Does `defer` replay the identical `tool_use_id`?** | verified the session resumes and work completes; **not** verified the same call is replayed. Decides whether we can promise "the exact call you approved ran". |

### X — cannot be established from an agent session on this host

| | why |
|---|---|
| `claude` sandbox (any of it) | **not supported on native Windows**; this host is Windows 11 |
| ~~`agy` `command()` re-test~~ | **done 2026-07-24** — run on explicit authorisation, byte-exact backup, restore after every case, SHA-256 verified unchanged |
| Channels | research preview; needs a plugin install and org enablement |
| Workflows | plan-gated; needs `/config` opt-in on Pro |
| Agent teams | needs `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1` (env-only, safe — but untested) |
| Live smoke gates | permanently a human action item ([development guide](agents/developing-baton.md#live-vendor-smoke-tests)) |

---

## D. Gaps that are not about the vendors — current state, not a snapshot

1. **Every finding here is Windows-only, and so is AER Flow itself (decision C-10, #1405).** Kept as
   its own item rather than folded away now that the product is Windows-only too: the sandbox
   correction is the proof the distinction still matters even so — a platform-scoped *vendor*
   observation was generalised into a product claim once already, and the lesson (don't generalise a
   finding past what was actually measured) outlives the cross-platform goal that originally
   prompted it.

2. **`src/` audit coverage against corrected vendor reality is partial, not swept.** #521, #529 and
   others were found by looking, each in the first file checked for that specific issue — that is
   not evidence the rest of `ClaudeWorkerAdapter`/`AgyWorkerAdapter` are clean, only that nobody
   has looked comprehensively.

3. **Every decision's disposition against measured vendor reality is tracked, and current** — see
   the decision-audit sweep (retired 2026-08-28 with the decision registers it read — #1397), formerly enforced by
   `pixi run audit-completeness` rather than restated here.

4. **Vendor drift during a run is unhandled.** Both CLIs have shipped a new version mid-session
   before (`agy` 1.1.6→1.1.7, `claude` 2.1.219→2.1.220, same day). What AER does when the binary
   changes under a running room is not designed; `vendor-check` only detects drift between probe
   runs, not mid-run.

5. **`agy`'s documentation corpus is thinner than claude's** (77 pages / 310 KB vs 172 pages / 7 MB).
   Both are now fully swept by `pixi run vendor-survey`, so this is a volume asymmetry in the source
   material, not a coverage gap on AER's side — but a symmetry claim about the two vendors is weaker
   evidence on the `agy` side for exactly this reason.

## E. Recomputing coverage

`pixi run vendor-survey` re-reads both corpora and reports which pages moved since the last run.
`pixi run vendor-verify` re-runs the behaviours decisions actually rest on, each with a control arm.
Both exist so re-establishing coverage is a command, not a fresh manual read — run either on a
vendor version bump, which `pixi run vendor-check` (free) detects.
