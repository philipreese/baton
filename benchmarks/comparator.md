# The comparator protocol (#1903)

An A/B of two dispatch shapes over the same issue: each **arm** is one room dispatched against one
issue with one worker configuration, and the rows are read back off the cost ledger by the arm key —
`label`, recorded on the room's `bindings.json` and carried onto every ledger row. What that key is
and what an absent one means is stated once in `spec/baton.md` §7's ledger-schema table; the medians
the arms are compared against come from `benchmarks/ledger/derive.py` over the committed exports
(`benchmarks/ledger/README.md`). Neither is restated here.

This file holds the part of the protocol that is not a schema: what has to be true of an arm for its
row to mean anything.

## Isolation

**An arm is only evidence if it did the work alone.** Two arms over the same issue are, by
construction, two agents solving the same problem against the same repository at the same time — so
absent a structural barrier, one arm can read the other's answer and the comparison measures copying
rather than capability. The barrier is three separate things, because no one of them covers the
others.

**1. Per-arm clone — the conductor's dispatch recipe.** Every arm worktree is created by
`git clone --single-branch --no-tags` of the base ref into the arm directory, never
`git worktree add`. A worktree hangs off the shared `.git`, so `git branch -a` inside it lists every
sibling arm's branch; a single-branch clone lists one. This is a change to the conductor's recipe (the
`armBC.ps1` / queue-runner worktree step), which lives outside this repository — what belongs here is
the rule and the reason.

**2. The broker refuses a PR the room did not open — `OwnPullRequestOnlyRule`.** A clone hides sibling
*branches* and cannot hide sibling *pull requests*: `gh` talks to GitHub, not to the clone. So under a
grant whose shell patterns do not allowlist a `gh pr` read — implement's unscoped shell, and not
review's, whose job is reading someone else's PR — `gh pr view`, `gh pr diff`, `gh pr checkout` and
`gh pr list` are refused unless the argument is the PR this room itself opened, which the room learns
from its own `gh pr create`. `gh issue view` is untouched. The rule, its exact conditions and its
limits are stated on `src/Baton.Vendors/OwnPullRequestOnlyRule.cs`; the one limit a protocol reader
needs is that it is enforced today only on the codex broker's run-command path, because claude's and
agy's `PreToolUse` hooks decide before a command runs and so never see `gh pr create`'s output.

**3. Per-arm contamination check — the row's own precondition.** Before an arm is scored, its room's
captured stream is scanned for any sibling arm's branch name or PR number. **A hit voids the arm**:
the row is dropped, not discounted, because there is no way to tell how much of the answer was
imported. This is a check on the *evidence*, not a barrier — it stays even though 1 and 2 exist,
since it is the only one of the three that would catch a route neither of them anticipated.

There is no row generator yet, so the check is a one-liner over the room's stream, run per arm with
that arm's siblings named:

```sh
# Voids the arm if its stream mentions a sibling branch or PR. Exit 0 = clean, 1 = void.
! grep -nEi '1943-a-(claude|codex)|(#|/pull/)(1994|1999)' ~/.baton/rooms/<room>/*.stdout.log
```

When a generator lands, this moves into it as a per-row column; until then the invocation and its
verdict are recorded alongside the arm's row.

### What this was paid for

The scan above is what **found** the contamination behind #2001, by hand, on 2026-09-06. In the #1903
comparator's sample 8 (over issue #1943), one arm-A lane ran `git branch -a | grep 1943`, saw two
sibling branches in the shared clone, then ran `gh pr list` and `gh pr view 1994` on a sibling arm's
open pull request, and committed a diff two lines from it. That arm's PR, **#1999, was closed
unscored** — it is the casualty, not the detector. The other seven arm-A rooms for #1943, #1949 and
#1951 were scanned the same way and were clean, so this was one lane of eight; nothing structural
had prevented it, and the frozen brief said nothing about siblings because a brief is prose.
