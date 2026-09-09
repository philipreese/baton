# Runbook — the vendor probe suite

[`docs/vendor-capabilities.md`](../vendor-capabilities.md) is the reference the permission, quota and
worker design all rest on. This is how it stays true.

Introduced in #504, after seven claims about the vendor CLIs turned out wrong in a single sitting —
every one of them the same shape: probe one surface, find nothing, report absent.

## The one rule that explains everything

**A negative claim needs more evidence than a positive one.**

A positive claim carries its own proof: the run happened, the output is quoted, done. A negative
claim — "this vendor cannot do X" — is a statement about *everywhere you didn't look*, and a CLI is
not one surface. There are at least nine:

`--help` · subcommands · **in-session slash commands** · config files · structured output streams ·
environment variables · stderr · local state directories · the vendor's SDK

That is why `Finding.Absent` **throws** if you hand it an empty surface list. Requiring the list does
not make a probe thorough; it makes an incomplete one *visibly* incomplete, which is the part that
kept failing. The generated matrix has no way to render a bare "absent" — every negative reads
"not found on: `--help`, subcommand list, …", and a short list is a visible invitation to look
further.

Two findings in `docs/vendor-capabilities.md` exist only because someone pushed back on a confident
negative: `/usage`, which is absent from `--help` and works perfectly as a slash command, and
`--permission-prompt-tool`, which was undocumented through 2.1.258 and accepted
([later help-surface reading](../vendor-capabilities.md#probe-2026-09-08)).

## The two commands, and why they are separate

| | cost | who runs it |
|---|---|---|
| `pixi run vendor-probe` | **real subscription usage**, a few minutes | you, deliberately |
| `pixi run vendor-check` | **nothing** | every `pixi run test`; also `gates`/`gates-fast` since #1487 |

The probe drives live authenticated CLIs, so it is permanently a human action item (CLAUDE.md) — the
same rule as the `smoke-*` tasks, for the same reason. But asking *"has the CLI moved since we last
looked?"* costs nothing: `--version` is a local string that starts no session.

So the free check is the trigger for the paid one. The probe records the versions it ran against in
`docs/vendor-probe.lock.json`; `VendorProbeStalenessTests` (in `Baton.Architecture.Tests`, which *is*
in the default suite) and the `vendor-check` task compare that against what is installed, through the
same `DriftGrace.Evaluate` call. The day a CLI self-updates, both go **WARN** — loud, but still exit
0 — for `DriftGrace.Window` (seven days as of #1487), then hard-fail past it. See "Turning drift
deliberate" below for the tempo this replaced and how the grace window works; the duration itself is
cited here once and not restated below.

### Why none of this is in CI

Not only because the probe spends usage. **No CI runner has an authenticated `claude` or `agy` on
PATH.** A CI job would find both vendors absent and go green forever — a pass meaning only "the
vendors were never here". That green is worse than no check at all, because it looks like coverage,
and it is precisely the false negative this suite exists to stop us reporting as fact.

So the check runs where the CLIs actually live, and where they actually self-update: the operator's
machine. Where it cannot know, it **skips** — never passes.

## Turning drift deliberate (#1487)

**The incident this fixed.** `claude` auto-updated 2.1.247→2.1.251 on 2026-08-30, and
`VendorProbeStalenessTests` correctly hard-failed every `pixi run gates` on this machine until the
operator ran `vendor-probe` and re-pinned — correct pressure (the findings really were unverified),
wrong tempo (it red-washed five PR reports over a weekend for drift nobody had chosen to accept yet).

Two changes:

**1. Auto-update goes off on both CLIs**, so drift only happens when someone updates on purpose. Set
per `docs/vendor-capabilities.md`'s dated entry for this measurement:

- `claude`: `DISABLE_AUTOUPDATER: "1"` under the `env` key of `settings.json` (**not** a top-level
  `autoUpdates` key — that key does not exist in the settings reference at all; see the dated row for
  what was checked). Stops the background check only — `claude update` still works, which is exactly
  what the loop below wants.
- `agy`: `AGY_CLI_DISABLE_AUTO_UPDATE=1` as an environment variable. Undocumented (not in the public
  CLI settings or reference pages); found by inspecting the shipped binary, the same `Surfaces.Binary`
  evidence class the probes use for exactly this reason.

Neither was flipped by this change — see the dated row for why (spend/environment-mutation policy,
CLAUDE.md) — this is the measured setting for the operator's own runbook step.

**2. The staleness tripwire grew a grace window** instead of hard-failing the instant a CLI moves.
`DriftGrace` (`tools/Baton.VendorProbe/DriftGrace.cs`) records the drift instant the first time it is
seen, in a machine-local, gitignored file beside the lock file
(`docs/vendor-probe.drift.local.json` — gitignored because the clock is a property of *this machine's*
installed CLI, not something to commit). WARN-and-pass within the grace window (see above),
hard-fail past it, hard-fail if the bookkeeping file exists but cannot be read or cleared (fail closed
on broken bookkeeping, never on fresh drift), and clear the bookkeeping the moment `vendor-probe`
re-pins and drift is gone (directly on re-pin, and in `DriftGrace.Evaluate` when a recorded instant
predates the lock's newest `RecordedAt` for a vendor that is now `ok` — #2123).

### The deliberate-update loop

With auto-update off, a CLI only moves when you choose to move it:

1. Update the CLI yourself (`claude update`, `agy update`, or your package manager's upgrade command).
2. `pixi run vendor-probe` — re-establishes every finding against the new version and rewrites the
   lock file.
3. PR the refreshed pins (the #1483 shape: `docs/vendor-capabilities.probe.json`,
   `docs/vendor-capabilities.probe.md`, `docs/vendor-probe.lock.json`, and any hand-edited row in
   `docs/vendor-capabilities.md` the new findings warrant).

The grace window exists for the gap between step 1 happening (deliberately, or on a machine where
auto-update could not be turned off) and step 2 landing — time to notice the WARN and re-probe before
it becomes a hard-fail, rather than either extreme this replaced.

## Running the probe

Both vendor CLIs must be on PATH and already logged in. Then:

```sh
pixi run vendor-probe
```

It writes three files:

- `docs/vendor-capabilities.probe.json` — every finding, with its evidence class and surface list
- `docs/vendor-capabilities.probe.md` — the same as a matrix, shaped like the hand-written doc
- `docs/vendor-probe.lock.json` — the versions probed, which is what the free check compares against

Then **read the diff against `docs/vendor-capabilities.md` and update it by hand.** The generated
matrix is deliberately not the published document: the published one carries reasoning, corrections
and design consequences that no probe can produce. The generated one is the evidence you edit from.

Narrow a run with `--vendor claude` when only one CLI moved. The unprobed vendor is carried through
all three files: it keeps its recorded version in the lock, and its rows stay in the matrix marked
**`(carried, not re-probed)`**. So a narrowed run never downgrades the other vendor to "never
probed", and never lets a carried row pass as freshly established.

A vendor that *is* probed is replaced wholesale rather than row-merged. A capability the probe no
longer emits has been deliberately removed; resurrecting it from the previous file would republish a
finding nothing currently establishes, which is worse than a missing row because it looks measured.

## Reading a result

Every cell is one of three things, and the difference is the whole point:

- **observed** — a run demonstrated it. The only class that may be stated as a plain fact.
- *inspected* — read from help text or the binary, never executed. Help text is a claim, not a
  behaviour; `--permission-mode manual` is documented and is a **no-op** headless.
- **not found on: …** — looked for, on these surfaces. Never a bare absence.

## Adding a probe

Add a method to `Probes.cs` returning a `Finding`, and wire it into `RunAll`. Before you write it:

1. **Name the surfaces you will actually consult**, and pass all of them — including ones you check
   and come up empty on. The list is the evidence for a negative.
2. **Distinguish the CLI from the model.** Asking a model a question and getting prose back is not a
   capability. The `/usage` probe requires a *percentage against a window*, because on one vendor the
   model answers conversationally about usage, and that reads exactly like a working feature.
3. **For flags, use the control.** `FlagProbe.Baseline` runs a flag that certainly does not exist, so
   the CLI's own rejection behaviour is established first. Without it, exit 0 could mean "accepted"
   or "unknown flags ignored", and reading it as the former is how `--permission-prompt-tool` was
   recorded as absent.
4. **Don't parse help output a line at a time.** `claude` puts `--effort`'s accepted values on the
   *continuation* line; a single-line regex reported them as undocumented.

## Two environment hazards, both of which produced wrong answers

**Never invoke a vendor CLI through a shell.** `Cli.Invoke` uses `ProcessStartInfo.ArgumentList`, so
nothing between this code and the CLI interprets the arguments. On Windows, probing `claude -p
"/usage"` through Git Bash produced a confident wrong answer: MSYS path conversion rewrote the leading
`/usage` into `C:/Program Files/Git/usage` *before the CLI saw it*, and the model dutifully answered
about that path — which reads exactly like "the command does not exist."

**Strip every `^CLAUDE` variable, not just `^CLAUDE_CODE_`.** A nested `claude` launched from inside a
Claude Code session inherits the parent's tool set and MCP servers, which no harness-spawned worker
ever has (the harness — `baton run`/`baton dispatch` — spawns workers; since #1420 the daemon spawns
nothing). An earlier probe missed `CLAUDECODE`, `CLAUDE_EFFORT`, `CLAUDE_PID` and `CLAUDE_JOB_DIR`
and produced a result we nearly wrote down as fact. `Cli.Invoke` does this for you.

## A third hazard: the tool that isn't installed

While probing `agy`'s binary for RPC service names, two `strings | grep` passes returned nothing.
Both were about to be written down as "the service names aren't in the binary." `strings` **was not
installed on the machine** — the pipeline was reporting an empty result from a command that never
ran.

This is the purest form of the mistake the whole suite is built around: **an empty result from a tool
that isn't there is byte-identical to an empty result from a tool that looked and found nothing.**
`grep`'s exit code is 1 either way through a pipe.

So: when a probe comes up empty, prove the probe *works* before believing it. Run it against
something you know is present and confirm it finds that. The rewrite in Python found 58 paths on the
same binary — which is how we know the zero was real that time, and that the service names live in
the spawned language-server process rather than the CLI.

The same discipline is why `FlagProbe` runs a control flag: an instrument that cannot produce a
positive result has not established a negative one.

## What the suite must never do

**It must not mutate the operator's environment.** No writes to `~/.claude`, `~/.gemini`, or any
settings file; no installing an alternate auth mode or API key to make a probe work. Use
`Cli.InScratch` for a throwaway working directory.

That last one is not fussiness. `agy`'s permission grants are *only* expressible as edits to a global
settings file — so a probe that took the convenient path would permanently widen the operator's real
permissions to answer a question about a flag.

Related: [`vendor-capabilities.md`](../vendor-capabilities.md), `#504`, `#472`,
[0015](../decisions/0015-three-kinds-of-needs-you.md).
