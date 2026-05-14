# Domain Model

This document describes the first domain shape. It is intentionally conceptual and should evolve as implementation begins.

## Tenancy

- Tenant: A logical education platform or brand.
- TenantDomain: Custom domain or subdomain connected to a tenant.
- TenantBranding: Logo, colors, typography, public theme settings.
- TenantFeatureFlag: Feature availability per tenant.
- TenantSettings: Locale, timezone, auth settings, billing settings, content settings.

## Identity

- User: A person known to the platform.
- UserProfile: Personal details and tenant-specific profile data.
- Role: Named role such as admin, editor, instructor, learner.
- Permission: Fine-grained capability.
- Membership: A user's relationship with a tenant or organization.
- Invitation: Invite flow for admins, instructors, editors, or learners.
- AuditLog: Security and administrative activity.

## Content and Pages

- ContentType: Definition of structured content types.
- ContentEntry: Instance of a content type.
- Page: Public or private page owned by a tenant.
- PageVersion: Draft/published page version.
- PageBlock: Composable block inside a page.
- NavigationMenu: Header, footer, sidebar, or contextual navigation.
- Redirect: Tenant-level URL redirects.

## Media

- MediaAsset: Image, video, document, audio, or generic file.
- MediaFolder: Logical organization.
- MediaVariant: Transcoded, resized, or optimized derivative.
- StorageObject: Physical object metadata for MinIO/S3.

## Education Catalog

- Program: A higher-level learning product.
- Course: A course listed in a tenant catalog.
- CourseVersion: Versioned course structure.
- Category: Catalog grouping.
- Level: Generic level model, for example beginner/intermediate or CEFR in an English product.
- Tag: Search and discovery metadata.
- InstructorProfile: Public instructor information.

## Learning Content

- Module: Group of lessons inside a course version.
- Lesson: Unit of learning.
- LessonItem: Video, article, file, quiz, assignment, live session reference, or embedded tool.
- LearningPath: Ordered or conditional sequence across courses or lessons.
- CompletionRule: Requirements for completing a lesson, module, or course.

## Assessment

- Assessment: Quiz, exam, placement test, survey, or assignment wrapper.
- QuestionBank: Reusable question collection.
- Question: Prompt and answer definition.
- Attempt: Learner attempt.
- AttemptAnswer: Submitted answer.
- Score: Scoring result and metadata.

## Enrollment and Access

- Enrollment: User access to a course, program, cohort, or product package.
- Entitlement: Permission to access a paid or assigned capability.
- Cohort: Group of learners moving through content together.
- Classroom: Scheduled or managed learning group.
- Progress: Learner progress against course structure.

## Scheduling

- InstructorAvailability: Available teaching windows.
- Session: Live class or appointment.
- Booking: Reservation for a learner or group.
- Attendance: Participation and completion record.
- LiveProviderMeeting: External provider metadata, such as Zoom or Google Meet.

## Billing

- Product: Sellable platform item.
- Plan: Package or subscription definition.
- Price: Currency, interval, and amount.
- Subscription: Recurring access.
- Order: Purchase intent and lifecycle.
- InvoiceReference: External invoice/payment record pointer.
- PaymentProviderAccount: Tenant-level provider configuration.

## Analytics

- LearningEvent: Learner activity event.
- ContentEvent: Content interaction event.
- CommerceEvent: Funnel and payment event.
- AdminEvent: Operational event.

Events should be append-only where possible and suitable for reporting, automation, and future learning analytics.

