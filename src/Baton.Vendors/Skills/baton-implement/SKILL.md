---
name: baton-implement
description: Rules for Baton implementation lanes.
---

# baton implement lane

The brief defines the work; this governs lane conduct. Only explicit grants override it.

## Before anything

- Run `git status` and `git log --oneline -3`; create/verify the named branch from `origin/main`.
- Read the issue (`gh issue view <n>`) and verify claims against the tree.

## What the lane never does

- No aggregate gates or receipts; the engine verifies after exit. Fix pre-push `gates-lane-fast` failures.
- No sub-agents. The second reader is the conductor's own review lane.
- No live vendor CLIs unless the brief grants each by name and run count.
- No `--no-verify`, no force-push.
- Write only in the workspace or `$BATON_OUTPUT_DIR`; use fixtures, not real vendor memory/home.
- Never restart/reinstall the real daemon, task, or installed tool.

## Two checks on every code change

This package owns these checks. Apply them; list findings in `changes.md`.
`AGENTS.md` routes workers here instead of restating them.

- **State enumeration.** For an added/renamed state word, list and fix every switching predicate.
- **Value provenance.** For a changed value source, list every reader and confirm each remains correct.

## Verification, before the commit

Record each exit code in the workspace:

1. `dotnet build -warnaserror` (through `python tools/buildlock.py` where present).
2. Touched test projects only, filtered as briefed; never the whole suite.
3. `dotnet format --verify-no-changes`.
4. `pixi run audit-recordonce`, and `pixi run audit-docsbudget` where that task exists.

Report red checks; name any that cannot run and why.

## Delivery

- One commit unless the brief says otherwise, subject `<type>(<scope>): Capitalised description`.
  Use the brief's subject verbatim when given.
- Push to `origin <branch>`; open a draft with `gh pr create --draft`. End the body with
  `Closes #<n>` (or brief-specified `Part of #<n>`) alone on the last line.
- No AI attribution. Read back the stored body (`gh pr view --json body`) and fix appended text.
- `gh pr view` without a selector reaches your PR; another PR number is refused.

## End-of-implementation self-check

Before completion, in this same context:

- Compare `origin/main..HEAD` to the brief; name unmet acceptance.
- Confirm final HEAD is pushed and a draft PR exists at that head.
- Re-read the stored PR body against the final commit. Treat `changes.md` as an as-of handoff:
  its local HEAD/time describe what was known when it was written; Baton's post-exit
  observation, not a later rewrite of that handoff, records final push and PR facts.
- Confirm required checks ran and recorded exits are true.

This is result validation, not an independent review. Uncertain GitHub effects remain obligations;
issuing a command does not prove its intended state.

## changes.md

Write the review handoff to `$BATON_OUTPUT_DIR/changes.md`:

- State local HEAD and observation time. Preserve this before commit/push/PR; report later facts
  through the branch and PR, not here.
- Per finding/slice: files, change, reason.
- Verification commands and exit codes.
- What was NOT done and why; only the operator scales down.
- For changed protected paths (spec/baton.md §11 C-15), add `## PROTECTED` and explain why
  operator merge is required.

## Public repository

- Never name the two inspiration products anywhere.
- No legal analysis: licensing, patent, and trademark questions are the operator's.
