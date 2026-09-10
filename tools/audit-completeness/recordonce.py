"""Fail a change that writes the same passage into more than one file (#671).

`record-once` is the gate with the worst compliance record in this repo, and the half of it that
concerns restatement had no checker. It is prose enforcing prose, so it fails the way prose does:
one change restated a single corrected fact into five files, and CI was green throughout.

Operates on the DIFF, never the tree, and on PROSE, not on issue references.

**Counting references was tried first and does not work.** Measured against the merged PR this was
written for: it flagged the one real restatement (5 files) and also flagged the issue that PR
*implements* (30 files), plus two legitimate cross-references to prior art (7 and 4 files). A
reference proliferating is the register working. Worse, no threshold separates them -- the true
positive sat below the false ones.

What the defect actually looked like was one *sentence* in four files. So: normalise added prose,
shingle it, and fail when the same shingle lands in two files at once. Thirty mentions of one issue
share no shingles; one sentence written twice shares all of them. This also catches restatement that
cites no issue at all, which the reference design could not see by construction.

    pixi run audit-recordonce            # against origin/main
    pixi run audit-recordonce -- <base>  # against any other base

WHAT IT CANNOT CHECK:
  * A copy of text that ALREADY EXISTS in the tree. The population is added lines, so both copies
    have to be written in the same change. Pasting a paragraph out of the development guide into a
    new doc is invisible -- which is the dominant real shape of the violation. #674.
  * Which change introduced a duplication. `git diff` emits a modified line as `+`, so touching two
    files that already shared a passage reads the same as writing it twice. #674.
  * A comment the change FALSIFIED without touching -- absent from the diff by definition. #636's.
  * The same fact PARAPHRASED. Shingles match text, not meaning: nine consecutive words have to
    match, so one substituted word inside the window defeats it. Measured, and worth knowing how
    weak this makes the tool on the shape people actually write: `recordonce.py`'s own module
    docstring and `selfcheck.py`'s `_recordonce_discriminates` docstring record the same measurement
    independently, and share ZERO 9-grams. #675 cites that pair as a live instance; reading
    docstrings was necessary to see it and is not sufficient.
  * STRING LITERALS, still. Error messages, CLI help and journey titles are prose a change can
    restate, and #675 named them. Not fixed here: an unanchored search for a comment opener matches
    inside `"https://..."`, and reading code as prose is the false-positive class this checker has
    already shipped twice. Comment context is the fix #675 prescribes; literals need a lexer.
  * A comment TRAILING code on the same line -- `x = 1; /* note */`. Openers are anchored at the
    start of a line for the reason above.
  * A hunk that BEGINS inside a block comment, which reads as ordinary code: only added lines are
    visible, so there is no opener to see. Assuming closed can only miss prose, never invent it.
  * `.json`, `.txt` and the VALUES in `.yml`/`.toml`/`.csproj` -- data positions, not comments.
    `.rst`/`.mdx` are not listed because neither exists in this repo; add them with a file, not in
    advance.
  * A file whose EXTENSION is not in LANGUAGES -- a `.rb`, a `.lua` -- reads as nothing, silently.
    Extensionless files do not; see `NO_EXTENSION` for what that fallback is and what found it.
    An unknown extension is not given the same treatment, since guessing `#` on a data format would
    read values as prose. Adding one is one LANGUAGES row.
  * Whether the surviving copy is the right one. It finds duplicates; it does not rank them.
  * Whether a marker's ISSUE is real or open. Its canonical PATH is checked -- a marker naming a file
    that is not there exempts nothing and says so -- but reaching GitHub from a gate would make CI
    depend on a network call to stay green. #676 lists this; it is deliberately not fixed.
  * How MANY passages one change exempts. Uncapped, and judged to be correct rather than a gap:
    each marker now costs a comment beside the passage it covers and names where the fact really
    lives, so twenty exemptions are twenty visible decisions rather than one line muting a file. A
    numeric cap would be arbitrary and would fail the change that legitimately needs it.
"""
from __future__ import annotations

import collections
import re
import subprocess
import sys
from pathlib import Path

from completeness import generated_changelog

ROOT = Path(__file__).resolve().parents[2]

# Escapes one PASSAGE, for a second copy that is genuinely right -- a decision record and the code it
# governs. An issue AND a canonical path are both required, so the marker reads as a decision with a
# destination rather than a mute (#676).
#
# The unit is the contiguous run the marker sits in, not the file. As a file-level hatch it was too
# coarse in one direction and too weak in the other: a marker anywhere in `docs/plan.md` stopped every
# other passage that change added to plan.md from being compared, and a change could mute every file
# it touched with one added line each. A run is the passage, so exempting a second one costs a second
# marker beside it -- which is the point, since each is a separate decision.
#
# Read from the file AT HEAD rather than from the diff, which is the other half of #676. Matched
# among added lines, an exemption granted by an earlier PR exempted nothing later: reword both copies
# of a deliberately duplicated passage without re-touching the marker line and it was flagged again,
# so the hatch had to be re-applied to stay applied.
# Anchored to the START of the comment, so a marker is a comment line and not a phrase inside one.
# Measured on this file: the docstring below explaining that `record-once-ok: #901 docs/B.md` written
# as a Python literal exempts nothing was itself read as a marker naming `docs/B.md``, backtick and
# all, the moment #675 made docstrings visible. Prose ABOUT the marker is the one text guaranteed to
# contain it, so an unanchored match turns every explanation into a decision.
SUPPRESS = re.compile(r"^\s*record-once-ok:\s*#(\d{3,})\s+(?:canonical\s+is\s+)?(\S+)\s*$")

# Anything opening a comment with `record-once-ok` that SUPPRESS then refuses to parse -- a missing
# path, a missing issue, a block closer trailing on the line. Without this a mistyped marker is a
# silent no-op: no exemption, no message, and an author who believes a decision was recorded facing
# a gate that believes nothing was said -- the failure `replacing()` in controls.py records.
SUPPRESS_LOOSE = re.compile(r"^\s*record-once-ok\b")

# Markdown's one comment form, for marker purposes only (#691). Markdown is this gate's dominant
# population, and before this the documented comment form silently exempted nothing there: `prose`
# is the RAW line in a markdown file, so `<!--` in front of the marker defeated SUPPRESS's anchor,
# and defeated SUPPRESS_LOOSE's identically -- the mistyped-marker reporter could not see the well
# typed one either. Anchored to the line start for the same reason SUPPRESS is: `<!--` quoted
# mid-sentence is prose about a comment. The closer is optional so an unclosed opener still reads
# as the decision it announces; words for shingling keep coming from the raw line either way.
MD_COMMENT = re.compile(r"^\s*<!--\s*(?P<body>.*?)\s*(?:-->\s*)?$")

# A marker an author visibly attempted in a shape the own-line anchor can never honour: buried
# behind a markdown list bullet, or behind a doubled `<!--` opener (both measured silent in the
# #691 review). Matched for REPORTING only -- these land in the malformed path even when the text
# inside would parse cleanly, because honouring them would quietly widen the own-line rule
# MD_COMMENT deliberately anchors. A mention in running prose stays inert: this requires the
# bullet or the opener to open the line, never to sit mid-sentence. The plain single-opener form
# also matches, harmlessly -- SUPPRESS/SUPPRESS_LOOSE always claim it first.
MD_BURIED = re.compile(r"^\s*(?:[-*+]\s+|\d+[.)]\s+)?(?:<!--\s*)+record-once-ok\b")

# The issue field of a marker that announced itself and did not parse. Not a real issue number, and
# it never reaches the exempting path -- it exists so one code path carries both author errors.
MALFORMED = "?"

# Long enough that ordinary phrasing does not collide by accident, short enough to catch a restated
# clause rather than only a whole restated paragraph.
SHINGLE = 9

# Comment leaders, markup and citation noise, so one sentence matches across a `///` C# comment, a
# `#` Python one and a markdown paragraph -- which is how the measured case was spread.
LEADER = re.compile(r"^\s*(///|//|/\*|\*/|\*|#+|--|<!--|-->|-|\d+\.)\s*")
MARKUP = re.compile(r"</?[a-zA-Z][^>]*>|[`*_\[\]()<>]|&\w+;")
NOISE = re.compile(r"#\d{3,}|https?://\S+")

# A markdown inline-link TARGET -- the `(nnnn-slug.md)` after a `]` -- is a pointer to a canonical
# record, not prose, exactly as an issue number or a bare URL is (both dropped by NOISE). Two decision
# records that legitimately link the same decision would otherwise collide on the slug words baked into
# the target path: 0046 and 0047 shared exactly one shingle -- `md 0003 0003 templates collapse to
# three shapes md`, entirely `](0003-templates-collapse-to-three-shapes.md)` link text with no shared
# prose. That is "a reference proliferating is the register working," the false-positive class this
# checker's own design disavows. Stripped BEFORE MARKUP, which dissolves the `]( )` that identifies a
# target; only the target is dropped, the visible `[text]` stays and normalises as prose. This drops
# ANY inline-link target -- `.md` cross-link, external URL, image -- not only decision links. `[^)]*`
# stops at the first `)`, so a target that nests a paren (a `(...Foo_(bar))` URL) would leak its tail
# as stray prose; none exists anywhere in the repo today (measured across docs/, src/, tests/, tools/
# before landing), and a leak could at most add a shingle a real duplication already carries, never
# drop one -- the safe direction, same as every other narrowing in this file.
LINK_TARGET = re.compile(r"\]\([^)]*\)")


# Prose only. Duplicated *code* across files is ordinary -- two tests legitimately open with the same
# `var grant = new PermissionGrant(...)` and the same `using var stderr = new StringWriter()`, and
# flagging those was the second false-positive class this check produced. In a code file only comment
# lines are read; markdown is prose apart from the exclusions below.
PROSE_EVERYWHERE = (".md",)

# Comment CONTEXT, per language, replacing a single leader regex applied to every line of every file
# (#675). The regex read a line and could not read a block: a `/* */` body carries no leader on its
# own lines, and neither does a Python docstring, so the prose that most often states a fact once was
# the prose least visible here. Worse, it matched a leader wherever it appeared -- a `#` inside a
# docstring, a `*` in a table -- so fragments of non-comment text entered the stream as if they were
# comments. Contiguity stops those fragments welding onto their neighbours; this stops them being
# read at all.
#
# Openers are anchored at the start of a line, closers are not. `/* note */` trailing a statement is
# therefore missed, deliberately: an unanchored `//` matches inside `"https://..."` and an unanchored
# `/*` inside any string holding one, and reading code as prose is the false-positive class this
# checker has already shipped twice.
#
# (extensions, line-comment openers, (block open, block close) pairs)
LANGUAGES = (
    ((".cs", ".dart", ".go", ".rs", ".ts", ".js", ".java", ".kt", ".kts"),
     ("///", "//"), (("/*", "*/"),)),
    ((".py",), ("#",), (('"""', '"""'), ("'''", "'''"))),
    ((".csproj", ".props", ".targets", ".axaml", ".xml", ".slnx", ".html", ".resx"),
     (), (("<!--", "-->"),)),
    ((".ps1",), ("#",), (("<#", "#>"),)),
    ((".sh", ".yml", ".yaml", ".toml", ".cfg", ".ini", ".editorconfig", ".gitignore",
      ".gitattributes", ".properties"), ("#",), ()),
)
LINE_OPENER = re.compile(r"^\s*")

# A file with no extension at all -- `.githooks/pre-push`, a `Dockerfile`, a `Makefile` -- is read as
# `#`-commented rather than as nothing. Measured on the file that prompted it: `.githooks/pre-push`
# went from 11 of its 12 lines read to 0 the moment the table above replaced the single leader regex,
# and nothing said so. `#` only, which is narrower than the leader regex it restores: an extensionless
# file that turns out to be C-like is read by nothing here, and unread is the direction to be wrong in.
# An UNKNOWN extension still reads as nothing -- add it to LANGUAGES; see WHAT IT CANNOT CHECK.
NO_EXTENSION = ("#",)

# Text whose duplication `record-once` PRESCRIBES, and which therefore cannot be evidence against it.
#
#   * A markdown table row. The decision-index row repeats the record's own title verbatim -- a
#     derived copy, generated from the record since #952 (before that it was hand-written in three
#     files and this exemption was carrying the duplication the register itself mandated).
#   * A fenced block inside markdown. Two runbooks showing the same `pixi run` invocation are
#     showing the same command, not restating a fact.
#   * A generated file. Its single source is a string literal in the generator, invisible here; the
#     copies are derived. Rewording the banner re-emits it into every generated file at once, and
#     those files cannot carry a suppression marker -- Baton.Architecture.Tests fails hand edits.
PATH_PREFIX = re.compile(r"^b/")
TABLE_ROW = re.compile(r"^\s*\|")
FENCE = re.compile(r"^\s*(```|~~~)")
GENERATED = re.compile(r"GENERATED FILE", re.IGNORECASE)


def language(path: str) -> tuple[tuple[str, ...], tuple[tuple[str, str], ...]]:
    """This file's line-comment openers and block-comment delimiters.

    An extensionless file falls back to `NO_EXTENSION`; an unrecognised extension reads as nothing.
    """
    for extensions, line_openers, blocks in LANGUAGES:
        if path.endswith(extensions):
            return line_openers, blocks
    if Path(path).suffix == "":
        return NO_EXTENSION, ()
    return (), ()


def comment_text(lines: list[str], line_openers, blocks):
    """Each line's comment content, or None where the line is not comment prose.

    A generator over ONE hunk, and block state starts closed on every hunk. Only added lines are
    visible here, so a hunk beginning in the middle of a `/* */` cannot be told from one that is not
    in a comment at all. Assuming closed can only miss prose; assuming open would read code as prose,
    which is the direction that has already produced false positives twice.
    """
    closer = None
    for line in lines:
        if closer is not None:
            end = line.find(closer)
            if end == -1:
                yield line
            else:
                yield line[:end]
                closer = None
            continue

        indent = LINE_OPENER.match(line).end()
        body = line[indent:]

        for opener, close in blocks:
            if body.startswith(opener):
                rest = body[len(opener):]
                end = rest.find(close)
                # `"""one-liner"""` opens and closes on one line; the delimiters being identical for
                # a docstring is why the search starts AFTER the opener rather than at the line head.
                if end == -1:
                    closer = close
                    yield rest
                else:
                    yield rest[:end]
                break
        else:
            for opener in line_openers:
                if body.startswith(opener):
                    yield body[len(opener):]
                    break
            else:
                yield None


def prose_runs(path: str, hunks: list[list[str]]) -> list[list[str]]:
    """The added prose of one file as CONTIGUOUS runs, each a normalised word stream.

    Runs, not one stream per file, because a shingle that spans a break is evidence of a sentence
    nobody wrote. Measured, twice, before this was changed:

      * A `.py` file whose real comment `# the gate refuses a payload it cannot judge` was followed
        by an unrelated docstring line `# a hash inside a docstring` (read because `COMMENT` matches
        a leading `#` with no notion of context) produced FIVE shingles, every one of them a word
        sequence present in no line of the file.
      * Two `///` comments 400 lines apart in one `.cs` file -- two hunks, handed over adjacent with
        nothing marking the gap -- produced five more. This one needs no docstring and no Python: it
        is the ordinary shape of any change that edits two places in a file.

    Both could be reported as the `e.g. "..."` sample under a real finding, and both could make two
    files share a shingle neither of them contains. A checker whose evidence can be fabricated is
    not one anybody should act on.

    A run breaks at a hunk boundary, at a non-prose line, and at a prose line carrying no words --
    an empty `///` or a blank markdown line. That last one is a deliberate choice rather than a
    side effect: it is a paragraph break, two paragraphs are two passages, and a sentence cannot
    wrap across one. It can only ever shrink the shingle set, never invent a match.
    """
    return [words for words, _ in marked_runs(path, hunks)]


def marked_runs(path: str, hunks: list[list[str]]) -> list[tuple[list[str], tuple[str, str] | None]]:
    """Every contiguous run, paired with the `record-once-ok` marker sitting in it, or None.

    The marker is read out of the run's PROSE, never off the raw line, which is #676's context test:
    the string `record-once-ok: #901 docs/B.md` written as a Python literal is code and exempts
    nothing, while the same words in a comment are a decision. Before this, any tracked file
    containing those characters anywhere exempted itself -- `selfcheck.py` had to assemble the string
    from fragments to be able to have a fixture for the checker at all.
    """
    if any(GENERATED.search(line) for hunk in hunks for line in hunk[:8]):
        return []

    markdown = path.endswith(PROSE_EVERYWHERE)
    line_openers, blocks = language(path)
    runs: list[tuple[list[str], tuple[str, str] | None]] = []
    for lines in hunks:
        current: list[str] = []
        marker: tuple[str, str] | None = None
        fenced = False
        comments = [None] * len(lines) if markdown else list(comment_text(lines, line_openers, blocks))
        for line, comment in zip(lines, comments):
            words: list[str] = []
            prose = None
            marker_text = None
            if markdown and FENCE.match(line):
                fenced = not fenced
            elif fenced or (markdown and TABLE_ROW.match(line)):
                pass
            elif markdown:
                prose = line
                # The marker is read out of the comment BODY while the words stay raw -- see
                # MD_COMMENT for why the two diverge in markdown and nowhere else.
                marker_text = m.group("body") if (m := MD_COMMENT.match(line)) else line
            elif comment is not None:
                prose = comment
                marker_text = comment

            if prose is not None:
                if (found := SUPPRESS.search(marker_text)) is not None:
                    marker = (found.group(1), found.group(2))
                elif SUPPRESS_LOOSE.search(marker_text) is not None:
                    marker = (MALFORMED, prose.strip())
                elif markdown and MD_BURIED.match(line) is not None:
                    marker = (MALFORMED, prose.strip())
                words = normalise(prose)

            if words:
                current.extend(words)
            elif current:
                runs.append((current, marker))
                current, marker = [], None
        if current:
            runs.append((current, marker))
    return runs


def normalise(line: str) -> list[str]:
    text = LEADER.sub("", line)
    text = LINK_TARGET.sub("]", text)    # a link target is a pointer, not prose (see LINK_TARGET)
    text = MARKUP.sub(" ", text)
    text = NOISE.sub(" ", text)          # the issue number is not the fact
    text = re.sub(r"[^a-z0-9 ]+", " ", text.lower())
    return text.split()


# A real historical change this must still fire on, pinned by SHA and by exact result (`--prove`).
#
# Fixtures are not enough and that is measured, not cautionary: two earlier designs of this checker
# passed every fixture written for them and were useless against the diff they existed to catch. The
# first counted issue references and flagged the issue its own PR implemented; the second read
# duplicated test setup as restatement. Both looked healthy in `selfcheck.py`.
#
# fc884cd is the #666 merge, which restated one corrected fact across several files.
#
# The pin is the file-sets, not how many there are. A count only ever moves in one direction:
# `SHINGLE = 3` would satisfy `>= n` while making the tool unusable, and any false positive the pin
# happened to include would become mandatory -- fixing it would break the pin. Pinning the sets
# means a change to WHICH passages are found has to be adjudicated line by line, which is the only
# reading of this list that is worth anything. Each entry below was read; none is boilerplate.
#
# REPINNED ONCE, #675, and the adjudication is the point of keeping this list. Teaching the checker
# to read comment CONTEXT rather than line leaders made `tools/aer-agy-loop/dispatch.py` visible for
# the first time -- its prose is a module docstring, which carries no leader on any line. The pinned
# two-file group grew a third file and a second group appeared. Both were read before repinning:
#
#   "on claude the write tools stay pre-approved and AER's PreToolUse hook confines them to the outbox"
#
# is in `docs/decisions/0004-permission-scopes.md:57`, `src/Aer.Adapters/IWorkerAdapter.cs:100-101`
# and `dispatch.py:231` at that SHA -- one fact, three files, near-verbatim. That is the defect this
# checker exists for, found by the change rather than broken by it. A pin moving is not a licence to
# repin; this one moved because the passage was read and judged real.
#
# #1458 renamed every one of these paths (Baton everywhere). The tuples below still spell out the
# NAMES AS THEY EXISTED AT fc884cd -- pinned by SHA against a commit git history cannot rewrite, so
# they stay `Aer.*`/`aer-agy-loop` regardless of what the current tree calls the same files.
PROVEN_SHA = "fc884cd6dac19f16d803c28246e101e1c9fef493"
PROVEN_GROUPS = (
    ('docs/decisions/0004-permission-scopes.md', 'src/Aer.Adapters/IWorkerAdapter.cs',
     'tools/aer-agy-loop/dispatch.py'),
    ('docs/decisions/0004-permission-scopes.md', 'tools/aer-agy-loop/dispatch.py'),
    ('docs/decisions/0029-the-gate-is-three-mechanisms.md', 'docs/documentation-lessons.md',
     'src/Aer.Adapters/ClaudeWorkerAdapter.cs', 'tests/Aer.Cli.Tests/HookCheckCommandTests.cs'),
    ('docs/decisions/0029-the-gate-is-three-mechanisms.md',
     'tests/Aer.Cli.Tests/HookCheckCommandTests.cs'),
    ('docs/documentation-lessons.md', 'src/Aer.Adapters/ClaudeWorkerAdapter.cs'),
    ('docs/documentation-lessons.md', 'tests/Aer.Cli.Tests/HookCheckCommandTests.cs'),
    ('docs/runbooks/live-claude-smoke.md',
     'tests/Aer.Cli.SmokeTests/LiveReadOnlyReviewerSmokeTest.cs'),
    ('docs/vendor-doc-audit.md', 'src/Aer.Cli/HookCheckCommand.cs'),
    ('src/Aer.Adapters/ClaudeWorkerAdapter.cs', 'tests/Aer.Adapters.Tests/ClaudeWorkerAdapterTests.cs'),
    ('src/Aer.Adapters/IncoherentPermissionGrantException.cs',
     'src/Aer.Adapters/WorkerBindingResolver.cs'),
    ('src/Aer.Adapters/WorkerBindingResolver.cs',
     'tests/Aer.Adapters.Tests/WorkerBindingResolverTests.cs'),
    ('src/Aer.Cli/HookCheckCommand.cs', 'src/Aer.Cli/OutboxPath.cs',
     'tests/Aer.Cli.Tests/OutboxWriteExemptionTests.cs'),
    ('src/Aer.Cli/HookCheckCommand.cs', 'tests/Aer.Cli.Tests/HookCheckCommandTests.cs'),
    ('src/Aer.Cli/HookCheckCommand.cs', 'tests/Aer.Cli.Tests/OutboxWriteExemptionTests.cs'),
    ('src/Aer.Cli/OutboxPath.cs', 'tests/Aer.Cli.Tests/OutboxWriteExemptionTests.cs'),
    ('tests/Aer.Ui.Tests/SessionAnswerWithoutOutputFileTests.cs',
     'tests/Aer.Ui.Tests/TestSupport/SessionTurnStubAdapter.cs'),
)


def prove(sha: str, expected: tuple[tuple[str, ...], ...]) -> tuple[bool, list[str]]:
    """Run against a recorded historical change and report whether it finds the same passages."""
    try:
        by_file = added_lines_by_file(f"{sha}^", head=sha)
    except subprocess.CalledProcessError as exc:
        return False, [f"cannot read {sha[:7]} -- {exc.stderr.strip()}"]

    found = {tuple(g) for g in groups(by_file)[0]}
    want = {tuple(g) for g in expected}
    if found == want:
        return True, [f"{len(found)} passage(s) in {sha[:7]}, all as pinned"]

    detail = [f"no longer finds in {sha[:7]}:  {g}" for g in sorted(want - found)]
    detail += [f"now finds in {sha[:7]}, unpinned:  {g}" for g in sorted(found - want)]
    return False, detail


def added_lines_by_file(base: str, head: str = "HEAD") -> dict[str, list[list[str]]]:
    """Every line this change adds, keyed by file and SPLIT BY HUNK.

    `--unified=0` so no context line is counted. The split matters and is not bookkeeping: two hunks
    are two places in the file, and text joined across them is text nobody wrote. See `prose_runs`.
    """
    out = subprocess.run(
        ["git", "diff", "--unified=0", f"{base}...{head}"],
        capture_output=True, text=True, check=True, **GIT_TEXT).stdout

    by_file: dict[str, list[list[str]]] = collections.defaultdict(list)
    current = None
    hunk: list[str] | None = None
    for line in out.splitlines():
        if line.startswith("+++"):
            # git quotes a path holding non-ASCII or shell-special characters: `+++ "b/docs/café.md"`.
            # Matching only `+++ b/` left `current` pointing at the previous file, so that file's
            # added lines were appended to a stream belonging to a different path.
            path = line[4:].strip()
            current = None if path == "/dev/null" else PATH_PREFIX.sub("", path.strip('"'), count=1)
            hunk = None
        elif line.startswith("@@"):
            hunk = None
        elif line.startswith("+") and current:
            if hunk is None:
                hunk = []
                by_file[current].append(hunk)
            hunk.append(line[1:])
    return by_file


# Every `git` read here is UTF-8, decoded leniently. `text=True` alone uses the LOCALE codec, which
# on Windows is cp1252 -- and cp1252 rejects exactly five bytes: 0x81, 0x8D, 0x8F, 0x90, 0x9D.
#
# THE SHAPE DIFFERS BY PLATFORM, which matters when reading a bug report against this:
#   * Windows -- the decode happens in subprocess's reader THREAD, so the UnicodeDecodeError does not
#     propagate. `run` returns with `stdout` set to None and what stops the process is an
#     `AttributeError` two calls away. The thread's own traceback IS printed by `threading.excepthook`
#     first, so the cause is on screen -- just detached from the traceback that ends the run, above
#     it, and attributed to a thread the reader never started.
#   * POSIX -- the decode happens in the caller, so it raises `UnicodeDecodeError` directly.
#
# Found by this checker crashing on a change to `docs/vendor-doc-audit.md`. Note which characters
# actually did it, because the obvious guess is wrong: an em dash is `e2 80 94` and decodes cleanly
# under cp1252. What tripped it was one U+2190 LEFTWARDS ARROW (`e2 86 90` -- the 0x90 in the error),
# alongside ten U+274C CROSS MARK (`e2 9d 8c`) and a U+23F8. A guard written against "non-ASCII" or
# "outside latin-1" therefore does not hold this; see `CP1252_REJECTS` in selfcheck.py.
#
# It went unnoticed because `added_lines_by_file` reads only the CHANGED hunks while `file_at` reads
# whole files at HEAD, so the two have wildly different odds of meeting a rejected byte -- and only
# the second is new (#676). Both are fixed, along with every other such call in `tools/`: leaving one
# alone leaves the same defect for whichever file trips it first. `errors="replace"` rather than
# strict because a mangled character can cost at most a shingle match, while a raised exception costs
# the whole gate -- and a gate that cannot run is what this one exists to prevent.
GIT_TEXT = {"encoding": "utf-8", "errors": "replace"}


def file_at(path: str, rev: str = "HEAD") -> list[str] | None:
    """One file's full text at a revision, or None when it is not there (a new or deleted file)."""
    out = subprocess.run(["git", "show", f"{rev}:{path}"],
                         capture_output=True, text=True, check=False, **GIT_TEXT)
    return out.stdout.splitlines() if out.returncode == 0 else None


def exemptions(path: str, at) -> tuple[set[tuple[str, ...]], list[str], list[str]]:
    """Shingles a marker exempts in this file as it stands, what exempted them, and author errors.

    Three returns, not two, because a marker that does not take effect has to reach the exit code
    rather than a printed note. Both bad cases are unambiguous typos with a cheap fix, and both
    previously read as an exemption that happened: the note said "exempted ... not compared" over a
    passage that was compared.

    Read from the whole file rather than from the diff. A marker matched among ADDED lines only held
    for the change that added it, so rewording either copy of a deliberately duplicated passage --
    without re-touching the marker comment, which `--unified=0` would not show -- brought the finding
    straight back. An exemption is a decision about a passage, not about one commit.
    """
    lines = at(path)
    if lines is None:
        return set(), [], []

    shingles: set[tuple[str, ...]] = set()
    notes: list[str] = []
    bad: list[str] = []
    for words, marker in marked_runs(path, [lines]):
        if marker is None:
            continue
        issue, canonical = marker
        if issue == MALFORMED:
            bad.append(f"{path}: a comment opens `record-once-ok` and does not parse, so it\n"
                       f"      exempts nothing: \"{canonical}\"\n"
                       "      Expected `record-once-ok: #<issue> <canonical path>`, alone on the line.")
            continue
        # The canonical location has to exist, which is the part of #676's "nothing verifies this"
        # that needs no network. A marker naming a file that is not there is a typo, and a typo that
        # silences a gate is worse than no marker -- so it exempts nothing and says so, rather than
        # being honoured on the strength of matching a regex.
        if not (ROOT / canonical).exists():
            bad.append(f"{path}: marker #{issue} names `{canonical}`, which does not exist, so it\n"
                       "      exempts nothing.")
            continue
        notes.append(f"{path}: passage(s) exempted by #{issue}, canonical is {canonical}")
        for i in range(len(words) - SHINGLE + 1):
            shingles.add(tuple(words[i:i + SHINGLE]))
    return shingles, notes, bad


def groups(by_file: dict[str, list[list[str]]], at=None
           ) -> tuple[dict[tuple[str, ...], list[tuple[str, ...]]], list[str], list[str]]:
    """File-sets that share at least one shingle, what a marker exempted, and markers that failed.

    `at` is how a file's CURRENT text is fetched -- `path -> lines or None`. A callable rather than
    a revision so a fixture can supply text directly: markers have to be read from whole files, and a
    fixture with no git object behind it would otherwise silently fall back to "no exemptions" and
    look identical to one whose exemption worked. None means no marker source at all.
    """
    # Shingled across each contiguous run rather than per line, because the measured restatement
    # wrapped mid-sentence in every file it landed in -- and no further than a run, because text
    # joined across a break is text nobody wrote. See `prose_runs`.
    where: dict[tuple[str, ...], set[str]] = collections.defaultdict(set)
    suppressed: list[str] = []
    bad: list[str] = []
    for path, hunks in by_file.items():
        exempt, notes, failed = exemptions(path, at) if at else (set(), [], [])
        suppressed.extend(notes)
        bad.extend(failed)
        for words in prose_runs(path, hunks):
            for i in range(len(words) - SHINGLE + 1):
                shingle = tuple(words[i:i + SHINGLE])
                # Exempted per SHINGLE, so the marked passage stops matching while everything else
                # this change added to the same file is still compared.
                if shingle not in exempt:
                    where[shingle].add(path)

    # One entry per set of files, not per shingle: a restated paragraph produces dozens of
    # overlapping shingles and printing each would bury the finding.
    by_group: dict[tuple[str, ...], list[tuple[str, ...]]] = collections.defaultdict(list)
    for shingle, files in where.items():
        if len(files) > 1:
            by_group[tuple(sorted(files))].append(shingle)
    return by_group, sorted(suppressed), sorted(bad)


def violations(by_file: dict[str, list[list[str]]], at=None) -> list[str]:
    by_group, _, bad = groups(by_file, at)

    # A restated passage spanning four files also produces a group for every pair and triple within
    # it, and collapsing those turns two dozen entries back into the handful a person has to fix.
    # Collapse only when the smaller group's shingles are also the larger's: two unrelated passages
    # that happen to nest would otherwise leave one of them unprinted and undiscoverable.
    maximal = [f for f in by_group
               if not any(other != f and set(f) < set(other)
                          and set(by_group[f]) <= set(by_group[other])
                          for other in by_group)]

    # A marker that announces an exemption and does not deliver one fails the run rather than
    # printing among the notes. It is an author error with a one-line fix, and the alternative is a
    # gate whose own output has to be read to learn that nothing was exempted -- which is exactly
    # how `audit-completeness` once shipped a false 16/16 while exiting 1.
    problems = [f"  {note}" for note in bad]
    for files in sorted(maximal):
        shingles = by_group[files]
        sample = " ".join(sorted(shingles)[0])
        problems.append(
            f"  the same wording was added to {len(files)} files:\n"
            + "\n".join(f"      {p}" for p in files)
            + f"\n      e.g. \"{sample}\"\n"
            + "      Keep it in one; link from the rest. A deliberate second copy needs\n"
            + "      `record-once-ok: #<issue> <canonical path>` in a comment beside that copy\n"
            + "      (in markdown: `<!-- record-once-ok: ... -->` alone on its own line -- not\n"
            + "      inside a list item, nothing after the closer -- and no blank line between\n"
            + "      it and the passage) -- which exempts that passage only, holds for later\n"
            + "      changes too, and is reported.")
    return problems


def excluded_from_comparison(path: str) -> str | None:
    """main()'s population filters, extracted so the selfcheck can hold them still — they run
    OUTSIDE the violations() path every other arm exercises, so a typo'd prefix here would
    otherwise be invisible. Returns the reason a changed file is not compared, or None when it is:
    "changelog" (#1367, release-please transcribes one commit line into every affected package's
    changelog — mechanical duplication whose canonical home is the commit) or "restored-decision"
    (#1431, docs/decisions/ holds records restored VERBATIM from the pre-reset tree under an owner
    ruling that restored history is not edited, so their shared historical boilerplate is not
    actionable). Filtered in main(), not added_lines_by_file, so --prove's historical
    re-derivation sees exactly what it was pinned against."""
    if generated_changelog(Path(path).name):
        return "changelog"
    if path.replace("\\", "/").startswith("docs/decisions/"):
        return "restored-decision"
    return None


def main(argv: list[str]) -> int:
    if len(argv) > 1 and argv[1] == "--prove":
        ok, detail = prove(PROVEN_SHA, PROVEN_GROUPS)
        if not ok:
            print("!! the checker no longer finds what it was built to find.", file=sys.stderr)
            for line in detail:
                print(f"   {line}", file=sys.stderr)
            print("   Adjudicate each line before repinning: a passage that stopped being found is\n"
                  "   a regression, and one newly found has to be a real restatement.",
                  file=sys.stderr)
            return 1
        print(f"record-once --prove: {detail[0]}")
        print(" OK still fires on real historical data, not only on its fixtures")
        return 0

    base = argv[1] if len(argv) > 1 else "origin/main"
    try:
        by_file = added_lines_by_file(base)
    except subprocess.CalledProcessError as exc:
        # Fail closed and say which half is missing: a shallow clone cannot see the base, and a
        # checker that silently passes on an unreadable diff is the thing this file exists to stop.
        print(f"!! cannot diff against '{base}' -- {exc.stderr.strip()}", file=sys.stderr)
        print("   CI needs actions/checkout with fetch-depth: 0 for this to work.", file=sys.stderr)
        return 1

    # Population filters — the why lives on excluded_from_comparison, which the selfcheck pins.
    skipped = sorted(p for p in by_file if excluded_from_comparison(p) == "changelog")
    for p in skipped:
        del by_file[p]
    if skipped:
        print(f" -- generated changelog(s), not compared (#1367): {', '.join(skipped)}")

    restored = sorted(p for p in by_file if excluded_from_comparison(p) == "restored-decision")
    for p in restored:
        del by_file[p]
    if restored:
        print(f" -- restored decision records, not compared (#1431): {len(restored)} file(s)")

    print(f"record-once: {len(by_file)} changed file(s) against {base}")
    if not by_file:
        # An empty population passing looks exactly like a real pass, which is the failure this
        # tool's neighbours exist to prevent. Say which one it was. On a push to `main`,
        # `origin/main...HEAD` is empty and only `--prove` carries the job.
        print(" -- nothing to compare: no file differs from the base")
        return 0

    at_head = lambda path: file_at(path, "HEAD")  # noqa: E731
    _, suppressed, _ = groups(by_file, at_head)
    for note in suppressed:
        print(f" -- exempted by `record-once-ok`, not compared: {note}")

    problems = violations(by_file, at_head)
    if not problems:
        print(" OK no wording was added to more than one file")
        return 0

    print(f" !! {len(problems)} problem(s): shared added wording, or a marker that exempts "
          "nothing\n", file=sys.stderr)
    for p in problems:
        print(p, file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
