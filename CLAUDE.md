# Baton — Claude Code Instructions

The product is **Baton**; "AER" stays the name of the ecosystem around it, and this repo is Baton's engine layer (#1458 3b: its own identifiers — `Baton.slnx`, `pixi.toml`'s workspace name — moved off the legacy `aer-flow`/`AerFlow` spelling onto `baton`/`Baton` to match). Built in .NET, that layer reads structured workflow definitions, dispatches them to Workers, and bridges outputs back to the engine. `spec/baton.md` is the system's sole behavioral register — read it first; its rulings govern everything below.

**This file is for developing Baton.** If your job is instead to *invoke* Baton — run a lane against
some other repo and collect its output — read `docs/agents/invoking-baton.md` and stop there.

---

## Repo structure

```
baton/
├── src/                        ONE shipped binary, three projects (#1458 3b — down from six):
│   ├── Baton/                  The core execution engine and routing state machine (ex-Baton.Flow)
│   ├── Baton.Vendors/          Vendor adapters (Claude/Gemini) + the built-in template catalog
│   └── Baton.Cli/               Command-line interface (baton run/dispatch/decide/cancel/supply/
│                              resume/status/templates/keep/unkeep), plus two folded-in verbs that
│                              used to be their own shipped binaries:
│                              - `baton mcp` (ex-Baton.Mcp + Baton.Mcp.Host) — the stdio MCP server
│                                workers connect to (fleet_status, baton yield, memory proposals).
│                                PermissionGateTool/PermissionReturnShape (the mid-lane ask
│                                machinery) were deleted earlier (#1417, spec/baton.md §5)
│                              - `baton daemon` (ex-Baton.Daemon) — the background runner NARROWED
│                                to the 7-kept surface (#1420): mutex, settings load, fleet-wide
│                                concurrency-cap apply, and RoomRetentionSweep as a hosted service.
│                                No HTTP listener remains — every REST/WS route, pairing, WebSocket
│                                broadcast, and sidecar supervision are deleted, along with
│                                Baton.RoomSession and Baton.Sidecar in full. The room-watcher
│                                (serving fleet_status/the registry), the snapshot push loop, and
│                                the quota-runway ledger (spec/baton.md §7) are unbuilt new work for
│                                a later PR, not something #1420's narrowing preserved
├── tests/                     Unit/integration tests; live-smoke test projects (Baton.Cli.SmokeTests)
│                              live outside Baton.slnx (default CI skips them) — see docs/runbooks/
├── spec/
│   └── baton.md                the sole register (§11) — system identity, dispatch contract,
│                              gates, bindings/permissions, what's out of scope. Read it first;
│                              its rulings govern judgment calls everywhere else in this tree
├── docs/                      vendor-capabilities.md / vendor-doc-audit.md / vendor-coverage.md
│                              (the vendor registers), runbooks/, agents/ (harness-facing docs),
│                              dispatch.md. No decision or design register — spec/baton.md §11
├── tools/                     vendor-verify (re-runnable vendor checks; `--sentinels` runs only the
│                              ones a design rests on), vendor-survey, Baton.VendorProbe,
│                              smoke-preflight (free gate on the smoke tasks), audit-completeness
│                              (standing check, gate `record-once` below).
│                              `ls tools/` is the authority — this line is a map, not a register
├── .github/workflows/
│   ├── ci.yml                 lint + fmt + test on win (Windows-only, #1405)
│   └── release-please.yml     versioning and changelog
└── pixi.toml                  task runner and toolchain manager
```

---

## Running tasks

Always use `pixi run <task>`. Never invoke `dotnet` directly in CI or development.

On a fresh clone, run **`pixi run setup-hooks`** — which points `core.hooksPath` at the committed
`.githooks/`, so `pixi run gates-fast` runs on every push. The hook is in the repo but git does not
use it until that command has been run once per clone.

| Task | Command |
|---|---|
| `build` | `dotnet build` |
| `test` | `dotnet test` |
| `lint` | `dotnet build -warnaserror` |
| `fmt` | `dotnet format` (fix) |
| `fmt-check` | `dotnet format --verify-no-changes` (CI) |
| `gates` | every local gate under **one exit code** (#685) — membership lives in `tools/gates/gates.py`, never restated here. Run this rather than the members individually: reading per-member statuses is what has twice reported green while a checker exited 1 |
| `gates-fast` | the same minus `test`. What the pre-push hook runs when no receipt covers the tree |
| `gates-fast-cover` | the same, minus every member this tree already holds a per-member receipt for (#1910). To get those receipts, run the member itself through `python tools/gates/gates.py --record-member <member>` — pass the name only; it runs that member's own gate task for you and receipts what it watched, so a `dotnet build` you ran by hand is not a `lint` receipt. This pass then completes the fast set and the push skips. Never mints a whole-run receipt |
| `setup-hooks` | one-time per clone: `git config core.hooksPath .githooks` |

**.NET 10 SDK** is required and installed separately — pixi does not manage it:
- Windows: `winget install Microsoft.DotNet.SDK.10`
- Linux (Claude Code remote sandbox): `sudo apt-get install -y dotnet-sdk-10.0` directly, skipping `apt-get update` (or ignoring its exit code) — the sandbox's `deadsnakes`/`ondrej/php` PPAs are broken (403/unsigned) and make `apt-get update` fail, but that's unrelated to .NET: the `dotnet-sdk-10.0` package already resolves fine from `archive.ubuntu.com`/`security.ubuntu.com`, so `apt-get install` succeeds without a clean `update`. Installs straight to `/usr/bin/dotnet` — no `PATH` edit needed.

No other toolchain is required: #1474 ported the process-execution engine (previously `native/core`, a
Rust crate reached over an FFI boundary) into plain C# inside `src/Baton` (`Baton.Core` namespace,
`src/Baton/Core/`) — no Rust toolchain, no native library to build. The archived `aer-works/aer-core`
repo is that history, not a current dependency (Architecture Rule 3 below covers what P/Invoke
surface remains).

---

## Live-vendor smoke tests

Some milestones' completion gates are real, live runs against a vendor CLI (`pixi run
smoke-claude`, `pixi run smoke-mixed-vendor`, …) — see `docs/runbooks/`. These live outside
`Baton.slnx` and default CI on purpose.

**Owner ruling, 2026-08-28.** Live-vendor gates may be run by an agent session when the vendor CLIs
are **already authenticated on the machine** — disclose the expected spend before running one (see
"Cost and reversibility are the operator's call" below). Auth provisioning itself stays a human
action item: there is no headless or non-interactive way to sign a vendor CLI in from inside an
agent session, and there should not be one — dropping in an API key to make a gate pass would test
a different auth path than the one the project exists to support (Adapter Isolation:
`ClaudeWorkerAdapter`/`AgyWorkerAdapter` deliberately own no key-handling code and shell out to
whatever vendor CLI is already authenticated on the host, because the project's whole point is
working against **subscriptions**, not API keys).

If a session's host happens to already carry a subscription login for one vendor (e.g. a Claude
Code session's own `claude` CLI), that is a coincidence of the host, not a capability — it does not
extend to any other vendor's CLI, and a future session should not assume it will recur or try to
work around its absence (installing a different auth mode, requesting API keys, stubbing the
adapter, etc.). When implementing a phase gated by one of these tests: build the test, fixtures,
`pixi run` task, and runbook exactly like the pattern in `docs/runbooks/`, run everything that
*can* run locally (`build`, `test`, `lint`, `fmt-check`), and either run the live smoke task itself
(when its vendor is authenticated here, spend disclosed first) or leave it un-run and say so plainly
in the PR body and the phase's tracking issue — don't mark a live-run item done on anything short of
an actual recorded run.

---

## Before you ship — the gates every change runs through

Each was paid for by a specific failure, named so it stays concrete instead of becoming a recitation.
Roughly ordered by when they first bite, but several bite continuously — treat the order as a reading
aid, not a schedule.

**Cite a gate by its slug, never its number.** Every heading carries one (`common-sense`,
`right-instrument`, …). Numbers are positional: merging two gates once already invalidated every
citation elsewhere in the repo, and the slug is what survives the next restructure.

**1. Common sense first — `common-sense`.** Ask the obvious question before building anything. Does the thing you are
about to verify or depend on actually exist? Does a helper for this already exist? Is the failure you
are theorising the one that was actually measured?
*#534's fix was one condition away from a parser already in the file — the shape was there, and
finding that first is what the gate buys, whether or not you end up sharing the code. #532 was scoped
to self-check a `PreToolUse` hook AER does not ship; the issue is real, its stated mechanism was not.*

**Before claiming any vendor fact is unmeasured or undocumented, run both of these.** They are the
two registers, and knowing one exists is not knowing the other does — every rediscovery so far has
been someone checking one and concluding from silence:
- `python tools/vendor-verify/verify.py --list` — every check, its claim, and whether it is a
  sentinel. This is what "we measured that" looks like.
- `docs/vendor-doc-audit.md` — doc-vs-reality findings, including behaviour verified by running it
  that no check guards. This is what "we found that out" looks like.
*Both were skipped in one afternoon on #554. Its body asserted agy's hooks had "no measured sentinel"
when `--list` shows two, and an undocumented `modelName` field was announced as a new finding when
`vendor-doc-audit.md` already recorded it twice — more thoroughly, across three hook events rather
than the one measured. An issue body is a claim about the registers as they were the day it was
written, never evidence about them today.*

**2. The instrument has to fit the claim — `right-instrument`.** Before asking whether a test passes,
ask whether a test is what answers this at all. A change to what a *person sees* is not verified by
tests, however green; a claim about a *vendor* is not verified by that vendor's documentation; a claim
about *durability* is not verified by a happy-path unit test. Name the kind of claim, then pick the
instrument that can falsify it — and if the honest instrument is unavailable, say what was not
verified rather than substituting a cheaper one and reporting it as coverage.
*Three times all-green meant broken: 195 passing UI tests and a clean build with the feature invisible
on the primary path; then a milestone green **including a live vendor smoke test** whose chat was
fundamentally unusable when a person actually drove it. Both suites were written by the same reasoning
that produced the gap, and both asked "does this do what I designed" rather than "did I design the
right thing". The product-journey harness that once caught this (`spec/journeys.md`,
`Baton.Journeys.Tests`) was deleted in the spec v2.0 reset along with the interactive product it tested
(#1397) — harness-facing journeys are future work that will bring its own checks (`spec/baton.md`
§10); until it lands, this gate has no structural instrument and rests on judgment alone.*

**3. V&V that actually verifies — `v-and-v`.** Red before green, *proven* — never a test written against
already-fixed code. A **control arm that discriminates**, read first: if the control fails, the result
is about the harness, not the product. Assert **polarity in both directions** when two behaviours are
one condition apart. A test double that can fail the same way as the thing under test cannot
discriminate.
*All four happened during #527 and the fixes after it — including a green check certifying that `agy`
surfaces a deny reason it does not.*

**4. Blast radius — `blast-radius`.** Trace every consumer of what you are changing *before* editing. A second defect
found on the way becomes its own issue with its own measurement — never a *silent* side effect of the
current fix. Since 2026-07-28 that issue is normally fixed in the same PR rather than deferred (see
"found-while-fixing" under Git conventions); what this gate requires is the issue and the measurement,
not the wait.
*`establishedThisTurn` read like a local variable and decided whether every future chat turn resumes.*

**5. The scope of the claim — `claim-scope`.** A claim about a *population* — both vendors, every platform, all
workers — is measured across that population or scoped to what was measured. Not the same as blast
radius: that asks what your change touches, this asks what your **claim** covers.
*A `claude`-only measurement justified an `agy` sentence: `agy.broken-hook-fails-open` claimed the
failure was **silent** when no positive control for silence exists on that vendor. A Windows-only
sandbox observation became a product-wide capability claim.*

**6. One register — record once, reference everywhere — `record-once`.** A fact is stated once, in one
canonical record; every other location links to it with at most a one-clause gloss, never a
restatement — restating a fact in three places is how a stale one drifts silently in two of them.
Anything discovered that outlives the change gets a durable home *before* the change ships — an
issue, `vendor-doc-audit.md`, or a new, freshly-written decision record (`spec/baton.md` §11: never
retroactive, never reaching back into the register the reset deleted). A comment saying "tracked
separately" with no issue behind it is not a record. Never transcribe a value that lives somewhere
authoritative — cite the command that computes it. A comment that describes code is a claim about
that code: when the code changes, the comment is part of the change.

Before editing anything spec-shaped, check `spec/baton.md` first: it is the sole register of what's
settled, not whichever artifact happened to prompt the edit. Before changing a decision, check it
against every other decision touching the same object, not only the ones it already cites. Before
citing an open issue as evidence that something is still unresolved, check its actual state — a
closed issue cited as "not yet landed" is stale the moment it closes. And before closing a PR
touching `spec/baton.md`, `docs/vendor-*.md`, or `tools/vendor-verify/verify.py`, run
`pixi run audit-completeness` **and read its exit code, not its output** — a pass that fixes drift
while leaving a format violation behind has only relocated the problem.
*This gate was itself two gates saying one thing — "record once" and "docs and decisions are one
register" — which is the drift it exists to stop, in the file that defines it. `gemini-3-flash` sat
wrong in four files while pinning nothing; `audit-completeness.md` carried three different check
counts in one afternoon because the number was copied into a file whose own script computes it; a
test's doc comment claimed the opposite of its code. M29's criterion was "corrected" to match
`02-screens.md` without checking `journeys.md` first, directly contradicting J17. Phone-authoring
timing was independently restated in three documents, one of which went stale while the other two
didn't. Fourteen decisions shipped with no `Rests on` table (#589) — and the first count of it,
written into this very gate, undercounted at thirteen. Then `audit-completeness` was reported as
passing 16/16 while it was exiting 1, because its output was filtered for `OK`/`FAIL` and its failure
prefix is `!!`; the false claim shipped in a merged PR body. The check was correct, pointed at the
right thing, and read instead of run.*

**7. A second reader before a PR is called ready — `second-reader`.** A PR touching `src/`, or making a claim in
`docs/`, is not ready on the author's own say-so. Run it past a **reviewer agent** — one that did not
write the change — and act on what it finds before declaring the work done. Report what the review
said, including "nothing", rather than silently absorbing it. A typo, a version bump, or a comment fix
that changes no claim about behaviour does not need one; if you are unsure, it does.
*Every recurring failure above was caught by a second reader noticing, never by the author
re-reading their own work. An author checking their own claim is the same instrument twice.*

**Exception under `baton dispatch`: skip the in-lane reviewer agent above.** A dispatched conductor
already runs its own separate `review` lane per PR, so an `implement`/`review` worker spawning a second
one is redundant spend, not extra rigor — enforced at the tool level (`WorkerRole.AllowsSubagents`,
spec/baton.md §3, #1802).

Not `/code-review`, which is **operator-triggered and billed** and cannot be launched from an agent
session; a reviewer agent spends this session's own usage, and running one is the author's job rather
than the operator's to ask for. It is also the deliberate exception to "Delegating to subagents"
below: that rule is about saving *effort*, and review buys a second *instrument* instead. Hand the
reviewer the specific claims to check, not a request for an opinion.

**Name the model; don't inherit it.** A reviewer left unspecified runs on the parent session's model,
which silently makes the most expensive option the default for every pass regardless of what the pass
is doing. Pick the tier with the membership test `tools/vendor-verify/README.md` §"The model tier, and
why it is not a blanket downgrade" already applies to its own checks: **would a weaker model plausibly
reach the opposite conclusion, for a reason that has nothing to do with the thing under review?** If
yes — the pass has to notice something that is not on the list it was handed — it needs the strong
model, which is where the defects have actually turned up: claims broader than their measurement, and
a fix that introduced the very defect it illustrated. If no, the list fully determines the work and a
cheap model runs it; `verify.py`'s `CHEAP` is what "cheap" resolves to. A pass that splits gets the
strong model, or gets split into two. Below this gate's own floor — a typo, a version bump, a comment
fix asserting nothing — skip the reviewer rather than run a cheap one out of habit.

Note the test keys on the *pass*, not on the change: "I handed it a specific list" cannot be the
trigger for the cheap tier, because handing over a specific list is already mandatory above.

Say what a pass will spend before spending it, the same way the cost-and-reversibility policy below requires for a live run: it is the
operator's budget either way. `tools/baton-agy-loop/dispatch.py` used to announce the tier before it
dispatched, so that path said it without being asked; #1759 retired it, and no launch path says it
automatically now, so naming the tier is on whoever launches the reviewer, every time.
*Four reviewer passes in one session all inherited the parent's model because none was ever named, so
the frontier rate was paid for the grep half too (#548).*

**The question underneath all seven: name the user-visible behaviour this change improves.** If you
cannot, it may be ceremony — and rigour that is not buying correctness is what this project keeps
having to cut back out. `tools/audit-completeness` is a standing check for exactly that reason —
extend its population when `tools/vendor-verify/verify.py` or the tracked-markdown allowlist
(`tools/audit-completeness/docs-allowlist.txt`) grows, never for open-ended rigour with no named
failure behind it. These other gates stay deliberately without a checker of their own; this one
earned one because its population (vendor-verify checks, tracked markdown) is enumerable and its
omissions are otherwise invisible.

**A broken check is fixed in the change that found it, not filed** — until it is, every later change
ships past it. The single exception to "a second defect becomes its own issue", which is about
*product* defects. Enforced where it can be: `pixi run audit-controls` fails a checker with no
discriminating control, and `recordonce.py` pins the exact passages it must still find in a real
merge (`PROVEN_GROUPS`) — fixtures alone have twice certified a checker that was useless on real
data.

---

## Cost and reversibility are the operator's call

Not a gate — a rule about who decides, which is why it sits outside the shipping checklist rather
than inside it.

Say what a live run spends and what an
irreversible step could break, then let them decide. Before calling something a human action item,
separate *"only a person can do this"* from *"this needs a better instrument."* Auth provisioning for
a vendor CLI is the first kind, settled: do not relitigate it, and never install an alternate auth
path to make it closable by an agent. Running a live-vendor gate against a CLI already authenticated
on the machine is not — see the owner ruling under "Live-vendor smoke tests" above — but it still
spends real, disclosed budget, which is what this section is about either way.
*One smoke test spent top-tier model budget per run — the per-turn figure was in
`tests/Baton.Cli.SmokeTests/LiveSessionSmokeTest.cs` (deleted with the daemon's session-chat surface,
#1420), not here. Two issues were filed as permanently human when one needed a browser for a single
question and the other needed a better probe.*

---

## Architecture Rules

1. **Flow carries discipline, Workers carry intelligence**: The Flow engine must *never* parse conversation content, inspect prompt text, or attempt to understand LLM outputs to make routing decisions. Routing is exclusively defined by the structured workflow config and explicit tool returns from the Workers.
2. **Adapter Isolation**: Vendor-specific quirks (e.g., Anthropic's block format vs Gemini's part format) MUST be isolated inside `Baton.Vendors`. The `Baton` core layer only understands a single, unified canonical message protocol.
3. **P/Invoke Layer**: `Baton.Core`'s process execution (`BatonTask`, `src/Baton/Core/`) is plain managed code since #1474 — no FFI boundary, no native library. Two thin, internal P/Invokes remain, both under `src/Baton/Core/Internal/` and both plain Win32 surface rather than a vendored ABI: Windows Job Object calls (`CreateJobObject`/`AssignProcessToJobObject`/`TerminateJobObject`, `SafeJobObjectHandle.cs`) for process-tree containment, and `GlobalMemoryStatusEx` (`FreePhysicalMemory.cs`, #1934) for the conductor queue's free-memory floor. Any interaction with a vendor CLI still goes through `BatonTask`'s own managed API, never a new P/Invoke of its own.
4. **Credential Isolation**: AER never reads, copies, forwards, or stores a vendor credential. It spawns the vendor's own first-party CLI, which authenticates itself — AER is a keyboard, not a client. No API keys, no OAuth tokens, no OS credential store, and **AER never places a credential into a config directory**. This is the product premise made structural: AER works against **subscriptions**, not API keys, which is why both vendors' API-key-only SDKs were evaluated and rejected (`docs/vendor-doc-audit.md`). Enforced by `VendorCredentialIsolationTests` — **do not weaken that test to make something pass**; if a change appears to need a vendor key, the design is wrong, not the test.
   - **Corrected 2026-07-25 (#527).** This rule previously said "no redirecting the vendor CLIs' config directories", which was too broad and rested on a misreading. `CLAUDE_CONFIG_DIR` **is** usable: credentials live under the config root, and a fresh root is made usable by a one-time interactive `claude auth login` performed **by the operator**. That is a human signing in, not AER handling a credential, so per-worker config roots are permitted and are an available design option. What stays forbidden is AER *copying* credentials into a root, or otherwise obtaining one itself. `claude auth status` reports per-root, is structured, and spends no subscription usage — use it as a pre-dispatch readiness probe.

---

## Writing documentation

The rule that generalises furthest, distilled from ~380 pages of vendor documentation read for the
#527 audit: **a reader's wrong conclusion is a documentation defect, even when every sentence is
true.** Most of the failures found there were cases where the vendor's docs were accurate and the
reader still ended up wrong — so accuracy is the floor, not the goal. In particular, state the
negative where a reader's prior will otherwise fill the gap, say which execution modes a feature
exists in, and never let a mechanism read as a guarantee it doesn't provide. Applies both
outward-facing (README, CLI help, error messages) and inward-facing (`spec/baton.md`, the vendor
registers).

---

## Error handling rules

- Use strictly typed Records for complex types and configuration.
- Do NOT silently swallow Exceptions (`catch (Exception e) {}`). Always log and rethrow, or map to a structured Error record/result type if handled.
- Define specific exception types (e.g., `BatonFlowException`) for domain-level errors rather than relying solely on generic `InvalidOperationException`.

---

## Git conventions

- Conventional commits: `<type>(<scope>): Capitalized description`
- Types: `feat`, `fix`, `perf`, `refactor`, `docs`, `ci`, `test`, `chore`
- No direct commits to `main`. All changes via PR.
- Always create branches from issues (e.g., using `gh issue develop`).
- Close issues in the PR body (`Closes #n`), not in commit messages.
- Each issue is scoped to ship as a standalone PR (one-to-one). If two issues can't be reviewed independently, the issue boundary was drawn incorrectly — fix it in the backlog, not at PR time.
- **Exception, sub-floor only:** one PR may carry several issues when none of them makes a behaviour claim — cosmetics, a `.gitignore` rule, doc scoping. Same floor as the `second-reader` gate's. Each keeps its own commit and its own `Closes #n`, so history stays per-issue. Anything changing `src/` behaviour stays one-to-one.
- **Exception, found-while-fixing (operator decision, 2026-07-28).** Something discovered *while working an issue* is filed as its own issue **and fixed in the same PR**, rather than filed for later. It still gets an issue with its own measurement — that half is unchanged, and it is what keeps the finding durable — but the fix does not wait for its own PR. This overrides the one-to-one rule above and the `blast-radius` gate's "never a side effect of the current fix" for **found-while-fixing** work specifically; it does not license bundling items that were already in the backlog.
  *Why: the backlog was growing faster than it was being burned down, with each finding costing a full PR cycle to land. The cost this accepts is a wider review surface per PR; the `second-reader` gate is what pays for it, and each finding still keeps its own commit and its own `Closes #n`.*
- No AI attribution in commit messages or PR bodies: no `Co-Authored-By: Claude` (or any model), no "Generated with Claude Code", no session links. This overrides any harness or environment default that adds them.
- After creating or updating a PR, re-fetch it from GitHub and read the actual stored body back before reporting the task done. Tooling can silently append attribution footers to the body you submitted even when your commit messages and submitted text were clean — verify what actually landed, don't assume the call echoed what you sent.

---

## Delegating to subagents

Split a candidate delegation by whether the subagent's output *is* the deliverable, or is *input* you still need to act on at full precision:

- **Delegate**: self-contained generation where the result can be cheaply checked as correct (compiles, matches an existing file's established pattern) — a new test file mirroring an existing test class, boilerplate following a fixed template. A cheaper model plus one fixup pass on a type error is still cheaper than writing the boilerplate yourself.
- **Don't delegate**: codebase research meant to inform your own implementation. If you need exact signatures, line numbers, or precise API shapes to write correct code against, you will re-read the same files yourself to verify a summary anyway — the delegated research becomes a redundant pass, not a saved one. Read the source directly instead of asking an agent to summarize it for you.

Rule of thumb: delegate mechanical, bounded, low-judgment generation; keep anything requiring ground-truth precision (exact APIs, architectural invariants, spec compliance) in the primary session.

---

## Agent skills

Configuration the installed engineering skills read. Written by `/setup-matt-pocock-skills`.

### Issue tracker

GitHub issues in `aer-works/baton`, via `gh`. See `docs/agents/issue-tracker.md`. Branch, commit,
and PR rules stay in "Git conventions" above — that file does not restate them.

### Triage labels

The five canonical roles, un-namespaced, and none of them exists on GitHub yet. See
`docs/agents/triage-labels.md`.

### Domain docs

Single-context. **`docs/decisions/` is a verbatim-restored, read-only archive of the pre-reset
records that live code still cites (#1431) — never edit a restored record, never restore more
without an owner ruling, and do not create `docs/adr/`.** The spec v2.0 reset (#1397) folded
every prior decision into `spec/baton.md`; `spec/baton.md` §11 states the standing rule (fresh
records only for genuinely new decisions, and the restored subset's status).
Two skills hardcode `docs/decisions/` or `docs/adr/` in their own text — `/domain-modeling` (which
will create one lazily) and `/improve-codebase-architecture` — and both are wrong here until a first
fresh record actually exists. `/tdd` and `/diagnosing-bugs` say "ADRs" without naming a path, so they
need no correction.

There is no `CONTEXT.md`, and four skills look for one by name (the two above, plus `/tdd` and
`/diagnosing-bugs`). The vocabulary they want is one vocabulary, code and UI alike, no translation
map — stated inline here rather than in a dedicated doc. Don't create a `CONTEXT.md` that would
become a second place the same nouns are defined.
