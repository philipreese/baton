# `vendor-verify` — re-run the vendor behaviours AER depends on

```
pixi run vendor-verify                        # every check that needs no special authorisation
pixi run vendor-verify -- --list              # names, groups, and what each one claims
pixi run vendor-verify -- --only gate         # one group: gate | fanout | cost | lifecycle | agy | effort | models
pixi run vendor-verify -- --allow-config-writes   # also the checks that touch real settings files
```

## Why this exists

The vendor tooling in this repo has three legs, and they answer different questions:

| tool | question |
|---|---|
| `pixi run vendor-probe` | what can the installed CLIs *do* (capability matrix + version lock) |
| `pixi run vendor-survey` | what do the vendors *say* (doc corpus mirror + constraint harvest) |
| `pixi run vendor-verify` | do the behaviours we *designed against* still hold |

The audit in `docs/vendor-doc-audit.md` found **four vendor statements that were wrong** and two that
contradicted each other. So documentation is a claim, not a fact — and a doc page changing is a
reason to re-run these checks, not a reason to believe the new page.

Before this existed, each of these behaviours was established once by an ad-hoc script in a
temporary directory that got deleted with the session. That is the exact failure mode that made
`vendor-probe` necessary: decision 0015 inverted its whole mechanism on a `--permission-prompt-tool`
row that nobody could re-run.

## The three rules

Each was learned by getting it wrong first, and all three are non-negotiable for anything added here.

**1. One variable per check — always a control arm.**
`gate.requires-user-interaction` uses two MCP tools that are byte-identical except for the
`_meta` annotation. `gate.hook-exit-2-beats-allow` runs the same hook and the same allow rule twice,
changing only the exit code. Without the control, "the tool did not run" is equally consistent with
"the gate held" and "the model never tried" — a negative from an instrument that cannot distinguish
two causes is not evidence.

**2. Prove execution with a side effect, never with the model's prose.**
Every check asserts on a **sentinel file** written by the tool or the hook itself. A model will
report calling a tool it never called. A hook whose *command* fails looks exactly like a hook that
never fired — this audit concluded twice that agy CLI hooks were broken, when the real cause was a
leading backslash in a JSON-escaped path producing exit 127. The vendor's own logs had said so all
along.

**3. Never probe a slash command from Git Bash on Windows.**
MSYS path conversion rewrites a leading `/usage` into `C:/Program Files/Git/usage` **before the CLI
sees it**, and the model answers about that path — which reads exactly like *"the command does not
exist."* Use `cmd`/PowerShell, `MSYS_NO_PATHCONV=1`, or a leading `//`. Treat any "the CLI doesn't
have that" conclusion reached in Git Bash as unproven until re-run somewhere else.

A check that cannot separate its cases must return `INCONCLUSIVE`. That is a real result, and it is
more useful than a confident wrong one.

### The instruments, and what each one can't see

Picking the instrument is most of the work. Three are in use here, in increasing strength:

| instrument | proves | blind to |
|---|---|---|
| **sentinel file** written by the tool | that specific tool ran | *which* agent ran it — a subagent can write the file its child was supposed to write |
| **hook fire count** + a discovery control on `PreToolUse` | the event occurred, and the config was loaded | nothing, *provided* the control arm fires |
| **`SubagentStart`/`SubagentStop` timeline** | how many agents the CLI actually started, and their overlap | nothing the model can fake |

`fanout.nesting-allowed-by-default` went through all three. Prose first (worthless — a model will
describe a nested spawn it never performed), then a sentinel file (ambiguous — the middle subagent
can just write the file itself, byte-identically), and finally counting spawns. Each redo was
prompted by asking what *else* could produce the same observation.

## Reading the output

| status | meaning |
|---|---|
| `PASS` | the behaviour AER depends on still holds |
| `FAIL` | **it changed** — a decision may now rest on something untrue. Exit code 1. |
| `INCONCLUSIVE` | the control arm didn't establish a baseline; the check proved nothing |
| `SKIPPED` | needs `--allow-config-writes` |

**Every check asserts the *measured* behaviour, not the documented one.** Where the two disagree —
`fanout.nesting-allowed-by-default` and `gate.allowedtools-is-preapproval-not-ceiling` both
contradict their docs — `PASS` means "still contradicting, as recorded". Encoding the vendor's
version instead would leave those checks permanently red and make a real change indistinguishable
from the known discrepancy. The check name states what is true, so a `FAIL` always means *something
moved*.

## Cost and safety

Every check starts a real CLI session and spends real subscription usage, so this never runs in CI
— the same permanent-human-action-item rule as `smoke-*` and `vendor-probe`. This is not a rounding
error: one verification session took the operator's plan from **33% used to 78%**.

### The model tier, and why it is not a blanket downgrade

Checks run on **the cheapest model each vendor offers, at the lowest effort** —
`claude --model haiku --effort low`, `agy --model gemini-3.8-flash-low`. Most checks measure a
**mechanism**: does a hook fire, is a flag honoured, is an elicitation routed. The model has no say
in any of that, so paying for a frontier model buys nothing.

**Seven checks are exempt and run on the vendor default**, listed in `NEEDS_CAPABILITY`. The test
for membership is one question:

> Would a less capable model plausibly produce the **opposite** observation, for a reason that has
> nothing to do with the vendor behaviour under test?

For fan-out checks, yes — a weak model may simply do the work itself rather than spawning
subagents, and "no subagents ran" would read as a cap holding. For
`gate.allowedtools-is-preapproval-not-ceiling` (#529), emphatically yes: the finding *is* that the
model reaches for `Bash` when `Write` is withheld. A model that never thinks of `Bash` would make
the restriction look like a boundary and quietly reverse the most consequential result in the audit.

Downgrading those would reintroduce the exact failure this suite exists to avoid — **an instrument
that cannot separate two causes** — wearing a cost optimisation as a disguise.

Two consequences, both deliberate:

- **The tier is printed on every result line** (`[cheap-model]`) and in the run header. A result
  produced on a downgraded model must never be indistinguishable from one produced as originally
  measured.
- **`--full-model` runs everything on the vendor default.** Reach for it when a cheap-model result
  looks wrong and you need to know whether the *model* changed or the *vendor* did.

Injection happens inside `run()`, not at each call site, so a future check cannot forget it — and a
check that passes its own `--model` wins, because that flag is then the variable under test.

Checks are `safe` unless marked otherwise: **no configuration is read, copied, or modified**, and
the operator's `~/.claude` and `~/.gemini` settings are untouched. A check marked `mutates-config`
is skipped unless `--allow-config-writes` is passed; it copies the file byte-exact, adds exactly
one key, restores in a `finally`, and re-verifies the sha256 — printing a loud warning and keeping
the backup if the restore doesn't match.

**One thing `safe` does not mean.** Every `claude -p` invocation writes a session transcript into
`~/.claude/projects/<cwd-slug>/`, exactly as any ordinary CLI run does — and since each arm uses a
fresh temp working directory, a full suite would otherwise leave ~50 orphan project directories
there. The runner therefore records what exists before the run and sweeps only the directories it
created, and only ones slugged under the OS temp root. Nothing pre-existing is touched. An earlier
version of this section claimed nothing was written outside the temp dirs; that was wrong.

`CLAUDE_*` environment variables are stripped before every invocation, so a check probes the vendor
CLI rather than the harness that launched it. A check testing a specific `CLAUDE_CODE_*` knob sets
that one back — it is the variable under test.

## Layout

```
verify.py            the runner; one @check-decorated function per behaviour
servers/
  mcp_gate_server.py    control tool + gated tool, identical but for requiresUserInteraction
  mcp_prompt_tool.py    a --permission-prompt-tool that always answers allow
  mcp_slow_server.py    blocks BATON_BLOCK_SECONDS with no progress notifications
```

Groups: `gate` (what actually stops an action), `fanout` (subagent depth and concurrency), `cost`
(spend and structured output), `durability` (sessions and config roots), `lifecycle` (daemon and
background sessions), `agy`.

## Running it

A full run is long — every arm is a real CLI session. Run it **per group**, in the background, and
**unbuffered**:

```
python -u tools/vendor-verify/verify.py --only gate
```

Without `-u`, Python holds `print` output in an 8 KB block buffer when stdout is redirected, so a
run in progress looks identical to a run producing nothing, and a run killed on a timeout loses
everything it had found.

### Most of these are findings, not tests — `--sentinels` is the set worth re-running

```
python -u tools/vendor-verify/verify.py --sentinels     # after a vendor version bump
python -u tools/vendor-verify/verify.py --list          # every check, tagged SENTINEL or settled
```

A **sentinel** is a check whose result a design has already committed to, so a vendor changing it
would break AER *silently*. There are a handful. Everything else is a **settled finding**: the
conclusion is written into a decision record, and the code that produced it is a receipt rather than
a test. Re-running those spends real subscription usage to re-confirm what is no longer in question.

The distinction exists because this suite is easy to mistake for a test suite and then grow like
one. It is not. A finding that would merely *add* a capability is never a sentinel, because nothing
built on it can rot; the bar is "a design AER has committed to would silently become wrong."

## Adding a check

```python
@check("group.short-name", "group", "the one-sentence claim being tested", sentinel=False)
def _my_check():
    ...
    return PASS, "what was observed"
```

Give it a control arm, assert on a file, and return `INCONCLUSIVE` when the control arm didn't
establish a baseline. Then record the result in the "Verified by running it" section of
`docs/vendor-doc-audit.md` and strike the row from the backlog in `docs/vendor-coverage.md`.

**The description is a claim, and the passing condition must test all of it.** This is the rule that
went wrong most often here, four times in checks written or reviewed on the same day — a check
described as *"blocks the call and surfaces its reason"* that only ever gated on the block, so it
went green while certifying something measured false. A check that passes on less than it claims is
worse than no check: it converts an open question into a confident wrong answer. If a second
property is interesting but unproven, print it in the detail string and say it is not claimed —
never put it in the description.

## What is *not* here

Group F of the backlog in `docs/vendor-coverage.md` — claims that cannot be established from this
machine at all (claude's OS-enforced sandbox doesn't exist on native Windows; org/managed settings
need an organisation; Remote Control push needs a paired device). Those are listed as unestablishable
rather than pending, so they stop looking like work.
