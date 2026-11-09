# ADR-0095: Revision-Pinned Membership Custom Fields

## Status

Proposed

**Date:** 2026-11-10
**Deciders:** @cemil (approval pending)

This proposal answers P03-5/G9. It authorizes no migration, permission grant,
consent writer or runtime route before maintainer acceptance. The
[decision package](../roadmap/phase-03-delivery-log.md#p03-5-proposed-custom-field-contract-and-plan-2026-11-10)
owns the four implementation steps and their proof obligations.

On acceptance, dated Amendments in ADR-0043, ADR-0073, ADR-0074, ADR-0076,
ADR-0078 and ADR-0079 link the exact extensions below. Their historical bodies
remain intact and the records remain Accepted outside these bounded changes:

- ADR-0043's object-schema admission remains unchanged. A closed field envelope
  and a narrower form profile become its first Membership consumer.
- ADR-0073's profile acquires revision-pinned values; no global User column,
  cross-module foreign key or additional multi-root write exception is admitted.
- ADR-0074's existing fence protocol gains named Customization definition and
  Tenancy consent consumers; existing Identity lock-mode derivation is unchanged.
- ADR-0076/0078 gain exact custom-field operations and two self-consent writes.
  The existing own-Membership **read** policy grants no mutation authority.
- ADR-0079 gains one assignable Tenant pair through explicit registry v3 adoption,
  one advisory capability and the closed protected receipt/transport consumers.
  Existing profile-write holders gain the bounded custom-value operation.

## Decision Drivers

- Phase 03 requires a working definition editor and membership form, with real
  schema validation, privacy classification and historical interpretation.
- A schema change cannot reinterpret another person's stored value, require
  global User storage or silently convert existing memberships.
- Arbitrary tenant field names defeat name-based diagnostic scrubbing. Schema,
  values and consent evidence need explicit capture and disclosure boundaries.
- Sensitive values need a real person's purpose-specific consent. An admin's
  profile edit or an unchecked default cannot create that evidence.
- The delivered native transport has finite routes, a 16 KiB request limit and
  256 KiB responses. A valid field must be usable within those limits.
- Four coherent implementation steps keep the review cycles proportional to
  ownership and security boundaries; no unrelated CI-window work is selected.

## Considered Options

1. **Immutable definitions, pinned values and explicit personal consent**
   (chosen). Reuse schema admission, module contracts, transaction owners,
   protected transport and the existing Tenant fence.
2. **One mutable definition per key** (rejected). Editing classification or
   type changes the meaning of historical data and can hide retained PII.
3. **Full JSON Schema authoring and a general visual builder** (rejected here).
   Validation alone does not supply usable controls for every schema keyword.
   Phase 06 owns the general builder; this packet admits a finite form profile.
4. **Refuse all sensitive values until another packet supplies consent**
   (not selected). It leaves a required classification without a usable path;
   the bounded consent slice fits this packet without a new provider or bypass.
5. **New generic JSONB or cross-module write endpoint** (rejected). It evades
   schema pins, resource admission and audit ownership.

## Decision

LearnStack stores Membership custom values with immutable Customization
definition pins inside Identity's MembershipProfile. Definitions admit a bounded
form profile through the existing object-schema validator. Tenancy owns separate
append-only personal consent acts for sensitive fields. Explicit schema
transitions, current authorization, revision-aware disclosure and whole-value
diagnostic redaction govern all reads and writes.

## Context

### Ownership and immutable history

`TenantCustomFieldDef` is a tenant-wide Customization aggregate. Only
`target_entity = Membership` is admitted. Organization-specific definitions and
User, Course, Lesson, Enrollment and LiveSession targets refuse; their later
target ownership remains with the phases named in
[Phase 03](../roadmap/phase-03-identity-admin.md#tenant-custom-fields).

The concept key is tenant/target-qualified and permanently reserved. Revisions
have distinct typed IDs and increasing integer `schema_version`, unique on
tenant/target/key/version. At most one revision per concept is Active. Draft
body editing, publication and retirement are explicit acts; publication retires
the incumbent and activates its successor in one transaction. Published schema,
labels, classification, purpose, requiredness and ordering are immutable.
Changing any of them publishes a successor; no compatibility claim permits an
in-place edit. Drafts can be discarded but retain their key/version reservation.
Referenced revisions are never hard-deleted by this packet.

Each MembershipProfile slot retains concept key, exact definition ID/version,
JSON value and any consent evidence ID. Identity resolves definitions through
Customization Application.Contracts on its existing ambient owner; no join,
navigation, FK into another module, second business transaction or raw setter is
added. Exact revision reads are uncached and tenant-filtered. New binding uses
Active-at-read eligibility, as the existing exact-definition contract does;
concurrent later retirement does not invalidate the admitted pin. This is not
an Active-at-COMMIT guarantee.

Existing pins remain interpretable after successor publication or retirement.
They are read-only when their revision is no longer Active; explicit removal or
replacement with the current Active revision is permitted. Replacement validates
the new value and obtains new consent where required; it performs no automatic
conversion, backfill or consent transfer. A classification downgrade never
changes an older value's pinned classification. A revoked Membership's successor
starts without the retired membership's custom values or consent.

### A form profile within the existing validator

Every stored schema is a draft-2020-12 object envelope with exactly one property
named `value`, `required: [value]` and `additionalProperties: false`. The field's
shape lives under `properties.value`; instances use the same envelope. Both
schema admission and instance validation call `IJsonSchemaValidator`; a bare
scalar or a prevalidated client assertion is never a substitute.

The additional field profile admits:

- String, integer, finite number and Boolean controls; string enums use a select.
- Objects with explicit named properties, closed additional properties and
  explicit required children; arrays have one homogeneous item schema.
- At most two container levels below `value`, at most ten child properties per
  object, and explicit `maxItems` of at most ten. Object and enum labels are
  localized metadata, rendered as literal text.
- Bounded string length and numeric/array bounds. No references, composition,
  conditionals, regex, executable renderer, HTML, upload, external lookup or
  unsupported annotation is admitted by this profile. Dates are plain strings
  unless the selected profile explicitly supplies a validated date control;
  this proposal selects no date-format assertion.

The field profile is an allowlist in addition to all existing ADR-0043 gates.
Unknown form-profile keywords refuse even where the general schema dialect
would ignore them. The editor supplies type-specific controls, localized labels,
validation hints and a preview; it does not present a general visual builder.
Nested forms use grouped labels, predictable add/remove controls and error
summaries. Unsupported shapes refuse at definition admission, never after a
member has started filling in the form.

`pii_category` is an explicit, required choice from None, PII-Identity,
PII-Behaviour and PII-Sensitive. No default or name-based inference supplies it;
Payment and unrecognized values refuse. One category covers the complete field
value, including every nested child. Sensitive definitions also require an
immutable purpose key and localized notice. A successor is needed to change
that purpose/notice or classification.

Missing and explicit null are different. Omission leaves an existing slot
unchanged; an explicit remove clears it. Null is rejected as a field value; an
optional empty field is absent. Defaults are not admitted or materialized.
No value is silently inserted because a schema contains a default annotation.

Requiredness applies to explicit custom-field form submission. Publication
neither invalidates existing/new Memberships nor changes invitation, seed,
sign-in or ordinary name/locale edits. The form shows missing required fields.
Each set/update submission leaves a complete Active-required-field answer in the
server's effective result; unchanged current pins count, and a historical pin
does not satisfy a different required Active revision. Explicit removal-only
requests can leave an incomplete form; they collect no new value and cannot be
blocked by another unanswered field. Required sensitive fields do not make
personal consent compulsory: declining consent leaves the custom-field form
incomplete and leaves the person's existing account/membership access intact.

### Finite authoring and storage budgets

These are application limits, not measured latency or plan-entitlement claims:

| Subject | Selected bound |
|---|---|
| Active definitions for Membership in one tenant | 20 |
| Stored slots in one MembershipProfile, including retired pins | 20 |
| One envelope schema | 4 KiB of UTF-8 |
| One localized label | 128 UTF-8 bytes; enabled locales only |
| One sensitive-purpose notice per locale | 512 UTF-8 bytes |
| One complete definition authoring input, before form encoding | 4 KiB of UTF-8 |
| One custom-value submission, including pins and envelope | 4 KiB of UTF-8 |
| Complete stored custom-value collection, including pins | 32 KiB of UTF-8 |
| Actual native encoded request | Existing 16 KiB; reject before dispatch |
| Definition/history list page | 20 rows; opaque cursor, no search |

All stages check their actual serialized byte limit, including localization,
JSON escaping and form encoding. A byte expansion is a bounded actionable
validation failure, not truncation. The API and direct application writers
enforce the same logical limits; the larger general customization-row ceiling
does not grant a larger custom-field input. History remains paged rather than
loading every retained revision. A full slot set needs an explicit removal
before adding another field; retirement alone releases no stored-value slot.

Admission also proves usability within these bounds. Every scalar/container has
an explicit finite value bound. A conservative canonical-serialization upper
bound includes JSON escaping, field keys, complete operation/pin metadata and
native encoding overhead. An individual field whose maximum answer cannot fit
one request refuses. Publication computes the same bound for the complete
resulting Active-required set, including an initially empty Profile; it refuses
an impossible set before replacing the incumbent. Unknown/unprovable bounds
refuse. Optional fields can be saved separately after required answers exist;
the existing effective-result validation preserves omitted slots. Tests include
one 4,096-character required string, twenty 256-character required strings,
multibyte/escaped text and exact passing/failing boundaries. No extra timer,
larger transport or incomplete staged-save authority is inferred.

### Personal consent without cross-module writes

Tenancy owns `MembershipFieldConsent`: append-only records for one exact
tenant/nullable organization, Membership, subject User, definition ID/version
and immutable purpose/notice pin. Capture and withdrawal append a new state with
a monotonically increasing sequence for that exact tuple. Neither timestamps
nor UUID ordering choose the current state. Subject/actor references and audit
attribution remain personal data. There is no provider token, email, profile
value or freeform notice copy in the consent record.

Two exact self-consent commands receive a new closed own-consent admission
policy. They require the authenticated actor's own current Active Membership,
admitted tenant/org lifecycle, the actual current host/session and explicit
confirmation. They require no admin or profile-write grant, grant no general own
mutation power and cannot be dispatched under OwnMembershipRead. Identity's
Application.Contracts supplies exact owned-Membership eligibility; Customization
supplies the exact purpose/notice revision. Another person's ID or an admin
checkbox cannot authorize capture. A new grant for a Deprecated revision requires
a retained exact value pin on that person's Membership. Reading or withdrawing
an existing exact consent tuple requires no retained value or Active definition:
a successful grant followed by a failed save, value removal, retirement or
Draft discard cannot strand a person's consent. The ordinary admission ceiling
still requires their own current Active Membership; access after membership
loss remains within P03-10's separately authorized subject-request contract.
Drafts admit no grant. New grants otherwise require an Active definition.

Capture/withdrawal has an expected latest-consent token; a stale confirmation
cannot overwrite a newer withdrawal. Each command writes only its Tenancy root
and its MUST audit on one transaction. The app principal gets ordinary RLS-bound
SELECT/INSERT, with UPDATE/DELETE/TRUNCATE denied and immutable-record backstops.
No consent operation also mutates Membership. Successful consent followed by a
failed value save is reported as two distinct acts; no atomic cross-module
promise or hidden retry is made.

Consent changes take the existing exclusive Tenant fence. Custom-value writes
take its shared mode, then the Membership lock, freshly revalidate live
admission, exact definitions and current consent through owning Contracts, and
retain the fence through commit. Definition writers also take the exclusive
Tenant fence before concept/root locks, serializing Active-count admission and
publication. These are named new consumers; existing User-before-Tenant order,
strongest-plan/no-upgrade rule and existing Identity transition modes remain.
Profile-only writes add no User lock. No new DB role, definer, connection lane,
tenant setter or context writer is introduced.

A sensitive value requires the exact latest Granted evidence; successor pins
require their own consent. Withdrawal prevents later writes using the old
evidence and hides retained sensitive values from ordinary self/admin DTOs and
forms. Reads recheck current consent at their authorized disclosure observation;
already disclosed bytes cannot be recalled. Withheld state is visible without
the value. Reconsent is an explicit personal act, never an automatic effect of
opening a form. Withdrawal is not erasure: values and immutable evidence remain
discoverable to the separately authorized P03-10 fulfillment inventory. P03-10
owns erasure/export authority and exact retained linkage; Phase 11 owns later
purge/backup operations. Neither is claimed implemented by this proposal.

The stored consent ID records the original write's authority, not a mutable
"current consent" flag. Ordinary disclosure additionally checks the latest state
of that same tuple. Explicit reconsent can restore disclosure of a retained value;
the confirmation says so. It does not rewrite the historical evidence ID or
authorize a different revision or purpose.

### Authorization, audit and protected transport

Registry v3 adds exactly Tenant `customization.custom_field.admin`. Only explicit
audited v3 adoption adds that pair to Tenant Admin; warm seed, registration,
startup and migration grant nothing. v1/v2 tenants keep existing grants. No
Organization definition-admin pair or automatic Editor grant is introduced.
Existing Tenant/Organization `identity.membership_profile.write` holders gain
custom-value writes only under the existing self/admin target predicates; no
general directory, cross-org mutation or provider/global profile power follows.

Definition register, draft edit, publish, retire and discard are MUST. Publication
designates its successor when two instances of the same root are tracked. Value
writes remain Membership-root MUST; consent capture/withdrawal are Tenancy-root
MUST. Other-person custom-value reads are MUST sensitive reads before disclosure;
own reads and nonsensitive definition configuration reads use explicit Off
catalogue entries with their independent authorization. Refusals preserve the
existing audit ownership and 403/404-on-audit-fault rules.

New endpoints, native operations and SDK parsers form an exact method/path/type
census in the accepted package before activation. Own consent is POST-only;
definition and value mutations use protected actor/scope-bound idempotency and
strong If-Match where the resource already exists. Consent convergence binds
the exact expected predecessor and resulting record. Receipts contain versions
and immutable targets, not raw values or consent notices. Replay reauthorizes
the original target and current disclosure eligibility. Conflict requires a
fresh explicit read; a stale form never overwrites or retries automatically.

`canManageCustomFields` is the tenth advisory administration capability. Its
predicate is current Tenant definition-admin authority and applicable orgless
write context; exact parsers, operation manifests and stale-session controls
change together. Per-member controls still use actual target eligibility. New
screens share existing entry/scope navigation; a learner can manage personal
consent outside Studio without receiving administration authority.

### Diagnostics and retained copies

All custom values, including None, are excluded as whole values from diagnostic
events, validation messages, audit snapshots/diffs, traces, error archives and
Sentry payloads. Classification still governs storage/disclosure/fulfillment;
it is not permission to log a tenant's arbitrary value. Definition schema,
labels and notices receive the same payload exclusion. Fixed error codes and
bounded field locations replace attempted values and provider exception text.
This conservatively extends the category-driven baseline without claiming
general secrecy for unrelated application exceptions.

Mapped-property PII census declarations, contained-child capture, protected
receipts and local/native response custody are extended with their first
writers. Real nested canaries and nonempty safe controls exercise immediate
and deferred database failures, validation, serialized logs, spans, local error
archives, Sentry envelopes, API responses and audit capture. Consent linkage is
classified; no profile value is copied into consent audit or an integration
event. No new provider, background job or durable-value cache is selected.

## Consequences

### Positive

- Existing data keeps its original interpretation and personal-data category.
- Admins can define and preview usable fields; members get localized forms and
  personal consent controls with explicit failure and recovery states.
- The packet reuses isolation, schema admission, module contracts, ownership,
  audit and native transport rather than adding a general JSON write surface.

### Negative

- New Customization and Tenancy tables, an Identity JSONB column, authorization
  policies, a registry adoption and more privacy tests increase packet scope.
- Twenty slots include historical pins. Finite input and form-profile limits
  can require simplifying a field; they are deliberate MVP usability bounds.
- Publication/consent changes hold an exclusive Tenant fence. Actual latency is
  qualified by tests, with no production fairness or p95 claim.
- Withdrawal hides but does not erase retained values. Later authorized DSAR
  and operational retention work must account for them and their linkage.
- Requiredness does not make custom fields an account-creation prerequisite.
  This choice preserves delivered invitation/sign-in flows and personal consent.

### Neutral

- P03-3/P03-4 evidence follow-ups remain separately open before phase exit.
- No existing application deadline, CI ceiling, worker count or qualification
  rule changes. A passing test subset does not establish final-source A/B.
- Phase 06 retains the general visual builder; later target activation and
  DSAR/provider work remain with their existing named packets.

## Implementation Notes

After acceptance, the four-step
[package](../roadmap/phase-03-delivery-log.md#p03-5-proposed-custom-field-contract-and-plan-2026-11-10)
delivers foundation/consent, Membership values/privacy, native/UI and complete
qualification. Each implementation commit has two fresh independent review
rounds; verified corrections are committed before the next step. The package
owns exact route/request census, SQL constraints/grants, module APIs and tests.

Reconcile Customization, Identity and Tenancy specifications, permission/audit
matrices and diagrams; Architecture 13/23/32; Standards 04/05/07/08/09/11/15/16/
18/19/21; glossary; decision index; Phase 03 G9/Status and append-only delivery
records. Amend each named predecessor with a dated link and the exact bounded
extension, using the next available amendment number at acceptance. Do not
rewrite prior review verdicts or historical Accepted decisions.

## Architecture Tests

- Activate `Every_TenantCustomFieldDef_Declares_PiiCategory` only with its actual
  aggregate/migration and missing/invalid/Payment counterexamples.
- Extend `Mapped_Properties_Have_Explicit_Personal_Data_Classification`,
  `User_Aggregate_Has_No_TenantScoped_Columns`, module/aggregate ownership,
  RLS/WithCheck, native operation/policy, permission/audit and diagnostics rules.
  No new multi-root exception, context setter or role bypass is inferred.
- Real app-role PostgreSQL controls cover concurrent publication, requiredness,
  retained pins, classification change, removal/slot reuse, consent sequence,
  stale confirmation, withdrawal versus value commit, audit-fault rollback,
  cross-tenant/org/person access and direct mutation refusal.
- Real native/SDK controls cover supported nested forms, body/payload limits,
  Origin/CSRF, stale ETag/idempotent replay, revoked authority, late old-session
  publication, outages, localized errors and learner-only consent.
- Keyboard, focus, screen-reader, reflow and contrast checks apply to the new
  screens. Automated checks and maintainer-reported manual results remain
  distinct; neither unperformed checks nor a subset is marked Passed.

## References

- [ADR-0043](0043-customization-payload-validation.md),
  [ADR-0073](0073-scoped-membership-and-identity-data-foundation.md),
  [ADR-0074](0074-membership-admission-under-ordered-locks.md),
  [ADR-0076](0076-live-scoped-authorization.md),
  [ADR-0077](0077-trusted-authorization-evidence.md),
  [ADR-0078](0078-native-session-scope-and-self-entry.md),
  [ADR-0079](0079-bounded-identity-administration.md).
- [Customization model](../architecture/32-tenant-customization-model.md),
  [Data Protection](../architecture/23-data-protection.md),
  [Phase 03](../roadmap/phase-03-identity-admin.md).
- [Customization specification](../modules/customization/README.md),
  [Identity specification](../modules/identity/README.md),
  [Tenancy specification](../modules/tenancy/README.md).
- [Database](../standards/05-database.md),
  [frontend](../standards/07-frontend-architecture.md),
  [permissions](../standards/19-permissions.md),
  [audit](../standards/18-audit-coverage.md) and
  [test catalogue](../standards/21-architecture-tests-catalogue.md).
