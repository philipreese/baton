# Baton

Baton is a vendor-neutral worker-room engine an agent harness drives: it dispatches vendor CLI
agents (`claude`, `agy`) as workers inside durable, auditable rooms, and reports completion through
a machine contract.

Built in .NET, it parses a declared workflow, hands each step's work to a Worker, and folds the result back into the run.

## Documentation

**Start here:** [`spec/baton.md`](spec/baton.md) — the spec, and the sole register for what the
system is: the dispatch unit, the completion contract, gates, Fleet Glass observability, the
narrowed daemon, and bindings/permissions. If this README and the spec disagree, the spec wins.

- [Agent Instructions](CLAUDE.md) - Architectural rules and development workflows for AI agents.
- [Invoking Baton](docs/agents/invoking-baton.md) - For an agent whose job is to *run* a Baton lane
  against some other repo rather than develop Baton: the invocation that works today, a complete
  workflow+bindings pair, and the edges it will hit.
- [Vendor capabilities](docs/vendor-capabilities.md) - What each worker CLI can actually enforce and
  ask, every claim observed rather than assumed.
- [Runbooks](docs/runbooks/) - Manual, key-gated operational procedures not covered by CI.

## Verbs

| Verb | What it does |
|---|---|
| `baton run` / `baton dispatch` / `baton redispatch` | Start a workflow room, or rerun a terminal one with an amended brief. |
| `baton cancel` / `baton decide` / `baton resolve` / `baton resume` / `baton supply` | Mutate an already-started room — cancel a lane, record a pause decision, resolve a captured response, resume a stalled pump, supply a supplementary output. |
| `baton status` | Read-only projection of a room's current state. |
| `baton keep` / `baton unkeep` | Mark/unmark a room exempt from `RoomRetentionSweep`'s artifact pruning. |
| `baton deliver <file> [--title <text>] [--room <room-dir>]` (`--room-dir` also accepted) | Deliver an orchestrator artifact into a room (defaults to standing conductor room) so it reaches the Fleet Glass inbox. |
| `baton room delete <room-dir> [--keep-deliverables] [--force]` | Remove one room for good: its directory, its `room-registry.jsonl` lines, and (best-effort) a deliverables tombstone. Refuses a non-terminal room unless `--force` — see `spec/baton.md` §8. |
| `baton rooms prune --terminal [--older-than <days>] [--state <state>] [--dry-run] [--yes]` | Batch form of `room delete`, plus unconditional registry hygiene (dedupe, drop lines whose directory is gone). Lists candidates by default; `--yes` actually deletes. |
| `baton templates` | List the built-in workflow template catalog. |
| `baton ledger [<room-dir>] [filters] [--format text\|json\|csv] [--drill]` | Read the repository's cost ledger: per-vendor token and estimate subtotals, then a labelled all-vendor estimate, for a room or the whole fleet. `baton ledger --rebuild` is the separate burn-ledger rebuild (`spec/baton.md` §7). |
| `baton memory audit [--format text\|json]` | Read-only inventory of this machine's Claude memory roots (live and archived), each mapped to a canonical repository identity, with duplicate/orphan/stale/no-provenance/ambiguous findings. Writes nothing — see `spec/baton.md` §12. |
| `baton mcp` / `baton daemon` | The stdio MCP server workers connect to (`fleet_status`, `yield`, `memory-edit-proposal`, `promote-artifact`, `room_detail`), and the narrowed background daemon (`spec/baton.md` §7). |

`spec/baton.md` is the authority on every verb's exact contract — this table is an index, not a
restatement.

## Fleet Glass push notifications

`tools/fleet-glass/pusher.py` can push terminal/attention-worthy fleet events (a failed lane, a
stalled room, a pusher-level anomaly) to a phone via [ntfy](https://ntfy.sh) — see
[`spec/baton.md`](spec/baton.md) §6 for the full behavior (tier table, quiet hours, dedup). To
enable it, copy `tools/fleet-glass/pusher.config.example.json` to `pusher.config.json` and set:

| Key | Purpose |
|---|---|
| `ntfy_topic` | The ntfy topic to push to. **Unset or blank disables the feature entirely** (one startup log line, no error). |
| `ntfy_server` | ntfy server base URL; defaults to `https://ntfy.sh`. A self-hosted instance requiring auth also needs `ntfy_token` in `secrets.local.json` (see `secrets.local.example.json`) — never in `pusher.config.json`. |
| `ntfy_quiet_hours` | Optional `{"start", "end", "timezone"}` (24h `HH:MM`, wrapping past midnight allowed; timezone defaults to `America/New_York`). Suppresses every tier below `urgent`; omit for no quiet hours at all. |
| `ntfy_state_file` | Where the dedup ledger is persisted; defaults to `ntfy-state.local.json` beside `pusher.py`. |

Event type → ntfy priority (`NTFY_EVENT_TIERS`, spec/baton.md §6): `lane_failed` → urgent,
`zombie_detected` / `pusher_anomaly` → high, `lane_succeeded_with_warnings` → default.

Fleet Glass (`tools/fleet-glass/glass.html`) and `fleet_status` also show each authenticated vendor's
own headless `/usage` report — session/weekly percent used, reset instant, and the vendor's own
machine-local caveat — as **advisory runway**, harvested by the daemon on a slow, lane-gated cadence
(`spec/baton.md` §6, "`vendors[]`", issue #1391). That reporting slice gates nothing itself; the
counters it harvests are what `baton dispatch`'s **runway hold** reads (#1848) before admitting new
work — held at week ≥85% or session ≥90% per vendor, bypassable only with
`--override-runway "<reason>"`, which is recorded. `spec/baton.md` §7, "Runway hold (#1848)", is the
contract.

Each window row also carries two derived cells (#1746): the **burn rate** in percentage points per
hour and the **minutes to exhaustion** at that rate, both computed by the daemon's projection from a
short ring of recent harvests rather than by the page. Either reads `unknown` when absent — a rate
needs two harvests and disappears again when the vendor's window rolls over, and no exhaustion
estimate exists without a positive rate. `spec/baton.md` §6's `windows[]` table states the absence
rules in full.

## Vendor authentication

Baton does not authenticate to any model provider. It spawns the vendor's own first-party CLI
(`claude`, `agy`) as a subprocess, and that CLI uses whatever login the operator already established
on their own machine.

**Baton never reads, copies, forwards, or stores a vendor credential** — no API keys, no OAuth tokens,
no access to the OS credential store, and it never places a credential into a config directory. This
is an enforced invariant, not an intention: see
[`VendorCredentialIsolationTests`](tests/Baton.Architecture.Tests/VendorCredentialIsolationTests.cs).

Baton is a personal tool. It is not offered as a product or a service, and it does not provide,
resell, or proxy access to any provider — you bring a CLI you have already signed into yourself.
Each vendor CLI remains subject to its own provider's terms, between the operator and that provider.

## Prerequisites

- **[pixi](https://pixi.sh)** — task runner.
- **.NET 10 SDK** — install separately (not managed by pixi):
  - Windows: `winget install Microsoft.DotNet.SDK.10`
- Root `global.json` pins `dotnet test` to Microsoft.Testing.Platform (not VSTest) on this SDK — no
  contributor action needed, but it means a bare `dotnet test` (outside `pixi run test`) now takes
  MTP's own flags, not VSTest's (`--filter` no longer works for xUnit; see xunit's [MTP
  docs](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform)).

## Quickstart
```bash
# Install the Pixi environment
pixi install

# Run tests
pixi run test

# Format code
pixi run fmt
```

## Installing `baton`

`baton` is distributed as a self-built, unpublished `dotnet tool` — there is no public NuGet feed;
a single-developer project doesn't need one.

**First install, or refreshing an already-installed tool: `pixi run tool-refresh`.** Installs side-by-side
per-commit versions under `~/.baton/tools/<sha>` with a lightweight PATH launcher in `~/.dotnet/tools`
resolving `current` at process start — see [`spec/baton.md`](spec/baton.md) §8 (*Installation and versioning*)
for the authoritative directory structure, launcher details, and automatic pruning policy.

`pixi run verify-pack` runs the underlying install → run → uninstall round trip end to end against a
trivial fixture (no live vendor call) — it's the same check CI runs unattended on every push.

