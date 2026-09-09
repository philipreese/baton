# Vendor/model trials (pre-comparator)

Raw data points from one-off trials run 2026-09-04 through 2026-09-08, before the structured
comparator (`benchmarks/comparator.md`, issue #1903) existed. Recovered from conductor session memory
2026-09-09 before that memory was trimmed to standing rules only — this file is the durable home for
the numbers; no narrative, no rationale beyond what's needed to read the row.

## Sol (codex) vs Opus (claude) review pairs

Same commit, same brief, independent review by each vendor.

| PR | Sol (codex) | Opus (claude) | Notes |
|---|---|---|---|
| #1880 | high, 6 min, 1 HIGH (brief-literal: recording didn't meet spec) | high, 9 min, 1 MEDIUM + 3 LOW (prose/consistency defects Sol missed) | Complementary: Sol = spec compliance, Opus = prose sweep. Neither ran tests (review grant). |
| #1879 | high, 10 min, BLOCK: 2 HIGH (partial-write duplication; loss marker latched before durable) + 1 MEDIUM | high, 11 min, APPROVE-with-findings: saw both Sol HIGHs but rated LOW, added 2 real MEDIUM (stale doc, spec over-promise) | Sol severity-calibrated on mechanism; Opus broader on prose/spec, under-rates runtime hazards it can't execute. |
| #1883 (2081-line diff) | ARRESTED at 100-tool-step review cap (101 steps), no verdict | still running when Sol arrested | Sol's shape is many small reads on large diffs — codex reviews need `maxToolSteps` raised for big diffs. |
| #1883 rerun (Sol: 200 steps, "one `git diff origin/main...HEAD`, don't re-read") | 5 min, APPROVE, zero findings | 14 min, BLOCK: 2 HIGH (multi-model token sum priced at requested model; completeness:complete for unread usage) + 3 MEDIUM + 5 LOW | Sol's own check literally named the mechanism and still missed it — cross-file mechanism tracing (parser+resolver+store together) is where Sol loses. |
| #1892 (JSON-lines ledger refactor, solo Sol) | 6 min, APPROVE, zero findings, thin one-sentence summary | — | Conductor spot-check confirmed lock names/file modes/dedupe keys/public surface unchanged; merged 99a9171b. For "no behaviour change" refactors, Sol + a conductor grep sufficed. |

Three-sample read at the time: Sol wins on brief-literal compliance and speed, loses on cross-file
mechanism tracing. Ruling proposed (not re-verified since): engine/accounting PR reviews stay Opus
high; Sol high for doc/tests/tooling PRs and as a cheap second reader.

## Terra / Astra (codex) implement samples

| Sample | Brief | Result | Review |
|---|---|---|---|
| #1885 A/B (2026-09-05 01:14–03:55) | Opus medium vs Terra max, same brief, separate branches | Opus: 90 min → PR #1888, 32 files (16 mechanical), conflicted with main (40-min rebase lane), 2 false body claims. Terra: ~65 min → PR #1890, 13 files, mergeable, engine verify caught a missed round-trip tripwire. | Opus arm: APPROVE 1 MEDIUM + 4 LOW. Terra arm: APPROVE 3 MEDIUM + 6 LOW, all record-vs-code disagreements (unreachable branch w/ fake-reason test, const in wrong home, spec names a discriminator the code doesn't use). Merged #1888 (Opus), closed #1890 (Terra). |
| #1897 (Terra max, 40-min box) | launcher fix | PR #1899 in 35 min, +48/−6, exactly the 3 files named, both test arms specified, clean body | Sol-high, 2 min, 120-step budget, APPROVE no findings. Merged 5f098c71. |
| #1898 (Astra high, 30-min box, 150 steps) | regex fix | PR #1900 in 26 min, +37/−6, exactly the files named, pinned arms correct | Sol-high, 2 min, APPROVE 1 LOW (no bare `6` arm). Merged 278ff787. |

Read at the time: Terra max is a credible implementer at smaller footprint but sloppier on the repo's
record discipline (record-once, drift checks, spec↔code) — the class this repo gates hardest. Astra
high credible for small precisely-specified fixes. Both codex implementers produced tighter diffs than
the Opus lanes on comparable briefs, on this small sample.

## Twin (A/B) tool-step arrests, 2026-09-05 afternoon

Same brief, two branches each: #1901 (Opus high vs Sol max, branch `1901-sol`) and #1530 part 1
(Sonnet high vs Terra max, branch `1530-sonnet`).

- Both codex arms arrested at the implement role's default 300-tool-step cap (Terra at 52 min, Sol at
  ~45 min) with large uncommitted trees. Redispatched with `--max-tool-steps 600` + a "commit a
  checkpoint first, do not start over" continuation brief; both then finished (Sol → PR #1915 in ~35
  more min; Terra's second attempt's whole process tree died at 12:44 with no terminal fact, cause
  unknown, third attempt launched).
- Claude arms finished in one pass: Opus → PR #1913 in 68 min; Sonnet → 3 commits in 80 min but timed
  out before pushing (conductor pushed manually) → PR #1916. Both got Opus-high BLOCKs with 2 highs
  each.
- Outcome: #1901 → Opus arm merged (1de43a0c), Sol arm closed (2 open mediums at same stage). #1530
  part 1 → Sonnet arm merged (88a97b7c) after 3 review rounds + 2 conductor pushes; Terra arm still in
  its second fix round with a 3-high first review (removed a safety guard, broke the spec register) —
  closed as non-selected. Neither codex arm won this pair; both needed 600 tool steps and produced
  deeper first-review findings than the Claude arm on the same brief. Sample size two, recorded on
  #1903.

Standing lesson: give codex implement lanes `maxToolSteps` ≥ 600 from the start (they read files one
at a time); every implement brief should say "commit a checkpoint before any long build."

## Lane refusal audit (2026-09-05, `night/lane_audit.py`, 12 h, 59 rooms)

Refusals ran ~4% of tool calls per vendor overall, but 10–20% of a REVIEW room's steps specifically.

- Claude reviews: the review shell grant refuses `cd`, `cat`, `head`, `echo`, `git grep`, and any
  `&&`/`|` compound containing one of them — 46 hook denials measured.
- Codex reviews: backslash paths (14 denials), `apply_patch` in the shell (5), `git -C`, room paths.
- Agy: not refusals but repeats — 88/192 identical `view_file` re-reads in one room; `manage_task`
  withheld.

Led to a standing "tools line" in every review brief (claude: Read/Grep only, no cd/cat/pipes; codex:
baton_read_text/baton_search_text, forward slashes, no apply_patch; both: quote prior findings
inline, never a room path) — this rule already lives in `[[dispatch-and-vendor-rules]]`, this section
is the measurement that produced it.

## Buildlock contention (2026-09-06 23:40, measured across 60 lanes)

1,343 buildlock invocations across 60 lanes (~22 per lane), 346 minutes total of lock waits. Led to
the standing "build once per round, `dotnet test --no-build` for repeats" brief rule (already in
`[[dispatch-and-vendor-rules]]`) — the waits were the bottleneck, not the box's clock speed; the
15.7 GB RAM box's build lock protects memory as much as MSB4166, so build *count* is the lever, not
concurrency.

## In-lane second-reader cost (2026-09-03, 24 implement rooms)

8 of 24 rooms spawned an in-lane `Agent` second reader (baton's own then-CLAUDE.md rule 7 told any PR
author to); those 8 accounted for ~1.24B of ~1.56B cache-read tokens that night — the six heaviest
rooms were all agent rooms (108–424M each vs 7–60M typical). Separately, a lane's own `pixi run gates`
run duplicated the engine's post-exit verify behind the shared build lock, turning a 5-minute change
into a 47-minute lane. After #1811 mechanically withheld `Agent`/`Task` for implement/review roles:
**25 rooms, 0 Agent spawns, ~0.59B cache-read, 2026-09-04 02:00–05:15** vs 24 rooms / 1.56B before —
~2.6× cheaper per room, more merges.
