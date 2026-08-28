# ADR-0041: Correcting False Statements in Accepted ADRs

## Status

Proposed

**Date:** 2026-08-28 **Deciders:** @platform

## Decision Drivers

- **The written rule and the practised rule disagree, and both have a
  constituency.**
  [Documentation Standards § ADR Amendments](../standards/13-documentation.md)
  says Accepted ADRs are "otherwise immutable" and that clarifications go in
  dated Amendments at the bottom of the file. The corpus does something else and
  has for months:
  [ADR-0003 Amendment 3](0003-tenant-isolation-defense-in-depth.md) replaced a
  wrong RLS template inside § Decision outcome,
  [ADR-0017](0017-tenant-organization-hierarchy.md) superseded a namespace inside
  the same section, [ADR-0023 Amendment 2](0023-strongly-typed-id-source-generator.md)
  did the same for a cross-cutting identifier list, and
  [ADR-0031 Amendment 1](0031-postgresql-major-version.md) corrected a function
  name at six carriers. Every one of them disclosed the edit in a dated
  amendment. None of them was permitted by the standard as written.
- **The two rules protect different things.** Immutability protects the
  *record*: what was decided, on what evidence, by whom — so a later reader can
  audit the reasoning rather than a rewritten version of it. Correction protects
  the *reader*: an ADR is not an archive, it is the document an engineer opens
  before writing a migration.
- **The harm is asymmetric, and it is measurable.** ADR-0031 named PostgreSQL
  18's UUIDv7 generator `gen_uuid_v7()`. No such function exists —
  `SELECT gen_uuid_v7()` on `postgres:18.4-alpine` is
  `ERROR: function gen_uuid_v7() does not exist`; the name is `uuidv7()`. Five
  documents repeated it. Under strict immutability all six keep the wrong name
  in their body forever, and the next engineer writing a `DEFAULT` clause copies
  a function that does not exist out of the document the standard tells them to
  read. An amendment at the bottom of a 200-line ADR does not reach a reader who
  landed on § Decision Drivers from a search hit.
- **"Does not change the decision" is not a workable test on its own.** It is
  the test the standard already uses for amendments, and it is the test every
  edit above claimed. Without a bound on *what kind* of statement may be
  corrected, it licenses rewriting the prose around a decision until the record
  no longer shows what was actually argued.

## Considered Options

### Option A — Strict immutability

No edit to an Accepted ADR's body, ever. Corrections live only in amendments.

- **For:** the record is exactly what was accepted. No judgement call, no
  reviewer argument, nothing to police.
- **Against:** the body keeps stating things that are false, and a reader who
  does not scroll to the bottom acts on them. It also requires reverting four
  existing amendments' worth of work and restoring a broken RLS template and a
  non-existent SQL function to the documents an engineer reads first.

### Option B — Bounded correction, disclosed by amendment

An Accepted ADR's body may be corrected in place for a **false statement of
verifiable fact**, and for nothing else. The correction is recorded in a dated
Amendment that says what was wrong, how it was measured, and every carrier
changed.

- **For:** matches what the corpus already does, keeps the reader safe, and
  bounds the licence to a class a reviewer can check — a symbol either exists or
  it does not.
- **Against:** the class needs a definition sharp enough to argue about, and a
  reviewer has to apply it.

### Option C — Correct anything that does not change the decision

- **For:** simplest to state.
- **Against:** it is the rule the corpus has been applying informally, and this
  PR is what it produced: two paragraphs of § Decision Drivers and § The durable
  store in [ADR-0037](0037-idempotency-key-contract.md) rewritten to argue a
  distinction the original author had not drawn. The decision was indeed
  unchanged. The record of how it was argued was not.

## Decision

**Option B.** An Accepted ADR's body may be corrected in place when, and only
when, the text is a **false statement of verifiable fact** — one that can be
shown false by running something, opening a file, or following a link:

- a symbol, function, type, file, table or column that does not exist under the
  name given (`gen_uuid_v7()`);
- a link that does not resolve, or resolves to a document that no longer covers
  the subject;
- SQL, code or configuration that does not do what the surrounding prose says it
  does (ADR-0003's permissive policy pair);
- an enumeration that has drifted from a list this corpus declares canonical
  elsewhere.

Everything else — reasoning, framing, tone, trade-offs, consequences, anything
that is a judgement rather than a fact, and **any text in § Decision or
§ Decision outcome that states the decision itself** — is immutable. It changes
by superseding ADR, or not at all.

Three obligations attach to every in-place correction:

1. **A dated Amendment**, at the bottom, naming what was wrong, how it was shown
   wrong (the command, the query, the file), and **every carrier changed** —
   inside this ADR and outside it. The amendment is the record; the edit alone is
   not.
2. **The decision is restated as unchanged** in that amendment. If it cannot be,
   the correction is out of scope and the change is a superseding ADR.
3. **The reviewer checks the class, not the intent.** "Does this text assert
   something that is verifiably false?" is the question. A reviewer who has to
   reason about whether the meaning shifted is looking at an edit that does not
   qualify.

Where the correction would change how the decision *reads* rather than what it
*names* — [ADR-0037](0037-idempotency-key-contract.md)'s two paragraphs are the
worked example — the body stays as written and the amendment carries the
corrected reading. That ADR is restored to its accepted text in the same PR that
raises this one.

## Consequences

### Positive

- The documents an engineer opens before writing a migration stop naming
  functions that do not exist and templates that leak across tenants.
- The four existing in-place corrections become compliant rather than tolerated,
  and their amendments become the model rather than the exception.
- The class is narrow enough to check in review without a debate about intent.

### Negative

- A reader who wants the text exactly as accepted has to read `git log -L` on the
  section. The amendment names the carriers, so the trail exists, but it is a
  trail rather than the document.
- Two rules now govern ADR edits where one did before, and the boundary between
  them is a judgement in the small number of cases that sit near it.

### Neutral

- No existing ADR is superseded. ADR-0003, ADR-0017, ADR-0023 and ADR-0031 keep
  their amendments; this ADR is what they were tacitly relying on.

## Implementation Notes

- [Documentation Standards § ADR Amendments](../standards/13-documentation.md)
  is rewritten to carry both rules, citing this ADR.
- [CLAUDE.md § Things to never do](../../CLAUDE.md) currently states the
  prohibition without the exception; it gains the bound.
- No architecture test can enforce this — the class is semantic. It is a review
  rule, enumerated in
  [Code Review Standards](../standards/17-code-review.md) alongside the other
  documentation gates.

## References

- [Documentation Standards](../standards/13-documentation.md) — the rule this ADR amends.
- [ADR-0003 Amendment 3](0003-tenant-isolation-defense-in-depth.md) — the RLS template correction.
- [ADR-0031 Amendment 1](0031-postgresql-major-version.md) — the `uuidv7()` correction and its carrier table.
- [ADR-0023 Amendment 4](0023-strongly-typed-id-source-generator.md) — the `idempotency_keys` list correction.
- [ADR-0037 Amendment 1](0037-idempotency-key-contract.md) — the worked example of a correction that does *not* qualify.
