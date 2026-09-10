// Executable tests for the #1912 conductor panel in tools/fleet-glass/glass.html -- the pure string
// builders that turn the projection's `queue` section into the table under the fleet row.
//
// WHY IT EXTRACTS RATHER THAN IMPORTS. glass.html is one self-contained file on purpose: it is
// published as a Claude.ai artifact and embedded verbatim into Baton.Cli (#1946), so there is no
// module for a test to import. The alternative -- a second copy of these functions in a .mjs the page
// does not use -- is the drift `record-once` exists to stop, and it would go green while the page
// itself was broken. So this reads the shipped bytes, slices the marked block, and evaluates THAT.
//
// Run: `node tools/fleet-glass/glass.selftest.mjs` (pixi task: fleet-glass-selftest).

import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const html = readFileSync(join(here, "glass.html"), "utf8");

const BEGIN = ">>> QUEUE-PANEL-BEGIN";
const END = "<<< QUEUE-PANEL-END";

// The extraction fails LOUD, never vacuous. A renamed marker or a gutted block would otherwise leave
// this file passing while testing nothing at all -- which is the exact failure mode a text-extraction
// harness invites and the only one it cannot detect after the fact.
const beginAt = html.indexOf(BEGIN);
const endAt = html.indexOf(END);
if (beginAt < 0 || endAt < 0 || endAt < beginAt) {
  console.error(`glass.selftest.mjs: FAIL -- could not find the ${BEGIN} / ${END} markers in glass.html.`);
  console.error("  If the panel moved, move the markers with it; if it was deleted, delete this file too.");
  process.exit(1);
}
const source = html.slice(html.indexOf("\n", beginAt) + 1, endAt).replace(/^\s*\/\/.*$/gm, "");
const REQUIRED = ["queueSlotsLineHtml", "queueLanesTableHtml", "queuePendingTableHtml", "queuePrTableHtml", "queueBoardHtml"];
const missing = REQUIRED.filter(fn => !source.includes(`function ${fn}`));
if (missing.length) {
  console.error(`glass.selftest.mjs: FAIL -- the marked block no longer defines: ${missing.join(", ")}`);
  process.exit(1);
}

// The two page-level helpers the block calls, SLICED from glass.html for the same reason the panel
// itself is (see the header): a hand copy is a second copy, and this one is load-bearing -- the
// panel's freshness clauses are built on `age` returning the literal word "just now" and null, so a
// re-typed shim would keep asserting against itself after the page reworded either.
function sliceOne(pattern, what) {
  const found = [...html.matchAll(pattern)];
  if (found.length !== 1) {
    console.error(`glass.selftest.mjs: FAIL -- expected exactly one ${what} in glass.html, found ${found.length}.`);
    console.error("  These are sliced, never copied; if the page reshaped one, update the pattern here.");
    process.exit(1);
  }
  return found[0][0];
}

const escSource = sliceOne(/^const esc = \(s\) => .*;$/gm, "definition of `esc`");

// `age` is the one thing that cannot be taken verbatim: it reads the wall clock, and a test that
// moved with it would assert nothing. Exactly ONE substitution, and the count is checked first -- a
// page that grew a second Date.now() must not quietly leave one of them live.
const ageSourceRaw = sliceOne(/^function age\(iso\)\{[\s\S]*?\n\}$/gm, "definition of `age`");
const clockReads = (ageSourceRaw.match(/Date\.now\(\)/g) || []).length;
if (clockReads !== 1) {
  console.error(`glass.selftest.mjs: FAIL -- glass.html's age() reads Date.now() ${clockReads} time(s); this harness substitutes exactly one.`);
  process.exit(1);
}
const NOW = Date.parse("2026-09-07T12:00:00Z");
const ageSource = ageSourceRaw.replace("Date.now()", "NOW");

const { esc, age } = new Function("NOW", `${escSource}\n${ageSource}\nreturn { esc, age };`)(NOW);

const panel = new Function("esc", "age", `${source}\nreturn { ${REQUIRED.join(", ")} };`)(esc, age);
const { queueSlotsLineHtml, queuePendingTableHtml, queuePrTableHtml, queueLanesTableHtml, queueBoardHtml } = panel;

const failures = [];
function check(name, cond) {
  if (!cond) failures.push(name);
}

// -- no board is THREE facts, and each gets its own word (#1912 fix round) --
// FleetProjectionWriter.BuildQueueSectionAsync's remarks are the register for which state produces
// which reason; these assert the page tells them apart instead of rendering one blank space.
check("a machine that has never used the queue renders NOTHING -- a note about a feature it does not use would be noise on every tick",
      queueBoardHtml(undefined, "no-queue-file") === "");
check("a section the daemon could not build this tick renders the daemon's OWN reason, not blank space",
      queueBoardHtml(undefined, "The queue file is not readable as a queue.").includes("The queue file is not readable as a queue."));
check("... labelled as this tick's failure rather than as an empty queue",
      queueBoardHtml(undefined, "boom").includes("Conductor rows unavailable this tick"));
check("the mailbox delivery -- neither key, because pusher.py composes key by key -- says the rows are daemon-page-only",
      queueBoardHtml(undefined).includes("this delivery does not carry them"));
check("a null queue section with no reason is the mailbox case too",
      queueBoardHtml(null).includes("this delivery does not carry them"));
check("(control) the three absences render three DIFFERENT things -- otherwise the split says nothing",
      new Set([queueBoardHtml(undefined, "no-queue-file"),
               queueBoardHtml(undefined, "boom"),
               queueBoardHtml(undefined)]).size === 3);
check("a daemon reason carrying markup is escaped -- an exception message is not trusted HTML",
      !queueBoardHtml(undefined, "<img src=x onerror=1>").includes("<img")
      && queueBoardHtml(undefined, "<img src=x onerror=1>").includes("&lt;img"));
check("(control) a present-but-empty queue section DOES render a board -- 'no queue file' and 'an empty queue' are different facts",
      queueBoardHtml({ slots: { cap: 4, live: 0, floorGb: 2, nightBand: false, lanes: [] }, pending: [], pullRequests: [] }).includes("queueboard-head"));

// -- weighted slots --
{
  const q = { slots: { cap: 4, live: 3.5, floorGb: 1.2, freeGb: 6.4, nightBand: true, lanes: [] } };
  const out = queueSlotsLineHtml(q);
  check("the slot line shows live / cap", out.includes("3.5 / 4 weighted slots"));
  check("the slot line shows free memory against the floor", out.includes("6.4 GiB free, floor 1.2 GiB"));
  check("the slot line names which floor band is in force", out.includes("night band"));
  check("the slot line says a review lane starts even at the cap -- the cap is not a promise nothing else can start",
        out.includes("review lanes weigh 0 and start even at the cap"));
  check("(control) a day-band projection says day band, not night",
        queueSlotsLineHtml({ slots: { ...q.slots, nightBand: false } }).includes("day band"));
}
check("an unmeasured free-memory reading says so rather than printing a stand-in number",
      queueSlotsLineHtml({ slots: { cap: 4, live: 0, floorGb: 2, nightBand: false } }).includes("memory unmeasured"));
check("a held queue says so on the slot line",
      queueSlotsLineHtml({ held: true, slots: { cap: 4, live: 0, floorGb: 2, nightBand: false } }).includes("QUEUE HELD"));
check("(control) an unheld queue does not",
      !queueSlotsLineHtml({ held: false, slots: { cap: 4, live: 0, floorGb: 2, nightBand: false } }).includes("QUEUE HELD"));

// -- live lanes, rendered with whatever weight the caller passes -- the renderer displays any
// number it's given; this is NOT an assertion about what QueueWeights.For actually returns today
// (every mutating lane weighs 1.0 on any adapter, review weighs 0 -- see spec/baton.md) --
{
  const out = queueLanesTableHtml({ slots: { lanes: [
    { room: "/r/a", label: "1912-lane", role: "implement", adapter: "claude", weight: 1 },
    { room: "/r/b", label: "1930-lane", role: "implement", adapter: "codex", weight: 0.5 },
    { room: "/r/c", label: "review-x", role: "review", adapter: "claude", weight: 0 },
  ] } });
  check("the renderer displays each lane's own weight verbatim, including a fractional one (1 / 0.5 / 0)",
        out.includes(">1</td>") && out.includes(">0.5</td>") && out.includes(">0</td>"));
  check("a lane whose bindings were unreadable says so rather than rendering blank",
        queueLanesTableHtml({ slots: { lanes: [{ room: "/r/x", weight: 1 }] } }).includes("role unknown"));
  check("(control) no live lane renders an explicit empty line, not a headerless table",
        queueLanesTableHtml({ slots: { lanes: [] } }).includes("No lane is running."));
}

// -- the dispatch queue: stage, round, and the reason each item is waiting --
{
  const pending = [
    { tag: "a", stage: "implement", round: 0, issue: 1900, role: "implement", reason: "next", isNext: true, external: false, halted: false },
    { tag: "b", stage: "review", round: 2, issue: 1901, role: "review", reason: "behind", isNext: false, external: false, halted: false },
    { tag: "c", stage: "fix", round: 3, role: "implement", reason: "memory", isNext: false, external: false, halted: false },
    { tag: "d", stage: "re-review", round: 4, role: "review", reason: "runway-held", isNext: false, external: false, halted: false },
    { tag: "e", stage: "continue", round: 1, role: "implement", reason: "brief-missing", isNext: false, external: false, halted: false },
    { tag: "f", stage: "implement", round: 4, role: "implement", reason: "halted", isNext: false, external: false, halted: true },
    { tag: "g", stage: null, round: 0, role: "implement", reason: "slots", isNext: false, external: false, halted: false },
  ];
  const out = queuePendingTableHtml({ pending });
  for (const p of pending) check(`the pending table renders item '${p.tag}'`, out.includes(`>${p.tag}</td>`));
  for (const r of ["next", "behind", "memory", "runway-held", "brief-missing", "halted", "slots"]) {
    check(`the pending table renders the '${r}' wait reason`, out.includes(r));
  }
  check("every stage word reaches the table",
        ["implement", "review", "fix", "re-review", "continue"].every(s => out.includes(`>${s}</td>`)));
  check("a stage-less dispatch request falls back to its role rather than rendering an empty stage cell",
        out.includes(">implement</td>"));
  check("the round is rendered per row", out.includes(">4</td>") && out.includes(">0</td>"));
  check("the NEXT item is marked as such -- position alone must not imply it, since twins are reordered",
        out.includes('class="q-next"'));
  check("a halted item is marked distinctly from the next one", out.includes('class="q-halt"'));
  check("(control) exactly one row carries the q-next mark", (out.match(/q-next/g) || []).length === 1);
  check("(control) nothing queued renders an explicit empty line",
        queuePendingTableHtml({ pending: [] }).includes("Nothing queued."));
}

// -- A/B twin pairs: adjacent, with the arm label, and nothing else special --
{
  const out = queuePendingTableHtml({ pending: [
    { tag: "1530-opus", stage: "implement", round: 0, issue: 1530, role: "implement", reason: "next", isNext: true, arm: "claude opus", twinIssue: 1530 },
    { tag: "1530-sonnet", stage: "implement", round: 0, issue: 1530, role: "implement", reason: "behind", isNext: false, arm: "claude sonnet", twinIssue: 1530 },
    { tag: "other", stage: "implement", round: 0, issue: 1600, role: "implement", reason: "behind", isNext: false },
  ] });
  check("both arms carry their arm label", out.includes("claude opus") && out.includes("claude sonnet"));
  check("both twin rows are marked as a pair", (out.match(/tr class="twin"/g) || []).length === 2);
  // `<tr><td`, not `<tr>`: the header row is a bare `<tr>` too, so counting those would pass at two
  // and would keep passing if the twin mark were dropped from one arm.
  check("(control) a non-twin row is NOT marked -- otherwise the mark says nothing",
        (out.match(/<tr><td/g) || []).length === 1);
  const opusAt = out.indexOf("1530-opus");
  const sonnetAt = out.indexOf("1530-sonnet");
  const otherAt = out.indexOf(">other<");
  check("the two arms render adjacent, ahead of the unrelated item", opusAt < sonnetAt && sonnetAt < otherAt);
  check("an item with no arm axes renders a dash rather than a label repeating its tag", out.includes(">—</td>"));
}

// -- PR stages --
{
  const out = queuePrTableHtml({ pullRequests: [
    { tag: "a", pr: 2028, stage: "review", round: 1, verdict: "block", checks: "failing", checksObservedAt: "2026-09-07T11:00:00Z", halted: false },
    { tag: "b", pr: 2035, stage: "ready", round: 3, verdict: "approve", checks: "passing", checksObservedAt: "2026-09-07T11:59:30Z", halted: false },
    { tag: "c", pr: 2040, stage: "fix", round: 2, halted: true },
    { tag: "cancelled", pr: 2173, stage: "review", round: 2, state: "Cancelled", halted: false },
  ] });
  check("the PR number is rendered", out.includes("#2028") && out.includes("#2035"));
  check("every stage this table can carry renders", out.includes(">review</td>") && out.includes(">ready</td>"));
  check("the last verdict decision is rendered", out.includes("block") && out.includes("approve"));
  check("a PR with no verdict yet says so rather than rendering blank", out.includes("no verdict"));
  check("the checks word carries its OWN age -- the advancer reads only settled lanes, so it is never this instant's",
        out.includes("failing (1h ago)") && out.includes("passing (just now)"));
  check("(control) checks never observed says so, not 'passing'", out.includes("not observed"));
  check("a halted work item is marked on its PR row", out.includes("halted"));
  check("a cancelled work item is marked on its PR row", out.includes("review · cancelled"));
  check("(control) no PR renders an explicit empty line",
        queuePrTableHtml({ pullRequests: [] }).includes("No work item has a pull request open."));
}

// -- the standing-verdict age: a collapsed ledger row can be hours old and still current --
{
  const base = { slots: { cap: 4, live: 0, floorGb: 2, nightBand: false, lanes: [] }, pending: [], pullRequests: [] };
  check("an hours-old scheduling decision renders its age, never bare",
        queueBoardHtml({ ...base, lastDecisionAt: "2026-09-07T09:00:00Z" }).includes("last scheduling decision 3h ago"));
  check("(control) a ledger with no rows at all says so distinctly",
        queueBoardHtml(base).includes("no scheduling decision recorded yet"));
}

// -- escaping: every cell goes through esc, so a hand-edited queue file cannot inject markup --
{
  const out = queuePendingTableHtml({ pending: [
    { tag: "<img src=x onerror=1>", stage: "implement", round: 0, role: "implement", reason: "behind" },
  ] });
  check("a tag carrying markup is escaped, never rendered as HTML",
        !out.includes("<img") && out.includes("&lt;img"));
}

if (failures.length) {
  console.error(`glass.selftest.mjs: FAIL -- ${failures.length} check(s):`);
  for (const f of failures) console.error(`  !! ${f}`);
  process.exit(1);
}
console.log(`glass.selftest.mjs: pass`);
