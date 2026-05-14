# Platform Vision

LearnStack is a core platform for creating online education products.

It should behave less like a fixed LMS and more like an education-aware platform engine: content management, page composition, media management, course catalog, learning workflows, tenant configuration, integrations, and product-specific extensions should all be first-class concerns.

## Product Thesis

Education businesses usually need more than course CRUD.

They need:

- Public landing pages and SEO-friendly content.
- Program and course catalogs.
- Learning materials, quizzes, assignments, and progress tracking.
- Student, instructor, and admin portals.
- Tenant-specific branding, navigation, domains, and feature flags.
- Optional billing, authentication policies, scheduling, and integrations.
- A way to create different vertical products without rebuilding the foundation.

LearnStack should provide that foundation.

## Core Platform vs Vertical Product

The core platform owns reusable capabilities:

- Tenancy
- Identity
- Content and pages
- Media
- Education catalog
- Learning content
- Enrollment
- Assessment primitives
- Notifications
- Analytics events
- Integrations
- Billing primitives

Vertical products add domain-specific behavior:

- Online English education
- Exam preparation
- Corporate academy
- Kids education
- Instructor marketplace
- Certification programs

For example, an English learning product may add CEFR levels, placement tests, speaking practice, teacher matching, vocabulary banks, pronunciation feedback, and lesson packages. Those should not pollute the generic core.

## Design Principles

- Multi-tenant from the beginning.
- Modular monolith first, service extraction later only when needed.
- Headless core APIs with product-specific frontend experiences.
- Education-specific domain model, not a generic CMS with a few course fields.
- Provider adapters for payments, auth, storage, search, live class tools, and notifications.
- Auditability and event tracking as platform primitives.
- Versioned publish workflows for content that affects learners.

## Non-Goals for the First Version

- Building a full marketplace.
- Building a custom video conferencing system.
- Implementing every LMS standard immediately.
- Starting with microservices.
- Optimizing for every possible education domain before the first vertical product exists.

