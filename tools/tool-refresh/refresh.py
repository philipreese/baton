"""Side-by-side per-commit refresh of the `baton` tool (#1668).

Replaces the single-global-tool drain cycle (#1645) with isolated side-by-side installs:
- Pack `baton` from this checkout.
- Install into `~/.baton/tools/<short-sha>` via `dotnet tool install baton --tool-path ... --add-source bin/pack`.
- Run sanity invocations against the newly installed executable.
- Atomically flip `~/.baton/tools/current` pointer file (temp file + rename).
- Ensure launcher scripts (`baton.cmd`, `baton.ps1`, `baton`) are installed in `~/.dotnet/tools` on PATH,
- Restart the `baton-daemon` scheduled task so a re-refresh cycles the daemon onto the launcher's
  `current` pointer.
- Prune unreferenced versions older than the top 3 installs.
- No drain wait; no `draining.json` write. Keep `draining.json` honoured by dispatch as an operator-invoked stop only.

Usage:      pixi run tool-refresh [--dry-run] | pixi run tool-refresh --abort
Selftest:   pixi run tool-refresh-selftest   (python tools/tool-refresh/refresh.py --selftest)
"""
import argparse
import datetime as dt
import json
import os
import re
import shutil
import subprocess
import sys
import time
import uuid
from dataclasses import dataclass, field
from enum import Enum
from typing import Callable, List, Optional, Sequence, Set

VERSION_ELEMENT = re.compile(r"<Version>\s*(?P<version>\S+?)\s*</Version>")
VERSION_PROPS_RELATIVE_PATH = os.path.join("src", "Baton.Cli", "Directory.Build.props")

DRAIN_MARKER_FILENAME = "draining.json"
DRAIN_MARKER_REASON = "tool-refresh"
ABORT_INVOCATION = "pixi run tool-refresh --abort"

BATON_PATHS_RELATIVE_PATH = os.path.join("src", "Baton", "Status", "BatonPaths.cs")
DRAIN_MARKER_CONST = re.compile(r"DrainMarkerFileName\s*=\s*\"(?P<name>[^\"]+)\"")
DRAIN_MARKER_TYPE_RELATIVE_PATH = os.path.join("src", "Baton", "Status", "DrainMarker.cs")
ABORT_INVOCATION_CONST = re.compile(r"AbortInvocation\s*=\s*\"(?P<invocation>[^\"]+)\"")


@dataclass
class CommandResult:
    returncode: int
    stdout: str = ""
    stderr: str = ""


@dataclass(frozen=True)
class DaemonIdentity:
    pid: int
    creation_time: str
    executable_path: str
    version: str


@dataclass(frozen=True)
class DaemonProcess:
    pid: int
    creation_time: str
    executable_path: str


class DaemonTaskState(Enum):
    QUERY_FAILED = "query-failed"
    ABSENT = "absent"
    DISABLED = "disabled"
    ACTIVE = "active"


class DaemonExitState(Enum):
    EXITED = "exited"
    TIMEOUT = "timeout"
    QUERY_FAILED = "query-failed"
    IDENTITY_CHANGED = "identity-changed"


class DaemonVerificationState(Enum):
    ACCEPTED = "accepted"
    NO_CANDIDATE = "no-candidate"
    QUERY_FAILED = "query-failed"
    DUPLICATE = "duplicate"
    WRONG_PATH = "wrong-path"
    VERSION_MISMATCH = "version-mismatch"
    IDENTITY_UNSTABLE = "identity-unstable"
    UNHEALTHY = "unhealthy"


@dataclass(frozen=True)
class DaemonVerification:
    state: DaemonVerificationState
    identity: Optional[DaemonIdentity] = None


Runner = Callable[[List[str]], CommandResult]


def real_runner(cmd: List[str]) -> CommandResult:
    proc = subprocess.run(cmd, capture_output=True, text=True, check=False)
    return CommandResult(proc.returncode, proc.stdout, proc.stderr)


@dataclass
class Deps:
    """Everything the refresh needs from the outside world, injectable so --selftest never spawns a
    real `dotnet`/`baton`/`pixi` or touches this machine's real NuGet cache, ~/.baton or ~/.dotnet/tools."""

    run: Runner = real_runner
    repo_root: str = ""
    baton_home: str = ""
    rooms_root: str = ""
    tools_root: str = ""
    dotnet_tools_root: str = ""
    nuget_packages_root: str = ""
    sleep: Callable[[float], None] = time.sleep
    monotonic: Callable[[], float] = time.monotonic
    clock: Callable[[], float] = time.time
    out: "Sequence[str]" = field(default_factory=list)

    def __post_init__(self) -> None:
        if not self.repo_root:
            self.repo_root = default_repo_root()
        if not self.baton_home:
            self.baton_home = default_baton_home()
        if not self.rooms_root:
            self.rooms_root = os.path.join(self.baton_home, "rooms")
        if not self.tools_root:
            self.tools_root = os.path.join(self.baton_home, "tools")
        if not self.dotnet_tools_root:
            self.dotnet_tools_root = os.path.join(os.path.expanduser("~"), ".dotnet", "tools")
        if not self.nuget_packages_root:
            self.nuget_packages_root = os.environ.get(
                "NUGET_PACKAGES", os.path.join(os.path.expanduser("~"), ".nuget", "packages"))


def default_repo_root() -> str:
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def default_baton_home() -> str:
    override = os.environ.get("BATON_HOME", "").strip()
    return override if override else os.path.join(os.path.expanduser("~"), ".baton")


def read_repo_version(repo_root: str) -> Optional[str]:
    """The version `baton --version` will report once packed from this checkout."""
    props_path = os.path.join(repo_root, VERSION_PROPS_RELATIVE_PATH)
    try:
        with open(props_path, "r", encoding="utf-8") as f:
            text = f.read()
    except OSError:
        return None
    match = VERSION_ELEMENT.search(text)
    return match.group("version") if match else None


def read_repo_commit_sha(deps: Deps) -> Optional[str]:
    """Resolves the current git commit short SHA for this checkout."""
    result = deps.run(["git", "-C", deps.repo_root, "rev-parse", "--short", "HEAD"])
    if result.returncode == 0 and result.stdout.strip():
        return result.stdout.strip()
    # Fallback to full rev-parse truncated
    res_full = deps.run(["git", "-C", deps.repo_root, "rev-parse", "HEAD"])
    if res_full.returncode == 0 and res_full.stdout.strip():
        return res_full.stdout.strip()[:8]
    return None


def drain_marker_path(deps: Deps) -> str:
    return os.path.join(deps.baton_home, DRAIN_MARKER_FILENAME)


def remove_drain_marker(deps: Deps, dry_run: bool, print_fn: Callable[[str], None]) -> bool:
    """Removes a drain marker if present. Used by `--abort`."""
    path = drain_marker_path(deps)
    if dry_run:
        print_fn(f"tool-refresh: [dry-run] would remove drain marker: {path}")
        return False

    try:
        os.remove(path)
    except FileNotFoundError:
        return False
    except OSError as exc:
        print_fn(f"tool-refresh: could not remove the drain marker {path} ({exc})")
        return False
    print_fn(f"tool-refresh: drain marker removed: {path}")
    return True


def abort(deps: Deps, print_fn: Callable[[str], None], dry_run: bool = False) -> int:
    """`--abort`: clear an operator-written drain marker and do nothing else."""
    if dry_run:
        remove_drain_marker(deps, dry_run=True, print_fn=print_fn)
        return 0

    if remove_drain_marker(deps, dry_run=False, print_fn=print_fn):
        return 0
    print_fn(f"tool-refresh: no drain marker at {drain_marker_path(deps)} -- nothing to abort")
    return 0


def dotnet_tool_installed(deps: Deps) -> bool:
    result = deps.run(["dotnet", "tool", "list", "--global"])
    if result.returncode != 0:
        return False
    return any(line.split() and line.split()[0] == "baton" for line in result.stdout.splitlines())


def run_step(deps: Deps, cmd: List[str], dry_run: bool, print_fn: Callable[[str], None]) -> CommandResult:
    printable = " ".join(cmd)
    if dry_run:
        print_fn(f"tool-refresh: [dry-run] would run: {printable}")
        return CommandResult(0)
    print_fn(f"tool-refresh: running: {printable}")
    return deps.run(cmd)


def purge_nuget_cache(deps: Deps, version: str, dry_run: bool, print_fn: Callable[[str], None]) -> None:
    cache_dir = os.path.join(deps.nuget_packages_root, "baton", version)
    if dry_run:
        print_fn(f"tool-refresh: [dry-run] would purge NuGet cache: {cache_dir}")
        return
    if os.path.isdir(cache_dir):
        try:
            shutil.rmtree(cache_dir)
            print_fn(f"tool-refresh: purged NuGet cache {cache_dir}")
        except OSError as exc:
            print_fn(f"tool-refresh: warning: could not purge NuGet cache {cache_dir}: {exc}")
    else:
        print_fn(f"tool-refresh: NuGet cache {cache_dir} already absent, nothing to purge")


def read_current_pointer(tools_root: str) -> Optional[str]:
    current_file = os.path.join(tools_root, "current")
    if not os.path.isfile(current_file):
        return None
    try:
        with open(current_file, "r", encoding="utf-8") as f:
            return f.read().strip() or None
    except OSError:
        return None


def verify_target_exe(deps: Deps, tool_dir: str, version: str) -> bool:
    """Returns True iff tool_dir's binary reports `version` via --version and passes the
    `templates --json` smoke check. Shared by the fresh-install verify gate and by the idempotent
    re-refresh check (F4, #1670 review) that decides whether an already-installed SHA can be reused
    as-is."""
    target_exe = os.path.join(tool_dir, "baton.exe" if (sys.platform == "win32" or os.name == "nt") else "baton")
    if not os.path.isfile(target_exe):
        return False
    version_result = deps.run([target_exe, "--version"])
    if version_result.returncode != 0 or version_result.stdout.strip() != version:
        return False
    smoke_result = deps.run([target_exe, "templates", "--json"])
    return smoke_result.returncode == 0


# #1701: a just-written destination file can carry a transient handle (Defender/indexer scanning
# it, more likely under machine load) that makes Windows' ReplaceFile -- what os.replace calls --
# fail with ERROR_ACCESS_DENIED even though nothing else in this process holds it open. Same class
# as the well-known shutil/os.replace PermissionError on Windows. Retried, not failed closed
# immediately, because the race is transient and a real `pixi run tool-refresh` flip can hit it.
POINTER_REPLACE_RETRIES = 10
POINTER_REPLACE_BACKOFF_S = 0.2


def write_current_pointer(
    tools_root: str, sha: str, dry_run: bool, print_fn: Callable[[str], None],
    replace: Callable[[str, str], None] = os.replace, sleep: Callable[[float], None] = time.sleep,
) -> bool:
    """Atomically updates ~/.baton/tools/current to point at sha.

    `replace`/`sleep` are injectable so a selftest arm can inject a transient PermissionError on
    the first replace attempt without waiting out a real backoff or faking the filesystem."""
    current_path = os.path.join(tools_root, "current")
    if dry_run:
        print_fn(f"tool-refresh: [dry-run] would atomically write current pointer '{sha}' to {current_path}")
        return True

    os.makedirs(tools_root, exist_ok=True)
    tmp_path = os.path.join(tools_root, f"current.tmp.{uuid.uuid4().hex}")
    try:
        with open(tmp_path, "w", encoding="utf-8") as f:
            f.write(f"{sha}\n")
        last_exc: Optional[OSError] = None
        for attempt in range(POINTER_REPLACE_RETRIES):
            try:
                replace(tmp_path, current_path)
                if attempt > 0:
                    print_fn(f"tool-refresh: flipped current pointer to {sha} (retry {attempt} after a transient replace failure)")
                else:
                    print_fn(f"tool-refresh: flipped current pointer to {sha}")
                return True
            except PermissionError as exc:
                last_exc = exc
                if attempt < POINTER_REPLACE_RETRIES - 1:
                    sleep(POINTER_REPLACE_BACKOFF_S)
        print_fn(f"tool-refresh: could not write current pointer to {current_path}: {last_exc}")
        return False
    except OSError as exc:
        print_fn(f"tool-refresh: could not write current pointer to {current_path}: {exc}")
        return False
    finally:
        if os.path.exists(tmp_path):
            try:
                os.remove(tmp_path)
            except OSError:
                pass


def sweep_stale_launcher_backups(deps: Deps, dry_run: bool, print_fn: Callable[[str], None]) -> None:
    """F6 (#1670 review): install_launcher's rename fallback leaves baton.exe.old.<guid> files behind
    on every failed-uninstall/failed-delete event; nothing else in this tool ever cleaned them up.
    Sweeps them on each refresh, skipping any still locked by a live process."""
    if dry_run or not os.path.isdir(deps.dotnet_tools_root):
        return
    for name in os.listdir(deps.dotnet_tools_root):
        if not name.startswith("baton.exe.old."):
            continue
        path = os.path.join(deps.dotnet_tools_root, name)
        try:
            os.remove(path)
            print_fn(f"tool-refresh: swept stale launcher backup {path}")
        except OSError:
            pass  # still locked -- leave it for a later refresh


def install_launcher(deps: Deps, dry_run: bool, print_fn: Callable[[str], None]) -> bool:
    """Installs the baton launcher scripts into ~/.dotnet/tools, uninstalling any legacy global tool.
    Returns False (refresh must fail closed, F5) if a stale baton.exe is still present afterward --
    PATHEXT resolves .exe before .cmd/.ps1, so a leftover global-tool shim would silently shadow the
    launcher on every bare `baton` invocation."""
    if dry_run:
        print_fn("tool-refresh: [dry-run] would uninstall legacy global baton tool if present")
    elif dotnet_tool_installed(deps):
        uninstall_res = run_step(deps, ["dotnet", "tool", "uninstall", "--global", "baton"], dry_run, print_fn)
        if uninstall_res.returncode != 0:
            print_fn(f"tool-refresh: warning: failed to uninstall global baton tool (exit {uninstall_res.returncode}): {uninstall_res.stderr.strip()}")
            legacy_exe = os.path.join(deps.dotnet_tools_root, "baton.exe")
            if os.path.isfile(legacy_exe):
                try:
                    os.remove(legacy_exe)
                    print_fn("tool-refresh: removed legacy baton.exe shim from ~/.dotnet/tools")
                except OSError:
                    try:
                        old_exe = os.path.join(deps.dotnet_tools_root, f"baton.exe.old.{uuid.uuid4().hex}")
                        os.rename(legacy_exe, old_exe)
                        print_fn("tool-refresh: renamed legacy baton.exe shim to allow launcher scripts to resolve")
                    except OSError as exc2:
                        print_fn(f"tool-refresh: warning: could not remove legacy baton.exe: {exc2}")
        else:
            print_fn("tool-refresh: uninstalled legacy global baton tool to allow launcher scripts to resolve on PATH")

    if not dry_run:
        legacy_exe = os.path.join(deps.dotnet_tools_root, "baton.exe")
        if os.path.isfile(legacy_exe):
            print_fn(
                f"tool-refresh: baton.exe is still present at {legacy_exe} after uninstall/remove/rename -- "
                "PATHEXT resolves .exe before .cmd/.ps1, so a bare `baton` would keep silently running the "
                "stale global tool instead of the launcher. Refusing to declare the refresh done; close "
                "whatever process holds it and re-run."
            )
            return False

    sweep_stale_launcher_backups(deps, dry_run, print_fn)

    launcher_dir = os.path.join(deps.repo_root, "tools", "tool-refresh", "launcher")
    if not dry_run:
        os.makedirs(deps.dotnet_tools_root, exist_ok=True)

    for name in ["baton.cmd", "baton.ps1", "baton"]:
        src = os.path.join(launcher_dir, name)
        dst = os.path.join(deps.dotnet_tools_root, name)
        if dry_run:
            print_fn(f"tool-refresh: [dry-run] would copy {src} to {dst}")
            continue
        if os.path.isfile(src):
            shutil.copy2(src, dst)

    if not dry_run:
        print_fn(f"tool-refresh: launcher scripts installed in {deps.dotnet_tools_root}")
    return True


def daemon_process_query_cmd() -> List[str]:
    """The CIM query used to find live baton.exe daemon processes for identity and replacement
    checks, so those checks cannot drift about what counts as 'the daemon process'."""
    return [
        "powershell", "-NoProfile", "-Command",
        "Get-CimInstance -ClassName Win32_Process -Filter \"Name='baton.exe'\" | "
        # Anchored on the verb position (first arg after the executable) rather than a bare
        # '*daemon*' substring match, which would also catch e.g. `baton dispatch --spec-text
        # "... daemon ..."` and kill an unrelated live process on every refresh (#1777 fix round F2).
        "Where-Object { $_.CommandLine -match '^(\"[^\"]+\"|\\S+)\\s+daemon(\\s|$)' } | "
        "ForEach-Object { \"{0}|{1}|{2}\" -f $_.ProcessId, $_.CreationDate.ToUniversalTime().ToString('o', [System.Globalization.CultureInfo]::InvariantCulture), $_.ExecutablePath }",
    ]


def query_daemon_processes(deps: Deps) -> Optional[List[DaemonProcess]]:
    """Returns daemon processes, or None when the process query itself failed."""
    result = deps.run(daemon_process_query_cmd())
    if result.returncode != 0:
        return None
    processes: List[DaemonProcess] = []
    for line in result.stdout.splitlines():
        line = line.strip()
        if "|" not in line:
            continue
        fields = [field.strip() for field in line.split("|")]
        if len(fields) == 3:
            pid_str, creation_time, path = fields
        else:
            continue
        if not pid_str.isdigit():
            continue
        processes.append(DaemonProcess(int(pid_str), creation_time, path))
    return processes


def find_daemon_processes(deps: Deps) -> List[DaemonProcess]:
    """Returns a best-effort daemon listing for diagnostics and compatibility callers."""
    return query_daemon_processes(deps) or []


def daemon_version(deps: Deps, executable_path: str) -> Optional[str]:
    result = deps.run([executable_path, "--version"])
    if result.returncode != 0:
        return None
    version = result.stdout.strip()
    return version or None


def _same_daemon_process(left: DaemonProcess, right: DaemonProcess) -> bool:
    return (
        left.pid == right.pid
        and left.creation_time == right.creation_time
        and os.path.normcase(os.path.normpath(left.executable_path))
        == os.path.normcase(os.path.normpath(right.executable_path))
    )


def capture_daemon_identity(
    deps: Deps, process: DaemonProcess, expected_version: Optional[str] = None
) -> Optional[DaemonIdentity]:
    """Read and immediately revalidate one process as a single stable identity."""
    if not process.creation_time:
        return None
    first_version = daemon_version(deps, process.executable_path)
    if first_version is None or (expected_version and first_version != expected_version):
        return None
    rechecked = query_daemon_processes(deps)
    if rechecked is None or len(rechecked) != 1 or not _same_daemon_process(process, rechecked[0]):
        return None
    second_version = daemon_version(deps, rechecked[0].executable_path)
    if second_version is None or second_version != first_version:
        return None
    return DaemonIdentity(
        rechecked[0].pid,
        rechecked[0].creation_time,
        rechecked[0].executable_path,
        second_version,
    )


def capture_daemon_identities(deps: Deps) -> Optional[List[DaemonIdentity]]:
    """Captures stable PID, creation time, executable, and version before a scheduler stop."""
    processes = query_daemon_processes(deps)
    if processes is None:
        return None
    identities: List[DaemonIdentity] = []
    for process in processes:
        identity = capture_daemon_identity(deps, process)
        if identity is None:
            return None
        identities.append(identity)
    return identities


DAEMON_OLD_EXIT_RETRIES = 15
DAEMON_OLD_EXIT_BACKOFF_S = 1.0
DAEMON_START_ATTEMPTS = 2


def wait_for_daemon_exit(
    deps: Deps, old_identity: DaemonIdentity, print_fn: Callable[[str], None]
) -> DaemonExitState:
    """Proves that the exact pre-restart identity disappeared without force-killing it."""
    for attempt in range(DAEMON_OLD_EXIT_RETRIES):
        processes = query_daemon_processes(deps)
        if processes is None:
            print_fn("tool-refresh: could not query daemon identity while waiting for the old daemon to exit")
            return DaemonExitState.QUERY_FAILED
        old_identity_present = any(
            process.pid == old_identity.pid
            and process.creation_time == old_identity.creation_time
            and os.path.normcase(os.path.normpath(process.executable_path))
            == os.path.normcase(os.path.normpath(old_identity.executable_path))
            for process in processes
        )
        if old_identity_present:
            current_version = daemon_version(deps, old_identity.executable_path)
            if current_version is None:
                print_fn("tool-refresh: could not revalidate the old daemon version while waiting for exit")
                return DaemonExitState.QUERY_FAILED
            if current_version != old_identity.version:
                print_fn("tool-refresh: the old daemon identity changed while waiting for exit")
                return DaemonExitState.IDENTITY_CHANGED
        if not old_identity_present:
            waited = attempt * DAEMON_OLD_EXIT_BACKOFF_S
            print_fn(f"tool-refresh: old daemon pid={old_identity.pid} exited after ~{waited:.0f}s")
            return DaemonExitState.EXITED
        if attempt < DAEMON_OLD_EXIT_RETRIES - 1:
            deps.sleep(DAEMON_OLD_EXIT_BACKOFF_S)
    return DaemonExitState.TIMEOUT


def _parse_datetime(value: object) -> Optional[dt.datetime]:
    if not isinstance(value, str) or not value.strip():
        return None
    text = value.strip()
    dmtf = re.fullmatch(r"(\d{14})\.(\d{6})([+-])(\d{3})", text)
    if dmtf:
        try:
            base = dt.datetime.strptime(dmtf.group(1), "%Y%m%d%H%M%S")
            offset_minutes = int(dmtf.group(4))
            if offset_minutes > 14 * 60:
                return None
            if dmtf.group(3) == "-":
                offset_minutes = -offset_minutes
            parsed = base.replace(
                microsecond=int(dmtf.group(2)),
                tzinfo=dt.timezone(dt.timedelta(minutes=offset_minutes)),
            )
        except (ValueError, OverflowError):
            return None
    else:
        try:
            parsed = dt.datetime.fromisoformat(text.replace("Z", "+00:00"))
        except (ValueError, OverflowError):
            return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=dt.timezone.utc)
    try:
        return parsed.astimezone(dt.timezone.utc)
    except (ValueError, OverflowError):
        return None


def _parse_timestamp(value: object) -> Optional[float]:
    parsed = _parse_datetime(value)
    if parsed is None:
        return None
    try:
        return parsed.timestamp()
    except (OSError, ValueError, OverflowError):
        return None


def _canonical_timestamp(value: object) -> Optional[str]:
    """Returns the microsecond UTC spelling shared by CIM and heartbeat process start times."""
    instant = _parse_datetime(value)
    if instant is None:
        return None
    return instant.strftime("%Y-%m-%dT%H:%M:%S.") + f"{instant.microsecond:06d}Z"


DAEMON_HEARTBEAT_MAX_AGE_S = 120.0


def daemon_health_probe(deps: Deps, identity: DaemonIdentity) -> bool:
    """Reads a fresh heartbeat that proves the exact candidate daemon is turning over."""
    heartbeat_path = os.path.join(deps.baton_home, "fleet", "heartbeat.json")
    try:
        with open(heartbeat_path, "r", encoding="utf-8") as f:
            heartbeat = json.load(f)
        started_at = _parse_timestamp(heartbeat.get("startedAt"))
        tick_completed_at = _parse_timestamp(heartbeat.get("tickCompletedAt"))
        process_started_at = _parse_timestamp(identity.creation_time)
        heartbeat_identity = heartbeat.get("identity")
        if started_at is None or tick_completed_at is None or process_started_at is None:
            return False
        if not isinstance(heartbeat_identity, dict):
            return False
        if heartbeat_identity.get("pid") != identity.pid:
            return False
        if _canonical_timestamp(heartbeat_identity.get("processStartTime")) != _canonical_timestamp(
            identity.creation_time
        ):
            return False
        heartbeat_path = heartbeat_identity.get("executablePath")
        heartbeat_version = heartbeat_identity.get("version")
        if not isinstance(heartbeat_path, str) or not heartbeat_path.strip():
            return False
        if not isinstance(heartbeat_version, str) or not heartbeat_version.strip():
            return False
        if (
            os.path.normcase(os.path.normpath(heartbeat_path))
            != os.path.normcase(os.path.normpath(identity.executable_path))
            or heartbeat_version != identity.version
        ):
            return False
        if started_at > tick_completed_at:
            return False
        if started_at + 5.0 < process_started_at:
            return False
        now = deps.clock()
        return tick_completed_at <= now + 5.0 and now - tick_completed_at <= DAEMON_HEARTBEAT_MAX_AGE_S
    except (OSError, ValueError, TypeError, AttributeError):
        return False


def path_is_under(path: str, directory: str) -> bool:
    path_norm = os.path.normcase(os.path.normpath(path))
    directory_norm = os.path.normcase(os.path.normpath(directory))
    return path_norm.startswith(directory_norm + os.sep)


TASK_NOT_FOUND_MARKER = "BATON_TASK_NOT_FOUND"
TASK_ERROR_MARKER = "BATON_TASK_ERROR"
TASK_STATE_PREFIX = "BATON_TASK_STATE="
TASK_STATES = frozenset({"disabled", "ready", "running", "queued"})


def daemon_task_query_cmd() -> List[str]:
    """Uses a strict PowerShell protocol so query errors cannot look like task absence."""
    script = (
        "$ErrorActionPreference = 'Stop'; "
        "try { "
        "$tasks = @(Get-ScheduledTask -TaskName 'baton-daemon' -ErrorAction Stop); "
        "if ($tasks.Count -eq 0) { [Console]::WriteLine('BATON_TASK_NOT_FOUND'); exit 0 }; "
        "if ($tasks.Count -ne 1) { [Console]::WriteLine('BATON_TASK_ERROR'); exit 2 }; "
        "$state = [string]$tasks[0].State; "
        "if ([string]::IsNullOrWhiteSpace($state)) { [Console]::WriteLine('BATON_TASK_ERROR'); exit 2 }; "
        "[Console]::WriteLine('BATON_TASK_STATE=' + $state); exit 0 "
        "} catch { "
        "if ($_.CategoryInfo.Category -eq 'ObjectNotFound' -and "
        "($_.FullyQualifiedErrorId -match 'ObjectNotFound|NoMatching' -or "
        "$_.Exception.Message -match 'No MSFT_ScheduledTask objects found')) { "
        "[Console]::WriteLine('BATON_TASK_NOT_FOUND'); exit 0 }; "
        "[Console]::WriteLine('BATON_TASK_ERROR'); "
        "[Console]::Error.WriteLine($_.Exception.Message); exit 1 "
        "}"
    )
    return ["powershell", "-NoProfile", "-Command", script]


def daemon_task_state(deps: Deps) -> DaemonTaskState:
    """Classifies only the strict query protocol; every other result fails closed."""
    result = deps.run([
        *daemon_task_query_cmd(),
    ])
    lines = [line.strip() for line in result.stdout.splitlines() if line.strip()]
    if result.returncode != 0 or len(lines) != 1:
        return DaemonTaskState.QUERY_FAILED
    marker = lines[0]
    if marker == TASK_NOT_FOUND_MARKER:
        return DaemonTaskState.ABSENT
    if marker == TASK_ERROR_MARKER:
        return DaemonTaskState.QUERY_FAILED
    if not marker.startswith(TASK_STATE_PREFIX):
        return DaemonTaskState.QUERY_FAILED
    state = marker[len(TASK_STATE_PREFIX):].strip().casefold()
    if state not in TASK_STATES:
        return DaemonTaskState.QUERY_FAILED
    if state == "disabled":
        return DaemonTaskState.DISABLED
    return DaemonTaskState.ACTIVE


DAEMON_VERIFY_RETRIES = 30
DAEMON_VERIFY_BACKOFF_S = 2.0


def verify_daemon_started_under(deps: Deps, tool_dir: str, print_fn: Callable[[str], None]) -> bool:
    """Compatibility wrapper for callers that only need the replacement acceptance result."""
    return verify_daemon_replacement(deps, tool_dir, "", None, print_fn) is not None


def verify_daemon_replacement(
    deps: Deps,
    tool_dir: str,
    expected_version: str,
    old_identity: Optional[DaemonIdentity],
    print_fn: Callable[[str], None],
    retries: int = DAEMON_VERIFY_RETRIES,
) -> Optional[DaemonIdentity]:
    """Accept one singleton daemon with a stable identity and a fresh daemon heartbeat."""
    result = verify_daemon_replacement_result(deps, tool_dir, expected_version, old_identity, print_fn, retries)
    return result.identity if result.state == DaemonVerificationState.ACCEPTED else None


def verify_daemon_replacement_result(
    deps: Deps,
    tool_dir: str,
    expected_version: str,
    old_identity: Optional[DaemonIdentity],
    print_fn: Callable[[str], None],
    retries: int = DAEMON_VERIFY_RETRIES,
) -> DaemonVerification:
    """Return a distinct outcome so only a genuinely absent candidate can justify one retry."""
    last_state = DaemonVerificationState.NO_CANDIDATE
    for attempt in range(retries):
        processes = query_daemon_processes(deps)
        if processes is None:
            return DaemonVerification(DaemonVerificationState.QUERY_FAILED)
        if not processes:
            last_state = DaemonVerificationState.NO_CANDIDATE
        elif len(processes) != 1:
            return DaemonVerification(DaemonVerificationState.DUPLICATE)
        else:
            process = processes[0]
            if old_identity is not None and process.pid == old_identity.pid and process.creation_time == old_identity.creation_time:
                last_state = DaemonVerificationState.IDENTITY_UNSTABLE
            elif not process.creation_time:
                return DaemonVerification(DaemonVerificationState.IDENTITY_UNSTABLE)
            elif not path_is_under(process.executable_path, tool_dir):
                return DaemonVerification(DaemonVerificationState.WRONG_PATH)
            else:
                identity = capture_daemon_identity(deps, process, expected_version or None)
                if identity is None:
                    reported_version = daemon_version(deps, process.executable_path)
                    if expected_version and reported_version is not None and reported_version != expected_version:
                        return DaemonVerification(DaemonVerificationState.VERSION_MISMATCH)
                    return DaemonVerification(DaemonVerificationState.IDENTITY_UNSTABLE)
                if not daemon_health_probe(deps, identity):
                    last_state = DaemonVerificationState.UNHEALTHY
                else:
                    revalidated = capture_daemon_identity(deps, process, expected_version or None)
                    if revalidated is None or revalidated != identity:
                        return DaemonVerification(DaemonVerificationState.IDENTITY_UNSTABLE)
                    if not daemon_health_probe(deps, revalidated):
                        last_state = DaemonVerificationState.UNHEALTHY
                    else:
                        waited = attempt * DAEMON_VERIFY_BACKOFF_S
                        print_fn(
                            f"tool-refresh: daemon appeared after ~{waited:.0f}s "
                            f"(poll {attempt + 1} of {retries})"
                        )
                        return DaemonVerification(DaemonVerificationState.ACCEPTED, revalidated)
        if attempt < retries - 1:
            deps.sleep(DAEMON_VERIFY_BACKOFF_S)
    return DaemonVerification(last_state)


def scan_live_room_shas(rooms_root: str) -> Set[str]:
    """Finds all tool SHAs referenced in live (non-terminal) rooms."""
    live_shas: Set[str] = set()
    if not os.path.isdir(rooms_root):
        return live_shas

    for rname in os.listdir(rooms_root):
        rdir = os.path.join(rooms_root, rname)
        if not os.path.isdir(rdir):
            continue
        # Non-terminal check
        if os.path.isfile(os.path.join(rdir, "terminal.json")):
            continue
        bpath = os.path.join(rdir, "bindings.json")
        if os.path.isfile(bpath):
            try:
                with open(bpath, "r", encoding="utf-8") as f:
                    bdata = json.load(f)
                if isinstance(bdata, dict):
                    for entry in bdata.values():
                        if isinstance(entry, dict):
                            sha = entry.get("ToolSha") or entry.get("tool_sha")
                            if sha and isinstance(sha, str):
                                live_shas.add(sha.strip())
            except Exception:
                pass
    return live_shas


def prune_tools(deps: Deps, dry_run: bool, print_fn: Callable[[str], None], keep_count: int = 3) -> List[str]:
    """Cleans legacy tool installations while retaining the newest keep_count and active room versions."""
    tools_root = deps.tools_root
    if not os.path.isdir(tools_root):
        return []

    current_file = os.path.join(tools_root, "current")
    current_sha: Optional[str] = None
    if os.path.isfile(current_file):
        try:
            with open(current_file, "r", encoding="utf-8") as f:
                current_sha = f.read().strip()
        except OSError:
            pass

    entries = []
    for name in os.listdir(tools_root):
        dir_path = os.path.join(tools_root, name)
        if os.path.isdir(dir_path) and name != "current":
            try:
                mtime = os.path.getmtime(dir_path)
            except OSError:
                mtime = 0.0
            entries.append((name, mtime, dir_path))

    # Sort descending by mtime (newest first)
    entries.sort(key=lambda x: x[1], reverse=True)

    live_shas = scan_live_room_shas(deps.rooms_root)
    pruned: List[str] = []

    for idx, (sha, _, dir_path) in enumerate(entries):
        if idx < keep_count:
            continue
        if sha in live_shas:
            continue
        if current_sha and sha == current_sha:
            continue

        if dry_run:
            print_fn(f"tool-refresh: [dry-run] would prune old tool directory: {dir_path}")
            pruned.append(sha)
        else:
            try:
                shutil.rmtree(dir_path)
                print_fn(f"tool-refresh: pruned old tool directory: {dir_path}")
                pruned.append(sha)
            except OSError as exc:
                print_fn(f"tool-refresh: warning: could not prune {dir_path}: {exc}")

    return pruned


def refresh(deps: Deps, dry_run: bool, print_fn: Callable[[str], None]) -> int:
    """Executes the side-by-side refresh: pack -> install -> verify -> flip pointer -> launcher -> daemon -> prune."""
    version = read_repo_version(deps.repo_root)
    if version is None:
        print_fn(
            f"tool-refresh: could not read a <Version> from {VERSION_PROPS_RELATIVE_PATH} under "
            f"{deps.repo_root} -- refusing to proceed."
        )
        return 1

    sha = read_repo_commit_sha(deps)
    if sha is None:
        print_fn(f"tool-refresh: could not resolve current git commit SHA under {deps.repo_root}")
        return 1

    print_fn(f"tool-refresh: checkout version is {version}, commit is {sha}")

    pack_result = run_step(deps, ["pixi", "run", "pack"], dry_run, print_fn)
    if pack_result.returncode != 0:
        print_fn(f"tool-refresh: pack failed (exit {pack_result.returncode}): {pack_result.stderr.strip()}")
        return 1

    expected_nupkg = os.path.join(deps.repo_root, "bin", "pack", f"baton.{version}.nupkg")
    if not dry_run and not os.path.isfile(expected_nupkg):
        print_fn(f"tool-refresh: pack reported success but {expected_nupkg} does not exist -- refusing to install.")
        return 1

    purge_nuget_cache(deps, version, dry_run, print_fn)

    tool_dir = os.path.join(deps.tools_root, sha)
    skip_install = False

    # F4 (#1670 review): a re-refresh at an unchanged HEAD must never rmtree a directory a live lane
    # loaded from. If the SHA is already installed and verifies, this is a no-op -- skip straight to
    # the pointer flip. If it exists but fails verify, only remove it when nothing live references it
    # (current pointer or a non-terminal room's ToolSha); otherwise install into a fresh `<sha>-<n>`
    # side path and flip there instead of touching the directory in place.
    if not dry_run and os.path.isdir(tool_dir):
        if verify_target_exe(deps, tool_dir, version):
            print_fn(f"tool-refresh: {sha} is already installed and verified at {tool_dir} -- skipping reinstall")
            skip_install = True
        else:
            current_sha = read_current_pointer(deps.tools_root)
            live_shas = scan_live_room_shas(deps.rooms_root)
            is_live = sha == current_sha or sha in live_shas
            if is_live:
                suffix = 1
                candidate = f"{tool_dir}-{suffix}"
                while os.path.isdir(candidate):
                    suffix += 1
                    candidate = f"{tool_dir}-{suffix}"
                tool_dir = candidate
                print_fn(
                    f"tool-refresh: {sha} exists at {os.path.join(deps.tools_root, sha)} but failed "
                    f"verification and is live -- installing into {tool_dir} instead of touching a "
                    "directory a running lane may be using"
                )
            else:
                try:
                    shutil.rmtree(tool_dir)
                    print_fn(f"tool-refresh: {sha} exists but failed verification and is not live -- reinstalling at {tool_dir}")
                except OSError as exc:
                    print_fn(f"tool-refresh: warning: could not clean existing tool directory {tool_dir}: {exc}")

    if not skip_install:
        install_cmd = [
            "dotnet", "tool", "install", "baton",
            "--tool-path", tool_dir,
            "--add-source", "bin/pack",
        ]
        install_result = run_step(deps, install_cmd, dry_run, print_fn)
        if install_result.returncode != 0:
            print_fn(f"tool-refresh: install failed (exit {install_result.returncode}): {install_result.stderr.strip()}")
            return 1

        target_exe = os.path.join(tool_dir, "baton.exe" if (sys.platform == "win32" or os.name == "nt") else "baton")

        if dry_run:
            print_fn(f"tool-refresh: [dry-run] would verify directly: '{target_exe} --version' == {version} and 'templates --json'")
        else:
            version_result = deps.run([target_exe, "--version"])
            installed_version = version_result.stdout.strip()
            if version_result.returncode != 0 or installed_version != version:
                print_fn(
                    f"tool-refresh: verify failed -- '{target_exe} --version' printed "
                    f"{installed_version!r} (exit {version_result.returncode}), expected {version!r}."
                )
                return 1

            smoke_result = deps.run([target_exe, "templates", "--json"])
            if smoke_result.returncode != 0:
                print_fn(
                    f"tool-refresh: verify failed -- '{target_exe} templates --json' exited "
                    f"{smoke_result.returncode}: {smoke_result.stderr.strip()}"
                )
                return 1

    # Atomically flip pointer -- the actual installed directory name, which may be a `<sha>-<n>`
    # side path rather than `sha` itself (see the live-directory guard above).
    pointer_sha = sha if dry_run else os.path.basename(tool_dir)
    if not write_current_pointer(deps.tools_root, pointer_sha, dry_run, print_fn):
        return 1

    # Ensure launcher on PATH -- fails closed (F5) if a stale global-tool baton.exe still shadows it
    if not install_launcher(deps, dry_run, print_fn):
        return 1

    # Restart the daemon task -- it is a long-running process that only picks up a newly flipped
    # `current` pointer by being restarted; it does not re-resolve it on its own mid-run.
    active_daemon_verified = False
    if sys.platform == "win32" or os.name == "nt":
        daemon_stop_cmd = [
            "powershell", "-NoProfile", "-Command",
            "Stop-ScheduledTask -TaskName baton-daemon -ErrorAction SilentlyContinue",
        ]
        daemon_start_cmd = [
            "powershell", "-NoProfile", "-Command",
            "Start-ScheduledTask -TaskName baton-daemon -ErrorAction SilentlyContinue",
        ]

        if dry_run:
            run_step(deps, daemon_stop_cmd, dry_run, print_fn)
            run_step(deps, daemon_start_cmd, dry_run, print_fn)
        else:
            # A machine where the task was never registered, or where it is deliberately disabled,
            # receives installation-only success; refresh must not imply daemon activity it did not
            # verify.
            task_state = daemon_task_state(deps)
            if task_state == DaemonTaskState.QUERY_FAILED:
                print_fn(
                    "tool-refresh: could not query the baton-daemon scheduled task; refusing to "
                    "claim installation or daemon activity succeeded"
                )
                return 1
            if task_state in (DaemonTaskState.ABSENT, DaemonTaskState.DISABLED):
                reason = task_state.value
                print_fn(
                    f"tool-refresh: baton-daemon scheduled task is {reason} -- skipped restart and "
                    "post-restart verify; installation-only success (daemon activity not claimed)"
                )
            else:
                pre_restart = capture_daemon_identities(deps)
                if pre_restart is None:
                    print_fn(
                        "tool-refresh: could not capture the pre-restart daemon PID, executable, and "
                        "version. Refusing to stop the scheduled task; check the process query and re-run."
                    )
                    return 1
                if len(pre_restart) > 1:
                    listing = ", ".join(
                        f"pid={identity.pid} path={identity.executable_path} version={identity.version}"
                        for identity in pre_restart
                    )
                    print_fn(
                        f"tool-refresh: found multiple pre-restart daemon identities ({listing}); "
                        "singleton ownership is already violated. Refusing to restart."
                    )
                    return 1

                old_identity = pre_restart[0] if pre_restart else None
                if old_identity is None:
                    print_fn("tool-refresh: no pre-restart daemon identity was found")
                else:
                    print_fn(
                        f"tool-refresh: captured old daemon pid={old_identity.pid} "
                        f"path={old_identity.executable_path} version={old_identity.version}"
                    )

                stop_result = run_step(deps, daemon_stop_cmd, dry_run=False, print_fn=print_fn)
                if stop_result.returncode != 0:
                    print_fn(
                        f"tool-refresh: scheduled-task stop failed (exit {stop_result.returncode}); "
                        "the daemon was not restarted. Check Task Scheduler and re-run."
                    )
                    return 1

                if old_identity is not None:
                    exit_state = wait_for_daemon_exit(deps, old_identity, print_fn)
                else:
                    exit_state = DaemonExitState.EXITED
                if exit_state != DaemonExitState.EXITED:
                    ceiling = DAEMON_OLD_EXIT_RETRIES * DAEMON_OLD_EXIT_BACKOFF_S
                    if exit_state == DaemonExitState.QUERY_FAILED:
                        print_fn(
                            "tool-refresh: daemon identity query failed while waiting for the old "
                            "daemon to exit. Refusing to start or accept a replacement."
                        )
                    elif exit_state == DaemonExitState.IDENTITY_CHANGED:
                        print_fn(
                            "tool-refresh: the pre-restart daemon identity changed while waiting "
                            "for exit. Refusing to start or accept a replacement."
                        )
                    else:
                        print_fn(
                            f"tool-refresh: old daemon pid={old_identity.pid if old_identity else 'unknown'} "
                            f"did not exit within ~{ceiling:.0f}s after requesting task stop. Refusing "
                            "to start or accept a replacement; do not force-kill by default. Check "
                            "daemon.log/Task Scheduler, close any holder, and re-run tool-refresh."
                        )
                    return 1

                replacement: Optional[DaemonIdentity] = None
                polls_per_attempt = max(1, DAEMON_VERIFY_RETRIES // DAEMON_START_ATTEMPTS)
                for start_attempt in range(DAEMON_START_ATTEMPTS):
                    start_result = run_step(deps, daemon_start_cmd, dry_run=False, print_fn=print_fn)
                    if start_result.returncode != 0:
                        print_fn(
                            f"tool-refresh: scheduled-task start attempt {start_attempt + 1} failed "
                            f"(exit {start_result.returncode}); refusing a retry"
                        )
                        return 1
                    verification = verify_daemon_replacement_result(
                        deps, tool_dir, version, old_identity, print_fn, retries=polls_per_attempt
                    )
                    replacement = verification.identity
                    if verification.state == DaemonVerificationState.ACCEPTED:
                        break
                    if verification.state != DaemonVerificationState.NO_CANDIDATE:
                        print_fn(
                            f"tool-refresh: first-start verification failed distinctly: "
                            f"{verification.state.value}; refusing a second start"
                        )
                        return 1
                    if start_attempt + 1 < DAEMON_START_ATTEMPTS:
                        print_fn(
                            "tool-refresh: no replacement candidate appeared after the first successful "
                            "task start; evidence is compatible with Task Scheduler swallowing an "
                            "overlap-policy start, retrying Start-ScheduledTask once"
                        )

                if replacement is None:
                    ceiling = DAEMON_VERIFY_RETRIES * DAEMON_VERIFY_BACKOFF_S
                    found = find_daemon_processes(deps)
                    listing = (
                        ", ".join(
                            f"pid={process.pid} start={process.creation_time} path={process.executable_path}"
                            for process in found
                        )
                        if found else "none"
                    )
                    print_fn(
                        f"tool-refresh: replacement-start/health timeout: no replacement candidate "
                        f"under {tool_dir} within ~{ceiling:.0f}s ({listing}). Refusing to declare the "
                        "refresh done. Check daemon.log, Task Scheduler, the installed tool path, and "
                        "the singleton lock, then re-run tool-refresh."
                    )
                    return 1

                active_daemon_verified = replacement is not None

                old_text = (
                    f"pid={old_identity.pid} path={old_identity.executable_path} version={old_identity.version}"
                    if old_identity is not None else "none"
                )
                print_fn(
                    f"tool-refresh: restarted baton-daemon scheduled task; old identity [{old_text}], "
                    f"new identity [pid={replacement.pid} path={replacement.executable_path} "
                    f"version={replacement.version}]"
                )

    # Prune old tool installations
    prune_tools(deps, dry_run, print_fn, keep_count=3)

    if dry_run:
        print_fn(
            f"tool-refresh: [dry-run] preview complete -- target baton {version} ({pointer_sha}) "
            f"at {tool_dir}; no installation, pointer flip, daemon restart, or active-daemon "
            "verification performed"
        )
    else:
        if active_daemon_verified:
            print_fn(f"tool-refresh: verified -- baton {version} ({pointer_sha}) installed at {tool_dir} and active")
        else:
            print_fn(
                f"tool-refresh: installed -- baton {version} ({pointer_sha}) installed at {tool_dir}; "
                "installation-only success (daemon activity not claimed)"
            )
    return 0


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--dry-run", action="store_true", help="print every command; run none of the mutating ones")
    parser.add_argument(
        "--abort", action="store_true",
        help="remove a manual drain marker and do nothing else")
    parser.add_argument("--selftest", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    if args.abort:
        return abort(Deps(), print, args.dry_run)

    return refresh(Deps(), args.dry_run, print)


# ---------------------------------------------------------------------------------------------
# Selftest
# ---------------------------------------------------------------------------------------------

def _assert_isolated(deps: Deps) -> None:
    real_home = os.path.realpath(default_baton_home())
    if os.path.realpath(deps.baton_home) == real_home:
        raise AssertionError(
            f"selftest fixture resolved the REAL baton home ({real_home}) -- refusing to run: "
            "every arm must inject a temp baton_home")
    real_nuget = os.path.realpath(
        os.environ.get("NUGET_PACKAGES", os.path.join(os.path.expanduser("~"), ".nuget", "packages")))
    if os.path.realpath(deps.nuget_packages_root) == real_nuget:
        raise AssertionError(
            f"selftest fixture resolved the REAL NuGet packages root ({real_nuget}) -- refusing to run")
    real_dotnet_tools = os.path.realpath(os.path.join(os.path.expanduser("~"), ".dotnet", "tools"))
    if os.path.realpath(deps.dotnet_tools_root) == real_dotnet_tools:
        raise AssertionError(
            f"selftest fixture resolved the REAL dotnet tools root ({real_dotnet_tools}) -- refusing to run")


def _fixture_repo(td: str, version: str) -> str:
    props_dir = os.path.join(td, "src", "Baton.Cli")
    os.makedirs(props_dir, exist_ok=True)
    with open(os.path.join(props_dir, "Directory.Build.props"), "w", encoding="utf-8") as f:
        f.write(f"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>")
    pack_dir = os.path.join(td, "bin", "pack")
    os.makedirs(pack_dir, exist_ok=True)
    open(os.path.join(pack_dir, f"baton.{version}.nupkg"), "w", encoding="utf-8").close()

    launcher_dir = os.path.join(td, "tools", "tool-refresh", "launcher")
    os.makedirs(launcher_dir, exist_ok=True)
    for name in ["baton.cmd", "baton.ps1", "baton"]:
        open(os.path.join(launcher_dir, name), "w", encoding="utf-8").close()
    return td


def _write_test_heartbeat(
    baton_home: str,
    started_at: Optional[str] = None,
    identity: Optional[DaemonIdentity] = None,
    tick_completed_at: Optional[str] = None,
) -> None:
    """Fixture helper for the daemon heartbeat contract, including replacement identity."""
    now = dt.datetime.now(dt.timezone.utc).isoformat()
    identity = identity or DaemonIdentity(4243, now, r"C:\baton\tools\new\baton.exe", "2.3.4")
    heartbeat_path = os.path.join(baton_home, "fleet", "heartbeat.json")
    os.makedirs(os.path.dirname(heartbeat_path), exist_ok=True)
    with open(heartbeat_path, "w", encoding="utf-8") as f:
        json.dump(
            {
                "startedAt": started_at or now,
                "tickCompletedAt": tick_completed_at or now,
                "identity": {
                    "pid": identity.pid,
                    "processStartTime": identity.creation_time,
                    "executablePath": identity.executable_path,
                    "version": identity.version,
                },
                "services": {},
            },
            f,
        )


def _make_room(rooms_root: str, name: str, terminal: bool, tool_sha: Optional[str] = None) -> str:
    room_dir = os.path.join(rooms_root, name)
    os.makedirs(room_dir, exist_ok=True)
    if terminal:
        with open(os.path.join(room_dir, "terminal.json"), "w", encoding="utf-8") as f:
            f.write("{}")
    if tool_sha is not None:
        bindings = {
            "worker": {
                "Adapter": "claude",
                "ToolSha": tool_sha,
            }
        }
        with open(os.path.join(room_dir, "bindings.json"), "w", encoding="utf-8") as f:
            json.dump(bindings, f)
    return room_dir


def _selftest_version_compare() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        props_dir = os.path.join(td, "src", "Baton.Cli")
        os.makedirs(props_dir)
        props_path = os.path.join(props_dir, "Directory.Build.props")

        with open(props_path, "w", encoding="utf-8") as f:
            f.write("<Project>\n  <PropertyGroup>\n    <Version>1.2.3</Version>\n  </PropertyGroup>\n</Project>\n")
        if read_repo_version(td) != "1.2.3":
            print(f"  FAILED: read_repo_version did not read 1.2.3, got {read_repo_version(td)!r}")
            ok = False

        missing_version_dir = os.path.join(td, "no-props")
        os.makedirs(missing_version_dir)
        if read_repo_version(missing_version_dir) is not None:
            print("  FAILED: read_repo_version returned a value with no Directory.Build.props present")
            ok = False

    return ok


def _selftest_pointer_flip_atomic() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        tools_dir = os.path.join(td, "tools")
        messages: List[str] = []
        if not write_current_pointer(tools_dir, "abc12345", dry_run=False, print_fn=messages.append):
            print("  FAILED: write_current_pointer failed")
            ok = False

        current_file = os.path.join(tools_dir, "current")
        if not os.path.isfile(current_file):
            print("  FAILED: current pointer file was not created")
            ok = False
        else:
            with open(current_file, "r", encoding="utf-8") as f:
                content = f.read().strip()
            if content != "abc12345":
                print(f"  FAILED: current pointer has {content!r}, want 'abc12345'")
                ok = False

        # Flip to another sha
        if not write_current_pointer(tools_dir, "def67890", dry_run=False, print_fn=messages.append):
            print("  FAILED: secondary write_current_pointer failed")
            ok = False
        with open(current_file, "r", encoding="utf-8") as f:
            content = f.read().strip()
        if content != "def67890":
            print(f"  FAILED: updated current pointer has {content!r}, want 'def67890'")
            ok = False

    return ok


def _selftest_pointer_flip_retries_transient_permission_error() -> bool:
    """#1701: a real second occurrence hit `[WinError 5] Access is denied` on os.replace flipping
    current -- a transient handle on a just-written destination, not shared state between runs.
    One injected PermissionError must not fail the flip; a PERSISTENT one still must fail closed."""
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        tools_dir = os.path.join(td, "tools")
        real_replace = os.replace

        # One transient failure, then success -- the flip must retry and still land.
        calls: List[int] = []
        sleeps: List[float] = []

        def flaky_once(src: str, dst: str) -> None:
            calls.append(1)
            if len(calls) == 1:
                raise PermissionError("[WinError 5] Access is denied")
            real_replace(src, dst)

        messages: List[str] = []
        result = write_current_pointer(
            tools_dir, "abc12345", dry_run=False, print_fn=messages.append,
            replace=flaky_once, sleep=sleeps.append,
        )
        if not result:
            print("  FAILED: write_current_pointer failed despite the transient PermissionError being retryable")
            ok = False
        if len(calls) != 2:
            print(f"  FAILED: replace was called {len(calls)} time(s), want 2 (one failure, one retry that lands)")
            ok = False
        if not sleeps:
            print("  FAILED: no backoff sleep was taken between the failed attempt and the retry")
            ok = False

        current_file = os.path.join(tools_dir, "current")
        if not os.path.isfile(current_file):
            print("  FAILED: current pointer file was not created after the retried flip")
            ok = False
        else:
            with open(current_file, "r", encoding="utf-8") as f:
                if f.read().strip() != "abc12345":
                    print("  FAILED: current pointer does not hold the flipped-to sha after retry")
                    ok = False

        # A PERSISTENT PermissionError must still fail closed, not retry forever.
        def always_locked(src: str, dst: str) -> None:
            raise PermissionError("[WinError 5] Access is denied")

        persistent_sleeps: List[float] = []
        messages2: List[str] = []
        result2 = write_current_pointer(
            tools_dir, "def67890", dry_run=False, print_fn=messages2.append,
            replace=always_locked, sleep=persistent_sleeps.append,
        )
        if result2:
            print("  FAILED: write_current_pointer reported success despite a persistent PermissionError")
            ok = False
        if len(persistent_sleeps) != POINTER_REPLACE_RETRIES - 1:
            print(f"  FAILED: persistent failure slept {len(persistent_sleeps)} time(s), want {POINTER_REPLACE_RETRIES - 1} (retries exhausted, then fail closed)")
            ok = False
        # Fail closed means the LAST GOOD pointer survives -- never a half-written or missing one.
        with open(current_file, "r", encoding="utf-8") as f:
            if f.read().strip() != "abc12345":
                print("  FAILED: current pointer was corrupted by the persistent failure -- want it still holding the prior good sha 'abc12345'")
                ok = False
        if any(name.startswith("current.tmp.") for name in os.listdir(tools_dir)):
            print("  FAILED: a current.tmp.<uuid> file was left behind after the persistent failure exhausted its retries")
            ok = False
        if not any("Access is denied" in m for m in messages2):
            print(f"  FAILED: failure message did not surface the underlying PermissionError. Messages: {messages2}")
            ok = False

    return ok


def _selftest_prune_logic() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        baton_home = td
        tools_root = os.path.join(td, "tools")
        rooms_root = os.path.join(td, "rooms")
        dotnet_tools_root = os.path.join(td, "dotnet_tools")
        nuget_root = os.path.join(td, "nuget")
        os.makedirs(tools_root)
        os.makedirs(rooms_root)

        # Create 5 tool directories with different mtimes
        shas = ["sha1_newest", "sha2", "sha3", "sha4_referenced", "sha5_old"]
        for idx, sha in enumerate(shas):
            sdir = os.path.join(tools_root, sha)
            os.makedirs(sdir)
            # Higher mtime for earlier index (newest)
            mtime = 1000.0 - (idx * 100.0)
            os.utime(sdir, (mtime, mtime))

        # Write current pointer to sha1_newest
        with open(os.path.join(tools_root, "current"), "w", encoding="utf-8") as f:
            f.write("sha1_newest\n")

        # Create live room referencing sha4_referenced
        _make_room(rooms_root, "live-room-1", terminal=False, tool_sha="sha4_referenced")
        # Create terminal room referencing sha5_old
        _make_room(rooms_root, "term-room-1", terminal=True, tool_sha="sha5_old")

        deps = Deps(
            run=real_runner, baton_home=baton_home, rooms_root=rooms_root,
            tools_root=tools_root, dotnet_tools_root=dotnet_tools_root,
            nuget_packages_root=nuget_root, repo_root=td
        )
        _assert_isolated(deps)

        messages: List[str] = []
        pruned = prune_tools(deps, dry_run=False, print_fn=messages.append, keep_count=3)

        # sha1, sha2, sha3 are top 3 -> kept.
        # sha4 is 4th, but referenced in live room -> kept.
        # sha5 is 5th, referenced only in terminal room -> pruned!
        if pruned != ["sha5_old"]:
            print(f"  FAILED: prune_tools pruned {pruned!r}, want ['sha5_old']")
            ok = False

        if os.path.exists(os.path.join(tools_root, "sha5_old")):
            print("  FAILED: sha5_old directory still exists on disk")
            ok = False
        if not os.path.exists(os.path.join(tools_root, "sha4_referenced")):
            print("  FAILED: sha4_referenced was wrongly deleted")
            ok = False
        if not os.path.exists(os.path.join(tools_root, "sha1_newest")):
            print("  FAILED: sha1_newest was wrongly deleted")
            ok = False

    return ok


def _selftest_refresh_end_to_end_mocked() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        baton_home = os.path.join(td, "baton")
        tools_root = os.path.join(baton_home, "tools")
        rooms_root = os.path.join(baton_home, "rooms")
        dotnet_tools_root = os.path.join(td, "dotnet_tools")
        nuget_root = os.path.join(td, "nuget")
        repo_root = _fixture_repo(os.path.join(td, "repo"), "2.3.4")

        commands_run: List[List[str]] = []
        daemon_state = {"phase": "old", "start_calls": 0}
        old_exe = os.path.join(tools_root, "oldsha", "baton.exe")
        new_exe = os.path.join(tools_root, "c0ffee11", "baton.exe")

        def run(cmd: List[str]) -> CommandResult:
            commands_run.append(cmd)
            if cmd[:3] == ["git", "-C", repo_root] and cmd[3:5] == ["rev-parse", "--short"]:
                return CommandResult(0, "c0ffee11\n")
            if cmd[:3] == ["pixi", "run", "pack"]:
                return CommandResult(0)
            if cmd[:3] == ["dotnet", "tool", "list"]:
                return CommandResult(0, "Package Id      Version\nbaton           1.0.0\n")
            if cmd[:4] == ["dotnet", "tool", "uninstall", "--global"]:
                return CommandResult(0)
            if cmd[:4] == ["dotnet", "tool", "install", "baton"]:
                # Create fake target exe
                tool_dir = os.path.join(tools_root, "c0ffee11")
                os.makedirs(tool_dir, exist_ok=True)
                target_exe = new_exe
                open(target_exe, "w").close()
                return CommandResult(0)
            if len(cmd) == 2 and cmd[1] == "--version":
                return CommandResult(0, "2.3.4\n")
            if len(cmd) == 3 and cmd[1:] == ["templates", "--json"]:
                return CommandResult(0, "[]\n")
            if cmd[:3] == ["dotnet", "build", "src/Baton.Cli"]:
                return CommandResult(0)
            if cmd[0] == "powershell" and "Get-ScheduledTask" in cmd[3]:
                return CommandResult(0, "BATON_TASK_STATE=Ready\n")
            if cmd[0] == "powershell" and "Get-Process -Id" in cmd[3]:
                return CommandResult(0, "4243\n" if daemon_state["phase"] == "new" else "")
            if cmd[0] == "powershell" and "Win32_Process" in cmd[3]:
                if daemon_state["phase"] == "old":
                    return CommandResult(0, f"4242|2026-01-01T00:00:00.000Z|{old_exe}\n")
                if daemon_state["phase"] == "new":
                    return CommandResult(0, f"4243|2026-01-01T00:01:00.000Z|{new_exe}\n")
                return CommandResult(0, "")
            if cmd[0] == "powershell" and "Stop-ScheduledTask" in cmd[3]:
                daemon_state["phase"] = "stopped"
                return CommandResult(0)
            if cmd[0] == "powershell" and "Start-ScheduledTask" in cmd[3]:
                daemon_state["start_calls"] += 1
                if daemon_state["start_calls"] > 1:
                    daemon_state["phase"] = "new"
                    _write_test_heartbeat(
                        baton_home,
                        identity=DaemonIdentity(4243, "2026-01-01T00:01:00.000Z", new_exe, "2.3.4"),
                    )
                return CommandResult(0)
            if cmd[0] == "powershell":
                return CommandResult(0)
            raise AssertionError(f"unexpected command in refresh selftest: {cmd}")

        deps = Deps(
            run=run, baton_home=baton_home, rooms_root=rooms_root,
            tools_root=tools_root, dotnet_tools_root=dotnet_tools_root,
            nuget_packages_root=nuget_root, repo_root=repo_root
        )
        _assert_isolated(deps)

        messages: List[str] = []
        code = refresh(deps, dry_run=False, print_fn=messages.append)
        if code != 0:
            print(f"  FAILED: refresh exited {code}, want 0. Messages: {messages}")
            ok = False
        if not any(m.startswith("tool-refresh: verified -- baton 2.3.4 ")
                   and m.endswith(" and active") for m in messages):
            print("  FAILED: real refresh did not report its verified active install")
            ok = False
        if any("[dry-run] preview complete" in m for m in messages):
            print("  FAILED: real refresh falsely reported a preview")
            ok = False

        current_file = os.path.join(tools_root, "current")
        if not os.path.isfile(current_file):
            print("  FAILED: current pointer file missing after refresh")
            ok = False
        else:
            with open(current_file, "r", encoding="utf-8") as f:
                if f.read().strip() != "c0ffee11":
                    print("  FAILED: current pointer did not contain 'c0ffee11'")
                    ok = False

        # Launcher files must be copied to dotnet_tools_root
        for name in ["baton.cmd", "baton.ps1", "baton"]:
            if not os.path.isfile(os.path.join(dotnet_tools_root, name)):
                print(f"  FAILED: launcher file {name} was not installed in {dotnet_tools_root}")
                ok = False

        # Global tool uninstall was run
        if not any("uninstall" in " ".join(c) for c in commands_run):
            print("  FAILED: dotnet tool uninstall --global baton was not executed")
            ok = False

        # The daemon task is restarted after every refresh so it cycles onto the newly flipped tool
        # head.
        powershell_cmds = [" ".join(c) for c in commands_run if c and c[0] == "powershell"]
        if not any("baton-daemon" in c for c in powershell_cmds):
            print(f"  FAILED: baton-daemon scheduled task was not restarted. powershell commands: {powershell_cmds}")
            ok = False

        # The retired mailbox task must not be mentioned by any refresh action. Keep this negative
        # assertion on the complete command trace so a future build/log/restart step cannot quietly
        # resurrect its task or state writes.
        refresh_commands = [" ".join(c) for c in commands_run]

        def no_retired_pusher_command(commands: Sequence[str]) -> bool:
            return not any("fleet-glass-pusher" in c.lower() for c in commands)

        if not no_retired_pusher_command(refresh_commands):
            print(f"  FAILED: refresh mentioned the retired fleet-glass-pusher task. commands: {refresh_commands}")
            ok = False

        # Synthetic pre-change arm: the assertion must reject the old restart shape, or this test
        # would still pass against a refresh that had reintroduced the retired task.
        reintroduced_commands = refresh_commands + [
            "Stop-ScheduledTask -TaskName fleet-glass-pusher",
            "Start-ScheduledTask -TaskName fleet-glass-pusher",
            "Add-Content fleet-glass-pusher.log refresh",
        ]
        if no_retired_pusher_command(reintroduced_commands):
            print("  FAILED: retired-task negative assertion did not detect a synthetic reintroduction")
            ok = False

        # The old identity is observed, the scheduler is stopped, and only then is the new task
        # started and verified.
        win32_indexes = [i for i, c in enumerate(powershell_cmds) if "Win32_Process" in c]
        daemon_start_index = next(
            i for i, c in enumerate(powershell_cmds) if "Start-ScheduledTask -TaskName baton-daemon" in c)
        daemon_stop_index = next(
            i for i, c in enumerate(powershell_cmds) if "Stop-ScheduledTask -TaskName baton-daemon" in c)
        if not win32_indexes or win32_indexes[0] >= daemon_stop_index or daemon_stop_index >= daemon_start_index:
            print(
                f"  FAILED: old identity/stop/start ordering was wrong. "
                f"powershell commands: {powershell_cmds}"
            )
            ok = False

        if not any("old identity [pid=4242" in m and "new identity [pid=4243" in m for m in messages):
            print(f"  FAILED: refresh did not report old and new daemon identities. Messages: {messages}")
            ok = False
        if daemon_state["start_calls"] != 2:
            print(f"  FAILED: swallowed first start was not retried exactly once: {daemon_state}")
            ok = False

    return ok


def _selftest_fail_closed_on_daemon_verify_failure() -> bool:
    """#1773: if no baton.exe daemon process comes up under the just-installed tool_dir after
    restarting baton-daemon (e.g. an orphan on the old sha is still holding the mutex), refresh must
    fail closed -- not report success because Start-ScheduledTask itself returned 0."""
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        baton_home = os.path.join(td, "baton")
        tools_root = os.path.join(baton_home, "tools")
        rooms_root = os.path.join(baton_home, "rooms")
        dotnet_tools_root = os.path.join(td, "dotnet_tools")
        nuget_root = os.path.join(td, "nuget")
        repo_root = _fixture_repo(os.path.join(td, "repo"), "6.6.6")

        def run(cmd: List[str]) -> CommandResult:
            if cmd[:3] == ["git", "-C", repo_root] and cmd[3:5] == ["rev-parse", "--short"]:
                return CommandResult(0, "6060606\n")
            if cmd[:3] == ["pixi", "run", "pack"]:
                return CommandResult(0)
            if cmd[:3] == ["dotnet", "tool", "list"]:
                return CommandResult(0, "")
            if cmd[:4] == ["dotnet", "tool", "install", "baton"]:
                tool_dir = os.path.join(tools_root, "6060606")
                os.makedirs(tool_dir, exist_ok=True)
                target_exe = os.path.join(tool_dir, "baton.exe" if (sys.platform == "win32" or os.name == "nt") else "baton")
                open(target_exe, "w").close()
                return CommandResult(0)
            if len(cmd) == 2 and cmd[1] == "--version":
                return CommandResult(0, "6.6.6\n")
            if len(cmd) == 3 and cmd[1:] == ["templates", "--json"]:
                return CommandResult(0, "[]\n")
            if cmd[:3] == ["dotnet", "build", "src/Baton.Cli"]:
                return CommandResult(0)
            if cmd[0] == "powershell" and "Get-ScheduledTask" in cmd[3]:
                return CommandResult(0, "BATON_TASK_STATE=Ready\n")
            if cmd[0] == "powershell" and "Win32_Process" in cmd[3]:
                # No matching process ever reports back -- e.g. the task never actually launched it.
                return CommandResult(0, "")
            if cmd[0] == "powershell":
                return CommandResult(0)
            raise AssertionError(f"unexpected command: {cmd}")

        deps = Deps(
            run=run, baton_home=baton_home, rooms_root=rooms_root,
            tools_root=tools_root, dotnet_tools_root=dotnet_tools_root,
            nuget_packages_root=nuget_root, repo_root=repo_root, sleep=lambda s: None,
        )
        _assert_isolated(deps)

        messages: List[str] = []
        code = refresh(deps, dry_run=False, print_fn=messages.append)
        if code == 0:
            print("  FAILED: refresh exited 0 despite no baton.exe daemon process coming up after restart")
            ok = False
        if not any("replacement-start/health timeout" in m for m in messages):
            print(f"  FAILED: refresh did not report the replacement timeout loudly. Messages: {messages}")
            ok = False

    return ok


def _selftest_daemon_verify_waits_for_a_slow_launch() -> bool:
    """#1842: the daemon came up ~21s after Start-ScheduledTask on the operator's machine and the old
    5s poll declared a successful refresh failed. The verify must keep polling up to its ceiling and
    succeed when the daemon appears on a later poll -- and say how long it took."""
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        baton_home = os.path.join(td, "baton")
        tools_root = os.path.join(baton_home, "tools")
        rooms_root = os.path.join(baton_home, "rooms")
        dotnet_tools_root = os.path.join(td, "dotnet_tools")
        nuget_root = os.path.join(td, "nuget")
        repo_root = _fixture_repo(os.path.join(td, "repo"), "6.6.6")
        tool_dir = os.path.join(tools_root, "6060606")
        # The first query captures the old identity; the replacement appears on a later query,
        # beyond the old short polling ceiling.
        appear_on_query = 1 + 8
        queries = {"n": 0}

        def run(cmd: List[str]) -> CommandResult:
            if cmd[:3] == ["git", "-C", repo_root] and cmd[3:5] == ["rev-parse", "--short"]:
                return CommandResult(0, "6060606\n")
            if cmd[:3] == ["pixi", "run", "pack"]:
                return CommandResult(0)
            if cmd[:3] == ["dotnet", "tool", "list"]:
                return CommandResult(0, "")
            if cmd[:4] == ["dotnet", "tool", "install", "baton"]:
                os.makedirs(tool_dir, exist_ok=True)
                target_exe = os.path.join(tool_dir, "baton.exe" if (sys.platform == "win32" or os.name == "nt") else "baton")
                open(target_exe, "w").close()
                return CommandResult(0)
            if len(cmd) == 2 and cmd[1] == "--version":
                return CommandResult(0, "6.6.6\n")
            if len(cmd) == 3 and cmd[1:] == ["templates", "--json"]:
                return CommandResult(0, "[]\n")
            if cmd[:3] == ["dotnet", "build", "src/Baton.Cli"]:
                return CommandResult(0)
            if cmd[0] == "powershell" and "Get-ScheduledTask" in cmd[3]:
                return CommandResult(0, "BATON_TASK_STATE=Ready\n")
            if cmd[0] == "powershell" and "Get-Process -Id" in cmd[3]:
                return CommandResult(0, "4243\n" if queries["n"] >= appear_on_query else "")
            if cmd[0] == "powershell" and "Win32_Process" in cmd[3]:
                queries["n"] += 1
                if queries["n"] == 1:
                    return CommandResult(0, f"4242|{os.path.join(td, 'old', 'baton.exe')}\n")
                if queries["n"] >= appear_on_query:
                    return CommandResult(0, f"4243|{os.path.join(tool_dir, 'baton.exe')}\n")
                return CommandResult(0, "")
            if cmd[0] == "powershell":
                return CommandResult(0)
            raise AssertionError(f"unexpected command: {cmd}")

        deps = Deps(
            run=run, baton_home=baton_home, rooms_root=rooms_root,
            tools_root=tools_root, dotnet_tools_root=dotnet_tools_root,
            nuget_packages_root=nuget_root, repo_root=repo_root, sleep=lambda s: None,
        )
        _assert_isolated(deps)

        messages: List[str] = []
        code = refresh(deps, dry_run=False, print_fn=messages.append)
        if code != 0:
            print(f"  FAILED: refresh exited {code} although the daemon appeared on poll {appear_on_query - 1}. Messages: {messages}")
            ok = False
        if not any("daemon appeared after" in m for m in messages):
            print(f"  FAILED: refresh did not report how long the daemon took to appear. Messages: {messages}")
            ok = False
        if queries["n"] < appear_on_query:
            print(f"  FAILED: verify stopped polling after {queries['n'] - 1} polls, before the daemon appeared")
            ok = False

    return ok


def _selftest_fail_closed_on_verify_failure() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        baton_home = os.path.join(td, "baton")
        tools_root = os.path.join(baton_home, "tools")
        rooms_root = os.path.join(baton_home, "rooms")
        dotnet_tools_root = os.path.join(td, "dotnet_tools")
        nuget_root = os.path.join(td, "nuget")
        repo_root = _fixture_repo(os.path.join(td, "repo"), "9.9.9")

        def run(cmd: List[str]) -> CommandResult:
            if cmd[:3] == ["git", "-C", repo_root]:
                return CommandResult(0, "badsha99\n")
            if cmd[:3] == ["pixi", "run", "pack"]:
                return CommandResult(0)
            if cmd[:3] == ["dotnet", "tool", "list"]:
                return CommandResult(0, "")
            if cmd[:4] == ["dotnet", "tool", "install", "baton"]:
                tool_dir = os.path.join(tools_root, "badsha99")
                os.makedirs(tool_dir, exist_ok=True)
                target_exe = os.path.join(tool_dir, "baton.exe" if (sys.platform == "win32" or os.name == "nt") else "baton")
                open(target_exe, "w").close()
                return CommandResult(0)
            if len(cmd) == 2 and cmd[1] == "--version":
                return CommandResult(1, "", "crash")
            if len(cmd) == 3 and cmd[1:] == ["templates", "--json"]:
                return CommandResult(0)
            raise AssertionError(f"unexpected command: {cmd}")

        deps = Deps(
            run=run, baton_home=baton_home, rooms_root=rooms_root,
            tools_root=tools_root, dotnet_tools_root=dotnet_tools_root,
            nuget_packages_root=nuget_root, repo_root=repo_root
        )
        _assert_isolated(deps)

        messages: List[str] = []
        code = refresh(deps, dry_run=False, print_fn=messages.append)
        if code == 0:
            print("  FAILED: refresh exited 0 despite failing verification")
            ok = False

        current_file = os.path.join(tools_root, "current")
        if os.path.exists(current_file):
            print("  FAILED: current pointer was flipped despite failing verification")
            ok = False

    return ok


def _selftest_dry_run_touches_nothing() -> bool:
    import tempfile
    from unittest.mock import patch

    ok = True
    with tempfile.TemporaryDirectory() as td:
        baton_home = os.path.join(td, "baton")
        tools_root = os.path.join(baton_home, "tools")
        rooms_root = os.path.join(baton_home, "rooms")
        dotnet_tools_root = os.path.join(td, "dotnet_tools")
        nuget_root = os.path.join(td, "nuget")
        repo_root = _fixture_repo(os.path.join(td, "repo"), "5.5.5")

        def run(cmd: List[str]) -> CommandResult:
            if cmd[:3] == ["git", "-C", repo_root]:
                return CommandResult(0, "drysha55\n")
            raise AssertionError(f"--dry-run must not run commands, got: {cmd}")

        deps = Deps(
            run=run, baton_home=baton_home, rooms_root=rooms_root,
            tools_root=tools_root, dotnet_tools_root=dotnet_tools_root,
            nuget_packages_root=nuget_root, repo_root=repo_root
        )
        _assert_isolated(deps)

        messages: List[str] = []
        # Exercise the scheduled-task preview on every CI host, including Linux gate runners.
        with patch.object(sys, "platform", "win32"):
            code = refresh(deps, dry_run=True, print_fn=messages.append)
        if code != 0:
            print(f"  FAILED: --dry-run exited {code}, want 0")
            ok = False
        if not any("[dry-run]" in m for m in messages):
            print("  FAILED: --dry-run printed no [dry-run] lines")
            ok = False
        if os.path.exists(os.path.join(tools_root, "current")):
            print("  FAILED: --dry-run wrote current pointer")
            ok = False
        if any(m == "tool-refresh: restarted baton-daemon scheduled task" for m in messages):
            print("  FAILED: --dry-run falsely reported a completed daemon restart")
            ok = False
        if any(m.startswith("tool-refresh: verified -- ") and m.endswith(" and active")
               for m in messages):
            print("  FAILED: --dry-run falsely reported an active verified install")
            ok = False
        if not any(m.startswith("tool-refresh: [dry-run] preview complete -- ") for m in messages):
            print("  FAILED: --dry-run did not identify its final report as a preview")
            ok = False

    return ok


def _selftest_abort() -> bool:
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        deps = Deps(baton_home=td, rooms_root=os.path.join(td, "rooms"),
                    tools_root=os.path.join(td, "tools"),
                    dotnet_tools_root=os.path.join(td, "dotnet_tools"),
                    nuget_packages_root=os.path.join(td, "nuget"),
                    repo_root=td)
        _assert_isolated(deps)

        marker = drain_marker_path(deps)
        with open(marker, "w") as f:
            f.write("{}")

        messages: List[str] = []
        if abort(deps, messages.append) != 0 or os.path.exists(marker):
            print("  FAILED: abort did not remove drain marker")
            ok = False

        messages = []
        if abort(deps, messages.append) != 0:
            print("  FAILED: secondary abort failed")
            ok = False

    return ok


def _selftest_marker_filename_matches_the_cli() -> bool:
    """F7 (#1670 review): reinstates the cross-check the deleted drain-scan selftest used to run.
    DRAIN_MARKER_FILENAME/ABORT_INVOCATION are transcriptions of BatonPaths.DrainMarkerFileName /
    DrainMarker.AbortInvocation -- a mismatch is silent (refresh.py would write/name a marker no
    verb reads or a recovery command that doesn't work), and nothing else catches a future rename on
    the C# side. Reads this repo's own real source files, not a fixture -- there is nothing to fake
    a cross-check against."""
    ok = True
    props_path = os.path.join(default_repo_root(), BATON_PATHS_RELATIVE_PATH)
    try:
        with open(props_path, "r", encoding="utf-8") as f:
            text = f.read()
    except OSError as exc:
        print(f"  FAILED: could not read {props_path} to check the marker filename ({exc})")
        return False

    match = DRAIN_MARKER_CONST.search(text)
    if match is None:
        print(f"  FAILED: no DrainMarkerFileName constant found in {BATON_PATHS_RELATIVE_PATH}")
        return False
    if match.group("name") != DRAIN_MARKER_FILENAME:
        print(
            f"  FAILED: this tool writes {DRAIN_MARKER_FILENAME!r} but the CLI reads "
            f"{match.group('name')!r} -- the two halves of the drain would not meet"
        )
        ok = False

    marker_type_path = os.path.join(default_repo_root(), DRAIN_MARKER_TYPE_RELATIVE_PATH)
    try:
        with open(marker_type_path, "r", encoding="utf-8") as f:
            marker_type_text = f.read()
    except OSError as exc:
        print(f"  FAILED: could not read {marker_type_path} to check the abort invocation ({exc})")
        return False

    abort_match = ABORT_INVOCATION_CONST.search(marker_type_text)
    if abort_match is None:
        print(f"  FAILED: no AbortInvocation constant found in {DRAIN_MARKER_TYPE_RELATIVE_PATH}")
        return False
    if abort_match.group("invocation") != ABORT_INVOCATION:
        print(
            f"  FAILED: every refusal tells the operator to run "
            f"{abort_match.group('invocation')!r}, which is not this tool's own {ABORT_INVOCATION!r}"
        )
        ok = False

    return ok


def _selftest_rerefresh_skips_reinstall_when_sha_already_verified() -> bool:
    """F4 (#1670 review): a same-head re-refresh of an already-installed, already-verified, LIVE
    SHA must be a no-op for that directory -- never rmtree it, never reinstall into it."""
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        baton_home = os.path.join(td, "baton")
        tools_root = os.path.join(baton_home, "tools")
        rooms_root = os.path.join(baton_home, "rooms")
        dotnet_tools_root = os.path.join(td, "dotnet_tools")
        nuget_root = os.path.join(td, "nuget")
        repo_root = _fixture_repo(os.path.join(td, "repo"), "7.7.7")

        install_calls: List[List[str]] = []
        daemon_state = {"running": True, "pid": 4343}

        def run(cmd: List[str]) -> CommandResult:
            if cmd[:3] == ["git", "-C", repo_root] and cmd[3:5] == ["rev-parse", "--short"]:
                return CommandResult(0, "deadbeef\n")
            if cmd[:3] == ["pixi", "run", "pack"]:
                return CommandResult(0)
            if cmd[:3] == ["dotnet", "tool", "list"]:
                return CommandResult(0, "")
            if cmd[:4] == ["dotnet", "tool", "install", "baton"]:
                install_calls.append(cmd)
                install_dir = cmd[cmd.index("--tool-path") + 1]
                os.makedirs(install_dir, exist_ok=True)
                target_exe = os.path.join(install_dir, "baton.exe" if (sys.platform == "win32" or os.name == "nt") else "baton")
                open(target_exe, "w").close()
                return CommandResult(0)
            if len(cmd) == 2 and cmd[1] == "--version":
                return CommandResult(0, "7.7.7\n")
            if len(cmd) == 3 and cmd[1:] == ["templates", "--json"]:
                return CommandResult(0, "[]\n")
            if cmd[:3] == ["dotnet", "build", "src/Baton.Cli"]:
                return CommandResult(0)
            if cmd[0] == "powershell" and "Get-ScheduledTask" in cmd[3]:
                return CommandResult(0, "BATON_TASK_STATE=Ready\n")
            if cmd[0] == "powershell" and "Win32_Process" in cmd[3]:
                exe_path = os.path.join(tools_root, "deadbeef", "baton.exe")
                return CommandResult(0, f"{daemon_state['pid']}|2026-01-01T00:00:00.000Z|{exe_path}\n" if daemon_state["running"] else "")
            if cmd[0] == "powershell" and "Get-Process -Id" in cmd[3]:
                return CommandResult(0, f"{daemon_state['pid']}\n" if daemon_state["running"] else "")
            if cmd[0] == "powershell" and "Stop-ScheduledTask" in cmd[3]:
                daemon_state["running"] = False
                return CommandResult(0)
            if cmd[0] == "powershell" and "Start-ScheduledTask" in cmd[3]:
                daemon_state["pid"] += 1
                daemon_state["running"] = True
                _write_test_heartbeat(
                    baton_home,
                    identity=DaemonIdentity(
                        daemon_state["pid"],
                        "2026-01-01T00:00:00.000Z",
                        os.path.join(tools_root, "deadbeef", "baton.exe"),
                        "7.7.7",
                    ),
                )
                return CommandResult(0)
            if cmd[0] == "powershell":
                return CommandResult(0)
            raise AssertionError(f"unexpected command in re-refresh selftest: {cmd}")

        deps = Deps(
            run=run, baton_home=baton_home, rooms_root=rooms_root,
            tools_root=tools_root, dotnet_tools_root=dotnet_tools_root,
            nuget_packages_root=nuget_root, repo_root=repo_root
        )
        _assert_isolated(deps)

        messages: List[str] = []
        if refresh(deps, dry_run=False, print_fn=messages.append) != 0:
            print(f"  FAILED: first refresh did not exit 0. Messages: {messages}")
            ok = False

        # A live room now references this SHA, as dispatch would have recorded after the first refresh.
        _make_room(rooms_root, "live-room", terminal=False, tool_sha="deadbeef")

        tool_dir = os.path.join(tools_root, "deadbeef")
        marker = os.path.join(tool_dir, "marker.txt")
        with open(marker, "w", encoding="utf-8") as f:
            f.write("byte-identical-sentinel")

        messages2: List[str] = []
        if refresh(deps, dry_run=False, print_fn=messages2.append) != 0:
            print(f"  FAILED: second (same-head, live) refresh did not exit 0. Messages: {messages2}")
            ok = False

        if len(install_calls) != 1:
            print(f"  FAILED: 'dotnet tool install' ran {len(install_calls)} times, want 1 -- re-refresh must skip reinstall")
            ok = False

        if not os.path.isfile(marker):
            print("  FAILED: re-refresh touched/removed the live tool directory -- marker.txt is gone")
            ok = False

        current_file = os.path.join(tools_root, "current")
        with open(current_file, "r", encoding="utf-8") as f:
            if f.read().strip() != "deadbeef":
                print("  FAILED: current pointer is not 'deadbeef' after the idempotent re-refresh")
                ok = False

    return ok


def _selftest_rerefresh_sidepaths_when_live_and_broken() -> bool:
    """F4 (#1670 review): if the existing tool_dir for this SHA fails verify AND is live (current or
    a room's ToolSha), refresh must install into a fresh `<sha>-<n>` side path rather than rmtree the
    directory a lane may be running from."""
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        baton_home = os.path.join(td, "baton")
        tools_root = os.path.join(baton_home, "tools")
        rooms_root = os.path.join(baton_home, "rooms")
        dotnet_tools_root = os.path.join(td, "dotnet_tools")
        nuget_root = os.path.join(td, "nuget")
        repo_root = _fixture_repo(os.path.join(td, "repo"), "8.8.8")

        os.makedirs(tools_root, exist_ok=True)
        broken_dir = os.path.join(tools_root, "broken01")
        os.makedirs(broken_dir, exist_ok=True)
        with open(os.path.join(broken_dir, "marker.txt"), "w", encoding="utf-8") as f:
            f.write("must-survive")
        with open(os.path.join(tools_root, "current"), "w", encoding="utf-8") as f:
            f.write("broken01\n")

        install_calls: List[List[str]] = []
        installed_dirs: List[str] = []
        daemon_state = {"running": False, "pid": 4444}

        def run(cmd: List[str]) -> CommandResult:
            if cmd[:3] == ["git", "-C", repo_root] and cmd[3:5] == ["rev-parse", "--short"]:
                return CommandResult(0, "broken01\n")
            if cmd[:3] == ["pixi", "run", "pack"]:
                return CommandResult(0)
            if cmd[:3] == ["dotnet", "tool", "list"]:
                return CommandResult(0, "")
            if cmd[:4] == ["dotnet", "tool", "install", "baton"]:
                install_calls.append(cmd)
                install_dir = cmd[cmd.index("--tool-path") + 1]
                installed_dirs.append(install_dir)
                os.makedirs(install_dir, exist_ok=True)
                target_exe = os.path.join(install_dir, "baton.exe" if (sys.platform == "win32" or os.name == "nt") else "baton")
                open(target_exe, "w").close()
                return CommandResult(0)
            if len(cmd) == 2 and cmd[1] == "--version":
                # broken01's own exe is a stub with nothing behind it -- only a freshly-installed
                # exe (under a side path) reports the right version.
                if os.path.normcase(os.path.dirname(cmd[0])) == os.path.normcase(broken_dir):
                    return CommandResult(1, "", "not a real exe")
                return CommandResult(0, "8.8.8\n")
            if len(cmd) == 3 and cmd[1:] == ["templates", "--json"]:
                return CommandResult(0, "[]\n")
            if cmd[:3] == ["dotnet", "build", "src/Baton.Cli"]:
                return CommandResult(0)
            if cmd[0] == "powershell" and "Get-ScheduledTask" in cmd[3]:
                return CommandResult(0, "BATON_TASK_STATE=Ready\n")
            if cmd[0] == "powershell" and "Win32_Process" in cmd[3]:
                exe_path = os.path.join(installed_dirs[-1], "baton.exe") if installed_dirs else ""
                return CommandResult(0, f"{daemon_state['pid']}|2026-01-01T00:00:00.000Z|{exe_path}\n" if exe_path and daemon_state["running"] else "")
            if cmd[0] == "powershell" and "Get-Process -Id" in cmd[3]:
                return CommandResult(0, f"{daemon_state['pid']}\n" if daemon_state["running"] else "")
            if cmd[0] == "powershell" and "Stop-ScheduledTask" in cmd[3]:
                daemon_state["running"] = False
                return CommandResult(0)
            if cmd[0] == "powershell" and "Start-ScheduledTask" in cmd[3]:
                daemon_state["pid"] += 1
                daemon_state["running"] = True
                _write_test_heartbeat(
                    baton_home,
                    identity=DaemonIdentity(
                        daemon_state["pid"],
                        "2026-01-01T00:00:00.000Z",
                        os.path.join(installed_dirs[-1], "baton.exe"),
                        "8.8.8",
                    ),
                )
                return CommandResult(0)
            if cmd[0] == "powershell":
                return CommandResult(0)
            raise AssertionError(f"unexpected command in side-path selftest: {cmd}")

        deps = Deps(
            run=run, baton_home=baton_home, rooms_root=rooms_root,
            tools_root=tools_root, dotnet_tools_root=dotnet_tools_root,
            nuget_packages_root=nuget_root, repo_root=repo_root
        )
        _assert_isolated(deps)

        messages: List[str] = []
        code = refresh(deps, dry_run=False, print_fn=messages.append)
        if code != 0:
            print(f"  FAILED: refresh did not exit 0. Messages: {messages}")
            ok = False

        if not os.path.isfile(os.path.join(broken_dir, "marker.txt")):
            print("  FAILED: the live-but-broken directory was touched (marker.txt gone) instead of side-pathed")
            ok = False

        side_path = os.path.join(tools_root, "broken01-1")
        if not os.path.isdir(side_path):
            print(f"  FAILED: expected a side-installed directory at {side_path}, found none")
            ok = False
        elif not any(cmd[cmd.index("--tool-path") + 1] == side_path for cmd in install_calls if "--tool-path" in cmd):
            print(f"  FAILED: 'dotnet tool install' was never targeted at {side_path}")
            ok = False

        current_file = os.path.join(tools_root, "current")
        with open(current_file, "r", encoding="utf-8") as f:
            if f.read().strip() != "broken01-1":
                print("  FAILED: current pointer was not flipped to the side-installed directory")
                ok = False

    return ok


def _selftest_install_launcher_fails_closed_on_stale_exe() -> bool:
    """F5 (#1670 review): install_launcher must fail closed, naming the holder, if baton.exe is
    still present in the launcher directory after the uninstall/remove/rename fallback chain --
    PATHEXT resolves .exe before .cmd/.ps1, so a stale shim would silently shadow the launcher."""
    import tempfile

    ok = True
    with tempfile.TemporaryDirectory() as td:
        dotnet_tools_root = os.path.join(td, "dotnet_tools")
        os.makedirs(dotnet_tools_root, exist_ok=True)
        stale_exe = os.path.join(dotnet_tools_root, "baton.exe")
        with open(stale_exe, "w", encoding="utf-8") as f:
            f.write("stale global-tool shim")

        def run(cmd: List[str]) -> CommandResult:
            if cmd[:3] == ["dotnet", "tool", "list"]:
                return CommandResult(0, "Package Id      Version\nbaton           1.0.0\n")
            if cmd[:4] == ["dotnet", "tool", "uninstall", "--global"]:
                # Simulate the uninstall failing while something still holds baton.exe open, and the
                # remove/rename fallback ALSO failing (both raise inside install_launcher naturally
                # if the file is genuinely locked; here we monkeypatch os.remove/os.rename instead).
                return CommandResult(1, "", "process cannot access the file")
            raise AssertionError(f"unexpected command: {cmd}")

        real_remove = os.remove
        real_rename = os.rename

        def locked_remove(path):
            if os.path.normcase(path) == os.path.normcase(stale_exe):
                raise PermissionError("locked")
            return real_remove(path)

        def locked_rename(src, dst):
            if os.path.normcase(src) == os.path.normcase(stale_exe):
                raise PermissionError("locked")
            return real_rename(src, dst)

        deps = Deps(run=run, dotnet_tools_root=dotnet_tools_root, repo_root=td,
                    baton_home=td, rooms_root=os.path.join(td, "rooms"),
                    tools_root=os.path.join(td, "tools"), nuget_packages_root=os.path.join(td, "nuget"))
        _assert_isolated(deps)

        os.remove, os.rename = locked_remove, locked_rename
        try:
            messages: List[str] = []
            result = install_launcher(deps, dry_run=False, print_fn=messages.append)
        finally:
            os.remove, os.rename = real_remove, real_rename

        if result is not False:
            print("  FAILED: install_launcher returned truthy despite a stale baton.exe still present")
            ok = False
        if not any(stale_exe in m for m in messages):
            print(f"  FAILED: install_launcher's failure message did not name the holder path {stale_exe}. Messages: {messages}")
            ok = False

    return ok


def _selftest_daemon_identity_and_verify() -> bool:
    """Identity capture, delayed old exit, and replacement path/version/health acceptance."""
    ok = True

    query_count = {"n": 0}

    def run_delayed_exit(cmd: List[str]) -> CommandResult:
        if cmd[0] == "powershell" and "Win32_Process" in cmd[3]:
            query_count["n"] += 1
            if query_count["n"] < 3:
                return CommandResult(0, "4321|legacy-selftest|C:\\baton\\tools\\oldsha\\baton.exe\n")
            return CommandResult(0, "")
        if cmd[0] == "powershell" and "Get-Process -Id" in cmd[3]:
            query_count["n"] += 1
            return CommandResult(0, "4321\n" if query_count["n"] < 3 else "")
        if len(cmd) == 2 and cmd[1] == "--version":
            return CommandResult(0, "1.0.0\n")
        raise AssertionError(f"unexpected command: {cmd}")

    deps = Deps(run=run_delayed_exit, sleep=lambda _seconds: None)
    messages: List[str] = []
    identities = capture_daemon_identities(deps)
    if identities != [DaemonIdentity(4321, "legacy-selftest", "C:\\baton\\tools\\oldsha\\baton.exe", "1.0.0")]:
        print(f"  FAILED: pre-restart identity capture returned {identities!r}")
        ok = False
    if wait_for_daemon_exit(deps, identities[0], messages.append) != DaemonExitState.EXITED:
        print("  FAILED: delayed old daemon exit was not observed")
        ok = False

    def run_wrong_dir(cmd: List[str]) -> CommandResult:
        if cmd[0] == "powershell" and "Win32_Process" in cmd[3]:
            return CommandResult(0, "9999|C:\\baton\\tools\\othersha\\baton.exe\n")
        if len(cmd) == 2 and cmd[1] == "--version":
            return CommandResult(0, "2.0.0\n")
        if cmd[0] == "powershell" and "Get-Process -Id" in cmd[3]:
            return CommandResult(0, "9999\n")
        raise AssertionError(f"unexpected command: {cmd}")

    deps2 = Deps(run=run_wrong_dir, sleep=lambda s: None)
    if verify_daemon_started_under(deps2, os.path.join("C:\\", "baton", "tools", "newsha"), lambda m: None):
        print("  FAILED: verify_daemon_started_under accepted a process under a different tool_dir")
        ok = False

    def run_right_dir(cmd: List[str]) -> CommandResult:
        if cmd[0] == "powershell" and "Win32_Process" in cmd[3]:
            return CommandResult(0, "8888|C:\\baton\\tools\\newsha\\baton.exe\n")
        if len(cmd) == 2 and cmd[1] == "--version":
            return CommandResult(0, "2.0.0\n")
        if cmd[0] == "powershell" and "Get-Process -Id" in cmd[3]:
            return CommandResult(0, "8888\n")
        raise AssertionError(f"unexpected command: {cmd}")

    deps3 = Deps(run=run_right_dir, sleep=lambda s: None)
    if not verify_daemon_started_under(deps3, os.path.join("C:\\", "baton", "tools", "newsha"), lambda m: None):
        print("  FAILED: verify_daemon_started_under rejected a process actually under tool_dir")
        ok = False

    return ok


def _selftest_daemon_task_absent_or_disabled_skips_restart_and_verify() -> bool:
    """An absent or disabled task gets installation-only success; no active daemon is claimed."""
    import tempfile

    ok = True
    for label, task_stdout in [
        ("absent", "BATON_TASK_NOT_FOUND\n"),
        ("Disabled", "BATON_TASK_STATE=Disabled\n"),
    ]:
        with tempfile.TemporaryDirectory() as td:
            baton_home = os.path.join(td, "baton")
            tools_root = os.path.join(baton_home, "tools")
            rooms_root = os.path.join(baton_home, "rooms")
            dotnet_tools_root = os.path.join(td, "dotnet_tools")
            nuget_root = os.path.join(td, "nuget")
            repo_root = _fixture_repo(os.path.join(td, "repo"), "3.3.3")

            commands_run: List[List[str]] = []

            def run(cmd: List[str], task_stdout: str = task_stdout) -> CommandResult:
                commands_run.append(cmd)
                if cmd[:3] == ["git", "-C", repo_root] and cmd[3:5] == ["rev-parse", "--short"]:
                    return CommandResult(0, "3a3a3a3\n")
                if cmd[:3] == ["pixi", "run", "pack"]:
                    return CommandResult(0)
                if cmd[:3] == ["dotnet", "tool", "list"]:
                    return CommandResult(0, "")
                if cmd[:4] == ["dotnet", "tool", "install", "baton"]:
                    tool_dir = os.path.join(tools_root, "3a3a3a3")
                    os.makedirs(tool_dir, exist_ok=True)
                    target_exe = os.path.join(tool_dir, "baton.exe" if (sys.platform == "win32" or os.name == "nt") else "baton")
                    open(target_exe, "w").close()
                    return CommandResult(0)
                if len(cmd) == 2 and cmd[1] == "--version":
                    return CommandResult(0, "3.3.3\n")
                if len(cmd) == 3 and cmd[1:] == ["templates", "--json"]:
                    return CommandResult(0, "[]\n")
                if cmd[:3] == ["dotnet", "build", "src/Baton.Cli"]:
                    return CommandResult(0)
                if cmd[0] == "powershell" and "Get-ScheduledTask" in cmd[3]:
                    return CommandResult(0, task_stdout)
                if cmd[0] == "powershell" and "Win32_Process" in cmd[3]:
                    return CommandResult(0, "")
                if cmd[0] == "powershell":
                    return CommandResult(0)
                raise AssertionError(f"unexpected command in daemon-task-{label} selftest: {cmd}")

            deps = Deps(
                run=run, baton_home=baton_home, rooms_root=rooms_root,
                tools_root=tools_root, dotnet_tools_root=dotnet_tools_root,
                nuget_packages_root=nuget_root, repo_root=repo_root
            )
            _assert_isolated(deps)

            messages: List[str] = []
            code = refresh(deps, dry_run=False, print_fn=messages.append)
            if code != 0:
                print(f"  FAILED ({label}): refresh exited {code}, want 0. Messages: {messages}")
                ok = False

            powershell_cmds = [" ".join(c) for c in commands_run if c and c[0] == "powershell"]
            if any("Start-ScheduledTask -TaskName baton-daemon" in c for c in powershell_cmds):
                print(f"  FAILED ({label}): baton-daemon restart was attempted despite the task being {label}")
                ok = False
            if not any(f"baton-daemon scheduled task is {label.casefold()}" in m.casefold() for m in messages):
                print(f"  FAILED ({label}): no skip message naming the task state was printed. Messages: {messages}")
                ok = False
            if any("Win32_Process" in c for c in powershell_cmds):
                print(f"  FAILED ({label}): daemon identity query ran despite the task being skipped. powershell commands: {powershell_cmds}")
                ok = False

    return ok


def _selftest_daemon_restart_failure_modes() -> bool:
    """Covers bounded old-exit timeout and rejection of same-PID, wrong-path, and unhealthy replacements."""
    ok = True
    old_path = "C:\\baton\\tools\\oldsha\\baton.exe"
    new_path = "C:\\baton\\tools\\newsha\\baton.exe"
    old_identity = DaemonIdentity(7001, "legacy-selftest", old_path, "1.0.0")

    def run_old_stuck(cmd: List[str]) -> CommandResult:
        if cmd[0] == "powershell" and "Win32_Process" in cmd[3]:
            return CommandResult(0, f"7001|legacy-selftest|{old_path}\n")
        if cmd[0] == "powershell" and "Get-Process -Id" in cmd[3]:
            return CommandResult(0, "7001\n")
        if len(cmd) == 2 and cmd[1] == "--version":
            return CommandResult(0, "1.0.0\n")
        raise AssertionError(f"unexpected command in old-exit-timeout selftest: {cmd}")

    if wait_for_daemon_exit(Deps(run=run_old_stuck, sleep=lambda _seconds: None), old_identity, lambda _m: None) != DaemonExitState.TIMEOUT:
        print("  FAILED: old-exit timeout accepted a still-live old PID")
        ok = False

    def run_replacement(cmd: List[str], health: bool = True, pid: int = 7002, path: str = new_path) -> CommandResult:
        if cmd[0] == "powershell" and "Win32_Process" in cmd[3]:
            return CommandResult(0, f"{pid}|legacy-selftest|{path}\n")
        if len(cmd) == 2 and cmd[1] == "--version":
            return CommandResult(0, "2.0.0\n")
        if cmd[0] == "powershell" and "Get-Process -Id" in cmd[3]:
            return CommandResult(0, f"{pid}\n" if health else "")
        raise AssertionError(f"unexpected command in replacement selftest: {cmd}")

    replacement_deps = Deps(run=run_replacement, sleep=lambda _seconds: None)
    if verify_daemon_replacement(replacement_deps, "C:\\baton\\tools\\newsha", "2.0.0", old_identity, lambda _m: None, retries=1) is None:
        print("  FAILED: healthy replacement was rejected in failure-mode selftest")
        ok = False
    if verify_daemon_replacement(
        Deps(run=lambda cmd: run_replacement(cmd, pid=7001), sleep=lambda _seconds: None),
        "C:\\baton\\tools\\newsha", "2.0.0", old_identity, lambda _m: None, retries=1
    ) is not None:
        print("  FAILED: same-PID replacement was accepted")
        ok = False
    if verify_daemon_replacement(
        Deps(run=lambda cmd: run_replacement(cmd, health=False), sleep=lambda _seconds: None),
        "C:\\baton\\tools\\newsha", "2.0.0", old_identity, lambda _m: None, retries=1
    ) is not None:
        print("  FAILED: unhealthy replacement was accepted")
        ok = False
    if verify_daemon_replacement(
        Deps(run=lambda cmd: run_replacement(cmd, path="C:\\baton\\tools\\othersha\\baton.exe"), sleep=lambda _seconds: None),
        "C:\\baton\\tools\\newsha", "2.0.0", old_identity, lambda _m: None, retries=1
    ) is not None:
        print("  FAILED: wrong-path replacement was accepted")
        ok = False

    return ok


def _selftest_full_refresh_daemon_outcomes() -> bool:
    """Exercise full-refresh command order, bounded starts, messaging, and pointer state."""
    from collections import Counter
    import tempfile

    task_and_identity = [
        "task-query",
        "identity-process", "identity-version", "identity-process", "identity-version",
    ]
    stop_and_old_exit = ["stop", "old-exit-process"]
    healthy_replacement = [
        "replacement-process", "replacement-version",
        "replacement-process", "replacement-version",
        "replacement-version", "replacement-process", "replacement-version",
    ]
    active_prefix = task_and_identity + stop_and_old_exit

    def trace_matches(actual: List[str], expected: List[str]) -> bool:
        return actual == expected

    cases = [
        (
            "normal restart", "normal", 1, True, "active",
            active_prefix + ["start"] + healthy_replacement,
        ),
        (
            "swallowed first start", "swallowed", 2, True, "active",
            active_prefix + ["start"] + ["replacement-process"] * 15
            + ["start"] + healthy_replacement,
        ),
        (
            "PID reuse between probes", "pid-reuse", 1, False, "identity-unstable",
            active_prefix + [
                "start", "replacement-process", "replacement-version",
                "replacement-process", "replacement-version",
            ],
        ),
        (
            "duplicate daemon rows", "duplicate", 1, False, "duplicate",
            active_prefix + ["start", "replacement-process"],
        ),
        (
            "version mismatch", "version-mismatch", 1, False, "version-mismatch",
            active_prefix + [
                "start", "replacement-process", "replacement-version",
                "replacement-version",
            ],
        ),
        (
            "wrong path", "wrong-path", 1, False, "wrong-path",
            active_prefix + ["start", "replacement-process"],
        ),
        (
            "unhealthy candidate", "unhealthy", 1, False, "unhealthy",
            active_prefix + ["start"] + [
                "replacement-process", "replacement-version",
                "replacement-process", "replacement-version",
            ] * 15,
        ),
        (
            "nonzero start", "nonzero-start", 1, False, "start attempt 1 failed",
            active_prefix + ["start"],
        ),
        (
            "query failure", "query-failure", 0, False, "could not query the baton-daemon",
            ["task-query"],
        ),
        (
            "absent task", "absent", 0, True, "installation-only success",
            ["task-query"],
        ),
        (
            "disabled task", "disabled", 0, True, "installation-only success",
            ["task-query"],
        ),
        (
            "old-exit timeout", "old-exit-timeout", 0, False, "Refusing to start or accept",
            task_and_identity + ["stop"] + ["old-exit-process", "old-exit-version"] * 15,
        ),
    ]
    ok = True
    for label, outcome, expected_starts, expected_success, expected_text, expected_trace in cases:
        with tempfile.TemporaryDirectory() as td:
            baton_home = os.path.join(td, "baton")
            tools_root = os.path.join(baton_home, "tools")
            rooms_root = os.path.join(baton_home, "rooms")
            dotnet_tools_root = os.path.join(td, "dotnet_tools")
            nuget_root = os.path.join(td, "nuget")
            repo_root = _fixture_repo(os.path.join(td, "repo"), "1.0.0")
            old_path = os.path.join(tools_root, "oldsha", "baton.exe")
            new_path = os.path.join(tools_root, "newsha1", "baton.exe")
            old_creation = "2026-01-01T00:00:00+00:00"
            new_creation = "2026-01-01T00:01:00+00:00"
            state = {"phase": "old", "starts": 0, "process_queries": 0, "new_version_calls": 0}
            commands: List[List[str]] = []
            daemon_trace: List[str] = []
            stop_requested = False

            def process_rows() -> str:
                if state["phase"] == "old":
                    return f"100|{old_creation}|{old_path}\n"
                if state["phase"] == "stopped":
                    return ""
                if outcome == "duplicate":
                    return f"200|{new_creation}|{new_path}\n201|{new_creation}|{new_path}\n"
                if outcome == "wrong-path":
                    return f"200|{new_creation}|{old_path}\n"
                if outcome == "pid-reuse":
                    if state["process_queries"] % 2 == 0:
                        return f"100|{new_creation}|{new_path}\n"
                    return f"100|2026-01-01T00:02:00+00:00|{new_path}\n"
                return f"200|{new_creation}|{new_path}\n"

            def run(cmd: List[str]) -> CommandResult:
                nonlocal stop_requested
                commands.append(cmd)
                if cmd[:3] == ["git", "-C", repo_root] and cmd[3:5] == ["rev-parse", "--short"]:
                    return CommandResult(0, "newsha1\n")
                if cmd[:3] == ["pixi", "run", "pack"]:
                    return CommandResult(0)
                if cmd[:3] == ["dotnet", "tool", "list"]:
                    return CommandResult(0, "")
                if cmd[:4] == ["dotnet", "tool", "install", "baton"]:
                    install_dir = cmd[cmd.index("--tool-path") + 1]
                    os.makedirs(install_dir, exist_ok=True)
                    open(os.path.join(install_dir, "baton.exe"), "w").close()
                    return CommandResult(0)
                if len(cmd) == 2 and cmd[1] == "--version":
                    normalized_executable = os.path.normcase(cmd[0])
                    if normalized_executable == os.path.normcase(old_path):
                        daemon_trace.append(
                            "old-exit-version" if stop_requested else "identity-version"
                        )
                    elif state["starts"] and normalized_executable == os.path.normcase(new_path):
                        daemon_trace.append("replacement-version")
                    if os.path.normcase(cmd[0]) == os.path.normcase(new_path):
                        state["new_version_calls"] += 1
                    if (os.path.normcase(cmd[0]) == os.path.normcase(new_path)
                            and outcome == "version-mismatch" and state["new_version_calls"] > 1):
                        return CommandResult(0, "9.9.9\n")
                    return CommandResult(0, "1.0.0\n")
                if len(cmd) == 3 and cmd[1:] == ["templates", "--json"]:
                    return CommandResult(0, "[]\n")
                if cmd[0] == "powershell" and "Get-ScheduledTask" in cmd[3]:
                    daemon_trace.append("task-query")
                    if outcome == "query-failure":
                        return CommandResult(1, "", "query failed")
                    task_state = {
                        "absent": "BATON_TASK_NOT_FOUND\n",
                        "disabled": "BATON_TASK_STATE=Disabled\n",
                    }.get(outcome, "BATON_TASK_STATE=Ready\n")
                    return CommandResult(0, task_state)
                if cmd[0] == "powershell" and "Win32_Process" in cmd[3]:
                    daemon_trace.append(
                        "replacement-process" if state["starts"] else (
                            "old-exit-process" if stop_requested else "identity-process"
                        )
                    )
                    state["process_queries"] += 1
                    return CommandResult(0, process_rows())
                if cmd[0] == "powershell" and "Stop-ScheduledTask" in cmd[3]:
                    daemon_trace.append("stop")
                    stop_requested = True
                    if outcome != "old-exit-timeout":
                        state["phase"] = "stopped"
                    return CommandResult(0)
                if cmd[0] == "powershell" and "Start-ScheduledTask" in cmd[3]:
                    daemon_trace.append("start")
                    state["starts"] += 1
                    if outcome == "nonzero-start":
                        return CommandResult(5, "", "start failed")
                    if outcome == "swallowed" and state["starts"] == 1:
                        state["phase"] = "stopped"
                    else:
                        state["phase"] = "new"
                        if outcome != "unhealthy":
                            _write_test_heartbeat(
                                baton_home,
                                new_creation,
                                DaemonIdentity(200, new_creation, new_path, "1.0.0"),
                            )
                    return CommandResult(0)
                if cmd[0] == "powershell":
                    return CommandResult(0)
                raise AssertionError(f"unexpected command in {label} selftest: {cmd}")

            deps = Deps(
                run=run, baton_home=baton_home, rooms_root=rooms_root,
                tools_root=tools_root, dotnet_tools_root=dotnet_tools_root,
                nuget_packages_root=nuget_root, repo_root=repo_root,
                sleep=lambda _seconds: None,
            )
            _assert_isolated(deps)
            messages: List[str] = []
            code = refresh(deps, dry_run=False, print_fn=messages.append)
            joined = "\n".join(messages)
            pointer_path = os.path.join(tools_root, "current")
            pointer = open(pointer_path, encoding="utf-8").read().strip() if os.path.isfile(pointer_path) else None
            if (code == 0) != expected_success:
                print(f"  FAILED ({label}): expected failure, got success. Messages: {messages}")
                ok = False
            if code != 0 and expected_success:
                print(f"  FAILED ({label}): expected success, got {code}. Messages: {messages}")
                ok = False
            if state["starts"] != expected_starts:
                print(f"  FAILED ({label}): start count {state['starts']}, want {expected_starts}")
                ok = False
            if expected_success and pointer != "newsha1":
                print(f"  FAILED ({label}): final pointer {pointer!r}, want 'newsha1'")
                ok = False
            if not expected_success and pointer != "newsha1":
                print(f"  FAILED ({label}): failure did not leave the installed pointer at 'newsha1': {pointer!r}")
                ok = False
            if expected_text.casefold() not in joined.casefold():
                print(f"  FAILED ({label}): missing final classification {expected_text!r}. Messages: {messages}")
                ok = False
            if not trace_matches(daemon_trace, expected_trace):
                print(f"  FAILED ({label}): daemon command trace {daemon_trace!r}, want {expected_trace!r}")
                ok = False
            if Counter(daemon_trace) != Counter(expected_trace):
                print(
                    f"  FAILED ({label}): daemon command counts {Counter(daemon_trace)!r}, "
                    f"want {Counter(expected_trace)!r}"
                )
                ok = False
            if outcome in {"absent", "disabled", "query-failure"} and any(
                event in daemon_trace for event in ("start", "stop")
            ):
                print(f"  FAILED ({label}): installation-only/refusal path ran start or stop: {daemon_trace!r}")
                ok = False
            if outcome == "old-exit-timeout" and "start" in daemon_trace:
                print(f"  FAILED ({label}): old-exit timeout still ran a start: {daemon_trace!r}")
                ok = False
            if outcome == "nonzero-start" and state["process_queries"] != 3:
                print(f"  FAILED ({label}): nonzero start ran replacement probes: {state}")
                ok = False

    normal_trace = cases[0][-1]
    swapped_trace = normal_trace.copy()
    stop_index = swapped_trace.index("stop")
    start_index = swapped_trace.index("start")
    swapped_trace[stop_index], swapped_trace[start_index] = swapped_trace[start_index], swapped_trace[stop_index]
    if trace_matches(swapped_trace, normal_trace):
        print("  FAILED: exact command trace accepted a swapped start/stop trace")
        ok = False
    return ok


def _selftest_daemon_query_matches_verb_position_not_anywhere() -> bool:
    """#1777 fix round F2: `-like '*daemon*'` matched the substring anywhere on the command line, so
    a `baton dispatch --spec-text "... daemon ..."` process would be mistaken for a daemon during
    refresh identity checks. The query must anchor on the verb position (first arg after the executable)."""
    ok = True
    query = daemon_process_query_cmd()[-1]
    if "*daemon*" in query:
        print(f"  FAILED: query still contains an unanchored '*daemon*' match: {query!r}")
        ok = False
    if "\\s+daemon(\\s|$)" not in query:
        print(f"  FAILED: query does not anchor 'daemon' at the verb position. query: {query!r}")
        ok = False
    return ok


def _selftest_daemon_query_serializes_invariant_creation_time() -> bool:
    """The process-query boundary must not emit PowerShell's locale-formatted CreationDate."""
    ok = True
    query = daemon_process_query_cmd()
    expected_format = (
        "CreationDate.ToUniversalTime().ToString('o', "
        "[System.Globalization.CultureInfo]::InvariantCulture)"
    )
    if expected_format not in query[3]:
        print(f"  FAILED: process query does not serialize CreationDate invariantly: {query[3]!r}")
        ok = False

    creation_time = "2026-09-27T08:27:27.2997980Z"
    executable_path = r"C:\baton\tools\new\baton.exe"
    calls: List[List[str]] = []

    def run(command: List[str]) -> CommandResult:
        calls.append(command)
        return CommandResult(0, f"134756|{creation_time}|{executable_path}\n")

    processes = query_daemon_processes(Deps(run=run))
    expected = [DaemonProcess(134756, creation_time, executable_path)]
    if processes != expected or calls != [query]:
        print(f"  FAILED: invariant query fixture parsed as {processes!r}, calls={calls!r}")
        ok = False
    return ok


def _selftest_timestamp_parsing() -> bool:
    """DMTF offsets must match ISO instants and malformed values must fail closed."""
    ok = True
    equivalent_pairs = [
        ("positive DMTF offset", "20260926155800.299798+060", "2026-09-26T15:58:00.299798+01:00"),
        ("negative DMTF offset", "20260926155800.299798-060", "2026-09-26T15:58:00.299798-01:00"),
    ]
    for label, dmtf, iso in equivalent_pairs:
        try:
            parsed = _parse_timestamp(dmtf)
            canonical = _canonical_timestamp(dmtf)
        except (TypeError, ValueError, OverflowError) as exc:
            print(f"  FAILED ({label}): valid DMTF timestamp raised {exc!r}")
            ok = False
            continue
        if parsed != _parse_timestamp(iso) or canonical != _canonical_timestamp(iso):
            print(f"  FAILED ({label}): DMTF and ISO timestamps did not resolve to the same instant")
            ok = False

    malformed = [
        "20261326155800.299798+060",
        "20260926155800.299798+999",
        "20260926155800.bad+060",
        "9/27/2026 4:27:27 AM",
    ]
    for value in malformed:
        try:
            parsed = _parse_timestamp(value)
            canonical = _canonical_timestamp(value)
        except (TypeError, ValueError, OverflowError) as exc:
            print(f"  FAILED ({value!r}): malformed timestamp raised {exc!r}")
            ok = False
            continue
        if parsed is not None or canonical is not None:
            print(f"  FAILED ({value!r}): malformed timestamp was accepted")
            ok = False
    return ok


def _selftest_scheduler_protocol_and_heartbeat_identity() -> bool:
    """Cover the fail-closed scheduler protocol and every replacement-heartbeat polarity."""
    import tempfile

    ok = True
    expected_query = daemon_task_query_cmd()
    if "-ErrorAction Stop" not in expected_query[3] or "SilentlyContinue" in expected_query[3]:
        print(f"  FAILED: scheduled-task query is not an explicit fail-closed protocol: {expected_query}")
        ok = False

    scheduler_cases = [
        ("provider/access failure", CommandResult(1, TASK_ERROR_MARKER + "\n", "access denied"), DaemonTaskState.QUERY_FAILED),
        ("explicit absence", CommandResult(0, TASK_NOT_FOUND_MARKER + "\n"), DaemonTaskState.ABSENT),
        ("disabled", CommandResult(0, TASK_STATE_PREFIX + "Disabled\n"), DaemonTaskState.DISABLED),
        ("ready", CommandResult(0, TASK_STATE_PREFIX + "Ready\n"), DaemonTaskState.ACTIVE),
        ("unknown state", CommandResult(0, TASK_STATE_PREFIX + "Mystery\n"), DaemonTaskState.QUERY_FAILED),
        ("malformed", CommandResult(0, "Ready\n"), DaemonTaskState.QUERY_FAILED),
        ("multiple states", CommandResult(0, TASK_STATE_PREFIX + "Ready\n" + TASK_STATE_PREFIX + "Running\n"), DaemonTaskState.QUERY_FAILED),
        ("suppressed error", CommandResult(0, ""), DaemonTaskState.QUERY_FAILED),
    ]
    for label, result, expected in scheduler_cases:
        calls: List[List[str]] = []

        def run(_cmd: List[str], result: CommandResult = result) -> CommandResult:
            calls.append(_cmd)
            return result

        state = daemon_task_state(Deps(run=run, baton_home="scheduler-fixture"))
        if state != expected or calls != [expected_query]:
            print(f"  FAILED ({label}): state={state!r}, calls={calls!r}, want {expected!r} and one exact query")
            ok = False

    now = dt.datetime(2026, 9, 27, 8, 29, 27, tzinfo=dt.timezone.utc)
    candidate = DaemonIdentity(4243, "2026-09-27T08:27:27.2997980Z", r"C:\baton\tools\new\baton.exe", "2.0.0")
    base = {
        "startedAt": "2026-09-27T08:27:27.000Z",
        "tickCompletedAt": "2026-09-27T08:28:30.000Z",
        "identity": {
            "pid": candidate.pid,
            "processStartTime": "2026-09-27T08:27:27.2997983Z",
            "executablePath": candidate.executable_path,
            "version": candidate.version,
        },
        "services": {},
    }

    def probe(body: dict, probe_identity: DaemonIdentity = candidate) -> bool:
        with tempfile.TemporaryDirectory() as td:
            heartbeat_path = os.path.join(td, "fleet", "heartbeat.json")
            os.makedirs(os.path.dirname(heartbeat_path), exist_ok=True)
            with open(heartbeat_path, "w", encoding="utf-8") as f:
                json.dump(body, f)
            return daemon_health_probe(
                Deps(baton_home=td, clock=lambda: now.timestamp()), probe_identity
            )

    cases = [
        ("correct replacement heartbeat", base, True),
        ("old but recent heartbeat", {**base, "identity": {**base["identity"], "pid": 4242}}, False),
        ("stale heartbeat", {**base, "tickCompletedAt": "2026-09-27T08:26:59.000Z"}, False),
        ("future clock skew", {**base, "tickCompletedAt": "2026-09-27T08:29:33.000Z"}, False),
        ("malformed times", {**base, "startedAt": "not-a-time"}, False),
        ("candidate identity mismatch", {**base, "identity": {**base["identity"], "version": "1.0.0"}}, False),
        (
            "microsecond process identity mismatch",
            {**base, "identity": {**base["identity"], "processStartTime": "2026-09-27T08:27:27.2997990Z"}},
            False,
        ),
        ("invalid ordering", {**base, "startedAt": "2026-09-27T08:29:28.000Z"}, False),
        ("malformed identity time", {**base, "identity": {**base["identity"], "processStartTime": "bad"}}, False),
    ]
    for label, body, expected in cases:
        if probe(body) != expected:
            print(f"  FAILED ({label}): heartbeat acceptance polarity was wrong")
            ok = False
    locale_candidate = DaemonIdentity(
        candidate.pid, "9/27/2026 4:27:27 AM", candidate.executable_path, candidate.version
    )
    if probe(base, locale_candidate):
        print("  FAILED: locale-formatted process identity was accepted")
        ok = False
    return ok


def selftest() -> int:
    arms = [
        ("version compare", _selftest_version_compare),
        ("pointer flip atomic", _selftest_pointer_flip_atomic),
        ("pointer flip retries a transient PermissionError, fails closed on a persistent one", _selftest_pointer_flip_retries_transient_permission_error),
        ("prune logic", _selftest_prune_logic),
        ("refresh end-to-end mocked", _selftest_refresh_end_to_end_mocked),
        ("fail closed on verify failure", _selftest_fail_closed_on_verify_failure),
        ("dry-run touches nothing", _selftest_dry_run_touches_nothing),
        ("abort clears drain marker", _selftest_abort),
        ("the marker filename this tool writes is the one the CLI reads", _selftest_marker_filename_matches_the_cli),
        ("re-refresh skips reinstall when the SHA is already verified", _selftest_rerefresh_skips_reinstall_when_sha_already_verified),
        ("re-refresh side-paths when live and broken", _selftest_rerefresh_sidepaths_when_live_and_broken),
        ("install_launcher fails closed on a stale exe", _selftest_install_launcher_fails_closed_on_stale_exe),
        ("full refresh daemon outcomes are bounded and classified", _selftest_full_refresh_daemon_outcomes),
        ("daemon process query anchors on the verb position, not '*daemon*' anywhere", _selftest_daemon_query_matches_verb_position_not_anywhere),
        ("daemon process query serializes invariant UTC creation time", _selftest_daemon_query_serializes_invariant_creation_time),
        ("timestamp parsing is equivalent and fail closed", _selftest_timestamp_parsing),
        ("scheduler protocol and heartbeat identity are fail closed", _selftest_scheduler_protocol_and_heartbeat_identity),
    ]
    ok = True
    for name, fn in arms:
        print(f"selftest: {name}")
        if not fn():
            ok = False

    print("selftest: pass" if ok else "selftest: FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
