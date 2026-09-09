import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const workerSource = readFileSync(join(here, "service-worker.js"), "utf8");
const failures = [];
const check = (name, condition) => { if (!condition) failures.push(name); };

const listeners = new Map();
const lifecycleCalls = [];
const serviceWorker = {
  addEventListener(type, listener) { listeners.set(type, listener); },
  location: { origin: "https://private.example" },
  skipWaiting() { lifecycleCalls.push("skipWaiting"); return Promise.resolve(); },
  clients: { claim() { lifecycleCalls.push("claim"); return Promise.resolve(); } },
};
const worker = new Function(
  "self", "Response", "AbortController", "setTimeout", "clearTimeout", "fetch",
  `${workerSource}\nreturn { navigationResponse, isDashboardNavigation };`)(
    serviceWorker, Response, AbortController, setTimeout, clearTimeout,
    async () => { throw new TypeError("network unavailable in fetch-listener test"); });

for (const [eventName, expectedCall] of [["install", "skipWaiting"], ["activate", "claim"]]) {
  let lifecyclePromise;
  listeners.get(eventName)({ waitUntil(promise) { lifecyclePromise = promise; } });
  check(`${eventName} waits for its lifecycle operation`, lifecyclePromise instanceof Promise);
  await lifecyclePromise;
  check(`${eventName} performs ${expectedCall}`, lifecycleCalls.includes(expectedCall));
}

const success = await worker.navigationResponse(
  { url: "https://private.example/", mode: "navigate" },
  async (_request, options) => {
    check("a navigation is fetched with no-store", options.cache === "no-store");
    return new Response("real dashboard", { status: 200 });
  });
check("a successful navigation returns the real network response", success.status === 200 && await success.text() === "real dashboard");

for (const status of [502, 503, 504]) {
  const failedResponse = await worker.navigationResponse(
    { url: "https://private.example/", mode: "navigate" },
    async () => new Response(`proxy ${status}`, { status }));
  const failedResponseHtml = await failedResponse.text();
  check(`a resolved ${status} navigation returns the self-contained Baton Retry page`,
    failedResponse.status === 503 && failedResponseHtml.includes("Baton cannot be reached") && failedResponseHtml.includes("Retry"));
}

const failed = await worker.navigationResponse(
  { url: "https://private.example/", mode: "navigate" },
  async () => { throw new TypeError("network down"); });
const failureHtml = await failed.text();
check("a network failure returns the self-contained Baton Retry page", failed.status === 503 && failureHtml.includes("Baton cannot be reached") && failureHtml.includes("Retry"));
check("the static failure page makes no fleet-data claim", failureHtml.includes("contains no fleet data"));
check("Retry returns to the real page", failureHtml.includes("location.replace(\"/\")"));

const timedOut = await worker.navigationResponse(
  { url: "https://private.example/", mode: "navigate" },
  (_request, options) => new Promise((_resolve, reject) => options.signal.addEventListener("abort", () => reject(new Error("timed out")))),
  5);
check("a bounded navigation timeout returns the same failure page", timedOut.status === 503);

const recovered = await worker.navigationResponse(
  { url: "https://private.example/", mode: "navigate" },
  async () => new Response("recovered", { status: 200 }));
check("Retry recovers by fetching the real page again", recovered.status === 200 && await recovered.text() === "recovered");

const intercepted = (request) => {
  let response;
  listeners.get("fetch")({ request, respondWith(value) { response = value; } });
  return response;
};
check("the root dashboard navigation is intercepted", intercepted({ mode: "navigate", url: "https://private.example/" }) instanceof Promise);
check("the index dashboard navigation is intercepted", intercepted({ mode: "navigate", url: "https://private.example/index.html" }) instanceof Promise);
for (const [name, request] of [
  ["projection", { mode: "navigate", url: "https://private.example/projection.json" }],
  ["events", { mode: "navigate", url: "https://private.example/events" }],
  ["unrelated path", { mode: "navigate", url: "https://private.example/unrelated" }],
  ["cross-origin dashboard", { mode: "navigate", url: "https://other.example/" }],
  ["non-navigation projection", { mode: "cors", url: "https://private.example/projection.json" }],
]) {
  check(`${name} bypasses the worker`, intercepted(request) === undefined);
}
check("the worker names no Cache API, preserving the no-live-data-cache invariant", !workerSource.includes("caches."));

if (failures.length) {
  console.error(`service-worker.selftest.mjs: FAIL -- ${failures.length} check(s):`);
  for (const failure of failures) console.error(`  !! ${failure}`);
  process.exit(1);
}

console.log("service-worker.selftest.mjs: pass");
