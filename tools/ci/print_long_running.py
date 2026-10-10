"""Project local MTP warnings to safe test identities; never print raw diagnostics."""

from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[2]
WARNING = re.compile(
    r"[^\r\n]* xUnit\.net INFORMATION \[Long Running Test\] '"
    r"(?P<identity>Baton(?:\.[A-Za-z_][A-Za-z0-9_]*)+)"
    r"(?:\([^\r\n]*\))?', Elapsed: "
    r"(?P<elapsed>[0-9]{2,}:[0-5][0-9]:[0-5][0-9](?:\.[0-9]+)?)"
)


def safe_warning(line: str) -> str | None:
    # Only validated identity and duration may leave the runner; theory arguments stay local.
    match = WARNING.fullmatch(line)
    if match is None:
        return None
    return f"long-running: {match['identity']} elapsed={match['elapsed']}"


def main() -> None:
    for path in sorted((ROOT / ".local-data/ci-test-diagnostics").glob("**/*.diag")):
        with path.open(encoding="utf-8", errors="replace") as stream:
            for line in stream:
                warning = safe_warning(line.rstrip("\r\n"))
                if warning is not None:
                    print(warning, flush=True)


if __name__ == "__main__":
    try:
        main()
    except OSError:
        print("Could not read local test diagnostics.", file=sys.stderr)
        raise SystemExit(1)
