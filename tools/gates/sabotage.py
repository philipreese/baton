"""Sabotage fixtures: prove each shell-out gate member goes red on violated property (#1601).

WHY THIS EXISTS
---------------
A green gate proves the gate ran, not that the property holds. Proof of concept already happened:
a quoting or configuration defect can make a check silently pass or skip without validating the
property it exists to enforce.

For each gates member that shells out (subprocess-based audit-* checkers), a sabotage fixture
deliberately violates the guarded property in an isolated temp directory, runs the member against it,
and asserts it exits non-zero.

The ratchet test enumerates all gates members from `tools/gates/gates.py` and fails if any member
lacks a sabotage fixture unless explicitly allowlisted with a one-line justification.
"""
from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Callable

ROOT = Path(__file__).resolve().parents[2]


def _clean_git_env() -> dict[str, str]:
    """Scrub GIT_* environment variables to prevent leaking outer repository context."""
    return {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}


def _init_git_repo(path: Path) -> None:
    """Initialize a throwaway git repository in path with dummy user credentials."""
    env = _clean_git_env()
    subprocess.run(["git", "init", "-q"], cwd=path, check=True, env=env)
    subprocess.run(["git", "config", "user.email", "sabotage@localhost"], cwd=path, check=True, env=env)
    subprocess.run(["git", "config", "user.name", "Sabotage"], cwd=path, check=True, env=env)


FIXTURES: dict[str, Callable[[], None]] = {}


def fixture(name: str):
    """Register a sabotage fixture for a named gate member."""
    def decorator(fn: Callable[[], None]):
        FIXTURES[name] = fn
        return fn
    return decorator


@fixture("audit-completeness")
def _sabotage_audit_completeness() -> None:
    with tempfile.TemporaryDirectory() as td:
        dest = Path(td)
        tools_dir = dest / "tools" / "audit-completeness"
        tools_dir.mkdir(parents=True)
        shutil.copy2(ROOT / "tools" / "audit-completeness" / "completeness.py", tools_dir / "completeness.py")
        (dest / "CLAUDE.md").write_text("# Baton\n", encoding="utf-8")

        proc = subprocess.run(
            [sys.executable, "-u", str(tools_dir / "completeness.py")],
            cwd=dest,
            capture_output=True,
            text=True,
            env=_clean_git_env(),
        )
        assert proc.returncode != 0, (
            f"audit-completeness exited {proc.returncode} on empty/violating tree; expected non-zero"
        )


@fixture("audit-recordonce")
def _sabotage_audit_recordonce() -> None:
    with tempfile.TemporaryDirectory() as td:
        dest = Path(td)
        _init_git_repo(dest)
        tools_dir = dest / "tools" / "audit-completeness"
        tools_dir.mkdir(parents=True)
        shutil.copy2(ROOT / "tools" / "audit-completeness" / "recordonce.py", tools_dir / "recordonce.py")
        shutil.copy2(ROOT / "tools" / "audit-completeness" / "completeness.py", tools_dir / "completeness.py")

        (dest / "doc1.md").write_text("initial text\n", encoding="utf-8")
        env = _clean_git_env()
        subprocess.run(["git", "add", "."], cwd=dest, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "init"], cwd=dest, check=True, env=env)

        duplicate_sentence = "This is a duplicated sentence that should definitely be flagged by the record once checker.\n"
        (dest / "doc1.md").write_text("initial text\n" + duplicate_sentence, encoding="utf-8")
        (dest / "doc2.md").write_text("other text\n" + duplicate_sentence, encoding="utf-8")
        subprocess.run(["git", "add", "."], cwd=dest, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "add duplication"], cwd=dest, check=True, env=env)

        proc = subprocess.run(
            [sys.executable, "-u", str(tools_dir / "recordonce.py"), "HEAD~1"],
            cwd=dest,
            capture_output=True,
            text=True,
            env=env,
        )
        assert proc.returncode != 0, (
            f"audit-recordonce exited {proc.returncode} on duplicated wording; expected non-zero"
        )


@fixture("audit-waitceiling")
def _sabotage_audit_waitceiling() -> None:
    with tempfile.TemporaryDirectory() as td:
        dest = Path(td)
        _init_git_repo(dest)
        tools_dir = dest / "tools" / "audit-completeness"
        tools_dir.mkdir(parents=True)
        shutil.copy2(ROOT / "tools" / "audit-completeness" / "waitceiling.py", tools_dir / "waitceiling.py")

        tests_dir = dest / "tests"
        tests_dir.mkdir(parents=True)
        (tests_dir / "FooTests.cs").write_text("// base\n", encoding="utf-8")
        env = _clean_git_env()
        subprocess.run(["git", "add", "."], cwd=dest, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "init"], cwd=dest, check=True, env=env)

        (tests_dir / "FooTests.cs").write_text(
            "class FooTests {\n    void Test() {\n        Task.Delay(TimeSpan.FromSeconds(5));\n    }\n}\n",
            encoding="utf-8",
        )
        subprocess.run(["git", "add", "."], cwd=dest, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "add short wait"], cwd=dest, check=True, env=env)

        proc = subprocess.run(
            [sys.executable, "-u", str(tools_dir / "waitceiling.py"), "HEAD~1"],
            cwd=dest,
            capture_output=True,
            text=True,
            env=env,
        )
        assert proc.returncode != 0, (
            f"audit-waitceiling exited {proc.returncode} on sub-60s wait ceiling; expected non-zero"
        )


@fixture("audit-retiredphrases")
def _sabotage_audit_retiredphrases() -> None:
    with tempfile.TemporaryDirectory() as td:
        dest = Path(td)
        tools_dir = dest / "tools" / "audit-completeness"
        tools_dir.mkdir(parents=True)
        shutil.copy2(ROOT / "tools" / "audit-completeness" / "retiredphrases.py", tools_dir / "retiredphrases.py")

        docs_dir = dest / "docs"
        docs_dir.mkdir(parents=True)
        phrase = "standing " + "grant"
        (docs_dir / "violating.md").write_text(f"This mentions {phrase} without marker.\n", encoding="utf-8")

        proc = subprocess.run(
            [sys.executable, "-u", str(tools_dir / "retiredphrases.py")],
            cwd=dest,
            capture_output=True,
            text=True,
            env=_clean_git_env(),
        )
        assert proc.returncode != 0, (
            f"audit-retiredphrases exited {proc.returncode} on retired phrase; expected non-zero"
        )


@fixture("audit-docsbudget")
def _sabotage_audit_docsbudget() -> None:
    with tempfile.TemporaryDirectory() as td:
        dest = Path(td)
        _init_git_repo(dest)
        tools_dir = dest / "tools" / "audit-completeness"
        tools_dir.mkdir(parents=True)
        shutil.copy2(ROOT / "tools" / "audit-completeness" / "docsbudget.py", tools_dir / "docsbudget.py")
        (tools_dir / "docs-allowlist.txt").write_text("allowed.md\n", encoding="utf-8")

        (dest / "allowed.md").write_text("# Allowed\n", encoding="utf-8")
        (dest / "unbudgeted.md").write_text("# Unbudgeted\n", encoding="utf-8")
        env = _clean_git_env()
        subprocess.run(["git", "add", "."], cwd=dest, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", "init"], cwd=dest, check=True, env=env)

        proc = subprocess.run(
            [sys.executable, "-u", str(tools_dir / "docsbudget.py")],
            cwd=dest,
            capture_output=True,
            text=True,
            env=env,
        )
        assert proc.returncode != 0, (
            f"audit-docsbudget exited {proc.returncode} on unbudgeted markdown; expected non-zero"
        )


@fixture("audit-speccitations")
def _sabotage_audit_speccitations() -> None:
    with tempfile.TemporaryDirectory() as td:
        dest = Path(td)
        tools_dir = dest / "tools" / "audit-completeness"
        tools_dir.mkdir(parents=True)
        shutil.copy2(ROOT / "tools" / "audit-completeness" / "speccitations.py", tools_dir / "speccitations.py")

        spec_dir = dest / "spec"
        spec_dir.mkdir(parents=True)
        (spec_dir / "baton.md").write_text("See Program.cs:123 for parser details.\n", encoding="utf-8")

        proc = subprocess.run(
            [sys.executable, "-u", str(tools_dir / "speccitations.py")],
            cwd=dest,
            capture_output=True,
            text=True,
            env=_clean_git_env(),
        )
        assert proc.returncode != 0, (
            f"audit-speccitations exited {proc.returncode} on line-number citation; expected non-zero"
        )


@fixture("audit-commentspecrefs")
def _sabotage_audit_commentspecrefs() -> None:
    with tempfile.TemporaryDirectory() as td:
        dest = Path(td)
        tools_dir = dest / "tools" / "audit-completeness"
        tools_dir.mkdir(parents=True)
        shutil.copy2(ROOT / "tools" / "audit-completeness" / "commentspecrefs.py", tools_dir / "commentspecrefs.py")

        spec_dir = dest / "spec"
        spec_dir.mkdir(parents=True)
        (spec_dir / "baton.md").write_text("# §1 Section\n", encoding="utf-8")

        src_dir = dest / "src"
        src_dir.mkdir(parents=True)
        (src_dir / "Foo.cs").write_text("// per §999 not a real section\n", encoding="utf-8")

        proc = subprocess.run(
            [sys.executable, "-u", str(tools_dir / "commentspecrefs.py")],
            cwd=dest,
            capture_output=True,
            text=True,
            env=_clean_git_env(),
        )
        assert proc.returncode != 0, (
            f"audit-commentspecrefs exited {proc.returncode} on unresolved section; expected non-zero"
        )


@fixture("audit-clitripwire")
def _sabotage_audit_clitripwire() -> None:
    with tempfile.TemporaryDirectory() as td:
        dest = Path(td)
        tools_dir = dest / "tools" / "audit-completeness"
        tools_dir.mkdir(parents=True)
        shutil.copy2(ROOT / "tools" / "audit-completeness" / "clitripwire.py", tools_dir / "clitripwire.py")

        cli_dir = dest / "src" / "Baton.Cli"
        cli_dir.mkdir(parents=True)
        (cli_dir / "Program.cs").write_text(
            'namespace Baton.Cli;\npublic static class Program {\n'
            '    public static readonly string[] knownSubcommands = new[] { "run", "status", "cancel", "resume", "decide" };\n'
            '}\n',
            encoding="utf-8",
        )

        parsers = [
            ("RunOptionsParser.cs", 'Usage: baton run <workflow-file> --bindings <bindings-file> [--room-dir <dir>]'),
            ("StatusOptionsParser.cs", 'Usage: baton status <room-dir>'),
            ("CancelOptionsParser.cs", 'Usage: baton cancel <room-dir>'),
            ("ResumeOptionsParser.cs", 'Usage: baton resume <room-dir> --worker <role> --message <text>'),
            ("DecideOptionsParser.cs", 'Usage: baton decide <room-dir> --decision <verdict>'),
        ]
        for fname, usage in parsers:
            cls_name = fname.replace(".cs", "")
            (cli_dir / fname).write_text(
                f'namespace Baton.Cli;\npublic static class {cls_name} {{\n'
                f'    public const string Usage = "{usage}";\n'
                f'}}\n',
                encoding="utf-8",
            )

        doc_dir = dest / "docs" / "agents"
        doc_dir.mkdir(parents=True)
        doc_lines = [
            "```",
            "baton run wf.json --bindings b.json --nonexistent-sabotage-flag",
            "```",
            "`baton status /tmp/room`",
            "`baton cancel /tmp/room`",
            "`baton resume /tmp/room --worker impl --message hello`",
            "`baton decide /tmp/room --decision accept`",
            "`baton run wf1.json --bindings b.json`",
            "`baton run wf2.json --bindings b.json`",
            "`baton run wf3.json --bindings b.json`",
            "`baton run wf4.json --bindings b.json`",
            "`baton run wf5.json --bindings b.json`",
        ]
        (doc_dir / "invoking-baton.md").write_text("\n".join(doc_lines) + "\n", encoding="utf-8")

        proc = subprocess.run(
            [sys.executable, "-u", str(tools_dir / "clitripwire.py")],
            cwd=dest,
            capture_output=True,
            text=True,
            env=_clean_git_env(),
        )
        assert proc.returncode != 0, (
            f"audit-clitripwire exited {proc.returncode} on unknown flag; expected non-zero"
        )


@fixture("audit-selfcheck")
def _sabotage_audit_selfcheck() -> None:
    with tempfile.TemporaryDirectory() as td:
        dest = Path(td)
        tools_dir = dest / "tools" / "audit-completeness"
        tools_dir.mkdir(parents=True)
        shutil.copy2(ROOT / "tools" / "audit-completeness" / "selfcheck.py", tools_dir / "selfcheck.py")
        shutil.copy2(ROOT / "tools" / "audit-completeness" / "recordonce.py", tools_dir / "recordonce.py")

        # #1759: this used to break the dispatch.py TEMPLATES population selfcheck.py's own
        # _templates_are_dispatchable read; that check (and dispatch.py) is gone. An empty
        # register_models() population trips the same "TEMPLATES is empty -- this compared
        # nothing"-shaped assert on the surviving checks that still call it (_shapes_discriminate,
        # _no_transcribed_counts) -- register_models() itself asserts `accepted is not None`.
        completeness_src = (ROOT / "tools" / "audit-completeness" / "completeness.py").read_text(encoding="utf-8")
        mutated = completeness_src.replace(
            "def register_models():\n",
            'def register_models():\n    return None, "sabotage: register_models neutered"\n',
            1,
        )
        assert mutated != completeness_src, "register_models mutation target not found in completeness.py"
        (tools_dir / "completeness.py").write_text(mutated, encoding="utf-8")

        proc = subprocess.run(
            [sys.executable, "-u", str(tools_dir / "selfcheck.py")],
            cwd=dest,
            capture_output=True,
            text=True,
            env=_clean_git_env(),
        )
        assert proc.returncode != 0, (
            f"audit-selfcheck exited {proc.returncode} on a neutered register_models(); expected non-zero"
        )


@fixture("diff-shape-selftest")
def _sabotage_diff_shape_selftest() -> None:
    with tempfile.TemporaryDirectory() as td:
        dest = Path(td)
        tools_dir = dest / "tools" / "diff-shape"
        tools_dir.mkdir(parents=True)
        src = (ROOT / "tools" / "diff-shape" / "diff_shape.py").read_text(encoding="utf-8")
        # Mutate is_protected_tooling to always report nothing protected -- the selftest's own
        # widened-path arms (l)-(p) and (s)-(w) assert those protected-tooling edits fail, so a
        # neutered predicate must go red (#1744: pixi.toml itself no longer routes through this
        # function, so those arms -- not (d) -- are what a neutered predicate now flips).
        mutated = src.replace(
            "def is_protected_tooling(path: str) -> bool:\n"
            '    """Check if path belongs to the protected-tooling set (whole-file/directory half -- pixi.toml\n'
            '    is handled separately, at line level, by _pixi_toml_protected_hunk_touched)."""\n'
            "    p = path.replace(\"\\\\\", \"/\")",
            "def is_protected_tooling(path: str) -> bool:\n"
            '    """Check if path belongs to the protected-tooling set (whole-file/directory half -- pixi.toml\n'
            '    is handled separately, at line level, by _pixi_toml_protected_hunk_touched)."""\n'
            "    return False\n"
            "    p = path.replace(\"\\\\\", \"/\")",
        )
        assert mutated != src, "is_protected_tooling mutation target not found in diff_shape.py"
        (tools_dir / "diff_shape.py").write_text(mutated, encoding="utf-8")

        proc = subprocess.run(
            [sys.executable, "-u", str(tools_dir / "diff_shape.py"), "--selftest"],
            cwd=dest,
            capture_output=True,
            text=True,
            env=_clean_git_env(),
        )
        assert proc.returncode != 0, (
            f"diff-shape --selftest exited {proc.returncode} with is_protected_tooling neutered; expected non-zero"
        )


@fixture("audit-controls")
def _sabotage_audit_controls() -> None:
    with tempfile.TemporaryDirectory() as td:
        dest = Path(td)
        tools_dir = dest / "tools" / "audit-completeness"
        tools_dir.mkdir(parents=True)
        shutil.copy2(ROOT / "tools" / "audit-completeness" / "controls.py", tools_dir / "controls.py")
        shutil.copy2(ROOT / "tools" / "audit-completeness" / "selfcheck.py", tools_dir / "selfcheck.py")
        shutil.copy2(ROOT / "tools" / "audit-completeness" / "completeness.py", tools_dir / "completeness.py")
        shutil.copy2(ROOT / "tools" / "audit-completeness" / "recordonce.py", tools_dir / "recordonce.py")

        # Mutate controls.py to remove a control decorator, tripping the uncontrolled-check check.
        # #1759: retargeted off the dispatch.py-backed "every gemini template pins..." check (gone
        # along with dispatch.py) onto a surviving single-control check.
        controls_src = (ROOT / "tools" / "audit-completeness" / "controls.py").read_text(encoding="utf-8")
        mutated = controls_src.replace(
            '@control("step 9\'s probe-input exemption excuses a marked line and nothing else",',
            '# @control("step 9\'s probe-input exemption excuses a marked line and nothing else",',
        )
        assert mutated != controls_src, "control decorator mutation target not found in controls.py"
        (tools_dir / "controls.py").write_text(mutated, encoding="utf-8")

        proc = subprocess.run(
            [sys.executable, "-u", str(tools_dir / "controls.py")],
            cwd=dest,
            capture_output=True,
            text=True,
            env=_clean_git_env(),
        )
        assert proc.returncode != 0, (
            f"audit-controls exited {proc.returncode} on missing control; expected non-zero"
        )


# Allowlist for gate members where sabotage is not meaningful, each with a one-line justification.
ALLOWLIST: dict[str, str] = {
    "audit-staleness-ext-selftest": "pure synthetic selftest already proving internal polarity without live GitHub dependencies",
    "audit-waitceiling-selftest": "pure synthetic selftest exercising 8 red/green discrimination arms",
    "audit-retiredphrases-selftest": "pure synthetic selftest exercising regex and marker polarity arms",
    "audit-docsbudget-selftest": "pure synthetic selftest exercising allowlist discrimination",
    "audit-speccitations-selftest": "pure synthetic selftest exercising citation pattern discrimination",
    "audit-commentspecrefs-selftest": "pure synthetic selftest exercising comment reference resolution polarity",
    "audit-clitripwire-selftest": "pure synthetic selftest exercising 7 CLI parser and doc drift arms",
    "flake-watch-selftest": "pure synthetic selftest exercising 5 flake disagreement discrimination arms",
    "buildlock-selftest": "pure synthetic selftest exercising subprocess serialization, crash safety, and timeouts",
    "tool-refresh-selftest": "pure synthetic selftest exercising drain-predicate classification, version-compare, fail-loud-on-failure, and --dry-run discrimination arms, each against injected fakes",
    "gates-selftest": "pure synthetic selftest exercising aggregation polarity plus the #1636 gate-receipt and pre-push-hook discrimination arms",
    "fmt-check": "MSBuild-driven code format verification where sabotage requires compiling the .NET solution",
    "lint": "MSBuild compiler warning-as-error gate where sabotage requires compiling the .NET solution",
    "vendor-check": "dotnet CLI probe runner that queries host CLI versions under grace-window semantics",
    "test-no-build": "full xUnit test suite runner where sabotage is covered by individual test assertions",
    "gate-sabotage": "the sabotage suite and ratchet runner itself; self-tested by executing all sabotage fixtures",
    "fleet-glass-worker-selftest": "pure synthetic selftest already proving red/green discrimination for the heartbeat merge (#1656 F2) and cursor/limit/count polarity, over in-memory fixtures with no live Worker",
    "launcher-selftest": "pure synthetic selftest exercising fail-closed pointer arms plus real argv-forwarding/exit-code/pointer-flip discrimination (#1670 F2) against a compiled mock exe fixture under a temp BATON_HOME",
    "deepswe-derived-check": "paired scratch-copy selftest invokes the exact --check-all entry point and proves edited and missing derived outputs exit non-zero",
    "deepswe-derived-check-selftest": "scratch-copy selftest exercises every current snapshot through production --check-all without touching the benchmark tree",
    "vendor-verify-selftest": "pure synthetic selftest running each local vendor-verify check against a faithful fixture that must PASS and a mutated one that must NOT, plus the sqlite temp-copy location/loud-cleanup and pbtxt parser-limitation arms and #1928's agy tool-classification arms over synthetic catalogues; refuses a local check with no fixture registered, and fails closed if either suite fails",
}


def check_ratchet(gates_members: list[str]) -> list[str]:
    """Ratchet: fails naming any gate member without a sabotage fixture or allowlist entry (#1601)."""
    faults: list[str] = []
    all_members = set(gates_members)
    covered = set(FIXTURES.keys()) | set(ALLOWLIST.keys())

    uncovered = sorted(all_members - covered)
    for m in uncovered:
        faults.append(
            f"gate member '{m}' has no sabotage fixture registered and is not on the allowlist (see issue #1601)"
        )

    both = sorted(set(FIXTURES.keys()) & set(ALLOWLIST.keys()))
    for m in both:
        faults.append(
            f"gate member '{m}' has a sabotage fixture registered but is also present in the allowlist"
        )

    orphans = sorted(set(ALLOWLIST.keys()) - all_members)
    for m in orphans:
        faults.append(
            f"allowlist entry '{m}' does not exist in gates.py members"
        )

    return faults


def _load_gate_members() -> list[str]:
    sys.path.insert(0, str(ROOT / "tools" / "gates"))
    import gates  # type: ignore[import-not-found]
    return sorted(set(gates.OVERLAP + gates.BUILD_PHASE + gates.AFTER_BUILD_FULL))


def run_all_fixtures() -> tuple[int, list[str]]:
    passed = 0
    failures: list[str] = []
    for name, fn in sorted(FIXTURES.items()):
        try:
            fn()
            passed += 1
            print(f"  OK  sabotage verified: {name} exits non-zero on violating input")
        except Exception as ex:  # noqa: BLE001
            failures.append(f"{name}: {ex}")
            print(f"  !!  sabotage FAILED: {name} -- {ex}")
    return passed, failures


def selftest() -> int:
    """Selftest for the ratchet logic: prove it trips on unlisted, orphaned, or duplicated members."""
    failures: list[str] = []

    # 1. Uncovered member trips ratchet and names the member citing #1601
    faults_uncovered = check_ratchet(["uncovered-gate-member", "fmt-check"])
    if not any("uncovered-gate-member" in f and "1601" in f for f in faults_uncovered):
        failures.append("ratchet did not trip on uncovered gate member citing #1601")

    # 2. Member in both FIXTURES and ALLOWLIST trips ratchet
    orig_allowlist = dict(ALLOWLIST)
    try:
        ALLOWLIST["audit-completeness"] = "duplicate entry test"
        faults_both = check_ratchet(["audit-completeness", "fmt-check"])
        if not any("audit-completeness" in f and "both" in f.lower() or "already" in f.lower() or "present" in f.lower() for f in faults_both):
            failures.append("ratchet did not trip on member present in both fixtures and allowlist")
    finally:
        ALLOWLIST.clear()
        ALLOWLIST.update(orig_allowlist)

    # 3. Orphan allowlist entry trips ratchet
    faults_orphan = check_ratchet(["audit-completeness"])  # Missing fmt-check and other allowlisted items
    if not any("not exist" in f for f in faults_orphan):
        failures.append("ratchet did not trip on orphaned allowlist entries")

    # 4. Valid set passes cleanly
    valid_members = _load_gate_members()
    faults_valid = check_ratchet(valid_members)
    if faults_valid:
        failures.append(f"valid gates members failed ratchet: {faults_valid}")

    if failures:
        print(f"gate-sabotage: selftest FAIL -- {'; '.join(failures)}", file=sys.stderr)
        return 1

    print("gate-sabotage: selftest OK (all 4 ratchet arms discriminate)")
    return 0


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    if "--selftest" in argv:
        return selftest()

    print("gate-sabotage: verifying sabotage fixtures and gates ratchet (#1601)")
    members = _load_gate_members()
    print(f"gate-sabotage: {len(members)} gates members found in tools/gates/gates.py")

    ratchet_faults = check_ratchet(members)
    if ratchet_faults:
        print(f" !! ratchet tripped ({len(ratchet_faults)} problem(s)):", file=sys.stderr)
        for f in ratchet_faults:
            print(f"      {f}", file=sys.stderr)
        return 1

    passed, fixture_failures = run_all_fixtures()
    print(f"gate-sabotage: {passed} fixture(s) passed, {len(ALLOWLIST)} allowlisted member(s)")

    if fixture_failures:
        print(f" !! {len(fixture_failures)} sabotage fixture(s) failed:", file=sys.stderr)
        for f in fixture_failures:
            print(f"      {f}", file=sys.stderr)
        return 1

    print(f" OK every shell-out gate member ({passed}) goes red on sabotage; all other members ({len(ALLOWLIST)}) allowlisted")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
