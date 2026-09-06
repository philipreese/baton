"""Run every local gate, report one verdict, exit once.

WHY THIS EXISTS, and it is not convenience. Each gate below already reports correctly on its own.
The failure this removes is in how they get READ: a checker was run, its stdout filtered for a
success token, and the filtered text reported as green while the process exited 1. That has now
happened twice on this repo -- `audit-completeness` was reported passing 16/16 while exiting 1
because its output was filtered for OK/FAIL and its failure prefix is `!!`, and `audit-recordonce`
was reported as exit 0 from a stale shell variable while it was flagging 8 duplications.

Both times the gate worked and the reading of it did not. So this collapses the exit codes into
one: there is no per-gate status to sample, no shell variable to go stale between commands, and the
only thing worth reporting is this process's own exit code.

Run every gate even after one fails -- fail-fast hides the others, and a session that has to
re-run the whole set to discover the next problem starts filtering output again.
"""
import argparse
import csv
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import member_receipt  # noqa: E402 -- sibling module, importable however this file was invoked

# Sequencing (#986): one full run used to build the .NET tree twice -- `lint` forces a full
# `--no-incremental` build and `test` then built it all again -- and every audit waited for both.
# Now the pure-file audits run DURING the build phase, and the test suite reuses lint's build
# (`test-no-build`; pixi.toml owns why that is safe under `gates` and exposed outside it).
#
# The split is deliberate, not stylistic. OVERLAP holds only gates that read files and run python:
# nothing that starts MSBuild, and nothing that touches the built Baton.Cli binary. `fmt-check`
# loads every project through MSBuild. `audit-selfcheck`/`audit-controls` used to refresh a copy of
# the repo's built CLI too (#717) -- overlapping either with `lint`'s build would have reintroduced
# the concurrent-MSBuild and torn-binary failures that MSBUILDDISABLENODEREUSE (#909) and the
# 2026-08-04 mutual-kill catalogue were paid for. #1759 retired the dispatch.py call that CLI
# dependency rested on (pixi.toml's `audit-selfcheck` entry has the full account); they stay out of
# OVERLAP regardless (see AFTER_BUILD_FAST below).
OVERLAP = [
    "audit-completeness",
    "audit-recordonce",
    "audit-staleness-ext-selftest",
    "audit-waitceiling",
    "audit-waitceiling-selftest",
    "audit-retiredphrases",
    "audit-retiredphrases-selftest",
    "audit-docsbudget",
    "audit-docsbudget-selftest",
    "audit-speccitations",
    "audit-speccitations-selftest",
    "audit-commentspecrefs",
    "audit-commentspecrefs-selftest",
    "audit-clitripwire",
    "audit-clitripwire-selftest",
    "flake-watch-selftest",
    # #1402: pure python against an isolated temp lock file -- starts no MSBuild and never touches
    # the real build lock, so it cannot interfere with the build phase it overlaps.
    "buildlock-selftest",
    # #1645: pure python against injected fakes and temp dirs -- never spawns a real dotnet/baton/
    # pixi process and never touches this machine's real ~/.baton or NuGet cache, so it is exactly as
    # safe to overlap as buildlock-selftest above.
    "tool-refresh-selftest",
    # #1601: isolated sabotage suite (tools/gates/sabotage.py) overlapping the build.
    "gate-sabotage",
    # #1636: this file's own selftest -- a real temp git repo and `sh`, no MSBuild and no built
    # CLI, same shape as buildlock-selftest above. Was defined as a pixi task but never gated
    # anywhere; wired in now because it is what proves the gate-receipt logic below still
    # discriminates, not merely that it ran once at review time.
    "gates-selftest",
    # #1656 F2 (2026-09-02 review): plain `node` against in-memory synthetic fixtures -- no
    # network, no MSBuild, no built CLI, same overlap-safety shape as tool-refresh-selftest above.
    # Its sibling `fleet-glass-pusher-selftest` deliberately stays UNWIRED (fleet-glass is an
    # unbuilt-by-CI operator tool, per that task's own pixi.toml comment); this one is wired in
    # anyway because it is the ONLY thing standing between worker.js's paging/heartbeat-merge logic
    # and a silent revert going undetected (the F2 finding this fixes: reverting the merge broke
    # nothing in CI before this).
    "fleet-glass-worker-selftest",
    # #1670 F2: exercises baton.cmd/baton.ps1 against a mock exe fixture built with the legacy
    # Framework csc.exe (ships with Windows, no MSBuild involved) -- entirely under a temp
    # BATON_HOME, never the live tools root, same overlap-safety shape as tool-refresh-selftest
    # above.
    "launcher-selftest",
    # #1603: pure Python selftest; see diff_shape.py's selftest() for the discrimination arms.
    "diff-shape-selftest",
    # #1870: the checker only reads committed benchmark inputs/outputs; its selftest invokes that
    # same production check against a temporary snapshot copy with one derived cell edited.
    "deepswe-derived-check",
    "deepswe-derived-check-selftest",
    # #1852 phase A2 (2026-09-05 review): verify.py's own selftest -- pure python over synthetic
    # fixture trees under a temp scratch root, plus one temp sqlite file it creates itself. `--selftest`
    # returns from verify.py's main() BEFORE anything reads or sweeps the operator's ~/.claude, drives
    # no vendor CLI and spends nothing, so it has the same overlap-safety shape as buildlock-selftest
    # above. The paid `vendor-verify` run stays unwired and unwirable.
    # ONE ENVIRONMENTAL DEPENDENCY, unlike its neighbours: the sqlite arm copies its fixture store to
    # a scratch directory OUTSIDE the user home (verify.py's `_scratch_root`, system-drive root by
    # default, `BATON_VENDOR_VERIFY_TMP` to move it). A host where that cannot be created makes this
    # member go RED, not skip -- deliberately, since the alternative is a copy of a memory store back
    # under ~/. The scratch directory itself persists between runs; only its per-run children go.
    "vendor-verify-selftest",
    # #1901 C3: identical overlap-safety shape to the two above -- the checker reads the committed
    # cost-ledger exports and its own committed outputs, and the selftest runs that same production
    # check against a temporary copy with one derived cell edited. Neither reads ~/.baton, so a gate
    # run on a machine that has never run Baton behaves the same as one on the operator's.
    "ledger-derived-check",
    "ledger-derived-check-selftest",
    # #1935: pure Python against synthetic payloads and temporary snapshot trees. It protects the
    # live collector's selection, validation, removal refusal, and idempotence without networking.
    "deepswe-refresh-selftest",
]

# The MSBuild owners, strictly sequential: one MSBuild at a time.
BUILD_PHASE = [
    "fmt-check",
    "lint",
]

# `vendor-check` is sequential because it reads the CLI binary `lint` writes -- it runs after the
# build phase, once the overlapped audits have been joined. `audit-selfcheck`/`audit-controls` sat
# here for the matching reason before #1759: `tools/baton-agy-loop/dispatch.py` loaded the worker
# catalog from the built `Baton.Cli` binary AT IMPORT, so running either check before `lint` produced
# that binary died with "baton engine CLI binary not found ... Build it first" -- overlapped, it raced
# the very build it depended on, invisible everywhere a prior build had left the binary on disk, and a
# hard first-run FAIL in a fresh worktree, which is exactly the intermittent gate failure #1088 spent
# a session diagnosing. #1759 retired dispatch.py and its own selftest arm (`baton-dispatch-selftest`,
# which used to sit right here) along with it; the two checks below no longer touch the CLI at all but
# stay in this phase rather than being relocated as part of that change.
AFTER_BUILD_FAST = [
    "audit-selfcheck",
    "audit-controls",
    # #1487: the loud half of the drift grace window. Console.WriteLine here is inherited straight to
    # the gates output (run_gates -> pixi_runner), which a passing xunit test's ITestOutputHelper is
    # not -- dotnet test only prints a test's output when it fails, so this is the layer that can
    # actually make a fresh, still-within-grace drift visible without turning the run red.
    "vendor-check",
]

# The full run's test leg. `test-no-build` reuses the assemblies `lint` just built; if `lint`
# failed, the aggregate is already red, so a stale-assembly test result cannot turn a broken run
# green. Outside `gates`, use `pixi run test` (which force-rebuilds -- #688).
AFTER_BUILD_FULL = AFTER_BUILD_FAST + ["test-no-build"]

# #1676: CI runs `--ci`, which excludes every name here from the run and requires each to carry a
# non-empty reason -- the same shape sabotage.py's ALLOWLIST enforces for its own ratchet, so an
# entry cannot be added silently. Empty on purpose: every current gates.py member was verified
# (during #1676) to run on windows-latest with no live vendor CLI and no dependency on the real
# ~/.baton -- see OVERLAP's own per-entry comments, which is what that verification rests on. Add an
# entry here only when a future member genuinely cannot run on a hosted runner, with the reason
# cited the same way.
CI_SKIP: dict[str, str] = {}


def validate_ci_skip():
    """Ratchet: every CI_SKIP entry names a real gate member and carries a reason (#1676)."""
    problems = []
    members = set(_all_members())
    for name, reason in CI_SKIP.items():
        if name not in members:
            problems.append(f"CI_SKIP names {name!r}, which is not a gates.py member")
        if not reason or not reason.strip():
            problems.append(f"CI_SKIP[{name!r}] has no reason")
    return problems


def _dedupe(names):
    seen = []
    for name in names:
        if name not in seen:
            seen.append(name)
    return seen

PASS_MARK = "GATES: PASS"
FAIL_MARK = "GATES: FAIL"
# #1796: a member's own tools/buildlock.py wrapper timed out waiting for the build lock --
# contention, not a broken gate. Distinct exit code (buildlock.py's own BUILDLOCK_BLOCKED_EXIT,
# not imported here to keep this module's only dependency on that file's public contract, its exit
# code, rather than its implementation) so a blocked run is reported and exits differently from
# both a pass and a real failure.
BUILDLOCK_BLOCKED_EXIT = 75
BLOCKED_MARK = "GATES: BLOCKED"
# This process's own exit code for a blocked-only run (no real failures): distinct from PASS (0)
# and FAIL (1) so a caller reading only the exit code -- not summarise()'s text -- still sees three
# outcomes, matching the module docstring's "the verdict is the exit code alone" rule applied one
# level up.
BLOCKED_EXIT_CODE = 3


def _status_word(code):
    """pass/FAIL/BLOCKED for one gate member's exit code (#1796)."""
    if code == 0:
        return "pass"
    if code == BUILDLOCK_BLOCKED_EXIT:
        return "BLOCKED"
    return "FAIL"

# #1648: git exports these to every hook (a pre-push hook, in particular). A fixture that spawns
# `git init`/`git -C` under an inherited GIT_DIR re-initializes the INVOKING repo, not its own path
# argument -- this is what turned a gates-selftest fixture into a live incident. `scrubbed_env()` is
# the general blanket ("any GIT_* key"); GIT_ENV_KEYS is the explicit list `main()` strips from its
# own process environment, matching `.githooks/pre-push`'s own `unset` line.
GIT_ENV_KEYS = (
    "GIT_DIR",
    "GIT_WORK_TREE",
    "GIT_INDEX_FILE",
    "GIT_PREFIX",
    "GIT_COMMON_DIR",
    "GIT_OBJECT_DIRECTORY",
    "GIT_ALTERNATE_OBJECT_DIRECTORIES",
)


def scrubbed_env():
    """A copy of os.environ with every GIT_*-prefixed key removed (#1648, spec/baton.md C-12)."""
    return {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}


# Quiet mode (#1560): a dispatched worker that runs `gates` inherits ~2,500 tests' worth of stdout
# into its conversation context and then re-reads it on every subsequent model call -- one small
# renderer lane measured 1.25M input + 43.8M cache-read tokens, most of it this file's inherited
# output. Quiet mode drops PASSING gates' logs and prints a FAILING gate's output tail-bounded.
# This does not reintroduce the filtering the module docstring forbids: nothing here reads the
# text to DECIDE anything -- the verdict is still the exit code alone; quiet only changes how much
# of an already-decided gate's log gets echoed.
QUIET_FAIL_TAIL_LINES = 400


def emit_failure_output(name, data, tail_lines=QUIET_FAIL_TAIL_LINES):
    """Print a failing gate's captured output, tail-bounded, naming the rerun for the full log."""
    lines = data.splitlines(keepends=True)
    if len(lines) > tail_lines:
        print(f"  [{name}: {len(lines) - tail_lines} earlier line(s) elided -- "
              f"rerun `pixi run {name}` for the full log]", flush=True)
        lines = lines[-tail_lines:]
    sys.stdout.flush()
    sys.stdout.buffer.write(b"".join(lines))
    sys.stdout.buffer.flush()


def shutdown_build_servers(run=subprocess.run):
    """Best-effort `dotnet build-server shutdown` (#1671): frees MSBuild/VBCSCompiler worker nodes.

    Never raises -- this is cleanup, not a gate outcome, so a shutdown that itself fails to run
    must not turn an otherwise-passing gates run red. `run` is injectable so the selftest below can
    prove the CALL SITES (after a test* gate, and in main()'s finally on a raise) without spawning a
    real `dotnet` process.
    """
    try:
        run(["dotnet", "build-server", "shutdown"], check=False,
            capture_output=True, timeout=60)
    except (OSError, subprocess.TimeoutExpired):
        pass


def run_gates(names, runner, shutdown=shutdown_build_servers):
    """Run each gate, print a per-gate line, return (failed, blocked) name lists (#1796).

    #1671: also shuts down the MSBuild build servers after every gate whose name starts with
    "test", pass or fail. Why that scope: spec/baton.md §11 C-13.
    """
    failed = []
    blocked = []
    for name in names:
        code = runner(name)
        status = _status_word(code)
        print(f"  {status:>4}  {name}  (exit {code})", flush=True)
        if status == "FAIL":
            failed.append(name)
        elif status == "BLOCKED":
            blocked.append(name)
        if name.startswith("test"):
            shutdown()
    return failed, blocked


def join_gates(procs, quiet=False):
    """Join overlapped gates: re-print each one's output verbatim, return (failed, blocked) (#1796).

    The re-print is byte-for-byte, no decode and no filter -- re-printing is where the filtering
    the module docstring describes creeps back in, so nothing here inspects the text. The verdict
    is the exit code alone. Under --quiet a passing gate's output is dropped and a failing (or
    blocked) gate's is tail-bounded (#1560); the exit-code contract is unchanged.
    """
    failed = []
    blocked = []
    for name, proc in procs:
        out, _ = proc.communicate()
        code = proc.returncode
        status = _status_word(code)
        if not quiet:
            sys.stdout.flush()
            sys.stdout.buffer.write(out)
            sys.stdout.buffer.flush()
        elif code != 0:
            emit_failure_output(name, out)
        print(f"  {status:>4}  {name}  (exit {code})", flush=True)
        if status == "FAIL":
            failed.append(name)
        elif status == "BLOCKED":
            blocked.append(name)
    return failed, blocked


def summarise(names, failed, blocked=()):
    """The single line worth reading. Exit code, not this text, is the contract.

    #1796: a real failure always wins the headline even alongside a blocked member -- naming only
    the real failures, the same precedence VerifyRunner's own marker-preference mirrors on the
    engine side. A run with no real failures but at least one blocked member reports BLOCKED, never
    PASS -- a blocked run is not a green run.
    """
    if failed:
        return f"{FAIL_MARK} {len(failed)} of {len(names)} -- {', '.join(failed)}"
    if blocked:
        return f"{BLOCKED_MARK} {len(blocked)} of {len(names)} -- {', '.join(blocked)}"
    return f"{PASS_MARK} {len(names)} of {len(names)}"


def pixi_runner(name):
    # Output is inherited, not captured: a captured gate would have to be re-printed to be
    # readable, and re-printing is where the filtering that caused this file creeps back in. (The
    # overlapped audits are the deliberate exception; join_gates re-prints them raw.)
    return subprocess.run(["pixi", "run", name], check=False).returncode


def quiet_pixi_runner(name):
    # The --quiet counterpart (#1560): capture, and echo only a FAILING gate's output
    # (tail-bounded). The decision is still the exit code -- captured text is never inspected.
    proc = subprocess.run(["pixi", "run", name], check=False,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if proc.returncode != 0:
        emit_failure_output(name, proc.stdout)
    return proc.returncode


def pixi_spawner(name):
    # stderr folded into stdout so the join-time re-print loses nothing a terminal would have
    # shown. An overlapped audit that outgrows the OS pipe buffer just blocks until join drains
    # it -- late, never lost.
    return subprocess.Popen(["pixi", "run", name], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)


def run_all(after_build, spawner=pixi_spawner, runner=pixi_runner, quiet=False, skip=frozenset()):
    """Overlapped audits start first, the build phase runs while they work, then everything joins.

    `skip` (#1676) drops CI_SKIP-marked names from every phase before anything runs -- `--ci`'s way
    of excluding them, rather than running and discarding their result.

    Returns `(names, failed, blocked)` (#1796) -- `blocked` names a buildlock-timeout member
    distinctly from a real failure, same precedence `summarise` applies to the headline.
    """
    overlap = [n for n in OVERLAP if n not in skip]
    build_phase = [n for n in BUILD_PHASE if n not in skip]
    after_build = [n for n in after_build if n not in skip]
    procs = [(name, spawner(name)) for name in overlap]
    failed, blocked = run_gates(build_phase, runner)
    join_failed, join_blocked = join_gates(procs, quiet=quiet)
    failed += join_failed
    blocked += join_blocked
    after_failed, after_blocked = run_gates(after_build, runner)
    failed += after_failed
    blocked += after_blocked
    return overlap + build_phase + after_build, failed, blocked


def run_gates_and_shutdown(after_build, runner, quiet, shutdown=shutdown_build_servers, run_all_fn=run_all,
                            skip=frozenset()):
    """`run_all`, plus a build-server shutdown that fires even if `run_all_fn` raises (#1671).

    `run_gates` above already shuts down after each test* gate; this is the outer net -- e.g. for
    `--fast` runs, which carry no test* gate at all, or a `run_all_fn` that dies before reaching
    one. `run_all_fn`/`shutdown` are injectable so the selftest below can prove the finally fires
    under a real exception without spawning a real `dotnet` process or a real gate run.
    """
    try:
        return run_all_fn(after_build, runner=runner, quiet=quiet, skip=skip)
    finally:
        shutdown()


RECEIPT_NAME = "baton-gate-receipt"
RECEIPT_MAX_AGE_S = 6 * 3600
RECEIPT_TIME_FORMAT = "%Y-%m-%dT%H:%M:%SZ"


def _git_dir(cwd=None):
    """The current tree's git dir -- a worktree's own, not the main checkout's (#1636)."""
    out = subprocess.run(["git", "rev-parse", "--git-dir"], cwd=cwd,
                         capture_output=True, text=True, check=True).stdout.strip()
    return out if os.path.isabs(out) else os.path.normpath(os.path.join(cwd or os.getcwd(), out))


def receipt_path(cwd=None):
    return os.path.join(_git_dir(cwd), RECEIPT_NAME)


def _tree_and_dirty(cwd=None):
    """The identity a receipt is checked against: the committed tree plus a hash of what is not.

    `git status --porcelain` decides dirty/clean (it sees untracked files; `git diff HEAD` does
    not). The diff hash is the finer-grained half -- two dirty trees with the same HEAD can still
    differ, and a receipt for one is not a receipt for the other.
    """
    tree = subprocess.run(["git", "rev-parse", "HEAD^{tree}"], cwd=cwd,
                          capture_output=True, text=True, check=True).stdout.strip()
    status = subprocess.run(["git", "status", "--porcelain"], cwd=cwd,
                            capture_output=True, text=True, check=True).stdout
    dirty = bool(status.strip())
    # `git diff HEAD`'s stdout embeds arbitrary tracked-file bytes (a fixture captured on a
    # non-UTF-8-clean source, a binary-looking text file) -- `text=True` alone decodes with the
    # OS's default locale encoding (cp1252 on Windows), which raises UnicodeDecodeError on a byte
    # that codepage has no mapping for and turns an already-passed gates run into a nonzero exit
    # (the exact failure class this module's write_receipt docstring says a receipt-write failure
    # must never cause). utf-8 with errors="replace" never raises, and a `diff_hash` computed from
    # a replaced-lossy diff is still a valid, if imprecise, identity for the receipt's purpose.
    diff = subprocess.run(["git", "diff", "HEAD"], cwd=cwd,
                          capture_output=True, text=True, encoding="utf-8", errors="replace",
                          check=True).stdout
    diff_hash = hashlib.sha256(diff.encode("utf-8")).hexdigest()
    return tree, dirty, diff_hash


def write_receipt(mode, cwd=None):
    """Record that `mode` ('full'/'fast') just passed on the current tree. Overwrites any prior.

    Best-effort, like buildlock's holder-info sidecar: a receipt that failed to write just means
    the next push re-runs gates for real, which is always safe. Letting that failure propagate
    would flip an already-printed PASS verdict to a nonzero exit -- exactly the failure class this
    module's own docstring exists to stop.
    """
    try:
        tree, dirty, diff_hash = _tree_and_dirty(cwd)
        receipt = {
            "tree": tree,
            "dirty": dirty,
            "diff_hash": diff_hash,
            "mode": mode,
            "timestamp_utc": datetime.now(timezone.utc).strftime(RECEIPT_TIME_FORMAT),
        }
        with open(receipt_path(cwd), "w", encoding="utf-8") as f:
            json.dump(receipt, f)
    except (OSError, subprocess.CalledProcessError) as e:
        print(f"gates: could not write the gate receipt ({e}) -- next push will just re-run gates",
              flush=True)


def delete_receipt(cwd=None):
    """A receipt for a tree that just failed gates is worse than none -- it would say PASS."""
    try:
        os.remove(receipt_path(cwd))
    except OSError:
        pass


# ---------------------------------------------------------------------------------------------
# Per-member receipts (#1910, spec/baton.md C-12 ruling C). The store, its authentication and the
# reasons for both are member_receipt.py's; THE COVERING RULE IS HERE, stated once:
#
#   `--check-receipt` exits 0 when EITHER a whole-run receipt still holds for the current tree
#   identity (the #1636 rule above, unchanged), OR every member of the fast set -- exactly the
#   members `gates --fast` itself runs, `fast_member_set()` below -- holds a valid per-member
#   receipt for that SAME identity. A member missing, or present at a different identity, or older
#   than RECEIPT_MAX_AGE_S, means no skip: the hook falls through to a real `gates --fast` run.
#
# Nothing narrower is accepted. The union has to cover the whole fast set, which is what makes the
# skip honest -- the ruling's "names the same member set". `--fast --skip-covered` (pixi's
# `gates-fast-cover`) is what makes reaching that union affordable rather than a second full run.
# ---------------------------------------------------------------------------------------------


def fast_member_set():
    """The members `gates --fast` runs -- the population a member-receipt union has to cover."""
    return _dedupe(OVERLAP + BUILD_PHASE + AFTER_BUILD_FAST)


def tree_identity(cwd=None):
    """The identity every receipt, whole-run or per-member, is checked against."""
    tree, dirty, diff_hash = _tree_and_dirty(cwd)
    return {"tree": tree, "dirty": dirty, "diff_hash": diff_hash}


def record_member_result(member, code, cwd=None, git_dir=None, identity=None):
    """Record `member` at the current identity on exit 0; drop any receipt it has otherwise.

    The delete half is not symmetry for its own sake: a member that passed at this tree earlier and
    FAILS now would otherwise leave a valid receipt behind, and the union would keep covering the
    fast set with a member that is currently red.

    `git_dir`/`identity` are passed in by `record_run_members` below, which computes both once for a
    whole run; alone, this resolves them itself.
    """
    try:
        git_dir = git_dir or _git_dir(cwd)
        if code == 0:
            member_receipt.write(git_dir, member, identity or tree_identity(cwd))
        else:
            member_receipt.delete(git_dir, member)
    except (OSError, subprocess.CalledProcessError) as e:
        print(f"gates: could not record the {member} receipt ({e})", flush=True)


def record_run_members(names, failed, blocked, cwd=None):
    """One pass over a finished run: every member that exited 0 records, every other one drops.

    Done here rather than inside the runners so that a fake-runner selftest cannot write receipts
    into a real git dir, and so the identity is computed once for the whole run -- once, literally
    (#1936 review): this used to recompute `_git_dir` + `tree_identity` per member, which is four
    `git` processes each (one of them `git diff HEAD` over the whole tree) at the end of every run.
    """
    not_passed = set(failed) | set(blocked)
    try:
        git_dir = _git_dir(cwd)
        identity = tree_identity(cwd)
    except (OSError, subprocess.CalledProcessError) as e:
        print(f"gates: could not record per-member receipts ({e})", flush=True)
        return
    for name in names:
        record_member_result(name, 1 if name in not_passed else 0, cwd,
                             git_dir=git_dir, identity=identity)


def covered_members(cwd=None, max_age_s=RECEIPT_MAX_AGE_S):
    """The fast members holding a valid per-member receipt for the CURRENT tree identity."""
    try:
        return member_receipt.covered(
            _git_dir(cwd), tree_identity(cwd), fast_member_set(), max_age_s)
    except (OSError, subprocess.CalledProcessError):
        return set()


# ---------------------------------------------------------------------------------------------
# Telemetry (#1671). What is recorded, when, and why a separate sidecar: spec/baton.md §11 C-13.
# Every function here is best-effort: a telemetry read that fails must never turn a gates run red.
# ---------------------------------------------------------------------------------------------

TELEMETRY_NAME = "baton-gate-receipt.telemetry"
BUILD_PROCESS_NAMES = ("MSBuild.exe", "VBCSCompiler.exe")


def telemetry_path(cwd=None):
    return os.path.join(_git_dir(cwd), TELEMETRY_NAME)


def _free_physical_mb():
    """Free physical RAM in MB via the Win32 API -- `None` off Windows (pixi.toml's linux-64 leg)."""
    if sys.platform != "win32":
        return None
    import ctypes

    class _MemoryStatusEx(ctypes.Structure):
        _fields_ = [
            ("dwLength", ctypes.c_ulong),
            ("dwMemoryLoad", ctypes.c_ulong),
            ("ullTotalPhys", ctypes.c_ulonglong),
            ("ullAvailPhys", ctypes.c_ulonglong),
            ("ullTotalPageFile", ctypes.c_ulonglong),
            ("ullAvailPageFile", ctypes.c_ulonglong),
            ("ullTotalVirtual", ctypes.c_ulonglong),
            ("ullAvailVirtual", ctypes.c_ulonglong),
            ("sullAvailExtendedVirtual", ctypes.c_ulonglong),
        ]

    try:
        status = _MemoryStatusEx()
        status.dwLength = ctypes.sizeof(_MemoryStatusEx)
        if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):  # type: ignore[attr-defined]
            return None
        return status.ullAvailPhys // (1024 * 1024)
    except OSError:
        return None


def _is_build_process(name, commandline):
    """Pure filter: MSBuild.exe/VBCSCompiler.exe by name, a test host by command line.

    Why a test host needs the command-line half: spec/baton.md §11 C-13. Kept pure and
    fixture-tested (selftest below) so the WMI call in `_build_process_count` stays a thin,
    untested-by-necessity adapter.
    """
    if name in BUILD_PROCESS_NAMES:
        return True
    return name == "dotnet.exe" and bool(commandline) and "testhost" in commandline


def _build_process_count():
    """System-wide MSBuild/VBCSCompiler/testhost process count -- `None` off Windows.

    `Get-CimInstance Win32_Process`, not `tasklist`: a testhost only discriminates from any other
    `dotnet.exe` process by its command line, which `tasklist` does not expose. One PowerShell
    call, no new dependency (no `psutil` in this repo's Python env).
    """
    if sys.platform != "win32":
        return None
    try:
        out = subprocess.run(
            [
                "powershell", "-NoProfile", "-Command",
                "Get-CimInstance Win32_Process | Select-Object Name,CommandLine "
                "| ConvertTo-Csv -NoTypeInformation",
            ],
            capture_output=True, text=True, check=False, timeout=15,
        ).stdout
    except (OSError, subprocess.TimeoutExpired):
        return None
    try:
        rows = list(csv.reader(io.StringIO(out)))
    except csv.Error:
        return None
    return sum(1 for row in rows[1:] if len(row) >= 2 and _is_build_process(row[0], row[1]))


def telemetry_snapshot():
    return {"free_physical_mb": _free_physical_mb(), "build_process_count": _build_process_count()}


def write_telemetry(mode, start, end, cwd=None):
    """Record a start/end telemetry pair for the run that just finished. Overwrites any prior.

    Best-effort like `write_receipt`: a write failure here must not flip an already-decided PASS
    to a nonzero exit, and is never consulted by `--check-receipt`.
    """
    try:
        telemetry = {
            "mode": mode,
            "start": start,
            "end": end,
            "timestamp_utc": datetime.now(timezone.utc).strftime(RECEIPT_TIME_FORMAT),
        }
        with open(telemetry_path(cwd), "w", encoding="utf-8") as f:
            json.dump(telemetry, f)
    except OSError as e:
        print(f"gates: could not write the telemetry sidecar ({e})", flush=True)


def _format_age(age_s):
    if age_s < 60:
        return f"{int(age_s)}s"
    if age_s < 3600:
        return f"{int(age_s // 60)}m"
    return f"{age_s / 3600:.1f}h"


def receipt_status(cwd=None, max_age_s=RECEIPT_MAX_AGE_S):
    """(valid, receipt_dict, age_seconds) for the receipt against the CURRENT tree.

    receipt_dict/age are None if invalid. Every mismatch -- missing file, unparseable JSON,
    different tree, different dirty-hash, or a timestamp older than max_age_s -- is treated the
    same way: not valid, fall back to running gates for real. A receipt only ever narrows when
    gates are skipped, never widens it.
    """
    try:
        with open(receipt_path(cwd), "r", encoding="utf-8") as f:
            receipt = json.load(f)
    except (OSError, ValueError):
        return False, None, None

    tree, dirty, diff_hash = _tree_and_dirty(cwd)
    if receipt.get("tree") != tree:
        return False, None, None
    if receipt.get("dirty") != dirty or receipt.get("diff_hash") != diff_hash:
        return False, None, None

    try:
        written = datetime.strptime(receipt["timestamp_utc"], RECEIPT_TIME_FORMAT).replace(
            tzinfo=timezone.utc)
    except (KeyError, ValueError, TypeError):
        return False, None, None
    age = (datetime.now(timezone.utc) - written).total_seconds()
    if age < 0 or age > max_age_s:
        return False, None, None
    return True, receipt, age


def check_receipt():
    """`--check-receipt` entry point: prints the skip line and exits 0 iff the receipt still holds.

    Exits 1 silently otherwise -- the pre-push hook falls through to a real `gates-fast` run on
    exit 1, and that run's own output is the message; this command adds nothing to it. Every
    failure mode (a corrupt receipt, a `git` invocation that errors) is caught rather than left to
    print a traceback: this command has exactly two honest outputs, the skip line or exit 1.
    """
    try:
        valid, receipt, age = receipt_status()
        if valid:
            print(f"pre-push: gates receipt for tree {receipt['tree'][:7]} "
                  f"({receipt['mode']}, {_format_age(age)} old) -- skipping", flush=True)
            return 0

        # #1910: the member union, per the covering rule above. Second, not first, because a
        # whole-run receipt is the cheaper check and the commoner case.
        members = fast_member_set()
        covered = covered_members()
        if set(members) <= covered:
            identity = tree_identity()
            print(f"pre-push: per-member gate receipts cover all {len(members)} fast members for "
                  f"tree {identity['tree'][:7]} -- skipping", flush=True)
            return 0
        return 1
    except (OSError, subprocess.CalledProcessError, KeyError):
        return 1


def record_member(member, runner=None, cwd=None):
    """`--record-member <member>`: run THAT member's own gate command, receipt it iff it exits 0.

    The front door the component runs spec/baton.md C-12's ruling C names go through. The caller
    supplies a member NAME and nothing else, and the command is resolved HERE, from the one table
    this file already owns -- its member lists, every entry of which is a pixi task -- and run
    through `pixi_runner`, the identical invocation `run_gates` makes during a real run. So the
    receipt attests "baton ran member X's own command against this tree and watched it exit 0",
    which is exactly what the covering rule above assumes when it lets a push skip member X.

    Why the caller may not choose the command (#1936 review, and this is the whole reason for the
    shape): the receipt is filed under the member NAME, so a weaker command recorded under it covers
    a member that never ran. `--record-member lint -- dotnet build -warnaserror` exits 0 without
    recompiling an untouched project -- while `lint`'s own task line forces `--no-incremental`
    precisely because MSBuild skipping a project is how a non-compiling tree passes (pixi.toml says
    so at that line) -- and the push then skipped the rebuild that would have failed. `argparse`
    refuses trailing argv for us (unrecognised arguments, exit 2); this refuses a name with no entry
    in the table, since a name this file does not run has no command to resolve.

    A member outside `fast_member_set()` -- `test-no-build`, say -- is still recordable and still
    covers nothing: the covering rule reads the fast set alone, so that receipt is a diagnostic.
    """
    if member not in _all_members():
        print(f"gates: --record-member {member!r} is not a gate member, so there is no command to "
              f"run for it -- `gates.py --help` lists every member", flush=True)
        return 2
    # `runner=None` and resolved here, not `runner=pixi_runner` in the signature: a default argument
    # binds the function object at DEFINITION time, so a selftest arm that patches the module global
    # would silently spawn a real `pixi run <member>` from inside `gates-selftest` -- an OVERLAP
    # member, running concurrently with `lint`'s build, which is the concurrent-MSBuild hazard that
    # OVERLAP comment exists to prevent.
    code = (runner or pixi_runner)(member)
    record_member_result(member, code, cwd)
    return code


def _init_temp_repo(path):
    """A minimal real git repo -- the receipt tests need real `git rev-parse`/`diff` answers.

    `git -C path init` (not `git init path`) and an explicit env=scrubbed_env() on every call
    (#1648): git honours an inherited GIT_DIR over either init syntax, so under the pre-push hook
    this used to re-initialize the INVOKING repo instead of `path`. The explicit env= makes this
    fixture immune regardless of what gates.py's own process environment looks like -- see the
    gates-selftest tripwire arm below, which asserts exactly that.
    """
    env = scrubbed_env()
    subprocess.run(["git", "-C", path, "init", "-q"], check=True, env=env)
    subprocess.run(["git", "-C", path, "config", "user.email", "test@example.com"], check=True, env=env)
    subprocess.run(["git", "-C", path, "config", "user.name", "Test"], check=True, env=env)
    with open(os.path.join(path, "file.txt"), "w", encoding="utf-8") as f:
        f.write("hello\n")
    subprocess.run(["git", "-C", path, "add", "."], check=True, env=env)
    subprocess.run(["git", "-C", path, "commit", "-q", "-m", "initial"], check=True, env=env)


def _write_stub_pixi(bin_dir, real_gates_py, call_log, fast_exit=0):
    """A fake `pixi` on PATH: forwards `run gates-check-receipt` to the REAL gates.py (so the
    forged-receipt case exercises the real check-receipt logic end to end), and records any
    `run gates-fast` call to call_log instead of actually running gates -- exiting `fast_exit`,
    configurable so a test can prove the hook still propagates a REAL gates failure, not just that
    it attempted one (a hardcoded exit 0 here would pass a hook that swallowed gates-fast's exit).

    Also appends the stub's own GIT_DIR/GIT_INDEX_FILE (or `unset` for either) to call_log,
    alongside `called` -- this is what lets the caller prove the HOOK's `unset` line, not just
    scrubbed_env(), scrubbed them before this subprocess ever ran (#1651 F1).
    """
    stub = os.path.join(bin_dir, "pixi")
    with open(stub, "w", encoding="utf-8", newline="\n") as f:
        f.write(
            "#!/bin/sh\n"
            'if [ "$1" = "run" ] && [ "$2" = "gates-check-receipt" ]; then\n'
            f'    exec "{sys.executable}" -u "{real_gates_py}" --check-receipt\n'
            "fi\n"
            'if [ "$1" = "run" ] && [ "$2" = "gates-fast" ]; then\n'
            f'    printf \'called\\n\' >> "{call_log}"\n'
            f'    printf \'%s\\n\' "${{GIT_DIR-unset}}/${{GIT_INDEX_FILE-unset}}" >> "{call_log}"\n'
            f'    exit {fast_exit}\n'
            "fi\n"
            'exit 1\n'
        )
    os.chmod(stub, 0o755)
    return stub


def selftest():
    """The control arm. An aggregator that cannot go red is a green light with extra steps.

    Discriminating in both directions, on BOTH paths: an all-pass run must report PASS, and a
    single failing gate must be reported and named whether it ran sequentially or overlapped.
    Without the overlapped arm, join_gates could stop collecting failures and this file would
    keep reporting PASS -- the exact class of fault it exists to stop.

    Covers the aggregation logic only. That `pixi run <gate>`'s own exit code survives the
    subprocess boundary was proven end to end by introducing a real formatting violation and
    watching `fmt-check` come back `(exit 2)` with the others still reported -- see the commit
    that added this file. The overlapped path's boundary got the same proof when #986 landed: a
    real recordonce duplication came back `FAIL audit-recordonce` from inside the overlap -- see
    that PR.
    """
    ok = True

    failed, blocked = run_gates(["a", "b"], lambda name: 0)
    line = summarise(["a", "b"], failed, blocked)
    if failed or blocked or not line.startswith(PASS_MARK):
        print(f"  control FAILED: an all-pass run did not report pass -- {line}")
        ok = False

    failed, blocked = run_gates(["a", "b"], lambda name: 1 if name == "b" else 0)
    line = summarise(["a", "b"], failed, blocked)
    if failed != ["b"] or blocked or not line.startswith(FAIL_MARK) or "b" not in line:
        print(f"  control FAILED: a failing gate was not reported -- {line}")
        ok = False

    # #1796: a BLOCKED member (buildlock's own exit code) must be reported distinctly from a real
    # failure -- named in `blocked`, not `failed` -- and the headline must read BLOCKED, not PASS.
    failed, blocked = run_gates(["a", "b"], lambda name: BUILDLOCK_BLOCKED_EXIT if name == "b" else 0)
    line = summarise(["a", "b"], failed, blocked)
    if failed or blocked != ["b"] or not line.startswith(BLOCKED_MARK) or "b" not in line:
        print(f"  control FAILED: a blocked gate was not reported distinctly -- failed={failed} "
              f"blocked={blocked} line={line!r}")
        ok = False

    # A real failure alongside a blocked member must still headline FAIL, naming only the real
    # failure -- the same precedence VerifyRunner's own marker-preference mirrors on the engine side.
    failed, blocked = run_gates(
        ["a", "b", "c"],
        lambda name: {"a": 0, "b": BUILDLOCK_BLOCKED_EXIT, "c": 1}[name])
    line = summarise(["a", "b", "c"], failed, blocked)
    if failed != ["c"] or blocked != ["b"] or not line.startswith(FAIL_MARK) or "b" in line:
        print(f"  control FAILED: a mixed failed+blocked run did not headline the real failure "
              f"alone -- failed={failed} blocked={blocked} line={line!r}")
        ok = False

    # The overlapped path, with real subprocesses so communicate()/returncode are the real thing.
    def fake_spawner(code):
        return subprocess.Popen(
            [sys.executable, "-c", f"print('overlap-output'); raise SystemExit({code})"],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT)

    failed, blocked = join_gates([("good", fake_spawner(0)), ("bad", fake_spawner(3))])
    if failed != ["bad"] or blocked:
        print(f"  control FAILED: the overlapped path did not report the failing gate -- "
              f"failed={failed} blocked={blocked}")
        ok = False

    failed, blocked = join_gates(
        [("good", fake_spawner(0)), ("stuck", fake_spawner(BUILDLOCK_BLOCKED_EXIT))])
    if failed or blocked != ["stuck"]:
        print(f"  control FAILED: the overlapped path did not report the blocked gate -- "
              f"failed={failed} blocked={blocked}")
        ok = False

    # #1671: build-server shutdown must be reached on a FAILING test* gate, not only a passing run
    # -- proven with an injected counting fake, red-first (this arm would have caught the shutdown
    # call being placed only after a success check, or only inside a passing branch).
    shutdown_calls = []
    failed, _ = run_gates(["test-x"], lambda name: 1, shutdown=lambda: shutdown_calls.append(1))
    if failed != ["test-x"] or not shutdown_calls:
        print(
            f"  control FAILED: build-server shutdown was not reached after a failing test* "
            f"gate -- failed={failed} shutdown_calls={len(shutdown_calls)}"
        )
        ok = False

    # A non-test* gate must NOT trigger a shutdown -- it belongs to the build phase, which is
    # covered by run_gates_and_shutdown's outer finally below, not per-gate.
    shutdown_calls_nontest = []
    run_gates(["lint"], lambda name: 1, shutdown=lambda: shutdown_calls_nontest.append(1))
    if shutdown_calls_nontest:
        print("  control FAILED: a non-test* gate name triggered a build-server shutdown")
        ok = False

    # #1671: the outer net must fire even when run_all itself raises -- e.g. a `--fast` run with
    # no test* gate at all, or a crash before one is reached.
    shutdown_calls_outer = []

    def _raising_run_all(after_build, runner, quiet, skip=frozenset()):
        raise RuntimeError("boom")

    try:
        run_gates_and_shutdown(
            [], None, False,
            shutdown=lambda: shutdown_calls_outer.append(1),
            run_all_fn=_raising_run_all,
        )
    except RuntimeError:
        pass
    if not shutdown_calls_outer:
        print("  control FAILED: build-server shutdown was not reached when run_all raised")
        ok = False

    # The quiet path (#1560), both directions: a failing overlapped gate must still be REPORTED
    # (named in the failed list -- quiet must never eat a red), and a passing gate's output must
    # actually be dropped. Both arms discriminate: without the quiet branch the second arm sees
    # "overlap-output"; if quiet ever stopped collecting failures the first arm goes green-blind.
    failed, _ = join_gates([("good", fake_spawner(0)), ("bad", fake_spawner(3))], quiet=True)
    if failed != ["bad"]:
        print(f"  control FAILED: the quiet overlapped path did not report the failing gate -- {failed}")
        ok = False

    captured = io.BytesIO()

    class _Buf:
        buffer = captured
        @staticmethod
        def write(text):
            captured.write(text.encode())
        @staticmethod
        def flush():
            pass

    real_stdout = sys.stdout
    sys.stdout = _Buf()  # type: ignore[assignment]  # only .buffer/.flush are touched below
    try:
        join_gates([("good", fake_spawner(0))], quiet=True)
    finally:
        sys.stdout = real_stdout
    if b"overlap-output" in captured.getvalue():
        print("  control FAILED: quiet mode echoed a PASSING gate's output")
        ok = False

    # The tail bound: an over-long failing log is elided with the rerun named, and the tail kept.
    sys.stdout = _Buf()  # type: ignore[assignment]
    captured.seek(0); captured.truncate()
    try:
        emit_failure_output("longgate", b"".join(b"line%d\n" % i for i in range(500)), tail_lines=100)
    finally:
        sys.stdout = real_stdout
    got = captured.getvalue()
    if b"line499" not in got or b"line0\n" in got:
        print("  control FAILED: the tail bound did not keep the tail / drop the head")
        ok = False

    # The gate receipt (#1636): a real git repo, not fakes -- the whole point is that tree/dirty
    # hashes come from real `git rev-parse`/`diff` output.
    with tempfile.TemporaryDirectory() as td:
        repo = os.path.join(td, "repo")
        os.makedirs(repo)
        _init_temp_repo(repo)
        receipt_file = receipt_path(repo)

        def _forge(**overrides):
            with open(receipt_file, encoding="utf-8") as f:
                data = json.load(f)
            data.update(overrides)
            with open(receipt_file, "w", encoding="utf-8") as f:
                json.dump(data, f)

        # A pass writes a receipt that validates against the tree it was written for.
        write_receipt("fast", cwd=repo)
        valid, receipt, age = receipt_status(cwd=repo)
        if not valid or receipt.get("mode") != "fast" or age is None:
            print(f"  control FAILED: a fresh receipt did not validate -- {valid=} {receipt=}")
            ok = False

        # A fail deletes it -- no receipt left to be found valid or invalid.
        delete_receipt(cwd=repo)
        if os.path.exists(receipt_file):
            print("  control FAILED: delete_receipt left the receipt file behind")
            ok = False
        valid, _, _ = receipt_status(cwd=repo)
        if valid:
            print("  control FAILED: receipt_status validated a deleted receipt")
            ok = False

        # A receipt for a different tree does not match.
        write_receipt("full", cwd=repo)
        _forge(tree="0" * 40)
        valid, _, _ = receipt_status(cwd=repo)
        if valid:
            print("  control FAILED: a receipt for a different tree matched")
            ok = False

        # A receipt for the same tree but a different dirty-hash does not match.
        write_receipt("full", cwd=repo)
        _forge(diff_hash="0" * 64)
        valid, _, _ = receipt_status(cwd=repo)
        if valid:
            print("  control FAILED: a receipt with a mismatched dirty-hash matched")
            ok = False

        # A receipt older than the age ceiling does not match.
        write_receipt("full", cwd=repo)
        stale = datetime.now(timezone.utc) - timedelta(hours=7)
        _forge(timestamp_utc=stale.strftime(RECEIPT_TIME_FORMAT))
        valid, _, _ = receipt_status(cwd=repo)
        if valid:
            print("  control FAILED: a receipt older than the 6h ceiling matched")
            ok = False

        # ---- Per-member receipts and the covering rule (#1910) ----
        # The whole-run receipt above is deliberately left as the arms above left it: these arms
        # touch only the per-member store, and the telemetry arm below still needs that receipt.
        git_dir = _git_dir(repo)
        identity = tree_identity(repo)
        fast = fast_member_set()
        file_txt = os.path.join(repo, "file.txt")

        def _record_all(ident=None):
            for name in fast:
                member_receipt.write(git_dir, name, ident or tree_identity(repo))

        # The union covers the fast set only when every member of it is present. Both directions:
        # a covering union that did not cover would make the whole mechanism dead, and a
        # non-covering union that DID would let a push skip a member that never ran.
        _record_all(identity)
        if covered_members(cwd=repo) != set(fast):
            print(f"  control FAILED: a full set of member receipts did not cover the fast set -- "
                  f"missing {sorted(set(fast) - covered_members(cwd=repo))}")
            ok = False
        member_receipt.delete(git_dir, fast[0])
        if set(fast) <= covered_members(cwd=repo):
            print(f"  control FAILED: the union covered the fast set with {fast[0]!r} missing")
            ok = False

        # A receipt for a different tree covers nothing -- the stale-hash arm.
        member_receipt.write(git_dir, fast[0], dict(identity, tree="0" * 40))
        if fast[0] in covered_members(cwd=repo):
            print("  control FAILED: a member receipt for a different tree matched")
            ok = False

        # The age ceiling, through the injected clock rather than a forged timestamp (the MAC makes
        # forging one impossible, which is the point of the arms below).
        _record_all(identity)
        aged = member_receipt.covered(
            git_dir, identity, fast, RECEIPT_MAX_AGE_S, now=int(time.time()) + 7 * 3600)
        if aged:
            print(f"  control FAILED: member receipts older than the {RECEIPT_MAX_AGE_S}s ceiling "
                  f"matched -- {sorted(aged)}")
            ok = False

        # Forgery 1, member_receipt.py's own docstring: written by hand here on purpose, since this
        # is the file a lane could trivially produce for itself to skip a gate that never ran.
        with open(os.path.join(member_receipt.member_dir(git_dir), f"{fast[0]}.json"),
                  "w", encoding="utf-8") as f:
            json.dump({"member": fast[0], **identity, "written_epoch": int(time.time())}, f)
        if fast[0] in covered_members(cwd=repo):
            print("  control FAILED: an unsigned hand-made member receipt was accepted")
            ok = False

        # Forgery 2, member_receipt.py's own docstring: the copy an unsigned scheme cannot tell from
        # the real thing, since the two files differ only in their filename.
        shutil.copyfile(
            os.path.join(member_receipt.member_dir(git_dir), f"{fast[1]}.json"),
            os.path.join(member_receipt.member_dir(git_dir), f"{fast[0]}.json"))
        if fast[0] in covered_members(cwd=repo):
            print(f"  control FAILED: {fast[1]}'s receipt was accepted as {fast[0]}'s")
            ok = False

        # Fail closed: no key, no coverage. A lost key costs one gates run, never a free skip.
        _record_all(identity)
        key_backup = member_receipt.key_path(git_dir) + ".bak"
        os.replace(member_receipt.key_path(git_dir), key_backup)
        if covered_members(cwd=repo):
            print("  control FAILED: member receipts were accepted with the MAC key gone")
            ok = False
        os.replace(key_backup, member_receipt.key_path(git_dir))

        # A member that FAILS drops its receipt rather than keeping the one an earlier pass wrote.
        record_member_result(fast[0], 0, cwd=repo)
        if fast[0] not in covered_members(cwd=repo):
            print("  control FAILED: record_member_result did not record a passing member")
            ok = False
        record_member_result(fast[0], 1, cwd=repo)
        if fast[0] in covered_members(cwd=repo):
            print("  control FAILED: a member that failed kept its earlier receipt")
            ok = False

        # ---- The front door: a receipt names the command BATON ran (#1936 review) ----
        # Before this fix `--record-member <name> -- <anything>` filed the caller's command under
        # the caller's chosen name, so a weaker command minted a receipt covering a member that
        # never ran. Four arms: the member's own command is what runs, an unrecordable name runs
        # nothing, a failing member is not receipted, and (below, through the real CLI) a
        # caller-supplied command is refused before anything executes.
        asked = []

        def _recording_runner(name):
            asked.append(name)
            return 0

        if record_member(fast[0], runner=_recording_runner, cwd=repo) != 0 or asked != [fast[0]]:
            print(f"  control FAILED: --record-member did not run the named member's own command "
                  f"-- ran {asked}")
            ok = False
        if fast[0] not in covered_members(cwd=repo):
            print("  control FAILED: --record-member did not receipt a member that exited 0")
            ok = False

        def _refusing_runner(name):
            raise AssertionError(f"--record-member ran a command for {name!r}, which is no member")

        if record_member("not-a-gate-member", runner=_refusing_runner, cwd=repo) != 2:
            print("  control FAILED: --record-member accepted a name that is not a gate member")
            ok = False

        if record_member(fast[0], runner=lambda name: 1, cwd=repo) != 1 or \
                fast[0] in covered_members(cwd=repo):
            print("  control FAILED: --record-member receipted a member whose own command failed")
            ok = False

        # The CLI itself, since the repro in the finding was a command line: a caller-supplied
        # command must be refused BEFORE it runs. The marker file is what discriminates -- an
        # implementation that refused after running the command would leave it behind.
        marker = os.path.join(td, "record-member-ran.marker")
        supplied = subprocess.run(
            [sys.executable, os.path.abspath(__file__), "--record-member", fast[0], "--",
             sys.executable, "-c", f"open({marker!r}, 'w').close()"],
            cwd=repo, capture_output=True, text=True, check=False)
        if supplied.returncode != 2 or os.path.exists(marker):
            print(f"  control FAILED: --record-member accepted a caller-supplied command -- exit "
                  f"{supplied.returncode}, the command ran: {os.path.exists(marker)}")
            ok = False

        # #1936 re-review: the empty name is a name, and main() has to dispatch on the flag being
        # PRESENT rather than on its value being truthy -- a falsy value that fell through ran the
        # whole gate suite instead of the refusal. Exit 2 plus the refusal line is what discriminates:
        # a fall-through would exit 0 or 1, minutes later, with the members' own output.
        empty_name = subprocess.run(
            [sys.executable, os.path.abspath(__file__), "--record-member", ""],
            cwd=repo, capture_output=True, text=True, check=False)
        if empty_name.returncode != 2 or "is not a gate member" not in empty_name.stdout:
            print(f"  control FAILED: `--record-member ''` did not refuse -- exit "
                  f"{empty_name.returncode} stdout={empty_name.stdout[:200]!r}")
            ok = False

        # The identity is computed ONCE for a whole run, which is what record_run_members' docstring
        # claims -- it used to recompute it per member, ~4 git processes each across ~30 members.
        # Counted through an injected wrapper, so the arm reads the property rather than the timing.
        identity_calls = []
        real_tree_and_dirty = globals()["_tree_and_dirty"]

        def _counting_tree_and_dirty(cwd=None):
            identity_calls.append(cwd)
            return real_tree_and_dirty(cwd)

        globals()["_tree_and_dirty"] = _counting_tree_and_dirty
        try:
            record_run_members([fast[0], fast[1], fast[2]], [fast[1]], [], cwd=repo)
        finally:
            globals()["_tree_and_dirty"] = real_tree_and_dirty
        if len(identity_calls) != 1:
            print(f"  control FAILED: record_run_members computed the tree identity "
                  f"{len(identity_calls)} time(s) for a 3-member run, not once")
            ok = False
        recorded = covered_members(cwd=repo)
        if fast[1] in recorded or not {fast[0], fast[2]} <= recorded:
            print(f"  control FAILED: a shared identity changed which members a run records -- "
                  f"{sorted(recorded)}")
            ok = False

        # Dirty-tree arm 1: recorded CLEAN, checked dirty. The receipt is for a tree that no longer
        # exists, and nothing on the file itself says so -- the identity recomputation is what does.
        _record_all()
        with open(file_txt, "a", encoding="utf-8") as f:
            f.write("uncommitted\n")
        if covered_members(cwd=repo):
            print("  control FAILED: member receipts recorded on a clean tree matched a dirty one")
            ok = False

        # Dirty-tree arm 2: recorded DIRTY, checked at a DIFFERENT dirty state. Distinct from arm 1:
        # a check that only compared the dirty BOOL would pass this one, since both states are dirty.
        _record_all()
        if covered_members(cwd=repo) != set(fast):
            print("  control FAILED: member receipts recorded on a dirty tree did not match that "
                  "same dirty tree")
            ok = False
        with open(file_txt, "a", encoding="utf-8") as f:
            f.write("and more\n")
        if covered_members(cwd=repo):
            print("  control FAILED: member receipts matched a differently-dirty tree")
            ok = False

        subprocess.run(["git", "-C", repo, "checkout", "--", "file.txt"],
                       check=True, env=scrubbed_env())

        # #1671: telemetry lives in its own sidecar and never perturbs --check-receipt. A valid
        # receipt (the "full" one written just above, still forged-stale) stays INVALID after
        # write_telemetry runs -- the discriminating half: if telemetry ever wrote into
        # baton-gate-receipt itself instead of its own file, this control would flip to valid the
        # moment write_telemetry's own timestamp/mode fields overwrote the forged stale one.
        write_telemetry("full", {"free_physical_mb": 123, "build_process_count": 4}, {"free_physical_mb": 99}, cwd=repo)
        if not os.path.exists(telemetry_path(repo)):
            print("  control FAILED: write_telemetry did not create the sidecar file")
            ok = False
        valid, _, _ = receipt_status(cwd=repo)
        if valid:
            print("  control FAILED: writing telemetry resurrected an invalid (stale) receipt")
            ok = False
        with open(telemetry_path(repo), encoding="utf-8") as f:
            telemetry = json.load(f)
        if telemetry.get("start", {}).get("build_process_count") != 4:
            print(f"  control FAILED: telemetry sidecar did not round-trip its snapshot -- {telemetry!r}")
            ok = False

        # #1671 follow-up: a testhost only discriminates by command line -- a `dotnet.exe` whose
        # command line names `testhost` must count, and an unrelated `dotnet.exe` (no testhost in
        # its command line) must not. Red-first: a plain `name == "testhost.exe"` filter fails the
        # first fixture (VSTest never runs as that literal process name) and the old `tasklist`
        # substring approach would fail the second by over-matching any "dotnet.exe" line.
        fixture = [
            ("MSBuild.exe", "MSBuild.exe /nologo project.sln"),
            ("VBCSCompiler.exe", "VBCSCompiler.exe -pipename:foo"),
            ("dotnet.exe", r"C:\Program Files\dotnet\dotnet.exe exec ...\testhost.dll --port 123"),
            ("dotnet.exe", "dotnet.exe build project.csproj"),
            ("explorer.exe", None),
        ]
        counted = sum(1 for name, cmd in fixture if _is_build_process(name, cmd))
        if counted != 3:
            print(f"  control FAILED: _is_build_process miscounted the fixture -- got {counted}, want 3")
            ok = False

        # The hook itself (sh): a forged, currently-valid receipt makes it exit 0 with the skip
        # line and never call `pixi run gates-fast`; no receipt makes it fall through and call it.
        sh = shutil.which("sh")
        if sh is None:
            print("  control FAILED: no `sh` on PATH -- cannot exercise .githooks/pre-push")
            ok = False
        else:
            hook = os.path.abspath(os.path.join(
                os.path.dirname(__file__), "..", "..", ".githooks", "pre-push"))
            real_gates_py = os.path.abspath(__file__)
            bin_dir = os.path.join(td, "bin")
            os.makedirs(bin_dir)
            call_log = os.path.join(td, "calls.log")
            # fast_exit=7, not 0: the miss arm below must prove the hook PROPAGATES a real gates
            # failure, not merely that it attempted one -- a hook that swallowed gates-fast's exit
            # (e.g. `pixi run gates-fast || true`) would pass a hardcoded-0 stub undetected.
            _write_stub_pixi(bin_dir, real_gates_py, call_log, fast_exit=7)
            env = dict(os.environ)
            env["PATH"] = bin_dir + os.pathsep + env.get("PATH", "")
            # #1651 F1: main()'s GIT_ENV_KEYS pop (its first statement) already popped these keys
            # from THIS process's os.environ before selftest() ran, so `env = dict(os.environ)`
            # above starts clean regardless of what .githooks/pre-push's own `unset` line does --
            # copying it gave that line no discriminating control. Poison GIT_DIR/GIT_INDEX_FILE into the
            # subprocess's env here so the hook itself has to scrub them; the stub records what it
            # actually saw (see _write_stub_pixi) and the miss arm below asserts on that record.
            hookenv_decoy = os.path.join(td, "hookenv-decoy", ".git")
            env["GIT_DIR"] = hookenv_decoy
            env["GIT_INDEX_FILE"] = os.path.join(hookenv_decoy, "index")
            # #1910: the room-side directory the hook records its push timing into -- a real
            # directory, so the arms below read the file it actually wrote rather than asserting on
            # a path. Named explicitly rather than inherited: this process may itself be running
            # inside a dispatched execution, whose own artifact directory the selftest must not
            # append to.
            output_dir = os.path.join(td, "output")
            os.makedirs(output_dir)
            env["BATON_OUTPUT_DIR"] = output_dir
            timing_file = os.path.join(output_dir, "push-timing.jsonl")

            def _timing_lines():
                try:
                    with open(timing_file, encoding="utf-8") as f:
                        return [json.loads(line) for line in f if line.strip()]
                except (OSError, ValueError):
                    return []

            # The per-member arms above left receipts for a dirty identity behind; clear them so
            # the two arms below turn on the whole-run receipt alone, and the union arms after them
            # turn on receipts this block wrote.
            shutil.rmtree(member_receipt.member_dir(_git_dir(repo)), ignore_errors=True)

            write_receipt("fast", cwd=repo)
            hit = subprocess.run([sh, hook], cwd=repo, env=env,
                                 capture_output=True, text=True, check=False)
            if hit.returncode != 0 or "-- skipping" not in hit.stdout:
                print(f"  control FAILED: hook did not skip on a valid receipt -- "
                      f"exit={hit.returncode} stdout={hit.stdout!r} stderr={hit.stderr!r}")
                ok = False
            if os.path.exists(call_log):
                print("  control FAILED: hook called gates-fast despite a valid receipt")
                ok = False

            delete_receipt(cwd=repo)
            miss = subprocess.run([sh, hook], cwd=repo, env=env,
                                  capture_output=True, text=True, check=False)
            if not os.path.exists(call_log):
                print(f"  control FAILED: hook did not attempt gates with no receipt -- "
                      f"exit={miss.returncode} stdout={miss.stdout!r} stderr={miss.stderr!r}")
                ok = False
            else:
                with open(call_log, encoding="utf-8") as f:
                    call_log_lines = f.read().splitlines()
                if len(call_log_lines) < 2 or call_log_lines[1] != "unset/unset":
                    print(f"  control FAILED: hook did not scrub GIT_DIR/GIT_INDEX_FILE before "
                          f"calling gates-fast -- call_log={call_log_lines!r}")
                    ok = False
            if miss.returncode != 7:
                print(f"  control FAILED: hook did not propagate gates-fast's own exit code -- "
                      f"got {miss.returncode}, gates-fast exited 7")
                ok = False

            # #1910 item 3: the hook records one timing line per push -- both on the skip path and
            # on the fallback path, which is the pair the ledger's before/after reading needs.
            timings = _timing_lines()
            if len(timings) != 2 or any(
                    not isinstance(t.get("pushWaitMs"), int)
                    or not isinstance(t.get("prePushGateMs"), int)
                    or t["prePushGateMs"] < 0 for t in timings):
                print(f"  control FAILED: the hook did not record one well-formed push-timing line "
                      f"per push -- {timings!r}")
                ok = False

            # #1910: with NO whole-run receipt, a full set of per-member receipts for this tree
            # makes the hook skip -- the covering rule end to end, through the real --check-receipt.
            os.remove(call_log)
            union_identity = tree_identity(repo)
            for name in fast_member_set():
                member_receipt.write(_git_dir(repo), name, union_identity)
            union_hit = subprocess.run([sh, hook], cwd=repo, env=env,
                                       capture_output=True, text=True, check=False)
            if union_hit.returncode != 0 or "-- skipping" not in union_hit.stdout:
                print(f"  control FAILED: hook did not skip on a covering member union -- "
                      f"exit={union_hit.returncode} stdout={union_hit.stdout!r}")
                ok = False
            if os.path.exists(call_log):
                print("  control FAILED: hook called gates-fast despite a covering member union")
                ok = False

            # One member short and it falls through again. The discriminating half: without it, a
            # covered() that returned everything it was asked about would pass the arm above.
            member_receipt.delete(_git_dir(repo), fast_member_set()[0])
            union_miss = subprocess.run([sh, hook], cwd=repo, env=env,
                                        capture_output=True, text=True, check=False)
            if not os.path.exists(call_log) or union_miss.returncode != 7:
                print(f"  control FAILED: hook skipped with {fast_member_set()[0]!r} unreceipted -- "
                      f"exit={union_miss.returncode} stdout={union_miss.stdout!r}")
                ok = False

            # #1936 review: a millisecond clock that returns a non-number reaches the hook's
            # `$((...))` as an arithmetic error -- fatal under a POSIX sh, aborting an otherwise-good
            # push -- unless the hook's numeric guard catches it first. With a stub clock ahead of
            # everything on PATH the hook must still take its decision, print its skip line, write NO
            # timing line, and say nothing on stderr. Asserting the exit code alone would not
            # discriminate: bash (which is `sh` under git-bash) reports an arithmetic error and
            # carries on where dash dies, so the empty-stderr and no-new-timing-line halves are what
            # catch the guard being removed.
            #
            # The stub shadows `python`, which is what the hook reads its clock through and why --
            # stated once, next to now_ms() in .githooks/pre-push. The probe below asserts the shadow
            # is IN EFFECT before this arm concludes anything from the hook's behaviour, and what it
            # buys is a LEGIBLE failure rather than a missing one: where the double is unreachable the
            # real clock runs, the hook writes its line, and the assertion below fires anyway -- a red
            # blaming the hook for a fault the harness never managed to inject. That is exactly what
            # the `date` version of this arm did on CI. The probe names it as the harness's failure
            # instead. The other direction -- a clock that resolves nowhere, so this arm would pass
            # for the wrong reason -- is held by the one-well-formed-line-per-push arm above, which
            # is what fails if the hook stops recording timing at all.
            clock_dir = os.path.join(td, "noclock")
            os.makedirs(clock_dir)
            clock_stub = os.path.join(clock_dir, "python")
            with open(clock_stub, "w", encoding="utf-8", newline="\n") as f:
                f.write("#!/bin/sh\nprintf 'not-a-number\\n'\n")
            os.chmod(clock_stub, 0o755)
            broken_clock_env = dict(env)
            broken_clock_env["PATH"] = clock_dir + os.pathsep + env["PATH"]
            probe = subprocess.run(
                [sh, "-c", "python -c 'import time; print(int(time.time() * 1000))'"],
                cwd=repo, env=broken_clock_env, capture_output=True, text=True, check=False)
            if probe.stdout.strip() != "not-a-number":
                print(f"  control FAILED: the broken-clock stub is not in effect under {sh!r}, so "
                      f"the arm below would prove nothing -- probe stdout={probe.stdout!r}")
                ok = False
            write_receipt("fast", cwd=repo)
            before_timings = len(_timing_lines())
            no_clock = subprocess.run([sh, hook], cwd=repo, env=broken_clock_env,
                                      capture_output=True, text=True, check=False)
            if (no_clock.returncode != 0 or "-- skipping" not in no_clock.stdout
                    or no_clock.stderr.strip() or len(_timing_lines()) != before_timings):
                print(f"  control FAILED: the hook did not survive a clock that returns a "
                      f"non-number -- exit={no_clock.returncode} stdout={no_clock.stdout!r} "
                      f"stderr={no_clock.stderr!r} timing lines {before_timings} -> "
                      f"{len(_timing_lines())}")
                ok = False
            delete_receipt(cwd=repo)

    # Tripwire (#1648): _init_temp_repo must survive an inherited GIT_DIR/GIT_INDEX_FILE, not
    # merely work in a plain shell. A DECOY repo stands in for "the real repo an inherited
    # GIT_DIR would redirect this fixture into"; if _init_temp_repo's env=scrubbed_env() is ever
    # dropped, this arm goes red one of two ways depending on platform (#1651 F2): finish and
    # leave the decoy's HEAD/tree/config rewritten, caught by the before/after comparison below --
    # or `_init_temp_repo(other)` can raise CalledProcessError partway through (observed on
    # Windows: `git -C other init` guesses bare because the redirected GIT_DIR's path ends in
    # `/.git`, so the later `git add .` fails against that bare guess), which is caught below so
    # selftest still reports FAIL instead of crashing before printing it. Both paths run on every
    # platform; see the commit that added this arm for the red (scrub reverted) and green (scrub
    # restored) transcripts.
    with tempfile.TemporaryDirectory() as td:
        decoy = os.path.join(td, "decoy")
        os.makedirs(decoy)
        _init_temp_repo(decoy)

        def _decoy_state():
            head = subprocess.run(["git", "-C", decoy, "rev-parse", "HEAD"],
                                  capture_output=True, text=True, check=True).stdout
            tree = subprocess.run(["git", "-C", decoy, "write-tree"],
                                  capture_output=True, text=True, check=True).stdout
            config = subprocess.run(["git", "-C", decoy, "config", "--list", "--local"],
                                    capture_output=True, text=True, check=True).stdout
            return head, tree, config

        before = _decoy_state()

        other = os.path.join(td, "other")
        os.makedirs(other)
        prior_git_dir = os.environ.get("GIT_DIR")
        prior_git_index_file = os.environ.get("GIT_INDEX_FILE")
        os.environ["GIT_DIR"] = os.path.join(decoy, ".git")
        os.environ["GIT_INDEX_FILE"] = os.path.join(decoy, ".git", "index")
        crashed_reason = None
        try:
            _init_temp_repo(other)
        except subprocess.CalledProcessError as e:
            crashed_reason = str(e)
        finally:
            if prior_git_dir is None:
                os.environ.pop("GIT_DIR", None)
            else:
                os.environ["GIT_DIR"] = prior_git_dir
            if prior_git_index_file is None:
                os.environ.pop("GIT_INDEX_FILE", None)
            else:
                os.environ["GIT_INDEX_FILE"] = prior_git_index_file

        if crashed_reason is not None:
            print(f"  control FAILED: _init_temp_repo under an inherited GIT_DIR/GIT_INDEX_FILE "
                  f"crashed instead of finishing -- {crashed_reason}")
            ok = False

        after = _decoy_state()
        if before != after:
            print("  control FAILED: _init_temp_repo under an inherited GIT_DIR/GIT_INDEX_FILE clobbered the decoy repo")
            ok = False
        if not os.path.isdir(os.path.join(other, ".git")):
            print("  control FAILED: _init_temp_repo did not create its own repo under an inherited GIT_DIR/GIT_INDEX_FILE")
            ok = False

    # Unrecognised-flag control (#1684): before this fix, an unknown flag fell through every
    # `"--x" in sys.argv` check and reached run_all() -- silently running the full suite instead of
    # refusing. This must exit 2 fast, with no gate run and no receipt written/touched.
    with tempfile.TemporaryDirectory() as td:
        repo = os.path.join(td, "repo")
        os.makedirs(repo)
        _init_temp_repo(repo)
        write_receipt("fast", cwd=repo)
        rp = receipt_path(repo)
        before_mtime = os.path.getmtime(rp)

        start = time.monotonic()
        bogus = subprocess.run([sys.executable, os.path.abspath(__file__), "--bogus"],
                               cwd=repo, capture_output=True, text=True, check=False)
        elapsed = time.monotonic() - start

        if bogus.returncode != 2:
            print(f"  control FAILED: --bogus did not exit 2 -- got {bogus.returncode} "
                  f"(stdout={bogus.stdout!r} stderr={bogus.stderr!r})")
            ok = False
        if elapsed >= 1.0:
            print(f"  control FAILED: --bogus took {elapsed:.2f}s -- not under 1s, so it did not "
                  f"refuse before running gate members")
            ok = False
        after_mtime = os.path.getmtime(rp)
        if before_mtime != after_mtime:
            print("  control FAILED: --bogus modified the gate receipt")
            ok = False

    # `--fast --skip-covered` (pixi's gates-fast-cover), which had no control arm at all (#1936
    # review). Two invariants in one fixture, and the second is the one that matters: it runs only
    # the members this tree has no receipt for, and the receipt kind it may NOT leave behind is the
    # whole-run one, which covers the entire fast set by itself -- so a gates-fast-cover run that
    # ran nothing would otherwise certify every member it skipped. Driven through main() against
    # injected globals rather than a subprocess: the real run_gates_and_shutdown spawns `dotnet
    # build-server shutdown` in its finally, and this file's own OVERLAP entry says gates-selftest
    # starts no MSBuild.
    with tempfile.TemporaryDirectory() as td:
        repo = os.path.join(td, "repo")
        os.makedirs(repo)
        _init_temp_repo(repo)
        ran = []

        def _fake_cover_runner(name):
            ran.append(name)
            return 0

        def _fake_cover_spawner(name):
            ran.append(name)
            return fake_spawner(0)

        def _fake_run_gates_and_shutdown(after_build, runner, quiet, skip=frozenset()):
            return run_all(after_build, spawner=_fake_cover_spawner, runner=runner,
                           quiet=quiet, skip=skip)

        prior_cwd = os.getcwd()
        prior_argv = list(sys.argv)
        prior_globals = (pixi_runner, telemetry_snapshot, run_gates_and_shutdown)
        try:
            os.chdir(repo)
            globals()["pixi_runner"] = _fake_cover_runner
            globals()["telemetry_snapshot"] = lambda: {}
            globals()["run_gates_and_shutdown"] = _fake_run_gates_and_shutdown

            fast = fast_member_set()
            for name in fast:
                member_receipt.write(_git_dir(repo), name, tree_identity(repo))
            delete_receipt(cwd=repo)

            sys.argv = ["gates.py", "--fast", "--skip-covered"]
            covered_rc = main()
            covered_ran, ran[:] = list(ran), []
            covered_receipt = os.path.exists(receipt_path(repo))

            member_receipt.delete(_git_dir(repo), fast[0])
            sys.argv = ["gates.py", "--fast", "--skip-covered"]
            short_rc = main()
            short_ran, ran[:] = list(ran), []
            short_receipt = os.path.exists(receipt_path(repo))

            # The polarity arm: the SAME fixture and the same fake runner, without --skip-covered,
            # must run every fast member and write the whole-run receipt. Without it, "no receipt
            # afterwards" is satisfiable by a fixture that could never have produced one.
            delete_receipt(cwd=repo)
            sys.argv = ["gates.py", "--fast"]
            full_rc = main()
            full_ran, ran[:] = list(ran), []
            full_receipt = os.path.exists(receipt_path(repo))

            # C-12: CI is the independent run and never skips a member on a receipt written by the
            # machine it is checking, so the two flags are refused together rather than reconciled.
            delete_receipt(cwd=repo)
            sys.argv = ["gates.py", "--fast", "--skip-covered", "--ci"]
            ci_rc = main()
            ci_ran, ran[:] = list(ran), []

            # `--record-member`'s name-only path END TO END through main(): parse, dispatch,
            # resolve, run, receipt. The arms in the fixture above call record_member directly, and
            # the CLI arm dies in parse_args before the dispatch is reached -- so without this the
            # one line main() dispatches on has no execution anywhere, and it is the line CLAUDE.md
            # tells every lane to use.
            member_receipt.delete(_git_dir(repo), fast[1])
            sys.argv = ["gates.py", "--record-member", fast[1]]
            front_door_rc = main()
            front_door_ran, ran[:] = list(ran), []
            front_door_covered = fast[1] in covered_members(cwd=repo)
        finally:
            os.chdir(prior_cwd)
            sys.argv = prior_argv
            (globals()["pixi_runner"], globals()["telemetry_snapshot"],
             globals()["run_gates_and_shutdown"]) = prior_globals

        if covered_rc != 0 or covered_ran or covered_receipt:
            print(f"  control FAILED: --fast --skip-covered with every member already receipted "
                  f"exited {covered_rc}, ran {covered_ran}, and left a whole-run receipt: "
                  f"{covered_receipt}")
            ok = False
        if short_rc != 0 or short_ran != [fast[0]] or short_receipt:
            print(f"  control FAILED: --fast --skip-covered with {fast[0]!r} unreceipted exited "
                  f"{short_rc}, ran {short_ran}, and minted a whole-run receipt: {short_receipt}")
            ok = False
        if full_rc != 0 or full_ran != fast or not full_receipt:
            print(f"  control FAILED: a full --fast run over the same fixture ran {len(full_ran)} "
                  f"of {len(fast)} member(s) and wrote a whole-run receipt: {full_receipt}")
            ok = False
        if ci_rc != 2 or ci_ran:
            print(f"  control FAILED: --skip-covered with --ci was not refused -- exit {ci_rc}, "
                  f"ran {ci_ran}")
            ok = False
        if front_door_rc != 0 or front_door_ran != [fast[1]] or not front_door_covered:
            print(f"  control FAILED: `--record-member {fast[1]}` through main() exited "
                  f"{front_door_rc}, ran {front_door_ran}, and left it covered: "
                  f"{front_door_covered}")
            ok = False

    # The CI_SKIP ratchet (#1676): an orphan name (not a real member) and a blank reason must both
    # trip validate_ci_skip; a real member with a real reason must not.
    real_member = _all_members()[0]
    orig_ci_skip = dict(CI_SKIP)
    try:
        CI_SKIP.clear()
        CI_SKIP["does-not-exist-as-a-gate-member"] = "orphan name"
        problems = validate_ci_skip()
        if not any("not a gates.py member" in p for p in problems):
            print(f"  control FAILED: an orphan CI_SKIP name did not trip the ratchet -- {problems}")
            ok = False

        CI_SKIP.clear()
        CI_SKIP[real_member] = "   "
        problems = validate_ci_skip()
        if not any("has no reason" in p for p in problems):
            print(f"  control FAILED: a blank CI_SKIP reason did not trip the ratchet -- {problems}")
            ok = False

        CI_SKIP.clear()
        CI_SKIP[real_member] = "a real reason"
        problems = validate_ci_skip()
        if problems:
            print(f"  control FAILED: a valid CI_SKIP entry tripped the ratchet -- {problems}")
            ok = False
    finally:
        CI_SKIP.clear()
        CI_SKIP.update(orig_ci_skip)

    # `skip=` (#1676) must drop a name from every phase before spawning/running it -- proven by a
    # fake spawner/runner that would raise if ever called for the skipped name, so a filter that
    # ran-and-discarded it (rather than never starting it) still fails this arm.
    orig_overlap, orig_build_phase = list(OVERLAP), list(BUILD_PHASE)

    def _refusing_spawner(name):
        if name == "overlap-x":
            raise AssertionError("skip= did not exclude an OVERLAP member from spawning")
        return fake_spawner(0)

    def _refusing_runner(name):
        if name in ("build-x", "after-x"):
            raise AssertionError(f"skip= did not exclude {name!r} from running")
        return 0

    try:
        OVERLAP[:] = ["overlap-x"]
        BUILD_PHASE[:] = ["build-x"]
        names, failed, blocked = run_all(["after-x"], spawner=_refusing_spawner, runner=_refusing_runner,
                                          skip=frozenset({"overlap-x", "build-x", "after-x"}))
    finally:
        OVERLAP[:] = orig_overlap
        BUILD_PHASE[:] = orig_build_phase
    if names or failed or blocked:
        print(f"  control FAILED: run_all's skip= did not exclude every member -- "
              f"names={names} failed={failed} blocked={blocked}")
        ok = False

    print("selftest: pass" if ok else "selftest: FAIL")
    return 0 if ok else 1


def _all_members():
    """Every gate member this file can run, build order first (OVERLAP, then BUILD_PHASE, then
    the full test leg) -- what `--help` lists, so the listing can never drift from run_all()'s own
    membership."""
    seen = []
    for name in OVERLAP + BUILD_PHASE + AFTER_BUILD_FULL:
        if name not in seen:
            seen.append(name)
    return seen


def build_parser():
    """#1684: argparse, not `"--x" in sys.argv` -- an unrecognised flag must be refused (usage on
    stderr, exit 2) rather than silently falling through to a full gate run, which is what
    happened when `--help` was mistyped and ran the whole suite instead of printing usage."""
    parser = argparse.ArgumentParser(
        prog="gates.py",
        description="Run every local gate, report one verdict, exit once.",
        epilog="gate members (build order):\n  " + "\n  ".join(_all_members()),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--fast", action="store_true",
                        help="skip the test suite (AFTER_BUILD_FAST instead of AFTER_BUILD_FULL)")
    parser.add_argument("--quiet", action="store_true",
                        help="drop a passing gate's output; tail-bound a failing gate's")
    parser.add_argument("--selftest", action="store_true",
                        help="run this file's own control-arm suite instead of any gate")
    parser.add_argument("--check-receipt", action="store_true",
                        help="exit 0 and print the skip line iff a still-valid gate receipt exists")
    parser.add_argument("--skip-covered", action="store_true",
                        help="run only the members with no valid per-member receipt for this tree "
                             "(#1910; with --fast, this is what completes a lane's own coverage)")
    parser.add_argument("--record-member", metavar="MEMBER",
                        help="run gate member MEMBER's own command (`pixi run MEMBER`) and record a "
                             "per-member receipt for this tree iff it exits 0 (#1910). The name is "
                             "the whole input -- a command of your own is not accepted")
    parser.add_argument("--ci", action="store_true",
                        help="exclude CI_SKIP members, validate the ratchet, and assert the run "
                             "matches the tracked member list (#1676; what CI's own job passes)")
    return parser


def main():
    # #1648: scrub before dispatching on any mode, so every child gate -- and every git call this
    # process itself makes (receipt_status included) -- inherits a clean environment. This is
    # defense in depth alongside _init_temp_repo's own explicit env=; it does not replace it,
    # because _init_temp_repo is also reachable directly (the selftest tripwire calls it with
    # os.environ deliberately poisoned to prove the explicit env= is what actually protects it).
    for k in GIT_ENV_KEYS:
        os.environ.pop(k, None)

    args = build_parser().parse_args()

    # #1936 review: argparse owns this now. `--record-member` takes a member NAME and nothing else,
    # so trailing argv (`--record-member lint -- dotnet build`) is refused for free as unrecognised
    # arguments, exit 2 -- the hand-rolled pre-argparse dispatch this replaces existed only to pass
    # a caller's command through uninterpreted, which is the thing `record_member` no longer accepts.
    # `is not None`, not truthiness (#1936 re-review): `--record-member ""` is a member name this
    # file cannot run, and record_member says so and exits 2. Dispatching on truthiness dropped it
    # through to a FULL gate run instead -- the same surprise build_parser's docstring above exists
    # to stop, in the same file.
    if args.record_member is not None:
        return record_member(args.record_member)
    if args.selftest:
        return selftest()
    if args.check_receipt:
        return check_receipt()
    if args.skip_covered and args.ci:
        # CI is the independent run (C-12): it never skips a member on the strength of a receipt
        # written by the machine it is checking, and --ci's own member-list assertion would fail
        # anyway. Refused loudly rather than silently ignored.
        print("gates: --skip-covered is not usable with --ci -- CI runs every member", flush=True)
        return 2

    if args.ci:
        problems = validate_ci_skip()
        if problems:
            print("gates: CI_SKIP ratchet failed:")
            for p in problems:
                print(f"  - {p}")
            return 2

    mode = "fast" if args.fast else "full"
    after_build = AFTER_BUILD_FAST if mode == "fast" else AFTER_BUILD_FULL
    quiet = args.quiet
    skip = frozenset(CI_SKIP) if args.ci else frozenset()
    if args.skip_covered:
        # #1910: the completion pass. Skipping a member here is the same decision --check-receipt
        # makes about the whole set, taken one member at a time and against the same identity, so
        # the two cannot disagree about what a receipt is worth.
        already = covered_members()
        skip = frozenset(skip | already)
        print(f"gates: {len(already)} member(s) already receipted for this tree, skipping them: "
              f"{', '.join(sorted(already)) or '(none)'}")

    start_snapshot = telemetry_snapshot()
    names, failed, blocked = run_gates_and_shutdown(
        after_build, quiet_pixi_runner if quiet else pixi_runner, quiet, skip=skip)
    end_snapshot = telemetry_snapshot()
    write_telemetry(mode, start_snapshot, end_snapshot)

    print()
    print(f"gates: ran {len(names)} member(s): {', '.join(names)}")

    # #1676: the assertion that CI cannot silently drift from the tracked member list. Under --ci
    # this must equal every gates.py member (build order) minus CI_SKIP -- any other result means
    # run_all's own filtering diverged from _all_members(), not that a member merely failed.
    if args.ci:
        expected = [n for n in _dedupe(OVERLAP + BUILD_PHASE + after_build) if n not in CI_SKIP]
        if names != expected:
            print("gates: CI member list does not match the tracked list -- ")
            print(f"  ran:      {names}")
            print(f"  expected: {expected}")
            delete_receipt()
            return 1

    print(summarise(names, failed, blocked))
    # #1796: a blocked-only run is not a pass -- it must not write a receipt a later push would
    # read as "gates already ran" (the receipt is exactly what a passing run's exit lets a future
    # push skip re-running). BLOCKED_EXIT_CODE (3) is distinct from both PASS (0) and a real FAIL
    # (1): the room-level engine caller (Baton.Mutation.VerifyRunner) discriminates on gates.py's
    # printed marker line, not this process's own exit code, but a human or CI script reading only
    # the exit code must still see three distinct outcomes.
    # #1910: every member this run actually ran gets (or loses) its own receipt, whatever the
    # aggregate verdict was -- that is what lets a later `--skip-covered` pass stand on the legs
    # this run already paid for, and what stops a member that just went red from staying covered.
    record_run_members(names, failed, blocked)
    if failed or blocked:
        delete_receipt()
    elif args.skip_covered:
        # A partial run must never mint a WHOLE-run receipt: it did not run every fast member, and
        # the whole-run receipt claims exactly that. Its member receipts carry what it did prove,
        # and the covering rule is what reads them -- the two receipt kinds stay unblurred.
        print("gates: partial run (--skip-covered) -- per-member receipts only, no whole-run receipt")
    else:
        write_receipt(mode)
    if failed:
        return 1
    if blocked:
        return BLOCKED_EXIT_CODE
    return 0


if __name__ == "__main__":
    sys.exit(main())
