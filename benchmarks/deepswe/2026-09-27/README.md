# DeepSWE selected-configuration snapshot — 2026-09-27

This is an immutable input snapshot for routing discussions, generated from DeepSWE's public
leaderboard artifact. The raw observations are in
[`selected-configurations.csv`](selected-configurations.csv); create a new dated directory for a
later refresh rather than editing this one.

## Provenance

- Source: [https://deepswe.datacurve.ai/artifacts/v1.1/leaderboard-live.json](https://deepswe.datacurve.ai/artifacts/v1.1/leaderboard-live.json)
- Cost source: displayed costs from [https://deepswe.datacurve.ai/](https://deepswe.datacurve.ai/), every cost except the price adjustments listed below reconciled against the artifact's own cost and required to stay within 4x of it (the artifact can retain launch-price costs after the canonical page applies announced price changes). For `gpt-5-6-luna` the bound was replaced by `--accept-price-adjustment`: every selected configuration of the model showed the same displayed/artifact ratio to within 2%, which is a whole-model price change rather than a misread row, so the page's CURRENT price is what is recorded here. Reason given for `gpt-5-6-luna`: DeepSWE artifact retains launch cost while all five canonical displayed effort costs are exactly 0.20x artifact (0.000% spread), consistent with the established whole-model price adjustment in #1955.
  - `gpt-5-6-luna` / max (`mini_swe_agent_gpt_5_6_luna_max`): launch price 3.02812, current price 0.605623, factor 0.20.
  - `gpt-5-6-luna` / xhigh (`mini_swe_agent_gpt_5_6_luna_xhigh`): launch price 1.53562, current price 0.307124, factor 0.20.
  - `gpt-5-6-luna` / high (`mini_swe_agent_gpt_5_6_luna_high`): launch price 0.777901, current price 0.15558, factor 0.20.
  - `gpt-5-6-luna` / medium (`mini_swe_agent_gpt_5_6_luna_medium`): launch price 0.21631, current price 0.043262, factor 0.20.
  - `gpt-5-6-luna` / low (`mini_swe_agent_gpt_5_6_luna_low`): launch price 0.0724062, current price 0.0144812, factor 0.20.
- Provider cross-check: recorded with `--allow-missing-provider`, which accepts a row upstream reports no `provider` for and infers its vendor from the model prefix alone. Reason given: The live artifact omits provider on Gemini, Claude 5, and GPT-5.6 rows, while gpt-6-astra rows supply openai; model prefixes identify the configured vendor and every supplied provider still must match.
- Upstream generation time: `2026-09-22T06:27:15.860279+00:00`
- Benchmark: DeepSWE v1.1, using the upstream leaderboard's shared harness/configuration data.
- Selection: 41 configurations matched the model-family rules in
  [`../selection.json`](../selection.json).
- Values retain the established Baton snapshot precision: whole percentage points, compact displayed
  output-token precision, whole steps, and cents. The source JSON remains canonical when finer
  precision is required.

## Change from the prior Baton snapshot

- Compared with `2026-09-05`.
- Added models: none.
- Removed models: none.

Individual rows may also change as DeepSWE completes attempts or adjusts cost accounting. Review the
CSV diff before using a new snapshot to change routing policy; this generator records evidence, not
the policy interpretation.
