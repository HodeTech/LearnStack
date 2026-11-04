# ADR-0093: Verified Invitation Acceptance and Durable Email Handoff

## Status

Accepted

**Date:** 2026-11-04
**Deciders:** @cemil

The maintainer selects automatic invitation email, authorizes the verified review
corrections, accepts this revised decision and directs implementation. This record
replaces its unaccepted manual-sharing proposal. The [Phase 03 Status](../roadmap/phase-03-identity-admin.md)
owns implementation and qualification; no runtime behavior is claimed here.

**Bounded successor decisions:**

- [ADR-0003](0003-tenant-isolation-defense-in-depth.md)'s eight-definer closure gains
  the exact offer-entry function and two SMTP custody functions: eleven in total.
  Their named FORCE-RLS owner visibility is not ordinary application isolation.
- [ADR-0042](0042-tenant-provisioning-cross-aggregate-transaction.md)'s one-exception
  clause gains exactly `AcceptInvitationCommandHandler`, writing Invitation and
  Membership inside Identity. Provisioning remains valid; cross-module writes
  remain forbidden. The literal allowlist has exactly those two handlers.
- [ADR-0047](0047-transactional-background-job-runtime.md)'s external-provider
  boundary gains only the named encrypted SMTP custody connector and attributed
  execution receipt below. Ordinary external application DB/tenant access remains
  denied. Unknown effects, retained occupancy and six-attempt rules are unchanged.
- [ADR-0079](0079-bounded-identity-administration.md)'s `tenancy.setting.write`
  expands from `branding.theme` to also cover only `identity.invitation.ttl_days`.
  Existing holders gain that bounded operation; no new pair or automatic grant
  adoption occurs. Version-1 admins without the pair cannot edit the policy.
- [ADR-0036](0036-tenant-resolution-trusted-inputs.md),
  [ADR-0040](0040-ambient-unit-of-work.md),
  [ADR-0053](0053-trusted-public-server-rendering.md),
  [ADR-0054](0054-bounded-public-renderer-admission.md),
  [ADR-0064](0064-tenant-api-authentication-boundary.md),
  [ADR-0070](0070-owned-tenant-bff-sessions.md),
  [ADR-0076](0076-live-scoped-authorization.md) and
  [ADR-0078](0078-native-session-scope-and-self-entry.md) gain only the exact
  invitation scope, policy, native-route and replay consumers named below.

These are new bounded decisions, recorded by dated Amendments linking this
successor and current canonical carriers. They are not false-at-entry corrections
under ADR-0041. Earlier Accepted bodies and metadata remain historical; none is
turned into a whole-record redirect. ADR-0073 receives invitation navigation;
ADR-0072/0074/0077 remain the binding, lock and evidence baselines. No historical
numeric census is rewritten merely because this packet adds a later consumer.

## Decision Drivers

- P03-4 owes real new/existing-user acceptance, verified current email, fresh
  delegation, revocation/reissue, usable delivery and a reconciled grant outcome.
- The maintainer chooses automatic email in this packet. Full Phase 08a templates,
  preferences and notification orchestration are not prerequisites for its minimum
  invitation sender; the earlier manual-only recommendation is not selected.
- Existing native scope requires Membership. Its three global-self descriptors
  cannot authorize first-Membership acceptance, and token email is not current.
- One ambient transaction supplies no unrecorded two-root exception. SMTP supplies
  neither exactly-once delivery nor a reliable lookup after an uncertain send.
- Secrets in a queued email need durable private custody. Identifier-only job
  parameters/plans/outcomes and the external ordinary-DB denial must remain true.

## Considered Options

1. **Automatic SMTP with typed jobs and private encrypted custody** (selected).
   Send and track invitations now, preserving explicit uncertain outcomes.
2. **Administrator-only link sharing** (not selected by the maintainer). Smaller
   delivery scope, but does not satisfy the selected automatic-email experience.
3. **Send during the API/business transaction** (rejected). External delay and
   uncertainty cannot share the database's atomic commit or safely retry it.
4. **Place email/token/ciphertext in job plans or replay responses** (rejected).
   Breaks identifier-only contracts or retains a second uncontrolled credential.
5. **Eventual Membership after invitation consumption** (rejected). Cannot supply
   the required single consumption/grant outcome.
6. **New invitation-policy permission/release** (not selected). Adds unnecessary
   grant migration for one tenant setting. Explicit existing-key expansion is
   chosen and tested; it is not described as an unchanged permission meaning.
7. **Unconditional exclusive Tenant lock** (not selected). Preserve ADR-0074's
   strongest actual-effect plan; do not serialize every invitation unnecessarily.

## Decision

LearnStack delivers automatic invitation email, verified-recipient acceptance and
truthful recovery in P03-4. A narrowly admitted offer establishes only its exact
nonmember scope. Fresh provider email and current inviter delegation precede one
atomic Invitation/Membership result. Typed external jobs send an encrypted,
version-bound email through a closed SMTP provider store, preserving uncertain
submission and retained reservations. Acceptance itself publishes no session;
normal explicit API admission and account continuity still govern publication.

## Context

### Offer and recipient

Invitation is an Identity tenant/organization-scoped root with typed UUIDv7 ID,
immutable normalized recipient email, inviter UserId, nullable organization, one
compatible RoleId and pinned Role version. No invitee UserId is supplied by an
administrator. Email is a recipient condition, never an account-merge key.

Use ASCII dot-atom local part and DNS domain, at most 254 bytes, with no display
name, quoted local part, comment, whitespace or control. Lowercase ASCII across
that address; perform no alias/dot/plus stripping or Unicode compatibility
folding. This is a deliberate matching policy, not universal SMTP case semantics.
The provider address uses the same parser. Provider account uniqueness remains
separate and is neither changed nor inferred from invitation matching.

Tenant-capability Role requires NULL organization; Organization-capability Role
requires non-NULL target organization. The same-key Tenant-to-Organization
**delegation** bridge never permits assigning an incompatible Role template.
Current whole grants must fit the stored inviter's live ceiling. A changed pinned
Role requires revocation/new offer; reissue does not silently repin it.

### Lifecycle, lifetime and resend

| State | Authoritative behavior |
|---|---|
| Pending, unexpired | Inspect; explicitly accept; revoke; or explicitly resend by reissue. |
| Pending, expired | Reject acceptance; show expiry; an authorized reissue renews it. |
| Accepted | Retain actor, exact result MembershipId and `created`/`already_member` disposition. No reissue, revocation or second grant. |
| Revoked | Terminal. A replacement offer has a new ID. |

Create commits generation 1, its verifier, frozen expiry, queued delivery custody
and job together. There is no undated `not_prepared` state. One Pending offer per
null-safe tenant/org/email tuple includes expired offers awaiting reissue. Duplicate
creation is an authorized conflict, never a hidden resend.

The Tenancy-owned `identity.invitation.ttl_days` setting defaults to 14 days and
allows 1–30. Only orgless Tenant `tenancy.setting.write` can edit it. Existing v2
holders gain this operation; v1 without that pair still creates invitations using
14 days and may explicitly adopt v2 through the existing accepted procedure.
Registration/seed repetition neither grants nor restores the pair. A new value
affects future generations only. The absent/default precondition is distinct from
an existing version-zero setting; first creation uses presence plus version and
natural uniqueness, not an unconditional upsert.

**Resend means explicit reissue:** create a new generation, secret, expiry,
encrypted message and job; atomically invalidate the old acceptance verifier.
It requires current authority/delegation, pinned Role and canonical If-Match plus
protected idempotency. Repeat of the same key returns its version receipt rather
than queuing twice. No API returns the raw link. Copying links is not the selected
administrator delivery workflow.

Revocation, acceptance and reissue invalidate obsolete acceptance authority in
their ordinary transaction. Prepare refuses an obsolete generation observed
before its plan commits. There is no ordinary provider-table cancellation writer:
a message already prepared or admitted to SMTP can still arrive with an invalid
link. Neither a new generation nor an expired lease releases an uncertain older
delivery reservation. Provider recovery cancellation below fences an unclaimed
external attempt; it is distinct from business withdrawal.

### Automatic email and private custody

Identity is the first production job consumer. It registers two external-profile
keys, `identity.invitation_email_tenant.v1` and
`identity.invitation_email_organization.v1`, for exact TenantWide/Organization
routes. Existing external queue/pool counts stay fixed. The shared tenant-wide
`identity.invitation_email` capacity group has four retained slots; uncertainty
can fill them and delay that tenant's later mail.

Creation/reissue uses `IInvitationEmailStager` and `IBackgroundJobScheduler` on
its current announced writable owner. The durable job, Invitation generation and
private envelope commit together. The provider spool is framework/provider
control storage, not another business root or a generic notification module.
`invitation_email_envelopes` and `invitation_email_attempt_receipts` are owned by
common Infrastructure. Their custody/receipt changes have their own explicit
capture exclusions and no fabricated business snapshots.

Encrypt the bounded recipient/subject/body/link message using AES-256-GCM with a
fresh 12-byte nonce, 16-byte tag and immutable scope/generation/spool/job identity
as authenticated data. Plaintext is at most 32 KiB. A named keyring of 1–4 keys
comes only from `ISecretProvider`; ciphertext identifies its key. Active key
changes do not discard keys required by outstanding ciphertext or restoration.
Missing keys refuse; they never regenerate links or silently select another key.
The setup/rotation/restore runbook and key-loss controls ship in this packet.

Parameters and plan carry only immutable GUID Invitation/generation/spool IDs;
outcome carries only a provider ReceiptId. Generation ordinals, enums, email,
URLs, ciphertext, keys and exceptions never enter jobs/outbox/inbox/vendor state.
Prepare/finalize are exact audited Identity requests. Finalization changes only
the still-current Invitation generation; stale-generation completion is a truthful
no-op with its retained provider evidence, not a second send or status overwrite.

The Identity Contracts port `IInvitationEmailOperation` retains the existing
Execute/Reconcile shape; `InvitationSmtpOperation` uses shared `IEmailProvider`.
The SMTP adapter pins MailKit/MimeKit 4.18.2. Provider configuration fixes host,
port, sender and required TLS; credentials use the secret provider. Only explicit
Development Mailpit configuration permits unauthenticated plaintext SMTP. Request
or tenant-authored content supplies no endpoint, TLS downgrade or headers. Render
bounded localized en/tr text and HTML with escaped tenant labels; the full Phase
08a template/preference engine later integrates this boundary.

SMTP resilience disables automatic send retry. Only the typed job outcome policy
can admit another attempt; no nested Polly retry repeats an uncertain send.
A stable Message-ID identifies the same logical operation, without claiming
SMTP deduplication. Final SMTP 250 means **submission accepted**, not inbox delivery.
[RFC 5321's final-reply timeout rule](https://www.rfc-editor.org/rfc/rfc5321.html#section-4.5.3.2.6)
explains why a missing final response cannot establish failure or justify resend.

### Confined external provider source

Ordinary external UoW/DbContext/repository/ISender/tenant-setter access remains
refused. `JobExecutor.InvokeExternalAsync` alone mints a nonserialized immutable
`InvitationSmtpExecutionReceipt` from the exact registered definition/adapter,
validated route, job/attempt/operation, immutable plan fingerprint, current fence,
deadline and Execute/Reconcile purpose. Plain DTO IDs are not this authority.
The receipt is invocation-bound, closes new acquisitions on invalidation and
retains already-owned work through actual cleanup.

Only a dedicated provider connector consumes it, on a separate app-role source
with four retained slots including checkout/cleanup and five-second SQL operation
budgets. No migration credential, fifth login role, ordinary UoW, raw connection
escape, general callback or tenant GUC writer is added. A timed-out physical owner
keeps its slot until disposal; lost COMMIT has bounded exact reconciliation.

Two fixed migration-owned SECURITY DEFINER functions, on this connector only,
validate actual job/attempt/plan/ExecuteIntent/spool bindings and their narrow
role-qualified owner visibility before accessing provider storage:

- `fn_invitation_smtp_claim_or_read`: Execute may atomically claim its exact
  Created attempt as Sending before **any** socket starts. Reconcile reads only
  immutable evidence; fresh eligible recovery may durably fence Created as
  CancelledBeforeSend, preventing a paused former Execute. Reconcile never sends.
- `fn_invitation_smtp_complete_attempt`: write-once terminal observation for an
  already-issued exact provider claim, including reproducible late evidence
  after lease loss. It neither starts work, changes Invitation/business state,
  releases job occupancy nor bypasses the runtime's checkpoint authority.

Functions return encrypted material only to the winning original claim, or
bounded receipts. No Invitation/User/Membership directory or plaintext projection
exists. Standard 05 owns the exact SQL signatures, static bodies/search paths,
policy arms, source/fence/grant/caller census and known/unknown commit rules.
This is an explicitly enumerated provider-storage exception to ADR-0047's source
boundary, not permission for other external adapters to access application data.

| Proven SMTP/provider observation | Runtime outcome and UI |
|---|---|
| Final 250, actual send/socket cleanup and durable receipt | Succeeded; submission accepted, never delivered/read. |
| Definite temporary rejection/no acceptance, actual cleanup and durable receipt | Failed/TransientRepeatable; bounded existing retry, same logical operation/generation. |
| Definite permanent rejection, cleanup and durable receipt | Failed/Permanent; visible failed result/recovery. |
| Durable cancellation before any socket, former start fenced | Cancelled/TransientRepeatable with positive provider evidence. This is not framework NeverStarted. |
| Lost final reply, crash after Sending before receipt, unsettled I/O or contradictory evidence | Unknown; visible uncertain/quarantined state, retained reservation, no blind retry. |
| Exact durable terminal receipt found after restart | Reproduce it; never execute the completed operation again. |

UI separates queued, submission accepted, failed, uncertain, expired and revoked.
Known failure offers authorized reissue; uncertainty explains possible late old
mail and the retained-capacity limit. It does not claim that reissue reconciles the
old effect. No evidence can manufacture a missing SMTP response after an
unobservable crash. P03-9 owns privileged recovery; this packet supplies the
provider-specific evidence/runbook, not a bypass or an invented release proof.

### Current provider email and verification

`ICurrentInvitationRecipientReader` calls the configured issuer's fixed UserInfo
endpoint with this request's stock-validated access token. No supplied URL,
redirect, realm-admin credential, User email mirror or account search is added.
For every Pending inspection and first acceptance, independently require the same
exact `sub`, one typed `email`, Boolean `email_verified == true` and mailbox match.
Reject duplicate/ill-typed claims. Bound response to 16 KiB and provider I/O to
five seconds within the existing ten-second API deadline; no business locks or
retry extending that deadline. Evidence binds request/issuer/subject/token and
expires with that invocation. Provider and database observations are not atomic.

Show the complete escaped, bidirectionally isolated verified address, not a
possibly colliding display label. The later accept POST makes its own observation;
inspection is no authorization cache. Wrong or unverified email yields bounded
recovery without revealing the expected address.

New people use existing opt-in global-self bootstrap, which commits/disposes
before tenant acceptance. It keeps the deployment budgets of 10 attempts/minute,
100/day and 10,000 lifetime allocations; disabled/exhausted creation cannot be
bypassed by an invitation. Account binding stays authority plus exact subject.

The fixed `kc_action=VERIFY_EMAIL` purpose reuses state/nonce/PKCE and exact callback
under the original invitation/subject. Cancellation or a different subject never
verifies or replaces that continuation. Realm-wide `verifyEmail=false` can remain;
qualification explicitly verifies that the action is enabled/supported and its
send/cooldown UI works. Existing Mailpit handles development verification email;
production requires configured Keycloak SMTP. No new LearnStack password/reset or
verification sender replaces Keycloak. The pinned
[UserInfo endpoint](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/protocol/oidc/endpoints/UserInfoEndpoint.java),
[property mapper](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/protocol/oidc/mappers/UserPropertyMapper.java)
and [VerifyEmail action](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/authentication/requiredactions/VerifyEmail.java)
support this choice; actual changed-email/nonseed verification proof remains due.

### Offer entry, secrets and native ownership

Email contains a stored, live host-mapping-derived HTTPS origin and fixed localized
landing with `#<invitation-id>.<secret>`. The origin is server-derived/frozen for
that generation and independently revalidated; no caller-supplied URL, scheme,
port or return destination is accepted. No secret enters path, query, OIDC state,
returnTo, callback or another host's URL. A separate code enables manual entry.

**Browser GET renders only navigation/inspection UI.** Secret-carrying API
inspection/result operations are POST with strict bodies, explicitly physically
ReadOnly and Off where self-only. This clarifies the earlier phase/Architecture
13 GET wording; no GET accepts, consumes, queues or grants an invitation.

The native landing runs before Next/RSC, with no third-party scripts, analytics or
error reporter. Its first bounded script scrubs the fragment then captures through
exact-Origin/CSRF POST. Without JavaScript the clean form accepts a pasted code.
GET allocates the bounded preauthentication continuation/CSRF/browser binding;
capture verifies that exact host/browser proof and never consumes the offer.
Clean confirmation forms use existing exact-Origin-compatible same-origin
referrer policy, not an unqualified no-referrer change.

One continuation per host/browser group uses the existing 1,024 total/128 pending/
16 per-source/five-minute limits. After verified issuer/subject association it
also counts inside the existing eight-owner subject ceiling. It has a separate
opaque `__Host-ls-invitation` cookie, HttpOnly/Secure/Path=/, SameSite=Lax. It grants
no session. Replacing a continuation affects only that invitation owner, preserving
live sessions. Epoch/late-publication/cleanup rules persist. Five-minute expiry or
restart asks the person to reopen the original email; this cannot consume a second
grant. Actual cleanup controls retention, not a timeout or cleared cookie.

A 32-byte CSPRNG secret has one domain-separated verifier. Hash unsigned-32-bit
big-endian length-prefixed UTF-8 `learnstack.identity.invitation.v1`, RFC UUID
bytes and raw random bytes with SHA-256. The API computes it before SQL; database
`verifier_digest` input is exactly those 32 digest bytes, never the raw secret.
Application-held secret/digest comparisons use fixed-time APIs where applicable;
indexed SQL digest equality is **not claimed constant-time**. High entropy and
masked outcomes remain separate controls. The consumed digest is the accepted
generation's verifier hash, retained only for exact same-actor reconciliation.
Reissue replaces the active verifier; revoke clears it; expiry disables it by
predicate; Accepted moves it to consumed-verifier custody. No old verifier grants
new admission. Sensitive digests/SQL parameters stay out of server error/statement
logs and provider diagnostics; the packet includes DB bind-log configuration and
actual leakage controls rather than assuming application redaction covers them.

After peer/body/host, provider, issuer/sub quota and native-source checks, a
separate exact invitation branch normalizes the existing Active binding and calls
`fn_identity_invitation_entry` with ID/digest, independently classified host tenant
and installed authority/subject key. It returns only offer tuple/version/generation,
bound actor admission, stored inviter global admission and result identifiers.
The inviter Boolean is not delegation proof. No email/profile/grants/arbitrary
User lookup is returned. The short lookup closes before the scoped owner starts.

`InvitationEntryScope` is the eleventh logical setter: one operation-bound witness
on ReadOnly inspection/result or ordinary ReadWrite acceptance owners. This is
not classified as a ninth short-ReadOnly-only setter. Both reuse the four existing
physical context writers. The one-use witness binds exact request, credential,
subject/actor, native source, real host, stored offer and mode. The new
`HostAndInvitationOffer` origin admits only `InspectInvitationQuery`,
`AcceptInvitationCommand` and `GetOwnInvitationResultQuery`. Extend enum/factory,
minter/resolver, middleware/policy/source and caller tests together. No AccountSite,
directory, public write, Platform/unknown-host or general protected operation is
admitted. TenantHost caps the tenant; OrganizationHost requires exact organization.
Provider scope/assertions must agree and never fill or override missing authority.

### Atomic acceptance, current authority and audit

Use existing ordered locks: complete sorted exclusive invitee/inviter User keys,
then the strongest actual Tenant mode, offered Role, Invitation, and existing
Membership row. Shared covers nonadministrator effects, including create/revoke/
reissue and ordinary-member acceptance; actual Tenant Admin eligibility/assignment
changes select Exclusive. Derive the full plan from prepared OLD/NEW effects and
revalidate after waiting; changed classification conflicts rather than upgrades.
Current delegation has ADR-0076's committed per-act snapshot boundary, not a
promise to cancel work already admitted before a later revocation.

Fresh post-wait state proves both User accounts Active, including the existing
Active-member case without a Membership INSERT. The offer-derived function
rechecks only those exact bound/global facts; `IInvitationDelegationReader`
separately reads current stored-inviter Membership/Role/grant facts on the same
scope. No invented HTTP candidate, seed/SystemActor flag or `was_admin` authorizes
it. Trial/Active tenant and Active/nondeleted target organization admit ordinary
work through `IIdentityScopeLifecycleReader`; Suspended/Archived/deleted scope
refuses. NULL organization requires eligible tenant work, without a fake branch.

| Exact target Membership | Initial acceptance |
|---|---|
| None | Create one Active Membership/Profile/assignment; consume offer. |
| Active | Consume as `already_member`; explicitly tell both parties existing Roles/profile are unchanged. |
| Suspended | Conflict; no reactivation/consumption. Use the existing status operation. |
| Only Revoked history | Create one fresh successor; never resurrect a predecessor. |

Exactly one Invitation and at most one Membership are written by
`AcceptInvitationCommandHandler`. Central plural MUST
`identity.invitation.accept` and `identity.membership.admit_invitation` commit on
that owner with actual subject IDs. **Disposition and result MembershipId live on
the tracked Invitation**, so its captured after-state/diff provides created versus
already_member evidence. The unchanged Membership event is subject-only, with
NULL snapshots/diff; it does not claim a disposition metadata field or a fabricated
creation. Designating an unchanged subject is supported independently of the
multiple-instances-of-one-type rule. No generic audit-result channel is added.
Natural uniqueness, global admission and last-admin guards remain; MUST failure
rolls back consumption and grant together.

Predispatch Accepted goes to exact Off `GetOwnInvitationResultQuery`, not mutation
intents. It requires consumed digest, same currently Active actor and original
current Active Membership, before Pending-only version/expiry/email/inviter/Role
conditions. Suspended/Revoked/deleted/or successor original Membership gives masked
404 and no new grant. If a Pending command sees Accepted after lock wait, return
recoverable conflict with truthful failed-attempt audit; a fresh result read can
reconcile. Unknown COMMIT stays unknown until authoritative observation. It never
repeats a successful consumption or substitutes a successor.

Only Pending checks expected version, current generation, post-wait database
expiry, Role pin and fresh email/delegation before its conditional transition.
Accept/accept, accept/revoke, expiry and reissue races have one locked winner.
No global/tenant owner joins or retargets; no native session follows merely from
this result. Explicit API scope admission/AccountSite and same-subject continuity
still govern publication. Accepted-but-no-session recovery preserves the grant.

### Finite transport, privacy and completion

The [revised phase package](../roadmap/phase-03-delivery-log.md#p03-4-automatic-email-review-corrections-and-accepted-plan-2026-11-04)
owns exact routes/steps; Standards 04/05/07/11/18/19 own their canonical DTO,
precondition, source, permission, audit and storage contracts. Reuse applicable
membership-admin and exact scope. Invitable-Role summaries expose only currently
delegable ID/key/scope/version, not general Role-admin/grant configuration.

Create/revoke/reissue/policy writes use bounded durable protected receipts, strong
preconditions as applicable and fresh target disclosure. Add Invitation and exact
policy TenantSetting receipt kinds; store no raw secret or message. Entry accepts
strict body-held code/version and rejects a generic Idempotency-Key; offer/actor/
consumed-digest result convergence is its natural key. Missing/malformed/weak/
wildcard/list required If-Match is 400 validation_failed; stale canonical version
is 409 concurrency_conflict. Wrong-purpose/foreign/invalid/expired/revoked/replaced
link is masked 404 with usable generic reopen/contact-admin recovery. Matching
unverified/mismatched email is bounded 403; no expected-email disclosure. Existing
quota 429 and unavailable/indeterminate 503 remain. Standard 09 owns the exact
finite additional error keys before writer activation; no proposed 428 applies.

Classify recipient, inviter/acceptor/Membership linkage, Role pin, verifier,
consumed verifier, ciphertext/key IDs, provider receipts/job linkage and volatile
continuations in canonical Data Protection. Snapshot markers and real per-sink
canaries cover application/Sentry/archive/traces, database bind logs and SMTP
instrumentation. No credential/email/message leaks into diagnostic/test artifacts,
audit snapshots, validation attempted values or identifier-only events. Authorized
recipient email/relationship DSAR remains P03-10; credentials are not exported.
Phase 11 owns retained-copy/purge extensions, without implying this packet already
erases or anonymizes those stores. Current actor closure still fences acceptance.

## Consequences

### Positive

- Administrators send and track invitations inside Studio; a real new verified
  person joins the exact institution/branch through a complete recovery path.
- Current provider email, fresh delegation, one locked consumption/grant and
  central audit preserve identity/scope without self-escalation or email merging.
- Durable encrypted custody and provider evidence survive ordinary restart while
  generic job payloads, external DB denials and truthful delivery status remain.

### Negative

- SMTP/keyring/TLS/sender setup, two private tables, two provider functions, a
  dedicated four-slot source and a narrow external execution-receipt seam add
  maintenance. Total confined definers become eleven; the scope setter count is
  eleven and physical context writers remain four.
- SMTP response loss/crash can quarantine delivery and occupy all four tenant
  slots until positive recovery evidence. No exactly-once/inbox guarantee exists.
  Reissue does not free unknown old work; old invalid messages can arrive later.
- Existing setting-write holders gain the invitation policy act. Without v2
  adoption, administrators can use the default but cannot change it.
- New-user creation remains opt-in, 10 attempts/minute, 100/day and 10,000 lifetime
  allocations per authority. Verification depends on SMTP/provider availability;
  five-minute local/provider continuations may require reopening the email.
- Provider and DB state have separate observation points; later email/delegation
  changes cannot retroactively cancel an already admitted operation.

### Neutral

- This packet brings forward only invitation email. Phase 08a owns the full engine;
  no new notification module, tenant template editor or generic sender surface.
- Existing job attempts, worker counts, owner quotas, API/shutdown/CI/test windows,
  Role grant manifests, global-self descriptors and four login roles stay fixed.
- Acceptance, implementation, real qualification and merge remain distinct.

## Implementation Notes

Before runtime, reconcile current Standards 01/04/05/07/08/09/10/11/13/16/18/19/20/21
where applicable, Identity/Tenancy module specs/diagrams/matrices/providers,
Architecture 13 invitation diagram/23 inventory/15 job handoff, glossary and G8.
Use dated Amendments in ADR-0003/0036/0040/0042/0047/0053/0054/0064/0070/0073/0076/
0078/0079, each naming this successor and exact changed current carriers. No
false-at-entry erratum or historical Decision rewrite is selected. Preserve
ADR-0033/0044 durability/composer behavior and ADR-0072/0074/0077 baseline semantics.

The seven-step package assigns exact provider/caller/source/ACL/precondition/
privacy/route/error controls before activation. Every step commits, runs two
fresh independent review rounds, fixes verified findings in new commits and
continues. The canonical aggregate rule becomes
`Cross_Aggregate_Writes_Are_Confined_To_Named_Exceptions`, with exactly
ProvisionTenantCommandHandler and AcceptInvitationCommandHandler; retain a
navigation alias for its historical name, not duplicate rule ownership.

## Architecture tests

Extend real nonempty subjects in existing aggregate/context/setter/SQL/permission/
privacy/audit/native/job/isolation rules. New source exceptions must be executable
closed lists, not comments. Preserve current rule status until actual tests land.

Mandatory behavior:

- Real app-role tenant/two-branch RLS/FK/uniqueness; exact eleven-function body/ACL/
  owner-policy set, wrong-purpose/mode/host/subject, scope cleanup and pool reuse.
- Provider signup and actual verification email, current UserInfo change/unverified/
  wrong subject/duplicate fields/outage; actual committed realm registration,
  email-edit/username/duplicate-email settings, not presumed account edits.
- Strongest lock-mode dirty/clean controls, after-wait inviter revocation/Role pins,
  Trial/Active positive and Suspended/Archived/deleted scope refusal; actual closure
  before the User fence even for already_member; no compatible-Role shortcut.
- Both commit orders for accept/revoke/expiry/reissue, Active unchanged-role audit,
  fresh successor, plural MUST rollback and original-version unknown-COMMIT retry.
- Real SMTP acceptance/rejection, receive-once normal path, deterministic same
  operation retry only after known no-effect, pre-send fencing, final-250-before-
  checkpoint restart, lost-reply/crash Unknown with retained occupancy, stale
  generation finalization, key rotation/loss/restore and positive leakage controls.
- Native HTTPS/new/existing/different-account, no-JavaScript code entry, clean
  token URL/history/BFCache, verification cancellation/expiry/restart, exact subject
  owner ceiling and accepted-but-no-session recovery; actual human accessibility.

Final same-source required CI/A/B/original-archive proof remains mandatory. No
local fixture, old P03-3 follow-up or maintainer merge is substituted for it.

## References

- [Phase 03/G8](../roadmap/phase-03-identity-admin.md#invitation-flow),
  [Identity](../modules/identity/README.md), [permissions](../modules/identity/permissions.md),
  [audit](../modules/identity/audit.md)
- [ADR-0072](0072-application-owned-identity-bootstrap.md),
  [ADR-0073](0073-scoped-membership-and-identity-data-foundation.md),
  [ADR-0074](0074-membership-admission-under-ordered-locks.md),
  [ADR-0077](0077-trusted-authorization-evidence.md)
- [ADR-0033](0033-audit-durability-model.md), [ADR-0044](0044-audit-write-path.md),
  [ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md)
- [Identity architecture](../architecture/13-identity-and-auth.md#invitations),
  [Data Protection](../architecture/23-data-protection.md),
  [Phase 08a](../roadmap/phase-08a-assessment-notifications.md#notifications)
- [Database](../standards/05-database.md), [Security](../standards/11-security.md),
  [Audit](../standards/18-audit-coverage.md), [Catalogue](../standards/21-architecture-tests-catalogue.md)
- [MailKit 4.18.2](https://www.nuget.org/packages/MailKit/4.18.2),
  [MimeKit 4.18.2](https://www.nuget.org/packages/MimeKit/4.18.2)


## Amendments

### 2026-11-04 — Acceptance version transport clarification

The finite-transport paragraph combines “body-held code/version” with required
canonical If-Match. It does not name a duplicate-version reconciliation rule.
For the Step 5 API and Step 6 native forms, observed acceptance version has one
carrier: canonical strong **If-Match**. All three entry JSON bodies contain only
Invitation ID and code; a duplicate body version is not admitted. Inspection and
result reject conditional headers. This concretizes the already-required strong
precondition rather than claiming a false-at-entry runtime defect.

Pending acceptance validates that header and returns 400 for missing/malformed
required preconditions or 409 for a stale canonical version. Predispatch Accepted
still selects the exact own-result read before Pending-only preconditions,
provider work or mutation intents; an obsolete original tag cannot block that
reconciliation. Generic Idempotency-Key remains refused.

[Standard 04](../standards/04-api-design.md#verified-invitation-transport) owns the
current wire contract. Its implementation carriers are the finite API
`InvitationEntryHttpInput` and `InvitationEntryController`, the served OpenAPI
snapshot and SDK entry operations, and the native confirmation forms as their
owning steps land. The phase Status block owns delivery and qualification.
The historical body is preserved. The Decision, authority, lifecycle, lock,
audit, provider and execution-limit choices are unchanged.
