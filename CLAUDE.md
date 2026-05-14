# Working in this repository

This file is read first by Claude Code (and any other agent following the
convention). It tells you what this project is, what state it is in, and
the conventions you must follow when contributing.

## What this is

LearnStack is a **multi-tenant core platform for building education
products** — not a single LMS. It powers different learning brands,
landing pages, catalogs, portals, and vertical education products. The
first vertical (online English learning) is planned; more are expected.

## What state this is in

**Pre-implementation.** This repository currently contains only
documentation: architecture, decisions, engineering standards, and a
phased roadmap. There is no application code yet. Every code reference
in the docs (e.g. `LearnStack.Modules.Education.Application`,
`ILiveClassProvider`, `ITenantSearch`) describes intended shape, not
existing files.

## Where to start

For any task, read in this order:

1. [README.md](README.md) — direction at a glance.
2. [docs/architecture/01-platform-vision.md](docs/architecture/01-platform-vision.md) — what we build and why.
3. [docs/architecture/05-mvp-scope.md](docs/architecture/05-mvp-scope.md) — what is in / out / deferred.
4. [docs/roadmap/README.md](docs/roadmap/README.md) — phased plan with explicit dependencies.
5. [docs/standards/00-principles.md](docs/standards/00-principles.md) — the beliefs every other standard descends from.
6. [docs/glossary.md](docs/glossary.md) — terminology; the single source of truth for project-specific terms.

## Documentation layout

| Directory | Purpose | Mutability |
|-----------|---------|------------|
| `docs/architecture/` | Conceptual descriptions of what we are building. Numbered `NN-topic.md` linearly. | Editable as the system evolves. |
| `docs/decisions/` | ADRs — one-time decisions with status, context, decision, consequences. Redirect / superseded ADRs live under `_redirects/`. | Accepted ADRs are immutable except for dated Amendments. |
| `docs/standards/` | Engineering rules (`NN-topic.md`, 00 – 19). Each anchored standard carries a `**Derives from:** ADR-NNNN` header. | Editable as the team learns; standard changes cite an ADR. |
| `docs/roadmap/` | Phased plan (`phase-NN-topic.md`, 00 – 11 with 02a/02b and 08a/08b/08c splits). | Editable per phase. |
| `docs/glossary.md` | Terminology source of truth. | Editable; new term goes here first, then used. |

## Hard rules

- **English** is the documentation language ([ADR 0007](docs/decisions/0007-documentation-language-and-conventions.md)). The Turkish-facing UI of any tenant is separate.
- **Mermaid** for diagrams in fenced ` ```mermaid ` blocks. Diagrams must remain readable in text form (titles + bullet fallbacks) for renderers that don't support Mermaid.
- **Single source of truth.** Each piece of knowledge lives in exactly one place. The glossary holds terms. ADRs hold decisions. Standards hold ongoing rules. Architecture docs hold conceptual descriptions. Roadmap holds phases. Do not duplicate.
- **ADR numbers are sequential and never reused.** Superseded ADRs become redirect stubs under `decisions/_redirects/`. Adding a new ADR uses the next free number.
- **Standards changes cite an ADR.** A new standard rule or a change to an existing one is paired with an ADR when the rule is non-trivial.
- **Modular monolith with four cross-module mechanisms** ([ADR 0010](docs/decisions/0010-cross-module-communication.md)): application contract, intra-module domain event, integration event via outbox, read-model projection. No fifth.
- **Tenant isolation is defense-in-depth from day one** ([ADR 0003](docs/decisions/0003-tenant-isolation-defense-in-depth.md)): tenant context + EF query filter + PostgreSQL RLS + architecture tests.
- **Self-hosted infrastructure preferred** for Keycloak (auth), LiveKit OSS (live classroom), MinIO (object storage), Meilisearch (search). See ADRs 0004, 0005.
- **The core platform stays generic.** Vertical-specific business rules (CEFR, exam curricula, ...) live in vertical modules, never in core modules. See [ADR 0011](docs/decisions/0011-extension-points.md) and [docs/architecture/06-extension-model.md](docs/architecture/06-extension-model.md).
- **Provider adapters everywhere.** Payments, auth, storage, search, live classroom, notifications all sit behind interfaces. No SaaS lock-in in `Domain` or `Application`.

## Conventions when editing docs

- **Short and declarative** — heading + bullets over essay paragraphs.
- **Present tense decisions** ("LearnStack uses ..."), not future tense ("LearnStack will use ...").
- **Cross-link liberally** — to glossary, related architecture docs, standards, ADRs. Use relative paths.
- **TODO comments** include a date and an owner: `// TODO(YYYY-MM-DD, @owner): refactor when X lands`.
- **Don't redefine glossary terms in other docs**; link to them.

## Conventions when editing code (future)

Once application code lands, the engineering standards under
`docs/standards/` are the authority for every PR. The most load-bearing
rules:

- C# / .NET 10, strongly-typed ids, records, MediatR pipeline, EF Core
  with per-module `DbContext` ([02](docs/standards/02-backend-coding.md), [05](docs/standards/05-database.md)).
- TypeScript strict + Next.js App Router; one frontend app under
  `frontend/` with route segments ([03](docs/standards/03-frontend-coding.md), [07](docs/standards/07-frontend-architecture.md)).
- REST + RFC 7807 Problem Details + cursor pagination + idempotency
  keys + ETag concurrency ([04](docs/standards/04-api-design.md)).
- OpenTelemetry + correlation id end to end ([10](docs/standards/10-observability.md)).
- WCAG 2.2 AA across all surfaces ([16](docs/standards/16-accessibility.md)).
- Audit-coverage matrix required per module ([18](docs/standards/18-audit-coverage.md)).
- Permission keys `{module}.{resource}.{action}` with closed action set ([19](docs/standards/19-permissions.md)).
- Zero-tolerance review blockers enumerated in [17](docs/standards/17-code-review.md).

## Commit conventions

- Conventional Commits style: `type(scope): subject`.
- Subject in imperative mood; ≤ 72 chars.
- For doc-only commits: `docs(scope): ...` where scope is one of
  `architecture`, `decisions`, `standards`, `roadmap`, or omitted for
  cross-cutting changes.
- Commits made with AI assistance carry the trailer
  `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`.

## Things to never do

- Edit an Accepted ADR's decision section. Write a new ADR that
  supersedes the old one instead.
- Introduce a fifth cross-module communication mechanism.
- Add vertical-specific code (CEFR, exam, English placement) to a
  core module.
- Reuse an ADR number.
- Add an architecture or standard document whose existence makes one
  of the existing documents ambiguous about ownership; if a topic
  needs more space, expand the existing doc rather than splintering.
- Mention a feature as "deferred to a later phase" without naming the
  phase that owns it.

## Where to look when stuck

- Term means what? — [docs/glossary.md](docs/glossary.md).
- Why was this decided? — `docs/decisions/`. Each ADR carries context.
- What rule applies to my change? — `docs/standards/`. The index is in
  [docs/standards/README.md](docs/standards/README.md).
- What's next? — [docs/roadmap/README.md](docs/roadmap/README.md).
- What's the shape of the live classroom / auth / search / etc.? —
  the corresponding `docs/architecture/NN-topic.md`.
