#!/usr/bin/env python3
"""#1691: measure billed-token RATE across the real room corpus in ~/.baton/rooms.

Why this exists
---------------
#1691 proposed arresting a runaway lane on billed tokens per unit time rather than on a total, on the
strength of three rooms. This script is the instrument that answers whether any such threshold
actually separates a runaway from normal traffic, over EVERY room on the machine rather than three.
Its answer, as of the sweep recorded in spec/baton.md SS3, is no. Re-run it rather than trusting that
paragraph: the corpus grows, and the numbers move with it.

    python tools/room-rate-sweep/sweep.py --sweep
    python tools/room-rate-sweep/sweep.py --sweep --window 2 --offsets duration
    python tools/room-rate-sweep/sweep.py --emit-fixture tests/Baton.Tests/Fixtures/billed-rate-rooms.json
    python tools/room-rate-sweep/sweep.py --selftest

#2034 added a second mode to the same instrument, because it asks about the same corpus and the same
accounting: `--budget-headroom` prints, per adapter and role, how the lanes that actually ran compare
to the `token_budget` ceiling in `src/Baton.Vendors/WorkerRoles.json` that would have arrested them.

    python tools/room-rate-sweep/sweep.py --budget-headroom
    python tools/room-rate-sweep/sweep.py --budget-headroom --since 2026-09-07T09:55:46Z

It reads each room's settled `terminal.json` usage rather than its `.stdout.log`, so the two modes do
not share the reconstruction caveats below. Its comparison is against `liveBilledTokens` -- the Σ
`Baton.Mutation.TokenBudgetMonitor` accumulated while the lane ran, which is the only quantity an
arrest is ever made on -- with `billedTokens` (the post-hoc fold over the whole stream) beside it,
because the ratio between them is what makes one number mean different things on different vendors.

What it measures, and what it assumes
-------------------------------------
Billed tokens are #1682's accounting as corrected by #1706, and the correction is VENDOR-ASYMMETRIC:

  * agy   -> input + output per usage line. A MEASUREMENT: agy's per-turn `step_update` usage is real,
             and its terminal `result.usage` is the exact cumulative sum of those lines (measured over
             three real rooms, docs/vendor-capabilities.md).
  * claude -> cache_creation ALONE. A FLOOR: the `input_tokens`/`output_tokens` on a mid-stream
             `assistant` line are placeholder values, not that message's real figures (measured,
             docs/vendor-capabilities.md), so summing them bills two columns that mean nothing. Every
             claude total this tool prints is therefore a LOWER BOUND on the room's real spend -- the
             live-seen fraction ran 0.28-0.91 across the 126-room sweep -- and the two vendors' totals
             are NOT the same quantity. Rows are marked accordingly rather than printed in one column
             as though they were comparable.

cache_read is excluded on both -- spec/baton.md SS3 has the reason. Deduped by message.id on claude
(agy lines carry no id). The rule is shared with `TokenBudgetMonitor` and `tools/fleet-glass/pusher.py`;
`tests/Baton.Tests/Fixtures/claude-billing-gate.json` is the cross-language gate that keeps the three
from drifting, and `--selftest` reads it here.

The per-line SAMPLES this tool emits stay raw (the stream's own four columns), because a fixture is a
record of what the vendor said; it is the BILLING over them that applies the rule above, in one place
(`sample_billed`), so no consumer has to know the vendor to bill correctly.

Time is the problem, and spec/baton.md SS3 states the vendor asymmetry behind it (as well as correcting
an earlier revision of this file, which claimed agy carries no time field at all -- it carries
`duration_seconds`, per-step elapsed rather than wall-clock). Per-line offsets come from one of three
places:

  * claude rooms          -> MEASURED, the line's own `timestamp`.
  * agy, `--offsets uniform` (default)
                          -> RECONSTRUCTED by spreading the room's usage lines uniformly across its
                             measured executionStarted..executionExited span from flow.jsonl.
  * agy, `--offsets duration`
                          -> RECONSTRUCTED by the running cumulative sum of every step's
                             `duration_seconds`, rescaled to the measured span. Closer to the truth on a
                             room whose steps run strictly back to back (over `38c24d11` the raw sum is
                             686.4s against a 698.9s measured span, a 98% match), and WRONG in a
                             different direction on a room with overlapping/backgrounded steps, where
                             the raw sum can exceed the span severalfold -- which is why it rescales
                             rather than being used raw, and why it is offered as a CROSS-CHECK on the
                             uniform reconstruction rather than as a replacement for it.

Neither agy reconstruction is authoritative. What makes the #1691 conclusion safe is that it does not
rest on either: `--sweep`'s billed-per-minute column is total / measured span, exact on both vendors
with no reconstruction at all, and the separation question is answered there.

Corrected 2026-09-02 (#1707 review F10): the width sweep behind the "no width reverses the ordering"
claim in spec/baton.md SS3 was run by hand at W in {1, 2, 3, 5, 8, 10} minutes -- `--window` takes one
value per invocation, there is no built-in multi-width loop, and the range was previously stated two
different ways with neither recorded anywhere re-runnable. This docstring is now that single record;
spec/baton.md SS3 no longer restates a range of its own -- its refutation argument was replaced by a
reconstruction-free pigeonhole bound that does not depend on sweeping widths at all.
"""

import argparse
import glob
import json
import os
import sys
from datetime import datetime, timedelta

ROOMS = os.path.join(os.path.expanduser("~"), ".baton", "rooms")

# The window --billed-rate-limit is stated in. Mirrors Baton.Mutation.TokenBudgetMonitor's own
# BilledRateWindow; that C# constant is the one the engine enforces, this is the analysis copy.
WINDOW = timedelta(minutes=5)


def _parse_time(raw):
    return datetime.fromisoformat(raw.replace("Z", "+00:00"))


def room_span(room_dir):
    """(started, exited, exit_reason, cancelled, produced_work, executions) from the room's flow.jsonl.

    `produced_work` is the one that matters, and the one an earlier revision of this script got wrong
    (#1707 review) -- spec/baton.md SS3 states why the weaker exit-reason test is not enough. Here it is
    True only for a room journalling at least one `executionSucceeded` and no `executionFailed`.
    """
    path = os.path.join(room_dir, "flow.jsonl")
    if not os.path.exists(path):
        return None
    started = exited = reason = None
    cancelled = False
    succeeded = failed = executions = 0
    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            try:
                record = json.loads(line)
            except ValueError:
                continue
            event = record.get("Event") or {}
            kind = event.get("eventType")
            if kind == "executionStarted":
                executions += 1
                if started is None:
                    started = _parse_time(record["WriterUtcTimestamp"])
            elif kind == "executionExited":
                exited = _parse_time(record["WriterUtcTimestamp"])
                reason = event.get("Reason")
            elif kind in ("cancellationRequested", "executionCancelled"):
                cancelled = True
            elif kind == "executionSucceeded":
                succeeded += 1
            elif kind == "executionFailed":
                failed += 1
    if started is None or exited is None:
        return None
    return started, exited, reason, cancelled, succeeded > 0 and failed == 0, executions


def billed_samples(stdout_log, span, offsets="uniform"):
    """(vendor, [(offset_seconds, input, output, cache_creation), ...]) using #1682's accounting.

    `offsets` selects the agy reconstruction ('uniform' or 'duration'); it is ignored on claude, whose
    offsets are measured either way. The module docstring has what each one assumes.
    """
    started = span[0]
    claude = []
    agy = []
    elapsed_before = []
    running_elapsed = 0.0
    seen_ids = set()
    vendor = None
    with open(stdout_log, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                record = json.loads(line)
            except ValueError:
                continue
            if record.get("type") == "assistant":
                vendor = "claude"
                message = record.get("message") or {}
                usage = message.get("usage")
                if not isinstance(usage, dict):
                    continue
                if not any(k in usage for k in (
                        "input_tokens", "output_tokens",
                        "cache_read_input_tokens", "cache_creation_input_tokens")):
                    continue
                message_id = message.get("id")
                # #1686 review F6/F4: repeated ids carry an IDENTICAL usage object (verified over these
                # captures), so first sighting wins, exactly as the monitor does it.
                if isinstance(message_id, str) and message_id and message_id in seen_ids:
                    continue
                stamp = record.get("timestamp")
                if not stamp:
                    continue
                # #1707 review F7: register the id only once a usable (timestamped) sample is actually
                # in hand -- the same guard tools/fleet-glass/pusher.py:extract_live_counts already
                # carries. Registering before this point would poison seen_ids on a first-sighting line
                # that has usage but no timestamp, permanently dropping every later, timestamped repeat
                # of that same id.
                if isinstance(message_id, str) and message_id:
                    seen_ids.add(message_id)
                claude.append((
                    (_parse_time(stamp) - started).total_seconds(),
                    usage.get("input_tokens") or 0,
                    usage.get("output_tokens") or 0,
                    usage.get("cache_creation_input_tokens") or 0,
                ))
                continue
            step = record.get("step_update")
            if isinstance(step, dict):
                vendor = "agy"
                # Every DONE step carries `duration_seconds` -- its own elapsed time, not a wall-clock
                # stamp. Accumulated across ALL steps (tool steps included, since they consume wall
                # clock too) so a usage line's position reflects the work that preceded it.
                if isinstance(step.get("duration_seconds"), (int, float)):
                    running_elapsed += float(step["duration_seconds"])
                if (step.get("state") == "DONE"
                        and step.get("step_type") == "agent_response"
                        and isinstance(step.get("usage"), dict)):
                    usage = step["usage"]
                    agy.append((
                        usage.get("input_tokens") or 0,
                        usage.get("output_tokens") or 0,
                        usage.get("cache_creation_input_tokens") or 0,
                    ))
                    elapsed_before.append(running_elapsed)
    if vendor == "agy":
        duration = (span[1] - span[0]).total_seconds()
        count = len(agy)
        if count == 0:
            return vendor, []
        # RECONSTRUCTED either way, see module docstring.
        if offsets == "duration" and elapsed_before and elapsed_before[-1] > 0:
            # Rescaled onto the measured span: the raw cumulative sum matches it closely on a room
            # whose steps run back to back and overshoots severalfold on one with overlapping steps,
            # so the SHAPE is what this method contributes, never the absolute elapsed figure.
            scale = duration / elapsed_before[-1]
            return vendor, [(elapsed_before[i] * scale, a, b, c) for i, (a, b, c) in enumerate(agy)]
        return vendor, [(duration * (i + 1) / count, a, b, c) for i, (a, b, c) in enumerate(agy)]
    return vendor, claude


def sample_billed(vendor, sample):
    """#1706: the billed contribution of ONE raw sample, and the one place the vendor asymmetry lives.

    The module docstring has the measurement. On claude only `cache_creation` is a real figure, so
    billing `input`/`output` there sums two placeholder columns -- which is what this tool did until
    #1706 and what made its claude rows disagree with the engine that produced them.
    """
    if vendor == "claude":
        return sample[3]
    return sample[1] + sample[2]


def billed_total(vendor, samples):
    return sum(sample_billed(vendor, s) for s in samples)


def peak_window(samples, window=WINDOW, vendor="agy"):
    """Largest sum of billed inside any trailing `window`, on `vendor`'s own accounting (#1706)."""
    width = window.total_seconds()
    best = running = 0
    oldest = 0
    for sample in samples:
        running += sample_billed(vendor, sample)
        while samples[oldest][0] < sample[0] - width:
            running -= sample_billed(vendor, samples[oldest])
            oldest += 1
        best = max(best, running)
    return best


def scan(role_prefix, window=WINDOW, offsets="uniform"):
    rows = []
    for name in sorted(os.listdir(ROOMS)):
        if not name.startswith(role_prefix):
            continue
        room_dir = os.path.join(ROOMS, name)
        span = room_span(room_dir)
        if span is None:
            continue
        # Sorted, not glob order (#1707 review): a multi-execution room has several logs and the
        # unordered pick was nondeterministic. `executions` is reported alongside so a reader can see
        # when the total (one execution) and the span (all of them) disagree -- the rate is understated
        # for those rooms, which is why produced_work below matters more than the rate for them.
        logs = sorted(p for p in glob.glob(
            os.path.join(room_dir, "artifacts", "execution_*", ".stdout.log")) if os.path.getsize(p) > 0)
        if not logs:
            continue
        vendor, samples = billed_samples(logs[0], span, offsets)
        if not samples:
            continue
        total = billed_total(vendor, samples)
        minutes = (span[1] - span[0]).total_seconds() / 60
        rows.append({
            "room": name,
            "vendor": vendor,
            "total": total,
            # #1706: a claude total is a LOWER BOUND, an agy total a measurement -- carried per row so
            # no reader has to infer it from the vendor column.
            "billed_is_floor": vendor == "claude",
            "minutes": round(minutes, 2),
            "per_minute": round(total / minutes) if minutes else 0,
            "peak_window": peak_window(samples, window, vendor),
            "samples": len(samples),
            "reason": span[2],
            "cancelled": span[3],
            "produced_work": span[4],
            "executions": span[5],
        })
    return rows


def separation_rows(rows, reference_room):
    """The rooms that refute a rate threshold: faster than the reference AND they produced their work.

    Both halves are load-bearing. Faster-and-failed says nothing (a failing lane may deserve arrest);
    produced-work-and-slower says nothing either. Only a room that burned faster than the reference and
    still delivered proves a limit catching the reference would have killed real work.
    """
    reference = next((r for r in rows if reference_room in r["room"]), None)
    if reference is None:
        return None, []
    faster = [r for r in rows if r["per_minute"] > reference["per_minute"] and r["produced_work"]]
    return reference, faster


def command_sweep(args):
    window = timedelta(minutes=args.window)
    rows = scan(args.role_prefix, window, args.offsets)
    rows.sort(key=lambda r: r["per_minute"], reverse=True)
    print("role prefix: %s   window: %g min   agy offsets: %s   rooms swept: %d"
          % (args.role_prefix, args.window, args.offsets, len(rows)))
    # #1707 review F/M4: `peakWin` is EXACT for claude (the line's own timestamp) and RECONSTRUCTED for
    # agy (module docstring) -- both agy reconstructions rescale onto the same measured span, so a
    # peakWin comparison between two agy rows cannot by construction detect burstiness the span does not
    # already imply. `tok/min` (total / measured span) carries no such caveat on either vendor.
    print("(peakWin is EXACT on claude, RECONSTRUCTED on agy -- see --offsets and the module docstring)")
    # #1706: the two vendors' token columns are NOT the same quantity -- a claude row is a lower bound
    # (cache_creation alone is measurable; the seen fraction ran 0.28-0.91 across the sweep), an agy row
    # is a measurement. Printing them in one column unmarked is what made the pre-#1706 output readable
    # as a cross-vendor comparison it cannot support, so every floor row carries a trailing `+`.
    print("(a `+` on tok/min, peakWin and total marks a FLOOR -- claude rows only; see the docstring)")
    print("%-8s %10s %10s %11s %7s %5s %-16s %-6s %-5s %s"
          % ("vendor", "tok/min", "peakWin", "total", "min", "n", "reason", "canc", "work", "room"))
    for row in rows:
        mark = "+" if row["billed_is_floor"] else ""
        print("%-8s %10s %10s %11s %7.1f %5d %-16s %-6s %-5s %s" % (
            row["vendor"], "%d%s" % (row["per_minute"], mark), "%d%s" % (row["peak_window"], mark),
            "%d%s" % (row["total"], mark),
            row["minutes"], row["samples"], row["reason"], row["cancelled"],
            "yes" if row["produced_work"] else "NO", row["room"]))

    reference, faster = separation_rows(rows, args.reference_room)
    if reference is None:
        return 0
    delivered = [r for r in rows if r["produced_work"]]
    print("\nreference room %s burns %d billed tokens/min." % (args.reference_room, reference["per_minute"]))
    print("Rooms that PRODUCED THEIR WORK (>=1 executionSucceeded, 0 executionFailed) while burning "
          "faster: %d of the %d such rooms swept (%d rooms swept in all)."
          % (len(faster), len(delivered), len(rows)))
    for row in faster:
        print("   %-8s %s  %d tok/min (%.2fx the reference)"
              % (row["vendor"], row["room"], row["per_minute"], row["per_minute"] / reference["per_minute"]))
    if faster:
        print("\nNo billed-rate threshold separates the reference room from traffic that delivered: any "
              "limit low enough to arrest it also fires on each room listed above. Arrest forecloses "
              "retry (spec/baton.md SS3). Re-run with --window/--offsets to check that this does not "
              "turn on either choice.")
    return 0


def command_emit_fixture(args):
    payload = {
        "_comment": (
            "#1691 replay fixture, generated by tools/room-rate-sweep/sweep.py --emit-fixture. "
            "One entry per room: billed usage samples in emitted order as "
            "[offsetSeconds, inputTokens, outputTokens, cacheCreationTokens]. claude offsets are the "
            "line's own `timestamp` (MEASURED); agy offsets are RECONSTRUCTED -- agy stamps no "
            "wall-clock time on any line, only a per-step `duration_seconds` -- see the script's "
            "docstring for the two reconstruction methods and what each assumes. Repeated claude "
            "message.ids are collapsed to their first sighting here, the same rule "
            "TokenBudgetMonitor applies -- verified against these captures to carry an identical "
            "usage object per repeat. The SAMPLES are raw -- the stream's own four columns -- while "
            "`totalBilled`/`peakBilledIn5MinWindow` apply #1706's vendor-asymmetric billing over them: "
            "cache_creation alone on claude (a FLOOR, flagged by `billedIsFloor`, because the "
            "input/output columns on a mid-stream assistant line are placeholders) and input + output "
            "on agy (a measurement). A consumer replaying the raw samples through the engine's own "
            "ClaudeUsageParser reproduces these totals; one summing all three columns does not. "
            "`separation` is the corpus-wide answer to whether any rate "
            "threshold exists, captured here so a test can read the measurement rather than restate "
            "it. Regenerate rather than hand-editing."),
        "rooms": {},
    }
    for name in args.rooms:
        matches = [d for d in sorted(os.listdir(ROOMS)) if name in d]
        if not matches:
            print("no room matching %r" % name, file=sys.stderr)
            return 1
        room_dir = os.path.join(ROOMS, matches[0])
        span = room_span(room_dir)
        logs = sorted(p for p in glob.glob(
            os.path.join(room_dir, "artifacts", "execution_*", ".stdout.log")) if os.path.getsize(p) > 0)
        vendor, samples = billed_samples(logs[0], span, args.offsets)
        payload["rooms"][matches[0]] = {
            "vendor": vendor,
            "offsetsAreMeasured": vendor == "claude",
            "durationSeconds": round((span[1] - span[0]).total_seconds(), 3),
            "exitReason": span[2],
            "cancelled": span[3],
            "producedWork": span[4],
            "totalBilled": billed_total(vendor, samples),
            "billedIsFloor": vendor == "claude",
            "peakBilledIn5MinWindow": peak_window(samples, WINDOW, vendor),
            "samples": [[round(s[0], 3), s[1], s[2], s[3]] for s in samples],
        }

    # The separation measurement itself, so a test can assert over CAPTURED data instead of literals
    # retyped from a terminal (#1707 review: the first version of that test compared eight hardcoded
    # doubles and could not fail).
    rows = scan(args.role_prefix, WINDOW, args.offsets)
    reference, faster = separation_rows(rows, args.reference_room)
    if reference is not None:
        payload["separation"] = {
            "_comment": (
                "Billed tokens per minute = total / measured executionStarted..executionExited span. "
                "EXACT on both vendors -- no reconstruction is involved in this block, which is why "
                "the #1691 conclusion rests on it. `fasterAndDelivered` lists every swept room that "
                "burned faster than the reference AND produced its work (>=1 executionSucceeded, 0 "
                "executionFailed). `executions` (#1707 review F6) is the one field that tells a reader "
                "whether a row's figures are comparable: `totalBilled` comes from the FIRST execution's "
                "log alone while `minutes` spans the room's first executionStarted to its last "
                "executionExited, so on any row with executions > 1 the rate is understated and "
                "totalBilled may not belong to the execution that delivered."),
            "rolePrefix": args.role_prefix,
            "referenceRoom": reference["room"],
            "referenceTokensPerMinute": reference["per_minute"],
            "roomsSwept": len(rows),
            "roomsThatDelivered": sum(1 for r in rows if r["produced_work"]),
            "fasterAndDelivered": [
                {"room": r["room"], "vendor": r["vendor"], "tokensPerMinute": r["per_minute"],
                 "totalBilled": r["total"], "minutes": r["minutes"], "executions": r["executions"]}
                for r in sorted(faster, key=lambda r: r["per_minute"], reverse=True)
            ],
        }
    with open(args.emit_fixture, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(payload, handle, indent=2)
        handle.write("\n")
    print("wrote %s (%d rooms)" % (args.emit_fixture, len(payload["rooms"])))
    return 0


def role_ceiling(role, adapter):
    """#2034: the token budget `role` enforces on `adapter`, from a WorkerRoles.json entry.

    `token_budget` is either a scalar (one ceiling for every vendor) or a per-vendor map -- the shape
    Baton.Vendors.WorkerRoleCatalog resolves in C#. This is the analysis copy of that resolution, and
    it deliberately returns None rather than a fallback when a map does not name `adapter`: an
    unnamed vendor has NO ceiling from that role, and printing one it does not enforce is the whole
    defect this mode exists to measure.
    """
    budget = (role or {}).get("token_budget")
    if isinstance(budget, dict):
        value = budget.get(adapter)
        return value if isinstance(value, int) else None
    return budget if isinstance(budget, int) else None


def room_arrests(room_dir):
    """[(adapter, reason)] for every `executionArrested` this room journalled.

    Read off the EVENT's own `Adapter` and `Reason` fields rather than off the room -- a mixed-vendor
    room holds steps on more than one adapter, so crediting every arrest in it to every adapter the
    room mentions is wrong in exactly the direction that invents arrests for the vendor under study.

    Adapter is as fine as this gets: `executionArrested` carries no step id, so an arrest cannot be
    attributed to a ROLE from the event alone. The join exists -- `captureResolved` carries both
    `StepId` and `ExecutionId` -- and is deliberately not walked here (#2034); a caller wanting
    per-role arrest counts has to add it rather than read this function's per-adapter counts as one.
    """
    path = os.path.join(room_dir, "flow.jsonl")
    if not os.path.exists(path):
        return []
    arrests = []
    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            try:
                record = json.loads(line)
            except ValueError:
                continue
            event = record.get("Event") or {}
            if event.get("eventType") != "executionArrested":
                continue
            arrests.append((event.get("Adapter"), event.get("Reason")))
    return arrests


def settled_steps(rooms_dir, since=None):
    """One row per settled step in the corpus: its adapter, role, state and recorded usage.

    `since` filters on the room's own `terminalAt`, which is what makes "the rooms settled after a
    fix landed" a one-flag question. Note the granularity, because it is invisible otherwise: the
    filter is per ROOM, not per step -- a room that settled after the cutoff contributes every step
    it holds, including one that ran hours before it, and a room still running (no `terminal.json`)
    is excluded whenever its steps ran. Ask "has anything run since X" of the corpus's modification
    times as well before reading an empty table as an answer. Rows whose usage is absent entirely are still returned, with
    None figures -- never a fabricated zero, following WorkerUsage's own convention -- so a caller
    can tell "no lane ran" from "lanes ran and measured nothing".
    """
    rows = []
    for name in sorted(os.listdir(rooms_dir)):
        room_dir = os.path.join(rooms_dir, name)
        bindings_path = os.path.join(room_dir, "bindings.json")
        terminal_path = os.path.join(room_dir, "terminal.json")
        if not (os.path.isfile(bindings_path) and os.path.isfile(terminal_path)):
            continue
        try:
            with open(bindings_path, encoding="utf-8", errors="replace") as handle:
                bindings = json.load(handle)
            with open(terminal_path, encoding="utf-8", errors="replace") as handle:
                terminal = json.load(handle)
        except ValueError:
            continue
        settled_at = terminal.get("terminalAt")
        if since is not None and (settled_at is None or _parse_time(settled_at) < since):
            continue
        for step in terminal.get("steps") or []:
            binding = bindings.get(step.get("id")) or {}
            usage = step.get("usage") or {}
            rows.append({
                "room": name,
                "step": step.get("id"),
                "adapter": binding.get("Adapter"),
                "state": step.get("state"),
                "settledAt": settled_at,
                # What TokenBudgetMonitor actually arrests on (the running Σ it accumulated live) and
                # what the post-hoc fold read off the whole stream. On claude the first is a FLOOR and
                # the two differ by the under-read; on agy and on codex since #2022 they agree.
                "live": usage.get("liveBilledTokens"),
                "posthoc": usage.get("billedTokens"),
            })
    return rows


def _percentile(values, percent):
    ordered = sorted(v for v in values if v is not None)
    if not ordered:
        return None
    index = min(len(ordered) - 1, int(round((percent / 100.0) * (len(ordered) - 1))))
    return ordered[index]


def budget_headroom_rows(steps, arrests, roles):
    """#2034: per (adapter, role), the distribution of live billed tokens against that role's ceiling.

    The comparison is against `live`, not against `posthoc`, because live is the quantity the arrest
    is made on -- comparing a post-hoc total to a ceiling answers a question no monitor ever asks.
    `ratio` (posthoc / live) is carried beside it because it is what makes one ceiling mean different
    things on different vendors: a vendor whose live meter under-reads by 2.4x is protected by a 250k
    ceiling at roughly 600k of real spend, and a vendor whose meter is complete is protected at 250k.
    """
    buckets = {}
    for step in steps:
        key = (step["adapter"], step["step"])
        buckets.setdefault(key, []).append(step)
    arrest_counts = {}
    for adapter, reason in arrests:
        arrest_counts[(adapter, reason)] = arrest_counts.get((adapter, reason), 0) + 1
    rows = []
    for (adapter, role_id), group in sorted(buckets.items(), key=lambda pair: (str(pair[0][0]), str(pair[0][1]))):
        live = [s["live"] for s in group]
        posthoc = [s["posthoc"] for s in group]
        ratios = sorted(s["posthoc"] / s["live"] for s in group if s["live"] and s["posthoc"])
        ceiling = role_ceiling(roles.get(role_id), adapter)
        live_p95 = _percentile(live, 95)
        rows.append({
            "adapter": adapter,
            "role": role_id,
            "n": len(group),
            "measured": len([v for v in live if v is not None]),
            "liveP50": _percentile(live, 50),
            "liveP95": live_p95,
            "liveMax": _percentile(live, 100),
            "posthocP95": _percentile(posthoc, 95),
            "ratioP50": round(ratios[len(ratios) // 2], 2) if ratios else None,
            "ceiling": ceiling,
            # How much of the ceiling the p95 lane already spends. >= 100 % means the measured p95
            # lane arrests; the closer to 100 the fewer lanes finish.
            "p95PercentOfCeiling": (
                round(100.0 * live_p95 / ceiling, 1) if ceiling and live_p95 is not None else None),
            "arrestedOnBudget": arrest_counts.get((adapter, "TokenBudget"), 0),
            "arrestedOnRate": arrest_counts.get((adapter, "BilledRate"), 0),
            "arrestedOnToolSteps": arrest_counts.get((adapter, "ToolStepCap"), 0),
        })
    return rows


def command_budget_headroom(args):
    """The #2034 instrument: is each role's ceiling above the lanes that actually run under it?

    Re-run it rather than trusting any table pasted into an issue -- the corpus grows, and a vendor
    whose meter changes (codex, #2022) moves its whole column with one fix.
    """
    with open(args.roles, encoding="utf-8") as handle:
        roles = {entry["id"]: entry for entry in json.load(handle)}
    since = _parse_time(args.since) if args.since else None
    steps = settled_steps(ROOMS, since)
    arrests = []
    for name in sorted(os.listdir(ROOMS)):
        arrests.extend(room_arrests(os.path.join(ROOMS, name)))
    rows = budget_headroom_rows(steps, arrests, roles)
    if not rows:
        print("no settled steps%s -- nothing to compare against a ceiling."
              % (" since %s" % args.since if since else ""))
        return 0
    header = ("adapter/role", "n", "live p50", "live p95", "live max", "posthoc p95",
              "ratio", "ceiling", "p95 % of ceiling", "arrests (budget/rate/steps)")
    print("%-26s %4s %10s %10s %10s %12s %6s %10s %17s %s" % header)
    for row in rows:
        print("%-26s %4d %10s %10s %10s %12s %6s %10s %17s %d/%d/%d" % (
            "%s/%s" % (row["adapter"], row["role"]), row["n"],
            _cell(row["liveP50"]), _cell(row["liveP95"]), _cell(row["liveMax"]),
            _cell(row["posthocP95"]), _cell(row["ratioP50"]), _cell(row["ceiling"]),
            _cell(row["p95PercentOfCeiling"]),
            row["arrestedOnBudget"], row["arrestedOnRate"], row["arrestedOnToolSteps"]))
    print("\narrest columns are per ADAPTER, not per role: executionArrested carries the adapter it "
          "fired on but no step id, so the same three counts repeat down a vendor's rows.")
    return 0


def _cell(value):
    return "n/a" if value is None else str(value)


def _selftest_ceiling_resolves_per_vendor_and_refuses_to_invent_one():
    """#2034: a per-vendor `token_budget` map answers for the vendors it names and for NO others.

    The control that discriminates is the third case: a role whose map omits `codex` must resolve to
    None, not to some other vendor's number and not to a default. A resolver that fell back would
    pass the first two arms and report a ceiling the engine does not enforce.
    """
    scalar = {"token_budget": 1200000}
    per_vendor = {"token_budget": {"claude": 250000, "agy": 250000, "codex": 250000}}
    partial = {"token_budget": {"claude": 250000}}
    assert role_ceiling(scalar, "codex") == 1200000, "a scalar budget applies to every vendor"
    assert role_ceiling(per_vendor, "codex") == 250000, "a map answers for the vendor it names"
    assert role_ceiling(partial, "codex") is None, "a map must not invent a ceiling for an absent vendor"
    assert role_ceiling({}, "codex") is None, "a role with no budget has no ceiling"


def _selftest_arrests_are_credited_to_the_adapter_that_arrested():
    """#2034: an arrest counts against the adapter on its OWN event, never against every adapter the
    room mentions.

    Both arms are needed. The positive arm alone passes under the naive "this room contains a codex
    step, so its arrests are codex arrests" scan; the negative arm is what fails it -- the fixture
    room holds one claude arrest and one codex step, and a correct reader credits codex with zero.
    """
    steps = [
        {"room": "r", "step": "review", "adapter": "codex", "state": "Succeeded",
         "settledAt": None, "live": 100, "posthoc": 100},
    ]
    arrests = [("claude", "TokenBudget")]
    rows = budget_headroom_rows(steps, arrests, {"review": {"token_budget": {"codex": 250000}}})
    assert len(rows) == 1 and rows[0]["adapter"] == "codex", rows
    assert rows[0]["arrestedOnBudget"] == 0, \
        "a claude arrest must not be credited to the codex row: %r" % rows[0]
    rows = budget_headroom_rows(steps, [("codex", "TokenBudget")],
                                {"review": {"token_budget": {"codex": 250000}}})
    assert rows[0]["arrestedOnBudget"] == 1, "a codex arrest must be credited to the codex row"


def _selftest_headroom_compares_the_live_meter_and_never_fabricates_a_zero():
    """#2034: the ceiling comparison is against the LIVE Σ (what TokenBudgetMonitor arrests on), and a
    step that recorded no usage is excluded from the distribution rather than counted as 0.

    The discriminating arm is the third step: it has no figures at all. Counting it as zero would
    drag p50 down and make a tight ceiling read as roomy -- the failure mode that makes this whole
    measurement lie in the safe-looking direction.
    """
    steps = [
        {"room": "a", "step": "review", "adapter": "claude", "state": "Succeeded",
         "settledAt": None, "live": 100000, "posthoc": 240000},
        {"room": "b", "step": "review", "adapter": "claude", "state": "Succeeded",
         "settledAt": None, "live": 200000, "posthoc": 480000},
        {"room": "c", "step": "review", "adapter": "claude", "state": "Failed",
         "settledAt": None, "live": None, "posthoc": None},
    ]
    row = budget_headroom_rows(steps, [], {"review": {"token_budget": {"claude": 250000}}})[0]
    assert row["n"] == 3 and row["measured"] == 2, row
    assert row["liveP50"] == 100000 and row["liveMax"] == 200000, row
    assert row["ratioP50"] == 2.4, row
    # 200000 of a 250000 ceiling, off the LIVE figure. Off `posthoc` it would read 192 %, which is a
    # different claim about a quantity no monitor compares to a budget.
    assert row["p95PercentOfCeiling"] == 80.0, row


def _selftest_dedupe_does_not_poison_on_a_missing_timestamp():
    """#1707 review F7: a first-sighting claude line with usage but no `timestamp` must be dropped
    WITHOUT blocking a later, timestamped repeat of the same message.id from being counted -- the same
    guard tools/fleet-glass/pusher.py:extract_live_counts already carries. Proves the fix by construction:
    two lines share one message.id, the first has no timestamp, the second does; before the fix the
    first line poisoned seen_ids and the second was silently dropped too, yielding zero samples.
    """
    import tempfile

    lines = [
        json.dumps({"type": "assistant", "message": {
            "id": "m1", "usage": {"input_tokens": 5, "output_tokens": 3}}}),
        json.dumps({"type": "assistant", "timestamp": "2026-09-01T00:00:10Z", "message": {
            "id": "m1", "usage": {"input_tokens": 5, "output_tokens": 3}}}),
    ]
    with tempfile.NamedTemporaryFile("w", suffix=".log", delete=False, encoding="utf-8") as handle:
        handle.write("\n".join(lines) + "\n")
        path = handle.name
    try:
        started = _parse_time("2026-09-01T00:00:00Z")
        vendor, samples = billed_samples(path, (started, started))
        assert vendor == "claude", "expected claude, got %r" % (vendor,)
        assert samples == [(10.0, 5, 3, 0)], (
            "expected the timestamped repeat to be counted once, got %r" % (samples,))
    finally:
        os.unlink(path)


def _selftest_claude_bills_cache_creation_alone_against_the_shared_gate():
    """#1706 review M1/M5: this tool is the THIRD implementation of the claude billing rule, and it was
    the one left on the superseded reading -- summing the two placeholder columns while the engine that
    produced its inputs had stopped. It now bills through `sample_billed`, and the expected values come
    from the SAME fixture the engine's ClaudeEngineAndPusherBillingGateTests and pusher.py's selftest
    read, so a rule change landing on two of the three fails here rather than drifting silently.

    Control on the harness, read first: the agy arm bills input+output over the same raw sample and must
    NOT agree with the claude arm -- without it a `sample_billed` that returned 0 for everything, or that
    ignored the vendor, would pass the claude assertions below.
    """
    gate_path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                             "..", "..", "tests", "Baton.Tests", "Fixtures", "claude-billing-gate.json")
    assert os.path.isfile(gate_path), "shared billing-gate fixture not found at %s" % gate_path
    with open(gate_path, encoding="utf-8") as handle:
        gate = json.load(handle)

    # #1724 item 6: guard the instrument first, matching the C# gate's `The_fixture_discriminates_
    # absent_from_zero` and pusher.py's own "discriminates an ABSENT billed figure from a measured 0"
    # check -- without both an absent case and a measured-zero case in the shared fixture, an edit that
    # removed the explicit-0 arm would weaken all three consumers' guards while this one kept passing.
    assert any(case["expectedBilledTokens"] is None for case in gate["cases"]), (
        "shared fixture must carry a case with expectedBilledTokens: null")
    assert any(case["expectedBilledTokens"] == 0 for case in gate["cases"]), (
        "shared fixture must carry a case with expectedBilledTokens: 0 (measured, not absent)")

    checked = 0
    for case in gate["cases"]:
        expected = case["expectedBilledTokens"]
        samples = []
        seen = set()
        for raw in case["lines"]:
            record = json.loads(raw)
            if record.get("type") != "assistant":
                continue
            usage = (record.get("message") or {}).get("usage")
            if not isinstance(usage, dict):
                continue
            message_id = (record.get("message") or {}).get("id")
            if isinstance(message_id, str) and message_id in seen:
                continue
            if isinstance(message_id, str) and message_id:
                seen.add(message_id)
            samples.append((0.0, usage.get("input_tokens") or 0, usage.get("output_tokens") or 0,
                            usage.get("cache_creation_input_tokens") or 0))
        # This tool has no "absent" representation -- it sums samples -- so a null expectation is read
        # as "nothing billable on those lines", which is 0 here and correctly reported as absent by the
        # two consumers that CAN express absence. Stated rather than left to look like agreement.
        want = 0 if expected is None else expected
        got = billed_total("claude", samples)
        assert got == want, "case %r: expected %r billed, got %r" % (case["name"], want, got)
        checked += 1
    assert checked == len(gate["cases"]), "not every fixture case was exercised"

    control = [(0.0, 14205, 443, 0)]
    assert billed_total("agy", control) == 14648, "the agy arm must bill input+output"
    assert billed_total("claude", control) == 0, (
        "the claude arm must bill cache_creation ALONE -- if this equals the agy figure, sample_billed "
        "is not reading the vendor and every claude row above is back on the pre-#1706 accounting")


def command_selftest(_args):
    tests = [_selftest_dedupe_does_not_poison_on_a_missing_timestamp,
             _selftest_claude_bills_cache_creation_alone_against_the_shared_gate,
             _selftest_ceiling_resolves_per_vendor_and_refuses_to_invent_one,
             _selftest_arrests_are_credited_to_the_adapter_that_arrested,
             _selftest_headroom_compares_the_live_meter_and_never_fabricates_a_zero]
    failed = 0
    for test in tests:
        name = test.__name__
        try:
            test()
        except AssertionError as exc:
            failed += 1
            print("FAIL %s: %s" % (name, exc), file=sys.stderr)
        else:
            print("OK %s" % name)
    print("selftest: %d of %d passed" % (len(tests) - failed, len(tests)))
    return 1 if failed else 0


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--selftest", action="store_true",
                        help="run this script's own unit tests (no room corpus needed)")
    parser.add_argument("--sweep", action="store_true", help="print the per-room rate table")
    parser.add_argument("--budget-headroom", action="store_true",
                        help="#2034: print the per adapter/role table of live billed tokens against "
                             "that role's token_budget ceiling, plus the arrest counts per adapter")
    parser.add_argument("--since", metavar="ISO8601",
                        help="with --budget-headroom, keep only rooms whose terminalAt is at or "
                             "after this instant -- how you scope the table to the lanes that ran "
                             "after a metering fix landed")
    parser.add_argument("--roles", default=os.path.join(
        os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
        "src", "Baton.Vendors", "WorkerRoles.json"),
        help="the role catalog the ceilings are read from (default: this repo's WorkerRoles.json)")
    parser.add_argument("--role-prefix", default="dispatch-implement",
                        help="which rooms to sweep (default: dispatch-implement)")
    parser.add_argument("--reference-room", default="38c24d11",
                        help="the room the separation question is asked about (default: #1691's runaway)")
    parser.add_argument("--emit-fixture", metavar="PATH",
                        help="write a replay fixture for --rooms to PATH")
    parser.add_argument("--rooms", nargs="*", default=[],
                        help="room name fragments to emit into the fixture")
    parser.add_argument("--window", type=float, default=WINDOW.total_seconds() / 60, metavar="MINUTES",
                        help="trailing window width for the peak column (default: 5, the width "
                             "--billed-rate-limit is stated in). Sweep it to check the separation "
                             "answer does not turn on this choice.")
    parser.add_argument("--offsets", choices=("uniform", "duration"), default="uniform",
                        help="how agy per-line offsets are reconstructed; ignored on claude, whose "
                             "offsets are measured. See the module docstring.")
    args = parser.parse_args(argv)

    if args.selftest:
        return command_selftest(args)

    if not os.path.isdir(ROOMS):
        print("no room corpus at %s -- nothing to sweep." % ROOMS, file=sys.stderr)
        return 2
    if args.emit_fixture:
        return command_emit_fixture(args)
    if args.budget_headroom:
        return command_budget_headroom(args)
    if args.sweep:
        return command_sweep(args)
    parser.print_help()
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
