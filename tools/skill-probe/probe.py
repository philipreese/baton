#!/usr/bin/env python3
"""Reproduces #2079's measurement: does the claude CLI activate a PROJECTED skill under ``-p``?

#2074 ruled the floor for an attached skill on claude is "reachable without acting, PROVEN".
`ClaudeWorkerAdapter.Resolve` realizes an attached package by projecting it into
``<workspace>/.claude/skills/<name>/`` (#1929) and then depends on the CLI loading *and* the model
activating it. This script is the proof, or the disproof.

Six live `baton dispatch` runs, each on its own throwaway git workspace: three carrying
``--skill marker-probe`` and three carrying nothing, all with the same brief. The brief never
mentions the marker file or the skill -- only the fixture package's body does -- so a marker at the
worker's cwd can only have come from the projected skill.

Four things are recorded per run, and the first two are read in this order on purpose:

1. **placement** -- ``<cwd>/.claude/skills/marker-probe/SKILL.md`` exists. This is the
   discriminating control: a missing marker means nothing until placement is confirmed, because
   *not placed* is a #1929 defect and *placed but not activated* is what #2074 asked about.
2. **marker** -- ``<cwd>/PROBE-ACTIVATED.txt``, the activation signal.
3. **prompt echo** -- the CLI's own ``{"type":"system","subtype":"init"}`` line in the room's
   ``.stdout.log`` carries a ``skills`` array; whether ``marker-probe`` is in it says whether the
   CLI *loaded* the projection, independently of whether the model used it.
4. **tokens** -- ``tokensIn`` from the cost ledger row for the run's execution id, so the probe arm
   can be differenced against the control arm.

**The worker's cwd is not the ``--workspace`` directory.** ``implement`` delivers a branch, so the
engine provisions a worktree and the projection and the marker both land there. Every path above is
resolved from the room's ``bindings.json``, never guessed from ``--workspace``.

**Where the fixture lives, and why not where #2079 named it.** #2079 asked for
``skills/marker-probe/`` at the repository root. That is the ``<workspace>/skills/`` scan rung
(`SkillPackageResolver`), which `ClaudeWorkerAdapter.PlanSkillProjection` reads for every dispatch
that names no skill -- so a package at that path would be projected into, and rostered on, every
future no-``--skill`` claude lane run against this repository, telling each worker to write
``PROBE-ACTIVATED.txt``. It would also contaminate this script's own control arm. The fixture sits
under ``tools/skill-probe/skills/`` instead and is reached through ``BATON_SKILLS_PATH``, the
override rung `SkillPackageResolver` documents as "a one-off experiment", which is exactly this.

Usage::

    python tools/skill-probe/probe.py --runs 3            # the full six-run measurement
    python tools/skill-probe/probe.py --preflight         # free: resolve the binding, dispatch nothing

This spends real subscription budget on the vendor CLI. `--preflight` does not.
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
FIXTURE_SKILLS_DIR = Path(__file__).resolve().parent / "skills"
SKILL_NAME = "marker-probe"
MARKER_FILE = "PROBE-ACTIVATED.txt"

# The brief. It names the task the fixture's `description:` claims to serve, and mentions neither
# the marker file nor the skill -- a brief that named either would measure obedience, not activation.
BRIEF = (
    "Write a short workspace orientation note for this repository: what it appears to contain, and "
    "how a newcomer would start reading it. Two or three sentences is enough. Do not modify any "
    "file that is already tracked by git."
)

ROLE = "implement"


def run_dispatch(*, room_dir: Path, workspace: Path, with_skill: bool, model: str,
                 timeout_minutes: int, preflight: bool) -> subprocess.CompletedProcess[str]:
    baton = shutil.which("baton")
    if baton is None:
        raise SystemExit("baton is not on PATH -- see README's 'Installing baton'.")

    cmd = [
        baton, "dispatch", ROLE,
        "--spec-text", BRIEF,
        "--adapter", "claude",
        "--model", model,
        "--workspace", str(workspace),
        "--room-dir", str(room_dir),
        "--expect-pr", "false",
        "--verify", "git --version",
        "--timeout", str(timeout_minutes),
    ]
    if preflight:
        # A name no rung holds. `SkillPackageResolver.Resolve` refuses it in `RoleDispatch.ToBinding`,
        # before a room directory exists and before the vendor CLI is spawned, and the refusal names
        # every rung it searched -- so this asks, for free, whether BATON_SKILLS_PATH is actually
        # being read on this machine. It is the free half of the measurement, not a rehearsal of it.
        cmd += ["--skill", f"{SKILL_NAME}-absent"]
    elif with_skill:
        cmd += ["--skill", SKILL_NAME]

    env = dict(os.environ)
    # The override rung. See this module's docstring for why the fixture is not on the repo-local one.
    env["BATON_SKILLS_PATH"] = str(FIXTURE_SKILLS_DIR)

    return subprocess.run(
        cmd, capture_output=True, text=True, encoding="utf-8", errors="replace",
        env=env, cwd=str(REPO_ROOT), timeout=(timeout_minutes + 10) * 60)


def make_workspace(root: Path, label: str, *, preflight: bool) -> Path:
    """A throwaway git repository. Git, because `implement` delivers a branch and the engine
    provisions a worktree from the workspace before the worker starts."""
    ws = root / label
    ws.mkdir(parents=True)
    (ws / "README.md").write_text(
        "# throwaway\n\nA scratch repository created by tools/skill-probe/probe.py.\n",
        encoding="utf-8")
    for args in (["init", "-q", "-b", "main"],
                 ["-c", "user.name=probe", "-c", "user.email=probe@example.invalid", "add", "-A"],
                 ["-c", "user.name=probe", "-c", "user.email=probe@example.invalid",
                  "commit", "-q", "-m", "chore: seed"]):
        subprocess.run(["git", *args], cwd=str(ws), check=True,
                       capture_output=True, text=True)

    # Decision 0004's project ceiling: a headless dispatch against an unseen directory fails closed
    # rather than prompting, so the throwaway has to be trusted before it can be dispatched against.
    # Forgotten again in `main`'s finally (`baton trust <ws> --forget`, the only verb whose purpose is
    # removal; `--revoke` would leave a tombstone, and `--ceiling` re-trust deletes tombstones only by
    # replacing them with a live record), so a run leaves nothing in project-ceilings.json.
    if not preflight:
        baton_exe(["trust", str(ws), "--ceiling", "all"])
    return ws


def baton_exe(args: list[str]) -> subprocess.CompletedProcess[str]:
    baton = shutil.which("baton")
    if baton is None:
        raise SystemExit("baton is not on PATH -- see README's 'Installing baton'.")
    return subprocess.run([baton, *args], capture_output=True, text=True,
                          encoding="utf-8", errors="replace")


def worker_working_directory(room_dir: Path, fallback: Path) -> Path:
    """The directory the worker actually ran in, read from the room rather than assumed."""
    bindings = room_dir / "bindings.json"
    if not bindings.exists():
        return fallback
    try:
        data = json.loads(bindings.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return fallback
    for entry in data.values() if isinstance(data, dict) else []:
        if isinstance(entry, dict) and entry.get("WorkingDirectory"):
            return Path(entry["WorkingDirectory"])
    return fallback


def stdout_logs(room_dir: Path) -> list[Path]:
    return [Path(p) for p in glob.glob(str(room_dir / "artifacts" / "execution_*" / ".stdout.log"))]


def cli_listed_skill(room_dir: Path) -> bool | None:
    """Whether the CLI's own `system`/`init` echo listed the skill. None when no init line was found
    (no stream-json output at all), which is a different answer from "listed nothing"."""
    seen_init = False
    for log in stdout_logs(room_dir):
        with log.open(encoding="utf-8", errors="replace") as handle:
            for line in handle:
                line = line.strip()
                if not line.startswith("{"):
                    continue
                try:
                    event = json.loads(line)
                except ValueError:
                    continue
                if event.get("type") != "system" or event.get("subtype") != "init":
                    continue
                seen_init = True
                if any(SKILL_NAME in str(s) for s in event.get("skills", [])):
                    return True
    return False if seen_init else None


def execution_ids(room_dir: Path) -> list[str]:
    return [Path(p).name[len("execution_"):]
            for p in glob.glob(str(room_dir / "artifacts" / "execution_*"))]


def ledger_rows(ids: list[str]) -> list[dict]:
    """The cost-ledger rows for these executions. Both ledger layouts are searched rather than the
    throwaway workspace's repository slug being predicted -- #2066 relocated the ledger, and a
    remote-less throwaway has no slug worth guessing."""
    baton_root = Path(os.environ.get("USERPROFILE", os.path.expanduser("~"))) / ".baton"
    candidates = list(baton_root.glob("ledger/*.jsonl")) + list(baton_root.glob("*/cost-ledger.jsonl"))
    rows = []
    for path in candidates:
        try:
            text = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for line in text.splitlines():
            if not any(i in line for i in ids):
                continue
            try:
                rows.append(json.loads(line))
            except ValueError:
                pass
    return rows


SKILLS_LINE = re.compile(r"^.*\bSkills:.*$", re.MULTILINE)


def skills_line(console: str) -> str:
    """`ClaudeWorkerAdapter.AnnounceSkillProjection` writes this to the dispatch console, not to the
    room -- so it is read back from the captured process output."""
    matches = SKILLS_LINE.findall(console)
    return matches[0].strip() if matches else "(none printed)"


def measure(root: Path, label: str, *, with_skill: bool, model: str, timeout_minutes: int,
            preflight: bool) -> dict:
    workspace = make_workspace(root, f"ws-{label}", preflight=preflight)
    room_dir = root / f"room-{label}"
    started = time.time()
    proc = run_dispatch(room_dir=room_dir, workspace=workspace, with_skill=with_skill,
                        model=model, timeout_minutes=timeout_minutes, preflight=preflight)
    console = (proc.stdout or "") + "\n" + (proc.stderr or "")
    cwd = worker_working_directory(room_dir, workspace)
    ids = execution_ids(room_dir)
    rows = ledger_rows(ids) if ids else []

    marker_path = cwd / MARKER_FILE
    projected = cwd / ".claude" / "skills" / SKILL_NAME / "SKILL.md"
    return {
        "run": label,
        "arm": "probe" if with_skill else "control",
        "exit": proc.returncode,
        "workspace": str(workspace),
        "worker_cwd": str(cwd),
        "room": str(room_dir),
        "projected": projected.exists(),
        "marker": marker_path.exists(),
        "marker_content": marker_path.read_text(encoding="utf-8", errors="replace").strip()
                          if marker_path.exists() else None,
        "cli_listed_skill": cli_listed_skill(room_dir),
        "skills_line": skills_line(console),
        "tokens_in": sum(r.get("tokensIn", 0) for r in rows) if rows else None,
        "billed_tokens": sum(r.get("billedTokens", 0) for r in rows) if rows else None,
        "wall_seconds": round(time.time() - started, 1),
        "console_tail": console.strip()[-1500:],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runs", type=int, default=3,
                        help="runs per arm (default 3, i.e. the six runs #2079 authorizes)")
    parser.add_argument("--start", type=int, default=1,
                        help="first run index, so a stopped-and-inspected first pair can be resumed "
                             "without re-spending on it")
    parser.add_argument("--model", default="opus",
                        help="claude model. Defaults to the frontier tier's, because a result "
                             "measured on a weaker model would not scope to the lanes that ship.")
    parser.add_argument("--timeout", type=int, default=6, help="per-run timeout in minutes")
    parser.add_argument("--preflight", action="store_true",
                        help="resolve the binding and print the roster; dispatch nothing, spend nothing")
    parser.add_argument("--out", default=None, help="write the raw results as JSON here")
    parser.add_argument("--keep", action="store_true", help="keep the throwaway workspaces")
    args = parser.parse_args()

    root = Path(tempfile.mkdtemp(prefix="skill-probe-"))
    results = []
    try:
        for index in range(args.start, args.start + args.runs):
            for with_skill in (True, False):
                label = f"{'probe' if with_skill else 'control'}-{index}"
                print(f"== {label} ==", flush=True)
                result = measure(root, label, with_skill=with_skill, model=args.model,
                                 timeout_minutes=args.timeout, preflight=args.preflight)
                results.append(result)
                print(json.dumps({k: v for k, v in result.items() if k != "console_tail"},
                                 indent=1), flush=True)
                if args.preflight:
                    print(result["console_tail"], flush=True)
    finally:
        if args.out:
            Path(args.out).write_text(json.dumps(results, indent=1), encoding="utf-8")
        for result in results:
            # The trust `make_workspace` recorded is scoped to a directory that is about to stop
            # existing; leaving it behind would grow project-ceilings.json once per run forever. This
            # is the OPERATOR's real store (no BATON_HOME override), so the record has to go, not be
            # revoked: `--revoke` leaves a tombstone that nothing but `--forget` removes (#2121).
            baton_exe(["trust", result["workspace"], "--forget"])
        if not args.keep and not results:
            shutil.rmtree(root, ignore_errors=True)

    print("\n| run | arm | projected | marker | CLI listed skill | tokensIn |")
    print("|---|---|---|---|---|---|")
    for r in results:
        print(f"| {r['run']} | {r['arm']} | {r['projected']} | {r['marker']} | "
              f"{r['cli_listed_skill']} | {r['tokens_in']} |")
    print(f"\nartifacts kept under {root}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
