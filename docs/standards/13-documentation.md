# 13 — Documentation Standards

**Status:** Active
**Derives from:** [ADR 0007 — Documentation Language and Conventions](../decisions/0007-documentation-language-and-conventions.md).

How LearnStack writes, organizes, and maintains documentation.

## Language

LearnStack documentation is written in **English**. See [ADR 0007](../decisions/0007-documentation-language-and-conventions.md).

The product-facing UI for tenants is independent and can be in any language.

## Layout

```
docs/
  architecture/   NN-topic.md   core architectural concepts
  decisions/      NNNN-topic.md ADRs
    _redirects/   redirect stubs for superseded ADRs
  roadmap/        phase-NN-topic.md   (with phase-NNa / phase-NNb splits for parallel tracks)
  standards/      NN-topic.md   engineering rules
  runbooks/       *.md           operations procedures (Phase 11+)
  modules/        NN-module/    per-module specs (created with the first module impl)
  glossary.md     terminology
```

The `learnstack-hub` repository follows a similar `docs/` layout for its own concerns;
cross-repo references use absolute URLs.

## Local-Only Directories (not committed)

Some working directories live on disk but are **gitignored** and never travel with the
repository. Today:

| Path | Purpose | Notes |
|------|---------|-------|
| `docs/analysis/` | Scratchpad for exploratory analyses, prior-art studies, redesign master plans, vendor reviews, and other research artefacts that informed decisions but are not themselves part of the spec. | Gitignored. Contents are personal / team-internal context. |

**Rule:** committed files **must not reference paths under `docs/analysis/`**. That
means:

- No `docs/analysis/...` paths in committed Markdown — not as relative links, not in
  prose ("see `docs/analysis/foo.md` §2"), not in code-fenced examples.
- No `docs/analysis/...` paths in commit messages, PR descriptions, code comments, or
  docstrings.
- No `docs/analysis/...` paths in tests, configuration, or generated assets.

The reasoning is that local analyses are a transient working surface: contents can be
rewritten, renamed, or deleted on a whim, and broken links from committed docs would
silently rot. The committed corpus must stand on its own.

When committed content needs to reference *the outcome* of an analysis (e.g. a vendor
comparison that motivated a decision), capture the outcome in the appropriate place
(ADR, architecture doc, standard, glossary) — not by linking to the analysis. Prose
attribution of the form "prior-art X demonstrated Y" is fine and encouraged; the path
reference is what is forbidden.

## Required Docs

The following are kept current and treated as code:

- Architecture docs.
- ADRs.
- Standards.
- Roadmap.
- Glossary.
- Local development setup (in `README.md`).
- API OpenAPI spec (generated).

## Doc Types

| Type | Purpose | Mutability |
|------|---------|-----------|
| Architecture (`architecture/`) | Conceptual descriptions of what we are building | Editable as the system evolves |
| ADR (`decisions/`) | A one-time decision with status, context, decision, consequences | Immutable after acceptance, except for typo fixes |
| Standard (`standards/`) | Ongoing engineering rules | Editable as the team learns |
| Runbook (`runbooks/`) | Operational procedures | Editable; review quarterly |
| Roadmap (`roadmap/`) | Phased plan | Editable per phase |

## Anchored Standards

Every standard begins with a `**Derives from:**` line on the second line that names its authority. The authority is one of:

- An ADR — preferred when one exists (`[ADR 0010 — Cross-Module Communication](../decisions/0010-cross-module-communication.md)`).
- An authoritative external standard — when LearnStack adopts an external rule directly (`WCAG 2.2 AA`).
- One or more sibling standards — when the rule lives at the standards layer and no ADR is warranted (`[11-security.md § Audit Log](11-security.md)`).

Multiple sources can be listed comma-separated. A standard with no authority means the rule is folklore and either needs an ADR drafted or is a documentation bug.

## ADRs

Every ADR has:

```markdown
# ADR NNNN: <Title>

## Status
Proposed | Accepted | Superseded | Deprecated

## Decision Drivers
<bullet list of the forces / constraints / goals that made a decision necessary>

## Considered Options
<short numbered list — at least the chosen one and one rejected alternative>

## Decision
<the decision, present tense>

## Context
<why the decision was needed; deeper alternatives analysis>

## Consequences
<positive and negative implications>

## Amendments
<dated, append-only clarifications that do not change the Decision>
```

Rules:
- Numbered sequentially (`0001`, `0002`, ...). Never reused.
- Accepted ADRs are immutable except for typo fixes and dated Amendments.
- A new decision that supersedes an old one is a **new ADR**; the old one is marked
  `Superseded by ADR-NNNN` and reduced to a redirect stub. The full stub lives in
  `decisions/_redirects/` when the file is otherwise empty.
- Required for: technology choices, security-sensitive decisions, persistence strategy
  changes, provider decisions, cross-module contract changes, deployment-model
  changes, anything expensive to reverse.

## Mermaid Diagrams

- Use fenced ` ```mermaid ` code blocks.
- Avoid ASCII art when Mermaid is clearer.
- Diagrams that don't render still have a text description so the document is readable without rendering.

Common diagram types:
- `flowchart` for architecture and decision flows.
- `sequenceDiagram` for request / event flows.
- `classDiagram` rarely (DSL diverges from C#).
- `erDiagram` for entity relationships.
- `stateDiagram-v2` for lifecycle states.

## Style

- Tight, declarative prose; heading + bullet list preferred over essay paragraphs.
- One H1 per file; H1 matches the filename's intent.
- Decisions in present tense ("LearnStack uses ...") not future tense.
- Avoid filler ("It is important to note that ...").
- Use tables when comparing options or listing rules.
- Code samples short, compilable in principle, using project conventions.
- Cross-link liberally: glossary terms, related architecture docs, standards, ADRs.

## Glossary

- The glossary (`docs/glossary.md`) is the single source of truth for project-specific terms.
- Other docs do not redefine terms; they link to the glossary.
- New term: add to glossary first, then use.

## Code Comments

- Comment the *why*, not the *what*.
- Don't restate the code in prose.
- Public APIs across module boundaries get XML doc comments.
- TODO comments include a date and an owner: `// TODO(2026-05-14, @cemil): refactor when X lands`.
- Multi-paragraph comments are a smell; either the code is wrong or it deserves its own doc.

## API Documentation

- OpenAPI is generated, not handwritten.
- Endpoint descriptions, parameter docs, and response schemas are populated from XML comments and attribute metadata.
- The published OpenAPI is the contract.

## Per-Module Specifications

When a module reaches "design stable, ready to implement", it gets a spec under `docs/modules/<module>/` (this directory is created with the first module spec; it does not exist during pre-implementation) containing **at minimum**:

- **Overview** — what the module owns, what it does not.
- **Entity-relationship diagram** (Mermaid `erDiagram`) — aggregate roots, owned entities, cross-module id references.
- **State diagram** (Mermaid `stateDiagram-v2`) — for entities with non-trivial lifecycle (CourseVersion publish state, Enrollment state, LiveSession state, etc.).
- **Sequence diagram** (Mermaid `sequenceDiagram`) — for at least the primary write use case and the primary integration-event flow.
- **Component diagram** — modules / packages / external systems the module talks to.
- **Integration-event catalogue** — published events with versioned schema and consumer list.
- **Permission matrix** — Resource × Action, role defaults. See [19-permissions.md](19-permissions.md).
- **Audit coverage matrix** — MUST/SHOULD operations per resource. See [18-audit-coverage.md](18-audit-coverage.md).
- **Performance budget** — read/write latency targets specific to the module.
- **Risks and open questions**.

A module spec without these sections is not "done"; reviewers block merges that skip required diagrams.

## ADR Amendments

Accepted ADRs are otherwise immutable, but **dated Amendments** are allowed at the bottom of the file for clarifications that do not change the decision:

```markdown
## Amendments

### YYYY-MM-DD — Clarification

…short note about what was previously ambiguous and how it should be read now.
```

Amendments must not change the Decision section. If the decision itself changes, write a new ADR that supersedes the old one.

## When to Update Documentation

| Change | Doc to update |
|--------|---------------|
| New module | Module boundaries doc + glossary |
| New provider adapter | Extension model doc + 20-infrastructure-stack.md if it touches Dapr / APISIX / Hub |
| New ADR-worthy decision | New ADR (with Decision Drivers + Considered Options) |
| New cross-module contract | Cross-module contracts doc |
| Schema migration | Inline in code; reference standards doc if a new pattern |
| New translatable content type | i18n strategy doc |
| New Tenant Customization aggregate | [32-tenant-customization-model.md](../architecture/32-tenant-customization-model.md) + glossary |
| New feature key or limit key | [21-feature-flags.md](../architecture/21-feature-flags.md) catalog + matching `FeatureKeys` / `LimitKeys` entry |
| New Hub endpoint | New ADR (the surface is a cross-repository contract; see [ADR-0034](../decisions/0034-hub-contract-surface-invariant.md)) + [24-learnstack-hub.md](../architecture/24-learnstack-hub.md) |
| Standards rule change | The standard itself + an ADR if non-trivial |

## Documentation Reviews

- Architecture docs and standards reviewed in normal PR flow.
- Quarterly review of the standards directory for drift.
- Stale docs (out of sync with code for > 30 days) tracked as bugs.

## Forbidden

- Documentation describing imagined behavior (write docs after the code, or alongside it).
- Documenting "what the code does" line by line.
- Multiple definitions of the same term across docs.
- Mermaid blocks with no text fallback.
- Editing accepted ADRs to change the decision (write a new ADR).
