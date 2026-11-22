# ADR-0107: Data Request Fulfillment and Global Account Closure

## Status

Accepted — 2026-11-22, after the maintainer reviewed the drafts, four independent reviews of them and the corrections those reviews led to. The dated record is the [Phase 03 acceptance record](../roadmap/phase-03-delivery-log.md#p03-10-decision-package-and-acceptance-2026-11-22). Acceptance closes the workflow part
of gate G16 of [Phase 03](../roadmap/phase-03-identity-admin.md#the-decision-register)
(its shared fence part was Accepted earlier) and, with
[ADR-0106](0106-subject-data-requests-authority-and-status.md) and
[ADR-0108](0108-privileged-audit-redaction.md), is the P03-10 decision package. It
authorizes the code it describes and is not proof that anything is implemented.

**Date:** 2026-11-22
**Deciders:** @cemil

The maintainer chose, on the questions the package put to them: a human operator for
the privileged step; an encrypted PostgreSQL artifact behind a port; anonymise-and-revoke
for the Membership; a 24-hour cancellable window; deletion of the Keycloak user; no
whole-tenant offboarding in this packet; and in-product status with no new notification.
The maintainer also confirmed that the application is in development and not in use, so
no backward compatibility is owed for the schema, grants or contracts this ADR changes.

## Decision Drivers

- G16 asks how exports, closure and privileged redaction finish durably: artifact
  delivery, reauthorization, expiry, job and storage prerequisites, participant
  inventory, the write fence, the last-membership case, provider finalization, exact
  grants and order, and reconciliation.
- No object storage port or adapter exists, no download path exists, the native web
  transport carries only JSON of at most 256 KiB in 10 seconds, and the only e-mail
  carrier serves invitations.
- [ADR-0101](0101-platform-operator-authority.md) admits human operators only and refuses
  job and external frames the platform scope; the platform scope facade refuses SQL that
  names `audit_log`, writes one result row per scope, and refuses a row whose module is
  `identity`.
- `users` refuses UPDATE and DELETE for every role by trigger. `memberships` refuses
  DELETE for every role and the UPDATE of a Revoked row; the aggregate refuses profile
  and assignment changes outside Active or Suspended; a Revoked Membership's profile cannot
  change; invitations hold their recipient immutably and the aggregate refuses the system
  actor; `membership_field_consents` is append-only.
- The row policies of the Identity tables carry no owner arm, and admit a write of an
  organization-bound row only under that organization's announcement, so the unit of every
  write is one scope group
  ([ADR-0106](0106-subject-data-requests-authority-and-status.md) § 2).
- The identity fence contract orders a User fence before every affected exclusive Tenant
  key ([ADR-0072](0072-application-owned-identity-bootstrap.md),
  [ADR-0074](0074-membership-admission-under-ordered-locks.md)).
- Erasure must not claim more than it does: retained references are reported truthfully
  ([Data Protection](../architecture/23-data-protection.md)).

## Considered Options

1. **Participant registrations inside the module boundary, transactional jobs per scope
   group for the tenant side, human-operator acts for the privileged side, and
   PostgreSQL custody for the artifact** (chosen).
2. **Fan-out by integration events** (`DataExportRequestedV1`,
   `UserAnonymisationRequestedV1`) (rejected). Every consumer needs an inbox, a
   subscription and replay semantics, and a global closure has no real tenant for the
   ordinary outbox. The work is group-local and needs one transaction.
3. **An automatic confined database role for the privileged step** (rejected). It would
   supersede ADR-0101's rule that no machine acts privileged and add a trigger exception
   and a standing connection.
4. **A storage port and a SeaweedFS adapter now** (rejected). It pulls Phase 04's media
   work forward and adds a provider SDK and its boundary rule for one artifact class.
5. **A single-part artifact of about 192 KiB** (rejected). An administrator's own audit
   rows exceed it quickly, so the export would fail for the people who act most.
6. **Hard delete of the Membership and profile rows** (rejected). It needs a critical
   DELETE exception on a table closed to every role, while the same person's identifier
   survives in audit and in the global binding.
7. **Hold every tenant key in one operator transaction through the whole closure**
   (rejected). The per-group erasure runs as the application role and needs those keys.
8. **Definer functions for the closure participant list and the `users` transitions**
   (rejected). The Identity tables have no owner arm and the `users` policy admits only
   the application role, so a migration-owned function sees no rows and its UPDATE fails
   the policy. The platform role bypasses row security, so column-limited grants and one
   trigger exception are smaller and need no new policy arm.

## Decision

### 1. Participants

LearnStack introduces no integration event for these workflows. A foundation port
`IUserDataParticipant` in `LearnStack.SharedKernel.DataProtection` is implemented by each
applicable module (`Export`, `Erase`, `DescribeRetained` for a subject and a scope group)
and collected by the composition root, as `IAuditCatalogSource` and `IOperationPolicySource`
are. These are registrations, not module-to-module calls: no module references another
module's code, so the four mechanisms of [ADR-0010](0010-cross-module-communication.md)
stay four, and ADR-0010 gains a dated clarification saying foundation-port registrations
are outside them. An architecture rule pins that no module references another module's
participant type.

**One transaction per participant.** The job runs each participant's `Erase` in its own
transaction and records the participant's completion in the request's progress record
before the next, so no transaction writes two modules' tables
([Database Standards](../standards/05-database.md): no cross-module write transaction), and
a resumed job skips completed participants. Today only Identity writes; Tenancy
(membership consents), Customization (definition attribution) and Education (creator
attribution) describe retained references, and Audit's participant only describes and
plans because its work is privileged (ADR-0108). Content and Media are scaffolds with no
table. Any module that adds a table holding a person reference, Phase 04's media and Phase
07's learner behaviour included, registers a participant and a locator before its first
writer ships; `Every_PII_Module_RegistersUserReferenceLocator` becomes a census over every
module with a classified person reference, with a planted missing registration.

### 2. Jobs

Per scope group the module declares, under
[ADR-0047](0047-transactional-background-job-runtime.md), a `TenantWide` and an
`Organization` transactional definition for each of export, erasure and artifact purge
(`identity.data_export_*`, `identity.data_erasure_*`, `identity.data_export_purge_*`,
version 1), with identifier-only parameters. The request is created in the group's own
context, so it submits the job of the matching scope. They run as SystemActor with a
trusted job policy and never enter the platform scope. The erasure and the purge are
submitted with `ScheduleAsync` at their due time (the 24-hour window and the artifact
expiry), so no recurrence is introduced. The runtime has no cancel call, so a job whose
request was cancelled or revoked meanwhile completes as a recorded no-op. The production
job census tests change in the commit that registers a definition.

### 3. Export bundle

The bundle is one JSON document with a schema version, the request identity, the
generation time and one section per participant, for the request's group.

- **Membership, profile and consents.** The Membership with status, organization, role
  names and timestamps; the display name and locale; every custom-field value with its
  definition revision, label and classification, including values withheld from ordinary
  disclosure by a withdrawn consent (`None` is not an exclusion); and the consent history.
- **Invitations.** Those in which the person is the recipient or the acceptor, with their
  own address and role; those the person issued, with the recipient address omitted (a third
  party).
- **Audit.** The rows of the request's scope in which the person is the actor, or the entity
  (an administrator's change to their Membership), as envelope fields (time, module,
  operation, outcome, entity type) with the person's own `ip_address` and `user_agent`, and
  the payload columns only for rows whose entity is the subject's own Membership, profile or
  consent, with any other person's reference replaced by `third_party`. A self-requested
  export selects these rows across the whole tenant, an administrator-requested one only
  within the group, as ADR-0108 § 4 states for redaction. A self-requested export also lists
  the person's own global identity events (their creation and failed attempts matched by the
  subject key) as envelope fields. The section is capped at 8 MiB, newest first; a cap hit
  is stated in the bundle (`truncated`, the rows included and the rows total) and in the
  receipt.
- **Excluded.** Tenant settings, other people's data, internal administrator notes,
  internal identifiers (including the `UserId`), the subject key, the raw provider subject
  and every credential, verifier, ciphertext and key.

### 4. Artifact custody, retrieval and expiry

- A port `IDataExportArtifactStore` with the default `PostgresDataExportArtifactStore`.
  The owning phase of an object-storage adapter is Phase 04 and the trigger is that phase
  shipping `IStorageProvider`, or a measured need to exceed the ceiling below.
- The bundle is encrypted with AES-256-GCM, a fresh 12-byte nonce per chunk, from a key
  ring in `ISecretProvider` (`DataExport:KeyRing`, one to four 32-byte keys with an active
  version, like the invitation envelope; the web launcher allow-list carries the name). The
  associated data binds format and key version, tenant, request, artifact, chunk index and
  chunk count. A chunk holds at most 128 KiB of plaintext. The bundle ceiling is the
  setting `DataExport:MaxBytes`, 16 MiB by default and 64 MiB at most; above it the request
  ends `failed` with the closed reason `export_too_large`, which the 8 MiB audit cap makes
  rare. A subject has at most one `available` artifact per group: a new export revokes the
  previous one, which bounds the stored volume; a per-tenant quota is Phase 11's, triggered
  by the first tenant storage limit.
- The artifact row has its own lifecycle `available`, `revoked`, `expired`, `purged`. It
  expires 7 days after completion, so a request filed on the last allowed day is readable
  for 7 more. Expiry is a time check: a read after `expires_at` is refused whether or not
  the purge job ran, and the operator inspection lists expired artifacts not yet purged. The
  delayed purge job deletes the chunk rows and keeps the metadata row as the receipt. The
  subject or the requesting administrator may revoke an unexpired artifact, which purges it
  at once.
- Retrieval is subject-only, through the live session or the retained own-scope path of
  ADR-0106 § 9. A CSRF-protected POST mints a ticket valid 15 minutes, bound to the session
  and the request and stored as a digest in `data_export_tickets`. It is a download cursor:
  it authorizes the sequential chunk reads of one download, is marked consumed when the
  final chunk is served, and a resumed or repeated download mints a new ticket. The API
  decrypts each chunk and serves it as JSON (`index`, `count`, base64 `bytes`, at most 256
  KiB) that the BFF concatenates into one `attachment` response; no ciphertext leaves the
  API and no URL is a bearer credential. Every ticket mint and every chunk read is a MUST
  `read-sensitive` audit row, and an audit failure refuses the disclosure. The
  reauthorization is this explicit ticketed act in the live session; no step-up
  authentication claim exists for tenant sessions and none is made.
- This replaces the "signed URL" of Data Protection, which contradicted both
  [Security](../standards/11-security.md)'s short-lived URL rule and the revocation
  requirement.

### 5. Tenant-scoped erasure

The erasure job takes the canonical fences (the subject's exclusive User fence, then the
tenant key exclusively), re-runs the eligibility of ADR-0106 § 7, and in one transaction of
its group:

1. revokes the group's unexpired export artifacts of the subject and cancels its not yet
   started export requests (an export job re-reads the erasure state under the shared
   tenant fence before it writes and discards its output if an erasure began);
2. erases the Membership with explicit domain operations, `RemoveAssignmentsForErasure`,
   `ClearProfileForErasure` (display name and locale to NULL, custom values to `[]`) and
   `RevokeForErasure`. They accept the system actor and any status, are idempotent, and run
   only under the erasure witness, so a Suspended or an earlier-revoked Membership is
   handled by the same path and the ordinary methods keep their rules;
3. revokes the pending invitations the person issued in the group through a domain
   operation that takes the subject as the aggregate actor (the aggregate refuses the system
   actor), while the audit actor stays the job's.

The database accepts the profile clearing on a Membership of any status through one
exception in the contextual mutation guard: a clearing UPDATE of exactly those three profile
columns when the User fence and the tenant fence are held and an `erasure` request for that
subject and group in `fulfilling` exists. No other column, table or caller is widened. The
Membership row stays as a Revoked tombstone holding the pseudonymous `UserId`, organization,
status and the existing timestamps and actors; the phase text "removes the Membership and its
MembershipProfile" is corrected to say so. Tenant erasure never touches `users`, the
provider account or another group, and a person whose only Membership is erased keeps a
working global account with zero memberships. The request then moves to
`awaiting_privileged_step` and an operator completes it (§ 8).

### 6. Per-store treatment and the retained report

Every store that can carry the subject has one closed outcome, and the receipt reports the
closed kind, never a row identifier. The kinds are: `consent_evidence`, `invitation_record`,
`invitation_envelope`, `audit_actor_reference`, `audit_entity_reference`,
`audit_metadata_reference`, `unscoped_audit_rows`, `transport_evidence`,
`execution_evidence`, `provider_event_record`, `idempotency_cache`,
`attribution_reference`, `federation_actor_reference`, `request_receipt`, `external_copy`
and `unserved_group`.

| Store | Outcome |
|---|---|
| `membership_profiles` (name, locale, custom values) | cleared (§ 5) |
| `membership_role_assignments` | deleted |
| `memberships` | pseudonymised tombstone: Revoked, `UserId` retained |
| `data_export_chunks` | purged at revocation, expiry or erasure |
| `data_export_artifacts`, `data_export_tickets`, `data_subject_requests` | retained as `request_receipt`, holding no personal data beyond the subject identifier; retention target and purge are Phase 11's |
| `membership_field_consents` | `consent_evidence`: append-only evidence of lawful basis; a withdrawal admitted concurrently appends and is reported |
| `invitations` the person issued, pending | revoked |
| `invitations` the person accepted, pending ones addressed to them, `invitation_email_envelopes`, `invitation_email_attempt_receipts` | `invitation_record` and `invitation_envelope`: retained as the record of an authority-granting offer; no erasure transition exists, a pending offer is not locatable by subject and envelope ciphertext is immutable; Phase 11 retention |
| `audit_log` | six columns redacted by the privileged step (ADR-0108); the rest `audit_actor_reference`, `audit_entity_reference` and `audit_metadata_reference` |
| `outbox_messages`, `event_deliveries`, `inbox_messages`, `background_*`, Hangfire tables | `transport_evidence` and `execution_evidence`: immutable; erasure proves no quiescence |
| `identity_provider_event_receipts`, `_targets`, `_problems`, `_scan` | `provider_event_record`: independent 32-day retirement; tenant targets reported |
| `idempotency_keys` | `idempotency_cache` until the existing 24-hour retention; no new purge authority |
| creator, updater and deleter attribution on every mapped aggregate | `attribution_reference`: pseudonymous `UserId` |
| `federation_connections` (pending and test actor identifiers) | `federation_actor_reference`: retained; the broker and the row are untouched by subject erasure, and closure leaves them because the tenant owns them |
| `users` | untouched by tenant erasure; see § 7 |
| Keycloak events, diagnostics, WAL, backups, SMTP copies, BFF sessions | `external_copy`: reported only; Phase 11 owns purge and reconciliation |
| Phase 07 and later learner behaviour, Phase 04 media | integrated by the first writer through § 1 |

The retained outcomes are the truthful report of the request; none is called anonymous. The
recipient address of invitations the person accepted, and its encrypted envelope, is the
largest retained personal copy and stays until Phase 11 retention; acceptance of this ADR is
the maintainer's acceptance of that retention. The controller's own legal-basis review of
each retained class is owed per deployment and is not provided here.

### 7. Global account closure

- **Request.** Closure is a data request of kind `closure`, created by the person alone,
  through a live session or the retained path of ADR-0106 § 9, in the originating group,
  with the 24-hour window. The row lives in that originating tenant, no administrator
  creates or reads it (the administrator operations exclude the kind), and no Platform
  permission is involved. A person with no Membership has no tenant context and cannot file
  a closure through this surface; they hold no tenant data, and the global-self extension for
  them is owned by Phase 11 data operations and triggered by the first such request. The
  surface warns before submission that the account will stop working and that no in-product
  status will remain.
- **Approver.** The platform operator is the human approver. The runbook requires the
  operator to confirm the request with the person through the e-mail the identity provider
  holds, read in the realm console and never stored by LearnStack, before Begin; this is the
  control a hijacked session cannot supply.
- **Participants.** The operator's service reads, on the platform connection through a
  column-limited SELECT grant, every (tenant, organization-or-none) group in which the
  person holds a Membership of any status, including a Revoked one whose profile may still
  hold data, and reads each tenant's lifecycle through the existing Tenancy port
  `IIdentityScopeLifecycleReader`, so no cross-module join exists. The set is read live and
  stored in no tenant-readable table.
- **Begin.** An operator act in the platform scope (`PlatformScope` executor) takes the
  subject's exclusive User fence and then every participant tenant key exclusively in
  canonical order, on the platform connection, bounded by the scope's own wait (the step
  record names the mechanism), using the same key derivation as `IIdentityMutationLocks`,
  which is tenant-announced and single-tenant and cannot be used here;
  `Identity_Membership_Invariants_Use_Fresh_Ordered_Locks` gains a separate census leg for
  this acquirer. It refuses `blocked_window` before the request's execution time,
  `blocked_hold` when a hold source reports one, and `blocked_last_administrator` when a
  participant tenant that allows ordinary work would be left without another eligible
  tenant-wide administrator (an Active User with an Active tenant-wide administrator
  Membership); a tenant that does not allow ordinary work (not Trial or Active, per
  ADR-0102's lifecycle) is not checked and does not block. A refusal leaves the request
  `scheduled` with its `last_refusal`, so the person can act on it. Otherwise, in one
  transaction, it moves the User Active to Closing, sets the closure request `fulfilling`
  and writes the result row. The User move is a direct UPDATE by the platform role of
  `status` and `provider_subject` only, under one trigger exception that requires the
  platform session, the exclusive User fence and every participant tenant fence held on that
  connection and permits only that transition. If the User is already Closing for another of
  the person's closure requests, Begin resumes it instead. The fences are held through that
  transaction, which is the nonadmitting transition they exist to order; Standard 05 and
  Phase 03 G16 gain a dated erratum saying that "through completion" means this. The same
  operator call then performs the fan-out.
- **Fan-out.** `fan_out_closure` is the per-group pipeline act `begin_closure` invokes, not
  a separately routed endpoint. For each participant group, under ADR-0102's confined
  announcement extended to announce the group's organization, it creates that group's
  `erasure` request in the group's own transaction (origin closure, `fulfilling`, no
  window, business actor SystemActor with the operator attribution in the audit metadata)
  and enqueues its erasure job. An existing non-terminal erasure of the same subject and
  group is adopted instead of conflicting; a terminal one that did not complete is replaced
  by a new request. A repeated Begin call resumes every group lacking a completed or
  in-flight request, and the fan-out erasure ends `awaiting_privileged_step`.
- **Closing is irreversible and cannot stick.** There is no path from Closing back to
  Active, and a person cannot be resurrected by a delayed mirror, a pending invitation, a
  new Membership, provider intake or delayed work, which all refuse a non-Active User by the
  existing rules. Closure retries until it finishes: a group whose job failed is retried by
  repeating Begin; a group whose tenant does not allow ordinary work is `unserved_group`.
  Finalize may complete with unserved groups, which it must name; their data stays and is
  reported, and Phase 11's offboarding owns them.
- **Finalize.** A second operator act, allowed when every participant group's request is
  `awaiting_privileged_step` or `completed`, or its group is named `unserved_group`, runs in
  two transactions. The first redacts the subject's audit rows in every participant tenant
  and the platform sentinel tenant (ADR-0108) and writes the intent row that carries the
  redaction counts. The Keycloak user is then deleted through `IIdentityProviderAccountRemoval`
  (a missing user is success). The second transaction sets Closed, removes the raw provider
  subject through the same column-limited UPDATE under the same trigger exception, keeps the
  key and actor as the permanent tombstone, marks every participant request and every
  non-terminal closure request of the subject `completed`, and writes the result row. A crash
  between the steps is repaired by re-running the act, which is idempotent.
- **Provider port.** `IIdentityProviderAccountRemoval` is bound to
  `IProviderResilience<IIdentityProviderAccountRemoval>`, has the default implementation
  `RefusingIdentityProviderAccountRemoval`, which fails closed, and a Keycloak adapter
  selected at the composition root. The adapter's confined realm client holds `manage-users`;
  that role also lets the client create and update users and reset credentials in the shared
  realm, so a leaked secret reaches every tenant's users. The accepted mitigations are the
  secret store, rotation by the runbook and the provider's own admin-event stream, whose
  retention is Phase 11's. Fine-grained admin permissions are not relied on; if the pinned
  Keycloak supports scoping the deletion, the adapter adopts it by a dated amendment.
- **Reach and status.** A Closing or Closed person's sessions fail at the next API
  admission with the uniform 401; deleting the Keycloak user ends refresh, and an issued
  access token grants nothing because binding admission fails. While Active the person
  reads the request through their session; afterwards there is no in-product status, and the
  operator confirms completion out of band from the retained closure request. A consent
  withdrawal needs an admitted entry, so none follows closure; the consent evidence stays and
  is reported.
- **No refund.** Closure refunds no creation budget or permanent allocation, and a Closed
  key never reappears at issuer rebind.

### 8. Principals, grants, operations and keys

| Principal | Grant |
|---|---|
| `learnstack_app` | SELECT, INSERT and UPDATE of the status, `row_version`, progress and result columns on `data_subject_requests`; INSERT, SELECT and UPDATE of status on `data_export_artifacts`; INSERT, SELECT and UPDATE of the consumed marker on `data_export_tickets`; INSERT, SELECT and DELETE on `data_export_chunks`; EXECUTE on `fn_identity_own_data_request_scopes`; the profile clearing of § 5 under its witness |
| `learnstack_platform` | A closed-column SELECT on `memberships`, `users` and `data_subject_requests`; UPDATE of the status, `row_version`, completion time and result columns on `data_subject_requests`; UPDATE of `status` and `provider_subject` on `users` under the trigger exception of § 7; its existing six-column UPDATE on `audit_log`; no other privilege |
| Keycloak | One new realm client with `manage-users`, its secret in `ISecretProvider` |

The one new definer function, `fn_identity_own_data_request_scopes`, reads
`data_subject_requests` and `memberships` through an owner arm added for it alone, the way
the consent function reads its table. Two non-assignable Platform keys are registered:
`platform.data_request.read` and `platform.data_request.write`. The platform operations are
closed registry entries. Read: `inspect` (a platform-scope read with an entry row, in the
style of [ADR-0103](0103-operator-recovery-of-asynchronous-work.md) § 6, listing requests
awaiting an operator step, overdue `scheduled` closures, stuck `fulfilling` requests and
expired artifacts not yet purged). Write: `complete_erasure` (audit slug
`audit.redaction.apply`, ADR-0108), `begin_closure`, `fan_out_closure` (pipeline executor)
and `finalize_closure`. The Begin and Finalize rows use a non-`identity` module because the
operator result write refuses `identity`
([ADR-0044](0044-audit-write-path.md) Amendment 17) and carry the platform sentinel tenant;
each participant tenant's own fan-out request carries its tenant's MUST rows, which is the
"requested, recorded against every affected tenant" evidence. The step records fix the exact
slugs and spellings, and ADR-0101 (the key table) and ADR-0102 (the announcement) gain dated
amendments. An ordinary job, a dispatcher credential or a SystemActor holds none of this
authority.

### 9. Transport stores and the replay floor

[ADR-0103](0103-operator-recovery-of-asynchronous-work.md) § 9 asked for a floor. It is:
erasure never rewrites or deletes an outbox, delivery, inbox or job row; a replayed or reset
delivery re-runs its handler under that handler's own subject rules, which refuse a Closing
or Closed User but not a tenant-erased one, whose User stays Active, so a replayed
subject-linked delivery may write rows for that person and is reported as retained transport
evidence; the recovery runbook states that erasure does not reset replay; and the step
records prove both cases with an actual replay. The Active-tenant test stays.

### 10. Out of scope, with owners

| Item | Owner and trigger |
|---|---|
| Whole-tenant deprovisioning, including the `federation_connections` row and broker of an archived tenant, and the data of an `unserved_group` | Phase 11 data operations; the first real tenant offboarding request. ADR-0099 gets a dated amendment saying so. |
| E-mail or other notification of a request | Phase 08a |
| Right to restriction | Phase 08a (notifications) and Phase 09 (analytics) |
| Terminated-host recovery for consent-only entry | Phase 11; the first terminated tenant host |
| Closure for a person with no Membership | Phase 11 data operations; the first such request |
| Audit-log read-API export files and their retention | Phase 06 (Studio audit screens); the first export writer |
| The Hub compliance cap `gdpr.hard_delete.enabled` | Governs hard delete of tenant content; anonymise-and-revoke uses none, and no consumer is added |

## Context

- **Why the privileged half is human.** ADR-0101 makes the operator a person with a
  per-act permission and refuses jobs and external frames the platform scope. The
  privileged half is small and rare, and a visible `awaiting_privileged_step` is honest
  about the wait. An automatic role would be a standing principal on a table closed to
  every role.
- **Why anonymise and revoke.** The identifier survives in audit and in `users`
  regardless, so deleting the Membership row removes little. The aggregate already
  transitions to Revoked, and the one new database exception is the narrowest that makes an
  earlier-revoked person's profile erasable.
- **Why two operator acts for closure.** The fences must be held while the User becomes
  nonadmitting and free while the per-group erasure runs as the application role. Begin and
  Finalize separate those, and each is idempotent and resumable.
- **Why a sole administrator can be refused and an unserved tenant cannot.** A tenant that
  allows ordinary work needs an administrator, so the person hands over first; the 30-day
  target of ADR-0106 § 12 counts from when no refusal stands. A tenant that is suspended or
  archived has no ordinary work to protect, so it must not hold a person's closure hostage;
  its data stays, is reported, and has a named owner.
- **The recovery predicate.** The P03-9 closeout asked whether operator recovery needs a
  user-eligibility-aware predicate. It does not: Begin refuses to leave an operable tenant
  without another eligible administrator, and operator recovery can name a new
  administrator for any other tenant.
- **What would change our minds.** A hold source, a delegate authority, an object-storage
  adapter, a second administrator population or a real offboarding request each reopens the
  part this ADR names, with the phase and trigger above.

## Consequences

### Positive

- Every Accepted retained-reference promise is reported per request, not asserted.
- The widened database authority is the witnessed clearing, one `users` trigger exception
  and column-limited platform grants; no new owner arm except the one retained-scope function.
- No new integration event, recurrence or notification sender is introduced.

### Negative

- Erasure and closure each wait for an operator for the privileged step.
- A person in several organizations files and tracks one request per group.
- The recipient address of invitations the person accepted remains until Phase 11.
- A sole administrator of an operable tenant cannot close until they hand over.
- A Closing person gets no in-product confirmation.
- The audit section of a very large export is capped and reports the cap.

### Neutral

- The Membership tombstone keeps a pseudonymous identifier, as audit and `users` do.

## Implementation Notes

- The step records name the exact slugs, keys, tables, migrations and function names and add
  them to the module matrices, the permission matrix, the audit catalogue and the
  architecture-test catalogue.
- New tables join the durable asynchronous store inventory or its exclusion list
  (`Durable_Async_Store_Inventory_Covers_The_Storage_Census`) and the personal-data
  classification census in the same commit. The function and setter censuses of Standard 05
  and ADR-0095 grow by `fn_identity_own_data_request_scopes`.
- The acceptance commit corrects the carriers that still describe the old design: Data
  Protection (the `DataExportRequestedV1` and `UserAnonymisationRequestedV1` fan-out, the
  removal of the Membership, the Keycloak invalidation, the final reconciliation job, the
  `name`/`email` placeholders and the signed URL), the Phase 03 text and C18, the Identity
  module spec, Audit Subsystem § 10 and ADR-0016's and ADR-0044's mentions of
  `UserGdprDeletedIntegrationEvent`, and the glossary.
- Bulk changes use tracked aggregates, never `ExecuteUpdate`, `ExecuteDelete` or
  `ExecuteSql*` (`No_Set_Based_Write_Bypasses_The_Audit_Capture`).

## References

- [ADR-0010](0010-cross-module-communication.md),
  [ADR-0037](0037-idempotency-key-contract.md),
  [ADR-0044](0044-audit-write-path.md),
  [ADR-0047](0047-transactional-background-job-runtime.md),
  [ADR-0072](0072-application-owned-identity-bootstrap.md),
  [ADR-0074](0074-membership-admission-under-ordered-locks.md),
  [ADR-0079](0079-bounded-identity-administration.md),
  [ADR-0097](0097-retained-provider-events-and-tenant-audit-mirror.md),
  [ADR-0099](0099-confined-tenant-federation.md),
  [ADR-0101](0101-platform-operator-authority.md),
  [ADR-0102](0102-production-tenant-onboarding.md),
  [ADR-0103](0103-operator-recovery-of-asynchronous-work.md),
  [ADR-0104](0104-controlled-issuer-rebind.md),
  [ADR-0106](0106-subject-data-requests-authority-and-status.md),
  [ADR-0108](0108-privileged-audit-redaction.md)
- [Data Protection](../architecture/23-data-protection.md),
  [Database Standards](../standards/05-database.md),
  [Phase 03](../roadmap/phase-03-identity-admin.md)
