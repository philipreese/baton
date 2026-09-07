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

Usage:            python tools/buildlock.py [--replay] [--class build|readonly] <command> [args...]
Replay:           OPT-IN, and the opt-in is the whole safety argument (#2010, 2026-09-07 review). A
                  wrapped command EXECUTES unless its invocation carries `--replay`; only a flagged
                  invocation reads, writes or voids a receipt, so an unflagged one behaves exactly as
                  this wrapper did before #2010 (inherited stdout included). The rule for which call
                  sites may carry the flag lives HERE, and pixi.toml cites it rather than restating
                  it: a command whose whole output is a VERDICT over worktree content may opt in; a
                  command that PRODUCES an artifact another command then reads, or whose verdict is a
                  function of state this file cannot fingerprint, may NOT -- UNLESS that state is
                  itself rebuilt from fingerprinted source by a leg that always executes immediately
                  before it, in the same fixed task line. That exception is not a softening, and it
                  is what makes the two `--no-build` test legs legal rather than something at their
                  call site: `dotnet test --no-build` grades `bin/`, which is outside the
                  fingerprint, so it qualifies ONLY because `test`'s own unflagged
                  `dotnet build --no-incremental` leg and, for `test-no-build`,
                  `lint` inside `gates` force that `bin/` from source on every run. A command that
                  reads out-of-fingerprint state with no such leg in front of it -- `dotnet run
                  --project …`, i.e. `vendor-check` -- may NOT opt in, and reading the clause as
                  soft is exactly how that mistake gets made. The fingerprint is HEAD
                  plus the porcelain status plus every dirty path's content -- `bin/`, `obj/`, the
                  environment and the wall clock are all outside it. So every `dotnet build` line is
                  excluded, `lint`'s and `test`'s especially: their `--no-incremental` is the #687 /
                  #688 protection against MSBuild skipping a project whose source predates its
                  assembly, and a replayed build performs no rebuild at all -- it would delete that
                  protection on the exact commands those two issues named, unconditionally, not just
                  in the branch-excursion case. What the receipt holds and what a reader sees:
                  docs/dispatch.md (#2010).
                  THE ALLOWLIST, and it is pinned rather than merely written: exactly three call
                  sites carry `--replay` -- `test`'s TEST leg, `test-no-build`, and `fmt-check`.
                  `REPLAY_ALLOWLIST` below holds their verbatim segments and a selftest arm reads
                  pixi.toml and asserts the set of `--replay`-carrying segments equals it, so a
                  fourth line, or the flag moved one leg left onto `test`'s `dotnet build`, fails
                  `pixi run buildlock-selftest` naming the offender. Prose alone could not: `test`
                  and `build` are outside diff-shape's PIXI_PROTECTED_TASK_RULE, so that move needs
                  no operator merge.
                  BOUNDARY, stated rather than guarded: the same argv run WITHOUT the flag leaves an
                  existing receipt alone, so a pass can outlive a later unflagged failure of the same
                  command. The pixi lines are fixed and each is flagged or not, so they cannot reach
                  it. The path that IS open, named rather than closed: a `--verify-cmd` (its row in
                  docs/dispatch.md) whose allowlisted `dotnet test …` reproduces an opted-in task's
                  argv verbatim, in the same worktree -- the engine wraps it unflagged, so its
                  failure would leave the earlier pass standing for the flagged caller to replay.
                  Closing it needs an unflagged run to compute the key, which is the tree walk the
                  opt-in exists to avoid paying for.
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
import base64
import hashlib
import json
import os
from pathlib import Path
import re
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
REPLAY_MAX_AGE_S = 6 * 60 * 60
REPLAY_TAIL_BYTES = 64 * 1024
# Receipt format version. It is inside the KEY (so a bump orphans every existing file under a key
# nothing asks for again) and, since the 2026-09-07 review, also inside the BODY -- which is what
# lets prune_receipts recognise an orphan rather than leave it on disk forever.
REPLAY_VERSION = 1

# The `--replay` allowlist, mechanically pinned (#2010, 2026-09-07 re-review). The docstring's
# "Replay" section is the RULE; this is the SET the rule currently admits, as (pixi task, verbatim
# command segment) pairs. `_selftest_replay_allowlist` below asserts pixi.toml's flagged segments
# equal it exactly, which is what stops the two failures prose cannot: a fourth line opting in, and
# the flag moving from `test`'s test leg onto its `dotnet build --no-incremental` leg -- neither
# task name is under diff-shape's PIXI_PROTECTED_TASK_RULE, so neither move needs an operator merge.
REPLAY_ALLOWLIST = {
    ("test",
     "python tools/buildlock.py --replay dotnet test --no-build --minimum-expected-tests 1"),
    ("test-no-build",
     "python tools/buildlock.py --replay dotnet test --no-build "
     "--max-parallel-test-modules 1 --minimum-expected-tests 1"),
    ("fmt-check",
     "python tools/buildlock.py --replay dotnet format --verify-no-changes"),
}


def replay_call_sites(pixi_toml: str) -> set[tuple[str, str]]:
    """Every `--replay`-carrying command segment in a pixi.toml, as (task name, segment).

    Pure over text so the selftest can feed it a fixture and prove the arm discriminates; a
    checker that only ever sees the real file cannot tell "the set matches" from "I found
    nothing". Comment lines are skipped -- pixi.toml discusses the flag more often than it
    carries it -- and a chained `cmd` is split on `&&` so a per-LEG answer is possible at all.
    """
    sites: set[tuple[str, str]] = set()
    current_table = ""
    for raw in pixi_toml.splitlines():
        line = raw.strip()
        table = re.match(r"^\[tasks\.([A-Za-z0-9_-]+)\]", line)
        if table:
            current_table = table.group(1)
            continue
        if line.startswith("#") or "--replay" not in line:
            continue
        key = re.match(r"^([A-Za-z0-9_-]+)\s*=", line)
        name = current_table if (not key or key.group(1) == "cmd") else key.group(1)
        body = re.search(r'cmd\s*=\s*"([^"]*)"', line)
        for segment in (body.group(1) if body else line).split("&&"):
            if "--replay" in segment:
                sites.add((name, " ".join(segment.split())))
    return sites


def replay_inputs(command: list[str], priority_class: str) -> tuple[Path, str] | None:
    """Fail closed on unreadable inputs; NUL porcelain preserves unusual/renamed paths."""
    def git(*args: str) -> bytes:
        return subprocess.run(["git", *args], check=True, stdout=subprocess.PIPE,
                              stderr=subprocess.DEVNULL, timeout=30).stdout

    try:
        root_raw, git_dir_raw, head = git(
            "rev-parse", "--show-toplevel", "--absolute-git-dir", "HEAD").splitlines()
        root = Path(os.fsdecode(root_raw))
        git_dir = Path(os.fsdecode(git_dir_raw))
        identity = json.dumps([REPLAY_VERSION, os.getcwd(), priority_class, command]).encode()
        key = hashlib.sha256(identity).hexdigest()
        status = git("-C", str(root), "status", "--porcelain=v1", "-z",
                     "--untracked-files=all", "--ignore-submodules=none")
        digest = hashlib.sha256(identity + head + hashlib.sha256(status).digest())
        entries = iter(status.split(b"\0")[:-1])
        paths = []
        for entry in entries:
            paths.append(entry[3:])
            if b"R" in entry[:2] or b"C" in entry[:2]:
                paths.append(next(entries))
        for name in sorted(set(paths)):
            path = root / os.fsdecode(name)
            digest.update(name + b"\0")
            if path.is_symlink():
                digest.update(b"link\0" + os.fsencode(os.readlink(path)))
            elif path.is_file():
                with path.open("rb") as f:
                    digest.update(b"file\0" + hashlib.file_digest(f, "sha256").digest())
            elif not path.exists():
                digest.update(b"missing\0")
            else:
                # Dirty submodules/directories need their own tree model; never guess a pass.
                return None
        return git_dir / "buildlock" / (key + ".json"), digest.hexdigest()
    except (OSError, ValueError, StopIteration, subprocess.SubprocessError):
        return None


def replay_pass(inputs: tuple[Path, str] | None, command: list[str]) -> bool:
    if inputs is None:
        return False
    path, fingerprint = inputs
    try:
        receipt = json.loads(path.read_text(encoding="utf-8"))
        if (receipt.get("version") != REPLAY_VERSION
                or receipt["fingerprint"] != fingerprint or receipt["exit_code"] != 0
                or not 0 <= time.time() - receipt["passed_at"] <= REPLAY_MAX_AGE_S):
            return False
        tail = base64.b64decode(receipt["stdout_tail"], validate=True)
        passed = time.strftime("%H:%M:%S", time.localtime(receipt["passed_at"]))
    except (OSError, ValueError, KeyError, TypeError, OverflowError):
        return False
    print(f"buildlock: replaying {subprocess.list2cmdline(command)} "
          f"— unchanged since the pass at {passed}", flush=True)
    sys.stdout.buffer.write(tail)
    sys.stdout.buffer.flush()
    return True


def invalidate_pass(inputs: tuple[Path, str] | None) -> bool:
    if inputs is None:
        return False
    try:
        inputs[0].unlink(missing_ok=True)
        return True
    except OSError:
        return False


def prune_receipts(directory: Path, keep: Path) -> None:
    """Drop this directory's expired and version-orphaned receipts. Bounded, best-effort.

    Runs only on a WRITE, and only over the receipt directory being written to -- one worktree's
    own, holding one file per distinct (cwd, class, argv) that has opted in, at up to
    REPLAY_TAIL_BYTES plus change each. Nothing else ever deletes one: `invalidate_pass` removes
    only the exact key being re-run, and a format bump (REPLAY_VERSION) orphans every existing file
    under a key that is never read again, so without this the main checkout's `.git/buildlock` grows
    for the life of the clone. Never a reason to fail the command that just passed.
    """
    for sibling in directory.glob("*.json"):
        if sibling == keep:
            continue
        try:
            receipt = json.loads(sibling.read_text(encoding="utf-8"))
            live = (receipt.get("version") == REPLAY_VERSION
                    and 0 <= time.time() - receipt["passed_at"] <= REPLAY_MAX_AGE_S)
        except (OSError, ValueError, KeyError, TypeError):
            live = False  # unreadable is unusable: replay_pass would refuse it too
        if not live:
            try:
                sibling.unlink()
            except OSError:
                pass


def run_recorded(command: list[str], env: dict[str, str], priority_class: str,
                 inputs: tuple[Path, str] | None) -> int:
    """Stream stdout unchanged, retaining a bounded byte tail; stderr stays inherited."""
    if inputs is None:
        # Not replay-eligible (no `--replay`, or unreadable inputs): nothing to record, so stdout
        # stays the INHERITED handle it was before #2010 rather than a pipe this process pumps.
        # That is most of the pipe's blast radius removed -- see the remark on the read loop below
        # for the part that necessarily remains.
        return subprocess.run(command, env=env).returncode
    before = replay_inputs(command, priority_class)
    # A previous holder may have published while this process queued.
    if not invalidate_pass(inputs):
        before = None
    tail = bytearray()
    # UNVERIFIED RESIDUAL, and it is on the MSBuild-owning commands specifically (2026-09-07 review):
    # a pipe ends at EOF, which arrives only when every process holding the write handle has closed
    # it, so a grandchild that outlives the wrapped command would stall this loop after the build
    # finished -- in the file whose Timeout section exists to stop exactly that hang class, and this
    # loop has no bound of its own. MSBuild's handle-inheritance behaviour was not measured. The
    # opted-in set is what bounds the exposure: it is `dotnet format --verify-no-changes` and the
    # `--no-build` test legs, which do own MSBuild, so this is narrowed rather than gone.
    # `pixi.toml`'s activation env (MSBUILDDISABLENODEREUSE, UseSharedCompilation=false) is what
    # stops node reuse leaving such a process behind, and it does not apply outside a pixi shell.
    with subprocess.Popen(command, env=env, stdout=subprocess.PIPE) as child:
        assert child.stdout is not None
        while chunk := child.stdout.read1(8192):
            sys.stdout.buffer.write(chunk)
            sys.stdout.buffer.flush()
            tail.extend(chunk)
            del tail[:-REPLAY_TAIL_BYTES]
        code = child.wait()
    after = replay_inputs(command, priority_class) if code == 0 and before else None
    if after is not None and after == before:
        path, fingerprint = after
        temporary = None
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent,
                                             delete=False) as f:
                temporary = f.name
                json.dump({"version": REPLAY_VERSION, "fingerprint": fingerprint, "exit_code": 0,
                           "passed_at": time.time(),
                           "stdout_tail": base64.b64encode(tail).decode("ascii")}, f)
            os.replace(temporary, path)
            prune_receipts(path.parent, path)
        except OSError:
            pass  # Losing an optimization must not turn a passing command into a failure.
        finally:
            if temporary is not None:
                try:
                    os.unlink(temporary)
                except OSError:
                    pass
    return code

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
    argv = sys.argv[1:]
    # OPT-IN (see the docstring's Replay section for the rule this flag encodes). There is
    # deliberately no negative twin: the default IS forced execution, so `--no-replay` would be a
    # second flag on one axis whose only use -- defeating a task line's baked-in `--replay` -- is
    # unreachable, since `pixi run <task>` cannot insert a flag ahead of the command.
    replay = bool(argv and argv[0] == "--replay")
    if replay:
        argv = argv[1:]
    priority_class, command = split_class(argv)
    if command and command[0] == "--replay":
        replay = True
        command = command[1:]
    if priority_class not in CLASSES:
        print(f"buildlock: unknown priority class {priority_class!r} -- one of {', '.join(CLASSES)}")
        return 2
    if not command:
        print("buildlock: no command given -- usage: python tools/buildlock.py "
              "[--replay] [--class build|readonly] <command> [args...]")
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

    # Unflagged: no key computed, no tree walked, no receipt read, written or voided -- which is
    # also what keeps this file's own selftest out of the real repository's receipt store.
    inputs = replay_inputs(command, priority_class) if replay else None
    if replay_pass(inputs, command):
        return 0
    # Delete BEFORE waiting/spawning: a failed or interrupted attempt voids the previous pass.
    if not invalidate_pass(inputs):
        inputs = None
    if priority_class == CLASS_READONLY:
        return run_recorded(command, dict(os.environ), priority_class, inputs)

    env = dict(os.environ)
    if env.get(HELD_MARKER):
        # Marker inherited: probe, don't trust (see the module docstring's Nesting section).
        handle = try_acquire_once(lock_path(), command)
        if handle is None:
            # Lock held -- by our ancestor, per the docstring's stated residual. Run inside
            # its exclusion.
            return run_recorded(command, env, priority_class, inputs)
    else:
        timeout_s = float(env.get("BATON_BUILDLOCK_TIMEOUT_S", "1800"))
        handle = acquire(lock_path(), command, timeout_s)
    env[HELD_MARKER] = str(os.getpid())
    try:
        return run_recorded(command, env, priority_class, inputs)
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
    push's own log: 2015 + 2000 + 2000 ms, ~6s of fabricated queueing against a temp lock file. That
    is the measurement (the sabotage transcript in #1936's PR comment), not the ~4s first estimated
    from the arms' nominal sleeps, and this docstring is where the figure lives. It landed on the
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


def _selftest_replay() -> bool:
    """Real commands in an isolated tracked tree; the counter lives outside the inputs."""
    with tempfile.TemporaryDirectory() as td:
        repo = os.path.join(td, "repo")
        os.mkdir(repo)
        env = _selftest_env(BATON_BUILDLOCK_FILE=os.path.join(td, "replay.lock"))
        for name in list(env):
            if name.startswith("GIT_"):
                env.pop(name)

        def git(*args: str) -> None:
            subprocess.run(["git", *args], cwd=repo, env=env, check=True,
                           capture_output=True, timeout=30)

        tracked = os.path.join(repo, "tracked file.txt")
        with open(tracked, "w", encoding="utf-8") as f:
            f.write("initial")
        git("init", "-q")
        git("add", ".")
        git("-c", "user.name=Selftest", "-c", "user.email=selftest@example.invalid",
            "-c", "core.hooksPath=/dev/null", "commit", "-qm", "fixture")
        counter = os.path.join(td, "counter")
        failure = os.path.join(td, "fail")
        command = [sys.executable, "-c",
                   "import os,sys; open(sys.argv[1],'a').write('run\\n'); "
                   "print('receipt output'); sys.exit(3 if os.path.exists(sys.argv[2]) else 0)",
                   counter, failure]

        def run(*flags: str) -> subprocess.CompletedProcess:
            """An OPTED-IN invocation -- what a pixi task line that carries `--replay` looks like."""
            return subprocess.run(
                [sys.executable, os.path.abspath(__file__), "--replay", *flags, *command],
                cwd=repo, env=env, capture_output=True, text=True, check=False, timeout=180)

        def run_unflagged() -> subprocess.CompletedProcess:
            """The DEFAULT invocation: no `--replay`, so no receipt is read, written or voided."""
            return subprocess.run([sys.executable, os.path.abspath(__file__), *command],
                                  cwd=repo, env=env, capture_output=True, text=True,
                                  check=False, timeout=180)

        def count() -> int:
            with open(counter, encoding="utf-8") as f:
                return len(f.readlines())

        first, second = run(), run()
        if (first.returncode != 0 or second.returncode != 0 or count() != 1
                or "buildlock: replaying" not in second.stdout
                or "receipt output" not in second.stdout):
            print(f"  control FAILED: unchanged pass did not replay -- {second.stdout!r}")
            return False
        for content in ("edited", "edited again"):
            before = count()
            with open(tracked, "w", encoding="utf-8") as f:
                f.write(content)
            result = run()
            if result.returncode != 0 or count() != before + 1:
                print("  control FAILED: tracked content edit replayed")
                return False
        # THE OPT-IN (2026-09-07 review). A live receipt is sitting next to an unchanged tree right
        # now -- the arm above just proved a flagged run replays in exactly this state -- and an
        # UNFLAGGED command must still execute, twice running. Both halves discriminate: "executes
        # once" would also pass under an inversion that merely stopped WRITING receipts, and the
        # absence of the replay line is what separates executing from replaying silently.
        before = count()
        unflagged_first, unflagged_second = run_unflagged(), run_unflagged()
        if (unflagged_first.returncode != 0 or unflagged_second.returncode != 0
                or count() != before + 2
                or "replaying" in unflagged_first.stdout + unflagged_second.stdout):
            print(f"  control FAILED: an unflagged command did not execute twice -- "
                  f"{count() - before} run(s), {unflagged_second.stdout!r}")
            return False
        # ... and left that receipt untouched: the docstring's stated BOUNDARY, pinned so a future
        # change that starts voiding receipts from unflagged runs updates the claim with the code.
        before = count()
        still_valid = run()
        if still_valid.returncode != 0 or count() != before or "replaying" not in still_valid.stdout:
            print("  control FAILED: an unflagged run consumed the receipt")
            return False
        # ... and WROTE none either -- the third verb, and the one the two arms above cannot reach.
        # The key is (cwd, class, argv), identical for the flagged and unflagged forms, so a receipt
        # written by an unflagged run would land on the same path with the same fingerprint and every
        # later arm would read identically. Emptying the store first is what makes the write visible.
        # Load-bearing beyond the docstring: gates.py's OVERLAP justification for running this
        # selftest beside lint's build rests on unflagged runs never touching the real
        # `.git/buildlock`, and arms 2-7 use the REAL repository as cwd.
        store = Path(repo, ".git", "buildlock")
        for existing in store.glob("*.json"):
            existing.unlink()
        before = count()
        wrote_nothing = run_unflagged()
        left_behind = sorted(p.name for p in store.glob("*.json"))
        if wrote_nothing.returncode != 0 or count() != before + 1 or left_behind:
            print(f"  control FAILED: an unflagged run wrote a receipt -- {left_behind}")
            return False
        with open(failure, "w", encoding="utf-8") as f:
            f.write("fail")
        with open(tracked, "w", encoding="utf-8") as f:
            f.write("edited before the failure")  # moves off the receipted fingerprint
        failed = run()
        before = count()
        failed_again = run()
        if failed.returncode != 3 or failed_again.returncode != 3 or count() != before + 1:
            print("  control FAILED: last failure was replayed")
            return False
        os.remove(failure)
        if run().returncode != 0 or count() != before + 2:
            print("  control FAILED: execution did not recover after failure")
            return False
        # A replay must not even OPEN a lock file: this parent directory does not exist.
        before = count()
        env["BATON_BUILDLOCK_FILE"] = os.path.join(td, "missing", "cannot-open.lock")
        unlocked = run()
        env["BATON_BUILDLOCK_FILE"] = os.path.join(td, "replay.lock")
        if unlocked.returncode != 0 or count() != before or "replaying" not in unlocked.stdout:
            print("  control FAILED: replay touched the lock")
            return False
        # Discover the fixture's receipt without changing this process's working directory.
        receipts = list(Path(repo, ".git", "buildlock").glob("*.json"))
        if len(receipts) != 1:
            print(f"  control FAILED: expected one fixture receipt, got {len(receipts)}")
            return False
        receipt_path = receipts[0]
        receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
        receipt["passed_at"] = time.time() - REPLAY_MAX_AGE_S - 1
        receipt_path.write_text(json.dumps(receipt), encoding="utf-8")
        if run().returncode != 0 or count() != before + 1:
            print("  control FAILED: expired receipt replayed")
            return False
        receipt_path.write_text("broken json", encoding="utf-8")
        if run().returncode != 0 or count() != before + 2:
            print("  control FAILED: corrupt receipt did not execute")
            return False
        # Pruning on write, both polarities: an EXPIRED sibling and a VERSION-ORPHANED one go, the
        # live receipt this write just produced stays. Without the negative half a prune that
        # emptied the directory would look identical from here.
        store = receipt_path.parent
        stale = {
            store / "expired.json": {"version": REPLAY_VERSION, "fingerprint": "x", "exit_code": 0,
                                     "passed_at": time.time() - REPLAY_MAX_AGE_S - 1,
                                     "stdout_tail": ""},
            store / "orphan.json": {"version": REPLAY_VERSION + 1, "fingerprint": "x",
                                    "exit_code": 0, "passed_at": time.time(), "stdout_tail": ""},
        }
        for planted, body in stale.items():
            planted.write_text(json.dumps(body), encoding="utf-8")
        with open(tracked, "w", encoding="utf-8") as f:
            f.write("edited for the prune arm")
        if run().returncode != 0:
            print("  control FAILED: the prune arm's own run did not pass")
            return False
        survivors = sorted(p.name for p in store.glob("*.json"))
        if survivors != [receipt_path.name]:
            print(f"  control FAILED: prune left {survivors}, want just the live receipt")
            return False
    print("  replay controls: pass")
    return True


def _selftest_replay_allowlist() -> bool:
    """pixi.toml's `--replay` set is exactly REPLAY_ALLOWLIST -- membership, not just the default.

    The rest of this file's replay arms pin what the flag DOES. This one pins WHO CARRIES IT, which
    was prose plus a partial line-level protection: `test` and `build` are outside diff-shape's
    PIXI_PROTECTED_TASK_RULE, so moving the flag from `test`'s `--no-build` leg onto its
    `dotnet build --no-incremental` leg trips no gate and silently deletes #687/#688's protection.
    Exact-segment equality catches that move (the segment text changes) as well as a fourth opt-in.
    """
    pixi = Path(__file__).resolve().parent.parent / "pixi.toml"
    try:
        text = pixi.read_text(encoding="utf-8")
    except OSError as exc:
        print(f"  control FAILED: could not read {pixi} -- {exc}")
        return False
    found = replay_call_sites(text)
    if found != REPLAY_ALLOWLIST:
        for extra in sorted(found - REPLAY_ALLOWLIST):
            print(f"  control FAILED: unallowlisted --replay call site {extra[0]}: {extra[1]!r}")
        for missing in sorted(REPLAY_ALLOWLIST - found):
            print(f"  control FAILED: allowlisted --replay call site gone {missing[0]}: "
                  f"{missing[1]!r}")
        print("  (the allowlist and the rule behind it: REPLAY_ALLOWLIST and this module's "
              "docstring, 'Replay')")
        return False
    # The discriminating control: on a fixture carrying a fourth opt-in and the flag moved onto a
    # build leg, the same reader must report both. Without it, a parser that silently matched
    # nothing would pass the equality above only by accident of an empty allowlist -- and would
    # keep passing after someone did opt a build line in.
    fixture = (
        '[tasks]\n'
        '# discussion of --replay in a comment must not count\n'
        'lint = { cmd = "python tools/buildlock.py --replay dotnet build -warnaserror" }\n'
        'test = { cmd = "python tools/buildlock.py dotnet build --no-incremental && '
        'python tools/buildlock.py --replay dotnet test --no-build --minimum-expected-tests 1" }\n'
    )
    intruders = replay_call_sites(fixture)
    expected_intruders = {
        ("lint", "python tools/buildlock.py --replay dotnet build -warnaserror"),
        ("test",
         "python tools/buildlock.py --replay dotnet test --no-build --minimum-expected-tests 1"),
    }
    if intruders != expected_intruders:
        print(f"  control FAILED: the allowlist reader missed a planted opt-in -- {intruders}")
        return False
    # The docstring names the same three in prose, which is a second copy of the set and the one a
    # reader meets first. Pinned too, cheaply: a legitimate future change to REPLAY_ALLOWLIST must
    # update that sentence or fail here, rather than leaving it stale and green.
    unnamed = sorted(task for task, _ in REPLAY_ALLOWLIST if f"`{task}`" not in (__doc__ or ""))
    if unnamed:
        print(f"  control FAILED: allowlisted task(s) unnamed in the module docstring -- {unnamed}")
        return False
    print("  replay allowlist: pass")
    return True


def selftest() -> int:
    ok = _selftest_replay()
    ok = _selftest_replay_allowlist() and ok
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
        # reached it -- red before the fix, with every one of those waits in this file (how much,
        # measured, is stated once in _selftest_env's docstring).
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
