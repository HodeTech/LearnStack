# ADR-0052: Anonymous Public Read Boundary

## Status

> **Implementation update — 2026-10-05.**
> All four P02d-4 steps are delivered in PR #25, awaiting maintainer review/merge.
> The original pending/not-started statements below are acceptance-time history;
> [Amendment 12](#amendment-12--current-delivery-navigation-2026-10-05)
> identifies the delivery records. P02d-5/6 remain the server/page owners.

Accepted — maintainer approval recorded 2026-10-03. Implementation is pending.

**Date:** 2026-10-03
**Deciders:** @cemil

The [P02d-4 approval package](../roadmap/phase-02d-walking-skeleton.md#p02d-4-decision-package-2026-10-03)
records the accepted public contracts, implementation steps and dated
clarifications appended to ADR-0036 and ADR-0040. Implementation has not started.

## Decision Drivers

- P02d-4 creates the first production anonymous read endpoints and v1 baseline.
- The renderer needs site configuration without selecting a tenant or exposing
  tenant/organization identifiers through an edge host-lookup API.
- Host-only admission must not become broader when Phase 02b adds authentication.
- RLS limits tenant/organization scope; it does not enforce publication or access.
- An anonymous read must have a falsifiable barrier against tenant-owned writes,
  including a controller or nested handler that bypasses the intended reader.
- Current ownership, trusted-hop checks, audit durability and writer behavior
  remain binding under ADR-0036, ADR-0040 and ADR-0050.

## Considered Options

1. **Host-resolved public reads in read-only ambient transactions** (chosen).
   Preserve API authority and add an independently enforced public scope ceiling.
2. **Edge lookup returning tenant/organization IDs** (rejected). Adds a second
   resolution surface and identifiers the public renderer does not need.
3. **Ordinary writable transactions with review-only write exclusion** (rejected).
   A controller or nested command can bypass the read path without violating RLS.
4. **Replace the normal context with a host-only context for public endpoints**
   (rejected). Makes reconciliation depend on the route and can discard claim,
   agreement or membership failures rather than preserving ADR-0036.
5. **A separate public-read database role** (rejected for P02d-4). A fifth role
   requires a new ADR under ADR-0003. Separate credentials and connection ownership
   would also need a contract compatible with ADR-0040's shared ambient unit;
   transaction-local READ ONLY protects its enlisted writes without that change.
6. **A separate public read model** (rejected for P02d-4). Introduces projection
   ownership, synchronization and revocation lag before this packet demonstrates
   a need for them. Publication and access remain fresh predicates on source rows.

## Decision

LearnStack serves every institution public read as a `[PublicSurface]` MediatR
request dispatched through `ISender` in an explicitly read-only ambient transaction.
A site bootstrap exposes an allowlist of rendering configuration; it accepts no
host, tenant or organization selector in route, query or body and exposes no
tenant/organization IDs. ADR-0036 determines the effective host from the direct
request or authenticated trusted hop. Factory-preserved host scope narrows public
eligibility, while normal reconciliation, query filters and RLS remain active.
For a physical read-only owner, transaction mode is established before the tenant
announcement: one bounded exception to ADR-0040's first-statement ordering.

This is the accepted contract, not a delivery claim. The dated clarifications and
detail-carrier changes accompany acceptance; runtime proof belongs to P02d-4.

## Context

Frontend Architecture Standards and Infrastructure Stack Standards, linked below,
prescribed frontend edge lookup at the preparation baseline. The new bootstrap replaces
that public
lookup contract. P02d-5 still owns the server transport, trusted-hop configuration,
entry behavior and rendering mode; this ADR does not claim they exist.

ADR-0036 reconciles host, claim and membership independently of routing. Its row 7
permits an organization claim on a tenant host; row 14 permits claim-selected
tenancy on a platform host. Those valid contexts are not equivalent to the scope
of an institution's public host. The shipped middleware supplies no claims yet;
this is a first-consumer boundary, not evidence of a current authentication exploit.

ADR-0040 already makes one announced connection and one owned transaction the
unit of work. A read-only mode narrows that transaction without creating another
connection, tenant-context setter or cross-module mechanism. It adds one explicit
read-only setup statement before the tenant announcement. This ADR authorizes
that bounded departure from ADR-0040's first-statement sketch, following the
new-decision exception pattern of ADR-0042. It does not grant an amendment licence
to change ADR-0040's Decision. Mixed-mode joins must be refused;
an outer writable transaction cannot silently make a public query writable.

The runtime write barrier protects the enlisted ambient transaction only. It
cannot prevent writes on a separately opened connection or outside the pipeline.
Institution public-data endpoints must use the marked request path; controllers,
minimal API handlers and their helpers may not access persistence directly or open
their own connections. Structural controls detect bypasses; static tests are not
the proof. Real HTTP/database tests must exercise the dispatched path, rejected
write attempts and a successful writable control. Independent rejected-assertion
audit keeps its existing sanctioned path.

Reconsider this choice if measured source-query load cannot meet the packet's
performance budget, or a new delivery boundary needs independently operated reads.
Any replacement must specify projection freshness or connection/role ownership and
demonstrate equivalent host, access and write isolation before changing this ADR.

## Consequences

### Positive

- One host mapping remains authoritative for direct and trusted-hop requests.
- Public route/query/body inputs and rendering configuration carry no tenancy
  selectors; effective-host resolution retains ADR-0036's trusted inputs.
- Claims can cause refusal but cannot enlarge anonymous content visibility.
- PostgreSQL READ ONLY blocks writes on the enlisted transaction; structural
  guards detect paths that could bypass that runtime barrier.
- Existing exact-pin definition reads and typed settings remain reusable through
  ADR-0010 application contracts on the same ambient connection.

### Negative

- The context seam gains immutable host provenance; cursor binding also needs
  the classified effective host's digest at the API boundary.
- Public execution needs read-mode nesting, lifecycle and controller tests.
- Out-of-pipeline or independent-connection writes remain a bypass risk. Structural
  guards and real HTTP/database proofs must accompany the mandatory dispatch path.
- No-store responses perform fresh Education reads; production query-plan and
  representative local measurements must be recorded without a p95 claim.

### Neutral

- This does not add a marketplace, grant, enrollment, payment, media or CMS API.
- READ ONLY does not promise a whole-request snapshot or linearizable revocation.
- Normal writer transactions, the reconciliation matrix and the setter sets keep
  their existing contracts, except for the disclosed public read-only setup order.
  Rejected-assertion audit remains independent.

## Implementation Notes

- **P02d-4:** retain nullable immutable `HostScope` on `ITenantContext`. Only
  `TenantContextFactory` constructs it from the attempt's existing host IDs after
  reconciliation succeeds. Ambient, unresolved and claim-only contexts have none.
  Do not add a host string or routing metadata to the resolution attempt.
- The API derives cursor host binding from immutable `HostClassification`; pass
  only its digest into the public query. It is pagination context, never tenant
  authority, and does not enter retained logs or public configuration.
- Public queries require matching real host scope. Tenant hosts expose tenant-wide
  rows only; organization hosts expose tenant-wide and their own organization's
  rows, intersected with the normal context filters and RLS.
- Select ReadOnly/ReadWrite before dispatch; keep existing callers ReadWrite by
  default. A mixed-mode join fails before handler invocation and marks the unit
  rollback-only. Same-mode joins retain the existing frame ownership rules.
- The physical ReadOnly owner begins a READ COMMITTED transaction, executes and
  awaits `SET TRANSACTION READ ONLY`, then returns the owner frame. The existing
  sanctioned setter announces tenant/organization before any data SQL or handler
  dispatch. This control statement is the only permitted predecessor; default
  writable transactions retain first-statement tenant announcement. Begin cleans
  up a partially opened transaction if control setup fails or is cancelled, marks
  the unit rollback-only and rethrows, without waiting for pipeline cleanup.
  Mode resets with each transaction; no session-level setting may leak into a later
  writer or pooled connection. A successful read-only commit may precede a writer
  on the same reusable unit under
  [ADR-0040 Amendment 8](0040-ambient-unit-of-work.md#amendment-8--read-only-public-frames-2026-10-03).
  Resetting mode never clears rollback-only poisoning; a poisoned unit requires a
  fresh scope under existing
  [ADR-0040 Amendment 2](0040-ambient-unit-of-work.md#amendment-2--the-handles-shape-and-what-a-joiners-rollback-does-not-do-2026-08-28).
- Every public request is marked `[PublicSurface]`, audit Off, GET/HEAD only and
  dispatched through `ISender`. Structural controls cover endpoint methods, audit
  registration and controller dependencies/bodies; READ ONLY blocks write attempts
  on the enlisted transaction, not on arbitrary connections.
- ADR-0050 eligibility precedes inventory, body or descriptor loading. A body
  query includes eligible parent and public policy in the same SQL statement.
- **P02d-5:** replace edge ID lookup with the approved bootstrap and configure
  the trusted server hop without client-supplied tenant authority.
- **P02d-6:** render the approved DTOs, safe theme fallback and bounded content
  states. Rendering, accessibility and HTML injection remain that packet's work.

## Architecture Tests

New names below are Registered in the existing catalogue before implementation,
not passing-test claims; that catalogue owns canonical names and status. Preserve these
existing rules without renaming them:

- `TenantContext_Is_Constructed_Only_By_The_Factory`,
  `TenantContext_Is_Instantiated_In_One_File` and
  `SetTenant_Callers_Are_The_Enumerated_Four` keep construction and writers closed.
- `PublicSurface_Marker_Set_Is_Enumerated` gains non-empty endpoint/method equality;
  `PublicSurface_Requests_Are_Never_ReadSensitive` retains its audit exclusion.

Registered additions, each requiring clean and planted offending controls:

- `HostScope_Is_Constructed_Only_By_The_Factory`: immutable host provenance with
  no claim-only, ambient or unresolved construction path.
- `PublicSurface_Requests_Are_Registered_Off`: every marked request is audit Off.
- `PublicSurface_Endpoints_Dispatch_Through_The_Pipeline`: the non-empty institution
  read-route inventory dispatches marked requests through `ISender`.
- `PublicSurface_Controllers_Do_Not_Access_Persistence`: scan dependencies and
  bodies, including async bodies and helpers, with direct, indirect and
  service-locator offenders. Cover minimal API bypasses in endpoint controls too.
- `PublicSurface_Response_Schemas_Exclude_Internal_Fields`: recursive DTO/OpenAPI
  allowlist and forbidden-field controls.
- `PublicSurface_Reads_Do_Not_Invoke_JsonSchema_Validation`: public reads never
  acquire or invoke the write-time schema validator, directly or through helpers.

Behavioral proofs complement these structural controls: row 7/14 host narrowing,
eligibility masking and real HTTP/database positive controls. Prove READ ONLY
through EF and raw SQL as `learnstack_app`, with a successful writable control,
mixed-mode nesting, rollback, cancellation and next-transaction controls, including
sticky poisoned-unit refusal. Check zero committed tenant-owned writes and zero
normal public audits. These are obligations, not current runtime test results.

## References

- [ADR-0003 — Tenant Isolation](0003-tenant-isolation-defense-in-depth.md)
- [ADR-0010 — Cross-Module Communication](0010-cross-module-communication.md)
- [ADR-0036 — Tenant Resolution Trusted Inputs](0036-tenant-resolution-trusted-inputs.md)
- [ADR-0040 — Ambient Unit of Work](0040-ambient-unit-of-work.md)
- [ADR-0042 — Tenant Provisioning Cross-Aggregate Transaction](0042-tenant-provisioning-cross-aggregate-transaction.md)
- [ADR-0050 — Publication and Course Content Access](0050-publication-and-course-content-access.md)
- [ADR-0051 — Ordered Text Card Presentation](0051-ordered-text-card-presentation.md)
- [API Design Standards](../standards/04-api-design.md)
- [Frontend Architecture Standards](../standards/07-frontend-architecture.md#tenant-resolution)
- [Infrastructure Stack Standards](../standards/20-infrastructure-stack.md#host--tenant-resolution)
- [Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md)
- [Frontend Architecture](../architecture/14-frontend-architecture.md)


## Amendment 1 — P02d-4 foundation delivery (2026-10-03)

This delivery note updates the implementation status recorded at acceptance; it
changes no decision. P02d-4 Step 1 implements factory-only immutable HostScope,
matching public admission, ReadOnly/ReadWrite ambient frames and pipeline mode
selection. Same-mode joining, mixed-mode poison, partial setup cleanup and mode
reset are exercised against PostgreSQL as the application role. EF and SQL write
refusals have independent writable controls.

Institution public endpoints, lifecycle/eligibility readers, HTTP controls and
OpenAPI/SDK/CI proofs remain with Steps 2–4. The original Status and Architecture
Tests sections record the acceptance baseline; the catalogue and
[P02d-4 delivery record](../roadmap/phase-02d-walking-skeleton.md#p02d-4-step-1-authority-and-read-only-foundation)
own current implementation and review evidence.

## Amendment 2 — P02d-4 site bootstrap delivery (2026-10-03)

This delivery note changes no decision. Step 2 implements the live Tenancy
configuration port and site GET/HEAD through the marked Off read-only pipeline.
The early public response policy covers successes and errors, including bodyless
HEAD, no-store and absence of response validators. Explicit Off registration,
controller/helper persistence exclusion and endpoint dispatch/method controls
have planted positive and negative cases. PostgreSQL-backed HTTP cases exercise
lifecycle withdrawal, locale emptiness/invalidity, whole-theme/null and effective
attribution. Education reads and full OpenAPI/SDK/CI proof remain Steps 3–4.

The [packet delivery record](../roadmap/phase-02d-walking-skeleton.md#p02d-4-step-2-public-site-bootstrap)
owns validation and both review rounds; it does not mark the whole packet complete.

## Amendment 3 — P02d-4 Education read delivery (2026-10-04)

This delivery note changes no decision. Step 3 implements catalog, course and
lesson GET/HEAD through marked Off read-only requests. Fresh source SQL intersects
host scope, normal filters and RLS; the body SELECT includes the eligible public
parent and exact URL translations. Requiredness is admitted metadata carried
through the internal definition projection, not read-time schema evaluation.
Marketing, outline, alternates and content use the approved DTO allowlist; exact
locale and bounded scoped seek cursors precede data selection.

The no-schema-evaluation guard has a non-empty production handler census and
planted transitive controls. Real PostgreSQL/HTTP cases and actual consumer
EXPLAIN plans are recorded in the
[Step 3 delivery record](../roadmap/phase-02d-walking-skeleton.md#p02d-4-step-3-public-education-reads).
Both independent review rounds are pending at this implementation boundary.
Full served/snapshot OpenAPI, SDK and required CI proof remain Step 4.

## Amendment 4 — Education read review closeout (2026-10-04)

This delivery note changes no decision. Both independent Step 3 review rounds
are complete. The verified Unicode cursor defect is fixed with bounded refusal
and real HTTP controls; no verified finding remains after fresh Round 2 review.
The [Step 3 delivery record](../roadmap/phase-02d-walking-skeleton.md#p02d-4-step-3-public-education-reads)
owns review and verification evidence. Contract/SDK/CI delivery remains Step 4.

## Amendment 5 — contract, SDK and CI implementation (2026-10-04)

This delivery note changes no decision. Step 4 implements the sole committed v1
snapshot and production served equality, eight-operation and recursive public DTO
controls. The SDK supplies generated types and four injected GET wrappers with
bounded success/Problem Details parsing. Required frontend regeneration refuses
drift. Always-running OpenAPI CI uses the pinned compatibility policy, mutation
fixtures and explicit companions for open-enum tool gaps; current response enums
remain closed. Verified operation-free base composition is the only bootstrap.

The [Step 4 delivery record](../roadmap/phase-02d-walking-skeleton.md#p02d-4-step-4-contract-sdk-and-ci)
owns actual verification, the two independent review rounds and the approved live
sixth-check rollout. Reviews and settings activation remain pending at this
implementation boundary. P02d-5/6 remain the server consumer/browser owners.

## Amendment 6 — contract and compatibility review fixes (2026-10-04)

This delivery note changes no decision. Independent Step 4 review verifies gaps
between the pinned diff tool and ADR-0024's type/enum policy. The deterministic
companion now covers reachable request/response schemas and local object
references; additional CLI controls prove the previously missed changes fail.
Problem Details parameter schema and SDK parsing match the existing string-valued
localized-message carrier. Invalid local path encoding returns a bounded result,
and caller cancellation retains precedence. The
[Step 4 delivery record](../roadmap/phase-02d-walking-skeleton.md#p02d-4-step-4-contract-sdk-and-ci)
owns reproduction and regression evidence. Fresh Round 2 and the approved live
sixth-check registration remain pending at this fix boundary.

## Amendment 7 — second contract/CI review fixes (2026-10-04)

This delivery note changes no decision. Step 4 Round 2 verifies additional
compatibility gaps in shared-reference normalization, inherited parameters and
content-form parameter/header schemas. Per-occurrence materialization, immutable
analysis and tool-supported policy projections close those gaps while preserving
nonbreaking controls. Exact dot segments remain inside the SDK refusal boundary;
extra-operation contract mutants supply the promised regression proof. The
[Step 4 delivery record](../roadmap/phase-02d-walking-skeleton.md#p02d-4-step-4-contract-sdk-and-ci)
owns the verified findings and fix evidence. Fresh focused review and live sixth
required-check activation remain pending at this fix boundary.

## Amendment 8 — recursive compatibility verification (2026-10-04)

This delivery note changes no decision. Fresh focused review verifies the prior
fixes and identifies an incompatible refusal of a recursive open-enum addition.
Private effective recursive schema copies now keep cycle edges inside the
normalized policy graph, preserving closed consumers and stable identity across
documentation changes. Six added CLI controls complement the earlier matrix.
The [Step 4 delivery record](../roadmap/phase-02d-walking-skeleton.md#p02d-4-step-4-contract-sdk-and-ci)
owns proof and final review evidence. Live sixth-check activation is still pending.

## Amendment 9 — inline recursive comparison fix (2026-10-04)

This delivery note changes no decision. Final focused review identifies recursive
normalization using an unstable traversal identity outside Schema components.
Stable declaration origins now cover inline schemas and schemas inside Response
components, with unchanged/open-addition CLI controls for both placements. The
[Step 4 delivery record](../roadmap/phase-02d-walking-skeleton.md#p02d-4-step-4-contract-sdk-and-ci)
owns reproduction and fresh verification. Live sixth-check activation is pending.

## Amendment 10 — implementation and review closeout (2026-10-04)

This delivery note changes no decision. All four P02d-4 implementation steps and
their two independent review rounds are complete. Fresh focused verification of
the final compatibility fixes approves, with no verified finding remaining. The
[Step 4 delivery record](../roadmap/phase-02d-walking-skeleton.md#p02d-4-step-4-contract-sdk-and-ci)
owns the complete regression and final control counts. Actual PR CI and live
sixth required-check registration remain the final rollout proof; this note does
not claim merge or browser delivery. P02d-5/6 remain the server/page owners.

## Amendment 11 — live CI and required-check delivery (2026-10-04)

This delivery note changes no decision. All six real PR CI jobs pass. After the
successful `openapi diff` job, its approved sixth required GitHub Actions context
is activated; full API read-back proves strict checking and every other live
protection setting preserved. The
[Step 4 delivery record](../roadmap/phase-02d-walking-skeleton.md#p02d-4-step-4-contract-sdk-and-ci)
owns the exact head, run, artifacts and activation proof. P02d-4 implementation,
review loops and rollout are complete in
[PR #25](https://github.com/HodeTech/LearnStack/pull/25), awaiting maintainer
review and merge. The original acceptance Status remains historical; P02d-5/6
remain the server consumer/browser owners.

## Amendment 12 — current delivery navigation (2026-10-05)

This delivery note changes no decision. The Status section now begins with a
dated current-delivery disclosure; its original acceptance statements and all
earlier amendments remain unchanged. Amendments 1–4 record host provenance,
read-only frames, site bootstrap and Education reads; Amendments 5–11 record
contract/SDK/CI delivery, reviews and required-check activation.
[ADR-0036 Amendment 9](0036-tenant-resolution-trusted-inputs.md#2026-10-05--amendment-9-public-host-provenance-delivery)
and ADR-0050/0051 Amendment 3 record their delivered P02d-4 parts without
rewriting acceptance history. The
[Step 4 delivery record](../roadmap/phase-02d-walking-skeleton.md#p02d-4-step-4-contract-sdk-and-ci)
owns current verification. PR #25 awaits maintainer review/merge; server/page
consumers remain P02d-5/6.

Updated carriers: this Status disclosure, the ADR-0036/0050/0051 delivery notes,
the Education module spec and the P02d-4 delivery record.
