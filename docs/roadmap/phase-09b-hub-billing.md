# Phase 09b: Hub Billing and Invoicing (parallel track)

## Goal

Extend the **`learnstack-hub`** repository with the **platform-level billing** that
governs each tenant's LearnStack subscription: `Plan` ↔ `HubSubscription` lifecycle,
`HubInvoice` / `HubInvoiceLine` ledger, payment-provider adapter, usage-based
metering integration, dunning and grace-period rules, and the operator UI for these.

Runs **in parallel** with Phase 09 (LearnStack-side tenant storefront billing). Both
phases own their own surfaces and do not overlap:

| Concern | Owner |
|---------|-------|
| Tenant sells courses to learners | **Phase 09** (LearnStack core's `Billing` module — storefront) |
| LearnStack vendor charges a tenant for the LearnStack subscription | **Phase 09b** (Hub-side platform billing) |

Phase 02c shipped `Plan` and `HubSubscription` as **provisioning** primitives; this
phase fills in the **commercial** side.

Decisions referenced:

- [ADR-0019 LearnStack Hub](../decisions/0019-learnstack-hub.md)
- [ADR-0020 Triple Deployment + Hybrid License](../decisions/0020-triple-deployment-hybrid-license.md)
- [ADR-0021 Feature-Based Entitlement](../decisions/0021-feature-based-entitlement.md)

## Scope

### Hub-Side Aggregates

- `HubInvoice` / `HubInvoiceLine` — invoice ledger per tenant.
- `UsageAggregate` — aggregated usage by tenant + month (concurrent classroom
  sessions, total minutes, storage GB, learner count, custom-domain count).
- `DunningPolicy` — per-plan rules for missed-payment escalation.
- `PaymentProviderAccount` (Hub-side) — Stripe / iyzico / wire-only configurations
  for vendor's own payment collection.
- Extension of `HubSubscription` with `billing_state`, `current_period_start`,
  `current_period_end`, `cancel_at_period_end`, `dunning_state`, `grace_until`.

### Usage Ingestion

- LearnStack core's `POST /api/v1/usage/report` (already shipped in Phase 02c)
  produces the raw stream; this phase adds **aggregation** on Hub side: a Hangfire
  job rolls raw reports into `UsageAggregate` daily.
- Soft-limit alerts (`usage.alert.soft_limit_reached` event, already produced in
  Phase 02c) surface in the operator portal and (optionally) email the tenant admin.

### Billing Lifecycle

- New subscription on tenant create (via the existing Phase-02c flow) starts with
  `billing_state = trial`.
- Trial → active transition triggers the first invoice generation.
- Period-end (monthly / annual) closes the current period, generates an invoice
  (`HubInvoice`), and emits `learnstack.hub.invoice.generated` Dapr event.
- Failed payment enters `dunning_state = grace` for the configured window
  (default 14 days); during grace, the entitlement projection stays active. On grace
  expiry, the projection downgrades to a `read-only` feature set and a notice banner
  is pushed via the next entitlement refresh.
- Cancellation honours `cancel_at_period_end` (no immediate access loss).

### Payment Provider Adapters

- Stripe adapter (cards + ACH + SEPA).
- iyzico adapter (Turkish market).
- Manual / wire-transfer adapter (operator marks payment received).
- All three implement `IHubPaymentProvider`; adding a fourth is a code edit, not an
  ADR.

> **Naming.** Hub-side `IHubPaymentProvider` is **distinct** from the LearnStack-core
> `IPaymentProvider` (Phase 09's tenant-facing storefront billing). The two
> interfaces share a common shape (idempotency key, webhook signature verification,
> status mapping) but are scoped to different billing relationships:
> `IPaymentProvider` charges *learners* on behalf of a tenant; `IHubPaymentProvider`
> charges *tenants* on behalf of LearnStack for their LearnStack subscription. Hub
> adapters live in the `learnstack-hub` repo's
> `Hub.Infrastructure.Payments.{Stripe,Iyzico,Manual}` packages; LearnStack-core
> adapters live in `LearnStack.Infrastructure.Payments.{...}` in this repo.
> Mixing them in one process is forbidden by the Hub / LearnStack codebase
> separation invariant ([ADR-0019](../decisions/0019-learnstack-hub.md)).

### Operator Portal Extensions

- Per-tenant billing tab: subscription state, current period, recent invoices,
  payment-provider info, dunning state, grace expiry.
- Invoice viewer + PDF export.
- Plan-change workflow: operator switches a tenant's plan; entitlement projection is
  re-pushed; pro-rated invoice generated.
- Bulk invoice export (CSV) for accounting.

### Tenant-Facing Hooks (in LearnStack core, **not** new code in Hub)

- A read-only billing tab in Studio (LearnStack core) shows the tenant their own
  subscription state, recent invoices, and "next payment due" — sourced from a thin
  proxy API that calls the Hub. The proxy lives in LearnStack core and uses
  `IEntitlementProvider`'s billing-info extension; **no new Hub endpoint** is added
  to the four-endpoint contract surface.

### Compliance

- Tax handling per region (Stripe Tax for the Stripe adapter; manual table for
  others).
- Invoice retention 7 years (per the Hub-side audit retention floor).
- Hub-side audit entries for every operator billing action (plan change, manual
  invoice, refund).

## Deliverables

- Hub-side billing aggregates + schema + Hangfire jobs.
- Stripe + iyzico + manual payment-provider adapters.
- Operator portal billing surface.
- LearnStack-core proxy endpoint that the tenant's Studio billing tab consumes.
- Dunning + grace-period state machine working end-to-end.
- Invoice PDF generation pipeline.

## Completion Criteria

- An operator can move a tenant from trial → active → cancelled with appropriate
  invoices generated and entitlement projection refreshes.
- A failed payment triggers dunning; the tenant continues operating until grace
  expiry; on expiry, the projection downgrades to read-only and Studio shows the
  notice banner.
- The tenant's Studio billing tab shows accurate subscription + invoice data, sourced
  via the proxy from Hub.
- Operator audit log captures every billing action with `actor.hubOperator = true`.
- Hub-side architecture tests green; LearnStack-core architecture tests
  (`LearnStack_Modules_DoNotReference_Hub`, no new endpoints on internal API) still
  green.

## Risks

- **Two-billing confusion**: tenant admins conflating their own storefront billing
  with the platform billing they pay for LearnStack. Mitigated by clear UI separation
  (storefront in Studio's "Catalog → Products"; platform billing under
  "Settings → Subscription") + consistent terminology.
- **Payment provider drift**: Stripe / iyzico API breaking changes. Mitigated by
  adapter pattern + contract tests against recorded fixtures.
- **Grace period gaming**: tenants entering grace repeatedly. Mitigated by
  `IUsageReporter` continuing to report during grace; operator portal surfaces
  serial-grace tenants.

## Phase Exit Decision

Phase 09b is complete when SaaS deployment can charge a real tenant end-to-end:
operator provisions tenant → trial → active → invoice generated → payment captured
→ next period rolls over. Self-Hosted tenants do not need Phase 09b (they pay via
license-key purchase, not subscription billing).
