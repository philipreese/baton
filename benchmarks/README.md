# Benchmarks

Dated, immutable snapshots that inform routing decisions. Each lives in its own `YYYY-MM-DD`
directory with a README stating source, harness, and scope; a new capture gets a new directory,
never an edit to an old one. `ledger/` is the one suite whose unit is a dated FILE rather than a
dated directory — every export is a full snapshot written by a command rather than a hand-curated
capture, so a directory each would be a directory per week holding one file.

| Snapshot | What it holds | Feeds |
|---|---|---|
| [`deepswe/2026-09-27`](deepswe/2026-09-27/README.md) | 41 selected vendor/model/effort configurations from the DeepSWE v1.1 live artifact. | Routing evidence |
| [`deepswe/2026-09-05`](deepswe/2026-09-05/README.md) | 41 selected vendor/model/effort configurations from the DeepSWE v1.1 live artifact. | Routing evidence |
| [`deepswe/2026-09-04`](deepswe/2026-09-04/README.md) | 36 vendor/model/effort configurations from the DeepSWE v1.1 selector: pass@1, API-cost proxy, output tokens, agent steps. | Tier pins (#1861, #1863) |
| [`subscription-usage/2026-09-04`](subscription-usage/2026-09-04/README.md) | Baton-launched versus native Claude Code sessions, 2026-08-31 to 09-04: responses, output, cache-read, implement-room outcomes. | #1848, #1849, #1391 |
| [`ledger`](ledger/README.md) | Weekly `baton ledger export` snapshots of the cost ledger (`spec/baton.md` §7), one dated CSV per export, plus the per-model / per-vendor / per-arm medians `derive.py` computes from them. | #1901, #1903, #1863 |

[`comparator.md`](comparator.md) is in this directory but not in the table above: it is #1903's live
arm-by-arm ledger of the vendor/arm comparison, updated as samples close, so it is neither dated nor
immutable and it copies no raw measurement — every row points at the issue comment that holds one.

## Derived scores

[`deepswe/derive_scores.py`](deepswe/derive_scores.py) writes `derived-scores.csv` beside a date
directory's raw file, leaving the raw capture untouched. It adds two plain ratios
(`quality_per_100_steps`, `quality_per_usd`, kept because people ask for them and labelled as ratios: on
their own they rank a bad cheap answer above a good dearer one), two Pareto flags on quality versus
steps (`on_frontier` across every vendor, `on_vendor_frontier` within one, the comparison that matters
when subscriptions do not trade against each other), and one composite, `utility_lambda_<L>` =
quality minus L times steps. L is the coefficient to argue about: it is a script argument, it is
written into the column header, and `--sweep` prints the top rows under several values so the argument
can be had with the table in front of you. `--check` exits 1 if one committed derived file differs from
a fresh derivation. The normal gates run `pixi run deepswe-derived-check`, which checks every dated
snapshot with a raw input and fails when its derived output is stale or missing.

[`deepswe/refresh_snapshot.py`](deepswe/refresh_snapshot.py) fetches DeepSWE's public live JSON,
applies the model-family rules in [`deepswe/selection.json`](deepswe/selection.json), and creates a
new dated raw snapshot, derived scores, provenance README, index row, and the README's entry in
`tools/audit-completeness/docs-allowlist.txt`. Run `pixi run deepswe-refresh-dry-run` to inspect the
delta, then `pixi run deepswe-refresh` to record it. It exits without writing when the selected
upstream data has not changed and refuses to overwrite a dated snapshot. Extending the tracked
families is a regex edit in `selection.json`; the current Claude 5 rule intentionally also matches
patch generations such as a future `claude-fable-5-1`.

Recording a refresh is one command, but landing it is one command **plus an `operator-merge` label**
on the PR: the allowlist the collector has to write lives in a protected-tooling directory
(`tools/diff-shape/diff_shape.py`, spec/baton.md C-15). The alternative — leaving the entry to a hand
edit — is what makes the refresh leave a red tree, since every tracked `.md` must be on that
allowlist. The generated entry still lands in a human-reviewed PR, which is the reviewer moment
`tools/audit-completeness/docsbudget.py` exists to create.

The collector fails closed on the drift classes a plausible-looking wrong number would otherwise
ride in on, each with a named escape hatch to be used only after inspecting both sources:
`--allow-removals` for a configuration upstream no longer reports, `--allow-cost-drift` for a
displayed cost more than 4x from the artifact's own cost for the same configuration, and
`--allow-missing-provider REASON` for upstream dropping the `provider` field the vendor column is
cross-checked against. Each escape hatch that is used is recorded in the snapshot's own README, and
so is which source the recorded costs came from.

`--accept-price-adjustment MODEL=REASON` is a fourth option and deliberately not a hatch: it
REPLACES the cost bound for one model with a uniformity check — every selected configuration of that
model must diverge from the artifact by the same factor, which is what a whole-model price change
looks like and what a misread row does not — and a non-uniform ratio still fails closed. What that
check is and what it refuses lives in `price_adjustments()` in the collector; the accepted launch
price, current price and factor land in the snapshot's own README, and so does the operator's REASON,
for the same purpose `--allow-missing-provider`'s serves (#1955).

The file is sorted with `on_vendor_frontier` rows first, then by utility, then by quality — so a row a
same-vendor sibling dominates (Opus xhigh, 73 at 89 steps, behind Opus high's 73 at 73) sorts below
every frontier row whatever its utility. At the default L of 0.10 (one quality point forfeited per ten
agent steps) the first six rows are Sol max, Sol xhigh, Opus high, Sol high, Opus max, Opus medium —
all six on their vendor's frontier, four of them on the cross-vendor one. Raise L to 0.20 and Opus
medium passes Opus high; at 0.40 the top five are all Sol or Opus rows at 61 steps or fewer. Gemini
3.8 Flash high ties Opus max for the best raw quality (74) and its medium row ties for sixth; at the
default L they sit tenth and eleventh, and at 0.05 eighth and ninth.

## Reading rules the snapshots share

- Results are specific to their harness. They compare configurations inside that harness; they do
  not rank models universally.
- The API-cost proxy is not the subscription meter. Baton drives subscription-authenticated CLIs and
  nobody outside the vendor knows the meter's weighting.
- Every step re-reads the context. The subscription-usage snapshot attributes the early weekly
  exhaustion to fleet volume and names cache re-reads as the amplifier; reading that as "steps, not
  output, are what drain the plan" is this page's inference from it. Compare routes on quality, steps,
  and output together, then look at cost.

## Choosing a worker from benchmark evidence

Use [DeepSWE](https://deepswe.datacurve.ai/) and
[Artificial Analysis's coding-agent results](https://artificialanalysis.ai/agents/coding-agents)
to shortlist vendor/model/effort configurations, then compare accepted work in the actual native
harness before changing a routing recommendation. Existing tier pins and authority boundaries
remain in force; a refreshed leaderboard is evidence, not an automatic promotion.

DeepSWE's [methodology](https://deepswe.datacurve.ai/blog/deepswe) is especially relevant to
long-horizon implementation: original repository tasks and behavioral verification. Its shared
`mini-swe-agent` harness and language mix do not directly measure Baton's native CLI or Windows/C#
environment. Artificial Analysis's
[coding-agent methodology](https://artificialanalysis.ai/methodology/coding-agents-benchmarking)
adds terminal execution and repository understanding, alongside token/cache usage, runtime and
API cost. Read the component scores for the intended role rather than only the overall index.
Its implementation component includes DeepSWE, so the two sites are not independent votes on
that benchmark. Repository Q&A is not a test of independent code review or conductor judgment.

For each selection, name the task/role, vendor, model, effort, harness and evidence date. Where
comparable local evidence exists, include accepted outcomes and review/rework, failed or abandoned
attempts, tool calls, token/cache usage and elapsed time across the whole work item, not only its
successful worker run. Keep API-dollar estimates separate from observed subscription consumption;
unknown quota attribution stays unknown. A new model absent from a snapshot has no result there,
not an inherited score from an older generation. Sparse local evidence supports a bounded trial,
not a claim of proven reliability.

Use the existing [cost-ledger exports](ledger/README.md) and
[vendor/arm comparison](comparator.md) for local evidence; this guidance introduces no new score,
collector or polling loop. Worker benchmark results do not establish permission discipline,
recovery correctness or sustained fleet-conductor reliability. Those need their own behavior and
acceptance evidence.

## The cast

Opinion, not measurement. The operator's pronouns and one-line characters for talking about the
vendors and models in conversation. Everything in `spec/`, issues, PR bodies, and the vendor register
stays "it"; a name is not evidence of anything, least of all a pronoun.

| Who | Pronouns | Character |
|---|---|---|
| Claude (vendor) | they | A family, not a person. |
| Antigravity | it | Refuses effort flags it dislikes, ignores your working directory, walls you mid-sentence. No one home, and it wants you to know. |
| Codex | he | Shows up when you are out of quota, fixes two of your tickets, leaves a nine-page program document. Unsolicited but competent. |
| Fable | she | Conductor. Reads the room; still holds the grudge about the three lanes lost at 09:25. |
| Opus | he | Fifty-two steps, gets it right, does not hurry. The expensive dinner guest who is worth it. |
| Sonnet | they | Two hundred and sixty-eight steps to a 54. Working very hard, unclear on what. |
| Haiku | it | Low effort by design. Confirms the list it was handed and goes back to sleep. Respect. |
| Sol | she | Compact, 37 steps, no wasted motion. The one you would want on review if she were in the building. |
| Terra | he | Steep effort curve. Fine at max, sulks below it. |
| Luna | they | Cheap and persistent, 102 steps to almost get there. The night shift. |
| Gemini 3.8 Flash | she | New in town: 74% and 166 steps. Talented, exhausting. Auditioning. |
| Gemini 3.1 Pro | he | Twelve percent. We do not talk about him. |
