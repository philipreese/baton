# Recover a missed workflow run

Manual recovery restores a missed invocation; it does not authorize work outside the existing
release policy. In particular, release publication remains batched once daily except for an
already-approved urgent exception. Do not create a commit solely to trigger either workflow.

## CI

An authorized operator can dispatch `CI` on `main` with `expected_sha` set to the
current remote main commit. Verify the missing run and intended revision first.
This is CI recovery only; it does not publish a release or repair release-please.

A manual-only preflight checks the live main ref before any repository checkout or code execution.
A stale SHA, non-main selection, malformed input or API failure makes that job fail.
All checkouts use the immutable event SHA. The preflight is an admission check: main may
advance after admission, but this run continues to validate only its recorded SHA.
It never establishes validation for a different revision.

Inspect the `CI recovery` result, not the ordinary `ci` job, which is skipped on
manual runs. Recovery succeeds only when test, gates and pack all succeed; a failed,
cancelled, skipped or missing result is an error. Ordinary PR/push jobs keep their
existing dependency paths; the extra aggregate runs only for manual recovery.

For example, after checking the current remote main SHA:

```powershell
gh workflow run ci.yml --ref main -f expected_sha=<verified-main-sha>
```

## Release please

Use release recovery only after confirming that the automatic `release-please` run for the current
main revision is missing and that running it is eligible under the release policy. Read the full
current main SHA from GitHub, then dispatch the workflow on `main` with that exact value:

```powershell
gh workflow run release-please.yml --ref main -f expected_sha=<verified-main-sha>
```

The manual-only admission job rejects a non-main selection, a malformed or stale SHA, and an API
failure before the release action runs. The release job checks live main again immediately before
the existing `googleapis/release-please-action` step. The same action and manifest state used by
automatic pushes own create, update, publish and no-op behavior, so repeating a recovery does not
introduce another release implementation or duplicate a release.

The upstream action targets a branch and cannot be pinned to the admitted commit. The workflow keeps
automatic and manual runs in the existing per-ref concurrency group, checks live main after the
action, and marks `Release recovery` failed if main advanced or if admission/action work failed,
was cancelled, was skipped or went missing. That failure surfaces a real race; it does not claim the
action made no change before the race was detected. Inspect the action output and current release PR,
tag and GitHub release state before retrying against the new current main SHA.

After invocation, verify the run's event SHA and `expected_sha` are the intended main revision, then
require both `release-please` and `Release recovery` to succeed. A successful no-op is valid when the
existing release metadata is already current. For the post-merge verification, recover only a
genuinely missed eligible invocation; do not fabricate a release or trigger an extra publication.

Independent review and green CI are required before activating workflow changes. An
already-authorized operator does not need a second reviewer for each invocation.
