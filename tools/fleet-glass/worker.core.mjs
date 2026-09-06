/**
 * Pure, testable core of tools/fleet-glass/worker.js's paging and heartbeat-merge logic (#1656,
 * F2 -- 2026-09-02 review). worker.js `import`s these functions rather than redefining them --
 * Cloudflare Workers support ES modules, and the deployed entry point stays worker.js (wrangler.toml's
 * `main`). Split out solely so tools/fleet-glass/worker.selftest.mjs can exercise the actual code
 * path with plain `node`, no live Cloudflare Worker or KV namespace needed.
 */

// #1656: deliverables_list's opaque cursor (full contract in spec/baton.md §6). `atob`/`btoa` are
// standard Workers runtime globals; Node also provides both as globals (18.16+ / current LTS).
export function encodeDeliverablesCursor(item) {
  return btoa(JSON.stringify({ pushedAt: item.pushed_at || "", id: item.id }));
}
export function decodeDeliverablesCursor(cursor) {
  try {
    const parsed = JSON.parse(atob(cursor));
    if (parsed && typeof parsed.id === "string" && parsed.id) {
      return { pushedAt: typeof parsed.pushedAt === "string" ? parsed.pushedAt : "", id: parsed.id };
    }
  } catch {
    // Malformed or foreign cursor -- degrade to the start, never throw.
  }
  return null;
}

// deliverables_list's page computation, pulled out of handleMcp's tools/call branch verbatim (see
// worker.js) so it can be exercised without a live KV-backed inbox index. `filtered` is the index
// AFTER any `room` filter has already been applied by the caller -- this function knows nothing
// about rooms, only paging. Returns the exact shape the tool response carries: `items`, `count`
// (filtered.length, the total after the room filter -- NOT the page size), and `next_cursor`
// (null once exhausted).
export function computeDeliverablesPage(filtered, rawLimit, cursor) {
  const limit = typeof rawLimit === "number" && rawLimit > 0 ? Math.min(Math.floor(rawLimit), 200) : 50;
  let startIndex = 0;
  if (typeof cursor === "string" && cursor) {
    const decoded = decodeDeliverablesCursor(cursor);
    if (decoded) {
      // #1656 F2 fix (2026-09-02 review, found while writing worker.selftest.mjs): the encoded
      // cursor names the FIRST item of the NEXT page (`nextItem = filtered[startIndex + limit]`
      // below), not the last item of the page just shown -- resuming at `foundAt + 1` skipped that
      // item on every single "load more" click. Resume AT the found index instead.
      const foundAt = filtered.findIndex((m) => m && m.id === decoded.id && (m.pushed_at || "") === decoded.pushedAt);
      startIndex = foundAt >= 0 ? foundAt : 0;
    }
  }
  const items = filtered.slice(startIndex, startIndex + limit);
  const nextItem = filtered[startIndex + limit];
  const nextCursor = nextItem ? encodeDeliverablesCursor(nextItem) : null;
  return { items, count: filtered.length, next_cursor: nextCursor };
}

// fleet_status's page computation over the FULL terminal archive, pulled out the same way. `page`
// is expected already validated by the caller (see isValidFleetStatusPage below) -- this function
// only computes the slice, page/limit clamping, and next_page.
export function computeFleetStatusPage(archive, rawPage, rawLimit) {
  const limit = typeof rawLimit === "number" && rawLimit > 0 ? Math.min(Math.floor(rawLimit), 200) : 50;
  const page = Math.floor(rawPage);
  const start = page * limit;
  const rooms = archive.slice(start, start + limit);
  return {
    rooms,
    page,
    limit,
    terminal_total: archive.length,
    next_page: start + limit < archive.length ? page + 1 : null,
  };
}

// Bad/missing `page` (non-number, negative, NaN, Infinity) degrades to the plain unpaged
// fleet_status response rather than crashing -- this is the gate worker.js's handleMcp checks
// before calling computeFleetStatusPage at all.
export function isValidFleetStatusPage(page) {
  return typeof page === "number" && Number.isFinite(page) && page >= 0;
}

// Both isoStrings this ever compares come from the same producer's datetime.isoformat() call
// (pusher.py), so a plain string comparison over two well-formed ISO-8601 UTC instants sorts the
// same as comparing the instants themselves -- no Date parsing, and no timezone-offset pitfall to
// get wrong. Either argument being absent/non-string degrades to "the other one, or null".
export function maxIsoOrNull(a, b) {
  const aOk = typeof a === "string" && a.length > 0;
  const bOk = typeof b === "string" && b.length > 0;
  if (aOk && bOk) return a > b ? a : b;
  if (aOk) return a;
  if (bOk) return b;
  return null;
}

// #1981: the daemon can hang with its process alive -- on 2026-09-06 it stopped writing its fleet
// projection for thirteen minutes while the scheduled task still reported Running, and this page kept
// rendering the frozen picture as if it were current. `derived_at` (in the default
// FLEET_GLASS_PROJECTION_SOURCE=file mode, the DAEMON's own write timestamp -- spec/baton.md §6) is
// the signal; the two thresholds below are what make reading it honest.
//
// PROJECTION_STALE_AFTER_MS is FleetProjectionWriter.StaleAfterTicks (3) x its default 30s tick --
// that C# symbol is the source, and this literal is the cross-language transcription the
// `stdoutTail`/`doingNow` port pairs already accept (there is no shared module across the boundary).
// A daemon run with a widened BATON_FLEET_PROJECTION_INTERVAL_SECONDS would need this widened too.
export const PROJECTION_STALE_AFTER_MS = 90 * 1000;
// The second arm has NO constant of its own, deliberately -- and this is the correction the
// 2026-09-06 review forced. It is measured against the reader's own clock, so it must sit above the
// cadence on which a fresh `derived_at` actually REACHES the mailbox, and that cadence is not a
// constant anywhere: pusher.py paces the derived-freshness ping adaptively against its heartbeat
// sub-budget (`adaptive_producer_interval_s`, `HEARTBEAT_DAILY_WRITES = 60`), so it starts a UTC day
// at ~1440s and widens from there -- DERIVED_PING_INTERVAL_SECONDS (300s) is only its FLOOR. A fixed
// 7-minute bound derived from that floor fired on a healthy, quiet fleet for roughly 17 of every 24
// minutes: the exact false-fire #1613 pulled the old pushed_at-keyed banner out for, and #1829
// demoted its successor to a neutral line for.
//
// So the pusher now reports the interval it actually coalesced to (`derived_ping_interval_s`) in the
// ping body, worker.js stores it beside `derived_at`, and this arm marks stale at 3x it. Three,
// matching FleetProjectionWriter's own StaleAfterTicks: one missed delivery is ordinary, three in a
// row is not. The reported value is `reported_ping_interval_s(adaptive_heartbeat_interval_s(...))` in
// pusher.py -- NOT any number in pusher.log: every "interval now Ns" line there carries the snapshot
// or deliver cadence (paced against SNAPSHOT_DAILY_WRITES / DELIVER_DAILY_WRITES), and the ping's own
// cadence has no log line at all, so the two differ ~288s vs ~1440s at the start of a UTC day.
//
// Three consequences worth stating rather than leaving a reader to infer:
//  - No reported cadence, no arm. An unredeployed pusher sends no `derived_ping_interval_s`, and this
//    fails QUIET (arm (a) still covers the incident this exists for) rather than falling back to a
//    guessed number, which is the guess that produced the defect above.
//  - On GRACEFUL depletion the bound self-widens. `adaptive_producer_interval_s` returns
//    `seconds_left_in_day / writes_left`, so the last cadence the pusher reports before its heartbeat
//    sub-budget runs out is already most of the remaining day -- and once it is out, `heartbeat_
//    allowed` stops the ping entirely and no fresher cadence arrives. Arm (b) is therefore effectively
//    off for the rest of that day instead of alarming about a pusher that is rationing writes on
//    purpose; a pusher that has genuinely died is what glass.html's HEARTBEAT_DEAD_MS banner owns,
//    and it ranks above this one.
//  - On a HARD 429 that argument does not hold, and this is the round-3 review's finding 3. A live
//    Cloudflare cap makes `mark_kv_write_cap_exhausted` pin all three sub-budgets in ONE step, so the
//    gate closes with the last reported cadence still narrow (300s at the floor, ~1440s typically).
//    `reported_ping_interval_s` is the pusher-side half of the fix -- a ping sent while the ledger
//    carries a live `resets_at` reports the whole remaining cap window, or omits the field when that
//    reset time is unknown -- but it can only report on a ping it is allowed to send, and during a
//    live cap it is allowed none. So the RESIDUAL, stated rather than papered over: between roughly
//    15 and 72 minutes after a hard 429, and until the cap resets, this arm can report `unreachable`
//    about a healthy daemon. glass.html shadows it (its row 8 catches that state first -- see the
//    precedence table there); `fleet_status` has no chain and serves it, so a conductor reading
//    `reason: "unreachable"` during a known cap window should read it as "no write has landed",
//    which is what it measures, and not as the hung daemon its own wording suggests.
export const PROJECTION_UNREACHABLE_CADENCE_MULTIPLE = 3;

// Two arms, one verdict, because neither covers the other:
//
//  (a) "hung"        -- `lastContactAt - derivedAt`: how stale the projection ALREADY WAS at the
//      moment the fleet machine last spoke to the mailbox. Insensitive to WHEN that POST arrived --
//      a delivery that took a minute does not age the gap it reports -- which is what lets this be
//      sensitive (90s) without false-firing on a quiet fleet whose next ping is 24 minutes out.
//      This is the arm that would have caught 2026-09-06 while the pusher kept pinging.
//  (b) "unreachable" -- `now - derivedAt`: fires when nothing fresh has arrived at all, which is
//      what (a) cannot see -- if the pusher dies alongside the daemon, `lastContactAt` freezes too
//      and (a) stays quiet forever at whatever gap it last saw.
//
// Clock note: `derivedAt` is stamped by the fleet machine, `lastContactAt` by the Worker (worker.js
// re-stamps pushed_at / heartbeat_at on arrival, see its /heartbeat and /push handlers), so arm (a)
// is NOT two stamps from one clock -- it carries a full fleet<->Cloudflare skew term plus however
// long the POST took, and arm (b) compares the reader's clock against the same fleet-machine stamp.
// Seconds either way between NTP-disciplined hosts; neither threshold is exact enough to shave, and
// both are chosen with room for that.
//
// Returns null -- never a fabricated verdict -- when `derivedAt` is missing or unparseable; that case
// is already its own banner ("No derivation timestamp yet"), and a `stale: true` here would double it.
export function projectionStaleness(derivedAt, lastContactAt, nowMs, pingIntervalMs = null,
                                    staleAfterMs = PROJECTION_STALE_AFTER_MS,
                                    cadenceMultiple = PROJECTION_UNREACHABLE_CADENCE_MULTIPLE) {
  const derivedMs = typeof derivedAt === "string" && derivedAt ? Date.parse(derivedAt) : NaN;
  if (!Number.isFinite(derivedMs)) return null;
  const contactMs = typeof lastContactAt === "string" && lastContactAt ? Date.parse(lastContactAt) : NaN;
  // Floored at 0: a projection stamped slightly ahead of the comparison instant is clock skew, not
  // negative staleness, and either way it is not stale.
  const ageAtContactMs = Number.isFinite(contactMs) ? Math.max(0, contactMs - derivedMs) : null;
  const ageMs = Math.max(0, nowMs - derivedMs);
  if (ageAtContactMs !== null && ageAtContactMs > staleAfterMs) {
    return { stale: true, reason: "hung", ageMs: ageAtContactMs };
  }
  // Absent/zero/negative cadence -> arm (b) is simply not armed (see the constant's comment).
  const unreachableAfterMs = typeof pingIntervalMs === "number" && Number.isFinite(pingIntervalMs) && pingIntervalMs > 0
    ? pingIntervalMs * cadenceMultiple
    : null;
  if (unreachableAfterMs !== null && ageMs > unreachableAfterMs) {
    return { stale: true, reason: "unreachable", ageMs };
  }
  return { stale: false, reason: null, ageMs };
}

// #1690 item 2: the pure core of handleDeliver's batching -- given the existing inbox index and the
// items in one /deliver POST, returns the updated index (each stored item stamped with the batch id
// it lives in), the single content blob to write under `inbox:batch:<batchId>`, and any INBOX_CAP
// eviction overflow. worker.js does only the actual `env.FLEET.put`/`delete` calls around this, so
// worker.selftest.mjs can exercise the real batching logic with plain node -- no live KV needed.
// This is the fold that turns a K-item POST from K+1 KV writes (one inbox:item:<id> put per item,
// pre-#1690) into 2 (one inbox:batch:<id> put for the whole batch, plus the index put).
// F8 (2026-09-02 review): the pure decision for deliverable_read's outcome, pulled out of
// handleMcp's tools/call branch (see worker.js) so it can be exercised without live KV -- same
// "worker.js does only the actual env.FLEET calls around this" split every other function in this
// file follows. `content` is whatever the batch-blob/legacy-key reads resolved to (null if neither
// hit); `batchKey` non-null means the INDEX itself claims this id exists (deliverableBatchKeyFor
// found a batch_id for it), which is what lets this tell "genuinely no such id" apart from "known,
// but KV's eventual consistency across colos hasn't propagated its blob yet" -- the index and the
// blob are two separate reads that can observably disagree for a short window.
export function deliverableReadOutcome(content, batchKey) {
  if (content !== null) return { found: true, content };
  if (batchKey) return { found: false, pending: true };
  return { found: false, pending: false };
}

// F5 (2026-09-02 review): refcount `inbox:batch:<id>` blobs so worker.js can reclaim ones no index
// entry references any more, instead of leaving them orphaned forever (unbounded KV storage growth
// -- storage has its own free-tier ceiling even though it isn't write-budgeted). A batch id can lose
// its last reference two ways: (1) eviction, when the index exceeds `inboxCap`, and (2) re-delivery
// of the SAME id under a new batch id, which re-stamps its index entry and would otherwise abandon
// the old batch it used to point at. `staleBatchIds` tracks both; `orphanedBatchIds` (returned
// alongside `evicted`) is the subset with no remaining reference anywhere in the FINAL index, for
// worker.js to `env.FLEET.delete`. Batches evict roughly contiguously, so the amortised cost is ~1
// delete per batch (DELIVER_BATCH_KV_WRITE_COST in pusher.py budgets a flat +1 for this).
export function computeDeliverBatch(existingIndex, items, batchId, inboxCap) {
  let index = existingIndex.slice();
  const batchContent = {};
  const staleBatchIds = new Set();
  for (const item of items) {
    if (!item || typeof item.id !== "string" || !item.id) continue;
    if (typeof item.room !== "string" || !item.room) continue;
    batchContent[item.id] = String(item.content ?? "");
    const { content: _content, ...meta } = item;
    const prior = index.find((m) => m.id === item.id);
    if (prior && typeof prior.batch_id === "string" && prior.batch_id && prior.batch_id !== batchId) {
      staleBatchIds.add(prior.batch_id);
    }
    index = index.filter((m) => m.id !== item.id);
    index.unshift({ ...meta, pushed_at: item.pushed_at || new Date().toISOString(), batch_id: batchId });
  }
  // F12 (2026-09-02 review): count DISTINCT ids actually stored -- two items sharing an id within
  // one POST filter-then-unshift down to a single index entry above, so counting `items.length`
  // (or incrementing per loop iteration) double-counted the duplicate; `stored` is what the
  // /deliver response reports back to the caller.
  const stored = Object.keys(batchContent).length;
  let evicted = [];
  if (index.length > inboxCap) {
    evicted = index.slice(inboxCap);
    index = index.slice(0, inboxCap);
  }
  for (const m of evicted) {
    if (typeof m.batch_id === "string" && m.batch_id) staleBatchIds.add(m.batch_id);
  }
  const referenced = new Set(index.filter((m) => typeof m.batch_id === "string").map((m) => m.batch_id));
  const orphanedBatchIds = [...staleBatchIds].filter((id) => !referenced.has(id) && id !== batchId);
  return { index, batchContent, stored, evicted, orphanedBatchIds };
}

// #1690 item 2, read side: which `inbox:batch:<id>` key (if any) currently holds `itemId`'s content,
// per the index's own `batch_id` stamp -- null means "not found, or delivered before this change",
// which worker.js's deliverable_read treats as "fall back to the legacy inbox:item:<id> key".
export function deliverableBatchKeyFor(index, itemId) {
  const meta = index.find((m) => m && m.id === itemId);
  return meta && typeof meta.batch_id === "string" && meta.batch_id ? `inbox:batch:${meta.batch_id}` : null;
}

// #1712: Cloudflare's free-tier KV namespace hits a HARD daily write cap distinct from the pusher's
// own #1690 soft ledger (spec/baton.md §6) -- measured live (`wrangler tail`, 2026-09-02) as every
// `env.FLEET.put` throwing this exact message. worker.js was letting that bubble up as a bare 500;
// this pure classifier is what lets every put path (push, heartbeat, deliver's index/batch/eviction
// puts) answer 429 instead, without each one re-matching the message text itself.
const KV_WRITE_CAP_MESSAGE = "KV put() limit exceeded for the day.";

export function classifyKvError(err) {
  const message = err && typeof err.message === "string" ? err.message : String(err ?? "");
  return message.includes(KV_WRITE_CAP_MESSAGE) ? "kv-write-cap" : null;
}

// The 429 body's `resets_at`: the next UTC midnight strictly after `nowMs`, ISO-8601 -- same instant
// shape as pusher.py's own `next_utc_midnight_iso` (this is the worker-side twin, not a shared
// import: this file has no dependency on the pusher's Python).
export function nextUtcMidnightIso(nowMs) {
  const now = new Date(nowMs);
  const next = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1, 0, 0, 0, 0));
  return next.toISOString();
}
