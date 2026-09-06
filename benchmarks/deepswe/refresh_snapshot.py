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


def reconcile_cost(
    label: str, config: str, artifact_cost: float, displayed_cost: float, allow_cost_drift: bool
) -> float:
    """Verify a scraped displayed cost against the artifact's own cost; never substitute it blind."""
    if not math.isfinite(displayed_cost) or displayed_cost <= 0:
        raise ValueError(
            f"{label}: displayed cost for {config!r} must be finite and greater than zero; "
            f"got {displayed_cost!r}"
        )
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


def cost_provenance(display_url: str, used_displayed_costs: bool, allow_cost_drift: bool) -> str:
    """State where the recorded costs actually came from.

    A value of the cost source, not a flag next to it: a snapshot built from artifact costs must not
    be able to claim canonical-page provenance.
    """
    if not used_displayed_costs:
        return (
            "- Cost source: the leaderboard artifact's own `mean_cost_usd`. The canonical page was "
            "NOT consulted for this snapshot, so a cost the page has since adjusted is recorded here "
            "at its launch price."
        )
    line = (
        f"- Cost source: displayed costs from [{display_url}]({display_url}), each reconciled "
        f"against the artifact's own cost and required to stay within {COST_DRIFT_FACTOR:g}x of it "
        "(the artifact can retain launch-price costs after the canonical page applies announced "
        "price changes)."
    )
    if allow_cost_drift:
        line += " Divergences past that bound were accepted for this run via `--allow-cost-drift`."
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
) -> int:
    rows = select_rows(
        payload,
        selection["model_patterns"],
        display_cost_by_config,
        allow_cost_drift,
        missing_provider_reason,
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
                    selection["display_url"], display_cost_by_config is not None, allow_cost_drift
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
        "--allow-missing-provider",
        metavar="REASON",
        help="record the snapshot without the upstream provider cross-check; the reason is written "
        "into the snapshot README",
    )
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args(argv)
    if args.selftest:
        return selftest()
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
    )


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"refresh_snapshot: {error}", file=sys.stderr)
        raise SystemExit(1)
