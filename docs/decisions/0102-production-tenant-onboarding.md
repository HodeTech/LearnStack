# ADR-0102: Production Tenant Onboarding

## Status

Accepted — 2026-11-19, after the maintainer's decisions on the dashboard, the producer
reset, the operator client and the grant store, and after three independent reviews and
the corrections they led to. The dated record is the
[Phase 03 acceptance record](../roadmap/phase-03-delivery-log.md#p03-9-package-review-dispositions-and-acceptance-2026-11-19).
Acceptance closes the production provisioning, first-administrator and recovery parts of
gate G13 together with [ADR-0101](0101-platform-operator-authority.md) and
[ADR-0104](0104-controlled-issuer-rebind.md), and authorizes the code it describes. It
is not proof that anything is implemented.

**Date:** 2026-11-19
**Deciders:** @cemil

## Decision Drivers

- [ADR-0072](0072-application-owned-identity-bootstrap.md) and
  [ADR-0073](0073-scoped-membership-and-identity-data-foundation.md) leave production
  first-admin setup to P03-9. The fixed seed capability grants no operator authority.
- The tenant, host-mapping, Role-template and first-admin commands are `Seed`-only by
  construction. Their handlers pass a seed guard that demands a single allowed source
  `Seed`, an exact trusted `Seed` source and the fixed demo declaration, and the API
  registers deny-all continuation and foundation admissions. Adding a source to their
  policies would still be refused in the handler, so production onboarding needs its own
  commands.
- The invariants that make an administrator safe, last-admin protection, the User fence
  and ordered membership locks
  ([ADR-0074](0074-membership-admission-under-ordered-locks.md)), live in app-role
  domain code. A raw write on the platform connection skips them and the audit capture
  of [ADR-0044](0044-audit-write-path.md) § 7.
- A tenant that resolves to no host is not onboarded, and the Hub endpoint that pushes
  host mappings does not exist ([Phase 02c](../roadmap/phase-02c-hub-foundation.md)), so
  the no-Hub baseline needs an operator-admitted mapping.
- The first-admin command refuses any tenant with Membership history, so recovery of a
  tenant whose administrators are gone is a different predicate, not a repeat.
- C14 asks for positive and denied proof, partial-commit proof, unknown-COMMIT
  reconciliation, and says seed authority is insufficient.

## Considered Options

1. **Operator-admitted commands of their own, on the app role, through the ordinary
   pipeline** (chosen). They reuse the domain aggregates and extracted shared cores, and
   keep the `Seed` commands untouched.
2. **Add the operator source to the existing `Seed` commands** (rejected). The seed
   guard requires a single `Seed` source and the fixed declaration, so the handler
   refuses it, and widening the guard weakens the seed's only-this-source property.
3. **Run onboarding as raw SQL on the platform connection** (rejected). It skips the
   last-admin and User invariants and the audit capture, and widens a `BYPASSRLS` role.
4. **A platform-issued invitation for the first administrator** (rejected here). It
   reuses [ADR-0093](0093-verified-invitation-acceptance-and-handoff.md) but needs an
   exception for an inviter who holds no Membership and depends on automatic email
   delivery, so recovery would depend on mail.

## Decision

> **Erratum — 2026-11-19.** Item 1 below says the target tenant is announced by "one
> new logical tenant setter", "the eleventh", and that "the four context writers do not
> change". No new setter exists, and the claim was unachievable when it was written:
> the query filters and the transaction behavior read the context accessor, so an act
> that is not installed in it is invisible to row security. The operator act executor
> installs an immutable resolved ambient context for the admitted target, the ordinary
> transaction behavior announces it, the setter census does not move, and the executor
> is a fifth writer of the accessor; shown by
> `SetTenant_Callers_Are_The_Enumerated_Four` (`TenantContextConstructionTests`) and
> `PlatformOperatorActExecutorTests`. The Decision is unchanged: an admitted operator
> act runs on the application role under the admitted target tenant's announcement and
> the platform role is not used. Current authority:
> [ADR-0040 Amendment 25](0040-ambient-unit-of-work.md). Recorded in Amendment 1.

1. **A confined operator source and announcement.** `AuthorizationSourceKind` gains
   `PlatformOperator`, minted only from an
   [ADR-0101](0101-platform-operator-authority.md) admission for one of the operations
   below. The five commands carry `FiniteTrusted` policies whose only source is
   `PlatformOperator`, and the `Seed` policies and guards are unchanged, so neither
   authority can perform the other's acts. The target tenant is announced to the unit of
   work by one new logical tenant setter, confined to that source: it reads the admitted
   operation's declared target, never an ambient or header value, announces once per
   unit of work and refuses any other caller. It is the eleventh logical tenant setter;
   [ADR-0040](0040-ambient-unit-of-work.md) and
   [ADR-0036](0036-tenant-resolution-trusted-inputs.md) receive dated disclosures, and
   the four context writers do not change. The platform role is not used.

2. **The commands.** `OperatorProvisionTenant` creates a Tenant and its default
   Organization. `OperatorMapHost` maps a host through the existing host-mapping store
   and its reserved-host and platform-host rules. `OperatorInitializeRole` creates one
   finite Role template. `OperatorCreateFirstAdmin` creates the first tenant-wide
   administrator Membership. `OperatorRecoverAdmin` creates a replacement administrator.
   `platform.tenant.write` covers the first two; `platform.tenant_bootstrap.write`
   covers the other three. Shared cores are extracted from the seed handlers so the
   domain logic exists once; the seed and operator handlers differ only in their guard.
3. **Identity of the acts.** The operator supplies a UUIDv7 `tenantId` and a slug, and
   an act is idempotent on that pair: the same pair finds the existing tenant, and a
   known slug under another id refuses. A lost response therefore loses nothing. The
   business actor of every written row is `UserId.SystemActor`; the operator is recorded
   in audit only, as [ADR-0101](0101-platform-operator-authority.md) § 9 fixes. Operator
   policies do not use the membership-candidate path, which refuses `SystemActor`.
4. **Host mapping.** The operator asserts ownership of the host. LearnStack verifies no
   DNS, and the seed does not either. The Hub's custom-domain approval for SaaS stays
   the Hub's and is unreachable here. The runbook states that a Self-Hosted operator is
   responsible for DNS and TLS.
5. **Naming the person.** The operator names the first administrator by provider subject
   at the identity authority's issuer. The person must already have completed
   global-self bootstrap at the tenant's mapped host, so a `users` binding exists. A
   confined single-act resolver computes the canonical subject key under the stored
   `AuthorityId`, the request supplying neither, and returns the `UserId` of an Active
   user only. An unknown or non-Active subject refuses with a 409 `bootstrap_required`
   problem. The operator creates no `User`, and an email address is never an identifier.
6. **Order.** Provision, map the host, the person signs in once at that host, each Role
   template, then the first administrator. Each step commits in its own transaction, as
   [ADR-0073](0073-scoped-membership-and-identity-data-foundation.md) requires, and the
   Role roots keep their pristine-prefix predicates. A retry resumes from the first
   absent step by reading state, never by repeating a write blind. An unknown COMMIT is
   reconciled by that read. Naming a tenant that does not exist, or a person who is not
   bootstrapped, refuses before any write.
7. **First administrator.** `OperatorCreateFirstAdmin` refuses unless the tenant has no
   Membership history, the seed command's predicate, evaluated under the ordered locks
   of [ADR-0074](0074-membership-admission-under-ordered-locks.md) inside the
   transaction. A tenant that already has an administrator, or ever had a Membership,
   cannot gain one this way, so the act is not an escalation path.
8. **Recovery.** `OperatorRecoverAdmin` refuses unless, under the same locks, the tenant
   has no active tenant-wide administrator Membership. It has its own audit slug and
   reason slug, accepts the same person-naming rule, and leaves the last-admin invariant
   unchanged. It is the only act that may add an administrator to a tenant with
   Membership history.
9. **Audit.** Each step writes its MUST rows in its own transaction through the ordinary
   pipeline, under the real target tenant. Provisioning writes two, the Tenant and the
   Organization. The pipeline's authorization audit context, evidence coordinator and
   draft composer, which today admit a null actor, a missing tenant and a null reason
   only for `Seed`, gain a `PlatformOperator` branch that records the closed reason slug
   and `metadata.operator` of [ADR-0101](0101-platform-operator-authority.md) § 9. The
   rows never carry the ticket.
10. **Seed capability.** The Seeder refuses to run when `Deployment:Mode` is not
    Development. A test proves the seed source cannot perform an operator act and the
    operator source cannot perform a seed act.
11. **Signals.** Counters record each act by operation and outcome, and a resumed setup
    by the step it resumed at.

## Context

Onboarding is not the platform scope's job. The scope exists to cross tenants and write
platform tables, and the platform role bypasses row security. A first administrator is
the opposite case: one tenant, ordinary tables, and the strongest invariants in the
Identity module. Running it on the app role keeps those invariants and the capture of
[ADR-0044](0044-audit-write-path.md) § 7, and costs one new authorization source, one
new confined setter and five commands, which the closed censuses make visible and
testable. The cost is real: the source enum, the setter census and the audit composition
all change, and their existing negative controls must keep refusing everything else.

The seed guard is the reason for separate commands. It is a deliberate only-this-source
check, and the operator authority must not borrow it. Extracting shared cores keeps the
duplication to the guard.

Naming a person by provider subject keeps email out of authority. It asks the person to
sign in once first, which is the zero-membership entry P03-3 already ships. The operator
reads the `sub` from the issuer's own console, which the operator already holds. The
operator-supplied tenant id is safe because the command is reachable only with an
admission, and it makes a retry exact.

What would reopen this: a Hub that pushes provisioning and mappings, a need for a
self-service first administrator, or an operator population too large for subject-based
naming.

## Consequences

### Positive

- No production onboarding depends on the development seed, and a test proves the two
  authorities cannot substitute.
- The last-admin and User invariants and the audit capture are unchanged and exercised.
- The no-Hub baseline can create a reachable tenant with an administrator, and recover
  one.

### Negative

- A new authorization source kind, a new confined setter and a new resolver widen closed
  sets that tests pin, so each census is edited deliberately.
- An operator needs the person's `sub`, and the person must sign in first at a mapped
  host.
- The operator, not LearnStack, vouches for host ownership.

### Neutral

- Shared cores are extracted from the seed handlers; the seed behavior does not change.
- Seeded Development tenants are unaffected.

## Implementation Notes

- Step 2 delivers the source, setter, resolver, commands, routes, audit-composition
  branch, Seeder mode refusal and the onboarding runbook. It touches the source enum,
  `OperationPolicy` and `TrustedOperationDescriptor` source guards, the authorization
  audit context, the evidence coordinator and the draft composer.
  > **Erratum — 2026-11-19.** The proof item below reads "succeeding after the last one
  > is revoked". The database refuses that state: the deferred last-administrator guard
  > rejects any commit that leaves a tenant without an available administrator, so no
  > ordinary write reaches it; shown by SQLSTATE `P0303` in
  > `PlatformOperatorOnboardingTests`. The proof reaches the state by an owner-level
  > repair that steps around that guard and shows the invariant intact after the
  > recovery, and "the setter's operation binding" reads as the executor's binding of
  > the admitted operation to its request type. The Decision is unchanged. Current
  > authority: Amendment 1 item 4. Recorded in Amendment 1.
- Proof: positive and denied acts; wrong tenant; unbootstrapped person; interruption
  between every pair of steps with a retry that completes once; an unknown COMMIT; a
  tenant with history refusing the first-admin act; recovery refusing a tenant with an
  active administrator and succeeding after the last one is revoked; the last-admin
  invariant after recovery; seed insufficiency in both directions. The mutation targets
  are the source check, the in-transaction predicates, the resolver's Active filter and
  the setter's operation binding.

## Carriers

Dated disclosures in [ADR-0036](0036-tenant-resolution-trusted-inputs.md) (the target
announcement), [ADR-0040](0040-ambient-unit-of-work.md) (the eleventh logical setter),
[ADR-0044](0044-audit-write-path.md) and [ADR-0033](0033-audit-durability-model.md) (the
audit-composition branch), [ADR-0072](0072-application-owned-identity-bootstrap.md) and
[ADR-0073](0073-scoped-membership-and-identity-data-foundation.md) (the production
path), [ADR-0074](0074-membership-admission-under-ordered-locks.md) (the recovery
predicate under its locks), Standards 05, 18 and 19, the Identity and Tenancy module
specifications, and the Phase 03 Status.

## Amendments

### Amendment 1 — As built: an executor-installed context, no new setter (2026-11-19)

Step 2 of the packet shipped the five acts. Two statements of this record were false
when it was accepted, and each carries an inline erratum above it: the new tenant setter
and unchanged context writers of § 1, and the proof item that revokes the last
administrator. Both were shown by the shipped code and its tests
(`SetTenant_Callers_Are_The_Enumerated_Four`, `PlatformOperatorActExecutorTests`, and
SQLSTATE `P0303` in `PlatformOperatorOnboardingTests`). The items below refine what the
record left open or stated differently from what was built; they are history, not
errata.

1. **No new setter; a fifth context writer.** The operator act executor
   (`LearnStack.Infrastructure/PlatformOperator/PlatformOperatorActExecutor.cs`)
   consumes the single-use admission, opens a fresh scope, installs an immutable
   resolved `Ambient` context for the admitted tenant (no user, no organization, no host
   scope) and sends the command through the ordinary pipeline, which announces that
   context as it does for seed, delivery and job acts. The logical setter census stays
   at fourteen ([ADR-0040](0040-ambient-unit-of-work.md) Amendments 22 and 23), the
   platform role is not used, and the executor is the fifth writer of the context
   accessor ([ADR-0036](0036-tenant-resolution-trusted-inputs.md) Amendment 2). The one
   source that may mint the operator descriptor is the fourth minting source, scoped to
   the executor's frame and bound to the operation of the admission. The routes that may
   call the executor are pinned by
   `Only_The_Operator_Onboarding_Routes_Run_An_Operator_Act`.
2. **Provisioning on a resolved context.** The new tenant's id is the act's target, so
   the executor installs a resolved context for an id that has no `tenants` row yet, and
   the ordinary insert policy admits it. The operator provisioning command therefore
   needs no provisioning announcement, and the seed command keeps its own.
3. **The resolver is the sixteenth definer.**
   `fn_identity_operator_resolve_user(uuid, bytea)` takes the stored authority id and
   the derived subject key. The application derives the key with the one
   `IdentitySubjectKey` implementation under the deployment's qualified authority, and
   the function refuses any other authority, any session but the application role, any
   key that is not 32 bytes and any transaction that is not an ordinary read-write
   tenant one, then answers an Active user's id or null. The handlers ask the guard
   whether the act is live before they call it. The function is nonetheless callable by
   any application-role transaction that announces a tenant, so what it protects is the
   subject lookup's confinement to the application role and not a witness bound to an
   act; [Database Standards](../standards/05-database.md) records the residual.
4. **Recovery is a break-glass for repaired states.** The last-administrator guard is
   deferred to commit and rejects any write that leaves a tenant without an available
   administrator, which means a Membership with an Active tenant-wide administrator Role
   whose User is Active. Revoking or suspending the last administrator is therefore not
   reachable through any ordinary path, and Users have no admitted update or removal
   owner today. § 8's predicate is a Membership predicate, as accepted, and is
   implemented as written: the act refuses while an Active tenant-wide administrator
   Membership exists. A tenant whose only administrator Membership is nominally Active
   but unusable is not a recoverable tenant under it, and a predicate that also reads
   the User's eligibility would need a second confined definer. The maintainer decides
   whether that is wanted.
5. **"Membership history" is tenant-wide history.** § 7 refuses a tenant that "ever had
   a Membership". The predicate is evaluated under the act's own context, which carries
   no organization and no tenant-wide read witness, so row security shows it
   tenant-wide Memberships only. A tenant whose only Memberships are organization-scoped
   reads as having had none. Ordinary writes cannot produce that state, because the
   graph guard requires a tenant-wide administrator, so it is reachable only after an
   owner-level repair, as recovery's state is, and the database's own guard reads the
   same way.
6. **"Bootstrapped" means a global binding.** §§ 5 and 6 say the person signs in at the
   tenant's mapped host first. The resolver finds an Active user of the stored authority
   with that subject, wherever they signed in, because `users` is global per authority.
   The runbook says so and asks the operator to confirm the subject belongs to the
   intended person.
7. **Idempotency as built.** An exact repeat of an act answers from what was stored; a
   repeat that changes any value under the same identifiers is refused
   (`lockey_identifier_taken`). The administrator and Role acts find their own
   Membership or Role before judging the Role's present state, so a template the new
   administrator edited afterwards does not make the original act's retry fail. A
   repeated act commits its ordinary MUST rows with no captured change, so the trail
   shows every attempt. The host-mapping retry drops the host's cached resolution again.
8. **Signals (§ 11).** `learnstack_platform_operator_acts_total{operation,outcome}`
   counts each act as `applied`, `replayed`, `refused` or `failed`; a `replayed` count
   by operation is a setup resumed at that step. Event 7109 (Warning) records an
   admitted act the pipeline itself refused as forbidden, which writes no durable row.
9. **Request shape.** Every identifier is a UUIDv7 and the platform sentinel is refused
   as a tenant (400). A host mapping must state both `isActive` and `isPubliclyLive`,
   because no act unmaps a host. A blank provider subject or one over 255 characters is
   a 400, and a refused storage write answers in the vocabulary the sibling Identity
   handlers use (`concurrency_conflict`, `dependency_unavailable`, or
   `identity_foundation_refused`).

The business actor of every written row is `UserId.SystemActor` and the audit actor is
NULL, as § 3 and [ADR-0101](0101-platform-operator-authority.md) § 9 say. A refused
operator act writes the ordinary `failed` MUST row in the target tenant, carrying the
operator in `metadata.operator`, and an authorization refusal of an operator request
writes no durable denial row ([ADR-0033](0033-audit-durability-model.md) Amendment 14).

Carriers changed with this amendment: ADR-0033 Amendment 14, ADR-0036 Amendment 22 and
ADR-0040 Amendment 25, each with an inline erratum beside the false text; Database
Standards, Security Standards, Audit Coverage, Permission Standards and the
Architecture-Tests Catalogue; the Identity and Tenancy module specifications; the
glossary and the onboarding runbook.
[ADR-0072](0072-application-owned-identity-bootstrap.md),
[ADR-0073](0073-scoped-membership-and-identity-data-foundation.md) and
[ADR-0074](0074-membership-admission-under-ordered-locks.md) are read and unchanged: no
sentence of theirs is stale. The Decision, Status, Date and every other section are
unchanged. Acceptance of the linked records is not proof that anything they decide is
implemented.

## References

- [ADR-0101](0101-platform-operator-authority.md),
  [ADR-0103](0103-operator-recovery-of-asynchronous-work.md),
  [ADR-0104](0104-controlled-issuer-rebind.md)
- [ADR-0072](0072-application-owned-identity-bootstrap.md),
  [ADR-0073](0073-scoped-membership-and-identity-data-foundation.md),
  [ADR-0074](0074-membership-admission-under-ordered-locks.md),
  [ADR-0093](0093-verified-invitation-acceptance-and-handoff.md)
- [Identity module](../modules/identity/README.md),
  [Database Standards](../standards/05-database.md)
- [Phase 03](../roadmap/phase-03-identity-admin.md): G13, D12, C14
