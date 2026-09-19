---
name: baton-review
description: The standing rules for a baton review lane. Read-only, verdict first, findings by severity with file:line and a concrete failure scenario. Applies whenever baton dispatched you to review a PR, a branch, or a claim.
---

# baton review lane

The brief names what to judge; this skill says how a review is conducted and shaped. It is an
adversarial review of claims: the PR body, the lane's own `changes.md`, and the code's comments all
assert things, and the job is to try to falsify each one against the diff and the tree.

## Read-only

- No edits, no commits, no branches, no `gh pr comment`, `edit`, or `merge`. The only writes are
  the output files the Required outputs block names, under `$BATON_OUTPUT_DIR`.
- No gates, no sub-agents, no live vendor CLIs. When a claim needs a build or test result, either
  the engine ran it before your turn (the verify-results file the prompt names, when there is one)
  or it is unmeasured: say which.
- Read with the harness's own file and search tools. The shell grant is a read-only `git`/`gh`
  allowlist; `cd`, `cat`, `head`, pipes, and `git grep` are refused and cost a step each.

## Reading order

1. The issue (`gh issue view <n>`): what was asked.
2. The PR body and the lane's account (`changes.md`, or the `.review/` file the brief names): what
   is claimed.
3. The diff against `origin/main`, then the surrounding code the diff does not show. The diff is
   where the change is; the defects are usually in what it did not touch.

## What to judge, on every review

- Each numbered question in the brief, answered explicitly, even when the answer is "holds".
- **Tests are revert-failing.** Would the test pass against the pre-change code? A test written
  after the fix that cannot fail is not evidence.
- **Comments and docs are claims.** A comment the change falsified without touching, a sentence
  that now overclaims, a spec paragraph that still describes the old behaviour.
- **State enumeration and value provenance.** The review package owns these checks. A new vocabulary
  word with a predicate left unfixed; a value whose source moved with a reader still reading the old
  one. Enumerate readers; the lane's list is a claim. AGENTS.md directs review workers here; the
  implement package holds the shared lane-side form.
- **Record-once.** A fact now stated in two places is a finding, whichever copy is right.
- **Scope.** Anything in the diff the issue did not ask for, and anything the issue asked for that
  the diff does not do.

## Output shape

Write the review file the Required outputs block names incrementally, so a timeout leaves a partial
review rather than none.

1. **Line one is the verdict, alone:** `APPROVE` or `BLOCK`. No other vocabulary. BLOCK when any
   HIGH finding stands or the change does not do what the issue asked; APPROVE otherwise, with the
   MEDIUM and LOW findings listed for the next round.
2. Findings grouped by severity: `HIGH`, then `MEDIUM`, then `LOW`. Each carries:
   - the file and line (`path/to/file.cs:123`), from the diff or the tree, never approximate;
   - a concrete failure scenario: the input or state, and the wrong output or crash it produces. A
     finding with no scenario is an opinion and is LOW at most;
   - what would resolve it, in one sentence.
3. The brief's numbered questions, each answered with the evidence used.
4. What could not be verified, named as such rather than assumed either way.

Where the role also declares a structured verdict file, its `decision` is your own call and is
always written; its findings mirror the prose.

## Public repository

- The two products this project was inspired by are never named, in the review or anywhere else.
- No legal analysis: a licensing, patent, or trademark question is noted for the operator, not
  answered.
