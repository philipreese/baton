"""Fail closed to full coverage unless a complete PR diff only edits the page (#2498)."""
import os
from pathlib import Path
import re
import subprocess

PAGE_PATHS = {b"tools/fleet-glass/glass.html", b"tools/fleet-glass/glass.selftest.mjs"}


def page_only(root: Path, base: str, head: str) -> bool:
    if not all(re.fullmatch(r"[0-9a-f]{40}", revision) for revision in (base, head)):
        raise ValueError("classification requires exact base and head SHAs")
    # Raw, NUL-delimited, complete git output retains types and both sides of renames
    # (as delete/add). No pagination or path quoting can conceal an unrelated change.
    result = subprocess.run(
        ["git", "diff", "--raw", "-z", "--no-abbrev", "--no-renames", f"{base}...{head}", "--"],
        cwd=root, env={k: v for k, v in os.environ.items() if not k.startswith("GIT_")},
        check=True, capture_output=True,
    )
    fields = result.stdout.split(b"\0")
    if fields == [b""]:
        return False
    if fields[-1] != b"" or len(fields) % 2 != 1:
        raise ValueError("malformed raw diff")
    for index in range(0, len(fields) - 1, 2):
        header, path = fields[index:index + 2]
        if not re.fullmatch(rb":100644 100644 [0-9a-f]{40} [0-9a-f]{40} M", header) or path not in PAGE_PATHS:
            return False
    return True


if __name__ == "__main__":
    value = str(page_only(Path.cwd(), os.environ["PR_BASE_SHA"], os.environ["PR_HEAD_SHA"])).lower()
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
        stream.write(f"page-only={value}\n")
    print(f"page-only={value}")
