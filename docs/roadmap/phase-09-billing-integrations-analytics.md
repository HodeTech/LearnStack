# Phase 09: Billing, Integrations, and Analytics

## Goal

Move LearnStack toward a real commercial and operational platform: package and
subscription foundations, external integrations, measurable analytics, and the search
engine that the earlier phases have been running without.

Billing exists as a core capability, while provider-specific and tenant-specific pricing
rules stay behind adapters and configuration.

This is also the phase where the platform starts being able to answer questions about
itself. Every phase up to here produced capability; this one produces the instruments
that say whether the capability is being used, what it costs, and whether anyone paid
for it.

## Scope

### Billing primitives

- Product.
- Plan.
- Price.
- Order.
- Subscription.
- Invoice reference.
- Payment provider account.
- **Billing-source entitlement bridge.** Phase 09 does not own the `Entitlement`
  aggregate ([Phase 07](phase-07-enrollment-learner-portal.md) does). It owns the
  producer side: a paid `Order` emits an `OrderPaidV1` integration event through the
  outbox, carrying the buyer's user id, the granted product (course / program /
  package), and the tenant context. The Enrollment module consumes it and creates an
  `Entitlement` with `source = billing`. See
  [Phase 07 § Entitlements](phase-07-enrollment-learner-portal.md) for the consumer
  contract, and [ADR-0010](../decisions/0010-cross-module-communication.md) for why this
  crossing is an integration event rather than a direct call.

### Stateful entitlement — credit packs and quotas

The [ADR-0018 Amendment (2026-08-08)](../decisions/0018-tenant-driven-customization-model.md)
draws the genericity boundary here explicitly: a ten-session credit pack or a
"three make-up classes per term" allowance is a **platform feature**, not tenant
customization data. A `TenantContentType` JSON Schema can declare a shape; it cannot
declare a ledger that is decremented, refunded, expired and audited.

So the capability is built in code, once, generically, and gated by plan:

- Phase 09 owns the **purchase**: a credit-pack `Product` with a quantity, sold through
  the same `Order` path as anything else, producing the same `OrderPaidV1`.
- [Phase 07](phase-07-enrollment-learner-portal.md) owns the **balance**: the pack lands
  as an `Entitlement` carrying a remaining count and an expiry, and every movement is
  audited.
- [Phase 08b](phase-08b-scheduling.md) owns the **decrement point**: confirming a
  `LiveBooking` consumes a credit; cancelling inside the tenant's window refunds it.

Naming these three in one place matters because the failure mode is a balance that two
modules both believe they own. If Phase 07's `Entitlement` has no room for a countable
balance, this phase is where that is discovered, and the fix belongs in Phase 07's
aggregate rather than in a second ledger here.

The pack's **size, price, expiry window and refund rule** are tenant configuration. The
ledger is not.

### Payment provider adapter

- Provider abstraction behind `IPaymentProvider`, per the adapter pattern in
  [06-extension-model.md](../architecture/06-extension-model.md).
- Webhook endpoint convention: signature-verified, idempotent on
  `(provider, event_id)`, tenant derived from the stored provider account and never
  from the payload — the same rules the classroom webhook follows in
  [Phase 08c](phase-08c-classroom.md).
- Payment status mapping from provider vocabulary into LearnStack's.
- Tenant-specific provider configuration; provider credentials read through
  `ISecretProvider`, never from tenant rows.

Candidate providers: Stripe, iyzico, PayTR, and a manual / offline provider. The manual
provider ships first and is not a placeholder — it is the one every deployment needs,
it exercises the whole order lifecycle without a network dependency, and it is what
integration tests run against.

### Commerce use cases

- Free course enrollment.
- Paid course access.
- Package / credit-pack purchase.
- Subscription placeholder.
- Coupon / discount placeholder.
- Manual payment approval.

### Integrations

By the time this phase starts, most adapters already exist — email and SMS from
[Phase 08a](phase-08a-assessment-notifications.md), storage from
[Phase 04](phase-04-cms-media-pages.md), live classroom from
[Phase 08c](phase-08c-classroom.md). What this phase adds is the **registry**: one
place where a tenant's configured providers, their credentials' secret references,
their health, and their per-tenant enablement are visible and manageable.

- Integration registry with per-tenant provider selection and health reporting.
- CRM provider placeholder.
- LTI / xAPI readiness — the export shape, not the certification.

### Search: engine, isolation, and index topology

Search has been running on the `ITenantSearch` port since
[Phase 04](phase-04-cms-media-pages.md), backed by **PostgreSQL full-text search**.
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md) demand-gates Meilisearch
behind that port, with the trigger "search quality or scale exceeds PostgreSQL FTS".
This phase is where the trigger is expected to fire. PostgreSQL FTS is adequate for
"find the row"; a public course-catalog search box is a different job. It has no typo
tolerance, no prefix or as-you-type matching, and no native faceting, and its Turkish
support is a Snowball stemmer rather than ICU-quality tokenisation of an agglutinative
language. Those are precisely the weaknesses
[ADR-0012](../decisions/0012-search-strategy.md) cites when it selects Meilisearch, and
a Turkish-serving tenant meets all four the first time a visitor mistypes a course
name.

Two things must be settled before the adapter lands. Both are corrections to the
existing design, and both are landed as a dated Amendment to
[ADR-0012](../decisions/0012-search-strategy.md) plus an update to
[20-search.md](../architecture/20-search.md).

#### The isolation problem

Every other data path in LearnStack carries four independent isolation layers
([ADR-0003](../decisions/0003-tenant-isolation-defense-in-depth.md)): request context,
EF query filter, PostgreSQL Row Level Security, and an architecture test. ADR-0012 as
written gives search **one** — a `tenant_id` filter composed in application code — and
hands the engine a master key with no tenant scope on it. One forgotten filter, or one
call path that reaches the SDK directly, and the isolation is gone with nothing behind
it. That is a single-layer surface inside a corpus that claims defense in depth
everywhere else.

Two observations sharpen this. First, the PostgreSQL FTS default that search runs on
until this phase is protected by RLS like every other table, because its projection
tables are ordinary tenant-owned tables. **The Meilisearch migration is the moment
search isolation gets weaker**, not stronger — so the adapter has to bring the missing
layer with it. Second, an engine that cannot enforce isolation itself is not
automatically disqualified; it just cannot be the only thing enforcing it.

The resolution — search gets four layers too, at different boundaries:

| Layer | Database path | Search path |
|---|---|---|
| Request context | `ITenantContext`, resolved per request | `ITenantSearch` resolves the tenant or throws; it never falls back to platform scope |
| Caller cannot bypass | EF global query filter | The helper composes the `tenant_id` filter and ANDs caller filters underneath it; callers pass criteria, never filter strings |
| **Engine-enforced** | PostgreSQL RLS | **Meilisearch tenant token** — a short-TTL token minted per request from the master key with the tenant's `searchRules` baked in. The engine refuses out-of-tenant documents even when the query layer is wrong |
| Mechanical | Architecture test | Architecture test banning direct SDK use outside the adapter namespace, plus a startup assertion that every registered index declares `tenant_id` filterable |

The engine-enforced layer is the one ADR-0012 is missing, and it is the one that turns
a code review into a guarantee. Concretely:

- The Meilisearch master key lives behind `ISecretProvider` and never leaves the search
  adapter. No request-handling code holds it.
- `ITenantSearch` mints a tenant token per request (or per short-lived cache window)
  scoped to the current tenant, and queries with that token.
- `IPlatformSearch` — the cross-tenant path for legal hold and abuse investigation —
  is the only caller allowed to use an unscoped key, it is a separate type, and every
  call writes a `platform-admin` audit entry per
  [18-audit-coverage.md](../standards/18-audit-coverage.md).
- Integration tests assert that a query issued with tenant A's token against a
  deliberately un-filtered request returns zero of tenant B's documents. The test that
  matters is the one where the application-layer filter is *removed on purpose*.

Residual risk, stated rather than hidden: both search layers live inside the same
process boundary, so a compromised application process defeats both. RLS has the same
property. The layers protect against the realistic failure — a bug, a new call path, a
forgotten filter — not against a compromised host, which is
[11-security.md](../standards/11-security.md)'s problem.

#### The index-explosion problem

ADR-0012 rejects index-per-tenant with clear arithmetic: 10,000 tenants × 4 locales ×
4 kinds is 160,000 indexes, and Meilisearch is not built for that cardinality. Then
[20-search.md § Tenant Content-Type Kinds](../architecture/20-search.md) reintroduces
exactly that explosion through a different door: a `TenantContentType` marked
`searchable: true` "materialises a search kind", producing an index per locale. Kinds
are tenant-declared, so the kind set is unbounded and grows with tenant count — the
rejected topology, arrived at by another route. It also collides: two tenants declaring
the same content-type key would share an index name while having different schemas.

The resolution — the index topology is closed and tenant-independent:

- **Built-in kinds are a closed, platform-owned set** (`course`, `content-entry`,
  `media`, `lesson-item`). One index per `(kind, locale)`. Adding a kind is a code
  change and a schema registration, never a tenant row.
- **Total index count is `|kinds| × |locales|`** — eight today with two locales — and
  it does not move when the ten-thousandth tenant signs up. That is the property the
  ADR wanted and the reason it rejected index-per-tenant.
- **Tenant-declared searchable content types do not get their own index.** They are
  documents in the shared `content-entry` index, discriminated by the
  `content_type_key` field the schema already carries, and always underneath the
  mandatory `tenant_id` filter. Two tenants using the same key never collide, because
  no query ever crosses the tenant filter.
- **Tenant-declared facets resolve through a flattened, generic facet map** —
  `facet_string.*`, `facet_number.*`, `facet_date.*` — rather than per-type fields. An
  English tenant's `level` facet and a yoga tenant's `difficulty` facet are the same
  index attribute with different values, which is precisely
  [ADR-0018](../decisions/0018-tenant-driven-customization-model.md)'s claim expressed
  in an index.
- **Facet declarations are capped per tenant**, enforced at `TenantContentType`
  registration. Filterable attributes are a shared, finite resource on a shared index;
  an uncapped declaration is one tenant degrading everyone's search.
- Locale stays an index split, unchanged. ADR-0012's reasoning there is sound: ICU
  tokenisation, stop words and stemming are genuinely per-language, the locale list is
  small and platform-controlled, and it does not grow with tenants.

#### Search work in this phase

- Meilisearch adapter behind `ITenantSearch` / `IPlatformSearch`, with tenant-token
  minting and the master key behind `ISecretProvider`.
- Reindex from the existing projection tables into the Meilisearch topology, with the
  alias-swap cutover from [20-search.md](../architecture/20-search.md) § Reindex.
- Search query telemetry feeding the analytics module.
- Platform-admin cross-tenant search via `IPlatformSearch`, audited.
- Drift dashboards and the nightly reconciliation surface.
- The dated ADR-0012 Amendment and the 20-search.md correction described above.

The outbox-driven indexing pipeline is engine-independent and does not change: the same
integration events that maintained the FTS projection tables feed the Meilisearch
indexer. That is what makes this a swap rather than a rewrite, and it is the payoff for
having shipped the port five phases before the engine.

### Live classroom usage analytics

The counters emitted in [Phase 08c](phase-08c-classroom.md) become reports here.
Provider-agnostic metrics:

- Session created, room opened.
- Participant joined, participant left, connection duration.
- Screen share started / stopped.
- Recording started / stopped, recorded minutes.
- Attendance status.
- Downstream bytes and concurrent-participant peak.
- Provider error.

These serve two consumers at once: learning analytics, and the provider cost monitoring
that feeds [Phase 08c](phase-08c-classroom.md)'s LiveKit Cloud-versus-self-hosted
decision rule. That rule reads its inputs from the reports built here — without them it
has no way to fire.

### Analytics

Event groups:

- Learning events.
- Content events.
- Commerce events.
- Admin events.
- Live classroom events.
- System events.

Read models:

- Course completion report.
- Enrollment report.
- Funnel report.
- Active learners.
- Content performance.
- Live session attendance report.
- Classroom usage and cost report.

Analytics read models are projections per
[ADR-0010](../decisions/0010-cross-module-communication.md) — they are built from
integration events, not from cross-module queries into other modules' tables. Every
event carries a version suffix from the first one; an unversioned analytics event is a
schema that can never change.

## Deliverables

- Billing domain primitives and the `OrderPaidV1` producer path.
- Payment adapter infrastructure with the manual provider working end to end.
- Credit-pack purchase path, with the balance boundary against
  [Phase 07](phase-07-enrollment-learner-portal.md) settled and written down.
- Integration registry with per-tenant provider configuration and health.
- Meilisearch adapter behind `ITenantSearch`, with engine-enforced tenant tokens and
  the closed index topology.
- `IPlatformSearch` cross-tenant search, audited.
- Reindex and nightly reconciliation, with drift dashboards.
- Dated ADR-0012 Amendment and 20-search.md correction covering isolation layers and
  index topology.
- Analytics event ingestion, the read models listed above, and the classroom usage and
  cost report.

## Completion Criteria

- Product, plan, and price can be created for a tenant.
- The manual payment provider drives an order to paid through the adapter, and a paid
  order produces an `Entitlement` in the Enrollment module via `OrderPaidV1`.
- A credit pack can be bought, its balance decremented by a confirmed booking, and
  refunded by a cancellation inside the tenant's window.
- Webhook idempotency is tested: the same provider event delivered twice produces one
  order state change.
- Search runs on Meilisearch through `ITenantSearch` with per-request tenant tokens.
- **A query issued with the application-layer tenant filter deliberately removed still
  returns zero cross-tenant documents**, because the engine rejects them. This is the
  criterion that proves the isolation layer is real.
- Total Meilisearch index count equals `|kinds| × |locales|` and does not change when a
  tenant is added — verified by adding a tenant with three searchable content types and
  observing no new index.
- Platform-admin cross-tenant search runs through `IPlatformSearch` and writes a
  `platform-admin` audit entry per call.
- Reindex completes with an alias swap and no search downtime.
- Learning, commerce, and classroom events can be reported, and the classroom cost
  report produces the inputs [Phase 08c](phase-08c-classroom.md)'s provider decision
  rule needs.

## Risks

- **Designing too closely around the first payment provider.** Provider vocabulary leaks
  into the domain most easily through status enums and webhook payload shapes. The
  manual provider shipping first is the mitigation: a domain that models manual payment
  cleanly is a domain nobody shaped around Stripe.
- **Merging billing and enrollment into the same model.** The bridge is one integration
  event in one direction. If Phase 09 code reads `Entitlement` or Phase 07 code reads
  `Order`, the boundary is gone.
- **A second credit ledger.** The most likely place is a "remaining sessions" counter
  added here for convenience while Phase 07 holds the authoritative one. Two counters
  disagree the first time a refund races a booking.
- **Migrating search without the engine-enforced layer.** The tempting version of this
  phase ships the Meilisearch adapter with the existing application-side filter and
  leaves tenant tokens for later. That version makes tenant isolation strictly weaker
  than it was on PostgreSQL FTS, which is a regression disguised as a feature.
- **Index topology settled by accident.** If the adapter is written before the
  ADR-0012 Amendment lands, whatever the first indexer does becomes the topology, and
  the tenant-declared-kind explosion arrives by default.
- **Designing analytics only for the current dashboard.** Read models built backwards
  from a specific chart cannot answer the next question. Model the events; derive the
  charts.
- **Growing event schemas without versioning.** Cheap now, and unfixable once a
  consumer exists in another process.
- **Ignoring classroom usage costs.** The cost counters exist from Phase 08c
  specifically so this phase can report on them; if the reports slip, the self-hosting
  decision in Phase 08c never gets its inputs and defaults to whatever was configured
  first.

## Phase Exit Decision

[Phase 10](phase-10-english-learning-mvp.md) begins when a tenant can sell something and
find something.

Concretely: an order goes from created to paid through the payment adapter and grants an
`Entitlement` the learner can act on; a credit pack's balance survives a purchase, a
booking and a cancellation without disagreeing with itself; search runs on Meilisearch
with cross-tenant leakage blocked by the engine and demonstrated under a test that
removes the application-layer filter on purpose; the index count is independent of
tenant count; and the analytics read models — including the classroom cost report —
return real numbers for a tenant that has actually been used.

If the Meilisearch trigger has **not** fired by the end of this phase — search quality on
PostgreSQL FTS is adequate for every live tenant — then the correct exit is to ship the
ADR-0012 Amendment and the topology decision, leave the adapter unwritten, and record
the trigger as still pending in
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md)'s table. A demand-gated
building block whose demand has not arrived is not an incomplete phase. Writing the
adapter anyway, before a tenant needs it, is the failure this roadmap is structured to
avoid.
