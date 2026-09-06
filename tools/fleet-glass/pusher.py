"""Fleet Glass pusher: derive the fleet snapshot via `baton mcp` (stdio MCP, #1458: folded from the
standalone Baton.Mcp.Host binary into a Baton.Cli verb) and scan ~/.baton/rooms
for terminal-room deliverables, then POST both outbound to the Cloudflare mailbox Worker (worker.js)
every ~25s. Moved into the repo, with the deliverables inbox added, by aer-works/baton#1413.

Outbound-only; the machine running this accepts no inbound connections.

THE SNAPSHOT HALF -- change-gated (#1457) and coalescing-floored (#1538)
-------------------------------------------------------------------------
The wrapped {rooms, underhood, timelines, stale_hidden_count} body is hashed (stable, sort_keys)
before every POST; a hash that matches the last SUCCESSFUL push's (persisted in push_state_file, key
SNAPSHOT_HASH_KEY) skips the POST. A missing/unreadable persisted hash always re-pushes (fail toward
one extra write, never toward silence, same posture as the deliverables state file below); a FAILED
POST never persists the hash, so the next cycle retries. See `snapshot_hash` / `should_push_snapshot`.

COALESCING FLOOR (#1538): when the change-gate says CHANGED, push only if >= min_push_interval_s
(default 90s, and adaptively widened by the write-budget ledger below) since the last actual push;
otherwise log `coalesced (Ns since last push)` and let the next cycle retry.

WRITE BUDGET LEDGER (#1690, split into per-producer sub-budgets and pacing by the 2026-09-02 review's
F1): the real KV-write cost per producer, each producer's own daily sub-budget, its own adaptive
cadence, and the exhaustion posture are all spec/baton.md §6's canonical record now (the "Fleet Glass
write budget" entry) -- read there once, not restated per-section here. This module's own piece is
`KV_DAILY_WRITE_TARGET` and the "WRITE BUDGET LEDGER" section below (`load_budget_ledger` /
`record_budget_write` / `adaptive_producer_interval_s` / `snapshot_pushes_allowed` /
`deliver_allowed` / `heartbeat_allowed`), which spec/baton.md cites back. The ledger itself lives in
its own file (`DEFAULT_BUDGET_STATE_FILE`, F4), written atomically -- see that constant's own
comment.

SINGLE-INSTANCE GUARD (#1538): on startup, atomically claim pusher.lock (O_EXCL-style create).
If the lock exists and its PID is alive with 'pusher' in its command line, terminate-and-replace it
(deploys always win). If the PID is dead or not a pusher, log and reclaim. Release on clean exit.

`timelines` and `stale_hidden_count` (#1505) are both frozen, append-only-derived facts computed
once per cycle from on-disk state (flow.jsonl event counts, the stale-room filter) -- neither reads
`now()` beyond what `drop_stale_rooms`'s own cutoff already did, so neither field makes the hash
churn on wall-clock time alone. The same property now holds for `live` telemetry too (F6, 2026-09-02
review): `quantize_live_for_hash` buckets the telemetry VALUES themselves (lastActivityAt's own
parsed instant, toolCalls/outputTokens coarsened to their own grain) rather than the wall-clock
moment it happens to be called, so an unchanged Running room's telemetry never forces a push on its
own either -- see that function's own docstring for the exact grains and why bucketing the clock
instead (the pre-fix shape) forced a snapshot push every bucket_seconds on ANY active fleet,
regardless of whether anything moved. The change-gate above only re-pushes on a real content change,
in every one of these three fields. See "THE TIMELINE HALF" below for the KV-write arithmetic this
adds.

Config comes from pusher.config.json next to this script (gitignored, machine-local -- ship
pusher.config.example.json and copy it):
    {
      "dll": "<path to Baton.Cli.dll (baton mcp)>",
      "push_url": "https://.../push/<PUSH_TOKEN>",
      "deliver_url": "https://.../deliver/<PUSH_TOKEN>",   # optional; derived from push_url if absent
      "heartbeat_url": "https://.../heartbeat/<PUSH_TOKEN>", # optional; derived from push_url if absent
      "interval_seconds": 25,
      "min_push_interval_s": 90,                          # optional; coalescing floor for snapshot pushes
      "lock_file": "pusher.lock",                          # optional; defaults next to this script
      "roots": [],
      "max_age_days": 3,
      "rooms_root": "~/.baton/rooms",                         # optional; defaults there
      "secret_patterns_file": "secretpatterns.local.txt",    # optional; defaults next to this script
      "push_state_file": "push-state.local.json",            # optional; defaults next to this script
      "underhood_dirs": [],
      "ntfy_topic": "<ntfy.sh topic, or a self-hosted one>",  # optional; a missing/blank topic
                                                               # disables ntfy pushes silently (one
                                                               # startup log line) -- see the "NTFY
                                                               # PUSH" section in this file for the
                                                               # tier table, quiet hours and dedup
      "ntfy_server": "https://ntfy.sh",                       # optional; self-hosted instances override
      "ntfy_quiet_hours": {                                   # optional; omit for no quiet hours
        "start": "22:00", "end": "07:00", "timezone": "America/New_York"
      },
      "ntfy_state_file": "ntfy-state.local.json"              # optional; defaults next to this script
    }
`ntfy_token` (a self-hosted ntfy instance's auth token), if needed, lives in secrets.local.json next
to this script -- {"ntfy_token": "..."} -- see secrets.local.example.json. ntfy.sh's own hosted
service needs no token for a private-by-obscurity topic name.

push_url (and deliver_url/heartbeat_url, if set) embed the push token -- the config file is a local
secret; never print or commit it.

THE TIMELINE HALF (#1505, extended by #1613 item 4)
-------------------------------------------
Pre-#42 (the daemon has not yet been given the projection job, spec/baton.md §7), this pusher gets
per-room timelines the same way it gets the fleet snapshot: one `room_detail` call per room each
cycle its timeline can still change -- every cycle for a non-terminal room, exactly once per process
lifetime for a terminal one (see `resolve_room_timeline`'s own docstring for the caching policy) --
through the SAME dotnet-mcp process `derive_snapshot_and_timelines` already spawns for
`fleet_status` -- never a second `dotnet` spawn per room. `extract_timeline` keeps only a fixed,
named set of content-free fields off each entry (see its own docstring for exactly which -- not
restated here, so this paragraph cannot go stale the way it once did when that set grew); `room_detail`'s
`stdout` field and any `note`/`detail`/`error` text are dropped unconditionally, so stdout can never
ride the mailbox through this path -- see the module's secret gate above for why that boundary exists
at all. Capped at the last TIMELINE_CAP (30) entries per
room: a lane's timeline is step-level transitions (dispatch, execution start/exit, retries, decisions)
written a handful of times per step, not a line per stdout write -- a lane produces tens of these
over its life, not thousands, and rides inside the SAME snapshot write the change-gate above already
gates (never a write of its own), so it costs nothing extra against the write-budget ledger
(spec/baton.md §6). Keyed by room PATH, never room NAME (#1505 review note: fleet_status dedupes
rooms by path, so two same-named rooms under different roots are distinct entries; a name-keyed join
would hand one room's timeline to the other -- exactly the wrong-and-confident failure mode #41's
removal below exists to stop, reintroduced by a careless join).

THE HEARTBEAT HALF (#1486), extended by #1613 item 2
-------------------------------------------
The change-gate above makes pushed_at legitimately stale on a quiet fleet, and nothing distinguishes
that from a dead pusher. Independent of the gated snapshot, this loop also POSTs a timestamp ping to
worker.js's /heartbeat route at a coarse fixed cadence -- hourly, tracked in push_state_file under
HEARTBEAT_STATE_KEY, and a more frequent derived-freshness ping (below) -- both gated by
`heartbeat_allowed` against the SAME write-budget ledger the snapshot half spends from (spec/baton.md
§6, "Fleet Glass write budget"). Same save-only-after-success discipline as
push_snapshot_and_record: POST first, record the timestamp only afterwards, so a failed heartbeat
retries next cycle instead of silently going stale. Heartbeat failures are logged and never raise
into the snapshot path -- see main()'s heartbeat try/except, which runs in its own block after the
snapshot has already been sent.

Pre-#1613 this body was a literal "{}"; it now carries `{"derived_at": ...}` -- an ISO timestamp
naming when THIS process's snapshot derivation last completed, not a deliverable, so it still does
not pass through the secret gate below (nothing in it that gate exists to catch). The Worker still
stamps its OWN receipt time server-side for heartbeat_at (see worker.js's /heartbeat handler);
derived_at travels inside the body precisely because — unlike heartbeat_at — it names a fact only
the pusher itself knows. The same endpoint is now ALSO hit on a second, independent, more frequent
cadence (`should_send_derived_ping`, "derived_at" section below) whenever a snapshot push hasn't
already delivered a fresher derived_at recently -- see that section for why this does not blow the
write budget above.

THE DELIVERABLES HALF (#1413 half 2)
-------------------------------------
Each run walks every TERMINAL room under rooms_root (a room with a terminal.json) and, for each,
uploads ONLY that room's declared output artifact(s) -- terminal.json's own "outputs" list, which is
exactly the room's `--output` file(s) -- plus a small verdict summary (state/error/try). NEVER
prompt.txt, NEVER .stdout.log, NEVER the rest of the artifacts directory; `declared_outputs` below is
the sole source of what gets read.

Before any deliverable content is uploaded it passes the SECRET GATE (`secret_hit_index`): scanned
against a denylist of regex patterns loaded from secret_patterns_file (gitignored -- the patterns
themselves are sensitive, since they reveal what to grep for; ship secretpatterns.example.txt with
generic placeholders instead). On a hit, the real content is replaced with a stub naming which
pattern index matched, and the hit is logged. If the patterns file is MISSING or UNREADABLE, this
fails CLOSED: every deliverable in that run is withheld, stub included, until an operator fixes it --
see `load_secret_patterns`'s docstring for why that state is deliberately never memorized as "done".

Dedupe is per (room_path, artifact, content-hash) -- `push_state_file` (gitignored) remembers the hash
last pushed for each (room_path, artifact) pair, and a run that finds an unchanged hash skips re-pushing
it (matching the snapshot half's path-keyed join, #1617; room name is kept for display only). A room
with zero declared outputs (typically a Failed room) still gets ONE deliverable, carrying
only the verdict summary, so a failure with nothing to show is still visible in the inbox.

WRITE BUDGET, KEY MIGRATION, & BATCH CAPPING (#1617, PR #1632; folded to writes/batch by #1690, cost
and cap both revised by the 2026-09-02 review's F3(a)/F5/F13):
Each /deliver POST costs DELIVER_BATCH_KV_WRITE_COST (3) KV writes flat, independent of how many
items it carries -- worker.js's handleDeliver (see its own storage-key docstring) makes 2 puts always
(inbox:batch:<id>, inbox:index) plus, conservatively, +1 for the delete path (a legacy eviction, or
F5's refcounted orphaned-batch reclaim) -- spec/baton.md §6, "Fleet Glass write budget" has the full
arithmetic and `deliver_allowed`'s own ledger gating. When keys migrated from room_name to room_path,
`gather_deliverables` automatically migrates legacy `f"{room_name}::{artifact}"` entries on load
under their respective room_path keys and drops the old keys, stamping `__format_version__ = 2`. This
avoids an all-at-once re-push storm of already-delivered history (measured at 210 deliverables / 211
KV writes worst case on this machine without migration). To protect against retry storms on network
errors or payload cap violations (>5MB body cap), deliver POSTs are capped at a cumulative-BYTES
budget (DEFAULT_DELIVER_BATCH_BYTES, ~4MB) plus a generous item-count ceiling
(DEFAULT_DELIVER_BATCH_COUNT_CEILING) -- F13: a fixed item count was only ever a proxy for the body
cap deliver's own POST is actually constrained by, and post-fold (a batch costs the same flat 3
regardless of K) a small fixed count bought nothing but extra write-amplifying batches for a large
backlog. A backlog drains across successive cycles (paced by F1's own deliver sub-budget and
adaptive interval, below), and a failing batch retries only its own capped bytes rather than an
unbounded full-fleet burst.

A PER-ITEM pattern hit IS memorized as pushed, unlike the missing-patterns-file case: its stub was
delivered, and not memorizing it would re-send that stub every cycle. The trade-off is that a
false-positive match does not self-heal when the offending pattern is later narrowed -- to re-offer
such an item, delete its (room, artifact) entry from push_state_file, or touch the artifact so its
hash changes.

PROJECTION FILE READ PATH (#1557 PR-B1, defaulted on by PR-B2)
--------------------------------------------------------------
Two sources for the fleet snapshot, selected by FLEET_GLASS_PROJECTION_SOURCE. Source order as of
PR-B2: `file` (the daemon's own projection file) is the DEFAULT and is used whenever the file is
present and fresher than PROJECTION_STALE_AFTER_S; otherwise this cycle falls back to
`derive_snapshot_and_timelines` (the `dotnet mcp` spawn) and the pushed body carries `staleness`.
`FLEET_GLASS_PROJECTION_SOURCE=derive` pins the pre-PR-B2 always-derive behavior. The switch, the
staleness rule, and the compare command are specified once in spec/baton.md §6 (the "PR-B1 … second,
opt-in source" passage and PR-B2's default flip beneath it) -- `read_projection_file` and
`compare_projection` carry the mechanics, not a second statement of the rule;
`derive_snapshot_and_timelines`'s own docstring carries the condition under which that path is
deleted (PR-C).

Usage: python pusher.py [--once] [--selftest] [--compare-projection]
Writes pusher.log (rotating-ish: truncated at 1MB) next to this script.
"""

from __future__ import annotations

import atexit
import hashlib
import json
import os
import re
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone
from io import BytesIO
from pathlib import Path

HERE = Path(__file__).parent
LOG = HERE / "pusher.log"

DEFAULT_ROOMS_ROOT = Path.home() / ".baton" / "rooms"
DEFAULT_SECRET_PATTERNS_FILE = HERE / "secretpatterns.local.txt"
DEFAULT_PUSH_STATE_FILE = HERE / "push-state.local.json"
DEFAULT_BUDGET_STATE_FILE = HERE / "write-budget.local.json"  # F4 (2026-09-02 review): the write-
                                                                # budget ledger's own file -- why
                                                                # separate from push-state.local.json:
                                                                # spec/baton.md §6.
DEFAULT_LOCK_FILE = HERE / "pusher.lock"

# #1557 PR-B1 added FLEET_GLASS_PROJECTION_SOURCE=file as an opt-in second source; PR-B2 makes
# `file` the DEFAULT -- main()'s loop reads the daemon's own BatonPaths.FleetProjectionFile and only
# spawns `dotnet mcp` when that file is absent or stale (PROJECTION_STALE_AFTER_S below).
# `FLEET_GLASS_PROJECTION_SOURCE=derive` pins the old always-derive behavior for one release, for an
# operator who needs to rule the file out while diagnosing. Any other value is treated as
# `PROJECTION_SOURCE_DEFAULT` rather than raising on a typo'd env var.
FLEET_GLASS_PROJECTION_SOURCE_ENV = "FLEET_GLASS_PROJECTION_SOURCE"
PROJECTION_SOURCE_DEFAULT = "file"

# #1557 plan §5: the `baton-daemon` scheduled task writes the file every cycle, so the fallback fires
# only when that task is down or behind: the file is treated as stale -- and the cycle falls back to
# `derive_snapshot_and_timelines` -- once it is older than 3 of the pusher's own coalescing windows
# (900s), or when it is absent/unreadable/malformed.
PROJECTION_STALE_AFTER_S = 900


def resolve_projection_file_path() -> Path:
    """Mirrors `BatonPaths.FleetProjectionFile` (src/Baton/Status/BatonPaths.cs) -- BATON_HOME when
    set to a non-blank value, else `~/.baton`, then `fleet/projection.json`. This is the ONE place
    that rule is duplicated in Python (#1557 PR-B1); every other reference goes through this
    function rather than re-deriving the path."""
    home_override = os.environ.get("BATON_HOME", "")
    root = Path(home_override) if home_override.strip() else Path.home() / ".baton"
    return root / "fleet" / "projection.json"


def read_projection_file(path: Path, now_ts: float, max_age_s: float = PROJECTION_STALE_AFTER_S):
    """Returns `(data, staleness)`. `data` is the parsed projection object (`{"derived_at": ...,
    "rooms": [...]}`) when the file is present, well-formed, and fresh -- `None` otherwise, in which
    case the caller falls back to `derive_snapshot_and_timelines` for this cycle (#1557 plan §5).
    `staleness` is `None` when `data` is fresh (nothing to report -- glass.html's chip stays absent,
    same optional-field convention as `pusher.writeBudgetExhaustedUntil`), else
    `{daemon_derived_at, age_s, stale: True}` for the pushed body -- `daemon_derived_at`/`age_s` are
    `None` when the file is absent/unreadable/malformed rather than merely old."""
    try:
        raw = path.read_text(encoding="utf-8")
    except OSError:
        return None, {"daemon_derived_at": None, "age_s": None, "stale": True}

    try:
        parsed = json.loads(raw)
    except json.JSONDecodeError:
        return None, {"daemon_derived_at": None, "age_s": None, "stale": True}

    daemon_derived_at = parsed.get("derived_at") if isinstance(parsed, dict) else None
    if not isinstance(daemon_derived_at, str):
        return None, {"daemon_derived_at": None, "age_s": None, "stale": True}

    try:
        derived_dt = datetime.fromisoformat(daemon_derived_at.replace("Z", "+00:00"))
    except ValueError:
        return None, {"daemon_derived_at": daemon_derived_at, "age_s": None, "stale": True}

    age_s = max(0.0, now_ts - derived_dt.timestamp())
    if age_s > max_age_s:
        return None, {"daemon_derived_at": daemon_derived_at, "age_s": round(age_s, 1), "stale": True}

    if not isinstance(parsed.get("rooms"), list):
        return None, {"daemon_derived_at": daemon_derived_at, "age_s": round(age_s, 1), "stale": True}

    return parsed, None


def log(msg: str) -> None:
    try:
        if LOG.exists() and LOG.stat().st_size > 1_000_000:
            LOG.write_text("", encoding="utf-8")
        with LOG.open("a", encoding="utf-8") as f:
            f.write(f"{datetime.now(timezone.utc).isoformat()} {msg}\n")
    except OSError:
        pass


# ---------------------------------------------------------------------------------------------
# Single-instance guard (#1538)
# ---------------------------------------------------------------------------------------------

def _try_create_lock(lock_path: Path, pid: int) -> bool:
    try:
        flags = os.O_CREAT | os.O_EXCL | os.O_WRONLY
        fd = os.open(str(lock_path), flags)
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                f.write(f"{pid}\n")
        except Exception:
            pass
        return True
    except (FileExistsError, OSError):
        return False


def read_lock_pid(lock_path: Path) -> int | None:
    try:
        raw = lock_path.read_text(encoding="utf-8").strip()
        return int(raw)
    except (OSError, ValueError):
        return None


def is_pid_alive(pid: int | None) -> bool:
    if pid is None or pid <= 0:
        return False
    if sys.platform == "win32":
        import ctypes
        kernel32 = ctypes.windll.kernel32
        SYNCHRONIZE = 0x00100000
        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, False, pid)
        if not handle:
            if kernel32.GetLastError() == 5:  # ERROR_ACCESS_DENIED
                return True
            return False
        try:
            # 258 is WAIT_TIMEOUT (still active); 0 is WAIT_OBJECT_0 (signaled / exited)
            res = kernel32.WaitForSingleObject(handle, 0)
            return res == 258
        finally:
            kernel32.CloseHandle(handle)
    else:
        try:
            os.kill(pid, 0)
            return True
        except (OSError, ProcessLookupError):
            return False


def get_process_cmdline(pid: int) -> str:
    if pid <= 0:
        return ""
    if sys.platform == "win32":
        try:
            out = subprocess.run(
                ["wmic", "process", "where", f"ProcessId={pid}", "get", "CommandLine"],
                capture_output=True, text=True, timeout=5, check=False,
            )
            lines = [ln.strip() for ln in out.stdout.splitlines() if ln.strip() and ln.strip().lower() != "commandline"]
            if lines:
                return lines[0]
        except Exception:
            pass
        try:
            out = subprocess.run(
                ["powershell", "-NoProfile", "-Command", f"(Get-CimInstance Win32_Process -Filter 'ProcessId = {pid}').CommandLine"],
                capture_output=True, text=True, timeout=5, check=False,
            )
            if out.returncode == 0 and out.stdout.strip():
                return out.stdout.strip()
        except Exception:
            pass
        return ""
    else:
        try:
            return Path(f"/proc/{pid}/cmdline").read_text().replace("\x00", " ")
        except Exception:
            return ""


def terminate_process(pid: int) -> None:
    if pid <= 0 or pid == os.getpid():
        return
    try:
        os.kill(pid, signal.SIGTERM)
    except (OSError, ProcessLookupError):
        pass
    for _ in range(20):
        if not is_pid_alive(pid):
            return
        time.sleep(0.05)
    if sys.platform == "win32" and is_pid_alive(pid):
        try:
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True, timeout=5, check=False)
        except Exception:
            pass


def acquire_lock(lock_path: Path, pid: int | None = None) -> bool:
    """Atomically claim lock_path with `pid`. If lock exists, check whether the holder is alive:
    if alive and its command line contains 'pusher', terminate-and-replace it (deploys always win);
    if dead or not a pusher, log and reclaim."""
    if pid is None:
        pid = os.getpid()

    if _try_create_lock(lock_path, pid):
        return True

    old_pid = read_lock_pid(lock_path)
    if old_pid is not None and old_pid != pid:
        if is_pid_alive(old_pid):
            cmdline = get_process_cmdline(old_pid)
            if "pusher" in cmdline.lower() and "claude" not in cmdline.lower():
                terminate_process(old_pid)
                log(f"replaced stale instance pid={old_pid}")
            else:
                log(f"reclaimed stale lock (pid={old_pid} not a pusher)")
        else:
            log(f"reclaimed stale lock (pid={old_pid} dead)")
    else:
        log("reclaimed unreadable stale lock")

    try:
        if lock_path.exists():
            lock_path.unlink()
    except OSError:
        pass

    if _try_create_lock(lock_path, pid):
        return True
    try:
        lock_path.write_text(f"{pid}\n", encoding="utf-8")
        return True
    except OSError as ex:
        log(f"failed to write lock file: {ex}")
        return False


def release_lock(lock_path: Path, pid: int | None = None) -> None:
    if pid is None:
        pid = os.getpid()
    try:
        if lock_path.is_file():
            cur_pid = read_lock_pid(lock_path)
            if cur_pid == pid:
                lock_path.unlink()
    except OSError:
        pass


# ---------------------------------------------------------------------------------------------
# Fleet snapshot (unchanged pipeline: derive via `baton mcp`, drop stale rooms, gather underhood)
# ---------------------------------------------------------------------------------------------

def rpc(proc: subprocess.Popen, req_id: int, method: str, params=None):
    msg = {"jsonrpc": "2.0", "id": req_id, "method": method}
    if params is not None:
        msg["params"] = params
    proc.stdin.write(json.dumps(msg) + "\n")
    proc.stdin.flush()
    while True:
        line = proc.stdout.readline()
        if not line:
            raise RuntimeError("host closed stdout")
        line = line.strip()
        if not line:
            continue
        resp = json.loads(line)
        if resp.get("id") == req_id:
            return resp


TIMELINE_CAP = 30  # last N timeline entries kept per room -- see module docstring's "THE TIMELINE
                    # HALF" for why a lane's step-level event count stays well under this.


def is_terminal_room(room_path: str) -> bool:
    """A room is terminal once terminal.json exists -- the same fast-path fleet_status itself
    uses (spec/baton.md §6). Non-terminal rooms are re-fetched every cycle (their timeline keeps
    growing); a terminal room's flow.jsonl is already frozen, so #1613 fetches it through
    room_detail exactly ONCE (see `derive_snapshot_and_timelines`'s cache parameter) rather than
    either skipping it forever (the pre-#1613 behavior -- a finished room showed no timeline at
    all) or re-fetching frozen bytes every cycle for nothing."""
    try:
        return (Path(room_path) / "terminal.json").is_file()
    except (OSError, TypeError):
        return False


def extract_timeline(room_detail_result: dict) -> list[dict]:
    """Content-free timeline projection from one room_detail response: KEEP ONLY `type`,
    `timestamp`, `stepId`, and `exitCode` off each timeline entry. Does not enumerate fields to DROP
    (stdout, note, error, detail) -- it enumerates the four fields it KEEPS, so a future room_detail
    field never leaks through by accident of this function failing to name it. `stdout` is never
    read at all, whether or not room_detail's response carries one.

    `stepId`/`exitCode` (#1613 item 4) are admitted under the content ruling in spec/baton.md §6, not
    restated here.

    The synthetic "unreadable" entry (RoomDetailTool.ReadTimelineAsync, e.g. a held-open ledger) is
    kept as a type-only marker -- its `detail` (an exception message) is dropped like any other
    entry's, so the timeline still shows "something is wrong here" without smuggling free text.

    No event TYPE is excluded, deliberately (#1537): this function never inspects `type`'s value,
    only its shape (a string) -- so the vocabulary here is exactly whatever the engine journals,
    never a second, narrower list to keep in sync with FlowEvent/CoreEvent/RoomEvent. The selftest's
    "admits every event type unfiltered" check is what keeps that true; a future type-keyed filter
    would fail it.
    """
    timeline = room_detail_result.get("timeline")
    if not isinstance(timeline, dict):
        return []
    entries = timeline.get("entries")
    if not isinstance(entries, list):
        return []
    out = []
    for entry in entries:
        if not isinstance(entry, dict):
            continue
        event_type = entry.get("type")
        if not isinstance(event_type, str):
            continue
        kept = {"type": event_type}
        timestamp = entry.get("timestamp")
        if isinstance(timestamp, str):
            kept["timestamp"] = timestamp
        step_id = entry.get("stepId")
        if isinstance(step_id, str):
            kept["stepId"] = step_id
        exit_code = entry.get("exitCode")
        if isinstance(exit_code, int) and not isinstance(exit_code, bool):
            kept["exitCode"] = exit_code
        out.append(kept)
    return out[-TIMELINE_CAP:]


# ---------------------------------------------------------------------------------------------
# Live telemetry for Running rooms (#1613 item 1, extended by this review's live-token finding and
# items 3/4's incremental reader): a tool-call count, claude-only live token counts, and a
# last-stream-activity instant, read directly off the currently-running execution's own
# already-captured .stdout.log -- no new `dotnet mcp` round trip, no engine change. Why pusher-side
# rather than engine-side (the ExecutionUsageProjector seam), and the token fields' exact gating and
# additive-vs-level semantics: spec/baton.md §6's `rooms[].live` schema entry, not restated here.
# ---------------------------------------------------------------------------------------------

def _running_execution_id(room: dict) -> str | None:
    steps = room.get("steps")
    if not isinstance(steps, list):
        return None
    for step in steps:
        if isinstance(step, dict) and step.get("state") == "Running" and isinstance(step.get("execution"), str):
            return step["execution"]
    return None


def _find_stdout_paths(room_path: str, execution_id: str) -> tuple[Path | None, Path | None]:
    """(stdout_path, rollover_path) for the Running execution's own captured stream. The same
    two-location fallback ArtifactManager/ExecutionUsageProjector use on the engine side (the live
    output directory, then artifacts/pruned for a retention-swept execution) -- mirrored here rather
    than shelling out, since the path shape itself (`artifacts/execution_<id>/.stdout.log`) is a
    stable, already-public on-disk contract (ArtifactManager.AllocateOutputDirectory /
    .ResolvePrunedOutputDirectory). `rollover_path` is the sibling `.stdout.log.1`
    ExecutionStreamLogger's single 8 MiB rollover produces in the SAME directory (#1613 review
    finding 3) -- None when no rollover has happened yet for this execution."""
    for relative in (f"artifacts/execution_{execution_id}", f"artifacts/pruned/execution_{execution_id}"):
        base = Path(room_path) / relative
        candidate = base / ".stdout.log"
        if candidate.is_file():
            rollover = base / ".stdout.log.1"
            return candidate, (rollover if rollover.is_file() else None)
    return None, None


def _read_new_lines(path: Path, offset: int) -> tuple[list[str], int]:
    """Complete lines appended to `path` since byte `offset` (#1613 review finding 4 -- read only
    the delta, never the whole file, every cycle), and the new offset positioned right after the
    last complete line consumed. A trailing partial line -- the vendor CLI mid-flush, no newline
    yet -- is left UNCONSUMED so it is read whole next cycle instead of split across two parses."""
    try:
        with path.open("rb") as f:
            f.seek(offset)
            chunk = f.read()
    except OSError:
        return [], offset
    if not chunk:
        return [], offset
    text = chunk.decode("utf-8", errors="replace")
    last_newline = text.rfind("\n")
    if last_newline == -1:
        return [], offset
    complete = text[:last_newline]
    consumed = len(complete.encode("utf-8")) + 1  # + the newline itself
    return complete.split("\n"), offset + consumed


# #1886: the three stream envelopes this file can read, by their own discriminator. Recognition is
# keyed on the ENVELOPE, never on whether a tool call or a usage figure was found on the line -- a
# claude stream's opening batch of `type: "system"` init lines has honestly counted zero tool calls,
# while a batch of an envelope none of these sets matches has counted nothing at all, and the two must
# not both read as `toolCalls: 0` (spec/baton.md §6's absent-never-a-substituted-zero rule for
# `rooms[].live`). agy is keyed off its own top-level `event` string instead, below.
_CLAUDE_STREAM_TYPES = frozenset({"assistant", "user", "system", "result"})
_CODEX_STREAM_TYPES = frozenset({
    "thread.started", "turn.started", "turn.completed", "turn.failed",
    "item.started", "item.updated", "item.completed",
})
# The SAME item types `Baton.Status.CodexUsageParser.TryParseToolName` gates on -- one tool step per
# `item.started` carrying one of them, so this file and the engine count the same events.
#
# THIS SET IS A SECOND COPY OF THE ENGINE'S, DELIBERATELY, AND ONLY THE FALLBACK READS IT.
# `CodexUsageParser` is the ONE arithmetic: the canonical statement of which item types are a tool
# step and of how `turn.completed.usage` becomes billed/context figures. This restatement exists
# because a Python reader cannot call into it, and because -- as of #1557 PR-B2 -- everything below
# runs ONLY on a cycle where the daemon's projection file is absent or stale, or where an operator
# has pinned `FLEET_GLASS_PROJECTION_SOURCE=derive`. On the default `file` path the daemon's own
# `CodexUsageParser` reading is what reaches the glass and none of this executes.
# The condition that deletes this copy outright, and why it cannot be deleted today, is
# `derive_snapshot_and_timelines`'s own REMOVAL CONDITION (PR-C) -- `extract_live_counts` is named
# there as group-(b). Why the duplication is accepted rather than dropped in favour of
# absent-not-zero: spec/baton.md §6's `rooms[].live` entry, not restated here.
_CODEX_TOOL_ITEM_TYPES = frozenset({"command_execution", "file_change", "mcp_tool_call", "web_search"})


def extract_live_counts(lines: list[str], seen_message_ids: set | None = None) -> dict:
    """THE FALLBACK READER (#1557 PR-B2). Reached only from `attach_live_telemetry`, i.e. only on a
    cycle where the daemon's projection file was absent or stale, or where an operator pinned
    `FLEET_GLASS_PROJECTION_SOURCE=derive`; on the default `file` path the daemon's own C# readers
    (`TokenBudgetMonitor` over the vendor's `IWorkerUsageParser`) produce every field below and none
    of this runs. `derive_snapshot_and_timelines`'s REMOVAL CONDITION names this function as part of
    the group-(b) block that comes out with PR-C, and why that is not satisfiable yet.

    A tool-call COUNT, plus live token/turn fields for ALL THREE vendors (#1682 -- agy's
    `step_update` usage was found live during that issue's own evidence gathering; the prior "agy has
    no usage to read" claim recorded here was wrong and is corrected in this same change; #1886 added
    codex), tolerant of a torn last line (the file is still being written) and of every vendor's
    stream envelope:
      - claude: `type`-keyed; a completed `assistant` message's `message.content` array carries a
        `{"type": "tool_use", ...}` block per tool call -- shape measured against real #1559
        capture fixtures (tests/Baton.Cli.Tests/RunCommandEchoTests.cs). The SAME `assistant`
        message's `message.usage` object carries the cache split a context figure needs -- the exact
        key names and where they were measured are spec/baton.md §6's `rooms[].live` entry, not
        restated here; see below for how each is used. #1706: its `input_tokens`/`output_tokens` are
        PLACEHOLDERS, not this message's real figures, and are read by nothing here.
      - agy: `event`-keyed; a `step_update` heartbeat with `state` in `"DONE"`/`"ERROR"` (its terminal
        lifecycle states) and `step_type: "tool"` marks one completed real tool step -- #1686 review
        F3: mirrors the engine's own `ClaudeUsageParser.CountToolSteps` unit (spec/baton.md §3),
        shape measured live against agy 1.1.11 (AgyWorkerAdapter.TryParseProgressEvent's own #1088
        doc comment). A `step_update` with
        `state: "DONE"` and `step_type: "agent_response"` carries its own `usage` object
        (`input_tokens`/`output_tokens`) -- measured live against a real #1682 evidence capture
        (`dispatch-implement-38c24d11`).
      - codex (#1886): `type`-keyed on a dotted event name; an `item.started` whose `item.type` is one
        of `_CODEX_TOOL_ITEM_TYPES` is one tool step, and a `turn.completed` carries the turn's own
        `usage`. Both shapes are the engine's -- `Baton.Status.CodexUsageParser` is the canonical
        statement of each, including that codex reports `input_tokens` INCLUSIVE of
        `cached_input_tokens` (so the fresh-input component here is the non-cached remainder, floored
        at 0). Shape measured against a real capture, `tests/Baton.Cli.Tests/Fixtures/codex-live-stream.jsonl`.
    A line that fails to parse as JSON is skipped, not an error -- the vendor CLI may have flushed
    a partial line at the exact moment this read caught the file mid-write.

    Returns, when at least one line matched a KNOWN envelope, `{"toolCalls": int}`, plus:
      - `"billedTokens"`: present only if at least one usage-bearing line in THIS batch reported
        one -- the SUM over the batch (additive: the caller accumulates this across every batch it
        has ever read for the execution, spec/baton.md §6), the same quantity the engine's own
        `TokenBudgetMonitor` arrests on (#1682) and read by the same per-vendor rule: on agy
        `input + output`, on claude `cache_creation` alone (#1706 -- the other two columns are
        placeholders). NOT `thinking_tokens` on either vendor, which is a breakdown already counted
        inside `output_tokens` (measured against real #1682 evidence: Σinput + Σoutput reproduces
        agy's own Σ`total_tokens` exactly). Whole-tree on claude, including subagent `assistant`
        events (they carry `parent_tool_use_id` but are not filtered out).
      - `"billedIsFloor"`: `True`, and omitted entirely otherwise, when at least one line in this
        batch contributed a claude cache-creation figure -- #1706: `billedTokens` is then a LOWER
        BOUND on the execution's real spend, not a measurement of it, and the glass says so rather
        than printing a number that reads as complete. The caller ORs this across batches, the same
        stickiness the engine's own `TokenBudgetMonitor._billedIsFloor` has, because one incomplete
        batch makes the accumulated total incomplete.
      - `"turns"`: present alongside `billedTokens` -- the COUNT of usage-bearing lines in this batch
        (additive, same convention).
      - `"context"`: `{"contextTokens": int, "cacheReadTokens": int}` from the LATEST claude
        `assistant` line in this batch that reports all three of `input_tokens`/
        `cache_read_input_tokens`/`cache_creation_input_tokens` together -- a LEVEL (the caller
        replaces, never sums, its own running value). NOT available on agy, whose step_update usage
        carries no cache-creation figure to build a comparable trio from
        (docs/vendor-capabilities.md); available on codex off `turn.completed.usage`, see below.
        Absent when no line in the batch reports the full trio: never a partial or fabricated
        figure, and never built from `input_tokens` alone (summing that across turns would
        re-count each turn's whole repeated context -- the trap this field exists to avoid).
        On codex the trio comes off `turn.completed.usage` instead and is built by the SAME rule
        `TokenBudgetMonitor` applies to that parser's readings: any of the three components present
        yields a level, the absent ones contributing nothing.

    #1886: `toolCalls` itself is ABSENT -- not 0 -- when NO line in the batch matched any of the three
    envelopes above, so that a zero here can only ever mean a stream this reader understands. The rule
    and the failure behind it are spec/baton.md §6's `rooms[].live` entry, not restated.

    `seen_message_ids` (#1686 review F6): claude can split one API response's usage across several
    consecutive `assistant` events sharing the SAME `message.id` and an IDENTICAL `message.usage`
    object -- measured against real `.stdout.log` captures (spec/baton.md §3: up to ~60% of
    usage-bearing lines on a real room are such repeats). Passing the SAME set across every batch this
    process has ever read for an execution (the caller-owned `live_cache` state,
    `live_telemetry_for_room` below) dedupes a repeat rather than summing it again; a line with no
    `message.id` (agy; claude's own terminal line is never read here) always accumulates. `None`
    (the default) dedupes only within this one call, for a caller with no cross-batch state to thread
    (a one-shot read, or a test).
    """
    tool_calls = 0
    billed_tokens = 0
    turns = 0
    usage_seen = False
    billed_is_floor = False
    context = None
    envelope_seen = False
    if seen_message_ids is None:
        seen_message_ids = set()
    for raw_line in lines:
        line = raw_line.strip()
        if not line:
            continue
        try:
            evt = json.loads(line)
        except json.JSONDecodeError:
            continue
        if not isinstance(evt, dict):
            continue

        event_type = evt.get("type")
        if event_type in _CLAUDE_STREAM_TYPES or event_type in _CODEX_STREAM_TYPES \
                or isinstance(evt.get("event"), str):
            envelope_seen = True

        if event_type == "assistant":
            message = evt.get("message")
            content = message.get("content") if isinstance(message, dict) else None
            if isinstance(content, list):
                tool_calls += sum(1 for b in content if isinstance(b, dict) and b.get("type") == "tool_use")
            usage = message.get("usage") if isinstance(message, dict) else None
            message_id = message.get("id") if isinstance(message, dict) else None
            # #1686 review F6: a repeated message.id means this usage object was already summed off an
            # earlier chunk of the SAME API response -- skip it rather than double-counting.
            already_counted = isinstance(message_id, str) and message_id and message_id in seen_message_ids
            # #1686 review F13: register the id only once a usage object is actually in hand -- an
            # `assistant` line carrying an id but no usage must not poison the seen-set for the line
            # that later carries that same id's real usage (the engine only reaches its own set for a
            # line that already parsed as usage; this keeps both sides on the same registration point).
            if isinstance(usage, dict) and isinstance(message_id, str) and message_id:
                seen_message_ids.add(message_id)
            if isinstance(usage, dict) and not already_counted:
                in_tok = usage.get("input_tokens")
                cache_creation = usage.get("cache_creation_input_tokens")
                cache_read = usage.get("cache_read_input_tokens")
                numeric = lambda v: isinstance(v, int) and not isinstance(v, bool)
                # #1706: `output_tokens` and `input_tokens` on this line are PLACEHOLDERS -- the same
                # engine-side measurement `ClaudeUsageParser.TryParseIncrementalUsage` documents, and
                # the reason this file's own `billedTokens` SAW only 28-91% of the real figure across
                # the 126-room sweep in spec/baton.md §3 (i.e. under-read by 9-72%; the fraction seen
                # and the fraction missed are easy to state backwards, so both are spelled out here).
                # Only `cache_creation_input_tokens` is a real billed figure, so only it accumulates.
                # The floor mark keys on EITHER cache column, not on cache_creation alone, so that this
                # and the engine's `TryParseIncrementalUsage` -- which accepts a reading on either --
                # agree about which lines are claude usage lines at all.
                # #1706 review M5: the floor mark and the BILLED CONTRIBUTION key on different
                # things, and conflating them made this file disagree with the engine on exactly one
                # real line shape. A line carrying `cache_read_input_tokens` but no
                # `cache_creation_input_tokens` IS a claude usage line -- so it marks the floor, the
                # same reading the engine's `TryParseIncrementalUsage` accepts -- but it carries NO
                # measurable billed component, so it must contribute no billed tokens and must not on
                # its own make `billedTokens` reportable. It previously reported `billedTokens: 0`
                # there while the engine reported nothing at all: same shape, two answers, and 0 is
                # the fabricated zero both sides exist to refuse. Pinned from the shared fixture by
                # `_selftest_claude_billing_gate` below and by `ClaudeEngineAndPusherBillingGateTests`.
                if numeric(cache_creation) or numeric(cache_read):
                    billed_is_floor = True
                if numeric(cache_creation):
                    billed_tokens += cache_creation
                    turns += 1
                    usage_seen = True
                # #1666 review F5: a sub-agent's own turn (root `parent_tool_use_id` a string) never
                # updates the reported context -- mirrors the engine's TokenBudgetMonitor, which tracks
                # the sub-agent bucket separately and clears it on the next parent line rather than
                # letting a smaller sub-agent reading replace the parent's (spec/baton.md §3).
                if numeric(in_tok) and numeric(cache_read) and numeric(cache_creation) \
                        and not isinstance(evt.get("parent_tool_use_id"), str):
                    # The context LEVEL is unaffected: it is what the vendor loaded for this request,
                    # and the placeholder `input_tokens` contributes 2 tokens to a six-figure sum.
                    context = {
                        "contextTokens": in_tok + cache_read + cache_creation,
                        "cacheReadTokens": cache_read,
                    }
        elif evt.get("event") == "step_update":
            step = evt.get("step_update")
            if isinstance(step, dict) and step.get("step_type") == "tool" \
                    and step.get("state") in ("DONE", "ERROR"):
                tool_calls += 1
            elif isinstance(step, dict) and step.get("state") == "DONE":
                if step.get("step_type") == "agent_response":
                    usage = step.get("usage")
                    if isinstance(usage, dict):
                        out = usage.get("output_tokens")
                        in_tok = usage.get("input_tokens")
                        numeric = lambda v: isinstance(v, int) and not isinstance(v, bool)
                        if numeric(out) or numeric(in_tok):
                            billed_tokens += (out if numeric(out) else 0) + (in_tok if numeric(in_tok) else 0)
                            turns += 1
                            usage_seen = True
        elif event_type == "item.started":
            # #1886: one tool step per started tool item -- the same unit
            # `CodexUsageParser.CountToolSteps` accumulates for the engine's arrest cap, so the glass
            # and the cap can never disagree about how busy a codex lane is. `item.completed` is
            # deliberately NOT counted; the register above names the double it avoids.
            # FALLBACK ONLY, and a restatement of the parser's arithmetic rather than a second
            # authority for it -- `_CODEX_TOOL_ITEM_TYPES` above carries both halves of that.
            item = evt.get("item")
            if isinstance(item, dict) and item.get("type") in _CODEX_TOOL_ITEM_TYPES:
                tool_calls += 1
        elif event_type == "turn.completed":
            # #1886: codex reports one usage object per COMPLETED TURN, and `input_tokens` already
            # includes `cached_input_tokens` -- CodexUsageParser is the canonical statement of both,
            # and of why the fresh-input component is the floored remainder rather than the raw field.
            # FALLBACK ONLY, same as the `item.started` arm above: on the default `file` source it is
            # that parser's own reading, not this block's, that reaches the glass.
            # Note the turn count is structurally sparse on this vendor next to claude's: one
            # `turn.completed` can sit behind hundreds of tool calls.
            usage = evt.get("usage")
            if isinstance(usage, dict):
                numeric = lambda v: isinstance(v, int) and not isinstance(v, bool)
                total_in = usage.get("input_tokens")
                cached_in = usage.get("cached_input_tokens")
                cache_write = usage.get("cache_write_input_tokens")
                out = usage.get("output_tokens")
                fresh_in = None
                if numeric(total_in):
                    fresh_in = max(0, total_in - cached_in) if numeric(cached_in) else total_in
                # The SAME billed components TokenBudgetMonitor sums off this parser's readings:
                # fresh input + output + cache write. `reasoning_output_tokens` is read by nothing --
                # a breakdown already inside `output_tokens`, the same exclusion both other vendors get.
                if fresh_in is not None or numeric(out) or numeric(cache_write):
                    billed_tokens += (fresh_in or 0) + (out if numeric(out) else 0) \
                        + (cache_write if numeric(cache_write) else 0)
                    turns += 1
                    usage_seen = True
                if fresh_in is not None or numeric(cached_in) or numeric(cache_write):
                    context = {
                        "contextTokens": (fresh_in or 0) + (cached_in if numeric(cached_in) else 0)
                        + (cache_write if numeric(cache_write) else 0),
                    }
                    # Absent, never a substituted 0, when the line carried no cache-read figure --
                    # matching TokenBudgetMonitor, whose CacheReadLevelTokens is simply the latest
                    # reading's own (nullable) field and is omitted from the projection when null.
                    if numeric(cached_in):
                        context["cacheReadTokens"] = cached_in

    if not envelope_seen:
        # #1886: no known envelope in this batch -- report nothing rather than a zero that would read
        # as a count it never took. See this function's own docstring.
        return {}

    result = {"toolCalls": tool_calls}
    if usage_seen:
        result["billedTokens"] = billed_tokens
        result["turns"] = turns
    if billed_is_floor:
        result["billedIsFloor"] = True
    if context is not None:
        result["context"] = context
    return result


def _apply_live_delta(state: dict, delta: dict) -> None:
    """Merge one parsed batch (a rollover file or newly-appended live-file bytes) into a
    per-execution running state: `toolCalls`/`billedTokens`/`turns` ACCUMULATE (#1613 review findings
    3/4, extended to the #1682 fields the same way -- every batch this process has ever read for the
    execution), `billedIsFloor` is STICKY (#1706: once any batch's contribution was a lower bound, the
    accumulated total is one, and no later complete batch can make it whole again), `context` is the
    latest LEVEL seen -- only overwritten when the batch actually reports one, so an empty or tool-only
    batch never blanks out a level that was already known."""
    counts = state["counts"]
    # #1886: gated on PRESENCE, and the state seeds `counts` empty rather than at `{"toolCalls": 0}`.
    # Both halves are needed together: a delta that omits the field cannot un-fabricate a zero the
    # seed already put there. Once any batch has reported a count, later count-free batches of a
    # known envelope keep adding 0 to it, so a lane that has genuinely stopped calling tools still
    # reads its accumulated total rather than going absent again.
    if "toolCalls" in delta:
        counts["toolCalls"] = counts.get("toolCalls", 0) + delta["toolCalls"]
    if "billedTokens" in delta:
        counts["billedTokens"] = counts.get("billedTokens", 0) + delta["billedTokens"]
        counts["turns"] = counts.get("turns", 0) + delta["turns"]
    if delta.get("billedIsFloor"):
        counts["billedIsFloor"] = True
    if "context" in delta:
        state["context"] = delta["context"]


LAST_ACTIVITY_BUCKET_SECONDS = 90  # #1613 review finding 1: floor lastActivityAt's mtime to this
                                    # bucket BEFORE it enters the payload, so a continuously-
                                    # streaming lane's every-chunk mtime advance does not itself
                                    # change snapshot_hash every cycle (the #1457 change-gate) -- see
                                    # the module docstring's write-budget arithmetic. Quantizing
                                    # rather than excluding (unlike derived_at) is deliberate: a lane
                                    # that streams text without ever calling a tool would otherwise
                                    # change no field in `live` at all, so glass would keep rendering
                                    # a stale "active Nm ago" for a lane that is actually streaming.


def _quantized_activity_iso(mtime: float, bucket_seconds: float = LAST_ACTIVITY_BUCKET_SECONDS) -> str:
    bucketed = (mtime // bucket_seconds) * bucket_seconds
    return datetime.fromtimestamp(bucketed, tz=timezone.utc).isoformat()


STDOUT_TAIL_MAX_LINES = 40  # #1710: "last ~40 lines" per the issue's own design.
STDOUT_TAIL_MAX_BYTES = 4_000  # #1710: hard cap per room, ~4 KB.
STDOUT_TAIL_READ_WINDOW_BYTES = 65_536  # generous headroom read from EOF -- a run of unusually long
                                          # lines still yields STDOUT_TAIL_MAX_LINES candidates before
                                          # the byte cap below trims them. Never a whole-file read of a
                                          # log that can run to megabytes -- the module docstring's
                                          # "read the file the way extract_live_counts does" means the
                                          # same bounded-read discipline, not literally that function
                                          # (extract_live_counts parses JSON events off an incremental
                                          # delta; this reads a fixed tail window off the whole file
                                          # every cycle, since the tail is a snapshot of "now", not an
                                          # accumulator).
STDOUT_TAIL_TRUNCATION_MARK = "…"


def _decode_utf8_boundary_safe(data: bytes) -> str:
    """Strict UTF-8 decode, falling back to `errors="replace"` only for bytes that are genuinely
    invalid (never for a straddled multi-byte character at a caller-chosen cut point -- callers of
    this function are expected to have already trimmed to a real line boundary, so a
    `UnicodeDecodeError` here means the log itself carries non-UTF-8 bytes, not a torn character)."""
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return data.decode("utf-8", errors="replace")


def _read_tail_text(path: Path, window_bytes: int = STDOUT_TAIL_READ_WINDOW_BYTES) -> str:
    """Last `window_bytes` bytes of `path`, decoded, with a possibly-torn leading line dropped (the
    seek landed mid-line unless it started at byte 0). Bounds the read against a multi-megabyte log
    -- #1710's own selftest arm (a 5 MB log) is what this window exists for.

    #1723: the boundary is found on the RAW BYTES (searching for the `\\n` byte, which never appears
    as part of a multi-byte UTF-8 sequence) before anything is decoded -- decoding first and then
    slicing the decoded text, as the pre-#1723 version did, still worked here because the drop only
    ever discarded text before the found newline, but doing the search byte-side is what makes the
    same discipline safe to reuse below the max_bytes cut too, where decoding before slicing is
    exactly the bug (see `stdout_tail_for_room`)."""
    try:
        size = path.stat().st_size
    except OSError:
        return ""
    start = max(0, size - window_bytes)
    try:
        with path.open("rb") as f:
            f.seek(start)
            chunk = f.read()
    except OSError:
        return ""
    if start > 0:
        nl = chunk.find(b"\n")
        chunk = chunk[nl + 1:] if nl != -1 else b""
    return _decode_utf8_boundary_safe(chunk) if chunk else ""


STDOUT_TAIL_BLOB_ELISION_THRESHOLD = 200  # #1723: a whitespace-free token this long (base64, a data
                                            # URI, a hex dump) reads as noise, never as prose -- the
                                            # operator's own words, "stdout by itself is unintelligible
                                            # and useless".
_BLOB_TOKEN_PATTERN = re.compile(r"\S{%d,}" % STDOUT_TAIL_BLOB_ELISION_THRESHOLD)


def _elide_blob_tokens(text: str) -> str:
    """Replaces any whitespace-free run of >= `STDOUT_TAIL_BLOB_ELISION_THRESHOLD` characters with a
    byte-count marker (#1723). Applies to every surviving tail line, JSON-rendered or plain-text
    alike -- a blob can show up embedded in ordinary non-JSON output just as easily as inside a
    stream-json field."""
    return _BLOB_TOKEN_PATTERN.sub(
        lambda m: f"…[{len(m.group(0).encode('utf-8'))} bytes elided]…", text)


STDOUT_TAIL_PROSE_FIELD_LIMIT = 200  # #1723: one prose line stays short even when the source field
                                       # (an assistant message, a tool_result body) runs long.


def _prose_first_line(text: str, limit: int = STDOUT_TAIL_PROSE_FIELD_LIMIT) -> str:
    """First line of `text` (a multi-line assistant message or tool result renders as ONE prose line,
    matching #1723's "one short prose line" per stream-json line), truncated to `limit` chars."""
    first = text.strip().splitlines()[0] if text.strip() else ""
    return first if len(first) <= limit else first[:limit] + STDOUT_TAIL_TRUNCATION_MARK


def _cap_plain_line(line: str, limit: int = STDOUT_TAIL_PROSE_FIELD_LIMIT) -> str:
    """Caps `line` to `limit` chars (review rev1738 F3). `stdout_tail_for_room` applies this, after
    blob elision, to EVERY surviving tail line -- both `_render_tail_line`'s raw passthrough
    (malformed/non-dict JSON, or a dict whose `type`/`event` this module's dispatch has no arm for)
    and its rendered prose lines, most of which are already comfortably under `limit` via
    `_prose_first_line`'s own cap and so pass through this unchanged. Applied BEFORE the `max_bytes`
    cut: without this, a long plain-text line (a stack trace, a verbose crash message) with ordinary
    spacing was capped by neither this nor `_elide_blob_tokens` (whitespace-free runs only), so when
    it was the newest, still-open line and alone exceeded the remaining byte budget, the forward
    boundary search found nothing ahead of it and the whole line -- possibly the only content a
    Running room has to show -- was dropped, leaving just the truncation mark. Caveat: a rendered
    line that runs past `limit` (e.g. a `[tool_result: ...]` line whose body is near
    `STDOUT_TAIL_PROSE_FIELD_LIMIT` chars, plus the wrapping brackets) can lose its closing bracket
    to the cut -- cosmetic, and strictly better than the pre-fix whole-line drop it replaces."""
    return line if len(line) <= limit else line[:limit] + STDOUT_TAIL_TRUNCATION_MARK


def _prose_summarize_tool_input(tool_input: object, limit: int = 120) -> str:
    """`key=value, ...` one-liner off a `tool_use` block's `input` object, truncated -- the "one-line
    summary of its input" #1723 asks for. Not a general JSON pretty-printer: values are stringified
    plainly (JSON-encoded only when not already a string) and newlines are flattened, since this is a
    glance summary, not a faithful re-serialization."""
    if not isinstance(tool_input, dict) or not tool_input:
        return ""
    parts = []
    for key, value in tool_input.items():
        rendered = value if isinstance(value, str) else json.dumps(value, separators=(",", ":"))
        parts.append(f"{key}={rendered.replace(chr(10), ' ')}")
    summary = ", ".join(parts)
    return summary if len(summary) <= limit else summary[:limit] + STDOUT_TAIL_TRUNCATION_MARK


class _Unrecognized:
    """Sentinel `_render_stream_json_prose` returns when the envelope's OWN dispatch key (claude's
    `type`, agy's `event`) carries a value this function has no arm for at all -- e.g. a vendor field
    added tomorrow, or a shape neither vendor's adapter documents. Distinct from `None`, which means
    the shape WAS recognized and #1723 deliberately judged it noise (see the list in this module's
    docstring below). `_render_tail_line` echoes this sentinel's line raw, mirroring the C# renderer's
    `EchoStreamJsonLine` fail-visible posture (`src/Baton.Cli/RunCommand.cs:589-593`) -- never
    swallow a valid-JSON envelope this function does not recognize (review rev1738 F1/F4)."""


_UNRECOGNIZED = _Unrecognized()


def _render_stream_json_prose(evt: dict) -> str | None | _Unrecognized:
    """One prose line for a parsed stream-json object; `None` if the line's shape IS recognized but
    #1723 judged it deliberately silent -- a hook lifecycle marker, a thinking-only block, a
    rate-limit ping, an agy heartbeat that isn't a DONE/ERROR step, each named at its own `return
    None` below; or `_UNRECOGNIZED` if the envelope's own top-level `type`/`event` VALUE has no arm
    here at all, in which case `_render_tail_line` echoes the raw line rather than dropping it
    (review rev1738 F1/F4 -- see `_Unrecognized`'s own doc comment). This only covers the TOP-LEVEL
    dispatch: a recognized `type`/`event` whose nested sub-shape doesn't match any arm (e.g. an
    `assistant` message with a non-dict `message`) still falls through to that branch's own `return
    None`, not `_UNRECOGNIZED` -- a narrower, pre-existing fail-silent gap this rev1738 round does not
    close, left for a future pass rather than expanding this one's scope.

    This is a Python sibling of `Baton.Cli`'s `RunCommand.EchoStreamJsonLine`/`WorkerStreamRendering`
    (`src/Baton.Cli/RunCommand.cs`, `src/Baton.Cli/WorkerStreamRendering.cs`) and of
    `AgyWorkerAdapter.TryParseProgressEvent` (`src/Baton.Vendors/AgyWorkerAdapter.cs:1257-1358`) by
    necessity, not by choice: the pusher is Python until #1557 moves projection into the daemon, so
    the same envelope shapes are recognized a second time here rather than shared. See those files
    for the authoritative shape-by-shape rules; this function mirrors their overall SHAPE (which
    events exist, which are noise) but NOT their exact output in every case. Three deliberate
    divergences, disclosed rather than restated (review rev1738 F2):
    - the claude `tool_use` arm below appends a `key=value` summary of the tool's `input` object that
      the C# side never produces (`RunCommand.EchoStreamJsonLine`'s `tool` Kind carries only the tool
      NAME, `src/Baton.Cli/RunCommand.cs:571`; the Kind itself is assigned by
      `ClaudeWorkerAdapter.TryParseProgressEvent`, not `WorkerStreamRendering.cs`, which only
      delegates rendering to `EchoStreamJsonLine`). Kept because it is materially more useful on a
      bounded glance surface than a bare name -- and it is why raw tool-input values (paths, command
      strings, arbitrary key/value pairs) reach this tail where the C# renderer never puts them;
      `stdout_tail_for_room`'s secret gate (`_gate_tail_lines`) still runs AFTER this function, on the
      rendered string, so a known-secret-shaped input value is still withheld, but anything else is
      not
    - the claude `user`/`tool_result` arm below has NO C# counterpart at all --
      `ClaudeWorkerAdapter.TryParseProgressEvent` returns `false` for a `type: "user"` line
      (`src/Baton.Vendors/ClaudeWorkerAdapter.cs:1299-1305`), so `EchoStreamJsonLine` echoes it raw;
      this function instead renders it, a Python-only addition
    - the agy `step_update` arm below renders `[tool: {step_type} — done|error]`, and also fires on
      the ERROR state, where `AgyWorkerAdapter.TryParseProgressEvent` renders Kind `"status"` (→
      `[status: {step_type}]`) and is DONE-only (`src/Baton.Vendors/AgyWorkerAdapter.cs:1292-1306`)
    """
    if "type" in evt:
        evt_type = evt.get("type")

        if evt_type == "assistant":
            message = evt.get("message")
            content = message.get("content") if isinstance(message, dict) else None
            if not isinstance(content, list):
                return None
            for block in content:
                if not isinstance(block, dict):
                    continue
                block_type = block.get("type")
                if block_type == "text":
                    text = block.get("text")
                    if isinstance(text, str) and text.strip():
                        return _prose_first_line(text)
                elif block_type == "tool_use":
                    name = block.get("name")
                    if isinstance(name, str) and name:
                        summary = _prose_summarize_tool_input(block.get("input"))
                        return f"[tool: {name}({summary})]" if summary else f"[tool: {name}]"
            return None  # a thinking-only block, or an empty content array -- nothing to show.

        if evt_type == "user":
            message = evt.get("message")
            content = message.get("content") if isinstance(message, dict) else None
            if not isinstance(content, list):
                return None
            for block in content:
                if not isinstance(block, dict) or block.get("type") != "tool_result":
                    continue
                body = block.get("content")
                if isinstance(body, list):
                    body = next(
                        (c.get("text") for c in body if isinstance(c, dict) and isinstance(c.get("text"), str)),
                        None)
                if isinstance(body, str) and body.strip():
                    prefix = "[tool_result error: " if block.get("is_error") else "[tool_result: "
                    return prefix + _prose_first_line(body) + "]"
            return None

        if evt_type == "result":
            is_error = evt.get("is_error")
            if not isinstance(is_error, bool):
                return None
            if is_error:
                summary = evt.get("result")
                text = summary if isinstance(summary, str) and summary else "no error detail in the result envelope"
                return f"[result: error — {_prose_first_line(text)}]"
            return "[result: success]"

        if evt_type == "system":
            subtype = evt.get("subtype")
            if subtype == "init":
                return "[status: Session started]"
            if subtype == "status":
                status = evt.get("status")
                if isinstance(status, str) and status:
                    return f"[status: {status}]"
            return None  # every other subtype (hook lifecycle, thinking-token estimates, ...) is noise.

        return _UNRECOGNIZED  # a claude "type" value this renderer has no arm for (e.g. rate_limit_event).

    if "event" in evt:
        # agy's envelope is keyed on "event" (init/step_update/result), NOT claude's "type" -- a
        # genuinely different parse, mirroring AgyWorkerAdapter.TryParseProgressEvent's own shapes
        # (src/Baton.Vendors/AgyWorkerAdapter.cs:1257-1358), same discipline as extract_live_counts'
        # existing `evt.get("event") == "step_update"` branch a few hundred lines above (review
        # rev1738 F1).
        event = evt.get("event")

        if event == "init":
            return "[status: Session started]"

        if event == "step_update":
            step = evt.get("step_update")
            if not isinstance(step, dict):
                return None
            state = step.get("state")
            step_type = step.get("step_type")
            if state in ("DONE", "ERROR") and isinstance(step_type, str) \
                    and step_type not in ("unknown", "checkpoint", "user_input"):
                marker = "done" if state == "DONE" else "error"
                return f"[tool: {step_type} — {marker}]"
            return None  # the ACTIVE edge, or a DONE/ERROR unknown/checkpoint/user_input step -- noise.

        if event == "result":
            # Mirrors AgyWorkerAdapter.TryParseProgressEvent's own priority (AgyWorkerAdapter.cs:1308-1347):
            # a non-empty `response` wins regardless of status; only then does a non-SUCCESS `status`
            # render as an error; a SUCCESS result with an empty response, or no status at all, is the
            # bare `case "result":` -- ignore, no signal.
            result = evt.get("result")
            if not isinstance(result, dict):
                return None
            response = result.get("response")
            if isinstance(response, str) and response.strip():
                return _prose_first_line(response)
            status = result.get("status")
            if isinstance(status, str) and status and status != "SUCCESS":
                error = result.get("error")
                text = error if isinstance(error, str) and error else "no error detail in the result envelope"
                return f"[result: error — {_prose_first_line(text)}]"
            return None

        return _UNRECOGNIZED  # an agy "event" value this renderer has no arm for.

    return _UNRECOGNIZED  # neither claude's "type" key nor agy's "event" key present at all.


def _render_tail_line(raw_line: str) -> str | None:
    """One rendered tail line for `raw_line`, or `None` if it should be dropped entirely (#1723). A
    line that parses as a JSON OBJECT routes through `_render_stream_json_prose`; anything else --
    malformed JSON, valid JSON that is not an object (an array, a bare number), or an object whose
    `type`/`event` this module doesn't recognize at all -- passes through UNCHANGED here, mirroring
    `WorkerStreamLineRenderer`'s non-JSON and unrecognized-envelope fallbacks, both of which echo raw
    rather than drop. The caller (`stdout_tail_for_room`) is responsible for capping this raw
    passthrough to `STDOUT_TAIL_PROSE_FIELD_LIMIT` chars (`_cap_plain_line`, review rev1738 F3) AFTER
    blob elision -- capping here first would feed `_elide_blob_tokens` an already-truncated line and
    report the wrong elided byte count for a long whitespace-free blob."""
    stripped = raw_line.strip()
    if not stripped:
        return raw_line
    try:
        evt = json.loads(stripped)
    except json.JSONDecodeError:
        return raw_line
    if not isinstance(evt, dict):
        return raw_line
    rendered = _render_stream_json_prose(evt)
    if rendered is _UNRECOGNIZED:
        return raw_line
    return rendered


def _gate_tail_lines(lines: list[str], patterns: list[re.Pattern] | None) -> list[str]:
    """Per-LINE secret gate for #1710's stdout tail -- never the whole-tail withholding
    `_apply_secret_gate` does for a deliverable artifact: a matching line becomes `[withheld]`, every
    other line rides through untouched, so one hit never blanks a room's whole tail. `patterns is
    None` (the `load_secret_patterns` fail-closed sentinel -- missing/unreadable patterns file)
    withholds every line, the same fail-closed posture the deliverables path takes on the same
    condition (`_apply_secret_gate`'s own `patterns is None` branch)."""
    if patterns is None:
        return ["[withheld]" for _ in lines]
    return ["[withheld]" if secret_hit_index(line, patterns) is not None else line for line in lines]


def stdout_tail_for_room(room_path: str, execution_id: str, patterns: list[re.Pattern] | None,
                          max_lines: int = STDOUT_TAIL_MAX_LINES,
                          max_bytes: int = STDOUT_TAIL_MAX_BYTES) -> str | None:
    """`live.stdoutTail` for one Running room (#1710, rendered as prose and blob-elided by #1723): the
    last `max_lines` RAW lines of the CURRENT execution's `.stdout.log`, each rendered to prose
    (`_render_tail_line` -- a stream-json object becomes one short human-readable line or is dropped;
    a non-JSON line passes through), blob-elided (`_elide_blob_tokens`), then secret-gated per
    surviving line (`_gate_tail_lines`), hard-capped at `max_bytes` by truncating from the FRONT (the
    newest lines are what a live tail is for) and marking the cut with `STDOUT_TAIL_TRUNCATION_MARK`
    on the first surviving line -- ON A LINE BOUNDARY, never mid-character (#1723: the pre-fix version
    decoded the byte-truncated tail with `errors="replace"` and only dropped a *found* leading partial
    line, so a cut landing inside a line with no earlier newline within the truncated budget left a
    genuine U+FFFD at the front; this version finds the boundary on the raw bytes FIRST, so a strict
    decode of what's kept never straddles a character, at the cost of possibly emitting less than
    `max_bytes` when no boundary exists inside the budget at all -- in that case the newest surviving
    line is kept alone rather than the tail collapsing to just the truncation mark, review rev1738
    F3). None when there is no captured
    stdout yet for this execution -- absent, never a fabricated empty string, matching
    `live_telemetry_for_room`'s own never-fabricated convention."""
    stdout_path, _rollover_path = _find_stdout_paths(room_path, execution_id)
    if stdout_path is None:
        return None
    text = _read_tail_text(stdout_path)
    if not text:
        return None
    raw_lines = text.splitlines()[-max_lines:]
    rendered = []
    for raw in raw_lines:
        line = _render_tail_line(raw)
        if line is not None:
            # #1723: elide FIRST, off the full (possibly long) rendered/raw line, so a blob's
            # reported byte count is the real one; THEN cap (review rev1738 F3) so no single
            # surviving line -- rendered or raw passthrough -- can alone exceed the max_bytes budget
            # below and get dropped whole by the forward boundary search.
            rendered.append(_cap_plain_line(_elide_blob_tokens(line)))
    gated = _gate_tail_lines(rendered, patterns)
    tail = "\n".join(gated)
    if not tail:
        return None
    encoded = tail.encode("utf-8")
    if len(encoded) > max_bytes:
        # Reserve the marker's own bytes out of the budget UP FRONT so the final, marker-prefixed
        # tail never exceeds max_bytes -- computing the cut against the full budget and prepending
        # the marker afterwards can overshoot by the marker's own length.
        marker_bytes = STDOUT_TAIL_TRUNCATION_MARK.encode("utf-8")
        content_budget = max(0, max_bytes - len(marker_bytes))
        cut_start = len(encoded) - content_budget
        # #1723: search for the boundary on the RAW BYTES, from the tentative cut point FORWARD, so
        # the found `\n` is always a real line terminator (never a byte inside a multi-byte character
        # -- `\n` cannot appear as a UTF-8 continuation or lead byte) and the subsequent decode is
        # strict rather than papering over a straddle with `errors="replace"`.
        nl_index = encoded.find(b"\n", max(0, cut_start))
        if nl_index != -1:
            body_bytes = encoded[nl_index + 1:]
        else:
            # No line boundary inside the budget at all -- rather than drop every surviving line
            # (review rev1738 F3: the newest, still-open line may be the ONLY content a Running room
            # has to show), keep the newest line alone. It is already capped to
            # STDOUT_TAIL_PROSE_FIELD_LIMIT chars by the render loop above, so this only trims
            # further on a genuinely tiny `max_bytes` (e.g. a selftest budget); trimmed from the end,
            # on a UTF-8 lead-byte boundary, to keep the hard `max_bytes` contract.
            body_bytes = gated[-1].encode("utf-8") if gated else b""
            if len(body_bytes) > content_budget:
                # `body_bytes[-content_budget:]` would slice to the WHOLE line if content_budget is
                # ever 0 (max_bytes <= len(marker_bytes)) -- `-0` means "from index 0", not "keep
                # nothing" -- so slice by the explicit start index instead.
                body_bytes = body_bytes[len(body_bytes) - content_budget:]
                while body_bytes and (body_bytes[0] & 0xC0) == 0x80:
                    body_bytes = body_bytes[1:]
        tail = STDOUT_TAIL_TRUNCATION_MARK + _decode_utf8_boundary_safe(body_bytes)
    return tail


DOING_NOW_LIMIT = 140  # #1793: `live.doingNow`'s own cap -- one line, no elision marker (contrast
                        # STDOUT_TAIL_PROSE_FIELD_LIMIT's own "…"-suffixed caps above).


def _first_line_only(text: str) -> str:
    """First line of `text`, UNtruncated -- the line-boundary half of `_prose_first_line` without its
    char cap, since `doing_now` caps with a plain slice (no trailing mark) instead."""
    stripped = text.strip()
    return stripped.splitlines()[0] if stripped else ""


def _truncate_plain_no_marker(text: str, limit: int) -> str:
    """Hard-truncates to `limit` chars with NO trailing marker -- #1793's own "no elision markers"
    wording, contrast every other cap in this module."""
    return text if len(text) <= limit else text[:limit]


def _first_argument_value(tool_input: object) -> str | None:
    """Same first-property read `_prose_summarize_tool_input` does, but the VALUE alone, with no
    `key=` label prefixed the way that function's own summary carries one -- see
    `StdoutTailRenderer.FirstArgumentValue`'s doc comment for the worked example."""
    if not isinstance(tool_input, dict) or not tool_input:
        return None
    for value in tool_input.values():
        rendered = value if isinstance(value, str) else json.dumps(value, separators=(",", ":"))
        return rendered.replace("\n", " ")
    return None


def doing_now_for_room(room_path: str, execution_id: str, patterns: list[re.Pattern] | None,
                        max_lines: int = STDOUT_TAIL_MAX_LINES) -> str | None:
    """`live.doingNow` (#1793, spec/baton.md §6 has the schema entry -- not restated here). This
    module's own arm is a second, independently-written implementation of the C# side's
    `StdoutTailRenderer.ComputeDoingNow` (`src/Baton.Cli/Daemon/StdoutTailRenderer.cs`), kept aligned
    with it via a checked-in fixture the two selftests both read rather than a shared call -- see this
    module's own selftest arm for the path.

    Scans the last `max_lines` RAW lines of the current execution's `.stdout.log`, from the NEWEST
    line backward, for the last `assistant` stream-json line, passing over any non-assistant line
    found along the way rather than halting the search at it. `None` on every shape
    `StdoutTailRenderer.ComputeDoingNow`'s own doc comment lists as a no-fabrication case, matching
    `stdout_tail_for_room`'s own never-fabricated convention. The derived line runs through the SAME
    `_gate_tail_lines` secret gate `stdout_tail_for_room` applies, as a one-element list -- a hit
    becomes `[withheld]`, and `patterns` `None` (the `_load_secret_patterns` fail-closed sentinel)
    withholds it unconditionally."""
    line = _doing_now_line_for_room(room_path, execution_id, max_lines)
    return _gate_tail_lines([line], patterns)[0] if line is not None else None


def _doing_now_line_for_room(room_path: str, execution_id: str,
                              max_lines: int = STDOUT_TAIL_MAX_LINES) -> str | None:
    stdout_path, _rollover_path = _find_stdout_paths(room_path, execution_id)
    if stdout_path is None:
        return None
    text = _read_tail_text(stdout_path)
    if not text:
        return None
    raw_lines = text.splitlines()[-max_lines:]
    for raw in reversed(raw_lines):
        stripped = raw.strip()
        if not stripped:
            continue
        try:
            evt = json.loads(stripped)
        except json.JSONDecodeError:
            continue
        if not isinstance(evt, dict) or evt.get("type") != "assistant":
            continue

        message = evt.get("message")
        content = message.get("content") if isinstance(message, dict) else None
        if not isinstance(content, list):
            return None

        last_block = None
        for block in content:
            if isinstance(block, dict):
                last_block = block
        if last_block is None:
            return None

        block_type = last_block.get("type")
        if block_type == "text":
            block_text = last_block.get("text")
            if isinstance(block_text, str) and block_text.strip():
                return _truncate_plain_no_marker(_first_line_only(block_text), DOING_NOW_LIMIT)
            return None

        if block_type == "tool_use":
            name = last_block.get("name")
            if not isinstance(name, str) or not name:
                return None
            tool_input = last_block.get("input")
            description = tool_input.get("description") if isinstance(tool_input, dict) else None
            if isinstance(description, str) and description:
                return _truncate_plain_no_marker(_first_line_only(description), DOING_NOW_LIMIT)
            main_argument = _first_argument_value(tool_input)
            line = f"{name} {_truncate_plain_no_marker(_first_line_only(main_argument), 80)}" \
                if main_argument else name
            return _truncate_plain_no_marker(line, DOING_NOW_LIMIT)

        return None
    return None


def live_telemetry_for_room(room: dict, live_cache: dict | None = None,
                             patterns: list[re.Pattern] | None = None) -> dict | None:
    """None when there is no Running step, or its execution has no captured stdout yet (dispatch
    just started) -- absent, never a fabricated zero, matching ExecutionUsageView's own
    never-null/never-fabricated convention on the engine side. `lastActivityAt`'s honesty property
    and the token fields' gating are spec/baton.md §6's `rooms[].live` schema entry, not restated
    here.

    `live_cache` is the caller-owned `(byte_offset, running_counts)` dict #1613 review findings 3/4
    need to avoid re-reading and re-parsing the whole `.stdout.log` every cycle -- the same
    caller-owned-dict pattern `terminal_timeline_cache` already uses. Keyed by `room_path::
    execution_id`, so a retry's fresh execution starts its own counters rather than inheriting a
    finished one's. Defaults to a fresh, single-call dict when omitted (tests, and any caller that
    genuinely wants a one-shot whole-file read -- offset 0 reading to EOF is equivalent)."""
    if live_cache is None:
        live_cache = {}
    execution_id = _running_execution_id(room)
    room_path = room.get("path")
    if execution_id is None or not isinstance(room_path, str) or not room_path:
        return None

    stdout_path, rollover_path = _find_stdout_paths(room_path, execution_id)
    if stdout_path is None:
        return None

    try:
        mtime = stdout_path.stat().st_mtime
        current_size = stdout_path.stat().st_size
    except OSError:
        return None

    key = f"{room_path}::{execution_id}"
    state = live_cache.setdefault(key, {
        # #1886: `counts` seeds EMPTY -- see `_apply_live_delta`'s own note. A pre-seeded
        # `{"toolCalls": 0}` here is what made an unreadable stream shape render as a measured zero.
        "stdout_offset": 0, "rollover_offset": 0, "counts": {}, "context": None,
        # #1686 review F6: persists across every batch read for this execution -- a message.id read in
        # an earlier cycle's batch must still dedupe a repeat that shows up in a LATER cycle's batch.
        "seen_message_ids": set(),
    })

    # #1613 review finding 3: `.stdout.log` rolls over to `.stdout.log.1` at 8 MiB and resets to
    # empty (ExecutionStreamLogger.cs) -- a size DECREASE since the offset we last read is the
    # rollover signal. The rename preserves content byte-for-byte, so hand the read position across
    # to the rollover file's own (sticky, independently-tracked) offset rather than re-reading
    # anything already counted -- this also self-heals a SECOND rollover later in the same
    # execution's life, since `.stdout.log.1` gets overwritten each time and the same
    # decrease-detection applies to its own offset too.
    if current_size < state["stdout_offset"]:
        state["rollover_offset"] = max(state["rollover_offset"], state["stdout_offset"])
        state["stdout_offset"] = 0

    if rollover_path is not None:
        try:
            rollover_size = rollover_path.stat().st_size
        except OSError:
            rollover_size = 0
        if rollover_size < state["rollover_offset"]:
            state["rollover_offset"] = 0
        rollover_lines, state["rollover_offset"] = _read_new_lines(rollover_path, state["rollover_offset"])
        if rollover_lines:
            _apply_live_delta(state, extract_live_counts(rollover_lines, state["seen_message_ids"]))

    new_lines, state["stdout_offset"] = _read_new_lines(stdout_path, state["stdout_offset"])
    if new_lines:
        _apply_live_delta(state, extract_live_counts(new_lines, state["seen_message_ids"]))

    result = dict(state["counts"])
    if state["context"] is not None:
        result.update(state["context"])
    result["lastActivityAt"] = _quantized_activity_iso(mtime)
    tail = stdout_tail_for_room(room_path, execution_id, patterns)
    if tail is not None:
        result["stdoutTail"] = tail
    doing_now = doing_now_for_room(room_path, execution_id, patterns)
    if doing_now is not None:
        result["doingNow"] = doing_now
    return result


def attach_live_telemetry(room_list: list, live_cache: dict, patterns: list[re.Pattern] | None = None) -> None:
    """Mutates each Running room in the (already stale-filtered) list in place, adding a `live`
    field. Gated on the pusher's own displayed `state`, not the raw engine state: a room
    fleet_status already downgraded to Stalled (#1513, a CONFIRMED-dead process) never gets a live
    section a dead process cannot honestly back. Called AFTER drop_stale_rooms in main()'s loop, on
    purpose -- `lastActivityAt` is a real file mtime, not a manufactured "now" stamp, so unlike
    `exhaustedUntil` it never needs `newest_timestamp`'s skip set: running it post-filter simply
    means it plays no part in the staleness decision at all, sidestepping the question by
    construction rather than by exemption. `live_cache` is main()'s own persisted dict (#1613 review
    findings 3/4) -- REQUIRED here (unlike `live_telemetry_for_room`'s optional default) because a
    fresh dict every call would defeat the whole point of incremental reading."""
    if not isinstance(room_list, list):
        return
    for room in room_list:
        if not isinstance(room, dict) or room.get("state") != "Running":
            continue
        live = live_telemetry_for_room(room, live_cache, patterns)
        if live is not None:
            room["live"] = live


def prune_live_telemetry_cache(live_cache: dict, room_list: list) -> dict:
    """New dict carrying forward only the cache entries for executions still actually Running in
    `room_list` -- a finished or retried execution's counters must not linger forever in a
    long-lived pusher process. Mirrors `terminal_timeline_cache`'s own per-cycle prune in main()."""
    live_keys = set()
    for room in room_list or []:
        if not isinstance(room, dict) or room.get("state") != "Running":
            continue
        execution_id = _running_execution_id(room)
        room_path = room.get("path")
        if execution_id is not None and isinstance(room_path, str) and room_path:
            live_keys.add(f"{room_path}::{execution_id}")
    return {k: v for k, v in live_cache.items() if k in live_keys}


PRUNED_ITEMS_CAP = 20  # #1155: newest N pruned execution dirs surfaced per room -- keeps the KV payload bounded.


def pruned_info_for_room(room: dict, pruned_cache: dict | None = None) -> dict | None:
    """`rooms[].pruned` -- shape and rationale are canonical in spec/baton.md §6 (#1155), not
    restated here. `None` when there is nothing to report (no directory, or an unreadable one),
    so an old consumer of the pushed snapshot sees no change. `items` caps at `PRUNED_ITEMS_CAP`,
    newest-`prunedAt`-first; `count` is the true total.

    `pruned_cache` is the caller-owned per-room cache (#1756 review F2) keyed on `room_path`,
    storing the `(pruned/ dir mtime, child count)` this result was computed for -- a `rglob` walk
    over a `pruned/` directory holding thousands of files is skipped whenever neither has changed
    since the last call, mirroring `live_telemetry_cache`'s own incremental-avoids-rework shape. A
    rename into `pruned/` changes the directory's own mtime, so the key still invalidates on a new
    entry even though `rglob` never runs to notice it directly. Defaults to a fresh, single-call
    dict when omitted (tests, and any caller that genuinely wants a one-shot walk)."""
    if pruned_cache is None:
        pruned_cache = {}
    room_path = room.get("path")
    if not isinstance(room_path, str) or not room_path:
        return None
    pruned_root = Path(room_path) / "artifacts" / "pruned"
    if not pruned_root.is_dir():
        return None

    try:
        dir_stat = pruned_root.stat()
        children = list(pruned_root.iterdir())
    except OSError:
        return None

    cache_key = (dir_stat.st_mtime, len(children))
    cached = pruned_cache.get(room_path)
    if cached is not None and cached[0] == cache_key:
        return cached[1]

    entries = []
    for child in children:
        try:
            stat = child.stat()
            size = (sum(f.stat().st_size for f in child.rglob("*") if f.is_file())
                    if child.is_dir() else stat.st_size)
        except OSError:
            continue
        entries.append({
            "name": child.name,
            "bytes": size,
            "prunedAt": datetime.fromtimestamp(stat.st_mtime, tz=timezone.utc).isoformat(),
        })

    if not entries:
        result = None
    else:
        entries.sort(key=lambda e: e["prunedAt"], reverse=True)
        result = {"count": len(entries), "items": entries[:PRUNED_ITEMS_CAP]}

    pruned_cache[room_path] = (cache_key, result)
    return result


def attach_pruned_info(room_list: list, pruned_cache: dict) -> None:
    """Mutates each room in `room_list` in place, adding a `pruned` field (see
    `pruned_info_for_room`) when its `artifacts/pruned/` directory has anything in it. Mirrors
    `attach_live_telemetry`'s own in-place-mutation shape; called after `drop_stale_rooms` in
    main()'s loop for the same reason `attach_live_telemetry` is -- `prunedAt` is a real file
    mtime, not a manufactured "now" stamp, so it plays no part in the staleness decision.
    `pruned_cache` is main()'s own persisted dict (#1756 review F2), REQUIRED here for the same
    reason `live_cache` is required by `attach_live_telemetry` -- a fresh dict every call would
    defeat the whole point of caching across polls."""
    if not isinstance(room_list, list):
        return
    for room in room_list:
        if not isinstance(room, dict):
            continue
        pruned = pruned_info_for_room(room, pruned_cache)
        if pruned is not None:
            room["pruned"] = pruned


def prune_pruned_info_cache(pruned_cache: dict, room_list: list) -> dict:
    """New dict carrying forward only the cache entries for rooms still present in `room_list` --
    a room dropped by `drop_stale_rooms` (or one whose path simply moved) must not linger forever
    in a long-lived pusher process. Mirrors `prune_live_telemetry_cache`'s own per-cycle prune in
    main()."""
    room_paths = {r.get("path") for r in (room_list or [])
                  if isinstance(r, dict) and isinstance(r.get("path"), str)}
    return {k: v for k, v in pruned_cache.items() if k in room_paths}


LIVE_TELEMETRY_HASH_BUCKET_SECONDS = 300  # #1690 item 3: telemetry churn gate -- spec/baton.md §6.
LIVE_TELEMETRY_TOOLCALLS_GRAIN = 5      # F6 (2026-09-02 review): coarsen toolCalls to this grain.
LIVE_TELEMETRY_TOKENS_GRAIN = 10_000    # F6: coarsen outputTokens to this grain.


def _quantize_live_value(live: dict, bucket_seconds: float) -> dict:
    """F6 (2026-09-02 review): quantize the telemetry VALUES themselves, never the wall clock a
    caller happens to compute them at -- why the pre-fix (clock-bucketed) version was a churn
    generator: spec/baton.md §6, "Fleet Glass write budget", not restated here. `lastActivityAt`
    (already ISO, mtime-bucketed to 90s by `_quantized_activity_iso` before it reaches here) is
    re-bucketed to the coarser `bucket_seconds` grain by its OWN parsed instant; `toolCalls`/
    `outputTokens` are rounded down to their own grain."""
    out = dict(live)
    last_activity = live.get("lastActivityAt")
    if isinstance(last_activity, str):
        try:
            instant = datetime.fromisoformat(last_activity).timestamp()
        except ValueError:
            pass
        else:
            out["lastActivityAt"] = (instant // bucket_seconds) * bucket_seconds
    tool_calls = live.get("toolCalls")
    if isinstance(tool_calls, (int, float)) and not isinstance(tool_calls, bool):
        out["toolCalls"] = (int(tool_calls) // LIVE_TELEMETRY_TOOLCALLS_GRAIN) * LIVE_TELEMETRY_TOOLCALLS_GRAIN
    output_tokens = live.get("outputTokens")
    if isinstance(output_tokens, (int, float)) and not isinstance(output_tokens, bool):
        out["outputTokens"] = (int(output_tokens) // LIVE_TELEMETRY_TOKENS_GRAIN) * LIVE_TELEMETRY_TOKENS_GRAIN
    return out


def quantize_live_for_hash(room_list: list, bucket_seconds: float = LIVE_TELEMETRY_HASH_BUCKET_SECONDS) -> list:
    """A copy of `room_list` for HASHING ONLY (never posted -- the real `live` section rides the wire
    every cycle unchanged): every room's `live` section, if present, has its VALUES quantized by
    `_quantize_live_value` (F6). Everything OTHER than `live` is copied through untouched, so any
    non-telemetry difference the change-gate already cared about still flips the hash on the very
    next cycle."""
    out = []
    for room in room_list or []:
        if isinstance(room, dict) and isinstance(room.get("live"), dict):
            quantized = dict(room)
            quantized["live"] = _quantize_live_value(room["live"], bucket_seconds)
            out.append(quantized)
        else:
            out.append(room)
    return out


def resolve_room_timeline(room_path: str, is_terminal: bool, cache: dict, fetch_fn) -> list[dict]:
    """#1613 item 4's caching POLICY, pulled out of `derive_snapshot_and_timelines` so it is
    testable without a live `dotnet` subprocess: a non-terminal room always calls `fetch_fn`
    (its timeline keeps growing); a terminal room calls it AT MOST ONCE -- a cache hit returns the
    cached entries without calling `fetch_fn` again, and a fetch that comes back non-empty is
    written into `cache` (mutated in place) so every later call for the same room_path short-
    circuits. A terminal room whose fetch returns [] (error, or a genuinely empty timeline) is
    NOT cached, so it retries next cycle rather than assuming empty is a stable answer."""
    if not is_terminal:
        return fetch_fn(room_path)

    cached = cache.get(room_path)
    if cached is not None:
        return cached

    entries = fetch_fn(room_path)
    if entries:
        cache[room_path] = entries
    return entries


def derive_snapshot_and_timelines(dll: str, roots: list, terminal_timeline_cache: dict | None = None) -> tuple[str, dict]:
    """THE DOTNET-SPAWN PATH. As of #1557 PR-B2 this is no longer the default source -- `file` is
    (`PROJECTION_SOURCE_DEFAULT`), and this runs only on a cycle where the projection file is
    absent or stale, or when an operator has pinned `FLEET_GLASS_PROJECTION_SOURCE=derive`.

    REMOVAL CONDITION (PR-C, which closes #1557): this function, and the group-(b) projection block
    it feeds (`extract_live_counts` through `attach_pruned_info`), come out once BOTH hold --
      1. one full release has run with `file` in effect and NO
         "projection file stale or absent ... falling back to derive" line in pusher.log, and
      2. the projection file carries per-room `timelines`.
    (2) is not satisfiable today and is not a nicety: `timelines` for a non-terminal room needs a
    `room_detail` call per cycle, which is this subprocess, so PR-C cannot delete this path while
    the file omits them -- see the `timelines` gap issue named in PR-B2's body. (1) is measurable
    now that the `baton-daemon` scheduled task runs on the operator's machine (#1905 made the file
    the default on that basis): the fallback below is the edge case, and the log line it emits on
    a stale cycle is the honest signal that the daemon is down or behind.

    Returns (the rooms JSON exactly as fleet_status produced it, {room_path: [timeline entries]}
    for every room with one) -- ONE dotnet-mcp process for both, reused across every room_detail
    call in this cycle (module docstring's "THE TIMELINE HALF"): spawning a fresh `dotnet` per room
    would multiply the exact per-cycle subprocess cost the daemon-owns-the-projection design (#1502
    menu #42) exists to kill.

    Non-terminal rooms are re-fetched every cycle (their timeline keeps growing). Terminal rooms
    (#1613 item 4 -- pre-#1613 they were skipped forever, which is why a finished lane showed no
    timeline at all) are fetched through room_detail exactly ONCE per process lifetime and served
    from `terminal_timeline_cache` on every cycle after: a terminal room's flow.jsonl is frozen, so
    re-fetching identical bytes every ~25s cycle would be pure waste, and would also make the
    pushed snapshot's hash churn on nothing (the #1457 change-gate). `terminal_timeline_cache` is
    caller-owned (main()'s own dict, persisted across loop iterations, mutated in place here) --
    there is no on-disk cache, so a pusher restart self-heals by refetching once more.
    """
    terminal_timeline_cache = {} if terminal_timeline_cache is None else terminal_timeline_cache
    # #1458: dll now points at Baton.Cli.dll -- "mcp" is the verb that used to be the whole binary
    # (Baton.Mcp.Host.dll's own Main). Argv shape mirrors ClaudeWorkerAdapter's own
    # EnsureMemoryProposalMcpConfig, the canonical explanation of why the verb comes first.
    proc = subprocess.Popen(
        ["dotnet", dll, "mcp", "--fleet-status-tool", "--room-detail-tool"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        text=True, encoding="utf-8",
    )
    try:
        rpc(proc, 1, "initialize", {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "fleet-pusher", "version": "0.2.0"},
        })
        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        proc.stdin.flush()
        resp = rpc(proc, 2, "tools/call", {
            "name": "fleet_status",
            "arguments": {"roots": roots} if roots else {},
        })
        result = resp.get("result")
        if result is None:
            raise RuntimeError(f"tools/call error: {resp.get('error')}")
        text = result["content"][0]["text"]
        rooms = json.loads(text)  # validate before pushing; raises on garbage
        room_list = rooms if isinstance(rooms, list) else (rooms.get("rooms") or [])

        timelines = {}
        next_id = 3

        def fetch_timeline(room_path: str) -> list[dict]:
            nonlocal next_id
            detail_resp = rpc(proc, next_id, "tools/call", {
                "name": "room_detail",
                "arguments": {"room": room_path},
            })
            next_id += 1
            detail_result = detail_resp.get("result")
            if detail_result is None:
                log(f"room_detail error for {room_path}: {detail_resp.get('error')}")
                return []
            detail = json.loads(detail_result["content"][0]["text"])
            return extract_timeline(detail)

        for room in room_list:
            if not isinstance(room, dict):
                continue
            room_path = room.get("path")
            if not isinstance(room_path, str) or not room_path:
                continue

            try:
                entries = resolve_room_timeline(
                    room_path, is_terminal_room(room_path), terminal_timeline_cache, fetch_timeline)
                if entries:
                    timelines[room_path] = entries
            except Exception as ex:  # noqa: BLE001 — one room's timeline must not sink the cycle
                log(f"room_detail failed for {room_path}: {type(ex).__name__}: {ex}")
    finally:
        proc.terminate()
    return text, timelines


_NEWEST_TIMESTAMP_SKIP_KEYS = frozenset({"exhaustedUntil", "live", "pruned"})


def newest_timestamp(node, _skip_keys: frozenset = _NEWEST_TIMESTAMP_SKIP_KEYS) -> str:
    """Max ISO-8601-looking string anywhere in the room object -- shape-agnostic on purpose,
    so a fleet_status field rename degrades to 'room has no timestamp' (kept), never a crash.

    `exhaustedUntil` (#1551) is excluded by key, the first deliberate exception to "shape-agnostic":
    it's a vendor-quota park's reset instant, a FUTURE timestamp by construction while parked.
    Folding it into this scan would make an abandoned parked room's "newest timestamp" always
    outrun drop_stale_rooms' cutoff below -- a room nobody is watching would never age out.

    `live`/`pruned` (whole subtrees) are excluded for a different reason: to keep the two projection
    sources' staleness decisions IDENTICAL (#1557 PR-B2). In `derive` mode these blocks do not exist
    yet when drop_stale_rooms runs -- `attach_live_telemetry`/`attach_pruned_info` are called
    afterwards, deliberately, so that `lastActivityAt`/`prunedAt` (real file mtimes, not step
    timestamps) "play no part in the staleness decision at all" (those functions' own docs). In
    `file` mode the daemon has already embedded them, so without this skip the same room would be
    scanned differently by source: one whose newest STEP timestamp is past the cutoff but whose
    stdout was touched recently would be dropped under `derive` and kept under `file`. Skipping the
    subtree here restores by exemption what derive mode gets by construction, in the one place both
    sources share."""
    best = ""
    if isinstance(node, dict):
        for k, v in node.items():
            if k in _skip_keys:
                continue
            best = max(best, newest_timestamp(v, _skip_keys))
    elif isinstance(node, list):
        for v in node:
            best = max(best, newest_timestamp(v, _skip_keys))
    elif isinstance(node, str) and len(node) >= 19 and node[4] == "-" and node[10] == "T":
        best = node
    return best


def drop_stale_rooms(body: str, max_age_days: float) -> tuple[str, int]:
    """Filter rooms whose newest timestamp is older than the cutoff -- zombie RUNNING rooms
    included (a room that died without terminal.json shows Running forever; age is the only
    honest signal). Rooms with no parseable timestamp are KEPT: unreadable is a finding the
    glass should show, not silently drop.

    Returns (filtered body, dropped count). #1505 landmine #43: a dropped room used to be logged
    ONLY to pusher.log -- a room that vanished and a room that never existed looked identical on the
    glass. The count is now the caller's to carry into the pushed snapshot (as
    `stale_hidden_count`), so the page can show "N older than {max_age_days}d hidden" instead of
    silence."""
    data = json.loads(body)
    # fleet_status emits a bare room list; tolerate a {rooms: [...]} wrapper too.
    bare = isinstance(data, list)
    rooms = data if bare else data.get("rooms")
    if not isinstance(rooms, list):
        return body, 0
    cutoff = datetime.now(timezone.utc).timestamp() - max_age_days * 86400
    kept = []
    for room in rooms:
        ts = newest_timestamp(room)
        if ts:
            try:
                when = datetime.fromisoformat(ts.replace("Z", "+00:00")).timestamp()
                if when < cutoff:
                    continue
            except ValueError:
                pass
        kept.append(room)
    dropped = len(rooms) - len(kept)
    if dropped:
        log(f"filtered {dropped} stale room(s) older than {max_age_days}d")
    if bare:
        return json.dumps(kept), dropped
    data["rooms"] = kept
    return json.dumps(data), dropped


# ---------------------------------------------------------------------------------------------
# Hot-set capping (#1656) -- measurement and full contract: spec/baton.md §6, "Paging and the
# terminal hot-set cap". Terminal rooms are frozen (terminal.json never changes once written) and
# glass.html itself already only ever RENDERS the newest slice of them, so this moves the same cap
# upstream: only the newest HOT_TERMINAL_CAP terminal rooms ride the plain fleet_status response;
# the rest are still derived and pushed (as `terminal_archive`, a field worker.js's /push handler
# stores under its own KV key, never inside "snapshot") but only served back a page at a time.
# ---------------------------------------------------------------------------------------------

HOT_TERMINAL_CAP = 40  # matches what glass.html already slices the Succeeded bucket to
                        # client-side pre-#1656 (groupLanesHtml(visibleDone.slice(0,40), ...)) --
                        # picked to keep the same "what an operator actually looks at" size, not a
                        # new number.

HOT_NONTERMINAL_WARN = 60  # F3 (2026-09-02 review): the cap above bounds only the terminal bucket
                            # -- Running/Stalled/Indeterminate rooms ride the plain fleet_status
                            # response in FULL, uncapped (spec/baton.md §6). This is a signal, not a
                            # cap: one log line when concurrently-active rooms cross the threshold,
                            # so an incident storm shows up in pusher.log rather than only as a
                            # bigger push the day it happens.


def nonterminal_warn_line(non_terminal_count: int) -> str | None:
    """One log line when `non_terminal_count` exceeds HOT_NONTERMINAL_WARN, else None -- a signal,
    not a cap. Full contract: spec/baton.md §6, "Paging and the terminal hot-set cap"."""
    if non_terminal_count > HOT_NONTERMINAL_WARN:
        return (f"non-terminal room count {non_terminal_count} exceeds HOT_NONTERMINAL_WARN "
                f"({HOT_NONTERMINAL_WARN}) -- unbounded, no cap")
    return None

_TERMINAL_STATES = frozenset({"Succeeded", "Failed"})  # the two buckets glass.html's own Terminal
                                                        # section covers (render()'s `termContent`)
                                                        # -- Running/Stalled/Indeterminate/unreadable
                                                        # rooms are never terminal by this measure.


def split_hot_and_archive(room_list: list) -> tuple[list, list, int]:
    """Splits `room_list` (fleet_status's own per-room objects) into `(hot_rooms, terminal_archive,
    terminal_total)`. `hot_rooms` (non-terminal rooms plus the newest HOT_TERMINAL_CAP terminal
    ones) is what rides the plain (no `page`) fleet_status response; `terminal_archive` is the FULL
    terminal population (not just the tail beyond the cap -- a `page=0` fetch then returns the same
    newest rooms `hot_rooms` already carried); `terminal_total` is the total terminal count. Full
    contract, including the "newest" measure and why a malformed room degrades to non-terminal
    rather than being dropped: spec/baton.md §6, "Paging and the terminal hot-set cap"."""
    non_terminal = [r for r in room_list if not (isinstance(r, dict) and r.get("state") in _TERMINAL_STATES)]
    terminal = [r for r in room_list if isinstance(r, dict) and r.get("state") in _TERMINAL_STATES]
    terminal.sort(key=newest_timestamp, reverse=True)
    hot_rooms = non_terminal + terminal[:HOT_TERMINAL_CAP]
    return hot_rooms, terminal, len(terminal)


def _git(cwd: str, *args: str) -> str:
    try:
        out = subprocess.run(
            ["git", *args], cwd=cwd, capture_output=True, text=True, timeout=15, check=False,
        )
        return out.stdout.strip()
    except (OSError, subprocess.TimeoutExpired):
        return ""


def gather_underhood(cfg: dict) -> list:
    """Worktree telemetry for active lanes: branch, diff shape, newest commit.

    CONTENT-FREE BY DESIGN: branch names, file counts, and +/- totals only -- no diff hunks, so
    nothing here can leak a secret VALUE. Fleet-level only, never attached to a specific room row --
    #1505 removed the `underhood_logs` name-matching heuristic that used to do that (`name.endswith(
    e["name"].lstrip("w")) or e["name"].lstrip("w") in name`, a substring match on a w-stripped
    directory name): two similarly-named lanes could silently attach the WRONG lane's log tail to a
    worktree entry. Wrong-and-confident is worse than absent (spec/baton.md's epic #1502 ratified
    decisions) -- this stays a fleet-level section with no per-room attribution until a real
    room<->worktree key exists to replace the guess, not before."""
    import glob as globmod

    entries = []
    for pattern in cfg.get("underhood_dirs", []):
        for d in sorted(globmod.glob(pattern)):
            if not (Path(d) / ".git").exists():
                continue
            branch = _git(d, "rev-parse", "--abbrev-ref", "HEAD")
            shortstat = _git(d, "diff", "--shortstat", "HEAD")
            dirty = len([ln for ln in _git(d, "status", "--porcelain").splitlines() if ln])
            last = _git(d, "log", "-1", "--format=%s\x1f%cI")
            subject, _, committed = last.partition("\x1f")
            entries.append({
                "name": Path(d).name,
                "branch": branch,
                "uncommitted": shortstat or ("clean" if dirty == 0 else f"{dirty} file(s) touched"),
                "last_commit": subject[:120],
                "last_commit_at": committed,
            })
    return entries


class KvWriteCapError(RuntimeError):
    """#1712: the Worker answered 429 {"reason": "kv-write-cap", "resets_at": ...} -- Cloudflare's
    own daily KV write cap (spec/baton.md §6), not an ordinary push failure. Raised out of
    post_json so every producer can back its own write-budget ledger sub-budget off immediately
    (mark_kv_write_cap_exhausted below) rather than retrying into the same cap every cycle."""

    def __init__(self, resets_at: str):
        super().__init__(f"kv write cap hit -- resumes {resets_at}")
        self.resets_at = resets_at


def post_json(url: str, body: str) -> None:
    req = urllib.request.Request(
        url, data=body.encode("utf-8"), method="POST",
        # Cloudflare's edge 403s the default Python-urllib user-agent.
        headers={"content-type": "application/json", "user-agent": "fleet-pusher/0.2"},
    )
    try:
        with urllib.request.urlopen(req, timeout=20) as resp:
            if resp.status != 200:
                raise RuntimeError(f"push status {resp.status}")
    except urllib.error.HTTPError as ex:
        if ex.code == 429:
            try:
                payload = json.loads(ex.read().decode("utf-8"))
            except (ValueError, UnicodeDecodeError):
                payload = None
            resets_at = payload.get("resets_at") if isinstance(payload, dict) else None
            if isinstance(payload, dict) and payload.get("reason") == "kv-write-cap" \
                    and isinstance(resets_at, str) and resets_at:
                raise KvWriteCapError(resets_at) from ex
        raise RuntimeError(f"push status {ex.code}") from ex


SNAPSHOT_HASH_KEY = "__snapshot_hash__"


def build_wrapped(room_list, underhood, timelines, stale_hidden_count,
                   terminal_total: int = 0, terminal_archive: list | None = None,
                   conductor: dict | None = None, pusher: dict | None = None,
                   staleness: dict | None = None, vendors: list | None = None) -> dict:
    """The exact snapshot body main() pushes. One home so the leak selftest exercises the real push
    path's construction, not a hand-rebuilt copy that could drift from it (PR #1508 review).

    `terminal_total`/`terminal_archive` (#1656) default to 0/None so every pre-existing call site
    (this module's own hash/selftest fixtures) keeps working unchanged -- callers that care about
    the hot-set split pass `room_list` as already-capped `hot_rooms` (see `split_hot_and_archive`)
    and the FULL terminal population separately here. Post-#1690 item 2, `terminal_archive` rides
    inside this SAME "snapshot" KV value (worker.js's /push handler no longer splits it into its own
    key) -- the plain (no `page`) fleet_status response still hides it, by omission on the READ side
    now rather than the write side.

    `pusher` (#1690) is an optional small object the write-budget ledger attaches -- currently only
    `{"writeBudgetExhaustedUntil": iso}` on the one final snapshot sent when the daily KV write
    budget runs out (spec/baton.md §6, "Fleet Glass write budget"). Absent on every ordinary push,
    same optional-field convention as `conductor` above -- glass.html's freshness strip reads it
    absent-safe.

    `staleness` (#1557 PR-B1) is `read_projection_file`'s own return value, forwarded verbatim
    (spec/baton.md §6, the PR-B1 passage) -- absent on every ordinary push, same optional-field
    convention as `pusher` above.

    `vendors` (#1391) is the fleet projection's own `vendors[]` block, forwarded verbatim -- absent
    whenever nothing has ever been harvested, same optional-field convention as the three above."""
    wrapped = {"rooms": room_list,
               "underhood": underhood,
               "timelines": timelines,
               "stale_hidden_count": stale_hidden_count,
               "terminal_total": terminal_total,
               "terminal_archive": terminal_archive or []}
    if conductor is not None:
        wrapped["conductor"] = conductor
    if pusher is not None:
        wrapped["pusher"] = pusher
    if staleness is not None:
        wrapped["staleness"] = staleness
    if vendors is not None:
        wrapped["vendors"] = vendors
    return wrapped


def assemble_wrapped(room_list, underhood, timelines, stale_hidden_count,
                     conductor=None, staleness=None, vendors=None):
    """main()'s post-source tail, in ONE place so both projection sources reach the pushed body
    through the same code: hot/archive split, timeline filtering, then `build_wrapped`. Returns
    `(wrapped, hot_rooms, hot_paths, terminal_total, terminal_archive, warn_line)` -- `warn_line`
    is `nonterminal_warn_line`'s own return, logged by the caller rather than here so this stays
    side-effect-free and callable from `--selftest`.

    #1557 PR-B2: extracted so the byte-identity selftest arm can push the SAME fixture through
    both the `derive` and the `file` source and compare the finished snapshots, not just the room
    dicts -- `timelines`/`terminal_total`/`stale_hidden_count`/`staleness` are top-level fields
    that never pass through `_diff_room`'s room-level comparison, and `timelines` is exactly where
    the two sources are known to differ today (that function's own comment)."""
    hot_rooms, terminal_archive, terminal_total = split_hot_and_archive(room_list or [])
    non_terminal_count = len(room_list or []) - terminal_total
    warn_line = nonterminal_warn_line(non_terminal_count)
    hot_paths = {r.get("path") for r in hot_rooms if isinstance(r, dict)}
    wrapped = build_wrapped(
        hot_rooms,
        underhood,
        {p: t for p, t in (timelines or {}).items() if p in hot_paths},
        stale_hidden_count,
        terminal_total=terminal_total,
        terminal_archive=terminal_archive,
        conductor=conductor,
        staleness=staleness,
        vendors=vendors)
    return wrapped, hot_rooms, hot_paths, terminal_total, terminal_archive, warn_line


def snapshot_post_body(wrapped: dict, derived_at: str | None) -> str:
    """Final wire serialization shared by main() and the frozen projection identity arm."""
    return json.dumps({**wrapped, "derived_at": derived_at})


def snapshot_hash(wrapped: dict) -> str:
    """Stable hash of the wrapped {rooms, underhood} body -- sort_keys so the hash does not depend
    on dict insertion order upstream, independent of the (unsorted) exact string actually POSTed."""
    return sha256_hex(json.dumps(wrapped, sort_keys=True).encode("utf-8"))


def should_push_snapshot(state: dict, current_hash: str) -> bool:
    """True unless `current_hash` matches the last SUCCESSFUL push's hash persisted under
    SNAPSHOT_HASH_KEY. A missing/unreadable persisted value (state.get returns None) always
    pushes -- fail toward one extra write, never toward silence."""
    return state.get(SNAPSHOT_HASH_KEY) != current_hash


LAST_PUSH_TS_KEY = "__last_push_ts__"
DEFAULT_MIN_PUSH_INTERVAL_S = 90


def should_coalesce_push(state: dict, now_ts: float, min_interval_s: float = DEFAULT_MIN_PUSH_INTERVAL_S) -> bool:
    """True if less than min_interval_s has elapsed since the last actual snapshot push."""
    last = state.get(LAST_PUSH_TS_KEY)
    if not isinstance(last, (int, float)):
        return False
    return (now_ts - last) < min_interval_s


LAST_DELIVER_TS_KEY = "__last_deliver_ts__"


def should_coalesce_producer(state: dict, ts_key: str, now_ts: float, min_interval_s: float) -> bool:
    """F1 (2026-09-02 review): generalises should_coalesce_push to any producer's own last-sent
    timestamp key -- deliver now gets its own adaptive pacing (`adaptive_deliver_interval_s`), not
    just a sub-budget, so it needs the same coalescing check snapshot already had."""
    last = state.get(ts_key)
    if not isinstance(last, (int, float)):
        return False
    return (now_ts - last) < min_interval_s


# ---------------------------------------------------------------------------------------------
# WRITE BUDGET LEDGER (#1690, split into per-producer sub-budgets and pacing by the 2026-09-02
# review's F1) -- a hard, pusher-owned daily cap on KV writes. Full design, the incident history,
# and the per-producer cost table: spec/baton.md §6, "Fleet Glass write budget" -- this section is
# the code that record cites, not a second copy of the reasoning.
# ---------------------------------------------------------------------------------------------

KV_DAILY_WRITE_TARGET = 700  # overall sanity ceiling, well under Cloudflare's 1,000/day free-tier KV
                              # write cap -- headroom for the ledger's own inherent granularity (a
                              # write can only be skipped whole, never partially) and anything this
                              # arithmetic hasn't modeled, per aer-works/baton#1690's "Not this
                              # issue" note (≥30% headroom or file the R2/Durable-Object exit). The
                              # per-producer sub-budgets below are what actually gates a write.
SNAPSHOT_DAILY_WRITES = 300   # F1 (2026-09-02 review): ≈288 pushes/day at the 300s coalescing
                              # floor, plus slack for the exhaustion notice.
DELIVER_DAILY_WRITES = 320    # F1: deliver's own share, paced independently so it can never out-race
                              # snapshot for a shared pool the way it did pre-fix.
HEARTBEAT_DAILY_WRITES = 60   # F1: 24 hourly beats plus room for the derived-freshness ping, now
                              # paced against this same sub-budget (`adaptive_heartbeat_interval_s`).
SNAPSHOT_KV_WRITE_COST = 1   # matches worker.js's /push handler post-#1690 item 2: ONE
                              # env.FLEET.put("snapshot", ...) per push (terminal_archive rides
                              # inside that same value, never a separate KV key or write).
DELIVER_BATCH_KV_WRITE_COST = 3  # F3(a)/F5 (2026-09-02 review): the 2 puts worker.js's /deliver
                              # always makes (inbox:batch:<id>, inbox:index), plus a conservative +1
                              # for the delete path -- either a legacy inbox:item:<id> eviction, or
                              # F5's refcounted orphaned inbox:batch:<id> reclaim. Why +1 is
                              # conservative rather than exact, and what is unverified about it, is
                              # spec/baton.md §6, "Fleet Glass write budget" -- not restated here.
HEARTBEAT_KV_WRITE_COST = 1  # matches worker.js's /heartbeat handler: one
                              # env.FLEET.put("heartbeat_at", ...) per POST, whichever of the two
                              # cadences (hourly beat, derived-freshness ping) fired it.

BUDGET_STATE_KEY = "__write_budget__"


def utc_day_str(now_ts: float) -> str:
    return datetime.fromtimestamp(now_ts, tz=timezone.utc).strftime("%Y-%m-%d")


def next_utc_midnight_iso(now_ts: float) -> str:
    """ISO-8601 instant of the next 00:00 UTC strictly after now_ts -- what a `writeBudgetExhaustedUntil`
    value names (glass's freshness strip reads it verbatim, absent-safe)."""
    now = datetime.fromtimestamp(now_ts, tz=timezone.utc)
    tomorrow = (now + timedelta(days=1)).replace(hour=0, minute=0, second=0, microsecond=0)
    return tomorrow.isoformat()


def seconds_left_in_day(now_ts: float) -> float:
    now = datetime.fromtimestamp(now_ts, tz=timezone.utc)
    tomorrow = (now + timedelta(days=1)).replace(hour=0, minute=0, second=0, microsecond=0)
    return max(0.0, (tomorrow - now).total_seconds())


def load_budget_ledger(state: dict, now_ts: float) -> dict:
    """Today's {date, snapshot, deliver, heartbeat, exhausted_notice_sent, kv_write_cap_resets_at}
    counters. A missing/corrupt persisted ledger returns a fresh, zeroed ledger for today. A stored
    date strictly EARLIER than today rolls over to a fresh, zeroed ledger for today, same as always
    -- including `kv_write_cap_resets_at` (#1829): a real Cloudflare daily cap cannot still be live
    once the ledger has rolled to a new day, so it is dropped on rollover rather than carried
    forward.

    F10 (2026-09-02 review) monotonic guard against a clock rollback -- what it guards against and
    why an all-zero stored ledger is exempt: spec/baton.md §6, "Fleet Glass write budget", not
    restated here."""
    today = utc_day_str(now_ts)
    raw = state.get(BUDGET_STATE_KEY)
    if isinstance(raw, dict) and isinstance(raw.get("date"), str):
        def _count(key: str) -> int:
            v = raw.get(key)
            return v if isinstance(v, int) and not isinstance(v, bool) else 0
        stored_date = raw["date"]
        kv_write_cap_resets_at = raw.get("kv_write_cap_resets_at")
        stored = {
            "date": stored_date,
            "snapshot": _count("snapshot"),
            "deliver": _count("deliver"),
            "heartbeat": _count("heartbeat"),
            "exhausted_notice_sent": bool(raw.get("exhausted_notice_sent", False)),
        }
        if isinstance(kv_write_cap_resets_at, str) and kv_write_cap_resets_at:
            stored["kv_write_cap_resets_at"] = kv_write_cap_resets_at
        if stored_date == today:
            return stored
        stored_used = stored["snapshot"] + stored["deliver"] + stored["heartbeat"]
        if today > stored_date or stored_used == 0:
            return {"date": today, "snapshot": 0, "deliver": 0, "heartbeat": 0, "exhausted_notice_sent": False}
        return stored  # F10: refuse the rollback -- keep serving the later, already-spent day.
    return {"date": today, "snapshot": 0, "deliver": 0, "heartbeat": 0, "exhausted_notice_sent": False}


def mark_kv_write_cap_exhausted(state: dict, now_ts: float, resets_at: str | None = None) -> dict:
    """#1712: a live 429 (reason=kv-write-cap) from the Worker is stronger evidence than the
    ledger's own count -- exhaust every producer's sub-budget for the rest of today outright, so
    the existing #1690 exhausted/skip-producer paths (snapshot_pushes_allowed / deliver_allowed /
    heartbeat_allowed) take over on the very next check for ALL THREE producers, not just whichever
    one happened to hit the cap. Also marks `exhausted_notice_sent` so the day's one final
    "budget exhausted" snapshot is never attempted -- it is exactly the write that cannot land
    either. `max(...)` rather than a plain assignment: never regresses a sub-budget that has
    already counted higher than its own daily target (shouldn't happen, but a real KV write already
    recorded must never be un-recorded).

    #1829: `resets_at`, when given, is the Worker's own `resets_at` from the 429 body -- stored on
    the ledger so the NEXT payload the pusher builds (snapshot or heartbeat) can carry it verbatim
    to the glass. This is the one REAL cap signal; glass.html's banner must key on it, not on an
    inference from two timestamps aging together."""
    ledger = load_budget_ledger(state, now_ts)
    ledger["snapshot"] = max(ledger.get("snapshot", 0), SNAPSHOT_DAILY_WRITES)
    ledger["deliver"] = max(ledger.get("deliver", 0), DELIVER_DAILY_WRITES)
    ledger["heartbeat"] = max(ledger.get("heartbeat", 0), HEARTBEAT_DAILY_WRITES)
    if isinstance(resets_at, str) and resets_at:
        # #1829: a live 429 leaves the one-shot "budget exhausted" notice (below, in main()) UNSENT
        # -- it is the one channel that still reaches the glass, since the notice reuses the
        # snapshot-push path rather than inventing a second one, and it now carries this real
        # `resets_at` alongside its own locally-computed `writeBudgetExhaustedUntil`. Without a
        # `resets_at` (the plain over-budget case, no live 429 involved), the notice is sent
        # immediately by the caller that detected it, same as before.
        ledger["kv_write_cap_resets_at"] = resets_at
    else:
        ledger["exhausted_notice_sent"] = True
    state[BUDGET_STATE_KEY] = ledger
    return ledger


def kv_write_cap_pusher_fields(ledger: dict) -> dict:
    """#1829: the pusher-side equivalent of glass.html's cap-banner classifier -- {} when the ledger
    carries no `kv_write_cap_resets_at` (no live 429 has been observed today), else
    `{"kvWriteCapResetsAt": <resets_at>}` to merge into the `pusher` object a snapshot/heartbeat
    payload sends. glass.html's banner keys on the exact same absent/present distinction, read back
    as `snap.pusher.kvWriteCapResetsAt` -- kept as one small pure function so the selftest can pin
    the decision directly rather than only through the full main() loop."""
    resets_at = ledger.get("kv_write_cap_resets_at")
    if isinstance(resets_at, str) and resets_at:
        return {"kvWriteCapResetsAt": resets_at}
    return {}


def budget_used(ledger: dict) -> int:
    return ledger.get("snapshot", 0) + ledger.get("deliver", 0) + ledger.get("heartbeat", 0)


def budget_left(ledger: dict, target: int = KV_DAILY_WRITE_TARGET) -> int:
    return max(0, target - budget_used(ledger))


def record_budget_write(state: dict, now_ts: float, producer: str, cost: int) -> dict:
    """Mutates `state[BUDGET_STATE_KEY]` in place (caller persists via save_push_state against the
    ledger's own file, F4) and returns the updated ledger. `producer` is one of
    "snapshot"/"deliver"/"heartbeat"."""
    ledger = load_budget_ledger(state, now_ts)
    ledger[producer] = ledger.get(producer, 0) + cost
    state[BUDGET_STATE_KEY] = ledger
    return ledger


def snapshot_pushes_allowed(ledger: dict, daily_budget: int = SNAPSHOT_DAILY_WRITES) -> bool:
    """False once the snapshot producer has spent its OWN sub-budget for the day -- F1 (2026-09-02
    review): no longer a shared-pool reserve, since a shared pool let deliver spend snapshot's share
    out from under it. Snapshot's sub-budget is deliberately the one that stops first: a stale but
    still-fresh-enough fleet row is a smaller loss than a silently-stopped deliverables inbox."""
    return ledger.get("snapshot", 0) < daily_budget


def deliver_allowed(ledger: dict, daily_budget: int = DELIVER_DAILY_WRITES, cost: int = DELIVER_BATCH_KV_WRITE_COST) -> bool:
    return ledger.get("deliver", 0) + cost <= daily_budget


def heartbeat_allowed(ledger: dict, daily_budget: int = HEARTBEAT_DAILY_WRITES, cost: int = HEARTBEAT_KV_WRITE_COST) -> bool:
    return ledger.get("heartbeat", 0) + cost <= daily_budget


def adaptive_producer_interval_s(
    ledger: dict, now_ts: float, producer: str, min_interval_s: float, daily_budget: int, cost: int,
) -> float:
    """F1 (2026-09-02 review): one adaptive-cadence formula shared by all three producers -- the
    taper-vs-hard-stop rationale, and why only snapshot had this pre-fix: spec/baton.md §6, "Fleet
    Glass write budget", not restated here."""
    writes_left = max(0, daily_budget - ledger.get(producer, 0))
    return max(min_interval_s, seconds_left_in_day(now_ts) / max(1, writes_left / max(1, cost)))


def adaptive_snapshot_interval_s(
    ledger: dict, now_ts: float,
    min_push_interval_s: float = DEFAULT_MIN_PUSH_INTERVAL_S,
    daily_budget: int = SNAPSHOT_DAILY_WRITES,
) -> float:
    """Snapshot's own name for `adaptive_producer_interval_s` -- kept as its own function since
    main() and the selftest both call it by this name (spec/baton.md §6 has why)."""
    return adaptive_producer_interval_s(ledger, now_ts, "snapshot", min_push_interval_s, daily_budget, SNAPSHOT_KV_WRITE_COST)


def adaptive_deliver_interval_s(
    ledger: dict, now_ts: float,
    min_deliver_interval_s: float = 0.0,
    daily_budget: int = DELIVER_DAILY_WRITES,
) -> float:
    """F1 (2026-09-02 review): deliver's own pacing. Pre-fix, deliver had no floor of its own at all
    -- a batch waiting every cycle spent its whole (shared-pool) share within the first couple of
    hours of a busy day, which is exactly the failure mode that made the old reserve useless."""
    return adaptive_producer_interval_s(ledger, now_ts, "deliver", min_deliver_interval_s, daily_budget, DELIVER_BATCH_KV_WRITE_COST)


def adaptive_heartbeat_interval_s(
    ledger: dict, now_ts: float,
    min_interval_s: float = 300.0,  # DERIVED_PING_INTERVAL_SECONDS, kept a literal default so this
                                     # function has no import-order dependency on it (defined later
                                     # in this module).
    daily_budget: int = HEARTBEAT_DAILY_WRITES,
) -> float:
    """F1 (2026-09-02 review): the derived-freshness ping's own pacing. Pre-fix, `should_send_
    derived_ping`'s fixed 300s interval meant that once the snapshot half throttled past 300s (and so
    stopped suppressing the ping via LAST_PUSH_TS_KEY), the ping fired every 300s all day -- 288
    writes against what was meant to be a small, fixed share. The hourly heartbeat beat
    (`should_send_heartbeat`) keeps its own fixed 3600s cadence -- only 24/day, a small and steady
    draw this sub-budget can always afford -- so only the ping's own interval is paced adaptively
    here; see main() and `simulate_worst_case_daily_writes` for how the two combine against one
    shared "heartbeat" ledger counter."""
    return adaptive_producer_interval_s(ledger, now_ts, "heartbeat", min_interval_s, daily_budget, HEARTBEAT_KV_WRITE_COST)


def should_log_budget(state: dict, now_ts: float, interval: float = 3600) -> bool:
    """True once at least `interval` seconds (default hourly) have elapsed since the last budget log
    line -- same fail-toward-one-extra-log posture as should_send_heartbeat."""
    last = state.get("__last_budget_log_ts__")
    if not isinstance(last, (int, float)):
        return True
    return (now_ts - last) >= interval


def format_budget_log_line(ledger: dict, interval_s: float, target: int = KV_DAILY_WRITE_TARGET) -> str:
    return (f"budget: used {budget_used(ledger)}/{target} "
            f"(snap {ledger.get('snapshot', 0)}, deliver {ledger.get('deliver', 0)}, "
            f"beat {ledger.get('heartbeat', 0)}), interval now {int(interval_s)}s")


def simulate_worst_case_daily_writes(
    interval_seconds: float = 25,
    min_push_interval_s: float = DEFAULT_MIN_PUSH_INTERVAL_S,
    min_deliver_interval_s: float = 0.0,
    ledger_enabled: bool = True,
    snapshot_daily_writes: int = SNAPSHOT_DAILY_WRITES,
    deliver_daily_writes: int = DELIVER_DAILY_WRITES,
    heartbeat_daily_writes: int = HEARTBEAT_DAILY_WRITES,
    snapshot_cost: int = SNAPSHOT_KV_WRITE_COST,
    deliver_cost=DELIVER_BATCH_KV_WRITE_COST,  # int, OR (F2's pre-#1690 red control) a
                                                # callable(batch_size) -> int for the K+1 shape.
    deliver_batch_size: int = 10,              # only meaningful when deliver_cost is callable.
    heartbeat_cost: int = HEARTBEAT_KV_WRITE_COST,
    deliver_ping_interval_s: float = 300,  # DERIVED_PING_INTERVAL_SECONDS, kept a literal default so
                                            # this function has no import-order dependency on it
    heartbeat_interval_s: float = 3600,    # HEARTBEAT_INTERVAL_SECONDS, same reason
) -> dict:
    """#1690 item 4, the arithmetic gate -- widened by the 2026-09-02 review's F1/F2 from a single
    total into per-producer write TIMESTAMPS; why a total alone cannot catch what F1 found: spec/
    baton.md §6, "Fleet Glass write budget", not restated here. `main()`'s own gating functions drive
    this simulation, so it cannot drift from what actually ships.

    `ledger_enabled=False` (F2's red control) bypasses every gating check. Returns `{"ledger":
    {...final counters...}, "snapshot_write_ts": [...], "deliver_write_ts": [...],
    "heartbeat_write_ts": [...]}` -- the caller asserts both `budget_used(result["ledger"]) <=
    KV_DAILY_WRITE_TARGET` and a distribution bound over the write-timestamp lists. A synthetic day
    starting at epoch 0 -- any fixed UTC-day start works, since every gating function here only ever
    measures elapsed time, never wall-clock identity."""
    state: dict = {}
    now_ts = 0.0
    day_end = 86400.0
    last_snapshot_push_ts: float | None = None
    last_deliver_push_ts: float | None = None
    snapshot_write_ts: list[float] = []
    deliver_write_ts: list[float] = []
    heartbeat_write_ts: list[float] = []

    def _deliver_cost_now() -> int:
        return deliver_cost(deliver_batch_size) if callable(deliver_cost) else deliver_cost

    while now_ts < day_end:
        ledger = load_budget_ledger(state, now_ts)
        snap_ok = (not ledger_enabled) or snapshot_pushes_allowed(ledger, snapshot_daily_writes)
        if snap_ok:
            interval = (adaptive_snapshot_interval_s(ledger, now_ts, min_push_interval_s, snapshot_daily_writes)
                        if ledger_enabled else min_push_interval_s)
            if last_snapshot_push_ts is None or (now_ts - last_snapshot_push_ts) >= interval:
                record_budget_write(state, now_ts, "snapshot", snapshot_cost)
                last_snapshot_push_ts = now_ts
                snapshot_write_ts.append(now_ts)
                state[LAST_PUSH_TS_KEY] = now_ts  # mirrors push_snapshot_and_record's own side
                                                    # effect -- suppresses a redundant derived-ping
                                                    # below, exactly like the real loop.

        ledger = load_budget_ledger(state, now_ts)
        this_deliver_cost = _deliver_cost_now()
        deliver_ok = (not ledger_enabled) or deliver_allowed(ledger, deliver_daily_writes, this_deliver_cost)
        if deliver_ok:
            d_interval = (adaptive_deliver_interval_s(ledger, now_ts, min_deliver_interval_s, deliver_daily_writes)
                          if ledger_enabled else 0.0)
            if last_deliver_push_ts is None or (now_ts - last_deliver_push_ts) >= d_interval:
                record_budget_write(state, now_ts, "deliver", this_deliver_cost)
                last_deliver_push_ts = now_ts
                deliver_write_ts.append(now_ts)

        ledger = load_budget_ledger(state, now_ts)
        hb_interval = (adaptive_heartbeat_interval_s(ledger, now_ts, deliver_ping_interval_s, heartbeat_daily_writes)
                       if ledger_enabled else deliver_ping_interval_s)
        heartbeat_due = should_send_heartbeat(state, now_ts, heartbeat_interval_s)
        ping_due = should_send_derived_ping(state, now_ts, hb_interval)
        hb_ok = (not ledger_enabled) or heartbeat_allowed(ledger, heartbeat_daily_writes, heartbeat_cost)
        if (heartbeat_due or ping_due) and hb_ok:
            record_budget_write(state, now_ts, "heartbeat", heartbeat_cost)
            heartbeat_write_ts.append(now_ts)
            if heartbeat_due:
                state[HEARTBEAT_STATE_KEY] = now_ts
            if ping_due:
                state[DERIVED_PING_STATE_KEY] = now_ts

        now_ts += interval_seconds
    return {
        "ledger": load_budget_ledger(state, day_end - 1),
        "snapshot_write_ts": snapshot_write_ts,
        "deliver_write_ts": deliver_write_ts,
        "heartbeat_write_ts": heartbeat_write_ts,
    }


def push_snapshot_and_record(post, body: str, state: dict, state_path, current_hash: str, now_ts: float | None = None) -> None:
    """POST first, record the hash and push timestamp ONLY afterwards. This ordering is the
    change-gate's single most safety-critical property (a hash persisted for a FAILED push would
    gate every retry and go silent until the next content change), so it lives in one testable
    function instead of inline in main()'s loop -- the selftest proves a raising `post` leaves the
    state file untouched."""
    post(body)
    state[SNAPSHOT_HASH_KEY] = current_hash
    if now_ts is None:
        now_ts = time.time()
    state[LAST_PUSH_TS_KEY] = now_ts
    save_push_state(state_path, state)


def should_log_skip(streak: int, log_every: int) -> bool:
    """First skip in a streak logs immediately (so 'now skipping' is visible right away); after
    that, only every `log_every`th cycle -- keeps pusher.log from being mostly skip lines across a
    quiet fleet while still proving the loop is alive, given the 1MB truncation behavior."""
    return streak == 1 or streak % log_every == 0


def derive_deliver_url(cfg: dict) -> str | None:
    if cfg.get("deliver_url"):
        return cfg["deliver_url"]
    push_url = cfg.get("push_url", "")
    if "/push/" in push_url:
        return push_url.replace("/push/", "/deliver/", 1)
    return None


HEARTBEAT_STATE_KEY = "__last_heartbeat_ts__"
HEARTBEAT_INTERVAL_SECONDS = 3600  # hourly cadence; gated against the write-budget ledger like every
                                    # other producer (`heartbeat_allowed`) -- see the module
                                    # docstring's "THE HEARTBEAT HALF" section and spec/baton.md §6,
                                    # "Fleet Glass write budget".


def derive_heartbeat_url(cfg: dict) -> str | None:
    if cfg.get("heartbeat_url"):
        return cfg["heartbeat_url"]
    push_url = cfg.get("push_url", "")
    if "/push/" in push_url:
        return push_url.replace("/push/", "/heartbeat/", 1)
    return None


def should_send_heartbeat(state: dict, now_ts: float, interval: float = HEARTBEAT_INTERVAL_SECONDS) -> bool:
    """True once at least `interval` seconds have elapsed since the last recorded heartbeat.
    A missing/unreadable persisted timestamp always sends -- same fail-toward-one-extra-write
    posture as should_push_snapshot."""
    last = state.get(HEARTBEAT_STATE_KEY)
    if not isinstance(last, (int, float)):
        return True
    return (now_ts - last) >= interval


def send_heartbeat_and_record(post, state: dict, state_path, now_ts: float, extra_state: dict | None = None) -> None:
    """POST first, record only afterwards -- same ordering discipline as push_snapshot_and_record
    (a raising `post` must leave `state` untouched, so a failed heartbeat retries next cycle
    instead of going silent). `extra_state` (#1613 item 2) lets one physical POST also stamp a
    second, independently-gated cadence's own state key (see `should_send_derived_ping` below)
    without a second network round trip -- merged in only after `post` succeeds, same
    all-or-nothing ordering as HEARTBEAT_STATE_KEY itself."""
    post()
    state[HEARTBEAT_STATE_KEY] = now_ts
    if extra_state:
        state.update(extra_state)
    save_push_state(state_path, state)


# ---------------------------------------------------------------------------------------------
# derived_at (#1613 item 2): what it is and why the glass banner keys on it instead of pushed_at
# is spec/baton.md §6's `derived_at` schema entry, not restated here. `pending_push_age_s`
# (this review's finding 2) rides the SAME `/heartbeat` ping body: derived_at alone cannot tell "the
# fleet is quiet" apart from "derivation keeps succeeding but every PUSH keeps failing" (a 413 from
# the 1 MB cap, a 5xx, a network blip) -- see `pending_push_age_s`'s own docstring below.
#
# Budget: derived_at must reach the server far more often than heartbeat_at's own hourly cadence to
# be a useful "stuck" signal, but a naive fixed-interval ping alongside the change-gated snapshot
# writes would blow the write-budget ledger's KV_DAILY_WRITE_TARGET (spec/baton.md §6, "Fleet Glass
# write budget") on its own. The two writes are made mutually exclusive per cycle instead of
# additive: an actual snapshot PUSH already carries a fresh derived_at in its own body (excluded
# from `snapshot_hash` so it never forces a push on its own -- see `main()`'s push branch), so
# `should_send_derived_ping` below only fires the dedicated ping when NEITHER a push nor a prior
# ping has landed one recently -- and even then, only when `heartbeat_allowed` says the ledger still
# has room. A day spent constantly pushing never also pays the ping's cost (it wouldn't fire); a
# quiet day pays the ping's cost instead, capped by the same ledger either way.
# ---------------------------------------------------------------------------------------------

DERIVED_PING_STATE_KEY = "__last_derived_ping_ts__"
DERIVED_PING_INTERVAL_SECONDS = 300  # 5 minutes -- well under the glass's RUNNING_SUSPICION_MS
                                      # (10 minutes) "stuck" threshold, so a genuinely wedged
                                      # derivation is caught on roughly the same timescale the
                                      # banner already used pre-#1613, not degraded to it.


def should_send_derived_ping(state: dict, now_ts: float, interval: float = DERIVED_PING_INTERVAL_SECONDS) -> bool:
    """True once `interval` seconds have elapsed since derived_at last reached the server by
    EITHER channel: an actual snapshot push (LAST_PUSH_TS_KEY) or a prior dedicated ping
    (DERIVED_PING_STATE_KEY) -- whichever is more recent. A missing/unreadable timestamp on both
    counts as "never landed" -- fail toward sending one extra ping, never toward silence, same
    posture as should_send_heartbeat/should_push_snapshot."""
    landed_via_push = state.get(LAST_PUSH_TS_KEY)
    landed_via_ping = state.get(DERIVED_PING_STATE_KEY)
    candidates = [t for t in (landed_via_push, landed_via_ping) if isinstance(t, (int, float))]
    if not candidates:
        return True
    return (now_ts - max(candidates)) >= interval


def pending_push_age_s(state: dict, current_hash: str, now_ts: float) -> float | None:
    """Seconds since the last SUCCESSFUL push, but ONLY when there is content actually waiting to go
    out (`should_push_snapshot` says the persisted hash no longer matches `current_hash`) -- this
    review's finding 2. A quiet, healthy fleet reports `None` here even though its own last push may
    genuinely have been hours ago; that is the whole point -- it is what lets glass tell "nothing to
    push" apart from "wants to push and can't", which `derived_at` alone cannot do (derivation
    succeeds every cycle regardless of whether the following POST does). A missing/unreadable
    LAST_PUSH_TS_KEY while content IS waiting also reports `None`: there is no successful-push
    baseline yet to measure age from (this process's first cycle), and reporting an arbitrary number
    here would be a fabricated figure, not an absent one -- same never-fabricate convention as every
    other optional field in this module. Because `push_snapshot_and_record` only updates
    LAST_PUSH_TS_KEY AFTER a successful POST, a run of failing pushes leaves it frozen, so this value
    grows cycle over cycle for as long as the failures continue -- exactly the "growing pending age"
    signal the heartbeat ping is meant to carry."""
    if not should_push_snapshot(state, current_hash):
        return None
    last = state.get(LAST_PUSH_TS_KEY)
    if not isinstance(last, (int, float)):
        return None
    return now_ts - last


# ---------------------------------------------------------------------------------------------
# Deliverables: terminal-room scan, secret gate, dedupe (#1413 half 2)
# ---------------------------------------------------------------------------------------------

def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def load_secret_patterns(path: Path) -> list[re.Pattern] | None:
    """Compiled denylist from a plain-text file (one Python regex per line; '#' starts a comment,
    blank lines ignored). Returns None -- the fail-closed sentinel -- if the file is missing or
    cannot be read/parsed.

    None is NOT the same as an empty list: an empty, present, readable file (a deliberate "nothing
    to withhold on" choice) returns []. Only an absent or broken file returns None, and every caller
    of this function must treat None as "withhold everything", per the owner's fail-closed ruling.
    """
    try:
        raw = path.read_text(encoding="utf-8")
    except OSError:
        return None
    patterns = []
    try:
        for line in raw.splitlines():
            stripped = line.strip()
            if not stripped or stripped.startswith("#"):
                continue
            patterns.append(re.compile(stripped))
    except re.error:
        return None
    return patterns


def secret_hit_index(text: str, patterns: list[re.Pattern]) -> int | None:
    """Index of the first pattern (in file order) that matches anywhere in text, else None."""
    for i, pattern in enumerate(patterns):
        if pattern.search(text):
            return i
    return None


def extract_title(text: str, fallback: str) -> str:
    """The file's first markdown heading (`# Title`), else the fallback (its filename)."""
    for line in text.splitlines():
        m = re.match(r"^#\s+(.+?)\s*$", line)
        if m:
            return m.group(1)
    return fallback


STATE_FORMAT_VERSION_KEY = "__format_version__"
CURRENT_STATE_FORMAT_VERSION = 2
DEFAULT_DELIVER_BATCH_COUNT_CEILING = 2000  # F13 (2026-09-02 review): a generous backstop on loop
                                             # iterations only -- DEFAULT_DELIVER_BATCH_BYTES below
                                             # is what actually constrains a batch now that a flat
                                             # DELIVER_BATCH_KV_WRITE_COST no longer scales with item
                                             # count. Kept under the parameter name `limit` for
                                             # backward compatibility with existing callers/selftest.
DEFAULT_DELIVER_BATCH_BYTES = 4_000_000     # F13: ~4MB, safely under worker.js's 5,000,000-char
                                             # /deliver body cap (worker.js:190) with room for JSON
                                             # structure/metadata overhead around each item's content.


def load_push_state(path: Path) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def save_push_state(path: Path, state: dict) -> None:
    """F4 (2026-09-02 review): write to a sibling temp file and `os.replace` it into place, atomic
    on both Windows and POSIX -- why `write_text`'s truncate-then-write was unsafe here: spec/
    baton.md §6, "Fleet Glass write budget", not restated here."""
    if STATE_FORMAT_VERSION_KEY not in state:
        state[STATE_FORMAT_VERSION_KEY] = CURRENT_STATE_FORMAT_VERSION
    tmp_path = path.parent / f"{path.name}.tmp"
    tmp_path.write_text(json.dumps(state, indent=2, sort_keys=True), encoding="utf-8")
    os.replace(tmp_path, path)


def migrate_push_state(
    state: dict,
    terminal_rooms: list[tuple[str, str, Path]],
    state_path: Path | None = None,
) -> bool:
    """Migrate legacy push-state keys from f"{room_name}::{artifact}" to f"{room_path}::{artifact}".

    For each terminal room:
    - If a legacy key is present and the corresponding path key is absent, adopt the legacy value
      under the path key and drop the legacy key, then persist.
    - A legacy key whose name matches two current rooms (the exact collision #1617 is about)
      must NOT be adopted for either — log it and let both re-push once.
    - Also records the state-file format version (__format_version__ = 2).
    """
    name_to_rooms: dict[str, list[tuple[str, str, Path]]] = {}
    for room_path, room_name, room_dir in terminal_rooms:
        name_to_rooms.setdefault(room_name, []).append((room_path, room_name, room_dir))

    changed = False
    for k in list(state.keys()):
        if k.startswith("__") or "::" not in k:
            continue
        prefix, artifact = k.split("::", 1)
        if prefix in name_to_rooms:
            rooms_for_name = name_to_rooms[prefix]
            if len(rooms_for_name) == 1:
                room_path, _, _ = rooms_for_name[0]
                if prefix != room_path:
                    path_key = f"{room_path}::{artifact}"
                    if path_key not in state:
                        state[path_key] = state[k]
                    del state[k]
                    changed = True
            else:
                paths_str = ", ".join(r[0] for r in rooms_for_name)
                log(f"migration: legacy key '{k}' matches {len(rooms_for_name)} rooms ({paths_str}); not adopting, will re-push")
                del state[k]
                changed = True

    if state.get(STATE_FORMAT_VERSION_KEY) != CURRENT_STATE_FORMAT_VERSION:
        state[STATE_FORMAT_VERSION_KEY] = CURRENT_STATE_FORMAT_VERSION
        changed = True

    if changed and state_path is not None:
        save_push_state(state_path, state)

    return changed


def find_terminal_rooms(rooms_root: Path) -> list[tuple[str, str, Path]]:
    """(room_path, room_name, room_dir) for every room directory that carries a terminal.json.

    A room with no terminal.json is still running (or was never dispatched) -- outside this
    function's job, which is only to find TERMINAL rooms; the fleet snapshot half already covers
    in-flight state.
    """
    if not rooms_root.is_dir():
        return []
    found = []
    for child in sorted(rooms_root.iterdir()):
        if child.is_dir() and (child / "terminal.json").is_file():
            found.append((str(child), child.name, child))
    return found


def load_terminal(room_dir: Path) -> dict | None:
    try:
        return json.loads((room_dir / "terminal.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def verdict_summary(terminal: dict) -> dict:
    return {
        "state": terminal.get("state"),
        "error": terminal.get("error"),
        "try": terminal.get("try"),
    }


def declared_outputs(terminal: dict) -> list[Path]:
    """terminal.json's own "outputs" list -- the room's declared `--output` artifact(s), and the
    ONLY thing this pusher ever reads out of a room's artifacts directory. Never prompt.txt, never
    .stdout.log: those simply never appear in this list, because it is not a directory walk."""
    return [Path(p) for p in terminal.get("outputs", []) if isinstance(p, str) and p]


def _apply_secret_gate(content_bytes: bytes, local_path: str, patterns: list[re.Pattern] | None):
    """Returns (content_to_upload, withheld, stub_reason, pattern_index) for one artifact's bytes.

    Fails CLOSED: `patterns is None` (the load_secret_patterns sentinel) withholds unconditionally,
    stub reason names the missing file rather than a matched pattern. NEVER logs or uploads a
    pattern's own text -- only its index -- so the denylist itself never leaks through the log or
    the mailbox.
    """
    if patterns is None:
        return (
            f"withheld — secret-pattern file missing, read locally: {local_path}",
            True, "patterns file missing", None,
        )
    text = content_bytes.decode("utf-8", errors="replace")
    hit = secret_hit_index(text, patterns)
    if hit is not None:
        return (
            f"withheld — secret-pattern match, read locally: {local_path}",
            True, f"matched pattern #{hit}", hit,
        )
    return text, False, None, None


def build_item(room_path: str, room_dir: Path, artifact_path: Path, verdict: dict,
                patterns: list[re.Pattern] | None, room_name: str | None = None) -> dict:
    """One deliverable for a declared output artifact. `artifact_path` is absolute (terminal.json
    stores absolute paths); the item's "artifact" field is that path relative to the room dir, so
    dedupe keys and inbox rows never carry the operator's home directory.

    Keyed by room PATH (#1617, matching the snapshot half's timeline join; room_name is kept for
    display only)."""
    if room_name is None:
        room_name = room_dir.name if room_dir is not None else Path(room_path).name
    try:
        raw = artifact_path.read_bytes()
    except OSError as ex:
        raw = f"(unreadable: {ex})".encode("utf-8")
    content_hash = sha256_hex(raw)
    try:
        rel = artifact_path.relative_to(room_dir).as_posix()
    except ValueError:
        rel = artifact_path.name
    content, withheld, stub_reason, pattern_index = _apply_secret_gate(raw, str(artifact_path), patterns)
    title = extract_title(raw.decode("utf-8", errors="replace"), artifact_path.name) if not withheld else artifact_path.name
    item = {
        "id": f"{room_path}::{rel}::{content_hash[:16]}",
        "room": room_path,
        "room_name": room_name,
        "artifact": rel,
        "title": title,
        "content_hash": content_hash,
        "withheld": withheld,
        "verdict": verdict,
        "content": content,
    }
    try:
        st = artifact_path.stat()
        item["created_at"] = datetime.fromtimestamp(st.st_mtime, timezone.utc).isoformat()
    except (OSError, ValueError):
        pass
    if stub_reason:
        item["stub_reason"] = stub_reason
    if pattern_index is not None:
        log(f"secret-gate: {room_name}/{rel} matched pattern #{pattern_index}: withheld")
    return item


def build_verdict_only_item(room_path: str, verdict: dict, room_dir: Path | None = None,
                            room_name: str | None = None) -> dict:
    """A room with zero declared outputs (typically Failed) still gets one inbox entry, so a
    failure with nothing to show is still visible rather than silently absent.

    Keyed by room PATH (#1617, matching the snapshot half's timeline join; room_name is kept for
    display only)."""
    if room_name is None:
        room_name = room_dir.name if room_dir is not None else Path(room_path).name
    text = json.dumps(verdict, indent=2, sort_keys=True)
    content_hash = sha256_hex(text.encode("utf-8"))
    item = {
        "id": f"{room_path}::__verdict__::{content_hash[:16]}",
        "room": room_path,
        "room_name": room_name,
        "artifact": None,
        "title": f"{room_name} — {verdict.get('state') or 'unknown'}",
        "content_hash": content_hash,
        "withheld": False,
        "verdict": verdict,
        "content": text,
    }
    if room_dir is not None:
        try:
            st = (Path(room_dir) / "terminal.json").stat()
            item["created_at"] = datetime.fromtimestamp(st.st_mtime, timezone.utc).isoformat()
        except (OSError, ValueError):
            pass
    return item


def _item_content_bytes(item: dict) -> int:
    content = item.get("content")
    return len(content.encode("utf-8")) if isinstance(content, str) else 0


def gather_conductor_deliverables(
    rooms_root: Path,
    state: dict,
    patterns: list[re.Pattern] | None,
    limit: int | None = DEFAULT_DELIVER_BATCH_COUNT_CEILING,
    max_bytes: int = DEFAULT_DELIVER_BATCH_BYTES,
) -> tuple[list[dict], int]:
    """Scans manifest.jsonl from the standing conductor room (or any conductor room under rooms_root)
    and gathers deliverable items with kind='conductor' and id derived from source_path (#1669).

    F13 (2026-09-02 review): capped by cumulative content BYTES (`max_bytes`), not just item count --
    `limit` remains a generous backstop on loop iterations. Returns `(items, total_bytes)` so a
    caller batching conductor and terminal-room items together (`gather_deliverables`) can carry the
    running byte total forward into its own loop rather than re-summing. At least one item is always
    admitted even if it alone exceeds `max_bytes` (fail toward one oversized batch, never toward
    silently dropping the only thing an operator has to look at)."""
    items = []
    total_bytes = 0
    conductor_dirs = []
    conductor_default = rooms_root / "conductor"
    if conductor_default.is_dir():
        conductor_dirs.append(conductor_default)

    if rooms_root.is_dir():
        try:
            for child in rooms_root.iterdir():
                if child.is_dir() and child != conductor_default:
                    if (child / "artifacts" / "conductor" / "manifest.jsonl").is_file():
                        conductor_dirs.append(child)
        except OSError:
            pass

    for conductor_dir in conductor_dirs:
        manifest_path = conductor_dir / "artifacts" / "conductor" / "manifest.jsonl"
        if not manifest_path.is_file():
            continue

        conductor_room_path = str(conductor_dir)
        conductor_artifacts_dir = conductor_dir / "artifacts" / "conductor"

        try:
            lines = manifest_path.read_text(encoding="utf-8-sig").splitlines()
        except Exception as ex:  # noqa: BLE001
            log(f"conductor manifest read error for {conductor_dir}: {type(ex).__name__}: {ex}")
            continue

        for line_num, line in enumerate(lines, start=1):
            if (limit is not None and len(items) >= limit) or total_bytes >= max_bytes:
                break
            if not line.strip():
                continue
            try:
                entry = json.loads(line)
            except json.JSONDecodeError as ex:
                log(f"conductor manifest JSONDecodeError in {conductor_dir} line {line_num}: {ex}")
                continue
            except Exception:
                continue
            if not isinstance(entry, dict):
                continue

            source_path = entry.get("source_path")
            if not isinstance(source_path, str) or not source_path:
                continue

            # F1 (2026-09-02 review): artifact_file is read from the manifest line, never
            # re-derived from the basename — DeliverCommand.cs keys the on-disk filename off a hash
            # of source_path precisely so two sources sharing a basename land on two distinct files;
            # re-deriving here would silently collapse them back onto one.
            artifact_file_name = entry.get("artifact_file")
            if not isinstance(artifact_file_name, str) or not artifact_file_name:
                continue

            basename = Path(source_path).name
            artifact_file = conductor_artifacts_dir / artifact_file_name
            if not artifact_file.is_file():
                continue

            try:
                raw = artifact_file.read_bytes()
            except Exception:
                continue

            content_hash = sha256_hex(raw)
            key = f"{conductor_room_path}::artifacts/conductor/{artifact_file_name}"
            if state.get(key) == content_hash:
                continue

            content, withheld, stub_reason, pattern_index = _apply_secret_gate(
                raw, str(artifact_file), patterns)

            title = entry.get("title") or basename
            delivered_at = entry.get("delivered_at")
            if not delivered_at:
                try:
                    st = artifact_file.stat()
                    delivered_at = datetime.fromtimestamp(st.st_mtime, timezone.utc).isoformat()
                except (OSError, ValueError):
                    delivered_at = datetime.now(timezone.utc).isoformat()

            item = {
                "id": f"{conductor_room_path}::conductor::{source_path}",
                "kind": "conductor",
                "room": conductor_room_path,
                "room_name": "conductor",
                "artifact": f"artifacts/conductor/{artifact_file_name}",
                "source_path": source_path,
                "title": title,
                "content_hash": content_hash,
                "withheld": withheld,
                "verdict": {"state": "Succeeded"},
                "content": content,
                "created_at": delivered_at,
            }
            if stub_reason:
                item["stub_reason"] = stub_reason
            item_bytes = _item_content_bytes(item)
            if items and total_bytes + item_bytes > max_bytes:
                break
            items.append(item)
            total_bytes += item_bytes

    return items, total_bytes


def gather_deliverables(
    rooms_root: Path,
    state: dict,
    patterns: list[re.Pattern] | None,
    state_path: Path | None = None,
    limit: int | None = DEFAULT_DELIVER_BATCH_COUNT_CEILING,
    max_bytes: int = DEFAULT_DELIVER_BATCH_BYTES,
) -> list[dict]:
    """Every not-yet-pushed deliverable across all terminal rooms and conductor rooms under
    rooms_root, capped by cumulative content BYTES (`max_bytes`, F13 -- 2026-09-02 review) with
    `limit` as a generous item-count backstop, not the primary constraint -- why bytes rather than
    count: spec/baton.md §6, "Fleet Glass write budget", not restated here.

    Migrates legacy room_name-keyed state entries to room_path keys before lookup (#1617 / PR #1632).
    "not yet pushed" is decided per (room_path, artifact) against `state[key] == content_hash` -- an
    unchanged hash is skipped. Deliberately NOT memorized into `state` here (the caller does that,
    only after a successful network push): when `patterns is None`, every item this run is withheld
    for that reason alone, and it must be re-offered on the NEXT run too, in case an operator has
    fixed the patterns file by then -- see `load_secret_patterns`.
    """
    if patterns is None:
        log("secret-gate: secret_patterns_file missing/unreadable — WITHHOLDING EVERYTHING this run (fail closed)")

    items = []
    conductor_items, total_bytes = gather_conductor_deliverables(rooms_root, state, patterns, limit=limit, max_bytes=max_bytes)
    items.extend(conductor_items)

    terminal_rooms = find_terminal_rooms(rooms_root)
    migrate_push_state(state, terminal_rooms, state_path=state_path)

    for room_path, room_name, room_dir in terminal_rooms:
        if (limit is not None and len(items) >= limit) or total_bytes >= max_bytes:
            break
        terminal = load_terminal(room_dir)
        if terminal is None:
            continue
        verdict = verdict_summary(terminal)
        outputs = declared_outputs(terminal)
        if not outputs:
            item = build_verdict_only_item(room_path, verdict, room_dir, room_name=room_name)
            key = f"{room_path}::{item['artifact']}"
            if state.get(key) != item["content_hash"]:
                item_bytes = _item_content_bytes(item)
                if items and total_bytes + item_bytes > max_bytes:
                    break
                items.append(item)
                total_bytes += item_bytes
            continue
        for artifact_path in outputs:
            if (limit is not None and len(items) >= limit) or total_bytes >= max_bytes:
                break
            item = build_item(room_path, room_dir, artifact_path, verdict, patterns, room_name=room_name)
            key = f"{room_path}::{item['artifact']}"
            if state.get(key) != item["content_hash"]:
                item_bytes = _item_content_bytes(item)
                if items and total_bytes + item_bytes > max_bytes:
                    break
                items.append(item)
                total_bytes += item_bytes
    return items


def mark_pushed(state: dict, items: list[dict]) -> dict:
    """New state dict with each item's (room_path, artifact) -> content_hash recorded. Pure, so callers
    control exactly when a successful push is allowed to count as "seen"."""
    updated = dict(state)
    for item in items:
        updated[f"{item['room']}::{item['artifact']}"] = item["content_hash"]
    return updated


# ---------------------------------------------------------------------------------------------
# NTFY PUSH -- severity tiers, quiet hours, dedup (#1558, ratified #1502 items 31/32/33: the three
# ship together or not at all). Independent of the Cloudflare mailbox above -- ntfy.sh (or a
# self-hosted instance) is a separate outbound POST, no write-budget ledger, no secret gate (the
# title/message this module builds is a short, content-free-by-construction line naming the event,
# never stdout/prompt text).
#
# Config (pusher.config.json, beside the existing keys):
#     "ntfy_topic": "<topic>",                # required to enable; a missing/blank topic disables
#                                              # the feature silently (one startup log line only)
#     "ntfy_server": "https://ntfy.sh",       # optional; self-hosted instances override this
#     "ntfy_quiet_hours": {                   # optional; omit for no quiet hours at all
#       "start": "22:00", "end": "07:00",     # operator-local ET, HH:MM 24h, wraps past midnight
#       "timezone": "America/New_York"
#     }
# `ntfy_token` (a self-hosted instance's auth token), if needed, lives in secrets.local.json next
# to this script -- {"ntfy_token": "..."} -- per the existing secrets.local.json pattern (gitignored;
# never the ntfy topic itself, which is not a secret in the same sense a push token is, but stays out
# of pusher.config.json's *.example.* sibling regardless).
#
# TIERS -> NTFY PRIORITY. One table; every caller of `ntfy_priority_for_event` reads this, nothing
# restates it (spec/baton.md §6 cites this table rather than repeating it).
# ---------------------------------------------------------------------------------------------

NTFY_DEFAULT_SERVER = "https://ntfy.sh"
NTFY_DEFAULT_STATE_FILE = HERE / "ntfy-state.local.json"

#: event type -> (ntfy priority string, human tier name). ntfy's own priority vocabulary is
#: min/low/default/high/urgent -- this project only ever emits three of the five (spec/baton.md §6).
NTFY_EVENT_TIERS: dict[str, str] = {
    "lane_failed": "urgent",
    "lane_succeeded_with_warnings": "default",
    "zombie_detected": "high",
    "pusher_anomaly": "high",
}


def ntfy_priority_for_event(event_type: str) -> str:
    """The ntfy `Priority` header value for an event type. Unknown event types fail toward "default"
    rather than raising or silently going urgent -- a typo'd event type should never train the
    operator to ignore urgent pushes, nor should it interrupt a dinner over nothing named here."""
    return NTFY_EVENT_TIERS.get(event_type, "default")


def _parse_hhmm(value: str) -> tuple[int, int]:
    hh, mm = value.split(":")
    return int(hh), int(mm)


#: US Eastern DST fallback, used ONLY when the stdlib `zoneinfo` lookup itself fails -- measured on
#: this project's own dev box (Windows, Python 3.12, no `tzdata` package installed): CPython's
#: `zoneinfo` ships no bundled tz database on Windows and raises `ZoneInfoNotFoundError` (a `KeyError`
#: subclass) for EVERY key, including "America/New_York", until the `tzdata` PyPI package is
#: installed. Rather than make quiet hours silently never fire on an un-provisioned Windows operator
#: box -- the exact platform this project's CI targets -- this computes the standard US DST rule
#: (2nd Sunday of March 02:00 local -> 1st Sunday of November 02:00 local, UTC-5/UTC-4) directly, for
#: "America/New_York" and its common aliases only. Any OTHER configured timezone with no `tzdata`
#: available still fails toward "never quiet" (the `except` below), same as before this fallback
#: existed -- this is a targeted fix for the one zone the config's own default and this issue name,
#: not a general US-timezone engine.
_US_EASTERN_ALIASES = frozenset({"America/New_York", "US/Eastern"})

#: zone names already logged as unresolvable this process -- `in_quiet_hours` runs once per room
#: per cycle, so an un-provisioned non-Eastern zone would otherwise log the same line every call;
#: this caps it at one line per zone name for the life of the process.
_LOGGED_UNRESOLVABLE_TZ_NAMES: set[str] = set()


def _us_eastern_offset_fallback(now_utc: datetime) -> timedelta:
    year = now_utc.year
    march1 = datetime(year, 3, 1, tzinfo=timezone.utc)
    second_sunday_march = march1 + timedelta(days=(6 - march1.weekday()) % 7 + 7)
    dst_start = second_sunday_march.replace(hour=7)  # 02:00 EST = 07:00 UTC
    nov1 = datetime(year, 11, 1, tzinfo=timezone.utc)
    first_sunday_nov = nov1 + timedelta(days=(6 - nov1.weekday()) % 7)
    dst_end = first_sunday_nov.replace(hour=6)  # 02:00 EDT = 06:00 UTC
    is_dst = dst_start <= now_utc < dst_end
    return timedelta(hours=-4) if is_dst else timedelta(hours=-5)


def in_quiet_hours(cfg: dict, now: datetime) -> bool:
    """True when `now` (any tz-aware datetime; converted to the configured zone) falls inside the
    configured quiet-hours window. No `ntfy_quiet_hours` in config -> never quiet (fail toward
    delivering, not toward silence). Handles a window that wraps past midnight (e.g. 22:00-07:00).
    `now` is the injection point for tests -- never `datetime.now()` called from inside here."""
    qh = cfg.get("ntfy_quiet_hours")
    if not qh:
        return False
    tz_name = qh.get("timezone", "America/New_York")
    try:
        from zoneinfo import ZoneInfo
        local = now.astimezone(ZoneInfo(tz_name))
        start_h, start_m = _parse_hhmm(qh["start"])
        end_h, end_m = _parse_hhmm(qh["end"])
    except KeyError as ex:
        # ZoneInfoNotFoundError is itself a KeyError subclass (missing tzdata, or a bad name); a
        # bare `qh["start"]`/`qh["end"]` KeyError takes the same fail-toward-"never quiet" exit.
        if tz_name in _US_EASTERN_ALIASES and "start" in qh and "end" in qh:
            local = (now.astimezone(timezone.utc) + _us_eastern_offset_fallback(now.astimezone(timezone.utc)))
            start_h, start_m = _parse_hhmm(qh["start"])
            end_h, end_m = _parse_hhmm(qh["end"])
        else:
            if tz_name not in _LOGGED_UNRESOLVABLE_TZ_NAMES:
                _LOGGED_UNRESOLVABLE_TZ_NAMES.add(tz_name)
                log(f"ntfy: quiet hours timezone {tz_name!r} unresolvable ({ex}) -- treating as never quiet")
            return False
    except (ValueError, OSError):
        return False
    start_minutes = start_h * 60 + start_m
    end_minutes = end_h * 60 + end_m
    now_minutes = local.hour * 60 + local.minute
    if start_minutes == end_minutes:
        return False  # a zero-width window is not a window
    if start_minutes < end_minutes:
        return start_minutes <= now_minutes < end_minutes
    # wraps past midnight, e.g. 22:00-07:00
    return now_minutes >= start_minutes or now_minutes < end_minutes


#: dedup decisions `ntfy_dedup_decision` can return.
NTFY_DEDUP_ALERT = "alert"          # first occurrence of this key
NTFY_DEDUP_FOLD = "fold"            # standing condition, no magnitude increase -- suppressed
NTFY_DEDUP_REALERT = "re-alert"     # standing condition whose magnitude increased


def ntfy_dedup_decision(state: dict, key: str, magnitude: int, now_ts: float) -> str:
    """Same shape as basis #922's anomaly dedup (first occurrence alerts, repeats fold, a magnitude
    increase re-alerts) -- pusher.py carried no such code at the time this was written (checked:
    no `anomaly`/`dedup`-standing-condition function exists anywhere in this file), so this is a
    fresh implementation of that shape rather than a reuse of a prior one. Mutates `state` in place
    (caller persists via save_push_state, same discipline as every other *_and_record helper in this
    module); pure return value is only the decision string."""
    entry = state.get(key)
    if entry is None:
        state[key] = {"first_seen": now_ts, "last_seen": now_ts, "magnitude": magnitude, "alert_count": 1}
        return NTFY_DEDUP_ALERT
    entry["last_seen"] = now_ts
    if magnitude > entry.get("magnitude", 0):
        entry["magnitude"] = magnitude
        entry["alert_count"] = entry.get("alert_count", 0) + 1
        return NTFY_DEDUP_REALERT
    return NTFY_DEDUP_FOLD


def ntfy_clear_dedup(state: dict, key: str) -> None:
    """Drops a resolved standing condition's dedup entry so its NEXT occurrence reads as a fresh
    first occurrence rather than folding forever against a condition that no longer holds."""
    state.pop(key, None)


def load_ntfy_secrets(here: Path = HERE) -> dict:
    """secrets.local.json next to this script -- gitignored, machine-local, per the pattern this
    tool's other secrets (the mailbox push token) already follow. Missing file -> {} (no token; a
    self-hosted ntfy server that requires auth will reject the push and the pusher logs that same as
    any other send failure -- never a crash on a missing secrets file)."""
    path = here / "secrets.local.json"
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}


def send_ntfy(sender, server: str, topic: str, token: str | None, title: str, message: str,
              priority: str) -> None:
    """One small function, injectable `sender` -- `sender(url, headers, body_bytes)`. Never called
    with a real network sender from a test; selftests pass a fake that records calls."""
    headers = {"Title": title, "Priority": priority}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    sender(f"{server.rstrip('/')}/{topic}", headers, message.encode("utf-8"))


def _urllib_ntfy_sender(url: str, headers: dict, body: bytes) -> None:
    req = urllib.request.Request(url, data=body, headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=10) as resp:
        resp.read()


def maybe_push_ntfy_event(cfg: dict, secrets: dict, state: dict, event_type: str, key: str,
                           magnitude: int, title: str, message: str, now: datetime,
                           sender=None) -> str:
    """The one entry point main() calls. Returns what happened, purely for logging:
    "disabled" (no ntfy_topic configured), "suppressed-quiet-hours", "folded", or "sent". Never
    raises on a send failure -- caller wraps the actual send in the same try/except-and-log posture
    every other producer in this module uses; this function itself only decides whether to try."""
    topic = cfg.get("ntfy_topic")
    if not topic:
        return "disabled"
    tier = ntfy_priority_for_event(event_type)
    if tier != "urgent" and in_quiet_hours(cfg, now):
        return "suppressed-quiet-hours"
    decision = ntfy_dedup_decision(state, key, magnitude, now.timestamp())
    if decision == NTFY_DEDUP_FOLD:
        return "folded"
    send_ntfy(sender or _urllib_ntfy_sender, cfg.get("ntfy_server", NTFY_DEFAULT_SERVER), topic,
               secrets.get("ntfy_token"), title, message, tier)
    return "sent"


#: the room-condition event types this pusher can detect purely from `fleet_status`'s own row
#: fields -- no new engine surface needed. Every key here is one of NTFY_EVENT_TIERS' keys.
_NTFY_ROOM_EVENT_KEYS = ("lane_failed", "lane_succeeded_with_warnings", "zombie_detected")


def ntfy_room_classification(room: dict) -> tuple[str, int] | None:
    """Classifies a single `fleet_status` room row into (event_type, magnitude), or None when the
    room is in no notifiable condition. Magnitude is what `ntfy_dedup_decision` compares to decide
    fold vs. re-alert -- for a failed lane that's the retry count (a failure that has now failed
    THREE times is worth re-alerting on even though the room never left "Failed"); for a zombie it's
    a constant 1 (there is no natural magnitude to a single stalled room beyond its own existence).
    "Succeeded with warnings" has no dedicated taxonomy field yet (spec/baton.md §6, #1390's hollow-
    success badge is separate, still gated) -- the best available signal today is a room that
    reached Succeeded only after at least one retry, or that still carries a non-empty `error`
    string despite a Succeeded state."""
    if not isinstance(room, dict):
        return None
    state = room.get("state")
    try_count = room.get("try") or 1
    if state == "Failed":
        return "lane_failed", int(try_count)
    if state == "Stalled":
        return "zombie_detected", 1
    if state == "Succeeded" and (room.get("error") or int(try_count) > 1):
        return "lane_succeeded_with_warnings", int(try_count)
    return None


def _ntfy_room_title_message(event_type: str, room: dict) -> tuple[str, str]:
    name = room.get("name") or room.get("path") or "(unknown room)"
    if event_type == "lane_failed":
        err = room.get("error") or "no error text"
        return f"Lane failed: {name}", err[:200]
    if event_type == "zombie_detected":
        return f"Lane stalled: {name}", "engine reads dead; room may need a redispatch"
    if event_type == "lane_succeeded_with_warnings":
        try_count = room.get("try") or 1
        detail = room.get("error") or f"succeeded after {try_count} attempt(s)"
        return f"Lane succeeded with warnings: {name}", detail[:200]
    return f"Fleet event: {name}", event_type


def prune_ntfy_dedup_state(ntfy_state: dict, room_list: list) -> None:
    """Mutates `ntfy_state` in place, dropping every per-room dedup key (`<event_type>:<path>`) for a
    room no longer present in `room_list` AT ALL -- distinct from `push_ntfy_room_events`'s own
    per-room clear, which only fires for a room that's still in the list but non-notifiable this
    cycle. A room that leaves the fleet entirely (deleted, or swept by RoomRetentionSweep) stops
    appearing in `room_list` and is never visited by that per-room clear again, so its entry would
    otherwise persist forever. Mirrors `prune_live_telemetry_cache`/`prune_pruned_info_cache`'s
    intersect-against-current-room_list shape. `pusher_anomaly:*` keys are left alone -- they're
    keyed by a fixed, small set of block names rather than per-room, so they're already bounded."""
    paths = {room.get("path") or room.get("name")
             for room in (room_list or []) if isinstance(room, dict)}
    for key in list(ntfy_state.keys()):
        event_type, sep, path = key.partition(":")
        if sep and event_type in _NTFY_ROOM_EVENT_KEYS and path not in paths:
            del ntfy_state[key]


def push_ntfy_room_events(cfg: dict, secrets: dict, ntfy_state: dict, room_list: list,
                           now: datetime, sender=None) -> None:
    """Called once per main() cycle over the CURRENT room list. A room with no notifiable condition
    this cycle has its dedup entries cleared, so a LATER re-occurrence (a lane that recovers, then
    fails again) reads as a fresh first occurrence rather than folding forever against a condition
    that already resolved. Also prunes (`prune_ntfy_dedup_state`) any per-room entry for a room that
    left the fleet entirely, not just one that's merely non-notifiable this cycle. Never raises -- a
    single room's send failure is logged and does not stop the rest of the fleet from being
    considered."""
    prune_ntfy_dedup_state(ntfy_state, room_list)
    for room in (room_list or []):
        if not isinstance(room, dict):
            continue
        path = room.get("path") or room.get("name")
        if not path:
            continue
        classification = ntfy_room_classification(room)
        keys = [f"{event_type}:{path}" for event_type in _NTFY_ROOM_EVENT_KEYS]
        if classification is None:
            for k in keys:
                ntfy_clear_dedup(ntfy_state, k)
            continue
        event_type, magnitude = classification
        active_key = f"{event_type}:{path}"
        for k in keys:
            if k != active_key:
                ntfy_clear_dedup(ntfy_state, k)
        title, message = _ntfy_room_title_message(event_type, room)
        try:
            outcome = maybe_push_ntfy_event(cfg, secrets, ntfy_state, event_type, active_key,
                                             magnitude, title, message, now, sender=sender)
            if outcome == "sent":
                log(f"ntfy: {event_type} for {room.get('name', path)}")
        except Exception as ex:  # noqa: BLE001 — one room's ntfy failure must not skip the rest
            log(f"ERROR (ntfy room event) {type(ex).__name__}: {ex}")


def push_ntfy_pusher_anomaly(cfg: dict, secrets: dict, ntfy_state_path: Path, block_name: str,
                              ex: Exception, now: datetime, sender=None) -> None:
    """Called from main()'s own except blocks (snapshot/heartbeat/deliver) -- a pusher-level
    anomaly, tier `high`. Keyed by block_name alone (not the exception text, which can vary run to
    run for the same underlying fault) so a repeating failure folds instead of paging every cycle.
    Owns its own load/save round-trip against `ntfy_state_path` (unlike `push_ntfy_room_events`,
    which shares an already-loaded state dict with its caller) -- each call site is a distinct
    except block that cannot assume any other block ran first this cycle, so a self-contained
    load-mutate-save is the only safe posture. Never raises."""
    try:
        state = load_push_state(ntfy_state_path)
        outcome = maybe_push_ntfy_event(
            cfg, secrets, state, "pusher_anomaly", f"pusher_anomaly:{block_name}", 1,
            f"Fleet Glass pusher anomaly ({block_name})", f"{type(ex).__name__}: {ex}"[:200],
            now, sender=sender)
        save_push_state(ntfy_state_path, state)
        if outcome == "sent":
            log(f"ntfy: pusher_anomaly ({block_name})")
    except Exception as ntfy_ex:  # noqa: BLE001 — the ntfy path itself must never break the loop
        log(f"ERROR (ntfy pusher anomaly) {type(ntfy_ex).__name__}: {ntfy_ex}")


# ---------------------------------------------------------------------------------------------
# Identity diff (#1557 PR-B1): runs BOTH the `derive` path and the `file` path once against the
# SAME live rooms and diffs them field-by-field, so the switch above can be flipped on trust rather
# than hope. `python pusher.py --compare-projection`.
# ---------------------------------------------------------------------------------------------

# Presence/shape-only fields (plan §4/item 3): the derive path never emitted these at all before
# #1557, so there is nothing on that side to diff against -- excluded from the byte-identity
# comparison below, checked for shape instead.
_COMPARE_SHAPE_ONLY_KEYS = {
    "processAlive": lambda v: isinstance(v, str) and v in ("alive", "dead", "unknown"),
    "stdout_last_write_ago_sec": lambda v: isinstance(v, (int, float)) and not isinstance(v, bool) and v >= 0,
    "elapsed": lambda v: isinstance(v, (int, float)) and not isinstance(v, bool) and v >= 0,
}

# #1807: fields on a Running room that legitimately move between the daemon's file sample and this
# process's derive sample (taken ~30s-ish apart, the daemon's own write cadence,
# src/Baton.Cli/Daemon/FleetProjectionWriter.cs `DefaultInterval`) -- excluded from the byte-identity
# comparison the same way `_COMPARE_SHAPE_ONLY_KEYS` is, and
# checked instead by `_compare_volatile_live`'s tolerance rules below. Chose per-field tolerance
# over the issue's other option (excluding Running rooms from `live.*` entirely) because it keeps
# checking Running rooms -- a counter that goes backwards, or a field that vanishes, is still a
# real derivation bug and still fails the compare.
#
# The issue proposed treating all five as monotone non-decreasing. A live run against this
# machine's own fleet (see the PR body) reds that on `cacheReadTokens`, which the docstring on
# `live_telemetry_for_room`'s `"context"` field already calls out as NOT cumulative: it's "a LEVEL
# (the caller replaces, never sums, its own running value)" taken from the LATEST usage-bearing
# line, so it moves in either direction as new turns land -- unlike the three true running counters
# below it. `contextTokens` comes from the same `"context"` object, so it gets the same treatment.
# #1812: that red turned out to be a genuine derivation bug, not sampling jitter -- the file path's
# `cacheReadTokens` was a running Σ (Mutation.TokenBudgetMonitor's own display-only accumulator,
# #1682) while the derive path replaces it per turn. Fixed on the C# side
# (WorkerUsage.CacheReadLevelTokens, src/Baton/Mutation/TokenBudgetMonitor.cs) so both paths report
# the same level; `_LEVEL_LIVE_KEYS` below stays presence/shape-only ONLY for a still-Running room
# (where the two samples can honestly land on different turns) and goes back under exact comparison
# once a room is settled (`_room_is_settled`), so a reintroduced sum-vs-level mismatch reds again.
_MONOTONE_LIVE_COUNTER_KEYS = ("billedTokens", "toolCalls", "turns")
_LEVEL_LIVE_KEYS = ("contextTokens", "cacheReadTokens")

# spec/baton.md §6 PR-B2 gate (#1807): a compare that is green because it had nothing live to check
# is not evidence -- require at least this many settled rooms before a clean diff counts as a pass.
_MIN_SETTLED_ROOMS_FOR_GREEN = 3


def _canonical(obj) -> str:
    """Canonical JSON per the plan's "byte-for-byte after canonical JSON serialization (sorted
    keys, same separators)" -- the ONE serialization both sides of every comparison below go
    through, so a diff can never be an artifact of key order or whitespace."""
    return json.dumps(obj, sort_keys=True, separators=(",", ":"))


def _normalize_room_for_compare(room: dict) -> dict:
    """Strips the fields excluded from strict equality before the canonical-JSON comparison: the
    shape-only keys above, `live.lastActivityAt` (see `_compare_last_activity` -- diffed
    separately, on the unquantized instant, never on the bucketed string), and the #1807 volatile
    live fields (`_MONOTONE_LIVE_COUNTER_KEYS`, `_LEVEL_LIVE_KEYS`, plus `stdoutTail` -- see
    `_compare_volatile_live`, which diffs them separately under a moving-value tolerance instead of
    byte equality)."""
    normalized = {k: v for k, v in room.items() if k not in _COMPARE_SHAPE_ONLY_KEYS}
    live = normalized.get("live")
    if isinstance(live, dict):
        live = dict(live)
        live.pop("lastActivityAt", None)
        for key in _MONOTONE_LIVE_COUNTER_KEYS + _LEVEL_LIVE_KEYS:
            live.pop(key, None)
        live.pop("stdoutTail", None)
        # #1793: doingNow reads the SAME growing `.stdout.log` at a different wall-clock instant than
        # stdoutTail does (both sides call `_find_stdout_paths`/`_read_tail_text` fresh, no shared
        # cache) -- same "can legitimately land on a different line while still Running" shape, so it
        # gets the same settled-only exact-compare treatment in `_compare_volatile_live` rather than
        # strict equality here.
        live.pop("doingNow", None)
        normalized["live"] = live
    return normalized


# #1557 PR-B2's own acceptance instrument, distinct from `_diff_room` above. `_diff_room` compares
# ONE ROOM between two LIVE samples taken ~30s apart, so it has to tolerate every field that can
# honestly move in between. This one compares the FINISHED PUSHED SNAPSHOT (including the
# `snapshot_post_body` serialization) over ONE FROZEN FIXTURE -- nothing is moving, so nothing volatile
# is tolerated, and the top-level keys `_diff_room` never sees (`timelines`, `stale_hidden_count`,
# `terminal_total`, `terminal_archive`, `underhood`, `conductor`, `vendors`, `staleness`) are in
# scope. Only two exclusions, both named:
#   - `rooms[].live.lastActivityAt` -- a 90s-quantized bucket off the same mtime, sampled at two
#     different instants (same reason `_normalize_room_for_compare` excludes it).
#   - `_COMPARE_SHAPE_ONLY_KEYS` -- `processAlive`/`stdout_last_write_ago_sec`/`elapsed`, which the
#     derive path has never emitted at all, so there is nothing on that side to compare against.
_SNAPSHOT_IDENTITY_EXCLUSIONS = ("rooms[].live.lastActivityAt", *sorted(_COMPARE_SHAPE_ONLY_KEYS))


def _identity_normalize_room(room: dict) -> dict:
    """`_normalize_room_for_compare`'s frozen-fixture sibling: strips ONLY the two exclusions named
    on `_SNAPSHOT_IDENTITY_EXCLUSIONS`, never the volatile-live fields that function drops -- over a
    fixture that is not moving, a `billedTokens`/`toolCalls`/`stdoutTail` difference is a real
    derivation difference, and tolerating it here would make the identity arm unable to see the one
    class of bug it exists for."""
    if not isinstance(room, dict):
        return room
    normalized = {k: v for k, v in room.items() if k not in _COMPARE_SHAPE_ONLY_KEYS}
    live = normalized.get("live")
    if isinstance(live, dict):
        live = {k: v for k, v in live.items() if k != "lastActivityAt"}
        normalized["live"] = live
    return normalized


def snapshot_identity_diffs(derive_wrapped: dict, file_wrapped: dict) -> list[str]:
    """Sorted top-level keys of the pushed snapshot that differ between the two projection sources,
    after `_SNAPSHOT_IDENTITY_EXCLUSIONS`. `[]` means the two sources produced the same pushed body.
    `["derived_at", "timelines"]` names the intentional source-dependent differences: file mode
    preserves the daemon timestamp, while derive mints a new one; the daemon also carries no
    per-room timelines yet (see `derive_snapshot_and_timelines`'s removal condition)."""
    def prepare(wrapped: dict) -> dict:
        prepared = dict(wrapped)
        for key in ("rooms", "terminal_archive"):
            value = prepared.get(key)
            if isinstance(value, list):
                prepared[key] = [_identity_normalize_room(r) for r in value]
        return prepared

    d_prepared, f_prepared = prepare(derive_wrapped), prepare(file_wrapped)
    return sorted(key for key in set(d_prepared) | set(f_prepared)
                  if _canonical(d_prepared.get(key)) != _canonical(f_prepared.get(key)))


def _compare_last_activity(path: str, derive_room: dict, file_room: dict) -> list[str]:
    """`rooms[].live.lastActivityAt` is excluded from strict equality (plan §4/item 3): both paths
    bucket the SAME underlying `.stdout.log` mtime (`LAST_ACTIVITY_BUCKET_SECONDS`=90s) but sample
    it at different instants -- the file can be up to `PROJECTION_STALE_AFTER_S` old, the derive
    path reads it live -- so they can legitimately land in different buckets while both are
    correct. Sanity-bounded instead: the two instants cannot honestly diverge by more than the
    file's own staleness ceiling plus one bucket."""
    d_live = derive_room.get("live")
    f_live = file_room.get("live")
    d_ts = d_live.get("lastActivityAt") if isinstance(d_live, dict) else None
    f_ts = f_live.get("lastActivityAt") if isinstance(f_live, dict) else None
    if d_ts is None or f_ts is None:
        return []
    try:
        d_epoch = datetime.fromisoformat(d_ts.replace("Z", "+00:00")).timestamp()
        f_epoch = datetime.fromisoformat(f_ts.replace("Z", "+00:00")).timestamp()
    except ValueError:
        return [f"{path}: live.lastActivityAt not parseable: derive={d_ts!r} file={f_ts!r}"]
    bound = PROJECTION_STALE_AFTER_S + LAST_ACTIVITY_BUCKET_SECONDS
    if abs(d_epoch - f_epoch) > bound:
        return [f"{path}: live.lastActivityAt diverges beyond the staleness bound ({bound}s): "
                f"derive={d_ts} file={f_ts}"]
    return []


def _tail_contains_earlier_last_line(later_tail: str, earlier_tail: str) -> bool:
    """#1812: the substring check `_compare_volatile_live` uses for a Running room's `stdoutTail`
    -- the earlier sample's last non-empty line must still appear somewhere in the later sample's
    tail, the way two reads of the SAME growing (or `[withheld]`-redacted) log window would. An
    empty earlier tail has nothing to check (vacuously true) -- covers both a brand-new stream and
    the earlier sample landing right after an 8 MiB rollover reset it to empty."""
    lines = [line for line in earlier_tail.splitlines() if line]
    if not lines:
        return True
    return lines[-1] in later_tail


def _compare_volatile_live(path: str, derive_room: dict, file_room: dict, derive_is_later: bool | None) -> list[str]:
    """#1812: which sample is chronologically later is NOT assumed from call order -- it is passed
    in as `derive_is_later`, computed once in `compare_projection` from both sides' own
    `derived_at` (`None` when either side's `derived_at` is missing or unparseable, in which case
    the ordering-dependent checks below are skipped for this room rather than guessing a
    direction -- the same fail-open shape `_compare_last_activity` uses for a missing timestamp).

    `_MONOTONE_LIVE_COUNTER_KEYS` (true running counters) must be >= whichever side is earlier --
    never simply "derive >= file": a daemon write landing between `compare_projection`'s derive
    step and its file-read step can legitimately make the FILE side the later one.

    `_LEVEL_LIVE_KEYS` (the latest-turn snapshot, not cumulative -- see that constant's own
    comment) get no ordering check on a Running room: only presence and numeric-shape, since either
    side can legitimately be smaller, larger, or absent-then-present as a new turn lands mid-stream.
    Once a room is settled (`_room_is_settled` -- terminal, or quiet past one daemon write
    interval), its counters can no longer legitimately be moving, so both `_LEVEL_LIVE_KEYS` and
    `stdoutTail` go back under EXACT comparison -- the #1812 review found a settled room's
    `cacheReadTokens` silently masking a genuine sum-vs-level derivation bug under the same
    presence/shape-only tolerance a still-Running room needs honestly.

    `stdoutTail` on a still-Running room is compared by containment rather than exact match: the
    two paths read the SAME `.stdout.log` at different wall-clock instants (`compare_projection`
    hands `attach_live_telemetry` a fresh `live_telemetry_cache` every run, but that cache has no
    effect on `stdout_tail_for_room` at all -- it reads the file straight off disk regardless of
    cache warmth), so the later sample's tail must still contain the earlier sample's last line
    (skipped, like the monotone counters, when `derive_is_later` could not be determined, and
    tolerant of an 8 MiB rollover resetting the earlier tail to empty). A field present on one side
    and missing on the other, or a non-numeric/non-string value, is still a real derivation
    difference on either kind of room -- none of that is tolerated."""
    diffs = []
    d_live = derive_room.get("live")
    f_live = file_room.get("live")
    if not isinstance(d_live, dict) or not isinstance(f_live, dict):
        return diffs

    settled = _room_is_settled(file_room)
    later, earlier = ("derive", "file") if derive_is_later else ("file", "derive")

    for key in _MONOTONE_LIVE_COUNTER_KEYS + _LEVEL_LIVE_KEYS:
        d_has, f_has = key in d_live, key in f_live
        if d_has != f_has:
            diffs.append(f"{path}: field {key!r} present in {'derive' if d_has else 'file'}, "
                         f"absent in {'file' if d_has else 'derive'}")
            continue
        if not d_has:
            continue
        d_val, f_val = d_live[key], f_live[key]
        if isinstance(d_val, bool) or isinstance(f_val, bool) or not isinstance(d_val, (int, float)) \
                or not isinstance(f_val, (int, float)):
            diffs.append(f"{path}: live.{key} not numeric: derive={d_val!r} file={f_val!r}")
            continue
        if key in _MONOTONE_LIVE_COUNTER_KEYS and derive_is_later is not None:
            later_val, earlier_val = (d_val, f_val) if derive_is_later else (f_val, d_val)
            if later_val < earlier_val:
                diffs.append(f"{path}: live.{key} moved backwards: {later}={later_val} (later) < "
                             f"{earlier}={earlier_val} (earlier)")
        elif key in _LEVEL_LIVE_KEYS and settled and d_val != f_val:
            diffs.append(f"{path}: live.{key} differs on a settled room: derive={d_val} file={f_val}")

    d_tail, f_tail = d_live.get("stdoutTail"), f_live.get("stdoutTail")
    if (d_tail is None) != (f_tail is None):
        diffs.append(f"{path}: field 'stdoutTail' present in "
                     f"{'derive' if d_tail is not None else 'file'}, absent in "
                     f"{'file' if d_tail is not None else 'derive'}")
    elif d_tail is not None and not isinstance(d_tail, str):
        diffs.append(f"{path}: live.stdoutTail not a string: derive={d_tail!r}")
    elif f_tail is not None and not isinstance(f_tail, str):
        diffs.append(f"{path}: live.stdoutTail not a string: file={f_tail!r}")
    elif d_tail is not None and f_tail is not None:
        if settled:
            if d_tail != f_tail:
                diffs.append(f"{path}: live.stdoutTail differs on a settled room: "
                             f"derive={d_tail!r} file={f_tail!r}")
        elif derive_is_later is not None:
            later_tail, earlier_tail = (d_tail, f_tail) if derive_is_later else (f_tail, d_tail)
            if not _tail_contains_earlier_last_line(later_tail, earlier_tail):
                diffs.append(f"{path}: live.stdoutTail: {later}'s tail does not contain "
                             f"{earlier}'s last line: {later}={later_tail!r} {earlier}={earlier_tail!r}")

    # #1793: doingNow, like stdoutTail, is a snapshot of "the last assistant line right now" -- on a
    # still-Running room the two samples can honestly land on different lines (a new turn arrived
    # between them), so it gets presence/shape-only tolerance there and only goes under EXACT
    # comparison once the room is settled and can no longer legitimately be moving.
    d_doing, f_doing = d_live.get("doingNow"), f_live.get("doingNow")
    if (d_doing is None) != (f_doing is None):
        diffs.append(f"{path}: field 'doingNow' present in "
                     f"{'derive' if d_doing is not None else 'file'}, absent in "
                     f"{'file' if d_doing is not None else 'derive'}")
    elif d_doing is not None and not isinstance(d_doing, str):
        diffs.append(f"{path}: live.doingNow not a string: derive={d_doing!r}")
    elif f_doing is not None and not isinstance(f_doing, str):
        diffs.append(f"{path}: live.doingNow not a string: file={f_doing!r}")
    elif settled and d_doing is not None and f_doing is not None and d_doing != f_doing:
        diffs.append(f"{path}: live.doingNow differs on a settled room: "
                     f"derive={d_doing!r} file={f_doing!r}")

    return diffs


def _diff_room(path: str, derive_room: dict, file_room: dict, derive_is_later: bool | None) -> list[str]:
    """Every field-level difference between one room's derive-path and file-path projections,
    after the plan §4/item 3 exclusions and the #1807/#1812 volatile-live tolerance. `derive_is_later`
    is threaded straight through to `_compare_volatile_live` -- see that function's own doc for what
    it means and where it comes from. Empty list means identical."""
    diffs = _compare_last_activity(path, derive_room, file_room)
    diffs.extend(_compare_volatile_live(path, derive_room, file_room, derive_is_later))

    d_norm = _normalize_room_for_compare(derive_room)
    f_norm = _normalize_room_for_compare(file_room)
    if _canonical(d_norm) != _canonical(f_norm):
        for key in sorted(set(d_norm) | set(f_norm)):
            if key not in f_norm:
                diffs.append(f"{path}: field {key!r} present in derive, absent in file")
            elif key not in d_norm:
                diffs.append(f"{path}: field {key!r} present in file, absent in derive")
            elif _canonical(d_norm[key]) != _canonical(f_norm[key]):
                diffs.append(f"{path}: field {key!r} differs: "
                             f"derive={_canonical(d_norm[key])} file={_canonical(f_norm[key])}")

    for key, shape_ok in _COMPARE_SHAPE_ONLY_KEYS.items():
        if key in file_room and not shape_ok(file_room[key]):
            diffs.append(f"{path}: field {key!r} present but shape-invalid: {file_room[key]!r}")

    return diffs


def _room_is_settled(file_room: dict) -> bool:
    """#1814: a room counts toward `_MIN_SETTLED_ROOMS_FOR_GREEN` only once there is EVIDENCE its
    counters can no longer move -- either the projection carries a terminal `state`
    (`_TERMINAL_STATES`), or the room's own directory holds `terminal.json` (`is_terminal_room`, the
    same fast-path fleet_status itself uses). Quiet time is NOT evidence: a Running room whose
    worker is inside one long tool call (a build, a test run, a lock wait) can go quiet for far
    longer than one daemon write interval without its counters having stopped moving -- #1814 found
    this misreading a still-Running room as settled and redding its `live.contextTokens` on ordinary
    derive/file sampling drift, not a real derivation bug. A Running room with no terminal fact
    stays Running however quiet it looks, and stays under the presence/shape-only + monotone checks
    `_compare_volatile_live` already gives a still-Running room."""
    if file_room.get("state") in _TERMINAL_STATES:
        return True
    path = file_room.get("path")
    if not isinstance(path, str):
        return False
    return is_terminal_room(path)


def _derive_is_later(derive_derived_at: str | None, file_derived_at: str | None) -> bool | None:
    """#1812: parses both sides' `derived_at` and reports whether the derive sample is the
    chronologically LATER one. `None` when either side's timestamp is missing or unparseable --
    callers skip the ordering-dependent checks in that case (`_compare_volatile_live`'s own doc)
    rather than falling back to an assumed call order, which is exactly the bug this replaces."""
    if not isinstance(derive_derived_at, str) or not isinstance(file_derived_at, str):
        return None
    try:
        d_epoch = datetime.fromisoformat(derive_derived_at.replace("Z", "+00:00")).timestamp()
        f_epoch = datetime.fromisoformat(file_derived_at.replace("Z", "+00:00")).timestamp()
    except ValueError:
        return None
    return d_epoch >= f_epoch


def compare_projection(dll: str, roots: list) -> int:
    """Runs the `derive` path (spawn `dotnet mcp`, then `attach_live_telemetry`/
    `attach_pruned_info` -- main()'s own pre-#1557 pipeline) and the `file` path (read
    `BatonPaths.FleetProjectionFile`) ONCE each against the same live rooms, then diffs every room
    both sides agree exists. Exit 0 on identical, 1 with the diff printed on mismatch.

    #1812: derivation happens FIRST, the projection file is read SECOND -- an earlier docstring
    here claimed the opposite order and had the monotone-counter check assume derive is always
    later because of it. The real order doesn't settle the question either (the daemon writes the
    file on its own independent cadence, so a write can land between the two steps below and make
    the file side the later sample) -- so ordering is read from each side's own `derived_at`
    instead of assumed from either the claimed or the actual call order. `derive_parsed`
    (fleet_status's raw result) carries no top-level `derived_at` the way the projection FILE does
    (`FleetProjectionWriter.cs:162`), so the derive side's timestamp is captured explicitly, right
    after `attach_live_telemetry` has read each room's live counters straight off disk -- that is
    the actual moment those counters were observed."""
    text, _timelines = derive_snapshot_and_timelines(dll, roots)
    derive_parsed = json.loads(text)
    derive_room_list = derive_parsed if isinstance(derive_parsed, list) else (derive_parsed.get("rooms") or [])
    patterns = load_secret_patterns(DEFAULT_SECRET_PATTERNS_FILE)
    attach_live_telemetry(derive_room_list, {}, patterns)
    attach_pruned_info(derive_room_list, {})
    derive_derived_at = datetime.now(timezone.utc).isoformat()

    projection_path = resolve_projection_file_path()
    file_data, staleness = read_projection_file(projection_path, time.time(), max_age_s=float("inf"))
    if file_data is None:
        print(f"COMPARE: projection file unreadable/absent/malformed at {projection_path} -- "
              f"{staleness}", file=sys.stderr)
        return 1
    file_derived_at = file_data.get("derived_at") if isinstance(file_data, dict) else None
    derive_is_later = _derive_is_later(derive_derived_at, file_derived_at)
    file_room_list = file_data.get("rooms") or []

    by_path_derive = {r["path"]: r for r in derive_room_list
                       if isinstance(r, dict) and isinstance(r.get("path"), str)}
    by_path_file = {r["path"]: r for r in file_room_list
                     if isinstance(r, dict) and isinstance(r.get("path"), str)}

    diffs = []
    only_in_derive = sorted(set(by_path_derive) - set(by_path_file))
    only_in_file = sorted(set(by_path_file) - set(by_path_derive))
    if only_in_derive:
        diffs.append(f"rooms only in derive path: {only_in_derive}")
    if only_in_file:
        diffs.append(f"rooms only in file path: {only_in_file}")

    common_paths = sorted(set(by_path_derive) & set(by_path_file))
    for path in common_paths:
        diffs.extend(_diff_room(path, by_path_derive[path], by_path_file[path], derive_is_later))

    settled_count = sum(1 for p in common_paths if _room_is_settled(by_path_file[p]))

    # #1557 plan item 4: an installed daemon build that predates #1786 (PR-A2) never wrote
    # `live.stdoutTail` at all -- that reds this diff on `stdoutTail` for every Running room, which
    # is a version gap, not a bug. Called out separately; the diff below still reports it (and
    # every other field) rather than swallowing it.
    stdout_tail_gap = any("'stdoutTail'" in d and "present in derive, absent in file" in d for d in diffs)
    if stdout_tail_gap:
        print("NOTE: rooms[].live.stdoutTail is present in the derive path but absent from the "
              "file for at least one Running room -- this looks like 'installed daemon predates "
              "#1786 (PR-A2)', not a genuine bug. Re-run after redeploying the daemon build.",
              file=sys.stderr)

    if diffs:
        print("COMPARE: MISMATCH", file=sys.stderr)
        for d in diffs:
            print(f"  !! {d}", file=sys.stderr)
        return 1

    # #1807: a clean diff on zero (or too few) settled rooms is not evidence the monotone-live
    # tolerance actually discriminates -- it just never got exercised. "green on 0" must not pass.
    if settled_count < _MIN_SETTLED_ROOMS_FOR_GREEN:
        print(f"COMPARE: FAIL -- only {settled_count} settled room(s) compared (need >= "
              f"{_MIN_SETTLED_ROOMS_FOR_GREEN}); a clean diff on too few settled rooms can't "
              f"certify the compare -- rerun once more rooms have finished or gone quiet.",
              file=sys.stderr)
        return 1

    print(f"COMPARE: identical ({len(by_path_file)} room(s) compared, {settled_count} settled, "
          f"{len(only_in_derive) + len(only_in_file)} room-set diff(s))")
    return 0


# ---------------------------------------------------------------------------------------------
# Main loop
# ---------------------------------------------------------------------------------------------

def main() -> None:
    cfg = json.loads((HERE / "pusher.config.json").read_text(encoding="utf-8"))
    once = "--once" in sys.argv
    # #1557 PR-B2: unrecognized values fail toward PROJECTION_SOURCE_DEFAULT rather than raising on
    # a typo'd env var. Note the fail-toward target is now `file`, i.e. the default, not "the path
    # that always worked" -- the per-cycle staleness fallback below is what keeps that safe.
    projection_source = os.environ.get(FLEET_GLASS_PROJECTION_SOURCE_ENV, PROJECTION_SOURCE_DEFAULT)
    if projection_source not in ("file", "derive"):
        log(f"{FLEET_GLASS_PROJECTION_SOURCE_ENV}={projection_source!r} not recognized -- "
            f"using {PROJECTION_SOURCE_DEFAULT!r}")
        projection_source = PROJECTION_SOURCE_DEFAULT
    log(f"projection source: {projection_source} "
        f"({FLEET_GLASS_PROJECTION_SOURCE_ENV}={os.environ.get(FLEET_GLASS_PROJECTION_SOURCE_ENV, '<unset>')})")
    interval = cfg.get("interval_seconds", 25)
    min_push_interval_s = cfg.get("min_push_interval_s", DEFAULT_MIN_PUSH_INTERVAL_S)
    lock_path = Path(cfg["lock_file"]).expanduser() if cfg.get("lock_file") else DEFAULT_LOCK_FILE
    rooms_root = Path(cfg["rooms_root"]).expanduser() if cfg.get("rooms_root") else DEFAULT_ROOMS_ROOT
    patterns_path = Path(cfg["secret_patterns_file"]).expanduser() if cfg.get("secret_patterns_file") else DEFAULT_SECRET_PATTERNS_FILE
    state_path = Path(cfg["push_state_file"]).expanduser() if cfg.get("push_state_file") else DEFAULT_PUSH_STATE_FILE
    # F4 (2026-09-02 review): the write-budget ledger's own file, separate from state_path -- see
    # DEFAULT_BUDGET_STATE_FILE's own comment for why.
    budget_path = Path(cfg["write_budget_file"]).expanduser() if cfg.get("write_budget_file") else DEFAULT_BUDGET_STATE_FILE
    # #1558: the ntfy dedup ledger's own file, beside push-state.local.json (never inside it -- a
    # room's ntfy dedup entry has nothing to do with the mailbox's content-hash dedup, and mixing
    # the two would make either one harder to reason about in isolation).
    ntfy_state_path = Path(cfg["ntfy_state_file"]).expanduser() if cfg.get("ntfy_state_file") else NTFY_DEFAULT_STATE_FILE
    ntfy_secrets = load_ntfy_secrets(HERE)
    if not cfg.get("ntfy_topic"):
        log("ntfy: no ntfy_topic configured -- push disabled")
    deliver_url = derive_deliver_url(cfg)
    heartbeat_url = derive_heartbeat_url(cfg)
    skip_log_every = max(1, round(600 / interval)) if interval > 0 else 1
    skip_streak = 0
    # #1613 item 4: terminal-room timelines are fetched through room_detail exactly ONCE per
    # process lifetime and served from here on every later cycle -- see
    # derive_snapshot_and_timelines's own doc. In-memory only: a restart self-heals by refetching.
    terminal_timeline_cache: dict = {}
    # #1613 review findings 3/4: per-execution (byte_offset, running_counts) for Running rooms'
    # live telemetry -- see live_telemetry_for_room's own doc. In-memory only, same self-heals-on-
    # restart posture as terminal_timeline_cache above.
    live_telemetry_cache: dict = {}
    # #1756 review F2: per-room (pruned/ dir mtime, child count) -> computed `pruned` result, so an
    # uncached rglob walk isn't repeated every poll for a room whose pruned/ tree hasn't changed --
    # see pruned_info_for_room's own doc. Same in-memory-only, self-heals-on-restart posture as
    # live_telemetry_cache above.
    pruned_info_cache: dict = {}
    # #1613 item 2: the wall-clock instant this process's OWN most recent `derive_snapshot_and_
    # timelines` call last completed successfully -- None until the first cycle succeeds. Carried
    # into the heartbeat/derived-ping section below regardless of whether THIS cycle's content
    # changed enough to push.
    last_derived_at: str | None = None
    # #1613 review finding 2: seconds since the last SUCCESSFUL push while content is still waiting
    # to go out -- None whenever nothing is pending (see pending_push_age_s). Carried forward
    # unchanged on a cycle whose derivation itself fails, same as last_derived_at above.
    pending_push_age: float | None = None
    # #1690: the adaptive snapshot interval actually in effect this cycle, purely for the hourly
    # budget log line -- carried forward so a cycle that skips the snapshot branch entirely (an
    # exception, or the budget-exhausted early-out) still has a sane value to log.
    effective_snapshot_interval = float(min_push_interval_s)

    acquire_lock(lock_path)
    atexit.register(release_lock, lock_path)

    # #1829: log the ledger's own state on every restart, not only on the hourly should_log_budget
    # cadence -- a restart is exactly the moment the ledger's persisted count is most worth
    # confirming (the diagnosis that prompted this: a restart-caused loss was suspected, then
    # withdrawn once the persisted file proved honest; this line is what would have shown that in
    # the log directly instead of needing a live read of write-budget.local.json).
    startup_ledger_state = load_push_state(budget_path)
    startup_ledger = load_budget_ledger(startup_ledger_state, time.time())
    log(f"starting -- {format_budget_log_line(startup_ledger, float(min_push_interval_s))}")

    try:
        while True:
            # #1558: default so the ntfy room-events block below (its own top-level try/except) has
            # something to iterate even on a cycle whose snapshot try/except raised before assigning
            # this -- an empty list means "detect nothing this cycle", never a crash.
            room_list: list = []
            try:
                # #1557 PR-B1: `file` mode reads the daemon's own projection file instead of
                # spawning `dotnet mcp` -- `used_file_this_cycle` is False whenever the file was
                # stale/absent (or the switch is off), in which case this cycle falls back to the
                # original derive path exactly as before. `staleness` rides into `wrapped` below
                # ONLY on a fallback cycle -- plan §5's absent-safe convention, mirroring
                # `pusher.writeBudgetExhaustedUntil`.
                staleness = None
                used_file_this_cycle = False
                if projection_source == "file":
                    projection_data, staleness = read_projection_file(
                        resolve_projection_file_path(), time.time())
                    if projection_data is not None:
                        # #1391: keep `vendors` alongside `rooms` rather than re-serializing just the
                        # room list. Both paths emit {"rooms": [...], "vendors"?: [...]} as of #1391
                        # (PR #1869) -- before it the derive path produced a bare room array and this
                        # branch threw `vendors` away. The migration itself is recorded on
                        # FleetStatusResponse's own doc comment in
                        # src/Baton.Cli/Mcp/VendorUsageProjectionReader.cs, not restated here.
                        body = json.dumps({
                            "rooms": projection_data.get("rooms", []),
                            **({"vendors": projection_data["vendors"]} if "vendors" in projection_data else {}),
                        })
                        # #1557 PR-B2 item 3: `timelines` comes from the file WHEN PRESENT -- never
                        # re-derived here, which would mean a `room_detail` call per non-terminal
                        # room and so the very `dotnet mcp` spawn this source exists to remove. The
                        # daemon does not write them yet (FleetProjectionWriter carries no timeline
                        # field), so today this resolves to {} on every file-mode cycle: a
                        # named, intentional difference between the two sources' pushed snapshots
                        # (`_selftest`'s byte-identity arm names it alongside `derived_at`, and
                        # `derive_snapshot_and_timelines`'s removal condition is gated on it).
                        # What an operator actually sees while that holds -- and why it is bounded
                        # -- is spec/baton.md §6's PR-B2 paragraph, not restated here. #1902.
                        raw_timelines = projection_data.get("timelines")
                        timelines = raw_timelines if isinstance(raw_timelines, dict) else {}
                        last_derived_at = projection_data.get("derived_at")
                        used_file_this_cycle = True
                    else:
                        log(f"{FLEET_GLASS_PROJECTION_SOURCE_ENV}=file: projection file stale or "
                            f"absent (daemon_derived_at={staleness.get('daemon_derived_at')}, "
                            f"age_s={staleness.get('age_s')}) -- falling back to derive this cycle")
                if not used_file_this_cycle:
                    body, timelines = derive_snapshot_and_timelines(
                        cfg["dll"], cfg.get("roots", []), terminal_timeline_cache)
                    last_derived_at = datetime.now(timezone.utc).isoformat()
                body, stale_hidden_count = drop_stale_rooms(body, cfg.get("max_age_days", 3))
                rooms = json.loads(body)
                raw_room_list = rooms if isinstance(rooms, list) else rooms.get("rooms")
                # #1391: advisory per-vendor usage runway, riding alongside rooms -- absent whenever
                # nothing has been harvested yet, same optional convention as `conductor`/`staleness`.
                vendors_list = None if isinstance(rooms, list) else rooms.get("vendors")
                conductor_info = None
                filtered_room_list = []
                for r in (raw_room_list or []):
                    if isinstance(r, dict) and (r.get("role") == "conductor" or r.get("name") == "conductor"):
                        c_path = r.get("path") or str(rooms_root / "conductor")
                        conductor_info = {
                            "path": c_path,
                            "artifacts_path": str(Path(c_path) / "artifacts" / "conductor"),
                        }
                    else:
                        filtered_room_list.append(r)
                room_list = filtered_room_list
                if conductor_info is None and (rooms_root / "conductor").is_dir():
                    c_path = str(rooms_root / "conductor")
                    conductor_info = {
                        "path": c_path,
                        "artifacts_path": str(Path(c_path) / "artifacts" / "conductor"),
                    }
                if not used_file_this_cycle:
                    # #1613 item 1: live telemetry for Running rooms, computed AFTER stale-filtering
                    # (never touches drop_stale_rooms' own newest_timestamp scan above) so it plays
                    # no part in the staleness decision at all.
                    live_telemetry_cache = prune_live_telemetry_cache(live_telemetry_cache, room_list)
                    # #1710: same fail-closed patterns load the deliverables path below uses -- a
                    # missing/unreadable patterns file withholds every stdout-tail line rather than
                    # skipping the gate (load_secret_patterns' own None sentinel).
                    stdout_tail_patterns = load_secret_patterns(patterns_path)
                    attach_live_telemetry(room_list, live_telemetry_cache, stdout_tail_patterns)
                    # #1155: same post-drop_stale_rooms placement as attach_live_telemetry above, for
                    # the same reason -- prunedAt is a real mtime, so it never enters the staleness
                    # scan. #1756 review F2: prune the cache first, same ordering as
                    # live_telemetry_cache above.
                    pruned_info_cache = prune_pruned_info_cache(pruned_info_cache, room_list)
                    attach_pruned_info(room_list, pruned_info_cache)
                # else: #1557 PR-B1 -- the projection file already carries `live`/`pruned` per room
                # (FleetProjectionWriter, #1786/#1789), computed once by the daemon; recomputing them
                # here would be the exact per-cycle duplicate work this switch exists to remove. The
                # in-memory caches above simply idle while `file` mode is in effect.
                # Timelines were fetched pre-stale-filter, keyed by path; only carry forward the ones
                # for rooms that survived drop_stale_rooms above, so a hidden room's timeline is hidden
                # with it rather than riding along as orphaned payload.
                surviving_paths = {r.get("path") for r in (room_list or []) if isinstance(r, dict)}
                terminal_timeline_cache = {
                    p: t for p, t in terminal_timeline_cache.items() if p in surviving_paths}
                # #1656: split BEFORE building the wrapped body, and filter `timelines` to
                # `hot_paths` (not the wider `surviving_paths`) -- spec/baton.md §6, "Paging and the
                # terminal hot-set cap".
                wrapped, hot_rooms, hot_paths, terminal_total, terminal_archive, warn_line = \
                    assemble_wrapped(room_list, gather_underhood(cfg), timelines, stale_hidden_count,
                                     conductor=conductor_info, staleness=staleness,
                                     vendors=vendors_list)
                if warn_line:
                    log(warn_line)
                # record-once-ok: #1690 spec/baton.md
                # #1690 item 3: the change-gate hashes a QUANTIZED copy (telemetry churn collapsed to
                # a 300s bucket) -- `wrapped` itself, posted verbatim below, always carries the exact
                # live values; only the hash's SENSITIVITY to telemetry-only churn is reduced.
                now_ts = time.time()
                hash_wrapped = dict(wrapped)
                hash_wrapped["rooms"] = quantize_live_for_hash(hot_rooms)  # F6: values, not the clock
                current_hash = snapshot_hash(hash_wrapped)
                snap_state = load_push_state(state_path)
                ledger_state = load_push_state(budget_path)
                ledger = load_budget_ledger(ledger_state, now_ts)
                if not snapshot_pushes_allowed(ledger):
                    if not ledger.get("exhausted_notice_sent"):
                        exhausted_until = next_utc_midnight_iso(now_ts)
                        pusher_notice = {"writeBudgetExhaustedUntil": exhausted_until}
                        # #1829: kv_write_cap_pusher_fields adds `kvWriteCapResetsAt`, a REAL
                        # Cloudflare 429 `resets_at`, only when mark_kv_write_cap_exhausted stored
                        # one -- the one cap signal glass.html's banner may key on, distinct from
                        # writeBudgetExhaustedUntil above (this pusher's own locally-computed
                        # midnight, sent even when the cap was only inferred from our own ledger
                        # crossing its configured target, never confirmed by a live 429).
                        pusher_notice.update(kv_write_cap_pusher_fields(ledger))
                        notice_wrapped = build_wrapped(
                            hot_rooms, gather_underhood(cfg),
                            {p: t for p, t in timelines.items() if p in hot_paths},
                            stale_hidden_count, terminal_total=terminal_total,
                            terminal_archive=terminal_archive, conductor=conductor_info,
                            pusher=pusher_notice,
                            staleness=staleness,
                            vendors=vendors_list)
                        post_body = snapshot_post_body(notice_wrapped, last_derived_at)
                        # F3(b) (2026-09-02 review): charge the ledger BEFORE the POST -- a lost
                        # response after a committed KV put is indistinguishable, from the client, from
                        # a failure, so the only safe posture for a hard external cap is to over-charge
                        # a genuine failure rather than risk under-charging a silent success
                        # (spec/baton.md §6).
                        ledger = record_budget_write(ledger_state, now_ts, "snapshot", SNAPSHOT_KV_WRITE_COST)
                        ledger["exhausted_notice_sent"] = True
                        ledger_state[BUDGET_STATE_KEY] = ledger
                        save_push_state(budget_path, ledger_state)
                        try:
                            post_json(cfg["push_url"], post_body)
                        except KvWriteCapError as ex:
                            # #1712: the notice snapshot is itself a KV write -- if the Worker is
                            # refusing writes for real, it cannot land either. Confirm the ledger is
                            # fully exhausted (all three producers) and log the ONE line; do not
                            # retry the notice.
                            cap_ledger = mark_kv_write_cap_exhausted(ledger_state, now_ts, ex.resets_at)
                            save_push_state(budget_path, ledger_state)
                            log(f"kv write cap hit at {datetime.now(timezone.utc).isoformat()}; "
                                f"no writes until {ex.resets_at}")
                            # #1829: log the `budget:` line on every 429, not only the hourly cadence.
                            log(format_budget_log_line(cap_ledger, effective_snapshot_interval))
                        except Exception as ex:  # noqa: BLE001 — loop must survive anything
                            log(f"ERROR (push, budget-exhausted notice) {type(ex).__name__}: {ex}")
                        else:
                            # F11 (2026-09-02 review): never persist the notice's own hash under
                            # SNAPSHOT_HASH_KEY -- the posted body was `notice_wrapped`, not `wrapped`,
                            # so a stored `current_hash` here would gate every future cycle on a body
                            # that was never actually sent as "current". Clearing it means the day's
                            # first non-exhausted cycle always re-pushes, regardless of content match,
                            # so the exhaustion banner can never outlive the instant it names.
                            snap_state.pop(SNAPSHOT_HASH_KEY, None)
                            snap_state[LAST_PUSH_TS_KEY] = now_ts
                            save_push_state(state_path, snap_state)
                            log(f"write budget exhausted for today -- sent final snapshot, "
                                f"resumes {exhausted_until}")
                    else:
                        log("write budget exhausted -- snapshot pushes stopped for today")
                elif should_push_snapshot(snap_state, current_hash):
                    effective_snapshot_interval = adaptive_snapshot_interval_s(ledger, now_ts, min_push_interval_s)
                    if should_coalesce_push(snap_state, now_ts, effective_snapshot_interval):
                        last_ts = snap_state[LAST_PUSH_TS_KEY]
                        elapsed = int(now_ts - last_ts)
                        log(f"coalesced ({elapsed}s since last push, interval now {int(effective_snapshot_interval)}s)")
                    else:
                        # derived_at rides the ACTUAL posted body but is excluded from current_hash
                        # (computed above from the quantized copy of `wrapped`) -- it must never make
                        # the change-gate think an otherwise-unchanged snapshot changed.
                        post_body = snapshot_post_body(wrapped, last_derived_at)
                        # F3(b): charge before the POST, same reasoning as the exhaustion-notice
                        # branch above. push_snapshot_and_record's own POST-then-record-hash ordering
                        # is unchanged -- only the ledger charge has moved ahead of the POST.
                        record_budget_write(ledger_state, now_ts, "snapshot", SNAPSHOT_KV_WRITE_COST)
                        save_push_state(budget_path, ledger_state)
                        try:
                            push_snapshot_and_record(
                                lambda b: post_json(cfg["push_url"], b),
                                post_body, snap_state, state_path, current_hash, now_ts=now_ts)
                        except KvWriteCapError as ex:
                            # #1712: exhaust every producer's sub-budget right now rather than
                            # waiting for deliver/heartbeat to each independently rediscover the same
                            # hard cap -- this producer's own SNAPSHOT_KV_WRITE_COST charge above
                            # already stands (F3(b)); this widens it to all three.
                            ledger_state = load_push_state(budget_path)
                            cap_ledger = mark_kv_write_cap_exhausted(ledger_state, now_ts, ex.resets_at)
                            save_push_state(budget_path, ledger_state)
                            log(f"kv write cap hit at {datetime.now(timezone.utc).isoformat()}; "
                                f"no writes until {ex.resets_at}")
                            log(format_budget_log_line(cap_ledger, effective_snapshot_interval))
                        except Exception as ex:  # noqa: BLE001 — a failing push must not skip the
                            # pending-push-age computation below (finding 2's whole point), and the
                            # loop must survive regardless -- caught here, not the outer except, so
                            # execution falls through to that computation either way. The budget
                            # charge above already stands even though this POST failed (F3(b)).
                            log(f"ERROR (push) {type(ex).__name__}: {ex}")
                        else:
                            if skip_streak:
                                log(f"skipped {skip_streak} unchanged cycle(s) since last push")
                                skip_streak = 0
                            log(f"pushed {len(body)} bytes")
                else:
                    skip_streak += 1
                    if should_log_skip(skip_streak, skip_log_every):
                        log(f"unchanged, skipped ({skip_streak} in a row)")
                # #1613 review finding 2: recomputed from `snap_state` AFTER the push attempt above,
                # whichever way it went -- a successful push just updated LAST_PUSH_TS_KEY in place
                # (push_snapshot_and_record mutates snap_state), so should_push_snapshot now agrees
                # the hash matches and this comes back None; a coalesced or failed push leaves
                # snap_state's hash stale, so this reports how long content has been waiting.
                pending_push_age = pending_push_age_s(snap_state, current_hash, time.time())

                # #1690: hourly ledger log line, gated the same way should_send_heartbeat is --
                # reload the LEDGER's own file so this reflects every write recorded above (snapshot,
                # and any deliver/heartbeat write from a PRIOR cycle already persisted to disk).
                log_ledger_state = load_push_state(budget_path)
                log_now_ts = time.time()
                if should_log_budget(log_ledger_state, log_now_ts):
                    log_ledger = load_budget_ledger(log_ledger_state, log_now_ts)
                    log(format_budget_log_line(log_ledger, effective_snapshot_interval))
                    log_ledger_state["__last_budget_log_ts__"] = log_now_ts
                    save_push_state(budget_path, log_ledger_state)
            except Exception as ex:  # noqa: BLE001 — loop must survive anything
                log(f"ERROR (snapshot) {type(ex).__name__}: {ex}")
                push_ntfy_pusher_anomaly(cfg, ntfy_secrets, ntfy_state_path, "snapshot", ex,
                                          datetime.now(timezone.utc))

            # Own try/except, runs AFTER the snapshot has already been sent above -- a slow or failing
            # heartbeat POST must never block or delay the snapshot path (#1486). Also carries the
            # derived-freshness ping (#1613 item 2) on the same lightweight endpoint whenever a push
            # hasn't already delivered a fresh derived_at recently -- see should_send_derived_ping --
            # and, since this review's finding 2, the current pending_push_age (omitted when None,
            # i.e. nothing is waiting to go out) so glass can alarm on a failing push independent of
            # derived_at, which stays fresh even while every push fails.
            try:
                if heartbeat_url is None:
                    pass  # no heartbeat_url configured and none derivable from push_url — skip quietly
                else:
                    hb_state = load_push_state(state_path)
                    now_ts = time.time()
                    ledger_state = load_push_state(budget_path)
                    hb_ledger = load_budget_ledger(ledger_state, now_ts)
                    # F1 (2026-09-02 review): the derived-freshness ping gets its OWN adaptive pacing
                    # against the heartbeat sub-budget -- see adaptive_heartbeat_interval_s's own
                    # docstring for why a fixed 300s interval alone blew the 60-write share once
                    # snapshot throttled past 300s and stopped suppressing it.
                    ping_interval = adaptive_heartbeat_interval_s(hb_ledger, now_ts)
                    heartbeat_due = should_send_heartbeat(hb_state, now_ts)
                    derived_ping_due = should_send_derived_ping(hb_state, now_ts, ping_interval)
                    if (heartbeat_due or derived_ping_due) and not heartbeat_allowed(hb_ledger):
                        log("write budget exhausted -- heartbeat/derived-ping skipped this cycle")
                    elif heartbeat_due or derived_ping_due:
                        payload_dict = {"derived_at": last_derived_at}
                        if pending_push_age is not None:
                            payload_dict["pending_push_age_s"] = pending_push_age
                        payload = json.dumps(payload_dict)
                        extra_state = {DERIVED_PING_STATE_KEY: now_ts} if derived_ping_due else None
                        # F3(b): charge before the POST -- send_heartbeat_and_record's own
                        # POST-then-record ordering (for HEARTBEAT_STATE_KEY/DERIVED_PING_STATE_KEY)
                        # is unchanged; only the ledger charge has moved ahead of it.
                        record_budget_write(ledger_state, now_ts, "heartbeat", HEARTBEAT_KV_WRITE_COST)
                        save_push_state(budget_path, ledger_state)
                        send_heartbeat_and_record(
                            lambda: post_json(heartbeat_url, payload),
                            hb_state, state_path, now_ts, extra_state=extra_state)
                        log("heartbeat sent" if heartbeat_due else "derived-freshness ping sent")
            except KvWriteCapError as ex:
                # #1712: same hard-cap posture as the snapshot producer above -- exhaust every
                # producer's sub-budget right now, not just heartbeat's own.
                ledger_state = load_push_state(budget_path)
                cap_ledger = mark_kv_write_cap_exhausted(ledger_state, time.time(), ex.resets_at)
                save_push_state(budget_path, ledger_state)
                log(f"kv write cap hit at {datetime.now(timezone.utc).isoformat()}; "
                    f"no writes until {ex.resets_at}")
                log(format_budget_log_line(cap_ledger, effective_snapshot_interval))
            except Exception as ex:  # noqa: BLE001 — loop must survive anything
                log(f"ERROR (heartbeat) {type(ex).__name__}: {ex}")
                push_ntfy_pusher_anomaly(cfg, ntfy_secrets, ntfy_state_path, "heartbeat", ex,
                                          datetime.now(timezone.utc))

            try:
                if deliver_url is None:
                    log("deliver: no deliver_url (set one, or a push_url containing /push/) — skipped")
                else:
                    state = load_push_state(state_path)
                    patterns = load_secret_patterns(patterns_path)
                    items = gather_deliverables(
                        rooms_root, state, patterns,
                        state_path=state_path,
                        limit=cfg.get("deliver_batch_cap", DEFAULT_DELIVER_BATCH_COUNT_CEILING),
                        max_bytes=cfg.get("deliver_batch_max_bytes", DEFAULT_DELIVER_BATCH_BYTES),
                    )
                    if items:
                        now_ts = time.time()
                        ledger_state = load_push_state(budget_path)
                        deliver_ledger = load_budget_ledger(ledger_state, now_ts)
                        if not deliver_allowed(deliver_ledger):
                            log(f"write budget exhausted -- withholding {len(items)} deliverable(s) this cycle")
                        else:
                            # F1 (2026-09-02 review): deliver gets its own adaptive pacing against its
                            # own sub-budget, so a backlog can never out-race the other two producers
                            # for a shared pool the way it did pre-fix.
                            deliver_interval = adaptive_deliver_interval_s(deliver_ledger, now_ts)
                            if should_coalesce_producer(state, LAST_DELIVER_TS_KEY, now_ts, deliver_interval):
                                last_deliver_ts = state[LAST_DELIVER_TS_KEY]
                                log(f"deliver coalesced ({int(now_ts - last_deliver_ts)}s since last "
                                    f"batch, interval now {int(deliver_interval)}s, {len(items)} "
                                    f"item(s) waiting)")
                            else:
                                # F3(b): charge before the POST -- mark_pushed's own content-dedupe
                                # stays POST-gated (only recorded on success), same as before; only
                                # the ledger charge (a hard external limit, not a content fact) moves
                                # ahead of it.
                                record_budget_write(ledger_state, now_ts, "deliver", DELIVER_BATCH_KV_WRITE_COST)
                                save_push_state(budget_path, ledger_state)
                                post_json(deliver_url, json.dumps({"items": items}))
                                if patterns is not None:
                                    state = mark_pushed(state, items)
                                state[LAST_DELIVER_TS_KEY] = now_ts
                                save_push_state(state_path, state)
                                log(f"delivered {len(items)} item(s) "
                                    f"({sum(1 for i in items if i['withheld'])} withheld)")
            except KvWriteCapError as ex:
                # #1712: same hard-cap posture as the other two producers -- exhaust every
                # producer's sub-budget right now, not just deliver's own.
                ledger_state = load_push_state(budget_path)
                cap_ledger = mark_kv_write_cap_exhausted(ledger_state, time.time(), ex.resets_at)
                save_push_state(budget_path, ledger_state)
                log(f"kv write cap hit at {datetime.now(timezone.utc).isoformat()}; "
                    f"no writes until {ex.resets_at}")
                log(format_budget_log_line(cap_ledger, effective_snapshot_interval))
            except Exception as ex:  # noqa: BLE001 — loop must survive anything
                log(f"ERROR (deliver) {type(ex).__name__}: {ex}")
                push_ntfy_pusher_anomaly(cfg, ntfy_secrets, ntfy_state_path, "deliver", ex,
                                          datetime.now(timezone.utc))

            # #1558: ntfy room-condition events -- own top-level try/except so a failure here never
            # touches the mailbox producers above. Runs every cycle over whatever `room_list` the
            # snapshot block above produced (possibly [] if that block raised early); dedup state is
            # round-tripped through disk the same way every other producer's state is. Gated on
            # `ntfy_topic` at the call site (not just inside `maybe_push_ntfy_event`) so a disabled
            # feature never even reads or writes `ntfy-state.local.json` -- the startup "disabled"
            # log line above is the only thing a disabled config still does.
            if cfg.get("ntfy_topic"):
                try:
                    ntfy_state = load_push_state(ntfy_state_path)
                    push_ntfy_room_events(cfg, ntfy_secrets, ntfy_state, room_list, datetime.now(timezone.utc))
                    save_push_state(ntfy_state_path, ntfy_state)
                except Exception as ex:  # noqa: BLE001 — loop must survive anything
                    log(f"ERROR (ntfy room events) {type(ex).__name__}: {ex}")

            if once:
                break
            time.sleep(interval)
    finally:
        release_lock(lock_path)


# ---------------------------------------------------------------------------------------------
# Selftest -- pins the secret-gate's fail-closed behavior and the dedupe/selection rules against
# synthetic fixtures. No network, no real ~/.baton: pixi run fleet-glass-pusher-selftest.
# ---------------------------------------------------------------------------------------------

def _make_room(root: Path, name: str, outputs_rel: list, state="Succeeded", error=None) -> Path:
    room_dir = root / name
    artifacts_dir = room_dir / "artifacts" / "execution_x"
    artifacts_dir.mkdir(parents=True)
    outputs_abs = []
    for rel, text in outputs_rel:
        p = artifacts_dir / rel
        p.write_text(text, encoding="utf-8")
        outputs_abs.append(str(p))
    (artifacts_dir / "prompt.txt").write_text("the worker's prompt, never uploaded", encoding="utf-8")
    (artifacts_dir / ".stdout.log").write_text("raw stdout, never uploaded", encoding="utf-8")
    (room_dir / "terminal.json").write_text(json.dumps({
        "state": state, "steps": [], "outputs": outputs_abs, "error": error, "try": None,
    }), encoding="utf-8")
    return room_dir



def _selftest() -> int:
    import tempfile
    failures = []

    def check(name, cond):
        if not cond:
            failures.append(name)

    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        rooms_root = tmp / "rooms"
        rooms_root.mkdir()
        _make_room(rooms_root, "room-a", [("report.md", "# Report A\n\nbody text\n")])
        _make_room(rooms_root, "room-b", [], state="Failed", error="boom")

        # -- fail-closed: patterns file missing entirely --
        missing_patterns = load_secret_patterns(tmp / "does-not-exist.txt")
        check("missing patterns file returns the None sentinel", missing_patterns is None)

        items = gather_deliverables(rooms_root, {}, missing_patterns)
        by_room = {i["room_name"]: i for i in items if i["artifact"]}
        check("deliverable item carries room path in 'room'",
              by_room["room-a"]["room"] == str(rooms_root / "room-a"))
        check("deliverable item carries room name in 'room_name'",
              by_room["room-a"]["room_name"] == "room-a")
        check("fail-closed: room-a's real report is withheld when patterns are missing",
              by_room["room-a"]["withheld"] is True
              and "patterns file missing" in by_room["room-a"]["stub_reason"]
              and "Report A" not in by_room["room-a"]["content"])
        check("fail-closed: prompt.txt/.stdout.log never enter the item stream",
              all("prompt" not in (i.get("artifact") or "") and "stdout" not in (i.get("artifact") or "")
                  for i in items))
        check("a room with zero declared outputs still yields one verdict-only item",
              any(i["room_name"] == "room-b" and i["artifact"] is None and i["verdict"]["error"] == "boom"
                  for i in items))

        # -- patterns present, no hit: real content passes through --
        clean_patterns_file = tmp / "clean.txt"
        clean_patterns_file.write_text("# comment only, no real patterns\n", encoding="utf-8")
        clean_patterns = load_secret_patterns(clean_patterns_file)
        check("an empty-but-present patterns file parses to [] (not the fail-closed sentinel)",
              clean_patterns == [])
        items2 = gather_deliverables(rooms_root, {}, clean_patterns)
        report = next(i for i in items2 if i["room_name"] == "room-a" and i["artifact"])
        check("clean content is uploaded verbatim when nothing matches",
              report["withheld"] is False and "Report A" in report["content"])
        check("title comes from the first markdown heading", report["title"] == "Report A")
        check("deliverable carries ISO-8601 created_at from artifact mtime",
              isinstance(report.get("created_at"), str) and "T" in report["created_at"])
        verdict_only = next(i for i in items2 if i["room_name"] == "room-b")
        check("verdict-only deliverable carries created_at from terminal.json mtime",
              isinstance(verdict_only.get("created_at"), str) and "T" in verdict_only["created_at"])
        unreadable_item = build_item("room-x", tmp / "nonexistent", tmp / "nonexistent" / "missing.md", {}, [])
        check("unreadable artifact omits created_at (never crashes)", "created_at" not in unreadable_item)
        unreadable_verdict = build_verdict_only_item("room-x", {}, tmp / "nonexistent")
        check("missing terminal.json omits created_at", "created_at" not in unreadable_verdict)

        # -- patterns present, a hit: withheld with the matched index, never the pattern text --
        hit_patterns_file = tmp / "hit.txt"
        hit_patterns_file.write_text("sk-[A-Za-z0-9]{10,}\nAKIA[0-9A-Z]{16}\n", encoding="utf-8")
        _make_room(rooms_root, "room-c", [("secret.md", "token: sk-abcdefghijklmnop\n")])
        hit_patterns = load_secret_patterns(hit_patterns_file)
        items3 = gather_deliverables(rooms_root, {}, hit_patterns)
        secret_item = next(i for i in items3 if i["room_name"] == "room-c")
        check("a pattern hit withholds the content", secret_item["withheld"] is True)
        check("the stub names the matched pattern's INDEX, not its text",
              secret_item["stub_reason"] == "matched pattern #0" and "sk-" not in secret_item["content"])

        # -- dedupe: an unchanged (room, artifact, hash) is not re-offered --
        state_after = mark_pushed({}, items2)
        items4 = gather_deliverables(rooms_root, state_after, clean_patterns)
        check("dedupe skips an already-pushed, unchanged artifact",
              not any(i["room_name"] == "room-a" and i["artifact"] == "artifacts/execution_x/report.md"
                      for i in items4))

        # -- polarity: changed content is offered again despite matching state key --
        (rooms_root / "room-a" / "artifacts" / "execution_x" / "report.md").write_text(
            "# Report A v2\n\nchanged\n", encoding="utf-8")
        items5 = gather_deliverables(rooms_root, state_after, clean_patterns)
        check("dedupe re-offers an artifact whose content changed",
              any(i["room_name"] == "room-a" and i["title"] == "Report A v2" for i in items5))

        # -- fail-closed is never memorized: gather_deliverables only reads state, it never writes
        # it -- main() is what decides whether to persist, and it skips that when patterns is None
        # (see main()'s "if patterns is not None: save_push_state(...)"). Proven here by calling
        # gather_deliverables twice against the SAME untouched state and requiring identical output,
        # which is exactly what "the caller never got a chance to mark this done" looks like.
        still_missing = load_secret_patterns(tmp / "still-missing.txt")
        first = gather_deliverables(rooms_root, {}, still_missing)
        second = gather_deliverables(rooms_root, {}, still_missing)
        check("a fail-closed run offers the same items every time (nothing here marks it done)",
              [i["id"] for i in first] == [i["id"] for i in second] and len(first) > 0)

        # -- #1617: deliverables join keyed by room path, not room name --
        shared_root1 = tmp / "cluster1" / "rooms"
        shared_root2 = tmp / "cluster2" / "rooms"
        shared_root1.mkdir(parents=True)
        shared_root2.mkdir(parents=True)
        room1_dir = _make_room(shared_root1, "same-name", [("report.md", "# Same Content\n")])
        room2_dir = _make_room(shared_root2, "same-name", [("report.md", "# Same Content\n")])

        items_r1 = gather_deliverables(shared_root1, {}, clean_patterns)
        items_r2 = gather_deliverables(shared_root2, {}, clean_patterns)
        check("deliverable item 'room' is the full room path string",
              items_r1[0]["room"] == str(room1_dir) and items_r2[0]["room"] == str(room2_dir))
        check("deliverable item 'room_name' carries the directory name for display",
              items_r1[0]["room_name"] == "same-name" and items_r2[0]["room_name"] == "same-name")
        check("deliverable item ids are distinct between same-named rooms in different paths",
              items_r1[0]["id"] != items_r2[0]["id"]
              and str(room1_dir) in items_r1[0]["id"]
              and str(room2_dir) in items_r2[0]["id"])

        # Dedupe keying: mark room1 pushed into state.
        state_with_r1 = mark_pushed({}, items_r1)
        check("state keys dedupe by room path, not room name",
              f"{room1_dir}::artifacts/execution_x/report.md" in state_with_r1
              and "same-name::artifacts/execution_x/report.md" not in state_with_r1)

        # Scanning room2 with state_with_r1 must NOT skip room2's deliverable (same name, same content, different path)
        items_r2_after_r1 = gather_deliverables(shared_root2, state_with_r1, clean_patterns)
        check("same-named room in different path is NOT skipped by dedupe when another room with same name and content was pushed",
              len(items_r2_after_r1) == 1 and items_r2_after_r1[0]["room"] == str(room2_dir))

        # (Control) scanning room1 again with state_with_r1 IS skipped by dedupe
        items_r1_again = gather_deliverables(shared_root1, state_with_r1, clean_patterns)
        check("(control) identical room path with unchanged content IS skipped by dedupe",
              len(items_r1_again) == 0)

        # -- Migration on load & format versioning (#1617 / PR #1632) --
        mig_rooms_root = tmp / "mig_rooms"
        mig_rooms_root.mkdir()
        mig_room_dir = _make_room(mig_rooms_root, "room-legacy", [("report.md", "# Legacy Content\n")])
        mig_hash = sha256_hex((mig_room_dir / "artifacts" / "execution_x" / "report.md").read_bytes())
        mig_state_file = tmp / "mig-push-state.json"

        # (a) an old-format state file with one legacy key migrates and the item is NOT re-pushed
        old_state = {"room-legacy::artifacts/execution_x/report.md": mig_hash}
        mig_state_file.write_text(json.dumps(old_state), encoding="utf-8")
        loaded_state = load_push_state(mig_state_file)

        mig_items = gather_deliverables(mig_rooms_root, loaded_state, clean_patterns, state_path=mig_state_file)
        check("(a) old-format state migrates: item is NOT re-pushed", len(mig_items) == 0)
        check("(a) old legacy key is removed from state", "room-legacy::artifacts/execution_x/report.md" not in loaded_state)
        check("(a) path key is adopted in state", f"{mig_room_dir}::artifacts/execution_x/report.md" in loaded_state)
        check("(a) state format version is recorded", loaded_state.get(STATE_FORMAT_VERSION_KEY) == CURRENT_STATE_FORMAT_VERSION)
        persisted_state = load_push_state(mig_state_file)
        check("(a) migrated state is persisted to disk",
              f"{mig_room_dir}::artifacts/execution_x/report.md" in persisted_state
              and "room-legacy::artifacts/execution_x/report.md" not in persisted_state
              and persisted_state.get(STATE_FORMAT_VERSION_KEY) == CURRENT_STATE_FORMAT_VERSION)

        # (b) a legacy key ambiguous between two same-named rooms is not adopted
        ambig_root1 = tmp / "ambig1" / "rooms"
        ambig_root2 = tmp / "ambig2" / "rooms"
        ambig_root1.mkdir(parents=True)
        ambig_root2.mkdir(parents=True)
        ambig_r1 = _make_room(ambig_root1, "ambig-room", [("report.md", "# Clash\n")])
        ambig_r2 = _make_room(ambig_root2, "ambig-room", [("report.md", "# Clash\n")])
        ambig_hash = sha256_hex((ambig_r1 / "artifacts" / "execution_x" / "report.md").read_bytes())
        ambig_state = {"ambig-room::artifacts/execution_x/report.md": ambig_hash}
        terminal_ambig = [(str(ambig_r1), "ambig-room", ambig_r1), (str(ambig_r2), "ambig-room", ambig_r2)]
        migrate_push_state(ambig_state, terminal_ambig)
        check("(b) ambiguous legacy key is not adopted for room 1",
              f"{ambig_r1}::artifacts/execution_x/report.md" not in ambig_state)
        check("(b) ambiguous legacy key is not adopted for room 2",
              f"{ambig_r2}::artifacts/execution_x/report.md" not in ambig_state)

        ambig_items_1 = gather_deliverables(ambig_root1, ambig_state, clean_patterns)
        ambig_items_2 = gather_deliverables(ambig_root2, ambig_state, clean_patterns)
        check("(b) both colliding rooms re-push once",
              len(ambig_items_1) == 1 and len(ambig_items_2) == 1)

        # (c) item id for unchanged deliverable after migration equals id new code computes
        expected_new_id = f"{mig_room_dir}::artifacts/execution_x/report.md::{mig_hash[:16]}"
        verdict = verdict_summary(load_terminal(mig_room_dir))
        computed_item = build_item(str(mig_room_dir), mig_room_dir, mig_room_dir / "artifacts" / "execution_x" / "report.md",
                                   verdict, clean_patterns, room_name="room-legacy")
        check("(c) item id equals the id new code computes (inbox:index dedupe in worker.js replaces rather than duplicates)",
              computed_item["id"] == expected_new_id)

        # -- Deliverables batch capping (#1617 / PR #1632) --
        cap_root = tmp / "cap_rooms"
        cap_root.mkdir()
        for i in range(15):
            _make_room(cap_root, f"room-batch-{i:02d}", [("report.md", f"# Batch {i}\n")])
        capped_items = gather_deliverables(cap_root, {}, clean_patterns, limit=10)
        check("gather_deliverables caps items at limit (default 10) to prevent retry storm",
              len(capped_items) == 10)

        # -- F13 (2026-09-02 review): cumulative-BYTES cap, not just item count -- a fixed count was
        # only ever a proxy for the body size worker.js's own POST cap actually constrains.
        bytes_root = tmp / "bytes_rooms"
        bytes_root.mkdir()
        big_text = "x" * 2_000_000  # 2MB per item -- 3 items would exceed a 4MB budget
        for i in range(5):
            _make_room(bytes_root, f"room-big-{i:02d}", [("report.md", big_text)])
        byte_capped_items = gather_deliverables(bytes_root, {}, clean_patterns, max_bytes=4_000_000)
        check("(F13) a cumulative-bytes cap admits only as many items as fit the byte budget",
              1 <= len(byte_capped_items) <= 2)
        oversized_root = tmp / "oversized_room"
        oversized_root.mkdir()
        _make_room(oversized_root, "room-huge", [("report.md", "y" * 5_000_000)])
        oversized_items = gather_deliverables(oversized_root, {}, clean_patterns, max_bytes=4_000_000)
        check("(F13) a SINGLE item larger than the byte budget is still admitted (fail toward one "
              "oversized batch, never toward silently dropping the only thing to show)",
              len(oversized_items) == 1)

    # -- F4 (2026-09-02 review): save_push_state writes atomically (temp file + os.replace), and the
    # write-budget ledger lives in its own file separate from push-state.local.json.
    with tempfile.TemporaryDirectory() as atomic_tmp:
        atomic_tmp = Path(atomic_tmp)
        state_file = atomic_tmp / "push-state.local.json"
        save_push_state(state_file, {"a": 1})
        check("(F4) save_push_state leaves no leftover .tmp file behind",
              not (atomic_tmp / "push-state.local.json.tmp").exists())
        check("(F4) save_push_state's content round-trips through load_push_state",
              load_push_state(state_file).get("a") == 1)
        save_push_state(state_file, {"a": 2})
        check("(F4) a second save_push_state call still round-trips (the atomic replace doesn't "
              "wedge on a pre-existing target file)",
              load_push_state(state_file).get("a") == 2)
        ledger_file = atomic_tmp / "write-budget.local.json"
        ledger_scratch: dict = {}
        record_budget_write(ledger_scratch, 1000.0, "snapshot", SNAPSHOT_KV_WRITE_COST)
        save_push_state(ledger_file, ledger_scratch)
        check("(F4) the ledger's own file round-trips independent of push-state.local.json",
              load_budget_ledger(load_push_state(ledger_file), 1000.0)["snapshot"] == 1)

    # -- deliver_url derivation --
    check("deliver_url derives from push_url by swapping the path segment",
          derive_deliver_url({"push_url": "https://h/push/TOK"}) == "https://h/deliver/TOK")
    check("deliver_url respects an explicit override",
          derive_deliver_url({"push_url": "https://h/push/TOK", "deliver_url": "https://other/x"}) == "https://other/x")
    check("deliver_url is None when it cannot be derived or configured",
          derive_deliver_url({"push_url": "https://h/nope/TOK"}) is None)

    # -- #1486: heartbeat --
    check("heartbeat_url derives from push_url by swapping the path segment",
          derive_heartbeat_url({"push_url": "https://h/push/TOK"}) == "https://h/heartbeat/TOK")
    check("heartbeat_url respects an explicit override",
          derive_heartbeat_url({"push_url": "https://h/push/TOK", "heartbeat_url": "https://other/x"}) == "https://other/x")
    check("heartbeat_url is None when it cannot be derived or configured",
          derive_heartbeat_url({"push_url": "https://h/nope/TOK"}) is None)

    check("a missing persisted heartbeat timestamp always sends (fail toward one extra write)",
          should_send_heartbeat({}, 10_000.0) is True)
    check("cadence: no beat before the hour is up",
          should_send_heartbeat({HEARTBEAT_STATE_KEY: 10_000.0}, 10_000.0 + HEARTBEAT_INTERVAL_SECONDS - 1) is False)
    check("cadence: a beat is due once the interval has fully elapsed",
          should_send_heartbeat({HEARTBEAT_STATE_KEY: 10_000.0}, 10_000.0 + HEARTBEAT_INTERVAL_SECONDS) is True)

    with tempfile.TemporaryDirectory() as tmp:
        sp = Path(tmp) / "push-state.json"

        def _hb_boom():
            raise RuntimeError("heartbeat post failed")

        hb_state = {SNAPSHOT_HASH_KEY: "unrelated-untouched-hash"}
        try:
            send_heartbeat_and_record(_hb_boom, hb_state, sp, 10_000.0)
        except RuntimeError:
            pass
        check("a FAILED heartbeat post persists nothing (no state file written)", not sp.exists())
        check("a FAILED heartbeat post leaves the in-memory state dict untouched",
              HEARTBEAT_STATE_KEY not in hb_state and hb_state[SNAPSHOT_HASH_KEY] == "unrelated-untouched-hash")

        send_heartbeat_and_record(lambda: None, hb_state, sp, 10_000.0)
        check("a successful heartbeat records the timestamp for the next cycle's cadence gate",
              load_push_state(sp).get(HEARTBEAT_STATE_KEY) == 10_000.0)
        check("a successful heartbeat leaves the unrelated snapshot-hash key alone (snapshot path unaffected)",
              load_push_state(sp).get(SNAPSHOT_HASH_KEY) == "unrelated-untouched-hash")

    with tempfile.TemporaryDirectory() as tmp:
        sp = Path(tmp) / "push-state.json"
        extra_state = {DERIVED_PING_STATE_KEY: 5_000.0}
        send_heartbeat_and_record(lambda: None, {}, sp, 5_000.0, extra_state=extra_state)
        check("send_heartbeat_and_record's extra_state (#1613 item 2) lands alongside HEARTBEAT_STATE_KEY",
              load_push_state(sp).get(DERIVED_PING_STATE_KEY) == 5_000.0
              and load_push_state(sp).get(HEARTBEAT_STATE_KEY) == 5_000.0)

        sp2 = Path(tmp) / "push-state-no-extra.json"

        def _boom2():
            raise RuntimeError("boom")

        try:
            send_heartbeat_and_record(_boom2, {}, sp2, 5_000.0, extra_state=extra_state)
        except RuntimeError:
            pass
        check("a FAILED post never lands extra_state either (same all-or-nothing ordering)",
              not sp2.exists())

    # -- #1613 item 2: derived_at ping cadence, decoupled from the hourly heartbeat --
    check("a missing persisted derived_at landing timestamp always pings (fail toward one extra write)",
          should_send_derived_ping({}, 10_000.0) is True)
    check("no ping needed within the interval since the last PUSH landed a fresh derived_at",
          should_send_derived_ping({LAST_PUSH_TS_KEY: 10_000.0}, 10_000.0 + DERIVED_PING_INTERVAL_SECONDS - 1) is False)
    check("a ping is due once the interval has fully elapsed since the last push",
          should_send_derived_ping({LAST_PUSH_TS_KEY: 10_000.0}, 10_000.0 + DERIVED_PING_INTERVAL_SECONDS) is True)
    check("a prior PING (not just a push) also resets the interval",
          should_send_derived_ping({DERIVED_PING_STATE_KEY: 10_000.0}, 10_000.0 + 60) is False)
    check("whichever landed MORE RECENTLY wins -- a fresher ping beats a stale push",
          should_send_derived_ping(
              {LAST_PUSH_TS_KEY: 0.0, DERIVED_PING_STATE_KEY: 10_000.0}, 10_000.0 + 60) is False)
    check("(control) a stale push AND a stale ping both outside the interval -- due",
          should_send_derived_ping(
              {LAST_PUSH_TS_KEY: 0.0, DERIVED_PING_STATE_KEY: 0.0}, DERIVED_PING_INTERVAL_SECONDS) is True)

    # -- #1613 item 2: derived_at rides the ACTUAL posted body but is excluded from the hash that
    # gates the change-gate -- a hash computed from `wrapped` (never touching derived_at) must be
    # identical to one computed from the same `wrapped` regardless of what derived_at value would
    # later be spliced into the posted JSON alongside it.
    wrapped_no_derived = {"rooms": [{"name": "room-a", "state": "Running"}], "underhood": []}
    hash_before = snapshot_hash(wrapped_no_derived)
    posted_body_1 = json.dumps({**wrapped_no_derived, "derived_at": "2026-09-01T00:00:00Z"})
    posted_body_2 = json.dumps({**wrapped_no_derived, "derived_at": "2026-09-01T00:05:00Z"})
    # This review's finding 7: the two checks that used to sit here were both non-discriminating --
    # `hash_before == snapshot_hash(wrapped_no_derived)` is `snapshot_hash(x) == snapshot_hash(x)`
    # (holds no matter what main() hashes), and `"derived_at" not in wrapped_no_derived` restates a
    # literal two lines above. Both would still pass if main() hashed `post_body` instead of
    # `wrapped`. The discriminating claim: hashing the POSTED body (which DOES carry derived_at)
    # gives a DIFFERENT hash than hashing `wrapped` alone -- proving the exclusion at :1156/:1168 is
    # load-bearing, not incidental, and this arm would actually fail if a future edit hashed the
    # wrong thing.
    check("hashing the POSTED body (derived_at included) differs from hashing `wrapped` alone -- "
          "main() must hash `wrapped`, never `post_body`, or the change-gate would re-trigger on "
          "derived_at alone",
          snapshot_hash(json.loads(posted_body_1)) != hash_before)
    check("(control) the two posted bodies DO differ -- proving derived_at actually rides the "
          "wire, it just doesn't gate the push",
          posted_body_1 != posted_body_2)

    # -- #1457: snapshot change-gate (KV daily quota) --
    wrapped_a = {"rooms": [{"name": "room-a", "state": "Running"}], "underhood": []}
    wrapped_a_reordered = {"underhood": [], "rooms": [{"state": "Running", "name": "room-a"}]}
    wrapped_b = {"rooms": [{"name": "room-a", "state": "Succeeded"}], "underhood": []}
    hash_a = snapshot_hash(wrapped_a)
    check("snapshot_hash is stable across dict key/field order (sort_keys)",
          hash_a == snapshot_hash(wrapped_a_reordered))
    check("snapshot_hash changes when the wrapped body's content changes",
          hash_a != snapshot_hash(wrapped_b))

    check("a missing persisted hash always pushes (fail toward one extra write)",
          should_push_snapshot({}, hash_a) is True)
    check("an unreadable/missing state.get sentinel (None) never matches a real hash",
          should_push_snapshot({SNAPSHOT_HASH_KEY: None}, hash_a) is True)
    check("a matching persisted hash skips the push",
          should_push_snapshot({SNAPSHOT_HASH_KEY: hash_a}, hash_a) is False)
    check("a stale persisted hash (content changed since) triggers a push",
          should_push_snapshot({SNAPSHOT_HASH_KEY: hash_a}, snapshot_hash(wrapped_b)) is True)

    check("should_log_skip fires on the first skip of a streak",
          should_log_skip(1, 24) is True)
    check("should_log_skip is quiet between the coarse cadence points",
          all(not should_log_skip(n, 24) for n in range(2, 24)))
    check("should_log_skip fires again at the coarse cadence boundary",
          should_log_skip(24, 24) is True and should_log_skip(48, 24) is True)

    # Post-before-save ordering (#1457 review finding A): a raising post must leave the state file
    # untouched; a succeeding one must persist the hash. Real temp file, stubbed post.
    with tempfile.TemporaryDirectory() as tmp:
        sp = Path(tmp) / "push-state.json"

        def _boom(_body):
            raise RuntimeError("post failed")

        try:
            push_snapshot_and_record(_boom, "{}", {}, sp, hash_a)
        except RuntimeError:
            pass
        check("a FAILED post persists nothing (state file untouched, retries next cycle)",
              not sp.exists())
        push_snapshot_and_record(lambda _body: None, "{}", {}, sp, hash_a)
        check("a successful post persists the hash for the next cycle's gate",
              load_push_state(sp).get(SNAPSHOT_HASH_KEY) == hash_a)

    # -- #1505: timeline extraction strips content-bearing fields (the mailbox's stdout boundary) --
    stdout_leak = "SECRET_STDOUT_LINE_THAT_MUST_NEVER_RIDE_THE_MAILBOX"
    fake_room_detail = {
        "name": "room-x",
        "stdout": {"text": stdout_leak, "truncated": False, "totalBytes": 999, "source": "execution_1"},
        "timeline": {
            "entries": [
                {"type": "flow.ExecutionRequestAccepted", "timestamp": "2026-08-31T00:00:00Z"},
                {"type": "core.ExecutionStarted", "timestamp": "2026-08-31T00:00:01Z", "detail": stdout_leak},
            ],
            "truncated": False,
            "totalEntries": 2,
        },
        "note": stdout_leak,
    }
    extracted = extract_timeline(fake_room_detail)
    check("extract_timeline keeps real type+timestamp entries (positive control)",
          extracted == [
              {"type": "flow.ExecutionRequestAccepted", "timestamp": "2026-08-31T00:00:00Z"},
              {"type": "core.ExecutionStarted", "timestamp": "2026-08-31T00:00:01Z"},
          ])
    check("extract_timeline drops an entry's `detail` field even when populated",
          all("detail" not in e for e in extracted))
    # The claim under test is "none of it touches the SNAPSHOT" -- prove it against the body main()
    # actually pushes by building it through the SAME build_wrapped() main() calls, so a future edit
    # to that construction can't silently invalidate this proof.
    wrapped_with_timeline = build_wrapped([], [], {"/rooms/room-x": extracted}, 0)
    serialized = json.dumps(wrapped_with_timeline)
    check("the stdout/detail/note leak string is absent from the fully serialized pushed body",
          stdout_leak not in serialized)

    # Negative control for the positive-control claim above: an extractor that always returns []
    # would also pass the leak check for the wrong reason -- prove real entries actually survive.
    check("(control) a non-empty timeline still carries its real entries into the serialized body",
          "flow.ExecutionRequestAccepted" in serialized)

    unreadable_detail = extract_timeline({
        "timeline": {"entries": [{"type": "unreadable", "detail": "ledger held by pid 1234, path C:\\secret\\room"}],
                     "truncated": False, "totalEntries": 1}
    })
    check("an 'unreadable' marker entry survives as a type-only marker",
          unreadable_detail == [{"type": "unreadable"}])

    check("extract_timeline caps at TIMELINE_CAP entries, keeping the newest tail",
          extract_timeline({"timeline": {"entries": [{"type": f"e{i}"} for i in range(TIMELINE_CAP + 5)],
                                          "truncated": False, "totalEntries": TIMELINE_CAP + 5}}) ==
          [{"type": f"e{i}"} for i in range(5, TIMELINE_CAP + 5)])

    check("extract_timeline degrades to [] for a room_detail response with no timeline at all",
          extract_timeline({"name": "room-y", "note": "no flow.jsonl yet"}) == [])

    # #1537: extract_timeline admits every event TYPE -- it has never filtered on `type`, only on
    # field shape (KEEP-ONLY type+timestamp, see the function's own docstring). This is the
    # discriminating control for that claim: it would fail the moment anyone added a type-keyed
    # allowlist, including one that (wrongly) tried to list "every type we know about today" --
    # the "someFutureType" entry has no home in FlowEvent.cs/CoreEvent.cs/RoomEvent.cs and must
    # still survive. The 29 real tags are current as of this change (10 flow + 2 core + 17 room);
    # they are a snapshot for this test's own realism, not a source of truth the engine must keep
    # in sync -- the engine is the source of truth, and this test doesn't police it.
    every_known_type = [
        "flow.executionRequestAccepted", "flow.executionRequestRejected", "flow.executionSucceeded",
        "flow.executionFailed", "flow.executionCancelled", "flow.cancellationRequested",
        "flow.workflowPaused", "flow.externalDecisionRecorded", "flow.workflowResumed",
        "flow.stepRetryScheduled",
        "core.executionStarted", "core.executionExited",
        "room.heldWorkDispatched", "room.heldWorkEscalated", "room.heldWorkResolved",
        "room.grantRecorded", "room.grantAmended", "room.grantRevoked", "room.escalationRaised",
        "room.turnHostDormancyEntered", "room.turnHostDormancyCleared",
        "room.runtimePermissionAsked", "room.runtimePermissionAnswered", "room.runtimePermissionRevoked",
        "room.workflowSwitched", "room.standingPermissionRevoked",
        "room.workerJoined", "room.workerRenamed", "room.orchestratorAssigned",
        "flow.someFutureType",
    ]
    assert len(every_known_type) <= TIMELINE_CAP, \
        "synthetic list outgrew TIMELINE_CAP -- shorten it; this is not a filter to widen"
    admitted = extract_timeline({
        "timeline": {"entries": [{"type": t, "timestamp": "2026-08-31T00:00:00Z"} for t in every_known_type],
                     "truncated": False, "totalEntries": len(every_known_type)}
    })
    check("extract_timeline admits every event type unfiltered, known or not -- no type-keyed allowlist",
          [e["type"] for e in admitted] == every_known_type)

    # -- #1613 item 4: stepId/exitCode are ids/counts, kept -- but only where the entry has them,
    # never fabricated, and the stdout leak check above still holds with them present --
    step_exit_entries = extract_timeline({
        "timeline": {
            "entries": [
                {"type": "flow.executionRequestAccepted", "timestamp": "2026-09-01T00:00:00Z", "stepId": "build"},
                {"type": "core.executionExited", "timestamp": "2026-09-01T00:00:05Z", "exitCode": 0},
                {"type": "core.executionExited", "timestamp": "2026-09-01T00:00:06Z", "exitCode": -1},
                {"type": "flow.executionSucceeded", "timestamp": "2026-09-01T00:00:07Z"},
            ],
            "truncated": False, "totalEntries": 4,
        }
    })
    check("extract_timeline keeps stepId where the entry carries one",
          step_exit_entries[0] == {"type": "flow.executionRequestAccepted", "timestamp": "2026-09-01T00:00:00Z", "stepId": "build"})
    check("extract_timeline keeps exitCode where the entry carries one, including zero and negative",
          step_exit_entries[1]["exitCode"] == 0 and step_exit_entries[2]["exitCode"] == -1)
    check("extract_timeline omits stepId/exitCode where the entry carries neither",
          "stepId" not in step_exit_entries[3] and "exitCode" not in step_exit_entries[3])
    check("extract_timeline never invents stepId/exitCode on an entry that lacks them",
          "exitCode" not in step_exit_entries[0] and "stepId" not in step_exit_entries[1])

    # -- #1613 item 4: terminal-timeline caching policy (fetch once, not per cycle) --
    fetch_calls = []

    def counting_fetch(room_path):
        fetch_calls.append(room_path)
        return [{"type": "flow.executionSucceeded"}]

    term_cache: dict = {}
    first = resolve_room_timeline("/rooms/term-a", True, term_cache, counting_fetch)
    check("first call for a terminal room fetches", fetch_calls == ["/rooms/term-a"])
    check("first call's result is cached", term_cache.get("/rooms/term-a") == first)
    second = resolve_room_timeline("/rooms/term-a", True, term_cache, counting_fetch)
    check("a SECOND cycle's call for the SAME terminal room does NOT fetch again (cache hit)",
          fetch_calls == ["/rooms/term-a"] and second == first)

    fetch_calls.clear()
    resolve_room_timeline("/rooms/live-a", False, term_cache, counting_fetch)
    resolve_room_timeline("/rooms/live-a", False, term_cache, counting_fetch)
    check("(control) a non-terminal room fetches on EVERY call, cache or not",
          fetch_calls == ["/rooms/live-a", "/rooms/live-a"])

    empty_calls = []

    def empty_fetch(room_path):
        empty_calls.append(room_path)
        return []

    empty_cache: dict = {}
    resolve_room_timeline("/rooms/term-empty", True, empty_cache, empty_fetch)
    resolve_room_timeline("/rooms/term-empty", True, empty_cache, empty_fetch)
    check("a terminal room whose fetch returns [] is never cached, so it retries every cycle",
          empty_calls == ["/rooms/term-empty", "/rooms/term-empty"]
          and "/rooms/term-empty" not in empty_cache)

    # -- #1613 item 1: live telemetry for Running rooms --
    check("extract_live_counts counts claude tool_use blocks across assistant events",
          extract_live_counts([
              json.dumps({"type": "assistant", "message": {"content": [{"type": "text", "text": "hi"}]}}),
              json.dumps({"type": "assistant", "message": {"content": [
                  {"type": "tool_use", "name": "Bash", "input": {"command": "ls"}},
                  {"type": "tool_use", "name": "Read", "input": {"path": "x"}},
              ]}}),
          ]) == {"toolCalls": 2})
    check("extract_live_counts counts agy DONE/tool step_update heartbeats",
          extract_live_counts([
              json.dumps({"event": "init"}),
              json.dumps({"event": "step_update", "step_update": {"state": "ACTIVE", "step_type": "tool"}}),
              json.dumps({"event": "step_update", "step_update": {"state": "DONE", "step_type": "tool"}}),
              json.dumps({"event": "step_update", "step_update": {"state": "DONE", "step_type": "agent_response"}}),
          ]) == {"toolCalls": 1})
    check("extract_live_counts ignores a torn/unparseable last line instead of raising",
          extract_live_counts(['{"type": "assistant", "message": {"content": [{"type": "tool_use"}]}}',
                                '{"type": "assistant", "message": {"conte']) == {"toolCalls": 1})
    check("extract_live_counts also counts a tool step at its ERROR terminal state, not DONE only "
          "(#1686 review F3 -- mirrors the engine's own ClaudeUsageParser/AgyUsageParser.CountToolSteps "
          "DONE-or-ERROR unit; previously a failed agy tool call incremented the engine's arrest count "
          "without incrementing the operator's lane-card count)",
          extract_live_counts([
              json.dumps({"event": "step_update", "step_update": {"state": "ERROR", "step_type": "tool"}}),
          ]) == {"toolCalls": 1})
    # -- #1682: billed tokens/turns for BOTH vendors, on the shape a real capture confirmed
    # 2026-09-01/02 (docs/vendor-capabilities.md) -- `message.usage` on every claude `assistant`
    # line and agy's DONE/agent_response `step_update.usage`, not just either vendor's terminal line.
    real_assistant_usage_line = json.dumps({
        "type": "assistant",
        "message": {
            "content": [{"type": "text", "text": "ok"}],
            "usage": {
                "input_tokens": 2, "cache_creation_input_tokens": 12066,
                "cache_read_input_tokens": 15092, "output_tokens": 4,
                "service_tier": "standard",
            },
        },
    })
    real_counts = extract_live_counts([real_assistant_usage_line])
    check("billedTokens is cache_creation ALONE off the real captured claude envelope shape (#1706 -- "
          "NOT input/output, which are mid-stream placeholder values on this line; NOT thinking; and NOT "
          "cache_read, which is display-only)",
          real_counts.get("billedTokens") == 12066)
    check("a claude batch marks billedTokens as a floor (#1706)", real_counts.get("billedIsFloor") is True)
    check("turns is 1 for a single usage-bearing line", real_counts.get("turns") == 1)
    check("contextTokens sums the message's three input-side usage counts (fresh input plus both "
          "cache counters)",
          real_counts.get("context", {}).get("contextTokens") == 2 + 12066 + 15092)
    check("cacheReadTokens is cache_read_input_tokens alone",
          real_counts.get("context", {}).get("cacheReadTokens") == 15092)

    additive_claude_lines = [
        json.dumps({"type": "assistant", "message": {"usage": {
            "input_tokens": 2, "output_tokens": 3, "cache_creation_input_tokens": 100,
            "cache_read_input_tokens": 0}}}),
        json.dumps({"type": "assistant", "message": {"usage": {
            "input_tokens": 2, "output_tokens": 3, "cache_creation_input_tokens": 60,
            "cache_read_input_tokens": 0}}}),
    ]
    check("billedTokens/turns are ADDITIVE across multiple assistant messages in one batch "
          "(whole-tree, including subagent assistant lines, which are never filtered out)",
          extract_live_counts(additive_claude_lines).get("billedTokens") == 160
          and extract_live_counts(additive_claude_lines).get("turns") == 2)

    # -- #1706: the twin of the engine's own measurement, on a REAL consecutive line pair from room
    # dispatch-implement-3dc5e21a's `.stdout.log` (the same pair
    # TokenBudgetMonitorTests.The_dedupe_premise_holds_on_a_real_consecutive_pair_from_room_3dc5e21a
    # uses -- one home for the fixture's provenance, two languages reading it). Its `input_tokens` of
    # 2 and `output_tokens` of 1 are the placeholders; only the 39,901 cache-creation figure is real.
    real_pair_1706 = [
        json.dumps({"type": "assistant", "message": {
            "id": "msg_011Cee7wqgwCecnuPg5NCH6y", "content": [{"type": "text"}],
            "usage": {"input_tokens": 2, "cache_creation_input_tokens": 39901,
                      "cache_read_input_tokens": 0, "output_tokens": 1}}}),
        json.dumps({"type": "assistant", "message": {
            "id": "msg_011Cee7wqgwCecnuPg5NCH6y", "content": [{"type": "tool_use"}],
            "usage": {"input_tokens": 2, "cache_creation_input_tokens": 39901,
                      "cache_read_input_tokens": 0, "output_tokens": 1}}}),
    ]
    real_pair_counts = extract_live_counts(real_pair_1706)
    check("#1706: the real captured pair bills 39,901 -- cache_creation once, deduped, with neither "
          "placeholder column added (pre-#1706 this read 2 + 1 + 39,901)",
          real_pair_counts.get("billedTokens") == 39901)
    check("#1706: that real pair is marked a floor", real_pair_counts.get("billedIsFloor") is True)
    check("#1706: billedIsFloor is STICKY across batches -- a later batch with no claude usage at all "
          "never clears a floor an earlier batch established",
          (lambda state: (_apply_live_delta(state, extract_live_counts(real_pair_1706)),
                          _apply_live_delta(state, extract_live_counts(
                              [json.dumps({"event": "step_update", "step_update": {
                                  "state": "DONE", "step_type": "agent_response",
                                  "usage": {"input_tokens": 5, "output_tokens": 5}}})])),
                          state["counts"].get("billedIsFloor"))[-1])(
              {"counts": {"toolCalls": 0}, "context": None}) is True)
    check("context is the LATEST message's level within a batch, never summed across messages",
          extract_live_counts([
              json.dumps({"type": "assistant", "message": {"usage": {
                  "output_tokens": 1, "input_tokens": 100, "cache_read_input_tokens": 0,
                  "cache_creation_input_tokens": 0}}}),
              json.dumps({"type": "assistant", "message": {"usage": {
                  "output_tokens": 1, "input_tokens": 5, "cache_read_input_tokens": 200,
                  "cache_creation_input_tokens": 0}}}),
          ]).get("context") == {"contextTokens": 205, "cacheReadTokens": 200})
    # #1666 review F5: parent trio (300) -> sub-agent trio with a SMALLER context (40) -> parent trio
    # with a SMALLER value than the first (100, e.g. a genuine post-compaction drop). Mirrors the
    # engine's TokenBudgetMonitorTests same-bucket-drop arm: the sub-agent line must not touch
    # `context` at all, but a later genuine parent drop still must.
    context_bucket_lines = [
        json.dumps({"type": "assistant", "message": {"usage": {
            "output_tokens": 1, "input_tokens": 5, "cache_read_input_tokens": 290,
            "cache_creation_input_tokens": 5}}}),
        json.dumps({"type": "assistant", "parent_tool_use_id": "toolu_01subagent",
                    "message": {"usage": {
                        "output_tokens": 1, "input_tokens": 5, "cache_read_input_tokens": 30,
                        "cache_creation_input_tokens": 5}}}),
    ]
    check("a sub-agent trio line (root parent_tool_use_id a string) leaves `context` UNCHANGED, "
          "matching the engine's cleared-bucket rule that a sub-agent reading never sets the parent's "
          "reported level",
          extract_live_counts(context_bucket_lines).get("context")
          == {"contextTokens": 300, "cacheReadTokens": 290})
    context_bucket_lines_then_parent_drop = context_bucket_lines + [
        json.dumps({"type": "assistant", "message": {"usage": {
            "output_tokens": 1, "input_tokens": 5, "cache_read_input_tokens": 90,
            "cache_creation_input_tokens": 5}}}),
    ]
    check("a later PARENT trio line with a smaller value still drops `context` -- a genuine drop is "
          "never pinned by an earlier, larger sub-agent reading",
          extract_live_counts(context_bucket_lines_then_parent_drop).get("context")
          == {"contextTokens": 100, "cacheReadTokens": 90})
    check("billedTokens/turns are ABSENT, never a substituted zero, when no line reports usage",
          "billedTokens" not in extract_live_counts([
              json.dumps({"type": "assistant", "message": {"content": [{"type": "tool_use"}]}})])
          and "turns" not in extract_live_counts([
              json.dumps({"type": "assistant", "message": {"content": [{"type": "tool_use"}]}})]))
    check("context is ABSENT when the cache fields aren't ALL present -- never a partial figure "
          "built from input_tokens alone (the trap the original ruling correctly named)",
          "context" not in extract_live_counts([
              json.dumps({"type": "assistant", "message": {"usage": {"output_tokens": 4, "input_tokens": 2}}})]))
    check("agy DONE/tool step_update heartbeats contribute no token fields (no usage on that step_type)",
          extract_live_counts([
              json.dumps({"event": "step_update", "step_update": {"state": "DONE", "step_type": "tool"}})
          ]) == {"toolCalls": 1})
    # #1682: corrects the prior claim that agy carries "no usage field to read at all" -- a real
    # capture (dispatch-implement-38c24d11) shows DONE/agent_response step_updates DO carry one.
    real_agy_usage_line = json.dumps({
        "event": "step_update",
        "step_update": {
            "state": "DONE", "step_type": "agent_response",
            "usage": {"input_tokens": 14205, "output_tokens": 443, "thinking_tokens": 349,
                       "cache_read_tokens": 0, "total_tokens": 14648},
        },
    })
    real_agy_counts = extract_live_counts([real_agy_usage_line])
    check("billedTokens reads agy's DONE/agent_response step_update.usage (input + output, NOT thinking)",
          real_agy_counts.get("billedTokens") == 14205 + 443)
    check("turns is 1 for a single agy usage-bearing line", real_agy_counts.get("turns") == 1)
    check("#1706 POLARITY: agy's step_update usage carries its REAL input/output, so its billed figure "
          "is a measurement and billedIsFloor is absent -- without this arm a rule that marked every "
          "batch a floor would pass every claude check above",
          "billedIsFloor" not in real_agy_counts)
    check("agy step_update contributes no `context` -- claude-only (no cache_creation figure to build a trio from)",
          "context" not in real_agy_counts)
    check("a terminal `result` line's usage never leaks into live counts -- only type==assistant/step_update are read",
          extract_live_counts([
              json.dumps({"type": "result", "usage": {"output_tokens": 999, "input_tokens": 999}})
          ]) == {"toolCalls": 0})

    # -- #1886: the codex envelope. The SAME sanitized capture the engine-side arm reads
    # (FleetProjectionWriterTests.RunningRoom_CodexAdapter_ProjectsLiveToolCallsTurnsAndBilledTokens),
    # so the two arithmetics are pinned against one stream rather than two hand-written fixtures that
    # can drift apart -- the shared-fixture discipline `_selftest_claude_billing_gate` already uses.
    codex_fixture = (Path(__file__).resolve().parent.parent.parent
                     / "tests" / "Baton.Cli.Tests" / "Fixtures" / "codex-live-stream.jsonl")
    codex_lines = codex_fixture.read_text(encoding="utf-8").splitlines()
    codex_counts = extract_live_counts(codex_lines)
    check("#1886: the real codex capture's mcp_tool_call items are COUNTED, not read as 0 -- the "
          "reported symptom (`0 calls` beside a stream carrying hundreds)",
          codex_counts.get("toolCalls") == 128)
    check("#1886: codex billedTokens matches the engine's own arithmetic off the same turn.completed "
          "-- fresh input (127,806 - 126,720) + output 689 + cache write 0, reasoning excluded",
          codex_counts.get("billedTokens") == 1_775)
    check("#1886: one turn.completed is one turn (structurally sparse on this vendor -- a single turn "
          "can sit behind hundreds of tool calls, unlike claude's per-message turns)",
          codex_counts.get("turns") == 1)
    check("#1886: codex context level is input + cache write, and cacheReadTokens is the cached "
          "component alone -- the LEVEL TokenBudgetMonitor reports, never a sum across turns",
          codex_counts.get("context") == {"contextTokens": 127_806, "cacheReadTokens": 126_720})
    check("#1886 POLARITY: billedIsFloor is ABSENT on codex, not merely false -- without this arm a "
          "rule that marked every batch a floor would pass every check above",
          "billedIsFloor" not in codex_counts)
    check("#1886: an item.completed is not counted a second time -- only item.started is a tool step, "
          "the same unit CodexUsageParser.CountToolSteps accumulates",
          extract_live_counts([
              json.dumps({"type": "item.started", "item": {"type": "mcp_tool_call", "tool": "t"}}),
              json.dumps({"type": "item.completed", "item": {"type": "mcp_tool_call", "tool": "t"}}),
          ]).get("toolCalls") == 1)
    check("#1886: a codex item type that is not a tool (agent_message) is not a tool step",
          extract_live_counts([
              json.dumps({"type": "item.completed", "item": {"type": "agent_message", "text": "hi"}}),
          ]).get("toolCalls") == 0)
    # The absent-vs-real-zero boundary on the FALLBACK reader, the Python twin of the two daemon arms
    # RunningRoom_CodexAdapter_ToolItemsWithoutCompletedTurn_OmitsUsageFields /
    # RunningRoom_CodexAdapter_NoToolItems_ReportsRealZeroToolCalls. Both compare the WHOLE returned
    # dict rather than checking three keys `not in` it: that pins the absence of every other field at
    # once, so a future arm that starts fabricating (say) a `turns: 0` cannot slip past.
    check("#1886: a codex stream with tool items but NO turn.completed reports the tool count and "
          "NOTHING else -- no usage line has been read, and a substituted 0 for billedTokens/turns/"
          "context would read on the glass as a lane that has burned nothing",
          json.loads(codex_lines[-1])["type"] == "turn.completed"  # the slice below is meaningless without this
          and extract_live_counts(codex_lines[:-1]) == {"toolCalls": 128})
    check("#1886 POLARITY: a codex stream carrying neither a tool item nor a completed turn reports a "
          "REAL 0 -- the envelope is understood, the count was actually taken. One condition from the "
          "unknown-envelope arm below, which reports {} instead.",
          extract_live_counts([
              json.dumps({"type": "thread.started", "thread_id": "th_1"}),
              json.dumps({"type": "turn.started"}),
          ]) == {"toolCalls": 0})
    # The absent-not-zero half. Red before the #1886 fix: `extract_live_counts` returned
    # `{"toolCalls": 0}` unconditionally, and `live_telemetry_for_room` pre-seeded the same 0, so an
    # unreadable envelope reported a count it had never taken.
    check("#1886: a batch matching NO known envelope reports toolCalls ABSENT, never 0 -- a zero from "
          "this function has to mean a stream it understands that has called no tool",
          extract_live_counts([json.dumps({"kind": "some-future-vendor-event", "n": 3})]) == {})
    check("#1886 POLARITY: a KNOWN envelope with no tool call in it still reports a real 0 -- a claude "
          "stream's opening `system` init line has honestly counted none, and must not go absent",
          extract_live_counts([json.dumps({"type": "system", "subtype": "init"})]) == {"toolCalls": 0})
    check("#1886: _apply_live_delta gates on PRESENCE -- an unknown-envelope batch leaves the running "
          "counts untouched rather than seeding a 0 into them",
          (lambda state: (_apply_live_delta(state, extract_live_counts(
              [json.dumps({"kind": "some-future-vendor-event"})])), state["counts"])[-1])(
              {"counts": {}, "context": None}) == {})

    # #1686 review F6 -- extract_live_counts's own docstring above has the measured shape this
    # reproduces; dedupe by message.id closes it.
    def _dup_line(message_id: str, cache_creation: int) -> str:
        return json.dumps({"type": "assistant", "message": {"id": message_id, "usage": {
            "input_tokens": 2, "output_tokens": 3, "cache_creation_input_tokens": cache_creation,
            "cache_read_input_tokens": 0}}})

    dup_message_lines = [_dup_line("msg_1", 110), _dup_line("msg_1", 110), _dup_line("msg_2", 55)]
    dup_seen_ids: set = set()
    dup_counts = extract_live_counts(dup_message_lines, dup_seen_ids)
    check("billedTokens dedupes a repeated message.id instead of summing it twice",
          dup_counts.get("billedTokens") == 110 + 55)
    check("turns dedupes the same way", dup_counts.get("turns") == 2)

    # A repeat that arrives in a LATER batch (a later poll cycle) must still dedupe against the SAME
    # persistent seen_message_ids the caller threads through live_cache's per-execution state.
    later_batch_counts = extract_live_counts([_dup_line("msg_1", 110)], dup_seen_ids)
    check("a repeated message.id in a LATER batch (persistent seen-set) still dedupes",
          "billedTokens" not in later_batch_counts)

    check("live_telemetry_for_room is None with no Running step",
          live_telemetry_for_room({"path": "/rooms/x", "steps": [{"id": "s1", "state": "Succeeded"}]}) is None)
    check("live_telemetry_for_room is None when the Running step has no captured stdout yet",
          live_telemetry_for_room({
              "path": str(Path(tempfile.mkdtemp()) / "nonexistent-room"),
              "steps": [{"id": "s1", "state": "Running", "execution": "exec-none"}],
          }) is None)

    with tempfile.TemporaryDirectory() as tmp:
        room_dir = Path(tmp) / "live-room"
        exec_dir = room_dir / "artifacts" / "execution_exec-live-1"
        exec_dir.mkdir(parents=True)
        (exec_dir / ".stdout.log").write_text(
            json.dumps({"type": "assistant", "message": {"content": [{"type": "tool_use", "name": "Bash"}]}}) + "\n",
            encoding="utf-8")
        live = live_telemetry_for_room({
            "path": str(room_dir),
            "steps": [{"id": "s1", "state": "Running", "execution": "exec-live-1"}],
        })
        check("live_telemetry_for_room reads the Running step's own .stdout.log and counts tool calls",
              live is not None and live["toolCalls"] == 1)
        check("live_telemetry_for_room's lastActivityAt is a real ISO instant (the file's own mtime)",
              live is not None and isinstance(live.get("lastActivityAt"), str) and "T" in live["lastActivityAt"])

        pruned_dir = room_dir / "artifacts" / "pruned" / "execution_exec-pruned-1"
        pruned_dir.mkdir(parents=True)
        (pruned_dir / ".stdout.log").write_text(
            json.dumps({"event": "step_update", "step_update": {"state": "DONE", "step_type": "tool"}}) + "\n",
            encoding="utf-8")
        live_pruned = live_telemetry_for_room({
            "path": str(room_dir),
            "steps": [{"id": "s1", "state": "Running", "execution": "exec-pruned-1"}],
        })
        check("live_telemetry_for_room falls back to artifacts/pruned, same as the engine side",
              live_pruned is not None and live_pruned["toolCalls"] == 1)

    running_room = {"path": "/rooms/r", "state": "Running",
                     "steps": [{"id": "s1", "state": "Running", "execution": "exec-none"}]}
    stalled_room = {"path": "/rooms/s", "state": "Stalled",
                     "steps": [{"id": "s1", "state": "Running", "execution": "exec-none"}]}
    room_list_for_live = [running_room, stalled_room]
    attach_live_telemetry(room_list_for_live, {})
    check("attach_live_telemetry never adds a `live` key it cannot honestly back (no stdout yet)",
          "live" not in running_room)
    check("attach_live_telemetry gates on the DISPLAYED state, never touching a Stalled room "
          "(#1513 confirmed-dead) even though its raw step still reads Running",
          "live" not in stalled_room)

    # #1155: pruned_info_for_room / attach_pruned_info -- red first, the two selftest arms the
    # issue asked for: directory absent -> no field; directory present with 25 -> count 25, 20 newest.
    with tempfile.TemporaryDirectory() as tmp:
        no_pruned_room_dir = Path(tmp) / "no-pruned-room"
        (no_pruned_room_dir / "artifacts").mkdir(parents=True)
        check("pruned_info_for_room is None when artifacts/pruned/ does not exist",
              pruned_info_for_room({"path": str(no_pruned_room_dir)}) is None)

        no_pruned_room = {"path": str(no_pruned_room_dir)}
        attach_pruned_info([no_pruned_room], {})
        check("attach_pruned_info never adds a `pruned` key it cannot back (no pruned/ dir)",
              "pruned" not in no_pruned_room)

        many_pruned_room_dir = Path(tmp) / "many-pruned-room"
        pruned_root = many_pruned_room_dir / "artifacts" / "pruned"
        pruned_root.mkdir(parents=True)
        for i in range(25):
            exec_dir = pruned_root / f"execution_{i:02d}"
            exec_dir.mkdir()
            (exec_dir / "report.md").write_text(f"pruned artifact {i}", encoding="utf-8")
            mtime = time.time() - (25 - i)  # execution_00 oldest, execution_24 newest
            os.utime(exec_dir, (mtime, mtime))

        pruned_info = pruned_info_for_room({"path": str(many_pruned_room_dir)})
        check("pruned_info_for_room reports the true total count (25), not just the capped list",
              pruned_info is not None and pruned_info["count"] == 25)
        check("pruned_info_for_room caps `items` at PRUNED_ITEMS_CAP (20)",
              pruned_info is not None and len(pruned_info["items"]) == 20)
        check("pruned_info_for_room's `items` are the 20 NEWEST by prunedAt, not the first 20 found",
              pruned_info is not None
              and {i["name"] for i in pruned_info["items"]}
              == {f"execution_{n:02d}" for n in range(5, 25)})
        check("pruned_info_for_room's items carry name/bytes/prunedAt",
              pruned_info is not None
              and all(isinstance(i["name"], str) and isinstance(i["bytes"], int)
                      and isinstance(i["prunedAt"], str) and "T" in i["prunedAt"]
                      for i in pruned_info["items"]))
        check("pruned_info_for_room sums a pruned execution dir's bytes from its files",
              pruned_info is not None
              and next(i for i in pruned_info["items"] if i["name"] == "execution_24")["bytes"]
              == len("pruned artifact 24"))

        many_pruned_room = {"path": str(many_pruned_room_dir)}
        attach_pruned_info([many_pruned_room], {})
        check("attach_pruned_info attaches the same `pruned` shape in place",
              many_pruned_room.get("pruned", {}).get("count") == 25)

        empty_subdirs_room_dir = Path(tmp) / "empty-subdirs-room"
        empty_pruned_root = empty_subdirs_room_dir / "artifacts" / "pruned"
        empty_pruned_root.mkdir(parents=True)
        (empty_pruned_root / "execution_empty-1").mkdir()
        (empty_pruned_root / "execution_empty-2").mkdir()
        empty_pruned_info = pruned_info_for_room({"path": str(empty_subdirs_room_dir)})
        check("pruned_info_for_room attaches entries with bytes: 0 for pruned dirs holding only "
              "empty subdirectories, rather than treating them as absent (#1756 review F3)",
              empty_pruned_info is not None and empty_pruned_info["count"] == 2
              and all(i["bytes"] == 0 for i in empty_pruned_info["items"]))

    # #1756 review F2: pruned_info_cache -- a second call with an unchanged pruned/ dir hits the
    # cache (no rglob walk); a new pruned entry changes the dir's (mtime, child count) key and
    # invalidates it.
    with tempfile.TemporaryDirectory() as tmp:
        cache_room_dir = Path(tmp) / "cache-room"
        cache_pruned_root = cache_room_dir / "artifacts" / "pruned"
        cache_pruned_root.mkdir(parents=True)
        (cache_pruned_root / "execution_cache-1").mkdir()

        rglob_calls = {"n": 0}
        real_rglob = Path.rglob

        def counting_rglob(self, pattern):
            rglob_calls["n"] += 1
            return real_rglob(self, pattern)

        pruned_cache: dict = {}
        cache_room = {"path": str(cache_room_dir)}
        Path.rglob = counting_rglob
        try:
            first = pruned_info_for_room(cache_room, pruned_cache)
            calls_after_first = rglob_calls["n"]
            check("pruned_info_for_room walks the tree on the first call",
                  first is not None and calls_after_first > 0)

            second = pruned_info_for_room(cache_room, pruned_cache)
            check("pruned_info_for_room's second call over an UNCHANGED pruned/ dir hits the "
                  "cache -- no additional rglob walk (#1756 review F2)",
                  second == first and rglob_calls["n"] == calls_after_first)

            (cache_pruned_root / "execution_cache-2").mkdir()
            third = pruned_info_for_room(cache_room, pruned_cache)
            check("pruned_info_for_room's cache invalidates when a new pruned entry appears "
                  "(child count changes the cache key)",
                  third is not None and third["count"] == 2
                  and rglob_calls["n"] > calls_after_first)
        finally:
            Path.rglob = real_rglob

    # -- this review, finding 4: incremental reading -- a second cycle over an UNCHANGED cache only
    # counts newly-appended bytes, never re-parses the whole file. --
    with tempfile.TemporaryDirectory() as tmp:
        room_dir = Path(tmp) / "incremental-room"
        exec_dir = room_dir / "artifacts" / "execution_exec-inc-1"
        exec_dir.mkdir(parents=True)
        stdout_path = exec_dir / ".stdout.log"
        stdout_path.write_text(json.dumps(
            {"type": "assistant", "message": {"content": [{"type": "tool_use", "name": "Bash"}]}}) + "\n",
            encoding="utf-8")
        inc_room = {"path": str(room_dir), "steps": [{"id": "s1", "state": "Running", "execution": "exec-inc-1"}]}
        inc_cache: dict = {}
        inc_live1 = live_telemetry_for_room(inc_room, inc_cache)
        check("first cycle counts the initial tool call", inc_live1["toolCalls"] == 1)
        state_after_1 = inc_cache[f"{room_dir}::exec-inc-1"]
        check("first cycle's offset advances to the end of the file it just read",
              state_after_1["stdout_offset"] == stdout_path.stat().st_size)

        with stdout_path.open("a", encoding="utf-8") as f:
            f.write(json.dumps(
                {"type": "assistant", "message": {"content": [{"type": "tool_use", "name": "Read"}]}}) + "\n")
        inc_live2 = live_telemetry_for_room(inc_room, inc_cache)
        check("second cycle ADDS only the newly appended tool call -- proving this reads the delta, "
              "not the whole file again (a whole-file re-read would also land on 2, so this is "
              "checked together with the offset assertion above/below, not alone)",
              inc_live2["toolCalls"] == 2)
        check("a cycle with nothing new appended leaves the offset (and count) unchanged",
              live_telemetry_for_room(inc_room, inc_cache)["toolCalls"] == 2)

    # -- this review, finding 3: `.stdout.log` rollover at 8 MiB (ExecutionStreamLogger.cs) must
    # never silently reset the count to zero -- a size DECREASE is the rollover signal. --
    with tempfile.TemporaryDirectory() as tmp:
        room_dir = Path(tmp) / "rollover-room"
        exec_dir = room_dir / "artifacts" / "execution_exec-roll-1"
        exec_dir.mkdir(parents=True)
        stdout_path = exec_dir / ".stdout.log"

        def _tool_line(name):
            return json.dumps({"type": "assistant", "message": {"content": [{"type": "tool_use", "name": name}]}}) + "\n"

        stdout_path.write_text(_tool_line("Bash"), encoding="utf-8")
        roll_room = {"path": str(room_dir), "steps": [{"id": "s1", "state": "Running", "execution": "exec-roll-1"}]}
        roll_cache: dict = {}
        check("pre-rollover: first cycle counts the initial tool call",
              live_telemetry_for_room(roll_room, roll_cache)["toolCalls"] == 1)
        with stdout_path.open("a", encoding="utf-8") as f:
            f.write(_tool_line("Read"))
        check("pre-rollover: second cycle adds the newly appended call",
              live_telemetry_for_room(roll_room, roll_cache)["toolCalls"] == 2)

        # Simulate ExecutionStreamLogger's single rollover: a REAL rollover is a rename, so the
        # moved file carries exactly what the pusher had already (fully) caught up to -- a fresh,
        # much smaller `.stdout.log` starts alongside it.
        rollover_path = exec_dir / ".stdout.log.1"
        rollover_path.write_text(stdout_path.read_text(encoding="utf-8"), encoding="utf-8")
        stdout_path.write_text(_tool_line("Grep"), encoding="utf-8")
        live_after_rollover = live_telemetry_for_room(roll_room, roll_cache)
        check("finding 3: toolCalls stays MONOTONIC across a rollover -- the pre-rollover count is "
              "preserved (never reset to zero) and the post-rollover file's own new call is added",
              live_after_rollover["toolCalls"] == 3)
        check("a cycle AFTER the rollover, with nothing new, never re-counts the rollover file again",
              live_telemetry_for_room(roll_room, roll_cache)["toolCalls"] == 3)

    # -- this review, finding 1: lastActivityAt is quantized to a coarse bucket before it enters the
    # payload, bounding snapshot_hash churn for a continuously-streaming lane. --
    bucket_aligned_base = LAST_ACTIVITY_BUCKET_SECONDS * 10.0  # exactly on a bucket boundary
    check("two mtimes inside the same bucket produce an identical lastActivityAt",
          _quantized_activity_iso(bucket_aligned_base)
          == _quantized_activity_iso(bucket_aligned_base + LAST_ACTIVITY_BUCKET_SECONDS - 1))
    check("crossing a bucket boundary changes lastActivityAt",
          _quantized_activity_iso(bucket_aligned_base)
          != _quantized_activity_iso(bucket_aligned_base + LAST_ACTIVITY_BUCKET_SECONDS))

    with tempfile.TemporaryDirectory() as tmp:
        room_dir = Path(tmp) / "streaming-room"
        exec_dir = room_dir / "artifacts" / "execution_exec-stream-1"
        exec_dir.mkdir(parents=True)
        stdout_path = exec_dir / ".stdout.log"
        stdout_path.write_text("", encoding="utf-8")

        def _streaming_room():
            return {"name": "streaming-room", "path": str(room_dir), "state": "Running",
                    "steps": [{"id": "s1", "state": "Running", "execution": "exec-stream-1"}]}

        stream_cache: dict = {}
        # Bucket-aligned so "+10" is guaranteed to stay inside the same bucket below.
        base_mtime = (1_700_000_000.0 // LAST_ACTIVITY_BUCKET_SECONDS) * LAST_ACTIVITY_BUCKET_SECONDS
        os.utime(stdout_path, (base_mtime, base_mtime))
        rooms_1 = [_streaming_room()]
        attach_live_telemetry(rooms_1, stream_cache)
        hash_1 = snapshot_hash(build_wrapped(rooms_1, [], {}, 0))

        os.utime(stdout_path, (base_mtime + 10, base_mtime + 10))  # same bucket
        rooms_2 = [_streaming_room()]
        attach_live_telemetry(rooms_2, stream_cache)
        hash_2 = snapshot_hash(build_wrapped(rooms_2, [], {}, 0))
        check("finding 1: mtime advancing WITHIN one bucket leaves the pushed snapshot hash "
              "unchanged -- a continuously-streaming lane no longer forces a push every cycle",
              hash_1 == hash_2)

        os.utime(stdout_path, (base_mtime + LAST_ACTIVITY_BUCKET_SECONDS, base_mtime + LAST_ACTIVITY_BUCKET_SECONDS))
        rooms_3 = [_streaming_room()]
        attach_live_telemetry(rooms_3, stream_cache)
        hash_3 = snapshot_hash(build_wrapped(rooms_3, [], {}, 0))
        check("finding 1: crossing a bucket boundary DOES change the pushed snapshot hash",
              hash_1 != hash_3)

    # -- this review, finding 2: pending_push_age_s -- absent when nothing is waiting to push, and
    # growing from the last SUCCESSFUL push while content keeps failing to go out. --
    check("pending_push_age_s is None when the persisted hash already matches (nothing waiting)",
          pending_push_age_s({SNAPSHOT_HASH_KEY: "h", LAST_PUSH_TS_KEY: 0.0}, "h", 10_000.0) is None)
    check("pending_push_age_s is None with no successful-push baseline yet, even if content waits",
          pending_push_age_s({}, "h", 10_000.0) is None)
    check("pending_push_age_s is the elapsed time since the last SUCCESSFUL push, while content "
          "still differs from the persisted hash",
          pending_push_age_s({LAST_PUSH_TS_KEY: 9_000.0}, "h", 10_000.0) == 1_000.0)

    # -- #1710: stdout tail for a Running room's detail pane -- the cap, the per-line secret gate, and
    # absence on both a terminal room and a missing log. --
    with tempfile.TemporaryDirectory() as tmp:
        room_dir = Path(tmp) / "tail-room"
        exec_dir = room_dir / "artifacts" / "execution_exec-tail-1"
        exec_dir.mkdir(parents=True)
        stdout_path = exec_dir / ".stdout.log"

        # A 5 MB log: a giant early line neither the read window nor the line cap should ever
        # surface, followed by 60 lines long enough (~150 bytes each) that the last 40 of them alone
        # exceed STDOUT_TAIL_MAX_BYTES -- exercising the truncate-from-the-front path, not just the
        # line-count cap.
        big_line = "x" * (5 * 1024 * 1024)
        tail_lines = [f"line-{i:03d}-" + "y" * 140 for i in range(60)]
        stdout_path.write_text(big_line + "\n" + "\n".join(tail_lines) + "\n", encoding="utf-8")
        tail = stdout_tail_for_room(str(room_dir), "exec-tail-1", [])
        check("stdout_tail_for_room caps a 5 MB log at STDOUT_TAIL_MAX_BYTES",
              tail is not None and len(tail.encode("utf-8")) <= STDOUT_TAIL_MAX_BYTES)
        check("stdout_tail_for_room never surfaces the padding line that pushed the log to 5 MB",
              tail is not None and "x" * 100 not in tail)
        check("stdout_tail_for_room keeps only the newest lines (the last one written survives)",
              tail is not None and "line-059" in tail)
        check("truncating from the front marks the cut with STDOUT_TAIL_TRUNCATION_MARK on the "
              "first surviving line, rather than silently dropping the earliest lines",
              tail is not None and tail.startswith(STDOUT_TAIL_TRUNCATION_MARK))
        check("the byte cap actually dropped lines (fewer than the STDOUT_TAIL_MAX_LINES the "
              "line-count slice alone would have kept) -- proves the truncate-from-the-front path "
              "ran, not just the line-count cap",
              tail is not None and len(tail.split("\n")) < STDOUT_TAIL_MAX_LINES)

        # The withheld line: one matching line becomes [withheld], the rest of the tail rides through.
        secret_room_dir = Path(tmp) / "secret-room"
        secret_exec_dir = secret_room_dir / "artifacts" / "execution_exec-secret-1"
        secret_exec_dir.mkdir(parents=True)
        secret_stdout = secret_exec_dir / ".stdout.log"
        secret_stdout.write_text("plain line one\nAKIA_FAKE_SECRET_TOKEN\nplain line two\n", encoding="utf-8")
        secret_patterns = [re.compile(r"AKIA_FAKE")]
        secret_tail = stdout_tail_for_room(str(secret_room_dir), "exec-secret-1", secret_patterns)
        check("a line matching a secret pattern is replaced with [withheld], never dropping the tail",
              secret_tail is not None and "[withheld]" in secret_tail
              and "plain line one" in secret_tail and "plain line two" in secret_tail
              and "AKIA_FAKE_SECRET_TOKEN" not in secret_tail)
        withheld_all_tail = stdout_tail_for_room(str(secret_room_dir), "exec-secret-1", None)
        check("a missing/unreadable patterns file (None, the fail-closed sentinel) withholds every "
              "line of the tail, matching the deliverables path's own fail-closed posture",
              withheld_all_tail is not None
              and all(ln == "[withheld]" for ln in withheld_all_tail.split("\n")))

        # Absence: no captured stdout yet for this execution.
        check("stdout_tail_for_room is absent (None), never a fabricated empty string, when the "
              "execution has no captured .stdout.log yet",
              stdout_tail_for_room(str(room_dir), "exec-never-started", []) is None)

        # -- #1723: a multi-byte character straddling the max_bytes truncation cut yields no U+FFFD.
        # Five short filler lines, then one long line of 2-byte UTF-8 characters (broken into
        # sub-200-char runs by spaces, so the #1723 blob-elision arm below never touches it) with NO
        # trailing newline (the log is still being written) -- the shape of the #1723 bug report: the
        # truncation cut lands inside the newest, still-open line, with no newline anywhere ahead of
        # the cut to recover a boundary from, so the pre-fix version left a genuine leading U+FFFD.
        straddle_room_dir = Path(tmp) / "straddle-room"
        straddle_exec_dir = straddle_room_dir / "artifacts" / "execution_exec-straddle-1"
        straddle_exec_dir.mkdir(parents=True)
        straddle_long_line = ("é" * 50 + " ") * 60
        (straddle_exec_dir / ".stdout.log").write_text("short\n" * 5 + straddle_long_line, encoding="utf-8")
        straddle_tail = stdout_tail_for_room(str(straddle_room_dir), "exec-straddle-1", [], max_bytes=106)
        check("#1723: a multi-byte character straddling the byte-cap cut never yields a U+FFFD",
              straddle_tail is not None and "�" not in straddle_tail)
        check("rev1738 F3: with no line boundary anywhere ahead of the cut, the newest (already "
              "capped) line is kept, trimmed to fit -- not dropped to just the truncation mark",
              straddle_tail is not None and straddle_tail.startswith(STDOUT_TAIL_TRUNCATION_MARK)
              and straddle_tail != STDOUT_TAIL_TRUNCATION_MARK
              and len(straddle_tail.encode("utf-8")) <= 106)

        # -- #1723: a stream-json assistant/tool_use/tool_result triple renders to three prose lines
        # with no braces, mirroring Baton.Cli's WorkerStreamLineRenderer output shapes. --
        prose_room_dir = Path(tmp) / "prose-room"
        prose_exec_dir = prose_room_dir / "artifacts" / "execution_exec-prose-1"
        prose_exec_dir.mkdir(parents=True)
        prose_lines_in = [
            json.dumps({"type": "assistant", "message": {"content": [
                {"type": "text", "text": "Reading the issue now."}]}}),
            json.dumps({"type": "assistant", "message": {"content": [
                {"type": "tool_use", "name": "Bash", "input": {"command": "ls -la"}}]}}),
            json.dumps({"type": "user", "message": {"content": [
                {"type": "tool_result", "tool_use_id": "x", "is_error": False,
                 "content": "total 0\ndrwxr-xr-x"}]}}),
        ]
        (prose_exec_dir / ".stdout.log").write_text("\n".join(prose_lines_in) + "\n", encoding="utf-8")
        prose_tail = stdout_tail_for_room(str(prose_room_dir), "exec-prose-1", [])
        prose_out_lines = prose_tail.split("\n") if prose_tail else []
        check("#1723: an assistant/tool_use/tool_result triple renders to exactly three prose lines",
              len(prose_out_lines) == 3)
        check("#1723: none of the rendered prose lines carry a raw JSON brace",
              prose_tail is not None and "{" not in prose_tail and "}" not in prose_tail)
        check("#1723: assistant text renders as the plain text itself",
              prose_out_lines[:1] == ["Reading the issue now."])
        check("#1723: tool_use renders as the tool name plus a one-line input summary",
              prose_out_lines[1:2] == ["[tool: Bash(command=ls -la)]"])
        check("#1723: tool_result renders its first line",
              prose_out_lines[2:3] == ["[tool_result: total 0]"])

        # -- #1723: a 5 KB base64-shaped token (no whitespace) is elided, never shipped whole. --
        blob_room_dir = Path(tmp) / "blob-room"
        blob_exec_dir = blob_room_dir / "artifacts" / "execution_exec-blob-1"
        blob_exec_dir.mkdir(parents=True)
        blob_token = "Q" * 5000
        (blob_exec_dir / ".stdout.log").write_text(f"before\n{blob_token}\nafter\n", encoding="utf-8")
        blob_tail = stdout_tail_for_room(str(blob_room_dir), "exec-blob-1", [])
        check("#1723: a long whitespace-free token is elided rather than shipped whole",
              blob_tail is not None and blob_token not in blob_tail)
        check("#1723: the elision marker names the elided byte count",
              blob_tail is not None and "[5000 bytes elided]" in blob_tail)

        # -- #1723: a plain (non-JSON) line passes through unchanged. --
        plain_room_dir = Path(tmp) / "plain-room"
        plain_exec_dir = plain_room_dir / "artifacts" / "execution_exec-plain-1"
        plain_exec_dir.mkdir(parents=True)
        plain_line = "just a normal stdout line, nothing special here"
        (plain_exec_dir / ".stdout.log").write_text(plain_line + "\n", encoding="utf-8")
        plain_tail = stdout_tail_for_room(str(plain_room_dir), "exec-plain-1", [])
        check("#1723: a plain non-JSON line passes through unchanged", plain_tail == plain_line)

        # -- review rev1738 F1: an agy step_update line renders to prose, not an absent tail. --
        agy_room_dir = Path(tmp) / "agy-room"
        agy_exec_dir = agy_room_dir / "artifacts" / "execution_exec-agy-1"
        agy_exec_dir.mkdir(parents=True)
        agy_line = json.dumps({"event": "step_update",
                                "step_update": {"state": "DONE", "step_type": "tool"}})
        (agy_exec_dir / ".stdout.log").write_text(agy_line + "\n", encoding="utf-8")
        agy_tail = stdout_tail_for_room(str(agy_room_dir), "exec-agy-1", [])
        check("rev1738 F1: an agy DONE/tool step_update line renders to a prose line, not None",
              agy_tail == "[tool: tool — done]")

        # -- review rev1738 F1/F4: an unknown-but-valid JSON dict (neither claude's `type` nor agy's
        # `event` key) passes through raw rather than being dropped. --
        unknown_room_dir = Path(tmp) / "unknown-room"
        unknown_exec_dir = unknown_room_dir / "artifacts" / "execution_exec-unknown-1"
        unknown_exec_dir.mkdir(parents=True)
        unknown_line = json.dumps({"kind": "some_future_vendor_shape", "value": 1})
        (unknown_exec_dir / ".stdout.log").write_text(unknown_line + "\n", encoding="utf-8")
        unknown_tail = stdout_tail_for_room(str(unknown_room_dir), "exec-unknown-1", [])
        check("rev1738 F1/F4: a valid JSON dict this renderer has no `type`/`event` arm for passes "
              "through raw rather than being dropped from the tail",
              unknown_tail == unknown_line)

        # -- review rev1738 F3: a long plain-text open line at the newest position still yields a
        # tail containing its (capped) head, not just the truncation mark. --
        long_plain_room_dir = Path(tmp) / "long-plain-room"
        long_plain_exec_dir = long_plain_room_dir / "artifacts" / "execution_exec-long-plain-1"
        long_plain_exec_dir.mkdir(parents=True)
        long_plain_line = "x " * 3000  # ~6 KB of ordinary spaced text, no trailing newline (still open).
        (long_plain_exec_dir / ".stdout.log").write_text(long_plain_line, encoding="utf-8")
        long_plain_tail = stdout_tail_for_room(str(long_plain_room_dir), "exec-long-plain-1", [])
        check("rev1738 F3: a 6 KB plain-text open line is capped rather than dropped whole, so the "
              "tail still carries its (capped) head instead of collapsing to just the truncation mark",
              long_plain_tail is not None and long_plain_tail != STDOUT_TAIL_TRUNCATION_MARK
              and long_plain_tail.startswith("x x x"))

    # -- #1793: doing_now_for_room -- the checked-in fixture the C# port's own test reads (see
    # StdoutTailRendererTests.ComputeDoingNow_ReadsCheckedInFixture_MatchingPusherPySelftest), plus
    # the text-only and no-description-fallback arms. --
    with tempfile.TemporaryDirectory() as tmp:
        repo_root = Path(__file__).resolve().parent.parent.parent
        fixture_path = repo_root / "tests" / "fixtures" / "doing-now-sample.stdout.log"
        fixture_room_dir = Path(tmp) / "fixture-room"
        fixture_exec_dir = fixture_room_dir / "artifacts" / "execution_exec-fixture-1"
        fixture_exec_dir.mkdir(parents=True)
        (fixture_exec_dir / ".stdout.log").write_text(
            fixture_path.read_text(encoding="utf-8"), encoding="utf-8")
        fixture_doing_now = doing_now_for_room(str(fixture_room_dir), "exec-fixture-1", [])
        check("#1793: doing_now_for_room reads the checked-in fixture identically to the C# port "
              "(StdoutTailRenderer.ComputeDoingNow) -- a tool_use's own description field, since "
              "it's the LAST content block on the LAST assistant line",
              fixture_doing_now == "Running gates a second time before committing")

        text_room_dir = Path(tmp) / "doing-now-text-room"
        text_exec_dir = text_room_dir / "artifacts" / "execution_exec-text-1"
        text_exec_dir.mkdir(parents=True)
        (text_exec_dir / ".stdout.log").write_text(
            json.dumps({"type": "assistant", "message": {"content": [
                {"type": "text", "text": "Reviewing the diff before I commit."}]}}) + "\n",
            encoding="utf-8")
        check("#1793: doing_now_for_room uses the last assistant TEXT block when it's the last block",
              doing_now_for_room(str(text_room_dir), "exec-text-1", []) ==
              "Reviewing the diff before I commit.")

        fallback_room_dir = Path(tmp) / "doing-now-fallback-room"
        fallback_exec_dir = fallback_room_dir / "artifacts" / "execution_exec-fallback-1"
        fallback_exec_dir.mkdir(parents=True)
        (fallback_exec_dir / ".stdout.log").write_text(
            json.dumps({"type": "assistant", "message": {"content": [
                {"type": "tool_use", "name": "Bash", "input": {"command": "git status"}}]}}) + "\n",
            encoding="utf-8")
        check("#1793: doing_now_for_room falls back to 'name first-argument-value' when the "
              "tool_use input carries no description field",
              doing_now_for_room(str(fallback_room_dir), "exec-fallback-1", []) == "Bash git status")

        no_assistant_room_dir = Path(tmp) / "doing-now-no-assistant-room"
        no_assistant_exec_dir = no_assistant_room_dir / "artifacts" / "execution_exec-no-assistant-1"
        no_assistant_exec_dir.mkdir(parents=True)
        (no_assistant_exec_dir / ".stdout.log").write_text(
            json.dumps({"type": "result", "subtype": "success", "is_error": False, "result": "ok"}) + "\n",
            encoding="utf-8")
        check("#1793: doing_now_for_room is absent (None) when the tail carries no assistant line",
              doing_now_for_room(str(no_assistant_room_dir), "exec-no-assistant-1", []) is None)

        # #1818: doingNow runs through the SAME secret gate stdoutTail does.
        secret_doing_now_room_dir = Path(tmp) / "doing-now-secret-room"
        secret_doing_now_exec_dir = secret_doing_now_room_dir / "artifacts" / "execution_exec-doing-now-secret-1"
        secret_doing_now_exec_dir.mkdir(parents=True)
        (secret_doing_now_exec_dir / ".stdout.log").write_text(
            json.dumps({"type": "assistant", "message": {"content": [
                {"type": "text", "text": "AKIA_FAKE_SECRET_TOKEN leaked here"}]}}) + "\n",
            encoding="utf-8")
        check("#1818: doing_now_for_room withholds a line matching a secret pattern, exactly like "
              "stdout_tail_for_room does",
              doing_now_for_room(str(secret_doing_now_room_dir), "exec-doing-now-secret-1",
                                  [re.compile(r"AKIA_FAKE")]) == "[withheld]")
        check("#1818: doing_now_for_room withholds unconditionally when patterns is None (the "
              "fail-closed sentinel), matching stdout_tail_for_room's own posture",
              doing_now_for_room(str(text_room_dir), "exec-text-1", None) == "[withheld]")

    # Absence on a terminal room: attach_live_telemetry never runs live_telemetry_for_room (and so
    # never the stdout tail) for anything other than a Running room -- covered structurally by the
    # same gate "attach_live_telemetry never adds a `live` key" above proves for the whole `live`
    # section, restated here for the field #1710 adds specifically.
    with tempfile.TemporaryDirectory() as tmp:
        terminal_room_dir = Path(tmp) / "terminal-room"
        terminal_exec_dir = terminal_room_dir / "artifacts" / "execution_exec-done-1"
        terminal_exec_dir.mkdir(parents=True)
        (terminal_exec_dir / ".stdout.log").write_text("last line before it finished\n", encoding="utf-8")
        terminal_room = {"path": str(terminal_room_dir), "state": "Succeeded",
                          "steps": [{"id": "s1", "state": "Succeeded", "execution": "exec-done-1"}]}
        terminal_list = [terminal_room]
        attach_live_telemetry(terminal_list, {}, [])
        check("a terminal room never gets a `live` section, so it never gets a stdoutTail either",
              "live" not in terminal_room)

    # Payload growth bound: N Running rooms each carrying a full-cap stdout tail add at most
    # N * STDOUT_TAIL_MAX_BYTES to the pushed payload -- #1710's own bound, summed off the real
    # `live.stdoutTail` values `attach_live_telemetry` produced rather than asserted in the abstract.
    with tempfile.TemporaryDirectory() as tmp:
        running_rooms = []
        for i in range(3):
            room_dir = Path(tmp) / f"bound-room-{i}"
            exec_dir = room_dir / "artifacts" / f"execution_exec-bound-{i}"
            exec_dir.mkdir(parents=True)
            (exec_dir / ".stdout.log").write_text(("z" * 200 + "\n") * 100, encoding="utf-8")
            running_rooms.append({"path": str(room_dir), "state": "Running", "name": f"bound-{i}",
                                   "steps": [{"id": "s1", "state": "Running", "execution": f"exec-bound-{i}"}]})
        bare_rooms = [{"path": r["path"], "state": r["state"], "name": r["name"], "steps": r["steps"]}
                      for r in running_rooms]
        attach_live_telemetry(running_rooms, {}, [])
        tail_bytes_total = sum(
            len(r["live"]["stdoutTail"].encode("utf-8"))
            for r in running_rooms if isinstance(r.get("live"), dict) and "stdoutTail" in r["live"])
        bound = len(running_rooms) * STDOUT_TAIL_MAX_BYTES
        print(f"pusher.py selftest: #1710 stdout-tail bytes across {len(running_rooms)} Running "
              f"rooms = {tail_bytes_total} bytes (bound {bound} bytes)")
        check("#1710: pushed-payload growth from Running rooms' stdout tails stays within "
              "Running rooms x STDOUT_TAIL_MAX_BYTES -- the per-room cap `stdout_tail_for_room` "
              "already enforces, summed across the fleet",
              tail_bytes_total <= bound)

    # Worst-day write count is unchanged with the tail present: SNAPSHOT_KV_WRITE_COST is a FLAT
    # per-push charge (spec/baton.md §6) with no notion of payload bytes, so a push carrying Running
    # rooms' stdout tails costs the identical ledger charge as one that does not -- the tail costs
    # bytes and churn, never an extra write.
    small_ledger_state: dict = {}
    small_body = json.dumps(build_wrapped(bare_rooms, [], {}, 0))
    record_budget_write(small_ledger_state, 0.0, "snapshot", SNAPSHOT_KV_WRITE_COST)
    small_used = budget_used(load_budget_ledger(small_ledger_state, 0.0))

    tail_ledger_state: dict = {}
    tail_body = json.dumps(build_wrapped(running_rooms, [], {}, 0))
    record_budget_write(tail_ledger_state, 0.0, "snapshot", SNAPSHOT_KV_WRITE_COST)
    tail_used = budget_used(load_budget_ledger(tail_ledger_state, 0.0))

    check("#1710: a push with Running rooms' stdout tails attached (a bigger payload) still charges "
          "the identical flat SNAPSHOT_KV_WRITE_COST a push without them would charge",
          len(tail_body) > len(small_body) and small_used == tail_used == SNAPSHOT_KV_WRITE_COST)

    # -- #1710: the tail rides the hash as-is (never quantized) -- a Running room whose stdoutTail is
    # the only thing that changed still flips snapshot_hash on the very next cycle, the same as any
    # other structural change. Proven end-to-end through the real quantize_live_for_hash/
    # build_wrapped/snapshot_hash path, not just by reading _quantize_live_value's field list. --
    tail_room_a = {"path": "/r/tail", "state": "Running",
                   "live": {"toolCalls": 1, "lastActivityAt": _quantized_activity_iso(1000.0),
                             "stdoutTail": "line one\nline two"}}
    tail_room_b = {"path": "/r/tail", "state": "Running",
                   "live": {"toolCalls": 1, "lastActivityAt": _quantized_activity_iso(1000.0),
                             "stdoutTail": "line one\nline two\nline three"}}
    hash_tail_a = snapshot_hash(build_wrapped(quantize_live_for_hash([tail_room_a]), [], {}, 0))
    hash_tail_b = snapshot_hash(build_wrapped(quantize_live_for_hash([tail_room_b]), [], {}, 0))
    check("#1710: quantize_live_for_hash never touches stdoutTail -- a room whose tail is the ONLY "
          "thing that changed (every quantized field identical) still changes the pushed hash",
          hash_tail_a != hash_tail_b)
    check("#1710: quantize_live_for_hash rides stdoutTail through byte-for-byte, unlike toolCalls/"
          "lastActivityAt above it",
          quantize_live_for_hash([tail_room_a])[0]["live"]["stdoutTail"] == "line one\nline two")

    with tempfile.TemporaryDirectory() as tmp:
        sp = Path(tmp) / "push-state.json"
        push_fail_state = {LAST_PUSH_TS_KEY: 0.0, SNAPSHOT_HASH_KEY: "old-hash"}

        def _push_boom(_body):
            raise RuntimeError("push failed (e.g. a 413 from the 1 MB cap)")

        try:
            push_snapshot_and_record(_push_boom, "{}", push_fail_state, sp, "new-hash", now_ts=100.0)
        except RuntimeError:
            pass
        check("finding 2: a FAILED push leaves LAST_PUSH_TS_KEY frozen, so pending_push_age_s keeps "
              "GROWING cycle over cycle instead of resetting -- this is what lets the heartbeat ping "
              "carry a growing pending age while pushes keep failing",
              pending_push_age_s(push_fail_state, "new-hash", 100.0) == 100.0
              and pending_push_age_s(push_fail_state, "new-hash", 500.0) == 500.0)

    # -- #1613 item 1: live telemetry is attached AFTER stale-filtering, never before -- it plays no
    # part in the staleness decision at all, sidestepping the exhaustedUntil-shaped landmine by
    # construction (ordering) rather than by adding it to newest_timestamp's skip set.
    with tempfile.TemporaryDirectory() as tmp:
        room_dir = Path(tmp) / "old-but-live-room"
        exec_dir = room_dir / "artifacts" / "execution_exec-old-1"
        exec_dir.mkdir(parents=True)
        (exec_dir / ".stdout.log").write_text("{}\n", encoding="utf-8")
        old_step_iso = datetime.fromtimestamp(
            datetime.now(timezone.utc).timestamp() - 10 * 86400, tz=timezone.utc).isoformat()
        stale_room_body = json.dumps([{
            "name": "old-but-live-room", "path": str(room_dir), "state": "Running",
            "steps": [{"id": "s1", "state": "Running", "execution": "exec-old-1", "timestamp": old_step_iso}],
        }])
        filtered, dropped = drop_stale_rooms(stale_room_body, max_age_days=3)
        check("a room with only an old step timestamp still drops as stale BEFORE live telemetry "
              "is ever attached -- a fresh .stdout.log mtime never rescues it from the filter",
              dropped == 1 and json.loads(filtered) == [])

    # -- #1505: stale-room drop becomes a visible count, never a silent disappearance (landmine #43) --
    now_iso = datetime.now(timezone.utc).isoformat()
    old_iso = (datetime.now(timezone.utc).timestamp() - 10 * 86400)
    old_iso = datetime.fromtimestamp(old_iso, tz=timezone.utc).isoformat()
    stale_body = json.dumps([
        {"name": "fresh", "state": "Running", "steps": [{"id": "s1", "state": "Running", "timestamp": now_iso}]},
        {"name": "zombie", "state": "Running", "steps": [{"id": "s1", "state": "Running", "timestamp": old_iso}]},
        {"name": "no-timestamp", "state": "Failed"},
    ])
    filtered_body, dropped_count = drop_stale_rooms(stale_body, max_age_days=3)
    check("drop_stale_rooms reports a non-zero dropped count for an aged zombie room",
          dropped_count == 1)
    filtered_rooms = json.loads(filtered_body)
    check("the dropped count matches what's actually missing from the filtered list",
          len(filtered_rooms) == 2 and not any(r["name"] == "zombie" for r in filtered_rooms))
    check("a room with no parseable timestamp is kept, not silently dropped",
          any(r["name"] == "no-timestamp" for r in filtered_rooms))

    fresh_body, fresh_dropped = drop_stale_rooms(
        json.dumps([{"name": "fresh", "state": "Running",
                     "steps": [{"id": "s1", "state": "Running", "timestamp": now_iso}]}]),
        max_age_days=3)
    check("(control) nothing dropped when every room is recent", fresh_dropped == 0)

    # -- #1551: an abandoned parked room's real (old) step timestamp must still win over its
    # FUTURE exhaustedUntil reset instant, or a room nobody is watching never ages out --
    future_iso = (datetime.now(timezone.utc).timestamp() + 30 * 86400)
    future_iso = datetime.fromtimestamp(future_iso, tz=timezone.utc).isoformat()
    check("(control) exhaustedUntil alone reads as the room's newest timestamp when included",
          newest_timestamp({"steps": [{"exhaustedUntil": future_iso}]}, _skip_keys=frozenset()) == future_iso)
    parked_body = json.dumps([
        {"name": "abandoned-park", "state": "Running",
         "steps": [{"id": "s1", "state": "Failed", "timestamp": old_iso, "exhaustedUntil": future_iso}]},
    ])
    parked_filtered, parked_dropped = drop_stale_rooms(parked_body, max_age_days=3)
    check("an abandoned parked room drops as stale off its real (old) step timestamp, "
          "not its future exhaustedUntil reset instant",
          parked_dropped == 1 and json.loads(parked_filtered) == [])

    # -- #1538: single-instance guard --
    with tempfile.TemporaryDirectory() as tmp:
        tmp_dir = Path(tmp)
        lock_file = tmp_dir / "pusher.lock"

        # 1. Clean acquisition and release
        check("acquire_lock succeeds on fresh lock file",
              acquire_lock(lock_file, pid=11111) is True)
        check("lock file holds the claimed PID",
              read_lock_pid(lock_file) == 11111)
        # Release with wrong PID must not delete lock file
        release_lock(lock_file, pid=22222)
        check("release_lock ignores non-matching PID",
              lock_file.is_file() and read_lock_pid(lock_file) == 11111)
        # Release with matching PID deletes lock file
        release_lock(lock_file, pid=11111)
        check("release_lock cleans up when PID matches",
              not lock_file.exists())

        # 2. Reclaim stale lock from a fake dead PID
        lock_file.write_text("99999999\n", encoding="utf-8")
        check("dead PID is recognized as not alive",
              is_pid_alive(99999999) is False)
        check("acquire_lock reclaims lock from dead PID",
              acquire_lock(lock_file, pid=33333) is True)
        check("reclaimed lock now holds the new PID",
              read_lock_pid(lock_file) == 33333)
        release_lock(lock_file, pid=33333)

        # 3. Reclaim from corrupted lock file
        lock_file.write_text("not-a-pid\n", encoding="utf-8")
        check("acquire_lock reclaims unreadable lock file",
              acquire_lock(lock_file, pid=44444) is True)
        check("reclaimed lock holds new PID after corruption",
              read_lock_pid(lock_file) == 44444)
        release_lock(lock_file, pid=44444)

        # 4. Replace running pusher instance
        proc = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(15) # pusher_test_subproc"])
        try:
            lock_file.write_text(f"{proc.pid}\n", encoding="utf-8")
            check("child process is alive", is_pid_alive(proc.pid) is True)
            check("child process command line contains pusher", "pusher" in get_process_cmdline(proc.pid).lower())
            check("acquire_lock terminates and replaces running pusher",
                  acquire_lock(lock_file, pid=55555) is True)
            for _ in range(30):
                if proc.poll() is not None:
                    break
                time.sleep(0.05)
            check("stale pusher process was terminated", proc.poll() is not None)
            check("lock now belongs to new PID", read_lock_pid(lock_file) == 55555)
        finally:
            if proc.poll() is None:
                proc.terminate()
            release_lock(lock_file, pid=55555)

        # 5. Non-pusher process is NOT killed when lock is reclaimed
        proc_unrelated = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(15) # unrelated_task"])
        try:
            lock_file.write_text(f"{proc_unrelated.pid}\n", encoding="utf-8")
            check("acquire_lock reclaims lock from non-pusher without killing it",
                  acquire_lock(lock_file, pid=66666) is True)
            check("non-pusher process remains alive", proc_unrelated.poll() is None)
        finally:
            if proc_unrelated.poll() is None:
                proc_unrelated.terminate()
            release_lock(lock_file, pid=66666)

    # -- #1538: coalescing floor (KV daily cap protection) --
    check("should_coalesce_push is False when no prior push recorded",
          should_coalesce_push({}, 1000.0, 90) is False)
    check("should_coalesce_push is True within min_interval window",
          should_coalesce_push({LAST_PUSH_TS_KEY: 1000.0}, 1050.0, 90) is True)
    check("should_coalesce_push is False once min_interval has elapsed",
          should_coalesce_push({LAST_PUSH_TS_KEY: 1000.0}, 1090.0, 90) is False)
    check("should_coalesce_push is False past min_interval window",
          should_coalesce_push({LAST_PUSH_TS_KEY: 1000.0}, 1150.0, 90) is False)

    with tempfile.TemporaryDirectory() as tmp:
        sp = Path(tmp) / "push-state.json"
        state = {}

        # Cycle 1: initial push at t=0
        h1 = "hash_1"
        check("cycle 1: should push new content", should_push_snapshot(state, h1) is True)
        check("cycle 1: should not coalesce first push", should_coalesce_push(state, 0.0, 90) is False)
        push_snapshot_and_record(lambda _b: None, "{}", state, sp, h1, now_ts=0.0)
        check("cycle 1: state records snapshot hash", state.get(SNAPSHOT_HASH_KEY) == h1)
        check("cycle 1: state records push timestamp", state.get(LAST_PUSH_TS_KEY) == 0.0)

        # Cycle 2: unchanged content at t=25
        check("cycle 2: unchanged content is skipped by change-gate",
              should_push_snapshot(state, h1) is False)

        # Cycle 3: changed content at t=50 (within 90s floor)
        h2 = "hash_2"
        check("cycle 3: change-gate detects new hash", should_push_snapshot(state, h2) is True)
        check("cycle 3: floor coalesces push (50s < 90s)", should_coalesce_push(state, 50.0, 90) is True)
        check("cycle 3: persisted hash remains unchanged across coalesced cycle",
              state.get(SNAPSHOT_HASH_KEY) == h1 and state.get(LAST_PUSH_TS_KEY) == 0.0)

        # Cycle 4: changed content still waiting at t=75 (within 90s floor)
        check("cycle 4: change-gate still wants to push", should_push_snapshot(state, h2) is True)
        check("cycle 4: floor still coalesces (75s < 90s)", should_coalesce_push(state, 75.0, 90) is True)

        # Cycle 5: floor expires at t=95 (>= 90s)
        check("cycle 5: change-gate still wants to push", should_push_snapshot(state, h2) is True)
        check("cycle 5: floor allows push (95s >= 90s)", should_coalesce_push(state, 95.0, 90) is False)
        push_snapshot_and_record(lambda _b: None, "{}", state, sp, h2, now_ts=95.0)
        check("cycle 5: state updated with new hash", state.get(SNAPSHOT_HASH_KEY) == h2)
        check("cycle 5: state updated with new push timestamp", state.get(LAST_PUSH_TS_KEY) == 95.0)

    # -- #1656: hot-set capping (split_hot_and_archive) --
    def _room(path, state, ts):
        return {"path": path, "state": state, "steps": [{"id": "s1", "state": state, "timestamp": ts}]}

    running_room = _room("/r/running", "Running", "2026-09-01T00:00:00Z")
    terminal_rooms = [_room(f"/r/term-{i}", "Succeeded" if i % 2 else "Failed",
                             f"2026-09-01T00:{i:02d}:00Z") for i in range(50)]
    mixed = [running_room, *terminal_rooms]
    hot, archive, total = split_hot_and_archive(mixed)
    check("hot set keeps every non-terminal room", running_room in hot)
    check("hot set caps terminal rooms at HOT_TERMINAL_CAP", sum(1 for r in hot if r is not running_room) == HOT_TERMINAL_CAP)
    check("terminal_total counts every terminal room, not just the hot slice", total == 50)
    check("archive carries the FULL terminal population, not just the tail beyond the cap", len(archive) == 50)
    check("archive is sorted newest-first (same measure as drop_stale_rooms' newest_timestamp)",
          archive[0]["path"] == "/r/term-49" and archive[-1]["path"] == "/r/term-0")
    check("the hot set's terminal slice is the SAME newest rooms archive page 0 would return",
          {r["path"] for r in hot if r is not running_room} == {r["path"] for r in archive[:HOT_TERMINAL_CAP]})

    few_terminal = [running_room, terminal_rooms[0], terminal_rooms[1]]
    hot2, archive2, total2 = split_hot_and_archive(few_terminal)
    check("a fleet with fewer terminal rooms than the cap keeps all of them hot",
          len(hot2) == 3 and total2 == 2)

    malformed = [running_room, {"path": "/r/no-state"}, "not-a-dict"]
    hot3, archive3, total3 = split_hot_and_archive(malformed)
    check("a room missing 'state' degrades to non-terminal (kept, never silently dropped)",
          any(r.get("path") == "/r/no-state" for r in hot3 if isinstance(r, dict)))
    check("a non-dict list entry degrades to non-terminal too, never raises", "not-a-dict" in hot3)

    empty_hot, empty_archive, empty_total = split_hot_and_archive([])
    check("an empty room list yields an empty hot set, empty archive, zero total",
          empty_hot == [] and empty_archive == [] and empty_total == 0)

    wrapped_with_archive = build_wrapped(hot, [], {}, 0, terminal_total=total, terminal_archive=archive)
    check("build_wrapped carries terminal_total/terminal_archive through to the pushed body",
          wrapped_with_archive["terminal_total"] == 50 and len(wrapped_with_archive["terminal_archive"]) == 50)
    check("build_wrapped defaults terminal_total/terminal_archive for callers that don't pass them "
          "(every pre-#1656 call site keeps working unchanged)",
          build_wrapped([], [], {}, 0) == {"rooms": [], "underhood": [], "timelines": {},
                                            "stale_hidden_count": 0, "terminal_total": 0,
                                            "terminal_archive": []})

    # #1656 F2 (2026-09-02 review): worker_displayed_heartbeat_at, the hand-copied Python mirror of
    # worker.js's maxIsoOrNull heartbeat merge, is deleted -- the real function now has executable
    # coverage in tools/fleet-glass/worker.selftest.mjs (`node tools/fleet-glass/worker.selftest.mjs`
    # / `pixi run fleet-glass-worker-selftest`), which discriminates against the actual worker.core.mjs
    # code path instead of a copy that could drift from it silently.

    # -- #1669: Conductor room deliverables and upsert identity --
    with tempfile.TemporaryDirectory() as td:
        c_root = Path(td)
        c_room = c_root / "conductor"
        c_art = c_room / "artifacts" / "conductor"
        c_art.mkdir(parents=True)
        c_src = Path(td) / "original-notes.md"
        c_src.write_bytes(b"# Plan Title\nSome content here")

        dest_file = c_art / "original-notes.md"
        dest_file.write_bytes(b"# Plan Title\nSome content here")

        manifest_file = c_art / "manifest.jsonl"
        manifest_entry = {
            "title": "Plan Title",
            "source_path": str(c_src),
            "delivered_at": "2026-09-02T12:00:00Z",
            "sha256": sha256_hex(b"# Plan Title\nSome content here"),
            "artifact_file": "original-notes.md",
        }
        manifest_file.write_text(json.dumps(manifest_entry) + "\n", encoding="utf-8")

        # 1. Gather fresh conductor deliverable
        c_items = gather_deliverables(c_root, {}, [])
        check("gather_deliverables gathers conductor deliverable from manifest", len(c_items) == 1)
        if c_items:
            c_item = c_items[0]
            check("conductor deliverable has kind='conductor'", c_item.get("kind") == "conductor")
            check("conductor deliverable id is derived from source_path",
                  c_item.get("id") == f"{str(c_room)}::conductor::{str(c_src)}")
            check("conductor deliverable carries title", c_item.get("title") == "Plan Title")
            check("conductor deliverable carries content", c_item.get("content") == "# Plan Title\nSome content here")
            check("conductor deliverable is not withheld without secret match", c_item.get("withheld") is False)

        # 2. Dedupe against push state
        c_state = mark_pushed({}, c_items)
        c_items_deduped = gather_deliverables(c_root, c_state, [])
        check("gather_deliverables skips already-pushed conductor deliverable with unchanged content",
              len(c_items_deduped) == 0)

        # 3. Re-delivery with updated content (upsert)
        dest_file.write_bytes(b"# Plan Title\nUpdated content")
        manifest_entry2 = {
            "title": "Plan Title v2",
            "source_path": str(c_src),
            "delivered_at": "2026-09-02T12:30:00Z",
            "sha256": sha256_hex(b"# Plan Title\nUpdated content"),
            "artifact_file": "original-notes.md",
        }
        manifest_file.write_text(json.dumps(manifest_entry2) + "\n", encoding="utf-8")

        c_items_updated = gather_deliverables(c_root, c_state, [])
        check("gather_deliverables picks up updated conductor deliverable", len(c_items_updated) == 1)
        if c_items_updated:
            c_up = c_items_updated[0]
            check("re-delivered item has identical id for upsert",
                  c_up.get("id") == f"{str(c_room)}::conductor::{str(c_src)}")
            check("re-delivered item has updated content hash",
                  c_up.get("content_hash") == sha256_hex(b"# Plan Title\nUpdated content"))

        # 4. Secret gate withholding on conductor deliverable
        secret_pats = [re.compile(r"sk-[A-Za-z0-9]{10,}")]
        dest_file.write_text("# Leaked\nsk-secretkey123456789", encoding="utf-8")
        c_items_leaked = gather_deliverables(c_root, {}, secret_pats)
        check("conductor deliverable with secret is withheld",
              len(c_items_leaked) == 1 and c_items_leaked[0].get("withheld") is True)

    # F1 (2026-09-02 review): two sources sharing a basename must not collide on one on-disk file --
    # artifact_file is read from the manifest line, never re-derived from the basename, so two
    # distinct hashed filenames stay two distinct files with two distinct byte payloads.
    with tempfile.TemporaryDirectory() as td:
        c_root = Path(td)
        c_room = c_root / "conductor"
        c_art = c_room / "artifacts" / "conductor"
        c_art.mkdir(parents=True)

        src_a = Path(td) / "projA" / "notes.md"
        src_a.parent.mkdir(parents=True)
        src_a.write_bytes(b"# A\nProject A content")

        src_b = Path(td) / "projB" / "notes.md"
        src_b.parent.mkdir(parents=True)
        src_b.write_bytes(b"# B\nProject B content")

        dest_a = c_art / "aaaaaaaa-notes.md"
        dest_a.write_bytes(b"# A\nProject A content")
        dest_b = c_art / "bbbbbbbb-notes.md"
        dest_b.write_bytes(b"# B\nProject B content")

        manifest_file = c_art / "manifest.jsonl"
        entries = [
            {
                "title": "A",
                "source_path": str(src_a),
                "delivered_at": "2026-09-02T12:00:00Z",
                "sha256": sha256_hex(b"# A\nProject A content"),
                "artifact_file": "aaaaaaaa-notes.md",
            },
            {
                "title": "B",
                "source_path": str(src_b),
                "delivered_at": "2026-09-02T12:00:01Z",
                "sha256": sha256_hex(b"# B\nProject B content"),
                "artifact_file": "bbbbbbbb-notes.md",
            },
        ]
        manifest_file.write_text("\n".join(json.dumps(e) for e in entries) + "\n", encoding="utf-8")

        same_basename_items = gather_deliverables(c_root, {}, [])
        check("same-basename sources produce two distinct conductor deliverables",
              len(same_basename_items) == 2)
        by_source = {i.get("source_path"): i for i in same_basename_items}
        check("same-basename source A keeps its own bytes",
              by_source.get(str(src_a), {}).get("content") == "# A\nProject A content")
        check("same-basename source B keeps its own bytes",
              by_source.get(str(src_b), {}).get("content") == "# B\nProject B content")
        check("same-basename sources use distinct artifact paths",
              by_source.get(str(src_a), {}).get("artifact") != by_source.get(str(src_b), {}).get("artifact"))

    # -- #1673: Conductor manifest UTF-8 BOM tolerance, corrupt-line logging, and cross-language fixture --
    with tempfile.TemporaryDirectory() as td:
        c_root = Path(td)
        c_room = c_root / "conductor"
        c_art = c_room / "artifacts" / "conductor"
        c_art.mkdir(parents=True)

        c_src_bom = Path(td) / "bom-notes.md"
        c_src_bom.write_bytes(b"# BOM Title\nContent with BOM")
        dest_bom = c_art / "11111111-bom-notes.md"
        dest_bom.write_bytes(b"# BOM Title\nContent with BOM")

        manifest_file = c_art / "manifest.jsonl"
        manifest_entry_bom = {
            "title": "BOM Title",
            "source_path": str(c_src_bom),
            "delivered_at": "2026-09-02T12:00:00Z",
            "sha256": sha256_hex(b"# BOM Title\nContent with BOM"),
            "artifact_file": "11111111-bom-notes.md",
        }
        # (a) A manifest whose first line carries a UTF-8 BOM parses and yields the item
        bom_bytes = b"\xef\xbb\xbf" + json.dumps(manifest_entry_bom).encode("utf-8") + b"\n"
        manifest_file.write_bytes(bom_bytes)

        bom_items = gather_deliverables(c_root, {}, [])
        check("conductor manifest carrying UTF-8 BOM parses and yields deliverable (#1673 arm a)",
              len(bom_items) == 1 and bom_items[0].get("title") == "BOM Title")

        # (b) A garbage line is logged and skipped while good lines still yield
        c_src_good = Path(td) / "good-notes.md"
        c_src_good.write_bytes(b"# Good Title\nGood content")
        dest_good = c_art / "22222222-good-notes.md"
        dest_good.write_bytes(b"# Good Title\nGood content")

        manifest_entry_good = {
            "title": "Good Title",
            "source_path": str(c_src_good),
            "delivered_at": "2026-09-02T12:01:00Z",
            "sha256": sha256_hex(b"# Good Title\nGood content"),
            "artifact_file": "22222222-good-notes.md",
        }
        garbage_manifest = (
            "corrupt garbage line that is not json\n"
            + json.dumps(manifest_entry_bom) + "\n"
            + "another { malformed json line\n"
            + json.dumps(manifest_entry_good) + "\n"
        )
        manifest_file.write_text(garbage_manifest, encoding="utf-8")
        garbage_items = gather_deliverables(c_root, {}, [])
        check("garbage manifest line is skipped while good lines yield deliverables (#1673 arm b)",
              len(garbage_items) == 2 and {i.get("title") for i in garbage_items} == {"BOM Title", "Good Title"})

        # (3) Cross-language pin: parse checked-in fixture tests/fixtures/conductor-manifest.jsonl produced by C# path
        fixture_path = HERE.parent.parent / "tests" / "fixtures" / "conductor-manifest.jsonl"
        check("cross-language conductor manifest fixture file exists (#1673)", fixture_path.is_file())
        fixture_raw = fixture_path.read_bytes()
        check("cross-language fixture has no UTF-8 BOM and starts with '{'",
              len(fixture_raw) > 0 and fixture_raw[0] == 0x7B and not fixture_raw.startswith(b"\xef\xbb\xbf"))

        manifest_file.write_bytes(fixture_raw)
        fixture_artifact = c_art / "c44a8b84-fixture-plan.md"
        fixture_artifact.write_bytes(b"# Fixture Plan\nFixture content")

        fixture_items = gather_deliverables(c_root, {}, [])
        check("pusher selftest parses cross-language conductor manifest fixture (#1673)",
              len(fixture_items) == 1 and fixture_items[0].get("title") == "Fixture Plan"
              and fixture_items[0].get("kind") == "conductor")

    # -- #1656 F3 (2026-09-02 review): nonterminal_warn_line threshold behavior, restored alongside
    # the #1669 conductor block above rather than being displaced by it (F2, 2026-09-02 review) --
    check("non_terminal_count at the threshold does not warn", nonterminal_warn_line(HOT_NONTERMINAL_WARN) is None)
    check("non_terminal_count one over the threshold warns, naming the threshold",
          nonterminal_warn_line(HOT_NONTERMINAL_WARN + 1) is not None
          and "HOT_NONTERMINAL_WARN" in nonterminal_warn_line(HOT_NONTERMINAL_WARN + 1))

    conductor_obj = {"path": "/r/conductor", "artifacts_path": "/r/conductor/artifacts/conductor"}
    wrapped_with_conductor = build_wrapped([], [], {}, 0, conductor=conductor_obj)
    check("build_wrapped carries conductor object through to the snapshot",
          wrapped_with_conductor.get("conductor") == conductor_obj)

    # -- #1690 item 1: write budget ledger --
    wrapped_with_pusher = build_wrapped([], [], {}, 0, pusher={"writeBudgetExhaustedUntil": "2026-09-03T00:00:00+00:00"})
    check("build_wrapped carries the pusher block through to the snapshot, absent-safe otherwise",
          wrapped_with_pusher.get("pusher") == {"writeBudgetExhaustedUntil": "2026-09-03T00:00:00+00:00"}
          and "pusher" not in build_wrapped([], [], {}, 0))

    # -- #1391: per-vendor usage runway rides `vendors[]`, absent-safe like conductor/pusher above --
    # #1746 adds two DERIVED window keys (`ratePctPerHour`/`minutesToExhaustion`); this file computes
    # neither and must forward whatever the daemon's projection put there, so the fixture carries them
    # and the assertion is verbatim equality, not a key-by-key subset.
    vendors_obj = [{"adapter": "claude", "harvestedAt": "2026-09-04T18:00:00+00:00",
                     "windows": [{"name": "session", "percentUsed": 8, "rawLine": "Current session: 8% used",
                                  "ratePctPerHour": 4.5, "minutesToExhaustion": 1226.7}],
                     "liveLanes": 1}]
    wrapped_with_vendors = build_wrapped([], [], {}, 0, vendors=vendors_obj)
    check("build_wrapped carries the vendors block through to the snapshot, absent-safe otherwise",
          wrapped_with_vendors.get("vendors") == vendors_obj
          and "vendors" not in build_wrapped([], [], {}, 0))

    check("next_utc_midnight_iso names the NEXT 00:00 UTC, not the current day's",
          next_utc_midnight_iso(datetime(2026, 9, 2, 16, 50, tzinfo=timezone.utc).timestamp())
          == datetime(2026, 9, 3, 0, 0, tzinfo=timezone.utc).isoformat())
    check("seconds_left_in_day is under a day and positive for a mid-day instant",
          0 < seconds_left_in_day(datetime(2026, 9, 2, 12, 0, tzinfo=timezone.utc).timestamp()) < 86400)

    fresh_ledger = load_budget_ledger({}, 1000.0)
    check("a fresh/empty state produces a zeroed ledger for today",
          fresh_ledger == {"date": utc_day_str(1000.0), "snapshot": 0, "deliver": 0, "heartbeat": 0,
                            "exhausted_notice_sent": False})
    ledger_state = {}
    record_budget_write(ledger_state, 1000.0, "snapshot", SNAPSHOT_KV_WRITE_COST)
    record_budget_write(ledger_state, 1001.0, "deliver", DELIVER_BATCH_KV_WRITE_COST)
    record_budget_write(ledger_state, 1002.0, "heartbeat", HEARTBEAT_KV_WRITE_COST)
    same_day_ledger = load_budget_ledger(ledger_state, 1003.0)
    check("record_budget_write accumulates each producer's own counter",
          same_day_ledger["snapshot"] == 1 and same_day_ledger["deliver"] == DELIVER_BATCH_KV_WRITE_COST
          and same_day_ledger["heartbeat"] == 1)
    check("budget_used sums every producer", budget_used(same_day_ledger) == 2 + DELIVER_BATCH_KV_WRITE_COST)
    check("budget_left is the target minus used",
          budget_left(same_day_ledger, target=700) == 700 - (2 + DELIVER_BATCH_KV_WRITE_COST))
    next_day_ts = 1000.0 + 86400.0
    next_day_ledger = load_budget_ledger(ledger_state, next_day_ts)
    check("a UTC-day rollover resets the ledger to zero, never carrying yesterday's counts",
          budget_used(next_day_ledger) == 0 and next_day_ledger["date"] != same_day_ledger["date"])
    check("a corrupt/malformed persisted ledger degrades to zeroed, never crashes",
          budget_used(load_budget_ledger({BUDGET_STATE_KEY: {"date": utc_day_str(5.0), "snapshot": "garbage"}}, 5.0)) == 0)

    # -- F10 (2026-09-02 review): monotonic rollover guard against a clock that jumps backward
    # across midnight (an NTP correction), which must never hand the same real day a second full
    # budget.
    day1 = utc_day_str(1000.0)
    day1_spent = {BUDGET_STATE_KEY: {"date": day1, "snapshot": 250, "deliver": 0, "heartbeat": 0,
                                      "exhausted_notice_sent": False}}
    rolled_forward = load_budget_ledger(day1_spent, 1000.0 + 86400.0)
    check("a genuine forward rollover still resets to zero for the new day",
          rolled_forward["snapshot"] == 0 and rolled_forward["date"] != day1)
    rolled_backward = load_budget_ledger(day1_spent, 1000.0 - 3600.0)  # clock jumped back pre-midnight
    check("(F10) a clock jump BACKWARD across midnight, with real usage already spent, refuses to "
          "roll over -- the ledger keeps serving the later, already-spent day rather than handing it "
          "a second full budget",
          rolled_backward["snapshot"] == 250 and rolled_backward["date"] == day1)
    day1_untouched = {BUDGET_STATE_KEY: {"date": day1, "snapshot": 0, "deliver": 0, "heartbeat": 0,
                                          "exhausted_notice_sent": False}}
    rolled_backward_zero = load_budget_ledger(day1_untouched, 1000.0 - 3600.0)
    check("a clock jump backward against an ALL-ZERO stored ledger is harmless and re-keys onto the "
          "earlier day (nothing to double-count yet)",
          rolled_backward_zero["snapshot"] == 0 and rolled_backward_zero["date"] != day1)

    # -- #1712: post_json classifies a live 429 {"reason": "kv-write-cap", "resets_at": ...} into
    # KvWriteCapError instead of a generic push-status RuntimeError, and mark_kv_write_cap_exhausted
    # exhausts ALL THREE producers' sub-budgets in one step (not just whichever one hit the cap). --
    def _fake_429(reason: str | None, resets_at: str | None) -> object:
        body: dict = {}
        if reason is not None:
            body["reason"] = reason
        if resets_at is not None:
            body["resets_at"] = resets_at
        payload = json.dumps(body).encode("utf-8")

        class _FakeOpener:
            def open(self, req, data=None, timeout=None):  # noqa: ARG002 — matches OpenerDirector.open's positional shape
                raise urllib.error.HTTPError(req.full_url, 429, "Too Many Requests", None, BytesIO(payload))
        return _FakeOpener()

    real_opener = urllib.request._opener
    try:
        urllib.request.install_opener(_fake_429("kv-write-cap", "2026-09-03T00:00:00+00:00"))
        try:
            post_json("https://example.invalid/push/tok", "{}")
            check("(#1712) a 429 kv-write-cap response raises KvWriteCapError", False)
        except KvWriteCapError as caught:
            check("(#1712) post_json classifies a 429 kv-write-cap body into KvWriteCapError", True)
            check("(#1712) KvWriteCapError carries the body's resets_at verbatim",
                  caught.resets_at == "2026-09-03T00:00:00+00:00")
        except Exception:  # noqa: BLE001 — the check above already records failure either way
            check("(#1712) a 429 kv-write-cap response raises KvWriteCapError, not some other error", False)

        # (control) a 429 with a DIFFERENT reason (or none at all) is an ordinary push failure, not
        # the write cap -- proves the classifier discriminates rather than treating every 429 alike.
        urllib.request.install_opener(_fake_429("some-other-reason", "2026-09-03T00:00:00+00:00"))
        try:
            post_json("https://example.invalid/push/tok", "{}")
            check("(control, #1712) a 429 with an unrelated reason still raises", False)
        except KvWriteCapError:
            check("(control, #1712) a 429 with an unrelated reason must NOT classify as kv-write-cap", False)
        except RuntimeError:
            check("(control, #1712) a 429 with an unrelated reason raises the ordinary RuntimeError", True)
    finally:
        urllib.request.install_opener(real_opener)

    kv_cap_ledger_state: dict = {}
    mark_kv_write_cap_exhausted(kv_cap_ledger_state, 1000.0)
    kv_cap_ledger = load_budget_ledger(kv_cap_ledger_state, 1000.0)
    check("(#1712) mark_kv_write_cap_exhausted exhausts ALL THREE producers' sub-budgets in one call",
          not snapshot_pushes_allowed(kv_cap_ledger) and not deliver_allowed(kv_cap_ledger)
          and not heartbeat_allowed(kv_cap_ledger))
    check("(#1712) mark_kv_write_cap_exhausted also marks exhausted_notice_sent, so the #1690 "
          "exhaustion-notice snapshot (itself a KV write) is never attempted",
          kv_cap_ledger["exhausted_notice_sent"] is True)

    # -- #1829: mark_kv_write_cap_exhausted WITH a real resets_at (the live-429 path, all four
    # main() call sites) leaves exhausted_notice_sent unset so the exhaustion-notice snapshot still
    # gets its one attempt, now carrying the real resets_at -- see kv_write_cap_pusher_fields, the
    # pusher-side equivalent of glass.html's cap-banner classifier.
    live_cap_state: dict = {}
    live_cap_ledger = mark_kv_write_cap_exhausted(live_cap_state, 1000.0, "2026-09-05T00:00:00+00:00")
    check("(#1829) a live 429's resets_at is stored on the ledger",
          live_cap_ledger.get("kv_write_cap_resets_at") == "2026-09-05T00:00:00+00:00")
    check("(#1829) a live 429 does NOT itself mark exhausted_notice_sent -- the exhaustion-notice "
          "snapshot still gets one attempt, now carrying the real resets_at",
          live_cap_ledger.get("exhausted_notice_sent") is not True)
    check("(#1829) a live 429 still exhausts all three sub-budgets, same as the plain-exhaustion path",
          not snapshot_pushes_allowed(live_cap_ledger) and not deliver_allowed(live_cap_ledger)
          and not heartbeat_allowed(live_cap_ledger))
    check("(#1829) kv_write_cap_pusher_fields -- the pusher-side equivalent of glass.html's cap-"
          "banner classifier -- adds kvWriteCapResetsAt once the ledger carries a live resets_at",
          kv_write_cap_pusher_fields(live_cap_ledger) == {"kvWriteCapResetsAt": "2026-09-05T00:00:00+00:00"})
    check("(control, #1829) a ledger with NO live 429 (the ordinary over-budget case) never yields "
          "the cap-banner field -- proves the classifier discriminates rather than firing on every "
          "exhaustion",
          kv_write_cap_pusher_fields(kv_cap_ledger) == {})
    check("(control, #1829) a fresh, never-exhausted ledger never yields the cap-banner field either",
          kv_write_cap_pusher_fields(load_budget_ledger({}, 1000.0)) == {})

    with tempfile.TemporaryDirectory() as cap_restart_tmp:
        cap_restart_ledger_file = Path(cap_restart_tmp) / "write-budget.local.json"
        cap_restart_state: dict = {}
        mark_kv_write_cap_exhausted(cap_restart_state, 1000.0, "2026-09-05T00:00:00+00:00")
        save_push_state(cap_restart_ledger_file, cap_restart_state)
        # A restart re-reads the ledger from a fresh, empty in-memory dict -- exactly what a
        # Stop-ScheduledTask/Start-ScheduledTask kill leaves main() with.
        reloaded_cap_ledger = load_budget_ledger(load_push_state(cap_restart_ledger_file), 1000.0)
        check("(#1829) a ledger restart keeps BOTH the exhausted sub-budget counts and the live "
              "429's resets_at",
              reloaded_cap_ledger.get("snapshot") == SNAPSHOT_DAILY_WRITES
              and reloaded_cap_ledger.get("kv_write_cap_resets_at") == "2026-09-05T00:00:00+00:00")
        # A daily cap cannot still be live once the ledger has rolled to the next UTC day: the
        # field must go with the counts, or the glass would show a cap banner for a day that never
        # hit one (#1831 review).
        rolled_cap_ledger = load_budget_ledger(load_push_state(cap_restart_ledger_file), 1000.0 + 86400.0)
        check("(#1829) a UTC-day rollover drops the live 429's resets_at along with the counts",
              "kv_write_cap_resets_at" not in rolled_cap_ledger
              and kv_write_cap_pusher_fields(rolled_cap_ledger) == {})

    already_high_state: dict = {"__write_budget__": {"date": utc_day_str(1000.0),
                                                       "snapshot": SNAPSHOT_DAILY_WRITES + 5,
                                                       "deliver": 0, "heartbeat": 0,
                                                       "exhausted_notice_sent": False}}
    mark_kv_write_cap_exhausted(already_high_state, 1000.0)
    check("(#1712) mark_kv_write_cap_exhausted never regresses a sub-budget already counted higher "
          "than its own daily target",
          already_high_state["__write_budget__"]["snapshot"] == SNAPSHOT_DAILY_WRITES + 5)

    check("(control) an empty ledger allows every producer",
          snapshot_pushes_allowed(fresh_ledger) and deliver_allowed(fresh_ledger) and heartbeat_allowed(fresh_ledger))
    check("a snapshot sub-budget spent to its own daily cap stops snapshot pushes but leaves "
          "deliver/heartbeat's OWN sub-budgets untouched -- F1's whole point: no shared pool",
          not snapshot_pushes_allowed({"date": "x", "snapshot": SNAPSHOT_DAILY_WRITES, "deliver": 0, "heartbeat": 0})
          and deliver_allowed({"date": "x", "snapshot": SNAPSHOT_DAILY_WRITES, "deliver": 0, "heartbeat": 0})
          and heartbeat_allowed({"date": "x", "snapshot": SNAPSHOT_DAILY_WRITES, "deliver": 0, "heartbeat": 0}))
    at_target = {"date": utc_day_str(0.0), "snapshot": SNAPSHOT_DAILY_WRITES, "deliver": DELIVER_DAILY_WRITES,
                 "heartbeat": HEARTBEAT_DAILY_WRITES}
    check("a ledger fully spent on every producer's own sub-budget disallows every producer",
          not snapshot_pushes_allowed(at_target) and not deliver_allowed(at_target) and not heartbeat_allowed(at_target))
    check("deliver_allowed needs room for its own full cost, not just >0 left",
          not deliver_allowed({"date": "x", "snapshot": 0, "deliver": DELIVER_DAILY_WRITES - 1, "heartbeat": 0},
                               cost=DELIVER_BATCH_KV_WRITE_COST))

    check("adaptive_snapshot_interval_s never drops below the configured coalescing floor",
          adaptive_snapshot_interval_s(fresh_ledger, 0.0, min_push_interval_s=90) >= 90)
    check("adaptive_snapshot_interval_s widens as the spendable sub-budget shrinks (fewer writes "
          "left, longer between pushes)",
          adaptive_snapshot_interval_s({"date": "x", "snapshot": SNAPSHOT_DAILY_WRITES - 50, "deliver": 0, "heartbeat": 0}, 0.0, min_push_interval_s=90)
          > adaptive_snapshot_interval_s({"date": "x", "snapshot": 0, "deliver": 0, "heartbeat": 0}, 0.0, min_push_interval_s=90))
    check("(F1) adaptive_deliver_interval_s widens the same way against deliver's OWN sub-budget",
          adaptive_deliver_interval_s({"date": "x", "snapshot": 0, "deliver": DELIVER_DAILY_WRITES - 6, "heartbeat": 0}, 0.0)
          > adaptive_deliver_interval_s({"date": "x", "snapshot": 0, "deliver": 0, "heartbeat": 0}, 0.0))
    check("(F1) adaptive_heartbeat_interval_s widens the same way against heartbeat's OWN sub-budget",
          adaptive_heartbeat_interval_s({"date": "x", "snapshot": 0, "deliver": 0, "heartbeat": HEARTBEAT_DAILY_WRITES - 2}, 0.0)
          > adaptive_heartbeat_interval_s({"date": "x", "snapshot": 0, "deliver": 0, "heartbeat": 0}, 0.0))

    check("should_log_budget fires with no prior log (fail toward one extra log line)",
          should_log_budget({}, 10_000.0) is True)
    check("should_log_budget is quiet before the hour is up",
          should_log_budget({"__last_budget_log_ts__": 10_000.0}, 10_000.0 + 3599, interval=3600) is False)
    check("should_log_budget fires again once the hour has elapsed",
          should_log_budget({"__last_budget_log_ts__": 10_000.0}, 10_000.0 + 3600, interval=3600) is True)
    check("format_budget_log_line names every producer and the current interval",
          format_budget_log_line({"snapshot": 5, "deliver": 2, "heartbeat": 1}, 123.4, target=700)
          == "budget: used 8/700 (snap 5, deliver 2, beat 1), interval now 123s")

    def _max_gap(write_ts: list, day_end: float = 86400.0) -> float:
        """Largest gap between consecutive writes, INCLUDING from t=0 to the first write and from
        the last write to day_end -- a producer that never writes until noon has a gap at the START
        of the day that a bare max(diff(consecutive)) would miss entirely. F1 (2026-09-02 review)."""
        if not write_ts:
            return day_end
        points = [0.0] + sorted(write_ts) + [day_end]
        return max(b - a for a, b in zip(points, points[1:]))

    def _legacy_shared_pool_worst_case(min_push_interval_s: float = 300, interval_seconds: float = 25) -> dict:
        """F1 (2026-09-02 review) RED CONTROL, frozen rather than derived from the live gating
        functions above (same "hardcode the shape you're proving is bad" reasoning as F2's own
        `ledger_enabled=False` arm below) -- what it reproduces and why: spec/baton.md §6, not
        restated here. Returns write-timestamp lists so the same distribution checks the new design
        must pass can be run against the old one too."""
        legacy_target, legacy_reserve = 700, 100
        used = 0
        now_ts = 0.0
        day_end = 86400.0
        last_snapshot_ts = None
        snapshot_ts: list = []
        deliver_ts: list = []
        heartbeat_ts: list = []
        hb_last = None
        ping_last = None
        while now_ts < day_end:
            if (legacy_target - used) > legacy_reserve:
                spendable = (legacy_target - used) - legacy_reserve
                interval = max(min_push_interval_s, (day_end - now_ts) / max(1, spendable))
                if last_snapshot_ts is None or (now_ts - last_snapshot_ts) >= interval:
                    used += 1
                    last_snapshot_ts = now_ts
                    snapshot_ts.append(now_ts)
                    ping_last = now_ts  # mirrors LAST_PUSH_TS_KEY suppressing the derived ping
            if legacy_target - used >= 2:  # the pre-this-PR flat deliver cost
                used += 2
                deliver_ts.append(now_ts)
            heartbeat_due = hb_last is None or (now_ts - hb_last) >= 3600
            ping_due = ping_last is None or (now_ts - ping_last) >= 300
            if (heartbeat_due or ping_due) and legacy_target - used >= 1:
                used += 1
                heartbeat_ts.append(now_ts)
                if heartbeat_due:
                    hb_last = now_ts
                if ping_due:
                    ping_last = now_ts
            now_ts += interval_seconds
        return {"used_total": used, "snapshot_write_ts": snapshot_ts,
                "deliver_write_ts": deliver_ts, "heartbeat_write_ts": heartbeat_ts}

    # -- F1 (2026-09-02 review) red control -- why a total-only gate could not have caught this,
    # and what the two distribution assertions below actually check: spec/baton.md §6, "Fleet Glass
    # write budget", not restated here.
    legacy = _legacy_shared_pool_worst_case(min_push_interval_s=300)
    legacy_max_gap = _max_gap(legacy["snapshot_write_ts"])
    legacy_last_write = max(legacy["snapshot_write_ts"] + legacy["deliver_write_ts"] + legacy["heartbeat_write_ts"], default=0.0)
    print(f"pusher.py selftest: RED (shared-pool design, {legacy['used_total']} writes total) "
          f"max snapshot gap = {int(legacy_max_gap)}s, last write overall = {int(legacy_last_write)}s of 86400s")
    check("(F1 red control) the shared-pool design this PR replaces DOES overshoot the 30-minute "
          "max-gap bound -- proving a total-only gate could not have caught this",
          legacy_max_gap > 1800)
    check("(F1 red control) the shared-pool design this PR replaces DOES go dark before the day ends",
          legacy_last_write < 86400 - 1800)

    # F7 (2026-09-02 review): run the gate for BOTH the default (90s) and #1690's own deployed
    # mitigation (300s) -- the PR body must name which one the deployed number describes.
    for label, min_push in (("min_push_interval_s=90 (gate default)", 90),
                             ("min_push_interval_s=300 (#1690's deployed mitigation)", 300)):
        result = simulate_worst_case_daily_writes(min_push_interval_s=min_push)
        result_ledger = result["ledger"]
        total = budget_used(result_ledger)
        max_gap = _max_gap(result["snapshot_write_ts"])
        last_write = max(result["snapshot_write_ts"] + result["deliver_write_ts"] + result["heartbeat_write_ts"], default=0.0)
        print(f"pusher.py selftest: worst-case daily KV writes, {label}: total {total} "
              f"(snap {result_ledger['snapshot']}, deliver {result_ledger['deliver']}, "
              f"heartbeat {result_ledger['heartbeat']}), max snapshot gap {int(max_gap)}s, "
              f"last write {int(last_write)}s of 86400s")
        check(f"arithmetic gate ({label}): worst-case daily writes stay at or under "
              f"KV_DAILY_WRITE_TARGET ({KV_DAILY_WRITE_TARGET})", total <= KV_DAILY_WRITE_TARGET)
        check(f"F1 GREEN: distribution gate ({label}) -- max gap between snapshot writes never "
              f"exceeds 30 minutes, never a half-hour blind spot", max_gap <= 1800)
        check(f"F1 GREEN: distribution gate ({label}) -- the day's last write lands within 30 "
              f"minutes of midnight, the day ends still serving", last_write >= 86400 - 1800)

    # F2 (2026-09-02 review): the pre-#1690 shape, hardcoded via ledger_enabled=False rather than
    # derived from the shipped gating functions, so this control cannot pass for the same reason the
    # real arm passes.
    pre_1690 = simulate_worst_case_daily_writes(
        ledger_enabled=False, snapshot_cost=2, deliver_cost=lambda k: k + 1)
    pre_1690_total = budget_used(pre_1690["ledger"])
    print(f"pusher.py selftest: pre-#1690 shape (ungated) worst-case daily writes = {pre_1690_total}")
    check("(F2 control) the pre-#1690 write shape, ungated, DOES overshoot 1,000/day -- proves the "
          "gate can discriminate a real overrun rather than always passing regardless of input",
          pre_1690_total > 1000)

    # -- F6 (2026-09-02 review) -- see quantize_live_for_hash's own docstring and spec/baton.md §6.
    frozen_live = {"toolCalls": 3, "outputTokens": 1234, "lastActivityAt": _quantized_activity_iso(1000.0)}
    frozen_room = {"path": "/r/a", "state": "Running", "live": dict(frozen_live)}
    quantized_now = quantize_live_for_hash([frozen_room])
    quantized_again = quantize_live_for_hash([frozen_room])
    check("(F6 control) an IDLE room's quantized contribution is identical across two separate "
          "calls -- there is no clock argument for it to have depended on",
          json.dumps(quantized_now, sort_keys=True) == json.dumps(quantized_again, sort_keys=True))
    advancing_room = {"path": "/r/a", "state": "Running",
                       "live": {"toolCalls": 3, "outputTokens": 1234,
                                "lastActivityAt": _quantized_activity_iso(1000.0 + LIVE_TELEMETRY_HASH_BUCKET_SECONDS)}}
    check("a room whose lastActivityAt genuinely advances a full bucket DOES change the quantized "
          "contribution -- proves this isn't just always collapsing to one constant value",
          json.dumps(quantized_now, sort_keys=True) != json.dumps(quantize_live_for_hash([advancing_room]), sort_keys=True))
    nudged_room = {"path": "/r/a", "state": "Running",
                   "live": {"toolCalls": 4, "outputTokens": 1234, "lastActivityAt": frozen_live["lastActivityAt"]}}
    check("a single toolCalls nudge within the same coarsening grain does not flip the hash",
          json.dumps(quantized_now, sort_keys=True) == json.dumps(quantize_live_for_hash([nudged_room]), sort_keys=True))
    jump_room = {"path": "/r/a", "state": "Running",
                 "live": {"toolCalls": 8, "outputTokens": 1234, "lastActivityAt": frozen_live["lastActivityAt"]}}
    check("a toolCalls jump that crosses the coarsening grain DOES flip the hash",
          json.dumps(quantized_now, sort_keys=True) != json.dumps(quantize_live_for_hash([jump_room]), sort_keys=True))
    no_live_room = {"path": "/r/b", "state": "Succeeded"}
    check("a room with no `live` section passes through unchanged (structural fields never touched)",
          quantize_live_for_hash([no_live_room]) == [no_live_room])
    structural_change_a = quantize_live_for_hash([{"path": "/r/a", "state": "Running", "live": {"toolCalls": 1}}])
    structural_change_b = quantize_live_for_hash([{"path": "/r/a", "state": "Succeeded", "live": {"toolCalls": 1}}])
    check("a STRUCTURAL change (state) still changes the quantized contribution immediately",
          json.dumps(structural_change_a, sort_keys=True) != json.dumps(structural_change_b, sort_keys=True))
    full_hash_a = snapshot_hash(build_wrapped(quantize_live_for_hash([frozen_room]), [], {}, 0))
    full_hash_b = snapshot_hash(build_wrapped(quantize_live_for_hash([frozen_room]), [], {}, 0))
    check("end-to-end: snapshot_hash of the quantized rooms is identical for an idle room evaluated "
          "twice, through the real build_wrapped/snapshot_hash path main() uses",
          full_hash_a == full_hash_b)

    # -- #1706 review M5: the SHARED cross-language billing gate ------------------------------------
    # One fixture file, two consumers -- this and tests/Baton.Tests/Status/
    # ClaudeEngineAndPusherBillingGateTests.cs. Every check above transcribes its line into this file;
    # these read the engine's own fixture, so a rule change landing on only one side fails on both.
    gate_path = Path(__file__).resolve().parent.parent.parent / "tests" / "Baton.Tests" / "Fixtures" / "claude-billing-gate.json"
    check("the shared claude billing-gate fixture is where both consumers look for it (#1706 M5)",
          gate_path.is_file())
    if gate_path.is_file():
        gate = json.loads(gate_path.read_text(encoding="utf-8"))
        gate_cases = gate["cases"]
        # Guard the instrument first: absent and zero must both appear in the fixture, or an
        # implementation collapsing the two -- the exact defect this gate closes -- passes every arm
        # below without the file being able to notice.
        check("the shared fixture discriminates an ABSENT billed figure from a measured 0",
              any(c["expectedBilledTokens"] is None for c in gate_cases)
              and any(c["expectedBilledTokens"] == 0 for c in gate_cases)
              and any(c["expectedBilledIsFloor"] is False for c in gate_cases))
        for gate_case in gate_cases:
            counts = extract_live_counts(gate_case["lines"], set())
            expected_billed = gate_case["expectedBilledTokens"]
            check(f"shared gate [{gate_case['name']}]: billedTokens matches the engine",
                  counts.get("billedTokens") == expected_billed
                  if expected_billed is not None else "billedTokens" not in counts)
            check(f"shared gate [{gate_case['name']}]: billedIsFloor matches the engine",
                  counts.get("billedIsFloor", False) == gate_case["expectedBilledIsFloor"])

    # -- #1557 PR-B1: FLEET_GLASS_PROJECTION_SOURCE=file's read path + the compare's own exclusion
    # logic. A synthetic projection file (never a real daemon/dotnet spawn -- see
    # `compare_projection`'s own live-machine run for that half) exercises
    # `read_projection_file`'s fresh/stale/absent arms; synthetic rooms exercise `_diff_room`'s
    # identical-vs-planted-difference arms. --
    with tempfile.TemporaryDirectory() as proj_tmp:
        proj_tmp = Path(proj_tmp)
        now = time.time()
        fresh_projection = proj_tmp / "fresh-projection.json"
        fresh_projection.write_text(json.dumps({
            "derived_at": datetime.fromtimestamp(now, tz=timezone.utc).isoformat(),
            "rooms": [{"name": "room-a", "path": "C:\\rooms\\room-a", "state": "Succeeded"}],
        }), encoding="utf-8")
        data, staleness = read_projection_file(fresh_projection, now)
        check("read_projection_file: a fresh file returns data with no staleness object",
              data is not None and staleness is None and data["rooms"][0]["name"] == "room-a")

        stale_derived_at = datetime.fromtimestamp(
            now - PROJECTION_STALE_AFTER_S - 1, tz=timezone.utc).isoformat()
        stale_projection = proj_tmp / "stale-projection.json"
        stale_projection.write_text(json.dumps({"derived_at": stale_derived_at, "rooms": []}), encoding="utf-8")
        data, staleness = read_projection_file(stale_projection, now)
        check("read_projection_file: a file older than PROJECTION_STALE_AFTER_S falls back to derive (data is None)",
              data is None)
        check("read_projection_file: the stale fallback's staleness carries the daemon's own derived_at",
              staleness is not None and staleness["stale"] is True
              and staleness["daemon_derived_at"] == stale_derived_at)

        data, staleness = read_projection_file(proj_tmp / "does-not-exist.json", now)
        check("read_projection_file: an absent file falls back to derive with daemon_derived_at=None",
              data is None and staleness is not None and staleness["stale"] is True
              and staleness["daemon_derived_at"] is None)

    # -- #1557 PR-B2 ACCEPTANCE: the pushed snapshot is identical across both projection sources
    # over one frozen fixture, or every difference is named. Runs the pusher's OWN derivation
    # (`attach_live_telemetry`/`attach_pruned_info`) and the file read over the SAME room tree, then
    # pushes both through main()'s `assemble_wrapped` AND `snapshot_post_body`, then diffs with
    # `snapshot_identity_diffs` -- see that function for the two exclusions and why this is a
    # different instrument from `_diff_room` above.
    #
    # SCOPE, stated rather than implied: the base per-room fields (`name`/`path`/`state`/`steps`/…)
    # are shared between the two arms by construction. Both sources get them from the SAME C#
    # projector -- the daemon calls `FleetStatusTool`'s room processing in-process, the derive path
    # reaches the same code over MCP -- so re-deriving them twice here would measure nothing. What
    # this arm measures is the `live` block (Python `extract_live_counts` vs. C#
    # `TokenBudgetMonitor`/`StdoutTailRenderer`) and the snapshot assembly around it.
    # No vendors or non-empty pruned block is exercised by this fixture.
    # The cross-process half is `--compare-projection`'s job.
    #
    # The file arm's `live` values are HAND-DERIVED from the fixture lines below, not transcribed
    # from a run of the derive path: `billedTokens` 1200 and `billedIsFloor` True come from the
    # shared cross-language fixture's own `expectedBilledTokens`/`expectedBilledIsFloor` for this
    # exact case (read above -- the engine is the oracle, not this file); `turns` 2 is the two
    # DISTINCT message ids across three lines (#1686 dedup); `contextTokens` 702 is the newest usage
    # line's own level (input 2 + cache_creation 700 + cache_read 0), NOT a sum across lines;
    # `cacheReadTokens` 0 and `toolCalls` 0 are the fixture's own zeros (no cache_read, no tool_use
    # block). A derive-side change to any of those reds this arm.
    with tempfile.TemporaryDirectory() as ident_tmp:
        ident_root = Path(ident_tmp)
        ident_gate_case = None
        if gate_path.is_file():
            ident_gate_case = next((c for c in json.loads(gate_path.read_text(encoding="utf-8"))["cases"]
                                    if c["name"].startswith("a repeated message.id")), None)
        check("#1557 PR-B2 identity arm: the shared billing-gate fixture supplies its stdout lines "
              "(the arm's independent oracle for billedTokens/billedIsFloor)",
              ident_gate_case is not None)
        if ident_gate_case is not None:
            ident_run_room = ident_root / "room-run"
            (ident_run_room / "artifacts" / "execution_e1").mkdir(parents=True)
            (ident_run_room / "artifacts" / "execution_e1" / ".stdout.log").write_text(
                "\n".join(ident_gate_case["lines"]) + "\n", encoding="utf-8")
            ident_done_room = ident_root / "room-done"
            ident_done_room.mkdir()
            (ident_done_room / "terminal.json").write_text("{}", encoding="utf-8")

            ident_base = [
                {"name": "room-run", "path": str(ident_run_room), "state": "Running", "role": "worker",
                 "steps": [{"id": "s1", "state": "Running", "execution": "e1",
                            "timestamp": "2026-09-05T00:00:00Z"}]},
                {"name": "room-done", "path": str(ident_done_room), "state": "Succeeded", "role": "worker",
                 "steps": [{"id": "s1", "state": "Succeeded", "timestamp": "2026-09-05T00:00:00Z"}]},
            ]
            ident_underhood = [{"k": "v"}]
            # What `derive_snapshot_and_timelines` returns from its per-room `room_detail` calls.
            # The daemon now writes this exact content-free projection into projection.json too.
            ident_timelines = {str(ident_run_room): [{"type": "executionStarted",
                                                       "timestamp": "2026-09-05T00:00:00Z"}]}

            derive_rooms = json.loads(json.dumps(ident_base))
            attach_live_telemetry(derive_rooms, {}, [])
            attach_pruned_info(derive_rooms, {})
            derive_derived_at = datetime.now(timezone.utc).isoformat()
            derive_wrapped, _, _, _, _, _ = assemble_wrapped(
                derive_rooms, ident_underhood, ident_timelines, 0)
            derive_post_body = json.loads(snapshot_post_body(derive_wrapped, derive_derived_at))
            file_derived_at = derive_derived_at

            file_rooms = json.loads(json.dumps(ident_base))
            file_rooms[0]["live"] = {
                "toolCalls": 0, "billedTokens": ident_gate_case["expectedBilledTokens"],
                "turns": 2, "billedIsFloor": ident_gate_case["expectedBilledIsFloor"],
                "contextTokens": 702, "cacheReadTokens": 0,
                # Quantized off the same mtime the derive arm reads; excluded from the diff either
                # way (`_SNAPSHOT_IDENTITY_EXCLUSIONS`), present so the field's absence on one side
                # is not what makes the arm pass.
                "lastActivityAt": _quantized_activity_iso(
                    (ident_run_room / "artifacts" / "execution_e1" / ".stdout.log").stat().st_mtime),
            }
            # The three daemon-only fields (`_COMPARE_SHAPE_ONLY_KEYS`) ride the file side alone --
            # excluded by name, and present here so the exclusion is actually exercised.
            file_rooms[0].update({"processAlive": "alive", "stdout_last_write_ago_sec": 1.0, "elapsed": 12.0})
            ident_projection = ident_root / "projection.json"
            ident_projection.write_text(json.dumps({
                "derived_at": file_derived_at,
                "rooms": file_rooms,
                "timelines": ident_timelines,
            }), encoding="utf-8")
            ident_data, ident_staleness = read_projection_file(ident_projection, time.time())
            check("#1557 PR-B2 identity arm: the fixture projection file reads fresh (no fallback)",
                  ident_data is not None and ident_staleness is None)
            file_timelines = ident_data.get("timelines") if isinstance(ident_data, dict) else None
            file_wrapped, _, _, _, _, _ = assemble_wrapped(
                ident_data["rooms"], ident_underhood,
                file_timelines if isinstance(file_timelines, dict) else {}, 0)

            file_post_body = json.loads(snapshot_post_body(file_wrapped, ident_data["derived_at"]))
            identity_diffs = snapshot_identity_diffs(derive_post_body, file_post_body)
            check("#1902 acceptance: the full posted bodies are identical between file and derive. "
                  f"Actual diff: {identity_diffs}",
                  identity_diffs == [])
            check("#1902 acceptance: each posted body carries the same derived_at",
                  derive_post_body["derived_at"] == derive_derived_at
                  and file_post_body["derived_at"] == file_derived_at)
            check("#1902 acceptance: file timelines exactly match derive timelines",
                  derive_wrapped["timelines"] == ident_timelines
                  and file_wrapped["timelines"] == ident_timelines)

            # CONTROL, read before trusting the green above: the comparator must RED on a real
            # derivation difference. Without this the arm certifies the harness, not the change.
            control_rooms = json.loads(json.dumps(ident_data["rooms"]))
            control_rooms[0]["live"]["billedTokens"] = ident_gate_case["expectedBilledTokens"] + 1
            control_wrapped, _, _, _, _, _ = assemble_wrapped(control_rooms, ident_underhood, {}, 0)
            control_post_body = json.loads(snapshot_post_body(control_wrapped, ident_data["derived_at"]))
            check("(control) #1557 PR-B2 acceptance: a one-token `live.billedTokens` difference on "
                  "the file side IS reported -- the identity arm above discriminates, it is not "
                  "green because everything volatile was excluded",
                  "rooms" in snapshot_identity_diffs(derive_post_body, control_post_body))
            # -- #1557 PR-B2 found-while-fixing: drop_stale_rooms runs on the room list BEFORE the
            # live/pruned attach, so in `derive` mode `newest_timestamp` never sees those blocks --
            # `attach_live_telemetry`'s own doc calls that deliberate. In `file` mode the daemon has
            # already embedded them, which silently put two real mtimes into the staleness scan and
            # made the two sources drop DIFFERENT rooms. Fixed by adding `live`/`pruned` to
            # `_NEWEST_TIMESTAMP_SKIP_KEYS`; this arm is what sees it, since the identity arm above
            # calls assemble_wrapped directly and never runs the filter.
            stale_step_ts = (datetime.now(timezone.utc) - timedelta(days=30)).isoformat()
            aged_base = {"name": "room-aged", "path": str(ident_run_room), "state": "Running",
                         "steps": [{"id": "s1", "state": "Running", "execution": "e1",
                                    "timestamp": stale_step_ts}]}
            aged_derive_body = json.dumps({"rooms": [json.loads(json.dumps(aged_base))]})
            aged_file_room = json.loads(json.dumps(aged_base))
            aged_file_room["live"] = {"toolCalls": 0,
                                      "lastActivityAt": datetime.now(timezone.utc).isoformat()}
            aged_file_room["pruned"] = {"count": 1,
                                        "prunedAt": datetime.now(timezone.utc).isoformat()}
            aged_file_body = json.dumps({"rooms": [aged_file_room]})
            derive_kept, derive_hidden = drop_stale_rooms(aged_derive_body, 3)
            file_kept, file_hidden = drop_stale_rooms(aged_file_body, 3)
            check("#1557 PR-B2: a room aged past the cutoff but carrying a FRESH live.lastActivityAt/"
                  "pruned.prunedAt is dropped by BOTH sources -- the file's embedded live/pruned "
                  "blocks must not enter the staleness scan the derive path never shows them to",
                  json.loads(derive_kept)["rooms"] == [] and json.loads(file_kept)["rooms"] == []
                  and derive_hidden == 1 and file_hidden == 1)
            check("(control) #1557 PR-B2: the same room with a RECENT step timestamp is kept by both "
                  "sources -- the arm above is not green because drop_stale_rooms drops everything",
                  all(len(json.loads(drop_stale_rooms(json.dumps({"rooms": [dict(
                          r, steps=[{"id": "s1", "state": "Running", "execution": "e1",
                                     "timestamp": datetime.now(timezone.utc).isoformat()}])]}), 3)[0])["rooms"]) == 1
                      for r in (json.loads(json.dumps(aged_base)), aged_file_room)))

            control_top = dict(file_post_body, stale_hidden_count=1)
            check("(control) #1557 PR-B2 acceptance: a TOP-LEVEL field difference is reported too "
                  "-- `_diff_room` never sees these, which is why this arm exists alongside it",
                  "stale_hidden_count" in snapshot_identity_diffs(derive_post_body, control_top))

    identical_derive = {"name": "room-a", "path": "C:\\rooms\\room-a", "state": "Running",
                         "live": {"toolCalls": 3, "lastActivityAt": "2026-09-03T12:00:00+00:00"}}
    identical_file = {"name": "room-a", "path": "C:\\rooms\\room-a", "state": "Running",
                       "live": {"toolCalls": 3, "lastActivityAt": "2026-09-03T12:01:00+00:00"}}
    check("compare identity diff: identical rooms diff clean (lastActivityAt's own bucket excluded)",
          _diff_room("C:\\rooms\\room-a", identical_derive, identical_file, True) == [])

    planted_file = {"name": "room-a", "path": "C:\\rooms\\room-a", "state": "Running",
                     "live": {"toolCalls": 4, "lastActivityAt": "2026-09-03T12:01:00+00:00"}}
    planted_diff = _diff_room("C:\\rooms\\room-a", identical_derive, planted_file, True)
    check("compare identity diff: a planted field difference is reported (exit 1 -- main() maps this to sys.exit)",
          any("toolCalls" in d for d in planted_diff))

    shape_invalid_file = {"processAlive": "not-a-real-status"}
    check("compare identity diff: shape-only fields (never diffed against derive) are still shape-checked",
          any("processAlive" in d for d in _diff_room("C:\\rooms\\room-a", {}, shape_invalid_file, True)))

    # -- #1807/#1812: volatile-live tolerance arms, on a Running room recent enough that
    # `_room_is_settled` reads False (anchored to real `time.time()`, not a fixed date, so these stay
    # "not settled" regardless of when selftest runs) -- exercises the STILL-MOVING tolerance path.
    # `derive_is_later` is passed explicitly rather than assumed. --
    _live_now = time.time()
    monotone_derive = {"name": "room-a", "path": "C:\\rooms\\room-a", "state": "Running", "role": "worker",
                        "live": {"toolCalls": 5, "turns": 2, "billedTokens": 1000, "cacheReadTokens": 50,
                                 "contextTokens": 200, "stdoutTail": "line2\nline3\nline4\n",
                                 "lastActivityAt": datetime.fromtimestamp(_live_now, tz=timezone.utc).isoformat()}}
    monotone_file = {"name": "room-a", "path": "C:\\rooms\\room-a", "state": "Running", "role": "worker",
                      "live": {"toolCalls": 3, "turns": 1, "billedTokens": 800, "cacheReadTokens": 40,
                               "contextTokens": 150, "stdoutTail": "line2\nline3\n",
                               "lastActivityAt": datetime.fromtimestamp(_live_now - 5, tz=timezone.utc).isoformat()}}
    check("#1807 volatile-live tolerance: a Running room whose live counters moved forward (derive later) diffs clean",
          _diff_room("C:\\rooms\\room-a", monotone_derive, monotone_file, True) == [])

    backwards_file = {**monotone_file, "live": {**monotone_file["live"], "toolCalls": 9}}
    backwards_diff = _diff_room("C:\\rooms\\room-a", monotone_derive, backwards_file, True)
    check("#1807 volatile-live tolerance: a true counter that moved BACKWARDS (derive later) is still a real difference",
          any("toolCalls" in d and "backwards" in d for d in backwards_diff))

    # Measured on a real live run (PR body): cacheReadTokens/contextTokens are a LEVEL from the
    # latest turn, not a running counter -- on a still-Running room they can legitimately go DOWN.
    level_dropped_file = {**monotone_file,
                           "live": {**monotone_file["live"], "cacheReadTokens": 999999, "contextTokens": 50000}}
    check("#1807 volatile-live tolerance: cacheReadTokens/contextTokens dropping on a Running room is NOT a real difference",
          _diff_room("C:\\rooms\\room-a", monotone_derive, level_dropped_file, True) == [])

    role_diff_file = {**monotone_file, "role": "conductor"}
    role_diff = _diff_room("C:\\rooms\\room-a", monotone_derive, role_diff_file, True)
    check("#1807 volatile-live tolerance: a room whose role differs still fails (the tolerance doesn't swallow it)",
          any("role" in d for d in role_diff))

    # -- #1812 F2: derive_is_later is READ, not assumed. When the FILE sample is actually the later
    # one (a daemon write landed between compare_projection's derive step and its file-read step),
    # the direction check must invert -- the file's higher counters are legitimate, and a counter
    # that's higher on derive (the now-EARLIER sample) is the real backwards move. --
    file_later_file = {**monotone_file, "live": {**monotone_file["live"], "toolCalls": 9, "turns": 4,
                                                  "billedTokens": 5000, "stdoutTail": "line3\nline4\nline5\n"}}
    check("#1812 derived_at ordering: file sample later -- file's higher counters are NOT a backwards move",
          _diff_room("C:\\rooms\\room-a", monotone_derive, file_later_file, False) == [])

    file_later_backwards_file = {**monotone_file, "live": {**monotone_file["live"], "toolCalls": 1}}
    inverted_diff = _diff_room("C:\\rooms\\room-a", monotone_derive, file_later_backwards_file, False)
    check("#1812 derived_at ordering: file sample later -- derive's now-stale HIGHER toolCalls (5) "
          "vs file's fresher lower one (1) is a real backwards move once inverted",
          any("toolCalls" in d and "backwards" in d for d in inverted_diff))

    check("#1812 derived_at ordering: unknown order (derived_at missing/unparseable on either side) "
          "skips the monotone-direction check rather than assuming one",
          _derive_is_later(None, "2026-09-03T12:00:00+00:00") is None
          and _derive_is_later("not-a-timestamp", "2026-09-03T12:00:00+00:00") is None)
    unknown_order_diff = _diff_room("C:\\rooms\\room-a", monotone_derive, backwards_file, None)
    check("#1812 derived_at ordering: unknown order -- a counter difference that would fail under "
          "either assumed direction is NOT reported when the order can't be established",
          not any("toolCalls" in d for d in unknown_order_diff))

    # -- #1812 F1: once a room is SETTLED (state terminal, or quiet past one write interval), the
    # #1807 level tolerance goes back to exact -- a real sum-vs-level derivation bug (like the
    # cacheReadTokens one this PR fixes) must not hide behind "it's just a level that moved". --
    settled_derive = {**monotone_derive, "state": "Succeeded",
                       "live": {**monotone_derive["live"], "cacheReadTokens": 100, "contextTokens": 200}}
    settled_file_matching = {**monotone_file, "state": "Succeeded",
                              "live": {**monotone_file["live"], "cacheReadTokens": 100, "contextTokens": 200,
                                       "stdoutTail": "line2\nline3\nline4\n"}}
    check("#1812 settled-room level exactness: identical cacheReadTokens/stdoutTail on a settled room diffs clean",
          _diff_room("C:\\rooms\\room-a", settled_derive, settled_file_matching, True) == [])

    settled_file_mismatched = {**monotone_file, "state": "Succeeded",
                                "live": {**monotone_file["live"], "cacheReadTokens": 881499}}
    settled_diff = _diff_room("C:\\rooms\\room-a", settled_derive, settled_file_mismatched, True)
    check("#1812 settled-room level exactness: a mismatched cacheReadTokens on a SETTLED room is a real "
          "difference (this is the exact shape of the sum-vs-level bug the PR fixes)",
          any("cacheReadTokens" in d and "settled" in d for d in settled_diff))

    settled_tail_diff = _diff_room("C:\\rooms\\room-a", settled_derive,
                                    {**settled_file_matching, "live": {**settled_file_matching["live"], "stdoutTail": "line9\n"}},
                                    True)
    check("#1812 settled-room level exactness: a mismatched stdoutTail on a settled room is a real difference",
          any("stdoutTail" in d and "settled" in d for d in settled_tail_diff))

    # -- #1812 F3: on a still-Running room, stdoutTail is compared by containment (the later sample's
    # tail must still hold the earlier sample's last line), not by exact match or presence/shape only.
    tail_ok_file = {**monotone_file, "live": {**monotone_file["live"], "stdoutTail": "line3\n"}}
    check("#1812 Running-room stdoutTail: derive's tail contains file's (earlier) last line -- clean",
          _diff_room("C:\\rooms\\room-a", monotone_derive, tail_ok_file, True) == [])

    tail_broken_file = {**monotone_file, "live": {**monotone_file["live"], "stdoutTail": "totally different content\n"}}
    tail_diff = _diff_room("C:\\rooms\\room-a", monotone_derive, tail_broken_file, True)
    check("#1812 Running-room stdoutTail: derive's tail does NOT contain file's (earlier) last line -- a real difference",
          any("stdoutTail" in d and "does not contain" in d for d in tail_diff))

    check("#1807 settled-rooms floor: a terminal room counts as settled",
          _room_is_settled({"state": "Succeeded"}) is True)
    with tempfile.TemporaryDirectory() as settled_tmp:
        quiet_room = {"state": "Running", "path": settled_tmp,
                      "live": {"lastActivityAt": datetime.fromtimestamp(
                          time.time() - 600, tz=timezone.utc).isoformat()}}
        check("#1814 settled-rooms floor: a Running room quiet for 10 minutes with no terminal fact "
              "is NOT settled -- quiet time is not evidence",
              _room_is_settled(quiet_room) is False)

        terminal_dir = Path(settled_tmp) / "term-room"
        terminal_dir.mkdir()
        (terminal_dir / "terminal.json").write_text("{}", encoding="utf-8")
        check("#1814 settled-rooms floor: a Running room whose dir holds terminal.json IS settled",
              _room_is_settled({"state": "Running", "path": str(terminal_dir),
                                 "live": {"lastActivityAt": datetime.now(timezone.utc).isoformat()}}) is True)

    fewer_than_floor = [{"state": "Running",
                          "live": {"lastActivityAt": datetime.now(timezone.utc).isoformat()}}] * 2
    check(f"#1807 settled-rooms floor: {_MIN_SETTLED_ROOMS_FOR_GREEN} is the actual floor "
          "(a compare with fewer settled rooms than this must not pass, i.e. 'green on 0')",
          sum(1 for r in fewer_than_floor if _room_is_settled(r)) < _MIN_SETTLED_ROOMS_FOR_GREEN)

    # -- #1558: ntfy severity tiers --
    check("tier table: lane_failed is urgent", ntfy_priority_for_event("lane_failed") == "urgent")
    check("tier table: lane_succeeded_with_warnings is default",
          ntfy_priority_for_event("lane_succeeded_with_warnings") == "default")
    check("tier table: zombie_detected is high", ntfy_priority_for_event("zombie_detected") == "high")
    check("tier table: pusher_anomaly is high", ntfy_priority_for_event("pusher_anomaly") == "high")
    check("tier table: an unrecognized event type fails toward default, never urgent",
          ntfy_priority_for_event("something_made_up") == "default")

    # -- #1558: quiet hours, both sides of a wrapping window, plus a non-wrapping one. Built as UTC
    # instants offset by the known EST (UTC-5) January offset rather than via ZoneInfo directly, so
    # this selftest passes with or without the `tzdata` package -- and on a box lacking it (this dev
    # box measured: Windows, Python 3.12, no `tzdata` installed) it exercises the SAME
    # `_us_eastern_offset_fallback` path `in_quiet_hours` itself falls back to, rather than a
    # different one than production hits here. --
    def _et(month, day, hour, minute):
        return datetime(2026, month, day, hour, minute, tzinfo=timezone.utc) + timedelta(hours=5)

    wrapping_cfg = {"ntfy_quiet_hours": {"start": "22:00", "end": "07:00", "timezone": "America/New_York"}}
    inside_late = _et(1, 5, 23, 30)
    inside_early = _et(1, 5, 3, 0)
    outside = _et(1, 5, 12, 0)
    boundary_start = _et(1, 5, 22, 0)
    boundary_end = _et(1, 5, 7, 0)
    check("quiet hours (wrapping 22:00-07:00): 23:30 ET is inside", in_quiet_hours(wrapping_cfg, inside_late))
    check("quiet hours (wrapping 22:00-07:00): 03:00 ET is inside", in_quiet_hours(wrapping_cfg, inside_early))
    check("quiet hours (wrapping 22:00-07:00): 12:00 ET is outside", not in_quiet_hours(wrapping_cfg, outside))
    check("quiet hours: the start boundary itself is inside (inclusive)",
          in_quiet_hours(wrapping_cfg, boundary_start))
    check("quiet hours: the end boundary itself is outside (exclusive)",
          not in_quiet_hours(wrapping_cfg, boundary_end))
    non_wrapping_cfg = {"ntfy_quiet_hours": {"start": "13:00", "end": "15:00", "timezone": "America/New_York"}}
    check("quiet hours (non-wrapping 13:00-15:00): 14:00 ET is inside",
          in_quiet_hours(non_wrapping_cfg, _et(1, 5, 14, 0)))
    check("quiet hours (non-wrapping 13:00-15:00): 16:00 ET is outside",
          not in_quiet_hours(non_wrapping_cfg, _et(1, 5, 16, 0)))
    check("quiet hours: no ntfy_quiet_hours configured at all -> never quiet",
          not in_quiet_hours({}, inside_late))
    # a UTC instant that lands inside the window once converted to ET -- proves the conversion runs,
    # not just a same-timezone comparison.
    utc_instant_inside_et_window = datetime(2026, 1, 6, 4, 30, tzinfo=timezone.utc)  # 23:30 ET
    check("quiet hours: a UTC `now` is converted to the configured timezone before comparing",
          in_quiet_hours(wrapping_cfg, utc_instant_inside_et_window))

    # -- #1558: dedup -- first occurrence alerts, a repeat folds, a magnitude increase re-alerts,
    # and clearing lets a later occurrence read as fresh again --
    dedup_state: dict = {}
    check("dedup: first occurrence alerts",
          ntfy_dedup_decision(dedup_state, "k1", 1, 1000.0) == NTFY_DEDUP_ALERT)
    check("dedup: same magnitude repeat folds",
          ntfy_dedup_decision(dedup_state, "k1", 1, 1025.0) == NTFY_DEDUP_FOLD)
    check("dedup: a magnitude increase re-alerts",
          ntfy_dedup_decision(dedup_state, "k1", 3, 1050.0) == NTFY_DEDUP_REALERT)
    check("dedup: back down to the same (now higher-water) magnitude folds again",
          ntfy_dedup_decision(dedup_state, "k1", 3, 1075.0) == NTFY_DEDUP_FOLD)
    ntfy_clear_dedup(dedup_state, "k1")
    check("dedup: clearing a resolved condition drops its entry",
          "k1" not in dedup_state)
    check("dedup: a fresh occurrence after clearing alerts again, not folds",
          ntfy_dedup_decision(dedup_state, "k1", 1, 1100.0) == NTFY_DEDUP_ALERT)

    # -- #1558: transport is one small function behind an injectable sender; a selftest never makes
    # a live ntfy request -- `fake_sender` below is the only sender any check here ever uses --
    sent_calls: list = []

    def fake_sender(url: str, headers: dict, body: bytes) -> None:
        sent_calls.append((url, dict(headers), body))

    send_ntfy(fake_sender, "https://ntfy.sh", "my-topic", None, "Title", "Body", "urgent")
    check("send_ntfy: hits <server>/<topic>", sent_calls[-1][0] == "https://ntfy.sh/my-topic")
    check("send_ntfy: sets the Priority header from the tier passed in",
          sent_calls[-1][1]["Priority"] == "urgent")
    check("send_ntfy: no token -> no Authorization header", "Authorization" not in sent_calls[-1][1])
    sent_calls.clear()
    send_ntfy(fake_sender, "https://ntfy.sh", "my-topic", "tok123", "Title", "Body", "high")
    check("send_ntfy: a token adds a Bearer Authorization header",
          sent_calls[-1][1].get("Authorization") == "Bearer tok123")

    # -- #1558: maybe_push_ntfy_event -- disabled, quiet-hours suppression (urgent always sends),
    # and dedup fold, end to end --
    sent_calls.clear()
    no_topic_state: dict = {}
    outcome = maybe_push_ntfy_event({}, {}, no_topic_state, "lane_failed", "k", 1, "t", "m",
                                     outside, sender=fake_sender)
    check("maybe_push_ntfy_event: no ntfy_topic configured -> disabled, no send",
          outcome == "disabled" and not sent_calls)

    quiet_cfg = {"ntfy_topic": "my-topic", **wrapping_cfg}
    default_tier_state: dict = {}
    outcome = maybe_push_ntfy_event(quiet_cfg, {}, default_tier_state, "lane_succeeded_with_warnings",
                                     "k", 1, "t", "m", inside_late, sender=fake_sender)
    check("maybe_push_ntfy_event: a default-tier event during quiet hours is suppressed",
          outcome == "suppressed-quiet-hours" and not sent_calls)

    urgent_state: dict = {}
    outcome = maybe_push_ntfy_event(quiet_cfg, {}, urgent_state, "lane_failed", "k", 1, "t", "m",
                                     inside_late, sender=fake_sender)
    check("maybe_push_ntfy_event: urgent always sends, even during quiet hours",
          outcome == "sent" and len(sent_calls) == 1)
    sent_calls.clear()
    outcome = maybe_push_ntfy_event(quiet_cfg, {}, urgent_state, "lane_failed", "k", 1, "t", "m",
                                     inside_late, sender=fake_sender)
    check("maybe_push_ntfy_event: an unchanged repeat folds and does not send again",
          outcome == "folded" and not sent_calls)
    outcome = maybe_push_ntfy_event(quiet_cfg, {}, urgent_state, "lane_failed", "k", 3, "t", "m",
                                     inside_late, sender=fake_sender)
    check("maybe_push_ntfy_event: a magnitude increase re-alerts and sends again",
          outcome == "sent" and len(sent_calls) == 1)

    # -- #1558: per-room classification and the main()-facing push_ntfy_room_events sweep --
    check("ntfy_room_classification: a Failed room classifies as lane_failed with try as magnitude",
          ntfy_room_classification({"state": "Failed", "try": 2}) == ("lane_failed", 2))
    check("ntfy_room_classification: a Stalled room classifies as zombie_detected",
          ntfy_room_classification({"state": "Stalled"}) == ("zombie_detected", 1))
    check("ntfy_room_classification: a plain Succeeded room (no retries, no error) is not notifiable",
          ntfy_room_classification({"state": "Succeeded", "try": 1}) is None)
    check("ntfy_room_classification: Succeeded after a retry classifies as succeeded-with-warnings",
          ntfy_room_classification({"state": "Succeeded", "try": 2}) == ("lane_succeeded_with_warnings", 2))
    check("ntfy_room_classification: a Running room is not notifiable",
          ntfy_room_classification({"state": "Running"}) is None)

    sent_calls.clear()
    room_events_cfg = {"ntfy_topic": "my-topic"}
    room_events_state: dict = {}
    failing_room = {"path": "/rooms/x", "name": "room-x", "state": "Failed", "try": 1, "error": "boom"}
    push_ntfy_room_events(room_events_cfg, {}, room_events_state, [failing_room], outside, sender=fake_sender)
    check("push_ntfy_room_events: a newly-failed room alerts once", len(sent_calls) == 1)
    sent_calls.clear()
    push_ntfy_room_events(room_events_cfg, {}, room_events_state, [failing_room], outside, sender=fake_sender)
    check("push_ntfy_room_events: the same still-failed room folds on the next cycle", not sent_calls)
    failing_room_retried = {**failing_room, "try": 4}
    push_ntfy_room_events(room_events_cfg, {}, room_events_state, [failing_room_retried], outside, sender=fake_sender)
    check("push_ntfy_room_events: a higher try count on the same failed room re-alerts",
          len(sent_calls) == 1)
    sent_calls.clear()
    recovered_room = {"path": "/rooms/x", "name": "room-x", "state": "Running"}
    push_ntfy_room_events(room_events_cfg, {}, room_events_state, [recovered_room], outside, sender=fake_sender)
    check("push_ntfy_room_events: a recovered room sends nothing and clears its dedup entries",
          not sent_calls and "lane_failed:/rooms/x" not in room_events_state)
    push_ntfy_room_events(room_events_cfg, {}, room_events_state, [failing_room], outside, sender=fake_sender)
    check("push_ntfy_room_events: the SAME room failing again after recovering alerts fresh, not folded",
          len(sent_calls) == 1)

    # -- #1817 review: rooms that leave the fleet entirely get their dedup keys pruned, not just
    # cleared while merely non-notifiable this cycle --
    prune_state = {
        "lane_failed:/rooms/gone": {"first_seen": 1, "last_seen": 1, "magnitude": 1, "alert_count": 1},
        "zombie_detected:/rooms/still-here": {"first_seen": 1, "last_seen": 1, "magnitude": 1, "alert_count": 1},
        "pusher_anomaly:deliver": {"first_seen": 1, "last_seen": 1, "magnitude": 1, "alert_count": 1},
    }
    prune_ntfy_dedup_state(prune_state, [{"path": "/rooms/still-here", "state": "Stalled"}])
    check("prune_ntfy_dedup_state: a room absent from the current room_list loses its dedup key",
          "lane_failed:/rooms/gone" not in prune_state)
    check("prune_ntfy_dedup_state: a room still present keeps its fold state",
          "zombie_detected:/rooms/still-here" in prune_state)
    check("prune_ntfy_dedup_state: pusher_anomaly keys are left alone (not per-room, already bounded)",
          "pusher_anomaly:deliver" in prune_state)

    # -- #1558: pusher-level anomaly, keyed by block name so a repeating fault folds --
    with tempfile.TemporaryDirectory() as anomaly_tmp:
        anomaly_state_path = Path(anomaly_tmp) / "ntfy-state.json"
        anomaly_cfg = {"ntfy_topic": "my-topic"}
        sent_calls.clear()
        push_ntfy_pusher_anomaly(anomaly_cfg, {}, anomaly_state_path, "deliver",
                                  RuntimeError("boom"), outside, sender=fake_sender)
        check("push_ntfy_pusher_anomaly: a first-time anomaly sends", len(sent_calls) == 1)
        sent_calls.clear()
        push_ntfy_pusher_anomaly(anomaly_cfg, {}, anomaly_state_path, "deliver",
                                  RuntimeError("boom again, different text"), outside, sender=fake_sender)
        check("push_ntfy_pusher_anomaly: a repeat in the SAME block folds regardless of exception text",
              not sent_calls)

    if failures:
        print(f"pusher.py selftest: FAIL -- {len(failures)} check(s):", file=sys.stderr)
        for f in failures:
            print(f"  !! {f}", file=sys.stderr)
        return 1
    print("pusher.py selftest: pass")
    return 0


if __name__ == "__main__":
    if "--selftest" in sys.argv:
        sys.exit(_selftest())
    if "--compare-projection" in sys.argv:
        _cfg = json.loads((HERE / "pusher.config.json").read_text(encoding="utf-8"))
        sys.exit(compare_projection(_cfg["dll"], _cfg.get("roots", [])))
    main()
