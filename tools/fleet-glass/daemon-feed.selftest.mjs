// Behavioral test over the shipped daemon-feed function in glass.html. The page remains a single
// embedded artifact, so this extracts the production bytes instead of maintaining a second copy.
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const html = readFileSync(join(here, "glass.html"), "utf8");
const start = html.indexOf("function startDaemonFeed(applySnapshot");
const endMatch = /\r?\n}\r?\n\r?\n\(async \(\) =>/.exec(html.slice(start));
const end = endMatch ? start + endMatch.index + endMatch[0].indexOf("}") : -1;
if (start < 0 || end < 0) {
  console.error("daemon-feed.selftest.mjs: FAIL -- could not extract startDaemonFeed from glass.html.");
  process.exit(1);
}

const source = html.slice(start, end + 1);
const failures = [];
const check = (name, condition) => { if (!condition) failures.push(name); };
const tick = () => new Promise(resolve => setTimeout(resolve, 0));

class FakeEventSource {
  constructor(url) { this.url = url; this.listeners = new Map(); }
  addEventListener(type, listener) { this.listeners.set(type, listener); }
  emit(type, arg) { this.listeners.get(type)?.(arg); }
}

const banners = [];
const elements = { fresh: { textContent: "" }, inboxbanner: { innerHTML: "" } };
const snapshots = [];
const fleetEventsReceived = [];
let eventSource;
let reads = 0;
const fetch = async (url, options) => {
  reads += 1;
  check("each projection read uses no-store", url === "/projection.json" && options.cache === "no-store");
  return { ok: true, json: async () => ({ rooms: [], sequence: reads }) };
};
const startDaemonFeed = new Function("fetch", "EventSource", "banner", "esc", "$", "lastGood",
  `${source}\nreturn startDaemonFeed;`)(
    fetch,
    class extends FakeEventSource { constructor(url) { super(url); eventSource = this; } },
    value => banners.push(value),
    value => value,
    name => elements[name],
    { rooms: [] });

startDaemonFeed(
  snapshot => snapshots.push(snapshot),
  evt => fleetEventsReceived.push(evt));
await tick();
check("the daemon feed subscribes to the projection event stream", eventSource?.url === "/events");
check("the initial successful read renders a snapshot", snapshots.length === 1);

eventSource.emit("error");
check("an EventSource disconnect visibly marks retained data noncurrent",
  banners.at(-1)?.includes("last successful read, not a current fleet view")
  && elements.fresh.textContent === "connection lost — last successful read shown");

eventSource.emit("open");
await tick();
check("a recovered EventSource triggers a fresh projection read", reads === 2 && snapshots.length === 2);

eventSource.emit("fleet", { data: JSON.stringify({ id: 1, kind: "attemptStarted", attemptId: "att-1", declaredRole: "implement" }) });
check("the daemon feed receives fleet event frames from SSE", fleetEventsReceived.length === 1 && fleetEventsReceived[0].id === 1);

eventSource.emit("fleet", { data: JSON.stringify({ id: 1, kind: "attemptStarted", attemptId: "att-1" }) });
check("the daemon feed deduplicates replayed fleet events with same monotonic id", fleetEventsReceived.length === 1);

eventSource.emit("fleet", { data: JSON.stringify({ id: 2, kind: "attemptSettled", attemptId: "att-1", outcome: "succeeded" }) });
check("the daemon feed receives subsequent monotonic fleet events", fleetEventsReceived.length === 2 && fleetEventsReceived[1].id === 2);

eventSource.emit("fleet", { data: "not-valid-json{" });
check("the daemon feed ignores malformed event payloads safely", fleetEventsReceived.length === 2);

if (failures.length) {
  console.error(`daemon-feed.selftest.mjs: FAIL -- ${failures.length} check(s):`);
  for (const failure of failures) console.error(`  !! ${failure}`);
  process.exit(1);
}

console.log("daemon-feed.selftest.mjs: pass");
