# ADR 0007: Documentation Language and Conventions

## Status

Accepted

## Decision

LearnStack documentation is written in **English**. Diagrams use **Mermaid** in fenced ` ```mermaid ` code blocks. Engineering rules live under `docs/standards/`. Architectural decisions are captured as ADRs under `docs/decisions/`. Terminology is centralized in `docs/glossary.md`.

## Context

LearnStack is built by a multi-vertical platform team and intended to host multiple education products over time. Documentation must be:

- Searchable and skimmable for engineers joining the project.
- Reviewable by external collaborators or future contributors.
- Renderable inline in any common Markdown viewer (GitHub, VS Code, IDEs) and Mermaid-aware platforms.
- Tooling-friendly: AI assistants, doc generators, and linters all default to English.

The first vertical product is Turkish-facing (English-learning platform for Turkish learners), but the engineering corpus is independent of the consumer language.

## Documentation Layout

```
docs/
  architecture/   NN-topic.md   core architectural concepts
  decisions/      NNNN-topic.md ADRs
  roadmap/        phase-NN-topic.md
  standards/      NN-topic.md   engineering rules
  glossary.md     terminology
```

## Style Rules

- Tight, declarative prose; heading + bullet list preferred over essay paragraphs.
- One H1 per file; the H1 matches the filename's intent.
- Decisions stated in present tense ("LearnStack uses ...") not future tense.
- Avoid filler ("It is important to note that ..."). Just state the fact.
- Mermaid for diagrams. Avoid ASCII art when Mermaid is clearer.
- Cross-link liberally: link to glossary terms, related architecture docs, standards docs, and ADRs.
- Code samples are short, compilable in principle, and use the project's actual conventions (strongly-typed ids, etc.).

## Consequences

- Existing Turkish-language draft docs are translated as they are revisited; new docs are English-first.
- The product-facing UI for tenants is independent and may be in any language.
- Glossary is the source of truth for project-specific terms; other docs do not redefine them.
- An ADR is required whenever an architectural decision affects more than one module or sets a long-lived rule.
- The Mermaid syntax is part of the doc; renderers that do not support it must not break the document — text fallback always present.
