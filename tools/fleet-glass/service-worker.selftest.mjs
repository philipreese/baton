import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const workerSource = readFileSync(join(here, "service-worker.js"), "utf8");
const failures = [];
const check = (name, condition) => { if (!condition) failures.push(name); };

const listeners = new Map();
const serviceWorker = {
  addEventListener(type, listener) { listeners.set(type, listener); },
  skipWaiting() { return Promise.resolve(); },
  clients: { claim() { return Promise.resolve(); } },
};
const worker = new Function(
  "self", "Response", "AbortController", "setTimeout", "clearTimeout", "fetch",
  `${workerSource}\nreturn { navigationResponse };`)(
    serviceWorker, Response, AbortController, setTimeout, clearTimeout, fetch);

const success = await worker.navigationResponse(
  { url: "https://private.example/", mode: "navigate" },
  async (_request, options) => {
    check("a navigation is fetched with no-store", options.cache === "no-store");
    return new Response("real dashboard", { status: 200 });
  });
check("a successful navigation returns the real network response", success.status === 200 && await success.text() === "real dashboard");

const failed = await worker.navigationResponse(
  { url: "https://private.example/", mode: "navigate" },
  async () => { throw new TypeError("network down"); });
const failureHtml = await failed.text();
check("a network failure returns the self-contained Retry page", failed.status === 503 && failureHtml.includes("Fleet Glass cannot be reached") && failureHtml.includes("Retry"));
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

let bypassed = false;
listeners.get("fetch")({ request: { mode: "cors", url: "https://private.example/projection.json" }, respondWith() { bypassed = true; } });
listeners.get("fetch")({ request: { mode: "same-origin", url: "https://private.example/events" }, respondWith() { bypassed = true; } });
listeners.get("fetch")({ request: { mode: "same-origin", url: "https://private.example/unrelated" }, respondWith() { bypassed = true; } });
check("projection, events, and unrelated requests bypass the worker", !bypassed);
check("the worker names no Cache API, preserving the no-live-data-cache invariant", !workerSource.includes("caches."));

if (failures.length) {
  console.error(`service-worker.selftest.mjs: FAIL -- ${failures.length} check(s):`);
  for (const failure of failures) console.error(`  !! ${failure}`);
  process.exit(1);
}

console.log("service-worker.selftest.mjs: pass");
