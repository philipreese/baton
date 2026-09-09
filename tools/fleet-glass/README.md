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

### Known Android installation collision — not fixed

Do not treat the current daemon URL as independently installable while another installed private
web app uses the same Tailscale hostname with root scope. A physical Android Chrome check reached
the Fleet Glass page and its correct manifest at the daemon's `:9443` URL, but the browser menu
offered to open the already-installed app rather than install Fleet Glass. No phone package or
WebAPK intent-filter inspection has yet confirmed the cause.

The leading, source-backed hypothesis is a cross-port URL-handler collision: Chromium's current
[WebAPK Android manifest template](https://github.com/chromium/chromium/blob/main/chrome/android/webapk/shell_apk/AndroidManifest.xml)
matches scheme, host, and path, but has no port field. Both installed apps currently use `/` as
their scope, so changing Fleet Glass to a path below that root would still be included by the
existing app's root handler. Changing Fleet Glass's manifest name, id, or service worker does not
separate that handler.

Before installing Fleet Glass, give it a separately verified private HTTPS hostname (for example,
a Tailscale Service only after the tailnet administrator has made that service available and
authorized this node), then open that hostname in Android Chrome and confirm it is secure. The
deployment plan and recovery procedure must preserve the other installed app and require a physical
phone confirmation before this limitation is called resolved. Do not remove or reinstall either app
as a substitute for hostname isolation; reinstalling Fleet Glass is only a later recovery option
for stale metadata after coexistence has been proven.

To verify on a phone after an operator has intentionally exposed the separately hosted private HTTPS
URL, first connect and authenticate the phone's Tailscale client to the same tailnet as the daemon.
Open the isolated private URL in Android Chrome and confirm it loads securely with no certificate
warning before beginning installation:

1. Use **Install app** from the Android Chrome browser menu.
2. Confirm the install prompt identifies Fleet Glass and shows its icon, then complete installation.
3. Launch it from the Android launcher and confirm it opens in a standalone window, independent of
   ordinary Chrome tabs.
4. Change the fleet and confirm the installed view receives the fresh projection; then confirm the
   normal Chrome tab remains separate.
5. With the app open online once and the worker activated, disconnect the phone from the tailnet and
   relaunch it. Confirm the static failure page and Retry appear; reconnect, use Retry, and confirm
   the real page returns.

Chrome's [PWA update guidance](https://web.dev/learn/pwa/update) distinguishes installed-app assets
from manifest metadata and notes that update timing depends on the browser lifecycle. Page changes
can therefore arrive before launcher icon, name, or splash changes. If installation metadata remains
stale, removing and reinstalling Fleet Glass is a recovery option; it is not a claim that every
manifest change requires reinstallation.

The automated listener and worker tests cover MIME types, no-store headers, private-only
registration, successful navigation, network failure and timeout fallback, Retry/recovery, and
bypass of projection/events/unrelated requests. They cannot verify Tailscale Service availability or
authorization, phone tailnet authentication, certificate trust, Android installation,
service-worker activation timing, or launch behaviour; those remain unverified until these steps are
performed on a physical Android device.
