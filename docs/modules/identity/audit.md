# Identity — Audit Coverage Matrix

**Status: Proposed.** No request/catalogue/model below exists yet. Derived from
Proposed [ADR-0072](../../decisions/0072-application-owned-identity-bootstrap.md),
[ADR-0073](../../decisions/0073-scoped-membership-and-identity-data-foundation.md)
and [Standard 18](../../standards/18-audit-coverage.md). Part of the [module spec](README.md).

The baseline MUST floor applies. Planned `IdentityAuditCatalogSource` declares
actual operations: catalogue-to-matrix coverage is total; planned markers must
disappear when requests compile. Audit slugs are not additional permission keys.

| Planned request/path | Operation | Resource | Classification / owner |
|---|---|---|---|
| Global bootstrap/adoption/non-success reconciliation | Defined only in ADR-0072 | User/global immutable subject | Exact central contract there; no local SQL/retention variant |
| `InitializeSeedRole` | `identity.role.initialize` (planned) | Role including grants | MUST, same Role transaction |
| `CreateFirstSeedAdminMembership` | `identity.membership.create_first_seed_admin` (planned) | Membership/Profile/Assignments | MUST, same Membership transaction |
| `CreateSeedMembership` | `identity.membership.create_seed` (planned) | Membership/Profile/Assignments | MUST, same Membership transaction |
| `SetSeedMembershipProfile` | `identity.membership.set_seed_profile` (planned) | Membership including Profile | MUST, same Membership transaction |
| Contextual seed verification | `identity.seed_state.read` (planned) | Exact bounded declared seed facts | Explicit Off; no directory/provider profile; request registered before use |

No seed command mutates a second root. Role grants travel in Role diffs;
assignments/Profile travel in Membership diffs. Planned instance-qualified paths
include `/Membership/{id}/Profile`, `/Membership/{id}/RoleAssignments/{roleId}`
and `/Role/{id}/Grants/{scope}:{key}`. Collection snapshots appear only when
actually complete; partial loads cannot claim a complete collection. Stable
resource/subject keys are references, not safe diagnostic labels. Global paths
and non-success reconciliation follow ADR-0072's canonical owner.

## Personal-data declarations

Step 2's actual mapped-property census declares a typed category or explicit
None/purpose for **every** mapped property, including inherited actor references
and owned children. This table assigns semantic review; it is not a completed
census or a reflection/name heuristic.

| Planned subject | Personal/reference paths to classify | Snapshot/diagnostic rule |
|---|---|---|
| User | AuthorityId, raw `provider_subject`, subject key, actor and immutable audit linkage; no email/verification mirror | Raw subject explicitly `[PiiSensitive]`; keys/actor are classified retained linkage, not diagnostic labels; canonical grants/encoding in Standard 05 |
| Membership | `UserId`, lifecycle/audit actor references and person-to-tenant/org relationship | Explicit reference classifications; no diagnostic value capture |
| MembershipProfile | Display/contact fields and locale preference | Personal scalars/whole JSON marked for snapshot redaction; fixed diagnostics |
| Assignment | Membership/person linkage and assignment history | Exact paths under Membership row; never log raw DTOs |
| Role/Grant | Built-in key, capability scope, scoped keys, tenant and inherited actor references | None only for reviewed nonpersonal metadata; actor/linkage paths stay classified |

No credentials/token/browser binding, global active scope or dormant custom-field
blob belongs in these models. Publication does not make arbitrary tenant text
diagnostically safe. SQL-only/control/vendor inventory remains in canonical
[Data Protection](../../architecture/23-data-protection.md), not another durable-store list.

Translate expected conflicts/upstream failures to fixed bounded descriptors
before L1/error-tracking sinks; retain no value-bearing original inner exception.
Unexpected paths retain a fixed operational signal. Snapshot markers, name-based
logging guards and LS0002 remain distinct; no global exception scrubbing is claimed.

## Qualification and later owners

P03-1 Steps 2/4 prove actual root/child capture, MUST rollback, inherited
references and sensitive/neutral-key nested canaries at each real sink:
validation/response, logs, local archive, Sentry envelope and audit snapshot.
Every sink receives a positive event. Preserve existing TenantSetting whole-value
redaction. No runtime test is claimed here.

P03-2 adds normal authorization/denied/read enrichment; P03-5 extends revision-aware
dynamic values; P03-10 delivers applicable user-reference locators and export/
erasure/closure under separate authority. A stub or retained hash is not completion
or anonymisation. The module table records current versus planned proof status.
