"""Fetch DeepSWE's live JSON and create a dated, immutable Baton snapshot.

The selected model families live in selection.json. A normal refresh is:

    python benchmarks/deepswe/refresh_snapshot.py

Use --dry-run to inspect the upstream delta without writing. The command does nothing when the
selected data is unchanged, and refuses to overwrite an existing dated snapshot.

A successful run also adds the snapshot's generated README to
tools/audit-completeness/docs-allowlist.txt, because every tracked .md must be on that allowlist or
audit-docsbudget goes red. What that costs, and why it is still the right trade, is in
benchmarks/README.md under "Derived scores".
"""

from __future__ import annotations

import argparse
import csv
import io
import json
import math
import re
import sys
import tempfile
import urllib.request
from contextlib import redirect_stderr
from datetime import date
from pathlib import Path
from typing import NamedTuple

import derive_scores

ROOT = Path(__file__).resolve().parent
SELECTION = ROOT / "selection.json"
RAW = "selected-configurations.csv"
FIELDS = (
    "vendor",
    "model",
    "effort",
    "pass_at_1_percent",
    "pass_at_1_uncertainty_percent",
    "avg_api_cost_usd",
    "output_tokens",
    "agent_steps",
)
MODEL_VENDOR_PREFIXES = {
    "claude-": "anthropic",
    "gemini-": "google",
    "gpt-": "openai",
}
# The artifact and the canonical page disagree BY DESIGN -- the page applies announced price changes
# the artifact still carries at launch prices -- so the reconciliation bound cannot be tight without
# failing every ordinary run. What it catches is the gross error class: a scrape that reads the wrong
# row, a units mix-up, or a page that starts emitting a different quantity under the same key. A
# plausible-but-wrong value inside the bound is NOT caught here; dataset scoping and the duplicate
# refusal in displayed_costs() are what address that. Past the bound the run fails closed with both
# numbers named, and --allow-cost-drift records an inspected divergence.
COST_DRIFT_FACTOR = 4.0
# A whole-model price change moves every one of that model's configurations by the same factor,
# whatever their token mix, because it is a change to the per-token price and nothing else. That is
# what --accept-price-adjustment tests for: the spread across a model's selected configurations has
# to close to within this fraction before the bound above is stood down for that model. It is a
# consistency check on the factor the operator declares, NOT a substitute for the bound's failure
# class, and the difference is worth stating in both directions:
#   - a scrape that reads the wrong row shifts one configuration and leaves the rest alone, so the
#     ratios come out non-uniform and the run still fails closed;
#   - so does a quantity error whose factor varies with token mix -- a per-token or cached-input
#     breakdown scraped in place of the aggregate does not scale with the aggregate (luna's
#     input/output token ratio runs ~48 at low against ~210 at max);
#   - but a PAGE-WIDE FLAT RESCALE -- cents for dollars, a currency switch, any constant multiple --
#     is uniform BY CONSTRUCTION and is indistinguishable here from a price change. Nothing in this
#     check catches it. What catches it is COST_DRIFT_FACTOR on every model NOT named under the
#     option, which the same rescale also moves. Two invocations defeat THAT: naming every model at
#     once, and pairing this option with --allow-cost-drift, which composes with it (see
#     price_adjustments()) and accepts the undeclared models' divergence wholesale. Both are
#     disclosed in the snapshot README's cost provenance rather than silent.
# The 2% is chosen, not measured -- it is the issue's own figure, kept because nothing measured
# justifies widening it. Measured 2026-09-06 on gpt-5-6-luna: five configurations spanning
# 150k..15.4M mean input tokens, ratio 0.20 on every one at the two decimals #1955 tabulates. At the
# three decimals #1955 also publishes, that same measurement is inconclusive: 0.014/0.072 = 0.19444
# against 0.156/0.778 = 0.20051 is a 3.1% spread, past this tolerance. So the first live
# --accept-price-adjustment gpt-5-6-luna run may fail closed with the low row as the outlier; that
# is anticipated rather than a bug, and widening this is a call to make after inspecting upstream's
# stored precision on the small-cost rows, not before. The statistic is the spread between EXTREMES
# (max/min - 1, in price_adjustments()), which gets stricter as a model gains configurations (#1955).
PRICE_ADJUSTMENT_TOLERANCE = 0.02
ALLOWLIST = Path("tools/audit-completeness/docs-allowlist.txt")


def load_selection(path: Path = SELECTION) -> dict:
    with path.open(encoding="utf-8") as f:
        value = json.load(f)
    for key in ("source_url", "display_url", "dataset", "benchmark_version", "model_patterns"):
        if key not in value:
            raise ValueError(f"selection config is missing {key!r}")
    if not value["model_patterns"]:
        raise ValueError("selection config must contain at least one model pattern")
    return value


def fetch_json(url: str) -> dict:
    request = urllib.request.Request(url, headers={"User-Agent": "baton-deepswe-snapshot/1"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def fetch_text(url: str) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": "baton-deepswe-snapshot/1"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return response.read().decode("utf-8")


CONFIG_RECORD = re.compile(r'config:"([a-zA-Z0-9_-]+)"((?:(?!config:").)*)', re.DOTALL)
# Anchored on a record separator so `adjusted_mean_cost_usd:` or `x_mean_cost_usd:` is a different
# key, not this one.
MEAN_COST = re.compile(r'(?:^|[,{\s])mean_cost_usd:([0-9.eE+-]+)')


def displayed_costs(html: str, dataset: str) -> dict[str, float]:
    """Extract the server-rendered costs after DeepSWE's current price adjustments.

    DeepSWE's artifact currently retains launch-price costs for some models while its canonical page
    serves adjusted costs in the hydration data. Keeping these inputs separate also makes a future
    upstream schema change fail loudly instead of silently recording a plausible stale price.

    Scoped to one dataset: the page's hydration payload tags each record with `source:"<dataset>"`,
    and a second dataset reusing a config id would otherwise be indistinguishable. A config id that
    appears twice within the dataset is refused rather than resolved by document order -- an
    ambiguous cost is the case that most needs to fail closed.

    Records are delimited by `config:"`, so the scoping holds only while the discriminator FOLLOWS
    the config id it belongs to, which is the order the observed payload uses. A payload that emitted
    `source:` before `config:` would put each record's discriminator inside the previous record's
    span; requiring it to sit between the config id and the cost turns that layout into no costs at
    all -- a loud failure in main() -- rather than a cost attributed to the neighbouring config. The
    live payload is unfetched (both orders are consistent with the fixture this was written from),
    and the sibling assumption has no loud failure at all: the FIRST separator-anchored
    mean_cost_usd: in a record wins, so a record that ever carries a breakdown alongside its
    aggregate is read silently as whichever comes first, with only the reconciliation bound behind it.
    """
    discriminator = f'source:"{dataset}"'
    costs: dict[str, float] = {}
    for config, segment in CONFIG_RECORD.findall(html):
        scope = segment.find(discriminator)
        if scope < 0:
            continue
        found = MEAN_COST.search(segment)
        if not found or found.start() < scope:
            continue
        value = float(found.group(1))
        if config in costs:
            raise ValueError(
                f"canonical page lists config {config!r} twice within dataset {dataset!r} "
                f"({costs[config]} then {value}); inspect the page -- if the hydration payload is "
                "legitimately duplicated, the scrape needs narrowing to a single block"
            )
        costs[config] = value
    return costs


def vendor_for(row: dict, allow_missing_provider: bool = False) -> str:
    model = str(row.get("model", ""))
    inferred = None
    for prefix, vendor in MODEL_VENDOR_PREFIXES.items():
        if model.startswith(prefix):
            inferred = vendor
            break
    if inferred is None:
        raise ValueError(f"cannot infer vendor for model {model!r}; add its prefix locally")
    provider = str(row.get("provider") or "").strip()
    if not provider:
        # Absence is not agreement: without the upstream field the cross-check cannot fire at all,
        # which is the schema drift this collector is supposed to fail closed on.
        if not allow_missing_provider:
            raise ValueError(
                f"upstream reports no provider for {model!r}, so the vendor cross-check cannot run; "
                "inspect the schema and rerun with --allow-missing-provider REASON if intentional"
            )
        return inferred
    if provider.lower() != inferred:
        raise ValueError(
            f"provider mismatch for {model!r}: upstream reports {provider.lower()!r}, "
            f"expected {inferred!r}"
        )
    return inferred


def check_displayed_cost(label: str, config: str, displayed_cost: float) -> None:
    if not math.isfinite(displayed_cost) or displayed_cost <= 0:
        raise ValueError(
            f"{label}: displayed cost for {config!r} must be finite and greater than zero; "
            f"got {displayed_cost!r}"
        )


class PriceAdjustment(NamedTuple):
    """One configuration's accepted launch-price/current-price pair, for the README's provenance."""

    model: str
    effort: str
    config: str
    artifact_cost: float
    displayed_cost: float
    ratio: float


def price_adjustments(
    payload: dict,
    patterns: list[str],
    display_cost_by_config: dict[str, float] | None,
    accepted_reasons: dict[str, str] | None,
) -> dict[tuple[str, str], PriceAdjustment]:
    """Verify that each named model's page/artifact divergence is one uniform per-model factor.

    For a model named under --accept-price-adjustment the reconciliation bound is not relaxed, it is
    REPLACED: every selected configuration of that model must show the same displayed/artifact ratio
    to within PRICE_ADJUSTMENT_TOLERANCE, whose comment states what that check discriminates and
    what it leaves to the bound. Only then is the page's current price recorded for those
    configurations. Anything else -- a non-uniform ratio, a model that matched nothing, a model with
    too few configurations to measure uniformity from -- fails closed with both numbers named.

    Fewer than two selected configurations is refused rather than passed vacuously: a single ratio
    agrees with itself no matter how wrong it is, so accepting it would make this an unbounded
    per-model cost override while the snapshot README claims a uniformity check was performed. The
    operator's path there is to leave that model undeclared and take --allow-cost-drift, which is
    why the two options compose rather than exclude one another (#1955): a declared model's rows
    never reach the drift hatch at all -- select_rows() substitutes the accepted current price for
    them -- and a declared model that fails this check fails the whole run closed from here,
    whatever --allow-cost-drift says. So a run carrying a many-configuration model with a real
    adjustment AND a single-configuration model past the bound has one correct invocation.

    Keys are (model, config id), not the config id alone: an UNDECLARED model whose row happened to
    carry a declared model's config id must still reconcile against the bound rather than inherit
    the adjustment.

    Ratios are computed from the raw upstream floats, never from the snapshot's rounded cents: at
    snapshot precision gpt-5-6-luna low is 0.01/0.07, a 0.14 ratio that no uniformity check would
    accept alongside its own model's 0.20.
    """
    if not accepted_reasons:
        return {}
    if display_cost_by_config is None:
        raise ValueError(
            "--accept-price-adjustment compares the canonical page's displayed costs against the "
            "artifact's, and this run captured no displayed costs; rerun without --source-file"
        )
    compiled = [re.compile(pattern) for pattern in patterns]
    observed: dict[str, list[PriceAdjustment]] = {model: [] for model in accepted_reasons}
    for row in payload.get("rows") or []:
        model = str(row.get("model", ""))
        if model not in observed or not any(pattern.fullmatch(model) for pattern in compiled):
            continue
        effort = str(row.get("reasoning_effort") or "")
        label = f"{model} / {effort or 'default'}"
        config = str(row.get("config", ""))
        # Diagnose a malformed artifact row as one, before the page gets blamed for having no cost
        # under the empty config id.
        if not config:
            raise ValueError(f"{label}: missing config identifier")
        if config not in display_cost_by_config:
            raise ValueError(f"{label}: canonical page has no displayed cost for {config!r}")
        artifact_cost = metric(row, "mean_cost_usd", 0)
        if artifact_cost == 0:
            raise ValueError(f"{label}: mean_cost_usd must be greater than zero")
        displayed_cost = display_cost_by_config[config]
        check_displayed_cost(label, config, displayed_cost)
        observed[model].append(
            PriceAdjustment(
                model, effort, config, artifact_cost, displayed_cost, displayed_cost / artifact_cost
            )
        )

    accepted: dict[tuple[str, str], PriceAdjustment] = {}
    for model, entries in observed.items():
        if len(entries) < 2:
            raise ValueError(
                f"--accept-price-adjustment {model!r} matched {len(entries)} selected "
                "configuration(s); uniformity cannot be measured from fewer than two, so accepting "
                "it would be an unbounded per-model cost override rather than a check -- inspect "
                "both sources and, if the divergence is intentional, leave this model undeclared "
                "and use --allow-cost-drift, which can be combined with --accept-price-adjustment "
                "for the models that do have two or more configurations"
            )
        ratios = [entry.ratio for entry in entries]
        spread = max(ratios) / min(ratios) - 1
        if spread > PRICE_ADJUSTMENT_TOLERANCE:
            detail = "; ".join(
                f"{entry.config}: artifact {entry.artifact_cost:g}, displayed "
                f"{entry.displayed_cost:g}, {entry.ratio:.2f}x"
                for entry in entries
            )
            raise ValueError(
                f"--accept-price-adjustment {model!r}: displayed/artifact ratios are not uniform "
                f"({spread:.1%} spread, past {PRICE_ADJUSTMENT_TOLERANCE:.0%}), so this is not one "
                f"price change; inspect both sources -- {detail}"
            )
        for entry in entries:
            accepted[(entry.model, entry.config)] = entry
    return accepted


def parse_price_adjustments(values: list[str] | None) -> dict[str, str]:
    """Parse each --accept-price-adjustment MODEL=REASON into model -> operator reason.

    The reason is mandatory for the same purpose --allow-missing-provider REASON's is: a later
    reader of the snapshot README can undo the arithmetic, but nothing else in the record says what
    the operator believed happened or on what evidence. Refusing a repeat of one model with a
    different reason keeps the README's single recorded reason from being the arbitrary last one.
    """
    parsed: dict[str, str] = {}
    for value in values or []:
        model, separator, reason = value.partition("=")
        model, reason = model.strip(), reason.strip()
        if not separator or not model or not reason:
            raise ValueError(
                f"--accept-price-adjustment expects MODEL=REASON; got {value!r}. The reason is "
                "recorded in the snapshot README beside the launch price, current price and factor"
            )
        if parsed.get(model, reason) != reason:
            raise ValueError(
                f"--accept-price-adjustment names {model!r} twice with different reasons; the "
                "snapshot README records one reason per model"
            )
        parsed[model] = reason
    return parsed


def reconcile_cost(
    label: str, config: str, artifact_cost: float, displayed_cost: float, allow_cost_drift: bool
) -> float:
    """Verify a scraped displayed cost against the artifact's own cost; never substitute it blind."""
    check_displayed_cost(label, config, displayed_cost)
    ratio = displayed_cost / artifact_cost
    if 1 / COST_DRIFT_FACTOR <= ratio <= COST_DRIFT_FACTOR:
        return displayed_cost
    divergence = (
        f"{label}: displayed cost {displayed_cost} for {config!r} diverges from the artifact cost "
        f"{artifact_cost} by {ratio:.2f}x, past the {COST_DRIFT_FACTOR:g}x reconciliation bound"
    )
    if not allow_cost_drift:
        raise ValueError(divergence + "; inspect both sources and rerun with --allow-cost-drift if intentional")
    print(f"  accepted cost drift: {divergence}")
    return displayed_cost


def display_model(source_model: str) -> str:
    """Keep Baton's established dotted release-number spelling for upstream machine slugs."""
    if source_model.startswith(("gpt-", "gemini-")):
        displayed = re.sub(r"^(gpt|gemini)-(\d+)-(\d+)(?=-|$)", r"\1-\2.\3", source_model)
        # The first hand-copied snapshot omitted Google's transient "preview" channel suffix.
        return displayed.removesuffix("-preview")
    if source_model.startswith("claude-"):
        return re.sub(r"^(claude-[a-z]+-\d+)-(\d+)$", r"\1.\2", source_model)
    return source_model


def rounded_int(value: float) -> int:
    # Decimal-style half-up is less surprising for displayed benchmark aggregates than bankers'
    # rounding, though real leaderboard values almost never land exactly on .5.
    return int(float(value) + 0.5)


def displayed_output_tokens(value: float) -> int:
    """Match the source UI's compact precision, expanded back to an integer token count."""
    quantum = 100 if value < 10_000 else 1_000
    return rounded_int(value / quantum) * quantum


def metric(row: dict, name: str, minimum: float, maximum: float | None = None) -> float:
    try:
        value = float(row[name])
    except (KeyError, TypeError, ValueError) as error:
        raise ValueError(f"{name} is missing or not numeric") from error
    if not math.isfinite(value) or value < minimum or (maximum is not None and value > maximum):
        bounds = f"{minimum}..{maximum}" if maximum is not None else f">= {minimum}"
        raise ValueError(f"{name} must be finite and {bounds}; got {value!r}")
    return value


def select_rows(
    payload: dict,
    patterns: list[str],
    display_cost_by_config: dict[str, float] | None = None,
    allow_cost_drift: bool = False,
    missing_provider_reason: str | None = None,
    price_adjustment_by_row: dict[tuple[str, str], PriceAdjustment] | None = None,
) -> list[dict[str, str]]:
    compiled = [re.compile(pattern) for pattern in patterns]
    source_rows = payload.get("rows")
    if not isinstance(source_rows, list):
        raise ValueError("leaderboard JSON has no rows array")

    selected = []
    keys = set()
    for row in source_rows:
        model = str(row.get("model", ""))
        if not any(pattern.fullmatch(model) for pattern in compiled):
            continue
        effort = str(row.get("reasoning_effort") or "")
        key = (model, effort)
        if key in keys:
            raise ValueError(f"duplicate selected configuration: {model} / {effort or '(default)'}")
        keys.add(key)
        config = str(row.get("config", ""))
        if display_cost_by_config is not None and not config:
            raise ValueError(f"{model} / {effort}: missing config identifier")
        if display_cost_by_config is not None and config not in display_cost_by_config:
            raise ValueError(f"{model} / {effort}: canonical page has no displayed cost for {config!r}")
        pass_at_1 = metric(row, "pass_at_1", 0, 1)
        uncertainty = metric(row, "ci_half", 0, 1)
        artifact_cost = metric(row, "mean_cost_usd", 0)
        output_tokens = metric(row, "mean_output_tokens", 0)
        steps = metric(row, "mean_agent_steps", 0)
        if artifact_cost == 0:
            raise ValueError(f"{model} / {effort}: mean_cost_usd must be greater than zero")
        if output_tokens == 0:
            raise ValueError(f"{model} / {effort}: mean_output_tokens must be greater than zero")
        if steps == 0:
            raise ValueError(f"{model} / {effort}: mean_agent_steps must be greater than zero")
        cost = artifact_cost
        if display_cost_by_config is not None:
            # A verified uniform per-model price adjustment stands in for the bound on that model's
            # rows; every other row still has to reconcile. Keyed on the model as well as the config
            # id, so an undeclared model's row sharing a config id cannot inherit the adjustment and
            # skip the bound.
            adjustment = (price_adjustment_by_row or {}).get((model, config))
            if adjustment is not None:
                cost = adjustment.displayed_cost
            else:
                cost = reconcile_cost(
                    f"{model} / {effort or 'default'}",
                    config,
                    artifact_cost,
                    display_cost_by_config[config],
                    allow_cost_drift,
                )
        displayed_cost = f"{cost:.2f}"
        displayed_tokens = displayed_output_tokens(output_tokens)
        displayed_steps = rounded_int(steps)
        if displayed_cost == "0.00" or displayed_tokens == 0 or displayed_steps == 0:
            raise ValueError(
                f"{model} / {effort}: cost, output tokens, and steps must remain positive "
                "at snapshot precision"
            )
        selected.append(
            {
                "vendor": vendor_for(row, missing_provider_reason is not None),
                "model": display_model(model),
                "effort": effort,
                "pass_at_1_percent": str(rounded_int(pass_at_1 * 100)),
                "pass_at_1_uncertainty_percent": str(rounded_int(uncertainty * 100)),
                "avg_api_cost_usd": displayed_cost,
                "output_tokens": str(displayed_tokens),
                "agent_steps": str(displayed_steps),
            }
        )
    if not selected:
        raise ValueError("the selection patterns matched no leaderboard rows")
    selected.sort(key=lambda row: (-int(row["pass_at_1_percent"]), row["model"], row["effort"]))
    return selected


def csv_text(rows: list[dict[str, str]]) -> str:
    output = io.StringIO()
    writer = csv.DictWriter(output, fieldnames=FIELDS, lineterminator="\n")
    writer.writeheader()
    writer.writerows(rows)
    return output.getvalue()


def latest_snapshot(root: Path) -> Path | None:
    snapshots = sorted(path for path in root.iterdir() if path.is_dir() and (path / RAW).is_file())
    return snapshots[-1] if snapshots else None


def model_set(rows: list[dict[str, str]]) -> set[str]:
    return {row["model"] for row in rows}


def read_rows(path: Path) -> list[dict[str, str]]:
    with path.open(encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f))


def configuration_key(row: dict[str, str]) -> tuple[str, str]:
    return row["model"], row["effort"]


def delta_text(
    previous: list[dict[str, str]], current: list[dict[str, str]]
) -> tuple[str, list[str], list[str], list[str]]:
    old_models, new_models = model_set(previous), model_set(current)
    added, removed = sorted(new_models - old_models), sorted(old_models - new_models)
    old_by_key = {configuration_key(row): row for row in previous}
    new_keys = {configuration_key(row) for row in current}
    removed_configs = sorted(
        f"{model}/{effort or 'default'}" for model, effort in old_by_key.keys() - new_keys
    )
    changed = sum(old_by_key.get(configuration_key(row)) != row for row in current)
    parts = [f"{len(current)} configurations", f"{changed} new or changed"]
    if added:
        parts.append("added models: " + ", ".join(added))
    if removed:
        parts.append("removed models: " + ", ".join(removed))
    if removed_configs:
        parts.append("removed configurations: " + ", ".join(removed_configs))
    return "; ".join(parts), added, removed, removed_configs


def cost_provenance(
    display_url: str,
    used_displayed_costs: bool,
    allow_cost_drift: bool,
    price_adjustment_by_row: dict[tuple[str, str], PriceAdjustment] | None = None,
    price_adjustment_reasons: dict[str, str] | None = None,
) -> str:
    """State where the recorded costs actually came from.

    A value of the cost source, not a flag next to it: a snapshot built from artifact costs must not
    be able to claim canonical-page provenance -- and once some rows bypass the bound, the sentence
    claiming every cost was held inside it stops being true, so it is rewritten rather than annotated.
    """
    if not used_displayed_costs:
        return (
            "- Cost source: the leaderboard artifact's own `mean_cost_usd`. The canonical page was "
            "NOT consulted for this snapshot, so a cost the page has since adjusted is recorded here "
            "at its launch price."
        )
    adjustments = price_adjustment_by_row or {}
    scope = "each" if not adjustments else "every cost except the price adjustments listed below"
    line = (
        f"- Cost source: displayed costs from [{display_url}]({display_url}), {scope} reconciled "
        f"against the artifact's own cost and required to stay within {COST_DRIFT_FACTOR:g}x of it "
        "(the artifact can retain launch-price costs after the canonical page applies announced "
        "price changes)."
    )
    if allow_cost_drift:
        line += " Divergences past that bound were accepted for this run via `--allow-cost-drift`."
    if not adjustments:
        return line
    adjusted_models = list(dict.fromkeys(a.model for a in adjustments.values()))
    models = ", ".join(f"`{name}`" for name in adjusted_models)
    line += (
        f" For {models} the bound was replaced by `--accept-price-adjustment`: every selected "
        "configuration of the model showed the same displayed/artifact ratio to within "
        f"{PRICE_ADJUSTMENT_TOLERANCE:.0%}, which is a whole-model price change rather than a "
        "misread row, so the page's CURRENT price is what is recorded here."
    )
    # The factor says what was done; only the operator's reason says why it was believed to be a
    # price change, which is what a later reader of this snapshot cannot reconstruct from the
    # numbers. Same shape as provider_provenance()'s.
    for name in adjusted_models:
        reason = (price_adjustment_reasons or {}).get(name)
        if reason:
            line += f" Reason given for `{name}`: {reason}"
    for adjustment in adjustments.values():
        line += (
            f"\n  - `{adjustment.model}` / {adjustment.effort or 'default'} "
            f"(`{adjustment.config}`): launch price {adjustment.artifact_cost:g}, current price "
            f"{adjustment.displayed_cost:g}, factor {adjustment.ratio:.2f}."
        )
    return line


def provider_provenance(missing_provider_reason: str | None) -> str:
    if missing_provider_reason is None:
        return (
            "- Provider cross-check: every selected row carried an upstream `provider` agreeing with "
            "the model prefix; a missing one fails the run closed."
        )
    return (
        "- Provider cross-check: recorded with `--allow-missing-provider`, which accepts a row "
        "upstream reports no `provider` for and infers its vendor from the model prefix alone. "
        f"Reason given: {missing_provider_reason}"
    )


def snapshot_readme(
    snapshot_date: str,
    source_url: str,
    cost_source: str,
    provider_source: str,
    version: str,
    generated_at: str,
    rows: list[dict[str, str]],
    prior_name: str | None,
    added: list[str],
    removed: list[str],
) -> str:
    change_lines = []
    if prior_name:
        change_lines.append(f"- Compared with `{prior_name}`.")
    change_lines.append("- Added models: " + (", ".join(f"`{name}`" for name in added) if added else "none" ) + ".")
    change_lines.append("- Removed models: " + (", ".join(f"`{name}`" for name in removed) if removed else "none") + ".")
    return f"""# DeepSWE selected-configuration snapshot — {snapshot_date}

This is an immutable input snapshot for routing discussions, generated from DeepSWE's public
leaderboard artifact. The raw observations are in
[`selected-configurations.csv`](selected-configurations.csv); create a new dated directory for a
later refresh rather than editing this one.

## Provenance

- Source: [{source_url}]({source_url})
{cost_source}
{provider_source}
- Upstream generation time: `{generated_at or 'not reported'}`
- Benchmark: DeepSWE {version}, using the upstream leaderboard's shared harness/configuration data.
- Selection: {len(rows)} configurations matched the model-family rules in
  [`../selection.json`](../selection.json).
- Values retain the established Baton snapshot precision: whole percentage points, compact displayed
  output-token precision, whole steps, and cents. The source JSON remains canonical when finer
  precision is required.

## Change from the prior Baton snapshot

{chr(10).join(change_lines)}

Individual rows may also change as DeepSWE completes attempts or adjusts cost accounting. Review the
CSV diff before using a new snapshot to change routing policy; this generator records evidence, not
the policy interpretation.
"""


def update_index(index_path: Path, snapshot_date: str, count: int, version: str) -> None:
    """Publish one index row per dated snapshot, idempotent on the row's KEY.

    Index rows get hand-curated after publication (the "Feeds" column names the issues a snapshot
    fed), so matching the whole generated row string would append a second row for a date that
    already has a curated one.
    """
    text = index_path.read_text(encoding="utf-8")
    key = f"| [`deepswe/{snapshot_date}`](deepswe/{snapshot_date}/README.md) |"
    row = (
        f"{key} {count} selected vendor/model/effort configurations from the DeepSWE "
        f"{version} live artifact. | Routing evidence |"
    )
    if key in text:
        return
    marker = "|---|---|---|\n"
    if marker not in text:
        raise ValueError(f"cannot find benchmark index table in {index_path}")
    index_path.write_text(text.replace(marker, marker + row + "\n", 1), encoding="utf-8", newline="\n")


def update_docs_allowlist(allowlist_path: Path, readme_path: Path) -> None:
    """Add a generated snapshot README to the tracked-markdown allowlist, in sorted position.

    Every tracked .md must be on tools/audit-completeness/docs-allowlist.txt or audit-docsbudget
    fails, so a refresh that emits a README and stops there leaves a red tree. The entry is the
    README's path relative to the repository root, which is the allowlist's own grandparent
    directory. The file's existing line terminator is preserved so the diff stays one line.
    """
    repo_root = allowlist_path.resolve().parents[2]
    entry = readme_path.resolve().relative_to(repo_root).as_posix()
    # newline="" both ways: read_text's universal-newline translation would hide a CRLF checkout
    # (the file is LF in the index, CRLF in a Windows worktree) and rewrite all 60 lines.
    with allowlist_path.open(encoding="utf-8", newline="") as f:
        raw = f.read()
    terminator = "\r\n" if "\r\n" in raw else "\n"
    entries = [line.strip() for line in raw.splitlines() if line.strip()]
    if entry in entries:
        return
    position = next((i for i, existing in enumerate(entries) if existing > entry), len(entries))
    entries.insert(position, entry)
    allowlist_path.write_text(terminator.join(entries) + terminator, encoding="utf-8", newline="")


def create_snapshot(
    payload: dict,
    selection: dict,
    root: Path,
    snapshot_date: str,
    dry_run: bool,
    index_path: Path | None,
    display_cost_by_config: dict[str, float] | None = None,
    allow_removals: bool = False,
    allowlist_path: Path | None = None,
    allow_cost_drift: bool = False,
    missing_provider_reason: str | None = None,
    accept_price_adjustment: dict[str, str] | None = None,
) -> int:
    adjustments = price_adjustments(
        payload, selection["model_patterns"], display_cost_by_config, accept_price_adjustment
    )
    rows = select_rows(
        payload,
        selection["model_patterns"],
        display_cost_by_config,
        allow_cost_drift,
        missing_provider_reason,
        adjustments,
    )
    raw = csv_text(rows)
    previous_path = latest_snapshot(root)
    previous = read_rows(previous_path / RAW) if previous_path else []
    summary, added, removed, removed_configs = delta_text(previous, rows)
    print(f"DeepSWE {snapshot_date}: {summary}")
    # Evidence import must fail closed: a transiently incomplete upstream response is otherwise
    # indistinguishable from an intentional removal and would become a plausible immutable record.
    if removed_configs and not allow_removals:
        raise ValueError(
            "upstream removed selected configurations; inspect the delta and rerun with "
            "--allow-removals if intentional: " + ", ".join(removed_configs)
        )
    if previous_path and csv_text(previous) == raw:
        if not dry_run:
            # Repair publication for the snapshot that is still current, so an interrupted earlier
            # run cannot leave the index or the docs allowlist permanently missing its entry.
            if allowlist_path:
                update_docs_allowlist(allowlist_path, previous_path / "README.md")
            if index_path:
                update_index(index_path, previous_path.name, len(rows), selection["benchmark_version"])
        print(f"No selected data changed since {previous_path.name}; no snapshot created.")
        return 0
    if dry_run:
        print("Dry run; no files written.")
        return 0

    target = root / snapshot_date
    if target.exists():
        raise FileExistsError(f"refusing to overwrite immutable snapshot {target}")
    with tempfile.TemporaryDirectory(prefix=f".{snapshot_date}-", dir=root) as temp:
        staging = Path(temp) / snapshot_date
        staging.mkdir()
        (staging / RAW).write_text(raw, encoding="utf-8", newline="\n")
        (staging / "README.md").write_text(
            snapshot_readme(
                snapshot_date,
                selection["source_url"],
                cost_provenance(
                    selection["display_url"],
                    display_cost_by_config is not None,
                    allow_cost_drift,
                    adjustments,
                    accept_price_adjustment,
                ),
                provider_provenance(missing_provider_reason),
                selection["benchmark_version"],
                str(payload.get("generated_at", "")),
                rows,
                previous_path.name if previous_path else None,
                added,
                removed,
            ),
            encoding="utf-8",
            newline="\n",
        )
        derive_scores.write_or_check(staging, derive_scores.DEFAULT_LAMBDA, check=False)
        staging.replace(target)
    # Publication follows the snapshot landing, never precedes it: a missing allowlist entry is a
    # loud docsbudget failure, while an entry naming a directory that was never written is silent.
    if allowlist_path:
        update_docs_allowlist(allowlist_path, target / "README.md")
    if index_path:
        update_index(index_path, snapshot_date, len(rows), selection["benchmark_version"])
    print(f"Created immutable snapshot {target}")
    return 0


def selftest() -> int:
    fixture = {
        "generated_at": "2026-09-05T00:00:00Z",
        "rows": [
            {
                "model": "gpt-6-astra", "provider": "openai", "reasoning_effort": "high",
                "config": "mini_swe_agent_gpt_6_astra_high",
                "pass_at_1": 0.741, "ci_half": 0.029, "mean_cost_usd": 6.523,
                "mean_output_tokens": 29557.3, "mean_agent_steps": 28.75,
            },
            {
                "model": "claude-fable-5-1", "provider": "anthropic", "reasoning_effort": "max",
                "config": "mini_swe_agent_claude_fable_5_1_max", "pass_at_1": 0.70,
                "ci_half": 0.04, "mean_cost_usd": 10, "mean_output_tokens": 50001,
                "mean_agent_steps": 60.49,
            },
            {
                "model": "unselected-model", "reasoning_effort": "high", "pass_at_1": 1,
                "ci_half": 0, "mean_cost_usd": 1, "mean_output_tokens": 1, "mean_agent_steps": 1,
            },
        ],
    }
    selection = load_selection()
    rows = select_rows(fixture, selection["model_patterns"])
    assert [row["model"] for row in rows] == ["gpt-6-astra", "claude-fable-5.1"]
    assert rows[0]["pass_at_1_percent"] == "74"
    assert rows[0]["output_tokens"] == "30000"
    assert rows[1]["vendor"] == "anthropic"
    assert displayed_output_tokens(8_179.57) == 8_200
    dataset = selection["dataset"]
    costs = displayed_costs(
        '<script>config:"mini_swe_agent_gpt_6_astra_high",source:"deep-swe",'
        'mean_cost_usd:5.72,mean_output_tokens:29557.3,'
        'config:"mini_swe_agent_gpt_6_astra_high",source:"other-benchmark",'
        'mean_cost_usd:99.0</script>',
        dataset,
    )
    # Scoped: the same config id under another dataset is not this benchmark's cost, and the
    # unscoped version of this scrape returned 99.0 for it.
    assert costs == {"mini_swe_agent_gpt_6_astra_high": 5.72}
    try:
        displayed_costs(
            '<script>config:"c",source:"deep-swe",mean_cost_usd:1.0,'
            'config:"c",source:"deep-swe",mean_cost_usd:2.0</script>',
            dataset,
        )
    except ValueError as error:
        assert "twice within dataset" in str(error)
    else:
        raise AssertionError("a duplicate config id was resolved by document order")
    assert displayed_costs(
        '<script>config:"c",source:"deep-swe",adjusted_mean_cost_usd:1.0</script>', dataset
    ) == {}
    # The record layout the scoping depends on: with the discriminator ahead of its own config id
    # this yields nothing (main() then fails closed) instead of billing one dataset's cost to the
    # neighbouring config -- which is what it did before the ordering requirement.
    assert displayed_costs(
        '<script>source:"other-benchmark",config:"c",mean_cost_usd:99.0,'
        'source:"deep-swe",config:"d",mean_cost_usd:1.0</script>',
        dataset,
    ) == {}
    try:
        select_rows(fixture, selection["model_patterns"], costs)
    except ValueError as error:
        assert "canonical page has no displayed cost" in str(error)
    else:
        raise AssertionError("a missing canonical displayed cost was accepted")
    page_costs = {
        "mini_swe_agent_gpt_6_astra_high": 5.72,
        "mini_swe_agent_claude_fable_5_1_max": 9.61,
    }
    reconciled = select_rows(fixture, selection["model_patterns"], page_costs)
    # The control: within the bound the displayed cost is what lands, not the artifact's 6.52.
    assert reconciled[0]["avg_api_cost_usd"] == "5.72"
    drifted = {**page_costs, "mini_swe_agent_gpt_6_astra_high": 0.5}
    try:
        select_rows(fixture, selection["model_patterns"], drifted)
    except ValueError as error:
        message = str(error)
        assert "mini_swe_agent_gpt_6_astra_high" in message
        assert "0.5" in message and "6.523" in message
        assert "reconciliation bound" in message
    else:
        raise AssertionError("an unreconciled displayed cost was substituted for the artifact cost")
    accepted = select_rows(fixture, selection["model_patterns"], drifted, allow_cost_drift=True)
    assert accepted[0]["avg_api_cost_usd"] == "0.50"
    without_provider = {
        "rows": [{k: v for k, v in fixture["rows"][0].items() if k != "provider"}]
    }
    try:
        select_rows(without_provider, selection["model_patterns"])
    except ValueError as error:
        assert "no provider" in str(error)
    else:
        raise AssertionError("a missing provider silently disabled the vendor cross-check")
    allowed = select_rows(
        without_provider, selection["model_patterns"], missing_provider_reason="synthetic"
    )
    assert allowed[0]["vendor"] == "openai"
    invalid = {"rows": [{**fixture["rows"][0], "mean_agent_steps": 0}]}
    try:
        select_rows(invalid, selection["model_patterns"])
    except ValueError as error:
        assert "greater than zero" in str(error)
    else:
        raise AssertionError("zero agent steps were accepted")
    for field in ("mean_cost_usd", "mean_output_tokens"):
        invalid = {"rows": [{**fixture["rows"][0], field: 0}]}
        try:
            select_rows(invalid, selection["model_patterns"])
        except ValueError as error:
            assert "greater than zero" in str(error)
        else:
            raise AssertionError(f"zero {field} was accepted")
    below_precision = {
        "rows": [
            {
                **fixture["rows"][0],
                "mean_cost_usd": 0.001,
                "mean_output_tokens": 0.01,
                "mean_agent_steps": 0.01,
            }
        ]
    }
    try:
        select_rows(below_precision, selection["model_patterns"])
    except ValueError as error:
        assert "snapshot precision" in str(error)
    else:
        raise AssertionError("values that round to zero were accepted")
    wrong_vendor = {"rows": [{**fixture["rows"][0], "provider": "mystery"}]}
    try:
        select_rows(wrong_vendor, selection["model_patterns"])
    except ValueError as error:
        assert "provider mismatch" in str(error)
    else:
        raise AssertionError("a mismatched provider was accepted")

    # --accept-price-adjustment. Shaped on the measured gpt-5-6-luna divergence (#1955): the page
    # serves a fifth of the artifact's cost on every configuration of the model. Displayed costs are
    # exact products of the artifact costs, because the ratio the check reads is the raw one -- the
    # issue's own 3-decimal table rounds to a 3% spread that this check would (correctly) refuse.
    luna = {
        "generated_at": "2026-09-06T00:00:00Z",
        "rows": [
            {
                "model": "gpt-5-6-luna", "provider": "openai", "reasoning_effort": "max",
                "config": "mini_swe_agent_gpt_5_6_luna_max", "pass_at_1": 0.67, "ci_half": 0.04,
                "mean_cost_usd": 3.0281, "mean_output_tokens": 73400, "mean_agent_steps": 102,
            },
            {
                "model": "gpt-5-6-luna", "provider": "openai", "reasoning_effort": "high",
                "config": "mini_swe_agent_gpt_5_6_luna_high", "pass_at_1": 0.44, "ci_half": 0.03,
                "mean_cost_usd": 0.778, "mean_output_tokens": 25800, "mean_agent_steps": 49,
            },
        ],
    }
    luna_costs = {row["config"]: row["mean_cost_usd"] * 0.2 for row in luna["rows"]}
    luna_reason = "vendor cut the per-token price; #1955"
    luna_accepted = {"gpt-5-6-luna": luna_reason}
    # The control the acceptance is read against: a 5x divergence is past the bound, so without the
    # option these same inputs fail closed. If this arm ever stops raising, the arm below proves
    # nothing.
    try:
        select_rows(luna, selection["model_patterns"], luna_costs)
    except ValueError as error:
        assert "reconciliation bound" in str(error)
    else:
        raise AssertionError("a 5x price adjustment was accepted without --accept-price-adjustment")
    adjustments = price_adjustments(
        luna, selection["model_patterns"], luna_costs, luna_accepted
    )
    assert set(adjustments) == {("gpt-5-6-luna", config) for config in luna_costs}
    adjusted = select_rows(
        luna, selection["model_patterns"], luna_costs, price_adjustment_by_row=adjustments
    )
    # The page's current price is what lands, not the artifact's 3.03 launch price.
    assert [row["avg_api_cost_usd"] for row in adjusted] == ["0.61", "0.16"]
    note = cost_provenance("https://example.invalid/", True, False, adjustments, luna_accepted)
    assert "launch price 3.0281, current price 0.60562, factor 0.20" in note
    assert "current price 0.1556, factor 0.20" in note
    # The unadjusted sentence would still claim every cost was held inside the bound.
    assert "every cost except the price adjustments listed below reconciled" in note
    assert "`gpt-5-6-luna`" in note and "2%" in note
    # The numbers say what was done; only the operator's reason says why, so it is in the record.
    assert f"Reason given for `gpt-5-6-luna`: {luna_reason}" in note
    # An UNDECLARED model's row carrying a declared model's config id must still reconcile: the
    # adjustment map is keyed on (model, config), so this row reaches the bound and fails closed.
    # Keyed on the config id alone it would silently inherit luna's accepted 0.61.
    shared_config = {
        **luna,
        "rows": [
            *luna["rows"],
            {
                "model": "gpt-6-astra", "provider": "openai", "reasoning_effort": "high",
                "config": "mini_swe_agent_gpt_5_6_luna_max", "pass_at_1": 0.5, "ci_half": 0.02,
                "mean_cost_usd": 3.0281, "mean_output_tokens": 20000, "mean_agent_steps": 20,
            },
        ],
    }
    shared_adjustments = price_adjustments(
        shared_config, selection["model_patterns"], luna_costs, luna_accepted
    )
    try:
        select_rows(
            shared_config,
            selection["model_patterns"],
            luna_costs,
            price_adjustment_by_row=shared_adjustments,
        )
    except ValueError as error:
        message = str(error)
        assert "gpt-6-astra" in message and "reconciliation bound" in message
    else:
        raise AssertionError("an undeclared model inherited an adjustment by sharing a config id")
    # A non-uniform ratio is the misread-row case the bound was built for: still closed.
    skewed = {**luna_costs, "mini_swe_agent_gpt_5_6_luna_high": 0.778 * 0.22}
    try:
        price_adjustments(luna, selection["model_patterns"], skewed, luna_accepted)
    except ValueError as error:
        message = str(error)
        assert "not uniform" in message
        assert "0.778" in message and "0.17116" in message
        assert "0.20x" in message and "0.22x" in message
    else:
        raise AssertionError("a non-uniform per-configuration ratio was accepted as one price change")
    # One configuration agrees with itself whatever its ratio, so it is refused rather than passed.
    single = {**luna, "rows": luna["rows"][:1]}
    try:
        price_adjustments(single, selection["model_patterns"], luna_costs, luna_accepted)
    except ValueError as error:
        # The refusal has to name the invocation that does work, or the operator of a run carrying
        # both a many-configuration and a single-configuration adjustment has nowhere to go.
        assert "fewer than two" in str(error)
        assert "can be combined with --accept-price-adjustment" in str(error)
    else:
        raise AssertionError("uniformity was declared from a single configuration")
    unidentified = {**luna, "rows": [{**luna["rows"][0], "config": ""}, luna["rows"][1]]}
    try:
        price_adjustments(unidentified, selection["model_patterns"], luna_costs, luna_accepted)
    except ValueError as error:
        assert "missing config identifier" in str(error)
    else:
        raise AssertionError("an artifact row with no config identifier was matched to a page cost")
    try:
        price_adjustments(
            luna, selection["model_patterns"], luna_costs, {"gpt-5-6-sol": luna_reason}
        )
    except ValueError as error:
        assert "matched 0 selected" in str(error)
    else:
        raise AssertionError("a model that matched no selected configuration was accepted")
    try:
        price_adjustments(luna, selection["model_patterns"], None, luna_accepted)
    except ValueError as error:
        assert "captured no displayed costs" in str(error)
    else:
        raise AssertionError("a price adjustment was accepted with no displayed costs to compare")
    # Default path unchanged: no named model means no adjustment machinery runs at all.
    assert price_adjustments(luna, selection["model_patterns"], luna_costs, None) == {}
    assert price_adjustments(luna, selection["model_patterns"], None, {}) == {}
    # The reason is mandatory, and refused before anything is fetched -- this arm runs main() with
    # no --source-file, so a refusal arriving any later would be a live request.
    with redirect_stderr(io.StringIO()) as missing_reason:
        try:
            main(["--accept-price-adjustment", "gpt-5-6-luna"])
        except SystemExit as error:
            assert error.code == 2
        else:
            raise AssertionError("a price adjustment with no operator reason was accepted")
    assert "MODEL=REASON" in missing_reason.getvalue()
    assert parse_price_adjustments(["gpt-5-6-luna=cut announced"]) == {
        "gpt-5-6-luna": "cut announced"
    }
    # A reason containing '=' survives; only the first separator splits.
    assert parse_price_adjustments(["m=price: 1 = one"]) == {"m": "price: 1 = one"}
    assert parse_price_adjustments(["m=same", "m=same"]) == {"m": "same"}
    try:
        parse_price_adjustments(["m=first", "m=second"])
    except ValueError as error:
        assert "twice with different reasons" in str(error)
    else:
        raise AssertionError("one model's two conflicting reasons were silently reduced to one")
    with tempfile.TemporaryDirectory() as temp:
        adjusted_root = Path(temp) / "adjusted"
        adjusted_root.mkdir()
        # --allow-cost-drift does NOT rescue a declared model whose ratios are not uniform: this is
        # the invariant that used to be enforced by refusing the two options together, and it holds
        # at the composition point rather than at the parser.
        try:
            create_snapshot(
                luna, selection, adjusted_root, "2026-09-06", True, None, skewed, False, None,
                True, None, luna_accepted,
            )
        except ValueError as error:
            assert "not uniform" in str(error)
        else:
            raise AssertionError("--allow-cost-drift accepted a non-uniform declared adjustment")
        assert (
            create_snapshot(
                luna, selection, adjusted_root, "2026-09-06", False, None, luna_costs, False, None,
                False, None, luna_accepted,
            )
            == 0
        )
        adjusted_readme = (adjusted_root / "2026-09-06" / "README.md").read_text(encoding="utf-8")
        assert "launch price 3.0281, current price 0.60562, factor 0.20" in adjusted_readme
        assert "`--accept-price-adjustment`" in adjusted_readme
        assert f"Reason given for `gpt-5-6-luna`: {luna_reason}" in adjusted_readme
        assert "0.61" in (adjusted_root / "2026-09-06" / RAW).read_text(encoding="utf-8")
        # The case the two options used to deadlock on: one model with several configurations and a
        # real uniform adjustment, alongside a single-configuration model past the bound, which
        # cannot be declared (uniformity is unmeasurable from one ratio). Composing the options is
        # its one correct invocation, and the snapshot README records both.
        solo = {
            "model": "gemini-3-1-pro-preview", "provider": "google", "reasoning_effort": "high",
            "config": "mini_swe_agent_gemini_3_1_pro_high", "pass_at_1": 0.62, "ci_half": 0.03,
            "mean_cost_usd": 4.0, "mean_output_tokens": 31000, "mean_agent_steps": 55,
        }
        mixed = {**luna, "rows": [*luna["rows"], solo]}
        mixed_costs = {**luna_costs, solo["config"]: 0.8}
        mixed_root = Path(temp) / "mixed"
        mixed_root.mkdir()
        assert (
            create_snapshot(
                mixed, selection, mixed_root, "2026-09-06", False, None, mixed_costs, False, None,
                True, None, luna_accepted,
            )
            == 0
        )
        mixed_readme = (mixed_root / "2026-09-06" / "README.md").read_text(encoding="utf-8")
        assert "`--allow-cost-drift`" in mixed_readme
        assert "`--accept-price-adjustment`" in mixed_readme
        mixed_csv = (mixed_root / "2026-09-06" / RAW).read_text(encoding="utf-8")
        # luna's declared current price, and the drifted single-configuration model's, both landed.
        assert "0.61" in mixed_csv and "0.80" in mixed_csv
    with tempfile.TemporaryDirectory() as temp:
        source_file = Path(temp) / "source.json"
        source_file.write_text(json.dumps(fixture), encoding="utf-8")
        with redirect_stderr(io.StringIO()):
            try:
                main(["--source-file", str(source_file)])
            except SystemExit as error:
                assert error.code == 2
            else:
                raise AssertionError("--source-file was allowed to write without --dry-run")
        # A repository-shaped tree: the allowlist path is read relative to its own grandparent.
        repo = Path(temp) / "repo"
        root = repo / "benchmarks" / "deepswe"
        root.mkdir(parents=True)
        allowlist = repo / ALLOWLIST
        allowlist.parent.mkdir(parents=True)
        # CRLF on purpose: the real allowlist is LF in the index and CRLF in a Windows worktree, and
        # a reader that translates line endings away rewrites all 60 lines instead of adding one.
        allowlist.write_text("CHANGELOG.md\r\nzz-last.md\r\n", encoding="utf-8", newline="")
        index = repo / "benchmarks" / "README.md"
        index.write_text("| Snapshot | What it holds | Feeds |\n|---|---|---|\n", encoding="utf-8")
        assert create_snapshot(fixture, selection, root, "2026-09-05", False, None) == 0
        assert (root / "2026-09-05" / "derived-scores.csv").is_file()
        artifact_readme = (root / "2026-09-05" / "README.md").read_text(encoding="utf-8")
        assert "Cost source: the leaderboard artifact's own" in artifact_readme
        assert "reconciled" not in artifact_readme
        # The primary journey: changed data -> new dated snapshot -> index row -> allowlist entry.
        changed = {
            **fixture,
            "rows": [{**fixture["rows"][0], "pass_at_1": 0.755}, *fixture["rows"][1:]],
        }
        assert (
            create_snapshot(
                changed, selection, root, "2026-09-06", False, index, page_costs, False, allowlist
            )
            == 0
        )
        key = "| [`deepswe/2026-09-06`](deepswe/2026-09-06/README.md) |"
        published = index.read_text(encoding="utf-8")
        assert published.count(key) == 1
        assert (
            f"2 selected vendor/model/effort configurations from the DeepSWE "
            f"{selection['benchmark_version']} live artifact." in published
        )
        assert allowlist.read_bytes() == (
            b"CHANGELOG.md\r\nbenchmarks/deepswe/2026-09-06/README.md\r\nzz-last.md\r\n"
        )
        reconciled_readme = (root / "2026-09-06" / "README.md").read_text(encoding="utf-8")
        assert "reconciled" in reconciled_readme and "5.72" in (root / "2026-09-06" / RAW).read_text(
            encoding="utf-8"
        )
        # Every escape hatch used has to be legible in the snapshot's own README.
        drifted_root = repo / "drifted"
        drifted_root.mkdir()
        assert (
            create_snapshot(
                fixture, selection, drifted_root, "2026-09-06", False, None, drifted, False, None,
                True, "upstream dropped the field on 2026-09-06",
            )
            == 0
        )
        hatched = (drifted_root / "2026-09-06" / "README.md").read_text(encoding="utf-8")
        assert "`--allow-cost-drift`" in hatched
        assert "upstream dropped the field on 2026-09-06" in hatched
        # Index rows get curated after publication; a repair pass must not append a second one.
        index.write_text(published.replace("| Routing evidence |", "| Tier pins (#1861) |", 1),
                         encoding="utf-8")
        assert (
            create_snapshot(
                changed, selection, root, "2026-09-07", False, index, page_costs, False, allowlist
            )
            == 0
        )
        assert not (root / "2026-09-07").exists()
        repaired = index.read_text(encoding="utf-8")
        assert repaired.count(key) == 1
        assert "Tier pins (#1861)" in repaired
        assert allowlist.read_text(encoding="utf-8").count(
            "benchmarks/deepswe/2026-09-06/README.md"
        ) == 1
        reduced = {**fixture, "rows": fixture["rows"][:1]}
        try:
            create_snapshot(reduced, selection, root, "2026-09-06", False, None)
        except ValueError as error:
            assert "--allow-removals" in str(error)
        else:
            raise AssertionError("a removed selected configuration was accepted")
        atomic_root = Path(temp) / "atomic"
        atomic_root.mkdir()
        real_derive = derive_scores.write_or_check

        def fail_derive(*_args, **_kwargs):
            raise RuntimeError("synthetic derive failure")

        try:
            derive_scores.write_or_check = fail_derive
            try:
                create_snapshot(fixture, selection, atomic_root, "2026-09-05", False, None)
            except RuntimeError as error:
                assert "synthetic derive failure" in str(error)
            else:
                raise AssertionError("synthetic derive failure was swallowed")
            assert not (atomic_root / "2026-09-05").exists()
        finally:
            derive_scores.write_or_check = real_derive
    print("refresh_snapshot selftest: pass")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--date", default=date.today().isoformat(), help="snapshot directory date (YYYY-MM-DD)")
    parser.add_argument("--dry-run", action="store_true", help="show the selected delta without writing")
    parser.add_argument("--source-file", type=Path, help="read saved JSON for a dry-run replay/debugging check")
    parser.add_argument(
        "--allow-removals", action="store_true", help="accept removed configurations after inspecting the delta"
    )
    parser.add_argument(
        "--allow-cost-drift",
        action="store_true",
        help=f"accept a displayed cost past the {COST_DRIFT_FACTOR:g}x artifact-reconciliation bound",
    )
    parser.add_argument(
        "--accept-price-adjustment",
        metavar="MODEL=REASON",
        action="append",
        help="record the canonical page's current price for MODEL (spelled as the artifact spells "
        "it) when every selected configuration of it diverges from the artifact by the same factor "
        f"to within {PRICE_ADJUSTMENT_TOLERANCE:.0%} (widest ratio over narrowest); REASON is "
        "written into the snapshot README beside the recorded factor; repeatable",
    )
    parser.add_argument(
        "--allow-missing-provider",
        metavar="REASON",
        help="record the snapshot without the upstream provider cross-check; the reason is written "
        "into the snapshot README",
    )
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args(argv)
    if args.selftest:
        return selftest()
    # Parsed here, before anything is fetched, so a missing REASON costs a request to nobody.
    try:
        accepted_adjustments = parse_price_adjustments(args.accept_price_adjustment)
    except ValueError as error:
        parser.error(str(error))
    if not re.fullmatch(r"\d{4}-\d{2}-\d{2}", args.date):
        parser.error("--date must be YYYY-MM-DD")
    if args.source_file and not args.dry_run:
        parser.error("--source-file is replay-only and requires --dry-run because it has no displayed-cost capture")
    selection = load_selection()
    if args.source_file:
        with args.source_file.open(encoding="utf-8") as f:
            payload = json.load(f)
        costs = None
    else:
        print(f"Fetching {selection['source_url']}")
        payload = fetch_json(selection["source_url"])
        print(f"Fetching displayed costs from {selection['display_url']}")
        costs = displayed_costs(fetch_text(selection["display_url"]), selection["dataset"])
        if not costs:
            raise ValueError(
                f"{selection['display_url']} carried no displayed cost for dataset "
                f"{selection['dataset']!r}: either the source discriminator or the mean_cost_usd key "
                "changed upstream, or the page no longer ships hydration data"
            )
    return create_snapshot(
        payload,
        selection,
        ROOT,
        args.date,
        args.dry_run,
        ROOT.parent / "README.md",
        costs,
        args.allow_removals,
        ROOT.parents[1] / ALLOWLIST,
        args.allow_cost_drift,
        args.allow_missing_provider,
        accepted_adjustments,
    )


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"refresh_snapshot: {error}", file=sys.stderr)
        raise SystemExit(1)
