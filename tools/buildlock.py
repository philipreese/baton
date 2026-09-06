"""User-global build lock: one MSBuild-heavy command at a time, across every worktree (#1402).

("User" rather than "host": the lock file lives in the per-user temp dir. One user on one
Windows box is the whole deployment (#1405), so the distinction is academic here.)

Two concurrent MSBuild runs on this machine kill each other (MSB4166, zero-test test legs,
vanished obj/ -- the 2026-08-04 mutual-kill catalogue). The old protection was doctrine: "one
implement lane at a time", which serializes WHOLE lanes to protect the ~20% of their wall-clock
that is build. This puts the check in the tool instead: every MSBuild-owning pixi task runs
through this wrapper, so any number of concurrent lanes queue automatically at the build itself,
and a worker that never heard of the rule still obeys it.

Windows-only, deliberately (#1405): the lock is an OS-level region lock (msvcrt.locking) on a
file in the machine's temp directory. The kernel releases it the instant the holding process
dies, however it dies -- so there is no stale-lock file to detect, no PID-liveness check, and no
steal logic. A crashed holder frees the lock by crashing.

Usage:            python tools/buildlock.py [--class build|readonly] <command> [args...]
Priority classes: TWO, and the whole difference is whether the command can start an MSBuild (#1910).
                  `build` (the default, and what every pixi task that runs `dotnet` uses) queues for
                  the exclusive lock exactly as described above. `readonly` declares that the command
                  starts no MSBuild and therefore has nothing to serialize against: it runs
                  IMMEDIATELY, never queueing behind a build or a test run. That is a priority, not a
                  bypass -- a `readonly` command whose argv looks MSBuild-owning (`starts_msbuild`
                  below) is REFUSED with exit 2 rather than run outside the exclusion.
                  THE GUARD'S EXACT REACH, since it is narrower than "cannot smuggle a build past the
                  lock" (#1936 review): it reads the argv it is handed and catches a DIRECT
                  `dotnet`/`msbuild` invocation on a build verb. An indirect launcher carries no such
                  token and is NOT detected -- `pixi run lint`, `sh -c "dotnet build"`, `cmd /c
                  build.cmd` (the verb is inside one argv element) all pass it, and they are pinned as
                  fixtures in the selftest so the claim and the code stay the same width. So the guard
                  is a tripwire on the one task line that declares this class, not a sandbox around an
                  arbitrary command, which is why the class stays restricted to that line rather than
                  being offered as a general opt-out. `pixi run gates-check-receipt` is
                  the caller this exists for: a push's receipt check is pure git and file reads, and
                  making it wait behind a lane's `dotnet test` was the largest single source of push
                  latency measured on 2026-09-05 (spec/baton.md C-12, ruling C).
Wait accounting:  BATON_BUILDLOCK_WAIT_LOG -- when set, a `build`-class run that actually WAITED
                  appends the milliseconds it waited, one integer per line, to that path. Unset by
                  default and read by nothing else. `.githooks/pre-push` is the only intended setter,
                  and the attribution is only honest because of that: this wrapper runs for every
                  `dotnet` invocation in a lane, so a shell that exports this variable globally
                  accumulates the lane's own build waits into whatever is reading the file.
Diagnostics:      a sidecar .info file (never locked) names the holder -- PID, command, start
                  time -- so the wait message can say WHO it is waiting on.
Nesting:          a wrapped command that itself runs wrapped tasks would deadlock on its own
                  lock; the wrapper exports BATON_BUILDLOCK_HELD=<pid> to its child. The marker
                  is only an env var and can outlive its setter (a detached grandchild, a
                  debugging shell that exported it by hand), so it is treated as a HINT, not a
                  grant: an inheritor probes the lock with one non-blocking acquire. Free lock
                  means the marker was stale -- take the lock properly. Held lock means the
                  holder is overwhelmingly the ancestor that set the marker -- run directly.
                  Residual risk, accepted: a process carrying a stale marker while an UNRELATED
                  build holds the lock skips the queue; that needs the marker to leak AND the
                  race to land in the same window, strictly narrower than trusting the marker.
Timeout:          BATON_BUILDLOCK_TIMEOUT_S (default 1800) -- fails LOUDLY on expiry rather than
                  hanging past a lane's budget, exiting BUILDLOCK_BLOCKED_EXIT (75, chosen because no
                  `dotnet` subcommand this wrapper runs exits with it) rather than the generic 1 a
                  wrapped command's own failure would use (#1796) -- BLOCKED is contention, not a
                  broken gate, and tools/gates/gates.py (and, through it, the engine's own verify
                  step) tells the two apart by this exit code alone, never by the wrapped command's
                  actual failure semantics. BATON_BUILDLOCK_FILE overrides the lock path; anyone may
                  set it, but its intended use is selftest isolation -- overriding it elsewhere opts
                  that process out of the shared exclusion.
Selftest knob:    BATON_BUILDLOCK_SELFTEST_HOLDER_DELAY_S (default 0) -- only read by the
                  --selftest timeout-path holder, sleeps before it acquires the lock. Used to
                  falsify the fix for #1627: set to 1 against the pre-#1627 code (fixed 0.2s
                  sleep as the ordering signal) and its arm 3 fails; the current code polls the
                  holder's .info sidecar instead and still passes.
"""
import json
import os
import subprocess
import sys
import tempfile
import time
from typing import BinaryIO

HELD_MARKER = "BATON_BUILDLOCK_HELD"
POLL_S = 2.0
PROGRESS_EVERY_S = 10.0
# #1796: see the module docstring's Timeout section for why this is distinct from a plain 1.
BUILDLOCK_BLOCKED_EXIT = 75

# #1910: the two priority classes, spelled once here and described once in the module docstring.
CLASS_BUILD = "build"
CLASS_READONLY = "readonly"
CLASSES = (CLASS_BUILD, CLASS_READONLY)
WAIT_LOG_VAR = "BATON_BUILDLOCK_WAIT_LOG"

# The verbs that make a `dotnet` invocation start an MSBuild, plus msbuild itself. Deliberately a
# denylist of verbs rather than an allowlist of safe commands: the readonly class is for python and
# git, and anything shaped like a build must fail closed into the exclusive class. `dotnet
# build-server` is not here on purpose -- it SHUTS DOWN build servers and starts no project build.
MSBUILD_VERBS = frozenset({"build", "test", "format", "run", "pack", "publish", "restore", "msbuild", "clean"})


def starts_msbuild(command: list[str]) -> bool:
    """Whether `command`'s argv looks like it starts an MSBuild -- the readonly class's guard.

    Pure and fixture-tested (selftest arm below) so the refusal cannot rot into a rubber stamp: a
    readonly caller that names a build verb is refused rather than run outside the exclusion. How
    far that reaches, and what it deliberately does not catch, is the module docstring's Priority
    classes note -- read it before widening either the check or a claim made for it.
    """
    for index, token in enumerate(command):
        name = os.path.basename(token).lower()
        if name in ("msbuild", "msbuild.exe"):
            return True
        if name in ("dotnet", "dotnet.exe"):
            rest = command[index + 1:]
            return bool(rest) and rest[0].lower() in MSBUILD_VERBS
    return False


def record_wait(waited_s: float) -> None:
    """Append the milliseconds this run spent queued to BATON_BUILDLOCK_WAIT_LOG, if it is set.

    Best-effort like write_holder_info: a wait log that cannot be written is a lost measurement,
    never a reason to fail the build the caller is waiting on.
    """
    path = os.environ.get(WAIT_LOG_VAR)
    if not path:
        return
    try:
        with open(path, "a", encoding="utf-8") as f:
            f.write(f"{int(waited_s * 1000)}\n")
    except OSError:
        pass


def lock_path() -> str:
    return os.environ.get(
        "BATON_BUILDLOCK_FILE",
        os.path.join(tempfile.gettempdir(), "baton-build.lock"),
    )


def read_holder_info(path: str) -> str:
    try:
        with open(path + ".info", "r", encoding="utf-8") as f:
            info = json.load(f)
        return f"PID {info['pid']} ({info['command']}) since {info['since']}"
    except (OSError, ValueError, KeyError):
        return "an unidentified process (no .info sidecar)"


def write_holder_info(path: str, command: list[str]) -> None:
    info = {
        "pid": os.getpid(),
        "command": " ".join(command),
        "since": time.strftime("%Y-%m-%d %H:%M:%S"),
    }
    try:
        with open(path + ".info", "w", encoding="utf-8") as f:
            json.dump(info, f)
    except OSError:
        pass  # diagnostics only; never a reason to fail the build


def try_acquire_once(path: str, command: list[str]) -> "BinaryIO | None":
    """One non-blocking acquire: the handle if the lock was free, None if someone holds it."""
    import msvcrt

    handle = open(path, "a+b")  # noqa: SIM115 -- on success, held for the process lifetime
    try:
        handle.seek(0)
        msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
    except OSError:
        handle.close()
        return None
    write_holder_info(path, command)
    return handle


def acquire(path: str, command: list[str], timeout_s: float) -> BinaryIO:
    """Block until the region lock on byte 0 is ours; return the open handle keeping it.

    The handle is intentionally leaked to the end of the process: the OS releases the region
    lock at process death, which is the entire crash-safety story.
    """
    import msvcrt

    handle = open(path, "a+b")  # noqa: SIM115 -- held for the process lifetime, see above
    started = time.monotonic()
    deadline = started + timeout_s
    last_progress = 0.0
    while True:
        try:
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
            write_holder_info(path, command)
            # #1910: the queue time, recorded only when there WAS one -- the loop below sleeps
            # POLL_S between attempts, so an uncontended acquire returns here at ~0 elapsed and
            # logs nothing. A reader summing the file is summing contention, not runs.
            waited = time.monotonic() - started
            if waited >= POLL_S:
                record_wait(waited)
            return handle
        except OSError:
            now = time.monotonic()
            if now >= deadline:
                handle.close()
                record_wait(now - started)
                # #1796: BLOCKED, exit BUILDLOCK_BLOCKED_EXIT -- distinct from a real gate failure so
                # tools/gates/gates.py (and, through it, the engine's verify step) can tell "the build
                # lock was busy" from "the gate broke". BLOCKED is the leading word on this line
                # deliberately: it is the one machine-recognized marker gates.py and
                # Baton.Mutation.VerifyRunner both key off (see their own BuildLockBlockedLine/BLOCKED
                # constants) -- the rest of the message is free text for a human reader.
                print(
                    f"buildlock: BLOCKED after {timeout_s:.0f}s waiting for the build lock "
                    f"held by {read_holder_info(path)} -- raise BATON_BUILDLOCK_TIMEOUT_S or "
                    f"find out why the holder is stuck",
                    flush=True,
                )
                sys.exit(BUILDLOCK_BLOCKED_EXIT)
            if now - last_progress >= PROGRESS_EVERY_S:
                last_progress = now
                print(
                    f"buildlock: waiting for the build lock held by {read_holder_info(path)} "
                    f"({deadline - now:.0f}s until timeout)",
                    flush=True,
                )
            time.sleep(POLL_S)


def split_class(argv: list[str]) -> tuple[str, list[str]]:
    """`(priority_class, command)` from an argv -- `--class <name>` / `--class=<name>` or the default.

    Only leading, and only once: everything after the class is the command verbatim, so a wrapped
    command carrying its own `--class` flag is untouched.
    """
    if argv and argv[0].startswith("--class"):
        if argv[0] == "--class":
            return (argv[1] if len(argv) > 1 else ""), argv[2:]
        # `partition`, not `split("=", 1)[1]` (#1936 review): a typo like `--classx` starts with
        # `--class` and carries no `=`, and indexing [1] raised IndexError -- a traceback where
        # main()'s "unknown priority class" exit 2 below is the answer this file already had.
        _, sep, value = argv[0].partition("=")
        return (value if sep else ""), argv[1:]
    return CLASS_BUILD, argv


def main() -> int:
    priority_class, command = split_class(sys.argv[1:])
    if priority_class not in CLASSES:
        print(f"buildlock: unknown priority class {priority_class!r} -- one of {', '.join(CLASSES)}")
        return 2
    if not command:
        print("buildlock: no command given -- usage: python tools/buildlock.py "
              "[--class build|readonly] <command> [args...]")
        return 2

    if priority_class == CLASS_READONLY:
        # #1910: declared to start no MSBuild, so there is nothing to serialize -- run now rather
        # than queue. Refused, never silently promoted, when the argv says otherwise: a readonly
        # class that ran a build outside the exclusion would be the mutual-kill failure this whole
        # file exists to stop, wearing a flag.
        if starts_msbuild(command):
            print(f"buildlock: refusing --class {CLASS_READONLY} for a command that starts an "
                  f"MSBuild ({' '.join(command)}) -- run it in the default {CLASS_BUILD} class")
            return 2
        return subprocess.run(command, check=False).returncode

    env = dict(os.environ)
    if env.get(HELD_MARKER):
        # Marker inherited: probe, don't trust (see the module docstring's Nesting section).
        handle = try_acquire_once(lock_path(), command)
        if handle is None:
            # Lock held -- by our ancestor, per the docstring's stated residual. Run inside
            # its exclusion.
            return subprocess.run(command, env=env, check=False).returncode
    else:
        timeout_s = float(env.get("BATON_BUILDLOCK_TIMEOUT_S", "1800"))
        handle = acquire(lock_path(), command, timeout_s)
    env[HELD_MARKER] = str(os.getpid())
    try:
        return subprocess.run(command, env=env, check=False).returncode
    finally:
        import msvcrt

        try:
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
            handle.close()
        except OSError:
            pass  # process exit releases it regardless


# ---------------------------------------------------------------------------------------------
# Selftest: the three behaviours the mechanism is FOR, each proven with real processes.
# ---------------------------------------------------------------------------------------------

_CHILD_HOLD_AND_STAMP = """
import os, sys, time
sys.argv = [sys.argv[0], sys.executable, "-c",
    "import time,sys; open(sys.argv[1],'a').write(f'{time.monotonic()} start\\\\n'); "
    "time.sleep(0.6); open(sys.argv[1],'a').write(f'{time.monotonic()} end\\\\n')",
    sys.argv[1]]
sys.exit(__import__('buildlock').main())
"""

_CHILD_ACQUIRE_AND_DIE = """
import os, sys
import buildlock
handle = buildlock.acquire(buildlock.lock_path(), ["deliberate-crash"], 5.0)
os._exit(0)  # dies holding the lock -- the OS must release it
"""

_CHILD_ACQUIRE_AND_SLEEP = """
import os, time
import buildlock
delay = float(os.environ.get("BATON_BUILDLOCK_SELFTEST_HOLDER_DELAY_S", "0"))
if delay:
    time.sleep(delay)
handle = buildlock.acquire(buildlock.lock_path(), ["slow-holder"], 5.0)
time.sleep(float(os.environ.get("BATON_BUILDLOCK_SELFTEST_HOLD_S", "3")))
"""


def _wait_for_holder(lock_file: str, holder_pid: int, ceiling_s: float = 10.0) -> bool:
    """Poll the .info sidecar until it names holder_pid, or ceiling_s elapses.

    Replaces a fixed sleep as the ordering signal between a selftest's holder and waiter
    children: the sidecar is written (write_holder_info) only after the holder's msvcrt lock
    acquisition succeeds, so its presence is a direct acquisition signal rather than a guess
    at how long acquisition takes on a loaded host.
    """
    deadline = time.monotonic() + ceiling_s
    while time.monotonic() < deadline:
        try:
            with open(lock_file + ".info", "r", encoding="utf-8") as f:
                info = json.load(f)
            if info.get("pid") == holder_pid:
                return True
        except (OSError, ValueError, KeyError):
            pass
        time.sleep(0.02)
    return False


def _selftest_env(**overrides: str) -> dict[str, str]:
    """This process's environment minus the two variables a selftest child must never inherit.

    HELD_MARKER: an inherited nesting marker would let a child skip the acquisition these arms are
    measuring.

    WAIT_LOG_VAR (#1936 review): `.githooks/pre-push` exports it for a whole `gates --fast` run, and
    `buildlock-selftest` is one of that run's members -- so its children's DELIBERATE contention
    (arm 1's loser waiting out a POLL_S tick, arms 3 and 5 timing out on purpose) was appended to the
    push's own log, about 4s of fabricated queueing against a temp lock file. It landed on the
    fallback path only, which is one half of exactly the before/after comparison C-12's ruling C
    exists to drive. Arm 7 sets its own log path explicitly on top of this.
    """
    env = dict(os.environ)
    env.pop(HELD_MARKER, None)
    env.pop(WAIT_LOG_VAR, None)
    env.update(overrides)
    return env


def _spawn_selftest_child(code: str, lock_file: str, *args: str, hold_s: str | None = None) -> subprocess.Popen:
    env = _selftest_env(
        BATON_BUILDLOCK_FILE=lock_file,
        BATON_BUILDLOCK_TIMEOUT_S="20",
        PYTHONPATH=os.path.dirname(os.path.abspath(__file__)),
    )
    if hold_s is not None:
        env["BATON_BUILDLOCK_SELFTEST_HOLD_S"] = hold_s
    return subprocess.Popen(
        [sys.executable, "-c", code, *args], env=env,
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
    )


def selftest() -> int:
    ok = True
    with tempfile.TemporaryDirectory() as td:
        lock_file = os.path.join(td, "selftest.lock")
        stamps = os.path.join(td, "stamps.txt")

        # #1936 review, and the one arm that covers every child spawned below: an INHERITED wait log
        # must collect nothing from this file's synthetic contention (`_selftest_env` says why that
        # matters). Set for the whole selftest and asserted empty at the end, so a child env that
        # ever stops scrubbing it is caught wherever it is spawned. Restored below; nothing else in
        # this process reads it.
        inherited_log = os.path.join(td, "inherited-wait.log")
        prior_wait_log = os.environ.get(WAIT_LOG_VAR)
        os.environ[WAIT_LOG_VAR] = inherited_log

        # 1. Two wrapped commands started together must serialize (no interval overlap).
        a = _spawn_selftest_child(_CHILD_HOLD_AND_STAMP, lock_file, stamps)
        b = _spawn_selftest_child(_CHILD_HOLD_AND_STAMP, lock_file, stamps)
        # Every wait below is bounded: a regression that makes the mechanism HANG (the exact
        # anti-pattern the timeout exists to prevent) must fail this selftest loudly, not hang
        # the gate that runs it.
        a.communicate(timeout=30), b.communicate(timeout=30)
        if a.returncode != 0 or b.returncode != 0:
            print(f"  control FAILED: wrapped commands exited {a.returncode}/{b.returncode}")
            ok = False
        else:
            with open(stamps, encoding="utf-8") as f:
                lines = [line.split() for line in f.read().splitlines()]
            intervals, current = [], None
            for ts, kind in lines:
                if kind == "start":
                    current = float(ts)
                else:
                    intervals.append((current, float(ts)))
            intervals.sort()
            if len(intervals) != 2 or intervals[0][1] > intervals[1][0]:
                print(f"  control FAILED: hold intervals overlap -- {intervals}")
                ok = False

        # 2. A holder that dies without releasing must free the lock (OS-level release).
        crasher = _spawn_selftest_child(_CHILD_ACQUIRE_AND_DIE, lock_file)
        crasher.communicate(timeout=30)
        env = _selftest_env(BATON_BUILDLOCK_FILE=lock_file, BATON_BUILDLOCK_TIMEOUT_S="3")
        after = subprocess.run(
            [sys.executable, os.path.abspath(__file__), sys.executable, "-c", "pass"],
            env=env, capture_output=True, text=True, check=False, timeout=30,
        )
        if after.returncode != 0:
            print(f"  control FAILED: lock survived its holder's death -- {after.stdout}")
            ok = False

        # 3. The timeout path must fail loudly, not hang: waiter with a 1s budget against a
        #    holder that sleeps well past it. Ordering is proven by the holder's .info sidecar
        #    (written only once its msvcrt lock acquisition succeeds), not a fixed sleep guessing
        #    how long acquisition takes -- a fixed sleep loses the race under host load (#1627).
        #    The hold is 60s, not the 3s this arm used before #1910: `_wait_for_holder` proves the
        #    holder ACQUIRED, and nothing bounded how long the waiter's own python startup then took
        #    against a 3s hold -- on a loaded host (measured 2026-09-06, this arm red inside a
        #    `gates` run overlapping lint's full rebuild) the waiter started after the holder had
        #    already released, acquired the free lock, and exited 0 where 75 was wanted. A hold that
        #    outlasts any plausible startup removes that race without weakening the assertion: the
        #    waiter must still BLOCK on its 1s budget. Terminated below, so the long hold costs no
        #    wall clock.
        holder = _spawn_selftest_child(_CHILD_ACQUIRE_AND_SLEEP, lock_file, hold_s="60")
        if not _wait_for_holder(lock_file, holder.pid, ceiling_s=10.0):
            print(
                "  control FAILED: holder never signaled lock acquisition within 10s "
                "(distinct from the timeout-path assertion below)"
            )
            ok = False
            holder.terminate()
            holder.communicate(timeout=30)
        else:
            env["BATON_BUILDLOCK_TIMEOUT_S"] = "1"
            waiter = subprocess.run(
                [sys.executable, os.path.abspath(__file__), sys.executable, "-c", "pass"],
                env=env, capture_output=True, text=True, check=False, timeout=30,
            )
            holder.terminate()
            holder.communicate(timeout=30)
            if waiter.returncode != BUILDLOCK_BLOCKED_EXIT or "buildlock: BLOCKED" not in waiter.stdout:
                print(
                    f"  control FAILED: timeout path exited {waiter.returncode} (want "
                    f"{BUILDLOCK_BLOCKED_EXIT}) without a loud BLOCKED message -- {waiter.stdout!r}"
                )
                ok = False

        # 4. A stale inherited marker with a FREE lock must be probed, not trusted: the run
        #    must take the lock properly (visible via the .info sidecar it writes) rather than
        #    skipping acquisition.
        try:
            os.remove(lock_file + ".info")
        except OSError:
            pass
        env[HELD_MARKER] = "999999"  # nobody's pid; simulates a marker that outlived its setter
        env["BATON_BUILDLOCK_TIMEOUT_S"] = "5"
        stale = subprocess.run(
            [sys.executable, os.path.abspath(__file__), sys.executable, "-c", "pass"],
            env=env, capture_output=True, text=True, check=False, timeout=30,
        )
        if stale.returncode != 0 or not os.path.exists(lock_file + ".info"):
            print(
                f"  control FAILED: stale marker + free lock exited {stale.returncode}; "
                f".info written: {os.path.exists(lock_file + '.info')} -- the probe path "
                f"trusted the marker instead of taking the free lock"
            )
            ok = False

        # 5. #1910, the priority classes, against a REAL lock holder: a readonly command must run
        #    while the lock is held, and a build-class command must not. Both arms in one window
        #    against the same holder -- the readonly arm alone would pass on a machine where the
        #    holder never acquired, and the build arm is what proves the lock was genuinely held.
        env.pop(HELD_MARKER, None)
        # A 60s hold, not the 3s arm 3 uses: BOTH probes below have to run inside one hold, and a
        # loaded host (three lanes building, which is the very condition this priority class exists
        # for) can spend seconds just starting a python process. The holder is terminated the moment
        # the probes are done, so the long hold costs no wall clock -- it only removes the race.
        holder = _spawn_selftest_child(_CHILD_ACQUIRE_AND_SLEEP, lock_file, hold_s="60")
        if not _wait_for_holder(lock_file, holder.pid, ceiling_s=10.0):
            print("  control FAILED: holder never signaled lock acquisition within 10s "
                  "(priority-class arm)")
            ok = False
            holder.terminate()
            holder.communicate(timeout=30)
        else:
            env["BATON_BUILDLOCK_TIMEOUT_S"] = "20"
            started = time.monotonic()
            readonly = subprocess.run(
                [sys.executable, os.path.abspath(__file__), "--class", CLASS_READONLY,
                 sys.executable, "-c", "pass"],
                env=env, capture_output=True, text=True, check=False, timeout=30,
            )
            readonly_s = time.monotonic() - started

            env["BATON_BUILDLOCK_TIMEOUT_S"] = "1"
            queued = subprocess.run(
                [sys.executable, os.path.abspath(__file__), sys.executable, "-c", "pass"],
                env=env, capture_output=True, text=True, check=False, timeout=30,
            )
            holder.terminate()
            holder.communicate(timeout=30)

            if readonly.returncode != 0 or readonly_s >= 5.0:
                print(
                    f"  control FAILED: a readonly command queued behind the held lock -- exit "
                    f"{readonly.returncode} after {readonly_s:.2f}s (want exit 0, far under the "
                    f"holder's hold)"
                )
                ok = False
            if queued.returncode != BUILDLOCK_BLOCKED_EXIT:
                print(
                    f"  control FAILED: the default build class did not queue behind the same "
                    f"holder -- exit {queued.returncode}, want {BUILDLOCK_BLOCKED_EXIT}"
                )
                ok = False

        # 6. #1910: readonly is a priority, not a bypass -- an MSBuild-shaped command is refused.
        #    Fixture-driven both ways, so a guard that matched everything (or nothing) fails here.
        for argv, want in (
            ([sys.executable, "-c", "pass"], False),
            (["git", "status", "--porcelain"], False),
            (["dotnet", "build-server", "shutdown"], False),
            (["dotnet", "build", "-warnaserror"], True),
            ([r"C:\Program Files\dotnet\dotnet.exe", "test", "--no-build"], True),
            (["msbuild", "Baton.slnx"], True),
            # The DOCUMENTED non-detections (#1936 review), pinned here so the module docstring's
            # claim and this function keep the same width: an indirect launcher hides the token
            # from an argv reader, and `watch` is not itself a build verb. Each of these DOES start
            # an MSBuild when run; none is caught, which is the reason the class stays confined to
            # one task line rather than being a general opt-out.
            (["pixi", "run", "lint"], False),
            (["sh", "-c", "dotnet build"], False),
            (["cmd", "/c", "build.cmd"], False),
            (["dotnet", "watch", "build"], False),
        ):
            if starts_msbuild(argv) != want:
                print(f"  control FAILED: starts_msbuild({argv}) != {want}")
                ok = False

        refused = subprocess.run(
            [sys.executable, os.path.abspath(__file__), "--class", CLASS_READONLY,
             "dotnet", "build"],
            env=env, capture_output=True, text=True, check=False, timeout=30,
        )
        if refused.returncode != 2 or "refusing" not in refused.stdout:
            print(
                f"  control FAILED: --class readonly ran an MSBuild command instead of refusing "
                f"it -- exit {refused.returncode}, stdout {refused.stdout!r}"
            )
            ok = False

        # 6b. #1936 review: a readonly command's NONZERO exit must propagate. `gates-check-receipt`
        #     is composed as `buildlock.py --class readonly python gates.py --check-receipt`
        #     (pixi.toml) and .githooks/pre-push's whole decision is that exit code, so a readonly
        #     path that ever swallowed it into a constant 0 would make every push skip the gates
        #     silently -- and arm 5's readonly probe, which runs a command that exits 0, could not
        #     tell. 3 is arbitrary, nonzero, and distinct from the 2 a refusal uses.
        propagated = subprocess.run(
            [sys.executable, os.path.abspath(__file__), "--class", CLASS_READONLY,
             sys.executable, "-c", "import sys; sys.exit(3)"],
            env=env, capture_output=True, text=True, check=False, timeout=30,
        )
        if propagated.returncode != 3:
            print(
                f"  control FAILED: a --class readonly command's exit 3 did not propagate -- got "
                f"{propagated.returncode}"
            )
            ok = False

        # 6c. #1936 review: `--class` parsing over the forms a caller can actually type. The
        #     `--classx` rows are the regression: that argv starts with `--class`, carries no `=`,
        #     and used to raise IndexError instead of reaching the unknown-class refusal.
        for parsed, want in (
            (["--class", CLASS_READONLY, "python"], (CLASS_READONLY, ["python"])),
            ([f"--class={CLASS_READONLY}", "python"], (CLASS_READONLY, ["python"])),
            (["--classx", "python"], ("", ["python"])),
            (["--class"], ("", [])),
            (["python", "--class=whatever"], (CLASS_BUILD, ["python", "--class=whatever"])),
        ):
            if split_class(parsed) != want:
                print(f"  control FAILED: split_class({parsed}) -> {split_class(parsed)}, want {want}")
                ok = False

        typo = subprocess.run(
            [sys.executable, os.path.abspath(__file__), "--classx", sys.executable, "-c", "pass"],
            env=env, capture_output=True, text=True, check=False, timeout=30,
        )
        if typo.returncode != 2 or "unknown priority class" not in typo.stdout:
            print(
                f"  control FAILED: a `--class` typo did not reach the unknown-class refusal -- "
                f"exit {typo.returncode}, stdout {typo.stdout!r}, stderr {typo.stderr!r}"
            )
            ok = False

        # 7. #1910: the wait log records a run that WAITED and stays silent for one that did not.
        #    Both polarities: a log written unconditionally would make every uncontended build look
        #    like contention on the row the ledger reads.
        wait_log = os.path.join(td, "wait.log")
        env["BATON_BUILDLOCK_WAIT_LOG"] = wait_log
        env["BATON_BUILDLOCK_TIMEOUT_S"] = "20"
        env.pop(HELD_MARKER, None)
        uncontended = subprocess.run(
            [sys.executable, os.path.abspath(__file__), sys.executable, "-c", "pass"],
            env=env, capture_output=True, text=True, check=False, timeout=30,
        )
        if uncontended.returncode != 0 or os.path.exists(wait_log):
            print(
                f"  control FAILED: an uncontended acquire wrote a wait log -- exit "
                f"{uncontended.returncode}, log present: {os.path.exists(wait_log)}"
            )
            ok = False

        #    A 10s hold, unlike the arms above: this waiter has to QUEUE and then ACQUIRE, so the
        #    holder must still hold when it starts (or it records no wait) and must let go before
        #    the waiter's own budget expires (or it BLOCKs instead of acquiring). 10s is the margin
        #    against the startup-under-load race that took arm 3 red on 2026-09-06; the waiter's
        #    budget is far wider still.
        holder = _spawn_selftest_child(_CHILD_ACQUIRE_AND_SLEEP, lock_file, hold_s="10")
        if not _wait_for_holder(lock_file, holder.pid, ceiling_s=10.0):
            print("  control FAILED: holder never signaled lock acquisition within 10s (wait-log arm)")
            ok = False
            holder.terminate()
            holder.communicate(timeout=30)
        else:
            env["BATON_BUILDLOCK_TIMEOUT_S"] = "60"
            contended = subprocess.run(
                [sys.executable, os.path.abspath(__file__), sys.executable, "-c", "pass"],
                env=env, capture_output=True, text=True, check=False, timeout=90,
            )
            holder.communicate(timeout=30)
            waits = []
            try:
                with open(wait_log, encoding="utf-8") as f:
                    waits = [int(line) for line in f.read().split()]
            except (OSError, ValueError):
                pass
            if contended.returncode != 0 or not waits or waits[0] < int(POLL_S * 1000):
                print(
                    f"  control FAILED: a contended acquire did not record its wait -- exit "
                    f"{contended.returncode}, log {waits}"
                )
                ok = False
        env.pop(WAIT_LOG_VAR, None)

        # The inherited-log sentinel set at the top: every child spawned above ran with
        # BATON_BUILDLOCK_WAIT_LOG pointing here, and none of their fabricated waits may have
        # reached it. Red before the fix at ~4s across three arms.
        if prior_wait_log is None:
            os.environ.pop(WAIT_LOG_VAR, None)
        else:
            os.environ[WAIT_LOG_VAR] = prior_wait_log
        if os.path.exists(inherited_log):
            with open(inherited_log, encoding="utf-8") as f:
                leaked = f.read().split()
            print(
                f"  control FAILED: this selftest's synthetic contention leaked into an inherited "
                f"{WAIT_LOG_VAR} -- {leaked} ms"
            )
            ok = False

    print("selftest: pass" if ok else "selftest: FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    if "--selftest" in sys.argv[1:2]:
        sys.exit(selftest())
    sys.exit(main())
