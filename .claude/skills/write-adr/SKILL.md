---
name: write-adr
description: >
  Author a new Architecture Decision Record using the LearnStack template (Decision
  Drivers + Considered Options) and reserve its number. USE FOR: capturing a
  one-time architectural decision that other docs will cite, picking between two or
  more incompatible technology / pattern choices, recording the reason a rule is
  the way it is. DO NOT USE FOR: changing an existing Accepted ADR's decision
  (write a new ADR that supersedes it), correcting a false statement in an
  Accepted ADR's body (that is an erratum or a replacement under
  [ADR-0041](../../../docs/decisions/0041-correcting-false-statements-in-accepted-adrs.md),
  not a new ADR), recording day-to-day implementation choices
  (those go in code review / commit messages), or research notes (use
  `docs/analysis/`, which is gitignored).
---

# Writing a LearnStack ADR

## Purpose

Record a one-time architectural decision in `docs/decisions/NNNN-<topic>.md` using
the project's template so the decision is traceable, immutable, and properly
cross-linked.

## When to use

- A technology choice between two or more incompatible options.
- A pattern adoption that other code must comply with.
- A change to a cross-module contract.
- Anything in [13-documentation.md § ADRs](../../../docs/standards/13-documentation.md)'
  "Required for" list.

## When not to use

- Changing the decision an Accepted ADR records. Write a **new** ADR that
  supersedes it instead.
- Correcting a statement in an Accepted ADR's body that was false when it entered
  the record. That is an inline erratum, or in-place replacement where the text is
  a canonical artifact for reuse — see
  [13-documentation.md § Correcting and Amending ADRs](../../../docs/standards/13-documentation.md).
- Implementation-level choices that fit in a commit message.
- "We might do X someday" — defer until the decision is real.
- Tenant-customization rule changes that are data, not code.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Decision topic | Yes | Short title; will appear in filename and ADR header. |
| Reserved number | No | Check [decisions/README.md § Open ADR Drafts](../../../docs/decisions/README.md) first — your topic may already have a reserved number. |
| Forces / constraints | Yes | The Decision Drivers you'll list. |
| Alternatives considered | Yes | At least the chosen option and one rejected. |

## Workflow

### Step 1: Pick the number

1. Open [decisions/README.md](../../../docs/decisions/README.md).
2. If your topic is in the **Open ADR Drafts** table, use the reserved number.
3. Otherwise, pick the next free number after the highest in the Active list.
4. **Never reuse a superseded number.** Superseded ADRs become redirect stubs and
   the original number is permanently retired from circulation.

### Step 2: Create the file

Create `docs/decisions/NNNN-<topic-kebab>.md`. Use this skeleton (from the template
the project follows — see existing ADRs 0014–0022 for canonical examples):

```markdown
# ADR-NNNN: <Title>

## Status

Proposed | Accepted | Superseded | Deprecated   <!-- choose one -->

## Decision Drivers

- <Force / constraint / goal #1>
- <Force / constraint / goal #2>
- <…>

## Considered Options

1. **Option A — <short name>** (chosen)
2. **Option B — <short name>** (rejected because …)
3. **Option C — <short name>** (rejected because …)

## Decision

LearnStack <does X / uses Y / adopts Z>. <One paragraph stating the decision in
present tense.>

## Context

<Deeper background, prior-art references, why this surfaced now.>

## Consequences

### Positive

- <…>

### Negative

- <…>

### Neutral

- <…>

## Implementation notes

- Phase 0Xx — <what lands>.
- Phase 0Yy — <what lands>.

## Architecture tests

<List the non-skippable tests this ADR introduces or relies on, if any.>

## References

- Related ADRs (linked by number).
- Related architecture docs (`docs/architecture/NN-topic.md`).
- Related standards (`docs/standards/NN-topic.md`).
- Prior-art (Nexora paths if applicable — see
  [13-documentation.md § Local-Only Directories](../../../docs/standards/13-documentation.md)
  for the rule against `docs/analysis/` references).
```

**No `## Amendments` heading at authoring time.** The corpus convention is a
biconditional — every ADR with the heading has at least one entry, and every ADR
without amendments omits it entirely. Append the section with the first dated
clarification, which must not change the Decision section; one recording a
correction names what was wrong, how it was shown wrong, and every carrier
changed.

### Step 3: Cross-link

After writing the ADR:

1. Add an entry to the **Active ADRs** table in
   [decisions/README.md](../../../docs/decisions/README.md).
2. Remove the topic from the **Open ADR Drafts** table if it was reserved.
3. Update any standard that derives from this decision — set its `Derives from`
   header to include this ADR.
4. Update relevant architecture docs to cite the new ADR.
5. If the decision supersedes an older ADR, **retain the older ADR in place** —
   edit its status to `Superseded by ADR-NNNN`, add a short "why superseded"
   note linking forward, and leave the original Decision section intact for
   history. (Pattern: ADR-0011 → ADR-0018.) The `decisions/_redirects/`
   directory is **only** for the early-draft renumbering stubs
   (0004 / 0005 / 0006 — see
   [decisions/README.md § Redirect ADRs](../../../docs/decisions/README.md));
   never move a superseded ADR there.

### Step 4: Validate

- Ensure the **Decision Drivers** section is concrete (constraints, deadlines,
  stakeholder asks — not aesthetic preferences).
- Ensure the **Considered Options** section lists at least the chosen option **and**
  one rejected alternative, each with a reason.
- Ensure the **Decision** is one short paragraph in present tense.
- Ensure the **Status** is `Proposed` (flip to `Accepted` only when the team agrees).
- Ensure no `docs/analysis/*` paths appear in the body
  ([rule](../../../docs/standards/13-documentation.md)). Cite Nexora repo paths if
  you're referencing prior-art.

## Common pitfalls

- **Future tense Decision.** Use "LearnStack uses…" not "LearnStack will use…". An
  Accepted ADR is the present state.
- **Vague Drivers.** "We want flexibility" is not a driver; "Self-Hosted customers
  must run air-gapped" is.
- **One-option Considered Options.** Forces the author to compare, even briefly,
  against at least one rejected alternative. If you cannot name a rejected option,
  the decision probably isn't ADR-worthy.
- **Rewriting an Accepted ADR's Decision.** Add an Amendment (date +
  clarification), or write a superseding ADR. The two bounded corrections
  [ADR-0041](../../../docs/decisions/0041-correcting-false-statements-in-accepted-adrs.md)
  permits do not reach the decision itself: where a correction would change how
  the decision *reads* rather than what it *names*, the body stays and the
  amendment carries the reading.
- **Reusing a number.** ADR numbers are sequential and immutable per
  [decisions/README.md](../../../docs/decisions/README.md). The rule is enforced
  by code review, not by an architecture test today; do not depend on a test to
  catch it.
- **Forgetting the README index.** A new ADR that isn't in
  `decisions/README.md` is effectively unfindable.
