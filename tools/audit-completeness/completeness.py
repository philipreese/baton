"""Recompute the audit's completeness register, so "we did all of it" is checkable rather than claimed.

WHY THIS EXISTS
---------------
A claim of completeness ships with the artifact that lets someone check it -- prose alone enforces
nothing (CLAUDE.md, "PROSE THAT NOBODY READS IS USELESS"). This is that artifact for the #527 audit
chain.

Each step takes a population that can be ENUMERATED and asserts every member carries a disposition.
`main()` is the list of them; do not restate it here -- a restated count is one `selfcheck.py`
now asserts against the code.

This script recomputes what is mechanically recomputable (populations, and which members carry a
disposition) and prints what it CANNOT check, because a completeness checker that hides its own
blind spots is the thing it exists to prevent.

    pixi run audit-completeness

SCOPE
-----
A standing check, wired into CI: `.github/workflows/ci.yml`'s `audit` job runs it, and `audit` is a
required leg of the aggregate `ci` gate. Run it locally too before a PR touching `spec/baton.md`,
`docs/vendor-*.md`, or `tools/vendor-verify/verify.py` -- a local run has the mirrored corpus CI
does not, so step 2 checks strictly more there.

It is standing because it was once scoped as a one-time instrument, then run cold nine days later
and failed: 11 decisions and 2 vendor-verify checks had accumulated with no disposition, invisible
because nothing was re-running it (back when decision records existed as a separate register --
spec v2.0, #1397, folded their content into spec/baton.md itself). A completeness check whose
population keeps growing cannot be frozen at the population it was born with.

Every check verifies that a REASON WAS WRITTEN DOWN -- never that the reason is any good. It catches
an omission, not a wrong judgement. Extend it when a population grows (a new vendor-verify check, a
new step worth enumerating) -- not for open-ended rigour with no named failure behind it.
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def read(path):
    p = os.path.join(ROOT, path)
    if not os.path.exists(p):
        return ""
    with open(p, encoding="utf-8", errors="replace") as f:
        return f.read()


def rule(title):
    print("\n" + "=" * 78)
    print(title)
    print("=" * 78)


def line(label, got, expected=None, note=""):
    ok = "  " if expected is None else ("OK" if got == expected else "!!")
    exp = "" if expected is None else f"  (expected {expected})"
    print(f" {ok} {label:<46} {got}{exp}{note and '  -- ' + note}")
    return expected is None or got == expected


def step1_sources():
    rule("STEP 1 -- every doc source considered has a disposition")
    survey = read("tools/vendor-survey/vendor_survey.py")
    included = {
        "claude docs (llms.txt)": "CLAUDE_INDEX" in survey,
        "agy docs (sitemap.xml)": "AGY_SITEMAP" in survey,
        "MCP specification (llms.txt)": "MCP_INDEX" in survey,
        "vendor CLI --help (both)": "fetch_cli_help" in survey,
        "agy changelog / terms / pricing / product": "EXTRA_SOURCES" in survey,
        "agy GitHub CHANGELOG": "github-CHANGELOG" in survey,
        "both vendors' issue trackers": "ISSUE_REPOS" in survey,
    }
    ok = True
    for name, present in included.items():
        ok &= line(name, "included" if present else "MISSING", "included")
    print("\n Excluded, with reason (each must be a deliberate call, not an omission):")
    for name, why in [
        ("vendor CLI runtime logs", "manual surface; read directly when a run misbehaves"),
        ("SDK package source", "both SDKs are API-key-only and were rejected (Rule 4)"),
        ("anything behind vendor auth", "not reachable from an agent session"),
        ("Anthropic API docs (docs.claude.com)", "AER spawns CLIs, never the API -- Rule 4"),
    ]:
        print(f"    - {name:<42} {why}")
    return ok


def step2_corpus():
    rule("STEP 2 -- every mirrored page carries an audit-register disposition")
    ledger = read(".vendor-survey/ledger.tsv")
    if not ledger.strip():
        print("    !! no audit register found -- run `pixi run vendor-survey` first")
        return False
    rows = [r for r in ledger.splitlines()[1:] if r.strip()]
    corpus_dir = os.path.join(ROOT, ".vendor-survey", "corpus")
    corpus_present = os.path.isdir(corpus_dir)
    pages = len([f for f in os.listdir(corpus_dir)]) if corpus_present else 0
    dispositions = {}
    for r in rows:
        parts = r.split("\t")
        if len(parts) >= 2:
            dispositions[parts[-1].strip()] = dispositions.get(parts[-1].strip(), 0) + 1
    # Two environments, deliberately two different claims (#589). Locally the mirrored corpus sits
    # beside the audit register, so the strongest check available is a cross-check: every file on disk
    # has a row. In CI only ledger.tsv is committed -- the 11MB corpus stays ignored -- so that
    # cross-check is IMPOSSIBLE rather than merely inconvenient, and reporting it as passing anyway
    # would be the "green means verified" failure this tool exists to catch. There the audit register
    # is treated as the snapshot it is and the check narrows to one it can actually make: every row
    # carries a disposition. Both arms still fail loudly; the CI arm just claims less.
    if corpus_present:
        ok = line("pages mirrored", pages)
        ok &= line("pages with an audit-register row", len(rows), pages,
                   "every page must have a disposition")
    else:
        dispositioned = [r for r in rows if r.split("	")[-1].strip()]
        ok = line("audit-register rows carrying a disposition", len(dispositioned), len(rows),
                  "corpus not mirrored here, so the file-count cross-check cannot run -- "
                  "a local run with the corpus present is what does that")

    # `PENDING-DEPTH` in the audit register is the HARVEST's recommendation ("worth a depth read"), not an
    # outcome -- vendor_survey.py runs before anyone reads anything. Counting it as a disposition
    # let this step report full coverage while 137 pages sat flagged, one of which (SEP-1036,
    # URL-mode elicitation) changed decision 0029. Same defect as a title column passing for a
    # reason in step 6: the check was weaker than the claim it certified.
    #
    # So the read-state is COMPUTED here, by joining the recommendation against whether the page is
    # actually cited in the audit prose. Citation is the strongest evidence available without a
    # human attestation, and it is recomputed on every run rather than recorded once and trusted.
    for d, n in sorted(dispositions.items()):
        print(f"      {d:<20} {n}")
    flagged = [r.split("\t") for r in rows if r.split("\t")[-1].strip() == "PENDING-DEPTH"]
    cited, uncited = [], []
    for p in flagged:
        (cited if page_is_cited(p[1], p[0]) else uncited).append(p)
    line("depth-flagged pages", len(flagged))
    # "carries a disposition", NOT "produced a finding". A page read and found inapplicable is
    # finished; requiring a finding would make out-of-scope pages permanently outstanding and
    # reward writing one up. The reason still has to be in the prose -- see the disposition table
    # in vendor-doc-audit.md, which is what closes this population.
    ok &= line("  ... carrying a disposition in the audit prose", len(cited), len(flagged),
               "a page with no disposition anywhere is genuinely unread")
    if uncited:
        print("\n    Depth-flagged and NOT cited anywhere -- the real outstanding population:")
        for p in sorted(uncited, key=lambda r: -int(r[4]))[:40]:
            print(f"      relevance {int(p[4]):>5}   {p[0]}/{p[1]}")
    return ok


AUDIT_PROSE = ["docs/vendor-doc-audit.md", "docs/vendor-capabilities.md", "docs/vendor-coverage.md"]
_prose = None


def page_is_cited(name, vendor):
    """Does the audit prose reference this mirrored page by name?

    Slugs are hierarchical (`agent-sdk__typescript` -> `agent-sdk/typescript`) and the docs cite
    them three ways: as a URL path, as `page.md:line` provenance, or as a backticked name. All
    three count. A bare English word that merely happens to match does NOT -- the leaf must appear
    with a delimiter around it, or `mcp` would match every sentence containing the word.

    A page's identity is vendor + name: `claude/mcp` and `agy/mcp` are different pages, and the
    audit register keeps the vendor in its own column. The fully-qualified form is always accepted,
    which is what makes short leaves like `mcp` citable at all.
    """
    global _prose
    if _prose is None:
        _prose = "".join(read(d) for d in AUDIT_PROSE).lower()
    # _prose is lowercased, so the patterns must be too -- audit-register names carry original case
    # (`github-CHANGELOG`), and matching case-sensitively silently reported a dispositioned page
    # as unread.
    slug = name.replace("__", "/").lower()
    qualified = f"{vendor.lower()}/{slug}"
    if re.search(re.escape(qualified), _prose):
        return True
    leaf = slug.split("/")[-1]
    if len(leaf) < 4:
        # Too short to match safely on its own -- "mcp" appears in half the prose. The qualified
        # form above is the only route for these, which is how the disposition table writes them.
        return False
    return any(re.search(p, _prose) for p in
               (re.escape(slug), re.escape(name.lower()), re.escape(leaf) + r"\.md",
                r"[/`]" + re.escape(leaf) + r"[`\s)\].,:]"))


def step3_gaps():
    """The reverse-traceability half of this step (every check -> the documented gap it closes) was
    dropped in the spec v2.0 reset (#1397): it rested entirely on docs/architecture-impact.md, now
    deleted, and neither surviving vendor register (`vendor-coverage.md`, `vendor-doc-audit.md`) ever
    carried a check name literally -- measured directly, zero hits for any of ~30 checks that had
    relied on the impact register alone. Recreating that traceability would mean authoring ~30 new
    citations into the vendor registers, a content rewrite this reset does not scope. What remains is
    the population counts below, still free and still real.
    """
    rule("STEP 3 -- vendor-verify backlog population counts")
    coverage = read("docs/vendor-coverage.md")
    verify = read("tools/vendor-verify/verify.py")
    checks = re.findall(r'^@check\("([^"]+)"', verify, re.M)
    struck = len(re.findall(r"~~", coverage)) // 2
    open_rows = len(re.findall(r"\*\*open\*\*", coverage))
    line("vendor-verify checks registered", len(checks))
    line("backlog rows struck (verified/corrected)", struck)
    line("rows explicitly marked open", open_rows)
    print("\n Still NOT auto-checkable: that a struck row was struck for the RIGHT reason.")
    return True


def step8_cited_checks_exist():
    """Every vendor-verify check name cited anywhere in the tree is actually registered.

    STEP 5 runs registered -> documented. This runs the inverse, citation -> registered, and the
    inverse is the one that had never been checked.

    Paid for by #554: a doc comment cited `agy.add-dir-grants-files-not-config` three times as the
    measurement the agy permission gate rested on. No such check exists. The real neighbouring check
    (`gate.add-dir-loads-no-config`) is claude-scoped and states the OPPOSITE. Nothing caught it --
    not the build, not the tests, not this script, not the author re-reading his own diff. An
    independent reviewer found it by running a grep nobody had thought to automate.

    A fabricated citation is worse than an uncited claim: it reads as evidence, it survives review by
    looking exactly like the real names around it, and the next person to trust it inherits a
    conclusion that was never measured. Note the ordering that makes this non-optional -- CLAUDE.md
    gate `common-sense` had ALREADY been extended that same day with "run `verify.py --list` before claiming a
    vendor fact is unmeasured", by the same author who then fabricated the name hours later. Prose
    did not hold. This is the population gate `record-once` describes as earning a checker: enumerable, and
    invisible when omitted.
    """
    rule("STEP 8 -- every cited vendor-verify check name actually exists")
    verify = read("tools/vendor-verify/verify.py")
    registered = set(re.findall(r'^@check\("([^"]+)"', verify, re.M))
    if not registered:
        print("    !! no @check registrations found -- cannot judge citations")
        return False

    # Derive the prefixes from the registrations rather than hardcoding them, so a new check group
    # is covered the day it is added rather than the day someone remembers to update this list.
    prefixes = sorted({name.split(".", 1)[0] for name in registered if "." in name})
    # Every real check name carries at least one hyphen in its suffix; requiring that keeps prose
    # like "the agy.hook family" and identifiers like `System.Text` out of the population.
    pattern = re.compile(
        r"\b(?:" + "|".join(re.escape(p) for p in prefixes) + r")\.[a-z0-9]+(?:-[a-z0-9]+)+\b")

    # Names deliberately written down BECAUSE they do not resolve -- prose describing the #554
    # fabrication. Kept as an explicit list with a reason rather than a looser regex, so the escape
    # hatch is enumerable too: anything added here is a claim that a name is meant to dangle, and a
    # reader can check that claim. Never add a name here to silence a real citation.
    INTENTIONALLY_UNRESOLVED = {
        "agy.add-dir-grants-files-not-config":
            "the #554 fabrication itself, named in the prose that records it",
    }

    roots = ["src", "tools", "docs", "tests", "spec", "CLAUDE.md"]
    exts = {".cs", ".py", ".md", ".json", ".ps1", ".sh"}
    bad = {}
    for root in roots:
        if os.path.isfile(root):
            paths = [root]
        else:
            paths = [os.path.join(d, f)
                     for d, _, fs in os.walk(root) for f in fs
                     if os.path.splitext(f)[1] in exts
                     and "bin" not in d.split(os.sep) and "obj" not in d.split(os.sep)]
        for path in paths:
            for cited in set(pattern.findall(read(path))):
                if cited not in registered and cited not in INTENTIONALLY_UNRESOLVED:
                    bad.setdefault(cited, []).append(path.replace("\\", "/"))

    ok = line("cited check names that resolve", "all" if not bad else f"{len(bad)} DO NOT",
              "all", "a citation naming nothing reads as evidence and is not")
    for cited, where in sorted(bad.items()):
        print(f"      NOT REGISTERED: {cited}")
        for w in sorted(where)[:4]:
            print(f"          cited in {w}")
    return ok


# Two predicates, because the two call sites need opposite tolerances.
#
# TOKEN_SHAPE: lowercase and hyphenated. Used on the register's own fence, where a digit must NOT be
# required -- agy serves Anthropic and OpenAI models, and a digit-free catalogue entry would make the
# sanity arm print "the PARSE is wrong" about a perfectly correct parse.
#
# PIN_SHAPE: the same, plus at least one DIGIT. Used on the tools/ walk, where `--model` appears in
# prose and every following word is a candidate -- the digit is what rejects `read-only`,
# `fail-closed` and `skip-permissions`. Cost: a real pin with no digit is invisible to the walk.
TOKEN_SHAPE = re.compile(r"[a-z][a-z0-9.]*(?:-[a-z0-9.]+)+")
PIN_SHAPE = re.compile(r"(?=.*[0-9])[a-z][a-z0-9.]*(?:-[a-z0-9.]+)+")


def register_models():
    """The `agy models` catalogue as the register records it: `(names, "")`, or `(None, why)`.

    The register records the CLI's own output verbatim in a fenced block. Parsing that block rather
    than keeping a hand-maintained list here makes re-running `agy models` into the register the
    single act that updates every caller -- record once, per the gate.

    Residual risk, the only one worth carrying: a second fence added BEFORE the models block, in the
    SAME section, is still taken instead. The shape arm below is the backstop, and it only catches a
    block whose tokens are not model names -- a fence holding some OTHER model list parses silently
    wrong.
    """
    caps = read("docs/vendor-capabilities.md")
    section = re.search(r"##\s+`agy models`[^\n]*\n(.*?)(?=\n##\s|\Z)", caps, re.S)
    if not section:
        return None, "could not locate the `agy models` section in docs/vendor-capabilities.md"
    fence = re.search(r"```[a-zA-Z]*\n(.*?)```", section.group(1), re.S)
    if not fence:
        return None, "the `agy models` section carries no fenced block"
    accepted = set(fence.group(1).split())

    # Asserts the PARSE, not just non-emptiness: without it a mis-parse blames every PIN while the
    # fault is the parse -- the right verdict pointing at the wrong file. (`line()` with no
    # `expected` prints no marker and returns True, so the count alone cannot catch it.)
    # Two arms because they support two different conclusions: non-model-shaped tokens DO establish a
    # bad parse, a surprising COUNT does not -- a catalogue legitimately shrinking to 4 would trip it,
    # and blaming the parse there would name the wrong file.
    unshaped = {n for n in accepted if not TOKEN_SHAPE.fullmatch(n)}
    if unshaped:
        return None, ("the `agy models` block parsed to token(s) that are not model names -- the"
                      f" PARSE is wrong, not the pins. Got: {sorted(unshaped)[:8]}")
    # A smoke alarm, not a diagnosis: wide enough that only a wild parse trips it, and the message
    # says to go look rather than naming a culprit. The real count is printed by the caller.
    if not 5 <= len(accepted) <= 40:
        return None, (f"the `agy models` block parsed to {len(accepted)} names, outside the expected"
                      " 5..40. Either the catalogue changed dramatically or the parse drifted --"
                      " check which before trusting any verdict below.")
    return accepted, ""


# A model name a check passes DELIBERATELY because the catalogue does not list it. Step 9's whole
# job is to fail an uncatalogued name, so a probe whose entire point is rejection reads as the defect
# it is testing for -- `effort.agy-rejection-is-per-model` exists to establish what agy does with
# `gemini-3-pro`, which by construction cannot be a catalogued name.
#
# Per-line and explicit rather than a name list: a list would exempt that string everywhere,
# including somewhere it really was a stale pin. The marker sits on the line it excuses, so the
# intent is readable where the reader is, and `grep` finds every one.
UNCATALOGUED_ON_PURPOSE = "aer-uncatalogued-on-purpose"


def is_probe_input(line: str) -> bool:
    """Whether this line's model name is a deliberately-invalid probe input.

    Pure, so `selfcheck.py` can hold BOTH directions -- that a marked line is exempt, and that an
    unmarked uncatalogued name still fails. An exemption with only the first half asserted is a
    switch for turning the step off.
    """
    return UNCATALOGUED_ON_PURPOSE in line


def step9_pinned_models_exist():
    """Every pinned model name is one its OWN vendor's catalogue carries.

    Two joins, one per vendor with a catalogue to join against:
      * `agy` -- every pinned name is one `agy models` lists (the original arm; everything below
        about population and scope describes it).
      * `codex` -- every `WorkerTiers.json` tier on the codex adapter names a model AND an effort the
        recorded `model/list` answer at `src/Baton.Vendors/codex-model-list-2026-09-08.jsonl` carries
        (#1863, the codex arm below). Added when the first codex tier pin shipped: until then the
        count of tier pins with no catalogue join was zero, and it went to one. `CodexWorkerAdapter`
        already refuses an unrecorded model, but only at DISPATCH -- after the lane has been queued
        and the operator is waiting -- so the pin's validity rested on a human read of the recording.

    Population, precisely: the shared tier pins in `src/Baton.Vendors/WorkerTiers.json` (#888, the
    canonical source the engine reads -- `tools/baton-agy-loop/dispatch.py` read the same file until
    #1759 retired it), `verify.py`'s `CHEAP['agy']`, and any `agy` model name in a pin POSITION under
    `tools/`. The catalog moved out of `tools/` when the pins left the `dispatch.py` literal, so "in a
    tool" alone no longer bounds it -- the src/ file is read directly (see the tier-pin arm below),
    and the `tools/` textual walk covers the rest.

    Paid for the same day this step was written. `dispatch.py`'s new template set -- written partly
    to STOP stale pins -- shipped its first draft pinning `gemini-3.1-pro`. `agy models` lists
    `gemini-3.1-pro-high` and `gemini-3.1-pro-low`; the bare name is not an accepted value, so the
    template would have failed at dispatch, after the operator had already paid for a run. That is
    #547's failure class ("nothing guards a stale model pin reaching the product on agy") reproduced
    inside the file meant to prevent it, by an author who had read the register hours earlier.

    Prose could not have caught it: `gemini-3.1-pro` reads exactly like a real model name, appears as
    a substring of two real ones, and is used correctly in surrounding prose about the grid's holes.
    Only a join against the enumerated set separates it from the valid names -- which is precisely the
    population CLAUDE.md gate `record-once` describes as earning a checker.

    THIS IS NOT THE FIRST CHECK OF ITS KIND, AND SAYING SO IS THE POINT
    `tools/smoke-preflight/preflight.py` already validates model pins against agy's catalogue, was
    built for the same failure class (`gemini-3-flash` pinning nothing for months), and does it
    BETTER where it runs: it queries `agy models` live rather than joining against a recording.

    They are complementary, and the split is not a matter of taste:
      * preflight's population is `tests/Baton.Cli.SmokeTests` and its fixtures. It does not read
        `tools/`, which is where these pins live.
      * preflight needs a live `agy` binary and degrades to a WARNING without one, so it cannot gate
        anything in CI. It runs as a `depends-on` of the `smoke-*` tasks, which are permanently
        human-gated live runs.
      * this step reads a recorded register, needs no vendor, and runs in CI's `audit` job.
    So: preflight covers tests/ precisely but only when a person runs a smoke test; this covers
    tools/ approximately on every PR. Neither subsumes the other.

    SCOPE, stated because two limits are narrower than the title
      * **agy and codex only, and only codex's TIER pins.** The `claude` pins (`opus`, `haiku`) are
        CLI aliases, and `claude` has no catalogue subcommand at all -- `claude models` is taken as a
        PROMPT and answered, which spends usage (preflight's header documents this). So nothing here
        validates them. The codex arm reads `WorkerTiers.json` only: the `tools/` textual walk below
        is an agy-shaped join (`accepted` is `agy models`' answer), so a `gpt-` name found there is
        checked against agy's list, which serves OpenAI models too -- widening the codex arm to that
        walk would need a second pass and is not what shipped.
      * **The tools/ scan is TEXTUAL**, and declared as a limitation rather than presented as
        equivalent to reading the code. It finds names in a pin POSITION -- next to `--model`, or as
        a `"model":` value -- which is what keeps prose about model names out of the population. A
        pin built at runtime, or written in a shape this pattern does not match, is invisible to it.
        `WorkerTiers.json` (the shared worker-role catalog, #888) is additionally read directly, so
        the tier model pins do not rest on the regex -- and, unlike the pre-#1759 `dispatch.py`
        import this step used to make, that direct read survived dispatch.py's retirement (#887's
        front door was always the intended replacement).
    """
    rule("STEP 9 -- every pinned model name is one its own vendor's catalogue carries"
         " (agy: `agy models`; codex tiers: the recorded model/list)")
    accepted, why = register_models()
    if accepted is None:
        print(f"    !! {why}")
        return False
    line("model names enumerated by the register", len(accepted))

    # Claude's own CLI aliases -- the one genuine exclusion from the walk below. Mirrors
    # `smoke-preflight/preflight.py`'s CLAUDE_ALIASES rather than inventing a second list; that file
    # records that `haiku` was verified to resolve via `modelUsage` rather than assumed.
    CLAUDE_CLI_ALIASES = {"opus", "sonnet", "haiku", "fable"}

    pins = []  # (where, model)

    # The tier model pins live in the shared worker-role catalog now (#888), not a dispatch.py literal:
    # WorkerTiers.json maps each tier to {adapter, model, effort}, and the engine reads it directly
    # (`tools/baton-agy-loop/dispatch.py` read the same file too, before #1759's retirement removed
    # it). Check that source DIRECTLY rather than importing dispatch.py -- the check then reads the truth instead of a
    # Python view rebuilt from the same file, and it survived dispatch.py's retirement (the front door
    # replaces it, #887) instead of breaking with it.
    tiers_path = os.path.join(ROOT, "src", "Baton.Vendors", "WorkerTiers.json")
    if not os.path.exists(tiers_path):
        # A missing source is a HARD failure, not a quiet skip. Skipping would let a rename drop the
        # tier pins from the population while `verify.py`'s pin kept the step green -- a check that
        # passes because it stopped looking, which is the exact defect step 2's comment names.
        print(f"    !! {tiers_path} not found -- the tier model pins cannot be checked, so this step"
              " cannot make its claim")
        return False

    with open(tiers_path, encoding="utf-8") as f:
        tier_map = json.load(f)
    for tier_name, tier in tier_map.items():
        # agy tiers only -- the claude pins are CLI aliases this step's SCOPE excludes. A tier
        # that omits `model` (the vendor's own default) pins nothing to check.
        if tier.get("adapter") == "agy" and tier.get("model"):
            pins.append((f"WorkerTiers.json[{tier_name!r}]", tier["model"]))
    if not pins:
        print("    !! WorkerTiers.json defines no gemini tier with a model pin -- the catalog has been"
              " emptied, renamed, or restructured, so this step is no longer checking it")
        return False

    # verify.py's CHEAP carries the same kind of pin and goes stale the same way. Regexed rather than
    # imported: verify.py does work at import time that this script has no business triggering.
    # Each source is required to contribute, for the same reason the missing-file arm above is a hard
    # failure: a population that silently shrinks is how a check keeps printing OK about less and less.
    before_cheap = len(pins)
    cheap = re.search(r'"agy":\s*\[([^\]]*)\]', read("tools/vendor-verify/verify.py"))
    if cheap:
        for tok in re.findall(r'"([^"]+)"', cheap.group(1)):
            if tok.startswith("gemini-") or tok.startswith("claude-") or tok.startswith("gpt-"):
                pins.append(("verify.py CHEAP['agy']", tok))
    if len(pins) == before_cheap:
        print("    !! verify.py's CHEAP['agy'] no longer yields a model pin -- if that is deliberate,"
              " remove this arm rather than letting it silently check nothing")
        return False

    # Everything else under tools/, found in a PIN POSITION rather than anywhere in the prose. The
    # docstring's population includes "any agy model name in a pin position under tools/"; without this
    # arm the code read two named sources while the claim covered that tree, which is the claim-wider-
    # than-measurement defect this whole step exists to catch, in the step itself.
    pin_position = re.compile(
        r'(?:--model["\s,]+|"[Mm]odel"\s*:\s*")([A-Za-z][A-Za-z0-9.\-]*)')
    seen = {(w, m) for w, m in pins}
    for dirpath, _, filenames in os.walk(os.path.join(ROOT, "tools")):
        # Build artefacts are not source. Walking them made the audit's runtime depend on
        # whether someone had built, and a hit would have been reported with a bin/ path.
        parts = dirpath.split(os.sep)
        # Component test, not a substring test, so a directory merely starting with bin/obj is not
        # excluded. Note this walk is ABSOLUTE, so a checkout under a path component named bin or
        # obj skips tools/ entirely -- unlike step 8's relative walk.
        if "__pycache__" in parts or "obj" in parts or "bin" in parts:
            continue
        for fn in filenames:
            if not fn.endswith((".py", ".md", ".toml", ".json")):
                continue
            full = os.path.join(dirpath, fn)
            rel = os.path.relpath(full, ROOT).replace("\\", "/")
            with open(full, encoding="utf-8", errors="replace") as f:
                for lineno, text in enumerate(f, 1):
                    if is_probe_input(text):
                        continue
                    for name in pin_position.findall(text):
                        # Both conditions are load-bearing. A prefix test on `gemini-|gpt-` would
                        # skip `claude-sonnet-4-6` and `claude-opus-4-6-thinking`, which `agy models`
                        # LISTS -- agy serves Anthropic models too. "Not an alias" alone is too wide,
                        # because `--model` appears in prose here and every following word becomes a
                        # candidate. PIN_SHAPE is what separates a model identifier from English.
                        if name in CLAUDE_CLI_ALIASES or not PIN_SHAPE.fullmatch(name):
                            continue
                        key = (f"{rel}:{lineno}", name)
                        if key not in seen:
                            seen.add(key)
                            pins.append(key)
    ok = True
    for where, model in pins:
        good = model in accepted
        ok &= good
        print(f" {'OK' if good else '!!'} {where:<46} {model}"
              f"{'' if good else '  -- not listed by `agy models`'}")
    return bool(ok) & _codex_tier_pins_are_recorded(tier_map)


# The recorded `codex model/list` answer -- the same file `CodexWorkerAdapter.ValidateModel` refuses
# against, so the audit and the adapter join a codex pin to ONE register rather than two readings of
# it. `docs/vendor-codex-probe-2026-09-04.md` is the record of how it was captured, and says the list
# should be re-probed rather than trusted forever; a codex release that retires a model makes this
# file stale, and re-running that probe is what corrects it.
CODEX_RECORDING = "src/Baton.Vendors/codex-model-list-2026-09-08.jsonl"


def _codex_tier_pins_are_recorded(tier_map):
    """#1863: every codex tier pin names a model AND an effort the recorded catalogue carries.

    Joined the way the agy pins above are joined to `agy models`, with one addition the agy arm has
    no equivalent for: codex's recording enumerates each model's `supportedReasoningEfforts`, so the
    EFFORT is checkable too, and a tier pinning `xhigh` on a model that stops offering it is the same
    class of stale pin as a retired model name.

    Every failure here is a HARD failure, including "no codex tier pins at all". That mirrors the agy
    arm's zero-population refusal for its reason: a population that silently shrinks is how a check
    keeps printing OK about less and less. If a later ruling moves every tier off codex, deleting
    this arm is a deliberate edit rather than a check that quietly stopped looking.
    """
    path = os.path.join(ROOT, *CODEX_RECORDING.split("/"))
    if not os.path.exists(path):
        print(f"    !! {CODEX_RECORDING} not found -- the codex tier pins cannot be checked, so this"
              " step cannot make its claim")
        return False

    # JSONL: one JSON value per line, the `model/list` answer being the object carrying
    # `result.data`. Located by SHAPE rather than by line number, so a re-probe that emits the
    # session/status lines in a different order does not silently stop finding the catalogue.
    catalogue = {}
    with open(path, encoding="utf-8") as f:
        for text in f:
            text = text.strip()
            if not text:
                continue
            try:
                value = json.loads(text)
            except json.JSONDecodeError:
                continue
            data = value.get("result", {}).get("data") if isinstance(value, dict) else None
            if not isinstance(data, list):
                continue
            for entry in data:
                if isinstance(entry, dict) and entry.get("id"):
                    catalogue[entry["id"]] = {
                        e.get("reasoningEffort")
                        for e in entry.get("supportedReasoningEfforts") or []
                        if isinstance(e, dict)
                    }

    if not catalogue:
        print(f"    !! {CODEX_RECORDING} carries no `result.data` model list -- the recording has been"
              " emptied or reshaped, so this arm is no longer checking anything")
        return False
    line("codex models enumerated by the recording", len(catalogue))

    pins = [(name, tier.get("model"), tier.get("effort"))
            for name, tier in tier_map.items()
            if tier.get("adapter") == "codex" and tier.get("model")]
    if not pins:
        print("    !! WorkerTiers.json defines no codex tier with a model pin -- if that is deliberate,"
              " remove this arm rather than letting it silently check nothing")
        return False

    ok = True
    for name, model, effort in pins:
        efforts = catalogue.get(model)
        if efforts is None:
            why = "  -- not in the recorded codex model/list"
        elif effort and effort not in efforts:
            why = f"  -- effort '{effort}' is not one {model} records ({', '.join(sorted(efforts))})"
        else:
            why = ""
        ok &= not why
        where = f"WorkerTiers.json[{name!r}]"
        print(f" {'OK' if not why else '!!'} {where:<46} {model} / {effort or '(vendor default)'}{why}")
    return ok


# Multi-word phrases only, and matched with word boundaries -- a first version used bare "open" and
# "unknown" and got 43 hits, nearly all false: "reopened", "opened", etc. A single word is too common
# in ordinary English to signal staleness; these phrases are specific enough that a false hit is
# itself worth a look.
STALENESS_PHRASES = (
    "still open", "still unknown", "remains open", "not yet landed", "not yet resolved",
    "not yet probed", "unprobed", "highest-value open", "no issue owns", "TODO",
)
CITATION_DIRS = ("docs", "spec")
CITATION_EXCLUDE = ()
ISSUE_RE = re.compile(r"#(\d{2,5})\b")


def repo_is_unreachable(stderr: str) -> bool:
    """Does this `gh` failure mean the REPO NAME is wrong, rather than the network being down?

    The distinction is the whole point: a wrong name is the one failure mode that must not skip.
    Pointed at a repo that does not exist, `gh issue list` fails, STEP 4 prints SKIPPED, and
    `main()`'s rollup excludes skips -- so the stale-citation check silently stops running while
    `pixi run audit-completeness` keeps exiting 0. That happened once already, in the branch that
    prepared the baton rename: every markdown link could move ahead of the flip, and this live call
    could not. GitHub's GraphQL layer resolves the current name and does not follow the rename
    redirect that rescues links, so the window between repointing this line and the flip is exactly
    when the check would go quiet.
    """
    text = (stderr or "").lower()
    return any(marker in text for marker in (
        "could not resolve to a repository",   # GraphQL, the shape `gh issue list` returns
        "not found",                           # REST-flavoured wording, e.g. `gh api`
        "no such repository",
    ))


def step4_stale_citations():
    rule("STEP 4 -- no doc cites a closed issue as though it were still open")
    gh = _shutil_which("gh")
    if gh is None:
        print("    SKIPPED -- `gh` not on PATH. This step needs it; it does not fail without it.")
        return None
    try:
        out = subprocess.run(
            # Flipped with the rename itself (docs/runbooks/repo-rename.md step 2), which is why
            # this one line moved separately from every markdown link in #823: it is a live call,
            # and `repo_is_unreachable` above is what makes a wrong name here loud rather than
            # silently skipped.
            ["gh", "issue", "list", "--repo", "aer-works/baton", "--state", "all",
             "--limit", "1000", "--json", "number,state"],
            capture_output=True, text=True, cwd=ROOT, timeout=30)
    except (OSError, subprocess.TimeoutExpired):
        print("    SKIPPED -- `gh` did not respond (offline, or not authenticated).")
        return None
    if out.returncode != 0:
        if repo_is_unreachable(out.stderr):
            print(f"    !! the repo this step queries does not resolve: {out.stderr.strip()[:200]}")
            print("       This is not a skip. Every other failure here (offline, unauthenticated,")
            print("       rate-limited) leaves the repo NAME correct; this one means the name is")
            print("       wrong, and a wrong name makes STEP 4 check nothing while the rollup stays")
            print("       green. See docs/runbooks/repo-rename.md step 2.")
            return False
        print(f"    SKIPPED -- `gh issue list` failed: {out.stderr.strip()[:200]}")
        return None
    import json
    try:
        issues = {i["number"]: i["state"] for i in json.loads(out.stdout)}
    except (ValueError, KeyError):
        print("    SKIPPED -- could not parse `gh issue list` output.")
        return None
    if not issues:
        print("    SKIPPED -- `gh` returned zero issues; treating as not-actually-queryable.")
        return None

    findings = []
    for base in CITATION_DIRS:
        for dirpath, _, filenames in os.walk(os.path.join(ROOT, base)):
            for fn in filenames:
                if not fn.endswith(".md"):
                    continue
                rel = os.path.relpath(os.path.join(dirpath, fn), ROOT).replace("\\", "/")
                if any(rel.startswith(x) or rel == x for x in CITATION_EXCLUDE):
                    continue
                for lineno, text in enumerate(read(rel).splitlines(), start=1):
                    lowered = text.lower()
                    if not any(re.search(r"\b" + re.escape(w) + r"\b", lowered)
                               for w in STALENESS_PHRASES):
                        continue
                    for m in ISSUE_RE.finditer(text):
                        n = int(m.group(1))
                        if issues.get(n) == "CLOSED":
                            findings.append((rel, lineno, n, text.strip()[:100]))

    ok = line("closed issues cited as open/unresolved", len(findings), 0,
              "each is a doc that has not caught up with GitHub")
    for rel, lineno, n, snippet in findings:
        print(f"      {rel}:{lineno}  cites #{n} (CLOSED)  -- {snippet}")
    return ok


GATE_HEADING = re.compile(r"^\*\*\d+\..*?—\s*`([a-z][a-z-]+)`", re.M)
# The word "gate" followed by a bare number, in any casing. Not `gate-<n>` and not `Gate` alone.
# Written without a literal example: this file is inside the population it scans, and spelling one
# out made the lint report its own documentation as four violations.
NUMERIC_GATE = re.compile(r"\bgates?\s+\d+", re.I)
# A backticked slug in the same breath as the word "gate", which is how every real citation reads.
# The slug group is deliberately CASE-SENSITIVE while the word is not: under a blanket re.I the
# `[a-z]` class also matches capitals, and prose about a "validity gate `DependsOn`" -- not a
# shipping gate at all -- was reported as citing a gate that does not exist.
CITED_SLUG = re.compile(r"(?i:\bgates?)\s+`([a-z][a-z-]+)`")
GATE_SCAN_DIRS = ("docs", "spec", "src", "tools", "tests", ".github")
GATE_SCAN_FILES = ("CLAUDE.md", "README.md", "pixi.toml")
GATE_SCAN_EXCLUDE = ()
GATE_SCAN_SUFFIXES = (".md", ".py", ".cs", ".toml", ".yml", ".yaml", ".rs", ".go")


def generated_changelog(filename: str) -> bool:
    """Whether a file is a release-please-generated changelog, judged by NAME alone.

    #1365/#1367: generated changelogs transcribe immutable commit messages verbatim --
    into EVERY affected package's changelog for a monorepo release -- so both a numeric
    gate citation (step 10) and N identical transcriptions of one commit line
    (record-once) are unactionable there at every link: the commit is history, the
    transcription is mechanical, and each blocked release PR #309 in turn. The living-
    document lints share this one predicate rather than growing two; keeping NEW commit
    messages clean is review-time work. Pure, so selfcheck can drive both arms.
    """
    return filename == "CHANGELOG.md"


def gate_slugs(claude_md: str) -> set[str]:
    """The gate slugs CLAUDE.md actually defines, from its own headings."""
    return set(GATE_HEADING.findall(claude_md))


# GitHub closes an issue whenever a closing keyword sits beside a reference. It does not care about
# negation, tense, or whether the text is in a table cell, a quotation or a code span. THREE real
# incidents, each costing a reopen, an explanatory comment and a correction to the record:
#
#   #684  "filed, not fixed: #NNN"          negated
#   #692  "Does not close #NNN or #MMM"     negated
#   #694  "| #NNN | ... | closed #MMM |"    DESCRIPTIVE, past tense, in a table -- and #694 was the
#                                           PR adding this very lint, which passed while doing it
#
# The first version keyed on NEGATION, because that was the shape of the two incidents in front of
# it. That is scoping a check to the symptom: the mechanism never involved negation at all. The rule
# below keys on POSITION instead, which is the only thing that distinguishes a deliberate
# declaration from an accident.
#
# A line BEGINNING with a closing keyword is a declaration and is exempt in full -- including a
# second occurrence on that line, since `Closes #675. Closes #676.` is one deliberate act. Anywhere
# else is flagged. `\W{0,3}` mirrors what GitHub itself requires, the keyword immediately before the
# number: "fixed BY #99" closes nothing, so this stays silent on it. A lint that fires where the
# parser does not would train authors to reword around a phantom.
CLOSING_KEYWORD = re.compile(
    r"\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\b\W{0,3}#(\d+)", re.IGNORECASE)
# A list marker before the keyword is still a declaration: "This PR:\n- Closes #12" is the most
# common PR-template shape there is, GitHub closes on it, and #975's second reader found it matched
# NEITHER register — the accident lint flagged a deliberate close while the partial-closure lint
# never inspected its target.
DECLARATION_LINE = re.compile(
    r"^\s*(?:(?:[-*+]|\d+[.)])\s+)?(?:[*_]{1,2})?(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\b", re.IGNORECASE)


def negated_close_faults(body: str) -> list:
    """Issue numbers a PR body will close that it does not mean to.

    Named for the negated case it was first written for; it now covers every accidental form, which
    is what the name would say if renaming it were free of churn across CI and two arm files.

    Pure, and takes the body as text rather than reaching for the GitHub API, for the same reason
    `gate_citation_faults` does: a lint that can only run against a real PR cannot be shown to
    discriminate. `selfcheck.py` drives it with all three incident bodies verbatim.
    """
    faults = []
    for line in (body or "").splitlines():
        if DECLARATION_LINE.match(line):
            continue
        faults.extend(int(n) for n in CLOSING_KEYWORD.findall(line))
    return faults


# The partial-closure lint (#975). Named failure: PR #961's body declared `Closes #903` while three
# of that issue's four scopes were unbuilt -- the remainder survived only as a closure comment,
# invisible to the open backlog for a day until rediscovered by accident and re-homed (#971-#973).
# A closure comment is not a backlog; the moment the issue closed, the unbuilt work had no open home.
#
# Honest scope: #903's remaining scopes were prose headings, not boxes, and this lint would not have
# seen them. It enforces the CHECKABLE form; the companion convention -- stated here once, repeated
# only by the failure message -- is that multi-scope issue bodies carry their scopes as task-list
# boxes (`- [ ]`) precisely so a partial closure becomes machine-visible.
# Deliberately wider than what GitHub certainly renders as a checkbox: the numbered form may draw
# as literal text, but an author who wrote "1. [ ] scope" meant an unbuilt scope either way, and a
# false fire on coincidental prose of that exact shape is far less likely than a numbered scope
# list. Wide is the safe direction for a lint whose miss strands work outside the backlog.
UNCHECKED_BOX = re.compile(r"^\s*(?:[-*+]|\d+[.)])\s+\[ \]")
FENCE_LINE = re.compile(r"^\s*(`{3,}|~{3,})")


def declared_closure_targets(body: str) -> list[int]:
    """Issue numbers a PR body DELIBERATELY closes: keyword references on declaration lines.

    The exact complement of `negated_close_faults`, built from the same two regexes so the two
    lints can never disagree about which close is meant. Targeting is strict (operator constraint,
    2026-08-04): only issues the body declares closed are inspected by the partial-closure lint --
    an umbrella issue that is merely referenced never trips it, however many open boxes it carries,
    because closing a DIFFERENT issue than the one left partially built is often exactly right.
    """
    targets = []
    for line in (body or "").splitlines():
        if DECLARATION_LINE.match(line):
            targets.extend(int(n) for n in CLOSING_KEYWORD.findall(line))
    return list(dict.fromkeys(targets))


def unchecked_scope_lines(issue_body: str) -> list[str]:
    """The unchecked task-list boxes in an issue body, fenced code blocks excluded.

    Pure, same reason as `negated_close_faults`: a lint that can only run against a live issue
    cannot be shown to discriminate. Fence tracking follows CommonMark's closing rule, which the
    first draft did not (#975's second reader): a closer must use the opener's marker character,
    run at least as long, and carry nothing but whitespace after it. Getting that wrong in either
    direction is costly — a shorter nested run closing a longer fence un-hides quoted example
    boxes, and treating ```js as a closer desyncs the tracker so every REAL box after it is
    silently swallowed for the rest of the document.
    """
    boxes = []
    fence = None  # (marker char, run length) of the open fence
    for line in (issue_body or "").splitlines():
        m = FENCE_LINE.match(line)
        if m:
            run = m.group(1)
            rest = line[m.end():]
            if fence is None:
                fence = (run[0], len(run))
            elif run[0] == fence[0] and len(run) >= fence[1] and not rest.strip():
                fence = None
            continue
        if fence is None and UNCHECKED_BOX.match(line):
            boxes.append(line.strip())
    return boxes


def partial_closure_faults(declared: dict) -> dict:
    """{issue number: its unchecked box lines} for every declared target that still has any.

    `declared` maps each declared closure target to its issue body -- the caller owns fetching,
    so this stays pure and the selfcheck arms can drive it with planted bodies.
    """
    faults = {}
    for number, body in declared.items():
        boxes = unchecked_scope_lines(body)
        if boxes:
            faults[number] = boxes
    return faults


def gate_citation_faults(files: dict, slugs: set[str]) -> list:
    """Every gate citation that cannot survive a renumbering, or names a gate that does not exist.

    `files` maps a display path to its text. Pure, so a checker can drive it with planted input --
    a lint that can only be run against the real tree cannot be shown to discriminate.

    Two faults, one cause. CLAUDE.md's own gate list says to cite a gate by its slug and never its
    number, because numbers are positional and merging two gates once already invalidated every
    citation in the repo. That instruction had been prose for months, and prose does not renumber
    citations: `pixi.toml` cited a gate ordinal past the end of the list.
    """
    faults = []
    for path, text in sorted(files.items()):
        for lineno, raw in enumerate(text.splitlines(), start=1):
            for m in NUMERIC_GATE.finditer(raw):
                faults.append((path, lineno, "cites a gate by NUMBER", m.group(0), raw.strip()[:90]))
            for m in CITED_SLUG.finditer(raw):
                if m.group(1) not in slugs:
                    faults.append((path, lineno, "cites a gate slug that does not exist",
                                   m.group(1), raw.strip()[:90]))
    return faults


def step10_gate_citations():
    """No file cites a shipping gate by a number, or by a slug CLAUDE.md does not define."""
    rule("STEP 10 -- every gate citation survives the list being renumbered")
    slugs = gate_slugs(read("CLAUDE.md"))
    if not slugs:
        print("    !! no gate headings found in CLAUDE.md -- the expected set is empty, so this"
              " step cannot judge any citation")
        return False

    files = {}
    for name in GATE_SCAN_FILES:
        files[name] = read(name)
    for base in GATE_SCAN_DIRS:
        for dirpath, dirnames, filenames in os.walk(os.path.join(ROOT, base)):
            dirnames[:] = [d for d in dirnames if d not in (".git", "bin", "obj", "__pycache__")]
            for fn in filenames:
                if not fn.endswith(GATE_SCAN_SUFFIXES):
                    continue
                if generated_changelog(fn):
                    continue
                rel = os.path.relpath(os.path.join(dirpath, fn), ROOT).replace("\\", "/")
                if any(rel.startswith(x) for x in GATE_SCAN_EXCLUDE):
                    continue
                files[rel] = read(rel)

    line("gate slugs defined by CLAUDE.md", len(slugs))
    line("files scanned for gate citations", len(files))
    faults = gate_citation_faults(files, slugs)
    ok = line("citations that cannot survive a renumbering", len(faults), 0,
              "a number is positional; the slug is what survives the next restructure")
    for path, lineno, why, what, snippet in faults:
        print(f"      {path}:{lineno}  {why}: {what!r}  -- {snippet}")
    return ok


# STEP 11 (register-pin agreement) was retired by #952, which removed the hand-written copies it
# policed instead of keeping them synchronized: decision-audit.md no longer transcribes a record
# count (its text cites the command that computes one), and the vendor version pin lives only in
# vendor-capabilities.md's dated history table, pointed at by the other two headers. The two #797
# incidents that step guarded against are impossible without the copies. History: git log on this
# comment.


def step13_structural_claims():
    """#314: the checkable slice of "the spec is the source of truth" -- structural claims.

    Three asserts, each a prose claim the tree can falsify the day it drifts:
    - CLAUDE.md's repo-structure map names exactly the src/* projects on disk, both directions.
      First live catch, before this step even ran in CI: Baton.Mcp and Baton.Mcp.Host had shipped
      without the map noticing.
    - Every src/... path cited by spec/*.md resolves in the tree.
    - Every docs/runbooks/*.md referenced from pixi.toml exists.

    Deliberately absent, recorded here and on #314: the reverse runbook direction (three runbooks
    are procedure records with no pixi task on purpose); navigation destinations (the redesign
    deletes that shell); template ids (no live doc restates them).
    """
    rule("STEP 13 -- structural claims: repo map, cited src paths, runbook references")
    ok = True

    src = os.path.join(ROOT, "src")
    on_disk = {d for d in os.listdir(src) if os.path.isdir(os.path.join(src, d))} if os.path.isdir(src) else set()
    block = re.search(r"## Repo structure.*?```(.*?)```", read("CLAUDE.md"), re.S)
    # #1458: the regex used to require a literal "." after "Baton" (every src/ project was
    # "Baton.X"), which cannot match a bare "Baton/" entry -- the 3b consolidation introduced
    # exactly one (src/Baton, the engine, ex-Baton.Flow). The trailing "?" is what widens the
    # pattern to match both "Baton/" and "Baton.X/" without also matching an unrelated "Batonfoo/".
    mapped = set(re.findall(r"(Baton(?:\.[A-Za-z.]+)?)/", block.group(1))) if block else set()
    # A check over an empty population passes vacuously — assert the anchors held before
    # trusting the comparison (found by #314's second reader).
    ok &= line("src/ directories found (a 0 here means the scan itself broke)", 1 if on_disk else 0, 1)
    ok &= line("repo-map entries parsed from CLAUDE.md (0 = anchor regex broke)", 1 if mapped else 0, 1)
    missing = sorted(on_disk - mapped)
    ghosts = sorted(m for m in mapped - on_disk if os.path.isdir(os.path.join(ROOT, "src")))
    ok &= line("src/ projects missing from CLAUDE.md's repo map", len(missing), 0,
               "Baton.Mcp shipped invisibly once already")
    for name in missing:
        print(f"      NOT IN MAP: src/{name}")
    ok &= line("repo-map entries with no src/ directory behind them", len(ghosts), 0)
    for name in ghosts:
        print(f"      GHOST: {name}/")

    bad_paths = []
    spec_files = [f"spec/{f}" for f in os.listdir(os.path.join(ROOT, "spec")) if f.endswith(".md")]
    for doc in spec_files:
        # A doc's own "## Naming note (transitional)" tail (spec/baton.md's rename plan, e.g.
        # `src/Baton`) cites paths a FUTURE PR creates, not the current tree -- excluded the same
        # way `aer-uncatalogued-on-purpose` excludes a deliberately-invalid probe input above.
        text = read(doc).split("## Naming note (transitional)")[0]
        for cited in sorted(set(re.findall(r"src/[A-Za-z0-9._/-]+[A-Za-z0-9]", text))):
            if not os.path.exists(os.path.join(ROOT, cited)):
                bad_paths.append((doc, cited))
    ok &= line("cited src/ paths that do not resolve", len(bad_paths), 0,
               "a renamed project must drag its citations with it")
    for doc, cited in bad_paths:
        print(f"      DANGLING: {doc} -> {cited}")

    bad_runbooks = [rb for rb in sorted(set(re.findall(r"docs/runbooks/[a-z0-9-]+\.md", read("pixi.toml"))))
                    if not os.path.exists(os.path.join(ROOT, rb))]
    ok &= line("pixi.toml runbook references that do not resolve", len(bad_runbooks), 0)
    for rb in bad_runbooks:
        print(f"      DANGLING: pixi.toml -> {rb}")
    return ok


def step12_vendor_pin_wellformed():
    """The one arm of retired STEP 11 whose threat survived #952: the vendor-capabilities.md pin's
    CANONICAL copy. vendor-doc-audit.md and vendor-coverage.md point at vendor-capabilities.md's
    dated history table instead of restating it, so cross-file disagreement is structurally gone --
    but a reshaped/emptied history table would leave those pointers dangling with nothing noticing.
    Well-formedness only, not currency: re-measuring is what refreshes pins.
    """
    rule("STEP 12 -- vendor-capabilities.md's dated history pin is well-formed")
    head = read("docs/vendor-capabilities.md")
    pin = re.search(r"^\| \d{4}-\d{2}-\d{2}.*`(?:agy|claude)`.*\d+\.\d+", head, re.M)
    return line("vendor-capabilities.md carries a dated, versioned history row",
                "yes" if pin else "MISSING", "yes",
                "the canonical pin two other headers point at (#952)")


def _shutil_which(name):
    import shutil
    return shutil.which(name)


def git_state():
    rule("REPO STATE")
    for label, cmd in [("branch", ["git", "rev-parse", "--abbrev-ref", "HEAD"]),
                       ("commits ahead of main", ["git", "rev-list", "--count", "origin/main..HEAD"]),
                       ("uncommitted files", ["git", "status", "--porcelain"])]:
        try:
            out = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=ROOT).stdout.strip()
        except OSError:
            out = "?"
        if label == "uncommitted files":
            out = str(len([x for x in out.splitlines() if x.strip()]))
        line(label, out)


def pr_body_mode() -> int:
    """`--pr-body`: read a PR body on stdin and refuse a negated closing keyword.

    Its own mode rather than a step, because the body is not in the tree -- only CI has it, and only
    while the PR is still editable, which is the one moment the fault is free to fix.

    STDIN is the only input, so a caller who passes a path instead gets a loud refusal rather than
    a pass over the empty stdin that a path argument leaves behind (#860). That silent pass let a
    body with a real fault be reported locally as clean, three times, until CI caught the fault the
    local run had already been asked about.
    """
    stray = [a for a in sys.argv[1:] if a != "--pr-body"]
    if stray:
        print(f"!! --pr-body reads the body on STDIN and takes no argument; got: {' '.join(stray)}")
        print("   Nothing was checked. Pipe the body in instead:")
        print("     gh pr view <n> --json body -q .body | python completeness.py --pr-body")
        return 1

    body = sys.stdin.read()
    if not body.strip():
        # Genuinely empty is a real pass: a body with no text can close nothing. The misuse that
        # LOOKS like this -- a path argument -- is refused above, before stdin is ever read.
        print("OK the body is empty; there is no keyword that could close anything.")
        return 0

    faults = negated_close_faults(body)
    if not faults:
        print("OK every closing keyword is on a declaration line; nothing closes by accident.")
        return 0
    print("!! this PR body will CLOSE issue(s) from a keyword that is not a declaration:")
    for n in dict.fromkeys(faults):          # first-seen order, one line per issue
        print(f"   #{n}")
    print("   GitHub closes on a keyword beside a number regardless of negation, tense, or")
    print("   the text being in a table, a quotation or a code span.")
    print("   Either move the close onto its own line starting `Closes #n` -- which is exempt")
    print("   in full -- or reword so no keyword sits beside the number: `#123 remains open`,")
    print("   `filed separately: #123`, or a `#NNN` placeholder when quoting an example.")
    return 1


def fetch_issue(number: int) -> dict:
    """{'body': ..., 'state': ...} for one issue via `gh`, or raises SystemExit loudly.

    Loud on EVERY failure, unlike STEP 4's offline-skip: this mode guards a merge that is about to
    close the issue, CI always has a token, and a skip here would bless exactly the partial close
    the lint exists to stop. A local caller without `gh` auth gets the same loud refusal — honest
    over convenient.
    """
    import json
    try:
        out = subprocess.run(
            ["gh", "issue", "view", str(number), "--repo", "aer-works/baton", "--json", "body,state"],
            capture_output=True, text=True, encoding="utf-8", cwd=ROOT, timeout=30)
    except (OSError, subprocess.TimeoutExpired) as e:
        raise SystemExit(f"!! could not fetch issue #{number}: {e}. Nothing was checked.")
    if out.returncode != 0:
        raise SystemExit(f"!! `gh issue view {number}` failed: {out.stderr.strip()[:200]}. Nothing was checked.")
    try:
        return json.loads(out.stdout)
    except ValueError:
        raise SystemExit(f"!! could not parse `gh issue view {number}` output. Nothing was checked.")


def pr_closures_mode() -> int:
    """`--pr-closures`: read a PR body on stdin and refuse a declared close whose target issue
    still carries unchecked scope boxes (#975).

    Same stdin-only contract as `--pr-body`, same #860 refusal of a stray argument. Only OPEN
    declared targets are inspected: re-declaring a close against an already-closed issue cannot
    lose work this PR is responsible for, and flagging legacy boxes there would teach authors to
    distrust the lint (the direction that gets one turned off).

    Fixing the flagged ISSUE does not re-trigger CI the way editing the PR body does — the issue
    body is fetched live at run time, so a plain re-run of the failed check picks the fix up.
    """
    stray = [a for a in sys.argv[1:] if a != "--pr-closures"]
    if stray:
        print(f"!! --pr-closures reads the body on STDIN and takes no argument; got: {' '.join(stray)}")
        print("   Nothing was checked. Pipe the body in instead:")
        print("     gh pr view <n> --json body -q .body | python completeness.py --pr-closures")
        return 1

    body = sys.stdin.read()
    targets = declared_closure_targets(body)
    if not targets:
        print("OK the body declares no closes; there is nothing to inspect.")
        return 0

    declared = {}
    for number in targets:
        issue = fetch_issue(number)
        if str(issue.get("state", "")).upper() == "OPEN":
            declared[number] = issue.get("body") or ""

    faults = partial_closure_faults(declared)
    if not faults:
        print(f"OK every declared close ({', '.join('#' + str(n) for n in targets)}) targets an "
              "issue with no unchecked scope boxes.")
        return 0
    print("!! this PR body declares a close on issue(s) whose scope boxes are not all checked —")
    print("   the close would strand that work outside the open backlog (#961/#903 is the incident):")
    for n, boxes in faults.items():
        print(f"   #{n}:")
        for box in boxes[:10]:
            print(f"      {box}")
        if len(boxes) > 10:
            print(f"      ... and {len(boxes) - 10} more")
    print("   Either finish the scope and check its box, re-home an unbuilt scope as its own issue")
    print("   (then check the box with a pointer), split the issue, or drop the closing keyword")
    print("   (`#N remains open — see ...`). The convention this enforces: multi-scope issues carry")
    print("   their scopes as task-list boxes so a partial close is machine-visible. After editing")
    print("   the ISSUE, re-run this check — the issue body is fetched live, so no PR edit is needed.")
    return 1


def main() -> int:
    # A snippet quoted from a UTF-8 doc must survive a cp1252 console: STEP 4 once found its
    # stale citation and then died PRINTING it, reporting the count but never the culprit.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    if "--pr-closures" in sys.argv:
        return pr_closures_mode()
    if "--pr-body" in sys.argv:
        return pr_body_mode()
    print(__doc__.split("USAGE")[0].strip().splitlines()[0])
    results = [step1_sources(), step2_corpus(), step3_gaps(), step4_stale_citations(),
               step8_cited_checks_exist(), step9_pinned_models_exist(), step10_gate_citations(),
               step12_vendor_pin_wellformed(), step13_structural_claims()]
    git_state()
    rule("WHAT THIS SCRIPT CANNOT CHECK")
    for x in [
        "The BUILD PLAN -- every design decision -> a sequenced piece of work -- is not checked",
        "  at all: its completeness is a judgement, not a join. (Step 8 is the CITATION",
        "  check; this list previously mislabelled it as the build plan.)",
        "That the vendor-verify checks still pass -- run `pixi run vendor-verify` for that.",
        "Whether a source nobody thought of exists. Enumeration cannot find its own blind spot.",
        "Step 9 checks the AGY pins and codex's TIER pins. `opus`/`haiku` are claude CLI",
        "  aliases with no vendor catalogue to join against, so nothing HERE validates them --",
        "  smoke-preflight does check claude alias/shape, but only for tests/. The codex arm",
        "  reads WorkerTiers.json only, not the tools/ walk, whose join is agy's list.",
        "Step 9 proves a name is one the CLI LISTS, never that the CLI still lists it --",
        "  both registers are recordings, and re-running `agy models` or the codex probe",
        "  (docs/vendor-codex-probe-2026-09-04.md) is what refreshes them.",
        "Step 4 only catches a citation near a staleness WORD -- a doc that calls a closed issue",
        "  \"resolved\" while still describing the old, wrong behaviour reads clean to this check.",
        "Step 11 checks the three version headers AGREE, never that any is the version",
        "  installed today -- re-measuring against the live CLIs is what refreshes them.",
        "Step 10 only sees citations that use the WORD 'gate'. Referring to a gate by its title,",
        "  its position ('the sixth one'), or by quoting its text goes unnoticed -- and a slug that",
        "  is correct is not thereby CITED CORRECTLY: this checks the shape, never the aptness.",
    ]:
        print(f"    - {x}")
    print()
    # step4 returns None when `gh` is unavailable -- that is "not checked", not "passed", so it
    # is excluded from the pass/fail roll-up rather than counted either way.
    checked = [r for r in results if r is not None]
    return 0 if all(checked) else 1


if __name__ == "__main__":
    sys.exit(main())
