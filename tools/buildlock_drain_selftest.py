"""Offline regression controls for buildlock.run_recorded's bounded pipe drain.

The production module is loaded in a spawned driver.  The driver replaces only replay_inputs so
run_recorded writes to a temporary receipt without consulting Git or the real build-lock store.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
from typing import Any
from unittest.mock import patch


PAYLOAD = b"stdout-diagnostic\x00\xff\n" + b"stderr-diagnostic\x01\xfe\n"
HOLDER_LIFETIME_S = 4.5
DRIVER_TIMEOUT_S = 3.5
INDEPENDENT_TIMEOUT_S = 2.5
# The main-hold driver deliberately observes two sequential phases while keeping the same
# process alive.  Give the outer observer both phase budgets plus only startup/serialization slack.
MAIN_HOLD_TIMEOUT_S = DRIVER_TIMEOUT_S + INDEPENDENT_TIMEOUT_S + 0.5


# These two Windows fixtures transfer the Popen-owned child handle to the observer while both
# processes are live. Waiting on this exact handle cannot accidentally observe a reused PID.
REGISTER_OWNED_CHILD = r'''
import _winapi
import os
def register_owned_child(child, result_path):
    observer = _winapi.OpenProcess(_winapi.PROCESS_DUP_HANDLE, False,
                                  int(os.environ["BUILDLOCK_SELFTEST_OBSERVER_PID"]))
    try:
        handle = _winapi.DuplicateHandle(_winapi.GetCurrentProcess(), int(child._handle),
                                        observer, _winapi.SYNCHRONIZE, False, 0)
        pathlib.Path(result_path).with_suffix(".process.json").write_text(json.dumps({"handle": handle}))
    finally:
        _winapi.CloseHandle(observer)
'''


FIXTURE = r'''
import os
import subprocess
import sys

mode = sys.argv[1]
if mode == "complete":
    os.write(1, b"stdout-diagnostic\x00\xff\n")
    os.write(2, b"stderr-diagnostic\x01\xfe\n")
    raise SystemExit(0)
if mode == "complete-slow":
    import time
    time.sleep(1.75)
    os.write(1, b"stdout-diagnostic\x00\xff\n")
    os.write(2, b"stderr-diagnostic\x01\xfe\n")
    raise SystemExit(0)
if mode == "nonzero":
    os.write(1, b"stdout-diagnostic\x00\xff\n")
    os.write(2, b"stderr-diagnostic\x01\xfe\n")
    raise SystemExit(7)
if mode == "eof-live":
    os.close(1)
    os.close(2)
    import time
    time.sleep(0.3)
    raise SystemExit(0)
if mode in ("hold-stdout", "hold-stderr", "hold-child75"):
    os.write(1, (mode + "\n").encode())
    sleeper_code = (
        "import os,sys,time\n"
        "fd=1 if sys.argv[1]=='stdout' else 2\n"
        "end=time.monotonic()+float(sys.argv[2])\n"
        "while time.monotonic() < end:\n"
        "    os.write(fd,b'continuous-descendant-output\\n')\n"
        "    time.sleep(.03)\n"
    )
    sleeper = [sys.executable, "-c", sleeper_code,
               "stdout" if mode != "hold-stderr" else "stderr", sys.argv[2]]
    if mode != "hold-stderr":
        subprocess.Popen(sleeper, stdout=None, stderr=subprocess.DEVNULL, close_fds=False)
    else:
        subprocess.Popen(sleeper, stdout=subprocess.DEVNULL, stderr=None, close_fds=False)
    raise SystemExit(75 if mode == "hold-child75" else 0)
raise SystemExit("unknown fixture mode")
'''


DRIVER = r'''
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import time

INDEPENDENT_TIMEOUT_S = 2.5
module_path, fixture_path, result_path, receipt_path, mode, holder_lifetime = sys.argv[1:]
spec = importlib.util.spec_from_file_location("buildlock_under_test", module_path)
if spec is None or spec.loader is None:
    raise RuntimeError("cannot load buildlock module")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

receipt = Path(receipt_path)
inputs = (receipt, "fixture-fingerprint")
module.replay_inputs = lambda command, priority_class: inputs
fixture_mode = "complete" if mode == "receipt" else (
    "hold-stdout" if mode in ("main-hold", "main-hold-retained") else mode
)
command = [sys.executable, fixture_path, fixture_mode]
if mode in ("hold-stdout", "hold-stderr", "hold-child75", "main-hold", "main-hold-retained"):
    command.append(holder_lifetime)
env = dict(os.environ)

if mode in ("main-hold", "main-hold-retained"):
    lock_path = str(Path(receipt_path).with_suffix(".lock"))
    for name in ("BATON_BUILDLOCK_HELD", "BATON_BUILDLOCK_WAIT_LOG", "BATON_LOCK_WAIT_LOG"):
        os.environ.pop(name, None)
    os.environ["BATON_BUILDLOCK_FILE"] = lock_path
    os.environ["BATON_BUILDLOCK_TIMEOUT_S"] = "2"
    sys.argv = [module_path, "--replay", *command]
    started = time.monotonic()
    code = module.main()
    elapsed = time.monotonic() - started
    retained_handle = None
    if mode == "main-hold-retained":
        retained_handle = module.acquire(lock_path, ["negative-control"], 2)
    independent_env = dict(os.environ)
    for name in ("BATON_BUILDLOCK_HELD", "BATON_BUILDLOCK_WAIT_LOG", "BATON_LOCK_WAIT_LOG"):
        independent_env.pop(name, None)
    independent_env["BATON_BUILDLOCK_FILE"] = lock_path
    independent = subprocess.Popen(
        [sys.executable, module_path, sys.executable, fixture_path, "complete-slow"],
        env=independent_env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    try:
        independent_stdout, independent_stderr = independent.communicate(timeout=INDEPENDENT_TIMEOUT_S)
        independent_code = independent.returncode
    except subprocess.TimeoutExpired:
        independent.kill()
        independent.communicate(timeout=5)
        independent_code = None
        independent_stdout = b""
        independent_stderr = b""
    Path(result_path).write_text(json.dumps({
        "code": code,
        "elapsed": elapsed,
        "receipt": receipt.exists(),
        "replayed": False,
        "independent_code": independent_code,
        "independent_stdout": independent_stdout.decode("latin-1"),
        "independent_stderr": independent_stderr.decode("latin-1"),
    }), encoding="utf-8")
    if retained_handle is not None:
        import msvcrt

        retained_handle.seek(0)
        msvcrt.locking(retained_handle.fileno(), msvcrt.LK_UNLCK, 1)
        retained_handle.close()
    raise SystemExit(0)

started = time.monotonic()
code = module.run_recorded(command, env, "build", inputs)
elapsed = time.monotonic() - started
replayed = False
if mode in ("receipt", "eof-live", "hold-stdout", "hold-stderr"):
    replayed = module.replay_pass(inputs, command)
Path(result_path).write_text(json.dumps({
    "code": code,
    "elapsed": elapsed,
    "receipt": receipt.exists(),
    "replayed": replayed,
}), encoding="utf-8")
'''


def _load_result(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def _run_driver(
    driver: Path,
    module_path: Path,
    fixture: Path,
    root: Path,
    mode: str,
    timeout_s: float = DRIVER_TIMEOUT_S,
    owned_child: bool = False,
) -> tuple[subprocess.CompletedProcess[bytes] | None, dict[str, Any] | None, float]:
    result = root / f"{mode}.result.json"
    receipt = root / f"{mode}.receipt.json"
    command = [
        sys.executable,
        str(driver),
        str(module_path),
        str(fixture),
        str(result),
        str(receipt),
        mode,
        str(HOLDER_LIFETIME_S),
    ]
    started = time.monotonic()
    env = dict(os.environ)
    if owned_child:
        env["BUILDLOCK_SELFTEST_OBSERVER_PID"] = str(os.getpid())
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env)
    child_handle = None
    try:
        try:
            stdout, stderr = process.communicate(timeout=timeout_s)
        except subprocess.TimeoutExpired:
            # A timeout is still refusal, but its owned descendants must not retain fixture files.
            # Kill only this Popen's tree before reaping it; never search for unrelated processes.
            try:
                # The root can exit naturally while an inherited writer still holds our capture
                # pipe. Its PID is no longer a safe kill target (it may be reused); bounded EOF
                # is the only safe proof that inherited writers have closed in this state.
                root_exited = process.poll() is not None
                if os.name == "nt" and not root_exited:
                    cleanup = subprocess.run(
                        ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                        capture_output=True, check=False, timeout=5,
                    )
                    # If it exited between poll and taskkill, do not treat the stale PID as a
                    # cleanup refusal. The communicate barrier still has to establish EOF.
                    if cleanup.returncode != 0 and process.poll() is None:
                        raise RuntimeError(f"driver tree cleanup failed: {cleanup.stderr!r}")
            finally:
                if process.poll() is None:
                    process.kill()
                process.wait(timeout=5)
                process.communicate(timeout=5)
                # EOF proves the writers closed, not that Windows finished releasing every
                # resource of a terminated child (#2681). Keep both barriers before unlink.
                if owned_child:
                    import _winapi

                    child_handle = _load_result(result.with_suffix(".process.json"))["handle"]
                    if _winapi.WaitForSingleObject(child_handle, 5000) != _winapi.WAIT_OBJECT_0:
                        raise RuntimeError("driver owned child did not terminate within 5s")
            return None, None, time.monotonic() - started
    finally:
        if owned_child and child_handle is None and result.with_suffix(".process.json").is_file():
            import _winapi

            child_handle = _load_result(result.with_suffix(".process.json"))["handle"]
        if child_handle is not None:
            _winapi.CloseHandle(child_handle)
    completed = subprocess.CompletedProcess(command, process.returncode, stdout, stderr)
    return completed, _load_result(result) if result.is_file() else None, time.monotonic() - started


def _check_complete_run(
    driver: Path, module_path: Path, fixture: Path, root: Path, mode: str,
    expected_code: int, expect_receipt: bool,
) -> list[str]:
    completed, result, _ = _run_driver(driver, module_path, fixture, root, mode)
    failures: list[str] = []
    if completed is None or result is None:
        return [f"{mode}: driver exceeded {DRIVER_TIMEOUT_S}s"]
    if result["code"] != expected_code or completed.returncode != 0:
        failures.append(f"{mode}: return code was {result['code']} / driver {completed.returncode}")
    if completed.stdout != PAYLOAD:
        failures.append(f"{mode}: diagnostic bytes changed: {completed.stdout!r}")
    if result["receipt"] != expect_receipt:
        failures.append(f"{mode}: receipt presence was {result['receipt']}, expected {expect_receipt}")
    return failures


def _check_receipt_replay(driver: Path, module_path: Path, fixture: Path, root: Path) -> list[str]:
    completed, result, _ = _run_driver(driver, module_path, fixture, root, "receipt")
    if completed is None or result is None:
        return [f"receipt: driver exceeded {DRIVER_TIMEOUT_S}s"]
    failures: list[str] = []
    if completed.returncode != 0 or result["code"] != 0:
        failures.append(f"receipt: initial pass failed: {result['code']}")
    if not result["receipt"] or not result["replayed"]:
        failures.append("receipt: complete capture did not replay")
    if completed.stdout.count(PAYLOAD) != 2 or b"buildlock: replaying" not in completed.stdout:
        failures.append(f"receipt: replay output was not preserved: {completed.stdout!r}")
    return failures


def _check_eof_while_child_lives(
    driver: Path, module_path: Path, fixture: Path, root: Path
) -> list[str]:
    completed, result, _ = _run_driver(driver, module_path, fixture, root, "eof-live")
    if completed is None or result is None:
        return [f"eof-live: driver exceeded {DRIVER_TIMEOUT_S}s"]
    failures: list[str] = []
    if completed.returncode != 0 or result["code"] != 0:
        failures.append(f"eof-live: EOF with live child failed: {result['code']}")
    if not result["receipt"] or not result["replayed"]:
        failures.append("eof-live: genuine EOF did not produce a replayable complete receipt")
    if result["elapsed"] < 0.2:
        failures.append(f"eof-live: direct child was not allowed to remain live: {result['elapsed']:.2f}s")
    return failures


def _check_main_lock_release(
    driver: Path, module_path: Path, fixture: Path, root: Path
) -> list[str]:
    completed, result, _ = _run_driver(
        driver, module_path, fixture, root, "main-hold", timeout_s=MAIN_HOLD_TIMEOUT_S
    )
    if completed is None or result is None:
        return [f"main-hold: driver exceeded {MAIN_HOLD_TIMEOUT_S}s"]
    failures: list[str] = []
    if result["code"] != 1 or result["receipt"]:
        failures.append(f"main-hold: drain failure/receipt polarity was {result['code']}/{result['receipt']}")
    if result["elapsed"] >= DRIVER_TIMEOUT_S:
        failures.append(f"main-hold: production drain exceeded {DRIVER_TIMEOUT_S}s: {result['elapsed']:.2f}s")
    if result["independent_code"] != 0:
        failures.append(f"main-hold: independent command could not acquire released lock: {result['independent_code']}")
    if b"drain" not in completed.stderr.lower() and b"capture" not in completed.stderr.lower():
        failures.append(f"main-hold: no actionable drain diagnostic: {completed.stderr!r}")

    retained, retained_result, _ = _run_driver(
        driver, module_path, fixture, root, "main-hold-retained", timeout_s=MAIN_HOLD_TIMEOUT_S
    )
    if retained is None or retained_result is None:
        failures.append(f"main-hold-retained: driver exceeded {MAIN_HOLD_TIMEOUT_S}s")
    elif retained_result["independent_code"] != 75:
        failures.append(
            "main-hold-retained: retained-lock probe did not remain blocked: "
            f"{retained_result['independent_code']}"
        )
    return failures


def _check_held_run(driver: Path, module_path: Path, fixture: Path, root: Path, mode: str) -> list[str]:
    completed, result, elapsed = _run_driver(driver, module_path, fixture, root, mode)
    failures: list[str] = []
    if completed is None or result is None:
        return [f"{mode}: wrapper exceeded {DRIVER_TIMEOUT_S}s while a descendant held the pipe"]
    if elapsed >= DRIVER_TIMEOUT_S or result["elapsed"] >= DRIVER_TIMEOUT_S:
        failures.append(f"{mode}: drain was not bounded: outer={elapsed:.2f}s inner={result['elapsed']:.2f}s")
    if result["code"] != 1 or completed.returncode != 0:
        failures.append(f"{mode}: drain failure was not ordinary failure: {result['code']}")
    if result["receipt"] or result["replayed"]:
        failures.append(f"{mode}: an incomplete capture was replayable")
    if b"drain" not in completed.stderr.lower() and b"capture" not in completed.stderr.lower():
        failures.append(f"{mode}: no actionable drain diagnostic: {completed.stderr!r}")
    return failures


def _check_driver_timeout_cleanup(module_path: Path, root: Path) -> list[str]:
    """A refused Windows driver must not leave its file-owning child behind."""
    if os.name != "nt":
        return []  # This control observes Windows open-file deletion semantics.
    driver = root / "timeout-driver.py"
    marker = root / "timeout-cleanup.result.json"
    owned_file = root / "timeout-cleanup.receipt.lock"
    child_code = (
        "import json,pathlib,sys,time\n"
        "handle=open(sys.argv[1],'wb')\n"
        "pathlib.Path(sys.argv[2]).write_text(json.dumps({'file_owned':True}))\n"
        "time.sleep(30)\n"
    )
    driver.write_text(
        "import json,pathlib,subprocess,sys,time\n"
        + REGISTER_OWNED_CHILD +
        f"child_code={child_code!r}\n"
        "child=subprocess.Popen([sys.executable,'-c',child_code,"
        "sys.argv[4].replace('.json','.lock'),sys.argv[3]],"
        # Inherit the captured pipes and separately register the exact child termination handle.
        "close_fds=True)\n"
        "register_owned_child(child,sys.argv[3])\n"
        "time.sleep(30)\n",
        encoding="utf-8",
    )
    started = time.monotonic()
    completed, result, _ = _run_driver(
        driver, module_path, driver, root, "timeout-cleanup", owned_child=True
    )
    if completed is not None or result is not None:
        return ["timeout-cleanup: forced driver timeout was not refused"]
    if not marker.is_file() or not _load_result(marker).get("file_owned"):
        return ["timeout-cleanup: child did not establish open-file ownership"]
    try:
        owned_file.unlink()
    except OSError as error:
        return [f"timeout-cleanup: refused driver left its child's file open: {error}"]
    if time.monotonic() - started >= 30:
        return ["timeout-cleanup: ownership lease expired before cleanup was observed"]
    return []


def _check_tree_cleanup_waits_for_descendant_eof(module_path: Path, root: Path) -> list[str]:
    """A successful tree-kill result is not proof that an owned descendant closed its file."""
    if os.name != "nt":
        return []
    driver = root / "tree-cleanup-driver.py"
    marker = root / "tree-cleanup.result.json"
    owned_file = root / "tree-cleanup.receipt.lock"
    cleanup_returned = root / "tree-cleanup.returned"
    child_observed = root / "tree-cleanup.observed"
    release_child = root / "tree-cleanup.release"
    child_closed = root / "tree-cleanup.closed"
    child_code = (
        "import json,pathlib,sys,time\n"
        "handle=open(sys.argv[1],'wb')\n"
        "pathlib.Path(sys.argv[2]).write_text(json.dumps({'file_owned':True}))\n"
        "deadline=time.monotonic()+15\n"
        "while not pathlib.Path(sys.argv[3]).exists() and time.monotonic()<deadline: time.sleep(.01)\n"
        "if not pathlib.Path(sys.argv[3]).exists(): raise SystemExit('cleanup marker unavailable')\n"
        "pathlib.Path(sys.argv[4]).write_text('observed')\n"
        # Reproduce termination ordering: inherited pipes can close before the file handle.
        "import os\n"
        "os.close(1)\n"
        "os.close(2)\n"
        "while not pathlib.Path(sys.argv[5]).exists() and time.monotonic()<deadline: time.sleep(.01)\n"
        "if not pathlib.Path(sys.argv[5]).exists(): raise SystemExit('release marker unavailable')\n"
        "handle.close()\n"
        "pathlib.Path(sys.argv[6]).write_text('closed')\n"
    )
    driver.write_text(
        "import json,pathlib,subprocess,sys,time\n"
        + REGISTER_OWNED_CHILD +
        f"child_code={child_code!r}\n"
        "result=pathlib.Path(sys.argv[3])\n"
        "base=result.parent\n"
        "child=subprocess.Popen([sys.executable,'-c',child_code,"
        "str(pathlib.Path(sys.argv[4]).with_suffix('.lock')),str(result),"
        "str(base/'tree-cleanup.returned'),str(base/'tree-cleanup.observed'),"
        "str(base/'tree-cleanup.release'),str(base/'tree-cleanup.closed')],"
        # Keep inherited stdout/stderr, then close them early to distinguish EOF from termination.
        "close_fds=True)\n"
        "register_owned_child(child,result)\n"
        "time.sleep(30)\n",
        encoding="utf-8",
    )
    real_run = subprocess.run
    failures: list[str] = []
    release_timer: threading.Timer | None = None

    def return_before_descendant_exit(args: Any, **kwargs: Any) -> subprocess.CompletedProcess[bytes]:
        nonlocal release_timer
        if isinstance(args, (list, tuple)) and args and args[0] == "taskkill":
            cleanup_returned.write_text("returned", encoding="utf-8")
            deadline = time.monotonic() + 2
            while not child_observed.exists() and time.monotonic() < deadline:
                time.sleep(0.01)
            if not child_observed.exists():
                raise RuntimeError("tree-cleanup: child did not observe cleanup return")
            release_timer = threading.Timer(
                1.5, lambda: release_child.write_text("release", encoding="utf-8")
            )
            release_timer.start()
            return subprocess.CompletedProcess(args, 0, b"", b"")
        return real_run(args, **kwargs)

    try:
        with patch.object(subprocess, "run", side_effect=return_before_descendant_exit):
            completed, result, _ = _run_driver(
                driver, module_path, driver, root, "tree-cleanup", owned_child=True
            )
        if completed is not None or result is not None:
            failures.append("tree-cleanup: refused driver was not reported as a timeout")
        if not child_closed.is_file():
            failures.append("tree-cleanup: wrapper returned before its file-owning descendant closed")
        try:
            owned_file.unlink()
        except OSError as error:
            failures.append(f"tree-cleanup: descendant file remained open after wrapper returned: {error}")
    finally:
        if release_timer is not None:
            release_timer.cancel()
            release_timer.join(timeout=2)
        release_child.touch(exist_ok=True)
        deadline = time.monotonic() + 5
        while not child_closed.exists() and time.monotonic() < deadline:
            time.sleep(0.02)
    return failures


def _check_root_exit_before_pipe_eof(module_path: Path, root: Path) -> list[str]:
    """A naturally exited root must not be taskkilled by a now-stale PID."""
    if os.name != "nt":
        return []
    driver = root / "natural-root-exit-driver.py"
    marker = root / "natural-root-exit.result.json"
    child_closed = root / "natural-root-exit.closed"
    driver.write_text(
        "import subprocess,sys\n"
        "subprocess.Popen([sys.executable,'-c',"
        "'import pathlib,sys,time; time.sleep(2); pathlib.Path(sys.argv[1]).write_text(\"closed\")',"
        "sys.argv[3].replace('.result.json','.closed')],close_fds=True)\n"
        "open(sys.argv[3],'w').write('root-exited')\n",
        encoding="utf-8",
    )
    completed, result, _ = _run_driver(
        driver, module_path, driver, root, "natural-root-exit", timeout_s=1.0
    )
    if completed is not None or result is not None:
        return ["natural-root-exit: wrapper did not report its bounded timeout"]
    if not marker.is_file() or not child_closed.is_file():
        return ["natural-root-exit: root/descendant EOF barrier was not observed"]
    return []


def _check_owned_child_wait_failure(module_path: Path, root: Path) -> list[str]:
    """A missing child termination signal stays loud and closes the transferred handle."""
    if os.name != "nt":
        return []
    import _winapi

    real_wait = _winapi.WaitForSingleObject
    real_close = _winapi.CloseHandle
    refused: list[int] = []
    closed: list[int] = []
    marker = root / "timeout-cleanup.result.process.json"

    def refuse_owned_wait(handle: int, timeout_ms: int) -> int:
        if marker.is_file() and handle == _load_result(marker)["handle"] and timeout_ms == 5000:
            refused.append(handle)
            return _winapi.WAIT_TIMEOUT
        return real_wait(handle, timeout_ms)

    def close_handle(handle: int) -> None:
        closed.append(handle)
        real_close(handle)

    with patch.object(_winapi, "WaitForSingleObject", side_effect=refuse_owned_wait), \
            patch.object(_winapi, "CloseHandle", side_effect=close_handle):
        try:
            _check_driver_timeout_cleanup(module_path, root)
        except RuntimeError as error:
            if str(error) != "driver owned child did not terminate within 5s":
                return [f"owned-child-wait: unexpected refusal: {error}"]
        else:
            return ["owned-child-wait: missing termination signal was accepted"]
    if len(refused) != 1 or refused[0] not in closed:
        return ["owned-child-wait: transferred handle was not waited on and closed"]
    return []


def _check_driver_cleanup_failure_reaps_root(module_path: Path, root: Path) -> list[str]:
    """Failure of the Windows tree-kill helper stays loud and still reaps our root."""
    if os.name != "nt":
        return []
    driver = root / "cleanup-failure-driver.py"
    driver.write_text("import time\ntime.sleep(30)\n", encoding="utf-8")
    original_popen = subprocess.Popen
    failures: list[str] = []
    for error in (OSError("fixture launch failure"), subprocess.TimeoutExpired(["taskkill"], 5)):
        owned: list[subprocess.Popen[bytes]] = []

        def launch(*args: Any, **kwargs: Any) -> subprocess.Popen[bytes]:
            process = original_popen(*args, **kwargs)
            owned.append(process)
            return process

        try:
            with patch.object(subprocess, "Popen", side_effect=launch), \
                    patch.object(subprocess, "run", side_effect=error):
                try:
                    _run_driver(driver, module_path, driver, root, "cleanup-failure")
                except type(error):
                    pass
                else:
                    failures.append(f"cleanup-failure: {type(error).__name__} was hidden")
            if len(owned) != 1 or owned[0].poll() is None:
                failures.append(f"cleanup-failure: {type(error).__name__} left the owned root alive")
        finally:
            for process in owned:
                if process.poll() is None:
                    process.kill()
                process.communicate(timeout=5)
    return failures


def selftest(module_path: str | os.PathLike[str] | None = None) -> bool:
    """Run bounded controls against the supplied production buildlock module."""
    production = Path(module_path) if module_path is not None else Path(__file__).with_name("buildlock.py")
    production = production.resolve()
    failures: list[str] = []
    with tempfile.TemporaryDirectory(prefix="buildlock-drain-") as temp:
        root = Path(temp)
        fixture = root / "fixture.py"
        driver = root / "driver.py"
        fixture.write_text(FIXTURE, encoding="utf-8", newline="\n")
        driver.write_text(DRIVER, encoding="utf-8", newline="\n")
        failures += _check_complete_run(driver, production, fixture, root, "complete", 0, True)
        failures += _check_complete_run(driver, production, fixture, root, "nonzero", 7, False)
        failures += _check_receipt_replay(driver, production, fixture, root)
        failures += _check_eof_while_child_lives(driver, production, fixture, root)
        failures += _check_held_run(driver, production, fixture, root, "hold-stdout")
        failures += _check_held_run(driver, production, fixture, root, "hold-stderr")
        failures += _check_held_run(driver, production, fixture, root, "hold-child75")
        failures += _check_main_lock_release(driver, production, fixture, root)
        failures += _check_driver_timeout_cleanup(production, root)
        failures += _check_tree_cleanup_waits_for_descendant_eof(production, root)
        failures += _check_root_exit_before_pipe_eof(production, root)
        failures += _check_owned_child_wait_failure(production, root)
        failures += _check_driver_cleanup_failure_reaps_root(production, root)
    if failures:
        print("buildlock drain selftest: FAIL", file=sys.stderr)
        for failure in failures:
            print(f"  {failure}", file=sys.stderr)
        return False
    print("buildlock drain selftest: pass (complete, nonzero, replay, stdout/stderr drain arms)")
    return True


def main(module_path: str | os.PathLike[str] | None = None) -> int:
    if module_path is None and len(sys.argv) > 1:
        module_path = sys.argv[1]
    return 0 if selftest(module_path) else 1


if __name__ == "__main__":
    raise SystemExit(main())

