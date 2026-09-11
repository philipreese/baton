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

import importlib.util
import os
import shutil
import subprocess
import sys
import tempfile
import time
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


# Match the bounded Windows access-refusal policy established by the snapshot atomic-swap cleanup:
# exponential backoff from 10 ms to 200 ms, with about one second of total waiting (#2095).
TEMP_TREE_RETRY_FIRST_DELAY_S = 0.01
TEMP_TREE_RETRY_MAX_DELAY_S = 0.2
TEMP_TREE_RETRY_BUDGET_S = 1.0


def _remove_temp_tree(
    path: Path,
    remove: Callable[[Path], object] = shutil.rmtree,
    sleep: Callable[[float], object] = time.sleep,
    retry_permission_errors: bool = os.name == "nt",
) -> int:
    """Remove a fixture tree, retrying only bounded Windows access refusals."""
    attempts, waited, delay = 0, 0.0, TEMP_TREE_RETRY_FIRST_DELAY_S
    while True:
        attempts += 1
        try:
            remove(path)
            return attempts
        except PermissionError as error:
            if not retry_permission_errors:
                raise
            if waited >= TEMP_TREE_RETRY_BUDGET_S:
                error.add_note(
                    f"temporary fixture cleanup of {path} still refused after {attempts} "
                    f"attempts over {waited:.2f} s; a live resource owner may remain"
                )
                raise
            sleep(delay)
            waited += delay
            delay = min(delay * 2, TEMP_TREE_RETRY_MAX_DELAY_S)


def _run_temp_tree_fixture(
    prefix: str,
    body: Callable[[Path], None],
    make: Callable[..., str] = tempfile.mkdtemp,
    remove: Callable[[Path], object] = _remove_temp_tree,
) -> None:
    """Run a fixture body and preserve both body and cleanup failures."""
    root = Path(make(prefix=prefix))
    try:
        body(root)
    except Exception as primary:
        try:
            remove(root)
        except Exception as cleanup:
            raise ExceptionGroup(
                "fixture assertion and temporary-tree cleanup both failed",
                [primary, cleanup],
            ) from None
        raise
    else:
        remove(root)


FIXTURES: dict[str, Callable[[], None]] = {}


def fixture(name: str):
    """Register a sabotage fixture for a named gate member."""
    def decorator(fn: Callable[[], None]):
        FIXTURES[name] = fn
        return fn
    return decorator


@fixture("workflow-recovery-selftest")
def _sabotage_workflow_recovery(
    run_in_temp_tree: Callable[[str, Callable[[Path], None]], None] = _run_temp_tree_fixture,
) -> None:
    def exercise(dest: Path) -> None:
        for relative in ["tools/workflow-recovery/selftest.py", ".github/workflows/ci.yml",
                         ".github/workflows/release-please.yml", "pixi.toml", "tools/gates/gates.py"]:
            target = dest / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / relative, target)
        def run(control: str | None = None) -> tuple[subprocess.CompletedProcess, float]:
            command = [sys.executable, "-B", str(dest / "tools/workflow-recovery/selftest.py")]
            if control is not None:
                command.append(control)
            started = time.perf_counter()
            result = subprocess.run(
                command, cwd=dest, capture_output=True, text=True, timeout=30)
            return result, time.perf_counter() - started
        baseline, baseline_seconds = run()
        assert baseline.returncode == 0, baseline.stdout + baseline.stderr
        workflow = dest / ".github/workflows/ci.yml"
        original = workflow.read_text(encoding="utf-8")
        mutant_seconds: list[tuple[str, float]] = []
        for before, after, control in [
            ('[ "$live_sha" != "$EXPECTED_SHA" ]', '[ "$live_sha" = "$EXPECTED_SHA" ]', "ci-live-sha"),
            ("needs.test.result == 'success'", "needs.test.result != 'success'", "ci-pack-success"),
        ]:
            assert before in original
            workflow.write_text(original.replace(before, after), encoding="utf-8")
            mutated, seconds = run(control)
            mutant_seconds.append((control, seconds))
            assert mutated.returncode != 0 and "AssertionError" in mutated.stderr, mutated.stderr
        workflow.write_text(original, encoding="utf-8")
        release_workflow = dest / ".github/workflows/release-please.yml"
        release_original = release_workflow.read_text(encoding="utf-8")
        release_workflow.write_text(
            release_original.replace("RELEASE_RESULT: ${{ needs.release-please.result }}",
                                     "RELEASE_RESULT: success"), encoding="utf-8")
        mutated, seconds = run("release-result-input")
        mutant_seconds.append(("release-result-input", seconds))
        assert mutated.returncode != 0 and "AssertionError" in mutated.stderr, mutated.stderr
        timings = ", ".join(f"{name}={seconds:.3f}s" for name, seconds in mutant_seconds)
        print(f"  workflow-recovery sabotage timing: baseline={baseline_seconds:.3f}s, {timings}")

    run_in_temp_tree("workflow-recovery-sabotage-", exercise)


@fixture("ci-selftest")
def _sabotage_ci_selftest() -> None:
    """Prove the CI selftest rejects a fail-open same-revision coverage aggregate."""
    with tempfile.TemporaryDirectory() as td:
        dest = Path(td)
        tools_dir = dest / "tools" / "ci"
        tools_dir.mkdir(parents=True)
        for name in ["aggregate.py", "selftest.py", "test_shards.py"]:
            shutil.copy2(ROOT / "tools" / "ci" / name, tools_dir / name)

        # Exercise the production aggregate assertions without selftest.main()'s unrelated live
        # project identity evaluations and workflow checks. Only aggregate.py below is sabotaged,
        # in the isolated copy imported by this driver.
        driver = tools_dir / "sabotage_driver.py"
        driver.write_text(
            "import selftest\n"
            "selftest.check_aggregate_table()\n",
            encoding="utf-8",
        )

        def run() -> tuple[subprocess.CompletedProcess, float]:
            started = time.perf_counter()
            result = subprocess.run(
                [sys.executable, "-B", str(driver)],
                cwd=dest,
                capture_output=True,
                text=True,
                env=_clean_git_env(),
                timeout=60,
            )
            return result, time.perf_counter() - started

        baseline, baseline_seconds = run()
        assert baseline.returncode == 0, "control failed before sabotage:\n" + baseline.stdout + baseline.stderr

        aggregate = tools_dir / "aggregate.py"
        original = aggregate.read_text(encoding="utf-8")
        mutated = original.replace(
            '    if gates != "success":\n',
            '    if False:  # sabotage: accept a non-success gates result\n',
            1,
        )
        assert mutated != original, "gates-result mutation target not found in aggregate.py"
        aggregate.write_text(mutated, encoding="utf-8")

        sabotaged, sabotaged_seconds = run()
        assert sabotaged.returncode != 0 and "AssertionError" in sabotaged.stderr, (
            sabotaged.stdout + sabotaged.stderr
        )
        print(
            f"  ci-selftest aggregate timing: control={baseline_seconds:.3f}s, "
            f"mutant={sabotaged_seconds:.3f}s"
        )

@fixture("audit-completeness")
def _sabotage_audit_completeness() -> None:
    with tempfile.TemporaryDirectory() as td:
        dest = Path(td)
        tools_dir = dest / "tools" / "audit-completeness"
        tools_dir.mkdir(parents=True)
        shutil.copy2(ROOT / "tools" / "audit-completeness" / "completeness.py", tools_dir / "completeness.py")
        guide = dest / "docs" / "agents" / "developing-baton.md"
        guide.parent.mkdir(parents=True)
        guide_text = (
            "# Developing Baton\n\n"
            "**1. Known gate — `known-gate`.**\n"
            "**2. Common sense — `common-sense`.**\n"
            "**3. Record once — `record-once`.**\n")
        guide.write_text(guide_text, encoding="utf-8")

        verify = dest / "tools" / "vendor-verify" / "verify.py"
        verify.parent.mkdir(parents=True)
        verify.write_text('@check("known.real-check")\n', encoding="utf-8")
        agents = dest / "AGENTS.md"
        known_slug = "known-" + "gate"
        clean_agents = f"Use gate `{known_slug}`; evidence is known.real-check.\n"
        agents.write_text(clean_agents, encoding="utf-8")

        module_spec = importlib.util.spec_from_file_location(
            "sabotage_completeness", tools_dir / "completeness.py")
        assert module_spec is not None and module_spec.loader is not None
        completeness = importlib.util.module_from_spec(module_spec)
        module_spec.loader.exec_module(completeness)

        previous_cwd = Path.cwd()
        os.chdir(dest)
        try:
            assert completeness.step8_cited_checks_exist(), (
                "STEP 8 baseline failed before AGENTS.md vendor-check sabotage")
            assert completeness.step10_gate_citations(), (
                "STEP 10 baseline failed before AGENTS.md gate-citation sabotage")

            agents.write_text("Evidence is known.not-a-check.\n", encoding="utf-8")
            assert not completeness.step8_cited_checks_exist(), (
                "STEP 8 ignored a fabricated vendor-check citation in AGENTS.md")

            numeric_citation = "See " + "gate " + "1.\n"
            agents.write_text(numeric_citation, encoding="utf-8")
            assert not completeness.step10_gate_citations(), (
                "STEP 10 ignored a numeric gate citation in AGENTS.md")

            agents.write_text(clean_agents, encoding="utf-8")
            (dest / "CLAUDE.md").write_text(
                "# Bridge\n\n**1. Bridge-only gate — `bridge-gate`.**\n", encoding="utf-8")
            guide.unlink()
            assert not completeness.step10_gate_citations(), (
                "STEP 10 accepted CLAUDE.md when the canonical development guide was absent")
        finally:
            os.chdir(previous_cwd)

        agents.write_text(clean_agents, encoding="utf-8")
        guide.write_text(guide_text, encoding="utf-8")

        # Retain the member-level shell-out check after the targeted, otherwise-green controls above.
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

        phrase = "standing " + "grant"
        (dest / "AGENTS.md").write_text(
            f"This mentions {phrase} without marker.\n", encoding="utf-8")

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


@fixture("fleet-glass-selftest")
def _sabotage_fleet_glass_selftest() -> None:
    """#1912. A fixture rather than an allowlist entry, because this member has the one failure mode
    an allowlist cannot speak to: it slices its subject out of `glass.html` by marker, so a rename or
    a gutted block would leave it passing while testing nothing. Two arms, and the second is the one
    that earns the fixture -- a broken PANEL must red it, AND a missing MARKER must red it too."""
    glass_src = (ROOT / "tools" / "fleet-glass" / "glass.html").read_text(encoding="utf-8")
    selftest_src = (ROOT / "tools" / "fleet-glass" / "glass.selftest.mjs").read_text(encoding="utf-8")

    def run(glass_text: str, arm: str) -> None:
        with tempfile.TemporaryDirectory() as td:
            dest = Path(td)
            (dest / "glass.html").write_text(glass_text, encoding="utf-8")
            (dest / "glass.selftest.mjs").write_text(selftest_src, encoding="utf-8")
            proc = subprocess.run(
                ["node", str(dest / "glass.selftest.mjs")],
                cwd=dest,
                capture_output=True,
                text=True,
                env=_clean_git_env(),
            )
            assert proc.returncode != 0, (
                f"fleet-glass-selftest exited {proc.returncode} on {arm}; expected non-zero"
            )

    # Arm 1: the panel still renders, but stops marking which item launches next.
    broken_panel = glass_src.replace('class="q-next"', 'class="q-plain"')
    assert broken_panel != glass_src, "q-next mutation target not found in glass.html"
    run(broken_panel, "a panel that no longer marks the next item")

    # Arm 2: the extraction anchor is gone. Without this arm a marker rename would silently make the
    # member vacuous, which is exactly the failure the member cannot detect about itself.
    no_marker = glass_src.replace(">>> QUEUE-PANEL-BEGIN", ">>> QUEUE-PANEL-RENAMED")
    assert no_marker != glass_src, "QUEUE-PANEL-BEGIN marker not found in glass.html"
    run(no_marker, "a renamed extraction marker")


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
    "buildlock-selftest": "pure synthetic selftest exercising subprocess serialization, crash safety, timeouts, priority-class discrimination, and (#2010) the replay opt-in, invalidation, expiry and prune arms against a throwaway git repository under a temp dir",
    "tool-refresh-selftest": "pure synthetic selftest exercising drain-predicate classification, version-compare, fail-loud-on-failure, and --dry-run discrimination arms, each against injected fakes",
    "gates-selftest": "pure synthetic selftest exercising aggregation polarity plus the #1636 gate-receipt and pre-push-hook discrimination arms",
    "fmt-check": "MSBuild-driven code format verification where sabotage requires compiling the .NET solution",
    "lint": "MSBuild compiler warning-as-error gate where sabotage requires compiling the .NET solution",
    "vendor-check": "dotnet CLI probe runner that queries host CLI versions under grace-window semantics",
    "test-no-build": "full xUnit test suite runner where sabotage is covered by individual test assertions",
    "gate-sabotage": "the sabotage suite and ratchet runner itself; self-tested by executing all sabotage fixtures",
    "fleet-glass-daemon-feed-selftest": "production-byte synthetic EventSource harness covering its disconnect marker and recovery read without a daemon or browser",
    "fleet-glass-service-worker-selftest": "production-byte synthetic worker harness covering lifecycle callbacks, network fallback/recovery, and dashboard-only interception without a browser",
    "launcher-selftest": "pure synthetic selftest exercising fail-closed pointer arms plus real argv-forwarding/exit-code/pointer-flip discrimination (#1670 F2) against a compiled mock exe fixture under a temp BATON_HOME",
    "deepswe-derived-check": "paired scratch-copy selftest invokes the exact --check-all entry point and proves edited and missing derived outputs exit non-zero",
    "deepswe-derived-check-selftest": "scratch-copy selftest exercises every current snapshot through production --check-all without touching the benchmark tree",
    "vendor-verify-selftest": "pure synthetic selftest running each local vendor-verify check against a faithful fixture that must PASS and a mutated one that must NOT, plus the sqlite temp-copy location/loud-cleanup and pbtxt parser-limitation arms and #1928's agy tool-classification arms over synthetic catalogues; refuses a local check with no fixture registered, and fails closed if either suite fails",
    "ledger-derived-check": "paired scratch-copy selftest invokes the exact --check entry point and proves edited, missing and absent-input derived outputs exit non-zero",
    "ledger-derived-check-selftest": "scratch-copy selftest checks the fixture derivation against hand-computed answers, then drives production --check through edited/missing/no-input arms without touching the committed exports",
    "deepswe-refresh-selftest": "synthetic selftest covers selection, dataset-scoped display-cost parsing with duplicate refusal, displayed-vs-artifact cost reconciliation in both polarities, missing/mismatched provider refusal, numeric validation, removal refusal, derivation, and index/docs-allowlist publication (create, curated-row, and repair paths) in temporary directories, plus (#1955) the per-model price-adjustment arms -- the without-the-option control, uniform acceptance and its provenance reason, non-uniform/single-configuration/no-match/no-displayed-cost/undeclared-model-sharing-a-config-id refusals, MODEL=REASON parsing and refusal, and full create_snapshot round-trips with and without --allow-cost-drift alongside it",
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


def _format_fixture_failure(error: BaseException) -> str:
    """Render nested fixture failures without hiding either side of an exception group."""
    if isinstance(error, BaseExceptionGroup):
        nested = "; ".join(_format_fixture_failure(item) for item in error.exceptions)
        return f"{error.message}: {nested}"
    detail = f"{type(error).__name__}: {error}"
    notes = getattr(error, "__notes__", ())
    if notes:
        detail += f" ({'; '.join(notes)})"
    return detail


def run_all_fixtures() -> tuple[int, list[str]]:
    passed = 0
    failures: list[str] = []
    for name, fn in sorted(FIXTURES.items()):
        try:
            fn()
            passed += 1
            print(f"  OK  sabotage verified: {name} exits non-zero on violating input")
        except Exception as ex:  # noqa: BLE001
            detail = _format_fixture_failure(ex)
            failures.append(f"{name}: {detail}")
            print(f"  !!  sabotage FAILED: {name} -- {detail}")
    return passed, failures


def _start_directory_owner(root: Path, seconds: float) -> subprocess.Popen:
    """Start a Windows child that holds a non-delete-shared handle to root."""
    owner = subprocess.Popen(
        [sys.executable, "-B", "-c",
         "import ctypes,sys,time; k=ctypes.windll.kernel32; "
         "k.CreateFileW.restype=ctypes.c_void_p; "
         "h=k.CreateFileW('.',0x80000000,3,None,3,0x02000000,None); "
         "assert h != ctypes.c_void_p(-1).value; print('ready',flush=True); "
         "time.sleep(float(sys.argv[1]))", str(seconds)],
        cwd=root,
        stdout=subprocess.PIPE,
        text=True,
    )
    assert owner.stdout is not None
    assert owner.stdout.readline().strip() == "ready"
    owner.stdout.close()
    return owner


def _cleanup_retry_selftest() -> list[str]:
    """Prove transient sharing clears, while persistent and unrelated failures escape."""
    failures: list[str] = []
    refusal = PermissionError(32, "synthetic sharing violation", "fixture")
    calls = 0
    sleeps: list[float] = []

    def refuse_then_succeed(_path: Path) -> None:
        nonlocal calls
        calls += 1
        if calls <= 3:
            raise refusal

    attempts = _remove_temp_tree(
        Path("synthetic"), refuse_then_succeed, sleeps.append, retry_permission_errors=True)
    if attempts != 4 or calls != 4 or sleeps != [0.01, 0.02, 0.04]:
        failures.append(f"transient cleanup control used attempts={attempts}, sleeps={sleeps}")

    sleeps.clear()
    try:
        _remove_temp_tree(
            Path("synthetic"), lambda _path: (_ for _ in ()).throw(refusal),
            sleeps.append, retry_permission_errors=True)
    except PermissionError as error:
        if error is not refusal or len(sleeps) != 9 or not (1.0 <= sum(sleeps) < 1.2):
            failures.append(f"persistent cleanup control did not exhaust the bounded budget: {sleeps}")
    else:
        failures.append("persistent cleanup control was swallowed or retried without bound")

    unrelated_calls = 0

    def unrelated(_path: Path) -> None:
        nonlocal unrelated_calls
        unrelated_calls += 1
        raise FileNotFoundError(2, "synthetic missing tree", "fixture")

    try:
        _remove_temp_tree(
            Path("synthetic"), unrelated, sleeps.append, retry_permission_errors=True)
    except FileNotFoundError:
        if unrelated_calls != 1:
            failures.append("non-sharing cleanup failure was retried")
    else:
        failures.append("non-sharing cleanup failure was swallowed")

    wired_runner_calls: list[str] = []

    def record_runner(prefix: str, _body: Callable[[Path], None]) -> None:
        wired_runner_calls.append(prefix)

    try:
        _sabotage_workflow_recovery(record_runner)
    except Exception as error:  # noqa: BLE001 -- the control reports broken fixture wiring
        failures.append(f"workflow recovery fixture did not expose its cleanup boundary: {error}")
    if wired_runner_calls != ["workflow-recovery-sabotage-"]:
        failures.append(f"workflow recovery fixture bypassed bounded cleanup: {wired_runner_calls}")

    primary = AssertionError("primary workflow mutation assertion")
    cleanup = PermissionError(32, "persistent workflow cleanup refusal", "fixture")
    try:
        _run_temp_tree_fixture(
            "synthetic-",
            lambda _path: (_ for _ in ()).throw(primary),
            lambda **_kwargs: "synthetic",
            lambda _path: (_ for _ in ()).throw(cleanup),
        )
    except ExceptionGroup as errors:
        detail = _format_fixture_failure(errors)
        if (errors.exceptions != (primary, cleanup)
                or "primary workflow mutation assertion" not in detail
                or "persistent workflow cleanup refusal" not in detail):
            failures.append(f"combined fixture failure lost diagnostic detail: {detail}")
    except Exception as error:  # noqa: BLE001 -- wrong outer failure is itself the control result
        failures.append(f"fixture cleanup replaced its primary assertion: {error}")
    else:
        failures.append("simultaneous fixture and cleanup failures were swallowed")

    if os.name != "nt":
        return failures

    root = Path(tempfile.mkdtemp(prefix="sabotage-cleanup-selftest-"))
    owner = _start_directory_owner(root, 0.2)
    try:
        attempts = _remove_temp_tree(root)
        if attempts <= 1:
            failures.append("real directory-sharing control did not require a retry")
    except PermissionError as exc:
        failures.append(f"cleanup did not outlive a short-lived directory owner: {exc}")
    finally:
        owner.wait(timeout=5)
        if root.exists():
            _remove_temp_tree(root)

    root = Path(tempfile.mkdtemp(prefix="sabotage-cleanup-live-owner-"))
    owner = _start_directory_owner(root, 5.0)
    try:
        try:
            _remove_temp_tree(root)
        except PermissionError as error:
            if not any("live resource owner may remain" in note for note in error.__notes__):
                failures.append("exhausted cleanup did not identify a possible live owner")
        else:
            failures.append("cleanup masked a directory handle held beyond its retry budget")
    finally:
        if owner.poll() is None:
            owner.terminate()
        owner.wait(timeout=5)
        if root.exists():
            _remove_temp_tree(root)

    return failures


def selftest() -> int:
    """Selftest the cleanup boundary and ratchet logic."""
    failures = _cleanup_retry_selftest()

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

    cleanup_failures = _cleanup_retry_selftest()
    if cleanup_failures:
        print(f"gate-sabotage: cleanup selftest FAIL -- {'; '.join(cleanup_failures)}", file=sys.stderr)
        return 1
    print("gate-sabotage: cleanup selftest OK (transient, persistent, and unrelated arms discriminate)")

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
