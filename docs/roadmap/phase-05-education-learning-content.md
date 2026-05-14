# Phase 05: Education Catalog and Learning Content

## Goal

Build the core education domain: program, course, course version, module, lesson, lesson item, and completion rule models.

The focus is to create a generic learning engine that is ready for vertical products such as online English education without hardcoding their domain concepts.

## Scope

### Catalog

- Program.
- Course.
- Course version.
- Category.
- Level.
- Tag.
- Instructor profile reference.
- Catalog visibility.
- Featured courses.
- Course SEO metadata.

### Course Versioning

Course and CourseVersion must be separated.

Reasons:

- Published course changes should not break existing learner experiences.
- Draft course structure can be prepared before publishing.
- Existing enrollments can remain attached to the correct version.

Required capabilities:

- Draft version.
- Published version.
- Version clone.
- Change summary.
- Publish validation.

### Learning Structure

- Module.
- Lesson.
- Lesson item.
- Lesson ordering.
- Optional and required lessons.
- Estimated duration.
- Prerequisite readiness.

### Lesson Item Types

Initial item types:

- Rich text lesson.
- Video.
- File/download.
- Embedded content.
- Quiz reference.
- Assignment reference placeholder.
- Live session reference placeholder.

### Completion Rules

Initial rules:

- Mark as complete.
- Video watched placeholder.
- Quiz passed placeholder.
- All required lessons completed.

### Admin Studio Education Screens

- Program list/detail.
- Course list/detail.
- Course structure editor.
- Module editor.
- Lesson editor.
- Lesson item editor.
- Course publish flow.

## Deliverables

- Education catalog API.
- Versioned course structure.
- Lesson content management.
- Course publish workflow.
- Public catalog rendering data.

## Completion Criteria

- Admin can create a course and add modules and lessons.
- Course can be edited as draft and then published.
- Published course detail can be read by the public site.
- CourseVersion behavior is covered by integration tests.
- Lesson items can be stored with different types.

## Risks

- Making Course directly mutable.
- Hardcoding a vertical-specific level system into the core.
- Freezing lesson item types too early.
- Overdesigning completion rules before the progress phase.

