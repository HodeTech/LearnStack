# Phase 09: Billing, Integrations, and Analytics

## Goal

Move LearnStack toward a real commercial and operational platform: package/subscription foundations, external integrations, and measurable analytics.

Billing should exist as a core capability, but provider-specific and vertical-specific pricing rules should remain behind adapters and configuration.

## Scope

### Billing Primitives

- Product.
- Plan.
- Price.
- Order.
- Subscription.
- Invoice reference.
- Payment provider account.
- **Billing-source entitlement bridge.** Phase 09 does not own the `Entitlement` aggregate (Phase 07 does). It owns the producer side: a paid `Order` emits an `OrderPaidV1` integration event with the buyer's user id, the granted product (course / program / package), and the tenant context. The Enrollment module (Phase 07) consumes the event and creates an `Entitlement` with `source = billing`. See [Phase 07 § Entitlements](phase-07-enrollment-learner-portal.md) for the consumer contract.

### Payment Provider Adapter

Initial design:

- Provider abstraction.
- Webhook endpoint convention.
- Idempotency.
- Payment status mapping.
- Tenant-specific provider configuration.

Potential providers:

- Stripe.
- iyzico.
- PayTR.
- Manual/offline payment.

### Commerce Use Cases

- Free course enrollment.
- Paid course access.
- Package purchase.
- Subscription placeholder.
- Coupon/discount placeholder.
- Manual payment approval.

### Integrations

Adapter approach:

- Email provider.
- SMS provider.
- Live classroom provider.
- Live meeting fallback provider.
- Storage provider.
- Search provider.
- CRM provider placeholder.
- LTI/xAPI readiness.

### Live Classroom Usage Analytics

Track provider-agnostic metrics:

- Session created.
- Room opened.
- Participant joined.
- Participant left.
- Participant connection duration.
- Screen share started/stopped.
- Recording started/stopped.
- Attendance status.
- Provider error.

These events should support both learning analytics and provider cost monitoring.

### Analytics

Event groups:

- Learning events.
- Content events.
- Commerce events.
- Admin events.
- Live classroom events.
- System events.

Read model examples:

- Course completion report.
- Enrollment report.
- Funnel report.
- Active learners.
- Content performance.
- Live session attendance report.
- Classroom usage and cost report.

### Search

- Course search.
- Content search.
- Media search metadata.
- Indexing jobs.
- Tenant-scoped search.

## Deliverables

- Billing domain primitives.
- Payment adapter infrastructure.
- Integration registry.
- Analytics event ingestion and basic reports.
- Search indexing MVP.
- Live classroom usage reporting foundation.

## Completion Criteria

- Product, plan, and price can be created.
- Mock/manual payment provider works through the adapter.
- Successful order creates entitlement.
- Webhook idempotency is tested.
- Learning, commerce, and classroom events can be reported.
- Tenant-scoped search works.

## Risks

- Designing too closely around the first payment provider.
- Merging billing and enrollment into the same model.
- Designing analytics only for dashboard needs.
- Growing event schemas without versioning.
- Ignoring live classroom usage costs until after production launch.

