# Identity — Audit Coverage Matrix

**Status: Global and contextual seed writers implemented.** Current qualification
and review state lives in the [phase Status block](../../roadmap/phase-03-identity-admin.md).
The User off-path catalogue, contextual requests and persistence exist.
Derived from jointly Accepted [ADR-0072](../../decisions/0072-application-owned-identity-bootstrap.md),
[ADR-0073](../../decisions/0073-scoped-membership-and-identity-data-foundation.md)
and jointly Accepted [ADR-0076](../../decisions/0076-live-scoped-authorization.md) /
[ADR-0077](../../decisions/0077-trusted-authorization-evidence.md), with
[Standard 18](../../standards/18-audit-coverage.md). Part of the [module spec](README.md).

The baseline MUST floor applies. `IdentityAuditCatalogSource` declares
actual operations: catalogue-to-matrix coverage is total. Audit slugs are not
additional permission keys. The P03-2 rows below are accepted classifications,
with runtime writers and registrations pending. `(planned)` marks an absent
request/writer; `(off-path)` marks a central event without a MediatR request.
Catalogue → matrix remains total; the reverse request join applies only when
the request exists, and off-path events register by slug when their writer lands.
No synthetic request or executable registration is claimed.

| Resource | Operation | Class | Why |
|---|---|---|---|
| User/global immutable subject | `identity.user.create` `(off-path)` | **MUST** (`security-event`) | ADR-0072's confined creation/adoption owner and central non-success reconciliation; success shares the User transaction. Exact global witness, source and retention contract stays there. |
| Role including grants | `identity.role.initialize` | **MUST** (`security-event`) | `InitializeSeedRole`; one Role transaction, with contained grants. |
| Membership including Profile/Assignments | `identity.membership.create_first_seed_admin` | **MUST** (`security-event`) | `CreateFirstSeedAdminMembership`; one Membership transaction. |
| Membership including Profile/Assignments | `identity.membership.create_seed` | **MUST** (`security-event`) | `CreateSeedMembership`; one Membership transaction. |
| MembershipProfile, captured under Membership | `identity.membership_profile.set_seed_profile` | **MUST** (`update`) | `SetSeedMembershipProfile`; same Membership transaction. The slug matches the gated sub-resource while the row's subject remains its owning aggregate. |
| Authorization decision | `identity.authorization.deny` `(planned)` `(off-path)` | **MUST** (`security-event`) | Central HTTP/application/resource denial owner; one decided invocation event, separate from ordinary plural resource intents. No tenant override or host suppression budget. |
| Membership directory | `identity.membership.list` `(planned)` | **MUST** (`read-sensitive`) | Administrative directory query under existing Tenant `identity.membership.admin`; ordinary ReadWrite owner commits its row before disclosure. |
| Role including grants | `identity.role.change_grants` `(planned)` | **MUST** (`security-event`) | `ChangeRoleGrantsCommand`; one Role root and contained grant diff, expected row version and post-lock authority recheck; success/audit share the transaction. |
| Role including grants | `identity.role.adopt_manifest` `(planned)` | **MUST** (`security-event`) | `AdoptRoleGrantManifestCommand`; separately admitted finite v1 manifest adoption, one Role root and contained grant diff; no automatic registry/seed upgrade. |

**Explicit Off reads.** Contextual seed verification request types are registered
Off before use; they inspect only the exact bounded declared seed facts, with no
directory/provider profile. Ordinary existing-binding lookups create no mutation
intent under ADR-0072. Off request registration is separate from MUST operation
rows; no Off value is presented as an `OperationClass` or a MUST audit slug.

No seed command mutates a second root. Role grants travel in Role diffs;
assignments/Profile travel in Membership diffs. Instance-qualified paths
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

| Subject | Personal/reference paths to classify | Snapshot/diagnostic rule |
|---|---|---|
| User | AuthorityId, raw `provider_subject`, subject key, actor and immutable audit linkage; no email/verification mirror | Raw subject explicitly `[PiiSensitive]`; keys/actor are classified retained linkage, not diagnostic labels; canonical grants/encoding in Standard 05 |
| Membership | `UserId`, lifecycle/audit actor references and person-to-tenant/org relationship | Explicit reference classifications; no diagnostic value capture |
| MembershipProfile | Display name and locale preference | Both personal scalars marked for snapshot redaction; fixed diagnostics |
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
redaction. Steps 3/4 also extend the existing audit-interface/matrix controls
for the central global seams, exact classifications and planted wrong-column,
missing-global-row and child-capture controls; canonical signatures/SQL stay in
[Standard 05](../../standards/05-database.md#proposed-global-central-audit-branch).
No runtime test is claimed here.

P03-2's Accepted [trusted evidence contract](../../standards/18-audit-coverage.md#trusted-authorization-evidence)
owns immutable provenance, fixed denial occurrence/time and exact full-row
verification, pre-success barriers and refusal preservation. It keeps normal
resource classification unchanged and AccountSite success Off. These paths and
the four planned rows remain unimplemented here; P03-5 extends revision-aware
dynamic values; P03-10 delivers applicable user-reference locators and export/
erasure/closure under separate authority. A stub or retained hash is not completion
or anonymisation. The module table records current versus planned proof status.

Authorization Membership/Role IDs in `metadata.authorization` are retained
personal linkage, outside the six-column redaction grant. Each row follows its
own [retention target](../../standards/18-audit-coverage.md#retention): SecurityEvent
seven years, ReadSensitive two years. Purge remains Phase 11 work; P03-10's Audit
participant locates/reports surviving actor/subject linkage without metadata
UPDATE authority. No email mirror or Platform/operator flag population is added.
