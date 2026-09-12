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
const REQUIRED = ["queueSlotsLineHtml", "queueLanesTableHtml", "queuePendingTableHtml", "queuePrTableHtml", "queuePrRowsHtml", "queuePrHistoryHtml", "queueBoardHtml", "streamStatusSummaryHtml", "streamEventReceiptHtml", "streamGroupedEventsHtml", "streamHistoryHtml", "streamHomeHtml"];
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
const { queueSlotsLineHtml, queuePendingTableHtml, queuePrTableHtml, queuePrHistoryHtml, queueLanesTableHtml, queueBoardHtml, streamStatusSummaryHtml, streamEventReceiptHtml, streamGroupedEventsHtml, streamHistoryHtml, streamHomeHtml } = panel;

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
    { repository: "github.com/acme/one", pr: 2028, prState: "open", freshness: "current", observedAt: "2026-09-07T11:59:30Z", attemptedAt: "2026-09-07T11:59:30Z", headSha: "aaaaaaaa11111111", deployment: "not-recorded", lanes: [
      { tag: "a", stage: "review", state: "Done", round: 1, verdict: "block", checks: "failing", checksObservedAt: "2026-09-07T11:00:00Z", checksHeadSha: "bbbbbbbb22222222", twinIssue: 1530 },
      { tag: "cancelled", stage: "review", state: "Cancelled", round: 2 },
      { tag: "halted", stage: "fix", state: "Failed", round: 2, halted: true, twinIssue: 1600 },
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
  check("idle stream summary answers what is blocked or stale", idleOut.includes("None blocked or stale"));
  check("idle stream summary answers what needs the operator", idleOut.includes("All clear · no operator action needed"));
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
}

if (failures.length) {
  console.error(`glass.selftest.mjs: FAIL -- ${failures.length} check(s):`);
  for (const f of failures) console.error(`  !! ${f}`);
  process.exit(1);
}
console.log(`glass.selftest.mjs: pass`);
