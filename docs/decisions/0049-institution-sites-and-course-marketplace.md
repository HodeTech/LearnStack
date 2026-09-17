# ADR-0049: Institution Sites and an Optional Course Marketplace

## Status

Proposed — 2026-09-15. Product and architecture direction for maintainer review.
This draft accepts no P02d-2 gate and authorizes no implementation. The existing
Accepted ADRs remain binding unless a maintainer-approved decision supersedes them
under the repository's ADR governance.

**Date:** 2026-09-15
**Updated:** 2026-09-17
**Deciders:** Cemil (repository maintainer; approval pending)

## Decision Drivers

- The maintainer is considering a full course marketplace: a shared catalog,
  platform checkout, commission and payouts to education institutions.
- The target includes sales in Turkey and internationally. Whether sellers are
  initially Turkish institutions or institutions from multiple countries remains
  open; international buyers and international seller payouts are separate scopes.
- There is no current Self-Hosted customer commitment. The scenario must remain
  viable without turning a hypothetical integration into a launch dependency.
- Institutions may also need their own branded site, custom domain or platform
  subdomain. Marketplace participation must not require abandoning that site.
- [P02d-1](../roadmap/phase-02d-walking-skeleton.md#merge-and-closeout-2026-09-14)
  already supplies tenant-owned courses and lessons with database isolation.
  P02d-2 is about to introduce their first application writers and seed data.
- [ADR-0048](0048-walking-skeleton-publication.md) currently associates publication
  with anonymous lesson-body eligibility. A paid or private course needs an explicit
  access boundary before a public reader relies on that association.
- Shared discovery must not broaden an institution's access to another institution's
  learners, lesson bodies, finances or administration.

## Considered Options

1. **Institution sites with optional marketplace participation** (recommended).
   Shared education capabilities and ownership; separate presentation, discovery and
   commercial policies for each channel.
2. **Replace institution sites with a marketplace** (not recommended). Simpler public
   positioning, but removes the branded-site use case and the ability to serve an
   institution's existing audience independently.
3. **Keep only the current institution-site product** (not recommended for the new
   request). Fits the Accepted roadmap but supplies no shared course marketplace.
4. **Create an independently editable platform-owned copy of every course** (rejected).
   Creates competing authoring identities, progress and access grants. A deliberately
   licensed, versioned delivery replica is a different topology; it is not selected
   here and requires the distribution contract described under Deployment scope.

## Decision

The proposed decision is: LearnStack provides institution-owned sites and an optional
[Course Marketplace](../glossary.md#billing) over one education foundation. It adds a
LearnStack-branded education distribution product to the institution-site platform.

On acceptance, the binding product boundaries are exactly these:

1. An institution remains a tenant and owns its courses, lessons and operating data;
   its branches remain organizations under ADR-0017.
2. Institution sites work independently of marketplace participation. Institution
   admission and individual listings are explicit opt-ins.
3. A sales channel neither transfers content ownership nor grants lesson-body access.
4. The initial Course Marketplace serves centrally operated SaaS institutions.
   Dedicated and Self-Hosted participation is excluded from that initial scope.
5. Course Marketplace content and learner commerce stay outside the Hub's tenant
   subscription and deployment-metadata boundary.

These are proposed boundaries, not shipped behavior. The open contracts below are not
implicitly accepted with this paragraph, and no implementation can rely on them while
they remain open. The [acceptance checklist](#implementation-notes) names the decisions
needed to make this draft ready for acceptance.

## Open Implementation Contracts

The following candidate contracts and scope alternatives require explicit resolution.
Conditional P02d-2/P02d-4 obligations apply only if their protected-content scope is
approved through the G3 process below. Existing Accepted constraints continue to apply
regardless of this proposal.

### Institution and channel boundaries

This table describes target behavior. Course versions and learner access/progress
are future capabilities owned by
[Phase 05](../roadmap/phase-05-education-learning-content.md),
and [Phase 07](../roadmap/phase-07-enrollment-learner-portal.md).
[Phase 09](../roadmap/phase-09-billing-integrations-analytics.md) owns institution
storefront commerce in the current plan; Course Marketplace commerce needs the
separate ownership decision below. None is already supplied by P02d-1.

| Concern | Proposed ownership and behavior |
|---|---|
| Institution | A `Tenant`; its branches and campuses remain `Organization` values under [ADR-0017](0017-tenant-organization-hierarchy.md) |
| Content | Education retains the tenant-owned course and lesson identities, translations and exact customization bindings |
| Branded site | Institution branding, navigation and catalog presentation; custom domain remains optional |
| Marketplace | LearnStack-branded discovery, approved institution profiles and opt-in course listings |
| Sales terms | Separate from lesson content; identify the seller, course or version, channel, price, currency and commercial terms |
| Learner access | A grant for the owning institution's course/version; independent of which authorized channel originated the purchase |
| Progress | Within one installation, shared for the same learner and course-version enrollment; cross-installation progress requires an additional identity and delivery contract |

The same course can have different channel offers without duplicating its learning
content. An institution profile is not the institution's entire private record, and a
public instructor profile requires an explicit publication boundary.

An independent instructor can later be evaluated as a seller without reusing
`Organization` for unrelated businesses. Institution-only seller admission is an
explicit initial-scope proposal for maintainer approval. If individual sellers are
required at launch, that scope and their onboarding must be decided before the
marketplace plan is accepted.

### Publication, discovery and access

Three separate questions govern a course:

- Is its content published and ready for the applicable read path?
- In which channel, if any, may its public catalog information be discovered?
- May this caller read this lesson body or retrieve its protected media?

An approved catalog listing does not answer the third question. Free access, anonymous
previews and access requiring an enrollment are also distinct cases; a zero price
does not necessarily mean anonymous access.

The public API, renderer, caches and media delivery enforce the same access policy.
Hiding a link or a button is insufficient. An unavailable access evaluator refuses
protected content; it cannot fall back to the public path.

**Accepted baseline.** [G3's publication/transition contract](../roadmap/phase-02d-walking-skeleton.md#p02d-1-accepted-answers)
closed on 2026-09-14 through ADR-0048. P02d-2 inherits that public-only contract;
only command names and seeded states remain open under G3. This draft does not reopen
the closed part. The [pending proposal record](../roadmap/phase-02d-walking-skeleton.md#pending-course-marketplace-proposal)
keeps the request to reconsider it distinct from an approved change.

If the maintainer elects protected-content authoring in P02d-2, its decision pass must
first approve reopening that part of G3. The alternatives for that approval are:

- A minimal, persisted content-access policy before the first writers, with a forward
  migration over P02d-1's schema. This is the recommended option if P02d-2 is explicitly
  expanded to protected-content authoring. Approving the hybrid direction alone does
  not select it. It needs no prices, channels, orders or federation identifiers in
  Education.
- Retaining the current public-only skeleton contract explicitly. Protected content
  authoring remains unavailable until a separately owned decision and migration land
  before its first writer or reader. This is not an implicit promise that the current
  `published` flag can later protect paid content.

The first option needs a new superseding ADR replacing
[ADR-0048](0048-walking-skeleton-publication.md)'s anonymous-access implication. It
specifies exact policy, defaults, preview rules, migration treatment, command validation
and fail-closed behavior until
[Phase 07](../roadmap/phase-07-enrollment-learner-portal.md) provides Course Access.
Once that change is approved, append a dated G3 supersession entry to the phase's
decision record, link the new ADR from G3's status and the new entry, and update
affected packet criteria. Preserve the original question, accepted answer and delivery
record. Until those approval records exist, the current G3 answer remains binding.
If protected authoring is approved, P02d-4 must deny restricted bodies while no
authenticated grant reader exists.
Changing that contract is a new decision, not an erratum to a statement that was false
when written. This direction draft neither chooses the migration nor supersedes the
Accepted security contract.

### Shared discovery without shared private data

The candidate marketplace read path uses a dedicated projection of approved public
listing fields and their source identifiers. Source modules supply those fields through
[ADR-0010](0010-cross-module-communication.md)'s application contracts and durable
integration events; the catalog consumer is its **read-model projection** mechanism.
Durable delivery here is a future producer/consumer contract, not a new broker
requirement for P02d-2. ADR-0035 still gates a transport adapter on its named trigger.
A marketplace query does not remove Education's tenant filters
or borrow a platform-admin connection to enumerate private tables.

Listing withdrawal, source deletion and seller suspension have explicit propagation
and invalidation rules. Checkout revalidates the authoritative offer and seller
eligibility; a stale search result is never authorization to charge or grant access.
The projection's storage roles, audit classification and global read boundary need
their own accepted contract before that surface is implemented.

`learnstack.com` is the maintainer's illustrative Course Marketplace address, not an
allocated domain, a configured platform host or a replacement for existing
`learnstack.app`, `learnstack.dev` or local development host conventions. Selecting its
actual host and route classification remains open. On that proposed platform surface,
an institution profile path identifies a public marketplace resource, not an authority
to switch the caller's tenant. Full institution-site rendering under a path on the
same host, if selected, needs a separate trusted-resolution decision against
[ADR-0036](0036-tenant-resolution-trusted-inputs.md). The existing host resolver must
not gain an unchecked path or header fallback.

For a genuinely tenantless platform-host request, the existing context remains
**unresolved**; no tenant id is invented. In `TenantContextBehavior`, gate 1 admits
that request only with `[AllowsUnresolvedTenantContext]`. `[PublicSurface]` addresses
the separate gate for a **resolved `HostOnly`** context; neither marker implies the
other. The first catalog endpoint needs an accepted request/host matrix and an explicit
extension of the closed unresolved-request allow-list, authorization and audit
classification. A marker alone grants no database access.
The gates are nested: admitting an unresolved request does not run the `HostOnly`
gate. The contract must deliberately select platform-host, tenant-host or both
surfaces and prove their admission rules; applying both markers by default is not
that decision. An unresolved public read cannot invent a tenant-scoped audit row.

That contract must select the projection's table class and least-privilege reader role
before its first migration. `TenantId.PlatformSentinel` is never an announced request
tenant, under [ADR-0044](0044-audit-write-path.md); removing tenant filters or using a
platform-admin connection to read Education is not an alternative. Existing
`platform_host_to_tenant` and `platform_killswitches` are bounded platform-scoped
precedents, not blanket permission to add a global table. No new endpoint, table class
or role is approved by this draft.

### Identity and teaching experience

The existing [global User and tenant Membership design](../architecture/13-identity-and-auth.md#multi-tenant-identity-model)
fits learners buying from multiple institutions and instructors working with several
institutions within one installation. Those application capabilities are not implemented
yet. Dedicated and Self-Hosted installations may use independent identity issuers;
their local user identifiers and email addresses do not establish a shared learner.
Federation requires a verified issuer/subject binding to the central identity, with
explicit course-version and grant ownership. It must not let a customer-controlled
issuer mint central marketplace privileges.

A learner's combined library is an authorized projection of their own grants. It
does not expose an institution's roster to another institution. Cross-domain sign-in
uses the identity provider's supported redirect flow with origin-scoped sessions;
the proposal does not assume a cookie can be shared with arbitrary custom domains.

### Commerce and the Hub boundary

The domain serving a page does not decide who collects payment.
[Phase 09b's division of responsibility](../roadmap/phase-09b-hub-billing.md#division-of-responsibility)
and [ADR-0019](0019-learnstack-hub.md) currently distinguish two billing relationships.
This proposal adds a **third commercial relationship**; it is not already covered by
either existing payment port:

| Relationship | Standing and owner |
|---|---|
| Learner pays an institution through its storefront | Accepted Phase 09 scope, LearnStack core |
| Institution pays the LearnStack vendor for its software subscription | Accepted Phase 09b scope, Hub |
| Learner pays through the Course Marketplace; commission and institution payouts follow | Proposed new commerce scope outside Hub; authoritative module/service and delivery phase remain acceptance blockers |

Each learner order records its channel, seller, offer and payment arrangement
immutably. The proposed third relationship includes seller payables, refunds,
reconciliation and provider-backed payouts; Phase 09's existing storefront primitives
do not implement that relationship.

Seller, offer, payable and payout are descriptive proposal terms here, not newly
accepted Billing aggregate names. Reusing or translating Phase 09's order and
payment contracts belongs to the commerce-ownership decision.

They can share payment and order primitives while preserving their accounting
boundaries. Refunds and disputes follow the arrangement captured at purchase time,
not the institution's current settings. Provider charge models make this separation
material: responsibility for fees, refunds and disputes varies with the selected
flow ([Stripe Connect charge models](https://docs.stripe.com/connect/integration-recommendations)).
This reference is evidence about the distinction, not a provider selection.

Marketplace commerce belongs on the LearnStack product side. Its authoritative module
or service, global data scope and audit boundary must be named before this proposal is
accepted as an implementation plan; this draft does not select a new runtime. The Hub
continues to own institutions' LearnStack subscriptions and deployment metadata, under
[ADR-0034](0034-hub-contract-surface-invariant.md). It receives no learner orders or
course content through an expanded entitlement payload. The existing
[Hub Marketplace](../glossary.md#billing) belongs to Phase 12, not this proposal.
[Phase 12's unresolved ADR-0034 collision](../roadmap/phase-12-hub-marketplace.md#scope-on-the-learnstack-side)
is the precedent: publication does not automatically turn tenant-authored material into
Hub metadata. Course listings and instructor profiles remain on the LearnStack product
side in this proposal; seller commercial records are not added to the Hub's permitted
metadata by inference. Any alternative that puts them there needs its own cross-repo
decision under ADR-0034. This proposal does not settle Phase 12's bundle question.

### Deployment scope

The first shared marketplace targets LearnStack's centrally operated SaaS deployment.
Institution sites remain independent of marketplace participation. The other deployment
modes are still prepared seams, not supported releases, under
[ADR-0035](0035-demand-gated-infrastructure.md) and the
[deployment architecture](../architecture/25-deployment-models.md).

The rows distinguish network topologies within the existing enum values; they add no
`DeploymentMode` value or module-level mode branch.

| Deployment mode and network topology | Proposed central marketplace boundary |
|---|---|
| `Dedicated` — LearnStack-operated | Separate database and potentially separate identity issuer; needs an explicit cross-installation contract despite LearnStack operating it |
| `SelfHostedOnline` — publicly reachable learning surface | Possible opt-in external seller source only after identity, offer validation, delivery, reconciliation and support obligations are accepted |
| `SelfHostedOnline` — private/VPN-only learning surface | General buyers cannot reach the local learning surface; central checkout requires a separately accepted delivery arrangement |
| `SelfHostedAirGapped` | Local institution use; no live central catalog synchronization, checkout or grant delivery dependency |

For connected installations, compare remote fulfillment, a licensed central delivery
replica, and referral to the institution's own checkout before selecting a topology.
A referral alone does not satisfy platform checkout, commission and seller payouts.
A replica needs explicit export permission, an authoritative authoring source, immutable
version mapping, media rights, residency, withdrawal and continuity for existing buyers.
Neither arrangement is included in the proposed initial marketplace.

A customer-operated database, identity provider or signed event is not authoritative
evidence of central payment, seller eligibility or learner identity. Before federation,
define authenticated installation registration separately from tenant ownership, one
recognized active source at migration cutover, and handling of restored clones and
stale credentials. A shared codebase and globally unique identifiers supply neither
that trust boundary nor delivery availability.

Institution software licensing, marketplace seller eligibility and a learner's course
grant have separate lifecycles. Licence expiry, seller disconnect, refund and security
revocation need explicit effects on new sales, existing access and outstanding money.
The Hub entitlement projection is not a course-order or fulfillment protocol.

No new microservice, repository or infrastructure adapter is required merely to
accept the direction. Frontend separation follows
[ADR-0009](0009-frontend-single-app-first.md)'s measured split triggers.

## Context

The current [platform vision](../architecture/01-platform-vision.md) describes
LearnStack as infrastructure rather than an education product of its own. Its
[Non-goals](../architecture/01-platform-vision.md#non-goals) exclude an independent
instructor marketplace, and
[MVP scope § Deferred](../architecture/05-mvp-scope.md#deferred) places marketplace
features outside the roadmap. An institution-only seller policy
does not avoid the broader positioning change: LearnStack-branded discovery and
checkout add a product-facing role.
The current [Billing roadmap](../roadmap/phase-09-billing-integrations-analytics.md)
describes institution storefront payments, not platform commission and seller payouts.
Hybrid delivery is a product-scope expansion, not an already-supported configuration.

P02d-1's isolation and content ownership are reusable. The absent command, API and
frontend consumers make this a useful decision point, but do not make marketplace
identity, moderation or commerce implemented.

### Why these alternatives differ

Replacing institution sites discards the existing brand and standalone-deployment use
case without evidence that institutions prefer it. Retaining only institution sites is
the valid Accepted baseline, but does not answer the request for shared platform
checkout and payouts. The hybrid proposal preserves that baseline while adding an
explicitly separate commercial channel.

An independently editable platform-owned course copy splits authorship: edits,
withdrawal, version identity and learner progress acquire competing authorities. No
requirement currently justifies that split. A licensed immutable delivery replica
differs: its authoring source remains explicit, and export rights, version mapping and
withdrawal need a distribution contract before it can be selected.

### Evidence that would change the proposal

- Institutions unwilling to opt in, or buyers gaining no useful discovery advantage,
  favor retaining the institution-site product without a Course Marketplace.
- Demonstrated marketplace-only demand with no branded-site need would reopen the
  two-channel product cost, rather than make sites an unconditional permanent burden.
- Provider eligibility, delivery responsibility or support economics that cannot meet
  the intended country/seller model block that commercial scope before implementation.
- A signed Self-Hosted requirement with incompatible connectivity or residency needs
  reopens external delivery choices; a hypothetical customer does not trigger them.

These are decision-review triggers, not claims that market validation has occurred.

### One-way-door assessment before P02d-2

[ADR-0035](0035-demand-gated-infrastructure.md)'s test and
[Decision Timing](../roadmap/README.md#decision-timing) apply to the next consumer, not
to every possible future feature at once:

| Boundary | Would waiting change code written in the meantime? | Required disposition before that code |
|---|---|---|
| Publication versus protected-content access | Yes, if P02d-2 writes protected content or P02d-4 exposes it under the old public contract | Resolve G3 through the explicit process above before protected authoring; public-only work otherwise stays under ADR-0048 |
| Tenant-owned source identity and organization scope | Already structural in P02d-1; moving content to a platform tenant would change writers, grants and references | Retain tenant ownership and parent-derived scope in P02d-2; do not add channel-controlled ownership or accept a tenant id from a listing |
| Global catalog and commerce storage, role and request context | Yes for their first tables, queries and writer contracts; no existing Education query needs broader visibility merely because a separate projection is added | Accept those contracts before the first catalog/commerce migration, request or export writer; do not add a sentinel tenant or broaden existing RLS |
| Source publication/export contract | Yes once a writer promises marketplace listing updates | Decide consent, stable source ids, revision ordering, withdrawal and durable delivery before adding that producer contract; P02d-2 publication promises only tenant-local publication |
| Cross-installation identity and fulfillment | Yes for the first external listing, order or grant; P02d-2 has no such consumer | Keep external participation outside the initial scope; name and accept its delivery owner before admitting an external source |

P02d-2 must not encode `published = marketplace-listed`, platform-owned course copies,
global listing ids as tenant authority, or prices/payables in Education. A later
projection may backfill approved source data through tenant-scoped contracts; its
existence does not require rewriting every Education filter or migration. Its own
isolation and global-read decisions cannot wait until after that projection is written.
These missing product capabilities are not described as demand-gated adapters with
imaginary default implementations.

## Consequences

### Positive

- Institutions can retain their brand and audience while optionally obtaining
  marketplace distribution.
- Within one installation, content and learner progress retain one owning record across
  presentation channels.
- Tenant isolation and the customization model remain useful foundations.

### Negative

- LearnStack takes on two product experiences and marketplace operations: seller
  onboarding, moderation, support, refunds, disputes and payout reconciliation.
- Channel pricing, attribution, support responsibility and catalog duplication need
  explicit product rules; implementation cannot infer them from a hostname.
- Supporting both channels does not demonstrate market demand or Amazon-scale
  capacity. Those require commercial evidence and measured workloads.

## Implementation Notes

**Acceptance blockers — all open.** Before this ADR is marked Accepted, the maintainer
must approve its product boundaries and the decision pass must record:

| Open item | Required accepted record |
|---|---|
| Product positioning | Exact revisions to `CLAUDE.md` § What this is, the vision introduction and Non-goals, MVP scope and the repository README introduction; preserve the genericity boundary |
| Delivery ownership | A named Course Marketplace phase with packet sequencing, owning module/service and exit criteria in the roadmap; explicitly decide whether Phase 09 expands or a new phase owns the capability. Phase 12 is not a substitute |
| Authoritative commerce and public-read boundary | Name the owning module/service and the boundary between tenant source data, the public projection and commerce. Assign the detailed request/host matrix, table classes, audit and reader-role contract to that phase before their first consumers |
| P02d-2 access scope | Retain accepted public-only G3, or approve the explicit reopening and superseding access ADR before protected-content writers |
| Initial commercial scope | Seller eligibility, platform/seller/buyer country combinations, payment and invoicing responsibility, fulfillment responsibility and the owner of the remaining commerce rules |

This is an acceptance checklist, not a list of work silently deferred to unnamed
phases. No Course Marketplace implementation is assigned to an existing phase until
that roadmap decision is approved.

After those decisions are accepted, the implementation pass:

1. Applies the approved positioning and roadmap changes together; updates Phase 09b's
   money-flow explanation without transferring course commerce to Hub.
2. Completes P02d-2's remaining G3 command/seed-state details and other open gates.
   If protected authoring was selected, records the dated G3 supersession and new
   access ADR as described above before writing code. Otherwise preserves ADR-0048.
3. Keeps P02d-2's fixture ownership tenant-local. Explicit free/public examples prove
   the skeleton; any protected example must have an explicit denial contract before
   P02d-4's public readers. Marketplace orders and payouts are not fake seed outcomes.
4. Re-scopes P02d-4–6 where the accepted access and route decisions require it before
   OpenAPI and frontend contracts are frozen. Retain their tenant-isolation proofs.
5. Uses Phase 02b's durable events, Phase 03's identity, Phase 05's versioned content,
   Phase 07's access grants and Phase 09's commerce ownership as inputs to the revised
   sequencing, not reasons to implement an unauthorized shortcut in P02d-2.

The following business choices remain open before commerce implementation: the initial
platform/seller/buyer country and currency combinations, eligible seller types,
payment/invoicing responsibility, commission and attribution, refunds and payout timing.
Evaluate one seller per checkout as a scope-reduction option once payment and
fulfillment responsibilities are known; it still needs real platform payment,
commission and seller payout. Multi-seller
checkout is a separate scope decision. No provider or legal arrangement is selected by
this draft.

## Architecture Tests

Existing tenant/organization isolation, module-boundary, audit and context-provenance
tests remain required. The implementation decision pass assigns these additional
behavioral proofs to their first consumers:

- Publication or listing alone cannot expose a restricted lesson body or media URL.
- Marketplace participation and withdrawal cannot change content ownership.
- A projection contains only the declared public fields; a tenant cannot read another
  tenant's private source records.
- A public catalog request admits only its accepted host/context combinations and
  uses its read-only projection policy; neither marker nor the platform sentinel
  becomes authority to query private Education records or fabricate a tenant context.
- An equivalent valid access grant works across authorized presentation channels;
  a different learner's grant does not.
- Checkout refuses an ineligible seller or offer despite a stale listing, and replayed
  payment events cannot duplicate access grants, payable entries or payouts.
- A refund affects only access attributable to its durable order/fulfillment reference;
  independently justified access survives. The access contract must define that
  attribution and effective-access calculation before the first billing-source grant;
  `source = billing` alone cannot distinguish separate purchases.
- Before any external seller participates, prove source and identity binding, recovery
  after ambiguous fulfillment, and rejection of restored installations' stale authority.

These are proposed proof obligations, not registered or passing tests.

## References

- [Education module](../modules/education/README.md)
- [Phase 02d packet and gate register](../roadmap/phase-02d-walking-skeleton.md#packets-and-decision-gates)
- [Course Access](../glossary.md#enrollment--access)
- [Tenant isolation](../architecture/09-tenant-isolation.md)
- [Tenant customization](../architecture/32-tenant-customization-model.md)
