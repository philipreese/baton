---
name: baton-implement
description: Rules for Baton implementation lanes.
---

# baton implement lane

The brief defines the work; this governs lane conduct. Only explicit grants override it.

## Before anything

- Run `git status` and `git log --oneline -3`; in a fresh detached clone, create the named branch
  from `origin/main`; otherwise verify it.
- Read the cited issue (`gh issue view <n>`) and verify its claims against the tree; the body may be stale.

## What the lane never does

- No aggregate gates or receipts; the engine verifies after exit. The normal pre-push
  `gates-lane-fast` still runs; fix real failures.
- No sub-agents. The second reader is the conductor's own review lane.
- No live vendor CLIs (`claude`, `codex`, `agy`, anything spending subscription budget) unless the
  brief grants them by name, with a run count.
- No `--no-verify`, no force-push.
- Write only in the workspace or `$BATON_OUTPUT_DIR`; use fixtures, never real vendor memory or the
  operator's Baton home.
- Never restart or reinstall the real daemon, its scheduled task, or the installed tool.

## Two checks on every code change

This package owns these checks. Apply them and list each finding in `changes.md`; `AGENTS.md` routes
workers here instead of restating them.

- **State enumeration.** For an added/renamed state word, list and fix every switching predicate.
- **Value provenance.** For a changed value source, list every reader and confirm each remains correct.

## Verification, before the commit

Run in the workspace, recording each exit code:

1. `dotnet build -warnaserror` (through `python tools/buildlock.py` where the repo has it).
2. The touched test projects only, filtered where the brief says so; never the whole suite.
3. `dotnet format --verify-no-changes`.
4. `pixi run audit-recordonce`, and `pixi run audit-docsbudget` where that task exists.

Report red checks; name any that cannot run and why.

## Delivery

- One commit on the named branch unless the brief says otherwise, subject
  `<type>(<scope>): Capitalised description`; a subject the brief gives is used verbatim.
- Push to `origin <branch>`; open the PR as a draft with `gh pr create --draft`. The body ends with
  `Closes #<n>` (or
  `Part of #<n>`, as the brief says) alone on the last line.
- No AI attribution. Read back the stored body (`gh pr view --json body`) and fix appended text.
- `gh pr view` with no selector reaches your own PR; naming another PR's number is refused.

## End-of-implementation self-check

Before completion, in this same context:

- Compare final `origin/main..HEAD` to the brief; name any unmet acceptance.
- Confirm final HEAD is pushed and a draft PR exists at that head.
- Re-read the stored PR body and `changes.md`; all claims must match the final commit.
- Confirm every required check ran and every recorded exit code is true.

This is result validation, not an independent review. Failed or uncertain GitHub effects remain explicit
obligations; issuing a command does not prove its intended state.

## changes.md

Write the review handoff to `$BATON_OUTPUT_DIR/changes.md`:

- One section per requested finding/slice: files, change, and reason.
- Every verification command with its exit code.
- What was NOT done and why; only the operator may scale down.
- If a protected path (`tools/diff-shape/diff_shape.py`; spec/baton.md §11 C-15) changed, add
  `## PROTECTED` naming it and why operator merge is required.

## Public repository

- Never name the two products this project drew its inspiration from, anywhere.
- No legal analysis: licensing, patent, and trademark questions are the operator's.
