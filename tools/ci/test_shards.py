"""Discover and run one authoritative Baton.slnx test shard (#2182)."""

from __future__ import annotations

import argparse
import json
from pathlib import Path, PurePosixPath
import subprocess
import sys
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
SOLUTION = ROOT / "Baton.slnx"
FLOW_ANCHOR = PurePosixPath("tests/Baton.Tests/Baton.Tests.csproj")
IDENTITY_PROPERTIES = ("IsTestProject", "IsTestingPlatformApplication")


class ContractError(RuntimeError):
    """The solution cannot be partitioned without guessing."""


def solution_projects(solution: Path) -> list[tuple[PurePosixPath, Path]]:
    try:
        root = ET.parse(solution).getroot()
    except (OSError, ET.ParseError) as error:
        raise ContractError(f"cannot read solution {solution}: {error}") from error

    projects: list[tuple[PurePosixPath, Path]] = []
    seen: set[PurePosixPath] = set()
    for element in root.iter("Project"):
        raw = element.get("Path")
        if not raw:
            raise ContractError("solution contains a Project without a Path")
        relative = PurePosixPath(raw.replace("\\", "/"))
        if relative in seen:
            raise ContractError(f"solution contains duplicate project {relative}")
        seen.add(relative)
        project = solution.parent.joinpath(*relative.parts)
        if not project.is_file():
            raise ContractError(f"solution project is missing: {relative}")
        projects.append((relative, project))
    if not projects:
        raise ContractError("solution contains no projects")
    return projects


def evaluated_identity(project: Path) -> tuple[str, str]:
    command = [
        sys.executable,
        str(ROOT / "tools/buildlock.py"),
        "dotnet",
        "msbuild",
        str(project),
        "-getProperty:" + ",".join(IDENTITY_PROPERTIES),
    ]
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, check=False)
    if result.returncode:
        detail = (result.stderr or result.stdout).strip()
        raise ContractError(f"MSBuild identity evaluation failed for {project}: {detail}")
    start = result.stdout.find("{")
    try:
        payload = json.loads(result.stdout[start:])
        properties = payload["Properties"]
        return tuple(str(properties[name]).strip().lower() for name in IDENTITY_PROPERTIES)
    except (json.JSONDecodeError, KeyError, TypeError) as error:
        raise ContractError(f"MSBuild returned no usable test identity for {project}") from error


def discover_shards(
    solution: Path,
    evaluator=evaluated_identity,
) -> dict[str, list[PurePosixPath]]:
    tests: list[PurePosixPath] = []
    for relative, project in solution_projects(solution):
        identity = evaluator(project)
        if identity == ("true", "true"):
            tests.append(relative)
        elif identity != ("false", "false"):
            raise ContractError(
                f"ambiguous test identity for {relative}: "
                f"{IDENTITY_PROPERTIES[0]}={identity[0]!r}, "
                f"{IDENTITY_PROPERTIES[1]}={identity[1]!r}"
            )

    if FLOW_ANCHOR not in tests:
        raise ContractError(f"flow anchor is absent or not a test project: {FLOW_ANCHOR}")
    shards = {
        "flow": [FLOW_ANCHOR],
        "other": [project for project in tests if project != FLOW_ANCHOR],
    }
    for name, projects in shards.items():
        if not projects:
            raise ContractError(f"{name} shard is empty")
    if set(shards["flow"]) & set(shards["other"]) or set().union(*map(set, shards.values())) != set(tests):
        raise ContractError("test shards are not disjoint and exhaustive")
    return shards


def test_command(project: PurePosixPath) -> list[str]:
    return [
        sys.executable,
        str(ROOT / "tools/buildlock.py"),
        "dotnet",
        "test",
        "--project",
        str(project),
        "--no-build",
        "--minimum-expected-tests",
        "1",
    ]


def run_shard(name: str, solution: Path = SOLUTION) -> int:
    projects = discover_shards(solution)[name]
    print(f"test-shards: {name} owns {', '.join(map(str, projects))}", flush=True)
    for project in projects:
        result = subprocess.run(test_command(project), cwd=ROOT, check=False)
        if result.returncode:
            return result.returncode
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("shard", choices=("flow", "other"))
    args = parser.parse_args()
    try:
        return run_shard(args.shard)
    except ContractError as error:
        print(f"test-shards: {error}", file=sys.stderr, flush=True)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
