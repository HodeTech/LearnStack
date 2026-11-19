# ADR-0101: Platform Operator Authority

## Status

Proposed — 2026-11-19, for the maintainer's review. It closes no gate, changes no
Accepted record and authorizes no code until Accepted. Acceptance would close gate G13
for human operator authority together with
[ADR-0102](0102-production-tenant-onboarding-and-authority-rebind.md), and would
carry the bounded disclosures listed under
[Carriers owed at acceptance](#carriers-owed-at-acceptance).

**Date:** 2026-11-19
**Deciders:** @cemil

## Decision Drivers

- `DenyAllPlatformAdminGate` refuses everyone, so no cross-tenant act, killswitch
  toggle or privileged recovery exists. The gate's only input is a `reason` string, the
  scope writes a null actor and null correlation id, and no production caller of
  `EnterAsync` exists. A real gate needs a principal, an operation and a permission.
- [ADR-0004 Amendment 1](0004-authentication-strategy.md) places operators in the
  separate `learnstack-hub` realm and says tenant routes refuse them.
  [ADR-0064](0064-tenant-api-authentication-boundary.md) closes the tenant bearer
  scheme to every other issuer and to `scope=platform`. No code authenticates an
  operator today, the realm export carries one public client, no audience and no
  assurance mapper, and `/api/internal/*` has no endpoint.
- [ADR-0036](0036-tenant-resolution-trusted-inputs.md) routes operator cross-tenant
  work through `/api/internal/*` under the Hub chain, while
  [ADR-0004](0004-authentication-strategy.md) says that chain is independent of the
  operator JWT and uses a service account, and
  [ADR-0034](0034-hub-contract-surface-invariant.md) makes any new crossing a
  coordinated decision in both repositories. The two readings cannot both hold.
- Every phase from 02d to 11 must work without a Hub
  ([Phase 02c](../roadmap/phase-02c-hub-foundation.md)), including a Self-Hosted
  deployment whose operator is the customer.
- C14 requires a real operator with a specific Platform permission, tenant and
  wrong-purpose credentials refused before a bypass connection exists, surviving
  human attribution, reason and correlation, and a retained connection that cannot
  run autocommit after scope completion.
- [Standard 19](../standards/19-permissions.md) refuses Keycloak roles as authority and
  makes Platform definitions never assignable to tenant roles. The pinned OpenAPI gate
  ([ADR-0100](0100-p038-federation-contract-transition.md)) refuses every new
  assignable key, so the design must not touch the tenant grant union.

## Considered Options

1. **A separate strict operator scheme on a LearnStack-owned prefix, grants held by
   LearnStack** (chosen). The pinned issuer and audience gate admission; a closed
   Platform catalogue and a grant source decide authority per act.
2. **Hub-mediated operator identity on the `/api/internal` chain** (rejected). It
   needs a new claim or endpoint, so a coordinated ADR-0034 change in two
   repositories. It loses the no-Hub baseline and carries a service identity where
   C14 needs a human.
3. **Accept the `learnstack-hub` issuer on the tenant scheme** (rejected).
   [ADR-0064](0064-tenant-api-authentication-boundary.md) refuses it and a shared
   scheme invites a tenant token into an operator route.
4. **Authority from Keycloak realm or client roles** (rejected).
   [Standard 19](../standards/19-permissions.md) forbids it, and a role has no
   per-act freshness or audit.
5. **A deployment tool with deployment credentials only** (rejected as the general
   mechanism). It gives no verified human attribution. It remains the shape for the
   offline act in [ADR-0102](0102-production-tenant-onboarding-and-authority-rebind.md),
   which still authenticates the human.

## Decision

LearnStack admits one human operator population, authenticated at a pinned operator
issuer, and gates every privileged act on a per-act, LearnStack-held Platform
permission.

1. **Population.** Operators are LearnStack or deployment staff. The issuer is
   `PlatformOperator:Issuer`, by default the `learnstack-hub` realm, and may be any
   OIDC issuer a Self-Hosted deployment configures. No service or machine operator is
   admitted: the Hub service-account chain carries no operator, and background or
   external job frames are refused privileged entry
   ([ADR-0047](0047-transactional-background-job-runtime.md)). An operator is never a
   `users` row or a tenant member, and a tenant token never admits.
2. **Authentication.** A separate scheme, `PlatformOperator`, validates signature,
   exact issuer, audience `learnstack-platform`, an allow-listed public PKCE `azp`,
   `typ`, finite `exp` and a lifetime of at most 15 minutes. It exists only when the
   issuer is configured; otherwise no operator route is mapped. The tenant scheme
   still refuses the operator issuer and the operator scheme refuses the tenant
   realm. Roles and any other claim carry no authority.
3. **Assurance.** An act requires the `acr` claim from the operator issuer to be in
   `PlatformOperator:AcceptedAcr` and `auth_time` no older than
   `PlatformOperator:MaxAuthenticationAge` (default 15 minutes). This names the claim
   and the source that [Architecture 13](../architecture/13-identity-and-auth.md)
   requires and satisfies [ADR-0099](0099-confined-tenant-federation.md)'s rule that
   assurance comes only from LearnStack-controlled authentication. An absent or
   unlisted value refuses; no value is inferred from a role.
4. **Surface.** Operator routes live under `/api/platform/v1/*`, outside `/api/v1`
   and `/api/internal`. The prefix is unclassified by host, joins the route census,
   and is excluded from the pinned tenant document because the document includes
   only `/api/v1` paths. A target tenant is named in the route as data and never
   populates `ITenantContext`: no route resolves a tenant from a request value
   ([ADR-0036](0036-tenant-resolution-trusted-inputs.md)). The operator contract is
   served as its own document and is not part of the tenant SDK.
5. **Platform permissions.** Platform definitions live in a separate catalogue. They
   are never `Assignable`, never enter `OperationPolicyCatalog` or the tenant grant
   union, and the served tenant document stays byte-identical. The closed key set is:

   | Key | Act | Owner | |---|---|---| | `platform.killswitch.write` | Toggle a declared
   killswitch | this ADR | | `platform.tenant.write` | Provision a Tenant and default
   Organization | ADR-0102 | | `platform.tenant_bootstrap.write` | Role templates, first
   admin, recovery | ADR-0102 | | `platform.identity_authority.admin` | Controlled
   issuer rebind | ADR-0102 | | `platform.delivery.read` | Inspect dead letters and
   quarantined jobs | ADR-0103 | | `platform.delivery.write` | Targeted replay and
   narrow producer reset | ADR-0103 | | `platform.federation.read` | Report orphan or
   unreadable brokers | ADR-0099 | | `platform.federation.admin` | Operator disable and
   orphan removal | ADR-0099 |

   Standard 19 owns the final spelling and the scope table when Step 1 lands.
6. **Grants.** `IPlatformOperatorGrantSource` maps an operator subject key to Platform
   keys. The shipped adapter reads configuration, validates every entry against the
   catalogue at start, reloads without a restart and is evaluated on every act, never
   from claims. A database adapter is demand-gated
   ([ADR-0035](0035-demand-gated-infrastructure.md)): its trigger is a need for audited
   runtime grant changes, its owning phase is
   [Phase 11](../roadmap/phase-11-production-hardening.md), and the Hub operator portal
   consumes it. The operator subject key is the base64url SHA-256 of the issuer, a
   newline and `sub`; audit rows carry the key, never the raw `sub`.
7. **Admission.** `IPlatformOperatorGate.AdmitAsync(witness, operation, ticket)`
   replaces `IPlatformAdminGate.IsPermittedAsync(reason)`. The witness is minted only by
   the operator authentication boundary from a validated principal, under a closed
   minter census. An operation is a closed registry entry: permission, audit slug,
   reason slug and whether it needs the platform role. The reason is that slug. The
   ticket is a 1 to 64 character token matching `[A-Za-z0-9._:/#-]`, required for every
   write act and carried only in `metadata` and the bounded `reason` column of the
   written row. Admission runs before any privileged credential or connection is
   resolved.
8. **Scope.** `EnterAsync` takes a `PlatformAdmission` and nothing else. Admission is
   required by every operator act; the platform role is entered only by an act that
   needs it. The handle exposes no provider connection. Work runs through a bounded
   facade, completion and abandonment close the underlying connection, and a reference
   captured before completion cannot execute, begin a transaction or reopen. A mutant
   test captures such a reference. Entry still refuses job and external frames.
9. **Attribution.** Every row an operator act writes carries `actor_user_id` NULL, the
   closed reason slug, the correlation id and `metadata.operator`: subject key, issuer
   key, `kind` (`human`), accepted `acr` and the executing component. The shape is a
   fixed census, not free text. A tenant-targeted act writes its rows under the real
   target tenant so that tenant's administrators see it; the entry row and a
   platform-wide act use the platform sentinel.
10. **Result audit.** A MUST result row commits in the transaction that changes the
    state. A platform-role act uses a new witness-bound seam on the scope's work
    transaction, a second caller of the platform write in
    [ADR-0044](0044-audit-write-path.md) § 10. An outbox-role act uses the fenced
    witness of [ADR-0103](0103-operator-recovery-of-asynchronous-work.md). An app-role
    act uses the ordinary pipeline of
    [ADR-0102](0102-production-tenant-onboarding-and-authority-rebind.md). A forced
    audit failure rolls the act back, and an act the audit made unavailable answers 503.
11. **Denied evidence.** A pre-authentication refusal answers 401 with a counter and a
    structured log and writes no row
    ([ADR-0077](0077-trusted-authorization-evidence.md): a tenantless refusal invents
    no tenant). An authenticated operator who lacks the permission, names an unknown
    operation or supplies an invalid ticket answers 403 before any connection opens,
    with a counter and a Warning log carrying the operation slug and subject key. No
    durable denial row is written, because one needs a bypass connection for a refused
    caller or a third sentinel class. This ADR records that as the decided evidence.
12. **No-Hub baseline.** The surface depends on the configured issuer and the grant
    source only. Development has no permissive gate: its configuration grants the real
    pinned `learnstack-hub` demo operator and the proof runs against real Keycloak.
    Forwarding an operator identity from the Hub needs its own coordinated ADR.
13. **First consumer: the killswitch writer.**
    [ADR-0045 Amendment 1 § 4](0045-entitlement-and-feature-flag-socket.md) withheld it
    until a Platform gate existed. It is a service, not a MediatR request, and keeps the
    matrix row `tenancy.killswitch.toggle` off-path. It resolves a key only from
    `KillswitchKeys.All`, reads the before state, upserts the row with the operator, a
    bounded ticket and the time, writes the MUST result row on the work transaction, and
    removes the overlay cache entry after commit, including an indeterminate commit.
    The runbook states the 60-second cross-instance lag and the overlay's fail-open
    read.

## Context

The gate was shaped as a refusal on purpose: a permissive Development gate would repeat
the inversion where the demo passes and production refuses. P03-9 now owns the
population that makes the refusal answerable. The corpus supplies three fixed points:
operators live in their own realm, tenant schemes refuse them, and Keycloak roles are
not authority. It supplies two statements that cannot both be true, an operator reaching
LearnStack through the Hub chain and an operator JWT independent of that chain. This ADR
takes the second, because the first needs a Hub crossing that does not exist and cannot
serve the no-Hub baseline.

A scheme per audience keeps each acceptor class closed. A grant source evaluated per act
gives revocation without a token wait, which a claim cannot. Configuration is enough for
a population of a few staff and keeps the first packet free of a second platform table
and its writer; the port and the trigger make the table a plain addition. The
result-audit seam is the real cost: the entry row commits before the work and records
entry only, so a result row needs the work transaction, which the platform write seam
does not take today.

What would reopen this: a Hub operator portal that needs delegated operator identity
(an ADR-0034 amendment in both repositories), a need for durable denial evidence from
Phase 11 monitoring, or a service operator for scheduled platform work.

## Consequences

### Positive

- C14's positive and negative controls become provable against real Keycloak, and
  Self-Hosted works with no Hub.
- Tenant tokens cannot reach an operator route and operator tokens cannot reach a tenant
  route, by two closed schemes and a disjoint prefix.
- The tenant contract does not change, so the OpenAPI gate and G20 are not triggered.
- Attribution survives in every row: who, why, through which assurance, under which
  correlation.

### Negative

- Two schemes and a second realm client to operate and test, plus one new realm client
  and audience mapper in this repository's realm export.
- Configuration grants give no audited runtime grant change until the table adapter.
- No durable denial row: a refused operator leaves a counter and a log only.
- Operators have no browser screen in this repository. The Hub operator portal, which
  has not started, is the intended client of the served document.

### Neutral

- The scope's handle contract changes. Its only callers are tests today.
- `docs/runbooks/` begins here with operator access and the killswitch, earlier than the
  Phase 11 deliverable that names it.

## Implementation Notes

- Step 1 delivers the scheme, witness, catalogue, grant source, gate, scope redesign,
  result seam, route census, problem mapping, killswitch writer and runbooks. Realm
  changes: a `learnstack-platform` audience client, a public PKCE `learnstack-operator`
  client with an audience mapper, a token lifetime cap and an ACR-to-level mapping with
  step-up.
- The mutation targets are the gate call, its position before connection acquisition,
  the admission refusal inside job frames, the closure fence, a null actor and a missing
  result row.
- Architecture rules: `PlatformAdminScope_Entry_Requires_Platform_Permission` conjunct B
  is rewritten against the real gate, not deleted; SQL on `platform_killswitches` is
  confined to the writer; a Platform definition is non-assignable and the tenant
  document bytes are unchanged; the minter census of the witness is closed.
- The Platform permission rows start in the
  [Tenancy matrix](../modules/tenancy/permissions.md) and the platform operations in its
  [audit matrix](../modules/tenancy/audit.md).

## Carriers owed at acceptance

Each is a dated disclosure, never a body edit:
[ADR-0004](0004-authentication-strategy.md) (the operator scheme and audience),
[ADR-0036](0036-tenant-resolution-trusted-inputs.md) (the platform URL space),
[ADR-0044](0044-audit-write-path.md) (the second platform write caller and the
`metadata.operator` shape), [ADR-0045](0045-entitlement-and-feature-flag-socket.md)
(the writer's shape), [ADR-0100](0100-p038-federation-contract-transition.md) (a
Platform key is not assignable, so it does not recur the refusal). Standards 04, 11, 18,
19, 21, 12 and 13, Architecture 13 and 28, the glossary and Phase 03 follow.

## References

- [ADR-0102](0102-production-tenant-onboarding-and-authority-rebind.md),
  [ADR-0103](0103-operator-recovery-of-asynchronous-work.md)
- [ADR-0004](0004-authentication-strategy.md),
  [ADR-0034](0034-hub-contract-surface-invariant.md),
  [ADR-0036](0036-tenant-resolution-trusted-inputs.md),
  [ADR-0044](0044-audit-write-path.md),
  [ADR-0045](0045-entitlement-and-feature-flag-socket.md),
  [ADR-0064](0064-tenant-api-authentication-boundary.md),
  [ADR-0077](0077-trusted-authorization-evidence.md)
- [Permission Standards](../standards/19-permissions.md),
  [Security Standards](../standards/11-security.md)
- [Phase 03](../roadmap/phase-03-identity-admin.md): G13, D12, C14
