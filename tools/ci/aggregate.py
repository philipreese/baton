"""Fail-closed owner of the same-revision gates/test coverage union (#2182)."""

from __future__ import annotations

import os
import sys


VALID_RESULTS = {"success", "failure", "cancelled", "skipped"}


def verify_coverage(event: str, changes: str, dotnet: str, test: str, gates: str, mode: str) -> list[str]:
    errors: list[str] = []
    if event not in {"push", "pull_request"}:
        errors.append(f"unsupported aggregate event {event!r}")
        return errors
    for label, value in (("changes", changes), ("test", test), ("gates", gates)):
        if value not in VALID_RESULTS:
            errors.append(f"{label} has missing or unknown result {value!r}")
    if gates != "success":
        errors.append(f"gates must be success, got {gates!r}")

    if event == "pull_request":
        if changes != "success":
            errors.append(f"path filtering must succeed on a pull request, got {changes!r}")
        if dotnet not in {"true", "false"}:
            errors.append(f"path filtering returned unknown dotnet value {dotnet!r}")
        shards_required = dotnet == "true"
    else:
        if changes != "skipped":
            errors.append(f"the pull-request-only changes job must be skipped on push, got {changes!r}")
        shards_required = True

    expected_mode = "test-shard-complement" if shards_required else "full"
    expected_test = "success" if shards_required else "skipped"
    if mode != expected_mode:
        errors.append(f"coverage mode must be {expected_mode!r}, got {mode!r}")
    if test != expected_test:
        errors.append(f"test matrix must be {expected_test!r}, got {test!r}")
    return errors


def main() -> int:
    values = {name: os.environ.get(name, "") for name in ("EVENT", "CHANGES", "DOTNET", "TEST", "GATES", "MODE")}
    print("results: " + " ".join(f"{key.lower()}={value}" for key, value in values.items()))
    errors = verify_coverage(*(values[name] for name in ("EVENT", "CHANGES", "DOTNET", "TEST", "GATES", "MODE")))
    if errors:
        for error in errors:
            print(f"::error::{error}")
        return 1
    print("CI gate passed with complete same-revision coverage.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
