# Phase 09b: Hub Billing (pointer)

> **Pointer document.** The authoritative plan for Hub billing lives in the
> **`learnstack-hub`** repository at `docs/roadmap/hub-billing.md`. This file records
> only what a LearnStack reader needs: why the phase exists, where the repository
> boundary falls, what starts the work, and the one LearnStack-side dependency. The Hub
> billing aggregates, the dunning and grace state machine, the invoice ledger and the
> operator portal surface are Hub concerns and are described there, not here.

## Goal

Let the LearnStack vendor charge a tenant for that tenant's LearnStack subscription —
plan and subscription lifecycle, invoicing, payment collection, dunning, grace, and the
operator surface over all of it. Every line of that is Hub-side code in the Hub
repository ([ADR-0019](../decisions/0019-learnstack-hub.md),
[ADR-0020](../decisions/0020-triple-deployment-hybrid-license.md)).

## Division of responsibility

Two different billing relationships exist. Conflating them is the most common reading
error in this corpus, so it is written out:

| Money flows | Phase | Where the code lives |
|---|---|---|
| A learner pays a **tenant** for a course | [Phase 09](phase-09-billing-integrations-analytics.md) — storefront billing | LearnStack core, behind `IPaymentProvider` |
| A tenant pays the **LearnStack vendor** for the platform | Phase 09b — platform billing | `learnstack-hub`, behind `IHubPaymentProvider` |

The two ports share a shape — idempotency key, webhook signature verification, status
mapping — and never run in the same process. Self-Hosted tenants skip this phase
entirely: they buy a licence key rather than a subscription.

## Scope on the LearnStack side

Deliberately almost nothing — but **two** things, not one. The first is **usage
reporting**: the
`IUsageReporter` adapter and `POST /api/v1/usage/report`, which ship in
[Phase 02c](phase-02c-hub-foundation.md) and are already part of the contract surface
enumerated in [ADR-0034](../decisions/0034-hub-contract-surface-invariant.md). Phase 09b
aggregates and bills against that stream; it does not extend it.

A read-only subscription view inside Studio is optional and, if built, reads through
`IEntitlementProvider` — no LearnStack module gains a dependency on Hub billing, and
`LearnStack_Modules_DoNotReference_Hub` stays green. Any new endpoint the Hub billing
design needs requires an ADR, per ADR-0034's second invariant.

## Trigger

Phase 09b starts when a tenant must be invoiced commercially — a signed SaaS or
Dedicated contract with a paying customer. Until then the roadmap holds the slot and the
Hub repository holds the design.

## Phase Exit Decision

Phase 09b is complete when a SaaS deployment charges a real tenant end to end: operator
provisions the tenant, trial converts to active, an invoice is generated, payment is
captured, and the next period rolls over — with LearnStack unchanged apart from the
usage it was already reporting. The exit criteria in detail live in the Hub repository's
`docs/roadmap/hub-billing.md`.
