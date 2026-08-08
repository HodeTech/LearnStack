# Phase 08b: Scheduling and Booking

## Goal

Build the scheduling backbone — instructor availability, live session lifecycle, bookings, attendance modelling, session materials — without taking on the WebRTC classroom in the same milestone.

This phase was originally part of a combined "08B: Scheduling and In-App Classroom". It was split to reduce the load of a single phase (10 domain types, two largely independent subsystems) and to keep classroom integration risk isolated.

[Phase 08c](phase-08c-classroom.md) follows and delivers the in-app classroom that consumes the scheduling primitives built here.

Scheduling is deliberately a pure domain phase: it introduces no new infrastructure. Every mechanism it relies on already exists — the notification engine from [Phase 08a](phase-08a-assessment-notifications.md), the Hangfire background-job host and tenant-aware job activator from [Phase 02b](phase-02b-events-auth.md), and the outbox from [Phase 02a](phase-02a-kernel-tenancy.md).

## Scope

### Scheduling

- Instructor availability — recurring and one-off teaching windows.
- `LiveSession` aggregate — scheduled live event with time, capacity, role mix, session timezone, and lifecycle (`scheduled → opened → in-progress → ended → archived`).
- `LiveBooking` — reservation that ties a learner or cohort to a session; statuses (`pending`, `confirmed`, `cancelled`, `no_show`).
- Group and one-on-one readiness.
- Cancellation and reschedule semantics; conflict detection.
- Session capacity enforcement.
- Session material attachment — files, links, or content entries surfaced inside the classroom (rendered in [Phase 08c](phase-08c-classroom.md)).

### Notifications Wiring

Live-session notifications ride the notification engine built in
[Phase 08a](phase-08a-assessment-notifications.md) — dispatch orchestration, channel
adapters, delivery status and `TenantTemplateLibrary` template resolution all come from
there — and run on the Hangfire background-job host wired in
[Phase 02b](phase-02b-events-auth.md), which is also what carries tenant context into
the scheduled reminder jobs. This phase contributes only the trigger points and the
template keys; it builds no dispatch machinery of its own.

- Booking confirmation.
- Session reminder (24h, 1h).
- Cancellation / rescheduling notice.
- Instructor-side booking digest.

No-show notifications and attendance-derived notifications wait until
[Phase 08c](phase-08c-classroom.md) populates the actual attendance.

### Domain Concepts Delivered Here

- `InstructorAvailability`
- `LiveSession`
- `LiveSessionParticipant`
- `LiveBooking`
- `LiveSessionMaterial`

The classroom-runtime concepts (`LiveRoom`, `LiveRoomProvider`, `LiveRoomToken`, `LiveAttendance`, `LiveRecording`, `LiveSessionEvent`) land in [Phase 08c](phase-08c-classroom.md).

Every table behind these concepts is `[TenantOwned]`, and the scheduling ones are additionally `[OrganizationScoped]` — a branch schedules its own instructors and its own rooms. Each carries an EF global query filter and a Row Level Security policy built from the canonical template in [Database Standards](../standards/05-database.md), per [ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md).

## Deliverables

- Scheduling API.
- Booking flow end to end (admin / learner / instructor).
- Session material attachment.
- Notifications wired through [Phase 08a](phase-08a-assessment-notifications.md) for booking lifecycle events.
- Tenant timezone handling for session times.

## Completion Criteria

- Instructor availability can be defined.
- A learner or admin can create a session booking; conflicts and capacity are enforced.
- Sessions move through their lifecycle on schedule (`opened` at start time, `ended` at scheduled end or explicit close).
- Booking-related notifications dispatch via the [Phase 08a](phase-08a-assessment-notifications.md) engine, on [Phase 02b](phase-02b-events-auth.md) Hangfire jobs that carry tenant context into the reminder schedule.
- Sessions render correctly in the admin studio schedule view (without classroom controls — those come in [Phase 08c](phase-08c-classroom.md)).

## Risks

- Building scheduling as a general-purpose calendar product instead of a focused live-session scheduler.
- Hard-coupling scheduling to the live-classroom runtime; the seam between scheduling and classroom is the `LiveSession` aggregate id only.
- Timezone handling sloppiness — instructor availability windows have a timezone; sessions store UTC + the host's timezone at booking time.

## Phase Exit Decision

[Phase 08c](phase-08c-classroom.md) can begin when an instructor's availability produces bookable sessions, a booking survives the full lifecycle (create, confirm, reschedule, cancel) with conflicts and capacity enforced, the booking notifications arrive through the [Phase 08a](phase-08a-assessment-notifications.md) engine, and cross-tenant and cross-organization isolation tests over the new scheduling tables are green under `learnstack_app`.

The classroom phase consumes exactly one thing from here — the `LiveSession` aggregate id. If Phase 08c needs any other scheduling internal, the seam is wrong and belongs back in this phase.
