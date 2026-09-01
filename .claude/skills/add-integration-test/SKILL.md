---
name: add-integration-test
description: >
  Write a Testcontainers-backed integration test in
  `backend/tests/LearnStack.Tests.Integration` that exercises a real Postgres —
  connected as `learnstack_app`, never as the owner — and asserts behaviour under
  tenant + organization context. No Valkey and no Kafka: nothing the backend runs
  calls them. Phase 02a Packet 6 shipped the Postgres fixture, CI's
  `backend-integration` job, both migration chains, the RLS policies, a
  two-tenant seed and the schema-level isolation suite; Packet 7 re-runs those
  cases through `TenantResolverMiddleware` and the EF query filters. Docker-bound cases carry
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
- UI flows. There is no `LearnStack.Tests.EndToEnd` project. End-to-end means a
  browser: Playwright over a running stack, owned by
  [Phase 06](../../../docs/roadmap/phase-06-renderer-admin-studio.md) per
  [Testing Standards § End-to-End Tests](../../../docs/standards/06-testing.md).
  [Phase 02d](../../../docs/roadmap/phase-02d-walking-skeleton.md) puts two
  tenants in a browser but gates on a human opening them, not on a Playwright run.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Scenario | Yes | A short name + setup + act + assert. |
| Seed | Yes | Minimum tenants / orgs / users / customization data the scenario needs. |
| Required containers | Yes | **Postgres, always and only.** Not Valkey, not Kafka — nothing the backend runs calls them. Meilisearch / LiveKit / SeaweedFS only for a provider-contract test, and only from the phase that ships the adapter. |
| Tenant context | Yes | Which tenant + org the act phase runs as. |

## Workflow

### Step 1: Reuse the fixture

> **Two fixtures, and picking the wrong one is the common mistake.**
> `PostgresFixture` is the container plus the four roles and nothing else — use it
> for a role-level or provisioning question. `SchemaFixture` builds on it and is
> what almost every test wants: both migration chains applied and **every one of
> the ten tables seeded for two tenants**, with a second organization under tenant
> A. Share it with `[Collection(SharedSchema.Name)]` rather than
> `IClassFixture<>`, so one container serves the whole schema suite.
>
> Anything touching either carries `[Trait(RequiresDocker.Key, RequiresDocker.Value)]`,
> which is how CI routes it to `backend-integration` rather than `backend`.

What the fixtures do today:

- Spin **Postgres only** via Testcontainers. Not Valkey, not Kafka — nothing the
  backend runs today calls either, and both sit behind the `gated` compose profile
  per [ADR-0035](../../../docs/decisions/0035-demand-gated-infrastructure.md).
- Provision the **four database roles** by running the same script the compose
  stack runs, then apply both migration chains **as `learnstack_migration`** —
  which owns every table — and expose a connection as **`learnstack_app`** for the
  tests themselves. A test that connects as the owner or as a `BYPASSRLS` role
  passes even when every policy is inert, so it proves nothing.
- Seed **every table for both tenants**, deliberately: a count assertion against a
  table the fixture never populated passes whatever the policy says. That shipped
  once, in Packet 6, and is why `SchemaFixture` fills all ten.
- Expose the seeded ids as `SchemaFixture.TenantA` / `TenantB` / `OrgA1` / `OrgA2`,
  and the session-variable helpers as `SchemaQueries.SetTenantAsync` /
  `SetSettingAsync` — `set_config(name, value, true)`, not `SET LOCAL`, because
  PostgreSQL's `SET` takes no bind parameter.

There is no `AsTenant(...)` helper. Scope a block by opening a transaction and
calling `SchemaQueries.SetTenantAsync(connection, transaction, tenantId)` as its
first statement, which is what the shipped suite does and what
`IUnitOfWork.SetTenantContextAsync` does in production.

```csharp
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class EnrollmentIsolationTests
{
    private readonly SchemaFixture _schema;

    public EnrollmentIsolationTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task A_tenant_sees_only_its_own_enrollments()
    {
        // learnstack_app, never the owner: a test that connects as the owner — or
        // as either BYPASSRLS role — passes against inert policies.
        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();

        // First statement inside the transaction, which is what
        // IUnitOfWork.SetTenantContextAsync does in production. Outside a
        // transaction the setting is discarded and every later assertion is
        // measuring nothing.
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);

        await using var read = new NpgsqlCommand(
            "SELECT count(*) FROM enrollments", (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);

        (await read.ExecuteScalarAsync()).Should().Be(1L);

        // No commit: the transaction rolls back on dispose, so the fixture's
        // seeded row counts stay what the other cases assert.
    }
}
```

### Step 2: Mandatory tenant isolation pair

Every `[TenantOwned]` entity ships with these two tests **at minimum**:

The fixture seeds a row for **both** tenants — that is what makes the assertion
mean anything. A count of zero against a table nothing populated passes whatever
the policy says; that shipped once, in Packet 6.

```csharp
[Fact]
public async Task Tenant_A_cannot_read_Tenant_B_data()
{
    await using var connection = await PostgresFixture.OpenAsync(
        _schema.Postgres.AppConnectionString);
    await using var transaction = await connection.BeginTransactionAsync();
    await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);

    await using var read = new NpgsqlCommand(
        "SELECT count(*) FROM entities WHERE tenant_id = @other",
        (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
    read.Parameters.AddWithValue("other", SchemaFixture.TenantB);

    (await read.ExecuteScalarAsync()).Should().Be(0L);
}

[Fact]
public async Task Unsetting_tenant_context_returns_zero_rows_through_RLS()
{
    // No transaction and no set_config: app.tenant_id is unset, the policy
    // predicate is NULL, and NULL is false for USING and WITH CHECK alike.
    await using var connection = await PostgresFixture.OpenAsync(
        _schema.Postgres.AppConnectionString);
    await using var read = new NpgsqlCommand(
        "SELECT count(*) FROM entities", (NpgsqlConnection)connection);

    (await read.ExecuteScalarAsync()).Should().Be(0L);
}
```

Nothing throws `TenantContextMissingException` today — the type itself shipped in
Packet 3, in `LearnStack.SharedKernel/Errors/`. The `DbCommandInterceptor` that
throws it is described in Standards 05 and 11 and lands in **Packet 7**, which owns
it. Until it does, the fail-closed behaviour is the empty result, which is what to
assert. From Packet 7 the same read **through a module `DbContext`** is a loud
`TenantContextMissingException` — the interceptor is an EF `DbCommandInterceptor`
keyed on the marker a sanctioned setter stamps, so it never sees a raw
`NpgsqlCommand`. The case above opens its own connection from `PostgresFixture` and
therefore keeps asserting the empty result; a new case exercising the EF path is
what asserts the throw.

For `[OrganizationScoped]` entities, add the cross-org pair:

```csharp
[Fact]
public async Task Org_X_cannot_read_Org_Y_within_TenantA()
{
    await using var connection = await PostgresFixture.OpenAsync(
        _schema.Postgres.AppConnectionString);
    await using var transaction = await connection.BeginTransactionAsync();
    await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
    await SchemaQueries.SetSettingAsync(
        connection, transaction, "app.organization_id", SchemaFixture.OrgA1.ToString());

    await using var read = new NpgsqlCommand(
        "SELECT count(*) FROM entities WHERE organization_id = @other",
        (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
    read.Parameters.AddWithValue("other", SchemaFixture.OrgA2);

    (await read.ExecuteScalarAsync()).Should().Be(0L);
}
```

And one more, which the org-scoped template needs and an ordinary session cannot
reach: with `app.scope = 'tenant'` set, the **read** widens across organizations
and neither write does. Without that case both `AS RESTRICTIVE` guards can be
deleted with the suite green — measured, in Packet 6. Set the variable in the test
itself; nothing sets it at runtime, because the flag derives from the actor's role
and roles arrive in
[Phase 02b](../../../docs/roadmap/phase-02b-events-auth.md). See
`TenancySchemaTests.TheTenantScopeHatchWidensReadsAndNeitherWrite`.

The Packet 7 half of these cases goes through the **request**, and it needs no
production endpoint to do so. Register a **test-only controller in the test
fixture** — `AddApplicationPart` plus `TestControllerFilter`, the shipped
`IApplicationModelConvention` that keeps only the probe types this fixture names
and removes every other `ITestOnlyController`. That is the precedent
`IdempotencyFixture` set for `/api/v1/sideeffectprobe`, and
`ProductionHostFixture` is the counterpart that adds none, so the production
endpoint set stays exactly what a deployed instance serves. It drives the real
middleware chain and the real EF
query filters without moving Phase 02d's first `/api/v1/*` read endpoints earlier.

### Step 3: Outbox round-trip

> **Steps 3 to 5 are the shape, not today's API.** `IOutbox`, the outbox
> dispatcher and `audit_log` do not exist yet — Phase 02b owns the first two,
> Packet 9 the third — and the durable `IIdempotencyStore` ships on the trigger
> [ADR-0037 Amendment 1](../../../docs/decisions/0037-idempotency-key-contract.md)
> names: the first `[Idempotent]` endpoint, or the first deployment running more
> than one instance. Packet 6 shipped the `idempotency_keys` table, not the store. The `_fx.*` members below are illustrative of what those phases will
> provide; today the only fixtures are `PostgresFixture` and `SchemaFixture`, and
> the only session-context helper is `SchemaQueries`. Write against Step 1 and
> Step 2's shapes until the owning phase lands.

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
    var a = await _fx.PostAsync("/api/v1/enrollments", payload, key);
    var b = await _fx.PostAsync("/api/v1/enrollments", payload, key);
    Assert.Equal(a.EnrollmentId, b.EnrollmentId);
    Assert.Equal(1, await _fx.Db.Enrollments.CountAsync());
}
```

### Step 6: Use real containers, not in-memory substitutes

The integration suite uses Testcontainers because in-memory substitutes don't run
RLS, don't enforce `SET LOCAL`, and don't reproduce Postgres-specific behaviour.
Don't substitute `UseInMemoryDatabase` even for "fast" tests.

### Step 7: Speed

`SchemaFixture` is a **collection** fixture (`ICollectionFixture` behind
`[Collection(SharedSchema.Name)]`), so one container and one applied schema serve
every class in the schema suite. `PostgresFixture` is taken as an `IClassFixture`
by the class that needs the roles without the schema. If a class needs a unique
seed, prefer **inside-the-fixture** seeding over a new container — a fixture
carrying only one of the two migration chains is what narrowed every structural
sweep to eight of ten tables, and let a second permissive policy on
`outbox_messages` pass the whole suite.

Roll the transaction back rather than committing, so the seeded row counts other
cases assert on stay what they were.

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
- **Forgetting the tenant statement.** `SchemaQueries.SetTenantAsync(connection,
  transaction, tenantId)` is the transaction's first statement today; without it
  `app.tenant_id` is unset and every tenant-owned table returns empty — which
  reads exactly like "there is no data" and masks a missing filter or policy.
- **Asserting on row count without `AsNoTracking`.** EF's change tracker can
  hold a stale instance; use `AsNoTracking()` in reads after writes.
- **Sharing seed across tenants.** Always seed per-tenant inside a transaction
  that has issued its own tenant statement; cross-tenant seed creates ambiguity.
- **In-memory DB substitution.** Forbidden for integration tests; RLS doesn't run.
- **One test asserting six things.** Prefer one assertion per test for triage.
- **Container reuse without reset.** The fixture handles cleanup; don't roll
  your own that mutates global state.
