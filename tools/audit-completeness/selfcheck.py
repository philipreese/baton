"""Assert the tooling's own enumerable surfaces, because the checkers had no checker.

Most assertions here map to a defect that actually shipped into a draft of #627 and was caught by a
reviewer or by hand; `_instruments_self_test` is the exception -- it guards the two helpers below
rather than a shipped defect. The surfaces are enumerable -- templates x settings, booleans x flag
directions, a regex x input classes -- which is the criterion CLAUDE.md gate `record-once` names for
when something earns a checker. That criterion had been applied to docs/decisions/ and vendor-verify
and never to the tooling being written.

Runs in CI's `audit` job alongside `completeness.py`. Plain asserts, no test framework: this repo's
python tooling has none, and adding one for a handful of assertions is the ceremony the gates exist
to cut.

    pixi run audit-selfcheck

Each assertion reports the population it examined, and `main()` fails any check that does not --
because "OK" over an empty population is the failure mode this file exists to catch, and a check
that quietly stopped comparing looks exactly like one that compared and agreed. A LINT with nothing
to flag is still a pass; it just has to say what it searched.

WHAT THIS CANNOT CHECK
  * That a check's population is the RIGHT population. It asserts the join holds, never that the
    join is the one worth making.
  * Anything about prose. The defect class that dominated #627 -- a comment asserting what the code
    does not do -- is not reachable from here, and #631 is the other half of that answer.
"""
from __future__ import annotations

import ast
import contextlib
import importlib.util
import inspect
import io
import os
import re
import sys
import tokenize
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CHECKS: list[tuple[str, object]] = []
FAILURES: list[str] = []


def check(name):
    """Register a named assertion. Registration only -- main() runs them, so the header prints first.

    A returned string is the population the assertion examined, printed alongside the OK. An
    assertion that examined nothing says so.
    """
    def deco(fn):
        CHECKS.append((name, fn))
        return fn
    return deco


def load(path: Path, name: str):
    """Import a tool by path without leaving a __pycache__ behind."""
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    prior, sys.dont_write_bytecode = sys.dont_write_bytecode, True
    try:
        spec.loader.exec_module(mod)
    finally:
        sys.dont_write_bytecode = prior
    return mod


# Module-level so `controls.py` can point a check at a MUTATED COPY in a temp tree. A control that
# edited these tracked files in place would leave the repo broken if it were interrupted, and the
# faults being injected are deliberately the kind that make a checker pass -- the worst kind to
# leave behind.
LINT_DIRS = (ROOT / "tools" / "audit-completeness",)

completeness = load(ROOT / "tools" / "audit-completeness" / "completeness.py", "_selfcheck_audit")
verify = load(ROOT / "tools" / "vendor-verify" / "verify.py", "_selfcheck_verify")



def register_models() -> set[str]:
    """The agy catalogue, from completeness.py's own parser rather than a second copy of it.

    A duplicated parse here would drift against the one step 9 actually uses, which is the failure
    the whole file is about.
    """
    accepted, why = completeness.register_models()
    assert accepted is not None, f"the register cannot be parsed, so nothing below can join to it: {why}"
    return accepted


# ---------------------------------------------------------------------------------------------
# Two reusable instruments
# ---------------------------------------------------------------------------------------------

def code_tokens(text: str):
    """The file's code, ignoring its prose: every token except comments, docstrings, and whitespace.

    WHAT IT CANNOT SEE, stated because the obvious reading of the line above is wrong: NEWLINE,
    INDENT and DEDENT all strip to empty and are dropped with the rest of the whitespace, so BLOCK
    STRUCTURE is invisible. `if x:\\n    y = 1\\nz = 2` and `if x:\\n    y = 1\\n    z = 2` produce
    identical token lists. Moving a statement into or out of a conditional, a loop or a `try` is
    exactly the edit someone would want a "prose-only?" instrument to catch, and this one does not.
    The self-test below carries that case as a known-failing polarity rather than leaving it implied.

    Turns "this commit only touches comments" from a characterisation into an assertion. It was
    written after a commit was described as prose-only while it had changed two user-visible string
    literals; running this is what caught that.

    Docstrings are located by POSITION, via ast, not by quote style. A `'''...'''` string used as a
    real value is code and stays; a triple-quoted string in a docstring slot is prose and goes. The
    earlier quote-style test had both backwards, and on Python 3.12+ (PEP 701) also missed
    f-string-quoted prose entirely, since an f-string no longer tokenizes as STRING.

    An instrument for a caller to point at two revisions of a file. It has no standing population, so
    nothing here asserts anything about this repo's own files with it -- only that it works.
    """
    docstrings = set()
    for node in ast.walk(ast.parse(text)):
        if isinstance(node, (ast.Module, ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            body = getattr(node, "body", None)
            if not body:
                continue
            first = body[0]
            if isinstance(first, ast.Expr) and isinstance(first.value, ast.Constant) \
                    and isinstance(first.value.value, str):
                docstrings.add((first.lineno, first.col_offset))

    out = []
    for tok in tokenize.generate_tokens(io.StringIO(text).readline):
        if tok.type in (tokenize.COMMENT, tokenize.NL):
            continue
        if tok.type == tokenize.STRING and tok.start in docstrings:
            continue
        if tok.string.strip():
            out.append((tok.type, tok.string))
    return out


def control_arm(baseline, mutate, restore, describe=""):
    """Run a discriminating control, refusing to report unless the baseline is green FIRST.

    `baseline()` must return True for an unmutated tree. If it does not, the harness is broken and
    the mutated result means nothing -- so this raises instead of reporting a pass. That is the
    failure it exists to prevent: a control run was once reported as three passes while the
    comparison copy was reading an empty string for every file, so every arm failed identically for
    a reason that had nothing to do with the injected fault.

    Returns the mutated-tree result, having proved the baseline was green. Note what that does and
    does not buy: it rules out a baseline that fails for every input, but a `baseline` that ignores
    the mutation entirely still returns True twice and reports a NON-discriminating pass. Only the
    caller's assertion on the returned value catches that, which is why every caller asserts False.
    """
    assert baseline() is True, (
        f"control baseline is NOT green{' for ' + describe if describe else ''}, so nothing measured "
        "after this would mean anything. Two causes, and they need different fixes: either the "
        "harness is broken (the failure this exists to catch), or the tree ALREADY fails the check "
        "being controlled -- in which case fix that first and this arm will speak again. Check the "
        "other assertions: if one of them is also red, it is the second case."
    )
    mutate()
    try:
        return baseline()
    finally:
        restore()


def is_citation(src, m):
    """True if the count sits inside a double-quoted span on its own line.

    A quoted count is reporting what some OTHER text said; an unquoted one is this file making a
    claim. The distinction is not decoration: this check's own comment recording why it exists
    quotes both historical wrong values, and the first version failed on that sentence. A check
    that cries wolf about the note explaining it gets deleted.

    Two stated costs. A genuine transcription written inside double quotes is skipped. And only
    `"` is paired -- prose apostrophes make `'` unpairable -- so a single-quoted citation still
    reads as a claim.

    Triple-quote delimiters are blanked before pairing, and that is load-bearing rather than tidy: a
    ONE-LINE docstring wraps its own contents in `"` characters, so a wrong count inside one paired
    as a quoted span and was skipped as a citation. `controls.py` caught it on its first run, with a
    planted count the lint reported as clean.

    Writing that example out with a real number here made this very file trip the lint -- the third
    fixture in a row to do so. Anything illustrating a count must not BE one.
    """
    line_start = src.rfind("\n", 0, m.start()) + 1
    line_end = src.find("\n", m.end())
    line = src[line_start:line_end if line_end != -1 else len(src)]
    # Blanked, not stripped, so every offset below still lines up with the real line.
    line = re.sub(r'"{3}', "   ", line)
    quotes = [i for i, c in enumerate(line) if c == '"']
    rel = m.start() - line_start
    return any(a < rel < b for a, b in zip(quotes[0::2], quotes[1::2]))


@check("both shapes accept known pins, and PIN_SHAPE rejects English")
def _shapes_discriminate():
    # PIN_SHAPE guards the tools/ walk, where `--model` appears in prose and every following word is
    # a candidate, so it requires a digit. TOKEN_SHAPE guards the register's own fence, where
    # requiring a digit would be INVERTED -- agy serves models from several vendors and a digit-free
    # catalogue entry would be reported as a bad parse.
    english = ("read-only", "fail-closed", "cross-vendor", "skip-permissions")
    for word in english:
        assert not completeness.PIN_SHAPE.fullmatch(word), (
            f"PIN_SHAPE matches {word!r}, an English word. It is everywhere in this repo, and a "
            "match makes the walk report it as an invalid model pin."
        )
    # POSITIVE control, against a literal rather than the register. Asserting either shape over
    # `register_models()` proves nothing: that parser REJECTS any register whose tokens do not all
    # fullmatch TOKEN_SHAPE (completeness.py's `unshaped` arm), so the population arrives
    # pre-filtered and the assertion is satisfied by the filter that produced it. With no positive
    # control, `PIN_SHAPE = re.compile(r"(?!)")` -- a regex matching NOTHING -- left every assertion
    # in this file green while step 9's tools/ walk silently stopped finding any pin at all.
    known_pins = ("gemini-3.1-pro-high", "gemini-3.6-flash-low", "claude-sonnet-4-6",
                  "gpt-oss-120b-medium")
    for pin in known_pins:
        assert completeness.PIN_SHAPE.fullmatch(pin), (
            f"PIN_SHAPE rejects {pin!r}, a real agy model name. The tools/ walk gates on this, so it "
            "would stop finding pins entirely and step 9 would pass by looking at nothing."
        )
        assert completeness.TOKEN_SHAPE.fullmatch(pin), (
            f"TOKEN_SHAPE rejects {pin!r} -- the register parse would call a correct parse a bad one"
        )
    models = register_models()
    # The register is still read, for the ONE thing it can honestly say: how big PIN_SHAPE's stated
    # blind spot currently is. Its digit requirement is a deliberate cost, not a defect, so a
    # digit-free catalogue entry is measured and reported rather than failed.
    blind = sorted(m for m in models if not completeness.PIN_SHAPE.fullmatch(m))
    note = (f"{len(english)} English words rejected, {len(known_pins)} known pins accepted, "
            f"{len(models)} catalogue entries parsed")
    return note + (f"; PIN_SHAPE is blind to {blind} (digit-free, invisible to the tools/ walk)"
                   if blind else "; no catalogue entry is digit-free, so the walk's blind spot is empty")


@check("step 9's probe-input exemption excuses a marked line and nothing else")
def _probe_input_exemption():
    """An exemption asserted in ONE direction is a switch for turning the step off.

    Step 9 fails an agy model name the catalogue does not list. `effort.agy-rejection-is-per-model`
    has to pass a name the catalogue cannot list -- that is the measurement -- so the marker exists.
    What makes it safe rather than a hole is that the unmarked case still fails, which is the arm
    below that would go quiet if the exemption ever widened.
    """
    # The fixture model name is DIGIT-FREE on purpose. A realistic one here would be an unmarked
    # uncatalogued name sitting in `--model` position in a tracked file, so step 9 would flag this
    # very fixture -- and the unmarked arm cannot carry the marker without ceasing to be the unmarked
    # arm. `PIN_SHAPE` requires a digit, and no catalogue entry is digit-free, so a digit-free name is
    # outside the walk's population by construction rather than by an exemption.
    marked = '    run(["agy", "--model", "aer-fixture-model"])  # ' + completeness.UNCATALOGUED_ON_PURPOSE
    unmarked = '    run(["agy", "--model", "aer-fixture-model"])'
    assert completeness.is_probe_input(marked), (
        "step 9: a line carrying the marker was not exempted, so a deliberate probe input fails CI")
    assert not completeness.is_probe_input(unmarked), (
        "step 9: an UNMARKED uncatalogued name was exempted -- the exemption has stopped being an "
        "exemption and step 9 no longer guards a stale pin")
    # A marker anywhere on the line counts, deliberately -- it may sit in a trailing comment or in a
    # docstring line quoting an error message. What must not count is a line without it.
    assert completeness.is_probe_input(f"# {completeness.UNCATALOGUED_ON_PURPOSE} explains why"), (
        "step 9: the marker stopped being recognised in a leading comment")
    return "3 arms: marked exempt, unmarked still fails, marker position free"


@check("a PR body closes only the issues it declares, whatever the grammar around a keyword")
def _negated_close_lint():
    """Both must-fire fixtures are REAL BODIES, verbatim from the merges that auto-closed an issue.

    `NEGATED_CLOSE` in completeness.py carries which merges those were and what each cost. What
    matters here is that a CLAUDE.md note sat between the two incidents and did not prevent the
    second, so the fixtures are the incidents rather than invented shapes.

    The must-NOT-fire half carries as much weight. A deliberate `Closes #n` is the convention this
    repo runs on, and a lint that flagged it would be turned off within a week.
    """
    must_fire = [
        ("#692's body, verbatim", "**Does not close #532 or #550** - it is the measurement"),
        ("#684's body, verbatim", "filed, not fixed: #688"),
        # #694's, and the reason this lint keys on POSITION rather than on negation: past tense,
        # about a different PR, inside a table cell -- it passed the negation-only version while
        # closing #532 for the second time, in the PR that added the lint.
        ("#694's body, verbatim", "| #692 | `Does not close` | closed #532 |"),
        ("contraction", "The root cause isn't fixed: #99."),
        ("uppercase", "This does NOT resolve #123."),
        ("never", "Found but never closed #77"),
        ("descriptive, no negation", "The crash was fixed #690 in an earlier commit."),
        ("second one on a non-declaration line", "It changes nothing. Closes #12."),
    ]
    must_not_fire = [
        ("the convention itself", "Closes #675. Closes #676."),
        ("the safe rewording", "#532 remains open - see the comment thread."),
        ("the other safe rewording", "filed separately: #691"),
        ("a bare reference", "Related: #504, 0023, #479."),
        # A declaration line is exempt IN FULL, second occurrence included -- `Closes #675. Closes
        # #676.` is one deliberate act, and flagging its tail would refuse the repo's own convention.
        # The mirror case, a `Closes #n` buried mid-line, is in must_fire: under the position rule it
        # is flagged, and correctly, since a close that is meant belongs on a line of its own.
        ("bold declaration", "**Closes #12.** The rest of the body follows."),
        # The PR-template shape #975's reviewer found matched neither register: a bulleted close is
        # deliberate, GitHub closes on it, and flagging it here would fight the commonest template.
        ("bulleted declaration", "This PR:\n- Closes #12\n- Adds feature X"),
        ("starred bulleted declaration", "* Closes #13"),
        ("numbered declaration", "1. Fixes #14"),
        ("no keyword at all", "Not the same as #345, which is a different concern."),
        # GitHub links a keyword only when it sits immediately before the reference, so this closes
        # nothing and the lint must agree. Firing here would teach authors to reword around a
        # phantom, and a lint nobody believes is worse than none.
        ("'by' - GitHub ignores it", "The root cause is not fixed by #99 either."),
    ]
    for label, body in must_fire:
        assert completeness.negated_close_faults(body), (
            f"negated-close lint: [{label}] was accepted -- GitHub would close the issue: {body!r}")
    for label, body in must_not_fire:
        assert not completeness.negated_close_faults(body), (
            f"negated-close lint: [{label}] was refused, and GitHub closes nothing here: {body!r}")

    # The numbers themselves, not merely that something fired -- a lint reporting the wrong issue
    # sends the author to edit a line that is not the problem.
    assert completeness.negated_close_faults("Does not close #532 or #550") == [532], (
        "negated-close lint: reported the wrong issue number, or more than the keyword binds to")

    return (f"{len(must_fire)} must fire ({sum(1 for l, _ in must_fire if 'verbatim' in l)} real "
            f"incident bodies) + {len(must_not_fire)} must NOT fire")


@check("a declared close is refused while its target issue still carries unchecked scope boxes")
def _partial_closure_lint():
    """#975's two pure halves. The incident is #961 closing #903 with three of four scopes unbuilt;
    the strict-targeting arms pin the operator's constraint — declarations only — whose rationale
    lives on `declared_closure_targets` itself.

    The honest-scope arm is deliberate: #903's own scopes were prose headings, which this lint
    cannot see — `completeness.py`'s register comment above UNCHECKED_BOX records the companion
    convention (boxes, not headings) that makes partial closure machine-visible at all.
    """
    targets = completeness.declared_closure_targets
    assert targets("Closes #971\n\nProse about #903 and its boxes.") == [971], (
        "partial-closure lint: a bare reference was treated as a declared close, or the "
        "declaration itself was missed — strict targeting is broken in one direction or the other")
    assert targets("Closes #675. Closes #676.") == [675, 676], (
        "partial-closure lint: a two-close declaration line no longer yields both targets")
    assert targets("This PR:\n- Closes #12\n- Adds feature X") == [12], (
        "partial-closure lint: the bulleted PR-template declaration is invisible again — the exact "
        "false pass the #975 review found, where the closed issue is never inspected at all")
    assert targets("It changes nothing. Closes #12.") == [], (
        "partial-closure lint: a mid-line keyword became a declared target — that is the negated-"
        "close lint's fault to flag, and inspecting it here would widen targeting past declarations")

    boxes = completeness.unchecked_scope_lines
    open_box = "## Scopes\n- [ ] build the thing\n- [x] already done\n* [ ] starred too\n+ [ ] plussed too\n1. [ ] numbered too"
    assert boxes(open_box) == ["- [ ] build the thing", "* [ ] starred too", "+ [ ] plussed too", "1. [ ] numbered too"], (
        "partial-closure lint: unchecked boxes were missed or a checked box was counted")
    fenced = "```\n- [ ] just an example\n```\n~~~\n- [ ] tilde-fenced example\n~~~\n- [ ] real"
    assert boxes(fenced) == ["- [ ] real"], (
        "partial-closure lint: a box quoted inside a fence was counted, or the one after it missed")
    nested = "````\n```\n- [ ] quoted example\n```\nstill fenced\n````\n- [ ] real"
    assert boxes(nested) == ["- [ ] real"], (
        "partial-closure lint: a shorter same-char run closed a longer fence early, un-hiding a "
        "quoted example box")
    sticky = "```python\ncode\n```js\nstill the same block per CommonMark\n```\n- [ ] real box after"
    assert boxes(sticky) == ["- [ ] real box after"], (
        "partial-closure lint: an info-string line was taken as a closer, desyncing the tracker so "
        "the real box after the block was silently swallowed — the sticky false pass from the #975 "
        "review")
    assert boxes("## Scope one\nprose only\n## Scope two\nmore prose") == [], (
        "partial-closure lint: fired on prose headings — the documented limitation stopped holding, "
        "which means the convention comment is now wrong, not that this got better")

    faults = completeness.partial_closure_faults(
        {971: "- [x] all built\n- [x] shipped", 903: "- [ ] compaction\n- [ ] archival"})
    assert list(faults) == [903] and len(faults[903]) == 2, (
        "partial-closure lint: the join reported the wrong issue, missed one, or flagged a "
        "fully-checked target")
    return "4 targeting arms + 5 box arms + 1 join arm"


@check("a wrong repo name fails STEP 4 loudly; every other gh failure still skips")
def _step4_names_a_missing_repo():
    """The rollup excludes skips, so "the repo does not exist" and "we are offline" must not both
    be skips -- `completeness.repo_is_unreachable` records what that cost. The must-fire half is
    the wording `gh` actually returns; the must-NOT-fire half is every ordinary reason a developer
    without network or auth should still get a green local run.
    """
    must_fire = [
        ("gh GraphQL, verbatim shape", "GraphQL: Could not resolve to a Repository with the name 'aer-works/baton'."),
        ("REST wording", "gh: Not Found (HTTP 404)"),
        ("case-insensitive", "COULD NOT RESOLVE TO A REPOSITORY"),
    ]
    must_not_fire = [
        ("offline", "error connecting to api.github.com: dial tcp: lookup api.github.com: no such host"),
        ("unauthenticated", "gh: To use GitHub CLI in a GitHub Actions workflow, set the GH_TOKEN environment variable"),
        ("rate limited", "API rate limit exceeded for user ID 12345."),
        # The empty string is what a crashed-without-stderr call leaves. It must skip, not fail:
        # asserting a wrong NAME from no evidence at all is the false-confidence direction.
        ("no stderr at all", ""),
    ]
    for label, text in must_fire:
        assert completeness.repo_is_unreachable(text), (
            f"step 4: [{label}] would have SKIPPED, so a wrong repo name checks nothing while the "
            f"rollup stays green: {text!r}")
    for label, text in must_not_fire:
        assert not completeness.repo_is_unreachable(text), (
            f"step 4: [{label}] would FAIL the gate, so a developer offline or unauthenticated "
            f"cannot get a green local run: {text!r}")
    return f"{len(must_fire)} must fire + {len(must_not_fire)} must NOT fire"


@check("--pr-body refuses a path argument instead of passing over the empty stdin it leaves")
def _pr_body_reads_stdin_only():
    """#860, whose cost `completeness.pr_body_mode`'s own docstring records. The arms here are the
    two states that must stay distinguishable: a stray argument is a usage fault (loud), a piped
    empty body is a real pass (an empty body can close nothing).
    """
    def run(argv: list[str], stdin_text: str) -> tuple[int, str]:
        saved_argv, saved_stdin, saved_stdout = sys.argv, sys.stdin, sys.stdout
        sys.argv, sys.stdin, sys.stdout = argv, io.StringIO(stdin_text), io.StringIO()
        try:
            code = completeness.pr_body_mode()
            return code, sys.stdout.getvalue()
        finally:
            sys.argv, sys.stdin, sys.stdout = saved_argv, saved_stdin, saved_stdout

    faulty = "filed, not fixed: #688\n"

    code, out = run(["completeness.py", "--pr-body", "some/body.md"], "")
    assert code == 1, "a path argument was accepted, so nothing was checked and it still passed"
    assert "STDIN" in out, f"the refusal must say where the body goes; got {out!r}"

    # The control that makes the arm above mean something: the SAME faulty body that a path
    # argument would have hidden is caught the moment it actually arrives on stdin.
    code, _ = run(["completeness.py", "--pr-body"], faulty)
    assert code == 1, "a real fault on stdin stopped being caught"

    code, out = run(["completeness.py", "--pr-body"], "   \n")
    assert code == 0, "a genuinely empty piped body must pass -- it can close nothing"
    assert "empty" in out, f"an empty body's pass must say so, not read as a checked pass; got {out!r}"

    return "3 arms: path argument refused, real fault on stdin still caught, empty body passes loudly"


@check("the gate-citation lint separates a slug from an ordinal")
def _gate_lint_discriminates():
    # Step 10's population is the whole repo, so it can only ever report "0 faults" -- which is what
    # a lint pointed at nothing also reports. `gate_citation_faults` is pure for exactly this
    # reason: drive it with planted input and both directions become checkable.
    slugs = completeness.gate_slugs(completeness.read("CLAUDE.md"))
    assert slugs, "CLAUDE.md defines no gate slugs -- the lint has no expected set to judge against"

    # ASSEMBLED, NOT SPELLED OUT -- the fifth fixture in this pair of files to need it. Every checker
    # here scans the directory it lives in, so a fault written as a literal IS a fault, in a real
    # file, and the checker reports itself. Step 10 did exactly that on these two lines. The rule:
    # a fixture for a checker must not be readable BY that checker.
    ordinal = "run this before shipping -- CLAUDE.md gate " + "8."
    absent_slug = "see gate " + "`record-twice` for the rule."

    # MUST be caught. The first is what `pixi.toml` actually carried; the second is what renaming a
    # gate would leave behind everywhere.
    caught = {"an ordinal": ordinal, "a slug that does not exist": absent_slug}
    for label, text in caught.items():
        faults = completeness.gate_citation_faults({"planted.md": text}, slugs)
        assert faults, f"the lint does not flag {label}: {text!r}"

    # MUST NOT be caught. A lint that fires on correct prose gets deleted, and each of these was a
    # real false positive or a near-miss: `DependsOn` is a validity gate in milestone-history.md and
    # not a shipping gate at all, and it only matched because a blanket re.I made `[a-z]` match
    # capitals too.
    ignored = {
        "a correct slug citation": f"see gate `{sorted(slugs)[0]}` for the rule.",
        "an unrelated capitalised gate name": "the validity gate `DependsOn` walks ancestors.",
        "a hyphenated ordinal": "the gate-7 branch is unrelated.",
        "the bare word": "this Gate is a different thing entirely.",
    }
    for label, text in ignored.items():
        faults = completeness.gate_citation_faults({"planted.md": text}, slugs)
        assert not faults, f"the lint fires on {label}: {text!r} -> {faults}"

    # #1365/#1367: the name-level predicate that keeps generated changelogs out of the
    # living-document lints (step 10 here, and record-once's changed-file population).
    # Both arms: the release-please artifact is skipped; a near-name living doc is not.
    assert completeness.generated_changelog("CHANGELOG.md"), \
        "generated changelogs must be skipped by name -- release PR #309 was unmergeable without this"
    for near in ("CHANGES.md", "changelog-notes.md", "CHANGELOG.py"):
        assert not completeness.generated_changelog(near), \
            f"the skip must be exact -- {near!r} is a living document and stays policed"

    return (f"{len(slugs)} slugs; {len(caught)} fault shapes caught, "
            f"{len(ignored)} correct shapes ignored; changelog name-skip discriminates")


@check("step 9 fails CLOSED when either of its two file sources goes unreadable")
def _step9_fails_closed():
    # TWO of step 9's four sources, and the title says two rather than implying all four. The
    # uncontrolled pair: WorkerTiers.json's own absence (a hard failure whose arm would need the file
    # read, not `completeness.read`, to fail) and the tools/ regex walk (whose population is the whole
    # tree) -- both read via a raw `open()`/`os.walk` rather than `completeness.read`, which is what
    # the `sources` monkeypatch below can reach.
    #
    # Monkeypatched rather than mutating the tree: a test that renames files leaves the repo broken
    # if it is interrupted, and completeness.py derives ROOT from __file__ so it cannot simply be
    # relocated -- that is what silently broke a control run into reading empty strings.
    # step 9 prints its full report on every call; the assertions read its return value, so the
    # output is noise here.
    def baseline():
        with contextlib.redirect_stdout(io.StringIO()):
            return completeness.step9_pinned_models_exist() is True

    real_read = completeness.read
    sources = {
        "docs/vendor-capabilities.md (the register)": "vendor-capabilities",
        "tools/vendor-verify/verify.py (the CHEAP pin)": "verify.py",
    }
    for label, needle in sources.items():
        result = control_arm(
            baseline,
            lambda needle=needle: setattr(completeness, "read",
                                          lambda p: "" if needle in p else real_read(p)),
            lambda: setattr(completeness, "read", real_read),
            describe=f"step9 with {label} unreadable")
        assert result is False, (
            f"step 9 passed with {label} unreadable -- a population that silently shrinks is how a "
            "check keeps printing OK about less and less"
        )
    return f"{len(sources)} of step 9's 4 sources controlled"


@check("no tooling file transcribes a count its own code computes")
def _no_transcribed_counts():
    # `record-once`: never transcribe a value that lives somewhere authoritative. Both patterns were
    # real -- a docstring said "eight steps" while main() ran nine, and a comment said "(today: 12)"
    # against the count `register_models()` computes.
    #
    # The population is every python file in this pair of tools, INCLUDING this one, which is where
    # the live instances were: this file's own docstring said "six assertions" while more were
    # registered. Those quoted counts are what `is_citation` is exercised on -- three of them, across
    # two comments, and if it misclassified any one the assert below would fire.
    #
    # SCOPE, because the report says "nothing transcribes a count" and that is a claim about these
    # two patterns, not about the tree: only `<n> steps`, `<n> assertions` and `today: <n>` are
    # searched. Prose that transcribes a computed value in any other shape is invisible here. Two
    # such were found by a reviewer and fixed by CITING the expression instead -- which is the actual
    # remedy, since no pattern list will ever cover English.
    files = sorted(f for d in LINT_DIRS for f in d.glob("*.py"))
    assert files, "no tooling files found -- the population is empty"
    steps = len(re.findall(r"^def step\d", completeness.read("tools/audit-completeness/completeness.py"), re.M))
    # `completeness.read` returns "" for a missing path, so a rename or a typo makes this 0 in
    # silence -- and the first real transcription it ever caught would be reported as "claims 9
    # steps; 0 are defined". The same "population that silently shrinks" this file checks for
    # elsewhere.
    assert steps, ("no `def stepN` functions found in completeness.py -- the value this lint "
                   "compares against is not being computed, so it cannot judge any claim")
    fence_count = len(register_models())
    words = {"one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7,
             "eight": 8, "nine": 9, "ten": 10, "eleven": 11, "twelve": 12}

    def claimed(tok):
        tok = tok.lower()
        return words.get(tok, int(tok) if tok.isdigit() else None)

    found = cited = 0
    for path in files:
        src = path.read_text(encoding="utf-8")
        for m in re.finditer(r"\b([a-z]+|\d+)\s+(steps|assertions)\b", src, re.I):
            n = claimed(m.group(1))
            if n is None:
                continue
            if is_citation(src, m):
                cited += 1
                continue
            found += 1
            expected = steps if m.group(2).lower() == "steps" else len(CHECKS)
            assert n == expected, (
                f"{path.name} claims {n} {m.group(2).lower()}; {expected} are defined. Cite the code "
                f"or drop the number -- this exact sentence stood at 'eight' while main() ran nine."
            )
        for m in re.finditer(r"today:\s*(\d+)", src):
            if is_citation(src, m):
                cited += 1
                continue
            found += 1
            assert int(m.group(1)) == fence_count, (
                f"{path.name} says 'today: {m.group(1)}' where the register's fence holds {fence_count}"
            )
    # Zero transcribed counts is the DESIRED state -- this is a lint, and a lint with no violations
    # is healthy. So an empty population is not a failure, but it is REPORTED as empty: the version
    # of this check that scanned one file found nothing after that file was rewritten, and went on
    # printing OK about a comparison it was no longer making.
    scanned = f"{len(files)} files scanned"
    skipped = f", {cited} quoted citation(s) skipped" if cited else ""
    return (f"{scanned}, {found} transcribed count(s) verified{skipped}" if found
            else f"{scanned}{skipped}; NOTHING transcribes a count, so nothing was compared")


@check("the two reusable instruments work on themselves")
def _instruments_self_test():
    # A table rather than a run of asserts, so the count in the population line is COUNTED. Written
    # as `4 code_tokens polarities` it was already wrong one edit later, in the file that lints for
    # exactly that -- and the lint's patterns do not cover the word "polarities", so it stayed green.
    #
    # `same=True` means the two inputs must be indistinguishable to the instrument (prose changed);
    # `same=False` means it must tell them apart (code changed). Both directions, because a
    # `code_tokens` that returned [] for everything would satisfy only the first kind.
    polarities = [
        ("comment text is invisible", "x = 1  # comment\n", "x = 1  # different comment\n", True),
        ("docstring text is invisible", '"""doc a."""\nx = 1\n', '"""doc b."""\nx = 1\n', True),
        ("a real code change is visible", "x = 1\n", "x = 2\n", False),
        # The defect it was written for: a string literal a USER sees is code, not prose, however it
        # is quoted. Triple-quoted and not in a docstring slot, so quote style cannot classify it.
        ("a triple-quoted VALUE is code", 'x = """v1"""\n', 'x = """v2"""\n', False),
        # The KNOWN blind spot, pinned in the direction it actually behaves so the docstring's stated
        # limitation is checked rather than merely claimed. If a future edit starts keeping
        # NEWLINE/INDENT/DEDENT this fails, and the docstring has to be corrected with it.
        ("block structure is NOT visible (known gap)",
         "if x:\n    y = 1\nz = 2\n", "if x:\n    y = 1\n    z = 2\n", True),
    ]
    for label, a, b, same in polarities:
        if same:
            assert code_tokens(a) == code_tokens(b), f"code_tokens: {label} -- it distinguished them"
        else:
            assert code_tokens(a) != code_tokens(b), f"code_tokens: {label} -- it missed the change"

    try:
        control_arm(lambda: False, lambda: None, lambda: None, describe="deliberately red baseline")
    except AssertionError:
        pass
    else:
        raise AssertionError("control_arm reported a result on a RED baseline -- its whole purpose")
    return (f"{len(polarities)} code_tokens polarities "
            f"({sum(1 for p in polarities if not p[3])} must discriminate) "
            "+ control_arm's red baseline")


@check("the record-once checker fires on restated prose, not on text the register prescribes")
def _recordonce_discriminates():
    """A table, not a run of asserts, so the population line is counted rather than transcribed.

    `fires=True` means the input must be reported; `fires=False` means it must not. Both directions,
    because a checker that reported everything would satisfy only the first kind -- and three
    designs of this one have now been wrong in the second, each time on text whose duplication
    `record-once` itself prescribes.
    """
    rec = load(ROOT / "tools" / "audit-completeness" / "recordonce.py", "_selfcheck_recordonce")

    # recordonce takes added lines split BY HUNK, so every fixture says which lines are contiguous.
    # Written out rather than defaulted, because contiguity is now load-bearing: a fixture that
    # silently rejoined two hunks would exercise the fabricated-shingle path the split exists to
    # close, and would do it invisibly.
    def one(*lines: str) -> list[list[str]]:
        """One hunk: every line contiguous with the next."""
        return [list(lines)]

    sentence = "the vendor refuses the call before any hook is ever consulted here"
    restated = {"src/A.cs": one(f"// {sentence}"), "docs/B.md": one(sentence)}

    # Written out as a literal, which #676 is what makes possible: the marker is now read out of a
    # file's COMMENT PROSE, so these characters sitting in a Python string are code and exempt
    # nothing. Before that, any tracked file containing them anywhere exempted itself, and this line
    # had to be assembled from fragments to avoid disabling the checker it is a fixture for.
    # The canonical path has to be a file that EXISTS -- a marker naming one that does not is
    # refused, which the last arm below asserts. So the fixture names a real one.
    marker = "// record-once-ok: #901 canonical is CLAUDE.md"

    # Genuinely different sentences, as real files citing one issue have -- a fixture that repeated
    # one sentence ten times would be restatement, and the checker would be right to say so.
    distinct = [
        "// Pre-approved either way, because the hook is what confines the target path.",
        "// Refused at bind time rather than discovered after the run is paid for.",
        "// The exemption covers write-family tools only; a read carries a path too.",
        "// Fails closed on every payload it cannot judge, including its own defects.",
        "// Resolves every component so a planted link cannot launder the target.",
        "// Adapter-agnostic, so narrowing this would refuse on one vendor at bind time.",
        "// Measured live across four consecutive dispatches with distinct task directories.",
        "// The vendor ignores the process working directory, which is why cwd is not it.",
        "// Only the deny list enforces; the allow list merely stops the prompt appearing.",
        "// Kept as its own condition so the operator learns which mistake they made.",
    ]
    title = "A reviewer's verdict is evidence for a human decision, never the decision itself"
    banner = ["// GENERATED FILE - DO NOT EDIT.", "// Regenerate: pixi run tokens",
              "// Hand edits are reverted by the next regeneration and fail CI in the meantime."]
    fenced = ["Run it like this:", "```bash", "pixi run audit-recordonce -- origin/main", "```"]

    polarities = [
        ("one sentence written into two files", restated, True),
        # A restatement that reaches a code file only through its comments still has to be found:
        # the measured case spread one corrected fact across `///` comments and markdown alike.
        ("prose restated across a comment and a doc",
         {"src/C.cs": one(f"/// {sentence}"), "docs/D.md": one(f"- {sentence}")}, True),
        # Guards contiguity from the other side: a break rule that split per line would satisfy every
        # arm below while losing the only shape the checker was built for. See `groups` in
        # recordonce.py for why a run spans consecutive comment lines.
        ("a sentence wrapped across two comment lines",
         {"src/W.cs": one("/// the vendor refuses the call before any hook",
                          "/// is ever consulted here at all"),
          "docs/W.md": one("the vendor refuses the call before any hook is ever consulted here "
                           "at all")}, True),
        # Was a FALSE POSITIVE until hunks were split apart, and measured as one: neither hunk holds
        # nine words, so the only shingle either file can produce is the join -- a word sequence
        # present in no line of either. Two files "sharing" it shared nothing. The same fabrication
        # also reached the `e.g. "..."` sample printed under real findings.
        ("two files sharing only a cross-hunk join",
         {p: [["/// the gate refuses a payload"], ["/// it cannot judge at all"]]
          for p in ("src/H1.cs", "src/H2.cs")}, False),
        # #675's coverage half. None of these carried a leader on the line holding the words, so all
        # three were invisible to the leader regex -- and a Python docstring is not an exotic case
        # here: repinning PROVEN_GROUPS on this change was caused by exactly one, in dispatch.py.
        ("a block-comment body with no leader on its lines",
         {"src/BC.cs": one("/* the vendor refuses the call before any hook",
                           "   is ever consulted here at all */"),
          "docs/BC.md": one(f"{sentence} at all")}, True),
        ("a python docstring restated into a doc",
         {"tools/x.py": one("def f():", f'    """{sentence}."""'),
          "docs/PY.md": one(sentence)}, True),
        ("an xml comment restated into a doc",
         {"src/X.csproj": one(f"<!-- {sentence} -->"), "docs/XM.md": one(sentence)}, True),
        # The other direction of the same change: context means code positions stop being read, and
        # a `#if` is not a `#` comment. Under the old leader regex this fired.
        ("a C# preprocessor directive shared by two files",
         {p: one("#if WINDOWS", "#region the vendor refuses the call before any hook is here",
                 "#endif") for p in ("src/P1.cs", "src/P2.cs")}, False),
        # An unanchored opener would find `//` inside every one of these and read the URL as prose.
        ("the same long url in code in two files",
         {p: one('var u = "https://example.com/a/b/c/d/e/f/g/h/i/j";')
          for p in ("src/U1.cs", "src/U2.cs")}, False),
        # Was a false positive under the reference-counting design: one issue cited in many files
        # is the register working, and ten different sentences share no wording.
        ("one issue cited in ten files",
         {f"src/F{i}.cs": one(f"{line} See #901.") for i, line in enumerate(distinct)}, False),
        # Was a false positive under the first shingling design: duplicated test setup is ordinary.
        ("duplicated test setup code",
         {f"tests/T{i}.cs": one("var grant = new PermissionGrant(ReadFiles: true, WriteFiles: false);",
                                "using var stderr = new StringWriter();") for i in range(3)}, False),
        # The three shapes the first draft failed CI on, for the reason recorded beside `TABLE_ROW`
        # in recordonce.py. The first of them fired on every new decision record.
        ("a decision record, its index row and its plan row",
         {"docs/decisions/0042-x.md": one(f"# 0042 - {title}"),
          "docs/decisions/README.md": one(f"| [0042](0042-x.md) | {title} | M26 |"),
          "docs/plan.md": one(f"| 0042 | {title} | done |")}, False),
        # Two decision records with DIFFERENT bodies that share only a "Relates to" footer listing the
        # same prior decisions collide on the slug words baked into the `](nnnn-slug.md)` link targets,
        # never on a shared sentence -- the exact 0046/0047 false positive `LINK_TARGET` strips. This is
        # the real shape and the reason the bodies differ here: pre-fix the footer's link run alone forms
        # a 9-gram across both files; post-fix the targets are gone and the surviving `relates to 0013
        # and 0003` is far too short to shingle, so it is the shared LINK that stops colliding, not a
        # whole line collapsing below the floor.
        ("two decisions sharing only a Relates-to link footer",
         {"docs/decisions/0090-p.md": one(
             "A place is a container and a workflow is the work that runs under it.",
             "Relates to [0013](0013-room-is-the-user-facing-noun.md) and "
             "[0003](0003-templates-collapse-to-three-shapes.md)."),
          "docs/decisions/0091-q.md": one(
             "Templates are data resolved the way the role catalog already is.",
             "Relates to [0013](0013-room-is-the-user-facing-noun.md) and "
             "[0003](0003-templates-collapse-to-three-shapes.md).")},
         False),
        ("a regenerated banner in two generated files",
         {"src/Baton.Ui.Core/Generated.cs": [banner], "src/Baton.Ui/Theme/Tokens.axaml.cs": [banner]}, False),
        ("the same command block fenced in two runbooks",
         {"docs/runbooks/a.md": [fenced], "docs/runbooks/b.md": [fenced]}, False),
        # A file with no extension is still a file with comments, and the per-language table read it
        # as nothing while every arm above stayed green. `NO_EXTENSION` in recordonce.py carries the
        # measurement; what this arm adds is that a narrowing cannot ship silently again.
        ("prose in an extensionless file restated into a doc",
         {".githooks/pre-push": one(f"# {sentence}"), "docs/EX.md": one(sentence)}, True),
    ]
    for label, by_file, fires in polarities:
        found = rec.violations(by_file)
        if fires:
            assert found, f"record-once: {label} was accepted -- the shape this exists for"
        else:
            assert not found, f"record-once: {label} was rejected -- {found}"

    # -- #676, the exemption. Its own table, because every arm needs a marker source: markers are
    # read from whole files now, not from the diff, so these say what each file CONTAINS as well as
    # what the change added.
    marked = {"src/A.cs": [f"// {sentence}", marker], "docs/B.md": [sentence]}
    added = {"src/A.cs": one(f"// {sentence}"), "docs/B.md": one(sentence)}
    at = lambda path: marked.get(path)  # noqa: E731

    # The marker is in neither file's ADDED lines. Under the old added-lines match this was flagged
    # again the moment someone reworded a copy without re-touching the marker -- the "too weak over
    # time" half of #676. The exemption is a decision about the passage, so it has to outlive the
    # commit that made it.
    assert not rec.violations(added, at), (
        "record-once: an exemption granted by an earlier change no longer holds")

    # And it must be reported, or a silenced run reads exactly like a clean one.
    notes = rec.groups(added, at)[1]
    assert notes and "#901" in notes[0] and "CLAUDE.md" in notes[0], (
        f"record-once: the exemption was not reported with its issue and canonical path -- {notes}")

    # PASSAGE-level, not file-level: a second, unmarked restatement in the SAME file is still found.
    # Under a file-granular hatch one marker in a file stopped everything else in it being compared.
    other = "a withheld write reaching the outbox is the only exemption that exists"
    marked_two = {"src/A.cs": [f"// {sentence}", marker, "", f"// {other}"],
                  "docs/B.md": [sentence], "docs/C.md": [other]}
    assert rec.violations(
        {"src/A.cs": one(f"// {sentence}", marker, "", f"// {other}"),
         "docs/B.md": one(sentence), "docs/C.md": one(other)},
        lambda path: marked_two.get(path)), (
        "record-once: a marker exempted a passage it does not sit beside")

    # The context test. The same characters in a code position -- a Python string literal, which is
    # exactly how this file writes `marker` above -- must exempt nothing.
    literal = {"tools/x.py": [f'marker = "{marker}"', f"# {sentence}"], "docs/B.md": [sentence]}
    assert rec.violations({"tools/x.py": one(f'marker = "{marker}"', f"# {sentence}"),
                           "docs/B.md": one(sentence)}, lambda path: literal.get(path)), (
        "record-once: a marker written as a code literal silenced the checker")

    # Prose ABOUT the marker is not a marker. The false positive the anchored SUPPRESS was added
    # for, and it was live rather than theoretical -- `SUPPRESS` in recordonce.py records which
    # docstring it was and what it exempted. The second assertion is the load-bearing one: a mention
    # must be inert, not merely un-honoured, or every document explaining the syntax fails the gate.
    mention = f"// see {marker[3:]} for the format"
    describes = {"src/A.cs": [f"// {sentence}", mention], "docs/B.md": [sentence]}
    at_mention = lambda path: describes.get(path)  # noqa: E731
    assert rec.violations(added, at_mention), (
        "record-once: a marker named inside a sentence exempted a passage")
    assert not rec.groups(added, at_mention)[2], (
        "record-once: prose describing the marker was reported as a broken one")

    # A marker whose canonical location does not exist, and one that does not parse at all, each
    # exempt nothing AND fail the run. Both are unambiguous typos, and both previously landed as a
    # printed note saying the passage had been exempted while it was being compared.
    typo = marker.replace("CLAUDE.md", "docs/no-such-file.md")
    absent = {"src/A.cs": [f"// {sentence}", typo], "docs/B.md": [sentence]}
    at_typo = lambda path: absent.get(path)  # noqa: E731
    assert rec.violations(added, at_typo), (
        "record-once: a marker naming a file that does not exist still exempted the passage")
    assert any("does not exist" in b for b in rec.groups(added, at_typo)[2]), (
        "record-once: a refused marker was reported as though it had been honoured")

    # One file, no shared wording: the ONLY thing wrong is the marker, so a green run here would be
    # the silent no-op itself rather than any restatement finding masking it.
    broken = {"src/A.cs": ["// record-once-ok: #901", f"// {sentence}"]}
    solo = rec.violations({"src/A.cs": one("// record-once-ok: #901", f"// {sentence}")},
                          lambda path: broken.get(path))
    assert len(solo) == 1 and "does not parse" in solo[0], (
        f"record-once: a marker with no canonical path failed silently -- {solo}")

    # -- #691, the markdown half of the hatch. Markdown is this gate's dominant population and an
    # HTML comment is the only comment form it has; before this, the comment form exempted nothing
    # AND reported nothing, which is the same silent no-op class as `broken` above, scoped to
    # exactly the files most likely to need a marker.
    md_marker = "<!-- record-once-ok: #901 canonical is CLAUDE.md -->"
    # No marker on the src side, deliberately: one side's marker exempts the pair (the #676 arm
    # above pins that), so a fixture carrying the C# marker too would pass with the markdown one
    # still dead -- which is exactly how the first draft of this arm failed to discriminate.
    md_marked = {"src/A.cs": [f"// {sentence}"], "docs/B.md": [sentence, md_marker]}
    assert not rec.violations(added, lambda path: md_marked.get(path)), (
        "record-once: an HTML-comment marker in a markdown file exempted nothing")

    # Its malformed sibling must be REPORTED, not silent -- SUPPRESS_LOOSE has to see the same
    # comment shape SUPPRESS does, or the mistyped-marker class reopens for markdown specifically.
    md_typo = {"docs/B.md": [sentence, "<!-- record-once-ok #901 CLAUDE.md -->"],
               "src/A.cs": [f"// {sentence}", marker]}
    assert any("does not parse" in b for b in rec.groups(added, lambda path: md_typo.get(path))[2]), (
        "record-once: a malformed HTML-comment marker in markdown failed silently")

    # And prose about the comment form stays inert -- mid-sentence, the `<!--` never opens the
    # line, so a doc explaining this syntax (this repo has one) neither exempts nor reports.
    md_mention = {"docs/B.md": [sentence, f"write {md_marker} beside the copy"],
                  "src/A.cs": [f"// {sentence}"]}
    at_md_mention = lambda path: md_mention.get(path)  # noqa: E731
    assert rec.violations(added, at_md_mention), (
        "record-once: an HTML-comment marker quoted mid-sentence exempted a passage")
    assert not rec.groups(added, at_md_mention)[2], (
        "record-once: prose describing the markdown marker form was reported as a broken one")

    # Buried markers -- a list bullet or a doubled opener in front of the comment, the two shapes
    # the #691 review measured as fully silent. Never honoured (the own-line rule stands), never
    # silent (both land in the malformed report). What separates these from the inert mid-sentence
    # mention above is that the bullet or the opener OPENS the line.
    for buried in (f"- {md_marker}", f"<!-- {md_marker}"):
        md_buried = {"docs/B.md": [sentence, buried], "src/A.cs": [f"// {sentence}"]}
        at_buried = lambda path, m=md_buried: m.get(path)  # noqa: E731
        assert rec.violations(added, at_buried), (
            f"record-once: a buried marker ({buried!r}) exempted a passage the own-line rule refuses")
        assert rec.groups(added, at_buried)[2], (
            f"record-once: a buried marker ({buried!r}) failed silently instead of being reported")

    # The locale-decoding crash `GIT_TEXT` in recordonce.py records (#690). Run against a real
    # tracked file whose bytes are not cp1252-decodable: the defect lives in how a subprocess pipe is
    # decoded, so no in-memory fixture can reach it, and the second assertion is what stops the arm
    # quietly ceasing to discriminate if that file is ever rewritten in ASCII.
    unmappable = "docs/vendor-doc-audit.md"
    at_head = rec.file_at(unmappable)
    assert at_head, f"record-once: file_at returned nothing for {unmappable}, so this arm tested nothing"
    # The property has to be "holds a byte cp1252 REJECTS", not "holds a non-ASCII character", and
    # the difference is not pedantic: cp1252 rejects exactly these five bytes, so an em dash
    # (`e2 80 94`) or a check mark (`e2 9c 85`) decodes cleanly under it while satisfying any
    # "outside latin-1" test. An earlier version of this guard asserted `ord(c) > 255` and would have
    # gone on passing after every rejecting character was edited out, leaving both this arm and the
    # `audit-controls` arm that depends on it silently testing nothing.
    #
    # Today the file qualifies on U+274C CROSS MARK (x10), U+2190 LEFTWARDS ARROW and U+23F8 -- the
    # arrow being the `0x90` in the crash that found the defect. Deliberately not pinned to those
    # characters: any rejecting byte does, and naming them would make an ordinary edit look like a
    # regression.
    CP1252_REJECTS = {0x81, 0x8D, 0x8F, 0x90, 0x9D}
    assert any(b in CP1252_REJECTS for line in at_head for b in line.encode("utf-8")), (
        f"record-once: {unmappable} no longer holds a byte cp1252 rejects, so neither this arm nor "
        "`audit-controls`' hostile-codec arm discriminates -- point both at a file that does")

    # -- #1431/#1367: main()'s population filters run OUTSIDE the violations() path every arm
    # above exercises, so a typo'd prefix or flipped branch there is invisible to all of them.
    # Both directions per filter: what each must exclude, and near-miss paths it must not.
    excl = rec.excluded_from_comparison
    assert excl("docs/decisions/0001-two-nouns-workflow-and-session.md") == "restored-decision", (
        "record-once: a restored decision record was not excluded from comparison")
    assert excl("CHANGELOG.md") == "changelog", (
        "record-once: a generated changelog was not excluded from comparison")
    for near_miss in ("docs/decisionsx/0001-imposter.md", "docs/B.md", "src/A.cs",
                      "docs/CHANGELOG-notes.md"):
        assert excl(near_miss) is None, (
            f"record-once: {near_miss} was excluded from comparison but is ordinary population")

    return (f"{len(polarities)} record-once polarities "
            f"({sum(1 for p in polarities if not p[2])} must NOT fire) + 9 exemption arms "
            f"+ 6 population-filter polarities + a non-cp1252 file read through git")


@check("the record-once checker still finds the passages it found in a real merge")
def _recordonce_still_fires_on_real_data():
    """Fixtures above encode the failures already known. This one runs against a real diff.

    Two designs of that checker passed every fixture written for them and were useless on the merge
    they existed to catch, so the fixtures cannot be the whole test. Registered here rather than left
    as a bare CLI mode so `audit-controls` reaches it: an unadjudicated pin would otherwise sit green
    forever.
    """
    rec = load(ROOT / "tools" / "audit-completeness" / "recordonce.py", "_selfcheck_recordonce_pin")
    ok, detail = rec.prove(rec.PROVEN_SHA, rec.PROVEN_GROUPS)
    assert ok, "record-once no longer finds what it found in " + rec.PROVEN_SHA[:7] + ":\n  " \
        + "\n  ".join(detail)
    return detail[0]


@check("the record-once checker applies every exclusion reason to its changed-file population")
def _recordonce_applies_every_exclusion():
    """#1466: `excluded_from_comparison()` classifies files to exclude, but `main()` must actually
    delete matching entries from `by_file` before comparison. #1465 proved the blind spot: a new
    reason existed in the classifier while `main()` still ignored it, and `_recordonce_discriminates`
    passed 20/20 because it called the classifier directly.

    Runs `main()` against a synthetic change set containing one file per dynamically enumerated
    exclusion reason alongside an ordinary file, all sharing identical prose. If `main()` applies all
    exclusions, only the ordinary file survives and comparison succeeds; if any reason is not deleted,
    the un-deleted file collides with the ordinary file and `main()` fails with a duplication violation.
    """
    rec = load(ROOT / "tools" / "audit-completeness" / "recordonce.py", "_selfcheck_recordonce_exclusions")

    # Enumerate exclusion reasons dynamically from AST of excluded_from_comparison.
    source = inspect.getsource(rec.excluded_from_comparison)
    tree = ast.parse(source)
    reasons = {
        node.value.value
        for node in ast.walk(tree)
        if isinstance(node, ast.Return)
        and node.value is not None
        and isinstance(node.value, ast.Constant)
        and isinstance(node.value.value, str)
    }
    assert reasons, "record-once: found no exclusion reasons in excluded_from_comparison"

    # Known sample paths representing each exclusion category. If a future reason is added to
    # excluded_from_comparison without registering a sample path fixture here, the test fails
    # immediately rather than silently testing an incomplete set of reasons.
    fixtures = {
        "changelog": "CHANGELOG.md",
        "restored-decision": "docs/decisions/0001-two-nouns-workflow-and-session.md",
    }
    uncovered = reasons - set(fixtures.keys())
    assert not uncovered, (
        f"record-once: excluded_from_comparison returned reason(s) {sorted(uncovered)} with no "
        "fixture path in selfcheck -- register sample path(s) to verify main() applies them"
    )

    for reason, path in fixtures.items():
        classified = rec.excluded_from_comparison(path)
        assert classified == reason, (
            f"record-once: fixture {path} expected classification {reason!r}, got {classified!r}"
        )

    sentence = "a withheld write reaching the outbox is the only exemption that exists in this design"
    ordinary = "src/Ordinary.cs"
    test_files = {path: reason for reason, path in fixtures.items()}
    test_files[ordinary] = None

    # Synthetic diff: all files share the exact same added passage.
    synthetic_diff = {}
    synthetic_head = {}
    for path in test_files:
        line = sentence if path.endswith(".md") else f"// {sentence}"
        synthetic_diff[path] = [[line]]
        synthetic_head[path] = [line]

    rec.added_lines_by_file = lambda base: {k: [list(h) for h in v] for k, v in synthetic_diff.items()}
    rec.file_at = lambda path, rev="HEAD": synthetic_head.get(path, [])

    buf_out = io.StringIO()
    buf_err = io.StringIO()
    with contextlib.redirect_stdout(buf_out), contextlib.redirect_stderr(buf_err):
        code = rec.main([])

    assert code == 0, (
        f"record-once main() failed to apply all exclusion reasons (exit {code}):\n"
        f"stdout:\n{buf_out.getvalue()}\nstderr:\n{buf_err.getvalue()}"
    )

    out_text = buf_out.getvalue()
    assert "record-once: 1 changed file(s)" in out_text, (
        f"record-once main() did not reduce population to 1 ordinary file:\n{out_text}"
    )
    for path in fixtures.values():
        assert path not in buf_err.getvalue(), (
            f"record-once main() reported violation in excluded path {path}:\n{buf_err.getvalue()}"
        )

    return f"{len(reasons)} dynamically enumerated exclusion reason(s) ({', '.join(sorted(reasons))}) applied and deleted in main()"


@check("the agy.tools-classified sentinel check discriminates on unknown or multiply-classified tools")
def _agy_tools_classified_discriminates():
    """#1928 / #623: asserts _agy_tools_classified rejects uncatalogued or duplicated tools.

    Validates that unregistered tools produce FAIL naming the tool, valid fixtures PASS,
    and entries appearing in multiple categories cause FAIL.
    """
    tool_lists = verify.load_agy_adapter_tool_lists()
    expected_lists = {"ReadTools", "WriteTools", "ShellTools", "SubagentAndTaskTools", "NetworkTools"}
    assert expected_lists.issubset(set(tool_lists.keys())), (
        f"missing expected lists in AgyWorkerAdapter.cs: {expected_lists - set(tool_lists.keys())}"
    )

    fixture_known = [
        "view_file", "list_dir", "find_by_name", "grep_search",
        "write_to_file", "replace_file_content", "multi_replace_file_content", "generate_image",
        "run_command", "manage_task", "invoke_subagent", "define_subagent", "manage_subagents",
        "search_web", "read_url_content", "browser_click", "browser_navigate",
    ]
    status, msg = verify._agy_tools_classified(fixture_known, tool_lists=tool_lists)
    assert status == verify.PASS, f"known tools fixture failed classification: {status} -- {msg}"

    unknown_tool = "__unknown_test_tool__"
    fixture_unknown = ["view_file", unknown_tool]
    status, msg = verify._agy_tools_classified(fixture_unknown, tool_lists=tool_lists)
    assert status == verify.FAIL, f"unknown tool fixture did not FAIL: {status} -- {msg}"
    assert unknown_tool in msg, f"unknown tool {unknown_tool} not named in failure message: {msg}"

    duplicate_lists = {k: list(v) for k, v in tool_lists.items()}
    duplicate_lists["ReadTools"].append("run_command")
    status, msg = verify._agy_tools_classified(fixture_known, tool_lists=duplicate_lists)
    assert status == verify.FAIL, f"multiply-classified tool fixture did not FAIL: {status} -- {msg}"
    assert "multiply classified" in msg, f"multiply-classified failure message missing expected text: {msg}"

    return "3 classification arms (matching PASS, unknown FAIL with name, duplicate FAIL)"


def main() -> int:
    print(__doc__.strip().splitlines()[0])
    print("=" * 78)
    for line in ("Most assertions map to a defect that shipped into a draft of #627.",
                 "Cannot check: whether a population is the RIGHT population, or anything in prose."):
        print(f"  - {line}")
    print()

    for name, fn in CHECKS:
        try:
            population = fn()
        except AssertionError as e:
            FAILURES.append(f"{name}: {e}")
            print(f" !! {name}\n      {e}")
        except Exception as e:  # noqa: BLE001 -- see below
            # Not just AssertionError. A check can raise FileNotFoundError (a bindings.json that was
            # never written), JSONDecodeError, or SystemExit(2) from argparse if a flag it names is
            # removed. Any of those used to abort the whole run before the remaining checks and
            # before the summary -- the exit code stayed non-zero, so never a false pass, but the
            # file's whole premise is that a failure says what failed.
            FAILURES.append(f"{name}: {type(e).__name__}: {e}")
            print(f" !! {name}\n      raised {type(e).__name__}: {e}\n"
                  f"      (a raise, not a failed assertion -- the check itself is broken)")
        else:
            if not population:
                # An assertion that reports no population cannot be distinguished from one that
                # examined nothing, which is the whole defect class here.
                FAILURES.append(f"{name}: reported no population")
                print(f" !! {name}\n      passed without reporting a population -- it cannot be "
                      "told apart from a check that compared nothing")
            else:
                print(f" OK {name}")
                print(f"      {population}")

    if FAILURES:
        print(f"\n{len(FAILURES)} failing assertion(s).")
        return 1
    print(f"\nAll {len(CHECKS)} assertions hold.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
