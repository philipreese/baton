"""Controls for the narrow Fleet Glass CI classification and coverage (#2498)."""
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import tomllib
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]


def main():
    from page_changes import page_only
    spec = importlib.util.spec_from_file_location("gates", ROOT / "tools/gates/gates.py")
    gates = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(gates)
    from aggregate import verify_coverage

    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}

        def git(*args):
            return subprocess.check_output(["git", *args], cwd=root, env=env).decode().strip()

        git("init", "-q")
        git("config", "user.email", "fixture@example.invalid")
        git("config", "user.name", "fixture")
        page = root / "tools/fleet-glass/glass.html"
        page.parent.mkdir(parents=True)
        page.write_text("before")
        (root / "unknown.txt").write_text("before")
        git("add", ".")
        git("commit", "-qm", "base")
        base = git("rev-parse", "HEAD")
        assert not page_only(root, base, base)
        page.write_text("after")
        git("add", ".")
        git("commit", "-qm", "page")
        head = git("rev-parse", "HEAD")
        assert page_only(root, base, head)
        blob = git("rev-parse", "HEAD:tools/fleet-glass/glass.html")
        for mode in ("120000", "100755"):
            git("update-index", "--cacheinfo", mode, blob, "tools/fleet-glass/glass.html")
            git("commit", "-qm", "unsafe-mode")
            assert not page_only(root, head, git("rev-parse", "HEAD"))
        git("update-index", "--force-remove", "tools/fleet-glass/glass.html")
        git("commit", "-qm", "delete")
        deleted = git("rev-parse", "HEAD")
        assert not page_only(root, head, deleted)
        git("add", "tools/fleet-glass/glass.html")
        git("commit", "-qm", "add")
        assert not page_only(root, deleted, git("rev-parse", "HEAD"))
        (root / "unknown.txt").write_text("mixed")
        git("add", ".")
        git("commit", "-qm", "mixed")
        assert not page_only(root, base, git("rev-parse", "HEAD"))
        git("mv", str(page.relative_to(root)), "renamed.html")
        git("commit", "-qm", "rename")
        assert not page_only(root, head, git("rev-parse", "HEAD"))
        assert not page_only(root, head, base)  # merge-base to old head is empty
        for invalid in ("", "HEAD", "0" * 40):
            try:
                page_only(root, invalid, head)
            except (ValueError, subprocess.CalledProcessError):
                pass
            else:
                raise AssertionError("unreadable diff accepted")

    selected = gates.ci_member_set(page_only=True)
    assert set(gates._all_members()) - set(selected) == {
        "fmt-check", "lint", "vendor-check", "ci-selftest", "test-no-build",
    }
    assert all(gates.CI_PAGE_SKIP.values())
    assert set(gates.BUILD_PHASE).isdisjoint(selected)
    for member in ("fleet-glass-selftest", "fleet-glass-daemon-feed-selftest", "fleet-glass-service-worker-selftest", "ci-page-selftest"):
        assert member in selected
    gates.OVERLAP.append("new-member")
    try:
        assert "new-member" in gates.ci_member_set(page_only=True)
    finally:
        gates.OVERLAP.pop()

    # Exercise main's real population selection, with member execution replaced only at
    # the process boundary. Receipting a partial CI population would be a false claim.
    ran = []
    class Passed:
        returncode = 0
        def communicate(self):
            return b"", None

    def spawn(name):
        ran.append(name)
        return Passed()

    def run(name):
        ran.append(name)
        return 0

    def execute(after_build, runner, quiet, skip):
        return gates.run_all(after_build, spawner=spawn, runner=run, quiet=True, skip=skip)
    with patch.object(sys, "argv", ["gates.py", "--ci", "--ci-page-only"]), \
         patch.object(gates, "run_gates_and_shutdown", execute), \
         patch.object(gates, "telemetry_snapshot", return_value=None), \
         patch.object(gates, "write_telemetry"), patch.object(gates, "delete_receipt"), \
         patch.object(gates, "write_receipt", side_effect=AssertionError("CI wrote receipt")), \
         patch.object(gates, "record_run_members", side_effect=AssertionError("CI wrote member receipt")):
        assert gates.main() == 0
    assert ran == selected
    good = ("pull_request", "success", "false", "skipped", "success", "page-only", "true")
    assert not verify_coverage(*good)
    for index, values in enumerate(( ["push", "unknown"], ["failure", "skipped", ""], ["true", ""], ["success", "failure"], ["failure", "skipped"], ["full", "", "test-shard-complement"], ["false", "", "unknown"] )):
        for value in values:
            row = list(good)
            row[index] = value
            assert verify_coverage(*row), row
    from selftest import check_pixi_and_workflow, check_aggregate_table
    check_pixi_and_workflow()
    check_aggregate_table()
    tasks = tomllib.loads((ROOT / "pixi.toml").read_text(encoding="utf-8"))["tasks"]
    assert tasks["gates-ci-page-only-quiet"]["cmd"] == "python -u tools/gates/gates.py --ci --ci-page-only --quiet"
    workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
    assert "run: python tools/ci/page_changes.py" in workflow
    assert "PR_BASE_SHA: ${{ github.event.pull_request.base.sha }}" in workflow
    assert "PR_HEAD_SHA: ${{ github.event.pull_request.head.sha }}" in workflow
    assert workflow.count("PAGE_ONLY: ${{ needs.changes.outputs.page-only }}") == 2
    assert 'echo "task=gates-ci-page-only-quiet"' in workflow
    selection = workflow.split("name: Select authoritative coverage mode", 1)[1].split("      - uses:", 1)[0]
    script = selection.split("        run: |\n", 1)[1]
    script = "\n".join(line[10:] for line in script.splitlines())
    cases = [
        ("pull_request", "success", "false", "true", "page-only", "gates-ci-page-only-quiet"),
        ("pull_request", "success", "false", "false", "test-shard-complement", "gates-ci-test-complement-quiet"),
        ("pull_request", "success", "true", "true", "test-shard-complement", "gates-ci-test-complement-quiet"),
        ("pull_request", "success", "true", "false", "test-shard-complement", "gates-ci-test-complement-quiet"),
        ("pull_request", "failure", "false", "true", "test-shard-complement", "gates-ci-test-complement-quiet"),
        ("pull_request", "", "", "", "test-shard-complement", "gates-ci-test-complement-quiet"),
        ("pull_request", "success", "", "false", "test-shard-complement", "gates-ci-test-complement-quiet"),
        ("push", "skipped", "false", "true", "test-shard-complement", "gates-ci-test-complement-quiet"),
        ("workflow_dispatch", "skipped", "false", "true", "test-shard-complement", "gates-ci-test-complement-quiet"),
    ]
    # Use the same Git Bash discovery as existing Windows workflow controls.
    import shutil
    bash = shutil.which("bash")
    if os.name == "nt":
        git_bash = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe"
        if git_bash.exists():
            bash = str(git_bash)
    assert bash, "bash is required to test the actual workflow script"
    with tempfile.TemporaryDirectory() as directory:
        output = Path(directory) / "output"
        for event, changes, dotnet, page, mode, task in cases:
            output.write_text("")
            env = dict(os.environ, EVENT=event, CHANGES=changes, DOTNET=dotnet, PAGE_ONLY=page,
                       GITHUB_OUTPUT=output.as_posix())
            subprocess.run([bash, "-c", script], env=env, check=True, capture_output=True)
            assert output.read_text().splitlines() == [f"mode={mode}", f"task={task}"]
    print("Page-only CI classifier, membership and fail-closed aggregate controls passed")


if __name__ == "__main__":
    main()
