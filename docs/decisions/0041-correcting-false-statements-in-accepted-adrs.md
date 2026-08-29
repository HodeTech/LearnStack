# ADR-0041: Correcting False Statements in Accepted ADRs

## Status

Proposed

**Date:** 2026-08-28 **Deciders:** @platform

## Context

[Documentation Standards § ADR Amendments](../standards/13-documentation.md)
says an Accepted ADR is "otherwise immutable" and that clarifications go in dated
Amendments appended at the bottom. Twelve other tracked documents restate the
prohibition, four of them as a hard review blocker.

The corpus does not obey it, and the first draft of this ADR was wrong about how
it disobeys. That draft claimed four precedents for correcting an Accepted ADR's
body in place. Checked against `git log`, the four are four *different*
instruments, and only one is the one claimed:

| Case | What actually happened |
|---|---|
| ADR-0003 Amendment 3 | The wrong RLS template sat inside **Amendment 1**, at line 53 of an 89-line file. `## Decision` (lines 7–17) was never touched, and ADR-0003 has no `Decision outcome` heading. An amendment corrected an amendment. |
| ADR-0017 Amendment 2 | The wrong namespace is **still there** — `docs/decisions/0017-tenant-organization-hierarchy.md:154` carries the original `LearnStack.Modules.Identity.Domain.Entities`, and that line has never been edited. Amendment 2 added a dated banner above the fence. An **inline erratum**. |
| ADR-0023 Amendment 2 | Touched no body text at all: a single insertion hunk. |
| ADR-0031 Amendment 1 | Genuinely replaced text in accepted body sections, at six carriers, in one commit, with a table naming each. |

So the practice this ADR was written to legitimise has been used **once**. The
instrument the corpus actually reaches for is the one the first draft dismissed.

Two further facts the check turned up, both relevant to the decision:

- **An undisclosed in-place edit exists.** Commit `a1ad5fb` (PR #6, 2026-05-21)
  added `UserId` to the cross-cutting value-object list in ADR-0023's
  § Implementation Notes — an Accepted ADR, edited in place, with no amendment
  anywhere. Nobody recorded it and no review caught it. That is what the
  prohibition is for, and it is also evidence that a prohibition nothing enforces
  does not prevent the thing.
- **ADR-0023 Amendment 4, on this branch, edits § Implementation Notes in place**
  to remove `idempotency_keys` from a list. It is disclosed, but it is the same
  instrument as ADR-0031 Amendment 1, not the ADR-0017 one.

## Decision Drivers

- **The written rule and the practised rule disagree, and the practised rule is
  not what anyone assumed.** Three instruments are in use — erratum,
  amendment-editing-an-amendment, and replacement — and no document distinguishes
  them.
- **The two rules protect different people.** Immutability protects the record:
  what was decided, on what evidence, by whom, so a later reader can audit the
  reasoning rather than a rewritten version of it. Correction protects the
  engineer who opens the ADR before writing a migration.
- **The harm is asymmetric and measurable in one direction only.** ADR-0031 named
  PostgreSQL 18's UUIDv7 generator `gen_uuid_v7()`. Measured on
  `postgres:18.4-alpine`: `SELECT gen_uuid_v7()` is
  `ERROR: function gen_uuid_v7() does not exist`, and `pg_proc` holds no such
  function even with all 46 bundled extensions installed. The name is `uuidv7()`.
  Five documents repeated the wrong one, including `IGuidFactory.cs`'s XML
  remarks. Under strict immutability all six keep it.
- **"Does not change the decision" is not a workable test alone.** It is the test
  amendments already use, and it is the test every edit above claimed. Without a
  bound on *what kind* of statement may be touched it licenses rewriting the
  prose around a decision until the record no longer shows what was argued.
- **Nor is "verifiable fact" alone.** A statement can be verifiably false today
  and have been true on the day it was accepted. Correcting that is not fixing an
  error; it is rewriting history to match the present, which is the exact harm
  immutability exists to prevent.

## Considered Options

### Option A — Strict immutability

No edit to an Accepted ADR's body, ever. Corrections live only in amendments at
the bottom of the file.

- **For:** the record is exactly what was accepted. No judgement call.
- **Against:** a reader who arrives at § Decision Drivers from a search hit never
  scrolls to the bottom, and acts on the false statement. It also requires
  reverting ADR-0031 Amendment 1 and restoring a non-existent SQL function to six
  documents.

### Option B — Bounded in-place replacement, disclosed by amendment

The first draft's answer: replace the false text, record it in a dated amendment
naming every carrier.

- **For:** the reader sees only correct text.
- **Against:** it is the strongest instrument, and it was chosen without noticing
  that the corpus's own dominant precedent is weaker. Applied by default it
  destroys the accepted wording in every case, including the many where a banner
  would have served.

### Option C — Correct anything that does not change the decision

- **For:** simplest to state.
- **Against:** this is what the corpus was doing informally, and PR #14 is what it
  produced: two paragraphs of [ADR-0037](0037-idempotency-key-contract.md)'s
  § Decision Drivers and § The durable store rewritten to argue a distinction the
  original author had not drawn. The decision was indeed unchanged. The record of
  how it was argued was not.

### Option D — Inline erratum, with replacement as a bounded exception

Keep the accepted text. Put a dated erratum immediately beside it, so a reader who
never scrolls still sees it. Record the correction in a dated Amendment. Replace
the text only where an erratum cannot reach the reader.

- **For:** it is what [ADR-0017](0017-tenant-organization-hierarchy.md) already
  does, and it protects both constituencies at once — the accepted wording
  survives in the document rather than only in `git log -L`, and the reader is
  warned at the point of reading.
- **Against:** the document grows, and the exception still needs a boundary sharp
  enough to argue about.

## Decision

**Option D.**

### The default: inline erratum

An Accepted ADR's body is **not edited**. Where it carries a statement that was
false when the ADR was accepted, add a dated erratum immediately adjacent to it —
before the paragraph, or before the fence — and record the correction in a dated
Amendment. The corrected value lives in whichever standard or architecture
document is the operational authority for it.

### The exception: in-place replacement

Replacement is licensed only when **all three** hold:

1. **The statement was false when the ADR was accepted** — not true then and
   stale now. Evidence is a command and its output, pinned where possible to the
   authority as it stood on the acceptance date.
2. **An erratum cannot protect the reader**, because the text travels away from
   its banner. That means either:
   - the text is meant to be **executed or copied elsewhere** — a template, a DDL
     or config fence, a command, a symbol a reader will type into code; or
   - the same false token is carried by **more than one document**, at least one
     of which cannot hold a Markdown banner (source, script, workflow).
3. **The diff adds and removes no normative content** — no obligation, scope,
   alternative, rationale or consequence.

ADR-0003 Amendment 3 meets (2): an RLS template exists to be copied, and had in
fact been copied into four documents, wrong in all four. ADR-0031 Amendment 1
meets (2) by the second limb: six carriers, one of them C# XML remarks. ADR-0017's
namespace meets neither limb — nobody lifts a namespace declaration out of an ADR
into a migration — and an erratum is what it correctly got.

### What is never touched

- **§ Status, § Date, § Deciders.** A Status change is a lifecycle event recorded
  as such, never a fact correction.
- **Rationale, framing, trade-offs, judgements.** "This approach performs better",
  the sizing of a consequence, the weighing of an option — none of these is a
  verifiable fact, and all of them are immutable. They change by superseding ADR.
- **Anything in § Decision that states the decision itself.** Where a correction
  would change how the decision *reads* rather than what it *names*, the body
  stays and the amendment carries the reading. ADR-0037 Amendment 1 is the worked
  example: its two paragraphs were rewritten in this branch and restored in the
  same commit that raises this ADR, so ADR-0037's body at `HEAD` is byte-identical
  to its text on `main`.

### What is not a correction at all

- **A link whose target moved.** Retargeting the URL with the link text unchanged
  is maintenance: nothing the ADR asserts changes, and no amendment is owed.
- **A statement that has gone stale.** A list that has drifted since acceptance, a
  file that has since been renamed, a document that no longer covers the subject —
  these were true when written. They are history. They get an amendment, or a
  superseding ADR, never a rewrite.

### The obligations

Both instruments carry the same three:

1. **A dated Amendment** naming what was wrong, **how it was shown wrong** (the
   command, the query, the file), and **every carrier changed**, inside this ADR
   and outside it. Where the correction is an enumeration, the amendment names the
   document the corpus treats as canonical for that list. The amendment is the
   record; the edit alone is not.
2. **The decision is restated as unchanged** in that amendment. If it cannot be,
   the change is a superseding ADR.
3. **Two gates in review, checked separately.** First: is there reproducible
   evidence the statement was false *at acceptance*? Second: does the diff move
   any normative content? A reviewer who has to reason about whether the meaning
   shifted is looking at an edit that does not qualify.

## Consequences

### Positive

- The documents an engineer opens before writing a migration stop naming
  functions that do not exist, and the accepted wording survives in the document
  rather than only in `git log -L`.
- The corpus's three instruments become three named instruments with a rule for
  choosing between them, instead of one prohibition and three undisclosed
  departures from it.
- The class is narrow enough to check in review without a debate about intent.

### Negative

- Errata accumulate in the body of a long-lived ADR, and a reader meets the
  correction before the thing corrected.
- Two rules now govern ADR edits where one did before, and the boundary is a
  judgement in the small number of cases near it.

### What accepting this costs on this branch

Named so the cost is visible before the decision, not after:

- **[ADR-0023](0023-strongly-typed-id-source-generator.md) Amendment 4** removed
  `idempotency_keys` from a list in § Implementation Notes in place. It fails
  exception limb (2) — a list of tables is not copied and has one carrier — so it
  becomes an erratum.
- **[ADR-0031](0031-postgresql-major-version.md) Amendment 1** stands under limb
  (2)'s second clause, but its own prose cites ADR-0003 Amendment 3 as precedent
  for correcting "wrong content inside an Accepted ADR **in place**". That is
  false: ADR-0003's edit was inside Amendment 1. The citation is corrected in the
  same commit that raises this ADR, because it is a statement about git history
  that git history refutes.
- **Commit `a1ad5fb`'s undisclosed edit to ADR-0023** is owed a retroactive
  amendment. Nothing enforces the prohibition today, which is why it survived
  fifteen months unnoticed.

### Neutral

- No existing ADR is superseded.

## Implementation Notes

Accepting this ADR is one commit that touches **fourteen files**. The rule is
stated in seventeen sentences across thirteen tracked files; the first draft named
three of them.

**Rewrites — the rule changes:**

| File | Sites |
|---|---|
| [13-documentation.md](../standards/13-documentation.md) | six, not one; § ADR Amendments is only the largest |
| [CLAUDE.md](../../CLAUDE.md) | two — § Things to never do, and the § Documentation layout table row |
| [decisions/README.md](README.md) | "Accepted ADRs are not rewritten" — today it does not even carry the amendment escape |
| [.github/CONTRIBUTING.md](../../.github/CONTRIBUTING.md) | § Never |
| [standards-check/SKILL.md](../../.claude/skills/standards-check/SKILL.md) | a hard blocker checklist item, which would mechanically block a compliant correction |
| [write-adr/SKILL.md](../../.claude/skills/write-adr/SKILL.md) | four sentences |
| [commit-and-pr/SKILL.md](../../.claude/skills/commit-and-pr/SKILL.md) | its parenthetical is already false today — amendments are permitted |
| [standards/README.md](../standards/README.md) | "Immutable history" |
| [21-architecture-tests-catalogue.md](../standards/21-architecture-tests-catalogue.md) | its justification for correcting ADR-0018's test names in place |

**Additions:**

- [17-code-review.md](../standards/17-code-review.md) has **no documentation gate
  at all** — sixteen zero-tolerance rows, all code or schema; one doc-adjacent
  checklist item, about OpenAPI. The rule belongs as a seventeenth § Zero
  Tolerance row, and the file's `**Derives from:**` header gains ADR-0041, because
  its parenthetical currently claims every blocker maps to ADR-0003 or ADR-0010.
- [decisions/template.md](template.md) states the amendment mechanism correctly
  and under-specifies obligation 1; its amendment stanza gains the three things an
  amendment must name.

**Left alone, deliberately:** the Packet 5 delivery record in
[phase-02a-kernel-tenancy.md](../roadmap/phase-02a-kernel-tenancy.md) argues from
ADR body immutability. It is a dated delivery record and is not rewritten
([CLAUDE.md § Documentation layout](../../CLAUDE.md)); it becomes
historically-true-and-stale, which is what a frozen record is for.

**Enforcement.** The class test is semantic and no architecture test can check it.
A narrower *disclosure* check is buildable in the CI meta job, against its existing
base-resolution machinery: an Accepted ADR changed in a diff that adds no dated
amendment anywhere is a failure. It cannot check the class, and it cannot key on
"the pre-amendment portion" — three ADRs put amendments in the top third of the
file, and two use `## Amendment N` with no container — so it enforces only that
something was disclosed. That is still more than the zero enforcement the
prohibition has today, which is what let `a1ad5fb` through.

## References

- [Documentation Standards](../standards/13-documentation.md) — the rule this ADR
  amends.
- [ADR-0017 Amendment 2](0017-tenant-organization-hierarchy.md) — the inline
  erratum this ADR makes the default.
- [ADR-0003 Amendment 3](0003-tenant-isolation-defense-in-depth.md) — the RLS
  template correction; an amendment correcting an amendment.
- [ADR-0031 Amendment 1](0031-postgresql-major-version.md) — the `uuidv7()`
  correction and its carrier table.
- [ADR-0023 Amendment 4](0023-strongly-typed-id-source-generator.md) — the
  `idempotency_keys` list correction.
- [ADR-0037 Amendment 1](0037-idempotency-key-contract.md) — the worked example of
  a correction that does *not* qualify.
