# Phase 10: English Learning Vertical MVP

## Goal

Prove that LearnStack can produce a real vertical education product: an online English learning platform.

The core can evolve during this phase, but every change should pass one question: is this a generic platform capability or an English learning vertical requirement?

## Scope

### English Vertical Domain

- CEFR level taxonomy:
  - A1
  - A2
  - B1
  - B2
  - C1
  - C2 placeholder
- Placement test.
- Level recommendation.
- Grammar topic taxonomy.
- Vocabulary bank.
- Speaking practice content type.
- Teacher matching metadata.
- Lesson package definitions.

### Public Site

Initial pages:

- Home page.
- Courses page.
- Level pages.
- Placement test landing page.
- Instructor/teacher page.
- Pricing/packages page.
- Blog/resources page.
- Contact/lead form page.

### Learning Experience

- Learner onboarding.
- Placement test attempt.
- Recommended level/course.
- Enrolled course dashboard.
- Lesson player.
- Vocabulary resources.
- Speaking session booking.
- In-app classroom entry from the learner portal.

### In-App Speaking Sessions

English learning should use the LearnStack classroom capability for speaking sessions.

Initial scope:

- One-on-one speaking session.
- Small group speaking session readiness.
- Instructor joins from instructor portal.
- Learner joins from learner portal.
- Session material panel.
- Attendance.
- Session notes placeholder.
- Recording metadata placeholder.
- Speaking-session learning events.

Deferred:

- AI pronunciation scoring.
- Live transcription.
- Automatic speaking feedback.
- Breakout rooms.
- Advanced whiteboard.

### Instructor Experience

- Instructor profile.
- Availability management.
- Session list.
- Join classroom action.
- Learner notes placeholder.
- Attendance marking.

### Admin Experience

- Manage CEFR levels.
- Manage English courses.
- Manage placement test.
- Manage teachers.
- Manage lesson packages.
- View leads/enrollments.
- View speaking session schedule.

### Commercial Flow

MVP options:

- Manual enrollment.
- Manual payment approval.
- Optional online payment adapter.
- Lead form to admin follow-up.

## Deliverables

- First vertical product running on LearnStack.
- Tenant-specific English learning site.
- Placement test MVP.
- English course catalog.
- Learner portal flow.
- Teacher/session workflow.
- In-app speaking classroom MVP.

## Completion Criteria

- Visitor can open the English tenant public site.
- Visitor can start a placement test.
- Test result produces a level recommendation.
- Admin can enroll a user in the recommended course.
- Learner can open a course and complete a lesson.
- Learner can book or access a speaking session.
- Instructor and learner can join the session inside the portal.
- English-specific code is added without breaking core module boundaries.

## Risks

- Polluting core boundaries to ship the English MVP faster.
- Hardcoding CEFR concepts into the generic Level model.
- Starting pronunciation and AI feedback before the classroom workflow is stable.
- Letting landing page polish outrank platform validation.

