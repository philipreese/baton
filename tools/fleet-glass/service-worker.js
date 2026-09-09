// #2168 -- this worker exists only in the daemon-served, private delivery. It deliberately has no
// cache: Fleet Glass is a live reading, so a retained dashboard, projection, event stream, or other
// live response would make old fleet state look current. The sole offline response is this worker's
// self-contained navigation failure page.
const NAVIGATION_TIMEOUT_MS = 8000;
const DASHBOARD_PATHS = new Set(["/", "/index.html"]);

const failurePage = `<!doctype html>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Baton unavailable</title>
<style>body{font:16px system-ui,sans-serif;max-width:32rem;margin:12vh auto;padding:0 1.5rem;color:#232830}button{font:inherit;padding:.6rem 1rem}</style>
<h1>Baton cannot be reached</h1>
<p>The private daemon did not respond. This page contains no fleet data.</p>
<button type="button" id="retry">Retry</button>
<script>document.getElementById("retry").addEventListener("click",()=>location.replace("/"));</script>`;

function unavailableResponse(){
  return new Response(failurePage, {
    status: 503,
    headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" },
  });
}

async function networkNavigation(request, fetchImpl = fetch, timeoutMs = NAVIGATION_TIMEOUT_MS){
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);
  try {
    return await fetchImpl(request, { cache: "no-store", signal: controller.signal });
  } finally {
    clearTimeout(timeout);
  }
}

async function navigationResponse(request, fetchImpl = fetch, timeoutMs = NAVIGATION_TIMEOUT_MS){
  try {
    const response = await networkNavigation(request, fetchImpl, timeoutMs);
    return response.ok ? response : unavailableResponse();
  } catch {
    return unavailableResponse();
  }
}

function isDashboardNavigation(request){
  const url = new URL(request.url);
  return request.mode === "navigate"
    && url.origin === self.location.origin
    && DASHBOARD_PATHS.has(url.pathname);
}

self.addEventListener("install", (event) => event.waitUntil(self.skipWaiting()));
self.addEventListener("activate", (event) => event.waitUntil(self.clients.claim()));
self.addEventListener("fetch", (event) => {
  // Fleet Glass is a live reading: only its same-origin dashboard routes receive the static
  // failure response. Projection data, the event stream, and every other route always bypass this
  // worker, even if a browser reports their request as a navigation.
  if(!isDashboardNavigation(event.request)) return;
  event.respondWith(navigationResponse(event.request));
});
