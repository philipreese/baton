# Cost-ledger exports

The cost ledger (spec/baton.md §7) lives at `~/.baton/ledger/<repository>.jsonl` and only on the
operator's machine. The question #1901 exists to answer — *is work through Baton more or less
efficient than a direct session* — has to be answerable from this repository, the way
[`../deepswe`](../deepswe) is, including in the case where the answer is unflattering. So the ledger
is exported here on a **weekly** cadence, by hand or by the conductor.

```
baton ledger export --to benchmarks/ledger [--as-of 2026-09-05]
```

The verb's contract — what one file holds, what its name means, what the run is allowed to read —
is spec/baton.md §7's export paragraph, and none of it is restated here. The one consequence worth
having in front of you while reading the table: a file is a full snapshot taken when the export ran,
so **its name is not a window**, and the `Newest row` column is what says how current it is.

The row schema is spec/baton.md §7's table, not restated here, and `Schema version` is
`LedgerCsv.SchemaVersion` — that section says what it is derived from. All you need to read the table
below: equal versions mean two files' headers are byte-comparable, and a version that changes between
two exports means a field was added or renamed in between.

## Redaction

These files are published from a machine, into a public repository, so they go through the narrowing
spec/baton.md §7 describes — including why it fails closed rather than scrubbing best-effort. As a
reader of what landed here: room identities appear as bare names rather than paths, and `repository` still
carries the public `github.com/owner/repo` handle because that is the ledger's key. The scan is a
pattern set, not a proof: it refuses drive-qualified paths, `/Users/` and `/home/` segments, and the
exporting account's name, so an export that carried one of those does not exist — it failed instead of
landing. Free-text reason cells can still carry other strings (a UNC share, a relative path, another
host's account name); read them before trusting a fresh export, and widen the pattern set when one slips.

## Exports

<!-- baton ledger export: table begins -->
| Export | Schema version | Rows | Newest row (endedAt, UTC) |
|---|---|---|---|
| [`2026-09-05.csv`](2026-09-05.csv) | `62-8d4b80ea0edc` | 89 | 2026-09-06T03:27:55Z |
<!-- baton ledger export: table ends -->

`2026-09-05.csv` is the **baseline**: taken before `baton ledger backfill` (#1901 C2) had been run
against the store and before any comparator run (#1903), so it is what "Baton's cost, as recorded at
the time" looked like with nothing reconstructed. Every row in it is a `baton-execution` row written
at settle; no row carries `pr`, so `derive.py`'s per-PR medians are empty over it by construction and
its whole population is reported as unattributed. That is the honest pre-backfill reading, and the
next export is where the joins appear.

## Derivation

[`derive.py`](derive.py) reads **the committed CSVs only** — never `~/.baton` — and writes
`medians.md` and `medians.json` beside them.

Its docstring is the only place the metrics, the three cuts and the absence handling are defined; do
not look for them twice. Two things that page cannot say for you. First: the cut worth caring about
is the arm — the dispatch `label` #1901 C1 stamps — because without it there is no A/B, only a
before-and-after. Second: medians, not means, since one arrested or runaway lane moves a mean and
says nothing about the typical case. And read the population block before any number under it. A
median over four PRs is a number; it is not evidence.

`python benchmarks/ledger/derive.py --check` exits 1 when the committed outputs differ from a fresh
derivation, and `--selftest` proves that check discriminates by sabotaging a scratch copy. Both run
under `pixi run gates` (`ledger-derived-check`, `ledger-derived-check-selftest`).

## Reading rules

- A row is an **attempt**, not an outcome. A retry mints a fresh execution id and is its own row, so
  summing a PR's rows is the cost of reaching that PR including everything that failed on the way —
  which is the number the efficiency question wants, and not the number a per-attempt average gives.
- The two money columns are **estimates** — API list-price equivalent and a modelled plan meter.
  Neither is an invoice or subscription spend. `derive.py` deliberately medians tokens and time
  rather than dollars for that reason.
- A `github-backfill` row records a merged PR that nothing ran for: no vendor, no tokens, no
  estimate. It is in the population for the `pr` join and contributes nothing to a token median.
- `completeness: partial` means the stream the tokens were read from was truncated. Its numbers are
  floors, not measurements.
