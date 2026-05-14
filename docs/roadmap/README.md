# LearnStack Roadmap

This roadmap describes how LearnStack evolves from an architecture concept into a multi-tenant core platform for building education products.

LearnStack is not a single LMS implementation. It is an education-aware CMS, learning engine, and platform foundation that can power multiple brands, landing pages, catalogs, portals, and vertical education products.

## Phases

- [Phase 00: Product Strategy and Architecture Definition](phase-00-product-architecture.md)
- [Phase 01: Repository, Tooling, and Local Infrastructure](phase-01-repository-tooling.md)
- [Phase 02a: Platform Kernel and Multi-Tenancy](phase-02a-kernel-tenancy.md)
- [Phase 02b: Events, Outbox, and Identity Integration](phase-02b-events-auth.md)
- [Phase 03: Identity, Authorization, and Admin Foundation](phase-03-identity-admin.md)
- [Phase 04: Headless CMS, Page Builder, and Media Library](phase-04-cms-media-pages.md)
- [Phase 05: Education Catalog and Learning Content](phase-05-education-learning-content.md)
- [Phase 06: Public Site Renderer and Admin Studio](phase-06-renderer-admin-studio.md)
- [Phase 07: Enrollment, Learner Portal, and Progress Tracking](phase-07-enrollment-learner-portal.md)
- [Phase 08a: Assessment, Notifications, and Background Jobs](phase-08a-assessment-notifications.md)
- [Phase 08b: Scheduling and Booking](phase-08b-scheduling.md)
- [Phase 08c: In-App Live Classroom](phase-08c-classroom.md)
- [Phase 09: Billing, Integrations, and Analytics](phase-09-billing-integrations-analytics.md)
- [Phase 10: English Learning Vertical MVP](phase-10-english-learning-mvp.md)
- [Phase 11: Production Hardening, Operations, and Scale](phase-11-production-hardening.md)

## Roadmap Logic

The first goal is to build a reliable platform core:

- The tenant and domain model must be correct from the beginning.
- The modular monolith boundaries must remain clear.
- CMS and education catalog capabilities must work together.
- Public site, admin studio, and learner portal experiences should be powered by the same core platform.
- The English learning product should be implemented as a vertical product, not hardcoded into the core.
- Live online classes should happen inside the product experience through a provider-agnostic classroom layer.

## Success Criteria

At the end of this roadmap, LearnStack should be able to:

- Create a new tenant or education brand.
- Publish tenant-specific landing pages, navigation, course catalogs, and course detail pages.
- Manage courses, modules, lessons, and learning materials.
- Grant learner access to courses.
- Track learner progress.
- Run quiz and placement-test flows.
- Run in-app live online classes with scheduling, attendance, classroom events, recording consent, and recording metadata.
- Extend payment, notifications, search, storage, analytics, and live classroom providers through adapters.
