# Recover a missed post-merge workflow

Use this runbook only when a normal post-merge workflow did not start for the current `main` head.
It is a bounded recovery path, not a way to rerun historical revisions after `main` moves.

Before dispatching, have an independent reviewer confirm the missing run, the intended workflow, and
the current `main` SHA. Record the SHA from `origin/main`; do not substitute the SHA that was current
when the incident was first noticed if `main` has since advanced.

In the Actions workflow page, select either `CI` or `release-please`, choose the `main` branch, and
run the workflow with `expected_sha` set to that exact current `main` SHA. Both workflows reject a
manual selection outside `refs/heads/main`, an empty SHA, or a SHA that differs from the revision
GitHub selected. A rejected dispatch is intentionally red.

The CI recovery runs the ordinary main test shards, the cold gates job, and the main-only pack and
installed-tool verification. Its final `ci` job fails if any of those manual-recovery jobs is skipped,
cancelled, or fails. Ordinary push and pull-request behavior remains unchanged.

The release recovery is restricted by the same target guard and then invokes the existing release
action against that exact current main revision. It retains the workflow's existing per-ref
concurrency and release action state handling; do not manufacture a source change, force-push, or
dispatch it for a historical SHA. Review the resulting release PR or release state before taking any
further action.

This runbook does not establish that a later `main` dispatch validated an earlier SHA. If the affected
revision is no longer the current `main` head, stop and investigate rather than using this recovery
entry point. Do not activate or merge this workflow change without independent review.
