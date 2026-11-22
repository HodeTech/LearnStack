# ADR-0106: Subject Data Requests: Authority, Scope and Status

## Status

Accepted — 2026-11-22, after the maintainer reviewed the drafts, four independent reviews of them and the corrections those reviews led to. The dated record is the [Phase 03 acceptance record](../roadmap/phase-03-delivery-log.md#p03-10-decision-package-and-acceptance-2026-11-22). Acceptance closes gate G15 of
[Phase 03](../roadmap/phase-03-identity-admin.md#the-decision-register) and, with
[ADR-0107](0107-data-request-fulfillment-and-account-closure.md) and
[ADR-0108](0108-privileged-audit-redaction.md), is the P03-10 decision package. It
authorizes the code it describes and is not proof that anything is implemented.

**Date:** 2026-11-22
**Deciders:** @cemil

The maintainer chose, on the questions the package put to them, the human-operator
privileged step, reuse of the existing `identity.membership.admin` key for administrator
requests, and a 24-hour cancellable window
([ADR-0107](0107-data-request-fulfillment-and-account-closure.md) carries the other
choices).

## Decision Drivers

- [Data Protection](../architecture/23-data-protection.md#rights-we-support) separates a
  tenant-scoped request from global closure and says RLS is one boundary, not the
  subject predicate. G15 asks who may request which person's data, in which scope, for
  which recipient and purpose, and what status survives the loss of the Membership.
- No request, status, permission or surface exists. Every existing self-service operation
  carries no assignable key.
- The pinned OpenAPI gate refuses every further assignable permission key (G20,
  [ADR-0100](0100-p038-federation-contract-transition.md)).
- The row policies of `memberships`, `membership_profiles` and role assignments admit a
  write only when the announced organization equals the row's (or both are absent);
  `app.scope='tenant'` widens reads only. A job or act therefore writes the rows of one
  organization, or of none, per transaction, and existing administration already refuses a
  target whose organization differs from the context's.
- A learner needs a request and status path with no Studio rights, including after the
  Membership is gone, and a Suspended person has no native session.
- Targets: an export within 7 days, a deletion within 30 days, failures escalated.

## Considered Options

1. **A request bound to one scope group, self plus administrator-on-behalf under the
   existing membership-administration authority, with a retained own-scope path and no
   delegate** (chosen).
2. **A request spanning every organization of a tenant** (rejected). It needs one
   transaction to write rows under several organization announcements, or a new RLS
   exception, which the policies forbid.
3. **A dedicated assignable `identity.data_request.admin` key** (rejected now). It needs a
   registry release, a role migration, SDK and BFF changes and a G20 transition record,
   and no tenant yet needs the roles separated.
4. **Self-service only, administrators acting through the platform operator** (rejected).
   A tenant could not meet its controller duty without a platform operator.
5. **A delegate or representative authority** (rejected for this packet). It needs a
   proof of the third party that no corpus document specifies.

## Decision

1. **Kinds.** A *data request* is `export`, `erasure` or `closure`. `export` and `erasure`
   are described here; `closure` is global and described in
   [ADR-0107](0107-data-request-fulfillment-and-account-closure.md) § 7, and shares this
   ADR's identity, status vocabulary, idempotency and audit rules. Distinct operation
   names, routes and copy keep erasure and closure apart.
2. **Scope group.** A request is bound to one *scope group*: a tenant and either one
   organization or none (tenant-wide), the group of the context in which it is filed. It
   covers the subject's Membership in that group, of which a person has at most one
   current row. A person with Memberships in several organizations files one request per
   group, and the UI lists them from the person's contexts.
3. **Authority.**

   | Requester | May request | Scope |
   |---|---|---|
   | The person, through a live session | `export`, `erasure` for themselves; `closure` | The group of the session's context (for a closure, the originating group; the closure itself is global) |
   | The person, through the retained own-scope path (§ 9) | The same | Any group where they hold or held a Membership or have a request |
   | A holder of `identity.membership.admin` covering the group | `export` for a person with a Membership of any status in the group; `erasure` only for a Membership that is already Revoked | That group |

   A Tenant-scoped holder covers every group of their tenant, an Organization-scoped
   holder only their organization's group. An administrator erases the data of an Active
   or Suspended member only after revoking that Membership, an existing and separately
   audited act, so no administrator destroys a working member's data in one step; the
   Revoked target needs its own target authorization, because the membership
   administration authorization refuses Revoked targets. An administrator never creates a
   `closure`, never reads a closure request, and a Platform operator creates no tenant
   request. No
   delegate, guardian or representative authority exists; naming it needs its own decision,
   its owning phase is Phase 06 and the trigger is the first tenant that requires it.
4. **Permissions.** Self operations carry no key and use two new policy kinds,
   `OwnDataRequest` (an Active Membership in the announced context) and
   `OwnDataRequestEntry` (the retained own scope, which also serves a Suspended person).
   Administrator operations are `Protected` with the existing alternatives
   `[identity.membership.admin at Tenant, at Organization]`. **No assignable key is added
   and the grant union's bytes do not change, so G20 is not triggered and stays Open.** A
   dedicated key is the next decision when a tenant needs a data-protection role distinct
   from membership administration; the owning phase is Phase 06.
5. **Subject predicate and third parties.** Every participant selects rows by the subject's
   `UserId` and the request's group inside the tenant's `ITenantContext` as
   `learnstack_app`. A row that names another person as a different party is included
   only with that reference replaced by the literal `third_party`, and a field that holds
   another person's data (an invitee's address on an invitation the subject issued) is
   omitted. A same-tenant second person, another organization's rows and tenant-internal
   notes never appear. PII category is never disclosure authority.
6. **Recipient and purpose.** The recipient of an export is always the subject, for data
   subject access (GDPR Art. 15, KVKK Madde 11). An administrator who requests one sees
   its status and counts and never its content; the artifact is retrievable only through
   the subject's own surface ([ADR-0107](0107-data-request-fulfillment-and-account-closure.md)
   § 4).
7. **Eligibility, holds and refusals.** A closed port `IDataRequestEligibility` decides at
   creation and again at execution. Its default implementation checks structure: the
   subject holds a Membership of any status in the group (a Revoked tombstone counts; on
   the retained path, held one or has a request there); no non-terminal request of the same
   kind, subject and group exists, which a partial unique index on the non-terminal rows
   enforces so two concurrent creations cannot both pass (the answer is 409
   `business_rule_violation` with the `lockey_request_already_active` message, shown only to
   a requester the group's scope covers); and an `erasure` is refused when the subject is
   the only eligible tenant-wide administrator. A second port `IDataRequestHoldSource` has the
   default `NoHoldSource`; its owning phase is Phase 09 (payments and disputes) and its
   trigger the first aggregate that can carry a hold. A refusal at creation is a response
   (`blocked_last_administrator`, `blocked_hold`) and writes no request; the status
   `blocked` is reserved for a request that reached execution and failed its re-check. The
   hold check also runs at a closure's Begin. A refusal that leaves a scheduled request in
   place records its closed code in the request's `last_refusal`, which the receipt shows
   so the person can act on it.
8. **Status and transitions.** The closed statuses and their only transitions are:

   | Kind | Path |
   |---|---|
   | `export` | `requested` → `fulfilling` → `completed`; `fulfilling` → `failed`; `requested` → `cancelled` |
   | `erasure` | `scheduled` → `fulfilling` → `awaiting_privileged_step` → `completed`; `scheduled` → `cancelled`; `fulfilling` → `blocked` or `failed` |
   | `closure` | `scheduled` → `fulfilling` (Begin) → `completed` (Finalize); `scheduled` → `cancelled` until Begin commits |

   A closure has no stored `awaiting_privileged_step`: the participant groups are other
   tenants' rows, so Finalize's precondition and the operator inspection read them. A
   fan-out erasure of a closure starts `fulfilling` at once, has no window and cannot be
   cancelled, because closure is irreversible once Begin commits. An export finishes once
   it is `fulfilling` and its artifact is then revoked if an erasure began; it is not
   cancelled mid-run. Every terminal status (`completed`, `blocked`, `failed`,
   `cancelled`) frees the one-active slot, so a failed request is recovered by filing a
   new one. `failed` is read from the job phase (a quarantined or dead-lettered job), as
   the invitation status reader does, so no hook runs in a dead job; the one-active check
   and the receipt use the same reader. A request holds an immutable snapshot of the
   subject and group, so removing the Membership strands neither pending work nor its
   status. `completed` means every step, including the privileged one, finished; the
   receipt never says it earlier.
9. **Retained own scope.** A person reads and files requests after their Membership is
   gone, and a Suspended person does so at all, through a path modelled on the consent
   subject of [ADR-0095](0095-revision-pinned-membership-custom-fields.md): a new definer
   function `fn_identity_own_data_request_scopes` finds the groups where the subject has
   any Membership or any request, with the same confinement, grants and quota rules as
   the consent function; a new `TenantContextOrigin` admits the resulting tenant context;
   and the closed native retained-entry set grows by the operations list, create, read,
   cancel, ticket and chunk. ADR-0095's statement in its consent-only entry section that no
   extra global reader, function or owner is introduced receives a dated Amendment 5, and
   the function and setter censuses of Standard 05 grow by this function. The function
   reads `data_subject_requests` and `memberships` through owner arms added for it alone,
   as the consent function reads its table through one, and the step record states each
   arm. The status receipt holds only the request identity, kind, status, timestamps,
   `last_refusal` and the closed retained-reference kinds of ADR-0107 § 6; it holds no
   personal data beyond the subject identifier the request row itself carries.
10. **Idempotency and transport.** Creation requires an `Idempotency-Key` and answers 202
    with the request resource and its `ETag`; every later mutation requires `If-Match`
    ([ADR-0037](0037-idempotency-key-contract.md),
    [ADR-0079](0079-bounded-identity-administration.md)). OpenAPI and the SDK change only
    by additions.
11. **Cancellation window.** An `erasure` and a `closure` start `scheduled` with an
    execution time 24 hours after creation, except a closure's fan-out erasure (§ 8). The
    requester or the subject cancels until then, and a closure until its Begin commits. An
    `export` has no window; its download ticket's lifetime is a separate, shorter limit.
12. **Targets and escalation.** An export is ready within minutes and always within 7
    days; an erasure or closure not `completed` after 30 days is a defect. The operator
    inspection of [ADR-0107](0107-data-request-fulfillment-and-account-closure.md) § 8
    lists requests awaiting an operator step, overdue `scheduled` closures and stuck
    `fulfilling` requests, and the runbook names the escalation.
13. **Audit.** Every creation, cancellation, completion, administrator status read and
    artifact retrieval is a MUST-class operation committed with its state change
    ([ADR-0033](0033-audit-durability-model.md)); a read of another person's request is
    `read-sensitive` and refuses on audit failure. The Step 1 record fixes the operation
    names and registers them in the Identity audit matrix.

## Context

- **Why the group is the unit.** The policies that protect organizations also decide what
  one transaction may write, so a request that spans organizations would need a new RLS
  exception. A request per group keeps every write under the context that owns it, at the
  cost of one filing per organization for a person in several.
- **Why no delegate.** A guardian changes who may receive personal data and nothing in
  the corpus defines how that party is proven. Refusing it is safe and cheap to widen.
- **Why reuse `identity.membership.admin`.** The holder can already revoke and read a
  member; a request is not a wider capability over the same person. A separate key costs
  a G20 transition and a registry release this packet does not need.
- **What was not decided here.** The right to restriction and the right to object stay as
  [Data Protection](../architecture/23-data-protection.md#right-to-restriction-of-processing)
  states them with no mechanism; the owning phase is Phase 08a (the first notification
  writer) and Phase 09 for analytics ingestion. Marketplace purposes stay outside Phase 03
  ([ADR-0049](0049-institution-sites-and-course-marketplace.md) is Proposed).

## Consequences

### Positive

- A learner exports or erases their own data in a group with no administrator, no new
  key and no change to the row policies.
- Terminal states free the slot, so no request can block a person forever.
- No assignable key is added, so the pinned OpenAPI document changes only by additions.

### Negative

- A person in several organizations files once per organization.
- The retained own-scope path grows by several operations, each with its own denied proof.
- An administrator-created request reaches a subject who may not know of it; the subject
  sees it on their own status page, and the audit row records the administrator.

### Neutral

- The group, not the tenant, is the unit of every tenant request and receipt; a closure is
  the one global request and is filed in its originating group.

## Implementation Notes

- The tables, policies, grants, native operations and census updates land in the P03-10
  steps recorded in the Phase 03 delivery log. Each new tenant table follows the canonical
  template in [Database Standards](../standards/05-database.md) and gets isolation tests
  as `learnstack_app`.
- Rules that enforce this ADR: `Tenant_Scoped_Export_Contains_No_Foreign_Tenant_Rows`
  (two-tenant person, a same-tenant second person and a foreign organization),
  `Permission_Registry_Has_DeniedTest` for every administrator operation, and a census
  that no assignable key and no grant-union byte changed.

## References

- [ADR-0033](0033-audit-durability-model.md), [ADR-0037](0037-idempotency-key-contract.md),
  [ADR-0079](0079-bounded-identity-administration.md),
  [ADR-0095](0095-revision-pinned-membership-custom-fields.md),
  [ADR-0100](0100-p038-federation-contract-transition.md),
  [ADR-0107](0107-data-request-fulfillment-and-account-closure.md),
  [ADR-0108](0108-privileged-audit-redaction.md)
- [Data Protection](../architecture/23-data-protection.md),
  [Identity module](../modules/identity/README.md),
  [Phase 03](../roadmap/phase-03-identity-admin.md)
