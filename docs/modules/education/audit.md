# Education Audit Coverage Matrix

**Status:** P02d-2 Step 3 implemented; both review rounds passed — 2026-10-02. The six
writer registrations below and the two contextual verification queries (Off) are
executable in both composition roots under the [module spec](README.md) and
[Audit Coverage Standards](../../standards/18-audit-coverage.md).

| Resource | Operation | Class | Why |
|---|---|---|---|
| `Course` | `education.course.publish` | **MUST** | Makes eligible course marketing metadata publishable under ADR-0050; course publication remains MUST |
| `Lesson` | `education.lesson.publish` | **MUST** | Adds one publication prerequisite; anonymous body access also requires eligible parent and public policy under [ADR-0050](../../decisions/0050-publication-and-course-content-access.md) |

P02d-2's accepted contract names the full command set. Each operation has an
executable catalogue entry. A translation belongs to its root's captured navigation,
with the owning course or lesson as audit subject. No standalone satellite
publication or cross-root publication is declared.

**P02d-4 Accepted public classification — implementation pending.** The catalog,
course-detail and lesson-detail requests are Off under ADR-0052; their registrations
land with the marked types. Normal GET/HEAD reads add no audit row or synthetic
business-write operation. Independent rejected-assertion audit remains sanctioned.
No anonymous request is delivered by this acceptance. The writer MUST floor remains.

## P02d-2 accepted additions

Accepted on 2026-10-02 with the
[writer contract](README.md#p02d-2-accepted-writer-contract). Step 3 implements these
catalogue registrations:

| Resource | Operation | Class | Why |
|---|---|---|---|
| `Course` | `education.course.create` | SHOULD | Draft authoring, before public exposure |
| `Course` | `education.course.translation_add` | SHOULD | Draft-only contained translation; no independent satellite subject |
| `Lesson` | `education.lesson.create` | SHOULD | Draft authoring, independently scoped root |
| `Lesson` | `education.lesson.translation_add` | SHOULD | Draft-only contained translation and validated body |

The two publish operations remain MUST. Under accepted ADR-0050, course publication
exposes eligible marketing metadata; lesson publication enables content only under
the parent/access rules. Restriction does not lower the selected MUST classification.
`GetCourseSeedStateQuery` and `GetLessonSeedStateQuery` are explicitly Off in
`EducationAuditCatalogSource`; they declare no synthetic write operation.
