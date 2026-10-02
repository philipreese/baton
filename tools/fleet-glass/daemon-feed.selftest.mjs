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
const sourceBetween = (startText, endText, label) => {
  const from = html.indexOf(startText);
  const to = from < 0 ? -1 : html.indexOf(endText, from + startText.length);
  if (from < 0 || to < 0) {
    console.error(`daemon-feed.selftest.mjs: FAIL -- could not extract ${label} from glass.html.`);
    process.exit(1);
  }
  return html.slice(from, to).trim();
};
const writeControllerSource = sourceBetween(
  "function createGlassWriteController(", "function wireGlassWriteClicks", "createGlassWriteController");
const clickWireSource = sourceBetween(
  "function wireGlassWriteClicks(", "(async () =>", "wireGlassWriteClicks");
const bannerSource = sourceBetween("function banner(html){", "function queueWriteFeedback", "banner");
const queueFeedbackSource = sourceBetween("function queueWriteFeedback(html){", "// #1391: advisory per-vendor usage", "queueWriteFeedback");
const productionWiringIsPresent = text =>
  text.includes("createGlassWriteController(fetch, applySnapshot, banner, queueWriteFeedback, esc);")
  && text.includes("startDaemonFeed(glassWrites.observeSnapshot, recordFleetEvent);")
  && text.includes("wireGlassWriteClicks(document, glassWrites, window.confirm.bind(window));");
const assertProductionWiring = text => {
  if(!productionWiringIsPresent(text)) throw new Error("production Glass write wiring is absent");
};
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

const stamp = second => new Date(Date.UTC(2026, 9, 2, 12, 0, second)).toISOString();
const responseFor = (snapshot, ok = true, status = 200) => ({
  ok, status,
  text: async () => "Queue command accepted",
  json: async () => snapshot
});
const button = route => ({disabled: false, dataset: {glassRoute: route}});
const olderUnheld = {rooms: [], derived_at: stamp(0), queue: {held: false}};
const olderHeld = {rooms: [], derived_at: stamp(0), queue: {held: true}};
const olderThanBaselineHeld = {rooms: [], derived_at: stamp(-1), queue: {held: true}};
const firstMatchingHeld = {rooms: [], derived_at: stamp(0), queue: {held: true}};
const newerHeld = {rooms: [], derived_at: stamp(2), queue: {held: true}};
const newerUnheld = {rooms: [], derived_at: stamp(2), queue: {held: false}};

const productionBanner = new Function("$", `${bannerSource}\nreturn banner;`)(id => elements[id]);
const productionQueueFeedback = new Function("$", `${queueFeedbackSource}\nreturn queueWriteFeedback;`)(id => elements[id]);
const makeProductionHarness = fetchImpl => {
  elements.banner.innerHTML = "";
  elements["queue-write-feedback"].innerHTML = "";
  const applied = [];
  const controller = new Function("fetchImpl", "applySnapshot", "banner", "renderQueueFeedback", "esc",
    `${writeControllerSource}\nreturn createGlassWriteController(fetchImpl, applySnapshot, banner, renderQueueFeedback, esc);`)(
      fetchImpl,
      snap => { applied.push(snap); productionBanner("<b>Projection freshness warning</b>"); },
      productionBanner,
      productionQueueFeedback,
      value => String(value).replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c])));
  return {controller, applied};
};
const queueFeedbackText = () => elements["queue-write-feedback"].innerHTML.toLowerCase();
const makeProductionClickBinding = controller => {
  let clickListener;
  const documentRef = {addEventListener: (type, listener) => { if(type === "click") clickListener = listener; }};
  new Function("documentRef", "glassWrites", "confirmAction",
    `${clickWireSource}\nreturn wireGlassWriteClicks(documentRef, glassWrites, confirmAction);`)(
      documentRef, controller, () => true);
  return async targetButton => clickListener({target: {closest: selector => selector === ".glasswrite" ? targetButton : null}});
};
const makeFeed = (fetchImpl, observeSnapshot) => {
  let feedEventSource;
  const starter = new Function("fetch", "EventSource", "banner", "esc", "$", "lastGood",
    `${source}\nreturn startDaemonFeed;`)(
      fetchImpl,
      class extends FakeEventSource { constructor(url) { super(url); feedEventSource = this; } },
      productionBanner,
      value => String(value),
      id => elements[id],
      olderUnheld);
  starter(observeSnapshot, () => {});
  return {get eventSource(){return feedEventSource;}};
};

elements.banner = {innerHTML: ""};
elements["queue-write-feedback"] = {innerHTML: ""};
check("production page wires the controller through both click and SSE paths", productionWiringIsPresent(html));
const bypassedBinding = html.replace(
  "wireGlassWriteClicks(document, glassWrites, window.confirm.bind(window));", "/* mutation: click handler bypassed */");
const bypassedFeed = html.replace(
  "startDaemonFeed(glassWrites.observeSnapshot, recordFleetEvent);", "startDaemonFeed(applySnapshot, recordFleetEvent);");
const mutationRejected = mutated => {
  try { assertProductionWiring(mutated); return false; } catch { return true; }
};
check("mutation control rejects bypassing the production click binding", mutationRejected(bypassedBinding));
check("mutation control rejects bypassing the production SSE/render binding", mutationRejected(bypassedFeed));

{
  let eventSource;
  let nextProjection = 0;
  const projectionResponses = [responseFor(olderUnheld), new Error("offline"), responseFor(olderUnheld), responseFor(newerHeld)];
  const fetchImpl = async (url, options) => {
    if(url === "/queue/hold"){
      check("production click issues the expected same-origin queue POST", options.method === "POST");
      return responseFor();
    }
    check("production feed and action reads stay on the projection endpoint", url === "/projection.json");
    check("production projection reads use no-store", options.cache === "no-store");
    const next = projectionResponses[nextProjection++];
    if(next instanceof Error) throw next;
    return next;
  };
  const h = makeProductionHarness(fetchImpl);
  const feed = makeFeed(fetchImpl, h.controller.observeSnapshot);
  eventSource = feed.eventSource;
  await tick();
  const click = makeProductionClickBinding(h.controller);
  await click(button("/queue/hold"));
  check("accepted POST plus failed GET stays in its own feedback region",
    elements["queue-write-feedback"].innerHTML.includes("accepted")
    && elements["queue-write-feedback"].innerHTML.includes("projection refresh failed")
    && elements.banner.innerHTML.includes("Projection freshness warning"));
  eventSource.emit("projection");
  await tick();
  check("stale SSE projection leaves accepted hold visible beside health warning",
    queueFeedbackText().includes("accepted")
    && queueFeedbackText().includes("waiting for a newer snapshot reporting the queue held")
    && elements.banner.innerHTML.includes("Projection freshness warning"));
  eventSource.emit("projection");
  await tick();
  check("later matching SSE state is described as observed, without causal wording",
    elements["queue-write-feedback"].innerHTML.includes("newer queue snapshot reports the queue held")
    && !elements["queue-write-feedback"].innerHTML.includes("confirm the action"));
  eventSource.emit("error");
  check("SSE disconnect warning does not erase queue receipt feedback",
    elements.banner.innerHTML.includes("event stream disconnected")
    && elements["queue-write-feedback"].innerHTML.includes("Queue command accepted"));
}

{
  const h = makeProductionHarness(async (url) => url === "/queue/resume"
    ? responseFor()
    : responseFor(olderHeld));
  h.controller.observeSnapshot(olderHeld);
  const click = makeProductionClickBinding(h.controller);
  await click(button("/queue/resume"));
  check("accepted resume stays separate from stale held state",
    queueFeedbackText().includes("accepted")
    && queueFeedbackText().includes("projection reports held"));
  h.controller.observeSnapshot(newerUnheld);
  check("later matching resume state is observed without pretending causality",
    queueFeedbackText().includes("newer queue snapshot reports the queue unheld"));
}

{
  const h = makeProductionHarness(async (url) => url === "/queue/hold"
    ? responseFor("refused", false, 403)
    : responseFor(olderUnheld));
  h.controller.observeSnapshot(olderUnheld);
  const click = makeProductionClickBinding(h.controller);
  await click(button("/queue/hold"));
  check("a refused write is an error and never creates accepted queue feedback",
    elements.banner.innerHTML.includes("Glass write failed")
    && !queueFeedbackText().includes("accepted"));
}

{
  const h = makeProductionHarness(async () => { throw new Error("offline"); });
  h.controller.observeSnapshot(olderUnheld);
  const click = makeProductionClickBinding(h.controller);
  await click(button("/queue/hold"));
  check("a network-failed POST is an error and never creates accepted queue feedback",
    elements.banner.innerHTML.includes("Glass write failed")
    && !queueFeedbackText().includes("accepted"));
}

{
  const h = makeProductionHarness(async url => url === "/queue/hold" ? responseFor() : responseFor(firstMatchingHeld));
  h.controller.observeSnapshot({rooms: [], derived_at: "invalid", queue: {held: false}});
  const click = makeProductionClickBinding(h.controller);
  await click(button("/queue/hold"));
  check("a matching first post-receipt snapshot only establishes an unknown baseline",
    queueFeedbackText().includes("first valid timestamp observed after the receipt")
    && queueFeedbackText().includes("waiting for a newer snapshot"));
  h.controller.observeSnapshot(firstMatchingHeld);
  check("equal timestamp cannot resolve accepted hold feedback",
    queueFeedbackText().includes("waiting for a newer snapshot"));
  h.controller.observeSnapshot({rooms: [], derived_at: stamp(0), queue: {held: true}});
  check("repeated equal timestamp cannot resolve accepted hold feedback",
    queueFeedbackText().includes("waiting for a newer snapshot"));
  h.controller.observeSnapshot(olderThanBaselineHeld);
  check("older timestamp cannot resolve accepted hold feedback",
    queueFeedbackText().includes("waiting for a newer snapshot"));
  h.controller.observeSnapshot({rooms: [], derived_at: stamp(1), queue: {held: false}});
  check("opposite state cannot resolve accepted hold feedback",
    queueFeedbackText().includes("projection reports unheld")
    && queueFeedbackText().includes("waiting for a newer snapshot"));
  h.controller.observeSnapshot(newerHeld);
  check("newer matching observation resolves only to a state report",
    queueFeedbackText().includes("newer queue snapshot reports the queue held"));
}

{
  let finishPost;
  let postCount = 0;
  const delayedPost = new Promise(resolve => { finishPost = resolve; });
  const h = makeProductionHarness(async url => {
    if(url === "/queue/hold") { postCount += 1; return delayedPost; }
    return responseFor(olderUnheld);
  });
  h.controller.observeSnapshot(olderUnheld);
  const click = makeProductionClickBinding(h.controller);
  const firstClick = click(button("/queue/hold"));
  await tick();
  h.controller.observeSnapshot(olderUnheld); // a projection render may replace the DOM button
  await click(button("/queue/hold")); // a fresh control button can exist after an SSE rerender
  check("controller serializes queue POSTs across recreated controls", postCount === 1);
  finishPost(responseFor());
  await firstClick;
  check("ignored overlapping hold cannot replace the accepted receipt",
    queueFeedbackText().includes("queue command accepted")
    && queueFeedbackText().includes("queue held"));
}

if (failures.length) {
  console.error(`daemon-feed.selftest.mjs: FAIL -- ${failures.length} check(s):`);
  for (const failure of failures) console.error(`  !! ${failure}`);
  process.exit(1);
}

console.log("daemon-feed.selftest.mjs: pass");
