# Comparator

## What it measures

Each issue has nine possible arms: A is a Baton lane, B is a plain headless session, and C is a
headless session told to use the CLI's native subagent mechanism; each arm is crossed with `claude`,
`codex`, and `agy`. Every arm gets the same frozen brief pinned on its issue. An independent Opus
high review reads each PR without reading its siblings, and one winner merges after at most one
bounded fix round. The ledger below points to the raw issue-comment rows instead of copying their
timings, token counts, costs, and diff figures.

## Isolation

The 2026-09-06 lesson in [#2001](https://github.com/philipreese/baton/issues/2001) came out of a
**Baton lane**, not a headless one: sample 8's `agy` arm was arm A, dispatched through the queue
runner on the shared clone (worktrees off one `.git`), and it read `git branch --contains` and
`gh pr view 1994` before committing, so that arm is void. The single-branch per-arm clone is
therefore the recipe for **both** the headless arms (B and C) and the Baton-lane arms (A) — #2001
part 1 changed the headless recipe first, and sample 6's codex B/C arms are the first that ran under
it. The broker-side rule that reaches an arm-A lane from inside Baton is
[PR #2016](https://github.com/philipreese/baton/pull/2016) ("Refuse an implement lane a PR it did not
open"), still open and unmerged.

Stream scanning is not yet a standing pass over every sample: sample 6's seven streams were scanned
for sibling branch names and PR numbers and were clean ([row][s6]), and sample 8's `agy` arm was read
post hoc, which is how its contamination was found ([row][s8]). Where a scan runs, either a sibling
branch name or a sibling PR number voids the arm.

## Decision rule

The operator ruled these before the last samples closed:

1. Per role, the tier pin goes to the vendor whose arm produced the merged PR most often across that role's samples. A tie goes to the cheaper arm by measured cost.
2. A run that made no edit, was arrested, or was voided counts as a loss for that sample when the cause was the vendor's. When the cause was Baton's own tooling (#1996, #1998, #2001, #2002), the sample is voided for that vendor and rerun after the fix lands, so every vendor gets a fair run.
3. Between two merged arms, fewer fix rounds ranks higher.
4. Arm C (native subagent) replaces arm B only if it wins at least two of the three claude samples.
5. No tier changes from this experiment until the reruns in rule 2 have happened.

Standing principle, in the operator's words: “measure before deciding.” ([ruling][rule])

## Ledger

`Provenance` says where this summary row came from — this document's own vocabulary, **not** the cost
ledger's `sourceKind` field (`spec/baton.md` §7), whose closed set contains none of these values. A
not-run row is `hand-recorded`, because no execution stream exists. “See row” deliberately leaves the
raw measurement in its linked comment.

| Sample | Issue | Arm | Vendor | Provenance | PR | Tokens | Review outcome | Merged | Raw row |
|---:|---:|:---:|---|---|---:|---|---|:---:|---|
| 1 | #1902 | A | `claude` | `baton-room` | #1956 | See row | BLOCK → bounded fix | yes | [row][s1] |
| 1 | #1902 | A | `codex` | `hand-recorded` | — | — | not run; rerun-only lane pulled 2026-09-06 16:30 | no | [row][s1] |
| 1 | #1902 | A | `agy` | `hand-recorded` | — | — | not run; rerun-only lane pulled 2026-09-06 16:30 | no | [row][s1] |
| 1 | #1902 | B | `claude` | `hand-recorded` | — | — | not run after the merged-arm refusal | no | [row][s1] |
| 1 | #1902 | B | `codex` | `headless-stream` | #1986 | See row | BLOCK | no | [row][s1] |
| 1 | #1902 | B | `agy` | `headless-stream` | #1992 | See row | BLOCK | no | [row][s1] |
| 1 | #1902 | C | `claude` | `hand-recorded` | — | — | not run | no | [row][s1] |
| 1 | #1902 | C | `codex` | `headless-stream` | #1988 | See row | BLOCK; no native subagent used | no | [row][s1] |
| 1 | #1902 | C | `agy` | `headless-stream` | #1995 | See row | BLOCK | no | [row][s1] |
| 2 | #1911 | A | `claude` | `baton-room` | #1960 | See row | BLOCK → bounded fix | yes | [row][s2] |
| 2 | #1911 | A | `codex` | `hand-recorded` | — | — | not run; arm A claude only | no | [row][s2] |
| 2 | #1911 | A | `agy` | `hand-recorded` | — | — | not run; arm A claude only | no | [row][s2] |
| 2 | #1911 | B | `claude` | `headless-stream` | — | See row | ran, then refused after 2 min — found the merge one commit ahead of its worktree; no edit, no PR. Not a sample | no | [row][armB] |
| 2 | #1911 | B | `codex` | `hand-recorded` | — | — | not run | no | [row][s2] |
| 2 | #1911 | B | `agy` | `hand-recorded` | — | — | not run | no | [row][s2] |
| 2 | #1911 | C | `claude` | `hand-recorded` | — | — | not run | no | [row][s2] |
| 2 | #1911 | C | `codex` | `hand-recorded` | — | — | not run | no | [row][s2] |
| 2 | #1911 | C | `agy` | `hand-recorded` | — | — | not run | no | [row][s2] |
| 3 | #1918 | A | `claude` | `baton-room` | #1952 | See row | APPROVE → bounded fix | yes | [row][s3] |
| 3 | #1918 | A | `codex` | `hand-recorded` | — | — | not run; arm A claude only | no | [row][s3] |
| 3 | #1918 | A | `agy` | `hand-recorded` | — | — | not run; arm A claude only | no | [row][s3] |
| 3 | #1918 | B | `claude` | `headless-stream` | #1968 | See row | ran, PR #1968 (+177/−31), closed unmerged; excluded from scoring — post-merge worktree, issue read as closed. Review outcome not recorded | no | [row][armB] |
| 3 | #1918 | B | `codex` | `hand-recorded` | — | — | not run | no | [row][s3] |
| 3 | #1918 | B | `agy` | `hand-recorded` | — | — | not run | no | [row][s3] |
| 3 | #1918 | C | `claude` | `hand-recorded` | — | — | not run | no | [row][s3] |
| 3 | #1918 | C | `codex` | `hand-recorded` | — | — | not run | no | [row][s3] |
| 3 | #1918 | C | `agy` | `hand-recorded` | — | — | not run | no | [row][s3] |
| 4 | #1947 | A | `claude` | `hand-recorded` | — | — | not run | no | [row][s4] |
| 4 | #1947 | A | `codex` | `hand-recorded` | — | — | not run | no | [row][s4] |
| 4 | #1947 | A | `agy` | `baton-room` | #1973 | See row | APPROVE | no | [row][s4] |
| 4 | #1947 | B | `claude` | `headless-stream` | #1969 | See row | APPROVE | yes | [row][s4] |
| 4 | #1947 | B | `codex` | `hand-recorded` | — | — | not run | no | [row][s4] |
| 4 | #1947 | B | `agy` | `hand-recorded` | — | — | not run | no | [row][s4] |
| 4 | #1947 | C | `claude` | `headless-stream` | #1970 | See row | APPROVE | no | [row][s4] |
| 4 | #1947 | C | `codex` | `hand-recorded` | — | — | not run | no | [row][s4] |
| 4 | #1947 | C | `agy` | `hand-recorded` | — | — | not run | no | [row][s4] |
| 5 | #1948 | A | `claude` | `hand-recorded` | — | — | not run | no | [row][s5] |
| 5 | #1948 | A | `codex` | `baton-room` | #1980 | per-last-event, unusable ([#2020](https://github.com/philipreese/baton/issues/2020), open) | BLOCK | no | [row][s5] |
| 5 | #1948 | A | `agy` | `hand-recorded` | — | — | not run | no | [row][s5] |
| 5 | #1948 | B | `claude` | `headless-stream` | #1975 | See row | APPROVE | no | [row][s5] |
| 5 | #1948 | B | `codex` | `hand-recorded` | — | — | not run | no | [row][s5] |
| 5 | #1948 | B | `agy` | `hand-recorded` | — | — | not run | no | [row][s5] |
| 5 | #1948 | C | `claude` | `headless-stream` | #1977 | See row | APPROVE | yes | [row][s5] |
| 5 | #1948 | C | `codex` | `hand-recorded` | — | — | not run | no | [row][s5] |
| 5 | #1948 | C | `agy` | `hand-recorded` | — | — | not run | no | [row][s5] |
| 6 | #1949 | A | `claude` | `baton-room` | #1990 | See row | BLOCK | no | [row][s6] |
| 6 | #1949 | A | `codex` | `baton-room` | #1989 | per-last-event, unusable ([#2020](https://github.com/philipreese/baton/issues/2020), open) | BLOCK | no | [row][s6] |
| 6 | #1949 | A | `agy` | `baton-room` | #1991 | See row | arrested, then hand-pushed; BLOCK (#2002) | no | [row][s6] |
| 6 | #1949 | B | `claude` | `headless-stream` | #2000 | See row | BLOCK | no | [row][s6] |
| 6 | #1949 | B | `codex` | `headless-stream` | #2006 | See row | BLOCK | no | [row][s6] |
| 6 | #1949 | B | `agy` | `hand-recorded` | — | — | not run pending #2002 | no | [row][s6] |
| 6 | #1949 | C | `claude` | `headless-stream` | #2003 | See row | APPROVE → bounded fix | yes | [row][s6] |
| 6 | #1949 | C | `codex` | `headless-stream` | #2007 | See row | BLOCK; no native subagent used | no | [row][s6] |
| 6 | #1949 | C | `agy` | `hand-recorded` | — | — | not run pending #2002 | no | [row][s6] |
| 7 | #1951 | A | `claude` | `baton-room` | #1993 | See row | BLOCK → bounded fix | yes | [row][s7] |
| 7 | #1951 | A | `codex` | `baton-room` | #1997 | per-last-event, unusable ([#2020](https://github.com/philipreese/baton/issues/2020), open) | BLOCK, 4 medium / 5 low; hand-pushed after #1998 — scored a LOSS, not a voided run. A rerun ran after #2013 anyway and landed PR #2023: BLOCK, 3 medium, closed unmerged | no | [row][s7], [rerun][s7r] |
| 7 | #1951 | A | `agy` | `baton-room` | — | See row | arrested with no commit or PR; rerun after #2002 | no | [row][s7] |
| 7 | #1951 | B | `claude` | `hand-recorded` | — | — | not run | no | [row][s7] |
| 7 | #1951 | B | `codex` | `hand-recorded` | — | — | not run | no | [row][s7] |
| 7 | #1951 | B | `agy` | `hand-recorded` | — | — | not run pending #2002 | no | [row][s7] |
| 7 | #1951 | C | `claude` | `hand-recorded` | — | — | not run | no | [row][s7] |
| 7 | #1951 | C | `codex` | `hand-recorded` | — | — | not run | no | [row][s7] |
| 7 | #1951 | C | `agy` | `hand-recorded` | — | — | not run pending #2002 | no | [row][s7] |
| 8 | #1943 | A | `claude` | `baton-room` | #1994 | See row | APPROVE → bounded fix | yes | [row][s8] |
| 8 | #1943 | A | `codex` | `baton-room` | #2021 | per-last-event, unusable ([#2020](https://github.com/philipreese/baton/issues/2020), open) | first run void: no edit under the grant (#1996). Rerun after #2013 landed PR #2021: BLOCK, 1 high / 2 medium, closed unmerged | no | [row][s8], [rerun][s8r] |
| 8 | #1943 | A | `agy` | `baton-room` | #1999 | See row | void: sibling contamination (#2001), repeated steps (#2002); rerun owed | no | [row][s8] |
| 8 | #1943 | B | `claude` | `hand-recorded` | — | — | not run | no | [row][s8] |
| 8 | #1943 | B | `codex` | `hand-recorded` | — | — | not run | no | [row][s8] |
| 8 | #1943 | B | `agy` | `hand-recorded` | — | — | not run pending #2002 | no | [row][s8] |
| 8 | #1943 | C | `claude` | `hand-recorded` | — | — | not run | no | [row][s8] |
| 8 | #1943 | C | `codex` | `hand-recorded` | — | — | not run | no | [row][s8] |
| 8 | #1943 | C | `agy` | `hand-recorded` | — | — | not run pending #2002 | no | [row][s8] |

## Caveats

- The box carried other load during every sample, so wall clocks include contention.
- `codex` arm C did not use its multi-agent feature in either of the two runs it made: #1988 (sample 1 — its only non-file, non-shell calls are three empty `wait` calls, [row][s1]) and #2007 (sample 6 — `0 collab_tool_call`, [row][s6]). Every other `codex` arm C cell is `not run`.
- Across the three `claude` arm B/C comparisons (samples 4, 5, 6), arm C cost more than arm B every time and won two of the three merges; the ratios and the dollar figures they come from are in the sample-6 row ([row][s6]), not copied here.
- The `agy` rows are not a model measurement until #2002 lands.
- What samples 7 and 8 still owe, arm by arm — `codex` owes no rerun anywhere:
  - Sample 7 arm A `agy`: arrested with no commit or PR; rerun owed after #2002.
  - Sample 8 arm A `agy`: void (#2001/#2002); rerun owed.
  - Sample 7 arm A `codex`: settled as a LOSS on #1997's own findings, not a rerun; the post-#2013 rerun (#2023) landed anyway.
  - Sample 8 arm A `codex`: the rerun landed as #2021 (BLOCK, closed unmerged), so that arm carries a verdict, not a debt.
  - Samples 7 and 8, arms B and C: never run at all, so what they owe is a first run, not a rerun.

## How to add a sample

1. Pin the frozen brief on the issue.
2. Dispatch arm A × 3 through the queue runner, each lane in its own single-branch clone — the shared clone is what let sample 8's `agy` arm read its sibling's PR, and the broker-side guard is still open (PR #2016).
3. Run arms B and C headless, one at a time, under the same per-arm single-branch clone recipe.
4. Send every PR to the same independent review, with sibling PRs excluded.
5. Post the raw row on #1903 and update this ledger with its pointer.

[s1]: https://github.com/philipreese/baton/issues/1903#issuecomment-5562760290
[s2]: https://github.com/philipreese/baton/issues/1903#issuecomment-5559989763
[s3]: https://github.com/philipreese/baton/issues/1903#issuecomment-5559989763
[s4]: https://github.com/philipreese/baton/issues/1903#issuecomment-5560737453
[s5]: https://github.com/philipreese/baton/issues/1903#issuecomment-5561743124
[s6]: https://github.com/philipreese/baton/issues/1903#issuecomment-5563557923
[s7]: https://github.com/philipreese/baton/issues/1903#issuecomment-5563664913
[s8]: https://github.com/philipreese/baton/issues/1903#issuecomment-5563182256
[armB]: https://github.com/philipreese/baton/issues/1903#issuecomment-5560240517
[s7r]: https://github.com/philipreese/baton/issues/1903#issuecomment-5565829724
[s8r]: https://github.com/philipreese/baton/issues/1903#issuecomment-5565755238
[rule]: https://github.com/philipreese/baton/issues/1903#issuecomment-5563450634
