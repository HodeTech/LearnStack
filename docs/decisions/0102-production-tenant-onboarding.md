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
