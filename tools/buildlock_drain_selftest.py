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
import time
from typing import Any


PAYLOAD = b"stdout-diagnostic\x00\xff\n" + b"stderr-diagnostic\x01\xfe\n"
HOLDER_LIFETIME_S = 4.5
DRIVER_TIMEOUT_S = 3.5


FIXTURE = r'''
import os
import subprocess
import sys

mode = sys.argv[1]
if mode == "complete":
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

module_path, fixture_path, result_path, receipt_path, mode, holder_lifetime = sys.argv[1:]
spec = importlib.util.spec_from_file_location("buildlock_under_test", module_path)
if spec is None or spec.loader is None:
    raise RuntimeError("cannot load buildlock module")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

receipt = Path(receipt_path)
inputs = (receipt, "fixture-fingerprint")
module.replay_inputs = lambda command, priority_class: inputs
fixture_mode = "complete" if mode == "receipt" else ("hold-stdout" if mode == "main-hold" else mode)
command = [sys.executable, fixture_path, fixture_mode]
if mode in ("hold-stdout", "hold-stderr", "hold-child75", "main-hold"):
    command.append(holder_lifetime)
env = dict(os.environ)

if mode == "main-hold":
    lock_path = str(Path(receipt_path).with_suffix(".lock"))
    for name in ("BATON_BUILDLOCK_HELD", "BATON_BUILDLOCK_WAIT_LOG", "BATON_LOCK_WAIT_LOG"):
        os.environ.pop(name, None)
    os.environ["BATON_BUILDLOCK_FILE"] = lock_path
    os.environ["BATON_BUILDLOCK_TIMEOUT_S"] = "2"
    sys.argv = [module_path, "--replay", *command]
    started = time.monotonic()
    code = module.main()
    elapsed = time.monotonic() - started
    independent_env = dict(os.environ)
    for name in ("BATON_BUILDLOCK_HELD", "BATON_BUILDLOCK_WAIT_LOG", "BATON_LOCK_WAIT_LOG"):
        independent_env.pop(name, None)
    independent = subprocess.Popen(
        [sys.executable, module_path, sys.executable, fixture_path, "complete"],
        env=independent_env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    try:
        independent_stdout, independent_stderr = independent.communicate(timeout=2.5)
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
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        stdout, stderr = process.communicate(timeout=DRIVER_TIMEOUT_S)
    except subprocess.TimeoutExpired:
        process.kill()
        process.communicate(timeout=5)
        return None, None, time.monotonic() - started
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
    completed, result, _ = _run_driver(driver, module_path, fixture, root, "main-hold")
    if completed is None or result is None:
        return [f"main-hold: driver exceeded {DRIVER_TIMEOUT_S}s"]
    failures: list[str] = []
    if result["code"] != 1 or result["receipt"]:
        failures.append(f"main-hold: drain failure/receipt polarity was {result['code']}/{result['receipt']}")
    if result["independent_code"] != 0:
        failures.append(f"main-hold: independent command could not acquire released lock: {result['independent_code']}")
    if b"drain" not in completed.stderr.lower() and b"capture" not in completed.stderr.lower():
        failures.append(f"main-hold: no actionable drain diagnostic: {completed.stderr!r}")
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

