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
    "failed": "failed",
    "cancelled": "cancelled",
    "indeterminate": "indeterminate",
}

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


def load_jsonl(path: Path) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    records: list[dict[str, Any]] = []
    parse_failures = 0
    non_object_rows = 0
    line_count = 0
    with path.open(encoding="utf-8", errors="replace") as handle:
        for line in handle:
            line_count += 1
            try:
                value = json.loads(line)
            except (json.JSONDecodeError, UnicodeError):
                parse_failures += 1
                continue
            if not isinstance(value, dict):
                non_object_rows += 1
                continue
            records.append(value)
    records, exact_duplicates = exact_dedupe(records)
    audit = file_fingerprint(path)
    audit.update(
        {
            "rows_seen": line_count,
            "rows_parsed": len(records),
            "parse_failures": parse_failures,
            "non_object_rows": non_object_rows,
            "exact_duplicates_removed": exact_duplicates,
        }
    )
    return records, audit


def load_queue(path: Path) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    parse_failures = 0
    non_object_rows = 0
    try:
        value = json.loads(path.read_text(encoding="utf-8", errors="replace"))
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


def load_coverage(path: Path) -> tuple[Window, dict[str, Any]]:
    text = path.read_text(encoding="utf-8", errors="replace")
    match = re.search(r"Snapshot window:\s*(\S+)\s+through\s*(\S+)\.", text)
    if match is None:
        raise ValueError(f"{path}: missing exact snapshot window")
    start = parse_time(match.group(1))
    end = parse_time(match.group(2))
    if start is None or end is None or start > end:
        raise ValueError(f"{path}: invalid snapshot window")
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
    role = str(item.get("Role") or "").lower()
    text = " ".join(str(item.get(field) or "") for field in ("Tag", "Reason", "Role")).lower()
    if role == "review":
        return "review"
    for label, needles in WORK_MIX_RULES[1:]:
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


def assign_conductor(started: datetime | None, handoffs: list[dict[str, Any]]) -> str | None:
    if started is None:
        return None
    matches = []
    for handoff in handoffs:
        begin = parse_time(handoff.get("start"))
        end = parse_time(handoff.get("end"))
        if begin is not None and begin <= started and (end is None or started <= end):
            matches.append(str(handoff.get("conductor") or "unknown"))
    return matches[0] if len(matches) == 1 else None


def build_lifecycles(
    queue_rows: list[dict[str, Any]],
    event_rows: list[dict[str, Any]],
    quota_rows: list[dict[str, Any]],
    window: Window,
    handoffs: list[dict[str, Any]],
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    queue_by_room: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for item in queue_rows:
        if key := room_key(item.get("RoomDirectory")):
            queue_by_room[key].append(item)

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
        added = parse_time(item.get("AddedAt"))
        launched = parse_time(item.get("LaunchedAt"))
        outcome = event_outcome(events, quota)
        usage = {field: (quota or {}).get(field) for field in USAGE_FIELDS}
        present_usage = sum(value is not None for value in usage.values())
        if present_usage == 0:
            usage_status = "unavailable"
        elif present_usage == len(USAGE_FIELDS):
            usage_status = "complete_fields"
        else:
            usage_status = "partial_fields"
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
                "inherited": None if added is None else added < window.start,
                "in_flight": outcome == "in_flight",
                "outcome": outcome,
                "conductor": assign_conductor(started, handoffs),
                "added_at": iso(added),
                "launched_at": iso(launched),
                "attempt_started_at": iso(attempted),
                "execution_started_at": iso(started),
                "execution_exited_at": iso(exited),
                "terminal_at": iso(terminal),
                "quota_recorded_at": iso(quota_recorded),
                "observed_day_utc": iso(terminal or quota_recorded or exited or started)[:10]
                if (terminal or quota_recorded or exited or started)
                else None,
                "queue_wait_seconds": seconds_between(added, launched),
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


def phase_summaries(lifecycles: list[dict[str, Any]]) -> list[dict[str, Any]]:
    result = []
    roles = {str(row.get("worker_role") or "unknown") for row in lifecycles}
    for role in sorted(roles, key=lambda value: (ROLE_DISPLAY_ORDER.get(value, 99), value)):
        selected = [row for row in lifecycles if str(row.get("worker_role") or "unknown") == role]
        for field in (
            "queue_wait_seconds",
            "launch_to_start_seconds",
            "lane_service_seconds",
            "verification_seconds",
            "execution_to_terminal_seconds",
        ):
            result.append({"worker_role": role, "phase": field, **numeric_summary(row.get(field) for row in selected)})
    return result


def usage_summaries(lifecycles: list[dict[str, Any]]) -> list[dict[str, Any]]:
    groups: dict[tuple[str, str, str], list[dict[str, Any]]] = defaultdict(list)
    for row in lifecycles:
        key = (
            str(row.get("adapter") or "unknown"),
            str(row.get("model") or "unknown"),
            str(row.get("worker_role") or "unknown"),
        )
        groups[key].append(row)
    result = []
    for (adapter, model, role), rows in sorted(groups.items()):
        item: dict[str, Any] = {
            "adapter": adapter,
            "model": model,
            "worker_role": role,
            "execution_rows": len(rows),
        }
        for field in USAGE_FIELDS:
            values = [row["usage"].get(field) for row in rows if row["usage"].get(field) is not None]
            item[f"{field}_observed_n"] = len(values)
            item[f"{field}_sum"] = sum(values) if values else None
        result.append(item)
    return result


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
        audit["identity_conflicts"] = identity_conflicts(source, rows)
        manifests.append(audit)
        tables[source] = rows

    handoffs = load_optional_rows(handoff_path, "handoffs")
    exclusions = load_optional_rows(exclusion_path, "exclusions")
    events = [row for row in tables["room_events"] if in_window(row, "room_events", window)]
    quotas = [row for row in tables["quota_ledger"] if in_window(row, "quota_ledger", window)]
    lifecycles, joins = build_lifecycles(tables["queue"], events, quotas, window, handoffs)
    exclusion_ids = load_exclusion_ids(exclusions)
    primary = [row for row in lifecycles if row["execution"] not in exclusion_ids]
    candidates = fable_candidates(tables["queue"], lifecycles)

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
        "schema_version": 1,
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
            "locked_room_after_cutoff_verified": False,
            "github_window_queries": "unavailable",
        },
        "exclusion_manifest": {
            "requested_episode": "Fable runaway",
            "status": "applied_exact_ids" if exclusion_ids else "unresolved_no_exclusion_applied",
            "primary_excluded_execution_ids": sorted(exclusion_ids),
            "candidate_ambiguity": candidates,
            "inclusive_lifecycle_rows": len(lifecycles),
            "primary_lifecycle_rows": len(primary),
        },
        "conductor_attribution": {
            "status": "provided" if handoffs else "unavailable_no_authoritative_intervals",
            "interval_count": len(handoffs),
            "attributed_lifecycle_rows": sum(row.get("conductor") is not None for row in lifecycles),
        },
        "join_manifest": joins,
        "inclusive": {
            "lifecycle_rows": lifecycles,
            "outcomes": count_by(lifecycles, ("worker_role", "outcome")),
            "phase_summaries": phase_summaries(lifecycles),
            "usage_summaries": usage_summaries(lifecycles),
        },
        "primary": {
            "same_as_inclusive": not exclusion_ids,
            "lifecycle_row_count": len(primary),
            "outcomes": None if not exclusion_ids else count_by(primary, ("worker_role", "outcome")),
            "phase_summaries": None if not exclusion_ids else phase_summaries(primary),
            "usage_summaries": None if not exclusion_ids else usage_summaries(primary),
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
        "limitations": [
            "No authoritative conductor handoff intervals or personal conductor usage were supplied.",
            "The exact Fable runaway execution IDs were not supplied; no statistical outlier was substituted.",
            "Queue decision rows are observed decisions, not a duration measure; suppressed repeats are unknown.",
            "Queue events do not represent full attempt cost, and missing usage fields remain null rather than zero.",
            "Issue, pull-request, review, check, merge, and deployment timestamps are absent from the local snapshot.",
            "Work mix is a tag/role text proxy and is not a complexity or outcome target.",
            "Parallel and overlapping durations are reported separately and are never summed.",
        ],
        "minimum_missing_extraction": [
            "Authoritative UTC conductor ownership intervals, including temporary/shared ownership, plus measured personal conductor usage by interval.",
            "Exact room and execution IDs, with UTC boundaries, for the operator-identified Fable runaway episode.",
            "Window-scoped issue/PR/review/check/merge/deploy timestamps joined by issue, PR, exact head, and deployed revision.",
            "Extractor inventory for the skipped parse failures and confirmation that the named locked post-cutoff room has no event at or before the cutoff.",
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
                "rows_intersecting_window",
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
        "GitHub window queries were unavailable under this lane's command grant, so merge, closure, review/check, and deployment coverage is absent.",
        "",
        "## Exclusions manifest",
        "",
        f"Status: **{exclusions['status']}**. Inclusive rows: {exclusions['inclusive_lifecycle_rows']}; ",
        f"primary rows: {exclusions['primary_lifecycle_rows']}. No largest-run or statistical-outlier exclusion was made.",
        "",
        md_table(exclusions["candidate_ambiguity"], ["room", "tag", "executions"], max_rows),
        "",
        "## Comparison boundary",
        "",
        "A conductor before/after comparison is unavailable: the snapshot has no authoritative conductor ownership intervals and no personal conductor usage. ",
        "The tables below are an inclusive worker-metadata baseline, not a claim that all three-day metadata or a causal comparison is complete.",
        "",
        "## Phase joins",
        "",
        md_table([dataset["join_manifest"]], list(dataset["join_manifest"].keys()), max_rows),
        "",
        f"Observably inherited lifecycle rows: {dataset['available_axes']['inherited_rows']}; ",
        f"inheritance is unknown for {dataset['available_axes']['inherited_unknown_rows']} queue-unjoined rows. ",
        f"In-flight/censored lifecycle rows: {dataset['available_axes']['in_flight_rows']}; their unfinished durations remain `unknown`, never zero.",
        "",
        "## Worker outcomes (inclusive sensitivity view)",
        "",
        md_table(dataset["inclusive"]["outcomes"], ["worker_role", "outcome", "observed_rows"], max_rows),
        "",
        "## Worker activity by UTC day",
        "",
        "These are terminal or quota-record observations, not accepted deliveries or complete attempts.",
        "",
        md_table(
            dataset["available_axes"]["worker_activity_by_utc_day"],
            ["observed_day_utc", "outcome", "observed_rows"],
            max_rows,
        ),
        "",
        "## Non-overlapping phase summaries by worker role",
        "",
        "Each duration is summarized independently. Rows must not be added across phases or parallel executions.",
        "",
        md_table(
            dataset["inclusive"]["phase_summaries"],
            ["worker_role", "phase", "n", "median", "p90_nearest_rank", "max"],
            max_rows,
        ),
        "",
        "## Worker usage-field coverage",
        "",
        "Values are grouped by vendor/model/worker role and retain each source field's observed row count. ",
        "They are not conductor costs, subscription quota, cross-vendor currency, or full lifecycle-attempt costs.",
        "",
        md_table(
            dataset["inclusive"]["usage_summaries"],
            [
                "adapter", "model", "worker_role", "execution_rows",
                "tokensIn_observed_n", "tokensIn_sum", "tokensOut_observed_n", "tokensOut_sum",
                "cacheRead_observed_n", "cacheRead_sum", "cacheCreation_observed_n", "cacheCreation_sum",
                "thinking_observed_n", "thinking_sum", "turns_observed_n", "turns_sum",
            ],
            max_rows,
        ),
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
        "The available snapshot establishes counts and phase distributions for joined worker metadata only. ",
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

    # Polarity and inclusive-boundary check.
    assert window.contains(start) and window.contains(end)
    assert america_new_york(start).utcoffset() == timedelta(hours=-4)
    january = parse_time("2026-01-10T12:00:00Z")
    assert january is not None and america_new_york(january).utcoffset() == timedelta(hours=-5)
    failed_events = [{"type": "executionFailed", "at": "2026-09-08T00:00:03Z"}]
    assert event_outcome(failed_events, None) == "failed"
    arrested_events = [{"type": "executionArrested", "at": "2026-09-08T00:00:04Z"}]
    assert event_outcome(arrested_events, None) == "arrested"
    assert event_outcome([], {"outcome": "Succeeded"}) == "succeeded"

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
    lifecycles, _joins = build_lifecycles(queue, events, [], window, [])
    assert len(lifecycles) == 1
    assert lifecycles[0]["inherited"] is True
    assert lifecycles[0]["in_flight"] is True
    assert lifecycles[0]["lane_service_seconds"] is None
    assert lifecycles[0]["usage"]["tokensIn"] is None

    quota_only, _joins = build_lifecycles(
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
    quota_unknown, _joins = build_lifecycles(
        [],
        [],
        [{"room": "room-c", "execution": "e3", "at": "2026-09-08T01:00:00Z"}],
        window,
        [],
    )
    assert quota_unknown[0]["outcome"] == "unknown"
    assert quota_unknown[0]["inherited"] is None
    assert queue_reason_category({"decision": "waited", "reason": "private novel text"}) == "other"

    print("three-day-metadata selftest: PASS (polarity, dedup/conflict, inclusive boundary, censoring)")


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
