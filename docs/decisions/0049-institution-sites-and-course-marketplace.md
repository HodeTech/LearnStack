# ADR-0049: Institution Sites and an Optional Course Marketplace

## Status

Proposed — 2026-09-15. Product direction for maintainer review; updated 2026-10-01
after two external reviews and verification against the Accepted corpus and code.
This draft accepts no gate, authorizes no implementation and supersedes no ADR.

**Direction endorsement — 2026-10-02.** The maintainer endorsed the recommendations
and authorized P02d-2 preparation: institution sites plus optional full marketplace,
a dated-cohort pilot, one SaaS installation/region, single-seller checkout, a proposed
Marketplace module / Phase 09a, participation in all eligible SaaS plans and the
platform/institution support split. This releases the planning hold for preparation,
not approval of unseen access or commerce contracts. This ADR remains Proposed.

**Date:** 2026-09-15
**Deciders:** Cemil (repository maintainer; approval pending)

## Decision Drivers

- The maintainer's target is a full course marketplace: shared discovery, platform
  checkout, commission and institution payouts alongside institution-owned sites.
- Turkey and international sales are both targets. Initial seller countries,
  platform country, payment corridors and provider eligibility remain undecided.
  International buyers do not imply support for international seller payouts.
- There is no committed Self-Hosted customer. A hypothetical deployment must not
  create a launch dependency.
- LearnStack serves institutions that teach live. Buying content access, a cohort
  place, a session reservation or a consumable pack creates different obligations.
- [P02d-1](../roadmap/phase-02d-walking-skeleton.md#merge-and-closeout-2026-09-14)
  shipped tenant-owned courses and lessons with isolation. Their first application
  writers are next; [ADR-0050](0050-publication-and-course-content-access.md)
  separates publication/access under the accepted P02d-2 package, not commerce.
- Shared discovery must not expose private lessons, learners, finances or operations
  across institutions. Marketplace economics and operational readiness are unproven.

## Considered Options

1. **Institution sites plus an optional full Course Marketplace** (recommended).
   Reuses tenant-owned education; adds a distinct discovery and commercial channel.
2. **Discovery and referral only** (pilot alternative, not selected). Institutions
   handle checkout. Reduces central payment and payout scope but still needs consent,
   moderation, privacy and attribution rules; does not meet the full target alone.
3. **Replace institution sites with the marketplace** (not recommended). Removes
   independent branding and customer relationships without evidence of that demand.
4. **Retain only institution sites** (valid Accepted baseline). Avoids a new business
   model, but does not satisfy the marketplace request.
5. **Independently editable platform-owned course copies** (rejected). Creates
   competing authoring, access and progress authorities. An immutable licensed delivery
   replica is a different, unselected distribution topology.
6. **Put course commerce in Hub / Phase 12** (rejected under current decisions).
   Conflicts with [ADR-0034](0034-hub-contract-surface-invariant.md)'s content boundary;
   Hub's customization-bundle market is a different product. Changing that boundary
   would require a separate cross-repository decision.

## Decision

The proposed decision is: LearnStack provides institution-owned sites and an optional
[Course Marketplace](../glossary.md#billing) over one education foundation. It adds a
LearnStack-branded distribution product to the institution-site platform.

On acceptance, the binding product boundaries are exactly these:

1. An institution remains a tenant owning its courses, lessons and operating data;
   its branches remain organizations under [ADR-0017](0017-tenant-organization-hierarchy.md).
2. Institution sites operate independently. Institution admission and individual
   marketplace listings require explicit opt-in.
3. A sales channel transfers neither content ownership nor lesson-body access.
   Publication, discovery and caller access have separate contracts.
4. The proposed initial source scope is institutions in **one LearnStack-operated
   SaaS installation and one selected region**. Other SaaS installations, Dedicated
   and Self-Hosted sources require a later cross-installation contract.
5. Course Marketplace content and learner commerce remain on the LearnStack product
   side, outside Hub's software-subscription and permitted tenant-metadata boundary.

The direction is endorsed; the architecture remains Proposed, not shipped. The
[acceptance checklist](#implementation-notes) must close before this ADR is Accepted.
Its [scoping companion](../architecture/34-course-marketplace-scoping.md) describes
candidate delivery contracts; it does not accept them by reference.

## Context

### Product and investment change

Before this proposal, the [vision](../architecture/01-platform-vision.md) and
`CLAUDE.md` described LearnStack as infrastructure, not its own education product. The
[vision Non-goals](../architecture/01-platform-vision.md#non-goals) and
[MVP Deferred scope](../architecture/05-mvp-scope.md#deferred) excluded marketplace
features from the roadmap. The 2026-10-02 endorsement records a hybrid
target in those mutable documents without claiming an Accepted marketplace design.
Institution-only sellers do not avoid this positioning change.

Hybrid delivery preserves the branded-site use case. Marketplace-only delivery has
no validated demand advantage. Independent course copies split authorship, withdrawal,
version identity and learner progress; no requirement justifies that split. A licensed
replica retains an authoritative source but needs export and continuity contracts.
Referral is a possible pilot, not an assumed substitute for the requested checkout.

Commerce, shared inventory and external payments are platform capabilities under
[ADR-0018](0018-tenant-driven-customization-model.md), not `TenantContentType` rows.
Plan-entitlement governance does not select a paid tier or prove seller eligibility;
the [participation contract](../architecture/34-course-marketplace-scoping.md#participation-and-genericity)
keeps those choices separate.

### Publication, discovery and access

[G3's publication answer](../roadmap/phase-02d-walking-skeleton.md#p02d-1-accepted-answers)
closed on 2026-09-14 through ADR-0048. Hybrid direction endorsement alone did not
reopen that public-only implication. The maintainer subsequently approved
[ADR-0050](0050-publication-and-course-content-access.md) and the exact P02d-2 package
on 2026-10-02; the
[dated G3 supersession](../roadmap/phase-02d-walking-skeleton.md#g3-supersession-2026-10-02)
preserves the original question, accepted answer and delivery history. ADR-0050
owns independent publication, persisted policy, restricted backfill and denial
before anonymous lesson exposure. This marketplace proposal itself supersedes no ADR.

P02d-2 has no technical dependency on marketplace commerce. Its decision pass is
Accepted; P02d-2 policy, migration, writers and seed are delivered following
maintainer resumption. Public-read enforcement remains P02d-4, rendering P02d-6.
Commerce feasibility does not reopen its access decision or
silently block the independent packet.

### Commerce and the Hub boundary

[Phase 09b](../roadmap/phase-09b-hub-billing.md#division-of-responsibility) and
[ADR-0019](0019-learnstack-hub.md) define two existing relationships. This proposal
adds a third:

| Relationship | Standing and owner |
|---|---|
| Learner pays an institution through its storefront | Accepted Phase 09 scope; LearnStack core |
| Institution pays LearnStack for software | Accepted Phase 09b scope; Hub |
| Learner buys through the Course Marketplace; commission and seller payouts follow | Proposed new product-side scope; ownership and milestone require approval |

Hub owns software subscriptions, plans, licences and permitted tenant metadata under
ADR-0019; ADR-0034 constrains crossings and content. It receives no learner orders,
listings or course content through an enlarged entitlement payload. The
[Phase 12 content question](../roadmap/phase-12-hub-marketplace.md#scope-on-the-learnstack-side)
is a relevant precedent, not authorization for this commerce scope.

The proposed [module and milestone allocation](../architecture/34-course-marketplace-scoping.md#proposed-delivery-ownership)
and [Phase 09a draft](../roadmap/phase-09a-course-marketplace-pilot.md) keep commerce
outside Hub and Education. They do not select a provider, merchant of
record, legal role or new runtime. Content access alone does not fulfill a live seat.

### Evidence and decision timing

Institutions refusing offers or commission, buyers receiving no incremental discovery
benefit, or unsustainable support/payment/refund costs favor the existing site product.
Provider or legal constraints can invalidate a country/seller combination. A committed
external-deployment need can reopen distribution choices; a hypothetical one cannot.

The [pilot contract](../architecture/34-course-marketplace-scoping.md#pilot-evidence)
requires recorded entry conditions and measurable stop/go thresholds before a live
pilot; successful evidence precedes broad rollout. No market validation is claimed.
The [one-way-door assessment](../architecture/34-course-marketplace-scoping.md#one-way-door-assessment)
assigns each irreversible contract to its first consumer. It does not put speculative
prices, marketplace IDs, stock or payout fields into P02d-2.

## Consequences

### Positive

- Institutions retain their brand and audience while opting into distribution.
- Within one installation, content and enrollment progress keep one owner across
  authorized channels. Isolation and customization remain useful foundations.

### Negative

- LearnStack adds buyer acquisition, seller onboarding, moderation, support, refunds,
  disputes and reconciliation to operating two product experiences.
- Live delivery, shared capacity, customer attribution and data-protection roles need
  explicit contracts. Neither a hostname nor a payment-success event supplies them.
- This direction proves neither demand, positive unit economics nor Amazon-scale
  capacity; those require pilot evidence and measured workloads.

## Implementation Notes

**Acceptance disposition — 2026-10-02.** Product recommendations are endorsed; the
remaining architecture and commercial acceptance items are open. Before acceptance,
record approved
high-level boundaries below and their named delivery owners. Detailed security, privacy
and commerce decisions remain mandatory before their first consumers; this direction
cannot approve an unspecified schema, role or payment arrangement.

| Open item | Required acceptance record |
|---|---|
| Positioning | Exact revisions to `CLAUDE.md` § What this is, vision introduction/Non-goals, MVP scope and README; preserve ADR-0018's genericity boundary |
| Delivery ownership | Named marketplace module and phase, packet sequencing and exit criteria; evaluate the companion's proposed Marketplace module / P09a. Phase 12 is not a substitute |
| Pilot product and topology | Content access, cohort place or session reservation; full checkout versus referral pilot; institution seller type, one regional installation and single/multi-seller checkout. Name live capacity, cancellation and delivery-failure owners; no session-pack consumption without its separate ledger ADR/release |
| Commercial feasibility | Platform/seller/buyer countries and currencies; contractual education seller, invoice issuer, collector and provider/card-network merchant of record; provider eligibility, KYC, tax and applicable regulated-funds responsibilities, with legal/provider validation before commerce implementation |
| Participation and public discovery | Plan availability versus seller admission, listing opt-in, moderation and suspension; public-projection owner and a search contract distinct from tenant and privileged platform search |
| Operations and support | Backoffice owner and staff population; approval, suspension, refund/dispute and delivery support responsibilities. Assign realm/audience, permission/resource scope, exceptional private review and reasoned audit contracts before the first staff reader or crossing |
| Privacy and distribution | Purpose-based controller/processor assessment, platform versus institution permissions/consent, DSAR/export/erasure and retention ownership; data residency/transfers and media rights. Include Architecture 23 and Phase 03 in the approval impact set |
| Public-read and commerce security | Owner of the request/host matrix, table classes, reader/writer roles, audit classification and ordered/recoverable fulfillment/payable/payout contract before their first migrations, readers or producers |
| P02d-2 access and planning | ADR-0050/0051 and the exact package Accepted 2026-10-02, with dated G3 supersession; P02d-2 implementation delivered after maintainer resumption. This does not accept marketplace commerce |
| Pilot evidence | Approve the entry conditions, metric owners and a dated stop/go threshold record before a live pilot; positive evidence is a broad-rollout gate |

Approval of this draft requires the positioning and named roadmap changes together.
It does not move marketplace orders, payouts or pretend live fulfillment into P02d-2
seed data. That packet still closes its own command, seed and isolation gates. Any
approved access/route expansion updates P02d-4–6 before their contracts freeze.

## Architecture Tests

Existing isolation, module, audit and context-provenance tests remain required.
The [proof obligations](../architecture/34-course-marketplace-scoping.md#proposed-proof-obligations)
cover public-field minimization, authorized access, shared live capacity, stale offers,
source-scoped refunds, reordered events, ambiguous provider results, delivery failures
and reconciliation from the **first central SaaS commerce implementation**. External
source proofs are additional, not the start of failure handling.

These are proposed obligations, not registered or passing tests. Their owning phase
registers actual tests in the architecture catalogue when the implementations exist.

## References

- [Proposed marketplace scoping](../architecture/34-course-marketplace-scoping.md)
- [Education module](../modules/education/README.md)
- [P02d packet and gate register](../roadmap/phase-02d-walking-skeleton.md#packets-and-decision-gates)
- [Course Access](../glossary.md#enrollment--access)
- [Tenant isolation](../architecture/09-tenant-isolation.md)
- [Tenant customization](../architecture/32-tenant-customization-model.md)
- [Data protection](../architecture/23-data-protection.md)
- [Identity and DSAR](../roadmap/phase-03-identity-admin.md)
