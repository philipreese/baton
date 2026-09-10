"""Discriminating controls for CI coverage ownership and shard discovery (#2182)."""

from __future__ import annotations

import ast
from pathlib import Path, PurePosixPath
import re
import sys
import tempfile
import tomllib

from aggregate import verify_coverage
from test_shards import ContractError, discover_shards, evaluated_identity, test_command


ROOT = Path(__file__).resolve().parents[2]
FLOW = PurePosixPath("tests/Baton.Tests/Baton.Tests.csproj")
FLOW_COMMAND = "python tools/buildlock.py dotnet build --no-incremental && python tools/ci/test_shards.py flow"
OTHER_COMMAND = "python tools/buildlock.py dotnet build --no-incremental && python tools/ci/test_shards.py other"


def refused(action, diagnostic: str) -> None:
    try:
        action()
    except ContractError as error:
        assert diagnostic in str(error), error
        return
    raise AssertionError("invalid fixture was accepted")


def write_solution(root: Path, projects: list[str]) -> Path:
    for project in projects:
        path = root / project
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("<Project />\n", encoding="utf-8")
    body = "\n".join(f'  <Project Path="{project}" />' for project in projects)
    solution = root / "Baton.slnx"
    solution.write_text(f"<Solution>\n{body}\n</Solution>\n", encoding="utf-8")
    return solution


def check_discovery() -> None:
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        names = [str(FLOW), "tests/One.Tests/One.Tests.csproj", "tests/Two.Tests/Two.Tests.csproj", "tests/Three.Tests/Three.Tests.csproj", "tests/Four.Tests/Four.Tests.csproj", "tests/Helper/Helper.csproj"]
        solution = write_solution(root, names)
        identities = {name: (("false", "false") if "Helper" in name else ("true", "true")) for name in names}
        calls: list[str] = []

        def evaluate(project: Path) -> tuple[str, str]:
            relative = project.relative_to(root).as_posix()
            calls.append(relative)
            return identities[relative]

        shards = discover_shards(solution, evaluate)
        assert shards["flow"] == [FLOW]
        assert shards["other"] == [PurePosixPath(name) for name in names[1:5]]
        assert calls == names
        assert set(shards["flow"]).isdisjoint(shards["other"])
        assert set(shards["flow"] + shards["other"]) == {PurePosixPath(name) for name in names[:5]}

        new_name = "tests/New.Tests/New.Tests.csproj"
        identities[new_name] = ("true", "true")
        solution = write_solution(root, names + [new_name])
        assert discover_shards(solution, evaluate)["other"][-1] == PurePosixPath(new_name)

        identities[str(FLOW)] = ("false", "false")
        refused(lambda: discover_shards(solution, evaluate), "flow anchor")
        identities[str(FLOW)] = ("true", "true")
        for name in names[1:5] + [new_name]:
            identities[name] = ("false", "false")
        refused(lambda: discover_shards(solution, evaluate), "other shard is empty")
        identities[names[1]] = ("", "")
        refused(lambda: discover_shards(solution, evaluate), "ambiguous test identity")
        identities[names[1]] = ("true", "false")
        refused(lambda: discover_shards(solution, evaluate), "ambiguous test identity")
        (root / names[1]).unlink()
        refused(lambda: discover_shards(solution, evaluate), "solution project is missing")

    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        (root / "identity.props").write_text(
            "<Project><PropertyGroup><IsTestProject>true</IsTestProject>"
            "<IsTestingPlatformApplication>true</IsTestingPlatformApplication>"
            "</PropertyGroup></Project>\n",
            encoding="utf-8",
        )
        project = root / "Imported.Tests.csproj"
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><Import Project="identity.props" /></Project>\n',
            encoding="utf-8",
        )
        assert evaluated_identity(project) == ("true", "true")

    current = discover_shards(ROOT / "Baton.slnx")
    assert current["flow"] == [FLOW]
    assert len(current["other"]) == 4
    assert test_command(FLOW) == [
        sys.executable,
        str(ROOT / "tools/buildlock.py"),
        "dotnet",
        "test",
        "--project",
        str(FLOW),
        "--no-build",
        "--minimum-expected-tests",
        "1",
    ]


def check_pixi_and_workflow() -> None:
    pixi = tomllib.loads((ROOT / "pixi.toml").read_text(encoding="utf-8"))
    tasks = pixi["tasks"]
    assert tasks["test-flow"]["cmd"] == FLOW_COMMAND
    assert tasks["test-other"]["cmd"] == OTHER_COMMAND
    assert tasks["ci-selftest"]["cmd"] == "python tools/ci/selftest.py"
    assert tasks["gates-ci-test-complement-quiet"]["cmd"] == "python -u tools/gates/gates.py --ci --ci-test-shards-cover --quiet"

    workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
    matrix = workflow.split("      matrix:\n", 1)[1].split("    runs-on:", 1)[0]
    legs = re.findall(r'name: "([^"]+)", task: "([^"]+)"', matrix)
    assert legs == [("windows-shard-flow", "test-flow"), ("windows-shard-other", "test-other")]
    assert workflow.count("name: Snapshot ~/.baton before tests") == 1
    sentinel = workflow.split("name: Assert ~/.baton untouched by tests", 1)[1].split("- name:", 1)[0]
    assert "if: always()" in sentinel and "aer-before.txt" in sentinel and "aer-after.txt" in sentinel
    assert workflow.count("ref: ${{ github.sha }}") >= 4
    assert "run: pixi run ${{ steps.coverage.outputs.task }}" in workflow
    assert "run: python tools/ci/aggregate.py" in workflow

    gates_tree = ast.parse((ROOT / "tools/gates/gates.py").read_text(encoding="utf-8"))
    after_fast = next(
        node.value
        for node in gates_tree.body
        if isinstance(node, ast.Assign)
        and any(isinstance(target, ast.Name) and target.id == "AFTER_BUILD_FAST" for target in node.targets)
    )
    assert "ci-selftest" in ast.literal_eval(after_fast)


def check_aggregate_table() -> None:
    valid = [
        ("push", "skipped", "", "success", "success", "test-shard-complement"),
        ("pull_request", "success", "true", "success", "success", "test-shard-complement"),
        ("pull_request", "success", "false", "skipped", "success", "full"),
    ]
    for row in valid:
        assert not verify_coverage(*row), row

    base = valid[1]
    mutations = [
        base[:3] + ("failure",) + base[4:],
        base[:3] + ("cancelled",) + base[4:],
        base[:3] + ("skipped",) + base[4:],
        base[:4] + ("failure",) + base[5:],
        base[:4] + ("cancelled",) + base[5:],
        base[:4] + ("skipped",) + base[5:],
        (base[0], "failure") + base[2:],
        (base[0], "cancelled") + base[2:],
        base[:5] + ("",),
        base[:5] + ("full",),
        ("pull_request", "success", "false", "success", "success", "full"),
        ("pull_request", "success", "", "skipped", "success", "full"),
    ]
    for row in mutations:
        assert verify_coverage(*row), row


def main() -> None:
    check_discovery()
    check_pixi_and_workflow()
    check_aggregate_table()
    print("CI shard discovery, bindings, sentinels and aggregate truth table passed")


if __name__ == "__main__":
    main()
