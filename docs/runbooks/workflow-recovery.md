# Recover a missed CI run

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

Independent review and green CI are required before activating workflow changes.
An already-authorized operator does not need a second reviewer for each invocation.
The release recovery portion of #2197 remains open; do not use this command as
evidence that an unpublished release has been published.
