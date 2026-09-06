# DeepSWE selected-configuration snapshot — 2026-09-05

This is an immutable input snapshot for routing discussions, generated from DeepSWE's public
leaderboard artifact. The raw observations are in
[`selected-configurations.csv`](selected-configurations.csv); create a new dated directory for a
later refresh rather than editing this one.

## Provenance

- Source: [https://deepswe.datacurve.ai/artifacts/v1.1/leaderboard-live.json](https://deepswe.datacurve.ai/artifacts/v1.1/leaderboard-live.json)
- Cost source: displayed costs from [https://deepswe.datacurve.ai/](https://deepswe.datacurve.ai/)
  (the artifact can retain launch-price costs after the canonical page applies announced price
  changes). **Not reconciled.** This snapshot was recorded by the collector before it verified a
  displayed cost against the artifact's own cost, so each cost here was taken from the page and
  written without comparison. It has not been regenerated through the reconciling path: doing so
  needs a live fetch, which the session that added the reconciliation could not make. The next
  refresh reconciles from upstream and does not inherit these values.
- Provider cross-check: recorded before a missing upstream `provider` failed the run closed, so a
  row upstream reported no provider for would have been accepted here on the model prefix alone.
- Upstream generation time: `2026-09-03T22:24:37.984682+00:00`
- Benchmark: DeepSWE v1.1, using the upstream leaderboard's shared harness/configuration data.
- Selection: 41 configurations matched the model-family rules in
  [`../selection.json`](../selection.json).
- Values retain the established Baton snapshot precision: whole percentage points, compact displayed
  output-token precision, whole steps, and cents. The source JSON remains canonical when finer
  precision is required.

## Change from the prior Baton snapshot

- Compared with `2026-09-04`.
- Added models: `gpt-6-astra`.
- Removed models: none.

Individual rows may also change as DeepSWE completes attempts or adjusts cost accounting. Review the
CSV diff before using a new snapshot to change routing policy; this generator records evidence, not
the policy interpretation.
