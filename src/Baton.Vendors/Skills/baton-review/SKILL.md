---
name: baton-review
description: The standing rules for a baton review lane. Read-only, verdict first, findings by severity with file:line and a concrete failure scenario. Applies whenever baton dispatched you to review a PR, a branch, or a claim.
---

# baton review lane

The brief names what to judge. Review its claims adversarially: test the PR body, the lane's
`changes.md`, and code comments against the diff and tree.

## Read-only

- No edits, commits, branches, `gh pr comment`, `edit`, `merge`, or other public writes. The only
  writes are the Required outputs under `$BATON_OUTPUT_DIR`.
- No gates, sub-agents, or live vendor CLIs. For build/test claims, use the engine's verify-results
  file when supplied; otherwise say they are unmeasured.
- Use the harness's file/search tools. The shell grant is a read-only `git`/`gh` allowlist;
  `cd`, `cat`, `head`, pipes, and `git grep` are refused.

## Reading order

1. The issue (`gh issue view <n>`): what was asked.
2. The PR body and lane account (`changes.md`, or the named `.review/` file): what is claimed.
3. The diff against `origin/main`, then surrounding code it does not show; defects are often in
   what the diff did not touch.

## What to judge, on every review

- Each numbered brief question, answered explicitly, even when the answer is "holds".
- **Tests are revert-failing.** Ask whether each passes against pre-change code; a post-fix test
  that cannot fail is no evidence.
- **Comments and docs are claims.** Check falsified untouched comments, overclaims, and stale spec
  behavior.
- **State enumeration and value provenance.** The review package owns these checks. Find every predicate
  for new vocabulary and every reader of a changed value source; AGENTS.md directs review workers
  here and the implement package holds the shared lane-side form.
- **Record-once.** A fact stated twice is a finding, whichever copy is right.
- **Scope.** Find diff work the issue did not ask for and requested work the diff omits.

## Output shape

Write the named review file incrementally, so a timeout leaves a partial review.

1. **Line one is the verdict, alone:** `APPROVE` or `BLOCK`. No other vocabulary. BLOCK when any
   HIGH finding stands or the change does not do what the issue asked; APPROVE otherwise, with the
   MEDIUM and LOW findings listed for the next round.
2. Findings grouped by severity: `HIGH`, then `MEDIUM`, then `LOW`. Each carries the exact file
   and line (`path/to/file.cs:123`), a concrete input/state and wrong output/crash scenario (without
   one it is an opinion and LOW at most), and a one-sentence resolution.
3. The brief's numbered questions, each answered with the evidence used.
4. What could not be verified, named as such rather than assumed either way.

Where the role declares a structured verdict, always write your own `decision`; its findings mirror
the prose. Set `completion` to `in_progress` while evidence or required review work remains, and to
`complete` only afterward. An incomplete review may record `BLOCK` with `in_progress`; a completed
`BLOCK` has `complete`. Publish the final verdict only when review work is complete. See
`spec/baton.md` §13 for completion evidence limits and routing consequences.

## Public repository

- The two products this project was inspired by are never named, in the review or anywhere else.
- No legal analysis: a licensing, patent, or trademark question is noted for the operator, not
  answered.
