# ADR-0042: Tenant Provisioning as a Bounded Cross-Aggregate Transaction

## Status

Accepted

**Date:** 2026-08-30 **Deciders:** @platform

## Decision Drivers

- **A written rule and a mandated implementation contradict each other, and both
  are load-bearing.**
  [Architecture Standards § Aggregate Ownership](../standards/01-architecture-standards.md)
  says "cross-aggregate writes inside a single transaction are forbidden. Use an
  integration event."
  [Database Standards § Tenant-owned foreign keys](../standards/05-database.md)
  says the provisioning transaction "inserts the tenant, inserts its default
  organization, then `UPDATE`s the tenant — three statements in one transaction",
  and rejects the deferred-constraint alternative by name. `Tenant` and
  `Organization` are two aggregate roots
  ([ADR-0017](0017-tenant-organization-hierarchy.md)), so the mandated shape is
  the forbidden one.
- **The invariant the shape exists to protect is an isolation invariant, not a
  convenience.** `tenants.default_organization_id` is the anonymous
  organization scope's fallback and the target of a composite foreign key. A
  tenant observable without one is a tenant whose org-scoped reads have no
  default and whose `default_organization_id` is `NULL` on a column every later
  reader assumes is set after provisioning. The shipped aggregate already states
  it: `Tenant.AssignDefaultOrganization` — "Both statements run in one
  transaction, so a tenant is never observable without a default organization"
  (`Tenant.cs`).
- **No mechanism in the corpus can substitute for the atomicity.**
  [ADR-0010](0010-cross-module-communication.md) offers four cross-module
  mechanisms and [ADR-0017](0017-tenant-organization-hierarchy.md)'s 2026-08-10
  amendment already reasoned that "none of ADR-0010's four mechanisms offers an
  atomic substitute". An integration event moves the second write to a later
  transaction, which is precisely the window this invariant forbids.
- **[ADR-0040](0040-ambient-unit-of-work.md) deliberately declined to settle
  it.** Its § What the transaction spans says in terms: "It does **not** license
  a cross-aggregate or cross-module *write*. … **This ADR does not relax either
  rule.**" That was the right scope for an ADR about connection ownership, and
  it leaves the contradiction standing rather than resolving it.
- **Phase 02a Packet 7 is the first code that executes the shape.** Until now
  nothing wrote a `Tenant`. The seed and the provisioning command land together
  in Packet 7, so the exception is about to be established either by a decision
  record or by a merged commit that nobody wrote down.
- **Packet 7's aggregate-boundary decision widens the blast radius if the
  exception is left unbounded.** If `TenantDomain` and `TenantSetting` are
  promoted to roots — the direction Packet 7 takes, and the reason this driver is
  written conditionally rather than assumed — a provisioning path that "sets a
  tenant up" could plausibly write four roots in one transaction. The exception
  has to say which writes it covers, or it covers whatever the next handler
  wants. Nothing below depends on which way that boundary resolves.

## Considered Options

1. **A bounded exception, enumerated rather than principled** (chosen). Exactly
   one operation writes two aggregate roots in one transaction — `Tenant` and
   its default `Organization` — and the rule stays otherwise absolute.
2. **An integration event for the default organization** (rejected). The
   corpus's own prescribed substitute; it cannot deliver an atomicity invariant.
3. **`DEFERRABLE INITIALLY DEFERRED` on the composite foreign key** (rejected,
   and already rejected once by Database Standards).
4. **Fold `Organization` into the `Tenant` aggregate** (rejected). Removes the
   cross-aggregate write by removing the aggregate.
5. **Relax § Aggregate Ownership generally** (rejected). Replaces a rule that
   keeps module extraction cheap with a judgement call per handler.

## Decision

**Tenant provisioning writes exactly two aggregate roots in one transaction:
the `Tenant` and its default `Organization`.** This is a standing, named
exception to
[Architecture Standards § Aggregate Ownership](../standards/01-architecture-standards.md),
and it is the only one.

The exception is **bounded by enumeration, not by principle**:

- It covers the two roots named above and nothing else. A tenant's initial
  `TenantDomain`, `TenantSetting`, `TenantLocale` and `TenantFeatureFlag` rows
  are **not** covered — none of them carries an atomicity invariant against the
  tenant row, so each is written by its own command in its own transaction.
  `platform_host_to_tenant` is a projection rather than an aggregate and is
  outside the rule entirely.
- It is held by one operation. `ProvisionTenantCommand` (and the seeder that
  invokes it) is the whole set; the set is written down in the architecture test
  as a literal allow-list, so a second holder is a test edit and a review
  conversation, not a silent addition.
- It licenses nothing cross-**module**. Both roots are Tenancy's; a write
  crossing a module boundary remains forbidden with no exception, which is what
  keeps [ADR-0010](0010-cross-module-communication.md)'s extraction property
  intact.

Standards 01 § Aggregate Ownership gains a one-line carve-out citing this ADR.
[ADR-0040](0040-ambient-unit-of-work.md) carries a dated Amendment, because its
"one aggregate's write" table row and its "this ADR does not relax either rule"
sentence become an incomplete description of the sanctioned transaction — both
were true when written, so the instrument is an amendment and not an
[ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md) erratum.

## Context

### Why the integration event cannot substitute

The substitute § Aggregate Ownership prescribes is: write the first aggregate,
enqueue an integration event, let a consumer write the second. Applied here, the
`tenants` row commits first and the `organizations` row arrives in a later
transaction. Between them the tenant is a committed, readable row with
`default_organization_id IS NULL`.

That window is not theoretical and not brief in the failure case. The outbox
guarantees the event is eventually delivered, not that it is delivered before
the next request. Anything that resolves the tenant in between — a host lookup
against a seeded `platform_host_to_tenant` row, an operator opening the tenant,
Phase 02d's anonymous render — sees a tenant whose default organization does not
exist. And if the consumer fails permanently, the window never closes: the
outbox retries, but no retry can create an organization whose id the tenant row
was supposed to point at.

The deeper point is that an integration event is an eventual-consistency
mechanism and the invariant is a consistency invariant. Substituting one for the
other does not weaken the guarantee, it deletes it.

### Why not a deferred constraint

[Database Standards](../standards/05-database.md) already rejected
`DEFERRABLE INITIALLY DEFERRED`, and its reason stands: a deferred constraint
moves the failure to `COMMIT`, where the error names the constraint rather than
the statement that broke it. It also would not help. Deferring the *constraint*
does not merge two transactions into one; it only relaxes when the check runs
inside whatever transaction is open. The three-statement shape needs one
transaction either way.

### Why `Organization` is not folded into `Tenant`

It is the obvious way to make the problem disappear, and
[ADR-0017](0017-tenant-organization-hierarchy.md) already decided against it for
reasons that have not changed: an organization has its own lifecycle
(created, renamed, archived) independent of its tenant; it is a permission scope
in its own right ([Permission Standards](../standards/19-permissions.md)'s
Organization scope); and every organization-scoped row in every module keys on
it. An aggregate that must be loaded whole to rename one branch of a
fifty-branch tenant is the wrong boundary, and the RLS policy reads
`app.organization_id` and joins nothing — the isolation model does not need
containment either.

### What would change our minds

- **If `default_organization_id` became legitimately optional** — a deployment
  shape where a tenant has no organizations at all — the invariant dissolves and
  the exception with it. Nothing in the corpus points that way today: ADR-0017
  makes the default organization the tenant's own root branch.
- **If the two-level hierarchy grew a third level** the provisioning path would
  have more than two roots to consider, and the enumerated exception would need
  re-deriving rather than extending. ADR-0017 fixes the hierarchy at two levels,
  so this is a re-open condition, not a foreseen one.
- **If PostgreSQL grew a usable cross-transaction atomicity primitive** the
  shape could change without changing the rule. It has not.

### What this deliberately does not settle

- **Whether a child write bumps `Tenant.row_version`.** That is a concurrency
  question owned by [ADR-0039](0039-optimistic-concurrency-token.md) and the
  Packet 7 boundary decision, not an aggregate-ownership question.
- **The provisioning command's own contract** — its parameters, its
  `[AllowsUnresolvedTenantContext]` marker, its permission key. Those are
  Packet 7 and Phase 03 respectively.
- **Deprovisioning.** Every foreign key is `ON DELETE RESTRICT` and tenant
  hard-deprovisioning has no owning phase
  ([Tenancy module spec § Risks](../modules/tenancy/README.md)). Whatever writes
  that path will need its own decision; this ADR grants it nothing.

## Consequences

### Positive

- The mandated implementation and the written rule stop contradicting each
  other. An implementer reading either one reaches the same code.
- The invariant is preserved by the mechanism that actually delivers it, and the
  cost of that choice is written down where a reviewer can find it.
- The exception is countable. A hole nobody counts becomes a hole everybody
  uses; a literal allow-list of one is a hole with a name on it.
- The rule keeps its force everywhere else. § Aggregate Ownership remains
  absolute for every other handler, and cross-**module** writes stay forbidden
  with no exception at all.

### Negative

- The corpus now has a rule with an exception, which is strictly harder to teach
  **and to enforce** than a rule without one: the architecture test has to carry
  an allow-list, and an allow-list is a thing that can grow. Mitigated only by
  the exception being singular and named.
- The architecture test that bounds it is a source scan, and a source scan is
  not proof. See § Implementation Notes for exactly what it does and does not
  catch.
- A future reader may reasonably ask why the same argument does not license the
  next atomic-looking pair. The answer is in § Context and not in the rule text,
  which is a weaker place for it to live.

### Neutral

- No schema change. The three-statement shape, the nullable
  `default_organization_id` and the composite foreign key are all already
  shipped in Packet 6's migration; this ADR records why they are legal, not what
  they are.
- No change to [ADR-0040](0040-ambient-unit-of-work.md)'s connection model. The
  two writes already share the ambient unit of work; what changes is that the
  second one is now sanctioned rather than tacitly tolerated.

## Implementation Notes

- **The transaction shape** is the one
  [Database Standards](../standards/05-database.md) and the
  [Tenancy module spec](../modules/tenancy/README.md) already draw: `BEGIN` →
  `SET LOCAL app.tenant_id` to the registry-supplied id → `INSERT tenants` →
  `INSERT organizations` → `UPDATE tenants SET default_organization_id` →
  `COMMIT`. The tenant id is never minted in the handler; the self-keyed policy's
  `WITH CHECK` passes because the context was set to that id first.
- **The carve-out** is one line in
  [Architecture Standards § Aggregate Ownership](../standards/01-architecture-standards.md),
  citing this ADR. The rule text itself is unchanged.
- **The architecture test** is
  `Cross_Aggregate_Writes_Are_Confined_To_Tenant_Provisioning`. The **rule** is
  registered in [the catalogue](../standards/21-architecture-tests-catalogue.md)
  in the same commit as this ADR, at Status **Registered** and Phase 02a
  Packet 7; the **test** lands with the provisioning handler it guards, later in
  that packet, and the catalogue row moves to Implemented then. Registering the
  rule ahead of the code is the corpus's ordinary order and is why the exception
  cannot arrive un-enumerated. It scans MediatR handler sources for write calls
  (`Add`/`AddRange`/`Update`/`Remove`) against more than one `DbSet` whose entity
  implements `IAggregateRoot<TId>`, and holds a literal allow-list of exactly one
  handler type.

  > **Erratum (2026-09-03, Amendment 1).** The scan described in the sentence
  > above cannot fire, and could not on the day this was written: `Application`
  > may not reference `Infrastructure`, so no handler can name a `DbSet`. The
  > shipped rule counts constructor parameters deriving from
  > `IAggregateWriteStore<TRoot, TId>` instead. See § Amendments.

  **What it proves and what it does not.** It catches the direct form, which is
  the form a handler is written in. It does not catch a write routed through a
  repository, a helper or a second `DbContext` reached indirectly — the same
  limit [the catalogue § What a structural test proves](../standards/21-architecture-tests-catalogue.md)
  states for every source scan. The binding control is that the allow-list has
  one entry and growing it is a reviewed diff; the scan is what makes the
  ordinary mistake loud.
- **The seeder does not hold a second copy of the exception.** It invokes
  `ProvisionTenantCommand` rather than writing the two aggregates itself, so the
  allow-list stays at one entry and the seed exercises the same path production
  does.

## Amendments

### Amendment 1 (2026-09-03): the rule counts ports, not `DbSet` use

**What was false when it entered the record.** § Implementation Notes specifies
`Cross_Aggregate_Writes_Are_Confined_To_Tenant_Provisioning` as a source scan for
`Add` / `AddRange` / `Update` / `Remove` against more than one `DbSet`. That scan
can never fire. `Application` may not reference `Infrastructure` — a shipped
dependency rule that predates this ADR — so no handler can name a `DbSet` at all,
and a rule that cannot fire while carrying Status **Implemented** claims coverage
the suite does not have. An inline erratum marks the sentence; the rationale, the
carve-out and everything else in the record stand.

**What ships.** The rule reflects over the production assemblies and counts, per
`IRequestHandler<,>` implementation, the constructor parameters deriving from
`IAggregateWriteStore<TRoot, TId>`. More than one is the cross-aggregate write,
and the literal allow-list holds exactly one name:
`ProvisionTenantCommandHandler`. Counting a **type** rather than a name means
renaming a port does not escape the rule, and fusing the two ports into one to
hide the write is itself caught — measured, as one of three mutations that each
turn the rule red.

**Why two ports rather than one.** `ITenantWriteStore` and
`IOrganizationWriteStore` are separate interfaces deliberately. A single fused
port would have been less code and would have hidden the very thing this ADR
exists to enumerate.

**What did not change.** The sanctioned operation, its three statements, the
one-entry allow-list, and the seeder's obligation to invoke the command rather
than write the two aggregates itself.

## References

- [ADR-0017: Tenant / Organization Hierarchy](0017-tenant-organization-hierarchy.md)
- [ADR-0040: The Ambient Unit of Work](0040-ambient-unit-of-work.md)
- [ADR-0010: Cross-Module Communication](0010-cross-module-communication.md)
- [ADR-0041: Correcting False Statements in Accepted ADRs](0041-correcting-false-statements-in-accepted-adrs.md)
- [Architecture Standards § Aggregate Ownership](../standards/01-architecture-standards.md)
- [Database Standards](../standards/05-database.md)
- [Tenancy module spec](../modules/tenancy/README.md)
- [Phase 02a: Platform Kernel, Multi-Tenancy, Organization, and Foundation Sockets](../roadmap/phase-02a-kernel-tenancy.md)
