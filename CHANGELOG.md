# Changelog

**One release train again, named `baton`, from `0.32.0` on (#1835).** Between `0.14.0` and
`0.31.0` the repo shipped as three release-please components bumped in lock-step (`flow-v*`,
`vendors-v*`, `cli-v*`); those entries lived in per-project `CHANGELOG.md` files under `src/`,
removed in #1953 once release-please stopped writing them — read them from git at the last commit
that carried them: `git show v0.35.0:src/Baton.Cli/CHANGELOG.md` (likewise `src/Baton/` and
`src/Baton.Vendors/`). New entries are prepended here, tagged `vX.Y.Z`. See
`release-please-config.json`. Everything below this line predates the split: the repo's single
shared version through `0.14.0`.

## [0.35.0](https://github.com/philipreese/baton/compare/v0.34.0...v0.35.0) (2026-09-06)


### Features

* **accounting:** Backfill settled rooms and merged PRs into the cost ledger and stamp dispatch labels on rows ([#1931](https://github.com/philipreese/baton/issues/1931)) ([20dc565](https://github.com/philipreese/baton/commit/20dc56525e1a54e07c7c3cc30323fb88559e2a0a))
* **accounting:** Export the cost ledger under benchmarks/ledger with a committed baseline and the medians the comparator reads ([#1938](https://github.com/philipreese/baton/issues/1938)) ([5d793c2](https://github.com/philipreese/baton/commit/5d793c2bd91f75d498af044464256b40d569827e))
* **accounting:** Record issue, PR, review verdict, diff shape and conductor resolutions on cost-ledger rows ([#1913](https://github.com/philipreese/baton/issues/1913)) ([1de43a0](https://github.com/philipreese/baton/commit/1de43a0ce8f2c908006b3c725e2ecd1869dbaddb))
* **benchmarks:** Automate DeepSWE benchmark snapshot refreshes ([#1937](https://github.com/philipreese/baton/issues/1937)) ([4c85dbc](https://github.com/philipreese/baton/commit/4c85dbc1366df334852c5ad60e7300e6b61e8acc))
* **cli:** Record every arrest outcome on the room and write the terminal fact when a worker dies outside baton ([#1916](https://github.com/philipreese/baton/issues/1916)) ([88a97b7](https://github.com/philipreese/baton/commit/88a97b7cf423b1b1b9033973e76588261a382b13))
* **dispatch:** Record runway admission decisions and reserve headroom across concurrent dispatches ([#1932](https://github.com/philipreese/baton/issues/1932)) ([9d10a8b](https://github.com/philipreese/baton/commit/9d10a8b4810653b12cbddda8a1f7e282e86c169a))
* **gates:** Accept a lane's own component receipts for the pre-push gates-fast set and give receipt checks lock priority ([#1936](https://github.com/philipreese/baton/issues/1936)) ([f547d29](https://github.com/philipreese/baton/commit/f547d2980ecb617ba8a1c53bd5c9f60823cdcd15))
* **glass:** Read the daemon's projection file by default and keep the pusher's own derivation as the stale fallback ([#1905](https://github.com/philipreese/baton/issues/1905)) ([517382d](https://github.com/philipreese/baton/commit/517382d4863555c0c83ef504d1da9f05be547db8))
* **memory:** Add the canonical per-repository memory store with a reversible non-destructive import ([#1940](https://github.com/philipreese/baton/issues/1940)) ([9df7735](https://github.com/philipreese/baton/commit/9df77358456fa0c221ac13f2a75f6037c81338a9))
* **memory:** Add the memory audit verb that inventories Claude memory roots against repository identity ([#1908](https://github.com/philipreese/baton/issues/1908)) ([a94bfdb](https://github.com/philipreese/baton/commit/a94bfdb23ea2b10e9fe2080493a606998c5b1078))
* **memory:** Inventory Codex and Antigravity memory roots content-blind and record their formats ([#1930](https://github.com/philipreese/baton/issues/1930)) ([f40aeae](https://github.com/philipreese/baton/commit/f40aeae8f44160533a7defd3e0791e503f812b2a))
* **queue:** Host the conductor's dispatch queue and scheduler in the daemon with a recorded decision per launch ([#1939](https://github.com/philipreese/baton/issues/1939)) ([1e63a17](https://github.com/philipreese/baton/commit/1e63a17534bb184b27ec3bd1eda15904d053765a))
* **skills:** Add the canonical skill manifest, resolver, lint, requirement check and --skill dispatch surface ([#1941](https://github.com/philipreese/baton/issues/1941)) ([bdee980](https://github.com/philipreese/baton/commit/bdee980599040330007603586f74494dc1100489))
* **usage:** Harvest a derived Codex plan-usage snapshot for the runway hold and the glass ([#1926](https://github.com/philipreese/baton/issues/1926)) ([f63fe61](https://github.com/philipreese/baton/commit/f63fe61e7ce712ec206b4587ee110a017aff9549))
* **vendors:** Realize canonical skill packages per vendor as the floor slice of [#1151](https://github.com/philipreese/baton/issues/1151) ([#1929](https://github.com/philipreese/baton/issues/1929)) ([61cbec3](https://github.com/philipreese/baton/commit/61cbec3e83f1c08b58554efd20d62098ce74b623))


### Bug Fixes

* **glass:** Project a Codex execution's live tool calls, turns and billed tokens instead of a zero ([#1907](https://github.com/philipreese/baton/issues/1907)) ([2e140ca](https://github.com/philipreese/baton/commit/2e140ca8df753525232340ab2b785da283173b0f))
* **review:** Refuse a finding with no status instead of binding it to the enum default ([#1922](https://github.com/philipreese/baton/issues/1922)) ([9649990](https://github.com/philipreese/baton/commit/96499906838e6ab5a9c3e166c575bd1420c186a1))
* **review:** Stamp instruments on a redispatched review and drop the inherited verify paragraph ([#1906](https://github.com/philipreese/baton/issues/1906)) ([cb6a157](https://github.com/philipreese/baton/commit/cb6a1579d14c84ccdfd4121cd99fb3bb13509e86))


### Documentation

* **vendors:** Place agy's model catalogue into the canonical purpose tiers from the 2026-09-05 measurement ([#1925](https://github.com/philipreese/baton/issues/1925)) ([9e47c59](https://github.com/philipreese/baton/commit/9e47c5964c95628d9c50b73779371a4af5e720a0))


### Tests

* **cli:** Widen the live-cancel in-flight window so the arrest lands before the step completes ([#1924](https://github.com/philipreese/baton/issues/1924)) ([c2b1f71](https://github.com/philipreese/baton/commit/c2b1f71ecb3515c4e77ff0b199c19a49064027bc))
* **vendor-verify:** Pin agy's tool-name lists to the CLI's live tool catalogue ([#1928](https://github.com/philipreese/baton/issues/1928)) ([42064b3](https://github.com/philipreese/baton/commit/42064b38db3c8eeb928f627903a2564f56ae731a))

## [0.34.0](https://github.com/philipreese/baton/compare/v0.33.0...v0.34.0) (2026-09-05)


### Features

* **accounting:** Add the ledger verb with per-vendor rollups, time filters and JSON/CSV export ([#1893](https://github.com/philipreese/baton/issues/1893)) ([0be9e84](https://github.com/philipreese/baton/commit/0be9e84bf3367d64bc28b9866e869f266854521b))
* **accounting:** Append a labelled two-estimate cost ledger row per settled execution attempt ([#1883](https://github.com/philipreese/baton/issues/1883)) ([98dc364](https://github.com/philipreese/baton/commit/98dc364f101ea6cbf58567964f40a705c4699d8d))
* **dispatch:** Hold new dispatches when the vendor's runway is short, with an audited override ([#1894](https://github.com/philipreese/baton/issues/1894)) ([a9f4d99](https://github.com/philipreese/baton/commit/a9f4d999892edd5bfa838365d83f8618e0fc13ac))
* **glass:** Show per-vendor burn rate and minutes to exhaustion from the usage projection ([#1891](https://github.com/philipreese/baton/issues/1891)) ([678f44e](https://github.com/philipreese/baton/commit/678f44ef52351e559809ccb960ee4e9d711410d5))
* **review:** Run allowlisted verify commands before the reviewer's first turn and attach the results ([#1889](https://github.com/philipreese/baton/issues/1889)) ([0a61197](https://github.com/philipreese/baton/commit/0a6119750463ebd86a0fde38bfef1c3b0f825390))
* **usage:** Journal a declared stream-log loss so the reason survives a refused marker ([#1888](https://github.com/philipreese/baton/issues/1888)) ([074942d](https://github.com/philipreese/baton/commit/074942dc2c0f79558fee619ad2430956a74df4d7))


### Bug Fixes

* **launcher:** Run baton.exe outside ErrorActionPreference Stop so a stderr line cannot kill the daemon task ([#1899](https://github.com/philipreese/baton/issues/1899)) ([5f098c7](https://github.com/philipreese/baton/commit/5f098c7180ff26ecd2a54ba44d66da32ad11029d))
* **usage:** Accept an on-the-hour reset time in the claude usage harvest ([#1900](https://github.com/philipreese/baton/issues/1900)) ([278ff78](https://github.com/philipreese/baton/commit/278ff78798243cc7f29f00f523625016a4b63a7c))


### Code Refactoring

* **store:** Extract a generic JSON-lines ledger shared by the burn and cost ledgers ([#1892](https://github.com/philipreese/baton/issues/1892)) ([99a9171](https://github.com/philipreese/baton/commit/99a9171b722ee9d4fd74c060ff205dceee8ac823))

## [0.33.0](https://github.com/philipreese/baton/compare/v0.32.0...v0.33.0) (2026-09-05)


### Features

* **adapters:** Add the probe-backed Codex execution substrate ([#1862](https://github.com/philipreese/baton/issues/1862)) ([426cd43](https://github.com/philipreese/baton/commit/426cd4348b6a872ce4af0225e8a650bbd6f59560))
* **adapters:** enforce Codex role grants through app-server ([#1871](https://github.com/philipreese/baton/issues/1871)) ([d00406a](https://github.com/philipreese/baton/commit/d00406ad69ca6e33069f7ca9c01eacb8cd374d76))
* **glass:** Project each vendor's own usage counters as advisory runway ([#1869](https://github.com/philipreese/baton/issues/1869)) ([8c724b9](https://github.com/philipreese/baton/commit/8c724b9c1ad444c929a5adb111d63e5edf60bbb3))


### Bug Fixes

* **adapters:** Park a claude weekly-limit 429 on the result event instead of failing the lane ([#1860](https://github.com/philipreese/baton/issues/1860)) ([176e6e1](https://github.com/philipreese/baton/commit/176e6e1a8d5e1f92eb7cad4b7b87f9e16e362a1e))
* **ci:** skip closed PR diff-shape events ([#1855](https://github.com/philipreese/baton/issues/1855)) ([01ce0a4](https://github.com/philipreese/baton/commit/01ce0a4aa5bb5718f5b46a6637dd4c957b1d97e9))
* **codex:** Derive the capability text and effort table from the recorded model catalog ([#1880](https://github.com/philipreese/baton/issues/1880)) ([067c8de](https://github.com/philipreese/baton/commit/067c8dea4f5b1ee9f19400ee0a8da6a872ccfa92))
* **resolve:** Close a rejected capture terminally instead of leaving the workflow running ([#1881](https://github.com/philipreese/baton/issues/1881)) ([7a8c8b7](https://github.com/philipreese/baton/commit/7a8c8b7ed8f53f33f0cd5cabd38666d310482878))
* **usage:** Keep buffered stream chunks and in-memory token totals across a transient log write failure ([#1879](https://github.com/philipreese/baton/issues/1879)) ([cc16507](https://github.com/philipreese/baton/commit/cc16507d9b8c5bdfa2d19c892a332ea045aa5c45))


### Documentation

* **benchmarks:** Derive sortable scores and Pareto frontiers from the DeepSWE snapshot with a tunable steps penalty ([#1868](https://github.com/philipreese/baton/issues/1868)) ([b8243b6](https://github.com/philipreese/baton/commit/b8243b6756c485b08c6ce0abb705ed3bf897aba4))
* **benchmarks:** Index the snapshots and record the operator's cast of vendors and models ([#1865](https://github.com/philipreese/baton/issues/1865)) ([da39b67](https://github.com/philipreese/baton/commit/da39b675bc6290fb00c600859e07f6342d1c5b69))
* **vendor:** Re-probe claude 2.1.258 and record the six re-established findings ([#1859](https://github.com/philipreese/baton/issues/1859)) ([8069880](https://github.com/philipreese/baton/commit/8069880e030e26102e3daf24fd6a5595fe5cd006))


### Tests

* Remove the Unix-only test arms left dead by the Windows-only CI matrix ([#1873](https://github.com/philipreese/baton/issues/1873)) ([262ef81](https://github.com/philipreese/baton/commit/262ef813e06116a05c482ea1eb30dfdb21536e3f))


### Miscellaneous

* **gates:** reject stale DeepSWE derived scores ([#1874](https://github.com/philipreese/baton/issues/1874)) ([1b7c7ab](https://github.com/philipreese/baton/commit/1b7c7ab109a5c610950253841be03a8694a4098b))
* **vendors:** Move the frontier and standard tiers to Opus (high / medium) until the codex adapter lands ([#1866](https://github.com/philipreese/baton/issues/1866)) ([d45c21d](https://github.com/philipreese/baton/commit/d45c21d05ba7855dcfdb59ca0646596d31b204ac))

## [0.32.0](https://github.com/philipreese/baton/compare/v0.31.1...v0.32.0) (2026-09-04)


### Features

* capture Claude session IDs from worker streams ([#1851](https://github.com/philipreese/baton/issues/1851)) ([e9c89e6](https://github.com/philipreese/baton/commit/e9c89e62cee93eb7d1823faaf00bc4f00f75b4a3))
* **dispatch:** Continue a prior worker's vendor session for a follow-on lane with journaled provenance ([#1840](https://github.com/philipreese/baton/issues/1840)) ([3e998f2](https://github.com/philipreese/baton/commit/3e998f24343a426cef610302be685113968b96ef))
* **dispatch:** Rebind a quota-parked step to a declared fallback binding ([#1838](https://github.com/philipreese/baton/issues/1838)) ([44c3ce3](https://github.com/philipreese/baton/commit/44c3ce3c55e058234fcbdd992923d9aca9b0b6a4))


### Bug Fixes

* **adapters:** Refuse a room path with a .claude component, not the config-root value ([#1847](https://github.com/philipreese/baton/issues/1847)) ([2be2ced](https://github.com/philipreese/baton/commit/2be2ced4c87041249017384c51e8cc7e00b79891))
* **flow:** Treat a verify probe that cannot answer as unknown, never as the task being absent ([#1836](https://github.com/philipreese/baton/issues/1836)) ([2f58a56](https://github.com/philipreese/baton/commit/2f58a563feebd72390e7d124c0b3c45f5794c1ae))
* restore generated-code coverage exclusions ([#1850](https://github.com/philipreese/baton/issues/1850)) ([fa7e51e](https://github.com/philipreese/baton/commit/fa7e51e325485f9b7a0a8486cca09a7bd8998365))
* **tool-refresh:** Keep polling for a slow daemon launch instead of failing a healthy refresh at 5s ([#1845](https://github.com/philipreese/baton/issues/1845)) ([ad464bb](https://github.com/philipreese/baton/commit/ad464bbe6c8c95c85d47662b1649990316f87e38))


### Miscellaneous

* **tests:** Run dotnet test on Microsoft.Testing.Platform and take xunit v3 4.0.0 ([#1843](https://github.com/philipreese/baton/issues/1843)) ([0ae5a6f](https://github.com/philipreese/baton/commit/0ae5a6f9af13d24faafa6d933668a1511144fe82))

## [0.31.1](https://github.com/philipreese/baton/compare/v0.31.0...v0.31.1) (2026-09-04)


### Bug Fixes

* **glass:** Key the KV-cap banner on the worker's real 429 instead of two timestamps that share a write ([#1831](https://github.com/philipreese/baton/issues/1831)) ([4eb2578](https://github.com/philipreese/baton/commit/4eb2578ecc61e0025e5dabff1e490cee7fa764b2))


### Documentation

* **glass:** List the ntfy push configuration keys in the README ([#1830](https://github.com/philipreese/baton/issues/1830)) ([b2a94ae](https://github.com/philipreese/baton/commit/b2a94aed9496a9d4a3358f409bb876d5cc35c59c))


### Tests

* **vendor-verify:** Pin the claude sensitive-root write refusal as a sentinel ([#1832](https://github.com/philipreese/baton/issues/1832)) ([c3fcaae](https://github.com/philipreese/baton/commit/c3fcaae60492e9da6b5c2d9979110b5d04d2cc5a))


### Miscellaneous

* **release:** Collapse the three release-please components into one root package named baton ([#1837](https://github.com/philipreese/baton/issues/1837)) ([bcb4db7](https://github.com/philipreese/baton/commit/bcb4db7fe301ce1c81d337892693102d6892c7af))

## [0.14.0](https://github.com/aer-works/aer-flow/compare/v0.13.0...v0.14.0) (2026-07-18)


### Features

* Milestone 20 - Daemonization, Security, and Remote Control ([#223](https://github.com/aer-works/aer-flow/issues/223)) ([5a5b604](https://github.com/aer-works/aer-flow/commit/5a5b604d41e717ba643f18093c407b581b3666bd))
* **ui:** M19 Phase 2 - Navigation shell, Aer.Ui.Core seam, MVVM re-home, and the decision inbox ([#195](https://github.com/aer-works/aer-flow/issues/195)) ([2f25168](https://github.com/aer-works/aer-flow/commit/2f25168d843c609d986a0a73679e93087577c831))
* **ui:** M19 Phase 3 - Task view, human-first ([#196](https://github.com/aer-works/aer-flow/issues/196)) ([2743338](https://github.com/aer-works/aer-flow/commit/2743338e07313087221d6c091dceea3fe9592d0f))
* **ui:** M19 Phase 4 - Guided authoring, no hand-edited config files ([#197](https://github.com/aer-works/aer-flow/issues/197)) ([a3ef3b8](https://github.com/aer-works/aer-flow/commit/a3ef3b86c282194478e2247967a720356a93e86c))
* **ui:** M19 Phase 5 - Visual design pass ([#198](https://github.com/aer-works/aer-flow/issues/198)) ([8b500f5](https://github.com/aer-works/aer-flow/commit/8b500f56d6e0eb2e7d5175f2d58bfe8d675a1c9a))
* **ui:** M19 Phase 6 - Gate: the non-expert path in default CI ([#201](https://github.com/aer-works/aer-flow/issues/201)) ([d0602e0](https://github.com/aer-works/aer-flow/commit/d0602e03d83b7186632279341553a7bcbd35d62f))


### Bug Fixes

* **ui:** DAG node colors resolve the window's actual theme variant ([#205](https://github.com/aer-works/aer-flow/issues/205)) ([5dee3e1](https://github.com/aer-works/aer-flow/commit/5dee3e152ef64805c3fbfca631777f7e8d259413))
* **ui:** Polish title bar, navigation rail transitions, and step preview cache ([#221](https://github.com/aer-works/aer-flow/issues/221)) ([fc0ae3c](https://github.com/aer-works/aer-flow/commit/fc0ae3c17a66542a3a18dbf11073e11c2990bfc7))


### Documentation

* Fold M19 decisions of record; prune completed phase plan ([#203](https://github.com/aer-works/aer-flow/issues/203)) ([dfe946e](https://github.com/aer-works/aer-flow/commit/dfe946e02fba39883dd67d01f088dc1072d26413))
* **plan:** Define the M19 (Product UX) phase plan, redefine M19 scope, and settle the worker-config write-model gap ([#192](https://github.com/aer-works/aer-flow/issues/192)) ([943df5c](https://github.com/aer-works/aer-flow/commit/943df5cbbb382cce71dcf7bb0821c1a3d39a9a62))
* **ux:** M19 Phase 1 - UX baseline, principles, and information architecture ([#194](https://github.com/aer-works/aer-flow/issues/194)) ([cfa484a](https://github.com/aer-works/aer-flow/commit/cfa484a4a5bc39dd4b5afe01317b9d445ad540b4))


### Continuous Integration

* Code coverage report task (coverlet + ReportGenerator) ([#200](https://github.com/aer-works/aer-flow/issues/200)) ([d640b96](https://github.com/aer-works/aer-flow/commit/d640b9602f622789444aa551b5a3a71e5d617aa7))

## [0.13.0](https://github.com/aer-works/aer-flow/compare/v0.12.0...v0.13.0) (2026-07-17)


### Features

* **ui:** M18 Phase 1 — Transcript read seam ([#182](https://github.com/aer-works/aer-flow/issues/182)) ([4aef5d2](https://github.com/aer-works/aer-flow/commit/4aef5d2d354a50c7a531f7e6d2afe896cbdee37c))
* **ui:** M18 Phase 2 — The conversation view ([#183](https://github.com/aer-works/aer-flow/issues/183)) ([1e57b8d](https://github.com/aer-works/aer-flow/commit/1e57b8d1062710a8791e3821b5f65f07649a8e78))
* **ui:** M18 Phase 3 — Gate: conversation round trip in default CI ([#184](https://github.com/aer-works/aer-flow/issues/184)) ([b495890](https://github.com/aer-works/aer-flow/commit/b49589030fdbc959202c3983b7ec2063c08acfc9))


### Documentation

* **plan:** Define the M18 (Conversation View) phase plan and settle the transcript artifact contract ([#180](https://github.com/aer-works/aer-flow/issues/180)) ([5249530](https://github.com/aer-works/aer-flow/commit/524953067f3bc72b53d61667d916af421ecf4eee))

## [0.12.0](https://github.com/aer-works/aer-flow/compare/v0.11.0...v0.12.0) (2026-07-17)


### Features

* **adapters:** M17 Phase 4 — Dispatch integration: the third adapter ([#174](https://github.com/aer-works/aer-flow/issues/174)) ([0b11c9a](https://github.com/aer-works/aer-flow/commit/0b11c9a1970fd6fb0ebd4bb6ce4f48489bd14cdb))
* **dialogue:** M17 Phase 5 — Gates: stub round trip in default CI + live dialogue runbook ([#175](https://github.com/aer-works/aer-flow/issues/175)) ([5cac1f5](https://github.com/aer-works/aer-flow/commit/5cac1f5fc48ce17975f64268564b290af985b0ea))
* **workers:** M17 Phase 2 — Transcript contract + dialogue worker skeleton ([#172](https://github.com/aer-works/aer-flow/issues/172)) ([d4e0f95](https://github.com/aer-works/aer-flow/commit/d4e0f952ddc14a351ec50f29d65847d88f38b70b))
* **workers:** M17 Phase 3 — Turn loop, termination, and failure semantics ([#173](https://github.com/aer-works/aer-flow/issues/173)) ([b974809](https://github.com/aer-works/aer-flow/commit/b9748097ef9e0c0e55a95d9f470494c8e83aee8d))


### Documentation

* M17 Phase 1 — Real-workflow walkthrough (§18.1 baseline) ([#171](https://github.com/aer-works/aer-flow/issues/171)) ([e2e8e3d](https://github.com/aer-works/aer-flow/commit/e2e8e3dff178e67600ef44ced0b8999bcaa9d266))
* **plan:** Promote UI spec to v1.0 and plan M17–M19 ([#169](https://github.com/aer-works/aer-flow/issues/169)) ([d995738](https://github.com/aer-works/aer-flow/commit/d9957383eea4af343fbc8e42209e91f6a6b7305a))

## [0.11.0](https://github.com/aer-works/aer-flow/compare/v0.10.0...v0.11.0) (2026-07-16)


### Features

* **ui:** M16 Phase 1 — Template write seam + create/save walking skeleton ([#158](https://github.com/aer-works/aer-flow/issues/158)) ([e925a57](https://github.com/aer-works/aer-flow/commit/e925a57c727cfb4d6c6c2e61d24dba46af09c653))
* **ui:** M16 Phase 2 — Step & graph editing with live structural validation ([#160](https://github.com/aer-works/aer-flow/issues/160)) ([dc98f69](https://github.com/aer-works/aer-flow/commit/dc98f69b514781d4b9026f0c6b84e9fe6557f872))
* **ui:** M16 Phase 3 — PausePoint + SupersedeTargets editing ([#162](https://github.com/aer-works/aer-flow/issues/162)) ([46f66cd](https://github.com/aer-works/aer-flow/commit/46f66cd4628de3d024d9b344d797d4737bcd4369))
* **ui:** M16 Phase 4 — Worker-binding configuration editing ([#161](https://github.com/aer-works/aer-flow/issues/161)) ([b5acbb5](https://github.com/aer-works/aer-flow/commit/b5acbb583fdc658d5c1d873dc3225677c730a821))
* **ui:** M16 Phase 5 — Authoring round trips in default CI ([#163](https://github.com/aer-works/aer-flow/issues/163)) ([e009069](https://github.com/aer-works/aer-flow/commit/e009069e2cd168ef745e485541f5906622cb7f00))


### Documentation

* Define the M16 (UI Authoring) phase plan and prune completed milestones ([#155](https://github.com/aer-works/aer-flow/issues/155)) ([c3f2d82](https://github.com/aer-works/aer-flow/commit/c3f2d8220c1a0e164b39a1fdcafdf793567651d2))
* **spec:** Resolve M16's two spec gaps ahead of the phases that hit them ([#157](https://github.com/aer-works/aer-flow/issues/157)) ([ecaee17](https://github.com/aer-works/aer-flow/commit/ecaee179bee5e9bdf882b9e2336e441938e46def))

## [0.10.0](https://github.com/aer-works/aer-flow/compare/v0.9.0...v0.10.0) (2026-07-15)


### Features

* **ui:** M15 Phase 1 — Mutation seam + start/resume a workflow ([#145](https://github.com/aer-works/aer-flow/issues/145)) ([8b9c12e](https://github.com/aer-works/aer-flow/commit/8b9c12e686e1a8676df75fbfb7d4ab40ec062e33))
* **ui:** M15 Phase 2 — Resolve decisions: Approve / Reject ([#146](https://github.com/aer-works/aer-flow/issues/146)) ([f7109c7](https://github.com/aer-works/aer-flow/commit/f7109c7ff744d0622003a71925a3663df84762d0))
* **ui:** M15 Phase 3 — Artifact-carrying decisions: Retry-with-revision + Send-back ([#147](https://github.com/aer-works/aer-flow/issues/147)) ([5ee0c0a](https://github.com/aer-works/aer-flow/commit/5ee0c0a1c7e5f2f0dacce1d805a0427b950746d3))
* **ui:** M15 Phase 4 — Cancel: targeted live-execution cancel + host stop ([#148](https://github.com/aer-works/aer-flow/issues/148)) ([f1a2361](https://github.com/aer-works/aer-flow/commit/f1a2361b9ed887f17dbdd941f61034cb6bf63203))


### Documentation

* Define the M15 (UI Control Surface) phase plan and prune completed milestones ([#143](https://github.com/aer-works/aer-flow/issues/143)) ([5147b0d](https://github.com/aer-works/aer-flow/commit/5147b0df553bc2ab545c23a6ac9fee244ec0bb5e))
* **ui:** M15 Phase 5 — UI-driven mutation round trips, wired into default CI ([#149](https://github.com/aer-works/aer-flow/issues/149)) ([2f48589](https://github.com/aer-works/aer-flow/commit/2f4858984cd4cc5c58e7f8bce7247a226afce42e))

## [0.9.0](https://github.com/aer-works/aer-flow/compare/v0.8.0...v0.9.0) (2026-07-15)


### Features

* **ui:** M14 Phase 1 — Stack decision + walking skeleton ([#127](https://github.com/aer-works/aer-flow/issues/127)) ([5974df4](https://github.com/aer-works/aer-flow/commit/5974df4e68fde19a0c8029d8aec7771380be9e8f))
* **ui:** M14 Phase 2 — Task & execution projection + change observation ([#128](https://github.com/aer-works/aer-flow/issues/128)) ([d242321](https://github.com/aer-works/aer-flow/commit/d242321320e291f9e96eba56918765c22f5c2934))
* **ui:** M14 Phase 3 — DAG view (snapshot topology + status overlay) ([#134](https://github.com/aer-works/aer-flow/issues/134)) ([7604196](https://github.com/aer-works/aer-flow/commit/7604196be9d6be98818c11de144a2d1bd5304431))
* **ui:** M14 Phase 4 — Artifact lineage + snapshot-vs-template diff ([#135](https://github.com/aer-works/aer-flow/issues/135)) ([6b87cc4](https://github.com/aer-works/aer-flow/commit/6b87cc41984b985a09095c263f8e8e282f65794e))
* **ui:** M14 Phase 5 — Golden-projection determinism gate, wired into default CI ([#136](https://github.com/aer-works/aer-flow/issues/136)) ([053f17e](https://github.com/aer-works/aer-flow/commit/053f17e937fc093b00213fb4ab51048c0c25a062))


### Bug Fixes

* **release:** Seed aer-ui manifest at 0.0.1, not 0.0.0 ([#130](https://github.com/aer-works/aer-flow/issues/130)) ([d8aef7a](https://github.com/aer-works/aer-flow/commit/d8aef7ae891247a13e02aa30e2e086aab188a592))


### Documentation

* Define the M14 (UI Projection) phase plan ([#123](https://github.com/aer-works/aer-flow/issues/123)) ([b6469ed](https://github.com/aer-works/aer-flow/commit/b6469ede34547ce77bc3781bd7333b1b3047a5b9))
* **spec:** Define task-directory discovery (UI spec v0.8) ([#126](https://github.com/aer-works/aer-flow/issues/126)) ([b023d32](https://github.com/aer-works/aer-flow/commit/b023d325a7dc2fe48279c616e1e70ace87bf1324))


### Miscellaneous

* **release:** Version aer-ui as its own release-please package ([#129](https://github.com/aer-works/aer-flow/issues/129)) ([2cb929c](https://github.com/aer-works/aer-flow/commit/2cb929ccebe030439ac1f4d6557dab217d34d30a))

## [0.8.0](https://github.com/aer-works/aer-flow/compare/v0.7.0...v0.8.0) (2026-07-14)


### Features

* **cli:** M13 Phase 1 — Pack aer as a dotnet tool (single-platform) ([#114](https://github.com/aer-works/aer-flow/issues/114)) ([e6be44f](https://github.com/aer-works/aer-flow/commit/e6be44f99e542dbcba29f6327ea35c94ad95eaee))
* **cli:** M13 Phase 2 — Version wiring (release-please → package Version) ([#115](https://github.com/aer-works/aer-flow/issues/115)) ([c970aa5](https://github.com/aer-works/aer-flow/commit/c970aa5dbed9ffa0df1a89aa9b67f126f2d5338a))


### Documentation

* Define the M13 (Distribution) phase plan ([#112](https://github.com/aer-works/aer-flow/issues/112)) ([0d12778](https://github.com/aer-works/aer-flow/commit/0d12778902cc20f1c06f18ec538b317560d13f03))


### Continuous Integration

* **cli:** M13 Phase 3 — Multi-RID native-lib bundling (Windows/Linux/macOS) ([#116](https://github.com/aer-works/aer-flow/issues/116)) ([7180dfd](https://github.com/aer-works/aer-flow/commit/7180dfdc7d36c4b14d1b3edbed11a1c9916a6db0))


### Tests

* **cli:** M13 Phase 4 — Installed-tool round-trip check, wired into default CI ([#117](https://github.com/aer-works/aer-flow/issues/117)) ([102248e](https://github.com/aer-works/aer-flow/commit/102248e53e7a0883d7a0a9e3ce5671425f411db6))

## [0.7.0](https://github.com/aer-works/aer-flow/compare/v0.6.0...v0.7.0) (2026-07-13)


### Features

* **adapters:** M12 Phase 1 — Gemini worker adapter (headless agy CLI) ([#102](https://github.com/aer-works/aer-flow/issues/102)) ([4b944f5](https://github.com/aer-works/aer-flow/commit/4b944f5bc0ab6296ad4d408b5279d61068a80be4))
* **cli:** M12 Phase 2 — aer cancel + Ctrl+C host-stop wiring ([#103](https://github.com/aer-works/aer-flow/issues/103)) ([08b8d7a](https://github.com/aer-works/aer-flow/commit/08b8d7abd9da77c404ded2a5ce0c764407afca05))
* **cli:** M12 Phase 3 — aer decide + supplementary artifact recording ([#105](https://github.com/aer-works/aer-flow/issues/105)) ([e63b823](https://github.com/aer-works/aer-flow/commit/e63b8235db1ad9f05aed058a052a9ee7a6855d44))
* **cli:** M12 Phase 4 — Live mixed-vendor paused run (gated end-to-end) ([#106](https://github.com/aer-works/aer-flow/issues/106)) ([371049b](https://github.com/aer-works/aer-flow/commit/371049b08630fe078628693eecf1ed87732349bb))


### Documentation

* Add agent-delegation strategy to CLAUDE.md ([#104](https://github.com/aer-works/aer-flow/issues/104)) ([f3a78e9](https://github.com/aer-works/aer-flow/commit/f3a78e9e8733a94b7c5206aa7e31aaaea02f5b78))
* Define the M12 (Full Control Surface) phase plan ([#100](https://github.com/aer-works/aer-flow/issues/100)) ([555c8ce](https://github.com/aer-works/aer-flow/commit/555c8ce4d2a27a84ceea26e5b81b2868044d5a64))

## [0.6.0](https://github.com/aer-works/aer-flow/compare/v0.5.0...v0.6.0) (2026-07-12)


### Features

* **adapters:** M11 Phase 1 — Canonical worker-invocation protocol + Aer.Adapters seam ([#91](https://github.com/aer-works/aer-flow/issues/91)) ([4388492](https://github.com/aer-works/aer-flow/commit/43884920d76955ff75b7d4940b2f3531f3e91315))
* **adapters:** M11 Phase 2 — Claude worker adapter (headless claude CLI) ([#92](https://github.com/aer-works/aer-flow/issues/92)) ([b7395bd](https://github.com/aer-works/aer-flow/commit/b7395bd1c33a6527217bfde32ba7e73c40f64771))
* **cli:** M11 Phase 3 — aer run pump (the CLI driver) ([#93](https://github.com/aer-works/aer-flow/issues/93)) ([d4648a1](https://github.com/aer-works/aer-flow/commit/d4648a1cee9e369e2a34d2d6442d4ed5eb7d2631))


### Documentation

* Define the M11 (First Real Run) phase plan and reframe the post-M10 roadmap ([#89](https://github.com/aer-works/aer-flow/issues/89)) ([fa393aa](https://github.com/aer-works/aer-flow/commit/fa393aa05f3b35535d133fc9bd6a00f045df7695))


### Tests

* **cli:** M11 Phase 4 — Live two-step Claude run (gated end-to-end) ([#94](https://github.com/aer-works/aer-flow/issues/94)) ([ce3be15](https://github.com/aer-works/aer-flow/commit/ce3be15268363d8f44dbb8d11f2873b880ab0dee))

## [0.5.0](https://github.com/aer-works/aer-flow/compare/v0.4.0...v0.5.0) (2026-07-12)


### Features

* **flow:** M10 Phase 1 — Cancellation mutation surface: record, validate, non-process targets ([#75](https://github.com/aer-works/aer-flow/issues/75)) ([df31f67](https://github.com/aer-works/aer-flow/commit/df31f671fa8a1a9259fdc42d5738a6f3f5dac5c0))
* **flow:** M10 Phase 2 — Live cancellation delivery: in-flight Core executions ([#76](https://github.com/aer-works/aer-flow/issues/76)) ([196410e](https://github.com/aer-works/aer-flow/commit/196410ece90a02c0bdb82ea12520dfa26985cf61))
* **flow:** M10 Phase 3 — Crash-recovery reconciliation: reading back the Core log ([#80](https://github.com/aer-works/aer-flow/issues/80)) ([8332030](https://github.com/aer-works/aer-flow/commit/833203098219d7a6c8f8f2ae65d04cbb6e2cec05))
* **flow:** M10 Phase 4 — Cancellation + crash-recovery end-to-end integration tests ([#82](https://github.com/aer-works/aer-flow/issues/82)) ([37afd3d](https://github.com/aer-works/aer-flow/commit/37afd3de76e0c51c21f05d4d883b67884d19a0d8))


### Bug Fixes

* **flow:** Order in-flight registry capture before the round's log read ([#83](https://github.com/aer-works/aer-flow/issues/83)) ([55dfbd9](https://github.com/aer-works/aer-flow/commit/55dfbd99ceb605ded552b6a5117ca140bb296ae5))


### Documentation

* Define the M10 (Cancellation & Edge Cases) phase plan ([#73](https://github.com/aer-works/aer-flow/issues/73)) ([d85257f](https://github.com/aer-works/aer-flow/commit/d85257f70c676cba8f626cc869da5d45d58f6f1c))
* **spec:** Define recovery for the orphaned execution crash state (§7) ([#78](https://github.com/aer-works/aer-flow/issues/78)) ([82f80d9](https://github.com/aer-works/aer-flow/commit/82f80d955f01735f79e4a8d7e791f81cd6d2afd7))

## [0.4.0](https://github.com/aer-works/aer-flow/compare/v0.3.0...v0.4.0) (2026-07-12)


### Features

* **flow:** M9 Phase 1 — Pause Engine ([#64](https://github.com/aer-works/aer-flow/issues/64)) ([6aa4ff0](https://github.com/aer-works/aer-flow/commit/6aa4ff0330db7a6f727fc565b5fb512a14212220))
* **flow:** M9 Phase 2 — External Decision Handler: record, validate, Resume/Reject ([#65](https://github.com/aer-works/aer-flow/issues/65)) ([a8e580f](https://github.com/aer-works/aer-flow/commit/a8e580f6a30ca0c5ce09a1d8097a75fa618a0704))
* **flow:** M9 Phase 3 — RetryWithRevision + Supersede + the invalidation cascade ([#66](https://github.com/aer-works/aer-flow/issues/66)) ([3569690](https://github.com/aer-works/aer-flow/commit/35696900a6732bb5033680d495edba6f877f02b7))
* **flow:** M9 Phase 4 — Human worker support (non-process executions) ([#67](https://github.com/aer-works/aer-flow/issues/67)) ([e24f273](https://github.com/aer-works/aer-flow/commit/e24f2738cef966d21e8895e82a29761b7c64dd6e))


### Documentation

* Define the M9 (External Decisions) phase plan ([#62](https://github.com/aer-works/aer-flow/issues/62)) ([f4d9335](https://github.com/aer-works/aer-flow/commit/f4d9335a2db9e40e42f0f6dbe055e3c58f22ff5a))

## [0.3.0](https://github.com/aer-works/aer-flow/compare/v0.2.0...v0.3.0) (2026-07-11)


### Features

* **flow:** M8 Phase 1 — Attempt-history projection ([#51](https://github.com/aer-works/aer-flow/issues/51)) ([5579774](https://github.com/aer-works/aer-flow/commit/55797741cc8a48490bf8f4ae7b29fef1ea8fb452))
* **flow:** M8 Phase 2 — Retry Engine + retry-aware readiness ([#53](https://github.com/aer-works/aer-flow/issues/53)) ([a45ad6d](https://github.com/aer-works/aer-flow/commit/a45ad6d745ca029e20f4d50368ca4ba568f8d375))
* **flow:** M8 Phase 3 — Reactive concurrent dispatch ([#54](https://github.com/aer-works/aer-flow/issues/54)) ([afccaf5](https://github.com/aer-works/aer-flow/commit/afccaf5510b085e188023b7037ed809abe3d5806))


### Documentation

* Define the M8 (Reactive Scheduler) phase plan ([#49](https://github.com/aer-works/aer-flow/issues/49)) ([2e72b91](https://github.com/aer-works/aer-flow/commit/2e72b915ebba0971b1b6f339a6b1b79ee3e6c082))
* Require PR-body verification after create/update calls ([#52](https://github.com/aer-works/aer-flow/issues/52)) ([112ef08](https://github.com/aer-works/aer-flow/commit/112ef08de517b01bac752c6abe414c27b4f1e878))


### Tests

* **flow:** M8 Phase 4 — Fan-out/fan-in + retry end-to-end integration tests ([#56](https://github.com/aer-works/aer-flow/issues/56)) ([15d5adb](https://github.com/aer-works/aer-flow/commit/15d5adb77ff0c508fee275746009cc9f0d1de9de))

## [0.2.0](https://github.com/aer-works/aer-flow/compare/v0.1.0...v0.2.0) (2026-07-11)


### Features

* **core:** Initialize .NET solution and placeholder projects for aer-flow ([541258d](https://github.com/aer-works/aer-flow/commit/541258d75a43ad50fd25a63b8151d7e64c5d512c))
* **flow:** Add the Dependency Resolver for step readiness ([#38](https://github.com/aer-works/aer-flow/issues/38)) ([c460641](https://github.com/aer-works/aer-flow/commit/c4606410d54b3988c9139e2d327e31578948bc1f))
* **flow:** Add the Log Manager for crash-safe flow.jsonl appends ([#35](https://github.com/aer-works/aer-flow/issues/35)) ([8d8a728](https://github.com/aer-works/aer-flow/commit/8d8a7281653b87825a06fa4600b7edc146f22ddb))
* **flow:** Add the State Projector for FlowState reconstruction ([#37](https://github.com/aer-works/aer-flow/issues/37)) ([6601423](https://github.com/aer-works/aer-flow/commit/66014230b0aec75181105dfd4ecbdee6a4341b7a))
* **flow:** Add the Template Parser and Snapshot Binder ([#36](https://github.com/aer-works/aer-flow/issues/36)) ([75090a1](https://github.com/aer-works/aer-flow/commit/75090a1925023150fd5c80f8a776610f8094b30f))
* **flow:** Define the Phase 1 domain model ([#34](https://github.com/aer-works/aer-flow/issues/34)) ([61db539](https://github.com/aer-works/aer-flow/commit/61db539efef509d46167d08d0365b09989d70b46))
* **flow:** M7 Phase 6 — Artifact Manager + Core Dispatcher ([#41](https://github.com/aer-works/aer-flow/issues/41)) ([1a633ce](https://github.com/aer-works/aer-flow/commit/1a633ce4f0794f52730122c2d6f573958ad4ba1d))
* **flow:** M7 Phase 7 — Outcome Classifier + Contract Validator + Mutation Interface ([#43](https://github.com/aer-works/aer-flow/issues/43)) ([97b90a7](https://github.com/aer-works/aer-flow/commit/97b90a79cfebea010a73e6c3eb8efaf37de20350))
* **flow:** M7 Phase 8 — Concurrency Guard + end-to-end integration test ([#44](https://github.com/aer-works/aer-flow/issues/44)) ([eea819f](https://github.com/aer-works/aer-flow/commit/eea819f3a27724ee6af182215ea0aa8ccae950b8))


### Documentation

* Add IMPLEMENTATION_PLAN.md and clean up AER Overview ([638719c](https://github.com/aer-works/aer-flow/commit/638719c4b5909438d8641da50c8759f3434ed2c5))
* Add IMPLEMENTATION_PLAN.md and clean up AER Overview ([45030d7](https://github.com/aer-works/aer-flow/commit/45030d7518485562368ee896c37970656d285502))
* add placeholder README.md for aer-flow ([aaf9860](https://github.com/aer-works/aer-flow/commit/aaf9860980977f10f20f313d53341752a956ac64))
* **ci:** Fix stale spec cross-reference and adopt the system .NET SDK convention ([#27](https://github.com/aer-works/aer-flow/issues/27)) ([6472d0a](https://github.com/aer-works/aer-flow/commit/6472d0a193eb8a72937f1d375d65b167f8ecbf4a)), closes [#20](https://github.com/aer-works/aer-flow/issues/20)
* Land plan refinements that missed PR [#6](https://github.com/aer-works/aer-flow/issues/6) and prune resolved WorkflowTransition references ([#29](https://github.com/aer-works/aer-flow/issues/29)) ([9910dfa](https://github.com/aer-works/aer-flow/commit/9910dfa0d8c5c7135a4ddec626e440ac53f8d8ea)), closes [#28](https://github.com/aer-works/aer-flow/issues/28)
* point IMPLEMENTATION_PLAN.md at [#21](https://github.com/aer-works/aer-flow/issues/21) findings for future adapter work ([#33](https://github.com/aer-works/aer-flow/issues/33)) ([ff3c8cd](https://github.com/aer-works/aer-flow/commit/ff3c8cd4f237808319ad173cc96e61c3edc7ecbb))
* Replace IMPLEMENTATION_PLAN.md with spec-derived plan ([3496d36](https://github.com/aer-works/aer-flow/commit/3496d361f730420f57dfc3015136ee7eb8e368e2))
* **spec:** Define the OutputCondition content-condition language (§4.1) ([#23](https://github.com/aer-works/aer-flow/issues/23)) ([74f7fa8](https://github.com/aer-works/aer-flow/commit/74f7fa87330a55e12a2ae0f61bf8c6e5ce2dfca6)), closes [#16](https://github.com/aer-works/aer-flow/issues/16)
* **spec:** Name quota exhaustion as an open failure-model question (§21) ([#25](https://github.com/aer-works/aer-flow/issues/25)) ([d954a84](https://github.com/aer-works/aer-flow/commit/d954a8477315beb6c8cda62c84547a1d752d29c6)), closes [#18](https://github.com/aer-works/aer-flow/issues/18)
* **spec:** Name the pump problem in §21 with candidate resolutions and v1 intent ([#26](https://github.com/aer-works/aer-flow/issues/26)) ([dfc384c](https://github.com/aer-works/aer-flow/commit/dfc384cf6752f6c7feced3483462ad0b7d089b8f)), closes [#19](https://github.com/aer-works/aer-flow/issues/19)
* **spec:** Record pass-through Environment variables by name only, never value ([#24](https://github.com/aer-works/aer-flow/issues/24)) ([ac217a5](https://github.com/aer-works/aer-flow/commit/ac217a550584b1409be43ec2879618fec220375c)), closes [#17](https://github.com/aer-works/aer-flow/issues/17)
* **spec:** Remove undefined WorkflowTransition event from the event union ([#22](https://github.com/aer-works/aer-flow/issues/22)) ([b82f09c](https://github.com/aer-works/aer-flow/commit/b82f09c25b78be09837bed4c820c45178f7d7091)), closes [#15](https://github.com/aer-works/aer-flow/issues/15)


### Continuous Integration

* Add issues to the AER Roadmap board from project-automation ([#31](https://github.com/aer-works/aer-flow/issues/31)) ([75e7e4c](https://github.com/aer-works/aer-flow/commit/75e7e4cbf59ea462982c2bb148ffc7a4626ab813)), closes [#30](https://github.com/aer-works/aer-flow/issues/30)
* Cache NuGet restore and the aer-core Rust build ([#42](https://github.com/aer-works/aer-flow/issues/42)) ([6988bc6](https://github.com/aer-works/aer-flow/commit/6988bc622443f5393141146cbcaf2ddd972b8c0b))
* fix release-please action configuration ([4b28e9f](https://github.com/aer-works/aer-flow/commit/4b28e9fd0bd12ba9026e5b87780f3b2ccee3aa57))
* fix release-please action configuration ([eaa73a5](https://github.com/aer-works/aer-flow/commit/eaa73a519a6edc1a4bc163b1f234f3f450526e98))
* Fix Ubuntu target and Windows EPERM issue ([f0752cf](https://github.com/aer-works/aer-flow/commit/f0752cf9786549bec69d16ec159c399cd27d430b))
* Skip redundant project item-add on issue close ([#40](https://github.com/aer-works/aer-flow/issues/40)) ([05b7f87](https://github.com/aer-works/aer-flow/commit/05b7f8760fdffec91f888066cd16a51a903c8e1e))


### Miscellaneous

* **setup:** include pixi.lock, gitattributes, and spec files ([a2ea72b](https://github.com/aer-works/aer-flow/commit/a2ea72be2978a8396cda4e2f1f1665783f8f8f62))
* **setup:** initialize aer-flow repository ([801f348](https://github.com/aer-works/aer-flow/commit/801f348f5e2d1a21bbd25cd421cfd91c15b22c4d))
* **setup:** Initialize aer-flow repository with Pixi, CI, and CLAUDE instructions ([f3d9e8e](https://github.com/aer-works/aer-flow/commit/f3d9e8edd0b44c2127291023a057f2fc8a6cdaf3))
* update pixi.lock for new platforms ([b530375](https://github.com/aer-works/aer-flow/commit/b530375c53475417850118e6b3c0eaf8f189807f))
