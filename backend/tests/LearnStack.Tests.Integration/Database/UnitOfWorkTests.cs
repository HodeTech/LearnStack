using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The ambient unit of work against a real database: the connection it owns, the
/// session variables it issues, the context it enlists, and what a nested frame
/// can and cannot do.
/// </summary>
/// <remarks>
/// <para>
/// The container is shared with the schema cases, so every test here either rolls
/// back or removes what it committed. The one that commits does so on
/// <c>outbox_messages</c> and deletes the row through <c>learnstack_platform</c>
/// in a <c>finally</c>, because a row left behind changes a count another case
/// asserts.
/// </para>
/// <para>
/// <b>What this cannot prove.</b>
/// <see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// § What Packet 6 can and cannot prove says the multi-context property needs a
/// second <b>module</b> context, which Phase 03 ships. What is provable now is the
/// half that makes it work: a context resolved through the shared helper reads
/// rows written on the ambient connection inside the same uncommitted
/// transaction, which is only true if it enlisted rather than opening its own.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class UnitOfWorkTests
{
    private readonly SchemaFixture _schema;

    public UnitOfWorkTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task A_resolved_context_becomes_the_session_variables()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SetTenantContextAsync(
            Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));

        (await ReadAsync(unitOfWork, "SELECT current_setting('app.tenant_id', true)"))
            .Should().Be(SchemaFixture.TenantA.ToString());
        (await ReadAsync(unitOfWork, "SELECT current_setting('app.organization_id', true)"))
            .Should().Be(SchemaFixture.OrgA1.ToString());

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    [Trait(RequiresDocker.Key, RequiresDocker.Value)]
    public async Task A_resolved_context_holding_an_uninitialized_id_still_writes_the_empty_string()
    {
        // The failure this exists to prevent is specific and was measured, not
        // imagined. Vogen 7 gives an uninitialized id two different textual
        // forms: ToString() returns the literal "[UNINITIALIZED]", while string
        // interpolation of the same value returns "". So a setter written the
        // obvious way — context.TenantId.ToString() — sends
        // '[UNINITIALIZED]' into app.tenant_id, and the first policy predicate
        // that evaluates it raises 22P02 instead of filtering. Reading .Value
        // without a gate throws instead. Both are worse than the fail-closed
        // empty string, and only an IsInitialized() gate produces it.
        //
        // IsResolved is deliberately true here: an implementation is *supposed*
        // to hold "IsResolved implies initialized", and this asserts that the
        // one boundary where being wrong is a security fault does not take that
        // promise on trust.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();

        // Seeded session-scoped (the third argument is false), so it survives
        // the transaction the setter runs in. Without this the assertion below
        // passes on a connection where the variable was simply never set, which
        // proves nothing about the setter: the property under test is that it
        // *overwrites* a leftover with the fail-closed empty string, which is
        // the case that matters on a pooled connection.
        await ReadAsync(
            unitOfWork,
            "SELECT set_config('app.tenant_id', '018f4d40-0000-7000-8000-00000000dead', false)");
        await ReadAsync(
            unitOfWork,
            "SELECT set_config('app.organization_id', '018f4d40-0000-7000-8000-00000000beef', false)");

        await unitOfWork.SetTenantContextAsync(new UninitializedIdContext());

        (await ReadAsync(unitOfWork, "SELECT current_setting('app.tenant_id', true)"))
            .Should().BeEmpty("an uninitialized id must fail closed, not reach ::uuid");
        (await ReadAsync(unitOfWork, "SELECT current_setting('app.organization_id', true)"))
            .Should().BeEmpty();

        // And the fail-closed value is usable: the policy filters rather than
        // raising, which is the whole point of writing '' over the alternative.
        (await ReadAsync(unitOfWork, "SELECT count(*)::text FROM organizations"))
            .Should().Be("0");

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    [Trait(RequiresDocker.Key, RequiresDocker.Value)]
    public async Task A_resolved_context_holding_an_all_zero_tenant_id_still_writes_the_empty_string()
    {
        // IsInitialized() is only half the test, and this is the other half.
        // Measured: TenantId.From(Guid.Empty) does not throw and IsInitialized()
        // returns true for it — Vogen validates the value's shape, and these ids
        // declare no Validate() — so an all-zero tenant reaches the setter as a
        // perfectly well-formed id. It casts cleanly to ::uuid and then matches
        // every row a bug ever wrote under it, which is worse than raising.
        // The domain refuses it by hand (TenantOwned.EnsureRealTenant); this is
        // the same refusal at the session-variable boundary.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SetTenantContextAsync(Resolved(Guid.Empty, Guid.Empty));

        (await ReadAsync(unitOfWork, "SELECT current_setting('app.tenant_id', true)"))
            .Should().BeEmpty("an all-zero tenant names nothing and must not be written");
        (await ReadAsync(unitOfWork, "SELECT current_setting('app.organization_id', true)"))
            .Should().BeEmpty();
        (await ReadAsync(unitOfWork, "SELECT count(*)::text FROM organizations"))
            .Should().Be("0");

        // And it said so. A context that claims to be resolved and yields nothing
        // usable is a bug in its producer; the empty string keeps the request
        // fail-closed, but without this line the only symptom is data that looks
        // absent rather than a context that is broken.
        provider.GetRequiredService<CapturingLogger>().Records
            .Should().ContainSingle(record => record.Level == LogLevel.Error)
            .Which.Message.Should().Contain("no usable tenant id");

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    public void The_uninitialized_id_fixture_really_is_uninitialized()
    {
        // Guards the guard. The two cases above prove the setter's behaviour only
        // if the stub actually carries unassigned ids, and it reaches them through
        // an array element because VOG009 forbids default(TenantId). If a Vogen
        // upgrade ever made that produce an initialized value, both cases would
        // keep passing while testing nothing at all — silently, which is the one
        // failure mode a test cannot report on its own behalf.
        var context = new UninitializedIdContext();

        context.TenantId.IsInitialized().Should().BeFalse();
        context.OrganizationId!.Value.IsInitialized().Should().BeFalse();
    }

    [Fact]
    public async Task An_unresolved_context_leaves_every_tenant_owned_table_empty()
    {
        // Between Packet 6 and Packet 7 every request runs against
        // UnresolvedTenantContext, and this is what that costs: the GUCs are set
        // to the empty string, NULLIF turns them into NULL, and a NULL predicate
        // is false for USING and WITH CHECK alike. Fail-closed by construction,
        // not by a filter that does not exist yet.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SetTenantContextAsync(UnresolvedTenantContext.Instance);

        (await ReadAsync(unitOfWork, "SELECT current_setting('app.tenant_id', true)"))
            .Should().BeEmpty();

        // Every mapped entity type, swept from the model rather than listed. Two
        // of the six that a hand-written pair left out are the ones where
        // fail-closed is least obvious: tenant_settings, whose USING carries the
        // app.scope hatch, and platform_host_to_tenant, whose read policy is an OR
        // over app.resolving_host — a GUC SetTenantContextAsync never writes.
        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var entity in context.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            await using var command = (NpgsqlCommand)unitOfWork.Connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM \"{table}\"";
            command.Transaction = (NpgsqlTransaction?)unitOfWork.Transaction;
            counts[table] = (long)(await command.ExecuteScalarAsync())!;
        }

        counts.Should().HaveCount(8, "TenancyDbContext maps eight entity types");
        counts.Should().OnlyContain(entry => entry.Value == 0);

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task Two_contexts_under_two_tenants_each_see_only_their_own_rows()
    {
        // The property the whole query-filter seam turns on, and the one that
        // fails if the filter closes over anything other than a DbContext
        // instance member. EF compiles a filter into the model once per context
        // type; a closure over a local, a field of something else, or a captured
        // value is evaluated at that moment and baked in as a SQL literal. Every
        // later request in the process then carries the FIRST request's tenant id
        // in its WHERE clause — with a plausible number of rows, from the wrong
        // tenant. Two scopes in one process, two tenants, is the smallest shape
        // that can tell the two implementations apart: one context alone passes
        // either way.
        await using var provider = BuildProvider();

        await using var first = provider.CreateAsyncScope();
        var firstUnitOfWork = first.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await firstUnitOfWork.BeginTransactionAsync();
        await EnterTenantAsync(
            first.ServiceProvider, firstUnitOfWork, SchemaFixture.TenantA, SchemaFixture.OrgA1);

        var firstContext = first.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenantACount = await firstContext.Organizations.CountAsync();
        var tenantASeen = await firstContext.Organizations
            .Select(organization => organization.TenantId).Distinct().ToListAsync();

        await firstUnitOfWork.RollbackAsync();

        await using var second = provider.CreateAsyncScope();
        var secondUnitOfWork = second.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await secondUnitOfWork.BeginTransactionAsync();
        await EnterTenantAsync(
            second.ServiceProvider, secondUnitOfWork, SchemaFixture.TenantB, Guid.NewGuid());

        var secondContext = second.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenantBSeen = await secondContext.Organizations
            .Select(organization => organization.TenantId).Distinct().ToListAsync();

        await secondUnitOfWork.RollbackAsync();

        // Each sees its own tenant and nothing else. Under a baked-in literal the
        // second scope would repeat the first's list, which is why the assertion
        // is on the tenant ids the rows carry and not merely on the counts.
        tenantACount.Should().BeGreaterThan(0, "the fixture seeds organizations for tenant A");
        tenantASeen.Should().ContainSingle()
            .Which.Value.Should().Be(SchemaFixture.TenantA);
        tenantBSeen.Should().ContainSingle()
            .Which.Value.Should().Be(SchemaFixture.TenantB);
    }

    [Fact]
    public async Task The_organization_filter_admits_tenant_wide_rows_and_the_caller_own()
    {
        // The organization term — `organization_id IS NULL OR organization_id =
        // current` — had no test at any layer. Measured before this case existed:
        // flipping its OR to an AND survived all 960. The term is the one that
        // decides whether a tenant-wide row is visible to an organization-scoped
        // request, which is the distinction ADR-0017 exists for.
        //
        // The fixture seeds exactly the three shapes tenant A needs: one
        // tenant-wide setting (`tz`), one under OrgA1 (`theme`), one under OrgA2
        // (`theme`). Under OrgA1 the answer is the first two and not the third.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await EnterTenantAsync(
            scope.ServiceProvider, unitOfWork, SchemaFixture.TenantA, SchemaFixture.OrgA1);

        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var visible = await context.TenantSettings
            .Select(setting => new { setting.Key, setting.OrganizationId })
            .ToListAsync();

        visible.Should().HaveCount(2,
            "an organization sees the tenant-wide rows and its own, and no sibling's");
        visible.Should().Contain(setting => setting.OrganizationId == null,
            "the tenant-wide row is in scope — null is a scope, not an absence");
        visible.Should().Contain(
            setting => setting.OrganizationId != null
                && setting.OrganizationId.Value.Value == SchemaFixture.OrgA1,
            "the caller's own organization is in scope");
        visible.Should().NotContain(
            setting => setting.OrganizationId != null
                && setting.OrganizationId.Value.Value == SchemaFixture.OrgA2,
            "a sibling organization's row is not");

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task A_context_follows_the_accessor_after_it_was_built()
    {
        // Whether the filters read the CURRENT ambient tenant or the one that
        // happened to be there when EF constructed the context. In every flow the
        // corpus designs, the accessor is written first — the resolver middleware
        // at scope start, the event transport per delivery — so a snapshot would
        // be correct today and wrong the first time that ordering changed. This
        // asserts the mechanism rather than the ordering.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await EnterTenantAsync(
            scope.ServiceProvider, unitOfWork, SchemaFixture.TenantA, SchemaFixture.OrgA1);

        // Built while tenant A is ambient.
        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        (await context.Organizations.CountAsync()).Should().BeGreaterThan(0);

        // Now the ambient tenant changes underneath it, and only the accessor
        // moves — app.tenant_id stays on A, because SetTenantContextAsync is
        // transaction-local and this transaction already issued it.
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current =
            Resolved(SchemaFixture.TenantB, Guid.NewGuid());

        // Zero is the discriminating answer, and it is the only one available:
        // the filter now narrows to B while Row Level Security still narrows to
        // A, so their intersection is empty. A context that had frozen its tenant
        // context at construction would still be filtering to A, agree with the
        // policy, and hand back tenant A's rows — which is what this asserts
        // against. Reading the emitted SQL cannot tell the two apart, because both
        // emit the same parameterised text and differ only in the value bound.
        (await context.Organizations.CountAsync()).Should().Be(0,
            "the filter must read the accessor on every query, not the instance it "
            + "was built with — a frozen context would still be reading tenant A");

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task A_module_context_enlists_in_the_ambient_transaction()
    {
        // The property the shared registration helper exists for. The row is
        // written on the raw ambient connection and never committed; a context
        // that had opened its own connection could not see it, and a context that
        // saw the connection but not the transaction could not either.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await EnterTenantAsync(
            scope.ServiceProvider, unitOfWork, SchemaFixture.TenantA, SchemaFixture.OrgA1);

        await ExecuteAsync(unitOfWork,
            """
            INSERT INTO organizations
                (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
            VALUES (uuidv7(), @tenant, 'enlisted', 'Enlisted', 'Active', now(), @actor, 0)
            """,
            ("tenant", SchemaFixture.TenantA), ("actor", SchemaFixture.Actor));

        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        (await context.Organizations.CountAsync()).Should().Be(3,
            "the two seeded organizations plus the uncommitted one on this transaction");

        await unitOfWork.RollbackAsync();

        // And the rollback is real: a fresh scope sees the seeded two.
        await using var after = provider.CreateAsyncScope();
        var afterUnitOfWork = after.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await afterUnitOfWork.BeginTransactionAsync();
        await EnterTenantAsync(
            after.ServiceProvider, afterUnitOfWork, SchemaFixture.TenantA, SchemaFixture.OrgA1);

        (await after.ServiceProvider.GetRequiredService<TenancyDbContext>()
            .Organizations.CountAsync()).Should().Be(2);

        await afterUnitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task A_context_resolved_outside_the_transaction_fails_loudly()
    {
        // The alternative is a context that reads zero rows from every
        // tenant-owned table and cannot say why — indistinguishable from "there
        // is no data" at the call site.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var resolve = () => scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*outside the ambient transaction*");
    }

    [Fact]
    public async Task A_nested_frame_joins_and_its_commit_is_not_a_commit()
    {
        // An application contract reaching a second handler through ISender is
        // how this happens. The inner frame's commit must not make the outer
        // frame's work durable.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var outer = await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SetTenantContextAsync(Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));
        var transaction = unitOfWork.Transaction;

        var inner = await unitOfWork.BeginTransactionAsync();
        inner.IsOwner.Should().BeFalse();
        outer.IsOwner.Should().BeTrue();
        unitOfWork.Transaction.Should().BeSameAs(transaction, "a nested begin joins, it does not nest");

        await inner.CompleteAsync();

        unitOfWork.HasActiveTransaction.Should().BeTrue(
            "the inner frame resolved; the transaction is still the outer frame's");

        await unitOfWork.RollbackAsync();
        unitOfWork.HasActiveTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task An_absorbed_inner_failure_still_commits_the_outer_work()
    {
        // ADR-0040 § Nesting's worked example, through the REAL behavior against a
        // real database: an inner frame declines, the outer handler absorbs it and
        // reports success, and the outer handler's own row must be there
        // afterwards. The first implementation poisoned the unit here — the inner
        // rollback set the rollback-only flag before the joiner check — so the
        // outer commit threw and the row was discarded.
        await using var provider = BuildProvider();
        var slug = $"absorbed-{Guid.CreateVersion7():N}"[..20];

        try
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var outer = new TransactionBehavior<Probe, Result<string>>(
                    unitOfWork,
                    Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1),
                    NullLogger<TransactionBehavior<Probe, Result<string>>>.Instance);
                var inner = new TransactionBehavior<Probe, Result<string>>(
                    unitOfWork,
                    Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1),
                    NullLogger<TransactionBehavior<Probe, Result<string>>>.Instance);

                var result = await outer.Handle(
                    new Probe(),
                    async () =>
                    {
                        await ExecuteAsync(unitOfWork,
                            """
                            INSERT INTO organizations
                                (id, tenant_id, slug, display_name, status,
                                 created_at, created_by, row_version)
                            VALUES (uuidv7(), @tenant, @slug, 'Absorbed', 'Active', now(), @actor, 0)
                            """,
                            ("tenant", SchemaFixture.TenantA), ("slug", slug),
                            ("actor", SchemaFixture.Actor));

                        var innerResult = await inner.Handle(
                            new Probe(),
                            () => Task.FromResult(Result.FailFor<Result<string>>(
                                new Error(new LocalizedMessage("lockey_business_rule_violation")))),
                            default);

                        innerResult.IsFailure.Should().BeTrue();
                        return Result.Ok("absorbed");
                    },
                    default);

                result.IsSuccess.Should().BeTrue();
            }

            (await CountOrganizationsAsync(slug)).Should().Be(1L,
                "the outer handler took responsibility for the inner failure, and its work commits");
        }
        finally
        {
            await using var platform = await PostgresFixture.OpenAsync(
                _schema.Postgres.PlatformConnectionString);
            await using var cleanup = new NpgsqlCommand(
                "DELETE FROM organizations WHERE slug = @slug", (NpgsqlConnection)platform);
            cleanup.Parameters.AddWithValue("slug", slug);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task A_leaked_inner_frame_makes_the_outer_completion_throw()
    {
        // The frame-blind CommitAsync cannot tell the owning frame's commit from a
        // joiner's, so a frame nobody resolved turns the outer commit into a
        // silent no-op: success reported, nothing written. Resolving through the
        // handle is what catches it, and this is why TransactionBehavior uses the
        // handle rather than the bare call.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var outer = await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SetTenantContextAsync(Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));

        await unitOfWork.BeginTransactionAsync();   // leaked: never resolved

        var complete = async () => await outer.CompleteAsync();

        (await complete.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*innermost-first*");

        await unitOfWork.RollbackAsync();
        await unitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task Disposing_a_frame_out_of_order_collapses_the_whole_unit()
    {
        // A frame that ends unresolved has failed, so disposal goes through the
        // same path FailAsync does — including its leaked-frames collapse.
        //
        // The alternative was measured: the frame-blind rollback decremented the
        // shared depth by one and left the transaction open, so the still-open
        // inner frame's completion did nothing and reported success, and a frame
        // opened later joined an abandoned transaction, committed nothing, and
        // handed the exception to that entirely innocent caller.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var outer = await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SetTenantContextAsync(Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));
        var inner = await unitOfWork.BeginTransactionAsync();

        await outer.DisposeAsync();

        unitOfWork.HasActiveTransaction.Should().BeFalse(
            "the outer frame ended unresolved, so the whole unit is done");

        // And the abandoned inner frame cannot revive it — nor quietly report
        // success. Its frame is gone because the collapse failed it, not because
        // it committed, and the unit is marked; completing says so.
        var completeAbandoned = async () => await inner.CompleteAsync();

        (await completeAbandoned.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*already resolved by a rollback*");
        unitOfWork.HasActiveTransaction.Should().BeFalse();

        var begin = async () => await unitOfWork.BeginTransactionAsync();

        (await begin.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*rollback-only*", "the collapse marked the unit, so nothing joins it later");
    }

    [Fact]
    public async Task MarkRollbackOnly_outlives_the_transaction_it_marked()
    {
        // The interface says "irreversible". The first implementation cleared the
        // flag inside BeginTransactionAsync, so a unit marked before a transaction
        // was opened — or between two on the same scope — committed anyway.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        unitOfWork.MarkRollbackOnly();

        var begin = async () => await unitOfWork.BeginTransactionAsync();

        (await begin.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*rollback-only*");
    }

    [Fact]
    public async Task The_runtime_role_does_not_bypass_row_security()
    {
        // Asked of the server rather than of the connection string, because the
        // name is not the privilege: learnstack_app could have been granted
        // BYPASSRLS, and a superuser bypasses row security with rolbypassrls
        // false. The composition root's data source runs this on every physical
        // connection; here it is asserted directly of the role the suite uses.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT rolbypassrls OR rolsuper FROM pg_roles WHERE rolname = current_user",
            (NpgsqlConnection)connection);

        (await command.ExecuteScalarAsync()).Should().Be(false,
            "every isolation assertion in this suite is vacuous against a bypass role");
    }

    [Fact]
    public async Task The_data_source_refuses_a_runtime_role_that_was_granted_bypass()
    {
        // The name check cannot see this one: the connection string still says
        // learnstack_app. Only the server knows the role was granted BYPASSRLS,
        // and with it every policy in the database is inert — so the composition
        // root asks, once per physical connection.
        //
        // The grant is made and reverted here rather than mocked, because the
        // question is whether the initializer actually runs and actually reads
        // the right catalogue column. Reverted in a finally: the cluster is shared
        // with every other case in this collection, and all of them are vacuous
        // against a bypass role.
        try
        {
            // The superuser, because none of the four LearnStack roles may alter a
            // role — learnstack_migration owns tables, and PostgreSQL wants
            // CREATEROLE plus ADMIN OPTION.
            await _schema.Postgres.ExecuteAsSuperuserAsync("ALTER ROLE learnstack_app BYPASSRLS");

            await using var dataSource = PersistenceCompositionExtensions.BuildApplicationDataSource(
                _schema.Postgres.AppConnectionString);

            var open = async () =>
            {
                await using var connection = await dataSource.OpenConnectionAsync();
            };

            (await open.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*bypasses Row Level Security*");
        }
        finally
        {
            await _schema.Postgres.ExecuteAsSuperuserAsync("ALTER ROLE learnstack_app NOBYPASSRLS");
        }

        // And the same data source is fine once the grant is gone, so the guard is
        // a guard rather than a permanent refusal.
        await using var restored = PersistenceCompositionExtensions.BuildApplicationDataSource(
            _schema.Postgres.AppConnectionString);
        await using var healthy = await restored.OpenConnectionAsync();

        healthy.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Fact]
    public async Task The_data_source_refuses_a_runtime_role_that_can_reach_one()
    {
        // The escalation a check on the role's own attributes cannot see:
        // `GRANT learnstack_platform TO learnstack_app` leaves learnstack_app's
        // rolbypassrls false and lets it SET ROLE into a BYPASSRLS role. Through a
        // bridge role, because a guard keyed on the four names would catch the
        // direct grant and miss this one.
        try
        {
            await _schema.Postgres.ExecuteAsSuperuserAsync(
                "CREATE ROLE uow_bridge NOLOGIN; "
                + "GRANT learnstack_platform TO uow_bridge; "
                + "GRANT uow_bridge TO learnstack_app");

            await using var dataSource = PersistenceCompositionExtensions.BuildApplicationDataSource(
                _schema.Postgres.AppConnectionString);

            var open = async () =>
            {
                await using var connection = await dataSource.OpenConnectionAsync();
            };

            (await open.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*reach one which bypasses Row Level Security*");
        }
        finally
        {
            // DROP alone, and no REVOKE first. Measured on PG 18.4: dropping a
            // role clears every pg_auth_members row naming it, in both
            // directions, so learnstack_app stops reaching the bypass role
            // without a separate revoke. And `REVOKE uow_bridge FROM …` ERRORS
            // when the role does not exist — which is exactly the state a failed
            // setup leaves — so the unnecessary statement was also the one that
            // would replace the real failure with a cleanup complaint.
            await _schema.Postgres.ExecuteAsSuperuserAsync("DROP ROLE IF EXISTS uow_bridge");
        }
    }

    [Fact]
    public async Task A_nested_failure_makes_the_outer_commit_impossible()
    {
        // ADR-0040 § Nesting: an EXCEPTION in an inner frame marks the unit, and
        // the outer commit then throws rather than committing a partial one.
        //
        // The escalation is MarkRollbackOnly, not the inner rollback — that is
        // the distinction the ADR draws and the one the first implementation
        // collapsed. An inner rollback alone is
        // An_absorbed_inner_failure_still_commits_the_outer_work, and it commits.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var outer = await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SetTenantContextAsync(Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));

        var inner = await unitOfWork.BeginTransactionAsync();
        unitOfWork.MarkRollbackOnly();
        await inner.FailAsync();

        var commit = async () => await outer.CompleteAsync();

        (await commit.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*rollback-only*");
        unitOfWork.HasActiveTransaction.Should().BeFalse("the failed unit was rolled back");
    }

    [Fact]
    public async Task Disposing_with_a_live_transaction_rolls_it_back()
    {
        await using var provider = BuildProvider();
        Guid id;

        await using (var scope = provider.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.BeginTransactionAsync();
            await unitOfWork.SetTenantContextAsync(Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));

            id = Guid.CreateVersion7();
            await ExecuteAsync(unitOfWork,
                """
                INSERT INTO organizations
                    (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
                VALUES (@id, @tenant, 'abandoned', 'Abandoned', 'Active', now(), @actor, 0)
                """,
                ("id", id), ("tenant", SchemaFixture.TenantA), ("actor", SchemaFixture.Actor));

            // No commit, no rollback: the scope simply ends. Committing here would
            // commit work nobody claimed was finished.
        }

        await using var platform = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        await using var check = new NpgsqlCommand(
            "SELECT count(*) FROM organizations WHERE id = @id", (NpgsqlConnection)platform);
        check.Parameters.AddWithValue("id", id);

        (await check.ExecuteScalarAsync()).Should().Be(0L);
    }

    [Fact]
    public async Task A_committed_unit_survives_the_scope()
    {
        // The other half of the previous case, and the only test here that
        // commits — on outbox_messages, and cleaned up in a finally, because the
        // fixture's row counts are what the schema cases assert.
        await using var provider = BuildProvider();
        var correlation = $"00-uow-{Guid.CreateVersion7():N}";

        try
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await unitOfWork.BeginTransactionAsync();
                await unitOfWork.SetTenantContextAsync(
                    Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));

                await ExecuteAsync(unitOfWork,
                    """
                    INSERT INTO outbox_messages
                        (tenant_id, correlation_id, type, topic, partition_key, payload)
                    VALUES (@tenant, @correlation, 'T', 'learnstack.tenancy.tenant', 'k', '{}')
                    """,
                    ("tenant", SchemaFixture.TenantA), ("correlation", correlation));

                await unitOfWork.CommitAsync();
                unitOfWork.HasActiveTransaction.Should().BeFalse();
            }

            (await CountOutboxAsync(correlation)).Should().Be(1L);
        }
        finally
        {
            await using var platform = await PostgresFixture.OpenAsync(
                _schema.Postgres.PlatformConnectionString);
            await using var cleanup = new NpgsqlCommand(
                "DELETE FROM outbox_messages WHERE correlation_id = @correlation",
                (NpgsqlConnection)platform);
            cleanup.Parameters.AddWithValue("correlation", correlation);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    // Both terminal calls on the stale handle. Measured before the fix:
    // CompleteAsync committed the SECOND frame's uncommitted work and returned
    // success; DisposeAsync rolled it back and then made the second frame's own
    // CompleteAsync throw "already resolved by a rollback".
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_frame_left_over_from_a_committed_unit_does_not_touch_the_next_one(
        bool completeTheStaleFrame)
    {
        // The one door back to depth 1 with a live handle: the frame-blind
        // CommitAsync that ADR-0040 § Amendment keeps for a caller with no handle
        // to hand. Every route back through a ROLLBACK sets the sticky mark and
        // BeginTransactionAsync refuses — measured — so this is the only one.
        await using var provider = BuildProvider();
        var first = $"00-uow-{Guid.CreateVersion7():N}";
        var second = $"00-uow-{Guid.CreateVersion7():N}";

        try
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                var stale = await unitOfWork.BeginTransactionAsync();
                await unitOfWork.SetTenantContextAsync(
                    Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));
                await InsertOutboxAsync(unitOfWork, first);
                await unitOfWork.CommitAsync();

                var current = await unitOfWork.BeginTransactionAsync();
                await unitOfWork.SetTenantContextAsync(
                    Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));
                await InsertOutboxAsync(unitOfWork, second);

                if (completeTheStaleFrame)
                {
                    await stale.CompleteAsync();
                }
                else
                {
                    await stale.DisposeAsync();
                }

                unitOfWork.HasActiveTransaction.Should().BeTrue(
                    "the stale frame belongs to a transaction that is already over");

                await current.CompleteAsync();
            }

            (await CountOutboxAsync(first)).Should().Be(1L, "the first unit committed");
            (await CountOutboxAsync(second)).Should().Be(1L,
                "the second unit committed on its own frame, and the stale one neither "
                + "committed it early nor rolled it back");
        }
        finally
        {
            await DeleteOutboxAsync(first);
            await DeleteOutboxAsync(second);
        }
    }

    [Fact]
    public async Task An_abandoned_transaction_after_a_committed_one_is_just_rolled_back()
    {
        // The swallowed-commit diagnostic is for a nested frame nobody resolved.
        // With the flag left set by an earlier, entirely correct commit, an
        // ordinary abandoned transaction tripped it instead — and DisposeAsync
        // threw that diagnostic over whatever exception had abandoned the
        // transaction in the first place.
        await using var provider = BuildProvider();
        var committed = $"00-uow-{Guid.CreateVersion7():N}";
        var abandoned = $"00-uow-{Guid.CreateVersion7():N}";

        try
        {
            var dispose = async () =>
            {
                await using var scope = provider.CreateAsyncScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await unitOfWork.BeginTransactionAsync();
                await unitOfWork.SetTenantContextAsync(
                    Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));
                await InsertOutboxAsync(unitOfWork, committed);
                await unitOfWork.CommitAsync();

                // Opened, written, and left for the scope to clean up.
                await unitOfWork.BeginTransactionAsync();
                await unitOfWork.SetTenantContextAsync(
                    Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));
                await InsertOutboxAsync(unitOfWork, abandoned);
            };

            await dispose.Should().NotThrowAsync(
                "an abandoned transaction is rolled back, not reported as a commit "
                + "the unit swallowed");

            (await CountOutboxAsync(committed)).Should().Be(1L);
            (await CountOutboxAsync(abandoned)).Should().Be(0L, "it was never committed");
        }
        finally
        {
            await DeleteOutboxAsync(committed);
            await DeleteOutboxAsync(abandoned);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private ServiceProvider BuildProvider()
    {
        // The real registration path: the shared helper, the real
        // NpgsqlUnitOfWork, and the application role's data source. Only the
        // connection string differs from the composition root, because the
        // fixture's container is not the one appsettings names.
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString));
        services.AddLogging();
        // The composition root's shape, not a shortcut: a singleton accessor and
        // a transient ITenantContext resolved from it on every access. The module
        // context's query filters close over that context, so a provider without
        // it cannot build one — which is what these cases would otherwise be
        // silently testing around.
        services.AddSingleton<ITenantContextAccessor, ScopedAccessor>();
        services.AddTransient<ITenantContext>(sp =>
            sp.GetRequiredService<ITenantContextAccessor>().Current
            ?? UnresolvedTenantContext.Instance);
        // Capturing, so a case can assert the unit of work actually complained.
        // The Error it logs when a resolved context yields no usable tenant is
        // the only signal that state ever produces — the request itself just
        // reads zero rows — so an unasserted logger would leave the diagnostic
        // in the same position as the silence it replaced.
        services.AddSingleton<CapturingLogger>();
        services.AddSingleton<ILogger<NpgsqlUnitOfWork>>(
            sp => sp.GetRequiredService<CapturingLogger>());
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddModuleDbContext<TenancyDbContext>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The accessor a case writes to decide what tenant its scope runs under.
    /// </summary>
    /// <remarks>
    /// Not <c>AsyncLocal</c>-backed like the production one: these cases drive it
    /// synchronously and want the value to be visible to the whole provider, which
    /// is what makes "two scopes, two tenants, one process" expressible.
    /// </remarks>
    private sealed class ScopedAccessor : ITenantContextAccessor
    {
        public ITenantContext? Current { get; set; }
    }

    /// <summary>Collects what <see cref="NpgsqlUnitOfWork"/> logged.</summary>
    private sealed class CapturingLogger : ILogger<NpgsqlUnitOfWork>
    {
        private readonly List<(LogLevel Level, int EventId, string Message)> _records = [];

        public IReadOnlyList<(LogLevel Level, int EventId, string Message)> Records
        {
            get { lock (_records) { return [.. _records]; } }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (_records)
            {
                _records.Add((logLevel, eventId.Id, formatter(state, exception)));
            }
        }
    }

    private async Task<long> CountOrganizationsAsync(string slug)
    {
        await using var platform = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM organizations WHERE slug = @slug", (NpgsqlConnection)platform);
        command.Parameters.AddWithValue("slug", slug);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static Task InsertOutboxAsync(IUnitOfWork unitOfWork, string correlation) =>
        ExecuteAsync(unitOfWork,
            """
            INSERT INTO outbox_messages
                (tenant_id, correlation_id, type, topic, partition_key, payload)
            VALUES (@tenant, @correlation, 'T', 'learnstack.tenancy.tenant', 'k', '{}')
            """,
            ("tenant", SchemaFixture.TenantA), ("correlation", correlation));

    private async Task DeleteOutboxAsync(string correlation)
    {
        await using var platform = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var cleanup = new NpgsqlCommand(
            "DELETE FROM outbox_messages WHERE correlation_id = @correlation",
            (NpgsqlConnection)platform);
        cleanup.Parameters.AddWithValue("correlation", correlation);
        await cleanup.ExecuteNonQueryAsync();
    }

    private async Task<long> CountOutboxAsync(string correlation)
    {
        await using var platform = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM outbox_messages WHERE correlation_id = @correlation",
            (NpgsqlConnection)platform);
        command.Parameters.AddWithValue("correlation", correlation);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadAsync(IUnitOfWork unitOfWork, string sql)
    {
        await using var command = unitOfWork.Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = unitOfWork.Transaction;

        return (await command.ExecuteScalarAsync()) as string ?? string.Empty;
    }

    private static async Task ExecuteAsync(
        IUnitOfWork unitOfWork, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = (NpgsqlCommand)unitOfWork.Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = (NpgsqlTransaction?)unitOfWork.Transaction;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static StubTenantContext Resolved(Guid tenant, Guid organization) =>
        new(tenant, organization);

    /// <summary>
    /// Sets the session variables <b>and</b> the ambient accessor, which is the
    /// pairing production makes: <c>TenantResolverMiddleware</c> writes the
    /// accessor, and <c>TransactionBehavior</c> issues the <c>SET LOCAL</c> from
    /// the same context. A case that wrote only the first would leave the module
    /// context's query filter narrowing to the all-zero tenant and reading
    /// nothing — which is the filter working, not a fixture bug, and worth
    /// spelling out here so the next reader does not remove the filter to make a
    /// count come back.
    /// </summary>
    private static async Task EnterTenantAsync(
        IServiceProvider scope, IUnitOfWork unitOfWork, Guid tenant, Guid organization)
    {
        var context = Resolved(tenant, organization);
        scope.GetRequiredService<ITenantContextAccessor>().Current = context;
        await unitOfWork.SetTenantContextAsync(context);
    }

    /// <summary>
    /// Claims to be resolved while carrying ids nothing ever assigned.
    /// </summary>
    /// <remarks>
    /// <c>default(TenantId)</c> does not compile — Vogen's VOG009 analyzer
    /// prohibits it — so the uninitialized value comes from an array element,
    /// which the analyzer cannot see and the runtime leaves zeroed. That is also
    /// how one reaches production: a struct field nobody assigned, a
    /// <c>default(T)</c> in a generic, a deserializer that skipped a member.
    /// </remarks>
    private sealed class UninitializedIdContext : ITenantContext
    {
        private static readonly TenantId Unassigned = Zeroed<TenantId>();
        private static readonly OrganizationId UnassignedOrganization = Zeroed<OrganizationId>();

        private static T Zeroed<T>()
        {
            var slot = new T[1];
            return slot[0];
        }

        public bool IsResolved => true;

        public TenantId TenantId => Unassigned;

        public OrganizationId? OrganizationId => UnassignedOrganization;

        public UserId? UserId => null;

        public string? CorrelationId => null;

        public string? ModuleName => "uninitialized-id-probe";
    }

    /// <summary>A request type for driving the real behavior.</summary>
    public sealed record Probe : MediatR.IRequest<Result<string>>;

    /// <summary>
    /// A resolved context, standing in for what Packet 7's
    /// <c>TenantResolverMiddleware</c> will populate.
    /// </summary>
    private sealed class StubTenantContext(Guid tenant, Guid organization) : ITenantContext
    {
        // Converted here rather than at each call site: the stub's callers hold
        // raw fixture Guids and the contract holds typed ids.

        public bool IsResolved => true;

        public TenantId TenantId => SharedKernel.Identifiers.TenantId.From(tenant);

        public OrganizationId? OrganizationId =>
            SharedKernel.Identifiers.OrganizationId.From(organization);

        public UserId? UserId => null;

        public string? CorrelationId => null;

        public string? ModuleName => "tenancy";
    }
}
