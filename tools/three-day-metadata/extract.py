#!/usr/bin/env python3
"""Build a bounded, offline lifecycle dataset and report from metadata snapshots.

Expected input files are COVERAGE.txt, queue.json, queue-decisions.jsonl,
runway-admissions.jsonl, quota-ledger.jsonl, and room-events.jsonl. The coverage
file owns the exact inclusive window.

Examples:
  python tools/three-day-metadata/extract.py --selftest
  python tools/three-day-metadata/extract.py SNAPSHOT --format dataset
  python tools/three-day-metadata/extract.py SNAPSHOT --format report

Optional --handoffs JSON contains conductor/start/end intervals. Optional
--exclusions JSON contains exact execution IDs. Overlapping handoffs stay
unattributed, and an inclusive view is retained beside any exclusion view.

The tool deliberately consumes only structured metadata. It does not discover
room directories, read worker streams, call vendors, infer conductor identity
from room names, choose a statistical outlier, or infer missing usage as zero.
It follows tools/room-rate-sweep's deterministic room-name and structured
terminal-accounting boundaries but does not parse streams or derive a
vendor-comparable billed total. Real inputs and output belong outside the
repository; stdout lets the caller place them in a protected artifact directory.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import statistics
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Iterable
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError


SOURCE_SPECS = {
    "queue": ("queue.json", None),
    "queue_decisions": ("queue-decisions.jsonl", "at"),
    "runway_admissions": ("runway-admissions.jsonl", "at"),
    "quota_ledger": ("quota-ledger.jsonl", "at"),
    "room_events": ("room-events.jsonl", "at"),
}
CONTEXT_SOURCE_FILES = (
    "comparison-plan.md",
    "conductor-register.md",
    "queue-advice.md",
)

# This is the complete analytical terminal vocabulary. Product state is retained
# verbatim elsewhere; these values only give the report stable polarity.
TERMINAL_EVENT_OUTCOMES = {
    "executionSucceeded": "succeeded",
    "executionFailed": "failed",
    "executionCancelled": "cancelled",
    "executionArrested": "arrested",
    "executionIndeterminate": "indeterminate",
}
QUOTA_OUTCOMES = {
    "succeeded": "succeeded",
    "finishedduringteardown": "succeeded",
    "failed": "failed",
    "retryable": "failed",
    "permanent": "failed",
    "exhausteduntil": "failed",
    "tooldenied": "failed",
    "cancelled": "cancelled",
    "indeterminate": "indeterminate",
    "arrested": "arrested",
}

EXACT_WINDOW_DURATION = timedelta(hours=72)

# Ordered, complete work-mix proxy vocabulary. The first matching predicate wins.
WORK_MIX_RULES = (
    ("review", ("review", "rereview")),
    ("repair", ("fix", "repair", "cleanup")),
    ("measurement", ("measure", "investigat", "research", "audit", "probe")),
    ("docs", ("docs", "readme", "documentation")),
    ("implementation", ("implement",)),
)

# Queue reasons can contain operator prose. Only these bounded categories leave
# the extractor; every other value is retained as an observed "other" count.
QUEUE_REASON_MARKERS = (
    ("no-items", ("no-items",)),
    ("launch-gap", ("gap",)),
    ("slots", ("slot",)),
    ("memory", ("memory", "freegb", "floor")),
    ("hold", ("hold", "held")),
    ("runway", ("runway", "quota")),
)

USAGE_FIELDS = (
    "tokensIn",
    "tokensOut",
    "cacheRead",
    "cacheCreation",
    "thinking",
    "turns",
    "wallClockMs",
)
ROLE_DISPLAY_ORDER = {
    "implement": 0,
    "review": 1,
    "advise": 2,
    "fact-check": 3,
    "unknown": 4,
}
CONDUCTOR_ATTRIBUTION_STATES = (
    "attributed_unique",
    "ambiguous_overlapping_intervals",
    "unattributed_interval_gap",
    "unavailable_lifecycle_time",
    "unavailable_no_handoff_intervals",
)


@dataclass(frozen=True)
class Window:
    start: datetime
    end: datetime

    def contains(self, value: datetime) -> bool:
        return self.start <= value <= self.end


def parse_time(raw: Any) -> datetime | None:
    if not isinstance(raw, str) or not raw.strip():
        return None
    text = raw.strip()
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    try:
        value = datetime.fromisoformat(text)
    except ValueError:
        return None
    if value.tzinfo is None:
        return None
    return value.astimezone(timezone.utc)


def iso(value: datetime | None) -> str | None:
    if value is None:
        return None
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def america_new_york(value: datetime) -> datetime:
    """Convert UTC to America/New_York without requiring the optional tzdata wheel.

    Windows Python installations commonly lack an IANA database. The fallback is
    the U.S. rule in force since 2007: DST runs from 07:00Z on March's second
    Sunday through 06:00Z on November's first Sunday.
    """
    try:
        return value.astimezone(ZoneInfo("America/New_York"))
    except ZoneInfoNotFoundError:
        year = value.year

        def nth_sunday(month: int, ordinal: int) -> int:
            first = datetime(year, month, 1, tzinfo=timezone.utc)
            first_sunday = 1 + ((6 - first.weekday()) % 7)
            return first_sunday + 7 * (ordinal - 1)

        dst_start = datetime(year, 3, nth_sunday(3, 2), 7, tzinfo=timezone.utc)
        dst_end = datetime(year, 11, nth_sunday(11, 1), 6, tzinfo=timezone.utc)
        offset = -4 if dst_start <= value < dst_end else -5
        return value.astimezone(timezone(timedelta(hours=offset)))


def room_key(raw: Any) -> str | None:
    if not isinstance(raw, str) or not raw.strip():
        return None
    return raw.replace("\\", "/").rstrip("/").split("/")[-1].lower()


def canonical(record: dict[str, Any]) -> str:
    return json.dumps(record, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def exact_dedupe(records: Iterable[dict[str, Any]]) -> tuple[list[dict[str, Any]], int]:
    seen: set[str] = set()
    unique: list[dict[str, Any]] = []
    duplicates = 0
    for record in records:
        key = canonical(record)
        if key in seen:
            duplicates += 1
            continue
        seen.add(key)
        unique.append(record)
    return unique, duplicates


def file_fingerprint(path: Path) -> dict[str, Any]:
    payload = path.read_bytes()
    return {
        "file": path.name,
        "bytes": len(payload),
        "sha256": hashlib.sha256(payload).hexdigest(),
    }


def parse_jsonl_payloads(payloads: Iterable[bytes]) -> tuple[list[dict[str, Any]], dict[str, int]]:
    records: list[dict[str, Any]] = []
    parse_failures = 0
    non_object_rows = 0
    line_count = 0
    for payload in payloads:
        line_count += 1
        try:
            line = payload.decode("utf-8")
            value = json.loads(line)
        except (json.JSONDecodeError, UnicodeError):
            parse_failures += 1
            continue
        if not isinstance(value, dict):
            non_object_rows += 1
            continue
        records.append(value)
    records, exact_duplicates = exact_dedupe(records)
    return records, {
        "rows_seen": line_count,
        "rows_parsed": len(records),
        "parse_failures": parse_failures,
        "non_object_rows": non_object_rows,
        "exact_duplicates_removed": exact_duplicates,
    }


def load_jsonl(path: Path) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    with path.open("rb") as handle:
        records, counts = parse_jsonl_payloads(handle)
    return records, {**file_fingerprint(path), **counts}


def load_queue(path: Path) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    parse_failures = 0
    non_object_rows = 0
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, UnicodeError):
        value = {}
        parse_failures = 1
    items = value.get("items", []) if isinstance(value, dict) else []
    if not isinstance(items, list):
        items = []
        parse_failures += 1
    records = []
    for item in items:
        if isinstance(item, dict):
            records.append(item)
        else:
            non_object_rows += 1
    rows_seen = len(items)
    records, exact_duplicates = exact_dedupe(records)
    audit = file_fingerprint(path)
    audit.update(
        {
            "rows_seen": rows_seen,
            "rows_parsed": len(records),
            "parse_failures": parse_failures,
            "non_object_rows": non_object_rows,
            "exact_duplicates_removed": exact_duplicates,
        }
    )
    return records, audit


def validate_window_duration(start: datetime, end: datetime, source: Any) -> None:
    if end - start != EXACT_WINDOW_DURATION:
        raise ValueError(f"{source}: snapshot window must be exactly 72 hours")


def load_coverage(path: Path) -> tuple[Window, dict[str, Any]]:
    text = path.read_text(encoding="utf-8")
    match = re.search(r"Snapshot window:\s*(\S+)\s+through\s*(\S+)\.", text)
    if match is None:
        raise ValueError(f"{path}: missing exact snapshot window")
    start = parse_time(match.group(1))
    end = parse_time(match.group(2))
    if start is None or end is None or start > end:
        raise ValueError(f"{path}: invalid snapshot window")
    validate_window_duration(start, end, path)
    retained = re.search(r"(\d+) retained events", text)
    locked = re.search(r"running ([A-Za-z0-9_-]+) was skipped", text)
    manifest = file_fingerprint(path)
    manifest.update(
        {
            "declared_retained_room_events": int(retained.group(1)) if retained else None,
            "declared_locked_room_skipped": locked.group(1) if locked else None,
            "declares_other_parse_failures_unknown": "parse failures" in text and "unknown" in text,
            "declares_conductor_usage_missing": "No vendor conductor transcript/token extraction" in text,
            "declares_fable_identity_missing": "Fable anomaly IDs remain unknown" in text,
        }
    )
    return Window(start, end), manifest


def record_times(record: dict[str, Any], source: str) -> list[datetime]:
    fields = ("AddedAt", "LaunchedAt") if source == "queue" else (SOURCE_SPECS[source][1],)
    return [stamp for field in fields if field and (stamp := parse_time(record.get(field))) is not None]


def in_window(record: dict[str, Any], source: str, window: Window) -> bool:
    return any(window.contains(stamp) for stamp in record_times(record, source))


def identity_conflicts(source: str, rows: list[dict[str, Any]]) -> int:
    selectors = {
        "queue": lambda r: room_key(r.get("RoomDirectory")),
        "queue_decisions": lambda r: (r.get("at"), r.get("decision"), r.get("reason")),
        "runway_admissions": lambda r: (r.get("at"), room_key(r.get("room"))),
        "quota_ledger": lambda r: r.get("execution"),
        "room_events": lambda r: (
            room_key(r.get("room")),
            r.get("ExecutionId"),
            r.get("type"),
            r.get("at"),
        ),
    }
    grouped: dict[Any, set[str]] = defaultdict(set)
    for row in rows:
        identity = selectors[source](row)
        if identity is not None:
            grouped[identity].add(canonical(row))
    return sum(1 for values in grouped.values() if len(values) > 1)


def classify_work(item: dict[str, Any]) -> str:
    text = " ".join(str(item.get(field) or "") for field in ("Tag", "Reason", "Role")).lower()
    for label, needles in WORK_MIX_RULES:
        if any(needle in text for needle in needles):
            return label
    return "other"


def queue_reason_category(row: dict[str, Any]) -> str:
    decision = str(row.get("decision") or "unknown").lower()
    if decision == "advanced":
        return "lifecycle-advanced"
    if decision == "cancelled":
        return "operator-cancelled"
    reason = str(row.get("reason") or "").lower()
    if not reason:
        return "none-recorded"
    for category, markers in QUEUE_REASON_MARKERS:
        if any(marker in reason for marker in markers):
            return category
    return "other"


def event_outcome(events: list[dict[str, Any]], quota: dict[str, Any] | None) -> str:
    terminal = [
        (parse_time(event.get("at")), TERMINAL_EVENT_OUTCOMES[event.get("type")])
        for event in events
        if event.get("type") in TERMINAL_EVENT_OUTCOMES
    ]
    terminal = [(stamp, outcome) for stamp, outcome in terminal if stamp is not None]
    if terminal:
        return max(terminal, key=lambda pair: pair[0])[1]
    quota_outcome = str((quota or {}).get("outcome") or "").lower()
    if quota_outcome in QUOTA_OUTCOMES:
        return QUOTA_OUTCOMES[quota_outcome]
    return "in_flight" if events else "unknown"


def first_event_time(events: list[dict[str, Any]], kinds: set[str]) -> datetime | None:
    stamps = [
        stamp
        for event in events
        if event.get("type") in kinds and (stamp := parse_time(event.get("at"))) is not None
    ]
    return min(stamps) if stamps else None


def last_event_time(events: list[dict[str, Any]], kinds: set[str]) -> datetime | None:
    stamps = [
        stamp
        for event in events
        if event.get("type") in kinds and (stamp := parse_time(event.get("at"))) is not None
    ]
    return max(stamps) if stamps else None


def seconds_between(start: datetime | None, end: datetime | None) -> float | None:
    if start is None or end is None or end < start:
        return None
    return round((end - start).total_seconds(), 3)


def load_optional_rows(path: Path | None, key: str) -> list[dict[str, Any]]:
    if path is None:
        return []
    value = json.loads(path.read_text(encoding="utf-8"))
    rows = value.get(key, []) if isinstance(value, dict) else value
    if not isinstance(rows, list) or not all(isinstance(row, dict) for row in rows):
        raise ValueError(f"{path}: expected a JSON array of objects or object.{key}")
    return rows


def validate_handoffs(handoffs: list[dict[str, Any]]) -> None:
    for index, handoff in enumerate(handoffs):
        conductor = handoff.get("conductor")
        begin = parse_time(handoff.get("start"))
        raw_end = handoff.get("end")
        end = parse_time(raw_end) if raw_end is not None else None
        if not isinstance(conductor, str) or not conductor.strip():
            raise ValueError(f"handoff {index}: conductor must be a non-empty string")
        if begin is None:
            raise ValueError(f"handoff {index}: start must be an offset-aware timestamp")
        if raw_end is not None and end is None:
            raise ValueError(f"handoff {index}: end must be null or an offset-aware timestamp")
        if end is not None and end < begin:
            raise ValueError(f"handoff {index}: end precedes start")


def attribute_conductor(
    attribution_time: datetime | None, handoffs: list[dict[str, Any]]
) -> dict[str, Any]:
    if not handoffs:
        return {
            "conductor": None,
            "state": "unavailable_no_handoff_intervals",
            "candidates": [],
        }
    if attribution_time is None:
        return {
            "conductor": None,
            "state": "unavailable_lifecycle_time",
            "candidates": [],
        }
    candidates = sorted({
        str(handoff["conductor"]).strip()
        for handoff in handoffs
        if (begin := parse_time(handoff.get("start"))) is not None
        and begin <= attribution_time
        and ((end := parse_time(handoff.get("end"))) is None or attribution_time <= end)
    })
    if len(candidates) == 1:
        return {"conductor": candidates[0], "state": "attributed_unique", "candidates": candidates}
    if candidates:
        return {
            "conductor": None,
            "state": "ambiguous_overlapping_intervals",
            "candidates": candidates,
        }
    return {"conductor": None, "state": "unattributed_interval_gap", "candidates": []}


def select_lifecycle_evidence(
    event_rows: list[dict[str, Any]],
    quota_rows: list[dict[str, Any]],
    window: Window,
) -> tuple[list[dict[str, Any]], list[dict[str, Any]], dict[str, Any]]:
    """Select overlapping execution intervals, then retain their evidence through cutoff.

    Rows after the frozen cutoff may establish that a pre-window start spans the
    window, but are not admitted to lifecycle calculations. This reconstructs only
    joins present in the supplied snapshot; it cannot recover upstream truncation.
    """
    events_by_execution: dict[str, list[dict[str, Any]]] = defaultdict(list)
    quotas_by_execution: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in event_rows:
        execution = row.get("ExecutionId")
        if isinstance(execution, str) and execution:
            events_by_execution[execution].append(row)
    for row in quota_rows:
        execution = row.get("execution")
        if isinstance(execution, str) and execution:
            quotas_by_execution[execution].append(row)

    observed_in_window_ids: set[str] = set()
    intersecting_ids: set[str] = set()
    for execution in set(events_by_execution) | set(quotas_by_execution):
        execution_events = events_by_execution.get(execution, [])
        execution_quotas = quotas_by_execution.get(execution, [])
        observed_times = [
            stamp
            for row in (*execution_events, *execution_quotas)
            if (stamp := parse_time(row.get("at"))) is not None
        ]
        if any(window.contains(stamp) for stamp in observed_times):
            observed_in_window_ids.add(execution)
            intersecting_ids.add(execution)
            continue
        starts = [
            stamp
            for row in execution_events
            if row.get("type") in {"executionAttemptStarted", "executionStarted"}
            and (stamp := parse_time(row.get("at"))) is not None
        ]
        if not starts or min(starts) > window.end:
            continue
        ends = [
            stamp
            for row in execution_events
            if (row.get("type") in TERMINAL_EVENT_OUTCOMES or row.get("type") == "executionExited")
            and (stamp := parse_time(row.get("at"))) is not None
        ]
        ends.extend(
            stamp
            for row in execution_quotas
            if (stamp := parse_time(row.get("at"))) is not None
        )
        if not ends or max(ends) >= window.start:
            intersecting_ids.add(execution)

    joined_events = [
        row
        for row in event_rows
        if row.get("ExecutionId") in intersecting_ids
        and (stamp := parse_time(row.get("at"))) is not None
        and stamp <= window.end
    ]
    joined_quotas = [
        row
        for row in quota_rows
        if row.get("execution") in intersecting_ids
        and (stamp := parse_time(row.get("at"))) is not None
        and stamp <= window.end
    ]
    return joined_events, joined_quotas, {
        "intersecting_execution_ids": len(intersecting_ids),
        "observed_in_window_execution_ids": len(observed_in_window_ids),
        "interval_only_execution_ids": len(intersecting_ids - observed_in_window_ids),
        "in_window_event_rows": sum(in_window(row, "room_events", window) for row in event_rows),
        "in_window_quota_rows": sum(in_window(row, "quota_ledger", window) for row in quota_rows),
        "retained_pre_window_event_rows": sum(
            parse_time(row.get("at")) < window.start for row in joined_events
        ),
        "retained_pre_window_quota_rows": sum(
            parse_time(row.get("at")) < window.start for row in joined_quotas
        ),
        "joined_event_rows_through_cutoff": len(joined_events),
        "joined_quota_rows_through_cutoff": len(joined_quotas),
        "post_window_event_rows_excluded": sum(
            row.get("ExecutionId") in intersecting_ids
            and (stamp := parse_time(row.get("at"))) is not None
            and stamp > window.end
            for row in event_rows
        ),
        "post_window_quota_rows_excluded": sum(
            row.get("execution") in intersecting_ids
            and (stamp := parse_time(row.get("at"))) is not None
            and stamp > window.end
            for row in quota_rows
        ),
        "upstream_truncation_recoverable": False,
    }


def build_lifecycles(
    queue_rows: list[dict[str, Any]],
    decision_rows: list[dict[str, Any]],
    event_rows: list[dict[str, Any]],
    quota_rows: list[dict[str, Any]],
    window: Window,
    handoffs: list[dict[str, Any]],
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    queue_by_room: dict[str, list[dict[str, Any]]] = defaultdict(list)
    queue_by_tag: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for item in queue_rows:
        if key := room_key(item.get("RoomDirectory")):
            queue_by_room[key].append(item)
        if isinstance(item.get("Tag"), str) and item["Tag"]:
            queue_by_tag[item["Tag"]].append(item)

    launches_by_room: dict[str, list[dict[str, Any]]] = defaultdict(list)
    advances_by_tag: dict[str, list[dict[str, Any]]] = defaultdict(list)
    launches_by_tag: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for decision in decision_rows:
        kind = str(decision.get("decision") or "").lower()
        tag = decision.get("tag")
        if kind == "launched":
            if key := room_key(decision.get("room")):
                launches_by_room[key].append(decision)
            if isinstance(tag, str) and tag:
                launches_by_tag[tag].append(decision)
        elif kind == "advanced" and isinstance(tag, str) and tag:
            advances_by_tag[tag].append(decision)

    events_by_execution: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    event_rooms: set[str] = set()
    for event in event_rows:
        key = room_key(event.get("room"))
        execution = event.get("ExecutionId")
        if key:
            event_rooms.add(key)
        if key and isinstance(execution, str) and execution:
            events_by_execution[(key, execution)].append(event)

    quota_by_execution: dict[str, list[dict[str, Any]]] = defaultdict(list)
    quota_rooms: set[str] = set()
    for quota in quota_rows:
        key = room_key(quota.get("room"))
        execution = quota.get("execution")
        if key:
            quota_rooms.add(key)
        if isinstance(execution, str) and execution:
            quota_by_execution[execution].append(quota)

    lifecycle_keys = set(events_by_execution)
    for execution, quota_matches in quota_by_execution.items():
        event_key = next((key for key in events_by_execution if key[1] == execution), None)
        quota_room = room_key(quota_matches[-1].get("room")) or "unknown"
        lifecycle_keys.add(event_key or (quota_room, execution))

    lifecycles: list[dict[str, Any]] = []
    for room, execution in sorted(lifecycle_keys):
        events = events_by_execution.get((room, execution), [])
        quota_matches = quota_by_execution.get(execution, [])
        quota = quota_matches[-1] if quota_matches else None
        queue_matches = queue_by_room.get(room, [])
        item = queue_matches[-1] if queue_matches else {}
        started = first_event_time(events, {"executionStarted"})
        attempted = first_event_time(events, {"executionAttemptStarted"})
        exited = last_event_time(events, {"executionExited"})
        verify_started = first_event_time(events, {"verifyStarted"})
        verify_ended = last_event_time(events, {"verifyPassed", "verifyFailed"})
        terminal = last_event_time(events, set(TERMINAL_EVENT_OUTCOMES))
        quota_recorded = parse_time((quota or {}).get("at"))
        lifecycle_bound = started or attempted or terminal or quota_recorded
        launch_candidates = [
            row
            for row in launches_by_room.get(room, [])
            if (stamp := parse_time(row.get("at"))) is not None
            and (lifecycle_bound is None or stamp <= lifecycle_bound)
        ]
        launch_fact = max(launch_candidates, key=lambda row: parse_time(row.get("at"))) if launch_candidates else None
        tag = (launch_fact or {}).get("tag") or item.get("Tag")
        origin_matches = queue_by_tag.get(tag, []) if isinstance(tag, str) else []
        origin_item = origin_matches[-1] if origin_matches else item
        added = parse_time(origin_item.get("AddedAt"))
        launched = parse_time((launch_fact or {}).get("at")) or parse_time(item.get("LaunchedAt"))
        tag_launches = sorted(
            (
                (stamp, row)
                for row in launches_by_tag.get(tag, [])
                if (stamp := parse_time(row.get("at"))) is not None
            ),
            key=lambda pair: pair[0],
        ) if isinstance(tag, str) else []
        previous_launch = max(
            (stamp for stamp, _row in tag_launches if launched is not None and stamp < launched),
            default=None,
        )
        advances = [
            row
            for row in advances_by_tag.get(tag, [])
            if launched is not None
            and (stamp := parse_time(row.get("at"))) is not None
            and (previous_launch is None or previous_launch < stamp)
            and stamp <= launched
        ] if isinstance(tag, str) else []
        round_queued = parse_time(max(advances, key=lambda row: parse_time(row.get("at"))).get("at")) if advances else None
        if round_queued is not None:
            queue_wait_start = round_queued
            queue_wait_basis = "round_transition_to_launch"
        elif launched is not None and previous_launch is None:
            queue_wait_start = added
            queue_wait_basis = "item_added_to_initial_launch" if added is not None else "unavailable"
        else:
            queue_wait_start = None
            queue_wait_basis = "unavailable_missing_round_transition"
        outcome = event_outcome(events, quota)
        usage = {field: (quota or {}).get(field) for field in USAGE_FIELDS}
        present_usage = sum(value is not None for value in usage.values())
        if present_usage == 0:
            usage_status = "unavailable"
        elif present_usage == len(USAGE_FIELDS):
            usage_status = "complete_fields"
        else:
            usage_status = "partial_fields"
        attribution_time = started or attempted or launched or quota_recorded
        attribution = attribute_conductor(attribution_time, handoffs)
        lifecycles.append(
            {
                "room": room,
                "execution": execution,
                "worker_role": item.get("Role") or None,
                "adapter": (quota or {}).get("adapter") or item.get("Adapter") or None,
                "model": (quota or {}).get("model") or item.get("Model") or None,
                "work_mix_proxy": classify_work(item) if item else "other",
                "queue_state_at_snapshot": item.get("State") or None,
                "queue_round": item.get("Round") if item else None,
                "observed_launch_sequence": (
                    1 + sum(stamp < launched for stamp, _row in tag_launches)
                    if launched is not None else None
                ),
                "inherited": None if added is None else added < window.start,
                "in_flight": outcome == "in_flight",
                "outcome": outcome,
                "conductor": attribution["conductor"],
                "conductor_attribution_state": attribution["state"],
                "conductor_candidates": attribution["candidates"],
                "conductor_attribution_at": iso(attribution_time),
                "added_at": iso(added),
                "launched_at": iso(launched),
                "launch_time_source": "queue_decision" if launch_fact is not None else ("queue_snapshot" if launched is not None else "unavailable"),
                "round_queued_at": iso(round_queued),
                "attempt_started_at": iso(attempted),
                "execution_started_at": iso(started),
                "execution_exited_at": iso(exited),
                "terminal_at": iso(terminal),
                "quota_recorded_at": iso(quota_recorded),
                "observed_day_utc": iso(terminal or quota_recorded or exited or started)[:10]
                if (terminal or quota_recorded or exited or started)
                else None,
                "queue_wait_seconds": seconds_between(queue_wait_start, launched),
                "queue_wait_basis": queue_wait_basis,
                "item_age_at_launch_seconds": seconds_between(added, launched),
                "launch_to_start_seconds": seconds_between(launched, started),
                "lane_service_seconds": seconds_between(started, exited),
                "verification_seconds": seconds_between(verify_started, verify_ended),
                "execution_to_terminal_seconds": seconds_between(started, terminal),
                "usage_status": usage_status,
                "usage": usage,
                "quota_rows_for_execution": len(quota_matches),
                "queue_rows_for_room": len(queue_matches),
            }
        )

    event_execution_ids = {execution for _room, execution in events_by_execution}
    joins = {
        "event_execution_count": len(events_by_execution),
        "event_rooms": len(event_rooms),
        "event_rooms_with_queue": len(event_rooms & set(queue_by_room)),
        "event_rooms_with_quota_room": len(event_rooms & quota_rooms),
        "event_executions_with_quota": sum(
            1 for _room, execution in events_by_execution if execution in quota_by_execution
        ),
        "quota_execution_count": len(quota_by_execution),
        "quota_executions_with_events": len(set(quota_by_execution) & event_execution_ids),
        "queue_rooms": len(queue_by_room),
        "queue_rooms_with_events": len(set(queue_by_room) & event_rooms),
    }
    return lifecycles, joins


def fable_candidates(queue_rows: list[dict[str, Any]], lifecycles: list[dict[str, Any]]) -> list[dict[str, Any]]:
    rooms: dict[str, dict[str, Any]] = {}
    for item in queue_rows:
        searchable = " ".join(str(item.get(field) or "") for field in ("Tag", "Reason", "RoomDirectory"))
        if "fable" not in searchable.lower():
            continue
        key = room_key(item.get("RoomDirectory"))
        if key:
            rooms[key] = {"room": key, "tag": item.get("Tag") or None, "executions": []}
    for lifecycle in lifecycles:
        if lifecycle["room"] in rooms:
            rooms[lifecycle["room"]]["executions"].append(lifecycle["execution"])
    return [rooms[key] for key in sorted(rooms)]


def load_exclusion_ids(rows: list[dict[str, Any]]) -> set[str]:
    ids: set[str] = set()
    for row in rows:
        value = row.get("execution")
        if isinstance(value, str) and value:
            ids.add(value)
    return ids


def match_exclusions(
    exclusion_ids: set[str], lifecycles: list[dict[str, Any]]
) -> tuple[list[dict[str, Any]], list[str]]:
    lifecycle_ids = {row["execution"] for row in lifecycles}
    rows = [
        {"execution": execution, "matched_lifecycle": execution in lifecycle_ids}
        for execution in sorted(exclusion_ids)
    ]
    return rows, sorted(exclusion_ids - lifecycle_ids)


def apply_exclusions(
    exclusion_ids: set[str], lifecycles: list[dict[str, Any]]
) -> tuple[list[dict[str, Any]], list[dict[str, Any]], list[str], set[str], str]:
    exclusion_rows, unmatched_ids = match_exclusions(exclusion_ids, lifecycles)
    matched_ids = exclusion_ids - set(unmatched_ids)
    primary = [row for row in lifecycles if row["execution"] not in matched_ids]
    if matched_ids and unmatched_ids:
        status = "applied_matched_exact_ids_with_unmatched_requests"
    elif matched_ids:
        status = "applied_exact_ids"
    elif exclusion_ids:
        status = "requested_ids_unmatched_no_exclusion_applied"
    else:
        status = "unresolved_no_exclusion_applied"
    return primary, exclusion_rows, unmatched_ids, matched_ids, status


def count_by(rows: Iterable[dict[str, Any]], fields: tuple[str, ...]) -> list[dict[str, Any]]:
    counts = Counter(tuple(row.get(field) for field in fields) for row in rows)
    result = []
    for values, count in sorted(counts.items(), key=lambda pair: tuple(str(v) for v in pair[0])):
        record = dict(zip(fields, values))
        record["observed_rows"] = count
        result.append(record)
    return result


def numeric_summary(values: Iterable[Any]) -> dict[str, Any]:
    numbers = sorted(float(value) for value in values if isinstance(value, (int, float)) and not isinstance(value, bool))
    if not numbers:
        return {"n": 0, "median": None, "p90_nearest_rank": None, "max": None}
    p90 = numbers[max(0, math.ceil(0.9 * len(numbers)) - 1)]
    return {
        "n": len(numbers),
        "median": round(statistics.median(numbers), 3),
        "p90_nearest_rank": round(p90, 3),
        "max": round(max(numbers), 3),
    }


def phase_summaries(
    lifecycles: list[dict[str, Any]], *, by_conductor: bool = False
) -> list[dict[str, Any]]:
    result = []
    group_fields = (
        ("conductor", "conductor_attribution_state", "worker_role")
        if by_conductor else ("worker_role",)
    )
    groups: dict[tuple[Any, ...], list[dict[str, Any]]] = defaultdict(list)
    for row in lifecycles:
        groups[tuple(row.get(field) for field in group_fields)].append(row)
    for values, selected in sorted(
        groups.items(),
        key=lambda pair: (
            *(str(value or "unknown") for value in pair[0][:-1]),
            ROLE_DISPLAY_ORDER.get(str(pair[0][-1] or "unknown"), 99),
            str(pair[0][-1] or "unknown"),
        ),
    ):
        group = dict(zip(group_fields, values))
        for field in (
            "queue_wait_seconds",
            "launch_to_start_seconds",
            "lane_service_seconds",
            "verification_seconds",
            "execution_to_terminal_seconds",
        ):
            result.append({**group, "phase": field, **numeric_summary(row.get(field) for row in selected)})
    return result


def usage_summaries(
    lifecycles: list[dict[str, Any]], *, by_conductor: bool = False
) -> list[dict[str, Any]]:
    groups: dict[tuple[Any, ...], list[dict[str, Any]]] = defaultdict(list)
    for row in lifecycles:
        key: tuple[Any, ...] = (
            str(row.get("adapter") or "unknown"),
            str(row.get("model") or "unknown"),
            str(row.get("worker_role") or "unknown"),
        )
        if by_conductor:
            key = (
                row.get("conductor"),
                row.get("conductor_attribution_state"),
                *key,
            )
        groups[key].append(row)
    result = []
    for key, rows in sorted(
        groups.items(), key=lambda pair: tuple(str(value or "unknown") for value in pair[0])
    ):
        if by_conductor:
            conductor, attribution_state, adapter, model, role = key
        else:
            adapter, model, role = key
        item: dict[str, Any] = {
            "adapter": adapter,
            "model": model,
            "worker_role": role,
            "execution_rows": len(rows),
        }
        if by_conductor:
            item = {
                "conductor": conductor,
                "conductor_attribution_state": attribution_state,
                **item,
            }
        for field in USAGE_FIELDS:
            values = [row["usage"].get(field) for row in rows if row["usage"].get(field) is not None]
            item[f"{field}_observed_n"] = len(values)
            item[f"{field}_sum"] = sum(values) if values else None
        result.append(item)
    return result


def analytical_view(lifecycles: list[dict[str, Any]]) -> dict[str, Any]:
    return {
        "lifecycle_row_count": len(lifecycles),
        "outcomes": count_by(lifecycles, ("worker_role", "outcome")),
        "phase_summaries": phase_summaries(lifecycles),
        "usage_summaries": usage_summaries(lifecycles),
        "conductor_outcomes": count_by(
            lifecycles,
            ("conductor", "conductor_attribution_state", "worker_role", "outcome"),
        ),
        "conductor_phase_summaries": phase_summaries(lifecycles, by_conductor=True),
        "conductor_usage_summaries": usage_summaries(lifecycles, by_conductor=True),
    }


def build_dataset(input_dir: Path, handoff_path: Path | None, exclusion_path: Path | None) -> dict[str, Any]:
    coverage_path = input_dir / "COVERAGE.txt"
    window, coverage_declaration = load_coverage(coverage_path)
    tables: dict[str, list[dict[str, Any]]] = {}
    manifests = []
    for source, (filename, timestamp_field) in SOURCE_SPECS.items():
        path = input_dir / filename
        rows, audit = load_queue(path) if source == "queue" else load_jsonl(path)
        audit["source"] = source
        audit["timestamp_field"] = "AddedAt|LaunchedAt" if source == "queue" else timestamp_field
        audit["rows_with_valid_timestamp"] = sum(bool(record_times(row, source)) for row in rows)
        audit["rows_intersecting_window"] = sum(in_window(row, source, window) for row in rows)
        stamps = [stamp for row in rows for stamp in record_times(row, source)]
        audit["earliest_available_timestamp"] = iso(min(stamps)) if stamps else None
        audit["latest_available_timestamp"] = iso(max(stamps)) if stamps else None
        audit["identity_conflicts"] = identity_conflicts(source, rows)
        manifests.append(audit)
        tables[source] = rows

    handoffs = load_optional_rows(handoff_path, "handoffs")
    validate_handoffs(handoffs)
    exclusions = load_optional_rows(exclusion_path, "exclusions")
    joined_events, quotas, lifecycle_evidence = select_lifecycle_evidence(
        tables["room_events"], tables["quota_ledger"], window
    )
    events = [row for row in tables["room_events"] if in_window(row, "room_events", window)]
    decisions_through_cutoff = [
        row
        for row in tables["queue_decisions"]
        if (stamp := parse_time(row.get("at"))) is not None and stamp <= window.end
    ]
    lifecycles, joins = build_lifecycles(
        tables["queue"], decisions_through_cutoff, joined_events, quotas, window, handoffs
    )
    room_event_manifest = next(item for item in manifests if item["source"] == "room_events")
    room_event_manifest["rows_retained_for_lifecycle_join"] = len(joined_events)
    room_event_manifest["retained_pre_window_rows"] = lifecycle_evidence["retained_pre_window_event_rows"]
    quota_manifest = next(item for item in manifests if item["source"] == "quota_ledger")
    quota_manifest["rows_retained_for_lifecycle_join"] = len(quotas)
    quota_manifest["retained_pre_window_rows"] = lifecycle_evidence["retained_pre_window_quota_rows"]
    exclusion_ids = load_exclusion_ids(exclusions)
    (
        primary,
        exclusion_rows,
        unmatched_exclusion_ids,
        matched_exclusion_ids,
        exclusion_status,
    ) = apply_exclusions(exclusion_ids, lifecycles)
    candidates = fable_candidates(tables["queue"], lifecycles)

    attribution_state_counts = count_by(lifecycles, ("conductor_attribution_state",))
    uncertain_attribution_rows = sum(
        row.get("conductor_attribution_state") != "attributed_unique" for row in lifecycles
    )
    if not handoffs:
        attribution_status = "unavailable_no_authoritative_intervals"
    elif uncertain_attribution_rows:
        attribution_status = "provided_with_attribution_uncertainty"
    else:
        attribution_status = "provided_all_lifecycle_rows_uniquely_attributed"

    decisions = [row for row in tables["queue_decisions"] if in_window(row, "queue_decisions", window)]
    decision_categories = [
        {"decision": row.get("decision"), "reason_category": queue_reason_category(row)}
        for row in decisions
    ]
    admissions = [row for row in tables["runway_admissions"] if in_window(row, "runway_admissions", window)]
    event_type_counts = count_by(events, ("type",))
    declared = coverage_declaration.get("declared_retained_room_events")
    parsed_events = next(item["rows_parsed"] for item in manifests if item["source"] == "room_events")
    context_sources = []
    for filename in CONTEXT_SOURCE_FILES:
        path = input_dir / filename
        if path.exists():
            context_sources.append({
                **file_fingerprint(path),
                "status": "fingerprinted_not_machine_joined",
            })

    return {
        "schema_version": 3,
        "generated_by": "tools/three-day-metadata/extract.py",
        "window": {
            "start_utc": iso(window.start),
            "end_utc": iso(window.end),
            "start_america_new_york": america_new_york(window.start).isoformat(),
            "end_america_new_york": america_new_york(window.end).isoformat(),
            "inclusive": True,
        },
        "coverage_manifest": {
            "coverage_declaration": coverage_declaration,
            "sources": manifests,
            "context_sources": context_sources,
            "declared_vs_parsed_room_event_delta": None if declared is None else declared - parsed_events,
            "lifecycle_evidence": lifecycle_evidence,
            "locked_room_after_cutoff_verified": False,
            "github_window_queries": "unavailable",
        },
        "exclusion_manifest": {
            "requested_episode": "Fable runaway",
            "status": exclusion_status,
            "requested_execution_ids": sorted(exclusion_ids),
            "primary_excluded_execution_ids": sorted(matched_exclusion_ids),
            "exact_exclusions": exclusion_rows,
            "unmatched_exclusion_ids": unmatched_exclusion_ids,
            "candidate_ambiguity": candidates,
            "inclusive_lifecycle_rows": len(lifecycles),
            "primary_lifecycle_rows": len(primary),
        },
        "conductor_attribution": {
            "status": attribution_status,
            "interval_count": len(handoffs),
            "uniquely_attributed_lifecycle_rows": sum(
                row.get("conductor_attribution_state") == "attributed_unique" for row in lifecycles
            ),
            "uncertain_lifecycle_rows": uncertain_attribution_rows,
            "state_counts": attribution_state_counts,
            "attribution_time_precedence": [
                "executionStarted", "executionAttemptStarted", "queueLaunch", "quotaRecorded"
            ],
        },
        "join_manifest": joins,
        "inclusive": {
            "lifecycle_rows": lifecycles,
            **analytical_view(lifecycles),
        },
        "primary": {
            "same_as_inclusive": not matched_exclusion_ids,
            **analytical_view(primary),
        },
        "available_axes": {
            "event_types": event_type_counts,
            "queue_decisions": count_by(decision_categories, ("decision", "reason_category")),
            "queue_live_weight": numeric_summary(row.get("liveWeight") for row in decisions),
            "queue_free_gb": numeric_summary(row.get("freeGb") for row in decisions),
            "runway_admissions": count_by(admissions, ("vendor", "decision", "decidedBy")),
            "work_mix_proxy": count_by(lifecycles, ("work_mix_proxy",)),
            "worker_activity_by_utc_day": count_by(
                lifecycles, ("observed_day_utc", "outcome")
            ),
            "inherited_rows": sum(bool(row.get("inherited")) for row in lifecycles),
            "inherited_unknown_rows": sum(row.get("inherited") is None for row in lifecycles),
            "in_flight_rows": sum(bool(row.get("in_flight")) for row in lifecycles),
        },
        "limitations": ([
            "No authoritative conductor handoff intervals or personal conductor usage were supplied."
        ] if not handoffs else [
            "Supplied handoff intervals attribute worker lifecycles only; personal conductor usage remains unavailable, and interval gaps or overlaps stay explicit rather than being allocated."
        ]) + [
            "The exact Fable runaway execution IDs were not supplied; no statistical outlier was substituted.",
            "Queue decision rows are observed decisions, not a duration measure; suppressed repeats are unknown.",
            "Queue events do not represent full attempt cost, and missing usage fields remain null rather than zero.",
            "Issue, pull-request, review, check, merge, and deployment timestamps are absent from the local snapshot.",
            "Work mix is a tag/role text proxy and is not a complexity or outcome target.",
            "Parallel and overlapping durations are reported separately and are never summed.",
            "Pre-window lifecycle events are joined only when present in the supplied snapshot; upstream truncation can leave starts and durations unknown and cannot be repaired by this extractor.",
        ],
        "minimum_missing_extraction": ([
            "Authoritative UTC conductor ownership intervals, including temporary/shared ownership, plus measured personal conductor usage by interval."
        ] if not handoffs else [
            "Measured personal conductor usage by supplied ownership interval."
        ]) + [
            "Exact room and execution IDs, with UTC boundaries, for the operator-identified Fable runaway episode.",
            "Window-scoped issue/PR/review/check/merge/deploy timestamps joined by issue, PR, exact head, and deployed revision.",
            "Upstream inventory for rows skipped while the snapshot was created, plus confirmation that the named locked post-cutoff room has no event at or before the cutoff.",
            "Complete per-attempt usage availability/completeness markers and lineage across initial work, repair, and re-review.",
        ],
    }


def md_table(rows: list[dict[str, Any]], columns: list[str], max_rows: int) -> str:
    if not rows:
        return "_No observed rows._"
    shown = rows[:max_rows]
    header = "| " + " | ".join(columns) + " |"
    rule = "| " + " | ".join("---" for _ in columns) + " |"
    lines = [header, rule]
    for row in shown:
        cells = []
        for column in columns:
            value = row.get(column)
            if value is None:
                text = "unknown"
            elif isinstance(value, float):
                text = f"{value:.3f}".rstrip("0").rstrip(".")
            else:
                text = str(value)
            cells.append(text.replace("|", "\\|"))
        lines.append("| " + " | ".join(cells) + " |")
    if len(rows) > max_rows:
        lines.append(f"\n_Bounded display: {max_rows} of {len(rows)} rows._")
    return "\n".join(lines)


def render_report(dataset: dict[str, Any], max_rows: int) -> str:
    window = dataset["window"]
    coverage = dataset["coverage_manifest"]
    exclusions = dataset["exclusion_manifest"]
    attribution = dataset["conductor_attribution"]
    usage_columns = [
        "adapter", "model", "worker_role", "execution_rows",
        "tokensIn_observed_n", "tokensIn_sum", "tokensOut_observed_n", "tokensOut_sum",
        "cacheRead_observed_n", "cacheRead_sum", "cacheCreation_observed_n", "cacheCreation_sum",
        "thinking_observed_n", "thinking_sum", "turns_observed_n", "turns_sum",
    ]
    conductor_usage_columns = [
        "conductor", "conductor_attribution_state", *usage_columns
    ]

    def conductor_view_lines(view: dict[str, Any]) -> list[str]:
        if not attribution["interval_count"]:
            return [
                "",
                "### Conductor-stratified summaries",
                "",
                "_Not produced because no authoritative handoff intervals were supplied._",
            ]
        return [
            "",
            "### Conductor-stratified worker outcomes",
            "",
            md_table(
                view["conductor_outcomes"],
                ["conductor", "conductor_attribution_state", "worker_role", "outcome", "observed_rows"],
                max_rows,
            ),
            "",
            "### Conductor-stratified phase summaries",
            "",
            md_table(
                view["conductor_phase_summaries"],
                ["conductor", "conductor_attribution_state", "worker_role", "phase", "n", "median", "p90_nearest_rank", "max"],
                max_rows,
            ),
            "",
            "### Conductor-stratified worker usage-field coverage",
            "",
            md_table(view["conductor_usage_summaries"], conductor_usage_columns, max_rows),
        ]
    if attribution["interval_count"]:
        comparison_boundary = (
            f"The {attribution['interval_count']} supplied conductor interval(s) enable "
            "conductor-stratified worker outcomes, phases, and usage coverage below. "
            "Overlaps, gaps, and missing lifecycle times remain explicit uncertainty; personal "
            "conductor usage and delivery-system metadata remain unavailable."
        )
        conclusion_scope = (
            "The available snapshot establishes conductor-stratified worker-metadata counts and "
            "phase distributions under the supplied intervals. "
        )
    else:
        comparison_boundary = (
            "A conductor before/after comparison is unavailable: no authoritative conductor "
            "ownership intervals or personal conductor usage were supplied. The views below are "
            "worker-metadata baselines."
        )
        conclusion_scope = (
            "The available snapshot establishes counts and phase distributions for joined worker "
            "metadata only. "
        )
    lines = [
        "# Three-day metadata comparison",
        "",
        "## Coverage manifest",
        "",
        f"Frozen inclusive window: `{window['start_utc']}` through `{window['end_utc']}` ",
        f"(`{window['start_america_new_york']}` through `{window['end_america_new_york']}` America/New_York).",
        "",
        md_table(
            coverage["sources"],
            [
                "file", "bytes", "sha256", "rows_seen", "rows_parsed", "parse_failures",
                "exact_duplicates_removed", "identity_conflicts", "rows_with_valid_timestamp",
                "rows_intersecting_window", "earliest_available_timestamp", "latest_available_timestamp",
                "rows_retained_for_lifecycle_join", "retained_pre_window_rows",
            ],
            max_rows,
        ),
        "",
        "Context-only inputs (fingerprinted for provenance; no prose was copied into the dataset):",
        "",
        md_table(coverage["context_sources"], ["file", "bytes", "sha256", "status"], max_rows),
        "",
        f"COVERAGE.txt declared {coverage['coverage_declaration']['declared_retained_room_events']} retained room events; ",
        f"the parsed delta is `{coverage['declared_vs_parsed_room_event_delta']}`. Other upstream parse failures remain unknown. ",
        "The named locked room began after cutoff according to the supplied declaration, but its pre-cutoff absence was not independently verified.",
        "",
        f"For {coverage['lifecycle_evidence']['intersecting_execution_ids']} interval-overlapping execution IDs "
        f"({coverage['lifecycle_evidence']['interval_only_execution_ids']} established only by lifecycle overlap), "
        f"the extractor retained {coverage['lifecycle_evidence']['retained_pre_window_event_rows']} available pre-window event rows "
        f"and {coverage['lifecycle_evidence']['retained_pre_window_quota_rows']} available pre-window quota rows. It excluded "
        f"{coverage['lifecycle_evidence']['post_window_event_rows_excluded']} post-window event rows and "
        f"{coverage['lifecycle_evidence']['post_window_quota_rows_excluded']} post-window quota rows from calculations. This joins retained evidence only; "
        "events truncated before snapshot creation remain absent and their durations remain unknown.",
        "",
        "GitHub window queries were unavailable under this lane's command grant, so merge, closure, review/check, and deployment coverage is absent.",
        "",
        "## Exclusions manifest",
        "",
        f"Status: **{exclusions['status']}**. Inclusive rows: {exclusions['inclusive_lifecycle_rows']}; ",
        f"primary rows: {exclusions['primary_lifecycle_rows']}. No largest-run or statistical-outlier exclusion was made.",
        "",
        md_table(exclusions["candidate_ambiguity"], ["room", "tag", "executions"], max_rows),
        "",
        "Exact requested exclusions and whether each matched an interval-overlapping lifecycle:",
        "",
        md_table(exclusions["exact_exclusions"], ["execution", "matched_lifecycle"], max_rows),
        "",
        f"Unmatched exclusion IDs: `{exclusions['unmatched_exclusion_ids']}`.",
        "",
        "## Comparison boundary",
        "",
        comparison_boundary,
        "The primary exact-exclusion view and inclusive sensitivity view are not a claim that all three-day metadata or a causal comparison is complete.",
        "",
        "## Conductor attribution",
        "",
        f"Status: **{attribution['status']}**. Uniquely attributed lifecycle rows: "
        f"{attribution['uniquely_attributed_lifecycle_rows']}; uncertain rows: {attribution['uncertain_lifecycle_rows']}.",
        "Lifecycle attribution uses the first available timestamp in this order: execution start, attempt start, queue launch, quota record.",
        "",
        md_table(
            attribution["state_counts"],
            ["conductor_attribution_state", "observed_rows"],
            max_rows,
        ),
        "",
        "## Phase joins",
        "",
        md_table([dataset["join_manifest"]], list(dataset["join_manifest"].keys()), max_rows),
        "",
        f"Observably inherited lifecycle rows: {dataset['available_axes']['inherited_rows']}; ",
        f"inheritance is unknown for {dataset['available_axes']['inherited_unknown_rows']} queue-unjoined rows. ",
        f"In-flight/censored lifecycle rows: {dataset['available_axes']['in_flight_rows']}; their unfinished durations remain `unknown`, never zero.",
        "",
        "## Primary exact-exclusion view",
        "",
        f"This view contains {dataset['primary']['lifecycle_row_count']} lifecycle rows after applying only the exact IDs above. "
        f"Same as inclusive: `{dataset['primary']['same_as_inclusive']}`.",
        "",
        "### Worker outcomes",
        "",
        md_table(dataset["primary"]["outcomes"], ["worker_role", "outcome", "observed_rows"], max_rows),
        "",
        "### Phase summaries",
        "",
        "Queue wait pairs each retained lifecycle-transition decision with its next launch. If a later launch has no transition after the preceding launch, the value is censored; item age is retained separately in lifecycle rows and is not presented as queue wait.",
        "",
        md_table(
            dataset["primary"]["phase_summaries"],
            ["worker_role", "phase", "n", "median", "p90_nearest_rank", "max"],
            max_rows,
        ),
        "",
        "### Worker usage-field coverage",
        "",
        md_table(dataset["primary"]["usage_summaries"], usage_columns, max_rows),
        *conductor_view_lines(dataset["primary"]),
        "",
        "## Inclusive sensitivity view",
        "",
        "### Worker outcomes",
        "",
        md_table(dataset["inclusive"]["outcomes"], ["worker_role", "outcome", "observed_rows"], max_rows),
        "",
        "### Worker activity by UTC day",
        "",
        "These are terminal or quota-record observations, not accepted deliveries or complete attempts.",
        "",
        md_table(
            dataset["available_axes"]["worker_activity_by_utc_day"],
            ["observed_day_utc", "outcome", "observed_rows"],
            max_rows,
        ),
        "",
        "### Phase summaries by worker role",
        "",
        "Each duration is summarized independently. Some phases overlap; rows must not be added across phases or parallel executions.",
        "",
        md_table(
            dataset["inclusive"]["phase_summaries"],
            ["worker_role", "phase", "n", "median", "p90_nearest_rank", "max"],
            max_rows,
        ),
        "",
        "### Worker usage-field coverage",
        "",
        "Values are grouped by vendor/model/worker role and retain each source field's observed row count. ",
        "They are not conductor costs, subscription quota, cross-vendor currency, or full lifecycle-attempt costs.",
        "",
        md_table(
            dataset["inclusive"]["usage_summaries"],
            usage_columns,
            max_rows,
        ),
        *conductor_view_lines(dataset["inclusive"]),
        "",
        "## Scheduling context",
        "",
        "Observed queue decision rows (suppressed repeats and elapsed blocked/empty time are unknown):",
        "",
        md_table(
            dataset["available_axes"]["queue_decisions"],
            ["decision", "reason_category", "observed_rows"],
            max_rows,
        ),
        "",
        "Observed runway decisions:",
        "",
        md_table(
            dataset["available_axes"]["runway_admissions"],
            ["vendor", "decision", "decidedBy", "observed_rows"],
            max_rows,
        ),
        "",
        "Observed decision-row resource summaries:",
        "",
        md_table(
            [
                {"measure": "liveWeight", **dataset["available_axes"]["queue_live_weight"]},
                {"measure": "freeGb", **dataset["available_axes"]["queue_free_gb"]},
            ],
            ["measure", "n", "median", "p90_nearest_rank", "max"],
            max_rows,
        ),
        "",
        "## Work-mix proxy",
        "",
        "This ordered tag/role proxy is descriptive only; policy, model, complexity, inherited work, and work mix remain confounded.",
        "",
        md_table(
            dataset["available_axes"]["work_mix_proxy"],
            ["work_mix_proxy", "observed_rows"],
            max_rows,
        ),
        "",
        "## What can and cannot be concluded",
        "",
        conclusion_scope,
        "It does not establish whether delivery slowed after a conductor handoff, accepted-delivery throughput, first-pass acceptance, reopen/regression rates, ",
        "complete retry cost, conductor action delay, CI critical path, or merge-to-deploy delay. Small, dependent cohorts and missing attribution do not support a fitted causal model.",
        "",
        "## Ranked next actions",
        "",
    ]
    for index, item in enumerate(dataset["minimum_missing_extraction"], 1):
        lines.append(f"{index}. {item}")
    lines.extend(["", "## Limitations", ""])
    for item in dataset["limitations"]:
        lines.append(f"- {item}")
    return "\n".join(lines) + "\n"


def selftest() -> None:
    start = parse_time("2026-09-07T20:37:13Z")
    end = parse_time("2026-09-10T20:37:13Z")
    assert start is not None and end is not None
    window = Window(start, end)

    # Polarity, exact-duration, and inclusive-boundary controls.
    assert window.contains(start) and window.contains(end)
    validate_window_duration(start, end, "synthetic")
    for invalid_end in (end - timedelta(hours=1), end + timedelta(hours=1)):
        try:
            validate_window_duration(start, invalid_end, "synthetic")
        except ValueError:
            pass
        else:
            raise AssertionError("a non-72-hour window was accepted")
    assert america_new_york(start).utcoffset() == timedelta(hours=-4)
    january = parse_time("2026-01-10T12:00:00Z")
    assert january is not None and america_new_york(january).utcoffset() == timedelta(hours=-5)
    failed_events = [{"type": "executionFailed", "at": "2026-09-08T00:00:03Z"}]
    assert event_outcome(failed_events, None) == "failed"
    arrested_events = [{"type": "executionArrested", "at": "2026-09-08T00:00:04Z"}]
    assert event_outcome(arrested_events, None) == "arrested"
    quota_polarities = {
        "Succeeded": "succeeded",
        "FinishedDuringTeardown": "succeeded",
        "Failed": "failed",
        "Retryable": "failed",
        "Permanent": "failed",
        "ExhaustedUntil": "failed",
        "ToolDenied": "failed",
        "Cancelled": "cancelled",
        "Indeterminate": "indeterminate",
        "Arrested": "arrested",
    }
    for producer_value, expected in quota_polarities.items():
        assert event_outcome([], {"outcome": producer_value}) == expected

    # Invalid UTF-8 is a counted parse failure, never a replacement-character identity.
    parsed, parse_counts = parse_jsonl_payloads([
        b'{"execution":"good"}\n',
        b'{"execution":"bad-\xff"}\n',
    ])
    assert parsed == [{"execution": "good"}]
    assert parse_counts["rows_seen"] == 2 and parse_counts["parse_failures"] == 1

    # Exact duplicates collapse, but a same-identity conflicting observation remains visible.
    first = {"execution": "e1", "at": "2026-09-08T00:00:00Z", "tokensIn": 1}
    conflict = {"execution": "e1", "at": "2026-09-08T00:00:00Z", "tokensIn": 2}
    deduped, removed = exact_dedupe([first, dict(first), conflict])
    assert removed == 1 and len(deduped) == 2
    assert identity_conflicts("quota_ledger", deduped) == 1

    # Censoring and inherited work stay visible, with no zero-duration fabrication.
    queue = [{
        "Tag": "synthetic",
        "Role": "implement",
        "RoomDirectory": "C:/fixture/room-a",
        "AddedAt": "2026-09-07T20:00:00Z",
        "LaunchedAt": "2026-09-07T20:37:13Z",
        "State": "Running",
    }]
    events = [{
        "room": "room-a",
        "ExecutionId": "e1",
        "type": "executionStarted",
        "at": "2026-09-07T20:37:13Z",
    }]
    lifecycles, _joins = build_lifecycles(queue, [], events, [], window, [])
    assert len(lifecycles) == 1
    assert lifecycles[0]["inherited"] is True
    assert lifecycles[0]["in_flight"] is True
    assert lifecycles[0]["lane_service_seconds"] is None
    assert lifecycles[0]["usage"]["tokensIn"] is None

    quota_only, _joins = build_lifecycles(
        [],
        [],
        [],
        [{
            "room": "C:/fixture/room-b",
            "execution": "e2",
            "at": "2026-09-08T01:00:00Z",
            "outcome": "Succeeded",
            "tokensIn": 3,
        }],
        window,
        [],
    )
    assert len(quota_only) == 1 and quota_only[0]["outcome"] == "succeeded"
    assert quota_only[0]["usage"]["tokensIn"] == 3
    quota_controls, _joins = build_lifecycles(
        [],
        [],
        [],
        [
            {
                "room": f"quota-{index}", "execution": f"quota-{index}",
                "at": "2026-09-08T01:00:00Z", "outcome": producer_value,
            }
            for index, producer_value in enumerate(quota_polarities)
        ],
        window,
        [],
    )
    quota_control_outcomes = {row["execution"]: row["outcome"] for row in quota_controls}
    for index, expected in enumerate(quota_polarities.values()):
        assert quota_control_outcomes[f"quota-{index}"] == expected
    quota_unknown, _joins = build_lifecycles(
        [],
        [],
        [],
        [{"room": "room-c", "execution": "e3", "at": "2026-09-08T01:00:00Z"}],
        window,
        [],
    )
    assert quota_unknown[0]["outcome"] == "unknown"
    assert quota_unknown[0]["inherited"] is None

    # Each advance pairs only with its next launch. The current queue Round cannot
    # leak backward and censor a supported initial wait, and a missing later
    # transition cannot reuse the preceding round's transition.
    round_queue = [{
        "Tag": "synthetic-rounds",
        "Role": "review",
        "RoomDirectory": "C:/fixture/room-round-3",
        "AddedAt": "2026-09-09T00:59:46.067Z",
        "LaunchedAt": "2026-09-09T03:00:00Z",
        "Round": 3,
    }]
    round_decisions = [
        {
            "at": "2026-09-09T01:00:00Z", "tag": "synthetic-rounds",
            "decision": "launched", "room": "room-round-1",
        },
        {
            "at": "2026-09-09T01:30:00Z", "tag": "synthetic-rounds",
            "decision": "advanced", "room": "room-round-1",
        },
        {
            "at": "2026-09-09T01:35:00Z", "tag": "synthetic-rounds",
            "decision": "launched", "room": "room-round-2",
        },
        {
            "at": "2026-09-09T03:00:00Z", "tag": "synthetic-rounds",
            "decision": "launched", "room": "room-round-3",
        },
    ]
    round_events = [
        {
            "room": f"room-round-{index}", "ExecutionId": f"round-{index}",
            "type": "executionStarted", "at": stamp,
        }
        for index, stamp in (
            (1, "2026-09-09T01:00:01Z"),
            (2, "2026-09-09T01:35:01Z"),
            (3, "2026-09-09T03:00:01Z"),
        )
    ]
    rounds, _joins = build_lifecycles(round_queue, round_decisions, round_events, [], window, [])
    rounds_by_execution = {row["execution"]: row for row in rounds}
    assert rounds_by_execution["round-1"]["queue_wait_seconds"] == 13.933
    assert rounds_by_execution["round-1"]["queue_wait_basis"] == "item_added_to_initial_launch"
    assert rounds_by_execution["round-1"]["observed_launch_sequence"] == 1
    assert rounds_by_execution["round-2"]["queue_wait_seconds"] == 300
    assert rounds_by_execution["round-2"]["queue_wait_basis"] == "round_transition_to_launch"
    assert rounds_by_execution["round-3"]["queue_wait_seconds"] is None
    assert rounds_by_execution["round-3"]["queue_wait_basis"] == "unavailable_missing_round_transition"

    # Selection is by interval overlap, not by requiring a row inside the window.
    # Available pre-window event/quota evidence is retained through cutoff, while
    # post-window evidence is used only to establish overlap.
    crossing_events = [
        {
            "room": "crossing", "ExecutionId": "crossing-1", "type": "executionStarted",
            "at": "2026-09-07T20:30:00Z",
        },
        {
            "room": "crossing", "ExecutionId": "crossing-1", "type": "executionExited",
            "at": "2026-09-07T20:40:00Z",
        },
        {
            "room": "crossing", "ExecutionId": "crossing-1", "type": "executionSucceeded",
            "at": "2026-09-07T20:40:01Z",
        },
        {
            "room": "crossing", "ExecutionId": "crossing-1", "type": "executionProgress",
            "at": "2026-09-10T20:37:14Z",
        },
        {
            "room": "spanning", "ExecutionId": "spanning-1", "type": "executionStarted",
            "at": "2026-09-07T20:30:00Z",
        },
        {
            "room": "spanning", "ExecutionId": "spanning-1", "type": "executionExited",
            "at": "2026-09-10T20:40:00Z",
        },
        {
            "room": "before", "ExecutionId": "before-1", "type": "executionStarted",
            "at": "2026-09-07T20:00:00Z",
        },
        {
            "room": "before", "ExecutionId": "before-1", "type": "executionExited",
            "at": "2026-09-07T20:10:00Z",
        },
    ]
    crossing_quotas = [{
        "room": "crossing", "execution": "crossing-1", "at": "2026-09-07T20:35:00Z",
        "outcome": "Succeeded", "tokensIn": 7,
    }]
    joined, joined_quotas, evidence = select_lifecycle_evidence(
        crossing_events, crossing_quotas, window
    )
    assert {row["ExecutionId"] for row in joined} == {"crossing-1", "spanning-1"}
    assert len(joined_quotas) == 1 and joined_quotas[0]["tokensIn"] == 7
    assert evidence["interval_only_execution_ids"] == 1
    assert evidence["retained_pre_window_event_rows"] == 2
    assert evidence["retained_pre_window_quota_rows"] == 1
    assert evidence["post_window_event_rows_excluded"] == 2
    crossing, _joins = build_lifecycles([], [], joined, joined_quotas, window, [])
    crossing_by_execution = {row["execution"]: row for row in crossing}
    assert crossing_by_execution["crossing-1"]["outcome"] == "succeeded"
    assert crossing_by_execution["crossing-1"]["lane_service_seconds"] == 600
    assert crossing_by_execution["crossing-1"]["usage"]["tokensIn"] == 7
    assert crossing_by_execution["spanning-1"]["in_flight"] is True

    # Supplied intervals produce conductor-stratified summaries. Every
    # attribution state is explicit, including overlaps, gaps, and missing time.
    handoffs = [
        {"conductor": "before", "start": "2026-09-07T20:00:00Z", "end": "2026-09-09T02:00:00Z"},
        {"conductor": "after", "start": "2026-09-09T00:00:00Z", "end": None},
    ]
    validate_handoffs(handoffs)
    attribution_controls = {
        attribute_conductor(parse_time("2026-09-08T01:00:00Z"), handoffs)["state"],
        attribute_conductor(parse_time("2026-09-09T01:00:00Z"), handoffs)["state"],
        attribute_conductor(parse_time("2026-09-07T19:00:00Z"), handoffs)["state"],
        attribute_conductor(None, handoffs)["state"],
        attribute_conductor(parse_time("2026-09-08T01:00:00Z"), [])["state"],
    }
    assert attribution_controls == set(CONDUCTOR_ATTRIBUTION_STATES)
    attributed, _joins = build_lifecycles(
        [], [], [],
        [
            {
                "room": "before-room", "execution": "before-execution",
                "at": "2026-09-08T01:00:00Z", "outcome": "Succeeded", "tokensIn": 3,
            },
            {
                "room": "shared-room", "execution": "shared-execution",
                "at": "2026-09-09T01:00:00Z", "outcome": "Failed", "tokensIn": 5,
            },
            {
                "room": "after-room", "execution": "after-execution",
                "at": "2026-09-09T03:00:00Z", "outcome": "Succeeded", "tokensIn": 7,
            },
        ],
        window, handoffs,
    )
    attributed_view = analytical_view(attributed)
    assert {row["conductor"] for row in attributed_view["conductor_outcomes"]} == {
        "before", "after", None,
    }
    assert any(
        row["conductor_attribution_state"] == "ambiguous_overlapping_intervals"
        for row in attributed_view["conductor_phase_summaries"]
    )
    assert {row["conductor"] for row in attributed_view["conductor_usage_summaries"]} == {
        "before", "after", None,
    }

    # The declared first-match work-mix rule applies even when Role is not review.
    assert classify_work({"Role": "advise", "Reason": "rereview the evidence"}) == "review"
    assert queue_reason_category({"decision": "waited", "reason": "private novel text"}) == "other"

    # The report exposes primary and inclusive summaries as separate views, plus
    # the exact unmatched exclusion evidence.
    report_fixture = {
        "window": {
            "start_utc": iso(start), "end_utc": iso(end),
            "start_america_new_york": america_new_york(start).isoformat(),
            "end_america_new_york": america_new_york(end).isoformat(),
        },
        "coverage_manifest": {
            "sources": [], "context_sources": [], "declared_vs_parsed_room_event_delta": 0,
            "coverage_declaration": {"declared_retained_room_events": 0},
            "lifecycle_evidence": evidence,
        },
        "exclusion_manifest": {
            "status": "requested_ids_unmatched_no_exclusion_applied", "inclusive_lifecycle_rows": 1,
            "primary_lifecycle_rows": 1, "candidate_ambiguity": [],
            "exact_exclusions": [{"execution": "missing-id", "matched_lifecycle": False}],
            "unmatched_exclusion_ids": ["missing-id"],
        },
        "conductor_attribution": {
            "status": "provided_with_attribution_uncertainty", "interval_count": 2,
            "uniquely_attributed_lifecycle_rows": 2, "uncertain_lifecycle_rows": 1,
            "state_counts": [
                {"conductor_attribution_state": "attributed_unique", "observed_rows": 2},
                {"conductor_attribution_state": "ambiguous_overlapping_intervals", "observed_rows": 1},
            ],
        },
        "join_manifest": {},
        "primary": {
            "lifecycle_row_count": 1, "same_as_inclusive": True,
            "outcomes": [], "phase_summaries": [], "usage_summaries": [],
            "conductor_outcomes": [], "conductor_phase_summaries": [],
            "conductor_usage_summaries": [],
        },
        "inclusive": {
            "outcomes": [], "phase_summaries": [], "usage_summaries": [],
            "conductor_outcomes": [], "conductor_phase_summaries": [],
            "conductor_usage_summaries": [],
        },
        "available_axes": {
            "inherited_rows": 0, "inherited_unknown_rows": 1, "in_flight_rows": 0,
            "worker_activity_by_utc_day": [], "queue_decisions": [], "runway_admissions": [],
            "queue_live_weight": numeric_summary([]), "queue_free_gb": numeric_summary([]),
            "work_mix_proxy": [],
        },
        "minimum_missing_extraction": [],
        "limitations": [],
    }
    report = render_report(report_fixture, 20)
    assert report.index("## Primary exact-exclusion view") < report.index("## Inclusive sensitivity view")
    assert "missing-id" in report and "Unmatched exclusion IDs" in report
    assert "enable conductor-stratified worker outcomes" in report
    assert "Same as inclusive: `True`" in report
    primary_missing, matched, unmatched, matched_ids, status = apply_exclusions(
        {"missing-id"}, crossing
    )
    assert primary_missing == crossing and matched_ids == set()
    assert status == "requested_ids_unmatched_no_exclusion_applied"
    assert matched == [
        {"execution": "missing-id", "matched_lifecycle": False},
    ]
    assert unmatched == ["missing-id"]
    _primary_matched, matched, unmatched, matched_ids, status = apply_exclusions(
        {"crossing-1", "missing-id"}, crossing
    )
    assert matched == [
        {"execution": "crossing-1", "matched_lifecycle": True},
        {"execution": "missing-id", "matched_lifecycle": False},
    ]
    assert unmatched == ["missing-id"] and matched_ids == {"crossing-1"}
    assert status == "applied_matched_exact_ids_with_unmatched_requests"

    print("three-day-metadata selftest: PASS (measurement-integrity controls)")


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input_dir", nargs="?", type=Path, help="directory containing COVERAGE.txt and snapshot files")
    parser.add_argument("--format", choices=("dataset", "report"), default="report")
    parser.add_argument("--handoffs", type=Path, help="optional JSON ownership intervals")
    parser.add_argument("--exclusions", type=Path, help="optional JSON exact execution exclusions")
    parser.add_argument("--max-rows", type=int, default=20, help="maximum rows per Markdown table")
    parser.add_argument("--selftest", action="store_true")
    args = parser.parse_args(argv)
    if not args.selftest and args.input_dir is None:
        parser.error("input_dir is required unless --selftest is used")
    if args.max_rows < 1:
        parser.error("--max-rows must be positive")
    return args


def main(argv: list[str] | None = None) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", newline="\n")
    args = parse_args(sys.argv[1:] if argv is None else argv)
    if args.selftest:
        selftest()
        return 0
    try:
        dataset = build_dataset(args.input_dir, args.handoffs, args.exclusions)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    if args.format == "dataset":
        json.dump(dataset, sys.stdout, separators=(",", ":"), ensure_ascii=True)
        print()
    else:
        sys.stdout.write(render_report(dataset, args.max_rows))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
