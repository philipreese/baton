"""Fetch DeepSWE's live JSON and create a dated, immutable Baton snapshot.

The selected model families live in selection.json. A normal refresh is:

    python benchmarks/deepswe/refresh_snapshot.py

Use --dry-run to inspect the upstream delta without writing. The command does nothing when the
selected data is unchanged, and refuses to overwrite an existing dated snapshot.
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


def load_selection(path: Path = SELECTION) -> dict:
    with path.open(encoding="utf-8") as f:
        value = json.load(f)
    for key in ("source_url", "display_url", "benchmark_version", "model_patterns"):
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


def displayed_costs(html: str) -> dict[str, float]:
    """Extract the server-rendered costs after DeepSWE's current price adjustments.

    DeepSWE's artifact currently retains launch-price costs for some models while its canonical page
    serves adjusted costs in the hydration data. Keeping these inputs separate also makes a future
    upstream schema change fail loudly instead of silently recording a plausible stale price.
    """
    pairs = re.findall(
        r'config:"([a-zA-Z0-9_-]+)"(?:(?!config:").){0,5000}?mean_cost_usd:([0-9.eE+-]+)',
        html,
        re.DOTALL,
    )
    return {config: float(cost) for config, cost in pairs}


def vendor_for(row: dict) -> str:
    model = str(row.get("model", ""))
    inferred = None
    for prefix, vendor in MODEL_VENDOR_PREFIXES.items():
        if model.startswith(prefix):
            inferred = vendor
            break
    if inferred is None:
        raise ValueError(f"cannot infer vendor for model {model!r}; add its prefix locally")
    reported = str(row.get("provider") or inferred).lower()
    if reported != inferred:
        raise ValueError(
            f"provider mismatch for {model!r}: upstream reports {reported!r}, expected {inferred!r}"
        )
    return inferred


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
        cost = display_cost_by_config[config] if display_cost_by_config is not None else artifact_cost
        if not math.isfinite(cost) or cost <= 0:
            raise ValueError(f"{model} / {effort}: displayed cost must be finite and greater than zero")
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
                "vendor": vendor_for(row),
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


def snapshot_readme(
    snapshot_date: str,
    source_url: str,
    display_url: str,
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
- Displayed-cost source: [{display_url}]({display_url}) (the artifact can retain launch-price costs
  after the canonical page applies announced price changes).
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


def update_index(index_path: Path, snapshot_date: str, count: int) -> None:
    text = index_path.read_text(encoding="utf-8")
    row = (
        f"| [`deepswe/{snapshot_date}`](deepswe/{snapshot_date}/README.md) | {count} selected "
        "vendor/model/effort configurations from the DeepSWE v1.1 live artifact. | Routing evidence |"
    )
    if row in text:
        return
    marker = "|---|---|---|\n"
    if marker not in text:
        raise ValueError(f"cannot find benchmark index table in {index_path}")
    index_path.write_text(text.replace(marker, marker + row + "\n", 1), encoding="utf-8", newline="\n")


def create_snapshot(
    payload: dict,
    selection: dict,
    root: Path,
    snapshot_date: str,
    dry_run: bool,
    index_path: Path | None,
    display_cost_by_config: dict[str, float] | None = None,
    allow_removals: bool = False,
) -> int:
    rows = select_rows(payload, selection["model_patterns"], display_cost_by_config)
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
        if not dry_run and index_path:
            update_index(index_path, previous_path.name, len(rows))
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
                selection["display_url"],
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
    if index_path:
        update_index(index_path, snapshot_date, len(rows))
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
                "model": "claude-fable-5-1", "reasoning_effort": "max", "pass_at_1": 0.70,
                "config": "mini_swe_agent_claude_fable_5_1_max",
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
    costs = displayed_costs(
        '<script>config:"mini_swe_agent_gpt_6_astra_high",source:"deep-swe",'
        'mean_cost_usd:5.72,mean_output_tokens:29557.3</script>'
    )
    assert costs == {"mini_swe_agent_gpt_6_astra_high": 5.72}
    try:
        select_rows(fixture, selection["model_patterns"], costs)
    except ValueError as error:
        assert "canonical page has no displayed cost" in str(error)
    else:
        raise AssertionError("a missing canonical displayed cost was accepted")
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
        root = Path(temp) / "deepswe"
        root.mkdir()
        assert create_snapshot(fixture, selection, root, "2026-09-05", False, None) == 0
        assert (root / "2026-09-05" / "derived-scores.csv").is_file()
        index = Path(temp) / "README.md"
        index.write_text("| Snapshot | What it holds | Feeds |\n|---|---|---|\n", encoding="utf-8")
        assert create_snapshot(fixture, selection, root, "2026-09-06", False, index) == 0
        assert "deepswe/2026-09-05" in index.read_text(encoding="utf-8")
        assert create_snapshot(fixture, selection, root, "2026-09-06", False, None) == 0
        assert not (root / "2026-09-06").exists()
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
        costs = displayed_costs(fetch_text(selection["display_url"]))
        if not costs:
            raise ValueError("canonical page contained no displayed leaderboard costs")
    return create_snapshot(
        payload,
        selection,
        ROOT,
        args.date,
        args.dry_run,
        ROOT.parent / "README.md",
        costs,
        args.allow_removals,
    )


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"refresh_snapshot: {error}", file=sys.stderr)
        raise SystemExit(1)
