# Course Marketplace — Proposed Scope and Delivery Contracts

**Status: proposal, not an accepted architecture or delivery commitment.**
This companion to [ADR-0049](../decisions/0049-institution-sites-and-course-marketplace.md)
holds the detailed scope analysis. The ADR owns the proposed direction and acceptance
checklist. Existing Accepted decisions remain binding. No marketplace module, phase,
table, endpoint, feature key, payment provider or deployment adapter is implemented or
approved here.

**Review date:** 2026-10-02. The maintainer's target remains institution sites plus a
full marketplace, with Turkey and international sales. Initial seller geography is
deliberately undecided. The maintainer endorsed the recommendations and authorized
P02d-2 preparation. Exact access, security and commerce contracts still require
approval; legal/provider feasibility does not follow from a product preference.

## Proposed delivery ownership

**Endorsed planning target:** a product-side `Marketplace` module inside the
existing modular monolith, with catalog/operations and commerce responsibilities.
The proposed [Phase 09a](../roadmap/phase-09a-course-marketplace-pilot.md) records the
pilot packets and gates; it is not an Accepted delivery commitment.
Phase 09 keeps institution-storefront billing, Phase 09b keeps Hub
software billing, and Phase 12 keeps the Hub customization-bundle market.

| Owner | Boundary and dependencies |
|---|---|
| Education | Tenant-owned authoring, lessons and versions; versions belong to [Phase 05](../roadmap/phase-05-education-learning-content.md). No marketplace prices, inventory or payout state |
| Proposed Marketplace | Approved public profiles/listings, seller eligibility and moderation; channel offers and central orders, payables, refunds, payouts and reconciliation. Exact aggregates and global table classes require the commerce/security decisions |
| Billing | Existing [Phase 09](../roadmap/phase-09-billing-integrations-analytics.md) institution orders/payment contracts. Reuse of provider capabilities across the two commerce boundaries requires an approved application contract; no second owner for the same order |
| Enrollment | Sole owner of course access, enrollment and cohorts under [Phase 07](../roadmap/phase-07-enrollment-learner-portal.md); owns any accepted cohort-seat inventory, not implied by `CourseAccess` |
| Scheduling | [Phase 08b](../roadmap/phase-08b-scheduling.md) session availability, capacity, reservations, cancellation and rescheduling; shared site/marketplace capacity contract is still open |
| Identity / Audit | [Phase 03](../roadmap/phase-03-identity-admin.md) identity and authorization inputs; separate staff-access and data-protection decisions before their marketplace consumers |
| Hub | Software subscriptions and permitted tenant metadata under [ADR-0019](../decisions/0019-learnstack-hub.md); crossings remain bounded by [ADR-0034](../decisions/0034-hub-contract-surface-invariant.md). No learner commerce or source content |

Candidate P09a sequencing is: approve the scope and feasibility packet; implement
public catalog and seller operations; implement the selected offer's central checkout,
delivery and reconciliation; then run a bounded live pilot. Each packet has security
and failure proofs before its first consumer. Full checkout uses Phase 03 identity,
Phase 05 version identity, Phase 07 enrollment/access and Phase 09 payment inputs;
a live offer additionally needs the accepted capacity and classroom delivery contracts
in Phases 08b/08c. A referral pilot can omit central commerce dependencies, but its
scope must be explicitly selected.

Live sales also require the applicable production-readiness controls and exit proofs
in [Phase 11](../roadmap/phase-11-production-hardening.md), including recovery,
security and operational readiness. The approved sequencing must either satisfy that
gate or explicitly bring the required controls into the pilot's earlier delivery plan.
Do not assume foundation wiring is a production release, or require every demand-gated
adapter regardless of its trigger.

The proposed exit is an end-to-end purchase of the selected product, verified delivery,
recoverable payment/refund/payout handling, approved operational/privacy boundaries
and a recorded pilot stop/go decision. Broad rollout requires positive pilot evidence.
Approve the phase's contracts before moving its draft into the Accepted roadmap.
No new service, repository or frontend app follows from this proposal;
[ADR-0009](../decisions/0009-frontend-single-app-first.md) still governs frontend splits.

## First sale and delivery

A course/version identifies educational content, not every sellable delivery promise.
The same version can support several offers, but the first pilot selects **one** unit:

| Candidate | What the learner buys | Required delivery contract |
|---|---|---|
| Content access | Access to the identified version | Phase 07 course grant and protected content/media evaluation |
| Dated cohort place | Enrollment in a named cohort, schedule and any associated content | Phase 07 enrollment/cohort-seat authority; Phase 08b scheduling; capacity shared across institution site and marketplace |
| Session reservation | A specified live session or time slot | Phase 08b availability, temporary hold/expiry if used, confirmation, cancellation and rescheduling |
| Consumable session pack | A balance redeemable against future sessions | Separate ledger ADR and named release; no existing phase delivers consumption/refund/expiry accounting |

**Endorsed pilot candidate:** a dated cohort place. It is not ready to sell until its
inventory, delivery and commercial contracts exist. Recorded content is the smaller
delivery scope; session packages
must not be selected on the false assumption that Phase 09 supplies their ledger.

For any live offer, decide before checkout: authoritative seat/reservation owner,
branch, instructor, dates/time zone, teaching language, hold/confirmation behavior,
cancellation, schedule changes, no-shows and content-access inclusion. Institution
and marketplace writers consume the **same** authority; two grants do not prove two
available seats. If the last seat is bought through one channel, the other cannot
confirm it independently. Define recovery when payment succeeds but enrollment or
reservation fails, including buyer notification and refund obligations.

Prices and capacity do not belong on Education's course or lesson aggregates.
`OrderPaidV1` and `source = billing` in the existing plan do not prove live delivery
or identify which purchase justifies a grant. Resolve product-specific fulfillment
and durable order/fulfillment attribution before Phase 07's first billing consumer
contract/grant, with matching Phase 09 and P09a producer contracts. An alternative
requires a separately versioned marketplace consumer and migration before its first
grant. Refunds remove only that justification; independently justified access survives.

## Publication, discovery and access

Content publication, listing discoverability and caller access are separate questions.
Free enrollment, anonymous previews and restricted content are also separate; price
zero is not an access policy. Public APIs, renderers, caches and protected media enforce
the same policy. Failure of a protected-access evaluator denies access.

The current public-only G3 answer and [ADR-0048](../decisions/0048-walking-skeleton-publication.md)
remain binding. If protected authoring is requested in P02d-2, obtain explicit approval
to reopen G3 and write a superseding ADR before code. It selects persisted policy,
defaults, preview behavior, forward migration, command validation and fail-closed reads
until Phase 07 supplies grants. Append a dated G3 supersession and update affected
packet criteria while preserving the accepted question, answer and delivery history.
A hybrid-direction approval alone cannot make that change.

The alternative is an explicitly public-only skeleton. It supplies no private/paid
authoring promise; a later protected-content owner must land the decision and migration
before its first writer/reader. No prices, channel IDs, orders or federation identifiers
are needed in P02d-2 for either alternative. Public-only work is technically independent
of marketplace commerce. The maintainer released the preparation hold on
2026-10-02. The exact
[ADR-0050](../decisions/0050-publication-and-course-content-access.md) access proposal
and P02d-2 decision package still need approval before protected implementation.

## Public catalog and search

The candidate catalog is a dedicated projection containing **only approved public
fields** and stable source/revision identifiers, not a view exposing private Education
rows. An institution profile is not its private tenant record. Instructor biography,
image and discoverable attributes need explicit publication permission; moderation of
a listing grants no private lesson or learner access.

[ADR-0010](../decisions/0010-cross-module-communication.md)'s fourth mechanism,
read-model projection, owns the read path. Source application contracts and durable
outbox events supply approved changes. Consent/export, revision ordering, replay,
withdrawal, deletion and suspension propagation require a producer contract before its
first writer. A new broker is not a P02d-2 prerequisite; ADR-0035 still gates transport
adapters. Checkout revalidates authoritative eligibility and offer terms despite stale
search results.

[ADR-0012](../decisions/0012-search-strategy.md) and
[Search architecture](20-search.md) distinguish tenant search from privileged platform
search. `ITenantSearch` retains its tenant boundary. `IPlatformSearch` is an audited
administrative capability, not the public catalog port. Public search needs a separate
accepted contract over the minimized projection; it does not reuse a master key,
drop tenant filters or borrow a platform-admin connection. Phase 09's tenant-search
isolation work stays intact. Register the public index/query/writer boundary before
P09a's first search consumer; this proposal does not amend ADR-0012.

Shared marketplace facets need their own controlled meanings and localization.
Do not equate different institutions' level/taxonomy values merely because both are
called "beginner". Explicit mappings for public discovery do not rewrite tenant
taxonomies or access rules.

`learnstack.com` is the maintainer's illustrative address, not an allocated domain,
configured platform host or replacement for `learnstack.app`, `learnstack.dev` or local
hosts. An institution profile path identifies a public resource, not authority to
switch tenant. Full institution-site rendering under that path, if requested, needs a
separate [ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md) decision.

For genuinely tenantless platform-host requests, context remains **unresolved**.
Gate 1 in `TenantContextBehavior` requires `[AllowsUnresolvedTenantContext]`;
`[PublicSurface]` governs the separate, resolved `HostOnly` gate. The gates are nested,
not two annotations automatically applied together. Select platform-host, tenant-host
or both in the accepted request matrix and explicitly extend the closed unresolved
allow-list. Neither marker grants database access.

Select projection table class, least-privilege reader/writer roles and audit
classification before its first migration. Do not announce `TenantId.PlatformSentinel`
as a request tenant or fabricate tenant-scoped audit for an unresolved reader
([ADR-0044](../decisions/0044-audit-write-path.md)). Existing host-mapping and killswitch
tables are bounded precedents, not blanket permission for global data. The projection
writer's approved-field boundary matters as much as its read policy.

## Participation and genericity

Commerce ledgers, capacity state and external payments are platform code under
[ADR-0018](../decisions/0018-tenant-driven-customization-model.md), not tenant-authored
customization. Public descriptions and tenant presentation can remain data.

Separate plan availability, institution eligibility, listing consent, moderation and
operational suspension. Being on an eligible plan does not approve a seller or listing.
The endorsed business preference includes participation in all eligible SaaS plans,
with commission on marketplace sales. Plan availability is not seller admission;
eligibility, feature enforcement and financial terms still need accepted contracts.

The owning P09a decision uses [ADR-0021](../decisions/0021-feature-based-entitlement.md)
and [ADR-0045](../decisions/0045-entitlement-and-feature-flag-socket.md) for any feature,
limit and killswitch contract. Follow `add-feature-key` for accepted registry additions
before their consumers. Do not invent a key or implement entitlement bypasses now.
Seller withdrawal, security suspension, licence expiry and disconnection have distinct
effects on new sales, existing access and outstanding money; specify them together.

## Marketplace operations and support

**Endorsed business split:** LearnStack handles seller/listing
admission, marketplace checkout support and payment/refund/dispute coordination.
Institutions handle teaching, delivery and their own site customers. Record escalation,
cancellation authority, buyer notification, response targets and financial-loss
responsibility; "institution site independence" does not settle these duties.

The P09a staff-access decision names the backoffice owner, human staff population,
realm/token audience, permissions, resource scope and audit responsibility. Existing
rules separate `learnstack-hub` operators from `learnstack` tenant-facing endpoints
([ADR-0004](../decisions/0004-authentication-strategy.md),
[Security](../standards/11-security.md)). Existing internal-API crossings are bounded;
marketplace moderation is not automatically one of them. A changed realm admission or
new Hub crossing needs its own accepted decision; no third realm or service is assumed.

Default review access covers approved public listing fields. Private lesson inspection,
buyer order access and learner data require separate least-privilege permissions,
purpose/reason, resource and time scope, PII controls and an auditable exceptional
access contract. A tenant administrator cannot gain cross-tenant moderation by opt-in.
A listing approval is not a grant to inspect private education or all students.

Trust/safety rules cover seller verification, misleading or unlawful listings,
suspension/appeal, instructor publication rights and buyer complaints. Specify
attribution, ranking and promotions, channel pricing and who may contact the learner.
A marketplace purchase does not transfer the institution's entire customer relationship
to LearnStack.

## Privacy, residency and media

[Data Protection](23-data-protection.md#processor-agreements) describes the institution
service's tenant-controller / LearnStack-processor arrangement. **Inference requiring
legal assessment:** central buyer accounts, order history, fraud assessment and platform
support can have different purposes and responsibilities. Do not extend that blanket
arrangement merely because a course remains tenant-owned. The EDPB evaluates roles per
processing activity and actual purpose/means, not just entity or contract labels
([final Guidelines 07/2020](https://www.edpb.europa.eu/system/files/documents/2023-10/EDPB_guidelines_202007_controllerprocessor_final_en.pdf)).

Before P09a's first identity/order/support PII writer, approve a purpose matrix:
data categories, authority and legal roles; consent/legal basis; platform/institution
recipients; access, export, erasure, retention and payment-record exceptions;
subprocessor and incident responsibility. Update Architecture 23 and
[Phase 03's DSAR boundary](../roadmap/phase-03-identity-admin.md) together.

Resolve what happens to central purchases when a tenant membership is erased, what
an institution's export may include, and how a global account closure treats retained
financial records. Institution marketing permission does not automatically authorize
platform marketing. Ownership, legal controllership and database table class are
different decisions; none supplies another by inference.

One regional installation limits source topology; it does not prove no international
transfer. Assess buyer, seller, support access, payment/identity/storage subprocessors,
disaster recovery and media distribution against the actual permitted regions and
rights. Use the current [KVKK transfer framework](https://www.kvkk.gov.tr/Icerik/2053/Yurtdisina-Aktarim)
where applicable. Media export, previews, instructor images, withdrawal and existing
buyer continuity need explicit rights even without external-source federation.

## Installation and identity scope

**Recommended launch boundary:** one identified LearnStack-operated SaaS installation,
one selected region and institution sellers. Region and seller countries are unresolved.
[Residency architecture](23-data-protection.md#data-residency) permits separate regional
SaaS instances; `SaaS` alone therefore identifies neither one database nor one issuer.
International buyers are a commercial/transfer decision, not an instruction to federate
seller installations.

| Mode / topology | Proposed source participation |
|---|---|
| Selected regional `SaaS` installation | Initial candidate scope |
| Another `SaaS` installation, even LearnStack-operated | Excluded initially; cross-installation contract required |
| `Dedicated` | Excluded initially; separate database and possibly issuer |
| `SelfHostedOnline`, public learning surface | Future remote fulfillment, licensed replica or referral only after a delivery/trust decision |
| `SelfHostedOnline`, private/VPN-only | Buyers cannot rely on direct local delivery; needs a different accepted arrangement |
| `SelfHostedAirGapped` | Independent local institution use; no live central synchronization, checkout or grant-delivery dependency |

These are topologies within existing enum values, not new deployment modes or
module-level branches. Prepared seams are not supported releases; see
[Deployment Models](25-deployment-models.md#supported-today-versus-prepared-seam).

Within one installation the planned [global User / tenant Membership model](13-identity-and-auth.md#multi-tenant-identity-model)
can serve a learner across institutions. A combined library projects only their grants,
not cross-tenant rosters. Supported OIDC redirects and origin-scoped sessions handle
custom domains; arbitrary domains cannot share a session cookie by assumption.

External identity needs verified issuer/subject binding, not email equality. A customer
issuer cannot mint central privileges. Before any external source, decide authenticated
installation registration, source/tenant mapping, version identity, offer validation,
delivery acknowledgements, reconciliation and support. A restored clone, stale
credential or customer-signed event is not evidence of central payment or active
source authority.
At migration cutover recognize one active source and invalidate the former authority.

Compare remote fulfillment, referral and immutable licensed delivery replicas before
admitting an external source. Referral alone does not meet central-checkout goals.
Replicas need source/export permission, immutable version mapping, media rights,
residency, withdrawal and continued service to existing buyers. No connector or replica
is selected or needed for P02d-2.

## Financial and commercial contract

The approved P09a scope must identify the contractual education seller, invoice issuer,
payment collector and provider/card-network merchant of record. Those roles can differ;
a UI hostname does not select them. Stripe's
[merchant-of-record contract](https://docs.stripe.com/connect/merchant-of-record)
illustrates that charge configuration changes that provider role; it is not a provider
choice or a legal conclusion for LearnStack.

The endorsed business preference is that institutions sell the education and
LearnStack operates the marketplace. It does not determine those legal/provider
roles or approve an unvalidated country/currency arrangement.

Record platform/seller/buyer countries, currencies, institution seller types, KYC,
tax/invoicing and any applicable payment-intermediation or funds-handling requirements.
Provider and legal validation are needed before commerce implementation, not just a
generic `IPaymentProvider` interface. Turkey/international comparisons remain open:

- Turkish institution sellers / international buyers: international payment acceptance,
  refunds, currencies and applicable buyer obligations still need validation.
- Multiple-country sellers / international buyers: additionally validate onboarding,
  each payout corridor, settlement/currency restrictions and provider loss allocation.

Stripe's [business-country availability](https://stripe.com/global) does not list
Turkey for standard payment acceptance as reviewed on 2026-10-01.
[Connect cross-border payouts](https://docs.stripe.com/connect/cross-border-payouts)
have their own platform/account restrictions; international cards do not prove every
seller can receive a payout. [iyzico's marketplace documentation](https://docs.iyzico.com/urunler/pazaryeri)
describes sub-sellers, commission and foreign-currency payments, but does not establish
eligibility for every intended foreign seller. No provider is selected here.

Determine commission/fees, attribution, cancellation/refund policy, payout timing,
reserves or other accepted loss allocation, reconciliation and support duties from the
purchase-time arrangement. Provider refund/dispute/negative-balance responsibilities
vary by [charge model](https://docs.stripe.com/connect/integration-recommendations).
For applicable Turkish transactions, assess the intermediary's information, complaint
and record duties against the
[Ministry's distance-contract guidance](https://tuketici.ticaret.gov.tr/yayinlar/tuketici-bilgi-rehberi/mesafeli-sozlesmeler-hakkinda-bilgilendirme);
a referral pilot is not assumed to remove all intermediary responsibilities.

**Endorsed initial target:** one seller per checkout. That reduces splitting but still
requires central payment, commission, seller liability and payout. Payment collection,
delivery confirmation and money release are separate transitions: iyzico's
[approval API](https://docs.iyzico.com/urunler/pazaryeri/pazaryeri-entegrasyonu/onay)
is one concrete example. The eventual provider contract chooses their ordering and
recovery; it must not promise an atomic transaction spanning provider and database.

## Pilot evidence

The proposed P09a pilot needs approved scope and an explicit decision record **before
live sales**: participating institutions/offers/commission, selected product and
capacity contract, provider/legal eligibility, protected delivery, support/refund
readiness, applicable Phase 11 launch controls and reconciliation/recovery proofs.
A manual or referral discovery experiment can inform demand without pretending to
implement a central checkout.

Record measurement owners, cohort/sample and observation window, numeric stop/go
thresholds, and which costs count before the pilot starts. Measure opt-in supply,
incremental buyer discovery/conversion, delivered purchases, cancellation/refund/dispute
rates, support burden and contribution after payment fees, losses, acquisition and
delivery obligations. No numbers or demand evidence are invented in this draft.

Do not require live-pilot results before building its bounded implementation; require
feasibility before implementation, readiness before live sales, and positive evidence
before broad rollout. Unviable provider corridors, delivery promises or contribution
economics stop or re-scope that offer rather than authorize unchecked expansion.

## One-way-door assessment

[ADR-0035](../decisions/0035-demand-gated-infrastructure.md) and
[Decision Timing](../roadmap/README.md#decision-timing) apply to the next consumer:

| Boundary | Required before the first affected code |
|---|---|
| Protected publication | Approved G3 reopening, superseding ADR and migration before protected P02d-2 writers/readers; otherwise public-only baseline |
| Source identity and organization scope | Keep P02d-1 tenant ownership and parent-derived scope; listings are not tenant authority |
| Global catalog / commerce | Accept table classes, roles, host/context, audit and export rules before P09a migration or reader/producer; no broader Education filters |
| Source publication/export | Consent, revisions, withdrawal and durable delivery before the first listing producer; P02d-2 promises tenant-local publication only |
| Live capacity and fulfillment | Shared authority, holds/confirmation, cancellation and payment/delivery-failure recovery before the first live offer |
| Purchase-attributed access | Grant/fulfillment attribution before Phase 07's first billing consumer; coordinate Phase 09/P09a producers or explicitly version the new consumer before its first grant |
| Financial/PII writers | Country/provider/role feasibility, purpose-based privacy and recoverable accounting contracts before central commerce |
| External source | Accepted identity, trust, delivery and lifecycle contract before any other installation participates; excluded from initial P09a |

Later projection backfill can use tenant-scoped source contracts. Marketplace design
does not require rewriting every Education migration, but its own irreversible choices
cannot wait until after its producers/readers exist. These absent product capabilities
are not demand-gated adapters with imaginary defaults.

## Proposed proof obligations

These are **future behavioral obligations, not registered or passing tests**. P09a's
decision packet assigns implementations and tests before each consumer; applicable
existing isolation, module and audit tests continue to run.

- Listing/publication cannot expose restricted lesson bodies or protected media.
  Field minimization and writer permissions keep private data out of the projection.
- Participation/withdrawal cannot transfer content ownership. Public catalog admission
  obeys its host/context matrix and read-only policy; no invented tenant or privileged
  source query. Tenant and administrative search boundaries remain intact.
- Valid access works across authorized channels for its learner/version; another
  learner's grant fails. Refund removes only its purchase justification.
- Both sales channels contend for the same last live place; at most one confirmation.
  Expired holds, instructor cancellation and payment-without-delivery resolve according
  to the accepted buyer contract, not just a successful course grant.
- Checkout rejects stale/ineligible offers and sellers. Duplicate payment/delivery
  events cannot duplicate grants, payable movements, reservations or provider payouts.
- A crash after provider completion but before local persistence, or a lost response,
  leaves a recoverable unknown outcome. Retry uses the provider's idempotency/status
  contract; it cannot blindly charge or transfer again.
- Refund before a delayed payment event does not resurrect access or release money.
  Reordered events converge according to authoritative state; Stripe explicitly
  [does not guarantee event order](https://docs.stripe.com/webhooks#event-ordering).
- Paid-but-failed enrollment/reservation is reconciled, compensated or escalated under
  the chosen contract. Provider success alone cannot mark fulfillment complete.
- Failed refund/transfer, post-payout refund, insufficient balance and partial
  settlement produce reconciled liabilities and bounded retries/escalation; money
  movements cannot disappear into a generic success flag.
- Reconciliation compares provider and local order/delivery/payable/transfer facts;
  auditable repair cannot create double access, charge, refund or payout.
- Staff approval, suspension and exceptional private inspection enforce permission,
  purpose/resource scope and audit; tenant admins gain no platform authority.
- Privacy proofs cover membership erasure versus central retained orders, tenant export
  minimization and independently scoped platform/institution consent.
- Before external participation, prove issuer/source binding, delivery recovery and
  rejection of restored clones or stale authority in addition to all initial proofs.
