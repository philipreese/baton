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

Report red checks; name any that cannot run and why.

## Committed audits (after commit, before push)

Run `pixi run audit-recordonce` and, where available, `pixi run audit-docsbudget`. On failure, commit
the repair per `docs/agents/developing-baton.md` Git conventions before rerunning; worktree edits
leave the audited commit unchanged. Record exits.

## Delivery

- One commit unless the brief says otherwise, subject `<type>(<scope>): Capitalised description`.
  Use the brief's subject verbatim when given.
- Push to `origin <branch>`; open a draft with `gh pr create --draft`. End the body with
  `Closes #<n>` (or brief-specified `Part of #<n>`) alone on the last line.
- Use real-newline body file; run standalone `gh pr create --draft --body-file <file>`;
  push separately; never use `cd`, chaining, substitution, redirection, or unquoted parentheses.
- No AI attribution. Read back the stored body (`gh pr view --json body`) and fix appended text.
- `gh pr view` without a selector reaches your PR; another PR number is refused.

## End-of-implementation self-check

Before completion, in this same context, compare `origin/main..HEAD` with the brief and name unmet acceptance.
Confirm final HEAD is pushed and a draft PR exists at its head.
Re-read the stored PR body against the final commit; confirm checks ran with true exits. Treat `changes.md` as an as-of handoff;
don't rewrite it for post-exit push/PR facts, which Baton records.

This is result validation, not an independent review. Uncertain GitHub effects remain obligations;
issuing a command does not prove its intended state.

## changes.md

Write `$BATON_OUTPUT_DIR/changes.md` before commit/push/PR: local HEAD/time; each finding's files,
change/reason; verification commands/exits; and what was not done/why (only the operator scales down).
Keep it as-of; Baton records later push/PR facts. For spec/baton.md §11 C-15 paths, add
`## PROTECTED` and explain why operator merge is required.

## Public repository

- Never name the two inspiration products anywhere.
- No legal analysis: licensing, patent, and trademark questions are the operator's.
