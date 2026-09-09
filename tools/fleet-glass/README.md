# fleet-glass

The outbound half of Fleet Glass: `pusher.py` reads a projection of the local room fleet and PUTs it
into the Cloudflare KV mailbox `worker.js` serves to `glass.html`. Everything the payload contains,
the write budget, the secret gate, and the page's own rendering rules are specified in
`spec/baton.md` §6 — this file is a pointer, not a second copy.

## Two deliveries of one page (#1946)

`glass.html` is also served by `baton daemon` over the operator's tailnet, off the same file — it is
embedded in `Baton.Cli` from this directory, so a change here reaches both deliveries with no second
copy to keep in step. The config keys, the bind rule and the `tailscale serve` recipe are in the
repo README's "Opening the glass over your tailnet"; it was ratified in `spec/baton.md` §11 C-11.

**Publishing the Claude.ai artifact is now optional**, and the tailnet URL is intended to become the
primary way to open the glass — intended, not yet true: as of #1946 slice 1 the tailnet bind has
never been executed and the page has never been rendered in a browser on this plane. The artifact and
the mailbox behind it still work unchanged, and are still the only glass reachable from inside a
Claude conversation; retiring them is its own issue, after the tailnet page has been proven on a
phone.

## Where the fleet snapshot comes from (#1557)

Two sources, selected by the `FLEET_GLASS_PROJECTION_SOURCE` environment variable. Order per cycle:

1. **`file` (the default).** Read `BatonPaths.FleetProjectionFile` (`~/.baton/fleet/projection.json`,
   or `$BATON_HOME/fleet/projection.json`), which `baton daemon`'s `FleetProjectionWriter` rewrites
   roughly every 30s. Used whenever the file is present, well-formed, and younger than
   `PROJECTION_STALE_AFTER_S` (900s). No subprocess is spawned. `rooms[].live`, `rooms[].pruned` and
   `vendors` are taken from the file verbatim — the pusher never recomputes them.
2. **`derive` (the fallback).** Spawn `dotnet Baton.Cli.dll mcp` and build the snapshot here, exactly
   as the pusher always did. Runs when the file is absent, unreadable, malformed or stale — that
   cycle's pushed body then carries a `staleness` object and `glass.html` shows a banner — or when an
   operator pins `FLEET_GLASS_PROJECTION_SOURCE=derive`. **Kept for one release**; the condition for
   deleting it is recorded on `derive_snapshot_and_timelines`'s docstring in `pusher.py`.

Any other value of the variable resolves to the default rather than raising. One known difference
between the two sources: `timelines` is empty under `file`, because the daemon does not write per-room
timeline entries yet (#1902). `pusher.py --selftest` asserts it is the *only* difference.

## Checks

- `pixi run fleet-glass-pusher-selftest` — `python tools/fleet-glass/pusher.py --selftest`. Pure
  Python, no network, no vendor, no `~/.baton` read.
- `pixi run fleet-glass-worker-selftest` — `node tools/fleet-glass/worker.selftest.mjs`.
- `pixi run fleet-glass-daemon-feed-selftest` — EventSource disconnect/reconnect behavior over the
  shipped `glass.html` function, including the retained-data noncurrent marker.
- `pixi run fleet-glass-service-worker-selftest` — the worker's first install/activate callbacks,
  dashboard-only navigation fallback, Retry/recovery, timeout, and live-route bypasses.
- `python tools/fleet-glass/pusher.py --compare-projection` — runs both sources once against the
  **live** fleet and diffs them room by room. Needs a running daemon and a built CLI; not a CI check.

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
