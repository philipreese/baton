// Executable tests for tools/fleet-glass/worker.core.mjs -- the pure functions worker.js's
// paging (deliverables_list, fleet_status) and heartbeat-merge logic are built from (#1656, F2 --
// 2026-09-02 review). No JS test runner exists in this repo; this is a standalone `node` script,
// same `check`/failures-list pattern pusher.py's own `_selftest` already uses.
//
// Run: `node tools/fleet-glass/worker.selftest.mjs` (pixi task: fleet-glass-worker-selftest).

import {
  encodeDeliverablesCursor,
  decodeDeliverablesCursor,
  computeDeliverablesPage,
  computeFleetStatusPage,
  computeDeliverBatch,
  deliverableBatchKeyFor,
  deliverableReadOutcome,
  isValidFleetStatusPage,
  maxIsoOrNull,
  projectionStaleness,
  PROJECTION_STALE_AFTER_MS,
  PROJECTION_UNREACHABLE_AFTER_MS,
  classifyKvError,
  nextUtcMidnightIso,
} from "./worker.core.mjs";

const failures = [];
function check(name, cond) {
  if (!cond) failures.push(name);
}

// -- cursor round-trip --
{
  const item = { id: "abc123", pushed_at: "2026-09-02T07:00:00Z" };
  const cursor = encodeDeliverablesCursor(item);
  const decoded = decodeDeliverablesCursor(cursor);
  check("cursor round-trips id", decoded && decoded.id === "abc123");
  check("cursor round-trips pushedAt", decoded && decoded.pushedAt === "2026-09-02T07:00:00Z");
}
{
  // An item with no pushed_at still encodes/decodes -- worker.js's handleDeliver always stamps one,
  // but computeDeliverablesPage/encodeDeliverablesCursor make no such assumption themselves.
  const item = { id: "no-pushed-at" };
  const decoded = decodeDeliverablesCursor(encodeDeliverablesCursor(item));
  check("cursor round-trips an item with no pushed_at (encodes as empty string)",
        decoded && decoded.id === "no-pushed-at" && decoded.pushedAt === "");
}

// -- malformed cursor degrades to the start, never throws --
check("garbage (non-base64) cursor decodes to null", decodeDeliverablesCursor("!!!not-base64!!!") === null);
check("valid base64 of non-JSON decodes to null", decodeDeliverablesCursor(btoa("not json")) === null);
check("valid JSON missing 'id' decodes to null", decodeDeliverablesCursor(btoa(JSON.stringify({ pushedAt: "x" }))) === null);
{
  const index = [
    { id: "a", pushed_at: "2026-09-02T07:03:00Z" },
    { id: "b", pushed_at: "2026-09-02T07:02:00Z" },
    { id: "c", pushed_at: "2026-09-02T07:01:00Z" },
  ];
  const withMalformedCursor = computeDeliverablesPage(index, 2, "garbage-cursor");
  check("a malformed cursor restarts the page from the beginning, not a crash",
        withMalformedCursor.items.length === 2 && withMalformedCursor.items[0].id === "a");
}

// -- limit respected, count = filtered total, next_cursor null at the end --
{
  const index = Array.from({ length: 7 }, (_, i) => ({ id: `id-${i}`, pushed_at: `2026-09-02T07:0${i}:00Z` }));
  const page1 = computeDeliverablesPage(index, 3, undefined);
  check("limit is respected: page1 has exactly 3 items", page1.items.length === 3);
  check("count is the filtered total, not the page size", page1.count === 7);
  check("next_cursor is set when more items remain", page1.next_cursor !== null);

  const page2 = computeDeliverablesPage(index, 3, page1.next_cursor);
  check("page2 continues where page1 left off", page2.items[0].id === "id-3");
  check("page2 count still reflects the filtered total", page2.count === 7);

  const page3 = computeDeliverablesPage(index, 3, page2.next_cursor);
  check("the final page holds the remainder (1 item, not 3)", page3.items.length === 1 && page3.items[0].id === "id-6");
  check("next_cursor is null once the page is exhausted", page3.next_cursor === null);

  const noCursorNeeded = computeDeliverablesPage(index, 200, undefined);
  check("a limit above the total still returns next_cursor null (nothing left)",
        noCursorNeeded.items.length === 7 && noCursorNeeded.next_cursor === null);
  check("limit is clamped at 200 even when a caller asks for more",
        computeDeliverablesPage(index, 9999, undefined).items.length <= 200);
  check("a non-positive/absent limit defaults to 50",
        computeDeliverablesPage(index, 0, undefined).items.length === 7 // 7 < default 50, whole list
        && computeDeliverablesPage(index, -5, undefined).items.length === 7);
}

// -- 1,000 synthetic entries: served page stays under the limit and under 64 KB --
{
  const bigIndex = Array.from({ length: 1000 }, (_, i) => ({
    id: `synthetic-${i}`,
    room: "/r/synthetic",
    room_name: "synthetic-room",
    artifact: "report.md",
    title: `Synthetic deliverable number ${i}`,
    pushed_at: new Date(Date.UTC(2026, 8, 2, 0, 0, i)).toISOString(),
    content_hash: "0".repeat(64),
    withheld: false,
  }));
  const page = computeDeliverablesPage(bigIndex, 50, undefined);
  check("1,000-entry index: served page stays at/under the requested limit", page.items.length <= 50);
  check("1,000-entry index: count reports the full 1,000, not the page size", page.count === 1000);
  const bytes = Buffer.byteLength(JSON.stringify(page), "utf8");
  check(`1,000-entry index: served page body stays under 64 KB (was ${bytes} bytes)`, bytes < 64 * 1024);

  // Paging all the way through 1,000 entries at the default limit (50) never exceeds the limit per
  // page and terminates (next_cursor eventually null) -- the same code path glass.html's "load
  // more" drives.
  let cursor;
  let pages = 0;
  let seen = 0;
  do {
    const p = computeDeliverablesPage(bigIndex, undefined, cursor);
    check(`page ${pages}: never exceeds the default limit of 50`, p.items.length <= 50);
    seen += p.items.length;
    cursor = p.next_cursor;
    pages += 1;
  } while (cursor != null && pages < 100);
  check("paging through all 1,000 entries visits every one exactly once", seen === 1000);
  check("paging through 1,000 entries at limit 50 takes exactly 20 pages", pages === 20);
}

// -- fleet_status page computation: limit respected, terminal_total, next_page null at the end --
{
  const archive = Array.from({ length: 95 }, (_, i) => ({ path: `/r/term-${i}` }));
  const page0 = computeFleetStatusPage(archive, 0, 40);
  check("fleet_status page 0 respects the limit", page0.rooms.length === 40);
  check("fleet_status terminal_total is the full archive size", page0.terminal_total === 95);
  check("fleet_status next_page advances when more remain", page0.next_page === 1);

  const page2 = computeFleetStatusPage(archive, 2, 40);
  check("fleet_status page 2 holds the remainder (15 items, not 40)", page2.rooms.length === 15);
  check("fleet_status next_page is null once exhausted", page2.next_page === null);

  check("fleet_status limit is clamped at 200", computeFleetStatusPage(archive, 0, 9999).limit === 200);
  check("fleet_status limit defaults to 50 on a non-positive/absent limit",
        computeFleetStatusPage(archive, 0, 0).limit === 50 && computeFleetStatusPage(archive, 0, undefined).limit === 50);
}

// -- #1155: pusher.py's own `pruned` field (see pusher.py's pruned_info_for_room/attach_pruned_info
// selftest) rides through computeFleetStatusPage untouched -- this function only slices the archive,
// it never allowlists per-room fields, so a room carrying `pruned` keeps it on every page. This is
// the "room drill-in exposes the item list" half of #1155: fleet_status (and the paged archive) hand
// back the whole room object, `pruned` included, with no separate pass-through code needed.
{
  const prunedRoom = {
    path: "/r/pruned-room",
    pruned: { count: 25, items: [{ name: "execution_24", bytes: 19, prunedAt: "2026-09-01T00:00:00Z" }] },
  };
  const plainRoom = { path: "/r/plain-room" };
  const archive = [prunedRoom, plainRoom];
  const page = computeFleetStatusPage(archive, 0, 40);
  check("computeFleetStatusPage passes a room's `pruned` field through untouched",
        JSON.stringify(page.rooms[0].pruned) === JSON.stringify(prunedRoom.pruned));
  check("computeFleetStatusPage never fabricates a `pruned` field on a room that has none",
        !("pruned" in page.rooms[1]));
}

// -- fleet_status page/limit bad-input degrades to default (isValidFleetStatusPage gate) --
check("isValidFleetStatusPage rejects a missing page", isValidFleetStatusPage(undefined) === false);
check("isValidFleetStatusPage rejects a non-number page", isValidFleetStatusPage("0") === false);
check("isValidFleetStatusPage rejects a negative page", isValidFleetStatusPage(-1) === false);
check("isValidFleetStatusPage rejects NaN", isValidFleetStatusPage(NaN) === false);
check("isValidFleetStatusPage rejects Infinity", isValidFleetStatusPage(Infinity) === false);
check("isValidFleetStatusPage accepts a valid page", isValidFleetStatusPage(0) === true);

// -- heartbeat merge picks the fresher of heartbeat/pushed_at, both directions --
check("a fresher pushed_at pulls the merged heartbeat forward",
      maxIsoOrNull("2026-09-02T07:11:28Z", "2026-09-02T07:34:00Z") === "2026-09-02T07:34:00Z");
check("(control, other direction) a fresher heartbeat wins when pushed_at is older",
      maxIsoOrNull("2026-09-02T07:34:00Z", "2026-09-02T07:11:28Z") === "2026-09-02T07:34:00Z");
check("an older pushed_at never regresses the merged heartbeat",
      maxIsoOrNull("2026-09-02T07:11:28Z", "2026-09-02T06:00:00Z") === "2026-09-02T07:11:28Z");
check("a quiet fleet (no push at all) still shows the raw heartbeat",
      maxIsoOrNull("2026-09-02T07:11:28Z", null) === "2026-09-02T07:11:28Z");
check("no heartbeat recorded yet, but a push has landed: the push time is still an honest signal",
      maxIsoOrNull(null, "2026-09-02T07:34:00Z") === "2026-09-02T07:34:00Z");
check("neither heartbeat nor push recorded yet: stays absent, never fabricated",
      maxIsoOrNull(null, null) === null);

// -- #1981: the daemon-hung banner condition, both arms and both polarities --
{
  const iso = (ms) => new Date(ms).toISOString();
  const now = Date.UTC(2026, 8, 6, 19, 0, 0);

  // (a) HUNG: the projection was already 5 minutes old when the fleet machine last reported in.
  // This is the 2026-09-06 incident's own shape -- the pusher kept pinging, the daemon did not
  // write. Measured contact-to-derivation, so it fires at 90s without waiting for a delivery.
  const hung = projectionStaleness(iso(now - 6 * 60_000), iso(now - 60_000), now);
  check("(#1981 arm a) a projection already stale at the fleet machine's last contact reads stale",
        hung.stale === true && hung.reason === "hung");
  check("(#1981 arm a) the reported age is the contact-to-derivation gap, not now-to-derivation",
        hung.ageMs === 5 * 60_000);

  // (a control) THE FALSE-FIRE THIS ARM EXISTS TO AVOID: a healthy daemon on a quiet fleet, whose
  // derived_at is 4 minutes old only because pusher.py's 300s ping cadence hasn't delivered a
  // fresher one yet. The gap AT CONTACT is one tick, so nothing fires -- a now-keyed 90s threshold
  // would have lit this up permanently (the #1613 false-fire, repeated).
  const quietButHealthy = projectionStaleness(iso(now - 4 * 60_000), iso(now - 4 * 60_000 + 30_000), now);
  check("(#1981 arm a control) a healthy daemon whose derived_at is merely UNDELIVERED does not fire",
        quietButHealthy.stale === false && quietButHealthy.reason === null);

  // (a boundary, both sides of the 3-tick threshold)
  check("(#1981 arm a) exactly at the 3-tick threshold is not yet stale (strictly greater fires)",
        projectionStaleness(iso(now - 60_000), iso(now - 60_000 + PROJECTION_STALE_AFTER_MS), now).stale === false);
  check("(#1981 arm a) one millisecond past the 3-tick threshold fires",
        projectionStaleness(iso(now - 60_000), iso(now - 60_000 + PROJECTION_STALE_AFTER_MS + 1), now).reason === "hung");

  // (b) UNREACHABLE: nothing fresh has arrived at all -- the pusher died alongside the daemon, so
  // arm (a) is frozen at whatever gap it last saw and would stay quiet forever.
  const bothDead = projectionStaleness(iso(now - 30 * 60_000), iso(now - 30 * 60_000 + 5_000), now);
  check("(#1981 arm b) a frozen contact AND a frozen derivation still reads stale",
        bothDead.stale === true && bothDead.reason === "unreachable");
  check("(#1981 arm b) its age is measured against the reader's own clock", bothDead.ageMs === 30 * 60_000);

  // (b control) under the ping cadence + slop, a delivery that simply hasn't happened yet is quiet.
  check("(#1981 arm b control) a derived_at younger than the ping cadence + slop does not fire",
        projectionStaleness(iso(now - (PROJECTION_UNREACHABLE_AFTER_MS - 1)), null, now).stale === false);
  check("(#1981 arm b) past the ping cadence + slop it fires even with no contact timestamp at all",
        projectionStaleness(iso(now - (PROJECTION_UNREACHABLE_AFTER_MS + 1)), null, now).reason === "unreachable");

  // Never a fabricated verdict: an absent/unparseable derived_at has its own banner already.
  check("(#1981) a missing derived_at yields no verdict at all, never stale:false",
        projectionStaleness(null, iso(now), now) === null
        && projectionStaleness("not-a-date", iso(now), now) === null
        && projectionStaleness(undefined, iso(now), now) === null);
  check("(#1981) an unparseable CONTACT timestamp degrades to arm (b) alone, never throws",
        projectionStaleness(iso(now - 60_000), "not-a-date", now).stale === false
        && projectionStaleness(iso(now - 30 * 60_000), "not-a-date", now).reason === "unreachable");
  check("(#1981) a derived_at stamped slightly ahead of the reader's clock floors at 0, never negative",
        projectionStaleness(iso(now + 2_000), iso(now + 1_000), now).ageMs === 0);
}

// -- #1690 item 2: deliver batching folds K items into ONE inbox:batch:<id> blob + the index --
{
  const items = [
    { id: "d1", room: "/r/a", content: "content one" },
    { id: "d2", room: "/r/a", content: "content two" },
  ];
  const { index, batchContent, stored, evicted } = computeDeliverBatch([], items, "batch-1", 500);
  check("computeDeliverBatch stores every item's content under one batch object", stored === 2);
  check("the batch content carries both items keyed by id",
        batchContent.d1 === "content one" && batchContent.d2 === "content two");
  check("the index stamps each entry with the batch id it lives in",
        index.every((m) => m.batch_id === "batch-1"));
  check("the index carries no raw content (metadata only)",
        index.every((m) => !("content" in m)));
  check("nothing evicted under the cap", evicted.length === 0);
}
{
  // A malformed item (no id, or no room) is skipped, same posture worker.js's old inline loop had.
  const items = [
    { id: "ok", room: "/r/a", content: "x" },
    { room: "/r/a", content: "no id" },
    { id: "no-room", content: "x" },
  ];
  const { batchContent, stored } = computeDeliverBatch([], items, "batch-2", 500);
  check("a malformed item (missing id or room) is skipped, not stored", stored === 1 && Object.keys(batchContent).length === 1);
}
{
  // A second /deliver POST for an id already in the index replaces it (same dedupe-by-id semantics
  // the pre-#1690 inline loop had via index.filter(...).unshift(...)).
  const existing = [{ id: "d1", room: "/r/a", batch_id: "old-batch", pushed_at: "2026-09-01T00:00:00Z" }];
  const { index, orphanedBatchIds } = computeDeliverBatch(existing, [{ id: "d1", room: "/r/a", content: "v2" }], "new-batch", 500);
  check("re-delivering an existing id replaces its index entry (new batch_id, not duplicated)",
        index.length === 1 && index[0].batch_id === "new-batch");
  check("(F5) re-delivering an id orphans its PREVIOUS batch blob -- nothing else references " +
        "'old-batch' any more, so it comes back for worker.js to delete",
        orphanedBatchIds.includes("old-batch"));
}
{
  // (F5 control) re-delivering an id whose previous batch is STILL referenced by another,
  // non-evicted index entry must NOT orphan it -- two ids can share one batch blob.
  const existing = [
    { id: "d1", room: "/r/a", batch_id: "shared-batch", pushed_at: "t1" },
    { id: "d2", room: "/r/a", batch_id: "shared-batch", pushed_at: "t2" },
  ];
  const { orphanedBatchIds } = computeDeliverBatch(existing, [{ id: "d1", room: "/r/a", content: "v2" }], "new-batch", 500);
  check("(F5 control) a batch still referenced by another surviving entry is NOT reported orphaned",
        !orphanedBatchIds.includes("shared-batch"));
}
{
  // INBOX_CAP eviction still fires the same way, just over batch-stamped entries.
  const existing = Array.from({ length: 5 }, (_, i) => ({ id: `old-${i}`, room: "/r/a", batch_id: `b-${i}`, pushed_at: `t${i}` }));
  const { index, evicted, orphanedBatchIds } = computeDeliverBatch(existing, [{ id: "new-1", room: "/r/a", content: "x" }], "b-new", 3);
  check("INBOX_CAP eviction trims the index to the cap", index.length === 3);
  check("eviction returns exactly the overflowed entries for the caller to clean up",
        evicted.length === 3 && evicted.every((m) => m.id.startsWith("old-")));
  check("(F5) every evicted entry's OWN batch id (unshared with any surviving entry) is reported " +
        "orphaned, for worker.js to reclaim the blob",
        evicted.every((m) => orphanedBatchIds.includes(m.batch_id)));
}
{
  // F12 (2026-09-02 review): two items sharing an id within ONE POST must not double-count `stored`
  // -- the filter-then-unshift above collapses them to a single index entry.
  const items = [
    { id: "dup", room: "/r/a", content: "first" },
    { id: "dup", room: "/r/a", content: "second" },
  ];
  const { index, stored } = computeDeliverBatch([], items, "batch-dup", 500);
  check("(F12) a duplicate id within one POST produces exactly ONE index entry", index.length === 1);
  check("(F12) `stored` counts distinct ids, not loop iterations -- was double-counting this case",
        stored === 1);
}

// -- #1690 item 2: deliverable_read's id -> batch resolution --
{
  const index = [
    { id: "batched-1", batch_id: "batch-a" },
    { id: "legacy-1" }, // no batch_id -- delivered before #1690
  ];
  check("an item with a batch_id resolves to its inbox:batch:<id> key",
        deliverableBatchKeyFor(index, "batched-1") === "inbox:batch:batch-a");
  check("an item with no batch_id (legacy, pre-#1690) resolves to null -- caller falls back to inbox:item:<id>",
        deliverableBatchKeyFor(index, "legacy-1") === null);
  check("an id absent from the index entirely also resolves to null, never throws",
        deliverableBatchKeyFor(index, "nonexistent") === null);
}

// -- F8 (2026-09-02 review): deliverable_read's outcome must distinguish "known but not yet
// replicated" from a genuinely nonexistent id -- KV's eventual consistency across colos means the
// index can propagate before the batch blob does.
check("(F8) content found: reports found=true with the content, regardless of batchKey",
      deliverableReadOutcome("the content", "inbox:batch:x").found === true
      && deliverableReadOutcome("the content", "inbox:batch:x").content === "the content");
check("(F8) content missing but the index claims a batch_id: pending, not not-found",
      deliverableReadOutcome(null, "inbox:batch:x").found === false
      && deliverableReadOutcome(null, "inbox:batch:x").pending === true);
check("(F8 control) content missing AND no batchKey (genuinely unknown id): not-found, not pending",
      deliverableReadOutcome(null, null).found === false
      && deliverableReadOutcome(null, null).pending === false);

// -- #1712: classifyKvError recognizes the exact daily-limit message on the exact object shape a
// caught KV error carries; anything else stays unclassified rather than false-positiving into a 429.
check("classifyKvError classifies the exact daily-limit message",
      classifyKvError(new Error("KV put() limit exceeded for the day.")) === "kv-write-cap");
check("classifyKvError classifies the message even wrapped with extra context",
      classifyKvError(new Error("write failed: KV put() limit exceeded for the day.")) === "kv-write-cap");
check("(control) classifyKvError leaves an unrelated KV error unclassified",
      classifyKvError(new Error("KV GET failed: network timeout")) === null);
check("(control) classifyKvError leaves a non-Error input unclassified, never throws",
      classifyKvError("plain string") === null && classifyKvError(null) === null && classifyKvError(undefined) === null);

// -- nextUtcMidnightIso: always the NEXT 00:00 UTC strictly after now, never the same instant --
check("nextUtcMidnightIso from mid-day lands on the same day's next midnight",
      nextUtcMidnightIso(Date.UTC(2026, 8, 2, 20, 2, 14)) === "2026-09-03T00:00:00.000Z");
check("nextUtcMidnightIso from exactly midnight still advances a full day (strictly after, never equal)",
      nextUtcMidnightIso(Date.UTC(2026, 8, 2, 0, 0, 0)) === "2026-09-03T00:00:00.000Z");

if (failures.length) {
  console.error(`worker.selftest.mjs: FAIL -- ${failures.length} check(s):`);
  for (const f of failures) console.error(`  !! ${f}`);
  process.exit(1);
}
console.log("worker.selftest.mjs: pass");
