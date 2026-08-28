# ADR-NNNN: <Title>

## Status

Proposed | Accepted | Superseded by ADR-NNNN | Deprecated

**Date:** YYYY-MM-DD
**Deciders:** @name, @name
**Supersedes:** ADR-NNNN (optional)

## Decision Drivers

Bullet list of the forces / constraints / goals that made a decision necessary. Each
bullet stands on its own — if a future reader can't tell *why* this list shaped the
decision, the bullet is too thin.

- Driver 1 (e.g. "compliance requires audit retention ≥ 7 years for security events").
- Driver 2 (e.g. "Hub-side billing must be air-gappable for Self-Hosted").
- Driver 3 (e.g. "team capacity is sufficient for an additional running service").

## Considered Options

Numbered list. Include the chosen option **and** at least one rejected alternative —
ADRs that present only the chosen option do not document a decision, they document a
preference. The "why we rejected X" is where the value lives.

1. **Option A** (chosen). One-line summary.
2. **Option B** (rejected). One-line summary.
3. **Option C** (rejected). One-line summary.

## Decision

Present-tense statement of the decision. One short paragraph or a few bullets. No
hedging.

> Example: "LearnStack uses **Option A** for X. The implementation lives in
> `LearnStack.Infrastructure.X`; the contract lives in `LearnStack.SharedKernel`."

## Context

Deeper explanation of the situation, the constraints, and the alternatives:

- Why option B was rejected.
- Why option C was rejected.
- What would change our minds.
- What we explicitly punted on for now.

This section can be longer than the Decision section. It is where reviewers in two
years figure out whether circumstances have changed enough to re-open the ADR.

## Consequences

### Positive

- Outcome 1.
- Outcome 2.

### Negative

- Cost 1.
- Cost 2.

### Neutral

- Trade-off 1.

## Implementation Notes (optional)

- Concrete code-level rules that follow from this decision (e.g. "every `[TenantOwned]`
  entity must have an EF query filter and an RLS policy").
- Architecture-test names that enforce the rule.
- Links to standards / architecture docs where the day-to-day rules live.

## Amendments

Dated, append-only clarifications that do not change the Decision section. If the
decision itself changes, write a new ADR that supersedes this one.

### YYYY-MM-DD — Clarification title

…short note about what was previously ambiguous and how it should be read now.

## References

<!-- Placeholders, not links. A link here is a link to a file that does not
     exist, and the CI Markdown audit is right to say so — including on the
     full-tree pass it falls back to when the diff base does not resolve.
     Replace each with a real relative link when copying the template. -->

- Related ADR — link to `NNNN-related.md`
- Related architecture doc — link to `../architecture/NN-related.md`
- Related standard — link to `../standards/NN-related.md`
- External link (optional).
