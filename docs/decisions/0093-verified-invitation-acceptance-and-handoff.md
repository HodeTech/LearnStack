# ADR-0093: Verified Invitation Acceptance and Bounded Handoff

## Status

Proposed

**Date:** 2026-11-04
**Deciders:** @cemil (approval pending)

This proposal closes P03-4/G8 only on explicit acceptance. No Invitation schema,
provider adapter, scope authority or endpoint is implemented by this record.
The [Phase 03 Status](../roadmap/phase-03-identity-admin.md) owns delivery state.

**Bounded supersession on acceptance:** ADR-0003's eight-definer closure gains
only the exact offer-entry function and Invitation owner visibility described
below; ordinary application RLS and the four-role model remain. ADR-0042's
one-operation exception list
gains exactly `AcceptInvitationCommandHandler`, writing Identity's Invitation
and Membership roots together. ADR-0042 remains Accepted for provisioning;
cross-module writes remain forbidden. ADR-0036/0040/0064/0070/0076/0078/0079 retain
their ordinary paths; their closed entry, setter, policy, native-operation and
receipt censuses gain only the invitation consumers explicitly named here.
Accepted bodies remain historical; acceptance appends dated disclosures.

## Decision Drivers

- P03-4 owes a usable new/existing-user invitation journey, current verified
  email, fresh delegation, revocation/reissue and one authoritative grant outcome.
- Current native scope admission requires Membership. The three global-self
  descriptors cannot admit a person who is accepting their first Membership.
- Provider tokens and the global User model deliberately provide no current
  email mirror. An old token claim cannot satisfy G8's email-change boundary.
- An ambient transaction alone licenses neither a second aggregate write nor a
  new tenant setter. Existing protected receipts cannot safely retain a raw link.
- Full notification orchestration belongs to Phase 08a. G8 explicitly permits
  administrator handoff now; the UI must describe what actually happened.

## Considered Options

1. **Verified acceptance with explicit administrator link handoff** (selected
   proposal). Deliver the complete offer/accept/recovery journey without an
   automatic invitation sender. Keycloak still owns email verification.
2. **Bring forward automatic invitation email** (viable, not selected). Requires
   the minimum provider adapter, durable private delivery custody, typed jobs,
   effect reconciliation and retry/status UX now. It remains a product choice
   for the maintainer, not an assumed prerequisite or a fake sent result.
3. **Eventual Membership creation after consumption** (rejected). Leaves a
   consumed offer without its grant, or a grant whose offer can still be revoked.
4. **Trust token email or reuse seed/unresolved scope** (rejected). Supplies
   neither current provider evidence nor legitimate pre-Membership authority.
5. **Retain recoverable raw links in replay storage** (rejected). A hashed offer
   does not protect a plaintext copy in an idempotency receipt or diagnostic.

## Decision

LearnStack implements tenant/organization-bound, verified-recipient invitations
with explicit administrator link handoff. A confined nonmember entry derives
scope from the exact stored offer and independently verified host, checks current
provider email and stored-inviter delegation, and atomically consumes the offer
with its Membership outcome. Membership creation grants no native session;
existing explicit API admission and account-continuity checks still precede
session publication.

## Context

### Offer, recipient and lifecycle

`Invitation` is an Identity-owned tenant/organization-scoped aggregate with a
typed UUIDv7 ID, immutable target email, inviter UserId, nullable organization,
one compatible RoleId and offered Role version. One Role keeps the offer clear;
later multi-Role administration already has its separate protected operation.
No caller supplies an invitee UserId. Email identifies the offer's recipient,
never a global binding or an account-merge target.

The accepted mailbox grammar is a bounded ASCII dot-atom local part and DNS
domain, at most 254 bytes, without display names, comments, quoted local parts,
whitespace or controls. Comparison lowercases ASCII in the complete address;
it performs no dot/plus stripping, alias lookup or Unicode compatibility folding.
This is a deliberate case-insensitive mailbox matching policy, not proof that
every SMTP server treats case variants alike. Unsupported addresses receive a
localized validation error before offer creation; provider addresses use the
same parser. Keycloak realm account uniqueness remains a provider responsibility.

| State | Permitted outcome |
|---|---|
| Pending, no link prepared | Administrator can prepare a link or revoke; acceptance refuses. |
| Pending, unexpired current verifier | Inspect; explicit acceptance; revoke; explicit link replacement. |
| Pending, expired | Display Expired from database time; acceptance refuses; authorized replacement renews the offer. |
| Accepted | Immutable actor, original MembershipId and `created`/`already_member` result; no reissue, revoke or second grant. |
| Revoked | Terminal; no reissue or acceptance. A new offer requires a new ID. |

Expiry is an effective state from `expires_at`, not a periodic job that must run
before refusal. Evaluate it after lock waits using database UTC. A Tenancy-owned
typed setting supplies a tenant default of 14 days, configurable to 1–30 days by
an orgless administrator with Tenant `tenancy.setting.write`. Each prepared link
freezes its expiry; later policy edits do not extend it. Replacement starts a new
generation and expiry from the current policy. Email/Role changes require revoke
and a new offer; replacement cannot silently repin a changed Role version.

Null-safe uniqueness permits one Pending offer per tenant, organization and
normalized email, including an expired Pending offer awaiting replacement.
An authorized duplicate create reports conflict and points to the existing offer;
it never reissues secretly. Historical Accepted/Revoked offers remain distinct.

### Handoff and secret custody

Creation persists the offer and its explicit `not_prepared` handoff fact, returning
the ordinary bounded version receipt and Location. A separate deliberate
`PrepareInvitationHandoffCommand` generates 32 CSPRNG bytes, stores only a
domain-separated SHA-256 verifier and generation, and returns the raw code once.
Preparing again atomically invalidates the former generation. No deterministic
token reconstruction, plaintext database copy or encrypted delivery store exists.
The verifier is sensitive credential material despite its one-way form.
Its canonical input is unsigned-32-bit-big-endian length-prefixed UTF-8
`learnstack.identity.invitation.v1`, Invitation UUID RFC/network-order bytes and
the 32 raw random bytes, hashed with SHA-256. Constant-time fixed-length equality
checks the stored value; tenant, exact ID and current generation remain separate
mandatory predicates. Generation advances only on explicit preparation and
cannot wrap or decrement.

The handoff response is no-store and excluded from generic/protected idempotency
response storage, RSC, page-snapshot caches, analytics and diagnostics. It requires
canonical strong If-Match and rejects Idempotency-Key. It performs no external
send. A lost response is **link unavailable**, not a claimed rollback: read the
new version and explicitly replace the link. A late former-version request cannot
replace a newer link. No automatic mutation retry is introduced.

The administrator receives a selectable link, Copy control and separate code
fallback. Labels say **link prepared**, **link replaced**, **not yet shared**;
clipboard success is not proof of sending or receipt. The administrator chooses
the communication channel. Automatic email/templates/preferences remain Phase
08a work; this handoff is the delivered P03-4 mechanism, not an unfinished sender.

The link uses the stored, independently admitted HTTPS entry origin and fixed
localized landing path, with `#<invitation-id>.<secret>` in its fragment. No token
appears in path, query, OIDC state, callback, returnTo or another host's URL.
The native landing is a small owned HTML response, before Next/RSC, with no
third-party scripts, analytics or error reporter. Its sole bounded script first
removes the fragment with history replacement, then captures it through an
exact-Origin/CSRF POST. Without JavaScript the same page supports pasting the
separate code. GET creates no offer, Membership, consumption or delivery fact.

Captured codes live only in the existing bounded native owner infrastructure:
one invitation continuation per host/browser group, counted within existing
1,024 owners / 128 pending / 16 per-source limits and five-minute pending
lifetime. No new registry or capacity allowance is inferred. Its separate opaque
HttpOnly/Secure/SameSite host cookie binds the continuation; provider credentials
remain in their existing entry owner. Opening another invite replaces only the
earlier invitation continuation, never a live session. Epoch, publication and
actual cleanup rules remain. Expiry/restart/lost custody asks the person to reopen
the original link. Logout, cancellation and completion clear contained secrets;
actual unsettled work retains capacity until cleanup ends.

The clean confirmation document keeps exact-Origin-compatible `same-origin`
referrer policy for no-JavaScript forms; the token-bearing fragment is already
removed. Callback/logout retain their existing policy. Browser history, BFCache,
late responses, canary leakage and owner teardown require actual browser proof.

### Current verified provider email

API Infrastructure supplies `ICurrentInvitationRecipientReader`, used only by
the exact invitation inspection/acceptance admission. It calls the configured
issuer's fixed `/protocol/openid-connect/userinfo` endpoint with this request's
stock-validated access token. No realm-admin credential, user search, supplied
URL, redirect, provider SDK in Application or broad account-management scope is
added. The existing `openid email` scope/mappers must be qualified.

Each Pending inspection and first acceptance performs a new no-cache call.
Require exact `sub` equality,
one well-typed `email`, Boolean `email_verified == true`, and the mailbox match.
Duplicate/ill-typed properties, wrong subject, token refusal, missing claims or
malformed response never become trusted evidence. Bound the response to 16 KiB
and the request to five seconds within the existing ten-second native API
deadline; no retry extends it. Hold no business locks during provider I/O.

Immutable evidence binds issuer, subject, token identity, request/invocation and
observation time; it expires at that request's original deadline and cannot be
cached/replayed into another act. Confirmed email is shown escaped and complete
with bidirectional isolation, not a possibly colliding display name. An inspection
does not authorize the later POST: acceptance repeats the provider observation
and refuses a changed address, subject or verification state.

**Freshness boundary:** current means the provider's fresh UserInfo observation
for that act. A later provider edit can race the database commit; this is not an
atomic provider/database snapshot. Existing actor admission still applies and
the immutable issuer/sub binding never changes because emails match.

The pinned Keycloak 26.6.2
[UserInfo endpoint](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/protocol/oidc/endpoints/UserInfoEndpoint.java)
loads the valid session's enabled user; its
[property mapper](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/protocol/oidc/mappers/UserPropertyMapper.java)
reads current UserModel properties. That source supports this choice but is not
a substitute for real changed/unverified-email qualification. The
[OIDC UserInfo subject rule](https://openid.net/specs/openid-connect-core-1_0.html#UserInfoResponse)
remains mandatory.

New users complete provider registration and Keycloak-owned email verification;
P03-4 adds only the fixed `kc_action=VERIFY_EMAIL` authorization purpose under
the existing state/nonce/PKCE callback transaction. The original invitation and
verified subject stay bound; cancellation is recovery, not verification, and a
changed subject cannot replace that continuation or another live session.
Keycloak's pinned [VerifyEmail action](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/authentication/requiredactions/VerifyEmail.java)
supports application initiation, including its explicit send/cooldown UI.
No arbitrary required-action, destination or caller-supplied OIDC option enters
this path. This preserves the realm's global registration policy and reserved
accounts while providing a usable verification-action continuation and recovery.
Existing development Mailpit carries those **provider** verification emails.
Production verification uses the operator-configured provider SMTP. An absent or
failed verification service is visible and cannot be bypassed by the invitation.
Actual nonseed verification, not a fixture-set verified flag, qualifies this path.

### Exact nonmember entry and scope

Invitation entry is a separate exact-purpose branch after existing visitor/body,
host, stock credential and issuer/sub quota checks and native-secret validation.
It does not enlarge the three global-self descriptors or ordinary PublicSurface.
Only known Tenant/Organization hosts participate; Platform/unknown hosts refuse.
No browser tenant/org field or legacy assertion supplies a missing scope.

A short physically READ ONLY app-role lookup calls the new confined owner
function `fn_identity_invitation_entry(invitation_id, verifier, host_tenant,
authority_id, subject_key)`. The last two values come only from this request's
verified provider subject and installed authority, after exact-subject existing-
binding normalization; they are not body/header selectors.
It returns only exact matching offer ID/version/generation, tenant/org, inviter
UserId, bound actor ID/admission Boolean, inviter-admitting Boolean and terminal
reconciliation metadata. Actor eligibility derives only from that verified
authority/subject binding, never a caller-selected target UserId. It returns
no email, provider subject, profile, Role graph, directory or arbitrary User state.
Pending/accepted verifiers have separate meanings; an accepted verifier can only
support that same actor's result reconciliation, never a new grant.

Standard 05 owns the exact signature/body/ACL/policy: migration-owned, fixed search
path, static parameterized SQL, no writes, PUBLIC execute revoked and only app
execute granted. Its narrow body joins only Identity's stored offer and the bound
actor/stored-inviter User state. The necessary owner visibility arm is limited
to Invitation. The existing `users_isolation` migration-owner arm already permits
its confined User reads and is unchanged; the fixed function repeats the exact
verified-subject/stored-inviter predicates. No additional app SELECT grant,
ordinary directory visibility or arbitrary-User reader is introduced. Physical
owner visibility is
broader than the logical exact projection; closed body/ACL/caller tests and the
admitted-application-credential threat boundary remain essential.

The lookup closes before the next owner starts. `InvitationEntryScope` becomes
the eleventh logical tenant setter; it consumes a single-use immutable witness
binding the actual request, credential, verified subject/actor, native source,
real host and stored offer tuple. TenantHost caps the tenant; OrganizationHost
requires exact organization. Optional provider scope and any allowed assertions
must exactly agree, never win a conflict. Wrong-purpose, missing, reused or changed
witness refuses before announcement. The separate `HostAndInvitationOffer`
origin admits only the exact inspection, acceptance and own-result consumers.
It permits no directory, general protected act, AccountSite, Platform capability,
public write or SQL read/write hatch. API composition supplies no other producer.

For a new person, deliberate existing global-self bootstrap commits and disposes
first, under its opt-in policy and current allocation budgets. Invitation entry
does not enable creation when bootstrap is disabled. Missing/nonadmitting actors
cannot accept; no User and tenant transaction join or retarget each other.

### Atomic grant, delegation and races

`AcceptInvitationCommandHandler` is the only added multi-root writer. It writes
one Invitation and, when needed, one Membership with its contained Profile and
single assignment on the ordinary ReadWrite owner. It writes no Role, Tenancy,
User, provider or private audit row. Central plural MUST intents
`identity.invitation.accept` and `identity.membership.admit_invitation` share that
transaction and designate the actual offer/result Membership. The latter is a
security event: its tracked graph records creation or its declared result records
`already_member` without a fabricated creation diff. There is no separate invented
Membership creation event for an unchanged existing member.

Take the complete sorted exclusive User keys for invitee and stored inviter,
then exclusive Tenant fence, offered Role lock, Invitation row, then any existing
target Membership row. Never acquire another User key under the Tenant fence or
upgrade shared mode. All invitation mutations use the exclusive Tenant fence;
the serialization cost is accepted for this bounded initial surface. Fresh reads
after waits revalidate token/generation, actual state, lifecycle and immutable
scope. Repeating the exact offer-derived lookup under both retained User fences
freshly confirms the bound invitee is Active, including the `already_member`
case that performs no Membership INSERT and therefore cannot rely on its trigger.
Then branch on the stored state: Accepted is not mutation-eligible and follows
the dispatch/race/result-read rules below. Only Pending additionally checks
expected version, database expiry, Role pin, inviter eligibility and complete
delegation graph.

`IInvitationDelegationReader` reads only the locked offer's stored inviter and
target-scope Membership/Role facts on that same announced owner. It cannot mint
an HTTP candidate for another person or accept a supplied arbitrary UserId.
Eligible NULL-org Tenant grants and matching-branch grants compose under the
existing whole-Role ceiling and same-key Tenant-to-Organization rule. Current
membership-admin is required. The offered Role's pinned version and whole current
grants must still fit; changed Role content requires revoke/new offer. No seed,
realm-role, SystemActor or stale `was_admin` flag supplies delegation. The exact
offer-derived owner function may recheck its stored inviter's global admission
Boolean after the complete User/Tenant plan; it adds no general User reader.

| Exact target Membership | Acceptance outcome |
|---|---|
| None | Create one Active Membership and its children; consume offer. |
| Active | Consume as `already_member`, retain that exact Membership and all existing Roles/profile unchanged; no silent elevation. |
| Suspended | Conflict; no consumption or reactivation. Administrator uses the existing explicit status operation. |
| Only Revoked history | Create one fresh successor ID/children; never resurrect a predecessor. |

Database natural uniqueness, admission/last-admin constraints and tracked audit
capture still apply. A failure, MUST audit failure or confirmed rollback leaves
neither a consumed offer nor a new grant. The entry dispatcher observes Accepted
before declaring mutation intents and dispatches the exact Off
`GetOwnInvitationResultQuery` instead. That query verifies
the consumed digest, immutable tuple, fresh Active invitee and original current
Active Membership before returning its bounded result. It precedes Pending-only
version/expiry/Role-pin/inviter checks: the original version is necessarily stale
after consumption, and later offer expiry or inviter/Role edits do not undo the
committed result. It cannot substitute a successor or repeat a mutation. Another
actor receives no accepted-result disclosure. Unknown COMMIT is indeterminate
until a fresh authoritative read; absence of a response is never rollback proof.
This result read does not require current email, inviter delegation, Role pin or
offer lifetime: it discloses only the same Active actor's already-committed result
under the original Active Membership. It never offers a new grant. If a Pending
command loses the race after dispatch and sees Accepted under lock, it returns a
fixed recoverable conflict and its truthful failed-attempt evidence; the client
can then perform the result read. It neither declares a second successful
consumption nor suppresses an already-declared MUST intent.

Accept/accept, accept/revoke and accept/replacement serialize on this plan; only
one winner consumes the current generation. Expiry is checked after waiting,
before the authoritative conditional transition. Revoke of Accepted refuses and
never revokes its Membership. No timer claims to erase work already admitted.

### Transport, authorization and evidence

The phase decision package owns the finite API/native route and DTO table.
Administration uses existing applicable `identity.membership.admin`, exact write
scope and fresh delegation. A separate invitable-Role projection returns only
delegable RoleId/key/scope/version summaries; it grants no `identity.role.admin`
or general Role/permission-registry access to an Org Admin.

Ordinary create/revoke/policy writes use canonical preconditions and protected
durable idempotency. Add Invitation and the exact invitation-policy TenantSetting
as typed replay targets, each with a current-authority MUST receipt read; never
retain a handoff secret. `ReadInvitationPolicyReceiptQuery` reads only that
original setting ID/key in the same orgless tenant, under current own-Membership
coverage; it exposes no other settings. The policy read returns an explicit
absent/default version precondition for first creation, then the actual setting
root's version. Natural key uniqueness and the conditional create refuse a race;
absence never authorizes an unconditional upsert. Acceptance
uses the offer/actor/consumed-verifier natural convergence key, rejects arbitrary
Idempotency-Key and admits no tenantless key namespace. Fresh inspection and
own-result reads are Off/no-store self navigation; administrative lists/detail/
receipt and invitable-Role reads are MUST sensitive reads. All actual mutation
consumers are MUST; no event payload contains email, verifier or raw link.

Malformed input/preconditions are 400; stale canonical version and actionable
existing-member/scope-configuration conflicts are 409. Missing native/bearer
admission, invalid link or foreign target are masked 404; invalid-present bearer
remains 401. An authenticated matching offer with unverified/mismatched provider
email gets bounded 403 recovery without disclosing the expected mailbox.
Provider/storage uncertainty is 503, existing spent quotas 429 with their existing
bounded hints. New surfaces retain standard no-store/body/deadline/Origin/CSRF,
immutable response ownership, paging and late-publication controls.

### Privacy and completion boundary

Classify every mapped property and volatile/durable copy: recipient email,
verifier/consumed verifier, inviter/acceptor/Membership linkage, Role pin, expiry,
replay target, raw handoff and continuation. Sensitive snapshot markers and real
sink canaries supplement the model census. No raw email/token/link/verifier enters
logs, spans, error tracking, audit snapshots, validation attempted values,
diagnostic/test artifacts or identifier-only integration events. Credentials and
verifiers never enter a DSAR artifact. Recipient email/relationships retain their
authorized subject/purpose export contract in P03-10; diagnostic exclusion removes
no access right. Authorized product responses
necessarily disclose the selected email/link; no-store is not erasure proof.

The canonical Data Protection inventory gains the copies and applicable P03-10
locators/closure integration. Phase 11 owns retained-copy/purge extensions; this
record grants no premature erasure privilege or anonymisation claim. No invite
can admit a Closing/Closed User under the existing User fence. New screen proof
covers enabled locales, forms, scope/effect labels, wrong-account recovery,
conflict, verification/outage/429/503, keyboard/no-JavaScript and owner loss.
Manual accessibility remains actual human evidence, never inferred from tests.

## Consequences

### Positive

- New and existing people can join the intended institution/branch without a
  preexisting Membership, identity merge, stale token-email trust or broad bypass.
- Consumption, grant and required audit have one database result; lost raw link
  delivery has an explicit replacement path that cannot replay a secret.
- The packet delivers useful invitations without a second notification engine.

### Negative

- Administrators share links themselves; automatic invitation email is absent.
- Adds one confined definer, one logical scope setter/origin and one same-module
  two-root exception, all requiring exact census and real database qualification.
- Acceptance depends on provider availability/verification SMTP and spends an
  additional bounded provider call. Provider and database snapshots are separate.
- Exclusive Tenant serialization can increase contention. Expired/terminal rows
  retain personal linkage until their separately authorized retention workflow.
- Volatile loss requires reopening/replacing a link; old messages then contain
  unusable codes. Matching an unusual address may require correcting its input.

### Neutral

- Existing CI/test/application windows, owner quotas and Role/grant manifests do
  not change. A larger test population does not justify automatic timer increases.
- No new login role, DB connector, notification queue, Hub crossing, global email
  mirror, invitation-email sender or privilege to change provider identity lands.
- Acceptance is distinct from implementation, actual qualification and merge.

## Implementation Notes

The [six-step package](../roadmap/phase-03-delivery-log.md#p03-4-document-review-and-proposed-decision-package-2026-11-04)
owns sequence, carriers, verification and review cadence. Acceptance first
reconciles Standards 01/04/05/07/11/18/19/21, Identity/Tenancy specs and matrices,
Architecture 13/23, glossary, decision index and G8. Append the necessary dated
navigation/supersession disclosures in ADR-0003/0036/0040/0042/0064/0070/0073/0076/
0078/0079; do not reserve amendment numbers or rewrite Accepted bodies. Name each
exact function/ACL/caller/setter/origin/policy/receipt consumer in Standard 05/11
and its enforcing catalogue rule before activation. Scope writers remain closed.

Use `implement-task` with entity, handler, migration, permission/audit, frontend,
localization and integration-test workflows as applicable. Every implementation
step includes its meaningful tests, commit and two fresh independent review
rounds; confirmed findings are fixed and committed before the next step.

## Architecture tests

Extend existing canonical aggregate, context-setter, subject-admission, module,
classification, audit, permission, native operation and replay rules with actual
production subjects and planted wrong-purpose/omission controls. The aggregate
rule's successor name and literal two-handler allowlist are reconciled together;
no misleading provisioning-only name claims the new exception is absent.

Mandatory behavioral proof includes:

- App-role tenant/two-branch isolation, foreign IDs, malformed/foreign/reused
  witnesses, exact definer/body/ACL/policy set and setter cleanup/pool reuse.
- Real nonseed Keycloak signup, email verification and current UserInfo; wrong,
  unverified, changed, duplicate/ill-typed claims, wrong subject and outage.
- Fresh inviter revocation, Role version/grant changes, archived scope, suspended
  invitee, Active no-elevation and Revoked-successor cases after lock waits.
- Both commit orders for accept/accept, accept/revoke, replacement, expiry and
  closure fence; MUST rollback, unknown-COMMIT and same-actor exact-result retry.
- Raw-secret response loss, stale link replacement, no plaintext replay copies,
  canaries at every real sink and positive redaction controls.
- Native HTTPS first Membership, existing account, different live account,
  no-JavaScript code entry/confirmation, canceled verification, owner restart,
  accepted-but-no-session recovery, history/late-response/cleanup boundaries.

Final packet qualification uses complete required same-source CI and original
archive checks. Earlier P03-3 proof/manual follow-ups are not silently completed,
waived or transferred by this proposal. No passing local fixture proves external
exactly-once sending, full WCAG conformance or production capacity.

## References

- [Phase 03 invitation scope and G8](../roadmap/phase-03-identity-admin.md#invitation-flow)
- [Identity module](../modules/identity/README.md),
  [permissions](../modules/identity/permissions.md),
  [audit](../modules/identity/audit.md)
- [ADR-0042](0042-tenant-provisioning-cross-aggregate-transaction.md),
  [ADR-0072](0072-application-owned-identity-bootstrap.md),
  [ADR-0073](0073-scoped-membership-and-identity-data-foundation.md),
  [ADR-0074](0074-membership-admission-under-ordered-locks.md)
- [ADR-0076](0076-live-scoped-authorization.md),
  [ADR-0077](0077-trusted-authorization-evidence.md),
  [ADR-0078](0078-native-session-scope-and-self-entry.md),
  [ADR-0079](0079-bounded-identity-administration.md)
- [Identity architecture](../architecture/13-identity-and-auth.md#invitations),
  [Data Protection](../architecture/23-data-protection.md)
- [Database](../standards/05-database.md#lock-snapshot-and-adoption-conditions),
  [Security](../standards/11-security.md#native-scope-and-global-self-admission),
  [Audit](../standards/18-audit-coverage.md),
  [Permissions](../standards/19-permissions.md)
- [Phase 08a notifications](../roadmap/phase-08a-assessment-notifications.md#notifications)
