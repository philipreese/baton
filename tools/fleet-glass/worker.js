/**
 * Fleet Glass mailbox (aer-works/baton#1392 follow-on; moved into the repo by #1413).
 *
 * Cloudflare Worker, three faces:
 *  - POST /push/<PUSH_TOKEN>    : the operator's machine pushes the latest fleet snapshot (JSON,
 *    from the fleet_status derivation). Outbound-only from the machine; this Worker never connects
 *    back to it.
 *  - POST /heartbeat/<PUSH_TOKEN> : the operator's machine pings on TWO independent cadences that
 *    share this one route (#1486, extended by #1613 item 2): an hourly liveness beat, and a more
 *    frequent derived-freshness ping whenever a snapshot push hasn't already delivered a fresh
 *    `derived_at` recently -- see pusher.py's cadence comments for the write-budget arithmetic. The
 *    stored `at` is this Worker's own receipt time (no dependency on the pusher host's clock);
 *    `derived_at`, if the body carries one, is the pusher's own claim about when ITS OWN snapshot
 *    derivation last completed -- a fact only the pusher knows, so unlike `at` it is NOT
 *    re-stamped here. Lets a reader tell "fleet is quiet" (snapshot old, heartbeat fresh) apart
 *    from "pusher is dead" (both old) apart from "pusher alive but derivation stuck" (heartbeat
 *    fresh, derived_at old) -- see fleet_status below. `pending_push_age_s`, if the body carries
 *    one (a 2026-09-01 review finding), is likewise the pusher's own claim -- seconds since its
 *    last SUCCESSFUL snapshot push, present only while it has content waiting to go out. Lets a
 *    reader tell "derivation stuck" apart from a FOURTH state this route alone can distinguish:
 *    "derivation is healthy but every push keeps failing" (a 413 from the push cap, a 5xx, a
 *    network blip) -- derived_at stays fresh in that case, since it only reflects derivation, not
 *    delivery.
 *  - POST /deliver/<PUSH_TOKEN> : the operator's machine pushes deliverable(s) -- a terminal room's
 *    declared output artifact(s) plus its verdict summary -- for the inbox surface (#1413). Body is
 *    `{"items": [...]}`; see `handleDeliver` for the item shape.
 *  - POST /mcp/<READ_SEGMENT>   : a minimal stateless MCP server (Streamable HTTP, JSON-RPC 2.0)
 *    exposing three read-only tools: `fleet_status` (the last pushed snapshot, with `heartbeat_at`
 *    and `derived_at` merged in from the separate key below), `deliverables_list` (inbox index,
 *    newest-first, optionally filtered by room), and `deliverable_read` (one item's full content).
 *    Read auth is the unguessable URL segment -- same posture as the operator's private ntfy topics.
 *
 * #1712: every `env.FLEET.put` above (push, heartbeat, deliver's index/batch/eviction writes) is
 * wrapped so Cloudflare's own daily KV write cap answers `429 {"reason": "kv-write-cap", "resets_at"}`
 * (`worker.core.mjs`'s `classifyKvError`/`nextUtcMidnightIso`) instead of a bare 500 -- spec/baton.md
 * §6 has the incident and the three-layer fix (worker/pusher/glass) this is one third of.
 *
 * Storage, all in one KV namespace (#1690 folded this to ONE write per /push and TWO per /deliver
 * batch -- spec/baton.md §6, "Fleet Glass write budget", has the full arithmetic):
 *  - "snapshot"          : the fleet snapshot, verbatim JSON, carrying pushed_at so consumers can
 *                          render honest staleness; absent data renders as absent, never fabricated.
 *                          Also carries `derived_at` (#1613 item 2) whenever the pusher included one
 *                          in the push body -- NOT part of pusher.py's own snapshot_hash, so its
 *                          presence never gates the #1457 change-gate. Also carries `terminal_archive`
 *                          (#1656, folded in by #1690 item 2 -- previously its own KV key, written by
 *                          a second `env.FLEET.put` on every push that had one): the plain (no `page`)
 *                          fleet_status response strips it back out on the READ side so it never
 *                          inflates the everyday response; a paged call reads it out of this same
 *                          value instead of a separate key.
 *  - "heartbeat_at"      : JSON `{"at": ISO-8601, "derived_at"?: ISO-8601, "pending_push_age_s"?:
 *                          number, "derived_ping_interval_s"?: number}` (#1613 item 2 widened this
 *                          from a bare ISO-8601 string; `pending_push_age_s` was added by a
 *                          2026-09-01 review finding, `derived_ping_interval_s` by #1981's; a bare
 *                          string still reads back as a legacy `at` value, self-healing the moment
 *                          the next heartbeat lands). Deliberately NOT part of the "snapshot" value
 *                          or its hash -- none of these fields may ever count as a snapshot content
 *                          change and trigger the change-gate (#1457) to push early.
 *  - "inbox:index"       : JSON array of deliverable METADATA (no content), newest-first, capped at
 *                          INBOX_CAP entries -- what deliverables_list returns. Each entry carries a
 *                          `batch_id` (#1690 item 2) naming which "inbox:batch:<id>" blob holds its
 *                          content; an entry with no `batch_id` predates #1690 and resolves through
 *                          the legacy "inbox:item:<id>" key instead.
 *  - "inbox:batch:<id>"  : ONE JSON object `{itemId: content, ...}` per /deliver POST (#1690 item 2)
 *                          -- every item in that POST's content, keyed by item id, in a single KV
 *                          value. Replaces the pre-#1690 "inbox:item:<id>" per-item key (K+1 writes
 *                          for a K-item batch); deliverable_read resolves an id to its batch via the
 *                          index's `batch_id`, falling back to "inbox:item:<id>" for anything
 *                          delivered before this change. Refcounted (F5, 2026-09-02 review):
 *                          `computeDeliverBatch` returns `orphanedBatchIds` -- batch ids no
 *                          remaining index entry references after this POST's eviction or
 *                          re-delivery -- and handleDeliver deletes those blobs, so KV storage no
 *                          longer grows without bound the way the pre-fix (unreclaimed) version did.
 *  - "inbox:item:<id>"   : LEGACY (pre-#1690) per-item content key, one deliverable's full content
 *                          (or a withheld stub) -- still read as deliverable_read's fallback, never
 *                          written to by a current pusher.
 */

import {
  computeDeliverablesPage,
  computeFleetStatusPage,
  computeDeliverBatch,
  deliverableBatchKeyFor,
  deliverableReadOutcome,
  isValidFleetStatusPage,
  maxIsoOrNull,
  projectionStaleness,
  classifyKvError,
  nextUtcMidnightIso,
} from "./worker.core.mjs";

const INBOX_CAP = 500;

const TOOLS = [
  {
    name: "fleet_status",
    description:
      "Read-only snapshot of room statuses across the operator's baton fleet, as last pushed by the fleet machine. Includes pushed_at for snapshot staleness, heartbeat_at for pusher liveness, derived_at for snapshot-derivation health, and pending_push_age_s for push-delivery health -- these are independent (#1486, #1613 item 2, 2026-09-01 review): a quiet fleet lets pushed_at go stale on purpose (heartbeat_at tells that apart from a dead pusher), a fleet whose derivation keeps failing lets derived_at go stale even while heartbeat_at stays fresh, and a fleet whose PUSHES keep failing (derivation healthy) grows pending_push_age_s even while derived_at stays fresh. With no arguments, `rooms` carries every non-terminal room plus only the newest N terminal ones (terminal_total names the full terminal count) -- pass `page` (0-based) and optionally `limit` (default 50, max 200) to page through the REST of the terminal archive instead; a paged call's response carries rooms/page/limit/terminal_total/next_page (null once exhausted) and omits every other top-level field. `projection` (#1981, absent when derived_at is unknown) is `{stale, reason, ageMs}` for the DAEMON's own projection file: `stale` with reason `hung` means the fleet machine was already serving a frozen projection when it last reported in, and `unreachable` means nothing fresh has arrived here at all -- in either case the rooms below are an old picture.",
    inputSchema: {
      type: "object",
      properties: {
        page: { type: "number" },
        limit: { type: "number" },
      },
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true },
  },
  {
    name: "deliverables_list",
    description:
      "Newest-first index of lane deliverables pushed across rooms (title, room, artifact, pushed_at, content_hash, withheld). Optionally filtered to one room. Never carries content -- call deliverable_read for that. Paged: limit (default 50, max 200) and an opaque cursor from a prior call's next_cursor; response carries items, count (the total after any room filter), and next_cursor (null once exhausted).",
    inputSchema: {
      type: "object",
      properties: {
        room: { type: "string" },
        limit: { type: "number" },
        cursor: { type: "string" },
      },
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true },
  },
  {
    name: "deliverable_read",
    description:
      "Full content of one deliverable by id (from deliverables_list), rendered markdown or a withheld-secret stub.",
    inputSchema: {
      type: "object",
      properties: { id: { type: "string" } },
      required: ["id"],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true },
  },
];

function rpcResult(id, result) {
  return { jsonrpc: "2.0", id, result };
}
function rpcError(id, code, message) {
  return { jsonrpc: "2.0", id, error: { code, message } };
}
function json(body, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}
function toolText(text) {
  return { content: [{ type: "text", text }] };
}
// #1712: every KV put path answers this instead of a bare 500 once classifyKvError recognizes
// Cloudflare's daily write-cap message -- spec/baton.md §6.
function kvWriteCapResponse() {
  return json({ reason: "kv-write-cap", resets_at: nextUtcMidnightIso(Date.now()) }, 429);
}
function toolError(text) {
  return { content: [{ type: "text", text }], isError: true };
}

// #1613 item 2: "heartbeat_at" widened from a bare ISO-8601 string to a small JSON object so one
// key can carry both `at` (this Worker's own receipt time, unconditionally re-stamped on every
// /heartbeat POST) and `derived_at` (the pusher's own claim, taken from the POST body verbatim
// when present, never re-stamped -- see this file's header). `pending_push_age_s` (2026-09-01
// review finding) rides the same object, same "taken from the body verbatim, never re-stamped"
// rule -- it too is a fact only the pusher itself knows. Reads a pre-#1613 bare-string value back
// as a legacy `at` with no `derived_at`/`pending_push_age_s`, so an old stored value degrades
// gracefully instead of throwing; the next heartbeat overwrites it with the new shape either way.
function readStoredHeartbeat(raw) {
  if (!raw) return { at: null, derivedAt: null, pendingPushAgeS: null };
  try {
    const parsed = JSON.parse(raw);
    if (parsed && typeof parsed === "object") {
      return {
        at: parsed.at ?? null,
        derivedAt: parsed.derived_at ?? null,
        pendingPushAgeS: typeof parsed.pending_push_age_s === "number" ? parsed.pending_push_age_s : null,
        derivedPingIntervalS: typeof parsed.derived_ping_interval_s === "number"
          ? parsed.derived_ping_interval_s : null,
      };
    }
  } catch {
    // Falls through to the legacy bare-string reading below.
  }
  return { at: raw, derivedAt: null, pendingPushAgeS: null, derivedPingIntervalS: null };
}

async function readHeartbeat(env) {
  const raw = await env.FLEET.get("heartbeat_at");
  const { at, derivedAt, pendingPushAgeS, derivedPingIntervalS } = readStoredHeartbeat(raw);
  return { heartbeatAt: at, derivedAt, pendingPushAgeS, derivedPingIntervalS };
}

async function readInboxIndex(env) {
  const raw = await env.FLEET.get("inbox:index");
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    // A corrupt index must not wedge every future push -- start fresh rather than fail closed on
    // metadata (the SECRET gate below is what fails closed; this is a resilience nicety for it).
    return [];
  }
}

async function handleDeliver(request, env) {
  if (request.method !== "POST") return new Response(null, { status: 405 });
  const body = await request.text();
  if (body.length > 5_000_000) return new Response("too large", { status: 413 });
  let parsed;
  try {
    parsed = JSON.parse(body);
  } catch {
    return new Response("not json", { status: 400 });
  }
  const items = Array.isArray(parsed?.items) ? parsed.items : null;
  if (!items) return new Response("expected {\"items\": [...]}", { status: 400 });

  const existingIndex = await readInboxIndex(env);
  // #1690 item 2: one inbox:batch:<id> blob for the WHOLE POST plus the index -- 2 KV writes per
  // batch regardless of item count K (was K+1: one inbox:item:<id> put per item, pre-#1690).
  const batchId = crypto.randomUUID();
  const { index, batchContent, stored, evicted, orphanedBatchIds } = computeDeliverBatch(existingIndex, items, batchId, INBOX_CAP);

  // #1712: one try/catch around every write this POST makes -- the daily KV cap fails whichever
  // put or delete hits it first, and the rest would fail the same way, so there is nothing to gain
  // from classifying each call site separately.
  try {
    for (const m of evicted) {
      // A LEGACY (pre-#1690) evicted entry costs a delete: its content lives at its own
      // inbox:item:<id> key.
      if (!m.batch_id) await env.FLEET.delete(`inbox:item:${m.id}`);
    }
    // F5 (2026-09-02 review): reclaim a batched entry's underlying `inbox:batch:<id>` blob once NO
    // remaining index entry references it (eviction, or this same POST re-delivering an id under a
    // new batch id) -- pre-fix, these were left orphaned forever, growing KV storage without bound
    // even though storage isn't write-budgeted the way writes are.
    for (const orphanId of orphanedBatchIds) {
      await env.FLEET.delete(`inbox:batch:${orphanId}`);
    }
    if (stored > 0) {
      await env.FLEET.put(`inbox:batch:${batchId}`, JSON.stringify(batchContent));
    }
    await env.FLEET.put("inbox:index", JSON.stringify(index));
  } catch (err) {
    if (classifyKvError(err) === "kv-write-cap") return kvWriteCapResponse();
    throw err;
  }
  return json({ ok: true, stored, index_size: index.length });
}

async function handleMcp(request, env) {
  if (request.method === "GET") {
    // Streamable HTTP allows a server that does not offer a GET/SSE stream.
    return new Response(null, { status: 405 });
  }
  let msg;
  try {
    msg = await request.json();
  } catch {
    return json(rpcError(null, -32700, "parse error"), 400);
  }
  // Batch requests are not supported by this minimal server.
  if (Array.isArray(msg)) {
    return json(rpcError(null, -32600, "batch not supported"), 400);
  }
  const { id, method, params } = msg;
  if (method === "initialize") {
    return json(
      rpcResult(id, {
        protocolVersion: params?.protocolVersion ?? "2025-03-26",
        capabilities: { tools: {} },
        serverInfo: { name: "baton-fleet", version: "0.2.0" },
      }),
    );
  }
  if (method === "notifications/initialized") {
    return new Response(null, { status: 202 });
  }
  if (method === "tools/list") {
    return json(rpcResult(id, { tools: TOOLS }));
  }
  if (method === "tools/call") {
    const name = params?.name;
    if (name === "fleet_status") {
      const args = params?.arguments || {};
      // #1656: `page` pages over the FULL terminal-room archive -- a plain 0-based page index
      // rather than an opaque cursor (unlike deliverables_list below), since the archive is a
      // single append-mostly array with no independent per-item identity worth round-tripping. On
      // the SAME tool rather than a new one so worker.js's TOOLS array stays exactly the three
      // read-only names FleetGlassReadOnlyTests pins.
      if (isValidFleetStatusPage(args.page)) {
        // #1690 item 2: terminal_archive now lives INSIDE the "snapshot" KV value (no separate
        // "terminal_archive" key -- folding it in is what took the /push handler from 2 writes to
        // 1), so a paged call reads the same key the plain call does and pulls its own field out.
        const raw = await env.FLEET.get("snapshot");
        let archive = [];
        if (raw) {
          try {
            const parsed = JSON.parse(raw);
            archive = Array.isArray(parsed?.terminal_archive) ? parsed.terminal_archive : [];
          } catch {
            archive = [];
          }
        }
        return json(rpcResult(id, toolText(JSON.stringify(computeFleetStatusPage(archive, args.page, args.limit)))));
      }
      const stored = await env.FLEET.get("snapshot");
      const { heartbeatAt, derivedAt: derivedAtFromHeartbeat, pendingPushAgeS, derivedPingIntervalS } =
        await readHeartbeat(env);
      const storedSnapshot = stored === null ? null : JSON.parse(stored);
      // derived_at (#1613 item 2, spec/baton.md §6) can reach this Worker by two independent
      // routes: a snapshot push's own body, or a dedicated /heartbeat ping (see readHeartbeat).
      // Both stamp the SAME isoformat() shape from the SAME producer (pusher.py), so a plain
      // lexicographic string max is a sound "most recent" comparison -- no Date parsing needed.
      const derivedAt = maxIsoOrNull(storedSnapshot?.derived_at, derivedAtFromHeartbeat);
      // pending_push_age_s (2026-09-01 review finding) has only ONE route -- the heartbeat ping,
      // never the snapshot body itself (a snapshot that successfully pushed has nothing pending by
      // definition) -- so there is no second value to max against here.
      // #1656: fold pushed_at into the DISPLAYED heartbeat_at (same maxIsoOrNull merge derived_at
      // already uses) -- rationale and the bug this fixes: spec/baton.md §6.
      const heartbeatDisplayAt = maxIsoOrNull(heartbeatAt, storedSnapshot?.pushed_at);
      if (stored === null) {
        return json(rpcResult(id, toolText(JSON.stringify({ pushed_at: null, rooms: null, heartbeat_at: heartbeatAt, derived_at: derivedAt, pending_push_age_s: pendingPushAgeS, note: "no snapshot pushed yet" }))));
      }
      // #1690 item 2: terminal_archive rides inside storedSnapshot now (folded in on write), but the
      // PLAIN (no `page`) response must stay exactly as small as it was when it lived in its own key
      // -- stripped here, on the read side, rather than never stored. A paged call reads it back via
      // the isValidFleetStatusPage branch above.
      const { terminal_archive: _archive, ...restSnapshot } = storedSnapshot;
      // heartbeat_at/derived_at/pending_push_age_s are merged in at read time, never written into
      // the "snapshot" value itself -- that keeps them out of pusher.py's change-gate hash (see
      // this file's header).
      // #1981: computed HERE, not in glass.html, so the two thresholds and the arithmetic live in
      // one place (worker.core.mjs, exercised by worker.selftest.mjs) -- the page is an artifact
      // that cannot import a module, so a copy over there would be a second implementation nothing
      // tests. Merged in at read time like the three fields above, for the same reason: it must not
      // enter pusher.py's change-gate hash. Absent when derived_at is missing/unparseable.
      // The cadence argument is the pusher's own reported ping interval (2026-09-06 review finding
      // A): without it the "nothing fresh has arrived at all" arm stays unarmed rather than guessing.
      const projection = projectionStaleness(
        derivedAt, heartbeatDisplayAt, Date.now(),
        derivedPingIntervalS === null ? null : derivedPingIntervalS * 1000);
      const snapshot = { ...restSnapshot, heartbeat_at: heartbeatDisplayAt, derived_at: derivedAt, pending_push_age_s: pendingPushAgeS };
      if (projection) snapshot.projection = projection;
      return json(rpcResult(id, toolText(JSON.stringify(snapshot))));
    }
    if (name === "deliverables_list") {
      const index = await readInboxIndex(env);
      const room = params?.arguments?.room;
      const filtered = room ? index.filter((m) => m.room === room) : index;
      // #1656: paged the same way as fleet_status's terminal archive, but with an OPAQUE cursor
      // (base64 of {pushedAt, id}) rather than a page index -- the inbox index is mutated by every
      // /deliver POST (dedupe-by-id unshift, INBOX_CAP eviction), so a page-index cursor could skip
      // or repeat items across two calls; a cursor anchored to a specific item's own identity
      // degrades gracefully (falls back to the start) instead of returning a silently wrong slice.
      const page = computeDeliverablesPage(filtered, params?.arguments?.limit, params?.arguments?.cursor);
      return json(rpcResult(id, toolText(JSON.stringify(page))));
    }
    if (name === "deliverable_read") {
      const itemId = params?.arguments?.id;
      if (!itemId) return json(rpcResult(id, toolError("id is required")));
      // #1690 item 2: resolve id -> its inbox:batch:<id> blob via the index's own batch_id stamp,
      // falling back to the legacy per-item key for anything delivered before this change (or a
      // missing/corrupt batch blob).
      const index = await readInboxIndex(env);
      const batchKey = deliverableBatchKeyFor(index, itemId);
      let content = null;
      if (batchKey) {
        const batchRaw = await env.FLEET.get(batchKey);
        if (batchRaw) {
          try {
            const batch = JSON.parse(batchRaw);
            if (batch && Object.prototype.hasOwnProperty.call(batch, itemId)) content = batch[itemId];
          } catch {
            // Corrupt batch blob -- fall through to the legacy key below.
          }
        }
      }
      if (content === null) {
        content = await env.FLEET.get(`inbox:item:${itemId}`);
      }
      // F8 (2026-09-02 review): KV is eventually consistent across colos -- there is a real window
      // where the index (read above, via readInboxIndex) has propagated while its
      // inbox:batch:<id> blob has not, which is exactly how an operator can see an id in
      // deliverables_list and then have deliverable_read 404 on it a moment later.
      // deliverableReadOutcome tells that apart from a genuinely nonexistent id.
      const outcome = deliverableReadOutcome(content, batchKey);
      if (!outcome.found) {
        if (outcome.pending) {
          return json(rpcResult(id, toolError(
            `deliverable ${itemId} is known but its content has not replicated yet -- retry in a minute`)));
        }
        return json(rpcResult(id, toolError(`no deliverable with id ${itemId}`)));
      }
      return json(rpcResult(id, toolText(outcome.content)));
    }
    return json(rpcResult(id, toolError(`unknown tool: ${name}`)));
  }
  if (typeof method === "string" && method.startsWith("notifications/")) {
    return new Response(null, { status: 202 });
  }
  return json(rpcError(id ?? null, -32601, `method not found: ${method}`));
}

// Constant-time token compare: a plain !== leaks match-prefix length through timing. Network
// jitter makes that impractical to exploit against a Worker, but the fix costs nothing.
function tokenMatches(candidate, secret) {
  if (typeof candidate !== "string" || typeof secret !== "string") return false;
  const enc = new TextEncoder();
  const a = enc.encode(candidate);
  const b = enc.encode(secret);
  let diff = a.length ^ b.length;
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    diff |= (a[i] ?? 0) ^ (b[i] ?? 0);
  }
  return diff === 0;
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const parts = url.pathname.split("/").filter(Boolean);

    if (parts[0] === "push") {
      if (!tokenMatches(parts[1], env.PUSH_TOKEN)) return new Response(null, { status: 404 });
      if (request.method !== "POST") return new Response(null, { status: 405 });
      const body = await request.text();
      if (body.length > 1_000_000) return new Response("too large", { status: 413 });
      let parsed;
      try {
        parsed = JSON.parse(body);
      } catch {
        return new Response("not json", { status: 400 });
      }
      // Legacy body is a bare rooms array; newer pushers send {rooms, underhood, ...} --
      // spread object bodies so extra sections ride along, keep wrapping bare arrays.
      const payload = Array.isArray(parsed) ? { rooms: parsed } : parsed;
      // #1690 item 2: `terminal_archive` (every terminal room; pusher.py's own hot-set cap keeps
      // `rooms` itself to non-terminal + the newest N terminal only, see pusher.py's
      // HOT_TERMINAL_CAP) rides straight into `payload` below, no separate KV key or write of its
      // own -- this file's own header docstring is the canonical record of who reads it back out
      // and how (the fleet_status handler, both the plain and the `page` branch).
      const snapshot = JSON.stringify({ pushed_at: new Date().toISOString(), ...payload });
      try {
        await env.FLEET.put("snapshot", snapshot);
      } catch (err) {
        if (classifyKvError(err) === "kv-write-cap") return kvWriteCapResponse();
        throw err;
      }
      return new Response("ok", { status: 200 });
    }

    if (parts[0] === "heartbeat") {
      if (!tokenMatches(parts[1], env.PUSH_TOKEN)) return new Response(null, { status: 404 });
      if (request.method !== "POST") return new Response(null, { status: 405 });
      // `at` is always THIS Worker's own receipt time, never read from the request (#1486) -- a
      // heartbeat's liveness claim must not depend on the pusher host's clock. `derived_at`
      // (#1613 item 2) and `pending_push_age_s` (2026-09-01 review finding), when the body carries
      // them, ARE read from the request: both name a fact only the pusher itself knows (when ITS
      // OWN derivation last completed; how long ITS OWN content has been waiting to push), which
      // this Worker has no other way to learn. A missing/unparseable body (including the
      // pre-#1613 literal "{}") degrades to neither field on this ping -- still a valid heartbeat.
      // #1981 (2026-09-06 review): `derived_ping_interval_s` rides the same rule -- the cadence this
      // ping was paced to is a fact only the pusher knows, and it is what makes fleet_status's
      // `projection` arm (b) decidable at all (worker.core.mjs). Absent from an unredeployed pusher.
      let derivedAt = null;
      let pendingPushAgeS = null;
      let derivedPingIntervalS = null;
      try {
        const body = await request.text();
        if (body) {
          const parsed = JSON.parse(body);
          if (parsed && typeof parsed.derived_at === "string" && parsed.derived_at) {
            derivedAt = parsed.derived_at;
          }
          if (parsed && typeof parsed.pending_push_age_s === "number" && isFinite(parsed.pending_push_age_s)) {
            pendingPushAgeS = parsed.pending_push_age_s;
          }
          if (parsed && typeof parsed.derived_ping_interval_s === "number"
              && isFinite(parsed.derived_ping_interval_s) && parsed.derived_ping_interval_s > 0) {
            derivedPingIntervalS = parsed.derived_ping_interval_s;
          }
        }
      } catch {
        // Malformed body -- treat exactly like an absent one; still a valid liveness ping.
      }
      const stored = { at: new Date().toISOString() };
      if (derivedAt) stored.derived_at = derivedAt;
      if (pendingPushAgeS !== null) stored.pending_push_age_s = pendingPushAgeS;
      if (derivedPingIntervalS !== null) stored.derived_ping_interval_s = derivedPingIntervalS;
      try {
        await env.FLEET.put("heartbeat_at", JSON.stringify(stored));
      } catch (err) {
        if (classifyKvError(err) === "kv-write-cap") return kvWriteCapResponse();
        throw err;
      }
      return new Response("ok", { status: 200 });
    }

    if (parts[0] === "deliver") {
      if (!tokenMatches(parts[1], env.PUSH_TOKEN)) return new Response(null, { status: 404 });
      return handleDeliver(request, env);
    }

    if (parts[0] === "mcp") {
      if (!tokenMatches(parts[1], env.READ_SEGMENT)) return new Response(null, { status: 404 });
      return handleMcp(request, env);
    }

    return new Response(null, { status: 404 });
  },
};
