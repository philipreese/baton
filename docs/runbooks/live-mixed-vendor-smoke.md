# Runbook: Live mixed-vendor paused-run smoke run (M12 Phase 4)

M12's completion gate (#98): a real `draft` (Claude) → `review` (Gemini/`agy`) workflow — §18.1's
composition case, the original goal the project was built for. Run through `baton run` against both
real headless CLIs, pausing at `review`'s declared `PausePoint`, resumed by a real `baton decide` to
terminal success, with real artifacts from both vendors on disk. This is the first time aer-flow
dispatches two different vendors in the same run, and the first time a live smoke test also
exercises the mutation surface (`baton decide`), not just `baton run`.

This mixed gate is agent-runnable only when both subscription CLI logins are already present;
disclose expected cost before starting. Missing authentication returns the task to a human; see the
[shared live-run policy](../agents/developing-baton.md#live-vendor-smoke-tests). Both adapters shell
out to the existing subscription-authenticated CLIs rather than owning key-handling code, and a
missing login must not be worked around by switching to API-key authentication.

## Prerequisites

- An authenticated `claude` CLI on `PATH` — see
  [`live-claude-smoke.md`](./live-claude-smoke.md)'s prerequisites; unchanged here.
- A subscription-authenticated `agy` (antigravity, Google Gemini's CLI) on `PATH`. `AgyWorkerAdapter`
  delegates authentication by launching that already-signed-in executable; it does not accept
  credentials itself.
- Outbound network access to both Anthropic's and Google's APIs.
- Standard repo prerequisites, same as the claude smoke runbook (.NET 10 SDK).

## Running it

```bash
pixi run smoke-mixed-vendor
```

This runs `dotnet test tests/Baton.Cli.SmokeTests` filtered to
`LiveMixedVendorPausedRunSmokeTest` — the same project as `smoke-claude`, still **not** part of
`Baton.slnx`, so it never builds or runs as a side effect of `pixi run build`/`test`/`lint`.

The test drives the CLI commands directly (see `LiveMixedVendorPausedRunSmokeTest`'s own doc comment
for exactly which calls), reading its fixture pair from `tests/Baton.Cli.SmokeTests/Fixtures/`:

- `draft-review-paused-workflow.json` — two steps, `draft` then `review`, `review` depending on
  `draft`'s output and declaring a `PausePoint` with no supersede targets.
- `draft-review-paused-bindings.json` — `draft` bound to the `claude` adapter
  (`claude-haiku-4-5-20251001`), `review` bound to the `gemini` adapter (`gemini-3.6-flash-low`) — edit
  either `Model` to point at a different model without touching any code. Both names are transcribed
  from the fixture as it stands and are **historical**: `gemini-3.6-flash-low` is a retired name that no
  shipped tier pins any more (`src/Baton.Vendors/WorkerTiers.json` is the pin register), so read this
  line as what the fixture contains rather than as a current model choice.

<!-- record-once-ok: #443 docs/runbooks/live-claude-smoke.md -->
Each run uses a fresh temporary room directory, so repeated runs never resume a prior one.

## What "green" means

The test passes when:

- `baton run` reaches a `Paused` workflow status with `draft` `Succeeded` and `review` `Paused`
  (`PausedOutcome: Succeeded`).
- `baton decide --type resume` against `review`'s paused execution reaches a `Terminal` workflow
  status with both steps `Succeeded`.
- Both declared outputs (`draft`, `review`) exist on disk under the run's `artifacts/` directory
  and are non-blank.

The test does not assert on the *content* either vendor wrote (spec §4.1's contract is "the file
exists", not "the file says X" — the same rule `live-claude-smoke.md` documents).

## If it fails

- **`draft` (Claude) never reaches `Succeeded`**: triage exactly as `live-claude-smoke.md`
  describes — this half of the run is unchanged from M11.
- **`review` (Gemini/`agy`) never reaches a paused `Succeeded` outcome**: check the step's latest
  execution's directory under `artifacts/` for whatever `agy` actually produced (or didn't).
  Re-run `agy -p "..." --mode accept-edits --add-dir "<artifacts root>"` by hand with the same
  flags `AgyWorkerAdapter` builds (see its XML doc remarks) to isolate CLI-vs-engine issues. A
  clarifying question with no file written is `agy`'s documented failure mode (spike #21) — it
  exits 0, and `ContractValidator` reads the missing output as retryable, same as any other
  contract failure.
- **`baton run` never pauses** (workflow reaches `Terminal` or fails before pausing): the fixture's
  `PausePoint` declaration is the only thing to check — this mechanism is proven end-to-end against
  a stub worker in `PauseDecisionSupersedeHumanEndToEndTests` (M9), so a live-only failure here
  points at the fixture, not the engine.
- **`baton decide` fails to resolve the pause**: engine-side decision semantics are proven at the
  `MutationInterface` layer (M9) and CLI wiring at `DecideCommandEndToEndTests` (M12 Phase 3) — if
  those are green but this isn't, the fault is almost certainly in one of the two live adapters, not
  the decision surface itself.
- **Everything else** (unexpected exception, hang): this is the same `project → resolve → dispatch →
  await` loop `RunCommandEndToEndTests`/`DecideCommandEndToEndTests` already exercise end-to-end in
  CI with shell-stub workers — if those are green but this isn't, the fault is in one of the live
  adapters or CLI invocations, not the engine.

## Recording a green run

M12 is complete once this has been run successfully at least once. Record the date and both CLI
versions used in the PR that lands this runbook (see `docs/milestone-history.md`, M12) — this
file only documents *how* to run it, not a rolling log of every run.

**Recorded green run:** 2026-07-13, `claude` CLI 2.1.207 and `agy` CLI 1.1.1 (Windows). Both adapters
needed the same Windows-only fix first (see `live-claude-smoke.md`'s 2026-07-13 entry for the full
root cause): `ClaudeWorkerAdapter`/`GeminiWorkerAdapter` each built one pre-quoted `cmd /c "..."`
string, which aer-core's Windows spawn (`Command::args`) re-quoted and corrupted a second time. Fixed
in both adapters by passing each token as its own `Args` element on Windows instead. With that fix,
`draft` (Claude) → paused `review` (Gemini/`agy`) → `baton decide --type resume` → `Terminal` ran to
completion end to end on the first live attempt.
