"""Drive SEP-1036 URL-mode elicitation end to end against an INTERACTIVE agy (#531).

Separate from verify.py because it is the one probe that needs a terminal: url mode is defined
around a person consenting and then answering in a browser, and every headless arm answers `cancel`
because there is nobody there. Running agy under a pty is what supplies the "there is somebody
there" half -- not a human, but a session that behaves like one has attached.

WHAT THIS WAS THOUGHT TO NEED, AND WHY THAT WAS WRONG
-----------------------------------------------------
#531 was filed as "permanently a human action item". That incorrectly generalized from the separate
live-vendor policy (docs/agents/developing-baton.md#live-vendor-smoke-tests). This probe's three
open questions split cleanly:

    does interactive agy SURFACE the url?    a pty and a string assertion -- no human
    does the completion notification RESUME? pure protocol -- no human, no browser
    does -32042 behave equivalently?         pure protocol -- no human

SEP-1036's "the user answers out of band" means the answer does not travel back through the MCP
client. The spec does not care whether a browser or a script hits that endpoint -- which is exactly
what takes the person out of the measurement loop. What stays human is whether a *person* finds the
surfaced link usable, and no AER decision rests on that.

THE ONE-VARIABLE PAIR
---------------------
Both arms are identical except for whether the out-of-band endpoint is ever hit.

    arm `unanswered`  the URL is never opened -> the gated tool MUST NOT complete
    arm `answered`    the URL is opened       -> does the tool complete, and does agy retry?

Without the first arm, a completion in the second proves nothing: a gate that opens on its own
looks identical to one that opened because it was answered.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER = os.path.join(HERE, "servers", "mcp_url_gate_server.py")

try:
    AGY_VERSION = subprocess.run(["agy", "--version"], capture_output=True,
                                 text=True, timeout=60).stdout.strip()
except Exception:                                                      # noqa: BLE001
    AGY_VERSION = "unknown"


def _sentinel(wd, name):
    p = os.path.join(wd, name)
    if not os.path.exists(p):
        return None
    try:
        return json.load(open(p, encoding="utf-8"))
    except ValueError:
        return open(p, encoding="utf-8").read().strip()


_ANSI = re.compile(r"\x1b\[[0-9;?]*[a-zA-Z]|\x1b\][^\x07]*\x07")


def _surfaced(tui, url):
    """Did the client render the elicitation url anywhere a person could see it?

    Returns a dict, never a bare bool: which needle matched is the difference between "agy showed
    a link" and "agy happened to echo a port number", and a single boolean throws that away.
    """
    if not url:
        return {"checked": False, "why": "no url was ever issued"}
    flat = re.sub(r"\s+", "", _ANSI.sub("", tui))
    host = url.split("/")[2]
    needles = {"full_url": url.replace(" ", ""), "host_port": host,
               "port_only": host.split(":")[-1], "path_segment": "aer-gate",
               "elicitation_id": "aer-531-0001"}
    hits = {k: (v in flat) for k, v in needles.items()}
    return {"checked": True, "any": any(hits.values()), **hits}


def _agy_cmd(wd):
    """The agy invocation, or a free stand-in when BATON_531_DRYRUN is set.

    The dry run exists so the harness can be proven to LAUNCH before any arm spends vendor usage.
    That order is not optional here: the first run of this probe reported four all-zero arms
    because bash never started agy at all, and an all-zero arm is indistinguishable from a real
    refusal unless something records that the instrument itself ran.
    """
    if os.environ.get("BATON_531_DRYRUN"):
        return "agy --help"
    # `--dangerously-skip-permissions` is what makes the run scriptable, and it is ALSO a candidate
    # cause of the result: the first four arms all answered `cancel` and never surfaced the url, and
    # a flag that auto-approves tool permissions is exactly the sort of thing that would auto-decline
    # an elicitation too. That confound is the probe's, not the vendor's, so it gets its own arm --
    # BATON_531_NO_SKIP drops the flag and relies on the driver's periodic CR to clear the trust
    # prompt instead. Without this control, "agy declines url mode" and "the probe told it to" are
    # the same observation.
    skip = "" if os.environ.get("BATON_531_NO_SKIP") else " --dangerously-skip-permissions"
    return ('agy -i "Call the MCP tool control_tool, then call elicit_tool. Call both." '
            '--add-dir %s%s' % (wd.replace("\\", "/"), skip))


def arm(flow, answer, seconds, keep=False):
    """One run. `flow` is hold|required; `answer` decides whether the URL is ever opened."""
    wd = tempfile.mkdtemp(prefix="v-531-")
    os.makedirs(os.path.join(wd, ".agents"), exist_ok=True)
    cfg = {"mcpServers": {"probe": {
        "command": sys.executable, "args": [SERVER],
        "env": {"BATON_SENTINEL_DIR": wd, "BATON_URL_FLOW": flow}}}}
    json.dump(cfg, open(os.path.join(wd, ".agents", "mcp_config.json"), "w"))
    log = os.path.join(wd, "tui.log")

    # The driver goes in a FILE, not an inline `bash -lc` string. The first version nested a
    # `for` loop, a `printf '\r'`, a pipe and a redirect inside one quoted argument; the CR ended
    # the line and bash died with a syntax error before agy ever started. Every arm then reported
    # all-zero, which reads exactly like "agy ignored the elicitation" -- a harness failure wearing
    # a finding's clothes. Sentinels caught it (`tui_bytes: 0`, `caps: null`), but only because
    # they record whether the instrument ran at all.
    sh = os.path.join(wd, "drive.sh")
    with open(sh, "w", newline="\n") as f:
        f.write("#!/bin/sh\n"
                "( sleep 6\n"
                "  i=0\n"
                "  while [ $i -lt %d ]; do printf '\\r'; sleep 4; i=$((i+1)); done\n"
                ") | winpty -Xallow-non-tty -Xplain %s > '%s' 2>&1\n"
                % (max(1, seconds // 4), _agy_cmd(wd), log.replace("\\", "/")))

    # Resolve bash explicitly and keep its stderr. `Popen(["bash", ...])` was failing silently:
    # every arm then returned zeros, which is the one thing this probe must never do quietly.
    bash = shutil.which("bash") or "bash"
    launch_err = os.path.join(wd, "launch.err")
    proc = subprocess.Popen([bash, sh.replace("\\", "/")], cwd=wd,
                            stdout=subprocess.DEVNULL,
                            stderr=open(launch_err, "w"))
    url, hit_by_agy = None, False
    start = time.time()
    deadline = start + seconds
    while time.time() < deadline:
        time.sleep(2)
        if url is None:
            url = _sentinel(wd, "URL.txt")
        # Did agy open it WITHOUT being told to? That is question 1, and it must be checked
        # before the probe opens it itself -- afterwards the two are indistinguishable.
        if url and _sentinel(wd, "URL_HIT.json"):
            hit_by_agy = True
            break
        if answer and url and time.time() > start + 25:
            # agy was given 25 s to open it unprompted first; only now does the probe stand in for
            # the browser. Doing this earlier would make "agy opened it" unobservable.
            try:
                urllib.request.urlopen(url, timeout=10).read()
            except Exception:                                              # noqa: BLE001
                pass
            break
    # Always leave room after the endpoint is hit for the client to react -- a retry that arrives
    # after the process is killed reads identically to a client that never retried.
    time.sleep(25)
    try:
        proc.kill()
    except Exception:                                                      # noqa: BLE001
        pass

    tui = open(log, encoding="utf-8", errors="replace").read() if os.path.exists(log) else ""
    err = (open(launch_err, encoding="utf-8", errors="replace").read().strip()
           if os.path.exists(launch_err) else "")
    # An arm whose TUI never produced a byte did not measure the vendor -- it measured the harness.
    # Reporting zeros for it would be indistinguishable from "agy ignored the elicitation", which
    # is precisely the confusion every control in this suite exists to prevent. So it is a
    # DIFFERENT kind of result, not a negative one.
    if not tui:
        return {"flow": flow, "answered": answer, "HARNESS_FAILED": True,
                "why": "agy produced no terminal output; the probe never ran",
                "launch_stderr": err or "(none captured)",
                "workdir": wd if keep else None}
    out = {
        "flow": flow, "answered": answer, "HARNESS_FAILED": False,
        "url": url,
        # "the url was never shown" is the load-bearing negative here, so the detector must not be
        # a plain substring test: a TUI wraps lines and injects ANSI, either of which would split
        # the url and make a rendered link look absent. Strip escapes, drop ALL whitespace, then
        # look for several independent needles -- the full url, the host:port, the unique path
        # segment, and the elicitation id. A negative only counts if none of them appear.
        "url_surfaced_in_tui": _surfaced(tui, url),
        "opened_by_agy_unprompted": hit_by_agy,
        "caps": _sentinel(wd, "CAPS.json"),
        "elicited": _sentinel(wd, "ELICITED.json"),
        "url_hit": _sentinel(wd, "URL_HIT.json"),
        "notified": _sentinel(wd, "NOTIFIED.json"),
        "retried": _sentinel(wd, "RETRIED.json"),
        # `tool_completed` only says the SERVER answered the call. Whether the CLIENT took that
        # answer is a different question, and conflating them would credit agy with accepting a
        # result it may have already torn down. The completion text is greppable for exactly this.
        "server_completed_call": os.path.exists(os.path.join(wd, "CALLED_elicit_tool")),
        "client_showed_result": "BATON_COMPLETION_SENTINEL" in re.sub(r"\s+", "", _ANSI.sub("", tui)),
        "agy_version": AGY_VERSION,
        "control_ran": os.path.exists(os.path.join(wd, "CALLED_control_tool")),
        "tui_bytes": len(tui),
    }
    if keep:
        out["workdir"] = wd
    else:
        shutil.rmtree(wd, ignore_errors=True)
    return out


def manual(flow, where):
    """Set up a workdir, print the command, and wait while a HUMAN runs agy there.

    This exists because the automated arms hit a wall that is NOT a vendor fact. Every elicitation
    -- including the simplest legal form-mode one, which is the positive control -- comes back
    `cancel` with latency 0.0 under a pty-driven session, with and without keystrokes, with and
    without `--dangerously-skip-permissions`. An instant cancel means no UI was ever attempted.

    From inside an agent session there is no way to separate:

        agy declares an elicitation capability it does not surface     (a real vendor finding)
        a driven pty is not a context where agy will prompt            (a fact about this harness)

    Separating them needs a person watching a real terminal. That is the whole of the remaining
    human step in #531 -- much narrower than the issue's original "characterise it end to end",
    and it is only this narrow because the instrument around it was built first.

    RUN form MODE FIRST. It is the control and it decides what every url arm is worth:

        a prompt appears   -> the harness was the problem; agy does elicit, and url mode is
                              worth re-testing by hand
        nothing appears    -> agy declares elicitation it never surfaces; a real finding, and
                              0029's url-mode migration is blocked on the vendor
    """
    os.makedirs(os.path.join(where, ".agents"), exist_ok=True)
    for stale in ("CAPS.json", "ELICITED.json", "URL_HIT.json", "NOTIFIED.json", "RETRIED.json",
                  "URL.txt", "CALLED_control_tool", "CALLED_elicit_tool"):
        try:
            os.remove(os.path.join(where, stale))
        except OSError:
            pass
    cfg = {"mcpServers": {"probe": {
        "command": sys.executable, "args": [SERVER],
        "env": {"BATON_SENTINEL_DIR": where, "BATON_URL_FLOW": flow}}}}
    json.dump(cfg, open(os.path.join(where, ".agents", "mcp_config.json"), "w"), indent=2)

    bar = "=" * 78
    print(bar)
    print("  MANUAL ARM -- flow=%s   agy %s" % (flow, AGY_VERSION))
    print(bar)
    print()
    print("  1. In a REAL terminal (not this session, no winpty), run:")
    print()
    print('       cd "%s"' % where)
    # --add-dir is NOT optional, even though cwd is already the workspace. Without it agy
    # reports "the requested MCP tools are not available" and never loads .agents/mcp_config.json,
    # which looks exactly like a tool the model declined to call.
    print('       agy -i "Call the MCP tool control_tool, then call elicit_tool. Call both." '
          '--add-dir %s' % where.replace("\\", "/"))
    print()
    print("     (--add-dir is required. Without it agy does not load .agents/mcp_config.json")
    print("      and says the probe tools are unavailable.)")
    print()
    print("  2. Wait for [CAPS.json] to appear below -- that is the probe server completing its")
    print("     MCP handshake. If it never appears, the server did not load and nothing after")
    print("     this point means anything.")
    print()
    print("  3. WATCH THE SCREEN. The only question that matters is whether agy ever asks")
    print("     you anything about the probe tool -- a prompt, a link, a consent dialog.")
    print()
    if flow == "form":
        print("     form mode: expect a yes/no style question. If none appears, agy declares")
        print("     an elicitation capability it does not surface.")
    else:
        print("     url mode: expect a link, or an offer to open one. If it appears, open it")
        print("     -- that completes the gate out of band and this probe will notice.")
    print()
    print("  4. Answer it however you like, or ignore it. Ctrl-C agy when you are done.")
    print()
    print("  Watching for sentinels. Ctrl-C HERE when finished.")
    print()

    seen = set()
    try:
        while True:
            for n in ("CAPS.json", "URL.txt", "ELICITED.json", "URL_HIT.json", "NOTIFIED.json",
                      "RETRIED.json", "CALLED_control_tool", "CALLED_elicit_tool"):
                if n not in seen and os.path.exists(os.path.join(where, n)):
                    seen.add(n)
                    # Printed in full. This was truncated to 200 chars and the cut landed exactly
                    # on `latency_s` -- the field that separates "refused on sight" from
                    # "something waited", which is the whole question here.
                    print("   [%-22s] %s" % (n, json.dumps(_sentinel(where, n), default=str)),
                          flush=True)
            time.sleep(1)
    except KeyboardInterrupt:
        el = _sentinel(where, "ELICITED.json") or {}
        print()
        print(bar)
        print("  agy version           :", AGY_VERSION)
        print("  control tool ran      :", os.path.exists(os.path.join(where, "CALLED_control_tool")))
        print("  elicitation issued    :", bool(el.get("issued")))
        print("  client's answer       :", json.dumps(el.get("response")))
        print("  answered after        :", el.get("latency_s"), "s")
        print("  url opened out of band:", os.path.exists(os.path.join(where, "URL_HIT.json")))
        print("  gated tool completed  :", os.path.exists(os.path.join(where, "CALLED_elicit_tool")))
        print()
        print("  latency ~0 AND no prompt on screen -> no UI was attempted (a vendor finding).")
        print("  a prompt you actually saw          -> the automated harness was the problem.")
        return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--flow", choices=["hold", "required", "form", "both", "all"],
                    default="both")
    ap.add_argument("--seconds", type=int, default=150, help="wall clock per arm")
    ap.add_argument("--keep", action="store_true", help="leave the workdir for inspection")
    ap.add_argument("--manual", metavar="DIR",
                    help="prepare DIR and wait while a human runs agy there by hand. Use with "
                         "--flow form first: it is the control that decides what the url arms "
                         "are worth.")
    args = ap.parse_args()

    if args.manual:
        return manual(args.flow if args.flow in ("form", "hold", "required") else "form",
                      os.path.abspath(args.manual))

    flows = ({"both": ["hold", "required"],
          "all": ["form", "hold", "required"]}).get(args.flow, [args.flow])
    results = []
    for flow in flows:
        for answered in (False, True):
            label = f"{flow}/{'answered' if answered else 'unanswered'}"
            print(f"--- {label} ---", flush=True)
            r = arm(flow, answered, args.seconds, args.keep)
            results.append((label, r))
            print(json.dumps(r, indent=2, default=str), flush=True)

    print("\n" + "=" * 72)
    for label, r in results:
        if r.get("HARNESS_FAILED"):
            print(f"{label:<22} HARNESS FAILED -- measured nothing about agy. {r['why']}")
            continue
        print(f"{label:<22} control={r['control_ran']} issued={bool(r['elicited'])} "
              f"surfaced={r['url_surfaced_in_tui'].get('any')} agy-opened={r['opened_by_agy_unprompted']} "
              f"hit={bool(r['url_hit'])} notified={bool(r['notified'])} "
              f"retried={bool(r['retried'])} srv-completed={r['server_completed_call']} "
              f"client-took-it={r['client_showed_result']} "
              f"latency={(r['elicited'] or {}).get('latency_s')}")
    if any(r.get("HARNESS_FAILED") for _, r in results):
        print("\nAt least one arm did not run. Nothing here is a finding about agy -- fix the "
              "harness and re-run.\nA broken instrument reporting zeros is the failure this suite "
              "is built to refuse.")
        return 1
    print("\nThe control is the `unanswered` row of each pair: if its tool completed, the gate "
          "opened on its own\nand the answered row proves nothing.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
