# ADR-0101: Platform Operator Authority

## Status

Accepted — 2026-11-19, after the maintainer's decisions on the dashboard, the producer
reset, the operator client and the grant store, and after three independent reviews and
the corrections they led to. The dated record is the
[Phase 03 acceptance record](../roadmap/phase-03-delivery-log.md#p03-9-package-review-dispositions-and-acceptance-2026-11-19).
Acceptance closes gate G13 for human operator authority together with
[ADR-0102](0102-production-tenant-onboarding.md) and
[ADR-0104](0104-controlled-issuer-rebind.md), and authorizes the code it describes. It
is not proof that anything is implemented.

**Date:** 2026-11-19
**Deciders:** @cemil

## Decision Drivers

- `DenyAllPlatformAdminGate` refuses everyone, so no cross-tenant act, killswitch toggle
  or privileged recovery exists. The gate's only input is a `reason` string, both the
  gate and the scope are singletons, the entry row writes a null actor and a null
  correlation id, and `EnterAsync` has no production caller. A real gate needs a
  principal, an operation and a permission.
- [ADR-0004 Amendment 1](0004-authentication-strategy.md) places operators in the
  separate `learnstack-hub` realm and says tenant routes refuse them.
  [ADR-0064](0064-tenant-api-authentication-boundary.md) registers exactly one bearer
  scheme for the tenant API and refuses every other issuer and `scope=platform`. No code
  authenticates an operator, the realm export carries one public client with no audience
  mapper and no assurance mapper, and `/api/internal/*` has no endpoint.
- [ADR-0036](0036-tenant-resolution-trusted-inputs.md) routes operator cross-tenant work
  through `/api/internal/*` under the Hub chain.
  [ADR-0034](0034-hub-contract-surface-invariant.md) and ADR-0004 define that chain as
  machine-to-machine under a Hub service identity. It carries no human operator, so it
  cannot supply C14's human attribution, and changing it is a coordinated decision in
  two repositories.
- Every phase from 02d to 11 must work without a Hub
  ([Phase 02c](../roadmap/phase-02c-hub-foundation.md)), including a Self-Hosted
  deployment whose operator is the customer.
- C14 requires a real operator with a specific Platform permission, tenant and
  wrong-purpose credentials refused before a bypass connection exists, surviving human
  attribution, reason and correlation, and a retained connection that cannot run
  autocommit after the scope completes.
- [Standard 19](../standards/19-permissions.md) refuses Keycloak roles as authority and
  makes Platform definitions never assignable to tenant roles. The pinned OpenAPI gate
  ([ADR-0100](0100-p038-federation-contract-transition.md)) refuses every new assignable
  key, so the design must not touch the tenant grant union.

## Considered Options

1. **A separate strict operator scheme on a LearnStack-owned prefix, grants held by
   LearnStack** (chosen). The pinned issuer and audience gate admission; a closed
   Platform catalogue and a grant source decide authority per act.
2. **Hub-mediated operator identity on the `/api/internal` chain** (rejected). It needs
   a new claim or endpoint, so a coordinated ADR-0034 change in two repositories. It
   loses the no-Hub baseline and carries a service identity where C14 needs a human.
3. **Accept the `learnstack-hub` issuer on the tenant scheme** (rejected).
   [ADR-0064](0064-tenant-api-authentication-boundary.md) refuses it, and a shared
   scheme invites a tenant token into an operator route.
4. **Authority from Keycloak realm or client roles** (rejected).
   [Standard 19](../standards/19-permissions.md) forbids it, and a role has no per-act
   freshness or audit.
5. **A deployment tool with deployment credentials only** (rejected as the general
   mechanism). It gives no verified human attribution. The offline rebind of
   [ADR-0104](0104-controlled-issuer-rebind.md) is a tool, but it authenticates the
   human through this ADR's scheme.

## Decision

LearnStack admits one human operator population, authenticated at a pinned operator
issuer, and gates every privileged act on a per-act, LearnStack-held Platform
permission.

1. **Population.** Operators are LearnStack or deployment staff. The issuer is
   `PlatformOperator:Issuer`, by default the `learnstack-hub` realm, and is always an
   issuer the deployment controls, never a tenant-configured or brokered provider. No
   service or machine operator is admitted: the Hub service-account chain carries no
   operator, and background or external job frames are refused privileged entry
   ([ADR-0047](0047-transactional-background-job-runtime.md)). An operator is never a
   `users` row or a tenant member, and a tenant token never admits.
2. **Authentication.** A second bearer scheme, `PlatformOperator`, validates signature,
   exact issuer, audience `learnstack-platform`, an allow-listed public PKCE `azp`,
   `typ` and a finite `exp`, and refuses a token whose `exp - iat` exceeds 15 minutes.
   It requires no `scope` claim: audience, client and assurance carry the purpose. The
   scheme is bound to the operator prefix and the tenant scheme remains the only scheme
   on `/api/v1`. The tenant scheme refuses the operator issuer and the operator scheme
   refuses the tenant realm. Roles and any other claim carry no authority.
3. **Assurance.** An act requires the `acr` claim to be in
   `PlatformOperator:AcceptedAcr` and `auth_time` to be no older than
   `PlatformOperator:MaxAuthenticationAge` (default 15 minutes). This names the claim
   and source that [Architecture 13](../architecture/13-identity-and-auth.md) requires,
   and [ADR-0099](0099-confined-tenant-federation.md)'s rule that assurance comes only
   from LearnStack-controlled authentication holds because the issuer is
   deployment-controlled. An absent or unlisted value refuses. A Self-Hosted issuer must
   emit `acr` and `auth_time` as the runbook specifies, or no operator can act.
4. **Surface.** Operator routes live under `/api/platform/v1/*`. The prefix is added to
   `VersionedRouteConvention.UnversionedRoutePrefixes` and to the host-classification
   prefixes beside `/api/internal`, joins its own route census and
   `OperationHttpClassifier` entries, and is served as its own OpenAPI document, outside
   the pinned tenant document and the tenant SDK. The controllers are present only when
   the issuer is configured, and a test proves absence otherwise. The gateway scaffold
   routes no `/api/platform` path; exposing it is a Phase 11 decision. A target tenant
   is data of the act and populates `ITenantContext` only through the confined
   announcement of [ADR-0102](0102-production-tenant-onboarding.md) § 1; no ordinary
   route resolves a tenant from a request value
   ([ADR-0036](0036-tenant-resolution-trusted-inputs.md)).
5. **Platform permissions.** Platform permission definitions live in a separate
   catalogue. They are never `Assignable`, never enter the tenant grant union, and the
   served tenant document stays byte-identical. Trusted operator policies may exist
   beside it. The closed key set this ADR family registers is:

   | Key | Acts | Owner |
   |---|---|---|
   | `platform.killswitch.write` | Toggle a declared killswitch | this ADR |
   | `platform.tenant.write` | Provision a Tenant and map its hosts | ADR-0102 |
   | `platform.tenant_bootstrap.write` | Role templates, first admin, admin recovery | ADR-0102 |
   | `platform.identity_authority.admin` | Controlled issuer rebind | ADR-0104 |
   | `platform.delivery.read` | Inspect dead letters and quarantined jobs | ADR-0103 |
   | `platform.delivery.write` | Targeted replay and narrow producer reset | ADR-0103 |
   | `platform.federation.read` | Report orphan, unreadable and nonconforming brokers | ADR-0099 A10 |
   | `platform.federation.admin` | Operator disable and orphan removal | ADR-0099 A10 |

   The `platform.*` examples in Standard 19 that no ADR registers stay unregistered, and
   Standard 19 cites the catalogue instead. It owns the final spelling when Step 1
   lands.
6. **Grants.** `IPlatformOperatorGrantSource` maps an operator subject digest to
   Platform keys. The shipped adapter binds `PlatformOperator:Grants` through
   `IOptionsMonitor`, validates every entry against the catalogue at start, takes effect
   on the next act when the configuration provider reloads (a provider that cannot
   reload needs a restart, and the runbook says so), and is evaluated on every act,
   never from claims. The adapter refuses a grant for the exported demo operator unless
   `Deployment:Mode` is Development, and refuses to start on any grant that fails
   validation. A database adapter is demand-gated
   ([ADR-0035](0035-demand-gated-infrastructure.md)): the trigger is a need for audited
   runtime grant changes, the owning phase is
   [Phase 11](../roadmap/phase-11-production-hardening.md), and managing it through an
   operator route needs its own decision.
7. **Admission.** `IPlatformOperatorGate.AdmitAsync(witness, operation, ticket)`
   replaces `IPlatformAdminGate.IsPermittedAsync(reason)`. The witness is minted by one
   minter class from a validated operator principal, and the closed minter census allows
   two callers: the operator scheme handler and the offline tool of
   [ADR-0104](0104-controlled-issuer-rebind.md), which validates the same token through
   the same validator. An operation is a closed registry entry: permission, audit slug,
   reason slug, and whether it needs the platform role. The reason is that slug. The
   optional ticket is 1 to 64 characters of `[A-Za-z0-9._:/#-]`. A malformed ticket
   answers 400 `validation_failed`. A missing permission or an unknown operation answers
   403. Admission runs before any privileged credential or connection is resolved.
8. **Scope.** `EnterAsync` takes a `PlatformAdmission` and keeps the compiler-supplied
   caller attributes. Admission is required by every operator act; the platform role is
   entered only by an act that needs it. The handle exposes no provider connection.
   Work runs through a bounded facade, completion and abandonment close the underlying
   connection, and a reference captured before completion cannot execute, begin a
   transaction or reopen. A mutant test captures such a reference. Entry still refuses
   job and external frames.
9. **Attribution.** An operator act writes `actor_user_id` NULL on its audit rows, the
   closed reason slug, the correlation id and `metadata.operator` with fixed members:
   `issuer`, `subjectDigest`, `kind` (`human`), `acr` and `executor`. The digest is the
   unpadded base64url SHA-256 of `learnstack.operator.subject.v1`, a line feed, the
   issuer, a line feed and `sub`; it is distinct from the identity subject key. The
   optional ticket appears only in sentinel-tenant rows, which no tenant can read. Rows
   a tenant can read carry the correlation id and never the ticket, and the response
   returns the correlation id for the operator's own record. Business columns an
   operator act writes use `UserId.SystemActor` for `created_by`, and
   `platform_killswitches.toggled_by` stays NULL.
10. **Result audit.** A MUST result row commits in the transaction that changes the
    state. A platform-role act writes it through a new witness-bound method on the
    scope's work transaction; `WritePlatformScopeAsync` keeps its single entry-row
    caller. An outbox-role act uses the fenced witnesses of
    [ADR-0103](0103-operator-recovery-of-asynchronous-work.md). An app-role act uses the
    ordinary pipeline extended by [ADR-0102](0102-production-tenant-onboarding.md). The
    rebind uses an intent row and a result row from its own role
    ([ADR-0104](0104-controlled-issuer-rebind.md)). The sentinel tenant may appear only
    on the platform-scope class of [ADR-0044](0044-audit-write-path.md) § 1, which this
    family extends to the entry row and a platform-wide result row; a tenant-targeted
    result row carries the real tenant. A forced audit failure rolls the act back and
    answers 503.
11. **Denied evidence.** A pre-authentication refusal answers 401 with a counter and a
    structured log and writes no row. An authenticated operator who lacks the permission
    or names an unknown operation answers 403 before any connection opens, with a
    counter and a Warning log carrying the operation slug and subject digest. No durable
    denial row is written, because one needs a bypass connection for a refused caller or
    a third sentinel arm. Operator denials are outside the tenant denial population of
    [ADR-0077](0077-trusted-authorization-evidence.md) and the denied-outcome rule of
    [Standard 18](../standards/18-audit-coverage.md), which receive dated disclosures
    saying so. Reopening needs Phase 11 security monitoring that requires durable
    denial evidence.
12. **No-Hub baseline.** The surface depends on the configured issuer and the grant
    source only. Development has no permissive gate: its configuration grants the real
    pinned `learnstack-hub` demo operator and the proof runs against real Keycloak.
    Forwarding an operator identity from the Hub needs its own coordinated ADR.
13. **First consumer: the killswitch writer.**
    [ADR-0045 Amendment 1 § 4](0045-entitlement-and-feature-flag-socket.md) withheld it
    until a Platform gate existed. It is a service, not a MediatR request, and keeps the
    matrix row `tenancy.killswitch.toggle` off-path with its `(planned)` marker removed
    in the commit that registers it. It resolves a key only from `KillswitchKeys.All`,
    reads the before state, upserts the row with the time and the ticket in the bounded
    `reason` column, writes the MUST result row on the work transaction, and removes the
    overlay cache entry after commit, including an indeterminate commit. Writes to
    `platform_killswitches` are confined to it by an architecture rule; the overlay's
    reads are unaffected. The runbook states the 60-second cross-instance lag, the
    fail-open read, and that no production code reads a killswitch yet.

    > **Erratum (2026-11-19, Amendment 1 below).** The text above puts the ticket in
    > the `reason` column. That table is readable by the application role, which § 9
    > forbids for a ticket, so the two sections disagreed when this record was accepted.
    > § 9 governs: the writer leaves `reason` NULL and the ticket is on the sentinel
    > audit rows only.
14. **Signals.** Counters record admissions by outcome and operation, refusals by cause,
    and result rows by operation, so a monitoring owner can alert without a log search.

## Context

The gate was shaped as a refusal on purpose: a permissive Development gate would repeat
the inversion where the demo passes and production refuses. P03-9 now owns the
population that makes the refusal answerable. The corpus fixes three points: operators
live in their own realm, tenant schemes refuse them, and Keycloak roles are not
authority. Its one unfinished edge is the route: ADR-0036 reaches operators only through
a machine chain that has no human. This ADR gives the human a LearnStack-owned prefix
and scheme, and leaves the Hub chain as it is.

A scheme per audience keeps each acceptor class closed, which is why a second scheme is
confined to its prefix instead of widening the tenant one. A grant source evaluated per
act gives revocation without a token wait, which a claim cannot. Configuration is enough
for a few staff and keeps the first packet free of a second platform table and its
writer; the port and the trigger make the table a plain addition. The result-audit
method is the real cost: the entry row commits before the work and records entry only,
so a result row needs the work transaction, which no platform write method takes today.

What would reopen this: a Hub operator portal that needs delegated operator identity (an
ADR-0034 amendment in both repositories), durable denial evidence from Phase 11
monitoring, or a service operator for scheduled platform work.

## Consequences

### Positive

- C14's positive and negative controls become provable against real Keycloak, and
  Self-Hosted works with no Hub.
- Tenant tokens cannot reach an operator route and operator tokens cannot reach a tenant
  route, by two closed schemes and a disjoint prefix.
- The tenant contract does not change, so the OpenAPI gate and G20 are not triggered.
- Attribution survives in every row: who, why, through which assurance, under which
  correlation, without leaking an operator's ticket to a tenant.

### Negative

- Two schemes and one new realm client and audience mapper in this repository's realm
  export, plus an `acr` and `auth_time` profile every Self-Hosted issuer must meet.
- Configuration grants give no audited runtime grant change until the table adapter.
- No durable denial row: a refused operator leaves a counter and a log only.
- Operators have no browser screen here. The Hub operator portal, not started, is the
  intended client of the served document.

### Neutral

- The scope's handle contract changes. Its only callers are tests today.
- `docs/runbooks/` begins here with operator access and the killswitch, earlier than the
  Phase 11 deliverable that names it.

## Implementation Notes

- Step 1 delivers the scheme, witness minter, catalogue, grant source, gate, scope
  redesign, result method, route mechanics, problem mapping, killswitch writer and
  runbooks. Realm changes: one public PKCE client `learnstack-operator` with an audience
  mapper for `learnstack-platform`, a client token lifetime of at most 15 minutes, and
  an ACR-to-level mapping with step-up. The proof reads a real token and shows the
  pinned realm emits `acr` and `auth_time`.
- The mutation targets are the gate call, its position before connection acquisition,
  the admission refusal inside job frames, the closure fence, a null actor, a missing
  result row and the cache removal after commit.
- Architecture rules: `PlatformAdminScope_Entry_Requires_Platform_Permission` conjunct B
  is rewritten against the real gate, not deleted, and gains its marker clause; the
  minter census is closed; a Platform definition is non-assignable and the tenant
  document bytes are unchanged; the operator prefix census is exact.

## Carriers

Each is a dated disclosure, never a body edit. ADR-0004 (scheme and audience), ADR-0036
(the platform URL space and the confined target announcement), ADR-0040 (the
announcement setter), ADR-0044 (the result method, the extended sentinel class and the
operator metadata), ADR-0045 (the writer's shape and the corrected actor claim),
ADR-0046 and ADR-0047 (through
[ADR-0103](0103-operator-recovery-of-asynchronous-work.md)), ADR-0064 (a second scheme
confined to its prefix), ADR-0077 (operator denials), ADR-0099 (Amendment 10) and
ADR-0100 (a Platform key does not recur the refusal). Standards 04, 11, 12, 13, 18, 19,
20 and 21, Architecture 13, 28 and 30, the glossary, the Tenancy and Identity module
specifications, and the Phase 03 and Phase 11 documents follow.

## Amendments

### Amendment 1 — The ticket stays out of a table the application role reads (2026-11-19)

Step 1 review found that § 13 stored the optional ticket in
`platform_killswitches.reason` while § 9 promises that a row a tenant can read never
carries it. The application role holds `SELECT` on that table under `USING (true)`, so
the overlay can read it; a ticket there would be reachable by any future module query,
including one an injection steered. The writer sets `reason` to NULL on every flip and
the ticket is recorded on the sentinel-tenant audit rows, where § 9 puts it. The column
stays for a future non-ticket explanation; nothing writes it. The Decision, Status, Date
and every other section are unchanged.

### Amendment 2 — Data request keys and operations (2026-11-22)

Accepted [ADR-0106](0106-subject-data-requests-authority-and-status.md), [ADR-0107](0107-data-request-fulfillment-and-account-closure.md) and [ADR-0108](0108-privileged-audit-redaction.md) (the [P03-10 decision package](../roadmap/phase-03-delivery-log.md#p03-10-decision-package-and-acceptance-2026-11-22)) add two non-assignable Platform keys to the closed set of § 5: `platform.data_request.read` (inspect requests awaiting an operator step) and `platform.data_request.write` (complete an erasure's privileged step, begin and finalize a global account closure, and the per-group fan-out act). The operations are closed registry entries of § 7 with audit slugs outside the `identity` module, as the operator result write requires, and the human-operator population of § 1 is unchanged: no service or machine operator acts for them. The Decision, Status, Date and every other section of this record are unchanged.

## References

- [ADR-0102](0102-production-tenant-onboarding.md),
  [ADR-0103](0103-operator-recovery-of-asynchronous-work.md),
  [ADR-0104](0104-controlled-issuer-rebind.md)
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
