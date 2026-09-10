# Runbook: Live Claude smoke run (M11 Phase 4)

M11's completion gate (#87): a real two-step `draft` → `review` workflow, run through `baton run`
against the real headless `claude` CLI, producing real artifacts on disk. This is the first time
aer-flow dispatches to a live LLM instead of `StubCoreDispatcher` or a shell-stub worker — so it
runs from this runbook and a dedicated `pixi run` task, never from default CI (no authenticated
subscription session or network access is available there, and a real call shouldn't gate every PR
anyway).

Before running, follow the
[shared policy for live vendor runs](../agents/developing-baton.md#live-vendor-smoke-tests). This
smoke run's local prerequisites are listed below.

## Prerequisites

- A subscription-authenticated `claude` CLI on `PATH`. `ClaudeWorkerAdapter` has no
  authentication-handling code of its own; it shells out to the authenticated CLI on this machine.
- Outbound network access to Anthropic's API.
- The usual repo prerequisites (`.NET 10` SDK — see the root `README.md`).

## Running it

```bash
pixi run smoke-claude
```

This runs `dotnet test tests/Baton.Cli.SmokeTests`, which is **not** part of `Baton.slnx` — it
never builds or runs as a side effect of `pixi run build`/`test`/`lint`, only from this task.

The test (`LiveClaudeRunSmokeTest`) drives `RunCommand.ExecuteAsync` — the same call `Program.cs`
makes — against the fixtures in `tests/Baton.Cli.SmokeTests/Fixtures/`:

- `draft-review-workflow.json` — two steps, `draft` then `review`, `review` depending on `draft`'s
  output.
- `draft-review-bindings.json` — both steps bound to the `claude` adapter
  (`claude-haiku-4-5-20251001`, chosen for speed/cost — edit this file to point at a different
  model without touching any code).

Each run uses a fresh temporary room directory, so repeated runs never resume a prior one.

## What "green" means

The test passes when:

- `baton run` reaches a `Terminal` workflow status with both steps `Succeeded`.
- Both declared outputs (`draft`, `review`) exist on disk under the run's `artifacts/` directory
  and are non-blank.

The test does not assert on the *content* Claude wrote (spec §4.1's contract is "the file exists",
not "the file says X" — the same rule that keeps `Baton` from ever parsing worker output).

## If it fails

- **Adapter/CLI-invocation problem** (`claude` not found, auth failure, malformed command): the
  workflow settles as `Failed` after `ContractValidator` finds a missing declared output, not a
  crash — check the step's latest execution's directory under `artifacts/` for whatever `claude`
  actually produced (or didn't), and re-run `claude -p "..."` by hand with the same flags
  `ClaudeWorkerAdapter` builds (see its XML doc remarks) to isolate CLI-vs-engine issues.
- **Everything else** (unexpected exception, hang): this is exactly the same `project → resolve →
  dispatch → await` loop `RunCommandEndToEndTests`/`WorkflowEndToEndTests` already exercise
  end-to-end in CI with a stub/shell-stub worker — if those are green but this isn't, the fault is
  almost certainly in `ClaudeWorkerAdapter` or the live `claude` invocation itself, not the engine.

## Recording a green run

M11 is complete once this has been run successfully at least once. Record the date and the
`claude` CLI version used in the PR that lands this runbook (see `docs/milestone-history.md`,
M11) — this file only documents *how* to run it, not a rolling log of every run.

**Recorded green run:** 2026-07-12, `claude` CLI 2.1.207. First attempt caught a real fixture bug
this runbook exists to catch: the `reviewer` binding's original `PermissionScope` (`"Write"` only)
let `claude` write its output but not read the `draft` input, so the step failed after refusing the
unapproved `Read` tool call. Fixed by granting `"Read,Write"` in `draft-review-bindings.json`. With
that fix, both steps ran to completion end to end.

## J6 — deny enforcement (safety gate, #331)

A *completion* run above proves a granted tool works. This gate proves the opposite: a **withheld**
tool is actually blocked, not merely omitted. Decision `0004` requires grants to fail closed;
`ClaudeWorkerAdapter` now pairs `--allowedTools` (auto-approve what's granted) with
`--disallowedTools` (actively deny what's withheld). This gate confirms the CLI honours the deny —
which no unit test can, because the translation was already correct and the outcome still wrong (the
whole point of #331).

Run each probe **several times** — Claude Code's headless sandbox behaviour is non-deterministic
(see #289's ~50% note). Strip **every** env var matching `^CLAUDE` first, not just `CLAUDE_CODE_*`:
they make a nested `claude` a *child session* that inherits the parent's tool set (an early probe was
bypassed by an inherited `Monitor` tool that isn't present in the harness's clean spawn, and a later
one drew a wrong conclusion because the `CLAUDE_CODE_` prefix misses `CLAUDECODE` itself). The harness
(`baton run`/`baton dispatch`, via `WorkerAdapterRegistry`) spawns `claude` as a plain process — since
#1420 the daemon spawns no workers at all — so the clean env is the representative one. Build the list rather
than hand-listing it, and verify it worked by reading `permissionMode` and the inherited `tools`
array out of the `system:init` event:

```sh
STRIP=$(env | grep -o '^CLAUDE[A-Z_]*' | sed 's/^/-u /' | tr '\n' ' ')
env $STRIP claude -p --output-format stream-json --verbose "..."
```

```bash
PROMPT="Run the shell command 'hostname' and report its exact output verbatim. If you are not permitted to run shell commands, reply with exactly NO_SHELL and nothing else."
CLEAN="env $(env | grep -o '^CLAUDE[A-Z_]*' | sed 's/^/-u /' | tr '\n' ' ')"   # every ^CLAUDE var, not a hand-list

# Leak repro — WITHOUT the deny flag, the withheld shell runs (the bug):
$CLEAN claude -p "$PROMPT" --allowedTools Read --output-format text            # -> the real hostname

# Fixed — WITH the deny flag, the withheld shell is refused:
$CLEAN claude -p "$PROMPT" --allowedTools Read --disallowedTools Bash --output-format text  # -> NO_SHELL
```

**Green** = the deny-flag runs consistently return `NO_SHELL` (or an explicit "no shell tool")
while the no-flag runs return the real hostname (confirming the environment actually reproduces the
leak, so the refusal is meaningful).

**Recorded evidence:** 2026-07-23, `claude` CLI 2.1.218 (Windows), clean env. Without the flag: 2/2
returned `Compy-2`. With `--disallowedTools Bash`: 4/4 refused (`NO_SHELL`). Agent-run de-risk
(host-coincidence auth, per the caveat above) — records that the mechanism enforces, does **not**
close the human gate.

**Gemini (`agy`) — checked, and clean for a different reason (#331).** `agy` has no
`--disallowedTools` equivalent, so the concern was whether a shell-*withheld* grant (which maps to a
`--mode`, not a refusal) still lets shell run. It does not: headless `agy` **auto-denies** any tool
needing the `command` permission it cannot prompt for — verified 2026-07-23 across `default` / `plan`
/ `accept-edits` (6/6 refused, *"a tool required the 'command' permission that headless mode cannot
prompt for, so it was auto-denied"*). Its request-side is refused at the adapter
(`AgyWorkerAdapter` throws for requested shell/network — unit-tested).

> **Corrected 2026-07-24 (`#472`).** This section previously said `agy` was fail-closed "the opposite
> of Claude Code's headless auto-*approve*". **That contrast is wrong: both vendors fail closed.**
> The auto-approve reading came from a probe that stripped only `CLAUDE_CODE_*` and so missed
> `CLAUDECODE` itself — the child still ran as a *child session*. Re-run with every `^CLAUDE`
> variable stripped, in a neutral directory, plain `claude -p` **denies** a `Write` it was not
> granted, and reports the denial structurally as
> `permission_denials: [{tool_name, tool_use_id, tool_input}]`.
>
> Also corrected: **`--permission-mode manual` is a no-op headless** — the session still reports
> `permissionMode: default` and no prompt is ever issued. Full matrix in
> [`vendor-capabilities.md`](../vendor-capabilities.md); build the strip list rather than hand-listing
> it (`env | grep -o '^CLAUDE[A-Z_]*'`).

**Recorded green run:** 2026-07-13, `claude` CLI 2.1.207 (Windows). A second, unrelated bug: the
`draft` step (no fixture changes involved) failed with no visible error. A capture-enabled repro of
`ClaudeWorkerAdapter.Resolve`'s exact target found `claude` received either no prompt at all or one
truncated at the first embedded newline. Root cause was in `ClaudeWorkerAdapter` itself, Windows-only
— aer-core's Windows spawn (`Command::args`) applies its own Win32 argument quoting/escaping to every
`CoreDispatchTarget.Args` element; handing it one already hand-quoted `cmd /c "..."` string (as the
Unix branch correctly does, since `execve` never re-quotes) made Rust escape the adapter's own quotes
a second time, corrupting the command. Fixed by passing each token as its own array element on
Windows instead of one pre-built string (Unix is unaffected and unchanged). A related, separately
real bug fixed in the same pass: a multi-line prompt's embedded newlines made `cmd.exe`'s `/c` tail
parser split it into multiple statements, silently dropping `--allowedTools`/`--output-format`/
`--model`; Windows prompts are now flattened to one line for exactly this reason. With both fixes,
`draft` → `review` ran to completion end to end again.

---

## The read-only reviewer gate (#649)

```
pixi run smoke-readonly
```

`LiveReadOnlyReviewerSmokeTest` is the gate; it asserts all three of `Succeeded`, the declared output
present in the outbox, and an empty workspace. Two of three is a different failure each way — no
report is #629's pay-then-fail, a leaked file is the grant not being enforced — which is why the test
asserts them together rather than in sequence.

**Recorded green run:** 2026-07-28, `claude` haiku, four consecutive dispatches. One earlier dispatch
that session exited 0 and produced nothing — the silent class #289 and #599 describe; replaying its
argv by hand succeeded and it did not recur. Re-run rather than reading it as a regression. #532 is
what would make that shape visible instead of silent.
