# ADR-0050: Publication and Course Content Access

## Status

> **Implementation update — 2026-10-02.**
> P02d-2 ships the persisted policy, restricted legacy backfill migration, six
> Education writers and exact-policy seed verification. P02d-4 anonymous-read
> enforcement and projections remain pending.
> The acceptance-time statement below is historical; see
> [Amendment 1](#amendment-1--p02d-2-implementation-delivery-2026-10-02).

Accepted — 2026-10-02. The maintainer approved the exact access policy, migration
and P02d-2 decision package. Implementation has not started.

**Date:** 2026-10-02
**Deciders:** Cemil (repository maintainer)
**Relationship:** Supersedes ADR-0048. The dated
[G3 supersession](../roadmap/phase-02d-walking-skeleton.md#g3-supersession-2026-10-02)
preserves the original answer and delivery history.

## Decision Drivers

- The endorsed hybrid direction includes paid learning. Publication must not promise
  anonymous lesson access for every course before the first writers and public readers.
- P02d-1 shipped independent course/lesson publication, not grants, prices or versions.
  Adding those subsystems is unnecessary to persist a safe access policy now.
- Phase 02d has no authenticated grant evaluator. A restricted course must remain
  restricted even if a request happens to contain credentials.
- Public course marketing metadata and private lesson content have different exposure
  contracts. Hiding a button cannot protect an API response, cache or media URL.
- The migration must handle existing P02d-1 rows without assuming every database is
  empty or inferring a public-access choice from a publication flag.

## Considered Options

1. **Course-level policy inherited by lessons** (chosen). Adds one explicit
   creation-time policy; separates publication from access without a grant subsystem.
2. **Retain published = anonymously readable** (rejected for protected authoring).
   Valid public-only baseline, but cannot safely represent the approved paid direction.
3. **Per-lesson policies and preview overrides now** (not selected). Adds policy
   composition and authoring workflows without a preview consumer. Phase 05 owns that
   authoring decision before its first preview writer; Phase 07 owns grant evaluation.
4. **CourseVersion, enrollment or commerce now** (rejected). Their identities and
   lifecycles remain with Phases 05, 07 and the proposed P09a, respectively.

## Decision

LearnStack separates independent publication from a course's content-access policy.
`Course.ContentAccess` is `public` or `enrollment_required`; lessons
inherit their parent's policy. Publication never creates a learner grant or selects
a sales channel.

### Storage and first writers

- Add `courses.content_access text NOT NULL` with a closed two-value CHECK and
  database default `enrollment_required`. Invalid or missing policy fails closed.
- The application creation contract requires an explicit policy. Missing, unknown
  or malformed input returns `validation_failed`; no application default silently
  opts content into public access. Domain validation also enforces the value set.
- P02d-2 selects the policy at course creation. It has no policy-edit, reparenting,
  unpublish, delete or preview command. There is no lesson policy column or override.
- Tenant, organization, ids, revision pins and translation ownership retain their
  existing isolation and parent-derivation rules. A policy is not a tenant entitlement,
  course grant, listing, price, order or instructor permission.

### Publication lifecycle

Both roots retain independent `draft` and `published` states and only the
`draft → published` transition. Creation starts in draft; a second publish is an
expected `business_rule_violation`. Publishing one root does not publish the other.
An empty course or incomplete locale coverage does not block publication.

Translations remain insert-only while their root is draft in this slice. Published
content is not edited or reordered. No CourseVersion or snapshot is created here.
Publishing is MUST-class audited, atomically with its root's state change. Course
publication does not claim a lesson mutation or access grant in audit.

### Anonymous exposure

| Surface | Eligibility and data |
|---|---|
| Course catalog / course detail | Published, live course in the admitted tenant/organization and enabled requested locale; declared marketing fields (title, summary, slug, level label) and policy can be public under either policy |
| Lesson list / detail under `public` | Existing parent/child publication, translation, deletion, organization and course-membership checks all pass; body access additionally requires the parent's policy to be `public` |
| Lesson list / detail under `enrollment_required` | No lesson ids, titles, slugs, order, counts, descriptors, bodies or media URLs are exposed anonymously; the course response can declare its restricted policy |
| Unknown or unresolvable policy | No lesson exposure; report the internal inconsistency without disclosing its cause to the caller |

Restricted course metadata does not make lesson existence public. P02d-4 omits all
restricted lesson inventory; direct lesson lookup returns the same `not_found`
contract as other hidden lessons. Course detail distinguishes restricted content
from an empty public course through policy, never through private lesson counts.
Marketing summaries are explicitly authored; never generate them from protected
lessons. Deny before loading protected descriptors, serializing bodies or minting
media URLs. HTML, RSC, SEO and structured data use the same public DTO boundary.
P02d-6 presents that bounded locked state without a fabricated payment/enrollment
action before its corresponding capability exists.

Phase 02d's anonymous read surface evaluates no learner grants. Credentials, staff
claims, seeder provenance or marketplace admission cannot unlock it. Phase 07's
authenticated reader must validate the caller's effective access for the correct
tenant/course version before protected data leaves the server. Failure or absence
of the evaluator denies protected access; there is no public fallback.

### Caches and media

- Apply eligibility before returning or populating any public representation. A
  cached source row is not authorization. Public cache families never hold a
  restricted body or a learner-specific grant response.
- P02d-4/5 retain their cache/header gates; this policy is an additional eligibility
  input, not permission for shared path-only caching. Phase 07's protected reader
  checks access before payloads or `304` responses and starts with private, no-store
  responses. It decides caller/version-qualified caching and revocation before caching.
- Do not emit protected body-derived media or bearer URLs through public DTOs,
  previews, logs or error details. Phase 04's first protected media producer and
  Phase 07's grant reader must enforce access on issuance and retrieval.
- This record does not make an independently public external URL private. The seed
  uses no remote media; a licensed protected-media delivery contract precedes such
  offers. URL safety validation is separate from a learner's access right.

### Forward migration and rollback

All pre-column rows receive `enrollment_required`, regardless of status. Preserve
ids, parent relationships, scope, translations, bodies, pins and publication state.
This deliberately removes their previously planned anonymous body eligibility;
it is not an inference that old content was private or that the database was empty.

New demo courses explicitly select `public` or `enrollment_required`. Seed reruns
do not overwrite an existing policy or repair mismatched published content silently.
An existing row needing a different choice requires a separately approved migration
or Phase 05 authoring workflow, not a seed bypass.

Migration down/up must be tested on disposable data. Removing the column would restore
the old public implication; it is unsafe as a live rollback once restricted content or
readers exist. Keep the forward schema and a compatible denying application when
rolling back a release, or stop serving public Education. An old reader that ignores
the retained column is unsafe. Any live downgrade needs a separately reviewed
containment plan; the technical Down method is not an operational authorization.

### Phase 05 and Phase 07 preservation

Phase 05's migration preserves access policy alongside ids, translated URLs, bodies,
order, scope and exact pins. It decides the policy's authoritative location in the
versioned structure before writers; no existing restricted version becomes public
because a new version or listing is created.

Phase 05 owns policy editing, previews and per-lesson exceptions if required.
Any future reparenting writer decides inherited access before moving a lesson; the
existing parent-scope trigger does not protect an access-policy transition.
Phase 07 owns grants, their purchase attribution and effective access. Neither can
infer eligibility from price, publication or Hub licensing. The first grant consumer
records its attribution contract before later billing producers rely on it.

## Context

[ADR-0048](0048-walking-skeleton-publication.md) intentionally established a public-only
skeleton in P02d-1. Its lifecycle remains useful, but the anonymously readable
implication changes when protected authoring is selected. This is a new decision,
not a correction of a false historical statement.

The maintainer approved this contract on 2026-10-02. The
[dated G3 supersession](../roadmap/phase-02d-walking-skeleton.md#g3-supersession-2026-10-02)
records the current policy and packet obligations; ADR-0048 retains its original
decision as history. Acceptance establishes the contract, not a shipped migration,
writer or reader.

## Consequences

- A published course can be marketed without exposing its restricted lesson inventory.
- The first writers persist access intent; no future payment flag has to redefine
  publication. The database change is additive, but eligibility changes deliberately.
- Legacy published rows become conservatively restricted; automatic public
  backfill and silent seed repair are rejected.
- Per-lesson previews, grants and protected media have explicit first-consumer
  decisions in named phases; this slice does not claim those capabilities.

## Implementation Notes

- P02d-2 Step 1: policy domain/configuration, forward migration and proofs.
  Creation commands in Step 3 require the policy; Step 4 seed names both choices.
- P02d-4: shared eligible-read predicate, marketing projection and hidden-lesson
  response equivalence; cache and OpenAPI policy representation are decided before API.
- P02d-5/6: no transport/cache bypass; public and locked renderer states.
- Phases 04/05/07: protected media, version/policy evolution and authenticated grants,
  respectively. No price, listing, balance or payout belongs in the Education change.

## Architecture Tests

Accepted obligations, not implemented tests:

- New policy requires explicit valid input; storage rejects every other value.
- Migration restricts existing rows and preserves all other data; disposable Down/Up
  preserves common columns and backfills restricted again, not lost public choices.
- Public courses with eligible lessons have positive controls; restricted, draft,
  deleted, wrong-course and cross-scope lessons have no anonymous exposure.
- Restricted course metadata includes no lesson inventory, counts or descriptors.
- Caches, direct lookup, credentials and media projection cannot bypass access.
- Seed convergence verifies policy exactly; conflicting existing policy fails nonzero.

Reserve agreed rule names as Registered in the architecture catalogue before code;
mark them Implemented only when their tests exist and run. Existing tenant/organization,
one-root publication, concurrency and audit guards remain required.

## References

- [Education module](../modules/education/README.md)
- [P02d-2 decision package](../roadmap/phase-02d-walking-skeleton.md#p02d-2-decision-package-2026-10-02)
- [Course access terminology](../glossary.md#enrollment--access)
- [Phase 05](../roadmap/phase-05-education-learning-content.md)
- [Phase 07](../roadmap/phase-07-enrollment-learner-portal.md)
- [ADR-0049](0049-institution-sites-and-course-marketplace.md)

## Amendments

### Amendment 1 — P02d-2 implementation delivery (2026-10-02)

P02d-2 delivered the explicit Course policy and CHECK/default, restricted legacy
backfill with forward/Down/reapply proofs, all six Education writers and exact-policy
seed convergence. `CourseContentAccessMigrationTests`, `EducationWriterTests` and
`SeederTests` provide the corresponding implementation evidence. Public eligibility,
restricted marketing projections, denial equivalence and cache/transport proofs
remain P02d-4 and its later consumers; authenticated grants remain Phase 07.

The not-started status and test-obligation wording were true at acceptance and are
preserved as history. This dated disclosure and the operational carriers now describe
the delivered scope. The Decision is unchanged.

Updated carriers: this status disclosure, [the ADR index](README.md),
[the glossary](../glossary.md), [the standards index](../standards/README.md),
[Frontend Architecture Standards](../standards/07-frontend-architecture.md),
[Accessibility Standards](../standards/16-accessibility.md),
[marketplace scoping](../architecture/34-course-marketplace-scoping.md),
[Proposed ADR-0049](0049-institution-sites-and-course-marketplace.md),
[Phase 04](../roadmap/phase-04-cms-media-pages.md) and
[Phase 07](../roadmap/phase-07-enrollment-learner-portal.md). The
[P02d-2 delivery record](../roadmap/phase-02d-walking-skeleton.md#p02d-2-implementation-delivery-2026-10-02)
is the current delivery authority.

#### Amendment 2 — Status navigation (2026-10-02)

The dated implementation disclosure now sits inside the Status section so direct
`#status` navigation shows current delivery before the unchanged acceptance-time
statement. Amendment 1 remains the delivery record; no decision changes.
