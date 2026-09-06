"""Derive the per-model / per-vendor / per-arm medians the comparator reads, from the committed
cost-ledger exports and nothing else (#1901 C3).

Reads `benchmarks/ledger/<YYYY-MM-DD>.csv` -- the NEWEST one only. Every export is the whole store,
and the ledger is append-only with immutable rows, so the newest file is a superset of every older
one; unioning them would count each execution once per export. Writes `medians.md` and
`medians.json` beside them. It never reads ~/.baton: that is the point of committing the exports.

The unit of observation is a MERGED PR, not an execution. Rows are grouped by their `pr` column, each
group's billed tokens, tool steps (`turns`) and wall clock summed, and its fix rounds counted as the
`implement`-role attempts after the first (a one-shot PR is 0). Medians are then taken across PRs,
cut per model, per vendor and per arm (the dispatch `label`). A PR whose rows span two models is
counted under both -- the question is "what does a PR cost with this model on it", and a PR is
routinely a mix.

Absence is not zero, the ledger's own doctrine carried through the derivation: a row with an empty
`billedTokens` contributes nothing to its PR's sum rather than a 0, a PR no row reported that
dimension for is EXCLUDED from that dimension's median rather than entering it as 0, and every
metric carries the number of PRs that actually fed it so a median cannot be read as more complete
than it is. The `identitySource` and `completeness` mix, and the count of rows no PR could be
attributed to, are reported for the same reason: how much of the population is backfilled or partial
is what decides whether a median drawn from it means anything.

What each number is for, and the reading rules, are written once in benchmarks/ledger/README.md.

Usage:
    python benchmarks/ledger/derive.py [--check | --selftest]
"""

from __future__ import annotations

import argparse
import csv
import json
import re
import shutil
import statistics
import sys
import tempfile
from pathlib import Path

LEDGER_ROOT = Path(__file__).resolve().parent
FIXTURE = LEDGER_ROOT / "fixtures" / "selftest.csv"
JSON_OUT = "medians.json"
MD_OUT = "medians.md"
EXPORT_NAME = re.compile(r"^\d{4}-\d{2}-\d{2}\.csv$")

# (json key, markdown heading, the row column summed into it). fixRounds is derived rather than
# summed, so it is not in this table -- per_pr() is the one place it is defined.
METRICS = (
    ("billedTokens", "Billed tokens", "billedTokens"),
    ("toolSteps", "Tool steps", "turns"),
    ("wallClockMs", "Wall clock (ms)", "wallClockMs"),
)
CUTS = (("byModel", "model", "Per model"), ("byVendor", "adapter", "Per vendor"), ("byArm", "label", "Per arm (label)"))
ABSENT = "(absent)"


def newest_export(root: Path) -> Path | None:
    """The newest dated export. Named files only: a fixture or a stray CSV must not become input."""
    exports = sorted(p for p in root.iterdir() if p.is_file() and EXPORT_NAME.match(p.name))
    return exports[-1] if exports else None


def load(path: Path) -> list[dict]:
    with path.open(encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f))


def number(cell: str | None) -> int | None:
    """An empty cell is ABSENT, never 0 -- the ledger's doctrine surviving the derivation."""
    if cell is None or cell.strip() == "":
        return None
    try:
        return int(float(cell))
    except ValueError:
        return None


def median(values: list[int]) -> int | float | None:
    if not values:
        return None
    m = statistics.median(values)
    return int(m) if float(m).is_integer() else round(m, 1)


def per_pr(rows: list[dict]) -> dict[str, dict]:
    """One record per merged PR: the summed dimensions, the fix rounds, and every model/vendor/arm
    that appears in it."""
    prs: dict[str, dict] = {}
    for row in rows:
        pr = (row.get("pr") or "").strip()
        if not pr:
            continue
        record = prs.setdefault(
            pr,
            {"implementAttempts": 0, "model": set(), "adapter": set(), "label": set()}
            | {key: [] for key, _heading, _column in METRICS},
        )
        for key, _heading, column in METRICS:
            value = number(row.get(column))
            if value is not None:
                record[key].append(value)
        if (row.get("role") or "").strip() == "implement":
            record["implementAttempts"] += 1
        for facet in ("model", "adapter", "label"):
            if (value := (row.get(facet) or "").strip()):
                record[facet].add(value)

    for record in prs.values():
        for key, _heading, _column in METRICS:
            record[key] = sum(record[key]) if record[key] else None
        # Fix rounds: the implement attempts AFTER the first. A PR reached in one go is 0, not 1 --
        # the metric is rework, and a metric whose floor is 1 cannot show the absence of rework.
        record["fixRounds"] = max(0, record["implementAttempts"] - 1)
    return prs


def summarise(records: list[dict]) -> dict:
    out = {"prs": len(records)}
    for key in [k for k, _h, _c in METRICS] + ["fixRounds"]:
        present = [r[key] for r in records if r.get(key) is not None]
        out[key] = {"median": median(present), "prs": len(present)}
    return out


def mix(rows: list[dict], column: str) -> dict[str, int]:
    counts: dict[str, int] = {}
    for row in rows:
        key = (row.get(column) or "").strip() or ABSENT
        counts[key] = counts.get(key, 0) + 1
    return dict(sorted(counts.items()))


def derive(rows: list[dict], source: str) -> dict:
    prs = per_pr(rows)
    payload = {
        "source": source,
        "population": {
            "rows": len(rows),
            "mergedPrs": len(prs),
            "rowsWithNoPr": sum(1 for r in rows if not (r.get("pr") or "").strip()),
            "identitySource": mix(rows, "identitySource"),
            "completeness": mix(rows, "completeness"),
        },
    }
    for cut_key, facet, _heading in CUTS:
        groups: dict[str, list[dict]] = {}
        for record in prs.values():
            for value in sorted(record[facet]):
                groups.setdefault(value, []).append(record)
        payload[cut_key] = {name: summarise(members) for name, members in sorted(groups.items())}
    return payload


def render(payload: dict) -> str:
    population = payload["population"]
    lines = [
        "# Cost-ledger medians",
        "",
        f"Derived by `benchmarks/ledger/derive.py` from `{payload['source']}`. Do not edit by hand.",
        "What these numbers are for, and what they do not say, is benchmarks/ledger/README.md.",
        "",
        "## Population",
        "",
        "| | |",
        "|---|---|",
        f"| Rows | {population['rows']} |",
        f"| Merged PRs | {population['mergedPrs']} |",
        f"| Rows attributable to no PR | {population['rowsWithNoPr']} |",
        f"| identitySource | {describe_mix(population['identitySource'])} |",
        f"| completeness | {describe_mix(population['completeness'])} |",
    ]

    headings = ["Billed tokens", "Tool steps", "Wall clock (ms)", "Fix rounds"]
    keys = [k for k, _h, _c in METRICS] + ["fixRounds"]
    for cut_key, _facet, heading in CUTS:
        lines += ["", f"## {heading}", ""]
        if not payload[cut_key]:
            lines.append("No merged PR in this export carries this facet.")
            continue
        lines.append("| " + " | ".join([heading.split(" (")[0], "PRs", *headings]) + " |")
        lines.append("|" + "---|" * (len(headings) + 2))
        for name, group in payload[cut_key].items():
            cells = [f"`{name}`", str(group["prs"])]
            cells += [describe_metric(group[key]) for key in keys]
            lines.append("| " + " | ".join(cells) + " |")

    return "\n".join(lines) + "\n"


def describe_mix(counts: dict[str, int]) -> str:
    return ", ".join(f"{name} {count}" for name, count in counts.items()) or "-"


def describe_metric(metric: dict) -> str:
    """A median no PR fed prints as `-`, never 0, and one only some PRs fed says how many."""
    if metric["median"] is None:
        return "-"
    return f"{metric['median']} ({metric['prs']})"


def write_or_check(root: Path, check: bool) -> int:
    source = newest_export(root)
    if source is None:
        print(f"derive: no <YYYY-MM-DD>.csv export in {root}", file=sys.stderr)
        return 1

    payload = derive(load(source), source.name)
    wanted = {
        JSON_OUT: json.dumps(payload, indent=2, sort_keys=False) + "\n",
        MD_OUT: render(payload),
    }

    if check:
        stale = []
        for name, expected in wanted.items():
            target = root / name
            # newline="" so CRLF drift is a difference rather than something universal-newline reading
            # hides; .gitattributes pins both outputs to LF so a fresh checkout compares byte-equal.
            current = ""
            if target.exists():
                with target.open(encoding="utf-8", newline="") as f:
                    current = f.read()
            if current != expected:
                stale.append(str(target))
        if stale:
            for target in stale:
                print(f"derive: {target} is stale or missing; rerun without --check", file=sys.stderr)
            return 1
        print(f"derive: {JSON_OUT} and {MD_OUT} are current against {source.name}")
        return 0

    for name, content in wanted.items():
        # newline="" so the LF the renderer emits reaches the file unchanged on Windows; a CRLF
        # translation here would make --check read every correct file as stale.
        with (root / name).open("w", encoding="utf-8", newline="") as f:
            f.write(content)
    print(f"derive: wrote {JSON_OUT} and {MD_OUT} from {source.name} "
          f"({payload['population']['rows']} row(s), {payload['population']['mergedPrs']} merged PR(s))")
    return 0


# The fixture's answers, computed by hand from benchmarks/ledger/fixtures/selftest.csv and stated
# here rather than snapshotted from a previous run -- a snapshot of the code's own output cannot tell
# a correct derivation from one that has been wrong since the day it was written. Every arm below is
# one the implementation could plausibly get wrong: PR 100's billed sum EXCLUDES its review row's
# empty cell (absence is not zero), PR 400 is excluded from the billed median entirely while still
# feeding the tool-step one (so the two contributor counts differ, 2 and 3), fix rounds are the
# implement attempts AFTER the first, and the no-PR row is in the population and in no median.
FIXTURE_ANSWERS = {
    "population": {
        "rows": 7,
        "mergedPrs": 4,
        "rowsWithNoPr": 1,
        "identitySource": {"recorded-root": 6, "working-directory": 1},
        "completeness": {ABSENT: 2, "complete": 4, "partial": 1},
    },
    "byModel": {
        "claude-opus-5": {
            "prs": 3,
            "billedTokens": {"median": 350, "prs": 2},
            "toolSteps": {"median": 40, "prs": 3},
            "wallClockMs": {"median": 4000, "prs": 3},
            "fixRounds": {"median": 0, "prs": 3},
        },
        "gemini-3-pro": {
            "prs": 1,
            "billedTokens": {"median": 1000, "prs": 1},
            "toolSteps": {"median": 100, "prs": 1},
            "wallClockMs": {"median": 10000, "prs": 1},
            "fixRounds": {"median": 0, "prs": 1},
        },
    },
}


def selftest() -> int:
    rows = load(FIXTURE)
    payload = derive(rows, FIXTURE.name)

    for key, expected in FIXTURE_ANSWERS.items():
        if payload[key] != expected:
            print(f"derive selftest: FAIL ({key})\n  wanted {expected}\n  got    {payload[key]}", file=sys.stderr)
            return 1

    # The two other cuts are the same PRs sliced differently, so they must agree with byModel rather
    # than merely exist -- a cut that silently grouped everything into one bucket would pass a bare
    # "is non-empty" check.
    if payload["byVendor"]["claude"] != payload["byModel"]["claude-opus-5"]:
        print("derive selftest: FAIL (byVendor disagrees with byModel)", file=sys.stderr)
        return 1
    if payload["byArm"]["A"] != payload["byModel"]["claude-opus-5"]:
        print("derive selftest: FAIL (byArm disagrees with byModel)", file=sys.stderr)
        return 1
    if payload["byArm"]["B"] != payload["byModel"]["gemini-3-pro"]:
        print("derive selftest: FAIL (byArm B disagrees with byModel)", file=sys.stderr)
        return 1

    # Then the same discrimination arms deepswe's derive_scores.py --selftest runs: prove --check
    # actually rejects drift, rather than proving it accepts a tree it has just written.
    with tempfile.TemporaryDirectory() as temp_dir:
        scratch = Path(temp_dir) / "ledger"
        scratch.mkdir()
        shutil.copy2(FIXTURE, scratch / "2026-01-02.csv")

        if write_or_check(scratch, check=False) != 0 or write_or_check(scratch, check=True) != 0:
            print("derive selftest: FAIL (a freshly written scratch export was rejected)", file=sys.stderr)
            return 1

        sabotaged = json.loads((scratch / JSON_OUT).read_text(encoding="utf-8"))
        sabotaged["population"]["rows"] += 1
        with (scratch / JSON_OUT).open("w", encoding="utf-8", newline="") as f:
            f.write(json.dumps(sabotaged, indent=2) + "\n")
        if write_or_check(scratch, check=True) != 1:
            print("derive selftest: FAIL (an edited derived file was accepted)", file=sys.stderr)
            return 1

        write_or_check(scratch, check=False)
        (scratch / MD_OUT).unlink()
        if write_or_check(scratch, check=True) != 1:
            print("derive selftest: FAIL (a missing derived file was accepted)", file=sys.stderr)
            return 1

        (scratch / "2026-01-02.csv").unlink()
        if write_or_check(scratch, check=False) != 1:
            print("derive selftest: FAIL (a directory with no export was accepted)", file=sys.stderr)
            return 1

    print("derive selftest: pass")
    return 0


def main(argv: list[str], ledger_root: Path = LEDGER_ROOT) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    modes = ap.add_mutually_exclusive_group()
    modes.add_argument("--check", action="store_true", help="exit 1 if the committed outputs differ from a fresh derivation")
    modes.add_argument("--selftest", action="store_true", help="prove the derivation matches known answers and that --check rejects drift")
    a = ap.parse_args(argv)

    if a.selftest:
        return selftest()
    return write_or_check(ledger_root, a.check)


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
