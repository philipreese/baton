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
accounting: `--budget-headroom` prints, per adapter and step, how the lanes that actually ran compare
to the ceiling that would have arrested them -- the resolved `TokenBudget` on each room's own
`bindings.json` where one exists (the figure that armed the monitor), falling back to today's
`src/Baton.Vendors/WorkerRoles.json` where none does, with a column saying which.

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

# Mirrors Baton.Vendors.WorkerRoleCatalog.KnownTokenBudgetAdapters -- the only keys a per-vendor
# `token_budget` map may name, and therefore the adapters whose ABSENCE from a map is a fail-closed
# refusal rather than an unwatched lane. `role_ceiling` has the split; the C# is TokenBudgetSpec.Resolve.
KNOWN_TOKEN_BUDGET_ADAPTERS = ("claude", "agy", "codex")

# The two non-numeric things a ceiling cell can say, kept distinct from None ("no ceiling, unwatched")
# because they support opposite operational conclusions. See `role_ceiling` and `budget_headroom_rows`.
CEILING_REFUSED = "refused"
CEILING_MIXED = "mixed"


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
    """#2034: what a WorkerRoles.json entry says about `adapter` -- a ceiling, or WHICH of the engine's
    two no-ceiling cases applies.

    `token_budget` is either a scalar (one ceiling for every vendor) or a per-vendor map -- the shape
    Baton.Vendors.WorkerRoleCatalog resolves in C#. This is the analysis copy of
    `TokenBudgetSpec.Resolve`, and it now carries that method's own split (spec/baton.md SS3), which an
    earlier revision of this function flattened into one None:

      * a map naming `adapter`               -> that figure.
      * a map omitting a KNOWN adapter       -> CEILING_REFUSED. The engine throws
        TokenBudgetAdapterNotConfiguredException at dispatch time: the combination is UNCONFIGURED,
        the lane never starts, and "codex review cannot be dispatched" is the opposite operational
        conclusion from "codex review runs unwatched".
      * a map omitting an UNKNOWN adapter    -> None, unwatched. WorkerRoleCatalog rejects any other
        key at load, so a test double or an engine-run capture adapter can never appear in a map.
      * no `token_budget` at all             -> None, unwatched on every adapter.

    Never a fallback to another vendor's figure: printing a ceiling the engine does not enforce is the
    defect this mode exists to measure. Note the TENSE -- this reads today's catalog, so
    CEILING_REFUSED says a dispatch would be refused NOW, never that some lane ran refused: no settled
    step can exist for a refused combination. That is why `budget_headroom_rows` only ever surfaces it
    on a row whose ceiling came from the catalog rather than from the room's own binding.
    """
    budget = (role or {}).get("token_budget")
    if isinstance(budget, dict):
        value = budget.get(adapter)
        if isinstance(value, int):
            return value
        return CEILING_REFUSED if adapter in KNOWN_TOKEN_BUDGET_ADAPTERS else None
    return budget if isinstance(budget, int) else None


def room_arrests(room_dir):
    """[(adapter, reason, when)] for every `executionArrested` this room journalled.

    Read off the EVENT's own `Adapter` and `Reason` fields rather than off the room -- a mixed-vendor
    room holds steps on more than one adapter, so crediting every arrest in it to every adapter the
    room mentions is wrong in exactly the direction that invents arrests for the vendor under study.

    `when` is the ledger line's own `WriterUtcTimestamp`, or None when it is absent or unparseable.
    It is what lets `--since` scope this column rather than printing a lifetime count under a windowed
    header (#2034 review) -- `arrests_in_window` applies it, at a granularity the step rows do not
    have: per EVENT here, against the per-ROOM `terminalAt` `settled_steps` filters on.

    Adapter is as fine as this gets: `executionArrested` carries no step id, so an arrest cannot be
    attributed to a ROLE from the event alone. The join exists -- `executionRequestAccepted` carries
    the request's `StepId` for every execution whose request named one, so it covers ordinary
    executions rather than only the capture resolutions `captureResolved` is written for -- and is
    deliberately not walked here (#2034); a caller wanting per-role arrest counts has to add it rather
    than read this function's per-adapter counts as one.
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
            stamp = record.get("WriterUtcTimestamp")
            try:
                when = _parse_time(stamp) if stamp else None
            except ValueError:
                when = None
            arrests.append((event.get("Adapter"), event.get("Reason"), when))
    return arrests


def arrests_in_window(arrests, since):
    """(kept, undated, unattributed): the arrests a table may count, and the two kinds it cannot.

    #2034 review, the HIGH: `--since` used to scope the step rows while the arrest columns walked the
    whole corpus, so a windowed table printed lifetime arrest counts under a header that claimed a
    scope -- the failure being a ceiling moved on arrests that predate the metering fix the window was
    drawn around. `since` is compared against each arrest's OWN timestamp, so this is per event.

    Two drops, counted rather than silent because both are invisible in the table itself:

      * `undated` -- no usable `WriterUtcTimestamp`, so the arrest cannot be placed inside or outside
        the window. A windowed run drops it (it cannot be shown under a scope it may not belong to);
        an unwindowed run keeps it, since there is no scope to violate.
      * `unattributed` -- a null `Adapter`, which every ledger line written before #1745 carries
        (Baton.Domain.FlowEvent's ExecutionArrested.Adapter). Those key on (None, reason) and so can
        never land on any adapter's row, with or without a window.

    Both counts are themselves SCOPED, because they are reported beside a scoped table: under a window
    they tally only the arrests that window covers, so an unattributable arrest from outside it is not
    announced as something this table is hiding. Unwindowed, they cover the corpus.
    """
    kept, undated, unattributed = [], 0, 0
    for adapter, reason, when in arrests:
        if since is not None:
            if when is None:
                undated += 1
                continue
            if when < since:
                continue
        if adapter is None:
            unattributed += 1
            continue
        kept.append((adapter, reason, when))
    return kept, undated, unattributed


def settled_steps(rooms_dir, since=None):
    """(rows, skipped): one row per settled step in the corpus, and what the walk could not read.

    `since` filters on the room's own `terminalAt`, which is what makes "the rooms settled after a
    fix landed" a one-flag question. Note the granularity, because it is invisible otherwise: the
    filter is per ROOM, not per step -- a room that settled after the cutoff contributes every step
    it holds, including one that ran hours before it, and a room still running (no `terminal.json`)
    is excluded whenever its steps ran. Ask "has anything run since X" of the corpus's modification
    times as well before reading an empty table as an answer. Rows whose usage is absent entirely are still returned, with
    None figures -- never a fabricated zero, following WorkerUsage's own convention -- so a caller
    can tell "no lane ran" from "lanes ran and measured nothing".

    `skipped` counts the rooms that never reached a row, because a short or empty table gets read as
    evidence that some vendor's lanes have not run, and a silently-dropped room is exactly the
    evidence that argument cannot afford to lose (#2034 review): `unreadable` is a settled room whose
    `bindings.json` is missing, or whose JSON failed to parse, or that could not be opened;
    `unsettled` is a room with no `terminal.json` yet; `undatable` is a settled room carrying no
    `terminalAt`, which only a windowed run drops -- it cannot be placed on either side of the cutoff,
    the same edge `arrests_in_window` counts for an arrest with no timestamp. Print them beside the
    table.

    `step` is the STEP id, not the role id. `RoleDispatch` makes the two the same string for a
    dispatch room -- StepId, Worker and the bindings key are all `role.Id` -- which is what this
    corpus is. `WorkflowTemplateComposer` does not: a composed template's step id is the PHASE name
    while the role is `phase.RoleId`, and it splices `<phase>-capture` steps that are no role at all.
    The binding carries no role id either, so the role is not recoverable here; such a row is labelled
    by its step, and its catalog ceiling degrades to `n/a` unless a phase happens to be named after a
    role -- safe, but a caller reading this column as a role over that corpus would be wrong.
    """
    rows = []
    skipped = {"unreadable": 0, "unsettled": 0, "undatable": 0}
    for name in sorted(os.listdir(rooms_dir)):
        room_dir = os.path.join(rooms_dir, name)
        if not os.path.isdir(room_dir):
            continue
        bindings_path = os.path.join(room_dir, "bindings.json")
        terminal_path = os.path.join(room_dir, "terminal.json")
        if not os.path.isfile(terminal_path):
            skipped["unsettled"] += 1
            continue
        if not os.path.isfile(bindings_path):
            skipped["unreadable"] += 1
            continue
        try:
            with open(bindings_path, encoding="utf-8", errors="replace") as handle:
                bindings = json.load(handle)
            with open(terminal_path, encoding="utf-8", errors="replace") as handle:
                terminal = json.load(handle)
        except (ValueError, OSError):
            skipped["unreadable"] += 1
            continue
        settled_at = terminal.get("terminalAt")
        if since is not None:
            if settled_at is None:
                skipped["undatable"] += 1
                continue
            if _parse_time(settled_at) < since:
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
                # The ceiling that ACTUALLY armed TokenBudgetMonitor for this lane -- the resolved
                # figure RoleDispatch.ToBinding wrote onto the binding, `--token-budget` override
                # included, which bypasses catalog resolution entirely. None means the lane ran
                # unwatched. Preferred over re-resolving today's catalog: see budget_headroom_rows.
                "bindingCeiling": binding.get("TokenBudget"),
                # What TokenBudgetMonitor actually arrests on (the running Σ it accumulated live) and
                # what the post-hoc fold read off the whole stream. On claude the first is a FLOOR and
                # the two differ by the under-read; on agy and on codex since #2022 they agree.
                "live": usage.get("liveBilledTokens"),
                "posthoc": usage.get("billedTokens"),
            })
    return rows, skipped


def _percentile(values, percent):
    ordered = sorted(v for v in values if v is not None)
    if not ordered:
        return None
    index = min(len(ordered) - 1, int(round((percent / 100.0) * (len(ordered) - 1))))
    return ordered[index]


def budget_headroom_rows(steps, arrests, roles):
    """#2034: per (adapter, step), the distribution of live billed tokens against the ceiling that
    would have arrested those lanes.

    The comparison is against `live`, not against `posthoc`, because live is the quantity the arrest
    is made on -- comparing a post-hoc total to a ceiling answers a question no monitor ever asks.
    `ratio` (posthoc / live) is carried beside it because it is what makes one ceiling mean different
    things on different vendors: a vendor whose live meter under-reads by 2.4x is protected by a 250k
    ceiling at roughly 600k of real spend, and a vendor whose meter is complete is protected at 250k.

    Which ceiling (#2034 review) -- `ceilingFrom` says which of two, per row, because they are not the
    same claim:

      * `binding` -- every lane in the row carried the SAME resolved `TokenBudget` on its own
        `bindings.json`. That is the figure that actually armed `TokenBudgetMonitor`, including a
        `--token-budget` override, which bypasses catalog resolution altogether. Preferred whenever
        it exists: re-resolving today's catalog silently re-baselines historical rows the moment a
        ceiling moves, changing what the percentage column means without the corpus changing.
      * `catalog` -- no lane in the row carried one, so the row falls back to today's WorkerRoles.json
        and `role_ceiling`'s tense caveat applies to the whole row.

    Lanes in one row that ran under DIFFERENT ceilings (an override on some, or an unwatched lane
    beside watched ones) resolve to CEILING_MIXED rather than to any one of them: no single percentage
    is meaningful over a mixed row, and folding an unwatched lane into a watched row's percentage is
    the same defect one level down. An unwatched lane is a distinct value here, never a silent absence.

    `n` is the count the distribution is actually over -- steps carrying a `liveBilledTokens` -- and
    `steps` is every settled step in the row whatever its state, with `states` breaking that down.
    Before #2034's review only the latter was printed, as `n`, so a row's p95 read as though it had
    more samples behind it than it did.
    """
    buckets = {}
    for step in steps:
        key = (step["adapter"], step["step"])
        buckets.setdefault(key, []).append(step)
    arrest_counts = {}
    for adapter, reason, _when in arrests:
        arrest_counts[(adapter, reason)] = arrest_counts.get((adapter, reason), 0) + 1
    rows = []
    for (adapter, step_id), group in sorted(buckets.items(), key=lambda pair: (str(pair[0][0]), str(pair[0][1]))):
        live = [s["live"] for s in group]
        posthoc = [s["posthoc"] for s in group]
        ratios = sorted(s["posthoc"] / s["live"] for s in group if s["live"] and s["posthoc"])
        # None is a member of this set, not a gap in it: a lane whose binding carries no TokenBudget
        # ran unwatched, and a row mixing one with a watched lane is mixed.
        armed = {s["bindingCeiling"] for s in group}
        if armed == {None}:
            ceiling, ceiling_from = role_ceiling(roles.get(step_id), adapter), "catalog"
        elif len(armed) == 1:
            ceiling, ceiling_from = armed.pop(), "binding"
        else:
            ceiling, ceiling_from = CEILING_MIXED, "binding"
        state_counts = {}
        for member in group:
            label = member["state"] if member["state"] is not None else "unrecorded"
            state_counts[label] = state_counts.get(label, 0) + 1
        live_p95 = _percentile(live, 95)
        rows.append({
            "adapter": adapter,
            "step": step_id,
            "steps": len(group),
            "measured": len([v for v in live if v is not None]),
            "states": ",".join("%s:%d" % (k, state_counts[k]) for k in sorted(state_counts)),
            "liveP50": _percentile(live, 50),
            "liveP95": live_p95,
            "liveMax": _percentile(live, 100),
            "posthocP95": _percentile(posthoc, 95),
            "ratioP50": round(ratios[len(ratios) // 2], 2) if ratios else None,
            "ceiling": ceiling,
            "ceilingFrom": ceiling_from,
            # How much of the ceiling the p95 lane already spends. >= 100 % means the measured p95
            # lane arrests; the closer to 100 the fewer lanes finish. Only ever computed against a
            # NUMBER -- CEILING_REFUSED and CEILING_MIXED are not ceilings to take a percentage of.
            "p95PercentOfCeiling": (
                round(100.0 * live_p95 / ceiling, 1)
                if isinstance(ceiling, int) and ceiling and live_p95 is not None else None),
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
    steps, skipped = settled_steps(ROOMS, since)
    arrests = []
    for name in sorted(os.listdir(ROOMS)):
        arrests.extend(room_arrests(os.path.join(ROOMS, name)))
    # The HIGH from #2034's review: this filter is what makes the arrest columns carry the same scope
    # the step rows do, instead of a lifetime count under a windowed header.
    arrests, undated, unattributed = arrests_in_window(arrests, since)
    rows = budget_headroom_rows(steps, arrests, roles)
    if not rows:
        print("no settled steps%s -- nothing to compare against a ceiling."
              % (" since %s" % args.since if since else ""))
        _print_corpus_coverage(skipped)
        return 0
    header = ("adapter/step", "steps", "n", "live p50", "live p95", "live max", "posthoc p95",
              "ratio", "ceiling", "from", "p95 % of ceiling", "arrests", "step states")
    print("%-26s %5s %4s %10s %10s %10s %12s %6s %10s %-7s %16s %8s  %s" % header)
    for row in rows:
        print("%-26s %5d %4d %10s %10s %10s %12s %6s %10s %-7s %16s %8s  %s" % (
            "%s/%s" % (row["adapter"], row["step"]), row["steps"], row["measured"],
            _cell(row["liveP50"]), _cell(row["liveP95"]), _cell(row["liveMax"]),
            _cell(row["posthocP95"]), _cell(row["ratioP50"]), _cell(row["ceiling"]),
            row["ceilingFrom"], _cell(row["p95PercentOfCeiling"]),
            "%d/%d/%d" % (row["arrestedOnBudget"], row["arrestedOnRate"], row["arrestedOnToolSteps"]),
            row["states"]))
    print("\n`n` is the steps carrying a liveBilledTokens -- the denominator of live p50/p95/max and "
          "of the ceiling percentage, and the only one printed. `posthoc p95` and `ratio` drop their "
          "own missing values, so their samples are <= n. `steps` counts every settled step in the "
          "row whatever its state; `step states` breaks that down (an arrested lane's live total sits "
          "at the ceiling by construction, so a row heavy with them reads tighter than its traffic).")
    print("`from` is where the ceiling came from: `binding` is the figure that ACTUALLY armed "
          "TokenBudgetMonitor for those lanes (bindings.json, --token-budget override included); "
          "`catalog` is today's %s re-resolved, which no lane in that row carried. `mixed` means the "
          "row's lanes ran under more than one ceiling (or one watched beside one unwatched), so no "
          "single percentage is meaningful. `refused` means today's catalog would refuse that "
          "dispatch outright -- fail closed, the lane never starts -- which is the opposite "
          "conclusion from the `n/a` of a lane that runs unwatched."
          % os.path.basename(args.roles))
    print("arrests are budget/rate/steps and are per ADAPTER, not per role: executionArrested carries "
          "the adapter it fired on but no step id, so the same three counts repeat down a vendor's "
          "rows.%s" % (
              (" They carry this run's scope too, filtered on each arrest's own WriterUtcTimestamp -- "
               "per EVENT, finer than the per-ROOM terminalAt the step rows above are filtered on."
               if since else " No --since was given, so they are lifetime counts.")))
    if undated:
        print("%d arrest(s) carry no usable WriterUtcTimestamp: they cannot be placed inside or "
              "outside the window and were dropped rather than counted under a scope they may not "
              "belong to." % undated)
    if unattributed:
        print("%d arrest(s) are on no row at all -- their own Adapter is null (every ledger line "
              "written before #1745), so they key on (None, reason) and no adapter's row can show "
              "them." % unattributed)
    _print_corpus_coverage(skipped)
    return 0


def _print_corpus_coverage(skipped):
    """What the corpus walk could not read -- printed even when the table is empty, because an empty
    table is precisely when someone is about to read it as "no lane of that vendor has run" (#2034
    review). A silently-dropped room is the evidence that claim cannot afford to lose.
    """
    print("skipped: %d unreadable (a settled room whose bindings.json/terminal.json is missing, "
          "unparseable or unopenable), %d still running (no terminal.json), %d undatable (settled "
          "with no terminalAt, dropped only by --since)."
          % (skipped["unreadable"], skipped["unsettled"], skipped["undatable"]))


def _cell(value):
    return "n/a" if value is None else str(value)


def _selftest_ceiling_resolves_per_vendor_and_refuses_to_invent_one():
    """#2034: a per-vendor `token_budget` map answers for the vendors it names, for NO others, and
    says WHICH kind of no-answer it is giving.

    Two controls, and the pair is what discriminates. A map omitting `codex` -- a KNOWN adapter --
    must not resolve to some other vendor's number (a fallback would report a ceiling the engine does
    not enforce) and must not resolve to the same value as an UNKNOWN adapter either: the engine
    refuses that dispatch (TokenBudgetSpec.Resolve throws) where it leaves the unknown one unwatched,
    and a resolver collapsing both to None reads out as "codex runs unwatched" when the truth is
    "codex cannot be dispatched". Both halves pass under the collapsed resolver individually; only
    asserting they DIFFER fails it.
    """
    scalar = {"token_budget": 1200000}
    per_vendor = {"token_budget": {"claude": 250000, "agy": 250000, "codex": 250000}}
    partial = {"token_budget": {"claude": 250000}}
    assert role_ceiling(scalar, "codex") == 1200000, "a scalar budget applies to every vendor"
    assert role_ceiling(per_vendor, "codex") == 250000, "a map answers for the vendor it names"
    assert role_ceiling(partial, "codex") == CEILING_REFUSED, (
        "a map omitting a KNOWN adapter is a fail-closed refusal at dispatch, not a ceiling and not "
        "an unwatched lane")
    assert role_ceiling(partial, "engine-capture") is None, (
        "a map omitting an UNKNOWN adapter leaves it unwatched -- WorkerRoleCatalog rejects such a "
        "key at load, so it can never be the configured-vs-missing question")
    assert role_ceiling(partial, "codex") != role_ceiling(partial, "engine-capture"), (
        "refused and unwatched are opposite operational conclusions and must not print the same")
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
         "settledAt": None, "bindingCeiling": None, "live": 100, "posthoc": 100},
    ]
    when = _parse_time("2026-09-05T12:00:00Z")
    arrests = [("claude", "TokenBudget", when)]
    rows = budget_headroom_rows(steps, arrests, {"review": {"token_budget": {"codex": 250000}}})
    assert len(rows) == 1 and rows[0]["adapter"] == "codex", rows
    assert rows[0]["arrestedOnBudget"] == 0, \
        "a claude arrest must not be credited to the codex row: %r" % rows[0]
    rows = budget_headroom_rows(steps, [("codex", "TokenBudget", when)],
                                {"review": {"token_budget": {"codex": 250000}}})
    assert rows[0]["arrestedOnBudget"] == 1, "a codex arrest must be credited to the codex row"


def _selftest_since_scopes_the_arrest_columns_and_not_only_the_step_rows():
    """#2034 review (HIGH): a windowed table must count only the arrests INSIDE its window.

    Proven end to end through the same three calls `command_budget_headroom` makes -- room_arrests,
    arrests_in_window, budget_headroom_rows -- rather than against the filter alone, because a command
    that forgot to call the filter would still pass a filter-only assertion. The fixture room holds two
    codex budget arrests, one an hour before the cutoff and one an hour after.

    The control is the unwindowed run, read first: it must print 2. Without it an arrest walk that
    dropped everything, or one that mis-parsed every timestamp, would satisfy the windowed arm.
    """
    import tempfile

    def line(stamp):
        return json.dumps({
            "owner": "flow",
            "Event": {"eventType": "executionArrested", "Reason": "TokenBudget", "Adapter": "codex"},
            "WriterUtcTimestamp": stamp})

    room = tempfile.mkdtemp()
    try:
        with open(os.path.join(room, "flow.jsonl"), "w", encoding="utf-8") as handle:
            handle.write(line("2026-09-04T23:12:31.5049859Z") + "\n")
            handle.write(line("2026-09-05T01:00:00.0000000Z") + "\n")
        arrests = room_arrests(room)
        assert len(arrests) == 2 and all(a[2] is not None for a in arrests), (
            "both arrests must be read and dated off WriterUtcTimestamp: %r" % (arrests,))
        steps = [{"room": "r", "step": "review", "adapter": "codex", "state": "Succeeded",
                  "settledAt": None, "bindingCeiling": 250000, "live": 100, "posthoc": 100}]
        roles = {"review": {"token_budget": {"codex": 250000}}}

        lifetime, undated, unattributed = arrests_in_window(arrests, None)
        row = budget_headroom_rows(steps, lifetime, roles)[0]
        assert (row["arrestedOnBudget"], undated, unattributed) == (2, 0, 0), (
            "control: an unwindowed run counts every arrest -- %r" % row)

        since = _parse_time("2026-09-05T00:00:00Z")
        windowed, undated, unattributed = arrests_in_window(arrests, since)
        row = budget_headroom_rows(steps, windowed, roles)[0]
        assert (row["arrestedOnBudget"], undated, unattributed) == (1, 0, 0), (
            "--since must scope the arrest column the same way it scopes the step rows: %r" % row)
    finally:
        os.unlink(os.path.join(room, "flow.jsonl"))
        os.rmdir(room)


def _selftest_undated_and_unattributed_arrests_are_dropped_and_counted():
    """#2034 review (HIGH, the two edges): an arrest that cannot be placed in the window, and one that
    cannot be placed on a row, are both dropped rather than shown -- and both are COUNTED, because
    neither is visible in the table that omits them.

    Polarity in both directions on the undated arm: it is kept when there is no window to violate and
    dropped when there is one. A filter that always dropped it would pass a one-sided assertion.

    The two null-Adapter arrests are the other discriminating pair -- one inside the window and one
    outside it. Both counts are reported beside a SCOPED table, so a windowed run must tally only the
    one its window covers rather than announcing an arrest from outside the window as something the
    table is hiding.
    """
    since = _parse_time("2026-09-05T00:00:00Z")
    inside = ("codex", "TokenBudget", _parse_time("2026-09-06T00:00:00Z"))
    undated_arrest = ("codex", "TokenBudget", None)
    null_adapter_inside = (None, "TokenBudget", _parse_time("2026-09-06T00:00:00Z"))
    null_adapter_outside = (None, "TokenBudget", _parse_time("2026-09-04T00:00:00Z"))
    every = [inside, undated_arrest, null_adapter_inside, null_adapter_outside]

    kept, undated, unattributed = arrests_in_window(every, None)
    assert (len(kept), undated, unattributed) == (2, 0, 2), (
        "unwindowed: the undated arrest is kept (no scope to violate) and both null-Adapter arrests "
        "are dropped and counted -- %r" % (kept,))

    kept, undated, unattributed = arrests_in_window(every, since)
    assert (len(kept), undated, unattributed) == (1, 1, 1), (
        "windowed: the undated arrest is dropped and counted, not printed under a scope it may not "
        "belong to; and only the IN-window null-Adapter arrest is announced -- %r" % (kept,))


def _selftest_headroom_compares_the_live_meter_and_never_fabricates_a_zero():
    """#2034: the ceiling comparison is against the LIVE Σ (what TokenBudgetMonitor arrests on), and a
    step that recorded no usage is excluded from the distribution rather than counted as 0.

    The discriminating arm is the third step: it has no figures at all. Counting it as zero would
    drag p50 down and make a tight ceiling read as roomy -- the failure mode that makes this whole
    measurement lie in the safe-looking direction.
    """
    steps = [
        {"room": "a", "step": "review", "adapter": "claude", "state": "Succeeded",
         "settledAt": None, "bindingCeiling": None, "live": 100000, "posthoc": 240000},
        {"room": "b", "step": "review", "adapter": "claude", "state": "Succeeded",
         "settledAt": None, "bindingCeiling": None, "live": 200000, "posthoc": 480000},
        {"room": "c", "step": "review", "adapter": "claude", "state": "Failed",
         "settledAt": None, "bindingCeiling": None, "live": None, "posthoc": None},
    ]
    row = budget_headroom_rows(steps, [], {"review": {"token_budget": {"claude": 250000}}})[0]
    # #2034 review: `n` is the distribution's own denominator (2), `steps` the settled steps behind
    # the row (3). Printing only the latter as `n` is what made a p95 read as better-sampled than it is.
    assert row["steps"] == 3 and row["measured"] == 2, row
    assert row["states"] == "Failed:1,Succeeded:2", row
    assert row["liveP50"] == 100000 and row["liveMax"] == 200000, row
    assert row["ratioP50"] == 2.4, row
    # 200000 of a 250000 ceiling, off the LIVE figure. Off `posthoc` it would read 192 %, which is a
    # different claim about a quantity no monitor compares to a budget.
    assert row["p95PercentOfCeiling"] == 80.0, row


def _selftest_the_corpus_walk_counts_every_room_it_could_not_read():
    """#2034 review: a room the walk drops must be COUNTED, because the argument this table is built to
    support is "no row for that vendor exists, therefore no such lane has run" -- and a silently
    dropped room is exactly the evidence that argument cannot afford to lose.

    The control read first is the good room: it must produce its row in both runs. Without it a walk
    that dropped everything and counted it would satisfy every assertion below.

    Polarity on the undatable room, which is the one that behaves differently by run: a settled room
    with no `terminalAt` is a perfectly good row in an unwindowed run and undroppable-but-unplaceable
    in a windowed one, so it is counted only where it is dropped.
    """
    import shutil
    import tempfile

    root = tempfile.mkdtemp()
    try:
        def room(name, bindings, terminal):
            path = os.path.join(root, name)
            os.mkdir(path)
            for filename, payload in (("bindings.json", bindings), ("terminal.json", terminal)):
                if payload is None:
                    continue
                with open(os.path.join(path, filename), "w", encoding="utf-8") as handle:
                    handle.write(payload if isinstance(payload, str) else json.dumps(payload))

        step = {"id": "review", "state": "Succeeded", "usage": {"liveBilledTokens": 1, "billedTokens": 1}}
        binding = {"review": {"Adapter": "codex", "TokenBudget": 250000}}
        room("good", binding, {"terminalAt": "2026-09-06T00:00:00Z", "steps": [step]})
        room("no-bindings", None, {"terminalAt": "2026-09-06T00:00:00Z", "steps": [step]})
        room("unparseable", "{not json", {"terminalAt": "2026-09-06T00:00:00Z", "steps": [step]})
        room("still-running", binding, None)
        room("undatable", binding, {"terminalAt": None, "steps": [step]})

        rows, skipped = settled_steps(root)
        assert len(rows) == 2, "control: the good and undatable rooms both contribute unwindowed: %r" % rows
        assert skipped == {"unreadable": 2, "unsettled": 1, "undatable": 0}, skipped

        rows, skipped = settled_steps(root, _parse_time("2026-09-05T00:00:00Z"))
        assert len(rows) == 1 and rows[0]["room"] == "good", rows
        assert skipped == {"unreadable": 2, "unsettled": 1, "undatable": 1}, (
            "a windowed run drops the undatable room and must say so: %r" % skipped)
    finally:
        shutil.rmtree(root, ignore_errors=True)


def _selftest_ceiling_prefers_the_figure_that_actually_armed_the_monitor():
    """#2034 review: when the room's own `bindings.json` carries the resolved TokenBudget, THAT is the
    ceiling the percentage is taken against -- not today's catalog, which no lane in the row ran under.

    Three arms, and the first two are the polarity pair. A `--token-budget 400000` override on a 250k
    role must read 50 % (against what armed the monitor), never 80 % (against the catalog); the same
    steps with no binding figure must fall back to the catalog and read 80 %, or the first arm would
    pass under a resolver that had simply stopped reading the catalog. The third arm is the control
    that stops a mixed row from averaging into a single number: an unwatched lane beside a watched one
    is a DIFFERENT ceiling, not a missing one, and a row spanning both reports no percentage at all.
    """
    def step(room, binding_ceiling):
        return {"room": room, "step": "review", "adapter": "claude", "state": "Succeeded",
                "settledAt": None, "bindingCeiling": binding_ceiling, "live": 200000,
                "posthoc": 200000}

    roles = {"review": {"token_budget": {"claude": 250000}}}

    row = budget_headroom_rows([step("a", 400000), step("b", 400000)], [], roles)[0]
    assert (row["ceiling"], row["ceilingFrom"], row["p95PercentOfCeiling"]) == (400000, "binding", 50.0), (
        "the override that armed TokenBudgetMonitor is the ceiling, not the catalog's 250000: %r" % row)

    row = budget_headroom_rows([step("a", None), step("b", None)], [], roles)[0]
    assert (row["ceiling"], row["ceilingFrom"], row["p95PercentOfCeiling"]) == (250000, "catalog", 80.0), (
        "with no binding figure the row falls back to today's catalog and says so: %r" % row)

    row = budget_headroom_rows([step("a", 400000), step("b", None)], [], roles)[0]
    assert (row["ceiling"], row["ceilingFrom"], row["p95PercentOfCeiling"]) == (CEILING_MIXED, "binding", None), (
        "an unwatched lane must not hide inside a watched row's percentage: %r" % row)


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
             _selftest_since_scopes_the_arrest_columns_and_not_only_the_step_rows,
             _selftest_undated_and_unattributed_arrests_are_dropped_and_counted,
             _selftest_the_corpus_walk_counts_every_room_it_could_not_read,
             _selftest_ceiling_prefers_the_figure_that_actually_armed_the_monitor,
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
                        help="with --budget-headroom, scope the whole table to this instant -- how "
                             "you ask about the lanes that ran after a metering fix landed. Step rows "
                             "are kept per ROOM (its terminalAt), arrest columns per EVENT (each "
                             "arrest's own WriterUtcTimestamp); the two granularities are printed "
                             "under the table rather than left to be inferred")
    parser.add_argument("--roles", default=os.path.join(
        os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
        "src", "Baton.Vendors", "WorkerRoles.json"),
        help="the FALLBACK role catalog for the ceiling column (default: this repo's "
             "WorkerRoles.json). Only consulted for a row whose lanes carried no resolved "
             "TokenBudget on their own bindings.json; the `from` column says which was used")
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
