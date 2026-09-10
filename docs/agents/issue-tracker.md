# Issue tracker: GitHub

Issues and specs for this repository live in **`philipreese/baton`**. Use the `gh` CLI; inside a
clone, `gh` can infer the repository from `git remote -v`.

## The repository's own rules come first

[`docs/agents/developing-baton.md`](developing-baton.md) § "Git conventions" is authoritative for
branches, commits, PR bodies, and the one-issue-one-PR boundary. Read it before changing tracker
state. This file covers only tracker mechanics.

## Creating an issue

Every issue gets an appropriate label and an entry on the **Baton Roadmap** board, project number
**3** in the **`philipreese`** user account. PRs are not boarded. Do not copy an old milestone into
new work: add a milestone only when the current repository configuration and the issue's scope name
one.

```sh
gh issue create --repo philipreese/baton \
  --title "fix(adapters): Capitalized description" \
  --body-file <path> --label type/bug --label layer/dispatch \
  --project "Baton Roadmap"
```

`gh issue create -p/--project` takes the board's title, not its number, and needs the `project`
OAuth scope. If authorization fails, an operator can run `gh auth refresh -s project`.

Never pass a multi-line body as an inline `--body` string. Write it to a file and pass
`--body-file`; use the same pattern for `gh pr create` and `gh issue comment`.

The repository uses `type/*`, `layer/*`, and `platform/*` labels as well as the dispatch and
triage labels in [`triage-labels.md`](triage-labels.md). Run `gh label list` before relying on any
additional label.

## Reading and updating

- **Read:** `gh issue view <number> --comments`
- **List:** `gh issue list --state open --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'`
- **Comment:** `gh issue comment <number> --body-file <path>`
- **Label:** `gh issue edit <number> --add-label "..."` / `--remove-label "..."`
- **Close:** normally let a PR body ending in `Closes #n` close the issue; use
  `gh issue close <number> --comment "..."` only for an explicit triage decision.

Before acting on an issue's claim such as "unused", "missing", or "broken", verify it against the
tree. An issue body records a claim about the repository when it was written, not current evidence.

## Pull requests as a triage surface

**PRs as a request surface: no.**

GitHub shares one number space across issues and PRs. Resolve an ambiguous `#42` with
`gh pr view 42`, then fall back to `gh issue view 42`.

## Skill vocabulary

When a skill says "publish to the issue tracker", create an issue in `philipreese/baton` with a
current label and the Baton Roadmap project entry described above. Add a milestone only when current
configuration names one.

When a skill says "fetch the relevant ticket", run `gh issue view <number> --comments`.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single issue with **child** issues as tickets. Maps and
children follow the same current project, label, and conditional-milestone rules as every issue.

- **Map:** an issue labelled `wayfinder:map`, holding the Notes / Decisions-so-far / Fog body.
- **Child ticket:** an issue linked to the map as a GitHub sub-issue. Where sub-issues are not
  enabled, add the child to a task list in the map body and put `Part of #<map>` at the top of the
  child body. Labels: `wayfinder:<type>` (`research`, `prototype`, `grilling`, or `task`).
  Once claimed, assign it to the driving developer.
- **Blocking:** GitHub's native issue dependencies. Add an edge with
  `gh api --method POST repos/philipreese/baton/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-db-id>`,
  where `<blocker-db-id>` is the numeric database id from
  `gh api repos/philipreese/baton/issues/<n> --jq .id`, not the issue number or node id.
- **Frontier query:** list the map's open children, then drop any with an open blocker or an
  assignee; first in map order wins.
- **Claim:** `gh issue edit <n> --add-assignee @me`.
- **Resolve:** comment with `--body-file`, close the child, then append a context pointer to the
  map's Decisions-so-far.
