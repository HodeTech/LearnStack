# Phase 08b: Scheduling and Booking

## Goal

Build the scheduling backbone — instructor availability, live session lifecycle, bookings, attendance modelling, session materials — without taking on the WebRTC classroom in the same milestone.

This phase was originally part of a combined "08B: Scheduling and In-App Classroom". It was split to reduce the load of a single phase (10 domain types, two largely independent subsystems) and to keep classroom integration risk isolated.

Phase 08c follows and delivers the in-app classroom that consumes the scheduling primitives built here.

## Scope

### Scheduling

- Instructor availability — recurring and one-off teaching windows.
- `LiveSession` aggregate — scheduled live event with time, capacity, role mix, session timezone, and lifecycle (`scheduled → opened → in-progress → ended → archived`).
- `LiveBooking` — reservation that ties a learner or cohort to a session; statuses (`pending`, `confirmed`, `cancelled`, `no_show`).
- Group and one-on-one readiness.
- Cancellation and reschedule semantics; conflict detection.
- Session capacity enforcement.
- Session material attachment — files, links, or content entries surfaced inside the classroom (rendered in Phase 08c).

### Notifications Wiring

Live-session notifications dispatch through the Phase 08a notification engine:

- Booking confirmation.
- Session reminder (24h, 1h).
- Cancellation / rescheduling notice.
- Instructor-side booking digest.

No-show notifications and attendance-derived notifications wait until Phase 08c populates the actual attendance.

### Domain Concepts Delivered Here

- `InstructorAvailability`
- `LiveSession`
- `LiveSessionParticipant`
- `LiveBooking`
- `LiveSessionMaterial`

The classroom-runtime concepts (`LiveRoom`, `LiveRoomProvider`, `LiveRoomToken`, `LiveAttendance`, `LiveRecording`, `LiveSessionEvent`) land in Phase 08c.

## Deliverables

- Scheduling API.
- Booking flow end to end (admin / learner / instructor).
- Session material attachment.
- Notifications wired through 08a for booking lifecycle events.
- Tenant timezone handling for session times.

## Completion Criteria

- Instructor availability can be defined.
- A learner or admin can create a session booking; conflicts and capacity are enforced.
- Sessions move through their lifecycle on schedule (`opened` at start time, `ended` at scheduled end or explicit close).
- Booking-related notifications dispatch via 08a.
- Sessions render correctly in the admin studio schedule view (without classroom controls — those come in 08c).

## Risks

- Building scheduling as a general-purpose calendar product instead of a focused live-session scheduler.
- Hard-coupling scheduling to the live-classroom runtime; the seam between scheduling and classroom is the `LiveSession` aggregate id only.
- Timezone handling sloppiness — instructor availability windows have a timezone; sessions store UTC + the host's timezone at booking time.

## Phase Exit Decision

Phase 08c can begin when scheduling, bookings, and material attachment are stable and notifications dispatch end to end.
