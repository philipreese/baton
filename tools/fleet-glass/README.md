# fleet-glass

Fleet Glass is served by `baton daemon` over the operator's private tailnet, from `glass.html`
embedded in `Baton.Cli`. The daemon-served page is the only supported delivery; it keeps the
existing manifest, service worker, projection route, and SSE route. Tailnet-only access remains
intact. When `Glass.OperatorLogin` is configured, that delivery alone also exposes queue
hold/resume and confirmed room cancel through the exact identity gate in `spec/baton.md` §11 C-11.

The operator-confirmed service URLs are recorded in the repo README's "Opening the glass over your
tailnet" section.

The bind rule and `tailscale serve` recipe are in the repo README's "Opening the glass over your
tailnet"; the listener remains opt-in and tailnet-only.

## Checks

- `pixi run fleet-glass-daemon-feed-selftest` — EventSource disconnect/reconnect behavior over the
  shipped `glass.html` function, including the retained-data noncurrent marker.
- `pixi run fleet-glass-service-worker-selftest` — the worker's first install/activate callbacks,
  dashboard-only navigation fallback, Retry/recovery, timeout, and live-route bypasses.
## Android standalone-install and offline-failure verification (#2166, #2168)

The daemon-hosted page links a same-origin manifest with standalone display and 192px/512px PNG
icons. The listener remains opt-in and tailnet-only; it also serves a navigation-only service worker
from that private origin. After one successful online visit has activated it, a later launch that
cannot reach the daemon (including an eight-second navigation timeout) shows a small static failure
page with Retry. Retry navigates to the real page again. No dashboard HTML, projection, event stream,
or other live response is cached or available offline; every normal launch and projection read goes
to the network with `no-store`.

Network freshness, install metadata, and the static failure page are separate facts. A reachable
live response is not necessarily current: the projection's existing staleness handling remains the
signal for that. The failure page has no fleet data at all.

### Android installation and offline-failure verification status

The earlier shared-host URL-handler collision is resolved for the installed app by private host
isolation. The conductor configured a private HTTPS service hostname as a proxy to local port 8420,
and the user confirmed that Baton installs and launches independently on its separate hostname.
This keeps the service private and does not require changing the manifest `id`, `start_url`, or
`scope`.

The underlying collision hypothesis was a cross-port URL-handler collision: Chromium's current
[WebAPK Android manifest template](https://github.com/chromium/chromium/blob/main/chrome/android/webapk/shell_apk/AndroidManifest.xml)
matches scheme, host, and path, but has no port field. The old shared hostname therefore allowed a
root-scoped installed app to claim the other app's URL; private host isolation separates those
handlers.

The final code's physical offline/retry behavior remains unverified. To complete that check on a
phone, first connect and authenticate the phone's tailnet client to the same private network as the
daemon, then open the isolated private HTTPS URL in Android Chrome and confirm it loads securely
with no certificate warning:

1. Use **Install app** from the Android Chrome browser menu.
2. Confirm the install prompt identifies Baton and shows its icon, then complete installation.
3. Launch it from the Android launcher and confirm it opens in a standalone window, independent of
   ordinary Chrome tabs.
4. Change the fleet and confirm the installed view receives the fresh projection; then confirm the
   normal Chrome tab remains separate.
5. With the app open online once and the worker activated, disconnect the phone from the tailnet and
   relaunch it. Confirm the static failure page and Retry appear; reconnect, use Retry, and confirm
   the real page returns. This step has not yet been verified on the final code.

Chrome's [PWA update guidance](https://web.dev/learn/pwa/update) distinguishes installed-app assets
from manifest metadata and notes that update timing depends on the browser lifecycle. Page changes
can therefore arrive before launcher icon, name, or splash changes. If installation metadata remains
stale after the browser has had time to process the update, removing and reinstalling Baton is a
recovery option; reinstallation is not universally required for a manifest change.

The automated listener, daemon-feed, and worker tests cover MIME types, no-store headers,
private-only registration, successful navigation, network failure and timeout fallback,
Retry/recovery, EventSource disconnect/recovery, and bypass of projection/events/unrelated
requests. They cannot verify Tailscale Service availability or authorization, phone tailnet
authentication, certificate trust, service-worker activation timing, or the final offline/retry
launch behaviour; the independent installation result is user-confirmed, but offline/retry remains
unverified on a physical Android device.
