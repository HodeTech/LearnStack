# Phase 03 — delivery log

The dated record of
[Phase 03: Identity Domain, Authorization, and Admin Foundation](phase-03-identity-admin.md).
The phase document holds the plan and Status; this log holds dated records under
[ADR-0069](../decisions/0069-phase-documents-and-delivery-logs.md).

Entries are append-only after their packet ships. An explicitly Accepted decision
package is normative and its gate links the exact answer. Preparation records are
not acceptance of implementation choices. Navigate by the generated index below.

<!-- BEGIN GENERATED INDEX: scripts/delivery-log-index.py — run `make docs-index`; do not edit by hand -->

**Index**

- [P03-0 — Entry preparation](#p03-0--entry-preparation) — 83 lines
  - [Entry preparation (2026-10-25)](#entry-preparation-2026-10-25) — 81 lines

<!-- END GENERATED INDEX -->

## P03-0 — Entry preparation

### Entry preparation (2026-10-25)

**Scope.** Reconcile the phase plan and its connected contracts before the first
identity, membership or authorization writer. No runtime, schema, provider realm,
Hub crossing or deployment activation is changed. No Phase 03 implementation
decision package is Accepted in this entry.

**Verified baseline.** Preparation starts from development at
`819ef226053f06ffcf787ceaceefa09781240f32`, after completed Phase 02b. Relevant
production seams were rechecked against that source:

- `TenantTokenProfile` requires stable UUID actor claims; the native provider
  requires actor and tenant before callback publication. Development reconciliation
  only installs declared seed identity attributes, not arbitrary runtime accounts.
- `TenantResolverMiddleware` asks membership before installing tenant context.
  `AuthorizationBehavior` still passes through before the ordinary transaction.
  Membership and Platform admission defaults deny everyone.
- The authenticated native caller admits only AccountSite. Existing provider user
  attributes emit active scope; ordinary BFF refresh preserves the original scope.
- The shared JSON Schema profile requires nonempty root properties. Current
  Customization definitions are versioned; no custom-field aggregate is delivered.
- Platform scope has a separate bypass connection and no admitted actor consumer;
  ordinary event/job execution supplies no privileged redaction authority.
- The committed gateway scaffold has priorities and no Hub-secret reference, but
  retains version wildcard paths. Architecture 30 already documents their remedy.

**Reconciliation.** Each first-writer question now has a packet, vehicle and
observable proof in the phase's decision/delivery/criterion tables. Detailed
answers remain with their last responsible consumer under
[Decision Timing](README.md#decision-timing).

| Concern | Canonical disposition |
|---|---|
| Actor bootstrap before accepted login | G1 / P03-1; real nonseed and concurrent mapping proof in C01 |
| Pre-context membership/permission access | G2 / P03-1 contract and P03-2 execution; exact grants/setters and C05 |
| Platform population, surface and actor chain | G13 / first privileged consumer; preserve ADR-0036 and demand-gated Hub boundary |
| Per-session scope switching | G4 implications before shared schema, transition in P03-3; independent real refresh proof |
| Grant ceilings, seed defaults and revocation | G5 / foundation and authorization; Standard 19 identifies unresolved target choices |
| Membership/profile/lifecycle/global disclosure | G3 before migration; conceptual ER no longer implies a single role or cross-module Organization FK |
| Protected native/API operations | G6 / policy then transport; exact SDK/CSRF/ETag/replay/cache boundary in C06 |
| Invitation identity, races, atomicity and delivery | G8 before its first affected schema/writer; usable bounded handoff owned here, full engine remains Phase 08a |
| Subject/org/recipient DSAR authority | G15; RLS remains tenant/org isolation and never substitutes for subject predicates |
| Custom-field revision/classification and schema profile | G9; Customization owns definitions; unsafe global User helper and unversioned illustrative DDL removed |
| Provider-event topology and attribution | G10; authenticated durable intake, stable dedup and outbox-backed facts, no direct business publication |
| Tenant federation and account linking | G12; broker/email alone grants no membership, with scoped provider ownership/recovery proof |
| DSAR artifact/workflow/global closure | G16; private artifact and job prerequisites, retained authority, closure fence and privileged reconciliation |
| Denied/sensitive-read audit and enrichment | G7; actual HTTP/application evidence ownership, trusted request port and locators |
| Packet/register/exit completeness | Twelve packets, nineteen gates, D01–D16 and C01–C21 with inherited catalogue obligations |
| Gateway correction boundary | G18; explicit version paths and inactive scaffold correction here, reviewed public allowlist/lint/activation Phase 11 |
| Stale ownership/PII/target descriptions | Phase and Customization architecture reflect the delivered marker and Membership target; G19 owns first-PII census |
| HMAC threat statement | Gateway architecture distinguishes forged body signatures from independent JWT/mTLS admission |

**Validation.** Documentation-only checks pass on the reconciled sources:

- `make lint-docs`: 2,399 relative fragment links resolve, ADR-0069 plan/log/status
  structure conforms and three generated delivery-log indexes are current.
- Existing guard controls: 9 fragment, 12 roadmap-structure, 9 delivery-index and
  5 workflow-link tests pass, 35 in total.
- The changed-document file-target sweep resolves 1,014 relative links across
  17 Markdown files. Gate/deliverable/criterion identities are complete and unique;
  all nineteen implementation gates remain Open.
- `git diff --check` and the added-prose wrap check pass. No runtime/configuration
  file or Accepted ADR is edited; all changed-document references resolve to
  canonical repository sources.

The semantic self-review checks realm/actor boundaries, earliest schema/adapter
consumers, scope/filter/write-guard separation, aggregate/module ownership, audit
and privacy evidence, external-effect recovery and complete phase-exit coverage.
No backend/frontend/provider/browser suite is rerun: this record qualifies the
document preparation, not the undelivered runtime behavior in the criteria.

**Next boundary.** P03-1 begins with explicit acceptance of G1/G2/G3/G19 and the
schema-affecting G4/G5 parts. It includes G8/G16 only where their answers affect
the first identity tables. No later feature table or dormant writer may preempt
its own gate. Selecting a provider/platform mechanism that changes an Accepted
decision needs its correct disclosure or successor before implementation.

**Preparation versus delivery.** The document plan is ready for that decision
pass. Nonseed bootstrap, live authorization, operator admission and DSAR runtime
proof remain required implementation work; this entry claims none of them passed.
