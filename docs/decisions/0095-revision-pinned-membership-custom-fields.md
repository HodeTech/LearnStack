# ADR-0095: Revision-Pinned Membership Custom Fields

## Status

Accepted

**Date:** 2026-11-10
**Deciders:** @cemil

Accepted after the maintainer's conditional approval and verified review
corrections on 2026-11-10. This decision answers P03-5/G9; acceptance establishes
the contract, not implementation or qualification. The
[decision package](../roadmap/phase-03-delivery-log.md#p03-5-proposed-custom-field-contract-and-plan-2026-11-10)
owns the four implementation steps and their proof obligations.

Acceptance includes dated Amendments in ADR-0043, ADR-0073, ADR-0074, ADR-0076,
ADR-0078 and ADR-0079 link the exact extensions below. Their historical bodies
remain intact and the records remain Accepted outside these bounded changes:

- ADR-0043's object-schema admission remains unchanged. A closed field envelope
  and a narrower form profile become its first Membership consumer.
- ADR-0073's profile acquires revision-pinned values; no global User column,
  cross-module foreign key or additional multi-root write exception is admitted.
- Standard 05 owns the fence protocol and gains named Customization definition
  and Tenancy consent consumers under this ADR. ADR-0074 receives navigation
  only; its Membership-admission timing and Identity mode derivation are unchanged.
- ADR-0076/0078 gain exact custom-field operations and two self-consent writes.
  The existing own-Membership **read** policy grants no mutation authority.
- ADR-0079 gains one assignable Tenant pair through explicit registry v3 adoption,
  one advisory capability and the closed protected receipt/transport consumers.
  Existing profile-write holders gain the bounded custom-value operation.

The consent-only recovery entry below also requires dated Amendments in
ADR-0003, ADR-0036, ADR-0040 and ADR-0072 for the exact new function/origin/setter
censuses. ADR-0037's generic replay and ADR-0044/0077's central audit/designation/
denial mechanisms apply unchanged; their new consumers are reconciled in the
canonical matrices/catalogues, not presented as changes to those decisions.

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
added. Exact revision reads are uncached and tenant-filtered. For these new
writers the final Active read follows the shared Tenant fence, retained through
commit; definition retirement/publication takes its exclusive counterpart. If
the value writer wins, retirement waits until that commit; if retirement wins,
the post-wait read refuses a new pin to that revision. Existing historical pins
survive later retirement. This stronger custom-field boundary does not change
the older content-type/taxonomy reader's Active-at-read contract.

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
  unsupported annotation is admitted by this profile. Date-like input uses a
  bounded plain text field; date-format assertions/controls are not admitted.

Numeric schemas and values share the browser's representation contract at every
boundary. Integers stay within -9,007,199,254,740,991 through
9,007,199,254,740,991. Numbers use finite IEEE-754 binary64 semantics, not exact
decimal arithmetic; integral numbers outside that safe-integer range also
refuse. Admission normalizes numeric bounds and values once to the supported
representation before schema validation and storage. Overflow, nonfinite values,
underflow of a nonzero input to zero, and integer precision loss refuse rather
than silently changing the answer. Admitted numbers must round-trip through the
actual API/SDK/native/form serializers without further value change. Direct API
and nested inputs have the same guard; no client-only safe-range claim suffices.
Tests include both safe-integer edges, each unsafe neighbor, exponent/precision
controls and a direct API-to-Portal-to-write round trip. This profile provides
no payment, arbitrary-precision integer or exact-decimal field type.

The field profile is an allowlist in addition to all existing ADR-0043 gates.
Its closed keyword set is:

| Schema position | Permitted keywords and constraints |
|---|---|
| Envelope | `$schema`, `type: object`, `properties` containing only `value`, `required: [value]`, `additionalProperties: false` |
| String | `type: string`, explicit `minLength`/`maxLength` within 1–256, optional nonempty unique string `enum` of at most twenty valid choices |
| Integer / number | `type`, explicit inclusive `minimum`/`maximum` in the supported numeric range |
| Boolean | `type: boolean` |
| Object | `type: object`, nonempty `properties` of at most ten children, `required` listing only unique declared children, `additionalProperties: false` |
| Array | `type: array`, one `items` schema, explicit `minItems`/`maxItems`, with `0 <= minItems <= maxItems <= 10` and `maxItems >= 1` |

All other keywords refuse, including every `format`, `const`, `default`,
`title`, `description`, `examples`, `$comment`, `readOnly` and `deprecated`.
Localized form metadata lives in the typed definition, not schema annotations.
Container depth counts object/array value nodes, starting at one when `value`
itself is a container: object→array→string is two; object→array→object is three
and refuses. ADR-0043's independent raw JSON depth ceiling still applies.
Field/property keys use bounded ASCII customization-key grammar and reject the
reserved names `constructor`, `prototype` and `__proto__`; parsed maps never use
prototype-inheriting assignment of arbitrary keys.

The additional admission proves nonemptiness for every node, including optional
children. It constructs a finite witness from intersecting length/range bounds,
valid enum choices and recursively satisfiable required children/items, then
validates the complete witness through the same admitted-schema validator. Empty
intersections, invalid/empty enum choices, contradictory nested requirements or
an unprovable witness refuse before persistence/publication. The proof uses only
this finite grammar, not a general JSON Schema satisfiability solver. Publication
rechecks nonemptiness and the combined-required-set budget together. Contradictory
string/numeric/enum/nested schemas have positive and refusal controls.

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

Here "sensitive" means exactly `pii_category = PII-Sensitive`. Identity and
Behaviour values retain their normal authorization/privacy protections but gain
no invented consent requirement. None is an explicit non-PII declaration, never
inferred or defaulted; its values still receive conservative diagnostic redaction.
None does not automatically exclude a subject-linked value from P03-10 fulfillment.

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
For a sensitive field, an unchanged pin counts only while the latest consent for
that exact tuple is Granted. Withdrawal makes its retained pin incomplete as
well as withheld. Mixed set/remove requests use the set/update rule; reconsent
can restore eligibility without changing the historical write evidence.

### Per-operation budgets and retained growth

These are application limits, not measured latency or plan-entitlement claims:

| Subject | Selected bound |
|---|---|
| Active definitions for Membership in one tenant | 20 |
| Retained concept keys / revisions per tenant, including discarded drafts | 100 / 1,000 |
| Live Draft revisions per tenant | 20 |
| Stored slots in one MembershipProfile, including retired pins | 20 |
| One envelope schema | 2 KiB of UTF-8, included in the complete-input limit |
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

These independent ceilings reserve no sum: schema, labels, notices and metadata
together must fit 4 KiB. The editor shows remaining authoring/value capacity and
the exact blocking limit. Authored locales are an enabled-locale subset containing
the default locale; display fallback carries its actual authored locale. Eight
512-byte notices cannot all fit one definition input and correctly refuse. The
4,096-character and twenty-256-character examples below are refusal controls.
Optional values share the 32 KiB stored budget, including historical pins; the
form shows usage and explicit removal/replacement, never truncates or hides pins.
Pure removal remains usable even when another required answer or capacity blocks
collection. Tests isolate encoded/aggregate/stored limits with exact passing
boundaries, multibyte values and preserved historical slots.

Retained definition limits are checked under the exclusive Tenant fence.
Discard releases a live-Draft slot only, not a reserved concept/revision. At the
history limit, editing/publishing an existing Draft, retirement, removal and
consent withdrawal remain available; new revisions refuse with honest capacity
copy. No purge or automatic key reuse is inferred. P03-9 owns any later admitted
operator capacity override; this packet supplies no pretend recovery command.

Consent/audit history has no lifetime row cap or automatic compaction in this
packet. It is retained personal evidence, not a finite total-storage claim.
Repeated same-state requests return their existing result and append no record;
new grants/reconsents require sixty seconds since the previous grant for that
tuple, enforced from durable evidence under the fence across replicas. Withdrawal
has no such cooldown and does not reserve grant capacity. Existing visitor,
verified-subject (600/minute/instance), concurrency and request deadlines apply;
they grant no deployment-wide throughput or lifetime storage bound. Indexed
sequence/history pages and per-tenant definition usage expose bounded working
sets; the operator runbook includes scoped retained-row/index-size inspection.
Phase 11 owns operational retention/monitoring/backup purge, while P03-10 owns
subject fulfillment; neither may drop current consent or unresolved replay facts.

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

`MembershipFieldConsentId` is a strongly typed UUID; the root is an
`AuditableEntity<MembershipFieldConsentId>` with no update/delete domain method.
Its `[TenantOwned]`/`[OrganizationScoped]` mapping, EF filter and canonical FORCE
RLS/WithCheck retain all ordinary isolation layers. Tenant, subject and nullable
organization are derived from Identity's exact owning Membership contract at
grant; NULL organization is valid only for a NULL-org Membership. Body fields
cannot select actor, tenant or organization. IDs into Identity/Customization are
values, never cross-module FK/navigation/join. Each record is the consent root's
own MUST audit subject, not the Tenant aggregate.

Purpose keys are explicit lowercase ASCII slugs, 1–64 characters, namespaced by
the tenant/target/concept. The exact definition revision disambiguates their
meaning; equal purpose text/keys never transfer consent across fields/revisions.
Evidence pins the displayed authored `notice_locale`, exact definition/notice
revision and domain-separated SHA-256 of that locale's immutable UTF-8 notice.
Locale is evidence, not another consent-state partition. Confirmation binds this
exact notice; a stale/disabled or substituted notice cannot grant consent. A
withdrawal can show its historically pinned notice even if that locale is later
disabled, with the actual `lang`; it cannot become inaccessible for that reason.

`membership_profiles.custom_fields` has static PersonalDataCategory.Sensitive
plus whole-property `[PiiSensitive]`, separately from per-revision classification.
Consent subject/actor IDs are Identity; Membership/definition/purpose/notice
linkage is Sensitive. Record state/sequence/timestamps are Audit, with remaining
mapped technical fields explicitly declared under the existing census rules.
Snapshot redaction preserves each marked property/diff with
`SensitiveTokenCatalog.RedactedValue`; no new redaction literal or dropped-property
convention is introduced. This records that a value changed without its contents.

Two exact self-consent commands receive closed, distinct admission rules.
Grant requires the authenticated actor's own current Active Membership,
admitted tenant/org lifecycle, the actual current host/session and explicit
confirmation. They require no admin or profile-write grant, grant no general own
mutation power and cannot be dispatched under OwnMembershipRead. Identity's
Application.Contracts supplies exact owned-Membership eligibility; Customization
supplies the exact purpose/notice revision. Another person's ID or an admin
checkbox cannot authorize capture. A new grant for a Deprecated revision requires
a retained exact value pin on that person's Membership. Reading or withdrawing
an existing exact consent tuple requires no retained value or Active definition:
a successful grant followed by a failed save, value removal, retirement or
Draft discard cannot strand a person's consent. Withdrawal/history use the
separate subject entry below and remain usable after Membership suspension or
revocation, Tenant suspension and Organization archive. No administrative grant
or current Active Membership is required for those two purposes.
Drafts admit no grant. New grants otherwise require an Active definition.

A bounded own-consent history lists the latest state for each of the current
Membership's exact consent tuples, including consent-only tuples after a failed
value save or definition retirement. It has cursor pages of twenty, no search
or foreign-Membership selection. A bounded owning-contract batch resolves the
retained notice revisions; no per-row directory lookup or N+1 query is inferred.
Portal profile exposes that history and withdrawal controls independently of
Active fields and stored values. Fresh navigation cannot lose the only way to
discover an existing grant.

Capture/withdrawal supplies `{sequence, evidenceId}` from the last own-state
projection; zero/null means no predecessor. Sequence is a nonnegative safe
integer; all other null/ID combinations refuse. A stale confirmation cannot
overwrite a newer withdrawal. Protected idempotency binds that predecessor,
actor, tuple and resulting record: an exact same-key replay returns the original
version receipt after fresh own-subject authorization, never performs another
transition, and is not a statement that the old state remains current. A new
key with stale predecessor conflicts; same-state requests append no record.
Each command writes only its Tenancy root
and its MUST audit on one transaction. The app principal gets ordinary RLS-bound
SELECT/INSERT, with UPDATE/DELETE/TRUNCATE denied and immutable-record backstops.
No consent operation also mutates Membership. Successful consent followed by a
failed value save is reported as two distinct acts; no atomic cross-module
promise or hidden retry is made.

### Consent-only entry after lifecycle loss

A separate `OwnConsentEntry` policy and `HostAndConsentSubject` origin admit
only host-capped own retained-scope discovery, exact history and withdrawal.
They require a verified provider subject, quota admission, existing Active
global binding and an independently classified still-live Tenant/Organization
host. They do not require an Active Membership, tenant administration lifecycle,
AccountSite or a successful public-site snapshot. Disabled/released/unknown
hosts remain unavailable; global Closing/Closed identity and terminated-host
recovery remain P03-9/10 subjects, not a global directory exemption.

One migration-owned Tenancy function, `fn_tenancy_own_field_consent_scope`, reads
only retained consent linkage for the verified actor and independently resolved
host ceiling. Its actor is taken from completed binding normalization, never a
request field. Closed list/exact-selection modes return at most twenty distinct
owned Membership/tenant/org scope keys, no values, grants, profile or provider
data. An Organization host cannot discover another branch. The function has a
fixed search path, exact app EXECUTE/PUBLIC revoke, owner/RLS exception and
body/caller census; it contains no cross-module join or extra login role.

`ConsentSubjectScope` consumes a single-use immutable operation/actor/subject/
real-host/scope/mode witness on the actual owner. History uses physical ReadOnly;
withdrawal uses ordinary ReadWrite. This is the twelfth logical tenant setter,
implemented by the same four physical context writers, and the function is the
twelfth confined definer. It supplies no Membership, native ordinary session,
tenant-wide `app.scope`, custom-value disclosure or generic scope override.
Withdrawal derives its exact tuple/org from retained consent, so revocation or
deletion of the Membership cannot turn it into a NULL-org or foreign-subject row.

> **Erratum — 2026-11-10.** The following paragraph incorrectly claims an existing
> state-only function can recheck global admission after the Tenant fence.
> `ContextualIdentityStorageDdl` makes the admin-state function trigger-only and
> requires an Active tenant-wide Tenant Admin; `GlobalIdentityBindingService`
> refuses inherited tenant ownership. Under ADR-0076's committed-read boundary,
> the completed pre-entry Active binding lookup is the global admission check.
> Post-fence withdrawal rechecks only its retained tuple/subject/org, operation
> witness, predecessor and audit. Already-admitted withdrawal may finish after
> concurrent closure as a revocation fact; it cannot grant consent, disclose
> values or resurrect a User/Membership. P03-10 must settle or reconcile these
> owners/unknown commits before claiming fulfillment. No extra global reader,
> function or owner is introduced. The Decision is unchanged; Amendment 1 records
> the correction and canonical carriers.

After taking the Tenant fence, the owning Identity contract freshly rechecks
the existing binding's global admission state before withdrawal. It uses the
existing state-only eligibility function; the Tenancy function gains no Identity
table read. This follows the global closure's User-before-Tenant fence rather
than treating an earlier Active observation as irrevocable admission.

Native `/account/consents` uses an existing bounded credentialed entry handle
and a fixed consent-only HTML shell, reachable after fresh sign-in with no
Membership. The enumerated consent entry POSTs use Origin/CSRF and the existing
entry owner/lifetime/capacity; they do not call CurrentSession or read branding/
public configuration. A missing/expired handle restarts sign-in, never bootstrap
or Membership creation. Ordinary active Portal can link to this same surface.
Real controls cover Suspended/Revoked Memberships, Suspended tenant, Archived
organization, removed values, failed saves, wrong subject/host/org and late
old-entry publication. No new registry or capacity allowance is inferred.

Consent changes take the existing exclusive Tenant fence. Custom-value writes
take its shared mode, then the Membership lock, freshly revalidate live
admission, exact definitions and current consent through owning Contracts, and
retain the fence through commit. Definition writers also take the exclusive
Tenant fence before concept/root locks, serializing Active-count admission and
publication. These are named new consumers; existing User-before-Tenant order,
strongest-plan/no-upgrade rule and existing Identity transition modes remain.
Profile-only writes add no User lock. Definition/value/ordinary consent writers
add no bypass or physical connector. Only the separately enumerated consent-only
entry adds the one definer/logical setter above; existing roles, the four physical
writers and three global-self descriptors are unchanged.

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

| Registry | Exact assignable set | Tenant Admin adoption |
|---|---|---|
| v1 | Historical five Identity scoped pairs | Historical finite v1 manifest |
| v2 | v1 plus the historical three Tenant Tenancy pairs | Existing explicit v1→v2 delta; unchanged fingerprint/transition |
| v3 | v2 plus Tenant `customization.custom_field.admin` | Only explicit v2→v3; adds exactly that pair |

A v1 role must first deliberately adopt v2; a direct v1→v3 jump refuses. Only
the exact orgless built-in Tenant Admin can adopt; source marker/fingerprint,
requested release/fingerprint, ETag and current role-admin authority must match.
The v3 fingerprint uses the existing length-framed SHA-256 algorithm over domain
`learnstack.identity.role.release.v3`, the exact v2 fingerprint, `TenantAdmin:Tenant`
and the ordered v3 delta `Tenant:customization.custom_field.admin:True`.
The marker advances atomically from exact v2 to exact v3 with the delta and MUST
audit. Same-release replay follows the existing outcome contract; downgrade,
unknown/mismatched release, custom/org role or stale source refuses.
Previously removed nonkernel v1/v2 grants stay removed; v3 is not a manifest
restore. Both exact transitions remain discoverable in the registry UI/DTO.
A forward migration installs v3 checks/guards without changing shipped v2 helper
behavior or fingerprints on a fresh migration chain. Warm seed/completion accepts
valid v3 state without restoring grants. Definition registration grants nothing.

Definition register, draft edit, publish, retire and discard are MUST. Lifecycle
is Draft→Active→Deprecated; retirement means Active→Deprecated, not a fourth
status. Publication retires its incumbent, activates the successor and calls
`IAuditSubject.Designate(successor)` when both instances are tracked. Value
writes remain Membership-root MUST; consent capture/withdrawal are Tenancy-root
MUST. Other-person custom-value reads are MUST sensitive reads before disclosure;
own reads and definition configuration reads use explicit Off
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

The pair is defined once in SharedKernel's bounded permission definitions and
registered by Customization's module policy source. Identity's advisory
projection evaluates that registered pair against current facts/catalogue and
orgless context, without importing Customization.Domain or treating registration
as a grant. DTO/OpenAPI/SDK/native parsers, labels, manifests and exact capability
tests change in the same activation commit. Both API and application admission
remain authoritative after a stale or revoked UI flag.

### Derived replay fingerprints

An unkeyed body fingerprint is derived personal data, even when no raw value is
stored. Low-entropy answers can be guessed if the remaining request/context and
digest are known; applying SHA-256 twice does not remove that risk. P03-5's new
body-bearing protected operations therefore use HMAC-SHA256 with a versioned,
domain-separated, length-framed actor/scope/operation/precondition/method/path/
query/body input. Historical generic/P03-3/P03-4 digests stay byte-identical; no
new P03-5 operation falls back to them. No intermediate unkeyed digest is stored
or logged. Early replay admission and the filter use one implementation.

The existing ISecretProvider supplies a deployment-shared independent random
32-byte key ring: one current key and at most three retained keys, with immutable
nonsecret ASCII key IDs. The fingerprint text carries version/key ID/HMAC only.
Configuration names are `Idempotency:CustomFields:CurrentKeyId` and the closed
`Idempotency:CustomFields:Keys:<id>` ring; secrets are base64 of exactly 32 bytes.
All IDs are distinct ASCII `[a-z0-9-]`, 1–32 characters, and current must exist.
No committed example contains a usable key. Only new P03-5 mutation admission
depends on this configuration; unrelated/public existing routes retain behavior.
A narrow tenant-bound store metadata read selects the incumbent key ID before
recomputation, including logically expired rows; it exposes no body or receipt.
Unknown/missing/malformed key material fails closed before claim/disclosure.
Existing 1,000 physical tenant slots and logical expiry/recycling rules remain;
24-hour expiry does not mean physical deletion or permission to drop a key.

Rotation first distributes the new key with old read keys to every replica,
then switches the shared current ID. A retired runtime key is removed only after
no physically retained row needs it; snapshot restore includes its matching key
ring. IDs/material are never silently reused/regenerated. Loss requires restore
of the same key; compromise requires operator-led write quiescence, resolution
of in-flight outcomes and an explicit decision on invalidating historical replay
before replacement. The runbook never deletes unresolved rows merely to rotate.
Historical backup keys remain in operator custody through backup retention.

Keyed fingerprints still carry Sensitive personal linkage; app/platform access,
WAL/backups, recycling and P03-10 inventory include them. HMAC reduces dictionary
exposure without anonymizing linkage or defending a compromised live key. Tests
cover candidate guessing without the key, both call sites, existing digest
compatibility, changed body/precondition, replicas/restarts/rotation, unknown keys,
expired retained rows and absence of raw/unkeyed copies in persisted evidence.

### Diagnostics and retained copies

All custom values, including None, are excluded as whole values from diagnostic
events, validation messages, audit snapshots/diffs, traces, error archives and
Sentry payloads. Classification still governs storage/disclosure/fulfillment;
it is not permission to log a tenant's arbitrary value. Definition schema,
labels and notices receive the same payload exclusion. Fixed error codes and
fixed structural locations/indices replace attempted values and provider
exception text; errors do not echo tenant-authored keys, labels or notices.
This conservatively extends the category-driven baseline without claiming
general secrecy for unrelated application exceptions.

Mapped-property PII census declarations, contained-child capture, protected
receipts and local/native response custody are extended with their first
writers. Real nested canaries and nonempty safe controls exercise immediate
and deferred database failures, validation, serialized logs, spans, local error
archives, Sentry envelopes, validation/L1 error responses and audit capture.
Authorized successful value disclosure retains its separate positive tests.
Consent linkage is
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
- The consent-only entry adds one confined definer and logical scope setter;
  keyed fingerprints need an independently retained deployment key ring.
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
matrices and diagrams; Architecture 13/23/32; Standards 04/05/07/08/09/10/11/15/16/
18/19/21; glossary; decision index; Phase 03 G9/Status and append-only delivery
records. Amend each named predecessor with a dated link and the exact bounded
extension, using the next available amendment number at acceptance. Do not
rewrite prior review verdicts or historical Accepted decisions.

## Architecture Tests

- Activate `Every_TenantCustomFieldDef_Declares_PiiCategory` only with its actual
  aggregate/migration, explicit None positive control and missing/invalid/Payment
  counterexamples. None is no automatic subject-export/erasure exclusion.
- Extend `Mapped_Properties_Have_Explicit_Personal_Data_Classification`,
  `User_Aggregate_Has_No_TenantScoped_Columns`, module/aggregate ownership,
  RLS/WithCheck, native operation/policy, permission/audit and diagnostics rules.
  No new multi-root exception or role bypass is inferred. The one enumerated
  consent-entry function/setter and its owner/RLS/caller census are explicit.
- Real app-role PostgreSQL controls cover concurrent publication, requiredness,
  retained pins, classification change, removal/slot reuse, consent sequence,
  stale confirmation, withdrawal versus value commit, audit-fault rollback,
  cross-tenant/org/person access and direct mutation refusal.
- Both definition/value fence winner orders, finite satisfiability witnesses,
  inactive-membership consent-only entry and keyed replay compatibility have
  real subjects/dirty controls, not only source literal assertions.
- Real native/SDK controls cover supported nested forms, body/payload limits,
  Origin/CSRF, stale ETag/idempotent replay, revoked authority, late old-session
  publication, outages, localized errors and learner-only consent.
- Keyboard, focus, screen-reader, reflow and contrast checks apply to the new
  screens. Automated checks and maintainer-reported manual results remain
  distinct; neither unperformed checks nor a subset is marked Passed.

## Amendments

### Amendment 1 — admitted withdrawal and global eligibility boundary (2026-11-10)

The post-fence state-only-function claim was false at acceptance. Inspection of
`ContextualIdentityStorageDdl` shows `pg_trigger_depth() < 1` refusal plus the
Active tenant-wide administrator requirement; `GlobalIdentityBindingService`
rejects inherited tenant/capture ownership. Neither is a reusable ordinary
withdrawal reader. ADR-0076's
[committed reads](0076-live-scoped-authorization.md#committed-reads-and-outcomes)
already keeps global eligibility at the earlier subject-binding observation and
allows admitted work to finish. The inline erratum states that boundary exactly,
including its explicit P03-10 settlement/reconciliation cost. Grant still requires
fresh admitted Membership/lifecycle after its fence; neither check invents a
second global read. This is not merely a renamed function or a through-COMMIT
global eligibility claim.

Carriers corrected: this ADR's inline erratum; Standard 05's custom-field commit
fence and Standard 11's consent-subject entry clauses; the Phase 03 correction
record. The canonical function/setter inventories remain Standard 05/11: twelve
confined definers, twelve logical setters and four physical writers. Historical
records and the Decision remain unchanged; no new global authority follows.

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
