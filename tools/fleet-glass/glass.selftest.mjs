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
const REQUIRED = ["queueSlotsLineHtml", "queueLanesTableHtml", "queuePendingTableHtml", "queuePrTableHtml", "queuePrRowsHtml", "queuePrHistoryHtml", "queueRetiredHistoryHtml", "queueBoardHtml", "streamStatusSummaryHtml", "streamEventReceiptHtml", "streamGroupedEventsHtml", "streamHistoryHtml", "streamHomeHtml", "streamQueueProjection"];
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

const vendorUsageRowSource = sliceOne(/^function vendorUsageRowHtml\(vendor, w, derived\)\{[\s\S]*?\n\}$/gm, "definition of `vendorUsageRowHtml`");
const vendorUsageSource = sliceOne(/^function vendorUsageHtml\(vendors\)\{[\s\S]*?\n\}$/gm, "definition of `vendorUsageHtml`");
const { vendorUsageRowHtml, vendorUsageHtml } = new Function("esc", "age", "$", `${vendorUsageRowSource}\n${vendorUsageSource}\nreturn { vendorUsageRowHtml, vendorUsageHtml };`)(
  esc,
  age,
  (id) => id === "vendorusage" ? vendorUsageSink : null);

const failures = [];
function check(name, cond) {
  if (!cond) failures.push(name);
}

const vendorUsageSink = { innerHTML: "" };
const explicitNullRow = vendorUsageRowHtml("codex", {
  name: "codex · unavailable (secondary)",
  rawLine: "null",
  windowKind: "secondary",
  windowDurationMins: null,
  percentUsed: null,
  resetsAt: null,
}, false);
check("an explicitly null vendor window renders unavailable with no invented usage or boundary",
      explicitNullRow.includes("usage unavailable") && explicitNullRow.includes("duration/reset unavailable"));

const malformedObjectRow = vendorUsageRowHtml("codex", {
  name: "codex · unavailable (secondary)",
  rawLine: '{"usedPercent":"not-a-number","windowDurationMins":"not-a-number"}',
  windowKind: "secondary",
  windowDurationMins: null,
  percentUsed: null,
  resetsAt: null,
}, false);
check("a malformed exposed object renders unknown/unreadable rather than explicit unavailable",
      malformedObjectRow.includes("unknown") && !malformedObjectRow.includes("usage unavailable"));

vendorUsageHtml([{
  adapter: "codex",
  harvestedAt: "2026-09-07T12:00:00Z",
  windows: [{
    name: "codex · 7d (primary)",
    rawLine: '{"usedPercent":17,"windowDurationMins":10080,"resetsAt":1778198400}',
    windowKind: "primary",
    windowDurationMins: 10080,
    percentUsed: 17,
    resetsAt: "2026-05-07T00:00:00Z",
  }],
  liveLanes: 0,
  source: "vendor",
}]);
check("a valid weekly-only account renders its vendor window without manufacturing a five-hour row",
      vendorUsageSink.innerHTML.includes("codex · 7d (primary)")
      && vendorUsageSink.innerHTML.includes("17% used")
      && (vendorUsageSink.innerHTML.match(/vendorusage-row/g) || []).length === 1
      && !vendorUsageSink.innerHTML.includes("5h"));

const panel = new Function("esc", "age", `${source}\nreturn { ${REQUIRED.join(", ")} };`)(esc, age);
const { queueSlotsLineHtml, queuePendingTableHtml, queuePrTableHtml, queuePrRowsHtml, queuePrHistoryHtml, queueRetiredHistoryHtml, queueLanesTableHtml, queueBoardHtml, streamStatusSummaryHtml, streamEventReceiptHtml, streamGroupedEventsHtml, streamHistoryHtml, streamHomeHtml, streamQueueProjection } = panel;

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

{
  const retirement = { kind: "operator", at: new Date().toISOString(), reason: "operator completed recovery" };
  const out = queueRetiredHistoryHtml({ retiredHistory: [{ tag: "failed-no-pr", stage: "fix", state: "Failed", retirement }] });
  check("a retired lifecycle lane without a PR remains in Fleet Glass history with its evidence",
        out.includes("failed-no-pr") && out.includes("operator") && out.includes("operator completed recovery"));
  check("retirement evidence also accompanies a retained PR lane",
        queuePrRowsHtml([{ pr: 2288, lanes: [{ tag: "merged", stage: "ready", state: "Done", round: 0, retirement }] }], "history")
          .includes("operator completed recovery"));
}

// -- weighted slots --
{
  const q = { slots: { cap: 4, live: 3.5, floorGb: 1.2, freeGb: 6.4, nightBand: true, lanes: [] } };
  const out = queueSlotsLineHtml(q);
  check("the slot line shows live / cap", out.includes("3.5 / 4 weighted slots"));
  check("the slot line shows free memory against the floor", out.includes("6.4 GiB free, floor 1.2 GiB"));
  check("the slot line names which floor band is in force", out.includes("night band"));
  check("the slot line distinguishes weightless reviews from the separate review cap",
        out.includes("review lanes weigh 0 against mutating slots, but respect the live-review cap"));
  check("(control) a day-band projection says day band, not night",
        queueSlotsLineHtml({ slots: { ...q.slots, nightBand: false } }).includes("day band"));
}
{
  const base = { slots: { cap: 4, live: 0, floorGb: 2, nightBand: false, lanes: [] }, pending: [], pullRequests: [] };
  const recorded = queueBoardHtml({ ...base,
    lifecycleSlots: { active: 4, activeCap: 4, prePr: 2, prePrCap: 2, liveReviews: 1,
      reviewCap: 2, consumingTags: ["ready-old", "halted-old", "live"] },
    flowDecision: { tag: "fix-existing", priorityBand: "repair", passedNewWorkHead: true,
      newWorkHeadCap: "lifecycle-cap" } });
  check("the three WIP caps and old ready/halted occupants render together from recorded fields",
        recorded.includes("4 / 4 active lifecycles") && recorded.includes("2 / 2 pre-PR")
        && recorded.includes("1 / 2 live reviews") && recorded.includes("ready-old, halted-old, live"));
  check("recorded selection shows band, pass-through, and holding cap",
        recorded.includes("fix-existing (repair)") && recorded.includes("passed new-work head")
        && recorded.includes("new-work head held by lifecycle-cap"));
  const unknown = queueBoardHtml(base);
  check("missing decision stays unknown rather than showing fabricated zero WIP",
        unknown.includes("WIP counts unrecorded") && !unknown.includes("0 / 4 active lifecycles"));
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
    { repository: "github.com/acme/one", pr: 2028, prState: "open", freshness: "current", observedAt: "2026-09-07T11:59:30Z", attemptedAt: "2026-09-07T11:59:30Z", headSha: "aaaaaaaa11111111", deployment: "not-recorded", lanes: [
      { tag: "a", stage: "review", state: "Done", round: 1, verdict: "block", checks: "failing", checksObservedAt: "2026-09-07T11:00:00Z", checksHeadSha: "bbbbbbbb22222222", twinIssue: 1530 },
      { tag: "cancelled", stage: "review", state: "Cancelled", round: 2 },
      { tag: "halted", stage: "fix", state: "Failed", round: 2, halted: true, twinIssue: 1600 },
      { tag: "waiting", stage: "ready", state: "Queued", round: 2, requiredCheckEvidenceWait: { headSha: "cccccccc33333333", firstUnreadableAt: "2026-09-07T11:00:00Z", latestObservationAt: "2026-09-07T11:59:30Z", attemptCount: 2, reason: "required checks have not materialized" } },
    ] },
    { repository: "github.com/acme/two", pr: 2035, freshness: "unknown", attemptedAt: "2026-09-07T11:59:30Z", observationError: "repository identity invalid", deployment: "not-recorded", lanes: [
      { tag: "b", stage: "ready", state: "Queued", round: 3, verdict: "approve", checks: "passing", checksObservedAt: "2026-09-07T11:59:30Z" },
    ] },
  ] });
  check("the PR number is rendered", out.includes("#2028") && out.includes("#2035"));
  check("one qualified PR row retains multiple lane links", (out.match(/github.com\/acme\/one #2028/g) || []).length === 1 && out.includes(">a</a>") === false && out.includes("a · review"));
  check("the last verdict decision is rendered", out.includes("block") && out.includes("approve"));
  check("a PR with no verdict yet says so rather than rendering blank", out.includes("no verdict"));
  check("historical checks name their commit, while legacy checks say commit unknown",
        out.includes("failing (1h ago; bbbbbbbb)") && out.includes("passing (just now; commit unknown)"));
  check("(control) checks never observed says so, not 'passing'", out.includes("checks not observed"));
  check("a required-check-evidence wait is operator-visible with its bounded-observation evidence",
        out.includes("waiting for check evidence (2 observations; first observed 1h ago; last observed just now; cccccccc; required checks have not materialized)"));
  check("a halted work item remains marked on its grouped PR row", out.includes("halted lane"));
  check("a cancelled lane on a confirmed-open PR is labelled explicitly", out.includes("cancelled lane · open PR"));
  check("grouped current lanes retain their own twin markers, including different twins",
        out.includes("twin #1530") && out.includes("twin #1600")
          && (out.match(/q-pr-lane twin/g) || []).length === 2);
  check("a grouped non-twin lane has no twin marker",
        /<div class="q-pr-lane">cancelled ·/.test(out));
  check("invalidated identity remains visible follow-up as unknown",
        out.includes("unknown") && out.includes("lookup: repository identity invalid"));
  check("every observation exposes when it was last checked",
        out.includes("last checked just now") && out.includes("last confirmed just now"));
  check("deployment absence says not recorded rather than none", out.includes(">not recorded</td>"));
  check("(control) no PR renders an explicit empty line",
        queuePrTableHtml({ pullRequests: [] }).includes("No open or unknown pull request needs follow-up."));

  const history = queuePrHistoryHtml({ pullRequestHistory: [
    { repository: "github.com/acme/one", pr: 2192, prState: "merged", freshness: "current", observedAt: "2026-09-07T11:59:30Z", attemptedAt: "2026-09-07T11:59:30Z", deployment: "not-recorded", lanes: [
      { tag: "2192-lane", stage: "review", state: "Cancelled", round: 1, checks: "failing", checksObservedAt: "2026-09-07T10:00:00Z", twinIssue: 1700 },
    ] },
    { repository: "github.com/acme/two", pr: 2035, prState: "closed", freshness: "stale", observedAt: "2026-09-07T09:00:00Z", attemptedAt: "2026-09-07T11:59:30Z", observationError: "gh pr view exited 1", deployment: "not-recorded", lanes: [
      { tag: "b", stage: "ready", state: "Queued", round: 3, verdict: "approve" },
    ] },
  ] });
  check("a fresh merged PR is distinct collapsed history with cancellation retained", history.includes("Completed PR history") && history.includes("merged") && history.includes("cancelled lane"));
  check("completed history retains its per-lane twin marker", history.includes("twin #1700"));
  check("stale trustworthy terminal history is prominent and honestly aged",
        history.includes("last known closed · stale") && history.includes("last checked just now")
          && history.includes("last confirmed 3h ago") && history.includes("lookup: gh pr view exited 1"));
  check("(control) no completed PR produces no history section", queuePrHistoryHtml({ pullRequestHistory: [] }) === "");
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

// -- #2241 / #2075: mobile-first stream home answers the 4 questions without opening diagnostics --
{
  // 1. Idle fleet answers
  const idleOut = streamStatusSummaryHtml(null, []);
  check("idle stream summary answers what is moving", idleOut.includes("No work moving · fleet is idle"));
  check("idle stream summary answers what is blocked or stale", idleOut.includes("No worker or PR blockage recorded"));
  check("idle stream summary does not invent all-clear without obligation evidence", idleOut.includes("Conductor request state unknown"));
  check("idle stream summary answers quota status", idleOut.includes("Quota: unmeasured"));

  // 2. Active, blocked, operator-needed, and quota facts
  const snap = {
    rooms: [
      { path: "rooms/dispatch-implement-1234", state: "Running", role: "implement", adapter: "claude" },
      { path: "rooms/dispatch-review-5678", state: "Stalled" },
      { path: "rooms/dispatch-unknown-9999", state: "Indeterminate" },
    ],
    queue: {
      held: true,
      slots: { lanes: [{ label: "lane-beta", role: "implement", adapter: "codex" }] },
      pending: [{ tag: "lane-gamma", stage: "implement", round: 1, role: "implement", reason: "runway-held" }],
      pullRequests: [{ pr: 2241, freshness: "stale" }],
    },
    projection: { stale: true },
    vendors: [{
      adapter: "claude",
      windows: [{ name: "claude · 5h (primary)", windowKind: "primary", percentUsed: 30, resetsAt: "2026-09-07T15:00:00Z" }],
    }],
  };
  const activeEvents = [
    { id: 10, kind: "attemptRefused", attemptId: "att-ref", outcomeDetail: "missing tool capability" },
  ];
  const fullOut = streamStatusSummaryHtml(snap, activeEvents);
  check("active stream summary answers moving with running room and lane names",
        fullOut.includes("moving") && fullOut.includes("dispatch-implement-1234") && fullOut.includes("lane-beta"));
  check("active stream summary answers blocked/stale with stalled, queue runway-held, stale PR, stale projection, and refused attempt",
        fullOut.includes("blocked/stale") && fullOut.includes("stalled room") && fullOut.includes("lane-gamma (runway-held)")
        && fullOut.includes("PR #2241") && fullOut.includes("projection is stale") && fullOut.includes("attempt refused"));
  check("active stream summary answers what needs operator with indeterminate room and held queue",
        fullOut.includes("need(s) operator") && fullOut.includes("indeterminate room") && fullOut.includes("queue is held"));
  check("stream summary progressively discloses quota details without column squeeze",
        fullOut.includes("70% rem (30% used)") && fullOut.includes("<details class=\"quota-disclosure\"><summary>Quota details</summary>"));
}

// -- #2241 action receipts: compact current action and last meaningful result --
{
  const receiptStarted = streamEventReceiptHtml({
    id: 1, kind: "attemptStarted", attemptId: "att-42", declaredRole: "implement", vendor: "claude", at: "2026-09-07T11:58:00Z",
  });
  check("attemptStarted receipt renders what, target, and status badge",
        receiptStarted.includes("Attempt started: att-42 (implement) on claude")
        && receiptStarted.includes("recorded in events.jsonl")
        && receiptStarted.includes("PENDING"));

  const receiptRevision = streamEventReceiptHtml({
    id: 2, kind: "revisionProduced", revisionId: "abcdef123456", revisionKind: "code", at: "2026-09-07T11:59:00Z",
  });
  check("revisionProduced receipt renders revision identity and git target",
        receiptRevision.includes("Revision produced: abcdef12 (code)") && receiptRevision.includes("recorded in git commit"));

  const receiptVerdict = streamEventReceiptHtml({
    id: 3, kind: "reviewVerdictObserved", reviewVerdict: "block", reviewEvidence: "missing check", at: "2026-09-07T11:59:30Z",
  });
  check("reviewVerdictObserved receipt renders verdict, failure status, and evidence",
        receiptVerdict.includes("Review verdict: block") && receiptVerdict.includes("FAILURE") && receiptVerdict.includes("[missing check]"));

  check("action receipts never output private reasoning or file:/// links",
        !receiptStarted.includes("file:///") && !receiptRevision.includes("file:///") && !receiptVerdict.includes("file:///"));

  const actualSuccessReceipt = streamEventReceiptHtml({
    id: 4, kind: "attemptSettled", attemptId: "att-real", outcome: "Succeeded", elapsedMilliseconds: 1000,
  });
  check("producer-shaped title-case succeeded outcomes render as success",
        actualSuccessReceipt.includes(">SUCCESS<") && actualSuccessReceipt.includes("receipt-status-success"));

  const actualRefusalReceipt = streamEventReceiptHtml({
    id: 5, kind: "attemptRefused", attemptId: "att-held", outcome: "runway-held",
  });
  check("attempt refusal renders the producer's outcome field when no detail is present",
        actualRefusalReceipt.includes("Attempt refused: runway-held"));

  const actualCheckReceipt = streamEventReceiptHtml({
    id: 6, kind: "checkObserved", checkName: "gates", checkStatus: "COMPLETED", checkConclusion: "SUCCESS",
  });
  check("producer-shaped uppercase check conclusions render as success",
        actualCheckReceipt.includes(">SUCCESS<") && actualCheckReceipt.includes("receipt-status-success"));

  const writeRefusedReceipt = streamEventReceiptHtml({
    id: 7, kind: "glassWriteRefused", outcomeDetail: "route=/queue/hold; login=<redacted>",
    at: "2026-09-12T03:00:00Z",
  });
  check("glass write refusal renders route, target, and refused badge without identity leak",
        writeRefusedReceipt.includes("Write refused: route=/queue/hold; login=&lt;redacted&gt;")
        && writeRefusedReceipt.includes("recorded in events.jsonl")
        && writeRefusedReceipt.includes("REFUSED")
        && !writeRefusedReceipt.includes("operator@example.com"));
}

// -- #2241 identity grouping, unknown fields, current vs retained history (#2200), and deduplication --
{
  const events = [
    // Duplicate monotonic id 1
    { id: 1, workId: "w-101", attemptId: "att-101", kind: "attemptStarted", declaredRole: "implement", vendor: "claude" },
    { id: 1, workId: "w-101", attemptId: "att-101", kind: "attemptStarted", declaredRole: "implement", vendor: "claude" },
    { id: 2, workId: "w-101", attemptId: "att-101", kind: "attemptProgressed" },
    { id: 3, workId: "w-101", attemptId: "att-101", kind: "revisionProduced", revisionId: "1234567890ab", revisionKind: "code" },

    // Retained cancelled lifecycle (#2200)
    { id: 4, workId: "w-102", attemptId: "att-102", pullRequestId: 2192, kind: "attemptStarted", declaredRole: "review" },
    { id: 5, workId: "w-102", attemptId: "att-102", pullRequestId: 2192, kind: "attemptSettled", outcome: "cancelled", outcomeDetail: "cancelled by conductor" },

    // Retained succeeded lifecycle
    { id: 6, workId: "w-103", attemptId: "att-103", issueId: 2075, kind: "attemptStarted", declaredRole: "implement" },
    { id: 7, workId: "w-103", attemptId: "att-103", issueId: 2075, kind: "attemptSettled", outcome: "succeeded", elapsedMilliseconds: 120000 },
  ];

  const streamHtml = streamGroupedEventsHtml(events);
  check("grouped stream groups related events by identity without inventing lineage",
        streamHtml.includes("work: w-101") && streamHtml.includes("attempt: att-101") && streamHtml.includes("12345678 (code)"));
  check("unknown fields remain visibly unknown",
        streamHtml.includes("issue: unknown") && streamHtml.includes("PR: unknown"));
  check("current active work is rendered under Current Work",
        streamHtml.includes("Current Work (1)") && streamHtml.includes("ACTIVE"));
  check("retained cancelled lifecycle (#2200) is distinct in Retained History and never active",
        streamHtml.includes("Retained History · #2200 (2)")
        && streamHtml.includes("CANCELLED (RETAINED)")
        && !streamHtml.includes("Current Work (2)"));
  check("settled lifecycle renders duration and outcome in action receipt card",
        streamHtml.includes("Settled succeeded in 2m") && streamHtml.includes("SUCCEEDED"));

  // History tab only view
  const historyHtml = streamHistoryHtml(events);
  check("streamHistoryHtml renders retained history without active cards",
        historyHtml.includes("Retained Lifecycle History · #2200 (2)")
        && !historyHtml.includes("Current Work")
        && historyHtml.includes("CANCELLED (RETAINED)"));

  // Stream home combines both
  const homeHtml = streamHomeHtml(null, events);
  check("streamHomeHtml combines status summary grid and grouped event cards",
        homeHtml.includes("stream-summary-grid") && homeHtml.includes("stream-group-card"));

  // Reconnect / replay deduplication: duplicated event list produces identical render
  const replayedEvents = [...events, ...events];
  const replayedHomeHtml = streamHomeHtml(null, replayedEvents);
  check("reconnect/replay duplicate events produce identical stream html output",
        replayedHomeHtml === homeHtml);

  // Read-only boundary: no mutating controls, forms, or verbs in stream output
  check("stream HTML enforces read-only boundary with no form elements",
        !homeHtml.includes("<form") && !homeHtml.includes("type=\"submit\""));
  check("stream HTML contains no mutating action verbs",
        !homeHtml.includes("baton hold") && !homeHtml.includes("baton resume")
        && !homeHtml.includes("baton cancel") && !homeHtml.includes("baton wake")
        && !homeHtml.includes("baton merge") && !homeHtml.includes("baton decide"));

  // Tab navigation structure in glass.html contains all 6 tabs including Deliverables
  check("glass.html tabs contain Stream, Fleet, Queue, Quota, History, and Deliverables",
        html.includes("data-tab=\"stream\"") && html.includes("data-tab=\"fleet\"")
        && html.includes("data-tab=\"queue\"") && html.includes("data-tab=\"quota\"")
        && html.includes("data-tab=\"history\"") && html.includes("data-tab=\"inbox\""));

  const retriedWork = [
    { id: 20, workId: "same-work", attemptId: "attempt-one", kind: "attemptStarted" },
    { id: 21, workId: "same-work", attemptId: "attempt-one", kind: "attemptSettled", outcome: "Indeterminate" },
    { id: 22, workId: "same-work", attemptId: "attempt-two", parentAttemptId: "attempt-one", kind: "attemptStarted" },
  ];
  const retriedHtml = streamGroupedEventsHtml(retriedWork);
  check("retry attempts sharing one work id remain separate lifecycles",
        retriedHtml.includes("Current Work (1)")
        && retriedHtml.includes("Retained History · #2200 (1)")
        && retriedHtml.includes("attempt: attempt-one")
        && retriedHtml.includes("attempt: attempt-two")
        && retriedHtml.includes("INDETERMINATE (RETAINED)"));

  const teardownHtml = streamGroupedEventsHtml([
    { id: 23, workId: "teardown-work", attemptId: "teardown-attempt", kind: "attemptSettled", outcome: "FinishedDuringTeardown" },
  ]);
  check("the second succeeded-shaped terminal outcome renders as success",
        teardownHtml.includes("SUCCEEDED") && !teardownHtml.includes("ACTIVE"));

  const refusedAdmission = {
    id: 24, workId: "review-work", attemptId: "review-attempt", kind: "admissionDecided",
    admissionDecision: "refused", missingCapabilities: ["file-write", "network"],
  };
  const refusedAdmissionHtml = streamGroupedEventsHtml([refusedAdmission]);
  check("a producer-shaped refused admission is retained rather than shown as active forever",
        refusedAdmissionHtml.includes("Current Work (0)")
        && refusedAdmissionHtml.includes("REFUSED (RETAINED)")
        && refusedAdmissionHtml.includes("Admission refused: file-write, network"));
  const refusedSummary = streamStatusSummaryHtml(null, [refusedAdmission]);
  check("a refused admission appears in the blocked summary",
        refusedSummary.includes("admission refused: file-write, network"));

  // #2612: the live tail can contain only checks/PR receipts after a settled event rolled over.
  // Retirement is safe only when the queue supplies one exact task identity; issue/PR-only or
  // malformed/mismatched evidence must leave the tail visible as unknown active work.
  const retiredTask = {
    tag: "2583-implement", stage: "review", state: "Done", issue: 2583, pr: 2585,
    task: { id: "task-2583", repository: "github.com/philipreese/baton", issue: 2583 },
    retirement: { kind: "merged", at: "2026-09-07T10:30:00Z", reason: "PR merged" },
  };
  const retiredQueue = { retiredHistory: [retiredTask] };
  const truncatedRetiredTail = [
    { id: 101, workId: "task-2583", attemptId: "attempt-2583", issueId: 2583, pullRequestId: 2585,
      kind: "checkObserved", checkName: "gates", checkStatus: "COMPLETED", checkConclusion: "SUCCESS" },
    { id: 102, workId: "task-2583", attemptId: "attempt-2583", issueId: 2583, pullRequestId: 2585,
      kind: "checkObserved", checkName: "audit", checkStatus: "COMPLETED", checkConclusion: "SUCCESS" },
    { id: 103, kind: "conductorObligationPending", obligationId: "obligation-1", obligationRequestedAction: "Continue work" },
    { id: 104, kind: "conductorObligationBlocked", obligationId: "obligation-1", obligationReason: "awaiting operator" },
    { id: 105, kind: "conductorObligationSubmitted", obligationId: "obligation-1" },
    { id: 106, kind: "conductorObligationTransportAcknowledged", obligationId: "obligation-1" },
  ];
  const truthfulTail = streamGroupedEventsHtml(truncatedRetiredTail, retiredQueue);
  check("exact retired task evidence moves a truncated check/PR tail to retained history",
        truthfulTail.includes("Current Work (0)") && truthfulTail.includes("Retained History · #2200 (1)"));
  check("retained stream card shows queue retirement kind, time, and reason",
        truthfulTail.includes("MERGED") && truthfulTail.includes("Retirement evidence: merged")
        && truthfulTail.includes("PR merged"));
  check("conductor obligation receipts are visible but do not become worker activity",
        truthfulTail.includes("Conductor Request Receipts (1)")
        && truthfulTail.includes("REQUEST RECEIPT") && !truthfulTail.includes("Current Work (5)"));
  check("transport acknowledgment remains a receipt, not action completion",
        truthfulTail.includes("Transport acknowledged") && !truthfulTail.includes("Action observed"));

  const terminalRetiredAttempts = [
    { id: 112, workId: "task-2583", attemptId: "attempt-succeeded", issueId: 2583,
      kind: "attemptSettled", outcome: "Succeeded", outcomeDetail: "published", elapsedMilliseconds: 5000 },
    { id: 113, workId: "task-2583", attemptId: "attempt-failed", issueId: 2583,
      kind: "attemptSettled", outcome: "Failed", outcomeDetail: "compiler error", elapsedMilliseconds: 61000 },
    { id: 114, workId: "task-2583", attemptId: "attempt-cancelled", issueId: 2583,
      kind: "attemptSettled", outcome: "Cancelled", outcomeDetail: "operator requested", elapsedMilliseconds: 2000 },
    { id: 115, workId: "task-2583", attemptId: "attempt-indeterminate", issueId: 2583,
      kind: "attemptSettled", outcome: "Indeterminate", outcomeDetail: "tail ended", elapsedMilliseconds: 3000 },
    { id: 116, workId: "task-2583", attemptId: "attempt-refused", issueId: 2583,
      kind: "attemptRefused", outcome: "capacity", outcomeDetail: "runway held", elapsedMilliseconds: 4000 },
  ];
  const terminalRetirementHtml = streamGroupedEventsHtml(terminalRetiredAttempts, retiredQueue);
  check("terminal attempts keep their own outcomes when the overall task was later merged",
        terminalRetirementHtml.includes("SUCCEEDED") && terminalRetirementHtml.includes("FAILED (RETAINED)")
        && terminalRetirementHtml.includes("CANCELLED (RETAINED)")
        && terminalRetirementHtml.includes("INDETERMINATE (RETAINED)")
        && terminalRetirementHtml.includes("REFUSED (RETAINED)")
        && !terminalRetirementHtml.includes("MERGED (RETAINED)"));
  check("terminal attempt details and elapsed times survive later retirement",
        terminalRetirementHtml.includes("Outcome: failed") && terminalRetirementHtml.includes("compiler error")
        && terminalRetirementHtml.includes("Settled failed in 1m")
        && terminalRetirementHtml.includes("operator requested")
        && terminalRetirementHtml.includes("capacity")
        && terminalRetirementHtml.includes("runway held") && terminalRetirementHtml.includes("5s"));

  const laterAttempts = [
    ...truncatedRetiredTail.slice(0, 2),
    { id: 117, workId: "task-2583", attemptId: "attempt-known-later", issueId: 2583,
      kind: "attemptStarted", at: "2026-09-07T11:00:00Z" },
    { id: 118, workId: "task-2583", attemptId: "attempt-unknown-later", issueId: 2583,
      kind: "attemptProgressed", at: "2026-09-07T11:01:00Z" },
  ];
  const laterAttemptsHtml = streamGroupedEventsHtml(laterAttempts, retiredQueue);
  check("known and unknown later attempts remain current despite older retirement evidence",
        laterAttemptsHtml.includes("Current Work (2)") && laterAttemptsHtml.includes("attempt-known-later")
        && laterAttemptsHtml.includes("attempt-unknown-later"));

  const receiptHtml = streamGroupedEventsHtml([
    { id: 119, kind: "conductorObligationPending", obligationId: "obligation-receipt-1",
      obligationRequestedAction: "Continue work" },
    { id: 120, kind: "conductorObligationTransportAcknowledged", obligationId: "obligation-receipt-1" },
  ]);
  check("request receipt cards show the obligation identity and request fields without worker unknown chips",
        receiptHtml.includes("obligation: obligation-receipt-1")
        && receiptHtml.includes("requested: Continue work")
        && !receiptHtml.includes("work: unknown") && !receiptHtml.includes("attempt: unknown")
        && !receiptHtml.includes("issue: unknown") && !receiptHtml.includes("PR: unknown"));

  const tailOnly = truncatedRetiredTail.slice(0, 2);
  check("without the current queue projection the same tail stays explicitly active/unknown",
        streamGroupedEventsHtml(tailOnly).includes("Current Work (1)")
        && streamGroupedEventsHtml(tailOnly).includes("ACTIVE"));
  check("issue/PR-only identity cannot borrow retirement evidence",
        streamGroupedEventsHtml([{ id: 107, issueId: 2583, pullRequestId: 2585, kind: "checkObserved" }], retiredQueue)
          .includes("Current Work (1)"));
  check("mismatched task identity cannot borrow retirement evidence",
        streamGroupedEventsHtml([{ id: 108, workId: "task-other", issueId: 2583, kind: "checkObserved" }], retiredQueue)
          .includes("Current Work (1)"));
  check("mismatched issue evidence cannot borrow an exact task retirement",
        streamGroupedEventsHtml([{ id: 109, workId: "task-2583", issueId: 9999, kind: "checkObserved" }], retiredQueue)
          .includes("Current Work (1)"));
  check("a stale queue projection leaves the live tail explicitly active",
        streamHomeHtml({ queue: retiredQueue, projection: { stale: true }, conductorObligations: { available: true, rows: [] } }, tailOnly)
          .includes("Current Work (1)"));
  for(const [label, overrides] of [
    ["missing freshness evidence", { derived_at: undefined, projectionStaleAfterSeconds: undefined }],
    ["invalid freshness threshold", { derived_at: "2026-09-07T11:59:00Z", projectionStaleAfterSeconds: "90" }],
    ["invalid freshness timestamp", { derived_at: "not-a-timestamp", projectionStaleAfterSeconds: 90 }],
    ["future freshness timestamp", { derived_at: "2026-09-07T12:01:00Z", projectionStaleAfterSeconds: 90 }],
  ]){
    const projection = { queue: retiredQueue, conductorObligations: { available: true, rows: [] }, ...overrides };
    const stream = streamHomeHtml(projection, tailOnly);
    const history = streamHistoryHtml(tailOnly, streamQueueProjection(projection));
    check(`${label} keeps stream and history tails explicitly active`,
          stream.includes("Current Work (1)") && history.includes("No retained lifecycle history recorded yet."));
  }
  check("ambiguous duplicate retired task identities remain unknown",
        streamGroupedEventsHtml([{ id: 110, workId: "task-2583", kind: "checkObserved" }],
          { retiredHistory: [retiredTask, { ...retiredTask, tag: "duplicate" }] }).includes("Current Work (1)"));
  check("malformed retirement evidence remains unknown rather than inventing terminal status",
        streamGroupedEventsHtml([{ id: 111, workId: "task-2583", kind: "checkObserved" }],
          { retiredHistory: [{ ...retiredTask, task: null }] }).includes("Current Work (1)"));

  const integratedProjection = {
    queue: retiredQueue, derived_at: new Date(Date.now() - 60_000).toISOString(), projectionStaleAfterSeconds: 90,
    conductorObligations: { available: true, rows: [] },
  };
  const integratedHome = streamHomeHtml(integratedProjection, truncatedRetiredTail);
  const integratedHistory = streamHistoryHtml(truncatedRetiredTail, streamQueueProjection(integratedProjection));
  check("actual stream and history callers pass the current queue projection",
        integratedHome.includes("Current Work (0)") && integratedHistory.includes("Retained Lifecycle History · #2200 (1)")
        && html.includes("streamGroupedEventsHtml(fleetEvents, streamQueueProjection(lastGood))")
        && html.includes("streamHistoryHtml(fleetEvents, streamQueueProjection(lastGood))"));
}

const batcherSource = sliceOne(/^function createFleetEventBatcher\(options\)\{[\s\S]*?\n\}$/gm, "definition of `createFleetEventBatcher`");
const { createFleetEventBatcher } = new Function(`${batcherSource}\nreturn { createFleetEventBatcher };`)();

// #2297: Batch Fleet Glass event replay rendering.
// A burst of replayed events performs O(N) ingestion and a bounded number of full renders.
{
  class FakeScheduler {
    constructor() {
      this.currentTime = 0;
      this.nextId = 1;
      this.tasks = new Map();
    }
    schedule(fn, delayMs) {
      const id = this.nextId++;
      this.tasks.set(id, { fn, runAt: this.currentTime + delayMs, id });
      return id;
    }
    cancel(id) {
      this.tasks.delete(id);
    }
    now() {
      return this.currentTime;
    }
    advance(ms) {
      this.currentTime += ms;
      while (true) {
        let nextTask = null;
        for (const task of this.tasks.values()) {
          if (task.runAt <= this.currentTime) {
            if (!nextTask || task.runAt < nextTask.runAt || (task.runAt === nextTask.runAt && task.id < nextTask.id)) {
              nextTask = task;
            }
          }
        }
        if (!nextTask) break;
        this.tasks.delete(nextTask.id);
        nextTask.fn();
      }
    }
  }

  // Acceptance 1: Deliver at least 673 distinct event messages without advancing scheduler -> 0 renders.
  // One scheduler flush produces exactly one ordered render of all events.
  {
    const scheduler = new FakeScheduler();
    let renderCount = 0;
    let lastRenderedEvents = null;
    const batcher = createFleetEventBatcher({
      debounceMs: 16,
      maxDelayMs: 50,
      scheduler,
      onRender: (evts) => {
        renderCount++;
        lastRenderedEvents = [...evts];
      },
    });

    const TOTAL_EVENTS = 673;
    // Feed 673 events in reverse order to prove sorting occurs on flush
    for (let i = TOTAL_EVENTS; i >= 1; i--) {
      batcher.recordEvent({ id: i, kind: "attemptStarted", attemptId: `att-${i}` });
    }

    check("673 event messages delivered without advancing scheduler cause 0 renders", renderCount === 0);
    check("all 673 events are immediately ingested into pending collection", batcher.events.length === TOTAL_EVENTS);
    check("flush is scheduled", batcher.isScheduled() && batcher.isDirty());

    // Advance scheduler past the bound
    scheduler.advance(50);

    check("one scheduler flush produces exactly 1 render for 673 events", renderCount === 1);
    check("rendered events count is 673", lastRenderedEvents?.length === TOTAL_EVENTS);
    check("rendered events are sorted in ascending order by ID",
          lastRenderedEvents?.[0]?.id === 1 && lastRenderedEvents?.[TOTAL_EVENTS - 1]?.id === TOTAL_EVENTS);
    check("batcher is no longer dirty or scheduled after flush", !batcher.isDirty() && !batcher.isScheduled());
  }

  // Acceptance 2: Duplicate IDs remain deduplicated across pending batch and already-rendered collection.
  {
    const scheduler = new FakeScheduler();
    let renderCount = 0;
    const batcher = createFleetEventBatcher({
      debounceMs: 16,
      maxDelayMs: 50,
      scheduler,
      onRender: () => { renderCount++; },
    });

    batcher.recordEvent({ id: 10, kind: "first" });
    const dupPendingAccepted = batcher.recordEvent({ id: 10, kind: "duplicate-pending" });
    check("duplicate ID in pending batch is rejected immediately", !dupPendingAccepted);
    check("pending collection has exactly 1 event for duplicate ID", batcher.events.length === 1);

    scheduler.advance(50);
    check("render occurred once", renderCount === 1);

    const dupRenderedAccepted = batcher.recordEvent({ id: 10, kind: "duplicate-after-render" });
    check("duplicate ID after flush is rejected immediately", !dupRenderedAccepted);
    check("collection still has exactly 1 event", batcher.events.length === 1);
    check("no new flush scheduled for duplicate event", !batcher.isScheduled() && !batcher.isDirty());
  }

  // Acceptance 3: An isolated event schedules and completes a visible render within the explicit bound.
  {
    const scheduler = new FakeScheduler();
    let renderCount = 0;
    let renderedAt = null;
    const batcher = createFleetEventBatcher({
      debounceMs: 16,
      maxDelayMs: 50,
      scheduler,
      onRender: () => {
        renderCount++;
        renderedAt = scheduler.now();
      },
    });

    scheduler.advance(100);
    batcher.recordEvent({ id: 1, kind: "isolated" });
    check("isolated event does not render synchronously", renderCount === 0);

    // Before debounce (at 115ms), still no render
    scheduler.advance(15);
    check("isolated event does not render before debounce delay", renderCount === 0);

    // At 116ms (16ms debounce), flush runs
    scheduler.advance(1);
    check("isolated event completes render within explicit debounce bound", renderCount === 1 && renderedAt === 116);
    check("visibility delay (16ms) is <= explicit max delay bound (50ms)", (renderedAt - 100) <= 50);
  }

  // Acceptance 4: Events arriving during or immediately after a flush cannot be stranded or require a second external event.
  {
    const scheduler = new FakeScheduler();
    let renderCount = 0;
    let batcher;
    batcher = createFleetEventBatcher({
      debounceMs: 16,
      maxDelayMs: 50,
      scheduler,
      onRender: () => {
        renderCount++;
        if (renderCount === 1) {
          // Event arriving during first flush execution
          batcher.recordEvent({ id: 2, kind: "during-flush" });
        }
      },
    });

    batcher.recordEvent({ id: 1, kind: "initial" });
    scheduler.advance(50);

    check("first flush ran and recorded re-entrant event", renderCount === 1);
    check("re-entrant event marked batcher dirty and scheduled next flush", batcher.isDirty() && batcher.isScheduled());

    // Advance scheduler to let the second flush run without any second external trigger
    scheduler.advance(16);
    check("re-entrant event completed render without second external event", renderCount === 2);
    check("both events rendered and sorted", batcher.events.length === 2 && batcher.events[1].id === 2);

    // Event arriving immediately after flush completes
    batcher.recordEvent({ id: 3, kind: "after-flush" });
    check("event immediately after flush is scheduled", batcher.isScheduled() && batcher.isDirty());
    scheduler.advance(16);
    check("event immediately after flush renders promptly", renderCount === 3 && batcher.events.length === 3);
  }

  // Acceptance 5: Continuous burst respects maximum delay bound (maxDelayMs).
  {
    const scheduler = new FakeScheduler();
    let renderTimes = [];
    const batcher = createFleetEventBatcher({
      debounceMs: 16,
      maxDelayMs: 50,
      scheduler,
      onRender: () => {
        renderTimes.push(scheduler.now());
      },
    });

    for (let t = 0; t <= 100; t += 5) {
      batcher.recordEvent({ id: t + 1, kind: "continuous" });
      scheduler.advance(5);
    }
    check("continuous event stream flushes within explicit maxDelayMs bound (50ms)",
          renderTimes.length >= 2 && renderTimes[0] === 50 && renderTimes[1] === 100);
  }
}

const obligationPanel = new Function("esc", "age", `${source}\nreturn conductorObligationsHtml;`)(esc, age);
const obligationRow = (status) => ({ status, requestedAction: "Continue work", owner: "queue-lifecycle",
  createdAt: "2026-09-07T11:00:00Z", reason: "Status explanation" });
const obligationView = (rows, overrides = {}) => ({ conductorObligations: {
  available: true, rows, unresolvedCount: rows.filter(r => r.status !== "ActionObserved").length,
  completedCount: rows.filter(r => r.status === "ActionObserved").length,
  quarantinedCount: 0, omittedCount: 0, ...overrides } });
check("absent schema is unknown, not empty", obligationPanel({}).includes("pending work is unknown"));
check("unavailable evidence is unknown", obligationPanel({conductorObligations:{available:false}}).includes("pending work is unknown"));
check("known empty has explicit empty message", obligationPanel(obligationView([])).includes("No unresolved conductor requests"));
for(const status of ["Pending", "Submitted", "TransportAcknowledged", "Blocked", "Unsupported"]){
  const rendered = obligationPanel(obligationView([obligationRow(status)]));
  check(`${status} remains unresolved`, rendered.includes("1 shown of 1") && !rendered.includes("Completed requests"));
}
check("observed action is separate completed history", obligationPanel(obligationView([obligationRow("ActionObserved")])).includes("Completed requests"));
check("quarantine plus completed never all-clear", !obligationPanel(obligationView([obligationRow("ActionObserved")], {quarantinedCount:1})).includes("No unresolved conductor requests"));
check("omission never all-clear", !obligationPanel(obligationView([], {omittedCount:1})).includes("No unresolved conductor requests"));
const hostileRow = {...obligationRow("Blocked"), requestedAction:"<img onerror=alert(1)>", owner:"<script>", reason:"<svg>"};
const escapedObligations = obligationPanel(obligationView([hostileRow]));
check("all displayed obligation text escaped", !escapedObligations.includes("<img") && !escapedObligations.includes("<script>") && !escapedObligations.includes("<svg>"));
const validAdvice = {state:"available", explanation:"<img src=x onerror=alert(1)>", choice:"hold", observedAt:"2026-09-07T11:59:00Z", completedAt:"2026-09-07T12:00:00Z"};
const advisedRow = {...obligationRow("Pending"), issue:2499, stage:"implement", advice:validAdvice};
const renderedAdvice = obligationPanel(obligationView([advisedRow]));
check("advice explanation is escaped and private-shaped fields are not rendered",
  renderedAdvice.includes("Advice only — no action taken") && renderedAdvice.includes("&lt;img")
    && !renderedAdvice.includes("<img") && !renderedAdvice.includes("holder") && !renderedAdvice.includes("path"));
const refusedAdmission = {state:"refused", owner:"Repository conductor",
  nextTrigger:"Inspect retained evidence; reconcile manually against the current head <svg>"};
const refusedAdvice = obligationPanel(obligationView([{...advisedRow,
  automaticAdmission:refusedAdmission}]));
check("automatic admission refusal shows owner and bounded manual next trigger, not completion",
  refusedAdvice.includes("Automatic admission refused") && refusedAdvice.includes("Repository conductor")
    && refusedAdvice.includes("Next trigger: Inspect retained evidence")
    && refusedAdvice.includes("&lt;svg&gt;") && !refusedAdvice.includes("<svg>")
    && !refusedAdvice.includes("Completed requests"));
for(const malformed of [null, [], {...refusedAdmission, state:"completed"},
  {...refusedAdmission, owner:"private-holder"}, {...refusedAdmission, nextTrigger:""},
  {...refusedAdmission, nextTrigger:"x".repeat(4097)}]){
  const rendered = obligationPanel(obligationView([{...advisedRow, automaticAdmission:malformed}]));
  check("malformed automatic admission fails closed without an asserted disposition",
    rendered.includes("Unknown advice") && !rendered.includes("Automatic admission refused")
      && !rendered.includes("Advice only — no action taken") && !rendered.includes("private-holder"));
}
const staleAdvice = obligationPanel(obligationView([{...advisedRow, advice:{...validAdvice, state:"stale"}}]));
check("stale advice is visibly stale and never styled as completed",
  staleAdvice.includes("Stale") && staleAdvice.includes("Advice recorded 2026-09-07T12:00:00Z")
    && !staleAdvice.includes("Completed 2026-09-07T12:00:00Z") && staleAdvice.includes("c-warn") && !staleAdvice.includes("c-ok"));
const pendingAdvice = obligationPanel(obligationView([{...advisedRow, advice:{...validAdvice, state:"pending"}}]));
check("non-final advice does not expose recommendation details",
  pendingAdvice.includes("Pending") && !pendingAdvice.includes("Choice: Hold") && !pendingAdvice.includes("Advice explanation: &lt;img"));
const blockedWithoutIssue = {...advisedRow, advice:{...validAdvice, state:"blocked"}};
delete blockedWithoutIssue.issue;
const blockedUnknownSource = obligationPanel(obligationView([blockedWithoutIssue]));
check("missing source issue keeps blocked disposition visible",
  blockedUnknownSource.includes("Blocked") && blockedUnknownSource.includes("Source unknown")
    && !blockedUnknownSource.includes("Unknown advice"));
const unknownAdvice = obligationPanel(obligationView([{...advisedRow, advice:{...validAdvice, state:"invented"}}]));
check("unknown advice fails closed visibly",
  unknownAdvice.includes("Unknown advice") && unknownAdvice.includes("Advice status: Unknown") && !unknownAdvice.includes("invented"));
const arrayStateAdvice = obligationPanel(obligationView([{...advisedRow, advice:{...validAdvice, state:["available"]}}]));
check("array-valued advice state fails closed",
  arrayStateAdvice.includes("Unknown advice") && arrayStateAdvice.includes("Advice status: Unknown"));
const arrayChoiceAdvice = obligationPanel(obligationView([{...advisedRow, advice:{...validAdvice, choice:["hold"]}}]));
check("array-valued advice choice fails closed",
  arrayChoiceAdvice.includes("Unknown advice") && arrayChoiceAdvice.includes("Advice status: Unknown"));
check("available advice does not move a pending row into completed history",
  renderedAdvice.includes("Unresolved requests (1 shown of 1)") && !renderedAdvice.includes("Completed requests"));
const adviceOnlySummary = streamStatusSummaryHtml(obligationView([advisedRow]), []);
check("advice-only summary uses neutral needs-attention wording",
  adviceOnlySummary.includes("Needs attention") && !adviceOnlySummary.includes("Needs Operator"));
const legacyObligation = obligationPanel(obligationView([obligationRow("Pending")]));
check("rows without advice retain the legacy card rendering",
  !legacyObligation.includes("Advice only") && legacyObligation.includes("Status explanation: Status explanation"));
const unsupportedManual = obligationPanel(obligationView([obligationRow("Unsupported")]));
check("scalar unsupported request shows the fixed manual next boundary",
  unsupportedManual.includes("Next boundary: owner intervention; no automatic retry"));
check("unsupported request remains unresolved without claiming completion",
  unsupportedManual.includes("Unresolved requests (1 shown of 1)") && !unsupportedManual.includes("Completed requests"));
check("pending request does not inherit unsupported manual guidance",
  !legacyObligation.includes("Next boundary: owner intervention; no automatic retry"));
const arrayUnsupported = obligationPanel(obligationView([obligationRow(["Unsupported"])]));
check("array-shaped unsupported status does not match the scalar guidance",
  !arrayUnsupported.includes("Next boundary: owner intervention; no automatic retry"));
const unknownUnsupported = obligationPanel(obligationView([obligationRow("UnknownStatus")]));
check("unknown status does not match the unsupported guidance",
  !unknownUnsupported.includes("Next boundary: owner intervention; no automatic retry"));
const actionTrigger = obligationPanel(obligationView([{...obligationRow("Unsupported"),
  advice:validAdvice,
  action:{kind:"replace-review", state:"queued", origin:"manual", owner:"Repository conductor",
    nextTrigger:"Inspect the current head"}}]));
check("valid action next trigger is not duplicated by fixed unsupported guidance",
  actionTrigger.includes("Next trigger: Inspect the current head")
    && !actionTrigger.includes("Next boundary: owner intervention; no automatic retry"));
const admissionTrigger = obligationPanel(obligationView([{...obligationRow("Unsupported"),
  advice:validAdvice, automaticAdmission:refusedAdmission}]));
check("valid automatic-admission next trigger is not duplicated by fixed unsupported guidance",
  admissionTrigger.includes("Next trigger: Inspect retained evidence")
    && !admissionTrigger.includes("Next boundary: owner intervention; no automatic retry"));
const mixedTrigger = obligationPanel(obligationView([{...obligationRow("Unsupported"),
  action:{kind:"replace-review", state:"queued", origin:"manual", owner:"Repository conductor",
    nextTrigger:"Inspect the current head"}, automaticAdmission:refusedAdmission}]));
check("valid action and admission do not suppress fallback when advice is absent",
  mixedTrigger.includes("Next boundary: owner intervention; no automatic retry")
    && !mixedTrigger.includes("Next trigger:"));
const malformedMixedTrigger = obligationPanel(obligationView([{...obligationRow("Unsupported"),
  advice:null,
  action:{kind:"replace-review", state:"queued", origin:"manual", owner:"Repository conductor",
    nextTrigger:"Inspect the current head"}, automaticAdmission:refusedAdmission}]));
check("valid action and admission do not suppress fallback when advice is malformed",
  malformedMixedTrigger.includes("Unknown advice")
    && malformedMixedTrigger.includes("Next boundary: owner intervention; no automatic retry")
    && !malformedMixedTrigger.includes("Next trigger:"));
const escapedUnsupported = obligationPanel(obligationView([{...obligationRow("Unsupported"), owner:"<foreign-owner>"}]));
check("unsupported manual guidance preserves escaped foreign owner labels",
  escapedUnsupported.includes("&lt;foreign-owner&gt;") && !escapedUnsupported.includes("<foreign-owner>"));
check("real stream rendering includes obligation panel", html.includes("contentEl.innerHTML = conductorObligationsHtml(lastGood) + streamGroupedEventsHtml(fleetEvents, streamQueueProjection(lastGood))"));
check("summary cannot claim all-clear for unresolved requests", streamStatusSummaryHtml(obligationView([obligationRow("Pending")]), []).includes("1 conductor request(s) unresolved"));
check("daemon-shaped stale obligation snapshot remains visibly as-of", obligationPanel({...obligationView([]), derived_at:"2026-09-01T00:00:00Z", projectionStaleAfterSeconds:90}).includes("Snapshot is stale"));
check("snapshot timestamp shown even with no rooms", obligationPanel({...obligationView([]), derived_at:"2026-09-01T00:00:00Z"}).includes("Snapshot as of 2026-09-01T00:00:00Z"));
const obligationFreshness = new Function(`${source}\nreturn conductorProjectionFreshness;`)();
const freshSnap = {derived_at:"2026-09-07T11:59:00Z", projectionStaleAfterSeconds:90};
check("daemon timestamp within actual threshold is fresh", obligationFreshness(freshSnap, NOW) === "fresh");
check("daemon threshold controls staleness", obligationFreshness({...freshSnap,projectionStaleAfterSeconds:30}, NOW) === "stale");
for(const invalid of [undefined, null, 0, -1, "90", NaN, Infinity]){
  check(`invalid freshness threshold ${invalid} stays unknown`, obligationFreshness({...freshSnap,projectionStaleAfterSeconds:invalid}, NOW) === "unknown");
}
check("invalid derivation timestamp stays unknown", obligationFreshness({...freshSnap,derived_at:"broken"}, NOW) === "unknown");
check("future derivation timestamp stays unknown", obligationFreshness({...freshSnap,derived_at:"2026-09-08T12:00:00Z"}, NOW) === "unknown");

check("mobile browsers use device width and retain zoom", html.includes('<meta name="viewport" content="width=device-width, initial-scale=1">'));
check("phone navigation wraps instead of clipping", html.includes('.tabs{flex-wrap:wrap;}'));

if (failures.length) {
  console.error(`glass.selftest.mjs: FAIL -- ${failures.length} check(s):`);
  for (const f of failures) console.error(`  !! ${f}`);
  process.exit(1);
}
console.log(`glass.selftest.mjs: pass`);
