# Education Audit Coverage Matrix

**Status:** Accepted design — 2026-09-14; updated 2026-10-02 with ADR-0050 and the
[module spec](README.md). Step 1 supplies an Education catalogue source and two
contextual verification queries classified Off. Every write row below remains a
forward declaration under
[Audit Coverage Standards](../../standards/18-audit-coverage.md).

| Resource | Operation | Class | Why |
|---|---|---|---|
| `Course` | `education.course.publish` `(planned)` | **MUST** | Makes eligible course marketing metadata publishable under ADR-0050; course publication remains MUST |
| `Lesson` | `education.lesson.publish` `(planned)` | **MUST** | Adds one publication prerequisite; anonymous body access also requires eligible parent and public policy under [ADR-0050](../../decisions/0050-publication-and-course-content-access.md) |

P02d-2's accepted contract names the full command set; its create/translation rows
below precede the handlers. Each implemented operation then loses `(planned)` and
gains its executable catalogue entry in the same commit. A translation belongs to
its root's captured navigation, with the owning course or lesson as audit subject.
No standalone satellite
publication or cross-root publication is declared.

Public-read classification belongs to P02d-4 G28. No anonymous request is shipped or
classified by this schema packet. This matrix cannot narrow the baseline MUST floor.

## P02d-2 accepted additions

Accepted on 2026-10-02 with the
[writer contract](README.md#p02d-2-accepted-writer-contract). These are classifications
ahead of code, not executable catalogue registrations:

| Resource | Operation | Class | Why |
|---|---|---|---|
| `Course` | `education.course.create` `(planned)` | SHOULD | Draft authoring, before public exposure |
| `Course` | `education.course.translation_add` `(planned)` | SHOULD | Draft-only contained translation; no independent satellite subject |
| `Lesson` | `education.lesson.create` `(planned)` | SHOULD | Draft authoring, independently scoped root |
| `Lesson` | `education.lesson.translation_add` `(planned)` | SHOULD | Draft-only contained translation and validated body |

The two publish operations remain MUST. Under accepted ADR-0050, course publication
exposes eligible marketing metadata; lesson publication enables content only under
the parent/access rules. Restriction does not lower the selected MUST classification.
`GetCourseSeedStateQuery` and `GetLessonSeedStateQuery` are explicitly Off in
`EducationAuditCatalogSource`; they declare no synthetic write operation.
