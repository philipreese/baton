# Baton agent entry point

Read the route that matches the task before acting; do not load every linked document by default.

- **Using or conducting Baton:** read [`docs/agents/invoking-baton.md`](docs/agents/invoking-baton.md)
  before starting a lane, queueing work, or collecting a worker output. Read [`README.md`](README.md)
  when you need the product overview or verb index.
- **Building Baton:** read [`docs/agents/developing-baton.md`](docs/agents/developing-baton.md)
  before editing, testing, or shipping this repository. Consult [`spec/baton.md`](spec/baton.md)
  before making a behavioral judgment; it is the sole behavioral authority.
- **Reading or updating canonical project memory:** first read the shipped `baton memory audit`,
  `add`, `import`, and `sync` contracts in [`README.md`](README.md) and
  [`spec/baton.md` §12](spec/baton.md#12-memory-vendor-memory-roots-and-batons-canonical-store-1852).
  Baton's store contains the canonical records; vendor markdown files are projections and caches.
  `add` and `import` do not sync automatically: automatic projection
  [#2138](https://github.com/philipreese/baton/issues/2138) and the projection index
  [#2139](https://github.com/philipreese/baton/issues/2139) remain open. Documentation links do not
  prove that a vendor loads a projection; that measurement remains [#2177](https://github.com/philipreese/baton/issues/2177).
- **Performing a worker role:** read the role package before beginning:
  [`baton-implement`](src/Baton.Vendors/Skills/baton-implement/SKILL.md),
  [`baton-review`](src/Baton.Vendors/Skills/baton-review/SKILL.md), or
  [`baton-advise`](src/Baton.Vendors/Skills/baton-advise/SKILL.md). Those existing packages are
  the lane instruction registers; the dispatch brief supplies only task-specific work.

