# Education Audit Coverage Matrix

**Status:** Accepted design — 2026-09-14, with the [module spec](README.md). No
Education request or catalogue source exists yet. Every row below is a forward
declaration under
[Audit Coverage Standards](../../standards/18-audit-coverage.md).

| Resource | Operation | Class | Why |
|---|---|---|---|
| `Course` | `education.course.publish` `(planned)` | **MUST** | Makes course content eligible for anonymous reading; the baseline requires course-publication audit |
| `Lesson` | `education.lesson.publish` `(planned)` | **MUST** | Makes the lesson body eligible when its parent is published, under [ADR-0048](../../decisions/0048-walking-skeleton-publication.md) |

P02d-2 decides the full command set and adds its create/translation rows before the
handlers. Each implemented operation then loses `(planned)` and gains its executable
catalogue entry in the same commit. A translation belongs to its root's captured
navigation, with the owning course or lesson as audit subject. No standalone satellite
publication or cross-root publication is declared.

Public-read classification belongs to P02d-4 G28. No anonymous request is shipped or
classified by this schema packet. This matrix cannot narrow the baseline MUST floor.
