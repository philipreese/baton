"""Offline guards for the bounded GitHub Actions recovery routes (#2197)."""
from __future__ import annotations

from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
CI = ROOT / ".github" / "workflows" / "ci.yml"
RELEASE = ROOT / ".github" / "workflows" / "release-please.yml"
MAIN_REF = "refs/heads/main"


def dispatch_target_is_eligible(event_name: str, ref: str, sha: str, expected_sha: str) -> bool:
    """Model the recovery_target shell guard shared by both workflows."""
    return event_name != "workflow_dispatch" or (
        ref == MAIN_REF and bool(expected_sha) and sha == expected_sha
    )


def ci_is_complete(event_name: str, recovery_target: str, test: str, gates: str, pack: str) -> bool:
    """Model ci's failure and manual-completeness checks for the relevant jobs."""
    results = (recovery_target, test, gates)
    if any(result in {"failure", "cancelled"} for result in results):
        return False
    return event_name != "workflow_dispatch" or all(result == "success" for result in (test, gates, pack))


def require(text: str, fragment: str, path: Path) -> None:
    if fragment not in text:
        raise AssertionError(f"{path.relative_to(ROOT)} is missing {fragment!r}")


def check_workflow_shapes() -> None:
    for path in (CI, RELEASE):
        text = path.read_text(encoding="utf-8")
        require(text, "workflow_dispatch:", path)
        require(text, "expected_sha:", path)
        require(text, "github.ref }}", path)
        require(text, "refs/heads/main", path)
        require(text, "github.sha }}", path)
        require(text, "inputs.expected_sha }}", path)
        require(text, "Recovery dispatch must select main and name its exact current SHA.", path)

    ci = CI.read_text(encoding="utf-8")
    require(ci, "github.event_name == 'push' || github.event_name == 'workflow_dispatch'", CI)
    require(ci, "A recovery dispatch is incomplete", CI)
    require(ci, "needs: [recovery_target, changes, test, gates, pack]", CI)

    release = RELEASE.read_text(encoding="utf-8")
    require(release, "needs: recovery_target", RELEASE)
    require(release, "target-branch: main", RELEASE)


def check_event_conditions() -> None:
    assert dispatch_target_is_eligible("push", MAIN_REF, "a", "")
    assert dispatch_target_is_eligible("pull_request", "refs/pull/1/merge", "a", "")
    assert dispatch_target_is_eligible("workflow_dispatch", MAIN_REF, "a", "a")
    assert not dispatch_target_is_eligible("workflow_dispatch", MAIN_REF, "new-main", "old-main")
    assert not dispatch_target_is_eligible("workflow_dispatch", "refs/heads/topic", "a", "a")
    assert not dispatch_target_is_eligible("workflow_dispatch", MAIN_REF, "a", "")

    assert ci_is_complete("push", "success", "success", "success", "failure")
    assert ci_is_complete("workflow_dispatch", "success", "success", "success", "success")
    assert not ci_is_complete("workflow_dispatch", "success", "skipped", "success", "success")
    assert not ci_is_complete("workflow_dispatch", "success", "success", "success", "skipped")
    assert not ci_is_complete("workflow_dispatch", "failure", "skipped", "skipped", "skipped")


if __name__ == "__main__":
    check_workflow_shapes()
    check_event_conditions()
    print("workflow recovery self-test passed")
