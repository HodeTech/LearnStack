using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class OperationReadScopeTests(SchemaFixture schema)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SQL_and_parameterized_EF_filters_share_the_exact_live_owner_read_scope(bool wide)
    {
        var context = Context();
        using var admission = new Admission(context);
        await using var provider = Provider(context);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        if (wide) await unit.SetTenantContextAsync(context, admission.Witness);
        else await unit.SetTenantContextAsync(context);
        (await ScalarAsync(unit, "SELECT current_user")).Should().Be("learnstack_app");
        (await ScalarAsync(unit, "SELECT current_setting('app.scope', true)")).Should().Be(wide ? "tenant" : string.Empty);
        ((IOperationReadScopeView)unit).IsTenantWideRead.Should().Be(wide);
        (await ScalarAsync(unit, "SELECT count(*) FROM tenant_settings")).Should().Be(wide ? 3L : 2L);
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        db.CurrentTenantWideRead.Should().Be(wide);
        db.TenantSettings.ToQueryString().Should().Contain("CurrentTenantWideRead",
            "the model must retain the context-instance parameter rather than bake in the first scope");
        var rows = await db.TenantSettings.AsNoTracking().Select(row => new { row.TenantId, row.OrganizationId }).ToListAsync();
        rows.Should().HaveCount(wide ? 3 : 2);
        rows.Should().OnlyContain(row => row.TenantId == context.TenantId);
        rows.Any(row => row.OrganizationId == OrganizationId.From(SchemaFixture.OrgA2)).Should().Be(wide);
        await frame.CompleteAsync();
        db.CurrentTenantWideRead.Should().BeFalse();
    }

    [Fact]
    public async Task A_context_without_the_common_owner_attachment_remains_narrow_even_when_SQL_is_wide()
    {
        var context = Context();
        using var admission = new Admission(context);
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context, admission.Witness);
        var options = new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql(unit.Connection)
            .AddInterceptors(new TenantContextGuardInterceptor(unit)).Options;
        await using var db = new TenancyDbContext(options, new StaticTenantContextAccessor(context));
        await db.Database.UseTransactionAsync(unit.Transaction);
        (await ScalarAsync(unit, "SELECT count(*) FROM tenant_settings")).Should().Be(3L);
        db.CurrentTenantWideRead.Should().BeFalse();
        (await db.TenantSettings.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("tenant")]
    [InlineData("organization")]
    [InlineData("missing")]
    [InlineData("expired")]
    public async Task A_missing_expired_or_mismatched_witness_poison_the_actual_owner(string mismatch)
    {
        var context = Context();
        using var admission = new Admission(context);
        var supplied = mismatch switch
        {
            "actor" => context with { Actor = UserId.From(IdentityMembershipFixture.PersonA) },
            "tenant" => context with { TenantId = TenantId.From(SchemaFixture.TenantB) },
            "organization" => context with { OrganizationId = OrganizationId.From(SchemaFixture.OrgA2) },
            _ => context,
        };
        if (mismatch == "expired") admission.Dispose();
        await using var provider = Provider(context);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        var announce = () => unit.SetTenantContextAsync(supplied, mismatch == "missing" ? null! : admission.Witness);
        await announce.Should().ThrowAsync<InvalidOperationException>();
        unit.IsRollbackOnly.Should().BeTrue();
        ((IOperationReadScopeView)unit).IsTenantWideRead.Should().BeFalse();
        await FluentActions.Awaiting(() => frame.CompleteAsync()).Should().ThrowAsync<InvalidOperationException>();
        unit.HasActiveTransaction.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_mixed_read_scope_joins_poison_outer_commit_even_after_inner_failure_is_absorbed(bool outerWide)
    {
        var context = Context();
        using var outerAdmission = new Admission(context);
        using var innerAdmission = new Admission(context);
        await using var provider = Provider(context);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var outer = await unit.BeginTransactionAsync();
        if (outerWide) await unit.SetTenantContextAsync(context, outerAdmission.Witness);
        else await unit.SetTenantContextAsync(context);
        await using var inner = await unit.BeginTransactionAsync();
        Func<Task> announce = outerWide ? () => unit.SetTenantContextAsync(context)
            : () => unit.SetTenantContextAsync(context, innerAdmission.Witness);
        await announce.Should().ThrowAsync<InvalidOperationException>().WithMessage("*read scope*rollback-only*");
        await inner.FailAsync();
        unit.IsRollbackOnly.Should().BeTrue();
        ((IOperationReadScopeView)unit).IsTenantWideRead.Should().BeFalse();
        await FluentActions.Awaiting(() => outer.CompleteAsync()).Should().ThrowAsync<InvalidOperationException>();
        unit.HasActiveTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task Compatible_wide_join_consumes_its_own_witness_and_restores_the_live_outer_frame()
    {
        var context = Context();
        using var outerAdmission = new Admission(context);
        using var innerAdmission = new Admission(context);
        await using var provider = Provider(context);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var outer = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context, outerAdmission.Witness);
        var announcement = unit.GetTenantContextAnnouncement(unit.Transaction);
        await using (var inner = await unit.BeginTransactionAsync())
        {
            await unit.SetTenantContextAsync(context, innerAdmission.Witness);
            unit.GetTenantContextAnnouncement(unit.Transaction).Should().BeSameAs(announcement);
            (await ScalarAsync(unit, "SELECT count(*) FROM tenant_settings")).Should().Be(3L);
            await inner.CompleteAsync();
        }
        innerAdmission.Dispose();
        ((IOperationReadScopeView)unit).IsTenantWideRead.Should().BeTrue();
        (await ScalarAsync(unit, "SELECT count(*) FROM tenant_settings")).Should().Be(3L);
        await outer.CompleteAsync();
    }

    [Fact]
    public async Task An_unbound_inner_frame_cannot_borrow_the_wide_outer_frames_read_authority()
    {
        var context = Context();
        using var admission = new Admission(context);
        await using var provider = Provider(context);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var outer = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context, admission.Witness);
        await using var inner = await unit.BeginTransactionAsync();
        var borrow = () => unit.Connection;
        borrow.Should().Throw<InvalidOperationException>();
        ((IOperationReadScopeView)unit).IsTenantWideRead.Should().BeFalse();
        await inner.FailAsync();
        await FluentActions.Awaiting(() => outer.CompleteAsync()).Should().ThrowAsync<InvalidOperationException>();
        unit.HasActiveTransaction.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_consumed_witness_cannot_rebind_to_another_unit_or_recycled_transaction_generation(bool sameUnit)
    {
        var context = Context();
        using var admission = new Admission(context);
        await using var provider = Provider(context);
        await using var firstScope = provider.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using (var frame = await first.BeginTransactionAsync())
        {
            await first.SetTenantContextAsync(context, admission.Witness);
            await frame.CompleteAsync();
        }
        admission.Witness.IsActive.Should().BeTrue("liveness does not renew a consumed binding");
        await using var secondScope = provider.CreateAsyncScope();
        var next = sameUnit ? first : secondScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var nextFrame = await next.BeginTransactionAsync();
        await FluentActions.Awaiting(() => next.SetTenantContextAsync(context, admission.Witness))
            .Should().ThrowAsync<InvalidOperationException>();
        next.IsRollbackOnly.Should().BeTrue();
        await nextFrame.FailAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_or_canceled_invocation_invalidates_raw_unit_access_EF_and_commit_before_cleanup(bool cancel)
    {
        var context = Context();
        using var cancellation = new CancellationTokenSource();
        using var admission = new Admission(context, cancellation.Token);
        await using var provider = Provider(context);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context, admission.Witness);
        var transaction = unit.Transaction;
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        (await db.TenantSettings.CountAsync()).Should().Be(3);
        var retainedQuery = db.TenantSettings.AsNoTracking();
        if (cancel) await cancellation.CancelAsync();
        else admission.Dispose();
        db.CurrentTenantWideRead.Should().BeFalse();
        unit.GetTenantContextAnnouncement(transaction).Should().BeNull();
        var raw = () => unit.Connection;
        raw.Should().Throw<InvalidOperationException>();
        await FluentActions.Awaiting(() => retainedQuery.ToListAsync()).Should().ThrowAsync<TenantContextMissingException>();
        await FluentActions.Awaiting(() => frame.CompleteAsync()).Should().ThrowAsync<InvalidOperationException>();
        unit.HasActiveTransaction.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_completion_and_rollback_clear_read_authority_on_the_same_unreset_pooled_connector(bool commit)
    {
        var context = Context();
        using var admission = new Admission(context);
        var parsed = new NpgsqlConnectionStringBuilder(schema.Postgres.AppConnectionString) { MaxPoolSize = 1, NoResetOnClose = true };
        await using var source = ApplicationDataSource.Build(parsed.ConnectionString);
        int backend;
        await using (var first = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance))
        {
            await using var frame = await first.BeginTransactionAsync();
            await first.SetTenantContextAsync(context, admission.Witness);
            backend = ((NpgsqlConnection)first.Connection).ProcessID;
            (await ScalarAsync(first, "SELECT current_setting('app.scope', true)")).Should().Be("tenant");
            if (commit) await frame.CompleteAsync();
            else await frame.FailAsync();
            first.IsTenantWideRead.Should().BeFalse();
        }
        await using var next = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var nextFrame = await next.BeginTransactionAsync();
        ((NpgsqlConnection)next.Connection).ProcessID.Should().Be(backend);
        (await ScalarAsync(next, "SELECT NULLIF(current_setting('app.scope', true), '')")).Should().Be(DBNull.Value);
        await ScalarAsync(next, "SELECT set_config('app.scope', 'tenant', false)");
        await next.SetTenantContextAsync(context);
        (await ScalarAsync(next, "SELECT current_setting('app.scope', true)")).Should().Be(string.Empty,
            "the ordinary announcement actively clears even a planted session-scoped hatch");
        (await ScalarAsync(next, "SELECT count(*) FROM tenant_settings")).Should().Be(2L);
    }

    [Fact]
    public async Task A_tenant_read_hatch_does_not_widen_sibling_organization_updates_or_deletes()
    {
        var context = Context();
        using var admission = new Admission(context);
        await using var provider = Provider(context);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context, admission.Witness);
        (await ScalarAsync(unit, "SELECT count(*) FROM tenant_settings")).Should().Be(3L);
        foreach (var verb in new[] { "UPDATE tenant_settings SET value = value", "DELETE FROM tenant_settings" })
        {
            await using var sibling = new NpgsqlCommand(verb + " WHERE organization_id = @organization",
                (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);
            sibling.Parameters.AddWithValue("organization", SchemaFixture.OrgA2);
            (await sibling.ExecuteNonQueryAsync()).Should().Be(0);
        }
        await using var own = new NpgsqlCommand("UPDATE tenant_settings SET value = value WHERE organization_id = @organization",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);
        own.Parameters.AddWithValue("organization", SchemaFixture.OrgA1);
        (await own.ExecuteNonQueryAsync()).Should().Be(1, "the positive control retains ordinary branch writes");
    }

    private ServiceProvider Provider(ITenantContext context)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => ApplicationDataSource.Build(schema.Postgres.AppConnectionString));
        services.AddLogging();
        services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(context));
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddModuleDbContext<TenancyDbContext>();
        return services.BuildServiceProvider();
    }

    private static async Task<object?> ScalarAsync(IUnitOfWork unit, string sql)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);
        return await command.ExecuteScalarAsync();
    }

    private static ScopeContext Context() => new(TenantId.From(SchemaFixture.TenantA),
        OrganizationId.From(SchemaFixture.OrgA1), UserId.From(IdentityMembershipFixture.AdminA));

    private sealed record ScopeContext(TenantId TenantId, OrganizationId? OrganizationId, UserId Actor) : ITenantContext
    {
        public bool IsResolved => true;
        public UserId? UserId => Actor;
        public TenantContextOrigin? Origin => TenantContextOrigin.ClaimAndMembership;
        public string? CorrelationId => null;
        public string? ModuleName => "tenancy";
    }

    private sealed class Probe;

    private sealed class Admission : OperationReadWitness.MintingSource, IDisposable
    {
        private readonly CandidateMinter _candidate = new();
        private readonly InvocationMinter _invocation = new();
        internal OperationReadWitness Witness { get; }
        internal Admission(ITenantContext context, CancellationToken cancellationToken = default)
        {
            var candidate = _candidate.Mint(context);
            Witness = ForTenantRead(_invocation.Mint(context, candidate, cancellationToken), candidate);
        }
        public void Dispose() { _invocation.Close(); _candidate.Dispose(); }
    }

    private sealed class CandidateMinter : AuthorizationCandidate.MintingSource
    {
        internal AuthorizationCandidate Mint(ITenantContext context) => ForCandidate(AuthorizationSourceKind.Http,
            context.UserId!.Value, context.TenantId, context.OrganizationId);
    }

    private sealed class InvocationMinter : OperationInvocation.MintingSource
    {
        private OperationInvocation? _current;
        private bool _closed;
        internal OperationInvocation Mint(ITenantContext context, AuthorizationCandidate candidate, CancellationToken cancellationToken)
        {
            var policy = OperationPolicy.ForProtected<Probe>("identity.membership.directory",
                new PermissionDefinition(PermissionScope.Tenant, "identity.membership.admin"), OperationReadScope.Tenant);
            return _current = ForInvocation(new Probe(), policy, context, candidate, null, cancellationToken);
        }
        internal void Close() => _closed = true;
        protected override bool IsCurrent(OperationInvocation invocation) => !_closed && ReferenceEquals(_current, invocation);
    }
}
