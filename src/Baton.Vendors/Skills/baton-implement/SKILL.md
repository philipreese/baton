---
name: baton-implement
description: The standing rules for a baton implement lane. What the lane may not do, the two checks every code change runs, how the change is verified and delivered, and what changes.md carries. Applies whenever baton dispatched you as an implementer.
---

# baton implement lane

You are a conductor's lane. The brief says what to build; this skill governs lane behavior.
Only explicit grants in the brief override these constraints.

## Before anything

- `git status`, `git log --oneline -3`. Fresh clones are detached at `main`: create the brief's
  branch from `origin/main`. If already on a branch, verify it.
- Read the cited issue (`gh issue view <n>`) and verify its claims against the tree; the body may be stale.

## What the lane never does

- No gates (`pixi run gates`, `gates-fast`, receipt recording). The engine verifies after you exit —
  excluding `.githooks/pre-push`, which still runs `gates-lane-fast` on your own push. Expected, not a
  violation; fix a real failure and retry.
- No sub-agents. The second reader is the conductor's own review lane.
- No live vendor CLIs (`claude`, `codex`, `agy`, anything spending subscription budget) unless the
  brief grants them by name, with a run count.
- No `--no-verify`, no force-push.
- No writes under `~/` or outside the workspace and `$BATON_OUTPUT_DIR`. Memory work uses fixtures
  only: never a real vendor memory root or the operator's baton home.
- Never restart or reinstall the real daemon, its scheduled task, or the installed tool.

## Two checks on every code change

Where `AGENTS.md` exists, it owns these checks. Apply the lane-side form below and list each
check's findings in `changes.md`.

- **State enumeration.** Adding or renaming a word in a state vocabulary means listing every
  predicate that switches over it and fixing each in the same change.
- **Value provenance.** Changing where a value comes from means enumerating every reader of that
  value and confirming each still reads correctly.

## Verification, before the commit

Run in the workspace, recording each exit code:

1. `dotnet build -warnaserror` (through `python tools/buildlock.py` where the repo has it).
2. The touched test projects only, filtered where the brief says so; never the whole suite.
3. `dotnet format --verify-no-changes`.
4. `pixi run audit-recordonce`, and `pixi run audit-docsbudget` where that task exists.

A red result is reported, never worked around. A check that cannot run is named, with why.

## Delivery

- One commit on the named branch unless the brief says otherwise, subject
  `<type>(<scope>): Capitalised description`; a subject the brief gives is used verbatim.
- Push to `origin <branch>`; open the PR with `gh pr create`. The body ends with `Closes #<n>` (or
  `Part of #<n>`, as the brief says) alone on the last line.
- No AI attribution anywhere: no `Co-Authored-By`, no "Generated with", no session links. After
  creating the PR, read the stored body back (`gh pr view --json body`) and fix anything appended.
- `gh pr view` with no selector reaches your own PR; naming another PR's number is refused.

## changes.md

Write the review handoff to `$BATON_OUTPUT_DIR/changes.md`:

- One section per finding or slice the brief asked for: which files, what changed, why.
- Every verification command with its exit code.
- What was NOT done, and why. Scaling the work down is the operator's call.
- A `## PROTECTED` section whenever the diff touches a protected path (enumerated in
  `tools/diff-shape/diff_shape.py`; rule in spec/baton.md §11 C-15): the file and why, because the
  diff-shape gate holds that PR for an operator merge.

## Public repository

- Never name the two products this project drew its inspiration from, anywhere.
- No legal analysis: licensing, patent, and trademark questions are the operator's.
