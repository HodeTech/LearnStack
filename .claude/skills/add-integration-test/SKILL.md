---
name: add-integration-test
description: >
  Write a Testcontainers-backed integration test in
  `backend/tests/LearnStack.Tests.Integration` that exercises a real Postgres —
  connected as `learnstack_app`, never as the owner — and asserts behaviour under
  tenant + organization context. No Valkey and no Kafka: nothing the backend runs
  calls them. The Postgres fixture and CI's `backend-integration` job both
  arrived in Phase 02a Packet 6; the tenant-isolation suite and the seeded
  tenants follow in Packet 7. Docker-bound cases carry
  `[Trait("Requires","Docker")]`, which is how CI routes them. USE FOR: cross-tenant / cross-org isolation tests (mandatory for every
  new `[TenantOwned]` / `[OrganizationScoped]` entity), outbox → consumer round
  trips, audit-pipeline assertions, RLS-effective-isolation tests. DO NOT USE FOR:
  pure unit tests (use `LearnStack.Tests.Unit`), architecture tests (use
  `LearnStack.Tests.Architecture` and the
  [add-architecture-test](../add-architecture-test/SKILL.md) skill), or
  performance tests.
---

# Adding an integration test

## Purpose

Stand up the project's Testcontainers fixture, run a real-world scenario, and
assert that the four-layer tenant isolation holds (context + EF filter + RLS +
architecture test) plus any other invariant the change touches. See
[06-testing.md](../../../docs/standards/06-testing.md).

## When to use

- Every new `[TenantOwned]` / `[OrganizationScoped]` entity (the isolation pair
  is mandatory).
- A handler whose correctness depends on RLS-level isolation (the unit test
  bypasses RLS).
- Outbox → consumer round-trip.
- Audit-pipeline-writes-the-expected-row.
- Provider-adapter contract test that talks to a containerised real provider
  (LiveKit OSS, SeaweedFS, Meilisearch).

## When not to use

- Domain-method invariants. Those are unit tests in `LearnStack.Tests.Unit`.
- Structural rules (no cross-module reference). Architecture tests own those.
- UI flows. E2E tests in `LearnStack.Tests.EndToEnd` own those.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Scenario | Yes | A short name + setup + act + assert. |
| Seed | Yes | Minimum tenants / orgs / users / customization data the scenario needs. |
| Required containers | Yes | **Postgres, always and only.** Not Valkey, not Kafka — nothing the backend runs calls them. Meilisearch / LiveKit / SeaweedFS only for a provider-contract test, and only from the phase that ships the adapter. |
| Tenant context | Yes | Which tenant + org the act phase runs as. |

## Workflow

### Step 1: Reuse the fixture

> **The Postgres fixture exists** — `LearnStack.Tests.Integration.Database.PostgresFixture`,
> shipped by Phase 02a Packet 6 along with CI's `backend-integration` job. What
> does **not** exist yet is the tenant-isolation suite or any seeded tenant: the
> fixture provisions the four roles and hands out four connection strings, and
> Packet 7 adds the schema, the policies and the seed. The `AsTenant(...)` helper
> below is Packet 7's shape, not today's.
>
> Anything using it carries `[Trait(RequiresDocker.Key, RequiresDocker.Value)]`,
> which is how CI routes it to `backend-integration` rather than `backend`.

What the fixture does today:

- Spins **Postgres only** via Testcontainers. Not Valkey, not Kafka — nothing the
  backend runs today calls either, and both sit behind the `gated` compose profile
  per [ADR-0035](../../../docs/decisions/0035-demand-gated-infrastructure.md).
- Provisions the **four database roles** before the first migration, then applies
  migrations **as `learnstack_migration`** — which owns every table — and exposes
  a connection as **`learnstack_app`** for the tests themselves. A test that
  connects as the owner or as a `BYPASSRLS` role passes even when every policy is
  inert, so it proves nothing.
- Seeds a baseline platform admin, two tenants, two orgs per tenant.
- Exposes `fixture.AsTenant(tenantId, organizationId?)` to scope a block.

```csharp
public sealed class EnrollmentCreateTests : IClassFixture<TestFixture>
{
    private readonly TestFixture _fx;
    public EnrollmentCreateTests(TestFixture fx) => _fx = fx;

    [Fact]
    public async Task Create_succeeds_for_member_of_target_tenant()
    {
        var courseVersionId = await _fx.SeedCourseVersionAsync(_fx.TenantA);

        using (_fx.AsTenant(_fx.TenantA, _fx.OrgA1))
        {
            var result = await _fx.Mediator.Send(new CreateEnrollmentCommand(
                LearnerId: _fx.LearnerInTenantA,
                CourseVersionId: courseVersionId,
                CohortId: null,
                Source: EnrollmentSource.Manual));

            Assert.True(result.IsSuccess);
        }
    }
}
```

### Step 2: Mandatory tenant isolation pair

Every `[TenantOwned]` entity ships with these two tests **at minimum**:

```csharp
[Fact]
public async Task Entity_TenantA_cannot_read_TenantB_data()
{
    using (_fx.AsTenant(_fx.TenantA)) {
        await _fx.CreateEntityAsync();   // commits a row tagged tenant_a
    }

    using (_fx.AsTenant(_fx.TenantB)) {
        var rows = await _fx.Db.Entities.ToListAsync();
        Assert.Empty(rows);   // RLS + filter both enforce
    }
}

[Fact]
public async Task Entity_query_with_no_tenant_context_returns_zero_rows()
{
    using (_fx.AsTenant(_fx.TenantA)) {
        await _fx.CreateEntityAsync();
    }

    using (_fx.AsNoTenant())   // app.tenant_id unset
    {
        // Either: the interceptor throws TenantContextMissingException
        // Or:     RLS returns zero rows (no `app.tenant_id`).
        var act = async () => await _fx.Db.Entities.ToListAsync();
        await Assert.ThrowsAsync<TenantContextMissingException>(act);
    }
}
```

For `[OrganizationScoped]` entities, add the cross-org pair:

```csharp
[Fact]
public async Task OrgScopedEntity_OrgX_cannot_read_OrgY_within_TenantA()
{
    using (_fx.AsTenant(_fx.TenantA, _fx.OrgA1)) {
        await _fx.CreateEntityAsync();
    }

    using (_fx.AsTenant(_fx.TenantA, _fx.OrgA2)) {
        var rows = await _fx.Db.Entities.ToListAsync();
        Assert.Empty(rows);
    }

    // Tenant-wide membership (no org) still sees the row by design:
    using (_fx.AsTenant(_fx.TenantA, organizationId: null)) {
        var rows = await _fx.Db.Entities.ToListAsync();
        Assert.NotEmpty(rows);
    }
}
```

### Step 3: Outbox round-trip

For a handler that publishes an integration event:

```csharp
[Fact]
public async Task CreateEnrollment_publishes_EnrollmentCreated_via_outbox()
{
    using (_fx.AsTenant(_fx.TenantA, _fx.OrgA1)) {
        await _fx.Mediator.Send(new CreateEnrollmentCommand(...));
    }

    var outboxRow = await _fx.Db.OutboxMessages
        .Where(x => x.Type.Contains("EnrollmentCreatedIntegrationEventV1"))
        .SingleAsync();

    Assert.Equal("learnstack.enrollment.enrollment", outboxRow.Topic);
    Assert.NotNull(outboxRow.Payload);
}
```

To assert the consumer side too, run the outbox processor synchronously in the
test:

```csharp
await _fx.RunOutboxProcessorOnceAsync();

using (_fx.AsTenant(_fx.TenantA, _fx.OrgA1)) {
    var auditEntry = await _fx.Audit
        .Where(x => x.Operation == "enrollment.create")
        .SingleAsync();
    Assert.Equal("create", auditEntry.OperationClass);
}
```

### Step 4: Audit assertion

```csharp
[Fact]
public async Task CreateEnrollment_writes_audit_entry_with_after_snapshot()
{
    using (_fx.AsTenant(_fx.TenantA, _fx.OrgA1)) {
        await _fx.Mediator.Send(new CreateEnrollmentCommand(...));

        var entry = await _fx.Audit
            .Where(x => x.Operation == "enrollment.create")
            .SingleAsync();

        Assert.Equal("create", entry.OperationClass);
        Assert.NotNull(entry.After);
        Assert.Null(entry.Before);
        Assert.Contains("\"learnerId\":", entry.After);
    }
}
```

### Step 5: Idempotency

For commands with an `Idempotency-Key`:

```csharp
[Fact]
public async Task Create_with_same_idempotency_key_returns_same_result()
{
    var key = "idem-12345";
    var a = await _fx.PostAsync("/v1/enrollments", payload, key);
    var b = await _fx.PostAsync("/v1/enrollments", payload, key);
    Assert.Equal(a.EnrollmentId, b.EnrollmentId);
    Assert.Equal(1, await _fx.Db.Enrollments.CountAsync());
}
```

### Step 6: Use real containers, not in-memory substitutes

The integration suite uses Testcontainers because in-memory substitutes don't run
RLS, don't enforce `SET LOCAL`, and don't reproduce Postgres-specific behaviour.
Don't substitute `UseInMemoryDatabase` even for "fast" tests.

### Step 7: Speed

The fixture is `IClassFixture` (per-class container lifetime). For test classes
that share the same seed shape, that's fast. If a class needs a unique seed,
prefer **inside-the-fixture** seeding over a new container.

CI parallelises by class; within a class tests run sequentially against the shared
container.

## Validation

- `dotnet test backend/tests/LearnStack.Tests.Integration` passes the new test.
- The mandatory isolation pair (`TenantA_cannot_read_TenantB`,
  `Org_X_cannot_read_Org_Y_within_TenantA`) is present for every new
  tenant-owned / org-scoped entity.
- A negative test confirms RLS-effective behaviour (empty result when
  `app.tenant_id` is unset or wrong).
- CI runtime for the new test is reasonable (< 5s per assertion is the budget;
  > 10s suggests over-seeding).

## Common pitfalls

- **Skipping the isolation pair.** Architecture tests catch the *policy*; only
  an integration test catches the *semantic* leak.
- **Forgetting `using fixture.AsTenant(...)`.** Without it, `app.tenant_id` is
  unset and queries return empty — which can mask a missing filter / RLS.
- **Asserting on row count without `AsNoTracking`.** EF's change tracker can
  hold a stale instance; use `AsNoTracking()` in reads after writes.
- **Sharing seed across tenants.** Always seed per-tenant inside an `AsTenant`
  block; cross-tenant seed creates ambiguity.
- **In-memory DB substitution.** Forbidden for integration tests; RLS doesn't run.
- **One test asserting six things.** Prefer one assertion per test for triage.
- **Container reuse without reset.** The fixture handles cleanup; don't roll
  your own that mutates global state.
