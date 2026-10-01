# Phase 09a: Course Marketplace Pilot

> **Status (2026-10-02): Proposed planning artifact.** The maintainer endorsed the
> hybrid direction and recommended preparation boundaries. The owning `Marketplace`
> module, one-installation pilot and dated-cohort sales unit are endorsed planning
> targets, not implemented or fully accepted contracts. ADR-0049 remains Proposed.
> Platform company/country, seller geography, currency corridors, provider and legal
> roles remain open. This file authorizes no code, onboarding or live sales.
>
> Phase identifiers describe ownership, not execution order. Packet identifiers below
> are `P09a-*`; this phase is distinct from institution billing in Phase 09, software
> billing in Phase 09b and the Hub bundle marketplace in Phase 12.

## Goal

Evaluate an opt-in full Course Marketplace alongside independent institution sites:
shared discovery, one-seller platform checkout, commission and institution payout for
one selected live product. Measure incremental learner value and sustainable delivery
economics before broad rollout.

[ADR-0049](../decisions/0049-institution-sites-and-course-marketplace.md) owns the
direction and acceptance conditions;
[the scoping companion](../architecture/34-course-marketplace-scoping.md) owns the
candidate domain, public-data, operations and recovery boundaries. This phase owns
delivery sequencing and its first-consumer gates, not a second copy of those contracts.

## Scope

### Endorsed planning boundaries

- Institutions keep tenant-owned content and their own sites. Participation and
  individual listings are opt-in. Independent individual instructors are not initial
  sellers; a later scope expansion requires its own admission decision.
- Initial sources belong to one selected regional SaaS installation. Actual region
  and commercial country combinations are undecided. Other SaaS installations,
  Dedicated and Self-Hosted sources are outside this pilot.
- The proposed module lives in the modular monolith. Hub contains no course listings,
  learner orders or payouts. Reuse of Billing's payment capability needs an approved
  contract; an order has one authoritative owner.
- Dated cohort participation is the recommended and endorsed first sales unit.
  One cohort/seat authority serves institution-site and marketplace sales; a course
  grant alone is not proof of live fulfillment.
- The endorsed target includes participation in all eligible SaaS plans, with
  commission on marketplace sales. Entitlement, seller
  eligibility, consent, moderation and security suspension remain separate.
- LearnStack coordinates marketplace payments/support; institutions deliver teaching.
  Institutions are the preferred contractual education sellers; that business
  preference does not select provider merchant of record, invoice issuer or legal
  support/refund liabilities. Validate each before commerce.
- No session-consumption ledger, multi-seller cart, cross-installation federation,
  editable platform course copy or new infrastructure runtime is supplied here.

### Inputs and timing

Full checkout depends on Phase 03 identity/authorization, Phase 05 version identity,
Phase 07 enrollment/grants, Phase 08b capacity/reservations, Phase 08c live delivery
and Phase 09 payment inputs. Before **live sales**, satisfy the applicable Phase 11
production-readiness proofs or explicitly accept bringing them earlier into this plan.
Demand-gated adapters still require their named triggers; not every adapter is a
blanket pilot dependency.

Purchase/grant attribution must be designed before Phase 07's **first billing consumer
contract**, with matching Phase 09/09a producers, or a separately versioned marketplace
consumer and migration before its first grant. It cannot wait until a Phase 09a
payment event already depends on it. Other first-consumer dependencies are below.

### Decision register

All execution contracts remain open. An endorsement of a product preference is not
provider/legal approval or an accepted storage/authorization design.

| Gate | First consumer and required decision | Owner / blocking point |
|---|---|---|
| M1 — commercial feasibility | Platform entity/country; initial seller/buyer countries/currencies; seller/KYC eligibility; contractual seller/invoice/collector/provider MoR; tax, applicable funds-handling duties and written provider/legal feasibility | P09a-0; before commerce implementation or commercial commitments |
| M2 — live product | Named cohort/version/branch/schedule/language; one capacity authority across both channels; hold/confirmation/expiry, cancellation, failed delivery and refund obligations | Enrollment / Scheduling; before first offer/checkout writer |
| M3 — public discovery | Approved public fields and projection writer; independent public search contract; host/context matrix, table class, least-privilege roles, audit and index boundaries | Marketplace; before first projection migration, public reader or search consumer |
| M4 — staff operations | Staff population and backoffice owner; realm/audience and permission/resource scope; seller/listing approval, suspension, appeal and exceptional private inspection with reason/audit | Identity / Marketplace / Audit; before first staff reader or cross-repository crossing |
| M5 — privacy and rights | Purpose-based legal roles; consent and recipients; DSAR/export/erasure, retention exceptions and incident/subprocessor ownership; residency/transfers and media rights | Architecture 23 / Phase 03 owners; before first marketplace PII writer or source export |
| M6 — source publication | Consent, stable tenant/source/revision IDs, ordering/replay, deletion/withdrawal/suspension, invalidation and checkout revalidation | Source modules / Marketplace; before first listing producer |
| M7 — attributed access | Durable purchase/fulfillment justification, effective-access calculation and source-scoped revocation; no `source = billing` shortcut | Phase 07 first consumer; Phase 09/09a producer compatibility |
| M8 — recoverable commerce | Purchase-time terms/channel/seller; provider idempotency/status; reordered/lost events; payment/delivery/payable/payout state and reconciliation, reserves/loss allocation, refund/dispute recovery | Marketplace commerce decision; before first central order/money writer |
| M9 — participation | Plan availability and feature/limit/killswitch contract under ADR-0021/0045; admission and consent separate; existing access/outstanding funds survive or terminate under explicit lifecycle rules | P09a-0/2; registry decisions before consumers, no speculative key now |
| M10 — launch readiness | Applicable Phase 11 security, recovery, operations/runbooks and supported deployment proofs; no reliance on foundation wiring alone | Before P09a-4 live sales |
| M11 — pilot evidence | Sellers/offers/commission commitment; sample/window, metric owners, numeric stop/go thresholds and counted acquisition/support/payment/delivery costs | Before live pilot; positive evidence before broad rollout |

### Proposed packets

| Packet | Contents | Cannot start until |
|---|---|---|
| P09a-0 | Accepted scope/feasibility and sequencing record; exact first-consumer contracts and any required ADRs | Product direction approved; M1 and affected design decisions resolved; prerequisites verified |
| P09a-1 | Public profiles/listings and bounded public catalog/search projection | M3/M5/M6 accepted; applicable identity/source dependencies |
| P09a-2 | Seller admission, moderation, staff/support access and participation control | M4/M5/M9 accepted; catalog boundary |
| P09a-3 | Single-seller offer/checkout, shared live capacity, attributed fulfillment, refunds/payables/payouts and reconciliation | M1/M2/M7/M8 accepted; relevant Phases 07/08b/08c/09 inputs |
| P09a-4 | Readiness verification, bounded live pilot and evidence/closeout | Prior packets pass; M10/M11 accepted and readiness proved |

A referral experiment remains a separately selectable alternative, not the selected
full-checkout pilot. No current Phase 02d seed fakes marketplace sales or grants.

## Deliverables

- Accepted milestone/ADR package, module specification, permission and audit matrices.
- Opt-in approved public catalog and seller operations with tested privacy boundaries.
- One dated live offer and single-seller checkout with verified shared capacity.
- Recoverable, attributable delivery and provider-backed financial handling.
- Approved operational, privacy and launch proof records.
- A dated pilot result and stop/go decision with costs and limitations disclosed.

## Completion Criteria

- Institution sites remain independently usable; listing withdraws without ownership
  transfer or exposing private content.
- Public projection/search contains only declared public data; no privileged source
  connection, invented tenant context or tenant-search filter removal.
- Staff approval and support enforce resource-scoped permissions and audit;
  membership in an institution does not confer platform moderation.
- Competing site/marketplace purchases cannot confirm the same last cohort place.
- Purchase attribution supports independent grants and refund of only the relevant
  justification. Restricted content/media stays protected.
- Duplicate, reordered or lost provider events, ambiguous responses, failed delivery,
  post-payout refunds and reconciliation have tested recovery/escalation outcomes.
- Company/country/provider/legal roles and actual processing purposes are validated.
- Applicable launch controls pass before real payment, not only a manual-provider test.
- Pilot metrics are evaluated against thresholds recorded before launch. Broad rollout
  occurs only through a separate positive evidence decision.

## Risks

- Institution software demand does not prove profitable buyer acquisition.
- A successful course grant can conceal failed live delivery or oversold seats.
- International card acceptance does not prove foreign-seller onboarding/payout.
- Global discovery can accidentally become a global private-data or operator surface.
- A region label does not establish the complete transfer or media-rights boundary.
- Provider/local state can diverge even in one SaaS installation; retries alone do not
  reconcile money or fulfill the teaching promise.

## Phase Exit Decision

Close this **pilot** only with exact delivered scope, first-consumer ADR/contracts,
test and readiness evidence, financial/operational recovery records and a dated
stop/go outcome. A negative pilot result can close a bounded experiment; it cannot
authorize broad rollout. Name the next approved scope explicitly rather than
silently expanding to federation, multi-seller commerce or a session ledger.

This Proposed record does not change the Accepted roadmap baseline until its decision
package is approved. P02d-2 readiness depends on its own access and writer decisions,
not on closing these future commerce gates.
