# ADR-0104: Controlled Issuer Rebind

## Status

Accepted — 2026-11-19, after the maintainer's decisions on the dashboard, the producer
reset, the operator client and the grant store, and after three independent reviews and
the corrections they led to. The dated record is the
[Phase 03 acceptance record](../roadmap/phase-03-delivery-log.md#p03-9-package-review-dispositions-and-acceptance-2026-11-19).
Acceptance closes the controlled issuer-rebind part of gate G13 together with
[ADR-0101](0101-platform-operator-authority.md) and
[ADR-0102](0102-production-tenant-onboarding.md), and authorizes the code it describes.
It is not proof that anything is implemented.

**Date:** 2026-11-19
**Deciders:** @cemil

## Decision Drivers

- [ADR-0072](0072-application-owned-identity-bootstrap.md) installs `identity_authority`
  once and says P03-9 owns "a frozen-admission, privileged issuer rebind after proven
  provider continuity, preserving AuthorityId, actors, keys and closed tombstones".
  Until then rename is unsupported, and the installer refuses any conflicting anchor.
- Subject keys hash the stable `AuthorityId` and the exact opaque `sub`, never the
  issuer URL ([Database Standards](../standards/05-database.md)). An origin change that
  preserves both leaves every key valid, and a changed `AuthorityId` re-keys every
  person and can resurrect a closed tombstone.
- The API qualifies `identity_authority` once per process in a read-only transaction and
  refuses admission on a mismatch. The read takes no lock, so a process that starts
  while a rebind runs can qualify against the old value and keep it until restart.
- The platform role runs in the API and worker hosts and bypasses row security. A column
  grant on it would let any platform-scope code path change the issuer, and a
  transaction-local setting is not a control, because any session can set it.
- The owner role used by the installer cannot write `audit_log`, so an owner-credential
  change could not commit with its audit row.
- C14 asks for positive and denied proof, partial-commit proof and identity-continuity
  proof, and says the dev seed authority is insufficient.

## Considered Options

1. **A dedicated rebind role used only by an offline tool that authenticates the human**
   (chosen). The credential never reaches a runtime host, the change and its audit row
   share one transaction, and the proof runs before the change.
2. **A column grant to the platform role guarded by a transaction setting** (rejected).
   Every runtime platform connection holds it, and the setting is settable by any
   session.
3. **The owner credential through the installer** (rejected). It cannot commit an audit
   row with the change, and the installer is built to refuse a conflicting anchor.
4. **An HTTP operator act under a runtime admission freeze** (rejected). It adds a
   database read to every authentication, and running processes keep the old issuer
   because qualification is startup-only.

## Decision

1. **What changes.** Only the origin of `identity_authority.issuer`, under the same
   realm path. `AuthorityId`, subject keys, actor ids and closed tombstones are
   preserved, no realm rename is admitted, and the new realm must preserve each `sub`,
   which a realm export and import does.
2. **The role.** A sixth login role, `learnstack_identity_rebind`, without `BYPASSRLS`,
   holds exactly: `SELECT` and `UPDATE (issuer)` on `identity_authority`; `SELECT` of
   `authority_id`, `subject_key`, `provider_subject` and `status` on `users`; and an
   `INSERT` on `audit_log` that a policy limits to the platform sentinel, the identity
   module and the two rebind operations, written only through the witness-bound methods
   of [ADR-0101](0101-platform-operator-authority.md) § 10. Its connection string,
   `ConnectionStrings:IdentityRebind`, exists only in the tool's environment, and an
   architecture rule confines the key to the tool.
3. **The tool.** A new project, `LearnStack.Tools.IdentityAuthorityRebind`, separate
   from the install-once installer. It reads the human's operator access token from
   standard input or the environment, never an argument, validates it with the operator
   validator of ADR-0101, and is the second sanctioned caller of the witness minter. The
   operator must hold `platform.identity_authority.admin`. A refusal exits non-zero with
   no change.
4. **A read-only realm credential.** The continuity proof enumerates users with a
   `view-users` credential the operator supplies at run time. It is not a LearnStack
   runtime or federation credential, and
   [ADR-0099](0099-confined-tenant-federation.md)'s refusal of user-level authority for
   LearnStack is unaffected.
5. **Freeze.** The tool runs in one transaction on a direct, non-pooled connection and
   takes an exclusive transaction-level advisory lock on a fixed key as its first
   statement. Authority qualification and the User-creation admission transaction take
   the shared form of that lock, so a start or a new binding waits for the rebind and
   then reads the new value. The tool also refuses while it sees a session of the
   application, outbox or ingress role other than its own. A process that holds no
   database connection is invisible to that check, so the runbook's stop step is the
   control for it. Such a process keeps the old issuer only until it restarts, because
   qualification then fails on the mismatch.
6. **Continuity proof.** Inside the lock, the tool reads the new issuer's discovery
   document and key set and requires its `issuer` to equal the new value. It enumerates
   the new realm's users completely or refuses. Every Active `provider_subject` must be
   among the enumerated `sub` values. The canonical subject key of no enumerated `sub`
   may equal a Closed user's key, so a closed person cannot reappear. A new-realm user
   with no stored key is allowed, because that person has not bootstrapped. A refusal
   reports counts and never identifiers.
7. **The change.** `UPDATE identity_authority SET issuer = @new WHERE id = 1`. A trigger
   refuses any change to `id` or `authority_id`. The issuer grammar stays in the tool
   and in qualification, which refuses a malformed or mismatched issuer, and is not
   duplicated in SQL.
8. **Audit.** A short standalone transaction commits an intent row first, so an aborted
   attempt is on record. The main transaction writes the MUST result row with the
   change, and a forced audit failure rolls the change back. Both rows use the platform
   sentinel and `metadata.operator`, and carry the ticket, because no tenant can read
   them.
9. **Retry.** Every refusal leaves the state unchanged and a retry repeats the whole
   proof. A tool run whose target already equals the stored issuer succeeds without
   writing a row. An unknown COMMIT is reconciled by reading the issuer.
10. **After the change.** Processes configured with the old issuer fail qualification at
    their next start. Deploying the new issuer configuration is the unfreeze.
11. **Signals.** Counters record each run by outcome and the continuity result by class.

## Context

A rebind is rare and dangerous, so it is shaped to be hard to do by accident. An HTTP
act would need a freeze on every authentication and still could not retire the issuer
that running processes hold. A dedicated role makes the strongest claim cheap to check:
no runtime host has the credential, so no runtime code path can change the issuer,
whatever it does. The cost is a sixth role, whose precedent is the fifth,
`learnstack_identity_ingress`, added the same way by
[ADR-0097](0097-retained-provider-events-and-tenant-audit-mirror.md).

The advisory lock closes the start race without a request-path read: qualification runs
once per process and user creation is rare, so a shared lock on each costs nothing
measurable. What it cannot close is a process that is running, idle and connectionless
when the tool runs. The honest statement is that the operator's stop step and the
restart requirement bound that case, not the database.

The proof compares against the stored facts rather than trusting an attestation. Active
rows retain the exact subject, so presence is a direct comparison; Closed rows retain
only the key, so absence needs the canonical encoding.

What would reopen this: a realm rename, a federated authority change, or a need to
rebind without a downtime window.

## Consequences

### Positive

- No runtime role or host can change the issuer, and the change, its proof and its
  result row are checkable.
- Starts and new bindings cannot straddle the change.
- Denied, partial and continuity cases have exact subjects.

### Negative

- A sixth database role, its provisioning, grants and census.
- A downtime window, and an operator-supplied read credential for the new realm.
- A connectionless survivor is bounded by procedure, not by the database.

### Neutral

- The install-once installer and its refusal of a conflicting anchor are unchanged.
- Development environments never need a rebind.

## Implementation Notes

- Step 3 delivers the role and its provisioning in the local stack and test fixtures,
  the grants and trigger migration with the canonical SQL, the tool, the lock calls in
  qualification and the creation transaction, the two audit methods and the runbook.
  Grant tests read column privileges, not table privileges, because table-level views do
  not show column grants.
- Proof: positive; no token, wrong audience, missing permission, stale authentication;
  killed between the intent row and the main transaction, and before commit; an unknown
  COMMIT; a forced audit failure; a missing Active subject; an extra new-realm user
  allowed; a Closed key present refusing; a live second session refusing; a start that
  blocks on the lock and then fails the mismatch; an unchanged `AuthorityId`; the
  trigger refusing an `authority_id` change; the architecture rule on the
  connection-string key. The mutation targets are the continuity comparison, the
  Closed-key check, the lock and the rollback on audit failure.

## Carriers

Dated disclosures in [ADR-0003](0003-tenant-isolation-defense-in-depth.md) (the sixth
role), [ADR-0044](0044-audit-write-path.md) (the rebind rows), and
[ADR-0072](0072-application-owned-identity-bootstrap.md) and
[ADR-0073](0073-scoped-membership-and-identity-data-foundation.md) (the rebind
contract). Standards 05 and 11, the Identity module specification, the Phase 03 Status
and the runbook follow.

## References

- [ADR-0101](0101-platform-operator-authority.md),
  [ADR-0102](0102-production-tenant-onboarding.md)
- [ADR-0003](0003-tenant-isolation-defense-in-depth.md),
  [ADR-0072](0072-application-owned-identity-bootstrap.md),
  [ADR-0097](0097-retained-provider-events-and-tenant-audit-mirror.md)
- [Database Standards](../standards/05-database.md)
- [Phase 03](../roadmap/phase-03-identity-admin.md): G13, D12, C14

## Amendment 1 — Implementation contract of the rebind tool (2026-11-20)

Step 3 of the platform-authority packet implements the decision. Each item below fixes
something the Decision left open or states where the code meets it. Item 4 widens the
freeze check beyond the roles § 5 lists and item 10 records a residual the Decision did
not state; none of them changes the Decision's choice of option, the role's grants or
the proof's rules.

1. **Where the code lives.** The database half, `IdentityIssuerRebind`, lives in the
   Identity module's Infrastructure `Bindings` namespace so unit and integration
   tests reach it, beside the install-once installer. The tool project holds
   configuration, the operator token, the new-realm reader and exit codes. The operator
   validator moved from the API project into Infrastructure, so the API's bearer scheme
   and the tool take one definition of the parameters, the bounded metadata manager and
   the closed profile. The witness minter stays one class; the tool is a second caller
   of the profile, with the executor `operator.rebind`, which is the "second sanctioned
   caller" of § 3. An architecture rule pins the callers of the profile and of the
   offline validator.
2. **The order.** The exclusive lock is the first statement of the transaction, held
   by a server-side `lock_timeout` of 30 seconds, so the tool waits for another rebind
   and refuses with a class. Under the lock it reads the stored authority. A run whose
   target already equals the stored issuer, under the same authority, ends there, with
   no row. Otherwise the intent row commits on a second connection before the freeze
   check, the proof or the change. An authority that is not the configured one is a
   continuity refusal, so it follows the intent row. An unknown COMMIT is reconciled on
   a fresh connection that first takes the shared lock, so it reads the issuer only
   once the original transaction has ended.
3. **The audit rows.** The two operations are `identity.authority.rebind_attempt`,
   outcome `indeterminate`, and `identity.authority.rebind`, outcome `success`, with the
   reason slug `issuer_rebind`, the entity type `IdentityAuthority` and the authority
   id as the entity. The policy arm admits exactly those two operation-and-outcome pairs
   and no other row, the ordinary tenant arm excludes the role so that an announced
   tenant cannot widen it, and the central audit installer owns the `audit_log` SQL, as
   it does for the creation witness. The result row carries counts and no subject.
4. **The freeze check counts the platform role too.** The platform role runs in the same
   hosts as the application role, and an idle pooled connection of any of the four is
   still a session. The check reports a count and never a session's identity. A pooled
   connection stays a session until its idle lifetime ends, so the runbook's stop step
   includes waiting for it. The check runs before the realm is read and again just
   before the change.
5. **"Reads the new value" needs a comparison, and "waits" is bounded.** A lock alone
   only makes a creation wait. Authority qualification takes the shared lock and then
   reads under a fresh snapshot, and the User-creation admission takes the shared lock,
   reads the stored authority and issuer, and refuses the creation when they are not
   the facts its process qualified with. A creation waits for the lock for the two
   seconds of its `lock_timeout` and is refused after that, and a start waits for the
   command timeout, thirty seconds, and then fails its start with the fixed
   qualification error. The two auxiliary read sources of the authorization and
   evidence lanes take no lock: they run under a five-second total startup deadline,
   and a start that straddles the change already fails at the locking read or at their
   own comparison.
6. **The role is checked before it is used.** The tool refuses unless the session is the
   rebind role, it holds no inherited, bypass, superuser, creation or replication
   attribute and no membership, no privilege on any other relation, and exactly the
   declared grants on the three it uses, compared column by column in both directions.
7. **Continuity details the Decision left open.** A Closing user that still holds its
   subject is judged by neither rule, because closure owns that transition; a Closing
   user that holds only its key is judged like a Closed one. A reported subject that
   cannot be a LearnStack subject is counted and cannot equal any stored one. Presence is
   the exact ordinal subject. The new realm is read in full twice and the reads must
   report the same users with none repeated, so a realm that changes while it is read is
   refused; a realm above 200,000 users is refused because the proof cannot finish in
   the tool's ten-minute deadline; a token that expires mid-enumeration is acquired
   again once per request; and the credential is sent only after the realm's discovery
   document names the target issuer.
8. **Inputs and exit codes.** The operator token comes from the environment or one line of
   standard input and the ticket, the realm client and its secret from the environment;
   an argument is refused. The deployment mode is a declared name exactly, because it
   decides whether a loopback operator issuer and the demo operator's grant are admitted,
   and outside Development the target issuer must be HTTPS. The tool prints one line of
   classes and counts to standard output, with the correlation id that finds the rows,
   and exits `0` for applied or already applied, `2` for a refusal that left the state
   unchanged and `3` when the commit's outcome could not be read back. An interrupt or a
   termination signal ends the run as the deadline does.
9. **Signals in a short-lived process.** The counters are defined in the library and
   exported by the tool only when an OTLP endpoint is configured in its environment;
   the printed line and the audit rows are the record otherwise.
10. **What the credential holder can do, and "the same realm path".** The tool checks the
    operator's token, grant and attribution; the database does not force them. The role
    holds `UPDATE (issuer)` and can insert a row of the admitted shape, so whoever holds
    its password can change the issuer, or record a rebind that did not happen, without
    the tool. The runbook therefore keeps the password apart from the operator's
    identity and retires it after the window. A database-enforced pairing of the change
    and its result row would need a confined definer that reads `audit_log`, and is the
    maintainer's decision. "The same realm path" means the realm name: the issuer must
    still end in `/realms/learnstack`, and the path before it may change.

The Decision, Status, Date and prior amendments of this record are unchanged.
Acceptance of the linked records is not proof that anything they decide is implemented.
