# ADR-0097: Retained Provider Events and Tenant Audit Mirror

## Status

**Accepted — 2026-11-11.** The maintainer approves P03-6/G10 and only the
machine-ingress part of G13 after the review corrections recorded in the
[acceptance entry](../roadmap/phase-03-delivery-log.md#p03-6-maintainer-review-corrections-and-acceptance-2026-11-11).
Acceptance installs no runtime capability. Human Platform authority and recovery
remain P03-9/G13 Open. The application has no active external users; this package
does not owe a legacy-client compatibility layer.

**Bounded changes:** this record extends ADR-0003's four-role
model with one confined machine login and its twelve-function census with the
three functions below. ADR-0036's context-construction/authority-ceiling boundary
gains the machine-only `TenantContextOrigin.ProviderEventRecipient`; its HTTP
resolution matrix is unchanged. ADR-0040's logical announcement census gains
only the provider-recipient owner. ADR-0072's global storage boundary admits the named
intake class, without widening bootstrap or its global audit discriminator.
ADR-0044/0077 gain one typed central mirror-evidence producer. ADR-0070's
development-only provider retention gains an application consumer; its session
contract is unchanged. ADR-0046/0057's tenant outbox, payload and inbox contracts
remain in force. Dated Amendments name these additions without rewriting the
Accepted bodies or marking the whole records Superseded.

**Date:** 2026-11-11
**Deciders:** @cemil

## Decision Drivers

- Authenticate the actual provider source and durably accept stable facts before
  attributing them to tenants; no tenant supplied by provider data is authority.
- Reuse Keycloak's existing persistent user-event store and LearnStack's working
  outbox/inbox, avoiding another broker, Java extension or HTTP webhook surface.
- Separate provider-global account activity from an action inside a tenant.
  Preserve unknown, unbound, tenantless and closed-account outcomes honestly.
- Recover duplicate, reordered and uncertain-commit intake without multiplying
  successful tenant mirrors or granting ordinary application code a directory.
- Make the finite recovery, extra database authority, privacy and retained-copy
  costs explicit before the first writer.

## Considered Options

1. **Authenticated pull and frozen tenant projections** (chosen). Repeatedly
   read the retained user-event window, commit minimized receipts and route each
   accepted fact once. Recovery is conditional on source retention and scanning.
2. **Provider-transactional SPI spool and acknowledged push** (not selected).
   Stronger capture needs a durable provider spool, explicit failure/rollback
   behavior, Java build/image/schema ownership and cluster recovery. A plain
   HTTP listener or after-commit callback does not supply those guarantees.
3. **Provider history only** (not selected). Keeps today's boundary but does not
   deliver the required tenant Audit mirror or application intake receipt.
4. **Trust tenant claims, email or the BFF's current scope** (rejected). The
   provider fact does not prove a particular tenant act or current Membership.

## Decision

LearnStack pulls selected persisted user events from the installed identity
authority using a dedicated read-only Keycloak service account. A confined
machine owner commits minimized global receipts and immutable tenant targets;
each target produces an identifier-only fact through the ordinary tenant outbox.
Audit consumes it through the shared transport inbox and central MUST write path.
This uses the [receipt, target and recipient witness vocabulary](../glossary.md#identity--membership).
It is a delayed provider-history mirror with finite recovery, not session
revocation, a complete provider ledger or proof of a tenant operation.

### Provider authentication and selected facts

- The adapter pins Keycloak 26.6.2, the installed exact issuer/AuthorityId and
  explicitly configured realm ID. It calls only the fixed token endpoint,
  `GET /admin/realms/learnstack/events/config` and
  `GET /admin/realms/learnstack/events`. No supplied URL, redirect, discovery
  override, `/admin-events`, user-management or event-deletion call is admitted.
- A separate confidential client, `learnstack-event-reader`, uses client
  credentials and only realm-management `view-events`. That provider role can
  also read admin-event history: the credential's upstream authority is broader
  than the adapter's admitted calls. It grants no LearnStack permission. Do not
  reuse the BFF secret, master administrator or existing demo service account.
- HTTPS certificate verification is mandatory outside the existing explicitly
  local development issuer allowance. Secrets use `ISecretProvider`; access
  tokens remain adapter memory only. Redirects and ambient proxy credentials are
  refused. The existing provider resilience contract applies to safe reads.
- The selected catalogue is LOGIN/LOGIN_ERROR, LOGOUT/LOGOUT_ERROR,
  SEND_RESET_PASSWORD/SEND_RESET_PASSWORD_ERROR, RESET_PASSWORD/RESET_PASSWORD_ERROR,
  UPDATE_CREDENTIAL/UPDATE_CREDENTIAL_ERROR, REMOVE_CREDENTIAL/REMOVE_CREDENTIAL_ERROR,
  UPDATE_PASSWORD/UPDATE_PASSWORD_ERROR, UPDATE_TOTP/UPDATE_TOTP_ERROR,
  REMOVE_TOTP/REMOVE_TOTP_ERROR and USER_DISABLED_BY_TEMPORARY_LOCKOUT /
  USER_DISABLED_BY_PERMANENT_LOCKOUT. Legacy types are distinct source facts,
  not aliases deduplicated by subject/time. Credential events remain generic;
  they do not assert MFA enrollment unless the qualified provider fact proves it.
- Unselected events, including ordinary code exchange, refresh and service
  login, produce no receipt or tenant mirror. Existing saved provider selection
  is preserved and unioned with required types; listeners/admin policy survive.
  A lockout event does not revoke BFF owners; that is P03-7.

### Scan, acknowledgement and recovery boundary

Activation records an immutable lower time boundary equal to activation time
minus the qualified source retention. It imports the still-retained selected
history, not an invented complete pre-activation history. No automatic User
creation, profile/email synchronization or identity merging occurs.

The adapter qualifies enabled event storage, required types and finite retention
of 7–30 days. Thirty days is LearnStack's selected policy maximum, not a
Keycloak configuration limit: it bounds full-sweep read amplification and keeps
source replay inside the 32-day receipt horizon. Fresh development retention
stays seven days. A longer existing policy is preserved by the P02b-6 development
realm reconciler but refuses mirror activation until the operator explicitly
resolves it; history is never deleted or silently shortened.

- Startup and hourly retained-window sweeps use inclusive millisecond bounds,
  ascending source time and bounded offset pages. Recent five-minute reads every
  thirty seconds reduce latency; they never replace a full sweep. Concurrent
  instances may read the same source; database uniqueness owns acceptance.
- One retained HTTP owner per API process, pages of at most 100 events, a 1 MiB
  response ceiling and a five-second total request budget bound each read.
  Bounded work slices resume unfinished sweeps; no page/time ceiling counts as
  completion. Polling, source cleanup and cancellation join actual work before
  freeing capacity. No new CI or application request window is selected.
- A successful local receipt COMMIT is the **application acceptance
  acknowledgement**. An HTTP 200 or a page cursor is not that acknowledgement.
  The provider receives no per-consumer ACK and expires history independently.
  An uncertain COMMIT reconciles the original stable identity; it does not mint
  another event or recompute recipients. A failed page leaves its scan incomplete.
- Source pagination orders only by time, with no ID tiebreaker or snapshot.
  Repeated sweeps mitigate late commits, equal timestamps and shifting pages;
  they do not prove gap-free capture. Expiry, manual purge, disabled capture,
  backup loss or sustained backlog can permanently lose unaccepted facts.
- Health records last attempted/completed sweep, policy/auth faults and backlog
  using closed categories, without person/tenant/event labels. A complete sweep
  older than two hours is degraded; approaching source retention minus 24 hours
  is critical. An observed recovery gap remains recorded; restarting or finishing
  a later scan does not retroactively prove that missing history was captured.
  This is a separately registered machine health check, not a new anonymous
  diagnostics route or a change to the existing public liveness response.

### Unresolved facts and continued scanning

One problematic item must not monopolize recovery of other retained history.
The global scan ledger owns durable, minimized problem records as part of the
same confined machine class; this adds no fourth definer or public recovery API.

- A normalized event that cannot be accepted, including a 257-tenant recipient
  set, records its stable source identity, immutable normalized fingerprint,
  closed refusal category and retry state. It is **unaccepted**, has no target,
  and earns no acceptance ACK. Successful siblings on that page commit normally;
  later pages and other time ranges continue. Retrying an unaccepted record may
  freeze recipients only when it first becomes acceptable; accepted recipients
  are never recomputed. A collision never overwrites an accepted fact.
- Malformed or oversized pages are retried with progressively smaller `max`
  values down to one at the same position. An unreadable singleton records only
  authority/realm, requested time interval, offset and closed reason. No raw body,
  arbitrary error, email, subject or body hash is retained. Continue probing the
  next position and schedule other intervals independently. Offset movement is
  already an acknowledged source limitation; a skipped position is an unresolved
  coverage gap, never evidence that an event was durably accepted.
- Problem retries use bounded fair work slices separate from forward scanning;
  restart resumes both queues. No immediate loop on one poison item and no
  source deletion, target truncation or timestamp watermark can hide it.
  Database unavailability, authentication or invalid source policy stop the
  affected lane; inability to persist a problem never authorizes progress ACK.
- Traversal may finish with unresolved problems, but the sweep is not marked
  complete. Coverage health names the closed problem category, age and count;
  the existing two-hour/retention-minus-24-hour thresholds still apply. Source
  expiry changes a problem to a recorded irrecoverable gap, not success. Retain
  minimized problem records for 32 days and retain the singleton's historical
  gap flag/earliest-gap time after retirement.
- The operating guide gives read-only problem inspection and correction of
  source policy/availability before retry. Fanout overflow requires correcting
  the real Membership state through existing authorized administration or a new
  reviewed fanout decision; operators cannot edit targets or bypass the cap.
  Malformed provider output requires provider repair. Manual fabricated ACK or
  a generic global replay/repair command is not part of this package.

### Global receipt and immutable attribution

`(AuthorityId, exact realm ID, provider event ID)` is the durable intake identity.
The provider ID is validated against the pinned UUID profile and retained; no
fresh application UUID, timestamp or payload equality replaces it. A separate
owner-minted ReceiptId identifies local custody.

Each returned fact must match its requested realm, selected type and inclusive
scan window; a malformed or out-of-window response is not accepted as history.
The allowlisted immutable fact contains source identity, source occurrence time,
closed type/outcome, and the existing authority/opaque-subject equality key when
present. No raw subject, username, email, IP, session ID, client attributes,
arbitrary error, credential details or raw JSON enters application persistence,
outbox, Audit, logs or artifacts. Equality keys and pseudonymous IDs remain PII.
The canonical fingerprint covers the normalized immutable fields; ignored
provider details are not fingerprinted. A matching duplicate returns the original
receipt/disposition; an identity collision with changed normalized facts refuses
and raises an integrity fault. Concurrent insertion losers reconcile on a fresh
READ COMMITTED snapshot, including after lost acknowledgement.

One acceptance transaction freezes distinct recipient tenants from the subject's
current nondeleted Active or Suspended Memberships, only for an existing Active
global User. The User fence orders acceptance against global lifecycle changes;
the Membership read is one fresh committed snapshot after acquiring that fence.
It is acceptance-time attribution, not event-time Membership or a serializable
promise against concurrent Membership edits. No Role/grant or Tenancy table is
joined and no directory is returned to the service.

| Source/binding state at first acceptance | Frozen outcome |
|---|---|
| Missing provider subject | Receipt only: `subject_absent` |
| Unknown authority/subject binding | Receipt only: `subject_unbound` |
| Closing/Closed global User | Receipt only: `subject_closed` |
| Active User without retained eligible Membership | Receipt only: `no_recipient` |
| One or more eligible recipient tenants | One target per distinct tenant, org NULL: `routed` |

Unknown/unbound facts are never retroactively routed after bootstrap. A later
Membership or Role change does not recompute a receipt's targets. More than 256
recipient tenants refuses the whole acceptance without truncation or ACK.
An unavailable binding store is a retryable failure, never `subject_unbound`.

**Privacy/product choice:** each recipient receives the same minimized
provider-global fact, even when its activity occurred outside that tenant or
before its current Membership. It reveals no other recipient, organization,
host or session. A tenant copy must describe provider history, never a successful
tenant login or an act authorized by that tenant. The provider subject is the
affected person, not automatically the human initiator of a reset/lockout.

### Confined machine authority and tenant production

`learnstack_identity_ingress` is a fifth, NOINHERIT/NOBYPASSRLS login, with no
membership in the existing roles, database/schema creation or business/Audit/
outbox access. It is not a Platform Admin. A provider-only data source with at
most two actual checkouts per API process performs only the named intake,
claim, retirement and fixed sweep-ledger operations. Five-second shared SQL I/O
budgets, finite server/driver bounds, cleanup retention and startup qualification
follow the [dedicated-source ownership pattern](../standards/05-database.md#dedicated-provider-source-and-uncertain-commits).
Two is this lane's independent allocation, not a share of SMTP's four checkouts
or the ordinary application pool. Credentials are never exposed to a
module, ordinary UoW, HTTP endpoint or native process.

Identity owns global `identity_provider_event_receipts`, the minimized problem
ledger and its singleton scan ledger, with no app-role SELECT/INSERT/UPDATE. Tenant-owned
`identity_provider_event_targets` contains the independently retained minimized
fact, stable target/EventId, tenant, optional affected UserId and scheduling
state. It uses the normal marker/filter/FORCE-RLS/test layers, org NULL, and
unique `(AuthorityId, realm ID, provider event ID, tenant_id)` identity.

Three new migration-owned SECURITY DEFINER functions have fixed
`search_path = pg_catalog`, schema-qualified static SQL, explicit owner-policy
arms under FORCE RLS and same-transaction PUBLIC EXECUTE revocation:

| Function | Closed authority |
|---|---|
| `fn_identity_provider_event_accept` | Ingress role only; exact installed authority and selected normalized fact; lock/recheck binding, create receipt and complete frozen target set atomically. No context setter or provider/person projection. |
| `fn_identity_provider_event_claim` | Ingress role only; claim one due frozen target with fresh acquisition token and database-time lease. Return only that target's execution keys, never User/Membership/profile rows or a general tenant list. |
| `fn_identity_provider_event_retire` | Ingress role only; bounded removal of receipts/problem records older than 32 days while preserving historical gap state. No target, business, outbox, inbox or Audit deletion. |

This extends the qualified twelve-function census to fifteen. Owner privileges
are inherently wider than function projections; actual bodies, policies, ACLs and
call sites must be qualified together. Compromise of the service credential or
trusted adapter can fabricate source input; role separation is confinement, not
cryptographic database proof that Keycloak emitted an argument.

The actual claimed target mints a private single-use `ProviderEventRecipient`
witness bound to target/EventId, tenant, acquisition token, deadline and owner.
Only its closed producer can announce that tenant using the existing ordinary
app UoW and physical writer. This is the thirteenth logical tenant setter and
the machine-only `TenantContextOrigin.ProviderEventRecipient` authority ceiling,
restricted to this production act; four physical context
writers remain. A caller UUID, tenant header, service JWT or ordinary event
envelope cannot substitute for the witness. HTTP/native routes cannot enter it.

Each target gets one persisted UUIDv7 EventId, distinct across tenants because
the outbox primary key is globally unique. On one ordinary app transaction the
producer reads its exact RLS-protected target, enqueues
`UserProviderEventObservedIntegrationEventV1` on `learnstack.identity.user` and
marks the target enqueued. Payload adds only TargetId; type/outcome/time stay in
the retained tenant target. A transaction-bound completion gate checks actual
token, unexpired database-time lease and sticky timeout immediately before
physical COMMIT. Lost ownership rolls back the entire target/outbox work.
Enqueued targets are never reclaimed; uncertain COMMIT reconciles stored state.
No direct INSERT into outbox, direct PublishAsync or tenant retargeting is added.

### Audit subscription and retained copies

`audit.provider-event-mirror.v1` is required Active only with the real handler.
The transport restores tenant scope, claims its shared inbox before construction
and executes as SystemActor. The Audit handler obtains only the exact target's
immutable fact through an Identity application read contract bound to the actual
tenant, EventId, TargetId and subscription. It reads no current Membership or
global receipt, so source receipt retirement cannot break delayed delivery.

One closed typed `ProviderEventAuditEvidence` attachment to the central intent
carries source occurrence time, selected type/outcome, receipt/target/event keys
and affected UserId. Only the validated mirror consumer mints it; no arbitrary
metadata dictionary or private module audit writer is admitted. Extend the
central composer with an explicit one-producer/one-consumer attachment census:
the validated mirror consumer's factory is its sole producer and the central
composer its sole consumer. This is separate from the existing
[authorization evidence populations](../standards/18-audit-coverage.md#evidence-populations)
and trusted transport-provenance minters; it does not claim they already form
one combined numbered census or widen any of their factories.

`audit.provider_event.observe` is MUST/SecurityEvent, declared by Audit's own
catalogue and matrix. It records the mirror, not an Identity mutation; the
request-keyed slug keeps the declaring module prefix. Its actor is SystemActor;
the affected person is a separately named subject reference, never fabricated
human causation. The row timestamp is processing time; provider occurrence and
provider result are distinct typed evidence. Audit success means the mirror
committed, not that a provider LOGIN_ERROR succeeded. The successful row and
completed inbox commit together. Retry/failed/indeterminate execution evidence
may exist separately; one successful mirror per target is the deduplication
promise, not one audit row of every outcome across all retries.

Global receipts/dedup remain for 32 days, exceeding the admitted 30-day maximum
source retention. Older source facts cannot be newly accepted; retirement does
not authorize historical replay or change the activation lower boundary. Tenant
targets are self-contained retained delivery evidence and are not purged while
outbox/delivery/inbox retry or replay can need them. Their production retention
and coordinated purge belong to Phase 11 with the existing transport/Audit
retention work; this baseline does not bound total durable tenant history.

P03-6 adds all receipt, target, source-history, envelope, inbox/delivery and Audit
references to the P03-10 subject-store inventory. P03-6's own migration installs
the affected-UserId/subject-key locator indexes now; P03-10 consumes them rather
than backfilling missing indexing. Facts accepted before later closure remain
historical copies;
new post-closure intake is receipt-only. P03-10 must reconcile pending copies and
immutable Audit linkage before its erasure writer ships. This decision grants
no global erasure, Audit redaction, Platform gate, recovery endpoint or read UI.

## Context

At `834547b8`, development stores provider events but application ingress and the
Audit consumer do not exist. Ordinary outbox/inbox require an admitted real
tenant; the global audit branch is exclusively `identity.user.create`. Existing
global-self discovery is a person-bound operation, not a machine directory.

The pinned Events API exposes stable IDs and saved history, but only offset/time
paging. A Java listener alone also lacks durable delivery. Pull is selected to
reuse the existing store with a visible finite-recovery limitation; rejecting
that limitation reopens this decision for a transactional provider spool.

## Consequences

### Positive

- Useful tenant evidence reuses real outbox/inbox deduplication and central audit.
- Unknown/global events acquire durable dispositions without invented tenants,
  email linking, application User creation or a new global audit discriminator.
- Provider reads and database machine authority are separated from ordinary app
  and BFF credentials; outage does not sit in the login/public request path.

### Negative

- Pull can permanently miss source facts; capture completeness and immediate
  account disable are not promised. Full-window sweeps amplify provider reads.
- A fifth database login, three owner functions, a logical tenant origin and
  source/client provisioning, rotation and recovery checks must be maintained.
- Cross-tenant provider-global activity is intentionally disclosed as a minimized
  fact to the acceptance-time recipients. Hashes/IDs remain linkable personal data.
- Tenant evidence grows until the named Phase 11 retention work. Source history,
  receipt retirement and future DSAR are different retention boundaries.

### Neutral

- No frontend screen, browser token, generic proxy, provider SDK, broker or Hub
  crossing is added. P03-7 logout and P03-9 human/operator authority stay separate.
- Audit viewer UI remains Phase 06. P03-6 qualifies safe operation/health and
  documents tenant-facing meaning; it cannot claim a new accessible screen.
- Existing app request, job, test and CI windows and source MUST audit survive.

## Implementation Notes

The [P03-6 package](../roadmap/phase-03-delivery-log.md#p03-6-provider-event-decision-pass-and-proposed-package-2026-11-11)
owns three implementation steps, each with commit, two fresh independent review
rounds and verified correction commits. Acceptance precedes all executable work.

1. Reconcile Standards 05/11/18/19/20/21, Architecture 13/15/23/31, Identity and
   Audit specs/matrices, glossary, deployment/realm guides and the closed
   role/function/source/origin/evidence inventories. Append affected predecessor
   Amendments, including ADR-0095's twelve-function/setter restatement; G10 and
   only the machine part of G13 link explicit acceptance.
2. Implement qualified storage/owners, source adapter and complete outbox/Audit
   flow under `add-provider-adapter`, `add-integration-event`,
   `add-ef-migration`, `add-audit-coverage` and relevant test skills. Private
   credentials use example templates and explicit fresh/warm provisioning;
   preservation, zero-write rerun, disable and rotation are real controls.
3. Keep real-provider failure and app-role isolation proof, source-bound complete
   CI A/B and original archives distinct from approval/local tests. Open the PR
   after implementation/reviews; do not merge or claim outstanding manual proof.

## Architecture Tests

Add `Provider_Event_Ingress_Is_Authenticated_And_Confined` and
`Provider_Event_Mirror_Is_Attributed_And_Idempotent` as Registered until real
production subjects and enforcement qualify them. Extend existing role/function,
tenant-origin/setter, evidence-producer, payload/outbox/inbox and module-boundary
controls rather than inventing duplicate names for those rules.

- Actual least-privilege provider reads, wrong issuer/realm/credentials, redirects,
  malformed/oversized data, required-type/retention drift, source outage/restart,
  repeated/equal-time/late pages, incomplete sweeps and preserved gap evidence.
- A persistent 257-tenant refusal followed by a valid historical event, sibling
  acceptance and restart; malformed/oversized singleton subdivision, fair retries,
  no raw-body retention and never-complete coverage until resolution. Source
  expiry preserves an irrecoverable gap through problem-record retirement.
- Real login success/error, reset, generic credential/MFA and lockout events;
  unchanged provider credentials/configuration and no fake event attribution.
- All disposition rows, one subject across two tenants/multiple organizations,
  suspended versus revoked history, concurrent closure/Membership changes,
  over-bound fanout, no retroactive routing and provider-supplied tenant spoofing.
- Concurrent same-ID acceptance, changed normalized collision, lost COMMIT ACK,
  stable per-tenant IDs, stalled/stale/expired claim, late completion and actual
  cleanup retention; no enqueue/mark split or new recipient after restart.
- Forced-RLS/real app and ingress roles, owner policy/ACL/call-site mutants,
  inability to read business/User/Membership tables or acquire Platform scope,
  forged/reused witnesses and exact recipient/tenant/EventId/subscription binding.
- Real required subscription and shared inbox; duplicate/reordered delivery,
  sibling independence, audit failure rollback and unknown COMMIT reconciliation.
  Verify provider result/affected subject separately from executing SystemActor.
- Receipt age boundary/retirement, surviving delayed target read, selected-store
  PII locators and raw subject/email/IP/token/error canaries across SQL diagnostic
  settings, application logs, outbox, Audit and retained artifacts.

## References

- [Phase 03](../roadmap/phase-03-identity-admin.md)
- [ADR-0003](0003-tenant-isolation-defense-in-depth.md),
  [ADR-0036](0036-tenant-resolution-trusted-inputs.md),
  [ADR-0040](0040-ambient-unit-of-work.md),
  [ADR-0044](0044-audit-write-path.md)
- [ADR-0046](0046-durable-event-delivery-and-recovery.md),
  [ADR-0057](0057-transactional-event-production-and-completion.md)
- [ADR-0070](0070-owned-tenant-bff-sessions.md),
  [ADR-0072](0072-application-owned-identity-bootstrap.md),
  [ADR-0077](0077-trusted-authorization-evidence.md),
  [ADR-0095](0095-revision-pinned-membership-custom-fields.md)
- [Identity architecture](../architecture/13-identity-and-auth.md),
  [Event architecture](../architecture/15-event-and-outbox.md),
  [Audit architecture](../architecture/31-audit-subsystem.md)
- [Database](../standards/05-database.md),
  [Security](../standards/11-security.md),
  [Audit coverage](../standards/18-audit-coverage.md),
  [Infrastructure](../standards/20-infrastructure-stack.md),
  [Architecture catalogue](../standards/21-architecture-tests-catalogue.md)
- [Development realm guide](../../infra/keycloak/README.md)
- [Pinned Events endpoint and permission](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/RealmAdminResource.java#L804-L896)
- [Pinned event identity and storage](https://github.com/keycloak/keycloak/blob/26.6.2/model/jpa/src/main/java/org/keycloak/events/jpa/JpaEventStoreProvider.java#L127-L153)
- [Pinned pagination](https://github.com/keycloak/keycloak/blob/26.6.2/model/jpa/src/main/java/org/keycloak/events/jpa/JpaEventQuery.java#L144-L157)
- [Pinned event catalogue](https://github.com/keycloak/keycloak/blob/26.6.2/server-spi-private/src/main/java/org/keycloak/events/EventType.java)
- [Pinned listener contract](https://github.com/keycloak/keycloak/blob/26.6.2/server-spi-private/src/main/java/org/keycloak/events/EventListenerProvider.java)
