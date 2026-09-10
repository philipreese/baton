"""Fail on phrases a decision record has retired.

A retired phrase is not ambiguous wording — it is the *previous name* of an object that a numbered
decision has since renamed. That makes every occurrence stale by definition, which is what lets this
be a lexical check with no false positives to triage: there is no context in which the old name is
still right.

Decision 0055 retires "standing grant" (the 0022 object is a *standing permission*; bare "grant" is
reserved for 0049's authority grant). Prose saying so enforces nothing on its own — this is the check
that does, per the project's rule that anything which must not regress needs one that runs and fails.

Deliberately NOT a general vocabulary lint. Telling authority-"grant" (correct) from permission-"grant"
(drift) needs the sentence's meaning, so a lexical rule would drown in false positives on correct
usage — exactly the shape `audit-controls` refuses. This checks only phrases with no legitimate
remaining use.

Exemptions are per-occurrence and must say why: put `retired-ok: <reason>` in a comment on the same
line. The one standing case is a verbatim quotation whose source has not been corrected — quoting
someone accurately outranks the rename.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# phrase -> the record that retired it and what replaced it.
RETIRED = {
    "standing grant": "0055 — say 'standing permission' (bare 'grant' is 0049's authority grant)",
}

SEARCH_ROOTS = ["docs", "spec", "src", "tests", "tools"]

SEARCH_SUFFIXES = {".md", ".html", ".cs", ".dart", ".axaml", ".py", ".json"}

# docs/archive holds superseded documents on purpose: a doc in the live tree is current, a doc that
# is not gets moved there (the development guide's repo map). Rewriting history there would defeat
# the point of keeping it. CHANGELOG is generated from commit subjects that were true when written.
EXCLUDED = (
    ROOT / "docs" / "archive",
    ROOT / "CHANGELOG.md",
)

MARKER_RE = re.compile(r"retired-ok\b:?\s*(.*)")


def _is_excluded(path: Path) -> bool:
    return any(path == e or e in path.parents for e in EXCLUDED)


def main(argv: list[str]) -> int:
    if "--selftest" in argv:
        return _selftest()

    problems: list[str] = []
    scanned = 0

    for root_name in SEARCH_ROOTS:
        root = ROOT / root_name
        if not root.is_dir():
            continue
        for path in root.rglob("*"):
            if not path.is_file() or path.suffix.lower() not in SEARCH_SUFFIXES:
                continue
            if _is_excluded(path):
                continue
            if any(part in {"bin", "obj", "node_modules", ".dart_tool", "__pycache__"}
                   for part in path.parts):
                continue
            # This file names the phrases it bans, so it would fail itself.
            if path.resolve() == Path(__file__).resolve():
                continue

            try:
                text = path.read_text(encoding="utf-8")
            except (UnicodeDecodeError, OSError):
                continue

            scanned += 1
            for lineno, line in enumerate(text.splitlines(), start=1):
                lowered = line.lower()
                for phrase, guidance in RETIRED.items():
                    if phrase not in lowered:
                        continue
                    marker = MARKER_RE.search(line)
                    if marker:
                        if not marker.group(1).strip():
                            problems.append(
                                f"{path.relative_to(ROOT)}:{lineno}: 'retired-ok:' with an empty "
                                f"reason — say why this occurrence stays")
                        continue
                    problems.append(
                        f"{path.relative_to(ROOT)}:{lineno}: retired phrase '{phrase}' — {guidance}. "
                        f"Mark 'retired-ok: <reason>' on the line if this occurrence must stay.")

    print(f"retiredphrases: {scanned} file(s) scanned for {len(RETIRED)} retired phrase(s)")
    if problems:
        print(f" !! {len(problems)} problem(s):")
        for p in problems:
            print(f"  {p}")
        return 1
    print(" OK no retired phrase in the live tree")
    return 0


def _selftest() -> int:
    """The control this checker would be useless without: prove it still catches what it exists for.

    A checker only ever exercised against a clean tree reports success either way — the failure this
    project has already paid for twice.
    """
    failures = []

    sample = "the second time should offer the standing grant first"
    if "standing grant" not in sample.lower():
        failures.append("the sample no longer contains the phrase; the selftest is vacuous")

    exempted = "// standing grant — retired-ok: quoting a source not yet corrected"
    if not MARKER_RE.search(exempted):
        failures.append("an exemption marker was not recognised")

    empty = "// standing grant — retired-ok:"
    m = MARKER_RE.search(empty)
    if m is None or m.group(1).strip():
        failures.append("an empty-reason marker was not detected as empty")

    if failures:
        print(" !! retiredphrases selftest FAILED:")
        for f in failures:
            print(f"  {f}")
        return 1
    print("retiredphrases: selftest OK")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
