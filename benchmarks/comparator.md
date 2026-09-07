# Comparator

## What it measures

Each issue has nine possible arms: A is a Baton lane, B is a plain headless session, and C is a
headless session told to use the CLI's native subagent mechanism; each arm is crossed with `claude`,
`codex`, and `agy`. Every arm gets the same frozen brief pinned on its issue. An independent Opus
high review reads each PR without reading its siblings, and one winner merges after at most one
bounded fix round. The ledger below points to the raw issue-comment rows instead of copying their
timings, token counts, costs, and diff figures.

## Isolation

The 2026-09-06 lesson in [#2001](https://github.com/philipreese/baton/issues/2001) is that every
headless arm runs in its own single-branch clone. Sample 8's `agy` arm instead ran in the shared
clone and inspected its sibling branch and PR before committing, so that arm is void. Every sample's
room streams are scanned for sibling branch names and PR numbers; either one voids the arm. The
corresponding broker-side isolation rule remains queued under #2001.

## Decision rule

The operator ruled these before the last samples closed:

1. Per role, the tier pin goes to the vendor whose arm produced the merged PR most often across that role's samples. A tie goes to the cheaper arm by measured cost.
2. A run that made no edit, was arrested, or was voided counts as a loss for that sample when the cause was the vendor's. When the cause was Baton's own tooling (#1996, #1998, #2001, #2002), the sample is voided for that vendor and rerun after the fix lands, so every vendor gets a fair run.
3. Between two merged arms, fewer fix rounds ranks higher.
4. Arm C (native subagent) replaces arm B only if it wins at least two of the three claude samples.
5. No tier changes from this experiment until the reruns in rule 2 have happened.

Standing principle, in the operator's words: “measure before deciding.” ([ruling][rule])

## Ledger

`sourceKind` says where this summary row came from. A not-run row is `hand-recorded`, because no
execution stream exists. “See row” deliberately leaves the raw measurement in its linked comment.

| Sample | Issue | Arm | Vendor | `sourceKind` | PR | Tokens | Review outcome | Merged | Raw row |
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
| 2 | #1911 | B | `claude` | `hand-recorded` | — | — | arm A only, B/C not run (rerun-only lanes pulled 2026-09-06 16:30) | no | [row][s2] |
| 2 | #1911 | B | `codex` | `hand-recorded` | — | — | arm A only, B/C not run (rerun-only lanes pulled 2026-09-06 16:30) | no | [row][s2] |
| 2 | #1911 | B | `agy` | `hand-recorded` | — | — | arm A only, B/C not run (rerun-only lanes pulled 2026-09-06 16:30) | no | [row][s2] |
| 2 | #1911 | C | `claude` | `hand-recorded` | — | — | arm A only, B/C not run (rerun-only lanes pulled 2026-09-06 16:30) | no | [row][s2] |
| 2 | #1911 | C | `codex` | `hand-recorded` | — | — | arm A only, B/C not run (rerun-only lanes pulled 2026-09-06 16:30) | no | [row][s2] |
| 2 | #1911 | C | `agy` | `hand-recorded` | — | — | arm A only, B/C not run (rerun-only lanes pulled 2026-09-06 16:30) | no | [row][s2] |
| 3 | #1918 | A | `claude` | `baton-room` | #1952 | See row | APPROVE → bounded fix | yes | [row][s3] |
| 3 | #1918 | A | `codex` | `hand-recorded` | — | — | not run; arm A claude only | no | [row][s3] |
| 3 | #1918 | A | `agy` | `hand-recorded` | — | — | not run; arm A claude only | no | [row][s3] |
| 3 | #1918 | B | `claude` | `hand-recorded` | — | — | arm A only, B/C not run (rerun-only lanes pulled 2026-09-06 16:30) | no | [row][s3] |
| 3 | #1918 | B | `codex` | `hand-recorded` | — | — | arm A only, B/C not run (rerun-only lanes pulled 2026-09-06 16:30) | no | [row][s3] |
| 3 | #1918 | B | `agy` | `hand-recorded` | — | — | arm A only, B/C not run (rerun-only lanes pulled 2026-09-06 16:30) | no | [row][s3] |
| 3 | #1918 | C | `claude` | `hand-recorded` | — | — | arm A only, B/C not run (rerun-only lanes pulled 2026-09-06 16:30) | no | [row][s3] |
| 3 | #1918 | C | `codex` | `hand-recorded` | — | — | arm A only, B/C not run (rerun-only lanes pulled 2026-09-06 16:30) | no | [row][s3] |
| 3 | #1918 | C | `agy` | `hand-recorded` | — | — | arm A only, B/C not run (rerun-only lanes pulled 2026-09-06 16:30) | no | [row][s3] |
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
| 5 | #1948 | A | `codex` | `baton-room` | #1980 | per-last-event, unusable until #1927's follow-up | BLOCK | no | [row][s5] |
| 5 | #1948 | A | `agy` | `hand-recorded` | — | — | not run | no | [row][s5] |
| 5 | #1948 | B | `claude` | `headless-stream` | #1975 | See row | APPROVE | no | [row][s5] |
| 5 | #1948 | B | `codex` | `hand-recorded` | — | — | not run | no | [row][s5] |
| 5 | #1948 | B | `agy` | `hand-recorded` | — | — | not run | no | [row][s5] |
| 5 | #1948 | C | `claude` | `headless-stream` | #1977 | See row | APPROVE | yes | [row][s5] |
| 5 | #1948 | C | `codex` | `hand-recorded` | — | — | not run | no | [row][s5] |
| 5 | #1948 | C | `agy` | `hand-recorded` | — | — | not run | no | [row][s5] |
| 6 | #1949 | A | `claude` | `baton-room` | #1990 | See row | BLOCK | no | [row][s6] |
| 6 | #1949 | A | `codex` | `baton-room` | #1989 | per-last-event, unusable until #1927's follow-up | BLOCK | no | [row][s6] |
| 6 | #1949 | A | `agy` | `baton-room` | #1991 | See row | arrested, then hand-pushed; BLOCK (#2002) | no | [row][s6] |
| 6 | #1949 | B | `claude` | `headless-stream` | #2000 | See row | BLOCK | no | [row][s6] |
| 6 | #1949 | B | `codex` | `headless-stream` | #2006 | See row | BLOCK | no | [row][s6] |
| 6 | #1949 | B | `agy` | `hand-recorded` | — | — | not run pending #2002 | no | [row][s6] |
| 6 | #1949 | C | `claude` | `headless-stream` | #2003 | See row | APPROVE → bounded fix | yes | [row][s6] |
| 6 | #1949 | C | `codex` | `headless-stream` | #2007 | See row | BLOCK; no native subagent used | no | [row][s6] |
| 6 | #1949 | C | `agy` | `hand-recorded` | — | — | not run pending #2002 | no | [row][s6] |
| 7 | #1951 | A | `claude` | `baton-room` | #1993 | See row | BLOCK → bounded fix | yes | [row][s7] |
| 7 | #1951 | A | `codex` | `baton-room` | #1997 | per-last-event, unusable until #1927's follow-up | BLOCK; hand-pushed after #1998 | no | [row][s7] |
| 7 | #1951 | A | `agy` | `baton-room` | — | See row | arrested with no commit or PR; rerun after #2002 | no | [row][s7] |
| 7 | #1951 | B | `claude` | `hand-recorded` | — | — | not run | no | [row][s7] |
| 7 | #1951 | B | `codex` | `hand-recorded` | — | — | not run; codex rerun owed | no | [row][s7] |
| 7 | #1951 | B | `agy` | `hand-recorded` | — | — | not run pending #2002; agy rerun owed | no | [row][s7] |
| 7 | #1951 | C | `claude` | `hand-recorded` | — | — | not run | no | [row][s7] |
| 7 | #1951 | C | `codex` | `hand-recorded` | — | — | not run; codex rerun owed | no | [row][s7] |
| 7 | #1951 | C | `agy` | `hand-recorded` | — | — | not run pending #2002; agy rerun owed | no | [row][s7] |
| 8 | #1943 | A | `claude` | `baton-room` | #1994 | See row | APPROVE → bounded fix | yes | [row][s8] |
| 8 | #1943 | A | `codex` | `baton-room` | — | per-last-event, unusable until #1927's follow-up | void: no edit under the grant (#1996); rerun owed | no | [row][s8] |
| 8 | #1943 | A | `agy` | `baton-room` | #1999 | See row | void: sibling contamination (#2001), repeated steps (#2002); rerun owed | no | [row][s8] |
| 8 | #1943 | B | `claude` | `hand-recorded` | — | — | not run | no | [row][s8] |
| 8 | #1943 | B | `codex` | `hand-recorded` | — | — | not run; codex rerun owed | no | [row][s8] |
| 8 | #1943 | B | `agy` | `hand-recorded` | — | — | not run pending #2002; agy rerun owed | no | [row][s8] |
| 8 | #1943 | C | `claude` | `hand-recorded` | — | — | not run | no | [row][s8] |
| 8 | #1943 | C | `codex` | `hand-recorded` | — | — | not run; codex rerun owed | no | [row][s8] |
| 8 | #1943 | C | `agy` | `hand-recorded` | — | — | not run pending #2002; agy rerun owed | no | [row][s8] |

## Caveats

- The box carried other load during every sample, so wall clocks include contention.
- `codex` arm C did not use its multi-agent feature in three runs.
- Across the three `claude` comparisons, arm C cost 2.2×, 2.2×, and 1.3× arm B; it won two of three.
- The `agy` rows are not a model measurement until #2002 lands.
- Samples 7 and 8 still owe `codex` and `agy` reruns.

## How to add a sample

1. Pin the frozen brief on the issue.
2. Dispatch arm A × 3 through the queue runner.
3. Run arms B and C headless, one at a time, using the per-arm single-branch clone recipe.
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
[rule]: https://github.com/philipreese/baton/issues/1903#issuecomment-5563450634
